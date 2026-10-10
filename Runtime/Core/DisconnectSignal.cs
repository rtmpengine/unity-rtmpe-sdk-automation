// RTMPE SDK — Runtime/Core/DisconnectSignal.cs
//
// What the gateway's Disconnect (0xFF) packet SAYS, and what the SDK is entitled
// to keep because of it.  Two pure decisions, in their own file so both can be
// driven by tests: everything else on this path lives in a NetworkManager partial
// that no test project compiles.

namespace RTMPE.Core
{
    /// <summary>
    /// Reads a disconnect the server sends: the reason it names, and whether the
    /// reconnect token survives it.
    /// </summary>
    public static class DisconnectSignal
    {
        /// <summary>
        /// Returns the <see cref="DisconnectReason"/> for the reason byte of a
        /// disconnect the server sent.
        /// </summary>
        /// <remarks>
        /// A byte this SDK version does not know is read as
        /// <see cref="DisconnectReason.ServerRequest"/>. Used by the SDK; not
        /// intended to be called from game code.
        /// </remarks>
        /// <param name="wire">The reason byte.</param>
        /// <returns>The reason the byte names.</returns>
        public static DisconnectReason FromWire(byte wire)
        {
            switch (wire)
            {
                case 0x00: return DisconnectReason.Unknown;
                case 0x02: return DisconnectReason.ServerRequest;
                case 0x05: return DisconnectReason.Kicked;
                case 0x07: return DisconnectReason.ProtocolError;
                default:   return DisconnectReason.ServerRequest;
            }
        }

        /// <summary>
        /// Whether the reconnect token and the last-room snapshot survive a
        /// disconnect the server sent with <paramref name="reason"/>.
        /// </summary>
        /// <remarks>
        /// <para>Returns <see langword="true"/> for
        /// <see cref="DisconnectReason.ServerRequest"/> only: the server closed
        /// the session, for example while restarting, and
        /// <see cref="NetworkManager.Reconnect"/> can usually resume it.
        /// <see cref="DisconnectReason.Kicked"/>,
        /// <see cref="DisconnectReason.ProtocolError"/> and
        /// <see cref="DisconnectReason.Unknown"/> discard the token.</para>
        /// <para>A kept token can still be refused by the server, in which case
        /// the reconnect attempt fails.</para>
        /// <para>The reasons the client raises itself
        /// (<see cref="DisconnectReason.ClientRequest"/>,
        /// <see cref="DisconnectReason.Timeout"/>,
        /// <see cref="DisconnectReason.ConnectionLost"/> and
        /// <see cref="DisconnectReason.NonceExhausted"/>) return
        /// <see langword="false"/> here; whether they keep the token is described
        /// on <see cref="DisconnectReason"/>.</para>
        /// </remarks>
        /// <param name="reason">The reason the server gave.</param>
        public static bool TokenSurvives(DisconnectReason reason) =>
            reason == DisconnectReason.ServerRequest;
    }
}
