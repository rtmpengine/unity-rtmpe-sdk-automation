# Troubleshooting Guide

> SDK Version: `com.rtmpe.sdk 1.0.8`

Problems you may meet while integrating the RTMPE SDK, with their causes and fixes. Each
entry starts with what you see — a Console message, an event or a behaviour — followed
by the cause and a checklist.

## Contents

- [Before you start](#before-you-start)
- [Connection](#connection)
- [Authentication](#authentication)
- [Reconnection](#reconnection)
- [Rooms and matchmaking](#rooms-and-matchmaking)
- [Spawning and ownership](#spawning-and-ownership)
- [State synchronisation](#state-synchronisation)
- [RPCs](#rpcs)
- [Scenes](#scenes)
- [Platforms](#platforms)
- [Authoring tools (analyzers and conversion)](#authoring-tools-analyzers-and-conversion)
- [Testing under a bad link — the Link Simulator](#testing-under-a-bad-link--the-link-simulator)
- [Reporting a problem](#reporting-a-problem)

---

## Before you start

1. **Turn on debug logs.** Tick **Enable Debug Logs** on your `NetworkSettings` asset.
   With it off, the SDK writes many of its warnings and errors as ordinary Console lines
   and leaves out its connection trace, so a Console filtered to warnings and errors
   hides them.
2. **Check the version line.** When the `NetworkManager` starts, it logs one line with
   the SDK version, the Unity version, the platform and the kind of build:

   ```text
   [RTMPE] SDK <version> — Unity <version>, <platform>, development build.
   ```

3. **Read the failure line.** When a first connection attempt times out, the SDK logs
   the stage at which the handshake stopped, for example:

   ```text
   [NM] connection failed after 10000 ms at stage 'NoServerReply' (transport=bound, handshakeInit=sent, challenge=not-received, session=not-established) — …
   ```

   | Stage | What it means |
   | --- | --- |
   | `TransportNotBound` | The UDP socket never opened: the operating system refused it (a firewall, or this build's network permission), or there is no route to the server. |
   | `HandshakeInitNotSent` | The socket opened, but the handshake was not sent before the timeout. |
   | `NoServerReply` | The handshake was sent and no answer came back. See [the timeout entry](#symptom-onconnectionfailed-reports-connection-timeout). |
   | `ServerReplyNotFinalized` | The server answered, but the session was never completed — usually a server-key mismatch. |

4. **Handle the events.** A player build has no Console. Subscribe to
   `NetworkManager.OnConnectionFailed`, `NetworkManager.OnDisconnected`,
   `RoomManager.OnRoomError` and `SpawnManager.OnSpawnRejected`, and log or show what
   they report.

---

## Connection

### Symptom: `Connect()` does nothing and the Console shows an error

`Connect()` refuses these cases before it starts an attempt, so no connection event
fires.

| Console message | Fix |
| --- | --- |
| `NetworkManager.Connect: apiKey must not be null or empty.` | No API key reached `Connect`. See [the missing-key entry](#symptom-rtmpeconnectionbootstrap-has-no-api-key-so-it-did-not-connect). |
| `NetworkManager.Connect: the NetworkManager is disabled or its GameObject is inactive.` | Activate the `NetworkManager` and its GameObject first. |
| `NetworkManager.Connect: called on a NetworkManager that is not the initialised singleton.` | Call `NetworkManager.Instance.Connect(...)`, not a stored reference to another copy. |
| `NetworkManager.Connect ignored — already in state …` | An attempt or session is already running. Wait for `OnDisconnected`, or call `Disconnect()` first. |
| `NetworkManager.Connect: WebGL is not a supported platform.` | See [WebGL is not a supported platform](troubleshooting.md#webgl-is-not-a-supported-platform). |

### Symptom: `OnConnectionFailed` fires straight away

The SDK checks the keys in your `NetworkSettings` asset before it sends anything, and a
configuration it cannot use fails the attempt at once. The reason names the setting.

| `OnConnectionFailed` reason | Fix |
| --- | --- |
| `No API-key envelope configured — set apiKeySealServerPublicKeyHex.` | Copy the dashboard's **Sealed-Box Public Key (X25519)** into **Api Key Seal Server Public Key Hex**. |
| `apiKeySealServerPublicKeyHex is not a valid X25519 public key.` | The field holds another value — usually the pinned (Ed25519) key, or a value that is not 64 hex characters. Copy the Sealed-Box Public Key again. |
| `apiKeySealServerPublicKeyHex equals pinnedServerPublicKeyHex — …` | The pinned key was copied into both fields. The two keys are different values. |
| `Server not pinned — refusing handshake. …` | **Server Pinning Mode** is `Strict` (the default) and **Pinned Server Public Key Hex** is empty: copy the dashboard's **Pinned Server Public Key** into it. Under `TrustOnFirstUse` with **Require First Use Provisioned** ticked, the pin must be stored before the first connection. The same reason appears when the pin store cannot be read; a Console line before it says what could not be read. |
| `pinnedServerPublicKeyHex is not a valid Ed25519 public key: …` | The pin is not 64 hex characters. Copy it again. |

### Symptom: `OnConnectionFailed` reports "Connection timeout."

The attempt ran for **Connection Timeout Ms** (10,000 by default) without completing; a
reconnect attempt reports "Reconnect timeout." instead. Read the failure line (see
[Before you start](#before-you-start)), then check the items for its stage:

- [ ] **`TransportNotBound`** — no firewall rule or missing network permission stops
      this build from opening a UDP socket.
- [ ] **`NoServerReply`** — **Server Host** and **Server Port** match the dashboard's
      connection settings.
- [ ] **`NoServerReply`** — UDP to the server port, and the replies to it, pass every
      firewall and router between the device and the server. TCP rules do not apply.
- [ ] **`NoServerReply`** — on macOS, the Application Firewall can discard the replies;
      the SDK then logs `[RTMPE] Connection timed out at NoServerReply on macOS — …`.
      Allow incoming connections for your app in **System Settings → Network → Firewall
      → Options**.
- [ ] **`NoServerReply`** — **Api Key Seal Server Public Key Hex** is this project's
      Sealed-Box Public Key. The server does not answer a handshake sealed to another key.
- [ ] **`NoServerReply`** — the API key is valid and active. When the handshake had to be
      sent more than once, a refusal is not reported, and a rejected key shows up as a
      plain timeout.
- [ ] **`ServerReplyNotFinalized`** — see
      [the server identity entry](#symptom-onconnectionfailed-reports-server-identity-verification-failed).

When the reason is a rejection recorded during the attempt instead — "Handshake rejected
by the server — …" or "Server identity verification failed — …" — see
[Authentication](#authentication).

### Symptom: it connects in the Editor, but a player build times out

- [ ] **Server Host** is `127.0.0.1` by default. In a build running on another device
      that address is the device itself, and the build logs
      `[RTMPE] Standalone build is connecting to a loopback address …`. Enter the
      dashboard's server host.
- [ ] The `NetworkManager`'s **Settings** field references your asset. When it is empty,
      the SDK uses built-in defaults and logs
      `[RTMPE] NetworkManager has no NetworkSettings assigned — falling back to an empty default.`
- [ ] The scenes in the build reference the asset you edited. When a project holds
      several `NetworkSettings` assets and only some carry the sealed-box key, the build
      log warns that it cannot tell which one your scenes use.
- [ ] The build has an API key source — see
      [the missing-key entry](#symptom-rtmpeconnectionbootstrap-has-no-api-key-so-it-did-not-connect).
- [ ] On macOS, the Application Firewall allows the app (see the previous entry).

### Symptom: a player build stops with an RTMPE error

| Build error begins | Fix |
| --- | --- |
| `[RTMPE] <n> NetworkSettings asset(s) exist but none carries an API-key envelope.` | A build without the sealed-box key could never connect. Set **Api Key Seal Server Public Key Hex** on the asset your `NetworkManager` uses, then build again. |
| `[RTMPE] This is a release build and … is in the project.` | A development build that did not finish left its staged API key file behind, and a release build would ship it. Delete the file the message names, then build again. |

A release build made while a `NetworkSettings` asset uses `InsecureNoPinning` does not
stop, but logs a warning that begins `[RTMPE] SECURITY: This is a RELEASE build`. Ship
with `Strict` and the pinned key, or with `TrustOnFirstUse`.

### Symptom: "Duplicate NetworkManager instance" in the Console

The full message begins
`[RTMPE] Duplicate NetworkManager instance on GameObject '…' — destroying it and keeping the existing one.`
The `NetworkManager` survives scene loads, so a second one in a scene you load is a
duplicate, and the SDK destroys its whole GameObject.

- [ ] Keep one `NetworkManager`, in the first scene your game loads.
- [ ] Keep components you need out of any GameObject that holds a duplicate
      `NetworkManager`.

### Symptom: the session ends with `DisconnectReason.ConnectionLost`

`ConnectionLost` has two causes, and the Console line tells them apart.

- **`[RTMPE] Heartbeat timeout — no acknowledged keep-alive within the liveness window. Disconnecting.`**
  The SDK sends a keep-alive every **Heartbeat Interval Ms** (5,000 by default) and
  declares the session lost when three in a row go unanswered and none has been answered
  within the liveness window — six intervals, 30 seconds at the defaults. The reconnect
  token is kept, so `Reconnect()` can resume the session.
- **`[RTMPE] Transport error: …`** The socket failed, for example because the device lost
  its network. The reconnect token is discarded; call `Connect(apiKey)`.

Keep-alives are sent from the main thread, so a paused Editor, a long loading hitch or an
app in the background sends none. For cellular links or low frame rates, raise
**Heartbeat Liveness Grace Ms**, which widens the liveness window.

---

## Authentication

### Symptom: "RtmpeConnectionBootstrap has no API key, so it did not connect."

No API key source answered. In the Editor the key comes from the credential vault that
the Setup Wizard writes, and a player build never carries that vault.

- [ ] **In the Editor:** store the key with **Window → RTMPE → Setup Wizard**.
- [ ] **A development build you test yourself:** in the Setup Wizard, tick **Inject this
      key into development builds**, then build with **Development Build** ticked.
- [ ] **A build you ship:** register a provider with `ApiKeySource.SetProvider` before
      the connection starts.
- [ ] **A launch you control** (your machine, a test rig, CI): pass
      `--rtmpe-api-key-file <path>` or set `RTMPE_API_KEY`.

A release build that no key source can reach also warns while it builds:
`[RTMPE] This release build has no API key source that a build can see.` See
[Giving a player build its API key](getting-started.md#giving-a-player-build-its-api-key).

### Symptom: `OnConnectionFailed` reports "Handshake rejected by the server"

The server declined the handshake, and the rest of the reason says what to check. The
SDK cannot verify who sent a rejection this early in the handshake, so it does not end
the attempt at once: it logs
`[RTMPE] A handshake rejection arrived on an unauthenticated frame — …` and reports the
reason when the attempt times out.

- [ ] The API key is active in the dashboard (**API Keys**) and belongs to the project
      whose connection settings this asset holds. A lost key cannot be shown again;
      create a new one.
- [ ] The key that reaches `Connect` is the one you expect (see the previous entry for
      the sources).
- [ ] **Api Key Seal Server Public Key Hex** and **Pinned Server Public Key Hex** match
      this project's **Connection settings**.
- [ ] When the reason names the connection limit, the project already holds as many
      sessions as it may. Close idle Editors and builds, then try again.

### Symptom: `OnConnectionFailed` reports "Server identity verification failed"

The server's identity key did not match **Pinned Server Public Key Hex** or, under
`TrustOnFirstUse`, the key stored for this host. An error in the Console reports the
failed check when the reply arrives. The SDK keeps waiting in case a genuine reply is
still on its way, so the attempt ends at the timeout with this reason, and the failure
line reads `ServerReplyNotFinalized`.

- [ ] Copy the dashboard's **Pinned Server Public Key** again, and check that the build
      uses the settings asset for this server.
- [ ] Under `TrustOnFirstUse`, a key stored for an earlier server stays in force. If the
      server's key changed for a known reason, call
      `NetworkManager.Instance.ClearPinnedKey()` before connecting.
- [ ] `InsecureNoPinning` skips the check. Use it for local testing only.

### Symptom: "JWT validation failed" closes the session as it opens

The Console shows `[RTMPE] SessionAck rejected: JWT validation failed (<reason>). Disconnecting.`,
and `OnDisconnected` fires with `DisconnectReason.Unknown` without `OnConnected`. The SDK
validates the session token it receives itself, against the JWT settings of your
`NetworkSettings` asset.

| Reason in parentheses | Fix |
| --- | --- |
| `JWT exp … is in the past …` or `JWT nbf … is in the future …` | The device clock is wrong by more than **Jwt Clock Skew Seconds** (120 by default) allows. Turn on automatic date and time, or raise the setting. |
| `JWT iss '…' does not match expected issuer` | Restore **Expected Jwt Issuer** to its default, `rtmpe-gateway`. |
| `JWT aud '…' does not match expected audience` | Restore **Expected Jwt Audience** to its default, `rtmpe-session`. |
| `JWT verification failed` | The token's signature did not verify against the key your settings name. Leave **Jwt Signature Algorithm** at `Ed25519` with **Jwt Signing Key Hex** and **Jwt Signing Key Pem** empty — the SDK then uses the key it verified during the handshake — or set **Jwt Signing Key Hex** to the dashboard's Pinned Server Public Key. |

When a configured key refuses a token that the server signed with its identity key, a
second error explains the choice. It begins
`[RTMPE] The SessionAck JWT was refused by the configured NetworkSettings key`.

An error saying that `expectedJwtIssuer` or `expectedJwtAudience` is empty, or that
`jwtSignatureAlgorithm` is `None`, means the check it names is switched off. Restore the
default value.

---

## Reconnection

### Symptom: `Reconnect()` returns `false`

| Console message | Meaning |
| --- | --- |
| `NetworkManager.Reconnect: no reconnect token — client must call Connect(apiKey) to re-authenticate.` | `CanReconnect` is `false`; sign in again with `Connect`. |
| `NetworkManager.Reconnect ignored — a reconnect attempt is already in progress.` | A retry loop is running. Wait for `OnConnected` or `OnReconnectFailed`. |
| `NetworkManager.Reconnect ignored — state is …, must be Disconnected.` | The manager is not disconnected yet. |

Decide with `CanReconnect`, not with the return value of `Reconnect()`. Between
attempts, a retry loop waits in `Disconnected`, where `Reconnect()` returns `false`
too — and a `Connect()` made in response cancels the loop and signs in from scratch:

```csharp
using RTMPE.Core;

var net = NetworkManager.Instance;
if (net.State == NetworkState.Disconnected)
{
    if (net.CanReconnect)
        net.Reconnect();                            // resume with the reconnect token
    else if (ApiKeySource.TryResolve(out string apiKey))
        net.Connect(apiKey);                        // no token: sign in again
}
```

### Symptom: `CanReconnect` is `false` after a drop

Only some endings keep the reconnect token:

- **Kept:** no keep-alive answered within the liveness window (`ConnectionLost`), the
  server closed the session (`ServerRequest`), and a reconnect attempt that timed out
  before the server answered.
- **Discarded:** your own `Disconnect()`, a first connection that timed out, a transport
  error, a kick, a protocol or configuration failure, and a retry loop that gave up.

When the token is gone, call `Connect(apiKey)`. [Getting Started](getting-started.md)
describes the reconnect flow in full.

### Symptom: `OnReconnectFailed` fires

`Reconnect()` makes up to **Max Reconnect Attempts** attempts (5 by default), with a
growing, randomised delay between them. `OnDisconnected` fires after each failed attempt,
and `OnReconnectFailed` — with the number of attempts made — when the loop gives up. By
then the token is discarded.

- [ ] Call `Connect(apiKey)` from `OnReconnectFailed` to sign in again.
- [ ] Do not wrap `Reconnect()` in a retry loop of your own; it already retries.

### Symptom: after a reconnect, the player is not back in the room

- [ ] **Auto Rejoin Last Room On Reconnect** (on by default) rejoins the last room after
      a successful `Reconnect()`: `OnAutoRejoinAttempt` fires, then
      `RoomManager.OnRoomJoined` — or `RoomManager.OnRoomError` when the room has closed
      in the meantime. Show a room list on that error.
- [ ] A new sign-in with `Connect()` does not rejoin automatically. Enter a room
      yourself, or let `RtmpeConnectionBootstrap` do it with its **Rejoin Last Room**
      option.

### Symptom: the session is gone when a mobile app returns from the background

A suspended app runs no `Update`, so it sends no keep-alives, and the operating system
may close its socket. What recovers the session depends on how you connect.

- **`RtmpeConnectionBootstrap`** (**Component → RTMPE → Connection Bootstrap**): leave
  **Reconnect On Drop** and **Recover On Resume** on. It reconnects when the app resumes,
  and with **Fresh Session When Recovery Fails** on it signs in again when the token did
  not survive.
- **Your own code:** do not call `Disconnect()` from `OnApplicationPause`. It signs the
  player out and discards the reconnect token. Recover on resume instead:

```csharp
using RTMPE.Core;
using UnityEngine;

public sealed class ResumeSession : MonoBehaviour
{
    private bool _suspended;

    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            _suspended = true;          // leave the session alone while suspended
            return;
        }

        if (!_suspended) return;        // some platforms report a resume at start-up
        _suspended = false;

        var net = NetworkManager.Instance;
        if (net == null || net.State != NetworkState.Disconnected) return;

        if (net.CanReconnect)
            net.Reconnect();
        else if (ApiKeySource.TryResolve(out string apiKey))
            net.Connect(apiKey);
    }
}
```

When the socket survived the suspension, the state is still `Connected` and nothing needs
to happen. When it did not, the keep-alive check notices within the liveness window;
handle that `OnDisconnected` the same way.

---

## Rooms and matchmaking

### Symptom: `CreateRoom`, `JoinRoom` or `JoinRoomByCode` does nothing

- [ ] The client is connected. Before `OnConnected` the Console shows
      `[RTMPE] RoomManager.<method>: requires Connected or InRoom state (current: …).`
- [ ] Every value is within the room rules. A value outside them is refused before the
      request is sent: the Console shows `[RTMPE] RoomManager.<method>: <reason>` and
      `RoomManager.OnRoomError` fires with the same reason. Subscribe to `OnRoomError` —
      it is the only report a player build can see.

| Value | Rule |
| --- | --- |
| `CreateRoomOptions.Name` | Up to 64 characters, with no control or invisible formatting characters. Empty lets the server choose. |
| `CreateRoomOptions.MaxPlayers` | `0` (the server chooses) or `1`–`100`. |
| `JoinRoomOptions.DisplayName` | Up to 32 characters, same character rule. |
| The `JoinRoomByCode` code | Exactly 6 characters from `ABCDEFGHJKMNPQRSTUVWXYZ23456789`. |

A join code is exactly six characters from that set, so a code shown with a separator
(`ABC-234`) or typed with a space is refused: the extra character is outside the set and
makes the code seven characters long. The set has no `O`, `I`, `L`, `0` or `1`, so a code
typed with one of those was misread. Lower case is accepted. Check a value first with
`RoomFieldLimits.ValidateRoomName`, `ValidateDisplayName`, `ValidateMaxPlayers` or
`ValidateRoomCode`: each returns `null` for a valid value and a sentence naming the rule
otherwise.

### Symptom: `OnRoomError` reports a request that drew no answer

- **"CreateRoom timed out"** — the server did not answer the create request, and the
  request is discarded. Try again.
- **"JoinRoom drew no answer … within its retransmit budget"** — the join is still
  outstanding, and a late answer is still accepted: `OnRoomJoined` can still fire. Treat
  the error as "no answer yet", not as a refusal.

### Symptom: `OnRoomError` reports "leave the current room before creating another"

The server keeps each session in one room and refuses a room created by a session that
still holds a seat. This SDK leaves the current room first when `CreateRoom` or `JoinRoom`
is called from inside one, so the message comes from an older SDK, or from a create that
crossed a join still on its way.

- [ ] Update the SDK, or call `LeaveRoom` and wait for `OnRoomLeft` before `CreateRoom`.
- [ ] A create or join called from inside a room raises `OnRoomLeft` first, when the
      server confirms the leave, and is sent after it. If that leave is refused or not
      answered, `OnRoomError` says the create or join was not sent.

### Symptom: `StartMatchmaking` throws

- **`ArgumentException`** — an option is invalid: `MatchmakingOptions.Mode` is empty,
  longer than 64 UTF-8 bytes or contains control characters, or `LobbyName` or
  `DisplayName` breaks its rule. The message names the field.
- **`InvalidOperationException`** — the client is not connected, or a request is already
  in flight. Call `CancelFindMatch()` before starting another.

The outcome arrives on `OnMatchmakingComplete`, `OnMatchmakingFailed`,
`OnMatchmakingCancelled` or `OnMatchmakingTimedOut`. The default timeout is 30 seconds.

### Symptom: `LobbyManager.ListRooms` throws `ArgumentException`

The query is refused before it is sent. The message names the part at fault:

- [ ] **The lobby name.** `LobbyQueryOptions.LobbyName` is empty by default, and there is
      no default lobby: set up to 32 characters from `A`–`Z`, `a`–`z`, `0`–`9`, `_` and
      `-`. Check a name with `LobbyName.IsValid(name)`.
- [ ] **A filter.** A filter has an empty key or a key longer than 32 bytes, an operator
      outside `LobbyFilterOp`, a `null` value, or a value that is not a `string`, `int`,
      `float`, `double` or `bool` — a `long` is the usual one — or `NaN` or an infinity.
      Check a value with `LobbyFilterValue.IsValid(value)`.
- [ ] **The size.** A query with so many filters that it does not fit in one packet is
      refused too.

---

## Spawning and ownership

### Symptom: a spawned object's `IsOwner` is always `false` (the player never moves)

`IsOwner` is `true` only on an object the SDK spawned with the local player as its
owner. When your code keeps reporting "not the owner", the object running it is usually
not the one you spawned.

- [ ] The object comes from `NetworkManager.Instance.Spawner.Spawn(...)`. A scene-placed
      or `Instantiate`d `NetworkBehaviour` is never networked, so its `IsOwner` stays
      `false`. An `Instantiate` the analyzer can prove creates one is reported as
      [`RTMPE1030`](diagnostics.md#RTMPE1030).
- [ ] `Spawn` returned an object, not `null` (see the next entry).
- [ ] No second copy of the script sits in the scene. Log `GetInstanceID()`, `IsSpawned`
      and `OwnerPlayerId` to tell the copies apart.
- [ ] You spawn from `RoomManager.OnRoomJoined` or later. The local player id arrives
      with the room, so an earlier spawn has no owner.

### Symptom: `Spawn` returns `null`

| Console message | Fix |
| --- | --- |
| `[SpawnManager] Spawn: prefab <id> is not registered.` | Register the prefab (see the next entry). |
| `[SpawnManager] Spawn refused: ownerPlayerId '…' is not the local player.` | Spawn under the local player, then hand the object over with `RequestOwnershipTransfer`. |
| `[SpawnManager] Spawn rate cap reached (<n>/s for one spawner, <m>/s in all); excess spawns dropped this bucket.` | One player spawned more in one second than **Max Spawns Per Second** (100 by default), or the room as a whole spawned more than eight times that. Spread the spawns out, or raise the setting. |
| `[SpawnManager] Spawn count cap reached (<n>); spawn dropped.` | More live objects than **Max Spawns Per Room** (5,000 by default). Despawn the objects you have finished with. |

The two cap messages are ordinary log lines unless **Enable Debug Logs** is on.

### Symptom: other players' objects never appear on this client

- [ ] The prefab is registered on every client under the same id. A client that receives
      a spawn for a prefab it does not know logs
      `[SpawnManager] CreateLocal: prefab <id> not registered.` To register prefabs,
      select them in the Project window, open **Window → RTMPE → Network Prefabs** and
      press **Allocate id for selection**. Then press **Generate RtmpePrefabIds.cs and the
      prefab registry**, and assign the generated registry to **Prefab Registry** on your
      `NetworkSettings` asset. In code, `Spawner.RegisterPrefab(id, prefab)` does the same.
- [ ] The spawner was in a room when it spawned. A spawn made before the room is joined
      logs `[SpawnManager] Spawn of prefab <id> before this client has a room seat` and
      reaches no other client.
- [ ] The room accepted the spawn. When it refuses one, `SpawnManager.OnSpawnRejected`
      fires with the object id and the reason, and the object exists on the spawner only.

### Symptom: remote objects stay frozen

The Console shows a line that begins
`[RTMPE] Networked object <id> ('…') is receiving remote transform updates`, and goes on
to say that the object carries no `NetworkTransformInterpolator`, or that its
interpolator is switched off. Motion arrives, but nothing draws it.

- [ ] Add **Component → RTMPE → Network Transform Interpolator** to the prefab next to
      its `NetworkTransform`, or enable the one that is there. The `NetworkTransform`
      Inspector offers a button for either.
- [ ] Keep **Tick Aligned Sampling** (on `NetworkTransform`) and **Owner Tick Timeline**
      (on `NetworkTransformInterpolator`) both on or both off. When they differ, the SDK
      logs `[RTMPE] Remote motion timing is half-enabled in this project.`

### Symptom: a fast object moves more slowly on other clients

The owner's broadcast speed is capped at **Max Owner Velocity Meters Per Second** (50 by
default), so faster movement reaches other clients clamped to that speed.

- [ ] Raise the setting for vehicles and projectiles, or set it to `0` to switch the cap
      off.
- [ ] Move an object instantly with `OwnerTeleportTo(position)` on its
      `NetworkTransform`, not by writing `transform.position`, so the jump is not
      counted as travel — and so other players see it arrive rather than travel
      there at their speed limit. One teleport per object per second is shown as one;
      a faster one is walked.

### Symptom: `Despawn` is refused for another player's object

The Console shows `[SpawnManager] Despawn refused for object <id>: it is owned by '…'`.
Only the owner's despawn reaches the room, so the SDK does not destroy the object locally
either. Ask the owner to despawn it, or take ownership first with
`RequestOwnershipTransfer` on `NetworkManager.Instance.Spawner.Ownership`.

---

## State synchronisation

### Symptom: `NetworkVariable` values never reach other clients

- [ ] The object was spawned with `Spawner.Spawn`; scene-placed objects are not
      networked.
- [ ] Every client constructs the variable in `OnNetworkSpawn()`, unconditionally, with
      `nameof(field)` as its name. A variable built only on the owner — inside
      `if (IsOwner)` — has nothing to receive its updates on other clients.
- [ ] Only the owner writes `Value`. A write on another client logs
      `… refused a write at assignment: this client does not own the object.` Send the
      owner an RPC, or take ownership with `RequestOwnershipTransfer`.
- [ ] Writes happen while the object is spawned. A write before `OnNetworkSpawn` or after
      `OnNetworkDespawn` logs `… dropped an assignment: the object is not spawned.` Pass a
      starting value to the variable's constructor instead.
- [ ] The value changed. Assigning the value a variable already holds sends nothing.
- [ ] The value can be sent — see [the refused-value entry](#symptom-a-value-is-refused-or-never-sent).

### Symptom: "two NetworkVariables derive identity" or "is already held by"

A variable's identity comes from its component's type and the name passed to its
constructor, and it must be unique across the object.

- **`InvalidOperationException: … two NetworkVariables derive identity <id>`** — one
  component constructs two variables under one name, or a base class and its subclass
  both use a member name. Pass each variable the name of its own member
  (`nameof(_field)`), and rename one member of a base/derived pair. The analyzer reports
  this at compile time as [`RTMPE1010`](diagnostics.md#RTMPE1010).
- **`… identity <id> is already held by <Type> on the same object.`** — the same component
  type sits twice on one object, and the second copy's variable does not replicate. Keep
  a single instance (mark the class `[DisallowMultipleComponent]`), or move the variable
  to a second type.

Renaming a type or a member changes the identity, so every client must run the same
build.

### Symptom: a value is refused or never sent

- **Non-finite numbers** — a `NaN` or infinite `float`, or a vector with such a
  component, is refused with `… refused a value at assignment: …`, and the previous value
  is kept. Test with `CanSend(value)` first — it answers without logging — or write with
  `TrySetValue(value)`, which returns `false` when the write is refused and logs the same
  warning for a value it cannot send.
- **Strings** — a string that is not valid UTF-8 (usually a surrogate pair cut in half by
  `Substring`) is refused the same way. A string too long for one packet — about 1,100
  ASCII characters — is kept but never sent, and the Console repeats a warning containing
  `does not fit a single datagram even on its own and is NOT being sent.` Shorten it, or
  split it across several variables.
- **Custom variable types** — a `Serialize` that throws logs `… Serialize() threw …`
  once a second, and the variable is not sent until it stops throwing.

### Symptom: a late joiner sees stale values for a moment

When a player joins, every client sends the current value of each variable it owns: once
straight away, and again one second later, because the first copy can be lost. A joiner
therefore has the room's state within about a second, and `OnValueChanged` fires on it
for each value that differs from the one it started with.

- [ ] The owners are still connected when the player joins.
- [ ] Your code reads the current `Value` when the object spawns, as well as subscribing
      to `OnValueChanged`.

[Late-join snapshot](architecture.md#8-late-join-snapshot) describes the full sequence.

### Symptom: `OnListChanged` reports `FullSync` again and again

A `NetworkVariableList` receives its whole contents as a `FullSync` when a player joins
(twice, as above), and again at every refresh: a list that has gone quiet is re-sent every
**Network Variable List Full Sync Interval Seconds** (5 by default; `0` switches the
refresh off). A `FullSync` does not mean the contents changed. Read the list, or compare
before rebuilding UI.

### Symptom: a `NetworkVariableList` stops growing

`Add` is refused, with a warning naming the limit, when the list is at `MaxCount`: the
smaller of **Max Network Variable List Size** (1,024 by default) and what one packet can
carry, because a joining player receives the whole list at once. `IsFull`, `CanAdd` and
`TryAdd` answer the question without a warning. Hold fewer elements, or split the data
across several lists.

### Symptom: updates arrive late or in bursts

- [ ] Measure first: open **Window → RTMPE → Network Debugger** during Play and read
      **Traffic** and **Diagnostics**. A **Server Backpressure** reading that climbs
      towards 255 means this client sends faster than the server accepts.
- [ ] A growing `NetworkManager.ReplicationFlushesDeferredCount` means variables are sent
      less often than asked; reduce what changes each tick.
- [ ] The main thread keeps up. Received packets are handled on the main thread, so long
      frames delay them, and a sustained backlog is dropped with
      `[RTMPE] MainThreadDispatcher: queue full …`.
- [ ] Other clients do not send more than this client accepts. The SDK drops packets
      above a per-second limit that grows with the room's player capacity, and logs
      `[RTMPE] Inbound packet rate exceeded …; dropping.` Reduce what each client sends,
      for example with per-variable send rates.
- [ ] This client moves no more objects than the server takes from one connection. Where
      the server supports it, the transforms owed in a tick leave together, up to 21 to a
      packet (38 with `quantizeTransforms`), and one connection may send 120 state packets
      and 1,920 transforms a second. At the default 30 Hz tick that is about 60 moving
      objects; fewer at a higher `tickRate`, and fewer still if objects use client-side
      prediction, whose input batches count as state packets. Past the limit, whole
      packets of transforms are dropped; heartbeats, RPCs and variables are not.
- [ ] Rehearse on a poor link with the
      [Link Simulator](#testing-under-a-bad-link--the-link-simulator), and see the
      [Performance Tuning Guide](performance-tuning.md).

---

## RPCs

### Symptom: an `[RtmpeRpc]` method never runs on other clients

- [ ] The caller is in a room. Outside one, the Console shows
      `[RTMPE] NetworkManager.SendEnhancedRpc: must be in a room.`
- [ ] The caller owns the object, or the object is shared. The server delivers an RPC on
      an object only from its owner, unless the object was spawned with
      `sharedAuthority: true` or is a world object (`RtmpeWorldAuthority`, shared by
      default). Other callers' RPCs are dropped without any message to them.
- [ ] The method is `public`, not `static`, declared on a `NetworkBehaviour`, and uses
      supported parameter types: `int`, `float`, `bool`, `string`, `byte[]`, `ulong`,
      `Vector3`, `Color`, `Quaternion` and registered `INetworkSerializable` types. The
      analyzers report violations at compile time.
- [ ] You expect it on the right clients. `RpcTarget.Others` does not run on the caller,
      and a method declared `RpcTarget.Server` never runs on a client.
- [ ] A method that declares `Caller` refuses other callers. The sender logs
      `[RTMPE] NetworkManager: '<Type>.<Method>' not sent — it declares Caller = …`, and a
      receiver logs `[RTMPE] RPC '<Type>.<Method>' refused: it declares Caller = …`.

[Remote Procedure Calls](api/index.md#remote-procedure-calls) describes targets and
callers.

### Symptom: an RPC is "not sent" because it exceeds the 1128-byte limit

An `[RtmpeRpc]` call carries at most 1,128 bytes of parameters, the value of
`EnhancedRpcPacketBuilder.MaxSendablePayloadBytes`. A larger call is not sent, and the
Console shows a warning that begins `[RTMPE] NetworkManager.SendEnhancedRpc: not sent —`.
Split the data across several calls, or keep large state in variables.

### Symptom: `no [RtmpeRpc] method with id 0x… on <Type>`

The call reached the object, but the SDK found no component to run it on. It routes a
call to the component that declares the method, and warns when no component on the
object declares it, or when several do.

- [ ] Sender and receiver use the same prefab, so the receiver's object carries the
      component that declares the method.
- [ ] Every client runs the same build. A method renamed or removed in one build is
      unknown to the others.
- [ ] For a room-wide event, declare the RPC on one dedicated networked object rather
      than on every player.

### Symptom: a custom parameter type arrives as `null`

The receiver logs a warning that begins
`[RTMPE] RpcSerializer: rejected unregistered INetworkSerializable type`. Payload types
are accepted only once registered. Register each type on every client before the first
RPC carrying it can arrive:

```csharp
using RTMPE.Rpc;
using UnityEngine;

public struct HitInfo : INetworkSerializable
{
    public int Damage;
    public Vector3 Point;

    public void NetworkSerialize(IRtmpeWriter writer)
    {
        writer.WriteInt32(Damage);
        writer.WriteVector3(Point);
    }

    public void NetworkDeserialize(IRtmpeReader reader)
    {
        Damage = reader.ReadInt32();
        Point = reader.ReadVector3();
    }
}

public static class RpcTypes
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register() => RpcTypeRegistry.Register<HitInfo>();
}
```

Use `Register<T>()` rather than the `Register(Type)` overload: it keeps the constructor
an IL2CPP build needs (see [the IL2CPP entry](#il2cpp-missingmethodexception-at-runtime)).
Marking a type `[RtmpeRpcSerializable]` registers it only when
`RpcTypeRegistry.AllowAppDomainScan` is `true`, and it is `false` by default.

### Symptom: "arg count mismatch" or "type mismatch" on the receiver

A warning such as `[RTMPE] RPC '<Type>.<Method>' arg count mismatch: …` or
`… type mismatch: …` means the sender and the receiver declare the method differently.
Rebuild every client from the same code.

---

## Scenes

### Symptom: the room never settles after a scene change

`OnAllPlayersSceneLoaded` fires only when every player has reported the scene loaded, and
`OnSceneLoadTimedOut` fires when that has not happened within **Scene Ready Timeout
Seconds** (60 by default). A client that could not load the scene never reports.

- [ ] The scene is in the build and ticked in the scene list (**File → Build Settings**,
      or **File → Build Profiles** in Unity 6). An unticked scene is not in the build.
- [ ] **Window → RTMPE → Network Scenes** lists the build's scenes, flags unticked,
      missing and duplicate names, and — with **Scan this project's scripts for scene
      names** — checks the names your scripts load. It can check only names written as
      literals, and counts the others as unchecked.
- [ ] If you load scenes yourself, call `NetworkManager.Instance.Scene.ReportReady()` when
      the load completes. [`RtmpeSceneLoader`](api/index.md#scene-loading-without-writing-any)
      does this for you, and logs `[RTMPE] RtmpeSceneLoader: the engine refused to load scene '…'`
      when Unity cannot load a scene.
- [ ] `RtmpeSceneLoader` sits on a root GameObject or on the `NetworkManager`'s object. On
      an object that a `Single` load destroys, it logs an error and never reports.

### Symptom: objects from the previous scene stay on other clients

A scene load destroys the local GameObjects and the SDK drops them from its registry, but
other clients keep them until their owner despawns them. Despawn the objects you own
before loading the next scene:

```csharp
using RTMPE.Core;
using UnityEngine.SceneManagement;

var spawner = NetworkManager.Instance.Spawner;
foreach (var obj in spawner.Registry.GetAll())
{
    if (obj.IsOwner)
        spawner.Despawn(obj.NetworkObjectId);
}
SceneManager.LoadScene("NextLevel");
```

For scene changes the whole room follows, see [Networked scenes](api/index.md#networked-scenes).

---

## Platforms

<a id="il2cpp-missingmethodexception-at-runtime"></a>
### Symptom: an IL2CPP build throws `MissingMethodException`, or RPCs stop working on device

Managed code stripping removes members that nothing calls directly, and the SDK reaches
RPC methods and variable members by reflection. The package's own `link.xml` preserves
the SDK runtime. Members in your assemblies need a `link.xml` of your own when stripping
is enabled (**Player Settings → Managed Stripping Level**):

- [ ] your `[RtmpeRpc]` methods — preserve the classes that declare them;
- [ ] your custom `NetworkVariable<T>` subclasses;
- [ ] your `INetworkSerializable` payload types.

```xml
<linker>
    <assembly fullname="Assembly-CSharp">
        <type fullname="MyGame.PlayerController" preserve="all" />
        <type fullname="MyGame.HealthVariable" preserve="all" />
        <type fullname="MyGame.HitInfo" preserve="all" />
    </assembly>
</linker>
```

Place the file under `Assets/`, and use your assembly's name when your scripts live in an
assembly definition.

Register payload types with `RpcTypeRegistry.Register<T>()`, which keeps their
constructor through stripping. When a class's public parameterless constructor has been
stripped, `RpcTypeRegistry.Register(Type)` throws `ArgumentException` for it, and the
`[RtmpeRpcSerializable]` scan skips it; receivers then log
`[RTMPE] RpcSerializer: rejected unregistered INetworkSerializable type` and the
parameter arrives as `null`.

### WebGL is not a supported platform

The SDK drives its transport from a dedicated background thread, and the browser player
provides no threads. In a WebGL player, `Connect()` and `Reconnect()` do nothing and log
`[RTMPE] NetworkManager.Connect: WebGL is not a supported platform.` (or the same line
for `Reconnect`), and selecting the WebGL build target (**Web** in Unity 6) raises a
compiler warning.

A custom transport installed with `NetworkManager.SetTransportFactory` does not change
this: it replaces the socket, not the thread above it. Play mode keeps working with the
WebGL target selected, because it runs in the Editor's own player — the compiler warning
is there so that the limit is visible before a WebGL build ships.

Supported platforms are Windows, macOS, Linux, Android and iOS.

### Symptom: a mobile build loses its session

- [ ] After the app returns from the background, see
      [the background entry](#symptom-the-session-is-gone-when-a-mobile-app-returns-from-the-background).
- [ ] On cellular links a stall can delay keep-alive answers past the liveness window.
      Raise **Heartbeat Liveness Grace Ms**, and raise **Connection Timeout Ms** when
      first connections are slow.
- [ ] The mobile build uses the same **Tick Rate** as every other client in the room, or
      remote motion plays at the wrong speed — see
      [Tick rate](performance-tuning.md#tick-rate).

---

<a id="authoring-tool-issues-roslyn-analyzer--conversion-quick-fixes"></a>
## Authoring tools (analyzers and conversion)

### Symptom: RTMPE diagnostics appear, but the only quick fix offered is "Suppress / Configure"

**Check the rule id first.** Most RTMPE rules have no quick fix, and for them
**Suppress** and **Configure** are the whole menu. Four rules carry a lightbulb —
`RTMPE1020`, `RTMPE2001`, `RTMPE2002` and `RTMPE2003` — and none of them is a
Warning-severity rule, so a list of warnings never offers one. The
[Analyzer Rule Reference](diagnostics.md) lists every rule with its severity and whether
it has a fix, and includes a one-file check that confirms the lightbulb works.

The five Warning-severity rules are `RTMPE1011`, `RTMPE1012`, `RTMPE1013`, `RTMPE1021`
and `RTMPE1030`; none of them has a quick fix.

When the rule is one of the four, the lightbulb depends on your IDE: JetBrains Rider,
Visual Studio 2022 and VS Code with **C# Dev Kit** offer the quick fixes, and VS Code
with the legacy **OmniSharp** extension does not (see
[Where each diagnostic appears](diagnostics.md#where-diagnostics-appear)). The quick fixes
run in the IDE, not in Unity's compiler, so a Unity Console notice that the
`RTMPE.SDK.CodeFixes` assembly contains no analyzers is harmless.

To switch VS Code to C# Dev Kit:

1. Install **C# Dev Kit** (`ms-dotnettools.csdevkit`) and leave the setting
   `dotnet.server.useOmnisharp` at `false` (the default).
2. In Unity, use the **Visual Studio Editor** package (`com.unity.ide.visualstudio`). It
   writes the analyzer references into the generated project files, for VS Code as well.
3. Regenerate the project files (**Edit → Preferences → External Tools → Regenerate
   project files**) and check that the reference is there:

   ```bash
   grep -c "RTMPE.SDK.CodeFixes.dll" Assembly-CSharp.csproj   # expect 1
   ```

4. Reload the window (**Developer: Reload Window**).

### Symptom: an RTMPE rule appears in the IDE but not in the Unity Console

The Unity Console shows `Error` and `Warning` rules only; `Info` rules appear in your
IDE. A rule decided from the whole solution appears in the IDE only when it analyses the
whole solution. See [Where each diagnostic appears](diagnostics.md#where-diagnostics-appear).

### Symptom: the Conversion Wizard cannot start its engine

The quick fixes are a convenience. **Window → RTMPE → Conversion Wizard** performs the
same conversions with no IDE support, through a conversion engine that runs outside
Unity.

The engine ships inside this package, under `Automation~/`, and the wizard finds it on
its own. The wizard builds the engine itself; what it needs from your machine is the .NET
8 SDK and, for the first build, network access to restore NuGet packages — until the
engine can be built there is nothing to launch, and a conversion is a manual edit. The
[Analyzer Rule Reference](diagnostics.md) shows the shape each rule expects.

- [ ] **"The conversion host was not found."** — no engine folder exists where the wizard
      looks, which means the copy under `Automation~/` is missing from this install.
      Reinstall the package, or press **Browse…** below the message and select the
      engine's `RTMPE.SDK.ConversionCli` folder.
- [ ] A folder chosen with **Browse…** is remembered per user and used ahead of the
      packaged engine for as long as it exists; the folder field is shown only while no
      engine is found. To return to the packaged engine, move or rename the folder you
      chose.
- [ ] A Unity launched from the Finder or the Dock does not inherit your shell's `PATH`.
      The wizard also looks in `DOTNET_ROOT` and in the standard .NET install locations.

[Automation](automation.md) covers the wizard, the engine and how `dotnet` is found.

---

## Testing under a bad link — the Link Simulator

The **Network Debugger** (**Window → RTMPE → Network Debugger**) has a **Link
Simulator** panel that delays, drops and reorders this client's traffic in the Editor, so
you can check prediction, smoothing and reliable delivery against a poor link.

1. Expand **Link Simulator** and tick **Simulate the link**.
2. Set **Delay (ms, one way)**, **Jitter (± ms)**, **Loss (%)** and **Allow reordering**,
   or press **Preset: 250 ms ± 50 ms, 5 % loss**.
3. Enter Play mode. The session's transport is shaped when the `NetworkManager` builds
   it; a session that is already running is shaped from its next connection attempt, and
   the panel's status line says which applies. Moving a dial affects a shaped session at
   once.

The setting belongs to this Editor and lasts until it closes. A second Editor playing the
other client is not affected, and nothing of the panel ships in a build.

| Reading | What it shows |
| --- | --- |
| **Link (out)** / **Link (in)** | Packets delivered, lost and held in each direction, plus any reordered or discarded. |
| **Reliable RTO** | The retransmission timeout for the next reliable packet (`NetworkManager.ReliableRtoSeconds`). |
| **Smoothed RTT** | The average round trip of acknowledged reliable packets (`NetworkManager.ReliableSmoothedRttSeconds`). |
| **Heartbeat RTT** | The round trip of the last answered keep-alive (`NetworkManager.LastRttMs`). |
| **Re-sent** | Reliable packets sent again (`NetworkManager.ReliableRetransmitsCount`). |
| **Given up** | Reliable packets never acknowledged (`NetworkManager.ReliableSendsDroppedCount`). |
| **RPC repeats received** | RPCs this client received twice (`NetworkManager.EnhancedRpcDuplicatesReceivedCount`). |
| **Replicas** | For each remote `NetworkTransform`: how it is drawn, its lag, its buffered samples and the frames it stood still for want of data. |

At the preset, expect **Given up** and **RPC repeats received** to stay at `0`,
**Re-sent** to climb with loss (and briefly at the start of a session, before the first
round trip is known), and each replica's frozen frames not to climb while its owner
moves.

- Reliable delivery covers the link from a client to the server. On the way in to this
  client, a message that happens once (a spawn, a despawn, an RPC, a reply) arrives as
  three copies and is lost only when all three are. A state or variable update lost on
  its way in is not sent again, so its content is missing here even when the sender's
  readings are clean.
- The simulator shapes this client's link only; the server is not simulated.
- A session on a simulated link is not recorded by the Editor's play-mode checks (see
  [Automation](automation.md)).

A test harness can compose the same transport in its own factory; pass a seed to the
`SimulatedLinkTransport` constructor for a reproducible run:

```csharp
using RTMPE.Core;
using RTMPE.Transport;

var link = new LinkConditions(delayMs: 250, jitterMs: 50, lossPercent: 5f, reorder: false);
NetworkManager.SetTransportFactory(settings =>
    new SimulatedLinkTransport(new UdpTransport(settings.serverHost, settings.serverPort), link));
```

---

## Reporting a problem

If none of the above resolves the issue, open a support ticket from your dashboard —
<https://portal.rtmpengine.com/en/dashboard/support> — and include:

1. The SDK's version line, as it appears in the log:
   `[RTMPE] SDK <version> — Unity <version>, <platform>, development build.`
2. The scripting backend (Mono or IL2CPP) and the target platform.
3. The full log from start-up to the failure, with **Enable Debug Logs** on.
4. For a connection problem, the failure line and the `OnConnectionFailed` reason.
5. What you expected, what happened, and a minimal reproduction project if possible.

## See also

- [Getting Started](getting-started.md)
- [API Reference](api/index.md)
- [Performance Tuning Guide](performance-tuning.md)
- [Analyzer Rule Reference](diagnostics.md)
- [Automation](automation.md)

---

*RTMPE SDK 1.0.8*
