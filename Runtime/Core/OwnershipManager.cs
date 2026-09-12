// RTMPE SDK — Runtime/Core/OwnershipManager.cs
//
// Manages network object ownership.
//
// Security contract (unchanged from plan, hardened implementation):
//  • Only the SERVER can GRANT ownership transfers.
//  • Clients REQUEST a transfer; the server validates and confirms.
//  • Local state ONLY changes via ApplyOwnershipGrant(), called from
//    the packet handler after the server confirms.
//
// Design notes:
//  • RequestOwnershipTransfer() sends TransferOwnership RPC (Method ID 200)
//    to the server. The server validates and broadcasts OwnershipGrant to all
//    clients. Local state ONLY changes via ApplyOwnershipGrant() (server-authoritative).

using System;
using System.Collections.Generic;
using UnityEngine;
using RTMPE.Rpc;

namespace RTMPE.Core
{
    /// <summary>
    /// Manages ownership of <see cref="NetworkBehaviour"/> objects.
    /// Access via <c>SpawnManager.Ownership</c>.
    /// All methods must be called from the Unity main thread.
    /// </summary>
    public sealed class OwnershipManager
    {
        private readonly NetworkObjectRegistry _registry;
        private readonly NetworkManager _networkManager;
        private readonly Func<long> _clock;

        // Outstanding ownership-transfer correlation IDs.  An attacker who can
        // observe a session's traffic could otherwise predict the next id from
        // a plain monotonic counter and race a forged response into the open
        // correlation window before the genuine reply lands.  IDs are now
        // drawn from RequestIdAllocator (CSPRNG-backed); HandleOwnershipTransferResponse
        // refuses any id we did not issue, and unanswered ids are pruned after
        // OutstandingRequestTtlMs to bound memory and the spoofing surface.
        // A grant is applied once per inbound ownership packet, and both
        // refusals below are decided from the packet's own contents — an empty
        // owner, or an object this client does not hold — so their rate is the
        // sender's.  One gate per reason: the two say different things about
        // what arrived.
        private long _lastGrantEmptyOwnerWarnTicks;
        private long _lastGrantUnknownObjectWarnTicks;

        // The two refusals that answer a forged packet rather than a malformed
        // one. Both route through RtmpeLog, which decides a SEVERITY and never a
        // rate: it writes at Debug.Log when verbose logging is off and at
        // Debug.LogWarning when it is on, so an unbounded caller is unbounded
        // either way.
        private long _lastUnknownResponseIdWarnTicks;
        private long _lastUnattestedGrantWarnTicks;
        private long _lastOwnershipAnnouncementThrowWarnTicks;
        private long _lastReassignmentThrowWarnTicks;
        private long _lastReassignmentStrandedCountWarnTicks;

        // Bound once in the constructor so a handover hands the fan-out the
        // same delegate rather than allocating one per object. The gate it
        // reads is this manager's, like every other gate above, so a session's
        // report budget is its own.
        private readonly Action<INbLifecycle, Exception> _reportAnnouncementFault;

        private readonly HashSet<uint> _outstanding = new HashSet<uint>();
        private readonly Dictionary<uint, long> _outstandingDeadlineMs = new Dictionary<uint, long>(16);

        // Per-request expectation map: the (objectId, newOwnerPlayerId) the
        // local SDK actually asked the gateway to apply.  ApplyOwnershipGrant
        // accepts a self-initiated grant only when the inbound (objectId,
        // newOwnerPlayerId) tuple matches one of these expectations — a peer
        // that crafts a grant for an object the local SDK never requested
        // is rejected client-side, not just at IsOwnershipTransferAuthorized
        // (defence-in-depth).  Wire-format limitation: the gateway's
        // OwnershipGrant broadcast does not echo the originating request_id,
        // so correlation is performed by tuple match rather than ID match.
        private readonly Dictionary<uint, (ulong ObjectId, string NewOwner)> _outstandingExpectations
            = new Dictionary<uint, (ulong, string)>(16);

        // Ten seconds matches the worst-case RTT + server processing budget for
        // an ownership-transfer round trip.  Beyond that the response, if it
        // ever arrives, is too stale to be the original request's reply.
        internal const long OutstandingRequestTtlMs = 10_000;

        /// <summary>
        /// Create an OwnershipManager.
        /// </summary>
        /// <param name="registry">The shared object registry.</param>
        /// <param name="networkManager">
        /// The active NetworkManager; used to send RPC packets.
        /// </param>
        public OwnershipManager(NetworkObjectRegistry registry, NetworkManager networkManager)
            : this(registry, networkManager, null)
        {
        }

        /// <summary>
        /// Overload taking the monotonic clock the TTL is measured against, so a
        /// suite can reach an expiry without waiting one out.
        /// </summary>
        internal OwnershipManager(
            NetworkObjectRegistry registry, NetworkManager networkManager, Func<long> clock)
        {
            _registry       = registry       ?? throw new ArgumentNullException(nameof(registry));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _clock          = clock ?? NowMs;

            // Bound once, so a grant hands the fan-out the same delegate rather
            // than allocating one per handover.
            _reportAnnouncementFault = ReportAnnouncementFault;
        }

        // ── Queries ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all currently registered objects whose owner matches
        /// <paramref name="playerId"/>.
        /// </summary>
        /// <param name="playerId">Room player UUID to query.</param>
        public IReadOnlyList<NetworkBehaviour> GetObjectsOwnedBy(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return Array.Empty<NetworkBehaviour>();

            var result = new List<NetworkBehaviour>();
            foreach (var obj in _registry.GetAll())
            {
                // Unity null check guards destroyed-but-not-unregistered objects.
                if (obj != null && obj.OwnerPlayerId == playerId)
                    result.Add(obj);
            }
            return result;
        }

        // ── Mutations ──────────────────────────────────────────────────────────

        /// <summary>
        /// Request an ownership transfer to <paramref name="newOwnerPlayerId"/>.
        ///
       /// Sends a TransferOwnership RPC (method_id = 200) to the server.
        /// The server validates and, if approved, broadcasts an OwnershipGrant
        /// to all clients. Local state is NOT mutated — only the server can grant
        /// ownership via <see cref="ApplyOwnershipGrant"/>.
        /// </summary>
        /// <param name="objectId">Network object ID to transfer.</param>
        /// <param name="newOwnerPlayerId">Target player's room UUID.</param>
        public void RequestOwnershipTransfer(ulong objectId, string newOwnerPlayerId)
        {
            // Argument validation runs BEFORE the registry / ownership
            // checks because the latter return silently — a caller with
            // a null target playerId would otherwise observe the same
            // no-op as a caller targeting an unknown objectId, masking the
            // programming error.  Surface the contract violation as an
            // ArgumentException so test fixtures and integrators catch the
            // misuse at the call site instead of debugging a missing
            // ownership-grant on the peer.
            if (string.IsNullOrEmpty(newOwnerPlayerId))
                throw new System.ArgumentException(
                    "newOwnerPlayerId must not be null or empty.",
                    nameof(newOwnerPlayerId));

            var obj = _registry.Get(objectId);
            if (obj == null)
            {
                Debug.LogWarning($"[OwnershipManager] Object {objectId} not found in registry.");
                return;
            }

            if (!obj.IsOwner)
            {
                Debug.LogError(
                    $"[OwnershipManager] Cannot request ownership transfer for object {objectId}: " +
                    $"local player is not the current owner.");
                return;
            }

            if (!_networkManager.IsConnected)
            {
                Debug.LogWarning("[OwnershipManager] Cannot send ownership transfer: not connected.");
                return;
            }

            // CSPRNG-backed correlation id; rerolls if it would collide with
            // any already-outstanding request.  Tracking the issued id lets
            // HandleOwnershipTransferResponse drop forged responses whose
            // request_id we never sent.
            uint requestId = AllocateOutstandingRequestId();

            // The expectation is the right to admit an unattested grant naming
            // this tuple, so it is recorded only once the request exists to be
            // answered.  BuildTransferOwnership refuses an owner id whose UTF-8
            // encoding exceeds its wire field; recording first would leave that
            // right open for the whole TTL against a packet never sent.
            // ⚠️ The FRAMING is inside this too, and it was not.  The comment
            // above reasons about a right left open against a packet never
            // sent, and BuildPacket is the second way to fail to produce one:
            // it refuses a payload past PacketBuilder.MaxApplicationPayloadBytes
            // by throwing.  Not reachable today — BuildTransferOwnership caps
            // the owner id at 256 bytes, so the payload is 292 against a 1155
            // ceiling — but the ordering, not the arithmetic, is what the
            // comment claims.
            byte[] packet;
            try
            {
                byte[] rpcPayload = RpcPacketBuilder.BuildTransferOwnership(
                    _networkManager.LocalPlayerId,
                    requestId,
                    objectId,
                    newOwnerPlayerId);

                packet = _networkManager.BuildPacket(
                    PacketType.Rpc, PacketFlags.Reliable, rpcPayload);
            }
            catch
            {
                ReleaseOutstandingRequest(requestId);
                throw;
            }

            _outstandingExpectations[requestId] = (objectId, newOwnerPlayerId);

            _networkManager.Send(packet, reliable: true);
        }

        /// <summary>
        /// Validate an inbound ownership-transfer response against the set of
        /// request ids the local SDK actually sent.  Returns true when the id
        /// matches an outstanding request (which is then closed); false when
        /// it does not — meaning the response is stale, duplicated, or forged.
        /// </summary>
        internal bool TryAcknowledgeResponse(uint requestId)
        {
            PruneExpiredOutstanding();
            if (_outstanding.Remove(requestId))
            {
                _outstandingDeadlineMs.Remove(requestId);
                _outstandingExpectations.Remove(requestId);
                return true;
            }
            // Redacted: only the action is logged.  The id and remote endpoint
            // are intentionally withheld from the message body to avoid
            // teaching an attacker which forgery attempts succeeded in landing.
            if (WarnGate.ShouldEmit(ref _lastUnknownResponseIdWarnTicks))
                RtmpeLog.Warning(
                    "[OwnershipManager] Dropped ownership-transfer response: unknown or expired request id.");
            return false;
        }

        /// <summary>
        /// Sweep stale outstanding ids.  Both production callers are ownership
        /// packet entry points, so the sweep runs only when another ownership
        /// packet arrives: a session that goes quiet holds its outstanding
        /// entries past the TTL until the next one does.  No periodic caller
        /// exists — an earlier note here named one.
        /// </summary>
        internal void PruneExpiredOutstanding()
        {
            if (_outstandingDeadlineMs.Count == 0) return;
            long nowMs = _clock();
            List<uint> stale = null;
            foreach (var kv in _outstandingDeadlineMs)
            {
                if (kv.Value <= nowMs)
                {
                    if (stale == null) stale = new List<uint>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null)
            {
                foreach (var id in stale)
                {
                    _outstanding.Remove(id);
                    _outstandingDeadlineMs.Remove(id);
                    _outstandingExpectations.Remove(id);
                }
            }
        }

        private uint AllocateOutstandingRequestId()
        {
            // RequestIdAllocator.Next is CSPRNG-backed but does NOT know about
            // this manager's pending set; reroll up to a small bound to ensure
            // the chosen id is not already outstanding here.
            uint id;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                id = RequestIdAllocator.Next();
                if (id != 0 && !_outstanding.Contains(id))
                {
                    _outstanding.Add(id);
                    _outstandingDeadlineMs[id] = _clock() + OutstandingRequestTtlMs;
                    return id;
                }
            }
            // Saturation fallback.
            //
            // Earlier revisions used `id = 1u` whenever the CSPRNG returned
            // zero on the final attempt — a deterministic sentinel that two
            // colliding allocators would land on simultaneously, allowing a
            // hostile gateway to race a forged response against id=1 and
            // close a legitimate request.  The sentinel is replaced with a
            // probe past 1 that finds the first id not currently
            // outstanding; saturation is therefore a soft-failure where
            // the chosen id is *guaranteed* unused at allocation time.
            //
            // The probe range is the constant below, not a cap on outstanding
            // requests — there is none.  What bounds the set is the TTL: an
            // unanswered id is swept, so the live count is the request rate
            // times that window.  If all 256 probed ids are taken the manager
            // is saturated within this range and the entry with the earliest
            // deadline is evicted, so the new request proceeds without
            // collision.
            id = RequestIdAllocator.Next();
            if (id != 0 && !_outstanding.Contains(id))
            {
                _outstanding.Add(id);
                _outstandingDeadlineMs[id] = _clock() + OutstandingRequestTtlMs;
                return id;
            }

            const int FallbackProbeRange = 256;
            for (uint candidate = 1u; candidate <= FallbackProbeRange; candidate++)
            {
                if (!_outstanding.Contains(candidate))
                {
                    _outstanding.Add(candidate);
                    _outstandingDeadlineMs[candidate] = _clock() + OutstandingRequestTtlMs;
                    return candidate;
                }
            }

            // Genuine saturation: evict the entry with the earliest deadline
            // (= most likely already-orphaned) and reuse its slot.  Better
            // than a deterministic-collision sentinel because the evicted
            // request gets its own well-defined cancellation rather than a
            // silent hand-off.
            uint evictId = 0u;
            long evictDeadline = long.MaxValue;
            foreach (var kv in _outstandingDeadlineMs)
            {
                if (kv.Value < evictDeadline)
                {
                    evictDeadline = kv.Value;
                    evictId       = kv.Key;
                }
            }
            if (evictId != 0u)
            {
                _outstanding.Remove(evictId);
                _outstandingDeadlineMs.Remove(evictId);
                // Defence-in-depth: clear the evicted request's expectation
                // tuple before reusing its id slot.  The caller
                // (RequestOwnershipTransfer) overwrites the entry in the
                // immediate next statement, but if a future caller path
                // forgets to write the new tuple — or runs intermediate
                // code that reads the dictionary — the stale tuple from
                // the orphaned request must not be observable as a
                // "matching expectation" in ConsumeMatchingExpectation.
                // Removing here makes the eviction's effect on every
                // tracking structure symmetric.
                _outstandingExpectations.Remove(evictId);
                _outstanding.Add(evictId);
                _outstandingDeadlineMs[evictId] = _clock() + OutstandingRequestTtlMs;
                return evictId;
            }

            // Unreachable in practice — _outstanding cannot be empty AND
            // every probe candidate occupied — but keep a defined return
            // for the static analyser.  Defence-in-depth: scrub any stale
            // expectation that may have been left under id=1 by an earlier
            // path so the fallback id is in a clean state when the caller
            // writes the fresh tuple.
            _outstandingExpectations.Remove(1u);
            _outstanding.Add(1u);
            _outstandingDeadlineMs[1u] = _clock() + OutstandingRequestTtlMs;
            return 1u;
        }

        /// <summary>
        /// Forget an issued request id across every structure that tracks it.
        /// </summary>
        private void ReleaseOutstandingRequest(uint requestId)
        {
            _outstanding.Remove(requestId);
            _outstandingDeadlineMs.Remove(requestId);
            _outstandingExpectations.Remove(requestId);
        }

        private static long NowMs()
        {
            // Stopwatch-based monotonic clock survives wall-time adjustments.
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            return ticks * 1000L / System.Diagnostics.Stopwatch.Frequency;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test seam: clear the outstanding set without firing callbacks.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void ResetOutstandingForTest()
        {
            _outstanding.Clear();
            _outstandingDeadlineMs.Clear();
            _outstandingExpectations.Clear();
        }
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Returns true when the local SDK has an outstanding ownership-
        /// transfer request whose target tuple matches the inbound
        /// <paramref name="objectId"/> / <paramref name="newOwnerPlayerId"/>
        /// pair.  When it matches, the matching expectation is consumed so a
        /// later replay of the same grant cannot pass twice.  Used by the
        /// NetworkManager handler to authorise self-initiated grants
        /// independent of the (peer-supplied) wire <c>senderId</c>.
        /// </summary>
        internal bool ConsumeMatchingExpectation(ulong objectId, string newOwnerPlayerId)
        {
            PruneExpiredOutstanding();
            uint matchedId = 0;
            bool found = false;
            foreach (var kv in _outstandingExpectations)
            {
                if (kv.Value.ObjectId == objectId
                    && string.Equals(kv.Value.NewOwner, newOwnerPlayerId, StringComparison.Ordinal))
                {
                    matchedId = kv.Key;
                    found = true;
                    break;
                }
            }
            if (!found) return false;
            _outstanding.Remove(matchedId);
            _outstandingDeadlineMs.Remove(matchedId);
            _outstandingExpectations.Remove(matchedId);
            return true;
        }

        /// <summary>
        /// Test seam: number of outstanding ownership-transfer requests.
        /// </summary>
        internal int OutstandingCount => _outstanding.Count;

        /// <summary>
        /// Test seam: the latest deadline on record, in the units the TTL is
        /// written in.  The clock a caller does not supply is the one every
        /// production call site uses, and nothing else can observe it without
        /// waiting out a real TTL — so a suite that only ever injects a clock
        /// measures the seam and leaves the default unheld.
        /// </summary>
        internal long NewestOutstandingDeadlineMs
        {
            get
            {
                long newest = 0L;
                foreach (var kv in _outstandingDeadlineMs)
                {
                    if (kv.Value > newest) newest = kv.Value;
                }
                return newest;
            }
        }

        /// <summary>
        /// Apply a server-confirmed ownership grant.
        ///
       /// Called by the packet handler when the server broadcasts an
        /// OwnershipGrant (or OwnershipTransfer RPC response).
        /// This is the ONLY place where local ownership state changes.
        /// </summary>
        /// <param name="objectId">Network object whose ownership changed.</param>
        /// <param name="newOwnerPlayerId">Room UUID of the new owner.</param>
        /// <param name="serverAttested">
        /// Pass <see langword="true"/> ONLY when the grant came from a code
        /// path that has independently verified server origin (e.g. the
        /// master-client / initial-assignment branches of
        /// <c>NetworkManager.IsOwnershipTransferAuthorized</c>).  When
        /// <see langword="false"/>, the grant is admitted only if a
        /// matching outstanding request was issued from this SDK (tuple
        /// correlation).
        ///
       /// <para>Wire-format limitation: the gateway's broadcast does not
        /// echo the originating <c>request_id</c>, so correlation is
        /// performed by <c>(objectId, newOwnerPlayerId)</c> tuple match
        /// rather than ID match.  Changing the wire format would require
        /// a coordinated gateway-side parser update.</para>
        /// </summary>
        public void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId, bool serverAttested)
        {
            // An empty owner id is the wire encoding for a *release*, and it is
            // refused rather than applied.  Locally it reads as harmless —
            // NetworkBehaviour.IsOwner short-circuits on an empty owner, so no
            // client would originate an update — but that is the weaker half.
            // The gateway refuses an empty transferee at ingress (audit A6-3b,
            // forwarder.rs transfer_new_owner_ok) precisely because an
            // alive-but-unowned object falls into its per-object fail-open
            // path, where *any* room member's mutation is forwarded; the
            // fail-open argument on the ingress side is stated as depending on
            // that refusal.  This is the receive half of the same rule, so a
            // release arriving here is a grant no honest gateway relayed.
            if (string.IsNullOrEmpty(newOwnerPlayerId))
            {
                if (WarnGate.ShouldEmit(ref _lastGrantEmptyOwnerWarnTicks))
                    Debug.LogWarning(
                        $"[OwnershipManager] ApplyOwnershipGrant: refusing an empty owner for object {objectId}.");
                return;
            }

            var obj = _registry.Get(objectId);
            if (obj == null)
            {
                if (WarnGate.ShouldEmit(ref _lastGrantUnknownObjectWarnTicks))
                    Debug.LogWarning(
                        $"[OwnershipManager] ApplyOwnershipGrant: object {objectId} not found.");
                return;
            }

            if (!serverAttested)
            {
                if (!ConsumeMatchingExpectation(objectId, newOwnerPlayerId))
                {
                    // Redacted: the offending objectId / newOwner are intentionally
                    // not logged so a probe attacker cannot tune their forgery
                    // attempts against the response stream.
                    if (WarnGate.ShouldEmit(ref _lastUnattestedGrantWarnTicks))
                        RtmpeLog.Warning(
                            "[OwnershipManager] ApplyOwnershipGrant rejected: no matching outstanding request and grant is not server-attested.");
                    return;
                }
            }

            // The registry holds one anchor per object, but ownership is read
            // per component, so the handover is addressed to the object rather
            // than to the entry that routes it — see SetOwnerAll for what each
            // component does with the answer, and for why it arrives in two
            // passes.
            SpawnLifecycleOps.SetOwnerAll(
                obj.ObjectComponents, newOwnerPlayerId, _reportAnnouncementFault);
        }

        /// <summary>
        /// Backwards-compatible overload that defers to
        /// <see cref="ApplyOwnershipGrant(ulong, string, bool)"/> with
        /// <c>serverAttested = false</c>.  Existing call sites that relied
        /// on the prior unconditional behaviour MUST migrate to the
        /// explicit overload — passing <c>true</c> only when the caller
        /// has independently verified the grant's provenance.
        /// </summary>
        public void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId)
            => ApplyOwnershipGrant(objectId, newOwnerPlayerId, serverAttested: false);

        /// <summary>
        /// Reassign every surviving object owned by <paramref name="fromPlayerId"/>
        /// to <paramref name="toOwnerId"/> (NEW-OWNERSHIP-1 host migration).
        ///
        /// <para>"Surviving" means <see cref="NetworkBehaviour.DestroyWithOwner"/>
        /// is <see langword="false"/>; objects with <c>DestroyWithOwner=true</c>
        /// are destroyed on owner-leave by
        /// <see cref="SpawnManager.OnPlayerLeftRoom"/> and so are absent here.
        /// Without this, a non-destroy object owned by a departed player would
        /// freeze — owned by someone who is gone and updatable by no one.</para>
        ///
        /// <para>The grant is applied <c>serverAttested: true</c>: the new owner
        /// is the server-elected room host (<c>CurrentRoom.MasterId</c>) and the
        /// departed-owner / roster facts driving the call are server-broadcast
        /// replicated state, so this is a deterministic, locally-computed
        /// application of server authority — every client converges to the same
        /// owner with no per-object wire grant.  The caller decides whether the
        /// reassignment is warranted via
        /// <see cref="OwnershipReassignmentPolicy.ShouldReassign"/>; the guards
        /// here are a defensive second line only.</para>
        /// </summary>
        /// <param name="fromPlayerId">Room UUID of the departed/replaced owner.</param>
        /// <param name="toOwnerId">Room UUID of the new owner (the room host).</param>
        public void ReassignObjectsToNewOwner(string fromPlayerId, string toOwnerId)
        {
            if (string.IsNullOrEmpty(fromPlayerId) ||
                string.IsNullOrEmpty(toOwnerId) ||
                fromPlayerId == toOwnerId)
            {
                return;
            }

            var owned = GetObjectsOwnedBy(fromPlayerId);
            int stranded = 0;
            for (int i = 0; i < owned.Count; i++)
            {
                var obj = owned[i];
                // Unity null check guards destroyed-but-not-unregistered objects.
                if (obj == null || obj.DestroyWithOwner) continue;

                // Exception isolation: continue processing remaining objects.
                // The same rule, over the same list, that SpawnManager applies
                // to the objects it destroys in the other half of this handover
                // — one object that cannot take its new owner must not leave
                // every object after it owned by a player who has left, which
                // is the freeze this whole path exists to end.
                try
                {
                    ApplyOwnershipGrant(obj.NetworkObjectId, toOwnerId, serverAttested: true);
                }
                catch (Exception ex)
                {
                    stranded++;
                    if (WarnGate.ShouldEmit(ref _lastReassignmentThrowWarnTicks))
                        Debug.LogError(
                            $"[RTMPE] OwnershipManager: object {obj.NetworkObjectId} could not be " +
                            $"reassigned to the room host and is still owned by a player who has " +
                            $"left: {ex.GetType().Name}: {ex.Message}", obj);
                }
            }

            // The per-object line above reports ONE of the objects a migration
            // stranded; this reports how many there were, which is the number an
            // operator acts on. On its own gate, so the per-object line cannot
            // starve it — and gated at all because a departure is a wire event
            // and every diagnostic on an inbound path is.
            if (stranded > 1 && WarnGate.ShouldEmit(ref _lastReassignmentStrandedCountWarnTicks))
                Debug.LogError(
                    $"[RTMPE] OwnershipManager: {stranded} of {owned.Count} objects owned by " +
                    $"{fromPlayerId} could not be reassigned and are still owned by a player " +
                    "who has left.");
        }

        // A user OnOwnershipChanged that throws.
        //
        // Debug.LogError rather than RtmpeLog.Error, and the reason is the
        // opposite of the one that governs RtmpeLog: that facade lowers the
        // severity of the SDK's OWN routine faults so a host app's crash
        // reporters do not ingest a transport blip. This is not one of those —
        // it is a fault in the application's code, which those reporters exist
        // to receive.
        //
        // ⚠️ Rate-gated, so what an operator sees is that a handler threw, not
        // how many did: one host migration announces every component of every
        // object the departed player owned, and an ungated line per component
        // is a console the fault itself has made unreadable. The count that
        // matters — objects left with an absent owner — is reported once per
        // handover by the caller.
        private void ReportAnnouncementFault(INbLifecycle component, Exception ex)
        {
            if (!WarnGate.ShouldEmit(ref _lastOwnershipAnnouncementThrowWarnTicks)) return;
            Debug.LogError(
                $"[RTMPE] NetworkBehaviour.OnOwnershipChanged threw on " +
                $"{component.GetType().Name}: {ex.GetType().Name}: {ex.Message}",
                component as UnityEngine.Object);
        }
    }
}
