// RTMPE SDK — Runtime/Core/Diagnostics/PinningPostureCheck.cs
//
// Whether a build about to ship is shipping a server-pinning posture that
// accepts any gateway, and how loudly to say so.
//
// S4-53.  `ServerPinningMode.InsecureNoPinning` makes the SDK accept any valid
// Ed25519 signature — a rogue gateway with its own keypair completes the
// handshake — and the only thing that ever said so was a runtime log line, once
// per second, in a player that has already been distributed.  Nothing looked at
// it when the build was made, which is the last moment anybody can act.
//
// ⛔ Reported, never refused, and the reason is written down rather than left to
// be argued about: the field is called `InsecureNoPinning`, an integrator who
// selects it has been told what it is, and a build against a local gateway is a
// legitimate thing to make.  Blocking a ship on a posture the owner chose is the
// owner's decision, not the SDK's.  What the SDK owes is that the decision cannot
// be made by accident or by inheritance from a template.

namespace RTMPE.Core.Diagnostics
{
    /// <summary>How a build should be told about the pinning posture it carries.</summary>
    internal enum PinningPostureVerdict
    {
        /// <summary>Nothing to say: no asset carries the unpinned mode.</summary>
        Pinned,

        /// <summary>
        /// Every <c>NetworkSettings</c> asset in the project is unpinned, and this
        /// is a release build.  Unambiguous — whichever asset the built scenes
        /// reference, the player accepts any gateway.
        /// </summary>
        ReleaseBuildIsUnpinned,

        /// <summary>
        /// Some assets are unpinned and some are not.  A project-level check
        /// cannot tell which one the built scenes reference, so the risk is named
        /// without claiming it is certain.
        /// </summary>
        SomeAssetsAreUnpinned,

        /// <summary>
        /// Unpinned, in a development build.  That is what the mode exists for, so
        /// it is said once and quietly.
        /// </summary>
        DevelopmentBuildIsUnpinned,
    }

    /// <summary>
    /// The pure half of the build-time pinning check, so the decision is driven by
    /// tests rather than by reading the Editor callback that carries it.
    /// </summary>
    internal static class PinningPostureCheck
    {
        /// <summary>
        /// Classify a project by how many of its <c>NetworkSettings</c> assets ship
        /// the unpinned mode.
        /// </summary>
        /// <param name="unpinnedCount">Assets whose mode is InsecureNoPinning.</param>
        /// <param name="pinnedCount">
        /// Assets whose mode is anything else.  ⚠️ Counted rather than derived from
        /// a total: an asset that could not be LOADED is neither, and treating it
        /// as pinned would let an unreadable project read as safe.
        /// </param>
        /// <param name="developmentBuild">
        /// Whether this is a development build.  The distinction is the whole point
        /// of the check: the mode is documented for local development, so a
        /// development build that uses it is doing what it was made for, and a
        /// release build that does is the case nobody was told about.
        /// </param>
        internal static PinningPostureVerdict Classify(
            int unpinnedCount, int pinnedCount, bool developmentBuild)
        {
            if (unpinnedCount <= 0) return PinningPostureVerdict.Pinned;
            if (developmentBuild) return PinningPostureVerdict.DevelopmentBuildIsUnpinned;
            if (pinnedCount > 0)  return PinningPostureVerdict.SomeAssetsAreUnpinned;
            return PinningPostureVerdict.ReleaseBuildIsUnpinned;
        }

        /// <summary>
        /// Whether a verdict is a regression rather than a note: a release build
        /// that ships a gateway impersonation hole, as against a development
        /// build doing what its setting documents.
        /// </summary>
        /// <remarks>
        /// The distinction is what a CI log scraper filters on.  At build time it
        /// is carried as a warning line marked <c>SECURITY:</c> rather than as an
        /// error, because Unity counts an error logged during a build into the
        /// report's error total and reports the build Failed — a block decided by
        /// log severity, which the validator's design refuses to be.
        /// </remarks>
        internal static bool IsARegression(PinningPostureVerdict verdict) =>
            verdict == PinningPostureVerdict.ReleaseBuildIsUnpinned
            || verdict == PinningPostureVerdict.SomeAssetsAreUnpinned;
    }
}
