// RTMPE SDK — Runtime/Rpc/RpcDefinitions.cs
//
// Wire-protocol constants for the RPC subsystem.
//
// Wire-protocol constants for RPC messages are defined here so both
// RpcPacketBuilder and RpcPacketParser share a single source of truth.
// Method IDs are permanent — never reuse a retired ID.

namespace RTMPE.Rpc
{
    /// <summary>
    /// The reserved ids of the SDK's built-in method-id calls, which
    /// <c>NetworkManager.SendRpc</c> sends. Games use <c>[RtmpeRpc]</c> methods instead.
    /// </summary>
    /// <remarks>
    /// An <c>[RtmpeRpc]</c> method whose id equals one of these is refused (analyzer rule
    /// RTMPE1004).
    /// </remarks>
    public static class RpcMethodId
    {
        /// <summary>The ping call.</summary>
        public const uint Ping               = 100;

        /// <summary>The call the SDK uses for ownership transfers.</summary>
        public const uint TransferOwnership  = 200;

        /// <summary>A request to apply damage.</summary>
        public const uint RequestDamage      = 300;

        /// <summary>
        /// A damage result, delivered to the addressed object's <c>IDamageable</c> component.
        /// </summary>
        public const uint ApplyDamage        = 301;

        /// <summary>A request to change the game state.</summary>
        public const uint GameStateChange    = 400;

        /// <summary>The current game state, sent to every client.</summary>
        public const uint SyncGameState      = 401;
    }

    /// <summary>
    /// The outcome of a call the server answered, in <c>RpcResponse.ErrorCode</c>.
    /// </summary>
    public enum RpcErrorCode : ushort
    {
        /// <summary>Success.</summary>
        OK              = 0,

        /// <summary>The server function refused the caller.</summary>
        Unauthorized    = 1,

        /// <summary>No server function handles the method, or none is registered.</summary>
        UnknownMethod   = 2,

        /// <summary>The server function failed, or answered outside the contract.</summary>
        HandlerError    = 3,

        /// <summary>The arguments were too large.</summary>
        OversizedPayload = 4,

        /// <summary>
        /// The server function did not answer in time.
        /// </summary>
        Timeout = 5,

        /// <summary>
        /// The server function could not be called: its endpoint did not answer, failed too
        /// often recently, may not be called, or too many calls were in flight for the caller
        /// or the project.
        /// </summary>
        Unavailable = 6,

        /// <summary>
        /// A code this SDK version does not know.
        /// </summary>
        Unknown          = 0xFFFF,
    }

    /// <summary>
    /// Sizes and limits of RPC messages.
    /// </summary>
    public static class RpcLimits
    {
        /// <summary>
        /// The largest payload, in bytes, accepted in a received RPC response or a received
        /// built-in method-id call; a larger one is refused as malformed.
        /// </summary>
        /// <remarks>
        /// This is not a send limit: a call you send must fit in one datagram. The send limits
        /// are <c>EnhancedRpcPacketBuilder.MaxSendablePayloadBytes</c> for <c>[RtmpeRpc]</c>
        /// calls and <c>RpcPacketBuilder.MaxSendablePayloadBytes</c> for built-in method-id
        /// calls.
        /// </remarks>
        public const int MaxPayloadBytes = 4096;

        /// <summary>
        /// The size, in bytes, of the header in front of a built-in method-id call's payload.
        /// </summary>
        public const int RequestHeaderSize = 18;

        /// <summary>
        /// The size, in bytes, of the header in front of an <c>[RtmpeRpc]</c> call's arguments.
        /// </summary>
        public const int EnhancedRequestHeaderSize = 27;

        /// <summary>
        /// The size, in bytes, of the header in front of an RPC response's payload.
        /// </summary>
        public const int ResponseHeaderSize = 21;
    }
}
