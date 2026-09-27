// RTMPE SDK — Runtime/Rpc/LegacyRpcVerifier.cs
//
// Pre-dispatch authorisation gate for the legacy MethodId-keyed RPC path
// (Rpc 0x50 without FLAG_ENHANCED_RPC).  AEAD authenticates the gateway as
// the relay, NOT the originating peer — exactly as for Enhanced RPC — so
// every wire-derived senderId / methodId pair must be passed through the
// same verification policy the enhanced parser enforces before the receiver
// invokes a handler.  Without this check a hostile peer can stamp a Ping
// or ApplyDamage with senderId=0 (the SDK's "uninitialised session"
// sentinel) or with the session id of another roster member and the
// receiver dispatches as if the gateway had attested origin.
//
// Trust model summary (mirrors EnhancedRpcVerifier.cs):
//   senderId   — wire-supplied; structurally rejected when zero, otherwise
//                deferred to EnhancedRpcVerifier.IsSenderAcceptable so a
//                roster-anchored verifier already wired by the integrator
//                covers both code paths.
//   methodId   — wire-supplied; per-method overrides may apply additional
//                checks (e.g. TransferOwnership goes through the existing
//                IsOwnershipTransferAuthorized predicate at the dispatch
//                site after this gate accepts).

namespace RTMPE.Rpc
{
    /// <summary>
    /// Decides whether a received built-in method-id call (<see cref="RpcMethodId"/>) is run.
    /// The SDK checks every such call with it; not intended to be called from game code.
    /// </summary>
    /// <remarks>
    /// It applies the sender check of <see cref="EnhancedRpcVerifier.IsSenderAcceptable"/>, so
    /// one sender check covers both kinds of call.
    /// </remarks>
    public static class LegacyRpcVerifier
    {
        /// <summary>
        /// Whether a built-in method-id call from <paramref name="senderId"/> is accepted:
        /// <see langword="false"/> for sender 0, otherwise the answer of
        /// <see cref="EnhancedRpcVerifier.IsSenderAcceptable"/>.
        /// </summary>
        /// <param name="senderId">The sender's session id, as the server reports it.</param>
        /// <param name="methodId">The call's method id. The check is the same for every
        /// id.</param>
        public static bool IsLegacyRpcAuthorized(ulong senderId, uint methodId)
        {
            // Zero is the SDK's pre-authentication sentinel and never a
            // legitimate origin for a legacy RPC.  EnhancedRpcVerifier
            // already enforces this guard but repeating it locally keeps
            // the contract explicit at the call site.
            if (senderId == 0UL) return false;
            return EnhancedRpcVerifier.IsSenderAcceptable(senderId);
        }
    }
}
