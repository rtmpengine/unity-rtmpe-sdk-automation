// RTMPE SDK — Runtime/Core/InputBuffer.cs
//
// Fixed-capacity ring buffer that stores unacknowledged InputPayloads for
// client-side prediction rollback.
//
// Capacity: 64 entries (power of two — enables bitwise AND masking).
// At 30 Hz, 64 entries covers >2 seconds of unacknowledged input — well
// beyond any realistic server round-trip time.  When the buffer is full
// the NEWEST push is rejected (Push returns false): the oldest entry is
// the rollback anchor closest to server-confirmed state and must be
// preserved so the replay path can re-simulate from a coherent baseline.
// Overwriting the oldest would silently roll the rollback horizon past
// the server's last ack, producing undetectable state corruption when
// the next reconciliation arrives.  A bounded counter
// (DroppedInputCount) surfaces the rejection rate to telemetry.
//
// No UnityEngine dependency — testable from pure .NET xunit projects.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// A fixed-size buffer of the <see cref="InputPayload"/> entries the server
    /// has not yet acknowledged, kept for client-side prediction.
    /// </summary>
    /// <remarks>
    /// It allocates nothing after construction. Not thread-safe.
    /// </remarks>
    public sealed class InputBuffer
    {
        // ── Constants ──────────────────────────────────────────────────────────

        /// <summary>The maximum number of entries the buffer holds.</summary>
        public const int Capacity = 64;

        // Bitwise AND mask for O(1) index wrapping without modulo arithmetic.
        private const int Mask = Capacity - 1;

        // ── Storage ────────────────────────────────────────────────────────────

        private readonly InputPayload[] _slots = new InputPayload[Capacity];

        // _head: index of the oldest unacknowledged entry.
        // _count: number of valid unacknowledged entries (0..Capacity inclusive).
        private int _head;
        private int _count;

        // Tracks the highest ack tick seen so far.  AcknowledgeUpTo rejects
        // any value that is not strictly greater than this, preventing a hostile
        // server from replaying a stale ack to clear the buffer out of order.
        private uint _lastAckedTick;
        private bool _hasLastAckedTick;

        // Monotonic counter incremented every time Push rejects an entry
        // because the buffer is saturated.  Exposed so integrators can wire
        // it into telemetry / on-screen debugging without poking internal
        // state.  64-bit width because at sustained-rejection extremes
        // (10 ms acks for hours on a misconfigured server) the count can
        // exceed 2³¹ across a single session.
        private long _droppedInputCount;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>The number of unacknowledged entries stored.</summary>
        public int Count => _count;

        /// <summary>
        /// The number of inputs <see cref="Push"/> has refused, because the buffer
        /// was full or because <see cref="InputPayload.MoveX"/> or
        /// <see cref="InputPayload.MoveY"/> was NaN or infinite.
        /// </summary>
        /// <remarks>
        /// A value that keeps rising usually means acknowledgements are not
        /// arriving in time. <see cref="Clear"/> does not reset it.
        /// </remarks>
        public long DroppedInputCount => _droppedInputCount;

        // ── Operations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Adds <paramref name="payload"/> after the newest entry.
        /// </summary>
        /// <remarks>
        /// The input is refused when the buffer is full, in which case the stored
        /// entries are kept, or when a movement value is NaN or infinite. Each
        /// refusal is counted in <see cref="DroppedInputCount"/>.
        /// </remarks>
        /// <param name="payload">The input to add.</param>
        /// <returns>
        /// <see langword="false"/> when the buffer is full or a movement value is
        /// NaN or infinite; otherwise <see langword="true"/>.
        /// </returns>
        public bool Push(InputPayload payload)
        {
            // Reject non-finite movement axes before they enter the rollback
            // buffer.  WriteTo enforces finiteness at the wire boundary and
            // throws, but an unfinished/buggy GatherInput callback can still
            // push NaN into the local CSP simulation where it poisons predicted
            // transforms for every subsequent replay tick (SDKS-04).  Mirroring
            // the WriteTo guard here keeps the buffer free of non-finite inputs
            // regardless of whether the packet ever reaches the wire.
            if (float.IsNaN(payload.MoveX)      || float.IsInfinity(payload.MoveX)
                || float.IsNaN(payload.MoveY)   || float.IsInfinity(payload.MoveY))
            {
                unchecked { _droppedInputCount++; }
                return false;
            }

            if (_count >= Capacity)
            {
                // Saturated: refuse the newest write.  The oldest entry is
                // the rollback anchor closest to the last server-confirmed
                // state; evicting it would silently roll the replay horizon
                // past the server's ack and produce undetectable corruption
                // on the next reconciliation.
                unchecked { _droppedInputCount++; }
                return false;
            }

            int slot = (_head + _count) & Mask;
            _slots[slot] = payload;
            _count++;
            return true;
        }

        /// <summary>
        /// Removes every entry whose <see cref="InputPayload.Tick"/> is at or
        /// before <paramref name="ackedTick"/>.
        /// </summary>
        /// <remarks>
        /// Ignored when <paramref name="ackedTick"/> is not later than the last
        /// acknowledged tick. Tick comparisons allow for the tick counter wrapping
        /// past <see cref="uint.MaxValue"/>.
        /// </remarks>
        /// <param name="ackedTick">The latest tick the server has acknowledged.</param>
        public void AcknowledgeUpTo(uint ackedTick)
        {
            // Monotonicity guard: a replayed or out-of-order ack cannot bulk-clear
            // inputs the server has not yet processed.  SeqLessOrEqual handles
            // the wrap boundary so an ack of (uint.MaxValue - 1) does not block
            // a subsequent ack of (uint.MaxValue + 1) ≡ 0.
            if (_hasLastAckedTick && SeqLessOrEqual(ackedTick, _lastAckedTick)) return;
            _hasLastAckedTick = true;
            _lastAckedTick    = ackedTick;

            while (_count > 0 && SeqLessOrEqual(_slots[_head].Tick, ackedTick))
            {
                _head  = (_head + 1) & Mask;
                _count--;
            }
        }

        // ── Modular sequence-number arithmetic (RFC 1982) ──────────────────────
        //
        // A signed 32-bit difference treats two unsigned values as "near" on a
        // ring of size 2³² when the gap between them is less than 2³¹.  The
        // CSP buffer is bounded at 64 entries, so any in-flight ack is at most
        // a few thousand ticks behind the head — orders of magnitude below the
        // 2³¹ wrap-distance threshold the comparison relies on.

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="a"/> is strictly
        /// greater than <paramref name="b"/> in 32-bit modular sequence-number
        /// space (i.e. <c>(int)(a - b) &gt; 0</c>).
        /// </summary>
        internal static bool SeqGreater(uint a, uint b) => (int)(a - b) > 0;

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="a"/> is less than
        /// or equal to <paramref name="b"/> in 32-bit modular sequence-number
        /// space.
        /// </summary>
        internal static bool SeqLessOrEqual(uint a, uint b) => (int)(a - b) <= 0;

        /// <summary>
        /// Copies every stored entry, oldest first, into <paramref name="dest"/>.
        /// </summary>
        /// <param name="dest">
        /// The destination; it must hold at least <see cref="Count"/> entries
        /// (<see cref="Capacity"/> always suffices).
        /// </param>
        /// <returns>The number of entries written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        public int CopyUnacknowledgedTo(InputPayload[] dest)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int written = 0;
            for (int i = 0; i < _count; i++)
                dest[written++] = _slots[(_head + i) & Mask];
            return written;
        }

        /// <summary>
        /// Copies, oldest first, every entry whose <see cref="InputPayload.Tick"/>
        /// is later than <paramref name="afterTick"/> into <paramref name="dest"/>:
        /// the inputs to apply again after a correction from the server.
        /// </summary>
        /// <remarks>
        /// Tick comparisons allow for the tick counter wrapping past
        /// <see cref="uint.MaxValue"/>, as in <see cref="AcknowledgeUpTo"/>.
        /// </remarks>
        /// <param name="afterTick">The last tick the correction covers.</param>
        /// <param name="dest">
        /// The destination; <see cref="Capacity"/> entries always suffice.
        /// </param>
        /// <returns>The number of entries written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        public int CopyUnacknowledgedAfter(uint afterTick, InputPayload[] dest)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int written = 0;
            for (int i = 0; i < _count; i++)
            {
                var slot = _slots[(_head + i) & Mask];
                if (SeqGreater(slot.Tick, afterTick))
                    dest[written++] = slot;
            }
            return written;
        }

        /// <summary>
        /// Removes every entry and forgets the last acknowledged tick.
        /// <see cref="DroppedInputCount"/> is not reset.
        /// </summary>
        public void Clear()
        {
            _head             = 0;
            _count            = 0;
            _hasLastAckedTick = false;
            _lastAckedTick    = 0;
        }
    }
}
