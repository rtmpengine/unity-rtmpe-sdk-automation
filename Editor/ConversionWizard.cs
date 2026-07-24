// RTMPE SDK — Editor/ConversionWizard.cs
//
// The in-editor host for the mechanical single-player → multiplayer
// conversions.  Open via: Window > RTMPE > Conversion Wizard.
//
// Design constraints (CONVERSION_WIZARD.md; completion-plan W3):
//  • THIN SHELL, NOT AN ENGINE (DD-W3-1).  v1 drives the proven headless
//    conversion CLI (`RTMPE.SDK.ConversionCli`) as an external process — the
//    same binary `make fix` / `make convert` / `make gen-rpc` run — so the
//    wizard's edit and the CLI's edit are byte-identical by *identity*, not
//    merely by shared code, and the editor loads no Roslyn at all.  Hosting
//    the transform core in-process is the post-W0 upgrade (it rides the
//    analyzer load spike in COMPILER_COMPATIBILITY.md) and changes only this
//    window's plumbing, not its flow.
//  • MANDATORY DIFF APPROVAL.  Nothing is written without the author
//    confirming the exact previewed diff first; there is no silent or
//    auto-apply path.  The identity-allocating conversions (NetworkVariable,
//    Enhanced RPC) show the source diff and the ledger diff in one preview,
//    and the RPC audience is always the human's designation, never inferred.
//  • OWN UNDO.  Unity's Undo API does not track external file writes, so the
//    wizard snapshots every file the diff names before applying and Revert
//    restores those snapshots.  The snapshots are serialised window state, not
//    plain fields: applying re-imports the edited script, and the domain reload
//    that follows would otherwise discard the undo path at the exact moment it
//    becomes useful.  They live for the editor session, not across a restart.
//  • SOURCE-DIFF-ONLY.  Scene/prefab YAML is invisible to the diff; the
//    standing warning tells the author to re-wire prefabs and scenes.
//
// Requirements: the RTMPE repository checkout (the CLI project lives in
// clients/unity-sdk/Tooling) and a .NET SDK on PATH.  Both are development
// prerequisites this embedded project already carries; the wizard degrades to
// an explanatory message when either is missing.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Stepper window: pick a script and a conversion, preview the exact diff
    /// the headless engine produces, approve it explicitly, apply atomically,
    /// and revert from the wizard's own snapshots if needed.
    /// </summary>
    public sealed class ConversionWizard : EditorWindow
    {
        // ── Conversions offered (each fronts one proven CLI verb) ───────────────

        private enum ConversionKind
        {
            Rebase,          // fix --kind rebase          (RTMPE2001, identity-free)
            OwnerGuard,      // fix --kind owner-guard     (RTMPE2003, identity-free)
            BaseOnDestroy,   // fix --kind base-ondestroy  (RTMPE1020, identity-free)
            NetworkVariable, // convert --member …         (allocates variableId in the ledger)
            EnhancedRpc,     // gen-rpc --method …:target  (derives the RPC id, ledger provenance)
        }

        private static readonly string[] KindLabels =
        {
            "Rebase to NetworkBehaviour (RTMPE2001)",
            "Insert owner guard in Update (RTMPE2003)",
            "Chain base.OnDestroy() (RTMPE1020)",
            "Generate NetworkVariable (RTMPE2002 — allocates an id)",
            "Generate Enhanced RPC (RTMPE2004 — derives an id)",
        };

        // Exactly the audiences the RPC host accepts (pinned by
        // WizardCliContractTests); Server first as the safe default — a
        // state-mutating method's audience is the human's designation (M3).
        private static readonly string[] RpcAudiences = { "Server", "Others", "All", "AllBuffered" };

        // ── Window state ────────────────────────────────────────────────────────

        private sealed class Candidate
        {
            public string FullTypeName;
            public string Role;
            public string AssetPath;   // null when unresolved
            public string Problem;     // why it is unresolved / not convertible
        }

        // Serializable, and held in a serialized field, because Apply re-imports
        // the edited script: the recompile and domain reload that follows
        // reconstructs this window, and anything Unity does not serialise is gone
        // by the time the author looks for Revert. Undo that survives only until
        // the edit it undoes takes effect is not an undo.
        [Serializable]
        private sealed class Snapshot
        {
            public string Path;
            public bool Existed;
            // Bytes, not text: Revert must restore the file exactly — a text
            // round-trip would strip a BOM and normalise what it never read.
            public byte[] Bytes;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();
        private string _artifactError;
        private bool _scanned;
        private int _selected = -1;
        private ConversionKind _kind;
        private string _memberSpec = string.Empty;
        private string _rpcMethod = string.Empty;
        private int _rpcAudience; // index into RpcAudiences
        private string _previewDiff;
        private string _previewNote;
        private List<string> _previewFiles;
        // The exact argument vector the shown diff came from. Apply reuses it
        // verbatim (+ --apply) so an input edited after previewing can never
        // smuggle an unpreviewed diff past the approval gate.
        private List<string> _previewArgs;
        // Every file the previewed diff names, as it stood when that diff was
        // computed. Apply refuses if any of them has moved since: the human
        // approved a diff derived from *these* bytes, and for the
        // identity-allocating conversions the ledger among them is what decides
        // the wire id — pinning only the script would let the id that ships
        // differ from the id that was shown.
        private List<Snapshot> _previewPins;
        private string _lastError;
        // The undo state is the only window state that must outlive the apply, so
        // it is the only state serialised: everything above it belongs to a
        // preview that is consumed before any write.
        [SerializeField] private List<Snapshot> _lastApply = new List<Snapshot>();
        [SerializeField] private string _lastApplySummary;
        private Vector2 _scroll;
        private Vector2 _diffScroll;

        // ── Entry point ─────────────────────────────────────────────────────────

        [MenuItem("Window/RTMPE/Conversion Wizard")]
        public static void Open()
        {
            var win = GetWindow<ConversionWizard>(false, "RTMPE Conversion", true);
            win.minSize = new Vector2(520, 460);
            win.Show();
        }

        // ── Path resolution ─────────────────────────────────────────────────────

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        // The embedded Unity project lives at <repo>/clients/unity-sdk, so the
        // repository root is two levels above the project root.  Outside the
        // RTMPE repo checkout there is no CLI to drive, and the wizard says so.
        private static string RepoRoot
            => Path.GetFullPath(Path.Combine(ProjectRoot, "..", ".."));

        private static string CliProject
            => Path.Combine(RepoRoot, "clients", "unity-sdk", "Tooling", "RTMPE.SDK.ConversionCli");

        // Release, matching the Makefile's host verbs: the wizard's edit and a
        // `make fix` edit are the same edit only while both run the same build.
        private const string BuildConfiguration = "Release";

        private const string EngineAssemblyName = "RTMPE.SDK.ConversionCli.dll";

        // A cold build compiles the engine and its analyzer dependencies; the run
        // that follows only parses one file. Separate budgets keep a slow first
        // build from being read as a hung conversion, and keep a genuinely stuck
        // conversion from being waited on for three minutes.
        private const int BuildTimeoutMs = 180_000;
        private const int RunTimeoutMs = 60_000;
        // The wait is polled in slices this long so the editor stays responsive and
        // the operator's cancel is seen within one slice rather than after minutes.
        private const int CancelPollIntervalMs = 100;

        // ── GUI ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Conversions are source-only. Prefab and scene YAML is invisible to the "
                + "diff below — after applying, re-wire the affected prefabs/scenes by hand.",
                MessageType.Warning);

            if (!Directory.Exists(CliProject))
            {
                EditorGUILayout.HelpBox(
                    "The conversion CLI project was not found at:\n" + CliProject +
                    "\n\nThe wizard drives the repository's headless conversion host, so it "
                    + "runs only inside the RTMPE repository checkout.",
                    MessageType.Error);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawScanStep();
            if (_selected >= 0 && _selected < _candidates.Count)
            {
                DrawActionStep();
                DrawPreviewAndApplyStep();
            }

            DrawRevertStep();
            EditorGUILayout.EndScrollView();
        }

        // ── Step 1 — Scan ───────────────────────────────────────────────────────

        private void DrawScanStep()
        {
            EditorGUILayout.LabelField("1. Pick a script (from the readiness artifact)", EditorStyles.boldLabel);

            if (GUILayout.Button(_scanned ? "Re-scan" : "Scan", GUILayout.Width(90)))
            {
                Scan();
            }

            if (_artifactError != null)
            {
                EditorGUILayout.HelpBox(_artifactError, MessageType.Warning);
            }

            if (!_scanned)
            {
                return;
            }

            if (_candidates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No candidate scripts. Generate the readiness artifact first:\n    make readiness",
                    MessageType.Info);
                return;
            }

            for (int i = 0; i < _candidates.Count; i++)
            {
                var candidate = _candidates[i];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                using (new EditorGUI.DisabledScope(candidate.AssetPath == null))
                {
                    bool on = GUILayout.Toggle(_selected == i, GUIContent.none, GUILayout.Width(18));
                    if (on && _selected != i)
                    {
                        _selected = i;
                        ClearPreview();
                    }
                }

                GUILayout.Label(new GUIContent(ShortName(candidate.FullTypeName), candidate.FullTypeName));
                GUILayout.FlexibleSpace();
                GUILayout.Label(candidate.Role, EditorStyles.miniBoldLabel);
                EditorGUILayout.EndHorizontal();

                if (candidate.Problem != null)
                {
                    EditorGUILayout.LabelField("   " + candidate.Problem, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private void Scan()
        {
            _candidates.Clear();
            _selected = -1;
            ClearPreview();
            _scanned = true;

            var artifact = ReadinessArtifactData.Load(ReadinessArtifactData.DefaultPath(), out _artifactError);
            if (artifact == null)
            {
                _artifactError = _artifactError
                    ?? "No readiness artifact at " + ReadinessArtifactData.DefaultPath()
                    + " — run `make readiness` from the repository root first.";
                return;
            }

            // The authority block is the superset (plain MonoBehaviours included),
            // so orchestrators and presentation leaves are listed too — with their
            // role visible, the human sees why a type is or is not worth converting.
            foreach (var entry in artifact.authority)
            {
                var candidate = new Candidate { FullTypeName = entry.name, Role = entry.role };
                ResolveScript(candidate);
                _candidates.Add(candidate);
            }
        }

        // MonoBehaviour file naming is a Unity invariant (a mismatched file name
        // cannot be bound in a scene), so exact "<ShortName>.cs" under Assets/ is
        // a reliable resolution — no path plumbing through the artifact needed.
        // Editor/ and Tests/ scripts are excluded per the wizard's scan contract.
        private void ResolveScript(Candidate candidate)
        {
            string shortName = ShortName(candidate.FullTypeName);
            var matches = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript " + shortName))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)
                    || Path.GetFileName(path) != shortName + ".cs"
                    || path.Contains("/Editor/")
                    || path.Contains("/Tests/"))
                {
                    continue;
                }

                matches.Add(path);
            }

            if (matches.Count == 1)
            {
                candidate.AssetPath = matches[0];
            }
            else
            {
                candidate.Problem = matches.Count == 0
                    ? "no script by that name under Assets/ — the readiness artifact describes types "
                        + "outside this project (regenerate it over your own scripts: "
                        + "make readiness SOURCE=<dir>)"
                    : "ambiguous: " + matches.Count + " scripts named " + shortName + ".cs (fail-closed)";
            }
        }

        // ── Step 2 — Action ─────────────────────────────────────────────────────

        private void DrawActionStep()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("2. Choose the conversion", EditorStyles.boldLabel);

            var newKind = (ConversionKind)EditorGUILayout.Popup((int)_kind, KindLabels);
            if (newKind != _kind)
            {
                _kind = newKind;
                ClearPreview();
            }

            // Any edit to a human-designated input voids the previewed diff —
            // approval is only ever of the exact bytes on screen.
            if (_kind == ConversionKind.NetworkVariable)
            {
                string member = EditorGUILayout.TextField(
                    new GUIContent("Member", "field[:CompanionName] — the field to convert; you name it, the engine converts it"),
                    _memberSpec);
                if (member != _memberSpec) { _memberSpec = member; ClearPreview(); }
            }
            else if (_kind == ConversionKind.EnhancedRpc)
            {
                string method = EditorGUILayout.TextField(
                    new GUIContent("Method", "the method to convert to an [RtmpeRpc]"),
                    _rpcMethod);
                if (method != _rpcMethod) { _rpcMethod = method; ClearPreview(); }
                int audience = EditorGUILayout.Popup(
                    new GUIContent("Audience", "always your designation — never inferred (M3)"),
                    _rpcAudience, RpcAudiences);
                if (audience != _rpcAudience) { _rpcAudience = audience; ClearPreview(); }
            }
        }

        // ── Step 3 — Preview, approve, apply ────────────────────────────────────

        private void DrawPreviewAndApplyStep()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("3. Preview diff, then apply", EditorStyles.boldLabel);

            if (GUILayout.Button("Preview diff", GUILayout.Width(120)))
            {
                Preview();
            }

            if (_lastError != null)
            {
                EditorGUILayout.HelpBox(_lastError, MessageType.Error);
            }

            if (_previewNote != null)
            {
                EditorGUILayout.HelpBox(_previewNote, MessageType.Info);
            }

            if (string.IsNullOrEmpty(_previewDiff))
            {
                return;
            }

            _diffScroll = EditorGUILayout.BeginScrollView(_diffScroll, GUILayout.MinHeight(160));
            EditorGUILayout.TextArea(_previewDiff, EditorStyles.label);
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Apply this exact diff…", GUILayout.Width(180)))
            {
                Apply();
            }
        }

        private void Preview()
        {
            ClearPreview();
            var args = BuildArgs(apply: false);
            if (args == null)
            {
                return;
            }

            // Read before the engine does. A pin taken only afterwards records
            // whatever the file became, which is not necessarily what the diff
            // was computed from — and then agrees with disk at apply time while
            // the human's approval refers to bytes nobody still has.
            string sourceAbsolute = ToAbsolute(_candidates[_selected].AssetPath);
            byte[] beforeRun = File.ReadAllBytes(sourceAbsolute);

            if (!RunCli(args, out string stdout, out string error))
            {
                _lastError = error;
                return;
            }

            // Reading again closes the remaining gap: neither side alone can tell
            // a file that held still from one edited while the engine had it open.
            if (!BytesEqual(beforeRun, File.ReadAllBytes(sourceAbsolute)))
            {
                _lastError = "The script changed while the engine was reading it — preview again.";
                return;
            }

            _previewArgs = args;
            if (IsNoOp(stdout))
            {
                // Idempotency surfaced, not hidden: the script is already at the
                // destination shape for this conversion.
                _previewNote = stdout.Trim();
                return;
            }

            _previewDiff = stdout;
            _previewFiles = ChangedFiles(stdout);
            if (_previewFiles.Count == 0)
            {
                _previewDiff = null;
                _lastError = "The engine returned no diff — nothing to apply.";
                return;
            }

            // The diff names its own file set, so the pins can only be taken once
            // it exists. That leaves the run itself unpinned for the ledger — a
            // sub-second window — while covering the one that matters: the
            // minutes a human spends reading the diff before approving it.
            _previewPins = Pin(Path.GetDirectoryName(sourceAbsolute), _previewFiles);
        }

        // A file the diff names, as it stands now. A file that does not exist is
        // pinned as absent rather than skipped: a ledger appearing between the
        // preview and the apply changes which ids the engine will find free, and
        // is exactly as much of a change as an edited one.
        private static List<Snapshot> Pin(string directory, List<string> fileNames)
        {
            var pins = new List<Snapshot>(fileNames.Count);
            foreach (string name in fileNames)
            {
                string path = Path.Combine(directory, name);
                bool exists = File.Exists(path);
                pins.Add(new Snapshot
                {
                    Path = path,
                    Existed = exists,
                    Bytes = exists ? File.ReadAllBytes(path) : null,
                });
            }

            return pins;
        }

        // The name of the first pinned file that no longer matches, or null when
        // every one of them still stands as previewed. Appearance and deletion
        // count as changes: the engine's next read would see a different world
        // either way.
        private string FirstMovedPin()
        {
            if (_previewPins == null)
            {
                return "the preview";
            }

            foreach (var pin in _previewPins)
            {
                bool exists = File.Exists(pin.Path);
                if (exists != pin.Existed
                    || (exists && !BytesEqual(pin.Bytes, File.ReadAllBytes(pin.Path))))
                {
                    return Path.GetFileName(pin.Path);
                }
            }

            return null;
        }

        private void Apply()
        {
            if (_previewFiles == null || _previewFiles.Count == 0 || _selected < 0)
            {
                return;
            }

            var candidate = _candidates[_selected];
            string sourceAbsolute = ToAbsolute(candidate.AssetPath);
            string sourceDir = Path.GetDirectoryName(sourceAbsolute);

            // The previewed diff was computed from the pinned bytes; if any file
            // it names has changed since (an IDE save, a git operation, a
            // concurrent conversion), the engine would apply an edit the human
            // never saw. Fail closed, and name the file so the cause is findable.
            string moved = FirstMovedPin();
            if (moved != null)
            {
                // Clear first: ClearPreview() resets _lastError, so the message
                // must be set after it or the user never sees why.
                ClearPreview();
                _lastError = "'" + moved + "' changed on disk after the preview — preview again.";
                return;
            }

            // The mandatory gate: the human confirms the exact previewed diff and
            // the exact file set it names (source + ledger together for the
            // identity-allocating conversions) before a byte is written.
            if (!EditorUtility.DisplayDialog(
                "Apply conversion?",
                "Apply the previewed diff to:\n\n  " + string.Join("\n  ", _previewFiles)
                + "\n\nThe wizard snapshots these files; Revert restores them.",
                "Apply", "Cancel"))
            {
                return;
            }

            // Snapshot exactly the files the approved diff names, before writing.
            _lastApply.Clear();
            foreach (var name in _previewFiles)
            {
                string path = Path.Combine(sourceDir, name);
                _lastApply.Add(new Snapshot
                {
                    Path = path,
                    Existed = File.Exists(path),
                    Bytes = File.Exists(path) ? File.ReadAllBytes(path) : null,
                });
            }

            // The pinned preview arguments — never rebuilt from the UI fields.
            var args = new List<string>(_previewArgs) { "--apply" };
            if (!RunCli(args, out string stdout, out string error))
            {
                // Keep the snapshots: the engine is fail-closed, but if anything
                // was written in a partial window, Revert is the recovery path.
                _lastError = error;
                _lastApplySummary = ShortName(candidate.FullTypeName) + " (failed apply — snapshots retained)";
                return;
            }

            foreach (var name in _previewFiles)
            {
                Reimport(Path.Combine(sourceDir, name));
            }

            _lastError = null;
            _lastApplySummary = ShortName(candidate.FullTypeName) + " — " + KindLabels[(int)_kind];
            _previewNote = "Applied. " + stdout.Trim().Split('\n')[stdout.Trim().Split('\n').Length - 1]
                + "\nRe-wire any prefabs/scenes that reference this script.";
            _previewDiff = null;
            _previewFiles = null;
        }

        // ── Revert ──────────────────────────────────────────────────────────────

        private void DrawRevertStep()
        {
            if (_lastApply.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Undo", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Last apply: " + _lastApplySummary, EditorStyles.miniLabel);
            if (GUILayout.Button("Revert", GUILayout.Width(80)))
            {
                Revert();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void Revert()
        {
            foreach (var snapshot in _lastApply)
            {
                // `Existed` is the discriminator, never `Bytes == null`: Unity's
                // serialisation restores a null array as an empty one, so a
                // created-file snapshot returns from a domain reload with zero
                // bytes rather than none.
                if (snapshot.Existed)
                {
                    File.WriteAllBytes(snapshot.Path, snapshot.Bytes);
                }
                else if (File.Exists(snapshot.Path))
                {
                    // The apply created it (a fresh ledger): remove it and the
                    // .meta the import generated, so no orphan asset remains.
                    File.Delete(snapshot.Path);
                    if (File.Exists(snapshot.Path + ".meta"))
                    {
                        File.Delete(snapshot.Path + ".meta");
                    }
                }

            }

            // One refresh covers restored and deleted files alike — ImportAsset
            // on a deleted path would log a spurious error.
            AssetDatabase.Refresh();
            _lastApply.Clear();
            _lastApplySummary = null;
            _previewNote = "Reverted to the pre-apply snapshots.";
        }

        // ── CLI plumbing ────────────────────────────────────────────────────────

        private List<string> BuildArgs(bool apply)
        {
            var candidate = _candidates[_selected];
            string file = ToAbsolute(candidate.AssetPath);
            var args = new List<string>();

            switch (_kind)
            {
                case ConversionKind.Rebase:
                    args.AddRange(new[] { "fix", "--file", file, "--type", candidate.FullTypeName, "--kind", "rebase" });
                    break;
                case ConversionKind.OwnerGuard:
                    args.AddRange(new[] { "fix", "--file", file, "--type", candidate.FullTypeName, "--kind", "owner-guard" });
                    break;
                case ConversionKind.BaseOnDestroy:
                    args.AddRange(new[] { "fix", "--file", file, "--type", candidate.FullTypeName, "--kind", "base-ondestroy" });
                    break;
                case ConversionKind.NetworkVariable:
                    if (string.IsNullOrWhiteSpace(_memberSpec))
                    {
                        _lastError = "Name the member to convert (field[:CompanionName]).";
                        return null;
                    }

                    args.AddRange(new[] { "--file", file, "--type", candidate.FullTypeName, "--member", _memberSpec.Trim() });
                    break;
                default: // EnhancedRpc
                    if (string.IsNullOrWhiteSpace(_rpcMethod))
                    {
                        _lastError = "Name the method to convert.";
                        return null;
                    }

                    args.AddRange(new[]
                    {
                        "gen-rpc", "--file", file, "--type", candidate.FullTypeName,
                        "--method", _rpcMethod.Trim() + ":" + RpcAudiences[_rpcAudience],
                    });
                    break;
            }

            if (apply)
            {
                args.Add("--apply");
            }

            return args;
        }

        // The engine runs in two phases, and the split is a safety property
        // rather than a structuring preference.
        //
        // `dotnet run` is a LAUNCHER: it builds, then starts the built binary as
        // a separate child. Process.Start therefore hands back the launcher, and
        // Kill() ends only that — the binary underneath survives. On an --apply
        // run that binary is the one holding the source file and the ledger open,
        // so a timeout would report failure, restore nothing, and leave an
        // unsupervised process to finish writing afterwards, racing whatever the
        // user does next with Revert. Kill(entireProcessTree) would answer it,
        // but it is .NET Core 3.0+ and absent from the Editor's target surface.
        //
        // Building first and then invoking the built assembly directly leaves one
        // process at every moment. The phase that can be killed mid-flight (the
        // build) writes only into bin/, and the phase that touches the user's
        // files cannot outlive its own kill.
        private static bool RunCli(List<string> args, out string stdout, out string error)
        {
            stdout = null;
            error = null;

            try
            {
                if (!Run(BuildArguments(), BuildTimeoutMs,
                        "Building the headless conversion engine…", 0.25f,
                        out string buildOutput, out string buildErrors, out int buildExit))
                {
                    error = BuildFailureText(buildErrors, buildOutput);
                    return false;
                }

                if (buildExit != 0)
                {
                    error = "The conversion engine failed to build:\n"
                        + BuildFailureText(buildErrors, buildOutput);
                    return false;
                }

                // Resolved after the build so a first run in a clean checkout
                // finds it. A missing assembly here means the build reported
                // success without producing one, which is a broken toolchain
                // rather than a refusal, and must not be read as either.
                string assembly = EngineAssembly(out string problem);
                if (assembly == null)
                {
                    error = problem;
                    return false;
                }

                var invocation = new List<string> { assembly };
                invocation.AddRange(args);

                if (!Run(invocation, RunTimeoutMs,
                        "Running the headless conversion engine…", 0.75f,
                        out string output, out string errors, out int exit))
                {
                    error = errors;
                    return false;
                }

                stdout = output;
                if (exit != 0)
                {
                    // 1 usage, 2 environment, 3 refusal, 4 apply-time stop —
                    // all fail-closed: the engine wrote nothing.
                    error = "Engine exit " + exit + ":\n" + errors.Trim();
                    return false;
                }

                return true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // `dotnet build` rather than `run`: this phase is allowed to be a
        // launcher precisely because nothing it spawns writes outside bin/.
        private static List<string> BuildArguments()
            => new List<string> { "build", CliProject, "-c", BuildConfiguration, "--nologo" };

        // The built assembly, discovered rather than composed: the target
        // framework is the csproj's to choose, and a wizard that hardcodes it
        // starts failing on the release that moves it — reporting "not found"
        // against a path that reads perfectly correct.
        //
        // Two candidates mean a stale framework directory survived a bump, and
        // the window refuses rather than picking. Which assembly runs is the
        // whole of the guarantee that the wizard's edit is the CLI's edit; an
        // ambiguity resolved silently here is that guarantee quietly withdrawn.
        private static string EngineAssembly(out string problem)
        {
            problem = null;
            string root = Path.Combine(CliProject, "bin", BuildConfiguration);
            if (!Directory.Exists(root))
            {
                problem = "the engine built but produced no output under:\n" + root;
                return null;
            }

            var built = Directory.GetFiles(root, EngineAssemblyName, SearchOption.AllDirectories);
            if (built.Length == 0)
            {
                problem = "the engine built but produced no " + EngineAssemblyName + " under:\n" + root;
                return null;
            }

            if (built.Length > 1)
            {
                Array.Sort(built, StringComparer.Ordinal);
                problem = "more than one built engine is present, so which one would run is undefined:\n  "
                    + string.Join("\n  ", built)
                    + "\n\nDelete the stale framework directory (or run `dotnet clean` on the CLI project).";
                return null;
            }

            return built[0];
        }

        // What to show when the engine build fails. MSBuild reports compiler and
        // restore diagnostics on standard output and commonly leaves standard error
        // empty, so a reader that only reads the error stream has nothing to show
        // precisely when there is most to say. Standard error still leads when it
        // carries anything — it is where a launch fault surfaces — and the output
        // stream stands in behind it.
        private static string BuildFailureText(string standardError, string standardOutput)
        {
            string reported = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            return string.IsNullOrWhiteSpace(reported)
                ? "The build reported no diagnostics on either stream."
                : reported.Trim();
        }

        // One process, start to finish: the assembly is handed to `dotnet` to
        // execute in place, so what Process.Start returns is what does the work
        // and what Kill() reaches.
        private static bool Run(
            List<string> args, int timeoutMs, string progressMessage, float progress,
            out string stdout, out string stderr, out int exitCode)
        {
            stdout = null;
            stderr = null;
            exitCode = -1;

            var info = new ProcessStartInfo
            {
                FileName = "dotnet",
                // The repo root, deliberately outside Tooling/: the Tooling-scoped
                // global.json pins the exact SDK for byte-compared analyzer builds
                // only, and must not bind the developer's machine here.
                WorkingDirectory = RepoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            try
            {
                using (var process = Process.Start(info))
                {
                    // Event-driven capture while waiting: reading the pipes only
                    // after WaitForExit deadlocks once the child fills a pipe
                    // buffer (a large diff is exactly that case).
                    var output = new System.Text.StringBuilder();
                    var errors = new System.Text.StringBuilder();
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Poll the wait in short slices rather than blocking once for the
                    // whole timeout, so the editor main thread stays responsive and
                    // the operator can abort a long build. The async readers above
                    // keep draining throughout, so no pipe fills while we wait. A
                    // cancel or a timeout kills the child — fail-closed on both, since
                    // nothing has been written on any path that reaches here.
                    int waited = 0;
                    while (!process.WaitForExit(CancelPollIntervalMs))
                    {
                        if (EditorUtility.DisplayCancelableProgressBar("RTMPE Conversion", progressMessage, progress))
                        {
                            process.Kill();
                            stderr = "The conversion was canceled.";
                            return false;
                        }

                        waited += CancelPollIntervalMs;
                        if (waited >= timeoutMs)
                        {
                            process.Kill();
                            stderr = "The conversion engine timed out (" + (timeoutMs / 1000) + " s).";
                            return false;
                        }
                    }

                    // Parameterless WaitForExit drains the async readers so the
                    // captured text is complete before the exit code is read.
                    process.WaitForExit();

                    stdout = output.ToString();
                    stderr = errors.ToString();
                    exitCode = process.ExitCode;
                    return true;
                }
            }
            catch (Exception e)
            {
                // Reached only when the process could not be started at all,
                // which is the one failure whose cause is worth guessing at.
                stderr = "Could not run `dotnet` — is the .NET SDK on PATH?\n" + e.Message;
                return false;
            }
        }

        // A host reports idempotency with a line opening "no-op:". The test is
        // line-wise rather than whole-output, because a host may legitimately
        // print an informational "note:" line ahead of its verdict — the RPC
        // verb does exactly that for a method already carrying the attribute,
        // and a whole-output prefix test reads that correct, idempotent run as
        // a diff with no changed files, which the wizard then reports as an
        // error. Anchoring at the start of a line keeps the test unambiguous:
        // every diff line carries a ' ', '+', '-' or '@' prefix, so source text
        // that merely contains the marker can never match.
        private static bool IsNoOp(string stdout)
        {
            foreach (string line in stdout.Split('\n'))
            {
                if (line.StartsWith("no-op:", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // The diff labels every file it touches with the "--- a/<name>" /
        // "+++ b/<name>" header pair the CLI emits together (ConversionCli.PrintDiff)
        // — parse that instead of re-deriving host file-naming rules here.
        //
        // A "+++ b/" line is a file header only when the line before it is the
        // "--- a/" half naming the same file. A converted body line can render
        // identically — a source line whose content begins "++ b/…", prefixed with
        // the diff's own "+", becomes "+++ b/…" — and matching it as a header would
        // invent a changed file that was never touched. The pairing check reads it
        // as the body content it is.
        private static List<string> ChangedFiles(string diff)
        {
            var files = new List<string>();
            string previous = null;
            foreach (var line in diff.Split('\n'))
            {
                if (line.StartsWith("+++ b/", StringComparison.Ordinal)
                    && previous != null
                    && previous.StartsWith("--- a/", StringComparison.Ordinal)
                    && previous.Substring("--- a/".Length) == line.Substring("+++ b/".Length))
                {
                    files.Add(line.Substring("+++ b/".Length).Trim());
                }

                previous = line;
            }

            return files;
        }

        private void ClearPreview()
        {
            _previewDiff = null;
            _previewNote = null;
            _previewFiles = null;
            _previewArgs = null;
            _previewPins = null;
            _lastError = null;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string ToAbsolute(string assetPath)
            => Path.Combine(ProjectRoot, assetPath);

        private static void Reimport(string absolutePath)
        {
            string projectRelative = absolutePath.StartsWith(ProjectRoot, StringComparison.Ordinal)
                ? absolutePath.Substring(ProjectRoot.Length + 1).Replace('\\', '/')
                : null;
            if (projectRelative != null && projectRelative.StartsWith("Assets/", StringComparison.Ordinal))
            {
                AssetDatabase.ImportAsset(projectRelative);
            }
        }

        private static string ShortName(string fullName)
        {
            int lastDot = fullName?.LastIndexOf('.') ?? -1;
            return lastDot >= 0 && lastDot + 1 < fullName.Length
                ? fullName.Substring(lastDot + 1)
                : fullName;
        }
    }
}
#endif
