// RTMPE SDK — Runtime/Core/DevelopmentApiKeyFileRegistrar.cs
//
// The engine-facing shell over DevelopmentApiKeyFile: where the streaming-assets
// path and the development-build flag come from, and where the source is
// registered.  The rule itself carries no UnityEngine dependency and is executed
// by the headless test runner; this file is the two lines that cannot be.
//
// ⚠️ `!UNITY_EDITOR` alone, and not a second symbol narrowing it to Unity: the
// source rules that read this package parse it under a fixed set of symbols, and
// a region behind one they do not define is a region no rule reads at all.  A
// file that no compiler and no rule can see is worse than a file that a headless
// project would refuse to compile — and no project compiles this one.

#if !UNITY_EDITOR
using System.IO;
using UnityEngine;

namespace RTMPE.Core
{
    internal static class DevelopmentApiKeyFileRegistrar
    {
        // ⛔ Excluded from the Editor rather than merely inert there: the Editor
        // assembly registers the wizard's vault in this same tier, and two
        // registrars in one slot is a rivalry whichever way it resolves.  In a
        // player that assembly does not exist, so this is the only claimant.
        //
        // ⚠️ BeforeSceneLoad, because RtmpeConnectionBootstrap may connect from
        // its own Awake in the first scene: a source registered after that has
        // nothing to answer.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            ApiKeySource.SetSecondaryProvider(
                () => DevelopmentApiKeyFile.Resolve(
                    Debug.isDebugBuild,
                    Application.streamingAssetsPath,
                    File.ReadAllText),
                DevelopmentApiKeyFile.SourceName);
        }
    }
}
#endif
