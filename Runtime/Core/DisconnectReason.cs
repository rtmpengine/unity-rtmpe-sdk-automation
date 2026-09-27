// RTMPE SDK — Runtime/Core/DisconnectReason.cs
//
// The reason codes NetworkManager.OnDisconnected carries.
//
// ⛔ The ORDINALS ARE A WIRE CONTRACT.  The gateway places one of them as the
// first payload byte of its Disconnect (0xFF) packet, and
// modules/gateway/src/packet/mod.rs::disconnect_reason declares the same values
// on that side — including the ones it reserves but never sends.  Reordering,
// inserting or removing a member therefore changes the wire, so it is a rule-1
// change requiring both sides at once.  The reserved gaps exist for that reason
// and must stay where they are even though only ServerRequest is ever emitted.
//
// Declared here rather than beside the partial class it is named for so the
// resumption policy in DisconnectSignal.cs — and the tests that drive it — can
// be compiled without UnityEngine.

namespace RTMPE.Core
{
    /// <summary>
    /// Why a connection closed: the argument of
    /// <see cref="NetworkManager.OnDisconnected"/>.
    /// </summary>
    /// <remarks>
    /// The reason also decides whether the reconnect token survives; check
    /// <see cref="NetworkManager.CanReconnect"/> before calling
    /// <see cref="NetworkManager.Reconnect"/>.
    /// </remarks>
    public enum DisconnectReason
    {
        /// <summary>
        /// The server ended the session without a specific reason, or the session
        /// the server issued failed the SDK's validation; the Console names the
        /// check that failed. The reconnect token is discarded.
        /// </summary>
        Unknown,

        /// <summary>
        /// You called <see cref="NetworkManager.Disconnect"/>. The reconnect token
        /// is discarded.
        /// </summary>
        ClientRequest,

        /// <summary>
        /// The server closed the session, for example while restarting, or gave a
        /// reason this SDK version does not know. The reconnect token is kept.
        /// </summary>
        ServerRequest,

        /// <summary>
        /// A connection or reconnect attempt did not complete within
        /// <see cref="NetworkSettings.connectionTimeoutMs"/>. This includes a
        /// handshake the server refused and a server key that does not match the
        /// pin. Also raised when every reconnect attempt has failed. The reconnect
        /// token is discarded, except that a reconnect attempt that timed out
        /// before the server answered keeps it for the remaining attempts.
        /// </summary>
        Timeout,

        /// <summary>
        /// Three heartbeats in a row went unanswered and none was acknowledged
        /// within <see cref="NetworkSettings.heartbeatLivenessGraceMs"/>, or a
        /// socket error ended the session. The reconnect token is kept after
        /// missed heartbeats and discarded after a socket error.
        /// </summary>
        ConnectionLost,

        /// <summary>
        /// The server removed this client from the session. The reconnect token
        /// is discarded.
        /// </summary>
        Kicked,

        /// <summary>
        /// The session sent 2^32 encrypted packets and must be established again.
        /// The reconnect token is discarded.
        /// </summary>
        NonceExhausted,

        /// <summary>
        /// The secure session could not be set up:
        /// <see cref="NetworkSettings.apiKeySealServerPublicKeyHex"/> is missing or
        /// invalid, the pinning mode requires a server key pin that is not
        /// available, or the key exchange failed. The reason is passed to
        /// <see cref="NetworkManager.OnConnectionFailed"/> first. The server can
        /// also end a session with this reason. The reconnect token is discarded,
        /// except when a pin is missing during a reconnect attempt.
        /// </summary>
        ProtocolError
    }
}
