// RTMPE SDK — Runtime/Sync/NetworkVariableList.cs
//
// Synchronised List<T> for network-replicated collections (inventory items,
// active buffs, kill feeds, …).  Modelled on Unity Netcode's NetworkList<T>
// and Photon's PunRPC-driven list pattern, with the RTMPE specifics:
//  • Operates inside the existing 30 Hz NetworkVariable flush loop.
//  • Wire format encodes a delta log (Add / Insert / RemoveAt / Set / Clear)
//    for steady-state efficiency and a periodic full-sync for safety against
//    a missed delta.  Late-joiner snapshot uses the FullSync op exclusively.
//    The periodic one is TryBeginPeriodicResync, driven from the flush loop on
//    a list that is CLEAN — which is the state a lost delta leaves it in, and
//    the reason the refresh cannot be hung off the dirty path.
//  • Per-element serialisation is delegated to a subclass hook
//    (WriteElement / ReadElement) so the same delta machinery covers
//    primitive ints, floats, vectors, strings, and (in future) any
//    INetworkSerializable.
//
// Wire format of a single payload (within the NetworkVariable value frame
// [value_len:2 LE][bytes]):
//
//  [op_count : 1 u8]
//  op record (per op_count):
//    [op : 1 u8]
//    followed by per-op fields (see ListOp).
//
//  ListOp.Add        : [elem_bytes]                           (always tail-append)
//  ListOp.Insert     : [index:2 LE ushort][elem_bytes]
//  ListOp.RemoveAt   : [index:2 LE ushort]
//  ListOp.Set        : [index:2 LE ushort][elem_bytes]
//  ListOp.Clear      : (no fields)
//  ListOp.FullSync   : [count:2 LE ushort][elem_bytes × count] (replaces the list)
//
// elem_bytes layout depends on the subclass:
//  NetworkVariableListInt      : 4-byte LE i32
//  NetworkVariableListFloat    : 4-byte LE f32
//  NetworkVariableListVector3  : 12-byte LE (x,y,z)
//  NetworkVariableListString   : 2-byte LE ushort len + UTF-8 bytes
//
// Failure handling:
//  • Out-of-range Insert / RemoveAt / Set indices are dropped at apply time
//    with a warning; the rest of the payload continues to apply.  This makes
//    the receiver tolerant to a delta that was authored against a slightly
//    newer state without crashing the gameplay layer.
//  • An Add or Insert that would push the list past its configured size
//    ceiling is dropped op-locally, so a stream of delta payloads cannot
//    grow the receiver's list without bound.  The same ceiling is refused on
//    the WRITE — RefuseAtCapacity, consulted by every mutator that can grow
//    the list — because a ceiling enforced on one side only is a licence to
//    hold elements every other client is required to drop.
//  • The op log applies atomically.  A malformed element, a truncated
//    payload, or an unknown op reverts the list to its pre-payload contents,
//    and change notifications are dispatched only once the whole payload has
//    applied — a subscriber never observes an op the payload then discards.
//  • op_count is a single byte — at most 255 ops per flush.  When more
//    mutations queue up between two flushes, the surplus is split across
//    subsequent flushes.  When the local op log exceeds a soft cap
//    (configurable via FullSyncOpThreshold) we promote to a full-sync to
//    bound the per-flush bandwidth.
//
// Performance:
//  • Mutation methods record a single struct in _pendingOps without
//    allocating per call; the change list is reused across flushes.
//  • Serialize iterates the change log into the BinaryWriter and resets
//    the log on success.
//
// Thread safety: same as NetworkVariable<T> — Unity main-thread only for
// mutation; the network thread reads incoming bytes and dispatches to
// ApplyVariableUpdate on the main thread via MainThreadDispatcher.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>Operation kinds in the NetworkVariableList delta log.</summary>
    internal enum ListOp : byte
    {
        Add      = 0x01,
        Insert   = 0x02,
        RemoveAt = 0x03,
        Set      = 0x04,
        Clear    = 0x05,
        FullSync = 0x06,
    }

    /// <summary>
    /// Abstract base for all NetworkVariableList&lt;T&gt; specialisations.
    /// Concrete subclasses (one per element type) provide
    /// <see cref="WriteElement"/> and <see cref="ReadElement"/>.
    /// </summary>
    /// <typeparam name="T">
    /// Element type.  No constraint at this layer; concrete subclasses
    /// constrain to the supported wire types.
    /// </typeparam>
    public abstract class NetworkVariableList<T> : NetworkVariableBase
    {
        // ── Local state ─────────────────────────────────────────────────────────

        private readonly List<T> _items = new List<T>();

        // Pending op log.  Retired by MarkClean — NOT by Serialize, so a payload
        // the datagram cap refuses can be retracted and offered again with the
        // same bytes.  When this list grows past FullSyncOpThreshold we collapse
        // it into a single FullSync op to bound per-flush bandwidth and
        // op-count overflow risk.
        private readonly List<PendingOp> _pendingOps = new List<PendingOp>();

        // ── Post-despawn write guards ─────────────────────────────────────────
        //
        // The scalar variables one file over enforce these on every path and a
        // list enforced neither, though a list has strictly more to lose: each
        // mutator queues an op AND raises a change event, so a write after
        // OnNetworkDespawn both re-publishes dead state on the next flush and
        // fires callbacks against subscribers the owner may already have cleared,
        // on a GameObject that may be mid-Destroy.
        //
        // ⛔ Two guards, not one, because the scalars have two and the difference
        // is deliberate.  Inbound tolerates nothing: an update arrives routed by
        // object id through a registry that only holds spawned objects, so a
        // wire write for a variable with no live owner has nothing to land on.
        // That one is the scalars' verbatim.
        //
        // ⚠️ Outbound is NOT the scalars' expression, and the difference is the
        // point.  `Owner != null && !Owner.IsSpawned` reads "after despawn" —
        // which is what the scalars' comment says it means — but the expression
        // is equally true BEFORE the first spawn, and there the two types are not
        // alike: a scalar takes its initial value in its constructor, while a
        // list is populated by calling Add.  A game that builds its list in
        // Awake would have had every element silently dropped, which is a worse
        // defect than the one being closed and would have shipped looking like a
        // fix.  So the close is latched on having BEEN spawned.
        // 🔴 Armed by the SPAWN, never by a write.  The first version of this
        // latched inside the getter — which is reached only from the five
        // mutators — so a list that is not mutated by local code while spawned
        // never armed it and the guard stayed open for the object's whole life.
        // That is not an edge case: on every non-owning peer a list is written
        // by the wire and never by a local call, so the repair protected
        // nothing on exactly the replicas most likely to still hold live
        // subscribers at teardown.
        //
        // Two arming sites, and both are needed.  `OnOwnerSpawned` covers a
        // variable that already exists when the object spawns; the constructor
        // covers one built INSIDE OnNetworkSpawn — which is where the SDK tells
        // developers to build them, and which runs after the spawn loop has
        // already walked the list.
        private bool _hasBeenSpawned;

        // Whether this list entered its current life already holding elements.
        // Building one before the object spawns is the pattern this class exists
        // to admit — the lifecycle rule above stays open for exactly that — but
        // on a client that does not own the object those elements are local
        // only: no peer was told about them, and every peer that runs the same
        // Awake builds its own copy.  The owner's first payload therefore
        // describes the whole list rather than a change to it, and only this
        // flag distinguishes that payload from every one that follows.
        private bool _preSpawnContentsPending;

        internal override void OnOwnerSpawned()
        {
            base.OnOwnerSpawned();
            _hasBeenSpawned = true;

            // ⛔ Armed only where this client cannot be the sender.  The elements
            // are superseded because they were built by a peer that the payload's
            // author knows nothing about — which is true of a replica and false
            // of the owner, whose pre-spawn build IS the state the room is about
            // to be told.  Without the side condition an inbound payload reaching
            // an owner destroys the owner's own list: reachable after a handover,
            // and the loss is silent because the discarded elements are still
            // described by the op log this client is about to flush.
            //
            // `WriteWouldNotLand` rather than `!IsOwner`, and read here rather
            // than at the discard: ownership is reconciled by
            // `ReconcileSyncComponentsToOwnership` before this loop runs and
            // `_isSpawned` is already true, so the predicate answers the
            // authority question alone at exactly the point the contents in hand
            // are the pre-spawn ones.
            _preSpawnContentsPending = _items.Count > 0 && WriteWouldNotLand;
        }

        /// <summary>
        /// Whether a mutation would be refused, and — when
        /// <paramref name="report"/> — saying so for the one refusal a caller
        /// could not otherwise see.
        /// </summary>
        /// <remarks>
        /// One body rather than two because the mutators and <c>CanSend</c> ask
        /// the SAME question and differ only in whether they may speak: a
        /// question must not log, and a refused mutation must.  Written as two
        /// properties, the two would drift.
        /// </remarks>
        private bool OutboundWritesRefused(bool report)
        {
            if (Owner == null)    return false;            // detached, purely local
            if (!Owner.IsSpawned) return _hasBeenSpawned;  // spawned once, not now

            // Live and networked: a write reaches the wire only if this client
            // owns the object.  ObjectDispatchOps.FlushAll skips every component
            // that fails exactly this test, so an accepted write would mutate
            // the list here, raise its change event to local subscribers, and
            // reach no other client — and leave IsDirty set with an op log
            // nothing ever serialises.
            return report ? RefuseUnownedWrite("list mutation") : WriteWouldNotBeSent;
        }

        private bool OutboundWritesClosed => OutboundWritesRefused(report: true);

        /// <inheritdoc/>
        /// <remarks>
        /// A list's lifecycle rule is not the scalars': it stays open before the
        /// first spawn, because that is when a game builds it.
        /// </remarks>
        private protected override bool WriteWouldNotLand => OutboundWritesRefused(report: false);

        private bool InboundWritesClosed => Owner == null || !Owner.IsSpawned;

        // Reusable scratch for the atomic apply path in Deserialize.
        // _rollbackBuffer captures the pre-payload contents so a payload that
        // fails partway is reverted in full; _deferredChanges holds the change
        // notifications until the whole payload has been confirmed applied, so
        // a subscriber never observes an op that the payload later rolled back.
        // Both reuse their backing storage across calls — no per-payload alloc.
        private readonly List<T> _rollbackBuffer = new List<T>();
        private readonly List<NetworkVariableListChangeEvent<T>> _deferredChanges =
            new List<NetworkVariableListChangeEvent<T>>();

        /// <summary>
        /// When the local change log exceeds this threshold the next flush is
        /// promoted to a FullSync.  255 (op_count's max) is the absolute hard
        /// cap; the soft default of 32 is well below that and matches the
        /// typical per-tick mutation budget for an inventory or buff list.
        /// </summary>
        public int FullSyncOpThreshold { get; set; } = 32;

        // Apply-side cap to defend against malicious / malformed payloads.
        // No legitimate sender should ever emit more than op_count's max value.
        private const int MaxOpsPerPayload = 255;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fires once per applied op (locally and on remote receivers).
        /// Useful for UI list widgets that prefer to react to deltas instead of
        /// rebuilding from scratch each tick.  The event delivers the list
        /// AFTER the op has been applied so callers always observe a
        /// consistent state.
        /// </summary>
        public event Action<NetworkVariableListChangeEvent<T>> OnListChanged;

        // ── Construction ───────────────────────────────────────────────────────

        protected NetworkVariableList(NetworkBehaviour owner, string memberName)
            : base(owner, memberName)
        {
            // Built inside OnNetworkSpawn — the documented place — the object is
            // already spawned and the spawn loop has already walked a list this
            // variable was not yet in.  Arming here is the only way such a
            // variable is ever latched.
            if (owner != null && owner.IsSpawned) _hasBeenSpawned = true;
        }

        // ── List-style API ────────────────────────────────────────────────────

        /// <summary>Number of elements currently in the list.</summary>
        public int Count => _items.Count;

        /// <summary>
        /// The largest number of elements this list will hold, from
        /// <c>NetworkSettings.maxNetworkVariableListSize</c> (default 1024).
        /// </summary>
        /// <remarks>
        /// The same number on both sides of the wire.  A receiver drops an
        /// inbound <c>Add</c> or <c>Insert</c> that would carry the list past it
        /// and refuses a <c>FullSync</c> that declares more, so a writer allowed
        /// past it would hold elements no replica is permitted to store — the
        /// two diverging with nothing to report it.  The refusal belongs on the
        /// write, where owner and replicas are left holding the same list.
        /// </remarks>
        public int MaxCount => ResolveMaxListSize();

        /// <summary>
        /// True when the list is at <see cref="MaxCount"/> and a further
        /// <see cref="Add"/> or <see cref="Insert"/> would be refused.
        /// </summary>
        public bool IsFull => _items.Count >= MaxCount;

        /// <summary>Read or replace an element at <paramref name="index"/>.</summary>
        public T this[int index]
        {
            get => _items[index];
            set
            {
                if ((uint)index >= (uint)_items.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                // Both refusals precede the lifecycle guard: an out-of-range
                // index and an unsendable element are caller errors whether or
                // not the object is spawned, and swallowing either after despawn
                // would hide a bug behind a lifecycle state.  (The bounds check
                // itself is above, and throws.)
                if (RefuseUnsendableElement(value, "indexer set")) return;
                if (OutboundWritesClosed) return;

                T previous = _items[index];
                _items[index] = value;
                EnqueueOp(new PendingOp(ListOp.Set, index, value));
                IsDirty = true;
                Raise(new NetworkVariableListChangeEvent<T>(
                    NetworkListChangeKind.Set, index, value, previous));
            }
        }

        /// <summary>Append an item to the end of the list.</summary>
        /// <remarks>
        /// Does nothing when the list is already at <see cref="MaxCount"/> —
        /// the return is void, so the caller learns it from
        /// <see cref="IsFull"/> beforehand or <see cref="TryAdd"/> instead.  The
        /// refusal names the ceiling in the console at most once a second, which
        /// is a diagnostic and not an answer to this call.
        /// </remarks>
        public void Add(T item)
        {
            if (RefuseUnsendableElement(item, "Add")) return;
            // ⛔ After the lifecycle gate, unlike the element refusal above. An
            // element this list can never put on the wire is a caller error
            // whatever state the object is in; a FULL list on a replica is not —
            // the wire filled it, the write was never this client's to make, and
            // reporting the ceiling there would name the wrong reason.
            if (OutboundWritesClosed) return;
            if (RefuseAtCapacity("Add")) return;
            int index = _items.Count;
            _items.Add(item);
            EnqueueOp(new PendingOp(ListOp.Add, index, item));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Add, index, item, default));
        }

        /// <summary>Insert <paramref name="item"/> at <paramref name="index"/>.</summary>
        /// <remarks>
        /// Does nothing when the list is already at <see cref="MaxCount"/>, on
        /// the same terms as <see cref="Add"/>: the index is still validated and
        /// still throws, because an out-of-range index is a caller error whether
        /// or not there was room.
        /// </remarks>
        public void Insert(int index, T item)
        {
            if ((uint)index > (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (RefuseUnsendableElement(item, "Insert")) return;
            if (OutboundWritesClosed) return;
            if (RefuseAtCapacity("Insert")) return;
            _items.Insert(index, item);
            EnqueueOp(new PendingOp(ListOp.Insert, index, item));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Insert, index, item, default));
        }

        /// <summary>Remove the element at <paramref name="index"/>.</summary>
        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (OutboundWritesClosed) return;
            T removed = _items[index];
            _items.RemoveAt(index);
            EnqueueOp(new PendingOp(ListOp.RemoveAt, index, default));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.RemoveAt, index, default, removed));
        }

        /// <summary>
        /// Remove the first occurrence of <paramref name="item"/>.
        /// Returns <see langword="true"/> when the item was found and removed.
        /// </summary>
        /// <remarks>
        /// ⚠️ The answer is measured, not assumed. Finding the element is not
        /// the same as removing it: <see cref="RemoveAt"/> returns without
        /// touching the list when outbound writes are closed — after despawn,
        /// and now also on a client that does not own the object — so a
        /// <see langword="true"/> derived from the lookup alone reports a
        /// removal that did not happen, which is precisely the silence the
        /// refusal exists to end.
        /// </remarks>
        public bool Remove(T item)
        {
            int idx = _items.IndexOf(item);
            if (idx < 0) return false;

            int before = _items.Count;
            RemoveAt(idx);
            return _items.Count != before;
        }

        /// <summary>Empty the list.</summary>
        public void Clear()
        {
            if (_items.Count == 0 && _pendingOps.Count == 0) return;
            if (OutboundWritesClosed) return;

            _items.Clear();
            // Clear collapses any prior queued ops — the receiver only needs
            // the final empty state.  A subsequent Add still queues normally.
            _pendingOps.Clear();
            EnqueueOp(new PendingOp(ListOp.Clear, 0, default));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Clear, -1, default, default));
        }

        /// <summary>True when <paramref name="item"/> is in the list.</summary>
        public bool Contains(T item) => _items.IndexOf(item) >= 0;

        /// <summary>Index of the first occurrence of <paramref name="item"/>, or -1.</summary>
        public int IndexOf(T item) => _items.IndexOf(item);

        /// <summary>
        /// Iterate the list contents in insertion order.  Allocates a single
        /// enumerator per foreach loop; mutating the list during enumeration
        /// will invalidate the enumerator (same contract as <see cref="List{T}"/>).
        /// </summary>
        public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

        // ── Op log helpers ─────────────────────────────────────────────────────

        private readonly struct PendingOp
        {
            public readonly ListOp Op;
            public readonly int    Index;
            public readonly T      Value;
            public PendingOp(ListOp op, int index, T value)
            {
                Op    = op;
                Index = index;
                Value = value;
            }
        }

        private void EnqueueOp(PendingOp op)
        {
            // Soft promotion to FullSync when the queue grows large enough.
            // Mass mutations (e.g. shuffling an inventory) collapse to a
            // single FullSync rather than 100+ tiny deltas.
            if (_pendingOps.Count >= FullSyncOpThreshold)
            {
                _pendingOps.Clear();
                _pendingOps.Add(new PendingOp(ListOp.FullSync, _items.Count, default));
                return;
            }
            _pendingOps.Add(op);
        }

        private void Raise(NetworkVariableListChangeEvent<T> evt)
        {
            try { OnListChanged?.Invoke(evt); }
            catch (Exception ex)
            {
                // Raised once per applied op, and a replica's ops come from the
                // wire — so a subscriber that throws on one throws on all of
                // them, at the rate the sender chooses.
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastListChangedThrowWarnTicks))
                    Debug.LogError(
                        $"[RTMPE] NetworkVariableList<{typeof(T).Name}>.OnListChanged subscriber threw " +
                        $"{ex.GetType().Name}: {ex.Message}.");
            }
        }

        // ── Resync handling ────────────────────────────────────────────────────

        /// <summary>
        /// Override the base resync hook so that a late joiner gets a single
        /// FullSync op instead of an empty delta-log.  The base class only sets
        /// IsDirty — without this override we would emit a 1-byte payload with
        /// op_count = 0, which carries no state.
        /// </summary>
        internal override void MarkDirtyForResync()
        {
            // Guarded on this repair's own framing — "the scalars enforce these
            // on every path and a list enforced none" — and this is a path: it
            // clears the op log, queues a FullSync and sets IsDirty, which is the
            // whole of the outbound state.  ⛔ Unreachable post-despawn today
            // (its caller walks the registry, which holds only spawned objects),
            // so this is defence in depth and is not the finding.
            if (OutboundWritesClosed) return;

            // Replace any pending ops with a single FullSync — a late joiner
            // only needs the current state, not the historical mutations.
            _pendingOps.Clear();
            _pendingOps.Add(new PendingOp(ListOp.FullSync, _items.Count, default));
            base.MarkDirtyForResync();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The periodic full-sync this file's header has always described.  A
        /// list's steady-state payload is a delta against the contents the
        /// receiver is assumed to hold, and the delta log is not idempotent — an
        /// <c>Add</c> that arrives twice appends twice and one that never
        /// arrives is never asked for again.  Delivery is best-effort whenever
        /// the ARQ extension is not negotiated, so without this a single lost
        /// datagram leaves owner and replica permanently apart, silently: no
        /// sequence number covers the list stream, and the last-writer-wins tick
        /// gate cannot tell a missed update from an unchanged one.
        ///
        /// <para>Only a list that has been sent at least once is refreshed.
        /// <c>LastFlushTimeUnscaled</c> is zero before the first successful
        /// flush and is returned there by <c>ResetThrottleState</c> on an
        /// ownership handover, so neither a newly spawned object nor a client
        /// that has just taken one buys a snapshot it has no prior state to
        /// correct.</para>
        /// </remarks>
        internal override bool IsDueForPeriodicResync(float nowUnscaled)
        {
            float interval = ResolveFullSyncIntervalSeconds();
            if (interval <= 0f) return false;

            // Never flushed: there is no replica state to refresh, and the first
            // ordinary flush will carry the whole list anyway.
            float sent = LastFlushTimeUnscaled;
            if (sent <= 0f) return false;

            // Measured from the later of the last send and the last attempt, so
            // a list whose snapshot does not fit is reconsidered once per
            // interval rather than on every tick for the rest of the session.
            float since = Math.Max(sent, _lastResyncAttemptUnscaled);

            // ⛔ The FIRST refresh is offset, and only the first.  A player
            // joining runs MarkAllVariablesDirtyForResync over every variable of
            // every owned object, which resets each one's flush clock to the
            // same frame — so without this every list in the scene would come
            // due on the same tick, and go on doing so for ever.  One offset is
            // enough: afterwards each list measures from its own last refresh,
            // and the period stays exactly `interval` rather than drifting.
            float due = _lastResyncAttemptUnscaled > 0f
                ? interval
                : interval + FirstResyncOffsetSeconds(interval);
            return nowUnscaled - since >= due;
        }

        /// <inheritdoc/>
        internal override bool TryBeginPeriodicResync(
            float nowUnscaled, int roomBytes, bool payloadIsEmpty)
        {
            if (!IsDueForPeriodicResync(nowUnscaled)) return false;

            // ⛔ The check that keeps this a refresh rather than a wedge. A list
            // is CLEAN whenever its last flush succeeded, and a delta flush
            // succeeds at any size — so a list of four hundred elements can be
            // clean, replicating perfectly by deltas, and have a snapshot that
            // does not fit a datagram. Arming one would queue a FullSync that
            // Serialize prefers over every later delta and that only a
            // successful flush retires: replication would end there, on a timer,
            // for exactly the lists that were working.
            //
            // ⛔ Silent, deliberately — and the cost of that is stated rather
            // than assumed. A list this refuses is still replicating by deltas,
            // so a warning about it would be one about working code; but a lost
            // delta stays permanent for it, and nothing here says so. The flush
            // names such a list once a second only once something ELSE marks it
            // dirty — a player joining, or a burst of edits past
            // FullSyncOpThreshold. With neither, the refresh is inert and
            // silent, which is why it is written into the API reference and the
            // tuning guide instead of left to be discovered.
            if (SnapshotBytesWithId() > roomBytes)
            {
                // ⛔ Stamped only when the whole datagram was free.  A refusal at
                // a payload that already carries something is about THIS TICK'S
                // ordering and not about the list — the rotation above the caller
                // gives it a turn at being first — and charging it a full
                // interval would defer a list that merely came second.  When the
                // payload was empty no later tick answers differently, and the
                // stamp is what stops an O(n) measurement running at 30 Hz for
                // the rest of the session.
                if (payloadIsEmpty) _lastResyncAttemptUnscaled = nowUnscaled;
                return false;
            }

            _lastResyncAttemptUnscaled = nowUnscaled;

            // MarkDirtyForResync declines on a despawned or unowned list, so the
            // dirty flag is the answer rather than the call being made.
            MarkDirtyForResync();
            return IsDirty;
        }

        /// <summary>
        /// A deterministic slice of one interval, spreading the first refresh of
        /// every list in the scene across the window instead of stacking them on
        /// one frame.
        /// </summary>
        /// <remarks>
        /// Mixed from the OBJECT as well as the variable: ids are unique within
        /// an object and not across them, so a phase taken from
        /// <c>VariableId</c> alone would give variable 0 of every object the
        /// same one and spread nothing.
        /// </remarks>
        private float FirstResyncOffsetSeconds(float interval)
        {
            ulong mix = (Owner != null ? Owner.NetworkObjectId : 0UL) * 31UL + VariableId;
            return interval * ((mix % ResyncPhaseBuckets) / (float)ResyncPhaseBuckets);
        }

        private const ulong ResyncPhaseBuckets = 64UL;

        // The last tick a refresh was performed, or refused on a free datagram.
        // A snapshot that no tick can carry then costs one measurement per
        // interval rather than one per tick.
        private float _lastResyncAttemptUnscaled;

        // Reused across measurements; SetLength(0) keeps the grown capacity — so a
        // list retains a buffer sized by the largest snapshot it has ever been
        // measured at, which for one that can never be sent exists only to keep
        // discovering that (~1.6 KB per 400-element int list). Measured at 1.2 us
        // per attempt, once per interval, and kept because growing it again on
        // every measurement would be the larger cost.
        private MemoryStream _measureMs;
        private BinaryWriter _measureBw;

        /// <summary>
        /// The framed wire length of a FullSync of the current contents —
        /// <c>[var_id:4][value_len:2]</c> plus what <see cref="WriteFullSync"/>
        /// writes.
        /// </summary>
        /// <remarks>
        /// Measured by writing it, rather than computed from an element size:
        /// a subclass element is whatever <c>WriteElement</c> makes of it, and
        /// <c>NetworkVariableListString</c> has no fixed width at all. Arithmetic
        /// here would be right for three of the four shipped lists and silently
        /// wrong for the fourth.
        /// </remarks>
        private int SnapshotBytesWithId()
        {
            if (_measureMs == null)
            {
                _measureMs = new MemoryStream(256);
                _measureBw = new BinaryWriter(_measureMs, Encoding.UTF8, leaveOpen: true);
            }
            _measureMs.SetLength(0);
            try
            {
                WriteFullSync(_measureBw);
                _measureBw.Flush();
            }
            catch (Exception)
            {
                // ⛔ WriteElement is a public extension point and a subclass's
                // may throw.  SerializeWithId contains that for the ordinary
                // flush — it reports once a second and publishes an empty
                // record — and this path had no such contract: the throw would
                // reach FlushAll, which re-dirties every variable on the
                // component and undoes the whole tick's flush, once per refresh
                // interval for ever.  A snapshot that cannot be written is a
                // snapshot that does not fit, and the element fault reports
                // itself through the flush the first time the list is dirty.
                return int.MaxValue;
            }
            return (int)_measureMs.Length + VariableIdAndLengthBytes;
        }

        /// <summary>
        /// The <c>[var_id:4 LE][value_len:2 LE]</c> prefix
        /// <c>NetworkVariableBase.SerializeWithId</c> writes ahead of the value.
        /// </summary>
        /// <remarks>
        /// Derived from the two field types rather than written as a number.
        /// This measurement decides whether a snapshot is offered or retracted,
        /// so a prefix that grew while the constant did not would arm a wedge
        /// the flush then walks into — and a literal that is right on the day it
        /// is written is the one no test can tell from a wrong one.
        /// </remarks>
        private const int VariableIdAndLengthBytes = sizeof(uint) + sizeof(ushort);

        // ── Subclass extension points ──────────────────────────────────────────

        /// <summary>Encode <paramref name="value"/> to <paramref name="writer"/>.</summary>
        protected abstract void WriteElement(BinaryWriter writer, T value);

        /// <summary>
        /// Whether <paramref name="value"/> is an element
        /// <see cref="WriteElement"/> can put on the wire.  Default: everything.
        /// </summary>
        /// <param name="value">The candidate element.</param>
        /// <param name="reason">Set when the answer is <see langword="false"/>.</param>
        /// <remarks>
        /// The list's half of the rule <c>NetworkVariable{T}.IsSendableValue</c>
        /// states for scalars: <b>a variable must not accept a value its own
        /// writer or reader refuses.</b>
        ///
        /// <para>⛔ Consulted by every mutator, not by a list of them. The
        /// scalar rule was first written as three named write paths and the
        /// string type — a fourth — was outside it; that omission is what this
        /// hook exists to make impossible for elements.</para>
        /// </remarks>
        protected virtual bool IsSendableElement(T value, out string reason)
        {
            reason = null;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="value"/> would be accepted as an element.
        /// </summary>
        /// <remarks>
        /// ⚠️ A refused element is a SILENT no-op: <see cref="Add"/> does not
        /// append, <see cref="Count"/> does not move, and no change event fires.
        /// Any caller keeping its own index arithmetic against
        /// <see cref="Count"/> diverges from this list without noticing, which
        /// is why the question is askable and <see cref="TryAdd"/> exists.
        ///
        /// <para>⚠️ Two things this does NOT answer.  It says nothing about room
        /// — a list at <see cref="MaxCount"/> refuses a value this returns
        /// <see langword="true"/> for, and <see cref="IsFull"/> is that half of
        /// the question.  And a <see langword="false"/> is not necessarily about
        /// the element at all: it is also the answer after the owning behaviour
        /// has despawned and on a client that does not own it.
        /// <see cref="TryAdd"/> answers all three at once.</para>
        /// </remarks>
        public bool CanSend(T value) => IsSendableElement(value, out _) && !WriteWouldNotLand;

        /// <summary>
        /// Append <paramref name="item"/>, reporting whether it was accepted.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when the element was refused — see
        /// <see cref="CanSend"/> — when the list is already at
        /// <see cref="MaxCount"/>, or when outbound writes are closed: after the
        /// owning behaviour has despawned, and on a client that does not own it.
        /// </returns>
        public bool TryAdd(T item)
        {
            if (!IsSendableElement(item, out _)) { RefuseUnsendableElement(item, "TryAdd"); return false; }

            // Asked silently: the caller is being handed the answer, and a
            // console line beside a returned false is noise it did not ask for.
            if (WriteWouldNotLand) return false;

            // Reported rather than asked silently, and in this order for the
            // reason Add states: a full list is a property of the list and not
            // of this call, so the next caller meets it too and the one line
            // that names the ceiling is worth more than silence.
            if (RefuseAtCapacity("TryAdd")) return false;

            Add(item);
            return true;
        }

        private static long _lastUnsendableElementWarnTicks;

        private bool RefuseUnsendableElement(T value, string site)
        {
            if (IsSendableElement(value, out string reason)) return false;

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastUnsendableElementWarnTicks))
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}> (id {VariableId} on " +
                    $"{OwnerLabel}) refused an element at {site}: {reason} The list is " +
                    "unchanged — Count did not move. Accepting it would throw out of " +
                    "WriteElement on the next flush, which has no catch — taking every " +
                    "variable behind it with it, on every tick, for the rest of the session.");
            return true;
        }

        // Shared by every growing mutator, and static per closed generic for the
        // same reason as the element gate above: a scene holding many lists of
        // one type meets the ceiling on all of them at once, and a per-instance
        // budget would be freshly open for each.
        private static long _lastCapacityRefusalWarnTicks;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test seam: reopen the shared capacity-refusal gate.
        /// </summary>
        /// <remarks>
        /// 🚨 Without it a case asserting the refusal REPORTS is measuring
        /// whichever earlier case in the same class spent the gate — one
        /// adversarial pass found exactly that, and a second found it again in
        /// the case written to close the first. The gate is static per closed
        /// generic, and a shard runs in under a tenth of a second.
        /// </remarks>
        internal static void ResetCapacityWarnGateForTest()
            => _lastCapacityRefusalWarnTicks = 0;
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Whether a growth by one must be refused because the list is at
        /// <see cref="MaxCount"/>.  Consulted by every mutator that can grow the
        /// list, never by a list of their names.
        /// </summary>
        private bool RefuseAtCapacity(string site)
        {
            // Unknown is not a licence and not a ceiling: with no settings asset
            // reachable this refuses nothing, and the receiver's own bound is
            // what still holds.  See TryResolveMaxListSize.
            if (!TryResolveMaxListSize(out int max)) return false;
            if (_items.Count < max) return false;

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastCapacityRefusalWarnTicks))
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}> (id {VariableId} on " +
                    $"{OwnerLabel}) refused a growth at {site}: the list holds {_items.Count} " +
                    $"elements and NetworkSettings.maxNetworkVariableListSize is {max}. The " +
                    "list is unchanged — Count did not move. Every receiver drops an inbound " +
                    "element past this ceiling, so growing past it here would leave this " +
                    "client holding elements no other client is allowed to store.");
            return true;
        }

        /// <summary>Decode the next element from <paramref name="reader"/>.</summary>
        protected abstract T ReadElement(BinaryReader reader);

        // ── Wire serialisation ─────────────────────────────────────────────────

        public override void Serialize(BinaryWriter writer)
        {
            // Re-validate the ops queue — Clear() collapses earlier ops, but a
            // hostile or buggy subclass override could still leave it empty.
            if (_pendingOps.Count == 0)
            {
                writer.Write((byte)0);
                return;
            }

            // If any FullSync was queued, emit ONLY that op.  Receivers see the
            // current state immediately and earlier deltas would be redundant.
            for (int i = 0; i < _pendingOps.Count; i++)
            {
                if (_pendingOps[i].Op == ListOp.FullSync)
                {
                    WriteFullSync(writer);
                    return;
                }
            }

            // If the queued op count exceeds the wire's single-byte cap (255),
            // collapse the entire log into a single FullSync.  This bounds
            // every payload to one tick of work and guarantees the receiver
            // converges to the authoritative state without partial-delta
            // ordering ambiguity.
            if (_pendingOps.Count > MaxOpsPerPayload)
            {
                // Rate-limited: the log survives serialisation now, so a list
                // whose FullSync does not fit a datagram takes this branch on
                // every tick it stays dirty rather than once per overflow.
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastOpLogOverflowWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: pending op log " +
                        $"({_pendingOps.Count}) exceeds wire cap ({MaxOpsPerPayload}); " +
                        "promoting this flush to a FullSync.");
                WriteFullSync(writer);
                return;
            }

            int n = _pendingOps.Count;
            writer.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                var op = _pendingOps[i];
                writer.Write((byte)op.Op);
                switch (op.Op)
                {
                    case ListOp.Add:
                        WriteElement(writer, op.Value);
                        break;
                    case ListOp.Insert:
                    case ListOp.Set:
                        writer.Write((ushort)op.Index);
                        WriteElement(writer, op.Value);
                        break;
                    case ListOp.RemoveAt:
                        writer.Write((ushort)op.Index);
                        break;
                    case ListOp.Clear:
                        // no payload
                        break;
                }
            }
        }

        /// <summary>
        /// Retire the op log alongside the dirty flag.
        /// </summary>
        /// <remarks>
        /// The log is the value of this variable as far as the wire is
        /// concerned, so it is retired where every other variable retires its
        /// value: once the flush has established that the bytes it produced
        /// can actually be sent. Retiring it inside <see cref="Serialize"/>
        /// instead — which is what this class used to do — discarded the
        /// operations at the moment they were written, so a payload the
        /// datagram cap then refused took the list's state with it and nothing
        /// re-sent it.
        /// </remarks>
        public override void MarkClean()
        {
            _pendingOps.Clear();
            base.MarkClean();
        }

        public override void Deserialize(BinaryReader reader)
        {
            // Drop a late inbound payload for a torn-down object, on the same
            // terms as the scalars' SetValueWithoutNotify: the owner may have
            // cleared its subscribers and the GameObject may be mid-Destroy, so
            // applying the ops here would let user code observe post-despawn
            // state through whatever callbacks remain in flight.
            //
            // ⛔ Returning WITHOUT reading is safe here and would not be
            // everywhere: NetworkBehaviour.ApplyVariableUpdate's contract puts
            // the entry's length with the CALLER — it hands this a reader over
            // exactly value_len bytes and re-seeks the batch afterwards, which
            // is the same contract the unknown-variable-id path relies on when
            // it warns and does not read.  A guard placed above a read in a
            // method that owned its own framing would desynchronise every entry
            // after it.
            if (InboundWritesClosed) return;

            byte opCount = reader.ReadByte();
            if (opCount == 0) return;

            // The configured ceiling bounds the list against an attacker who
            // streams unbounded Add/Insert deltas; resolved once per payload so
            // the per-op checks below do not repeat the settings lookup.
            int maxListSize = ResolveMaxListSize();

            // The op log applies atomically.  A malformed element, an exhausted
            // stream, or an unknown op must leave the list exactly as it was
            // before this payload — so the pre-payload contents are captured
            // here and restored on any failure, and change notifications are
            // queued rather than dispatched until the whole payload has been
            // confirmed applied.
            _rollbackBuffer.Clear();
            _rollbackBuffer.AddRange(_items);
            _deferredChanges.Clear();

            // The elements carried in from before the spawn are superseded by
            // the first payload that arrives, not added to by it: the sender
            // knows nothing of them, so its ops enumerate the list from empty
            // and applying them on top is how three elements become six.  The
            // discard sits inside the payload's atomic window so a rejected
            // payload restores them along with everything else, and it is
            // announced as a Clear because a subscriber holding an index-keyed
            // mirror is owed the same notice the wire's own Clear gives it.
            bool discardedPreSpawnContents = _preSpawnContentsPending && _items.Count > 0;
            if (discardedPreSpawnContents)
            {
                _items.Clear();
                _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                    NetworkListChangeKind.Clear, -1, default, default));
            }

            try
            {
                ApplyOpLog(reader, opCount, maxListSize);
            }
            catch (Exception ex)
            {
                // 🚨 Gated, like every other diagnostic in this file. This one
                // was not, and it is the one an attacker can drive: `Deserialize`
                // runs once per variable ENTRY, so a batch of 153 malformed
                // entries against a single registered list produced 153 warnings
                // from one datagram — each capturing a managed stack trace and a
                // player-log write in Unity, none of which the headless stub
                // shows. It was the dominant cost of what remained after the
                // declared-length repair beside it.
                if (WarnGate.ShouldEmit(ref _lastDeserialiseRejectionWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariableList<{typeof(T).Name}>.Deserialize: payload " +
                        $"rejected ({ex.GetType().Name}: {ex.Message}).  Restoring the " +
                        "pre-payload contents so owner and receiver stay converged.");
                _items.Clear();
                _items.AddRange(_rollbackBuffer);
                _rollbackBuffer.Clear();
                _deferredChanges.Clear();
                return;
            }

            // Payload applied cleanly — release the snapshot and dispatch the
            // queued notifications now that the list is in a consistent state.
            // The queued events are moved into a local before dispatch and the
            // shared field is cleared first: a subscriber that synchronously
            // re-enters Deserialize on this same instance then operates on a
            // fresh _deferredChanges and cannot corrupt this loop's iteration.
            _rollbackBuffer.Clear();
            _preSpawnContentsPending = false;

            // The op log that built the discarded elements describes a state
            // this list no longer holds.  Where the writes that produced it
            // could not have reached the wire in the first place, nothing will
            // ever flush it — so it would sit until an ownership change made
            // this client the sender and replayed a life's worth of local
            // construction into the room.
            if (discardedPreSpawnContents && WriteWouldNotLand) MarkClean();

            if (_deferredChanges.Count > 0)
            {
                var pending = _deferredChanges.ToArray();
                _deferredChanges.Clear();
                for (int i = 0; i < pending.Length; i++)
                    Raise(pending[i]);
            }
        }

        // Apply every op in the payload to _items, queueing change events into
        // _deferredChanges.  Throws on a corrupt payload (truncation, unknown
        // op, oversized FullSync) so the caller can revert atomically; benign
        // out-of-range Insert/RemoveAt/Set indices are skipped op-locally,
        // matching the receiver-tolerance contract in the file header.
        // Emission gate for the op-log refusals.  Every one of them means the
        // same thing — the peer sent an op this list cannot apply — and none is
        // actionable locally, so one gate serves them all.  Static, and it has
        // to be: the op count is a wire byte inside the per-variable loop, which
        // is itself a wire byte, so the product is the sender's to choose.
        // 🚨 One gate per refusal, where a single `_lastOpLogRefusalWarnTicks`
        // used to serve all five. They are five different faults in an inbound
        // op log — a cumulative ceiling, two index bounds and a second ceiling —
        // and a peer streaming over-cap Adds was deciding whether an
        // index-out-of-range report was ever written.
        private static long _lastAddOverCapWarnTicks;
        private static long _lastInsertIndexWarnTicks;
        private static long _lastInsertOverCapWarnTicks;
        private static long _lastRemoveAtIndexWarnTicks;
        private static long _lastSetIndexWarnTicks;

        // Static, per closed generic: a state frame carries ops for many lists,
        // and a per-list budget would be freshly open for each of them.
        private static long _lastListChangedThrowWarnTicks;

        private void ApplyOpLog(BinaryReader reader, byte opCount, int maxListSize)
        {
            for (int i = 0; i < opCount; i++)
            {
                if (reader.BaseStream.Position >= reader.BaseStream.Length)
                    throw new EndOfStreamException(
                        $"op log truncated after {i} of {opCount} ops");

                ListOp op = (ListOp)reader.ReadByte();
                switch (op)
                {
                    case ListOp.Add:
                    {
                        T val = ReadElement(reader);
                        // Cumulative ceiling: an Add that would push the list
                        // past its configured maximum is skipped, so a stream
                        // of Add deltas cannot grow the receiver without bound.
                        if (_items.Count >= maxListSize)
                        {
                            if (WarnGate.ShouldEmit(ref _lastAddOverCapWarnTicks))
                            {
                                Debug.LogWarning(
                                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Add would " +
                                    $"exceed the configured max {maxListSize}.  Dropping op.");
                            }
                            break;
                        }
                        int idx = _items.Count;
                        _items.Add(val);
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Add, idx, val, default));
                        break;
                    }
                    case ListOp.Insert:
                    {
                        ushort idx = reader.ReadUInt16();
                        T val = ReadElement(reader);
                        if (idx > _items.Count)
                        {
                            if (WarnGate.ShouldEmit(ref _lastInsertIndexWarnTicks))
                            {
                                Debug.LogWarning(
                                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Insert index " +
                                    $"{idx} exceeds Count {_items.Count}.  Dropping op.");
                            }
                            break;
                        }
                        if (_items.Count >= maxListSize)
                        {
                            if (WarnGate.ShouldEmit(ref _lastInsertOverCapWarnTicks))
                            {
                                Debug.LogWarning(
                                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Insert would " +
                                    $"exceed the configured max {maxListSize}.  Dropping op.");
                            }
                            break;
                        }
                        _items.Insert(idx, val);
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Insert, idx, val, default));
                        break;
                    }
                    case ListOp.RemoveAt:
                    {
                        ushort idx = reader.ReadUInt16();
                        if (idx >= _items.Count)
                        {
                            if (WarnGate.ShouldEmit(ref _lastRemoveAtIndexWarnTicks))
                            {
                                Debug.LogWarning(
                                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: RemoveAt index " +
                                    $"{idx} out of range (Count={_items.Count}).  Dropping op.");
                            }
                            break;
                        }
                        T removed = _items[idx];
                        _items.RemoveAt(idx);
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.RemoveAt, idx, default, removed));
                        break;
                    }
                    case ListOp.Set:
                    {
                        ushort idx = reader.ReadUInt16();
                        T val = ReadElement(reader);
                        if (idx >= _items.Count)
                        {
                            if (WarnGate.ShouldEmit(ref _lastSetIndexWarnTicks))
                            {
                                Debug.LogWarning(
                                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Set index " +
                                    $"{idx} out of range (Count={_items.Count}).  Dropping op.");
                            }
                            break;
                        }
                        T previous = _items[idx];
                        _items[idx] = val;
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Set, idx, val, previous));
                        break;
                    }
                    case ListOp.Clear:
                    {
                        _items.Clear();
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Clear, -1, default, default));
                        break;
                    }
                    case ListOp.FullSync:
                    {
                        ushort count = reader.ReadUInt16();

                        // Defence against an attacker-controlled wire field:
                        // pre-allocating capacity for the full uint16 range
                        // would commit ~512 KB per variable per tick at
                        // 16-byte elements.  A FullSync above the configured
                        // ceiling is rejected outright — the atomic restore in
                        // Deserialize keeps owner and receiver converged.
                        if (count > maxListSize)
                            throw new InvalidDataException(
                                $"FullSync count {count} exceeds configured max {maxListSize}");

                        _items.Clear();

                        // ⚠️ Pre-sized from what the payload can actually carry,
                        // not from what it CLAIMS. `count` is a wire field, and
                        // `maxListSize` bounds it at 1024 by default — so an
                        // 8-byte FullSync declaring 1024 elements committed
                        // ~10 KB of list capacity, the same "a declared number
                        // sizes an allocation" shape `WireByteBlock` exists to
                        // remove. Every element costs at least one byte on the
                        // wire, so the bytes remaining are an upper bound on how
                        // many can be there; a legitimate payload carries them
                        // and keeps the single allocation.
                        long remaining = reader.BaseStream.CanSeek
                            ? reader.BaseStream.Length - reader.BaseStream.Position
                            : 0;
                        int feasible = (int)Math.Max(0, Math.Min(count, remaining));
                        if (feasible > 0)
                            _items.Capacity = Math.Max(_items.Capacity, feasible);
                        for (int k = 0; k < count; k++)
                        {
                            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                                throw new EndOfStreamException(
                                    $"FullSync truncated after {k} of {count} elements");
                            _items.Add(ReadElement(reader));
                        }
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.FullSync, -1, default, default));
                        break;
                    }
                    default:
                        throw new InvalidDataException(
                            $"unknown op 0x{(byte)op:X2} at index {i} of {opCount}");
                }
            }
        }

        // One-line-per-second gates for this class's two diagnostics: the
        // op-log overflow warning in Serialize above, and the list-size error
        // in WriteFullSync below.  Both conditions persist across ticks — the
        // op log survives serialisation — so an ungated emission would be one
        // line per tick per list for as long as the list stays too large.
        private long _lastOpLogOverflowWarnTicks;

        // Its own gate: a list whose payloads are being rejected every tick must
        // not silence the op-log refusals, and the rate this fires at is the
        // diagnosis.
        private long _lastDeserialiseRejectionWarnTicks;
        private long _lastFullSyncCapErrorTicks;

        private void WriteFullSync(BinaryWriter writer)
        {
            // The wire format encodes the count as a 2-byte unsigned integer,
            // so lists larger than 65535 cannot be serialized as a FullSync.
            // Silent truncation would desync owner and remote — log a hard
            // error and emit a zero-length sync so receivers see "list cleared"
            // rather than a corrupted partial mirror.
            if (_items.Count > ushort.MaxValue)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastFullSyncCapErrorTicks))
                    Debug.LogError(
                        $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: list size {_items.Count} " +
                        $"exceeds wire cap {ushort.MaxValue}.  FullSync aborted; remote receivers " +
                        "will see an empty list until the size drops below the cap.");
                writer.Write((byte)1);
                writer.Write((byte)ListOp.FullSync);
                writer.Write((ushort)0);
                return;
            }
            writer.Write((byte)1);                  // op_count = 1
            writer.Write((byte)ListOp.FullSync);
            int count = _items.Count;
            writer.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                WriteElement(writer, _items[i]);
            }
        }

        // Test seam: per-instance override of the FullSync size cap so unit
        // tests can exercise the gate without standing up a full
        // NetworkManager + NetworkSettings asset.  Negative leaves the
        // setting-driven default in effect.  The field is always present so
        // ResolveMaxListSize stays branch-stable across builds; only the
        // mutator is excluded from the shipped Player assembly.
        private int _testMaxListSize = -1;

#if UNITY_INCLUDE_TESTS
        internal void SetMaxListSizeForTest(int max) => _testMaxListSize = max;
#endif // UNITY_INCLUDE_TESTS

        private int ResolveMaxListSize()
            => TryResolveMaxListSize(out int max) ? max : DefaultMaxListSize;

        /// <summary>
        /// The ceiling, and whether it came from a real source rather than from
        /// this file's fallback.
        /// </summary>
        /// <remarks>
        /// ⛔ The two answers are not interchangeable, and the receive path and
        /// the write path want different ones.  A receiver must have a number —
        /// an inbound payload has to be bounded by something — so it takes the
        /// fallback.  A writer must not <em>refuse</em> on one: a list built in
        /// <c>Awake</c>, before any <c>NetworkManager</c> has published itself,
        /// would be measured against a number this file invented, and a project
        /// that raised the setting would silently lose every element past 1024
        /// on exactly the path this class's own lifecycle design exists to
        /// support.  Depth must not refuse what the authority would admit.
        ///
        /// <para>Probed with <c>TryGetInstance</c> rather than
        /// <c>NetworkManager.Instance</c>: the property scans the scene and
        /// warns when nothing is published, and this runs on every
        /// <see cref="Add"/>.</para>
        /// </remarks>
        private bool TryResolveMaxListSize(out int max)
        {
            if (_testMaxListSize >= 0) { max = _testMaxListSize; return true; }

            if (RTMPE.Core.NetworkManager.TryGetInstance(out var manager)
                && manager?.Settings != null
                && manager.Settings.maxNetworkVariableListSize > 0)
            {
                max = manager.Settings.maxNetworkVariableListSize;
                return true;
            }

            max = DefaultMaxListSize;
            return false;
        }

        /// <summary>
        /// What an inbound payload is bounded by when no settings asset is
        /// reachable — a receive-side floor, never a reason to refuse a write.
        /// </summary>
        internal const int DefaultMaxListSize = 1024;

        // The companion seam for the resync interval.  Negative leaves the
        // setting-driven default in effect, and zero is a value a test may want
        // to assert on, so the disabled state is not the unset one.
        private float _testFullSyncIntervalSeconds = -1f;

#if UNITY_INCLUDE_TESTS
        internal void SetFullSyncIntervalForTest(float seconds)
            => _testFullSyncIntervalSeconds = seconds;
#endif // UNITY_INCLUDE_TESTS

        private float ResolveFullSyncIntervalSeconds()
        {
            if (_testFullSyncIntervalSeconds >= 0f) return _testFullSyncIntervalSeconds;

            // Zero is a setting and not an absence: it is how the refresh is
            // switched off, so it cannot double as "no asset said anything".
            // That is what the negative sentinel above is for, and what a
            // negative value arriving from a path that bypasses the Inspector
            // floor is read as here.
            if (RTMPE.Core.NetworkManager.TryGetInstance(out var manager)
                && manager?.Settings != null
                && manager.Settings.networkVariableListFullSyncIntervalSeconds >= 0f)
                return manager.Settings.networkVariableListFullSyncIntervalSeconds;
            return DefaultFullSyncIntervalSeconds;
        }

        /// <summary>
        /// The refresh cadence used when no <c>NetworkSettings</c> asset is
        /// reachable — held equal to the field's own default by a test.
        /// </summary>
        internal const float DefaultFullSyncIntervalSeconds = 5f;
    }

    /// <summary>Kind of change reported by <see cref="NetworkVariableList{T}.OnListChanged"/>.</summary>
    public enum NetworkListChangeKind : byte
    {
        Add      = 1,
        Insert   = 2,
        RemoveAt = 3,
        Set      = 4,
        Clear    = 5,
        FullSync = 6,
    }

    /// <summary>
    /// Event payload describing a single mutation applied to a
    /// <see cref="NetworkVariableList{T}"/>.  <see cref="Index"/> is meaningful
    /// only for Add/Insert/RemoveAt/Set; it is <c>-1</c> for Clear and FullSync.
    /// </summary>
    public readonly struct NetworkVariableListChangeEvent<T>
    {
        public readonly NetworkListChangeKind Kind;
        public readonly int Index;
        public readonly T   NewValue;
        public readonly T   PreviousValue;

        public NetworkVariableListChangeEvent(
            NetworkListChangeKind kind, int index, T newValue, T previousValue)
        {
            Kind          = kind;
            Index         = index;
            NewValue      = newValue;
            PreviousValue = previousValue;
        }
    }

    // ── Concrete subclasses ────────────────────────────────────────────────────

    /// <summary>Synchronised list of 32-bit signed integers.</summary>
    public sealed class NetworkVariableListInt : NetworkVariableList<int>
    {
        public NetworkVariableListInt(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, int value)
            => writer.Write(value);
        protected override int ReadElement(BinaryReader reader)
            => reader.ReadInt32();
    }

    /// <summary>Synchronised list of 32-bit IEEE-754 floats.</summary>
    // ⛔ A float LIST accepts a non-finite element where a float VARIABLE
    // refuses one, and the asymmetry is deliberate rather than an oversight.
    //
    // The rule is "a variable must not accept a value its own reader refuses" —
    // and ReadElement below is a bare ReadSingle with no finiteness gate, so a
    // NaN element does replicate: every peer receives it and every peer keeps
    // it. There is nothing lost and nothing to refuse. NetworkVariableFloat is
    // the opposite case: its Deserialize refuses a non-finite value and keeps
    // the receiver's PRIOR one, so accepting the write would show it to the
    // owner and to nobody else.
    //
    // ⚠️ That does not make a NaN in a list harmless — it reaches every peer's
    // OnListChanged, with the same hazard the scalar's comment names. But
    // refusing it here would be a NEW restriction the rule does not ask for,
    // taken against behaviour that works today, which is a different decision
    // and belongs to whoever owns the product surface rather than to an audit
    // remediation.

    public sealed class NetworkVariableListFloat : NetworkVariableList<float>
    {
        public NetworkVariableListFloat(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, float value)
            => writer.Write(value);
        protected override float ReadElement(BinaryReader reader)
            => reader.ReadSingle();
    }

    /// <summary>Synchronised list of <see cref="Vector3"/>.</summary>
    // ⛔ There is no NetworkVariableListVector2, and the absence is deliberate
    // rather than an oversight: the four shipped lists are the ones a game was
    // measured to need, and every list costs a wire form that has to be kept.
    // A game that wants one declares it — NetworkVariableList<T>'s constructor
    // is protected and WriteElement/ReadElement are the only two members a
    // subclass must supply, which is the same ten lines the scalar case takes.
    public sealed class NetworkVariableListVector3 : NetworkVariableList<Vector3>
    {
        public NetworkVariableListVector3(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        protected override Vector3 ReadElement(BinaryReader reader)
            => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <summary>
    /// Synchronised list of <see cref="string"/> values.  Each element is
    /// encoded as a 2-byte LE length prefix followed by UTF-8 bytes; null
    /// is normalised to <see cref="string.Empty"/> on write.
    /// </summary>
    public sealed class NetworkVariableListString : NetworkVariableList<string>
    {
        // Strict UTF-8 codec — the lax form silently substitutes U+FFFD for
        // malformed sequences, which lets a hostile peer smuggle bytes that
        // survive the decode but mutate downstream string-equality
        // invariants (kill-feed names compared against reserved sentinels,
        // chat-channel keys, etc.).  Symmetric with NetworkVariableString
        // and the RPC stack.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public NetworkVariableListString(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        /// <summary>
        /// Whether <paramref name="value"/> is one <see cref="WriteElement"/>
        /// can put on the wire.
        /// </summary>
        /// <remarks>
        /// 🔴 Same rule and same stakes as <c>NetworkVariableString</c>:
        /// <c>StrictUtf8</c> carries <c>throwOnInvalidBytes: true</c>, which
        /// sets the ENCODER fallback as well, so a lone surrogate throws out of
        /// <see cref="WriteElement"/> — inside the flush loop, which has no
        /// catch, on every tick, for ever. Refusing the element on the way in
        /// is the only place the list can still keep a coherent state.
        /// </remarks>
        protected override bool IsSendableElement(string value, out string reason)
        {
            string s = value ?? string.Empty;
            try
            {
                int byteCount = StrictUtf8.GetByteCount(s);
                if (byteCount > ushort.MaxValue)
                {
                    reason = $"the element is {byteCount} UTF-8 bytes, above the " +
                             $"{ushort.MaxValue}-byte wire maximum.";
                    return false;
                }
            }
            catch (EncoderFallbackException ex)
            {
                reason = "the element is not encodable as UTF-8 (" + ex.Message + ").";
                return false;
            }

            reason = null;
            return true;
        }

        protected override void WriteElement(BinaryWriter writer, string value)
        {
            string s = value ?? string.Empty;
            byte[] bytes = StrictUtf8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"NetworkVariableListString element is {bytes.Length} UTF-8 bytes — " +
                    $"exceeds {ushort.MaxValue}-byte wire limit.",
                    nameof(value));
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        protected override string ReadElement(BinaryReader reader)
        {
            ushort len = reader.ReadUInt16();
            if (len == 0) return string.Empty;
            // ⚠️ NOT `reader.ReadBytes(len)` — see `WireByteBlock`.  Every
            // element of a list carries its own declared length, so this site
            // is the same allocation primitive as the scalar one and worse per
            // packet: one FullSync payload can carry many of them.  The helper
            // raises the same `EndOfStreamException` on the same truncated
            // payload, which the FullSync apply path already handles by
            // dropping the whole payload.
            byte[] bytes = WireByteBlock.ReadExactly(
                reader, len, "NetworkVariableListString.ReadElement");
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new EndOfStreamException(
                    "NetworkVariableListString.ReadElement: malformed UTF-8 in element payload.",
                    ex);
            }
        }
    }
}
