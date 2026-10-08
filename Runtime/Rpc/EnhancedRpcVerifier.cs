// RTMPE SDK — Runtime/Rpc/EnhancedRpcVerifier.cs
//
// Trust model for inbound Enhanced RPC packets:
//
//  Field        Trust source                  Verification on receive
//  ─────────    ───────────────────────────   ─────────────────────────
//  AEAD tag     gateway-attested              transport pipeline
//  methodId     wire-supplied                 looked up against the
//                                             receiving object's
//                                             [RtmpeRpc] map; missing
//                                             ⇒ drop
//  senderId     wire-supplied (hostile)       structural reject (zero)
//                                             + optional membership
//                                             callback (SenderVerifier)
//  requestId    wire-supplied                 opaque correlation token,
//                                             not security-relevant
//  objectId     wire-supplied (hostile)       must resolve to a live
//                                             entry in the spawn
//                                             registry; verified at
//                                             dispatch time by
//                                             NetworkManager
//  target       wire-supplied (hostile)       must be a defined value
//                                             of the RpcTarget enum;
//                                             undefined values ⇒ drop
//  rpc_flags    gateway-written where the     read as RpcCallerFacts only
//               gateway asserts               while GatewayAttestsCaller
//               AttestedRpcCaller; the        answers yes; otherwise no
//               caller's own byte otherwise   fact at all.  Decides a
//                                             method's declared Caller
//                                             (IsCallerPermitted) and
//                                             admits sender id 0 for a
//                                             frame the server wrote
//  params       wire-supplied (hostile)       per-type bounds checks in
//                                             RpcSerializer; INetwork-
//                                             Serializable type names
//                                             must resolve via the
//                                             explicit RpcTypeRegistry
//
// AEAD authenticates the gateway as the relay, NOT the originating peer.
// A malicious peer in the same room can craft any senderId/objectId/target
// it likes; the gateway only attests "I delivered this payload to you", not
// "this payload was honestly authored".  Treat every wire-derived field as
// hostile until verified.
//
// Extension hooks:
//  • SenderVerifier  — integrators set this delegate to gate inbound
//                      senderId values against their own room/session
//                      roster.  Default: self-only — DefaultSenderVerifier
//                      admits ONLY the local session id (the gateway's
//                      echo of the client's own RPCs); peers must be opted
//                      in explicitly, because AEAD authenticates the
//                      gateway as relay, not the originating peer.
//  • ObjectExistsVerifier — optional sanity hook.  NetworkManager
//                      already gates dispatch on the spawn registry,
//                      so the default returns true (no extra check
//                      beyond the registry lookup the dispatch path
//                      already performs).  Provided so security-
//                      sensitive games can layer additional checks
//                      (e.g. "is this object in the sender's interest
//                      set?") without monkey-patching NetworkManager.
//
// The verifier is intentionally a static, allocation-free policy object.
// Inbound RPC dispatch is on the hot path and must not allocate per
// packet.

using System;
using RTMPE.Core.Diagnostics;

namespace RTMPE.Rpc
{
    /// <summary>
    /// The checks a received <see cref="RtmpeRpcAttribute"/> call passes before it runs: its
    /// sender, the object it addresses, its target and its declared caller.
    /// </summary>
    /// <remarks>
    /// <para><c>NetworkManager</c> sets the hooks for every connection: the sender check is
    /// <see cref="RoomAnchoredSenderVerifier"/>, and the object check accepts only objects
    /// spawned on this client. <see cref="Reset"/> restores the defaults; the SDK calls it when
    /// a session ends and when Play mode starts.</para>
    /// <para>To install a check of your own, set the hook from <c>NetworkManager.OnConnected</c>;
    /// it stays until the session ends. A hook that throws refuses the call and logs an
    /// error.</para>
    /// </remarks>
    public static class EnhancedRpcVerifier
    {
        /// <summary>
        /// The sender check: returns <see langword="true"/> to accept a call from a sender
        /// session id. Sender 0 is always refused, and so is every sender while this is
        /// <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// While connected, <c>NetworkManager</c> sets it to
        /// <see cref="RoomAnchoredSenderVerifier"/>. The default, restored by
        /// <see cref="Reset"/>, accepts only this client's own session.
        /// </remarks>
        public static Func<ulong, bool> SenderVerifier { get; set; } = DefaultSenderVerifier;

        /// <summary>
        /// Returns this client's session id, for the default sender check. Set by
        /// <c>NetworkManager</c>. While it is <see langword="null"/> or returns 0, the default
        /// sender check refuses every sender.
        /// </summary>
        public static Func<ulong> SelfSessionIdProvider { get; set; }

        /// <summary>
        /// Conservative default sender policy.
        ///
       /// <para>Accepts ONLY the local session ID (as reported by
        /// <see cref="SelfSessionIdProvider"/>); every other senderId is
        /// rejected.  This is the only safe default: AEAD authenticates
        /// the gateway as relay but does not bind a packet to its
        /// originating peer, so a peer in the room can otherwise stamp
        /// any senderId it likes onto an RPC and impersonate any other
        /// room member.</para>
        ///
       /// <para>Integrators that need peer RPCs MUST opt in explicitly
        /// by either:</para>
        /// <list type="bullet">
        /// <item><description>installing a roster-aware delegate on
        ///   <see cref="SenderVerifier"/>;</description></item>
        /// <item><description>switching to
        ///   <see cref="RoomAnchoredSenderVerifier"/> with the room
        ///   hooks wired; or</description></item>
        /// <item><description>calling
        ///   <see cref="SetServerAttestedSenderVerifier"/> if (and only
        ///   if) the gateway is known to attest senderIds out-of-band.</description></item>
        /// </list>
        /// </summary>
        internal static bool DefaultSenderVerifier(ulong senderId)
        {
            if (senderId == 0UL) return false;
            var provider = SelfSessionIdProvider;
            if (provider == null) return false;
            ulong self;
            try { self = provider(); }
            catch (Exception)
            {
                return false;
            }
            return self != 0UL && senderId == self;
        }

        /// <summary>
        /// Sets <see cref="SenderVerifier"/> to <paramref name="verifier"/>, which then decides
        /// every call from a non-zero sender; sender 0 is still refused.
        /// </summary>
        /// <param name="verifier">Returns <see langword="true"/> to accept a sender session id.
        /// <see langword="null"/> restores the default check, which accepts only this client's
        /// own session.</param>
        public static void SetServerAttestedSenderVerifier(Func<ulong, bool> verifier)
        {
            if (verifier == null)
            {
                SenderVerifier = DefaultSenderVerifier;
                return;
            }
            SenderVerifier = senderId =>
            {
                if (senderId == 0UL) return false;
                return verifier(senderId);
            };
        }

        /// <summary>
        /// The object check: returns <see langword="true"/> to accept a call to an object id.
        /// <see langword="null"/> accepts every object.
        /// </summary>
        /// <remarks>
        /// While connected, <c>NetworkManager</c> sets it to accept only objects spawned on this
        /// client. A call is delivered only to a spawned object in any case.
        /// </remarks>
        public static Func<ulong, bool> ObjectExistsVerifier { get; set; }

        // One-time advisory used by the lobby / single-player fallback inside
        // RoomAnchoredSenderVerifier to surface the gap that no roster anchor
        // is in effect.  Spammy per-packet warnings are unacceptable on the
        // hot path.
        private static int _permissiveLegacyWarned;

        // Lobby / single-player fallback.  Accepts any non-zero senderId and
        // emits a one-time advisory.  Used ONLY by RoomAnchoredSenderVerifier
        // when the local SDK is outside a room — pre-room flows have no peer
        // roster to anchor against and the gateway is the only legitimate
        // counterparty.  Not exposed as a default policy because in-room
        // peer environments require explicit roster anchoring or self-only
        // rejection (see DefaultSenderVerifier).
        private static bool PermissiveLegacySenderVerifier(ulong senderId)
        {
            if (senderId == 0UL) return false;
            if (System.Threading.Interlocked.CompareExchange(
                    ref _permissiveLegacyWarned, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] EnhancedRpcVerifier accepting RPC outside any room " +
                    "without a roster anchor.  This is the lobby / single-player " +
                    "fallback only; in-room peer traffic must use a roster-aware " +
                    "or server-attested verifier.");
            }
            return true;
        }

        // ── Roster-anchored sender verification ────────────────────────────
        //
        // The roster anchor is a triple of optional callbacks.  When all are
        // supplied AND the local SDK is currently joined to a room, inbound
        // RPCs are accepted only when the wire-supplied senderId belongs to
        // the active room roster (or equals the local session ID).  When
        // any callback is missing, or the SDK is not currently in a room,
        // we fall back to the permissive default (non-zero accepted, with a
        // one-time warning) so single-player / lobby-browser flows still
        // work.  Wiring is performed by NetworkManager at construction time
        // and torn down on Cleanup() / ClearSessionData() to avoid a stale
        // closure outliving the manager that captured it.

        /// <summary>
        /// Returns whether this client is in a room, for
        /// <see cref="RoomAnchoredSenderVerifier"/>. Set by <c>NetworkManager</c>.
        /// </summary>
        public static Func<bool> IsRoomJoined { get; set; }

        /// <summary>
        /// Returns this client's session id, for <see cref="RoomAnchoredSenderVerifier"/>. Set by
        /// <c>NetworkManager</c>.
        /// </summary>
        public static Func<ulong> LocalSessionIdProvider { get; set; }

        /// <summary>
        /// Returns whether a sender session id belongs to a player in this client's room, for
        /// <see cref="RoomAnchoredSenderVerifier"/>.
        /// </summary>
        /// <remarks>
        /// While connected, <c>NetworkManager</c> sets it to accept every non-zero sender and log
        /// a one-time warning, because the room's player list does not carry session ids. Set
        /// your own to be stricter.
        /// </remarks>
        public static Func<ulong, bool> IsRosterMemberSession { get; set; }

        // Once-per-AppDomain warning emitted when a roster-anchored verifier
        // is in force but has no IsRosterMemberSession callback wired.  The
        // resulting "self-only" policy is conservative but may surprise
        // integrators who expected peer RPCs to flow; surface the gap once.
        private static int _rosterAnchorSelfOnlyWarned;

        /// <summary>
        /// The sender check <c>NetworkManager</c> installs in <see cref="SenderVerifier"/>.
        /// </summary>
        /// <remarks>
        /// Sender 0 is refused. Outside a room (see <see cref="IsRoomJoined"/>), every other
        /// sender is accepted, with a one-time warning. In a room, this client's own session
        /// (<see cref="LocalSessionIdProvider"/>) is accepted, and any other sender is decided by
        /// <see cref="IsRosterMemberSession"/>; while that is <see langword="null"/>, it is
        /// refused, with a one-time warning.
        /// </remarks>
        /// <param name="senderId">The sender's session id.</param>
        /// <returns><see langword="true"/> to accept the call.</returns>
        public static bool RoomAnchoredSenderVerifier(ulong senderId)
        {
            if (senderId == 0UL) return false;

            var inRoom = IsRoomJoined;
            if (inRoom == null || !inRoom())
            {
                // Outside a room (lobby / browse / single-player) we cannot
                // anchor against a roster — accept any non-zero sender so
                // SDK consumers do not break in pre-room flows.  Within
                // such flows the gateway is the only legitimate counter-
                // party and there is no peer roster to anchor against.
                return PermissiveLegacySenderVerifier(senderId);
            }

            var localProvider = LocalSessionIdProvider;
            ulong localId = localProvider != null ? localProvider() : 0UL;
            if (localId != 0UL && senderId == localId) return true;

            var rosterCheck = IsRosterMemberSession;
            if (rosterCheck != null) return rosterCheck(senderId);

            // No session-ID roster available — surface the gap once and
            // reject every non-self senderId.  This is the correct
            // conservative posture in untrusted-peer environments because a
            // roster anchor that admits anyone offers no improvement over
            // the permissive default.
            if (System.Threading.Interlocked.CompareExchange(
                    ref _rosterAnchorSelfOnlyWarned, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] EnhancedRpcVerifier roster anchor active but " +
                    "IsRosterMemberSession is not wired — accepting only " +
                    "self-originated RPCs while in a room.  Wire " +
                    "IsRosterMemberSession to admit peer RPCs.");
            }
            return false;
        }

        /// <summary>
        /// Returns whether this session's server reports the caller of every call it delivers
        /// (<see cref="RpcCallerFacts"/>). Set by <c>NetworkManager</c>.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/>, or a hook that throws, reads as no: received calls then carry
        /// no caller facts.
        /// </remarks>
        public static Func<bool> GatewayAttestsCaller { get; set; }

        /// <summary>
        /// Returns the answer of <see cref="GatewayAttestsCaller"/>, or
        /// <see langword="false"/> when it is <see langword="null"/> or throws.
        /// </summary>
        public static bool IsCallerAttested()
        {
            var hook = GatewayAttestsCaller;
            if (hook == null) return false;
            try { return hook(); }
            catch (Exception) { return false; }
        }

        private const RpcCallerFacts KnownCallerFacts =
            RpcCallerFacts.OwnsObject | RpcCallerFacts.IsHost | RpcCallerFacts.IsServer;

        /// <summary>
        /// Returns the caller facts of a received call that this client can trust.
        /// </summary>
        /// <remarks>
        /// <see cref="RpcCallerFacts.None"/> unless the server reports callers (see
        /// <see cref="IsCallerAttested"/>). <see cref="RpcCallerFacts.IsServer"/> counts only
        /// for sender 0, and then alone; bits this SDK version does not define are dropped.
        /// </remarks>
        /// <param name="rpcFlags">The caller facts the call carries.</param>
        /// <param name="senderId">The sender's session id.</param>
        public static RpcCallerFacts AttestedCallerFacts(byte rpcFlags, ulong senderId)
        {
            if (!IsCallerAttested()) return RpcCallerFacts.None;

            var facts = (RpcCallerFacts)rpcFlags & KnownCallerFacts;
            if (senderId != 0UL)
                return facts & ~RpcCallerFacts.IsServer;
            return (facts & RpcCallerFacts.IsServer) != 0
                ? RpcCallerFacts.IsServer
                : RpcCallerFacts.None;
        }

        /// <summary>
        /// Whether <paramref name="facts"/> say the server made the call. Such a call skips the
        /// sender check.
        /// </summary>
        public static bool IsServerOrigin(RpcCallerFacts facts)
            => (facts & RpcCallerFacts.IsServer) != 0;

        /// <summary>
        /// Whether a caller with <paramref name="facts"/> may call a method that declares
        /// <paramref name="declared"/>.
        /// </summary>
        /// <remarks>
        /// The server satisfies every declaration. <see cref="RpcCaller.Owner"/> needs
        /// <see cref="RpcCallerFacts.OwnsObject"/>, <see cref="RpcCaller.Host"/> needs
        /// <see cref="RpcCallerFacts.IsHost"/>, and <see cref="RpcCaller.Server"/> is satisfied by
        /// the server alone.
        /// </remarks>
        public static bool IsCallerPermitted(RpcCaller declared, RpcCallerFacts facts)
        {
            if ((facts & RpcCallerFacts.IsServer) != 0) return true;
            switch (declared)
            {
                case RpcCaller.Anyone: return true;
                case RpcCaller.Owner:  return (facts & RpcCallerFacts.OwnsObject) != 0;
                case RpcCaller.Host:   return (facts & RpcCallerFacts.IsHost) != 0;
                default:               return false;
            }
        }

        /// <summary>
        /// Returns why this client may not call a method that declares
        /// <paramref name="declared"/>, or <see langword="null"/> when it may.
        /// </summary>
        /// <remarks>
        /// The SDK asks this before it sends a call, so a call every receiver would refuse is
        /// reported where it was made. No client may make a <see cref="RpcCaller.Server"/> call.
        /// While the server does not report callers, a call to a method that declares
        /// <see cref="RpcCaller.Owner"/> or <see cref="RpcCaller.Host"/> is refused too, unless its
        /// target is <see cref="RpcTarget.Server"/>.
        /// </remarks>
        /// <param name="declared">The method's declared caller.</param>
        /// <param name="target">The method's target.</param>
        /// <param name="gatewayAttests">Whether this session's server reports callers.</param>
        /// <param name="isHost">Whether this client is the room's host.</param>
        /// <param name="ownsObject">Whether this client owns the object the call
        /// addresses.</param>
        public static string CallerSendRefusal(
            RpcCaller declared, RpcTarget target, bool gatewayAttests, bool isHost, bool ownsObject)
        {
            switch (declared)
            {
                case RpcCaller.Anyone:
                    return null;
                case RpcCaller.Server:
                    return "only a server function can make this call";
                case RpcCaller.Owner:
                case RpcCaller.Host:
                    break;
                default:
                    return "it declares a caller this SDK does not know";
            }
            if (target != RpcTarget.Server && !gatewayAttests)
                return "this session's gateway does not attest callers, so every client " +
                       "receiving it would refuse it";
            if (declared == RpcCaller.Host && !isHost)
                return "this client is not the room's host";
            if (declared == RpcCaller.Owner && !ownsObject)
                return "this client does not own the object";
            return null;
        }

        /// <summary>
        /// Restores every hook to its default: the sender check accepts only this client's own
        /// session, and every other hook is <see langword="null"/>. Also re-enables the one-time
        /// warnings.
        /// </summary>
        /// <remarks>
        /// The SDK calls it when a session ends and when Play mode starts.
        /// </remarks>
        public static void Reset()
        {
            SenderVerifier         = DefaultSenderVerifier;
            ObjectExistsVerifier   = null;
            IsRoomJoined           = null;
            LocalSessionIdProvider = null;
            IsRosterMemberSession  = null;
            SelfSessionIdProvider  = null;
            GatewayAttestsCaller   = null;
            System.Threading.Interlocked.Exchange(ref _permissiveLegacyWarned,     0);
            System.Threading.Interlocked.Exchange(ref _rosterAnchorSelfOnlyWarned, 0);
            // The hook-failure gates re-arm with the rest: this runs on entering
            // play mode, and a gate carrying the previous session's timestamp
            // would swallow the first failure of the new one for up to a second
            // — which is exactly the run a developer is watching.
            System.Threading.Interlocked.Exchange(ref _lastSenderVerifierThrewTicks, 0L);
            System.Threading.Interlocked.Exchange(ref _lastObjectVerifierThrewTicks, 0L);
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEnter() => Reset();

        /// <summary>
        /// Whether <paramref name="targetByte"/> is a declared <see cref="RpcTarget"/> value. A
        /// call with any other target is refused.
        /// </summary>
        public static bool IsTargetDefined(byte targetByte)
            => Enum.IsDefined(typeof(RpcTarget), targetByte);

        /// <summary>
        /// Whether a received call runs on this client: only when the target it carries equals
        /// the method's declared target, and that target is not <see cref="RpcTarget.Server"/>,
        /// which runs only in your server function.
        /// </summary>
        /// <param name="declaredTarget">
        /// The <see cref="RtmpeRpcAttribute.Target"/> of the method.
        /// </param>
        /// <param name="wireTarget">The target the received call carries.</param>
        public static bool IsDispatchPermitted(RpcTarget declaredTarget, RpcTarget wireTarget)
            => declaredTarget == wireTarget && declaredTarget != RpcTarget.Server;

        /// <summary>
        /// Whether a call from <paramref name="senderId"/> passes the sender check:
        /// <see langword="false"/> for sender 0 and while <see cref="SenderVerifier"/> is
        /// <see langword="null"/>; otherwise the answer of <see cref="SenderVerifier"/>.
        /// </summary>
        /// <remarks>
        /// A verifier that throws refuses the call and logs an error.
        /// </remarks>
        public static bool IsSenderAcceptable(ulong senderId)
        {
            if (senderId == 0UL) return false;
            var hook = SenderVerifier;
            // A sender-policy gate must fail closed.  The default verifier is
            // non-null (see the initialiser), so a null hook means an explicit
            // assignment cleared it: treat that as deny-all rather than
            // admit-all, matching the discipline applied to a throwing
            // verifier below.  A caller that intends to admit peers installs
            // a concrete verifier — the self-only DefaultSenderVerifier, or a
            // roster-aware delegate via SetServerAttestedSenderVerifier.
            if (hook == null) return false;
            // Verifier hooks must fail-closed: a buggy integrator delegate
            // (NRE on a stale roster reference, Dictionary mutation in
            // flight, …) must drop the packet rather than abort the parse
            // boundary.  Unhandled propagation here would tear down the
            // EnhancedRpcPacketParser and silently consume the rest of the
            // inbound buffer.
            try { return hook(senderId); }
            catch (Exception ex)
            {
                // Rate-limited and sanitised for the same two reasons the parser
                // that calls this gates its own five refusals: this runs once per
                // INBOUND PACKET, so a delegate that throws for one senderId
                // writes a stack-traced LogError per packet on the main thread,
                // and a hostile peer chooses the senderId — the gateway forwards
                // an unrecognised target to the whole room precisely because
                // this refusal is supposed to be cheap.  The message is the
                // integrator's own text and may quote what it was handed.
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastSenderVerifierThrewTicks))
                    UnityEngine.Debug.LogError(
                        "[RTMPE] EnhancedRpcVerifier.SenderVerifier threw " +
                        $"{UntrustedLogText.Sanitise(ex.GetType().Name)}: " +
                        $"{UntrustedLogText.Sanitise(ex.Message)}.  Treating as deny — " +
                        "fix the verifier delegate to fail-closed.");
                return false;
            }
        }

        // One-line-per-second gates for the two fail-closed catches above and
        // below.  Both are on the inbound parse path and both are decided by
        // bytes a remote peer chose, so an ungated emission is a remote write
        // into the player's log and onto its main thread.
        private static long _lastSenderVerifierThrewTicks;
        private static long _lastObjectVerifierThrewTicks;

        /// <summary>
        /// Whether a call to <paramref name="objectId"/> passes the object check:
        /// <see langword="true"/> while <see cref="ObjectExistsVerifier"/> is
        /// <see langword="null"/>; otherwise its answer.
        /// </summary>
        /// <remarks>
        /// A verifier that throws refuses the call and logs an error. A call is delivered only
        /// to a spawned object in any case.
        /// </remarks>
        public static bool IsObjectAcceptable(ulong objectId)
        {
            var hook = ObjectExistsVerifier;
            if (hook == null) return true;
            // Same fail-closed discipline as IsSenderAcceptable above.
            try { return hook(objectId); }
            catch (Exception ex)
            {
                // Its own gate, not the sender hook's: a project whose object
                // verifier throws permanently would otherwise spend a shared
                // gate every second and hide the sender hook's failures behind
                // it indefinitely.
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastObjectVerifierThrewTicks))
                    UnityEngine.Debug.LogError(
                        "[RTMPE] EnhancedRpcVerifier.ObjectExistsVerifier threw " +
                        $"{UntrustedLogText.Sanitise(ex.GetType().Name)}: " +
                        $"{UntrustedLogText.Sanitise(ex.Message)}.  Treating as deny.");
                return false;
            }
        }
    }
}
