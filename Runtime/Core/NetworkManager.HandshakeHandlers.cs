// RTMPE SDK — Runtime/Core/NetworkManager.HandshakeHandlers.cs
//
// Handshake coroutine + Challenge/SessionAck packet handlers + auto-rejoin.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Core.Protocol;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Handshake coroutine ────────────────────────────────────────────────

        /// <summary>
        /// Wait for the UDP transport to bind (background thread sets LocalEndPoint),
        /// then build and transmit the encrypted HandshakeInit packet.
        ///
       /// Polls LocalEndPoint each frame.  The background thread's
        /// _transport.Connect() includes DNS resolution, so on a cold OS
        /// resolver cache — or under heavy first-frames (shader warm-up,
        /// on-access AV scans of a fresh binary) — the bind can take several
        /// seconds.  The wait budget therefore tracks the connection
        /// watchdog's own budget (see <see cref="TransportBindWaitPolicy"/>):
        /// the init is dispatched the moment the transport binds, and only
        /// the watchdog declares the attempt failed.
        /// </summary>
        private IEnumerator HandshakeInitCoroutine(string apiKey)
        {
            float maxWaitSecs = TransportBindWaitPolicy.MaxWaitSeconds(_settings.connectionTimeoutMs);
            float waited = 0f;

            while (TransportBindWaitPolicy.ShouldKeepWaiting(
                       transportBound: _transport.LocalEndPoint != null,
                       attemptActive:  _state == NetworkState.Connecting,
                       waitedSeconds:  waited,
                       maxWaitSeconds: maxWaitSecs))
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            _connectCoroutine = null;

            // The attempt was torn down while waiting (user disconnect or a
            // transport error already reported through its own path) — a late
            // HandshakeInit must not be emitted against a dead attempt.
            if (_state != NetworkState.Connecting)
                yield break;

            if (_transport.LocalEndPoint == null)
            {
                if (IsDebugLogEnabled)
                    LogDebug("Transport did not bind within the connection-timeout budget — timeout coroutine will handle failure.");
                yield break;
            }

            SendHandshakeInit(apiKey);
        }

        /// <summary>
        /// **N-1** — reconnect variant of <see cref="HandshakeInitCoroutine"/>.
        /// Waits for the UDP transport to bind, then sends a single
        /// <c>ReconnectInit</c> carrying the stored reconnect token.
        /// </summary>
        /// <remarks>
        /// The server's response is a normal <see cref="PacketType.Challenge"/>,
        /// handled by the same pipeline as the full handshake.  No extra
        /// client-side state machine is required — the Reconnecting state just
        /// marks the intent for observers.
        /// </remarks>
        private IEnumerator ReconnectInitCoroutine()
        {
            float maxWaitSecs = TransportBindWaitPolicy.MaxWaitSeconds(_settings.connectionTimeoutMs);
            float waited = 0f;

            while (TransportBindWaitPolicy.ShouldKeepWaiting(
                       transportBound: _transport.LocalEndPoint != null,
                       attemptActive:  _state == NetworkState.Reconnecting,
                       waitedSeconds:  waited,
                       maxWaitSeconds: maxWaitSecs))
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            _connectCoroutine = null;

            // Mirrors HandshakeInitCoroutine: never emit a ReconnectInit for
            // an attempt whose state machine has already moved on.
            if (_state != NetworkState.Reconnecting)
                yield break;

            if (_transport.LocalEndPoint == null)
            {
                if (IsDebugLogEnabled)
                    LogDebug("ReconnectInit: transport did not bind within the connection-timeout budget — timeout coroutine will handle failure.");
                yield break;
            }

            SendReconnectInit(_reconnectToken);
        }

        // ── Receive path (raised on network thread → marshalled to main thread) ─

        // Pre-resolved delegate for the per-packet main-thread dispatch.  The
        // static lambda has no captured state so the runtime emits exactly one
        // delegate instance for the entire process lifetime — no per-packet
        // closure box, no captured `this` pointer.  ProcessPacketAndReturn
        // reads `NetworkManager.Instance` to find the live receiver, which is
        // safe because the receive path is unconditionally torn down with the
        // singleton in OnDestroy.
        private static readonly Action<byte[], int> s_ProcessPacketDispatch =
            ProcessPacketAndReturn;

        // Drain handler executed on the Unity main thread.  Consumes the
        // pool-rented buffer end-to-end and returns it to the pool exactly
        // once.  The exception handling here is ownership-critical, not
        // cosmetic: MainThreadDispatcher.Update balances the rental itself
        // whenever a buffer action THROWS (its contract assumes a throwing
        // consumer never reached its own return).  If an exception escaped
        // this method after the finally below had already returned the
        // buffer, the dispatcher would return the same array a second time
        // and ArrayPool could lease it to two renters at once — silent
        // cross-packet corruption of inbound bytes and AEAD plaintext.
        // Catching here keeps a single owner for the rental and preserves
        // the dispatcher's balancing path for consumers that do not return
        // in a finally.
        private static void ProcessPacketAndReturn(byte[] buffer, int length)
        {
            var instance = System.Threading.Volatile.Read(ref _instance);
            if (instance == null)
            {
                // Manager was torn down between dispatch and drain — return
                // the rented buffer so the pool does not leak.
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                return;
            }

            try
            {
                instance.ProcessPacket(buffer, length);
            }
            catch (Exception ex)
            {
                // Same visibility the dispatcher's catch would have given —
                // the packet is lost either way; the pool must stay intact.
                //
                // ⛔ Gated: a handler fault that is deterministic in the frame
                // repeats for every datagram carrying it, by construction, and
                // this is the OUTERMOST catch — nothing below it bounds the rate
                // (`RPC-RD-05`).  🔑 Static, because the method is: an instance
                // field is not reachable from here, and the budget is per
                // process rather than per manager, which is the right grain for
                // a fault in the one receive path a process has.
                if (ShouldWarn(ref s_lastProcessPacketThrowWarnTicks))
                Debug.LogError(
                    $"[RTMPE] ProcessPacket threw while handling an inbound " +
                    $"packet; the frame is dropped and the receive buffer " +
                    $"returned once.\n{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        // Invoked SYNCHRONOUSLY on the network background thread for every
        // datagram.  The `rented` argument is owned by NetworkThread and is
        // returned to ArrayPool the moment this method returns, so we copy
        // the meaningful prefix into a SEPARATE pool rental that is ours to
        // hand off to the main thread.  Net effect: one pool rent + one
        // pool return per packet, zero managed-heap allocation in steady
        // state (delegate is pre-resolved, dispatcher work item carries the
        // (buffer, length) pair inline).
        private void HandlePacketReceivedRented(byte[] rented, int offset, int length)
        {
            // Telemetry — count wire-level inbound packets / bytes BEFORE
            // dispatching to the main thread so the metrics reflect the raw
            // socket-level traffic regardless of any decryption / decompression
            // applied later in ProcessPacket.  Interlocked.Add is lock-free
            // and safe to call from the network thread.
            if (length <= 0) return;
            System.Threading.Interlocked.Increment(ref _packetsIn);
            System.Threading.Interlocked.Add(ref _bytesIn, length);

            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

            // Re-rent so the buffer survives the cross-thread hop.  Rent may
            // hand back an array larger than `length`; the dispatched length
            // is the authoritative byte count for ProcessPacket, never
            // `owned.Length`.
            var owned = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            bool accepted = false;
            try
            {
                Buffer.BlockCopy(rented, offset, owned, 0, length);
                accepted = dispatcher.Enqueue(s_ProcessPacketDispatch, owned, length);
            }
            catch
            {
                // Enqueue failure (queue full + Throw policy, or OOM during
                // segment grow) must release the rental to keep the pool
                // honest — the dispatched drain will never run for this
                // buffer if we throw.
                System.Buffers.ArrayPool<byte>.Shared.Return(owned, clearArray: true);
                throw;
            }

            // Backpressure rejection (DropTail / DropHead) returns the rental
            // synchronously; ProcessPacketAndReturn never runs for a dropped
            // packet so the rental would otherwise leak.  The throttled
            // warning surfaces the drop without flooding the log under
            // sustained backpressure.
            if (!accepted)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(owned, clearArray: true);
                MaybeWarnRentedPacketDropped();
            }
        }

        // Throttle the dropped-rented-packet warning to at most one log per
        // second.
        private long _lastRentedDropWarnTicks;

        // 🔑 This was a hand-written copy of WarnGate until 2026-09-11, and the
        // copy had drifted: it compared `now - last < oneSecond` with no
        // `now >= last` clamp, so a timestamp reading behind the recorded one
        // held the gate SHUT for the length of the step — the one failure
        // WarnGate's own comment says a diagnostic must not have.  A second
        // implementation of one rule is a second thing to keep correct, and the
        // rule that reads this file could not see it either.
        private void MaybeWarnRentedPacketDropped()
        {
            if (!ShouldWarn(ref _lastRentedDropWarnTicks)) return;

            // Redacted: the log mentions only the bounded total, never the
            // dropped packet's content or peer address.
            Debug.LogWarning(
                "[RTMPE] Inbound packet dropped by MainThreadDispatcher backpressure; " +
                "pool rental returned.  See dispatcher.DroppedRentedPacketCount for the running total.");
        }

        /// <summary>
        /// Single funnel for every wire-level outbound packet.  Increments the
        /// <c>_packetsOut</c> / <c>_bytesOut</c> telemetry counters and
        /// forwards to <see cref="NetworkThread.SendOwned"/>.  All historical
        /// call sites that previously called <c>_networkThread.SendOwned</c>
        /// directly route through this helper so the Network Debugger sees a
        /// complete picture of outbound traffic.
        ///
       /// <para>The packet must be owned by the caller (defensive copies are
        /// the caller's responsibility, matching the original SendOwned
        /// contract).</para>
        /// </summary>
        private void SendToWire(byte[] packet)
        {
            if (packet == null) return;

            // Capture hook used by EncryptAndSendRedundant: when set, the
            // post-encryption bytes are routed to the override instead of the
            // real network thread.  Telemetry counters intentionally do NOT
            // tick for the captured copy — only the actual wire copies count.
            var capture = _wireSendOverride;
            if (capture != null)
            {
                AssertWireSendOverrideMainThread(nameof(SendToWire));
                capture(packet);
                return;
            }

            if (_networkThread == null) return;

            System.Threading.Interlocked.Increment(ref _packetsOut);
            System.Threading.Interlocked.Add(ref _bytesOut, packet.Length);

            _networkThread.SendOwned(packet);
        }

        // Rate-limit malformed-packet warnings to 1Hz so a hostile flood
        // cannot drive GC pressure via the log pipeline — each interpolated
        // $"..." would otherwise allocate a fresh formatted System.String,
        // and at 1 kHz of bad packets that cost dominates the receive thread
        // and the Update-frame budget on mobile.  A separate timestamp per
        // site means a slow-drip flow of one bad-magic + one bad-version
        // still surfaces both warnings; the cap is per category, not global.
        // The outermost ProcessPacket catch's budget.  Static: the method that
        // reports it is static, so no instance field is in scope.
        private static long s_lastProcessPacketThrowWarnTicks;

        // Spent by the two "too short to contain a valid header" reports and by
        // nothing else: they are the same sentence, word for word, raised from
        // the cheap pre-check and from the validator that repeats it.
        private long _lastBadHeaderWarnTicks;
        // ⛔ Their own budgets, not the one above.  Three different sentences
        // shared `_lastBadHeaderWarnTicks` until 2026-09-11, which is the shape
        // `NoGateIsSharedBetweenTwoDiagnostics` exists to refuse: a flood of
        // unknown packet types decided whether a malformed flags byte — a
        // protocol fault an operator needs to see once — was ever printed.
        private long _lastMalformedFlagsWarnTicks;
        private long _lastUnknownTypeWarnTicks;
        private long _lastBadMagicWarnTicks;
        private long _lastBadVersionWarnTicks;
        private long _lastAeadFailWarnTicks;

        // The LZ4 decompression failure's budget, declared here with the
        // other AeadPipeline gates it sits beside on the receive path.
        private long _lastLz4DecompressFailWarnTicks;

        // The legacy-RPC authorisation refusal's budget (ReceivePath).
        private long _lastLegacyRpcRefusedWarnTicks;

        // The uninitialised-replay-window refusal's budget (AeadPipeline).
        private long _lastReplayWindowMissingWarnTicks;
        private long _lastReplayWindowDropWarnTicks;
        private long _lastMissingEncryptionWarnTicks;
        private long _lastInboundFloodWarnTicks;
        private long _lastPreSessionGameDataWarnTicks;
        private long _lastMasterClientTransferWarnTicks;
        private long _lastEarlyObjectEvictWarnTicks;

        // One budget per failure reason rather than one for all of them:
        // the reasons name different repairs, and the one a deployment
        // without a bound server handler returns for ever would otherwise
        // spend a shared gate every second and hide the rest.
        private readonly RTMPE.Core.Rpc.RpcFailureGates _rpcFailureGates =
            new RTMPE.Core.Rpc.RpcFailureGates();

        // Inbound packet rate limiter — extracted to RTMPE.Core.Protocol.InboundBudget.
        // The state + token-bucket math live in that class; NetworkManager keeps
        // a single field reference and a thin public passthrough for the
        // observability counter.  The bucket starts at the pre-room defaults
        // (burst 3000 / sustained 1500 pps) and is resized to the negotiated
        // room capacity on join (see the OnRoomManager* handlers), since
        // peer-to-peer fan-out scales with member count.  See
        // Runtime/Core/Protocol/InboundBudget.cs for the full threading contract
        // and threat-model rationale.
        private readonly InboundBudget _inboundBudget = new InboundBudget();

        // Defence-in-depth: explicit session-established flag.  Game-data
        // packet handlers (Spawn / VariableUpdate / RPC / property
        // broadcasts) require BOTH _state == InRoom AND
        // _sessionEstablished — currently equivalent because InRoom is only
        // reachable after a successful SessionAck, but the redundant gate
        // closes any future state-machine refactor that decouples the two.
        // Set in OnSessionAck; cleared in TransitionTo(Disconnected).
        //
        // All accesses run on the Unity main thread: the witness is written in
        // OnSessionAck and cleared in TransitionTo, and the admission gate that
        // reads it sits inside ProcessPacket — which the network-receive thread
        // reaches only after marshalling the packet through the main-thread
        // dispatcher (network-thread teardown events are likewise re-dispatched
        // before touching state).  The volatile qualifier is therefore not
        // load-bearing for the current single-threaded access pattern; it is
        // retained as a low-cost barrier so the witness stays correctly
        // published if a later refactor ever observes it off the main thread.
        private volatile bool _sessionEstablished;

        // Handshake-progress witnesses for the connection-failure diagnostic.
        // A timeout otherwise surfaces only as an opaque "Timeout"; these record
        // how far the current attempt's handshake ladder climbed so the timeout
        // path can name the exact rung that stalled. Both are touched only on the
        // Unity main thread (SendHandshakeInit / OnChallenge / the timeout
        // coroutine), so neither needs the volatile barrier _sessionEstablished
        // carries for its cross-thread admission-gate role. Reset per attempt in
        // ClearSessionData.
        private bool _diagHandshakeInitSent;
        private bool _diagChallengeReceived;

        // One Challenge is acted on per handshake attempt. Cleared wherever a
        // fresh HandshakeHandler is constructed, which is the boundary of an
        // attempt on both the connect and the reconnect path, so the reset does
        // not depend on ClearSessionData having run in between. Main-thread
        // only, like the two witnesses above.
        private bool _challengeAccepted;

        // ── Pre-session rejection record ──────────────────────────────────────
        //
        // A Challenge (0x06) and a HandshakeError (0x0B) are exempt from
        // encryption and from the session gate — correctly, because no key
        // exists that early — so neither carries anything this client can
        // authenticate.  Acting on one lets a single spoofed datagram end the
        // attempt, which is the capability `OnSessionAck` already refuses to
        // grant a cleartext bootstrap envelope, in the same words.  Both
        // handlers therefore RECORD the diagnosis and drop the frame; the
        // connection watchdog remains the terminal authority and surfaces what
        // was recorded, so the developer still learns "wrong API key" instead of
        // a bare timeout while the attacker no longer chooses WHEN the attempt
        // ends — the only thing the forged frame ever bought.
        //
        // 🔑 Every recorded reason is labelled UNVERIFIED, and that label is not
        // decoration.  The category byte of a HandshakeError is chosen by
        // whoever sent the frame, so recording its description unqualified would
        // trade a kill switch for a lie switch: one spoofed 0x0B naming
        // "invalid API key" would make an unreachable gateway report a bad
        // credential, and the developer would rotate a live key over it.  The
        // string says so, so the diagnosis stays useful and stays honest.
        //
        // ⛔ First-wins, and NEITHER polarity is safe on its own: an attacker
        // injects a full RTT before any genuine reply, so first-wins prefers
        // theirs — and last-wins hands it to whoever floods last.  The label is
        // what makes the choice survivable; first-wins is then the better half,
        // because a flood cannot erase what was already recorded.
        //
        // The text itself is never the server's: it is this client's own
        // catalogue (`PacketGates.DescribeHandshakeError`) or a constant written
        // here.  The server-supplied note is scrubbed and logged for context
        // only, never surfaced.
        //
        // Main-thread only, like the witnesses above, and reset on the same
        // attempt boundary by ResetChallengeAdmission.
        private string _pendingHandshakeRejection;

        // ⛔ There is deliberately NO per-attempt cap on rejected Challenges,
        // and the first version of this repair had one.  Dropping a rejected
        // frame instead of ending the attempt does mean this handler can be
        // re-entered per packet, and Ed25519 verification is the expensive half
        // of ValidateChallenge — but a cap counted per attempt is spent ONLY by
        // frames that fail, which is to say only by an attacker, and the frame
        // it then locks out is the genuine one.  That is the very lockout the
        // _challengeAccepted comment above refuses in its last sentence,
        // rebuilt with 32 slots instead of one — and it turned a one-datagram
        // kill into a permanent one, because every retry re-arms a budget the
        // flood re-spends.
        //
        // The bound that is real is Gate 2 of ProcessPacket — InboundBudget's
        // token bucket, consumed before the header is even validated.  🔑 And it
        // cannot lock anything out, because a rate limit does not discriminate
        // by outcome, which is exactly the property a per-attempt count of
        // failures does not have.
        //
        // ⚠️ Its rate is 1500 pps only OUTSIDE a room, and an earlier draft of
        // this comment quoted that number as though it were the bound.  It is
        // not: `ConfigureForRoomSize` raises it to 300 + (maxPlayers-1)x150 at
        // room entry, and the restore was reached only from an EXPLICIT leave —
        // so a client that dropped out of a 100-player room carried 15 150 pps
        // into its next handshake.  `ClearSessionData` now restores it with the
        // rest of the per-session state, which is what makes the pre-room
        // figure true of a handshake again.

        // The witness the reconnect-token retention decision reads, and NOT the
        // one the timeout diagnostic reads.  `_diagChallengeReceived` records
        // that a Challenge-shaped frame ARRIVED and is deliberately set before
        // validation, so a rejection is attributed to "reply not finalised"
        // rather than "no server reply".  A security decision cannot use that:
        // the gateway spends the single-use reconnect token when it accepts a
        // ReconnectInit, so only a Challenge that VALIDATED proves the token is
        // gone — and a forged one would otherwise make this client discard a
        // token that is still good.  This is also the more precise witness for
        // the IP-migration-proof clause that decision states, because the write
        // it guards lives past validation too.
        private bool _diagChallengeValidated;

        // The pinning verdict for THIS attempt, resolved once by whichever
        // Round-1 sender ran, and never re-resolved per frame.
        //
        // 🔑 Resolving it inside OnChallenge was worse than it looked.  It is not
        // a cheap local computation: under TrustOnFirstUse with no configured
        // pin, `ServerKeyPinning.PreparePin` calls `TryLoadAuthoritative`, which
        // on the shipped `EncryptedFilePinStore` opens the pin file, reads it
        // whole and HMAC-verifies every record — and `MigratingPinStore.Load`
        // may perform an atomic WRITE of that file as a consequence of a read.
        // Re-parsing `pinnedServerPublicKeyHex` sat on the same path and THREW
        // once per frame when it was malformed.  While a rejected Challenge
        // ended the attempt none of that mattered: it ran once.  Dropping the
        // frame instead means it would have run once per attacker packet, at
        // the inbound budget's rate, as synchronous file I/O on the main
        // thread.  ⛔ So the repair is not to bound the reads — it is to have
        // one.
        //
        // A Challenge that arrives before the resolution exists is dropped: the
        // resolution is written by the Round-1 send, and until that send happens
        // there is no attempt for a genuine Challenge to belong to.
        private PinResolution _attemptPinResolution;
        private bool          _attemptPinResolved;

        // The single definition of the per-attempt Challenge admission state.
        // Called wherever a fresh HandshakeHandler is constructed — the boundary
        // of an attempt on both the connect and the reconnect path — and from
        // ClearSessionData, so no reset depends on the other having run.
        private void ResetChallengeAdmission()
        {
            _challengeAccepted         = false;
            _pendingHandshakeRejection = null;
            _diagChallengeValidated    = false;
            _attemptPinResolved        = false;
            _attemptPinResolution      = default;
        }

        // Every reason recorded from a pre-session frame passes through here, so
        // the qualification cannot be forgotten at one call site.
        private static string UnverifiedRejection(string reason) =>
            reason + " (Reported on a pre-session frame this client cannot " +
            "authenticate — treat as a hint, not a fact: an unreachable gateway " +
            "and a spoofed rejection are indistinguishable here.)";

        // Outbound reliability for the two client-emitted handshake steps
        // (HandshakeInit, HandshakeResponse).  Both are raw best-effort
        // datagrams the plain-UDP transport never retransmits, so a single loss
        // otherwise strands the attempt until the connection watchdog expires.
        // The slot parks the exact bytes of the outstanding step and re-emits
        // them on a bounded ladder — kept well inside the gateway's per-envelope
        // replay window — until the next step's reply disarms it.  ReconnectInit
        // is intentionally excluded: its single-use token is consumed on
        // receipt, so a blind re-emission would be rejected; the bounded
        // reconnect loop owns that recovery instead.  Driven from Update on the
        // main thread; disarmed on every session-teardown path via ClearSessionData.
        private readonly HandshakeRetransmit _handshakeRetransmit = new HandshakeRetransmit();

        // Cached in Start so the per-frame retransmit tick allocates no closure.
        // The parked bytes are the slot's own copy; the queue owns whatever it is
        // handed, so each re-emission clones a fresh array to send.
        private Action<byte[]> _handshakeResendCallback;

        // Sticky per-attempt witness: raised the moment the ladder actually
        // re-emits a step, cleared with the rest of the attempt state in
        // ClearSessionData.  From that moment a HandshakeError may be the gateway
        // answering our own duplicate, so the error path stands down for the
        // remainder of the attempt (see OnHandshakeError).  Deliberately NOT
        // slot-scoped: the ambiguity a re-emitted init creates outlives the init
        // slot itself — its duplicate's rejection can arrive after the Challenge
        // has already advanced the ladder to the Response — so the witness
        // persists across steps within one attempt.
        private bool _handshakeReemissionSent;

        private void ResendHandshakePacket(byte[] parked)
        {
            _handshakeReemissionSent = true;
            var copy = new byte[parked.Length];
            Buffer.BlockCopy(parked, 0, copy, 0, parked.Length);
            SendToWire(copy);
        }

        // Park an independent copy of a client-emitted handshake step for
        // re-emission.  The wire packet handed to SendToWire is owned by the send
        // queue, so the slot keeps its own copy and each re-emission clones again
        // — the bytes in flight are never the bytes the slot holds.  The arm and
        // tick clocks are both Time.unscaledTimeAsDouble so the ladder advances at
        // wall-clock even while the game is paused.
        private void ArmHandshakeRetransmit(byte[] wirePacket, string label)
        {
            if (wirePacket == null) return;
            var parked = new byte[wirePacket.Length];
            Buffer.BlockCopy(wirePacket, 0, parked, 0, wirePacket.Length);
            _handshakeRetransmit.Arm(parked, label, Time.unscaledTimeAsDouble);
        }

        // Drive the handshake-step re-emission ladder once per frame.  Gated on an
        // in-flight attempt so a stale armed slot can never resend into an
        // established or torn-down session; ClearSessionData disarms on every
        // teardown path, so this state gate is defence-in-depth over that.
        private void TickHandshakeRetransmit()
        {
            if (!_handshakeRetransmit.IsArmed) return;
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting)
                return;
            _handshakeRetransmit.Tick(Time.unscaledTimeAsDouble, _handshakeResendCallback);
        }

        /// <summary>
        /// Number of inbound packets dropped because the per-second token
        /// bucket was exhausted.  Surfaced for backpressure observability —
        /// any persistent non-zero rate means either a hostile gateway or a
        /// configuration mismatch (legitimate burst above the cap).
        /// </summary>
        public long DroppedInboundFloodPacketCount =>
            _inboundBudget.DroppedFloodPacketCount;

        /// <summary>
        /// Number of Enhanced RPC payloads dropped at enqueue time because
        /// the buffer-replay deferral queue hit one of its three caps
        /// (per-payload size, cumulative bytes, slot count).  Surfaced so an
        /// application can alert on a sustained non-zero rate that would
        /// otherwise only surface as quiet log lines.
        /// </summary>
        public long DroppedRpcReplayBufferCount =>
            _rpcReplayBuffer.DroppedCount;

        // Per-cap rate-limit gates for the warning emitted on each drop.
        // Stopwatch ticks are monotonic so an NTP step cannot freeze or
        // open the gates; ShouldWarn collapses concurrent emitters via CAS
        // so a flood of drops still produces at most one warning per second
        // per cap, keeping the log signal-to-noise ratio bounded.
        private long _lastCleartextSessionAckWarnTicks;
        private long _lastRejectedChallengeWarnTicks;
        private long _lastPinSettingsWarnTicks;
        private long _lastPinRefusalWarnTicks;

        /// <summary>
        /// Its own gate, not shared with the flood warning at Gate 2.
        /// </summary>
        /// <remarks>
        /// The two say different things — "packets are arriving faster than the
        /// bucket admits" and "Challenge verifications specifically are being
        /// rate-limited" — and a project sitting permanently on one would spend
        /// a shared gate every second and hide the other. That is the same rule
        /// `RpcSerializer` states for its three rejection reasons.
        /// </remarks>
        private long _lastChallengeVerifyBudgetWarnTicks;
        private long _lastUnpinnedModeWarnTicks;
        private long _lastHandshakeErrorWarnTicks;
        private long _lastUnsolicitedReplayWarnTicks;
        private long _lastStraySpawnWarnTicks;
        private long _lastStrayDespawnWarnTicks;
        private long _lastRpcDropPayloadWarnTicks;
        private long _lastRpcDropCumulativeWarnTicks;
        private long _lastRpcDropSlotWarnTicks;

        // Returns true when the per-site one-second gate has elapsed and the
        // caller should emit its warning.  The gate lives in WarnGate so the
        // per-tick emitters outside this class hold their diagnostics to the
        // same rate rather than to a second implementation of it.
        private static bool ShouldWarn(ref long lastWarnTicks)
            => RTMPE.Core.WarnGate.ShouldEmit(ref lastWarnTicks);

        // Token-bucket inbound packet admission moved to
        // RTMPE.Core.Protocol.InboundBudget.TryConsume — see that class for
        // the threat-model rationale, capacity choices, and threading contract.

        // RequiresActiveSession was extracted to RTMPE.Core.Protocol.PacketGates —
        // the static decision table is shared with future PacketDispatcher
        // tooling and is unit-testable in isolation. The local thin
        // passthrough below preserves the existing call-site shape.
        private static bool RequiresActiveSession(PacketType type)
            => RTMPE.Core.Protocol.PacketGates.RequiresActiveSession(type);

        // Length-aware overload — `data` may be a pool-rented buffer whose
        // physical .Length exceeds the meaningful packet size.  All header
        // and payload-bound checks therefore use the explicit
        // <paramref name="length"/> parameter, never <c>data.Length</c>.
        private void ProcessPacket(byte[] data, int length)
        {
            // ── Gate 1: Length sanity (cheapest; runs first) ────────────────
            // A null buffer or sub-header length cannot pass any subsequent
            // check — reject before consuming budget so a malformed-packet
            // flood does not also wedge the rate limiter against legitimate
            // traffic. Gate ordering: length before budget before header
            // validation before AEAD — cheapest checks run first.
            if (data == null || length < PacketProtocol.HEADER_SIZE)
            {
                if (ShouldWarn(ref _lastBadHeaderWarnTicks))
                    Debug.LogWarning("[RTMPE] Dropped packet: too short to contain a valid header.");
                return;
            }

            // ── Gate 2: Inbound budget (token bucket) ───────────────────────
            // Bound CPU under hostile flood.  The token bucket runs BEFORE any
            // magic / version / AEAD work so a replay-amplification attack
            // against a valid encrypted packet at line-rate cannot saturate
            // the main thread.  The cap scales with room size (peer-to-peer
            // fan-out grows with member count), so legitimate play stays below
            // it; surfaces drops via <see cref="DroppedInboundFloodPacketCount"/>.
            if (!_inboundBudget.TryConsume())
            {
                _inboundBudget.RecordDrop();
                if (ShouldWarn(ref _lastInboundFloodWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Inbound packet rate exceeded " +
                        $"{_inboundBudget.CurrentRefillPerSec:F0} pps (burst {_inboundBudget.CurrentMaxTokens:F0}); " +
                        "dropping. Sustained drops indicate either a hostile gateway " +
                        "flood or legitimate traffic above the cap — inspect " +
                        "DroppedInboundFloodPacketCount.");
                return;
            }

            // ── Gate 3: Header magic + version + type/flags extraction ─────
            // <see cref="PacketGates.ValidateHeader"/> is pure / side-effect
            // free and shares the same wire-format invariants the gateway
            // enforces, so it is unit-testable in isolation. The length
            // re-check inside it is redundant given Gate 1 but harmless;
            // per-failure-mode warning latches stay at this call site so
            // each failure mode has its own independent rate-limit.
            var hdrResult = PacketGates.ValidateHeader(
                data, length, out var packetType, out var wasEncrypted);
            switch (hdrResult)
            {
                case HeaderValidationResult.BadMagic:
                {
                    if (ShouldWarn(ref _lastBadMagicWarnTicks))
                    {
                        // Re-read on the cold failure path so the log line
                        // includes the observed magic value. Hot path
                        // pays nothing for the diagnostic surface.
                        var observedMagic = (ushort)(
                              data[PacketProtocol.OFFSET_MAGIC]
                            | (data[PacketProtocol.OFFSET_MAGIC + 1] << 8));
                        Debug.LogWarning(
                            $"[RTMPE] Dropped packet: bad magic 0x{observedMagic:X4} " +
                            $"(expected 0x{PacketProtocol.MAGIC:X4}).");
                    }
                    return;
                }

                case HeaderValidationResult.UnsupportedVersion:
                    if (ShouldWarn(ref _lastBadVersionWarnTicks))
                        Debug.LogWarning(
                            $"[RTMPE] Dropped packet: unsupported protocol version " +
                            $"{data[PacketProtocol.OFFSET_VERSION]} (expected {PacketProtocol.VERSION}).");
                    return;

                case HeaderValidationResult.TooShort:
                    // Pre-checked at Gate 1; keep the case here so a future
                    // ValidateHeader extension that adds new TooShort triggers
                    // (e.g. minimum-encrypted-size) is not silently dropped
                    // through the default arm.
                    if (ShouldWarn(ref _lastBadHeaderWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped packet: too short to contain a valid header.");
                    return;

                case HeaderValidationResult.MalformedFlags:
                    // The flags byte carries a bit outside the protocol's
                    // defined set — a corrupt, tampered, or version-skewed
                    // frame.  Shares the malformed-header warning latch.
                    if (ShouldWarn(ref _lastMalformedFlagsWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped packet: header flags byte carries a bit " +
                            "outside the protocol's defined set.");
                    return;

                case HeaderValidationResult.UnknownType:
                    // The type byte does not match any defined PacketType
                    // opcode.  The Rust gateway only ever emits bytes from its
                    // explicit allow-list (`PacketType::try_from`), so an
                    // off-list byte is either a tampered frame or a
                    // version-skewed gateway running a future opcode this
                    // build does not implement.  Drops the frame and shares
                    // the malformed-header warning latch so a flood cannot
                    // saturate the log pipeline.
                    if (ShouldWarn(ref _lastUnknownTypeWarnTicks))
                        Debug.LogWarning(
                            $"[RTMPE] Dropped packet: unknown packet type 0x" +
                            $"{data[PacketProtocol.OFFSET_TYPE]:X2}.");
                    return;

                case HeaderValidationResult.Ok:
                    break;
            }

            // If FLAG_ENCRYPTED is set the gateway has wrapped the payload in a
            // ChaCha20-Poly1305 AEAD envelope.  Decrypt before dispatching so every
            // handler always receives plaintext — handlers are unaware of encryption.
            //
            // Pre-handshake packets (Challenge, HandshakeAck) arrive plaintext by
            // protocol — the SDK has no key material yet.  Post-handshake packets
            // carry session-bound semantics and must be AEAD-protected; the
            // `requiresEnc` gate below enforces that.  SessionAck is the one
            // exception: it is the bootstrap envelope, and whether it is sealed
            // is a negotiated property of the handshake
            // (CapabilityFlags.EncryptedSessionAck) rather than a mandate of the
            // static gate.
            // Note: wasEncrypted + packetType were captured by ValidateHeader
            // above; both are read from the plaintext header bytes which the
            // decrypt path preserves verbatim, so subsequent state-machine
            // logic can rely on them across the decrypt boundary.
            int packetLength = length;

            // SessionAck bootstrap-encryption path.  When the handshake
            // negotiated CapabilityFlags.EncryptedSessionAck the gateway seals
            // the SessionAck payload under a one-time AEAD key derived from the
            // ECDH shared secret (HKDF info suffix \x03), with a fixed all-zero
            // 12-byte nonce and AAD = [0x08, FLAG_ENCRYPTED], and stamps
            // FLAG_ENCRYPTED on the header.  The decrypt decision is taken
            // purely from that wire bit: the SDK always advertises the
            // capability, so a sealed SessionAck means the gateway honoured it.
            // The regular session-key decrypt path cannot open this packet —
            // SessionAck is the bootstrap that delivers crypto_id, which the
            // session-AEAD nonce depends on — so it is routed through a
            // dedicated decrypt before the generic AEAD branch runs.
            //
            // `wasEncrypted` is wire truth (the FLAG_ENCRYPTED bit) and is
            // never mutated locally; `alreadyDecrypted` tracks whether the
            // SessionAck-specific path already produced plaintext so the
            // generic AEAD branch is skipped.
            bool alreadyDecrypted = false;
            if (wasEncrypted && packetType == PacketType.SessionAck)
            {
                data = DecryptSessionAckPacket(data, length);
                if (data == null)
                {
                    if (ShouldWarn(ref _lastAeadFailWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped SessionAck: bootstrap AEAD authentication " +
                            "failed — the ECDH-derived bootstrap key did not open the " +
                            "sealed envelope.");
                    return;
                }
                packetLength = data.Length;
                alreadyDecrypted = true;
            }
            if (wasEncrypted && !alreadyDecrypted)
            {
                // DecryptInboundPacket returns an exact-sized plaintext byte[]
                // (header + decrypted payload), severing the dependency on
                // the rented buffer's physical length.  Downstream handlers
                // can therefore continue to inspect data.Length safely.
                data = DecryptInboundPacket(data, length);
                if (data == null)
                {
                    // DecryptInboundPacket surfaces the precise drop reason at its
                    // own throttled site (Poly1305 tag mismatch, replay / out-of-
                    // window, or a missing replay window); a generic line here would
                    // double-log and mislabel a benign duplicate as an auth failure.
                    return;
                }
                packetLength = data.Length;
            }
            else if (PacketGates.RequiresExactFrameCopy(alreadyDecrypted, data.Length, length))
            {
                // As-received cleartext path: the frame still sits in an
                // oversized ArrayPool rental, so handlers that read data.Length
                // need an exact-sized copy cut to the on-wire length.  A
                // bootstrap-encrypted SessionAck has instead already been
                // replaced above with an exact-sized plaintext buffer whose
                // length is the decrypted size — strictly smaller than the
                // on-wire length once the AEAD tag is stripped — and
                // RequiresExactFrameCopy excludes that case so this copy never
                // reads past the shorter decrypted buffer.  The branch is cold:
                // only handshake bootstrap and a few protocol packets travel
                // unencrypted, so the per-packet allocation is amortised far
                // below the encrypted hot path's zero-copy benefit.
                var exact = new byte[length];
                Buffer.BlockCopy(data, 0, exact, 0, length);
                data = exact;
                packetLength = length;
            }

            // packetType was captured by ValidateHeader above and is read
            // from the plaintext header byte at OFFSET_TYPE — both decrypt
            // paths above preserve that byte verbatim, so re-reading after
            // the decrypt boundary would be redundant.

            // SessionAck is excluded from RequiresEncryption — it is the
            // bootstrap envelope, delivered before the session-AEAD state the
            // static gate protects exists.  Its own downgrade enforcement
            // lives in OnSessionAck: the pre-parse refusal keyed on the LOCAL
            // capability advertisement (PacketGates.IsSessionAckDowngrade)
            // rejects any cleartext envelope this client did not agree to,
            // and a post-parse check on the gateway's echo backs it up.
            bool requiresEnc = RequiresEncryption(packetType);
            if (!wasEncrypted && requiresEnc)
            {
                if (ShouldWarn(ref _lastMissingEncryptionWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] Dropped packet: {packetType} arrived without " +
                        "FLAG_ENCRYPTED. This packet type carries session-bound " +
                        "state and must be AEAD-protected once the session is " +
                        "established — accepting it would let an off-path " +
                        "attacker race a forged frame against the gateway's reply.");
                return;
            }

            // Centralised pre-dispatch session gate.  Game-data packet
            // handlers each carry their own InRoom check (defence-in-depth),
            // but routing the packet THROUGH the dispatcher first costs CPU
            // and surface area; rejecting at the gate is cheaper and
            // eliminates the implicit-state assumption that "InRoom implies
            // SessionEstablished".  A future state-machine refactor that
            // decouples the two cannot leak game-data dispatch through this
            // path.
            if (RequiresActiveSession(packetType) && !_sessionEstablished)
            {
                if (ShouldWarn(ref _lastPreSessionGameDataWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] Dropped packet: {packetType} arrived before " +
                        "SessionAck completed. Game-data packets are only valid " +
                        "after the session is established; pre-session traffic " +
                        "is rejected as a state-machine integrity check.");
                return;
            }

            if (IsDebugLogEnabled)
                LogDebug($"Received {packetType} ({packetLength} B).");

            switch (packetType)
            {
                // ── ECDH 4-step handshake ────────────────────────────────
                case PacketType.Challenge:    OnChallenge(data);    break;
                case PacketType.SessionAck:   OnSessionAck(data, wasEncrypted); break;
                case PacketType.HandshakeError: OnHandshakeError(data); break;

                // ── Legacy handshake (backward compatibility) ────────────────
                case PacketType.HandshakeAck: OnHandshakeAck(data); break;

                // ── Keep-alive ───────────────────────────────────────────────
                case PacketType.HeartbeatAck: OnHeartbeatAck(data); break;

                // ── Room lifecycle (0x20–0x23) ─────────────────────
                case PacketType.RoomCreate:
                case PacketType.RoomJoin:
                case PacketType.RoomLeave:
                case PacketType.RoomList:
                    OnRoomPacket(packetType, data);
                    break;

                // ── Custom property broadcasts (0x24–0x25) ─────────
                case PacketType.RoomPropertyUpdate:
                    OnRoomPropertyUpdateBroadcast(data);
                    break;
                case PacketType.PlayerPropertyUpdate:
                    OnPlayerPropertyUpdateBroadcast(data);
                    break;

                // ── Matchmaking (0x26, 0x2B) ──────────────────────
                case PacketType.MatchmakingResponse:
                    _matchmakingManager?.HandleMatchmakingResponse(
                        PacketParser.ExtractPayload(data));
                    break;

                // ── Lobby system (0x27–0x2A) ───────────────────────
                case PacketType.LobbyJoin:
                case PacketType.LobbyList:
                    OnLobbyPacket(packetType, data);
                    break;
                case PacketType.LobbyLeave:
                    // Fire-and-forget — no reply; notify listeners.
                    OnLobbyPacket(packetType, data);
                    break;
                case PacketType.LobbyRoomListUpdate:
                    OnLobbyRoomListUpdate(data);
                    break;

                // ── Room management broadcasts (0x2C, 0x2E, 0x2F) ──
                case PacketType.MasterClientChanged:
                case PacketType.KickPlayer:
                case PacketType.SceneLoaded:
                    OnRoomPacket(packetType, data);
                    break;

                // MasterClientTransfer (0x2D) is a client→server request packet
                // (see MasterClientPacketBuilder).  The gateway communicates a
                // successful transfer to all peers via MasterClientChanged
                // (0x2C); a server-broadcast 0x2D would either be a relay echo
                // or a forged packet from an off-path attacker.  Drop it
                // explicitly with a rate-limited warning so a future protocol
                // change that legitimately delivers 0x2D server→client surfaces
                // here rather than silently falling through to the default arm.
                case PacketType.MasterClientTransfer:
                    if (ShouldWarn(ref _lastMasterClientTransferWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Ignoring server-broadcast MasterClientTransfer " +
                            "(0x2D); this packet is client→server only. The authoritative " +
                            "master-client change is delivered via MasterClientChanged (0x2C).");
                    break;

                case PacketType.Disconnect:      OnServerDisconnect(data); break;
                case PacketType.Data:
                case PacketType.StateSync:        SafeRaise(OnDataReceived, data, nameof(OnDataReceived)); break;

                // DataAck carries the gateway-acknowledged ARQ sequence as a
                // 4-byte little-endian payload.  Drain the matching entry
                // from the outbound retransmit table BEFORE firing the
                // public event so subscribers observe the post-ack state.
                // A truncated payload (legacy gateway that pre-dates the
                // arq_seq wire extension) skips the ledger update but still
                // raises the event, preserving back-compat semantics.
                case PacketType.DataAck:
                    {
                        var ackPayload = PacketParser.ExtractPayload(data);
                        if (ackPayload != null && ackPayload.Length >= 4)
                        {
                            uint ackedSeq = (uint)(
                                  ackPayload[0]
                                | (ackPayload[1] << 8)
                                | (ackPayload[2] << 16)
                                | (ackPayload[3] << 24));
                            int cleared = _outboundReliableChannel.Acknowledge(ackedSeq);
                            if (cleared > 0 && IsDebugLogEnabled)
                                LogDebug($"DataAck arq_seq={ackedSeq} cleared the matching retransmit entry.");
                        }
                        SafeRaise(OnDataAcknowledged, nameof(OnDataAcknowledged));
                        LogDebug("DataAck received.");
                    }
                    break;

                // ── Networked object lifecycle ─────────────────────
                case PacketType.Spawn:            OnSpawnPacket(data);   break;
                case PacketType.Despawn:          OnDespawnPacket(data); break;
                case PacketType.SpawnRejected:    OnSpawnRejectedPacket(data); break;

                // ── RPC system ─────────────────────────────────────
                case PacketType.Rpc:              OnRpcRequest(data);    break;
                case PacketType.RpcResponse:      OnRpcResponse(data);   break;
                case PacketType.RpcBufferReplay:  HandleRpcBufferReplay(PacketParser.ExtractPayload(data)); break;

                // Receive inbound variable update packets.
                case PacketType.VariableUpdate:   HandleVariableUpdatePacket(data); break;

                default:
                    if (IsDebugLogEnabled)
                        LogDebug($"No handler for packet type 0x{(byte)packetType:X2}.");
                    break;
            }
        }

        // ── Handshake packet handlers ───────────────────────────────────────

        /// <summary>
        /// Handle an incoming <c>Challenge</c> (0x06) from the server.
        ///
       /// 1. Parse 128-byte payload: [ephemeral:32][static:32][sig:64].
        /// 2. Verify Ed25519 signature — reject on failure.
        /// 3. Derive session keys via X25519 ECDH + HKDF-SHA256.
        /// 4. Send <c>HandshakeResponse</c> containing the client public key.
        /// </summary>
        // Abort an in-flight handshake synchronously with a specific failure
        // reason — used when the attempt can never succeed (e.g. a Strict-pin
        // refusal) so the cause surfaces immediately rather than after the
        // connection-timeout window.  Mirrors the teardown
        // ConnectionTimeoutRoutine performs so the next retry starts from an
        // identical clean baseline (terminated thread, closed socket, stopped
        // coroutines).  Stopping the timeout coroutine is the load-bearing
        // step: it prevents a second OnConnectionFailed (with the generic
        // "Connection timeout." reason) from firing later, and the Disconnected
        // transition makes any other failure site a no-op via TransitionTo's
        // prev==next guard.
        // `preserveReconnectToken` defaults to false because almost every caller
        // is a verdict about THIS session that a resumption could not repair.
        // The exception is a refusal whose own cause is transient — an
        // unreadable pin store — where destroying a token the gateway has not
        // spent turns a momentary condition into a permanent one.
        private void FailHandshake(
            string failureReason,
            DisconnectReason reason,
            bool preserveReconnectToken = false)
        {
            int epoch = System.Threading.Volatile.Read(ref _connectionAttemptEpoch);
            SafeRaise(OnConnectionFailed, failureReason, nameof(OnConnectionFailed));

            // A handler that disconnects and reconnects — both admitted here,
            // the first because the state is still Connecting and the second
            // because the first reached Disconnected — has replaced everything
            // below with a fresh attempt's copy.  Carrying on would stop that
            // attempt's watchdog and handshake coroutine, close its socket and
            // wipe its session, and report it failed for a refusal that happened
            // before it existed.
            if (!AttemptIsStillLive(epoch)) return;

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }
            if (_connectCoroutine != null)
            {
                StopCoroutine(_connectCoroutine);
                _connectCoroutine = null;
            }

            RetireNetworkThread();
            try { _transport?.Disconnect(); }
            catch (Exception ex)
            {
                RtmpeLog.Warning($"[NM] Transport disconnect on handshake-failure teardown threw: {ex.Message}");
            }
            ClearSessionData(preserveReconnectToken);
            TransitionTo(NetworkState.Disconnected, reason);
        }

        // A server that declines the handshake (0x0B) lets the client distinguish
        // a deliberate refusal from an unreachable network, so the connect attempt
        // ends with an actionable reason instead of waiting out the timeout.  The
        // surfaced text is drawn from the local trusted catalogue keyed on the
        // category byte — the server's own reason string is unauthenticated on
        // this pre-session frame, so it is only scrubbed and logged for context,
        // never displayed as the failure.
        private void OnHandshakeError(byte[] data)
        {
            // Relevant only while a handshake is in flight; a stray rejection at
            // any other time is ignored rather than tearing down a live session.
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting)
                return;

            var payload = PacketParser.ExtractPayload(data);
            // A malformed payload leaves `code` at the generic category, so the
            // description resolved below is still well-formed and non-empty.
            RTMPE.Core.Protocol.PacketGates.TryParseHandshakeError(
                payload, out byte code, out string serverNote);
            string reason = RTMPE.Core.Protocol.PacketGates.DescribeHandshakeError(code);

            // Once a handshake step has actually been re-emitted this attempt, any
            // HandshakeError is ambiguous: the gateway answers a duplicate of an
            // already-accepted step with an error (a nonce-replay rejection for a
            // re-sent init, a no-pending-slot rejection for a re-sent Response),
            // and a production gateway collapses every error category onto the
            // generic code — so no code inspection can tell that self-induced
            // rejection apart from a real one.  Failing here on such an error
            // would abort an attempt whose genuine reply is merely still in
            // flight.  From the first re-emission onward the attempt is therefore
            // resolved only by cryptographic truth — a Challenge or SessionAck —
            // or by the connection watchdog; error frames are stood down.  Before
            // any re-emission the ambiguity is narrower — nothing was duplicated,
            // so the rejection is not the gateway answering our own copy — but it
            // is not absent: the frame is still unauthenticated, and a rejection
            // of the original send (wrong API key, malformed envelope, spent
            // reconnect token) is therefore recorded below rather than acted on.
            // What the two cases differ in is the account the watchdog finally
            // gives, not whether the attempt survives the frame.
            if (_handshakeReemissionSent)
            {
                LogDebug("Ignoring a handshake error received after a handshake step " +
                         "was re-emitted — the rejection may be the gateway answering " +
                         "our own duplicate; awaiting the genuine reply or the watchdog.");
                return;
            }

            // Record the diagnosis; do not end the attempt on it.  This frame
            // carries no signature, no AEAD tag and no session — a spoofed one
            // is indistinguishable from the gateway's, so acting on it hands any
            // party that can reach this socket a one-datagram kill switch on
            // every connect.  The genuine reply may still be in flight and the
            // ladder is still armed for it; the connection watchdog ends the
            // attempt and surfaces what was recorded here, so a real rejection
            // still reads "wrong API key" rather than a bare timeout.  Same
            // reasoning, same shape, as the cleartext-SessionAck drop.
            //
            // `reason` is this client's own account of the category byte, never
            // the server's text.  The note is scrubbed and logged for context
            // only, and its emitter is gated because an attacker chooses how
            // many of these arrive.
            if (ShouldWarn(ref _lastHandshakeErrorWarnTicks))
            {
                // ⛔ Written without `string.IsNullOrEmpty` on purpose: a gate's
                // body may call nothing but a console write, so that a closed
                // gate is provably skipping only a line.  A rule that admitted
                // a call in this condition would admit a bool-returning mutator
                // in it.
                if (serverNote != null && serverNote.Length > 0)
                    Debug.Log("[RTMPE] Gateway handshake-rejection note: "
                              + Diagnostics.UntrustedLogText.Sanitise(serverNote));

                Debug.LogWarning(
                    "[RTMPE] A handshake rejection arrived on an unauthenticated frame — " +
                    $"{reason}. Recorded, not acted on: nothing proves the gateway sent it. " +
                    "The attempt continues until the connection watchdog expires, which will " +
                    "report this reason.");
            }

            _pendingHandshakeRejection ??= UnverifiedRejection(
                "Handshake rejected by the server — " + reason);
        }

        private void OnChallenge(byte[] data)
        {
            // Guard: only process Challenge while we are actively connecting
            // or reconnecting.  N-1 adds the Reconnecting state — the server
            // replies to ReconnectInit with the same Challenge packet format,
            // so the same handler runs for both flows.
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting) return;
            if (_handshakeHandler == null)         return;

            // One Challenge per attempt.  The gateway consumes its pending-auth
            // entry when it accepts our HandshakeResponse, so a second Response
            // — which is what a second pass through this handler would emit —
            // is answered with "no pending handshake for address" and can end
            // an attempt the first Response had already carried to SessionAck.
            // A repeat is UDP duplication, an on-path replay, or the gateway
            // answering one of our own retransmitted inits from the Round 1 it
            // still has open — in every case the same bytes, carrying nothing
            // the first pass did not already act on.
            //
            // The latch is set only once a Challenge has validated, so an
            // injected malformed frame cannot spend the attempt's single slot
            // and lock out the real one.
            if (_challengeAccepted)
            {
                LogDebug("Challenge already accepted for this attempt — dropping duplicate.");
                return;
            }

            // Witness for the timeout diagnostic: a Challenge reached the client,
            // so the gateway both received our init and answered it. Recorded
            // before validation so a subsequent pin/transcript rejection is
            // attributed to "reply not finalised" rather than "no server reply".
            _diagChallengeReceived = true;

            var payload = PacketParser.ExtractPayload(data);

            // ⛔ Framing first, and BEFORE the verification charge below.
            //
            // That charge is deliberately blind to what the payload SAYS — that
            // is what keeps it a rate limit rather than a lockout. Blindness to
            // how LONG it is, is not the same property and does not buy it:
            // ValidateChallenge refuses any other length in nanoseconds, so a
            // frame of the wrong size costs nothing to reject and was being
            // charged the full price of an Ed25519 verification. A bare 13-byte
            // header — 41 bytes on the wire with UDP and IP — drained the whole
            // verification budget at roughly three tokens per byte, and an
            // attempt draws at most one genuine Challenge PER TRANSMISSION of its
            // init — the gateway answers a retransmission from the Round 1 it
            // still has open — so starving the frames the ladder draws ends the
            // attempt.  The ladder makes that harder, not easier: the budget must
            // now outlast five chances rather than one.
            //
            // Framing is settled by this length alone, it is pre-AEAD, and it
            // reveals nothing an observer does not already have.
            if (payload.Length != HandshakeHandler.ChallengePayloadBytes)
            {
                LogDebug($"Challenge payload is {payload.Length} bytes, not " +
                         $"{HandshakeHandler.ChallengePayloadBytes} — dropping.");
                _pendingHandshakeRejection ??= UnverifiedRejection(
                    "A Challenge arrived with the wrong payload length and was " +
                    "dropped without verifying it.");
                return;
            }

            // The verdict was taken by this attempt's Round-1 sender, before
            // anything reached the wire.  Nothing here re-reads the pin store,
            // re-parses the configured pin, or can throw on either.
            if (!_attemptPinResolved)
            {
                LogDebug("Challenge arrived before this attempt sent Round 1 — dropping.");
                return;
            }
            var resolution = _attemptPinResolution;

            switch (resolution.Decision)
            {
                case PinDecision.RefuseStrictNoPin:
                    // ⛔ Unreachable in practice and kept anyway: the Round-1
                    // sender refuses this verdict before it sends, so an attempt
                    // that got as far as receiving a Challenge did not carry it.
                    // Left as a defence in depth, and it must not end the
                    // attempt — the trigger is a frame nothing authenticated.
                    // Gated with the rest of the pre-validation region.
                    if (ShouldWarn(ref _lastPinRefusalWarnTicks))
                    {
                        Debug.LogError(
                            "[RTMPE] Server not pinned — refusing handshake.  Either set " +
                            "NetworkSettings.pinnedServerPublicKeyHex (Strict), pre-provision a " +
                            "pin via IServerKeyPinStore (TrustOnFirstUse + " +
                            "requireFirstUseProvisioned), or relax requireFirstUseProvisioned " +
                            "if first-flight TOFU capture is acceptable for this deployment.  " +
                            "A pin store that could not read its own state refuses here too, " +
                            "rather than capturing over the pins it holds; it logs the path it " +
                            "could not read, and that connect succeeds once the store is " +
                            "reachable again.");
                    }
                    // The VERDICT here is deterministic and configuration-derived
                    // — but the TRIGGER is a frame nothing authenticated, and this
                    // used to tear the attempt down and, through ClearSessionData,
                    // destroy the reconnect token with it.  A bare 13-byte 0x06 was
                    // enough.  So it records and drops like every other arm, and
                    // the fail-fast a developer actually needs was moved to where
                    // the verdict is decidable WITHOUT a frame: the pre-flight
                    // both Round-1 senders run before anything reaches the wire.
                    // Reaching this line now
                    // means the resolution changed between the two calls — a pin
                    // store that read once and then would not — which is a real
                    // case and is reported by the watchdog.
                    _pendingHandshakeRejection ??= UnverifiedRejection(
                        "Server not pinned — refusing handshake. No pin was configured, " +
                        "none was pre-provisioned, or the pin store could not be read.");
                    return;

                case PinDecision.ProceedUnpinned:
                    if (ShouldWarn(ref _lastUnpinnedModeWarnTicks))
                    {
                        Debug.LogWarning(
                            "[RTMPE] ServerPinningMode.InsecureNoPinning is active — the SDK will " +
                            "accept any valid Ed25519 signature. This is unsafe for production: a " +
                            "rogue gateway with its own keypair will complete the handshake. " +
                            "Configure pinnedServerPublicKeyHex (Strict) or TrustOnFirstUse instead.");
                    }
                    break;
            }

            // Pass the Round-1 payload this attempt emitted so ValidateChallenge
            // reconstructs the same transcript the gateway signed.  Both flows
            // supply one — the sealed envelope on a fresh handshake, the
            // ReconnectInit payload on a reconnect — so the transcript is unique
            // to the attempt in either case.
            //
            // Classify the in-flight handshake from the state machine rather
            // than from ciphertext-presence.  Anchoring on
            // NetworkState.Reconnecting (set only by StartReconnectAttempt)
            // and additionally requiring a non-empty reconnect token means a
            // fresh Connect() that races with a stale _reconnectToken left
            // over from a prior session cannot silently mis-engage the
            // reconnect transcript path during the brief window where the
            // ciphertext is also null.  Any future disagreement between
            // ciphertext-presence and the state-derived flow is caught by
            // ValidateChallenge's own defence-in-depth checks.
            HandshakeFlow handshakeFlow;
            if (_state == NetworkState.Reconnecting && !string.IsNullOrEmpty(_reconnectToken))
            {
                handshakeFlow = HandshakeFlow.Reconnect;
            }
            else
            {
                handshakeFlow = HandshakeFlow.Init;
            }

            // ── The cost this frame is about to impose, charged before it does ──
            //
            // `ValidateChallenge` runs an Ed25519 verify unconditionally — even
            // when the pin has already mismatched — and that is deliberate:
            // without it, an attacker who pins their own key could probe the
            // legitimate server's static key by measuring how quickly the client
            // bails out. The verify is not the defect. Its COST is: measured at
            // 3.93 ms on the main thread, against a Gate 2 token bucket that
            // charges every packet one token because it was sized from an
            // ordinary packet's ~1 µs. One token bought four thousand times the
            // work it was priced for, and the frame that buys it needs no
            // credential — 0x06 is exempt from the session gate, correctly,
            // because no key exists this early.
            //
            // 🔑 Charged on EVERY Challenge that reaches this point, genuine or
            // forged, and decided before the payload is looked at. That is what
            // keeps the bucket a rate limit rather than a lockout: it does not
            // discriminate by outcome, so an attacker's frames cannot spend a
            // budget that then refuses the real one — the §81 defect, which a
            // per-attempt cap on REJECTED challenges reintroduced and this must
            // not.
            //
            // ⛔ The one token Gate 2 already took is the packet's admission;
            // this is the remainder of what it will spend.
            if (!_inboundBudget.TryConsume(_inboundBudget.ChallengeVerifyCost - 1f))
            {
                _inboundBudget.RecordDrop();
                if (ShouldWarn(ref _lastChallengeVerifyBudgetWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Challenge signature verification is rate-limited and this " +
                        "frame was dropped without verifying. A handshake needs one " +
                        "verification and the burst carries several, so sustained drops here " +
                        "mean forged Challenge frames are arriving faster than the budget " +
                        "admits them — the attempt's own watchdog ends it rather than this.");
                return;
            }

            if (!_handshakeHandler.ValidateChallenge(
                    payload,
                    _lastRoundOnePayload,
                    handshakeFlow,
                    out _,                                    // serverEphemeralPub (stored inside handler)
                    out var verifiedServerStaticPub,          // captured for TOFU persistence
                    resolution.PinToEnforce))
            {
                // Record the diagnosis; do not end the attempt on it.  A
                // Challenge that fails to validate is, by definition, one this
                // client could not authenticate — so it is exactly as likely to
                // be an injected frame as the gateway's, and ending the attempt
                // here would let one spoofed datagram deny every connect.  The
                // genuine Challenge may still be in flight, the init ladder is
                // still armed to draw it, and the connection watchdog surfaces
                // this reason if none ever arrives.  Same shape as the
                // cleartext-SessionAck drop below.
                //
                // Gated, because an attacker chooses how many of these arrive
                // and this is a LogError with a stack trace on the main thread.
                if (ShouldWarn(ref _lastRejectedChallengeWarnTicks))
                    Debug.LogError(
                        "[RTMPE] Challenge validation failed — Ed25519 signature invalid, " +
                        "pin mismatch, or Challenge payload malformed. Dropped rather than " +
                        "acted on: nothing proves the gateway sent it, and the genuine " +
                        "Challenge may still be in flight. If none arrives, the connection " +
                        "watchdog will report this as the failure reason.");

                _pendingHandshakeRejection ??= UnverifiedRejection(
                    "Server identity verification failed — Ed25519 signature, " +
                    "server-key pin, or challenge format rejected.");
                return;
            }

            // Retain the verified server identity key: the Challenge Ed25519
            // signature has now been checked against it, so it is a trusted
            // anchor.  If the gateway advertises CapabilityFlags.IdentitySignedJwt
            // in the upcoming SessionAck, OnSessionAck verifies the JWT
            // signature against this same key.
            _serverIdentityPublicKey = verifiedServerStaticPub;

            // Close the attempt to further Challenges.  Set here rather than at
            // entry so a rejected frame leaves the slot open for the genuine
            // one.
            _challengeAccepted = true;

            // Only NOW is the init re-emission ladder retired.  A Challenge is
            // proof the gateway received our init and answered it — but only one
            // that validated is proof of anything at all, and disarming on an
            // unproven frame let a single forged datagram stop the ladder that
            // recovers a lost init.
            _handshakeRetransmit.Disarm();

            // The witness the reconnect-token retention decision reads.  The
            // gateway spends the single-use token when it accepts a
            // ReconnectInit, before it answers — so a VALIDATED Challenge is
            // what proves the token is gone.  See the field's declaration for
            // why the arrival witness above cannot serve here.
            _diagChallengeValidated = true;

            // A reconnect re-runs this handler against a fresh handshake handler
            // while the previous attempt's derived buffers may still be
            // referenced. Scrub them before overwriting so they are never
            // abandoned to GC un-zeroed.
            if (_sessionAckKey != null)
            {
                Array.Clear(_sessionAckKey, 0, _sessionAckKey.Length);
                _sessionAckKey = null;
            }
            if (_ipMigrationKey != null)
            {
                Array.Clear(_ipMigrationKey, 0, _ipMigrationKey.Length);
                _ipMigrationKey = null;
            }

            // Derive directional session keys (AEAD) + N-8 IP migration key via HKDF-SHA256.
            // Three independent expansions from a single PRK — info suffixes \x00, \x01, \x02.
            var derivedKeys = _handshakeHandler.DeriveSessionKeys(out _ipMigrationKey, out _sessionAckKey);
            if (derivedKeys == null)
            {
                Debug.LogError("[RTMPE] ECDH key derivation failed (degenerate shared secret). Disconnecting.");
                // Deterministic dead-end — the peer's ephemeral share collapsed
                // the shared secret — so surface a specific reason now instead
                // of waiting out the connection timeout.
                FailHandshake(
                    "Secure session key derivation failed (degenerate ECDH shared secret).",
                    DisconnectReason.ProtocolError);
                return;
            }
            _sessionKeyStore.InstallSessionKeys(derivedKeys);

            // Fresh session keys imply a fresh inbound nonce stream — reuse
            // of an old window would falsely reject the first packet of the
            // new session because its counter starts back at zero.
            //
            // The window MUST be live before the first AEAD-decrypted packet
            // is dispatched, so it is initialised here — immediately after
            // key derivation, before HandshakeResponse is sent and therefore
            // before any inbound frame can be sealed under the new keys.  The
            // receive path treats a null window as a hard reject for any
            // AEAD-bearing frame, so a missed initialisation cannot silently
            // degrade into a no-op replay check.
            if (_sessionKeyStore.ReplayWindow == null)
                _sessionKeyStore.EnsureReplayWindow();
            else
                _sessionKeyStore.ReplayWindow.Reset();

            // Persist the captured key only AFTER both transcript verification
            // and ECDH succeed.  Writing earlier would let a malformed
            // Challenge — one whose signature passes parsing but whose ECDH
            // produces a degenerate secret — poison the pin store.
            if (ServerKeyPinning.PersistFirstUse(
                    resolution, PinStore, verifiedServerStaticPub, out var pinPersistFailure))
            {
                LogDebug(
                    $"Captured server static key on first connect to " +
                    $"{resolution.Endpoint} (TrustOnFirstUse).");
            }
            else if (pinPersistFailure != null)
            {
                // The session proceeds on a key it has already verified; what
                // is lost is the record of it, so the next connect to this
                // endpoint captures on first use again and a key change in
                // between goes undetected.  Loud, because that is a silent
                // reduction in what pinning is protecting against.
                Debug.LogWarning(
                    $"[RTMPE] Could not persist the first-use pin for {resolution.Endpoint}: " +
                    $"{pinPersistFailure.GetType().Name}: {pinPersistFailure.Message}. The " +
                    "connection continues; pinning for this endpoint reverts to " +
                    "capture-on-first-use until a write succeeds.");
            }

            // Send the client's X25519 ephemeral public key to the server.
            // Use SendOwned — response is a freshly allocated array that
            // will not be reused, so the extra copy inside Send() is unnecessary.
            //
            // The cap advertisement carries every optional feature the SDK
            // is willing to honour for this session — see ComputeLocalCaps.
            // A gateway that does not understand the cap field tolerates the
            // trailing bytes (`payload[..32]` semantics are unchanged), so
            // emitting the advertisement is safe against legacy gateways.
            //
            // When ComputeLocalCaps signals InitHashEcho support — true on
            // every Init-flow handshake where the Round-1 ciphertext is in
            // scope — the SDK pairs the cap bit with the SHA-256 echo at
            // payload[37..69].  The gateway compares it against the same
            // hash it computed at Round-1, binding the ECDH exchange to the
            // exact session that authenticated the API key.  Reconnect
            // flows leave _lastHandshakeInitCiphertext null and therefore
            // do not advertise the bit: the single-use reconnect token
            // already provides the same binding without an echo.
            RTMPE.Core.Protocol.CapabilityFlags clientCaps = ComputeLocalCaps();
            byte[] initHashEcho = null;
            if ((clientCaps & RTMPE.Core.Protocol.CapabilityFlags.InitHashEcho) != 0
                && _lastHandshakeInitCiphertext != null)
            {
                initHashEcho = RTMPE.Protocol.PacketBuilder.ComputeInitHashEcho(
                    _lastHandshakeInitCiphertext);
            }
            byte[] response = initHashEcho != null
                ? _packetBuilder.BuildHandshakeResponse(
                    _handshakeHandler.ClientPublicKey,
                    RTMPE.Core.WireFormat.Default,
                    clientCaps,
                    initHashEcho)
                : _packetBuilder.BuildHandshakeResponse(
                    _handshakeHandler.ClientPublicKey,
                    RTMPE.Core.WireFormat.Default,
                    clientCaps);
            SendToWire(response);
            // Arm the re-emission ladder for the Response, the second and last
            // client-emitted handshake step.  The gateway consumes its pending
            // slot on the first Response, so a duplicate is dropped without
            // touching the established session, while a lost Response is completed
            // by the re-emission — verbatim resend is safe either way.
            ArmHandshakeRetransmit(response, "HandshakeResponse");
            LogDebug("Sent HandshakeResponse — awaiting SessionAck.");
        }

        /// <summary>
        /// The capability bitmask the SDK advertises for this session.
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck"/>
        /// is always set — the SDK can always decrypt the bootstrap envelope —
        /// and <see cref="RTMPE.Core.Protocol.CapabilityFlags.ArqAck"/> is
        /// added only when the local <c>EmitArqSequence</c> opt-in is active,
        /// keeping the on-wire promise honest (the SDK cannot consume the
        /// gateway's DataAck without the local opt-in).
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.InitHashEcho"/> is
        /// added on every Init-flow handshake where the Round-1 ciphertext is
        /// still cached: only then can the SDK compute the matching echo at
        /// <c>payload[37..69]</c>.  Reconnect flows clear the cache before
        /// reaching this point and therefore advertise the cap as absent —
        /// the gateway's reconnect short-circuit handles the binding
        /// independently of the echo.
        ///
        /// The same value is sent in HandshakeResponse and re-derived in
        /// OnSessionAck for the negotiation intersection, so both call sites
        /// route through here.
        /// </summary>
        private RTMPE.Core.Protocol.CapabilityFlags ComputeLocalCaps()
        {
            var caps = RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck;
            if (_settings != null && _settings.EmitArqSequence)
                caps |= RTMPE.Core.Protocol.CapabilityFlags.ArqAck;
            if (_lastHandshakeInitCiphertext != null)
                caps |= RTMPE.Core.Protocol.CapabilityFlags.InitHashEcho;
            return caps;
        }

        /// <summary>
        /// Handle <c>SessionAck</c> (0x08): parse crypto_id, JWT, and reconnect token,
        /// then transition to <see cref="NetworkState.Connected"/> and start heartbeat.
        /// </summary>
        /// <param name="data">The SessionAck packet bytes (already decrypted
        /// when the bootstrap envelope was sealed).</param>
        /// <param name="wasEncrypted">Wire truth: whether the SessionAck
        /// arrived carrying <see cref="PacketFlags.Encrypted"/>.  Used for the
        /// post-parse downgrade check against the gateway's advertised caps.</param>
        private void OnSessionAck(byte[] data, bool wasEncrypted)
        {
            // Guard: ignore stale ACKs that arrive after a timeout.
            // N-1: reconnect flow also terminates with SessionAck, so accept
            // both Connecting and Reconnecting states.
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting) return;

            // Refuse a cleartext bootstrap envelope BEFORE parsing anything
            // from it, and before any state this attempt depends on is
            // retired.  This client always advertises EncryptedSessionAck
            // (ComputeLocalCaps), and the local advertisement is the one
            // input an on-path attacker cannot rewrite — capability bytes
            // cross the wire outside the signed transcript and outside any
            // AEAD, so every field inside a cleartext SessionAck (including
            // the gateway_caps echo) is attacker-writable.  The JWT and
            // reconnect token this envelope carries are the session's bearer
            // credentials; accepting them in the clear hands them to any
            // passive observer the moment an active peer forces the
            // downgrade.  Reconnect flows terminate through the same Round-2
            // path and are sealed under the same negotiation, so no
            // legitimate flow reaches this branch.
            //
            // The frame is dropped rather than answered with a disconnect: it
            // carries nothing this client can authenticate, so it cannot be
            // told apart from one an attacker injected, and tearing the
            // attempt down would let a single spoofed datagram end a connect —
            // and, through ClearSessionData, spend the reconnect token with
            // it.  The genuine sealed SessionAck may still be in flight; the
            // re-emission ladder below stays armed for it and the connection
            // watchdog remains the terminal authority.  This matches the
            // sealed path in DecryptSessionAckPacket, which preserves the
            // one-shot key on an authentication failure for the same reason.
            if (RTMPE.Core.Protocol.PacketGates.IsSessionAckDowngrade(
                    ComputeLocalCaps(), wasEncrypted))
            {
                if (ShouldWarn(ref _lastCleartextSessionAckWarnTicks))
                    Debug.LogError(
                        "[RTMPE] Dropped a cleartext SessionAck: this client advertised " +
                        "encrypted-SessionAck support, so an unsealed bootstrap envelope " +
                        "is either spoofed or a downgraded gateway. Awaiting the sealed " +
                        "envelope; the connection timeout remains in force.");
                return;
            }

            // A SessionAck for this attempt means the Response was received and
            // the handshake is complete — retire the re-emission ladder now, even
            // if a later validation step below rejects this envelope, because a
            // resend of the Response could no longer change the outcome.
            _handshakeRetransmit.Disarm();

            // Remember whether this SessionAck is closing a reconnect flow —
            // we check it BEFORE the state transition below clears the context.
            bool wasReconnecting = _state == NetworkState.Reconnecting;

            var payload = PacketParser.ExtractPayload(data);

            if (!PacketParser.ParseSessionAck(payload,
                    out uint   cryptoId,
                    out string jwtToken,
                    out string reconnectToken,
                    out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps))
            {
                Debug.LogError("[RTMPE] SessionAck parse failed — malformed payload. Disconnecting.");
                return;
            }

            // Negotiate the session-effective capability set as the bitwise
            // intersection of what the SDK advertised in HandshakeResponse
            // (re-derived here via ComputeLocalCaps so the two are always
            // identical) and what the gateway returned in SessionAck.  The
            // intersection captured here gates the Send / AeadPipeline /
            // retransmit-tick paths for the rest of the session.  A legacy
            // gateway that does not understand the SessionAck tail yields
            // `gatewayCaps == None`, which makes the intersection empty and
            // falls back to the pre-capability behaviour — exactly what the
            // back-compat contract requires.
            RTMPE.Core.Protocol.CapabilityFlags localCaps = ComputeLocalCaps();
            _negotiatedPeerCaps =
                RTMPE.Core.Protocol.CapabilityFlagsWire.Negotiate(localCaps, gatewayCaps);

            // Secondary downgrade guard: a gateway that advertised
            // EncryptedSessionAck must also have sealed the bootstrap
            // envelope.  The PRIMARY refusal happens before parsing, keyed
            // on the LOCAL advertisement (PacketGates.IsSessionAckDowngrade)
            // — that one an on-path attacker cannot disarm.  This check is
            // retained as an independent invariant on the gateway's own
            // echo: it stays meaningful even if a future handshake revision
            // ever makes the local EncryptedSessionAck advertisement
            // conditional, and it costs one branch.  With the primary guard
            // upstream, a plaintext envelope never reaches this line today.
            if ((gatewayCaps & RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck) != 0
                && !wasEncrypted)
            {
                Debug.LogError(
                    "[RTMPE] SessionAck rejected: the gateway advertised encrypted-" +
                    "SessionAck support but delivered the bootstrap envelope in the " +
                    "clear. Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            // Validate the JWT before we trust the sub claim that becomes the
            // local session identifier.  When the gateway advertised
            // CapabilityFlags.IdentitySignedJwt the token is verified against
            // the server identity key the SDK verified on the Challenge —
            // how strong an anchor that is depends on the server-key pinning
            // mode.  Without that advertisement verification follows the
            // NetworkSettings configuration; either way the structural and
            // temporal claims are always enforced so a malformed or expired
            // token cannot install garbage as `_localPlayerId` and corrupt
            // every subsequent AEAD nonce.
            bool gatewayAssertsIdentityJwt =
                (gatewayCaps & RTMPE.Core.Protocol.CapabilityFlags.IdentitySignedJwt) != 0;

            // Honour the gateway's identity-signed-JWT assertion as a hard
            // requirement: if the identity key was not captured the SDK
            // cannot perform the promised verification, so it fails closed
            // rather than silently falling back to structural-only checks.
            // The Challenge always precedes SessionAck, so this is
            // unreachable today; the guard keeps a future handshake refactor
            // from quietly weakening token verification.
            if (gatewayAssertsIdentityJwt
                && (_serverIdentityPublicKey == null || _serverIdentityPublicKey.Length == 0))
            {
                Debug.LogError(
                    "[RTMPE] SessionAck rejected: the gateway advertised an " +
                    "identity-signed JWT but the server identity key was not " +
                    "available to verify it. Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            byte[] jwtVerificationKey =
                gatewayAssertsIdentityJwt ? _serverIdentityPublicKey : null;
            if (!TryValidateJwt(jwtToken,
                    expectedIssuer: _settings != null ? _settings.expectedJwtIssuer : null,
                    expectedAudience: _settings != null ? _settings.expectedJwtAudience : null,
                    jwtVerificationKey,
                    out string subject,
                    out string jwtError))
            {
                Debug.LogError(
                    $"[RTMPE] SessionAck rejected: JWT validation failed " +
                    $"({jwtError}). Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            if (!ulong.TryParse(subject, out var sessionId))
            {
                Debug.LogError(
                    "[RTMPE] SessionAck rejected: JWT sub claim is not a valid u64 session ID. " +
                    "Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            _sessionKeyStore.InstallCryptoId(cryptoId);
            _jwtToken       = jwtToken;
            _reconnectToken = reconnectToken;
            _localPlayerId  = sessionId;

            // Even under verbose logging we redact session-correlation identifiers
            // so support-bundle log captures never leak a full crypto_id or session_id
            // into third-party ticketing systems (Slack, Jira, Zendesk, etc.).
            LogDebug(
                $"SessionAck received: crypto_id={LogRedaction.Redact(cryptoId)}, " +
                $"session_id={LogRedaction.Redact(_localPlayerId)}, " +
                $"jwt_len={jwtToken?.Length ?? 0}");

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }

            // The attempt this ack belongs to, read before the application is
            // told anything.  Everything below the transition arms machinery
            // that is the property of THIS session — a liveness timer driven
            // from Update, the uplink's post-session capture, an auto-rejoin —
            // and the transition is where the application gets the stack.
            int sessionEpoch = System.Threading.Volatile.Read(ref _connectionAttemptEpoch);

            // Mark the session live BEFORE the state transition so any
            // observer hooked into TransitionTo (e.g. user-supplied
            // OnConnected handlers) sees a consistent snapshot.  The flag
            // is the centralised pre-dispatch gate's witness: without it
            // RequiresActiveSession-typed packets are dropped.
            _sessionEstablished = true;

            TransitionTo(NetworkState.Connected);

            // A handler may leave by either of two doors, and each moves a
            // different witness: beginning a fresh attempt moves the epoch,
            // while a plain Disconnect() — admitted from Connected, and run to
            // completion on this stack — moves only the state.  Past either
            // point a heartbeat armed here is a timer nothing owns: the
            // teardown that would have stopped it has already run, and neither
            // Connect() nor StartReconnectAttempt() touches the field, so six
            // intervals later its liveness verdict lands on whatever attempt is
            // live by then.  The uplink is the same question one field over —
            // ClearSessionData stops it without nulling it, so the null check
            // below would restart the instance the disconnect just retired.
            if (!AttemptIsStillLive(sessionEpoch) || _state != NetworkState.Connected) return;

            // Start keep-alive heartbeat.
            // Pass _packetBuilder so heartbeat packets share the same sequence
            // counter as all other outbound packets (prevents nonce reuse under AEAD).
            //
           // Lifecycle note: the OnRttUpdated lambda below captures `this` (via
            // LastRttMs and SafeRaise on instance event fields).  This is safe
            // because Cleanup() nulls _heartbeatManager — releasing the only
            // outstanding reference to the HeartbeatManager and therefore the
            // only path that could invoke the lambda — and OnDestroy() calls
            // Cleanup() before the NetworkManager itself is finalized.  The
            // lambda is therefore guaranteed to die with the manager it was
            // wired to.  Do NOT replace these with named methods unless the
            // Cleanup path is also updated to explicitly unsubscribe — the
            // implicit "_heartbeatManager = null drops the chain" contract is
            // load-bearing.
            // Reconnect path: a previous SessionAck may have wired event
            // handlers onto an earlier HeartbeatManager.  Stop and detach the
            // old one explicitly before assigning the new instance so the
            // old multicast list cannot accumulate stale per-cycle
            // subscriptions across reconnect bursts.  Stop() is idempotent
            // and safe even when the manager has not yet started.
            if (_heartbeatManager != null)
            {
                _heartbeatManager.Stop();
                _heartbeatManager.OnHeartbeatTimeout -= OnHeartbeatTimeout;
                // OnRttUpdated was wired to a fresh closure on each session,
                // so the old delegate list is unreachable once
                // _heartbeatManager is replaced — no symmetric -= needed.
            }

            _heartbeatManager = new HeartbeatManager(
                _settings.heartbeatIntervalMs, _packetBuilder, _settings.heartbeatLivenessGraceMs);
            _heartbeatManager.OnRttUpdated     += rtt => { LastRttMs = rtt; SafeRaise(OnRttUpdated, rtt, nameof(OnRttUpdated)); };
            _heartbeatManager.OnHeartbeatTimeout += OnHeartbeatTimeout;
            _heartbeatManager.Start();

            // SDK diagnostic uplink: transition from pre-session capture to normal
            // post-session capture.  Connect() / StartReconnectAttempt() already
            // created the instance and called StartPreSessionCapture() so handshake
            // errors are in its buffer.  Start() drains that buffer into the main
            // queue and begins the normal post-session hook.  Create a fresh instance
            // only if the setting was enabled after Connect() fired (edge case).
            if (_settings != null && _settings.enableDiagnosticsUplink)
            {
                if (_diagnosticsUplink == null)
                    _diagnosticsUplink = new Diagnostics.DiagnosticsUplink(_settings, _packetBuilder);
                _diagnosticsUplink.Start();
            }

            // Auto-rejoin the last room after a successful reconnect, if enabled.
            // The session is now fully established, so RoomManager.RequireConnected
            // will pass.  We intentionally do NOT clear _lastRoomId here — the
            // subsequent OnRoomJoined handler will refresh the snapshot with the
            // fresh RoomInfo returned by the server.
            if (wasReconnecting && _settings != null && _settings.autoRejoinLastRoomOnReconnect)
            {
                TryAutoRejoinLastRoom();
            }
        }

        /// <summary>
        /// Attempt to rejoin <see cref="LastRoomId"/> via
        /// <see cref="RoomManager.JoinRoom"/>.  No-op when no snapshot exists.
        /// Silent on RoomManager internal failures — the app can observe the
        /// outcome through the existing <see cref="RoomManager.OnRoomJoined"/> /
        /// <see cref="RoomManager.OnRoomError"/> events.
        /// </summary>
        private void TryAutoRejoinLastRoom()
        {
            if (string.IsNullOrEmpty(_lastRoomId))
            {
                LogDebug("Reconnect: no last room to auto-rejoin.");
                return;
            }

            if (_roomManager == null)
            {
                Debug.LogWarning("[RTMPE] Auto-rejoin: RoomManager is null (internal invariant violation).");
                return;
            }

            LogDebug($"Reconnect: auto-rejoining last room {_lastRoomId}.");
            SafeRaise(OnAutoRejoinAttempt, _lastRoomId, nameof(OnAutoRejoinAttempt));
            _roomManager.JoinRoom(_lastRoomId);
        }

    }
}
