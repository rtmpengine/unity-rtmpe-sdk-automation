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
// elem_bytes layout depends on the subclass, and each one is the scalar
// wrapper's own encoding for the same value type (NetworkVariableTypes.cs), so
// a list element and a variable of that type are one wire form:
//  NetworkVariableListInt        : 4-byte LE i32
//  NetworkVariableListFloat      : 4-byte LE f32
//  NetworkVariableListBool       : 1 byte (0 = false, 1 = true)
//  NetworkVariableListVector2    : 8-byte LE (x,y) f32
//  NetworkVariableListVector2Int : 8-byte LE (x,y) i32
//  NetworkVariableListVector3    : 12-byte LE (x,y,z) f32
//  NetworkVariableListString     : 2-byte LE ushort len + UTF-8 bytes
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
//  • The write is bounded by the datagram as well.  A joining player, the
//    periodic refresh and a burst of edits past FullSyncOpThreshold each
//    receive the whole list as one FullSync, and a FullSync travels as one
//    VariableUpdate entry — so a list whose snapshot exceeds
//    VariableFlushBudget.MaxEntryBytesAlone is one no joining player can
//    ever receive.  A growth that would carry the snapshot past that line
//    is refused where the count ceiling is, MaxCount and IsFull answer for
//    it, and a snapshot that does not fit is never armed: the delta log
//    keeps replicating to the replicas that hold the list, and the refusal
//    is reported once a second.
//  • The op log applies atomically.  A malformed element, a truncated
//    payload, or an unknown op reverts the list to its pre-payload contents,
//    and change notifications are dispatched only once the whole payload has
//    applied — a subscriber never observes an op the payload then discards.
//  • op_count is a single byte — at most 255 ops per flush — and a payload
//    is one datagram entry.  A flush writes the longest prefix of the op log
//    that fits both, and MarkClean retires that prefix: the surplus goes on
//    the next flush.  When the log exceeds a soft cap (configurable via
//    FullSyncOpThreshold), or would need more than one flush, and the whole
//    list fits one datagram, the flush is promoted to a full-sync instead.
//
// Performance:
//  • Mutation methods record a single struct in _pendingOps without
//    allocating per call; the change list is reused across flushes.  The
//    bytes the elements take on the wire are kept as a running total, so no
//    write walks the list to ask whether the snapshot still fits.
//  • Serialize iterates the change log into the BinaryWriter; MarkClean
//    retires what the flush carried.
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
using RTMPE.Core.Sync;

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
    /// A replicated list, such as an inventory, active effects or a grid of
    /// cells. The owner edits it and every client receives each change.
    /// Construct it in <c>OnNetworkSpawn</c> with <c>nameof(_field)</c>, like
    /// any network variable.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <remarks>
    /// <para>Use one of the concrete types, such as
    /// <see cref="NetworkVariableListInt"/>, or derive from this class and
    /// implement <see cref="WriteElement"/> and <see cref="ReadElement"/>.</para>
    /// <para>Edits are sent as changes. The whole list is sent instead when a
    /// player joins, when more than <see cref="FullSyncOpThreshold"/> edits are
    /// waiting or they need more than one send, and once the list has gone
    /// <c>NetworkSettings.networkVariableListFullSyncIntervalSeconds</c> (5 by
    /// default) without being sent. Every send restarts that interval, so a list
    /// edited more often than the interval is not re-sent whole by it. A change
    /// lost on the network stays lost until the whole list is sent again.</para>
    /// </remarks>
    public abstract class NetworkVariableList<T> : NetworkVariableBase, IReadOnlyList<T>
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
        //
        // Cleared in one place: the pool hand-back (OnOwnerRecycled), which is
        // the one event that says a next life is coming and its pre-spawn
        // writes are owed the same window the first life's were.  A despawn on
        // the way to Destroy never clears it.
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

        // Whether the payload Deserialize last applied stated the whole list —
        // a FullSync, or a Clear the ops after it enumerate from — as opposed
        // to edits of a list the sender assumes this client already holds.
        // Cleared on the way into every payload and by a rejected one, so it
        // speaks only of a payload that was applied.
        private bool _payloadStatesWholeList;

        /// <inheritdoc/>
        /// <remarks>
        /// A delta says nothing whole about a list this client never held the
        /// rest of: the elements it edits are the sender's, and the ones it
        /// leaves alone are wherever this client's copy had them.  So only a
        /// payload that stated the whole list is recorded.
        /// </remarks>
        internal override void NoteInboundDelivery()
        {
            if (_payloadStatesWholeList) HoldsDeliveredValue = true;
        }

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

        /// <inheritdoc/>
        /// <remarks>
        /// The op log is the sender's: edits this client made, against the
        /// state it held when it made them.  A client that has just stopped
        /// being the sender has nothing that will flush it — and a client that
        /// became the sender again later would flush edits against a state its
        /// peers no longer hold — so the log goes with the ownership.  A client
        /// that has just become the sender keeps what it holds, which is the
        /// pre-spawn build the lifecycle rule admits.
        /// </remarks>
        internal override void OnOwnerChanged()
        {
            if (!WriteWouldNotLand) return;
            _pendingOps.Clear();
            _opsCovered = NoFlushSinceRetire;
            IsDirty = false;
        }

        internal override void OnOwnerRecycled()
        {
            // The elements are the ended life's — the owner's last state, or the
            // wire's last delivery to a replica — and the op log describes
            // mutations no next life made.  Both go, and the emptying is
            // announced as a Clear, for the same reason the pre-spawn discard
            // in Deserialize is: a subscriber holding an index-keyed mirror is
            // owed the notice, or life 2's first Add lands on top of life 1's
            // rows in its view.  The pre-spawn window reopens with them: the
            // next life's Awake or OnEnable populates this list before it
            // spawns, which is the pattern the window exists to admit, and it
            // is the hand-back — not the despawn — that says a next life is
            // coming.  From here until that spawn a write is admitted as a
            // pre-spawn write, on a stale reference as on OnEnable's — the
            // instance is one the pool will hand out again, and what it holds
            // at its next spawn is what that life begins with.
            bool hadElements = _items.Count > 0;
            ItemsClear();
            _pendingOps.Clear();
            _opsCovered = NoFlushSinceRetire;
            _preSpawnContentsPending = false;
            _hasBeenSpawned = false;
            _lastResyncAttemptUnscaled = 0f;
            base.OnOwnerRecycled();
            if (hadElements)
                Raise(new NetworkVariableListChangeEvent<T>(NetworkListChangeKind.Clear, -1, default, default));
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

        /// <inheritdoc/>
        /// <remarks>
        /// A full sync of the current elements, whatever the op log holds and
        /// without touching it: <see cref="Serialize"/> writes the log, and an
        /// empty log — the common state of a list nobody has mutated since its
        /// last flush — carries no element at all.
        /// </remarks>
        internal override void SerializeSnapshot(BinaryWriter writer) => WriteFullSync(writer);

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
        /// The number of waiting edits above which the whole list is sent
        /// instead of the edits. The default is 32; at <c>0</c>, every change
        /// sends the whole list.
        /// </summary>
        /// <remarks>
        /// Lower it for a small list that changes continuously, so the whole list
        /// is sent often and a lost change is repaired quickly.
        /// </remarks>
        public int FullSyncOpThreshold { get; set; } = 32;

        // Apply-side cap to defend against malicious / malformed payloads.
        // No legitimate sender should ever emit more than op_count's max value.
        private const int MaxOpsPerPayload = 255;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised once for each change, after it is applied: on the owner when it
        /// edits the list, and on every other client when the change arrives.
        /// </summary>
        /// <remarks>
        /// A <see cref="NetworkListChangeKind.FullSync"/> is raised whenever the
        /// whole list arrives, for example when a player joins, usually with the
        /// contents this client already holds. Treat it as "read the list again"
        /// rather than as a change. An exception thrown by a handler is caught
        /// and logged.
        /// </remarks>
        public event Action<NetworkVariableListChangeEvent<T>> OnListChanged;

        // ── Construction ───────────────────────────────────────────────────────

        /// <summary>
        /// Creates an empty list registered with <paramref name="owner"/>.
        /// </summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
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

        /// <summary>The number of elements in the list.</summary>
        public int Count => _items.Count;

        /// <summary>
        /// The most elements this list can hold: the smaller of
        /// <c>NetworkSettings.maxNetworkVariableListSize</c> (default 1024) and
        /// the number of elements that fit in one datagram.
        /// </summary>
        /// <remarks>
        /// <para>The datagram limit applies because a joining player receives
        /// the whole list in one datagram. It is exact for fixed-size elements:
        /// 283 <c>int</c>s or <c>float</c>s,
        /// 141 <c>Vector2</c>s or <c>Vector2Int</c>s, and
        /// 94 <c>Vector3</c>s; a <c>bool</c> list reaches the configured limit
        /// first.</para>
        /// <para>For a list of strings the limit is the total size, so
        /// <see cref="MaxCount"/> counts empty strings; use <see cref="TryAdd"/>
        /// to find out whether a particular string fits.</para>
        /// </remarks>
        public int MaxCount => Math.Min(ResolveMaxListSize(), ElementsThatFitOneDatagram);

        /// <summary>
        /// Whether no further element can be added: the list is at
        /// <see cref="MaxCount"/>, or it has no room left for even the smallest
        /// element its type writes.
        /// </summary>
        /// <remarks>
        /// For a list of strings, <see langword="false"/> promises room only for
        /// the smallest element. Fill such a list with
        /// <c>while (list.TryAdd(next))</c>, not with a loop on
        /// <see cref="IsFull"/>.
        /// </remarks>
        public bool IsFull
            => AtConfiguredCeiling || !FitsAfterGrowthOf(SmallestElementBytes);

        /// <summary>Gets or replaces the element at <paramref name="index"/>.</summary>
        /// <param name="index">The element's position in the list.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is outside the list.
        /// </exception>
        /// <remarks>
        /// Setting raises a <see cref="NetworkListChangeKind.Set"/> change. A
        /// replacement does nothing, and logs a warning at most once a second,
        /// when the element cannot be sent, another player owns the list, or a
        /// larger element would make the list too big for one datagram; it also
        /// does nothing after the object has been despawned.
        /// </remarks>
        public T this[int index]
        {
            get => _items[index];
            set
            {
                if ((uint)index >= (uint)_items.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                T element = NormaliseElement(value);

                // Both refusals precede the lifecycle guard: an out-of-range
                // index and an unsendable element are caller errors whether or
                // not the object is spawned, and swallowing either after despawn
                // would hide a bug behind a lifecycle state.  (The bounds check
                // itself is above, and throws.)
                if (RefuseUnsendableElement(element, "indexer set")) return;
                if (OutboundWritesClosed) return;

                T previous = _items[index];
                if (RefuseReplacementPastTheDatagram(index, previous, element, out int before, out int after)) return;
                ItemsSet(index, element, before, after);
                EnqueueOp(PendingOp.Set(index, element, after));
                IsDirty = true;
                Raise(new NetworkVariableListChangeEvent<T>(
                    NetworkListChangeKind.Set, index, element, previous));
            }
        }

        /// <summary>Adds <paramref name="item"/> to the end of the list.</summary>
        /// <param name="item">The element to add.</param>
        /// <remarks>
        /// Raises an <see cref="NetworkListChangeKind.Add"/> change. Does nothing,
        /// and logs a warning at most once a second, when the list is full (see
        /// <see cref="MaxCount"/>), the element cannot be sent, or another player
        /// owns the list; it also does nothing after the object has been
        /// despawned. Use <see cref="TryAdd"/> to find out whether the element
        /// was added.
        /// </remarks>
        public void Add(T item)
        {
            item = NormaliseElement(item);
            if (RefuseUnsendableElement(item, "Add")) return;
            // ⛔ After the lifecycle gate, unlike the element refusal above. An
            // element this list can never put on the wire is a caller error
            // whatever state the object is in; a FULL list on a replica is not —
            // the wire filled it, the write was never this client's to make, and
            // reporting the ceiling there would name the wrong reason.
            if (OutboundWritesClosed) return;
            if (RefuseAtCapacity("Add", item, out int bytes)) return;
            int index = _items.Count;
            ItemsAdd(item, bytes);
            EnqueueOp(PendingOp.Add(index, item, bytes));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Add, index, item, default));
        }

        /// <summary>Inserts <paramref name="item"/> at <paramref name="index"/>.</summary>
        /// <param name="index">The position to insert at; <see cref="Count"/> adds to the end.</param>
        /// <param name="item">The element to insert.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is negative or greater than <see cref="Count"/>.
        /// </exception>
        /// <remarks>
        /// Raises an <see cref="NetworkListChangeKind.Insert"/> change. Does
        /// nothing in the cases <see cref="Add"/> describes.
        /// </remarks>
        public void Insert(int index, T item)
        {
            if ((uint)index > (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            item = NormaliseElement(item);
            if (RefuseUnsendableElement(item, "Insert")) return;
            if (OutboundWritesClosed) return;
            if (RefuseAtCapacity("Insert", item, out int bytes)) return;
            ItemsInsert(index, item, bytes);
            EnqueueOp(PendingOp.Insert(index, item, bytes));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Insert, index, item, default));
        }

        /// <summary>Removes the element at <paramref name="index"/>.</summary>
        /// <param name="index">The position of the element to remove.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is outside the list.
        /// </exception>
        /// <remarks>
        /// Raises a <see cref="NetworkListChangeKind.RemoveAt"/> change. Does
        /// nothing, and logs a warning at most once a second, when another player
        /// owns the list; it also does nothing after the object has been
        /// despawned.
        /// </remarks>
        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (OutboundWritesClosed) return;
            T removed = _items[index];
            ItemsRemoveAt(index);
            EnqueueOp(PendingOp.RemoveAt(index));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.RemoveAt, index, default, removed));
        }

        /// <summary>
        /// Removes the first occurrence of <paramref name="item"/>.
        /// </summary>
        /// <param name="item">The element to remove.</param>
        /// <returns>
        /// <see langword="true"/> when an element was removed;
        /// <see langword="false"/> when <paramref name="item"/> is not in the
        /// list, when another player owns the list, or after the object has been
        /// despawned.
        /// </returns>
        public bool Remove(T item)
        {
            int idx = IndexOf(item);
            if (idx < 0) return false;

            int before = _items.Count;
            RemoveAt(idx);
            return _items.Count != before;
        }

        /// <summary>Removes every element.</summary>
        /// <remarks>
        /// Raises a <see cref="NetworkListChangeKind.Clear"/> change. Does
        /// nothing, and logs a warning at most once a second, when another player
        /// owns the list; it also does nothing after the object has been
        /// despawned.
        /// </remarks>
        public void Clear()
        {
            if (_items.Count == 0 && _pendingOps.Count == 0) return;
            if (OutboundWritesClosed) return;

            ItemsClear();
            // Clear collapses any prior queued ops — the receiver only needs
            // the final empty state.  A subsequent Add still queues normally.
            _pendingOps.Clear();
            _opsCovered = NoFlushSinceRetire;
            EnqueueOp(PendingOp.Clear());
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Clear, -1, default, default));
        }

        /// <summary>Whether <paramref name="item"/> is in the list.</summary>
        /// <param name="item">The element to look for.</param>
        /// <returns><see langword="true"/> when the list holds the element.</returns>
        /// <remarks>
        /// The element is looked up in the form the list stores it (see
        /// <see cref="NormaliseElement"/>): a string list looks up
        /// <see langword="null"/> as an empty string.
        /// </remarks>
        public bool Contains(T item) => IndexOf(item) >= 0;

        /// <summary>
        /// The position of the first occurrence of <paramref name="item"/>, or
        /// -1 when the list does not hold it.
        /// </summary>
        /// <param name="item">The element to look for.</param>
        /// <returns>The element's position, or -1.</returns>
        /// <remarks>The element is looked up as <see cref="Contains"/> does.</remarks>
        public int IndexOf(T item) => _items.IndexOf(NormaliseElement(item));

        /// <summary>
        /// Returns an enumerator over the elements, in order. A <c>foreach</c>
        /// over the list does not allocate. Changing the list while enumerating
        /// invalidates the enumerator.
        /// </summary>
        /// <returns>An enumerator over the elements.</returns>
        public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

        // The interfaces behind the struct enumerator above.  IReadOnlyList<T>
        // — Count, the indexer's getter and the sequence — is what the public
        // surface already is, so declaring it costs nothing and lets a converted
        // field keep reaching a parameter typed as one.  The enumerators are
        // explicit so that `foreach` keeps binding to the allocation-free public
        // method by the pattern while a LINQ operator, a `ToList()` or a query
        // binds here.  A list that could be added to and enumerated but not
        // queried sent an integrator converting `List<T>` back to a copy on every
        // read; the synchronised list is a sequence and now says so.
        // Enumerating through the interface allocates one enumerator, as
        // List<T>'s own does.
        //
        // ⚠️ What the interface does NOT buy, measured rather than assumed:
        // LINQ's Count() still walks the list.  Enumerable.Count consults
        // ICollection<T> and the non-generic ICollection and never
        // IReadOnlyCollection<T> — on .NET 8 a probe whose Count property
        // recorded its reads answered Count() with zero reads — so the
        // constant-time read is the Count PROPERTY, and this comment's first
        // version, which said the opposite, was written from the type system
        // rather than from the runtime.
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => _items.GetEnumerator();

        // ── Op log helpers ─────────────────────────────────────────────────────

        private readonly struct PendingOp
        {
            public readonly ListOp Op;
            public readonly int    Index;
            public readonly T      Value;
            // The op's wire size — [op:1] and its fields — so Serialize can
            // fit a prefix of the log to a payload without writing it twice.
            // Zero for an element the writer could not measure: the op is
            // then sized as nothing here and reported by the flush that
            // writes it.
            public readonly int    Bytes;

            private PendingOp(ListOp op, int index, T value, int bytes)
            {
                Op    = op;
                Index = index;
                Value = value;
                Bytes = bytes;
            }

            public static PendingOp Add(int index, T value, int elementBytes)
                => new PendingOp(ListOp.Add, index, value, OpBytes(elementBytes));
            public static PendingOp Insert(int index, T value, int elementBytes)
                => new PendingOp(ListOp.Insert, index, value, IndexBytes + OpBytes(elementBytes));
            public static PendingOp Set(int index, T value, int elementBytes)
                => new PendingOp(ListOp.Set, index, value, IndexBytes + OpBytes(elementBytes));
            public static PendingOp RemoveAt(int index)
                => new PendingOp(ListOp.RemoveAt, index, default, IndexBytes + OpCodeBytes);
            public static PendingOp Clear()
                => new PendingOp(ListOp.Clear, 0, default, OpCodeBytes);
            public static PendingOp FullSync(int count)
                => new PendingOp(ListOp.FullSync, count, default, 0);

            private static int OpBytes(int elementBytes)
                => OpCodeBytes + (elementBytes < 0 ? 0 : elementBytes);
        }

        /// <summary>An op record's <c>[op:1]</c>, and the <c>[index:2]</c> the indexed ops carry.</summary>
        private const int OpCodeBytes = sizeof(byte);
        private const int IndexBytes  = sizeof(ushort);

        private void EnqueueOp(PendingOp op)
        {
            // Soft promotion to FullSync when the queue grows large enough.
            // Mass mutations (e.g. shuffling an inventory) collapse to a
            // single FullSync rather than 100+ tiny deltas — when that FullSync
            // can be sent.  A list past the datagram line (one this client
            // received whole and then came to own) keeps its deltas instead:
            // Serialize prefers a queued FullSync over every later delta and
            // only a successful flush retires it, so promoting one that cannot
            // be sent would end the list's replication at this write.  Its
            // deltas go out a payload at a time, as many flushes as they need.
            if (_pendingOps.Count >= FullSyncOpThreshold && SnapshotFitsAlone)
            {
                _pendingOps.Clear();
                _pendingOps.Add(PendingOp.FullSync(_items.Count));
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
        /// <remarks>
        /// Arms nothing for a snapshot the flush could never send.  Serialize
        /// prefers a queued FullSync over every later delta and only a
        /// successful flush retires it, so a FullSync past the datagram would
        /// stand in front of the delta log for the rest of the session — for
        /// every replica, not only the joiner.  The list is kept as it is: its
        /// deltas keep reaching the clients that hold it, the joiner does not
        /// receive it, and the console says so once a second.  A list built
        /// through this class never reaches that state — its growth is refused
        /// at the same line — so this is met only by a list received whole from
        /// an owner who was allowed past it and then handed over.
        /// </remarks>
        internal override void MarkDirtyForResync()
        {
            // Guarded on this repair's own framing — "the scalars enforce these
            // on every path and a list enforced none" — and this is a path: it
            // clears the op log, queues a FullSync and sets IsDirty, which is the
            // whole of the outbound state.  ⛔ Unreachable post-despawn today
            // (its caller walks the registry, which holds only spawned objects),
            // so this is defence in depth and is not the finding.
            if (OutboundWritesClosed) return;

            if (!SnapshotFitsAlone)
            {
                ReportUnsendableSnapshot();
                return;
            }

            // Replace any pending ops with a single FullSync — a late joiner
            // only needs the current state, not the historical mutations.
            _pendingOps.Clear();
            _pendingOps.Add(PendingOp.FullSync(_items.Count));
            base.MarkDirtyForResync();
        }

        // Per instance: a join re-flags every list at once, and each list past
        // the line is named — once a second per list, which a join does not
        // approach.
        private long _lastUnsendableSnapshotWarnTicks;

        /// <summary>
        /// The one line for a snapshot no flush could carry, from every path
        /// that would have armed one: a join, a fresh copy of the object, a
        /// flush fault re-flagging the component — and a queued snapshot the
        /// list has since outgrown.
        /// </summary>
        private void ReportUnsendableSnapshot()
        {
            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastUnsendableSnapshotWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}> (id {VariableId} on " +
                    $"{OwnerLabel}) cannot be re-sent whole: its snapshot is {DescribeSnapshot()}, " +
                    $"and one datagram carries {VariableFlushBudget.MaxEntryBytesAlone}. A client " +
                    "that does not hold this list — one joining now, or one holding a fresh copy of " +
                    "the object — does not receive it, its periodic refresh is skipped, and only the " +
                    "clients that already hold it follow its edits. Hold fewer elements, or split " +
                    "the list across several variables.");
        }

        private string DescribeSnapshot()
            => TryMeasureSnapshotBytesWithId(out int snapshot) ? $"{snapshot} bytes" : "not writable";

        /// <inheritdoc/>
        /// <remarks>
        /// The periodic full-sync this file's header has always described.  A
        /// list's steady-state payload is a delta against the contents the
        /// receiver is assumed to hold, and the delta log is not idempotent — an
        /// <c>Add</c> that arrives twice appends twice and one that never
        /// arrives is never asked for again.  Delivery to a replica is
        /// best-effort: the ARQ extension, where it is negotiated, covers this
        /// client's own link to the gateway, and the copy the gateway relays to
        /// the other players is unacknowledged whether or not it was.  So
        /// without this a single lost
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
            // succeeds at any size — so a list received whole from an owner
            // allowed past the datagram line, and then handed to this client,
            // can be clean, replicating perfectly by deltas, and have a
            // snapshot that does not fit. Arming one would queue a FullSync
            // that Serialize prefers over every later delta and that only a
            // successful flush retires: replication would end there, on a
            // timer, for exactly the lists that were working.  Asked against
            // the room LEFT in this tick's payload, which is narrower than the
            // datagram: a list that fits alone but not beside what was written
            // first is refused this tick and offered another.
            //
            // ⛔ Silent, deliberately — and the cost of that is stated rather
            // than assumed. A list this refuses is still replicating by deltas,
            // so a warning about it would be one about working code; but a lost
            // delta stays permanent for it, and nothing here says so.  A list
            // that can never be sent whole is named once a second by
            // MarkDirtyForResync when a player joins; a list that merely came
            // second this tick is named by nothing, because there is nothing to
            // fix.  Both are written into the API reference and the tuning
            // guide instead of left to be discovered.
            if (SnapshotBytesWithId() > roomBytes)
            {
                // ⛔ Stamped only when the whole datagram was free.  A refusal at
                // a payload that already carries something is about THIS TICK'S
                // ordering and not about the list — the rotation above the caller
                // gives it a turn at being first — and charging it a full
                // interval would defer a list that merely came second.  When the
                // payload was empty no later tick answers differently, and the
                // stamp is what keeps the question to once per interval rather
                // than once per tick for the rest of the session.
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

        // The scratch an element is measured in, and — for a list whose total
        // is unknown — a snapshot.  Reused across measurements; SetLength(0)
        // keeps the grown capacity, so the buffer is sized by the largest thing
        // ever measured in it: one element for every shipped list, and kept
        // because growing it again on every measurement would be the larger
        // cost.
        private MemoryStream _measureMs;
        private BinaryWriter _measureBw;

        // ── The elements' wire size, kept as they change ──────────────────────
        //
        // Every mutation of _items goes through the helpers below and nowhere
        // else, so the running total is exact by construction: the datagram
        // questions — may this list grow, may its snapshot be armed — are then
        // an addition, not a walk of the list on every write.  A subclass whose
        // writer cannot measure an element makes the total unknown until the
        // list is emptied; every datagram question is then answered by writing
        // the snapshot, and the writer's fault is the flush's to report.

        // Σ over _items of the bytes WriteElement writes for each element — the
        // FullSync's variable part — or ElementBytesUnknown.
        private int _elementBytes;
        private const int ElementBytesUnknown = -1;

        // Each takes the element's measured size — what ElementBytesOf answers,
        // -1 for an element the writer cannot write — so a caller that measured
        // it to decide the write does not measure it again to record it.
        private void ItemsAdd(T value, int bytes)
        {
            _items.Add(value);
            AccountFor(bytes, +1);
        }

        private void ItemsInsert(int index, T value, int bytes)
        {
            _items.Insert(index, value);
            AccountFor(bytes, +1);
        }

        private void ItemsRemoveAt(int index)
        {
            T removed = _items[index];
            _items.RemoveAt(index);
            AccountFor(ElementBytesOf(removed), -1);
            if (_items.Count == 0) _elementBytes = 0;
        }

        private void ItemsSet(int index, T value, int previousBytes, int bytes)
        {
            _items[index] = value;
            AccountFor(previousBytes, -1);
            AccountFor(bytes, +1);
        }

        private void ItemsClear()
        {
            _items.Clear();
            _elementBytes = 0;
        }

        /// <summary>Put the elements of <paramref name="from"/> back, with the total they were held at.</summary>
        private void ItemsRestore(List<T> from, int elementBytes)
        {
            _items.Clear();
            _items.AddRange(from);
            _elementBytes = elementBytes;
        }

        // sign is +1 for an element taken in and -1 for one let go; an element
        // the writer cannot measure poisons the total until the list empties.
        private void AccountFor(int bytes, int sign)
        {
            if (_elementBytes == ElementBytesUnknown) return;
            _elementBytes = bytes < 0 ? ElementBytesUnknown : _elementBytes + sign * bytes;
        }

        /// <summary>The bytes <paramref name="value"/> takes on the wire, or −1 when the writer cannot write it.</summary>
        private int ElementBytesOf(T value) => TryMeasureElement(value, out int bytes) ? bytes : -1;

        /// <summary>
        /// Measures how many bytes <see cref="WriteElement"/> writes for
        /// <paramref name="value"/>. The default writes the element to a scratch
        /// buffer and counts the bytes.
        /// </summary>
        /// <param name="value">The element to measure.</param>
        /// <param name="bytes">The element's size in bytes.</param>
        /// <returns>
        /// <see langword="false"/> when the element cannot be written.
        /// </returns>
        /// <remarks>
        /// Override it only when the size can be counted more cheaply than by
        /// writing the element; the answer must equal what
        /// <see cref="WriteElement"/> writes.
        /// </remarks>
        protected virtual bool TryMeasureElement(T value, out int bytes)
        {
            var writer = BeginMeasurement();
            try
            {
                WriteElement(writer, value);
                writer.Flush();
            }
            catch (Exception)
            {
                bytes = 0;
                return false;
            }
            bytes = (int)_measureMs.Length;
            return true;
        }

        // ── The snapshot's size ───────────────────────────────────────────────

        /// <summary>
        /// The framed wire length of a FullSync of the current contents —
        /// <c>[var_id:4][value_len:2]</c> plus what <see cref="WriteFullSync"/>
        /// writes — or <see cref="int.MaxValue"/> when it cannot be written.
        /// </summary>
        /// <remarks>
        /// An addition while the elements' total is known, which is whenever
        /// every element held could be measured; otherwise measured by writing
        /// the snapshot, which is what the flush would do.  ⛔ WriteElement is
        /// a public extension point and a subclass's may throw.  SerializeWithId
        /// contains that for the ordinary flush — it reports once a second,
        /// writes nothing, and answers false so the entry is retracted and the
        /// variable stays dirty — and a throw escaping a measurement would
        /// reach FlushAll, which re-dirties every variable on the component and
        /// undoes the whole tick's flush, once per refresh interval for ever.
        /// A snapshot that cannot be written is a snapshot that does not fit,
        /// and the element fault reports itself through the flush the first
        /// time the list is dirty.
        /// </remarks>
        private int SnapshotBytesWithId()
            => TryMeasureSnapshotBytesWithId(out int bytes) ? bytes : int.MaxValue;

        /// <summary>
        /// Whether a FullSync of the current contents fits a datagram holding
        /// it alone — the one condition under which arming it can end in a
        /// send.
        /// </summary>
        private bool SnapshotFitsAlone => SnapshotBytesWithId() <= VariableFlushBudget.MaxEntryBytesAlone;

        /// <summary>
        /// The framed FullSync of the current contents.  False when it cannot
        /// be written, in which case the length means nothing.
        /// </summary>
        private bool TryMeasureSnapshotBytesWithId(out int bytes)
        {
            if (_elementBytes != ElementBytesUnknown)
            {
                bytes = VariableIdAndLengthBytes + FullSyncFramingBytes + _elementBytes;
                return true;
            }

            var writer = BeginMeasurement();
            try
            {
                WriteFullSync(writer);
                writer.Flush();
            }
            catch (Exception)
            {
                bytes = 0;
                return false;
            }
            bytes = (int)_measureMs.Length + VariableIdAndLengthBytes;
            return true;
        }

        private BinaryWriter BeginMeasurement()
        {
            if (_measureMs == null)
            {
                _measureMs = new MemoryStream(256);
                _measureBw = new BinaryWriter(_measureMs, Encoding.UTF8, leaveOpen: true);
            }
            _measureMs.SetLength(0);
            return _measureBw;
        }

        // The bytes the smallest element the type writes takes — the default
        // value's, measured once.  A writer that refuses even that (a user
        // subclass over a class element, for which the default is null)
        // answers -1 here, and the smallest element HELD stands in: exact for
        // what the list holds, and nothing at all for an empty list, which is
        // never full.
        private int _smallestDefaultElementBytes = -2;

        private int SmallestElementBytes
        {
            get
            {
                if (_smallestDefaultElementBytes == -2)
                    _smallestDefaultElementBytes = ElementBytesOf(default);
                if (_smallestDefaultElementBytes >= 0) return _smallestDefaultElementBytes;

                int smallest = -1;
                for (int i = 0; i < _items.Count; i++)
                {
                    int bytes = ElementBytesOf(_items[i]);
                    if (bytes >= 0 && (smallest < 0 || bytes < smallest)) smallest = bytes;
                }
                return smallest < 0 ? 0 : smallest;
            }
        }

        /// <summary>
        /// How many elements of the smallest size the type writes fit one
        /// datagram beside the FullSync's own framing — the datagram's half of
        /// <see cref="MaxCount"/>.  Unbounded while no element has a
        /// measurable size.
        /// </summary>
        private int ElementsThatFitOneDatagram
        {
            get
            {
                int width = SmallestElementBytes;
                if (width <= 0) return int.MaxValue;
                return (VariableFlushBudget.MaxEntryBytesAlone - VariableIdAndLengthBytes - FullSyncFramingBytes) / width;
            }
        }

        /// <summary>
        /// What <see cref="WriteFullSync"/> writes ahead of the elements:
        /// <c>[op_count:1][op:1][count:2]</c>.
        /// </summary>
        private const int FullSyncFramingBytes = sizeof(byte) + sizeof(byte) + sizeof(ushort);

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

        /// <summary>Writes <paramref name="value"/> to <paramref name="writer"/>.</summary>
        /// <param name="writer">The writer to write the element to.</param>
        /// <param name="value">The element to write.</param>
        protected abstract void WriteElement(BinaryWriter writer, T value);

        /// <summary>
        /// Whether <paramref name="value"/> can be stored and sent as an element.
        /// The default accepts every element; override it to refuse elements
        /// <see cref="WriteElement"/> or <see cref="ReadElement"/> cannot handle.
        /// </summary>
        /// <param name="value">The candidate element.</param>
        /// <param name="reason">
        /// When the method returns <see langword="false"/>, why; the SDK includes
        /// it in the warning it logs.
        /// </param>
        /// <returns><see langword="true"/> to accept the element.</returns>
        protected virtual bool IsSendableElement(T value, out string reason)
        {
            reason = null;
            return true;
        }

        /// <summary>
        /// Returns the form in which the list stores <paramref name="value"/>.
        /// The default returns it unchanged; <see cref="NetworkVariableListString"/>
        /// stores <see langword="null"/> as an empty string.
        /// </summary>
        /// <param name="value">An element passed to one of the list's methods.</param>
        /// <returns>The element as the list stores it.</returns>
        /// <remarks>
        /// Applied to every element passed to the list's methods, but not to
        /// elements received from the owner. Override it when
        /// <see cref="WriteElement"/> writes a value in a different form, so that
        /// the owner and the other clients hold the same elements.
        /// </remarks>
        protected virtual T NormaliseElement(T value) => value;

        /// <summary>
        /// Whether <paramref name="value"/> can be sent as an element and this
        /// client can edit the list.
        /// </summary>
        /// <param name="value">The candidate element.</param>
        /// <returns>
        /// <see langword="false"/> when the element cannot be sent, another
        /// player owns the list, or the object has been despawned.
        /// </returns>
        /// <remarks>
        /// It does not check for room: a full list refuses an element this method
        /// accepts. Use <see cref="CanAdd"/> or <see cref="TryAdd"/> to include
        /// the capacity check.
        /// </remarks>
        public bool CanSend(T value)
            => IsSendableElement(NormaliseElement(value), out _) && !WriteWouldNotLand;

        /// <summary>
        /// Whether <see cref="Add"/> would add <paramref name="item"/> now: the
        /// element can be sent, this client can edit the list, and there is room
        /// for it. Changes nothing and logs nothing.
        /// </summary>
        /// <param name="item">The candidate element.</param>
        /// <returns><see langword="true"/> when the element would be added.</returns>
        /// <remarks>
        /// Use it, or <see cref="TryAdd"/>, to fill a list whose elements vary in
        /// size: <c>while (list.TryAdd(next))</c>.
        /// </remarks>
        public bool CanAdd(T item)
            => CanAddNormalised(NormaliseElement(item));

        private bool CanAddNormalised(T item)
            => IsSendableElement(item, out _)
            && !WriteWouldNotLand
            && !GrowthRefused(null, item, report: false, out _);

        /// <summary>
        /// Adds <paramref name="item"/> to the end of the list and reports
        /// whether it was added.
        /// </summary>
        /// <param name="item">The element to add.</param>
        /// <returns>
        /// <see langword="false"/> when the element cannot be sent, there is no
        /// room for it (see <see cref="MaxCount"/>), another player owns the
        /// list, or the object has been despawned.
        /// </returns>
        public bool TryAdd(T item)
        {
            item = NormaliseElement(item);
            if (!IsSendableElement(item, out _)) { RefuseUnsendableElement(item, "TryAdd"); return false; }

            // Asked silently: the caller is being handed the answer, and a
            // console line beside a returned false is noise it did not ask for.
            if (WriteWouldNotLand) return false;

            // Reported rather than asked silently, and in this order for the
            // reason Add states: a full list is a property of the list and not
            // of this call, so the next caller meets it too and the one line
            // that names the ceiling is worth more than silence.
            if (RefuseAtCapacity("TryAdd", item, out _)) return false;

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

        // The datagram bound's own gates, on the same terms: static per closed
        // generic, once a second, and one per refusal — a growth refused and a
        // replacement refused are two diagnostics, and a flood of one must not
        // decide whether the other is reported.
        private static long _lastDatagramRefusalWarnTicks;
        private static long _lastDatagramReplacementRefusalWarnTicks;

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

        /// <summary>Test seam: reopen the shared datagram-refusal gates, on the same terms.</summary>
        internal static void ResetDatagramWarnGateForTest()
        {
            _lastDatagramRefusalWarnTicks            = 0;
            _lastDatagramReplacementRefusalWarnTicks = 0;
        }

        /// <summary>Test seam: the snapshot's framed length as the running total answers it.</summary>
        internal int SnapshotBytesWithIdForTest() => SnapshotBytesWithId();

        /// <summary>
        /// Test seam: the snapshot's framed length measured by writing it — what
        /// the running total is held to.
        /// </summary>
        internal int MeasuredSnapshotBytesWithIdForTest()
        {
            var writer = BeginMeasurement();
            WriteFullSync(writer);
            writer.Flush();
            return (int)_measureMs.Length + VariableIdAndLengthBytes;
        }
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Whether a growth by <paramref name="item"/> must be refused: the list
        /// is at the configured ceiling, or the snapshot with that element in
        /// it would not fit one datagram.  Consulted by every mutator that can
        /// grow the list, never by a list of their names; reported here so the
        /// element's size, measured to decide, is handed on to record.
        /// </summary>
        private bool RefuseAtCapacity(string site, T item, out int elementBytes)
            => GrowthRefused(site, item, report: true, out elementBytes);

        /// <summary>
        /// The one predicate every growth asks, and <see cref="CanAdd"/> asks
        /// silently — one body, so the answer a fill loop reads and the answer
        /// <see cref="Add"/> acts on cannot drift apart.
        /// </summary>
        private bool GrowthRefused(string site, T item, bool report, out int elementBytes)
        {
            elementBytes = ElementBytesOf(item);

            // Unknown is not a licence and not a ceiling: with no settings asset
            // reachable the configured arm refuses nothing, and the receiver's
            // own bound is what still holds.  See TryResolveMaxListSize.  The
            // datagram is the wire's bound, the same on every client, and is
            // asked whether or not a setting is reachable.
            if (TryResolveMaxListSize(out int max) && _items.Count >= max)
            {
                if (report && RTMPE.Core.WarnGate.ShouldEmit(ref _lastCapacityRefusalWarnTicks))
                    UnityEngine.Debug.LogWarning(
                        $"[RTMPE] NetworkVariableList<{typeof(T).Name}> (id {VariableId} on " +
                        $"{OwnerLabel}) refused a growth at {site}: the list holds {_items.Count} " +
                        $"elements and NetworkSettings.maxNetworkVariableListSize is {max}. The " +
                        "list is unchanged — Count did not move. Every receiver drops an inbound " +
                        "element past this ceiling, so growing past it here would leave this " +
                        "client holding elements no other client is allowed to store.");
                return true;
            }

            // An element the writer cannot measure is not refused here: the
            // flush contains and reports that fault, and refusing it under the
            // datagram's name would send the developer looking at the wrong
            // bound.
            if (elementBytes < 0 || FitsAfterGrowthOf(elementBytes)) return false;

            if (report && RTMPE.Core.WarnGate.ShouldEmit(ref _lastDatagramRefusalWarnTicks))
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}> (id {VariableId} on " +
                    $"{OwnerLabel}) refused a growth at {site}: with this element the list's " +
                    $"snapshot would be {SnapshotBytesWithId() + elementBytes} bytes, and one " +
                    $"datagram carries {VariableFlushBudget.MaxEntryBytesAlone}. The list is " +
                    "unchanged — Count did not move. A joining player, the periodic refresh and a " +
                    "burst of edits each receive the whole list as one snapshot in one datagram, " +
                    "so a list past this line is one no joining player could ever receive. Hold " +
                    "fewer elements, or split the list across several variables.");
            return true;
        }

        /// <summary>The configured ceiling's arm: at it, where one is reachable.</summary>
        private bool AtConfiguredCeiling
            => TryResolveMaxListSize(out int max) && _items.Count >= max;

        /// <summary>
        /// The datagram's arm: whether the snapshot with one more element of
        /// <paramref name="elementBytes"/> still fits one datagram.  True — no
        /// refusal — when the snapshot cannot be measured, for the reason the
        /// growth predicate gives.
        /// </summary>
        private bool FitsAfterGrowthOf(int elementBytes)
            => !TryMeasureSnapshotBytesWithId(out int snapshot)
            || (long)snapshot + elementBytes <= VariableFlushBudget.MaxEntryBytesAlone;

        /// <summary>
        /// Whether replacing the element at <paramref name="index"/> with
        /// <paramref name="value"/> must be refused because the snapshot would
        /// then exceed one datagram, or because the edit itself could not be
        /// sent.  Only a replacement that grows the snapshot can be refused on
        /// the first ground, so a fixed-width element never is, and a list
        /// already past the line still takes a replacement of equal or smaller
        /// size.  The two sizes measured to decide are handed on to record.
        /// </summary>
        private bool RefuseReplacementPastTheDatagram(int index, T previous, T value, out int before, out int after)
        {
            before = ElementBytesOf(previous);
            after  = ElementBytesOf(value);
            if (before < 0 || after < 0) return false;

            // Two ways a replacement is one no flush could carry, one report.
            // The op itself must fit one payload: a replacement is the one
            // write that can shrink an element and still be unsendable — an
            // element a previous owner was allowed past the line, replaced by
            // one nearly as large.  A growth cannot reach that, because an
            // element that fits the snapshot fits its own op; a growth is
            // refused on the snapshot instead.
            string refused;
            if (OpCountBytes + OpCodeBytes + IndexBytes + after > MaxValueBytesPerPayload)
            {
                refused = $"a {after}-byte element does not fit one datagram as a single edit, so " +
                          "no flush could carry it";
            }
            else
            {
                if (after <= before || !TryMeasureSnapshotBytesWithId(out int snapshot)) return false;
                int snapshotAfter = snapshot - before + after;
                if (snapshotAfter <= VariableFlushBudget.MaxEntryBytesAlone) return false;
                refused = $"with a {after}-byte element in place of the {before}-byte one the list's " +
                          $"snapshot would be {snapshotAfter} bytes, and one datagram carries " +
                          $"{VariableFlushBudget.MaxEntryBytesAlone} — a joining player, the periodic " +
                          "refresh and a burst of edits each receive the whole list as one snapshot " +
                          "in one datagram, so a list past this line is one no joining player could " +
                          "ever receive";
            }

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastDatagramReplacementRefusalWarnTicks))
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}> (id {VariableId} on " +
                    $"{OwnerLabel}) refused a replacement at index {index}: {refused}. The list is " +
                    "unchanged. Hold fewer or smaller elements, or split the list across several " +
                    "variables.");
            return true;
        }

        /// <summary>Reads one element from <paramref name="reader"/>.</summary>
        /// <param name="reader">The reader positioned at the element.</param>
        /// <returns>The element read.</returns>
        protected abstract T ReadElement(BinaryReader reader);

        // ── Wire serialisation ─────────────────────────────────────────────────

        /// <summary>
        /// Writes the changes waiting to be sent, or the whole list. Used by the
        /// SDK when it sends the list; not intended to be called from game code.
        /// </summary>
        /// <param name="writer">The writer to write to.</param>
        public override void Serialize(BinaryWriter writer)
        {
            // Re-validate the ops queue — Clear() collapses earlier ops, but a
            // hostile or buggy subclass override could still leave it empty.
            if (_pendingOps.Count == 0)
            {
                _opsCovered = 0;
                writer.Write((byte)0);
                return;
            }

            // If any FullSync was queued, emit ONLY that op.  Receivers see the
            // current state immediately and earlier deltas would be redundant.
            // A queued snapshot the list has since outgrown — inbound state from
            // a peer this class's bound did not reach, applied under a handover
            // — is dropped rather than offered to a flush that retracts it for
            // ever; what follows it goes out as deltas.
            for (int i = 0; i < _pendingOps.Count; i++)
            {
                if (_pendingOps[i].Op != ListOp.FullSync) continue;
                if (SnapshotFitsAlone)
                {
                    _opsCovered = _pendingOps.Count;
                    WriteFullSync(writer);
                    return;
                }
                ReportUnsendableSnapshot();
                _pendingOps.RemoveAt(i);
                if (_pendingOps.Count == 0)
                {
                    _opsCovered = 0;
                    writer.Write((byte)0);
                    return;
                }
                break;
            }

            // The prefix one payload carries: the wire's op count, and the
            // bytes a datagram entry has for the value.  Every op this class
            // admits fits a payload of its own — a growth fits because its
            // element fits the snapshot, and a replacement is refused when its
            // op would not — so the prefix is never empty; were it, the first
            // op is written and the flush reports what it cannot send.
            int n = 0;
            int bytes = OpCountBytes;
            while (n < _pendingOps.Count && n < MaxOpsPerPayload
                   && bytes + _pendingOps[n].Bytes <= MaxValueBytesPerPayload)
            {
                bytes += _pendingOps[n].Bytes;
                n++;
            }
            if (n == 0) n = 1;

            // A log that needs more than one flush is sent as one FullSync
            // instead when the whole list fits a datagram: one payload, and the
            // receiver converges without a partial-delta ordering ambiguity.
            // A list past the line has no such payload, and goes out a prefix
            // at a time.
            if (n < _pendingOps.Count && SnapshotFitsAlone)
            {
                _opsCovered = _pendingOps.Count;
                WriteFullSync(writer);
                return;
            }

            _opsCovered = n;
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

        // What the last Serialize covered, for MarkClean to retire: the whole
        // log after a FullSync, a prefix of it after a delta payload, and
        // NoFlushSinceRetire when nothing has been serialised since the last
        // retirement — then MarkClean, called by a caller that has decided the
        // list's state is spent, retires everything, as it always did.
        private int _opsCovered = NoFlushSinceRetire;
        private const int NoFlushSinceRetire = -1;

        /// <summary>The <c>[op_count:1]</c> a delta payload opens with.</summary>
        private const int OpCountBytes = sizeof(byte);

        /// <summary>
        /// The value bytes one payload of this variable may carry: a datagram
        /// entry holding it alone, less the <c>[var_id:4][value_len:2]</c>
        /// prefix.
        /// </summary>
        private const int MaxValueBytesPerPayload = VariableFlushBudget.MaxEntryBytesAlone - VariableIdAndLengthBytes;

        /// <summary>
        /// Discards the changes the last send carried. Changes that did not fit
        /// in it stay waiting, and the list stays marked for sending. The SDK
        /// calls it after sending.
        /// </summary>
        public override void MarkClean()
        {
            int retire = _opsCovered == NoFlushSinceRetire
                ? _pendingOps.Count
                : Math.Min(_opsCovered, _pendingOps.Count);
            _pendingOps.RemoveRange(0, retire);
            _opsCovered = NoFlushSinceRetire;
            base.MarkClean();
            if (_pendingOps.Count > 0) IsDirty = true;
        }

        /// <summary>
        /// Reads changes sent by the owner and applies them. Used by the SDK
        /// when it receives the list; not intended to be called from game code.
        /// </summary>
        /// <param name="reader">The reader positioned at the changes.</param>
        /// <remarks>
        /// The changes are applied together: if any of them cannot be read, the
        /// list is left as it was and a warning is logged.
        /// <see cref="OnListChanged"/> is raised for each change once all of
        /// them are applied.
        /// </remarks>
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
            _payloadStatesWholeList = false;
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
            int rollbackElementBytes = _elementBytes;
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
                ItemsClear();
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
                ItemsRestore(_rollbackBuffer, rollbackElementBytes);
                _rollbackBuffer.Clear();
                _deferredChanges.Clear();
                _payloadStatesWholeList = false;
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
            if (discardedPreSpawnContents && WriteWouldNotLand)
            {
                _opsCovered = NoFlushSinceRetire;
                MarkClean();
            }

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
                        ItemsAdd(val, ElementBytesOf(val));
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
                        ItemsInsert(idx, val, ElementBytesOf(val));
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
                        ItemsRemoveAt(idx);
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
                        ItemsSet(idx, val, ElementBytesOf(previous), ElementBytesOf(val));
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Set, idx, val, previous));
                        break;
                    }
                    case ListOp.Clear:
                    {
                        ItemsClear();
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Clear, -1, default, default));
                        _payloadStatesWholeList = true;
                        break;
                    }
                    case ListOp.FullSync:
                    {
                        _payloadStatesWholeList = true;
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

                        ItemsClear();

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
                            T element = ReadElement(reader);
                            ItemsAdd(element, ElementBytesOf(element));
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

        // One-line-per-second gate for the list-size error in WriteFullSync
        // below.  The condition persists across ticks — the op log survives
        // serialisation — so an ungated emission would be one line per tick
        // per list for as long as the list stays too large.
        private long _lastFullSyncCapErrorTicks;

        // Its own gate: a list whose payloads are being rejected every tick must
        // not silence the op-log refusals, and the rate this fires at is the
        // diagnosis.
        private long _lastDeserialiseRejectionWarnTicks;

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

    /// <summary>The kind of change reported by <see cref="NetworkVariableList{T}.OnListChanged"/>.</summary>
    public enum NetworkListChangeKind : byte
    {
        /// <summary>An element was added at the end of the list, at <c>Index</c>.</summary>
        Add      = 1,

        /// <summary>An element was inserted at <c>Index</c>.</summary>
        Insert   = 2,

        /// <summary>The element at <c>Index</c> was removed; it is the event's previous value.</summary>
        RemoveAt = 3,

        /// <summary>The element at <c>Index</c> was replaced.</summary>
        Set      = 4,

        /// <summary>The list was emptied.</summary>
        Clear    = 5,

        /// <summary>
        /// The whole list was replaced by the owner's copy.
        /// </summary>
        /// <remarks>
        /// The whole list arrives when a player joins and when the list has gone
        /// the full-sync interval without being sent, usually with the contents
        /// this client already holds. Read the list again; do not treat the
        /// event as a change.
        /// </remarks>
        FullSync = 6,
    }

    /// <summary>
    /// Describes one change to a <see cref="NetworkVariableList{T}"/>, as passed
    /// to <see cref="NetworkVariableList{T}.OnListChanged"/>.
    /// </summary>
    /// <typeparam name="T">The list's element type.</typeparam>
    public readonly struct NetworkVariableListChangeEvent<T>
    {
        /// <summary>The kind of change.</summary>
        public readonly NetworkListChangeKind Kind;

        /// <summary>
        /// The position of the changed element; <c>-1</c> for
        /// <see cref="NetworkListChangeKind.Clear"/> and
        /// <see cref="NetworkListChangeKind.FullSync"/>.
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// The element added, inserted or set; <c>default</c> for the other
        /// kinds.
        /// </summary>
        public readonly T   NewValue;

        /// <summary>
        /// The element removed or replaced; <c>default</c> for the other kinds.
        /// </summary>
        public readonly T   PreviousValue;

        /// <summary>Creates a change description.</summary>
        /// <param name="kind">The kind of change.</param>
        /// <param name="index">The position of the changed element, or <c>-1</c>.</param>
        /// <param name="newValue">The element added, inserted or set.</param>
        /// <param name="previousValue">The element removed or replaced.</param>
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

    /// <summary>A replicated list of <see cref="int"/> values.</summary>
    public sealed class NetworkVariableListInt : NetworkVariableList<int>
    {
        /// <summary>Creates an empty list registered with <paramref name="owner"/>.</summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        public NetworkVariableListInt(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, int value)
            => writer.Write(value);
        protected override int ReadElement(BinaryReader reader)
            => reader.ReadInt32();
    }

    /// <summary>
    /// A replicated list of <see cref="float"/> values. NaN and infinite
    /// elements are accepted and sent.
    /// </summary>
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
        /// <summary>Creates an empty list registered with <paramref name="owner"/>.</summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        public NetworkVariableListFloat(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, float value)
            => writer.Write(value);
        protected override float ReadElement(BinaryReader reader)
            => reader.ReadSingle();
    }

    /// <summary>A replicated list of <see langword="bool"/> values.</summary>
    public sealed class NetworkVariableListBool : NetworkVariableList<bool>
    {
        /// <summary>Creates an empty list registered with <paramref name="owner"/>.</summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        public NetworkVariableListBool(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, bool value)
            => writer.Write(value);
        protected override bool ReadElement(BinaryReader reader)
            => reader.ReadBoolean();
    }

    /// <summary>
    /// A replicated list of <see cref="Vector2"/> values. Elements with NaN or
    /// infinite components are accepted and sent.
    /// </summary>
    public sealed class NetworkVariableListVector2 : NetworkVariableList<Vector2>
    {
        /// <summary>Creates an empty list registered with <paramref name="owner"/>.</summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        public NetworkVariableListVector2(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, Vector2 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
        }

        protected override Vector2 ReadElement(BinaryReader reader)
            => new Vector2(reader.ReadSingle(), reader.ReadSingle());
    }

    /// <summary>
    /// A replicated list of <see cref="Vector2Int"/> values, such as grid
    /// cells. Components are sent as 32-bit integers, so large values stay
    /// exact.
    /// </summary>
    public sealed class NetworkVariableListVector2Int : NetworkVariableList<Vector2Int>
    {
        /// <summary>Creates an empty list registered with <paramref name="owner"/>.</summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        public NetworkVariableListVector2Int(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        protected override void WriteElement(BinaryWriter writer, Vector2Int value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
        }

        protected override Vector2Int ReadElement(BinaryReader reader)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            return new Vector2Int(x, y);
        }
    }

    /// <summary>
    /// A replicated list of <see cref="Vector3"/> values. Elements with NaN or
    /// infinite components are accepted and sent.
    /// </summary>
    // The concrete lists carry the element types of the closed conversion
    // map, and the map's `List<T>` rows and the sealed classes here are held to
    // each other in both directions by the tooling's parity tests — a list
    // added here without its row is a type the conversion cannot name, and a
    // row without its class is a conversion that emits a type nobody ships.
    // The set used to stop at four (int, float, Vector3, string) on the ground
    // that a game had been measured to need no more; a game then needed a
    // List<Vector2Int> and was told to write the ten lines itself, which is a
    // refusal wearing a remedy.  Quaternion is the one map type with no list,
    // deliberately: no game has asked for a list of rotations, and every list
    // costs a wire form that has to be kept.  A game that wants one declares it
    // — NetworkVariableList<T>'s constructor is protected and
    // WriteElement/ReadElement are the only two members a subclass must supply.
    public sealed class NetworkVariableListVector3 : NetworkVariableList<Vector3>
    {
        /// <summary>Creates an empty list registered with <paramref name="owner"/>.</summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
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
    /// A replicated list of <see cref="string"/> values. <see langword="null"/>
    /// is stored as <see cref="string.Empty"/>.
    /// </summary>
    /// <remarks>
    /// An element that is not valid UTF-8, or that is longer than 65535 bytes of
    /// UTF-8, is refused. The whole list must also fit in one datagram, so use
    /// <see cref="NetworkVariableList{T}.TryAdd"/> to fill it.
    /// </remarks>
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

        /// <summary>Creates an empty list registered with <paramref name="owner"/>.</summary>
        /// <param name="owner">The component the list belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the list is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        public NetworkVariableListString(NetworkBehaviour owner, string memberName)
            : base(owner, memberName) { }

        /// <summary>
        /// Stores <see langword="null"/> as an empty string, the form in which it
        /// is sent.
        /// </summary>
        /// <param name="value">An element passed to one of the list's methods.</param>
        /// <returns>The element as the list stores it.</returns>
        protected override string NormaliseElement(string value) => value ?? string.Empty;

        /// <summary>
        /// Refuses a string that is not valid UTF-8 (for example one a
        /// <c>Substring</c> cut in the middle of a surrogate pair) or that is
        /// longer than 65535 bytes of UTF-8.
        /// </summary>
        /// <param name="value">The candidate element.</param>
        /// <param name="reason">When the method returns <see langword="false"/>, why.</param>
        /// <returns><see langword="true"/> to accept the element.</returns>
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

        /// <summary>
        /// Counts the element's size without encoding it.
        /// </summary>
        /// <param name="value">The element to measure.</param>
        /// <param name="bytes">The element's size in bytes.</param>
        /// <returns>
        /// <see langword="false"/> for a string that is not valid UTF-8 or is
        /// longer than 65535 bytes of UTF-8.
        /// </returns>
        protected override bool TryMeasureElement(string value, out int bytes)
        {
            bytes = 0;
            try
            {
                int count = StrictUtf8.GetByteCount(value ?? string.Empty);
                if (count > ushort.MaxValue) return false;
                bytes = sizeof(ushort) + count;
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
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
