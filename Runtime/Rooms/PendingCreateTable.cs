// RTMPE SDK — Runtime/Rooms/PendingCreateTable.cs
//
// In-flight CreateRoom request bookkeeping for RoomManager.  Carved out of
// RoomManager into its own type so the correlator's invariants (id-based
// matching with FIFO fallback, capacity cap, TTL-bounded growth) can be
// unit-tested without spinning up the full RoomManager surface (which
// depends on UnityEngine, PacketBuilder, RoomPacketBuilder, ...).
//
// Threading: every method must be invoked from the Unity main thread.
// RoomManager already enforces that contract; the table itself is
// not thread-safe by design.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Tracks requests awaiting a reply, so that each reply is matched to the request it
    /// answers and a request that is never answered expires. Used by
    /// <see cref="RoomManager"/> for room creation; not intended to be called from game code.
    /// </summary>
    /// <remarks>
    /// Call it from the main thread only. Times are <see cref="NowTicks"/> readings from a
    /// monotonic clock, so a change to the system clock does not affect expiry.
    /// </remarks>
    /// <typeparam name="T">The data kept with each request.</typeparam>
    public sealed class PendingCreateTable<T>
    {
        private readonly int  _capacity;
        private readonly long _ttlTicks;

        private readonly Dictionary<Guid, Entry> _byId    = new Dictionary<Guid, Entry>();
        private readonly Queue<Guid>             _byOrder = new Queue<Guid>();

        private readonly struct Entry
        {
            public readonly Guid Id;
            public readonly T    Payload;
            public readonly long SentAtTicks; // Stopwatch.GetTimestamp()
            public Entry(Guid id, T payload, long sentAtTicks)
            {
                Id          = id;
                Payload     = payload;
                SentAtTicks = sentAtTicks;
            }
        }

        /// <summary>
        /// Creates a table that holds at most <paramref name="capacity"/> requests, each for at
        /// most <paramref name="ttlSeconds"/> seconds.
        /// </summary>
        /// <param name="capacity">The most requests awaiting a reply at once.</param>
        /// <param name="ttlSeconds">How long a request may wait for its reply.</param>
        /// <exception cref="ArgumentOutOfRangeException">An argument is zero or
        /// negative.</exception>
        public PendingCreateTable(int capacity, double ttlSeconds)
        {
            if (capacity   <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (ttlSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(ttlSeconds));
            _capacity = capacity;
            _ttlTicks = (long)(ttlSeconds * Stopwatch.Frequency);
        }

        /// <summary>The number of requests awaiting a reply.</summary>
        public int  Count  => _byId.Count;
        /// <summary>Whether the table holds as many requests as its capacity.</summary>
        public bool IsFull => _byId.Count >= _capacity;

        /// <summary>
        /// Returns the current reading of the monotonic clock the table uses. Pass it to
        /// <see cref="Register"/> and <see cref="SweepExpired"/>.
        /// </summary>
        public static long NowTicks() => Stopwatch.GetTimestamp();

        /// <summary>
        /// Records a request and returns the new id that identifies it. Check
        /// <see cref="IsFull"/> first.
        /// </summary>
        /// <param name="payload">The data to keep with the request.</param>
        /// <param name="sentAtTicks">When the request was sent, from <see cref="NowTicks"/>.</param>
        /// <exception cref="InvalidOperationException">The table is full.</exception>
        public Guid Register(T payload, long sentAtTicks)
        {
            if (IsFull)
                throw new InvalidOperationException(
                    "PendingCreateTable: capacity exceeded — call IsFull first.");

            var id = Guid.NewGuid();
            _byId[id] = new Entry(id, payload, sentAtTicks);
            _byOrder.Enqueue(id);
            return id;
        }

        /// <summary>
        /// Finds and removes the request a reply answers: the request with
        /// <paramref name="echoedRequestId"/> when the reply names one, otherwise the oldest
        /// request.
        /// </summary>
        /// <param name="echoedRequestId">The request id the reply names, or
        /// <see langword="null"/>.</param>
        /// <param name="payload">The data kept with the matched request.</param>
        /// <returns>
        /// <see langword="false"/> when the reply answers no request in the table, including a
        /// reply that names an id the table does not hold.
        /// </returns>
        public bool TryMatch(Guid? echoedRequestId, out T payload)
        {
            if (echoedRequestId.HasValue)
            {
                // Echoed-id path is strict: hit-or-miss.  Falling through to
                // FIFO when the id is unknown would let a duplicate response
                // (whose original was already matched) silently consume some
                // OTHER pending request's options — exactly the cross-talk
                // class this correlator was introduced to eliminate.  Treat
                // unknown echoed ids as spurious.
                if (_byId.TryGetValue(echoedRequestId.Value, out var byIdEntry))
                {
                    _byId.Remove(echoedRequestId.Value);
                    RemoveFromOrder(echoedRequestId.Value);
                    payload = byIdEntry.Payload;
                    return true;
                }
                payload = default;
                return false;
            }

            while (_byOrder.Count > 0)
            {
                var headId = _byOrder.Dequeue();
                if (_byId.TryGetValue(headId, out var entry))
                {
                    _byId.Remove(headId);
                    payload = entry.Payload;
                    return true;
                }
                // headId was already removed by id-match above — keep draining.
            }

            payload = default;
            return false;
        }

        /// <summary>
        /// Removes every request that has waited longer than the table's time limit, and returns
        /// them.
        /// </summary>
        /// <remarks>
        /// The requests are removed before the list is returned, so the caller may use the
        /// table while it handles them. A sweep that finds nothing allocates nothing.
        /// </remarks>
        /// <param name="nowTicks">The current time, from <see cref="NowTicks"/>.</param>
        /// <returns>The id and data of each expired request.</returns>
        public IReadOnlyList<(Guid id, T payload)> SweepExpired(long nowTicks)
        {
            if (_byId.Count == 0) return Nothing;

            long cutoff = nowTicks - _ttlTicks;
            List<Guid> stale = null;
            foreach (var kv in _byId)
            {
                if (kv.Value.SentAtTicks <= cutoff)
                    (stale ??= new List<Guid>()).Add(kv.Key);
            }
            if (stale == null) return Nothing;

            var swept = new List<(Guid id, T payload)>(stale.Count);
            foreach (var id in stale)
            {
                T payload = _byId[id].Payload;
                _byId.Remove(id);
                RemoveFromOrder(id);
                swept.Add((id, payload));
            }
            return swept;
        }

        private static readonly (Guid id, T payload)[] Nothing =
            new (Guid id, T payload)[0];

        /// <summary>Removes every request.</summary>
        public void Clear()
        {
            _byId.Clear();
            _byOrder.Clear();
        }

        private void RemoveFromOrder(Guid id)
        {
            // Linear scan is acceptable: queue length ≤ _capacity (default 16).
            int n = _byOrder.Count;
            for (int i = 0; i < n; i++)
            {
                var entry = _byOrder.Dequeue();
                if (entry != id) _byOrder.Enqueue(entry);
            }
        }
    }
}
