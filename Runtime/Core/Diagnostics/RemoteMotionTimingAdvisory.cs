// RTMPE SDK — Runtime/Core/Diagnostics/RemoteMotionTimingAdvisory.cs
//
// Remote motion timing is one feature split across two components:
//
//   • NetworkTransform._tickAlignedSampling (the SENDING half) carries the
//     broadcast pose back from the frame instant to the boundary of the tick it
//     is labelled with, so the pose a peer replays at tick N is the pose the
//     owner actually held at tick N.  It removes an error bounded by one frame
//     period.
//
//   • NetworkTransformInterpolator._ownerTickTimeline (the RECEIVING half)
//     builds the render timeline from the owner's input tick rather than the
//     server's emission counter.  It removes a larger error: which server tick
//     a pose is filed under is decided by when the uplink arrived, so the
//     server timeline re-imprints uplink jitter as up to a full tick of
//     placement error.
//
// Each half removes a different error term, so one alone is a real but partial
// improvement — not a no-op, and not a regression.  What it is not is the
// feature: a project that enables one and leaves the other off keeps the error
// the missing half exists to remove, and has no signal that a missing half is
// why.  This advisory is that signal.
//
// The decision and the message are pure managed code with no UnityEngine
// dependency, so both are exercised directly under the headless test runner;
// the Debug.LogWarning sink lives at the receive-dispatch call site, the same
// arrangement RemoteInterpolatorAdvisory uses.

using System.Threading;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Warn-once gate and message for a half-enabled remote-motion-timing
    /// configuration — the sending and receiving halves of one feature set to
    /// different values.
    /// </summary>
    internal static class RemoteMotionTimingAdvisory
    {
        /// <summary>
        /// How the two halves of remote motion timing relate to each other.
        /// </summary>
        internal enum Pairing
        {
            /// <summary>Both halves off (the default) or both on. Nothing to report.</summary>
            Consistent = 0,

            /// <summary>
            /// The receiver builds the timeline from the owner's tick, but the
            /// owner still broadcasts a frame-instant sample under a tick label.
            /// </summary>
            TimelineWithoutAlignedSampling = 1,

            /// <summary>
            /// The owner aligns its sample to the tick boundary, but the receiver
            /// places samples by the server's emission tick, so the alignment
            /// buys nothing the server's own placement error does not reintroduce.
            /// </summary>
            AlignedSamplingWithoutTimeline = 2,
        }

        // One latch slot per inconsistent pairing.  Plain ints rather than a
        // dictionary because this runs on the receive path — every non-owner
        // state delta, at the room's broadcast cadence — and a healthy project
        // must not pay a hash lookup, nor a boxing comparer under IL2CPP, to be
        // told nothing is wrong.  Interlocked keeps "exactly once" exact without
        // a lock or an allocation.
        private static int s_timelineWithoutAlignedSampling;
        private static int s_alignedSamplingWithoutTimeline;

        /// <summary>
        /// Classifies the two flags. <see cref="Pairing.Consistent"/> covers both
        /// the all-off default and the fully-enabled configuration; the other two
        /// values name which half is missing.
        /// </summary>
        public static Pairing Classify(bool ownerTickTimeline, bool tickAlignedSampling)
        {
            if (ownerTickTimeline == tickAlignedSampling)
                return Pairing.Consistent;
            return ownerTickTimeline
                ? Pairing.TimelineWithoutAlignedSampling
                : Pairing.AlignedSamplingWithoutTimeline;
        }

        /// <summary>
        /// Records <paramref name="pairing"/> as surfaced and reports whether this
        /// call is the first for it. Always returns <see langword="false"/> for
        /// <see cref="Pairing.Consistent"/>, so the caller needs no separate
        /// check: a correctly-configured project never reaches
        /// <see cref="Compose"/>.
        ///
        /// Latched per pairing, not per object: this is a project-wide
        /// configuration state, and one line per replica would say one thing
        /// about the build several hundred times. The message is worded to match
        /// that scope — see <see cref="Compose"/>.
        /// </summary>
        public static bool ShouldWarn(Pairing pairing)
        {
            switch (pairing)
            {
                case Pairing.TimelineWithoutAlignedSampling:
                    return Interlocked.CompareExchange(ref s_timelineWithoutAlignedSampling, 1, 0) == 0;
                case Pairing.AlignedSamplingWithoutTimeline:
                    return Interlocked.CompareExchange(ref s_alignedSamplingWithoutTimeline, 1, 0) == 0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Builds the advisory text for an inconsistent pairing, naming the half
        /// that is set, the half that is not, and the error the missing half
        /// exists to remove. Pure; centralised here so the test project asserts
        /// on a stable string and any wording change is a single reviewed edit.
        /// </summary>
        /// <remarks>
        /// The object is named as the one where the mismatch was OBSERVED, not as
        /// the only place it exists. The latch is per configuration, so a project
        /// with several mismatched prefabs surfaces whichever delivers the first
        /// remote update — the wording has to send the developer to every prefab
        /// carrying the pair, not just to this one. It also avoids asserting what
        /// the remote owner is doing: both flags are read from the LOCAL prefab,
        /// which answers "is this project configured consistently" and not "what
        /// did that peer send".
        /// </remarks>
        public static string Compose(Pairing pairing, ulong objectId, string objectName)
        {
            string named = string.IsNullOrEmpty(objectName) ? "<unnamed>" : objectName;
            string head =
                $"[RTMPE] Remote motion timing is half-enabled in this project. Seen on networked " +
                $"object {objectId} ('{named}'); any other prefab carrying the same pair is affected " +
                "too. ";
            string body = pairing == Pairing.TimelineWithoutAlignedSampling
                ? "NetworkTransformInterpolator.OwnerTickTimeline is on but " +
                  "NetworkTransform.TickAlignedSampling is off, so remote objects are replayed on " +
                  "the owner's tick while that tick still labels a frame-instant sample: the " +
                  "frame-quantisation error tick-aligned sampling exists to remove — up to one " +
                  "frame period — stays in rendered motion. "
                : "NetworkTransform.TickAlignedSampling is on but " +
                  "NetworkTransformInterpolator.OwnerTickTimeline is off, so the broadcast pose is " +
                  "corrected onto its own tick boundary and then filed by the server's emission " +
                  "tick, which is chosen by uplink arrival time: the placement error the owner-tick " +
                  "timeline exists to remove — up to a full tick — stays in rendered motion. ";
            return head + body +
                "These are the sending and receiving halves of one feature and must be enabled " +
                "together for the whole correction. Logged once per configuration.";
        }

        /// <summary>
        /// Whether <paramref name="pairing"/> has already been surfaced in this
        /// process. Exposed for test fixtures and editor diagnostics; production
        /// code drives the gate through <see cref="ShouldWarn"/>.
        /// </summary>
        internal static bool WasSurfaced(Pairing pairing)
        {
            switch (pairing)
            {
                case Pairing.TimelineWithoutAlignedSampling:
                    return Volatile.Read(ref s_timelineWithoutAlignedSampling) != 0;
                case Pairing.AlignedSamplingWithoutTimeline:
                    return Volatile.Read(ref s_alignedSamplingWithoutTimeline) != 0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Re-arm the latch for a fresh play session.
        ///
        /// Statics survive play-mode exit when Enter Play Mode Options has domain
        /// reload disabled — a standard iteration setting. With only two latch
        /// slots there is no per-object key to make the gate re-arm by itself, so
        /// a developer who fixed the configuration, re-entered play and saw
        /// silence would have no way to tell a fix from a dead advisory. The SDK
        /// already resets its other process statics from
        /// NetworkManager's SubsystemRegistration hook; this joins them, which is
        /// also why the reset lives here rather than behind a UnityEngine
        /// attribute this file deliberately does not depend on.
        /// </summary>
        internal static void ResetLatch()
        {
            Volatile.Write(ref s_timelineWithoutAlignedSampling, 0);
            Volatile.Write(ref s_alignedSamplingWithoutTimeline, 0);
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Drains the latch so a fixture observes the first-surface path from a
        /// clean precondition.
        /// </summary>
        internal static void ResetForTests() => ResetLatch();
#endif // UNITY_INCLUDE_TESTS
    }
}
