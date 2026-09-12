# RTMPE SDK — C# API Reference

> SDK Version: `com.rtmpe.sdk 1.0.5`
> Namespaces: `RTMPE.Core` · `RTMPE.Rooms` · `RTMPE.Rpc` · `RTMPE.Sync` · `RTMPE.Transport`

---

## Table of Contents

- [Connecting, without writing any](#connecting-without-writing-any)
- [NetworkManager](#networkmanager)
- [ApiKeySource](#apikeysource)
- [RoomManager](#roommanager)
- [LobbyManager](#lobbymanager)
- [MatchmakingManager](#matchmakingmanager)
- [Networked scenes](#networked-scenes)
- [Scene loading, without writing any](#scene-loading-without-writing-any)
- [Interest management](#interest-management)
- [SpawnManager](#spawnmanager)
- [INetworkObjectPool](#inetworkobjectpool)
- [OwnershipManager](#ownershipmanager)
- [NetworkBehaviour](#networkbehaviour)
- [Remote Procedure Calls](#remote-procedure-calls)
- [NetworkTransform](#networktransform)
- [NetworkTransformInterpolator](#networktransforminterpolator)
- [Remote motion timing](#remote-motion-timing)
- [NetworkRigidbody / NetworkRigidbody2D](#networkrigidbody--networkrigidbody2d)
- [NetworkVariable types](#networkvariable-types)
- [NetworkObjectRegistry](#networkobjectregistry)
- [NetworkSettings](#networksettings)
- [CreateRoomOptions](#createroomoptions)
- [JoinRoomOptions](#joinroomoptions)
- [RoomInfo](#roominfo)
- [PlayerInfo](#playerinfo)
- [NetworkState enum](#networkstate-enum)
- [DisconnectReason enum](#disconnectreason-enum)
- [IDamageable interface](#idamageable-interface)
- [NetworkTransport (abstract)](#networktransport-abstract)
- [UdpTransport](#udptransport)

---

## NetworkManager

**Namespace:** `RTMPE.Core`
**Inherits:** `MonoBehaviour`
**Pattern:** Singleton — access via `NetworkManager.Instance`

`NetworkManager` is the central coordinator of the SDK. It owns the connection
lifecycle, crypto session, heartbeat, and all sub-managers. It persists across
scenes via `DontDestroyOnLoad` and subscribes to `SceneManager.sceneUnloaded` /
`sceneLoaded` to prune the `NetworkObjectRegistry` after a scene transition.
Place it on a GameObject **only in the boot scene**.

### Static members

```csharp
// Returns the NetworkManager placed in the scene. Returns null — with a
// one-time warning — when the scene contains none, and after OnApplicationQuit.
static NetworkManager Instance { get; }

// Thread-safe null check — no side effects.
static bool HasInstance { get; }
```

### Transport factory (pluggable)

A static hook that lets apps replace the built-in `UdpTransport` — used by
integration tests (mock transport) and by projects that must reach the gateway
some other way.
Install before the first `Connect()` call. A `null` return logs a warning and
falls back to `UdpTransport`; a factory that throws logs the exception as an
error first, then the same warning, and falls back as well. It does not
make WebGL reachable: what that platform lacks is the background thread above
the transport, which the factory does not reach — see
[Troubleshooting](../troubleshooting.md#webgl-is-not-a-supported-platform).

```csharp
// Delegate signature. Receives the active NetworkSettings.
delegate NetworkTransport TransportFactoryFn(NetworkSettings settings);

// Install / clear. Install before Connect(); changing mid-session does not
// re-create the live transport — Disconnect() first.
static void SetTransportFactory(TransportFactoryFn factory)
static void ClearTransportFactory()

// True when a custom factory is installed.
static bool HasCustomTransportFactory { get; }
```

### Connection

```csharp
// Begin the handshake with the RTMPE gateway.
// Must be called from the Disconnected state.
// apiKey — your API key from the RTMPE developer dashboard
//          (https://portal.rtmpengine.com/dashboard).  Obtain it with
//          ApiKeySource.Resolve() rather than from a [SerializeField]: a key
//          typed into the Inspector is written into the scene asset, committed
//          with it, and present in every build made from it.
void Connect(string apiKey)

// Shortcut reconnect using a previously-issued reconnect token.
// Returns true if a reconnect attempt was scheduled; false if no token is
// held, the manager is not in the Disconnected state, the platform refuses
// networking (WebGL), or a reconnect loop is already running — that loop
// rests in Disconnected between attempts, so a call during its backoff is
// refused even though both documented conditions for true appear to hold.
// On a successful SessionAck, if LastRoomId is populated and
// NetworkSettings.autoRejoinLastRoomOnReconnect is true, the SDK auto-calls
// Rooms.JoinRoom(LastRoomId).
bool Reconnect()

// Gracefully close the connection.
// Sends a Disconnect packet, drains the socket, then closes the UDP socket.
// Clears the reconnect token and last-room snapshot.
void Disconnect()
```

⛔ The WebGL refusal named above is the platform one: that player has no
background thread for the I/O loop, and no transport can supply it — see
[Troubleshooting](../troubleshooting.md#webgl-is-not-a-supported-platform).

### State

```csharp
// Current connection state (see NetworkState enum).
NetworkState State { get; }

// True when State is Connected or InRoom.
bool IsConnected { get; }

// True when State is InRoom.
bool IsInRoom { get; }
```

### Identity & tokens

```csharp
// Gateway session ID (numeric) extracted from the JWT sub claim.
// Valid after SessionAck — fire OnConnected.
ulong LocalPlayerId { get; }

// Room-scoped player UUID (e.g. "a1b2c3d4-…"). Populated when RoomManager
// receives a successful Create/Join response. Used by NetworkBehaviour.IsOwner.
string LocalPlayerStringId { get; }

// EdDSA (Ed25519) JWT bearer token issued at SessionAck. Use with the Room
// Service REST API. Returned as an opaque RedactedString so a stray log line
// cannot leak it — call .Reveal() at the point of use to get the raw string,
// or .IsEmpty to test for presence without revealing.
RedactedString JwtToken { get; }

// Reconnect token issued at SessionAck. Non-empty whenever a token is held and
// not yet consumed. Same RedactedString wrapper as JwtToken — call .Reveal()
// for the raw value.
RedactedString ReconnectToken { get; }

// True when the SDK holds a valid reconnect token.
bool CanReconnect { get; }
```

### Last-room snapshot

```csharp
// RoomInfo.RoomId of the most recently active room. Preserved across a
// token-preserving ClearSessionData so Reconnect() can auto-rejoin.
// Null when no room has been joined, after an explicit Disconnect(), or
// after LeaveRoom().
string LastRoomId { get; }

// RoomInfo.RoomCode (human-readable) paired with LastRoomId.
// Same lifetime as LastRoomId.
string LastRoomCode { get; }
```

### Round-trip time

```csharp
// Round-trip time in milliseconds, measured per HeartbeatAck.
// -1.0f before the first HeartbeatAck arrives.
float LastRttMs { get; }
```

### Counters

Lifetime totals an application can poll and alert on. Each one has a companion
log line behind a one-per-second gate, so the console shows *that* something is
happening and these show *how much* — the distinction that separates a single
event from a sustained one, and either from a rule firing on traffic it should
have admitted. None of them is reset by a disconnect or a reconnect.

```csharp
// Inbound spawns refused because the object id lay in THIS client's own id
// space under another player's name.  Every id the SDK mints folds its own
// session digest into the id's high half, so an honest room leaves this at
// zero; a rising reading names a peer composing ids by some other route.
long RefusedForeignIdClaimCount { get; }

// Inbound despawns refused because they named an object this client both owns
// and minted.  Usually rises behind the reading above — the record a refused
// despawn spends is typically the one a refused claim created — but not always:
// a lost ownership-transfer notice moves it on its own.
long RefusedForeignDespawnCount { get; }

// Inbound packets dropped because the per-second token bucket was exhausted.
// A persistent non-zero rate is either a hostile gateway or a legitimate burst
// above the configured cap.
long DroppedInboundFloodPacketCount { get; }

// Enhanced-RPC payloads dropped at enqueue time because the buffer-replay
// deferral queue hit one of its three caps (per-payload size, cumulative
// bytes, slot count).
long DroppedRpcReplayBufferCount { get; }
```

### Sub-managers

```csharp
// Room CRUD operations and events.
RoomManager Rooms { get; }

// Spawn / Despawn networked GameObjects (+ optional INetworkObjectPool).
SpawnManager Spawner { get; }

// Lobby browser — list / filter rooms without joining one.
LobbyManager Lobby { get; }

// Matchmaking — AutoJoinOrCreate by mode / lobby.
MatchmakingManager Matchmaking { get; }

// Networked scene-loading façade — the master client drives room-wide scene
// changes; every client receives OnSceneLoadStarted. See Networked scenes.
NetworkSceneManager Scene { get; }   // non-null once the manager's own Awake has run — NOT a connection test; LoadScene still requires being the room's master and throws when not in a room

// Local-player property helper: NetworkManager.LocalPlayer.SetProperty(key, value).
LocalPlayerContext LocalPlayer { get; }
```

### Master client

```csharp
// True when the local player is the current master client (host) of
// Rooms.CurrentRoom. Derived from the cached room snapshot, so it updates
// automatically on master_client_changed / host_changed. False when not in a
// room, when no host is set, or before the local player ID is known.
bool IsMasterClient { get; }
```

### Custom data channel

```csharp
// Send an application-defined message to the room. Main thread only —
// negotiated session state is read unsynchronised. AEAD-encrypted once the
// session is established. A defensive copy is taken, so the caller may reuse
// its buffer immediately; that makes the BUFFER safe to reuse, not the call
// safe to make from a worker.
//
// reliable: true opts the packet into the ARQ retransmit channel — effective
// only when NetworkSettings.EmitArqSequence is enabled AND the peer negotiated
// the ArqAck capability; otherwise the packet is sent unreliably (a one-time
// advisory is logged). No-ops with a warning when not connected.
void Send(byte[] data, bool reliable = false);
```

> The receive side is the `OnDataReceived` event below — it is raised for `Data`
> (0x10) and `StateSync` (0x40) packets with the full decrypted frame.

### Events

All events are dispatched on the **Unity main thread** via `MainThreadDispatcher`.

```csharp
// Fired when the AEAD session is fully established (after SessionAck).
event Action OnConnected

// Fired on every state transition, including Connecting / Reconnecting /
// Disconnecting. Arguments are (previous, current).
event Action<NetworkState, NetworkState> OnStateChanged

// Fired when the connection closes for any reason.
event Action<DisconnectReason> OnDisconnected

// Fired when the handshake fails before OnConnected.
event Action<string> OnConnectionFailed   // string = human-readable reason

// Fired after each successful HeartbeatAck. RTT in milliseconds.
event Action<float> OnRttUpdated

// Fired when a Data (0x10) or StateSync (0x40) packet is received.
// The argument is the full decrypted packet (header + payload).
event Action<byte[]> OnDataReceived

// Fired when the server acknowledges a reliable Data packet.
// Reserved for retransmit-suppression hooks.
event Action OnDataAcknowledged

// Fired when, after a successful Reconnect(), the SDK begins an
// automatic rejoin of LastRoomId. Outcome observable via the usual
// Rooms.OnRoomJoined / Rooms.OnRoomError events.
// Not fired when autoRejoinLastRoomOnReconnect is false or LastRoomId is null.
event Action<string> OnAutoRejoinAttempt

// Fired when the bounded reconnect loop kicked off by Reconnect() exhausts
// NetworkSettings.maxReconnectAttempts without reaching Connected. Argument
// is the number of attempts actually made. When this fires the manager has
// transitioned back to Disconnected and cleared all session state — the
// application MUST fall back to Connect(apiKey) to recover.
event Action<int> OnReconnectFailed
```

### Obsolete events

```csharp
// [Obsolete] Use Rooms.OnRoomJoined / Rooms.OnRoomCreated instead.
event Action<ulong> OnJoinedRoom

// [Obsolete] Use Rooms.OnRoomLeft instead.
event Action<ulong> OnLeftRoom
```

### Inspector fields

| Field      | Type              | Default | Description                                   |
|------------|-------------------|---------|-----------------------------------------------|
| `Settings` | `NetworkSettings` | `null`  | Assign your `RTMPESettings` asset here        |

---

## ApiKeySource

**Namespace:** `RTMPE.Core`

Resolves the API key `Connect` needs from a source outside the built artifact.

```csharp
// The three ambient sources, declared in the order Resolve() consults
// them — after the provider, which is a delegate rather than a constant.
// Read by Resolve(): "--rtmpe-api-key-file". Names a FILE holding the key.
// Preferred over the option below on any machine with more than one account.
const string CommandLineFileOption

// Read by Resolve(): "--rtmpe-api-key". Both "--rtmpe-api-key VALUE" and
// "--rtmpe-api-key=VALUE" are accepted, as is the single-dash spelling every
// built-in Unity player argument uses.
const string CommandLineOption

// Read by Resolve(): "RTMPE_API_KEY"
const string EnvironmentVariableName

// The failure that made the last TryResolve() return false for a reason other
// than "no source supplied one", or null.
Exception LastError { get; }

// Register the source consulted before the command line and the environment.
// The Editor registers the setup wizard's credential vault; pass null to clear.
// A provider that throws surfaces as InvalidOperationException rather than
// falling through to a different credential. It RETURNS a key rather than
// awaiting one, and it is called on the connect path — so a credential that has
// to be fetched is fetched first and handed back from here.
void SetProvider(Func<string> provider)

// The key, or "" when no source supplied one. Surrounding whitespace is
// removed, so a key pasted from a shell or a CI secret resolves cleanly.
// Throws InvalidOperationException when a source you configured failed rather
// than had nothing to give: a provider that threw, or --rtmpe-api-key-file
// naming a file that cannot be read, holds no key, or was given no path.
// Falling through would connect with a credential you did not configure.
string Resolve()

// The key, when a source supplied one. Never throws — it is called from a
// connect path; a configured source that failed leaves LastError set.
bool TryResolve(out string apiKey)
```

**Resolution order:** registered provider → the Editor's credential vault →
`--rtmpe-api-key-file` → `--rtmpe-api-key` → environment. A provider is code you
wrote deliberately; the other sources are properties of whatever launched the
process, so the deliberate decision wins. The vault sits one tier below your own
provider precisely so that shipping one does not take the wizard's key away from
the Editor.

> **Prefer the file form.** `ps` and `/proc/<pid>/cmdline` are world-readable, so
> `--rtmpe-api-key <key>` exposes the credential to every other account on the
> machine — a shared workstation or CI runner. `--rtmpe-api-key-file <path>` puts
> a path there instead.

In the Editor, `Window → RTMPE → Setup Wizard` writes the key to the OS
credential vault and the SDK registers it as the secondary source, so entering
Play mode needs no key in the scene. (The `Project Settings → RTMPE` pane edits
the `NetworkSettings` asset; it holds no credential and never has.)

> **What this protects, and what it does not.** A key a client presents at
> handshake is a key the person running that client can obtain. These sources
> remove the copy in the scene asset, the copy in your repository's history and
> the copy in the shipped player — three exposures that do not require the
> attacker to be the player. To keep the project key off the client entirely,
> hand `SetProvider` a short-lived per-player credential your own backend minted.
> The delegate is synchronous, so the fetch happens before it and the provider
> returns what the fetch produced — the two shapes that takes are written out in
> [getting-started.md → Giving a player build its API key](../getting-started.md#giving-a-player-build-its-api-key).

There is no API-key field on `NetworkSettings`, and there must not be: a
serialized field is written into the asset and travels with it.

---

## RoomManager

**Namespace:** `RTMPE.Rooms`
**Access:** `NetworkManager.Instance.Rooms`

A fresh `RoomManager` is created on every `Connect()` / `Reconnect()`, but your
event subscriptions are carried across the rebuild — subscribe once, and do not
re-subscribe from `OnConnected` or each reconnect adds another copy.

Handles room creation, joining, leaving, listing, custom properties,
master-client transfer, and scene coordination. All operations are sent with
`FLAG_RELIABLE` (reliable delivery), AEAD-encrypted once the session is
established, and produce an event callback.

### Operations

```csharp
// Create a new room. Fires OnRoomCreated on success, OnRoomError on failure —
// except a full local request table and a call made while disconnected, which
// are reported to the Console only.
// The create queue is capped at 16 in-flight requests.
void CreateRoom(CreateRoomOptions options = null)

// Join an existing room by its UUID.
// Fires OnRoomJoined on success, OnRoomError on failure.
void JoinRoom(string roomId, JoinRoomOptions options = null)

// Join an existing room by its 6-character join code (e.g. "XKCD42").
// Fires OnRoomJoined on success, OnRoomError on failure.
void JoinRoomByCode(string roomCode, JoinRoomOptions options = null)

// Leave the current room. Fires OnRoomLeft.
void LeaveRoom()

// Request a list of rooms. Fires OnRoomListReceived when the answer is the
// project's rooms, or OnRoomError when the server declined the request.
// publicOnly — when true, returns only rooms marked as public.
void ListRooms(bool publicOnly = true)

// Write custom properties on the current room. The write is a DELTA: the
// server merges the keys you send into the room's existing map and leaves
// the rest alone, and every client merges the broadcast the same way.
// Pass PropertyValue.Deletion() as a value to REMOVE that key — the server
// deletes it and every client applies the removal. Note that
// default(PropertyValue) is not a deletion; it is a zero-valued Int.
// Fires OnRoomPropertiesChanged
// once the server accepts and broadcasts the update.
//   MASTER ONLY, and enforced by the SERVER: it authorises every
//   room-property key against the room's host seat and answers a refusal with
//   no packet at all. For an ORDINARY key the SDK sends anyway rather than
//   pre-refusing — it cannot know that the master id it holds is current, and a
//   refusal built on a stale one would refuse the actual host permanently; the
//   write goes and OnRoomError reports the silence (below). ⚠️ For a RESERVED
//   key (the `__` prefix, e.g. `__scene`) it does refuse locally on its cached
//   IsMasterClient, sends nothing, and raises OnRoomError — so a genuine host
//   whose cached master id is stale cannot write one until that id catches up.
void SetRoomProperties(IReadOnlyDictionary<string, PropertyValue> properties)

// Set a single room property by key. Convenience wrapper over SetRoomProperties.
void SetRoomProperty(string key, PropertyValue value)

// Set custom properties for a player in the room. Fires OnPlayerPropertiesChanged.
//   YOUR OWN SEAT ONLY — playerId must be LocalPlayerStringId. The server
//   accepts a player-property write only from the session that owns the seat;
//   a foreign id is refused here with OnRoomError rather than sent to be
//   dropped in silence.
void SetPlayerProperties(string playerId, IReadOnlyDictionary<string, PropertyValue> properties)

// Request that the master-client role be transferred to targetPlayerId.
// On acceptance every client in the room receives OnMasterClientChanged.
//   HOST ONLY, and the server declines with no packet at all — so a request
//   that draws no broadcast within twelve seconds raises OnRoomError. It is
//   not pre-refused here: the master id this client holds may be stale, and a
//   refusal built on one would refuse the actual host.
void TransferMasterClient(string targetPlayerId)

// Request the server remove targetPlayerId from the room.
// On acceptance every client receives OnPlayerKicked.
//   HOST ONLY, reported the same way as TransferMasterClient above.
void KickPlayer(string targetPlayerId)

// Report that the local client finished loading sceneName. When every player
// has reported the same scene, all clients receive OnAllPlayersSceneLoaded.
void ReportSceneLoaded(string sceneName)
```

### State

```csharp
// The current room, or null when not in a room.
RoomInfo CurrentRoom { get; }

// True when the local player is currently in a room.
bool IsInRoom { get; }
```

### Events

```csharp
// Room created successfully. RoomInfo contains id, code, name, playerCount.
event Action<RoomInfo> OnRoomCreated

// Room joined successfully.
event Action<RoomInfo> OnRoomJoined

// Local player left the room.
event Action OnRoomLeft

// Another player joined the current room.
// The SDK auto-calls SpawnManager.MarkAllVariablesDirtyForResync() on this event
// so the joiner receives a full NetworkVariable snapshot within one 30 Hz tick.
event Action<PlayerInfo> OnPlayerJoined

// Another player left the room. Receives the player UUID string.
event Action<string> OnPlayerLeft

// Room list received after a call to ListRooms().
event Action<RoomInfo[]> OnRoomListReceived   // only when the list IS the project's rooms;
                                              // a refusal goes to OnRoomError

// A room operation failed. The string contains a diagnostic description.
//   Includes the two property failures the server answers with NO packet: a
//   player-property write
//   naming a seat that is not yours (refused here, nothing sent), and a
//   property write that drew no broadcast within twelve seconds.
//   ⚠️ The second says "the version did not move here", which is all the client
//   can honestly claim. It covers a version conflict, an authority refusal, a
//   validation or property-cap failure, a room or player that no longer exists,
//   a dropped broadcast after an ACCEPTED write, and plain packet loss — the
//   server answers all of them the same way, with nothing. Read the current
//   value and decide from it.
//   ⚠️ Nor is it "your write was rejected": a broadcast at a LATER version ends
//   the wait, because whether your write landed and was superseded or lost the
//   race outright is not something the client can tell.
event Action<string> OnRoomError

// Fired after the server accepts a RoomPropertyUpdate and broadcasts the
// new state to all clients. Argument is the post-update RoomInfo snapshot;
// RoomInfo.Properties reflects the authoritative map. Subscribers that need
// a delta should diff against CurrentRoom captured BEFORE the event fires —
// RoomManager swaps CurrentRoom to the new snapshot BEFORE invoking this event.
event Action<RoomInfo> OnRoomPropertiesChanged

// Fired after the server accepts a PlayerPropertyUpdate and broadcasts the
// new state to all clients. Arguments are (playerId, updatedPlayerInfo).
event Action<string, PlayerInfo> OnPlayerPropertiesChanged

// Fired when the room's master client changes — either automatically (FIFO
// promotion after the previous master disconnected) or manually (a
// TransferMasterClient request was accepted). Arguments are
// (previousMasterId, newMasterId); either may be empty when unknown
// (e.g. initial assignment).
event Action<string, string> OnMasterClientChanged

// Fired when the host removes a player from the room via KickPlayer.
// Arguments are (kickerId, targetPlayerId). Every client in the room
// receives this event — the kicked client observes their own ID as the
// target and should treat it as an authoritative disconnect.
event Action<string, string> OnPlayerKicked

// Fired when every player in the room has reported scene-loaded readiness
// for the authoritative scene (stored in the reserved Scene property).
// Argument is the scene name that just finished loading for everyone.
event Action<string> OnAllPlayersSceneLoaded
```

---

## LobbyManager

**Namespace:** `RTMPE.Rooms`
**Access:** `NetworkManager.Instance.Lobby`

Browse and filter the rooms in a lobby without joining one. A fresh
`LobbyManager` is created on every `Connect()` / `Reconnect()`, but your event
subscriptions are carried across the rebuild — subscribe once, and do not
re-subscribe from `OnConnected` or each reconnect adds another copy.

### Properties

```csharp
// Name of the lobby currently joined; "" while not in one.
string CurrentLobbyName { get; }

// True while the local client is in the lobby browser.
bool IsInLobby { get; }

// Most recent room list received from the server.
IReadOnlyList<LobbyRoomInfo> Rooms { get; }
```

### Methods

```csharp
// Enter the lobby browser. The name is REQUIRED: there is no default lobby,
// and the Room Service refuses an empty one. Up to 32 characters from
// [A-Za-z0-9_-]; anything else throws ArgumentException before a packet is
// sent, because the refusal would otherwise arrive as an empty room list —
// which is also what a lobby with no rooms looks like.
// The server replies with the current room list (OnRoomListUpdated).
void JoinLobby(string lobbyName)

// Leave the lobby browser (fire-and-forget).
void LeaveLobby()

// Request a filtered / sorted one-shot room list (see LobbyQueryOptions).
// opts is required and its LobbyName is held to the same rule as JoinLobby.
void ListRooms(LobbyQueryOptions opts)
```

### Events

```csharp
// Fired when the server pushes an updated room list — after JoinLobby,
// ListRooms, or a server-side lobby change.
event Action<IReadOnlyList<LobbyRoomInfo>> OnRoomListUpdated

// Fired when a JoinLobby is abandoned because the server never answered it
// (15 s).  The counterpart of RoomManager.OnRoomError.
event Action<string> OnLobbyError

// What went wrong with the most recent room-list payload, or None once one has
// been read in full.  Poll it to raise and clear a "room list unavailable"
// banner; use RefusedRoomLists for the running count.
LobbyListProblem LastProblem     { get; }
int              RefusedRoomLists { get; }
```

> ⛔ `OnLobbyError` says the join was not confirmed. It does **not** say the
> gateway holds no subscription for this session — it registers one on any join
> the Room Service did not refuse, whatever becomes of the reply. Answer it with
> `LeaveLobby()` or another `JoinLobby()`, never with an assumption that nothing
> was left behind.

### LobbyRoomInfo (immutable)

```csharp
string RoomId      { get; }
string RoomCode    { get; }
string Name        { get; }
int    PlayerCount { get; }
int    MaxPlayers  { get; }
bool   IsPublic    { get; }
string LobbyName   { get; }
```

### LobbyQueryOptions

```csharp
string            LobbyName  { get; set; } = "";   // required — see JoinLobby
int               MaxResults { get; set; } = 0;    // 1–100; 0 = server default (100)
LobbySort         SortBy     { get; set; } = LobbySort.PlayerCount;
List<LobbyFilter> Filters    { get; set; }         // null = no filter
```

- `LobbySort` — `PlayerCount` (0), `Age` (1), `Name` (2).
- `LobbyFilter` — `{ string Key; LobbyFilterOp Op; object Value; }`.
- `LobbyFilterOp` — `Eq` (0), `NotEq` (1), `Lt` (2), `Gt` (3), `LtEq` (4), `GtEq` (5).

---

## MatchmakingManager

**Namespace:** `RTMPE.Rooms`
**Access:** `NetworkManager.Instance.Matchmaking`

AutoJoinOrCreate matchmaking: the server atomically finds an open room
matching the requested mode / lobby or creates one, then joins the player.
A fresh `MatchmakingManager` is created on every `Connect()` / `Reconnect()`,
but your event subscriptions are carried across the rebuild — subscribe once,
and do not re-subscribe from `OnConnected` or each reconnect adds another copy.
An *in-flight* request does not survive: a reconnect during matchmaking reports
neither a match nor a failure, so treat a disconnect as the outcome.

### Property

```csharp
// True while a matchmaking request is in flight.
bool IsMatchmaking { get; }
```

### Methods

```csharp
// Start matchmaking (default 30 s timeout).
// Throws InvalidOperationException if not Connected / InRoom or a request
// is already in flight; ArgumentNullException for null options; ArgumentException
// for a Mode that is empty, over-long or carries a control character, and for an
// invalid LobbyName or DisplayName.
void StartMatchmaking(MatchmakingOptions options)

// Same, with an explicit timeout. Above 300 s it is clamped to 300; zero or
// negative is NOT clamped to a floor — it is discarded and the 30 s default
// applies, so passing 0 does not mean "no timeout"; NaN throws
// ArgumentException; double.PositiveInfinity disables the timeout.
void StartMatchmaking(MatchmakingOptions options, double timeoutSeconds)

// Abort an in-flight request. Idempotent.
void CancelFindMatch()
```

> `Tick(double)` drives the timeout clock but is called automatically by
> `NetworkManager.Update()` — applications do not call it.

### Events

```csharp
event Action<MatchmakingResult> OnMatchmakingComplete   // matched / created room
event Action<string>            OnMatchmakingFailed     // server-side failure
event Action                    OnMatchmakingCancelled  // CancelFindMatch()
event Action                    OnMatchmakingTimedOut   // timeout elapsed
```

### MatchmakingOptions

```csharp
string Mode        { get; set; } = "";   // required, non-empty
string LobbyName   { get; set; } = "";
int    MinPlayers  { get; set; } = 0;    // <= 0 → server default (2)
int    MaxPlayers  { get; set; } = 0;    // <= 0 → server default
string DisplayName { get; set; } = "";
```

### MatchmakingResult (immutable)

```csharp
string RoomId   { get; }
string RoomCode { get; }
bool   Created  { get; }   // true = a new room was created for this match
```

---

## Networked scenes

**Namespace:** `RTMPE.Rooms` (`NetworkSceneManager`)
**Access via:** `NetworkManager.Instance.Scene` — non-null once the manager's own
`Awake` has run, so holding one is **not** a connection test. ⚠️ It is null before
that: a component whose `Awake` runs earlier in script execution order, a second
`NetworkManager` that destroys itself, and any access after teardown all see
`null`. Reach for it from `Start` or later, or from an SDK event.

Coordinates a room-wide scene change. The **master client** calls `LoadScene`;
every client (including the master) receives `OnSceneLoadStarted` and is
responsible for actually loading the scene with Unity's own
`SceneManager.LoadSceneAsync`. Each client calls `ReportReady` when its local
load finishes; once **all** clients report, every client receives
`OnAllPlayersSceneLoaded`. A late joiner whose room already has a scene set
receives `OnSceneLoadStarted` immediately on join, so it catches up for free.

```csharp
// Master-client only. Instruct the room to switch scene. Throws
// InvalidOperationException if the caller is not in a room or not the master
// client, and ArgumentException if sceneName is null/empty.
void LoadScene(string sceneName, NetworkSceneLoadMode mode = NetworkSceneLoadMode.Single);

// Report that THIS client finished loading the current scene. No-op when not in
// a room or no scene is set.
void ReportReady();

// The room's authoritative scene name (empty until one is set).
string CurrentScene { get; }

// How that scene is to be loaded, from the reserved __scene_additive property.
// Single when the room has not set it.
NetworkSceneLoadMode CurrentSceneLoadMode { get; }

// How long a load may go unsettled before OnSceneLoadTimedOut is raised.
// 0 or negative disables the report. Defaults to
// DefaultSceneReadyTimeoutSeconds; the project-wide value comes from
// NetworkSettings.sceneReadyTimeoutSeconds.
float SceneReadyTimeoutSeconds { get; set; }
const float DefaultSceneReadyTimeoutSeconds = 60f;

event Action<string> OnSceneLoadStarted;       // (sceneName) — begin loading
event Action<string, NetworkSceneLoadMode>     // (sceneName, mode) — the same
     OnSceneLoadStartedWithMode;               //   occasion, carrying the mode
event Action<string> OnAllPlayersSceneLoaded;  // (sceneName) — everyone is ready
event Action<string> OnSceneLoadTimedOut;      // (sceneName) — the room did not settle

enum NetworkSceneLoadMode { Single = 0, Additive = 1 }
```

The scene name is carried on the reserved `__scene` room custom property, so it
also appears as `RoomInfo.CurrentScene`. The manager never touches Unity's
`SceneManagement` itself — you drive the actual load in response to the event.

### Which of the two load events to handle

`OnSceneLoadStartedWithMode` fires on the same occasions as
`OnSceneLoadStarted` and with the same scene name, with one gap: it is withheld
when a subscriber to `OnSceneLoadStarted` changes room while that event is being
raised. The SDK's own loader depends on that gap existing; the second argument is the
mode `LoadScene` wrote alongside it. **Prefer it** — applying an additive load
as a single one unloads the scene the room was still in. `OnSceneLoadStarted`
remains and keeps working: it is a shipped delegate, and widening it would break
every assembly compiled against it. Handle one or the other, not both.

The mode is the room's, taken from the same snapshot on every path, so the
client that requested the change and the one that joined five minutes later
agree on it. `LoadScene` writes the pair together. If you write `__scene`
yourself through `SetRoomProperties`, write `__scene_additive` with it — a name
written alone leaves the room on the mode the previous load set.

### When the room never settles

`OnAllPlayersSceneLoaded` arrives only once the server has heard from every
seat, so one client that never calls `ReportReady` — because it crashed, or
because the application forgot to — leaves the whole room waiting. That wait
used to be silent and unbounded; `OnSceneLoadTimedOut` ends it with a report.

Two limits are worth knowing before you handle it:

- **It does not name the player.** Readiness is aggregated by the server, which
  broadcasts only the completed rendezvous, so no client is told who is
  outstanding — including whether it is itself. The console line says whether
  *this* client has sent its own report, which is the one thing this side can
  establish.
- **It is not a substitute for `OnAllPlayersSceneLoaded`.** Each client's budget
  starts when its own load began, so starting the match here would split the
  room between players who started and players who did not. Use it to show a
  notice, offer to leave, or ask the host to act — the decision is yours, and a
  load that settles late still raises `OnAllPlayersSceneLoaded` when it does.
  Re-issuing the **same** scene name *is* a retry: every write the room accepts
  is announced, including one naming the scene the room is already on, and each
  starts a fresh rendezvous and a fresh deadline. Every client will load it
  again, so use it to restart a round, not to nudge one straggler.

The deadline is started by a scene *change*, so a client that joins a room which
already has a scene loads it without one: the room's rendezvous for that scene
may have completed before the client arrived, and no broadcast is coming for it
whatever happens. A room genuinely stuck mid-load is reported by the players who
were present when the load began.

The budget is read when the load begins, so changing
`SceneReadyTimeoutSeconds` mid-load applies to the next one, not the one already
running. Set it from `NetworkSettings.sceneReadyTimeoutSeconds` for the whole
project: `0` there means "use the SDK default", and `-1` switches the report
off.

---

## Connecting, without writing any

`RtmpeConnectionBootstrap` performs the entry flow so a project does not have to
write it: resolve a key, connect, enter a room, spawn the local player, ask the
SDK to restore the session after an unexpected drop, and re-enter that same room
when the connection comes back.

Attach it to the object that carries your `NetworkManager` — a **root** object in
a **boot scene** the game never reloads. It makes itself persistent, so a copy in
a scene the game returns to becomes a second one every time; the component says
so when that happens rather than connecting twice in silence.

| Inspector field | What it decides |
| --- | --- |
| **Connect On Start** | Connect when the scene starts, or wait for your `Connect()`. |
| **Rejoin Last Room** | After a drop, re-enter the room it was in rather than opening a new one. |
| **Reconnect On Drop** | After an unexpected drop, spend the reconnect token the SDK holds. Once per session; the retry ladder is the SDK's own. |
| **Entry Policy** | `CreateRoom`, `JoinRoom` or `Matchmaking`. |
| **Room Name** / **Room Id** / **Max Players** / **Matchmaking Mode** | The arguments those three policies take. |
| **Player Prefab** | Spawned on entering a room. Leave it empty to enter and spawn nothing. |

```csharp
// Everything an application needs to steer it, without subclassing anything.
Func<GameObject> ChoosePlayerPrefab      // choose a character, a team, a skin
Func<Pose>       ChooseSpawnPose         // choose a spawn point
event Action<NetworkBehaviour> OnLocalPlayerSpawned
event Action<string>           OnBootstrapFailed   // every refusal, already written for a human
NetworkBehaviour LocalPlayer             // what it spawned, or null
string           CurrentRoomId           // the room it is in, or null
void Connect()                           // when Connect On Start is off
void Restart()                           // forget the room and enter one again
```

⛔ **There is no API-key field on it and there must never be one.** Unity
serialises a string field into the scene asset, which is committed and present in
every build made from it. The key comes from
[`ApiKeySource`](#apikeysource).

The **Two Player Room** sample is this component wired end to end — `Matchmaking`
entry policy, a player prefab, and a credential component beside it — set up so
that two clients reach the same room without a room id being typed anywhere.

🔑 **It stands down when the SDK is already rejoining.** After a token-based
reconnect the SDK re-enters the last room itself — that is
`NetworkSettings.autoRejoinLastRoomOnReconnect`, on by default — and it does so
*after* the transition into `Connected`. The bootstrap asks the same three facts
the SDK asks and issues nothing, because a second room operation in one session
would take a new empty room instead of the one the player was in.

⛔ **A departure is never undone.** Leaving a room, being moved between rooms and
being kicked by the host all arrive as one event, so the component stands down and
waits for `Restart()`. Re-entering by itself would charge a room against the
project's quota on every departure under `CreateRoom`, and under `JoinRoom` would
re-enter the room the host had just removed the player from.

⚠️ **`OnBootstrapFailed` carries every refusal the component makes or observes**,
which is not quite every refusal there is: a few of the SDK's own are log-only and
reach no event at all — a `JoinRoom` with an empty id, a `CreateRoom` while its
in-flight table is full, a `Connect` on a disabled manager. The component
pre-empts the ones its own configuration can cause, at every door into a room
operation rather than only at `Connect()`; the rest are visible in the console.

⚠️ **It is optional by attachment.** A title with a lobby screen, a character
selector or a queue of its own should keep driving the flow itself: every event
the component subscribes to stays public and documented below.

## Scene loading, without writing any

**Namespace:** `RTMPE.Rooms` (`RtmpeSceneLoader`)
**Inherits:** `MonoBehaviour`

Attach it and the room's scene instruction loads itself. The component
subscribes to `OnSceneLoadStartedWithMode`, calls `SceneManager.LoadSceneAsync`
in the mode the room asked for, and calls `ReportReady()` when the load
finishes. It is what every project writes by hand, once.

```csharp
// The scene this loader is currently loading, or null. The instruction's scene,
// not the engine's progress — a load superseded by a newer instruction stops
// being current the moment the new one arrives.
string LoadingScene { get; }
```

There is nothing else on it, and nothing to call.

⚠️ **Put it on a root GameObject in your boot scene** — the scene the room never
loads — or on the object that carries the `NetworkManager`, which lives there
for the same reason. Both halves matter:

- *Root*, because a `Single` load destroys every object in every loaded scene:
  a loader living in one destroys itself with the load it started, never reports
  ready, and leaves the rest of the room waiting until the readiness deadline
  expires. On a root object it makes itself persistent in `Awake`; on the
  `NetworkManager`'s object it is already persistent. If it is neither, it logs
  an error naming the consequence before the load begins.
- *Boot scene*, because it is persistent: a copy sitting in a scene the room
  loads becomes a **second** persistent loader every time the room returns
  there, and another on the next return. Only the most recently enabled loader
  acts — so the scene is not loaded once per copy — and the newcomer says so,
  because the fix is a placement. It is the same rule `NetworkManager` states
  for itself.

An **additive** instruction naming a scene that is already open is refused
rather than obeyed: Unity does not deduplicate additive loads, so obeying it
would open a second copy of the scene and double every object in it, and the SDK
has no unload path to undo that. The client reports ready without reloading, and
says why. To genuinely restart an additive scene, unload it first — which scene
to close is your decision, not the loader's.

The scene must be in Build Settings, like any scene Unity loads by name. If the
engine refuses the load, the loader says so and names the likely cause —
otherwise a missing scene is indistinguishable, from the room's side, from a
client that is merely slow. **Window → RTMPE → Network Scenes** asks the same
question at authoring time, against the scene names your scripts pass to
`LoadScene`; it reads names as written, and counts the calls whose name it could
not read rather than passing over them.

Two instructions naming one scene are two different loads. Re-issuing the scene
the room is already on is how a restart is expressed, so the loader tracks
*which* instruction a finished load belongs to rather than comparing names:
Unity cannot cancel a load already under way, and a superseded one still
finishes and still calls back. Only the current instruction reports readiness.

⛔ **Optional by attachment, not default by behaviour.** A title that wants a
loading screen, a staged load, or gameplay held back until an animation ends
should not attach it: the events stay public and stay documented, and doing the
particular thing is still one subscription away. See the **Scene Transitions**
sample.

---

## Interest management

**Namespace:** `RTMPE.Rooms` (`InterestManager`)
**Inherits:** `MonoBehaviour`

Opt-in spatial culling. Attach `InterestManager` to a persistent object and
assign `TrackedTransform` to the local player; while in a room it reports the
player's position to the gateway (10 Hz by default), and the gateway restricts
room-wide broadcasts to clients whose spatial-grid neighbourhood overlaps the
source. Clients without an `InterestManager` receive every broadcast unchanged.

```csharp
// The Transform whose world position is reported. When null at a send tick
// NOTHING is sent — the tick is skipped and no position is recorded, so a
// transform never assigned leaves the gateway with no position at all.
// (public field / Inspector)
Transform TrackedTransform;

float UpdateInterval = 0.1f;   // seconds between position sends (10 Hz)
bool  UseXzPlane     = true;   // project on XZ (3-D). Set false for 2-D / top-down (XY)

// Optional secondary receive-side filter. When > 0, inbound state for objects
// farther than this radius is discarded locally (in addition to gateway culling).
// 0 disables it. HysteresisMargin widens the leave-radius to stop boundary flap.
float ReceiveFilterRadius = 0f;
float HysteresisMargin    = 1f;

void StartTracking();          // resume reporting
void StopTracking();           // pause; gateway keeps the last interest zone
bool IsTracking { get; }
```

> Interest management is optional. Leave it off for small rooms; enable it for
> large open worlds where most objects are irrelevant to any given player.

---

## SpawnManager

**Namespace:** `RTMPE.Core`
**Access:** `NetworkManager.Instance.Spawner`

Manages the lifecycle of networked GameObjects. All methods must be called
from the **Unity main thread**. A fresh `SpawnManager` is created on every
`Connect()` / `Reconnect()`; prefab registrations are carried across the
rebuild, but pool installs are not — call `SetObjectPool()` inside
`OnConnected()`.

### Prefab registration

Assign a generated registry and there is nothing to call. `Window → RTMPE →
Network Prefabs` writes `Assets/RTMPE/Generated/RtmpePrefabRegistry.asset`
alongside the `RtmpePrefabIds` constants, from one button and one ledger; drop
it into **`NetworkSettings.prefabRegistry`** and every prefab in the ledger that
still resolves to an asset is registered when a session is built — before the
first `Connect()`, and again on every reconnect. A registration whose prefab has
been deleted, and both halves of a shared id, are left out: there is nothing for
a row to point at.

```csharp
// NetworkSettings — the asset the Editor generates, or null to register by hand.
public NetworkPrefabRegistry prefabRegistry;
```

The methods remain, and are what you reach for to register something the ledger
does not know about — a prefab loaded from an asset bundle, or one built at
runtime:

```csharp
// Load a registry the application obtained itself — from an AssetBundle, from
// Addressables, from downloadable content. Same tolerance as the configured
// one: a row naming no prefab is reported and skipped. A null argument is a
// no-op.
void LoadPrefabRegistry(NetworkPrefabRegistry registry)
```

```csharp
// Map a numeric prefab ID to a Unity prefab.
// Registrations persist across reconnects; call once before the first Spawn().
void RegisterPrefab(uint prefabId, GameObject prefab)

// Remove a prefab mapping. Returns true if the ID was registered.
bool UnregisterPrefab(uint prefabId)

// Returns true if a prefab is registered for this ID.
bool HasPrefab(uint prefabId)

// The reverse: the ID this session would spawn `prefab` under, or false when
// none is registered for it. Ask this rather than keeping an ID of your own —
// see below.
bool TryGetPrefabId(GameObject prefab, out uint prefabId)
```

⛔ **Do not resolve an id by scanning `NetworkPrefabRegistry.Entries`.** It is
tempting — the asset is public and holds exactly the pairs you want — and it is
the wrong authority. The registry is one of the table's two writers and the
weaker one: a hand `RegisterPrefab` is applied **after** it and replaces the row,
and a registry that lists one id twice resolves to whichever row came last. So a
scan can hand `Spawn` an id that belongs to a different prefab, and nothing will
refuse it — the spawn path asks whether the id is registered, never which prefab
it names, so every client instantiates the wrong object and no message is
logged. `TryGetPrefabId` asks the table `Spawn` itself consults. Where several
ids name one prefab it answers the lowest, so two clients asking the same
question of the same table get the same answer.

Three things are worth knowing before you assign one:

- **Every prefab it names is resident, not merely shipped.** The registry holds
  hard references and is reached from `NetworkManager` through `NetworkSettings`,
  so every listed prefab **and its whole dependency tree** — meshes, materials,
  textures, clips — is loaded into memory with the scene or prefab that carries
  the `NetworkManager`, spawned or not. Unity's own `NetworkPrefabsList` behaves the same way, which is why it
  grew Addressables overrides. On a memory-constrained platform, leave a large
  and rarely-spawned prefab out of the ledger and register it by hand when you
  load it.
- **A hand registration wins — and so does everything else the session held.**
  The registry is loaded first and `RegisterPrefab` runs after it, so calling it
  for an id the asset also carries replaces that row: you can override one
  prefab without abandoning the asset for the rest. ⚠️ On a **reconnect** both
  kinds of removal are undone, by different halves of the rebuild: the registry
  is loaded again and the previous table is then copied over it. An
  `UnregisterPrefab` is undone by the RE-LOAD, because the asset still carries
  the row; a row you removed from the asset and regenerated is put back by the
  CARRY, because the session before the reconnect still held it. Either way it
  stays registered until the process restarts. Both are session-scoped effects;
  a shipped build connects fresh and sees only the asset.
- **A row that names no prefab is skipped, not fatal.** Deleting a prefab
  without regenerating leaves a row pointing at nothing; the SDK logs one line
  naming what it could not take at face value — rows that resolved to no prefab,
  rows that reused an id — and registers everything else. One id you
  cannot spawn is not a reason to leave the project unable to spawn anything.

### Spawn / Despawn

```csharp
// Instantiate the prefab registered as prefabId (via pool if installed),
// register it on the network, send a SpawnRequest, and broadcast to all peers.
// Returns the NetworkBehaviour of the new GameObject, or null if prefabId is
// not registered, the prefab has no NetworkBehaviour component, the owner claim
// names another player (see below), the spawn rate cap is exceeded
// (NetworkSettings.maxSpawnsPerSecond, default 100/s), the per-room count cap is
// reached (maxSpawnsPerRoom, default 5000), a matching despawn arrived first, or
// the owner has already left. ⚠️ The two caps log at most one line per second,
// so a burst — a wave, a level's props — returns nulls near-silently.
// Call only after OnRoomCreated / OnRoomJoined fires.
// ownerPlayerId: optional override; defaults to NetworkManager.LocalPlayerStringId.
//   A client may only spawn under its OWN identity — the server refuses a spawn
//   claiming another player and relays nothing, so naming one here is refused
//   here too rather than producing an object no other client would ever see.
//   To seat an object with somebody else, spawn it locally and hand it over
//   with OwnershipManager.RequestOwnershipTransfer.
NetworkBehaviour Spawn(
    uint prefabId,
    Vector3 position,
    Quaternion rotation,
    string ownerPlayerId = null,
    // sharedAuthority: false (the default) means only the owner may address this
    // object with an Enhanced RPC. Set it for an object the room shares — a door,
    // a scoreboard, a world button.
    bool sharedAuthority = false)

// Broadcast a despawn to all peers, unregister, and either:
//   - call INetworkObjectPool.Release(prefabId, gameObject) when a pool is installed
//   - call UnityEngine.Object.Destroy(gameObject) otherwise.
// Refused, with a console error and no local teardown, for an object owned by
// another player: the server relays a despawn only from the object's owner, so
// destroying it here would remove it from this client alone.
void Despawn(ulong networkObjectId)

// The same question without the side effect, and the same operation reporting
// its outcome instead of logging it.  True for an object this client owns, one
// that carries no owner, and an id it has never seen.
bool CanDespawn(ulong networkObjectId)
bool TryDespawn(ulong networkObjectId)
```

### Object pool

```csharp
// Install a pluggable pool. From the next spawn onwards all Instantiate /
// Destroy calls route through the pool. Pass null to revert.
void SetObjectPool(INetworkObjectPool pool)

// Remove any installed pool.
void ClearObjectPool()

// The currently-installed pool, or null when none is set.
INetworkObjectPool ObjectPool { get; }
```

### Late-join resync

```csharp
// Mark every NetworkVariable on every locally-owned, spawned object as dirty
// so the next 30 Hz flush retransmits its current value. Auto-wired to
// RoomManager.OnPlayerJoined — apps rarely call this directly.
public void MarkAllVariablesDirtyForResync()
```

### Teardown

```csharp
// Destroy (or release to pool) all spawned objects. Called on room leave
// and on disconnect. Fires NetworkBehaviour.OnNetworkDespawn for each.
public void ClearAll(bool resetObjectIdSpace = true)
```

### Object ID generation

```csharp
// Internal (ObjectIdMath.Compose):
//   high 32 bits = MixSessionId(gatewaySessionId) — xor-fold + SplitMix64 avalanche
//   low  32 bits = per-session spawn counter
//   objectId     = (high << 32) | (counter & 0xFFFFFFFF)
// The avalanche mixing spreads ids across the 64-bit space so two players'
// object ids do not collide without a server round-trip.
```

---

## INetworkObjectPool

**Namespace:** `RTMPE.Core`

Contract for plugging a custom object pool into `SpawnManager`. Install via
`SpawnManager.SetObjectPool(pool)`. When no pool is installed, `SpawnManager`
falls back to `UnityEngine.Object.Instantiate` / `UnityEngine.Object.Destroy`
(zero overhead).

```csharp
public interface INetworkObjectPool
{
    // Acquire a live, active GameObject for the requested prefab.
    // MUST NOT return null on success — SpawnManager treats null as a
    // contract violation (logs an error and falls back to Instantiate).
    GameObject Acquire(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation);

    // Release the instance back to the pool on despawn. The pool should
    // typically deactivate the GameObject and keep it for reuse.
    // prefabId may be uint.MaxValue if SpawnManager could not recover the
    // original prefab id — implementations should then Destroy the instance.
    void Release(uint prefabId, GameObject instance);
}
```

### Implementation contract

- All calls happen on the Unity main thread. Implementations need not be thread-safe.
- `Acquire` should reactivate the GameObject (`SetActive(true)`) if needed —
  `SpawnManager` also does this defensively after a successful acquire.
- Exceptions thrown from `Release` are caught by `SpawnManager` and logged. On a
  single `Despawn` the instance is then destroyed as a fallback; on the teardown
  path (`ClearAll`, i.e. room leave and disconnect) it is only logged, so a pool
  that refuses an instance there leaks it.

---

## OwnershipManager

**Namespace:** `RTMPE.Core`
**Access:** `NetworkManager.Instance.Spawner.Ownership` (the `SpawnManager.Ownership` property). `RequestOwnershipTransfer()` is a method on this `OwnershipManager`, not on `NetworkBehaviour`.

Manages object ownership. Ownership is **server-authoritative** — the local client
cannot self-assign ownership; it sends a request and waits for a server grant.

```csharp
// Request the server to transfer ownership of objectId to newOwnerPlayerId.
// The server validates the request and broadcasts an OwnershipTransfer RPC
// to all clients.
void RequestOwnershipTransfer(ulong objectId, string newOwnerPlayerId)

// Apply a server-decided ownership grant. The three-argument form is the
// primary entry point: serverAttested must be true for a grant that did not
// originate from a local RequestOwnershipTransfer call (an unattested grant
// with no matching outstanding request is rejected). The two-argument
// overload forwards with serverAttested: false and is kept for back-compat.
void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId, bool serverAttested)
void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId)

// Snapshot of live objects owned by a given player UUID.
IReadOnlyList<NetworkBehaviour> GetObjectsOwnedBy(string playerId)
```

---

## NetworkBehaviour

**Namespace:** `RTMPE.Core`
**Inherits:** `MonoBehaviour`

Base class for every script on a networked GameObject. Extend this instead of
`MonoBehaviour` for any script that must sync state across players.

### Properties

```csharp
// Server-assigned unique ID for this network object.
ulong NetworkObjectId { get; }

// UUID string of the owning player.
string OwnerPlayerId { get; }

// True only on the client that owns this object.
// Guard all Input.* reads and NetworkVariable.Value writes with:  if (!IsOwner) return;
bool IsOwner { get; }

// True from the moment the object is registered on the network, which is
// BEFORE OnNetworkSpawn runs — so it already reads true inside that override.
bool IsSpawned { get; }

// When true (default), this object is automatically despawned on all clients
// when the owning player disconnects.
// Set to false to keep the object alive after the owner leaves.
bool DestroyWithOwner { get; set; }   // settable property — do NOT use 'override'
```

### Override points

```csharp
// Called after the object is registered on the network.
// Initialize all NetworkVariable instances here — NOT in Awake() or Start().
protected virtual void OnNetworkSpawn() { }

// Called before the object is removed from the network.
// Unsubscribe all NetworkVariable.OnValueChanged events here.
protected virtual void OnNetworkDespawn() { }

// Called when ownership changes. Fires only on actual owner change.
protected virtual void OnOwnershipChanged(string previousOwner, string newOwner) { }

// Called once per simulated tick on every owned, spawned NetworkBehaviour by
// the central 30 Hz tick driver — one call per integrated tick (a long frame
// fires it several times; a short frame, zero). ⚠️ Bounded at 8 ticks per
// frame: a frame owing more than that (a GC stall, an Editor breakpoint, a
// mobile resume — beyond ~267 ms at 30 Hz) drops the surplus rather than
// catching up. The accumulator is charged with Time.deltaTime, so this stops
// entirely at Time.timeScale = 0.
// Use for fixed-cadence work: input sampling, deterministic timers.
protected virtual void OnFixedTick(float deltaTime) { }

// Client-side prediction (optional; owner only). GatherInput supplies this
// frame's input; ApplyInput deterministically re-applies one input during a
// reconciliation replay. Both are no-ops until overridden. ApplyInput MUST be
// deterministic and MUST use the passed deltaTime — never Time.deltaTime.
protected virtual InputPayload GatherInput() => default;
protected virtual void ApplyInput(InputPayload input, float deltaTime) { }
```

> Use `protected override void OnNetworkSpawn()` — not `protected new` or `public override`.

---

## Remote Procedure Calls

**Namespace:** `RTMPE.Rpc` (attribute, `RpcTarget`) · `RTMPE.Core` (the `RPC` call)

An RPC is a method one client invokes so it runs on other clients (or the
server). Declare the method with `[RtmpeRpc]` and invoke it by name with `RPC`.

```csharp
using UnityEngine;
using RTMPE.Core;
using RTMPE.Rpc;

public class Weapon : NetworkBehaviour
{
    // Sender: only the owner should trigger the shot.
    public void Fire()
    {
        if (!IsOwner) return;
        RPC(nameof(FireRpc), transform.position);   // invoke by name
    }

    // Receiver: runs on every client (All includes the sender).
    [RtmpeRpc(RpcTarget.All)]
    public void FireRpc(Vector3 origin) => SpawnMuzzleFlash(origin);
}
```

### `RpcTarget`

| Value | Audience |
|---|---|
| `RpcTarget.All` | every client in the room, **including** the sender |
| `RpcTarget.Others` | every client **except** the sender |
| `RpcTarget.Server` | the server only (ServerRpc pattern); may re-broadcast |
| `RpcTarget.AllBuffered` | like `All`, and replayed to clients that join later |

### The `RPC` call

```csharp
// On any NetworkBehaviour. methodName must name a [RtmpeRpc] method on the
// same object; use nameof(...) so a rename is a compile error, not a runtime miss.
void RPC(string methodName, params object[] args);
```

Supported argument types: `int`, `float`, `bool`, `string`, `byte[]`, `ulong`,
`Vector3`, `Color`, `Quaternion`, or any type implementing `INetworkSerializable`.

The gateway stamps the authenticated sender on every RPC; read it inside the
handler via `NetworkManager.CurrentRpcSenderId` to authorize the call (e.g.
compare against `NetworkManager.Instance.LocalPlayerId`).

### Server RPC with a reply — `SendEnhancedRpcAsync`

For a `RpcTarget.Server` method that returns a result, await the request instead
of using fire-and-forget `RPC`:

```csharp
Task<RpcResponse> SendEnhancedRpcAsync(
    NetworkBehaviour  sender,
    string            methodName,
    object[]          args,
    TimeSpan?         timeout            = null,   // defaults to DefaultServerRpcTimeout
    CancellationToken cancellationToken  = default);

// RpcResponse (readonly struct)
uint         RequestId;   // correlates the reply with the request
uint         MethodId;    // FNV-1a("Type.FullName.MethodName")
ulong        SenderId;    // authenticated originator
bool         Success;     // false → inspect ErrorCode
RpcErrorCode ErrorCode;
byte[]       Payload;     // server-returned bytes (empty when none)
```

The awaited call throws `InvalidOperationException` **synchronously** on a
pre-condition failure — not connected/in a room, unknown method name, `null`
sender, or a method whose `[RtmpeRpc]` target is not `RpcTarget.Server` (use the
fire-and-forget `RPC` for `All`/`Others`/`AllBuffered`) — and also when the
arguments cannot be encoded at all, including a parameter type outside the
serializer's set, because that failure is caught and re-thrown as an
`InvalidOperationException` rather than surfacing the serializer's own
`ArgumentException`. The one case that does reach you as `ArgumentException` is
a request too large for a datagram. Both are raised before the awaiter is
registered, so nothing is left pending either way. The task completes on the
server reply, the timeout, cancellation, or session teardown.

### Where to put an `[RtmpeRpc]` method (placement)

> An `[RtmpeRpc]` method may live on **any** `NetworkBehaviour`
> on the object — the inbound call resolves to the component that declares it.
> Place RPCs on the script that uses them, as you would in other SDKs.

Mechanics worth knowing:

- **Anchor precedence.** Each object routes through its *anchor* — the **first**
  `NetworkBehaviour` on the GameObject. If the anchor declares the method it wins;
  otherwise the SDK resolves to the sibling component that declares it.
- **Ambiguity.** Two components on one object own the same id when they share a
  type name — the same type mounted twice, or two instantiations of one generic
  type, which are named by the generic definition. The frame names no instance,
  so: if one of them is the **anchor** it receives the call, and if neither is,
  the call is deliberately not dispatched. Give the object one of them.
- **State and RPCs share both the construction and the scope.** Both are
  addressed by a 32-bit FNV-1a identity over `"scope.member"`, and both fold the
  owner's **qualified type name** — `Type.FullName` for an ordinary type, and the
  generic **definition**'s name for a constructed generic, because that is the
  only spelling a declaration has. The wire carries no component discriminator,
  so both reach any component of the object, and two components collide only when
  they share that name — the ambiguity above (see
  [NetworkVariable types](#networkvariable-types)).

### Author-time checks

The `[RtmpeRpc]` method must be a `public` instance method on a `NetworkBehaviour`
subclass, with supported parameter types and no overloads. Its FNV-1a id (derived
from `"Type.FullName.MethodName"`) must not collide with a reserved id
(`100, 200, 300, 301, 400, 401`) or with another `[RtmpeRpc]` method on the same
type. ⚠️ At run time a collision is **not** a startup error and does not throw
into your code: `RpcRegistry.Validate()` runs on the first spawn of that concrete
type, and `NetworkBehaviour` catches what it raises and writes it to the Console.
The type is then recorded unmappable and its RPCs stop dispatching, silently
apart from that one line — so the shipped Roslyn analyzers, which surface these
at compile time, are the check to rely on.

---

## NetworkTransform

**Namespace:** `RTMPE.Sync`
**Inherits:** `NetworkBehaviour`
**Attach to:** any prefab that should sync its position / rotation / scale.

`NetworkTransform` reads the `Transform` of its `GameObject` each frame, compares
against the last-sent values, and sends a `StateSync` (0x40) packet when the delta
exceeds the configured threshold.

Only the **owner** sends updates. Remote clients receive updates and feed them to
`NetworkTransformInterpolator`.

### Inspector fields

| Field                | Type    | Default | Description |
|----------------------|---------|---------|-------------|
| `SyncPosition`       | `bool`  | `true`  | Whether the network may write this object's position — **both directions**: an unsynced axis does not trigger a broadcast when it moves, and an inbound update does not move the replica |
| `SyncRotation`       | `bool`  | `true`  | The same, for rotation |
| `SyncScale`          | `bool`  | `false` | Send scale updates (enable only if scale changes) |
| `TickAlignedSampling`| `bool`  | `true` | Broadcast the pose held on the tick boundary rather than at the frame that sends it — see [Tick-aligned sampling](#tick-aligned-sampling) |
| `PositionThreshold`  | `float` | `0.01`  | Minimum metres delta before sending position |
| `RotationThreshold`  | `float` | `0.1`   | Minimum degrees delta before sending rotation |
| `ScaleThreshold`     | `float` | `0.001` | Minimum per-axis delta before sending scale; evaluated only when `SyncScale` is on |
| `EnablePrediction`   | `bool`  | `false` | Run client-side prediction and server reconciliation for this object |
| `LerpThreshold`      | `float` | `-1`    | Position error above which a correction is blended in rather than ignored. `-1` takes the project default from `NetworkSettings` |
| `SnapThreshold`      | `float` | `-1`    | Position error above which a correction snaps instead of blending. `-1` takes the project default from `NetworkSettings` |

> ⛔ These gate **whether** an update is sent and **whether** one is applied.
> They do not mask the payload: every transform update carries position,
> rotation and scale, because the wire format has no per-field presence bit to
> carry the flags in. So `Sync Position = false` means "my position is local
> business", not "position is absent from the wire".
>
> An earlier draft of this table read "Send position updates", and the receive
> half was not gated at all — the method that honoured the flags,
> `NetworkTransform.ApplyState`, was called by nothing in the shipped runtime,
> while `NetworkTransformInterpolator` drove every remote replica without them.


> The names above are the Unity Inspector labels for the component's private
> `[SerializeField]` fields (e.g. `_syncPosition`, `_positionThreshold`) — set
> them in the Inspector; they are not public code identifiers.

> `LerpThreshold` and `SnapThreshold` use `-1` as a sentinel meaning "defer to
> the project setting", which is why it is distinct from `0`: a literal `0` is a
> valid authored value meaning "never blend / always snap".

### Methods

```csharp
// Owner-only. Reposition the object without the move being treated as a
// high-speed slide by the anti-cheat velocity cap — use for respawns, fast
// travel, and scripted cinematics. Sets the transform immediately and carries
// the velocity baseline to the destination, so the jump itself is not measured
// as travel; movement AFTER the teleport is capped as usual. Abandons any
// reconciliation blend still in flight. It does not itself send a packet (the
// next change-detection update emits the new pose). Ignored (with a warning)
// on objects this client does not own.
void OwnerTeleportTo(Vector3 worldPosition);
```

---

## NetworkTransformInterpolator

**Namespace:** `RTMPE.Sync`
**Inherits:** `MonoBehaviour`
**Attach to:** same prefab as `NetworkTransform`.

Maintains a ring buffer of received `TransformState` snapshots and smoothly
interpolates between them each frame.

### Inspector fields

| Field                  | Type     | Default | Description |
|------------------------|----------|---------|-------------|
| `BufferSize`           | `int`    | `10`    | Number of snapshots to retain |
| `InterpolationDelay`   | `float`  | `0.1`   | Seconds behind latest snapshot — absorbs jitter. Floored at two ticks (`0.067`); a shorter value is raised and reported once |
| `AdaptiveDelay`        | `bool`   | `true` | Float the delay between the two-tick floor and `InterpolationDelay` according to the measured arrival spread, instead of holding the fixed value |
| `OwnerTickTimeline`    | `bool`   | `true` | Build the render timeline from the owning client's input tick rather than the server's broadcast tick — see [Owner-tick timeline](#owner-tick-timeline) |
| `MaxExtrapolationSeconds` | `float` | `0.05` | How long to continue along the last measured velocity when the render cursor outruns the newest snapshot — about one and a half 30 Hz snapshot intervals. Also the deceleration horizon: the prediction eases out to zero velocity across this window, so the value shapes the guess at every `t` and does not merely cut it off. `0` restores freeze-then-resume |
| `InterpolateScale`     | `bool`   | `false` | Match your `NetworkTransform.SyncScale` setting |
| `MaxInterpolatedSpeed` | `float`  | `50`    | Speed ceiling (world units/s) for an inbound snapshot; a further jump is walked toward the claimed point instead of snapping. `0` disables the gate |
| `MaxFutureSkewSeconds` | `double` | `10`    | Largest accepted timestamp skew into the future, relative to the local clock; rejects far-future values that would otherwise freeze the buffer |

> The names above are the Unity Inspector labels for private `[SerializeField]`
> fields (e.g. `_bufferSize`, `_interpolationDelay`) — configured in the
> Inspector; they are not public code identifiers.

### Notes

- Position uses `Vector3.Lerp`.
- Rotation uses `Quaternion.Slerp`.
- Snapshots with `timestamp ≤ latestTimestamp` are discarded (monotonic guard).
- `TryInterpolate()` is a no-op when fewer than 2 snapshots are available.

---

## Remote motion timing

Three settings decide how smooth a remote object looks. All three are **on by
default**.

⚠️ A default governs components created from the version that introduced it
onward. Unity serialises these fields, so a prefab or scene authored earlier
carries the value it was authored with and is **not** changed by upgrading — an
upgrade must not silently alter the motion of a game that has already shipped.
To adopt the new behaviour in an existing project, set them on the prefabs you
want it on.

⛔ **And adopt per project, not per prefab.** The advisory below compares the two
timeline halves *on one object*, so an old prefab (both off) and a new one (both
on) are each self-consistent and neither is reported — while the remote players
drawn from them sit on different timelines and, on a clean link, up to ~33 ms
apart. A room whose players are a mixture is a room whose players are drawn at
different instants.

### Owner-tick timeline

A broadcast record carries two clocks: the server's broadcast tick and the
owning client's input tick. With this setting off the interpolator builds its
render timeline from the **server** tick. That tick is a uniform emission counter, but
the instant within each tick at which the server samples an object shifts with
its load, so the sampling cadence is replayed as motion the owner never made.

`NetworkTransformInterpolator.OwnerTickTimeline` switches the timeline to the
**owner's** input tick, binding each pose to the tick of the client that
produced it. When the selected tick is absent from a record the interpolator
falls back to the local receive clock; the two tick domains are never mixed.

### Tick-aligned sampling

An owner samples its transform in `Update()` — at a visual-frame instant — but
labels the sample with the simulation tick current at that moment. A receiver
replays it as though it had been captured exactly on the tick boundary. The gap
between the two walks across the tick whenever the frame rate is not a multiple
of the tick rate, and that walk is rendered as jitter.

`NetworkTransform.TickAlignedSampling` interpolates the broadcast pose back to
the boundary, between the current sample and the previous one — never
extrapolating — so peers replay the pose the owner actually held at the tick the
sample is labelled with.

> **Enable both, or neither.** The two settings remove different errors, and
> each leaves the other's in place. `TickAlignedSampling` removes the owner's
> frame-sampling error, bounded by one frame period. `OwnerTickTimeline` removes
> a larger one: which server tick a pose is filed under is decided by when the
> uplink arrived, so the server timeline re-imprints uplink jitter as up to a
> full tick of placement error. Enabling one is a partial improvement, not the
> feature — they are one feature split across the sending and receiving
> components.

You do not have to remember this. The first time a **remote** object's state
reaches a prefab with one of the two enabled and the other not, the SDK logs a
single warning naming the half that is set, the half that is missing, and the
error that stays in rendered motion. It is logged once per configuration, not
once per object — so it names the object where the mismatch was seen, and any
other prefab carrying the same pair needs the same correction.

Two things it cannot tell you. It needs a second client: alone in the Editor
every object is locally owned, so nothing takes the remote path. And it reads
both settings from the local prefab, which answers *"is this project configured
consistently"* — not what a particular peer's build is doing.

### Adaptive delay

`InterpolationDelay` is a fixed buffer: latency paid on every remote object to
absorb jitter. `AdaptiveDelay` floats it between the two-tick floor and the
configured value according to the measured arrival spread, so a clean link
renders closer to real time and a jittery one keeps its buffer.

It is independent of the two timeline settings above and may be enabled on its
own.

⛔ `OwnerTickTimeline` reconstructs the remote timeline from the owning client's
input tick, which is the owner's own cadence only while every participant runs
the same `NetworkSettings.tickRate`. No wire field carries the owner's rate, so
the SDK cannot check the precondition — a session deliberately mixing cadences,
including across platforms, should leave both timeline settings off.

---

## NetworkRigidbody / NetworkRigidbody2D

**Namespace:** `RTMPE.Sync`
**Inherits:** `NetworkBehaviour`
**Attach to:** a prefab with a `Rigidbody` (3-D) or `Rigidbody2D` (2-D). The
component menu entries are **RTMPE → Network Rigidbody** and **RTMPE → Network
Rigidbody 2D**.

> ⛔ **Not driven end to end yet — do not reach for these to replicate an
> object.** The frames they send are `PhysicsSync` (0x45); the gateway
> validates one and then **drops** it, because no Sync Service consumer ingests
> rigidbody state and the tick engine models transforms only. The owner-side
> capture and the remote correction path below are implemented and tested, and
> nothing on the server drives them today. Use `NetworkTransform` for anything a
> peer must actually see.
>
> This page said "use these instead of `NetworkTransform` when the object is
> driven by Unity physics" until 2026-08-23.

The design, for when the server half lands: the **owner** simulates physics and
sends state on `FixedUpdate`; **remote** clients smoothly correct their local
body with velocity blending, dead reckoning (extrapolation between packets), and
position lerp — so bodies keep simulating between ticks rather than
rubber-banding.

Replicating a physics object today: put a `NetworkTransform` on it, let the owner
simulate, and let non-owners be driven by the interpolator with their own
`Rigidbody` set to kinematic.

### Inspector fields (3-D; 2-D is analogous)

| Field (Inspector label)     | Default | Description |
|-----------------------------|---------|-------------|
| Sync Position / Rotation    | `true`  | Replicate position / rotation |
| Sync Velocity               | `true`  | Replicate linear velocity so remote bodies keep moving between packets |
| Sync Angular Velocity       | `true`  | Replicate angular velocity |
| Sync Sleep State            | `true`  | Idle the remote body when the owner's body sleeps |
| Sync Constraints            | `true`  | Propagate runtime `RigidbodyConstraints` changes (gated by settings) |
| Position / Rotation Threshold | `0.01` / `0.1` | Minimum change before a field is sent |
| Make Remote Kinematic       | `false` | Set remote copies kinematic and apply pose directly — for fully owner-authoritative bodies (player characters) |
| Snap Threshold              | `3.0`   | Position error (m) above which the remote body teleports instead of lerping |
| Enable Dead Reckoning       | `true`  | Extrapolate remote position with the last velocity between packets |
| Send Rate Hz                | `20`    | Owner send rate (clamped to the 30 Hz tick) |
| Enable Owner Reconciliation | `false` | Owner snaps to server-confirmed pose on large divergence — enable only with an authoritative physics server |

### Methods

```csharp
// Capture the current Rigidbody state (position, rotation, velocity, angular
// velocity, sleep, constraint mask) — the snapshot the owner sends each tick.
PhysicsState GetState();
```

> **Receive-side plausibility caps.** On each receiver the component validates
> inbound physics packets against the local `NetworkSettings`
> (`maxLinearVelocity`, `maxAngularVelocity`, `maxPositionDeltaPerTick`,
> `allowDynamicConstraints`, world bounds) plus a per-object rate limit;
> non-finite (NaN/Inf) values are always rejected. This is client-side
> defence-in-depth that keeps one peer's corrupt or hostile stream from
> poisoning another peer's PhysX state — not server-enforced anti-cheat. Leave
> the values at their defaults unless you are tuning those tolerances.

---

### Refused writes

A `NetworkVariable` refuses a value its own reader would refuse, because such a
value replicates to nobody: the receiver keeps its prior value, so the owner sees
it and no peer does. **A refused write is a no-op** — no event, no `IsDirty`, and
the setter returns `void` — so ask, or use the `Try` form:

```csharp
bool CanSend(T value)          // would this assignment take effect?
bool TrySetValue(T value)      // assign, and report whether it did
```

`NetworkVariableString` and `NetworkVariableList<T>` carry the same pair
(`CanSend` / `TrySetValue`, and `CanSend` / `TryAdd` on the list).

A write is refused for three reasons, and `CanSend` answers all three:

| Refused because |
|---|
| the **value** is one this variable's own reader would refuse — see the table below |
| the object belongs to **another player**: the variable flush skips every component the local player does not own, so the write would be stored here, announced to local subscribers, and sent to nobody. An object with no owner at all — spawned before this client holds a room seat — is purely local and is not refused |
| the owning behaviour's **lifecycle** has closed writes to it — after `OnNetworkDespawn`, and for a scalar also before its first spawn |

> Replication is unaffected by the ownership rule: a value arriving from the
> owner is applied through the SDK's internal wire path, which every non-owning
> client's variables are written by and which this guard leaves alone. What is
> refused is a *local* assignment on a replica.
>
> ⚠️ Since **8.0.0** that wire path raises `OnValueChanged`. Until then it went
> through the public `SetValueWithoutNotify`, whose contract is "apply this
> without echoing a callback" — so the SDK's replication callback fired on the
> owner and on no receiving client at all, which is the opposite of what a
> replication callback is for.

| Type | Refuses |
|---|---|
| `NetworkVariableFloat` | `NaN`, `±Infinity` |
| `NetworkVariableVector2` | any non-finite component |
| `NetworkVariableVector3` | any non-finite component |
| `NetworkVariableString` | not encodable as UTF-8 (a lone surrogate — typically a `Substring` that split a surrogate pair), or above 65535 UTF-8 bytes |
| `NetworkVariableListString` | the same, per element |

`NetworkVariableQuaternion` is the exception: a non-unit quaternion has a
nearest valid rotation, so it is **sanitised on send** rather than refused on
write. `default(Quaternion)` is `(0,0,0,0)`, not identity — pass
`Quaternion.identity` explicitly.

⚠️ A float **list** accepts a non-finite element, because its reader accepts one
and the element does replicate. The rule is "refuse what your own reader
refuses", not "refuse everything undesirable".


## NetworkVariable types

**Namespace:** `RTMPE.Sync`

> **The identity is unique across the object, not within a component — and it
> is derived, not assigned.** A `NetworkVariable` may be declared on any
> `NetworkBehaviour` of an object, and every one of them is flushed by its owner
> and applied on receivers. What the wire carries is `[object_id][variable_id]`
> and no component discriminator, so
> the id is what resolves the variable: if a second component claims an id a
> sibling already holds, the SDK logs an error at `OnNetworkSpawn` and that
> variable does not replicate. The id stays with whichever component registered
> it first, which is component order while every component constructs its
> variables in `OnNetworkSpawn` — the order the spawn path imposes.
>
> **Construct them unconditionally.** The identity is the same on every peer —
> it is a function of the type and the member name — but the *binding from that
> identity to a component* is made when the variable is constructed. A
> construction guarded by `IsOwner` (or by any other per-peer condition) exists
> on one machine and not on another, so an update that lands correctly on the
> owner reaches no variable on the peer that skipped it, and is dropped as an
> unknown identity.

All `NetworkVariable<T>` types share the same contract:

```csharp
// Constructor
new NetworkVariableXxx(NetworkBehaviour owner, string memberName, T initialValue)

// Read (any client, any time after OnNetworkSpawn)
T Value { get; }

// Write (owner only, after OnNetworkSpawn)
T Value { set; }

// Subscribe to replicated changes (runs on Unity main thread, all clients)
event Action<T, T> OnValueChanged   // (previousValue, newValue)

// Clear the dirty flag — called internally after flush. Apps rarely call this.
void MarkClean()
```

**Rules:**
1. Create inside `OnNetworkSpawn()` — never in `Awake()` / `Start()`.
2. Pass **`nameof(_field)`** — the name of the member the variable is assigned
   to. The identity is the FNV-1a 32-bit hash of
   `"OwnerTypeName.memberName"` — the owner's own runtime type, not the type
   that declared the member — so it is unique across the object without anybody
   coordinating it: components on one GameObject share a single identity
   namespace, and they differ by type name.
   ⚠️ Two components that share a type name are the residual case: the same
   component type mounted twice, or two instantiations of one generic type,
   which are named by the generic definition. The second one's variables are
   refused at registration and reported, rather than left addressing the
   first's state.
   ⚠️ Renaming the type or the member is a new identity, and therefore a rebuild
   of every peer.
3. Only the owner writes `Value`. All clients read and react via `OnValueChanged`.
4. Store delegate references before subscribing so you can unsubscribe in `OnNetworkDespawn()`.

### Available types

| Class                        | T             | Wire size          |
|------------------------------|---------------|--------------------|
| `NetworkVariableInt`         | `int`         | 4 bytes (LE i32)   |
| `NetworkVariableFloat`       | `float`       | 4 bytes (LE f32)   |
| `NetworkVariableBool`        | `bool`        | 1 byte             |
| `NetworkVariableVector2`     | `Vector2`     | 8 bytes (2 × LE f32) |
| `NetworkVariableVector2Int`  | `Vector2Int`  | 8 bytes (2 × LE **int32**) |
| `NetworkVariableVector3`     | `Vector3`     | 12 bytes (3 × LE f32) |
| `NetworkVariableQuaternion`  | `Quaternion`  | 16 bytes (4 × LE f32) |
| `NetworkVariableString`      | `string`      | 2 B length + UTF-8 |

### Late-join snapshot behaviour

When another player joins the current room, `SpawnManager` automatically
re-flags every `NetworkVariable` on every locally-owned, spawned object so
the next 30 Hz flush transmits its current value. The joiner therefore sees
the correct variable values within ~33 ms instead of waiting for the next
value change. `OnValueChanged` does **not** fire on the owner during a
resync — only the dirty flag flips.

### Example — correct subscribe / unsubscribe pattern

```csharp
public class Fighter : NetworkBehaviour
{
    private NetworkVariableInt _health;
    private Action<int, int>   _onHealthChanged;    // stored reference

    protected override void OnNetworkSpawn()
    {
        _health = new NetworkVariableInt(this, nameof(_health), initialValue: 100);

        _onHealthChanged = (prev, next) => UpdateHealthBar(next);
        _health.OnValueChanged += _onHealthChanged; // subscribe
    }

    protected override void OnNetworkDespawn()
    {
        if (_health != null)
            _health.OnValueChanged -= _onHealthChanged; // unsubscribe with same reference
    }
}
```

### Synchronised collections — `NetworkVariableList<T>`

For replicated lists (inventory, active buffs, a kill feed), use a
`NetworkVariableList`. It rides the same 30 Hz flush as scalar variables and the
same identity rule (derived from the type and the member's name). The
owner mutates it; receivers observe each change. Concrete types:
`NetworkVariableListInt`, `NetworkVariableListFloat`, `NetworkVariableListVector3`,
`NetworkVariableListString`.

```csharp
// Constructor (create in OnNetworkSpawn, like any NetworkVariable)
new NetworkVariableListInt(NetworkBehaviour owner, string memberName);

// List API — owner writes; every client reads.
int  Count { get; }
T    this[int index] { get; set; }
void Add(T item);
void Insert(int index, T item);
void RemoveAt(int index);
bool Remove(T item);
void Clear();
bool Contains(T item);
int  IndexOf(T item);

// Capacity. Add and Insert are refused — silently, and reported once a second —
// once Count reaches MaxCount; TryAdd returns false instead of refusing quietly.
int  MaxCount { get; }        // NetworkSettings.maxNetworkVariableListSize
bool IsFull { get; }
bool TryAdd(T item);          // false if refused: full, unsendable, or not owner
bool CanSend(T value);        // whether the ELEMENT is sendable — not whether there is room

// Per-op change notification (fires on owner and receivers, after the op applies).
event Action<NetworkVariableListChangeEvent<T>> OnListChanged;
//   .Kind (Add/Insert/RemoveAt/Set/Clear/FullSync), .Index, .NewValue, .PreviousValue

// When the pending op log passes this many ops, the next flush collapses to a
// single full-sync (bounds per-tick bandwidth). Default 32; hard cap 255.
int FullSyncOpThreshold { get; set; }
```

Steady-state edits ship as a compact delta log; a periodic full-sync (and the
late-join snapshot) ships the whole list. Receivers apply each payload
atomically — a truncated or malformed payload reverts to the pre-payload
contents, so a bad payload cannot leave owner and receiver holding different
lists.

A **lost** payload is the other half of that, and the periodic full-sync is what
answers it: the delta log is not idempotent and delivery is best-effort whenever
the ARQ extension is not negotiated, so without a refresh one dropped datagram
would part owner and replica for the rest of the session, with no sequence number
that would notice. `NetworkSettings.networkVariableListFullSyncIntervalSeconds`
sets the cadence — default 5 s, `0` switches it off, and the cost is one snapshot
per list per interval.

The maximum list size is `NetworkSettings.maxNetworkVariableListSize`
(default 1024), and it is the same number on both sides: a receiver drops an
inbound element past it, and the owner's `Add` and `Insert` are refused at it
rather than growing a list no replica is permitted to store.

> ⚠️ A list large enough that its full-sync exceeds one datagram is never
> refreshed by that path, and nothing reports it — arming a snapshot the flush
> cannot send would leave it queued in front of the delta log, which is working.
> The list keeps replicating by deltas, and a lost delta stays permanent for it.
> That threshold is in bytes and arrives well before 1024 elements for most
> element types (~280 `int`s), so a large list needs measuring rather than
> assuming. (A *dirty* over-sized list — after a join, or a burst of edits — is
> reported by the flush once a second.)
>
> ⚠️ `MaxCount` is enforced on the write only where the ceiling is known. Before
> a `NetworkManager` has published itself — a list built in `Awake`, say — no
> setting is reachable, so `Add` refuses nothing rather than measuring against a
> fallback the project may have raised. Receivers always bound an inbound
> payload.

### Per-variable send rate — `[NetworkVariable(SendRateHz)]`

By default every dirty variable flushes at the global 30 Hz tick. Annotate a
`NetworkVariable` field to cap **that variable's** outbound rate independently —
useful for values that change often but only need slow sync (health, ammo):

```csharp
public sealed class PlayerStats : NetworkBehaviour
{
    public NetworkVariableVector3 velocity;              // 30 Hz default

    [NetworkVariable(SendRateHz = 10f)]                  // ~66% less bandwidth
    public NetworkVariableInt health;

    [NetworkVariable(SendRateHz = 2f)]                   // cosmetic counter
    public NetworkVariableInt killStreak;
}
```

`SendRateHz = 0` (default) means "use the global tick". Intermediate changes are
coalesced into the next eligible send window — the latest value is never lost,
only rate-limited. Values above 30 Hz cannot exceed the tick. The scan is
reflection-cached per type on first spawn (no per-flush cost).

---

## NetworkObjectRegistry

**Namespace:** `RTMPE.Core`

Thread-safe map of `ulong objectId → NetworkBehaviour`, protected by an
explicit `lock`. Managed internally by `SpawnManager`. Provides query,
enumeration, and eviction access.

```csharp
// Look up by object ID. Auto-evicts the entry if the GameObject has been
// Unity-destroyed (returns null in that case).
NetworkBehaviour Get(ulong objectId)

// Returns a read-only snapshot of all currently registered live objects.
// Safe to iterate — the snapshot is taken under lock and excludes destroyed entries.
IReadOnlyList<NetworkBehaviour> GetAll()

// Register an object. Returns false for a null object, and refuses a
// re-entrant call made from inside an OnNetworkDespawn handler (logged as an
// error). If a different object is already registered under the same
// NetworkObjectId, the NEW object takes the slot first and the previous one is
// despawned after — so Get(id) already answers with the new object while the
// evicted one is running OnNetworkDespawn. The collision is logged as an error.
bool Register(NetworkBehaviour obj)

// Remove the entry for the given object ID, if present.
void Unregister(ulong objectId)

// Sweep the dictionary in one pass and evict every entry whose
// GameObject was Unity-destroyed. Returns the number of evicted entries.
// Does NOT fire OnNetworkDespawn (the managed reference is unusable once
// Unity has destroyed the GameObject).
// NetworkManager calls this automatically after sceneUnloaded / sceneLoaded.
int PruneDestroyed()

// Unspawn every registered object (fires SetSpawned(false) on each) and empty
// the table. It destroys nothing: the GameObjects outlive the call, and it is
// SpawnManager.ClearAll that destroys them on room leave and disconnect.
void Clear()
```

---

## NetworkSettings

**Namespace:** `RTMPE.Core`
**Inherits:** `ScriptableObject`

Create via **right-click → Create → RTMPE → Settings** in the Project panel.
Assign to `NetworkManager.Settings` in the Inspector.

These are **serialized public fields** (not C# properties) on a `ScriptableObject`.
Set them in the Unity Inspector or assign them in code by field name.

| Field (camelCase)              | Type     | Default     | Description |
|--------------------------------|----------|-------------|-------------|
| `serverHost`                   | `string` | `"127.0.0.1"` | RTMPE Gateway hostname or IP |
| `serverPort`                   | `int`    | `7777`      | UDP port |
| `heartbeatIntervalMs`          | `int`    | `5000`      | Milliseconds between Heartbeat packets |
| `connectionTimeoutMs`          | `int`    | `10000`     | Milliseconds before handshake times out |
| `tickRate`                     | `int`    | `30`        | This client's own cadence; the server is fixed at 30 Hz |
| `autoRejoinLastRoomOnReconnect`| `bool`   | `true`      | Auto-call `Rooms.JoinRoom(LastRoomId)` after a successful token-based `Reconnect()` |
| `sendBufferBytes`              | `int`    | `262144`    | UDP socket SO_SNDBUF (256 KiB) |
| `receiveBufferBytes`           | `int`    | `262144`    | UDP socket SO_RCVBUF (256 KiB) |
| `networkThreadBufferBytes`     | `int`    | `65536`     | Background thread read buffer. Floored at `PacketBuilder.MaxDatagramBytes`: a datagram larger than this is truncated by the socket, fails its authentication tag and is dropped with no diagnostic |
| `enableDebugLogs`              | `bool`   | `false`     | Unity Console tracing — set true only in development |
| `apiKeySealServerPublicKeyHex` | `string` | `""`        | 64-char hex — the gateway's static X25519 public key. **Required**: the API key is sealed to it, and the gateway accepts no other envelope |
| `serverPinningMode`            | `ServerPinningMode` | `Strict` | Which gateway identities are accepted. `Strict` is the shipped default and refuses to connect while the pin below is blank. `TrustOnFirstUse` adopts the first identity it sees — but still refuses when the pin store cannot be read, and when `requireFirstUseProvisioned` is set and the endpoint is unseen. `InsecureNoPinning` accepts any |
| `requireFirstUseProvisioned`   | `bool`   | `false`     | Under `TrustOnFirstUse`, refuse an endpoint no earlier run has pinned rather than adopting it |
| `requirePinnedServerPublicKey` | `bool`   | `false`     | Legacy switch that forces `Strict` **whatever the enum says** — a project carrying it from an older asset is pinned strictly even when it selects another mode |
| `pinnedServerPublicKeyHex`     | `string` | `""`        | 64-char hex — the gateway identity `Strict` compares against. **Required** under the default mode; copy it from the RTMPE dashboard |

> The table above lists the **core connection fields**. `NetworkSettings` also
> carries advanced tuning fields (interest management, client-side prediction
> thresholds, spawn-rate limits, variable batching, JWT verification). Inspect
> the `NetworkSettings` asset in the Unity Inspector for the full, current set.

---

## CreateRoomOptions

**Namespace:** `RTMPE.Rooms`

```csharp
public sealed class CreateRoomOptions
{
    // Display name shown in room lists. Max 64 CHARACTERS — code points, not
    // bytes and not UTF-16 units, so an emoji costs one and an Arabic letter
    // costs one. Default: "" (server assigns a name).
    public string Name { get; set; } = string.Empty;

    // Max players allowed. Range: 1–100. 0 = server default (100).
    // Anything else is refused before a packet is sent, and reported on
    // OnRoomError. The room service never clamped it; an earlier note here
    // said it did.
    public int MaxPlayers { get; set; } = 0;

    // Whether the room appears in public room listings. Default: true.
    public bool IsPublic { get; set; } = true;

    // When true (default), a successful CreateRoom automatically issues the
    // JoinRoom that seats the creator as host, so you end up inside the room
    // and gameplay starts from OnRoomJoined. Set false only for the deliberate
    // two-step flow where you drive JoinRoom yourself — otherwise the room
    // stays empty ("waiting", zero players) and the host is never seated.
    public bool AutoJoinAsHost { get; set; } = true;
}
```

---

## JoinRoomOptions

**Namespace:** `RTMPE.Rooms`

```csharp
public sealed class JoinRoomOptions
{
    // Name displayed to other players in the room (max 32 characters).
    // Default: "" (empty) — the server assigns a fallback name when blank.
    public string DisplayName { get; set; } = string.Empty;
}
```

---

## RoomInfo

**Namespace:** `RTMPE.Rooms`

Received in `OnRoomCreated`, `OnRoomJoined`, and `OnRoomListReceived`.

```csharp
public sealed class RoomInfo
{
    public string      RoomId      { get; }   // UUID — use for JoinRoom()
    public string      RoomCode    { get; }   // 6-char join code, e.g. "XKCD42" — use for JoinRoomByCode()
    public string      Name        { get; }   // Display name
    public string      State       { get; }   // "waiting" | "playing" | "finished"
    public int         PlayerCount { get; }   // Current number of players
    public int         MaxPlayers  { get; }   // Maximum capacity
    public bool        IsPublic    { get; }   // Appears in public room lists
    public PlayerInfo[] Players    { get; }   // Player roster snapshot (may be empty for list responses)

    public string      MasterId    { get; }   // PlayerId of the current host, or "" if none (derived from Players)
    public string      CurrentScene{ get; }   // Authoritative scene name (reserved __scene property), or "" if unset

    // Room custom properties (read-only; always non-null) and the server's
    // monotonic version counter for optimistic-concurrency updates.
    public IReadOnlyDictionary<string, PropertyValue> Properties { get; }
    public int         PropertiesVersion { get; }
}
```

---

## PlayerInfo

**Namespace:** `RTMPE.Rooms`

Received in `RoomManager.OnPlayerJoined`.

```csharp
public sealed class PlayerInfo
{
    public string PlayerId    { get; }   // UUID string
    public string DisplayName { get; }   // Name set in JoinRoomOptions
    public bool   IsHost      { get; }   // True if this player is the room's CURRENT master
                                         // client — rewritten on every host migration,
                                         // so the creator reads false after one
    public bool   IsReady     { get; }   // True if the player has signalled ready state
}
```

---

## NetworkState enum

**Namespace:** `RTMPE.Core`

```csharp
public enum NetworkState
{
    Disconnected,    // Not connected. Call Connect() or Reconnect() from this state.
    Connecting,      // Initial handshake in progress.
    Connected,       // Session established. Can call CreateRoom / JoinRoom.
    InRoom,          // Inside a room. Can call Spawn.
    Disconnecting,   // Disconnect() called, draining socket.

    // Token-based reconnect in progress. Transitions directly to
    // Connected on success, or Disconnected on timeout / failure.
    Reconnecting,
}
```

---

## DisconnectReason enum

**Namespace:** `RTMPE.Core`

Received in `NetworkManager.OnDisconnected`.

```csharp
public enum DisconnectReason
{
    Unknown,         // Unclassified reason.
    ClientRequest,   // You called Disconnect().
    ServerRequest,   // Server initiated the disconnect (received a Disconnect packet).
    Timeout,         // Initial handshake OR token reconnect did not complete within
                     // NetworkSettings.connectionTimeoutMs.
    ConnectionLost,  // Three consecutive missed HeartbeatAck responses AND no
                     // authenticated ack inside the liveness grace window
                     // (heartbeatLivenessGraceMs; 0 = twice the miss window,
                     // so ~30 s at the default 5 000 ms interval) — the miss
                     // counter alone never disconnects. Or a non-recoverable
                     // transport error (SocketException from the network thread).
    Kicked,          // Server forcibly removed the player.
    NonceExhausted,  // The outbound AEAD nonce counter reached 2^32 packets;
                     // the session must be fully re-established.
    ProtocolError,   // The handshake could not be completed as specified. Most
                     // often that is local: an unset or unparseable
                     // apiKeySealServerPublicKeyHex, an Ed25519 key pasted into
                     // the X25519 field, or a pinning refusal — all of which
                     // fail before a byte is sent. It also covers a gateway
                     // packet that violates the expected sequence.
}
```

### Which reason fires when? Which preserves the reconnect token?

| Scenario                                                                         | Reason           | Token preserved? |
|----------------------------------------------------------------------------------|------------------|------------------|
| 3 consecutive missed `HeartbeatAck` responses                                    | `ConnectionLost` | **Yes — recoverable** |
| App calls `NetworkManager.Disconnect()`                                          | `ClientRequest`  | No               |
| Server sends a `Disconnect (0xFF)` packet                                        | `ServerRequest`  | No               |
| `Connect(apiKey)` or `Reconnect()` does not reach `SessionAck` within `connectionTimeoutMs` | `Timeout`        | No on a first connect; **yes** on a reconnect attempt that drew no validated Challenge |
| Background thread raises `SocketException`                                       | `ConnectionLost` | No               |
| Server kicks the player (game logic)                                             | `Kicked`         | No               |
| Outbound AEAD nonce counter exhausted (2³² packets)                               | `NonceExhausted` | No               |
| Handshake refused, locally or by the gateway (see the enum above)                 | `ProtocolError`  | No, except a pin configuration that refuses during a reconnect |

> **Reconnect pattern.** The heartbeat-miss path preserves the token because it
> is the case where the client has strong evidence that the session is still
> server-side valid (no `Disconnect` packet was received, no socket error was
> raised). Two failures *during a reconnect attempt* preserve it as well — a
> `Timeout` that drew no validated Challenge, and a `ProtocolError` raised when
> the pin configuration refuses (no pin configured, none pre-provisioned, the
> pin store unreadable, or the configured hex malformed) — so the table's "No"
> for those two reasons holds for a first connection and not for a retry. Check `NetworkManager.CanReconnect` in your
> `OnDisconnected` handler — if it returns `true`, call `Reconnect()`;
> otherwise call `Connect(apiKey)` with fresh credentials.

---

## IDamageable interface

**Namespace:** `RTMPE.Core`

Implement on any `NetworkBehaviour` that can receive damage via the built-in
`ApplyDamage` RPC (method_id `301`). The SDK's NetworkManager (client-side) dispatches this RPC by looking
for this interface via `GetComponentInParent<IDamageable>()`.

```csharp
public interface IDamageable
{
    // Called on all clients when an ApplyDamage RPC is received.
    // damage — always a positive integer (the gateway validates and discards damage ≤ 0).
    void ReceiveApplyDamage(int damage);
}
```

### Example

```csharp
public class PlayerHealth : NetworkBehaviour, IDamageable
{
    private NetworkVariableInt _health;

    protected override void OnNetworkSpawn()
    {
        _health = new NetworkVariableInt(this, nameof(_health), 100);
    }

    // Called on all clients via the ApplyDamage RPC.
    public void ReceiveApplyDamage(int damage)
    {
        if (!IsOwner) return;
        _health.Value = Mathf.Max(0, _health.Value - damage);
    }
}
```

---

## NetworkTransport (abstract)

**Namespace:** `RTMPE.Transport`
**Inherits:** `IDisposable`

Abstract base for all network transports. The built-in `UdpTransport` derives
from this. Custom transports (a mock for tests, or one reaching the gateway
another way) also derive from it and are installed via
`NetworkManager.SetTransportFactory(factory)`.

```csharp
public abstract class NetworkTransport : IDisposable
{
    // True while the underlying socket is open and ready for I/O.
    public abstract bool IsConnected { get; }

    // Local endpoint the OS assigned after Connect().
    // Null before Connect(). Used by the SDK for the HandshakeInit AAD.
    public virtual System.Net.IPEndPoint LocalEndPoint { get; }

    // Open the socket / WebSocket / mock transport.
    public abstract void Connect();

    // Close the socket. Safe to call multiple times.
    public abstract void Disconnect();

    // Send all bytes. The array is owned by the caller; implementations
    // must not retain a reference.
    public abstract void Send(byte[] data);

    // Non-blocking receive. Returns bytes written to buffer, or 0 if nothing is ready.
    // Implementations MUST return 0 immediately when no data is available.
    public abstract int Receive(byte[] buffer);

    // Non-blocking readability poll. Returns true if at least one datagram
    // is available to read.
    public abstract bool Poll(int microSeconds);

    public abstract void Dispose();
}
```

---

## UdpTransport

**Namespace:** `RTMPE.Transport`
**Inherits:** `NetworkTransport`

Built-in non-blocking UDP transport. Used by default unless a custom transport
is installed via `NetworkManager.SetTransportFactory`.

```csharp
// Constructor
UdpTransport(
    string host,
    int    port,
    int    sendBufferBytes    = 262144,   // 256 KiB
    int    receiveBufferBytes = 262144)   // 256 KiB

// Also exposed for zero-copy hot paths (e.g. ArrayPool-rented buffers).
public void Send(byte[] buffer, int offset, int count)
```

Inherits all abstract members from `NetworkTransport`. Notable behaviour:

- **IPv4-then-IPv6 fallback** on DNS resolution. IPv6-only hosts are supported.
- **Routing probe** during `Connect()` discovers the actual outgoing interface IP
  and stores it in `LocalEndPoint`. On failure (e.g. isolated test containers
  with no default route) the probe falls back to loopback and logs a warning —
  a real-server handshake will fail the AEAD AAD check as expected.
- `SocketError.WouldBlock` (no data ready) and `SocketError.ConnectionReset`
  (ICMP port-unreachable on Windows) are silently swallowed per RFC 1122.

---

*RTMPE SDK 1.0.5 — [Getting Started](../getting-started.md) — [Architecture](../architecture.md)*
