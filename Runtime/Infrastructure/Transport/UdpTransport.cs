// RTMPE SDK — Runtime/Infrastructure/Transport/UdpTransport.cs
//
// Non-blocking UDP socket transport.
//
// Design notes:
// • Blocking = false + Poll(0) avoids any blocking system call on the hot path.
// • SendTo / ReceiveFrom are used (not Connect+Send/Receive) to avoid the implicit
//   UDP "connection" state that can trigger ICMP port-unreachable errors on some OSes.
// • SocketError.WouldBlock / ConnectionReset are silently swallowed per RFC 1122;
//   the receive loop simply returns 0 bytes and retries next iteration.
// • An IDisposable _disposed guard prevents double-dispose races on shutdown.
//
// Concurrency model:
//  The socket lifetime is racy by design: Disconnect() may be called from any
//  thread (typically the main thread on shutdown) while the network background
//  thread is parked inside Poll() or ReceiveFrom().  Rather than serialise the
//  hot syscall paths under a lock — which would defeat the non-blocking design
//  and risk deadlocks if Dispose() ran on the same thread that holds the lock —
//  we treat disposal as racing with the next syscall and tolerate it:
//
//    1. _socket is read into a local variable once per call ("snapshot").  Any
//       subsequent Disconnect() that nulls the field cannot turn the local
//       reference into null mid-syscall, so the NullReferenceException class
//       of bug is eliminated.
//    2. ObjectDisposedException and the "racing close" SocketError variants are
//       caught and converted into a benign return (false / 0).  The caller
//       loop checks _running on the next iteration and exits cleanly.
//
// This is the conventional .NET idiom for closing a socket from a thread other
// than the one parked in the syscall — the same pattern used by Kestrel,
// SignalR and the BCL's own SocketAsyncEventArgs reference implementations.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RTMPE.Transport
{
    /// <summary>
    /// The built-in, non-blocking UDP transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The network thread calls <c>Send</c>, <see cref="Poll"/> and
    /// <see cref="Receive"/>; <see cref="Disconnect"/> and
    /// <see cref="Dispose"/> may be called from another thread at the same
    /// time.
    /// </para>
    /// <para>
    /// The host is resolved at the first <see cref="Connect"/> and the address
    /// is reused by later ones; an IPv4 address is preferred, and IPv6 is used
    /// when there is none. Datagrams that do not come from the server's
    /// address and port are dropped, and temporary socket errors (no data
    /// waiting, an unreachable port or route) are treated as "no data".
    /// </para>
    /// </remarks>
    public sealed class UdpTransport : NetworkTransport
    {
        // ── Configuration (immutable after construction) ───────────────────────
        private readonly string _host;
        private readonly int    _port;
        private readonly int    _sendBufferBytes;
        private readonly int    _receiveBufferBytes;
        private readonly TimeSpan _dnsTimeout;
        private readonly int    _maxDatagramSize;

        /// <summary>
        /// The default largest datagram <c>Send</c> accepts: 1200
        /// bytes. That fits the IPv6 minimum link MTU (1280 bytes) after IP and
        /// UDP headers, and survives tunnels such as PPPoE and IPsec that
        /// shrink the usable size below Ethernet's 1500 bytes. A larger
        /// datagram is refused rather than fragmented, since a fragmented
        /// datagram is lost whenever one fragment is.
        /// </summary>
        public const int DefaultMaxDatagramSize = 1200;

        /// <summary>
        /// Default time <see cref="Connect"/> waits for DNS resolution: 3
        /// seconds.
        /// </summary>
        public static readonly TimeSpan DefaultDnsTimeout = TimeSpan.FromSeconds(3);

        // Cached resolved address for the connection lifetime.  DNS is resolved
        // once at Connect() and reused across socket reconstructions.  Cache is
        // cleared on Dispose() but otherwise persists (UDP DNS TTL is irrelevant
        // for an already-bound session).
        private IPAddress    _cachedAddress;
        private AddressFamily _cachedFamily;

        // ── Runtime state ──────────────────────────────────────────────────────
        // _socket is volatile so that a Disconnect() on one thread is immediately
        // visible to readers on the network thread without an explicit fence.
        // Readers must still snapshot the field locally — see class header.
        //
       // _remoteEndPoint, _localEndPoint and _socketFamily are written by the
        // main-thread Connect() and read by the network thread inside the
        // syscalls below.  EndPoint is a reference type, so torn reads cannot
        // produce a partially-initialised object — but reordering across the
        // _socket publish is still possible on weak memory models (ARM /
        // IL2CPP).  Volatile.Read / Volatile.Write below pair with
        // Volatile.Write(_socket, …) inside Connect() so a thread that observes
        // the new socket also observes the matching endpoint state.
        private volatile Socket _socket;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test seam: invoked inside <see cref="Connect"/> immediately after the
        /// socket is committed to <c>_socket</c> and before this method reads the
        /// binding back.  Compiled only into test-runner builds.
        /// </summary>
        /// <remarks>
        /// S4-35 — a <c>Disconnect()</c> landing in that window is what
        /// <c>NetworkThread.Stop</c> issues when its 2 s join times out, and this
        /// method can be inside a 3 s DNS resolution when it does.  The window is a
        /// few instructions wide, so a fixture can only be IN it by being invited.
        /// </remarks>
        internal static Action<UdpTransport> AfterSocketCommittedForTest;

        /// <summary>
        /// Test seam: the instant between binding a socket and committing it —
        /// where a Dispose, or a later Connect, has already happened while this
        /// one was resolving.  Compiled only into test-runner builds.
        /// </summary>
        /// <remarks>
        /// Both seams are process-wide and are invoked by EVERY transport's
        /// Connect, including the transports of test classes running in
        /// parallel; each hands the fixture the instance, and a fixture acts
        /// only on its own.
        /// </remarks>
        internal static Action<UdpTransport> BeforeSocketCommittedForTest;
#endif
        // Serialises the commit of a socket against a later Connect, against
        // Disconnect and against Dispose.  Connect resolves the host for up to
        // DefaultDnsTimeout, and in that time the transport may have been
        // disposed, disconnected — NetworkThread.Stop issues a Disconnect to
        // break a loop out of exactly that Connect — or handed to a successor
        // thread whose own Connect has bound a socket: a commit landing then
        // would leave a bound socket nobody reads, or replace the successor's
        // socket and endpoint underneath its run loop.  A Connect commits only
        // if nothing has superseded it and the transport is still alive; held
        // for a few field flips, never across a syscall.
        private readonly object _connectGate = new object();
        private int             _connectGeneration;
        private EndPoint        _remoteEndPoint;
        private int             _socketFamilyRaw = (int)AddressFamily.InterNetwork; // reflects the active socket
        private volatile bool   _disposed;
        // Populated by Connect() after the socket is bound.
        // Reflects the actual outgoing source IP (discovered via a routing probe),
        // not 0.0.0.0 that would result from Bind(IPAddress.Any, 0).
        private System.Net.IPEndPoint _localEndPoint;

        private AddressFamily SocketFamily
        {
            get => (AddressFamily)Volatile.Read(ref _socketFamilyRaw);
            set => Volatile.Write(ref _socketFamilyRaw, (int)value);
        }

        // ── Properties ─────────────────────────────────────────────────────────
        /// <inheritdoc/>
        /// <remarks>
        /// Informational only: another thread can disconnect or dispose the
        /// transport at any moment, so do not use this property to synchronise
        /// with those calls.
        /// </remarks>
        public override bool IsConnected => _socket != null && !_disposed;

        /// <summary>
        /// The local endpoint of the bound socket: the address of the outgoing
        /// network interface (loopback when the route cannot be determined) and
        /// the port the operating system assigned. <see langword="null"/> before
        /// <see cref="Connect"/> and after <see cref="Disconnect"/>.
        /// </summary>
        public override System.Net.IPEndPoint LocalEndPoint => Volatile.Read(ref _localEndPoint);

        // ── Construction ───────────────────────────────────────────────────────

        // Default kernel socket buffer.  4 KiB (the previous default) holds
        // only ~3 MTU-sized datagrams — at a 30 Hz tick with 16 players the
        // session bursts ~480 datagrams/second and routinely overflows
        // SO_RCVBUF, producing silent kernel-side drops.  256 KiB
        // accommodates >200 datagrams in flight, comfortably absorbing the
        // worst tick-aligned burst that real games produce while staying
        // well under the per-socket rmem_max default on modern Linux/Windows.
        // Tunable via NetworkSettings.sendBufferBytes / receiveBufferBytes.
        /// <summary>
        /// Default size of the operating system's send and receive buffers for
        /// the socket: 262144 bytes (256 KiB).
        /// </summary>
        public const int DefaultSocketBufferBytes = 262_144;

        /// <summary>
        /// Creates a transport for the server at <paramref name="host"/> and
        /// <paramref name="port"/>. Nothing is resolved or opened until
        /// <see cref="Connect"/>.
        /// </summary>
        /// <param name="host">Server host name or IP address (for example "127.0.0.1").</param>
        /// <param name="port">Server UDP port (1–65535).</param>
        /// <param name="sendBufferBytes">Size of the socket's send buffer, in bytes.</param>
        /// <param name="receiveBufferBytes">Size of the socket's receive buffer, in bytes.</param>
        /// <param name="dnsTimeout">
        /// Longest time <see cref="Connect"/> waits for DNS resolution before it
        /// throws <see cref="TimeoutException"/>. <see langword="null"/> uses
        /// <see cref="DefaultDnsTimeout"/> (3 seconds).
        /// </param>
        /// <param name="maxDatagramSize">
        /// Largest datagram <c>Send</c> accepts, in bytes (1–65507).
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="host"/> is null, empty or blank.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="port"/> is outside 1–65535, <paramref name="dnsTimeout"/>
        /// is not positive, or <paramref name="maxDatagramSize"/> is outside 1–65507.
        /// </exception>
        public UdpTransport(
            string host,
            int    port,
            int    sendBufferBytes    = DefaultSocketBufferBytes,
            int    receiveBufferBytes = DefaultSocketBufferBytes,
            TimeSpan? dnsTimeout      = null,
            int    maxDatagramSize    = DefaultMaxDatagramSize)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host must not be null or whitespace.", nameof(host));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be in range 1–65535.");
            var effectiveTimeout = dnsTimeout ?? DefaultDnsTimeout;
            if (effectiveTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(dnsTimeout), "DNS timeout must be positive.");
            if (maxDatagramSize <= 0 || maxDatagramSize > 65_507)
                throw new ArgumentOutOfRangeException(nameof(maxDatagramSize),
                    "maxDatagramSize must be in range 1–65507 (UDP payload limit).");

            _host               = host;
            _port               = port;
            _sendBufferBytes    = sendBufferBytes;
            _receiveBufferBytes = receiveBufferBytes;
            _dnsTimeout         = effectiveTimeout;
            _maxDatagramSize    = maxDatagramSize;
        }

        /// <summary>
        /// Largest datagram <c>Send</c> accepts, in bytes; a larger one
        /// throws <see cref="ArgumentException"/>.
        /// </summary>
        public int MaxDatagramSize => _maxDatagramSize;

        // ── NetworkTransport ───────────────────────────────────────────────────

        /// <inheritdoc/>
        /// <exception cref="TimeoutException">DNS resolution took longer than the DNS timeout.</exception>
        public override void Connect()
        {
            // Connect is re-callable across reconnect attempts.  Close any socket
            // left bound by a prior attempt before binding a new one, so a caller
            // that re-enters Connect without an intervening Disconnect cannot
            // orphan the previous OS file descriptor.  The same atomic exchange as
            // Disconnect is used so a concurrent teardown still nulls the field
            // exactly once and the loser disposes nothing.  This call becomes the
            // latest Connect in the same step, so an earlier one still resolving
            // cannot commit over what this one binds.
            Socket stale;
            int    generation;
            lock (_connectGate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(UdpTransport));
                generation = ++_connectGeneration;
                stale = System.Threading.Interlocked.Exchange(ref _socket, null);
            }
            stale?.Dispose();

            // Resolve once per UdpTransport lifetime.  Reusing the cached IP across
            // reconnects keeps the captive-portal stall (where Dns.GetHostAddresses
            // can block 5–30s) bounded to the very first Connect of this instance.
            // The cache is cleared on Dispose so a freshly constructed transport
            // always re-resolves.
            IPAddress resolved = _cachedAddress;
            AddressFamily family = _cachedFamily;

            if (resolved == null)
            {
                IPAddress[] addresses;
                try
                {
                    addresses = ResolveHostAddresses(_host, _dnsTimeout);
                }
                catch (AggregateException ae) when (ae.InnerException != null)
                {
                    // Async DNS task surfaces failures wrapped in AggregateException;
                    // unwrap to preserve the SocketException type that callers expect.
                    throw ae.InnerException;
                }

                // Prefer IPv4, but fall back to IPv6 if no IPv4 address is available.
                // Previous code threw InvalidOperationException on IPv6-only hosts.
                family = AddressFamily.InterNetwork;
                foreach (var addr in addresses)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        resolved = addr;
                        break;
                    }
                }

                if (resolved == null)
                {
                    // No IPv4 — try IPv6.
                    foreach (var addr in addresses)
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            resolved = addr;
                            family   = AddressFamily.InterNetworkV6;
                            break;
                        }
                    }
                }

                if (resolved == null)
                    throw new InvalidOperationException(
                        $"No usable address found for host '{_host}'. " +
                        $"Resolved {addresses.Length} address(es), none IPv4 or IPv6.");
            }

            // Construct, configure and bind under a Dispose-on-failure guard.
            // Any exception thrown by the property setters (setsockopt) or by
            // Bind() must not leak the underlying OS file descriptor.  The
            // "transfer of ownership" pattern — assign to local, commit by
            // nulling the local — is the standard idiom for two-phase
            // construction of disposable resources in .NET.
            Socket pending   = null;
            Socket committed = null;
            try
            {
                pending = new Socket(family, SocketType.Dgram, ProtocolType.Udp)
                {
                    SendBufferSize    = _sendBufferBytes,
                    ReceiveBufferSize = _receiveBufferBytes,
                    Blocking          = false
                };

                // Bind to any local address/port — the OS assigns an ephemeral source port.
                var bindAny = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                pending.Bind(new IPEndPoint(bindAny, 0));

#if UNITY_INCLUDE_TESTS
                BeforeSocketCommittedForTest?.Invoke(this);
#endif
                // Commit ownership atomically, and only while this is still the
                // Connect the transport is waiting on: resolving and binding took
                // time, and in it the transport may have been disposed or taken
                // over by a later Connect (see _connectGate).  A socket that may
                // not be committed is this call's alone and is closed by the
                // finally.  The address, the family and the remote endpoint are
                // published here, under the same refusal: a Connect superseded
                // while resolving must not leave its answer in the cache or in
                // the endpoint the successor's session sends to.  (Where the
                // cache was already populated the write is the same value
                // again.)  Once _socket holds the reference the finally block
                // must not dispose it.  Family is published BEFORE the socket
                // itself so a reader that observes the new socket also sees the
                // matching family — Volatile.Write on _socket then provides the
                // release fence.
                lock (_connectGate)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(UdpTransport));
                    if (generation != _connectGeneration)
                        throw new InvalidOperationException(
                            "A later Connect, or a Disconnect, took this transport over while this one was resolving.");
                    _cachedAddress = resolved;
                    _cachedFamily  = family;
                    // Volatile.Write so that a thread that subsequently observes
                    // the freshly-published _socket also observes the matching
                    // remote endpoint state — paired with Volatile.Read in Send /
                    // Receive.
                    Volatile.Write(ref _remoteEndPoint, new IPEndPoint(resolved, _port));
                    SocketFamily = family;
                    _socket      = pending;
                }
                // ⛔ The snapshot this method uses from here on.  Rule 1 at the top
                // of this file — "_socket is read into a local variable once per
                // call" — applies to Connect as much as to the syscall paths, and
                // the line below it used to break: it read the FIELD back, and
                // Disconnect nulls that field with an Interlocked.Exchange from
                // another thread.  That window is not theoretical (S4-35):
                // NetworkThread.Stop gives a run loop 2 s to leave, and this method
                // can sit in DNS resolution for 3 s (DefaultDnsTimeout), after
                // which Stop calls Disconnect on this very transport precisely to
                // break the loop out — so the field is nulled underneath a Connect
                // that is still finishing.
                committed    = pending;
                pending      = null;
#if UNITY_INCLUDE_TESTS
                // Test seam: the instant between committing the socket and reading
                // it back — the window S4-35 is about, and one a fixture cannot
                // otherwise land in because it is a few instructions wide.
                AfterSocketCommittedForTest?.Invoke(this);
#endif
            }
            finally
            {
                // If we did not reach the commit point above, pending still owns
                // the half-initialised socket and must be disposed.  Otherwise
                // pending was nulled and this is a no-op.
                pending?.Dispose();
            }

            // Discover the actual outgoing source IP via a temporary routing probe.
            // Socket.Connect for UDP just records the destination and triggers the
            // kernel routing table lookup without sending any data. Reading
            // LocalEndPoint after connect gives the real outgoing interface IP
            // (not 0.0.0.0/[::] that Bind(Any) would produce).
            int boundPort;
            try
            {
                boundPort = ((IPEndPoint)committed.LocalEndPoint).Port;
            }
            catch (ObjectDisposedException)
            {
                // Rule 2 at the top of this file: a socket closed underneath a
                // syscall is a benign racing close, not a fault.  The snapshot
                // above turned this from a NullReferenceException on the network
                // thread into a disposed socket in hand, and here it becomes the
                // return the caller's loop is already written for — it observes
                // _running (or its generation) on the next poll and exits.
                //
                // ⚠️ _localEndPoint stays null, deliberately: this transport has no
                // local address, because it has no socket.  The handshake coroutine
                // waits on that field and its watchdog ends the attempt, which is
                // the truthful outcome for a connect whose socket was closed.
                return;
            }

            var loopback  = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            try
            {
                using var probe = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(_remoteEndPoint);
                var probeLocal = probe.LocalEndPoint as IPEndPoint;
                if (probeLocal != null)
                {
                    Volatile.Write(ref _localEndPoint, new IPEndPoint(probeLocal.Address, boundPort));
                }
                else
                {
                    // Should not happen on any supported platform, but guard defensively.
                    UnityEngine.Debug.LogWarning(
                        "[RTMPE] UdpTransport: routing probe returned a null LocalEndPoint " +
                        "after connect — falling back to loopback as this client's " +
                        "reported address. No address is bound into the handshake, so " +
                        "authentication is unaffected; a loopback answer here usually " +
                        "means datagrams are not leaving the host at all.");
                    Volatile.Write(ref _localEndPoint, new IPEndPoint(loopback, boundPort));
                }
            }
            catch (Exception ex)
            {
                // The routing probe is a best-effort kernel lookup (no data is sent).
                // It can fail on hosts with no default route (isolated test containers,
                // offline CI, certain mobile network transitions).
                // When it does, we fall back to loopback as the address this client
                // reports for itself.  Nothing in the handshake is bound to it:
                // HandshakeInit seals the API key with an EMPTY AAD
                // (SealedApiKeyCipher: aad=∅), so authentication does not depend on
                // the probe's answer.  🚨 Two messages here promised an AEAD failure
                // that cannot occur, which sent a reader hunting a crypto fault for
                // what is a routing one.  What a loopback answer usually means is
                // that the datagrams are not leaving the host — log that instead.
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] UdpTransport: routing probe failed " +
                    $"({ex.GetType().Name}: {ex.Message}). " +
                    "Falling back to loopback as this client's reported address. " +
                    "The handshake still authenticates — no address is bound into it — " +
                    "but datagrams may not be leaving this host; expected in isolated " +
                    "test environments with no default route.");
                Volatile.Write(ref _localEndPoint, new IPEndPoint(loopback, boundPort));
            }
        }

        /// <inheritdoc/>
        public override void Disconnect()
        {
            // Dispose() calls Close() internally; calling both is redundant and may throw.
            // Disconnect is idempotent — concurrent callers race only to null the
            // field; whoever wins disposes, the loser sees null and returns.
            // The network thread parked in ReceiveFrom/Poll on the doomed socket
            // unblocks with ObjectDisposedException, which Receive/Poll catch
            // and convert into a benign zero-return.
            //
            // A Connect still resolving when this runs is superseded with the
            // socket it would have bound: after a Disconnect nothing of this
            // transport is connected, and NetworkThread.Stop issues one for
            // exactly that Connect — a commit landing after it would be a bound
            // socket no loop reads until the next attempt or the Dispose.
            Socket s;
            lock (_connectGate)
            {
                _connectGeneration++;
                s = System.Threading.Interlocked.Exchange(ref _socket, null);
            }
            s?.Dispose();

            // Drop the bound-address witness with the socket that produced it.
            // LocalEndPoint is what the connect-timeout diagnostic reads to
            // decide whether the client ever reached the wire; left standing
            // after teardown it reports the previous socket's binding, and a
            // later attempt that never bound at all is misdiagnosed as one that
            // bound and got no answer.
            Volatile.Write(ref _localEndPoint, null);
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="data"/> is longer than <see cref="MaxDatagramSize"/>.</exception>
        public override void Send(byte[] data)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            // Reject oversize datagrams synchronously at the call site.  Without
            // this check the kernel surfaces EMSGSIZE asynchronously on the I/O
            // thread, which is reported through OnError far away from the bad
            // caller — much harder to diagnose.
            if (data.Length > _maxDatagramSize)
                throw new ArgumentException(
                    $"Datagram length {data.Length} exceeds MaxDatagramSize ({_maxDatagramSize}). " +
                    "Fragment at the application layer instead of relying on IP fragmentation.",
                    nameof(data));

            // Snapshot the field; if Disconnect() races with us, the local
            // reference keeps the socket alive for the duration of the syscall
            // (Dispose() releases the OS handle but the GC root is still held).
            var s = _socket;
            if (s == null)
                throw new InvalidOperationException("Transport is not connected. Call Connect() first.");
            var remote = Volatile.Read(ref _remoteEndPoint);

            try
            {
                // `Socket.SendTo` for UDP returns the number of bytes accepted by the
                // kernel.  For datagram sockets this is either the full payload
                // length or a SocketException is thrown (EMSGSIZE for oversize,
                // ENOBUFS for send-buffer exhaustion, etc.).  Microsoft's contract
                // does not formally permit a partial return, but this check is
                // cheap and catches platform quirks (e.g. Mono/IL2CPP edge cases)
                // before the symptom manifests as mysteriously dropped packets.
                int sent = s.SendTo(data, remote);
                if (sent != data.Length)
                {
                    throw new SocketException((int)SocketError.MessageSize);
                }
            }
            catch (SocketException sx)
                when (sx.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                // ENOBUFS — kernel send buffer exhausted.  Distinct from
                // MessageSize: the datagram is well-formed and would
                // succeed if the kernel had room.  Increment the dedicated
                // drop counter so a saturated uplink is observable
                // separately from oversized-payload bugs, then rethrow the
                // original SocketException so existing callers that
                // distinguish on SocketErrorCode continue to do so.
                Interlocked.Increment(ref _sendBufferExhaustedCount);
                throw;
            }
            catch (ObjectDisposedException)
            {
                // Disconnect() raced with us. Treat as a transport-closed signal —
                // the calling I/O loop is responsible for noticing and exiting.
                throw new InvalidOperationException("Transport was disconnected during Send.");
            }
        }

        // Cumulative count of Send calls that hit ENOBUFS.  A non-zero
        // sustained rate indicates uplink saturation — operators can
        // distinguish "too many packets" from "packets too large" without
        // parsing per-call exceptions.
        private long _sendBufferExhaustedCount;

        /// <summary>
        /// Number of sends that found the operating system's send buffer full
        /// (ENOBUFS); the send still throws. Never reset.
        /// </summary>
        public long SendBufferExhaustedCount =>
            Interlocked.Read(ref _sendBufferExhaustedCount);

        // The source-rejected sentinel is declared on NetworkTransport, which is
        // the contract the receive loop enforces against every implementation.
        // It is inherited here, so `UdpTransport.ReceiveSourceRejected` still
        // resolves for any existing caller; a second declaration would shadow
        // the base one and let the two drift apart.

        // Cumulative count of inbound datagrams dropped because the source
        // endpoint did not match the registered remote.  A non-zero value
        // signals an off-path attacker or routing oddity; non-resetting so
        // operators can correlate with session lifetime.
        private long _droppedSourceMismatchCount;

        /// <summary>
        /// Number of received datagrams dropped because they did not come from
        /// the server's address and port. Never reset.
        /// </summary>
        public long DroppedSourceMismatchCount =>
            Interlocked.Read(ref _droppedSourceMismatchCount);

        /// <summary>
        /// Sends <paramref name="count"/> bytes of <paramref name="buffer"/>,
        /// starting at <paramref name="offset"/>, as one datagram without
        /// copying them, for example from a buffer rented from
        /// <see cref="System.Buffers.ArrayPool{T}"/>.
        /// </summary>
        /// <param name="buffer">The buffer holding the datagram.</param>
        /// <param name="offset">Index of the datagram's first byte in <paramref name="buffer"/>.</param>
        /// <param name="count">Number of bytes to send.</param>
        /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="offset"/> and <paramref name="count"/> do not describe a range inside <paramref name="buffer"/>.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="count"/> is greater than <see cref="MaxDatagramSize"/>.</exception>
        /// <exception cref="InvalidOperationException">The transport is not connected, or was disconnected during the send.</exception>
        public void Send(byte[] buffer, int offset, int count)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            // Bounds expressed in subtraction form (`count > available`) so
            // a pathologically large pair (offset, count) cannot wrap
            // `offset + count` to a negative int that bypasses the
            // `> buffer.Length` test.  Same overflow class closed across
            // every parser in the SDK; keeping the trust boundary precise
            // here lets a misuse surface as a clean
            // ArgumentOutOfRangeException at the call site rather than as
            // a SocketException from deep inside SendTo.
            if (offset < 0 || count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    $"offset={offset}, count={count}, buffer.Length={buffer.Length}");
            if (count > _maxDatagramSize)
                throw new ArgumentException(
                    $"Datagram length {count} exceeds MaxDatagramSize ({_maxDatagramSize}). " +
                    "Fragment at the application layer instead of relying on IP fragmentation.",
                    nameof(count));

            var s = _socket;
            if (s == null)
                throw new InvalidOperationException("Transport is not connected. Call Connect() first.");
            var remote = Volatile.Read(ref _remoteEndPoint);

            try
            {
                // See the note in Send(byte[]) for why the return value is asserted.
                int sent = s.SendTo(buffer, offset, count, SocketFlags.None, remote);
                if (sent != count)
                {
                    throw new SocketException((int)SocketError.MessageSize);
                }
            }
            catch (SocketException sx)
                when (sx.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                // Mirror the un-sliced overload's accounting so the same
                // counter measures uplink saturation regardless of which
                // overload the caller picked.  Rethrow so the caller can
                // distinguish ENOBUFS from other SocketExceptions and apply
                // backoff (DrainSendQueue does so).
                Interlocked.Increment(ref _sendBufferExhaustedCount);
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw new InvalidOperationException("Transport was disconnected during Send.");
            }
        }

        /// <inheritdoc/>
        public override int Receive(byte[] buffer)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));

            // Snapshot — see class header for the rationale.
            var s = _socket;
            if (s == null) return 0;

            try
            {
                // The EndPoint type passed to ReceiveFrom must match the
                // socket's address family.  Using IPAddress.Any (IPv4) on an IPv6
                // socket throws ArgumentException and crashes the receive loop.
                EndPoint ep = SocketFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, 0)
                    : new IPEndPoint(IPAddress.Any,     0);
                int count = s.ReceiveFrom(buffer, ref ep);

                // Source pinning: drop datagrams whose source endpoint does
                // not match the registered remote.  AEAD will already reject
                // forged ciphertext, but Poly1305 verification is the most
                // expensive part of the receive path; an off-path attacker
                // who blasts random datagrams at the client port can pin a
                // mobile CPU at 100% while the AEAD layer faithfully rejects
                // every one.  Filtering by source first turns that
                // amplification vector into a benign no-op.
                var expected = Volatile.Read(ref _remoteEndPoint) as IPEndPoint;
                if (expected != null && ep is IPEndPoint actual
                    && !EndpointMatches(expected, actual))
                {
                    // Off-path spoof: a datagram arrived from an endpoint
                    // that is not the registered remote.  Return the
                    // dedicated "rejected, more may follow" sentinel so the
                    // caller drains the rest of the kernel queue in the
                    // same iteration.  Returning 0 (would-block) here
                    // previously short-circuited the drain loop, letting a
                    // sustained off-path flood add ~1 ms of latency to
                    // every legitimate response by deferring it to the next
                    // poll cycle.
                    Interlocked.Increment(ref _droppedSourceMismatchCount);
                    return ReceiveSourceRejected;
                }

                return count;
            }
            catch (ObjectDisposedException)
            {
                // Disconnect() ran while we were parked in ReceiveFrom.  This is
                // the expected shutdown path — return 0 so the I/O loop sees
                // "no data" and notices _running == false on the next iteration.
                return 0;
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.WouldBlock          // No data ready (Linux / macOS)
                   || ex.SocketErrorCode == SocketError.ConnectionReset     // ICMP port-unreachable (Windows)
                   || ex.SocketErrorCode == SocketError.ConnectionRefused   // ICMP port-unreachable (Linux)
                   || ex.SocketErrorCode == SocketError.MessageSize         // Oversized datagram — drop, keep receiving
                   || ex.SocketErrorCode == SocketError.NetworkReset        // Transient route change
                   || ex.SocketErrorCode == SocketError.HostUnreachable     // ICMP host-unreachable
                   || ex.SocketErrorCode == SocketError.NetworkUnreachable  // ICMP network-unreachable
                   || ex.SocketErrorCode == SocketError.OperationAborted    // Socket closed by another thread mid-syscall
                   || ex.SocketErrorCode == SocketError.Interrupted)        // EINTR — close-induced wake-up on POSIX
            {
                // All of these are benign / transient at the UDP layer: we
                // lose one datagram but the receive loop must keep running.
                // OperationAborted/Interrupted cover the case where Disconnect()
                // closes the socket without disposing the wrapper — the kernel
                // wakes ReceiveFrom with WSA_OPERATION_ABORTED (Windows) or
                // EBADF/EINTR (POSIX) and we treat it as a clean shutdown.
                return 0;
            }
        }

        /// <inheritdoc/>
        public override bool Poll(int microSeconds)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));
            var s = _socket;
            if (s == null) return false;
            try
            {
                return s.Poll(microSeconds, SelectMode.SelectRead);
            }
            catch (ObjectDisposedException)
            {
                // Disconnect() raced with the poll. Returning false makes the
                // caller skip Receive() this iteration and check _running.
                return false;
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.OperationAborted
                   || ex.SocketErrorCode == SocketError.Interrupted
                   || ex.SocketErrorCode == SocketError.NotSocket)
            {
                // Same shutdown story as Receive() — kernel woke us because the
                // descriptor was closed.  Treat as "no data; check _running".
                return false;
            }
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            // Under the gate, so a Connect between resolving and committing sees
            // the transport disposed before it can commit, or commits before
            // this Disconnect and is closed by it — never a socket left bound on
            // a transport nobody holds.
            lock (_connectGate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            // Drop the cached address so a freshly constructed transport always
            // re-resolves against the current network state.
            _cachedAddress = null;
            Disconnect();
        }

        /// <summary>
        /// Equality predicate for inbound source-IP pinning.  Compares port
        /// and address bytes directly; <see cref="IPEndPoint.Equals(object)"/>
        /// performs the same comparison but allocates a boxing
        /// <see cref="object"/> reference because the override is on the base
        /// type — calling it on the receive hot path is a small but real
        /// allocation per datagram, so we open-code it here.
        /// </summary>
        private static bool EndpointMatches(IPEndPoint expected, IPEndPoint actual)
        {
            if (expected.Port != actual.Port) return false;
            // IPAddress.Equals on the same family is a fast bytewise
            // comparison; cross-family endpoints (IPv4 vs IPv6) are never
            // equal under our routing model so the check is sufficient.
            return expected.Address.Equals(actual.Address);
        }

        // ── Bounded DNS resolution ──────────────────────────────────────────────

        /// <summary>
        /// Resolve <paramref name="host"/> via the async resolver with a hard
        /// upper bound on total wall-clock time.  The legacy synchronous
        /// <see cref="Dns.GetHostAddresses(string)"/> can block the calling
        /// thread for 5–30 seconds on captive portals or misconfigured DNS;
        /// because the network thread also drives the I/O loop, that stall
        /// directly translates into a frozen client.
        /// </summary>
        /// <exception cref="TimeoutException">
        /// Thrown when DNS does not return within <paramref name="timeout"/>.
        /// </exception>
        private static IPAddress[] ResolveHostAddresses(string host, TimeSpan timeout)
        {
            // A fast-path for literal IPs avoids a system DNS call entirely —
            // important on offline / firewalled machines where even loopback
            // resolution would time out unnecessarily.
            if (IPAddress.TryParse(host, out var literal))
                return new[] { literal };

            // Dns.GetHostAddressesAsync ignores its CancellationToken parameter
            // on .NET Standard 2.1 (the cancel hook landed in .NET 6).  We
            // therefore enforce the bound with Task.Wait(timeout): if it fires
            // first we throw TimeoutException; the underlying resolver task
            // continues on the thread pool and will be reaped once the OS
            // resolver call returns or the process exits.  This is acceptable
            // because the caller never sees the leaked task and the OS cap on
            // concurrent in-flight resolver calls is effectively unlimited.
            var resolveTask = Dns.GetHostAddressesAsync(host);
            if (!resolveTask.Wait(timeout))
            {
                throw new TimeoutException(
                    $"DNS resolution for '{host}' did not complete within {timeout.TotalMilliseconds:0} ms.");
            }
            return resolveTask.Result;
        }
    }
}
