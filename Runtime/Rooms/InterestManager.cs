// RTMPE SDK — Runtime/Rooms/InterestManager.cs
//
// Feature #6: Interest Management — client-side position reporting.
//
// InterestManager is a MonoBehaviour that periodically sends the local player's
// 2-D world position to the gateway via PositionUpdate (0x42) packets.  The
// gateway uses the position to apply spatial-grid culling so that room-wide
// broadcasts from the Sync Engine only reach clients whose 3×3 cell
// neighbourhood (default: 150 m × 150 m with a 50 m cell size) overlaps the
// source position embedded in the broadcast.
//
// Clients that never attach InterestManager (or that call StopTracking())
// receive every room-wide broadcast unchanged — opt-in semantics preserve
// full backwards compatibility.
//
// Usage:
//   Add InterestManager to the local player's GameObject, or to any persistent
//   manager object, and assign the tracked Transform.  The component sends
//   positions automatically while the player is in a room.
//
// Protocol:
//   Packet 0x42 payload — [x: f32 LE][y: f32 LE] (8 bytes)
//   The x/y values are the tracked transform's world position projected onto
//   the XZ plane (Y is vertical in Unity 3D games; for 2-D games, use Y).
//
// Gateway counterpart:
//   modules/gateway/src/interest/spatial_grid.rs
//   modules/gateway/src/session/store.rs  (AppSession.position)
//   modules/gateway/src/nats/broadcast.rs (zone-filtered dispatch)

using System.Collections.Generic;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Optional distance-based filtering for large rooms. While in a room, the component
    /// reports the position of <see cref="TrackedTransform"/> to the server every
    /// <see cref="UpdateInterval"/> seconds, and the server then sends this client state
    /// updates only for objects near it.
    /// </summary>
    /// <remarks>
    /// <para>A client without an <see cref="InterestManager"/> receives every update. While
    /// <see cref="TrackedTransform"/> is <see langword="null"/>, nothing is sent. A position
    /// that is NaN or infinite is not sent, and a warning is logged.</para>
    /// <para>Keep one enabled: every enabled <see cref="InterestManager"/> reports its own
    /// <see cref="TrackedTransform"/>, while the receive filter
    /// (<see cref="ReceiveFilterRadius"/>) uses the most recently enabled one.</para>
    /// </remarks>
    public sealed class InterestManager : MonoBehaviour
    {
        // ── Inspector / public fields ──────────────────────────────────────────

        /// <summary>
        /// The transform whose world position is reported, usually the local player's root.
        /// While it is <see langword="null"/>, nothing is sent.
        /// </summary>
        [Tooltip("The Transform whose world position is reported to the server, usually the " +
                 "local player's root. Nothing is sent while this is empty.")]
        public Transform TrackedTransform;

        /// <summary>
        /// Seconds between position reports, from <see cref="MinUpdateInterval"/> to 5. Default
        /// 0.1 (ten reports a second). Smaller values react faster and send more.
        /// </summary>
        [Tooltip("Seconds between position reports (default 0.1 = ten a second).")]
        [Range(MinUpdateInterval, 5f)]
        public float UpdateInterval = 0.1f;

        /// <summary>
        /// The shortest <see cref="UpdateInterval"/>, in seconds. A smaller value is raised to
        /// it, and a warning is logged.
        /// </summary>
        public const float MinUpdateInterval = 0.05f;

        private static long s_lastIntervalFloorWarnTicks;

        /// <summary>
        /// Whether to report the X and Z coordinates (3-D games with Y up). Default
        /// <see langword="true"/>; turn it off for 2-D games that use X and Y.
        /// </summary>
        [Tooltip("Project position onto XZ plane (Y is vertical). " +
                 "Disable for 2-D or top-down games that use X/Y coordinates.")]
        public bool UseXzPlane = true;

        /// <summary>
        /// When above 0, this client also discards incoming state for objects farther away than
        /// this, in world units, in addition to the server's filtering. Default 0 (off).
        /// </summary>
        /// <remarks>
        /// The filter starts once a position has been reported.
        /// </remarks>
        [Tooltip("When above 0, incoming state for objects farther away than this " +
                 "(world units) is discarded on this client. 0 turns the filter off.")]
        [Min(0f)]
        public float ReceiveFilterRadius = 0f;

        /// <summary>
        /// Extra distance, in world units, before a visible object is dropped by the receive
        /// filter, so objects at the edge of <see cref="ReceiveFilterRadius"/> do not flicker
        /// in and out. Default 1.
        /// </summary>
        /// <remarks>
        /// An object becomes visible within <see cref="ReceiveFilterRadius"/> and is dropped
        /// beyond <see cref="ReceiveFilterRadius"/> plus this margin.
        /// <see cref="NetworkSettings.interestHysteresisMargin"/> replaces this value while that
        /// setting is 0 or more, which it is by default.
        /// </remarks>
        [Tooltip("Extra distance (world units) before a visible object is dropped by the " +
                 "receive filter, so objects at the edge do not flicker. Used only while the " +
                 "NetworkSettings Interest Hysteresis Margin is negative.")]
        [Min(0f)]
        public float HysteresisMargin = 1f;

        // ── Static local-position accessor (used by NetworkManager) ───────────

        // Active component instance (singleton invariant enforced via OnEnable /
        // OnDisable below).  Exposed so NetworkManager can read the live
        // ReceiveFilterRadius and last-sent position without a component
        // reference on the hot path.  null when no manager is active.
        private static InterestManager s_active;

        /// <summary>
        /// The last world-space position reported to the gateway by the
        /// active <see cref="InterestManager"/>, expressed as a pair of
        /// horizontal coordinates.  In XZ mode (3-D, default) the tuple is
        /// (worldX, worldZ); in XY mode (2-D / top-down) it is (worldX,
        /// worldY).  Callers that need to compare against an object's
        /// position must consult <see cref="LocalUsesXzPlane"/> to pick the
        /// matching axis on the remote object.
        /// Returns (0, 0) when no manager is active.
        /// </summary>
        internal static (float h1, float h2) LocalPosition
            => s_active == null ? (0f, 0f) : (s_active._lastSentX, s_active._lastSentY);

        /// <summary>
        /// True when the active <see cref="InterestManager"/> reports
        /// positions on the XZ plane (Y vertical — 3-D default), false when
        /// it reports on the XY plane (top-down / 2-D games).  Returns true
        /// when no manager is active so callers default to the 3-D
        /// interpretation, matching the historical receive-filter behavior.
        /// </summary>
        internal static bool LocalUsesXzPlane
            => s_active == null ? true : s_active.UseXzPlane;

        /// <summary>
        /// Receive-side interest radius exposed by the active
        /// <see cref="InterestManager"/>.  Zero when filtering is disabled or
        /// no manager is active.  Read fresh from the Inspector field every
        /// call so a runtime toggle of <see cref="ReceiveFilterRadius"/> takes
        /// effect on the very next packet — no 100 ms hysteresis.
        /// </summary>
        internal static float LocalReceiveRadius
            => s_active == null ? 0f : Mathf.Max(0f, s_active.ReceiveFilterRadius);

        /// <summary>
        /// True while an <see cref="InterestManager"/> instance is active,
        /// <see cref="ReceiveFilterRadius"/> is greater than zero, AND a
        /// position has actually been reported (so the (0, 0) origin is not
        /// used as a default that would silently reject every remote object).
        /// </summary>
        internal static bool IsReceiveFilterActive
            => s_active != null
               && s_active._hasSentOnce
               && s_active.ReceiveFilterRadius > 0f;

        /// <summary>
        /// Hysteresis-aware visibility decision used by the receive-side
        /// interest filter.  Returns <see langword="true"/> when the packet
        /// for <paramref name="objectId"/> should be delivered to the
        /// matching <c>NetworkBehaviour</c>; <see langword="false"/> when
        /// the object lies outside the (possibly expanded) interest radius.
        ///
        /// <para>Semantics: an object ENTERS the visible set the first time
        /// its squared distance falls at or below <c>r²</c>, and LEAVES only
        /// once its squared distance strictly exceeds <c>(r + margin)²</c>.
        /// Between those bounds the previous decision is preserved, which
        /// kills the per-tick flap on the radius boundary.</para>
        ///
        /// <para>When the receive filter is inactive (no manager, no radius,
        /// no first send) this method returns <see langword="true"/> — every
        /// packet is delivered, matching the historical behaviour and
        /// preserving the secondary-defence framing of the filter.</para>
        ///
        /// <para>State is keyed by <c>objectId</c>; entries are evicted
        /// implicitly when the manager goes inactive (<see cref="OnDisable"/>)
        /// and explicitly when the radius is set to zero at runtime.  No
        /// per-frame allocation occurs on the hot path: the dictionary uses
        /// the default <c>EqualityComparer&lt;ulong&gt;</c> which avoids
        /// boxing.</para>
        /// </summary>
        internal static bool ShouldDeliver(ulong objectId, float distSq)
        {
            // Inactive filter → deliver unconditionally so callers can safely
            // collapse their pre-check (IsReceiveFilterActive) into this call.
            if (!IsReceiveFilterActive) return true;

            var self   = s_active;
            float r    = Mathf.Max(0f, self.ReceiveFilterRadius);
            float m    = Mathf.Max(0f, self.EffectiveHysteresisMargin);
            float rSq  = r * r;
            float roSq = (r + m) * (r + m);

            // Lookup current visibility; absence means "not yet seen", which
            // we treat as hidden so a one-shot enter test (distSq <= rSq)
            // triggers visibility on the FIRST in-range packet.
            bool wasVisible = self._visible.Contains(objectId);

            bool nowVisible;
            if (wasVisible)
            {
                // Currently visible: stay visible until distSq strictly
                // exceeds the OUTER bound.  Equality at the outer bound
                // keeps the object visible — biases toward retention which
                // is the correct game-feel default (avoids "popping out"
                // at a quantised boundary).
                nowVisible = distSq <= roSq;
            }
            else
            {
                // Currently hidden: become visible only when strictly inside
                // (or on) the INNER bound.  Hysteresis prevents flap because
                // re-entering at r is harder than leaving at r+m.
                nowVisible = distSq <= rSq;
            }

            if (nowVisible != wasVisible)
            {
                if (nowVisible) self._visible.Add(objectId);
                else            self._visible.Remove(objectId);
            }
            return nowVisible;
        }

        /// <summary>
        /// Drop hysteresis state for <paramref name="objectId"/>.  Called by
        /// the registry on despawn so a future spawn that re-uses the id
        /// starts hidden, matching the "first contact" semantics of
        /// <see cref="ShouldDeliver"/>.  Safe to call when no manager is
        /// active or the id is not tracked (idempotent).
        /// </summary>
        internal static void ForgetObject(ulong objectId)
        {
            var self = s_active;
            if (self == null) return;
            self._visible.Remove(objectId);
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test-only seam: clear all per-object hysteresis state on the
        /// active manager so a unit test can stage deterministic enter/leave
        /// sequences without disposing the component.  Compiled only when
        /// <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal static void ResetHysteresisStateForTests()
        {
            var self = s_active;
            if (self == null) return;
            self._visible.Clear();
        }

        /// <summary>
        /// Test-only seam: install a synthetic local position + radius so
        /// unit tests can drive <see cref="ShouldDeliver"/> without going
        /// through the gateway send-path.  Mirrors the post-send field
        /// updates in <see cref="SendCurrentPosition"/>.  Production code
        /// MUST NOT call this — it bypasses the SendPositionUpdate side
        /// effect that keeps the gateway's interest zone in sync.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void PrimeForTests(float worldX, float worldY,
                                    float receiveRadius, float hysteresisMargin)
        {
            _lastSentX        = worldX;
            _lastSentY        = worldY;
            _hasSentOnce      = true;
            ReceiveFilterRadius = receiveRadius;
            HysteresisMargin    = hysteresisMargin;
            _visible.Clear();
        }
#endif // UNITY_INCLUDE_TESTS

        // ── Private state ──────────────────────────────────────────────────────

        private float _accumulator;
        private float _lastSentX;
        private float _lastSentY;
        private bool  _hasSentOnce;
        private bool  _tracking = true;

        // Object IDs currently considered visible.  HashSet keyed by ulong
        // gives O(1) Contains/Add/Remove with no boxing under the default
        // EqualityComparer.  Bounded by simultaneous in-radius object count
        // (typically the size of the gateway's 3×3 cell neighbourhood).
        private readonly HashSet<ulong> _visible = new HashSet<ulong>();

        // Effective margin: NetworkSettings override > Inspector field.
        // Resolved per call to keep the override hot — a runtime tuning
        // change should take effect on the very next packet.
        private float EffectiveHysteresisMargin
        {
            get
            {
                var nm = NetworkManager.Instance;
                var s  = nm != null ? nm.Settings : null;
                if (s != null && s.interestHysteresisMargin >= 0f)
                    return s.interestHysteresisMargin;
                return HysteresisMargin;
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Pauses position reports. The server keeps using the last reported position until
        /// reporting resumes.
        /// </summary>
        public void StopTracking()  => _tracking = false;

        /// <summary>Resumes position reports after <see cref="StopTracking"/>.</summary>
        public void StartTracking() => _tracking = true;

        /// <summary>Whether reporting is on and the component is enabled.</summary>
        public bool IsTracking => _tracking && enabled;

        // ── Unity lifecycle ────────────────────────────────────────────────────

        private void Update()
        {
            if (!_tracking) return;

            var nm = NetworkManager.Instance;
            if (nm == null || !nm.IsInRoom) return;

            // Sanity-clamp the inspector-supplied interval before consuming
            // it.  Range attributes only fire from the Inspector; an asset
            // loaded via Addressables / AssetBundle / reflection can carry
            // a non-finite or zero/negative value, which would poison
            // `_accumulator` (NaN propagates through every subsequent `+=`)
            // and silently stall interest broadcasts for the rest of the
            // session.  A local read keeps the public field intact for the
            // diagnostics path while ensuring the timer math is sound.
            //
            // ⛔ Floored at the interval this field DECLARES, not at zero. A
            // value of 0.001 is finite and positive, so the old test passed it
            // through and the client sent PositionUpdate at frame rate —
            // spending the session's gateway token budget on interest traffic
            // and starving the gameplay packets that share it (S4-51). The
            // Inspector already refuses it; the paths this clamp exists for do
            // not go through the Inspector.
            float interval = UpdateInterval;
            if (float.IsNaN(interval) || float.IsInfinity(interval) || interval <= 0f)
                interval = 0.1f;
            else if (interval < MinUpdateInterval)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref s_lastIntervalFloorWarnTicks))
                    UnityEngine.Debug.LogWarning(
                        $"[RTMPE] InterestManager.UpdateInterval is {interval:0.###} s, below the " +
                        $"{MinUpdateInterval:0.##} s floor this field declares. Using " +
                        $"{MinUpdateInterval:0.##} s: at frame rate these updates spend the " +
                        "session's gateway rate budget and the gameplay packets share it.");
                interval = MinUpdateInterval;
            }

            // Real time: this is a broadcast cadence, not a simulation step.
            // The gateway ages a reported position out on its own wall clock and
            // the room keeps moving around a paused client, so charging the
            // interval in game time would stop a client reporting for exactly as
            // long as its Time.timeScale is zero — while its interest set, which
            // is what decides the traffic it receives, goes stale.
            _accumulator += Time.unscaledDeltaTime;
            if (_accumulator < interval) return;

            _accumulator -= interval;
            SendCurrentPosition(nm);
        }

        // ── Internal ───────────────────────────────────────────────────────────

        private void OnEnable()
        {
            // Singleton invariant: the most recently enabled InterestManager
            // wins.  A second leftover instance from a scene transition will
            // overwrite the previous one, but never disturb it via an out-of-
            // order OnDisable (see below).
            s_active = this;
        }

        private void OnDisable()
        {
            // Only clear if WE are the active instance.  Without this guard,
            // disabling a second manager (left over from a scene transition)
            // would wipe coordinates owned by the live one.
            if (s_active == this)
                s_active = null;

            // Visibility set is per-session: a future enable cycle (e.g. a
            // domain reload in the editor or a scene re-load) must restart
            // from "all hidden" so the first in-range packet enters via the
            // strict inner bound.  Clearing here also bounds memory growth
            // when an InterestManager is recycled across many rooms.
            _visible.Clear();
        }

        // Its own gate: a blown-up transform reports every interval until it is
        // fixed, and the rate at which this fires is the diagnosis.
        private long _lastNonFinitePoseWarnTicks;

        private void SendCurrentPosition(NetworkManager nm)
        {
            // Without a tracked transform there is no authoritative source for
            // the local player's position.  Skip the send — and skip recording
            // _hasSentOnce — so the receive filter stays inactive (defaulting
            // to "deliver everything") instead of using (0, 0) as a trap that
            // would silently discard every remote object far from world origin.
            if (TrackedTransform == null) return;

            var pos = TrackedTransform.position;
            float x, y;
            if (UseXzPlane)
            {
                x = pos.x;
                y = pos.z;
            }
            else
            {
                x = pos.x;
                y = pos.y;
            }

            // A non-finite pose is the same situation as no tracked transform
            // above, for the same reason and with worse consequences, so it is
            // refused in the same place and on the same terms.
            //
            // 🚨 `SendPositionUpdate` REFUSES a NaN/±Inf component — it throws,
            // deliberately, because the gateway's parser drops the transform.
            // The baseline was written BEFORE that call, so a single
            // non-finite frame did three things at once: it recorded a send
            // that never happened, it threw out of `Update()`, and — the one
            // that outlives the frame — it poisoned `LocalPosition`. The
            // receive filter compares squared distances against that, and every
            // comparison with NaN is false, so `ShouldDeliver` culls EVERY
            // remote object and the ones already visible go hidden. The client
            // goes blind for as long as the transform is not finite.
            //
            // ⛔ Nothing is cleared here. If a good position was sent earlier
            // the filter keeps using it, which is the last thing this client
            // actually told the gateway; if none ever was, `_hasSentOnce` stays
            // false and the filter stays inactive — "deliver everything" — which
            // is the same fail-open the no-transform arm chose above.
            if (!WireVector.IsFinite(x, y))
            {
                if (WarnGate.ShouldEmit(ref _lastNonFinitePoseWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] InterestManager: the tracked transform's position is not finite, " +
                        "so no PositionUpdate was sent and the interest filter is unchanged. " +
                        "A NaN or infinite position usually means a physics blow-up or a " +
                        "division by a zero delta upstream.");
                return;
            }

            _lastSentX   = x;
            _lastSentY   = y;
            _hasSentOnce = true;

            nm.SendPositionUpdate(x, y);
        }
    }
}
