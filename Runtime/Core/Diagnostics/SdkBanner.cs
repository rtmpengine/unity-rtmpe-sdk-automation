// RTMPE SDK — Runtime/Core/Diagnostics/SdkBanner.cs
//
// The line a player writes before anything else happens, and the line an
// integrator pastes when they report a fault.
//
// 🔑 It states the development-build flag because that flag decides an answer
// the same log then gives.  The key a build can carry is injected only into a
// development build and read only by one, so a player reporting that it has no
// API key is either a project that never switched the injection on or a release
// build doing exactly what a release build must — and without the flag those two
// produce the identical log.  Telling them apart cost a round trip to whoever
// ran the build, twice, before this line said it.
//
// ⛔ Composed here rather than at the call site.  NetworkManager.Lifecycle.cs is
// compiled by no project in this repository, so a sentence written there can be
// asserted only as text; a pure composer is executed by a shard, which is the
// difference between pinning what the line SAYS and pinning how it is spelled.
//
// ⚠️ The flag is the engine's own `Debug.isDebugBuild`, which is true in the
// Editor as well as in a development player.  That is not a caveat to work
// around: it is the same reading the key path takes, and the platform token
// beside it already names the Editor.

namespace RTMPE.Core.Diagnostics
{
    internal static class SdkBanner
    {
        /// <summary>What the line calls a build the engine reports as debug.</summary>
        internal const string DevelopmentBuild = "development build";

        /// <summary>…and the other one.</summary>
        /// <remarks>
        /// ⛔ Named, never left as the absence of the first.  A reader deciding
        /// whether a key should have been carried needs the answer stated, and
        /// a line that is silent when the answer is "no" is a line they have to
        /// know the rule to read.
        /// </remarks>
        internal const string ReleaseBuild = "release build";

        /// <summary>The banner, whole.</summary>
        internal static string Compose(
            string version, string unityVersion, string platform, bool developmentBuild)
            => "[RTMPE] SDK " + Stated(version)
             + " — Unity " + Stated(unityVersion)
             + ", " + Stated(platform)
             + ", " + (developmentBuild ? DevelopmentBuild : ReleaseBuild) + ".";

        /// <summary>A field that has nothing to say, saying so.</summary>
        /// <remarks>
        /// ⛔ A placeholder rather than an empty span: this line is read by
        /// somebody comparing it with a report, and "SDK  — Unity 6000.3" makes
        /// a missing value look like a formatting slip in the reader's terminal.
        /// </remarks>
        private static string Stated(string value)
            => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }
}
