// RTMPE SDK — Runtime/Core/EarlyObjectPacketBuffer.cs
//
// Bounded, order-preserving staging buffer for catch-up packets — networked-
// object lifecycle (Spawn 0x30 / Despawn 0x31) and the Enhanced-RPC buffer
// replay (RpcBufferReplay 0x52) — that reach the client before it has entered a
// room.
//
// The server replays a late-joiner's catch-up object set and buffered RPCs as
// the session binds to its room; that stream can arrive a frame or two ahead of
// the join reply that admits the client to InRoom. Holding those packets behind
// the room gate — rather than dropping them — lets the receive path release
// them in their original arrival order once the room context exists, so an
// object that spawned before the local player joined still renders and the RPCs
// that target it still fire. Nothing is applied while a packet is staged: the
// buffer drains only after the gate opens, so the gate's "no pre-room state"
// guarantee is preserved.
//
// A staged frame belongs to a room entry.  A catch-up frame carries an object
// id and no room, so the only thing that says which room it describes is when
// it arrived: which entries this client had begun by then — and an entry can
// end with no transition at all: a join ladder exhausted, a reply refused, a
// matchmaking deadline passed, a create swept.  Frames staged before the
// completing entry was begun are an ended entry's, the previous room's
// lifecycle, and releasing them into the room this client enters spawns
// objects that exist for nobody else there, despawns ones that do, and replays
// another room's RPCs.  Frames staged from that entry's beginning on arrived
// while it was an entry the server could be answering — under it or under a
// later one begun while it was still outstanding — and are its catch-up.  So
// every frame is staged under the entry begun most recently, the values are
// ordered (RoomEntrySequence), and the transition that opens the gate says
// which entry it completes: what was staged under an earlier one is discarded
// unreleased, and the rest is released.

using System;
using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// Which inbound handler replays a staged catch-up packet.  Kept as an
    /// explicit discriminator (rather than a raw packet-type byte) so the flush
    /// switch dispatches by intent; a kind added here without a matching flush
    /// case falls to the switch's <c>default</c> arm, which drops it with a
    /// diagnostic rather than silently mis-routing it.
    /// </summary>
    internal enum EarlyPacketKind
    {
        /// <summary>
        /// Spawn (0x30) — released via <c>ReleaseStagedSpawn</c>, not via
        /// <c>OnSpawnPacket</c>: the staged release is exempt from the
        /// per-second spawn rate cap, and re-entering the inbound handler would
        /// also re-run the pre-room gate this packet is being released past.
        /// </summary>
        Spawn,

        /// <summary>Despawn (0x31) — released via <c>ApplyDespawnPacket</c>.</summary>
        Despawn,

        /// <summary>RpcBufferReplay (0x52) — replayed via <c>HandleRpcBufferReplay</c>.</summary>
        RpcReplay,

        /// <summary>
        /// A legacy ownership-transfer RPC (0x50, method 200) for an object that
        /// is staged rather than built: applied after the spawn it follows, or
        /// it would be refused as naming nothing.
        /// </summary>
        LegacyRpc,

        /// <summary>
        /// A player left while the room's objects were staged: what that does to
        /// its objects — the ones it owned destroyed or handed on — is applied
        /// in its place among them, after the spawns that preceded it.
        /// </summary>
        PlayerLeft,

        /// <summary>A player arrived while the room's objects were staged; see <see cref="PlayerLeft"/>.</summary>
        PlayerJoined,

        /// <summary>
        /// The room's host changed while its objects were staged, and the
        /// previous host's objects pass to the new one; see <see cref="PlayerLeft"/>.
        /// </summary>
        MasterChanged,
    }

    /// <summary>
    /// What staging a Spawn or Despawn with its object id did (see
    /// <see cref="EarlyObjectPacketBuffer.StageObject"/>).
    /// </summary>
    internal enum ObjectStageOutcome
    {
        /// <summary>Staged at the tail.</summary>
        Staged,

        /// <summary>Staged at the tail, and something older was evicted to make room.</summary>
        StagedWithEviction,

        /// <summary>
        /// A Spawn of an object already staged for the same entry: the staged one
        /// keeps its place and takes the newer bytes — a re-send of the room's
        /// replay ladder, which carries the room's current view of the object.
        /// </summary>
        ReplacedTheStagedSpawn,

        /// <summary>
        /// A Despawn of an object staged for the same entry and never built: the
        /// staged spawn is removed and nothing is staged — the caller does the
        /// despawn's bookkeeping, since there is no object to destroy.
        /// </summary>
        CollapsedTheStagedSpawn,

        /// <summary>
        /// Not staged: the buffer was full under the in-room policy, which keeps
        /// what it holds — the room's catch-up — rather than evicting it.
        /// </summary>
        Refused,
    }

    /// <summary>
    /// Order-preserving, capacity-bounded hold for catch-up packets received
    /// while the client is not yet <c>InRoom</c>.  Confined to the receive
    /// (main) thread like the rest of the inbound path, so it carries no
    /// synchronisation of its own.
    /// </summary>
    internal sealed class EarlyObjectPacketBuffer
    {
        /// <summary>A staged catch-up packet and which inbound handler replays it.</summary>
        internal readonly struct Staged
        {
            /// <summary>Which handler replays this packet on flush.</summary>
            public EarlyPacketKind Kind { get; }

            /// <summary>The packet bytes owned by the buffer (raw frame for the
            /// lifecycle kinds, extracted payload for <see cref="EarlyPacketKind.RpcReplay"/>).</summary>
            public byte[] Data { get; }

            /// <summary>The room entry begun most recently when this packet arrived.</summary>
            public int Entry { get; }

            /// <summary>The object a Spawn or Despawn names, or 0 when it was staged without one.</summary>
            public ulong ObjectId { get; }

            /// <summary>The player a roster marker names (the previous host, for <see cref="EarlyPacketKind.MasterChanged"/>).</summary>
            public string Player { get; }

            /// <summary>The new host, for <see cref="EarlyPacketKind.MasterChanged"/>.</summary>
            public string Other { get; }

            /// <summary>Where a departure fell in the gateway's send order.</summary>
            public long Counter { get; }

            public Staged(EarlyPacketKind kind, byte[] data, int entry,
                          ulong objectId = 0UL, string player = null, string other = null, long counter = -1L)
            {
                Kind     = kind;
                Data     = data;
                Entry    = entry;
                ObjectId = objectId;
                Player   = player;
                Other    = other;
                Counter  = counter;
            }
        }

        /// <summary>
        /// Whether eviction passes <paramref name="kind"/> over while anything
        /// else is staged.
        /// </summary>
        /// <remarks>
        /// 🔑 A Spawn the ladder re-sends and a Despawn the room re-applies have
        /// copies; the room's buffered-RPC catch-up is sent ONCE per entry and a
        /// roster marker is the only record of what a departure or an arrival
        /// did to the objects around it — evicting either loses it for good.
        /// ⛔ A legacy RPC is not protected: a peer chooses how many it sends.
        /// What keeps a peer's traffic from evicting the room's objects is not
        /// this list but the in-room policy (<c>keepOldest</c>): there the oldest
        /// entries ARE the room's catch-up, so a full buffer refuses what arrives
        /// rather than evicting what it holds.
        /// </remarks>
        internal static bool IsProtected(EarlyPacketKind kind)
            => kind == EarlyPacketKind.RpcReplay
               || kind == EarlyPacketKind.PlayerLeft
               || kind == EarlyPacketKind.PlayerJoined
               || kind == EarlyPacketKind.MasterChanged;

        /// <summary>
        /// The most bytes staged at once, whatever the packet count: one
        /// mebibyte, on the terms <c>HeldVariableUpdates.MaxBytes</c> states.
        /// </summary>
        /// <remarks>
        /// A staged packet is a whole datagram, so a ceiling counted in packets
        /// alone prices every one of them at the largest the receive path
        /// admits.  The catch-up set this exists for is a Spawn per live object,
        /// far below that, so the byte line is reached by a sender that stages
        /// large packets into a join that never completes and not by a join.
        /// </remarks>
        internal const long MaxBytes = 1L << 20;

        private readonly int _capacity;
        private readonly LinkedList<Staged> _queue;

        // The staged Spawn of each object, while it is staged: a re-send of it
        // replaces the bytes in place, and a Despawn of it collapses the pair.
        private readonly Dictionary<ulong, LinkedListNode<Staged>> _spawnByObject =
            new Dictionary<ulong, LinkedListNode<Staged>>();

        // The staged ownership transfer of each object, while it is staged: a
        // later one replaces it in place, so a sender repeating transfers of one
        // object holds one entry however many it sends.
        private readonly Dictionary<ulong, LinkedListNode<Staged>> _transferByObject =
            new Dictionary<ulong, LinkedListNode<Staged>>();

        /// <summary>What appending did.</summary>
        private enum Appended { Staged, StagedWithEviction, Refused }

        // Maintained with the queue rather than walked; see the byte ceiling.
        private long _bytes;

        /// <param name="capacity">
        /// Maximum packets held at once.  Sized above a room's live object count
        /// so a full catch-up set is never partially shed under normal play; the
        /// cap exists only to bound the pathological case of a session that
        /// receives lifecycle packets yet never completes a join.
        ///
        /// ⚠️ Overflow evicts the OLDEST unprotected packet, which retains the
        /// most recent activity — right for a set that may never be replayed at
        /// all, and the reason the release must not be stretched: the moment
        /// this holds an ordered stream long enough to overflow, dropping the
        /// head drops a Spawn while keeping the Despawn that removes it.  The
        /// release is bounded to a few frames so that state is not reachable,
        /// and a scene load — which does hold a stream — stages it folded
        /// (<see cref="StageObject"/>) with the packets that have no copy
        /// protected (<see cref="IsProtected"/>).
        /// </param>
        public EarlyObjectPacketBuffer(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _queue = new LinkedList<Staged>();
        }

        /// <summary>Packets currently staged.</summary>
        public int Count => _queue.Count;

        /// <summary>The bytes those packets occupy — what the byte ceiling bounds.</summary>
        public long Bytes => _bytes;

        /// <summary>
        /// Stage one catch-up packet under <paramref name="entry"/>, the room
        /// entry begun most recently — a value from the sequence the managers
        /// share, so it is ordered against every other entry's.  At capacity
        /// the oldest staged packet that is not protected (<see cref="IsProtected"/>)
        /// is evicted first, so under overflow the buffer retains the most
        /// recent activity.  Returns <c>true</c> when an eviction occurred.
        /// </summary>
        public bool Stage(EarlyPacketKind kind, byte[] data, int entry)
            => Stage(kind, data, entry, keepOldest: false);

        /// <summary>
        /// <see cref="Stage(EarlyPacketKind, byte[], int)"/>, under the eviction
        /// policy <paramref name="keepOldest"/> names: <see langword="true"/> in
        /// the room, where the oldest entries are the room's catch-up and a full
        /// buffer refuses an unprotected arrival rather than evicting what it
        /// holds (a protected arrival displaces the NEWEST unprotected entry).
        /// Returns <c>true</c> when something was evicted or refused.
        /// </summary>
        public bool Stage(EarlyPacketKind kind, byte[] data, int entry, bool keepOldest)
            => Append(new Staged(kind, data, entry), keepOldest) != Appended.Staged;

        /// <summary>
        /// Stage an ownership transfer of <paramref name="objectId"/> as a
        /// <see cref="EarlyPacketKind.LegacyRpc"/>, replacing in place one already
        /// staged for that object under the same entry: the later transfer is the
        /// one that stands, and a sender repeating transfers of one object holds
        /// one entry.  Returns <c>true</c> when something was evicted or refused.
        /// </summary>
        public bool StageTransfer(byte[] data, int entry, ulong objectId, bool keepOldest)
        {
            if (objectId != 0UL
                && _transferByObject.TryGetValue(objectId, out var staged)
                && staged.Value.Entry == entry)
            {
                _bytes += (data?.Length ?? 0) - (staged.Value.Data?.Length ?? 0);
                staged.Value = new Staged(EarlyPacketKind.LegacyRpc, data, entry, objectId);
                return false;
            }
            var appended = Append(new Staged(EarlyPacketKind.LegacyRpc, data, entry, objectId), keepOldest);
            if (appended != Appended.Refused && objectId != 0UL) _transferByObject[objectId] = _queue.Last;
            return appended != Appended.Staged;
        }

        /// <summary>
        /// Stage a Spawn or Despawn of <paramref name="objectId"/>, folding it
        /// into what is already staged for that object under the same entry
        /// (<see cref="ObjectStageOutcome"/>).  An id of 0 stages without folding.
        /// </summary>
        /// <remarks>
        /// 🔑 While the room's scene loads, the buffer holds a whole load: the
        /// replay ladder's three sends of every object, and every object a peer
        /// spawns and removes meanwhile.  Folded, the catch-up costs one entry
        /// per object and a short-lived object none, so what fills the buffer is
        /// the room and not its traffic.
        /// ⛔ Only within one entry: a spawn staged under an entry that has
        /// since ended is that entry's room's, and folding a later entry's spawn
        /// into it would hand the object to the entry the adoption discards.
        /// </remarks>
        public ObjectStageOutcome StageObject(EarlyPacketKind kind, byte[] data, int entry, ulong objectId)
            => StageObject(kind, data, entry, objectId, -1L, keepOldest: false);

        /// <summary>
        /// <see cref="StageObject(EarlyPacketKind, byte[], int, ulong)"/>, with the
        /// packet's place in the gateway's send order
        /// (<paramref name="sendCounter"/>, kept on the entry for the release to
        /// judge a departed owner's spawn by, as the live path does) and the
        /// eviction policy <see cref="Stage(EarlyPacketKind, byte[], int, bool)"/>
        /// describes.  A re-send folded into a staged spawn keeps the staged
        /// one's place AND its counter: the place is what the counter describes.
        /// </summary>
        public ObjectStageOutcome StageObject(
            EarlyPacketKind kind, byte[] data, int entry, ulong objectId, long sendCounter, bool keepOldest)
        {
            if (objectId != 0UL
                && _spawnByObject.TryGetValue(objectId, out var staged)
                && staged.Value.Entry == entry)
            {
                if (kind == EarlyPacketKind.Spawn)
                {
                    _bytes += (data?.Length ?? 0) - (staged.Value.Data?.Length ?? 0);
                    staged.Value = new Staged(kind, data, entry, objectId, null, null, staged.Value.Counter);
                    return ObjectStageOutcome.ReplacedTheStagedSpawn;
                }
                if (kind == EarlyPacketKind.Despawn)
                {
                    Remove(staged);
                    return ObjectStageOutcome.CollapsedTheStagedSpawn;
                }
            }

            var appended = Append(new Staged(kind, data, entry, objectId, null, null, sendCounter), keepOldest);
            if (appended == Appended.Refused) return ObjectStageOutcome.Refused;
            if (kind == EarlyPacketKind.Spawn && objectId != 0UL) _spawnByObject[objectId] = _queue.Last;
            return appended == Appended.StagedWithEviction
                ? ObjectStageOutcome.StagedWithEviction
                : ObjectStageOutcome.Staged;
        }

        /// <summary>
        /// Stage a roster marker — <see cref="EarlyPacketKind.PlayerLeft"/>,
        /// <see cref="EarlyPacketKind.PlayerJoined"/> or
        /// <see cref="EarlyPacketKind.MasterChanged"/> — in its place among the
        /// staged objects.  Returns <c>true</c> when an eviction occurred.
        /// </summary>
        public bool StageMarker(EarlyPacketKind kind, int entry, string player, string other = null, long counter = -1L)
            => StageMarker(kind, entry, player, other, counter, keepOldest: false);

        /// <summary>
        /// <see cref="StageMarker(EarlyPacketKind, int, string, string, long)"/>
        /// under the eviction policy <paramref name="keepOldest"/> names.
        /// </summary>
        public bool StageMarker(EarlyPacketKind kind, int entry, string player, string other, long counter, bool keepOldest)
            => Append(new Staged(kind, null, entry, 0UL, player, other, counter), keepOldest) != Appended.Staged;

        private Appended Append(in Staged item, bool keepOldest)
        {
            bool evicted = false;
            int staged = item.Data?.Length ?? 0;
            bool protectedItem = IsProtected(item.Kind);

            // Both ceilings, and the queue's emptiness ends the walk: a single
            // packet larger than the byte ceiling is still staged, because the
            // receive path has no other copy of it.
            while (_queue.Count > 0
                   && (_queue.Count >= _capacity || _bytes + staged > MaxBytes))
            {
                // In the room the oldest entries are the room's own catch-up, and
                // what overflows the buffer is traffic that arrived since: an
                // unprotected arrival is refused, and a protected one displaces
                // the newest unprotected entry rather than the oldest.
                if (keepOldest && !protectedItem) return Appended.Refused;
                Remove(Victim(newest: keepOldest));
                evicted = true;
            }
            _queue.AddLast(item);
            _bytes += staged;
            return evicted ? Appended.StagedWithEviction : Appended.Staged;
        }

        // The staged packet eviction may take: the oldest unprotected one — the
        // newest, under the in-room policy — or the oldest at all when
        // everything staged is protected.
        private LinkedListNode<Staged> Victim(bool newest)
        {
            if (newest)
            {
                for (var node = _queue.Last; node != null; node = node.Previous)
                {
                    if (!IsProtected(node.Value.Kind)) return node;
                }
                return _queue.First;
            }
            for (var node = _queue.First; node != null; node = node.Next)
            {
                if (!IsProtected(node.Value.Kind)) return node;
            }
            return _queue.First;
        }

        // One staged packet leaves, from wherever it is.
        private void Remove(LinkedListNode<Staged> node)
        {
            Unindex(node);
            Dropped(node.Value);
            _queue.Remove(node);
        }

        private void Unindex(LinkedListNode<Staged> node)
        {
            ulong id = node.Value.ObjectId;
            if (id == 0UL) return;
            var index = node.Value.Kind == EarlyPacketKind.Spawn ? _spawnByObject
                      : node.Value.Kind == EarlyPacketKind.LegacyRpc ? _transferByObject
                      : null;
            if (index != null && index.TryGetValue(id, out var indexed) && ReferenceEquals(indexed, node))
                index.Remove(id);
        }

        private Staged TakeFirst()
        {
            var node = _queue.First;
            Unindex(node);
            _queue.RemoveFirst();
            return node.Value;
        }

        /// <summary>
        /// Remove and return every staged packet in arrival order, leaving the
        /// buffer empty.
        /// </summary>
        /// <remarks>
        /// ⛔ **Not the release path.** The receive path takes
        /// <see cref="DrainBounded"/> and the single-packet
        /// <see cref="Dispatch(Staged, Action{byte[]}, Action{byte[]}, Action{byte[]}, Action{EarlyPacketKind})"/>,
        /// because releasing a whole catch-up set in one frame is the defect
        /// `CORE-RD-01` names.  This pair remains as the buffer's whole-contents
        /// contract — ordering, eviction, emptiness — which its own tests state,
        /// and the two `Dispatch` overloads are held to the same routing by
        /// `TheBatchOverloadRoutesIdenticallyBecauseItDelegates`, so a divergent
        /// copy of the switch cannot hide here.
        /// </remarks>
        public Staged[] Drain()
        {
            if (_queue.Count == 0)
                return Array.Empty<Staged>();

            var items = new Staged[_queue.Count];
            _queue.CopyTo(items, 0);
            _queue.Clear();
            _spawnByObject.Clear();
            _transferByObject.Clear();
            _bytes = 0;
            return items;
        }

        /// <summary>
        /// Release up to <paramref name="maxItems"/> staged packets in arrival
        /// order.  Returns how many were dispatched.
        /// </summary>
        /// <remarks>
        /// A per-call ceiling rather than a per-packet budget, deliberately.
        /// The set this holds is bounded and arrives once, so what it needs is
        /// to be spread over a few frames — not metered against a rate whose
        /// purpose is to bound a sustained hostile stream.  Metering it stretched
        /// the release to tens of seconds, and everything downstream that
        /// assumes a lifecycle packet is applied promptly (the despawn tombstone
        /// and departed-player windows, both 5 s; every inbound handler that
        /// resolves an object id) is wrong over a window that long.
        ///
        /// ⚠️ <paramref name="dispatch"/> must not throw: the packet is dequeued
        /// before it runs, so an escaping exception loses that packet and stops
        /// the pump.  The caller is responsible for isolating its handler.
        /// </remarks>
        public int DrainBounded(Action<Staged> dispatch, int maxItems)
        {
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));

            int dispatched = 0;
            while (dispatched < maxItems && _queue.Count > 0)
            {
                var staged = TakeFirst();
                Dropped(staged);
                dispatch(staged);
                dispatched++;
            }
            return dispatched;
        }

        /// <summary>Discard every staged packet without replaying it.</summary>
        public void Clear()
        {
            _queue.Clear();
            _spawnByObject.Clear();
            _transferByObject.Clear();
            _bytes = 0;
        }

        // One packet has left the queue; its bytes go with it.  Every departure
        // that leaves the rest of the queue standing goes through here, so the
        // reading cannot drift from the queue it describes.
        private void Dropped(in Staged staged) => _bytes -= staged.Data?.Length ?? 0;

        /// <summary>
        /// The room entry <paramref name="entry"/> has completed and its
        /// catch-up may be released: every packet staged under it or under a
        /// later entry stays for the flush, and every packet staged under an
        /// earlier one is discarded here, unreleased — that entry ended without
        /// a transition, and what it staged is another room's lifecycle.
        /// Returns how many were discarded.
        /// </summary>
        /// <remarks>
        /// Entries are staged in arrival order and the value only ever rises
        /// between arrivals, so the discarded packets are a prefix of the
        /// queue; they are nonetheless found by value, not by position, so the
        /// rule holds whatever order a caller staged in.
        /// </remarks>
        public int AdoptEntry(int entry)
        {
            int discarded = 0;
            var node = _queue.First;
            while (node != null)
            {
                var next = node.Next;
                if (node.Value.Entry < entry) { discarded++; Remove(node); }
                node = next;
            }
            return discarded;
        }

        /// <summary>
        /// Route a drained batch to its per-kind handler in arrival order. The
        /// handlers are injected so the kind→handler mapping — including the loud
        /// drop of an unmapped kind — can be exercised independently of the
        /// Unity-only receive path that owns the live handlers. A kind added to
        /// <see cref="EarlyPacketKind"/> without a case here falls to
        /// <paramref name="onUnhandled"/> rather than being silently routed to
        /// the wrong handler.
        /// </summary>
        internal static void Dispatch(
            Staged[] drained,
            Action<byte[]> onSpawn,
            Action<byte[]> onDespawn,
            Action<byte[]> onRpcReplay,
            Action<EarlyPacketKind> onUnhandled)
        {
            foreach (var staged in drained)
                Dispatch(staged, onSpawn, onDespawn, onRpcReplay, onUnhandled);
        }

        /// <summary>
        /// Route ONE staged packet to its handler.  The paced drain releases a
        /// packet at a time, and the batch overload above delegates here, so the
        /// kind→handler mapping — including the loud drop of an unmapped kind —
        /// is stated once for both.
        /// </summary>
        internal static void Dispatch(
            Staged staged,
            Action<byte[]> onSpawn,
            Action<byte[]> onDespawn,
            Action<byte[]> onRpcReplay,
            Action<EarlyPacketKind> onUnhandled)
        {
            switch (staged.Kind)
            {
                case EarlyPacketKind.Spawn:     onSpawn(staged.Data);     break;
                case EarlyPacketKind.Despawn:   onDespawn(staged.Data);   break;
                case EarlyPacketKind.RpcReplay: onRpcReplay(staged.Data); break;
                default:                        onUnhandled(staged.Kind); break;
            }
        }

        /// <summary>
        /// Route ONE staged packet or marker, every kind included: what the
        /// receive path's release takes.  The three packet kinds the narrower
        /// overload routes go to the same handlers here.
        /// </summary>
        internal static void Dispatch(
            Staged staged,
            Action<Staged> onSpawn,
            Action<byte[]> onDespawn,
            Action<byte[]> onRpcReplay,
            Action<byte[]> onLegacyRpc,
            Action<Staged> onRosterMarker,
            Action<EarlyPacketKind> onUnhandled)
        {
            switch (staged.Kind)
            {
                // The whole entry, for the place in the send order it carries.
                case EarlyPacketKind.Spawn:         onSpawn(staged);          break;
                case EarlyPacketKind.LegacyRpc:     onLegacyRpc(staged.Data); break;
                case EarlyPacketKind.PlayerLeft:
                case EarlyPacketKind.PlayerJoined:
                case EarlyPacketKind.MasterChanged: onRosterMarker(staged);   break;
                case EarlyPacketKind.Despawn:       onDespawn(staged.Data);   break;
                case EarlyPacketKind.RpcReplay:     onRpcReplay(staged.Data); break;
                default:                            onUnhandled(staged.Kind); break;
            }
        }
    }
}
