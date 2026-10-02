# Changelog

All notable changes to the RTMPE Unity SDK are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and versions
follow `MAJOR.MINOR.PATCH`.

---

## [Unreleased]

## [1.0.8] - 2026-10-02

### Added

- **Host-only world objects.** When the server says which spawns the room's host sent
  (`CapabilityFlags.AttestedSpawnHost`), a world object is used only when the host spawned
  it; a world another player spawns is removed on every client running this version, and
  the room keeps the host's when the host runs this version too. `SpawnData.SpawnedByHost`,
  a `SpawnData` constructor that takes it, and `SpawnLifetimeFlags.SpawnedByHost` carry the
  server's word.
- **Per-player object limit.** The server limits how many of a room's objects one player
  may create; a spawn past it is reported through `OnSpawnRejected` with
  `SpawnRejectReason.PlayerAtObjectCeiling`. The room's host is not held to it.
- **`NetworkManager.DuplicateCopiesDroppedCount`.** Over UDP the server sends every message
  that happens once (a spawn, a despawn, an RPC, a reply) three times; the client applies
  the first copy and drops the others before decrypting them, and this counts what it
  dropped.
- **Additive scenes for late joiners.** `NetworkSceneManager.LoadScene` records the scenes
  the room has open (`ReservedPropertyKeys.SceneStack`), and a player who joins later is
  told each of them: the single-mode scene, then every additive scene over it.
- **Objects wait for the scene load.** While a client loads the room's scene in single
  mode, what the room sends about its objects is applied when it reports the load done
  (`NetworkSceneManager.ReportReady`), in the new scene and in the order it arrived; RPCs
  that arrive meanwhile run after the room's buffered events. `RtmpeConnectionBootstrap`
  spawns the local player then too.
- **Players' properties on entry.** Joining a room delivers the players' properties and
  property versions with the room, up to about 3 KB of them together, and this player's own
  version always.
- **Batched variable updates arrive batched.** Another player's batch of variable updates
  reaches this client as the one packet that player sent, where the server negotiates it
  (`CapabilityFlags.VariableBatchRelay`), instead of one packet per entry; its entries are
  applied in order, as single updates are.
- **The receive queue keeps room for control messages.** Under a flood, state frames and
  variable updates may fill at most 8000 of the queue's 10000 places; the rest stay for
  spawns, RPCs and room replies.
- **RPCs wait for their object.** An RPC that arrives before the object it addresses runs
  when the object spawns, including the `RpcTarget.AllBuffered` events a late joiner
  receives.
- **Teleports arrive at once for everyone.** `NetworkTransform.OwnerTeleportTo` sends the
  destination on the next tick, marked as a teleport where the server reads the mark
  (`CapabilityFlags.TransformTeleport`), and other players see the object arrive there
  instead of travelling there at their speed limit. One teleport per object per second is
  shown as one.
- **A tick's transforms travel together.** Where the server reads them as one packet
  (`CapabilityFlags.StateBatch`), the moving objects this client owns send the transforms
  they owe in a tick together, up to 21 to a packet (38 quantized), rather than one packet
  each, so a client moving many objects stays within what the server takes from one
  connection.

### Changed

- **Max Spawns Per Second** is counted for each player apart: one player's burst drops
  only that player's spawns. The room as a whole may spawn up to eight times the setting
  in a second.
- **Creating or joining a room from inside one leaves it first.** `CreateRoom`,
  `JoinRoom` and `JoinRoomByCode` called from inside another room send the leave, raise
  `OnRoomLeft` when the server confirms it, and then send the create or join. A room that
  cannot be left is reported through `OnRoomError`, and nothing else is sent.

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
