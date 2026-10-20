// RTMPE SDK — Runtime/Core/RoomCatchUpWindow.cs
//
// The window after a room entry in which the room's live objects reach this
// client.  The Room Service replays them to a joiner after the join reply and
// re-sends the bundle at +300 ms and ~700 ms later (catchupReReplayGaps in its
// spawn-buffer handler), so the last copy lands a little past a second after
// entry — the reach the world election already waits out.
//
// 🔑 Inside the window an inbound Spawn is admitted on the per-room count cap
// alone.  The per-second rate cap bounds a SUSTAINED hostile stream and
// discards what it refuses, and the replay is the one burst a client is handed
// that it can never ask for again: metered against the cap, a joiner entering
// a room of three hundred objects kept a hundred of them and never learned of
// the rest.  The staged release — the same set, arriving ahead of the join
// reply — has always been exempt on those terms; this is the same set arriving
// behind it, which is the order the room actually delivers.
//
// 🔑 Measured on arrivals, not on the frame a packet is drained in.  The
// window opens when the join reply ARRIVED and asks when a spawn ARRIVED, so a
// main thread stalled across the burst — a synchronous scene load in the join
// handler is the ordinary shape — drains the whole replay afterwards and still
// finds it inside the window.  SpawnManager reads each arrival off the
// dispatcher's stamp on the item it is executing.
//
// No engine type here: a shard drives it with a clock of its own.

namespace RTMPE.Core
{
    /// <summary>
    /// Whether a moment lies inside the window after a room entry in which
    /// the room's catch-up replay can still arrive.
    /// </summary>
    internal sealed class RoomCatchUpWindow
    {
        /// <summary>
        /// How long after a room entry the replay ladder can still deliver,
        /// in milliseconds — the reach
        /// <see cref="WorldSpawnElection.PeerReplayGraceMillis"/> derives from
        /// the Room Service's declaration, so the two never disagree about
        /// where the ladder ends.
        /// </summary>
        internal const long LengthMillis = WorldSpawnElection.PeerReplayGraceMillis;

        private bool _open;
        private long _closesAtMillis;

        /// <summary>The join reply arrived at <paramref name="enteredMillis"/>: the window runs from there.</summary>
        public void Open(long enteredMillis)
        {
            _open           = true;
            _closesAtMillis = enteredMillis + LengthMillis;
        }

        /// <summary>The room was left, or the session ended: nothing is expected.</summary>
        public void Close() => _open = false;

        /// <summary>Whether <paramref name="atMillis"/> — a spawn's arrival — lies inside the open window.</summary>
        public bool IsOpen(long atMillis)
            => _open && atMillis < _closesAtMillis;
    }
}
