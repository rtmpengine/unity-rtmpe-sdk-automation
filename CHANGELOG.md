# Changelog

All notable changes to the RTMPE Unity SDK are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and versions
follow `MAJOR.MINOR.PATCH`.

---

## [Unreleased]

## [1.0.7] - 2026-09-27

### Added

- **World objects.** `RtmpeWorldAuthority` and `RtmpeWorldSpawner` keep room state that
  no player owns and hand it to the next host. The **Shared World** sample shows both.
- **Server functions.** A call to an `RpcTarget.Server` method runs the project's server
  function, an HTTPS endpoint registered in the portal, and `SendEnhancedRpcAsync`
  returns its answer (`RpcResponse.TryReadResult`, `RpcErrorCode.Timeout`,
  `RpcErrorCode.Unavailable`).
- **RPC caller restrictions.** `[RtmpeRpc(…, Caller = …)]` limits who may call a method
  (`RpcCaller.Owner`, `RpcCaller.Host` or `RpcCaller.Server`), and a handler reads the
  caller through `CurrentRpcCaller` or `NetworkManager.CurrentRpcCallerFacts`. A method
  that declares a caller accepts calls only when the server vouches for the caller, and
  `NetworkManager.RpcCallerRefusedCount` counts the calls refused.
- **Late-join state.** A player who joins a room receives the current value of every
  `NetworkVariable`: updates for an object that has not spawned yet are held until it
  does, and every peer sends its state again one second after the join.
- **Link Simulator.** A Network Debugger panel that adds delay, jitter, loss and
  reordering to a Play session. `SimulatedLinkTransport` and `LinkConditions` apply the
  same conditions in tests.
- **Runtime checks.** The Editor records what it observes during Play — the connection,
  a shared room, both players, synchronised state and a reconnect — and the Network
  Readiness window shows the result.
- **Production step in the Setup Wizard.** Shows which API-key source a release build
  uses, and can write `Assets/RTMPE/RtmpeCredentialProvider.cs`.
- `NetworkVariableListBool`, `NetworkVariableListVector2` and
  `NetworkVariableListVector2Int`.
- Analyzer rule `RTMPE1030`: an `Instantiate` that creates a networked object without
  spawning it.
- Conversion Wizard: a `List<T>` field converts to a synchronised list, and
  **Applies on receivers** converts a method that applies state on every receiver into
  an RPC.
- `NetworkManager` readings, shown in the Network Debugger: `ReliableRetransmitsCount`,
  `ReliableRtoSeconds`, `ReliableSmoothedRttSeconds`,
  `EnhancedRpcDuplicatesReceivedCount`, `HeldVariableUpdateCount`,
  `StagedCatchUpPacketCount` and `SpawnsHeldForReturnCount`.
- A complete API reference, with revised guides, code documentation and Inspector
  tooltips.

### Changed

- ⚠️ A `NetworkVariableList` holds at most what one datagram carries, because a joining
  player receives it whole: 283 `int`s, 141 `Vector2Int`s or 94 `Vector3`s (a `string`
  list is bounded by bytes). `MaxCount` reports the limit, `Add` and `Insert` refuse past
  it, and `CanAdd` checks an element.
- ⚠️ `NetworkVariableList<T>` implements `IReadOnlyList<T>`, so `string.Join`,
  `Assert.Equal` and other `IEnumerable<T>` overloads read its elements.
- ⚠️ `Reconnect()` keeps the player id: other players see the player leave and return
  under the same id. A room whose last player dropped is kept for the reconnect token's
  lifetime, so that player can return to it.
- ⚠️ The automation kit's `make` commands read `APPLY` and `REPLICA_APPLY` from the
  command line only; set in the environment, they stop with an error.
- `NetworkVariableBase.SerializeWithId` returns whether it wrote an entry; a value that
  cannot be written stays dirty.

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
