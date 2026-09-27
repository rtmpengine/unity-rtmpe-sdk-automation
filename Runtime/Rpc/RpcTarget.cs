// RTMPE SDK — Runtime/Rpc/RpcTarget.cs
//
// Determines which clients receive an Enhanced RPC call.
// Used by the [RtmpeRpc] attribute and EnhancedRpcPacketBuilder.

namespace RTMPE.Rpc
{
    /// <summary>
    /// Who runs a call to an <see cref="RtmpeRpcAttribute"/> method.
    /// </summary>
    public enum RpcTarget : byte
    {
        /// <summary>
        /// Every client in the room, the caller included.
        /// </summary>
        All    = 0x00,

        /// <summary>
        /// Every client in the room except the caller.
        /// </summary>
        Others = 0x01,

        /// <summary>
        /// No client runs it, the caller included: it executes only in the project's server function,
        /// and the answer returns to the caller through <c>NetworkManager.SendEnhancedRpcAsync</c>.
        /// </summary>
        /// <remarks>
        /// The server function is the HTTPS endpoint you register in the RTMPE Developer Portal
        /// under Project → Server functions. Without one, the call is answered
        /// <see cref="RpcErrorCode.UnknownMethod"/>.
        /// </remarks>
        Server = 0x02,

        /// <summary>
        /// Like <see cref="RpcTarget.All"/>, and also delivered to players who join later.
        /// </summary>
        /// <remarks>
        /// The room keeps a bounded number of buffered calls and drops the oldest first. A
        /// player who joins receives the kept calls, in order, after entering the room.
        /// </remarks>
        AllBuffered = 0x03,
    }
}
