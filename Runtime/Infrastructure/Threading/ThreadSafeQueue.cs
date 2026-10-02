// RTMPE SDK — Runtime/Infrastructure/Threading/ThreadSafeQueue.cs
//
// Lock-free FIFO queue for cross-thread producer/consumer scenarios.
//
// Why ConcurrentQueue<T> instead of Queue<T> + lock?
//  ConcurrentQueue uses CAS (compare-and-swap) internally — no OS mutex is
//  acquired on the hot path. On .NET Standard 2.1 / Unity IL2CPP this eliminates
//  lock contention and priority inversion between the network background thread
//  and the Unity main thread, which is critical for sub-30 ms P99 latency.
//
// .NET Standard 2.1 note:
//  ConcurrentQueue<T>.Clear() was added in .NET 5 and is NOT available on
//  .NET Standard 2.1 / Unity IL2CPP. Use the drain-loop pattern (see Clear()).

using System.Collections.Concurrent;

namespace RTMPE.Threading
{
    /// <summary>
    /// A lock-free first-in, first-out queue backed by
    /// <see cref="ConcurrentQueue{T}"/>. Any number of threads may call
    /// <see cref="Enqueue"/> and <see cref="TryDequeue"/> at the same time.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    public sealed class ThreadSafeQueue<T>
    {
        private readonly ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();

        /// <summary>Returns <see langword="true"/> when the queue contains no items.</summary>
        public bool IsEmpty => _queue.IsEmpty;

        /// <summary>
        /// Number of items in the queue. Approximate: other threads can change
        /// the queue at any moment, so use it for diagnostics or soft limits.
        /// </summary>
        public int Count => _queue.Count;

        /// <summary>
        /// Adds <paramref name="item"/> to the end of the queue.
        /// </summary>
        /// <param name="item">The item to add.</param>
        public void Enqueue(T item) => _queue.Enqueue(item);

        /// <summary>
        /// Removes the item at the front of the queue. Returns
        /// <see langword="false"/>, with <paramref name="item"/> set to its
        /// default value, when the queue is empty.
        /// </summary>
        /// <param name="item">Receives the removed item.</param>
        public bool TryDequeue(out T item) => _queue.TryDequeue(out item);

        /// <summary>
        /// Removes every item in the queue.
        /// </summary>
        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
