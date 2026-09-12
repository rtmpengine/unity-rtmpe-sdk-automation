// RTMPE SDK — Samples~/BasicConnection/Scripts/ConnectionTest.cs
//
// Minimal demo MonoBehaviour: connect to the RTMPE gateway on Start, display
// live connection status with OnGUI, and disconnect cleanly on Destroy.
//
// Quick Start:
//   1. Add a NetworkManager component to a GameObject in your test scene
//      (Component > RTMPE > NetworkManager). ConnectionTest requires one —
//      NetworkManager.Instance returns null when the scene has none.
//   2. Attach this script to a GameObject in the same scene.
//   3. Supply the API key from outside the build. In the Editor, store it once
//      via Window > RTMPE > Setup Wizard and play mode reads it back from the OS
//      credential vault. No build carries that vault, so a player reads, in the
//      order ApiKeySource consults them: a provider registered with
//      ApiKeySource.SetProvider — the only one a shipped game can use — then a
//      key staged for a development build (Setup Wizard; that build reads it and
//      a release build refuses to carry it), then --rtmpe-api-key-file <path>,
//      then RTMPE_API_KEY.
//      See RTMPE.Core.ApiKeySource.
//   4. Fill in apiKeySealServerPublicKeyHex inside your NetworkSettings asset
//      with your gateway's X25519 public key (required — the API key is sealed
//      to it, and the gateway accepts no other envelope).
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
                // The wizard writes to a vault the Editor assembly registers, and a
                // build carries no Editor assembly — so naming it to a player, on
                // screen or in a log, points at a tool that cannot help them.
                // Asked at runtime rather than with #if, and the reason is the
                // opposite of what it looks like: a sample IS compiled here, by
                // SampleScriptsCompileTests — in ONE configuration. A branch the
                // preprocessor removes would slip past the only check a sample
                // gets. Both of these are compiled and both are checked.
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

            // A drop the SDK still holds a reconnect token for is resumed, not
            // redialled.  The distinction is not cosmetic: Connect(apiKey) puts
            // the credential back on the wire and starts a new session with a
            // new identity, while Reconnect() resumes the one the other players
            // already know and — with autoRejoinLastRoomOnReconnect on, the
            // default — re-enters the room by itself.
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
