// RTMPE SDK — Runtime/Rpc/RpcPacketParser.cs
//
// Parses incoming RPC response packets (PacketType.RpcResponse = 0x51).
// The standard 13-byte packet header has already been stripped by the
// transport layer — this parser operates on the RPC payload portion only.
//
// Wire format (all little-endian):
//  [request_id  : 4 LE u32]   — echoed from request for correlation
//  [method_id   : 4 LE u32]   — method that produced this response
//  [sender_id   : 8 LE u64]   — server-verified sender ID
//  [success     : 1 u8]       — 1=success, 0=failure
//  [error_code  : 2 LE u16]   — 0=OK, 1-6=error (see RpcErrorCode)
//  [payload_len : 2 LE u16]   — length of optional response payload
//  [payload     : N bytes]    — optional method-specific response data
//
// Total header: 21 bytes + N.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// The answer to a call, such as the result of
    /// <c>NetworkManager.SendEnhancedRpcAsync</c>.
    /// </summary>
    /// <remarks>
    /// A refusal by your server function is an answer with <see cref="Success"/> set to
    /// <see langword="false"/> and <see cref="ErrorCode"/> saying why.
    /// </remarks>
    public readonly struct RpcResponse
    {
        /// <summary>The id that matches the answer to its request.</summary>
        public readonly uint RequestId;
        /// <summary>The id of the method called.</summary>
        public readonly uint MethodId;
        /// <summary>The sender's session id; 0 for an answer from the server.</summary>
        public readonly ulong SenderId;
        /// <summary>
        /// Whether the call succeeded. <see langword="false"/> when <see cref="ErrorCode"/>
        /// explains a failure.
        /// </summary>
        public readonly bool Success;
        /// <summary>The outcome of the call.</summary>
        public readonly RpcErrorCode ErrorCode;
        /// <summary>
        /// The encoded result; empty when there is none. Read it with
        /// <see cref="TryReadResult"/>.
        /// </summary>
        public readonly byte[] Payload;

        /// <summary>Creates a response, for tests.</summary>
        /// <param name="requestId">The request id.</param>
        /// <param name="methodId">The method id.</param>
        /// <param name="senderId">The sender's session id; 0 for the server.</param>
        /// <param name="success">Whether the call succeeded.</param>
        /// <param name="errorCode">The outcome of the call.</param>
        /// <param name="payload">The encoded result; <see langword="null"/> becomes an empty
        /// array.</param>
        public RpcResponse(
            uint requestId,
            uint methodId,
            ulong senderId,
            bool success,
            RpcErrorCode errorCode,
            byte[] payload)
        {
            RequestId = requestId;
            MethodId  = methodId;
            SenderId  = senderId;
            Success   = success;
            ErrorCode = errorCode;
            Payload   = payload ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Decodes <see cref="Payload"/> into the values your server function returned.
        /// </summary>
        /// <param name="values">The values, in the order they were returned.</param>
        /// <returns>
        /// <see langword="true"/>, with an empty array for an empty payload;
        /// <see langword="false"/>, with an empty array, when the payload cannot be decoded.
        /// </returns>
        public bool TryReadResult(out object[] values)
        {
            values = Array.Empty<object>();
            if (Payload.Length == 0) return true;

            int count = Payload[0];
            // Every value is at least its one-byte tag: a count that cannot fit
            // is refused before an array is sized by it.
            if (count > Payload.Length - 1) return false;

            var read = new object[count];
            int offset = 1;
            for (int i = 0; i < count; i++)
            {
                object value;
                try
                {
                    value = RpcSerializer.ReadParam(Payload, ref offset);
                }
                catch (RpcDeserializationException)
                {
                    return false;
                }
                if (offset == -1) return false;
                read[i] = value;
            }
            if (offset != Payload.Length) return false;

            values = read;
            return true;
        }
    }

    /// <summary>
    /// A received built-in method-id call (<see cref="RpcMethodId"/>), as
    /// <see cref="RpcPacketParser.TryParseRequest"/> reads it. Not intended to be used from
    /// game code.
    /// </summary>
    public readonly struct RpcRequest
    {
        /// <summary>The method id.</summary>
        public readonly uint MethodId;
        /// <summary>The sender's session id.</summary>
        public readonly ulong SenderId;
        /// <summary>The request id.</summary>
        public readonly uint RequestId;
        /// <summary>The call's payload; empty when there is none.</summary>
        public readonly byte[] Payload;

        /// <summary>Creates a request, for tests.</summary>
        /// <param name="methodId">The method id.</param>
        /// <param name="senderId">The sender's session id.</param>
        /// <param name="requestId">The request id.</param>
        /// <param name="payload">The payload; <see langword="null"/> becomes an empty
        /// array.</param>
        public RpcRequest(uint methodId, ulong senderId, uint requestId, byte[] payload)
        {
            MethodId  = methodId;
            SenderId  = senderId;
            RequestId = requestId;
            Payload   = payload ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Reads RPC answers and built-in method-id calls. Used by the SDK; not intended to be
    /// called from game code.
    /// </summary>
    /// <remarks>
    /// Every method returns <see langword="false"/> for malformed data instead of throwing.
    /// </remarks>
    public static class RpcPacketParser
    {
        /// <summary>
        /// Reads a received RPC answer.
        /// </summary>
        /// <param name="data">The received answer.</param>
        /// <param name="response">The answer, when it could be read.</param>
        /// <returns><see langword="true"/> when the answer could be read; an error code this SDK
        /// version does not know reads as <see cref="RpcErrorCode.Unknown"/>.</returns>
        public static bool TryParseResponse(byte[] data, out RpcResponse response)
        {
            response = default;

            if (data == null || data.Length < RpcLimits.ResponseHeaderSize)
                return false;

            uint requestId = ReadU32LE(data, 0);
            uint methodId  = ReadU32LE(data, 4);
            ulong senderId = ReadU64LE(data, 8);
            bool success   = data[16] != 0;
            ushort errorCode  = ReadU16LE(data, 17);
            ushort payloadLen = ReadU16LE(data, 19);

            // Reject oversized payloads first (defense-in-depth: adversarial payloadLen)
            if (payloadLen > RpcLimits.MaxPayloadBytes)
                return false;

            // Subtraction-form bounds: avoids the additive-form overflow
            // surface and matches the convention adopted across the rest
            // of the SDK parsers.  Combined with the strict trailing-byte
            // check below, a well-formed response is exactly
            // ResponseHeaderSize + payloadLen bytes long.
            if (payloadLen > data.Length - RpcLimits.ResponseHeaderSize)
                return false;
            if (RpcLimits.ResponseHeaderSize + payloadLen != data.Length)
                return false;

            byte[] payload;
            if (payloadLen > 0)
            {
                payload = new byte[payloadLen];
                Buffer.BlockCopy(data, RpcLimits.ResponseHeaderSize, payload, 0, payloadLen);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            // Validate the wire-level errorCode against defined enum members
            // before the cast.  An out-of-range value (e.g. 999 from a buggy
            // gateway) was previously cast directly, producing an enum
            // instance that pattern-matched no case in user code.  Mapping
            // unknown codes onto <see cref="RpcErrorCode.Unknown"/> gives
            // application code a single explicit member to handle and
            // prevents silent misclassification as <see cref="RpcErrorCode.OK"/>.
            var resolvedError = errorCode switch
            {
                (ushort)RpcErrorCode.OK               => RpcErrorCode.OK,
                (ushort)RpcErrorCode.Unauthorized     => RpcErrorCode.Unauthorized,
                (ushort)RpcErrorCode.UnknownMethod    => RpcErrorCode.UnknownMethod,
                (ushort)RpcErrorCode.HandlerError     => RpcErrorCode.HandlerError,
                (ushort)RpcErrorCode.OversizedPayload => RpcErrorCode.OversizedPayload,
                (ushort)RpcErrorCode.Timeout          => RpcErrorCode.Timeout,
                (ushort)RpcErrorCode.Unavailable      => RpcErrorCode.Unavailable,
                _                                     => RpcErrorCode.Unknown,
            };

            response = new RpcResponse(
                requestId, methodId, senderId,
                success, resolvedError, payload);
            return true;
        }

        /// <summary>
        /// Reads a received built-in method-id call.
        /// </summary>
        /// <param name="data">The received call.</param>
        /// <param name="request">The call, when it could be read.</param>
        /// <returns><see langword="true"/> when the call could be read.</returns>
        public static bool TryParseRequest(byte[] data, out RpcRequest request)
        {
            request = default;

            if (data == null || data.Length < RpcLimits.RequestHeaderSize)
                return false;

            uint methodId  = ReadU32LE(data, 0);
            ulong senderId = ReadU64LE(data, 4);
            uint requestId = ReadU32LE(data, 12);
            ushort payloadLen = ReadU16LE(data, 16);

            // Reject oversized payloads first (defense-in-depth: adversarial payloadLen)
            if (payloadLen > RpcLimits.MaxPayloadBytes)
                return false;

            // Subtraction-form bounds + strict trailing-byte rejection.  A
            // well-formed request is exactly RequestHeaderSize + payloadLen
            // bytes long; surplus bytes beyond that are a protocol-drift
            // / smuggling signal and must not be silently retained.
            if (payloadLen > data.Length - RpcLimits.RequestHeaderSize)
                return false;
            if (RpcLimits.RequestHeaderSize + payloadLen != data.Length)
                return false;

            byte[] payload;
            if (payloadLen > 0)
            {
                payload = new byte[payloadLen];
                Buffer.BlockCopy(data, RpcLimits.RequestHeaderSize, payload, 0, payloadLen);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            request = new RpcRequest(methodId, senderId, requestId, payload);
            return true;
        }

        /// <summary>
        /// Build an RPC response payload.  This method is the server-side /
        /// test-fixture counterpart to <see cref="TryParseResponse"/>.
        ///
       /// Visibility: <c>internal</c>.  Client code (game scripts that import
        /// the SDK) must NOT construct response packets — doing so bypasses the
        /// server-authoritative trust model and could corrupt a peer's state
        /// if the bytes reach the network.  Unit tests access this method via
        /// <c>InternalsVisibleTo("RTMPE.SDK.Tests")</c> declared in
        /// <c>AssemblyInfo.cs</c>.
        /// </summary>
        internal static byte[] BuildResponse(
            uint requestId,
            uint methodId,
            ulong senderId,
            bool success,
            RpcErrorCode errorCode,
            byte[] payload = null)
        {
            if (payload == null) payload = Array.Empty<byte>();

            if (payload.Length > RpcLimits.MaxPayloadBytes)
                throw new ArgumentException(
                    $"RPC response payload exceeds maximum size ({payload.Length} > {RpcLimits.MaxPayloadBytes}).",
                    nameof(payload));

            ushort payloadLen = (ushort)payload.Length;
            var result = new byte[RpcLimits.ResponseHeaderSize + payload.Length];

            // [0..3] request_id (LE u32)
            result[0] = (byte)(requestId);
            result[1] = (byte)(requestId >> 8);
            result[2] = (byte)(requestId >> 16);
            result[3] = (byte)(requestId >> 24);

            // [4..7] method_id (LE u32)
            result[4] = (byte)(methodId);
            result[5] = (byte)(methodId >> 8);
            result[6] = (byte)(methodId >> 16);
            result[7] = (byte)(methodId >> 24);

            // [8..15] sender_id (LE u64)
            result[8]  = (byte)(senderId);
            result[9]  = (byte)(senderId >> 8);
            result[10] = (byte)(senderId >> 16);
            result[11] = (byte)(senderId >> 24);
            result[12] = (byte)(senderId >> 32);
            result[13] = (byte)(senderId >> 40);
            result[14] = (byte)(senderId >> 48);
            result[15] = (byte)(senderId >> 56);

            // [16] success (u8)
            result[16] = success ? (byte)1 : (byte)0;

            // [17..18] error_code (LE u16)
            ushort ec = (ushort)errorCode;
            result[17] = (byte)(ec);
            result[18] = (byte)(ec >> 8);

            // [19..20] payload_len (LE u16)
            result[19] = (byte)(payloadLen);
            result[20] = (byte)(payloadLen >> 8);

            // [21..] payload
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, result, RpcLimits.ResponseHeaderSize, payload.Length);

            return result;
        }

        // ── Little-endian readers ──────────────────────────────────────────────

        private static ushort ReadU16LE(byte[] data, int offset)
            => (ushort)(data[offset] | (data[offset + 1] << 8));

        private static uint ReadU32LE(byte[] data, int offset)
            => (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));

        private static ulong ReadU64LE(byte[] data, int offset)
            => (ulong)data[offset]
                | ((ulong)data[offset + 1] << 8)
                | ((ulong)data[offset + 2] << 16)
                | ((ulong)data[offset + 3] << 24)
                | ((ulong)data[offset + 4] << 32)
                | ((ulong)data[offset + 5] << 40)
                | ((ulong)data[offset + 6] << 48)
                | ((ulong)data[offset + 7] << 56);
    }
}
