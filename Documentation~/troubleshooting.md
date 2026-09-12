# Troubleshooting Guide

> SDK Version: `com.rtmpe.sdk 1.0.5`

Common issues encountered when integrating the RTMPE SDK, with diagnostic
steps and fixes. Each section leads with the symptom followed by a checklist.

---

## Connection issues

### Symptom: `OnConnectionFailed` fires with "Connection timeout." or "Reconnect timeout."

The handshake did not complete within `NetworkSettings.connectionTimeoutMs`
(default 10 000 ms).

**Diagnostic checklist:**

- [ ] Is the gateway reachable? On a shell:
      `nc -u <host> 7777` (Linux/macOS) or equivalent UDP test.
- [ ] Is outbound UDP 7777 allowed through the user's firewall and router?
      RTMPE uses UDP only — TCP rules do not apply.
- [ ] Is `Settings.pinnedServerPublicKeyHex` set to the correct 64-char hex
      key for the target environment? A key mismatch causes the Ed25519
      signature check on the `Challenge` to fail; the SDK aborts the
      handshake and the timeout coroutine fires `OnConnectionFailed`
      shortly after.
- [ ] Is `Settings.apiKeySealServerPublicKeyHex` the gateway's 64-char hex
      X25519 public key from the dashboard (and **not** the Ed25519 pin)?  A
      wrong or blank value means the gateway holds no scalar for the key the box
      was sealed to, cannot open the `HandshakeInit`, and drops it silently.
- [ ] Is the client system clock within 5 minutes of UTC? JWT `nbf` / `exp`
      claims are validated server-side; a skewed clock causes silent token
      rejection after a successful handshake.

**Common causes and fixes:**

| Cause                                   | Fix |
|-----------------------------------------|-----|
| Wrong environment sealed-box key        | Verify `Settings.apiKeySealServerPublicKeyHex` is the X25519 public key of the gateway you are connecting to. |
| Wrong pinned public key                 | Verify `Settings.pinnedServerPublicKeyHex` matches the gateway's Ed25519 public key for the target environment. |
| Corporate NAT drops unsolicited UDP     | The gateway carries a WebSocket transport for exactly this case; `NetworkManager.SetTransportFactory` is the client half. Ask RTMPE to enable it for your project — the licence does not permit standing up a bridge of your own that speaks the protocol. |
| Routing probe fell back to loopback     | Check the Unity Console for `[RTMPE] UdpTransport: routing probe failed …` — common in isolated test containers. The handshake still authenticates (no address is bound into the envelope), but the datagrams do not leave the host. |
| Pin store could not be read             | In `TrustOnFirstUse`, a store that cannot reach its own state refuses rather than capturing a replacement pin — the alternative is trusting whatever key answers this flight over the pin you provisioned. Look for `[RTMPE] PlayerPrefsPinStore: the pin for endpoint … could not be read` or `[RTMPE] EncryptedFilePinStore: … exists and could not be read`. Free whatever holds the file or restore preference access; `Strict` with `pinnedServerPublicKeyHex` set does not consult the store at all. |

---

### Symptom: Connection drops after ~15–30 seconds of idle

The SDK sends a `Heartbeat` every `heartbeatIntervalMs` (default 5 000 ms).
`DisconnectReason.ConnectionLost` is raised only when **both** conditions hold:
three consecutive `HeartbeatAck` responses are missed **and** no
AEAD-authenticated `HeartbeatAck` has arrived within the liveness-grace window
(default ≈ 30 s, twice the three-miss span). The grace window forgives a brief
stall that recovers with a real ack; only a `HeartbeatAck` refreshes it (other
inbound traffic does not), so keep `heartbeatIntervalMs` low enough that the ack
cadence stays inside the window.

- [ ] Is the Unity Editor paused? The network thread continues running, but
      `MainThreadDispatcher` does not drain actions while paused — callbacks
      appear to stop.
- [ ] Is `heartbeatIntervalMs` above `15_000`? With 3-miss tolerance the
      server-side session TTL is exceeded before the next heartbeat arrives.
- [ ] Is the application going to the background on mobile? **Do not call
      `Disconnect()` on pause.** `Disconnect()` is logout: it reaches
      `ClearSessionData(preserveReconnectToken: false)` and drops the very
      token `Reconnect()` needs, so a resume that calls `Reconnect()` after it
      returns `false` and logs *"no reconnect token — client must call
      Connect(apiKey) to re-authenticate."* A connection lost while
      backgrounded is the other path and it **keeps** the token
      (`preserveReconnectToken: true`), leaving the manager in `Disconnected`
      with `DisconnectReason.ConnectionLost`. So on resume:

      ```csharp
      void OnApplicationPause(bool paused)
      {
          if (paused) return;                        // backgrounding is not a logout
          if (manager.State != NetworkState.Disconnected) return;
          if (!manager.Reconnect())                  // false when the token is gone
              manager.Connect(apiKey);               // full re-authentication
      }
      ```

      `Reconnect()` also returns `false` while a retry loop is already in
      flight, so calling it from both `OnDisconnected` and here is safe.

---

### Symptom: Reconnect loop — connects then immediately disconnects

- [ ] Inspect the `DisconnectReason` argument in `OnDisconnected`. Compare
      against the enum in [API Reference §DisconnectReason](api/index.md#disconnectreason-enum).
- [ ] If the disconnect carries `ConnectionLost` immediately after
      `OnConnected`, the gateway rejected the first encrypted packet — typical
      causes are nonce-counter mismatch between SDK and gateway, or a server
      reboot that invalidated your `cryptoId`.
- [ ] If `OnAutoRejoinAttempt` fires followed immediately by an `OnRoomError`,
      the server has evicted the room UUID. Disable
      `autoRejoinLastRoomOnReconnect` and show a room-selection UI instead.

---

### Symptom: `CanReconnect` is false after a drop

The SDK wipes the reconnect token on:

- explicit `Disconnect()`;
- a first-connect handshake timeout, where no token was ever issued;
- a `Reconnect()` that drew a validated `Challenge` and still timed out;
- server-initiated `Disconnect`.

If your use case needs guaranteed resumption after those events, you must
call `Connect(apiKey)` with fresh credentials.

---

## Authentication issues

### Symptom: Handshake succeeds but the first room call fails with "invalid token"

- [ ] Is the JWT valid? `NetworkManager.Instance.JwtToken` is a `RedactedString`
      that logs as `<redacted>`; call `.Reveal()` to obtain the raw token, then
      decode the `exp` claim (`jwt.io` or equivalent). A `401` from the Room Service
      REST API indicates expiration or a signing-key mismatch between the
      gateway and Room Service.
- [ ] Is the system clock synchronised? Token TTLs default to 5 minutes. A
      clock drift greater than 5 minutes causes `exp` rejection.
- [ ] Are you using the correct environment's token? Dev tokens are signed
      with a different HMAC key than production tokens and are rejected by
      the production Room Service.

---

### Symptom: Auth token expires during a long session

The SDK does not auto-refresh the session JWT. When a token expires the
gateway ends the session, which the SDK surfaces through `OnDisconnected` —
typically with `DisconnectReason.ServerRequest` (or `Timeout` if a subsequent
reconnect cannot complete). In your `OnDisconnected` handler, when
`CanReconnect` is `false`, re-authenticate from scratch via `Connect(apiKey)`;
the reconnect-token path does not refresh an expired auth context.

---

## State synchronisation issues

### Symptom: `NetworkVariable` values never update on remote clients

- [ ] Was the `NetworkBehaviour` registered via `SpawnManager.Spawn()`? Objects
      instantiated with Unity's `Instantiate` are not tracked by the SDK.
- [ ] Is `NetworkBehaviour.NetworkObjectId` consistent between sender and
      receiver? Log it on both sides with
      `Debug.Log($"id={obj.NetworkObjectId}")`.
- [ ] Is the `NetworkVariable` created in `OnNetworkSpawn()`? Creating it in
      `Awake()` / `Start()` leaves `IsOwner` undefined at construction time
      and the variable is not tracked by the send loop.
- [ ] Are you writing `Value` on a non-owner? ⚠️ Since **4.0.0** the setter
      **refuses** it: the write is not stored, `IsDirty` is not set, and
      `OnValueChanged` does not fire. The reason is that the flush loop skips
      any component the local player does not own, so storing the value would
      show it to this client and to nobody else, for good, while leaving
      `IsDirty` set with nothing to ever clear it. You do not need
      `if (!IsOwner) return;` — the SDK does it — but the refusal is silent to
      the caller, so a write that seems to vanish on a replica is this.
      ⛔ An object with **no owner at all** — spawned before this client holds a
      room seat — is purely local and is *not* refused.
      (Corrected 2026-08-27. This entry said the opposite: that the setter
      mutates local state and fires the event. It described an earlier build.)
- [ ] **Is the value one the variable refuses?** A non-finite
      float or Vector3 component, or a string that is not encodable as UTF-8 or
      exceeds 65535 bytes, is refused on write: the prior value is kept, no
      event fires, and `IsDirty` does not move. The refusal is warned once a
      second and names the owning component. Ask first with `CanSend(value)`, or
      write through `TrySetValue(value)` / `TryAdd(item)`, which report the
      outcome the setter swallows.

---

### Symptom: Late-joiner sees default NetworkVariable values until the owner writes to them

This should not happen: `SpawnManager.MarkAllVariablesDirtyForResync` is
auto-called on `RoomManager.OnPlayerJoined`, and the joiner receives a full
snapshot within one 30 Hz tick. If you see it anyway, the resync did not run —
check that the joiner's object was spawned through `SpawnManager` and that the
owner is still connected when the join fires.

The manual equivalent, if you need to force one, is to have the owner write a
value that **differs** from the current one on `OnPlayerJoined`. ⚠️ Re-assigning the same value does not
work: the setter compares first and returns before it can mark the variable
dirty, so `Value = Value` sends nothing. And `IsOwner` belongs to the component,
not to the variable:

```csharp
NetworkManager.Instance.Rooms.OnPlayerJoined += _ =>
{
    if (!IsOwner) return;

    int current = _health.Value;
    _health.Value = current + 1;   // a real change — this one is broadcast
    _health.Value = current;       // and back, which is broadcast too
};
```

---

### Symptom: Silent packet loss — state lags behind without error

- [ ] Is `NetworkManager.LastRttMs` consistently high or spiking?
      `> 200 ms` on a LAN indicates significant packet loss. Open the Unity
      Profiler's Network view for deeper analysis.
- [ ] Is the connection operating under heavy packet loss (> 20 %)? At this
      level even packets marked reliable observe significant latency. Consider
      reducing tick rate or switching to a closer region.

---

### Symptom: a spawned object's `IsOwner` is always `false` (the player never moves)

`IsOwner` is `true` only for an object created through the SDK's spawn system
with the local player as its owner. If `Update()` keeps logging *"not the
owner"*, the object running that code is almost certainly not the one you
spawned:

- [ ] Is the object created with `NetworkManager.Instance.Spawner.Spawn(...)`?
      A scene-placed (or `Instantiate`d) `NetworkBehaviour` is never networked,
      so its `IsOwner` is permanently `false`. The networked player must come
      from `Spawn`.
- [ ] Did `Spawn` return non-`null`? When the prefab id is not registered,
      `Spawn` logs `[SpawnManager] Spawn: prefab {id} is not registered` and
      returns `null` — assign the result and check it. Register the prefab with
      `Spawner.RegisterPrefab(id, prefab)` (it persists across reconnects).
- [ ] Is a second copy of the script already in the scene? Its `Update()` logs
      *"not owner"* forever while the spawned copy is fine. Log
      `GetInstanceID()`, `IsSpawned`, and `OwnerPlayerId` to tell them apart.
- [ ] Spawn from `OnRoomJoined` (or later): the local player id arrives with the
      room, so spawning before then leaves the object unowned.

---

## RPC issues

### Symptom: `CreateRoom` / `JoinRoom` / `JoinRoomByCode` does nothing

- [ ] **Is a field outside the rule the Room Service applies to it?** (since
      this release) The SDK now judges these before sending, so the request
      never leaves and `OnRoomCreated` / `OnRoomJoined` never fire. The refusal
      is reported on **`OnRoomError`** and logged with the prefix
      `[RTMPE] RoomManager.<method>:`. Subscribe to `OnRoomError` — a shipped
      player has no console, so that event is the only place a released build
      can see this.

      The rules, all of them the server's own and restated in
      `RTMPE.Rooms.RoomFieldLimits`:

      | field | rule |
      |---|---|
      | `CreateRoomOptions.Name` | ≤ 64 **characters**, no control or invisible formatting characters |
      | `CreateRoomOptions.MaxPlayers` | `0` (let the server choose) or `1`–`100` |
      | `JoinRoomOptions.DisplayName` | ≤ 32 **characters**, same character rule |
      | `JoinRoomByCode` code | exactly 6 characters of `ABCDEFGHJKMNPQRSTUVWXYZ23456789` |

      ⚠️ **The join code is the one that catches people.** It contains no `O`,
      `I`, `L`, `0` or `1` — those are read for one another — so a UI that
      formats a code with a separator (`ABC-234`), or a player who typed a
      space, is refused. Case does not matter: the SDK raises it for you.

      Check a value before you call with `RoomFieldLimits.ValidateRoomName`,
      `.ValidateDisplayName`, `.ValidateMaxPlayers` or `.ValidateRoomCode` —
      each returns `null` when the value is fine and a sentence naming the
      broken rule when it is not.

- [ ] **Is a lobby filter carrying something JSON cannot express?**
      `LobbyManager.ListRooms` throws `ArgumentException` for a filter with an
      empty key, or a value that is not a string, int, float, double or bool —
      a `long` is the usual one — or a `NaN` or infinity. Before this release
      those filtered on zero and returned the wrong rooms. Pre-check with
      `LobbyFilterValue.IsValid(value)`.

### Symptom: an RPC is never delivered and nothing is thrown

- [ ] **Is the payload above the sendable ceiling?** An Enhanced
      RPC carries at most **1128** parameter bytes and a legacy `SendRpc` at most
      **1137** — what is left of one UDP datagram after the packet and RPC
      headers. `RpcLimits.MaxPayloadBytes` (4096) is the bound on what may be
      RECEIVED and was advertised as the send limit until 2026-08-23; roughly
      three quarters of that range could never be framed. Read the real ceiling
      from `EnhancedRpcPacketBuilder.MaxSendablePayloadBytes` /
      `RpcPacketBuilder.MaxSendablePayloadBytes`. An oversized call is refused
      with a `Debug.LogWarning` naming the ceiling; it is not sent, and it does
      not throw into your `Update()`.
      Send bulk data as game data in chunks: an RPC is not a file transfer, and
      IP fragmentation is unreliable on mobile and CGNAT links.

### Symptom: `RPC request: malformed payload, dropped` on the receiver

The gateway must preserve the `EnhancedRpc` wire flag when it fans an
`[RtmpeRpc]` call out to peers; a build that drops it delivers your enhanced
payload to the legacy parser, which reports *malformed*. This was a server-side
defect fixed on 2026-07-13 — no SDK or code change is needed on your side. If you
still see it, the gateway you are pointed at predates that fix; report the
address and the time, and it is fixed on the service.

### Symptom: `no [RtmpeRpc] method with id 0x… on <SomeType>` — the RPC is dropped

The call reached the object but the SDK looked for the method on the wrong
component. An object routes RPCs through its **anchor** (the **first**
`NetworkBehaviour` on the GameObject), and `<SomeType>` in the message is that
anchor. The named id is hashed from the *declaring* type, so this fires when the
`[RtmpeRpc]` method lives on a different component than the anchor.

- [ ] The SDK resolves the call to whichever component declares the method, so
      this should not occur — check that the *receiver's* object carries a
      component of the same type that the sender declared the method on (both
      clients run the same prefab).
- [ ] For a global event (e.g. "start game"), the clean home is a dedicated
      networked manager object that declares the RPC, rather than a per-player
      object.
- [ ] Coming from Photon Fusion (RPCs on the specific `NetworkBehaviour`)? That
      pattern is supported. See [API → Remote Procedure Calls](api/index.md#remote-procedure-calls).

### Symptom: an `[RtmpeRpc]` call never runs, no "malformed"/"no method" log

- [ ] Is the method `public`, non-`static`, on a `NetworkBehaviour` subclass, with
      only supported parameter types (`int`, `float`, `bool`, `string`, `byte[]`,
      `ulong`, `Vector3`, `Color`, `Quaternion`, or `INetworkSerializable`)? The
      shipped Roslyn analyzers flag violations at compile time.
- [ ] Does the wire audience match the declared one? A method declared
      `[RtmpeRpc(RpcTarget.Others)]` invoked as `All` (or vice-versa) is refused;
      declare the audience you actually send. A `RpcTarget.Server` method never
      runs on a client.
- [ ] Are you in a room? RPCs are dropped before `OnRoomJoined`.

---

## Performance symptoms

### Symptom: GC spikes every 1–2 seconds

Allocations in hot paths are the most common cause. Profile with
`Profiler.GetTotalAllocatedMemoryLong()` delta per tick.

- [ ] Are you caching the payload buffer from `OnDataReceived`? The SDK
      owns that buffer and reuses it — copy only the bytes you need.
- [ ] Are you spawning and despawning objects every frame? Install an
      [`INetworkObjectPool`](getting-started.md#step-12--object-pooling-optional)
      to eliminate `Instantiate` / `Destroy` allocations.
- [ ] Are you creating closures (anonymous lambdas) inside `Update`? Cache
      them as fields, as shown in the Getting Started guide.

**Expected GC budget:** ≤ ~2 KiB / tick at 30 Hz with 10
`NetworkVariable` instances and no user-level allocations. See
[performance-tuning.md](performance-tuning.md) for details.

---

### Symptom: CPU spikes on reconnection

`NetworkManager.Reconnect()` uses the `ReconnectBackoff` (Full-Jitter capped
exponential) internally. Do not wrap `Reconnect()` in a `while` loop — it
takes care of retry cadence automatically.

---

## Unity-specific issues

### IL2CPP: `MissingMethodException` at runtime

Unity AOT code stripping removes members it cannot prove are reached from a
static call site. The SDK ships a `link.xml` that preserves its own runtime
assembly (`RTMPE.SDK.Runtime`), so the SDK's reflective RPC and variable paths
survive stripping with no action on your part.

Three kinds of member in **your** assembly need preserving, and the SDK cannot
preserve any of them for you:

1. your `[RtmpeRpc]` methods;
2. any custom `NetworkVariable<T>` closed types;
3. **any RPC payload type carrying `[RtmpeRpcSerializable]`, together with its
   public parameterless constructor.**

When the **Managed Stripping Level** is above **Low**, add a `link.xml` under
your project's `Assets/` folder that preserves them:

```xml
<linker>
    <assembly fullname="Assembly-CSharp">
        <type fullname="MyGame.PlayerController" preserve="all" />
        <type fullname="MyGame.MyCustomState" preserve="all" />
        <type fullname="MyGame.MyRpcPayload" preserve="all" />
    </assembly>
</linker>
```

Replace `Assembly-CSharp` with your gameplay assembly name if you use an
`.asmdef`. The built-in `NetworkVariable<T>` closures for `int`, `float`,
`bool`, `Vector2`, `Vector2Int`, `Vector3`, `Quaternion`, and `string` are
already preserved by the SDK's own `link.xml`.

⚠️ **The third one fails differently from the first two, and more quietly.**
A payload type found by the `[RtmpeRpcSerializable]` scan is built through the
constructor `RpcTypeRegistry` resolves at registration time; nothing calls that
constructor from a static site, so the stripper is free to remove it. When it
does, the type still resolves by name and the SDK logs

```
[RTMPE] RpcTypeRegistry.Register: 'MyGame.MyRpcPayload' has no public
parameterless constructor, so it can be resolved by name but never
instantiated — every RPC carrying it will surface that parameter as null.
```

once at startup, and every RPC carrying that parameter then arrives with it
`null`. There is no `MissingMethodException` and no per-call message: on device
the calls simply do nothing useful. `preserve="all"` on the type keeps the
constructor. A `struct` payload is unaffected — a value type has no constructor
to strip.

---

### IL2CPP: `ExecutionEngineException` on generic `NetworkVariable<T>`

The SDK pre-specialises generic paths for `int`, `float`, `bool`, `Vector2`,
`Vector2Int`, `Vector3`, `Quaternion`, and `string`. A custom `T` requires
either:

- The `[Preserve]` attribute on the type definition, **or**
- A non-stripped code path that creates at least one `NetworkVariable<T>`
  instance, forcing AOT specialisation.

---

### WebGL is not a supported platform

`Connect()` and `Reconnect()` refuse in a WebGL player, and the package raises a
compile-time warning whenever the WebGL build target is selected.

Two layers stand between the SDK and the browser, and the pluggable transport
reaches only the lower one. The browser sandbox has no raw UDP socket — which a
custom `NetworkTransport` could answer — but above that socket the SDK runs its
send and receive loop on a dedicated background thread (`NetworkThread`), and
the WebGL player provides no threads at all. Installing a transport through
`NetworkManager.SetTransportFactory` therefore does not make a WebGL build work:
it replaces the socket and leaves the thread above it exactly where it was.

Play mode keeps working in the Editor with the WebGL build target selected,
because it runs on the Editor's own player. That asymmetry is why the
compile-time warning exists — without it the first honest signal would be a
shipped build that cannot connect.

Supported platforms are Windows, macOS, Linux, Android and iOS.

---

### Mobile: excessive battery drain

- Lower `NetworkManager.Settings.tickRate` from the default `30` to `10`–`15`
  for games that do not require sub-100 ms input latency.
- Do **not** call `Disconnect()` on `OnApplicationPause(true)`. It is logout —
  it clears the reconnect token, so the `Reconnect()` on resume returns `false`
  and the player pays a full re-authentication. Leave the session alone on
  pause; a link lost while backgrounded keeps the token, and the resume handler
  under *"Symptom: Connection drops after ~30 seconds"* is the one to use.

---

### Scene transitions: stale registry entries

If you load a new scene with `SceneManager.LoadScene` without first calling
`SpawnManager.Despawn` on scene-specific networked objects, the registry
holds dead references until the next `ClearAll()` (room leave / disconnect).

The SDK subscribes to `SceneManager.sceneUnloaded` / `sceneLoaded` and calls
`NetworkObjectRegistry.PruneDestroyed()` automatically to evict those dead
references. If you want server-side cleanup of those objects, despawn them
explicitly before loading the new scene:

```csharp
foreach (var obj in NetworkManager.Instance.Spawner.Registry.GetAll())
{
    if (obj.IsOwner)
        NetworkManager.Instance.Spawner.Despawn(obj.NetworkObjectId);
}
UnityEngine.SceneManagement.SceneManager.LoadScene("NextLevel");
```

---

### Symptom: the room never settles after a scene change, and nothing says why

`OnAllPlayersSceneLoaded` needs every player to report, and a client whose
engine refused the load never does. From the room's side that is
indistinguishable from a client that is merely slow — the readiness deadline
reports a room that did not settle and names no cause, because it has none to
name.

The commonest cause is the simplest: the scene is not in **File → Build
Settings → Scenes In Build**, or it is listed there and **unticked**. An
unticked entry is not in the build, so `LoadSceneAsync` answers null for it
exactly as it does for a scene that was never added — while the row sits in the
list looking present.

**Window → RTMPE → Network Scenes** answers both at authoring time. It shows the
build list — with unticked entries, entries whose asset is gone, and two scenes
sharing one name all called out — and, on **Scan**, compares the scene names
your own scripts pass to `LoadScene` against it.

⚠️ **It reads names as written.** A call passing a literal is checked; a call
passing a variable, a field, a constant or an interpolated string is counted as
*unchecked* rather than passed over, and the window says how many of each it
found. A green result means the names it could read are in the build — not that
every scene your game loads is.

Attaching [`RtmpeSceneLoader`](api/index.md#scene-loading-without-writing-any)
covers the other half at runtime: when the engine refuses a load it says so on
that client, naming the scene and the likely cause.

---

## Authoring-tool issues (Roslyn analyzer / conversion quick fixes)

### Symptom: RTMPE diagnostics appear, but the only quick fix offered is "Suppress / Configure"

**Check the rule id first.** Most RTMPE rules have no quick fix by design, and
on those *Suppress / Configure* is the complete and correct menu. Four rules
carry a lightbulb — `RTMPE1020`, `RTMPE2001`, `RTMPE2002` and `RTMPE2003` —
and none of the four Warning-severity rules is among them, so a
session spent on warnings never sees one. The
[Analyzer Rule Reference](diagnostics.md) lists every rule with its severity and
whether it is fixable, records the conditions under which a fixable rule still
withholds its fix, and gives a paste-ready one-file check on `RTMPE1020` —
Error severity, so it cannot be missed — that confirms the whole chain in
isolation. Work through it before treating this as a host problem: it explains
the symptom far more often than anything below does.

Everything that follows applies once you have confirmed the rule you are on is
one of the four.

The diagnostic surfacing at all proves `RTMPE.SDK.Analyzers.dll` loaded — the
analyzer half of the toolchain is working. The conversion quick fixes live in a
**second** assembly, `RTMPE.SDK.CodeFixes.dll`, and that one is what raises the
lightbulb. Both ship in `Packages/com.rtmpe.sdk/Analyzers/`, both carry the
`RoslynAnalyzer` asset label, and the package is complete when all four DLLs are
present:

```
Analyzers/RTMPE.SDK.Analyzers.dll         diagnostics  (RTMPE1xxx / RTMPE2xxx)
Analyzers/RTMPE.SDK.CodeFixes.dll         quick fixes  ← the lightbulb
Analyzers/RTMPE.SDK.Transforms.dll        shared edit core
Analyzers/RTMPE.SDK.Conversion.Core.dll   id/ledger engine
```

The asymmetry is by design, and it is what makes a host-side failure possible at
all: `CodeFixes` references
`Microsoft.CodeAnalysis.CSharp.Workspaces`, the Roslyn **IDE** layer. The
analyzer does not. Unity's own `csc` has no Workspaces layer, so it loads
`Analyzers` and skips `CodeFixes` — that is expected, not an error, and any
"no analyzers in assembly" notice about `CodeFixes` in the Unity Console is
cosmetic. Supplying Workspaces is the IDE's job, so whether the lightbulb
appears depends entirely on the editor:

| Editor | Diagnostics | Conversion quick fixes |
| --- | --- | --- |
| JetBrains Rider | yes | yes |
| Visual Studio 2022 | yes | yes |
| VS Code + **C# Dev Kit** (Roslyn LSP) | yes | yes |
| VS Code + **OmniSharp** (legacy C# extension) | yes | **no** — this symptom |

If you are on VS Code and see only Suppress/Configure:

1. Install **C# Dev Kit** (`ms-dotnettools.csdevkit`) and make sure the legacy
   OmniSharp-only path is not in use — in Settings, `dotnet.server.useOmnisharp`
   must be **false** (the default). OmniSharp reports analyzer diagnostics but
   does not run `CodeFixProvider`s loaded from an `<Analyzer>` reference.
2. In Unity, use the **Visual Studio Editor** package
   (`com.unity.ide.visualstudio`) — it is what emits the `<Analyzer …/>` entries
   into the generated `.csproj`, for VS Code as well as Visual Studio. The old
   `com.unity.ide.vscode` package is deprecated and does not.
3. Regenerate the project files (**Edit → Preferences → External Tools →
   Regenerate project files**) and confirm the entries landed:

   ```bash
   grep -c "RTMPE.SDK.CodeFixes.dll" Assembly-CSharp.csproj   # expect 1
   ```

4. Reload the window (**Developer: Reload Window**) so the LSP re-reads the
   analyzer set.

### The conversion path that does not depend on any IDE

The quick fixes are a convenience layer. Every conversion they perform is
executed by the same `RTMPE.SDK.Transforms` core through the wizard, which runs
as an out-of-process CLI and needs no IDE support at all:

**Window → RTMPE → Conversion Wizard**

If the lightbulb cannot be made to appear in your editor, the wizard produces
byte-identical output — it is a fallback with no loss of capability, not a
degraded mode.

The engine it runs ships inside this package, under `Automation~/`, and the
wizard resolves that folder without being told where it is. What it needs from
your machine is the .NET 8 SDK and one build; until that has been run there is
nothing to launch, and the conversions are manual edits —
`Documentation~/diagnostics.md` records the exact shape each rule expects, per
rule, including every condition under which a quick fix is deliberately
withheld.

Two things outrank that copy. A **Browse…** path recorded in an earlier session
is consulted first and is remembered per user, so one left behind keeps the
shipped engine from ever being reached. An RTMPE repository checkout comes next:
there the engine under `clients/unity-sdk/Tooling/` is the live source and the
package's copy a publication of it, so an edit to a transform is what the next
run executes. See [Automation](automation.md), which also covers why a
Finder-launched Unity reports no `dotnet` while your terminal has one.

---

## Reporting bugs

If none of the above resolves the issue, open a support ticket from your
dashboard — <https://portal.rtmpengine.com/en/dashboard/support> — and include:

1. **The SDK's own first log line**, which states all three of the things asked
   for below and one more this page cannot: whether the build is a development
   build. It reads
   `[RTMPE] SDK <version> — Unity <version>, <platform>, development build.`
   Paste it as it appears.
2. Unity version (e.g. `6000.1.0f1`) and scripting backend (Mono / IL2CPP).
3. Target platform (Windows / macOS / iOS / Android).
4. The full `NetworkManager` log with `NetworkSettings.enableDebugLogs = true`.
5. A minimal reproduction project if possible.

---

*RTMPE SDK 1.0.5*
