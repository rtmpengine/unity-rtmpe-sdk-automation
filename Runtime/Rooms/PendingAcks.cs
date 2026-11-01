// RTMPE SDK — Runtime/Rooms/PendingAcks.cs
//
// The book-keeping behind every "did the server act on that?" watch.
//
// Some refusals draw no reply, and a property conflict draws a resync rather
// than confirmation. A deadline reports the absence of a qualifying broadcast
// without claiming whether the request committed.
//
// 🔑 That is the shape of ROOM-RD-03 and ROOM-RD-07 — a control operation that
// draws no answer is never abandoned, and the caller is never told — and what it
// takes to hold is the same every time it appears: a deadline recorded per
// subject, swept from the per-frame driver rather than from the next call on the
// same path, bounded so a client that keeps issuing requests cannot grow it
// without limit, and handed out as a snapshot the caller may iterate while
// application code runs inside its own loop.
//
// The caller decides which broadcasts qualify: a property write requires its
// version and delta, a host command its target. This table holds the deadline
// and the caller's gate, not the evidence that an answer belongs to a request.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Requests awaiting the broadcast that would prove they landed, keyed by
    /// subject and reported as <typeparamref name="TReport"/> when their
    /// deadline passes.
    /// </summary>
    internal sealed class PendingAcks<TReport>
    {
        private readonly struct Entry
        {
            public readonly TReport Report;
            public readonly int     Gate;
            public readonly long    DeadlineTicks;

            public Entry(TReport report, int gate, long deadlineTicks)
            {
                Report        = report;
                Gate          = gate;
                DeadlineTicks = deadlineTicks;
            }
        }

        private readonly int _maxSubjects;

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>();

        // Reused across sweeps so a per-frame driver allocates nothing while
        // there is nothing to report — the cost ROOM-RD-03's closure had to fix
        // at the table rather than guard at the call site.
        //
        // ⚠️ The scratch is NEVER handed out. The caller's whole purpose is to
        // report each expiry to application code, and an application handler may
        // issue another request, leave the room, or drive another frame — so a
        // returned buffer that the next sweep clears and refills is a collection
        // mutated under the caller's own loop. That is the same hazard as the
        // create table's yielding iterator, one indirection further out, and it
        // is why what leaves here is a snapshot. It costs one array on the rare
        // frame that has something to report, and nothing at all on the others.
        private readonly List<string> _expiredScratch = new List<string>();
        private static readonly TReport[] Nothing = new TReport[0];

        /// <param name="maxSubjects">
        /// Ceiling on tracked subjects. ⛔ A non-positive ceiling is read as
        /// *unbounded*, never as *refuse everything*: a bound that fails that
        /// closed stops being a bound and becomes an outage of the diagnostic it
        /// exists to keep affordable.
        /// </param>
        internal PendingAcks(int maxSubjects)
        {
            _maxSubjects = maxSubjects > 0 ? maxSubjects : int.MaxValue;
        }

        internal int Count => _entries.Count;

        /// <summary>
        /// Record that a request on <paramref name="subject"/> has been sent and
        /// is owed an answer of at least <paramref name="gate"/> by
        /// <paramref name="deadlineTicks"/>. With <paramref name="preserveDeadline"/>,
        /// an existing request retains its deadline while its gate is updated.
        /// </summary>
        /// <returns>
        /// False when the table is full and this subject is not already in it —
        /// the request is still sent, it is simply not watched.
        /// </returns>
        internal bool Arm(string subject, TReport report, int gate, long deadlineTicks, bool preserveDeadline = false)
        {
            if (subject == null) return false;
            bool held = _entries.TryGetValue(subject, out var previous);
            if (!held && _entries.Count >= _maxSubjects) return false;

            // A retry must not extend the time allowed for the original request.
            if (held && preserveDeadline) deadlineTicks = previous.DeadlineTicks;
            _entries[subject] = new Entry(report, gate, deadlineTicks);
            return true;
        }

        /// <summary>
        /// An answer for <paramref name="subject"/> arrived at
        /// <paramref name="observed"/>. Clears the entry when that is at or past
        /// the gate the request declared.
        /// </summary>
        internal void Resolve(string subject, int observed)
        {
            if (subject == null) return;
            if (!_entries.TryGetValue(subject, out var e)) return;
            if (observed >= e.Gate) _entries.Remove(subject);
        }

        /// <summary>Forget everything — a room change, or a disconnect.</summary>
        internal void Clear() => _entries.Clear();

        /// <summary>
        /// The reports of every subject whose deadline has passed with no
        /// qualifying answer, removed from the table as they are returned.
        /// </summary>
        /// <remarks>
        /// ⚠️ Eager, not an iterator. `SweepExpired` on the create table was
        /// written as a C# iterator that removed from its own dictionary and
        /// yielded between two removals — and its documented purpose is to hand
        /// the result to observers, so application code ran in that window and a
        /// handler that cleared or re-swept made the next `MoveNext` throw. This
        /// one collects first and mutates once.
        /// </remarks>
        internal IReadOnlyList<TReport> SweepExpired(long nowTicks)
        {
            if (_entries.Count == 0) return Nothing;

            _expiredScratch.Clear();
            foreach (var kv in _entries)
                if (nowTicks >= kv.Value.DeadlineTicks) _expiredScratch.Add(kv.Key);

            if (_expiredScratch.Count == 0) return Nothing;

            var reports = new TReport[_expiredScratch.Count];
            for (int i = 0; i < _expiredScratch.Count; i++)
            {
                reports[i] = _entries[_expiredScratch[i]].Report;
                _entries.Remove(_expiredScratch[i]);
            }
            return reports;
        }
    }
}
