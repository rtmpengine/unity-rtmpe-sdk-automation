# RTMPE SDK — Documentation

> SDK Version: `com.rtmpe.sdk 1.0.8`

The RTMPE SDK connects a Unity game to the RTMPE real-time multiplayer service: players
meet in rooms, spawn networked objects and receive each other's state as it changes.

**New to the SDK?** Work in this order: create a project in the
[RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard), install the package,
run **Window → RTMPE → Setup Wizard**, then write the game code.
[Getting Started §3](getting-started.md#3-before-unity--create-your-project-in-the-dashboard)
begins with the dashboard step.

## Guides

| Page | What it covers |
| --- | --- |
| [Getting Started](getting-started.md) | From an empty project to players in a shared room: dashboard, installation, Setup Wizard, connecting, rooms, spawning, synchronised state, reconnection and object pooling. |
| [Architecture](architecture.md) | How the SDK is built: layers, threading, transport, security, connection lifecycle, late joining, reconnection and scene transitions. |
| [Performance Tuning](performance-tuning.md) | Tick rate, variable budgets, send rates, allocations, pooling and IL2CPP. |
| [Troubleshooting](troubleshooting.md) | Symptoms, causes and fixes for connection, synchronisation, RPC, platform and tooling problems. |
| [Automation](automation.md) | Scoring a project's network readiness and converting single-player scripts. |

## Reference

| Page | What it covers |
| --- | --- |
| [API Reference](api/index.md) | Every public type a game uses, with members, defaults and events. |
| [Analyzer Rule Reference](diagnostics.md) | Every `RTMPE####` diagnostic: what it detects, its severity and its quick fix. |

## Samples

Import the samples from **Window → Package Manager → RTMPE SDK → Samples**:

| Sample | What it shows |
| --- | --- |
| [Basic Connection](../Samples~/BasicConnection/README.md) | Connecting and disconnecting, with the connection state on screen. |
| [Player Spawn Flow](../Samples~/PlayerSpawnFlow/README.md) | Connect, open a room and spawn the local player from the generated prefab registry. |
| [Scene Transitions](../Samples~/SceneTransitions/README.md) | A room moving between two scenes with `RtmpeSceneLoader`. |
| [Two Player Room](../Samples~/TwoPlayerRoom/README.md) | Two clients in one room through matchmaking, each moving its own avatar. |
| [Shared World](../Samples~/SharedWorld/README.md) | Room state that belongs to no player, kept on the world object across a change of host. |

## Capabilities at a glance

- **Rooms, lobbies and matchmaking** — create, join by id or code, list public rooms, or
  let matchmaking seat players together.
- **Networked objects** — spawn registered prefabs; the owner drives them and every client
  sees the result. Objects can outlive their owner and pass to the host.
- **Synchronised state** — `NetworkVariable` values and lists, sent at the tick rate, with
  per-variable send rates.
- **Late joining** — a player who joins a room receives the current state of every object
  without any code on your side.
- **RPCs** — `[RtmpeRpc]` methods with caller restrictions, and server calls that return a
  reply.
- **Reconnection** — `Reconnect()` resumes a dropped session and rejoins the last room.
- **Scenes** — networked scene loading with a readiness barrier across the room.
- **Pluggable transport** — `NetworkManager.SetTransportFactory` replaces the built-in
  `UdpTransport`, for example with a test transport.
- **Object pooling** — `INetworkObjectPool` and `SpawnManager.SetObjectPool` replace
  `Instantiate` and `Destroy` for frequently spawned objects.
- **Editor tooling** — Setup Wizard, Network Prefabs and Network Scenes windows, Network
  Debugger with a link simulator, Roslyn analyzers and automated conversion.
