// RTMPE SDK — Editor/NetworkScenesInventory.cs
//
// What the project's build list says about the scene names its code asks for.
//
// A room's scene is a string, and until this file existed nothing compared that
// string to anything. `LoadScene("Map")` compiles; it writes a room property;
// the property reaches every client; and every client's engine then answers
// null because `Map` is not in Build Settings. From the room's side the result
// is a readiness deadline that names no cause, which is the same thing a slow
// client looks like.
//
// The comparison is one line. Everything below it is the part that decides
// whether the answer is worth reading: which spellings Unity accepts for one
// scene, what a DISABLED entry means, and what to say when the project holds
// two scenes of the same name.
//
// 🔑 Free of UnityEditor and UnityEngine, exactly as NetworkPrefabsInventory is.
// The build list arrives as plain data and the source arrives as text, so every
// rule here is reachable from a test — and the window supplies the editor. The
// alternative puts the whole contract behind a running Unity, where the only
// way to find out what a rule does is to make the mistake it judges.
//
// ⛔ The scan sees LITERALS. That is stated as a count rather than as a caveat:
// a call whose scene name is built at runtime is REPORTED, as unchecked, and
// the totals say how many there were. A check that silently passed over what it
// could not read would announce a clean project by looking at less of it.

using System;
using System.Collections.Generic;

namespace RTMPE.Editor
{
    /// <summary>
    /// One entry of the project's build list, as this layer sees it.
    /// </summary>
    public sealed class BuildScene
    {
        /// <summary>The project-relative asset path, e.g. <c>Assets/Scenes/Arena.unity</c>.</summary>
        public string Path { get; }

        /// <summary>Whether the entry is ticked in Build Settings.</summary>
        /// <remarks>
        /// ⛔ An unticked entry is not in the build. It is the trap this check
        /// exists for as much as a missing one is: the scene is visible in the
        /// list, reads as present, and <c>LoadSceneAsync</c> still answers null.
        /// </remarks>
        public bool Enabled { get; }

        /// <summary>Whether an asset still exists at <see cref="Path"/>.</summary>
        public bool AssetExists { get; }

        /// <summary>The file name without directory or <c>.unity</c> extension.</summary>
        public string Name => NetworkScenesInventory.NameOf(Path);

        public BuildScene(string path, bool enabled, bool assetExists)
        {
            Path        = path ?? string.Empty;
            Enabled     = enabled;
            AssetExists = assetExists;
        }
    }

    /// <summary>
    /// The project's build list, with the questions asked of it more than once.
    /// </summary>
    public sealed class SceneBuildList
    {
        /// <summary>Every entry, in the order Build Settings holds them.</summary>
        public IReadOnlyList<BuildScene> Entries { get; }

        /// <summary>Entries that are ticked.</summary>
        public int EnabledCount { get; }

        /// <summary>Ticked entries whose asset is gone.</summary>
        public int MissingAssetCount { get; }

        /// <summary>
        /// Names carried by more than one ticked entry.
        /// </summary>
        /// <remarks>
        /// 🔑 Unity resolves a bare name against the build list and takes the
        /// first match, so two scenes called <c>Arena</c> means one of them can
        /// never be loaded by name — and which one wins is decided by the order
        /// of a list nobody thinks of as ordered. The fix is to pass enough of
        /// the path to disambiguate, which is why the finding names both paths.
        /// </remarks>
        public IReadOnlyList<string> AmbiguousNames { get; }

        public SceneBuildList(IReadOnlyList<BuildScene> entries)
        {
            Entries = entries ?? Array.Empty<BuildScene>();

            int enabled = 0;
            int missing = 0;
            // 🔑 Case-insensitively, because that is how Unity resolves a bare
            // name: `SceneManager.LoadScene` documents `sceneName` as case
            // insensitive except from an AssetBundle, and a scene reached
            // through the build list is not from one. So two entries called
            // `Arena` and `arena` are one name as far as loading is concerned,
            // the first in the list wins, and the other can never be loaded by
            // name — the same finding as an exact duplicate, and it clears the
            // same two ways: pass enough of the path, or rename one.
            //
            // 🚨 It was ordinal until 2026-09-06, on a reason that read well and
            // was about the wrong subject: that flagging a project's own layout
            // is a warning nobody can clear. The warning is clearable, and what
            // the ordinal reading produced instead was a green over a scene the
            // project cannot load.
            var seen      = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new List<string>();

            foreach (var entry in Entries)
            {
                if (!entry.Enabled) continue;
                enabled++;
                if (!entry.AssetExists) missing++;

                string name = entry.Name;
                if (name.Length == 0) continue;
                seen.TryGetValue(name, out int count);
                seen[name] = count + 1;
                if (count == 1) ambiguous.Add(name);
            }

            EnabledCount      = enabled;
            MissingAssetCount = missing;
            AmbiguousNames    = ambiguous;
        }
    }

    /// <summary>
    /// What the build list has to say about one scene name.
    /// </summary>
    public enum SceneReferenceVerdict
    {
        /// <summary>
        /// Nothing here can be compared against the build list: the name is
        /// built at runtime, or the call is made on a loader the build list
        /// does not answer for.
        /// </summary>
        /// <remarks>
        /// ⚠️ The honest half of the check, and it is counted rather than
        /// hidden: a project whose scene names all come from a variable gets a
        /// window saying it checked nothing, instead of one saying everything
        /// is fine. The row's own sentence says which of the two it is.
        /// </remarks>
        NotChecked,

        /// <summary>The literal is empty, which the SDK refuses outright.</summary>
        Empty,

        /// <summary>No entry in the build list answers to this name.</summary>
        NotInBuild,

        /// <summary>
        /// An entry answers, but only once the comparison is loosened —
        /// surrounding space, or a path separator written the other way.
        /// </summary>
        /// <remarks>
        /// 🔑 Its own verdict, because both alternatives are wrong. Called a
        /// match, a stray space in a typed name — exactly the typo this window
        /// exists to catch — is reported green. Called a miss, a name that may
        /// well load is a red line under a project that works. What this check
        /// can say without guessing is that the two are not the same string, and
        /// that making them the same removes the question.
        /// <para>
        /// ⛔ Case is NOT one of these. Unity documents a bare name as case
        /// insensitive against the build list, so a query differing only in case
        /// loads — and reporting it was a red line under a project that works,
        /// which is the fault this verdict exists to avoid.
        /// </para>
        /// </remarks>
        SpellingDiffers,

        /// <summary>Entries answer, and every one of them is unticked.</summary>
        DisabledInBuild,

        /// <summary>More than one ticked entry answers to this name.</summary>
        AmbiguousName,

        /// <summary>One ticked entry answers, and its asset is gone.</summary>
        MissingAsset,

        /// <summary>One ticked entry answers, and it is there.</summary>
        Loadable,
    }

    /// <summary>
    /// A scene-load call together with the verdict on the name it passes.
    /// </summary>
    public sealed class SceneFinding
    {
        public SceneReference Reference { get; }

        public SceneReferenceVerdict Verdict { get; }

        /// <summary>The paths that answered to the name, for a verdict that names them.</summary>
        public IReadOnlyList<string> Candidates { get; }

        /// <summary>A sentence naming what is wrong and what to do, or null when nothing is.</summary>
        public string Explanation { get; }

        /// <summary>Whether this finding is something to act on.</summary>
        public bool IsProblem => Verdict != SceneReferenceVerdict.Loadable
                              && Verdict != SceneReferenceVerdict.NotChecked;

        public SceneFinding(SceneReference reference, SceneReferenceVerdict verdict,
                            IReadOnlyList<string> candidates, string explanation)
        {
            Reference   = reference;
            Verdict     = verdict;
            Candidates  = candidates ?? Array.Empty<string>();
            Explanation = explanation;
        }
    }

    /// <summary>
    /// Everything one pass of the check produced.
    /// </summary>
    public sealed class SceneInventory
    {
        public SceneBuildList Build { get; }

        public IReadOnlyList<SceneFinding> Findings { get; }

        /// <summary>Call sites whose scene name could be read.</summary>
        public int CheckedCount { get; }

        /// <summary>Call sites this layer has nothing to compare.</summary>
        public int NotCheckedCount { get; }

        /// <summary>Findings to act on.</summary>
        public int ProblemCount { get; }

        /// <summary>Source files the scan actually read.</summary>
        public int FilesScanned { get; }

        /// <summary>Source files the scan could not read.</summary>
        public int FilesUnreadable { get; }

        /// <summary>Source files the scan opened and could not read to the end.</summary>
        /// <remarks>
        /// A token that legally spans lines — a block comment, a verbatim
        /// string, a raw string, an interpolation hole — and is never closed
        /// consumes the rest of its file, so the calls below it were never
        /// looked for. Counted apart from
        /// <see cref="FilesUnreadable"/> because the two send a reader
        /// somewhere different: one is a file that would not open, the other a
        /// file whose text ends inside a token.
        /// </remarks>
        public int FilesPartlyRead { get; }

        /// <summary>
        /// Whether the pass saw less of the project than the project holds —
        /// because it hit its ceiling, or because part of the tree could not be
        /// walked.
        /// </summary>
        /// <remarks>
        /// ⛔ A truncated pass is never clean. It looked at less of the project
        /// than the project has, so its silence is a statement about coverage
        /// rather than about the code — and the two are indistinguishable to a
        /// reader unless one of them says so.
        /// </remarks>
        public bool Truncated { get; }

        /// <summary>Nothing to act on, and the pass was complete enough to say so.</summary>
        public bool IsClean => ProblemCount == 0
                            && FilesUnreadable == 0
                            && FilesPartlyRead == 0
                            && !Truncated
                            && Build.MissingAssetCount == 0
                            && Build.AmbiguousNames.Count == 0;

        public SceneInventory(SceneBuildList build, IReadOnlyList<SceneFinding> findings,
                              int filesScanned, int filesUnreadable, int filesPartlyRead,
                              bool truncated)
        {
            Build           = build ?? new SceneBuildList(Array.Empty<BuildScene>());
            Findings        = findings ?? Array.Empty<SceneFinding>();
            FilesScanned    = filesScanned;
            FilesUnreadable = filesUnreadable;
            FilesPartlyRead = filesPartlyRead;
            Truncated       = truncated;

            int examined = 0;
            int unread   = 0;
            int problems = 0;
            foreach (var finding in Findings)
            {
                if (finding.Verdict == SceneReferenceVerdict.NotChecked) unread++;
                else examined++;
                if (finding.IsProblem) problems++;
            }

            CheckedCount    = examined;
            NotCheckedCount = unread;
            ProblemCount    = problems;
        }
    }

    /// <summary>
    /// How much of a project one pass of the scan is allowed to read.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than an unbounded walk: this runs on a button in an
    /// editor, and a project with a vendored library under <c>Assets/</c> can
    /// hold tens of thousands of files. Hitting the ceiling is reported, so a
    /// short pass cannot be mistaken for a clean one.
    /// </remarks>
    public sealed class SceneScanBudget
    {
        /// <summary>Files one pass will open.</summary>
        public int MaxFiles { get; }

        /// <summary>Characters one pass will read, across all files.</summary>
        public int MaxCharacters { get; }

        /// <summary>Directories one pass will enter while collecting files.</summary>
        /// <remarks>
        /// A bound on the WALK rather than on the read, and the second of two:
        /// a linked directory is not followed, so a tree cannot loop through
        /// one, and this covers a tree that repeats itself some other way. The
        /// pass runs on a button in an editor, and a walk that does not end is
        /// a window that never comes back.
        /// </remarks>
        public int MaxDirectories { get; }

        public SceneScanBudget(int maxFiles, int maxCharacters, int maxDirectories = 20000)
        {
            MaxFiles       = maxFiles;
            MaxCharacters  = maxCharacters;
            MaxDirectories = maxDirectories;
        }

        /// <summary>The ceiling the window uses.</summary>
        /// <remarks>
        /// ⚠️ The file count is generous and the character count is the real
        /// bound. Four thousand files is ordinary for a project carrying two or
        /// three Asset Store packages, and a ceiling a normal project sits
        /// above makes the "saw less of the project" banner permanent — a green
        /// state nobody can reach teaches the same lesson a false red does.
        /// </remarks>
        public static SceneScanBudget Default => new SceneScanBudget(20000, 40_000_000);
    }

    /// <summary>
    /// The reading half of scene validation: build list in, verdicts out.
    /// </summary>
    public static class NetworkScenesInventory
    {
        /// <summary>The extension Unity's scene assets carry.</summary>
        public const string SceneExtension = ".unity";

        /// <summary>
        /// A scene's name: no directory, no extension.
        /// </summary>
        public static string NameOf(string assetPath)
        {
            string path = NormaliseEntry(assetPath);
            if (path.Length == 0) return string.Empty;

            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        /// <summary>
        /// A build-list path reduced to the form the match is made on: forward
        /// slashes, no surrounding space, no leading or trailing separator, no
        /// <c>.unity</c> extension.
        /// </summary>
        /// <remarks>
        /// 🔑 For the ENTRY only. Unity wrote this path, so canonicalising it
        /// is reading it; canonicalising the other side would be this check
        /// deciding what Unity tolerates, which is the one thing it is not in a
        /// position to know — see <see cref="NormaliseQuery"/>.
        /// </remarks>
        public static string NormaliseEntry(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string path = value.Replace('\\', '/').Trim();
            while (path.StartsWith("/", StringComparison.Ordinal)) path = path.Substring(1);
            while (path.EndsWith("/", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 1);

            return WithoutExtension(path);
        }

        /// <summary>
        /// A scene name as the code wrote it, with only its <c>.unity</c>
        /// extension taken off.
        /// </summary>
        /// <remarks>
        /// 🚨 Deliberately almost nothing. An earlier version trimmed the
        /// query, stripped its separators and converted its backslashes — so
        /// <c>" Arena "</c>, <c>"Arena/"</c> and <c>"/Arena"</c> all reported
        /// Loadable. A stray space in a typed name IS the class of typo this
        /// window exists to find, and cleaning it up before the comparison is
        /// the check answering a question the running game will not be asked.
        /// The extension survives because Unity documents the path form with
        /// it.
        /// </remarks>
        public static string NormaliseQuery(string value)
            => string.IsNullOrEmpty(value) ? string.Empty : WithoutExtension(value);

        /// <summary>
        /// Both sides reduced far enough that only a real difference of NAME
        /// survives — used to tell "no such scene" from "a scene spelled a
        /// little differently".
        /// </summary>
        public static string Loosely(string value)
            => NormaliseEntry(value);

        private static string WithoutExtension(string path)
            => path.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(0, path.Length - SceneExtension.Length)
                : path;

        /// <summary>
        /// Whether <paramref name="entryPath"/> is the entry Unity would find
        /// for <paramref name="query"/>.
        /// </summary>
        /// <remarks>
        /// 🔑 Unity accepts three spellings for one scene — the bare name, a
        /// trailing part of the path, and the whole project-relative path with
        /// its extension — and a check that only understood the first would put
        /// a red line under the very spelling it tells people to use to
        /// disambiguate. The match is therefore a suffix, and it is a suffix at
        /// a DIRECTORY boundary: <c>Arena</c> must not answer for
        /// <c>Scenes/BossArena</c>.
        /// </remarks>
        public static bool Matches(string entryPath, string query, StringComparison comparison)
            => Compare(NormaliseEntry(entryPath), NormaliseQuery(query), comparison);

        /// <summary>
        /// Whether <paramref name="entryPath"/> would be the entry for
        /// <paramref name="query"/> once case, surrounding space and separator
        /// direction are set aside.
        /// </summary>
        public static bool MatchesLoosely(string entryPath, string query)
            => Compare(Loosely(entryPath), Loosely(query), StringComparison.OrdinalIgnoreCase);

        private static bool Compare(string entry, string want, StringComparison comparison)
        {
            if (entry.Length == 0 || want.Length == 0) return false;

            if (entry.Length == want.Length) return string.Equals(entry, want, comparison);
            if (entry.Length < want.Length) return false;

            int at = entry.Length - want.Length;
            if (entry[at - 1] != '/') return false;

            return string.Compare(entry, at, want, 0, want.Length, comparison) == 0;
        }

        /// <summary>
        /// The Addressables entry point, whose scenes Build Settings does not
        /// list and whose names it therefore cannot answer for.
        /// </summary>
        public const string AddressablesReceiver = "Addressables";

        /// <summary>
        /// Whether the build list is the authority for a call made on
        /// <paramref name="receiver"/>.
        /// </summary>
        /// <remarks>
        /// 🚨 An Addressable scene is not in Build Settings and must not be —
        /// Unity's own documentation says to take it out — so judging one
        /// against the list produced a red line on every project using
        /// Addressables, telling its author to do the one thing the platform
        /// forbids. The name is not wrong; the list is the wrong list.
        /// <para>
        /// ⛔ One-sided, and that is what makes reading the receiver safe here
        /// when the scanner declines to read it anywhere else. A project is free
        /// to own a class called <c>Addressables</c>, so this can be wrong —
        /// and when it is, the cost is a row that says it was not checked,
        /// never a row that accuses a name that loads. The scanner's rule is
        /// that a receiver may not decide a call IS a scene load; this decides
        /// only that a verdict is withheld.
        /// </para>
        /// <para>
        /// ⚠️ It reaches one loader of two. A scene inside an AssetBundle is
        /// loaded through <c>SceneManager</c> like any other and is equally
        /// absent from the build list, and nothing at the call site tells the
        /// two apart — so that one is still judged as though it were in the
        /// build. Said in the window rather than left here.
        /// </para>
        /// </remarks>
        public static bool BuildListGoverns(string receiver)
        {
            if (string.IsNullOrEmpty(receiver)) return true;

            // The last segment, so a fully qualified call is the same call.
            int dot = receiver.LastIndexOf('.');
            string owner = (dot < 0 ? receiver : receiver.Substring(dot + 1)).Trim();

            return !string.Equals(owner, AddressablesReceiver, StringComparison.Ordinal);
        }

        /// <summary>
        /// The sentence for a call the build list does not answer for.
        /// </summary>
        internal const string ForeignLoaderNote =
            "This call is made on Addressables, which loads scenes from its own catalogue rather "
            + "than from Build Settings — so the build list says nothing about this name, and "
            + "neither does this check. An Addressable scene belongs OUT of Build Settings; if "
            + "the load fails, the address and the group are where to look.";

        /// <summary>
        /// What <paramref name="build"/> says about <paramref name="sceneName"/>.
        /// </summary>
        public static SceneReferenceVerdict Judge(SceneBuildList build, string sceneName,
                                                  out IReadOnlyList<string> candidates)
        {
            candidates = Array.Empty<string>();

            if (build == null) return SceneReferenceVerdict.NotInBuild;
            if (string.IsNullOrWhiteSpace(sceneName)) return SceneReferenceVerdict.Empty;

            var exact    = new List<BuildScene>();
            var ignoring = new List<BuildScene>();

            // 🔑 Case is forgiven here and nothing else is, because case is the
            // one difference Unity documents itself as forgiving: `LoadScene`
            // states that `sceneName` is case insensitive, except when the scene
            // is loaded from an AssetBundle — and a scene answered for by the
            // build list is not. Surrounding space and a reversed separator are
            // not documented anywhere, so they stay in the looser pass and are
            // reported rather than guessed at.
            foreach (var entry in build.Entries)
            {
                if (Matches(entry.Path, sceneName, StringComparison.OrdinalIgnoreCase)) exact.Add(entry);
                else if (MatchesLoosely(entry.Path, sceneName)) ignoring.Add(entry);
            }

            if (exact.Count == 0)
            {
                if (ignoring.Count == 0) return SceneReferenceVerdict.NotInBuild;
                candidates = PathsOf(ignoring);
                return SceneReferenceVerdict.SpellingDiffers;
            }

            // ⛔ The spelling outranks what follows, and it has to: a name that
            // matches nothing exactly cannot be judged against an entry it does
            // not name. What that costs is a second pass — correct the spelling,
            // press again, learn the entry was unticked all along — which is why
            // the sentence for it names the entry's own state as well.

            var ticked = new List<BuildScene>();
            foreach (var entry in exact) if (entry.Enabled) ticked.Add(entry);

            if (ticked.Count == 0)
            {
                candidates = PathsOf(exact);
                return SceneReferenceVerdict.DisabledInBuild;
            }

            if (ticked.Count > 1)
            {
                candidates = PathsOf(ticked);
                return SceneReferenceVerdict.AmbiguousName;
            }

            candidates = PathsOf(ticked);
            return ticked[0].AssetExists
                ? SceneReferenceVerdict.Loadable
                : SceneReferenceVerdict.MissingAsset;
        }

        // ⚠️ The room's cost is stated as a CONDITION, not as a fact. A call
        // site is reported with its receiver and never judged by it — a project
        // may call Unity's own SceneManager.LoadScene, where a name that does
        // not load is one client's failure and no room is involved — so a
        // sentence asserting the room's deadline would be false for exactly the
        // call sites this scanner declines to filter out.
        private const string RoomCost =
            "If the call goes through the SDK's scene manager, the name also reaches every client "
            + "in the room, each fails the same way, and the room waits out its readiness deadline.";

        /// <summary>
        /// The sentence for a verdict, or null when there is nothing to say.
        /// </summary>
        /// <remarks>
        /// Every line names a consequence, because a verdict a reader cannot act
        /// on is the state this column exists to replace.
        /// <para>
        /// ⛔ What each line may ASSERT is what Unity documents. Its resolution
        /// ORDER for a duplicated name is documented, so the ambiguity line
        /// states it; case insensitivity is documented, so a case-only
        /// difference never reaches the spelling line at all; what a stray space
        /// or a reversed separator does is documented nowhere, so that line
        /// refuses to guess.
        /// </para>
        /// </remarks>
        public static string Explain(SceneReferenceVerdict verdict, string sceneName,
                                     IReadOnlyList<string> candidates)
        {
            string name = string.IsNullOrEmpty(sceneName) ? "(empty)" : "'" + sceneName + "'";
            string list = Join(candidates);

            switch (verdict)
            {
                case SceneReferenceVerdict.Empty:
                    // ⛔ The two halves are not one statement. An EMPTY name is
                    // refused by the SDK's own scene manager before any room
                    // property is written, so nothing leaves this client; a name
                    // of only spaces passes that check, and only then does the
                    // room's cost apply.
                    return "The scene name is blank. The SDK's scene manager refuses an EMPTY name "
                         + "at the caller, so nothing leaves this client — but a name of only "
                         + "spaces passes that check and loads nothing on every client that gets "
                         + "it. " + RoomCost + " Pass the name of a scene that is in the build.";

                case SceneReferenceVerdict.NotInBuild:
                    return "No scene called " + name + " is in Build Settings, so the load fails: "
                         + "LoadSceneAsync answers null and LoadScene reports an error and loads "
                         + "nothing. " + RoomCost + " Add the scene under File → Build Settings → "
                         + "Scenes In Build.";

                case SceneReferenceVerdict.SpellingDiffers:
                    return "Build Settings holds " + list + ". The name here, " + name + ", differs "
                         + "from it only in surrounding space or in how its path separators are "
                         + "written — a leading one, a trailing one, or a backslash. Unity "
                         + "documents none of those as forgiven, and this check does not guess — "
                         + "make the two identical and the question does not arise. The entry's own "
                         + "state is judged on the next pass, once the names agree.";

                case SceneReferenceVerdict.DisabledInBuild:
                    return "Build Settings lists " + list + " but the entry is unticked, so it is "
                         + "not in the build and the load fails the same way a missing scene "
                         + "does. Tick it — an unticked scene reads as present in the list and "
                         + "behaves as though it were not there.";

                case SceneReferenceVerdict.AmbiguousName:
                    return "More than one scene in the build answers to " + name + ": " + list
                         + ". Unity takes the first, so one of them can never be loaded by name. "
                         + "Pass enough of the path to tell them apart, or rename one.";

                case SceneReferenceVerdict.MissingAsset:
                    return "Build Settings lists " + list + " but no asset is there any more. The "
                         + "entry has to be removed or repointed; until then the load fails.";

                default:
                    return null;
            }
        }

        /// <summary>
        /// Judge every reference against the build list.
        /// </summary>
        public static SceneInventory Build(SceneBuildList build,
                                           IReadOnlyList<SceneReference> references,
                                           int filesScanned, int filesUnreadable,
                                           int filesPartlyRead, bool truncated)
        {
            // ⛔ One default, in the constructor below, rather than one here
            // and one there. Two spellings of the same fallback mask each
            // other: either can be deleted on its own with every case green,
            // and the fault only appears when both are gone.
            var findings = new List<SceneFinding>();

            if (references != null)
            {
                foreach (var reference in references)
                {
                    if (reference == null) continue;

                    if (!reference.IsLiteral)
                    {
                        findings.Add(new SceneFinding(
                            reference, SceneReferenceVerdict.NotChecked, null, null));
                        continue;
                    }

                    // ⛔ Ahead of the verdict rather than after it. Judged first
                    // and overridden second, an Addressable name would still
                    // reach every counter and every summary as a fault before
                    // anything looked at what it was loaded by.
                    if (!BuildListGoverns(reference.Receiver))
                    {
                        findings.Add(new SceneFinding(
                            reference, SceneReferenceVerdict.NotChecked, null, ForeignLoaderNote));
                        continue;
                    }

                    var verdict = Judge(build, reference.SceneName, out var candidates);
                    findings.Add(new SceneFinding(
                        reference, verdict, candidates,
                        Explain(verdict, reference.SceneName, candidates)));
                }
            }

            return new SceneInventory(build, findings, filesScanned, filesUnreadable,
                                      filesPartlyRead, truncated);
        }

        /// <summary>
        /// The same call sites, judged again against a different build list.
        /// </summary>
        /// <remarks>
        /// 🔑 A verdict is a statement about two things, and only one of them
        /// is the source. Ticking a scene into the build changes every verdict
        /// in the window without changing a line of code — so a pass kept
        /// across that edit reports a fault the reader has just fixed, which is
        /// the fastest way to teach somebody that the window is wrong.
        /// <para>
        /// ⛔ Re-judged rather than discarded. The scan is a walk over every
        /// script in the project; throwing it away because the build list moved
        /// would make the window's own advice — tick this scene — cost a second
        /// full pass, and an answer that expensive is one people stop asking
        /// for.
        /// </para>
        /// </remarks>
        public static SceneInventory Rejudge(SceneInventory previous, SceneBuildList build)
        {
            if (previous == null) return null;

            var references = new List<SceneReference>(previous.Findings.Count);
            foreach (var finding in previous.Findings) references.Add(finding.Reference);

            return Build(build, references, previous.FilesScanned, previous.FilesUnreadable,
                         previous.FilesPartlyRead, previous.Truncated);
        }

        /// <summary>
        /// Read every file <paramref name="filePaths"/> names, within
        /// <paramref name="budget"/>, and collect the scene-load calls.
        /// </summary>
        /// <param name="filePaths">Source files to read, in the order to read them.</param>
        /// <param name="readText">Opens one file. Anything it throws counts the
        /// file as unreadable and the pass continues.</param>
        /// <param name="budget">The ceiling. Null means the default one.</param>
        /// <param name="sourcesIncomplete">The caller already knows its file
        /// list is short of the project — a directory it could not walk. Folded
        /// into <see cref="SceneInventory.Truncated"/>, which is the same
        /// statement: the pass did not see everything.</param>
        /// <remarks>
        /// ⛔ A file that will not open is COUNTED, never skipped silently. One
        /// unreadable file is one the check knows nothing about, and a pass that
        /// hid it would report a project it had not finished reading as clean.
        /// </remarks>
        public static SceneInventory Scan(SceneBuildList build,
                                          IEnumerable<string> filePaths,
                                          Func<string, string> readText,
                                          SceneScanBudget budget,
                                          bool sourcesIncomplete = false)
        {
            var limit      = budget ?? SceneScanBudget.Default;
            var references = new List<SceneReference>();

            int scanned    = 0;
            int unreadable = 0;
            int partial    = 0;
            long characters = 0;
            bool truncated = sourcesIncomplete;

            if (filePaths != null && readText != null)
            {
                foreach (string path in filePaths)
                {
                    if (scanned + unreadable >= limit.MaxFiles || characters >= limit.MaxCharacters)
                    {
                        truncated = true;
                        break;
                    }

                    string text;
                    try
                    {
                        text = readText(path);
                    }
                    catch (Exception)
                    {
                        unreadable++;
                        continue;
                    }

                    if (text == null) { unreadable++; continue; }

                    scanned++;
                    characters += text.Length;

                    // ⛔ Counted, and the calls it did yield are kept. A file
                    // whose text ends inside a token was read as far as that
                    // token, so what came back is true and incomplete at once —
                    // dropping it would hide findings, and counting the file as
                    // fully read would hide the ones below the token.
                    references.AddRange(SceneReferenceScanner.Scan(path, text, out bool fullyRead));
                    if (!fullyRead) partial++;
                }
            }

            return Build(build, references, scanned, unreadable, partial, truncated);
        }

        private static IReadOnlyList<string> PathsOf(List<BuildScene> entries)
        {
            var paths = new List<string>(entries.Count);
            foreach (var entry in entries) paths.Add(entry.Path);
            return paths;
        }

        private static string Join(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0) return "nothing";

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) text.Append(i == values.Count - 1 ? " and " : ", ");
                text.Append('\'').Append(values[i]).Append('\'');
            }
            return text.ToString();
        }
    }
}
