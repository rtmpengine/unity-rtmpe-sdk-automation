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
        private bool _foldTodo;
        private bool _sortWorstFirst;
        private readonly Dictionary<string, bool> _typeFold = new Dictionary<string, bool>();

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
                    "No readiness artifact found at:\n" + _artifactPath +
                    "\n\nGenerate it headlessly from the repository root:\n" +
                    "    make readiness\n" +
                    "or download CI's `network-readiness` artifact, then Refresh.",
                    MessageType.Info);
                return;
            }

            DrawHeader();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawTypes();
            DrawAuthority();
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

            GUILayout.FlexibleSpace();
            GUILayout.Label(Path.GetFileName(_artifactPath), EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Project score: " + _artifact.projectScore + "%", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                "generated " + _artifactWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (DateTime.UtcNow - _artifactWriteTimeUtc > StaleAge)
            {
                EditorGUILayout.HelpBox(
                    "This artifact is more than 24 hours old and may not reflect the current " +
                    "code. Re-run `make readiness` and Refresh.",
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

                bool open;
                _typeFold.TryGetValue(type.name, out open);
                EditorGUILayout.BeginHorizontal();
                // Tooltip carries the namespace-qualified name the row elides.
                open = EditorGUILayout.Foldout(open, new GUIContent(ShortName(type.name), type.name), true);
                GUILayout.FlexibleSpace();
                GUILayout.Label(type.score + "%", type.score >= 100
                    ? EditorStyles.boldLabel
                    : EditorStyles.label);
                EditorGUILayout.EndHorizontal();
                _typeFold[type.name] = open;

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
                GUILayout.Label(new GUIContent(ShortName(entry.name), entry.name), EditorStyles.boldLabel);
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

        // Namespace-qualified names dominate the row width; the qualifier adds
        // nothing inside a per-project window, so rows show the simple name and
        // keep the full name reachable as the row's tooltip.
        private static string ShortName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return fullName;
            }

            int lastDot = fullName.LastIndexOf('.');
            return lastDot >= 0 && lastDot + 1 < fullName.Length
                ? fullName.Substring(lastDot + 1)
                : fullName;
        }
    }
}
#endif
