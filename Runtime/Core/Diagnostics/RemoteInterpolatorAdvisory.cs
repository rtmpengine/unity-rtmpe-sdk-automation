// RTMPE SDK — Runtime/Core/Diagnostics/RemoteInterpolatorAdvisory.cs
//
// A non-owner networked object is driven entirely by the transform records the
// receiver decodes from the network, and the receive path applies that motion
// through the object's NetworkTransformInterpolator.  When the prefab carries a
// NetworkTransform (so it is registered for transform replication) but no
// interpolator, the receive path has nowhere to hand the decoded state: the
// record is discarded and the replica stays frozen on this client even though
// its replication traffic continues to arrive normally.  Nothing about that
// outcome is observable from the console — the object simply never moves.
//
// The same outcome has a second cause with a different remedy.  GetComponent<T>
// answers with a component that is switched off, so an object can carry an
// interpolator the receive path hands states to and Unity never calls Update on:
// the states are buffered by a behaviour that is not running, and the replica is
// as frozen as it would be with none.  Telling that reader to add a component
// produces a second interpolator on one transform, which is worse than what was
// reported — so the two faults are composed separately.
//
// This advisory turns those silent misconfigurations into a single, named,
// actionable console line each.  Because the inbound records arrive at the room's
// 30 Hz broadcast cadence, the surfacing is latched per object id so a
// persistently-misconfigured replica produces one diagnostic rather than a
// continuous stream.  The decision and the message are pure managed code with
// no UnityEngine dependency, so the warn-once behaviour and the wording are
// exercised directly under the headless test runner; the single
// Debug.LogWarning sink lives at the receive-dispatch call site.

using System.Collections.Concurrent;
using System.Threading;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Per-object warn-once gate for the two ways a non-owner object's decoded
    /// motion has nowhere to be applied: no
    /// <see cref="RTMPE.Sync.NetworkTransformInterpolator"/> at all, and one
    /// that is switched off.  The receive dispatch calls
    /// <see cref="ShouldWarn"/> once it has established which, and on the first
    /// occurrence for a given object emits <see cref="Compose"/> or
    /// <see cref="ComposeSwitchedOff"/>.  ⛔ Only the first is a drop path: a
    /// switched-off component is still handed the record, which is why nothing
    /// downstream reports it.
    /// </summary>
    internal static class RemoteInterpolatorAdvisory
    {
        // Object ids already surfaced.
        //
        // ⚠️ The MAP is concurrent; the gate is not.  ShouldWarn and ResetLatch
        // each touch two pieces of state, so a reset racing an add can burn a
        // slot or leave the set one entry over the ceiling.  Both are harmless
        // and neither is reachable today — the receive dispatch is main-thread
        // (it reads UnityEngine.Time on the same path) and so are both reset
        // sites.  Moving the receive path off the main thread would need this
        // pair made atomic, not merely the map kept concurrent.
        //
        // ⚠️ One entry per misconfigured OBJECT, not per misconfigured prefab: an
        // id is minted fresh for every spawn, so a prefab shipped without an
        // interpolator and spawned repeatedly — a projectile, a pickup — adds an
        // entry every time it is spawned.  The set is therefore bounded by the
        // ceiling below, and dropped at session end.
        private static readonly ConcurrentDictionary<ulong, byte> s_surfaced =
            new ConcurrentDictionary<ulong, byte>();

        // Distinct objects one session may surface.  Past it the advisory goes
        // quiet: a developer looking at several hundred lines naming the same
        // missing component has the information, and the alternative is a set
        // that grows with the spawn rate for the life of the process.
        //
        // 256 names every replica of a server-capacity room (100 players) twice
        // over and holds the map to a few tens of kilobytes.  It is a budget
        // rather than a derivation, so it is pinned by a test: changing it is a
        // reviewed edit and not a number that drifts.
        internal const int MaxSurfacedObjects = 256;

        // Counted rather than read from the map, because ConcurrentDictionary's
        // Count takes every one of its locks.
        private static int s_surfacedCount;

        /// <summary>
        /// Records <paramref name="objectId"/> as surfaced and reports whether
        /// this call is the first for that object.  Returns <see langword="true"/>
        /// exactly once per object id — the caller emits the advisory only on a
        /// <see langword="true"/> result, so a 30 Hz stream of records for the
        /// same misconfigured replica yields a single console line.
        /// </summary>
        public static bool ShouldWarn(ulong objectId)
        {
            // The ceiling is read before the add rather than enforced across it:
            // an overshoot of one entry per concurrent caller is possible and is
            // the price of not taking a lock on a receive path.  What matters is
            // that the set is bounded, not the exact bound.
            if (Volatile.Read(ref s_surfacedCount) >= MaxSurfacedObjects) return false;
            if (!s_surfaced.TryAdd(objectId, 0)) return false;
            Interlocked.Increment(ref s_surfacedCount);
            return true;
        }

        /// <summary>
        /// Builds the actionable advisory text naming the offending object and
        /// the remedy.  Pure; centralised here so the test project asserts on a
        /// stable string and any wording change is a single reviewed edit.
        /// </summary>
        public static string Compose(ulong objectId, string objectName)
        {
            string named = string.IsNullOrEmpty(objectName) ? "<unnamed>" : objectName;
            return
                $"[RTMPE] Networked object {objectId} ('{named}') is receiving remote " +
                "transform updates but carries no NetworkTransformInterpolator, so the " +
                "receive path has nowhere to apply the motion: the object stays frozen on " +
                "this client while its replication traffic continues to arrive.  Add a " +
                "NetworkTransformInterpolator component to the prefab alongside " +
                "NetworkTransform.  Logged once per object, for the first few " +
                "hundred objects of a session.";
        }

        /// <summary>
        /// Builds the advisory for a replica whose interpolator is present but
        /// switched off.  Pure, and kept beside <see cref="Compose"/> so the two
        /// remedies stay distinguishable in one reviewed place.
        /// </summary>
        public static string ComposeSwitchedOff(ulong objectId, string objectName)
        {
            string named = string.IsNullOrEmpty(objectName) ? "<unnamed>" : objectName;
            return
                $"[RTMPE] Networked object {objectId} ('{named}') is receiving remote " +
                "transform updates, and its NetworkTransformInterpolator is switched off: " +
                "the component never runs, so the motion is buffered and never rendered and " +
                "the object stays frozen on this client while its replication traffic " +
                "continues to arrive.  Enable the NetworkTransformInterpolator already on " +
                "the object — a second one is not needed and does not help.  Logged once " +
                "per object, for the first few hundred objects of a session.";
        }

        /// <summary>
        /// Whether <paramref name="objectId"/> has already been surfaced in this
        /// process.  Exposed for test fixtures and editor diagnostics; production
        /// code drives the gate through <see cref="ShouldWarn"/>.
        /// </summary>
        internal static bool WasSurfaced(ulong objectId) => s_surfaced.ContainsKey(objectId);

        /// <summary>
        /// Drop every surfaced id.
        ///
        /// Called at session end, where every object these ids name is already
        /// gone, and from <c>NetworkManager</c>'s SubsystemRegistration hook for
        /// a play-mode exit with domain reload disabled that reaches no session
        /// teardown at all.  Between them the set is bounded by a session rather
        /// than by the life of the process.
        ///
        /// ⛔ Not because a stale entry would suppress the advisory for a later
        /// object: <c>ObjectIdMath.Compose</c> mixes the gateway session id into
        /// the high half precisely so a reconnect cannot replay the previous
        /// session's id space, so an id from a dead session is one no live
        /// object can be issued.  The entries are dead weight, not a wrong
        /// answer — which is why this is a reset and not a correction, and why
        /// the ceiling above is what actually bounds a single long session.
        ///
        /// The reset lives here rather than behind a UnityEngine attribute
        /// because this file deliberately carries no UnityEngine dependency —
        /// the same arrangement <c>RemoteMotionTimingAdvisory</c> uses.
        /// </summary>
        internal static void ResetLatch()
        {
            s_surfaced.Clear();
            Volatile.Write(ref s_surfacedCount, 0);
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Drains the per-object latch so a fixture observes the first-surface
        /// path from a clean precondition.
        /// </summary>
        internal static void ResetForTests() => ResetLatch();
#endif // UNITY_INCLUDE_TESTS
    }
}
