// RTMPE SDK — Runtime/Infrastructure/Threading/NetworkThread.cs
//
// Dedicated background I/O thread for non-blocking UDP send/receive.
//
// Architectural decisions:
//  • Dedicated Thread (not ThreadPool/Task) — gives stable scheduling, critical for
//    P99 < 30 ms latency; ThreadPool tasks can be starved when the pool is busy. On
//    macOS the thread joins the Utility QoS class (see RunLoop), because .NET's
//    ThreadPriority does not map to a macOS QoS class.
//  • The first receive poll each iteration blocks up to PollWaitMicros, parking the
//    thread in the kernel between datagrams so the core can idle instead of spinning
//    at a fixed ~1 kHz Sleep cadence. At a 30 Hz server tick (33 ms budget) this is
//    ample, and on Apple Silicon it avoids the busy-wait heat a Poll(0)+Sleep(1) loop
//    incurs.
//  • A volatile bool _running acts as the cancellation signal; the thread checks it
//    each iteration. No CancellationToken is used (avoids allocation on hot path).
//  • Per-packet allocation is eliminated by renting receive and send buffers from
//    System.Buffers.ArrayPool<byte>.Shared.  When a subscriber to the rented event
//    is registered we hand the rented buffer through synchronously and return it
//    to the pool the moment the handler returns; otherwise the legacy event is
//    invoked with a freshly-allocated copy (still one fewer copy than before).

using System;
using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using RTMPE.Transport;

namespace RTMPE.Threading
{
    /// <summary>
    /// Dedicated background I/O thread that owns a <see cref="NetworkTransport"/> and
    /// drives the UDP send/receive loop at ~1 kHz.
    ///
    /// All events are raised on this background thread.
    /// Use <see cref="MainThreadDispatcher"/> to marshal them to Unity's main thread.
    /// </summary>
    public sealed class NetworkThread : IDisposable
    {
        // ── Windows timer resolution ───────────────────────────────
        // On Windows, Thread.Sleep(1) uses the system timer interrupt (~15.6 ms
        // default) giving ~64 Hz poll rate instead of the intended ~1 kHz.
        // timeBeginPeriod(1) requests 1 ms resolution for the process lifetime.
        // This is standard practice in multimedia / gaming applications.
        // The companion timeEndPeriod(1) is called in Dispose() to restore the
        // OS default when the network thread is no longer needed.
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
        private bool _timerResSet;
#endif
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        // Apple Silicon energy control.  .NET's ThreadPriority does not map to a
        // macOS Quality-of-Service class, so the receive thread is placed in the
        // Utility QoS class explicitly — the class Apple's Energy Efficiency Guide
        // designates for network I/O.  A Utility-QoS thread is scheduled on the
        // efficiency cores, drawing far less power than the performance cores while
        // comfortably handling the poll/decrypt workload.
        [DllImport("libc")] private static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
        private const uint QOS_CLASS_UTILITY = 0x11; // qos_class_t value from <pthread/qos.h>
#endif
        // ── Dependencies ───────────────────────────────────────────────────────
        private readonly NetworkTransport _transport;
        // Receive buffer size cap.  Each receive rents a buffer of this size from
        // ArrayPool and the kernel writes the datagram into it directly — no
        // shared scratch + copy step.
        private readonly int _receiveBufferSize;

        // ── Thread control ─────────────────────────────────────────────────────
        private Thread        _thread;
        private volatile bool _running;
        // Atomic guard for Start() — prevents two concurrent callers from
        // both passing the `if (_running) return` check and spawning duplicate threads.
        // 0 = stopped, 1 = running.  Interlocked.CompareExchange returns the old value;
        // if it was already 1 the caller lost the race and returns immediately.
        private int _startFlag;  // 0 = stopped, 1 = started
        private int _disposed;   // 0 = live, 1 = Dispose() has run; Start() refuses after it
        // Which run loop currently owns this instance's transport and send
        // queue.  Every Start() takes the next generation and every run loop
        // carries the one it was started under, so a loop can ask whether it is
        // still the current occupant of the socket it is about to touch.
        //
        // The question has an answer only because Stop() cannot always deliver
        // one: when the join times out the loop is still alive — typically
        // parked in a native receive — while the caller goes on to hand the
        // same transport to a fresh attempt.  Advancing the generation there
        // retires the survivor without waiting for it: it leaves the loop on
        // its next poll, and on the way out it neither flushes the queue nor
        // closes the socket, both of which now belong to its successor.
        private int _runGeneration;
        // The generation whose run loop ended on a session-fatal fault, or 0.
        // Its socket is gone, so the shutdown flush below has nothing to send —
        // but the frames it would have sent hold pool rentals that must still
        // come back, and Stop() called from the OnError handler returns at its
        // own run-flag guard without draining them.
        private int _faultedGeneration;

        // ── Outbound queue ─────────────────────────────────────────────────────
        // Holds (buffer, length, fromPool).  When fromPool is true the buffer
        // was rented from ArrayPool<byte>.Shared and MUST be returned exactly
        // once after the underlying transport has finished with it.  Reference
        // type byte[] in ConcurrentQueue does not allocate on the hot path
        // beyond the segment node; the struct itself is 16 bytes and fits in
        // ConcurrentQueue<T>'s internal slots without boxing.
        private readonly ThreadSafeQueue<SendItem> _sendQueue = new ThreadSafeQueue<SendItem>();

        // Hard cap on the outbound queue depth.  Prevents an unbounded heap
        // leak when producers (Send / SendOwned) outpace the drain — the
        // primary risk window is sustained ENOBUFS, where the drain backs off
        // to ~250 datagrams/second while a 30 Hz × 16-player session
        // continues to enqueue at ~480 datagrams/second.  Net growth ≈
        // 230 items/second, each holding up to ~1200 bytes of pool-rented
        // payload — without a cap the heap leaks ≈ 280 KiB/second until OOM.
        // See sendQueueMaxItems on NetworkSettings for the configurable bound.
        private readonly int _maxQueuedItems;
        private long _sendQueueDroppedCount;

        private readonly struct SendItem
        {
            public readonly byte[] Buffer;
            public readonly int    Length;
            public readonly bool   FromPool;
            public SendItem(byte[] buffer, int length, bool fromPool)
            {
                Buffer   = buffer;
                Length   = length;
                FromPool = fromPool;
            }
        }

        // Back-pressure guard: drain at most this many packets per loop iteration
        // to avoid starving the receive side under heavy write load.
        private const int MaxSendPerIteration = 100;
        // Cap inbound drain similarly — under burst, leaves time for sends.
        private const int MaxReceivePerIteration = 100;

        // The first receive poll of each loop iteration blocks up to this many
        // microseconds, parking the thread in the kernel until a datagram arrives
        // (or the span elapses) rather than returning immediately and spinning.
        // Any inbound datagram wakes the poll immediately, so during active play —
        // where state broadcasts arrive continuously — this bound only fills the
        // idle gaps between datagrams.  4 ms (vs the former 1 ms) cuts those idle
        // wakeups ~4x — the dominant thermal cost on Apple Silicon — while
        // bounding the added outbound-send latency to 4 ms, negligible at the
        // 30 Hz tick the server coalesces to and absorbed by remote interpolation.
        // The owner's own view is local-authoritative and so is unaffected.
        private const int PollWaitMicros = 4000;

        // ── Public surface ─────────────────────────────────────────────────────

        /// <summary>True while the background thread is running.</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Raised on the network background thread when a datagram is received.
        /// The argument is an exclusively-owned copy of the payload bytes
        /// (length is exactly the datagram size).
        /// Subscribe before calling <see cref="Start"/>.
        ///
        /// Prefer <see cref="OnPacketReceivedRented"/> in hot paths — it elides
        /// the per-packet allocation by handing the receive buffer through
        /// directly.  The legacy event remains supported for callers that
        /// retain the bytes beyond the synchronous handler return.
        /// </summary>
        public event Action<byte[]> OnPacketReceived;

        /// <summary>
        /// Zero-copy inbound delivery.  The handler is invoked synchronously with
        /// a buffer rented from <see cref="ArrayPool{T}.Shared"/>.  The rented
        /// buffer's <c>Length</c> is ≥ <paramref name="length"/> and only the
        /// first <paramref name="length"/> bytes are valid datagram payload.
        ///
        /// The rented buffer is returned to the pool the moment every subscriber
        /// returns.  Callers MUST NOT retain a reference to the array beyond the
        /// duration of the synchronous call — copy out anything they wish to
        /// keep.  Failing this rule yields use-after-return data corruption.
        ///
        /// When at least one rented subscriber is registered the legacy
        /// <see cref="OnPacketReceived"/> event is NOT raised for that packet —
        /// the rented event is the new canonical path.
        /// </summary>
        public event RentedPacketHandler OnPacketReceivedRented;

        /// <summary>
        /// Raised on the network background thread when a non-recoverable
        /// transport error occurs. After this event the thread exits.
        /// <para>
        /// Exactly once per run loop, and only for the loop that still owns the
        /// transport.  Both faulting paths beneath the loop — the send drain and
        /// the receive poll — end it rather than returning to it, so a socket
        /// that is fatal on every poll reports itself once instead of at poll
        /// cadence; and a loop superseded while it was unwinding stays silent,
        /// because the only subscriber to this event responds by tearing down
        /// whichever session is live when it runs.
        /// </para>
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// Synchronous handler for <see cref="OnPacketReceivedRented"/>.
        /// </summary>
        /// <param name="buffer">Pool-rented buffer — DO NOT retain past handler return.</param>
        /// <param name="offset">First byte of the datagram payload (always 0 today).</param>
        /// <param name="length">Number of valid bytes starting at <paramref name="offset"/>.</param>
        public delegate void RentedPacketHandler(byte[] buffer, int offset, int length);

        // ── Construction ───────────────────────────────────────────────────────

        /// <param name="transport">Transport to own and operate. Disposed on <see cref="Dispose"/>.</param>
        /// <param name="receiveBufferSize">
        /// Per-packet receive buffer size in bytes.  Defaults to the largest
        /// packet the gateway is permitted to send, because a datagram larger
        /// than this buffer is truncated by the socket, fails its
        /// authentication tag and is discarded before any parser runs — the
        /// loss is silent, and two server replies already exceed a typical
        /// scratch buffer on their own.
        /// </param>
        /// <param name="sendQueueMaxItems">
        /// Hard cap on the number of outbound <see cref="SendItem"/> entries the
        /// thread will hold at any one time.  When the queue is at the cap,
        /// <see cref="Send"/> and <see cref="SendOwned"/> drop the newest packet
        /// and increment <see cref="SendQueueDroppedCount"/> instead of
        /// enqueueing.  Default 4096 ≈ 4 MB at 1200 B/item — bounded for
        /// mobile while large enough to absorb routine bursts.
        /// </param>
        public NetworkThread(
            NetworkTransport transport,
            int receiveBufferSize = RTMPE.Protocol.PacketBuilder.MaxDatagramBytes,
            int sendQueueMaxItems = 4_096)
        {
            if (receiveBufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(receiveBufferSize));
            if (sendQueueMaxItems <= 0)
                throw new ArgumentOutOfRangeException(nameof(sendQueueMaxItems));

            _transport         = transport ?? throw new ArgumentNullException(nameof(transport));
            _receiveBufferSize = receiveBufferSize;
            _maxQueuedItems    = sendQueueMaxItems;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// Start the dedicated I/O thread. No-op if already running.
        /// Thread-safe: concurrent calls are correctly serialised by an atomic guard.
        /// </summary>
        public void Start()
        {
            // A disposed thread owns a disposed transport and a released queue;
            // starting one would spawn a run loop over neither. The shipped suite
            // has asserted this refusal since it was written, and the class never
            // had a disposed state to answer with — Start after Dispose passed the
            // CAS below and spawned a thread.
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(NetworkThread));

            // Use Interlocked.CompareExchange as a lock-free atomic guard.
            // The volatile `if (_running) return` check-then-set was not atomic:
            // two threads could both read false and both spawn a thread on the same
            // socket.  CAS atomically sets _startFlag from 0→1; only the thread that
            // observes the old value as 0 proceeds.
            if (System.Threading.Interlocked.CompareExchange(ref _startFlag, 1, 0) != 0)
                return; // already started by this or another caller

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // Raise Windows timer resolution to 1 ms so Thread.Sleep(1)
            // actually sleeps ~1 ms instead of the default ~15.6 ms.  The 1 ms
            // period is an optional throughput accelerator: a runtime that
            // cannot bind winmm (a trimmed or emulated Windows build) keeps the
            // OS-default granularity rather than aborting the connect this
            // thread serves.  _timerResSet stays false on a bind failure, so the
            // companion restore in Dispose is correctly skipped.
            if (!_timerResSet)
                _timerResSet = OptionalNativeCall.TryInvoke(() => timeBeginPeriod(1));
#endif

            // Claim the next generation before the run flag, not after.  The
            // flag is shared with any predecessor Stop() could not join, and
            // that loop is allowed to clear it on the way out; taking the
            // generation first is what makes it ask whose flag it is.
            int generation = Interlocked.Increment(ref _runGeneration);
            _running = true;
            _thread  = new Thread(() => RunLoop(generation))
            {
                Name         = "RTMPE-NetworkThread",
                IsBackground = true,   // Does not prevent process exit
                Priority     = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        /// <summary>
        /// Signal the thread to stop and wait up to 2 seconds for a clean exit.
        /// Safe to call multiple times. Blocks the calling thread.
        ///
        /// Asymmetry — when invoked from the network thread itself, this method
        /// does NOT join.  Calling <see cref="Thread.Join(int)"/> on the current
        /// thread would deadlock for the full timeout (a self-join can never
        /// satisfy itself).  Instead the running flag is cleared and the run
        /// loop exits naturally on its next iteration; cleanup that the caller
        /// normally relies on (transport disconnect, send-queue drain) runs in
        /// the loop's finally block and after RunLoop returns the thread
        /// terminates.
        /// <para>
        /// ⛔ The canonical way onto that path — an <see cref="OnError"/> handler
        /// that stops the thread — no longer reaches it: every fault now travels
        /// through <c>FailSession</c>, which clears the running flag before it
        /// raises, so such a call returns at the guard on the first line.  The
        /// self-join guard stands for a caller that reaches this method from the
        /// network thread by some other route; on the OnError path the loop
        /// releases its own queue on the way out instead.
        /// </para>
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            ReleaseRunFlags();

            // Self-join guard.  A consumer that wires OnError → Stop() (the
            // canonical disconnect-on-fatal-error pattern) executes this method
            // on the network thread — Thread.Join on Thread.CurrentThread blocks
            // for the full timeout and never completes.  Detect that case and
            // return early; the run loop observes _running=false on its next
            // poll and exits cleanly through the normal finally path.
            var t = _thread;
            if (t != null && Thread.CurrentThread == t)
            {
                // Do NOT null _thread here — the thread is still alive and
                // running; clearing the field would race a concurrent
                // foreign-thread Stop() that legitimately needs the reference
                // to call Join.  The thread self-references will be released
                // when RunLoop unwinds and a subsequent foreign Stop() (or
                // Dispose) completes the cleanup.
                return;
            }

            if (t != null && t.IsAlive)
            {
                if (!t.Join(2_000))
                {
                    // Thread.Interrupt() only works when the thread
                    // is in a managed blocking state (WaitSleepJoin). When blocked
                    // in a native ReceiveFrom, it has no effect. Closing the socket
                    // forces ReceiveFrom to throw a SocketException, which the
                    // RunLoop catch clause handles gracefully.
                    _transport.Disconnect();
                    if (!t.Join(500))
                    {
                        // The loop outlives this call, and the caller is free to
                        // start another one over the same transport the instant
                        // this returns.  Retire the survivor by generation: it
                        // leaves on its next poll and, finding itself superseded,
                        // touches neither the send queue nor the socket on the
                        // way out — the two things a successor would otherwise
                        // find drained and closed underneath it.
                        Interlocked.Increment(ref _runGeneration);
                    }
                }
            }

            _thread = null;

            // Drain the send queue to release any pool-rented buffers that never
            // made it onto the wire.  Skipping this would leak rented arrays
            // permanently from the shared pool's perspective.
            DrainAndReleasePending();
        }

        /// <summary>
        /// Enqueue <paramref name="data"/> for transmission on the next loop iteration.
        /// Thread-safe; returns immediately. The incoming array is copied internally
        /// so the caller can safely reuse or discard its buffer.
        ///
        /// Bounded contract — the outbound queue is capped at the
        /// <c>sendQueueMaxItems</c> value passed to the constructor (default
        /// 4096).  When the queue is at capacity (typically under sustained
        /// ENOBUFS, where the drain rate falls below the producer rate) the
        /// newest packet is dropped on the floor and
        /// <see cref="SendQueueDroppedCount"/> is incremented.  Drop-newest is
        /// chosen over drop-oldest because (a) it preserves arrival order of
        /// already-queued traffic — drop-oldest would invert sequence at the
        /// receiver and break per-packet ordering guarantees built on top of
        /// this layer; and (b) it requires no head-removal primitive on
        /// <see cref="ThreadSafeQueue{T}"/>.  No <see cref="OnError"/> is raised
        /// (matches the ENOBUFS ethos — telemetry only); integrators MUST
        /// monitor <see cref="SendQueueDroppedCount"/> to detect saturation.
        /// </summary>
        public void Send(byte[] data, bool reliable = false)
        {
            if (!_running || data == null || data.Length == 0) return;

            // Copy into a pool-rented buffer.  ArrayPool may hand back an array
            // larger than data.Length; we record the exact byte count alongside
            // the buffer so the transport sends only the meaningful prefix.
            var rented = ArrayPool<byte>.Shared.Rent(data.Length);

            // Cap check is intentionally a non-atomic Count read followed by
            // Enqueue.  A small overshoot bounded by the number of concurrent
            // producers is acceptable; the alternative — a lock around every
            // enqueue — would add cross-core contention on the hot send path.
            if (_sendQueue.Count >= _maxQueuedItems)
            {
                Interlocked.Increment(ref _sendQueueDroppedCount);
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                return;
            }

            try
            {
                Buffer.BlockCopy(data, 0, rented, 0, data.Length);
                _sendQueue.Enqueue(new SendItem(rented, data.Length, fromPool: true));
            }
            catch
            {
                // Enqueue throwing (OOM during segment grow, BlockCopy on a
                // bogus rented buffer) would otherwise leak the rental for
                // the lifetime of the process.
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                throw;
            }

            // Stop() races: if the worker shut down between the entry guard
            // and Enqueue, the item lives in the queue with no future
            // drainer.  Re-check and drain the queue ourselves so the
            // rental is returned and the caller's data is not silently
            // lost in a quiet leak.  Drain is idempotent — DrainAndReleasePending
            // is safe to call repeatedly.
            if (!_running) DrainAndReleasePending();
        }

        /// <summary>
        /// Enqueue <paramref name="ownedData"/> for transmission without copying.
        /// The caller MUST NOT read or modify <paramref name="ownedData"/> after
        /// this call — ownership is transferred to the send queue.
        ///
        /// Use this instead of <see cref="Send"/> when <paramref name="ownedData"/>
        /// was freshly allocated (e.g. the return value of
        /// <see cref="RTMPE.Protocol.PacketBuilder.Build"/>) and will not be
        /// reused.  Eliminates the redundant copy that <see cref="Send"/> makes,
        /// halving per-packet GC pressure on the hot data path.
        /// </summary>
        public void SendOwned(byte[] ownedData)
        {
            if (!_running || ownedData == null || ownedData.Length == 0) return;

            // Drop-newest under saturation.  See Send() for the rationale on
            // ordering and policy choice.  The owned array is not pool-rented,
            // so no return-to-pool step is needed — letting the reference
            // fall out of scope is sufficient.
            if (_sendQueue.Count >= _maxQueuedItems)
            {
                Interlocked.Increment(ref _sendQueueDroppedCount);
                return;
            }

            // Caller-owned arrays are sent in full; they must not be returned to
            // the pool because they were never rented from it.  Send's
            // try/catch wraps the rented-buffer return contract on
            // Enqueue-throw — there is no symmetric resource to release
            // here (the caller still holds the reference and the GC
            // reclaims it once it goes out of scope), so this path
            // intentionally has no try/catch.
            _sendQueue.Enqueue(new SendItem(ownedData, ownedData.Length, fromPool: false));

            // Stop()-race recovery, symmetric with Send.  Without it, an
            // ownedData byte[] enqueued AFTER the worker drained but BEFORE
            // the entry guard observed the new _running=false is silently
            // lost in the queue (the next Start() would transmit it, possibly
            // violating handshake ordering, or process exit drops it
            // entirely).  DrainAndReleasePending is idempotent and only
            // touches pool-rented items, so non-pooled SendItems passed here
            // are still GC-cleaned by reference loss — but the rented items
            // sharing the queue are returned to the pool either way.
            if (!_running) DrainAndReleasePending();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            System.Threading.Volatile.Write(ref _disposed, 1);
            Stop();
            _transport.Dispose();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // Restore the OS default timer resolution if it was raised.  The
            // restore is itself guarded: Dispose runs on teardown paths that
            // must never throw, and the period is only an accelerator.
            if (_timerResSet)
            {
                OptionalNativeCall.TryInvoke(() => timeEndPeriod(1));
                _timerResSet = false;
            }
#endif
        }

        // ── Private I/O loop ───────────────────────────────────────────────────

        private void RunLoop(int generation)
        {
            try
            {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
                // Runs on the network thread itself, so it sets THIS thread's QoS:
                // place it in the Utility class so macOS schedules the poll/decrypt
                // work on the energy-efficient cores.  Best-effort — a missing
                // symbol must not abort the I/O loop.
                OptionalNativeCall.TryInvoke(
                    () => pthread_set_qos_class_self_np(QOS_CLASS_UTILITY, 0));
#endif
                _transport.Connect();

                while (_running && StillOwns(generation))
                {
                    DrainSendQueue(generation);
                    // The first poll in TryReceive blocks up to PollWaitMicros, so
                    // it — not a fixed Sleep — provides the loop cadence while the
                    // core idles between datagrams.
                    TryReceive(generation);
                }

                // Clean shutdown: push anything still queued onto the wire before
                // the finally closes the socket. Stop() flips _running while the
                // loop is typically parked in the receive poll, so a packet
                // enqueued in that window never reaches another DrainSendQueue pass
                // and would otherwise be dropped by Stop()'s DrainAndReleasePending.
                // The graceful Disconnect emitted during teardown is exactly such a
                // packet; losing it strands the player's room seat until the
                // gateway's liveness timeout. A transport-level fault instead lands
                // in the catch below against a dead socket, so the flush runs only
                // on this clean-exit path where the transport is still connected.
                // Three exits, and only one of them may put bytes on the wire.
                //
                // A superseded loop leaves the queue alone entirely: those
                // frames are now the successor's to send, and this pass would
                // put them on a socket that belongs to a different session.
                //
                // A loop that ended on a session-fatal fault owns the queue but
                // no longer has a socket — the comment above describes that case
                // as landing in the catch below, and it did until the send and
                // receive drains were made to END the loop rather than return to
                // it.  Flushing there attempted every queued frame against a
                // dead socket, one throw apiece.  The rentals still have to come
                // back, so the queue is drained and released rather than sent.
                if (!StillOwns(generation))
                {
                    // Nothing: neither the queue nor the socket is ours. What is
                    // in the queue is either the successor's to send, or — when
                    // this instance was retired outright — released by the next
                    // Send on it, both of which drain while the run flag is down.
                }
                else if (Volatile.Read(ref _faultedGeneration) == generation)
                {
                    DrainAndReleasePending();
                }
                else
                {
                    FlushPendingSendsBestEffort();
                }
            }
            catch (ThreadInterruptedException)
            {
                // Normal: raised by Stop() when Join times out.  Defensive as
                // written today — the receive and send drains each catch
                // Exception themselves, so the only code this arm can see is the
                // transport connect above, and nothing here calls Interrupt at
                // all.  The ownership test is the same rule the other arms
                // carry: the run flag is shared with a successor, and a loop
                // that no longer owns it must not clear it.
                if (StillOwns(generation)) ReleaseRunFlags();
            }
            catch (Exception ex)
            {
                FailSession(generation, ex);
            }
            finally
            {
                // Closing the socket is the last act of the loop that owns it.
                // A superseded loop shares this transport with the successor
                // that took it over while this one was still unwinding, so here
                // it does nothing at all.
                if (StillOwns(generation))
                    _transport.Disconnect();
            }
        }

        // Whether the loop started under <paramref name="generation"/> is still
        // the one this instance's transport, send queue and run flags belong to.
        private bool StillOwns(int generation) =>
            Volatile.Read(ref _runGeneration) == generation;

        /// <summary>
        /// Give this instance up without waiting for its run loop to notice.
        /// </summary>
        /// <remarks>
        /// <see cref="Stop"/> retires a loop by generation only when its join
        /// times out — and it never reaches that code at all when the loop is
        /// ending on a transport fault, because the fault clears the run flags
        /// before it raises and <see cref="Stop"/> returns at its own guard on
        /// the first line.  The loop is then still unwinding, still owns its
        /// generation, and closes the socket in its <c>finally</c>.
        /// <para>
        /// That socket is shared: the manager keeps one transport across
        /// attempts and builds a fresh <c>NetworkThread</c> over it, so the
        /// dying loop was closing the connection its successor had just bound —
        /// measured, one disconnect with the successor live.  A generation
        /// counter cannot see across instances, so the owner says so explicitly
        /// here.  Idempotent, and a no-op for a loop that has already exited.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// ⛔ Advancing the generation is the whole of it. A first version also
        /// marked the instance retired so its exit could drain the send queue,
        /// on the reasoning that a retired loop has no successor to return its
        /// pool rentals — and no test could be made to discriminate it, because
        /// <see cref="Send"/> and <see cref="SendOwned"/> already drain and
        /// release whenever the run flag is down. Machinery whose property
        /// nothing can state does not ship.
        /// </remarks>
        public void Retire() => Interlocked.Increment(ref _runGeneration);

        /// <summary>
        /// Clear the pair of flags that together mean "a run loop exists".
        /// </summary>
        /// <remarks>
        /// One fact written twice, and it must be cleared in one place.  It was
        /// not: the interrupted arm cleared <c>_running</c> alone, and
        /// <see cref="Stop"/> returns at its own <c>!_running</c> guard before it
        /// would reach the other — so the atomic guard stayed claimed, every
        /// later <see cref="Start"/> lost its compare-exchange, and the instance
        /// could never run a loop again. Measured: one connect, then six
        /// attempts that never reached the transport, with no diagnostic that
        /// said why.
        /// </remarks>
        private void ReleaseRunFlags()
        {
            _running = false;
            Interlocked.Exchange(ref _startFlag, 0);
        }

        /// <summary>
        /// End the run loop on a transport fault the session cannot survive, and
        /// report it — the contract <see cref="OnError"/> states.
        /// </summary>
        /// <remarks>
        /// Ownership is the whole of the guard.  A superseded loop's fault
        /// describes a session that is already over: reporting it would tear
        /// down the successor's session through the one subscriber that acts on
        /// this event, and clearing the run flags — which are per-instance and
        /// therefore shared — would stop the successor's loop outright.
        /// </remarks>
        private void FailSession(int generation, Exception ex)
        {
            if (!StillOwns(generation)) return;

            Volatile.Write(ref _faultedGeneration, generation);
            // Released BEFORE invoking OnError so that any reconnect attempt
            // inside the handler can call Start() successfully.  Without it
            // _running stays true and the next Start() is a no-op —
            // permanently locking out reconnection.
            ReleaseRunFlags();
            OnError?.Invoke(ex);
        }

        // Backoff state for ENOBUFS handling.  When the kernel's send buffer
        // is exhausted we cannot make progress until it drains; spinning at
        // ~1 kHz and firing OnError on every iteration would create an error
        // storm visible to the SDK consumer.  Instead we sleep for an
        // exponentially-increasing interval (1 ms → cap) and keep the
        // pending item at the head of the queue by re-enqueuing it.
        private const int EnobufsBackoffStartMs = 1;
        private const int EnobufsBackoffCapMs   = 4;
        private int  _enobufsBackoffMs = EnobufsBackoffStartMs;
        private long _enobufsCount;

        // Faults confined to a single datagram (see TransportFaultPolicy).
        // Counted rather than surfaced through OnError, whose only subscriber
        // ends the session.
        private long _perPacketFaultCount;

        // One-shot latches for the two malformed-receive-result reports.  A
        // transport that gets either wrong gets it wrong on every datagram, so
        // the condition is named once and tracked on the fault counter after.
        private int _oversizeCountReported;
        private int _unspecifiedCountReported;

        // Poll telemetry: counts of blocking-poll calls that returned with data
        // (hit) vs. timed out idle (miss).  A miss is an idle wakeup —
        // misses/second ≈ thread wakeups/second (~250 Hz at the 4 ms
        // PollWaitMicros).  These are blocking kernel polls that yield the CPU,
        // not a busy-wait: a low hit rate just means the inbound path is idle,
        // which — through the wakeup rate — is the remaining thermal lever on
        // Apple Silicon.
        private long _pollHitCount;
        private long _pollMissCount;

        /// <summary>
        /// Total number of ENOBUFS events the send loop has absorbed.
        /// Exposed for telemetry; never resets across the thread's lifetime.
        /// </summary>
        public long EnobufsCount => Interlocked.Read(ref _enobufsCount);

        /// <summary>
        /// Total number of transport faults absorbed as per-datagram rather
        /// than raised through <see cref="OnError"/>.  Sustained growth means
        /// packets are being lost at the socket boundary while the session
        /// stays up — a signal that would otherwise be invisible, since the
        /// alternative to counting them is ending the session.
        /// </summary>
        public long PerPacketFaultCount => Interlocked.Read(ref _perPacketFaultCount);

        /// <summary>
        /// Total number of outbound packets dropped because the send queue
        /// was at its configured cap (drop-newest policy).  Sustained growth
        /// indicates the producer rate exceeds the drain rate — typically a
        /// symptom of an ENOBUFS-bounded uplink.  Exposed for telemetry;
        /// never resets across the thread's lifetime.
        /// </summary>
        public long SendQueueDroppedCount => Interlocked.Read(ref _sendQueueDroppedCount);

        /// <summary>
        /// Current depth of the outbound send queue.  Approximate — may be
        /// observed mid-mutation by a concurrent producer or by the drain
        /// loop.  Useful for telemetry dashboards and saturation tests.
        /// </summary>
        public int SendQueueCount => _sendQueue.Count;

        /// <summary>
        /// Cumulative count of <c>Poll(0)</c> calls that returned
        /// <see langword="true"/> (a datagram was waiting).  Combines with
        /// <see cref="PollMissCount"/> to give the poll hit rate:
        /// <c>hits / (hits + misses)</c>.  Below ~5% at 1 kHz cadence
        /// the thread is waking almost exclusively for nothing —
        /// the primary contributor to busy-wait thermal load on macOS.
        /// Never resets across the thread's lifetime.
        /// </summary>
        public long PollHitCount  => Interlocked.Read(ref _pollHitCount);

        /// <summary>
        /// Cumulative count of <c>Poll(0)</c> calls that returned
        /// <see langword="false"/> (no data available).  Each miss is one
        /// wasted wakeup; at 1 kHz cadence, misses/second approximates the
        /// thread wakeup rate on an idle connection.
        /// Never resets across the thread's lifetime.
        /// </summary>
        public long PollMissCount => Interlocked.Read(ref _pollMissCount);

        private void DrainSendQueue(int generation)
        {
            // ⛔ The ownership test belongs HERE, not on the loop condition in
            // RunLoop.  A loop condition is a statement about iterations; what
            // needs protecting is the syscall.  This pass is up to a hundred
            // sends deep and it parks inside `_transport.Send` — a blocking
            // sendto, a custom transport, or the ENOBUFS sleep below, which sits
            // in a catch and outside every try.  A loop parked there when the
            // generation advances used to finish the whole pass: measured, two
            // threads draining one queue into one socket, which inverts exactly
            // the arrival order this queue's drop-newest policy exists to keep.
            for (int i = 0;
                 i < MaxSendPerIteration && StillOwns(generation)
                 && _sendQueue.TryDequeue(out var item);
                 i++)
            {
                try
                {
                    // UdpTransport exposes a slice-aware overload that avoids
                    // copying the rented buffer down to its meaningful prefix.
                    if (_transport is UdpTransport udp)
                    {
                        udp.Send(item.Buffer, 0, item.Length);
                    }
                    else
                    {
                        // Fallback for other transports (KCP, mock): if the
                        // rented buffer is exactly the right size we hand it
                        // straight in; otherwise copy down to a temporary
                        // exact-sized array because the abstract Send contract
                        // sends the entire array.
                        if (item.Buffer.Length == item.Length)
                        {
                            _transport.Send(item.Buffer);
                        }
                        else
                        {
                            var exact = new byte[item.Length];
                            Buffer.BlockCopy(item.Buffer, 0, exact, 0, item.Length);
                            _transport.Send(exact);
                        }
                    }
                    // Successful send — reset the ENOBUFS backoff so the next
                    // exhaustion event starts at the minimum sleep again.
                    _enobufsBackoffMs = EnobufsBackoffStartMs;
                }
                catch (SocketException sx)
                    when (sx.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    // Kernel send buffer full.  This is transient: stop the
                    // current drain pass, sleep with exponential backoff,
                    // and re-enqueue the unsent item so it is retried on
                    // the next iteration.  Re-enqueue is to the tail (the
                    // backing ConcurrentQueue exposes no head-insertion
                    // primitive); under saturation any subsequent items
                    // already enqueued ahead are equally blocked, so the
                    // tail-reorder is bounded by MaxSendPerIteration and
                    // not observable in practice.  Crucially, do NOT raise
                    // OnError — at 1 kHz poll cadence that would generate
                    // up to a thousand error callbacks per second under
                    // sustained uplink saturation.
                    Interlocked.Increment(ref _enobufsCount);
                    _sendQueue.Enqueue(item);
                    int sleep = _enobufsBackoffMs;
                    _enobufsBackoffMs = Math.Min(_enobufsBackoffMs * 2, EnobufsBackoffCapMs);
                    Thread.Sleep(sleep);
                    return;
                }
                catch (Exception ex)
                {
                    if (item.FromPool) ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);

                    // A fault confined to this datagram costs the datagram, not
                    // the session: drop it, count it, and keep draining the
                    // queue behind it.  Raising OnError here would reach the
                    // one subscriber that tears the session down, so an
                    // oversize frame or an ICMP port-unreachable would end a
                    // healthy connection.
                    if (!TransportFaultPolicy.IsSessionFatal(ex))
                    {
                        Interlocked.Increment(ref _perPacketFaultCount);
                        continue;
                    }

                    FailSession(generation, ex);
                    break;  // The session is over; leave the rest of the queue to the teardown.
                }

                if (item.FromPool) ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);
            }
        }

        // Drain any send items that remain queued at shutdown, returning rented
        // buffers to the pool.  Without this the pool sees the rentals leaked.
        private void DrainAndReleasePending()
        {
            while (_sendQueue.TryDequeue(out var item))
            {
                if (item.FromPool) ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);
            }
        }

        // Transmit whatever is still queued when the run loop exits cleanly, then
        // return rented buffers. The discard-only DrainAndReleasePending covers
        // the error and stuck-thread paths, where the socket can no longer carry
        // traffic; a clean Stop() must instead deliver the pending frames — above
        // all the graceful Disconnect — so the gateway releases the room seat at
        // once rather than waiting out its liveness timeout. Best-effort by
        // design: teardown is already underway, so a send fault is swallowed and
        // left to that timeout, and the pass is bounded by a queue-size snapshot
        // so it cannot livelock against a producer (none runs on this path).
        private void FlushPendingSendsBestEffort()
        {
            int budget = _maxQueuedItems;
            while (budget-- > 0 && _sendQueue.TryDequeue(out var item))
            {
                try
                {
                    if (_transport is UdpTransport udp)
                    {
                        udp.Send(item.Buffer, 0, item.Length);
                    }
                    else if (item.Buffer.Length == item.Length)
                    {
                        _transport.Send(item.Buffer);
                    }
                    else
                    {
                        var exact = new byte[item.Length];
                        Buffer.BlockCopy(item.Buffer, 0, exact, 0, item.Length);
                        _transport.Send(exact);
                    }
                }
                catch (Exception)
                {
                    // Teardown is in progress; a failed final send falls back to
                    // the gateway's liveness-timeout seat reclamation.
                }
                finally
                {
                    if (item.FromPool) ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);
                }
            }
        }

        private void TryReceive(int generation)
        {
            // Drain all available datagrams each iteration instead of
            // consuming only one.  At 30 Hz with 16 players up to 16 packets can
            // arrive within a single 33 ms window.  Reading only one per 1 ms cycle
            // adds up to 15 ms of queuing latency for late-arriving packets.
            //
            // Each receive rents a fresh buffer from ArrayPool — no shared
            // scratch + copy step.  The buffer is returned to the pool the
            // moment the synchronous subscriber chain returns.
            try
            {
                for (int i = 0; i < MaxReceivePerIteration && StillOwns(generation); i++)
                {
                    // ⛔ Same rule as the send drain, and the reason is sharper
                    // here: this pass parks inside the first poll by design, so a
                    // generation that advances during it left ninety-nine more
                    // datagrams to be delivered — measured — into the successor's
                    // session, through a receive path that is documented
                    // single-threaded and that drives the AEAD replay window.
                    //
                    // Block up to PollWaitMicros on the first poll so the thread
                    // parks until a datagram arrives; drain any further queued
                    // datagrams without blocking (Poll(0)) and stop when the socket
                    // runs dry.
                    if (!_transport.Poll(i == 0 ? PollWaitMicros : 0))
                    {
                        Interlocked.Increment(ref _pollMissCount);
                        break;
                    }
                    Interlocked.Increment(ref _pollHitCount);

                    var rented = ArrayPool<byte>.Shared.Rent(_receiveBufferSize);
                    int n;
                    try
                    {
                        n = _transport.Receive(rented);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        throw;
                    }

                    if (n == 0)
                    {
                        // Would-block / socket disposed mid-syscall.  Stop
                        // the drain pass; RunLoop polls again on the next
                        // iteration after a 1 ms sleep.
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        break;
                    }

                    if (n < 0)
                    {
                        // NetworkTransport.ReceiveSourceRejected: a datagram was
                        // consumed and dropped because the source endpoint
                        // failed pinning.  More datagrams may be queued —
                        // continue draining in this iteration so an
                        // off-path flood does not add per-burst latency to
                        // legitimate responses.  The inner loop is bounded
                        // by MaxReceivePerIteration, so a kernel that ever
                        // (incorrectly) reports readiness without data
                        // cannot pin the CPU.
                        //
                        // ⛔ The whole array is cleared here, not the prefix
                        // ReturnReceiveBuffer clears on the delivery path.  A
                        // negative return carries no length, and the shipped
                        // transport reaches this line having already read a
                        // full off-path datagram into the buffer — so there is
                        // nothing to bound the erasure by.  Same for the other
                        // error returns on this path.
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        if (n == NetworkTransport.ReceiveSourceRejected) continue;

                        // Only the documented sentinel means "dropped, keep
                        // draining".  Receive is a public extension point, and
                        // another negative from a custom transport is an
                        // unspecified condition rather than a rejected source —
                        // end the drain so an implementation returning a raw
                        // error code cannot spin this loop to its bound on
                        // every poll.  Counted and reported like the oversize
                        // case below: silently ending every pass would make a
                        // transport that never delivers a packet look like a
                        // network that never answers.
                        Interlocked.Increment(ref _perPacketFaultCount);
                        if (Interlocked.CompareExchange(ref _unspecifiedCountReported, 1, 0) == 0)
                        {
                            UnityEngine.Debug.LogError(
                                $"[RTMPE] NetworkThread: transport returned {n} from Receive, which " +
                                "is neither a byte count nor NetworkTransport.ReceiveSourceRejected " +
                                $"({NetworkTransport.ReceiveSourceRejected}) — drain pass ended.  " +
                                "Further occurrences are counted in PerPacketFaultCount.");
                        }
                        break;
                    }

                    if (n > rented.Length)
                    {
                        // A count past the end of the buffer we supplied would
                        // read unrelated pool memory into a packet and hand it
                        // to the parser as though the peer had sent it.  The
                        // shipped transport cannot produce this; a custom one
                        // can, so the frame is dropped rather than trusted.
                        //
                        // A transport that gets this wrong gets it wrong on
                        // every datagram, so the condition is reported once and
                        // tracked thereafter on the per-packet fault counter —
                        // an integrator who lost one frame to a bad count is
                        // not helped by four hundred more lines saying so.
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        Interlocked.Increment(ref _perPacketFaultCount);
                        if (Interlocked.CompareExchange(ref _oversizeCountReported, 1, 0) == 0)
                        {
                            UnityEngine.Debug.LogError(
                                $"[RTMPE] NetworkThread: transport reported {n} bytes into a " +
                                $"{rented.Length}-byte buffer — datagram dropped.  Further " +
                                "occurrences are counted in PerPacketFaultCount.");
                        }
                        break;
                    }

                    // Asked once more, immediately before delivery.  The poll
                    // above can park for a whole generation, and this is the
                    // line that puts a datagram into a session — the loop
                    // condition is too coarse to protect it, and the pass is
                    // committed to this iteration by the time it returns.
                    if (!StillOwns(generation))
                    {
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        return;
                    }

                    DispatchReceived(rented, n);
                }
            }
            catch (Exception ex)
            {
                // Same rule as the send path: a fault that describes one
                // inbound datagram ends the drain pass, not the session.  On a
                // connected UDP socket an ICMP port-unreachable for an earlier
                // send is delivered here, which happens on every gateway
                // restart — the next poll finds the socket working.
                if (!TransportFaultPolicy.IsSessionFatal(ex))
                {
                    Interlocked.Increment(ref _perPacketFaultCount);
                    return;
                }

                FailSession(generation, ex);
            }
        }

        // Hand the just-received datagram to subscribers, then return the
        // rented buffer to the shared pool exactly once.  The try/finally
        // guarantees return even if a subscriber throws.
        private void DispatchReceived(byte[] rented, int length)
        {
            // Snapshot delegates once — Action invocation is not racy with
            // concurrent subscribe/unsubscribe but reading the field twice
            // could observe different values.
            var rentedHandler = OnPacketReceivedRented;
            var legacyHandler = OnPacketReceived;

            try
            {
                if (rentedHandler != null)
                {
                    // Zero-copy delivery: one buffer, one synchronous call.
                    // After all subscribers return the buffer goes back to
                    // the pool and is reused on the next receive.
                    //
                    // Per-subscriber isolation: a buggy integrator subscriber
                    // that throws would otherwise (a) prevent every later
                    // subscriber from observing the packet for this datagram
                    // and (b) propagate out of DispatchReceived to TryReceive's
                    // outer catch, which fires OnError and tears down the
                    // receive loop.  Walk the invocation list explicitly so
                    // each subscriber's exception is caught and logged without
                    // affecting siblings.  Same discipline as M19-SYNC-01 and
                    // M19-CORE-07.
                    InvokeRentedSubscribers(rentedHandler, rented, length);
                }
                else if (legacyHandler != null)
                {
                    // Legacy contract guarantees an exclusively-owned array
                    // sized exactly to the datagram length.  This path still
                    // saves one copy vs. the original "shared scratch + new
                    // byte[n]" implementation: kernel writes directly into
                    // `rented` and we copy out once into the exact-sized array.
                    var packet = new byte[length];
                    Buffer.BlockCopy(rented, 0, packet, 0, length);
                    InvokeLegacySubscribers(legacyHandler, packet);
                }
            }
            finally
            {
                ReturnReceiveBuffer(rented, length);
            }
        }

        // Return a receive rental to the pool with no datagram bytes left in it.
        //
        // ⛔ `written` must be an upper bound on every byte anything put into
        // this rental, not merely the length the transport reported.  Those are
        // the same number for the shipped transport, and the obligation is
        // stated on `NetworkTransport.Receive`; a transport that writes more
        // than it returns leaves the excess in the pool for the next renter.
        // That is the one respect in which this is weaker than asking the pool
        // to zero the whole array, and it is the reason the sibling returns on
        // the error paths — where no trustworthy length exists — still do.
        //
        // The saving is why: the pool sizes rentals to a power of two, so a
        // receive buffer sized for a maximal datagram is served from a 64 KiB
        // array and every heartbeat paid for zeroing all of it.  Measured at
        // ~1.7 µs per small datagram against ~41 ns for its own length.
        //
        // ⚠️ The clear must precede the return.  Between the two the array is
        // still exclusively ours; after it, a concurrent renter may already
        // hold it.
        //
        // The length is clamped rather than trusted because this runs in a
        // `finally`, where a throw would leak the rental outright.
        private static void ReturnReceiveBuffer(byte[] rented, int written)
        {
            if (written > 0)
            {
                Array.Clear(rented, 0, Math.Min(written, rented.Length));
            }
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }

        // 🚨 A field EACH.  The two fan-outs report the same condition, but a
        // shared budget lets a flood of one decide whether the other is ever
        // seen — refused by `NoGateIsSharedBetweenTwoMembers`, and rightly.
        private static long s_lastRentedSubscriberThrowWarnTicks;
        private static long s_lastLegacySubscriberThrowWarnTicks;

        private static void InvokeRentedSubscribers(
            RentedPacketHandler handler, byte[] rented, int length)
        {
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    ((RentedPacketHandler)subs[i])(rented, 0, length);
                }
                catch (Exception ex)
                {
                    // One line per DATAGRAM, on the network thread, for a
                    // subscriber whose throw is deterministic (`RPC-RD-05`).
                    // ⛔ The `continue with remaining subscribers` behaviour is
                    // outside the gate: only the line is rate-limited.
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref s_lastRentedSubscriberThrowWarnTicks))
                        UnityEngine.Debug.LogError(
                            "[RTMPE] NetworkThread: rented-packet subscriber threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                            "remaining subscribers.");
                }
            }
        }

        private static void InvokeLegacySubscribers(Action<byte[]> handler, byte[] packet)
        {
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    ((Action<byte[]>)subs[i])(packet);
                }
                catch (Exception ex)
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref s_lastLegacySubscriberThrowWarnTicks))
                        UnityEngine.Debug.LogError(
                            "[RTMPE] NetworkThread: legacy packet subscriber threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                            "remaining subscribers.");
                }
            }
        }
    }
}
