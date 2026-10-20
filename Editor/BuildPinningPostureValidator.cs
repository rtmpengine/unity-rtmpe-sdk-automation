// RTMPE SDK — Editor/BuildPinningPostureValidator.cs
//
// Build-time signal: a release Player build that ships
// `ServerPinningMode.InsecureNoPinning` accepts any gateway that can produce a
// valid Ed25519 signature — a rogue server with its own keypair completes the
// handshake — and before this the only thing that said so was a runtime log
// line, once per second, in a player already in somebody's hands (S4-53).
//
// ⛔ It reports and never blocks.  The field is named `InsecureNoPinning`, an
// integrator selecting it has been told what it is, and a build against a local
// gateway is a legitimate thing to make; refusing a ship over a posture the owner
// chose is the owner's call.  What is owed is that the choice cannot be made by
// accident, or inherited from a template, and reach a release unremarked.
// "Never blocks" includes the log severity: Unity counts every error logged
// while a build runs into `BuildReport.summary.totalErrors` and reports the
// build Failed, so a release build's regression is a WARNING line marked
// SECURITY — a mark a CI log scraper filters on as it would on the severity —
// and not an error.
//
// The whole body is guarded, as the sibling envelope validator's is: a fault in
// this check must never be what stops a legitimate build.

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Core.Diagnostics;
using RTMPE.Crypto;

namespace RTMPE.Editor
{
    internal sealed class BuildPinningPostureValidator : IPreprocessBuildWithReport
    {
        // After the envelope validator: that one can FAIL the build, and a failure
        // about a build that cannot connect at all is the more useful message to
        // reach first.
        public int callbackOrder => 1;

        public void OnPreprocessBuild(BuildReport report)
        {
            int unpinned;
            int pinned;
            int unreadable;
            bool development;
            string firstUnpinnedAsset = null;

            try
            {
                // The report is the authority for the build being MADE, which is
                // the question here — not what the Build Settings window happens
                // to show now.  IPreprocessBuildWithReport is always handed one;
                // without it this check cannot tell a release build from a
                // development one, and a signal that cannot make that distinction
                // is the one thing this check exists to add.
                if (report == null)
                {
                    Debug.LogWarning(
                        "[RTMPE] Build-time server-pinning check skipped: no build report, so a " +
                        "release build cannot be told from a development one.");
                    return;
                }

                development = (report.summary.options & BuildOptions.Development) != 0;

                unpinned   = 0;
                pinned     = 0;
                unreadable = 0;

                string[] guids = AssetDatabase.FindAssets("t:NetworkSettings");
                if (guids != null)
                {
                    foreach (string guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        var settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(path);

                        // An asset that will not load is counted as NEITHER: calling
                        // it pinned would let an unreadable project read as safe.
                        // It is counted as itself (audit P9-M1), so the verdict
                        // can say a build was not verified whole.
                        if (settings == null)
                        {
                            unreadable++;
                            continue;
                        }

                        if (settings.serverPinningMode == ServerPinningMode.InsecureNoPinning)
                        {
                            unpinned++;
                            firstUnpinnedAsset ??= path;
                        }
                        else
                        {
                            pinned++;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    "[RTMPE] Build-time server-pinning check skipped " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return;
            }

            var verdict = PinningPostureCheck.Classify(unpinned, pinned, development, unreadable);
            if (verdict == PinningPostureVerdict.Pinned) return;

            // A warning for every verdict — see the header — and the regression's
            // own mark, so the two are told apart in a log without the severity.
            string mark = PinningPostureCheck.IsARegression(verdict) ? "SECURITY: " : "";
            Debug.LogWarning("[RTMPE] " + mark + Describe(verdict, unpinned, pinned, unreadable, firstUnpinnedAsset));
        }

        /// <summary>
        /// What the unpinned mode does, said once for every verdict.
        /// </summary>
        internal const string What =
            "ServerPinningMode.InsecureNoPinning accepts any valid Ed25519 signature, so a " +
            "rogue gateway with its own keypair completes the handshake.";

        /// <summary>
        /// The remedy for a build that ships: Strict with the key pasted, the mode
        /// NetworkSettings itself says to use for builds you ship.
        /// </summary>
        /// <remarks>
        /// 🔑 Audit P9-M2: the one line marked SECURITY offered "Strict or
        /// TrustOnFirstUse" as equals — the weakest of three statements of one
        /// fact, and the one that runs at build time.  TrustOnFirstUse without
        /// requireFirstUseProvisioned pins whichever key answers each install's
        /// first connection, which is what that flag exists to close, so a
        /// release is told so rather than offered it as the alternative.
        /// </remarks>
        internal const string ReleaseRemedy =
            "Paste the gateway's key from the dashboard into pinnedServerPublicKeyHex and keep " +
            "Strict, the mode for builds you ship. TrustOnFirstUse in a release pins whichever " +
            "key answers each install's first connection unless requireFirstUseProvisioned is set.";

        /// <summary>The remedy for a development build, where first-run capture is a fair choice.</summary>
        internal const string DevelopmentRemedy =
            "Set pinnedServerPublicKeyHex (Strict) or use TrustOnFirstUse before a release build.";

        /// <summary>The sentence each verdict carries, naming an asset to open.</summary>
        private static string Describe(
            PinningPostureVerdict verdict, int unpinned, int pinned, int unreadable, string firstUnpinnedAsset)
        {
            string where = firstUnpinnedAsset != null ? " First one: " + firstUnpinnedAsset + "." : "";
            string unread = unreadable > 0
                ? " " + unreadable + " could not be loaded and were not checked."
                : "";

            switch (verdict)
            {
                case PinningPostureVerdict.ReleaseBuildIsUnpinned:
                    return "This is a RELEASE build and every NetworkSettings asset in the " +
                           "project (" + unpinned + ") is unpinned. " + What + " " + ReleaseRemedy + where;

                case PinningPostureVerdict.SomeAssetsAreUnpinned:
                    return "This is a RELEASE build and " + unpinned + " of " +
                           (unpinned + pinned + unreadable) + " NetworkSettings assets are unpinned." +
                           unread + " This check cannot tell which asset your built scenes reference. " +
                           What + " " + ReleaseRemedy + where;

                case PinningPostureVerdict.ReleaseBuildIsUnverified:
                    return "This is a RELEASE build and " + unreadable + " of " + (pinned + unreadable) +
                           " NetworkSettings assets could not be loaded, so whether this build pins its " +
                           "server was not verified. Reimport or recreate the unreadable asset, then rebuild.";

                case PinningPostureVerdict.DevelopmentBuildIsUnpinned:
                    return "This development build ships an unpinned NetworkSettings asset, " +
                           "which is what the mode is for — but a release build made from the same " +
                           "asset would accept any gateway. " + What + " " + DevelopmentRemedy + where;

                default:
                    return null;
            }
        }
    }
}
