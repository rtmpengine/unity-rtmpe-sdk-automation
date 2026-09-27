// RTMPE SDK — Runtime/Core/WorldSpawnElection.cs
//
// Who spawns the world, and when — the decision RtmpeWorldSpawner takes from
// the room's events and asks about once per frame.  No engine type here: a
// shard drives every rule below with a clock of its own.
//
// 🔑 The three cases, from the order the room actually delivers (§8 of
// architecture.md): the join reply first, then the object replay, repeated at
// +300 ms and again ~700 ms later.  A client cannot rely on "the replay has
// arrived by OnRoomJoined", so it cannot ask "is there a world?" at the moment
// it enters — the answer would be no for everyone, and every host would spawn
// one.
//
//   • A host whose roster is ITSELF ALONE spawns at once: nobody else could
//     have spawned a world into a room with one seat in it.
//   • A host who entered with others already seated — matchmaking seats a
//     party together — waits out the replay ladder, then spawns only if no
//     instance of the key has arrived.  A second and a half costs that much
//     empty world in the one entry shape where a world could already exist.
//   • A host PROMOTED after entry never spawns fresh while a world exists:
//     the previous host's world reaches it through ownership reassignment and
//     RtmpeWorldAuthority keeps it — or, against a gateway whose replay does
//     not follow an object's owner, re-creates it under the new host's own id
//     space, state and all.  Only when the grace passes with no instance at all — the
//     previous host never had one, or its spawn was lost — does the promoted
//     host spawn a fresh one, because a room with no world would otherwise
//     have none for the rest of its life.
//   • Everybody else never spawns.
//
// ⛔ Single-shot.  A schedule is spent when it answers, and it answers at most
// once per entry or promotion; the rare race it cannot see — two hosts each
// holding a world — is settled on receipt, by WorldAuthorityRegistry.

namespace RTMPE.Core
{
    /// <summary>
    /// Decides whether this client should spawn the room's world object now,
    /// from the room's entry and host-change events and a clock.
    /// </summary>
    internal sealed class WorldSpawnElection
    {
        /// <summary>
        /// How long a host that did not enter alone waits for the object
        /// replay before concluding that no world exists, in milliseconds.
        /// The Room Service re-replays the objects at +300 ms and again
        /// ~700 ms after that (<c>catchupReReplayGaps</c> in its spawn-buffer
        /// handler), measured from its first replay — which itself follows the
        /// reply this clock starts on — so the last chance lands a little past
        /// a second after entry; a second and a half is past it on every link
        /// this SDK is deployed over.  Held against that declaration by the
        /// election's tests, because the number is the service's and not this
        /// file's to keep.
        /// </summary>
        internal const long PeerReplayGraceMillis = 1500L;

        private bool _armed;
        private long _dueAtMillis;

        /// <summary>Whether a spawn decision is pending.</summary>
        public bool IsArmed => _armed;

        /// <summary>
        /// The room was entered.  Arms a decision when this client is the
        /// host: for now when the roster is this client alone, for
        /// <see cref="PeerReplayGraceMillis"/> from now otherwise.
        /// </summary>
        public void RoomEntered(bool isHost, int playerCount, long nowMillis)
        {
            if (!isHost)
            {
                Disarm();
                return;
            }
            Arm(playerCount <= 1 ? nowMillis : nowMillis + PeerReplayGraceMillis);
        }

        /// <summary>
        /// The room's host changed.  A promotion arms a grace-delayed decision
        /// unless one is already pending; losing the host role cancels any
        /// pending decision.
        /// </summary>
        public void HostChanged(bool nowHost, long nowMillis)
        {
            if (!nowHost)
            {
                Disarm();
                return;
            }
            if (_armed) return;
            Arm(nowMillis + PeerReplayGraceMillis);
        }

        /// <summary>The room was left, or the session ended: nothing is pending.</summary>
        public void RoomLeft() => Disarm();

        /// <summary>
        /// Whether a fresh world should be spawned now.  Spends the pending
        /// decision when it is due, and cancels it when this client is no
        /// longer the host.
        /// </summary>
        /// <param name="stillHost">Whether this client is the host at the time of asking.</param>
        /// <param name="worldExists">Whether any instance of the world key is held, ready or not.</param>
        /// <param name="nowMillis">The clock.</param>
        public bool ShouldSpawn(bool stillHost, bool worldExists, long nowMillis)
        {
            if (!_armed) return false;
            if (!stillHost)
            {
                Disarm();
                return false;
            }
            if (nowMillis < _dueAtMillis) return false;
            Disarm();
            return !worldExists;
        }

        private void Arm(long dueAtMillis)
        {
            _armed = true;
            _dueAtMillis = dueAtMillis;
        }

        private void Disarm()
        {
            _armed = false;
            _dueAtMillis = 0L;
        }
    }
}
