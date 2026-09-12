// RTMPE SDK — Runtime/Sync/NetworkTransformInterpolator.cs
//
// Buffered interpolation for smooth movement of non-owner networked objects.
//
// Design decisions:
//  • Extends MonoBehaviour so Update() fires automatically on the main thread.
//  • Owner suppression: AddState() is valid on any client, and by convention
//    only non-owner clients call it — the receive dispatch routes an owned
//    object to reconciliation instead.  Update() does not rely on that
//    convention: it renders nothing while SetLocallyOwned(true) stands, because
//    the owning client drives its own transform and sends it with
//    NetworkTransform.Update() while this component runs afterwards.
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

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Which of <see cref="NetworkTransformInterpolator"/>'s resolvers produced
    /// the pose a frame rendered.
    /// </summary>
    /// <remarks>
    /// The render path is a ladder of three resolvers that answer for their own
    /// frame and nothing spans two of them, so a HANDOVER between two of them —
    /// interpolation running out of bracketed samples and extrapolation taking
    /// over, or the reverse when a packet lands — steps the rendered pose by
    /// whatever the two disagree about, in one frame, with nothing recording
    /// that it happened.  Naming the answering resolver is what lets the frame
    /// after a handover know it is a handover; <see cref="None"/> is a frame
    /// that wrote no pose at all.
    /// </remarks>
    internal enum RenderSource
    {
        /// <summary>No resolver answered — the frame wrote nothing.</summary>
        None = 0,
        /// <summary>A bracketed pair (<c>TryInterpolate</c>).</summary>
        Interpolated = 1,
        /// <summary>The lone buffered snapshot (<c>TrySnapToSingleState</c>).</summary>
        SingleState = 2,
        /// <summary>Prediction past the newest sample (<c>TryExtrapolate</c>).</summary>
        Extrapolated = 3,
    }

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
                 "a jittery one keeps more buffer.  Enabled by default; the value above is the " +
                 "ceiling, never the floor, so turning this off can only ever add delay.")]
        // Shipped on.  The fixed 0.1 s was measured as the largest single term in
        // ~135 ms of visibility delay that a remote player costs before a packet
        // has crossed a single network hop — and it is paid on every link,
        // including the clean ones it was sized for the worst of.  Adaptive holds
        // the cursor 3× the measured arrival spread behind the two-tick floor, so
        // a healthy link renders ~67 ms behind instead of 100 ms and a jittery one
        // keeps every millisecond of the configured buffer.
        //
        // ⚠️ A default governs components created from here on.  Unity serialises
        // this field, so a prefab or scene authored against an earlier version
        // carries its own `false` and is not changed by this — which is the
        // intended blast radius for an SDK, not an oversight: an upgrade must not
        // silently alter the motion of a shipped game.
        [SerializeField] private bool _adaptiveDelay = true;

        [Tooltip("Build the render timeline from the owning client's input tick rather than the " +
                 "server's broadcast tick.  The server tick is a uniform emission counter whose " +
                 "per-tick sample instant shifts with server load; the owner input tick binds each " +
                 "pose to the tick of the client that produced it, so remote motion is replayed on " +
                 "its originating timeline and is unaffected by the server's sampling cadence.  The " +
                 "input tick is client-supplied, so a peer can distort only the timing of its own " +
                 "object's motion — never another's — and the monotonicity, future-skew, and " +
                 "offset-step guards on the sender-tick path bound even that.  Enabled by default, " +
                 "paired with NetworkTransform.TickAlignedSampling — the two halves of one feature.")]
        // Shipped on, and flipped in the same change as
        // NetworkTransform._tickAlignedSampling. ⛔ Never on its own: the two are
        // the receiving and sending halves of remote motion timing, and
        // RemoteMotionTimingAdvisory exists precisely to report a project that
        // has one without the other. Shipping a default that manufactures that
        // pairing would make the SDK warn every new project about its own
        // defaults.
        [SerializeField] private bool _ownerTickTimeline = true;

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
        // ⚠️ 0.05 s, not the 0.15 s this shipped as.  The cap is how long a
        // GUESS is allowed to stand in for data, and the guess is a straight
        // line: at the 50 u/s ceiling 0.15 s of it is 7.5 units of invented
        // travel, and an object that STOPS — the commonest thing an object
        // does — overshoots by v·cap/2 before anything can correct it, which
        // at a 5 u/s walk is 0.375 units of visible overshoot and then a
        // snap back.  0.05 s covers one and a half 30 Hz snapshot intervals,
        // which is the loss the cap exists for, and holds the same overshoot
        // to 0.125 units.  ⛔ Still a [SerializeField] with its [Range]: a
        // project whose objects genuinely fly needs the hatch, and lowering a
        // default is not the same as removing a control.
        //
        // 🔴 This is NOT only a ceiling, and reasoning about it as one is wrong.
        // TryExtrapolate's ease-out divides by this field (damping = 1 −
        // t/(2·cap)), so it is the DECELERATION HORIZON as well as the cut-off
        // and lowering it changes the prediction at every t, not only past the
        // old ceiling.  Measured against the real resolver at 5 u/s, as a
        // fraction of the object's true travel:
        //
        //     lost packets   t         cap 0.15 s   cap 0.05 s
        //     one            33.3 ms      88.9 %       66.7 %
        //     one and a half 50.0 ms      83.3 %       50.0 %
        //
        // ⛔ Kept coupled rather than decoupled, and the reason is a property, not
        // an oversight: the ease-out exists so the predicted VELOCITY reaches
        // exactly zero at the cap (d/dt of v·t·(1 − t/(2·C)) is v·(1 − t/C)),
        // which is what removes the velocity discontinuity where prediction turns
        // into a hold.  A damping horizon R ≠ C either reintroduces that
        // discontinuity (R > C) or makes R the real cap and this field a no-op
        // beyond it (R < C) — two knobs, one of which acts.  So the honest
        // reading is that this number answers "how long may a guess stand in for
        // data", and a shorter answer means LESS guessing at every t: at one lost
        // 30 Hz packet the replica renders 66.7 % of the true travel and is 5.6 cm
        // behind on a 5 u/s walk, which the absorber then spreads across its
        // window — against 0.125 units of overshoot-and-snap-back saved on every
        // stop.  Pinned as a derivation, not as a literal, by
        // RenderPathSequenceTests.E7.
        [SerializeField] [Range(0f, 0.5f)] private float _maxExtrapolationSeconds = 0.05f;

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

        // Whether this client owns the object — the render path's ownership
        // predicate, pushed in by the ownership pipeline (SetLocallyOwned)
        // rather than read from NetworkBehaviour, which keeps this component
        // free of the object model and testable on its own.  Written under the
        // lock beside the buffer it retires; read from Update without one, as a
        // bool read is atomic and that is the same access the render path
        // already makes of _ownerTickTimeline.
        private bool _locallyOwned;

        // ── Render-side error absorption ──────────────────────────────────────
        //
        // The three resolvers below each answer for their own frame and nothing
        // spans two of them: a frame that changes resolver, or whose resolver
        // steps far further than the last frame did, writes that step to the
        // transform whole and the eye reads it as a yank.  The absorber sits
        // between the resolver and the transform.  At the moment a discontinuity
        // is detected it captures the error — the pose already on screen MINUS
        // this frame's resolver target — ONCE, and renders
        // `target + error · RenderBlend.Weight(s)` for one
        // RenderBlendDurationSeconds window, with w(0) = 1 and w(1) = 0.
        //
        // ⛔ Error DECAY, not `Lerp(startPose, target, s)`.  The target keeps
        // moving, so a position lerp would both close on it and chase it at a
        // rate that depends on the object's own speed, and its pose at s = 0 is
        // only accidentally the one already on screen.  Decaying a captured
        // error reproduces the previous rendered pose EXACTLY at s = 0 and
        // renders true velocity plus a bounded decaying closing term thereafter,
        // whatever the object is doing.
        //
        // Threading: written from the render path (Update / ApplyToTransform) on
        // the Unity main thread, and from ResetRenderBlend under _syncRoot when a
        // retirement drops the timeline these poses belong to.  Read from the
        // render path without the lock — the same access that path already makes
        // of _locallyOwned and _ownerTickTimeline, and for the same reason.
        //
        // [NonSerialized] throughout: these describe one frame's relationship to
        // the last, and Unity serialises a private field it can see.  A pose
        // baked into a prefab would arrive on a fresh instance as a pose it
        // never rendered — which is the single failure mode this whole mechanism
        // must not have (see the CUT in BlendRenderedPosition).
        //
        // 🔑 TWO sequences are recorded, and they answer two different questions.
        // _lastRenderedPosition is what the SCREEN showed: the error is measured
        // from it, because continuity is owed to the pose the player saw.
        // _lastTargetPosition / _lastTargetStep are what the RESOLVER produced:
        // the TRIGGER is measured from them, because "did the pose stream jump?"
        // is a question about the resolver's output and not about what this
        // component chose to render of it.
        //
        // 🔴 They were ONE sequence, and that was the defect underneath both of
        // this mechanism's failures.  During an absorption the rendered steps are
        // small by construction — w(0) = 1 renders the previous pose EXACTLY, so
        // the arming frame's step is zero — and a trigger whose baseline is that
        // step collapses to RenderBlendTriggerRatio × RenderBlendStepFloorUnits =
        // 0.08 units, which an ordinary 60 fps step of a 5 u/s object (0.083)
        // already exceeds.  The absorber's own output therefore re-armed the
        // absorber: which is why the deadline could not be restarted (see
        // BlendRenderedPosition), and why the resolver-identity rule appeared to
        // need no magnitude test — it was firing constantly on smooth motion and
        // the freeze that would have exposed it was being suppressed by the
        // inherited deadline.
        //
        // ⛔ The two readings are IDENTICAL whenever no absorption is in flight —
        // with none, the pose shown IS the previous target — so this is not a
        // different trigger, it is the same trigger with a subject that stays
        // defined while the absorber is working.
        [NonSerialized] private Vector3      _lastRenderedPosition;
        [NonSerialized] private bool         _hasLastRendered;
        [NonSerialized] private Vector3      _lastTargetPosition;
        [NonSerialized] private bool         _hasLastTargetPosition;
        [NonSerialized] private float        _lastTargetStep;
        [NonSerialized] private bool         _hasLastTargetStep;
        [NonSerialized] private RenderSource _lastRenderSource;
        [NonSerialized] private Vector3      _renderError;
        [NonSerialized] private bool         _hasRenderError;

        // When the window both halves share opened.  WHICH arm it is is not
        // recorded: the arm is decided once per applied frame and handed to each
        // half as it runs, so a half that is asked cannot be answering about an
        // older one.  An identity carried by the deadline instead would stop
        // separating two arms the moment the unscaled clock did not advance
        // between them.
        [NonSerialized] private double       _renderArmedAt;

        // The facing's half of the same absorption.
        //
        // 🔴 Without it the body is absorbed and the head is not, and on the
        // ARMING frame those are not merely different rates: w(0) = 1 reproduces
        // the previous position exactly, so the object is perfectly still while
        // its facing turns the whole correction in one frame.  Measured on a
        // bursty 5 % link, 60 fps: a 89.7° facing step on a frame whose
        // rendered position moved 0.0000 units — a replica that spins on the
        // spot and then walks off in the new direction.
        //
        // ⛔ Carried as an OFFSET from the resolver's own facing, never as a
        // slerp from a frozen one, for the reason the position error is carried
        // rather than lerped from a frozen start: the target keeps moving during
        // the window, and a blend anchored to a stale pose lags it.
        [NonSerialized] private Quaternion   _lastRenderedRotation;
        [NonSerialized] private bool         _hasLastRenderedRotation;
        [NonSerialized] private Quaternion   _renderRotationError;
        [NonSerialized] private bool         _hasRenderRotationError;

        // Whether the displacement gate CLAMPED the newest accepted snapshot.
        // Written under _syncRoot by GateMotion (both AddState paths hold it),
        // read from the render path without one — same discipline as
        // _locallyOwned above.
        //
        // ⛔ NOT a security property, and it must not be read as one.  The
        // absorber acts downstream of ingest, so it cannot un-clamp anything: a
        // clamped snapshot is already clamped by the time any of this runs.  The
        // latch exists because the gate's walk toward a claimed teleport moves
        // the replica at maxSpeed + stepFloor·sendHz — 65 u/s at the shipped
        // defaults, about 1.08 units per frame at 60 fps against a ~0.1 unit
        // nominal — so without it the discontinuity trigger below fires on the
        // refusal and LENGTHENS it.  A peer that keeps the gate clamping
        // therefore disables its own replica's absorber and gains nothing but a
        // less smooth rendering of itself.
        //
        // ⚠️ The trigger reads the resolver's target sequence now, and a walk is
        // a CONSTANT velocity, so what would fire is the walk's first frame — not
        // "every frame of a refusal", which is what this said while the baseline
        // was the pose on screen.  One frame is still one frame too many: the
        // first thing an absorption does is hold the pose still, in a mechanism
        // whose whole purpose is to bound how fast a claim may move a replica
        // (P6).
        //
        // ⚠️ And it says "the newest ACCEPTED snapshot was clamped", which is
        // narrower than "the gate is currently clamping": it is rewritten only
        // when a snapshot is accepted, so one clamped sample followed by silence
        // leaves rule (b) suppressed until the next accepted sample, however long
        // that is.  ⛔ Deliberately not given an expiry, because in that state the
        // suppression is unreachable rather than merely long-lived: a new
        // discontinuity in an INTERPOLATED target requires a new sample, and any
        // accepted sample rewrites this latch — to false when it was within
        // budget, to true when it was not, which is the case the suppression is
        // for.  What silence produces instead is a resolver HANDOVER (the cursor
        // runs past the newest sample into extrapolation, and back when data
        // resumes), and rule (a) is not suppressed by this latch.  Held by
        // RenderPathSequenceTests.E8.
        [NonSerialized] private bool         _newestSampleWasClamped;

        // The absorption window, seconds.
        //
        // ⛔ Not a new number.  NetworkTransform.ReconcileDuration
        // (NetworkTransform.cs:270, documented at NetworkTransform.cs:30-31)
        // already spends exactly 100 ms lerping an OWNER's predicted pose onto a
        // server correction.  A replica absorbing a discontinuity and an owner
        // absorbing a reconciliation are the same event seen from two sides, so
        // the SDK has ONE error-absorption period rather than two numbers that
        // drift apart.
        private const double RenderBlendDurationSeconds = 0.10;

        // How many times the previous frame's rendered step this frame must
        // exceed before it is read as a discontinuity rather than as motion.
        //
        // EffectiveInterpolationDelay is clamped to
        // [MinInterpolationDelaySeconds, _interpolationDelay] — [66.7 ms,
        // 100 ms] at the shipped ceiling — and the adaptive controller may in
        // principle move the render cursor across that whole 33.3 ms span
        // between two frames.  At 60 fps a frame then advances the cursor
        // 16.7 + 33.3 = 50 ms against a 16.7 ms nominal: 3.0× with nothing
        // wrong anywhere.  The ratio must exceed that, hence 4.
        //
        // In practice the margin is far wider than 4/3: the delay is driven by
        // an EMA with a 1/30 gain, so one accepted packet moves the target by at
        // most a thirtieth of its own deviation and a full-span jump in a single
        // frame is unreachable on any real stream.  The bound is stated at the
        // worst case because that is the number that has to hold.
        //
        // ⚠️ The derivation assumes the shipped 0.1 s ceiling AND ~60 fps, and
        // it is the RATIO of one frame's possible cursor advance to one frame's
        // nominal advance — so both ends move it.  Authoring _interpolationDelay
        // above ≈ 0.117 s at 60 fps, or running much above 60 fps at the shipped
        // ceiling, lets a healthy adaptive adjustment reach 4× nominal on its
        // own; rule (b) then arms where nothing is wrong and stops
        // discriminating.  It stays bounded and self-limiting when it does —
        // the error is re-measured from the pose on screen and the window is a
        // deadline — but it is no longer measuring what it was written to
        // measure, and rule (a) is what still covers a real resolver handover.
        private const float RenderBlendTriggerRatio = 4.0f;

        // The floor under that ratio's baseline, world units.  Two owner send
        // quanta (NetworkTransform.cs:113, _positionThreshold = 0.01f): under
        // two quanta the previous frame's step is quantisation noise rather than
        // motion, and a ratio taken against noise arms on everything.
        //
        // ⛔ Deliberately NOT MotionGateStepFloorUnits (0.5 f).  That floor
        // exists for the INGEST gate, where it absorbs quantisation on a
        // per-SNAPSHOT step; 0.5 units is roughly the size of the snap this
        // trigger is hunting, so reusing it would make the trigger blind to its
        // own subject.
        private const float RenderBlendStepFloorUnits = 0.02f;

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
            // propagate NaN out of TryInterpolate and from there into
            // transform.position / transform.rotation, which Unity persists
            // unchecked.  (An earlier version of this comment named a
            // `_lastInterpolatedPose` field; no such field exists, and has not
            // for as long as the file has been under test.)  Quenching at
            // ingress keeps the buffer free of
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
            {
                // Nothing was refused: the gate is off, or this is the first
                // snapshot and has no predecessor to be measured against.  The
                // render-side absorber reads this latch to stay out of the way
                // of a refusal in progress, so leaving a stale `true` standing
                // here would suppress it on a stream that is no longer clamped.
                _newestSampleWasClamped = false;
                return candidate;
            }

            // Cap the interval feeding the displacement budget.  Without the
            // cap, a peer that withholds snapshots accrues an unbounded budget
            // (budget grows with the gap) and can then cash it in for a single
            // long jump; the cap bounds the budget to one MaxInterval window.
            double dt = timestamp - _lastGateTimestamp;
            if (dt > MotionGateMaxIntervalSeconds) dt = MotionGateMaxIntervalSeconds;

            Vector3 clamped = RemoteMotionGate.ClampPositionStep(
                _lastGatePosition,
                candidate.Position,
                dt,
                _maxInterpolatedSpeed,
                MotionGateStepFloorUnits);

            // Componentwise, deliberately not `clamped != candidate.Position`:
            // Unity's Vector3 operator== is an APPROXIMATE comparison (a ~1e-5
            // tolerance on the difference) and this file carries no dependency
            // on Vector3 operator overloads at all — see TryExtrapolate.  Exact
            // equality is also the right question: ClampPositionStep returns the
            // candidate itself, unmodified, when it is within budget.
            _newestSampleWasClamped =
                   clamped.x != candidate.Position.x
                || clamped.y != candidate.Position.y
                || clamped.z != candidate.Position.z;

            candidate.Position = clamped;
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
        /// The newest snapshot this interpolator has accepted, if it holds one.
        /// </summary>
        /// <remarks>
        /// This is the newest snapshot this component ACCEPTED, which is not
        /// what its transform holds: on a replica the transform carries this
        /// component's own render output, sampled a full
        /// <see cref="InterpolationDelaySeconds"/> behind the timeline.  A caller
        /// reconstructing a partial update needs the former — filling an absent
        /// field from the rendered pose feeds the delayed output back in as a
        /// fresh sample, and every such fill moves the object further behind.
        ///
        /// ⚠️ *Accepted*, not *sent*: the position reported here has been through
        /// the displacement gate (see <c>GateMotion</c>), so while a peer is
        /// claiming an implausible jump this is the clamped pose being walked
        /// toward the claim, not the claim.  That is the same pose the render
        /// path uses, which is what makes it the right baseline — but it is not
        /// a reading of what the sender wrote.
        ///
        /// Returns false while no snapshot has been accepted since the object
        /// last became a replica: the buffer is the record, so a handover
        /// (<see cref="RetireBufferedTimeline"/>) retires this with it rather
        /// than offering the previous replica life's pose to the next one.
        /// </remarks>
        public bool TryGetLatestAcceptedState(out TransformState state)
        {
            lock (_syncRoot)
            {
                int count = _buffer.Count;
                if (count == 0)
                {
                    state = default;
                    return false;
                }

                // Logical order runs from _head; the newest is logical count-1.
                // While the buffer is still filling _head is 0 and this is the
                // last entry added, and once it is full count == _bufferSize, so
                // the one expression covers both.
                state = _buffer[(_head + count - 1) % count].State;
                return true;
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
        /// Declare whether THIS client owns the object this component is attached
        /// to.  Called by the ownership pipeline on every event that can change
        /// the answer — a server-confirmed handover, and the start of each spawn
        /// cycle, which is where a pooled instance acquires a new owner without a
        /// handover ever being announced.
        /// </summary>
        /// <remarks>
        /// Every such event retires the sender-tick estimator, whichever way the
        /// object went.  The sender tick may be the OWNER's input tick, and that
        /// is a per-client counter which starts at zero on join and is never
        /// reconciled between clients: after a handover the new owner's ticks can
        /// sit far below the high-water the previous owner established, and the
        /// monotonic gate in <see cref="AddStateFromSenderTick"/> would reject
        /// every one of them until the new owner's counter climbed past it —
        /// minutes of a replica frozen on every peer's screen, with its traffic
        /// still arriving.  This is the same failure, and the same remedy, that
        /// <c>NetworkBehaviour</c> applies to the NetworkVariable inbound-tick
        /// gate on the same event.
        ///
        /// While the answer is <see langword="true"/> this component writes
        /// nothing: the owner drives its own transform and broadcasts it, and the
        /// buffered snapshots describe where a DIFFERENT client last drove the
        /// object.  Replaying them fights the new owner's own motion — and it
        /// fights it from the late execution slot this component deliberately
        /// occupies, so the interpolated pose is the one that survives the frame
        /// and the object is immobile for the client that just took it.
        ///
        /// The buffer is dropped only when the object came to this client.  A
        /// handover between two REMOTE peers leaves this client a replica either
        /// way, and there the snapshots are still the right ones to render: they
        /// carry receiver-clock timestamps, so the render cursor keeps its
        /// bracketing pair and the handover costs no visible pop.  The sender-tick
        /// estimator is retired on both, because it was built from the previous
        /// owner's counter and clock in either direction.
        /// </remarks>
        internal void SetLocallyOwned(bool locallyOwned)
        {
            lock (_syncRoot)
            {
                _locallyOwned = locallyOwned;
                RetireSenderTimelineState();
                // Unconditional, both directions.  The absorber's whole premise
                // is that the pose it blends from is one THIS component put on
                // screen for THIS object under its current ownership; an
                // ownership event ends that premise whichever way the object
                // went.  ⛔ It cannot ride on RetireBufferedTimeline below: that
                // fires only when the object came TO this client, and the
                // away-from-us direction deliberately keeps the buffer.
                ResetRenderBlend();
                if (locallyOwned) RetireBufferedTimeline();
            }
        }

        /// <summary>
        /// Retire everything the previous occupant of this instance left
        /// behind.  Called at the start of a spawn cycle, where the object is
        /// not the one these snapshots describe.
        /// </summary>
        /// <remarks>
        /// <see cref="SetLocallyOwned"/> keeps the buffer for a client that was
        /// and remains a replica, and that is right for a handover: the
        /// snapshots carry receiver-clock timestamps, so the render cursor
        /// keeps its bracketing pair and the handover costs no visible pop.  A
        /// pooled instance re-acquired as a replica reaches the same branch on
        /// a premise that is false — the snapshots belong to a different
        /// object, at a different place — so the first frame of the new life
        /// renders the old one's pose and the displacement gate meters the
        /// first real snapshot as travel from it.
        /// </remarks>
        internal void RetireForNewObjectLife()
        {
            lock (_syncRoot)
            {
                RetireSenderTimelineState();
                RetireBufferedTimeline();
            }
        }

        /// <summary>
        /// Drop the buffered replica timeline: the snapshots, the search cursor
        /// paired to them, the receiver-clock high-water and the displacement
        /// gate's reference pose.  Caller must hold <see cref="_syncRoot"/>.
        /// </summary>
        /// <remarks>
        /// The gate reference goes with the buffer.  It is the position the next
        /// snapshot's displacement is measured from, so leaving a pose behind
        /// from before the object changed hands would walk the first snapshot of
        /// the next replica life back toward it at the speed ceiling.  The
        /// high-water is reset for the same reason it is safe to: with no
        /// snapshots left there is nothing for a new one to be out of order with.
        /// </remarks>
        private void RetireBufferedTimeline()
        {
            ResetRenderBlend();
            _buffer.Clear();
            _head               = 0;
            _bracketCursor      = 0;
            _bracketCursorValid = false;
            _latestTimestamp    = double.MinValue;
            _lastGatePosition   = default;
            _lastGateTimestamp  = 0.0;
            _hasGateState       = false;
        }

        /// <summary>
        /// Forget the pose this component last put on screen, the discontinuity
        /// trigger's baseline, and any absorption in flight.  Caller must hold
        /// <see cref="_syncRoot"/>.
        /// </summary>
        /// <remarks>
        /// Called wherever the timeline this component is rendering stops being
        /// the one those poses belong to: a retirement
        /// (<see cref="RetireBufferedTimeline"/>, which covers a pooled reuse and
        /// the object coming to this client), an ownership event in EITHER
        /// direction (<see cref="SetLocallyOwned"/>), and a fresh test fixture.
        ///
        /// ⛔ The reset leaves <c>_hasLastRendered</c> false, and the next frame
        /// is therefore a CUT — it writes the resolver's pose verbatim.  That is
        /// the required behaviour, not a degradation: the alternative is a
        /// replica sliding in from wherever the previous occupant of a pooled
        /// instance died, which is invisible in a two-player test where both
        /// spawn at the origin and grossly wrong in a shipped game.
        /// </remarks>
        private void ResetRenderBlend()
        {
            _lastRenderedPosition   = default;
            _hasLastRendered        = false;
            _lastTargetPosition     = default;
            _hasLastTargetPosition  = false;
            _lastTargetStep         = 0f;
            _hasLastTargetStep      = false;
            _lastRenderSource       = RenderSource.None;
            _renderError            = default;
            _renderArmedAt          = 0.0;
            _hasRenderError         = false;
            _lastRenderedRotation   = default;
            _hasLastRenderedRotation = false;
            _renderRotationError    = default;
            _hasRenderRotationError = false;
            _newestSampleWasClamped = false;
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
        /// verbatim so the object shows its true pose promptly.
        ///
        /// <para>⚠️ A later second sample hands control back to interpolation, and
        /// that handover is NOT free — the comment here claimed "no visible
        /// seam" and nothing made it true.  This resolver pins the object at one
        /// snapshot's pose while interpolation resumes at the render cursor,
        /// which is a different point on the timeline, so the pose steps by
        /// whatever the two disagree about in the frame the second sample lands.
        /// What makes it invisible is downstream: the render path records which
        /// resolver answered each frame and absorbs the step across the
        /// absorption window (see <c>BlendRenderedPosition</c>, rule (a)).</para>
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
        /// position is the dominant cue.
        ///
        /// <para>⚠️ When a fresh snapshot resumes, <see cref="TryInterpolate"/>
        /// takes over — and the prediction error goes onto the screen in the
        /// single frame that happens in.  This comment used to say it "is
        /// absorbed over the next frames"; nothing absorbed anything, and no
        /// test asserted a frame SEQUENCE, so the claim survived.  It is true
        /// now, and it is true one layer down rather than here: the render path
        /// names the resolver that answered each frame and decays the handover
        /// error across the absorption window (see <c>BlendRenderedPosition</c>).
        /// This method still produces the error; what changed is that something
        /// spends it.</para>
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
            //
            // 🔴 The cap DIVIDES this term, so it is the deceleration horizon and
            // not merely a ceiling: halving it does not truncate the same curve,
            // it steepens the curve at every t.  That coupling is deliberate — it
            // is what puts the predicted velocity at exactly zero when the
            // prediction ends — and it is what makes _maxExtrapolationSeconds a
            // SHAPE parameter.  The cost of the shipped value is tabulated at the
            // field's declaration and asserted as a derivation in
            // RenderPathSequenceTests.E7; change one and the other must move.
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
            // Nothing to render for an object this client owns: the owner moves
            // it and broadcasts the result, and any snapshot still buffered is
            // another client's account of where it used to be.  Writing one here
            // would overwrite the owner's own motion — this component runs last
            // by design, so its pose is the one that survives the frame.
            if (_locallyOwned) return;

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
            //
            // ⛔ Written as if / else if rather than as one `||` chain, which is
            // the same control flow and NOT the same program: the ladder's
            // answer is two facts, the pose and WHICH resolver produced it, and
            // a `||` discards the second.  A handover between two of these — the
            // cursor running past the newest sample, or a packet landing and
            // bringing it back — steps the rendered pose by whatever the two
            // resolvers disagree about, in one frame; without the identity the
            // frame after a handover cannot tell it apart from motion.
            TransformState state;
            RenderSource   source;
            if (TryInterpolate(renderTime, out state))
                source = RenderSource.Interpolated;
            else if (TrySnapToSingleState(renderTime, out state))
                source = RenderSource.SingleState;
            else if (TryExtrapolate(renderTime, out state))
                source = RenderSource.Extrapolated;
            else
            {
                // Buffer underrun: no resolver could answer, so this frame
                // writes nothing and the pose already on screen stands.
                //
                // ⛔ _hasLastRendered is deliberately NOT cleared.  That pose is
                // still this component's own most recent output for this object,
                // and it is still the right thing for the next answered frame to
                // absorb its error against — clearing it would turn every gap in
                // the stream into a cut.  Only the resolver identity is
                // retired, because no resolver answered.
                _lastRenderSource = RenderSource.None;
                return;
            }

            ApplyToTransform(state, source);
        }

        // ── Private helpers ────────────────────────────────────────────────────

        // The sibling whose axis gates apply to this object.
        //
        // ⛔ Resolved lazily, not in Awake: this component and the
        // NetworkTransform are both MonoBehaviours on the same GameObject and
        // Unity does not order their Awake calls, so a resolution there would
        // depend on the component order in the prefab.
        //
        // 🔴 Held as a Component as well as an interface, and the pair is what
        // makes the null test correct. `ITransformAxisGates` is an interface, so
        // `iface == null` compiles to plain reference equality — NOT to Unity's
        // overloaded Object.operator==. A Destroy()d NetworkTransform is
        // non-null as an interface reference while Unity considers it null, so
        // an interface-only field reports gates that belong to a component that
        // no longer exists, and reads back the last serialised booleans rather
        // than throwing. NetworkBehaviour states this rule for its own sibling
        // caches: "the Unity null operator (==) is used everywhere instead of
        // `is` so a destroyed-but-not-finalised component is treated as missing
        // and re-queried".
        //
        // ⚠️ And the query is re-armed rather than latched for the life of the
        // GameObject. A NetworkTransform can be added at runtime, destroyed, or
        // arrive on a pooled instance an INetworkObjectPool re-composed — which
        // is the documented reason NetworkBehaviour.ResetSyncComponentCache
        // exists. A latch resolved once on an object with no sibling would
        // ignore the one added a frame later, for ever, in silence.
        [NonSerialized] private Component            _axisGatesComponent;
        [NonSerialized] private ITransformAxisGates  _axisGates;
        [NonSerialized] private bool                 _axisGatesQueried;

        private ITransformAxisGates AxisGates
        {
            get
            {
                // Unity's == on the Component, so a destroyed sibling reads as
                // missing and the query is re-armed.
                if (_axisGatesQueried && _axisGatesComponent == null && _axisGates != null)
                    _axisGatesQueried = false;

                if (!_axisGatesQueried)
                {
                    _axisGatesQueried   = true;
                    _axisGates          = GetComponent<ITransformAxisGates>();
                    _axisGatesComponent = _axisGates as Component;

                    // No sibling is not an answer worth keeping: one may be
                    // added at any time, and re-asking costs a GetComponent on
                    // a frame that was already going to write a transform.
                    if (_axisGates == null) _axisGatesQueried = false;
                }
                return _axisGates;
            }
        }

        // Apply an interpolated state to this object's Transform components.
        //
        // 🔴 The axis gates are read from the sibling NetworkTransform, and this
        // is where they have to be read. The comment here used to claim it
        // "matches the axis-gate pattern in NetworkTransform.ApplyState()" and
        // it did not: position and rotation were written unconditionally, and
        // ApplyState — the method that does honour them — is called by nothing
        // in the shipped runtime. So `_syncPosition = false` on a prefab left
        // every remote replica's position driven by the wire anyway, which is
        // the whole of what the toggle exists to prevent.
        //
        // ⚠️ No sibling means no gates, and everything is applied. An
        // interpolator on an object with no NetworkTransform has nothing to
        // inherit a policy from, and refusing by default would silently freeze
        // it.
        private void ApplyToTransform(TransformState state, RenderSource source)
        {
            var gates = AxisGates;

            // The resolver's own step is asked for BEFORE the gates, because
            // "did the pose stream jump?" is a question about the sequence this
            // component received and not about the part of it this object
            // happens to render.  A replica whose position is local business
            // still receives one, and its facing is owed the same continuity.
            bool armed = ObserveResolverStep(state.Position, source);

            // ⛔ The rendered-pose record lives INSIDE its own gate, and each
            // half keeps its own.  A frame this component does not write leaves
            // no pose of its own on screen, so what it wrote last stops
            // describing the object: claiming it would let a gate re-opened
            // mid-session blend from a place nobody has seen for as long as the
            // gate was shut, and — on a replica the wire never moves — keep a
            // running record of poses nothing ever showed.
            if (gates == null || gates.SyncPosition)
            {
                Vector3 shown = BlendRenderedPosition(state.Position, ref armed);
                transform.position = shown;

                _lastRenderedPosition = shown;
                _hasLastRendered      = true;
            }
            else
            {
                _hasLastRendered = false;
                _hasRenderError  = false;
            }

            if (gates == null || gates.SyncRotation)
            {
                Quaternion facing = BlendRenderedRotation(state.Rotation, armed);
                transform.rotation = facing;

                _lastRenderedRotation = facing;

                // The record is kept only when what was shown names a
                // direction.  A degenerate rotation is rendered as the resolver
                // produced it, and an offset measured against one is meaningless
                // in the other direction as well.
                _hasLastRenderedRotation = HasDirection(facing);
            }
            else
            {
                _hasLastRenderedRotation = false;
                _hasRenderRotationError  = false;
            }

            // ⚠️ Scale needs BOTH. _interpolateScale is this component's own
            // question — whether to smooth scale between snapshots — and
            // SyncScale is the object's, whether the network may write it at
            // all. They were two independent Inspector checkboxes held together
            // by a tooltip saying "to match NetworkTransform._syncScale = false":
            // a convention, enforced by nothing, and one checkbox away from a
            // replica whose scale is driven by the wire on an object that has
            // scale sync switched off. The defaults agreed, which is why
            // nothing was broken and nothing would have noticed.
            if (_interpolateScale && (gates == null || gates.SyncScale))
                transform.localScale = state.Scale;
        }

        // The rules that decide an arm, in order.  They are answered once per
        // applied frame, for both halves of the absorber at once.
        //
        // The rule, in order:
        //
        //   CUT      nothing has been rendered for this object's CURRENT life,
        //            so there is no pose of ours on screen to be continuous
        //            with.  ⛔ transform.position is NOT a stand-in for one: on
        //            a pooled instance it holds the PREVIOUS occupant's pose,
        //            and seeding from it slides every reused object in from
        //            wherever the last one died.  This is the single most
        //            important invariant here, and it is invisible in a
        //            two-player test where both players spawn at the origin.
        //
        //   (a)      the answering resolver changed AND the resolver's target
        //            jumped.  The three of them disagree at their boundaries by
        //            construction — that is what a boundary IS — but the SIZE of
        //            the disagreement is not fixed by it, and at the commonest
        //            boundary of all it is nil: interpolation and extrapolation
        //            alternate frame by frame on a 30 Hz stream rendered at
        //            60 fps, where both answer the same motion to within a frame
        //            of it.
        //
        //            🔴 The identity WITHOUT a magnitude test armed there anyway,
        //            and arming is not free: w(0) = 1 renders the previous pose
        //            EXACTLY, so the arming frame is a frame of ZERO movement.
        //            Measured on a constant-velocity target with the resolver
        //            alternating every frame — perfectly smooth motion, nothing
        //            wrong anywhere — that was 7.3 frozen frames per second at
        //            60 fps and 9.4 at 144, with per-frame steps swinging between
        //            0 and 1.9× nominal: a ~10 Hz judder manufactured by the
        //            thing that exists to remove judder.  What the identity
        //            still buys is the frames a ratio cannot speak for — after a
        //            frame that wrote nothing, or on the first frames of a life,
        //            no step has been measured for the ratio to be taken against
        //            and rule (b) declines.
        //
        //   (b)      the resolver's target moved further from its OWN previous
        //            target than RenderBlendTriggerRatio times the step it made
        //            last frame (floored, so a ratio is never taken against
        //            quantisation noise).  Suppressed while the ingest gate is
        //            clamping: see _newestSampleWasClamped.
        //
        // ⚠️ Rule (b) needs a MEASURED target step, so it is blind for the first
        // TWO rendered frames of a life: frame 1 cuts and has no predecessor,
        // frame 2 is the one that produces the first step, and frame 3 is the
        // first that can take a ratio against it.  Without that the only baseline
        // available on frame 2 is the floor, and a floor-based ratio arms on any
        // object moving faster than ~4.8 u/s at 60 fps — i.e. on ordinary motion,
        // every time anything spawns.  With one frame of history there is no way
        // to tell a snap from speed, so rule (b) declines to guess and rule (a)
        // covers the frame — bounded, because rule (a) also needs the resolver to
        // have CHANGED, which ordinary motion does not do.
        //
        // 🔑 Re-arm, never accumulate.  The error is re-measured from the pose on
        // screen NOW, which already contains whatever is left of an absorption in
        // flight, so arming twice cannot sum two errors into a growing offset.
        //
        // 🔑 And the deadline restarts WITH it, so a discontinuity gets a whole
        // window whatever happened to be open when it landed.  ⛔ That has not
        // always been safe, and the reason it is now is the trigger's SUBJECT.
        // With the trigger measured against the pose on SCREEN, an open
        // absorption dragged its own baseline down to RenderBlendStepFloorUnits
        // and re-armed itself every frame; restarting the clock then pinned s at
        // 0 for ever and froze the replica at the pose it held when the
        // correction arrived, with the suite green and the resolver working
        // perfectly.  The deadline was therefore INHERITED — and the cost of that
        // was never written down: w is then evaluated at the inherited s, so the
        // arming frame no longer reproduces the pose on screen and a large late
        // correction landing 83 ms into an open window was passed through 93 %
        // whole, which is the yank the mechanism exists to remove.  Measured on
        // the resolver's target sequence the absorber's own output is not part of
        // the question, so a re-arm needs a fresh jump in the RESOLVER's output.
        //
        // 🔑 A chain of arms is bounded by arithmetic rather than by a deadline.
        // Arming needs this frame's target step to exceed RenderBlendTriggerRatio
        // times the last one, so an UNBROKEN chain needs the resolver's step to
        // quadruple every frame; a step is bounded by the ingest gate at
        // _maxInterpolatedSpeed / sendHz + MotionGateStepFloorUnits per snapshot
        // (≈ 2.2 units at the shipped defaults, ≈ 1.1 per frame at 60 fps), which
        // is under three quadruplings from the floor.  A frozen run is a few
        // frames, not a life sentence, and it needs the resolver itself to be
        // escalating — E1 and E9 in RenderPathSequenceTests drive both halves.
        // Whether the resolver's output jumped on this frame, and the record of
        // the sequence that answers it — the target sequence described in the
        // field block, never the rendered one.
        //
        // ⛔ Asked once per applied frame, ahead of the axis gates, and it
        // records unconditionally.  A gate decides what this object renders, not
        // what it received: a sequence kept only on the frames one half happened
        // to write reports a jump across every gap in that half, and reports it
        // to the other half as well.
        private bool ObserveResolverStep(Vector3 target, RenderSource source)
        {
            bool armed = false;

            if (_hasLastTargetPosition)
            {
                float baseline = _hasLastTargetStep && _lastTargetStep > RenderBlendStepFloorUnits
                    ? _lastTargetStep
                    : RenderBlendStepFloorUnits;
                double threshold = RenderBlendTriggerRatio * (double)baseline;

                bool jumped =
                    DistanceSquared(target, _lastTargetPosition) > threshold * threshold;

                armed =
                    jumped
                    && (source != _lastRenderSource                                  // (a)
                        || (!_newestSampleWasClamped && _hasLastTargetStep));        // (b)

                if (armed)
                {
                    // Every arm gets a whole window, and is a distinct arm even
                    // where the clock does not separate it from the last one:
                    // an unscaled clock is not guaranteed to advance between two
                    // applies, and an identity carried by the deadline alone
                    // stops re-measuring exactly there.
                    _renderArmedAt = Time.unscaledTimeAsDouble;
                }

                _lastTargetStep    = (float)Math.Sqrt(
                    DistanceSquared(target, _lastTargetPosition));
                _hasLastTargetStep = true;
            }

            _lastTargetPosition    = target;
            _hasLastTargetPosition = true;
            _lastRenderSource      = source;
            return armed;
        }

        // Render the resolver's target with any pose error this component owes
        // the screen, decayed across the window the arm opened.
        //
        // The rules that decide an arm — the resolver-identity condition, the
        // magnitude condition, the deadline and the bound on a chain of them —
        // are stated above ObserveResolverStep, which answers them.  What is
        // left here is the CUT and the decay.
        private Vector3 BlendRenderedPosition(Vector3 target, ref bool armed)
        {
            // CUT: nothing of ours is on screen — a life that has rendered
            // nothing yet, or a gate that has been shut since the last time it
            // did — so there is no pose to be continuous with and the resolver's
            // own is the honest answer.
            if (!_hasLastRendered) return target;

            if (armed)
            {
                float ex = _lastRenderedPosition.x - target.x;
                float ey = _lastRenderedPosition.y - target.y;
                float ez = _lastRenderedPosition.z - target.z;

                // ⚠️ IsFiniteSnapshot admits any finite coordinate, including
                // ones near float's range, and the DIFFERENCE of two of those
                // overflows to infinity.  An infinite error renders an infinite
                // pose, that pose becomes the baseline the next frame measures
                // its own error from, and the component never recovers — so an
                // error that is not finite is refused rather than absorbed, and
                // any absorption in flight goes with it because the pose it was
                // measured from is no longer usable.  Degrades to a cut, which
                // is the pose the resolver produced on its own.
                if (Bad(ex) || Bad(ey) || Bad(ez))
                {
                    // ⛔ Withdrawn from BOTH halves, not just this one.  The
                    // correction is one event and the pose it would have been
                    // measured from is unusable, so a facing absorbed on the
                    // strength of it would decay an offset whose partner was
                    // never applied — the body cutting to the truth while the
                    // head swings round behind it.
                    _hasRenderError         = false;
                    _hasRenderRotationError = false;
                    armed                   = false;
                    return target;
                }

                _renderError    = new Vector3(ex, ey, ez);
                _hasRenderError = true;
            }

            if (!_hasRenderError) return target;

            // ⛔ Time.unscaledTimeAsDouble, not renderTime.  The absorber is a
            // display device with a fixed real-time window; renderTime is the
            // adaptive controller's cursor and it MOVES — the controller
            // shortening the delay mid-absorption would run the window fast, and
            // lengthening it would run it backwards.
            double s = (Time.unscaledTimeAsDouble - _renderArmedAt)
                       / RenderBlendDurationSeconds;

            // Terminates, and terminates on every pathology as well as on the
            // ordinary path.  `!(s >= 0.0)` is false only for a real
            // non-negative number, so it catches a NaN (a zero window, an
            // uninitialised clock) and a clock that ran backwards; `s >= 1.0`
            // ends the window.  Either way the absorption is dropped and the
            // resolver's own pose is rendered.
            if (!(s >= 0.0) || s >= 1.0)
            {
                _hasRenderError = false;
                return target;
            }

            float w = RenderBlend.Weight((float)s);
            return new Vector3(
                target.x + _renderError.x * w,
                target.y + _renderError.y * w,
                target.z + _renderError.z * w);
        }

        // Render the resolver's facing with any facing error this component owes
        // the screen decayed across the SAME window the position error uses.
        //
        // ⛔ It does not decide for itself that a correction happened.  The arm
        // is taken once from the resolver's target sequence and handed to both
        // halves, so one scalar still answers one question: a second trigger
        // measuring facing would be a second answer to "did the resolver jump",
        // free to disagree with the first, and the only failure this mechanism
        // has ever had came from a trigger asked two questions at once.
        //
        // 🔑 The offset is captured ONCE per arm, from the facing actually on
        // screen at that instant.  Recomputing it each frame would measure the
        // blend's own output and decay nothing.
        private Quaternion BlendRenderedRotation(Quaternion target, bool armed)
        {
            if (armed)
            {
                // Nothing of ours is on screen for this life, so there is no
                // facing to be continuous with — the same CUT the position path
                // takes, and for the same reason.
                _hasRenderRotationError =
                    _hasLastRenderedRotation && HasDirection(target);

                if (_hasRenderRotationError)
                {
                    _renderRotationError =
                        _lastRenderedRotation * Quaternion.Inverse(target);
                }
            }

            if (!_hasRenderRotationError) return target;

            double s = (Time.unscaledTimeAsDouble - _renderArmedAt)
                       / RenderBlendDurationSeconds;
            if (!(s >= 0.0) || s >= 1.0)
            {
                _hasRenderRotationError = false;
                return target;
            }

            // Slerp from the resolver's facing toward the pose the error
            // describes, on the weight the position error decays by, so the two
            // halves of one correction land together.
            float w = RenderBlend.Weight((float)s);
            return Quaternion.Slerp(target, _renderRotationError * target, w);
        }

        // Whether a rotation names a direction an offset can be measured
        // against.  Quaternion.Inverse is defined for unit quaternions.
        //
        // ⛔ Not a wire defence, and it must not be read as one: a rotation that
        // arrives over the network has already been through
        // TransformPacketParser, which refuses |q|² outside [0.9, 1.1] and
        // renormalises what it accepts, and the sender's WireQuaternion.ForWire
        // answers identity for a degenerate value rather than sending it.  What
        // this covers is the SDK's own public surface: AddState takes a
        // TransformState from application code, and the only check on that path
        // is IsFiniteSnapshot, which admits every finite quaternion including
        // the all-zero one.  Such a value is still rendered — withholding what
        // the resolver produced would be a larger change than the absorption it
        // is withheld for — but it is neither the baseline nor the target of
        // one.  NaN answers false through the comparisons rather than through a
        // test of its own.
        private static bool HasDirection(Quaternion q)
        {
            double n = (double)q.x * q.x + (double)q.y * q.y
                     + (double)q.z * q.z + (double)q.w * q.w;
            return n > 1e-6 && n < 1e6;
        }

        // Squared distance in double precision, componentwise.  Squared so the
        // trigger comparison needs no square root, and double so a large
        // teleport delta squared cannot leave the float mantissa's exact range —
        // the same discipline RemoteMotionGate.ClampPositionStep states.
        private static double DistanceSquared(Vector3 a, Vector3 b)
        {
            double dx = (double)a.x - b.x;
            double dy = (double)a.y - b.y;
            double dz = (double)a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
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
            float  maxExtrapolationSeconds = 0.05f,
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
                // A fresh fixture is a replica: the render path is what these
                // tests exercise, and an inherited ownership predicate would
                // silence it.
                _locallyOwned = false;
                // And it has rendered nothing, so it owes the screen no pose.
                ResetRenderBlend();
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

        /// <summary>
        /// Drive one frame of the render path.  <b>For unit tests only.</b>
        ///
        /// <c>Update</c> is private, as Unity requires, so which resolver owns a
        /// frame — and whether the frame writes the transform at all — is
        /// otherwise reachable only from the Unity runtime.  The three resolvers
        /// answer for themselves; that they are consulted, and by whom, is what
        /// this seam lets a fixture observe.
        /// </summary>
        internal void InvokeUpdateForTest() => Update();

        /// <summary>
        /// Which resolver answered the last frame that wrote a pose, or
        /// <see cref="RenderSource.None"/> for a frame that wrote none.
        /// <b>For unit tests only.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// The control for every sequence assertion over the render path: without
        /// it, which resolver owned a frame is inferred from the coordinates it
        /// produced, and an inference cannot tell a resolver handover apart from
        /// the motion it was supposed to absorb.
        /// </para>
        /// <para>
        /// ⛔ Which resolver ANSWERED, not which one wrote a pose.  It is
        /// recorded for every applied frame, because the trigger it feeds is a
        /// question about the resolver; a frame whose axis gates are all shut
        /// applies nothing to the transform and still reports the resolver that
        /// produced what it declined to write.  <c>RenderSource.None</c> means no
        /// resolver answered at all.
        /// </para>
        /// </remarks>
        internal RenderSource LastRenderSourceForTest => _lastRenderSource;

        /// <summary>
        /// Whether an absorption window is open. <b>For unit tests only.</b>
        /// </summary>
        internal bool BlendInFlightForTest => _hasRenderError;

        /// <summary>
        /// Whether the facing half of an absorption is open.
        /// <b>For unit tests only.</b>
        /// </summary>
        /// <remarks>
        /// Read separately from <see cref="BlendInFlightForTest"/> because the
        /// two halves are owed to different things: a replica may render a
        /// facing the wire drives while its position is local business, and
        /// there the position half is never open at all.
        /// </remarks>
        internal bool RotationBlendInFlightForTest => _hasRenderRotationError;
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
