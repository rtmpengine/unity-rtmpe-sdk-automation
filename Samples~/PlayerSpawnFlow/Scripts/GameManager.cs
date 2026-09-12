// RTMPE SDK — Sample: Player Spawn Flow
//
// The whole of the entry flow, from a cold start to a player standing in a
// room: connect, open or enter a room, spawn this client's avatar, and let go
// of it again when the connection ends. Everything else a game does — movement,
// scoring, scene changes — sits on top of this and is not what this sample is
// about.
//
// 🔑 The prefab id is ASKED FOR, never typed. Before this sample the flow ended
// in a hand-written `RegisterPrefab(1, prefab)` beside a `[SerializeField] uint
// _playerPrefabId = 1`, and both halves were a second, private copy of a table
// the SDK already keeps: Window → RTMPE → Network Prefabs allocates the ids and
// writes the registry asset, and NetworkManager loads it into the spawn manager
// on every connect. A number typed here agrees with that table only until
// somebody adds a prefab, and the day it stops agreeing every client spawns a
// different object for the same id.
//
// ⛔ And the question goes to the SPAWN MANAGER, not to the registry asset. The
// asset is one of the table's two writers and the weaker one — a hand
// RegisterPrefab is applied after it and outranks it — so scanning the asset
// answers a question about a copy. `Spawner.TryGetPrefabId` asks the table
// `Spawn` will actually consult, which is the only place the answer is not a
// guess.
//
// ⚠️ The API key is never a field on this component. Unity serialises a string
// field into the scene asset, which is committed and shipped inside the player;
// RTMPE.Core.ApiKeySource is the supported source and the README says where it
// comes from.

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

        // Bounded in the Inspector because the platform bounds it: a cap
        // outside 1-100 is refused before the request is sent, and a room that
        // never opens is a hard thing to trace back to a number in a field.
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
                // The wizard's vault is registered by the Editor assembly, which a
                // build does not carry, so the remedy that helps here is not the
                // one that helps in a player. Asked at runtime rather than with
                // #if because a sample IS compiled here, by SampleScriptsCompileTests,
                // in one configuration — so a branch the preprocessor removes would
                // slip past the only check a sample gets.
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
                // Named rather than silent, and both supported paths named,
                // because a spawn that does not happen leaves nothing to read.
                // The registry is opt-in — a project that has never opened the
                // window has none — and hand registration stays a supported
                // route for a prefab that arrives from a bundle or from
                // Addressables.
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

            // This object's own transform is the spawn point. A game that wants
            // spawn points chooses one here; a sample that carried a Vector3
            // field would be teaching a third place to keep the same fact.
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
