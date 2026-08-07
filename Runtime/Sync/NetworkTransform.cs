// RTMPE SDK — Runtime/Sync/NetworkTransform.cs
//
// MonoBehaviour component that synchronises a GameObject's position, rotation,
// and optionally scale over the RTMPE network, with optional client-side
// prediction (CSP) for the owning client.
//
// Design decisions:
//  • Extends NetworkBehaviour — inherits NetworkObjectId, IsOwner,
//    IsSpawned, and the OnNetworkSpawn / OnNetworkDespawn callbacks.
//  • Owner-only sending: only the authoritative owner sends transform updates.
//    All other clients receive server-broadcast StateDelta payloads via the
//    HandleStateSyncPacket handler in NetworkManager.
//  • Two thresholds guard against send spam:
//    - _positionThreshold (0.01 world units): Vector3.Distance check
//    - _rotationThreshold (0.1 degrees):      Quaternion.Angle check
//  • MarkClean() records the last-sent transform so the next Update() compares
//    against that baseline, not the object's initial spawn position.
//  • GetState() / ApplyState() provide a type-safe boundary between Unity
//    transform fields and the serialisation layer, enabling unit testing.
//  • _syncScale defaults to false because scale rarely changes at runtime.
//
// Client-side prediction (CSP) — optional, disabled by default:
//  When _enablePrediction is true the owner client:
//    1. Calls GatherInput() each 30 Hz tick, stamps the tick, pushes it to
//       _inputBuffer.  Game code moves the character immediately (prediction).
//    2. Sends the resulting transform to the server as usual.
//    3. When the server broadcasts back an authoritative StateDelta for this
//       object, ApplyReconciliation() fires (routed by NetworkManager):
//         • Error > _snapThreshold  →  snap directly to server position.
//         • Error > _lerpThreshold  →  start a 100 ms smooth lerp toward
//           the server position (_reconcileTimeLeft drives the blend).
//         • Error <= _lerpThreshold →  accept prediction as-is (no visual pop).
//    4. AcknowledgeUpTo(LocalTick - 1) trims the buffer each reconciliation.
//
//  Non-owning clients are unaffected by the prediction fields — they use the
//  NetworkTransformInterpolator for smooth playback.
//
// Threading: all methods run on the Unity main thread.

using System.Buffers;

using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>
    /// Synchronises a <see cref="GameObject"/>'s Transform over the RTMPE network.
    /// Attach this component to any networked prefab together with a
    /// <see cref="NetworkObjectRegistry"/> identifier.
    /// </summary>
    [AddComponentMenu("RTMPE/Network Transform")]
    public class NetworkTransform : NetworkBehaviour
    {
        // ── Inspector — Sync axes ──────────────────────────────────────────────

        [Header("Sync Axes")]
        [Tooltip("Sync world-space position.")]
        [SerializeField] private bool _syncPosition = true;

        [Tooltip("Sync world-space rotation.")]
        [SerializeField] private bool _syncRotation = true;

        [Tooltip("Sync local-space scale. Disabled by default (scale rarely changes).")]
        [SerializeField] private bool _syncScale = false;

        [Tooltip("Report the pose held on the simulation tick boundary instead of the one held " +
                 "at the visual frame that broadcasts it.  The owner samples its transform mid-tick " +
                 "but labels the sample with the current tick, and a receiver replays it as though " +
                 "it had been captured on the boundary; the difference walks across the tick " +
                 "whenever the frame rate is not a multiple of the tick rate, and is rendered as " +
                 "motion the owner never made.  When enabled the broadcast pose is interpolated " +
                 "back to the boundary between this sample and the previous one — never " +
                 "extrapolated — so what peers replay is the pose actually held at the labelled " +
                 "tick.  Pairs with the interpolator's owner-tick timeline, which is what puts the " +
                 "owner's tick on the receiver's clock; with the server-tick timeline the server " +
                 "re-labels the sample and the correction does not reach the receiver.  Disabled " +
                 "by default: the raw frame sample is broadcast unless opted in.")]
        [SerializeField] private bool _tickAlignedSampling = false;

        /// <summary>
        /// The sending half of remote motion timing, as configured on this
        /// component.  Read by the receive dispatch so a half-enabled pairing —
        /// this on while the interpolator's <c>OwnerTickTimeline</c> is off, or
        /// the reverse — is surfaced rather than silently doing nothing; see
        /// <see cref="RTMPE.Core.Diagnostics.RemoteMotionTimingAdvisory"/>.
        ///
        /// The dispatch reads it from a replica this client does not own, which
        /// is sound for the question being asked: it reports whether THIS build's
        /// prefab pairs the two halves, not what the remote owner's build does.
        /// Where a title ships per-platform builds the two can differ, so the
        /// answer is "is this project configured consistently" — never "what did
        /// that particular peer send".
        /// </summary>
        internal bool TickAlignedSampling => _tickAlignedSampling;

        // ── Inspector — Send thresholds ────────────────────────────────────────

        [Header("Send Thresholds")]
        [Tooltip("Minimum position change in world units before an update is sent.")]
        [SerializeField] private float _positionThreshold = 0.01f;

        [Tooltip("Minimum rotation change in degrees before an update is sent.")]
        [SerializeField] private float _rotationThreshold = 0.1f;

        [Tooltip("Minimum local-scale change per axis before an update is sent. " +
                 "Only evaluated when _syncScale is enabled.")]
        [SerializeField] private float _scaleThreshold = 0.001f;

        // ── Inspector — Client-side prediction ────────────────────────────────

        [Header("Client-Side Prediction")]
        [Tooltip("Enable client-side prediction and server reconciliation for the owning client. " +
                 "Requires game code to override GatherInput() on the NetworkBehaviour subclass.")]
        [SerializeField] private bool _enablePrediction = false;

        [Tooltip("Position error (world units) below which the prediction is accepted as-is. " +
                 "Leave at -1 (default) to inherit NetworkSettings.reconcileLerpThreshold; " +
                 "any non-negative override applies per-instance and ignores the project default.")]
        [SerializeField] private float _lerpThreshold = ReconcileUseProjectDefault;

        [Tooltip("Position error (world units) above which the object snaps immediately to the " +
                 "server position rather than lerping. Leave at -1 (default) to inherit " +
                 "NetworkSettings.reconcileSnapThreshold.")]
        [SerializeField] private float _snapThreshold = ReconcileUseProjectDefault;

        // Sentinel meaning "this Inspector field has not been overridden — read the
        // project-wide default from NetworkSettings on spawn".  -1 is chosen because
        // a negative threshold has no physical meaning (Vector3.Distance is always
        // non-negative, so any genuine use-case value is >= 0); using a sentinel
        // distinct from 0 lets a designer explicitly opt into "never lerp / always
        // snap" by setting the threshold to literal 0.
        internal const float ReconcileUseProjectDefault = -1f;

        // Final, resolved thresholds — the two _*Threshold fields above hold the
        // raw Inspector value (potentially the sentinel); these hold the value
        // actually consulted by ApplyReconciliation each frame.  Resolution
        // happens once on spawn and again on settings changes (rare).
        private float _resolvedLerpThreshold;
        private float _resolvedSnapThreshold;

        // ── Last-sent baseline ─────────────────────────────────────────────────

        private Vector3    _lastPosition;
        private Quaternion _lastRotation;
        private Vector3    _lastScale;

        // ── CSP state ──────────────────────────────────────────────────────────

        // Input ring buffer: stores unacknowledged InputPayloads for rollback.
        private readonly InputBuffer _inputBuffer = new InputBuffer();

        // Reconciliation lerp target, start (captured once at schedule time),
        // and remaining-time accumulator.
        //
       // Why both start-pose and time accumulator: true linear interpolation
        // requires a fixed start position so each frame's blend is
        //  pos = Lerp(_reconcileStart, _reconciledTarget, elapsed / duration)
        // — not a recursive Lerp(transform.position, target, dt/timeLeft) which
        // is mathematically an exponential ease-out and only reaches the target
        // because of the explicit end-frame snap.  Capturing the start pose at
        // schedule time fixes the blend and keeps it framerate-independent at
        // 30 / 60 / 120 / 144 fps.
        private Vector3    _reconciledTarget;
        private Quaternion _reconciledRotationTarget;
        private Vector3    _reconcileStart;
        private Quaternion _reconcileStartRotation;
        private float      _reconcileTimeLeft;

        // Guards input collection to exactly one push per LocalTick.
        // Update() runs at frame rate (e.g. 60 Hz) but LocalTick advances at 30 Hz;
        // without this guard the buffer would accumulate two entries per tick at 60 fps.
        // A separate _hasLastInputTick flag is used instead of a sentinel value so
        // every uint LocalTick (including 0 and uint.MaxValue) is unambiguously valid.
        private uint _lastInputTick;
        private bool _hasLastInputTick;

        // Guards 0x43 input-batch transmission to exactly once per LocalTick.
        // Phase 2.x (2026-04-25) — server-authoritative input pipeline.
        // The batch is built fresh from the input buffer on each transmission
        // and replays every unacknowledged frame, so a missed tick is recovered
        // from the next batch — but a duplicate-per-frame send wastes bandwidth.
        // Companion bool flag mirrors the _hasLastInputTick pattern.
        private uint _lastInputSendTick;
        private bool _hasLastInputSendTick;

        // Guards the owner's 0x40 transform broadcast to at most once per
        // LocalTick.  Update() runs at the visual frame rate; without this gate
        // a high-fps owner broadcasts far above the 30 Hz tick cadence the
        // server coalesces to, overrunning the per-session state budget so the
        // surplus is silently dropped and remote motion turns to jitter.
        private uint _lastTransformSendTick;
        private bool _hasLastTransformSendTick;

        // Cadence for the owner's at-rest keepalive: an idle owner re-sends its
        // unchanged pose every this-many simulation ticks so the tick engine
        // keeps the object in its state set and a late joiner receives the
        // current pose in its join snapshot.  MUST stay below the sync tick
        // engine's per-object stale timeout (3 s ≈ 90 ticks at 30 Hz); 30 ticks
        // (~1 s) holds a wide margin while keeping idle traffic negligible.
        private const uint TransformKeepaliveTicks = 30;

        // Wall-clock companion to TransformKeepaliveTicks.  The tick cursor above
        // can lag real time (NetworkManager caps the sim catch-up loop and drops
        // the surplus after a long frame), which under a severe hitch or a
        // sustained sub-tick frame rate would stretch the tick-based keepalive
        // past the server's WALL-CLOCK stale timeout and evict a present owner's
        // idle object.  This floor forces a refresh after this many seconds of
        // real time regardless of tick progress.  1 s mirrors the 30-tick cadence
        // and keeps a wide margin under the 3 s server timeout.
        private const double TransformKeepaliveSeconds = 1.0;

        // Unscaled real time of the last transform broadcast (moved or keepalive),
        // driving the wall-clock keepalive floor above.
        private double _lastTransformSendUnscaledTime;

        // Previous RAW transform sample and the instant it was taken, held as the
        // far end of the tick-alignment interpolation.  Deliberately separate
        // from the _lastPosition / _lastRotation change-detection baseline: that
        // baseline is also re-armed by the reconciliation paths, which move the
        // transform without a send, so pairing it with a send timestamp would
        // interpolate between two poses that never bracketed a tick boundary.
        // The sample stored here is the one read off the transform, before any
        // alignment or velocity clamp, so successive corrections compose from
        // observed poses rather than from previously corrected output.
        private Vector3    _alignSamplePosition;
        private Quaternion _alignSampleRotation;
        private double     _alignSampleTime;
        private bool       _hasAlignSample;

        // Reusable scratch array sized for the maximum batch.  Allocated once
        // per NetworkTransform so SendInputBatch() does NOT allocate on the
        // hot path — critical for staying inside the 33 ms tick budget when
        // many transforms send concurrently.  The InputBuffer's Capacity is
        // the upper bound on entries the buffer can ever hand out.
        private readonly InputPayload[] _inputSendScratch = new InputPayload[InputBuffer.Capacity];

        // Total wall-clock time (seconds) allowed for a medium-error reconciliation
        // lerp.  100 ms (3 ticks at 30 Hz) matches the previous 3-frame window at
        // 30 fps while remaining framerate-independent at 60/120/144 fps.
        private const float ReconcileDuration = 0.1f;

        // Reusable scratch for the rollback replay path.  Sized for the full
        // ring buffer so the worst-case "every frame is unacked" rollback
        // (saturated 2 s of input at 30 Hz) does not allocate during the
        // reconciliation hot path.
        private readonly InputPayload[] _replayScratch = new InputPayload[InputBuffer.Capacity];

        // ── Change-detection properties ────────────────────────────────────────

        /// <summary>
        /// True when the object has moved more than <c>_positionThreshold</c>
        /// world units since the last <see cref="MarkClean"/> call.
        /// </summary>
        public bool HasPositionChanged
            => Vector3.Distance(transform.position, _lastPosition) > _positionThreshold;

        /// <summary>
        /// True when the object has rotated more than <c>_rotationThreshold</c>
        /// degrees since the last <see cref="MarkClean"/> call.
        /// </summary>
        public bool HasRotationChanged
            => Quaternion.Angle(transform.rotation, _lastRotation) > _rotationThreshold;

        /// <summary>
        /// True when <c>_syncScale</c> is enabled and the object's local scale
        /// has changed by more than <c>_scaleThreshold</c> per axis since the
        /// last <see cref="MarkClean"/> call.
        /// </summary>
        public bool HasScaleChanged
            => _syncScale && Vector3.Distance(transform.localScale, _lastScale) > _scaleThreshold;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Capture the current transform into a <see cref="TransformState"/> snapshot.
        /// </summary>
        public TransformState GetState() => new TransformState
        {
            Position = transform.position,
            Rotation = transform.rotation,
            Scale    = transform.localScale,
        };

        /// <summary>
        /// Apply a received <see cref="TransformState"/> to this object's transform.
        /// Only axes with the corresponding sync flag enabled are written.
        /// </summary>
        public void ApplyState(TransformState state)
        {
            if (_syncPosition) transform.position   = state.Position;
            if (_syncRotation) transform.rotation   = state.Rotation;
            if (_syncScale)    transform.localScale = state.Scale;
        }

        /// <summary>
        /// Record the current transform as the new "last-sent" baseline.
        /// Call this after sending an update so the next <see cref="Update"/>
        /// compares against the just-sent values, not the initial spawn position.
        /// </summary>
        public void MarkClean()
        {
            MarkChangeDetectionBaseline();
            // Every caller of MarkClean re-establishes the baseline because
            // something moved the transform without the owner driving it there —
            // a reconciliation snap or lerp, a teleport, or the spawn pose.
            // Interpolating a tick correction across such a jump would report a
            // pose between the two, so the far end of the correction is dropped
            // and the next broadcast starts a fresh pair.
            //
            // The broadcast path must not route through here.  It takes
            // MarkChangeDetectionBaseline instead, because the anchor is read at
            // the top of every send: clearing it anywhere in that loop leaves it
            // false at every read and the correction never runs.  Reordering the
            // two calls does not avoid this — either order clears the anchor once
            // per send cycle, before it is consumed.
            _hasAlignSample = false;
        }

        /// <summary>
        /// Re-arm the change-detection baseline — the pose the next
        /// <c>Update</c> compares against to decide whether anything moved —
        /// WITHOUT disturbing the tick-alignment anchor.
        ///
        /// The anchor is the previous raw sample and is what the next send
        /// interpolates back from; the send itself establishes it.  Routing the
        /// broadcast path through <see cref="MarkClean"/> would clear that anchor
        /// before every send, leaving <c>_hasAlignSample</c> false at every read.
        /// </summary>
        private void MarkChangeDetectionBaseline()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale    = transform.localScale;
        }

        // ── Internal CSP API (called by NetworkManager) ───────────────────────

        /// <summary>
        /// Apply a server-authoritative <see cref="TransformState"/> to the owning
        /// client's prediction.  Called by <c>NetworkManager.HandleStateSyncPacket</c>
        /// when a <c>StateDelta</c> for this object is received by its owner.
        ///
       /// <para>When <c>_enablePrediction</c> is false this is a no-op —
        /// non-prediction owners do not reconcile.</para>
        /// </summary>
        internal void ApplyReconciliation(TransformState serverState)
        {
            // Without an explicit confirmedInputTick on the wire, fall back to
            // (LocalTick - 1) as the most-recent input the server can possibly
            // have processed.  This matches the original SDK behaviour and is
            // the worst case for replay (no in-flight inputs to re-simulate)
            // — but the new replay-aware overload still keeps the buffer
            // intact above that watermark so any genuinely-in-flight input is
            // re-applied on top of the snapped state.
            var nm = NetworkManager.Instance;
            uint confirmedInputTick = 0u;
            bool hasConfirmedTick   = false;
            if (nm != null && nm.LocalTick > 0u)
            {
                confirmedInputTick = nm.LocalTick - 1u;
                hasConfirmedTick   = true;
            }
            ApplyReconciliation(serverState, confirmedInputTick, hasConfirmedTick);
        }

        /// <summary>
        /// Reconcile against a server-authoritative <see cref="TransformState"/>
        /// that explicitly carries the highest input tick the server has
        /// applied to this object.  Snaps the transform back to the server
        /// pose at <paramref name="confirmedInputTick"/>, then replays every
        /// buffered input strictly greater than that watermark via
        /// <see cref="NetworkBehaviour.ApplyInput"/> so the local prediction
        /// stays consistent with the keystrokes the player has issued since
        /// the server's snapshot was taken.
        /// </summary>
        internal void ApplyReconciliation(
            TransformState serverState,
            uint           confirmedInputTick,
            bool           hasConfirmedTick)
        {
            // An owner is the sole authority over the objects it owns: it drives
            // them from its local transform and ships that pose to peers. The
            // RTMPE Sync Service relays those poses without an authoritative
            // simulation, so the state echoed back to the owner is only a
            // ~1-tick-delayed copy of its own uplink. Applying it here would drag
            // the object toward that stale pose on every server beat — a
            // rubber-band against live input that is visible even with a single
            // player in the room. Reconcile an owned object only against a server
            // configured to simulate authoritatively; otherwise the local
            // transform is already ground truth and must not be overwritten. A
            // null manager/settings (headless fixtures) keeps the historical
            // reconcile behaviour so the CSP contract stays under test.
            var settings = NetworkManager.Instance?.Settings;
            if (settings != null && !settings.reconcileOwnedObjects) return;

            // Full-state finiteness gate — covers Position, Rotation
            // (including the .w component, which PhysX may surface as NaN
            // after divide-by-zero recovery), and Scale.  The legacy
            // position-only check left rotation and scale unguarded; an
            // internal caller (test harness, custom dispatcher, future
            // server-broadcast path) could persist a NaN quaternion into
            // transform.rotation, after which every Slerp / Quaternion.Angle
            // returns NaN for the lifetime of the GameObject.
            if (!IsFiniteState(serverState)) return;

            if (!_enablePrediction)
            {
                // Passive-reconciliation path.  Non-predicting owners still
                // honour server-authoritative deltas — without this snap an
                // owner that disabled prediction would silently desync from
                // every other peer until the next state-resync, masquerading
                // as authoritative while the rest of the room observed the
                // server's truth.  Snap directly without the replay loop
                // (no input buffer to walk) and without the lerp blend
                // (the owner did not predict, so there is nothing to blend
                // *from*).  The teleport guards still apply here: previously
                // this default path bypassed them entirely, so the correction
                // cap and world bounds documented as anti-teleport protection
                // never ran for non-predicting owners.  Reject exactly as the
                // predictive path does — keep the local pose, re-assert it on
                // the next send — so a hostile server cannot relocate an
                // honest client's avatar to any finite position.
                if (_syncPosition)
                {
                    float passiveError =
                        Vector3.Distance(serverState.Position, transform.position);
                    if (RejectsServerCorrection(serverState.Position, passiveError))
                        return;
                    transform.position = serverState.Position;
                }
                if (_syncRotation) transform.rotation = serverState.Rotation;
                MarkClean();
                // As on the predictive path below: the server moved this object,
                // so the correction is not travel the velocity cap should charge
                // to the owner.  Only when the position was actually accepted —
                // a rejected correction leaves the pose, and the baseline, alone.
                if (_syncPosition) RebaseVelocityBaseline(transform.position);
                return;
            }

            var nm = NetworkManager.Instance;

            // Trim the input buffer up to the confirmed watermark.  The
            // server has now produced a state that incorporates every input
            // at or below this tick; anything still in the ring above it
            // remains "in-flight" and is re-simulated by the replay loop
            // further down.
            if (hasConfirmedTick)
                _inputBuffer.AcknowledgeUpTo(confirmedInputTick);

            float error = Vector3.Distance(serverState.Position, transform.position);

            // NaN/Inf positions (crafted packet or physics explosion) must not
            // corrupt transform.position through the reconciliation lerp path.
            if (float.IsNaN(error) || float.IsInfinity(error)) { MarkClean(); return; }

            // ── Server-correction cap & world bounds ─────────────────────────
            // A hostile or compromised server must not be able to teleport the
            // local client to an arbitrary world position.  The same two
            // guards run on the passive path above, so the shared helper is
            // the single definition of the teleport-rejection policy.
            if (RejectsServerCorrection(serverState.Position, error))
                return;

            if (error <= _resolvedLerpThreshold)
            {
                // Prediction was close enough — accept it, no visual correction.
                return;
            }

            if (error >= _resolvedSnapThreshold)
            {
                // Large error — snap immediately to avoid sustained visual drift.
                if (_syncPosition) transform.position = serverState.Position;
                if (_syncRotation) transform.rotation = serverState.Rotation;
                _reconcileTimeLeft = 0f;

                // ── CSP replay loop ───────────────────────────────────────────
                // After snapping back to the server-authoritative pose, walk
                // every input the player has issued since confirmedInputTick
                // and re-apply it on top of the snapped state.  This is the
                // canonical Quake / Source / Overwatch reconciliation flow:
                // server state is treated as ground-truth at the confirmed
                // tick, the local simulation rewinds to that tick, then
                // fast-forwards through the in-flight inputs to land at a
                // pose that already incorporates this frame's keystrokes.
                //
                // Without the replay step, every snap discards the player's
                // recent input — a noticeable hitch on every server
                // correction.  With it, the only visible artifact is the
                // small position delta produced by network latency, which
                // the lerp threshold absorbs on the next correction.
                // The server moved this object; the owner did not travel here.
                // Left on the pre-snap pose, the velocity cap would read the
                // correction as the owner's own motion and meter the broadcast
                // back toward a position the server already holds — at cap speed,
                // for as long as the correction was large.
                //
                // Taken BEFORE the replay: what the server asserted is not the
                // owner's travel, but the inputs replayed on top of it are, and
                // they are the owner's own log.  Re-basing after the replay would
                // excuse the whole of it — as many ticks of movement as the
                // buffer holds, at whatever speed the game moves per input.
                if (_syncPosition) RebaseVelocityBaseline(serverState.Position);

                if (hasConfirmedTick)
                    ReplayUnackedInputs(confirmedInputTick);

                // Update baseline so the next frame does not register a spurious
                // threshold violation and send the snapped position back to the server.
                MarkClean();
                return;
            }

            // Medium error — smooth linear lerp over ReconcileDuration seconds.
            //
            // Replay the in-flight input log on top of the server-confirmed
            // pose BEFORE capturing the lerp target.  Without this step, the
            // lerp settles on the server's stale snapshot at confirmedInputTick
            // and silently discards every keystroke the player issued between
            // that tick and "now" — producing recurring rubber-band / micro-
            // stutter on every server beat under any non-zero RTT.  The snap
            // branch above already calls ReplayUnackedInputs; the medium-error
            // branch must apply the same canonical CSP flow so the visible
            // pose incorporates current-frame input.
            //
            // Sequence:
            //   1. Capture current transform.position as the lerp start.
            //   2. Temporarily fast-forward transform.position from
            //      serverState.Position through the unacked input log.
            //   3. Read the post-replay pose as the lerp target.
            //   4. Restore the transform to the lerp start so the per-frame
            //      Update() blend produces a true linear interpolation
            //      from the player's pre-correction visual pose to the
            //      replay-adjusted target.
            _reconcileStart         = transform.position;
            _reconcileStartRotation = transform.rotation;

            if (_syncPosition) transform.position = serverState.Position;
            if (_syncRotation) transform.rotation = serverState.Rotation;
            if (hasConfirmedTick)
                ReplayUnackedInputs(confirmedInputTick);

            _reconciledTarget         = transform.position;
            _reconciledRotationTarget = transform.rotation;

            // Restore the visible pose so the lerp blends from where the
            // player saw the object to the replayed target.  Without the
            // restore, the medium-error branch would degenerate into a
            // hard snap-then-lerp-back cycle that visually inverts the
            // intended smoothing.
            if (_syncPosition) transform.position = _reconcileStart;
            if (_syncRotation) transform.rotation = _reconcileStartRotation;

            _reconcileTimeLeft = ReconcileDuration;

            // Mark the restored pose as clean so the next frame's change-
            // detection compares against _reconcileStart (the current visible
            // position), not the pre-correction stale baseline.  Without this
            // call the outbound StateSync deltas computed during the lerp
            // reference the old _lastPosition and look like intentional owner
            // movement to the server — causing a rubber-band feedback loop on
            // lossy links (SDKS-02).  The snap branch already calls MarkClean()
            // at the equivalent point (line 398); this mirrors that pattern.
            MarkClean();
        }

        /// <summary>
        /// Decide whether a server-authoritative correction to
        /// <paramref name="serverPosition"/> must be REJECTED (local pose
        /// kept) under the configured teleport guards.  Returns
        /// <see langword="true"/> to reject.
        /// </summary>
        /// <param name="serverPosition">The position the server is asserting.</param>
        /// <param name="error">Distance from the current local pose to
        /// <paramref name="serverPosition"/>.  Callers pass a value already
        /// proven finite by <c>IsFiniteState</c>.</param>
        /// <remarks>
        /// A hostile or compromised server must not be able to teleport the
        /// local client to an arbitrary world position.  Both guards run for
        /// EVERY reconciliation path — predictive and passive — so the
        /// protection documented on these settings does not depend on whether
        /// the owner happens to enable client-side prediction.
        ///
        /// Defaults are SECURE, not pass-through: <c>maxServerCorrectionDistance</c>
        /// ships at 50 (the distance cap is ACTIVE by default, matching the
        /// sibling <c>maxPositionDeltaPerTick = 50</c> anti-teleport default), so
        /// a server correction farther than 50 world-units is rejected out of the
        /// box and the local position is kept.  <c>worldBoundsEnabled</c> defaults
        /// to false (the AABB guard is opt-in).  Each guard is fully bypassed only
        /// when its setting is disabled (<c>maxServerCorrectionDistance = 0</c> /
        /// <c>worldBoundsEnabled = false</c>); a scene that legitimately performs
        /// large authoritative jumps (e.g. long-range respawn/teleport) must raise
        /// or zero the cap, otherwise those corrections are silently rejected.
        /// </remarks>
        private bool RejectsServerCorrection(Vector3 serverPosition, float error)
        {
            var settings = NetworkManager.Instance?.Settings;
            if (settings == null) return false;

            if (settings.maxServerCorrectionDistance > 0f
                && error > settings.maxServerCorrectionDistance)
            {
                Debug.LogWarning(
                    "[RTMPE] NetworkTransform.ApplyReconciliation: rejected " +
                    $"server correction of {error:F2}m (cap " +
                    $"{settings.maxServerCorrectionDistance:F2}m) — keeping " +
                    "local prediction.", this);
                return true;
            }

            if (settings.worldBoundsEnabled)
            {
                Vector3 d = serverPosition - settings.worldBoundsCenter;
                Vector3 e = settings.worldBoundsExtents;
                if (Mathf.Abs(d.x) > e.x
                    || Mathf.Abs(d.y) > e.y
                    || Mathf.Abs(d.z) > e.z)
                {
                    Debug.LogWarning(
                        "[RTMPE] NetworkTransform.ApplyReconciliation: rejected " +
                        $"server position {serverPosition} outside world " +
                        "bounds — keeping local prediction.", this);
                    return true;
                }
            }

            return false;
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// After the object is spawned, record the current transform baseline
        /// and reset prediction state so the first change-detection comparison
        /// is against the spawn position.
        /// </summary>
        protected override void OnNetworkSpawn()
        {
            MarkClean();
            _inputBuffer.Clear();
            _reconcileTimeLeft        = 0f;
            _reconciledRotationTarget = transform.rotation;
            _hasLastInputTick         = false;
            _hasLastInputSendTick     = false;
            _hasLastTransformSendTick = false;
            // The velocity cap measures from the pose the object spawned at,
            // never from wherever it last broadcast in a previous life: a pooled
            // object re-spawned across the map would otherwise be charged the
            // whole distance between the two, and clamp legitimate initial
            // motion for as long as it took to pay that off.
            //
            // Stating the discontinuity rather than suspending the gate for a
            // send: the object did not travel to its spawn point, and everything
            // it does from there is governed from the first frame.  Suspending it
            // instead hands one unmeasured broadcast to any subclass that calls
            // this method, which is public surface — it overrides on a class that
            // is neither sealed nor internal.
            RebaseVelocityBaseline(transform.position);
            _sendableSecondsSinceSent = 0.0;
            ResolveReconcileThresholds();
        }

        /// <summary>
        /// Resolve the per-instance lerp/snap thresholds, falling back to
        /// <see cref="NetworkSettings.reconcileLerpThreshold"/> /
        /// <see cref="NetworkSettings.reconcileSnapThreshold"/> when the
        /// Inspector field is left at <see cref="ReconcileUseProjectDefault"/>.
        /// Also enforces snap &gt; lerp by clamping snap upward when a designer
        /// authors them inverted; otherwise the lerp branch (error &lt;= lerp)
        /// would always succeed and the snap branch would be unreachable.
        /// </summary>
        private void ResolveReconcileThresholds()
        {
            // Inspector overrides: any non-negative value wins over the
            // project default.  Negative values (the sentinel) trigger
            // resolution from NetworkSettings.
            float lerp = _lerpThreshold;
            float snap = _snapThreshold;

            var settings = NetworkManager.Instance != null
                ? NetworkManager.Instance.Settings
                : null;

            if (lerp < 0f) lerp = settings != null ? settings.reconcileLerpThreshold : 0.1f;
            if (snap < 0f) snap = settings != null ? settings.reconcileSnapThreshold : 2.0f;

            // Inverted authoring (snap < lerp) would make the snap branch
            // unreachable.  Clamp snap to lerp so the worst-case behaviour is
            // "every error above lerp snaps" — degraded but coherent.
            if (snap < lerp) snap = lerp;

            _resolvedLerpThreshold = lerp;
            _resolvedSnapThreshold = snap;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test seam — re-resolve the thresholds without re-spawning.  Lets a
        /// fixture mutate <see cref="NetworkSettings"/> after the component is
        /// constructed and exercise both the inherit and override paths.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void ConfigureReconcileForTest(float lerpThreshold, float snapThreshold)
        {
            _lerpThreshold = lerpThreshold;
            _snapThreshold = snapThreshold;
            ResolveReconcileThresholds();
        }

        /// <summary>Resolved lerp threshold (test seam).</summary>
        internal float ResolvedLerpThreshold => _resolvedLerpThreshold;

        /// <summary>Resolved snap threshold (test seam).</summary>
        internal float ResolvedSnapThreshold => _resolvedSnapThreshold;
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Per-tick CSP work, driven by NetworkManager's fixed-cadence tick
        /// loop so a long frame still collects (and ships) one input sample
        /// per simulated tick rather than silently dropping the stutter's
        /// worth of keystrokes.  See <see cref="NetworkBehaviour.OnFixedTick"/>
        /// for the contract.
        /// </summary>
        protected override void OnFixedTick(float deltaTime)
        {
            if (!_enablePrediction) return;

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            // The tick driver guarantees exactly one invocation per simulated
            // tick, but the per-instance dedupe is retained as belt-and-braces
            // against re-entrant dispatch (e.g. a future settings reload that
            // restarts the loop mid-frame).
            if (_hasLastInputTick && nm.LocalTick == _lastInputTick) return;
            _lastInputTick    = nm.LocalTick;
            _hasLastInputTick = true;

            var input = CollectInput(nm.LocalTick);
            // Push returns false when the rollback window is saturated
            // (newest rejected to preserve the oldest as a replay anchor).
            // The drop is reflected in _inputBuffer.DroppedInputCount;
            // we deliberately do not log on the hot path because a
            // genuine network stall produces one rejection per tick and
            // the counter alone is enough to surface the condition via
            // the debugger window / telemetry.
            _inputBuffer.Push(input);

            // ── Server-authoritative input send (Phase 2.x) ─────────────
            // Re-ship the unacknowledged buffer once per tick.  This is
            // gated on _enablePrediction because the input buffer is only
            // filled when prediction is on; sending an empty batch every
            // tick from non-predicting owners would just burn bandwidth.
            // Each batch supersedes the prior, so a dropped UDP packet
            // costs at most one tick of latency.
            if (!_hasLastInputSendTick || nm.LocalTick != _lastInputSendTick)
            {
                _lastInputSendTick    = nm.LocalTick;
                _hasLastInputSendTick = true;
                SendInputBatch();
            }
        }

        /// <summary>
        /// Each frame:
        ///  • Owner (with or without prediction): if transform changed beyond
        ///    thresholds, transmit an update and mark clean.
        ///  • Reconciliation lerp: if pending, blend toward server target.
        ///
        /// CSP input collection no longer lives here — it runs from
        /// <see cref="OnFixedTick"/> so the cadence is locked to the
        /// simulation tick, not the visual frame.
        /// </summary>
        private void Update()
        {
            if (!IsOwner || !IsSpawned) return;

            // The owner velocity cap spends a budget that accrues at the cap
            // rate, and this is the clock it accrues on: time during which this
            // component was live and able to broadcast.  Wall-clock is the wrong
            // measure — a component that is disabled, unowned or despawned stops
            // broadcasting while wall-clock does not, so the interval between
            // two sends would price a spell of silence at the cap and let a
            // single frame spend all of it.
            //
            // Real seconds, to match the instants the displacement is measured
            // between; and the frame's whole delta, however long the frame was,
            // so a hitch does not throttle an owner that legitimately travelled
            // through it.
            _sendableSecondsSinceSent += Time.unscaledDeltaTime;

            // ── Transform send ────────────────────────────────────────────────
            // A changed pose is broadcast at most once per simulation tick, not
            // once per visual frame: the tick engine coalesces owner state to a
            // single sample per 30 Hz tick, so a per-frame send only spends the
            // per-session state budget on poses the server discards — and at high
            // frame rates overruns that budget, dropping the surplus as jitter.
            // An unchanged pose still emits a low-rate keepalive so the object
            // does not age out of the tick engine's state set; otherwise a late
            // joiner sees no pose for a motionless object until it next moves.
            // Both paths share the LocalTick cursor, so a keepalive re-bases the
            // change cadence too.
            var manager = NetworkManager.Instance;
            if (manager != null)
            {
                bool transformChanged =
                    HasPositionChanged || HasRotationChanged || HasScaleChanged;
                bool sendDue;
                if (transformChanged)
                {
                    sendDue = TransformBroadcastCadence.TickAdvanced(
                        _hasLastTransformSendTick, manager.LocalTick, _lastTransformSendTick);
                }
                else
                {
                    // Idle: keepalive on the tick cadence OR a wall-clock floor.
                    // The floor covers the case where the tick cursor lags real
                    // time (capped sim catch-up dropping surplus after a hitch or
                    // under a sub-tick frame rate), which would otherwise stretch
                    // the tick-based keepalive past the server's wall-clock stale
                    // timeout and evict this present owner's motionless object.
                    sendDue =
                        TransformBroadcastCadence.KeepaliveDue(
                            _hasLastTransformSendTick, manager.LocalTick,
                            _lastTransformSendTick, TransformKeepaliveTicks)
                        || TransformBroadcastCadence.KeepaliveDueWallClock(
                            _hasLastTransformSendTick,
                            Time.unscaledTimeAsDouble,
                            _lastTransformSendUnscaledTime,
                            TransformKeepaliveSeconds);
                }
                if (sendDue)
                {
                    _lastTransformSendTick         = manager.LocalTick;
                    _hasLastTransformSendTick      = true;
                    _lastTransformSendUnscaledTime = Time.unscaledTimeAsDouble;
                    // Re-arm the change-detection baseline, and only that.  The
                    // broadcast reads the transform and never writes it, so the
                    // two are order-independent for change detection.  The
                    // tick-alignment anchor is not: it is established by each
                    // send and consumed by the next, so any call clearing it in
                    // this loop leaves it false at every read.  MarkClean clears
                    // it by design for its other callers.
                    MarkChangeDetectionBaseline();
                    SendTransformUpdate();
                }
            }

            // ── Reconciliation lerp ───────────────────────────────────────────
            // True linear interpolation from the captured start pose to the
            // server target, parameterised by elapsed wall-clock time over
            // ReconcileDuration.  Framerate-independent at 30 / 60 / 120 / 144 fps
            // because the same elapsed value produces the same blend factor.
            //
           // Blend BOTH position and rotation so a partial mid-air rotation
            // correction does not get left behind when the position lerp
            // completes first.
            if (_reconcileTimeLeft > 0f)
            {
                // The blend assigns transform.position outright, so every metre
                // it covers belongs to the server's correction and none of it to
                // the owner's travel — the same property the snap and passive
                // paths re-base for, arriving one frame at a time rather than all
                // at once.  Carrying the velocity cap's baseline the identical
                // step leaves the cap measuring only what the owner adds on top,
                // which across the blend is nothing.
                Vector3 preBlendPosition = transform.position;

                _reconcileTimeLeft -= Time.deltaTime;
                float elapsed = ReconcileDuration - _reconcileTimeLeft;
                float t       = Mathf.Clamp01(elapsed / ReconcileDuration);

                if (_syncPosition)
                {
                    transform.position = Vector3.Lerp(
                        _reconcileStart,
                        _reconciledTarget,
                        t);
                }
                if (_syncRotation)
                {
                    transform.rotation = Quaternion.Slerp(
                        _reconcileStartRotation,
                        _reconciledRotationTarget,
                        t);
                }
                if (_reconcileTimeLeft <= 0f)
                {
                    // Snap to exact target on completion, then refresh baseline
                    // so the next frame does not echo the corrected state back.
                    if (_syncPosition) transform.position = _reconciledTarget;
                    if (_syncRotation) transform.rotation = _reconciledRotationTarget;
                    _reconcileTimeLeft = 0f;
                    MarkClean();
                }

                // After the completion snap as well as the per-frame step: both
                // move the pose, and the frame that finishes the blend is the one
                // carrying its last fraction.
                CarryVelocityBaseline(transform.position - preBlendPosition);
            }
        }

        // ── Private helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Re-apply every unacknowledged input strictly after
        /// <paramref name="confirmedInputTick"/> on top of the current
        /// (just-snapped) transform.  Inputs are replayed in oldest-first
        /// order so the resulting pose is the same one the predicted
        /// simulation produced before reconciliation arrived, modulo the
        /// server's correction at the confirmed tick.
        /// </summary>
        private void ReplayUnackedInputs(uint confirmedInputTick)
        {
            int n = _inputBuffer.CopyUnacknowledgedAfter(confirmedInputTick, _replayScratch);
            if (n == 0) return;

            // Use the same fixed simulation step that GatherInput / SendInputBatch
            // observe so replay produces a pose identical to the original
            // prediction's deterministic step.  That step is this client's own
            // cadence — NetworkManager.FixedTickInterval, resolved from
            // NetworkSettings.tickRate — and deliberately not the server's
            // broadcast rate: replay re-runs local prediction, which advanced on
            // the local clock.  When no manager is reachable (edit-mode tests) the
            // literal falls back to the default cadence transparently.
            const float DefaultFixedDt = 1f / 30f;
            var nm = NetworkManager.Instance;
            float dt = nm != null ? nm.FixedTickInterval : DefaultFixedDt;

            for (int i = 0; i < n; i++)
                ReplayInput(_replayScratch[i], dt);
        }

        private void SendTransformUpdate()
        {
            var manager = NetworkManager.Instance;
            if (manager == null) return;

            var state    = GetState();
            var settings = manager.Settings;

            // ── Sender finiteness gate ───────────────────────────────────
            // Reject a broadcast whose own components carry NaN/Inf BEFORE
            // they leave this client.  The peer-side parser already rejects
            // the same on receive, but a partially-corrupted local state
            // (e.g. physics-engine glitch on the owner) should not cost
            // bandwidth or surface as a peer-side anomaly when it can be
            // localised to the originator.  Skipping the send keeps the
            // last-known-good position broadcast as the peer baseline until
            // the owner's local state recovers.
            if (!IsFiniteState(state))
                return;

            // ── Tick-boundary alignment ───────────────────────────────────
            // Carry the sample back from the frame instant to the tick it is
            // labelled with, so a receiver replaying it at tick x tickInterval
            // sees the pose the owner actually held there.  The raw sample is
            // retained as the next correction's far end before the pose is
            // rewritten, and the correction runs BEFORE the velocity cap below
            // so the clamp still governs the value that reaches the wire.
            //
            // `poseInstant` is the instant the broadcast position belongs to,
            // which is the frame instant only while that position is the raw
            // sample.  The correction moves the sample backwards in time and
            // this moves with it, because the velocity cap divides a
            // displacement by the interval its two endpoints actually span —
            // and the residual is not constant, so a frame-instant denominator
            // disagrees with the pose it measures by a different amount on
            // every send.
            double sampleTime  = Time.unscaledTimeAsDouble;
            double poseInstant = sampleTime;
            if (_tickAlignedSampling)
            {
                // The residual is game time — the tick driver charges its
                // accumulator with Time.deltaTime — while the instants it is
                // measured against are real time.  Dividing by the scale
                // restores the reach-back to the units of the clock it is
                // subtracted from; at unit scale this is exact and free.
                //
                // At a scale of zero the quotient is not finite, and the
                // correction refuses it: the accumulator is frozen while the
                // clock is not, so there is no boundary to reach back to.
                double residual = manager.SubTickResidualSeconds / Time.timeScale;
                if (_hasAlignSample
                    && TransformTickAlignment.TryResolveFraction(
                           _alignSampleTime, sampleTime, residual, out float alignFraction))
                {
                    var rawPosition = state.Position;
                    var rawRotation = state.Rotation;
                    // The instant follows the position, and only the position:
                    // it dates the velocity cap's endpoints, and with position
                    // sync off the broadcast position is the raw sample however
                    // the rotation was corrected.
                    if (_syncPosition)
                    {
                        state.Position = Vector3.Lerp(_alignSamplePosition, rawPosition, alignFraction);
                        poseInstant    = sampleTime - residual;
                    }
                    if (_syncRotation)
                        state.Rotation = Quaternion.Slerp(_alignSampleRotation, rawRotation, alignFraction);
                    _alignSamplePosition = rawPosition;
                    _alignSampleRotation = rawRotation;
                }
                else
                {
                    _alignSamplePosition = state.Position;
                    _alignSampleRotation = state.Rotation;
                }
                _alignSampleTime = sampleTime;
                _hasAlignSample  = true;
            }
            else
            {
                // The anchor is the previous sample and the instant it was
                // taken at, and only a send under the correction establishes
                // one.  Sends made while the correction is off do not, so an
                // anchor kept across an off period no longer names the previous
                // sample: it names some older one, and the reach-back it admits
                // is bounded by that stale span rather than by this send's.
                // Turning the correction back on would then date a pose behind
                // the last one broadcast, and the cap divides by the interval
                // between the two.
                _hasAlignSample = false;
            }

            // ── Owner velocity cap ────────────────────────────────────────
            // Clamp the broadcast position when the apparent per-second
            // velocity exceeds the project-wide cap.  This is a client-side
            // anti-cheat scaffold; gateway-side reconciliation will refine
            // the policy in a future iteration.  The first send after spawn is
            // unclamped because no earlier broadcast exists to measure a
            // velocity against.  A teleport does not unclamp anything: it moves
            // the baseline to the destination, so the jump falls outside the
            // interval and the travel after it does not.
            if (_syncPosition && settings != null && settings.maxOwnerVelocityMetersPerSecond > 0f)
            {
                state.Position = ClampOwnerVelocity(state.Position, poseInstant);
            }

            // ── Pooled transform send path (GC Round 2, 2026-05-02) ─────
            // Rent the maximum possible size (full-precision + input tick =
            // 52 B); the quantized builder writes 29 B into the same buffer
            // when enabled.  ArrayPool.Rent may return a buffer larger than the
            // requested size, so we always pass the *exact* written length
            // (PAYLOAD_SIZE_WITH_TICK or QUANTIZED_PAYLOAD_SIZE_WITH_TICK) to
            // SendStateSync so the wire frame's payload_len matches the bytes we
            // actually wrote.  Renting always at the larger size keeps the rent
            // path single-bucket and lets the quantized fallback path reuse the
            // same buffer without a second rent.
            //
            // The send target is PacketType.StateSync (0x40), not
            // PacketType.Data (0x10): the gateway routes StateSync through
            // the NATS state-forward path so the Sync Service can ingest the
            // transform into the authoritative tick engine and broadcast it
            // to every peer in the room.  PacketType.Data was the legacy
            // wiring; the gateway echoes Data packets back to the sender, so
            // a transform sent under that type would never reach other
            // clients and would produce a self-feedback loop on the owner.
            //
            // SDKS-01: the current LocalTick is the client prediction tick that
            // produced this pose; it rides on the uplink so the server can echo
            // it back on the object's StateDelta as the reconciliation
            // watermark.  Trailing-field layout keeps the 48/25-byte prefixes
            // byte-identical, and the gateway accepts both the extended and
            // legacy lengths.
            uint inputTick = manager.LocalTick;
            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(TransformPacketBuilder.PAYLOAD_SIZE_WITH_TICK);
            try
            {
                int written = 0;
                if (settings != null && settings.quantizeTransforms)
                {
                    written = TransformPacketBuilder.BuildQuantizedUpdatePayloadInto(
                        buffer, 0, NetworkObjectId, state, inputTick);
                    // Quantized builder returns 0 on a degenerate / non-finite
                    // input.  Fall back to the full-precision encoder so the
                    // peer still receives a coherent update; the legacy
                    // decoder rejects NaN/Inf at parse time.
                }
                if (written == 0)
                {
                    written = TransformPacketBuilder.BuildUpdatePayloadInto(
                        buffer, 0, NetworkObjectId, state, inputTick);
                }

                manager.SendStateSync(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }

            _lastSentPosition         = state.Position;
            _lastSentTimeUnscaled     = poseInstant;
            _lastSentReachBack        = sampleTime - poseInstant;
            _hasLastSent              = true;
            _sendableSecondsSinceSent = 0.0;
        }

        // Owner-velocity cap state.  Initialised on first send (via
        // OnNetworkSpawn or the first SendTransformUpdate); carried to a new
        // pose by OwnerTeleportTo and by a server correction, both of which move
        // the object somewhere it did not travel to.
        //
        // The pair is the last position broadcast and the instant that
        // broadcast was dated to — not necessarily a pose the owner held, since
        // a clamped send stores the truncated point that went on the wire.  The
        // two are written together because the cap's whole arithmetic is one
        // divided by the other: a position carried back by the correction and
        // an instant read from the clock describe a velocity nothing travelled
        // at.  Under tick-aligned sampling that instant is a tick boundary,
        // which is earlier than the frame that observed it.
        //
        // Successive instants are ordered by construction — the correction is
        // refused unless its reach-back stays inside the current sample pair,
        // and the anchor it reaches for is dropped whenever the correction is
        // not running — so the interval is positive on every path that produces
        // it.  The clamp still tests: it is the last statement standing between
        // an unordered pair and an unbounded displacement, and the cost of
        // reaching it is one unclamped send rather than a frozen owner.
        private Vector3 _lastSentPosition;
        private double  _lastSentTimeUnscaled;
        private bool    _hasLastSent;

        // Seconds this component has been able to broadcast since it last did.
        // Charged by Update — which runs only for a spawned owner — and cleared
        // by a send, so it counts the time the cap's budget is owed for and no
        // other.  Never re-based: a discontinuity moves where the owner is, not
        // how long it has been entitled to move.
        private double  _sendableSecondsSinceSent;

        // How far the last broadcast's pose was dated back from the frame that
        // sampled it — zero unless the tick-boundary correction applied.  Held
        // because the live clock above counts frames while the cap measures
        // between poses, and this is exactly what separates the two.
        private double  _lastSentReachBack;

        /// <summary>
        /// Hold <paramref name="candidate"/> to the configured owner speed cap,
        /// measured against the previous broadcast, and return the point that
        /// may go on the wire.
        /// </summary>
        /// <param name="candidateInstant">
        /// The instant <paramref name="candidate"/> is the owner's position at,
        /// which the caller resolves alongside the position itself.  It is not
        /// read from the clock here: under tick-aligned sampling the broadcast
        /// position is dated to the tick boundary rather than to the frame, and
        /// a velocity taken from one endpoint's instant and the other's position
        /// describes no motion the owner made.
        /// </param>
        private Vector3 ClampOwnerVelocity(Vector3 candidate, double candidateInstant)
        {
            // First send: capture baseline, skip the check (no prior sample
            // means no velocity can be derived).
            if (!_hasLastSent) return candidate;

            var manager  = NetworkManager.Instance;
            var settings = manager?.Settings;
            float cap = settings != null ? settings.maxOwnerVelocityMetersPerSecond : 0f;
            if (cap <= 0f) return candidate;

            float dt = (float)(candidateInstant - _lastSentTimeUnscaled);

            // Bound the interval by the time this component actually spent able
            // to broadcast.  A component that stops broadcasting — disabled,
            // unowned, despawned — keeps accruing wall-clock it did not spend,
            // and the cap would price that silence at its full rate and let one
            // frame spend all of it: thirty seconds of it bought nine hundred
            // times the cap before this.
            //
            // The two are measured between different things, and the difference
            // is not slack to be guessed at.  Live time spans frame instants;
            // this interval spans the instants the two poses belong to, and
            // under tick-aligned sampling the earlier pose was dated back by
            // that send's reach-back.  Adding it back is the exact bound —
            // `dt = live - reachBackNow + reachBackThen <= live + reachBackThen`
            // — and it collapses to the live time when the correction is off,
            // where every reach-back is zero.
            //
            // A long frame is paid for in full: it is live time, and an owner
            // that legitimately travelled through a hitch is not throttled for
            // having done so.
            double liveInterval = _sendableSecondsSinceSent + _lastSentReachBack;
            if (dt > liveInterval) dt = (float)liveInterval;

            // No interval means no distance, not any distance.  The only way to
            // reach this is a baseline written this frame — a teleport or a
            // server correction — and there the baseline IS where the object
            // now is, so holding the broadcast to it costs a legitimate caller
            // nothing and denies a self-serve one an unmetered send every frame.
            if (dt <= 0f) return _lastSentPosition;

            Vector3 delta = candidate - _lastSentPosition;
            float distance = delta.magnitude;
            float maxDistance = cap * dt;
            if (distance <= maxDistance) return candidate;

            // Clamp by linear interpolation along the requested displacement.
            // A genuine teleport (respawn, scripted cinematic) must call
            // OwnerTeleportTo, which carries the baseline to the destination so
            // the jump is not measured as travel; without that escape hatch
            // legitimate level transitions would be visibly throttled.
            float t = distance > 0f ? maxDistance / distance : 0f;
            return _lastSentPosition + delta * t;
        }

        /// <summary>
        /// Move the owner to <paramref name="worldPosition"/> and carry the
        /// owner-velocity baseline with it, so the jump is not metered as
        /// travel.  Call this from gameplay code that legitimately repositions
        /// the owner (respawn, fast travel, scripted cinematic).  Does NOT
        /// itself send a packet — the next change-detection update emits the
        /// new pose.
        /// </summary>
        /// <remarks>
        /// The cap stays armed across the call: what is excused is the distance
        /// between the departure point and the destination, not the sends that
        /// follow.  Motion away from <paramref name="worldPosition"/> is metered
        /// from the instant of arrival, so a caller that teleports to where it
        /// already stands is excused a displacement of zero — which is what
        /// keeps a self-serve hatch from being a budget.
        /// </remarks>
        public void OwnerTeleportTo(Vector3 worldPosition)
        {
            // Reject teleport requests on objects this peer does not own.
            // The velocity-cap reset that follows is a legitimate escape hatch
            // for the owner (respawn, scripted travel) but a hostile path if
            // any peer can invoke it on any object — a remote player could
            // bypass the anti-cheat clamp on the local owner's transform.
            if (!IsOwner)
            {
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] NetworkTransform.OwnerTeleportTo on object {NetworkObjectId} " +
                    "ignored: caller does not own this object.  Teleport is owner-only " +
                    "by design; remote peers must use the standard reconciled position " +
                    "stream, not this fast path.");
                return;
            }
            transform.position = worldPosition;
            // Abandon any reconciliation blend still in flight.  The lerp writes
            // transform.position every frame from a start pose captured before
            // this call, so leaving it running walks the object back off the
            // destination over the next hundred milliseconds — and now that the
            // cap's baseline has moved with the teleport, every send during that
            // walk is metered backwards from the destination.
            _reconcileTimeLeft = 0f;
            RebaseVelocityBaseline(worldPosition);
            MarkClean();
        }

        /// <summary>
        /// Move the velocity cap's baseline onto a pose the owner arrived at
        /// without travelling there, and date it to now.
        /// </summary>
        /// <remarks>
        /// Re-basing is what excuses a discontinuity, and it excuses exactly the
        /// discontinuity it names: the jump itself is outside the next interval
        /// because the interval now starts at the destination, while ordinary
        /// motion away from that destination stays governed.  Suspending the gate
        /// instead — clearing the baseline so the following send is unmeasured —
        /// excuses the jump and everything the owner does alongside it, and a
        /// caller that jumps nowhere at all collects the same excuse. That is why
        /// this leaves the gate armed.
        /// </remarks>
        private void RebaseVelocityBaseline(Vector3 position)
        {
            _lastSentPosition     = position;
            _lastSentTimeUnscaled = Time.unscaledTimeAsDouble;
            _lastSentReachBack    = 0.0;   // dated to the frame, not a boundary
            _hasLastSent          = true;
        }

        /// <summary>
        /// Displace the velocity cap's baseline by <paramref name="delta"/>,
        /// leaving the interval it is dated to untouched.
        /// </summary>
        /// <remarks>
        /// For a discontinuity the owner did not author but which is delivered
        /// gradually: the reconciliation blend spreads one server correction
        /// across <see cref="ReconcileDuration"/>, so the pose it hands the next
        /// broadcast is part correction and part whatever the owner was doing.
        /// Re-basing outright would date the baseline to this frame and hand back
        /// the interval the owner's own motion is measured over; shifting it by
        /// the step the blend just applied subtracts the correction and only the
        /// correction.
        ///
        /// The caller measures <paramref name="delta"/> across the blend's own
        /// statements, so a blend that writes no position — <c>_syncPosition</c>
        /// off — hands this a zero and needs no case of its own.  What does need
        /// one is a baseline that does not exist yet: before the first send there
        /// is nothing to displace, and creating one here would meter that send
        /// against a pose it never broadcast.
        /// </remarks>
        private void CarryVelocityBaseline(Vector3 delta)
        {
            if (!_hasLastSent) return;
            _lastSentPosition += delta;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>Test seam — exposes the velocity-clamp helper without an Update tick.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.</summary>
        /// <summary>
        /// Drive one frame of the owner send path. <b>For unit tests only.</b>
        ///
        /// <c>Update</c> is private, as Unity requires, so the owner broadcast
        /// path — change detection, the send cadence, the tick-alignment
        /// correction and the velocity cap — is otherwise reachable only from the
        /// Unity runtime, and assertions about it can only read its source.  This
        /// seam lets a fixture execute it.
        /// </summary>
        internal void InvokeUpdateForTest() => Update();

        /// <summary>Test seam — clamps a candidate dated to the current frame,
        /// the instant the send path resolves for an uncorrected sample.</summary>
        internal Vector3 ClampOwnerVelocityForTest(Vector3 candidate) =>
            ClampOwnerVelocity(candidate, Time.unscaledTimeAsDouble);

        /// <summary>Test seam — primes the velocity-cap baseline as though a
        /// send had just happened, which is what a send leaves behind: the pose,
        /// its instant, and no time yet accrued toward the next budget.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.</summary>
        internal void PrimeVelocityBaselineForTest(Vector3 position, double timeUnscaled)
        {
            _lastSentPosition         = position;
            _lastSentTimeUnscaled     = timeUnscaled;
            _lastSentReachBack        = 0.0;
            _hasLastSent              = true;
            _sendableSecondsSinceSent = 0.0;
        }

        /// <summary>
        /// Test seam — declare that this component was live and able to
        /// broadcast for <paramref name="seconds"/>, which <c>Update</c> is what
        /// normally records.  A fixture driving the clamp through its seam
        /// rather than through <c>Update</c> has to say so: advancing the clock
        /// alone describes silence, and silence is not budget.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void AdvanceSendableTimeForTest(double seconds) =>
            _sendableSecondsSinceSent += seconds;

        /// <summary>
        /// Test seam — enables client-side prediction so the medium-error
        /// reconciliation path (lerp scheduling + <see cref="MarkClean"/>)
        /// can be exercised without the Unity Inspector.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void EnablePredictionForTest()
        {
            _enablePrediction = true;
        }

        /// <summary>
        /// Test seam — exposes the current value of <c>_reconcileTimeLeft</c>
        /// so tests can verify that a medium-error reconciliation actually
        /// scheduled a lerp (non-zero value) rather than snapping or no-oping.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal float ReconcileTimeLeftForTest => _reconcileTimeLeft;

        /// <summary>
        /// Test seam — pushes a synthetic input stamped with <paramref name="tick"/>
        /// into the CSP input buffer so tests can verify that reconciliation
        /// trims it against a server-supplied confirmed tick (SDKS-01).
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void PushInputForTest(uint tick) =>
            _inputBuffer.Push(new RTMPE.Core.InputPayload { Tick = tick });

        /// <summary>
        /// Test seam — number of unacknowledged inputs still buffered.  Used to
        /// assert the reconciliation watermark trimmed exactly the confirmed
        /// prefix and left the in-flight tail intact.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal int UnackedInputCountForTest => _inputBuffer.Count;
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Phase 2.x (2026-04-25) — server-authoritative input send.
        ///
       /// Snapshots the unacknowledged input ring buffer into the
        /// pre-allocated scratch array, builds a 0x43 batch payload, and
        /// hands it to <see cref="NetworkManager.SendInput"/> for unreliable
        /// UDP transmission.  Called at most once per LocalTick from
        /// <see cref="Update"/>.
        ///
       /// Bandwidth: at 30 Hz with the default 64-entry buffer, one full
        /// batch is 2 + 13×64 = 834 bytes per object.  In steady state the
        /// server acknowledges within 2-3 ticks, so the typical batch holds
        /// 2-3 entries (~30-50 bytes).
        /// </summary>
        private void SendInputBatch()
        {
            var manager = NetworkManager.Instance;
            if (manager == null) return;

            int count = _inputBuffer.CopyUnacknowledgedTo(_inputSendScratch);
            if (count == 0) return;

            // Pooled input batch send path (GC Round 2, 2026-05-02).
            // Rent the exact size; pass the written length back so SendInput
            // wraps only the bytes we wrote.
            int size   = InputPacketBuilder.ComputeBatchPayloadSize(count);
            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(size);
            try
            {
                int written = InputPacketBuilder.BuildBatchPayloadInto(
                    buffer, 0, _inputSendScratch, count);
                manager.SendInput(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        // Componentwise IsFinite over the position, rotation, and scale of a
        // TransformState.  Used by SendTransformUpdate to quench broadcasts
        // produced from a corrupt local pose before they leave the
        // originator.  Quaternion.w is included because PhysX-derived
        // rotations may carry a non-unit quaternion that has a finite x/y/z
        // and a non-finite w after a divide-by-zero recovery.
        // .NET Standard 2.1 has float.IsFinite, but the SDK targets older
        // Unity runtimes where it is not always available.  Spelled out as
        // !IsNaN && !IsInfinity to match the same pattern used elsewhere
        // in this file (see ApplyReconciliation:280-283).
        private static bool IsFiniteState(TransformState s)
        {
            var p = s.Position;
            var r = s.Rotation;
            var c = s.Scale;
            return Finite(p.x) && Finite(p.y) && Finite(p.z)
                && Finite(r.x) && Finite(r.y) && Finite(r.z) && Finite(r.w)
                && Finite(c.x) && Finite(c.y) && Finite(c.z);
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
