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

            public Staged(EarlyPacketKind kind, byte[] data, int entry)
            {
                Kind  = kind;
                Data  = data;
                Entry = entry;
            }
        }

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
        private readonly Queue<Staged> _queue;

        // Maintained with the queue rather than walked; see the byte ceiling.
        private long _bytes;

        /// <param name="capacity">
        /// Maximum packets held at once.  Sized above a room's live object count
        /// so a full catch-up set is never partially shed under normal play; the
        /// cap exists only to bound the pathological case of a session that
        /// receives lifecycle packets yet never completes a join.
        ///
        /// ⚠️ Overflow evicts the OLDEST, which retains the most recent activity
        /// — right for a set that may never be replayed at all, and the reason
        /// the release must not be stretched: the moment this holds an ordered
        /// stream long enough to overflow, dropping the head drops a Spawn while
        /// keeping the Despawn that removes it.  The release is bounded to a few
        /// frames so that state is not reachable.
        /// </param>
        public EarlyObjectPacketBuffer(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _queue = new Queue<Staged>();
        }

        /// <summary>Packets currently staged.</summary>
        public int Count => _queue.Count;

        /// <summary>The bytes those packets occupy — what the byte ceiling bounds.</summary>
        public long Bytes => _bytes;

        /// <summary>
        /// Stage one catch-up packet under <paramref name="entry"/>, the room
        /// entry begun most recently — a value from the sequence the managers
        /// share, so it is ordered against every other entry's.  At capacity
        /// the oldest staged packet is evicted first, so under overflow the
        /// buffer retains the most recent activity.  Returns <c>true</c> when
        /// an eviction occurred.
        /// </summary>
        public bool Stage(EarlyPacketKind kind, byte[] data, int entry)
        {
            bool evicted = false;
            int staged = data?.Length ?? 0;

            // Both ceilings, and the queue's emptiness ends the walk: a single
            // packet larger than the byte ceiling is still staged, because the
            // receive path has no other copy of it.
            while (_queue.Count > 0
                   && (_queue.Count >= _capacity || _bytes + staged > MaxBytes))
            {
                Dropped(_queue.Dequeue());
                evicted = true;
            }
            _queue.Enqueue(new Staged(kind, data, entry));
            _bytes += staged;
            return evicted;
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

            var items = _queue.ToArray();
            _queue.Clear();
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
                var staged = _queue.Dequeue();
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
            int remaining = _queue.Count;
            while (remaining-- > 0)
            {
                var staged = _queue.Dequeue();
                if (staged.Entry < entry) { discarded++; Dropped(staged); continue; }
                _queue.Enqueue(staged);
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
    }
}
