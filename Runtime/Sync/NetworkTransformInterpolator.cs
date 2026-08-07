// RTMPE SDK — Runtime/Sync/NetworkTransformInterpolator.cs
//
// Buffered interpolation for smooth movement of non-owner networked objects.
//
// Design decisions:
//  • Extends MonoBehaviour so Update() fires automatically on the main thread.
//  • Owner-only suppression: AddState() and Update() are both valid on any
//    client, but by convention only non-owner clients call AddState().  The
//    owning client uses NetworkTransform.Update() to SEND; the interpolator
//    is wired to RECEIVE via NetworkManager.OnDataReceived.
//  • Timestamping uses Time.unscaledTimeAsDouble (a local real-time clock that
//    advances independently of Time.timeScale) at the moment of receipt — no
//    network time-synchronisation required at this stage, and a pause / slow-mo
//    cannot desync the render clock from the real-time server tick.
//    The render cursor is Time.unscaledTimeAsDouble - _interpolationDelay,
//    giving a stable 100 ms window in which to always find a from/to state pair.
//  • Timestamping uses Time.unscaledTimeAsDouble (local real-time clock) for
//    high-resolution, non-rewinding time values suitable for sub-frame math.
//  • AddState() accepts a typed TransformState snapshot so that position,
//    rotation, and scale are always transported together without extra copies.
//  • Division-by-zero is guarded when two states share the same timestamp
//    (packets decoded in the same frame): the from-state is returned as-is.
//  • Quaternion.Slerp is used directly for rotation; Unity already selects
//    the shortest arc internally.
//  • No per-frame heap allocations: internal storage is a pre-allocated List<T>
//    used as a ring buffer; Update() accesses elements by index.
//  • Settings are [SerializeField] fields on the component (Unity convention),
//    keeping Inspector integration straightforward.
//  • Default buffer size of 10 provides ~7 states of margin over the minimum
//    2-state requirement at 30 Hz + 100 ms delay (~720 B total).
//  • Scale interpolation is opt-in (_interpolateScale = false by default)
//    matching the NetworkTransform default of not syncing scale.
//
// Threading: all public methods and Update() run on the Unity main thread.
// TryInterpolate(double) is pure logic and testable without a Unity scene.

using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Buffers server-received <see cref="TransformState"/> snapshots and applies
    /// smooth interpolated movement to this <see cref="GameObject"/> each frame.
    ///
   /// Add to any networked prefab alongside <see cref="NetworkTransform"/>.
    /// Call <see cref="AddState"/> whenever a <c>StateDelta</c> is decoded for
    /// this object (typically wired to <c>NetworkManager.OnDataReceived</c>).
    /// </summary>
    /// <remarks>
    /// The <see cref="DefaultExecutionOrder"/> attribute pins this component
    /// to a high (late) execution slot so render-time consumers — Camera
    /// follows, animation IK, gameplay scripts that read transform.position —
    /// see the interpolated pose for the current frame.  Without an explicit
    /// order, Unity's registration-order tie-breaker would let any user
    /// script with a higher script-execution priority observe the previous
    /// frame's pose, producing a one-frame lag artefact that is hard to
    /// reproduce because it depends on import ordering.  The chosen value
    /// (10000) sits comfortably after default user scripts (0) and most
    /// animation systems while leaving headroom (max int32) for downstream
    /// post-processing components.
    /// </remarks>
    [AddComponentMenu("RTMPE/Network Transform Interpolator")]
    [DefaultExecutionOrder(10000)]
    public class NetworkTransformInterpolator : MonoBehaviour
    {
        // ── Inspector configuration ────────────────────────────────────────────

        [Header("Interpolation Settings")]
        [Tooltip("Maximum number of states to keep in the delay buffer.  " +
                 "At 30 Hz with 100 ms delay, ~3 states are in the window; " +
                 "10 adds margin for jitter without meaningful memory cost.")]
        [SerializeField] [Range(2, 64)] private int _bufferSize = 10;

        [Tooltip("How far behind real-time (seconds) to render, ensuring there is always " +
                 "a 'from' and 'to' state available for interpolation.")]
        [SerializeField] [Range(0.067f, 0.5f)] private float _interpolationDelay = 0.1f;

        [Tooltip("Adapt the render delay to the measured arrival spread instead of holding the " +
                 "fixed value above.  When enabled the delay floats between the two-tick floor " +
                 "and the value above — a clean link renders closer to real time (less latency), " +
                 "a jittery one keeps more buffer.  Disabled by default: the delay stays exactly " +
                 "as configured unless opted in.")]
        [SerializeField] private bool _adaptiveDelay = false;

        [Tooltip("Build the render timeline from the owning client's input tick rather than the " +
                 "server's broadcast tick.  The server tick is a uniform emission counter whose " +
                 "per-tick sample instant shifts with server load; the owner input tick binds each " +
                 "pose to the tick of the client that produced it, so remote motion is replayed on " +
                 "its originating timeline and is unaffected by the server's sampling cadence.  The " +
                 "input tick is client-supplied, so a peer can distort only the timing of its own " +
                 "object's motion — never another's — and the monotonicity, future-skew, and " +
                 "offset-step guards on the sender-tick path bound even that.  Disabled by default: " +
                 "the server tick remains the timeline unless opted in.")]
        [SerializeField] private bool _ownerTickTimeline = false;

        /// <summary>
        /// The receiving half of remote motion timing, as configured on this
        /// component.  Read by the receive dispatch so a half-enabled pairing —
        /// this on while the owner's <c>TickAlignedSampling</c> is off, or the
        /// reverse — is surfaced rather than silently doing nothing; see
        /// <see cref="RTMPE.Core.Diagnostics.RemoteMotionTimingAdvisory"/>.
        /// Deliberately a plain field read with no lock: <c>bool</c> reads are
        /// atomic, and this is the same access the render path already makes.
        /// </summary>
        internal bool OwnerTickTimeline => _ownerTickTimeline;

        [Tooltip("Maximum time (seconds) to EXTRAPOLATE the last known velocity when the " +
                 "render cursor runs past the newest buffered snapshot — i.e. a packet is " +
                 "late or lost.  Instead of freezing at the last pose and snapping forward " +
                 "when the next snapshot lands (the visible 'stutter' on a jittery link), the " +
                 "object continues along its last measured velocity for up to this long, then " +
                 "holds.  The prediction is bounded by the same speed ceiling as inbound " +
                 "snapshots, so a mis-predicted burst cannot fling the object away.  Keep it " +
                 "small — roughly a few snapshot intervals — so a wrong guess self-corrects " +
                 "within a couple of frames when real data resumes.  Set to 0 to disable " +
                 "extrapolation and preserve the freeze-then-resume behaviour.")]
        [SerializeField] [Range(0f, 0.5f)] private float _maxExtrapolationSeconds = 0.15f;

        [Tooltip("Also interpolate local-space scale.  Disabled by default to match " +
                 "NetworkTransform._syncScale = false.")]
        [SerializeField] private bool _interpolateScale = false;

        [Tooltip("Speed ceiling (world units / second) for an inbound non-owner " +
                 "snapshot.  A snapshot whose position jumps further than this " +
                 "ceiling allows over the interval since the previous snapshot " +
                 "is pulled back along the line to the previous position, so a " +
                 "peer streaming teleported coordinates is walked toward the " +
                 "claimed point at the ceiling instead of snapping there on " +
                 "every observer's screen.  The 50 m/s default covers the " +
                 "ceiling for typical FPS / battle-royale / MMO movement " +
                 "(roughly Bolt's 12 m/s sprint plus generous head-room for " +
                 "vehicles, jump-pads, and grappling-style traversal); raise " +
                 "the value for racing or aerial-vehicle games whose remote " +
                 "objects legitimately exceed it, or set it to 0 to disable " +
                 "the gate entirely.")]
        [SerializeField] private float _maxInterpolatedSpeed = 50f;

        [Tooltip("Maximum accepted skew (seconds) into the future relative to " +
                 "the local clock.  Defends the interpolator against a hostile " +
                 "or buggy sender that puts double.MaxValue (or any far-future " +
                 "value) into the timestamp field — such a payload would " +
                 "otherwise permanently freeze the buffer because no subsequent " +
                 "timestamp could ever be greater.  10 seconds of forward skew " +
                 "comfortably absorbs every realistic clock drift while still " +
                 "rejecting double.MaxValue and similarly-absurd injections. " +
                 "Expressed as a relative offset rather than an absolute wall " +
                 "so persistent-world / social-VR sessions whose Time.unscaledTimeAsDouble " +
                 "exceeds 24h continue to interpolate normally.")]
        [SerializeField] private double _maxFutureSkewSeconds = 10.0;

        // ── Buffer state ───────────────────────────────────────────────────────

        /// <summary>
        /// One timestamped snapshot in the delay buffer.
        /// Timestamp is the local <c>Time.unscaledTimeAsDouble</c> at receive time.
        /// </summary>
        private struct TimestampedState
        {
            public double         Timestamp;
            public TransformState State;
        }

        // Ring-buffer storage: _head is the logical index of the oldest valid entry.
        // Entries are overwritten in-place once the buffer is full, giving O(1)
        // insertions regardless of buffer size.
        private readonly List<TimestampedState> _buffer = new List<TimestampedState>();
        private int _head; // index of the oldest valid entry (logical index 0)

        // Tracks the largest timestamp ever accepted. States with an equal or
        // smaller timestamp are discarded to maintain chronological buffer order
        // despite out-of-order UDP delivery or duplicate packets.
        private double _latestTimestamp = double.MinValue;

        // Position and receive-domain timestamp of the most recently buffered
        // snapshot, retained as the reference point for the per-update
        // displacement gate (see GateMotion / RemoteMotionGate).  _hasGateState
        // is false until the first snapshot is buffered, because the first
        // snapshot has no predecessor to measure a step against.
        private Vector3 _lastGatePosition;
        private double  _lastGateTimestamp;
        private bool    _hasGateState;

        // Minimum permitted per-update displacement (world units), independent
        // of the elapsed interval.  Absorbs transform-quantization noise and
        // the degenerate two-snapshots-one-timestamp case so the gate never
        // clamps a legitimately stationary or near-stationary object.
        private const float MotionGateStepFloorUnits = 0.5f;

        // Upper bound on the inter-snapshot interval that feeds the
        // displacement budget.  Beyond a gap this long the object's true
        // position cannot be recovered by interpolation regardless; capping
        // the interval here stops a peer from withholding snapshots to bank
        // an arbitrarily large budget and then cashing it in for one jump.
        private const double MotionGateMaxIntervalSeconds = 0.5;

        // ── Sender-clock alignment ─────────────────────────────────────────────
        //
        // The receiver-clock AddState path (above) timestamps each snapshot at
        // the moment the packet leaves the network thread.  Under jitter that
        // collapses sender intervals: two snapshots produced 33 ms apart on
        // the server can land 5 ms apart on the receiver, and the interpolation
        // segment between them runs 6× faster than the underlying motion.
        //
        // The sender-tick AddState overload below converts the wire tick into
        // a sender-domain timestamp, then offsets it into receiver wall-clock
        // space using a low-pass filter on the (receiver_now - sender_time)
        // delta.  States separated by N sender ticks remain N × tickInterval
        // apart in the buffer regardless of network jitter.  This is the same
        // pattern Quake3 / Source / Overwatch use for their snapshot streams.
        //
        // The offset is a single double accumulator updated as an exponential
        // moving average: offset := offset + alpha * (sample - offset).  Alpha
        // is chosen so the filter has a ~1 s time constant at 30 Hz (alpha ≈
        // 0.033), which absorbs single-packet jitter without lagging through a
        // genuine clock skew that develops over seconds.
        private double _clockOffset;        // receiver_now - sender_time, EMA
        private bool   _hasClockOffset;     // false until the first tick sample
        private const double ClockOffsetAlpha = 1.0 / 30.0;

        // The widest a single packet may move the clock offset.  Set far above
        // realistic jitter and path-change steps and far below a straggler's
        // lateness, so it caps only a pathological single packet without shifting
        // the steady-state estimate or slowing adaptation to a genuine skew.
        private const double MaxClockOffsetStepSeconds = 0.25;

        // Adaptive-delay inputs: a slow mean-absolute-deviation estimate of the
        // per-packet arrival spread, and the margin (in deviations) the render
        // delay holds above the floor to cover it.  The gain matches the
        // clock-offset filter's ~1 s time constant, so a lone straggler nudges
        // the delay rather than jumping it.
        private const double JitterEmaAlpha     = 1.0 / 30.0;
        private const double JitterMarginFactor = 3.0;
        private double _arrivalJitterEma;
        private bool   _hasArrivalJitter;

        // Highest sender tick observed, for wrap-safe out-of-order rejection.
        // Mirrors the modular arithmetic used by InputBuffer / NetworkVariable
        // so the whole SDK observes one wrap discipline.
        private uint _latestSenderTick;
        private bool _hasSenderTick;

        // The timeline mode the estimator state above was built from.  A change
        // means the high-water and offset describe a tick space that no longer
        // applies, so they are retired rather than compared across domains.
        private bool _timelineModeLatched;

        // Cursor hint for the bracketing-pair search in TryInterpolate.
        // Remote timestamps are monotonic by construction (AddState rejects
        // out-of-order writes; AddStateFromSenderTick filters via wrap-safe
        // sender-tick gating) and the per-frame render time advances
        // monotonically with Time.unscaledTimeAsDouble — together this makes the
        // search amortised O(1) via a logical-index cursor that only ever
        // advances forward.  Without the cursor the inner loop walks every
        // sample under the lock on every Update; at 5 000 networked objects
        // and a 64-cap ring this is hundreds of thousands of lock-protected
        // index reads per frame.
        //
        // _bracketCursor is a LOGICAL index relative to _head (i.e. 0 = oldest
        // valid entry).  It is reset to 0 whenever the buffer is cleared, the
        // ring head wraps past it (overwrite of the slot it points at), or an
        // out-of-order sample lands at or below the cursor's left edge — any
        // of which would otherwise let the search return a stale pair.
        private int _bracketCursor;
        // Tracks whether a Configure / Clear has invalidated the cursor since
        // the last successful search; used to skip the optimistic "start from
        // _bracketCursor" path until the buffer is repopulated.
        private bool _bracketCursorValid;

        // Lock guarding all ring-buffer mutations and reads (_buffer, _head,
        // _latestTimestamp).  The SDK convention routes packet callbacks through
        // MainThreadDispatcher, so in practice AddState() runs on the Unity main
        // thread; but the PUBLIC API allows any caller, and Update() also reads
        // on the main thread — a misbehaving integration (e.g. custom transport
        // forgetting to marshal) would otherwise corrupt the ring buffer with no
        // diagnostic.  The lock is uncontended in the common case (same thread
        // always), so overhead is one interlocked CAS per call (~20 ns).
        private readonly object _syncRoot = new object();

        // ── Properties (test-visible) ──────────────────────────────────────────

        /// <summary>
        /// Number of states currently held in the delay buffer.
        /// Useful for Inspector debug display and unit tests.
        /// </summary>
        public int BufferCount
        {
            // Read under the same lock as AddState/TryInterpolate so the
            // returned count is coherent with the internal state (not a
            // racing mid-write List.Count that briefly observes the wrong
            // value during Add/Clear).
            get { lock (_syncRoot) return _buffer.Count; }
        }

        /// <summary>
        /// The configured interpolation delay in seconds.
        /// Read-only after construction; set via Inspector or
        /// <see cref="ConfigureForTest"/> (test use only).
        /// </summary>
        public float InterpolationDelaySeconds => EffectiveInterpolationDelay();

        // Two 30 Hz tick periods.  Below this the render cursor (now − delay) sits
        // within a single tick of the newest buffered snapshot, so ordinary
        // arrival jitter carries it past that snapshot and the frame falls through
        // to extrapolation — a shimmer on an otherwise healthy link.  The inspector
        // Range keeps an authored value above the floor; this constant also governs
        // a value assigned from code or a prefab the Range predates.
        private const float MinInterpolationDelaySeconds = 2f / 30f;

        // Latches the single notice that a configured delay was raised to the floor.
        private bool _interpolationDelayFloored;

        // The configured delay, never below the two-tick floor.  A shorter value
        // is raised — and reported once — so a misconfiguration degrades to the
        // floor rather than into continuous render-cursor overrun.
        private float EffectiveInterpolationDelay()
        {
            // The configured value is the ceiling; a value under the two-tick
            // floor is raised to it and the override reported once.
            float ceiling = _interpolationDelay;
            if (ceiling < MinInterpolationDelaySeconds)
            {
                if (!_interpolationDelayFloored)
                {
                    _interpolationDelayFloored = true;
                    Debug.LogWarning(
                        "[RTMPE] NetworkTransformInterpolator '" + name + "': interpolation delay "
                        + _interpolationDelay + "s is below the two-tick floor of "
                        + MinInterpolationDelaySeconds + "s and was raised to it; a shorter delay "
                        + "leaves the render cursor without a stable snapshot pair.");
                }
                ceiling = MinInterpolationDelaySeconds;
            }

            if (!_adaptiveDelay || !_hasArrivalJitter)
                return ceiling;

            // Hold the cursor just far enough behind real time to cover the
            // measured arrival spread — never below the floor, never above the
            // configured ceiling.  A clean link collapses toward the floor (less
            // latency); a jittery one relaxes toward the ceiling.  The clamp also
            // makes a torn read of the estimate harmless.
            double target = MinInterpolationDelaySeconds + JitterMarginFactor * _arrivalJitterEma;
            if (target < MinInterpolationDelaySeconds) target = MinInterpolationDelaySeconds;
            if (target > ceiling) target = ceiling;
            return (float)target;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Enqueue a received server snapshot.
        /// Call this on the main thread when a <c>StateDelta</c> is decoded for
        /// this object ID.
        ///
       /// <paramref name="timestamp"/> should be <c>Time.unscaledTimeAsDouble</c> at the
        /// moment the packet was received so that the interpolation cursor
        /// (<c>Time.unscaledTimeAsDouble - _interpolationDelay</c>) can locate a
        /// surrounding pair.
        /// </summary>
        /// <param name="state">Decoded transform snapshot.</param>
        /// <param name="timestamp">
        /// Local monotonic receive time (<c>Time.unscaledTimeAsDouble</c>).
        /// </param>
        public void AddState(TransformState state, double timestamp)
        {
            // Reject non-finite (NaN, +Inf, -Inf) and absurd far-future
            // timestamps at the entry point.  A double.MaxValue payload would
            // otherwise lock the buffer permanently — any subsequent legitimate
            // timestamp would compare strictly less than _latestTimestamp and
            // be silently dropped.  The interpolator has its own ingress
            // independent of the InputPayload parser and needs the same gate.
            //
            // Skew expressed RELATIVE to the local clock
            // (Time.unscaledTimeAsDouble) — an unscaled real-time clock that
            // advances regardless of Time.timeScale, so a pause / slow-mo /
            // hitstop cannot desync the render clock from the real-time server
            // tick — not as an absolute wall.  The unscaled clock never resets
            // within a session, so an absolute 24-hour wall froze every
            // persistent-world / social-VR session past the first day of uptime.
            // 10 seconds of forward skew comfortably absorbs every realistic
            // clock drift while still rejecting double.MaxValue and similar
            // far-future injections.
            if (!double.IsFinite(timestamp)
                || timestamp - UnityEngine.Time.unscaledTimeAsDouble > _maxFutureSkewSeconds)
                return;

            // Reject non-finite components in the snapshot itself.  The
            // network parser rejects NaN/Inf positions on the receive thread,
            // but a caller that constructs a TransformState in user code (a
            // test harness, a custom dispatcher) can bypass that path —
            // and the downstream Vector3.Lerp / Quaternion.Slerp would then
            // propagate NaN into _lastInterpolatedPose and from there into
            // transform.position / transform.rotation, which Unity persists
            // unchecked.  Quenching at ingress keeps the buffer free of
            // poisoned entries and matches the snapshot's wire-format
            // contract.
            if (!IsFiniteSnapshot(state))
                return;

            lock (_syncRoot)
            {
                // Discard out-of-order and duplicate states — only strictly newer
                // timestamps advance the ring buffer.
                if (timestamp <= _latestTimestamp) return;

                // Bound an implausible position jump before the snapshot enters
                // the buffer: a peer streaming teleported coordinates is walked
                // toward the claimed position at the configured speed ceiling
                // rather than snapping there on every observer's screen.
                state = GateMotion(state, timestamp);

                _latestTimestamp = timestamp;

                // Fill the backing list to capacity on initial population, then
                // overwrite the oldest slot in-place — O(1) regardless of buffer size.
                if (_buffer.Count < _bufferSize)
                {
                    _buffer.Add(new TimestampedState { Timestamp = timestamp, State = state });
                    // _head stays 0 while filling; oldest is always index 0 during fill.
                }
                else
                {
                    // Overwrite the oldest slot and advance the ring head.
                    _buffer[_head] = new TimestampedState { Timestamp = timestamp, State = state };
                    _head = (_head + 1) % _bufferSize;
                    // The slot the cursor points at may have just been
                    // overwritten by the new write, and the entire logical
                    // window shifted left by one.  Pull the cursor in by one
                    // step (clamped at zero) so the next search resumes at a
                    // still-valid position rather than walking off the new
                    // oldest entry.
                    if (_bracketCursorValid)
                    {
                        _bracketCursor = _bracketCursor > 0 ? _bracketCursor - 1 : 0;
                    }
                }

                // Record the accepted (post-gate) position as the reference
                // point for the next snapshot's displacement check.
                _lastGatePosition  = state.Position;
                _lastGateTimestamp = timestamp;
                _hasGateState      = true;
            }
        }

        // Clamp the candidate snapshot's position against an implausible
        // per-update jump, measured from the most recently buffered snapshot.
        // A non-positive _maxInterpolatedSpeed disables the gate; the first
        // snapshot has no predecessor and is accepted verbatim.  The caller
        // holds _syncRoot.
        private TransformState GateMotion(TransformState candidate, double timestamp)
        {
            if (_maxInterpolatedSpeed <= 0f || !_hasGateState)
                return candidate;

            // Cap the interval feeding the displacement budget.  Without the
            // cap, a peer that withholds snapshots accrues an unbounded budget
            // (budget grows with the gap) and can then cash it in for a single
            // long jump; the cap bounds the budget to one MaxInterval window.
            double dt = timestamp - _lastGateTimestamp;
            if (dt > MotionGateMaxIntervalSeconds) dt = MotionGateMaxIntervalSeconds;

            candidate.Position = RemoteMotionGate.ClampPositionStep(
                _lastGatePosition,
                candidate.Position,
                dt,
                _maxInterpolatedSpeed,
                MotionGateStepFloorUnits);
            return candidate;
        }

        /// <summary>
        /// Enqueue a snapshot stamped by the sender's tick number.  The wire
        /// tick is mapped into receiver wall-clock space via an exponentially-
        /// smoothed clock-offset estimator so jitter on the receive path does
        /// not collapse the inter-snapshot interval the interpolator sees.
        ///
        /// <para>Out-of-order rejection uses 32-bit modular sequence-number
        /// arithmetic (RFC 1982 §3.2): a tick is accepted iff
        /// <c>(int)(senderTick - latestSenderTick) &gt; 0</c> after the first
        /// sample.  This is the same wrap discipline used by
        /// <c>InputBuffer</c> and <c>NetworkVariable</c> so a uint32 wrap that
        /// happens mid-session does not silently freeze the buffer.</para>
        ///
        /// <para><paramref name="receiverNow"/> should be
        /// <c>Time.unscaledTimeAsDouble</c> at receive time so the clock-offset
        /// estimator stays anchored to the same render-cursor clock that
        /// <see cref="Update"/> reads.</para>
        ///
        /// <para><paramref name="tickIntervalSeconds"/> is the period of the
        /// clock <paramref name="senderTick"/> is counted in, and must belong to
        /// the same domain as that tick: <see cref="ServerTickRate.IntervalSeconds"/>
        /// for a server broadcast tick, the owning client's cadence for an owner
        /// input tick. Supplying one domain's tick with the other's period scales
        /// the reconstructed timeline, which the clock-offset estimator cannot
        /// absorb — it corrects an offset, not a rate.</para>
        /// </summary>
        public void AddStateFromSenderTick(
            TransformState state,
            uint           senderTick,
            double         receiverNow,
            double         tickIntervalSeconds)
        {
            // Reject pathological tick interval values defensively — a zero
            // or negative interval would map every tick to the same sender
            // time, collapsing the buffer.  A non-finite value is also a
            // protocol bug we do not propagate.
            if (!double.IsFinite(receiverNow)
                || !double.IsFinite(tickIntervalSeconds)
                || tickIntervalSeconds <= 0.0)
                return;

            lock (_syncRoot)
            {
                // ── Wrap-safe out-of-order check ─────────────────────────────
                // Signed-difference comparison treats two unsigned values as
                // "near" on the 32-bit ring when the gap is < 2^31; any
                // realistic gameplay backlog (a few hundred ticks at most) is
                // orders of magnitude below that threshold.
                if (_hasSenderTick && (int)(senderTick - _latestSenderTick) <= 0)
                    return;

                // ── Sender-domain timestamp ──────────────────────────────────
                // Promote tick to double BEFORE multiplying so a tick close
                // to uint.MaxValue does not overflow during the conversion.
                double senderTime = (double)senderTick * tickIntervalSeconds;

                // ── Candidate clock-offset (not yet committed) ───────────────
                // Sample = receiver_now - sender_time.  The first sample is
                // adopted directly (no warm-up bias); subsequent samples are
                // low-pass filtered with ClockOffsetAlpha so a single jittery
                // packet does not yank the render cursor.  Compute the offset
                // this packet WOULD adopt without writing it, so a packet
                // rejected by the gates below leaves the EMA untouched.
                double sample = receiverNow - senderTime;
                double candidateOffset;
                if (_hasClockOffset)
                {
                    // Bound the pull one packet may exert on the offset.  A lone
                    // snapshot arriving a quarter-second past its tick would
                    // otherwise fold its whole lateness (scaled by the gain) into
                    // the render clock.  Ordinary jitter, and even an abrupt path
                    // change, move the sample far less, so the bound engages only
                    // on a pathological straggler and leaves the steady-state
                    // estimate identical to the plain moving average.
                    double delta = sample - _clockOffset;
                    if (delta > MaxClockOffsetStepSeconds) delta = MaxClockOffsetStepSeconds;
                    else if (delta < -MaxClockOffsetStepSeconds) delta = -MaxClockOffsetStepSeconds;
                    candidateOffset = _clockOffset + ClockOffsetAlpha * delta;
                }
                else
                {
                    candidateOffset = sample;
                }

                // Stored timestamp lives in the receiver wall-clock domain so
                // the existing TryInterpolate(renderTime) path — which reads
                // Time.unscaledTimeAsDouble - delay — needs no changes.
                double timestamp = senderTime + candidateOffset;

                // Validate BEFORE mutating any state.  A non-finite or
                // far-future timestamp, or a non-finite transform component,
                // must not advance the sender-tick high-water — doing so would
                // strand every later in-range tick as "stale" (line 404) — nor
                // poison the clock-offset EMA.  AddState applies the same
                // finiteness/skew (and NaN/Inf, line 284) guards on the
                // receiver-clock path; this alternate entry point mirrors them
                // to preserve the invariant that the buffer never holds NaN/Inf
                // components (SDKS-03).
                if (!double.IsFinite(timestamp)
                    || timestamp - UnityEngine.Time.unscaledTimeAsDouble > _maxFutureSkewSeconds)
                    return;
                if (!IsFiniteSnapshot(state))
                    return;

                // Packet accepted — commit the clock-offset EMA and the
                // high-water tick.  The tick is the authoritative ordering
                // signal on this path (the inlined buffer push below bypasses
                // AddState's per-timestamp monotonicity guard), so a duplicate
                // EMA-rounded timestamp cannot block a later tick-greater state.
                _clockOffset      = candidateOffset;
                _hasClockOffset   = true;
                // Arrival-spread estimate: how far this packet's implied offset
                // sits from the smoothed offset, low-pass filtered.  Feeds the
                // adaptive render delay; committed only on an accepted packet, so
                // a rejected one leaves it untouched exactly as the offset above.
                double deviation = sample - candidateOffset;
                if (deviation < 0.0) deviation = -deviation;
                _arrivalJitterEma = _hasArrivalJitter
                    ? _arrivalJitterEma + JitterEmaAlpha * (deviation - _arrivalJitterEma)
                    : deviation;
                _hasArrivalJitter = true;
                _latestSenderTick = senderTick;
                _hasSenderTick    = true;

                // Bound an implausible position jump before the snapshot
                // enters the buffer — identical displacement gate as the
                // receiver-clock AddState path.
                state = GateMotion(state, timestamp);

                // Track the highest stored timestamp so a later receiver-clock
                // AddState() call (mixed-mode integration) cannot insert an
                // older state in front of the sender-tick ordering.
                if (timestamp > _latestTimestamp) _latestTimestamp = timestamp;

                if (_buffer.Count < _bufferSize)
                {
                    _buffer.Add(new TimestampedState { Timestamp = timestamp, State = state });
                }
                else
                {
                    _buffer[_head] = new TimestampedState { Timestamp = timestamp, State = state };
                    _head = (_head + 1) % _bufferSize;
                    if (_bracketCursorValid)
                    {
                        _bracketCursor = _bracketCursor > 0 ? _bracketCursor - 1 : 0;
                    }
                }

                // Record the accepted (post-gate) position as the reference
                // point for the next snapshot's displacement check.
                _lastGatePosition  = state.Position;
                _lastGateTimestamp = timestamp;
                _hasGateState      = true;
            }
        }

        /// <summary>
        /// Enqueue a broadcast snapshot, choosing the render timeline from the
        /// ticks the record carries.  The server broadcast tick is used by
        /// default; when owner-tick timelining is enabled and the owner input
        /// tick is present, that tick drives the timeline instead, replaying the
        /// motion on the timeline of the client that produced it rather than the
        /// server's emission cadence.  A record whose selected tick is absent
        /// falls back to the receiver-clock path.
        /// </summary>
        /// <remarks>
        /// The two tick domains — the server emission counter and the owner input
        /// tick — are never mixed: the selector routes to at most one of them, and
        /// otherwise to the receiver clock, so the sender-tick high-water and
        /// clock-offset estimate always compare like with like.  The domain's
        /// period travels with its tick: a server tick is converted with
        /// <see cref="ServerTickRate.IntervalSeconds"/>, which is why this method
        /// takes only the owner's cadence — the server's rate is a property of the
        /// server and is never sourced from a client setting.  Switching the
        /// mode while snapshots are streaming (toggling the field in the Inspector
        /// during play) retires the estimator state built from the previous
        /// domain; the two tick spaces are unrelated numbers, so carrying a
        /// high-water across the switch would reject every snapshot whose new-domain
        /// tick happens to sit below it.  The buffered snapshots survive the switch
        /// — they carry receiver-clock timestamps — so the render cursor keeps its
        /// pair and the change costs no visible discontinuity.
        /// </remarks>
        /// <param name="ownerTickIntervalSeconds">
        /// The period the owner's input tick is counted in. Callers supply this
        /// client's own cadence, which is the owner's only while every participant
        /// is configured to the same <c>NetworkSettings.tickRate</c> — a
        /// precondition of the owner-tick timeline that this method cannot check,
        /// because no wire field carries the owner's rate. A session mixing
        /// cadences reconstructs the remote timeline at the wrong rate.
        /// It is read only on the owner-tick timeline; a server broadcast tick
        /// carries its own period and never consults it.
        /// </param>
        public void AddStateFromBroadcast(
            TransformState state,
            uint   serverTick, bool hasServerTick,
            uint   ownerTick,  bool hasOwnerTick,
            double receiverNow,
            double ownerTickIntervalSeconds)
        {
            bool ownerTimeline = _ownerTickTimeline;

            lock (_syncRoot)
            {
                if (_timelineModeLatched != ownerTimeline)
                {
                    _timelineModeLatched = ownerTimeline;
                    RetireSenderTimelineState();
                }
            }

            if (ownerTimeline)
            {
                if (hasOwnerTick)
                    AddStateFromSenderTick(state, ownerTick, receiverNow, ownerTickIntervalSeconds);
                else
                    AddState(state, receiverNow);
            }
            else
            {
                if (hasServerTick)
                    AddStateFromSenderTick(
                        state, serverTick, receiverNow, ServerTickRate.IntervalSeconds);
                else
                    AddState(state, receiverNow);
            }
        }

        /// <summary>
        /// Drops everything estimated from the current sender's tick stream: the
        /// monotonic high-water, the clock offset, and the arrival-spread EMA.
        /// Caller must hold <see cref="_syncRoot"/>.
        ///
        /// The buffered snapshots are deliberately NOT cleared — they carry
        /// receiver-clock timestamps, so the render cursor keeps its bracketing
        /// pair and retiring the estimator costs no visible discontinuity.
        /// </summary>
        private void RetireSenderTimelineState()
        {
            _hasSenderTick    = false;
            _latestSenderTick = 0u;
            _hasClockOffset   = false;
            _clockOffset      = 0.0;
            _hasArrivalJitter = false;
            _arrivalJitterEma = 0.0;
        }

        /// <summary>
        /// Retire the sender-tick estimator because the object changed hands.
        ///
        /// The sender tick may be the OWNER's input tick, and that is a per-client
        /// counter which starts at zero on join and is never reconciled between
        /// clients.  After a handover the new owner's ticks can sit far below the
        /// high-water the previous owner established, and the monotonic gate in
        /// <see cref="AddStateFromSenderTick"/> would reject every one of them
        /// until the new owner's counter climbed past it — minutes of a replica
        /// frozen on every peer's screen, with its traffic still arriving.
        ///
        /// This is the same failure, and the same remedy, that
        /// <c>NetworkBehaviour.SetOwner</c> already applies to the NetworkVariable
        /// inbound-tick gate; the interpolator was simply not included.
        /// </summary>
        internal void ResetSenderTimeline()
        {
            lock (_syncRoot)
                RetireSenderTimelineState();
        }

        /// <summary>
        /// Current sender-clock offset estimate (receiver_now - sender_time).
        /// Exposed for diagnostics and unit tests; converges to a stable value
        /// after a few seconds of streaming snapshots.
        /// </summary>
        internal double ClockOffsetEstimate
        {
            get
            {
                lock (_syncRoot)
                    return _hasClockOffset ? _clockOffset : 0.0;
            }
        }

        /// <summary>
        /// Current arrival-spread estimate (mean absolute deviation, seconds).
        /// Exposed for diagnostics and unit tests; drives the adaptive delay.
        /// </summary>
        internal double ArrivalJitterEstimate
        {
            get
            {
                lock (_syncRoot)
                    return _hasArrivalJitter ? _arrivalJitterEma : 0.0;
            }
        }

        /// <summary>
        /// Core interpolation logic — pure function, separated from the Unity
        /// frame callback for deterministic unit testing.
        ///
       /// Searches the delay buffer for the pair of states that bracket
        /// <paramref name="renderTime"/> and returns the linearly/spherically
        /// interpolated result.
        /// </summary>
        /// <param name="renderTime">
        /// The target render time.  Typically <c>Time.unscaledTimeAsDouble - _interpolationDelay</c>.
        /// </param>
        /// <param name="result">
        /// On success: the interpolated <see cref="TransformState"/>.
        /// Undefined on failure.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a valid interpolated state was produced.
        /// <see langword="false"/> when:
        /// <list type="bullet">
        ///  <item>The buffer has fewer than 2 states.</item>
        ///  <item><paramref name="renderTime"/> is before the oldest buffered state.</item>
        ///  <item><paramref name="renderTime"/> is after the newest buffered state.</item>
        /// </list>
        /// The caller (e.g. <see cref="Update"/>) should be a no-op on false.
        /// </returns>
        public bool TryInterpolate(double renderTime, out TransformState result)
        {
            result = default;

            // Snapshot the two buffered states we need while holding the lock,
            // then release it before the Vector/Quaternion math.  This keeps
            // the critical section tiny (no floating-point work under the lock)
            // and ensures AddState() is never blocked by per-frame arithmetic.
            TimestampedState from;
            TimestampedState to;
            lock (_syncRoot)
            {
                // Need at least two states to define an interpolation segment.
                if (_buffer.Count < 2) return false;

                // With the ring buffer, states are stored in logical order starting at
                // _head.  Logical index i maps to physical (_head + i) % count.
                //
                // Monotonic remote timestamps make the bracketing-pair search
                // amortised O(1) via a cursor that advances with render time;
                // resetting on reset / older-than-cursor samples preserves
                // correctness when streams restart.  Without this hint the
                // inner loop is O(N) under the lock per frame per object,
                // which under interest-managed broadcast scales to hundreds
                // of thousands of lock-protected reads at fleet scale.
                int count = _buffer.Count;

                // If the cursor is stale (Clear / ConfigureForTest / first
                // call after spawn) start from logical index 0 and rebuild.
                int start = _bracketCursorValid ? _bracketCursor : 0;
                if (start > count - 2) start = 0; // ring shrank under us
                int fromIndex = -1;

                // Adversarial guard: if renderTime is older than the cursor's
                // left edge (a legitimate clock rewind, or a fresh stream
                // arriving with smaller sender-tick timestamps that the EMA
                // mapped behind the cursor), restart at logical 0 so the
                // search can still locate a valid pair.  Without this the
                // forward walk would never revisit the older window.
                int startPhys = (_head + start) % count;
                if (_buffer[startPhys].Timestamp > renderTime) start = 0;

                for (int i = start; i < count - 1; i++)
                {
                    int iA = (_head + i)     % count;
                    int iB = (_head + i + 1) % count;
                    if (_buffer[iA].Timestamp <= renderTime && _buffer[iB].Timestamp >= renderTime)
                    {
                        fromIndex = i;
                        break;
                    }
                }

                // renderTime is outside the buffered range — no interpolation possible.
                if (fromIndex < 0) return false;

                _bracketCursor      = fromIndex;
                _bracketCursorValid = true;

                int physFrom = (_head + fromIndex)     % count;
                int physTo   = (_head + fromIndex + 1) % count;
                from = _buffer[physFrom];
                to   = _buffer[physTo];
            }

            // Guard against division by zero when two states share the same
            // timestamp (e.g. two packets decoded in the same frame).
            double span = to.Timestamp - from.Timestamp;
            float t;
            if (span < double.Epsilon)
                t = 0f; // identical timestamps → return from-state unchanged
            else
                t = Mathf.Clamp01((float)((renderTime - from.Timestamp) / span));

            result = new TransformState
            {
                Position = Vector3.Lerp(from.State.Position, to.State.Position, t),

                // Quaternion.Slerp selects the shortest arc automatically.
                Rotation = Quaternion.Slerp(from.State.Rotation, to.State.Rotation, t),

                // When scale interpolation is disabled, snap to the destination
                // value so callers that apply scale receive a stable result.
                Scale    = _interpolateScale
                    ? Vector3.Lerp(from.State.Scale, to.State.Scale, t)
                    : to.State.Scale,
            };
            return true;
        }

        /// <summary>
        /// Resolve the pose when exactly one snapshot is buffered.  A stationary
        /// object a late joiner receives as a single full-state snapshot — with
        /// no follow-up delta, because it has not moved — cannot be bracketed by
        /// <see cref="TryInterpolate"/> and would otherwise sit at its spawn pose
        /// until the next periodic snapshot arrives (up to one snapshot interval
        /// later).  Once the render cursor reaches that lone snapshot, apply it
        /// verbatim so the object shows its true pose promptly; a later second
        /// sample hands control back to interpolation with no visible seam.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when exactly one state is buffered and
        /// <paramref name="renderTime"/> has reached it; otherwise
        /// <see langword="false"/> (no state, a pair already handled by
        /// interpolation, or the cursor has not yet reached the lone state).
        /// </returns>
        internal bool TrySnapToSingleState(double renderTime, out TransformState result)
        {
            result = default;

            lock (_syncRoot)
            {
                // A List with exactly one element stores it at index 0; the ring
                // head only advances once the buffer has filled past one entry.
                if (_buffer.Count != 1) return false;

                TimestampedState only = _buffer[0];
                if (renderTime < only.Timestamp) return false;

                result = only.State;
            }
            return true;
        }

        /// <summary>
        /// Bridge a gap when the render cursor has advanced PAST the newest
        /// buffered snapshot — a late or lost packet — by continuing along the
        /// last measured velocity instead of freezing at the last pose and then
        /// snapping forward when the next snapshot lands (the visible stutter on
        /// a jittery link).  Velocity is estimated from the two newest snapshots;
        /// the newest position is advanced by it for the elapsed time, capped at
        /// <see cref="_maxExtrapolationSeconds"/>; and the result is bounded by
        /// the same speed ceiling inbound snapshots obey, so a collapsed
        /// inter-snapshot interval (receiver-clock jitter) cannot inflate the
        /// estimate into a fling.  That bound is <see cref="_maxInterpolatedSpeed"/>;
        /// a project that disables the gate by setting it non-positive opts out of
        /// the fling bound here too, and a collapsed interval can then yield one
        /// oversized step before the next snapshot corrects it.
        /// Rotation and scale are held at the newest
        /// sample — a brief hold reads far better than a mispredicted spin, and
        /// position is the dominant cue.  When a fresh snapshot resumes,
        /// <see cref="TryInterpolate"/> takes over and any small prediction error
        /// is absorbed over the next frames.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when a bounded extrapolated pose was produced;
        /// <see langword="false"/> when extrapolation is disabled
        /// (<see cref="_maxExtrapolationSeconds"/> ≤ 0), fewer than two states are
        /// buffered, the render cursor has not passed the newest state, or the two
        /// newest samples share a timestamp (no derivable velocity).
        /// </returns>
        internal bool TryExtrapolate(double renderTime, out TransformState result)
        {
            result = default;

            // Disabled → preserve the freeze-then-resume behaviour.
            if (_maxExtrapolationSeconds <= 0f) return false;

            TimestampedState newest;
            TimestampedState prev;
            double extrapSeconds;
            double stateSpan;
            lock (_syncRoot)
            {
                int count = _buffer.Count;
                // Two samples are the minimum to derive a velocity.
                if (count < 2) return false;

                // States are stored in logical order from _head; the newest is
                // logical index count-1 and its predecessor count-2.
                int physNewest = (_head + count - 1) % count;
                int physPrev = (_head + count - 2) % count;
                newest = _buffer[physNewest];
                prev = _buffer[physPrev];

                // Extrapolation applies strictly beyond the newest sample; while
                // the cursor is still bracketed, TryInterpolate owns the frame.
                if (renderTime <= newest.Timestamp) return false;

                stateSpan = newest.Timestamp - prev.Timestamp;
                if (stateSpan < double.Epsilon) return false; // no derivable velocity

                // Cap how far past the newest sample we are willing to predict.
                extrapSeconds = renderTime - newest.Timestamp;
                if (extrapSeconds > _maxExtrapolationSeconds)
                    extrapSeconds = _maxExtrapolationSeconds;
            }

            // Velocity (per second) from the two newest samples, projected
            // forward by the capped interval: predicted = newest + (newest -
            // prev) * (extrapSeconds / stateSpan).  Written componentwise so the
            // interpolator carries no dependency on Vector3 operator overloads —
            // the same headless-test-compatibility discipline RemoteMotionGate
            // follows.  Math runs outside the lock (mirrors TryInterpolate) to
            // keep the critical section allocation-free.
            Vector3 np = newest.State.Position;
            Vector3 pp = prev.State.Position;
            // Ease-out damping.  Continuing at the full last-known velocity to the
            // cap and then hard-holding OVERSHOOTS an abrupt stop — the last
            // moving sample still carries pre-stop velocity — and snaps back when
            // the true stopped pose arrives.  Ramp the effective velocity down to
            // zero across the window (displacement = v·s·(1 − s/(2·cap))): the
            // prediction starts at the full rate (a seamless continuation of the
            // motion) but decelerates into a hold, which halves the peak overshoot
            // and removes the velocity discontinuity at the cap, so the eventual
            // correction is both smaller and gentler.  `extrapSeconds ≤
            // _maxExtrapolationSeconds` (capped above) and the field is > 0 here
            // (early-returned otherwise), so `damping ∈ [0.5, 1]`.
            double damping = 1.0 - extrapSeconds / (2.0 * _maxExtrapolationSeconds);
            float scale = (float)((extrapSeconds / stateSpan) * damping);
            Vector3 predicted = new Vector3(
                np.x + (np.x - pp.x) * scale,
                np.y + (np.y - pp.y) * scale,
                np.z + (np.z - pp.z) * scale);

            // Bound the prediction to the inbound speed ceiling so a collapsed
            // state span cannot turn into a fling — the same gate GateMotion
            // applies to real snapshots.  Skipped only when the ceiling is
            // disabled (≤ 0), so disabling the gate does not silently reintroduce
            // a clamp via the step floor.
            if (_maxInterpolatedSpeed > 0f)
            {
                predicted = RemoteMotionGate.ClampPositionStep(
                    np,
                    predicted,
                    extrapSeconds,
                    _maxInterpolatedSpeed,
                    MotionGateStepFloorUnits);
            }

            result = new TransformState
            {
                Position = predicted,
                Rotation = newest.State.Rotation, // held — see summary
                Scale = newest.State.Scale,
            };
            return true;
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Each frame: advance the render cursor by <see cref="_interpolationDelay"/>
        /// seconds behind real time and apply the interpolated transform.
        /// No-op when fewer than 2 states are buffered (e.g. at startup).
        /// </summary>
        private void Update()
        {
            // Render cursor: local monotonic time minus the configured delay.
            // Using Time.unscaledTimeAsDouble gives sub-millisecond precision
            // without the drift risk of float accumulation, and keeps the render
            // cursor on real time so a Time.timeScale change (pause / slow-mo)
            // neither stalls nor rewinds remote interpolation.
            double renderTime = Time.unscaledTimeAsDouble - EffectiveInterpolationDelay();

            // Interpolate when the cursor is bracketed; fall back to the lone
            // snapshot; and finally EXTRAPOLATE across a late/lost packet so the
            // object glides on its last velocity instead of freezing then
            // snapping.  Each fallback fires only when the prior one cannot.
            if (TryInterpolate(renderTime, out TransformState state)
                || TrySnapToSingleState(renderTime, out state)
                || TryExtrapolate(renderTime, out state))
                ApplyToTransform(state);
        }

        // ── Private helpers ────────────────────────────────────────────────────

        // Apply an interpolated state to this object's Transform components.
        // Matches the axis-gate pattern in NetworkTransform.ApplyState().
        private void ApplyToTransform(TransformState state)
        {
            transform.position = state.Position;
            transform.rotation = state.Rotation;
            if (_interpolateScale) transform.localScale = state.Scale;
        }

        // ── Test seam ─────────────────────────────────────────────────────────
        //
       // ConfigureForTest allows unit tests to set the buffer parameters without
        // going through Unity serialisation.  Accessible via InternalsVisibleTo.

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Set buffer configuration without Unity Inspector serialisation.
        /// <b>For unit tests only.</b>  Compiled only when
        /// <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        /// <param name="bufferSize">Maximum number of buffered states.</param>
        /// <param name="interpolationDelay">Seconds behind real-time to render.</param>
        /// <param name="interpolateScale">Whether scale is interpolated.</param>
        internal void ConfigureForTest(
            int    bufferSize             = 10,
            float  interpolationDelay     = 0.1f,
            bool   interpolateScale       = false,
            double maxFutureSkewSeconds   = 10.0,
            float  maxInterpolatedSpeed   = 50f,
            float  maxExtrapolationSeconds = 0.15f,
            bool   adaptiveDelay          = false,
            bool   ownerTickTimeline      = false)
        {
            lock (_syncRoot)
            {
                _bufferSize             = bufferSize;
                _interpolationDelay     = interpolationDelay;
                _adaptiveDelay          = adaptiveDelay;
                _ownerTickTimeline      = ownerTickTimeline;
                _interpolateScale       = interpolateScale;
                _maxFutureSkewSeconds   = maxFutureSkewSeconds;
                _maxInterpolatedSpeed   = maxInterpolatedSpeed;
                _maxExtrapolationSeconds = maxExtrapolationSeconds;
                // Reset ring-buffer state for a consistent starting condition.
                _buffer.Clear();
                _head = 0;
                // Displacement-gate reference state: a fresh fixture must start
                // without an inherited previous position so the first snapshot
                // is accepted verbatim.
                _lastGatePosition  = default;
                _lastGateTimestamp = 0.0;
                _hasGateState      = false;
                // Reset the high-water timestamp so subsequent AddState calls
                // with small timestamps (test vectors) are not silently dropped.
                _latestTimestamp = double.MinValue;
                // Bracketing-pair cursor is paired to the live buffer; a fresh
                // fixture must restart the search at logical index 0.
                _bracketCursor      = 0;
                _bracketCursorValid = false;
                // Sender-clock estimator state: a fresh fixture must start
                // without any inherited tick / offset bias.
                _hasSenderTick   = false;
                _latestSenderTick = 0u;
                _hasClockOffset  = false;
                _clockOffset     = 0.0;
                _hasArrivalJitter = false;
                _arrivalJitterEma = 0.0;
                // Latch the fixture's mode so the first broadcast does not read
                // as a mid-stream switch and retire state it never built.
                _timelineModeLatched = ownerTickTimeline;
                // Delay-floor notice re-arms per fixture so a test can observe it.
                _interpolationDelayFloored = false;
            }
        }
        /// <summary>
        /// Set the render-timeline mode alone, leaving every other field and all
        /// accumulated state untouched — the Inspector-toggle-during-play case.
        /// <b>For unit tests only.</b>
        /// </summary>
        internal void SetTimelineModeForTest(bool ownerTickTimeline)
        {
            lock (_syncRoot) { _ownerTickTimeline = ownerTickTimeline; }
        }
#endif // UNITY_INCLUDE_TESTS

        // Componentwise IsFinite over the position, rotation, and (when
        // enabled) scale of an inbound TransformState.  Spelled with
        // !IsNaN && !IsInfinity so the SDK compiles on Unity runtimes that
        // do not expose float.IsFinite.
        private bool IsFiniteSnapshot(TransformState s)
        {
            var p = s.Position;
            var r = s.Rotation;
            if (Bad(p.x) || Bad(p.y) || Bad(p.z)) return false;
            if (Bad(r.x) || Bad(r.y) || Bad(r.z) || Bad(r.w)) return false;
            if (_interpolateScale)
            {
                var c = s.Scale;
                if (Bad(c.x) || Bad(c.y) || Bad(c.z)) return false;
            }
            return true;
        }

        private static bool Bad(float v) => float.IsNaN(v) || float.IsInfinity(v);
    }
}
