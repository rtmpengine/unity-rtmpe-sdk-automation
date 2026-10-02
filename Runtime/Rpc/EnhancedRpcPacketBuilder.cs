// RTMPE SDK — Runtime/Rpc/EnhancedRpcPacketBuilder.cs
//
// Builds payload bytes for Enhanced RPC packets.
// The caller wraps the result with PacketBuilder.Build(PacketType.Rpc,
// PacketFlags.Reliable | PacketFlags.EnhancedRpc, payload) to produce the
// full wire packet (13-byte standard header + Enhanced RPC payload).
//
// Enhanced RPC payload layout (all little-endian):
//  [method_id   :  4 LE u32]  FNV-1a("TypeName.MethodName")
//  [sender_id   :  8 LE u64]  gateway session ID
//  [request_id  :  4 LE u32]  client-assigned correlation ID
//  [object_id   :  8 LE u64]  NetworkBehaviour.NetworkObjectId
//  [target      :  1 u8]      RpcTarget (All=0x00, Others=0x01, Server=0x02)
//  [rpc_flags   :  1 u8]      reserved, set to 0x00
//  [param_count :  1 u8]      number of typed parameters that follow
//  [params…]                  typed param stream (RpcSerializer format)
//
// Total fixed header: 27 bytes.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Builds <see cref="RtmpeRpcAttribute"/> calls. Used by the SDK; not intended to be called
    /// from game code, which calls <c>NetworkBehaviour.RPC</c> or
    /// <c>NetworkManager.SendEnhancedRpcAsync</c>.
    /// </summary>
    public static class EnhancedRpcPacketBuilder
    {
        /// <summary>
        /// The largest size, in bytes, of one call's encoded arguments: what one datagram holds
        /// after the call's header. A call with larger arguments is not sent.
        /// </summary>
        public const int MaxSendablePayloadBytes =
            RTMPE.Protocol.PacketBuilder.MaxApplicationPayloadBytes
            - RpcLimits.EnhancedRequestHeaderSize;

        /// <summary>
        /// Builds an <see cref="RtmpeRpcAttribute"/> call.
        /// </summary>
        /// <param name="methodId">The method id (see
        /// <see cref="RpcRegistry.ComputeMethodId(Type, string)"/>).</param>
        /// <param name="senderId">This client's session id; must not be 0.</param>
        /// <param name="requestId">The id that matches the answer to this call.</param>
        /// <param name="objectId">The <see cref="RTMPE.Core.NetworkBehaviour.NetworkObjectId"/>
        /// of the object the call addresses.</param>
        /// <param name="target">Who runs the call.</param>
        /// <param name="args">The arguments, of the types <see cref="RpcSerializer"/>
        /// supports.</param>
        /// <returns>The encoded call.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="senderId"/> is 0; an argument's type is not supported, or a string or
        /// byte array argument is longer than 65535 bytes; the encoded arguments are larger than
        /// <see cref="MaxSendablePayloadBytes"/>; or there are more than 255 arguments.
        /// </exception>
        public static byte[] Build(
            uint      methodId,
            ulong     senderId,
            uint      requestId,
            ulong     objectId,
            RpcTarget target,
            object[]  args = null)
        {
            // Zero is the gateway's "unset" sentinel for the session id field;
            // building a packet with senderId=0 produces a frame the gateway
            // and every receiving peer silently drop as spoofed.  Failing
            // loudly at the builder turns a hard-to-trace silent drop into
            // an immediate, attributable programmer error.
            if (senderId == 0)
                throw new ArgumentException(
                    "senderId must be non-zero; gateway and peers reject senderId=0 as spoofed.",
                    nameof(senderId));

            if (args == null) args = Array.Empty<object>();

            if (args.Length > byte.MaxValue)
                throw new ArgumentException(
                    $"Enhanced RPC supports at most 255 parameters; got {args.Length}.",
                    nameof(args));

            // Measure total param bytes first to allocate exactly once.
            int paramBytes = 0;
            for (int i = 0; i < args.Length; i++)
            {
                int sz = RpcSerializer.MeasureParam(args[i]);
                if (sz == 0)
                    throw new ArgumentException(
                        $"Enhanced RPC: unsupported parameter type at index {i}: " +
                        $"'{args[i]?.GetType().FullName ?? "null"}'.",
                        nameof(args));
                paramBytes += sz;
            }

            // The bound that can actually be met, not the one this protocol
            // could carry.  Enforcing MaxPayloadBytes here declared legal every
            // call between MaxSendablePayloadBytes and 4096 — and the refusal
            // then came from PacketBuilder.Build, one layer down and outside
            // the caller's try, as an ArgumentException about a payload size
            // the caller never chose.
            if (paramBytes > MaxSendablePayloadBytes)
                throw new ArgumentException(
                    $"Enhanced RPC parameter data ({paramBytes} bytes) exceeds the " +
                    $"{MaxSendablePayloadBytes}-byte limit for a single " +
                    "datagram. Send the payload as game data in chunks, or reduce it: " +
                    "an RPC is not a file transfer, and IP fragmentation is unreliable " +
                    "on mobile and CGNAT links.",
                    nameof(args));

            int totalLen = RpcLimits.EnhancedRequestHeaderSize + paramBytes;
            var buf = new byte[totalLen];

            int offset = 0;

            // [0..3] method_id (LE u32)
            RpcSerializer.WriteU32LE(buf, offset, methodId);     offset += 4;

            // [4..11] sender_id (LE u64)
            RpcSerializer.WriteU64LE(buf, offset, senderId);     offset += 8;

            // [12..15] request_id (LE u32)
            RpcSerializer.WriteU32LE(buf, offset, requestId);    offset += 4;

            // [16..23] object_id (LE u64)
            RpcSerializer.WriteU64LE(buf, offset, objectId);     offset += 8;

            // [24] target (u8)
            buf[offset++] = (byte)target;

            // [25] rpc_flags (u8) — reserved
            buf[offset++] = 0x00;

            // [26] param_count (u8)
            buf[offset++] = (byte)args.Length;

            // [27…] typed parameter stream
            foreach (var arg in args)
                offset += RpcSerializer.WriteParam(arg, buf, offset);

            return buf;
        }
    }
}
