// RTMPE SDK — Editor/NetworkScenesWindow.cs
//
// Window > RTMPE > Network Scenes.
//
// The room's scene is a string that nothing checks. A host calls
// `LoadScene("Map")`, the property is written, every client is told, and every
// client's engine answers null because `Map` is not in Build Settings — at
// which point the only visible symptom is a room that never settles, which is
// also what a slow client looks like. This window moves that discovery back to
// the desk the name was typed on.
//
// ⛔ It reports; it changes nothing. Ticking a scene into the build, renaming
// one, or deciding which of two same-named scenes should keep the name are all
// choices with consequences outside this window's sight, and a validation that
// silently repaired them would be making them on the author's behalf. Every
// row names the fix instead.
//
// 🔑 Every rule is in NetworkScenesInventory and SceneReferenceScanner, which
// name no Unity type. What is here is the editor: the build list, the file
// walk, the buttons. That is the part no test can reach, and it is therefore
// the part that must be as small as it can be made.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Editor window comparing the scene names a project's code asks for
    /// against the scenes the project actually builds.
    /// </summary>
    public sealed class NetworkScenesWindow : EditorWindow
    {
        // The rows one section will draw before it stops. A project with a
        // hundred call sites is a project this window must still open in.
        private const int PreviewRows = 40;

        private SceneBuildList _build;
        private Vector2 _scroll;

        // 🔑 The result IS the record that a scan happened, rather than a flag
        // beside it. A separate flag is a second thing to clear, and the moment
        // the two disagree the window either offers a stale answer or hides a
        // fresh one — neither of which a reader can tell from the screen.
        private SceneInventory _inventory;

        // ── Entry point ─────────────────────────────────────────────────────────

        [MenuItem("Window/RTMPE/Network Scenes")]
        public static void Open()
        {
            var win = GetWindow<NetworkScenesWindow>(false, "RTMPE Scenes", true);
            win.minSize = new Vector2(520, 320);
            win.Show();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void OnEnable()
        {
            // Unity's own signal, rather than a poll on focus: the build list is
            // edited in another window, and a stale list here would judge every
            // scene name against scenes the project no longer has.
            EditorBuildSettings.sceneListChanged += OnSceneListChanged;
            ReadBuildList();
        }

        private void OnDisable()
        {
            EditorBuildSettings.sceneListChanged -= OnSceneListChanged;
        }

        // ⛔ The scan is kept and its verdicts re-taken. A verdict is a
        // statement about the source AND the build list, and this callback
        // moves the second half: an answer carried across it reports a fault
        // the reader has just fixed by ticking the scene — which is the
        // fastest way to teach somebody the window is wrong. Discarding it
        // instead would make the window's own advice cost a second walk over
        // every script in the project.
        private void OnSceneListChanged()
        {
            ReadBuildList();
            _inventory = NetworkScenesInventory.Rejudge(_inventory, _build);
            Repaint();
        }

        // Assets added, deleted, moved or reimported — which includes every
        // script edit that lands.
        // ⛔ The scan's answer describes the project as it was when it ran, and
        // this is the callback that says the project changed. Keeping it would
        // show a clean bill of health for source that has since been edited,
        // which is worse than showing nothing: the reader has no way to tell the
        // two apart.
        private void OnProjectChange()
        {
            ReadBuildList();
            _inventory = null;
            Repaint();
        }

        // ── Reading the project ─────────────────────────────────────────────────

        private void ReadBuildList()
        {
            var entries = new List<BuildScene>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene == null) continue;
                entries.Add(new BuildScene(scene.path, scene.enabled, AssetExists(scene.path)));
            }
            _build = new SceneBuildList(entries);
        }

        /// <summary>
        /// Whether an asset is at <paramref name="assetPath"/>.
        /// </summary>
        /// <remarks>
        /// The type is asked for rather than the asset loaded: a build list can
        /// name a hundred scenes, and loading one to find out whether it is
        /// there imports it. Unity answers null for a path holding nothing,
        /// which is the whole question.
        /// </remarks>
        private static bool AssetExists(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            return AssetDatabase.GetMainAssetTypeAtPath(assetPath) != null;
        }

        /// <summary>
        /// Every C# file under <c>Assets/</c>.
        /// </summary>
        /// <param name="complete">False when part of the tree could not be
        /// walked, so the list is short of the project.</param>
        /// <remarks>
        /// ⛔ <c>Assets/</c> only. A package's own sources are not the project's
        /// call sites — the SDK's <c>LoadScene</c> is the declaration this check
        /// is about, not a use of it — and walking <c>Library/</c> would read
        /// generated copies of what has already been read.
        /// <para>
        /// 🚨 Walked a directory at a time rather than with
        /// <c>SearchOption.AllDirectories</c>. That overload throws out of the
        /// ENUMERATOR on the first folder it cannot enter — a permission, a
        /// broken link, a directory an import deleted while the walk was in
        /// flight — and the throw would then arrive from inside <c>OnGUI</c>,
        /// where a scan the user asked for reads as a broken window. Here a
        /// folder that will not open costs the folder and is reported as
        /// incomplete coverage, which is what it is.
        /// </para>
        /// </remarks>
        private static List<string> ProjectScripts(SceneScanBudget budget, out string shortfall)
        {
            shortfall = null;
            var files = new List<string>();

            string assets = Application.dataPath;
            if (string.IsNullOrEmpty(assets) || !Directory.Exists(assets))
            {
                shortfall = "the project's Assets folder was not found";
                return files;
            }

            bool cut        = false;
            bool skipped    = false;
            bool unreadable = false;

            var limit   = budget ?? SceneScanBudget.Default;
            var pending = new Stack<string>();
            pending.Push(assets);
            int walked = 0;

            while (pending.Count > 0)
            {
                if (walked >= limit.MaxDirectories || files.Count >= limit.MaxFiles)
                {
                    cut = true;
                    break;
                }

                string directory = pending.Pop();
                walked++;

                try
                {
                    // ⛔ Files as well as directories. A linked FILE is the same
                    // duplication one level down — measured: an alias beside its
                    // target collects two scripts where the project has one, so
                    // every call in it is reported twice.
                    foreach (string file in Directory.GetFiles(directory, "*.cs"))
                    {
                        // ⛔ The same rule as the folders below, because Unity
                        // states it of both: a `.cs` whose name begins with a
                        // dot is not imported, so a call site in it is a red
                        // about code the built game does not contain.
                        if (SkippedByUnity(Path.GetFileName(file))) continue;
                        if (IsLink(file)) { skipped = true; continue; }
                        files.Add(file);
                    }

                    foreach (string child in Directory.GetDirectories(directory))
                    {
                        if (SkippedByUnity(Path.GetFileName(child))) continue;
                        if (IsLink(child)) { skipped = true; continue; }
                        pending.Push(child);
                    }
                }
                catch (Exception)
                {
                    unreadable = true;
                }
            }

            // 🔑 Named, not merely counted. One bool sends a reader to a
            // banner naming two causes and neither one of theirs; what they do
            // next differs entirely — raise a ceiling, unlink a folder, or fix a
            // permission.
            if (cut)        shortfall = "this pass reached its own ceiling";
            else if (skipped)    shortfall = "a linked file or folder under Assets was not followed";
            else if (unreadable) shortfall = "part of the tree could not be read";

            return files;
        }

        // Names the AssetDatabase never imports, so a script called one of them —
        // or inside a folder called one of them — is not in the project Unity
        // compiles.
        //
        // ⛔ Reporting a call site there is a red about code the built game does
        // not contain — a parked `OldGameplay~` beside a live folder is the
        // ordinary shape, and this package uses the convention four times.
        //
        // 🔑 Unity's Special Folders page states the rule of FILES and folders
        // alike, and this was applied to folders only: a `.Scratch.cs` beside a
        // live script was read and reported.
        //
        // ⚠️ Of the four clauses, one can fire on a file: the walk asks for
        // `*.cs`, so a name can never end in `~`, can never be `cvs`, and the
        // `.tmp` extension the same page names cannot reach here at all. The
        // whole rule is applied anyway rather than the leading dot alone,
        // because the question is the same question and a glob is a poor place
        // to keep the answer.
        private static bool SkippedByUnity(string name)
            => string.IsNullOrEmpty(name)
               || name[0] == '.'
               || name[name.Length - 1] == '~'
               || string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase);

        // 🚨 A link is NOT followed, and the pass reports that it saw less of
        // the project. Measured on this filesystem: `Assets/loop -> Assets` is
        // entered 82 times across 41 levels — the bound is the kernel's limit on
        // symlink hops, not the path length, which is why lengthening the base
        // path by 128 characters does not move either number — and it collects
        // the SAME script 41 times, so every call in it is reported 41 times and
        // the coverage line claims 41 scripts where the project has one.
        // Deduplicating instead needs the link's target, and resolving one is
        // .NET 6 while an Editor script compiles against netstandard 2.1.
        //
        // ⚠️ What this costs is a project that links a shared source folder in,
        // which Unity does compile: those scripts go unread. That is why the
        // shortfall is NAMED rather than counted — an unread folder the reader
        // is told about is the safe arm of a choice with no clean side, and
        // silence about it would be the one direction this check must never be
        // wrong in.
        //
        // ⛔ Answering false for something that cannot be asked is the safe arm
        // there too: it is then read, and a tree that loops is still bounded by
        // the two ceilings above.
        private static bool IsLink(string directory)
        {
            try
            {
                return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The path as the Project window shows it, so a row can be matched to a
        // file by eye and handed back to the asset database by click.
        private static string ProjectRelative(string absolutePath)
        {
            string assets = Application.dataPath;
            if (string.IsNullOrEmpty(assets) || string.IsNullOrEmpty(absolutePath)) return absolutePath;

            string normalised = absolutePath.Replace('\\', '/');
            string root       = assets.Replace('\\', '/');
            if (!normalised.StartsWith(root, StringComparison.Ordinal)) return normalised;

            return "Assets" + normalised.Substring(root.Length);
        }

        private void Rescan()
        {
            // ⛔ In a `finally`, not after the work. A progress bar left up by a
            // throw is modal: it blocks every other window, has no cancel of its
            // own, and the only way out is restarting the Editor. That is a
            // worse outcome than whatever threw.
            try
            {
                ReadBuildList();

                EditorUtility.DisplayProgressBar(ProgressTitle, "Listing scripts…", 0f);
                var budget  = SceneScanBudget.Default;
                var scripts = ProjectScripts(budget, out _shortfall);

                EditorUtility.DisplayProgressBar(
                    ProgressTitle, "Reading " + Plural(scripts.Count, "script", "scripts") + "…", 0.5f);
                _inventory = NetworkScenesInventory.Scan(
                    _build, scripts, ReadScript, budget, _shortfall != null);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // 🔑 A scan reads every script in the project on the GUI thread, which
        // on a large one is several seconds of a window that does not repaint.
        // Without this the first thing a user does looks like a hang, and the
        // conclusion they draw is about the tool rather than about the wait.
        private const string ProgressTitle = "RTMPE — reading scene names";

        // Why the last walk saw less than the project holds, or null when it saw
        // all of it. Kept beside the inventory because the inventory's own
        // `Truncated` folds this together with its reading budget.
        private string _shortfall;

        // Reads one file for the scan. Anything it throws is the inventory's to
        // count — a file the check could not open is a file it knows nothing
        // about, and that is a fact about coverage rather than an error to
        // interrupt the pass with.
        private static string ReadScript(string path)
        {
            return File.ReadAllText(path);
        }

        // ── Drawing ─────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_build == null) ReadBuildList();

            DrawToolbar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawBuildList();
            EditorGUILayout.Space();
            DrawReferences();
            EditorGUILayout.Space();
            DrawLimit();

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                Rescan();
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(Plural(_build.EnabledCount, "scene", "scenes") + " in build",
                            EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBuildList()
        {
            EditorGUILayout.LabelField("Build Settings", EditorStyles.boldLabel);

            if (_build.Entries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Build Settings holds no scenes. A room can still be told to change scene, "
                        + "and every client will fail to load whatever it is told — including the "
                        + "scene the project starts in.",
                    MessageType.Warning);
                return;
            }

            if (_build.MissingAssetCount > 0)
            {
                EditorGUILayout.HelpBox(
                    Plural(_build.MissingAssetCount, "ticked entry names", "ticked entries name")
                        + " a scene asset that is no longer there. Loading one answers null, and "
                        + "the room waits out its readiness deadline for every client that was "
                        + "told to.",
                    MessageType.Error);
            }

            if (_build.AmbiguousNames.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "More than one scene in the build answers to " + Join(_build.AmbiguousNames)
                        + ". Unity takes the first match for a bare name, so the others can never "
                        + "be loaded by name — pass enough of the path to tell them apart, or "
                        + "rename one.",
                    MessageType.Warning);
            }

            int drawn = 0;
            foreach (var entry in _build.Entries)
            {
                if (drawn >= PreviewRows) break;
                drawn++;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(entry.Enabled ? "✓" : "—", EditorStyles.miniLabel, GUILayout.Width(16));
                GUILayout.Label(entry.Path, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (!entry.AssetExists) GUILayout.Label("missing", EditorStyles.miniBoldLabel);
                else if (!entry.Enabled) GUILayout.Label("not in build", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            // ⛔ Named, not just counted. The findings list below draws its own
            // overflow line, and two identical "… and 20 more" labels are one
            // sentence as far as a reader — or a rule — can tell: either could
            // vanish and the other would answer for it.
            if (_build.Entries.Count > drawn)
            {
                EditorGUILayout.LabelField(
                    "… and " + (_build.Entries.Count - drawn) + " more in the build list",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawReferences()
        {
            EditorGUILayout.LabelField("Scene names your scripts ask for", EditorStyles.boldLabel);

            if (GUILayout.Button("Scan this project's scripts for scene names"))
            {
                Rescan();
                GUIUtility.ExitGUI();
            }

            if (_inventory == null) return;

            DrawCoverage();

            if (_inventory.Findings.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No scene-load call was found in this project's own scripts. If your scenes are "
                        + "changed through the room's __scene property directly, or from a package, "
                        + "this check has nothing to read.",
                    MessageType.Info);
                return;
            }

            // 🔑 Two shades of "nothing wrong here", and the difference is the
            // whole reason the window states its own coverage. A pass with no
            // findings is not the same as a project with nothing to act on: the
            // build list may be broken above, or the pass may have read less of
            // the project than the project holds. One message for both would be
            // the reassuring one, and it would be the false one exactly when it
            // mattered.
            if (_inventory.IsClean)
            {
                EditorGUILayout.HelpBox(
                    "Nothing to act on. Every scene name this check could read names a scene in "
                        + "the build, and the build list itself is sound.",
                    MessageType.Info);
            }
            else if (_inventory.ProblemCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "Every scene name this check could read names a scene in the build. There is "
                        + "still something above to look at.",
                    MessageType.Info);
            }

            int drawn = 0;
            foreach (var finding in _inventory.Findings)
            {
                if (!finding.IsProblem) continue;
                if (drawn >= PreviewRows) break;
                drawn++;
                DrawFinding(finding);
            }

            if (_inventory.ProblemCount > drawn)
            {
                EditorGUILayout.LabelField(
                    "… and " + (_inventory.ProblemCount - drawn) + " more to act on",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawFinding(SceneFinding finding)
        {
            var reference = finding.Reference;
            string where  = ProjectRelative(reference.FilePath) + ":" + reference.Line;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(where, EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(52)))
            {
                OpenAt(reference.FilePath, reference.Line);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            string call = (reference.Receiver.Length > 0 ? reference.Receiver + "." : string.Empty)
                        + reference.Method + "(" + reference.Argument + ")";
            GUILayout.Label(call, EditorStyles.miniLabel);

            EditorGUILayout.HelpBox(finding.Explanation, Severity(finding.Verdict));
            EditorGUILayout.EndVertical();
        }

        // ⚠️ Warning, not error, for the two verdicts that describe a project
        // that may be working today. A row a reader knows to be harmless
        // teaches them to skip the section it is in, and the errors are in the
        // same section.
        private static MessageType Severity(SceneReferenceVerdict verdict)
        {
            if (verdict == SceneReferenceVerdict.SpellingDiffers
                || verdict == SceneReferenceVerdict.AmbiguousName)
            {
                return MessageType.Warning;
            }
            return MessageType.Error;
        }

        private static void OpenAt(string filePath, int line)
        {
            string relative = ProjectRelative(filePath);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relative);
            if (asset == null) return;
            AssetDatabase.OpenAsset(asset, line);
        }

        // 🔑 The coverage line is the check's own account of itself, and it is
        // drawn whether or not anything was found. Without it a project whose
        // scene names all come from variables reads as a project with nothing
        // wrong, which is a different statement entirely.
        private void DrawCoverage()
        {
            // 🔑 The count of files opened is not a count of files read, and
            // saying "read" of a file the scan stopped inside is the sentence
            // this whole signal exists to stop being true. The qualifier is on
            // the primary number rather than only in the warning below, because
            // the number is what a reader takes away.
            EditorGUILayout.LabelField(
                Plural(_inventory.FilesScanned, "script", "scripts") + " read"
                    + (_inventory.FilesPartlyRead > 0
                        ? " (" + _inventory.FilesPartlyRead + " not to the end)"
                        : string.Empty) + " · "
                    + Plural(_inventory.CheckedCount, "scene name", "scene names") + " checked · "
                    + _inventory.NotCheckedCount + " not checked",
                EditorStyles.miniLabel);

            if (_inventory.Truncated)
            {
                EditorGUILayout.HelpBox(
                    "This pass saw less of the project than the project holds"
                        + (_shortfall == null ? "" : " — " + _shortfall)
                        + ". What it did not read, it cannot report on.",
                    MessageType.Warning);
            }

            if (_inventory.FilesUnreadable > 0)
            {
                EditorGUILayout.HelpBox(
                    Plural(_inventory.FilesUnreadable, "script", "scripts") + " could not be "
                        + "opened and " + (_inventory.FilesUnreadable == 1 ? "was" : "were")
                        + " not read. Whatever scene names they carry are outside this answer.",
                    MessageType.Warning);
            }

            // 🔑 Named apart from the file that would not open, because the two
            // are acted on differently: this one opened, and its text ends
            // inside a comment or a string that was never closed. Everything
            // below that point went unread, so the silence about it is about
            // this window rather than about the project.
            if (_inventory.FilesPartlyRead > 0)
            {
                EditorGUILayout.HelpBox(
                    Plural(_inventory.FilesPartlyRead, "script", "scripts") + " could not be read "
                        + "to the end: the text ends inside something that was opened and never "
                        + "closed — a block comment, a verbatim string, a raw string or an "
                        + "interpolation. Any scene name below that point was not looked for.",
                    MessageType.Warning);
            }
        }

        // ⛔ Stated where the answer is read, not in a document beside it. This
        // check reads string literals out of source text: a name assembled at
        // runtime, held in a constant, or chosen by a designer in the inspector
        // is invisible to it, and so is any call made from a package. Saying so
        // is the difference between a check and a claim.
        private void DrawLimit()
        {
            EditorGUILayout.LabelField("What this check does not see", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scene names are read as written: a call passing a literal is checked, and a call "
                    + "passing a variable, a field, a constant or an interpolated string is counted "
                    + "as unchecked rather than passed over. Only this project's own scripts under "
                    + "Assets are read, and both sides of a #if are read — a call in a branch this "
                    + "build does not compile is still reported. A call made on Addressables is "
                    + "counted as unchecked, because its scenes come from a catalogue and not from "
                    + "this list; a scene loaded out of an AssetBundle is not distinguishable at "
                    + "the call site and is judged as though it were in the build. A green result "
                    + "here means the names this check could read are in the build — not that "
                    + "every scene the game loads is.",
                MessageType.Info);
        }

        // A count and its noun, agreeing. A window that says "1 scenes in
        // build" is a window somebody stops reading carefully, and every
        // number here is one somebody has to act on.
        private static string Plural(int count, string singular, string plural)
            => count + " " + (count == 1 ? singular : plural);

        private static string Join(IReadOnlyList<string> values)
        {
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
#endif
