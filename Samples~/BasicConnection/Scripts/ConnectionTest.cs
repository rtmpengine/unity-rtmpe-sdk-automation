// RTMPE SDK — Samples~/BasicConnection/Scripts/ConnectionTest.cs
//
// Minimal demo MonoBehaviour: connect to the RTMPE server on Start, display
// live connection status with OnGUI, and disconnect cleanly on Destroy.
//
// Quick Start (the sample's README has the full steps):
//   1. Run Window > RTMPE > Setup Wizard to Finish, entering the API key,
//      Server Host, Server Port and both public keys from the dashboard
//      (Sealed-Box Public Key (X25519) and Pinned Server Public Key). The
//      wizard keeps the key in the OS credential store and writes the rest to
//      a NetworkSettings asset (Assets/RTMPE/NetworkSettings.asset if the
//      project had none).
//   2. Add a NetworkManager to a GameObject in your test scene
//      (Component > RTMPE > NetworkManager) and drag that asset onto its
//      Settings field: with the field empty it runs on defaults that cannot
//      connect.
//   3. Attach this script to a GameObject in the same scene.
//   4. A player build needs the key from outside the build: a provider
//      registered with ApiKeySource.SetProvider (the source a shipped game
//      uses), a key staged with the Setup Wizard (development builds),
//      --rtmpe-api-key-file <path>, --rtmpe-api-key <key>, or the
//      RTMPE_API_KEY environment variable. See RTMPE.Core.ApiKeySource.
//   5. Press Play.

using System.Collections;
using System.Text;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Samples.BasicConnection
{
    /// <summary>
    /// Minimal RTMPE connection demo. Attach to a scene GameObject.
    /// Requires a <see cref="NetworkManager"/> component to be present in the
    /// scene — <see cref="NetworkManager.Instance"/> returns <c>null</c> (and
    /// logs a warning) when the scene contains none.
    /// </summary>
    public sealed class ConnectionTest : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Connection")]
        [SerializeField]
        [Tooltip("Connect automatically on Start.")]
        private bool connectOnStart = true;

        [SerializeField]
        [Tooltip("Seconds to wait before an automatic reconnect attempt " +
                 "(0 = disabled). The attempt resumes the session with the " +
                 "reconnect token when the SDK still holds one, and falls back " +
                 "to a full Connect(apiKey) only when it does not.")]
        private float reconnectDelay = 5f;

        // ── Runtime state ─────────────────────────────────────────────────────

        private string _statusLine     = "Idle";
        private string _rttLine        = "";
        private bool   _shouldReconnect;
        private Coroutine _reconnectCoroutine;

        // A Reconnect() this sample started and the SDK is still running. Every
        // failed attempt inside it raises OnDisconnected; its outcome is
        // OnConnected or OnReconnectFailed.
        private bool   _ladderRunning;

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Subscribe to events before Start() so we never miss an early edge.
            var nm = NetworkManager.Instance;
            if (nm == null) { _statusLine = "ERROR: NetworkManager unavailable."; return; }

            nm.OnStateChanged   += OnStateChanged;
            nm.OnConnected      += OnConnected;
            nm.OnDisconnected   += OnDisconnected;
            nm.OnConnectionFailed += OnConnectionFailed;
            nm.OnReconnectFailed  += OnReconnectFailed;
            nm.OnRttUpdated     += OnRttUpdated;
        }

        private void Start()
        {
            if (connectOnStart)
                TryConnect();
        }

        private void OnDestroy()
        {
            // Clean up so listeners are not called after this object is gone.
            if (_reconnectCoroutine != null)
                StopCoroutine(_reconnectCoroutine);

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            nm.OnStateChanged   -= OnStateChanged;
            nm.OnConnected      -= OnConnected;
            nm.OnDisconnected   -= OnDisconnected;
            nm.OnConnectionFailed -= OnConnectionFailed;
            nm.OnReconnectFailed  -= OnReconnectFailed;
            nm.OnRttUpdated     -= OnRttUpdated;

            if (nm.IsConnected)
                nm.Disconnect();
        }

        // ── Public actions ────────────────────────────────────────────────────

        /// <summary>Connect with the resolved API key.</summary>
        public void TryConnect()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) { _statusLine = "ERROR: no NetworkManager."; return; }

            // Resolved rather than serialized: a key typed into the Inspector is
            // written into the scene asset, committed with it, and shipped
            // inside the built player.
            if (!ApiKeySource.TryResolve(out string apiKey))
            {
                // A player build cannot read the Setup Wizard's vault (only the
                // Editor can), so the message names the sources a player can use.
                // Application.isEditor is checked at runtime rather than with #if,
                // so both messages are compiled.
                _statusLine = Application.isEditor
                    ? "no API key — see Window > RTMPE > Setup Wizard."
                    : "no API key — this build needs one supplied from outside it.";
                Debug.LogWarning("[ConnectionTest] Cannot connect: no API key. " +
                                 (Application.isEditor
                                     ? "Store one via Window > RTMPE > Setup Wizard, launch with "
                                     : "The Setup Wizard's vault is written by the Editor and " +
                                       "no build carries it, so this player needs one of these " +
                                       "instead: register a provider with " +
                                       "ApiKeySource.SetProvider before connecting, launch with ") +
                                 ApiKeySource.CommandLineFileOption + " <path>, or set " +
                                 ApiKeySource.EnvironmentVariableName + ". " +
                                 (ApiKeySource.LastError == null
                                     ? ""
                                     : "A configured source failed: " +
                                       ApiKeySource.LastError.Message));
                return;
            }

            _shouldReconnect = reconnectDelay > 0f;
            _statusLine = "Connecting…";
            nm.Connect(apiKey);
        }

        /// <summary>Disconnect and stop auto-reconnect.</summary>
        public void TryDisconnect()
        {
            _shouldReconnect = false;
            _ladderRunning   = false;
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
            NetworkManager.Instance?.Disconnect();
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnStateChanged(NetworkState prev, NetworkState next)
        {
            _statusLine = $"State: {next}  (was {prev})";
        }

        private void OnConnected()
        {
            _ladderRunning = false;
            _statusLine = "Connected!";
            Debug.Log("[ConnectionTest] Connected to RTMPE gateway.");
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            _statusLine = $"Disconnected ({reason})";
            Debug.Log($"[ConnectionTest] Disconnected — reason: {reason}");
            ScheduleReconnect();
        }

        private void OnConnectionFailed(string error)
        {
            _statusLine = $"Connection failed: {error}";
            Debug.LogError($"[ConnectionTest] Connection failed — {error}");
            ScheduleReconnect();
        }

        private void OnReconnectFailed(int attempts)
        {
            // The SDK spent every attempt it was given: authenticate again.
            _ladderRunning = false;
            _statusLine = $"Reconnect failed after {attempts} attempt(s)";
            ScheduleReconnect();
        }

        private void OnRttUpdated(float rttMs)
        {
            _rttLine = $"RTT: {rttMs:F1} ms";
        }

        // ── Auto-reconnect ────────────────────────────────────────────────────

        // The one place a retry is scheduled. An attempt that times out raises
        // both OnConnectionFailed and OnDisconnected, and every failed attempt
        // inside Reconnect() raises OnDisconnected again — so a retry already
        // waiting, or the SDK's own attempts still running, are left alone.
        private void ScheduleReconnect()
        {
            if (!_shouldReconnect || reconnectDelay <= 0f || this == null) return;
            if (_reconnectCoroutine != null || _ladderRunning) return;
            _reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
        }

        private IEnumerator ReconnectAfterDelay()
        {
            _statusLine = $"Reconnecting in {reconnectDelay:F0} s…";
            // Real time: a paused game (Time.timeScale = 0) still reconnects.
            yield return new WaitForSecondsRealtime(reconnectDelay);
            _reconnectCoroutine = null;

            var nm = NetworkManager.Instance;
            if (nm == null || nm.State != NetworkState.Disconnected) yield break;

            // Resume the session when the SDK still holds a reconnect token.
            // Reconnect() keeps this player's identity and, with
            // autoRejoinLastRoomOnReconnect on (the default), re-enters the room;
            // Connect(apiKey) would start a new session with a new identity.
            if (nm.CanReconnect && nm.Reconnect())
            {
                _ladderRunning = true;
                _statusLine = "Resuming session…";
                Debug.Log("[ConnectionTest] Resuming with the reconnect token.");
                yield break;
            }

            // Reconnect() also refuses while attempts are running and in any state
            // but Disconnected — both ruled out above — so what is left is a token
            // never issued, spent, or wiped by an explicit Disconnect(). Only then
            // is a full handshake the right answer.
            _statusLine = "Token unusable — reconnecting from scratch…";
            TryConnect();
        }

        // ── HUD (visible in both Game view and Editor) ────────────────────────

        private readonly GUIStyle _labelStyle  = new GUIStyle();
        private bool              _styleReady;

        private void OnGUI()
        {
            if (!_styleReady)
            {
                _labelStyle.fontSize  = 16;
                _labelStyle.fontStyle = FontStyle.Bold;
                _labelStyle.normal.textColor = Color.white;
                _styleReady = true;
            }

            const int pad = 12;
            const int lineHeight = 24;

            // Background box
            GUI.Box(new Rect(pad, pad, 420, 130), GUIContent.none);

            GUI.Label(new Rect(pad + 8, pad + 6,              400, lineHeight), "[RTMPE] BasicConnection Demo", _labelStyle);
            GUI.Label(new Rect(pad + 8, pad + 6 + lineHeight, 400, lineHeight), _statusLine,  _labelStyle);
            GUI.Label(new Rect(pad + 8, pad + 6 + lineHeight * 2, 400, lineHeight), _rttLine, _labelStyle);

            var nm = NetworkManager.Instance;

            // The button is offered whenever a connection could be attempted.
            // Whether a key is available is settled inside TryConnect, which
            // says which source is missing — hiding the control would leave the
            // developer with nothing to press and nothing to read.
            float btnY = pad + 6 + lineHeight * 3 + 4;
            if (nm != null && nm.State == NetworkState.Disconnected)
            {
                if (GUI.Button(new Rect(pad + 8, btnY, 120, 28), "Connect"))
                    TryConnect();
            }
            else if (nm != null && nm.IsConnected)
            {
                if (GUI.Button(new Rect(pad + 8, btnY, 120, 28), "Disconnect"))
                    TryDisconnect();
            }
        }
    }
}
