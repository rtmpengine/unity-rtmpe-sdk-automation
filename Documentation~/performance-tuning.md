# Performance Tuning Guide

> SDK Version: `com.rtmpe.sdk 1.0.8`

How to keep the SDK's bandwidth, CPU and memory cost low, and how to measure it. Measure
before you tune: most costs come from how much state changes and how often, and the
readings below show both.

## Contents

- [Measure first](#measure-first)
- [Tick rate](#tick-rate)
- [Network variables](#network-variables)
- [Network variable lists](#network-variable-lists)
- [Transforms](#transforms)
- [Interest management](#interest-management)
- [Late joins](#late-joins)
- [RPCs](#rpcs)
- [Memory and garbage collection](#memory-and-garbage-collection)
- [Object pooling](#object-pooling)
- [IL2CPP and code stripping](#il2cpp-and-code-stripping)
- [Transport selection](#transport-selection)
- [Mobile](#mobile)

---

## Measure first

Open **Window → RTMPE → Network Debugger** during Play. Its sections show what the
session is doing while it runs:

| Section | What to read |
| --- | --- |
| **Traffic (rolling 1 s)** | Packets and bytes per second in each direction, totals, and the outbound **Send Queue**: packets queued, packets dropped because the queue was full, and send-buffer stalls. |
| **Diagnostics** | **Last RTT** and **Server Backpressure** — this session's sending allowance on the server: `0` while it is full, rising towards `255` as this client uses it up. Near `255`, packets from this client may be dropped. |
| **Network Variables** | Every spawned object's variables: whether each is waiting to be sent, its send rate, and how long ago it was last sent. |
| **Thread Health** | How the background network thread is keeping up. |

In a build, read the same values from `NetworkManager.Instance`:

| Property | What it tells you |
| --- | --- |
| `PacketsOutCounter`, `BytesOutCounter`, `PacketsInCounter`, `BytesInCounter` | Totals since start-up. Sample twice and divide by the interval for a rate. |
| `ServerBackpressure` | As above. Send less as it rises. |
| `SendQueueDroppedCount` | Packets dropped because this client produced them faster than it could send them. |
| `ReplicationFlushesDeferredCount` | Variable sends postponed to a later tick. A value that keeps growing means variables are sent less often than asked. |
| `LastRttMs` | The round trip of the last answered keep-alive, in milliseconds. |

For CPU, use the Unity Profiler's CPU view. The SDK's main-thread work appears under the
per-frame methods of its components:

- `Update` of `NetworkManager` — keep-alives, variable sends and your `OnFixedTick`
  overrides — and of `MainThreadDispatcher`, which handles received packets and runs most
  of your event handlers and RPC methods;
- `Update` of every `NetworkTransform` and `NetworkTransformInterpolator`, and of an
  `InterestManager`;
- `Update` of the drop-in components you use — `RtmpeConnectionBootstrap`,
  `RtmpeSceneLoader`, `RtmpeWorldAuthority`, `RtmpeWorldSpawner`;
- `FixedUpdate` of every `NetworkRigidbody` and `NetworkRigidbody2D`.

Events that a connection timeout or a reconnect attempt raises come from coroutines. Turn
**Enable Debug Logs** off while you measure: verbose logging adds its own cost.

---

## Tick rate

**Tick Rate** on the `NetworkSettings` asset (30 by default) is this client's cadence:
how often `OnFixedTick` runs, changed network variables are sent, and a moved
`NetworkTransform` is broadcast. It does not change how often the server sends room state
to you; the server sends at a fixed 30 Hz.

- **Every client in a room should use the same tick rate.** Remote motion is replayed on
  its owner's timeline (**Owner Tick Timeline** on `NetworkTransformInterpolator`, on by
  default), and a receiver counts the owner's ticks at its own rate. A client with a
  different rate reproduces remote motion at the wrong speed. Do not give one platform —
  a mobile build, for example — a lower rate than the others in the same room. See
  [Owner-tick timeline](api/index.md#owner-tick-timeline).
- **Set it before the `NetworkManager` starts.** The value is read once, when the
  `NetworkManager` wakes; changing the asset during a session has no effect.
- **Lower for the whole game, not for one client.** A slower game can lower the rate for
  every client, which cuts upload and CPU at the cost of coarser motion samples.
- **Do not raise it above 30.** The server keeps at most one sample per tick of its own,
  so faster sending adds upload without smoother motion.
- **Keep the frame rate at or above the tick rate.** Ticks run from `Update`. When a frame
  takes longer than a tick, the SDK runs several ticks in one frame to catch up, and after
  a long hitch it drops the ticks it cannot catch up.

---

## Network variables

A `NetworkVariable` is sent only when its value changes, at most once per tick. What you
pay for is the number of variables that change each tick and the size of their values;
[NetworkVariable types](api/index.md#networkvariable-types) lists the size of each type.

**Limit the send rate of values that change often but matter little.** Set `SendRateHz`
on the variable right after constructing it. Changes made between sends are merged, and
the latest value is sent at the next opportunity. `0`, the default, sends at the tick
rate, and a rate above the tick rate has no further effect.

```csharp
using RTMPE.Core;
using RTMPE.Sync;

public sealed class PlayerStats : NetworkBehaviour
{
    private NetworkVariableInt _health;
    private NetworkVariableInt _killStreak;

    protected override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _health = new NetworkVariableInt(this, nameof(_health), 100);
        _health.SendRateHz = 10f;       // at most ten sends a second

        _killStreak = new NetworkVariableInt(this, nameof(_killStreak));
        _killStreak.SendRateHz = 2f;
    }
}
```

The **Network Variables** section of the Network Debugger shows each variable's rate.

**Keep each component's per-tick changes small.** All of a component's changed variables
for one tick travel in one packet. When they do not fit, the rest wait for a later tick,
so a component with a lot of frequently changing state is updated less often than you
asked. Split such state across components or objects, or change less of it per tick.

**Batch many small updates.** With **Enable Variable Batching** on (it is off by
default), the changed variables of all the objects you own are packed into as few packets
as possible each tick, instead of one packet per component. It helps when many objects
each change a little. **Max Variables Per Batch** (32 by default, at most 64) limits the
updates in one batch.

---

## Network variable lists

A `NetworkVariableList` sends its edits at the next tick, and its whole contents when a
player joins and at each periodic refresh.

**Capacity.** A list holds at most `MaxCount` elements: the smaller of **Max Network
Variable List Size** (1,024 by default) and what one packet can carry, because a joining
player receives the whole list at once. For fixed-size elements that is 283 `int`s,
141 `Vector2Int`s or 94 `Vector3`s; for strings it depends on their length. Ask `IsFull`,
`CanAdd` or `TryAdd` rather than counting.

**Refresh.** A list that has gone quiet sends its whole contents again every **Network
Variable List Full Sync Interval Seconds** (5 by default), which repairs an edit lost on
the way. A list written every tick is not refreshed. A longer interval costs less and
repairs more slowly; `0` switches the refresh off, and a lost edit then stays lost.

Each refresh raises `OnListChanged` on receivers with `NetworkListChangeKind.FullSync`,
whether or not anything changed. Compare before rebuilding an expensive view.

---

## Transforms

**Thresholds.** A `NetworkTransform` broadcasts its pose on a tick only when it has moved
past **Position Threshold** (0.01 units) or **Rotation Threshold** (0.1°), or, with
**Sync Scale** on, **Scale Threshold**. An object that stands still sends only a
keep-alive, about once a second. Raise the thresholds on objects where small movements
do not matter, and leave **Sync Scale** off (the default) unless the scale changes.

**Quantize Transforms.** With **Quantize Transforms** on (on the `NetworkSettings` asset;
off by default), position and scale are sent at half precision and rotation in a compact
form, so each transform update is smaller. Precision is relative to the size of the
coordinate, so it falls as objects move away from the world origin, and an update with a
coordinate beyond ±65,504 units is sent at full precision. Leave it off for large worlds
and for placement that must be exact.

**Fast objects.** The owner's broadcast speed is capped by **Max Owner Velocity Meters
Per Second** (50 by default). See
[Troubleshooting](troubleshooting.md#symptom-a-fast-object-moves-more-slowly-on-other-clients)
before raising it.

---

## Interest management

In a large world, most clients do not need every object's updates. Add an
`InterestManager` to a persistent object and set its **Tracked Transform** to the local
player: the SDK reports that position, and the server then sends this client the
room-wide updates of its surroundings only.

- **Receive Filter Radius** (off at `0`) adds a filter on this client that discards
  updates for objects farther away than the radius.
- When a radius is set, **Interest Hysteresis Margin** on the `NetworkSettings` asset (1
  by default; `-1` uses each component's own **Hysteresis Margin**) keeps an object that
  lingers at the edge of the radius from flickering in and out.

See [Interest management](api/index.md#interest-management).

---

## Late joins

When a player joins, every client sends the current value of each variable it owns twice
— once straight away and once a second later — and every list's whole contents with it.
In a room where players join often, that traffic adds up. Keep per-object state small,
prefer a few short lists to many long ones, and see
[Late-join snapshot](architecture.md#8-late-join-snapshot) for the sequence.

---

## RPCs

Each `[RtmpeRpc]` call is its own reliable packet, carrying at most 1,128 bytes of
parameters. Use RPCs for events — a shot, a pickup, a chat line — and network variables
for state that changes continuously. A value sent by RPC every tick costs a packet per
call, where a variable sends only its latest value once per tick.

---

## Memory and garbage collection

- **Received packets are new arrays.** `OnDataReceived` hands you a new array for each
  packet, so you can keep it without copying; the SDK does not reuse it.
- **Sends allocate.** Each encrypted packet allocates a few short-lived arrays, so the
  number of packets you send drives the SDK's garbage. Send fewer, fuller packets: limit
  send rates, and batch variable updates.
- **Keep your handlers allocation-free.** Event handlers and RPC methods can run many
  times a second. Store delegates in fields instead of creating lambdas per call, and
  avoid LINQ and string formatting on those paths.
- **Log less in release builds.** Leave **Enable Debug Logs** off outside development;
  every message it adds is a formatted string.
- **Pool objects you spawn often** — see the next section.

---

## Object pooling

Every `Spawn` without a pool instantiates a GameObject, and every `Despawn` destroys one.
For objects spawned and despawned often — projectiles, effects — install an
`INetworkObjectPool` with `NetworkManager.Instance.Spawner.SetObjectPool(pool)`.

The spawn manager is rebuilt on every connection, and the pool does not carry over, so
install it from `OnConnected`. When an object returns to the pool, its network variables
have already been reset to their initial values. A minimal pool is in
[Getting Started](getting-started.md#step-12--object-pooling-optional).

---

## IL2CPP and code stripping

The package's `link.xml` preserves the SDK runtime, so the SDK itself needs nothing from
you. Your own `[RtmpeRpc]` methods, custom `NetworkVariable<T>` subclasses and
`INetworkSerializable` payload types need a `link.xml` of your own when code stripping is
on, and payload types should be registered with `RpcTypeRegistry.Register<T>()`. See
[Troubleshooting](troubleshooting.md#il2cpp-missingmethodexception-at-runtime).

Profile the scripting backend you ship: Mono and IL2CPP builds do not cost the same.

---

## Transport selection

The built-in `UdpTransport` serves every supported platform, and there is nothing to
choose. `NetworkManager.SetTransportFactory` installs a transport of your own — a
loopback transport for automated tests, for example. Install it before `Connect()`; a
change takes effect at the next connection attempt.

A custom transport does not add platforms: WebGL is not supported, whatever transport is
installed — see [Troubleshooting](troubleshooting.md#webgl-is-not-a-supported-platform).

To tune against a worse link than the one on your desk, shape it instead of replacing
it: the Editor's
[Link Simulator](troubleshooting.md#testing-under-a-bad-link--the-link-simulator) adds
delay, jitter and loss to the next session. [Architecture](architecture.md#3-transport-layer)
describes the transport contract.

---

## Mobile

- **Match the room's tick rate.** Use the same **Tick Rate** as every other client in the
  room (see [Tick rate](#tick-rate)). If the whole game runs on phones and tolerates
  coarser motion, lower it for everyone.
- **Keep the session through backgrounding.** Do not call `Disconnect()` when the app is
  paused; recover on resume instead, as
  [Troubleshooting](troubleshooting.md#symptom-the-session-is-gone-when-a-mobile-app-returns-from-the-background)
  shows.
- **Allow for cellular links.** Raise **Heartbeat Liveness Grace Ms** so that a brief
  stall does not end the session, and **Connection Timeout Ms** (10,000 by default) when
  first connections are slow.
- **Send less.** Every packet you avoid saves bandwidth and battery: limit send rates,
  batch variable updates, and use interest management in large worlds.

---

## See also

- [Architecture](architecture.md)
- [API Reference](api/index.md)
- [Troubleshooting](troubleshooting.md)

---

*RTMPE SDK 1.0.8*
