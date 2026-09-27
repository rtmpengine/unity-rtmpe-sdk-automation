// RTMPE SDK — Runtime/Rpc/RpcPacketBuilder.cs
//
// Builds payload bytes for RPC request packets (PacketType.Rpc = 0x50).
// The caller wraps the returned payload with PacketBuilder.Build() to produce
// the full wire packet (13-byte standard header + RPC payload).
//
// Wire format (all little-endian):
//  [method_id  : 4 LE u32]   — identifies the registered handler
//  [sender_id  : 8 LE u64]   — player/session ID (verified by server JWT)
//  [request_id : 4 LE u32]   — client correlation ID for async response matching
//  [payload_len: 2 LE u16]   — length of the variable method-specific payload
//  [payload    : N bytes]    — method-specific data (max 4096 bytes)
//
// Total overhead: 18 bytes + N.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Builds the SDK's built-in method-id calls (<see cref="RpcMethodId"/>). Used by the SDK;
    /// not intended to be called from game code, which uses <see cref="RtmpeRpcAttribute"/>
    /// methods instead.
    /// </summary>
    public static class RpcPacketBuilder
    {
        /// <summary>
        /// The largest payload, in bytes, a built-in method-id call can carry: what one datagram
        /// holds after the call's header.
        /// </summary>
        public const int MaxSendablePayloadBytes =
            RTMPE.Protocol.PacketBuilder.MaxApplicationPayloadBytes
            - RpcLimits.RequestHeaderSize;

        /// <summary>
        /// Builds a built-in method-id call.
        /// </summary>
        /// <remarks>
        /// For game messages, use <see cref="RtmpeRpcAttribute"/> methods and
        /// <c>NetworkBehaviour.RPC</c> instead.
        /// </remarks>
        /// <param name="methodId">The method id (see <see cref="RpcMethodId"/>).</param>
        /// <param name="senderId">This client's session id.</param>
        /// <param name="requestId">The id that matches the answer to this call.</param>
        /// <param name="payload">The method's payload; <see langword="null"/> or empty for
        /// none.</param>
        /// <returns>The encoded call.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="payload"/> is longer than <see cref="MaxSendablePayloadBytes"/>.
        /// </exception>
        [System.Obsolete("Use EnhancedRpcPacketBuilder.Build() + NetworkBehaviour.RPC() for new RPCs. " +
                         "This method is retained only for the built-in method IDs (Ping, TransferOwnership, etc.).")]
        public static byte[] BuildRequest(
            uint methodId,
            ulong senderId,
            uint requestId,
            byte[] payload = null)
        {
            if (payload == null) payload = Array.Empty<byte>();

            if (payload.Length > MaxSendablePayloadBytes)
                throw new ArgumentException(
                    $"RPC payload ({payload.Length} bytes) exceeds the " +
                    $"{MaxSendablePayloadBytes}-byte limit for a single datagram. " +
                    "Send bulk data as game data in chunks: an RPC is not a file " +
                    "transfer, and IP fragmentation is unreliable on mobile and CGNAT links.",
                    nameof(payload));

            ushort payloadLen = (ushort)payload.Length;
            var result = new byte[RpcLimits.RequestHeaderSize + payload.Length];

            // [0..3] method_id (LE u32)
            result[0] = (byte)(methodId);
            result[1] = (byte)(methodId >> 8);
            result[2] = (byte)(methodId >> 16);
            result[3] = (byte)(methodId >> 24);

            // [4..11] sender_id (LE u64)
            result[4]  = (byte)(senderId);
            result[5]  = (byte)(senderId >> 8);
            result[6]  = (byte)(senderId >> 16);
            result[7]  = (byte)(senderId >> 24);
            result[8]  = (byte)(senderId >> 32);
            result[9]  = (byte)(senderId >> 40);
            result[10] = (byte)(senderId >> 48);
            result[11] = (byte)(senderId >> 56);

            // [12..15] request_id (LE u32)
            result[12] = (byte)(requestId);
            result[13] = (byte)(requestId >> 8);
            result[14] = (byte)(requestId >> 16);
            result[15] = (byte)(requestId >> 24);

            // [16..17] payload_len (LE u16)
            result[16] = (byte)(payloadLen);
            result[17] = (byte)(payloadLen >> 8);

            // [18..] payload
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, result, RpcLimits.RequestHeaderSize, payload.Length);

            return result;
        }

        /// <summary>
        /// Builds a ping call (<see cref="RpcMethodId.Ping"/>).
        /// </summary>
        /// <param name="senderId">This client's session id.</param>
        /// <param name="requestId">The id that matches the answer to this call.</param>
        /// <returns>The encoded call.</returns>
#pragma warning disable CS0618
        public static byte[] BuildPing(ulong senderId, uint requestId)
            => BuildRequest(RpcMethodId.Ping, senderId, requestId);
#pragma warning restore CS0618

        /// <summary>
        /// Builds an ownership-transfer call (<see cref="RpcMethodId.TransferOwnership"/>) for
        /// the object <paramref name="objectId"/>.
        /// </summary>
        /// <param name="senderId">This client's session id.</param>
        /// <param name="requestId">The id that matches the answer to this call.</param>
        /// <param name="objectId">The object to transfer.</param>
        /// <param name="newOwnerPlayerId">The player id of the new owner.</param>
        /// <returns>The encoded call.</returns>
        /// <exception cref="ArgumentException"><paramref name="newOwnerPlayerId"/> is null,
        /// empty, or longer than 256 bytes of UTF-8.</exception>
        public static byte[] BuildTransferOwnership(
            ulong senderId,
            uint requestId,
            ulong objectId,
            string newOwnerPlayerId)
        {
            if (string.IsNullOrEmpty(newOwnerPlayerId))
                throw new ArgumentException(
                    "newOwnerPlayerId must not be null or empty.",
                    nameof(newOwnerPlayerId));

            byte[] ownerBytes = System.Text.Encoding.UTF8.GetBytes(newOwnerPlayerId);
            if (ownerBytes.Length > 256)
                throw new ArgumentException(
                    "newOwnerPlayerId UTF-8 encoding exceeds 256 bytes.",
                    nameof(newOwnerPlayerId));

            var payload = new byte[8 + 2 + ownerBytes.Length];

            // [0..7] object_id (LE u64)
            payload[0] = (byte)(objectId);
            payload[1] = (byte)(objectId >> 8);
            payload[2] = (byte)(objectId >> 16);
            payload[3] = (byte)(objectId >> 24);
            payload[4] = (byte)(objectId >> 32);
            payload[5] = (byte)(objectId >> 40);
            payload[6] = (byte)(objectId >> 48);
            payload[7] = (byte)(objectId >> 56);

            // [8..9] new_owner_len (LE u16)
            ushort ownerLen = (ushort)ownerBytes.Length;
            payload[8] = (byte)(ownerLen);
            payload[9] = (byte)(ownerLen >> 8);

            // [10..] new_owner UTF-8
            Buffer.BlockCopy(ownerBytes, 0, payload, 10, ownerBytes.Length);

#pragma warning disable CS0618
            return BuildRequest(RpcMethodId.TransferOwnership, senderId, requestId, payload);
#pragma warning restore CS0618
        }
    }
}
