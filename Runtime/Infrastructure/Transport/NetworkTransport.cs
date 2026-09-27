// RTMPE SDK — Runtime/Infrastructure/Transport/NetworkTransport.cs
//
// Abstract base for all network transports.
// The concrete transport shipped with this package is UdpTransport.  KCP and
// WebSocket transports are integrator-supplied and registered through
// NetworkManager.SetTransportFactory.  Registering one does not reach a new
// platform: the background thread that drives every transport is above this
// interface, not below it.
//
// All methods are invoked from the RTMPE background network thread and must
// be internally thread-safe. Do NOT call Unity APIs from implementations.

using System;

namespace RTMPE.Transport
{
    /// <summary>
    /// Base class for transports. The SDK uses <see cref="UdpTransport"/>
    /// unless a factory installed with <c>NetworkManager.SetTransportFactory</c>
    /// returns another transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations must be thread-safe: the SDK's network thread calls
    /// <see cref="Connect"/>, <see cref="Send"/>, <see cref="Poll"/> and
    /// <see cref="Receive"/>, while the main thread may call
    /// <see cref="Disconnect"/> or <see cref="Dispose"/> and read
    /// <see cref="LocalEndPoint"/> at the same time. Do not call Unity APIs
    /// from an implementation.
    /// </para>
    /// <para>
    /// The SDK may connect the same transport again, after
    /// <see cref="Disconnect"/>, for a later connection attempt.
    /// </para>
    /// </remarks>
    public abstract class NetworkTransport : IDisposable
    {
        /// <summary>True while the underlying socket is open and ready for I/O.</summary>
        public abstract bool IsConnected { get; }
        /// <summary>
        /// The local endpoint (address and port) of the connected socket, or
        /// <see langword="null"/> while the transport is not connected.
        /// </summary>
        /// <remarks>
        /// The SDK waits for this to become non-<see langword="null"/> before it
        /// sends the first handshake packet, so an implementation must return an
        /// endpoint once <see cref="Connect"/> has succeeded; the base
        /// implementation always returns <see langword="null"/>. The Network
        /// Debugger also displays it.
        /// </remarks>
        public virtual System.Net.IPEndPoint LocalEndPoint => null;
        /// <summary>
        /// Opens the socket and connects to the configured remote endpoint.
        /// Called on the network thread at the start of each connection
        /// attempt, before the loop begins.
        /// </summary>
        /// <exception cref="System.Net.Sockets.SocketException">The socket cannot be created or bound.</exception>
        /// <exception cref="InvalidOperationException">The transport has been disposed.</exception>
        public abstract void Connect();

        /// <summary>
        /// Closes the socket. Safe to call more than once. May be called from
        /// the main thread while the network thread is inside
        /// <see cref="Poll"/> or <see cref="Receive"/>.
        /// </summary>
        public abstract void Disconnect();

        /// <summary>
        /// Sends all of <paramref name="data"/> to the remote endpoint as one
        /// datagram. The array belongs to the caller: do not keep a reference
        /// to it.
        /// </summary>
        /// <remarks>
        /// To lose only this datagram rather than end the session, report a
        /// fault as an exception that <see cref="RTMPE.Threading.TransportFaultPolicy"/>
        /// treats as not fatal.
        /// </remarks>
        /// <param name="data">The datagram to send.</param>
        /// <exception cref="InvalidOperationException">The transport is not connected.</exception>
        public abstract void Send(byte[] data);

        /// <summary>
        /// The only negative value <see cref="Receive"/> may return: a datagram
        /// was read and discarded (the built-in transport does this for a
        /// datagram from an address other than the server's), and more may be
        /// waiting, so the receive loop reads again at once.
        /// </summary>
        /// <remarks>
        /// Any other negative value ends the current receive pass and is
        /// counted as a fault; do not return a raw error code.
        /// </remarks>
        public const int ReceiveSourceRejected = -1;

        /// <summary>
        /// Copies one waiting datagram into <paramref name="buffer"/> and
        /// returns its length; returns 0 at once when nothing is waiting, or
        /// <see cref="ReceiveSourceRejected"/> after reading and discarding a
        /// datagram. Must not block.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A count greater than the length of <paramref name="buffer"/>, or a
        /// negative value other than <see cref="ReceiveSourceRejected"/>, makes
        /// the SDK drop the datagram instead of parsing it. Never write past
        /// the end of <paramref name="buffer"/>.
        /// </para>
        /// <para>
        /// Important: the returned count must cover every byte written to
        /// <paramref name="buffer"/>, not only the meaningful ones. The buffer
        /// comes from a pool and only the reported length is cleared before
        /// its next use, so an implementation that decodes a framing in place
        /// must report the bytes it wrote.
        /// </para>
        /// </remarks>
        /// <param name="buffer">The buffer to copy the datagram into.</param>
        public abstract int Receive(byte[] buffer);

        /// <summary>
        /// Waits up to <paramref name="microSeconds"/> for a datagram to arrive
        /// and returns <see langword="true"/> when one is waiting to be read.
        /// With 0 it returns at once.
        /// </summary>
        /// <remarks>
        /// The network thread paces its loop with this wait: return as soon as
        /// a datagram arrives, and otherwise wait out the time, or the loop
        /// spins.
        /// </remarks>
        /// <param name="microSeconds">Longest time to wait, in microseconds.</param>
        public abstract bool Poll(int microSeconds);

        /// <inheritdoc/>
        public abstract void Dispose();
    }
}
