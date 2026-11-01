// TransportFaultPolicy — decides whether a transport fault ends the session.
//
// The network thread reports faults through a single OnError event, and the
// only subscriber tears the session down: stop the thread, clear session data,
// transition to Disconnected. That is the correct response to a socket that no
// longer works, and the wrong response to one datagram that could not be sent.
//
// The decision is isolated here, free of UnityEngine, so it is unit-testable
// under the dotnet/xunit harness rather than only inside a running Unity
// player.

using System;
using System.Net.Sockets;

namespace RTMPE.Threading
{
    /// <summary>
    /// Classifies a transport exception as either fatal to the session or
    /// confined to the datagram that produced it. <see cref="NetworkThread"/>
    /// uses it for exceptions thrown while sending and receiving.
    /// </summary>
    public static class TransportFaultPolicy
    {
        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="ex"/> means the
        /// socket is unusable and the session must end;
        /// <see langword="false"/> when the fault affects only the datagram
        /// that caused it.
        /// </summary>
        /// <remarks>
        /// Not fatal: an <see cref="ArgumentException"/>, and a
        /// <see cref="SocketException"/> whose error is
        /// <see cref="SocketError.ConnectionReset"/>,
        /// <see cref="SocketError.MessageSize"/>,
        /// <see cref="SocketError.HostUnreachable"/>,
        /// <see cref="SocketError.NetworkUnreachable"/> or
        /// <see cref="SocketError.NetworkDown"/>. Any other exception, and
        /// <see langword="null"/>, is fatal.
        /// </remarks>
        /// <param name="ex">The exception the transport threw.</param>
        public static bool IsSessionFatal(Exception ex)
        {
            if (ex == null) return true;

            // Caller-side argument faults describe the one buffer that was
            // passed in — an oversize frame, a null array. The socket is
            // untouched, and the next datagram is unaffected. This is the
            // common case behind an application payload that outgrew the
            // datagram budget.
            if (ex is ArgumentException) return false;

            if (ex is SocketException sx)
            {
                switch (sx.SocketErrorCode)
                {
                    // ICMP port-unreachable delivered against a previous
                    // datagram. On a connected UDP socket the kernel surfaces
                    // it on a later call, so it describes a peer that was not
                    // listening a moment ago, not a broken local socket — and
                    // it arrives routinely while a gateway restarts.
                    case SocketError.ConnectionReset:
                    // EMSGSIZE: this datagram exceeds the path limit. The next
                    // one, at a normal size, still sends.
                    case SocketError.MessageSize:
                    // Transient routing failures, also reported via ICMP.
                    case SocketError.HostUnreachable:
                    case SocketError.HostDown:
                    case SocketError.NetworkUnreachable:
                    case SocketError.NetworkDown:
                    // A route that changed under the datagram, and a call the
                    // OS interrupted: the receive path has always read both as
                    // transient, and the send path now agrees (audit P2-L04).
                    case SocketError.NetworkReset:
                    case SocketError.Interrupted:
                        return false;

                    // ⛔ Fatal on purpose, and said so because both look
                    // transient. AddressNotAvailable is the source address
                    // gone — a handoff — and the gateway knows the session by
                    // that address, so the session is over and the reconnect
                    // (which keeps the token) is what moves it. AccessDenied is
                    // a firewall's refusal, which the next datagram meets too.
                    case SocketError.AddressNotAvailable:
                    case SocketError.AccessDenied:
                        return true;

                    default:
                        return true;
                }
            }

            return true;
        }
    }
}
