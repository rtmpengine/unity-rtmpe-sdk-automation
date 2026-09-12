# RTMPE SDK — Documentation

> SDK Version: `com.rtmpe.sdk 1.0.5`
> Protocol Version: v5

Welcome to the RTMPE SDK documentation.

**Starting from nothing?** The order is **dashboard → Unity → Setup Wizard →
readiness → conversion → runtime**. A project in the
[RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard) issues the API
key and the two gateway public keys, and nothing in Unity connects without them;
**Window → RTMPE → Setup Wizard** — which opens by itself once per Editor
session while that is left on — takes those values and writes the project's
configuration. [Quick Start §3](getting-started.md#3-before-unity--create-your-project-in-the-dashboard)
is that step in full.

## Sections

- [Quick Start](getting-started.md) — the route in order: create a project in the
  dashboard, install the SDK, run the Setup Wizard, then connect, spawn, sync,
  reconnect, pool
- [Architecture](architecture.md) — SDK layers, threading model, crypto flow, late-join, reconnect, scene transitions
- [Troubleshooting](troubleshooting.md) — common issues and diagnostic checklists
- [Analyzer Rule Reference](diagnostics.md) — every `RTMPE####` rule, its severity, and whether it has a quick fix
- [Automation](automation.md) — scoring a project, the conversion host, and the convert → re-score loop
- [Performance Tuning](performance-tuning.md) — tick rate, memory budget, pooling, IL2CPP tips
- [API Reference](api/index.md) — complete C# class and method reference
- Samples — runnable examples, imported from **Window → Package Manager → RTMPE
  SDK → Samples**: [Basic Connection](../Samples~/BasicConnection/README.md)
  (connect and disconnect), [Player Spawn Flow](../Samples~/PlayerSpawnFlow/README.md)
  (connect → room → spawn), [Scene Transitions](../Samples~/SceneTransitions/README.md)
  (a room moving between scenes), [Two Player Room](../Samples~/TwoPlayerRoom/README.md)
  (two clients in one room, built twice and run side by side)

## Protocol framing

Every packet carries a fixed binary header followed by its payload. The header
is written by `PacketBuilder` and read by `PacketParser`, and no application
code constructs or inspects one. The layout is a private contract between this
SDK and the RTMPE gateway rather than a public interface: it carries no
compatibility promise, it changes between wire generations without notice, and
the licence does not permit implementing, hosting or operating a server that
speaks it.

## Capabilities beyond the basics

- **Late-join state snapshot** — new joiners receive the full `NetworkVariable`
  state within one 30 Hz tick. Zero application-code changes required.
- **Pluggable transport** — `NetworkManager.SetTransportFactory(...)` replaces
  the built-in `UdpTransport`: a mock for integration tests, or a transport that
  reaches the gateway some other way.
- **Auto room re-join** — `LastRoomId` / `LastRoomCode` survive a
  token-preserving clear; `Reconnect()` rejoins automatically when
  `NetworkSettings.autoRejoinLastRoomOnReconnect` is `true` (default).
- **Scene transition pruning** — `NetworkObjectRegistry.PruneDestroyed()` is
  auto-called on scene load/unload, preventing dead-reference leaks.
- **Object pooling** — `INetworkObjectPool` interface plus
  `SpawnManager.SetObjectPool()` eliminate `Instantiate`/`Destroy` GC
  pressure for high-churn objects.
