// RTMPE SDK — Runtime/Core/DepartedOwnerSpawns.cs
//
// Spawns that arrived while their owner's departure tombstone stood, sent by
// the gateway after the departure, kept for the owner's return.
//
// A player id outlives a departure: a reconnect keeps it, and so does a rejoin
// by the same session.  The room's events and the players' frames reach a peer
// along two paths with no order between them, so a returning player's first
// Spawn can land between that player's PlayerLeft and its PlayerJoined — and
// the tombstone (DepartedPlayerTracker), which exists to drop a departed
// player's straggling spawn, dropped the returner's first object for good.
//
// What tells the two apart is the gateway's own send order, together with its
// refusal to relay a spawn whose sender no longer holds the room.  Every packet
// the gateway sends this client carries the session's send counter, room events
// and relayed frames alike: a Spawn it sent BEFORE the PlayerLeft is a straggler
// of the life that ended, however late it reaches this client, and is dropped as
// it always was.  One it sent AFTER is held.  The gateway withholds a spawn
// relay whose sender has left the room or gone, or whose departure the room has
// announced while the leave awaits its answer — it tells one of its own
// sessions from a sender elsewhere by the id range it issues — and the room
// replays only the objects of players seated in it, so what it sends after a
// departure is the returner's.  What neither can judge: a relay the gateway
// judged in the moment before it marked the announcement, a relay that outlived
// a full leave and return of the same session, one from a sender on another
// gateway, and — for a player who left and came back on the same session
// inside the room's eviction grace — that life's torn-down objects, replayable
// again under the session that is back.  What the room's despawn relay ended
// here, and what this client ended itself under an owner a spawn still names —
// torn down with a departure under the owner that left, or despawned under an
// owner it had here — is not held while its record stands
// (SpawnManager.HoldForOwnersReturn).  Held spawns are applied the
// moment the owner's arrival lifts the tombstone, in the order they arrived,
// and dropped when no arrival comes before the tombstone expires.  A later
// departure of the same owner ends that life too, and takes with it what was
// held from it.

using System;
using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>What became of a spawn whose owner's departure tombstone stands.</summary>
    internal enum HeldSpawnVerdict
    {
        /// <summary>Kept for the owner's return.</summary>
        Held,

        /// <summary>
        /// Dropped as the tombstone always did: a straggler of the life that
        /// ended, or an object already despawned.
        /// </summary>
        Dropped,

        /// <summary>Dropped because a bound of the hold is full.</summary>
        Refused,
    }

    /// <summary>
    /// Items held per departed owner until that owner returns or the hold
    /// expires, each with the gateway's send counter it arrived under, bounded
    /// per owner and in total.  Single-thread; the surrounding
    /// <see cref="SpawnManager"/> runs every entry point on the Unity main thread.
    /// </summary>
    internal sealed class DepartedOwnerSpawns<T>
    {
        /// <summary>Most items held for one owner.</summary>
        public const int MaxPerOwner = 32;

        /// <summary>Most items held across every owner.</summary>
        public const int MaxTotal = 128;

        private struct Held
        {
            public T Item;
            public long SendCounter;
            public long ExpiresAtMs;
        }

        private readonly Dictionary<string, List<Held>> _byOwner =
            new Dictionary<string, List<Held>>(4);

        private int _count;

        /// <summary>Items held across every owner.</summary>
        public int Count => _count;

        /// <summary>
        /// Whether a packet the gateway sent under
        /// <paramref name="relaySendCounter"/> was sent after the departure it
        /// recorded under <paramref name="departureSendCounter"/> — the only
        /// packets a return can own.  Unknown on either side (<c>-1</c>) is not
        /// after: a spawn nothing places after the departure is a straggler.
        /// </summary>
        public static bool SentAfterDeparture(long departureSendCounter, long relaySendCounter)
            => departureSendCounter >= 0 && relaySendCounter > departureSendCounter;

        /// <summary>
        /// Hold <paramref name="item"/>, sent under
        /// <paramref name="relaySendCounter"/>, for
        /// <paramref name="ownerPlayerId"/> until <paramref name="expiresAtMs"/>.
        /// False when the owner id is empty or a bound refuses it — the caller
        /// drops the item, as it did before holds existed.
        /// </summary>
        public bool Hold(string ownerPlayerId, T item, long relaySendCounter, long expiresAtMs, long nowMs)
        {
            if (string.IsNullOrEmpty(ownerPlayerId)) return false;
            Prune(nowMs);
            if (_count >= MaxTotal) return false;
            if (!_byOwner.TryGetValue(ownerPlayerId, out var held))
            {
                held = new List<Held>(2);
                _byOwner[ownerPlayerId] = held;
            }
            if (held.Count >= MaxPerOwner) return false;
            held.Add(new Held { Item = item, SendCounter = relaySendCounter, ExpiresAtMs = expiresAtMs });
            _count++;
            return true;
        }

        /// <summary>
        /// Take every item still held for <paramref name="ownerPlayerId"/>, in the
        /// order it arrived.  Empty when there is none.
        /// </summary>
        public List<T> Take(string ownerPlayerId, long nowMs)
        {
            var released = new List<T>();
            if (string.IsNullOrEmpty(ownerPlayerId)) return released;
            Prune(nowMs);
            if (!_byOwner.TryGetValue(ownerPlayerId, out var held)) return released;
            _byOwner.Remove(ownerPlayerId);
            _count -= held.Count;
            foreach (var h in held) released.Add(h.Item);
            return released;
        }

        /// <summary>
        /// Drop what is held for <paramref name="ownerPlayerId"/> that the
        /// gateway sent before a departure it recorded under
        /// <paramref name="departureSendCounter"/>: the owner left again, and
        /// those items belong to the life that has just ended.  A departure with
        /// no place in the send order (<c>-1</c>) drops everything held for the
        /// owner.
        /// </summary>
        public void DropSentBefore(string ownerPlayerId, long departureSendCounter)
        {
            if (string.IsNullOrEmpty(ownerPlayerId)) return;
            if (!_byOwner.TryGetValue(ownerPlayerId, out var held)) return;
            int kept = 0;
            for (int i = 0; i < held.Count; i++)
            {
                if (SentAfterDeparture(departureSendCounter, held[i].SendCounter))
                    held[kept++] = held[i];
            }
            _count -= held.Count - kept;
            held.RemoveRange(kept, held.Count - kept);
            if (held.Count == 0) _byOwner.Remove(ownerPlayerId);
        }

        /// <summary>
        /// Drop every item held past its expiry.  Called every frame, so it
        /// allocates nothing: no closure, and no list unless an owner empties.
        /// </summary>
        public void Prune(long nowMs)
        {
            if (_count == 0) return;
            List<string> emptied = null;
            foreach (var kv in _byOwner)
            {
                var held = kv.Value;
                int kept = 0;
                for (int i = 0; i < held.Count; i++)
                {
                    if (held[i].ExpiresAtMs > nowMs)
                        held[kept++] = held[i];
                }
                _count -= held.Count - kept;
                held.RemoveRange(kept, held.Count - kept);
                if (held.Count == 0)
                {
                    if (emptied == null) emptied = new List<string>();
                    emptied.Add(kv.Key);
                }
            }
            if (emptied != null)
                foreach (var owner in emptied) _byOwner.Remove(owner);
        }

        /// <summary>Drop everything (room leave / session end).</summary>
        public void Clear()
        {
            _byOwner.Clear();
            _count = 0;
        }
    }

    /// <summary>
    /// The objects this client ended itself, each under the owners it had here —
    /// torn down with an owner whose departure was reported, under that owner,
    /// or despawned here by this client, under the owner at the end, the one
    /// before it and the one it was spawned under — kept <see cref="TtlMs"/>
    /// from the end, and a departed owner's from the latest report of the
    /// departure, as its tombstone is.  A spawn of one that still names such an
    /// owner is the room's copy of what it had not yet forgotten, and is refused
    /// while the record stands; one naming whoever took the object over since
    /// is not — an ownership transfer that raced the end on this client, after
    /// which the gateway relays no despawn from the owner it no longer knows,
    /// and the object lives on under the new one.  Bounded; single-thread, like
    /// the <see cref="SpawnManager"/> that drives it.
    /// </summary>
    internal sealed class EndedUnderOwner
    {
        /// <summary>How long a record stands: as a departure's tombstone does.</summary>
        public const long TtlMs = DepartedPlayerTracker.TtlMs;

        /// <summary>
        /// Most owners kept: as many as the tombstones hold, and one more.  A
        /// cap rather than a fit — a record can outlive the tombstone its
        /// owner's return lifted, and this client's own ends are kept under
        /// earlier owners as well as its own.  Past it, the owner whose record
        /// ends soonest makes way.
        /// </summary>
        public const int MaxOwners = DepartedPlayerTracker.MaxEntries + 1;

        /// <summary>
        /// Most objects kept under one owner.  Past it, what has ended is
        /// forgotten first, then the object whose record ends soonest.
        /// </summary>
        public const int MaxObjectsPerOwner = 512;

        private readonly struct Recorded
        {
            public readonly ulong ObjectId;
            public readonly long EndsAtMs;

            public Recorded(ulong objectId, long endsAtMs)
            {
                ObjectId = objectId;
                EndsAtMs = endsAtMs;
            }
        }

        private sealed class OwnerRecord
        {
            // Each object's own end: a departure reported again re-arms what
            // had not ended by then, and nothing that had.
            public readonly Dictionary<ulong, long> EndsAtMs = new Dictionary<ulong, long>();

            // The objects in the order their ends fall: a record ends one TTL
            // after it is made, the manager's clock only moves forward, and a
            // re-arm gives every record the same end.  So what ends soonest is
            // at the head, and forgetting or evicting costs what it removes.
            // Nearly: a departure records under one clock reading while a
            // callback in its teardown can record under a later one, and an
            // entry out of place only waits for the next sweep — Names reads
            // the end on record.  An entry whose object has been forgotten, or
            // recorded again since, no longer matches that end and is passed
            // over.
            public readonly Queue<Recorded> Order = new Queue<Recorded>();

            // The latest end: which owner makes way.
            public long LastEndsAtMs;
        }

        private readonly Dictionary<string, OwnerRecord> _byOwner =
            new Dictionary<string, OwnerRecord>(StringComparer.Ordinal);

        // Reused by the sweeps and the re-arm, so none allocates.
        private readonly List<string> _owners = new List<string>();
        private readonly List<ulong> _objects = new List<ulong>();

        /// <summary>Owners on record.</summary>
        public int OwnerCount => _byOwner.Count;

        /// <summary>Objects on record under <paramref name="ownerPlayerId"/>, ended or not.</summary>
        public int ObjectCount(string ownerPlayerId)
            => !string.IsNullOrEmpty(ownerPlayerId) && _byOwner.TryGetValue(ownerPlayerId, out var record)
                ? record.EndsAtMs.Count
                : 0;

        /// <summary>
        /// Entries in <paramref name="ownerPlayerId"/>'s order, passed-over ones
        /// included: one per object in steady state.
        /// </summary>
        public int OrderCount(string ownerPlayerId)
            => !string.IsNullOrEmpty(ownerPlayerId) && _byOwner.TryGetValue(ownerPlayerId, out var record)
                ? record.Order.Count
                : 0;

        /// <summary>
        /// Record that this client ended <paramref name="objectId"/> under
        /// <paramref name="ownerPlayerId"/>, and keep it from
        /// <paramref name="nowMs"/>.
        /// </summary>
        public void Record(string ownerPlayerId, ulong objectId, long nowMs)
        {
            if (string.IsNullOrEmpty(ownerPlayerId) || objectId == 0UL) return;
            if (!_byOwner.TryGetValue(ownerPlayerId, out var record))
            {
                if (_byOwner.Count >= MaxOwners) EvictSoonestEndingOwner();
                record = new OwnerRecord();
                _byOwner[ownerPlayerId] = record;
            }
            var ends = record.EndsAtMs;
            long end = nowMs + TtlMs;
            if (ends.TryGetValue(objectId, out long recorded))
            {
                // Recorded already at this moment — under an owner named twice.
                if (recorded == end) return;
            }
            else if (ends.Count >= MaxObjectsPerOwner)
            {
                // What has ended goes first; then the head, which the pass leaves
                // matching the end on record — the record that ends soonest.
                ForgetEnded(record, nowMs);
                if (ends.Count >= MaxObjectsPerOwner) ends.Remove(record.Order.Dequeue().ObjectId);
            }
            ends[objectId] = end;
            record.Order.Enqueue(new Recorded(objectId, end));
            if (end > record.LastEndsAtMs) record.LastEndsAtMs = end;
        }

        /// <summary>
        /// Keep <paramref name="ownerPlayerId"/>'s record from
        /// <paramref name="nowMs"/>: a departure reported again re-arms its
        /// tombstone, and tears nothing down — the objects are already gone.
        /// What had ended by then stays ended.
        /// </summary>
        public void Rearm(string ownerPlayerId, long nowMs)
        {
            if (string.IsNullOrEmpty(ownerPlayerId) || !_byOwner.TryGetValue(ownerPlayerId, out var record))
                return;
            ForgetEnded(record, nowMs);
            var ends = record.EndsAtMs;
            if (ends.Count == 0)
            {
                _byOwner.Remove(ownerPlayerId);
                return;
            }
            long end = nowMs + TtlMs;
            _objects.Clear();
            foreach (var objectId in ends.Keys) _objects.Add(objectId);
            record.Order.Clear();
            for (int i = 0; i < _objects.Count; i++)
            {
                ends[_objects[i]] = end;
                record.Order.Enqueue(new Recorded(_objects[i], end));
            }
            _objects.Clear();
            record.LastEndsAtMs = end;
        }

        /// <summary>
        /// Whether a spawn of <paramref name="objectId"/> naming
        /// <paramref name="ownerPlayerId"/> is of an object this client ended
        /// under that owner, while the record stands.
        /// </summary>
        public bool Names(string ownerPlayerId, ulong objectId, long nowMs)
            => !string.IsNullOrEmpty(ownerPlayerId)
               && _byOwner.TryGetValue(ownerPlayerId, out var record)
               && record.EndsAtMs.TryGetValue(objectId, out long end)
               && end > nowMs;

        /// <summary>Forget every record that has ended by <paramref name="nowMs"/>.</summary>
        public void Prune(long nowMs)
        {
            if (_byOwner.Count == 0) return;
            _owners.Clear();
            foreach (var kv in _byOwner)
            {
                ForgetEnded(kv.Value, nowMs);
                if (kv.Value.EndsAtMs.Count == 0) _owners.Add(kv.Key);
            }
            for (int i = 0; i < _owners.Count; i++) _byOwner.Remove(_owners[i]);
            _owners.Clear();
        }

        /// <summary>Drop everything (room leave / session end).</summary>
        public void Clear() => _byOwner.Clear();

        // Forget, from the head, every record that has ended by nowMs.
        private static void ForgetEnded(OwnerRecord record, long nowMs)
        {
            var ends = record.EndsAtMs;
            var order = record.Order;
            while (order.Count > 0)
            {
                var head = order.Peek();
                if (!ends.TryGetValue(head.ObjectId, out long end) || end != head.EndsAtMs)
                {
                    order.Dequeue();
                    continue;
                }
                if (end > nowMs) return;
                ends.Remove(head.ObjectId);
                order.Dequeue();
            }
        }

        private void EvictSoonestEndingOwner()
        {
            string soonest = null;
            long end = long.MaxValue;
            foreach (var kv in _byOwner)
            {
                if (kv.Value.LastEndsAtMs < end)
                {
                    end = kv.Value.LastEndsAtMs;
                    soonest = kv.Key;
                }
            }
            if (soonest != null) _byOwner.Remove(soonest);
        }
    }
}
