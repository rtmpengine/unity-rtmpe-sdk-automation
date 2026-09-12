// RTMPE SDK — Runtime/Core/Sync/VariableBatchManager.cs
//
// Per-tick accumulator + batched-emitter for NetworkVariable updates.
//
// Why this is its own class:
//   FlushDirtyNetworkVariables() walks every owned, spawned NetworkBehaviour
//   on each tick and calls FlushDirtyVariables(sender) — when batching is
//   enabled, the sender callback diverts each per-object payload into the
//   batch instead of emitting it immediately. Bundling the diversion logic
//   into one class makes the cap-eager-flush invariant ("never let a batch
//   exceed _activeCap entries within a tick") testable in isolation, and
//   isolates the per-instance buffer reuse pattern from the Update loop.
//
// Threading:
//   Main-thread only. Update() runs on the Unity main thread and is the
//   only path that reaches CollectIntoBatch / FlushPending. The internal
//   buffers (_pending, _scratch) are not synchronised; they assume the
//   single-thread invariant of the Update path.
//
// GC Round 2 (2026-05-02):
//   • Collector signature is now Action<byte[], int> so callers can pass a
//     pooled / cached buffer that is larger than the logical payload —
//     CollectIntoBatch reads only the leading `length` bytes.
//   • Pending entries are still per-payload byte[] allocations because they
//     must persist across CollectIntoBatch calls until FlushPending sends
//     the combined batch.  This per-entry allocation is the same cost the
//     pre-Round-2 ms.ToArray() path paid; the win is on the non-batching
//     path which now bypasses the copy entirely (NetworkBehaviour hands
//     ms.GetBuffer() + length to the un-batched sender without copying it).
//   • The combined batch byte[] (built by VariableBatchBuilder) is rented
//     from ArrayPool<byte>.Shared and returned after SendVariableBatchUpdate
//     wraps it.  Eliminates the per-tick batch allocation that scaled with
//     pending count.

using System;
using System.Buffers;
using System.Collections.Generic;

using UnityEngine;

using RTMPE.Sync;

namespace RTMPE.Core.Sync
{
    internal sealed class VariableBatchManager
    {
        private readonly List<byte[]> _pending = new List<byte[]>(32);

        // Reusable scratch sized to the active cap. Resized lazily when the
        // configured cap grows; never shrunk because shrinking would create
        // allocation pressure for a setting that is only ever raised as
        // the project's variable count grows.
        private byte[][] _scratch = new byte[32][];

        private int _activeCap;

        // Maximum wire size, in bytes, of a single batch payload — the datagram
        // application-payload ceiling the PacketBuilder enforces.  The
        // accumulator flushes before a pending batch would cross this, so a
        // batch is never built larger than one datagram and then rejected at
        // the send boundary.  Zero disables byte-aware splitting (entry-count
        // cap only), preserving the legacy behaviour for callers that do not
        // supply a budget.
        private readonly int _byteBudget;

        // Running wire size of the batch currently accumulating in _pending,
        // including the leading count byte; zero whenever _pending is empty.
        private int _pendingBytes;

        private readonly Action<byte[], int> _sendBatch;        // → NetworkManager.SendVariableBatchUpdate(byte[], int)
        private readonly Action<byte[], int> _sendSingleFallback; // → NetworkManager.SendVariableUpdate(byte[], int)

        // Cached delegates for the two senders FlushDirtyVariables is handed.
        // Method-group conversion allocates on every read and Collector is read
        // inside the 30 Hz loop, so it is cached; the other is cached for
        // symmetry, and so that the sender's identity is stable — a caller that
        // stored one and compared it later would otherwise never match.
        private Action<byte[], int> _collectorCache;
        private Action<byte[], int> _containedSingleSenderCache;

        // Drop accounting. The total is monotonic and is the reading an
        // application alerts on; the other two belong to the rate gate on the
        // log line, which reports how much it suppressed.
        private long _containedDropCount;
        private long _dropsSinceLastReport;
        private long _lastDropWarnTicks;

        public VariableBatchManager(
            Action<byte[], int> sendBatch,
            Action<byte[], int> sendSingleFallback,
            int byteBudget)
        {
            _sendBatch          = sendBatch          ?? throw new ArgumentNullException(nameof(sendBatch));
            _sendSingleFallback = sendSingleFallback ?? throw new ArgumentNullException(nameof(sendSingleFallback));
            _byteBudget         = byteBudget > 0 ? byteBudget : 0;
        }

        /// <summary>True iff <see cref="CollectIntoBatch"/> has stashed at least one entry awaiting flush.</summary>
        public bool HasPending => _pending.Count > 0;

        /// <summary>
        /// Apply the per-tick cap (clamped externally by
        /// <see cref="RTMPE.Sync.VariableBatchBuilder.ClampBatchCap"/> to
        /// [1, GatewayEntryCap] so an over-cap batch the gateway would drop is
        /// never produced).  Must be set before <see cref="CollectIntoBatch"/>
        /// is used; the collector consults the cap to decide when to eagerly
        /// flush.
        /// </summary>
        public void SetActiveCap(int cap) => _activeCap = cap;

        /// <summary>
        /// The cached <see cref="Action{Byte[], Int32}"/> form of <see cref="CollectIntoBatch"/>.
        /// Hot-path: returned once and reused on every tick to keep
        /// FlushDirtyVariables's per-NetworkBehaviour callback allocation-free.
        /// </summary>
        public Action<byte[], int> Collector => _collectorCache ??= CollectIntoBatch;

        /// <summary>
        /// The un-batched per-object sender, wrapped in the same containment
        /// the batched path applies to its own sends.
        /// </summary>
        /// <remarks>
        /// The contract is the flush's, not the batcher's: this sender never
        /// throws, because its caller runs inside a tick that must complete and
        /// the payload has already been <c>MarkClean()</c>'d, so an escaping
        /// throw would lose the update and take the rest of the tick with it.
        /// The throw that motivates it is <c>PacketBuilder.Build</c> refusing a
        /// payload above <c>MaxApplicationPayloadBytes</c>, which one oversized
        /// <c>NetworkVariableString</c> or a long enough list produces on a
        /// wholly ordinary tick; the send path reaches the transport too, so the
        /// reachable set is wider.  Batching is off by default, so this is the
        /// path most projects run.
        /// </remarks>
        public Action<byte[], int> ContainedSingleSender =>
            _containedSingleSenderCache ??= SafeSendSingle;

        private void CollectIntoBatch(byte[] payload, int length)
        {
            if (payload == null || length <= 0) return;

            // Flush the current batch before this entry would push its wire
            // size past one datagram, so a batch splits on cumulative byte size
            // as well as on entry count and is never built larger than the MTU.
            // Guarded on a non-empty pending set: a lone entry that exceeds the
            // budget on its own cannot be split further and is contained by the
            // send guard, not an endless flush loop.
            int entryWire = VariableBatchBuilder.EntryWireSize(length);
            if (_byteBudget > 0 && _pending.Count > 0
                && _pendingBytes + entryWire > _byteBudget)
            {
                FlushPending();
            }

            // The pending entry must persist across CollectIntoBatch calls
            // until FlushPending fires.  The caller's buffer (NetworkBehaviour's
            // cached _flushMs backing array, or another pooled buffer) will
            // be reused on the next tick, so we MUST copy out into our own
            // exact-sized byte[] here.  This per-payload allocation is
            // unavoidable at the batching boundary without a deeper redesign
            // (e.g. a per-tick parallel-lengths array + pooled mega-buffer);
            // it matches the cost of the pre-Round-2 ms.ToArray() call, so
            // batching is no worse than before, and non-batching is cheaper.
            var entry = new byte[length];
            Buffer.BlockCopy(payload, 0, entry, 0, length);

            if (_pending.Count == 0) _pendingBytes = VariableBatchBuilder.BatchHeaderBytes;
            _pending.Add(entry);
            _pendingBytes += entryWire;

            if (_pending.Count >= _activeCap)
            {
                FlushPending();
            }
        }

        /// <summary>
        /// Drop every pending payload without sending. Used at session
        /// boundary (ClearSessionData) so a reconnect does not flush
        /// stale variable updates onto the new session's nonce stream
        /// (the receiver's PacketBuilder counter restarts at zero).
        /// </summary>
        public void Clear()
        {
            _pending.Clear();
            _pendingBytes = 0;
            // _scratch is left alone — it is reused in place; the trailing
            // slots are always cleared at the start of FlushPending.
        }

        /// <summary>
        /// Encode every pending payload into a single VariableBatchUpdate
        /// packet and dispatch via the batch sender.  No-op when no payload
        /// is pending.  On builder exception, falls back to per-object
        /// VariableUpdate packets so a single corrupt payload cannot stall
        /// the entire frame's variable updates.
        /// </summary>
        public void FlushPending()
        {
            int count = _pending.Count;
            if (count == 0) return;
            if (_scratch.Length < count)
            {
                _scratch = new byte[count][];
            }
            for (int i = 0; i < count; i++) _scratch[i] = _pending[i];
            // Null the trailing slots so the array does not keep stale
            // references alive across the next batch.
            for (int i = count; i < _scratch.Length; i++) _scratch[i] = null;
            _pending.Clear();
            _pendingBytes = 0;

            // GC Round 2 (2026-05-02): rent the batch byte[] from ArrayPool
            // and use the BuildInto overload to write the wire bytes
            // directly into the rented buffer.  ComputeTotalSize gives the
            // exact size; ArrayPool.Rent may return a buffer larger than
            // the requested size, so we explicitly pass `total` to
            // SendVariableBatchUpdate(byte[], int) so the wire frame's
            // payload_len matches the bytes we actually wrote.
            int total;
            try
            {
                total = VariableBatchBuilder.ComputeTotalSize(_scratch, count);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] VariableBatchBuilder.ComputeTotalSize threw {ex.GetType().Name}: {ex.Message}. " +
                    "Falling back to per-object VariableUpdate packets for this batch.");
                for (int i = 0; i < count; i++) SafeSendSingle(_scratch[i], _scratch[i].Length);
                return;
            }

            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(total);
            try
            {
                int written;
                try
                {
                    written = VariableBatchBuilder.BuildInto(buffer, 0, _scratch, count);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] VariableBatchBuilder.BuildInto threw {ex.GetType().Name}: {ex.Message}. " +
                        "Falling back to per-object VariableUpdate packets for this batch.");
                    for (int i = 0; i < count; i++) SafeSendSingle(_scratch[i], _scratch[i].Length);
                    return;
                }

                SafeSendBatch(buffer, written, _scratch, count);
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        // No send or builder exception may propagate into the per-tick Update
        // loop: the dirtied variables are MarkClean()'d before they reach either
        // sender, so an escaping throw would both abort the remainder of the
        // frame and lose the update with no retry.  These wrappers keep a single
        // un-sendable payload — e.g. one object whose own update exceeds a
        // datagram — contained to a counted drop rather than a frame-wide abort.
        private void SafeSendBatch(byte[] buffer, int written, byte[][] entries, int count)
        {
            try
            {
                _sendBatch(buffer, written);
            }
            catch (Exception ex)
            {
                // The ENVELOPE is what did not fit, not the objects inside it.
                // Each entry was budgeted by the flush against the same cap the
                // builder enforces, so each one fits on its own; what pushes a
                // lone near-cap entry over is the batch's count byte and the
                // entry's length prefix, which no byte-split can remove — the
                // eager split only compares a SECOND entry against the budget.
                // Dropping here would cost every object in the batch for an
                // overflow of three bytes, so fall back to the per-object send
                // exactly as the two builder-failure arms above do.
                //
                // ⛔ NOT counted as a drop.  The counter reads lost updates, and
                // a batch whose entries all go out singly has lost nothing —
                // counting the envelope too would report 1 + N losses for N
                // successes and double-count each real one beside it.  Only
                // SafeSendSingle counts, and only when a single genuinely fails.
                // The failure is still reported, because a batching session
                // silently degrading to per-object sends is worth knowing.
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastBatchFallbackWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] VariableBatch send refused ({ex.GetType().Name}); " +
                        $"re-sending its {count} entr{(count == 1 ? "y" : "ies")} individually. " +
                        "At most one line per second.");

                for (int i = 0; i < count; i++)
                    SafeSendSingle(entries[i], entries[i].Length);
            }
        }

        // One-line-per-second gate for the envelope fallback above.  A batch
        // refused for its framing is refused on every tick that produces the
        // same shape, so the report is rate-limited while the per-entry sends
        // that follow it are not affected.
        private long _lastBatchFallbackWarnTicks;

        private void SafeSendSingle(byte[] payload, int length)
        {
            try
            {
                _sendSingleFallback(payload, length);
            }
            catch (Exception ex)
            {
                RecordContainedDrop("Variable update", ex);
            }
        }

        /// <summary>
        /// Total sends this manager has swallowed, over every entry into its
        /// containment.  A drop is a lost update the sender will not retry, so
        /// this is the unrated reading of the loss; the log line beside it is
        /// rate-limited and cannot be counted from.
        /// </summary>
        /// <remarks>
        /// ⚠ Reachable from inside the SDK assembly only — the manager is
        /// internal and the field holding it is private, so a shipping title
        /// cannot read this and has the once-a-second log line as its only
        /// signal.  Counted anyway, because it is what a test asserts on and
        /// what a future diagnostics surface would publish.
        /// <para>It counts sends that <em>threw</em>.  An update discarded
        /// before it reaches the transport — no network thread, or a session
        /// that is not connected — returns quietly and is not in this
        /// figure.</para>
        /// </remarks>
        public long ContainedDropCount => _containedDropCount;

        /// <summary>
        /// Account for one contained send failure and report it at most once a
        /// second, carrying the drops suppressed since the last line.
        /// </summary>
        /// <remarks>
        /// The condition persists: a variable whose serialized form exceeds a
        /// datagram fails on every tick it is dirtied, and a list re-flags itself
        /// dirty on every mutation with no equality check — so an ungated line
        /// here is a 30 Hz console flood on a permanent, single-cause fault. The
        /// suppressed count is carried because a latch would report a permanent
        /// data loss exactly once.
        /// </remarks>
        private void RecordContainedDrop(string what, Exception ex)
        {
            _containedDropCount++;
            _dropsSinceLastReport++;

            if (!WarnGate.ShouldEmit(ref _lastDropWarnTicks)) return;

            long suppressed = _dropsSinceLastReport - 1;
            _dropsSinceLastReport = 0;
            Debug.LogWarning(
                $"[RTMPE] {what} send failed ({ex.GetType().Name}): {ex.Message}. " +
                $"Dropped this tick; {_containedDropCount} dropped in total" +
                (suppressed > 0 ? $", {suppressed} since the last line" : string.Empty) + ".");
        }
    }
}
