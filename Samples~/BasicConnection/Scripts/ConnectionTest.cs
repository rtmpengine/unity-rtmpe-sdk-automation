// RTMPE SDK — Samples~/BasicConnection/Scripts/ConnectionTest.cs
//
// Minimal demo MonoBehaviour: connect to the RTMPE server on Start, display
// live connection status with OnGUI, and disconnect cleanly on Destroy.
//
// Quick Start:
//   1. Add a NetworkManager component to a GameObject in your test scene
//      (Component > RTMPE > NetworkManager). ConnectionTest requires one —
//      NetworkManager.Instance returns null when the scene has none.
//   2. Attach this script to a GameObject in the same scene.
//   3. Supply the API key from outside the build. In the Editor, store it once
//      via Window > RTMPE > Setup Wizard; Play mode reads it back from the OS
//      credential store. A player build uses the first of these that has one:
//        - a provider registered with ApiKeySource.SetProvider (the source a
//          shipped game uses);
//        - in a development build, a key staged with the Setup Wizard;
//        - --rtmpe-api-key-file <path>, then --rtmpe-api-key <key>;
//        - the RTMPE_API_KEY environment variable.
//      See RTMPE.Core.ApiKeySource.
//   4. In the Setup Wizard, enter the server's Sealed-Box Public Key (X25519)
//      from the dashboard; it is saved to your NetworkSettings asset. It is
//      required: the API key is sealed to it before it is sent.
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
            _statusLine = "Connected!";
            Debug.Log("[ConnectionTest] Connected to RTMPE gateway.");
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            _statusLine = $"Disconnected ({reason})";
            Debug.Log($"[ConnectionTest] Disconnected — reason: {reason}");

            if (_shouldReconnect && reconnectDelay > 0f && this != null)
            {
                _reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
            }
        }

        private void OnConnectionFailed(string error)
        {
            _statusLine = $"Connection failed: {error}";
            Debug.LogError($"[ConnectionTest] Connection failed — {error}");

            if (_shouldReconnect && reconnectDelay > 0f && this != null)
            {
                _reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
            }
        }

        private void OnRttUpdated(float rttMs)
        {
            _rttLine = $"RTT: {rttMs:F1} ms";
        }

        // ── Auto-reconnect ────────────────────────────────────────────────────

        private IEnumerator ReconnectAfterDelay()
        {
            _statusLine = $"Reconnecting in {reconnectDelay:F0} s…";
            yield return new WaitForSeconds(reconnectDelay);
            _reconnectCoroutine = null;

            var nm = NetworkManager.Instance;
            if (nm == null || nm.State != NetworkState.Disconnected) yield break;

            // Resume the session when the SDK still holds a reconnect token.
            // Reconnect() keeps this player's identity and, with
            // autoRejoinLastRoomOnReconnect on (the default), re-enters the room;
            // Connect(apiKey) would start a new session with a new identity.
            if (nm.CanReconnect && nm.Reconnect())
            {
                _statusLine = "Resuming session…";
                Debug.Log("[ConnectionTest] Resuming with the reconnect token.");
                yield break;
            }

            // Reconnect() refuses when the token was never issued, was spent, or
            // was wiped by an explicit Disconnect().  Only then is a full
            // handshake the right answer.
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
