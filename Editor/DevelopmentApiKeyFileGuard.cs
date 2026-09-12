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

            if (DevelopmentApiKeyStaging.RefusesTheBuild(development, present))
                throw new BuildFailedException(DevelopmentApiKeyStaging.Refusal());

            bool optedIn = EditorPrefs.GetBool(
                EditorProjectScope.Scoped(DevelopmentApiKeyStaging.OptInPreference), false);

            // ⛔ Asked before the injection decision and only for a release
            // build: the scan walks the project's scripts, and a development
            // build has an answer already — the injected key, or the player's
            // own runtime report naming the switch.
            if (ReleaseCredentialPathAdvisory.ShouldWarn(
                    development, ProjectRegistersAnApiKeyProvider()))
                Debug.LogWarning(ReleaseCredentialPathAdvisory.Message());

            string key = ApiKeyStore.Load();

            if (!DevelopmentApiKeyStaging.InjectsIntoTheBuild(
                    development, optedIn, !string.IsNullOrWhiteSpace(key)))
                return;

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
        // ⚠️ A text search, and it says so: a call inside `#if UNITY_EDITOR`, or
        // in a file excluded from the player's assemblies, reads the same as a
        // live one. That is the right direction for a WARNING — it errs towards
        // silence, and a warning that cried wolf on a project which does
        // register one would be switched off by the person who most needs it.
        //
        // Release builds only, so the walk is not paid on the iteration loop.
        private static bool ProjectRegistersAnApiKeyProvider()
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(
                             Application.dataPath, "*.cs", SearchOption.AllDirectories))
                {
                    if (File.ReadAllText(file)
                            .Contains(ReleaseCredentialPathAdvisory.ProviderRegistration))
                        return true;
                }
            }
            catch (Exception)
            {
                // ⛔ Unreadable project, unreadable answer: claim one IS
                // registered rather than warn on a scan that did not finish. A
                // build hook must not invent a fault out of its own failure.
                return true;
            }

            return false;
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
                // A build that has already produced its player is not failed for
                // a clean-up; the credential is named so somebody removes it, and
                // the next release build refuses until they do.
                Debug.LogError(
                    "[RTMPE] The development API key could not be removed from "
                    + DevelopmentApiKeyStaging.ProjectPath
                    + " (" + e.GetType().Name + "). Delete it: it is a credential, and a "
                    + "release build will refuse while it is there.");
            }
        }
    }
}
