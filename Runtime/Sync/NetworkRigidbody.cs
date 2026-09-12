// RTMPE SDK — Runtime/Sync/NetworkRigidbody.cs
//
// MonoBehaviour component that synchronises a GameObject's 3-D Rigidbody
// physics state (position, rotation, velocity, angular velocity, sleep) over
// the RTMPE network.
//
// ── Architecture ──────────────────────────────────────────────────────────────
//
//  Owner (authoritative physics):
//    FixedUpdate() captures the Rigidbody state each physics step.
//    When any enabled field exceeds its send threshold AND the configured
//    send-rate interval has elapsed, a PhysicsSync payload is built and
//    transmitted as PacketType.PhysicsSync (0x45).  No Sync Service
//    consumer ingests rigidbody state yet; the gateway validates the frame
//    and drops it, so remote replicas are not driven by this path today.
//
//  Non-owner (remote simulation):
//    ApplyRemoteState() is called by NetworkManager when a physics-sync packet
//    arrives for this object.  The received state is stored and applied each
//    FixedUpdate():
//      • MovePosition / MoveRotation smooth the body toward the authoritative
//        position, respecting physics constraints (no teleport through colliders).
//      • The received linear velocity is blended onto the Rigidbody so the
//        physics engine continues simulating between packets (no rubber-banding).
//      • Dead reckoning extrapolates the expected position using the last known
//        velocity for up to _deadReckoningTimeout seconds while no new packet
//        arrives, preventing objects from stopping mid-air between ticks.
//      • If the position error exceeds _snapThreshold, MovePosition snaps
//        immediately instead of lerping, correcting large desync without
//        prolonged visual drift.
//      • When IsSleeping is received, the remote body is put to sleep to stop
//        physics noise on stationary objects.
//
//  Owner reconciliation:
//    When the Sync Engine broadcasts the owner's own state back (because all
//    room members receive each tick), ApplyReconciliation() can optionally
//    apply a server-corrected position.  Currently a no-op placeholder — the
//    owner's local physics simulation is authoritative.
//
// ── Threading ─────────────────────────────────────────────────────────────────
//
//  All methods run on the Unity main thread.  ApplyRemoteState() is called from
//  NetworkManager (main thread) and stores state into _receivedState, which
//  FixedUpdate() reads on the next physics step (also main thread).  No lock is
//  required because both sites execute on the same thread.
//
// ── Design decisions ──────────────────────────────────────────────────────────
//
//  • Uses FixedUpdate (physics timestep) for both capture and application so
//    that Rigidbody.velocity and .angularVelocity reflect the physics engine's
//    actual state, not a mid-frame snapshot.
//  • MovePosition / MoveRotation are preferred over direct position / rotation
//    assignment on non-kinematic bodies: they participate in collision detection
//    within the physics step, preventing tunnelling.
//  • _makeRemoteKinematic: when true, the Rigidbody on non-owner clients is set
//    to kinematic on spawn, and position/rotation are applied directly.  This is
//    appropriate when the owner fully controls the body (e.g. a player character)
//    and remote clients should never simulate physics for it.
//  • Send rate defaults to 20 Hz (below the 30 Hz Sync Engine tick) to reduce
//    bandwidth while still producing smooth results with dead reckoning.
//  • Change detection uses per-field thresholds to suppress sends for micro-
//    movements (floating-point noise, sleep vibrations) that would waste bandwidth.
//  • Sleep state is only included in the payload when it changes, reducing the
//    common-case payload size for active objects.

using System.Buffers;

using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync.Internal;

namespace RTMPE.Sync
{
    /// <summary>
    /// Synchronises a 3-D <see cref="UnityEngine.Rigidbody"/> over the RTMPE network.
    /// <para>
    /// Attach alongside a <see cref="NetworkBehaviour"/> subclass on any prefab
    /// that is spawned via <c>SpawnManager</c> and driven by Unity physics.
    /// </para>
    /// <para>
    /// The owner client simulates physics and sends state updates. Non-owner
    /// clients apply what they receive, smoothly correcting their local
    /// Rigidbody using velocity blending, dead reckoning and position lerp.
    /// </para>
    /// <para>
    /// ⚠️ <b>Not driven end to end yet.</b> The frames this component sends are
    /// <c>PacketType.PhysicsSync</c> (0x45); the gateway validates one and then
    /// DROPS it, because no Sync Service consumer ingests rigidbody state and
    /// the tick engine models transforms only. So the correction path above is
    /// implemented, tested and reached by nothing — a remote replica is not
    /// driven by this component today. Use
    /// <see cref="NetworkTransform"/> for anything a peer must actually see, and
    /// treat this as owner-side capture plus a receive path waiting for its
    /// server half.
    /// </para>
    /// </summary>
    [AddComponentMenu("RTMPE/Network Rigidbody")]
    [RequireComponent(typeof(Rigidbody))]
    public class NetworkRigidbody : NetworkBehaviour
    {
        // Reconciliation runs once per inbound physics correction, at the
        // server's tick rate, and each of the four refusals below is decided
        // from that packet's own numbers — so an operator whose server is
        // misbehaving used to get one console line per correction, per body.
        // The fifth is the spawn-time configuration error, which a room
        // admitting a hundred objects reports a hundred times.
        //
        // Static: a scene holds many of these components and one frame corrects
        // them all, so a per-component budget would bound none of it.
        private static long _lastRigidbodyMissingWarnTicks;
        private static long _lastNonFiniteServerPositionWarnTicks;
        private static long _lastCorrectionCapWarnTicks;
        private static long _lastWorldBoundsWarnTicks;
        private static long _lastNonFiniteServerRotationWarnTicks;

        // ── Inspector — Sync toggles ───────────────────────────────────────────

        [Header("Sync Fields")]
        [Tooltip("Synchronise world-space position.")]
        [SerializeField] private bool _syncPosition = true;

        [Tooltip("Synchronise world-space rotation.")]
        [SerializeField] private bool _syncRotation = true;

        [Tooltip("Synchronise linear velocity so remote bodies continue moving between packets.")]
        [SerializeField] private bool _syncVelocity = true;

        [Tooltip("Synchronise angular velocity so remote bodies continue spinning between packets.")]
        [SerializeField] private bool _syncAngularVelocity = true;

        [Tooltip("Synchronise sleep state so remote bodies idle when the owner body is asleep.")]
        [SerializeField] private bool _syncSleepState = true;

        [Tooltip("Synchronise the RigidbodyConstraints bitmask so runtime constraint changes " +
                 "(e.g. freezing an axis after a ragdoll lands) propagate to remote bodies.")]
        [SerializeField] private bool _syncConstraints = true;

        // ── Inspector — Send thresholds ────────────────────────────────────────

        [Header("Send Thresholds")]
        [Tooltip("Minimum position change in world units since last send before an update is sent.")]
        [SerializeField] private float _positionThreshold = 0.01f;

        [Tooltip("Minimum rotation change in degrees since last send before an update is sent.")]
        [SerializeField] private float _rotationThreshold = 0.1f;

        [Tooltip("Minimum linear velocity change (magnitude) in units/second before an update is sent.")]
        [SerializeField] private float _velocityThreshold = 0.05f;

        [Tooltip("Minimum angular velocity change (magnitude) in radians/second before an update is sent.")]
        [SerializeField] private float _angularVelocityThreshold = 0.05f;

        // ── Inspector — Remote body ────────────────────────────────────────────

        [Header("Remote Body Behaviour")]
        [Tooltip("When true, the Rigidbody on non-owner clients is set kinematic on spawn. " +
                 "Position and rotation are then applied directly without MovePosition/MoveRotation. " +
                 "Use for player characters and objects whose physics are fully owner-authoritative.")]
        [SerializeField] private bool _makeRemoteKinematic = false;

        [Tooltip("Position error in world units above which the remote body snaps immediately " +
                 "to the authoritative position instead of lerping.")]
        [SerializeField] private float _snapThreshold = 3.0f;

        [Tooltip("Speed at which the remote body lerps toward the authoritative position. " +
                 "Higher values produce tighter following at the cost of visible correction steps.")]
        [SerializeField] [Range(1f, 50f)] private float _positionCorrectionSpeed = 10f;

        [Tooltip("Speed at which the remote body slerps toward the authoritative rotation.")]
        [SerializeField] [Range(1f, 50f)] private float _rotationCorrectionSpeed = 10f;

        [Tooltip("Convergence rate (per second) for the linear-velocity blend on remote " +
                 "non-kinematic bodies.  Used as the rate constant inside an exponential " +
                 "smoothing filter so the time-to-converge is identical at any physics step.")]
        [SerializeField] [Range(0.1f, 60f)] private float _velocityBlendRate = 10f;

        [Tooltip("Convergence rate (per second) for the angular-velocity blend on remote " +
                 "non-kinematic bodies.  Same exponential-smoothing semantics as " +
                 "_velocityBlendRate.")]
        [SerializeField] [Range(0.1f, 60f)] private float _angularVelocityBlendRate = 10f;

        [Tooltip("Position-error magnitude (world units) above which a non-kinematic " +
                 "remote body is teleported by direct .position assignment instead of " +
                 "being driven through MovePosition.  MovePosition sweeps the body " +
                 "through colliders for the upcoming physics step; on a large server " +
                 "correction the sweep can clamp short against geometry and leave the " +
                 "body desynced.")]
        [SerializeField] [Range(0.1f, 50f)] private float _snapDistanceThreshold = 1.5f;

        // ── Inspector — Owner reconciliation ───────────────────────────────────

        [Header("Owner Reconciliation")]
        [Tooltip("When true, the OWNER reconciles its local physics against the server-broadcast " +
                 "state.  Defensive snap-on-divergence: when the local position diverges from the " +
                 "server-confirmed position by more than _ownerReconcileSnapThreshold, the body " +
                 "snaps to the server position.  Below the threshold the local prediction is kept " +
                 "(no visual pop).  Disable for trusted-client deployments where the owner is fully " +
                 "authoritative; enable when an authoritative server simulates physics and emits " +
                 "corrections.")]
        [SerializeField] private bool _enableOwnerReconciliation = false;

        [Tooltip("Position error threshold (world units) above which the owner snaps to the " +
                 "server-confirmed position.  Set high enough to avoid fighting normal " +
                 "client-side prediction noise.")]
        [SerializeField] [Range(0.5f, 20f)] private float _ownerReconcileSnapThreshold = 3.0f;

        [Tooltip("Rotation error threshold (degrees) above which the owner snaps to the " +
                 "server-confirmed rotation.  Defaults to 30°, matching the legacy " +
                 "behaviour; titles with tight reconciliation tolerances (competitive " +
                 "shooters) typically lower this; titles with loose tolerances (casual " +
                 "ragdoll) raise it.  Tune in tandem with the position threshold.")]
        [SerializeField] [Range(1f, 180f)] private float _ownerReconcileRotationSnapDegrees = 30.0f;

        // ── Inspector — Dead reckoning ─────────────────────────────────────────

        [Header("Dead Reckoning")]
        [Tooltip("When true, the remote body's expected position is extrapolated using the " +
                 "last received velocity while no new packet has arrived.  Prevents objects " +
                 "from snapping to their last confirmed position between ticks.")]
        [SerializeField] private bool _enableDeadReckoning = true;

        [Tooltip("Seconds after the last packet after which dead reckoning stops. " +
                 "After this timeout the object is held at its last extrapolated position " +
                 "until a new packet arrives.")]
        [SerializeField] [Range(0.1f, 2.0f)] private float _deadReckoningTimeout = 0.5f;

        // ── Inspector — Send rate ──────────────────────────────────────────────

        [Header("Send Rate")]
        [Tooltip("How many times per second the owner sends a physics-state update. " +
                 "Values above 30 are clamped by the Sync Engine tick rate. " +
                 "20 Hz is the recommended default (balances bandwidth vs. smoothness).")]
        [SerializeField] [Range(1, 30)] private int _sendRateHz = 20;

        // ── Runtime state ──────────────────────────────────────────────────────

        private Rigidbody _rb;

        // Owner side: baseline for change detection.
        private PhysicsState _lastSentState;
        // Tracks whether any state has been sent yet (skips threshold on first send).
        private bool _hasSentOnce;
        // Tracks whether sleep state changed since last send (forces a send when it does).
        private bool _lastSleepState;
        // Last constraint mask sent (used to detect change-and-send-once semantics).
        private byte _lastSentConstraints;
        // Accumulator for send rate limiting.
        private float _sendAccum;

        // Non-owner side: latest state received from the network.
        private PhysicsState _receivedState;
        // Timestamp (Time.fixedTime) when the last packet was received.
        private float _lastReceiveTime;
        // True once at least one state has been received.
        private bool _hasReceivedState;
        // Last constraint mask actually applied to the local Rigidbody so we
        // only assign _rb.constraints when it changes (no-op writes are cheap
        // but apt to confuse Unity's PhysX cache).
        private byte _appliedConstraints;
        // True once the receive side has observed at least one ConstraintMask
        // field — until then we never touch _rb.constraints (avoids stamping
        // RigidbodyConstraints.None over an inspector-configured default).
        private bool _hasReceivedConstraints;

        // ── Receive-side hardening ─────────────────────────────────────────────
        //
       // Token-bucket rate limit for inbound physics packets, per object.  A
        // hostile peer or compromised server cannot drive PhysX state changes
        // faster than NetworkSettings.maxPhysicsPacketsPerSecond / second.
        private float _rateBucketTokens;
        private float _rateBucketLastTime;

        // True once a position has been accepted, so the per-tick delta cap
        // has a reference to compare against.
        private bool _hasAppliedPosition;

        // ── Owner-reconciliation deferral ──────────────────────────────────────
        //
        // Physics body state must be written aligned with the FixedUpdate
        // cadence; writing in the Update path (HandlePhysicsSyncPacket → this
        // component) with two FixedUpdates per Update at 60 fps physics + 30 fps
        // rendering produces a visible lurch every other physics step as the
        // body teleports mid-Update and then runs one extra integration step
        // before the next render.  Capture the desired correction here and
        // apply it from FixedUpdate so the write lands exactly once per
        // physics tick — the LCM beat goes away.
        //
        // Newer-wins (intentional): if multiple reconciliation packets arrive
        // between two FixedUpdate beats (e.g. when Update runs faster than
        // physics or under frame hitch), only the most recent pending snap is
        // kept.  This is correct because the most recent server state supersedes
        // all earlier states in the same reconciliation window; applying an older
        // snap followed immediately by a newer snap would produce a visible
        // double-teleport artifact.  The single-slot design is a deliberate
        // trade-off: it eliminates the artifact at the cost of discarding
        // intermediate snaps that would have been overwritten anyway.
        private Vector3    _pendingOwnerSnapPosition;
        private bool       _hasPendingOwnerSnapPosition;
        private Quaternion _pendingOwnerSnapRotation;
        private bool       _hasPendingOwnerSnapRotation;

        // What the scene authored, and whether the SDK is currently standing on
        // top of it.  Forcing a replica kinematic is a statement about who is
        // driving the body, so it has to be withdrawn by every event that
        // changes the answer; without that withdrawal an object handed to this
        // client mid-session stays frozen for the rest of its life.
        private bool _authoredKinematic;
        private bool _forcedKinematic;

        /// <summary>
        /// Apply, or withdraw, the kinematic mode a replica's body is held in.
        /// Called from the ownership pipeline on both events that can change
        /// the answer: a server-confirmed handover, and the start of a spawn
        /// cycle.
        /// </summary>
        internal void ReconcileKinematicToOwnership(bool locallyOwned)
        {
            // On a fresh instance the spawn path reaches this before
            // OnNetworkSpawn has resolved the body, and there is nothing to do.
            // On a pooled one there is: OnNetworkDespawn gives the body back but
            // does not forget it, so _rb is still live from the previous life
            // and this call captures and forces against it.  That is harmless
            // rather than correct-by-accident — OnNetworkSpawn restores,
            // re-captures and re-applies a few lines later, so the state this
            // leaves is replaced before anything reads it.
            if (_rb == null) return;

            // ⛔ Only the FORCE limb is gated on the setting.  Guarding the
            // restore with it too meant a game that turned
            // makeRemoteKinematic off at runtime while a body was held
            // wedged it with no path back — taking and giving back cannot
            // both depend on a flag that may change in between.
            if (locallyOwned) { RestoreAuthoredKinematic(); return; }

            if (!_makeRemoteKinematic || _forcedKinematic) return;
            _authoredKinematic = _rb.isKinematic;
            _forcedKinematic   = true;
            _rb.isKinematic    = true;
        }

        // Give the body back exactly what the scene had, never a constant: a
        // prefab authored kinematic must stay kinematic when this client takes
        // it, and writing `false` here would start simulating a body the game
        // never intended to simulate.
        private void RestoreAuthoredKinematic()
        {
            if (!_forcedKinematic) return;

            // The latch is dropped whether or not there is still a body to give
            // back to.  A game that destroys and re-adds the Rigidbody while one
            // is held would otherwise leave it raised over a body that no longer
            // exists, and the next spawn would write the OLD body's authored
            // value onto the new one.
            _forcedKinematic = false;
            if (_rb == null) return;
            _rb.isKinematic = _authoredKinematic;
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Cache the Rigidbody reference and apply kinematic mode to remote bodies.
        /// Called by the SDK after <c>NetworkBehaviour.SetSpawned(true)</c>.
        /// </summary>
        protected override void OnNetworkSpawn()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRigidbodyMissingWarnTicks))
                    Debug.LogError("[RTMPE] NetworkRigidbody.OnNetworkSpawn: " +
                                   "no Rigidbody found on this GameObject.", this);
                return;
            }

            // Restore before capturing.  A pooled instance re-enters this
            // method with the body still forced from its previous life, so
            // reading isKinematic first would record the SDK's own value as
            // the one the scene authored, and the object could never be
            // simulated again.
            RestoreAuthoredKinematic();
            _authoredKinematic = _rb.isKinematic;
            ReconcileKinematicToOwnership(IsOwner);

            if (IsOwner)
            {
                // Capture spawn state as the send baseline so the first FixedUpdate
                // compares against the actual spawn transform, not default zeroes.
                _lastSentState         = GetState();
                _lastSleepState        = _rb.IsSleeping();
                _lastSentConstraints   = _lastSentState.ConstraintMask;
                _hasSentOnce           = false;
            }

            // Receive-side constraint application bookkeeping.  Initialise to
            // the local Rigidbody's current constraints so the "change-detect
            // before assign" guard works correctly for the first received mask.
            _appliedConstraints     = (byte)(int)_rb.constraints;
            _hasReceivedConstraints = false;

            // Pre-fill the rate-limit bucket so the first inbound packet
            // immediately after spawn is admitted; the bucket then refills at
            // maxPhysicsPacketsPerSecond / second.  Capacity is bounded by the
            // configured rate so a sleeping object cannot accumulate burst
            // tokens beyond a single second's worth.
            var settings0 = NetworkManager.Instance?.Settings;
            float capacity0 = settings0 != null ? Mathf.Max(0f, settings0.maxPhysicsPacketsPerSecond) : 0f;
            _rateBucketTokens   = capacity0;
            _rateBucketLastTime = Time.fixedTime;
            _hasAppliedPosition = false;

            _sendAccum = 0f;

            // Pending owner-reconciliation buffer is per-spawn lifecycle: a
            // re-spawn must not carry an unconsumed snap from a previous run.
            _hasPendingOwnerSnapPosition = false;
            _hasPendingOwnerSnapRotation = false;
        }

        /// <summary>
        /// Give the body back its authored kinematic mode when the object
        /// leaves the network, so an instance returned to a pool — or one a
        /// game keeps using as non-networked debris — is not handed back
        /// frozen.
        /// </summary>
        protected override void OnNetworkDespawn()
        {
            RestoreAuthoredKinematic();
            _hasReceivedState = false;
            _hasSentOnce = false;
            _hasPendingOwnerSnapPosition = false;
            _hasPendingOwnerSnapRotation = false;
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || _rb == null) return;

            if (IsOwner)
                OwnerFixedUpdate();
            else
                RemoteFixedUpdate();
        }

        // ── Owner update ───────────────────────────────────────────────────────

        private void OwnerFixedUpdate()
        {
            // ── Drain queued reconciliation writes ────────────────────────────
            // ApplyReconciliation runs on the main thread / Update path when a
            // server-broadcast state arrives; mutating _rb.position directly
            // there causes lurches at the LCM of the Update / FixedUpdate
            // clocks (one teleport plus an extra integration step every Update
            // beat).  Queueing the target and applying it here aligns every
            // owner-side write with PhysX exactly once per physics step.
            if (_hasPendingOwnerSnapPosition)
            {
                _rb.position                 = _pendingOwnerSnapPosition;
                _hasPendingOwnerSnapPosition = false;
            }
            if (_hasPendingOwnerSnapRotation)
            {
                if (_makeRemoteKinematic) _rb.rotation = _pendingOwnerSnapRotation;
                else                      _rb.MoveRotation(_pendingOwnerSnapRotation);
                _hasPendingOwnerSnapRotation = false;
            }

            _sendAccum += Time.fixedDeltaTime;
            float sendInterval = 1f / _sendRateHz;
            if (_sendAccum < sendInterval) return;
            _sendAccum -= sendInterval;

            var current = GetState();

            // Build the data-field mask from fields that have changed beyond thresholds.
            byte dataMask = BuildChangedMask(current);

            // On the very first send, transmit all enabled fields regardless of thresholds
            // so remote clients receive an initial full state snapshot.
            // 🚨 `_hasSentOnce` is NOT set here. It records that a full-state
            // snapshot was BROADCAST, and the encode below can refuse — the
            // non-finite path added alongside the wire-coordinate contract. A
            // body whose first tick carried a non-finite value used to burn the
            // flag on a send that never went out, and every tick after it built
            // a CHANGED mask against `_lastSentState == default`: a body that
            // then recovered to within the position threshold of the origin
            // never broadcast its position at all, and velocity, angular
            // velocity and a zero constraint mask never got their bits set
            // either. Set with the rest of the "what was sent" bookkeeping,
            // after the send.
            if (!_hasSentOnce) dataMask = BuildFullMask();

            if (dataMask == 0x00) return; // nothing to send

            var manager = NetworkManager.Instance;
            if (manager == null) return;

            // Pooled physics send path (GC Round 2, 2026-05-02).
            // Compute exact size, rent, write into the buffer, send only the
            // written bytes, return.  ArrayPool.Rent may give a larger
            // buffer; passing `size` explicitly to SendPhysicsSync caps the
            // wire frame's payload_len to the bytes we actually wrote.
            int size   = PhysicsPacketBuilder.ComputePayloadSize(dataMask, twoDee: false);
            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(size);
            try
            {
                int written = PhysicsPacketBuilder.BuildPayloadInto(
                    buffer, 0, NetworkObjectId, current, dataMask);
                // The encoder refused: a field this mask selects is
                // non-finite, and it has already said which, once a second.
                //
                // 🔑 The return skips the bookkeeping below, not the send —
                // SendPhysicsSync already ignores a non-positive length.  That
                // bookkeeping is where the real damage was: `_lastSentState`
                // becomes the baseline every threshold below is measured
                // against, and a comparison involving a NaN is false, so
                // recording a diverged state would stop the AFFECTED FIELDS
                // broadcasting for the rest of this rigidbody's life — including
                // after the physics recovered — while the others carried on.
                // Leaving the last state that actually went out in place makes
                // the recovery a change again.
                //
                // ⚠️ Only `BuildFullMask()` can reach here, for the same reason:
                // BuildChangedMask compares against `_lastSentState` and a NaN
                // comparison is false, so a field that has just diverged never
                // gets its bit set.  That makes this the FIRST send after spawn
                // — which is also the one whose baseline everything after it is
                // measured against.
                if (written == 0) return;

                manager.SendPhysicsSync(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }

            _lastSentState       = current;
            _lastSleepState      = current.IsSleeping;
            _lastSentConstraints = current.ConstraintMask;
            _hasSentOnce         = true;
        }

        private byte BuildChangedMask(PhysicsState current)
        {
            byte mask = 0;

            if (_syncPosition && Vector3.Distance(current.Position, _lastSentState.Position) > _positionThreshold)
                mask |= PhysicsPacketBuilder.ChangedPosition;

            if (_syncRotation && Quaternion.Angle(current.Rotation, _lastSentState.Rotation) > _rotationThreshold)
                mask |= PhysicsPacketBuilder.ChangedRotation;

            if (_syncVelocity && (current.Velocity - _lastSentState.Velocity).magnitude > _velocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedVelocity;

            if (_syncAngularVelocity && (current.AngularVelocity - _lastSentState.AngularVelocity).magnitude > _angularVelocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedAngularVelocity;

            // Sleep state: only include when it has changed since last send.
            if (_syncSleepState && current.IsSleeping != _lastSleepState)
                mask |= PhysicsPacketBuilder.ChangedSleep;

            // Constraints: only include when the bitmask changed since last send.
            // Static constraint configurations therefore pay zero per-tick bandwidth.
            if (_syncConstraints && current.ConstraintMask != _lastSentConstraints)
                mask |= PhysicsPacketBuilder.ChangedConstraints;

            return mask;
        }

        private byte BuildFullMask()
        {
            byte mask = 0;
            if (_syncPosition)        mask |= PhysicsPacketBuilder.ChangedPosition;
            if (_syncRotation)        mask |= PhysicsPacketBuilder.ChangedRotation;
            if (_syncVelocity)        mask |= PhysicsPacketBuilder.ChangedVelocity;
            if (_syncAngularVelocity) mask |= PhysicsPacketBuilder.ChangedAngularVelocity;
            if (_syncSleepState)      mask |= PhysicsPacketBuilder.ChangedSleep;
            if (_syncConstraints)     mask |= PhysicsPacketBuilder.ChangedConstraints;
            return mask;
        }

        // ── Non-owner update ───────────────────────────────────────────────────

        private void RemoteFixedUpdate()
        {
            if (!_hasReceivedState) return;

            // ── Constraint application ────────────────────────────────────────
            // Apply the authoritative constraint mask once per change.  Done
            // BEFORE sleep/position/rotation logic because freezing an axis
            // affects how MovePosition / MoveRotation behave for the rest of
            // this physics step.
            if (_syncConstraints && _hasReceivedConstraints
                && _receivedState.ConstraintMask != _appliedConstraints)
            {
                _rb.constraints     = (RigidbodyConstraints)_receivedState.ConstraintMask;
                _appliedConstraints = _receivedState.ConstraintMask;
            }

            // ── Sleep handling ────────────────────────────────────────────────
            if (_syncSleepState && _receivedState.IsSleeping)
            {
                if (!_rb.IsSleeping()) _rb.Sleep();
                return; // sleeping body needs no correction
            }
            if (_rb.IsSleeping()) _rb.WakeUp();

            float timeSincePacket = Time.fixedTime - _lastReceiveTime;

            // ── Dead reckoning: project expected position forward ─────────────
            Vector3 targetPos = _receivedState.Position;
            if (_enableDeadReckoning && timeSincePacket < _deadReckoningTimeout && _syncVelocity)
                targetPos = _receivedState.Position + _receivedState.Velocity * timeSincePacket;

            // Frame-rate-independent blend factor.  1 - exp(-rate·dt) yields the
            // same time-to-converge at any physics step (50 / 60 / 120 Hz) where
            // the naive `dt * coefficient` Lerp would silently accelerate as the
            // step shrinks.
            float dt        = Time.fixedDeltaTime;
            float posBlend  = 1f - Mathf.Exp(-_positionCorrectionSpeed * dt);
            float rotBlend  = 1f - Mathf.Exp(-_rotationCorrectionSpeed * dt);
            float velBlend  = 1f - Mathf.Exp(-_velocityBlendRate         * dt);
            float angBlend  = 1f - Mathf.Exp(-_angularVelocityBlendRate  * dt);

            // ── Position correction ───────────────────────────────────────────
            if (_syncPosition)
            {
                float posError = Vector3.Distance(_rb.position, targetPos);
                if (posError > _snapThreshold)
                {
                    // Large error: teleport to the authoritative position.  On a
                    // dynamic body, MovePosition runs a sweep test for the next
                    // physics step and will clamp short against any collider in
                    // the path of a multi-metre correction, leaving the remote
                    // visibly desynced.  Direct .position assignment skips the
                    // sweep so the snap actually lands.
                    ApplySnapPosition(targetPos);
                }
                else
                {
                    // Small error: blend smoothly toward the projected position
                    // using exponential smoothing so the convergence rate is
                    // identical at any physics step.
                    Vector3 corrected = Vector3.Lerp(_rb.position, targetPos, posBlend);
                    if (_makeRemoteKinematic)
                        _rb.position = corrected;
                    else if (Vector3.Distance(_rb.position, corrected) > _snapDistanceThreshold)
                        // Even a "small-error" branch can produce a per-step
                        // motion larger than the sweep budget after a long
                        // hitch; route those through the bypass too.
                        _rb.position = corrected;
                    else
                        _rb.MovePosition(corrected);
                }
            }

            // ── Rotation correction ────────────────────────────────────────────
            if (_syncRotation)
            {
                Quaternion corrected = Quaternion.Slerp(
                    _rb.rotation, _receivedState.Rotation, rotBlend);
                if (_makeRemoteKinematic)
                    _rb.rotation = corrected;
                else
                    _rb.MoveRotation(corrected);
            }

            // ── Velocity blending (non-kinematic only) ─────────────────────────
            // Blending velocity (not snapping) avoids visual lurching when packets
            // arrive slightly out of order.  The physics engine continues to simulate
            // with this velocity between packets, producing natural-looking movement.
            if (!_makeRemoteKinematic)
            {
                if (_syncVelocity)
                    _rb.SetLinearVelocity(Vector3.Lerp(
                        _rb.GetLinearVelocity(), _receivedState.Velocity, velBlend));

                if (_syncAngularVelocity)
                    _rb.angularVelocity = Vector3.Lerp(
                        _rb.angularVelocity, _receivedState.AngularVelocity, angBlend);
            }
        }

        // Teleport the remote body to <paramref name="targetPos"/>, preserving
        // the most recent linear velocity on a non-kinematic body so the
        // physics simulation continues smoothly from the new origin instead of
        // dropping to zero motion the instant the snap lands.  Direct
        // .position assignment bypasses the upcoming-step sweep that
        // MovePosition performs on dynamic bodies; required when the
        // correction distance exceeds the normal interpolation budget.
        private void ApplySnapPosition(Vector3 targetPos)
        {
            if (_makeRemoteKinematic)
            {
                _rb.position = targetPos;
                return;
            }

            // Non-kinematic: assign position directly so PhysX moves the body
            // without a discrete-step sweep.  Linear velocity is preserved
            // from the latest accepted packet so simulation continues
            // naturally; the velocity blend below smooths any divergence.
            _rb.position = targetPos;
            if (_syncVelocity)
                _rb.SetLinearVelocity(_receivedState.Velocity);
        }

        // ── Internal API (called by NetworkManager) ────────────────────────────

        /// <summary>
        /// Apply an incoming physics-state update from a remote owner.
        /// Called by <c>NetworkManager.HandlePhysicsSyncPacket</c> on non-owner clients.
        /// </summary>
        /// <param name="incoming">Decoded physics snapshot.</param>
        /// <param name="changedMask">Bit-mask indicating which fields are valid.</param>
        internal void ApplyRemoteState(PhysicsState incoming, byte changedMask)
        {
            var settings = NetworkManager.Instance?.Settings;

            // ── Componentwise finiteness gate ─────────────────────────────────
            // Reject ANY inbound packet whose vector or quaternion fields
            // carry NaN / +Inf / -Inf in a sync field that this packet
            // claims to update.  PhysX has been observed to enter an
            // unrecoverable state on a single non-finite assignment to
            // Rigidbody.position / .velocity (the body either disappears
            // from the simulation or stops responding to forces); the
            // plausibility caps further down compare with `>` operators that
            // short-circuit to false for NaN, so the explicit IsFinite gate is
            // the only authoritative defence.
            //
            // Ahead of the token bucket, as in the 2-D component: a packet that
            // cannot be applied should not spend the budget that admits the
            // ones that can, or a flood of malformed frames throttles the
            // legitimate owner without ever changing any state.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                && !IsFiniteVector(incoming.Position)) return;
            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                && !IsFiniteVector(incoming.Velocity)) return;
            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                && !IsFiniteVector(incoming.AngularVelocity)) return;
            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0
                && !IsFiniteQuaternion(incoming.Rotation)) return;

            // ── Per-object inbound rate limit ─────────────────────────────────
            // Token-bucket: refill at maxPhysicsPacketsPerSecond per second,
            // capped at one second's worth.  Each accepted packet costs one
            // token.  When the bucket is empty the packet is silently dropped
            // — never resets the bucket via attacker-controlled input, so a
            // flood cannot reset the counter to its own benefit.
            if (settings != null && settings.maxPhysicsPacketsPerSecond > 0f)
            {
                float now = Time.fixedTime;
                float dt  = Mathf.Max(0f, now - _rateBucketLastTime);
                _rateBucketLastTime = now;
                float capacity = settings.maxPhysicsPacketsPerSecond;
                _rateBucketTokens = Mathf.Min(capacity,
                    _rateBucketTokens + dt * settings.maxPhysicsPacketsPerSecond);
                if (_rateBucketTokens < 1f) return;
                _rateBucketTokens -= 1f;
            }

            // ── Plausibility caps on velocity / angular velocity ──────────────
            if (settings != null)
            {
                if (settings.maxLinearVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                    && incoming.Velocity.sqrMagnitude
                       > settings.maxLinearVelocity * settings.maxLinearVelocity)
                    return;

                if (settings.maxAngularVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                    && incoming.AngularVelocity.sqrMagnitude
                       > settings.maxAngularVelocity * settings.maxAngularVelocity)
                    return;

                // One step is what this bounds, and one step is all it can
                // bound: a run of steps each within the cap is a body moving,
                // and no measurement of the positions alone separates that from
                // a crafted sequence of the same shape.  A second comparison
                // against a reference held still for N packets adds nothing —
                // N capped steps reach exactly N times the cap — so it can only
                // fire on travel that the cap already allowed.
                if (settings.maxPositionDeltaPerTick > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                    && _hasAppliedPosition
                    && (incoming.Position - _receivedState.Position).sqrMagnitude
                       > settings.maxPositionDeltaPerTick * settings.maxPositionDeltaPerTick)
                    return;
            }

            // Merge the received fields with the current known state so that fields
            // absent from this packet retain their last known values.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                _receivedState.Position = incoming.Position;
                _hasAppliedPosition     = true;
            }

            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
                _receivedState.Rotation = incoming.Rotation;

            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0)
                _receivedState.Velocity = incoming.Velocity;

            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0)
                _receivedState.AngularVelocity = incoming.AngularVelocity;

            if ((changedMask & PhysicsPacketBuilder.ChangedSleep) != 0)
                _receivedState.IsSleeping = incoming.IsSleeping;

            // ConstraintMask: only honoured when AllowDynamicConstraints is true.
            // Bits outside DynamicConstraintsAllowMask are stripped before assignment
            // so a hostile sender cannot toggle constraint bits the host policy
            // disallows (e.g. unfreezing an axis the local design has locked).
            if ((changedMask & PhysicsPacketBuilder.ChangedConstraints) != 0
                && settings != null && settings.allowDynamicConstraints)
            {
                byte allow = (byte)(settings.dynamicConstraintsAllowMask & 0xFF);
                _receivedState.ConstraintMask = (byte)(incoming.ConstraintMask & allow);
                _hasReceivedConstraints       = true;
            }

            _lastReceiveTime  = Time.fixedTime;
            _hasReceivedState = true;
        }

        /// <summary>
        /// Called by <c>NetworkManager</c> when the Sync Engine broadcasts the
        /// owner's own physics state back to all room members.
        ///
       /// <para>When <see cref="_enableOwnerReconciliation"/> is <c>true</c>,
        /// applies a defensive snap if the local body has diverged from the
        /// server-confirmed position by more than
        /// <see cref="_ownerReconcileSnapThreshold"/> world units.  Below the
        /// threshold the local prediction is kept intact, avoiding visual pops
        /// from normal physics noise.</para>
        ///
       /// <para>When <see cref="_enableOwnerReconciliation"/> is <c>false</c>
        /// (the default), this is a no-op — the owner's local physics
        /// simulation is treated as authoritative.  Set to true when an
        /// authoritative server simulates physics (anti-cheat / competitive
        /// modes) and broadcasts corrections back.</para>
        ///
       /// <para>Defensively rejects NaN/Inf positions and non-unit quaternions
        /// from the server payload — a bug or hostile signal will not be
        /// allowed to corrupt local <c>Rigidbody</c> state.</para>
        /// </summary>
        internal void ApplyReconciliation(PhysicsState serverState, byte changedMask)
        {
            if (!_enableOwnerReconciliation || _rb == null || !IsOwner) return;

            var settings = NetworkManager.Instance?.Settings;

            // Position snap on large divergence.
            if (_syncPosition && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                Vector3 sp = serverState.Position;
                if (!IsFiniteVector(sp))
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteServerPositionWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected non-finite " +
                            $"server position {sp} — keeping local state.", this);
                    return;
                }

                float err = Vector3.Distance(_rb.position, sp);

                // ── Server-correction cap & world bounds ─────────────────────
                // Defends against a hostile / compromised server that would
                // otherwise teleport the local Rigidbody to an arbitrary
                // location.  Mirrors NetworkTransform.ApplyReconciliation so
                // the same project-wide tuning governs both transforms and
                // rigidbodies.  Each guard is bypassed when its setting is
                // disabled (0 / false) for back-compat with existing scenes.
                if (settings != null)
                {
                    if (settings.maxServerCorrectionDistance > 0f
                        && err > settings.maxServerCorrectionDistance)
                    {
                        if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastCorrectionCapWarnTicks))
                            Debug.LogWarning(
                                "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected " +
                                $"server correction of {err:F2}m (cap " +
                                $"{settings.maxServerCorrectionDistance:F2}m) — keeping " +
                                "local state.", this);
                        return;
                    }

                    if (settings.worldBoundsEnabled)
                    {
                        Vector3 d = sp - settings.worldBoundsCenter;
                        Vector3 e = settings.worldBoundsExtents;
                        if (Mathf.Abs(d.x) > e.x
                            || Mathf.Abs(d.y) > e.y
                            || Mathf.Abs(d.z) > e.z)
                        {
                            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastWorldBoundsWarnTicks))
                                Debug.LogWarning(
                                    "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected " +
                                    $"server position {sp} outside world bounds — keeping " +
                                    "local state.", this);
                            return;
                        }
                    }
                }

                if (err > _ownerReconcileSnapThreshold)
                {
                    // Queue the snap for the next FixedUpdate so the actual
                    // _rb.position write lands on the physics cadence.  A
                    // direct write here would teleport the body mid-Update,
                    // and PhysX's next integration step (≤16 ms later at
                    // 60 fps physics) would then run on top of the snapped
                    // pose, producing a visible velocity-preserving lurch on
                    // every owner correction beat.
                    _pendingOwnerSnapPosition    = sp;
                    _hasPendingOwnerSnapPosition = true;
                }
            }

            // Rotation snap on large divergence (uses the same snap threshold as
            // position, scaled to degrees via Quaternion.Angle).
            if (_syncRotation && (changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
            {
                Quaternion sr = serverState.Rotation;
                // IsFinite must precede the magnitude band gate: a NaN component
                // makes magSq=NaN, and NaN<0.81 / NaN>1.21 are both false, so the
                // band check would silently pass a corrupt quaternion through.
                if (!IsFiniteQuaternion(sr))
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteServerRotationWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected non-finite " +
                            $"server rotation {sr} — keeping local state.", this);
                    return;
                }
                float magSq = sr.x * sr.x + sr.y * sr.y + sr.z * sr.z + sr.w * sr.w;
                if (magSq < 0.81f || magSq > 1.21f) return; // [0.9², 1.1²] band

                float angleErr = Quaternion.Angle(_rb.rotation, sr);
                if (angleErr > _ownerReconcileRotationSnapDegrees)
                {
                    // Same FixedUpdate-aligned deferral as the position branch
                    // above: a direct write here would land between physics
                    // steps and add a non-deterministic rotational nudge.
                    _pendingOwnerSnapRotation    = sr;
                    _hasPendingOwnerSnapRotation = true;
                }
            }
        }

        private static bool IsFiniteVector(Vector3 v)
            => !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
            && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        // Quaternion finiteness used by ApplyRemoteState's inbound gate.
        // Includes the w component because PhysX may carry a finite x/y/z
        // alongside a NaN w after a divide-by-zero recovery, which Unity
        // would otherwise persist as a corrupt rotation.
        private static bool IsFiniteQuaternion(Quaternion q)
            => !float.IsNaN(q.x) && !float.IsInfinity(q.x)
            && !float.IsNaN(q.y) && !float.IsInfinity(q.y)
            && !float.IsNaN(q.z) && !float.IsInfinity(q.z)
            && !float.IsNaN(q.w) && !float.IsInfinity(q.w);

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Capture the current <see cref="Rigidbody"/> state into a
        /// <see cref="PhysicsState"/> snapshot.  Called from
        /// <see cref="FixedUpdate"/> on the owner client.
        /// </summary>
        public PhysicsState GetState()
        {
            if (_rb == null)
                return default;
            return new PhysicsState
            {
                Position        = _rb.position,
                Rotation        = _rb.rotation,
                Velocity        = _rb.GetLinearVelocity(),
                AngularVelocity = _rb.angularVelocity,
                IsSleeping      = _rb.IsSleeping(),
                ConstraintMask  = (byte)(int)_rb.constraints,
            };
        }
    }
}
