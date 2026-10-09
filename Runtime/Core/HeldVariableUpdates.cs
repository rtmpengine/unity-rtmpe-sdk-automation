// RTMPE SDK — Runtime/Core/HeldVariableUpdates.cs
//
// Bounded, order-preserving hold for VariableUpdate (0x41) frames that reach
// the client before the object they describe exists here — the late-join
// snapshot, in the order the room actually delivers it.
//
// The Room Service publishes `player_joined` to the peers BEFORE it answers
// the joiner and BEFORE it replays the room's live objects to them, and every
// peer answers `player_joined` by marking every variable it owns dirty and
// flushing on its next tick (≈33 ms).  So the snapshot the peers send for the
// joiner's benefit reaches the joiner while it is still outside the room, or
// inside it with the object not yet spawned, and the inbound handler — which
// applies nothing pre-room and routes by object id through a registry that
// holds only spawned objects — dropped it.  A list recovered on its periodic
// full-sync; a scalar never did, because a scalar is re-sent only when its
// owner writes it again, which for the static values the snapshot exists for
// is never.  `architecture.md` §8 promised the snapshot "within one flush";
// the promise held only when the datagrams happened to arrive in the order
// the diagram drew them.
//
// What this holds is the frame, whole and unparsed beyond its object id: it
// is applied through the same handler a live frame takes, once the object it
// names has spawned, and in arrival order — so a scalar ends at the value its
// last frame carried and a list's deltas precede the full-sync that follows
// them, exactly as they would have had the object existed on arrival.
// Nothing is applied while held, so the pre-room "no state is applied before
// the join reply" guarantee stands; the release happens from inside the
// spawn that makes the object known, before the next inbound packet is
// dispatched, so no later live frame is ever overtaken by a held one and the
// per-variable tick watermark reads them in the order they were sent.
//
// A held frame belongs to a room entry, as a staged Spawn does: it carries an
// object id and no room, so the entry begun most recently when it arrived is
// the only thing that says which room it describes, and what was held under an
// entry that ended without a transition is discarded when the next entry
// completes (AdoptEntry), never released into a room it does not belong to.
//
// Bounded four ways.  A CAPACITY in frames AND in bytes, evicting the oldest
// first, because the hold can fill under a burst the join never completes —
// and a held frame is a whole datagram, so a ceiling counted in frames alone
// prices every frame at the largest one the receive path admits while the
// frames this hold exists for are a hundred bytes or so; a PER-OBJECT ceiling,
// because an object this client will never spawn — an unregistered prefab, a
// spawn the count cap refused, an owner who has left — whose owner writes a
// variable every tick would otherwise fill the whole capacity with its own
// stream and evict the snapshot frames of the objects still in the replay
// ladder (the ceiling keeps that object's NEWEST frames, which for a scalar
// is the value that wins anyway); an AGE, because a frame for an object that
// never spawns here has nothing to wait for and must not sit until the
// session ends; and a DISCARD on the object's despawn, for the same frame
// that arrived in the other order.  The age is generous rather than tight: a
// join ladder can run for seconds and the frames it outlasts are still the
// values the room holds, while an expired frame costs one stale value until
// its owner writes it again or a later snapshot re-sends it.
//
// Confined to the receive (main) thread like the rest of the inbound path, so
// it carries no synchronisation of its own.
//
// ⚠️ The type holds a frame against an object id and knows nothing else about
// it, and a second instance holds Enhanced RPC payloads on the same terms
// (NetworkManager._heldRpcEvents): an RPC addresses an object as a variable
// update does, and arrives ahead of it for the same reasons.

using System;
using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// Order-preserving, capacity- and age-bounded hold for VariableUpdate
    /// frames whose object is not yet spawned on this client, released per
    /// object the moment it spawns.
    /// </summary>
    internal sealed class HeldVariableUpdates
    {
        /// <summary>One held frame: which object it names, when it arrived, and under which room entry.</summary>
        internal readonly struct Held
        {
            /// <summary>The object id the frame's payload names.</summary>
            public ulong ObjectId { get; }

            /// <summary>The whole frame, owned by the hold.</summary>
            public byte[] Frame { get; }

            /// <summary>The room entry begun most recently when the frame arrived.</summary>
            public int Entry { get; }

            /// <summary>The monotonic clock reading at arrival, in milliseconds.</summary>
            public long ArrivedMillis { get; }

            /// <summary>
            /// The frame's place among its object's frames, counted from the first
            /// held for it: below <see cref="ProtectedHeadFrames"/> it is the
            /// object's head, which eviction takes last.
            /// </summary>
            public int Ordinal { get; }

            public Held(ulong objectId, byte[] frame, int entry, long arrivedMillis, int ordinal = 0)
            {
                ObjectId      = objectId;
                Frame         = frame;
                Entry         = entry;
                ArrivedMillis = arrivedMillis;
                Ordinal       = ordinal;
            }

            /// <summary>Whether the frame is in its object's protected head.</summary>
            public bool InHead => Ordinal < ProtectedHeadFrames;
        }

        /// <summary>
        /// How many of an object's first held frames eviction takes last:
        /// sixteen.
        /// </summary>
        /// <remarks>
        /// 🔑 The first frames held for an object are the late-join snapshot —
        /// its owner marks every variable dirty when a player joins and sends
        /// them all, and again a second later — and the snapshot is the only
        /// copy of a value that does not change: a frame after it carries what
        /// changed and nothing else. While the room's scene loads, an object
        /// whose owner writes a variable every tick fills its share of the hold
        /// in about two seconds, and evicting the oldest frame first evicted the
        /// snapshot, leaving the joiner with the default of every value that had
        /// not changed during its load. Sixteen covers the first copy of the
        /// snapshot of an object of up to sixteen components — and the second,
        /// a second later, only when its owner writes little in between, since
        /// live frames take the head's remaining places; past the head the
        /// oldest unprotected frame goes first, and a head is evicted only when
        /// nothing else is left to evict, which a hold of more than 2048 / 16 =
        /// 128 objects' heads reaches.
        /// </remarks>
        internal const int ProtectedHeadFrames = 16;

        /// <summary>
        /// How long a frame may wait for its object, in milliseconds.  Ten
        /// seconds: past every join ladder this SDK runs, and a frame that
        /// outlives it names an object this client will not be spawning.
        /// </summary>
        internal const long MaxAgeMillis = 10_000L;

        /// <summary>
        /// The most frames held for ONE object; past it the object's oldest
        /// frame goes.  Sixty-four: two snapshots' worth for an object of many
        /// components plus the changes between them, and a bound on what a
        /// stream for an object that never spawns here can take from the rest.
        /// </summary>
        internal const int PerObjectCapacity = 64;

        /// <summary>
        /// The most bytes ONE object's frames may hold: an eighth of the whole.
        /// </summary>
        /// <remarks>
        /// ⛔ The per-object ceiling has to be stated in both units, or it stops
        /// bounding what it exists to bound. In frames alone it capped one
        /// object at 64 of the hold's 2048 — three per cent — while in bytes
        /// sixteen frames of the largest size the receive path admits fill the
        /// whole byte budget, after which every arrival evicts from the head:
        /// the joiner's snapshot. An eighth leaves room for seven other objects
        /// at the same size and is far above what a snapshot frame is.
        /// </remarks>
        internal const long PerObjectMaxBytes = MaxBytes / 8L;

        /// <summary>
        /// The most bytes held at once, whatever the frame count: one mebibyte.
        /// </summary>
        /// <remarks>
        /// The frame ceiling answers "is a whole catch-up set ever partially
        /// shed"; this answers "what can the hold cost".  They are different
        /// questions because a held frame is a whole datagram: at the frame
        /// ceiling alone the hold is worth the ceiling times the largest
        /// datagram the receive path admits, which a member that writes a
        /// variable every tick for an object this client will never spawn is
        /// free to make it.  One mebibyte is above the frame ceiling times the
        /// size the frames this hold exists for actually are — a snapshot frame
        /// carries one object's components' variables — so the byte line is
        /// reached by a stream and not by a join.
        /// </remarks>
        internal const long MaxBytes = 1L << 20;

        private readonly int _capacity;

        // The frames' bytes, maintained with the queue rather than walked: the
        // hold is read once a frame by the debugger and written on the receive
        // path, and a sum over a queue of two thousand is not a per-frame cost.
        private long _bytes;

        // How many frames and how many bytes each object has in the queue, so
        // the per-object ceilings are a dictionary read and not a walk on every
        // hold.
        private readonly Dictionary<ulong, int> _perObject = new Dictionary<ulong, int>();

        // The ordinal the next frame held for each object takes; forgotten with
        // the object's last frame, so a new wait starts its head again.
        private readonly Dictionary<ulong, int> _nextOrdinal = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, long> _perObjectBytes = new Dictionary<ulong, long>();

        // One queue in arrival order rather than a queue per object: eviction
        // and expiry both take the OLDEST frame overall, which is the head of
        // an arrival-ordered queue and would need a search across per-object
        // queues.  A release scans the queue for one object's frames; the
        // queue is bounded by the capacity, and a release runs once per spawn.
        private readonly Queue<Held> _queue;

        // Scratch for the operations that keep some frames and drop others.
        // Never live across an `apply`: a release drains it before the first
        // frame is applied, so a release re-entered from inside an apply finds
        // it free.  A release allocates only when it has something to release.
        private readonly List<Held> _kept = new List<Held>();

        // Whether the last tick was a paused one (see Tick).
        private bool _paused;

        /// <param name="capacity">
        /// Maximum frames held at once.  Sized like the early-object staging
        /// buffer — above a room's live object count — so a whole late-join
        /// snapshot is never partially shed under normal play; overflow evicts
        /// the oldest, which keeps the most recent value of a variable whose
        /// frames are the ones being held.
        /// </param>
        public HeldVariableUpdates(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _queue    = new Queue<Held>();
        }

        /// <summary>Frames currently held.</summary>
        public int Count => _queue.Count;

        /// <summary>
        /// The bytes those frames occupy — the reading the byte ceiling is
        /// stated against, and the one an integrator watching a join can see.
        /// </summary>
        public long Bytes => _bytes;

        /// <summary>The monotonic clock this hold ages frames against, in milliseconds.</summary>
        public static long NowMillis()
        {
            // Stopwatch-based, as the SpawnManager's own clock is: a wall-time
            // adjustment must not expire a frame or keep one for ever.
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            return ticks * 1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>
        /// Hold <paramref name="frame"/> for <paramref name="objectId"/> under
        /// <paramref name="entry"/>, the room entry begun most recently, stamped
        /// with <paramref name="nowMillis"/>.  At the object's own ceiling that
        /// object's oldest frame outside its protected head goes first; at
        /// capacity the oldest such frame overall goes (see
        /// <see cref="ProtectedHeadFrames"/>).  Returns <c>true</c> when an
        /// eviction occurred.
        /// </summary>
        public bool Hold(ulong objectId, byte[] frame, int entry, long nowMillis)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            bool evicted = false;
            _perObject.TryGetValue(objectId, out int held);
            _perObjectBytes.TryGetValue(objectId, out long heldBytes);

            // The object's own ceilings, in frames AND in bytes: its oldest
            // frame is the first of its frames from the head; a walk, but only
            // on the holds that cross a ceiling, which is the stream these
            // ceilings exist to bound.  Its own frames are all that go — the
            // rest of the hold is not this object's to spend.
            while (_perObject.TryGetValue(objectId, out held) && held > 0
                   && (held >= PerObjectCapacity
                       || heldBytes + frame.Length > PerObjectMaxBytes))
            {
                RemoveFirst(anyObject: false, objectId);
                _perObjectBytes.TryGetValue(objectId, out heldBytes);
                evicted = true;
            }

            // Both ceilings, and the queue's emptiness ends the walk: a single
            // frame larger than the byte ceiling is still held, because a hold
            // that refuses the frame it was given holds nothing at all and the
            // receive path has no other copy of it.
            while (_queue.Count > 0
                   && (_queue.Count >= _capacity || _bytes + frame.Length > MaxBytes))
            {
                RemoveFirst(anyObject: true, 0UL);
                evicted = true;
            }

            _nextOrdinal.TryGetValue(objectId, out int ordinal);
            _nextOrdinal[objectId] = ordinal == int.MaxValue ? ordinal : ordinal + 1;
            _queue.Enqueue(new Held(objectId, frame, entry, nowMillis, ordinal));
            _bytes += frame.Length;
            _perObject.TryGetValue(objectId, out held);
            _perObject[objectId] = held + 1;
            _perObjectBytes.TryGetValue(objectId, out heldBytes);
            _perObjectBytes[objectId] = heldBytes + frame.Length;
            return evicted;
        }

        // One frame has left the queue: its bytes and its object's tallies go
        // with it.  Every removal that leaves the REST of the queue standing
        // goes through here; the two that do not — a release, which takes one
        // object's frames whole, and the session teardown, which takes
        // everything — clear the same readings directly.
        private void Dropped(in Held held)
        {
            _bytes -= held.Frame.Length;
            Forget(held.ObjectId, held.Frame.Length);
        }

        // One frame of the object has left the queue.
        private void Forget(ulong objectId, long frameBytes)
        {
            if (_perObjectBytes.TryGetValue(objectId, out long heldBytes))
            {
                long left = heldBytes - frameBytes;
                if (left <= 0) _perObjectBytes.Remove(objectId);
                else _perObjectBytes[objectId] = left;
            }

            if (!_perObject.TryGetValue(objectId, out int held)) return;
            if (held <= 1)
            {
                _perObject.Remove(objectId);
                _nextOrdinal.Remove(objectId);
            }
            else
            {
                _perObject[objectId] = held - 1;
            }
        }

        // Evict one frame — of any object, or of objectId — and the one taken is
        // the oldest outside its object's protected head, or the oldest at all
        // when every candidate is in one: a hold that would rather keep a head
        // than the frame it was just given still has to make room.
        private void RemoveFirst(bool anyObject, ulong objectId)
        {
            int victim = -1, fallback = -1, index = 0;
            foreach (var held in _queue)
            {
                if (anyObject || held.ObjectId == objectId)
                {
                    if (fallback < 0) fallback = index;
                    if (!held.InHead) { victim = index; break; }
                }
                index++;
            }
            if (victim < 0) victim = fallback;
            if (victim < 0) return;

            _kept.Clear();
            int count = _queue.Count;
            for (int i = 0; i < count; i++)
            {
                var held = _queue.Dequeue();
                if (i == victim) { Dropped(held); continue; }
                _kept.Add(held);
            }
            for (int i = 0; i < _kept.Count; i++) _queue.Enqueue(_kept[i]);
            _kept.Clear();
        }

        /// <summary>
        /// Remove every frame held for <paramref name="objectId"/> and hand
        /// each to <paramref name="apply"/> in arrival order, leaving every
        /// other object's frames as they were.  Returns how many were applied.
        /// </summary>
        /// <remarks>
        /// Every frame of the object is removed BEFORE the first is applied:
        /// an <paramref name="apply"/> that reaches back into this hold —
        /// through a user callback that spawns another object, say — sees a
        /// hold that no longer names this object, so a frame cannot be applied
        /// twice and a re-entrant hold of the same object cannot be released
        /// into the middle of this walk.
        /// ⚠️ <paramref name="apply"/> must not throw: a frame is dequeued
        /// before it runs, and an escaping exception loses the frames after it.
        /// The caller isolates its handler, as the staged release does.
        /// </remarks>
        public int Release(ulong objectId, Action<byte[]> apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            if (!_perObject.ContainsKey(objectId)) return 0;

            // The released frames get a list of their own — this walk is
            // re-entered from inside `apply` when a callback spawns another
            // object, and a shared scratch would be cleared under the outer
            // walk.  Allocated only on this path, which runs once per spawn
            // that actually had frames waiting.
            _kept.Clear();
            var released = new List<byte[]>();
            while (_queue.Count > 0)
            {
                var held = _queue.Dequeue();
                if (held.ObjectId == objectId) { _bytes -= held.Frame.Length; released.Add(held.Frame); }
                else _kept.Add(held);
            }
            for (int i = 0; i < _kept.Count; i++) _queue.Enqueue(_kept[i]);
            _kept.Clear();
            _perObject.Remove(objectId);
            _perObjectBytes.Remove(objectId);
            _nextOrdinal.Remove(objectId);

            for (int i = 0; i < released.Count; i++) apply(released[i]);
            return released.Count;
        }

        /// <summary>
        /// Drop every frame held for <paramref name="objectId"/> without
        /// applying it — the object was despawned before it spawned here.
        /// Returns how many were dropped.
        /// </summary>
        public int Discard(ulong objectId) => Remove(byObject: true, objectId, entryBelow: 0);

        /// <summary>
        /// Drop every frame older than <see cref="MaxAgeMillis"/> at
        /// <paramref name="nowMillis"/>.  Returns how many were dropped.
        /// Driven once per frame from the component's own frame loop.
        /// </summary>
        /// <param name="nowMillis">The hold's clock, <see cref="NowMillis"/>.</param>
        /// <param name="paused">
        /// The room's objects are waiting for this client's scene load
        /// (<c>NetworkManager.HoldsObjectsForSceneLoad</c>): the spawns these
        /// frames wait for are held too, and a load can outlast the age, so the
        /// hold does not age — and the first tick after it gives every frame its
        /// whole age again (<see cref="Restamp"/>) before the release spawns
        /// anything.  A pause, not a skip: the scene hold is bounded by a
        /// deadline of its own.
        /// </param>
        public int Tick(long nowMillis, bool paused = false)
        {
            if (paused)
            {
                _paused = true;
                return 0;
            }
            if (_paused)
            {
                _paused = false;
                Restamp(nowMillis);
                return 0;
            }

            // Arrival order is queue order and the stamp never decreases
            // between arrivals, so the expired frames are a prefix.
            int dropped = 0;
            while (_queue.Count > 0 && nowMillis - _queue.Peek().ArrivedMillis > MaxAgeMillis)
            {
                Dropped(_queue.Dequeue());
                dropped++;
            }
            return dropped;
        }

        /// <summary>
        /// Give every frame held now a whole <see cref="MaxAgeMillis"/> from
        /// <paramref name="nowMillis"/>.
        /// </summary>
        /// <remarks>
        /// For a wait that was not the object's absence: while the room's scene
        /// loads, the spawns these frames are waiting for are themselves held
        /// (<c>NetworkSceneManager.HoldsObjectsForSceneLoad</c>), and a load can
        /// take longer than the age — so a paused <see cref="Tick"/> stops aging
        /// the hold for the length of the load and the first tick after it
        /// starts each frame's wait again. Every stamp becomes the same instant,
        /// so the arrival-ordered queue still expires as a prefix.
        /// </remarks>
        public void Restamp(long nowMillis)
        {
            int count = _queue.Count;
            for (int i = 0; i < count; i++)
            {
                var held = _queue.Dequeue();
                _queue.Enqueue(new Held(held.ObjectId, held.Frame, held.Entry, nowMillis, held.Ordinal));
            }
        }

        /// <summary>
        /// The room entry <paramref name="entry"/> has completed: every frame
        /// held under it or under a later entry stays, and every frame held
        /// under an earlier one is discarded — that entry ended without a
        /// transition, and what it held is another room's state.  Returns how
        /// many were discarded.
        /// </summary>
        public int AdoptEntry(int entry) => Remove(byObject: false, 0UL, entryBelow: entry);

        /// <summary>
        /// Session teardown: discard everything.  A frame held across a
        /// session boundary would describe an object of a session that ended.
        /// </summary>
        public void ClearState()
        {
            _paused = false;
            _queue.Clear();
            _perObject.Clear();
            _nextOrdinal.Clear();
            _perObjectBytes.Clear();
            _bytes = 0;
        }

        // The two removals that keep some frames and drop others, as one walk
        // with the predicate spelled out rather than passed in: a closure per
        // despawn is an allocation on the receive path that a flag is not.
        private int Remove(bool byObject, ulong objectId, int entryBelow)
        {
            if (_queue.Count == 0) return 0;

            int removed = 0;
            _kept.Clear();
            while (_queue.Count > 0)
            {
                var held = _queue.Dequeue();
                bool drop = byObject ? held.ObjectId == objectId : held.Entry < entryBelow;
                if (drop) { removed++; Dropped(held); }
                else _kept.Add(held);
            }
            for (int i = 0; i < _kept.Count; i++) _queue.Enqueue(_kept[i]);
            _kept.Clear();
            return removed;
        }
    }
}
