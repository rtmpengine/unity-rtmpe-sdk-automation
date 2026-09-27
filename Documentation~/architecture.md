# RTMPE SDK — Architecture

> SDK Version: `com.rtmpe.sdk 1.0.7`

This page explains how the RTMPE SDK works inside a Unity game: how it is organised, which
threads it uses, how it reaches the RTMPE server, and what happens to connections, networked
objects and state during a session. For setup, follow [Getting Started](getting-started.md);
for every public type and member, see the [API Reference](api/index.md).

## Table of Contents

1. [Layer Overview](#1-layer-overview)
2. [Threading Model](#2-threading-model)
3. [Transport Layer](#3-transport-layer)
4. [Security](#4-security)
5. [Connection Lifecycle](#5-connection-lifecycle)
6. [Domain Layer](#6-domain-layer)
7. [Object Lifecycle](#7-object-lifecycle)
8. [Late-Join Snapshot](#8-late-join-snapshot)
9. [Reconnect Flow](#9-reconnect-flow)
10. [Scene Transition Handling](#10-scene-transition-handling)
11. [Object Pooling](#11-object-pooling)
12. [Data Flow — Outbound](#12-data-flow--outbound)
13. [Data Flow — Inbound](#13-data-flow--inbound)

## 1. Layer Overview

The package has three parts:

| Part | Contents |
| --- | --- |
| `RTMPE.SDK.Runtime` assembly | Everything that runs in a player: connections, rooms, spawning, replication and RPCs. Root namespace `RTMPE`. |
| `RTMPE.SDK.Editor` assembly | Editor tooling: the windows under **Window → RTMPE** (Setup Wizard, Network Prefabs, Network Scenes, Network Readiness, Conversion Wizard and Network Debugger), the **Project Settings → RTMPE** pane, and checks that run when you build a player. |
| Analyzers | Roslyn analyzers and code fixes that check your scripts against the `RTMPE` rules as they compile. See the [Analyzer Rule Reference](diagnostics.md), and [Automation](automation.md) for the Conversion Wizard. |

Inside the runtime, game code talks only to the domain layer; each layer below hands the
next a lower-level form of the same data:

```text
┌──────────────────────────────────────────────────────────────────┐
│ YOUR GAME CODE                                                   │
│ MonoBehaviour and NetworkBehaviour subclasses                    │
└─────────────────────────────────┬────────────────────────────────┘
                                  │ method calls and C# events
┌─────────────────────────────────▼────────────────────────────────┐
│ DOMAIN                                                           │
│ NetworkManager, RoomManager, LobbyManager, MatchmakingManager    │
│ NetworkSceneManager, SpawnManager, OwnershipManager              │
│ NetworkObjectRegistry, INetworkObjectPool, NetworkBehaviour      │
│ NetworkVariable, NetworkTransform, NetworkTransformInterpolator  │
└─────────────────────────────────┬────────────────────────────────┘
                                  │ payloads
┌─────────────────────────────────▼────────────────────────────────┐
│ PROTOCOL                                                         │
│ PacketBuilder, PacketParser and the per-feature builders/parsers │
│ HeartbeatManager, ReliableChannel                                │
└─────────────────────────────────┬────────────────────────────────┘
                                  │ packets
┌─────────────────────────────────▼────────────────────────────────┐
│ SECURITY                                                         │
│ HandshakeHandler, SessionKeys, SealedApiKeyCipher                │
│ ServerKeyPinning and the server-key pin stores                   │
└─────────────────────────────────┬────────────────────────────────┘
                                  │ encrypted, authenticated datagrams
┌─────────────────────────────────▼────────────────────────────────┐
│ INFRASTRUCTURE                                                   │
│ NetworkTransport (abstract), UdpTransport (default)              │
│ NetworkThread, MainThreadDispatcher, Lz4Compressor               │
└─────────────────────────────────┬────────────────────────────────┘
                                  │ UDP
                             RTMPE server
```

### Gameplay-readiness components

These optional components do the common work of a multiplayer scene without code:

| Component | Add it with | What it does |
| --- | --- | --- |
| `RtmpeConnectionBootstrap` | **Add Component → RTMPE → Connection Bootstrap** | Connects, enters a room (create, join by id, or matchmaking) and spawns the local player. After a drop it reconnects, or opens a fresh session when the old one cannot be resumed. See [Connecting, without writing any](api/index.md#connecting-without-writing-any). |
| `RtmpeSceneLoader` | **Add Component → RTMPE → Scene Loader** | Loads the scene the room asks for and reports when it has loaded ([§10](#10-scene-transition-handling)). |
| `RtmpeWorldSpawner` and `RtmpeWorldAuthority` | **Add Component → RTMPE → World Spawner** and **Add Component → RTMPE → World Authority** | Give the room one world object, owned by the host, for state that no player owns ([The world object](#the-world-object)). |
| `InterestManager` | Add the component to a persistent GameObject | Reports the local player's position so the server delivers room broadcasts by proximity, and can drop incoming state from distant objects. See [Interest management](api/index.md#interest-management). |

## 2. Threading Model

The SDK uses two threads. A background network thread does socket I/O and nothing else;
everything that reads or changes game state runs on Unity's main thread.

```text
┌──────────────────────────────────────────────────────────────────┐
│ UNITY MAIN THREAD                                                │
│ NetworkManager.Update: tick loop, variable flushes, heartbeat    │
│ MainThreadDispatcher: 200 items a frame, at most 10,000 queued   │
│ Packet validation, decryption and dispatch                       │
│ Every SDK event and callback; spawn and despawn                  │
└─────────────────────────────────▲────────────────────────────────┘
                                  │ encrypted datagrams, both directions
┌─────────────────────────────────┴────────────────────────────────┐
│ NETWORK THREAD  "RTMPE-NetworkThread", AboveNormal priority      │
│ 1. send up to 100 queued datagrams                               │
│ 2. receive up to 100 datagrams; the first poll waits up to 4 ms  │
│    and returns as soon as one arrives                            │
│ 3. queue each datagram for the main thread                       │
└──────────────────────────────────────────────────────────────────┘
```

- Each `NetworkManager` has one network thread, started for a connection and stopped when
  the session ends. It never parses or decrypts, so all packet handling happens on the main
  thread.
- Outbound datagrams wait in a queue of at most **Send Queue Max Items** (4,096 by
  default). When the queue is full, the newest datagram is dropped and counted in
  `NetworkManager.SendQueueDroppedCount`.
- The SDK needs this background thread, which is why WebGL is not a supported platform —
  see [WebGL is not a supported platform](troubleshooting.md#webgl-is-not-a-supported-platform).

### Thread contract

| API | Thread | Notes |
| --- | --- | --- |
| `NetworkManager.Connect()`, `Reconnect()`, `Disconnect()` | Main thread only | Every public `NetworkManager` method has the same contract. |
| `NetworkManager.Send()` | Main thread only | Reads session state without synchronisation. The SDK copies the buffer you pass, so you may reuse it as soon as the call returns. |
| `NetworkVariable.Value` (write) | Main thread only | A write on a client that does not own the object is refused with a warning. |
| `NetworkVariable.Value` (read) | Main thread only | A plain field read with no memory barrier: read from another thread, a multi-field value such as a `Vector3` or `Quaternion` can be seen half-written. |
| `NetworkObjectRegistry` | Main thread only | Detects destroyed objects with Unity's null check, which works only on the main thread. |
| SDK events and callbacks | Raised on the main thread | Every `NetworkManager` and manager event, `OnValueChanged`, and the `NetworkBehaviour` hooks. |
| `MainThreadDispatcher.Instance.Enqueue()` | Any thread | Hands work to the main thread. The dispatcher is created on first access, which must happen on the main thread: call `MainThreadDispatcher.Prewarm()` from `Awake` before a worker thread uses it. |
| `NetworkTransport` implementations | Network thread and main thread | `Connect`, `Send`, `Poll` and `Receive` run on the network thread; the main thread can call `Disconnect` and `Dispose` and read `LocalEndPoint` at the same time. An implementation must be safe to use from both threads and must not call Unity APIs. |

## 3. Transport Layer

A transport moves datagrams between the network thread and the RTMPE server. The SDK
ships one, `UdpTransport`, and uses it unless you install another.

### UdpTransport

All traffic — connection setup, rooms, RPCs and replication — travels over one UDP socket
to **Server Host** and **Server Port** (7777 by default) from the `NetworkSettings` asset.

| Property | Value |
| --- | --- |
| Address family | IPv4 preferred; IPv6 when the host resolves to IPv6 addresses only |
| Name resolution | Once per transport, with a 3-second timeout (`UdpTransport.DefaultDnsTimeout`) |
| Largest datagram | 1,200 bytes (`UdpTransport.DefaultMaxDatagramSize`), small enough to cross an IPv6 path without fragmentation. A larger send is refused with an `ArgumentException`. |
| Socket buffers | **Send Buffer Bytes** and **Receive Buffer Bytes**, 262,144 bytes (256 KiB) each by default |
| Largest payload | `PacketBuilder.MaxApplicationPayloadBytes`, so every packet fits in one datagram; a larger payload is refused with an `ArgumentException` where the packet is built |
| Source filtering | A datagram from any address other than the server's is dropped before decryption |

A socket error confined to one datagram, such as an oversized datagram or an ICMP
unreachable report, drops that datagram and the session continues
(`NetworkManager.PerPacketFaultCount`); a full kernel send buffer delays a datagram instead
(`NetworkManager.EnobufsCount`). Any other socket error ends the session with
`DisconnectReason.ConnectionLost` and clears the reconnect token.

### Reliable delivery

Room operations, spawns, despawns, RPCs, ownership requests and variable updates are sent
reliably: `ReliableChannel` keeps each frame until the server acknowledges it and re-sends
it when a timeout expires. Transform updates are sent once, because each one supersedes
the last.

| Setting | Default |
| --- | --- |
| Retransmit timeout | 200 ms until the first round trip is measured, then the measured round-trip time plus its variation (at least 100 ms). Each re-send of a frame doubles its wait. |
| Timeout ceiling | 2 s |
| Attempts | 8. A frame still unacknowledged after that is dropped and counted in `NetworkManager.ReliableSendsDroppedCount`. |
| Frames in flight | 64, of which 16 are kept for control frames (room operations, spawns, despawns and RPCs), so variable updates cannot fill the window. A variable update that finds no free slot waits for the next tick. |

`NetworkManager.ReliableRtoSeconds`, `ReliableSmoothedRttSeconds` and
`ReliableRetransmitsCount` report the channel. Reliable delivery needs **Emit Arq Sequence**
in the `NetworkSettings` asset (on by default) and a server that acknowledges reliable frames;
without either, a reliable send goes out once and the SDK logs a warning the first time.

### Pluggable transports

`NetworkManager.SetTransportFactory` installs a `TransportFactoryFn` that builds the
transport the next connection attempt runs on, and `NetworkManager.ClearTransportFactory()`
restores `UdpTransport`. A change made during a session takes effect at the next attempt,
so install the factory before `Connect()`. If the factory throws or returns `null`, the SDK
logs it and uses `UdpTransport` instead. The manager owns and disposes the transport the
factory returns. A transport derives from `NetworkTransport` and must be safe to use from both
threads ([§2](#2-threading-model)). Its `Receive` must not block: it returns the number of
bytes read, 0 when nothing is waiting, or `NetworkTransport.ReceiveSourceRejected` for a
datagram it discarded.

A factory replaces the socket, not the network thread above it, so it does not make WebGL
a supported platform — see
[WebGL is not a supported platform](troubleshooting.md#webgl-is-not-a-supported-platform).

`SimulatedLinkTransport` wraps another transport and adds delay, jitter, loss and optional
reordering; the Link Simulator panel of **Window → RTMPE → Network Debugger** applies one to
the next session. See
[Testing under a bad link](troubleshooting.md#testing-under-a-bad-link--the-link-simulator).

## 4. Security

Every connection is authenticated, and every packet after the handshake is encrypted. You
configure this layer with two server public keys from the dashboard, which the Setup Wizard
writes into the `NetworkSettings` asset. Where the API key itself comes from at run time is
described in
[Giving a player build its API key](getting-started.md#giving-a-player-build-its-api-key).

| Mechanism | What it does |
| --- | --- |
| Sealed API key | The API key is sealed to the server's X25519 public key (**Api Key Seal Server Public Key Hex**) before it leaves the device, so only the server can read it and a build carries no secret of the server's. Without that key the SDK does not connect: the attempt fails at once with `DisconnectReason.ProtocolError`. |
| Session keys | Each connection agrees fresh encryption keys through an ephemeral X25519 exchange. |
| Server identity | The server signs the handshake with its Ed25519 identity key. The SDK verifies the signature and checks the key against its pin. |
| Encryption | Every packet after the handshake is encrypted and authenticated with ChaCha20-Poly1305. A packet that fails authentication is dropped and a warning is logged, at most once a second. |
| Replay protection | A sliding window over the last 1,024 packets drops any packet that arrives twice or too late. These drops are logged the same way. |
| Session token | The token the server issues when the session opens is checked against **Expected Jwt Issuer**, **Expected Jwt Audience** and its validity period (allowing **Jwt Clock Skew Seconds**). A token that fails the check ends the attempt with `DisconnectReason.Unknown`. |
| Session limit | One session's keys protect at most 2³² outbound packets. The SDK logs a warning when fewer than 1,048,576 remain, so the game can reconnect at a convenient moment; at the limit it disconnects with `DisconnectReason.NonceExhausted`, which clears the reconnect token, and a new `Connect()` starts a session with new keys. |
| Compression | Payloads between 128 bytes and 16 KiB are compressed with LZ4 (`Lz4Compressor`) before encryption when compression helps, and decompressed after decryption. Game code never sees compressed data. |

### Server pinning

**Server Pinning Mode** chooses where the pin comes from. In every mode that has a pin, a
server that presents a different key is refused.

| Mode | Where the pin comes from |
| --- | --- |
| `Strict` (default) | **Pinned Server Public Key Hex**. With no pin configured, the SDK refuses to connect. Use this mode for every build you ship. |
| `TrustOnFirstUse` | The key the server presents on the first connection to each host and port, kept in a device-bound pin store (`NetworkManager.PinStore`). With **Require First Use Provisioned** on, the pin must already be in the store. |
| `InsecureNoPinning` | No pin: any valid signature is accepted, and a warning is logged on each connection. For local testing only. |

## 5. Connection Lifecycle

`NetworkManager.State` moves through six states. `Disconnect()`, heartbeat loss, the session
limit and a session token that fails its check end a session through `Disconnecting`; the
server ending the session or a socket failure goes straight to `Disconnected`. `Reconnecting`
is entered only through `Reconnect()` ([§9](#9-reconnect-flow)).

```text
 Disconnected ── Connect(apiKey) ──▶ Connecting ── handshake complete ──▶ Connected
  │  ▲  ▲  ▲                             │                                   │     ▲
  │  │  │  └─────────────────────────────┘                      room entered │     │ LeaveRoom()
  │  │  │     timeout, refusal or socket error                               ▼     │
  │  │  │                                                                 InRoom ──┘
  │  │  └── Disconnecting ◀── Disconnect(), heartbeat loss, session limit, failed token check
  │  └───── server ends the session, or socket error
  │
  └──▶ Reconnect() ──▶ Reconnecting ── resumed ──▶ Connected ── re-join ──▶ InRoom
                            └── attempt failed ──▶ Disconnected ──▶ next attempt,
                                or OnReconnectFailed when none remains
```

| Event | Raised |
| --- | --- |
| `OnStateChanged(previous, current)` | On every transition. |
| `OnConnected` | On every entry to `Connected`: after the handshake, after a reconnect, and after leaving a room. |
| `OnConnectionFailed(message)` | When a connection attempt times out or its handshake is refused, and when the socket fails during `Connecting`; raised just before the move to `Disconnected`. |
| `OnDisconnected(reason)` | On every entry to `Disconnected`, including between reconnect attempts. |
| `OnReconnectFailed(attempts)` | When the `Reconnect()` loop gives up. |

When a room is entered or left, the state changes before `Rooms.OnRoomCreated`,
`Rooms.OnRoomJoined` or `Rooms.OnRoomLeft` reaches your handlers. `Connect()` is accepted
only in `Disconnected`, and it cancels a `Reconnect()` loop that is waiting between attempts.
`Connect()` and `Reconnect()` are refused with an error while the `NetworkManager`
component is disabled or its GameObject is inactive.

### Heartbeat and liveness

`HeartbeatManager` sends a heartbeat every **Heartbeat Interval Ms** (5,000 ms by default),
and each answer updates the round-trip time (`NetworkManager.LastRttMs`,
`NetworkManager.OnRttUpdated`). The session is declared lost only when three heartbeats in a
row go unanswered **and** no authenticated answer has arrived for the liveness grace
(**Heartbeat Liveness Grace Ms**). The default, 0, means twice the three-heartbeat window:
30 seconds at the default interval. A configured value is never shorter than three
intervals.

A lost session goes `Disconnecting` → `Disconnected` with `DisconnectReason.ConnectionLost`
and keeps the reconnect token, so `Reconnect()` can resume it.

## 6. Domain Layer

### Managers

| Component | Access | Role |
| --- | --- | --- |
| `NetworkManager` | A component in your boot scene; `NetworkManager.Instance` | The entry point. Owns the connection state machine, the network thread and the managers below. It survives scene loads; a second instance destroys itself with a warning. `Instance` returns the scene's manager and never creates one. |
| `RoomManager` | `NetworkManager.Rooms` | Creates, joins, lists and leaves rooms; room and player properties; host changes and kicks; room events. |
| `LobbyManager` | `NetworkManager.Lobby` | Room lists with filters, and pushed updates while the client is in a lobby. |
| `MatchmakingManager` | `NetworkManager.Matchmaking` | Places the player in a room with players who ask for the same mode. |
| `NetworkSceneManager` | `NetworkManager.Scene` | Moves the whole room between scenes ([§10](#10-scene-transition-handling)). |
| `SpawnManager` | `NetworkManager.Spawner` | Prefab registration, spawning and despawning, the optional object pool, and what happens to a leaving player's objects. |
| `OwnershipManager` | `Spawner.Ownership` | Ownership queries, transfer requests, and the hand-over of a departed player's surviving objects. |
| `NetworkObjectRegistry` | `Spawner.Registry` | Maps each network object id to its `NetworkBehaviour`. Main thread only. |

`NetworkManager` builds fresh room, lobby, matchmaking and spawn managers for every
`Connect()` and every reconnect attempt. Handlers attached to their events and registered
prefabs move to the new instances; the object pool, and anything else set on `Spawner`, must
be set again after connecting. Read `NetworkManager.Rooms` and the other properties when you
need them rather than keeping the instances.

### NetworkBehaviour

Every networked script derives from `NetworkBehaviour`. An object can carry several, and the
SDK initialises and spawns all of them; `NetworkTransform` is one too. Each exposes the
object's identity — `NetworkObjectId`, `OwnerPlayerId`, `IsOwner` (true only on the owner's
client) and `IsSpawned`. `DestroyWithOwner` (true by default) applies to the whole object: the
spawn sends the value of the object's first `NetworkBehaviour`, and every client applies it to
all of the object's components. Set it on every `NetworkBehaviour` of the object in `Awake`,
as `RtmpeWorldAuthority` does.

| Hook | Called |
| --- | --- |
| `OnNetworkSpawn()` | On every client when the object is spawned there. Create its `NetworkVariable`s here. |
| `OnNetworkDespawn()` | On every client before the object is removed. Unsubscribe here. |
| `OnOwnershipChanged(previousOwner, newOwner)` | When the owner actually changes. |
| `OnFixedTick(deltaTime)` | Once per simulation tick on the owner, whatever the frame rate. |
| `OnDestroy()` | Unity's hook. An override must call `base.OnDestroy()` so the SDK can release the object (rule [`RTMPE1020`](diagnostics.md#RTMPE1020)). |

### NetworkVariable

A `NetworkVariable` is a value the owner writes and every client receives.

- The owner assigns `Value`; the change is sent on the next tick (**Tick Rate**, 30 Hz by
  default). To send one variable less often, set its `SendRateHz` after creating it.
- Every client, the owner included, raises `OnValueChanged(previous, current)` on the main
  thread when the value changes.
- A write is refused, with a warning, on a client that does not own the object, outside the
  object's spawned life, or for a value the variable cannot send (a non-finite float).
- Create variables in `OnNetworkSpawn` and pass the field name with `nameof`. The identity
  is derived from the component's type and the member name, so it is unique across the
  object, and every player needs the same build.
- Synchronised lists (`NetworkVariableList<T>`) send each edit, and a full copy on every
  join and every 5 s by default. See [NetworkVariable types](api/index.md#networkvariable-types).

**Writing your own variable type.** `NetworkVariable<T>` is public and abstract, for a
struct `T` that implements `IEquatable<T>`. Derive from it and implement
`Serialize(BinaryWriter)` and `Deserialize(BinaryReader)`. `Deserialize` applies the value
it reads through `ApplyFromWire`, which stores it, raises `OnValueChanged` on the receiving
client and leaves the variable clean, so a receiver never sends back what it was told. The
base constructor takes the owner and the member name, so a derived type declares a
constructor:

```csharp
using System.IO;
using RTMPE.Core;
using RTMPE.Sync;

public sealed class NetworkVariableByte : NetworkVariable<byte>
{
    public NetworkVariableByte(NetworkBehaviour owner, string memberName, byte initialValue = 0)
        : base(owner, memberName, initialValue) { }

    public override void Serialize(BinaryWriter writer) => writer.Write(Value);
    public override void Deserialize(BinaryReader reader) => ApplyFromWire(reader.ReadByte());
}
```

Do not apply received values with `SetValueWithoutNotify`, which is for seeding a value from
your own code and raises nothing. To refuse values the type cannot carry, override
`IsSendableValue`: `Serialize` cannot refuse.

### NetworkTransform and NetworkTransformInterpolator

`NetworkTransform` replicates position and rotation, and scale if enabled, from the owner.
The owner sends a changed pose at most once per tick, when it has moved further than
**Position Threshold** (0.01 units) or turned further than **Rotation Threshold** (0.1°),
and re-sends an unchanged pose about once a second. Client-side prediction (**Enable
Prediction**) is off by default.

`NetworkTransformInterpolator`, which Unity adds with `NetworkTransform`, renders the copies
on other clients slightly in the past — **Interpolation Delay**, 0.1 s by default, which
**Adaptive Delay** (on by default) shortens on a steady link — and extrapolates for at most
**Max Extrapolation Seconds** (0.05 s) when no newer state has arrived.

### RPCs and physics

| Feature | Types | Summary |
| --- | --- | --- |
| RPCs | `[RtmpeRpc]` methods on a `NetworkBehaviour` | `RPC(nameof(Method), args)` sends a call. `RpcTarget` chooses who receives it (`All`, `Others`, `Server`, `AllBuffered`) and `Caller` restricts who may send it (`Anyone`, `Owner`, `Host`, `Server`). `NetworkManager.SendEnhancedRpcAsync` awaits the answer of a server function. See [Remote procedure calls](api/index.md#remote-procedure-calls). |
| Physics | `NetworkRigidbody`, `NetworkRigidbody2D` | Replicate a rigidbody's physics state from its owner. See [NetworkRigidbody / NetworkRigidbody2D](api/index.md#networkrigidbody--networkrigidbody2d). |

## 7. Object Lifecycle

Networked objects are spawned from registered prefabs. Prefab ids come from the generated
prefab registry assigned to **Prefab Registry** in the `NetworkSettings` asset, or from
`Spawner.RegisterPrefab(id, prefab)`; registrations stay in place across reconnects.

```text
 Spawner.Spawn(prefabId, position, rotation)       on the owner, after Rooms.OnRoomJoined
   ├── pool.Acquire(...), or Object.Instantiate(...) when no pool is installed
   ├── a network object id is assigned
   ├── OnNetworkSpawn() on every NetworkBehaviour of the object
   ├── NetworkObjectRegistry.Register(...)
   └── the spawn is sent to the room
         │
         ▼
 every other client in the room
   ├── pool.Acquire(...) or Object.Instantiate(...)
   ├── OnNetworkSpawn() on every NetworkBehaviour of the object
   └── NetworkObjectRegistry.Register(...)

 Spawner.Despawn(objectId)                          on the owner
   ├── OnNetworkDespawn() on every NetworkBehaviour of the object
   ├── pool.Release(...), or Object.Destroy(...) when no pool is installed
   └── the despawn is sent; every other client tears its copy down the same way
```

- **Who may spawn.** A client spawns objects under its own identity only, from inside a room:
  a spawn made before the join reply exists on that client alone, with a warning. Spawn
  gameplay objects from `Rooms.OnRoomJoined`.
- **Refused spawns.** `Spawner.OnSpawnRejected(objectId, reason)` reports a spawn the room
  refused. The object stays on the spawning client only; destroy it if you cannot use it.
- **Who may despawn.** Only the owner's despawn reaches the room. `Despawn` refuses, with
  an error, an object another player owns; `CanDespawn` and `TryDespawn` ask the same
  question without the error.
- **Limits.** Spawns are limited by **Max Spawns Per Second** (100 by default) and **Max
  Spawns Per Room** (5,000 live objects by default). Object ids are assigned by the
  spawning client, without a round trip to the server.
- **Leaving.** Leaving a room, or losing the session, despawns every networked object
  locally: `OnNetworkDespawn` runs, and each object is destroyed or released to the pool.

### Ownership

- The client that spawns an object owns it, and `IsOwner` is true only there.
- The owner hands an object on with
  `OwnershipManager.RequestOwnershipTransfer(objectId, newOwnerPlayerId)`. The server checks
  the request and announces the grant to every client, and ownership changes only when the
  grant arrives. If nothing answers within ten seconds, `OnOwnershipTransferUnanswered`
  fires; a later grant is still applied.
- When a player leaves, every client tears down that player's objects that have
  `DestroyWithOwner` set and hands the rest to the room host itself, with no message per
  object. If the host left, the hand-over completes when the new host is announced.
- `Rooms.TransferMasterClient` moves the host role only; objects stay with their owners.

## 8. Late-Join Snapshot

`NetworkVariable` replication sends changes. Without help, a player who joins a running
room would see default values until each owner wrote again — which never happens for values
that rarely change, such as a name or a level layout. The SDK closes that gap with no code
in your game.

```text
 Existing client A                  RTMPE server                     New client B
         │                                │                                │
         │                                │◀────────────── joins the room ─│
         │◀───────────── player B joined ─│── join reply ─────────────────▶│
         │                                │── room's objects ─────────────▶│
         │  marks every owned             │                                │
         │  variable dirty                │                                │
         │── next tick ──────────────────▶│── snapshot ───────────────────▶│
         │── one second later ───────────▶│── snapshot again ─────────────▶│
```

### What a joiner receives

- **The room.** The join reply (`Rooms.OnRoomJoined`) carries the roster and the room's
  current properties, including its scene.
- **Objects.** Every live object in the room is spawned on the joiner. For 1.5 seconds after
  the join reply these spawns count against **Max Spawns Per Room** only, not **Max Spawns
  Per Second**, so a large room arrives whole.
- **Variable state.** When `Rooms.OnPlayerJoined` fires on an existing client, the SDK marks
  every variable on the objects that client owns as dirty
  (`SpawnManager.MarkAllVariablesDirtyForResync()`), so its next flush sends a full
  snapshot. It sends the snapshot again one second later, because the first copy can be
  lost on its way to the joiner and nothing else re-sends a value that has not changed.
- **Transforms.** An owner re-sends its pose about once a second even when nothing moves,
  so the joiner also receives the pose of a motionless object.
- **Buffered events.** RPCs sent with `RpcTarget.AllBuffered`
  ([below](#events-that-reach-the-late-joiner)).

Nothing is applied before the join reply: objects, despawns and buffered events that arrive
ahead of it are held and released in arrival order once the room is entered. A variable
update for an object the joiner does not have yet is held too — for at most ten seconds,
oldest dropped first when the hold is full — and applied when the object spawns, after its
`OnNetworkSpawn` has created its variables.

### Properties

- A resync raises nothing on the owner. On the joiner, each value is applied like any other
  update — through `ApplyFromWire` for the scalar types — and raises `OnValueChanged` only
  when it differs from the joiner's current value.
- **Latency to snapshot:** the next tick after the join is announced (about 33 ms at the
  default 30 Hz), once the joiner is a receiver of the room's traffic. A copy sent before
  the joiner becomes a receiver does not reach it; the second copy, one second later, then
  delivers the snapshot.
- **Cost:** two extra flushes per join. Every synchronised list the client owns is sent
  whole both times and raises `OnListChanged` with `NetworkListChangeKind.FullSync` on
  each receiver, so treat that event as "read the list", not "the list changed".

### Events that reach the late joiner

An RPC sent with `RpcTarget.All` or `RpcTarget.Others` reaches only the players present. One
sent with `RpcTarget.AllBuffered` is also stored by the server for the room, and a player who
joins later receives the stored events in the order they were sent, before any live RPC that
arrives meanwhile; the SDK spreads a long backlog over several frames.

The store is bounded: when it is full the oldest events are dropped first, and a late joiner
is not told. Use `AllBuffered` for the few events a late joiner cannot do without, such as a
door opened or a round started, and keep running totals in a `NetworkVariable`.

### The world object

State that no player owns — pickups, a generated layout, the current round — lives on the
room's world object: a prefab carrying `RtmpeWorldAuthority` beside the `NetworkBehaviour`
that holds the world's variables.

- `RtmpeWorldSpawner` spawns it from the host: at once when the host entered the room alone,
  and otherwise only if no world has arrived 1.5 seconds after the host entered or was
  promoted. A player who is not the host never spawns it.
- It survives its owner (`DestroyWithOwner` is off on every `NetworkBehaviour` of the
  object). When the host leaves, the new host takes over the same object if the server hands
  it the departed host's objects; otherwise the new host spawns a copy that carries the
  world's state and removes the original. Later joiners receive the world with the room's
  other objects.
- It is spawned with shared authority by default (**Shared Authority**), so any player can
  send its RPCs; its variables are written by its owner, the host. It survives scene loads
  by default (**Persist Across Scene Loads**).
- Find it with `RtmpeWorldAuthority.Find(key)` each time you need it, and learn of a new
  instance from `RtmpeWorldAuthority.OnWorldReady`. Populate a brand-new world in its
  `OnWorldBorn` event, which runs on the owner. Do not keep a reference across frames: the
  instance for a key can change.

See [Shared world state](getting-started.md#shared-world-state) for a walkthrough, and
[RtmpeWorldAuthority and RtmpeWorldSpawner](api/index.md#rtmpeworldauthority-and-rtmpeworldspawner)
for the full API.

## 9. Reconnect Flow

After the handshake the SDK holds a reconnect token, and `NetworkManager.CanReconnect` is
true while it does. `Reconnect()` presents the token to resume the session without the API
key. `NetworkManager` never reconnects on its own: call `Reconnect()` from your
`OnDisconnected` handler when `CanReconnect` is true, or let `RtmpeConnectionBootstrap` do
it (**Reconnect On Drop**, on by default).

### When the token survives

| What ended the session | `DisconnectReason` | Token and last room |
| --- | --- | --- |
| Heartbeat liveness lost | `ConnectionLost` | Kept |
| The server closed the session, for example when it restarts | `ServerRequest` | Kept |
| A `Reconnect()` attempt timed out before a validated answer from the server arrived | `Timeout` | Kept |
| The pinning configuration refused a `Reconnect()` attempt | `ProtocolError` | Kept |
| You called `Disconnect()` | `ClientRequest` | Cleared |
| A first `Connect()` timed out | `Timeout` | Cleared |
| A `Reconnect()` attempt timed out after a validated answer from the server arrived | `Timeout` | Cleared |
| A socket error | `ConnectionLost` | Cleared |
| The server kicked the player | `Kicked` | Cleared |
| The server reported a protocol error, or the handshake failed for any other reason | `ProtocolError` | Cleared |
| The session token failed its check, or the server sent reason `Unknown` | `Unknown` | Cleared |
| The session limit was reached | `NonceExhausted` | Cleared |

- A server disconnect that names no reason, or a reason this SDK does not recognise, is
  reported as `ServerRequest`.
- `ConnectionLost`, `Timeout` and `ProtocolError` appear on both sides of the table, so
  decide from `CanReconnect`, not from the reason. The enum is described in the
  [API reference](api/index.md#disconnectreason-enum).
- `Rooms.LeaveRoom()` clears the last room and keeps the token.

### The retry loop

- `Reconnect()` returns `false` and does nothing when no token is held, the state is not
  `Disconnected`, a loop is already running, or the `NetworkManager` is disabled.
- Otherwise it enters `Reconnecting` and makes up to **Max Reconnect Attempts** (5 by
  default), each with **Connection Timeout Ms** to complete — or a single attempt when the
  token is older than the lifetime the server stated for it. Between attempts the state
  returns to `Disconnected`, `OnDisconnected` fires, and the loop waits a random back-off
  whose ceiling starts at 1 s and doubles up to 30 s, in real time even while the game is
  paused.
- When the last attempt fails, the SDK clears the token and fires
  `OnReconnectFailed(attempts)`; call `Connect(apiKey)` to start a new session.
  `RtmpeConnectionBootstrap` does this for you (**Fresh Session When Recovery Fails**, on by
  default).
- While the loop runs, another `Reconnect()` is ignored with a warning. `Disconnect()` cancels
  the loop; `Connect()` cancels it only between attempts, and is refused with a warning while
  an attempt is in progress.

### Automatic room re-join

The SDK records each room it enters (`NetworkManager.LastRoomId`, `LastRoomCode`). After a
successful reconnect, when **Auto Rejoin Last Room On Reconnect** is on (the default), it
raises `OnAutoRejoinAttempt(roomId)` and joins that room; the outcome arrives on
`Rooms.OnRoomJoined` or `Rooms.OnRoomError`. Turn the setting off to offer your own
"Rejoin?" prompt instead.

- The other players see the player leave and join again, and it keeps its player id. Its
  networked objects were torn down when the session was lost ([§7](#7-object-lifecycle)), so
  spawn the player again from `Rooms.OnRoomJoined`, as on any entry.
- A room the reconnecting player was alone in is kept open for the lifetime of the reconnect
  token, so the re-join finds it. If the room cannot be joined — it has filled up or closed —
  `Rooms.OnRoomError` reports it.

## 10. Scene Transition Handling

`NetworkManager` survives scene loads, and so do `RtmpeConnectionBootstrap`,
`RtmpeSceneLoader` and, by default, the world object. Spawned objects are ordinary scene
objects: a single-mode load destroys those in the scene it unloads, and an additive load
destroys nothing.

```text
 SceneManager.LoadScene("Level2")                              single mode
      ▼
 Unity destroys the objects of the unloaded scene; each networked object's
 NetworkBehaviour.OnDestroy removes it from the SDK's bookkeeping
 (OnNetworkDespawn does not run)
      ▼
 sceneUnloaded, then sceneLoaded: NetworkObjectRegistry.PruneDestroyed()
 removes any registry entry whose GameObject has been destroyed
```

These steps are local. The room still holds the objects: other clients keep their copies
unless their own scene load destroys them, and a player who joins later still receives
them. To remove an object for everyone, call `Spawner.Despawn(objectId)` before loading the
scene. The world object is the exception: when its owner's copy is destroyed this way, it
reports the removal to the room.

### Networked scenes

To move the whole room to another scene, the host calls
`NetworkManager.Scene.LoadScene(sceneName, mode)`. Every client, the host included, receives
`OnSceneLoadStarted` and `OnSceneLoadStartedWithMode`, loads the scene and calls
`ReportReady()`. `OnAllPlayersSceneLoaded` fires when every player has reported, and
`OnSceneLoadTimedOut` fires if that has not happened within **Scene Ready Timeout Seconds**
(60 s by default). A player who joins later is told the room's current scene with the join.

`RtmpeSceneLoader` does the loading and the reporting for you. See
[Networked scenes](api/index.md#networked-scenes) and
[Scene loading, without writing any](api/index.md#scene-loading-without-writing-any).

## 11. Object Pooling

The SDK has no built-in pool: without one, spawns use `Object.Instantiate` and despawns use
`Object.Destroy`. For objects that come and go often — projectiles, effects, short-lived
props — implement `INetworkObjectPool` and install it with `Spawner.SetObjectPool(pool)`. Its
`Acquire(prefabId, prefab, position, rotation)` returns an instance of the prefab, and
`Release(prefabId, instance)` takes one back.

- Install the pool after every connect — in `OnConnected`, for example — because each
  `Connect()` and reconnect attempt builds a new spawn manager without one.
- Every spawn, local or from the room, calls `Acquire`, and the SDK positions and activates
  the instance it gets back. Every despawn calls `Release`.
- Variables created in `OnNetworkSpawn` are discarded at despawn, and your `OnNetworkSpawn`
  creates them again on the next spawn. Variables created elsewhere (a field initialiser,
  `Awake` or `OnEnable`) stay on the instance, so before `Release` the SDK returns them to
  their initial values, raising the change event where a value changes.
- A prefab id of `uint.MaxValue` in `Release` means the SDK does not know the prefab, and
  the pool should destroy the instance.
- If `Acquire` returns `null`, the SDK logs an error and instantiates the prefab for that
  spawn. If `Release` throws, it logs the exception and destroys the instance.
- You can swap pools at run time: a live object goes to whichever pool is installed when it
  despawns. `Spawner.ClearObjectPool()` returns to instantiate and destroy.

A complete example is in
[Step 12 — Object Pooling](getting-started.md#step-12--object-pooling-optional).

## 12. Data Flow — Outbound

Example: the owner moves its player, and `NetworkTransform` sends the new pose.

1. **Sample.** In `Update`, the owner's `NetworkTransform` compares the pose with the last
   one sent. If it moved past a threshold, or the once-a-second refresh is due, and nothing
   went out this tick, `TransformPacketBuilder` builds an update — a smaller, quantised one
   when **Quantize Transforms** is on.
2. **Refuse what cannot be sent.** A pose with a non-finite value is refused: nothing is
   sent, and the last pose sent stays the baseline for measuring later movement.
3. **Frame, compress and encrypt.** `NetworkManager` wraps the payload in a packet with
   `PacketBuilder`, compresses it with `Lz4Compressor` when that helps, and encrypts and
   authenticates it with the session's keys.
4. **Queue.** The encrypted datagram joins the network thread's send queue. A transform
   update is sent once; a reliable frame also stays in `ReliableChannel` until it is
   acknowledged.
5. **Send.** The network thread hands the datagram to the transport — `UdpTransport.Send`,
   or your own transport — which sends it to the server.

Variable updates take the same path from the tick loop: the dirty variables of each owned
component are written into one update (batched across objects when **Enable Variable
Batching** is on) and sent reliably.

## 13. Data Flow — Inbound

Example: a remote player moves, and this client receives the new pose.

1. **Receive (network thread).** The transport's `Poll` waits up to 4 ms for a datagram and
   `Receive` reads it, dropping datagrams from any address but the server's. Each datagram
   is copied into a pooled buffer and queued, still encrypted, for the main thread.
2. **Admit (main thread).** `MainThreadDispatcher` runs the queued work. The packet passes an
   inbound flood budget, sized to the room's capacity, and a header check; a packet that
   needs a session is dropped while none exists.
3. **Decrypt.** The packet is authenticated and decrypted with the session's keys, checked
   against the replay window and decompressed. A packet that fails is dropped, and the SDK
   logs it at most once a second.
4. **Route.** The payload goes to its handler: room traffic to `RoomManager`, spawns and
   despawns to `SpawnManager`, RPCs to the target `NetworkBehaviour`, variable updates to the
   object's variables, and transform updates to the state-sync handler.
5. **Apply.** The state-sync handler parses the update with `TransformPacketParser` and
   finds the object in `NetworkObjectRegistry`. A remote object's state goes to its
   `NetworkTransformInterpolator` (`AddStateFromBroadcast`). An update for an object this
   client owns is ignored unless **Reconcile Owned Objects** is on in the `NetworkSettings`
   asset.
6. **Render.** Each frame, the interpolator places the object between the buffered states
   that bracket its render time.

## See also

- [Getting Started](getting-started.md) — installation and setup, step by step.
- [API Reference](api/index.md) — every public type and member.
- [Performance Tuning](performance-tuning.md) — tick rate, variable budget and pooling.
- [Troubleshooting](troubleshooting.md) — symptoms and fixes.

*RTMPE SDK 1.0.7 — [Getting Started](getting-started.md) — [API Reference](api/index.md)*
