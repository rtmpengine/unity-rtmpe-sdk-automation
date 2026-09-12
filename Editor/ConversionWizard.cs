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
using System.Text;
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
            NetworkVariable, // convert --member …         (derives the id from the member name)
            EnhancedRpc,     // gen-rpc --method …:target  (derives the RPC id, ledger provenance)
        }

        private static readonly string[] KindLabels =
        {
            "Rebase to NetworkBehaviour (RTMPE2001)",
            "Insert owner guard in Update (RTMPE2003)",
            "Chain base.OnDestroy() (RTMPE1020)",
            "Generate NetworkVariable (RTMPE2002 — derives an id)",
            "Generate Enhanced RPC (RTMPE2004 — derives an id)",
        };

        // Exactly the audiences the RPC host accepts, pinned by
        // WizardCliContractTests.
        private static readonly string[] RpcAudiences = { "Server", "Others", "All", "AllBuffered" };

        /// <summary>
        /// The popup's first entry: not an audience, the absence of one.
        /// </summary>
        /// <remarks>
        /// 🔑 The host refuses to guess an audience for a method that mutates
        /// instance state — it exits 2 and says so — and this window used to
        /// make that refusal unreachable by always appending a designation. Index
        /// 0 was `Server`, so an author who touched nothing shipped a target that
        /// runs in a backend handler nobody has registered, under a tooltip
        /// promising the audience is never inferred.
        /// <para>
        /// ⛔ The choice is between three answers, not two. Others and All put an
        /// unvalidated write on every peer; Server runs nowhere; and neither is
        /// the default, because the engine already holds the rule that the
        /// designation is the author's. Selecting this entry sends no `:target`
        /// and lets the engine apply it.
        /// </para>
        /// </remarks>
        private const string AudienceUndesignated = "— choose one —";

        // Derived, so an audience added to the host's set reaches the popup
        // without a second list remembering to grow.
        private static readonly string[] AudienceChoices = BuildAudienceChoices();

        private static string[] BuildAudienceChoices()
        {
            var choices = new string[RpcAudiences.Length + 1];
            choices[0] = AudienceUndesignated;
            RpcAudiences.CopyTo(choices, 1);
            return choices;
        }

        // What an audience costs the author, stated where the audience is chosen.
        //
        // 🔑 Only Server carries a caution that holds before the method is read.
        // The host's other two — Others and All against an owner-guarded body —
        // are properties of the METHOD, and the popup is drawn before any body
        // has been parsed; those reach the author from the engine, on the diff,
        // with the body in hand.
        //
        // ⚠️ Phrased as a property of the target rather than a promise about
        // this conversion. The host leaves an already-annotated method's audience
        // alone, so a sentence claiming what THIS run will do would be false on
        // exactly the methods that already carry `[RtmpeRpc]`.
        internal static string AudienceCaveat(string audience)
            => audience == "Server"
                ? "A Server-targeted RPC executes only in a backend handler registered"
                    + " for its method id (RegisterServerRpc). With none registered the send"
                    + " resolves to an unknown method, and a converted call site no longer"
                    + " runs the body locally either."
                : null;

        // ── Window state ────────────────────────────────────────────────────────

        private sealed class Candidate
        {
            public string FullTypeName;
            public string Role;
            public string AssetPath;   // null when unresolved
            public string Problem;     // why it is unresolved / not convertible

            // Included in the same batch as the selected type. Only the
            // NetworkVariable conversion reads this — it is the one conversion
            // with a batch verb behind it, and the one where a partial
            // application costs a wire identity rather than a re-run.
            public bool InBatch;

            // Per candidate, never per window: each type names its own member,
            // and the whole point of the batch is that they differ.
            public string MemberSpec = string.Empty;
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

        // Where this package actually sits on disk.  Asked of the package
        // manager rather than assembled from a path: a package installed from a
        // registry lives under Library/PackageCache with its version in the
        // folder name, one installed from git lives elsewhere again, and one
        // being developed lives under Packages/ — only the manager knows which,
        // and `resolvedPath` answers for all three.
        //
        // 🔑 Null is a real answer, not a failure: it is what comes back when
        // the Editor code was dropped into Assets/ rather than installed as a
        // package.  The locator treats an absent package root as one fewer
        // candidate, so that reader still gets the override and the repository
        // layout rather than an exception.
        private static string PackageRoot
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(ConversionCliLocator).Assembly);
                return info?.resolvedPath;
            }
        }

        // An author-supplied location for the conversion host, held per user:
        // the checkout sits somewhere different on every machine, so this belongs
        // beside the other editor preferences rather than in the project.
        private static string CliOverride
        {
            get => EditorPrefs.GetString(ConversionCliLocator.OverridePrefKey, string.Empty);
            set => EditorPrefs.SetString(ConversionCliLocator.OverridePrefKey, value ?? string.Empty);
        }

        // The paths consulted, in order, and the first of them that exists.
        // Ordering and fallbacks live in ConversionCliLocator so they are covered
        // off-Editor; resolution is re-run per access because the author may point
        // the wizard at a checkout while the window is open.
        private static IReadOnlyList<string> CliCandidates
            => ConversionCliLocator.Candidates(ProjectRoot, CliOverride, PackageRoot);

        private static string CliProject
            => ConversionCliLocator.Resolve(
                ProjectRoot, CliOverride, PackageRoot, Directory.Exists);

        // Launch directory for the host's build and run.  Derived from the
        // resolved host so a checkout in an unusual place is honoured; falls back
        // to the project root only on the branch where nothing resolved and no
        // process is started anyway.
        private static string CliWorkingDirectory
            => ConversionCliLocator.WorkingDirectoryFor(CliProject) ?? ProjectRoot;

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

        // ── Scenes and prefabs the conversion could not reach ───────────────────

        // Assets Unity serialises as YAML and may invoke a member from by name.
        private static readonly string[] ScannedExtensions =
        {
            "*.unity", "*.prefab", "*.asset", "*.anim", "*.controller", "*.playable",
        };

        // A project can hold thousands of these. The cap keeps one press from
        // reading a gigabyte, and that it was hit is reported — a truncated scan
        // that reads as complete is the failure this whole feature exists about.
        private const int MaxScannedAssets = 4000;

        private ConversionYamlScan _sceneScan;
        private string _sceneScanSubject;
        private bool _sceneScanCapped;

        private void ScanScenesAndPrefabs()
        {
            var candidate = _candidates[_selected];
            var names = ConversionYamlScanner.NamesAtRisk(candidate.FullTypeName, candidate.MemberSpec);
            if (names.Count == 0)
            {
                _sceneScan = null;
                _sceneScanSubject = null;
                _previewNote = "name a member before scanning — there is nothing to look for yet";
                return;
            }

            var assets = new List<YamlAsset>();
            string root = Path.Combine(ProjectRoot, "Assets");
            _sceneScanCapped = false;

            if (Directory.Exists(root))
            {
                foreach (string pattern in ScannedExtensions)
                {
                    foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                    {
                        if (assets.Count == MaxScannedAssets)
                        {
                            _sceneScanCapped = true;
                            break;
                        }

                        // ⛔ A file that will not open is handed in with null text
                        // rather than skipped: the scan counts what it actually
                        // read, and quietly dropping one would inflate that number.
                        string text = null;
                        try
                        {
                            text = File.ReadAllText(file);
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                        {
                        }

                        assets.Add(new YamlAsset(ToProjectRelative(file), text));
                    }

                    if (_sceneScanCapped) break;
                }
            }

            _sceneScan = ConversionYamlScanner.Scan(assets, names);
            _sceneScanSubject = string.Join(", ", names);
        }

        private static string ToProjectRelative(string absolute)
            => absolute.StartsWith(ProjectRoot, StringComparison.Ordinal)
                ? absolute.Substring(ProjectRoot.Length + 1).Replace('\\', '/')
                : absolute;

        private void DrawSceneScan()
        {
            using (new EditorGUI.DisabledScope(_selected < 0))
            {
                if (GUILayout.Button("Scan scenes & prefabs for by-name references"))
                {
                    ScanScenesAndPrefabs();
                    GUIUtility.ExitGUI();
                }
            }

            if (_sceneScan == null) return;

            string account = ConversionYamlScanner.Describe(_sceneScan)
                + (_sceneScanCapped
                    ? " — stopped at " + MaxScannedAssets + " assets, so there may be more"
                    : string.Empty);

            EditorGUILayout.HelpBox(
                "Looked for: " + _sceneScanSubject + ".\n" + account,
                _sceneScan.Sightings.Count > 0 ? MessageType.Warning : MessageType.Info);

            foreach (var sighting in _sceneScan.Sightings)
            {
                EditorGUILayout.LabelField(
                    sighting.AssetPath + ":" + sighting.Line,
                    sighting.Key + ": " + sighting.Name);
            }
        }

        // ── GUI ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawDocumentationRow();

            EditorGUILayout.HelpBox(
                "Conversions are source-only. Prefab and scene YAML is invisible to the "
                + "diff below — after applying, re-wire the affected prefabs/scenes by hand. "
                + "A UnityEvent, an animation event or a SendMessage names a method as text and "
                + "does not go through a call site, so a converted method keeps running locally "
                + "and nothing reports it. Scan below to see which assets name what you converted.",
                MessageType.Warning);

            DrawSceneScan();

            // Resolved once for the whole frame.  IMGUI runs OnGUI twice per
            // frame — a layout pass then a repaint — and the two must emit the
            // same controls; re-reading the property per pass would let a path
            // typed into the field below change the branch between them, which
            // surfaces as a control-count mismatch rather than as the intended
            // switch.  A value edited here takes effect on the next frame.
            string host = CliProject;
            if (host == null)
            {
                EditorGUILayout.HelpBox(
                    ConversionCliLocator.UnavailableMessage(CliCandidates), MessageType.Error);
                DrawCliOverrideField();
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

        // 🔑 Drawn ahead of the host branch, not inside it. The branch below
        // returns early, and it is the one a reader most needs the page from:
        // "the conversion host was not found" is the message a package-only
        // install gets, and until this control existed the only route from it was
        // to already know which file to open.
        private static void DrawDocumentationRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                // The label carries the network requirement: this control opens the
                // rendered page in a browser rather than the copy inside the
                // package, and a reader with no connection needs to be told where
                // that copy is rather than left with a button that does nothing.
                var documentation = new GUIContent(
                    "Documentation",
                    "Opens the automation guide in your browser. Offline, the same page ships "
                    + "inside this package at Documentation~/" + DocumentationLinks.AutomationPage + ".");
                if (GUILayout.Button(documentation, GUILayout.Width(120)))
                {
                    Application.OpenURL(DocumentationLinks.Resolve(DocumentationLinks.AutomationPage));
                }
            }
        }

        // Lets an author name the checkout that holds the conversion host.  Shown
        // only while the host is unresolved, so the common case — working inside
        // the repository — never carries a path field it does not need.
        private void DrawCliOverrideField()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Conversion host location", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                string edited = EditorGUILayout.TextField(
                    new GUIContent(
                        "CLI project folder",
                        "Folder named " + ConversionCliLocator.CliProjectName
                        + " inside an RTMPE repository checkout."),
                    CliOverride);
                if (edited != CliOverride)
                {
                    CliOverride = edited;
                }

                if (GUILayout.Button("Browse…", GUILayout.Width(80)))
                {
                    string picked = EditorUtility.OpenFolderPanel(
                        "Locate " + ConversionCliLocator.CliProjectName, CliOverride, string.Empty);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        CliOverride = picked;
                    }

                    // A modal dialog consumes the event the current pass was
                    // drawing; abandoning the pass rather than finishing it
                    // against a stale event is the supported way back.
                    GUIUtility.ExitGUI();
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(CliOverride)))
                {
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    {
                        CliOverride = string.Empty;
                    }
                }
            }
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
                    "No candidate scripts. Score this project first:\n    "
                    + ReadinessArtifactData.RegenerateCommand(),
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

                GUILayout.Label(new GUIContent(ReadinessArtifactData.ShortName(candidate.FullTypeName), candidate.FullTypeName));
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
                    + " — score this project first:\n    " + ReadinessArtifactData.RegenerateCommand()
                    + "\nRun it from an RTMPE checkout, with the .NET 8 SDK on PATH.";
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
            string shortName = ReadinessArtifactData.ShortName(candidate.FullTypeName);
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
                        + "outside this project (regenerate it over your own scripts:\n    "
                        + ReadinessArtifactData.RegenerateCommand() + ")"
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
                DrawMemberField(_candidates[_selected]);
                DrawBatchStep();
            }
            else if (_kind == ConversionKind.EnhancedRpc)
            {
                string method = EditorGUILayout.TextField(
                    new GUIContent("Method", "the method to convert to an [RtmpeRpc]"),
                    _rpcMethod);
                if (method != _rpcMethod) { _rpcMethod = method; ClearPreview(); }
                int audience = EditorGUILayout.Popup(
                    // ⚠️ Not "never inferred". Leaving this on its first entry sends
                    // no target, and the host then REFUSES a method that mutates
                    // instance state and defaults one that does not — so the
                    // promise was false in exactly the state this popup now opens
                    // in. The tooltip says which of the two the author is in.
                    new GUIContent(
                        "Audience",
                        "yours to designate. Left unchosen, the host refuses a method that mutates"
                            + " instance state and defaults one that does not to Server (M3)"),
                    _rpcAudience, AudienceChoices);
                if (audience != _rpcAudience) { _rpcAudience = audience; ClearPreview(); }

                // The same element the argument vector will read, so the caution
                // on screen and the designation on the wire cannot describe
                // different audiences.
                string caveat = AudienceCaveat(AudienceChoices[_rpcAudience]);
                if (caveat != null)
                {
                    EditorGUILayout.HelpBox(caveat, MessageType.Warning);
                }
            }
        }

        private void DrawMemberField(Candidate candidate)
        {
            string member = EditorGUILayout.TextField(
                new GUIContent(
                    ReadinessArtifactData.ShortName(candidate.FullTypeName) + " member",
                    "field[:CompanionName] — the field to convert; you name it, the engine converts it"),
                candidate.MemberSpec);
            if (member != candidate.MemberSpec)
            {
                candidate.MemberSpec = member;
                ClearPreview();
            }
        }

        // The multi-select. Offered only for the NetworkVariable conversion,
        // because `convert-batch` is the only batch verb the engine exposes and
        // this is the conversion it exists for: the identity-allocating one,
        // where a run that stops half way has already spent wire ids on types
        // whose source did not land.
        //
        // ⚠️ Every participant must live in ONE directory, and the constraint is
        // enforced in BuildArgs rather than hinted at here. The engine's diff
        // labels files by name alone, and the whole preview→pin→snapshot→revert
        // chain resolves those names against the selected script's folder — so a
        // batch spanning folders would pin and restore the wrong paths. Refusing
        // is the honest answer; guessing a folder per name is not.
        private void DrawBatchStep()
        {
            var others = new List<Candidate>();
            foreach (var candidate in _candidates)
            {
                if (candidate.AssetPath != null && !ReferenceEquals(candidate, _candidates[_selected]))
                {
                    others.Add(candidate);
                }
            }

            if (others.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Convert more types in the same batch (one approval, all-or-nothing)",
                EditorStyles.miniBoldLabel);

            foreach (var candidate in others)
            {
                EditorGUILayout.BeginHorizontal();
                bool on = GUILayout.Toggle(candidate.InBatch, GUIContent.none, GUILayout.Width(18));
                if (on != candidate.InBatch)
                {
                    candidate.InBatch = on;
                    ClearPreview();
                }

                GUILayout.Label(
                    new GUIContent(ReadinessArtifactData.ShortName(candidate.FullTypeName), candidate.FullTypeName),
                    GUILayout.Width(180));
                using (new EditorGUI.DisabledScope(!candidate.InBatch))
                {
                    string member = EditorGUILayout.TextField(candidate.MemberSpec);
                    if (candidate.InBatch && member != candidate.MemberSpec)
                    {
                        candidate.MemberSpec = member;
                        ClearPreview();
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        // Every type this run converts, the selected one first — so the batch's
        // argument order, its diff order, and the order the human read them are
        // the same order.
        private List<Candidate> BatchParticipants()
        {
            var participants = new List<Candidate> { _candidates[_selected] };
            if (_kind != ConversionKind.NetworkVariable)
            {
                return participants;
            }

            foreach (var candidate in _candidates)
            {
                if (candidate.InBatch
                    && candidate.AssetPath != null
                    && !ReferenceEquals(candidate, _candidates[_selected]))
                {
                    participants.Add(candidate);
                }
            }

            return participants;
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
            //
            // Every participant, not just the selected one: in a batch the engine
            // reads N scripts, and a check covering one of them would leave the
            // rest exactly as unguarded as they were before the batch existed.
            var participants = BatchParticipants();
            var beforeRun = ReadParticipantSources(participants);
            if (beforeRun == null)
            {
                _lastError = "A selected script could not be read — check the file is present and "
                    + "not locked, then preview again.";
                return;
            }

            string sourceAbsolute = ToAbsolute(_candidates[_selected].AssetPath);

            if (!RunCli(args, out string stdout, out string error))
            {
                _lastError = error;
                return;
            }

            // Reading again closes the remaining gap: neither side alone can tell
            // a file that held still from one edited while the engine had it open.
            string moved = FirstParticipantThatChanged(participants, beforeRun);
            if (moved != null)
            {
                _lastError = moved + " changed while the engine was reading it — preview again.";
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
            //
            // One directory for the whole set, which is exactly what BuildBatchArgs
            // refuses a batch for not having: the diff labels carry no folder, so
            // this is the only directory they can be resolved against.
            _previewPins = Pin(Path.GetDirectoryName(sourceAbsolute), _previewFiles, out string pinFault);
            if (_previewPins == null)
            {
                // Discard the diff with the pins. Offering an approval this
                // window cannot hold to would be worse than offering none.
                ClearPreview();
                _lastError = pinFault + ", so the preview cannot be held to — preview again.";
            }
        }

        // Every participating script's bytes, in participant order, or null when
        // one of them could not be read.
        private static List<byte[]> ReadParticipantSources(List<Candidate> participants)
        {
            var bytes = new List<byte[]>(participants.Count);
            foreach (var participant in participants)
            {
                if (!TryRead(ToAbsolute(participant.AssetPath), out byte[] read))
                {
                    return null;
                }

                bytes.Add(read);
            }

            return bytes;
        }

        // A file's bytes, or false when the disk would not give them up.
        //
        // 🔑 Every caller here is asking a safety question — did this hold still,
        // is this what I am about to overwrite — and an exception thrown out of a
        // GUI callback answers none of them: the window is left mid-decision with
        // a stack trace in the console. Reading is fallible, so the callers treat
        // a failure as the unsafe answer rather than as no answer.
        private static bool TryRead(string path, out byte[] bytes)
        {
            try
            {
                bytes = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                || e is ArgumentException || e is NotSupportedException)
            {
                bytes = null;
                return false;
            }
        }

        // The first participating script that no longer holds the bytes it held
        // before the engine ran, or null when all of them do. A script that
        // cannot be re-read is reported as changed: what this answers is "did it
        // hold still", and an unreadable file has not shown that it did.
        //
        // ⚠️ Every participant, not just the selected one. The engine reads N
        // scripts in a batch, and a check covering one of them leaves the rest
        // exactly as unguarded as they were before the batch existed — while
        // reading as though the window checks its inputs.
        private static string FirstParticipantThatChanged(
            List<Candidate> participants, List<byte[]> before)
        {
            for (int i = 0; i < participants.Count && i < before.Count; i++)
            {
                if (!TryRead(ToAbsolute(participants[i].AssetPath), out byte[] now)
                    || !BytesEqual(before[i], now))
                {
                    return ReadinessArtifactData.ShortName(participants[i].FullTypeName);
                }
            }

            return null;
        }

        // A file the diff names, as it stands now. A file that does not exist is
        // pinned as absent rather than skipped: a ledger appearing between the
        // preview and the apply changes which ids the engine will find free, and
        // is exactly as much of a change as an edited one.
        // Returns null when a named file exists and will not be read: a pin
        // nobody could take is not a weaker guarantee, it is none at all, and
        // the approval it would sit under is the one that authorises a write.
        // `fault` names which of the two refusals happened, because "could not
        // be read" is untrue of a label that was never resolvable in the first
        // place, and a message that is untrue when it is said sends the author
        // to look at the wrong thing.
        private static List<Snapshot> Pin(string directory, List<string> fileNames, out string fault)
        {
            fault = null;
            var pins = new List<Snapshot>(fileNames.Count);
            foreach (string name in fileNames)
            {
                // 🔑 This is where a string from another process becomes a path
                // this window reads, overwrites, reimports and — for a file the
                // pin records as absent — DELETES. Every producer of a diff
                // label emits a bare file name today, and that guarantee lives
                // in four places none of which is here: `Path.GetFileName` at
                // three call sites and `VariableIdLedger.FileNameFor` at the
                // fourth. A label carrying a separator, a volume or a `..`
                // would make `Path.Combine` hand back a path outside the folder
                // the author approved — rooted, it discards the folder entirely.
                // The constraint is therefore stated where it is RELIED ON.
                //
                // ⛔ Refused by character rather than by `Path.IsPathRooted` or
                // `Path.GetFileName`: both answer differently on Windows and on
                // Linux, and a label the Editor refuses on one platform must not
                // be resolved on another.
                if (!IsABareFileName(name))
                {
                    fault = "the engine's diff names '" + name + "', which is not a plain file "
                        + "name in the script's own folder";
                    return null;
                }

                string path = Path.Combine(directory, name);
                bool exists = File.Exists(path);
                byte[] bytes = null;
                if (exists && !TryRead(path, out bytes))
                {
                    fault = "'" + name + "' is there and could not be read (check it is not "
                        + "locked, or checked out of version control)";
                    return null;
                }

                pins.Add(new Snapshot
                {
                    Path = path,
                    Existed = exists,
                    Bytes = bytes,
                });
            }

            return pins;
        }

        /// <summary>
        /// True when <paramref name="name"/> names a file and nothing else — no
        /// folder, no volume, no way up out of the directory it is resolved
        /// against.
        /// </summary>
        internal static bool IsABareFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
            {
                return false;
            }

            foreach (char c in name)
            {
                // ⛔ Both separators and the volume mark, on every platform. A
                // diff read on Linux may have been produced on Windows, and a
                // rule that admits `..\x` because this machine's separator is
                // `/` is a rule about the machine rather than about the label.
                if (c == '/' || c == '\\' || c == ':' || c < 0x20)
                {
                    return false;
                }
            }

            // ⛔ And the ENDS of the name, which is where Windows disagrees with
            // the bytes. The Win32 parser strips trailing dots and spaces from
            // the last component, so `Scripts\.. ` canonicalises to `Scripts\..`
            // — the parent folder — and `".. "` passes every character test
            // above, a space being 0x20 rather than below it. `Revert` deletes a
            // path its pin recorded as absent, so that is the folder holding the
            // author's scripts. A label must mean the same file on the machine
            // that wrote the diff and on the machine that reads it.
            return name == name.Trim() && name[name.Length - 1] != '.';
        }

        // The name of the first pinned file that no longer matches, or null when
        // every one of them still stands as previewed. Appearance and deletion
        // count as changes: the engine's next read would see a different world
        // either way.
        private string FirstMovedPin(out bool unreadable)
        {
            unreadable = false;
            if (_previewPins == null)
            {
                return "the preview";
            }

            foreach (var pin in _previewPins)
            {
                bool exists = File.Exists(pin.Path);
                if (exists != pin.Existed)
                {
                    return Path.GetFileName(pin.Path);
                }

                if (!exists)
                {
                    continue;
                }

                // Unreadable counts as not-still-standing, for the reason the
                // participant check gives: the question is whether it holds what
                // it held, and a file that will not be read has not said so. It
                // is reported apart from a changed one because the remedies
                // differ — one is "someone edited it", the other "something is
                // holding it" — and an author sent after the wrong one looks at
                // a file whose contents are exactly as they left them.
                if (!TryRead(pin.Path, out byte[] now))
                {
                    unreadable = true;
                    return Path.GetFileName(pin.Path);
                }

                if (!BytesEqual(pin.Bytes, now))
                {
                    return Path.GetFileName(pin.Path);
                }
            }

            return null;
        }

        // True while every file the previewed diff names still holds the bytes it
        // held when the diff was computed. If any of them has moved (an IDE save,
        // a git operation, a concurrent conversion), the engine would apply an
        // edit the human never saw: fail closed, name the file so the cause is
        // findable, and drop the diff — an approval this window cannot hold to is
        // worse than no approval at all.
        //
        // Stated here and asked from two points in Apply(), because a check
        // written out twice is two rules that agree until one of them is edited.
        private bool PreviewStillStands()
        {
            string moved = FirstMovedPin(out bool unreadable);
            if (moved == null)
            {
                return true;
            }

            // Clear first: ClearPreview() resets _lastError, so the message must
            // be set after it or the user never sees why.
            ClearPreview();
            _lastError = unreadable
                ? "'" + moved + "' could not be read, so the preview cannot be held to it — "
                    + "free the file and preview again."
                : "'" + moved + "' changed on disk after the preview — preview again.";
            return false;
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

            if (!PreviewStillStands())
            {
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

            // 🔑 Asked again on the far side of the dialog, and it is the same
            // question rather than a repeated one. The call above covers the
            // minutes between previewing and pressing Apply; a modal is a second
            // window of unbounded length — an IDE autosave, a branch switch, a
            // formatter on save do not wait for it to close — and everything
            // downstream from here treats the approval as describing the bytes on
            // disk: the snapshot Revert restores from is taken after this point,
            // and the engine recomputes its edit from a fresh read of its own.
            // Approving a diff and writing a different one is the one outcome
            // this window exists to make impossible.
            if (!PreviewStillStands())
            {
                return;
            }

            // Snapshot exactly the files the approved diff names, before writing.
            //
            // 🔑 The same function the preview pins with, not a second copy of
            // it: both answer "these files, as they stand now", and two spellings
            // of one question are two places for it to drift. It returns all or
            // nothing, which is the property that matters here — a half-taken
            // snapshot would let Revert restore the files it reached and leave
            // the rest converted, the mixed tree this window's undo exists to
            // make impossible.
            var snapshots = Pin(sourceDir, _previewFiles, out string snapshotFault);
            if (snapshots == null)
            {
                _lastError = snapshotFault + ", so this apply could not be undone — nothing "
                    + "was run.";
                return;
            }

            _lastApply.Clear();
            _lastApply.AddRange(snapshots);

            // The pinned preview arguments — never rebuilt from the UI fields.
            var args = new List<string>(_previewArgs) { "--apply" };
            if (!RunCli(args, out string stdout, out string error))
            {
                // Keep the snapshots: the engine is fail-closed, but if anything
                // was written in a partial window, Revert is the recovery path.
                _lastError = error;
                _lastApplySummary = ReadinessArtifactData.ShortName(candidate.FullTypeName) + " (failed apply — snapshots retained)";
                return;
            }

            foreach (var name in _previewFiles)
            {
                Reimport(Path.Combine(sourceDir, name));
            }

            _lastError = null;
            var applied = BatchParticipants();
            _lastApplySummary = (applied.Count > 1
                    ? applied.Count + " types (" + string.Join(", ", applied.ConvertAll(p => ReadinessArtifactData.ShortName(p.FullTypeName))) + ")"
                    : ReadinessArtifactData.ShortName(candidate.FullTypeName))
                + " — " + KindLabels[(int)_kind];
            _previewNote = "Applied. " + Outcome(stdout)
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

        // 🔑 Writing is fallible in every way reading is, and the stake is higher:
        // a restore that stops half way IS the mixed tree the snapshot set exists
        // to make impossible — some files back, the rest converted. So no fault
        // ends the sweep. Every snapshot is attempted, what failed is named, and
        // the undo state is kept whenever anything is still owed, because
        // clearing it here is what would turn a partial restore into a permanent
        // one.
        private void Revert()
        {
            var unrestored = new List<string>();
            foreach (var snapshot in _lastApply)
            {
                if (!TryRestore(snapshot))
                {
                    unrestored.Add(Path.GetFileName(snapshot.Path));
                }
            }

            // One refresh covers restored and deleted files alike — ImportAsset
            // on a deleted path would log a spurious error.
            AssetDatabase.Refresh();

            if (unrestored.Count > 0)
            {
                // Deliberately NOT cleared: what is still owed is exactly what a
                // second Revert must attempt once the author has freed the file,
                // and dropping it here would leave them a converted tree and no
                // way back to it.
                _lastError = "Reverted what could be reached. Still not restored: "
                    + string.Join(", ", unrestored)
                    + " — free the file (check it out of version control, or close what holds it)"
                    + " and press Revert again.";
                return;
            }

            _lastApply.Clear();
            _lastApplySummary = null;
            _lastError = null;
            _previewNote = "Reverted to the pre-apply snapshots.";
        }

        // One snapshot put back, or false when the disk refused it.
        private static bool TryRestore(Snapshot snapshot)
        {
            try
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

                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
        }

        // ── CLI plumbing ────────────────────────────────────────────────────────

        private List<string> BuildArgs(bool apply)
        {
            var candidate = _candidates[_selected];

            // 🔑 Refused here, where every other unusable input is refused, and
            // before any path is composed from it. The list toggle is disabled
            // for a candidate the scan could not resolve, so this is unreachable
            // through the window — but that is a property of a neighbouring rule,
            // and the cost of relying on it is an ArgumentNullException out of a
            // GUI callback rather than a sentence the author can act on.
            if (candidate.AssetPath == null)
            {
                _lastError = candidate.Problem
                    ?? "That type has no script this window can resolve — rescan, or convert it "
                        + "with the headless engine.";
                return null;
            }

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
                    var participants = BatchParticipants();
                    if (participants.Count > 1)
                    {
                        // One decision over N types. The single-type branch below
                        // is left exactly as it was rather than expressed through
                        // this one: `convert` and `convert-batch` are different
                        // verbs, and a developer converting one type must keep
                        // getting the command they have always got.
                        var batch = BuildBatchArgs(participants);
                        if (batch == null)
                        {
                            return null;
                        }

                        args.AddRange(batch);
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(candidate.MemberSpec))
                    {
                        _lastError = "Name the member to convert (field[:CompanionName]).";
                        return null;
                    }

                    args.AddRange(new[] { "--file", file, "--type", candidate.FullTypeName, "--member", candidate.MemberSpec.Trim() });
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
                        // No `:target` while the popup sits on its first entry:
                        // the host's own rule then decides, which for a
                        // state-mutating method is to refuse and say so.
                        "--method", _rpcAudience > 0
                            ? _rpcMethod.Trim() + ":" + AudienceChoices[_rpcAudience]
                            : _rpcMethod.Trim(),
                    });
                    break;
            }

            if (apply)
            {
                args.Add("--apply");
            }

            return args;
        }

        // The batch verb's argument vector, grouped by file: `--type` belongs to
        // the `--file` before it and `--member` to the `--type` before that. The
        // grammar is positional, so the grouping is not a formatting choice.
        //
        // Grouped rather than assumed one-type-per-file: two types can share a
        // file, and the verb refuses a `--file` named twice — correctly, because
        // both groups would be planned against the same original text. Today the
        // wizard's own resolver cannot produce that (a script is found only at
        // `<ShortName>.cs`), but a rule that holds only because of a neighbouring
        // rule is the one that breaks when the neighbour moves.
        private List<string> BuildBatchArgs(List<Candidate> participants)
        {
            string directory = null;
            foreach (var participant in participants)
            {
                if (string.IsNullOrWhiteSpace(participant.MemberSpec))
                {
                    _lastError = "Name the member to convert for "
                        + ReadinessArtifactData.ShortName(participant.FullTypeName) + " (field[:CompanionName]).";
                    return null;
                }

                string participantDirectory = Path.GetDirectoryName(ToAbsolute(participant.AssetPath));
                if (directory == null)
                {
                    directory = participantDirectory;
                }
                else if (!string.Equals(participantDirectory, directory, StringComparison.Ordinal))
                {
                    // See DrawBatchStep: the engine names diff files without their
                    // folder, and every pin, snapshot and revert path in this
                    // window resolves those names against one directory.
                    _lastError =
                        "A batch converts scripts from one folder. " + ReadinessArtifactData.ShortName(participant.FullTypeName)
                        + " is in a different folder from " + ReadinessArtifactData.ShortName(participants[0].FullTypeName)
                        + " — convert them in separate batches.";
                    return null;
                }
            }

            var byFile = new List<string>();
            var groups = new List<KeyValuePair<string, List<Candidate>>>();
            foreach (var participant in participants)
            {
                string file = ToAbsolute(participant.AssetPath);
                var group = groups.Find(g => string.Equals(g.Key, file, StringComparison.Ordinal));
                if (group.Value == null)
                {
                    group = new KeyValuePair<string, List<Candidate>>(file, new List<Candidate>());
                    groups.Add(group);
                }

                group.Value.Add(participant);
            }

            byFile.Add("convert-batch");
            foreach (var group in groups)
            {
                byFile.Add("--file");
                byFile.Add(group.Key);
                foreach (var participant in group.Value)
                {
                    byFile.Add("--type");
                    byFile.Add(participant.FullTypeName);
                    byFile.Add("--member");
                    byFile.Add(participant.MemberSpec.Trim());
                }
            }

            return byFile;
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
            string project = CliProject;
            if (project == null)
            {
                // Re-resolved on every access by design, so a checkout that moved
                // while the build ran reports as a missing engine rather than as
                // an exception out of the button that started it.
                problem = "the RTMPE checkout is no longer where it was when the build started.";
                return null;
            }

            string root = Path.Combine(project, "bin", BuildConfiguration);
            if (!Directory.Exists(root))
            {
                problem = "the engine built but produced no output under:\n" + root;
                return null;
            }

            string[] built;
            try
            {
                built = Directory.GetFiles(root, EngineAssemblyName, SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // An unreadable subdirectory under bin/, or the tree disappearing
                // between the check above and this walk. Either way the answer is
                // "which assembly runs is not knowable", which is a refusal.
                problem = "the built engine could not be located under:\n" + root + "\n\n" + e.Message;
                return null;
            }

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

        // Decoding, not display: a redirected stream is read with the PARENT
        // process's default encoding, and the editor's is not UTF-8 on every
        // platform the engine is driven from.
        //
        // 🔑 The stream this decodes is the approval artifact. What the author
        // approves is the text in the preview pane, so a byte read under the
        // wrong encoding is not a cosmetic fault here — it is consent given to
        // characters the engine never emitted, over a diff whose own source
        // lines may be non-ASCII.
        //
        // ⚠️ Both streams, because the failure path is where the reader is least
        // able to guess at a mangled word: stderr is what the error box renders.
        private static readonly UTF8Encoding EngineStreamEncoding = new UTF8Encoding(false);

        /// <summary>
        /// The launch description for one engine run: the executable, the
        /// directory it is launched from, its arguments, and the encoding its
        /// redirected streams are read under.
        /// </summary>
        internal static ProcessStartInfo BuildStartInfo(string workingDirectory, List<string> args)
        {
            var info = new ProcessStartInfo
            {
                // PATH first, so a machine that already resolves `dotnet` keeps
                // launching exactly what it launched before; the installer
                // locations are consulted only when PATH answers nothing, which
                // is the Finder-launched Unity this window is opened from on
                // macOS.  See DotnetExecutableLocator.
                FileName = DotnetExecutableLocator.Resolve(
                    Environment.GetEnvironmentVariable("PATH"),
                    Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    File.Exists),
                // Outside the folder the host sits in: the Tooling-scoped
                // global.json pins the exact SDK for byte-compared analyzer builds
                // only, and must not bind the developer's machine here.
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = EngineStreamEncoding,
                StandardErrorEncoding = EngineStreamEncoding,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            return info;
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

            var info = BuildStartInfo(CliWorkingDirectory, args);

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

        // What an apply amounts to: the engine's verdict, and anything it wanted
        // the author to know on the way there.
        //
        // ⚠️ The verdict alone is not enough. A host that could not compile-check
        // what it was about to write says so on a "note:" line and proceeds — and
        // that line sits above the verdict, so keeping only the last line reports
        // an unverified write in exactly the words used for a verified one. The
        // author is told once, at the moment the write happened.
        private static string Outcome(string stdout)
        {
            var lines = stdout.Trim().Split('\n');
            var notes = new List<string>();
            foreach (string line in lines)
            {
                if (line.StartsWith("note:", StringComparison.Ordinal))
                {
                    notes.Add(line.Trim());
                }
            }

            string verdict = lines[lines.Length - 1].Trim();
            return notes.Count == 0 ? verdict : verdict + "\n" + string.Join("\n", notes);
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
    }
}
#endif
