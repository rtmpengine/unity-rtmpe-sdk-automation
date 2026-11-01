// RTMPE SDK — Runtime/Rooms/PendingPropertyWrites.cs
//
// The book-keeping behind "did my property write land?".
//
// The caller checks the accepted delta against PropertyWriteFlights before
// resolving a watch: another writer can take the same version. A conflict resync
// is not confirmation, and retrying it must not renew the original deadline.
// A waiting write gets a fresh deadline only after its predecessor is resolved
// or expired.

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
        /// broadcast by <paramref name="deadlineTicks"/>. An existing watch keeps
        /// its deadline because only one write per subject is in flight.
        /// </summary>
        /// <returns>
        /// False when the table is full and this subject is not already in it —
        /// the write is still sent, it is simply not watched.
        /// </returns>
        internal bool Arm(string subject, int expectedVersion, long deadlineTicks)
            => _acks.Arm(subject, subject, expectedVersion, deadlineTicks, preserveDeadline: true);

        /// <summary>
        /// A broadcast already matched to the flight for <paramref name="subject"/>
        /// arrived at <paramref name="version"/>. Clears the corresponding watch.
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
