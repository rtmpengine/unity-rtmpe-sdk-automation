// RTMPE SDK — Runtime/Sync/NetworkRigidbody2D.cs
//
// MonoBehaviour component that synchronises a GameObject's 2-D Rigidbody2D
// physics state (position, rotation, velocity, angular velocity, sleep) over
// the RTMPE network.
//
// ── Architecture ──────────────────────────────────────────────────────────────
//
//  Mirrors NetworkRigidbody exactly but targets Rigidbody2D instead of Rigidbody.
//  Key 2-D differences:
//    • Position is Vector2 (XY plane); Z is never touched.
//    • Rotation is a single float in degrees (not a Quaternion).
//    • AngularVelocity is a single float in degrees/second.
//    • MovePosition(Vector2) / MoveRotation(float) are used for non-kinematic
//      correction (Rigidbody2D's equivalents of the 3-D MovePosition/Rotation).
//
//  Owner (authoritative physics):
//    FixedUpdate() captures Rigidbody2D state and sends PhysicsSync2D payloads
//    as PacketType.PhysicsSync (0x45) when enabled fields change beyond thresholds.
//    No Sync Service consumer ingests rigidbody state yet; the gateway
//    validates the frame and drops it, so nothing is broadcast back.
//
//  Non-owner (remote simulation):
//    ApplyRemoteState() is called by NetworkManager when a 2-D physics packet
//    arrives.  FixedUpdate() applies dead reckoning, position/rotation correction,
//    and velocity blending each physics step.  With no downlink producer this
//    path is not exercised today.
//
// ── Threading ─────────────────────────────────────────────────────────────────
//
//  All methods run on the Unity main thread.  No locks needed.
//
// ── Angle conventions ─────────────────────────────────────────────────────────
//
//  Rigidbody2D.rotation returns degrees in the range (-180, 180].
//  Rigidbody2D.angularVelocity returns degrees/second.
//  Both values are transmitted as-is (no conversion to radians).
//  Quaternion.Angle is NOT used for rotation-change detection; Mathf.Abs of
//  the delta angle (with DeltaAngle normalisation) is used instead.

using System.Buffers;

using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync.Internal;

namespace RTMPE.Sync
{
    /// <summary>
    /// Sends a 2-D <see cref="UnityEngine.Rigidbody2D"/>'s physics state from the
    /// owner, for other clients to follow with velocity blending and dead
    /// reckoning.
    /// </summary>
    /// <remarks>
    /// Important: the RTMPE server does not relay the state this component
    /// sends, so it does not move other players' copies. For a physics object
    /// other players must see, use <see cref="NetworkTransform"/> and make the
    /// <see cref="UnityEngine.Rigidbody2D"/> kinematic on clients that do not
    /// own it.
    /// </remarks>
    [AddComponentMenu("RTMPE/Network Rigidbody 2D")]
    [RequireComponent(typeof(Rigidbody2D))]
    public class NetworkRigidbody2D : NetworkBehaviour
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
        [Tooltip("Synchronise world-space 2-D position.")]
        [SerializeField] private bool _syncPosition = true;

        [Tooltip("Synchronise Z-axis rotation (degrees).")]
        [SerializeField] private bool _syncRotation = true;

        [Tooltip("Synchronise linear velocity so remote bodies keep moving between updates.")]
        [SerializeField] private bool _syncVelocity = true;

        [Tooltip("Synchronise angular velocity (deg/s) so remote bodies keep spinning between updates.")]
        [SerializeField] private bool _syncAngularVelocity = true;

        [Tooltip("Synchronise the sleep state, so a remote body sleeps when the owner's body does.")]
        [SerializeField] private bool _syncSleepState = true;

        [Tooltip("Synchronise the body's RigidbodyConstraints2D, so constraint changes made at " +
                 "run time (for example freezing the X axis during a climb) reach remote bodies. " +
                 "A receiver applies them only when NetworkSettings.allowDynamicConstraints is on.")]
        [SerializeField] private bool _syncConstraints = true;

        // ── Inspector — Send thresholds ────────────────────────────────────────

        [Header("Send Thresholds")]
        [Tooltip("Minimum position change, in world units, before an update is sent.")]
        [SerializeField] private float _positionThreshold = 0.01f;

        [Tooltip("Minimum rotation change, in degrees, before an update is sent.")]
        [SerializeField] private float _rotationThreshold = 0.1f;

        [Tooltip("Minimum linear velocity change, in units per second, before an update is sent.")]
        [SerializeField] private float _velocityThreshold = 0.05f;

        [Tooltip("Minimum angular velocity change, in degrees per second, before an update is sent.")]
        [SerializeField] private float _angularVelocityThreshold = 1.0f;

        // ── Inspector — Remote body ────────────────────────────────────────────

        [Header("Remote Body Behaviour")]
        [Tooltip("Make the Rigidbody2D kinematic on clients that do not own the object, and set " +
                 "its position and rotation directly. Use it for bodies the owner controls " +
                 "completely, such as player characters. The body's own type is restored when " +
                 "this client takes ownership or the object is despawned.")]
        [SerializeField] private bool _makeRemoteKinematic = false;

        [Tooltip("Position error, in world units, above which the remote body moves straight to " +
                 "the received position instead of moving smoothly.")]
        [SerializeField] private float _snapThreshold = 3.0f;

        [Tooltip("How fast the remote body moves toward the received position.")]
        [SerializeField] [Range(1f, 50f)] private float _positionCorrectionSpeed = 10f;

        [Tooltip("How fast the remote body turns toward the received rotation.")]
        [SerializeField] [Range(1f, 50f)] private float _rotationCorrectionSpeed = 10f;

        // ── Inspector — Owner reconciliation ───────────────────────────────────

        [Header("Owner Reconciliation")]
        [Tooltip("Snap the owner's body to a server correction when it has drifted further than " +
                 "the thresholds below; smaller differences are kept. Only for a server that " +
                 "simulates physics authoritatively. A correction farther than " +
                 "NetworkSettings.maxServerCorrectionDistance is refused.")]
        [SerializeField] private bool _enableOwnerReconciliation = false;

        [Tooltip("Position error, in world units, above which the owner snaps to the server's " +
                 "position. Set it high enough not to fight ordinary physics differences.")]
        [SerializeField] [Range(0.5f, 20f)] private float _ownerReconcileSnapThreshold = 3.0f;

        [Tooltip("Rotation error, in degrees, above which the owner snaps to the server's " +
                 "rotation.")]
        [SerializeField] [Range(1f, 180f)] private float _ownerReconcileRotationSnapDegrees = 30.0f;

        // ── Inspector — Dead reckoning ─────────────────────────────────────────

        [Header("Dead Reckoning")]
        [Tooltip("Continue the remote body along its last received velocity between updates, " +
                 "so it does not stop between them.")]
        [SerializeField] private bool _enableDeadReckoning = true;

        [Tooltip("Seconds after the last update when dead reckoning stops; the body is then " +
                 "corrected toward the last received position.")]
        [SerializeField] [Range(0.1f, 2.0f)] private float _deadReckoningTimeout = 0.5f;

        // ── Inspector — Send rate ──────────────────────────────────────────────

        [Header("Send Rate")]
        [Tooltip("Updates the owner sends per second, at most (1–30).")]
        [SerializeField] [Range(1, 30)] private int _sendRateHz = 20;

        // ── Runtime state ──────────────────────────────────────────────────────

        private Rigidbody2D _rb;

        // Owner side.
        private PhysicsState2D _lastSentState;
        private bool           _hasSentOnce;
        private bool           _lastSleepState;
        private byte           _lastSentConstraints;
        private float          _sendAccum;

        // Non-owner side.
        private PhysicsState2D _receivedState;
        private float          _lastReceiveTime;
        private bool           _hasReceivedState;
        private byte           _appliedConstraints;
        private bool           _hasReceivedConstraints;

        // Receive-side hardening: token-bucket rate limit and per-tick
        // delta-cap reference state.  Mirrors the 3-D component.
        private float _rateBucketTokens;
        private float _rateBucketLastTime;
        private bool  _hasAppliedPosition;

        // ⚠️ A RigidbodyType2D, not a bool: the 2D body carries Dynamic,
        // Kinematic and Static, so restoring "not kinematic" would turn a
        // Static body Dynamic.  The 3D component keeps a bool for the same
        // reason — each stores what its own API takes.
        private RigidbodyType2D _authoredBodyType;
        private bool            _forcedKinematic;

        /// <summary>
        /// Apply, or withdraw, the kinematic mode a replica's body is held in.
        /// Called from the ownership pipeline on both events that can change
        /// the answer: a server-confirmed handover, and the start of a spawn
        /// cycle.
        /// </summary>
        internal void ReconcileKinematicToOwnership(bool locallyOwned)
        {
            if (_rb == null) return;

            // ⛔ Only the FORCE limb is gated on the setting.  Guarding the
            // restore with it too meant a game that turned
            // makeRemoteKinematic off at runtime while a body was held
            // wedged it with no path back — taking and giving back cannot
            // both depend on a flag that may change in between.
            if (locallyOwned) { RestoreAuthoredBodyType(); return; }

            if (!_makeRemoteKinematic || _forcedKinematic) return;
            _authoredBodyType = _rb.bodyType;
            _forcedKinematic  = true;
            _rb.bodyType      = RigidbodyType2D.Kinematic;
        }

        private void RestoreAuthoredBodyType()
        {
            if (!_forcedKinematic) return;

            // Dropped whether or not there is still a body — see
            // NetworkRigidbody for the destroy-and-re-add case it answers.
            _forcedKinematic = false;
            if (_rb == null) return;
            _rb.bodyType = _authoredBodyType;
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Finds the <see cref="UnityEngine.Rigidbody2D"/> and, on a client that
        /// does not own the object, makes it kinematic when <b>Make Remote
        /// Kinematic</b> is on. A class that overrides it must call
        /// <c>base.OnNetworkSpawn()</c>.
        /// </summary>
        protected override void OnNetworkSpawn()
        {
            _rb = GetComponent<Rigidbody2D>();
            if (_rb == null)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRigidbodyMissingWarnTicks))
                    Debug.LogError("[RTMPE] NetworkRigidbody2D.OnNetworkSpawn: " +
                                   "no Rigidbody2D found on this GameObject.", this);
                return;
            }

            // Restore before capturing — see NetworkRigidbody for why a pooled
            // instance would otherwise record the SDK's own value as the one
            // the scene authored.
            RestoreAuthoredBodyType();
            _authoredBodyType = _rb.bodyType;
            ReconcileKinematicToOwnership(IsOwner);

            if (IsOwner)
            {
                _lastSentState       = GetState();
                _lastSleepState      = _rb.IsSleeping();
                _lastSentConstraints = _lastSentState.ConstraintMask;
                _hasSentOnce         = false;
            }

            _appliedConstraints     = (byte)(int)_rb.constraints;
            _hasReceivedConstraints = false;

            // Pre-fill the rate-limit bucket; see NetworkRigidbody for rationale.
            var settings0 = NetworkManager.Instance?.Settings;
            float capacity0 = settings0 != null ? Mathf.Max(0f, settings0.maxPhysicsPacketsPerSecond) : 0f;
            _rateBucketTokens   = capacity0;
            _rateBucketLastTime = Time.fixedTime;
            _hasAppliedPosition = false;

            _sendAccum = 0f;
        }

        /// <summary>
        /// Restores the body's own type when the object leaves the network, so a
        /// pooled or reused instance is not left kinematic. A class that
        /// overrides it must call <c>base.OnNetworkDespawn()</c>.
        /// </summary>
        protected override void OnNetworkDespawn()
        {
            RestoreAuthoredBodyType();
            _hasReceivedState = false;
            _hasSentOnce      = false;
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
            _sendAccum += Time.fixedDeltaTime;
            float sendInterval = 1f / _sendRateHz;
            if (_sendAccum < sendInterval) return;
            _sendAccum -= sendInterval;

            var  current  = GetState();
            byte dataMask = BuildChangedMask(current);

            // See NetworkRigidbody: the flag records a BROADCAST, and the
            // encode below can refuse a non-finite pose. Set after the send.
            if (!_hasSentOnce) dataMask = BuildFullMask();

            if (dataMask == 0x00) return;

            var manager = NetworkManager.Instance;
            if (manager == null) return;

            // Pooled 2-D physics send path (GC Round 2, 2026-05-02).
            int size   = PhysicsPacketBuilder.ComputePayloadSize(dataMask, twoDee: true);
            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(size);
            try
            {
                int written = PhysicsPacketBuilder.Build2DPayloadInto(
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
                // ⚠️ Two roads lead here, and they differ by the kind of
                // divergence.  A NaN never selects its own bit — BuildChangedMask
                // compares against `_lastSentState` and a comparison with NaN is
                // false — so a NaN field reaches the encoder only through
                // `BuildFullMask()`, the first send after spawn; an infinite
                // position, velocity or angular velocity does select its bit,
                // because its distance from anything is infinite, and is refused
                // here on any send (the rotation is neither: its angle against
                // anything is not a number, and the encoder substitutes rather
                // than refuses it).  Either way the field the mask left out
                // keeps the record it had (see the bookkeeping below), which is
                // what makes the recovery a change.
                if (written == 0) return;

                manager.SendPhysicsSync(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }

            // The baseline moves only for the fields this send carried — see
            // NetworkRigidbody for why a field the mask left out must not.
            _lastSentState       = PhysicsState2D.AfterSend(_lastSentState, current, dataMask);
            _hasSentOnce         = true;
            _lastSleepState      = current.IsSleeping;
            _lastSentConstraints = current.ConstraintMask;
        }

        private byte BuildChangedMask(PhysicsState2D current)
        {
            byte mask = 0;

            if (_syncPosition &&
                (current.Position - _lastSentState.Position).magnitude > _positionThreshold)
                mask |= PhysicsPacketBuilder.ChangedPosition;

            if (_syncRotation &&
                Mathf.Abs(Mathf.DeltaAngle(current.Rotation, _lastSentState.Rotation)) > _rotationThreshold)
                mask |= PhysicsPacketBuilder.ChangedRotation;

            if (_syncVelocity &&
                (current.Velocity - _lastSentState.Velocity).magnitude > _velocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedVelocity;

            if (_syncAngularVelocity &&
                Mathf.Abs(current.AngularVelocity - _lastSentState.AngularVelocity) > _angularVelocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedAngularVelocity;

            if (_syncSleepState && current.IsSleeping != _lastSleepState)
                mask |= PhysicsPacketBuilder.ChangedSleep;

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
            // Apply the authoritative constraint bitmask once per change.
            if (_syncConstraints && _hasReceivedConstraints
                && _receivedState.ConstraintMask != _appliedConstraints)
            {
                _rb.constraints     = (RigidbodyConstraints2D)_receivedState.ConstraintMask;
                _appliedConstraints = _receivedState.ConstraintMask;
            }

            // ── Sleep handling ────────────────────────────────────────────────
            if (_syncSleepState && _receivedState.IsSleeping)
            {
                if (!_rb.IsSleeping()) _rb.Sleep();
                return;
            }
            if (_rb.IsSleeping()) _rb.WakeUp();

            float timeSincePacket = Time.fixedTime - _lastReceiveTime;

            // ── Dead reckoning ─────────────────────────────────────────────────
            Vector2 targetPos = _receivedState.Position;
            if (_enableDeadReckoning && timeSincePacket < _deadReckoningTimeout && _syncVelocity)
                targetPos = _receivedState.Position + _receivedState.Velocity * timeSincePacket;

            // Frame-rate-independent smoothing.  The naive `dt * rate` lerp
            // coefficient produces a visibly-different time-to-converge at
            // every Project-Settings physics step (50 / 60 / 120 Hz), and at
            // 30 Hz it can exceed 1 — which silently degenerates into an
            // instant snap.  The exponential form `1 - exp(-rate * dt)`
            // converges to the same proportion of the remaining error per
            // unit of wall-clock time regardless of dt, matching the
            // discipline already in use in the 3-D companion.
            float posLerpT = 1f - Mathf.Exp(-_positionCorrectionSpeed * Time.fixedDeltaTime);
            float rotLerpT = 1f - Mathf.Exp(-_rotationCorrectionSpeed * Time.fixedDeltaTime);

            // ── Position correction ───────────────────────────────────────────
            if (_syncPosition)
            {
                float posError = (targetPos - _rb.position).magnitude;
                if (posError > _snapThreshold)
                {
                    if (_makeRemoteKinematic)
                        _rb.position = targetPos;
                    else
                        _rb.MovePosition(targetPos);
                }
                else
                {
                    Vector2 corrected = Vector2.Lerp(_rb.position, targetPos, posLerpT);
                    if (_makeRemoteKinematic)
                        _rb.position = corrected;
                    else
                        _rb.MovePosition(corrected);
                }
            }

            // ── Rotation correction ────────────────────────────────────────────
            // Use Mathf.LerpAngle to take the shortest arc through zero degrees.
            if (_syncRotation)
            {
                float correctedAngle = Mathf.LerpAngle(
                    _rb.rotation, _receivedState.Rotation, rotLerpT);
                if (_makeRemoteKinematic)
                    _rb.rotation = correctedAngle;
                else
                    _rb.MoveRotation(correctedAngle);
            }

            // ── Velocity blending (non-kinematic only) ─────────────────────────
            if (!_makeRemoteKinematic)
            {
                if (_syncVelocity)
                    _rb.SetLinearVelocity(Vector2.Lerp(
                        _rb.GetLinearVelocity(), _receivedState.Velocity, posLerpT));

                if (_syncAngularVelocity)
                    _rb.angularVelocity = Mathf.Lerp(
                        _rb.angularVelocity, _receivedState.AngularVelocity, rotLerpT);
            }
        }

        // Componentwise finiteness predicate used by the inbound gate.  Mirrors
        // the 3-D component's helper.  NaN/Inf comparison short-circuits the
        // plausibility caps that follow, so any non-finite value must be
        // rejected before those caps run.
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // ── Internal API (called by NetworkManager) ────────────────────────────

        /// <summary>
        /// Apply an incoming 2-D physics-state update from a remote owner.
        /// Called by <c>NetworkManager.HandlePhysicsSync2DPacket</c> on non-owner clients.
        /// </summary>
        internal void ApplyRemoteState(PhysicsState2D incoming, byte changedMask)
        {
            // Componentwise finiteness gate.  Mirrors the 3-D component: the
            // plausibility caps further down compare with `>` operators which
            // short-circuit to false for NaN — letting NaN propagate into
            // Rigidbody2D.position / linearVelocity puts Box2D into an
            // unrecoverable state on most Unity versions (body disappears,
            // joints detach).  Reject the entire packet rather than persist
            // a corrupt sub-field.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                && (!IsFinite(incoming.Position.x) || !IsFinite(incoming.Position.y)))
                return;

            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0
                && !IsFinite(incoming.Rotation))
                return;

            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                && (!IsFinite(incoming.Velocity.x) || !IsFinite(incoming.Velocity.y)))
                return;

            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                && !IsFinite(incoming.AngularVelocity))
                return;

            var settings = NetworkManager.Instance?.Settings;

            // Per-object inbound rate limit (token-bucket).  See NetworkRigidbody
            // for the threat model — same defence applies in 2-D.
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

            // Plausibility caps on velocity, angular velocity, and per-tick
            // position delta.  In 2-D, AngularVelocity is a single float in
            // degrees/second so we compare the absolute value.
            if (settings != null)
            {
                if (settings.maxLinearVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                    && incoming.Velocity.sqrMagnitude
                       > settings.maxLinearVelocity * settings.maxLinearVelocity)
                    return;

                if (settings.maxAngularVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                    && Mathf.Abs(incoming.AngularVelocity) > settings.maxAngularVelocity)
                    return;

                if (settings.maxPositionDeltaPerTick > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                    && _hasAppliedPosition
                    && (incoming.Position - _receivedState.Position).sqrMagnitude
                       > settings.maxPositionDeltaPerTick * settings.maxPositionDeltaPerTick)
                    return;
            }

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
        /// Defensive 2-D owner reconciliation.  Mirrors
        /// <see cref="NetworkRigidbody.ApplyReconciliation"/>: rejects
        /// non-finite payloads, enforces the server-correction distance cap
        /// and world bounds (audit P4-003), then applies a snap when the local
        /// position diverges from the server-confirmed position beyond
        /// <see cref="_ownerReconcileSnapThreshold"/>.
        /// </summary>
        internal void ApplyReconciliation(PhysicsState2D serverState, byte changedMask)
        {
            if (!_enableOwnerReconciliation || _rb == null || !IsOwner) return;

            var settings = NetworkManager.Instance?.Settings;

            if (_syncPosition && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                Vector2 sp = serverState.Position;
                if (float.IsNaN(sp.x) || float.IsInfinity(sp.x) ||
                    float.IsNaN(sp.y) || float.IsInfinity(sp.y))
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteServerPositionWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] NetworkRigidbody2D.ApplyReconciliation: rejected non-finite " +
                            $"server position {sp} — keeping local state.", this);
                    return;
                }

                float err = Vector2.Distance(_rb.position, sp);

                // ── Server-correction cap & world bounds ─────────────────────
                // Closes the 2D/3D parity gap (audit P4-003): the 3D path
                // already rejects a hostile server's teleport via these guards,
                // and the 2D path must apply the same project-wide tuning.  The
                // world AABB is checked on its X/Y components only (Z is
                // meaningless for a 2D body).  Each guard is bypassed when its
                // setting is disabled (0 / false) for back-compat.
                if (settings != null)
                {
                    if (settings.maxServerCorrectionDistance > 0f
                        && err > settings.maxServerCorrectionDistance)
                    {
                        if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastCorrectionCapWarnTicks))
                            Debug.LogWarning(
                                "[RTMPE] NetworkRigidbody2D.ApplyReconciliation: rejected " +
                                $"server correction of {err:F2}m (cap " +
                                $"{settings.maxServerCorrectionDistance:F2}m) — keeping " +
                                "local state.", this);
                        return;
                    }

                    if (settings.worldBoundsEnabled)
                    {
                        float dx = sp.x - settings.worldBoundsCenter.x;
                        float dy = sp.y - settings.worldBoundsCenter.y;
                        if (Mathf.Abs(dx) > settings.worldBoundsExtents.x
                            || Mathf.Abs(dy) > settings.worldBoundsExtents.y)
                        {
                            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastWorldBoundsWarnTicks))
                                Debug.LogWarning(
                                    "[RTMPE] NetworkRigidbody2D.ApplyReconciliation: rejected " +
                                    $"server position {sp} outside world bounds — keeping " +
                                    "local state.", this);
                            return;
                        }
                    }
                }

                if (err > _ownerReconcileSnapThreshold)
                {
                    // Snap applied immediately (no FixedUpdate deferral).  2D
                    // physics ticks synchronously with FixedUpdate on the same
                    // thread, so a direct write here does not produce the
                    // LCM-beat artifact that the 3D path avoids with its
                    // deferred pending-snap buffer.  If multiple reconciliation
                    // packets arrive in a single frame, each one overwrites the
                    // previous position (newer-wins); this is intentional —
                    // stale intermediate states would produce a stutter even if
                    // applied, and the latest state is always the most accurate.
                    if (_makeRemoteKinematic) _rb.position = sp;
                    else                      _rb.MovePosition(sp);
                }
            }

            if (_syncRotation && (changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
            {
                float sr = serverState.Rotation;
                if (!IsFinite(sr))
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteServerRotationWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] NetworkRigidbody2D.ApplyReconciliation: rejected non-finite " +
                            $"server rotation {sr} — keeping local state.", this);
                    return;
                }

                float angleErr = Mathf.Abs(Mathf.DeltaAngle(_rb.rotation, sr));
                if (angleErr > _ownerReconcileRotationSnapDegrees)
                {
                    if (_makeRemoteKinematic) _rb.rotation = sr;
                    else                      _rb.MoveRotation(sr);
                }
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the body's current physics state.
        /// </summary>
        /// <returns>
        /// The body's state, or the default value before the object has spawned.
        /// </returns>
        public PhysicsState2D GetState()
        {
            if (_rb == null)
                return default;
            return new PhysicsState2D
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
