// RTMPE SDK — Runtime/Core/Diagnostics/DiagnosticsPreSessionBuffer.cs
//
// Pure managed bounded buffer for log entries captured before a network
// session is established.  No Unity API dependencies — fully unit-testable.
//
// Threading: Enqueue is called from any thread (Unity's log callback is
// thread-agnostic), DrainInto and Clear are called from the main thread
// only.  The concurrent queue and the Interlocked counter are the only
// synchronisation primitives; no locks are acquired.

using System.Collections.Concurrent;
using System.Threading;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Bounded concurrent queue for log entries captured before the network
    /// session is established.  When the capacity is reached the oldest entry
    /// is evicted before the new one is inserted (drop-oldest).
    /// </summary>
    internal sealed class DiagnosticsPreSessionBuffer
    {
        // 256 entries: generous enough to survive a noisy multi-retry handshake
        // failure without growing unboundedly on a crash-flood path.
        private const int Capacity = 256;

        private readonly ConcurrentQueue<LogEntry> _queue =
            new ConcurrentQueue<LogEntry>();

        // Approximate count (Interlocked-maintained) used by the capacity guard.
        // ConcurrentQueue.Count is O(n); this counter gives O(1) at the cost of
        // a possible transient one-entry overshoot under concurrent pressure.
        private int _approxCount;

        // Entries the capacity guard evicted and nobody has yet accounted for.
        // Written from whichever thread evicts, taken on the main thread by the
        // promotion, so the packet that carries the promoted entries can name
        // what was lost ahead of them.
        private long _evicted;

        /// <summary>
        /// Current approximate entry count.  Exposed for capacity-overflow tests.
        /// </summary>
        internal int Count => Volatile.Read(ref _approxCount);

        /// <summary>
        /// Enqueue an entry.  When the buffer is at capacity the oldest entry
        /// is dequeued first so the total count stays bounded, and the eviction
        /// is counted for <see cref="TakeEvicted"/>.
        /// </summary>
        internal void Enqueue(byte level, uint tsMs, string msg, string stack)
        {
            if (Volatile.Read(ref _approxCount) >= Capacity)
            {
                if (_queue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _approxCount);
                    Interlocked.Increment(ref _evicted);
                }
            }
            _queue.Enqueue(new LogEntry(level, tsMs, msg, stack));
            Interlocked.Increment(ref _approxCount);
        }

        /// <summary>
        /// The number of entries evicted since the last call, and zero from
        /// then until the next eviction.  Main thread only.
        /// </summary>
        internal long TakeEvicted() => Interlocked.Exchange(ref _evicted, 0L);

        /// <summary>
        /// Drain all buffered entries to the caller via a receive delegate.
        /// Call this on the main thread when the session establishes so the
        /// caller can promote the entries into its own post-session queue.
        /// </summary>
        internal void DrainInto(System.Action<byte, uint, string, string> receive)
        {
            while (_queue.TryDequeue(out LogEntry e))
            {
                Interlocked.Decrement(ref _approxCount);
                receive(e.Level, e.TsMs, e.Msg, e.Stack);
            }
        }

        /// <summary>
        /// Discard all buffered entries.  Called when a connection attempt
        /// fails before the session is established so stale pre-session logs
        /// are not carried into the next attempt's uplink.
        /// </summary>
        internal void Clear()
        {
            while (_queue.TryDequeue(out _))
                Interlocked.Decrement(ref _approxCount);
            // An attempt that never reached a session sends nothing, so what it
            // evicted is not a gap in any stream a notice could open.
            Interlocked.Exchange(ref _evicted, 0L);
        }

        private readonly struct LogEntry
        {
            public readonly byte   Level;
            public readonly uint   TsMs;
            public readonly string Msg;
            public readonly string Stack;

            public LogEntry(byte level, uint tsMs, string msg, string stack)
            {
                Level = level;
                TsMs  = tsMs;
                Msg   = msg;
                Stack = stack;
            }
        }
    }
}
