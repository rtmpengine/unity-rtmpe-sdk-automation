// RTMPE SDK — Runtime/Infrastructure/Transport/SimulatedLinkTransport.cs
//
// A link simulator: a NetworkTransport that wraps the transport a session
// would otherwise run on and shapes what crosses it — delay, jitter, loss and
// optional reordering, in both directions — so prediction, reconciliation
// and the reliable ladder can be watched under a link the office network
// never provides.  Installed for a session through NetworkManager's transport
// factory (an integrator's own harness) or by the Editor's Link Simulator
// panel (Window > RTMPE > Network Debugger), which shapes whatever transport
// the session was going to use.
//
// It simulates the LINK, not the server: nothing here produces the gateway's
// backpressure, its receive window or its own drops.  A datagram it delivers
// is exactly the datagram the far side sent, later.
//
// Shape: two lanes, one per direction, each a queue ordered by the time a
// datagram is due.  Send() takes a copy of the datagram and puts it on the
// outbound lane; Poll() and Receive() pull whatever the inner transport has
// waiting onto the inbound lane, stamped with its due time, and drain both
// lanes — outbound to the inner transport's Send, inbound to the caller.  The
// lanes are drained only from these three calls, on the network thread that
// makes them, so nothing here runs a thread of its own and a datagram's
// delivery can be late by at most one poll interval (the network thread's
// PollWaitMicros, 4 ms) beyond the delay asked for.
//
// Threading: like every transport, driven from the network thread; the
// Conditions setter and the readings are also read and written from the
// main thread (the Editor panel), so every lane touch is under one lock.
// The inner transport's blocking Poll is called OUTSIDE that lock, so a
// Disconnect from another thread — which is how Stop() breaks a loop out of
// a long wait — is never held up by it.

using System;
using System.Diagnostics;
using System.Net.Sockets;
using RTMPE.Threading;

namespace RTMPE.Transport
{
    /// <summary>
    /// A <see cref="NetworkTransport"/> that shapes the link between the
    /// session and the transport it wraps: one-way delay, jitter, loss and
    /// optional reordering, applied to every datagram in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Link Simulator panel of <b>Window → RTMPE → Network Debugger</b>
    /// installs one around whatever transport the next session would have
    /// used. You can also install one from your own transport factory, set
    /// with <c>NetworkManager.SetTransportFactory</c>:
    /// <c>settings => new SimulatedLinkTransport(new UdpTransport(settings.serverHost, settings.serverPort), conditions)</c>.
    /// </para>
    /// <para>
    /// <see cref="Conditions"/> may be changed while a session runs; each
    /// datagram is shaped by the conditions in force when it is offered. A
    /// datagram the wrapped transport refuses when it is finally sent (a size
    /// it does not carry, an unreachable route) is dropped and counted in
    /// <see cref="Outbound"/>'s <see cref="LaneReadings.Refused"/>, because
    /// its sender has already returned; a full operating-system send buffer
    /// keeps the datagram for the next attempt. A fault that would end the
    /// session on the wrapped transport ends it here too.
    /// </para>
    /// <para>
    /// It allocates one array per datagram in each direction, so use it for
    /// testing rather than in builds you ship. It changes the link, not the
    /// server.
    /// </para>
    /// </remarks>
    public sealed class SimulatedLinkTransport : NetworkTransport
    {
        /// <summary>
        /// The most datagrams each direction holds. A datagram offered to a
        /// full direction is dropped and counted as
        /// <see cref="LaneReadings.Overflowed"/>.
        /// </summary>
        public const int MaxQueuedPerDirection = 4096;

        /// <summary>
        /// The most outbound datagrams sent when the transport is disconnected:
        /// the newest ones still waiting, in order. Older ones are dropped and
        /// counted as <see cref="LaneReadings.Discarded"/>.
        /// </summary>
        /// <remarks>
        /// The limit keeps a disconnect under a long delay from sending up to
        /// <see cref="MaxQueuedPerDirection"/> datagrams in one burst, which the
        /// server's per-address rate limit would refuse. The newest datagrams,
        /// which include the graceful disconnect, are the ones sent.
        /// </remarks>
        public const int MaxReleasedAtClose = 64;

        // How many datagrams one pull takes from the inner transport before
        // the caller gets an answer.  The network thread drains a hundred per
        // pass; a bench pulling more per call would only lengthen Poll.
        private const int MaxPulledPerCall = 256;

        // The largest datagram the inner transport can hand over: a UDP
        // payload cannot exceed this, and the pull buffer has to be at least
        // the inner's own delivery bound or the pull itself would truncate.
        private const int PullBufferBytes = 65_536;

        private readonly NetworkTransport _inner;
        private readonly Func<long> _clockMicros;
        private readonly Random _random;
        private readonly object _gate = new object();
        private readonly Lane _outbound = new Lane();
        private readonly Lane _inbound = new Lane();
        private readonly byte[] _pull = new byte[PullBufferBytes];
        private LinkConditions _conditions;
        private volatile bool _disposed;

        /// <summary>
        /// Wraps <paramref name="inner"/> and shapes its traffic with
        /// <paramref name="conditions"/>, drawing loss and jitter from a random
        /// generator seeded from the clock.
        /// </summary>
        /// <param name="inner">The transport to wrap; disposed with this one.</param>
        /// <param name="conditions">The conditions to apply.</param>
        /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
        public SimulatedLinkTransport(NetworkTransport inner, LinkConditions conditions)
            : this(inner, conditions, Environment.TickCount, null)
        {
        }

        /// <summary>
        /// Wraps <paramref name="inner"/> and shapes its traffic with
        /// <paramref name="conditions"/>, drawing loss and jitter from a random
        /// generator seeded with <paramref name="seed"/>: the same seed gives
        /// the same drops and delays for the same sequence of datagrams.
        /// </summary>
        /// <param name="inner">The transport to wrap; disposed with this one.</param>
        /// <param name="conditions">The conditions to apply.</param>
        /// <param name="seed">Seed for the random generator.</param>
        /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
        public SimulatedLinkTransport(NetworkTransport inner, LinkConditions conditions, int seed)
            : this(inner, conditions, seed, null)
        {
        }

        // The clock is a seam for tests: microseconds, monotonic.  Production
        // reads the Stopwatch.
        internal SimulatedLinkTransport(
            NetworkTransport inner, LinkConditions conditions, int seed, Func<long> clockMicros)
        {
            _inner       = inner ?? throw new ArgumentNullException(nameof(inner));
            _conditions  = conditions;
            _random      = new Random(seed);
            _clockMicros = clockMicros ?? StopwatchMicros;
        }

        /// <summary>The wrapped transport; disposed with this one.</summary>
        public NetworkTransport Inner => _inner;

        /// <summary>
        /// The conditions in force. May be set while a session runs; the next
        /// datagram offered in either direction is shaped by the new value.
        /// </summary>
        public LinkConditions Conditions
        {
            get { lock (_gate) return _conditions; }
            set { lock (_gate) _conditions = value; }
        }

        /// <summary>What the outbound direction (this client's sends) has done with its datagrams.</summary>
        public LaneReadings Outbound
        {
            get { lock (_gate) return _outbound.Snapshot(); }
        }

        /// <summary>What the inbound direction (datagrams the wrapped transport received) has done with its datagrams.</summary>
        public LaneReadings Inbound
        {
            get { lock (_gate) return _inbound.Snapshot(); }
        }

        /// <inheritdoc/>
        public override bool IsConnected => !_disposed && _inner.IsConnected;

        /// <inheritdoc/>
        public override System.Net.IPEndPoint LocalEndPoint => _inner.LocalEndPoint;

        /// <inheritdoc/>
        /// <remarks>
        /// Datagrams still waiting from a previous connection are dropped and
        /// counted as <see cref="LaneReadings.Discarded"/>.
        /// </remarks>
        public override void Connect()
        {
            ThrowIfDisposed();
            // A new life on the socket starts with empty lanes: a datagram
            // held from the previous one belongs to a session that is over.
            // Outside the lock, because Connect may block (DNS) and a
            // Disconnect from another thread is what ends a long one.
            ClearLanes();
            _inner.Connect();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Before the wrapped transport closes, the newest
        /// <see cref="MaxReleasedAtClose"/> outbound datagrams still waiting
        /// are sent in order, as a real link delivers what is already in flight;
        /// this lets the last packets sent before a disconnect, including the
        /// graceful disconnect, reach the server. Older outbound datagrams,
        /// and inbound datagrams still waiting, are dropped and counted as
        /// <see cref="LaneReadings.Discarded"/>.
        /// </remarks>
        public override void Disconnect()
        {
            ReleaseLanes();
            _inner.Disconnect();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The datagram is copied and passed to the wrapped transport once its
        /// delay has elapsed, unless the loss setting drops it.
        /// </remarks>
        public override void Send(byte[] data)
        {
            ThrowIfDisposed();
            if (data == null) throw new ArgumentNullException(nameof(data));

            long now = _clockMicros();
            lock (_gate)
            {
                // The caller owns its array and may reuse it the moment this
                // returns: the lane holds a copy, exact-sized because the
                // inner transport's Send sends the whole array it is given.
                Offer(_outbound, data, data.Length, now);
                DrainOutbound(now);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Returns a datagram the wrapped transport received once its delay has
        /// elapsed; a datagram the loss setting drops is never returned.
        /// </remarks>
        public override int Receive(byte[] buffer)
        {
            ThrowIfDisposed();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            long now = _clockMicros();
            Held held;
            lock (_gate)
            {
                DrainOutbound(now);
                PullInbound(now);
                if (!_inbound.TryPeekDue(now, out held)) return 0;
                _inbound.Pop();

                if (held.Bytes.Length > buffer.Length)
                {
                    // Never write past the buffer supplied.  The datagram is
                    // consumed and dropped, which is the one negative the
                    // receive loop is told to keep draining after.
                    _inbound.Refused++;
                    return ReceiveSourceRejected;
                }

                _inbound.Delivered(held);
            }

            // Exactly the datagram's length is written, so the count is the
            // bound on what was written that the receive path erases by.
            Buffer.BlockCopy(held.Bytes, 0, buffer, 0, held.Bytes.Length);
            return held.Bytes.Length;
        }

        /// <inheritdoc/>
        public override bool Poll(int microSeconds)
        {
            ThrowIfDisposed();

            long deadline = _clockMicros() + Math.Max(0, microSeconds);
            while (true)
            {
                long now = _clockMicros();
                long nextDue;
                lock (_gate)
                {
                    // A drain the kernel's send buffer stopped leaves its
                    // datagram due; waking for it again at once would spin on
                    // the same full buffer, so it is left out of the wait and
                    // met on the next call, after the park the direct path's
                    // backoff sleep would have taken.
                    bool sendBufferFull = DrainOutbound(now);
                    PullInbound(now);
                    if (_inbound.TryPeekDue(now, out _)) return true;
                    nextDue = EarliestDue(includeOutbound: !sendBufferFull);
                }

                long remaining = deadline - now;
                if (remaining <= 0) return false;

                // Park in the inner transport's poll for as long as the caller
                // allowed, or until the next held datagram comes due, whichever
                // is sooner — so a datagram due in one millisecond is delivered
                // in one millisecond, not at the end of a four-millisecond wait,
                // and a datagram the inner transport receives during the wait
                // is stamped when it arrives rather than when the wait ends.
                long wait = remaining;
                if (nextDue >= 0 && nextDue - now < wait) wait = Math.Max(0, nextDue - now);
                if (wait > 0)
                    _inner.Poll(wait > int.MaxValue ? int.MaxValue : (int)wait);
            }
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseLanes();
            _inner.Dispose();
        }

        // ── Lanes ──────────────────────────────────────────────────────────────

        // Shape one datagram under the conditions in force and put it on the
        // lane, or drop it there and then.  Under the lock.
        private void Offer(Lane lane, byte[] source, int length, long now)
        {
            LinkConditions c = _conditions;
            lane.Offered++;

            if (c.LossPercent > 0f && _random.NextDouble() * 100.0 < c.LossPercent)
            {
                lane.Lost++;
                return;
            }

            if (lane.Count >= MaxQueuedPerDirection)
            {
                lane.Overflowed++;
                return;
            }

            long holdMicros = c.DelayMs * 1000L;
            if (c.JitterMs > 0)
                holdMicros += (long)((_random.NextDouble() * 2.0 - 1.0) * c.JitterMs * 1000.0);
            if (holdMicros < 0) holdMicros = 0;

            long due = now + holdMicros;
            // Order kept: a datagram is never due before the one offered
            // ahead of it, so jitter bunches datagrams behind the latest due
            // rather than overturning them — what a queue on one path does.
            if (!c.Reorder && due < lane.LastDue) due = lane.LastDue;

            var bytes = new byte[length];
            Buffer.BlockCopy(source, 0, bytes, 0, length);
            lane.Add(new Held(due, bytes));
        }

        // Send every outbound datagram that has come due.  Under the lock.
        // Answers whether a full kernel send buffer ended the drain early.
        private bool DrainOutbound(long now)
        {
            while (_outbound.TryPeekDue(now, out Held held))
            {
                try
                {
                    _inner.Send(held.Bytes);
                }
                catch (SocketException sx) when (sx.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    // The kernel send buffer is full: transient, and the
                    // direct send path keeps its datagram for the next pass
                    // rather than losing it — so does this lane.  The drain
                    // ends here; the next call tries again.
                    _outbound.BufferExhausted++;
                    return true;
                }
                catch (Exception ex) when (!TransportFaultPolicy.IsSessionFatal(ex))
                {
                    // A refusal of this one datagram — the direct path counts
                    // it and carries on, and so does this lane.  Anything the
                    // policy calls fatal propagates to the loop that called us,
                    // which ends the session as it would for a direct send.
                    _outbound.Pop();
                    _outbound.Refused++;
                    continue;
                }

                _outbound.Pop();
                _outbound.Delivered(held);
            }

            return false;
        }

        // Take what the inner transport has waiting onto the inbound lane,
        // stamped as of now.  Non-blocking; bounded per call.  Under the lock.
        private void PullInbound(long now)
        {
            for (int i = 0; i < MaxPulledPerCall; i++)
            {
                if (!_inner.Poll(0)) return;

                int n = _inner.Receive(_pull);
                if (n == 0) return;
                if (n == ReceiveSourceRejected) continue;
                if (n < 0 || n > _pull.Length)
                {
                    // Neither a count nor the one documented sentinel: the
                    // receive loop ends its pass on this and counts it; the
                    // pull ends here for the same reason and counts it too —
                    // as offered and refused, so the lane's counts still add
                    // up to what it was offered.
                    _inbound.Offered++;
                    _inbound.Refused++;
                    return;
                }

                Offer(_inbound, _pull, n, now);
            }
        }

        // The earliest moment either lane has something due, or −1.
        private long EarliestDue(bool includeOutbound)
        {
            long due = -1;
            if (_inbound.TryPeekNext(out Held i)) due = i.Due;
            if (includeOutbound && _outbound.TryPeekNext(out Held o) && (due < 0 || o.Due < due)) due = o.Due;
            return due;
        }

        // Both lanes emptied for a new life on the socket, what they held
        // counted as discarded: neither lost to the link nor refused by it,
        // but belonging to a life that is over.
        private void ClearLanes()
        {
            lock (_gate)
            {
                _outbound.Discarded += _outbound.Count;
                _inbound.Discarded  += _inbound.Count;
                _outbound.Clear();
                _inbound.Clear();
            }
        }

        // The outbound lane's newest MaxReleasedAtClose sent, in its order and
        // ahead of their due times, the older counted as discarded, then both
        // lanes emptied — the close of a life on the socket.  A loop ending on
        // a session-fatal fault closes a dead socket, and the first such fault
        // the inner raises ends the sending rather than raising one per held
        // datagram — what was still to send is counted refused; a single
        // datagram the inner refuses is dropped and counted.  Nothing is owed
        // a throw at a close.
        private void ReleaseLanes()
        {
            lock (_gate)
            {
                long keepFrom = _outbound.OldestOfTheNewest(MaxReleasedAtClose);
                bool live = true;
                while (_outbound.TryPeekNext(out Held held))
                {
                    _outbound.Pop();
                    if (held.Sequence < keepFrom)
                    {
                        _outbound.Discarded++;
                        continue;
                    }

                    if (!live)
                    {
                        _outbound.Refused++;
                        continue;
                    }

                    try
                    {
                        _inner.Send(held.Bytes);
                        _outbound.Delivered(held);
                    }
                    catch (Exception ex)
                    {
                        _outbound.Refused++;
                        if (TransportFaultPolicy.IsSessionFatal(ex)) live = false;
                    }
                }
                _outbound.Clear();
                _inbound.Discarded += _inbound.Count;
                _inbound.Clear();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimulatedLinkTransport));
        }

        // Microseconds from the Stopwatch, split so the multiplication cannot
        // overflow a long on a host whose timestamp is already large.
        private static long StopwatchMicros()
        {
            long ticks = Stopwatch.GetTimestamp();
            long freq  = Stopwatch.Frequency;
            return ticks / freq * 1_000_000L + (ticks % freq) * 1_000_000L / freq;
        }

        // A datagram waiting on a lane: its copy, when it is due, and the
        // order it was offered in (for the reordering count).
        private readonly struct Held
        {
            public readonly long   Due;
            public readonly byte[] Bytes;
            public readonly long   Sequence;

            public Held(long due, byte[] bytes) : this(due, bytes, 0) { }

            public Held(long due, byte[] bytes, long sequence)
            {
                Due      = due;
                Bytes    = bytes;
                Sequence = sequence;
            }

            public Held WithSequence(long sequence) => new Held(Due, Bytes, sequence);
        }

        // One direction: a queue ordered by due time with a head index, so a
        // pop is O(1) and an in-order add (the common case — order kept, or
        // jitter smaller than the spacing) is an append.
        private sealed class Lane
        {
            private readonly System.Collections.Generic.List<Held> _items =
                new System.Collections.Generic.List<Held>();
            private int  _head;
            private long _nextSequence;
            private long _maxDeliveredSequence = -1;

            public long Offered, Lost, Overflowed, Refused, Discarded, BufferExhausted;
            private long _delivered, _reordered;

            /// <summary>The latest due time any datagram on this lane was given.</summary>
            public long LastDue { get; private set; }

            public int Count => _items.Count - _head;

            public void Add(Held held)
            {
                held = held.WithSequence(_nextSequence++);
                if (held.Due > LastDue) LastDue = held.Due;

                // Stable insertion by due time among the live entries: after
                // every entry due at or before this one.
                int lo = _head, hi = _items.Count;
                while (lo < hi)
                {
                    int mid = lo + ((hi - lo) >> 1);
                    if (_items[mid].Due <= held.Due) lo = mid + 1; else hi = mid;
                }
                _items.Insert(lo, held);
            }

            public bool TryPeekDue(long now, out Held held)
            {
                if (!TryPeekNext(out held)) return false;
                return held.Due <= now;
            }

            public bool TryPeekNext(out Held held)
            {
                if (Count == 0)
                {
                    held = default;
                    return false;
                }
                held = _items[_head];
                return true;
            }

            public void Pop()
            {
                _items[_head] = default;
                _head++;
                // Compact once the spent prefix is most of the list; the
                // memmove is then paid once per many pops rather than per pop.
                if (_head >= 64 && _head * 2 >= _items.Count)
                {
                    _items.RemoveRange(0, _head);
                    _head = 0;
                }
            }

            /// <summary>
            /// The offer sequence of the oldest datagram among the newest
            /// <paramref name="newest"/> this lane holds — every one at or
            /// above it is among them — or <see cref="long.MinValue"/> when the
            /// lane holds no more than that.
            /// </summary>
            /// <remarks>
            /// By offer order, not by position: under jitter the lane is
            /// ordered by due time, and a datagram offered later can sit ahead
            /// of one offered earlier.  Read once per close, so the sort's
            /// allocation is paid there and nowhere on the send path.
            /// </remarks>
            public long OldestOfTheNewest(int newest)
            {
                int count = Count;
                if (count <= newest) return long.MinValue;

                var sequences = new long[count];
                for (int i = 0; i < count; i++) sequences[i] = _items[_head + i].Sequence;
                Array.Sort(sequences);
                return sequences[count - newest];
            }

            public void Delivered(Held held)
            {
                _delivered++;
                if (held.Sequence < _maxDeliveredSequence) _reordered++;
                else _maxDeliveredSequence = held.Sequence;
            }

            public void Clear()
            {
                _items.Clear();
                _head = 0;
                LastDue = 0;
                // The counts stay: they describe the transport's life, and a
                // reconnect that emptied them would hide what the link did to
                // the attempt before it.
            }

            public LaneReadings Snapshot() => new LaneReadings(
                Offered, Lost, _delivered, Overflowed, _reordered, Refused, Discarded, BufferExhausted, Count);
        }
    }

    /// <summary>
    /// What one direction of a <see cref="SimulatedLinkTransport"/> has done
    /// with the datagrams offered to it since the transport was created. At
    /// any moment, <see cref="Offered"/> equals <see cref="Lost"/> +
    /// <see cref="Overflowed"/> + <see cref="Delivered"/> + <see cref="Refused"/>
    /// + <see cref="Discarded"/> + <see cref="Queued"/>.
    /// </summary>
    public readonly struct LaneReadings
    {
        internal LaneReadings(
            long offered, long lost, long delivered, long overflowed, long reordered,
            long refused, long discarded, long bufferExhausted, int queued)
        {
            Offered         = offered;
            Lost            = lost;
            Delivered       = delivered;
            Overflowed      = overflowed;
            Reordered       = reordered;
            Refused         = refused;
            Discarded       = discarded;
            BufferExhausted = bufferExhausted;
            Queued          = queued;
        }

        /// <summary>
        /// Datagrams offered to this direction. Inbound, this includes an
        /// invalid receive result from the wrapped transport, which is counted
        /// as offered and refused.
        /// </summary>
        public long Offered { get; }

        /// <summary>Datagrams dropped by the loss setting.</summary>
        public long Lost { get; }

        /// <summary>Datagrams passed on: to the wrapped transport (outbound) or to the SDK (inbound).</summary>
        public long Delivered { get; }

        /// <summary>Datagrams dropped because the direction was full (<see cref="SimulatedLinkTransport.MaxQueuedPerDirection"/>).</summary>
        public long Overflowed { get; }

        /// <summary>Datagrams delivered after a datagram that was offered later.</summary>
        public long Reordered { get; }

        /// <summary>
        /// Datagrams refused at delivery. Outbound: refused by the wrapped
        /// transport, or left unsent because a fatal fault ended a disconnect.
        /// Inbound: larger than the buffer offered for it, or an invalid
        /// receive result from the wrapped transport.
        /// </summary>
        public long Refused { get; }

        /// <summary>
        /// Datagrams dropped because the connection they belonged to ended: on
        /// a disconnect or dispose, the outbound datagrams older than the newest
        /// <see cref="SimulatedLinkTransport.MaxReleasedAtClose"/> and every
        /// inbound datagram still waiting; on a new
        /// <see cref="SimulatedLinkTransport.Connect"/>, whatever either
        /// direction still held.
        /// </summary>
        public long Discarded { get; }

        /// <summary>Outbound only: sends postponed because the operating system's send buffer was full; the datagram was kept.</summary>
        public long BufferExhausted { get; }

        /// <summary>Datagrams waiting in this direction now.</summary>
        public int Queued { get; }
    }
}
