// RTMPE SDK — Editor/DevelopmentApiKeyFileGuard.cs
//
// The build half of the development key: injected before a development build,
// removed after every build, and a release build carrying one is refused.
//
// The decisions and the wording are DevelopmentApiKeyStaging's, where a test
// project executes them; this is the pair of callbacks that ask and act.
//
// ⛔ The order inside OnPreprocessBuild is the whole of the fail-closed
// property: the refusal is decided BEFORE anything is written, so a release
// build that finds a residue stops rather than being handed one by this file.
//
// ⚠️ Unity 2021.2 added BuildPlayerProcessor.PrepareForBuild, which can add a
// streaming-assets path from outside the project entirely and would keep the key
// out of Assets/ even for the length of a build.  It is not used here because no
// project in this repository compiles this file — the API cannot be checked
// against a compiler before it ships — and IPreprocessBuildWithReport is the
// shape this package already proves elsewhere.  Worth revisiting from an Editor
// that can compile it.

using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RTMPE.Editor
{
    internal sealed class DevelopmentApiKeyFileGuard
        : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        // ⛔ The clean-up below runs on a SUCCESSFUL build only — Unity decides
        // that, not this file — so a development build that fails partway
        // leaves an API key sitting in Assets/StreamingAssets.  Before this,
        // the first thing that would mention it was the next RELEASE build's
        // refusal, which may be months and many commits away; in the meantime
        // the file reads like project content.  This says so on the Editor's
        // next domain reload instead.
        //
        // ⛔ It reports and does not delete, for the reason the release refusal
        // gives: a tool that quietly removes a credential it finds leaves
        // nobody knowing it was ever there, and whether it reached a commit is
        // the question that matters.
        //
        // ⚠️ Not the instant the build fails — the earliest an Editor-side
        // check runs is the next reload — and the residue is also kept out of
        // version control by the ignore line the wizard writes when the switch
        // goes on.  Two controls, because each has a case the other misses: a
        // project with no repository gets no line, and a developer who never
        // reloads before committing gets no message.
        [InitializeOnLoadMethod]
        private static void ReportAStagedKeyLeftByABuildThatDidNotFinish()
        {
            try
            {
                string path = DevelopmentApiKeyStaging.AbsolutePath(Application.dataPath);
                if (path == null || !File.Exists(path)) return;

                Debug.LogWarning(DevelopmentApiKeyStaging.ResidueReport());
            }
            catch (Exception)
            {
                // A check that cannot read the project says nothing rather than
                // accusing it: the release refusal is still in force, and a
                // false alarm here would be a message a developer learns to
                // ignore on the one day it is true.
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            bool development =
                (report.summary.options & BuildOptions.Development) != 0;

            // ⛔ The path derived from the project's own dataPath, never the
            // bare relative one: File.Exists cannot distinguish "not there" from
            // "cannot tell", and a build whose working directory is not the
            // project root would answer "nothing there" and ship the key.
            string path = DevelopmentApiKeyStaging.AbsolutePath(Application.dataPath);
            bool present = path != null && File.Exists(path);

            // The target this build is FOR, read once and used by both
            // decisions below: what may be shipped, and what may be written.
            string platform = report.summary.platform.ToString();
            bool platformReadsTheFile = RTMPE.Core.DevelopmentApiKeyFile.PlatformReadsTheFile(platform);

            if (DevelopmentApiKeyStaging.RefusesTheBuild(development, present, platformReadsTheFile))
            {
                throw new BuildFailedException(
                    development
                        ? DevelopmentApiKeyStaging.RefusalForPlatform(platform)
                        : DevelopmentApiKeyStaging.Refusal());
            }

            bool optedIn = EditorPrefs.GetBool(
                EditorProjectScope.Scoped(DevelopmentApiKeyStaging.OptInPreference), false);

            // ⛔ Asked before the injection decision and only for a release
            // build: the scan walks the project's scripts, and a development
            // build has an answer already — the injected key, or the player's
            // own runtime report naming the switch. The `!development` ahead
            // of the policy is what keeps the walk off the iteration loop: an
            // argument is evaluated before the call, so without it every
            // development build read every script under Assets/ to reach a
            // decision that ignored the answer.
            if (!development && ReleaseCredentialPathAdvisory.ShouldWarn(
                    development, ProjectRegistersAnApiKeyProvider()))
                Debug.LogWarning(ReleaseCredentialPathAdvisory.Message());

            // ⛔ Asked only where the verdict can use the answer — a development
            // build the injection is switched on for. The vault is the OS
            // credential store, and a release build that asked it paid a process
            // and possibly an unlock prompt for an answer its verdict ignores.
            string key = DevelopmentApiKeyStaging.ConsultsTheVault(development, optedIn)
                ? ApiKeyStore.Load()
                : null;

            // The target is read from the report rather than from the Editor's
            // active setting: a build can be issued for a platform the Editor
            // is not switched to, and the file is being written for THIS build.
            var verdict = DevelopmentApiKeyStaging.VerdictForTheBuild(
                development, optedIn, !string.IsNullOrWhiteSpace(key), platformReadsTheFile);

            if (verdict == DevelopmentApiKeyStaging.StagingVerdict.WithheldForThePlatform)
            {
                Debug.LogWarning(DevelopmentApiKeyStaging.NotStagedForPlatform(platform));
            }

            if (verdict != DevelopmentApiKeyStaging.StagingVerdict.Inject) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, key.Trim());
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                // ⛔ The build FAILS rather than continuing without the key. A
                // development build that silently came out unable to connect is
                // the fault this whole feature exists to remove, and it would be
                // discovered on the far side of a build and a launch.
                //
                // ⚠️ The cause by TYPE, never the exception: a file API quotes
                // the path it was handed, and that path is one directory from a
                // credential a build log must not republish.
                throw new BuildFailedException(
                    "[RTMPE] Could not inject the development API key into "
                    + DevelopmentApiKeyStaging.ProjectPath
                    + " (" + e.GetType().Name + "). The build stopped rather than produce a "
                    + "player that cannot connect.");
            }
        }

        // Whether any script in this project registers a provider.
        //
        // The walk is ReleaseCredentialPathAdvisory's, shared with the wizard's
        // production step, so the build and the step read one project the same
        // way; a reading of code, with the direction that implies — see it
        // there.  Reached for release builds only (the caller's short-circuit),
        // so the walk is not paid on the iteration loop.
        private static bool ProjectRegistersAnApiKeyProvider()
        {
            try
            {
                // ⛔ A script the walk could not read counts as a registration
                // it may have missed, for the same reason as the arm below: the
                // warning errs towards silence, never towards a fault of its own.
                //
                // ⛔ The PLAYER's list, not every registration: the warning is
                // about the build being made, and a provider registered in an
                // Editor script is not in it — which is the case the wizard's
                // own step draws a ⚠️ for rather than a ✅.
                int unreadable;
                ReleaseCredentialPathAdvisory.RegistrationsIn(
                    Application.dataPath, out unreadable, out string[] compiledIntoPlayers);
                return compiledIntoPlayers.Length > 0 || unreadable > 0;
            }
            catch (Exception)
            {
                // ⛔ Unreadable project, unreadable answer: claim one IS
                // registered rather than warn on a scan that did not finish. A
                // build hook must not invent a fault out of its own failure.
                return true;
            }
        }

        // ⛔ Unconditional, and it asks nothing: whatever the build was and
        // however it ended up here, the credential does not outlive it. A
        // condition on this side is a way for one to.
        //
        // ⚠️ Unity runs this only when the build SUCCEEDS. A build that failed
        // between the two callbacks leaves the file, which is exactly what the
        // release refusal above is for.
        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                string path = DevelopmentApiKeyStaging.AbsolutePath(Application.dataPath);
                if (path == null || !File.Exists(path)) return;

                AssetDatabase.DeleteAsset(DevelopmentApiKeyStaging.ProjectPath);
                if (File.Exists(path)) File.Delete(path);
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                // The player is already written — and carries the key, as a
                // development build's does by design — and the error logged
                // here makes Unity report the build Failed, deliberately: what
                // the clean-up failed to remove is the copy in the PROJECT
                // TREE, a committable credential, and a pipeline must not pass
                // that build on its result alone.  The file is named so
                // somebody removes it, and the next release build refuses
                // until they do.
                Debug.LogError(
                    "[RTMPE] The development API key could not be removed from "
                    + DevelopmentApiKeyStaging.ProjectPath
                    + " (" + e.GetType().Name + "). Delete it: it is a credential, and a "
                    + "release build will refuse while it is there.");
            }
        }
    }
}
