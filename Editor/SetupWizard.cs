// RTMPE SDK — Editor/SetupWizard.cs
//
// One-click setup wizard for integrating RTMPE into any Unity project.
// Opens automatically on first import or via: Window > RTMPE > Setup Wizard.
//
// Steps guided:
//  1. SDK import verification (assemblies + packages)
//  2. NetworkManager prefab placement in the scene
//  3. Connection Bootstrap — the entry flow, taken over by the SDK
//  4. API key & server configuration
//  5. Production — the key a RELEASE build will authenticate with
//  6. Game-Type defaults (max players, tick rate)
//  7. Configuration validation (the fields, not the network)

using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using RTMPE.Core;

namespace RTMPE.Editor
{
    /// <summary>
    /// Guided setup wizard shown on first SDK import or via the Window menu.
    /// </summary>
    public sealed class SetupWizard : EditorWindow
    {
        // ── State ─────────────────────────────────────────────────────────────

        // Serialized so the window stays on its step across a domain reload.
        // The production step writes a script into the project, which Unity
        // compiles and then reloads every assembly; a private field Unity does
        // not serialize came back as zero, so the reload sent the developer to
        // step 1 with the outcome of their press four clicks away.
        [SerializeField] private int _step;
        private const int TotalSteps = 7;

        private string _apiKey       = "";
        // What ApiKeyStore answered when this window opened. The field above is
        // an instruction to the vault only where the two differ — see
        // WizardApiKeyEdit, and SaveSettings() below, which is the only writer.
        private string _loadedApiKey = "";
        private string _gatewayHost  = "127.0.0.1";
        private int    _gatewayPort  = 7777;
        private int    _maxPlayers   = 16;
        private int    _tickRate     = 30;
        private string _sealKey      = "";
        private string _pinnedKey    = "";
        private string _jwtIssuer    = "";
        private string _jwtAudience  = "";

        // Serialized with _step below: the reload the production step causes
        // must not empty the bootstrap step's fields behind the developer's
        // back, and Cancel must still ask about edits made before it.
        [SerializeField] private string _roomName     = "";
        [SerializeField] private GameObject _playerPrefab;
        private bool   _bootstrapFound;
        // ⚠️ The bootstrap's own capacity, NOT the Game-Type step's. They were
        // one field, drawn on one screen as a 0-100 slider and on the other as
        // 1-100: choosing 0 ("let the server decide") on the bootstrap step was
        // silently clamped to 1 the moment the later step drew itself, and the
        // value written to the settings asset was the clamp.
        [SerializeField] private int    _bootstrapMaxPlayers;
        [SerializeField] private RoomEntryPolicy _bootstrapPolicy = RoomEntryPolicy.CreateRoom;
        [SerializeField] private string _bootstrapMode = "";

        private string _statusMsg    = "";
        private bool   _testPassed;
        private bool   _networkManagerFound;

        // Surface to the user when ApiKeyStore.Save() throws (OS keychain
        // quota, IPC failure with secret-tool, etc.).  Without this the
        // wizard would silently advance past the API-key step on a Save
        // failure and the developer would believe their key was persisted.
        private string _lastSaveError;

        // Set whenever the user has typed input in the current session;
        // gates the Cancel-confirmation dialog so a fresh open of the
        // wizard does not nag about discarding nothing.
        [SerializeField] private bool _hasUnsavedChanges;

        // ── Icons ─────────────────────────────────────────────────────────────

        // ── Entry points ──────────────────────────────────────────────────────

        [MenuItem("Window/RTMPE/Setup Wizard")]
        public static void Open()
        {
            var win = GetWindow<SetupWizard>(true, "RTMPE Setup Wizard", true);
            win.minSize = new Vector2(480, 420);
        }

        /// <summary>
        /// EditorPrefs key holding the persistent "don't auto-open the wizard"
        /// opt-out. Absent or false keeps auto-open enabled (the default); true
        /// suppresses it across Editor restarts, unlike the per-session
        /// SessionState guard which resets every launch.
        /// </summary>
        internal const string AutoOpenDisabledPrefKey = "RTMPE_Wizard_AutoOpenDisabled";

        /// <summary>
        /// <see cref="AutoOpenDisabledPrefKey"/> as it is actually stored.
        /// </summary>
        /// <remarks>
        /// ⛔ Scoped like everything else this wizard keeps. "I have set this up
        /// and do not want the wizard again" is a statement about a PROJECT, and
        /// under the bare name a developer who dismissed it once never saw it
        /// again in any project they opened afterwards.
        /// </remarks>
        internal static string AutoOpenDisabledPref => Pref(AutoOpenDisabledPrefKey);

        /// <summary>
        /// Auto-open on first SDK import. A persistent opt-out (Window menu)
        /// takes precedence over the per-session guard so a developer who turns
        /// auto-open off is never prompted again.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void AutoOpen()
        {
            if (EditorPrefs.GetBool(AutoOpenDisabledPref, false))
                return;

            if (!SessionState.GetBool("RTMPE_WizardShown", false))
            {
                SessionState.SetBool("RTMPE_WizardShown", true);
                // Delay so Editor finishes loading before opening.
                EditorApplication.delayCall += () =>
                {
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                        Open();
                };
            }
        }

        /// <summary>
        /// Flips the persistent auto-open opt-out. Wired to the Window menu (with
        /// a checkmark reflecting the current state) and covered by the Editor
        /// tests.
        /// </summary>
        [MenuItem("Window/RTMPE/Auto-Open Setup Wizard")]
        internal static void ToggleAutoOpen()
        {
            EditorPrefs.SetBool(AutoOpenDisabledPref,
                !EditorPrefs.GetBool(AutoOpenDisabledPref, false));
        }

        [MenuItem("Window/RTMPE/Auto-Open Setup Wizard", true)]
        private static bool ToggleAutoOpenValidate()
        {
            Menu.SetChecked("Window/RTMPE/Auto-Open Setup Wizard",
                !EditorPrefs.GetBool(AutoOpenDisabledPref, false));
            return true;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            LoadSettings();
            CheckNetworkManager();
            // Looked for again the next time the production step is drawn:
            // OnEnable runs after every domain reload, and the reload that
            // matters here is the one that compiles the script that step wrote.
            _providerScanTaken = false;
            // The placement is a question about the open scene, so it is asked
            // again whenever the hierarchy changes rather than on every repaint.
            EditorApplication.hierarchyChanged -= InvalidateProviderPlacement;
            EditorApplication.hierarchyChanged += InvalidateProviderPlacement;
            InvalidateProviderPlacement();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= InvalidateProviderPlacement;
        }

        // OnDestroy fires whether the wizard closes via the Cancel button (which
        // already prompts via TryCancel) or via Unity's window-chrome X button
        // (which bypasses the explicit Cancel path).  By the time OnDestroy
        // runs the window has already been retired, so a confirmation dialog
        // is too late — instead, surface a console warning so an integrator
        // who closes the X with unsaved edits has an unmistakable trace in
        // the editor log.  TryCancel clears _hasUnsavedChanges before scheduling
        // Close, so this branch only fires when the user dismissed the wizard
        // without going through the Cancel button.
        private void OnDestroy()
        {
            if (_hasUnsavedChanges)
            {
                Debug.LogWarning(
                    "[RTMPE] SetupWizard closed with unsaved changes; setup is incomplete. " +
                    "Reopen via Window > RTMPE > Setup Wizard to finish.");
            }
        }

        // ── GUI ───────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();
            GUILayout.Space(8);

            switch (_step)
            {
                case 0: DrawStepVerify();    break;
                case 1: DrawStepPrefab();    break;
                case 2: DrawStepBootstrap(); break;
                case 3: DrawStepApiKey();    break;
                case 4: DrawStepProduction(); break;
                case 5: DrawStepGameType();  break;
                case 6: DrawStepTestConn();  break;
            }

            GUILayout.FlexibleSpace();
            DrawFooter();
        }

        // ── Step renderers ────────────────────────────────────────────────────

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"RTMPE SDK — Step {_step + 1} / {TotalSteps}",
                    EditorStyles.boldLabel);
            }
        }

        private void DrawStepVerify()
        {
            EditorGUILayout.HelpBox(
                "✅  SDK assemblies are loaded correctly.\n" +
                "Bundled: FlatBuffers (vendored, Apache 2.0).",
                MessageType.Info);
        }

        private void DrawStepPrefab()
        {
            EditorGUILayout.HelpBox(
                _networkManagerFound
                    ? "✅  NetworkManager found in the active scene."
                    : "⚠️  No NetworkManager found. Click below to add one.",
                _networkManagerFound ? MessageType.Info : MessageType.Warning);

            if (!_networkManagerFound && GUILayout.Button("Add NetworkManager to Scene"))
                AddNetworkManagerToScene();
        }

        // The step that removes the entry flow from the developer's hands.
        //
        // ⛔ Offered, never imposed: a title with a lobby screen, a character
        // selector or a queue of its own drives the flow itself, and this step
        // says so rather than presenting the component as a requirement. It is
        // three fields because the component is three fields — anything else it
        // can do is a delegate an application sets from code.
        private void DrawStepBootstrap()
        {
            EditorGUILayout.HelpBox(
                _bootstrapFound
                    ? "✅  A Connection Bootstrap is in the scene. It connects, enters a room " +
                      "and spawns your player — with no code."
                    : "Optional.  Add the Connection Bootstrap and the SDK performs the entry " +
                      "flow for you: connect, enter a room, spawn the local player, and re-enter " +
                      "that room after a drop.  Leave it out to drive all of that yourself.",
                MessageType.Info);

            _playerPrefab = (GameObject)EditorGUILayout.ObjectField(
                PlayerPrefabLabel, _playerPrefab, typeof(GameObject), false);

            // ⛔ The policy is offered, because it is the field that decides what
            // the other three mean — and a step that set a room name while the
            // component was matchmaking wrote a value nothing sends.
            _bootstrapPolicy = (RoomEntryPolicy)EditorGUILayout.EnumPopup(
                EntryPolicyLabel, _bootstrapPolicy);

            if (_bootstrapPolicy == RoomEntryPolicy.CreateRoom)
                _roomName = EditorGUILayout.TextField(RoomNameLabel, _roomName);

            if (_bootstrapPolicy == RoomEntryPolicy.Matchmaking)
                _bootstrapMode = EditorGUILayout.TextField(MatchmakingModeLabel, _bootstrapMode);

            _bootstrapMaxPlayers = EditorGUILayout.IntSlider(
                BootstrapMaxPlayersLabel, _bootstrapMaxPlayers, 0, 100);

            // The SDK refuses an empty mode by throwing, so the step says so
            // here rather than letting Play be where the reader finds out.
            if (_bootstrapPolicy == RoomEntryPolicy.Matchmaking
                && string.IsNullOrEmpty(_bootstrapMode))
            {
                EditorGUILayout.HelpBox(
                    "Matchmaking needs a mode — players are matched with others asking for the "
                    + "same one. Type any short name, or choose CreateRoom, which needs none.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!_networkManagerFound))
            {
                if (GUILayout.Button(_bootstrapFound
                        ? "Apply these to the Connection Bootstrap"
                        : "Add Connection Bootstrap to Scene"))
                {
                    AddBootstrapToScene();
                }
            }

            if (!_networkManagerFound)
            {
                EditorGUILayout.HelpBox(
                    "Add a NetworkManager on the previous step first — the bootstrap drives one.",
                    MessageType.Warning);
            }
        }

        private static readonly GUIContent PlayerPrefabLabel =
            new GUIContent("Player Prefab",
                "Spawned for this client on entering a room.  It must be listed in " +
                "Window → RTMPE → Network Prefabs, which is where its id comes from.");

        private static readonly GUIContent RoomNameLabel =
            new GUIContent("Room Name",
                "Used when the bootstrap opens a room.  Empty lets the server choose one.");

        private static readonly GUIContent BootstrapMaxPlayersLabel =
            new GUIContent("Max Players",
                "0 lets the server choose; otherwise 1-100.");

        private static readonly GUIContent EntryPolicyLabel =
            new GUIContent("Entry Policy",
                "How the bootstrap gets into a room: open one, join one by id, or ask the " +
                "server to match this player with others.");

        private static readonly GUIContent MatchmakingModeLabel =
            new GUIContent("Matchmaking Mode",
                "Players are matched with others asking for the same mode.  Required: the SDK " +
                "refuses an empty one.");

        // ⚠️ These strings are the DASHBOARD's, not this window's own. A developer
        // reads a value on one screen and types it into the other, and this
        // window used to name three of them differently — "Gateway Host" and
        // "Gateway Port" for the dashboard's "Server Host" and "Server Port",
        // and "API-Key Seal Public Key (X25519)" for what the dashboard and the
        // documentation both call the "Sealed-Box Public Key (X25519)". A tester
        // reported it as confusing configuration, which is what it was.
        //
        // The labels of the gateway step, declared once because they are both
        // DRAWN and MEASURED.  Built inline they were two lists — the width
        // would have been computed from one set of strings and the column filled
        // with another — and they allocated a GUIContent per field per repaint.
        private static readonly GUIContent ApiKeyLabel =
            new GUIContent("API Key",
                "Issued in the dashboard.  Stored in the OS credential vault, never in the " +
                "scene or the settings asset.  The Editor is the only thing that reads that " +
                "vault, so a player build needs a source of its own, consulted in this order: " +
                "ApiKeySource.SetProvider, --rtmpe-api-key-file <path>, then the RTMPE_API_KEY " +
                "environment variable.");

        private static readonly GUIContent ServerHostLabel =
            new GUIContent("Server Host",
                "The server's host name or address, from the dashboard's connection settings.");

        private static readonly GUIContent ServerPortLabel =
            new GUIContent("Server Port",
                "The server's UDP port, from the dashboard's connection settings.");

        private static readonly GUIContent SealKeyLabel =
            new GUIContent("Sealed-Box Public Key (X25519)",
                "64-character hex X25519 key from the dashboard.  Required: the API key " +
                "is sealed to it before it is sent, and the server accepts it in no other form.");

        private static readonly GUIContent PinnedKeyLabel =
            new GUIContent("Pinned Server Public Key (Ed25519)",
                "64-character hex Ed25519 key from the dashboard.  Required while Server " +
                "Pinning Mode is Strict (the default).");

        private static readonly GUIContent JwtIssuerLabel =
            new GUIContent("Session Token Issuer (JWT iss)",
                "The issuer (iss claim) the session token must carry, from the dashboard.  " +
                "A token with a different issuer is rejected and the session is closed.  " +
                "Leave blank to keep the value already on the NetworkSettings asset, which " +
                "defaults to the RTMPE server's issuer — blank here does not turn the check " +
                "off.  Fill it in only when the dashboard shows a different issuer.  " +
                "Disabling the check means clearing the field on the asset, which accepts " +
                "a token from any issuer and is not a production setting.");

        private static readonly GUIContent JwtAudienceLabel =
            new GUIContent("Session Token Audience (JWT aud)",
                "The audience (aud claim) the session token must carry, from the dashboard.  " +
                "A token whose audience excludes this value is rejected.  Same handling as " +
                "the issuer above: blank keeps the asset's value, which defaults to the " +
                "RTMPE server's audience, and does not skip the check.");

        private static readonly GUIContent[] GatewayStepLabels =
        {
            ApiKeyLabel, ServerHostLabel, ServerPortLabel, SealKeyLabel,
            PinnedKeyLabel, JwtIssuerLabel, JwtAudienceLabel,
        };

        private void DrawStepApiKey()
        {
            EditorGUILayout.LabelField("Gateway Configuration", EditorStyles.boldLabel);

            // Unity's label column is a fixed width that does not grow with the
            // window, so a label longer than it is clipped with no ellipsis:
            // four of the seven below arrived at a tester cut mid-word, on a
            // window with room to spare.  The column is measured from the labels
            // themselves rather than set to a literal, and restored afterwards —
            // labelWidth is global editor state, and a window that leaves it set
            // re-lays every inspector drawn after it.
            var measured = new float[GatewayStepLabels.Length];
            for (int i = 0; i < GatewayStepLabels.Length; i++)
            {
                measured[i] = EditorStyles.label.CalcSize(GatewayStepLabels[i]).x;
            }

            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = WizardLabelLayout.LabelWidth(
                WizardLabelLayout.Widest(measured), EditorGUIUtility.currentViewWidth);
            try
            {
                // Render the API key as a masked password field. The on-disk
                // store is the OS credential vault via ApiKeyStore — see
                // SaveSettings(). Masking the GUI prevents shoulder-surfing /
                // screen-share leaks while the wizard is open.
                EditorGUI.BeginChangeCheck();
                _apiKey      = EditorGUILayout.PasswordField(ApiKeyLabel,     _apiKey);
                _gatewayHost = EditorGUILayout.TextField(ServerHostLabel,    _gatewayHost);
                _gatewayPort = EditorGUILayout.IntField(ServerPortLabel,     _gatewayPort);
                // Dashboard public keys — non-secret, hence plain text fields (only the
                // API key above is a secret and stays masked).  The seal key is what the
                // API key is sealed to; the pin is what Strict pinning compares the
                // gateway's identity key against.
                _sealKey     = EditorGUILayout.TextField(SealKeyLabel,        _sealKey);
                _pinnedKey   = EditorGUILayout.TextField(PinnedKeyLabel,      _pinnedKey);
                // Session-token claims — non-secret, copied from the dashboard.  Both
                // checks are already ON: NetworkSettings ships the canonical claims as
                // its defaults, and a blank field here writes nothing rather than
                // clearing them.  These fields are for a gateway that mints something
                // else, which is the only case where the asset's default is wrong.
                _jwtIssuer   = EditorGUILayout.TextField(JwtIssuerLabel,      _jwtIssuer);
                _jwtAudience = EditorGUILayout.TextField(JwtAudienceLabel,    _jwtAudience);
                if (EditorGUI.EndChangeCheck())
                    _hasUnsavedChanges = true;
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }

            // ⚠️ Said out loud because the meaning of the blank box changed.
            // It used to clear the stored key; it now says nothing to the vault,
            // which is what keeps a key safe when the store could not be read —
            // and that same silence would otherwise leave a developer believing
            // the wizard had shown them everything it holds.
            if (WizardApiKeyEdit.SaysNothingWasStored(_loadedApiKey, _apiKey))
                EditorGUILayout.HelpBox(
                    "This project's credential store did not return an API key. Leaving this "
                    + "blank changes nothing — paste a key to store one. If you expected a key "
                    + "here, check the Console: the store may have been unreadable rather than "
                    + "empty, and this window will not clear what it could not read.",
                    MessageType.Info);

            // Surface any prior Save() failure right next to the input that
            // caused it so the developer knows credential persistence failed.
            if (!string.IsNullOrEmpty(_lastSaveError))
                EditorGUILayout.HelpBox(_lastSaveError, MessageType.Error);

            DrawDevelopmentBuildStaging();
        }

        // ── The key a build can carry ────────────────────────────────────────
        //
        // 🔑 Drawn in the credential step because that is where a developer is
        // when the question arises: the vault above is the Editor's, and a
        // player built from a fully configured project still has no key. Of the
        // sources that remain, a double-clicked build inherits no argument vector
        // at all and takes its environment from the desktop session rather than
        // the shell the Editor was started from, so without this testing a
        // standalone build meant writing code or setting a variable for the
        // whole machine.
        //
        // ⛔ A switch, not a write. Nothing is put in the project here: a
        // development build injects the key while it runs and removes it when it
        // finishes, so there is no file to commit and none for a later release
        // build to pick up. What is stored is the developer's answer, in this
        // machine's EditorPrefs.
        private void DrawDevelopmentBuildStaging()
        {
            EditorGUILayout.Space();

            // Pref(), the same project-scoped helper every other setting here
            // uses: a machine runs more than one RTMPE project, and a key that
            // carried none would let the last one written decide for all of them.
            string preference = Pref(DevelopmentApiKeyStaging.OptInPreference);
            bool optedIn = EditorPrefs.GetBool(preference, false);
            bool hasKey  = !string.IsNullOrWhiteSpace(_apiKey)
                           || !string.IsNullOrWhiteSpace(_loadedApiKey);

            // The target the Editor is switched to, which is the one a build
            // pressed from here is made for.  The build hook reads its own
            // report rather than this, because a build can be issued for
            // another target entirely.
            string platform = EditorUserBuildSettings.activeBuildTarget.ToString();

            // ⛔ A residue is drawn here as well as reported to the console, and
            // it is drawn FIRST: the console line is written on a domain reload,
            // which the developer standing in this window may have scrolled past
            // hours ago, and a release build — or a development build for a
            // target that cannot read the file — refuses to run while it is
            // there.
            //
            // Asked again at most once a second rather than cached for the
            // panel's life: a build that failed a minute ago is exactly the
            // case, so an answer taken once at OnEnable would tell a developer
            // their project is clean while a credential sits in it — and an
            // answer taken on every repaint is a file stat many times a second
            // for a question whose answer changes on a build.
            if (ResidueIsInTheProject())
            {
                EditorGUILayout.HelpBox(DevelopmentApiKeyStaging.ResidueReport(), MessageType.Error);
            }

            EditorGUILayout.HelpBox(
                DevelopmentApiKeyStaging.Status(
                    hasKey, optedIn, RTMPE.Core.DevelopmentApiKeyFile.PlatformReadsTheFile(platform), platform),
                optedIn ? MessageType.Warning : MessageType.Info);

            using (new EditorGUI.DisabledScope(!hasKey && !optedIn))
            {
                bool now = EditorGUILayout.ToggleLeft(
                    DevelopmentApiKeyStaging.StageButton, optedIn);

                if (now != optedIn)
                {
                    EditorPrefs.SetBool(preference, now);
                    if (now) KeepStagedKeyOutOfVersionControl();
                }
            }

            // ⛔ Shown whenever the switch is on, not only in the frame it was
            // flipped: an IMGUI window redraws constantly and a message written
            // once is a message nobody reads. The three outcomes that leave the
            // key unignored are the ones a developer has to act on, and they
            // are the ones a silent return used to hide.
            if (optedIn && _versionControlStatus != null)
            {
                EditorGUILayout.HelpBox(
                    _versionControlStatus,
                    _versionControlSettled
                        ? MessageType.Info
                        : MessageType.Warning);
            }
        }

        // What the last attempt to keep the staged key out of version control
        // did, and whether it settled the question. Held rather than recomputed
        // per frame: the answer involves walking to the repository root and
        // reading a file, and OnGUI runs many times a second.
        private string _versionControlStatus;
        private bool   _versionControlSettled;

        // ⛔ Asked when the switch goes on, of the repository this project is
        // actually in. A build that fails between injection and clean-up leaves
        // the key in Assets/, and one `git add -A` after that puts a credential
        // in a history that outlives its deletion — so the line is written, or
        // the developer is told why it was not. Asking them to remember it and
        // stopping there would put the burden in the one place the rest of this
        // file refuses to put it.
        //
        // 🚨 This used to look for a .gitignore at the Unity project's own root
        // and return in silence when it found none. A repository laid out
        // `repo/Game/Assets` keeps its ignore file one directory higher, so the
        // ordinary case wrote nothing and said nothing, under a wizard sentence
        // claiming the project held no credential between builds.
        private void KeepStagedKeyOutOfVersionControl()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot)) return;

            // ⛔ The repository's root, walked to, never assumed to be the Unity
            // project's own. A project laid out `repo/Game/Assets` is the common
            // case this used to miss entirely, and missing it silently is the
            // whole of the exposure: the line is not written, nothing says so,
            // and one `git add -A` after a failed build commits an API key.
            DevelopmentApiKeyStaging.VersionControl outcome =
                DevelopmentApiKeyStaging.KeepOutOfVersionControl(projectRoot);

            _versionControlStatus   = DevelopmentApiKeyStaging.VersionControlStatus(outcome);
            _versionControlSettled  =
                outcome == DevelopmentApiKeyStaging.VersionControl.AlreadyIgnored
                || outcome == DevelopmentApiKeyStaging.VersionControl.LineAdded
                || outcome == DevelopmentApiKeyStaging.VersionControl.IgnoreFileWritten;

            // ⛔ And it is said in the console as well as in the window: a
            // developer who ticks the box and closes the wizard has still been
            // told, and the two outcomes that leave the file unignored are the
            // ones worth interrupting for.
            if (_versionControlSettled) Debug.Log("[RTMPE] " + _versionControlStatus);
            else                        Debug.LogWarning("[RTMPE] " + _versionControlStatus);
        }

        // ── The key a RELEASE build will have ────────────────────────────────
        //
        // 🔑 Asked here, before any build, because the previous step ends with
        // Play mode working and nothing on it says that a build will not: the
        // vault is the Editor's, and a release build carries no key of its own.
        // ReleaseCredentialPathAdvisory says so at build time; this step says it
        // where the source can still be chosen, offers the three a player can
        // read, and writes the one that reaches a player who double-clicked the
        // game.  The decisions and the wording are ReleaseCredentialSetup's,
        // where a test project executes them.
        //
        // ⛔ The scan is the BUILD's scan — ReleaseCredentialPathAdvisory's one
        // walk — so this step and the build hook read one project the same way.
        // Its answers are held rather than recomputed: the walk reads every
        // script under Assets/, the type lookup asks every loaded assembly, the
        // placement asks the open scene, and OnGUI runs many times a second.

        // What the last scan found: the scripts that register (sorted), how
        // many it could not read, whether it could look; where the wizard's
        // own script is (at its path, or moved — the name is what Unity binds
        // a MonoBehaviour to); the compiled type, which only a reload can
        // change; and whether all of that has been taken since the window was
        // enabled.  Non-serialized on purpose — a reload is exactly when the
        // answers have to be taken again.
        private string[] _providerRegistrations = new string[0];
        // …and the subset a player build compiles, which is the one the step's
        // claim is about: an Editor-only registration runs in Play mode and is
        // absent from the build the step is describing.
        private string[] _providerRegistrationsInPlayers = new string[0];
        private int      _providerUnreadable;
        private bool     _providerScanFailed;
        private string   _providerScriptPath;
        private Type     _compiledProviderType;
        private bool     _providerScanTaken;

        // Where the component stands in the open scene, held until the
        // hierarchy changes or a press moves it.
        private ReleaseCredentialSetup.Placement _placement;
        private string   _placementCarrier;
        private bool     _placementOnAPrefabInstance;
        private bool     _placementTaken;

        // What the last press of the provider button did, shown until the next
        // press or the reload that compiles what it wrote — after which the
        // placement below is the status.
        private string _providerWriteStatus;
        private bool   _providerWriteSettled;

        private void DrawStepProduction()
        {
            EditorGUILayout.LabelField("The key a release build will have", EditorStyles.boldLabel);

            if (!_providerScanTaken) ScanForProviderRegistration();

            EditorGUILayout.HelpBox(
                ReleaseCredentialSetup.Status(
                    _providerRegistrations, _providerRegistrationsInPlayers,
                    _providerUnreadable, _providerScanFailed),
                _providerRegistrationsInPlayers.Length > 0 && !_providerScanFailed
                    ? MessageType.Info
                    : MessageType.Warning);

            // The three sources, in the order the runtime consults them — the
            // getting-started guide's own table, so the step and the page a
            // developer reads next rank them the same way.
            foreach (ReleaseCredentialSetup.Source source in ReleaseCredentialSetup.Sources)
            {
                EditorGUILayout.LabelField(source.Name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    source.Spelling + " — " + source.Use, EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.Space();
            DrawProviderScript();

            // ⛔ Only while the previous step's switch is on: that is the one
            // case in which a development build carries the key by design, and
            // this step is where "I will just send the tester a development
            // build" is decided.
            if (EditorPrefs.GetBool(Pref(DevelopmentApiKeyStaging.OptInPreference), false))
            {
                EditorGUILayout.HelpBox(
                    ReleaseCredentialSetup.DevelopmentBuildHandedOverWarning(),
                    MessageType.Warning);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-check"))
                    ScanForProviderRegistration();

                // The section of the guide this step is the short form of:
                // the online copy, which opens in a browser on every machine.
                if (GUILayout.Button("Open the guide"))
                {
                    Application.OpenURL(DocumentationLinks.Resolve(
                        DocumentationLinks.GettingStartedPage,
                        DocumentationLinks.PlayerCredentialSection));
                }
            }
        }

        // The provider script: written on a press, then — once Unity has
        // compiled it — put on the object carrying the NetworkManager on a
        // second press, because the type does not exist until the reload that
        // follows the write, and a component cannot be added before its type.
        private void DrawProviderScript()
        {
            string script   = _providerScriptPath;
            Type   compiled = _compiledProviderType;

            // The SDK keeps one registration: every script that makes one and
            // is not the wizard's own is said — before the write, and after it,
            // since the write is what makes two.
            string[] others = RegistrationsOtherThan(script);
            if (others.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    ReleaseCredentialSetup.AnotherRegistrationNote(others), MessageType.Warning);
            }

            if (script == null)
            {
                // ⛔ A name already taken is known before any press — the same
                // lookup the write would refuse on — so the refusal is drawn in
                // the button's place rather than discovered by pressing it.
                // Not when the scan could not look: "compiled from somewhere
                // other than a script under Assets/" is then a claim about a
                // folder nothing here could list, and the press answers for
                // itself (CouldNotWrite).
                if (compiled != null && !_providerScanFailed)
                {
                    EditorGUILayout.HelpBox(
                        ReleaseCredentialSetup.WriteStatus(ReleaseCredentialSetup.Write.NameTaken),
                        MessageType.Warning);
                    return;
                }

                if (GUILayout.Button("Create " + ReleaseCredentialSetup.ProjectPath))
                    WriteProviderScript(compiled != null);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(script + " is in the project.", EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("Open", GUILayout.Width(60)))
                    {
                        // Null until the import after a write has run; a press
                        // in that window opens nothing rather than erring.
                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(script);
                        if (asset != null) AssetDatabase.OpenAsset(asset);
                    }
                }

                // The scan has the fact in hand: a script of the wizard's name
                // whose code no longer makes the registration is said as that,
                // and no placement is drawn for a component that registers
                // nothing — a ✅ about where it sits would be false.
                if (!_providerScanFailed && Array.IndexOf(_providerRegistrations, script) < 0)
                {
                    EditorGUILayout.HelpBox(
                        ReleaseCredentialSetup.ScriptRegistersNothingNote(script), MessageType.Warning);
                }
                else
                {
                    DrawProviderPlacement(compiled);
                }
            }

            if (!string.IsNullOrEmpty(_providerWriteStatus))
            {
                EditorGUILayout.HelpBox(
                    _providerWriteStatus,
                    _providerWriteSettled ? MessageType.Info : MessageType.Warning);
            }
        }

        // Whether a release build compiles the wizard's own provider script —
        // the fact the placement's ✅ ends by promising something about.
        //
        // ⛔ Read off the scan's PLAYER list rather than from the file's
        // existence: the script is the developer's the moment it is written,
        // and moving it under an Editor folder, into an editor-only assembly
        // definition, or wrapping its Awake in #if UNITY_EDITOR takes it out of
        // every build while leaving it exactly where the placement looks. A
        // scan that could not finish answers "yes": the step's top box says it
        // could not look, and a second warning about a build it did not read
        // would be a fault of the step's own.
        private bool ScriptIsCompiledIntoPlayers()
            => _providerScanFailed
               || string.IsNullOrEmpty(_providerScriptPath)
               || Array.IndexOf(_providerRegistrationsInPlayers, _providerScriptPath) >= 0;

        // Where the written component stands: compiling, not compiled, not a
        // component any more, no manager to carry it, on the manager, on
        // another object, or on none — each said in ReleaseCredentialSetup's
        // words, and the last one with the button that settles it.
        private void DrawProviderPlacement(Type compiled)
        {
            if (compiled == null)
            {
                EditorGUILayout.HelpBox(
                    ReleaseCredentialSetup.PlacementStatus(
                        EditorApplication.isCompiling
                            ? ReleaseCredentialSetup.Placement.Compiling
                            : ReleaseCredentialSetup.Placement.NotCompiled,
                        ScriptIsCompiledIntoPlayers()),
                    MessageType.Info);
                return;
            }

            if (!_placementTaken) TakeProviderPlacement(compiled);

            bool settled = _placement == ReleaseCredentialSetup.Placement.OnManager
                           || _placement == ReleaseCredentialSetup.Placement.OnAnotherObject;
            EditorGUILayout.HelpBox(
                ReleaseCredentialSetup.PlacementStatus(
                    _placement, ScriptIsCompiledIntoPlayers(), _placementCarrier)
                + (_placementOnAPrefabInstance ? " " + ReleaseCredentialSetup.PrefabInstanceNote : string.Empty),
                settled ? MessageType.Info : MessageType.Warning);

            if (_placement != ReleaseCredentialSetup.Placement.NotOnManager) return;

            // ⛔ Not in Play mode: a component added then runs its Awake and
            // vanishes when play stops, with the scene never marked dirty.
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Add " + ReleaseCredentialSetup.ClassName + " to the NetworkManager"))
                {
                    var manager = FindFirstObjectByType<NetworkManager>();
                    if (manager != null)
                    {
                        Undo.AddComponent(manager.gameObject, compiled);
                        // The manager's own scene: under multi-scene editing the
                        // active scene may be another one.
                        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                    }

                    InvalidateProviderPlacement();
                    Repaint();
                }
            }

            if (EditorApplication.isPlaying)
            {
                EditorGUILayout.LabelField(
                    "Leave Play mode to add it — a component added while playing does not outlive the session.",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        // The placement, asked of the open scene once per change of it.
        //
        // ⛔ The Component question first: Unity answers GetComponent and
        // AddComponent for a type that is not a Component with an error in the
        // Console, and the file is the developer's to edit into anything.
        private void TakeProviderPlacement(Type compiled)
        {
            _placementCarrier = null;
            _placementOnAPrefabInstance = false;

            if (!typeof(Component).IsAssignableFrom(compiled))
            {
                _placement = ReleaseCredentialSetup.Placement.NotAComponent;
            }
            else
            {
                var manager = FindFirstObjectByType<NetworkManager>();
                if (manager == null)
                {
                    _placement = ReleaseCredentialSetup.Placement.NoManager;
                }
                else if (manager.GetComponent(compiled) != null)
                {
                    _placement = ReleaseCredentialSetup.Placement.OnManager;
                    _placementOnAPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(manager);
                }
                else
                {
                    // On another active object is the developer's own placement,
                    // which the guide allows — said as such, and no second copy
                    // is offered.
                    var elsewhere = FindFirstObjectByType(compiled) as Component;
                    if (elsewhere != null)
                    {
                        _placement = ReleaseCredentialSetup.Placement.OnAnotherObject;
                        _placementCarrier = elsewhere.gameObject.name;
                    }
                    else
                    {
                        _placement = ReleaseCredentialSetup.Placement.NotOnManager;
                    }
                }
            }

            _placementTaken = true;
        }

        private void InvalidateProviderPlacement()
        {
            _placementTaken = false;
        }

        // The scripts that register a provider other than the wizard's own —
        // the ones a second registration would collide with.
        private string[] RegistrationsOtherThan(string ownScript)
        {
            var others = new System.Collections.Generic.List<string>();
            foreach (string registration in _providerRegistrations)
            {
                if (registration != ownScript) others.Add(registration);
            }

            return others.ToArray();
        }

        private void ScanForProviderRegistration()
        {
            try
            {
                // One walk — the build's own — then both questions of its list:
                // which scripts register, and where the wizard's own script is.
                int unreadable = 0;
                System.Collections.Generic.IEnumerable<string> scripts =
                    ReleaseCredentialPathAdvisory.ScriptsUnder(Application.dataPath, ref unreadable);
                _providerRegistrations =
                    ReleaseCredentialPathAdvisory.RegistrationsAmong(
                        Application.dataPath, scripts, ref unreadable, out _providerRegistrationsInPlayers);
                _providerScriptPath = ReleaseCredentialSetup.ScriptPathAmong(Application.dataPath, scripts);
                _providerUnreadable = unreadable;
                _providerScanFailed = false;
            }
            catch (Exception)
            {
                // ⛔ Said, not decided: the build hook claims a provider on the
                // same failure because a hook must not invent a fault, but this
                // is a conversation, and "I could not look" is the true sentence.
                _providerRegistrations = new string[0];
                _providerRegistrationsInPlayers = new string[0];
                _providerUnreadable = 0;
                _providerScriptPath = null;
                _providerScanFailed = true;
            }

            _compiledProviderType = CompiledProviderType(_providerScriptPath);
            _providerScanTaken = true;
            InvalidateProviderPlacement();
        }

        private void WriteProviderScript(bool typeOfThatNameIsCompiled)
        {
            ReleaseCredentialSetup.Write outcome = ReleaseCredentialSetup.WriteProvider(
                Application.dataPath, typeOfThatNameIsCompiled, NewLineForNewScripts());

            _providerWriteStatus  = ReleaseCredentialSetup.WriteStatus(outcome);
            _providerWriteSettled =
                outcome == ReleaseCredentialSetup.Write.Written
                || outcome == ReleaseCredentialSetup.Write.AlreadyThere;

            // ⛔ For a file that is already there as well as one just written:
            // the assembly definition governing it is a property of the PROJECT,
            // not of this press, and a project whose script predates the press
            // met the note on no path at all — which is the day it is true.
            if (outcome == ReleaseCredentialSetup.Write.Written
                || outcome == ReleaseCredentialSetup.Write.AlreadyThere)
            {
                // The import is what compiles it; the scan is retaken so the
                // status above turns on the file just written rather than on
                // the reload after it.  Neither is owed for a file that was
                // already there and already compiled.
                if (outcome == ReleaseCredentialSetup.Write.Written)
                {
                    AssetDatabase.Refresh();
                    ScanForProviderRegistration();
                }

                // A folder an assembly definition governs compiles the script
                // into that assembly, which sees the SDK only through a
                // reference — said now, not as "a compile error" later.
                string assemblyDefinition =
                    UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(ReleaseCredentialSetup.ProjectPath);
                // Null where that definition already references the SDK, which
                // is a project with nothing left to do: the note is the remedy
                // for a compile error, and one said to a project that cannot
                // have it is a warning the developer learns to skip.  The
                // reference is read from the definition — by name or by the guid
                // the Inspector writes by default, which only the asset database
                // can resolve.
                string note = string.IsNullOrEmpty(assemblyDefinition)
                    ? null
                    : ReleaseCredentialSetup.AssemblyDefinitionNote(
                        assemblyDefinition,
                        ReleaseCredentialPathAdvisory.AssemblyDefinitionReferences(
                            assemblyDefinition,
                            ReleaseCredentialSetup.RuntimeAssemblyName,
                            AssemblyDefinitionTextOf));
                if (note != null)
                {
                    _providerWriteStatus += " " + note;
                    _providerWriteSettled = false;
                }
            }

            // ⛔ And it is said in the console as well as in the window: a
            // developer who presses and closes the wizard has still been told,
            // and the outcomes that leave no working provider are worth
            // interrupting for.
            if (_providerWriteSettled) Debug.Log("[RTMPE] " + _providerWriteStatus);
            else                       Debug.LogWarning("[RTMPE] " + _providerWriteStatus);
        }

        // The text of the assembly definition a reference's guid names, read
        // through the asset database.
        //
        // ⛔ Not File.ReadAllText over the resolved path: what the database
        // answers is a project-relative path whose bytes need not be there — an
        // SDK installed the shipped way lives under Library/PackageCache and
        // answers `Packages/com.rtmpe.sdk/…`, which File.Exists says nothing
        // about, so the reference would read as absent on the default install
        // route and the wizard would tell a correct project to add it.
        private static string AssemblyDefinitionTextOf(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(path);
            return asset != null ? asset.text : null;
        }

        // When the residue question was last asked, and what it answered.  See
        // the draw above for why it is neither cached for the panel's life nor
        // asked on every repaint.
        private double _residueAskedAt;
        private bool   _residueIsThere;

        private bool ResidueIsInTheProject()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_residueAskedAt > 0d && now - _residueAskedAt < 1d) return _residueIsThere;

            _residueAskedAt = now;
            _residueIsThere = DevelopmentApiKeyStaging.ResidueIsInTheProject(Application.dataPath);
            return _residueIsThere;
        }

        // The line ending the Editor writes new scripts with, so the file opens
        // in the developer's IDE without a mixed-endings prompt.
        private static string NewLineForNewScripts()
        {
            switch (EditorSettings.lineEndingsForNewScripts)
            {
                case LineEndingsMode.Windows: return "\r\n";
                case LineEndingsMode.Unix:    return "\n";
                default:                      return Environment.NewLine;
            }
        }

        // The class Unity compiled from the written script, or null before that
        // reload — or when the project declares no such type.  Asked of the
        // script itself first: MonoScript.GetClass is the class Unity bound to
        // the file, in whatever namespace the developer has since put it, and
        // a lookup by the bare name alone read a namespaced copy as "not
        // compiled yet" for as long as it stayed that way.  Then, with no
        // script or none bound yet, of every loaded assembly by the bare name —
        // never Assembly-CSharp by name, because an assembly definition over
        // Assets/RTMPE would move the script out of it.
        //
        // ⚠️ The bare name, from any assembly: that is also how a name already
        // taken by the developer's own class is noticed before a second
        // declaration is written beside it — the conflict a second declaration
        // would be is with a type of that full name.
        private static Type CompiledProviderType(string scriptPath)
        {
            if (scriptPath != null)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                Type bound = script != null ? script.GetClass() : null;
                if (bound != null) return bound;
            }

            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(ReleaseCredentialSetup.ClassName, false);
                    if (type != null) return type;
                }
                catch (Exception)
                {
                    // An assembly that will not answer for a name is not the one
                    // holding it; the project's own assemblies do answer.
                }
            }

            return null;
        }

        private void DrawStepGameType()
        {
            EditorGUILayout.LabelField("Game Type Defaults", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _maxPlayers = EditorGUILayout.IntSlider("Max Players", _maxPlayers, 1, 100);
            _tickRate   = EditorGUILayout.IntSlider("Tick Rate (Hz)", _tickRate, 10, 60);
            if (EditorGUI.EndChangeCheck())
                _hasUnsavedChanges = true;

            EditorGUILayout.HelpBox(
                "Tick Rate is written to the NetworkSettings asset when you press Finish. It is " +
                "this client's tick rate; the server runs at a fixed 30 Hz, so 30 matches it.\n" +
                "Max Players is not written to any asset. Set a room's capacity when the room is " +
                "created: CreateRoomOptions.MaxPlayers with " +
                "NetworkManager.Instance.Rooms.CreateRoom(...), or Max Players on the " +
                "Connection Bootstrap.",
                MessageType.None);
        }

        private void DrawStepTestConn()
        {
            EditorGUILayout.LabelField("Configuration Validation", EditorStyles.boldLabel);

            // The button used to read "Ping Gateway" — but ValidateConfiguration
            // does not open a socket; it only confirms that the wizard's own
            // input fields are well-formed.  The previous label produced a
            // false sense of network connectivity that masked misconfigured
            // firewalls / routing during onboarding.  Live ping/echo testing
            // belongs in the runtime, behind a manager that owns the
            // transport.
            if (GUILayout.Button("Validate Configuration"))
                ValidateConfiguration();

            EditorGUILayout.HelpBox(
                "This step checks that the API key and gateway port fields " +
                "look valid.  It does NOT contact the gateway — open the " +
                "Network Debugger window after pressing Play to verify " +
                "actual connectivity.",
                MessageType.None);

            if (!string.IsNullOrEmpty(_statusMsg))
            {
                var type = _testPassed ? MessageType.Info : MessageType.Error;
                EditorGUILayout.HelpBox(_statusMsg, type);
            }
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // Cancel sits left-most so it is the natural target for a
                // "get me out of here" reflex.  Closing the window via the
                // OS chrome alone used to silently commit whatever partial
                // state had already been saved by a previous "Next →".
                if (GUILayout.Button("Cancel"))
                {
                    if (TryCancel())
                        return;
                }

                GUI.enabled = _step > 0;
                if (GUILayout.Button("← Back"))  { _step--;          }
                GUI.enabled = true;

                GUILayout.FlexibleSpace();

                if (_step < TotalSteps - 1)
                {
                    if (GUILayout.Button("Next →"))
                    {
                        if (TrySaveSettings())
                            _step++;
                    }
                }
                else
                {
                    if (GUILayout.Button("Finish"))
                    {
                        if (!TrySaveSettings())
                            return;
                        // Propagate the typed connection config to the NetworkSettings
                        // asset the runtime reads — TrySaveSettings persists only the
                        // editor-side EditorPrefs cache that repopulates the wizard.
                        PersistConnectionConfigToAsset();
                        ShowNotification(new GUIContent("RTMPE setup complete! 🎮"));
                        // After a successful Save the wizard is in a
                        // consistent state and the unsaved-changes guard
                        // must not fire on the imminent Close().
                        _hasUnsavedChanges = false;
                        EditorApplication.delayCall += Close;
                    }
                }
            }
        }

        /// <summary>
        /// Confirm with the user (when there are unsaved edits) and close
        /// the wizard without committing the in-memory state.  Returns
        /// <c>true</c> when the wizard is being closed so the caller can
        /// abort the rest of the GUI pass — Unity disposes the window on
        /// the next event tick.
        /// </summary>
        private bool TryCancel()
        {
            if (_hasUnsavedChanges)
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "Cancel RTMPE setup?",
                    "You have unsaved changes. Closing the wizard now will " +
                    "discard them and leave previously-saved settings " +
                    "untouched.",
                    "Discard changes",
                    "Keep editing");
                if (!confirmed) return false;
            }
            // Suppress the Unity "want to save?" path — Cancel always
            // discards.  Mark dirty=false so OnDestroy / external Close
            // cannot re-trigger the dialog.
            _hasUnsavedChanges = false;
            EditorApplication.delayCall += Close;
            return true;
        }

        /// <summary>
        /// Persist settings, surfacing any storage failure to the user
        /// instead of silently advancing the wizard.  Returns <c>true</c>
        /// only when persistence succeeded; the caller must not move past
        /// the current step on <c>false</c>.
        /// </summary>
        private bool TrySaveSettings()
        {
            try
            {
                SaveSettings();
                _lastSaveError = null;
                return true;
            }
            catch (System.Exception ex)
            {
                _lastSaveError =
                    $"Failed to save RTMPE settings: {ex.GetType().Name} — {ex.Message}";
                EditorUtility.DisplayDialog(
                    "RTMPE — settings not saved",
                    _lastSaveError + "\n\nThe wizard will remain on this step " +
                    "so you can correct the problem and retry.",
                    "OK");
                Repaint();
                return false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void CheckNetworkManager()
        {
            _networkManagerFound = FindFirstObjectByType<NetworkManager>() != null;
            _bootstrapFound      = FindFirstObjectByType<RtmpeConnectionBootstrap>() != null;
        }

        // ⛔ On the manager's own object, and not on one of its own. The
        // bootstrap makes itself persistent from a ROOT, and NetworkManager is
        // already the object a project keeps across scene loads — putting it
        // anywhere else is how a second persistent copy is manufactured, which
        // the component reports and neither of us wants a wizard to cause.
        private void AddBootstrapToScene()
        {
            var manager = FindFirstObjectByType<NetworkManager>();
            if (manager == null) return;

            var bootstrap = manager.GetComponent<RtmpeConnectionBootstrap>();
            if (bootstrap == null)
                bootstrap = Undo.AddComponent<RtmpeConnectionBootstrap>(manager.gameObject);

            // ⛔ The policy and the mode as well as the three visible fields:
            // a step that set a prefab and a room name onto a component still
            // pointing at a policy the reader never saw produced a component
            // whose configuration the reader could not account for.
            var so = new SerializedObject(bootstrap);
            var prefab = so.FindProperty("_playerPrefab");
            if (prefab != null) prefab.objectReferenceValue = _playerPrefab;
            var policy = so.FindProperty("_entryPolicy");
            if (policy != null) policy.enumValueIndex = (int)_bootstrapPolicy;
            var roomName = so.FindProperty("_roomName");
            if (roomName != null) roomName.stringValue = _roomName;
            var mode = so.FindProperty("_matchmakingMode");
            if (mode != null) mode.stringValue = _bootstrapMode;
            var maxPlayers = so.FindProperty("_maxPlayers");
            if (maxPlayers != null) maxPlayers.intValue = _bootstrapMaxPlayers;
            so.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            _bootstrapFound = true;
            Repaint();
        }

        private void AddNetworkManagerToScene()
        {
            // Resolve (or create) a NetworkSettings asset before instantiating
            // the component so the freshly-added NetworkManager is wired to a
            // real, on-disk asset rather than left with a null _settings
            // reference that would force CreateDefault() at runtime and lose
            // any project-specific configuration.
            var settings = PersistConnectionConfigToAsset();

            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            Undo.RegisterCreatedObjectUndo(go, "Add NetworkManager");

            if (settings != null)
            {
                // Wire the freshly-added component to the on-disk asset so it does
                // not fall back to a runtime-only CreateDefault() instance that
                // would discard the developer's configuration.
                var so = new SerializedObject(nm);
                var prop = so.FindProperty("_settings");
                if (prop != null)
                {
                    prop.objectReferenceValue = settings;
                    so.ApplyModifiedProperties();
                }
            }

            EditorSceneManager.MarkSceneDirty(
                EditorSceneManager.GetActiveScene());
            _networkManagerFound = true;
            Repaint();
        }

        // Find the first NetworkSettings asset in the project, or create a
        // default one at Assets/RTMPE/NetworkSettings.asset (creating the
        // parent folder when missing).  Multiple existing assets are
        // tolerated — the first hit wins so the wizard never blocks on an
        // ambiguous project layout.
        private static NetworkSettings ResolveOrCreateNetworkSettings()
        {
            var guids = AssetDatabase.FindAssets("t:NetworkSettings");
            if (guids != null && guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                if (guids.Length > 1)
                    Debug.LogWarning(
                        $"[RTMPE] {guids.Length} NetworkSettings assets exist in the project; the " +
                        $"wizard is using '{path}' (first match).  Multiple assets risk a build " +
                        "shipping a different NetworkSettings than the one you configured — keep a " +
                        "single asset, or confirm the one wired to your NetworkManager carries the " +
                        "API-key envelope.");
                var existing = AssetDatabase.LoadAssetAtPath<NetworkSettings>(path);
                if (existing != null) return existing;
            }

            const string folder    = "Assets/RTMPE";
            const string assetPath = folder + "/NetworkSettings.asset";

            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "RTMPE");

            var created = ScriptableObject.CreateInstance<NetworkSettings>();
            AssetDatabase.CreateAsset(created, assetPath);
            AssetDatabase.SaveAssets();
            return created;
        }

        // Resolve (or create) the project's NetworkSettings asset, copy the
        // wizard's connection config onto it, and persist.  This is the single
        // path by which the typed values reach the asset the runtime reads:
        // SaveSettings persists merely the editor-side EditorPrefs cache that
        // repopulates the wizard's own fields, which nothing at runtime consults.
        // Shared by the Finish step and the Add-NetworkManager action so the two
        // entry points cannot drift.
        private NetworkSettings PersistConnectionConfigToAsset()
        {
            var settings = ResolveOrCreateNetworkSettings();
            if (settings != null)
            {
                ApplyConnectionConfig(
                    settings, _gatewayHost, _gatewayPort, _tickRate, _pinnedKey, _sealKey,
                    _jwtIssuer, _jwtAudience);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
            return settings;
        }

        // Pure helper (no UnityEditor dependency) that copies the wizard's
        // connection config onto a NetworkSettings asset, so the field-propagation
        // contract is unit-testable without a live Editor scene; the caller
        // persists the result via EditorUtility.SetDirty + AssetDatabase.SaveAssets.
        // Host/port/tick always carry a meaningful wizard value and so overwrite
        // unconditionally; the dashboard public keys and JWT claims are written only
        // when supplied, so running the wizard with those fields blank never clears a
        // value the developer pasted directly onto the asset.  (_maxPlayers has no
        // asset home — rooms set it per-call via CreateRoomOptions — so it is not
        // copied here.)
        internal static void ApplyConnectionConfig(
            NetworkSettings settings, string host, int port, int tickRate,
            string pinnedKey, string sealKey, string jwtIssuer, string jwtAudience)
        {
            if (settings == null) return;
            settings.serverHost = host;
            settings.serverPort = port;
            settings.tickRate   = tickRate;
            if (!string.IsNullOrWhiteSpace(sealKey))
                settings.apiKeySealServerPublicKeyHex = sealKey.Trim();
            if (!string.IsNullOrWhiteSpace(pinnedKey))
                settings.pinnedServerPublicKeyHex = pinnedKey.Trim();
            // JWT claims are byte-compared case-sensitively by the validator, so
            // they are only trimmed of incidental whitespace — never lower-cased
            // like the hex keys above.
            if (!string.IsNullOrWhiteSpace(jwtIssuer))
                settings.expectedJwtIssuer = jwtIssuer.Trim();
            if (!string.IsNullOrWhiteSpace(jwtAudience))
                settings.expectedJwtAudience = jwtAudience.Trim();
        }

        private void ValidateConfiguration()
        {
            // Configuration-only check.  A live socket round-trip is intentionally
            // not performed here — the wizard runs in the Editor before any
            // NetworkManager bootstrap, and a half-baked Connect+Disconnect dance
            // would drown out misconfigured-firewall errors more than it surfaces
            // them.  Use the runtime Network Debugger window to confirm real
            // connectivity once the project is in Play mode.  The decision is
            // delegated to the pure WizardConfigValidator so it is unit-testable
            // off-Editor and reports the specific missing piece instead of a
            // blanket verdict.
            var (ok, message) = WizardConfigValidator.Validate(_apiKey, _gatewayPort, _pinnedKey, _sealKey);
            _testPassed = ok;
            _statusMsg = ok
                ? $"✅  Configuration is valid — {_gatewayHost}:{_gatewayPort} (Strict pinning active).  " +
                  "Press Play and open the Network Debugger window to verify the gateway is reachable."
                : "❌  " + message;
            Repaint();
        }

        /// <summary>
        /// This wizard's storage name for <paramref name="name"/>, qualified
        /// with the project it belongs to.
        /// </summary>
        /// <remarks>
        /// ⛔ `EditorPrefs` is per-user and per-Unity-install, never
        /// per-project. Two of the values below are the pinned Ed25519 key and
        /// the X25519 seal key, and step 1 of this wizard writes them onto a
        /// COMMITTED `NetworkSettings` asset — so under a fixed name, opening
        /// the wizard in a second project loaded the first project's keys and
        /// one click committed them here.
        /// </remarks>
        private static string Pref(string name) => EditorProjectScope.Scoped(name);

        private void LoadSettings()
        {
            // API key is read from the OS credential vault (DPAPI / macOS
            // Keychain / libsecret) via ApiKeyStore — never from plaintext
            // EditorPrefs. ApiKeyStore.Load() also one-shot migrates any
            // legacy plaintext entry written by older SDK versions.
            _apiKey      = ApiKeyStore.Load();
            // ⛔ Taken here and nowhere else. A baseline refreshed anywhere the
            // developer can type would compare the field against itself, which
            // is the one reading under which nothing is ever an instruction.
            _loadedApiKey = _apiKey;
            _gatewayHost = EditorPrefs.GetString(Pref("RTMPE_Host"),        "127.0.0.1");
            _gatewayPort = EditorPrefs.GetInt   (Pref("RTMPE_Port"),        7777);
            _maxPlayers  = EditorPrefs.GetInt   (Pref("RTMPE_MaxPlayers"),  16);
            _tickRate    = EditorPrefs.GetInt   (Pref("RTMPE_TickRate"),     30);
            _sealKey     = EditorPrefs.GetString(Pref("RTMPE_SealKey"),     "");
            _pinnedKey   = EditorPrefs.GetString(Pref("RTMPE_PinnedKey"),   "");
            _jwtIssuer   = EditorPrefs.GetString(Pref("RTMPE_JwtIssuer"),   "");
            _jwtAudience = EditorPrefs.GetString(Pref("RTMPE_JwtAudience"), "");
        }

        private void SaveSettings()
        {
            // 🔴 Conditional, and the condition is the whole repair. Saving
            // unconditionally meant every Next → wrote the field, and an empty
            // field deletes — so a keychain that would not answer when the
            // window opened cost the developer the key in it. The rule that
            // decides is stated once, in WizardApiKeyEdit, where it is executed
            // by the test shard rather than described here.
            //
            // 🔑 The baseline moves only after the store took it: Save throws
            // out of here into TrySaveSettings, and a baseline advanced before
            // the write would report the next unchanged Next → as nothing to do
            // while the vault still held the old key.
            if (WizardApiKeyEdit.ShouldWrite(_loadedApiKey, _apiKey))
            {
                ApiKeyStore.Save(_apiKey);
                _loadedApiKey = _apiKey;
            }

            EditorPrefs.SetString(Pref("RTMPE_Host"),       _gatewayHost);
            EditorPrefs.SetInt   (Pref("RTMPE_Port"),       _gatewayPort);
            EditorPrefs.SetInt   (Pref("RTMPE_MaxPlayers"), _maxPlayers);
            EditorPrefs.SetInt   (Pref("RTMPE_TickRate"),   _tickRate);
            EditorPrefs.SetString(Pref("RTMPE_SealKey"),    _sealKey);
            EditorPrefs.SetString(Pref("RTMPE_PinnedKey"),  _pinnedKey);
            EditorPrefs.SetString(Pref("RTMPE_JwtIssuer"),   _jwtIssuer);
            EditorPrefs.SetString(Pref("RTMPE_JwtAudience"), _jwtAudience);
        }
    }
}
