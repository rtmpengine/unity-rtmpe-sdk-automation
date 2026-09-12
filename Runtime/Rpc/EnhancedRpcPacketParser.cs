// RTMPE SDK — Runtime/Rpc/EnhancedRpcPacketParser.cs
//
// Parses inbound Enhanced RPC payload bytes.
// The standard 13-byte packet header has already been stripped by PacketParser.ExtractPayload()
// before this parser is called.
//
// Enhanced RPC payload layout (all little-endian):
//  [method_id   :  4 LE u32]  FNV-1a("TypeName.MethodName")
//  [sender_id   :  8 LE u64]  wire-supplied; AEAD authenticates the relay,
//                             NOT the originating peer.  Treated as hostile
//                             input and validated via EnhancedRpcVerifier.
//  [request_id  :  4 LE u32]  client correlation ID (opaque)
//  [object_id   :  8 LE u64]  wire-supplied; cross-checked against the
//                             spawn registry by NetworkManager and via the
//                             optional EnhancedRpcVerifier.ObjectExistsVerifier.
//  [target      :  1 u8]      wire-supplied RpcTarget; undefined enum
//                             values are rejected before construction so
//                             downstream code never sees an out-of-range
//                             cast.
//  [rpc_flags   :  1 u8]      reserved
//  [param_count :  1 u8]      number of typed parameters
//  [params…]                  typed param stream (RpcSerializer format)
//
// Total fixed header: 27 bytes.
//
// Trust model — see EnhancedRpcVerifier.cs for the full policy table.
// Every field above except request_id is treated as attacker-controlled
// and gated through TryParse before an EnhancedRpcRequest is constructed.

using System;
using RTMPE.Core;
using RTMPE.Core.Diagnostics;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Parsed representation of an inbound Enhanced RPC request.
    /// </summary>
    public sealed class EnhancedRpcRequest
    {
        /// <summary>FNV-1a method ID (matches <c>RpcRegistry.ComputeMethodId</c>).</summary>
        public uint MethodId { get; }

        /// <summary>Gateway-verified sender session ID.</summary>
        public ulong SenderId { get; }

        /// <summary>Client-assigned correlation ID (round-tripped to response).</summary>
        public uint RequestId { get; }

        /// <summary>The <c>NetworkBehaviour.NetworkObjectId</c> that originated the call.</summary>
        public ulong ObjectId { get; }

        /// <summary>Delivery audience declared by the sender.</summary>
        public RpcTarget Target { get; }

        /// <summary>Decoded typed argument array (may be empty, never null).</summary>
        public object[] Args { get; }

        internal EnhancedRpcRequest(
            uint methodId, ulong senderId, uint requestId,
            ulong objectId, RpcTarget target, object[] args)
        {
            MethodId  = methodId;
            SenderId  = senderId;
            RequestId = requestId;
            ObjectId  = objectId;
            Target    = target;
            Args      = args ?? Array.Empty<object>();
        }
    }

    /// <summary>
    /// Parses Enhanced RPC payload bytes into a structured <see cref="EnhancedRpcRequest"/>.
    /// </summary>
    public static class EnhancedRpcPacketParser
    {
        // ── Diagnostic gates ──────────────────────────────────────────────────
        //
        // Every refusal below is decided by bytes the sender chose, so an
        // ungated `Debug.LogWarning` on any of them is a remote write into the
        // player's log and onto its main thread: Unity captures a stack trace
        // and writes Player.log synchronously, and one 0x52 replay frame carries
        // up to `MaxRpcBufferReplayEvents` events, each reaching this parser.
        //
        // 🔑 The gateway relies on this refusal being cheap.  It forwards an
        // unrecognised RPC target to the whole room deliberately — the comment
        // at `nats/broadcast.rs` says the SDK's verifier already refuses targets
        // it does not know — so the cost of that refusal is paid by every other
        // client in the room, not by the sender.
        //
        // One gate per reason rather than one shared, for the reason
        // `RpcSerializer` gives beside its three: a project sitting on one of
        // them permanently would otherwise spend the shared gate every second
        // and hide the others indefinitely.
        private static long _lastUndefinedTargetWarnTicks;
        private static long _lastSenderRefusedWarnTicks;
        private static long _lastObjectRefusedWarnTicks;
        private static long _lastParamCountWarnTicks;
        private static long _lastDeserialiseFailureWarnTicks;

        /// <summary>
        /// Attempt to parse an Enhanced RPC payload.
        /// </summary>
        /// <param name="payload">
        /// The RPC payload bytes (after the 13-byte standard packet header has been removed
        /// via <c>PacketParser.ExtractPayload()</c>).
        /// </param>
        /// <param name="request">Populated on success; <see langword="null"/> on failure.</param>
        /// <returns>
        /// <see langword="true"/> when parsing succeeded;
        /// <see langword="false"/> for truncated or malformed data.
        /// </returns>
        public static bool TryParse(byte[] payload, out EnhancedRpcRequest request)
        {
            request = null;

            if (payload == null || payload.Length < RpcLimits.EnhancedRequestHeaderSize)
                return false;

            int offset = 0;

            uint  methodId  = RpcSerializer.ReadU32LE(payload, offset); offset += 4;
            ulong senderId  = RpcSerializer.ReadU64LE(payload, offset); offset += 8;
            uint  requestId = RpcSerializer.ReadU32LE(payload, offset); offset += 4;
            ulong objectId  = RpcSerializer.ReadU64LE(payload, offset); offset += 8;

            byte targetByte = payload[offset++];
            // rpc_flags — reserved, skip
            offset++;
            byte paramCount = payload[offset++];

            // offset is now 27 (= EnhancedRequestHeaderSize)

            // ── Verification gate ────────────────────────────────────────────
            //
           // Every field below is wire-supplied and must be validated BEFORE
            // we allocate the args array or run user-supplied
            // NetworkDeserialize for INetworkSerializable params.  Failing
            // checks here drops the packet at the cheapest possible point
            // and prevents a crafted payload from reaching downstream code
            // that branches on these fields.

            // Reject undefined target enum values.  An unchecked
            // (RpcTarget)targetByte cast would silently propagate an
            // attacker-chosen byte (0x00–0xFF) into game code that switches
            // on Target — observed in the wild as a confused-deputy
            // primitive.  Enum.IsDefined here is acceptable for hot-path
            // use because RpcTarget is a small, sealed enum (≤ 4 entries).
            if (!EnhancedRpcVerifier.IsTargetDefined(targetByte))
            {
                if (WarnGate.ShouldEmit(ref _lastUndefinedTargetWarnTicks))
                {
                    Debug.LogWarning(
                        $"[RTMPE] EnhancedRpcPacketParser: dropped RPC with undefined " +
                        $"target byte 0x{targetByte:X2} from sender {LogRedaction.Redact(senderId)} " +
                        $"(method 0x{methodId:X8}).");
                }
                return false;
            }
            RpcTarget target = (RpcTarget)targetByte;

            // Reject senderIds that fail the configured policy.  Default
            // policy: senderId==0 (the SDK's "uninitialised session"
            // sentinel) is always rejected; non-zero values are accepted
            // until an integrator installs EnhancedRpcVerifier.SenderVerifier
            // to enforce a roster check.  Strict roster enforcement is the
            // recommended deployment configuration in untrusted-peer
            // environments.
            if (!EnhancedRpcVerifier.IsSenderAcceptable(senderId))
            {
                if (WarnGate.ShouldEmit(ref _lastSenderRefusedWarnTicks))
                {
                    Debug.LogWarning(
                        $"[RTMPE] EnhancedRpcPacketParser: dropped RPC with " +
                        $"unacceptable senderId {LogRedaction.Redact(senderId)} " +
                        $"(method 0x{methodId:X8}, object {objectId}).");
                }
                return false;
            }

            // Optional object-id sanity hook.  NetworkManager will perform
            // the authoritative spawn-registry lookup at dispatch time —
            // this hook lets integrators layer game-specific invariants
            // (ownership, interest sets) without modifying SDK code.
            if (!EnhancedRpcVerifier.IsObjectAcceptable(objectId))
            {
                if (WarnGate.ShouldEmit(ref _lastObjectRefusedWarnTicks))
                {
                    Debug.LogWarning(
                        $"[RTMPE] EnhancedRpcPacketParser: dropped RPC with " +
                        $"unacceptable objectId {objectId} " +
                        $"(sender {LogRedaction.Redact(senderId)}, " +
                        $"method 0x{methodId:X8}).");
                }
                return false;
            }

            // Decode typed parameters.  Each ReadParam either returns the
            // decoded value or sets offset to -1 on truncation / unknown
            // type.  Unknown INetworkSerializable type names resolve to
            // null via the explicit RpcTypeRegistry — they do NOT trip
            // the offset==-1 path; downstream argument-type validation in
            // NetworkBehaviour.DispatchEnhancedRpc rejects nulls bound to
            // non-nullable parameters.
            //
            // Pre-flight allocation guard.  Each parameter occupies AT LEAST
            // one byte on the wire (the type tag); a paramCount of 255 with
            // only a few bytes of payload remaining is otherwise allowed to
            // allocate a 255-element object[] before the per-param truncation
            // check fires.  Reject before the allocation when the declared
            // count cannot fit even one type tag per parameter.
            if (paramCount > payload.Length - offset)
            {
                if (WarnGate.ShouldEmit(ref _lastParamCountWarnTicks))
                {
                    Debug.LogWarning(
                        $"[RTMPE] EnhancedRpcPacketParser: declared paramCount {paramCount} " +
                        $"cannot fit in remaining {payload.Length - offset} bytes (method 0x{methodId:X8}).");
                }
                return false;
            }
            var args = new object[paramCount];
            for (int i = 0; i < paramCount; i++)
            {
                object val;
                try
                {
                    val = RpcSerializer.ReadParam(payload, ref offset);
                }
                catch (RpcDeserializationException ex)
                {
                    // INetworkSerializable parameter rejected — drop the
                    // entire RPC rather than dispatch a partial argument list.
                    // Both interpolations are wire-influenced: `TypeName` is
                    // documented as the name the inbound packet reported, and
                    // the message is the integrator's own exception text, which
                    // routinely embeds the value that failed to decode.  They
                    // go through the same sanitiser `RpcSerializer` applies one
                    // file away — control characters fold, length is capped —
                    // so a peer cannot rewrite a developer's terminal or forge
                    // a log line through this path.
                    if (WarnGate.ShouldEmit(ref _lastDeserialiseFailureWarnTicks))
                    {
                        Debug.LogWarning(
                            $"[RTMPE] EnhancedRpcPacketParser: rejected RPC due to " +
                            $"deserialise failure on parameter {i} " +
                            $"('{UntrustedLogText.Sanitise(ex.TypeName)}'): " +
                            $"{UntrustedLogText.Sanitise(ex.Message)}");
                    }
                    return false;
                }
                if (offset == -1)
                    return false;   // truncated or unknown type
                args[i] = val;
            }

            request = new EnhancedRpcRequest(
                methodId, senderId, requestId, objectId, target, args);
            return true;
        }
    }
}
