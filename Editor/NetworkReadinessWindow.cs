// RTMPE SDK — Editor/NetworkReadinessWindow.cs
//
// Editor-only viewer for the Network Readiness artifact.  Open via:
// Window > RTMPE > Network Readiness.
//
// Design constraints:
//  • READ-ONLY ARTIFACT VIEWER (completion-plan W1, DD-5 Option A).  The
//    window renders the precomputed network-readiness.json that the headless
//    pipeline emits (`make readiness`, or CI's analyzer shard); it hosts no
//    scorer and never touches Roslyn, so opening it cannot trigger a compiler
//    load and this assembly keeps its Runtime-only reference set.
//  • The artifact on disk is the single source of truth.  Staleness is a
//    first-class UI state: the file's write time is always visible, and an
//    old artifact is flagged rather than silently rendered as current.
//  • Parsing is Unity's built-in JsonUtility against [Serializable] DTOs —
//    the artifact schema is a flat object of arrays (no dictionaries), which
//    is exactly the subset JsonUtility supports.  No JSON library dependency.
//
// Layout:
//  Header    : project score, artifact age, refresh/locate controls.
//  Types     : per-type score with a foldout of the six dimension verdicts;
//              uncleared dimensions are the type's remaining blockers.
//  Authority : the Phase-5 advisory classification (RTMPE9001) per component
//              type, with its evidence, recommendations and dependencies.
//  To-do     : the artifact's aggregated blocker list — each line names the
//              exact uncleared dimension, i.e. the remaining conversion work.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using RTMPE.Core;
using UnityEditor;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Editor window rendering the readiness artifact the headless scorer
    /// emitted: project score, per-type dimension verdicts, the advisory
    /// authority classification, and the outstanding to-do list.
    /// </summary>
    public sealed class NetworkReadinessWindow : EditorWindow
    {
        // Artifact DTOs + loader are shared with the Conversion Wizard — see
        // ReadinessArtifactData.cs (one schema definition for every viewer).

        // ── Window state ────────────────────────────────────────────────────────

        private const string PrefPrefix = "RTMPE.Readiness.";

        // Reload silently when the on-disk artifact is replaced; flag as stale
        // once it is older than a day — readiness is re-scored per change set,
        // so a day-old artifact almost certainly predates the current code.
        private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);

        private string _artifactPath;
        private ReadinessArtifactData _artifact;
        private string _loadError;
        private DateTime _artifactWriteTimeUtc;
        private Vector2 _scroll;
        private bool _foldTypes;
        private bool _foldAuthority;
        private bool _foldQuestions;
        private bool _foldRuntime;
        private bool _foldTodo;
        private bool _sortWorstFirst;
        private readonly Dictionary<string, bool> _typeFold = new Dictionary<string, bool>();

        // Answers recorded since this artifact was loaded. The score they belong
        // to is produced by the next SCAN, not by this window — so an answer
        // clicked here is shown as recorded and marked as not yet scored, rather
        // than silently rendered as though the number below it had moved.
        private readonly Dictionary<string, string> _recordedThisSession =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _answerNotes =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private string _answerStatus;
        private string _answerError;

        // Outcomes recorded since this artifact was loaded, and the drafts beside
        // them. Same reasoning as the answers above: the artifact does not move
        // until the next SCAN, so a click here is shown as recorded and marked as
        // not yet in the report rather than rendered as though it were.
        private readonly Dictionary<string, string> _runtimeRecordedThisSession =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _runtimeDetails =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private string _runtimeStatus;
        private string _runtimeError;

        // ── Entry point ─────────────────────────────────────────────────────────

        [MenuItem("Window/RTMPE/Network Readiness")]
        public static void Open()
        {
            var win = GetWindow<NetworkReadinessWindow>(false, "RTMPE Readiness", true);
            win.minSize = new Vector2(460, 380);
            win.Show();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _foldTypes = SessionState.GetBool(PrefPrefix + "Types", true);
            _foldAuthority = SessionState.GetBool(PrefPrefix + "Authority", true);
            _foldQuestions = SessionState.GetBool(PrefPrefix + "Questions", true);
            _foldRuntime = SessionState.GetBool(PrefPrefix + "Runtime", true);
            _foldTodo = SessionState.GetBool(PrefPrefix + "Todo", true);
            _sortWorstFirst = SessionState.GetBool(PrefPrefix + "WorstFirst", false);
            // Never trust a stored empty string: every downstream path helper
            // (GetDirectoryName in Locate…, the exists-check in OnFocus) assumes
            // a usable absolute path.
            string stored = SessionState.GetString(PrefPrefix + "Path", string.Empty);
            _artifactPath = string.IsNullOrEmpty(stored) ? DefaultArtifactPath() : stored;
            Load();
        }

        private void OnDisable()
        {
            SessionState.SetBool(PrefPrefix + "Types", _foldTypes);
            SessionState.SetBool(PrefPrefix + "Authority", _foldAuthority);
            SessionState.SetBool(PrefPrefix + "Questions", _foldQuestions);
            SessionState.SetBool(PrefPrefix + "Runtime", _foldRuntime);
            SessionState.SetBool(PrefPrefix + "Todo", _foldTodo);
            SessionState.SetBool(PrefPrefix + "WorstFirst", _sortWorstFirst);
            SessionState.SetString(PrefPrefix + "Path", _artifactPath ?? string.Empty);
        }

        // Re-stat the file when the window regains focus so an artifact
        // regenerated in a terminal shows up without a manual refresh.
        private void OnFocus()
        {
            if (_artifactPath != null && File.Exists(_artifactPath)
                && File.GetLastWriteTimeUtc(_artifactPath) != _artifactWriteTimeUtc)
            {
                Load();
            }
        }

        private static string DefaultArtifactPath() => ReadinessArtifactData.DefaultPath();

        private void Load()
        {
            _typeFold.Clear();

            // A fresh artifact is a fresh answer to "what has been scored". The
            // session's own records and their drafts are cleared with it, or an
            // answer that was recorded, scored, and then WITHDRAWN in the file by
            // hand would keep showing as chosen against a scan that never saw it.
            _recordedThisSession.Clear();
            _answerNotes.Clear();
            _answerStatus = null;
            _answerError = null;
            _runtimeRecordedThisSession.Clear();
            _runtimeDetails.Clear();
            _runtimeStatus = null;
            _runtimeError = null;

            if (!string.IsNullOrEmpty(_artifactPath) && File.Exists(_artifactPath))
            {
                _artifactWriteTimeUtc = File.GetLastWriteTimeUtc(_artifactPath);
            }

            _artifact = ReadinessArtifactData.Load(_artifactPath, out _loadError);
        }

        // ── GUI ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(4);

            if (_loadError != null)
            {
                EditorGUILayout.HelpBox("Could not read the readiness artifact:\n" + _loadError, MessageType.Error);
                return;
            }

            if (_artifact == null)
            {
                EditorGUILayout.HelpBox(
                    "No readiness artifact found at:\n" + _artifactPath
                    + "\n\nIt is written by the headless scorer, which travels inside this package"
                    + " under " + ConversionCliLocator.PackageAutomationFolder
                    + " and needs the .NET 8 SDK on PATH."
                    + " Run this from that folder's clients/unity-sdk (or from the root of an"
                    + " RTMPE repository clone); it scores THIS project, not the SDK:\n\n    "
                    + ReadinessArtifactData.RegenerateCommand()
                    + "\n\nThen press Refresh — or press Documentation above, which opens"
                    + " Documentation~/automation.md online. The same page ships inside this"
                    + " package under Documentation~/ if you are working offline.\n\n"
                    + "CI's `network-readiness` artifact is the SDK samples' score, not this"
                    + " project's: it will render, and it will describe types you do not have.",
                    MessageType.Info);
                return;
            }

            DrawHeader();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawRuntime();
            DrawTypes();
            DrawAuthority();
            DrawQuestions();
            DrawTodo();
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                Load();
            }

            if (GUILayout.Button("Locate…", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                string picked = EditorUtility.OpenFilePanel(
                    "Select network-readiness.json",
                    Path.GetDirectoryName(_artifactPath), "json");
                if (!string.IsNullOrEmpty(picked))
                {
                    _artifactPath = picked;
                    Load();
                }
            }

            _sortWorstFirst = GUILayout.Toggle(
                _sortWorstFirst, "Worst first", EditorStyles.toolbarButton, GUILayout.Width(78));

            // In the toolbar rather than in the empty-state box: the page answers
            // "what is this score" as often as "why is there no file", and the
            // second question is the only one the box below is drawn for.
            if (GUILayout.Button("Documentation", EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                Application.OpenURL(DocumentationLinks.Resolve(DocumentationLinks.AutomationPage));
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(Path.GetFileName(_artifactPath), EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(_artifact.ScoreHeadline(), EditorStyles.boldLabel);
            EditorGUILayout.Space(12);
            // 🔑 Beside the score rather than below it. The whole point of the
            // second result is that a reader who sees only the first takes it for
            // a measure of how finished the game is; a headline they have to
            // scroll to is a headline that arrives after that reading is made.
            GUILayout.Label(_artifact.RuntimeHeadline(), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                "generated " + _artifactWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (DateTime.UtcNow - _artifactWriteTimeUtc > StaleAge)
            {
                // The same command as the empty state, from the same place: this
                // banner offered the bare verb, which is the spelling that scores
                // the SDK's own samples — and a reader hitting THIS message
                // already has a working artifact, so the wrong one would quietly
                // replace a correct score with an unrelated one.
                EditorGUILayout.HelpBox(
                    "This artifact is more than 24 hours old and may not reflect the current"
                    + " code. Re-run\n    " + ReadinessArtifactData.RegenerateCommand()
                    + "\nand press Refresh.",
                    MessageType.Warning);
            }
        }

        private void DrawTypes()
        {
            _foldTypes = EditorGUILayout.Foldout(
                _foldTypes, "Scored types (" + _artifact.types.Count + ")", true);
            if (!_foldTypes)
            {
                return;
            }

            // A copy, so the "worst first" ordering never mutates artifact order —
            // the artifact list is the serializer's canonical order.
            var rows = new List<ReadinessArtifactData.TypeEntry>(_artifact.types);
            if (_sortWorstFirst)
            {
                rows.Sort((a, b) => a.score != b.score
                    ? a.score.CompareTo(b.score)
                    : string.CompareOrdinal(a.name, b.name));
            }

            foreach (var type in rows)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // 🔑 Dictionary refuses a null key by throwing, and this runs
                // inside OnGUI — which repaints — so a nameless entry would be an
                // exception every frame rather than the visible degradation the
                // loader promises for every other malformed shape. The emitter
                // always writes a name; the artifact on disk is not always the
                // emitter's, which is the whole reason Load validates at all.
                string foldKey = type.name ?? string.Empty;

                bool open;
                _typeFold.TryGetValue(foldKey, out open);
                EditorGUILayout.BeginHorizontal();
                // Tooltip carries the namespace-qualified name the row elides.
                open = EditorGUILayout.Foldout(open, new GUIContent(ReadinessArtifactData.ShortName(type.name), type.name), true);
                GUILayout.FlexibleSpace();
                GUILayout.Label(type.score + "%", type.score >= 100
                    ? EditorStyles.boldLabel
                    : EditorStyles.label);
                EditorGUILayout.EndHorizontal();
                _typeFold[foldKey] = open;

                if (open && type.dimensions != null)
                {
                    EditorGUI.indentLevel++;
                    foreach (var dim in type.dimensions)
                    {
                        EditorGUILayout.LabelField(
                            (dim.cleared ? "✓ " : "✗ ") + dim.dimension + " (" + dim.weight + ")",
                            dim.detail);
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawAuthority()
        {
            _foldAuthority = EditorGUILayout.Foldout(
                _foldAuthority, "Authority (advisory, " + _artifact.authority.Count + ")", true);
            if (!_foldAuthority)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "Advisory classification (RTMPE9001) — it recommends placement and guards; " +
                "it changes no ownership and enforces nothing.",
                MessageType.None);

            foreach (var entry in _artifact.authority)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(ReadinessArtifactData.ShortName(entry.name), entry.name), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label(entry.role, EditorStyles.miniBoldLabel);
                EditorGUILayout.EndHorizontal();

                if (entry.evidence != null && entry.evidence.Count > 0)
                {
                    EditorGUILayout.LabelField(string.Join("; ", entry.evidence), EditorStyles.wordWrappedMiniLabel);
                }

                if (entry.recommendations != null)
                {
                    foreach (var recommendation in entry.recommendations)
                    {
                        EditorGUILayout.LabelField("→ " + recommendation, EditorStyles.wordWrappedLabel);
                    }
                }

                if (entry.dependsOnAuthority != null)
                {
                    foreach (var dependency in entry.dependsOnAuthority)
                    {
                        EditorGUILayout.LabelField("↳ depends on authority: " + dependency,
                            EditorStyles.wordWrappedMiniLabel);
                    }
                }

                EditorGUILayout.EndVertical();
            }
        }

        private static readonly List<ReadinessArtifactData.RuntimeEntry> EmptyRuntime =
            new List<ReadinessArtifactData.RuntimeEntry>();

        // Where an outcome is written: beside the artifact, under the name the
        // artifact itself carries — the same rule, and the same refusal, as
        // AnswersPath below. The LEAF only: Path.Combine discards its first
        // argument when the second is rooted, and this window invites loading an
        // artifact it did not produce.
        private string RuntimePath()
        {
            if (_artifact == null || string.IsNullOrEmpty(_artifact.runtimeFile)) return null;
            if (string.IsNullOrEmpty(_artifactPath)) return null;

            string name = Path.GetFileName(_artifact.runtimeFile);
            if (string.IsNullOrEmpty(name)) return null;

            string directory = Path.GetDirectoryName(_artifactPath);
            return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
        }

        // The outcome standing for a check: what this session recorded, else what
        // the artifact was written with.
        private string CurrentResult(ReadinessArtifactData.RuntimeEntry entry, string key)
        {
            if (_runtimeRecordedThisSession.TryGetValue(key, out string recorded))
            {
                return recorded;
            }

            return entry.result ?? string.Empty;
        }

        private void RecordRuntime(string check, string result)
        {
            string path = RuntimePath();
            _runtimeStatus = null;
            _runtimeError = null;

            if (path == null)
            {
                _runtimeError = "This artifact predates the runtime checks and names no record file; "
                    + "regenerate it with " + ReadinessArtifactData.RegenerateCommand();
                return;
            }

            if (TheTwoRecordsAreOneFile())
            {
                _runtimeError = RecordsCollided + ReadinessArtifactData.RegenerateCommand();
                return;
            }

            string detail = _runtimeDetails.TryGetValue(check, out string draft) ? draft : string.Empty;
            // 🔑 The version is the package's own constant, not a field the
            // developer fills. The reader refuses an outcome that names none, and
            // the field this replaces could be blank, wrong, or stale from another
            // install — three ways to put a record on disk that the next scan
            // rejects WHOLE, taking the harness's outcomes with it. A build states
            // what it is; nobody has to be asked.
            if (!RuntimeChecksFile.Record(
                    path, check, result, RuntimeChecksFile.Developer, RtmpeSdk.Version, detail,
                    out string error))
            {
                _runtimeError = "Could not record the outcome: " + error;
                return;
            }

            _runtimeRecordedThisSession[check] = result ?? string.Empty;

            // 🔑 Same discipline as the answers: the report does not move until
            // the scan runs again, and saying so is the difference between a tool
            // that records and one that only appears to.
            _runtimeStatus = (string.IsNullOrEmpty(result) ? "Cleared " : "Recorded ")
                + check + " in " + path
                + " — it reaches the report on the next scan: "
                + ReadinessArtifactData.RegenerateCommand();
        }

        private void DrawRuntime()
        {
            // ⛔ Null-tolerant like every other section: the loader normalises an
            // artifact written before this section existed, and an OnGUI that
            // throws is a window nobody can use for as long as that file is
            // selected — not a visible degradation.
            var checks = _artifact.runtime ?? EmptyRuntime;

            _foldRuntime = EditorGUILayout.Foldout(
                _foldRuntime, _artifact.RuntimeHeadline(), true);
            if (!_foldRuntime)
            {
                return;
            }

            EditorGUI.indentLevel++;
            if (checks.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "This artifact carries no runtime section. It was written by a package older than"
                    + " the runtime checks; regenerate it with\n    "
                    + ReadinessArtifactData.RegenerateCommand(),
                    MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            EditorGUILayout.LabelField(
                "The score above is decided from your source and says nothing about a run. These are"
                + " decided by a run and say nothing about your source. Neither moves the other.",
                EditorStyles.wordWrappedMiniLabel);

            // ⛔ Read-only, and that is the change. This used to be a text field
            // the developer filled in: the version an outcome is stamped with was
            // whatever they typed, whatever a previous session left behind, or
            // blank — and a blank one was refused at the moment of recording,
            // which is a tool asking a person for a fact it already holds. The
            // package states its own version, so the window shows it rather than
            // asking. Restoring a writable control here restores all three.
            EditorGUILayout.LabelField(
                "Outcomes are stamped with the SDK version this window is part of.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("SDK version", RtmpeSdk.Version);

            foreach (var entry in checks)
            {
                if (entry == null)
                {
                    continue;
                }

                // 🚨 Never a raw null into a dictionary — Dictionary throws on one,
                // and this runs inside OnGUI, which repaints: a nameless row would
                // be an exception every frame rather than one bad row.
                string key = entry.check ?? string.Empty;
                string result = CurrentResult(entry, key);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(entry.title) ? key : entry.title,
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(result) ? "not-tested" : result,
                    EditorStyles.wordWrappedLabel);

                if (!string.IsNullOrEmpty(entry.establishes))
                {
                    EditorGUILayout.LabelField(entry.establishes, EditorStyles.wordWrappedMiniLabel);
                }

                // The evidence, shown rather than summarised: an outcome with no
                // observer beside it is the row a reader has no way to trace.
                if (!string.IsNullOrEmpty(entry.observedBy) || !string.IsNullOrEmpty(entry.observedAt))
                {
                    EditorGUILayout.LabelField(
                        "observed by " + (string.IsNullOrEmpty(entry.observedBy) ? "—" : entry.observedBy)
                        + " at " + (string.IsNullOrEmpty(entry.observedAt) ? "—" : entry.observedAt)
                        + " against " + (string.IsNullOrEmpty(entry.sdkVersion) ? "—" : entry.sdkVersion),
                        EditorStyles.wordWrappedMiniLabel);
                }

                if (!string.IsNullOrEmpty(entry.detail))
                {
                    EditorGUILayout.LabelField(entry.detail, EditorStyles.wordWrappedMiniLabel);
                }

                _runtimeDetails[key] = EditorGUILayout.TextField(
                    _runtimeDetails.TryGetValue(key, out string draft) ? draft : entry.detail ?? string.Empty);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("I saw this work", GUILayout.Width(120)))
                {
                    RecordRuntime(key, "passed");
                }

                if (GUILayout.Button("It failed", GUILayout.Width(80)))
                {
                    RecordRuntime(key, "failed");
                }

                if (GUILayout.Button("Not tested", GUILayout.Width(90)))
                {
                    RecordRuntime(key, null);
                }

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }

            if (_runtimeError != null)
            {
                EditorGUILayout.HelpBox(_runtimeError, MessageType.Error);
            }
            else if (_runtimeStatus != null)
            {
                EditorGUILayout.HelpBox(_runtimeStatus, MessageType.Info);
            }

            EditorGUI.indentLevel--;
        }

        private static readonly List<ReadinessArtifactData.QuestionEntry> EmptyQuestions =
            new List<ReadinessArtifactData.QuestionEntry>();

        private static readonly List<ReadinessArtifactData.AuthorityEntry> EmptyAuthority =
            new List<ReadinessArtifactData.AuthorityEntry>();

        // Where an answer is written: beside the artifact, under the name the
        // artifact itself carries. Null when this artifact names none — an older
        // one — so the questions still render and the recording controls do not
        // pretend to a destination they do not have.
        private string AnswersPath()
        {
            if (_artifact == null || string.IsNullOrEmpty(_artifact.answersFile)) return null;
            if (string.IsNullOrEmpty(_artifactPath)) return null;

            // ⛔ The LEAF only, never the artifact's string as a path. Path.Combine
            // discards its first argument when the second is rooted and does not
            // resolve "..", so an artifact naming "/etc/x.json" or "../x.json"
            // would choose where this window writes — and this window invites
            // loading an artifact it did not produce, which is exactly where such
            // a name comes from. The engine always emits a bare file name, so
            // nothing correct is lost.
            string name = Path.GetFileName(_artifact.answersFile);
            if (string.IsNullOrEmpty(name)) return null;

            string directory = Path.GetDirectoryName(_artifactPath);
            return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
        }

        // The answer standing for a type: what this session recorded, else what
        // the artifact was scored with.
        private string ChosenAnswer(string typeName)
        {
            if (_recordedThisSession.TryGetValue(typeName, out string recorded))
            {
                return recorded;
            }

            foreach (var entry in _artifact.authority ?? EmptyAuthority)
            {
                if (entry != null && string.Equals(entry.name, typeName, StringComparison.Ordinal))
                {
                    return entry.declared ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private string NoteDraft(string typeName)
        {
            if (_answerNotes.TryGetValue(typeName, out string draft))
            {
                return draft;
            }

            foreach (var entry in _artifact.authority ?? EmptyAuthority)
            {
                if (entry != null && string.Equals(entry.name, typeName, StringComparison.Ordinal))
                {
                    return entry.declaredNote ?? string.Empty;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Whether this artifact points both records at one file.
        /// </summary>
        /// <remarks>
        /// ⛔ The same refusal the host makes for <c>--answers</c> and
        /// <c>--runtime</c> given one path, and reachable here for the same
        /// reason <see cref="RuntimePath"/> takes only the leaf: this window
        /// invites loading an artifact it did not produce.
        /// <para>
        /// 🚨 Stated once and asked by BOTH writers. It was written into the
        /// runtime writer alone, under a comment describing what recording an
        /// ANSWER would do — the one direction it did not cover. Whichever
        /// record is written second is destroyed, and the scan then refuses the
        /// survivor whole.
        /// </para>
        /// <para>
        /// ⚠️ Ordinal, and deliberately: two names differing only in case are one
        /// file on Windows and macOS and two on Linux, so the comparison that is
        /// safe everywhere is the one that refuses the pair it cannot separate.
        /// Both names come from the same emitter, which writes one spelling.
        /// </para>
        /// </remarks>
        private bool TheTwoRecordsAreOneFile()
        {
            string answers = AnswersPath();
            string runtime = RuntimePath();
            return answers != null && runtime != null
                && string.Equals(answers, runtime, StringComparison.OrdinalIgnoreCase);
        }

        private const string RecordsCollided =
            "This artifact names the same file for its authority answers and its runtime record, "
            + "and they are two different records; regenerate it with ";

        private void RecordAnswer(string typeName, string decidedBy)
        {
            string path = AnswersPath();
            _answerStatus = null;
            _answerError = null;

            if (TheTwoRecordsAreOneFile())
            {
                _answerError = RecordsCollided + ReadinessArtifactData.RegenerateCommand();
                return;
            }

            if (path == null)
            {
                _answerError = "This artifact predates the authority questions and names no answers "
                    + "file; regenerate it with " + ReadinessArtifactData.RegenerateCommand();
                return;
            }

            // The draft the field wrote on the previous repaint, or — on the frame
            // a question is first drawn — the note the artifact was scored with.
            string note = _answerNotes.TryGetValue(typeName, out string draft) ? draft : NoteDraft(typeName);
            if (!AuthorityAnswersFile.Record(path, typeName, decidedBy, note, out string error))
            {
                _answerError = "Could not record the answer: " + error;
                return;
            }

            _recordedThisSession[typeName] = decidedBy ?? string.Empty;

            // 🔑 The number on this window does not move until the scan runs
            // again, and saying so is the difference between a tool that asks and
            // one that only appears to. The command is interpolated for THIS
            // project, the way every other regenerate prompt here is.
            _answerStatus = (string.IsNullOrEmpty(decidedBy)
                    ? "Answer withdrawn for "
                    : "Answer recorded for ")
                + ReadinessArtifactData.ShortName(typeName) + " in " + path
                + " — it reaches the score on the next scan: "
                + ReadinessArtifactData.RegenerateCommand();
        }

        private void DrawQuestions()
        {
            // ⛔ Null-tolerant, like every other section this window renders. The
            // loader normalises an artifact written before these questions
            // existed, and an OnGUI that throws is a window nobody can use for as
            // long as that file is selected — not a visible degradation.
            var questions = _artifact.questions ?? EmptyQuestions;

            // ⚠️ Unanswered first, then the total. A question STAYS on this list
            // once answered — the answer is meant to be changeable — so a bare
            // count never falls, and the one thing a reader wants from the
            // heading is whether any are still open.
            int unanswered = 0;
            foreach (var question in questions)
            {
                if (question != null && string.IsNullOrEmpty(ChosenAnswer(question.name ?? string.Empty)))
                {
                    unanswered++;
                }
            }

            _foldQuestions = EditorGUILayout.Foldout(
                _foldQuestions,
                "Authority — questions for you (" + unanswered + " unanswered of " + questions.Count + ")",
                true);
            if (!_foldQuestions)
            {
                return;
            }

            if (questions.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No authority questions: every component type states its posture in its own code.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "The rubric never guesses, so it asks. These types are on the network and declare no " +
                "posture; an answer is recorded beside the artifact and read by the next scan — it " +
                "settles the Authority dimension and nothing else, because State and Ownership still " +
                "measure the code. An answered question stays here so the answer can be changed.",
                MessageType.None);

            foreach (var question in questions)
            {
                if (question == null) continue;

                // 🔑 A Dictionary refuses a null key by THROWING, and this runs
                // inside OnGUI — which repaints — so a nameless entry would be an
                // exception every frame rather than the visible degradation this
                // window promises everywhere else. The same rule, in the same
                // words, guards the type foldouts above.
                string key = question.name ?? string.Empty;
                string chosen = ChosenAnswer(key);
                bool pending = _recordedThisSession.ContainsKey(key);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(
                    new GUIContent(ReadinessArtifactData.ShortName(question.name), question.name),
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    (string.IsNullOrEmpty(chosen) ? "unanswered" : chosen)
                        + (pending ? " (recorded — not yet scored)" : string.Empty),
                    EditorStyles.miniBoldLabel);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField(question.prompt, EditorStyles.wordWrappedLabel);

                // ⛔ Drawn, never skipped. The count in the heading above is the
                // artifact's, so a question quietly dropped here leaves a reader
                // counting rows that are not there — a silence where the whole
                // design of this window is a visible degradation.
                if (question.options == null || question.options.Count == 0)
                {
                    EditorGUILayout.LabelField(
                        "This artifact carries no answers to choose from for this type — regenerate it: "
                        + ReadinessArtifactData.RegenerateCommand(),
                        EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.EndVertical();
                    continue;
                }

                foreach (var option in question.options)
                {
                    if (option == null) continue;

                    bool isChosen = string.Equals(chosen, option.id, StringComparison.Ordinal);
                    if (GUILayout.Button(
                            (isChosen ? "✓ " : string.Empty) + option.label,
                            EditorStyles.miniButton))
                    {
                        RecordAnswer(key, option.id);
                    }

                    EditorGUILayout.LabelField(
                        (option.needsServerImplementation ? "⚠️ " : string.Empty) + option.consequence,
                        EditorStyles.wordWrappedMiniLabel);
                }

                EditorGUILayout.LabelField(
                    "Why — kept beside the answer, for whoever reads it next", EditorStyles.miniLabel);
                _answerNotes[key] = EditorGUILayout.TextField(NoteDraft(key));

                if (!string.IsNullOrEmpty(chosen)
                    && GUILayout.Button("Withdraw this answer", EditorStyles.miniButton))
                {
                    RecordAnswer(key, null);
                }

                EditorGUILayout.EndVertical();
            }

            if (!string.IsNullOrEmpty(_answerError))
            {
                EditorGUILayout.HelpBox(_answerError, MessageType.Error);
            }
            else if (!string.IsNullOrEmpty(_answerStatus))
            {
                EditorGUILayout.HelpBox(_answerStatus, MessageType.Info);
            }
        }

        private void DrawTodo()
        {
            _foldTodo = EditorGUILayout.Foldout(
                _foldTodo, "To-do — remaining conversion work (" + _artifact.todo.Count + ")", true);
            if (!_foldTodo)
            {
                return;
            }

            if (_artifact.todo.Count == 0)
            {
                EditorGUILayout.HelpBox("No outstanding readiness items.", MessageType.Info);
                return;
            }

            foreach (var item in _artifact.todo)
            {
                EditorGUILayout.LabelField("• " + item, EditorStyles.wordWrappedLabel);
            }
        }
    }
}
#endif
