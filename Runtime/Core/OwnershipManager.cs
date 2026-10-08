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
    /// Moves networked objects between players. Access it through
    /// <see cref="SpawnManager.Ownership"/>.
    /// </summary>
    /// <remarks>
    /// The server decides every transfer. When it grants one, each client applies
    /// the change and each component's <c>OnOwnershipChanged</c> runs. When a player
    /// leaves, their objects whose <see cref="NetworkBehaviour.DestroyWithOwner"/> is
    /// <see langword="false"/> pass to the room's host on every client. Call every
    /// member from the Unity main thread.
    /// </remarks>
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

        // A subscriber to the unanswered-transfer report that throws (S4-47). Its
        // own gate: one sweep can retire several entries, and a handler that
        // throws on one tends to throw on all of them.
        private long _lastUnansweredTransferThrowWarnTicks;

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
        /// Creates an ownership manager. Not intended to be called from game code:
        /// use <see cref="SpawnManager.Ownership"/>.
        /// </summary>
        /// <param name="registry">The registry of live networked objects.</param>
        /// <param name="networkManager">The manager that sends the requests.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/> or <paramref name="networkManager"/> is <see langword="null"/>.
        /// </exception>
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
        /// The live networked objects a player owns.
        /// </summary>
        /// <param name="playerId">The player's id in the room.</param>
        /// <returns>A new list of the objects; empty for a null or empty id.</returns>
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
        /// Asks the server to give an object this client owns to another player.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the current owner may call it: for an object this client does not
        /// own it logs an error and sends nothing. It logs a warning and sends
        /// nothing for an unknown object or when not connected.
        /// </para>
        /// <para>
        /// Nothing changes locally when the request is sent. If the server grants
        /// the transfer, every client applies it and each component's
        /// <c>OnOwnershipChanged</c> runs. If no answer arrives within 10 seconds,
        /// <see cref="OnOwnershipTransferUnanswered"/> is raised.
        /// </para>
        /// </remarks>
        /// <param name="objectId">The network object id of the object to transfer.</param>
        /// <param name="newOwnerPlayerId">The id of the player who should own it.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="newOwnerPlayerId"/> is null or empty, or its UTF-8 encoding
        /// is longer than 256 bytes.
        /// </exception>
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

            // The object's transform this frame leaves before the transfer that
            // ends this client's ownership of it (audit P3-E3 review).
            _networkManager.FlushStateSyncBatch();
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
        /// Raised once when a transfer this client asked for with
        /// <see cref="RequestOwnershipTransfer"/> received no answer within 10
        /// seconds. The arguments are the object id and the player the transfer
        /// named.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The server does not answer a transfer it refuses, so this is how a
        /// refusal shows; but the transfer can also still be granted later. A grant
        /// that arrives after this event is applied by the other clients; this client
        /// applies it only if it is the room's host, and otherwise keeps treating
        /// itself as the owner.
        /// </para>
        /// <para>
        /// Treat the outcome as unknown rather than as a refusal: request again, or
        /// report the failure to the player. A handler that throws is caught and
        /// logged.
        /// </para>
        /// </remarks>
        public event Action<ulong, string> OnOwnershipTransferUnanswered;

        /// <summary>
        /// Carry the subscribers of the ownership manager this one replaces:
        /// rebuilt with the spawn manager on every connect and reconnect
        /// attempt, and this event is the only report of an unanswered
        /// transfer.
        /// </summary>
        internal void AdoptSubscribersFrom(OwnershipManager previous)
        {
            if (previous == null || ReferenceEquals(previous, this)) return;
            OnOwnershipTransferUnanswered =
                SubscriberAdoption.Carry(OnOwnershipTransferUnanswered, previous.OnOwnershipTransferUnanswered);
        }

        /// <summary>
        /// Drive the outstanding-request clock: sweep what has aged out and report
        /// it.  Called once per frame by <c>SpawnManager.Tick</c>, itself driven
        /// from <c>NetworkManager.Update</c>.
        /// </summary>
        /// <remarks>
        /// ⛔ The periodic driver is the repair, not an optimisation.  Both other
        /// callers of the sweep are ownership PACKET entry points, so it ran only
        /// when another ownership packet arrived — and the case it exists for is a
        /// session where none does.  This is ROOM-RD-03 / ROOM-RD-07 on a third
        /// door: a timeout that existed, was correct, and was reachable only from
        /// the path the timed-out case does not take.
        /// </remarks>
        internal void Tick() => PruneExpiredOutstanding();

        /// <summary>
        /// Sweep stale outstanding ids and report each one to
        /// <see cref="OnOwnershipTransferUnanswered"/>.
        /// </summary>
        /// <remarks>
        /// Reached from the two ownership packet entry points AND, since S4-47,
        /// from <see cref="Tick"/> every frame — so a session that goes quiet no
        /// longer holds its outstanding entries past the TTL.
        /// <para>
        /// ⛔ Every entry is retired BEFORE any subscriber runs, and the reason is
        /// RE-ENTRANCY.  A handler runs application code inside this sweep, and two
        /// of the things it would plausibly do — acknowledge a response, or drive a
        /// frame — re-enter this method.  Raised mid-loop, the inner sweep finds the
        /// same ids still on record and reports every one of them again — and the
        /// report re-enters, so it does not stop: the mutation that moved the raise
        /// into the loop did not merely duplicate a notice, it took the test host
        /// down with a stack overflow.  Retiring first makes the inner sweep find
        /// nothing, which is what bounds it.
        /// <para>
        /// 🚨 Measured, not reasoned: the first version of this comment claimed the
        /// damage was to the collection being enumerated, and it is not — `stale` is
        /// a list, so a handler adding a deadline is safe.  A mutation that moved the
        /// raise into the loop SURVIVED the whole suite until the re-entrant case
        /// below was written.  ROOM-RD-03's `SweepExpired` is the same lesson: ask
        /// what application code can do in the window a sweep hands it.
        /// </para>
        /// </para>
        /// </remarks>
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
            if (stale == null) return;

            // What each expiry was ABOUT, captured while the tables still hold it
            // and reported after every one of them is retired.
            List<(ulong ObjectId, string NewOwner)> unanswered = null;
            foreach (var id in stale)
            {
                if (_outstandingExpectations.TryGetValue(id, out var expectation))
                {
                    if (unanswered == null)
                        unanswered = new List<(ulong, string)>();
                    unanswered.Add(expectation);
                }
                _outstanding.Remove(id);
                _outstandingDeadlineMs.Remove(id);
                _outstandingExpectations.Remove(id);
            }

            if (unanswered == null) return;
            for (int i = 0; i < unanswered.Count; i++)
                RaiseUnanswered(unanswered[i].ObjectId, unanswered[i].NewOwner);
        }

        /// <summary>
        /// Announce one unanswered transfer, isolating each subscriber.
        /// </summary>
        /// <remarks>
        /// One throwing handler must not cost the others their notice, and must
        /// not throw out of the frame loop this is now driven from.  Reported on a
        /// gate of its own — a sweep can retire several entries in one frame, and
        /// a handler that throws on one tends to throw on all of them.
        /// </remarks>
        private void RaiseUnanswered(ulong objectId, string newOwnerPlayerId)
        {
            var handler = OnOwnershipTransferUnanswered;
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action<ulong, string>)subs[i])(objectId, newOwnerPlayerId); }
                catch (Exception ex)
                {
                    if (WarnGate.ShouldEmit(ref _lastUnansweredTransferThrowWarnTicks))
                        Debug.LogError(
                            "[RTMPE] OwnershipManager.OnOwnershipTransferUnanswered threw: " +
                            ex.GetType().Name + ": " + ex.Message);
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
        /// Test seam: the outstanding request ids themselves.
        /// </summary>
        /// <remarks>
        /// Per INSTANCE, which is the point: the id a request went out with can
        /// also be read from the packet builder, but that is a static the whole
        /// test assembly shares, so a case reading it is answered by whatever
        /// another class sent in parallel.
        /// </remarks>
        internal uint[] OutstandingRequestIdsForTest
        {
            get
            {
                var ids = new uint[_outstanding.Count];
                _outstanding.CopyTo(ids);
                return ids;
            }
        }

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
        /// Applies an ownership change granted by the server, on this client only.
        /// Not intended to be called from game code: to move an object, use
        /// <see cref="RequestOwnershipTransfer"/>.
        /// </summary>
        /// <remarks>
        /// Each component of the object is given the new owner and its
        /// <c>OnOwnershipChanged</c> runs. A grant with an empty owner, or for an
        /// object this client does not hold, is refused with a warning.
        /// </remarks>
        /// <param name="objectId">The network object id of the object.</param>
        /// <param name="newOwnerPlayerId">The id of the new owner.</param>
        /// <param name="serverAttested">
        /// <see langword="true"/> when the caller has established that the grant comes
        /// from the server. When <see langword="false"/>, the grant is applied only if
        /// it matches a transfer this client requested that has not yet timed out,
        /// and that request is then closed.
        /// </param>
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
        /// Same as <see cref="ApplyOwnershipGrant(ulong, string, bool)"/> with
        /// <c>serverAttested</c> set to <see langword="false"/>: the grant is applied
        /// only if it matches a transfer this client requested. Not intended to be
        /// called from game code.
        /// </summary>
        /// <param name="objectId">The network object id of the object.</param>
        /// <param name="newOwnerPlayerId">The id of the new owner.</param>
        public void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId)
            => ApplyOwnershipGrant(objectId, newOwnerPlayerId, serverAttested: false);

        /// <summary>
        /// Gives every object owned by <paramref name="fromPlayerId"/> whose
        /// <see cref="NetworkBehaviour.DestroyWithOwner"/> is <see langword="false"/>
        /// to <paramref name="toOwnerId"/>, on this client only. Not intended to be
        /// called from game code.
        /// </summary>
        /// <remarks>
        /// The SDK calls it on every client when a player leaves or the host changes,
        /// so every client gives the objects to the room's host. Nothing happens when
        /// either id is null or empty or the two are equal. An object that cannot take
        /// its new owner is logged and the rest are still reassigned.
        /// </remarks>
        /// <param name="fromPlayerId">The id of the player who left, or of the previous host.</param>
        /// <param name="toOwnerId">The id of the new owner, normally the room's host.</param>
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
