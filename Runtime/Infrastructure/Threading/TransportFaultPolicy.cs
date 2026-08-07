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
    /// confined to the datagram that produced it.
    /// </summary>
    public static class TransportFaultPolicy
    {
        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="ex"/> means the
        /// socket itself is no longer usable, and the session must end.
        ///
        /// <para>Unrecognised exceptions are fatal. A fault this policy cannot
        /// name is one whose blast radius is unknown, and treating it as
        /// per-packet would keep a session alive on a transport that may have
        /// stopped working — so the default stays with the conservative
        /// behaviour and only the cases established below are downgraded.</para>
        /// </summary>
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
                    case SocketError.NetworkUnreachable:
                    case SocketError.NetworkDown:
                        return false;

                    default:
                        return true;
                }
            }

            return true;
        }
    }
}
