// RTMPE SDK — Runtime/Infrastructure/Threading/MainThreadDispatcher.cs
//
// Bridges the gap between the RTMPE background network thread and Unity's main thread.
// Unity APIs (Debug.Log, MonoBehaviour callbacks, scene queries) are only safe to call
// from the main thread. This dispatcher queues lambdas on the network thread and
// drains them inside Unity's Update() loop.
//
// Usage from any thread:
//  MainThreadDispatcher.Instance.Enqueue(() => { /* any Unity-safe code */ });
//
// UNITY MAIN THREAD RULE: MainThreadDispatcher.Instance must be accessed
// from the main thread only — it may create a new GameObject on first call,
// and AddComponent/DontDestroyOnLoad are not safe off-main-thread.  To guard
// against a misuse where a background thread is the first to touch the
// singleton, the main-thread id is captured at static init via
// RuntimeInitializeOnLoadMethod.  An off-main-thread access throws
// InvalidOperationException with a clear message instead of letting Unity
// surface a confusing UnityException later inside AddComponent.
//
// Pre-warming: callers that want to be explicit can call Prewarm() during
// their own Awake/Start to materialise the singleton before any background
// thread has reason to touch it.

using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace RTMPE.Threading
{
    /// <summary>
    /// What <see cref="MainThreadDispatcher"/> does with an action queued while
    /// <see cref="MainThreadDispatcher.MaxQueueDepth"/> actions are already
    /// waiting.
    /// </summary>
    public enum DispatcherFullPolicy
    {
        /// <summary>The new action is dropped (default). Actions already queued keep their order.</summary>
        DropTail,

        /// <summary>The oldest waiting action is dropped and the new one is queued.</summary>
        DropHead,

        /// <summary>The call that queues the action throws <see cref="InvalidOperationException"/>.</summary>
        Throw,
    }

    /// <summary>
    /// Runs work on the Unity main thread. Actions queued from any thread run
    /// in order, from this component's <c>Update</c>, at most 200 per frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One instance exists and persists across scene loads. It must be
    /// created on the main thread, through <see cref="Instance"/> or
    /// <see cref="Prewarm"/>; <see cref="Core.NetworkManager"/> creates it at
    /// start-up.
    /// </para>
    /// <para>
    /// When a session ends or a connection attempt fails, the SDK discards
    /// every action still waiting in the queue, including actions your code
    /// queued; they do not run.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-999)]
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        private static MainThreadDispatcher _instance;
        private static readonly object _instLock = new object();

        // Captured on the very first managed-thread to run user code in the
        // Unity domain — i.e. the main thread.  Compared in Instance to detect
        // off-main-thread access before AddComponent/DontDestroyOnLoad blow up.
        private static int _mainThreadId;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            // SubsystemRegistration runs on the main thread before any user
            // script.  Capturing the id here means later off-main-thread
            // accesses can be detected deterministically.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            lock (_instLock) { _instance = null; }
        }

        // ── Queue ──────────────────────────────────────────────────────────────
        // Concurrent queue + explicit Interlocked counter.  ConcurrentQueue.Count
        // is an O(N) walk on some implementations and only an *approximate*
        // snapshot — using it for back-pressure decisions allowed transient
        // over- or under-counts.  An explicit counter gives a precise depth
        // value for the policy check below.
        //
        // The work item carries both an Action (for the legacy zero-arg
        // overload) and an Action<object>+state pair (for the generic
        // Enqueue<TArg> overload).  Carrying both inside one struct keeps
        // a single FIFO for execution order; producers populate exactly
        // one of the two execution shapes.
        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        private int _depth;

        private readonly struct WorkItem
        {
            // One of:
            //   • Action               — legacy zero-arg overload.
            //   • StateAction + State  — generic Enqueue<TArg> overload (boxes
            //                            value-type args once per call; static
            //                            method refs pass through unboxed).
            //   • BufferAction + Buffer + Length — per-packet receive overload
            //                            that avoids any per-call allocation
            //                            by carrying the byte[]+int pair
            //                            inline in the queue node.
            public readonly Action Action;
            public readonly Action<object> StateAction;
            public readonly object State;
            public readonly Action<byte[], int> BufferAction;
            public readonly byte[] Buffer;
            public readonly int Length;
            // A received packet whose loss a later packet repairs, which gives
            // way to the rest of the queue when the queue fills (see
            // MaxSheddableDepth).
            public readonly bool Sheddable;
            // When the item was handed in, on the Stopwatch clock: what a
            // consumer that measures a window against arrivals rather than
            // against the frame it happens to be drained in reads through
            // ExecutingItemAgeMillis.
            public readonly long EnqueuedTicks;

            public WorkItem(Action action)
            {
                Action        = action;
                StateAction   = null;
                State         = null;
                BufferAction  = null;
                Buffer        = null;
                Length        = 0;
                Sheddable     = false;
                EnqueuedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            public WorkItem(Action<object> stateAction, object state)
            {
                Action        = null;
                StateAction   = stateAction;
                State         = state;
                BufferAction  = null;
                Buffer        = null;
                Length        = 0;
                Sheddable     = false;
                EnqueuedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            public WorkItem(Action<byte[], int> bufferAction, byte[] buffer, int length, bool sheddable)
            {
                Action        = null;
                StateAction   = null;
                State         = null;
                BufferAction  = bufferAction;
                Buffer        = buffer;
                Length        = length;
                Sheddable     = sheddable;
                EnqueuedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            public void Invoke()
            {
                if (Action != null)             Action();
                else if (BufferAction != null)  BufferAction(Buffer, Length);
                else if (StateAction != null)   StateAction(State);
            }
        }

        // Serialises the read-check → optional-dequeue → enqueue → increment
        // sequence so that concurrent producers cannot race past the cap.
        // Update()'s drain path (TryDequeue + Interlocked.Decrement) never
        // takes this lock, so there is no contention on the hot read path.
        private readonly object _enqueueLock = new object();

        /// <summary>
        /// What happens to an action queued while the queue is full. Defaults
        /// to <see cref="DispatcherFullPolicy.DropTail"/>. Set it on the main
        /// thread before the dispatcher is in use.
        /// </summary>
        public DispatcherFullPolicy FullPolicy { get; set; } = DispatcherFullPolicy.DropTail;

        // Limit callbacks executed per frame to bound worst-case stall time.
        // 200 × ~1 µs = ~200 µs — well inside a 33 ms frame budget at 30 Hz.
        private const int MaxActionsPerFrame = 200;

        /// <summary>
        /// The most actions that can wait in the queue (10000);
        /// <see cref="FullPolicy"/> decides what happens to one more.
        /// </summary>
        public const int MaxQueueDepth = 10_000;

        /// <summary>
        /// The most sheddable packets that can wait at once (8000): the rest of
        /// <see cref="MaxQueueDepth"/> is kept for everything else.
        /// </summary>
        /// <remarks>
        /// A packet is queued as sheddable when what it carries is usually sent
        /// again — a state frame, a variable update — and one more of them
        /// arriving with this many already waiting is dropped rather than queued.
        /// Without the reserve a flood of them filled the whole queue, and the
        /// queue then dropped whatever arrived next, a spawn, an RPC or a room
        /// reply as readily as the flood (audit P2-H2). With it, the rest of the
        /// queue keeps two thousand places when the flood has taken all of its
        /// own — fewer frames than that, because the server sends a spawn, an RPC
        /// or a reply three times and the copies are dropped only once they run.
        /// ⚠️ The packets dropped are the newest of the flood, not its oldest: the
        /// queue cannot take an item out of its middle. And a variable update
        /// dropped here leaves its variable stale until it is written again,
        /// which is the price of keeping the places the rest needs.
        /// </remarks>
        internal const int MaxSheddableDepth = MaxQueueDepth - 2_000;

        // How many sheddable packets are waiting.  Raised under _enqueueLock with
        // the item it counts, and lowered wherever an item leaves the queue — the
        // drain, a DropHead eviction and a discard — so it never counts an item
        // that is gone.
        private int _sheddableDepth;

        // Sheddable packets dropped because MaxSheddableDepth of them were
        // already waiting.  Also counted in _droppedRentedPacketCount: they are
        // received packets the queue did not take.
        private long _shedPacketCount;

        /// <summary>
        /// Number of sheddable packets dropped because
        /// <see cref="MaxSheddableDepth"/> of them were already waiting. Each is
        /// also counted in <see cref="DroppedRentedPacketCount"/>. Not intended to
        /// be called from game code.
        /// </summary>
        internal long ShedPacketCount => Interlocked.Read(ref _shedPacketCount);

        /// <summary>How many sheddable packets are waiting to run.</summary>
        internal int SheddableDepth => Volatile.Read(ref _sheddableDepth);

        // Track overflow events so operators can detect producer/consumer mismatch
        // without spamming the log.  We log the FIRST overflow and then every
        // power-of-two-th overflow (1, 2, 4, 8, 16, …) to retain visibility of
        // ongoing degradation without flooding the console at ~60 FPS.
        private long _overflowCount;

        // Counts buffer-pair Enqueue calls rejected by backpressure.  The
        // caller of that overload owns a pool rental that must be returned
        // when the dispatch never runs, so a separate counter (rather than a
        // share of _overflowCount) lets operators size the receive pool
        // against the precise drop rate of rented packets.
        private long _droppedRentedPacketCount;

        /// <summary>
        /// Number of actions dropped or refused because the queue was full,
        /// under any <see cref="FullPolicy"/>, since the dispatcher was created.
        /// </summary>
        public long OverflowCount => Interlocked.Read(ref _overflowCount);

        /// <summary>
        /// Number of received packets dropped because the queue was full: a
        /// new packet under <see cref="DispatcherFullPolicy.DropTail"/> or
        /// <see cref="DispatcherFullPolicy.Throw"/>, a queued one evicted
        /// under <see cref="DispatcherFullPolicy.DropHead"/>, or a sheddable one
        /// refused at <see cref="MaxSheddableDepth"/>. Not intended to be
        /// called from game code.
        /// </summary>
        public long DroppedRentedPacketCount => Interlocked.Read(ref _droppedRentedPacketCount);

        /// <summary>
        /// Raised when <see cref="DroppedRentedPacketCount"/> reaches 1, 2, 4, 8
        /// and each later power of two; the argument is the new count. Not
        /// intended to be used from game code.
        /// </summary>
        /// <remarks>
        /// Raised on the thread whose enqueue found the queue full (usually
        /// the network thread, not necessarily the main thread), and in some
        /// cases while the dispatcher holds its internal lock. A handler must
        /// be thread-safe, must not call Unity APIs and must not queue work on
        /// the dispatcher. An exception thrown by a handler is caught and
        /// logged.
        /// </remarks>
        public event System.Action<long> OnRentedPacketDropped;

        // Hook used to return a rented buffer to its origin pool when the
        // DropHead policy evicts an in-flight work item.  The original
        // network-thread caller already saw a true return for that item, so
        // it will never call Return itself — without this hook the rental
        // leaks for the lifetime of the process under sustained backpressure.
        // Defaults to the shared ArrayPool to match production callers; tests
        // override it to assert the eviction path returns through a known sink.
        private Action<byte[]> _bufferReturnHandler =
            static b => { try { System.Buffers.ArrayPool<byte>.Shared.Return(b, clearArray: true); } catch { /* foreign array; pool may reject */ } };

        /// <summary>
        /// Receives the buffer of a queued packet that will not reach its
        /// action normally: evicted under
        /// <see cref="DispatcherFullPolicy.DropHead"/>, discarded with the
        /// queue, or left behind when its action threw. The handler owns the
        /// buffer from then on. Defaults to returning it to
        /// <see cref="System.Buffers.ArrayPool{T}.Shared"/>; setting
        /// <see langword="null"/> installs a handler that does nothing. Not
        /// intended to be called from game code.
        /// </summary>
        /// <remarks>
        /// Called outside the dispatcher's internal lock: on the thread whose
        /// enqueue caused an eviction, or on the main thread. The handler must
        /// be thread-safe, and should only return or record the buffer.
        /// </remarks>
        public Action<byte[]> BufferReturnHandler
        {
            get => _bufferReturnHandler;
            set => _bufferReturnHandler = value ?? (static _ => { });
        }

        /// <summary>Number of actions waiting to run.</summary>
        public int Depth => Volatile.Read(ref _depth);

        // The enqueue stamp of the item Update is executing; zero between
        // items.  Main-thread only, like the drain that sets it.
        private long _executingItemEnqueuedTicks;

        /// <summary>
        /// How long ago, in milliseconds, the action now running was queued;
        /// zero when no queued action is running. Read it on the main thread.
        /// Not intended to be called from game code.
        /// </summary>
        /// <remarks>
        /// An action runs in the frame in which the main thread reaches it, not
        /// the frame in which it was queued, so after a stalled frame (for
        /// example a synchronous scene load) this can be seconds.
        /// </remarks>
        public long ExecutingItemAgeMillis
        {
            get
            {
                long enqueued = _executingItemEnqueuedTicks;
                if (enqueued == 0L) return 0L;
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - enqueued;
                return elapsed <= 0L ? 0L : elapsed * 1000L / System.Diagnostics.Stopwatch.Frequency;
            }
        }

        /// <summary>
        /// <see cref="ExecutingItemAgeMillis"/> of the dispatcher in the scene,
        /// or zero when none has been created — a reader on a code path that
        /// is not a dispatched item, or before any dispatcher exists.  Never
        /// creates one.
        /// </summary>
        internal static long CurrentItemAgeMillis
        {
            get
            {
                var instance = Volatile.Read(ref _instance);
                return instance == null ? 0L : instance.ExecutingItemAgeMillis;
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// The dispatcher, created on first access. The access that creates it
        /// must be on the Unity main thread; once it exists, any thread may
        /// read this property.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The dispatcher does not exist yet and the caller is not on the main
        /// thread. Call <see cref="Prewarm"/> from a main-thread <c>Awake</c>
        /// or <c>Start</c> to avoid this.
        /// </exception>
        public static MainThreadDispatcher Instance
        {
            get
            {
                // Hot-path Instance reads must be lock-free — the Awake-driven
                // publication is the only writer in steady state, and the
                // monitor-protected writes below provide the matching release
                // barrier.  Volatile.Read makes the dependent load a true
                // acquire so a background thread sees a fully-constructed
                // instance once Awake has published it.  AddComponent is the
                // only branch that needs synchronisation, and only on the
                // first-ever access before any GameObject exists.
                var cached = Volatile.Read(ref _instance);
                if (cached != null) return cached;

                lock (_instLock)
                {
                    // Re-check inside the lock — another main-thread caller
                    // may have raced us through the fast path and already
                    // constructed the GameObject.
                    if (_instance != null) return _instance;

                    // Refuse to create the GameObject from a background thread.
                    // Without this guard Unity raises UnityException from inside
                    // AddComponent, which is harder to diagnose than a precise
                    // managed exception thrown at the call site.
                    int currentId = Thread.CurrentThread.ManagedThreadId;
                    if (_mainThreadId != 0 && currentId != _mainThreadId)
                    {
                        throw new InvalidOperationException(
                            "MainThreadDispatcher.Instance must be accessed from the Unity main thread. " +
                            $"Current thread id = {currentId}, main thread id = {_mainThreadId}. " +
                            "Call MainThreadDispatcher.Prewarm() from a MonoBehaviour Awake() before any " +
                            "background thread calls Enqueue().");
                    }

                    var go = new GameObject("[RTMPE] MainThreadDispatcher");
                    DontDestroyOnLoad(go);
                    // Awake() runs synchronously inside AddComponent, setting _instance.
                    go.AddComponent<MainThreadDispatcher>();
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Creates the dispatcher if it does not exist yet, and returns it.
        /// Call it on the main thread, for example from <c>Awake</c> or
        /// <c>Start</c>, before other threads use <see cref="Instance"/>.
        /// </summary>
        public static MainThreadDispatcher Prewarm() => Instance;

        /// <summary>
        /// <see langword="true"/> when the caller is on the Unity main thread;
        /// <see langword="false"/> on any other thread, and on every thread
        /// before the main thread has been identified (see
        /// <see cref="MainThreadIsKnown"/>).
        /// </summary>
        public static bool IsMainThread =>
            _mainThreadId != 0
            && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// Whether the main thread has been identified. In a Unity player it is
        /// identified before the first scene loads; outside a player it may
        /// never be. Use it to tell "another thread" from "not yet known" when
        /// <see cref="IsMainThread"/> returns <see langword="false"/>.
        /// </summary>
        public static bool MainThreadIsKnown => _mainThreadId != 0;

        /// <summary>
        /// Queues <paramref name="action"/> to run on the Unity main thread.
        /// May be called from any thread, and returns without waiting. A
        /// <see langword="null"/> action is ignored.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When <see cref="MaxQueueDepth"/> actions are already waiting,
        /// <see cref="FullPolicy"/> decides the outcome: the new action is
        /// dropped (<see cref="DispatcherFullPolicy.DropTail"/>, the default),
        /// the oldest waiting action is dropped
        /// (<see cref="DispatcherFullPolicy.DropHead"/>), or this call throws
        /// (<see cref="DispatcherFullPolicy.Throw"/>). Use
        /// <see cref="TryEnqueue(Action)"/> to learn whether the action was
        /// queued.
        /// </para>
        /// <para>
        /// Actions still waiting when a session ends or a connection attempt
        /// fails are discarded without running.
        /// </para>
        /// </remarks>
        /// <param name="action">The action to run.</param>
        /// <exception cref="InvalidOperationException">
        /// The queue is full and <see cref="FullPolicy"/> is
        /// <see cref="DispatcherFullPolicy.Throw"/>.
        /// </exception>
        public void Enqueue(Action action)
        {
            if (action == null) return;
            EnqueueCore(new WorkItem(action), RejectionKind.Generic);
        }

        /// <summary>
        /// Queues <paramref name="action"/> like <see cref="Enqueue(Action)"/>,
        /// and reports whether it was queued.
        /// </summary>
        /// <param name="action">The action to run.</param>
        /// <returns>
        /// <see langword="true"/> when the action was queued (under
        /// <see cref="DispatcherFullPolicy.DropHead"/> this can evict the
        /// oldest waiting action); <see langword="false"/> when
        /// <paramref name="action"/> is <see langword="null"/> or the queue was
        /// full under <see cref="DispatcherFullPolicy.DropTail"/>, in which case
        /// the action never runs.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The queue is full and <see cref="FullPolicy"/> is
        /// <see cref="DispatcherFullPolicy.Throw"/>.
        /// </exception>
        public bool TryEnqueue(Action action)
        {
            if (action == null) return false;
            return EnqueueCore(new WorkItem(action), RejectionKind.Generic);
        }

        /// <summary>
        /// Queues <paramref name="action"/> with <paramref name="arg"/>, without
        /// a closure, and reports whether it was queued. Returns and throws as
        /// <see cref="TryEnqueue(Action)"/> does.
        /// </summary>
        /// <typeparam name="TArg">The argument type.</typeparam>
        /// <param name="action">The action to run; a static or cached delegate avoids allocating one per call.</param>
        /// <param name="arg">The value passed to <paramref name="action"/>.</param>
        /// <returns><see langword="true"/> when the action was queued.</returns>
        public bool TryEnqueue<TArg>(Action<TArg> action, TArg arg)
        {
            if (action == null) return false;
            return EnqueueCore(new WorkItem(static state =>
            {
                var pair = ((Action<TArg>, TArg))state;
                pair.Item1(pair.Item2);
            }, (action, arg)), RejectionKind.Generic);
        }

        // Producer threads invoke this when a generic-shape work item
        // (Action / Action<TArg>) is rejected by backpressure.  Symmetric
        // with OnRentedPacketDropped — sized & gated identically (logged at
        // power-of-two boundaries inside RecordOverflow) so dashboards can
        // sum the two streams without policy drift.
        private long _droppedGenericActionCount;

        /// <summary>
        /// Number of actions queued with <c>Enqueue</c> or <c>TryEnqueue</c>
        /// that were refused because the queue was full (under
        /// <see cref="DispatcherFullPolicy.DropTail"/> or
        /// <see cref="DispatcherFullPolicy.Throw"/>) since the dispatcher was
        /// created.
        /// </summary>
        public long DroppedGenericActionCount => Interlocked.Read(ref _droppedGenericActionCount);

        /// <summary>
        /// Raised when <see cref="DroppedGenericActionCount"/> reaches 1, 2, 4,
        /// 8 and each later power of two; the argument is the new count.
        /// </summary>
        /// <remarks>
        /// Raised on the thread that tried to queue the action, which is not
        /// necessarily the main thread, while the dispatcher holds its internal
        /// lock. A handler must be thread-safe, must not call Unity APIs and
        /// must not queue work on the dispatcher. An exception thrown by a
        /// handler is caught and logged.
        /// </remarks>
        public event System.Action<long> OnGenericActionDropped;

        // Delegate slot used by the byte[] overload to record buffer-pair
        // rejections.  Carried through EnqueueCore so the rejection branch
        // can tick the dedicated counter without inspecting the WorkItem
        // shape on every call.
        private enum RejectionKind { Generic, RentedBuffer }

        /// <summary>
        /// Queues <paramref name="action"/> to run on the Unity main thread with
        /// <paramref name="arg"/>, without the closure a lambda passed to
        /// <see cref="Enqueue(Action)"/> would capture. Otherwise it behaves as
        /// <see cref="Enqueue(Action)"/>.
        /// </summary>
        /// <typeparam name="TArg">The argument type.</typeparam>
        /// <param name="action">The action to run; a static or cached delegate avoids allocating one per call.</param>
        /// <param name="arg">The value passed to <paramref name="action"/>.</param>
        /// <exception cref="InvalidOperationException">
        /// The queue is full and <see cref="FullPolicy"/> is
        /// <see cref="DispatcherFullPolicy.Throw"/>.
        /// </exception>
        public void Enqueue<TArg>(Action<TArg> action, TArg arg)
        {
            if (action == null) return;
            // Re-shape Action<TArg> + TArg into Action<object> + object.  When
            // TArg is a reference type the boxing is a no-op cast; when it is
            // a value type the runtime boxes once per call (still cheaper than
            // a closure capturing a local plus the captured "this" pointer).
            EnqueueCore(new WorkItem(static state =>
            {
                var pair = ((Action<TArg>, TArg))state;
                pair.Item1(pair.Item2);
            }, (action, arg)), RejectionKind.Generic);
        }

        /// <summary>
        /// Queues a received packet's buffer for <paramref name="action"/>
        /// without allocating. Used by the SDK's receive path; not intended to
        /// be called from game code.
        /// </summary>
        /// <param name="action">A cached delegate that handles the buffer.</param>
        /// <param name="buffer">The caller's buffer, typically rented from a pool.</param>
        /// <param name="length">Number of valid bytes in <paramref name="buffer"/>.</param>
        /// <returns>
        /// <see langword="true"/> when the item was queued.
        /// <see langword="false"/> when <paramref name="action"/> or
        /// <paramref name="buffer"/> is <see langword="null"/>, or the queue was
        /// full under <see cref="DispatcherFullPolicy.DropTail"/>: the action
        /// then never runs, and the caller must return the buffer to its pool.
        /// </returns>
        /// <remarks>
        /// Once queued, the buffer is released by <paramref name="action"/>. If
        /// the item never runs (evicted under
        /// <see cref="DispatcherFullPolicy.DropHead"/>, or discarded when a
        /// session ends) or its action throws, the buffer is passed to
        /// <see cref="BufferReturnHandler"/> instead.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The queue is full and <see cref="FullPolicy"/> is
        /// <see cref="DispatcherFullPolicy.Throw"/>.
        /// </exception>
        public bool Enqueue(Action<byte[], int> action, byte[] buffer, int length)
            => Enqueue(action, buffer, length, sheddable: false);

        /// <summary>
        /// <see cref="Enqueue(Action{byte[], int}, byte[], int)"/>, for a received
        /// packet that may be <paramref name="sheddable"/>: refused, as a full
        /// queue refuses, once <see cref="MaxSheddableDepth"/> sheddable packets
        /// are waiting.
        /// </summary>
        internal bool Enqueue(Action<byte[], int> action, byte[] buffer, int length, bool sheddable)
        {
            if (action == null || buffer == null) return false;
            return EnqueueCore(new WorkItem(action, buffer, length, sheddable), RejectionKind.RentedBuffer);
        }

        private bool EnqueueCore(WorkItem item, RejectionKind kind)
        {
            // Lifted out of the locked region: a DropHead eviction that carries
            // a rented buffer is captured here and serviced AFTER the lock is
            // released.  Two reasons to do this:
            //   • A user-installed BufferReturnHandler that re-enters the
            //     dispatcher (or any code path that takes another lock that
            //     might be held by a thread waiting on _enqueueLock) would
            //     otherwise deadlock the producer.
            //   • ArrayPool.Return takes its own lock and zero-initialises
            //     the buffer; serialising that work behind _enqueueLock
            //     unnecessarily widens the producer-side critical section
            //     under backpressure.
            byte[] evictedBuffer = null;
            bool   evictedRented = false;

            bool result;
            lock (_enqueueLock)
            {
                // Read depth inside the lock so the check-policy-enqueue-increment
                // sequence is atomic with respect to other producers.  Update()'s
                // drain (TryDequeue + Interlocked.Decrement) never takes this lock,
                // so it can only make room — it cannot cause the depth to rise.
                //
                // A sheddable packet is refused at its own ceiling first, whatever
                // the policy: the places below MaxQueueDepth that it leaves are
                // the ones the rest of the queue is promised.
                if (item.Sheddable && Volatile.Read(ref _sheddableDepth) >= MaxSheddableDepth)
                {
                    Interlocked.Increment(ref _shedPacketCount);
                    BumpRentedDropAndNotify();
                    return false;
                }

                int current = Volatile.Read(ref _depth);
                if (current >= MaxQueueDepth)
                {
                    switch (FullPolicy)
                    {
                        case DispatcherFullPolicy.DropHead:
                            // Remove the oldest pending action to make room for
                            // the new one.  Decrement only when we actually popped
                            // (Update may have already drained the queue).  When
                            // the popped item carries a rented buffer, the original
                            // network-thread caller already received an accepted=true
                            // and will never invoke Return itself.  Route the buffer
                            // through BufferReturnHandler so the rental is balanced
                            // and ArrayPool is not silently starved under sustained
                            // backpressure.
                            if (_queue.TryDequeue(out var evicted))
                            {
                                Dequeued(evicted);
                                if (evicted.BufferAction != null)
                                {
                                    evictedRented = true;
                                    if (evicted.Buffer != null)
                                        evictedBuffer = evicted.Buffer;
                                }
                            }
                            RecordOverflow();
                            break;

                        case DispatcherFullPolicy.Throw:
                            RecordOverflow();
                            if (kind == RejectionKind.RentedBuffer)
                                BumpRentedDropAndNotify();
                            else
                                BumpGenericDropAndNotify();
                            throw new InvalidOperationException(
                                $"MainThreadDispatcher queue is full ({MaxQueueDepth} items). " +
                                "FullPolicy=Throw rejected the new action.");

                        case DispatcherFullPolicy.DropTail:
                        default:
                            RecordOverflow();
                            if (kind == RejectionKind.RentedBuffer)
                                BumpRentedDropAndNotify();
                            else
                                BumpGenericDropAndNotify();
                            return false;
                    }
                }

                _queue.Enqueue(item);
                Interlocked.Increment(ref _depth);
                if (item.Sheddable) Interlocked.Increment(ref _sheddableDepth);
                result = true;
            }

            // Outside the lock: bump the dropped-rented counter and invoke the
            // (potentially user-supplied) BufferReturnHandler.  See the field
            // comments above for the deadlock scenario this avoids.
            if (evictedRented)
            {
                BumpRentedDropAndNotify();
                if (evictedBuffer != null)
                {
                    try { _bufferReturnHandler(evictedBuffer); }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"[RTMPE] MainThreadDispatcher: BufferReturnHandler threw on DropHead eviction.\n{ex}");
                    }
                }
            }

            return result;
        }

        // Increments the rented-drop counter and raises the observability
        // event on every power-of-two transition (1, 2, 4, 8, …).  Mirrors
        // RecordOverflow's cadence so dashboards can correlate the two
        // signals 1:1 without polling.  Subscriber exceptions are isolated
        // so a buggy listener cannot starve the producer thread.
        private void BumpRentedDropAndNotify()
        {
            long total = Interlocked.Increment(ref _droppedRentedPacketCount);
            var handler = OnRentedPacketDropped;
            if (handler == null) return;
            if (total != 1 && (total & (total - 1)) != 0) return;
            try { handler(total); }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RTMPE] MainThreadDispatcher.OnRentedPacketDropped subscriber threw.\n{ex}");
            }
        }

        // Symmetric counter + observability event for generic-shape work
        // items (Action, Action<TArg>).  A dropped main-thread continuation
        // is functionally indistinguishable from a network outage to the
        // application code that posted it — without this signal,
        // pending-RPC continuations, ownership-grant follow-ups, and
        // scene-load resolutions vanish without operator-visible
        // telemetry.
        private void BumpGenericDropAndNotify()
        {
            long total = Interlocked.Increment(ref _droppedGenericActionCount);
            var handler = OnGenericActionDropped;
            if (handler == null) return;
            if (total != 1 && (total & (total - 1)) != 0) return;
            try { handler(total); }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RTMPE] MainThreadDispatcher.OnGenericActionDropped subscriber threw.\n{ex}");
            }
        }

        // Every way an item leaves the queue: the depths fall with it.
        private void Dequeued(in WorkItem item)
        {
            Interlocked.Decrement(ref _depth);
            if (item.Sheddable) Interlocked.Decrement(ref _sheddableDepth);
        }

        private void RecordOverflow()
        {
            // Saturate at long.MaxValue / 2 so the counter cannot wrap.  An
            // unchecked Interlocked.Increment past long.MaxValue rolls over
            // to long.MinValue, after which the (count & (count - 1)) == 0
            // power-of-two test fires every increment — the log cadence
            // contract breaks open into a flood.  Capping at half the range
            // gives effectively unlimited headroom (4.6 × 10^18 events) while
            // keeping the high bit clear, so the bitwise check stays sound.
            long count = Volatile.Read(ref _overflowCount);
            if (count < long.MaxValue / 2)
                count = Interlocked.Increment(ref _overflowCount);

            // Log on the first overflow and at every power-of-two overflow
            // afterwards (1, 2, 4, 8, 16, …).
            if (count == 1 || (count & (count - 1)) == 0)
            {
                Debug.LogError(
                    $"[RTMPE] MainThreadDispatcher: queue full ({MaxQueueDepth}); " +
                    $"policy={FullPolicy}; total overflow events={count}. " +
                    "This usually means the main thread is stalled or a background producer is misconfigured.");
            }
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Initialise the singleton reference for this MonoBehaviour instance.
        ///
       /// <para><b>Threading invariant:</b> Unity guarantees that
        /// <c>Awake</c> runs exclusively on the main thread.  The unsynchronised
        /// reads of <see cref="_instance"/> and <see cref="_mainThreadId"/> are
        /// therefore safe — no other thread can run <c>Awake</c> concurrently,
        /// and the only other writers (the static <see cref="Instance"/>
        /// accessor and <see cref="OnDestroy"/>) are themselves serialised
        /// under <see cref="_instLock"/>.  The lock around the assignment to
        /// <see cref="_instance"/> is preserved so the publication of the new
        /// reference happens under the same monitor that other threads acquire
        /// when they read it via <see cref="Instance"/>, providing the
        /// required release/acquire memory barrier.</para>
        /// </summary>
        private void Awake()
        {
            // Handle the edge case where Unity instantiates a second dispatcher
            // (e.g. scene has a prefab with this component).  This read is
            // safe without the lock because Awake is main-thread-only — see
            // the XML doc above for the full invariant.
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            // First Awake on the main thread always re-captures the id — the
            // SubsystemRegistration hook above already did this, but we belt-
            // and-brace in case a custom domain-reload sequence skipped it.
            if (_mainThreadId == 0)
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            lock (_instLock)
            {
                _instance = this;
            }
        }

        private void Update()
        {
            int processed = 0;
            while (processed < MaxActionsPerFrame && _queue.TryDequeue(out var item))
            {
                Dequeued(item);
                _executingItemEnqueuedTicks = item.EnqueuedTicks;
                try
                {
                    item.Invoke();
                }
                catch (Exception ex)
                {
                    // Never swallow silently in production — log and continue.
                    Debug.LogError($"[RTMPE] MainThreadDispatcher: unhandled exception in dispatched action.\n{ex}");

                    // Buffer-ownership contract on the successful path is
                    // "the BufferAction returns the rental in its own
                    // finally" (see NetworkManager.ProcessPacketAndReturn).
                    // When the user's code throws BEFORE reaching that
                    // finally, no one returns the buffer — the producer
                    // already saw accepted=true and the consumer never
                    // completed.  Without this catch-side return every
                    // dispatched throw permanently drains a slot from the
                    // ArrayPool, slowly starving the receive path.  We
                    // route through the registered handler so non-default
                    // pool wiring (tests, custom rentals) sees the same
                    // path the eviction / drain branches already exercise.
                    if (item.BufferAction != null && item.Buffer != null)
                    {
                        try { _bufferReturnHandler(item.Buffer); }
                        catch (Exception bex)
                        {
                            Debug.LogError(
                                $"[RTMPE] MainThreadDispatcher: BufferReturnHandler threw " +
                                $"during in-flight return: {bex.Message}");
                        }
                    }
                }
                finally
                {
                    _executingItemEnqueuedTicks = 0L;
                }
                processed++;
            }
        }

        private void OnDestroy()
        {
            // Drain pending rented buffers before destruction so ArrayPool
            // retains accurate accounting across scene transitions and domain
            // reloads.  The queue may hold up to MaxQueueDepth WorkItems,
            // each potentially carrying a pool-rented byte[] whose original
            // producer already received accepted=true and will never call
            // Return itself — without this drain the rentals leak.
            DrainPendingBuffers();

            lock (_instLock)
            {
                if (_instance == this)
                    _instance = null;
            }
        }

        // Pull every queued WorkItem and route any rented buffer through the
        // installed BufferReturnHandler.  Runs OUTSIDE _instLock — the lock
        // guards _instance only and the handler is documented as safe to
        // invoke from arbitrary threads without dispatcher locks held.
        private void DrainPendingBuffers()
        {
            while (_queue.TryDequeue(out var item))
            {
                Dequeued(item);
                if (item.BufferAction != null && item.Buffer != null)
                {
                    try { _bufferReturnHandler(item.Buffer); }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"[RTMPE] MainThreadDispatcher: BufferReturnHandler threw during shutdown drain: {ex.Message}");
                    }
                }
            }
        }

#if UNITY_INCLUDE_TESTS
        // Test seam: drives the same drain path the OnDestroy hook uses so
        // edit-mode tests can assert the buffer-return handler is invoked
        // for every queued rental.  Compiled only when
        // UNITY_INCLUDE_TESTS is defined so the shipped Player assembly
        // exposes only the OnDestroy lifetime path, never a manual drain
        // entry point.
        internal void DrainAndDisposeForTest() => DrainPendingBuffers();
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Discards every queued action without running it, whichever thread
        /// queued it, and passes each queued packet buffer to
        /// <see cref="BufferReturnHandler"/>. Main thread only.
        /// </summary>
        /// <remarks>
        /// The SDK calls this when a session ends or a connection attempt
        /// fails, so that work queued during that session does not run against
        /// the next one. Not intended to be called from game code.
        /// </remarks>
        public void DiscardPendingCallbacks()
        {
            Debug.Assert(
                IsMainThread,
                "[RTMPE] MainThreadDispatcher.DiscardPendingCallbacks must be called from the Unity main thread.");
            DrainPendingBuffers();
        }
    }
}
