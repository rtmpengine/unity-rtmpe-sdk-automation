// RTMPE SDK — Runtime/Rooms/RoomEntrySequence.cs
//
// One counter over every way into a room — a join armed, a create registered,
// a matchmaking request sent — so that any two entries this client begins are
// ordered, whichever manager began them.
//
// A catch-up frame carries an object id and no room, so the only thing that
// says which entry it arrived for is when it arrived: after some entries were
// begun and before others.  The receive path stages a frame under the entry
// begun most recently, and the transition into a room says which entry it
// completes; a frame staged under an EARLIER entry belongs to one that ended
// without a transition, and a frame staged under that entry or a later one
// arrived while this entry was the one the server could be answering.  That
// ordering is only meaningful across managers if the values come from one
// counter, which is what this class is.

namespace RTMPE.Rooms
{
    /// <summary>
    /// A monotonic sequence over the room entries this client begins.  Owned
    /// by <c>NetworkManager</c> for the life of the component and shared by
    /// the managers that begin entries, so a value from one is ordered against
    /// a value from the other and no value is ever issued twice.
    /// </summary>
    internal sealed class RoomEntrySequence
    {
        /// <summary>
        /// Ordered below every entry: the value a frame could carry before any
        /// entry was begun, and the value that adopts everything staged.
        /// </summary>
        public const int None = 0;

        /// <summary>The entry begun most recently, or <see cref="None"/>.</summary>
        public int Latest { get; private set; } = None;

        /// <summary>Begin an entry and return its value, above every earlier one.</summary>
        public int Begin() => ++Latest;
    }
}
