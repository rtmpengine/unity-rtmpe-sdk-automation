# Performance Tuning Guide

> SDK Version: `com.rtmpe.sdk 1.0.5`

How to configure the SDK for different game types and target devices.

Reference measurements are taken on a desktop PC (Intel i7-10700K, Unity 6000.0 LTS,
Mono backend, 30 Hz tick rate, 10 `NetworkVariable` instances per object).

---

## Tick rate selection

**Rooms broadcast at a fixed 30 Hz.** The rate is a property of the
Synchronization Service, not a per-room option: `CreateRoomRequest.tick_rate` is
reserved and unread, and no API changes it.

`NetworkSettings.tickRate` is therefore **this client's own cadence** — how often
its tick cursor advances, network variables flush, and a changed transform may be
broadcast. It does not command the server, and the receive path converts server
ticks with the server's rate whatever this is set to. Lowering it reduces client
CPU and uplink bandwidth; it does not change how often the server sends.

| Game type                    | Client cadence  | Rationale                               |
|------------------------------|-----------------|-----------------------------------------|
| FPS / action                 | 30 Hz (default) | One client sample per server tick       |
| Platformer / MOBA            | 20 Hz           | Good balance of responsiveness and load |
| MMO / many rooms             | 10 Hz           | Scales to large room counts             |
| Turn-based                   | 1–2 Hz          | Sync only on turn change                |
| Mobile (battery-sensitive)   | 10–15 Hz        | Reduces CPU and radio wake cycles       |

Two costs to weigh before moving off 30:

- **Below 30 the client under-samples its own motion.** Peers see this player at
  the lower rate no matter how often the server broadcasts, because the server
  can only relay the samples it was sent.
- **Above 30 the surplus is discarded, and can cost more than it buys.** The tick
  engine keeps only the latest sample per tick, and the extra traffic counts
  against the per-session state budget; past that budget the server sheds frames,
  which surfaces as jitter rather than as smoother motion.

⚠️ If you enable `NetworkTransformInterpolator.OwnerTickTimeline`, every
participant must run the **same** cadence. That timeline replays a remote object
on the owning client's tick, and no wire field carries the owner's rate — the
receiver assumes its own. Mixing cadences (a 15 Hz mobile build against a 30 Hz
desktop build) reconstructs the remote timeline at the wrong rate.

Set the value on the `RTMPESettings` asset **before** calling `Connect`. Changing
it mid-session has no effect until the next reconnect.

---

## Network variable budget

Each `NetworkVariable<T>` adds to the per-tick payload. Measured wire sizes
per variable (value-only; the outer `VariableUpdate` packet adds 8 bytes for
`object_id`, 4 bytes for `tick`, 1 byte for `var_count`, and 6 bytes of
`[var_id][value_len]` per entry):

| Type                         | Value bytes          |
|------------------------------|----------------------|
| `NetworkVariableInt`         | 4                    |
| `NetworkVariableFloat`       | 4                    |
| `NetworkVariableBool`        | 1                    |
| `NetworkVariableVector2`     | 8                    |
| `NetworkVariableVector2Int`  | 8                    |
| `NetworkVariableVector3`     | 12                   |
| `NetworkVariableQuaternion`  | 16                   |
| `NetworkVariableString`      | 2 + UTF-8 byte count |

**Target budget:** ≤ 50 KB/s per player at 30 Hz.

At 30 Hz with a 50 KB/s budget and an average of 10 wire bytes per variable
(a 4-byte value plus the 6-byte entry frame):

```
50_000 B/s ÷ 30 ticks/s ÷ 10 B/var ≈ 166 variables / tick / player
```

If you exceed this, consider:

- Using `NetworkTransform` instead of raw `NetworkVariable<Vector3>` — it
  applies delta compression and suppresses sub-threshold moves.
- Splitting high-frequency objects (position) from low-frequency ones
  (inventory, stats) and ticking them at different rates.
- Enabling LZ4 compression implicitly — the SDK calls
  `Lz4Compressor.CompressIfBeneficial` on every outbound payload and sets
  `FLAG_COMPRESSED` only when the compressed form is smaller. No
  configuration is required.

### Late-join snapshot cost

When another player joins the current room, `SpawnManager.MarkAllVariablesDirtyForResync`
re-flags every tracked `NetworkVariable` on every owned, spawned object. The
next 30 Hz flush transmits one extra `VariableUpdate` per owned object
carrying **every** tracked variable (not just the ones that changed recently).
Budget ~8 KB extra one-shot per join if you have ~64 tracked variables; at
30 Hz this is a single-frame blip, not a sustained cost.

### Periodic list full-sync

A `NetworkVariableList` ships steady-state edits as a delta against the contents
the receiver is assumed to hold. The log is not idempotent and delivery is
best-effort whenever the ARQ extension is not negotiated, so a dropped datagram
would otherwise part owner and replica for the rest of the session — nothing in
the list stream carries a sequence number that would notice.

`NetworkSettings.networkVariableListFullSyncIntervalSeconds` (default **5 s**)
is the refresh that repairs it. Each interval, every list that has gone quiet
sends one snapshot of its whole contents:

```
op_count(1) + op(1) + count(2) + 50 × 4      = 204 B   value
                        + [var_id:4][value_len:2] =   6 B   entry frame
                                                   -------
                                                     210 B  ÷ 5 s  = 42 B/s

+ VariableUpdate header (object_id 8, tick 4, var_count 1) = 223 B payload
+ RTMPE header, sub-header and AEAD tag                    ≈ 268 B on the wire
```

Twenty such lists on twenty objects are twenty datagrams — ≈1.1 KB/s against the
50 KB/s budget above; on one object they share a payload and cost ≈850 B/s. Set
the interval to `0` to switch the refresh off — appropriate only where the
transport is genuinely reliable, because a lost delta is then permanent.

**They do not all land on the same frame.** A player joining re-flags every
variable of every owned object at once, so without help every list's clock would
start on that frame and every refresh after it would land together. The first
refresh of each list is therefore offset by a deterministic slice of one interval,
mixed from the object and variable ids; every later one is exactly `interval`
after its own predecessor. (The flush's `_flushStartIndex` rotation is a different
mechanism — it reorders variables *within* one payload and staggers nothing
between objects.)

Two things the refresh does **not** do:

- A list that is being written every tick is never quiet, so it costs nothing
  extra.
- **A list whose snapshot does not fit one datagram is never refreshed, and
  nothing says so.** Arming one would queue a full-sync the flush cannot send,
  and the delta log — which is working — would sit behind it until a flush
  succeeded, so the refresh declines and stays silent. For most element types
  that threshold arrives well before the 1024-element
  `maxNetworkVariableListSize` ceiling (~280 `int`s). ⚠️ Such a list is still
  replicating by deltas and a lost one is still permanent for it: if that matters,
  split it across several variables. A console line about it appears only once
  something else marks the list dirty — a player joining, or a burst of edits past
  `FullSyncOpThreshold`.

⚠️ **Receivers see a `FullSync` change event each interval.** `OnListChanged`
raises `NetworkListChangeKind.FullSync` on every applied snapshot without
comparing contents, so a widget that rebuilds on that kind now rebuilds every
interval rather than only at late-join. React to the delta kinds, or compare
before rebuilding.

---

## Position and rotation thresholds

`NetworkTransform` suppresses updates that fall below a movement threshold,
saving bandwidth for stationary objects.

| Property             | Default threshold | Description                               |
|----------------------|-------------------|-------------------------------------------|
| `PositionThreshold`  | 0.01 m            | Sub-centimetre moves are suppressed       |
| `RotationThreshold`  | 0.1°              | Micro-rotations are suppressed            |

Configure these thresholds in the **Inspector** on the `NetworkTransform`
component. Increasing thresholds reduces bandwidth at the cost of visible
snapping for fast-moving objects.

---

## Memory allocation profile

### Hot-path allocations (30 Hz, 10 variables, Mono)

| Source                                                  | Allocations / tick | GC pressure |
|---------------------------------------------------------|--------------------|-------------|
| `NetworkVariable.SerializeWithId` — pool path (≤ 1 KiB) | 0                  | 0           |
| `NetworkBehaviour.FlushDirtyVariables` (growable stream fallback, rare) | 1  | ~256 B      |
| Inbound receive + cross-thread handoff (`TryReceive` → dispatcher) | 0      | pool-rented from `ArrayPool<byte>.Shared`, returned after `ProcessPacket`; a `new byte[len]` copy (sized to the datagram, not to the rental) occurs only for a legacy `OnPacketReceived` subscriber. On the delivery path the rental is cleared over the datagram's own length before it goes back rather than over the whole array the pool served; the error paths, which have no trustworthy length, still clear all of it |
| `PacketBuilder.Build` result array                      | 1 per send         | sized to payload |
| **Typical steady-state**                                | **~10 / tick**     | **~1–2 KiB / tick** |

Key optimisations already in place:

- `NetworkVariable.SerializeWithId` rents a pool-backed `byte[1024]` from
  `ArrayPool<byte>.Shared`. Falls back to a growable `MemoryStream` only for
  unusually large string values.
- `NetworkManager` caches the heartbeat and variable-update delegates (no
  closure allocation per frame).
- `NetworkThread` drains up to 100 packets per iteration to prevent receive
  queuing under burst load.

### Reducing further

- Do not cache the payload buffer passed to `OnDataReceived` — the SDK owns
  the buffer and reuses it immediately after your callback returns.
- Pre-size `List<T>` collections used inside network event handlers to avoid
  `List.Add` resizing.
- Install an [`INetworkObjectPool`](getting-started.md#step-12--object-pooling-optional)
  for any prefab that spawns and despawns frequently (bullets, hit FX,
  transient props). See the next section.

---

## Object pooling

Without pooling, every `Spawner.Spawn` allocates a new GameObject and every
`Spawner.Despawn` destroys one, producing GC pressure and frame-time hitches
during combat-heavy moments.

Install a pool once in `OnConnected`:

```csharp
private void OnConnected()
{
    NetworkManager.Instance.Spawner.SetObjectPool(new SimplePool());
    // …
}
```

The SDK routes every `Spawn`/`Despawn` through the pool when one is installed,
and falls back to `Object.Instantiate` / `Object.Destroy` when no pool is set.
See [Getting Started — Step 12](getting-started.md#step-12--object-pooling-optional)
for a minimal pool implementation.

### Expected impact

| Scenario                          | Without pool          | With pool          |
|-----------------------------------|-----------------------|--------------------|
| 20 bullets/sec spawn + despawn    | ~40 GC allocs/sec     | ~0 GC allocs/sec after warm-up |
| Frame-time variance under combat  | 1–3 ms spikes from GC | steady             |

The pool MUST reactivate the GameObject on `Acquire` and should deactivate it
on `Release`. `SpawnManager` defensively calls `SetActive(true)` after a pool
`Acquire` returns, so pool implementations that forget this still work.

---

## Scripting backend comparison

| Backend | Build time | Runtime CPU       | Memory    | Recommendation |
|---------|------------|-------------------|-----------|----------------|
| Mono    | Fast       | +5–10 % vs IL2CPP | +10 %     | Development only |
| IL2CPP  | Slower     | Baseline          | Baseline  | Required for iOS; recommended for Android / PC release |

### IL2CPP considerations

1. **Code stripping** — add the SDK to `link.xml` (see
   [troubleshooting.md § IL2CPP](troubleshooting.md#il2cpp-missingmethodexception-at-runtime)).
2. **Generic specialisation** — custom `NetworkVariable<T>` types need the
   `[Preserve]` attribute or a reachable instantiation site to avoid
   `ExecutionEngineException` at runtime.
3. **No `DynamicMethod`** — the SDK does not use `DynamicMethod`; full AOT
   compatibility is maintained.

---

## CPU budget

At 30 Hz each tick has 33.3 ms. Typical SDK CPU cost on the reference device:

| Operation                                    | Cost      |
|----------------------------------------------|-----------|
| Packet parsing (per packet)                  | ~0.2 ms   |
| `NetworkVariable` serialisation (10 vars)    | ~0.5 ms   |
| ChaCha20-Poly1305 encryption (1 KB payload)  | ~0.3 ms   |
| LZ4 compression (when beneficial, 1 KB)      | ~0.1 ms   |
| **Total at 10 variables + 2 KB/s**           | **~1 ms / tick (3 % of frame)** |

If the SDK consistently exceeds **10 % of your tick budget**, reduce the tick
rate or the number of variables per object.

Profile with the Unity Profiler deep-profile mode; filter by the
`RTMPE.SDK.Runtime` assembly to isolate SDK cost.

---

## Transport selection

The default `UdpTransport` is correct for every supported platform (Windows,
macOS, Linux, Android, iOS). `NetworkManager.SetTransportFactory` replaces it
where a project needs a deterministic loopback for integration tests, or must
reach the gateway some other way. ⚠️ A transport is only half of "some other
way": the stock gateway deployment speaks UDP and KCP, so a WebSocket client
also needs a server-side endpoint that speaks it — see the corporate-NAT row in
[Troubleshooting](troubleshooting.md).

```csharp
NetworkManager.SetTransportFactory(settings => new MyTransport(settings));
NetworkManager.Instance.Connect(apiKey);
```

WebGL is not a supported platform, and the factory does not change that — see
[Troubleshooting](troubleshooting.md#webgl-is-not-a-supported-platform).

See [Architecture §3](architecture.md#3-transport-layer) for the complete
transport contract.

---

## Mobile-specific tuning

| Setting              | Default       | Mobile recommendation |
|----------------------|---------------|-----------------------|
| `tickRate`           | 30 Hz         | 10–15 Hz              |
| `heartbeatIntervalMs`| 5 000 ms      | 5 000 ms (do not increase above 15 000; 3 missed heartbeats = timeout) |
| `connectionTimeoutMs`| 10 000 ms     | 15 000 ms (weaker radio links)                                  |
| Background behaviour | Active        | Leave the session alone on `OnApplicationPause(true)` and call `Reconnect()` on resume. ⛔ Never `Disconnect()` first: it is logout and clears the reconnect token, so the `Reconnect()` after it returns `false` |

Halving the tick rate from 30 to 15 Hz reduces both CPU usage and radio
wake cycles by approximately 50 %, which has a measurable impact on battery
life during extended play sessions.

---

## Related documentation

- [Architecture](architecture.md) — where allocations happen in the call stack
- [API Reference](api/index.md) — `NetworkSettings` field reference
- [Troubleshooting](troubleshooting.md) — GC spike and CPU spike diagnostics

---

*RTMPE SDK 1.0.5*
