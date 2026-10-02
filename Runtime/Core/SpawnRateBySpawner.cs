// RTMPE SDK — Runtime/Core/SpawnRateBySpawner.cs
//
// How many spawns each spawner has made in the current one-second bucket
// (audit P4-E2).
//
// The spawn rate cap (NetworkSettings.maxSpawnsPerSecond) used to be one count
// for every spawn this client created, whoever sent it.  A spawn refused by it
// is never sent again, so one member spawning past the cap spent every other
// member's second as well: their spawns in that second were dropped on this
// client for good, and so were this client's own.  Counted per spawner, the
// member spawning past the cap is the one whose spawns are dropped.
//
// 🔑 The spawner is read from the object id, not from the owner the payload
// names.  The high half of an id is the digest of the session that minted it,
// and the gateway relays a spawn only when that is the sender's own session —
// so it names who sent the spawn, and nobody can write another's.  The owner
// field cannot: a member that hands an id to another member before spawning it
// has its spawn relayed under that member's name, which would spend the other
// member's count with spawns it never made.
//
// The table is bounded all the same, because this client does not take the
// gateway's word for how many spawners there are; spawners past the bound
// share one count, which is the rule before this file applied to them alone.
//
// ⛔ And the room as a whole still has a ceiling.  The cap exists to bound the
// Instantiate work a flood of spawn frames can put on this client's main
// thread, and counted per spawner alone it would let the frames' senders — a
// gateway that is not honest can write any high half — multiply it by the
// table's bound.  SpawnersAtFullRate spawners' worth is admitted in one second
// in all: one member at its own cap uses one share of that, so no single
// member reaches it, and a room where many spawn at once still does.

using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// Spawns per spawner in the current rate bucket.  Main-thread only, like the
    /// spawn manager that holds it.
    /// </summary>
    internal sealed class SpawnRateBySpawner
    {
        /// <summary>
        /// Spawners counted apart in one bucket.  Past it, every spawner not
        /// already counted shares one count.
        /// </summary>
        internal const int MaxSpawners = 256;

        /// <summary>
        /// How many spawners' worth of spawns, at the per-spawner cap, one bucket
        /// admits in all.
        /// </summary>
        internal const int SpawnersAtFullRate = 8;

        private readonly Dictionary<uint, int> _counts = new Dictionary<uint, int>();
        private int _shared;
        private int _total;

        /// <summary>
        /// Whether one more spawn of <paramref name="objectId"/> fits this bucket:
        /// its spawner is under <paramref name="rateCap"/>, and the bucket under
        /// <see cref="SpawnersAtFullRate"/> times it.
        /// </summary>
        internal bool Admits(ulong objectId, int rateCap)
            => CountOf(objectId) < rateCap && _total < rateCap * SpawnersAtFullRate;

        /// <summary>Spawns charged in this bucket, to anybody.</summary>
        internal int Total => _total;

        /// <summary>
        /// The spawner of <paramref name="objectId"/>: the digest of the session
        /// that minted it, the id's high half (<see cref="ObjectIdMath.BelongsToSession"/>).
        /// </summary>
        internal static uint SpawnerOf(ulong objectId) => (uint)(objectId >> 32);

        /// <summary>Spawns charged in this bucket to the spawner of <paramref name="objectId"/>.</summary>
        internal int CountOf(ulong objectId)
        {
            if (_counts.TryGetValue(SpawnerOf(objectId), out int n)) return n;
            return _counts.Count >= MaxSpawners ? _shared : 0;
        }

        /// <summary>Charge the spawn of <paramref name="objectId"/> to its spawner.</summary>
        internal void Charge(ulong objectId)
        {
            uint key = SpawnerOf(objectId);
            if (_counts.TryGetValue(key, out int n)) _counts[key] = n + 1;
            else if (_counts.Count < MaxSpawners) _counts[key] = 1;
            else _shared++;
            _total++;
        }

        /// <summary>
        /// Give back the charge for a spawn of <paramref name="objectId"/> that did
        /// not happen.  Never below zero.
        /// </summary>
        internal void Refund(ulong objectId)
        {
            uint key = SpawnerOf(objectId);
            if (_counts.TryGetValue(key, out int n))
            {
                if (n > 1) _counts[key] = n - 1;
                else _counts.Remove(key);
            }
            else if (_shared > 0)
            {
                _shared--;
            }
            else
            {
                return;
            }
            _total--;
        }

        /// <summary>Start a new bucket: nobody has spawned in it.</summary>
        internal void Clear()
        {
            _counts.Clear();
            _shared = 0;
            _total = 0;
        }

        /// <summary>Spawners counted apart in this bucket.</summary>
        internal int SpawnersCounted => _counts.Count;
    }
}
