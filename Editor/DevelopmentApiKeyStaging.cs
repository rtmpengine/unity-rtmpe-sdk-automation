// RTMPE SDK — Editor/DevelopmentApiKeyStaging.cs
//
// The key a DEVELOPMENT build carries: injected while that build runs, removed
// when it finishes, and refused outright in a release build.
//
// The wizard's vault is the Editor's and no build carries it, so a project the
// wizard has fully configured still produces a player with no credential — and a
// double-clicked application inherits neither an argument vector nor the shell's
// environment, which left writing code as the only way to test a standalone
// build.  StreamingAssets is copied into a build verbatim, so a file there
// reaches the player; the whole question is how long it exists.
//
// ⛔ It is never STORED.  The build writes it, the build removes it, and nothing
// in the project holds a credential between builds: there is no file to commit,
// none to forget, and none for a later release build to pick up.  What remains
// possible is a build that CRASHED between the two callbacks, and that is what
// the release refusal is for — it fails closed on a residue rather than tidying
// one away, because a build that silently deleted a credential it found is a
// build nobody can reason about.
//
// ⚠️ What this cannot do, stated rather than implied: refuse a PRODUCTION key.
// The platform mints one kind — `api_key_service.go` builds `"rtmpe-" + hex`,
// and no row, prefix or claim distinguishes a production key from any other — so
// no code here can tell them apart.  What is enforceable is consent and
// duration: the injection happens only for a project whose developer switched it
// on, only for a development build, and only for as long as that build takes.
//
// ⛔ The decisions are here, pure, because the callbacks that act on them are
// compiled by no test project: a rule about an IPreprocessBuildWithReport is a
// rule about its text, and the answer it gives is worth more than its shape.

namespace RTMPE.Editor
{
    internal static class DevelopmentApiKeyStaging
    {
        /// <summary>The folder Unity copies into a build verbatim.</summary>
        /// <remarks>
        /// ⛔ The ROOT of it, and the same root the runtime reads.  A key one
        /// directory deeper is an injection that does nothing, silently.
        /// </remarks>
        internal const string ProjectFolder = "Assets/StreamingAssets";

        /// <summary>The injected file, as an asset path — what a message shows.</summary>
        internal const string ProjectPath =
            ProjectFolder + "/" + RTMPE.Core.DevelopmentApiKeyFile.FileName;

        /// <summary>
        /// Where the file actually is, derived from the project's own
        /// <c>Application.dataPath</c>.
        /// </summary>
        /// <remarks>
        /// ⛔ Never the bare relative path.  <c>File.Exists</c> answers
        /// <see langword="false"/> for "not there" and for "cannot tell" alike,
        /// so a build run from a working directory that is not the project root
        /// would report nothing present and ship the credential.
        /// </remarks>
        internal static string AbsolutePath(string dataPath)
            => string.IsNullOrEmpty(dataPath)
                ? null
                : System.IO.Path.Combine(
                    dataPath, "StreamingAssets", RTMPE.Core.DevelopmentApiKeyFile.FileName);

        /// <summary>
        /// The per-project switch, kept in EditorPrefs — a machine's own
        /// setting, not a project file somebody commits.
        /// </summary>
        internal const string OptInPreference = "RTMPE_InjectDevelopmentKey";

        /// <summary>What the wizard's control says.</summary>
        /// <remarks>
        /// ⛔ One statement of it: the player's own log and the getting-started
        /// document both tell a developer to use this, and three hand-written
        /// copies is three chances for the instruction to name a control that is
        /// not there.
        /// </remarks>
        internal const string StageButton = "Inject this key into development builds";

        /// <summary>
        /// Whether this build receives the key.
        /// </summary>
        /// <remarks>
        /// Three conditions, and none of them is a default: the build must be a
        /// development one, the developer must have switched the injection on
        /// for this project, and the vault must hold something to inject.
        /// </remarks>
        internal static bool InjectsIntoTheBuild(bool development, bool optedIn, bool keyAvailable)
            => development && optedIn && keyAvailable;

        /// <summary>
        /// Whether a build carrying a leftover key must be refused.
        /// </summary>
        /// <remarks>
        /// ⛔ The refusal is on the RELEASE build, and it is a refusal rather
        /// than a clean-up.  A file here means a previous build did not finish;
        /// a release build that quietly deleted a credential and carried on
        /// would leave nobody knowing it had ever been there.
        /// </remarks>
        internal static bool RefusesTheBuild(bool development, bool present)
            => present && !development;

        /// <summary>What the refusal says.</summary>
        internal static string Refusal()
            => "[RTMPE] This is a release build and " + ProjectPath + " is in the project. "
             + "That file holds an API key and StreamingAssets ships verbatim, so the build "
             + "would carry the credential to everyone who downloads it. It is written only "
             + "while a development build runs and removed when one finishes, so finding it "
             + "here means a build did not finish — delete it and build again. ⛔ Ticking "
             + "Development Build is not the way past this: that build reads the key, and one "
             + "you hand to a tester or upload to a store carries it just as far. RTMPE will "
             + "not delete a credential from your project for you.";

        /// <summary>
        /// What the wizard says beside the control, given whether a key is
        /// available and whether the injection is switched on.
        /// </summary>
        internal static string Status(bool hasKey, bool optedIn)
        {
            if (!hasKey)
            {
                return "Store an API key above first — there is nothing to inject yet.";
            }

            return optedIn
                ? "Development builds of this project receive this key: it is written to "
                  + ProjectPath + " while the build runs and removed when it finishes, so "
                  + "nothing in your project holds a credential between builds. ⛔ That build "
                  + "reads the key — do not distribute one, to testers or to a store."
                : "Switch this on and a development build of this project connects without any "
                  + "code. The key is injected for the length of the build only; release builds "
                  + "are refused if one is ever found in the project.";
        }

        /// <summary>The line a project's <c>.gitignore</c> is owed.</summary>
        /// <remarks>
        /// ⚠️ Still owed even though nothing is stored: a build that crashes
        /// between the two callbacks leaves the file, and the next `git add -A`
        /// would put a credential in history that outlives its deletion.
        /// </remarks>
        internal const string GitignoreLine = "Assets/StreamingAssets/rtmpe.devkey";

        /// <summary>Whether a <c>.gitignore</c> already keeps it out.</summary>
        internal static bool AlreadyIgnored(string gitignore)
            => gitignore != null
               && (gitignore.Contains(RTMPE.Core.DevelopmentApiKeyFile.FileName)
                   || gitignore.Contains("StreamingAssets/"));
    }
}
