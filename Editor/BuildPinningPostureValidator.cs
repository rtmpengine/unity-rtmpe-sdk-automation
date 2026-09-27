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

                unpinned = 0;
                pinned   = 0;

                string[] guids = AssetDatabase.FindAssets("t:NetworkSettings");
                if (guids != null)
                {
                    foreach (string guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        var settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(path);

                        // An asset that will not load is counted as NEITHER: calling
                        // it pinned would let an unreadable project read as safe.
                        if (settings == null) continue;

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

            var verdict = PinningPostureCheck.Classify(unpinned, pinned, development);
            if (verdict == PinningPostureVerdict.Pinned) return;

            // A warning for both verdicts — see the header — and the regression's
            // own mark, so the two are told apart in a log without the severity.
            string mark = PinningPostureCheck.IsARegression(verdict) ? "SECURITY: " : "";
            Debug.LogWarning("[RTMPE] " + mark + Describe(verdict, unpinned, pinned, firstUnpinnedAsset));
        }

        /// <summary>The sentence each verdict carries, naming an asset to open.</summary>
        private static string Describe(
            PinningPostureVerdict verdict, int unpinned, int pinned, string firstUnpinnedAsset)
        {
            const string What =
                "ServerPinningMode.InsecureNoPinning accepts any valid Ed25519 signature, so a " +
                "rogue gateway with its own keypair completes the handshake. Set " +
                "pinnedServerPublicKeyHex (Strict) or use TrustOnFirstUse.";

            string where = firstUnpinnedAsset != null ? " First one: " + firstUnpinnedAsset + "." : "";

            switch (verdict)
            {
                case PinningPostureVerdict.ReleaseBuildIsUnpinned:
                    return "This is a RELEASE build and every NetworkSettings asset in the " +
                           "project (" + unpinned + ") is unpinned. " + What + where;

                case PinningPostureVerdict.SomeAssetsAreUnpinned:
                    return "This is a RELEASE build and " + unpinned + " of " +
                           (unpinned + pinned) + " NetworkSettings assets are unpinned. This check " +
                           "cannot tell which asset your built scenes reference. " + What + where;

                case PinningPostureVerdict.DevelopmentBuildIsUnpinned:
                    return "This development build ships an unpinned NetworkSettings asset, " +
                           "which is what the mode is for — but a release build made from the same " +
                           "asset would accept any gateway. " + What + where;

                default:
                    return null;
            }
        }
    }
}
