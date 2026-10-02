// RTMPE SDK — Sample: Player Spawn Flow
//
// The entry flow from a cold start to a player standing in a room: connect,
// open a room, spawn this client's avatar, and let go of it again when the
// connection ends. Movement, scoring and scene changes sit on top of this.
//
// The prefab id is looked up, never typed. Window → RTMPE → Network Prefabs
// allocates the ids and writes the prefab registry asset; with that asset
// assigned to the NetworkSettings asset, NetworkManager loads it into the
// spawn manager on every connect. Spawner.TryGetPrefabId asks the same table
// Spawn uses, which also holds any prefab registered by hand.
//
// The API key is not a field on this component: a serialised string is
// written into the scene asset and shipped inside the player.
// RTMPE.Core.ApiKeySource supplies it; the README says where it comes from.

using System;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Rooms;

namespace RTMPE.Samples.PlayerSpawnFlow
{
    /// <summary>
    /// Connects on <c>Start</c>, opens a room, and spawns the local player in it.
    /// </summary>
    /// <remarks>
    /// Attach to a persistent GameObject in the scene, beside a
    /// <c>NetworkManager</c>. The object's own transform is the spawn point, so
    /// moving it in the scene moves where the player appears.
    /// </remarks>
    public class GameManager : MonoBehaviour
    {
        [Header("Room")]
        [SerializeField] private string _roomName = "Player Spawn Flow";

        // The room's capacity, 1–100. The SDK refuses a larger value before the
        // request is sent.
        [Range(1, 100)]
        [SerializeField] private int    _maxPlayers = 4;

        [Header("Spawn")]
        [Tooltip("The prefab this client spawns for itself. It must be listed in "
                 + "Window → RTMPE → Network Prefabs, which is where its id comes from.")]
        [SerializeField] private GameObject _playerPrefab;

        private NetworkBehaviour _localPlayer;

        // Named adapter delegates stored as fields so they can be removed in OnDestroy.
        // Anonymous lambdas cannot be unsubscribed; storing them prevents event-handler
        // leaks that would cause NullReferenceExceptions after this object is destroyed.
        private Action<RoomInfo> _onRoomJoinedHandler;
        private Action<RoomInfo> _onRoomCreatedHandler;

        private void Start()
        {
            // Resolved rather than serialized: a key typed into the Inspector is
            // written into this scene's asset, committed with it, and shipped
            // inside the built player.
            if (!ApiKeySource.TryResolve(out string apiKey))
            {
                // A player build cannot read the Setup Wizard's vault (only the
                // Editor can), so the message names the sources a player can use.
                // Application.isEditor is checked at runtime rather than with #if,
                // so both messages are compiled.
                Debug.LogError(
                    "[GameManager] No API key. " +
                    (Application.isEditor
                        ? "Store one via Window → RTMPE → Setup Wizard, "
                        : "The Setup Wizard's vault is written by the Editor and no build " +
                          "carries it, so this player needs one of these instead: register a " +
                          "provider with ApiKeySource.SetProvider before connecting, ") +
                    "or launch with " + ApiKeySource.CommandLineFileOption + " <path>, " +
                    "or set the " + ApiKeySource.EnvironmentVariableName + " environment variable. " +
                    (ApiKeySource.LastError == null
                        ? ""
                        : "A configured source failed: " + ApiKeySource.LastError.Message));
                return;
            }

            var net = NetworkManager.Instance;
            if (net == null) return;

            net.OnConnected    += OnConnected;
            net.OnDisconnected += OnDisconnected;

            // Store event handler references so they can be unsubscribed in OnDestroy.
            _onRoomJoinedHandler  = _ => OnRoomEntered();
            _onRoomCreatedHandler = _ => OnRoomEntered();
            net.Rooms.OnRoomJoined  += _onRoomJoinedHandler;
            net.Rooms.OnRoomCreated += _onRoomCreatedHandler;

            net.Connect(apiKey);
        }

        private void OnDestroy()
        {
            var net = NetworkManager.Instance;
            if (net == null) return;

            net.OnConnected    -= OnConnected;
            net.OnDisconnected -= OnDisconnected;

            // Unsubscribe room events using the stored references.
            if (_onRoomJoinedHandler  != null) net.Rooms.OnRoomJoined  -= _onRoomJoinedHandler;
            if (_onRoomCreatedHandler != null) net.Rooms.OnRoomCreated -= _onRoomCreatedHandler;
        }

        // ── Handlers ───────────────────────────────────────────────────────────

        private void OnConnected()
        {
            Debug.Log("[GameManager] Connected — creating room.");

            NetworkManager.Instance?.Rooms.CreateRoom(new CreateRoomOptions
            {
                Name       = _roomName,
                MaxPlayers = _maxPlayers,
            });
        }

        private void OnRoomEntered()
        {
            var net = NetworkManager.Instance;
            if (net == null || _playerPrefab == null) return;

            // OnRoomJoined and OnRoomCreated both route here, and OnRoomJoined
            // re-fires on an automatic rejoin after a reconnect. The local
            // avatar must outlive those repeats: spawning again while one is
            // still live would orphan the previous copy and leave duplicates in
            // the scene. The reference is cleared in OnDisconnected, so a fresh
            // session spawns anew.
            if (_localPlayer != null) return;

            if (!net.Spawner.TryGetPrefabId(_playerPrefab, out uint prefabId))
            {
                // Name both fixes: the generated prefab registry, or registering
                // the prefab by hand (for example a prefab loaded from an
                // AssetBundle or through Addressables).
                Debug.LogError(
                    "[GameManager] no prefab id is registered for " + _playerPrefab.name
                    + ", so no client could resolve it. Select the prefab in the Project window, "
                    + "open Window → RTMPE → Network Prefabs and press \"Allocate id for "
                    + "selection\"; then press \"Generate RtmpePrefabIds.cs and the prefab "
                    + "registry\" and assign the generated registry asset to the NetworkSettings "
                    + "asset's Prefab Registry field — or register it yourself with "
                    + "Spawner.RegisterPrefab before the room is entered.");
                return;
            }

            Debug.Log("[GameManager] Entered room — spawning local player.");

            // This object's own transform is the spawn point; a game with
            // several spawn points chooses one here.
            _localPlayer = net.Spawner.Spawn(
                prefabId,
                transform.position,
                transform.rotation);
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            Debug.Log($"[GameManager] Disconnected ({reason}).");
            _localPlayer = null;
        }
    }
}
