// RTMPE SDK — Runtime/Core/DevelopmentApiKeyFile.cs
//
// The credential a DEVELOPMENT build may carry.
//
// The Setup Wizard's vault is the Editor's: it lives in the OS keychain and no
// build carries it.  Of the four sources a player has, three are unreachable
// when a player is launched the way a player is launched — a double-clicked
// .app inherits neither an argument vector nor the shell's environment — which
// left writing code as the only way to test a standalone build of a project the
// wizard had fully configured.
//
// ⛔ A staged file closes that, and what keeps it out of a shipped player is one
// control, not two: the build refuses to carry it (DevelopmentApiKeyStaging, in
// the Editor assembly).  This file's development-build test is a second control
// over READING it, which is a different question — a release build that somehow
// carries one ignores it, but the plaintext key is still inside the artifact for
// anyone who opens it.  ⚠️ And neither control can refuse the one path that
// matters most: a DEVELOPMENT build handed to a tester or uploaded to a store
// reads the key and carries it just as far.  That is said in the wizard, in the
// build refusal and in the documentation, because it is the only defence there
// is against it.
//
// ⚠️ The switch is read HERE rather than at the registration site.  A registrar
// that decided it would leave the decision in code the player runs once, where
// a refactor can drop it silently; asked on every resolution, a release build
// that somehow carries a staged key still answers no key from this source.
//
// ⛔ No UnityEngine dependency, so the rule is reachable from a headless test
// runner exactly as the receive path's advisories are.  What needs the engine —
// the streaming-assets path, the development-build flag, the registration — is
// DevelopmentApiKeyFileRegistrar, which is a shell over this.

using System;
using System.IO;

namespace RTMPE.Core
{
    internal static class DevelopmentApiKeyFile
    {
        /// <summary>
        /// The staged file's name, under <c>Assets/StreamingAssets/</c> in the
        /// project and under the player's streaming-assets directory in a build.
        /// </summary>
        internal const string FileName = "rtmpe.devkey";

        /// <summary>
        /// How this source names itself in a diagnostic.  A message telling a
        /// developer to change what it returns names a file they staged, rather
        /// than an internal seam they cannot call.
        /// </summary>
        internal const string SourceName = "the development-build key staged in StreamingAssets";

        /// <summary>
        /// The staged key, or <see langword="null"/> when this build may not
        /// have one, none was staged, or the platform keeps streaming assets
        /// somewhere a file read cannot reach.
        /// </summary>
        internal static string Resolve(
            bool developmentBuild, string streamingAssetsPath, Func<string, string> readAllText)
        {
            if (!developmentBuild) return null;
            if (string.IsNullOrEmpty(streamingAssetsPath) || readAllText == null) return null;

            string contents;
            try
            {
                contents = readAllText(Path.Combine(streamingAssetsPath, FileName));
            }
            // ⛔ An absent file is this source answering "no key", never a
            // failure: nobody named it on a launch line, so there is no
            // configuration to have got wrong.  The named-file source
            // (--rtmpe-api-key-file) is the one that must fail loudly, and it
            // does, one method over.
            //
            // ⚠️ Android and WebGL keep streaming assets inside an archive or
            // behind a URL, so the read throws there on every launch.  That is a
            // platform saying "not here"; a development build on one of them
            // uses a provider like any other player.
            catch (IOException)                        { return null; }
            catch (UnauthorizedAccessException)        { return null; }
            catch (NotSupportedException)              { return null; }
            catch (ArgumentException)                  { return null; }
            catch (System.Security.SecurityException)  { return null; }

            return string.IsNullOrWhiteSpace(contents) ? null : contents.Trim();
        }
    }
}
