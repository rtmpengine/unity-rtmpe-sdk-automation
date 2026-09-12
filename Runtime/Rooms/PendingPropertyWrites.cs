// RTMPE SDK — Runtime/Rooms/PendingPropertyWrites.cs
//
// The book-keeping behind "did my property write land?".
//
// A property write is accepted by the server only at exactly the version it
// declares, and a conflict is answered with NO PACKET: the Room Service returns
// an error, publishes nothing, and the client hears nothing at all.  The only
// evidence a write landed is the broadcast that follows it, so the only way to
// learn that one did NOT is to notice a broadcast that never came.
//
// The mechanism that holds that — the deadline, the bound, the eager sweep and
// the snapshot handed to observers — is stated once in `PendingAcks`, which this
// type names the property-write half of.  What belongs here is the question a
// subject is waiting on.
//
// One subject at a time per key: the room's property map is one document, and a
// player's is one document per seat.  A second write to the same subject before
// the first resolves supersedes it, because the second carries the later version
// and it is the later version whose arrival settles both.
//
// ⛔ Resolution is `>=`, not `==`.  A broadcast carrying a version PAST the one
// we declared means somebody else's write was accepted after ours — our write
// may have landed and been superseded, or may have lost the race outright, and
// this table cannot tell those apart.  Neither can the caller, which is why the
// contract is "the version moved, read it" rather than "your write landed": a
// wrong success is worse than an ambiguous one, and reporting a failure for a
// write that did land would be exactly that.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Property writes awaiting the broadcast that would prove they landed.
    /// </summary>
    internal sealed class PendingPropertyWrites
    {
        /// <summary>
        /// The subject key for a room-level write. A player's own id is the key
        /// for a player-level one, and no player id is empty, so the two spaces
        /// cannot collide.
        /// </summary>
        internal const string RoomSubject = "";

        /// <summary>
        /// Ceiling on tracked subjects. A room holds one entry and each seat at
        /// most one more, so a full room is bounded by its capacity.
        ///
        /// <para>The cap is what bounds the residue: the caller's own seat is
        /// the only player id a write can name, but that check stands down while
        /// this client has no seat of its own to compare against, and a
        /// long-lived pre-seat session writing to many ids would otherwise grow
        /// this without limit. ⛔ Reaching it costs a WATCH, never a WRITE — the
        /// packet still goes, it is simply not waited on, because a bound that
        /// dropped the write would turn a diagnostic into an outage.</para>
        /// </summary>
        internal const int MaxSubjects = 128;

        // The report is the subject itself: the caller distinguishes the room
        // from a seat by comparing against RoomSubject, and names the seat in
        // what it reports.
        private readonly PendingAcks<string> _acks =
            new PendingAcks<string>(MaxSubjects);

        internal int Count => _acks.Count;

        /// <summary>
        /// Record that a write to <paramref name="subject"/> declaring
        /// <paramref name="expectedVersion"/> has been sent, and is owed a
        /// broadcast by <paramref name="deadlineTicks"/>.
        /// </summary>
        /// <returns>
        /// False when the table is full and this subject is not already in it —
        /// the write is still sent, it is simply not watched.
        /// </returns>
        internal bool Arm(string subject, int expectedVersion, long deadlineTicks)
            => _acks.Arm(subject, subject, expectedVersion, deadlineTicks);

        /// <summary>
        /// A broadcast for <paramref name="subject"/> arrived at
        /// <paramref name="version"/>. Clears the entry when that version is at
        /// or past the one awaited.
        /// </summary>
        internal void Resolve(string subject, int version) => _acks.Resolve(subject, version);

        /// <summary>Forget everything — a room change, or a disconnect.</summary>
        internal void Clear() => _acks.Clear();

        /// <summary>
        /// The subjects whose deadline has passed with no qualifying broadcast,
        /// removed from the table as they are returned.
        /// </summary>
        internal IReadOnlyList<string> SweepExpired(long nowTicks) => _acks.SweepExpired(nowTicks);
    }
}
