// RTMPE SDK — Runtime/Core/DepartedPlayerTracker.cs
//
// Bookkeeping for the leave-before-Spawn race on UDP transport.
//
// A player's `player_left` room event and a Spawn (0x30) that player owns are
// fanned out over different relay paths with no mutual ordering, so a Spawn can
// arrive AFTER its owner has left.  OnPlayerLeftRoom only tears down objects that
// are already registered, so a late Spawn would instantiate an object owned by a
// departed player that no live session can update or despawn — a permanent ghost
// in the local view.  This tracker remembers each departed player under a short
// TTL, together with where the departure fell in the gateway's send order.  A
// Spawn the gateway sent BEFORE the departure is a straggler of the life that
// ended and is dropped; one it sent AFTER is held for the owner's arrival
// (DepartedOwnerSpawns) — the gateway withholds a relay whose sender has left
// the room, gone, or been announced departed, and the room replays only what
// seated players own, within the limits DepartedOwnerSpawns states — and dropped
// when no arrival comes before the tombstone expires.
//
// The tombstone keys on the player id, which outlives a departure: a rejoin by
// the same session keeps it, and so does a reconnect.  A returning player would
// otherwise stay tombstoned and have their own legitimate spawns dropped, so the
// id is cleared explicitly on the return (see Remove, driven by
// SpawnManager.OnPlayerJoinedRoom).  The TTL remains the sole bound for the
// departed-and-not-returning case.

using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// Tracks players who have left the room, so a Spawn that raced behind its
    /// owner's departure is dropped instead of forming a ghost — or, sent after
    /// it, held for the owner's return.
    /// Single-thread; the surrounding <see cref="SpawnManager"/> guarantees every
    /// entry point runs on the Unity main thread.
    /// </summary>
    internal sealed class DepartedPlayerTracker
    {
        // Five seconds matches PendingDespawnTracker: generous against the UDP
        // reorder window (well under 200 ms even on poor links) while bounding
        // how long a departed player suppresses stray spawns.
        public const long TtlMs = 5_000;

        // Bounds memory under pathological room churn; the live set is otherwise
        // at most the room's peak membership within one TTL, far below this cap.
        public const int MaxEntries = 256;

        private struct Tombstone
        {
            public long ExpiresAtMs;
            public long DepartureSendCounter;
        }

        private readonly Dictionary<string, Tombstone> _byPlayerId =
            new Dictionary<string, Tombstone>(16);

        /// <summary>Live tombstone count.</summary>
        public int Count => _byPlayerId.Count;

        /// <summary>Drop every tombstone (room leave / disconnect).</summary>
        public void Clear() => _byPlayerId.Clear();

        /// <summary>
        /// Sweep entries at or past <paramref name="nowMs"/>.  Called from both
        /// mutation paths so a stale tombstone never suppresses a spawn or holds
        /// a cap slot.
        /// </summary>
        public void Prune(long nowMs)
        {
            if (_byPlayerId.Count == 0) return;
            List<string> stale = null;
            foreach (var kv in _byPlayerId)
            {
                if (kv.Value.ExpiresAtMs <= nowMs)
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null)
                foreach (var id in stale) _byPlayerId.Remove(id);
        }

        /// <summary>
        /// Arm (or renew) <paramref name="playerId"/>'s tombstone for
        /// <see cref="TtlMs"/>, for a departure with no place of its own in the
        /// gateway's send order: a fresh tombstone then judges every spawn of the
        /// player's a straggler until it goes.  At capacity the entry expiring
        /// soonest is evicted first.  Empty ids are ignored.
        /// </summary>
        public void Record(string playerId, long nowMs) => Record(playerId, nowMs, -1);

        /// <summary>
        /// Arm (or renew) <paramref name="playerId"/>'s tombstone for
        /// <see cref="TtlMs"/>, recording the gateway's send counter of the packet
        /// that carried the departure — <c>-1</c> when it arrived with none.
        /// A standing tombstone keeps the latest departure it has been told of:
        /// two reports of one departure can be dispatched out of their send
        /// order, and the earlier one must not move the boundary back.  At
        /// capacity the entry expiring soonest is evicted first.  Empty ids are
        /// ignored.
        /// </summary>
        public void Record(string playerId, long nowMs, long departureSendCounter)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            Prune(nowMs);
            if (_byPlayerId.TryGetValue(playerId, out var standing))
                departureSendCounter = System.Math.Max(departureSendCounter, standing.DepartureSendCounter);
            else if (_byPlayerId.Count >= MaxEntries)
                EvictSoonestExpiring();
            _byPlayerId[playerId] = new Tombstone
            {
                ExpiresAtMs = nowMs + TtlMs,
                DepartureSendCounter = departureSendCounter,
            };
        }

        /// <summary>
        /// Lift <paramref name="playerId"/>'s tombstone.  A player present in the
        /// room again — the id outlives a departure, so a rejoin and a reconnect
        /// both return under it — owns legitimate spawns that must no longer be
        /// suppressed; a departure never followed by a return instead expires on
        /// its own via <see cref="TtlMs"/>.  Absent or empty ids are a no-op.
        /// </summary>
        public void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            _byPlayerId.Remove(playerId);
        }

        /// <summary>
        /// True when <paramref name="playerId"/> departed within the last
        /// <see cref="TtlMs"/>.  Non-consuming: a departed player may own several
        /// late spawns, and every one is judged until the tombstone expires.
        /// </summary>
        public bool IsDeparted(string playerId, long nowMs)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            Prune(nowMs);
            return _byPlayerId.ContainsKey(playerId);
        }

        /// <summary>
        /// The standing tombstone of <paramref name="playerId"/>: when it expires,
        /// and the gateway's send counter of the departure it records
        /// (<c>-1</c> when none was known).  False when the player has none.
        /// </summary>
        public bool TryGetTombstone(
            string playerId, long nowMs, out long expiresAtMs, out long departureSendCounter)
        {
            expiresAtMs = 0;
            departureSendCounter = -1;
            if (string.IsNullOrEmpty(playerId)) return false;
            Prune(nowMs);
            if (!_byPlayerId.TryGetValue(playerId, out var tombstone)) return false;
            expiresAtMs = tombstone.ExpiresAtMs;
            departureSendCounter = tombstone.DepartureSendCounter;
            return true;
        }

        private void EvictSoonestExpiring()
        {
            string evict = null;
            long soonest = long.MaxValue;
            foreach (var kv in _byPlayerId)
            {
                if (kv.Value.ExpiresAtMs < soonest)
                {
                    soonest = kv.Value.ExpiresAtMs;
                    evict = kv.Key;
                }
            }
            if (evict != null) _byPlayerId.Remove(evict);
        }
    }
}
