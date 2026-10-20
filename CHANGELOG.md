# Changelog

All notable changes to the RTMPE Unity SDK are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and versions
follow `MAJOR.MINOR.PATCH`.

---

## [Unreleased]

## [1.0.15] - 2026-10-20

### Changed

- The changelog lists each release's features and the changes a project must adapt to.

## [1.0.14] - 2026-10-19

### Changed

- Variable updates for an object that has not spawned yet are kept until it spawns,
  despawns or the room changes (`HeldVariableUpdateBytes`, up to 1 MiB).
- Calls made while a room is being left are not sent into the next room.
- Changed values are sent ahead of an ownership transfer or a room leave, whatever their
  send rate.

## [1.0.13] - 2026-10-19

### Added

- Large server messages — long room lists, a room's RPC history, room entries — arrive in
  pieces over UDP when the server supports it.

### Changed

- ⚠️ `ApiKeySource` reads `--rtmpe-api-key-file` and `--rtmpe-api-key` before the Editor
  vault and a staged development key. A provider registered with `SetProvider` still comes
  first, and `RTMPE_API_KEY` last.
- ⚠️ `NetworkTransformInterpolator` holds its interpolation delay between 0.067 and 0.5 s.
- RPCs reach the room in the order they were sent, and a room entry applies the room's RPC
  history before live calls.
- `Reconnect()` recovers from a lost first packet within seconds.
- `RtmpeConnectionBootstrap` spawns the player again after a single-mode scene load
  destroys it, and catches up on what happened while it was disabled. `LocalPlayer` is
  `null` once its object is destroyed.
- Key agreement and signature checks are faster and allocate far less, and X25519 runs in
  constant time.

## [1.0.12] - 2026-10-12

### Added

- `LobbyManager.LastRoomListSource` tells a lobby update from the answer to a filtered
  `ListRooms`, and `LobbyManager.LobbyRooms` holds the joined lobby's list.

### Changed

- ⚠️ An RPC on an object this client may not call is refused at the call:
  `SendEnhancedRpc` logs a warning and `SendEnhancedRpcAsync` throws
  `InvalidOperationException`.
- Another player's spawn of a prefab not registered here yet waits up to 10 seconds for
  `RegisterPrefab` or `LoadPrefabRegistry`.
- Scene loads issued in a row are each announced in order, and a player who joins later
  loads each of them.
- The end of a connection reports the room entries and lobby requests it left unanswered,
  through `Rooms.OnRoomError` and `OnLobbyError`.
- `tickRate` and `maxLobbyStringBytes` set from code take effect at the next `Connect`.
- A spawn made from `OnRoomCreated` logs a warning; spawn from `Rooms.OnRoomJoined`.
- A development build refuses a leftover development key it did not write.

## [1.0.11] - 2026-10-10

### Changed

- ⚠️ A room's creator is its first player: nobody else can join until the creator has, and
  lobby lists do not offer the room before then. A session holds at most three rooms it
  has not joined, and one not joined within five minutes is closed.
- ⚠️ A private room is joined with its code. `JoinRoom` by id admits only the room's
  creator and a player whose seat it holds.
- ⚠️ A kicked player cannot rejoin that room for the rest of its session.
- ⚠️ `maxSpawnsPerRoom` defaults to 8,192, and the room keeps that many objects for players
  who join later.
- Property writes are sent one at a time, and a write the server refuses is sent again over
  the server's current properties.
- A room property is checked when it is written: an `int` must be a whole number in range,
  a `float`, `Vector3` or `Color` finite, and `bytes` base64.
- An object this client owns is despawned for the room when the game destroys it.
- A new owner sends the values it holds, and a transfer that arrives before its object
  waits for it.
- A server call the room refuses completes with `RpcErrorCode.Unavailable` or
  `RpcErrorCode.Unauthorized`.
- Reconnect requests cannot be replayed by another party, and a busy server can confirm a
  client's address before the handshake.
- The `NetworkBehaviour` inspector shows the object's network id, owner and spawn state.

## [1.0.10] - 2026-10-09

### Changed

- The messages for a refused `Despawn` or variable write point to the object's owner.
- The RPC reference states which players can call an object's RPCs.

## [1.0.9] - 2026-10-08

### Added

- `[NetworkVariable(SendRateHz = …)]` declares a variable's send rate where the variable is
  declared.

### Changed

- ⚠️ `NetworkManager.Send` is main-thread only; from a worker thread, queue the call with
  `MainThreadDispatcher.Enqueue`.
- ⚠️ `heartbeatIntervalMs` is at most 15 seconds.
- `serverHost`, `serverPort` and the socket buffer sizes set from code apply at the next
  `Connect` or `Reconnect`.
- `OnReconnectFailed` is raised for every reconnect that ends without restoring the session.
- Deactivating the `NetworkManager`'s GameObject pauses a connect or reconnect, and
  activating it again resumes it.
- A socket error keeps the reconnect token; `Disconnect()` after a dropped connection
  discards it.
- IPv6-only networks reach IPv4 servers through NAT64, and a change of network is detected
  at the first missed heartbeat.
- `RtmpeConnectionBootstrap` makes one room entry at a time and lets the application's own
  `Reconnect` attempts run.
- Entering Play mode without a domain reload starts with no API-key provider or supplied
  key from the previous session.

## [1.0.8] - 2026-10-02

### Added

- Host-only world objects: a world object is used only when the room's host spawned it
  (`SpawnData.SpawnedByHost`).
- A per-player object limit, reported through `OnSpawnRejected` with
  `SpawnRejectReason.PlayerAtObjectCeiling`.
- Additive scenes reach players who join later (`ReservedPropertyKeys.SceneStack`).
- Objects wait for a single-mode scene load and are applied when the load is reported done
  (`NetworkSceneManager.ReportReady`).
- Joining a room delivers the players' properties with the room.
- An RPC that arrives before its object runs when the object spawns.
- `NetworkTransform.OwnerTeleportTo` arrives at once for every player.
- `NetworkManager.DuplicateCopiesDroppedCount`.

### Changed

- ⚠️ **Max Spawns Per Second** is counted for each player.
- ⚠️ `CreateRoom`, `JoinRoom` and `JoinRoomByCode` called from inside a room leave it first.

## [1.0.7] - 2026-09-27

### Added

- World objects: `RtmpeWorldAuthority` and `RtmpeWorldSpawner` keep room state that no
  player owns and hand it to the next host. The **Shared World** sample shows both.
- Server functions: a call to an `RpcTarget.Server` method runs the project's server
  function, an HTTPS endpoint registered in the portal, and `SendEnhancedRpcAsync` returns
  its answer (`RpcResponse.TryReadResult`).
- RPC caller restrictions: `[RtmpeRpc(…, Caller = …)]` with `RpcCaller.Owner`,
  `RpcCaller.Host` or `RpcCaller.Server`; a handler reads `CurrentRpcCaller`.
- A player who joins a room receives the current value of every `NetworkVariable`.
- The Link Simulator, a Network Debugger panel that adds delay, jitter, loss and reordering
  to a Play session.
- Runtime checks recorded during Play and shown in the Network Readiness window.
- The Setup Wizard's production step, which can write
  `Assets/RTMPE/RtmpeCredentialProvider.cs`.
- `NetworkVariableListBool`, `NetworkVariableListVector2` and
  `NetworkVariableListVector2Int`.
- Analyzer rule `RTMPE1030`: an `Instantiate` that creates a networked object without
  spawning it.
- A complete API reference.

### Changed

- ⚠️ A `NetworkVariableList` holds at most what one datagram carries, because a joining
  player receives it whole: 283 `int`s, 141 `Vector2Int`s or 94 `Vector3`s (a `string`
  list is bounded by bytes). `MaxCount` reports the limit, `Add` and `Insert` refuse past
  it, and `CanAdd` checks an element.
- ⚠️ `NetworkVariableList<T>` implements `IReadOnlyList<T>`, so `string.Join`,
  `Assert.Equal` and other `IEnumerable<T>` overloads read its elements.
- ⚠️ `Reconnect()` keeps the player id: other players see the player leave and return
  under the same id.

## [1.0.6] - 2026-09-18

### Added

- **Multiplayer runtime.** `NetworkManager` is the entry point for connecting, rooms,
  spawning, ownership, synchronised state and RPCs. Traffic travels over UDP, encrypted
  and authenticated: the API key is sealed to the server's public key, and the server's
  identity is pinned.
- **Rooms, lobbies and matchmaking.** Create a room or join one by id or code, list public
  rooms, browse lobbies, or let matchmaking seat players together. Room and player
  properties, host transfer and kicks.
- **Networked objects.** Spawn registered prefabs, transfer ownership, keep an object when
  its owner leaves, and pool frequently spawned objects with `INetworkObjectPool`.
- **State synchronisation.** `NetworkVariable` scalar and list types with per-variable
  send rates; `NetworkTransform` with interpolation and optional client-side prediction;
  interest management.
- **RPCs.** `[RtmpeRpc]` methods with a choice of target, custom argument types
  (`INetworkSerializable`) and awaited calls (`SendEnhancedRpcAsync`).
- **Scenes.** Networked scene loading with a readiness barrier across the room.
- **Reconnection.** `Reconnect()` resumes a dropped session with its reconnect token and
  rejoins the last room.
- **Drop-in components.** `RtmpeConnectionBootstrap` connects, enters a room and spawns
  the local player; `RtmpeSceneLoader` loads the scene the room asks for.
- **API keys.** `ApiKeySource` resolves the key at run time. The Setup Wizard stores it in
  the OS credential vault for the Editor, and can stage it for development builds.
- **Editor tooling.** The Setup Wizard, the Network Prefabs and Network Scenes windows, the
  Network Debugger, the Network Readiness window and the Conversion Wizard.
- **Roslyn analyzers.** Twenty diagnostics, four with an IDE quick fix, documented in
  [`Documentation~/diagnostics.md`](Documentation~/diagnostics.md).
- **Automation.** A headless engine under `Automation~/` that scores a project's network
  readiness and converts single-player scripts.
- **Samples.** Basic Connection, Player Spawn Flow, Scene Transitions and Two Player Room.

### Licence

- [`LICENSE.md`](LICENSE.md) is a limited, service-linked licence: the SDK may be
  installed, read, modified for your own integration and shipped inside an application
  that connects to the RTMPE service; it may not be used to implement, host or operate a
  server that speaks the RTMPE wire protocol. Third-party components included in the
  package keep their own terms, listed in `LICENSE.md` §6.
