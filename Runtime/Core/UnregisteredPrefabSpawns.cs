// RTMPE SDK — Runtime/Core/UnregisteredPrefabSpawns.cs
//
// Another player's spawns that named a prefab this client had not registered
// yet, kept until RegisterPrefab names it (audit P4-M9).
//
// A spawn is sent once, and the room replays its objects to a client only as
// that client enters.  So a spawn this client could not build when it arrived —
// its prefab registered lazily, after a scene load or from an asset bundle —
// was an object every other player had and this one never would.  It is held
// instead: bounded per prefab and in total, for as long as what arrives for the
// object waits for it (HeldVariableUpdates.MaxAgeMillis), and applied when the
// registration lands, judged then by every gate it would have met on arrival.
// A despawn of the object takes its spawn out of the hold, the room's teardown
// empties it, its owner's departure ends it or hands it on (OwnerLeft), and
// what no registration claims in time is dropped and counted for the caller to
// report.

using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>A spawn packet held for its prefab's registration.</summary>
    internal readonly struct HeldPrefabSpawn
    {
        /// <summary>The spawn packet as it arrived, header included.</summary>
        public readonly byte[] Packet;

        /// <summary>The gateway's send counter it arrived under, <c>-1</c> when unknown.</summary>
        public readonly long SentAt;

        /// <summary>The object the spawn builds.</summary>
        public readonly ulong ObjectId;

        /// <summary>The owner the spawn names.</summary>
        public readonly string Owner;

        /// <summary>
        /// Whether that owner left while the spawn waited: the room kept the
        /// object and handed it to its host, which the release does too.
        /// </summary>
        public readonly bool OwnerLeft;

        /// <summary>
        /// The player the room has given the object to since its owner left — the
        /// host at that departure, and whoever each later departure of the holder
        /// or change of host handed it on to; null while it waits for a host to
        /// take it (the owner was the host, or no host was known).
        /// </summary>
        public readonly string Heir;

        public HeldPrefabSpawn(byte[] packet, long sentAt, ulong objectId = 0UL, string owner = null,
                               bool ownerLeft = false, string heir = null)
        {
            Packet    = packet;
            SentAt    = sentAt;
            ObjectId  = objectId;
            Owner     = owner;
            OwnerLeft = ownerLeft;
            Heir      = heir;
        }
    }

    /// <summary>
    /// Spawn packets held per prefab id until that id is registered or the hold
    /// expires, bounded per prefab and in total.  Single-thread; the surrounding
    /// <see cref="SpawnManager"/> runs every entry point on the Unity main thread.
    /// </summary>
    internal sealed class UnregisteredPrefabSpawns
    {
        /// <summary>Most spawns held for one prefab id.</summary>
        public const int MaxPerPrefab = 32;

        /// <summary>Most spawns held across every prefab id.</summary>
        public const int MaxTotal = 128;

        /// <summary>
        /// How long a spawn waits for its prefab's registration: as long as the
        /// variable updates, calls and transfers that arrive for its object
        /// wait for it, and no longer.  Built later, the object would be built
        /// without them — its late-join snapshot among them, which is not sent
        /// again — and differ from every other player's for good.
        /// </summary>
        public const long HoldMs = HeldVariableUpdates.MaxAgeMillis;

        private struct Held
        {
            public ulong ObjectId;
            public HeldPrefabSpawn Spawn;
            public long ExpiresAtMs;
            public string Owner;
            public bool DestroyWithOwner;
            public bool OwnerLeft;
            public string Heir;
        }

        private readonly Dictionary<uint, List<Held>> _byPrefab = new Dictionary<uint, List<Held>>(4);
        private int _count;
        private int _expiredUnreported;

        /// <summary>Spawns held across every prefab id.</summary>
        public int Count => _count;

        /// <summary>Whether anything is held for <paramref name="prefabId"/>.</summary>
        public bool Holds(uint prefabId) => _byPrefab.ContainsKey(prefabId);

        /// <summary>
        /// Hold the spawn of <paramref name="objectId"/>, sent under
        /// <paramref name="sentAt"/> and naming <paramref name="owner"/>, for
        /// <paramref name="prefabId"/>'s registration until <see cref="HoldMs"/>
        /// from <paramref name="nowMs"/>.  A copy of a spawn already held — the
        /// room's re-replay sends each object again — is the one held, and is
        /// answered as held.  False when a bound refuses it — the caller drops
        /// it, as it did before holds existed.
        /// </summary>
        public bool Hold(uint prefabId, ulong objectId, byte[] packet, long sentAt, long nowMs,
                         string owner = null, bool destroyWithOwner = false)
        {
            Prune(nowMs);
            if (_byPrefab.TryGetValue(prefabId, out var held))
            {
                for (int i = 0; i < held.Count; i++)
                    if (held[i].ObjectId == objectId) return true;
            }
            if (_count >= MaxTotal) return false;
            if (held != null)
            {
                if (held.Count >= MaxPerPrefab) return false;
            }
            else
            {
                held = new List<Held>(2);
                _byPrefab[prefabId] = held;
            }
            held.Add(new Held
            {
                ObjectId         = objectId,
                Spawn            = new HeldPrefabSpawn(packet, sentAt, objectId, owner),
                ExpiresAtMs      = nowMs + HoldMs,
                Owner            = owner,
                DestroyWithOwner = destroyWithOwner,
            });
            _count++;
            return true;
        }

        /// <summary>
        /// Take every spawn still held for <paramref name="prefabId"/>, in the
        /// order it arrived.  Empty when there is none.
        /// </summary>
        public List<HeldPrefabSpawn> Take(uint prefabId, long nowMs)
        {
            var released = new List<HeldPrefabSpawn>();
            Prune(nowMs);
            if (!_byPrefab.TryGetValue(prefabId, out var held)) return released;
            _byPrefab.Remove(prefabId);
            _count -= held.Count;
            foreach (var h in held)
                released.Add(new HeldPrefabSpawn(h.Spawn.Packet, h.Spawn.SentAt, h.ObjectId, h.Owner, h.OwnerLeft, h.Heir));
            return released;
        }

        /// <summary>
        /// Whether a spawn held for <paramref name="prefabId"/> whose owner left
        /// as it waited is one <paramref name="hasHeir"/> — given its owner and
        /// the heir recorded at the departure — finds nobody to hand the object
        /// to.
        /// </summary>
        public bool AwaitsAnHeir(uint prefabId, System.Func<string, string, bool> hasHeir)
        {
            if (!_byPrefab.TryGetValue(prefabId, out var held)) return false;
            for (int i = 0; i < held.Count; i++)
                if (held[i].OwnerLeft && !hasHeir(held[i].Owner, held[i].Heir)) return true;
            return false;
        }

        /// <summary>
        /// The prefab ids something is held for, or null when nothing is.
        /// </summary>
        public List<uint> HeldPrefabIds()
        {
            if (_count == 0) return null;
            return new List<uint>(_byPrefab.Keys);
        }

        /// <summary>
        /// <paramref name="owner"/> has left, as the gateway recorded under
        /// <paramref name="departedAt"/> (<c>-1</c> when it recorded none): what
        /// it spawned before leaving is the life that ended.  Every client tore
        /// down the objects declared destroyed with their owner, so their spawns
        /// are dropped and their ids returned; the room kept the others and
        /// handed them to its host — <paramref name="heir"/>, null when none was
        /// known — so theirs stay held, marked for the release to hand on to
        /// that player.  A spawn already handed to the leaver goes on to
        /// <paramref name="heir"/> as well.  Null when nothing was dropped.
        /// </summary>
        /// <remarks>
        /// <para>The heir is recorded here, not read at the release: the room
        /// hands a leaver's objects to the host it has when the leaver goes, and
        /// a later voluntary change of host moves none of them (audit P4-E1
        /// review).</para>
        /// <para>⛔ Nothing goes back to a player who left it.  A player id
        /// outlives a departure — a reconnect keeps it — so an heir who leaves and
        /// returns finds the object with whoever its departure handed it to, and
        /// an owner who returns and leaves again moves nothing it was given away
        /// (the second look at P4-E1).</para>
        /// </remarks>
        public List<ulong> OwnerLeft(string owner, long departedAt, string heir = null)
        {
            if (_count == 0 || string.IsNullOrEmpty(owner)) return null;
            List<ulong> dropped = null;
            List<uint> emptied = null;
            foreach (var entry in _byPrefab)
            {
                var held = entry.Value;
                int kept = 0;
                for (int i = 0; i < held.Count; i++)
                {
                    var h = held[i];
                    if (h.OwnerLeft)
                    {
                        if (h.Heir == owner) h.Heir = heir;
                        held[kept++] = h;
                        continue;
                    }
                    bool endedLife = h.Owner == owner
                        && !(departedAt >= 0 && h.Spawn.SentAt > departedAt);
                    if (endedLife && h.DestroyWithOwner)
                    {
                        (dropped ??= new List<ulong>()).Add(h.ObjectId);
                        continue;
                    }
                    if (endedLife)
                    {
                        h.OwnerLeft = true;
                        h.Heir      = heir;
                    }
                    held[kept++] = h;
                }
                _count -= held.Count - kept;
                held.RemoveRange(kept, held.Count - kept);
                if (held.Count == 0) (emptied ??= new List<uint>()).Add(entry.Key);
            }
            if (emptied != null)
                foreach (var prefabId in emptied) _byPrefab.Remove(prefabId);
            return dropped;
        }

        /// <summary>
        /// The room's host changed from <paramref name="previous"/> to
        /// <paramref name="next"/>: the held spawns whose owner left go where the
        /// room sends the objects they stand for.  When the previous host left,
        /// what it held and what waited for a host to take it go to the new one;
        /// on a voluntary change, the previous host stays and keeps what it holds,
        /// and what waited goes to it, the host of its time — as the gateway hands
        /// its records over.  Never to the player who left the object.
        /// </summary>
        public void HostChanged(string previous, string next, bool previousStillInRoom)
        {
            if (_count == 0) return;
            string taker = previousStillInRoom ? previous : next;
            if (string.IsNullOrEmpty(taker) || previous == next) return;
            foreach (var entry in _byPrefab)
            {
                var held = entry.Value;
                for (int i = 0; i < held.Count; i++)
                {
                    var h = held[i];
                    if (!h.OwnerLeft || h.Owner == taker) continue;
                    bool waiting = string.IsNullOrEmpty(h.Heir);
                    if (waiting || (!previousStillInRoom && h.Heir == previous))
                    {
                        h.Heir  = taker;
                        held[i] = h;
                    }
                }
            }
        }

        /// <summary>
        /// Drop every held spawn of <paramref name="objectId"/> — a despawn of
        /// the object is its last word.  Returns how many were dropped.
        /// </summary>
        public int Forget(ulong objectId)
        {
            int dropped = 0;
            List<uint> emptied = null;
            foreach (var entry in _byPrefab)
            {
                int removed = entry.Value.RemoveAll(h => h.ObjectId == objectId);
                if (removed == 0) continue;
                dropped += removed;
                if (entry.Value.Count == 0) (emptied ??= new List<uint>()).Add(entry.Key);
            }
            if (emptied != null)
                foreach (var prefabId in emptied) _byPrefab.Remove(prefabId);
            _count -= dropped;
            return dropped;
        }

        /// <summary>Drop what has waited past its expiry, counting it for <see cref="TakeExpiredCount"/>.</summary>
        public void Prune(long nowMs)
        {
            if (_count == 0) return;
            List<uint> emptied = null;
            foreach (var entry in _byPrefab)
            {
                int removed = entry.Value.RemoveAll(h => h.ExpiresAtMs <= nowMs);
                if (removed == 0) continue;
                _count -= removed;
                _expiredUnreported += removed;
                if (entry.Value.Count == 0) (emptied ??= new List<uint>()).Add(entry.Key);
            }
            if (emptied != null)
                foreach (var prefabId in emptied) _byPrefab.Remove(prefabId);
        }

        /// <summary>
        /// How many spawns expired unclaimed since the last call, and reset it:
        /// each is an object every other player has and this client will not.
        /// </summary>
        public int TakeExpiredCount()
        {
            int expired = _expiredUnreported;
            _expiredUnreported = 0;
            return expired;
        }

        /// <summary>Drop everything held — the room it belonged to is over.</summary>
        public void Clear()
        {
            _byPrefab.Clear();
            _count = 0;
        }
    }
}
