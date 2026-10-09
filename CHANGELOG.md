# Changelog

All notable changes to the RTMPE Unity SDK are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and versions
follow `MAJOR.MINOR.PATCH`.

---

## [Unreleased]

## [1.0.10] - 2026-10-09

### Changed

- **The RPC reference says which members can reach an object.** An object spawned with the
  default `sharedAuthority: false` takes RPCs from its owner alone, and the server drops other
  members' calls without an answer. The RPC section, the `RpcCaller` table and the server-call
  section now say so.
- **A refused `Despawn` or `NetworkVariable` write names the owner as the one who hands the
  object over.** The message points to an RPC to the owner, or to the owner's
  `RequestOwnershipTransfer`.

## [1.0.9] - 2026-10-08

### Added

- **Declarative send rates.** `[NetworkVariable(SendRateHz = 10f)]` on the field or property
  that holds a variable now sets that variable's send rate, so a rate can be declared where
  the variable is declared instead of only assigned after it is constructed. The declaration
  is applied once per spawn, after `OnNetworkSpawn` has returned; setting `SendRateHz` on the
  instance still wins over it.

### Changed

- ⚠️ **`NetworkManager.Send` is main-thread only, and enforced.** Called from another
  thread it logs an error and sends nothing, and the SDK never seals a frame off the main
  thread. Queue a send from a worker thread with `MainThreadDispatcher.Enqueue`.
- **`OnReconnectFailed` reports every reconnect that ends without restoring the session**,
  including one whose session fails the SDK's validation (`DisconnectReason.Unknown`). It
  is not raised when the application ends the attempts with `Disconnect` or `Connect`.
- **Reconnect attempts follow the server's last word.** The lifetime the server states for
  a reconnect token is measured from the last authenticated frame it sent, so a session
  that outlives that lifetime still gets the configured number of reconnect attempts.
- **Hiding the `NetworkManager` pauses its attempt.** Deactivating its GameObject during a
  connect or reconnect pauses the attempt; activating it again resumes it with the time and
  backoff it had left.
- **The session token is judged by its own issue time.** Its `exp` and `nbf` claims are
  checked against its `iat`, so a device whose clock is wrong still connects.
- **`Disconnect()` after a dropped connection discards the reconnect token.** With no
  reconnect running, it forgets the session the SDK was keeping for `Reconnect()`.
- **`Send(data, reliable: true)` marks the frame reliable**, so the gateway acknowledges it
  once and the SDK stops resending it. `Compressed` and `AppSequence` flags set on a frame
  passed to `Send` are cleared.
- **A first-use server key is pinned once its session is established**, not when its
  challenge arrives. When the pin store cannot save it, the key is still kept for the rest
  of the process.
- **Leaving during the handshake tells the gateway** once it has opened a session, so that
  session is released at once rather than when it times out.
- **A socket error keeps the reconnect token**, as missed heartbeats do, so `Reconnect()`
  resumes the session, and a retry loop continues past an attempt that hit one. Only a first
  `Connect()` that hits one clears the token.
- **`OnReconnectFailed` reports the attempts actually made.** A retry loop that ended because
  its token was spent reported one attempt more than it made.
- **`--rtmpe-api-key` given with no value is reported** as a failed source, as an empty
  `--rtmpe-api-key-file` already was, instead of falling through to `RTMPE_API_KEY`.
- **The key file is read within a bound.** A `--rtmpe-api-key-file` holding more than 4,096
  bytes, or giving nothing to read within two seconds (a pipe nothing writes to), is
  reported as a failed source instead of holding the thread that connects.
- **A reconnect after the server closed the session is spread.** Its first attempt waits a
  random 0.1–1 s, so the clients a server restart disconnects do not all reconnect at once.
  A reconnect after any other ending still starts at once.
- **A changed server endpoint takes effect at the next attempt.** `serverHost`,
  `serverPort` and the socket buffer sizes set from code after the `NetworkManager` started
  are used by the next `Connect` or `Reconnect`, which also judges and pins the server's key
  for that endpoint.
- **A pin store that throws refuses the connection and says so.** The attempt ends at once
  with "Server not pinned — refusing handshake" naming the store's exception, instead of
  timing out as a socket that bound too late.
- **The connection-timeout line says whether the server's Challenge verified.** A Challenge
  that arrived and did not verify is reported as `ChallengeNotVerified`, which cannot tell a
  wrong pin from a frame that was not the gateway's; `ServerReplyNotFinalized` now means a
  verified Challenge.
- **Processes that share a device's pin file no longer lose each other's pins.** Saves and
  removals hold a lock file beside it, so an Editor and a player, or two players, can pin
  servers at the same time.
- **A state frame keeps every update after one it refuses.** An update refused for its
  value (a rotation that is not a rotation, a non-finite number) is skipped, and the rest
  of the frame is still applied.
- ⚠️ **`heartbeatIntervalMs` is at most 15 seconds.** A longer interval is used as 15 seconds,
  with a warning: the server releases the room seat of a session it has not heard from for 30
  seconds or more, and an idle client's heartbeats are all it hears.
- **The round trip is the last keep-alive's.** After a keep-alive had to be sent again, the
  round trip reported is the resend's, so one lost datagram no longer adds a whole interval
  to it or raises `OnRttSpikeDetected`.
- **The Basic Connection sample retries once per failure.** It schedules one retry, waits in
  real time, and leaves the SDK's own reconnect attempts to finish.
- **An empty `serverHost` is refused when connecting.** `Connect()` and `Reconnect()` log
  the problem and change no state, instead of staying in `Connecting`; the
  `NetworkManager` still starts, and a host set from code before connecting is used.
- **`OnDisconnected` carries the server's reason.** When the server ends the session, a
  `Disconnect()` called from code it runs on the way out, such as an `OnNetworkDespawn`,
  no longer replaces that reason with `ClientRequest`.
- **`OnConnected` is not raised for a connection already ended.** A handler of
  `OnStateChanged` that disconnects on the way to `Connected` is not then told
  `OnConnected`.
- **A full send buffer is waited out on every platform.** Where the operating system reports
  it as "would block" (Linux, Android, Windows) the connection used to end; the datagram is
  now sent again after a short wait, as on Apple platforms, and before anything queued
  behind it.
- **Brief send errors no longer end the connection.** A reset route, an interrupted call or
  an unreachable host costs only the datagram.
- **A reconnect chooses the server address for the network it is on.** After a change of
  network, the address with a route is chosen again on every connection attempt.
- **Stopping during a slow DNS lookup no longer stalls the caller.** `Disconnect()` while the
  server name is still being resolved returns at once.
- **Socket buffer settings are kept in range at run time.** `sendBufferBytes` is held to
  4096–4194304 bytes and `networkThreadBufferBytes` to at most 65536, also for assets that
  never pass through the Inspector.
- **A network change is noticed at the first missed heartbeat.** When the address this
  device sends from has changed (for example from Wi-Fi to cellular), the session ends with
  `DisconnectReason.ConnectionLost` and keeps its reconnect token, so a reconnect resumes it
  within seconds instead of after the liveness window.
- **IPv6-only networks reach an IPv4 server.** When the device has no IPv4 route, the
  transport uses the server's IPv6 address, or reaches its IPv4 address — a literal one
  included — through the network's NAT64 prefix.
- **A variable whose reliable message was given up on is sent again.** When a reliable
  message goes unacknowledged through every retransmission, every variable on the objects
  this client owns is sent again at the next flush, so a value written once does not stay
  stale at the other players.
- **`RtmpeConnectionBootstrap` issues one room entry at a time.** It enters no room while
  another entry is still unanswered, whoever started it, and `Restart()` is refused with
  `OnBootstrapFailed` until that entry has ended.
- **`RtmpeConnectionBootstrap` says why it stopped opening new sessions.** The report names
  how the last attempt ended.
- **`RtmpeConnectionBootstrap` waits out the player's previous seat.** A rejoin after a new
  session that the room refuses as full, within 70 seconds of the lost connection, is tried
  once more when the server has released the seat the lost connection held.
- **`RtmpeConnectionBootstrap` keeps waiting for `Restart()` across a drop.** After the
  player leaves a room, is removed from it, or is refused the room it asked for, a reconnect
  no longer enters a room on its own.
- **`RtmpeConnectionBootstrap` lets your own reconnect attempts run.** When the application
  has started `NetworkManager.Reconnect` after a drop, the component waits for how they end
  instead of opening a fresh session over them.
- **`RtmpeConnectionBootstrap` recovers on resume after a long suspension.** With Recover
  On Resume on, a session the app was away from for at least the heartbeat's liveness
  window is resumed at once, instead of after the heartbeat notices the silence.
- **`RtmpeConnectionBootstrap.Connect()` starts again.** It gives back the budget of fresh
  sessions, so a later drop is recovered, and enters a room once connected.
- **Entering Play mode without a domain reload starts clean.** A provider registered with
  `ApiKeySource.SetProvider` in the last play session is no longer consulted, and the
  `RtmpeCredentialProvider` the Setup Wizard now writes, like the Two Player Room sample,
  forgets the key supplied in the last play session.

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
