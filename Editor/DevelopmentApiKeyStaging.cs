// RTMPE SDK — Editor/DevelopmentApiKeyStaging.cs
//
// The key a DEVELOPMENT build carries: injected while that build runs, removed
// when it finishes, and refused outright in a release build.
//
// The wizard's vault is the Editor's and no build carries it, so a project the
// wizard has fully configured still produces a player with no credential — and a
// double-clicked application inherits no argument vector at all, while the
// environment it inherits is the desktop session's and not the shell the Editor
// was started from, so testing a standalone build meant writing code or setting
// a variable for the whole machine.  StreamingAssets is copied into a build verbatim, so a file there
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
        /// Whether a staged key is sitting in the project right now — the
        /// residue of a build that did not finish, which the next build refuses.
        /// </summary>
        /// <remarks>
        /// ⛔ Asked of the same absolute path the build hook and the reload check
        /// ask about, so the three cannot disagree about what is there; a reader
        /// that cannot tell answers <see langword="false"/>, because the two
        /// controls that matter — the build's refusal and the ignore line — are
        /// in force either way and a false alarm is a message a developer learns
        /// to ignore on the one day it is true.
        /// </remarks>
        internal static bool ResidueIsInTheProject(string dataPath)
        {
            try
            {
                string path = AbsolutePath(dataPath);
                return path != null && System.IO.File.Exists(path);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

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

        /// <summary>What a build is owed by the injection, and why.</summary>
        internal enum StagingVerdict
        {
            /// <summary>This build asked for nothing: it is not a development
            /// build, the project has not switched the injection on, or the
            /// vault is empty.</summary>
            NotThisBuild,

            /// <summary>Everything was asked for and the target cannot read the
            /// file, so the key is withheld and the reason is said.</summary>
            WithheldForThePlatform,

            /// <summary>The key is written for the length of the build.</summary>
            Inject,
        }

        /// <summary>
        /// Whether this build receives the key, and when it does not, whether
        /// that is worth saying.
        /// </summary>
        /// <remarks>
        /// Four conditions, and none of them is a default: the build must be a
        /// development one, the developer must have switched the injection on
        /// for this project, the vault must hold something to inject, and the
        /// target must be one a player can read the staged file on.
        /// <para>
        /// ⛔ The platform is the one condition whose failure is ANNOUNCED. The
        /// other three are the developer's own settings, and a build that
        /// reports them back is noise; the platform is a fact about the target
        /// that the switch does not mention, and the build it produces would
        /// otherwise carry an extractable credential the player cannot use —
        /// on Android the file sits inside the APK in plain text, and the read
        /// throws on every launch.
        /// </para>
        /// <para>
        /// One verdict rather than a predicate beside a predicate: the decision
        /// to withhold and the decision to say so are the same decision, and
        /// two functions could come to disagree about which build they are
        /// describing.
        /// </para>
        /// </remarks>
        internal static StagingVerdict VerdictForTheBuild(
            bool development, bool optedIn, bool keyAvailable, bool platformReadsTheFile)
        {
            if (!ConsultsTheVault(development, optedIn) || !keyAvailable) return StagingVerdict.NotThisBuild;

            return platformReadsTheFile ? StagingVerdict.Inject : StagingVerdict.WithheldForThePlatform;
        }

        /// <summary>
        /// Whether this build's verdict can depend on what the vault holds — and
        /// so whether the build hook may ask it.
        /// </summary>
        /// <remarks>
        /// ⛔ The vault is not free to ask. <c>ApiKeyStore.Load</c> starts the
        /// platform's credential tool — <c>security</c> on macOS,
        /// <c>secret-tool</c> on Linux — or makes a DPAPI call on Windows, and a
        /// locked keychain or keyring can answer with an unlock prompt. A hook
        /// that read it on every build paid a process, and possibly a dialog, on
        /// every RELEASE build, whose verdict is
        /// <see cref="StagingVerdict.NotThisBuild"/> whatever the vault holds.
        /// <para>
        /// One condition, stated once: <see cref="VerdictForTheBuild"/> reads the
        /// key's availability behind exactly this, so the hook and the verdict
        /// cannot come to disagree about which builds the vault matters to.
        /// </para>
        /// </remarks>
        internal static bool ConsultsTheVault(bool development, bool optedIn)
            => development && optedIn;

        /// <summary>What the build log says when the target cannot read it.</summary>
        internal static string NotStagedForPlatform(string platformName)
            => "[RTMPE] The development key was NOT injected into this " + platformName
             + " build. " + platformName + " keeps streaming assets inside the application "
             + "archive, so a player cannot read " + ProjectPath + " — the key would have "
             + "shipped in plain text, extractable by anyone with the build and usable by "
             + "nobody. What this build authenticates with is whatever your own code registers "
             + "(" + ReleaseCredentialPathAdvisory.ProviderRegistration + "), the same as a "
             + "release build.";

        /// <summary>
        /// Whether a build carrying a leftover key must be refused.
        /// </summary>
        /// <remarks>
        /// ⛔ It is a refusal rather than a clean-up.  A file here means a
        /// previous build did not finish; a build that quietly deleted a
        /// credential and carried on would leave nobody knowing it had ever
        /// been there.
        /// <para>
        /// 🔴 Two builds are refused, and the second was missed: a RELEASE
        /// build, which must never carry one — and a build for a target that
        /// cannot READ one, development or not.  StreamingAssets is collected
        /// after this callback runs, so a residue the injection did not write
        /// is copied into the player all the same: an Android development
        /// build found one, was told the key had been withheld for its
        /// platform, and shipped it anyway — in plain text, inside the archive,
        /// extractable by anyone holding the build and usable by nobody.  The
        /// post-build clean-up then deleted the evidence on success.
        /// </para>
        /// </remarks>
        internal static bool RefusesTheBuild(bool development, bool present, bool platformReadsTheFile)
            => present && (!development || !platformReadsTheFile);

        /// <summary>What the refusal says when the target cannot read one.</summary>
        internal static string RefusalForPlatform(string platformName)
            => "[RTMPE] " + ProjectPath + " is in the project and this is a " + platformName
             + " build. " + platformName + " keeps streaming assets inside the application "
             + "archive, so the player cannot read that file — but the build still SHIPS it, "
             + "in plain text, to everyone who receives the build. It is written only while a "
             + "development build runs and removed when one finishes, so finding it here means "
             + "a build did not finish: delete it and build again. RTMPE will not delete a "
             + "credential from your project for you.";

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
        /// available, whether the injection is switched on, and whether a
        /// player for the target the Editor is switched to could read the file.
        /// </summary>
        /// <remarks>
        /// ⛔ The platform sentence is said while the switch is ON and only
        /// then: switched off, the control is an offer and the target may well
        /// change before it is taken; switched on, "development builds of this
        /// project receive this key" is a sentence the next build will falsify,
        /// and the developer learns it here rather than from a tester holding a
        /// build that cannot connect.
        /// </remarks>
        internal static string Status(
            bool hasKey, bool optedIn, bool platformReadsTheFile, string platformName)
        {
            if (!hasKey)
            {
                return "Store an API key above first — there is nothing to inject yet.";
            }

            if (optedIn && !platformReadsTheFile)
            {
                return "⚠️ This project's build target is " + platformName + ", and a "
                     + platformName + " player cannot read " + ProjectPath + " — it is inside "
                     + "the application archive there. A development build for this target is "
                     + "made WITHOUT the key and says so in the build log; what it authenticates "
                     + "with is whatever your own code registers. Switch the target to a desktop "
                     + "platform to use this control, and register a provider for the platform "
                     + "you actually ship.";
            }

            if (!optedIn && !platformReadsTheFile)
            {
                // ⛔ Said with the switch OFF too, and in one line: the offer
                // below promises a build that connects without any code, and
                // for this target that promise is false before it is taken.
                return "Switch this on and a development build of this project connects without "
                     + "any code — but not for this project's current build target ("
                     + platformName + "), where a player cannot read " + ProjectPath + " and the "
                     + "build is made without it.";
            }

            return optedIn
                ? "Development builds of this project receive this key: it is written to "
                  + ProjectPath + " while the build runs and removed when the build SUCCEEDS. "
                  + "Unity runs that clean-up on success only, so a build that fails partway "
                  + "leaves the file in your project — which is why RTMPE adds it to your "
                  + "repository's ignore list and says so above, and why the Editor reports "
                  + "one it finds. ⛔ That build reads the key — do not distribute one, to "
                  + "testers or to a store."
                : "Switch this on and a development build of this project connects without any "
                  + "code. The key is injected for the length of the build only; release builds "
                  + "are refused if one is ever found in the project.";
        }

        /// <summary>The line a project's <c>.gitignore</c> is owed.</summary>
        /// <remarks>
        /// <para>
        /// ⚠️ Still owed even though nothing is stored: a build that crashes
        /// between the two callbacks leaves the file, and the next `git add -A`
        /// would put a credential in history that outlives its deletion.
        /// </para>
        /// <para>
        /// ⛔ The bare file name, with no directory in it, because a pattern
        /// containing a slash is anchored to the directory the ignore file
        /// lives in — and that directory is very often NOT the Unity project's.
        /// A repository laid out `repo/Game/Assets` keeps its ignore file at
        /// `repo/`, where `Assets/StreamingAssets/rtmpe.devkey` matches
        /// nothing at all.  An unanchored name matches at every depth, which
        /// is the one shape that is right wherever the file turns out to be.
        /// </para>
        /// </remarks>
        internal const string GitignoreLine = RTMPE.Core.DevelopmentApiKeyFile.FileName;

        /// <summary>The line for the record Unity writes beside the staged key.</summary>
        /// <remarks>
        /// ⛔ Owed for the reason the key's own line is, one file over: the
        /// injection refreshes the asset database, so Unity writes
        /// <c>rtmpe.devkey.meta</c> beside the key, and a build that fails
        /// between the two callbacks leaves both.  The record holds no
        /// credential — but committed, it names a file every other clone lacks
        /// and says where a key was kept, so the pair is kept out together.
        /// The bare name, unanchored, for the reason <see cref="GitignoreLine"/>
        /// gives.
        /// </remarks>
        internal const string GitignoreMetaLine = RTMPE.Core.DevelopmentApiKeyFile.FileName + ".meta";

        /// <summary>Whether an ignore file already keeps both out, at any depth.</summary>
        /// <remarks>
        /// ⛔ A line equal to each bare name, and nothing else counts.  This
        /// deliberately no longer accepts `StreamingAssets/`: that pattern is
        /// anchored, so in a repository whose root sits above the Unity project
        /// it ignores nothing while reading exactly like coverage — an answer
        /// of "already ignored" there is the credential going into history.
        /// The cost of the strict reading is one redundant line in a project
        /// that was in fact covered; the cost of the loose one is the finding.
        /// <para>
        /// The bare line, exactly: an earlier `Assets/StreamingAssets/rtmpe.devkey`
        /// is not accepted as already ignoring the file, because that pattern
        /// ignores it only when the ignore file sits at the Unity project root,
        /// and this file is walked to wherever the repository keeps it.  A
        /// project upgraded from that line gains the bare one beside it — and a
        /// project that has the key's line alone gains the record's.
        /// </para>
        /// </remarks>
        internal static bool AlreadyIgnored(string gitignore) => LinesOwed(gitignore).Count == 0;

        /// <summary>
        /// The lines <paramref name="gitignore"/> does not carry yet, in the order
        /// they are written: the key's, then its record's.
        /// </summary>
        /// <remarks>
        /// What is owed rather than whether anything is, because the writer
        /// appends exactly this: a project that ignored the key before the
        /// record's line existed is given the record's line and not a second
        /// copy of the one it has.
        /// </remarks>
        internal static System.Collections.Generic.List<string> LinesOwed(string gitignore)
        {
            var present = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            if (gitignore != null)
            {
                foreach (string line in gitignore.Split('\n')) present.Add(line.Trim());
            }

            var owed = new System.Collections.Generic.List<string>(2);
            if (!present.Contains(GitignoreLine))     owed.Add(GitignoreLine);
            if (!present.Contains(GitignoreMetaLine)) owed.Add(GitignoreMetaLine);
            return owed;
        }

        /// <summary>What asking to keep the staged key out of version control did.</summary>
        internal enum VersionControl
        {
            /// <summary>No repository was found at or above the project.</summary>
            NoRepositoryFound,

            /// <summary>Its ignore file already carried both lines.</summary>
            AlreadyIgnored,

            /// <summary>The lines it lacked were appended to an existing ignore file.</summary>
            LineAdded,

            /// <summary>There was no ignore file, so one was written.</summary>
            IgnoreFileWritten,

            /// <summary>A repository was found and its ignore file could not be written.</summary>
            CouldNotWrite,
        }

        /// <summary>
        /// The directory of the repository <paramref name="projectRoot"/> is in,
        /// or <see langword="null"/> when no repository is found at or above it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⛔ The walk is the repair.  The previous version looked in the Unity
        /// project's own root and nowhere else, so the common `repo/Game/Assets`
        /// layout — a Unity project one directory inside its repository — got no
        /// ignore line and no word about it, which is exactly the case where one
        /// `git add -A` after a failed build commits an API key.
        /// </para>
        /// <para>
        /// A repository is a directory holding a `.git`, as a directory or as a
        /// FILE: a worktree and a submodule keep a file there, and a rule that
        /// only knew the directory would walk past the root it was standing in.
        /// </para>
        /// <para>
        /// The walk terminates without a bound because each step is strictly
        /// shorter than the last — <c>Path.GetDirectoryName</c> answers
        /// <see langword="null"/> at the filesystem root.
        /// </para>
        /// </remarks>
        internal static string RepositoryRootAbove(string projectRoot)
        {
            string directory = projectRoot;

            while (!string.IsNullOrEmpty(directory))
            {
                string git = System.IO.Path.Combine(directory, ".git");
                if (System.IO.Directory.Exists(git) || System.IO.File.Exists(git))
                    return directory;

                directory = System.IO.Path.GetDirectoryName(directory);
            }

            return null;
        }

        /// <summary>
        /// Add the staged key to the ignore file of the repository this project
        /// is in, and answer what that did.
        /// </summary>
        /// <remarks>
        /// ⛔ The answer is returned rather than swallowed.  Every outcome here
        /// is one a developer may need to act on — most of all the two that
        /// leave the file unignored — and the previous version returned in
        /// silence from three of them.
        /// </remarks>
        internal static VersionControl KeepOutOfVersionControl(string projectRoot)
        {
            string root = RepositoryRootAbove(projectRoot);
            if (root == null) return VersionControl.NoRepositoryFound;

            string ignoreFile = System.IO.Path.Combine(root, ".gitignore");

            try
            {
                bool exists = System.IO.File.Exists(ignoreFile);
                var owed = LinesOwed(exists ? System.IO.File.ReadAllText(ignoreFile) : null);
                if (owed.Count == 0) return VersionControl.AlreadyIgnored;

                // A file that does not end in a newline would otherwise have
                // the comment appended to its last pattern, changing it.
                var text = new System.Text.StringBuilder(
                    exists ? System.Environment.NewLine : string.Empty);
                text.Append("# RTMPE: an API key a development build injects, and the record "
                            + "Unity writes beside it — never commit either.")
                    .Append(System.Environment.NewLine);
                foreach (string line in owed) text.Append(line).Append(System.Environment.NewLine);

                System.IO.File.AppendAllText(ignoreFile, text.ToString());

                return exists ? VersionControl.LineAdded : VersionControl.IgnoreFileWritten;
            }
            catch (System.Exception)
            {
                // ⛔ By outcome, never by exception: a file API quotes the path
                // it was handed, and the developer is going to be shown this.
                return VersionControl.CouldNotWrite;
            }
        }

        /// <summary>What the wizard shows beside the switch about version control.</summary>
        /// <remarks>
        /// One statement of each, here, for the reason the rest of this file
        /// gives: the window that draws them is compiled by no test project.
        /// </remarks>
        internal static string VersionControlStatus(VersionControl outcome)
        {
            switch (outcome)
            {
                case VersionControl.AlreadyIgnored:
                    return "Your repository already ignores " + GitignoreLine + " and "
                         + GitignoreMetaLine + ".";
                case VersionControl.LineAdded:
                    return "Your repository's .gitignore now ignores " + GitignoreLine + " and "
                         + GitignoreMetaLine + " — a build that fails partway leaves the key "
                         + "in the project, and this keeps it out of a commit.";
                case VersionControl.IgnoreFileWritten:
                    return "Your repository had no .gitignore, so RTMPE wrote one ignoring "
                         + GitignoreLine + " and " + GitignoreMetaLine + " — a build that "
                         + "fails partway leaves the key in the project, and this keeps it out "
                         + "of a commit.";
                case VersionControl.CouldNotWrite:
                    return "⛔ RTMPE could not write your repository's .gitignore — please "
                         + "add a line reading " + GitignoreLine + " and one reading "
                         + GitignoreMetaLine + " to it yourself: a build that fails partway "
                         + "leaves an API key in this project, and a commit after that puts it "
                         + "in your history for good.";
                default:
                    return "⛔ No repository was found at or above this project. If you keep "
                         + "this project in version control, add a line reading " + GitignoreLine
                         + " and one reading " + GitignoreMetaLine + " to its ignore file: a "
                         + "build that fails partway leaves an API key in this project, and a "
                         + "commit after that puts it in your history for good.";
            }
        }

        /// <summary>
        /// What the Editor says when it finds a staged key sitting in the
        /// project outside a build.
        /// </summary>
        /// <remarks>
        /// ⛔ A report, not a deletion.  The file is a credential and removing
        /// it silently would leave nobody knowing it had been there — the same
        /// reason the release build refuses rather than tidying.  What this
        /// changes is WHEN it is noticed: the clean-up runs on a successful
        /// build only, so before this the residue of a failed one waited for
        /// the next RELEASE build to be mentioned at all, which may be months
        /// and many commits away.  ⚠️ It is heard on the Editor's next domain
        /// reload rather than the instant the build fails; that is minutes, not
        /// months, and it is the earliest an Editor-side check runs.
        /// </remarks>
        internal static string ResidueReport()
            => "[RTMPE] " + ProjectPath + " is in this project outside a build. It holds an API "
             + "key: the injection writes it while a development build runs and removes it when "
             + "that build SUCCEEDS, so finding it here means a build did not finish. Delete it "
             + "— and check that it was never committed. RTMPE will not delete a credential "
             + "from your project for you, and a build REFUSES TO RUN while it is there: every "
             + "release build, and a development build for a target that cannot read the file "
             + "(Android, WebGL), which would ship it without being able to use it. A "
             + "development build for a desktop target does run — it is the build the file "
             + "belongs to — and overwrites it with the key of the moment.";
    }
}
