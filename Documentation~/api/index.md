# RTMPE SDK C# API Reference

> SDK Version: `com.rtmpe.sdk 1.0.8`

This page is the reference for the public C# API of the RTMPE Unity SDK: the types a
game uses, their members, their defaults and the behaviour that matters when you call
them. For concepts and step-by-step setup, see [Getting Started](../getting-started.md)
and the [Architecture guide](../architecture.md).

Before you use the API, create your project in the RTMPE Developer Portal
(https://portal.rtmpengine.com/dashboard) and run **Window → RTMPE → Setup Wizard** in
the Unity Editor. The wizard writes the server address and keys into your
`NetworkSettings` asset, offers to add a `NetworkManager` to the open scene, and stores
your API key for the Editor.

---

## Table of Contents

- [Conventions](#conventions)
- [NetworkManager](#networkmanager)
- [NetworkSettings](#networksettings)
- [ApiKeySource](#apikeysource)
- [RtmpeConnectionBootstrap](#rtmpeconnectionbootstrap)
- [NetworkState](#networkstate)
- [DisconnectReason](#disconnectreason)
- [DisconnectSignal](#disconnectsignal)
- [ReconnectTokenLife](#reconnecttokenlife)
- [ReconnectBackoff](#reconnectbackoff)
- [RedactedString](#redactedstring)
- [Server key pinning](#server-key-pinning)
- [RoomManager](#roommanager)
- [RoomInfo](#roominfo)
- [PlayerInfo](#playerinfo)
- [PropertyValue](#propertyvalue)
- [CreateRoomOptions](#createroomoptions)
- [JoinRoomOptions](#joinroomoptions)
- [LocalPlayerContext](#localplayercontext)
- [RoomFieldLimits](#roomfieldlimits)
- [LobbyManager](#lobbymanager)
- [MatchmakingManager](#matchmakingmanager)
- [NetworkSceneManager](#networkscenemanager)
- [RtmpeSceneLoader](#rtmpesceneloader)
- [SpawnManager](#spawnmanager)
- [NetworkPrefabRegistry](#networkprefabregistry)
- [INetworkObjectPool](#inetworkobjectpool)
- [NetworkObjectRegistry](#networkobjectregistry)
- [OwnershipManager](#ownershipmanager)
- [NetworkBehaviour](#networkbehaviour)
- [RtmpeWorldAuthority and RtmpeWorldSpawner](#rtmpeworldauthority-and-rtmpeworldspawner)
- [InterestManager](#interestmanager)
- [Remote procedure calls](#remote-procedure-calls)
- [IDamageable](#idamageable)
- [NetworkTransform](#networktransform)
- [NetworkTransformInterpolator](#networktransforminterpolator)
- [Remote motion timing](#remote-motion-timing)
- [NetworkRigidbody / NetworkRigidbody2D](#networkrigidbody--networkrigidbody2d)
- [NetworkVariable types](#networkvariable-types)
- [NetworkVariableList](#networkvariablelist)
- [NetworkTransport](#networktransport)
- [UdpTransport](#udptransport)
- [SimulatedLinkTransport](#simulatedlinktransport)
- [MainThreadDispatcher](#mainthreaddispatcher)
- [See also](#see-also)

---

## Conventions

**Namespaces**

| Namespace | Contents |
|---|---|
| `RTMPE.Core` | `NetworkManager`, `NetworkSettings`, `ApiKeySource`, `NetworkBehaviour`, spawning, ownership, world authority, the connection bootstrap, disconnect and reconnect types |
| `RTMPE.Rooms` | Rooms, lobbies, matchmaking, networked scenes, the scene loader, interest management, room and player data |
| `RTMPE.Rpc` | RPC attributes, targets, callers, responses and parameter serialisation |
| `RTMPE.Sync` | `NetworkTransform`, `NetworkTransformInterpolator`, `NetworkRigidbody`, NetworkVariable types |
| `RTMPE.Crypto` | Server key pinning |
| `RTMPE.Transport` | `NetworkTransport`, `UdpTransport`, `SimulatedLinkTransport` |
| `RTMPE.Threading` | `MainThreadDispatcher` |

**Threading.** Main thread only: call every member on this page from the Unity main
thread unless its entry says otherwise. The SDK raises every event on the main thread,
except `MainThreadDispatcher.OnGenericActionDropped` and `OnRentedPacketDropped`, which
run on the thread that queued the dropped work. To call the SDK from another thread,
queue the call with [`MainThreadDispatcher`](#mainthreaddispatcher).

**Events.** Store the delegate you subscribe with, and unsubscribe with the same
delegate. The SDK builds new `Rooms`, `Lobby`, `Matchmaking` and `Spawner` managers on
every `Connect()` and every reconnect attempt; their events, and those of
`Spawner.Ownership`, keep their subscribers across the rebuild, so subscribe once (for
example before `Connect()`). A subscriber that throws is caught and logged, and the
remaining subscribers still run.

**Inspector fields.** Tables of component settings name each serialized field in
`PascalCase`; the Inspector shows it with spaces (`SyncPosition` appears as
**Sync Position**). These fields are private: set them in the Inspector.

**Version.** `RtmpeSdk.Version` is the SDK version string.

---

## NetworkManager

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class NetworkManager : MonoBehaviour`

The entry point of the SDK. It owns the connection, the encrypted session, the
heartbeat and the managers for rooms, lobbies, matchmaking, spawning and scenes.

Put one `NetworkManager` on a GameObject in your boot scene (**Add Component → RTMPE → NetworkManager**,
or let the Setup Wizard add it). It persists across scene loads, and its `Awake` runs
before other components' (`[DefaultExecutionOrder(-1000)]`). A second `NetworkManager`
destroys itself and logs "Duplicate NetworkManager instance".

### Inspector fields

| Field | Type | Default | Description |
|---|---|---|---|
| `Settings` | `NetworkSettings` | none | The settings asset. When it is empty, a default instance is used and a warning is logged; that instance has no server keys, so connections are refused until you assign a configured asset. |

### Static members

| Member | Description |
|---|---|
| `static NetworkManager Instance { get; }` | The manager in the scene. `null`, with a one-time warning, when the scene has none, and after the application starts quitting. The SDK never creates one for you. |
| `static bool TryGetInstance(out NetworkManager manager)` | Like `Instance`, without the warning. |
| `static bool HasInstance { get; }` | Whether a manager exists and the application is not quitting. May be read from any thread. |
| `static void SetTransportFactory(TransportFactoryFn factory)` | Installs a factory that builds the transport for the next connection attempt. See [Transport factory](#transport-factory). |
| `static void ClearTransportFactory()` | Restores the built-in `UdpTransport` for the next attempt. |
| `static bool HasCustomTransportFactory { get; }` | Whether a factory is installed. |
| `delegate NetworkTransport TransportFactoryFn(NetworkSettings settings)` | The factory signature. It receives the settings in use. |
| `static readonly TimeSpan DefaultServerRpcTimeout` | 30 seconds: the timeout `SendEnhancedRpcAsync` uses when you pass none. |

### Connection

| Member | Description |
|---|---|
| `void Connect(string apiKey)` | Starts connecting and moves to `Connecting`. Pass the key [`ApiKeySource`](#apikeysource) resolves. Success raises `OnConnected`. A failure raises `OnConnectionFailed`, then `OnDisconnected`; a session that fails the SDK's validation when it is established raises only `OnDisconnected`, with `DisconnectReason.Unknown`. Refused with a logged error, and no state change, when `apiKey` is empty, when the manager is disabled or its GameObject inactive, when the call is made on a manager that is not the active instance, and in a WebGL player. Ignored with a warning when the state is not `Disconnected`. |
| `bool Reconnect()` | Restores the previous session with the reconnect token the SDK holds. Makes up to `NetworkSettings.maxReconnectAttempts` attempts, waiting a random, growing delay between them (see [`ReconnectBackoff`](#reconnectbackoff)), and returns `true` when the attempts have started. When the server's stated token lifetime has passed, it makes one attempt. Returns `false`, with a log line, when no token is held, when the state is not `Disconnected`, when reconnect attempts are already running, when the manager is disabled or inactive, and in a WebGL player. When every attempt fails, the session data is cleared and `OnReconnectFailed` fires; call `Connect` to start again. An attempt that ends with `DisconnectReason.Unknown` (a session that fails the SDK's validation) stops the attempts and discards the token, and `OnReconnectFailed` is not raised. After a successful reconnect, the SDK rejoins `LastRoomId` when `NetworkSettings.autoRejoinLastRoomOnReconnect` is on. |
| `void Disconnect()` | Closes the connection: tells the server (when connected), stops the network thread and clears the session, including the reconnect token and the last-room snapshot. Raises `OnDisconnected` with `ClientRequest`. Also stops reconnect attempts that are waiting between tries. Does nothing when already disconnected and no reconnect is running. |

`Connect` and `Reconnect` refuse to run in a WebGL player, because the SDK drives the
network from a background thread that the browser does not provide. See
[WebGL is not a supported platform](../troubleshooting.md#webgl-is-not-a-supported-platform).

### State and session

| Member | Description |
|---|---|
| `NetworkState State { get; }` | The connection state. See [`NetworkState`](#networkstate). |
| `bool IsConnected { get; }` | `true` in `Connected` and `InRoom`. |
| `bool IsInRoom { get; }` | `true` in `InRoom`. |
| `NetworkSettings Settings { get; }` | The settings in use: the assigned asset, or the default instance. |
| `ulong LocalPlayerId { get; }` | The numeric session id the server assigned to this connection. Valid from `OnConnected`; `0` otherwise. RPC callers are identified by this id (`CurrentRpcSenderId`). |
| `string LocalPlayerStringId { get; }` | This client's player id in the current room: the id `NetworkBehaviour.OwnerPlayerId` and `PlayerInfo.PlayerId` use. Set when a room is entered; `null` outside a room. |
| `ulong CurrentRoomId { get; }` | Always `0`. Use `Rooms.CurrentRoom.RoomId`. |
| `bool IsMasterClient { get; }` | Whether this client is the host of `Rooms.CurrentRoom`, read from the room's roster. `false` outside a room and before the player id is known. |
| `RedactedString JwtToken { get; }` | The session's bearer token (a JWT), issued when the connection is established. See [`RedactedString`](#redactedstring). |
| `RedactedString ReconnectToken { get; }` | The token `Reconnect` uses. |
| `bool CanReconnect { get; }` | Whether a reconnect token is held. It does not check that the server still accepts the token. |
| `string LastRoomId { get; }` | `RoomInfo.RoomId` of the room this client was last in, kept across a disconnect that keeps the reconnect token so `Reconnect` can rejoin it. `null` after leaving the room, after `Disconnect()`, and whenever the token is discarded. |
| `string LastRoomCode { get; }` | The room code of `LastRoomId`, with the same lifetime. |
| `float LastRttMs { get; }` | The last round-trip time in milliseconds, measured by the heartbeat. `-1` before the first measurement. |
| `byte ServerBackpressure { get; }` | How loaded the server is with this session's traffic, from the last heartbeat reply: `0` means no throttling, and values near `255` mean the server is about to drop packets from this client. Clients that send often should slow down as it rises. `0` outside a session. |
| `IServerKeyPinStore PinStore { get; }` | The store that holds pinned server keys; a `MigratingPinStore` by default. See [Server key pinning](#server-key-pinning). |
| `void SetPinStore(IServerKeyPinStore store)` | Replaces the pin store. Call it before `Connect`. `null` restores the default store. |
| `void ClearPinnedKey()` | Forgets the pinned key for the configured `serverHost:serverPort`, so the next `TrustOnFirstUse` connection stores the server's key again. Exceptions from a custom store propagate. |

### Managers

| Member | Description |
|---|---|
| `RoomManager Rooms { get; }` | Rooms: create, join, leave, list, properties. See [`RoomManager`](#roommanager). |
| `LobbyManager Lobby { get; }` | Lobby room browsing. See [`LobbyManager`](#lobbymanager). |
| `MatchmakingManager Matchmaking { get; }` | Join-or-create matchmaking. See [`MatchmakingManager`](#matchmakingmanager). |
| `SpawnManager Spawner { get; }` | Networked objects. See [`SpawnManager`](#spawnmanager). |
| `NetworkSceneManager Scene { get; }` | Room-wide scene changes. See [`NetworkSceneManager`](#networkscenemanager). |
| `LocalPlayerContext LocalPlayer { get; }` | Writes this client's player properties. See [`LocalPlayerContext`](#localplayercontext). |

All six are available once the manager's `Awake` has run. `Rooms`, `Lobby`,
`Matchmaking` and `Spawner` are replaced on every `Connect()` and reconnect attempt, so
read them from the manager when you need them rather than keeping a reference.

### Ticks

| Member | Description |
|---|---|
| `uint LocalTick { get; }` | The simulation tick counter. Advances at `NetworkSettings.tickRate` while in a room and wraps at `uint.MaxValue`. |
| `uint ReplicationTick { get; }` | The counter NetworkVariable updates are stamped with. Advances only while in a room and drops the surplus after a long frame, so it is not a clock. Unrelated to `LocalTick`. |
| `float FixedTickInterval { get; }` | Seconds per tick: `1 / tickRate`. |
| `float SubTickResidualSeconds { get; }` | Time since the last tick boundary, in `[0, FixedTickInterval)`. |

### Readings

Counters an application can poll to show or alert on network health.

These are reset when a session starts:

| Member | Description |
|---|---|
| `long ReliableSendsDroppedCount { get; }` | Reliable messages (spawns, despawns, RPCs, room operations) abandoned after every retransmission went unacknowledged. Each one is a message peers did not receive; a warning is logged at most once a second. |
| `long ReliableRetransmitsCount { get; }` | Reliable messages sent again. |
| `long ReliableControlRefusedCount { get; }` | Reliable messages sent once, without retransmission, because the reliable window was full. |
| `long ReplicationFlushesDeferredCount { get; }` | NetworkVariable flushes postponed to a later tick because replication's share of the reliable window was in use. No value is lost; steady growth means variables replicate below the requested rate on this link. |
| `long ReplicationFlushesDowngradedCount { get; }` | Replication messages sent once, without retransmission, because they could not wait. Stays `0` unless `NetworkSettings.enableVariableBatching` is on. |
| `float ReliableRtoSeconds { get; }` | The retransmission timeout in force, in seconds. |
| `float ReliableSmoothedRttSeconds { get; }` | The smoothed round trip measured from acknowledgements, in seconds; `0` before the first. |
| `long EnhancedRpcDuplicatesReceivedCount { get; }` | RPCs delivered to this client a second time within a recent window. `0` on a healthy session. |
| `long RpcCallerRefusedCount { get; }` | RPCs this client refused because the caller did not satisfy the method's `Caller` declaration. |
| `long ServerRpcAnswersRefusedCount { get; }` | Answers to this client's server calls that came from another player instead of the server; they are ignored. |

These count from the moment the manager was created:

| Member | Description |
|---|---|
| `long PacketsOutCounter { get; }` | Packets sent. |
| `long BytesOutCounter { get; }` | Bytes sent. |
| `long PacketsInCounter { get; }` | Packets received. |
| `long BytesInCounter { get; }` | Bytes received. |
| `long DuplicateCopiesDroppedCount { get; }` | Copies of a server message this client had already received. Over UDP the server sends each message that happens once (a spawn, a despawn, an RPC, a reply) three times; the first copy is used and the others are dropped before they are decrypted, so this grows by about two for each such message. |
| `long DroppedInboundFloodPacketCount { get; }` | Inbound packets dropped by this client's inbound rate limit. |
| `long DroppedRpcReplayBufferCount { get; }` | RPCs dropped because they arrived while a room's buffered calls were being delivered and the queue holding them was full. |
| `long RefusedForeignIdClaimCount { get; }` | Inbound spawns refused because their object id belongs to this client's own id range under another player's name. `0` in a healthy room. |
| `long RefusedForeignDespawnCount { get; }` | Inbound despawns refused because they named an object this client both owns and created. |

These describe the network thread that is running; they read `0` when none is:

| Member | Description |
|---|---|
| `int SendQueueCount { get; }` | Packets waiting to be sent. |
| `long SendQueueDroppedCount { get; }` | Packets dropped because the send queue held `NetworkSettings.sendQueueMaxItems` items. |
| `long EnobufsCount { get; }` | Sends that found the operating system's send buffer full. |
| `long PerPacketFaultCount { get; }` | Transport faults that cost one packet rather than the session, such as an oversized datagram or a briefly unreachable route. Steady growth is packet loss. |

These show what is held at this moment:

| Member | Description |
|---|---|
| `int HeldVariableUpdateCount { get; }` | NetworkVariable updates held for objects that have not spawned on this client yet. Non-zero while a room is being entered. |
| `long HeldVariableUpdateBytes { get; }` | The bytes those updates occupy. |
| `int StagedCatchUpPacketCount { get; }` | Spawn, despawn and RPC packets held while this client is still entering a room. |
| `long StagedCatchUpPacketBytes { get; }` | The bytes those packets occupy. |
| `int SpawnsHeldForReturnCount { get; }` | Spawns held for a player who left and is expected back; held for five seconds at most. |
| `long LastInboundApplicationSequence { get; }` | The highest application sequence number received this session when `NetworkSettings.preserveApplicationSequence` is on; `-1` before the first. |

### Messaging

| Member | Description |
|---|---|
| `Task<RpcResponse> SendEnhancedRpcAsync(NetworkBehaviour sender, string methodName, object[] args, TimeSpan? timeout = null, CancellationToken cancellationToken = default)` | Calls a `RpcTarget.Server` method and returns the server's answer. See [Server calls with a reply](#server-calls-with-a-reply). |
| `void SendEnhancedRpc(NetworkBehaviour sender, string methodName, object[] args)` | The method `NetworkBehaviour.RPC` calls. Call `RPC` instead. |
| `void SendRpc(uint methodId, byte[] rpcPayload)` | Low-level: sends one of the built-in method-id calls (`RpcMethodId`) with a raw payload. Use `[RtmpeRpc]` methods for game messages. |
| `void Send(byte[] data, bool reliable = false)` | Low-level: sends `data` as a complete RTMPE packet that the caller has already framed. The SDK encrypts the packet but does not frame it: a buffer shorter than a packet header is ignored, and the header the caller wrote decides how the packet is read. Send game messages with [RPCs](#remote-procedure-calls) and state with [NetworkVariables](#networkvariable-types) instead. `reliable: true` asks for retransmission until the packet is acknowledged; it takes effect only when `NetworkSettings.EmitArqSequence` is on and the server supports it, and otherwise the packet is sent once and a one-time warning is logged. Logs a warning and does nothing when not connected. Copies `data`, so the buffer can be reused at once. Main thread only. |

### RPC context

Valid only while an `[RtmpeRpc]` method is running; outside one they read `0` and
`None`.

| Member | Description |
|---|---|
| `ulong CurrentRpcSenderId { get; }` | The session id of the call's sender. Compare it with `LocalPlayerId`. `0` for a call the server made. |
| `RpcCallerFacts CurrentRpcCallerFacts { get; }` | What the server reports about the caller of the running call. See [`RpcCaller` and `RpcCallerFacts`](#rpccaller-and-rpccallerfacts). |

### Events

| Member | Description |
|---|---|
| `event Action OnConnected` | Raised on every change to `Connected`: when `Connect` or a reconnect succeeds, and also when this client leaves a room (`InRoom` to `Connected`). |
| `event Action<NetworkState, NetworkState> OnStateChanged` | Every state change, as `(previous, current)`. |
| `event Action<DisconnectReason> OnDisconnected` | The connection closed. Also raised after each failed reconnect attempt, while later attempts may still follow. See [`DisconnectReason`](#disconnectreason). |
| `event Action<string> OnConnectionFailed` | A connection or reconnect attempt failed: it timed out (which includes a handshake the server refused and a server key that does not match the pin), a configuration problem stopped it, or a socket error ended a `Connect` attempt. The argument is a readable reason. `OnDisconnected` follows. Not raised when the session fails the SDK's validation; `OnDisconnected` reports that with `DisconnectReason.Unknown`. |
| `event Action<int> OnReconnectFailed` | Every `Reconnect` attempt failed. The argument is the number of attempts made. The manager is `Disconnected` and its session data is cleared; call `Connect` to start again. Not raised when an attempt ends with `DisconnectReason.Unknown`. |
| `event Action<string> OnAutoRejoinAttempt` | After a successful reconnect, the SDK is rejoining this room id. The outcome arrives through `Rooms.OnRoomJoined` or `Rooms.OnRoomError`. |
| `event Action<float> OnRttUpdated` | A new round-trip time, in milliseconds. |
| `event Action<byte[]> OnDataReceived` | Low-level: raised for every inbound data packet and transform-state packet, with the complete decrypted packet, header included. The SDK itself reads transform state from this event. Not needed for RPCs or NetworkVariables. |
| `event Action OnDataAcknowledged` | Low-level: the server acknowledged a reliable packet. |
| `event Action<ulong> OnJoinedRoom` | Obsolete. Raised when a room is entered, always with `0`. Use `Rooms.OnRoomJoined`. |
| `event Action<ulong> OnLeftRoom` | Obsolete. Raised when a room is left, always with `0`. Use `Rooms.OnRoomLeft`. |

### Transport factory

`SetTransportFactory` replaces the built-in `UdpTransport` with a transport you build:
a test double, or a transport of your own. The factory receives the `NetworkSettings`
in use. The manager builds its transport when it starts, and again at the start of a
connection attempt when the installed factory is not the one that built the current
transport. It reuses a transport across attempts, calling its `Connect` again, and
disposes it when it replaces it or is destroyed, so return a new instance each time.

- Install the factory before `Connect` or `Reconnect`. Installing or clearing one
  during a session takes effect at the next attempt.
- A factory that returns `null` logs a warning, and one that throws logs the exception;
  either way the attempt uses the built-in transport.
- A transport replaces the socket below the SDK's network thread, not the thread, so a
  factory does not make a WebGL build work; see
  [WebGL is not a supported platform](../troubleshooting.md#webgl-is-not-a-supported-platform).

See [`SimulatedLinkTransport`](#simulatedlinktransport) for a factory that shapes the
link for testing.

---

## NetworkSettings

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class NetworkSettings : ScriptableObject`

The project's connection settings, stored as an asset. The Setup Wizard fills in the
project's `NetworkSettings` asset, and makes one if there is none. You can also make one
with **Create → RTMPE → Settings** and assign it to the `NetworkManager`'s **Settings**
field, and keep several (for example one per environment) to swap between.

The fields are public; set them in the Inspector (each field appears with spaces,
`serverHost` as **Server Host**) or from code. Ranges are the Inspector's limits.

### Server

| Field | Type | Default | Description |
|---|---|---|---|
| `serverHost` | `string` | `"127.0.0.1"` | Host name or IP address of the RTMPE server. The dashboard shows yours. |
| `serverPort` | `int` | `7777` | UDP port of the RTMPE server (1–65535). |

### Security keys and pinning

| Field | Type | Default | Description |
|---|---|---|---|
| `apiKeySealServerPublicKeyHex` | `string` | `""` | Required. The server's X25519 public key, as 64 hexadecimal characters; the API key is sealed to it before it is sent. Copy the sealed-box public key from the dashboard. It is a different key from `pinnedServerPublicKeyHex`. |
| `pinnedServerPublicKeyHex` | `string` | `""` | The server's Ed25519 identity key, as 64 hexadecimal characters. Required under the default `Strict` pinning mode; copy it from the dashboard. |
| `serverPinningMode` | `ServerPinningMode` | `Strict` | Which server identity keys are accepted. See [Server key pinning](#server-key-pinning). |
| `requireFirstUseProvisioned` | `bool` | `false` | Under `TrustOnFirstUse`, refuse an endpoint whose key was not provisioned in the pin store beforehand (for example with `NetworkManager.PinStore.Save`), instead of storing the key the first connection presents. |
| `requirePinnedServerPublicKey` | `bool` | `false` | Deprecated; use `serverPinningMode`. When `true`, pinning is `Strict` whatever `serverPinningMode` says. |

### Session token checks

The SDK validates the session token (a JWT) the server issues when a connection is
established. The defaults match the RTMPE service.

| Field | Type | Default | Description |
|---|---|---|---|
| `expectedJwtIssuer` | `string` | `"rtmpe-gateway"` | Required `iss` claim. Empty accepts any issuer; do not ship it empty. |
| `expectedJwtAudience` | `string` | `"rtmpe-session"` | Required `aud` claim. Empty accepts any audience; do not ship it empty. |
| `jwtClockSkewSeconds` | `int` | `120` | Allowed clock difference, in seconds, when checking `exp` and `nbf`. |
| `jwtSignatureAlgorithm` | `NetworkSettings.JwtSignatureAlgorithm` | `Ed25519` | How the token's signature is verified: `Ed25519`, `RsaPkcs1Sha256` (RS256), or `None`. With `Ed25519` and no key configured, the SDK verifies with the identity key the server proves during the handshake. `None` logs an error when a token arrives with no key to check it against. |
| `jwtSigningKeyHex` | `string` | `""` | An Ed25519 public key, as 64 hexadecimal characters, that every token must be signed with. Used only with `Ed25519`; when set, it takes precedence over the key from the handshake. |
| `jwtSigningKeyPem` | `string` | `""` | A PEM-encoded RSA public key (at least 2048 bits) for `RsaPkcs1Sha256`. When set, it takes precedence over the key from the handshake. |

### Timing

| Field | Type | Default | Description |
|---|---|---|---|
| `heartbeatIntervalMs` | `int` | `5000` | Milliseconds between heartbeats (100–60000). A value below 100 is raised to 100 with a warning. |
| `heartbeatLivenessGraceMs` | `int` | `0` | How long the session may go without an acknowledged heartbeat, in milliseconds, before it is declared lost (0–300000). `0` means twice three heartbeat intervals (30 seconds at the default interval); other values are raised to at least three intervals. Raise it for clients that can stall briefly, such as mobile devices. |
| `connectionTimeoutMs` | `int` | `10000` | Milliseconds a connection or reconnect attempt may take (1000–60000). |
| `tickRate` | `int` | `30` | This client's tick rate in Hz (1–128): how often the tick counter advances, NetworkVariables are sent and a moved transform may be broadcast. The server's rate is fixed at 30 Hz, so 30 matches it. |

### Reconnect

| Field | Type | Default | Description |
|---|---|---|---|
| `autoRejoinLastRoomOnReconnect` | `bool` | `true` | After a successful `Reconnect`, rejoin `LastRoomId` automatically. Turn it off to choose the room yourself. |
| `maxReconnectAttempts` | `int` | `5` | Attempts one `Reconnect` call makes before `OnReconnectFailed` (1–50). |

### Buffers

| Field | Type | Default | Description |
|---|---|---|---|
| `sendBufferBytes` | `int` | `262144` | Socket send buffer size in bytes, 256 KiB (4096–4194304). |
| `receiveBufferBytes` | `int` | `262144` | Socket receive buffer size in bytes, 256 KiB. The minimum is the largest UDP datagram; smaller buffers lose datagrams in the operating system. |
| `networkThreadBufferBytes` | `int` | `65536` | The buffer each received datagram is read into, 64 KiB. The minimum is the largest UDP datagram; a larger datagram is lost without a message. |
| `sendQueueMaxItems` | `int` | `4096` | Packets the send queue holds at most (64–65536). When it is full, new packets are dropped and counted in `NetworkManager.SendQueueDroppedCount`. |

### Spawning and replication

| Field | Type | Default | Description |
|---|---|---|---|
| `prefabRegistry` | `NetworkPrefabRegistry` | none | The prefab registry the Network Prefabs window writes. When assigned, every prefab in it is registered automatically when a session is built. See [`SpawnManager`](#spawnmanager). |
| `maxSpawnsPerSecond` | `int` | `100` | Spawns accepted per second from each player, this client included (1–1000); the rest of that player's spawns are dropped, and other players' are not. The room as a whole may spawn up to eight times this in a second. The limit does not apply for a second and a half after a room is entered, while the room's existing objects arrive. |
| `maxSpawnsPerRoom` | `int` | `5000` | Networked objects that may exist on this client at once (100–50000). Keep it well above the server's per-player object limit (1024 by default), or one player can fill it; the room's host is not held to that limit. |
| `maxNetworkVariableListSize` | `int` | `1024` | Elements a `NetworkVariableList` may hold (1–65535); the same limit applies to what a client accepts. See [`NetworkVariableList`](#networkvariablelist). |
| `networkVariableListFullSyncIntervalSeconds` | `float` | `5` | Seconds a `NetworkVariableList` must go without being sent before the whole list is resent (0–300), which repairs a replica after a lost update. Every send restarts the interval. `0` turns the resend off. See [`NetworkVariableList`](#networkvariablelist). |

### Interest management

| Field | Type | Default | Description |
|---|---|---|---|
| `interestHysteresisMargin` | `float` | `1` | Extra distance, in world units, an object must move past `InterestManager.ReceiveFilterRadius` before it leaves the visible set (−1–50). While it is `0` or more, it replaces the `InterestManager`'s own `HysteresisMargin`; `-1` uses the component's value. |

### Scenes

| Field | Type | Default | Description |
|---|---|---|---|
| `sceneReadyTimeoutSeconds` | `float` | `0` | Seconds a networked scene load may go without every player reporting ready before `NetworkSceneManager.OnSceneLoadTimedOut` is raised. `0` uses the SDK default of 60 seconds; `-1` turns the report off. |

### Prediction and corrections

| Field | Type | Default | Description |
|---|---|---|---|
| `reconcileLerpThreshold` | `float` | `0.1` | Default correction threshold for a `NetworkTransform` with `EnablePrediction` on: a position error below it is accepted without a visible correction (0–10). |
| `reconcileSnapThreshold` | `float` | `2` | Default snap threshold for a `NetworkTransform` with `EnablePrediction` on: a position error above it snaps instead of blending (0–1000). A value below `reconcileLerpThreshold` is raised to it. |
| `reconcileOwnedObjects` | `bool` | `false` | Correct objects this client owns against the state the server sends back. Leave it off unless your server simulates movement authoritatively: otherwise owned objects are pulled toward their own delayed pose. |
| `maxOwnerVelocityMetersPerSecond` | `float` | `50` | Highest speed, in units per second, at which a `NetworkTransform` broadcasts its owner's movement (0–1000); faster movement is limited to it. Move instantly with `OwnerTeleportTo`. `0` turns the limit off. |
| `maxServerCorrectionDistance` | `float` | `50` | Largest server correction, in world units, that an owned `NetworkTransform`, `NetworkRigidbody` or `NetworkRigidbody2D` accepts; a larger one is refused with a warning and the local position is kept (0–100000). `0` turns the limit off. |
| `worldBoundsEnabled` | `bool` | `false` | Refuse corrections that would place an object outside the box below. |
| `worldBoundsCenter` | `Vector3` | `(0, 0, 0)` | Centre of the bounds box. |
| `worldBoundsExtents` | `Vector3` | `(10000, 10000, 10000)` | Half-size of the bounds box on each axis. |

### Physics receive checks

Checks `NetworkRigidbody` applies to physics state it receives. `0` turns a limit off.

| Field | Type | Default | Description |
|---|---|---|---|
| `maxLinearVelocity` | `float` | `1000` | Highest accepted linear speed, in units per second. |
| `maxAngularVelocity` | `float` | `1000` | Highest accepted angular speed (radians per second in 3-D, degrees per second in 2-D). |
| `maxPositionDeltaPerTick` | `float` | `50` | Largest accepted jump from the last accepted position, in world units. |
| `maxPhysicsPacketsPerSecond` | `float` | `240` | Physics updates accepted per object per second. |
| `allowDynamicConstraints` | `bool` | `false` | Apply constraint changes a sender makes at run time. Off, constraints stay as they were at spawn. |
| `dynamicConstraintsAllowMask` | `int` | `255` | With `allowDynamicConstraints`, the constraint bits that may change (0–255). |

### Lobby limits

| Field | Type | Default | Description |
|---|---|---|---|
| `maxLobbyRoomEntries` | `int` | `256` | Rooms accepted in one lobby room list (100–100000); a larger list is refused whole. |
| `maxLobbyStringBytes` | `int` | `256` | Longest string, in bytes, accepted in a lobby or matchmaking reply (16–65536). |

### Diagnostics

| Field | Type | Default | Description |
|---|---|---|---|
| `enableDebugLogs` | `bool` | `false` | Log verbose SDK activity to the Console. When off, routine SDK faults are logged at `Debug.Log` level so crash reporters do not collect them. Turn it on only in development. |
| `enableDiagnosticsUplink` | `bool` | `false` | Send captured log errors and exceptions to the RTMPE server for diagnosis during testing. It captures the whole process's log output, so leave it off in production. |
| `diagnosticsCaptureWarnings` | `bool` | `false` | Also send warnings. |
| `diagnosticsFlushIntervalMs` | `int` | `2000` | Milliseconds between diagnostics batches (250–60000). Errors are sent promptly regardless. |
| `diagnosticsMaxEntriesPerPacket` | `int` | `50` | Log entries per diagnostics packet (1–50). |
| `diagnosticsMaxPacketsPerInterval` | `int` | `4` | Diagnostics packets per batch (1–32). |

### Advanced protocol options

These change what the SDK sends. Leave them at their defaults unless the RTMPE server
you connect to is configured for the change.

| Field | Type | Default | Description |
|---|---|---|---|
| `EmitArqSequence` | `bool` | `true` | Read-only property backed by the Inspector field **Emit Arq Sequence**. Enables retransmission for reliable messages. When off, reliable messages are sent once and a one-time warning is logged. |
| `EmitGameplaySequencePrefix` | `bool` | `false` | Read-only property backed by the Inspector field **Emit Gameplay Sequence Prefix**. Adds a gameplay sequence number to packets marked for gameplay ordering. |
| `quantizeTransforms` | `bool` | `false` | Send `NetworkTransform` poses in a compact encoding: half-precision position and scale and a packed rotation, with about 0.1 % position error and 0.1° rotation error. Receivers accept both encodings. |
| `enableVariableBatching` | `bool` | `false` | Pack the tick's NetworkVariable updates from all owned objects into as few packets as possible. |
| `maxVariablesPerBatch` | `int` | `32` | Updates per batch packet (1–64). |
| `preserveApplicationSequence` | `bool` | `false` | Add an authenticated application sequence number to every encrypted packet. See `NetworkManager.LastInboundApplicationSequence`. |
| `enableGameplayOrdering` | `bool` | `false` | Reserved; has no effect. |
| `gameplayOrderingBufferSize` | `int` | `8` | Reserved; has no effect (2–64). |
| `requireLegacyRpcSender` | `bool` | `true` | Check the sender of [built-in method-id calls](#built-in-method-ids) before they run. Only the Editor reads it: player builds always check the sender. |

### Derived members

| Member | Description |
|---|---|
| `float TickInterval { get; }` | `1 / tickRate`, in seconds. |
| `ServerPinningMode EffectivePinningMode { get; }` | The pinning mode in force: `Strict` when `requirePinnedServerPublicKey` is set, otherwise `serverPinningMode`. |
| `int EffectiveHeartbeatIntervalMs { get; }` | `heartbeatIntervalMs`, raised to `MinHeartbeatIntervalMs`. |
| `const int MinHeartbeatIntervalMs` | 100. |
| `byte[] PinnedServerPublicKeyBytes { get; }` | `pinnedServerPublicKeyHex` as 32 bytes; `null` when empty. Throws `ArgumentException` for an invalid value. |
| `byte[] ApiKeySealServerPublicKeyBytes { get; }` | `apiKeySealServerPublicKeyHex` as 32 bytes; `null` when empty. Throws `ArgumentException` for an invalid value. |
| `const float DefaultNetworkVariableListFullSyncIntervalSeconds` | 5. |

`NetworkSettings.JwtSignatureAlgorithm` is a nested enum: `None` (0), `Ed25519` (1),
`RsaPkcs1Sha256` (2).

The settings asset has no API-key field: a key typed into a serialized field would be
saved in the asset and shipped with every build. Supply the key through
[`ApiKeySource`](#apikeysource) instead.

---

## ApiKeySource

**Namespace** `RTMPE.Core` · **Declaration** `public static class ApiKeySource`

Resolves the API key for `NetworkManager.Connect` from a source outside the project and
the build.

| Member | Description |
|---|---|
| `static string Resolve()` | The key, trimmed, or `""` when no source supplied one. Throws `InvalidOperationException` when a source you configured failed: a provider that threw, or a `--rtmpe-api-key-file` that cannot be read, holds no key or names no path. |
| `static bool TryResolve(out string apiKey)` | `true` with the key when a source supplied one. Never throws; a configured source that failed leaves `LastError` set. |
| `static bool IsAvailable()` | Whether a source supplied a key, without returning it. Never throws. |
| `static Exception LastError { get; }` | Why the last `TryResolve` failed when a configured source failed, or `null`. |
| `static void SetProvider(Func<string> provider)` | Registers your own source, consulted first. It returns the key, or `null` or `""` when it has none; a provider that throws stops resolution. It is called on the connect path and must return at once, so fetch the key before you connect. `null` removes the registration. |
| `const string CommandLineFileOption` | `"--rtmpe-api-key-file"`: a launch option naming a file that holds the key. |
| `const string CommandLineOption` | `"--rtmpe-api-key"`: a launch option carrying the key (`--rtmpe-api-key KEY` or `--rtmpe-api-key=KEY`; the single-dash form is also accepted). |
| `const string EnvironmentVariableName` | `"RTMPE_API_KEY"`: an environment variable holding the key. |

Sources, in the order `Resolve` consults them:

1. The provider registered with `SetProvider`.
2. In the Editor, the key the Setup Wizard stored in the operating system's credential
   store. In a development build, a key staged for development builds with the Setup
   Wizard's **Inject this key into development builds** (not available on Android). A
   release build reads neither.
3. The `--rtmpe-api-key-file` launch option.
4. The `--rtmpe-api-key` launch option.
5. The `RTMPE_API_KEY` environment variable.

Prefer `--rtmpe-api-key-file` to `--rtmpe-api-key` on a machine other accounts use:
other accounts can read a process's command line, and the file option puts a path
there instead of the key.

A key a client presents can be read by whoever runs the client. To keep your project
key off players' machines entirely, register a provider that returns a short-lived
key your own backend issues; see
[Giving a player build its API key](../getting-started.md#giving-a-player-build-its-api-key).

```csharp
using RTMPE.Core;
using UnityEngine;

public class ConnectOnStart : MonoBehaviour
{
    private void Start()
    {
        if (ApiKeySource.TryResolve(out string apiKey))
            NetworkManager.Instance.Connect(apiKey);
        else
            Debug.LogError("No RTMPE API key: " + (ApiKeySource.LastError?.Message ?? "no source supplied one."));
    }
}
```

---

<a id="connecting-without-writing-any"></a>
## RtmpeConnectionBootstrap

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class RtmpeConnectionBootstrap : MonoBehaviour` · **Add with** **Add Component → RTMPE → Connection Bootstrap**

Connects, enters a room and spawns the local player without code, and recovers the
session after an unexpected drop. Put it on a root GameObject in your boot scene,
beside the `NetworkManager`. It persists across scene loads; if a second copy appears,
the most recently enabled one acts and says so in the Console. It is optional: a game
with its own lobby or character selection can drive the same public API itself.

The key comes from [`ApiKeySource`](#apikeysource); the component has no API-key field.

### Inspector fields

| Field | Type | Default | Description |
|---|---|---|---|
| `ConnectOnStart` | `bool` | `true` | Connect when the scene starts. Off, call `Connect()` yourself. |
| `RejoinLastRoom` | `bool` | `true` | After a drop, enter the room it was in instead of a new one. |
| `ReconnectOnDrop` | `bool` | `true` | After an unexpected drop, call `NetworkManager.Reconnect` once for the session. |
| `RecoverOnResume` | `bool` | `true` | On mobile, start recovery as soon as the app returns to the foreground. Requires `ReconnectOnDrop`. |
| `FreshSessionWhenRecoveryFails` | `bool` | `true` | When the reconnect attempts fail, or there is no session to restore, connect again with the API key. |
| `FreshSessionAttempts` | `int` | `3` | New sessions it may open in a row without reaching a room (1–10). |
| `EntryPolicy` | `RoomEntryPolicy` | `CreateRoom` | How to enter a room: `CreateRoom` (open a room and host it), `JoinRoom` (enter the room named by `RoomId`) or `Matchmaking` (ask the server for a room to share, or a new one). |
| `RoomName` | `string` | `""` | `CreateRoom` only. Empty lets the server choose. |
| `RoomId` | `string` | `""` | `JoinRoom` only. |
| `MaxPlayers` | `int` | `0` | `CreateRoom` and `Matchmaking`. `0` lets the server choose; otherwise 1–100. |
| `MatchmakingMode` | `string` | `""` | `Matchmaking` only. Required for that policy. |
| `PlayerPrefab` | `GameObject` | none | Spawned for this client on entering a room — in a room that has a scene, once this client has loaded it. It must have an id in **Window → RTMPE → Network Prefabs**. Empty enters the room and spawns nothing. |

### Members

| Member | Description |
|---|---|
| `Func<GameObject> ChoosePlayerPrefab` | Chooses the prefab to spawn, overriding `PlayerPrefab`. |
| `Func<Pose> ChooseSpawnPose` | Chooses where the player appears. Defaults to this component's transform. |
| `event Action<NetworkBehaviour> OnLocalPlayerSpawned` | The local player was spawned. |
| `event Action<string> OnBootstrapFailed` | The flow cannot continue; the argument is a readable reason. A few SDK refusals are only logged and do not reach this event, such as `JoinRoom` with an empty id. |
| `NetworkBehaviour LocalPlayer { get; }` | The player object it spawned, or `null`. |
| `string CurrentRoomId { get; }` | The room it last entered, or `null`. |
| `void Connect()` | Resolves the key and connects. Use it when `ConnectOnStart` is off. |
| `void Restart()` | Forgets the remembered room and enters a room again with the configured policy. Refused with `OnBootstrapFailed` while in a room. |

After a reconnect with `autoRejoinLastRoomOnReconnect` on, the SDK rejoins the room
itself and the component does not issue a second request. When the player leaves a
room, is kicked or is moved to another room, the component waits for `Restart()`
instead of entering a room on its own.

The **Two Player Room** sample uses this component.

---

## NetworkState

**Namespace** `RTMPE.Core` · **Declaration** `public enum NetworkState`

| Value | Meaning |
|---|---|
| `Disconnected` | No connection. `Connect` and `Reconnect` are accepted in this state. Reconnect attempts also wait in this state between tries. |
| `Connecting` | A `Connect` is in progress. |
| `Connected` | Connected, not in a room. |
| `InRoom` | Connected and in a room. |
| `Disconnecting` | `Disconnect` is in progress. |
| `Reconnecting` | A reconnect attempt is in progress; it ends in `Connected` or `Disconnected`. |

---

<a id="disconnectreason-enum"></a>
## DisconnectReason

**Namespace** `RTMPE.Core` · **Declaration** `public enum DisconnectReason`

The argument of `NetworkManager.OnDisconnected`, and whether the reconnect token
survives (see `NetworkManager.CanReconnect`).

| Value | When | Reconnect token |
|---|---|---|
| `Unknown` | The server ended the session with an unspecified reason, or the session the server issued failed the SDK's validation, for example a session token that does not pass the checks under [Session token checks](#session-token-checks) (the Console names the check). `OnConnectionFailed` is not raised for a failed validation, and a running `Reconnect` stops without `OnReconnectFailed`. | Discarded |
| `ClientRequest` | You called `Disconnect()`. | Discarded |
| `ServerRequest` | The server closed the session, for example while restarting. Also used when the server gives no reason, or one this SDK version does not know. | Kept |
| `Timeout` | A connection or reconnect attempt did not complete within `connectionTimeoutMs`. This includes a handshake the server refused and a server key that does not match the pin: the reason is passed to `OnConnectionFailed` first. Also raised when every `Reconnect` attempt has failed. | Discarded after `Connect`. A reconnect attempt that timed out before the server answered keeps it for the remaining attempts. |
| `ConnectionLost` | Three heartbeats went unanswered and no heartbeat was acknowledged within `heartbeatLivenessGraceMs`; or a socket error ended the session. | Kept after missed heartbeats; discarded after a socket error |
| `Kicked` | The server removed this client from the session. | Discarded |
| `NonceExhausted` | The session sent 2³² encrypted packets and must be established again. | Discarded |
| `ProtocolError` | The secure session could not be set up: `apiKeySealServerPublicKeyHex` is missing or invalid, the pinning mode requires a pin that is not available (none configured or provisioned, or the pin store could not be read), or the key exchange failed. The reason is passed to `OnConnectionFailed` first. The server can also end a session with this reason. | Discarded, except that a missing pin during a reconnect attempt keeps it |

After an unexpected drop that kept the token (`ConnectionLost` or `ServerRequest`),
call `Reconnect()`. When `OnReconnectFailed` fires, or when no token is left, call
`Connect(apiKey)`. [`RtmpeConnectionBootstrap`](#rtmpeconnectionbootstrap) does this
for you.

```csharp
using System;
using RTMPE.Core;
using UnityEngine;

public class ReconnectOnDrop : MonoBehaviour
{
    private Action<DisconnectReason> _onDisconnected;
    private Action<int> _onReconnectFailed;

    private void OnEnable()
    {
        _onDisconnected = reason =>
        {
            var manager = NetworkManager.Instance;
            bool unexpected = reason == DisconnectReason.ConnectionLost
                           || reason == DisconnectReason.ServerRequest;
            if (manager == null || !unexpected) return;
            if (!manager.CanReconnect || !manager.Reconnect())
                ConnectAgain();
        };
        _onReconnectFailed = _ => ConnectAgain();

        NetworkManager.Instance.OnDisconnected += _onDisconnected;
        NetworkManager.Instance.OnReconnectFailed += _onReconnectFailed;
    }

    private void OnDisable()
    {
        if (!NetworkManager.HasInstance) return;
        NetworkManager.Instance.OnDisconnected -= _onDisconnected;
        NetworkManager.Instance.OnReconnectFailed -= _onReconnectFailed;
    }

    private static void ConnectAgain()
    {
        if (ApiKeySource.TryResolve(out string apiKey))
            NetworkManager.Instance.Connect(apiKey);
    }
}
```

---

## DisconnectSignal

**Namespace** `RTMPE.Core` · **Declaration** `public static class DisconnectSignal`

| Member | Description |
|---|---|
| `static bool TokenSurvives(DisconnectReason reason)` | Whether a server-initiated disconnect with this reason keeps the reconnect token and the last-room snapshot. `true` for `ServerRequest` only. |

---

## ReconnectTokenLife

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class ReconnectTokenLife`

The rule `Reconnect` uses to size its attempts: when the server stated how long the
reconnect token lasts and that time has passed, one attempt is made instead of
`maxReconnectAttempts`, and a fresh `Connect` follows sooner. A token past its stated
life is still tried once, because the server may have renewed it. The SDK keeps its
own instance; you do not need to create one.

| Member | Description |
|---|---|
| `const int AttemptsWorthSpendingOnAnOverAgeToken` | 1. |
| `static long NowUnixSeconds { get; }` | The current time in Unix seconds, the unit the methods take. |
| `bool HasStatement { get; }` | Whether a lifetime was recorded for the current token. |
| `void Forget()` | Drops the recorded lifetime. |
| `bool IsPastStatedLife(long nowUnixSeconds)` | Whether the stated lifetime has passed. `false` when nothing was stated or the clock moved backwards. |
| `int AttemptBudget(int configuredBudget, long nowUnixSeconds)` | The attempts to make: `configuredBudget` (at least 1), reduced to 1 once the stated lifetime has passed. |

`Record`, which stores what the server stated when a token is issued, is used by the SDK
and is not intended to be called from game code.

---

## ReconnectBackoff

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class ReconnectBackoff`

Randomised, capped exponential delays for retrying a connection. `Reconnect` uses one
between its attempts; you can use one to space your own `Connect` retries.

| Member | Description |
|---|---|
| `ReconnectBackoff(int baseDelayMs = DefaultBaseDelayMs, int maxDelayMs = DefaultMaxDelayMs, int? seed = null)` | Throws `ArgumentOutOfRangeException` when `baseDelayMs` is not positive or `maxDelayMs` is below it. Pass `seed` only in tests. |
| `const int DefaultBaseDelayMs` | 1000. |
| `const int DefaultMaxDelayMs` | 30000. |
| `int Attempt { get; }` | Delays drawn since construction or the last `Reset`. |
| `TimeSpan NextDelay()` | Draws the next delay: a random value between a tenth of the base delay and `baseDelayMs × 2^Attempt`, capped at `maxDelayMs`. |
| `void Reset()` | Starts again from the base delay. Call it after a successful connection. |
| `static int ComputeExponentialCapMs(int attempt, int baseDelayMs, int maxDelayMs)` | The cap for an attempt: `baseDelayMs × 2^attempt`, at most `maxDelayMs`. |

---

## RedactedString

**Namespace** `RTMPE.Core` · **Declaration** `public readonly struct RedactedString : IEquatable<RedactedString>`

Wraps a secret, such as `NetworkManager.JwtToken`, so it is not printed by accident:
`ToString()`, string interpolation and `Debug.Log` show `<redacted>`.

| Member | Description |
|---|---|
| `bool IsEmpty { get; }` | Whether there is no value. |
| `string Reveal()` | The value. Call it where you use the value, and do not store the result. |
| `override string ToString()` | `"<redacted>"`, or `""` when empty. |
| `const string Placeholder` | `"<redacted>"`. |
| `static explicit operator string(RedactedString s)` | The value, like `Reveal()`. |
| `Equals`, `==`, `!=`, `GetHashCode` | Compare the values ordinally. |

---

## Server key pinning

**Namespace** `RTMPE.Crypto`

The SDK checks the server's Ed25519 identity key during every handshake.
`NetworkSettings.serverPinningMode` decides which key it accepts.

### ServerPinningMode

**Declaration** `public enum ServerPinningMode`

| Value | Behaviour |
|---|---|
| `Strict` (default) | The key must equal `NetworkSettings.pinnedServerPublicKeyHex`. With no pin configured, every connection is refused. Use it for builds you ship. |
| `TrustOnFirstUse` | The first connection to a `host:port` stores the server's key in the pin store, and later connections must present the same key. The connection is refused when the pin store cannot be read, and, with `requireFirstUseProvisioned`, when no key was provisioned for the endpoint. |
| `InsecureNoPinning` | Any key is accepted, and a warning is logged each session. For local testing only. |

When the pinning mode requires a pin that is not available, the attempt ends at once
with `DisconnectReason.ProtocolError`. When the server presents a key that does not
match the pin, the SDK ignores its reply and the attempt ends after `connectionTimeoutMs`
with `DisconnectReason.Timeout`; `OnConnectionFailed` names the identity check as the
reason.

### IServerKeyPinStore

**Declaration** `public interface IServerKeyPinStore`

Storage for pinned keys, one 32-byte key per endpoint. Endpoints are the canonical
`host:port` strings `ServerKeyPinning.CanonicalEndpoint` produces.

| Member | Description |
|---|---|
| `byte[] Load(string endpoint)` | The stored key, or `null`. |
| `void Save(string endpoint, byte[] pin)` | Stores a key, replacing any previous one. |
| `void Clear(string endpoint)` | Removes the key; does nothing when there is none. |

### IPinStoreAvailability

**Declaration** `public interface IPinStoreAvailability`

Optional for a store whose storage can be unreadable. Without it, "no key" from `Load`
is taken to mean the endpoint was never pinned.

| Member | Description |
|---|---|
| `bool TryLoadAuthoritative(string endpoint, out byte[] pin)` | `false` when the storage could not be read, so a missing key is not mistaken for a first connection. |

### IProvisionedPinStore

**Declaration** `public interface IProvisionedPinStore`

Optional for a store that can tell a provisioned key from one found in a fallback
location. `requireFirstUseProvisioned` reads the store through it; a store without it is
taken at its word.

| Member | Description |
|---|---|
| `bool TryLoadProvisioned(string endpoint, out byte[] pin)` | The key from the storage provisioning writes to, without any fallback or migration. `false` when that storage could not be read. |

### Built-in stores

| Type | Description |
|---|---|
| `MigratingPinStore` | The default store. Writes to an `EncryptedFilePinStore` and moves a key found in `PlayerPrefsPinStore` into it on first read. Implements all three interfaces. |
| `EncryptedFilePinStore` | Stores keys in a file under `Application.persistentDataPath`, each record protected with a key derived from the device. Implements `IPinStoreAvailability`. |
| `PlayerPrefsPinStore` | Stores keys in `PlayerPrefs`. Implements `IPinStoreAvailability`. |

`ServerKeyPinning.CanonicalEndpoint(string host, int port)` returns the endpoint string
a key is stored under (the host trimmed and lower-cased; no DNS lookup).
`KeyHex.Decode32(string hex)` decodes a 64-character hexadecimal key.

To provision a key before the first connection, for example from a signed configuration
file:

```csharp
using RTMPE.Core;
using RTMPE.Crypto;

public static class PinProvisioning
{
    public static void Provision(string host, int port, string identityKeyHex)
    {
        var manager = NetworkManager.Instance;
        manager.PinStore.Save(
            ServerKeyPinning.CanonicalEndpoint(host, port),
            KeyHex.Decode32(identityKeyHex));
    }
}
```

---

## RoomManager

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class RoomManager` · **Access** `NetworkManager.Instance.Rooms`

Creates, joins, leaves and lists rooms, and writes room and player properties. Results
arrive as events. A new `RoomManager` is built on every `Connect()` and reconnect
attempt; its events keep their subscribers.

### Operations

| Member | Description |
|---|---|
| `void CreateRoom(CreateRoomOptions options = null)` | Creates a room. Raises `OnRoomCreated`; with `AutoJoinAsHost` (the default) the creator is then seated as host and `OnRoomJoined` follows. An invalid `Name` or `MaxPlayers` is refused with `OnRoomError`, as is a create the server refuses or does not answer within 30 seconds. Only logged: a call while not connected, and a call while 16 creates are awaiting replies. From inside a room it first leaves that room (`OnRoomLeft` when the server confirms) and then creates; if the room cannot be left, `OnRoomError` reports it and nothing is created. A room this client created and has not joined holds no seat, so from there the create is sent at once. |
| `void JoinRoom(string roomId, JoinRoomOptions options = null)` | Joins a room by id. Raises `OnRoomJoined`, or `OnRoomError` when the server refuses. The request is retransmitted until answered; when retransmission gives up, `OnRoomError` reports it, and a late success still raises `OnRoomJoined`. Only logged: an empty `roomId`, and a call while not connected. An invalid display name is refused with `OnRoomError`. From inside another room it first leaves that room (`OnRoomLeft` when the server confirms) and then joins, so a join then refused leaves this client in no room; if the room cannot be left, `OnRoomError` reports it and nothing is sent. |
| `void JoinRoomByCode(string roomCode, JoinRoomOptions options = null)` | Joins a room by its six-character code, which is upper-cased first. A code that cannot exist (wrong length, characters outside `RoomFieldLimits.RoomCodeAlphabet`) is refused with `OnRoomError`; otherwise as `JoinRoom`. |
| `void LeaveRoom()` | Leaves the current room. `OnRoomLeft` fires when the server confirms. `OnRoomError` reports a refusal, and reports a leave not answered within 15 seconds (the room is not left until a reply arrives). Only logged when not in a room. |
| `void ListRooms(bool publicOnly = true)` | Requests the project's rooms: `OnRoomListReceived` with the list. `OnRoomError` when the server declines, when the reply carries a status this SDK version does not know, and when no answer arrives within 15 seconds (`LastRoomListTimedOut` is then `true`). A list the server shortened is raised and also reported. A reply that cannot be read raises no event: it ends the wait and sets `LastRoomListProblem` to `Unreadable`, so check that property as well as the events. |
| `void SetRoomProperties(IReadOnlyDictionary<string, PropertyValue> properties)` | Writes room properties; host only. The write merges into the room's properties; `PropertyValue.Deletion()` removes a key. `OnRoomPropertiesChanged` fires on every client when the server applies it. Throws `ArgumentNullException` for `null` and `ArgumentException`, before anything is sent, for a write that breaks a rule under [Property writes](#property-writes). |
| `void SetRoomProperty(string key, PropertyValue value)` | Writes one room property. Throws as `SetRoomProperties` does, and `ArgumentException` for an empty key. |
| `void SetPlayerProperties(string playerId, IReadOnlyDictionary<string, PropertyValue> properties)` | Writes this client's player properties. `playerId` must be `NetworkManager.LocalPlayerStringId`; another id is refused with `OnRoomError` and nothing is sent. `OnPlayerPropertiesChanged` fires on every client when the server applies it. Throws `ArgumentException` for an empty id, `ArgumentNullException` for `null` properties, and `ArgumentException`, before anything is sent, for a write that breaks a rule under [Property writes](#property-writes). |
| `void TransferMasterClient(string targetPlayerId)` | Asks the server to make another player host; host only. `OnMasterClientChanged` fires on every client when it happens. A request not carried out within 12 seconds is reported with `OnRoomError`. Throws `ArgumentException` for an empty id. |
| `void KickPlayer(string targetPlayerId)` | Asks the server to remove a player from the room; host only. `OnPlayerKicked` fires when it happens. A request not carried out within 12 seconds is reported with `OnRoomError`. The host cannot be kicked. Throws `ArgumentException` for an empty id. |
| `void ReportSceneLoaded(string sceneName)` | Reports that this client finished loading `sceneName`. `NetworkSceneManager.ReportReady` calls it for you. Throws `ArgumentException` for an empty name. |

Operations that change a room (property writes, host commands, scene reports) require
`InRoom` and are only logged otherwise.

`HandleMasterClientChanged`, `HandlePlayerKicked`, `HandleAllPlayersSceneLoaded`,
`ApplyRoomPropertiesBroadcast` and `ApplyPlayerPropertiesBroadcast` are used by the SDK
to apply what the server sends, and are not intended to be called from game code.

### Property writes

- A write names the version it expects to create: the current `PropertiesVersion` plus
  one. The server applies it only at that version and only from a client entitled to
  write (the host for room properties, the player for its own properties).
- A refused write gets no reply. When no change arrives within 12 seconds,
  `OnRoomError` reports that the version did not move; read the current value and write
  again if you still need to.
- Keys starting with `__` are reserved for the SDK (`ReservedPropertyKeys`). The host
  may write `__scene`, `__scene_additive` and `__scene_stack`, which `NetworkSceneManager`
  uses; no other reserved key can be written. They count against the room's 20.
- Limits (`PropertyLimits`): a room holds at most 20 properties and a player 10; keys
  are up to 32 bytes of UTF-8 and values up to 512 bytes. A write that would take the
  room or the player past its count is refused like any other refused write.
- A write that breaks a rule on its own throws `ArgumentException` before anything is
  sent: an empty map, more properties than the limit, an empty or oversized key, a
  reserved key that cannot be written, a value over the limit, or a `float`, `Vector3`
  or `Color` value holding NaN or infinity.

### State

| Member | Description |
|---|---|
| `RoomInfo CurrentRoom { get; }` | The room this client is in, or `null`. |
| `bool IsInRoom { get; }` | Whether `CurrentRoom` is set. |
| `bool IsMasterClient { get; }` | Whether this client is the room's host. |
| `RoomInfo LastLeftRoom { get; }` | The room most recently left, set just before `OnRoomLeft`; cleared when a room is entered. |
| `bool LastRoomListTimedOut { get; }` | Whether the last `ListRooms` went unanswered. Cleared by the next `ListRooms` call; a reply that arrives after the timeout does not clear it. |
| `RoomPacketParser.RoomListOutcome LastRoomListProblem { get; }` | The problem found in the last room list: `Ok`, `Refused` (the list is not the project's rooms), `RoomsOmitted` (the list is real but short), `UnknownStatus`, or `Unreadable`. Returns to `Ok` when a complete list arrives, so you can poll it. |

### Events

| Member | Description |
|---|---|
| `event Action<RoomInfo> OnRoomCreated` | A room was created. With `AutoJoinAsHost`, wait for `OnRoomJoined` before starting gameplay. |
| `event Action<RoomInfo> OnRoomJoined` | This client entered a room, by `CreateRoom`, `JoinRoom`, matchmaking or the automatic rejoin. Start gameplay (for example spawning the player) here. |
| `event Action OnRoomLeft` | This client left the room: when the server confirms `LeaveRoom`, when the host kicks this client, when the server releases this client's seat, and when this client enters a different room — including the leave a `CreateRoom` or `JoinRoom` from inside a room makes first. The session stays connected. Not raised when the connection closes; handle `NetworkManager.OnDisconnected` for that. |
| `event Action<PlayerInfo> OnPlayerJoined` | Another player entered the room. The SDK resends every NetworkVariable this client owns so the newcomer receives current values. |
| `event Action<string> OnPlayerLeft` | Another player left the room; the argument is the player id. Also raised when another player is kicked. |
| `event Action<RoomInfo[]> OnRoomListReceived` | The project's rooms, after `ListRooms`. Not raised for a refused request, which goes to `OnRoomError`. |
| `event Action<string> OnRoomError` | A room operation failed or was not answered; the argument describes it. |
| `event Action<RoomInfo> OnRoomPropertiesChanged` | Room properties changed. The argument is the new snapshot; `CurrentRoom` already holds it, so keep the previous snapshot yourself if you need the difference. |
| `event Action<string, PlayerInfo> OnPlayerPropertiesChanged` | A player's properties changed: `(playerId, player)`. |
| `event Action<string, string> OnMasterClientChanged` | The host changed: `(previousHostId, newHostId)`. Either may be empty when unknown. Raised when the host leaves and another player is promoted, and after `TransferMasterClient`. |
| `event Action<string, string> OnPlayerKicked` | The host removed a player: `(kickerId, targetPlayerId)`. For another player, `OnPlayerLeft` follows. When the target is this client, the SDK leaves the room and raises `OnRoomLeft`; the session stays connected. |
| `event Action<string> OnAllPlayersSceneLoaded` | Every player reported the scene loaded. Raised for every such report from the server, including a late one from a round that a newer scene change replaced. Prefer `NetworkSceneManager.OnAllPlayersSceneLoaded`. |

---

## RoomInfo

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class RoomInfo`

A read-only snapshot of a room, received in `OnRoomCreated`, `OnRoomJoined`,
`OnRoomPropertiesChanged` and `OnRoomListReceived`.

| Member | Description |
|---|---|
| `string RoomId { get; }` | The room id. Pass it to `JoinRoom`. |
| `string RoomCode { get; }` | The six-character join code. Pass it to `JoinRoomByCode`. |
| `string Name { get; }` | The display name. |
| `string State { get; }` | `"waiting"`, `"playing"` or `"finished"`. |
| `int PlayerCount { get; }` | Players in the room. |
| `int MaxPlayers { get; }` | Capacity (1–100). |
| `bool IsPublic { get; }` | Whether the room appears in public lists. |
| `PlayerInfo[] Players { get; }` | The roster; may be empty in a list result. |
| `string MasterId { get; }` | The host's player id, or `""`. |
| `string CurrentScene { get; }` | The room's scene (the `__scene` property), or `""`. |
| `IReadOnlyDictionary<string, PropertyValue> Properties { get; }` | Room properties; never `null`. |
| `int PropertiesVersion { get; }` | Increases with every applied property write. |
| `RoomInfo WithProperties(IReadOnlyDictionary<string, PropertyValue> properties, int version)` | A copy with the properties replaced. |
| `RoomInfo WithPlayers(PlayerInfo[] players)` | A copy with the roster replaced and `PlayerCount` kept. |
| `RoomInfo WithRoster(PlayerInfo[] players)` | A copy with the roster replaced and `PlayerCount` set to its length. |
| `RoomInfo(string roomId, string roomCode, string name, string state, int playerCount, int maxPlayers, bool isPublic, PlayerInfo[] players = null, IReadOnlyDictionary<string, PropertyValue> properties = null, int propertiesVersion = 0)` | Constructs a snapshot, for tests. |

---

## PlayerInfo

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class PlayerInfo`

A read-only snapshot of a player in a room.

| Member | Description |
|---|---|
| `string PlayerId { get; }` | The player id. |
| `string DisplayName { get; }` | The display name given in `JoinRoomOptions` or `MatchmakingOptions`; may be empty. |
| `bool IsHost { get; }` | Whether this player is the room's current host; it changes when the host changes. |
| `bool IsReady { get; }` | Whether the player has signalled ready. |
| `IReadOnlyDictionary<string, PropertyValue> Properties { get; }` | The player's properties; never `null`. Entering a room delivers the players' properties with the room, up to about 3 KB of them together; a player whose properties did not fit reads empty until it changes one, then shows only the keys of that change, and a warning says so. |
| `int PropertiesVersion { get; }` | Increases with every applied write to this player's properties. Entering a room delivers each player's version, this client's own included, so a returning player's writes are accepted. |
| `PlayerInfo WithProperties(IReadOnlyDictionary<string, PropertyValue> properties, int version)` | A copy with the properties replaced. |
| `PlayerInfo WithIsHost(bool isHost)` | A copy with `IsHost` changed. |
| `PlayerInfo(string playerId, string displayName, bool isHost, bool isReady, IReadOnlyDictionary<string, PropertyValue> properties = null, int propertiesVersion = 0)` | Constructs a snapshot, for tests. |

---

## PropertyValue

**Namespace** `RTMPE.Rooms` · **Declaration** `public readonly struct PropertyValue : IEquatable<PropertyValue>`

A typed room or player property value. Create one with a factory method and read it
with the accessor for its type; the other accessors throw `InvalidOperationException`.

| Member | Description |
|---|---|
| `static PropertyValue OfInt(int v)` | |
| `static PropertyValue OfFloat(float v)` | |
| `static PropertyValue OfBool(bool v)` | |
| `static PropertyValue OfString(string v)` | `null` becomes `""`. |
| `static PropertyValue OfBytes(byte[] v)` | Copies the array. |
| `static PropertyValue OfVector3(Vector3 v)` | |
| `static PropertyValue OfColor(Color v)` | Linear RGBA. |
| `static PropertyValue Deletion()` | Removes the key it is written under. It never appears in a snapshot. `default(PropertyValue)` is an `Int` of 0, not a deletion. |
| `PropertyType Type { get; }` | `Int`, `Float`, `Bool`, `String`, `Bytes`, `Vector3`, `Color` or `Deleted`. |
| `bool IsDeletion { get; }` | Whether this is `Deletion()`. |
| `int AsInt()`, `float AsFloat()`, `bool AsBool()`, `string AsString()`, `Vector3 AsVector3()`, `Color AsColor()` | The value. |
| `byte[] AsBytes()` | A copy of the bytes. |
| `ReadOnlyMemory<byte> AsBytesReadOnly()` | The bytes without copying. |
| `object BoxedValue()` | The value boxed; `null` for a deletion. |

`PropertyLimits` holds the limits: `MaxPropertiesPerRoom` (20), `MaxPropertiesPerPlayer`
(10), `MaxKeyBytes` (32) and `MaxValueBytes` (512).

---

## CreateRoomOptions

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class CreateRoomOptions`

| Member | Description |
|---|---|
| `string Name { get; set; }` | Display name, up to 64 characters. Default `""`: the server names the room. |
| `int MaxPlayers { get; set; }` | Capacity, 1–100. Default `0`: the server's default of 100. Any other value is refused with `OnRoomError` before sending. |
| `bool IsPublic { get; set; }` | Whether the room appears in public lists. Default `true`. |
| `bool AutoJoinAsHost { get; set; }` | Seat the creator as host after the room is created, so `OnRoomJoined` follows `OnRoomCreated`. Default `true`. With `false`, the room stays empty until you call `JoinRoom` yourself. |

---

## JoinRoomOptions

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class JoinRoomOptions`

| Member | Description |
|---|---|
| `string DisplayName { get; set; }` | The name other players see, up to 32 characters. Default `""`: your game decides what to show for a player without a name. |

---

## LocalPlayerContext

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class LocalPlayerContext` · **Access** `NetworkManager.Instance.LocalPlayer`

Writes this client's player properties without repeating its player id.

| Member | Description |
|---|---|
| `string PlayerId { get; }` | `NetworkManager.LocalPlayerStringId`, or `""` outside a room. |
| `void SetProperty(string key, PropertyValue value)` | Writes one property through `RoomManager.SetPlayerProperties`. Logs an error when this client has no player id. Throws as `SetPlayerProperties` does, and `ArgumentException` for an empty key. |
| `void SetProperties(IReadOnlyDictionary<string, PropertyValue> properties)` | Writes several properties in one request. Throws `ArgumentNullException` for `null`. |

---

## RoomFieldLimits

**Namespace** `RTMPE.Rooms` · **Declaration** `public static class RoomFieldLimits`

The limits the room operations check before sending. Each `Validate…` method returns
`null` for an acceptable value and a sentence naming the broken rule otherwise.

| Member | Description |
|---|---|
| `const int MaxRoomNameRunes` | 64 characters. |
| `const int MaxDisplayNameRunes` | 32 characters. |
| `const int MaxRoomIdBytes` | 64 bytes. |
| `const int RoomCodeLength` | 6. |
| `const string RoomCodeAlphabet` | The characters room codes use; easily confused letters and digits are left out. |
| `const int MaxPlayersMin`, `const int MaxPlayersLimit`, `const int MaxPlayersServerDefault` | 1, 100 and 0 (let the server choose). |
| `static string ValidateRoomName(string name)` | An empty name is accepted. |
| `static string ValidateDisplayName(string name)` | An empty name is accepted. |
| `static string ValidateRoomId(string roomId)` | Letters, digits, `_` and `-`, up to 64 bytes. The SDK does not apply it when joining; use it for ids typed by players. |
| `static string ValidateRoomCode(string roomCode)` | Expects a code already passed through `NormaliseRoomCode`. |
| `static string ValidateMaxPlayers(int maxPlayers)` | |
| `static string NormaliseRoomCode(string roomCode)` | Upper-cases a code independently of the current culture. |
| `static int FirstUnsafeCharacterIndex(string s)` | Index of the first control or invisible formatting character, or `-1`. |

---

## LobbyManager

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class LobbyManager` · **Access** `NetworkManager.Instance.Lobby`

Browses the rooms of a lobby without joining one. A new `LobbyManager` is built on every
`Connect()` and reconnect attempt; its events keep their subscribers.

### Members

| Member | Description |
|---|---|
| `void JoinLobby(string lobbyName)` | Enters a lobby and starts receiving its room list. The name is required: up to 32 characters from `[A-Za-z0-9_-]`; an invalid name throws `ArgumentException` before anything is sent. The first list arrives through `OnRoomListUpdated`, and `IsInLobby` becomes `true` when the server confirms. |
| `void LeaveLobby()` | Leaves the lobby and clears `Rooms` at once. |
| `void ListRooms(LobbyQueryOptions opts)` | Requests one filtered, sorted room list. Throws `ArgumentNullException` for `null` and `ArgumentException` for an invalid lobby name or filter. |
| `string CurrentLobbyName { get; }` | The lobby this client is in, or `""`. |
| `bool IsInLobby { get; }` | Whether the server confirmed the lobby join. |
| `IReadOnlyList<LobbyRoomInfo> Rooms { get; }` | The last room list received. |
| `LobbyManager.LobbyListProblem LastProblem { get; }` | The problem found in the last room list, or `None` once a complete one arrives. Poll it to show and clear a "room list unavailable" notice. |
| `int RefusedRoomLists { get; }` | Room lists refused or shown short so far. |
| `event Action<IReadOnlyList<LobbyRoomInfo>> OnRoomListUpdated` | A room list arrived: the reply to `JoinLobby` or `ListRooms`, or an update while in the lobby. Also raised with an empty list when a join reply is refused whole and this client is taken out of the lobby. |
| `event Action<string> OnLobbyError` | A `JoinLobby` or a `ListRooms` query received no reply within 15 seconds. A timed-out join may still take effect on the server; call `LeaveLobby()` or `JoinLobby()` to be sure of the state. |

`LobbyManager.LobbyListProblem` values: `None`, `Unreadable` (the payload could not be
read), `UnsupportedVersion` (a newer format than this SDK reads), `TooManyEntries` (more
rooms than `NetworkSettings.maxLobbyRoomEntries`; refused whole), `RoomsOmitted` (rooms
that could not be read were left out, most often a name longer than
`maxLobbyStringBytes`) and `Refused` (the server declined the request, so the empty list
is not the lobby's contents). Each problem is also logged once.

### LobbyRoomInfo

**Declaration** `public sealed class LobbyRoomInfo`

| Member | Description |
|---|---|
| `string RoomId { get; }` | |
| `string RoomCode { get; }` | |
| `string Name { get; }` | |
| `int PlayerCount { get; }` | |
| `int MaxPlayers { get; }` | |
| `bool IsPublic { get; }` | |
| `string LobbyName { get; }` | |

### LobbyQueryOptions

**Declaration** `public sealed class LobbyQueryOptions`

| Member | Description |
|---|---|
| `string LobbyName { get; set; }` | Required; the rules of `JoinLobby`. Default `""`. |
| `int MaxResults { get; set; }` | 1–100. Default `0`: the server's default of 100. |
| `LobbySort SortBy { get; set; }` | `PlayerCount` (fullest first, the default), `Age` (oldest first) or `Name`. |
| `List<LobbyFilter> Filters { get; set; }` | Property filters a room must all satisfy. `null` or empty: no filter. |

`LobbyFilter` has `string Key` (a property key, up to 32 bytes), `LobbyFilterOp Op`
(`Eq`, `NotEq`, `Lt`, `Gt`, `LtEq`, `GtEq`) and `object Value` (a `string`, `int`,
`float`, `double` or `bool`; not `null`, NaN or infinite). `LobbyFilterValue` checks
filters the way `ListRooms` does: `DescribeKey`, `DescribeOp` and `Describe` return the
reason a part would be refused, or `null`, and `IsValid(object value)` checks a value.

`LobbyName` states the lobby-name rule: `LobbyName.MaxBytes` (32), `LobbyName.Alphabet`,
`LobbyName.IsValid(string name)` and `LobbyName.Describe(string name)`.

---

## MatchmakingManager

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class MatchmakingManager` · **Access** `NetworkManager.Instance.Matchmaking`

Join-or-create matchmaking: the server places the player in an open room with the same
mode and lobby, or creates one. A new `MatchmakingManager` is built on every `Connect()`
and reconnect attempt; its events keep their subscribers.

### Members

| Member | Description |
|---|---|
| `void StartMatchmaking(MatchmakingOptions options)` | Starts matchmaking with a 30-second timeout. Called from inside a room it does not leave the room first, as `CreateRoom` and `JoinRoom` do, and the server refuses it while the session holds a seat: call `LeaveRoom` first. |
| `void StartMatchmaking(MatchmakingOptions options, double timeoutSeconds)` | Starts matchmaking with a timeout: values above 300 are reduced to 300, zero or negative values use 30, and `double.PositiveInfinity` means no timeout. Throws `ArgumentNullException` for `null` options; `ArgumentException` for a `Mode` that is empty, longer than 64 bytes or contains a control character, for an invalid `LobbyName` or `DisplayName`, and for a NaN timeout; `InvalidOperationException` when not `Connected` or `InRoom`, or when a request is already in progress. |
| `void CancelFindMatch()` | Stops waiting for the request and raises `OnMatchmakingCancelled`. Does nothing when no request is in progress. |
| `bool IsMatchmaking { get; }` | Whether a request is in progress. |
| `void Tick(double nowSeconds)` | Advances the timeout. `NetworkManager` calls it every frame; you do not need to. |
| `event Action<MatchmakingResult> OnMatchmakingComplete` | The player was placed in a room. The SDK has already entered the room, so `Rooms.OnRoomJoined` fires just before this event. |
| `event Action<string> OnMatchmakingFailed` | The server refused the request or its reply could not be read; the argument is the reason. Also raised with `"session ended"` when the connection closed or was re-established during a request. |
| `event Action OnMatchmakingCancelled` | After `CancelFindMatch`. |
| `event Action OnMatchmakingTimedOut` | The timeout elapsed. |

If the server places the player after the request was cancelled or timed out, the late
reply still enters the room and raises `Rooms.OnRoomJoined`, but no matchmaking event.

### MatchmakingOptions

**Declaration** `public sealed class MatchmakingOptions`

| Member | Description |
|---|---|
| `string Mode { get; set; }` | Required game-mode key; players are matched with others asking for the same mode. Up to 64 bytes of UTF-8. |
| `string LobbyName { get; set; }` | Optional lobby. Empty: the room belongs to no lobby and does not appear in lobby lists. Otherwise the `LobbyName` rules apply. |
| `int MinPlayers { get; set; }` | Players needed to start. `0` or less: the server's default of 2. |
| `int MaxPlayers { get; set; }` | Capacity of a room created for the match. `0` or less: the server's default of 100. |
| `string DisplayName { get; set; }` | The name other players see, up to 32 characters. |

### MatchmakingResult

**Declaration** `public sealed class MatchmakingResult`

| Member | Description |
|---|---|
| `string RoomId { get; }` | The room the player was placed in. |
| `string RoomCode { get; }` | Its join code. |
| `bool Created { get; }` | `true` when the server created the room for this match. |

---

<a id="networked-scenes"></a>
## NetworkSceneManager

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class NetworkSceneManager` · **Access** `NetworkManager.Instance.Scene`

Coordinates a room-wide scene change. The host calls `LoadScene`; every client,
including the host, receives `OnSceneLoadStartedWithMode`, loads the scene itself and
calls `ReportReady`; when every player has reported, `OnAllPlayersSceneLoaded` fires on
every client. A player who joins a room that already has a scene receives
`OnSceneLoadStartedWithMode` on entry, once for each scene the room has open: the scene of
its last single-mode load, then each scene loaded additively over it, the room's latest
last. To have all
of this done for you, add [`RtmpeSceneLoader`](#rtmpesceneloader).

While this client loads the room's scene in single mode, what the room sends about its
objects — spawns, despawns, the buffered RPCs that address them — waits, and is applied when
this client calls `ReportReady()`, in the scene the load opened; a single-mode load destroys
whatever is built before it ends. RPCs that arrive meanwhile run after the room's buffered
ones. The wait applies only while something handles one of the two load events (a handler
that loads nothing should call `ReportReady()` at once), and it ends after
`SceneReadyTimeoutSeconds` (60 seconds when that is off, never more than 120) if nothing
reports, with a warning. Spawn your own player after your load rather than from
`Rooms.OnRoomJoined` when the room has a scene; `RtmpeConnectionBootstrap` does.

`NetworkManager.Scene` is `null` until the manager's `Awake` has run, so read it from
`Start` or later.

### Members

| Member | Description |
|---|---|
| `void LoadScene(string sceneName, NetworkSceneLoadMode mode = NetworkSceneLoadMode.Single)` | Tells the room to load a scene by writing the `__scene` and `__scene_additive` room properties, and `__scene_stack` — the scenes a later joiner loads — in the same write. Only the host's write is applied; another client's write is reported by `Rooms.OnRoomError` after 12 seconds. Writing the scene the room is already on starts it again for everyone. When the room's scenes do not fit in one property, or the room already holds 20 properties, the load still goes and a later joiner is told this scene alone, with a warning. Throws `ArgumentException` for an empty name and `InvalidOperationException` when not in a room. |
| `void ReportReady()` | Reports that this client finished loading the room's scene. The room completes a load only when every player, this one included, has reported it. Does nothing outside a room or when no scene is set. |
| `string CurrentScene { get; }` | The room's scene, or `""`. |
| `NetworkSceneLoadMode CurrentSceneLoadMode { get; }` | The room's load mode; `Single` when not set. |
| `float SceneReadyTimeoutSeconds { get; set; }` | How long a load may go unsettled before `OnSceneLoadTimedOut`; `0` or less turns the report off. Starts at `NetworkSettings.sceneReadyTimeoutSeconds`, or 60 seconds. A change applies from the next load. Turn it off only if every client always calls `ReportReady`; otherwise a load that never completes goes unreported. |
| `const float DefaultSceneReadyTimeoutSeconds` | 60. |
| `event Action<string, NetworkSceneLoadMode> OnSceneLoadStartedWithMode` | Load this scene in this mode. Raised for every scene write the room accepts, including one naming the scene the room is already on. Handle this event rather than `OnSceneLoadStarted`: loading an additive scene as `Single` unloads the scene the room is still in. |
| `event Action<string> OnSceneLoadStarted` | The same occasions, without the mode; raised just before `OnSceneLoadStartedWithMode`. Handle one of the two, not both. |
| `event Action<string> OnAllPlayersSceneLoaded` | Every player has reported this scene loaded. Start the round here, after checking that the argument equals `CurrentScene`: a late report from a round that a newer write replaced is not raised while it names the scene this client has not yet reported, but a late report naming a different scene is. |
| `event Action<string> OnSceneLoadTimedOut` | `SceneReadyTimeoutSeconds` passed after the load began without `OnAllPlayersSceneLoaded`. Raised once per load; if the room settles later, `OnAllPlayersSceneLoaded` still fires. |

`NetworkSceneLoadMode` values: `Single` (0) and `Additive` (1).

`OnSceneLoadTimedOut` cannot name the player who has not reported, because the server
reports only the completed round. Do not start the round from it: each client's timer
starts with its own load, so the room would split. Use it to show a notice, offer to
leave, or let the host restart the load. A player who joins while a scene is already set
loads it without a timeout, because the room's round for that scene may have completed
before it arrived.

If you write `__scene` yourself with `SetRoomProperties`, write `__scene_additive` with
it; otherwise the room keeps the mode of the previous load. A later joiner then loads that
scene alone: `__scene_stack` is read only while its last scene is `__scene`.

---

<a id="scene-loading-without-writing-any"></a>
## RtmpeSceneLoader

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class RtmpeSceneLoader : MonoBehaviour` · **Add with** **Add Component → RTMPE → Scene Loader**

Loads the scene the room asks for and reports when it is loaded, with no code: it
handles `OnSceneLoadStartedWithMode`, calls `SceneManager.LoadSceneAsync` in the room's
mode, and calls `ReportReady()` when the load finishes. It has no settings.

| Member | Description |
|---|---|
| `string LoadingScene { get; }` | The scene it is loading, or `null`. A newer instruction replaces it at once. |

- Put it on a root GameObject in your boot scene, or on the `NetworkManager`'s
  GameObject. On a root object it persists across scene loads. Placed where a `Single`
  load would destroy it, it logs an error when a load begins.
- Keep one. A copy in a scene the room loads becomes a second loader each time the room
  returns there; the most recently enabled copy acts, and an error is logged.
- The scene must be in the Build Settings. When Unity refuses the load, the loader logs
  the likely cause. **Window → RTMPE → Network Scenes** checks the scene names your
  scripts pass to `LoadScene` while you edit.
- An additive instruction for a scene that is already open is not loaded a second time:
  the client reports ready and logs why. To restart an additive scene, unload it first.
- Only the latest instruction reports readiness; a load it replaced finishes without
  reporting.

For a loading screen or a staged load, handle the `NetworkSceneManager` events yourself
instead. See the **Scene Transitions** sample.

---

## SpawnManager

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class SpawnManager` · **Access** `NetworkManager.Instance.Spawner`

Creates and destroys networked objects from registered prefabs, and relays each spawn
and despawn to the room. A new `SpawnManager` is built on every `Connect()` and
reconnect attempt: prefab registrations and `OnSpawnRejected` subscribers carry over,
but an object pool does not, so install your pool from `NetworkManager.OnConnected`.

### Prefab registration

Assign the registry that **Window → RTMPE → Network Prefabs** generates to
`NetworkSettings.prefabRegistry`, and every prefab in it is registered when a session is
built: before the first `Connect()` and again on every reconnect. Register prefabs the
registry does not list, such as ones loaded from an asset bundle, by hand.

| Member | Description |
|---|---|
| `void RegisterPrefab(uint prefabId, GameObject prefab)` | Maps an id to a prefab, replacing an existing mapping with a warning. Registrations made by hand are applied after the registry, so they win. Throws `ArgumentNullException` for a `null` prefab. |
| `bool UnregisterPrefab(uint prefabId)` | Removes a mapping; `true` when there was one. |
| `bool HasPrefab(uint prefabId)` | Whether an id is registered. |
| `bool TryGetPrefabId(GameObject prefab, out uint prefabId)` | The id this session spawns `prefab` under; the lowest one when several ids map to it. Use it instead of reading `NetworkPrefabRegistry.Entries`, which hand registrations can override. |
| `void LoadPrefabRegistry(NetworkPrefabRegistry registry)` | Loads a registry you obtained at run time, for example from Addressables. Rows without a prefab are skipped; a repeated id takes the prefab of the later row. Both are reported. `null` is ignored. |

- Every prefab the registry lists is loaded into memory with the scene that holds the
  `NetworkManager`, whether or not it is spawned. Leave large, rarely used prefabs out of
  the registry and register them by hand when you load them.
- On a reconnect the registry is loaded again and the previous session's registrations
  are carried over, so a prefab removed with `UnregisterPrefab` is registered again.

### Spawning and despawning

| Member | Description |
|---|---|
| `NetworkBehaviour Spawn(uint prefabId, Vector3 position, Quaternion rotation, string ownerPlayerId = null, bool sharedAuthority = false)` | Creates the object (through the pool when one is installed) and relays the spawn to the room. Returns its first `NetworkBehaviour`, or `null` when the id is not registered, the prefab has no `NetworkBehaviour`, `ownerPlayerId` names another player, or `maxSpawnsPerSecond` or `maxSpawnsPerRoom` is reached. Check the result. |
| `event Action<ulong, SpawnPacketParser.SpawnRejectReason> OnSpawnRejected` | The room refused a spawn this client made: `(objectId, reason)`. The local object is kept but reaches no other player; if you cannot use it, destroy it through the reference `Spawn` returned, not with `Despawn`. The refusal notice can be lost on the network, and then the event is not raised. |
| `void Despawn(ulong networkObjectId)` | Destroys the object (or returns it to the pool) and relays the despawn. Refused, with a logged error and nothing destroyed, for an object another player owns. |
| `bool CanDespawn(ulong networkObjectId)` | Whether `Despawn` would be relayed: `true` for an object this client owns, one with no owner, and an unknown id. |
| `bool TryDespawn(ulong networkObjectId)` | `Despawn` when allowed; `false`, doing nothing, for another player's object. |

`Spawn` details:

- Spawn after `Rooms.OnRoomJoined`. An object spawned before this client has a seat in a
  room is created locally, reaches no other player, and a warning is logged.
- A client spawns only under its own player id. To give an object to another player,
  spawn it yourself and use `OwnershipManager.RequestOwnershipTransfer`.
- `sharedAuthority: false` (the default) lets only the owner call RPCs on the object;
  `true` lets any member of the room call them. Use `true` for shared objects such as a
  door or a scoreboard.
- Object ids are chosen by the spawning client from its session's own id range, so two
  clients never pick the same id.
- `NetworkBehaviour.DestroyWithOwner`, read when the object is spawned, decides what
  happens when the owner leaves: `true` destroys the object on every client; `false`
  gives it to the room's host.

`SpawnPacketParser.SpawnRejectReason` values: `OwnerCollision` (the id already belongs
to another player), `RoomAtObjectCeiling` (the room holds as many objects as it
allows), `PlayerAtObjectCeiling` (this player has created as many of the room's objects
as the server allows one player; the room's host is not held to it), `ForeignIdSpace`
(the id belongs to another session's range), and `Unknown` (a reason this SDK version
does not know).

The constructor, `OnPlayerJoinedRoom` and `OnPlayerLeftRoom` are used by the SDK and
are not intended to be called from game code.

### Object pool

| Member | Description |
|---|---|
| `void SetObjectPool(INetworkObjectPool pool)` | Routes instantiation and destruction through a pool from the next spawn. `null` restores `Instantiate` and `Destroy`. Objects already alive are released to whichever pool is installed when they despawn. |
| `void ClearObjectPool()` | Removes the pool. |
| `INetworkObjectPool ObjectPool { get; }` | The installed pool, or `null`. |

### Other members

| Member | Description |
|---|---|
| `NetworkObjectRegistry Registry { get; }` | The live objects. See [`NetworkObjectRegistry`](#networkobjectregistry). |
| `OwnershipManager Ownership { get; }` | Ownership transfers. See [`OwnershipManager`](#ownershipmanager). |
| `void MarkAllVariablesDirtyForResync()` | Marks every NetworkVariable on every object this client owns to be sent again at the next flush. `OnValueChanged` does not fire. The SDK calls it when a player joins. |
| `void ScheduleFollowUpResync()` | Schedules a second full resend one second later. The SDK calls it when a player joins. |
| `void ClearAll(bool resetObjectIdSpace = true)` | Despawns and destroys (or releases) every networked object on this client. Nothing is sent to the room, so other players keep their copies. The SDK calls it when leaving a room and when disconnecting. If you call it while staying connected, pass `false`: `true` restarts the object id sequence, and objects spawned afterwards can reuse ids the room still remembers, so other players do not see them. |

---

## NetworkPrefabRegistry

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class NetworkPrefabRegistry : ScriptableObject`

A table of prefab ids and prefabs. **Window → RTMPE → Network Prefabs** generates it,
together with the `RtmpePrefabIds` constants; assign it to
`NetworkSettings.prefabRegistry`. **Create → RTMPE → Prefab Registry** creates an empty
one. Ids are allocated by the Network Prefabs window's ledger, and the next generation
replaces every row, so edits made by hand do not last.

| Member | Description |
|---|---|
| `IReadOnlyList<NetworkPrefabEntry> Entries { get; }` | The rows, in order; never `null`. |

`NetworkPrefabEntry` is one row: `uint id`, `GameObject prefab`, and the constructors
`NetworkPrefabEntry()` and `NetworkPrefabEntry(uint id, GameObject prefab)`.

---

## INetworkObjectPool

**Namespace** `RTMPE.Core` · **Declaration** `public interface INetworkObjectPool`

A pool that `SpawnManager` uses instead of `Instantiate` and `Destroy`. Install it with
`SpawnManager.SetObjectPool`. The SDK does not include a pool implementation.

| Member | Description |
|---|---|
| `GameObject Acquire(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation)` | Returns an instance of `prefab`. Do not return `null`: the SDK logs an error and falls back to `Instantiate`. The SDK positions and activates the instance. |
| `void Release(uint prefabId, GameObject instance)` | Takes an instance back when it despawns; typically deactivate it and keep it. `prefabId` is `uint.MaxValue` when the SDK does not know the prefab; destroy such an instance. |

- The SDK calls both methods on the main thread.
- If `Release` throws during a single `Despawn`, the SDK logs the exception and destroys
  the instance; when leaving a room or disconnecting, it only logs, so the instance is
  lost.
- Before `Release`, every NetworkVariable on the instance that was created outside
  `OnNetworkSpawn` (in a field initialiser, `Awake` or `OnEnable`) is reset to the
  value its constructor set, and `OnValueChanged` (or a list's `Clear` event) fires. The
  next spawn therefore starts from the prefab's values.

---

## NetworkObjectRegistry

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class NetworkObjectRegistry` · **Access** `NetworkManager.Instance.Spawner.Registry`

The live networked objects, by object id. Each object is registered under its first
`NetworkBehaviour`. Main thread only.

| Member | Description |
|---|---|
| `NetworkBehaviour Get(ulong objectId)` | The object, or `null`. An entry whose GameObject was destroyed is removed and answers `null`. |
| `IReadOnlyList<NetworkBehaviour> GetAll()` | A snapshot of the live objects. |
| `void GetAllSnapshot(IList<NetworkBehaviour> destination)` | Fills `destination` (cleared first) without allocating a list. Throws `ArgumentNullException` for `null`. |
| `bool Register(NetworkBehaviour obj)` | Used by `SpawnManager`. |
| `bool Register(NetworkBehaviour obj, out NetworkBehaviour evicted)` | Used by `SpawnManager`. |
| `void Unregister(ulong objectId)` | Used by `SpawnManager`. |
| `int PruneDestroyed()` | Removes entries whose GameObjects Unity destroyed and returns how many; `OnNetworkDespawn` does not run for them. The SDK calls it after scene loads. |
| `void Clear()` | Used by `SpawnManager` when leaving a room. |

An object is registered after its `OnNetworkSpawn` has run, so `Get` returns `null` for
it inside `OnNetworkSpawn`.

---

## OwnershipManager

**Namespace** `RTMPE.Core` · **Declaration** `public sealed class OwnershipManager` · **Access** `NetworkManager.Instance.Spawner.Ownership`

Moves objects between players. The server decides every transfer. When it grants one,
each client applies the change and each component's `OnOwnershipChanged` runs; see
`OnOwnershipTransferUnanswered` for a grant that arrives late.

| Member | Description |
|---|---|
| `void RequestOwnershipTransfer(ulong objectId, string newOwnerPlayerId)` | Asks the server to give an object this client owns to another player. Only the current owner can ask: otherwise it logs an error and sends nothing. Logs a warning for an unknown object or when not connected. Throws `ArgumentException` for an empty `newOwnerPlayerId`. |
| `event Action<ulong, string> OnOwnershipTransferUnanswered` | A transfer this client asked for received no answer within 10 seconds: `(objectId, newOwnerPlayerId)`. The server does not answer a refused transfer, so this is how a refusal shows, but the transfer can also still be granted later. A grant that arrives after this report is applied by the other clients; this client applies it only if it is the room's host, and otherwise keeps treating itself as the owner. Treat the outcome as unknown rather than as a refusal. |
| `IReadOnlyList<NetworkBehaviour> GetObjectsOwnedBy(string playerId)` | The live objects a player owns. |

When a player leaves, their objects with `DestroyWithOwner` set are destroyed, and the
others pass to the room's host on every client.

The constructor, `ApplyOwnershipGrant` and `ReassignObjectsToNewOwner` are used by the
SDK and are not intended to be called from game code: they change ownership on this
client only.

---

## NetworkBehaviour

**Namespace** `RTMPE.Core` · **Declaration** `public abstract class NetworkBehaviour : MonoBehaviour`

The base class for scripts on networked objects. Derive from it instead of
`MonoBehaviour` for any script that owns networked state or RPCs. One object may carry
several `NetworkBehaviour` components; they share the object's id and owner.

### Properties

| Member | Description |
|---|---|
| `ulong NetworkObjectId { get; }` | The object's id, chosen by the client that spawned it. `0` before it is spawned. |
| `string OwnerPlayerId { get; }` | The owner's player id (the `PlayerInfo.PlayerId` of that player). `""` before it is spawned. |
| `bool IsOwner { get; }` | Whether this client owns the object. Guard input handling and NetworkVariable writes with it. |
| `bool IsSpawned { get; }` | `true` from just before `OnNetworkSpawn` runs until just before `OnNetworkDespawn` runs. |
| `bool DestroyWithOwner { get; set; }` | Whether the object is destroyed when its owner leaves the room. Default `true`; with `false`, the room's host takes it over. Set it before spawning: the value at spawn time is what the room is told. |
| `IReadOnlyList<NetworkVariableBase> TrackedVariables { get; }` | The NetworkVariables registered on this component. Do not modify it. |
| `protected ulong CurrentRpcSender { get; }` | Inside an `[RtmpeRpc]` method: the caller's session id (see `NetworkManager.CurrentRpcSenderId`); `0` elsewhere. |
| `protected RpcCallerFacts CurrentRpcCaller { get; }` | Inside an `[RtmpeRpc]` method: what the server reports about the caller. `OwnsObject` is reported only on the object the call addressed. `None` elsewhere. |

### Methods

| Member | Description |
|---|---|
| `void RPC(string methodName, params object[] args)` | Calls an `[RtmpeRpc]` method. See [Calling an RPC](#calling-an-rpc). |

### Methods to override

| Member | Description |
|---|---|
| `protected virtual void OnNetworkSpawn()` | The object is on the network. Construct NetworkVariables here. `IsSpawned`, `IsOwner`, `NetworkObjectId` and `OwnerPlayerId` are set; the object is not in the registry yet. If a base class of your component overrides it, call `base.OnNetworkSpawn()` first (analyzer rule `RTMPE1022`). |
| `protected virtual void OnNetworkDespawn()` | The object is leaving the network. Unsubscribe from NetworkVariable events here. `IsSpawned` is already `false`. |
| `protected virtual void OnOwnershipChanged(string previousOwner, string newOwner)` | The owner changed. Not raised when the owner stays the same. |
| `protected virtual void OnFixedTick(float deltaTime)` | Called once per simulation tick on every spawned object this client owns, with the tick interval. A long frame calls it several times and a short frame not at all; at most 8 ticks run per frame, and the surplus is dropped. It stops while `Time.timeScale` is 0. |
| `protected virtual InputPayload GatherInput()` | Client-side prediction: returns this tick's input on the owner. Only a `NetworkTransform` calls it, on itself, when its `EnablePrediction` is on: override it in a class derived from `NetworkTransform` and use that component. Return `default` when there is no input. |
| `protected virtual void ApplyInput(InputPayload input, float deltaTime)` | Client-side prediction: applies one input again after a correction. Called, like `GatherInput`, only on a `NetworkTransform` itself: override both in the same derived class. It must be deterministic and use `deltaTime`, not `Time.deltaTime`. |
| `protected virtual void OnDestroy()` | Keeps the SDK's records right when the GameObject is destroyed outside the SDK. If you override it, call `base.OnDestroy()` (analyzer rule `RTMPE1020`). |

Override the lifecycle methods as `protected override`.

### InputPayload

**Declaration** `public struct InputPayload`

| Member | Description |
|---|---|
| `uint Tick` | Filled in by the SDK; leave it `0`. |
| `float MoveX` | Horizontal input, −1 to 1. Values outside the range are clamped when sent; NaN and infinity are refused. |
| `float MoveY` | Forward and back input, −1 to 1, clamped the same way. |
| `bool Jump` | Whether jump is pressed. |

---

<a id="world-authority"></a>
## RtmpeWorldAuthority and RtmpeWorldSpawner

**Namespace** `RTMPE.Core`

A room's **world object** holds state no player owns, such as pickups, a generated
layout or the round state. Put `RtmpeWorldAuthority` on a prefab beside the
`NetworkBehaviour` that holds the world's NetworkVariables, give that prefab an id in
**Window → RTMPE → Network Prefabs**, and add an `RtmpeWorldSpawner` to the scene. The
room's host spawns the world once per room; when the host leaves, the next host takes it
over; a player who joins late receives it with the room's other objects. The
**Shared World** sample uses these components.

### RtmpeWorldAuthority

**Declaration** `public sealed class RtmpeWorldAuthority : NetworkBehaviour` · **Add with** **Add Component → RTMPE → World Authority**

| Field | Type | Default | Description |
|---|---|---|---|
| `WorldKey` | `string` | `"world"` | The name `Find` looks the world up by. One world per key per room, and the same key on every client. |
| `SharedAuthority` | `bool` | `true` | Spawn the world so any member of the room can call its RPCs. Off, only the owner can, so players who are not the host cannot ask the world for anything. |
| `PersistAcrossSceneLoads` | `bool` | `true` | Keep this client's copy across scene loads. Off, a `Single` scene load destroys it; the owner tells the room, and the next scene's spawner creates a new world. |

| Member | Description |
|---|---|
| `static RtmpeWorldAuthority Find(string worldKey)` | The live world for the key on this client, or `null`. Call it when you need the world instead of keeping a reference: the instance can change. |
| `static event Action<RtmpeWorldAuthority> OnWorldReady` | Raised on every client when a key's world becomes a different instance, including its first spawn. One event for all keys: read `WorldKey` from the argument. Raised from the instance's first `Update`; nothing is raised when a world goes away. |
| `event Action OnWorldBorn` | Raised on the owner when a world that holds none of the room's state becomes the key's answer: populate it here. Subscribe from a sibling component's `OnNetworkSpawn`; `Find` returns this instance inside the handler. |
| `string WorldKey { get; }` | The configured key. |
| `bool SharedAuthority { get; }` | The configured value. |
| `bool PersistAcrossSceneLoads { get; }` | The configured value. |
| `bool WasRecreated { get; }` | `true` on a copy this client spawned to replace another instance of the world, with its contents carried over. |

The prefab's root GameObject must be active and its **World Authority** component
enabled; the spawner refuses a prefab that is not. Every `NetworkBehaviour` on the world
survives its owner leaving, whatever its own `DestroyWithOwner` says.

### RtmpeWorldSpawner

**Declaration** `public sealed class RtmpeWorldSpawner : MonoBehaviour` · **Add with** **Add Component → RTMPE → World Spawner**

| Field | Type | Default | Description |
|---|---|---|---|
| `WorldPrefab` | `GameObject` | none | The prefab that carries `RtmpeWorldAuthority`; its key and shared-authority setting are read from that component. The world appears at the spawner's transform. |

| Member | Description |
|---|---|
| `string WorldKey { get; }` | The key of the world it spawns, read from the prefab; `null` when there is no prefab or it has no `RtmpeWorldAuthority`. |

### Behaviour

- **Who spawns.** Only the host. A host who entered the room alone spawns at once; one
  who entered with others waits a second and a half for the room's existing objects, and
  spawns only if no world arrived. A player promoted to host takes over the existing
  world; if none exists, it waits the same second and a half and spawns one only if none
  has arrived.
- **Host changes.** When the owner leaves and the server hands the departed host's
  objects to the new host, the new host takes over the same object, with its variables as
  they were, and sends them to every player. Otherwise the new host spawns a copy that
  carries the world's state and removes the original, and `OnWorldReady` reports the
  copy. A new host that did not
  receive every variable of the world before the change raises `OnWorldBorn`: the values
  it received are kept, the handler fills in the rest, and the world is then sent to
  every player.
- **Populating.** Write the world's variables in `OnWorldBorn`. If the handler spawns
  objects, count what the room already holds first: a world can be born again in a room
  that still holds objects spawned for a world before it.
- **Two instances.** For a moment two instances of one key can exist, for example when
  two hosts' spawns cross. Every client treats the one with the smaller object id as the
  world, and the other's owner removes it.
- **Ownership.** `IsOwner` on the world tells you who owns it. `TransferMasterClient`
  moves the host role, not the world.
- **Refused spawns.** The world counts against the room's object limit. If the room
  refuses its spawn because it holds as many objects as it allows, the owner tries again
  with a new copy after 1, 2, 4 and 8 seconds, then every 8 seconds. After a refusal for
  any other reason, the world exists on this client only, and a warning is logged.
- **Destroyed outside the SDK.** When the owner's copy is destroyed by a scene load or by
  `Object.Destroy`, the other players' copies are removed too.

Checks that no client may skip belong in your server function; see
[Server functions](#server-functions). The host is a client like any other.

---

<a id="interest-management"></a>
## InterestManager

**Namespace** `RTMPE.Rooms` · **Declaration** `public sealed class InterestManager : MonoBehaviour`

Optional distance-based filtering for large rooms. While in a room, the component
reports the position of `TrackedTransform` to the server every `UpdateInterval`
seconds, and the server uses it to send this client state updates only for objects near
it. A client without an `InterestManager` receives every update. Keep one enabled: every
enabled `InterestManager` reports its own `TrackedTransform`, while the receive filter
uses the most recently enabled one.

| Member | Description |
|---|---|
| `Transform TrackedTransform` | The transform whose position is reported, usually the local player. While it is `null`, nothing is sent. |
| `float UpdateInterval` | Seconds between reports. Default `0.1` (10 per second); range `MinUpdateInterval`–5. |
| `const float MinUpdateInterval` | 0.05. Smaller values are raised to it with a warning. |
| `bool UseXzPlane` | Report X and Z (3-D games with Y up). Default `true`; turn it off for 2-D games that use X and Y. |
| `float ReceiveFilterRadius` | When above `0`, this client also discards incoming state for objects farther away than this, in world units. Default `0` (off). |
| `float HysteresisMargin` | Extra distance before a visible object is dropped by the receive filter, to stop objects at the edge flickering in and out. Default `1`. `NetworkSettings.interestHysteresisMargin` replaces it while that setting is `0` or more, which it is by default. |
| `void StartTracking()` | Resumes reporting. |
| `void StopTracking()` | Pauses reporting; the server keeps using the last reported position. |
| `bool IsTracking { get; }` | Whether reporting is on and the component is enabled. |

Positions that are NaN or infinite are not reported; a warning is logged.

---

## Remote procedure calls

**Namespace** `RTMPE.Rpc` for the attributes and types; the calls are on
`NetworkBehaviour` and `NetworkManager` (`RTMPE.Core`).

An RPC is a method one client calls and other clients (or your server function) run.
Mark the method with `[RtmpeRpc]` and call it by name with `RPC`.

```csharp
using RTMPE.Core;
using RTMPE.Rpc;
using UnityEngine;

public class Weapon : NetworkBehaviour
{
    public void Fire()
    {
        if (!IsOwner) return;
        RPC(nameof(FireRpc), transform.position);
    }

    // Runs on every client in the room, the caller included.
    [RtmpeRpc(RpcTarget.All)]
    public void FireRpc(Vector3 origin)
    {
        Debug.Log($"Shot fired from {origin}");
    }
}
```

### RtmpeRpcAttribute

**Declaration** `public sealed class RtmpeRpcAttribute : Attribute` (used as `[RtmpeRpc]`)

| Member | Description |
|---|---|
| `RtmpeRpcAttribute(RpcTarget target = RpcTarget.All)` | Marks a method as an RPC for the given audience. |
| `RpcTarget Target { get; }` | Who runs the call. |
| `RpcCaller Caller { get; set; }` | Who may make the call. Default `Anyone`. |

An RPC method must be a public instance method of a `NetworkBehaviour` subclass, with a
name no other RPC method of the type uses (no overloads) and supported parameter types.
Its id is derived from the type's full name and the method name, and must not equal a
reserved id (`RpcMethodId`) or another method's id. The analyzers report these problems
while you edit (rules `RTMPE1001` to `RTMPE1006`). At run time a collision is logged
when the type first spawns, and that type's RPCs are not delivered.

### RpcTarget

**Declaration** `public enum RpcTarget : byte`

| Value | Audience |
|---|---|
| `RpcTarget.All` | Every client in the room, the caller included. |
| `RpcTarget.Others` | Every client in the room except the caller. |
| `RpcTarget.Server` | No client runs it, the caller included: it executes only in the project's server function, and the answer returns to the caller (see [Server functions](#server-functions)). Without a registered server function the call is answered `UnknownMethod`. |
| `RpcTarget.AllBuffered` | Like `All`, and also delivered to players who join later. The room keeps a bounded number of buffered calls and drops the oldest first; see the [late-join snapshot](../architecture.md#8-late-join-snapshot). |

### RpcCaller and RpcCallerFacts

A method can declare who may call it. The caller's own SDK refuses a call it may not
make, and every receiving client checks the declaration against what the server reports
about the caller, never against anything the caller wrote.

```csharp
using RTMPE.Core;
using RTMPE.Rpc;

public class Scoreboard : NetworkBehaviour
{
    // Only the room's host may award points.
    [RtmpeRpc(RpcTarget.All, Caller = RpcCaller.Host)]
    public void AwardPoints(string playerId, int points)
    {
    }
}
```

| `RpcCaller` value | Who may call |
|---|---|
| `Anyone` (default) | Any member of the room. |
| `Owner` | The owner of the object the call addresses, or the server. |
| `Host` | The room's host, or the server. |
| `Server` | Only the server: a call your server function sends to the room. No client can make it, the host included. |

`RpcCallerFacts` is a `[Flags]` enum describing the caller of a received call: `None`,
`OwnsObject` (the caller owns the object the call addressed), `IsHost` (the caller is the
room's host) and `IsServer` (the server made the call). Read it inside a handler through
`CurrentRpcCaller` (on `NetworkBehaviour`) or `NetworkManager.CurrentRpcCallerFacts`.

- A call the caller may not make is not sent: `RPC` logs why, and
  `SendEnhancedRpcAsync` throws `InvalidOperationException`.
- A receiving client that refuses a call counts it in
  `NetworkManager.RpcCallerRefusedCount`.
- For a `RpcTarget.Server` method, the declaration is checked only when the call is
  made. Your server function receives the caller's identity and decides by it.

### Calling an RPC

| Member | Description |
|---|---|
| `void RPC(string methodName, params object[] args)` | On `NetworkBehaviour`. Calls the `[RtmpeRpc]` method `methodName` of this component's type (declared on it or inherited). Use `nameof(...)` so a rename is a compile error. Must be called in a room. A call that cannot be sent (unknown method, a `null` or unsupported argument, arguments too large to send, a declared caller this client does not satisfy) logs a warning and sends nothing. The arguments are not checked against the method's parameters before sending: a call with the wrong number or types of arguments is sent, and every receiver drops it with an error. |

Supported parameter types: `int`, `float`, `bool`, `string`, `byte[]`, `ulong`,
`Vector3`, `Color`, `Quaternion`, and types that implement `INetworkSerializable` (see
[Custom parameter types](#custom-parameter-types)). The encoded arguments of one call
must fit in `EnhancedRpcPacketBuilder.MaxSendablePayloadBytes` (1128 bytes), and a call
takes at most 255 arguments.

On each receiving client the call runs on the component of the addressed object that
declares the method:

- The first `NetworkBehaviour` on the GameObject is checked first; if it does not
  declare the method, the component that does receives the call.
- If two components of the same type (for example the same component added twice)
  declare it, the first component receives the call when it is one of them; otherwise
  the call is not delivered. Keep one of them per object.
- A call is delivered only while the object is spawned. A call whose arguments do not
  match the method is logged and dropped, and an exception the method throws is caught
  and logged.

### Server calls with a reply

| Member | Description |
|---|---|
| `Task<RpcResponse> SendEnhancedRpcAsync(NetworkBehaviour sender, string methodName, object[] args, TimeSpan? timeout = null, CancellationToken cancellationToken = default)` | On `NetworkManager`. Calls a `RpcTarget.Server` method of `sender`'s type and completes with the server's answer. |

- The task completes with an `RpcResponse` when the answer arrives; a refusal by your
  server function is an answer with `Success == false`, not an exception.
- The task is cancelled when the timeout has passed (default `DefaultServerRpcTimeout`,
  30 seconds), when `cancellationToken` is cancelled, and when the session ends. The
  timeout is checked every 5 seconds, so the task can be cancelled up to about 5 seconds
  after it; `cancellationToken` takes effect at once, so for a precise deadline pass a
  token from a `CancellationTokenSource` with `CancelAfter`. Awaiting a cancelled task
  throws `TaskCanceledException`. A token that is already cancelled returns a cancelled
  task and sends nothing.
- Thrown at the call, before anything is sent: `InvalidOperationException` when not in
  a room, for a `null` sender, an unknown method name, a method whose target is not
  `RpcTarget.Server`, or a declared `Caller` this client does not satisfy;
  `ArgumentException` for arguments that cannot be encoded or are too large.
- Only answers from the server complete the task; an answer from another player is
  ignored and counted in `NetworkManager.ServerRpcAnswersRefusedCount`.

```csharp
using System;
using System.Threading.Tasks;
using RTMPE.Core;
using RTMPE.Rpc;
using UnityEngine;

public class Shop : NetworkBehaviour
{
    // Runs in the project's server function, never on a client.
    [RtmpeRpc(RpcTarget.Server)]
    public void Purchase(string itemId)
    {
    }

    public async Task BuyAsync(string itemId)
    {
        try
        {
            RpcResponse response = await NetworkManager.Instance.SendEnhancedRpcAsync(
                this, nameof(Purchase), new object[] { itemId });

            if (response.Success && response.TryReadResult(out object[] values))
                Debug.Log($"Purchase confirmed with {values.Length} value(s).");
            else
                Debug.LogWarning($"Purchase refused: {response.ErrorCode}");
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("No answer: the call timed out or the session ended.");
        }
    }
}
```

### RpcResponse

**Declaration** `public readonly struct RpcResponse`

| Member | Description |
|---|---|
| `readonly uint RequestId` | Matches the answer to the request. |
| `readonly uint MethodId` | The method's id. |
| `readonly ulong SenderId` | `0` for an answer from the server. |
| `readonly bool Success` | `false` when `ErrorCode` explains a failure. |
| `readonly RpcErrorCode ErrorCode` | See below. |
| `readonly byte[] Payload` | The encoded result; empty when there is none. |
| `bool TryReadResult(out object[] values)` | Decodes `Payload` into the values your server function returned. `true` with an empty array for an empty payload; `false` when the payload cannot be decoded. |
| `RpcResponse(uint requestId, uint methodId, ulong senderId, bool success, RpcErrorCode errorCode, byte[] payload)` | Constructs a response, for tests. |

### RpcErrorCode

**Declaration** `public enum RpcErrorCode : ushort`

| Value | Meaning |
|---|---|
| `OK` | Success. |
| `Unauthorized` | The server function refused the caller. |
| `UnknownMethod` | No server function handles the method, or none is registered. |
| `HandlerError` | The server function failed or answered outside the contract. |
| `OversizedPayload` | The arguments were too large. |
| `Timeout` | The server function did not answer in time. |
| `Unavailable` | The server function could not be called: its endpoint did not answer, failed too often recently, may not be called, or too many calls were in flight for the caller or the project. |
| `Unknown` | A code this SDK version does not know. |

### Server functions

A `RpcTarget.Server` call runs in your **server function**: an HTTPS endpoint you run
and register in the RTMPE Developer Portal under **Project → Server functions**. It
decides what no client should decide, such as damage, scores and purchases.

**The request** is a `POST` with a JSON body:

```json
{
  "version": 1,
  "call_id": "9f2c0d4a7b1e4c55a3f2e8d1c0b9a7e6",
  "sent_at": 1727170000,
  "project_id": 42,
  "room_id": "room-abc",
  "caller": { "session_id": "1234", "player_id": "p-1", "is_host": true, "owns_object": false },
  "object_id": "4294967297",
  "method_id": 3141592653,
  "args": [ { "type": "int", "value": 3 }, { "type": "string", "value": "sword" } ]
}
```

- `method_id` is `RpcRegistry.ComputeMethodId(typeof(Shop), nameof(Shop.Purchase))`
  for the method called; `object_id` is the object's id as a decimal string.
- Each argument names its type: `int`, `float`, `bool`, `string`, `bytes` (base64),
  `vector3`, `color` and `quaternion` (arrays of 3, 4 and 4 numbers), `ulong` (a decimal
  string) and `serializable` (a `type_name` and a base64 `value`).
- Each call is sent once and never retried.

**The signature.** Every request carries an `RTMPE-Signature` header of the form
`t=<unix seconds>,kid=<key id>,v1=<base64 Ed25519 signature>`. Verify it with the public
key shown in the portal, over these four parts joined by a newline (`\n`):
`RTMPE-SERVER-FUNCTION-v1`, the `t` value, your endpoint URL exactly as registered, and
the raw request body. Then refuse a request older than five minutes, a `project_id`
that is not yours (all projects' calls are signed with the same key), and a `call_id`
you have already accepted within those five minutes.

**The answer.** Answer within 1.5 seconds with status `200` and a JSON body. A call
times out after 3 seconds, and answers slower than half of that count as failures of
the endpoint.

```json
{
  "ok": true,
  "result": [ { "type": "int", "value": 7 } ],
  "broadcast": [
    { "method_id": 12345, "object_id": "4294967297", "target": "all",
      "args": [ { "type": "bool", "value": true } ] }
  ]
}
```

- `result` (optional) reaches the caller as `RpcResponse.Payload`; read it with
  `TryReadResult`.
- `broadcast` (optional, at most 8 entries) are calls the server makes in the room:
  every client holding the object runs the method. `target` is `all`, `others` (everyone
  but the caller) or `all_buffered` (also delivered to later joiners), and must match the
  method's declared target. Declare such methods with `Caller = RpcCaller.Server` so no
  client can make the same call.
- To refuse, answer `{"ok": false, "error": "unauthorized"}` (`Unauthorized`) or
  `{"ok": false, "error": "unknown_method"}` (`UnknownMethod`). Any other error, a
  non-2xx status, or a body outside this shape is `HandlerError`. The whole answer is
  checked before any of it is applied.

### Custom parameter types

A type implementing `INetworkSerializable` can be an RPC parameter. The receiving
client must be able to create it, so every such type must also be registered on every
client, before the first call carrying it arrives. Register it with
`RpcTypeRegistry.Register<T>()` (recommended, and required under IL2CPP), with
`RpcTypeRegistry.Register(Type)`, or by marking it `[RtmpeRpcSerializable]` and setting
`RpcTypeRegistry.AllowAppDomainScan = true`. On a client where the type is not
registered, that argument arrives as `null` and a warning is logged: a method whose
parameter is a class runs with `null`, and a call to a method whose parameter is a
struct is dropped with an error. Registrations are cleared when Play mode starts, so
register from a start-up method.

```csharp
using RTMPE.Rpc;
using UnityEngine;

public struct HitInfo : INetworkSerializable
{
    public Vector3 Point;
    public int Damage;

    public void NetworkSerialize(IRtmpeWriter writer)
    {
        writer.WriteVector3(Point);
        writer.WriteInt32(Damage);
    }

    public void NetworkDeserialize(IRtmpeReader reader)
    {
        Point = reader.ReadVector3();
        Damage = reader.ReadInt32();
    }
}

public static class RpcTypes
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register() => RpcTypeRegistry.Register<HitInfo>();
}
```

| Type | Members |
|---|---|
| `INetworkSerializable` | `void NetworkSerialize(IRtmpeWriter writer)`; `void NetworkDeserialize(IRtmpeReader reader)`, called on a new instance. Read the values in the order they were written. A send calls `NetworkSerialize` twice (to measure, then to write); it must write the same data both times, or the call is not sent. Classes need a public parameterless constructor. |
| `IRtmpeWriter` | `WriteInt32`, `WriteFloat`, `WriteBool`, `WriteUInt64`, `WriteUInt16`, `WriteByte`, `WriteString` (up to 65535 bytes of UTF-8; `null` is written as `""`), `WriteBytes` (up to 65535 bytes), `WriteVector3`, `WriteQuaternion`, `WriteColor`. |
| `IRtmpeReader` | The matching `Read…` methods, and `bool HasFailed { get; }`: after a read past the end of the data it is `true` and every read returns a default value; the argument is then treated as unreadable. |
| `RtmpeRpcSerializableAttribute` | `[RtmpeRpcSerializable]` marks a type for the scan `AllowAppDomainScan` enables. |

`RpcTypeRegistry` (static):

| Member | Description |
|---|---|
| `static void Register<T>() where T : INetworkSerializable, new()` | Registers `T`. Safe to call more than once. |
| `static void Register(Type type)` | Registers a type known only at run time. Throws `ArgumentNullException` for `null` and `ArgumentException` for an abstract type or one without a public parameterless constructor. |
| `static bool AllowAppDomainScan { get; set; }` | When `true`, types marked `[RtmpeRpcSerializable]` are found automatically the first time an unknown type name arrives. Default `false`; reset when Play mode starts. |
| `static bool IsRegistered(string fullName)` | Whether a type name is registered. |
| `static IReadOnlyList<string> RegisteredTypeNames()` | The registered type names. |
| `static Type Resolve(string fullName)` | The registered type for a name, or `null`. |

### RpcRegistry

**Declaration** `public static class RpcRegistry`

| Member | Description |
|---|---|
| `static uint ComputeMethodId(Type type, string methodName)` | The id of an RPC method: a 32-bit FNV-1a hash of the type's full name and the method name. Throws `ArgumentNullException` for a `null` type. |
| `static uint ComputeMethodId(string typeName, string methodName)` | The same from a type name; pass the full name. |
| `static bool TryGetMethodId(Type type, string methodName, out uint methodId)` | The id of a named `[RtmpeRpc]` method, or `false`. |
| `static void Validate(Type type)` | Throws `InvalidOperationException` listing every id collision on the type. |

### Built-in method ids

`RpcMethodId` lists the reserved ids of the SDK's built-in method-id calls (`Ping`,
`TransferOwnership`, `RequestDamage`, `ApplyDamage`, `GameStateChange`, `SyncGameState`).
An `[RtmpeRpc]` method whose id equals one of them is refused (analyzer rule `RTMPE1004`).
`NetworkManager.SendRpc` sends these calls; games use `[RtmpeRpc]` methods instead.

---

## IDamageable

**Namespace** `RTMPE.Core` · **Declaration** `public interface IDamageable`

Receives the built-in `ApplyDamage` method-id call. The RTMPE service does not send this
call, so `ReceiveApplyDamage` is not called in a game connected to it. Prefer
`[RtmpeRpc]` methods for new code: for damage the server decides, declare a method with
`Caller = RpcCaller.Server` and send it from your server function (see
[Server functions](#server-functions)).

| Member | Description |
|---|---|
| `void ReceiveApplyDamage(int damage)` | Called on each client that receives the call, on the main thread, with a positive amount. Calls with zero or negative damage are dropped. |

The handler is looked up with `GetComponentInParent<IDamageable>()` from the object's
first `NetworkBehaviour`.

A health component that only your server function can damage, written with an
`[RtmpeRpc]` method:

```csharp
using System;
using RTMPE.Core;
using RTMPE.Rpc;
using RTMPE.Sync;

public class PlayerHealth : NetworkBehaviour
{
    private NetworkVariableInt _health;

    protected override void OnNetworkSpawn()
    {
        _health = new NetworkVariableInt(this, nameof(_health), 100);
    }

    // Sent by the server function in the "broadcast" list of its answer.
    [RtmpeRpc(RpcTarget.All, Caller = RpcCaller.Server)]
    public void TakeDamage(int damage)
    {
        if (!IsOwner) return;
        _health.Value = Math.Max(0, _health.Value - damage);
    }
}
```

---

## NetworkTransform

**Namespace** `RTMPE.Sync` · **Declaration** `public class NetworkTransform : NetworkBehaviour` · **Add with** **Add Component → RTMPE → Network Transform**

Synchronises a GameObject's position, rotation and, optionally, scale. The owner
broadcasts its pose when it moves more than a threshold, and about once a second while
it is still. Other clients draw the object through the `NetworkTransformInterpolator`
that Unity adds with this component.

The owner's broadcast movement is limited to
`NetworkSettings.maxOwnerVelocityMetersPerSecond` (50 units per second by default); use
`OwnerTeleportTo` to move instantly.

### Inspector fields

| Field | Type | Default | Description |
|---|---|---|---|
| `SyncPosition` | `bool` | `true` | Synchronise position. Off, moving does not trigger a broadcast and received updates do not move the replica. The flag does not make an update smaller. |
| `SyncRotation` | `bool` | `true` | The same, for rotation. |
| `SyncScale` | `bool` | `false` | The same, for local scale. A replica applies received scale only when `InterpolateScale` is on as well on its `NetworkTransformInterpolator`. |
| `TickAlignedSampling` | `bool` | `true` | Broadcast the pose held on the tick boundary instead of the one held at the frame that sends it. See [Tick-aligned sampling](#tick-aligned-sampling). |
| `PositionThreshold` | `float` | `0.01` | Movement, in world units, that triggers a broadcast. |
| `RotationThreshold` | `float` | `0.1` | Rotation, in degrees, that triggers a broadcast. |
| `ScaleThreshold` | `float` | `0.001` | Distance between the last sent and the current scale that triggers a broadcast; used only with `SyncScale`. |
| `EnablePrediction` | `bool` | `false` | Client-side prediction for the owner: input from `GatherInput` is recorded each tick and replayed with `ApplyInput` after a correction. Override those two methods in a class derived from `NetworkTransform`; the SDK calls them on this component only. Corrections reach owned objects only when `NetworkSettings.reconcileOwnedObjects` is on; with prediction off, an accepted correction snaps. |
| `LerpThreshold` | `float` | `-1` | With `EnablePrediction` on, the correction threshold: a position error below it is accepted without a visible correction; a larger one is blended in over about 0.1 seconds. `-1` uses `NetworkSettings.reconcileLerpThreshold`. |
| `SnapThreshold` | `float` | `-1` | With `EnablePrediction` on, a position error above it snaps instead of blending. `-1` uses `NetworkSettings.reconcileSnapThreshold`. |

### Members

| Member | Description |
|---|---|
| `bool HasPositionChanged { get; }` | Whether `SyncPosition` is on and the object moved more than `PositionThreshold` since the last baseline. |
| `bool HasRotationChanged { get; }` | The same, for rotation. |
| `bool HasScaleChanged { get; }` | The same, for scale. |
| `TransformState GetState()` | The current position, rotation and local scale. |
| `void ApplyState(TransformState state)` | Writes a state to the transform, for the axes whose sync flag is on. The SDK moves replicas through the interpolator, not through this method. |
| `void MarkClean()` | Records the current transform as the baseline the thresholds are measured from. |
| `void OwnerTeleportTo(Vector3 worldPosition)` | Owner only. Moves the object at once, without the jump counting against the velocity limit, and cancels any correction in progress. The destination is sent on the next simulation tick, like any movement past the Position Threshold; where the server supports it (`CapabilityFlags.TransformTeleport`) it is marked as a teleport, and other players see the object arrive there instead of travelling there at their speed limit. The server honours one teleport per object per second; a faster one still arrives, at the other players' speed limit. Ignored, with a warning, on an object this client does not own and for a NaN or infinite position. |

### TransformState

**Declaration** `public struct TransformState`

| Member | Description |
|---|---|
| `Vector3 Position` | World position. |
| `Quaternion Rotation` | World rotation. |
| `Vector3 Scale` | Local scale. |
| `uint ConfirmedInputTick`, `bool HasConfirmedInputTick` | On a correction: the owner input tick the server had applied. |
| `uint ServerTick`, `bool HasServerTick` | On a received update: the server tick it was broadcast in. |
| `static TransformState Identity { get; }` | Origin, no rotation, unit scale. |

---

## NetworkTransformInterpolator

**Namespace** `RTMPE.Sync` · **Declaration** `public class NetworkTransformInterpolator : MonoBehaviour` · **Add with** **Add Component → RTMPE → Network Transform Interpolator** (added automatically with Network Transform)

Draws a replica smoothly. It keeps a buffer of received poses and draws the object a
short delay behind the newest one, interpolating position linearly and rotation
spherically. If poses stop arriving, it continues along the last movement for up to
`MaxExtrapolationSeconds` and then holds. It does nothing on the owner. A pose older
than the newest one received is discarded.

### Inspector fields

| Field | Type | Default | Description |
|---|---|---|---|
| `BufferSize` | `int` | `10` | Poses kept (2–64). A value outside the range uses 10, with a warning. A change applies the next time the buffer is empty. |
| `InterpolationDelay` | `float` | `0.1` | Seconds behind the newest pose (0.067–0.5), which absorbs uneven arrival. The minimum is two ticks, 0.067 seconds; a smaller value is raised to it with a warning. With `AdaptiveDelay`, this is the largest delay used. |
| `AdaptiveDelay` | `bool` | `true` | Vary the delay between two ticks and `InterpolationDelay` with the measured arrival spread, so a steady link is drawn closer to real time. See [Adaptive delay](#adaptive-delay). |
| `OwnerTickTimeline` | `bool` | `true` | Time poses by the owner's tick instead of the server's broadcast tick. See [Owner-tick timeline](#owner-tick-timeline). |
| `MaxExtrapolationSeconds` | `float` | `0.05` | How long to continue along the last movement when no newer pose has arrived (0–0.5), easing to a stop over that time. `0` holds the last pose until the next one arrives. |
| `InterpolateScale` | `bool` | `false` | Apply received local scale, interpolated. It takes effect only when the `NetworkTransform`'s `SyncScale` is also on: turn both on to replicate scale. |
| `MaxInterpolatedSpeed` | `float` | `50` | Speed limit, in units per second, for received movement: a pose that jumps further is approached at this speed instead of snapped to. `0` turns the limit off. |
| `MaxFutureSkewSeconds` | `double` | `10` | How far ahead of the local clock a received pose may be timed; later poses are refused. A value outside 0.001 seconds to 30 days uses 10. |

### Members

| Member | Description |
|---|---|
| `int BufferCount { get; }` | Poses in the buffer. |
| `float InterpolationDelaySeconds { get; }` | The delay in force, after the two-tick minimum and `AdaptiveDelay`. |
| `bool TryGetLatestAcceptedState(out TransformState state)` | The newest accepted pose, after the speed limit. `false` when none has been accepted since the object became a replica. |
| `bool TryInterpolate(double renderTime, out TransformState result)` | The interpolated pose at `renderTime`. `false` with fewer than two poses or a time outside the buffer. |
| `void AddState(TransformState state, double timestamp)` | Adds a pose timed by the local clock (`Time.unscaledTimeAsDouble`). |
| `void AddStateFromSenderTick(TransformState state, uint senderTick, double receiverNow, double tickIntervalSeconds)` | Adds a pose timed by the sender's tick. |
| `void AddStateFromBroadcast(TransformState state, uint serverTick, bool hasServerTick, uint ownerTick, bool hasOwnerTick, double receiverNow, double ownerTickIntervalSeconds)` | Adds a received update, choosing its timeline as `OwnerTickTimeline` says. |

The SDK adds received poses itself; the `AddState` methods are for custom pipelines and
tests.

---

## Remote motion timing

Three settings decide how smoothly remote objects move, and all three are on by
default: [Tick-aligned sampling](#tick-aligned-sampling) on `NetworkTransform` (the
sending side), and the [Owner-tick timeline](#owner-tick-timeline) and
[adaptive delay](#adaptive-delay) on `NetworkTransformInterpolator` (the receiving side).

They are serialized fields, so a prefab keeps the values it was saved with. Use the
same values on every prefab: remote objects from a prefab with both timeline settings on
and from one with both off are drawn on different timelines, up to about a tick apart.

### Owner-tick timeline

Every received update carries two ticks: the server's broadcast tick and the owner's
input tick. Timed by the broadcast tick, a remote object moving at a steady speed can
show small, uneven steps the owner never made. `NetworkTransformInterpolator.OwnerTickTimeline`
times each pose by the owner's tick instead, so the steps follow the owner's own
movement. When an update does not carry the chosen tick, the pose is timed by its
arrival.

The owner's tick describes the owner's own timing only when every client uses the same
`NetworkSettings.tickRate`. If your clients run different tick rates, turn off both
timeline settings.

### Tick-aligned sampling

An owner reads its transform in `Update`, at a frame, but labels the sample with the
current tick, and receivers play it back as if it had been taken on the tick boundary.
When the frame rate is not a multiple of the tick rate, the gap between the two drifts
across the tick and appears as jitter. `NetworkTransform.TickAlignedSampling`
interpolates the broadcast pose back to the tick boundary, between the current sample
and the previous one, so receivers see the pose the owner held at that tick.

> **Important:** Enable both, or neither. The two settings remove different errors, and
> each leaves the other's in place. `TickAlignedSampling` removes the owner's
> frame-sampling error, which is at most one frame. `OwnerTickTimeline` removes a larger
> one: timed by the broadcast tick, a pose can be placed up to a tick away from the
> moment the owner held it. Enabling one is a partial improvement, not the feature.

When a remote object's update reaches a prefab that has one of the two settings on and
the other off, the SDK logs a single warning naming the half that is set, the half that
is missing, and the error that remains. It is logged once per configuration, naming the
object where it was seen. The check needs a second client, because objects you own are
not checked, and it reads your local prefab, so it tells you whether your project is
configured consistently, not what another player's build does.

### Adaptive delay

`InterpolationDelay` is a fixed buffer that absorbs uneven arrival at the cost of
latency on every remote object. `AdaptiveDelay` varies it between two ticks and the
configured value according to the measured arrival spread, so a steady link is drawn
closer to real time and an uneven one keeps its buffer. It is independent of the two
timeline settings and can be used alone.

---

## NetworkRigidbody / NetworkRigidbody2D

**Namespace** `RTMPE.Sync` · **Declarations** `public class NetworkRigidbody : NetworkBehaviour` (requires `Rigidbody`) and `public class NetworkRigidbody2D : NetworkBehaviour` (requires `Rigidbody2D`) · **Add with** **Add Component → RTMPE → Network Rigidbody** or **Add Component → RTMPE → Network Rigidbody 2D**

The owner sends its body's physics state; receivers are meant to follow it with velocity
blending and dead reckoning.

> **Important:** The RTMPE server does not relay the state these components send, so
> they do not move other players' copies. For a physics object other players must see,
> use `NetworkTransform` and make the `Rigidbody` kinematic on clients that do not own
> it.

### Inspector fields

| Field | Type | Default | Description |
|---|---|---|---|
| `SyncPosition` | `bool` | `true` | Send position. |
| `SyncRotation` | `bool` | `true` | Send rotation. |
| `SyncVelocity` | `bool` | `true` | Send linear velocity, so remote bodies keep moving between updates. |
| `SyncAngularVelocity` | `bool` | `true` | Send angular velocity. |
| `SyncSleepState` | `bool` | `true` | Put the remote body to sleep when the owner's body sleeps. |
| `SyncConstraints` | `bool` | `true` | Send constraint changes made at run time (see `NetworkSettings.allowDynamicConstraints`). |
| `PositionThreshold` | `float` | `0.01` | Movement, in world units, that triggers an update. |
| `RotationThreshold` | `float` | `0.1` | Rotation, in degrees, that triggers an update. |
| `VelocityThreshold` | `float` | `0.05` | Velocity change, in units per second, that triggers an update. |
| `AngularVelocityThreshold` | `float` | `0.05` (3-D), `1` (2-D) | Angular velocity change that triggers an update: radians per second in 3-D, degrees per second in 2-D. |
| `MakeRemoteKinematic` | `bool` | `false` | Make the body kinematic on non-owners and set its pose directly. For bodies the owner controls completely, such as player characters. |
| `SnapThreshold` | `float` | `3` | Position error, in world units, above which the remote body jumps instead of moving smoothly. |
| `PositionCorrectionSpeed` | `float` | `10` | How fast the remote body moves toward the received position (1–50). |
| `RotationCorrectionSpeed` | `float` | `10` | How fast the remote body turns toward the received rotation (1–50). |
| `VelocityBlendRate` | `float` | `10` | 3-D only. How fast the remote body's velocity converges on the received one, per second (0.1–60). |
| `AngularVelocityBlendRate` | `float` | `10` | 3-D only. The same, for angular velocity (0.1–60). |
| `SnapDistanceThreshold` | `float` | `1.5` | 3-D only. Error, in world units, above which a non-kinematic remote body is placed directly instead of moved through the physics step (0.1–50). |
| `EnableOwnerReconciliation` | `bool` | `false` | Snap the owner's body to a server correction when it has drifted more than the thresholds below. Only for a server that simulates physics authoritatively. |
| `OwnerReconcileSnapThreshold` | `float` | `3` | Position error, in world units, that makes the owner snap (0.5–20). |
| `OwnerReconcileRotationSnapDegrees` | `float` | `30` | Rotation error, in degrees, that makes the owner snap (1–180). |
| `EnableDeadReckoning` | `bool` | `true` | Continue the remote body along its last velocity between updates. |
| `DeadReckoningTimeout` | `float` | `0.5` | Seconds after the last update when dead reckoning stops (0.1–2). |
| `SendRateHz` | `int` | `20` | Updates the owner sends per second (1–30). |

### Members

| Member | Description |
|---|---|
| `PhysicsState GetState()` | `NetworkRigidbody`: the body's current state. |
| `PhysicsState2D GetState()` | `NetworkRigidbody2D`: the body's current state. |

`PhysicsState` fields: `Vector3 Position`, `Quaternion Rotation`, `Vector3 Velocity`,
`Vector3 AngularVelocity` (radians per second), `bool IsSleeping`,
`byte ConstraintMask`. `PhysicsState2D` fields: `Vector2 Position`, `float Rotation`
(degrees), `Vector2 Velocity`, `float AngularVelocity` (degrees per second),
`bool IsSleeping`, `byte ConstraintMask`.

Received physics state is checked against the limits under
[Physics receive checks](#physics-receive-checks) in `NetworkSettings`; NaN and infinite
values are always refused.

---

## NetworkVariable types

**Namespace** `RTMPE.Sync`

A NetworkVariable is a value the object's owner writes and every client in the room
receives. Changes are sent at the next replication tick; `OnValueChanged` fires on the
owner when it writes and on every other client when the value arrives.

```csharp
using System;
using RTMPE.Core;
using RTMPE.Sync;
using UnityEngine;

public class Fighter : NetworkBehaviour
{
    private NetworkVariableInt _health;
    private Action<int, int> _onHealthChanged;

    protected override void OnNetworkSpawn()
    {
        _health = new NetworkVariableInt(this, nameof(_health), 100);
        _onHealthChanged = (previous, current) => Debug.Log($"Health {previous} -> {current}");
        _health.OnValueChanged += _onHealthChanged;
    }

    protected override void OnNetworkDespawn()
    {
        if (_health != null) _health.OnValueChanged -= _onHealthChanged;
    }

    public void TakeDamage(int amount)
    {
        if (!IsOwner) return;
        _health.Value = Math.Max(0, _health.Value - amount);
    }
}
```

### Rules

1. Construct variables in `OnNetworkSpawn`, on every client, without conditions. A
   variable constructed only on some clients (for example behind `IsOwner`) receives
   nothing where it is missing.
2. Pass `nameof(_field)`: the name of the field the variable is assigned to. A
   variable's identity is derived from the component's type name and that name, the
   same on every client, and must be unique across the object, not only within its
   component. Two components of the same type on one object (or two constructions of one
   generic component type) produce the same identities; the second one's variables are
   reported as an error and do not replicate. Within one component, constructing two
   variables under the same name throws `InvalidOperationException`. Renaming the type or
   the field changes the identity, so every client must be updated together.
3. Only the owner writes `Value`. Every client reads it and reacts through
   `OnValueChanged`.
4. Keep the delegate you subscribe with and unsubscribe with it in `OnNetworkDespawn`.

### NetworkVariable\<T\>

**Declaration** `public abstract class NetworkVariable<T> : NetworkVariableBase where T : struct, IEquatable<T>`

| Member | Description |
|---|---|
| `T Value { get; set; }` | The value. Setting a different value stores it, marks the variable for sending and raises `OnValueChanged`; setting the same value does nothing. See [Refused writes](#refused-writes). |
| `event Action<T, T> OnValueChanged` | `(previous, current)`: on the owner when it writes, and on every other client when a different value arrives. Not raised by `SetValueWithoutNotify`. An exception in a handler is caught and logged. |
| `bool CanSend(T value)` | Whether assigning `value` would be accepted. See [Refused writes](#refused-writes). |
| `bool TrySetValue(T value)` | Assigns `value` and returns whether it took effect. `true` when the value is already held. |
| `void SetValueWithoutNotify(T value)` | Stores a value without raising `OnValueChanged` and without sending it. Ignored when the object is not spawned. |
| `protected NetworkVariable(NetworkBehaviour owner, string memberName, T initialValue = default)` | Registers the variable with `owner`. `initialValue` is set without an event. Throws `ArgumentException` for an empty `memberName`. |
| `protected virtual bool IsSendableValue(T value, out string reason)` | Override in a custom type to refuse values its own reader would refuse. |
| `protected internal void ApplyFromWire(T value)` | For a custom type's `Deserialize`: stores a received value and raises `OnValueChanged`. |

### NetworkVariableBase

**Declaration** `public abstract class NetworkVariableBase`

| Member | Description |
|---|---|
| `uint VariableId { get; }` | The variable's identity, derived from the owner's type name and the member name. |
| `bool IsDirty { get; }` | Whether a change is waiting to be sent. |
| `float SendRateHz { get; set; }` | The most updates this variable sends per second. `0` (default) sends at every replication tick. Negative values become `0`. See [Per-variable send rate](#per-variable-send-rate). |
| `virtual void MarkClean()` | Clears `IsDirty`. The SDK calls it after sending. |
| `abstract void Serialize(BinaryWriter writer)` | Writes the value. Implement it in a custom type. |
| `abstract void Deserialize(BinaryReader reader)` | Reads a received value. Implement it in a custom type, and store the value with `ApplyFromWire`. |
| `protected NetworkBehaviour Owner { get; }` | The component the variable belongs to. |

### Available types

| Type | Value type | Notes |
|---|---|---|
| `NetworkVariableInt` | `int` | |
| `NetworkVariableFloat` | `float` | Refuses NaN and infinity. |
| `NetworkVariableBool` | `bool` | |
| `NetworkVariableVector2` | `Vector2` | Refuses a NaN or infinite component. |
| `NetworkVariableVector2Int` | `Vector2Int` | Components are sent as 32-bit integers, so large values stay exact. |
| `NetworkVariableVector3` | `Vector3` | Refuses a NaN or infinite component. |
| `NetworkVariableQuaternion` | `Quaternion` | A rotation that is not a valid unit quaternion is sent as the nearest valid one, with a warning. `default(Quaternion)` is (0, 0, 0, 0), not the identity: pass `Quaternion.identity` as the initial value. |
| `NetworkVariableString` | `string` | See below. |

Each type has the constructor `(NetworkBehaviour owner, string memberName, T initialValue = default)`.

`NetworkVariableString` derives from `NetworkVariableBase` and has the same members as
`NetworkVariable<T>`: `Value`, `OnValueChanged`, `CanSend`, `TrySetValue` and
`SetValueWithoutNotify`, and the constructor
`(NetworkBehaviour owner, string memberName, string initialValue = "")`. `null` is
stored as `""`. It refuses a string that is not valid UTF-8 (for example one a
`Substring` cut in the middle of a surrogate pair) or longer than 65533 bytes of UTF-8.

Keep a `NetworkVariableString` value under about 1,100 bytes of UTF-8 (fewer characters
when the text is not ASCII), and split longer text across several variables. A value
must fit in one datagram with its framing to be sent. A longer value is accepted: it is
stored, `OnValueChanged` fires on the owner, and `CanSend` and `TrySetValue` return
`true`, but it is not sent, and a warning is logged once a second until a shorter value
is written.

### Refused writes

A refused write changes nothing: the value is not stored, no event is raised, and the
variable is not marked for sending. The setter cannot report this; use `CanSend` to ask
or `TrySetValue` to find out. A write is refused when:

- the value is one the variable refuses (see the table above);
- the object belongs to another player (an object with no owner, spawned before this
  client entered a room, is local and accepts writes);
- the object is not spawned: after `OnNetworkDespawn`, and for a single-value variable
  also before the first `OnNetworkSpawn`.

A warning is logged at most once a second for refused assignments. Values arriving from
the owner are not affected. A `NetworkVariableString` value too long for one datagram is
not refused but is not sent either; see [Available types](#available-types).

### Late joiners

When another player joins the room, each client sends every variable it owns at the
next replication tick, and again one second later. `OnValueChanged` does not fire on the
owner for this. The newcomer receives the values through its own `OnValueChanged`.

### Per-variable send rate

By default a changed variable is sent at the next replication tick (`tickRate`, 30 per
second). To send a variable less often, set `SendRateHz` after constructing it.
Intermediate values are not sent, but the latest one always is. A rate above the tick
rate has no further effect.

```csharp
protected override void OnNetworkSpawn()
{
    _health = new NetworkVariableInt(this, nameof(_health), 100);
    _health.SendRateHz = 10f;   // at most 10 updates per second
}
```

`NetworkVariableAttribute` (`[NetworkVariable]`) is an attribute for fields and
properties that hold a NetworkVariable; the analyzer checks where it is used (rule
`RTMPE1013`). Set the send rate with `SendRateHz` as shown above.

### Custom variable types

To replicate another value type, derive from `NetworkVariable<T>` and implement
`Serialize` and `Deserialize`. In `Deserialize`, read the value and store it with
`ApplyFromWire(value)`, which raises `OnValueChanged` on the receiving client;
`SetValueWithoutNotify` does not. Override `IsSendableValue` to refuse values your
`Deserialize` would refuse.

---

## NetworkVariableList

**Namespace** `RTMPE.Sync` · **Declaration** `public abstract class NetworkVariableList<T> : NetworkVariableBase, IReadOnlyList<T>`

A replicated list, such as an inventory, active effects or a grid of cells. The owner
edits it; every client receives each change. It follows the NetworkVariable
[rules](#rules): construct it in `OnNetworkSpawn` with `nameof(_field)`.

Concrete types: `NetworkVariableListInt`, `NetworkVariableListFloat`,
`NetworkVariableListBool`, `NetworkVariableListString`, `NetworkVariableListVector2`,
`NetworkVariableListVector2Int` and `NetworkVariableListVector3`. The `RTMPE2002`
conversion turns a `List<T>` field of one of these element types into the matching list;
see [Automation](../automation.md).

```csharp
// Constructor (call it in OnNetworkSpawn, like any NetworkVariable)
new NetworkVariableListInt(NetworkBehaviour owner, string memberName);
```

```csharp
// List API — owner writes; every client reads.
int  Count { get; }
T    this[int index] { get; set; }       // setting raises a Set change
void Add(T item);
void Insert(int index, T item);
void RemoveAt(int index);
bool Remove(T item);
void Clear();
bool Contains(T item);
int  IndexOf(T item);
List<T>.Enumerator GetEnumerator();      // allocation-free foreach; LINQ works through IReadOnlyList<T>

// Capacity
int  MaxCount { get; }                   // the most elements this list can hold
bool IsFull { get; }                     // at MaxCount, or no room left for the smallest element
bool CanAdd(T item);                     // whether Add(item) would succeed
bool TryAdd(T item);                     // Add, returning false when refused
bool CanSend(T value);                   // whether the element itself can be sent

// Changes
event Action<NetworkVariableListChangeEvent<T>> OnListChanged;
int  FullSyncOpThreshold { get; set; }   // pending edits above which the whole list is sent; default 32
```

- **Change events.** `OnListChanged` fires on the owner and on every receiver, after the
  change is applied, once per change. `NetworkVariableListChangeEvent<T>` has `Kind`,
  `Index` (`-1` for `Clear` and `FullSync`), `NewValue` and `PreviousValue`.
  `NetworkListChangeKind` values: `Add`, `Insert`, `RemoveAt`, `Set`, `Clear` and
  `FullSync` (the whole list was replaced by the owner's copy). A `FullSync` is also sent
  when a player joins and periodically, usually with the same contents, so treat it as
  "read the list again".
- **Refused edits.** `Add`, `Insert` and the indexer do nothing when the list is full or
  the element cannot be sent, and a warning is logged once a second; `TryAdd` reports
  it instead. Edits are refused on a list another player owns and after
  `OnNetworkDespawn`. Fill the list on the owner in `OnNetworkSpawn`, after constructing
  it. `RemoveAt`, `Insert` and the indexer throw `ArgumentOutOfRangeException` for an
  invalid index.
- **Capacity.** `MaxCount` is the smaller of `NetworkSettings.maxNetworkVariableListSize`
  (default 1024) and what one datagram carries, because a joining player receives the
  whole list in one datagram. That is 283 `int`s or `float`s, 141 `Vector2`s or
  `Vector2Int`s, and 94 `Vector3`s; a `bool` list reaches the configured limit first. A
  `string` list is limited by its total size: `MaxCount` counts empty strings, and
  `TryAdd` answers for a particular string. Fill a variable-size list with
  `while (list.TryAdd(next))`, not a loop on `IsFull`.
- **Updates.** Edits are sent as changes. When more than `FullSyncOpThreshold` edits are
  waiting, or they would need more than one flush, the whole list is sent instead if it
  fits one datagram. A received update that cannot be read is discarded whole.
- **Repair.** A lost change stays lost until the whole list is sent again: when a player
  joins, when more than `FullSyncOpThreshold` edits are waiting, and once the list has
  gone `NetworkSettings.networkVariableListFullSyncIntervalSeconds` (default 5) without
  being sent. Every send restarts that interval, so a list edited more often than the
  interval is not repaired by it. For a small list that changes continuously, lower
  `FullSyncOpThreshold`: at `0`, every change sends the whole list.
- **Elements.** A `NetworkVariableListString` refuses an element that is not valid
  UTF-8 or longer than 65535 bytes, and stores `null` as `""`. Float, `Vector2` and
  `Vector3` lists accept NaN and infinite elements.

---

## NetworkTransport

**Namespace** `RTMPE.Transport` · **Declaration** `public abstract class NetworkTransport : IDisposable`

The base class for transports. The SDK uses `UdpTransport` unless a factory installed
with `NetworkManager.SetTransportFactory` returns another one. The SDK calls these
members from its network thread and may call `Disconnect` from the main thread at the
same time, so an implementation must allow concurrent calls and must not call Unity
APIs.

| Member | Description |
|---|---|
| `abstract bool IsConnected { get; }` | Whether the transport is open. |
| `virtual IPEndPoint LocalEndPoint { get; }` | The local address. The SDK waits for it to become non-null before sending the first handshake packet, so a custom transport must return its bound address once it is ready; the base implementation returns `null`. Read from the main thread as well as the network thread. |
| `abstract void Connect()` | Opens the transport. |
| `abstract void Disconnect()` | Closes it; safe to call more than once. |
| `abstract void Send(byte[] data)` | Sends one datagram; throws `InvalidOperationException` when not connected. Do not keep a reference to `data`. |
| `abstract int Receive(byte[] buffer)` | Copies one waiting datagram into `buffer` and returns its length. Must not block: returns `0` at once when nothing is waiting, and `ReceiveSourceRejected` after reading and discarding a datagram. Never write past `buffer`, and return a count that covers every byte written: the buffer is reused, and only the reported length is cleared first. |
| `abstract bool Poll(int microSeconds)` | Whether a datagram is waiting, waiting up to `microSeconds`. |
| `abstract void Dispose()` | Releases the transport. |
| `const int ReceiveSourceRejected` | `-1`: a datagram was read and discarded, and more may be waiting. Any other negative value ends the current receive pass. |

---

## UdpTransport

**Namespace** `RTMPE.Transport` · **Declaration** `public sealed class UdpTransport : NetworkTransport`

The built-in, non-blocking UDP transport.

| Member | Description |
|---|---|
| `UdpTransport(string host, int port, int sendBufferBytes = DefaultSocketBufferBytes, int receiveBufferBytes = DefaultSocketBufferBytes, TimeSpan? dnsTimeout = null, int maxDatagramSize = DefaultMaxDatagramSize)` | Throws `ArgumentException` for a `null`, empty or blank host and `ArgumentOutOfRangeException` for a port outside 1–65535, a timeout that is not positive, or a datagram size outside 1–65507. |
| `const int DefaultSocketBufferBytes` | 262144. |
| `const int DefaultMaxDatagramSize` | 1200. |
| `static readonly TimeSpan DefaultDnsTimeout` | 3 seconds. |
| `int MaxDatagramSize { get; }` | Largest datagram `Send` accepts; a larger one throws `ArgumentException`. |
| `void Send(byte[] buffer, int offset, int count)` | Sends part of a buffer without copying it. |
| `long SendBufferExhaustedCount { get; }` | Sends that found the operating system's send buffer full. |
| `long DroppedSourceMismatchCount { get; }` | Datagrams dropped because they did not come from the server's address. |

- The host is resolved at the first `Connect` and the address is reused by later
  ones. An IPv4 address is preferred and IPv6 is used when there is none. `Connect`
  throws `TimeoutException` when resolution takes longer than the DNS timeout.
- Datagrams from any address other than the server's are dropped.
- Temporary socket errors (no data waiting, an unreachable port or route) are treated
  as "no data".

---

## SimulatedLinkTransport

**Namespace** `RTMPE.Transport` · **Declaration** `public sealed class SimulatedLinkTransport : NetworkTransport`

Wraps another transport and adds delay, jitter, loss and, optionally, reordering in both
directions, so you can test your game under a poor connection. The Link Simulator in
**Window → RTMPE → Network Debugger** installs one for the next session; you can also
install one in your own transport factory. It changes the link, not the server. See
[Testing under a bad link](../troubleshooting.md#testing-under-a-bad-link--the-link-simulator).

```csharp
using RTMPE.Core;
using RTMPE.Transport;

public static class BadLinkHarness
{
    public static void Install()
    {
        NetworkManager.SetTransportFactory(settings => new SimulatedLinkTransport(
            new UdpTransport(settings.serverHost, settings.serverPort),
            new LinkConditions(250, 50, 5f, reorder: false)));
    }
}
```

```csharp
// SimulatedLinkTransport
SimulatedLinkTransport(NetworkTransport inner, LinkConditions conditions);
SimulatedLinkTransport(NetworkTransport inner, LinkConditions conditions, int seed);  // the same seed gives the same drops
NetworkTransport Inner { get; }              // the wrapped transport; disposed with this one
LinkConditions   Conditions { get; set; }    // may change during a session; applies to the next datagram
LaneReadings     Outbound { get; }           // what this client's sends went through
LaneReadings     Inbound { get; }            // what the wrapped transport delivered
const int MaxQueuedPerDirection = 4096;
const int MaxReleasedAtClose = 64;
```

- Each direction holds at most `MaxQueuedPerDirection` datagrams; a datagram offered to
  a full direction is dropped and counted as `Overflowed`.
- On `Disconnect`, the newest `MaxReleasedAtClose` outbound datagrams still waiting are
  sent in order before the socket closes; older ones, and inbound datagrams still
  waiting, are counted as `Discarded`.
- A datagram the wrapped transport refuses when it is finally sent is dropped and
  counted as `Outbound.Refused`; a full send buffer keeps it for the next attempt,
  counted in `Outbound.BufferExhausted`. The network thread's own
  `NetworkManager.PerPacketFaultCount` and `EnobufsCount` stay at `0` for these.

### LinkConditions

**Declaration** `public readonly struct LinkConditions : IEquatable<LinkConditions>`

| Member | Description |
|---|---|
| `LinkConditions(int delayMs, int jitterMs, float lossPercent, bool reorder)` | Values are clamped: delay 0–`MaxDelayMs`, jitter 0–`MaxJitterMs`, loss 0–100 (NaN and infinity read as 0). |
| `int DelayMs { get; }` | Delay applied in each direction, so a round trip costs twice this. |
| `int JitterMs { get; }` | Each datagram's delay varies randomly by up to this much either way, never below zero. |
| `float LossPercent { get; }` | Share of datagrams dropped in each direction. |
| `bool Reorder { get; }` | Whether jitter may deliver a datagram before one sent earlier. Off, datagrams keep their order and bunch up instead. |
| `bool IsTransparent { get; }` | No delay, jitter or loss. |
| `static LinkConditions None { get; }` | No shaping. |
| `const int MaxDelayMs` | 10000. |
| `const int MaxJitterMs` | 5000. |
| `string Describe()` | A readable summary, such as `250 ms ± 50 ms, 5 % loss, order kept`, or `no shaping`. `ToString()` returns the same. |
| `Equals`, `==`, `!=` | Compare the four values. |

### LaneReadings

**Declaration** `public readonly struct LaneReadings`

What one direction did with its datagrams since the transport was created. At any
moment, `Offered` equals `Lost + Overflowed + Delivered + Refused + Discarded + Queued`.

| Member | Description |
|---|---|
| `long Offered { get; }` | Datagrams offered. |
| `long Lost { get; }` | Dropped by the loss setting. |
| `long Delivered { get; }` | Passed on: to the wrapped transport (outbound) or to the SDK (inbound). |
| `long Overflowed { get; }` | Dropped because the direction was full. |
| `long Reordered { get; }` | Delivered after a datagram offered later. |
| `long Refused { get; }` | Refused at delivery. |
| `long Discarded { get; }` | Dropped when the transport closed or reconnected. |
| `long BufferExhausted { get; }` | Outbound only: sends postponed because the operating system's send buffer was full. |
| `int Queued { get; }` | Waiting. |

---

## MainThreadDispatcher

**Namespace** `RTMPE.Threading` · **Declaration** `public sealed class MainThreadDispatcher : MonoBehaviour`

Runs work on the Unity main thread. Use it to call the SDK from another thread. It
persists across scene loads.

| Member | Description |
|---|---|
| `static MainThreadDispatcher Instance { get; }` | The dispatcher, created on first access. The first access must be on the main thread; `NetworkManager` creates it at start-up. |
| `static MainThreadDispatcher Prewarm()` | Creates the dispatcher. Call it from a main-thread `Awake` or `Start`. |
| `static bool IsMainThread { get; }` | Whether the caller is on the main thread. |
| `static bool MainThreadIsKnown { get; }` | Whether the main thread has been identified yet. |
| `void Enqueue(Action action)` | Queues `action` to run on the main thread. May be called from any thread. |
| `bool TryEnqueue(Action action)` | Like `Enqueue`, and reports whether the action was accepted. |
| `void Enqueue<TArg>(Action<TArg> action, TArg arg)` | Queues an action with an argument, without a closure. |
| `bool TryEnqueue<TArg>(Action<TArg> action, TArg arg)` | Like the above, and reports whether it was accepted. |
| `int Depth { get; }` | Actions waiting. |
| `const int MaxQueueDepth` | 10000. |
| `DispatcherFullPolicy FullPolicy { get; set; }` | What happens when the queue is full: `DropTail` (default; the new action is dropped), `DropHead` (the oldest waiting action is dropped) or `Throw` (`InvalidOperationException`). |
| `long OverflowCount { get; }` | Actions dropped or refused so far. |
| `long DroppedGenericActionCount { get; }` | Actions refused because the queue was full. |
| `event Action<long> OnGenericActionDropped` | An action was refused because the queue was full; the argument is `DroppedGenericActionCount`. Raised at the 1st, 2nd, 4th, 8th… refusal, on the thread that tried to queue the action, not on the main thread: a handler must be safe to run on any thread, must not call Unity APIs and must not queue work on the dispatcher. |

When a session ends or a connection attempt fails, the SDK discards every action still
waiting in the queue, including actions your code queued; they do not run. Do not rely
on an action queued just before a disconnect.

The members that carry received packets — `Enqueue(Action<byte[], int>, byte[], int)`,
`BufferReturnHandler`, `DroppedRentedPacketCount` and `OnRentedPacketDropped` — and
`ExecutingItemAgeMillis` and `DiscardPendingCallbacks` are used by the SDK and are not
intended to be called from game code. `OnRentedPacketDropped` is raised on the network
thread, under the same rules as `OnGenericActionDropped`.

Received state frames and variable updates may fill at most 8000 of the queue's places;
one more arriving then is dropped and counted in `DroppedRentedPacketCount`, and the other
2000 places stay for spawns, RPCs, room replies and the actions your code queues. A
variable update dropped this way leaves that variable stale until it is written again.

```csharp
using System.Threading.Tasks;
using RTMPE.Core;
using RTMPE.Threading;

public static class BackgroundWork
{
    public static void LeaveWhenDone(Task work)
    {
        work.ContinueWith(_ => MainThreadDispatcher.Instance.Enqueue(() =>
        {
            var manager = NetworkManager.Instance;
            if (manager != null && manager.IsInRoom) manager.Rooms.LeaveRoom();
        }));
    }
}
```

---

## See also

- [Getting Started](../getting-started.md)
- [Architecture guide](../architecture.md)
- [Performance tuning](../performance-tuning.md)
- [Troubleshooting](../troubleshooting.md)
- [Analyzer diagnostics](../diagnostics.md)
- [Automation](../automation.md)

*RTMPE SDK 1.0.8 — [Getting Started](../getting-started.md) — [Architecture](../architecture.md)*
