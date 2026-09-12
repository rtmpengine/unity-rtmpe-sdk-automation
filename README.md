# RTMPE SDK for Unity

Real-Time Multiplayer Engine — Unity 2022.3 LTS or later / .NET Standard 2.1 client SDK.

> **Current version: `1.0.5`** — a player states its own build type in its first
> log line, so a build that cannot connect says why. The
> entry into a game is a component, a player build resolves its own credential,
> and every sample compiles against the package that ships it.
>
> `RtmpeConnectionBootstrap` (`Component → RTMPE → Connection Bootstrap`) is the
> entry flow as a component: attach it beside your `NetworkManager` and the SDK
> connects, joins or creates a room, and reports what happened — instead of you
> writing that sequence again. It ships as an importable sample too.
>
> Remote motion now looks like it should out of the box: tick-aligned sampling,
> the owner's timeline and adaptive render delay default to on, which removes
> the placement error the server's own cadence imprints on other players'
> movement. ⛔ A default governs components created from this version onward —
> Unity serialises these fields, so prefabs and scenes authored earlier keep
> what they were saved with.
>
> Recovery now authenticates again rather than only saying so, a seat the server
> reclaims reaches the player it was taken from, and the Editor's readiness
> report separates what it read from a project from what a real run proved.
>
> ⛔ This package is not MIT. The terms are in [LICENSE.md](LICENSE.md), and
> they are not the terms any copy obtained before 2026-09-04 was published
> under. See [CHANGELOG.md](CHANGELOG.md).
>
> **The SDK is no longer MIT-licensed.** [`LICENSE.md`](LICENSE.md) is a
> limited, service-linked licence. You may install it, read and modify its
> source for your own integration, and ship it compiled inside an application
> that connects to the RTMPE service. You may not use it to implement, host or
> operate a server that speaks the RTMPE wire protocol, and you may not
> redistribute the SDK itself separately from an application of yours. The
> rights continue while your RTMPE account is in good standing.
>
> Copies obtained **between 2026-05-22 and 2026-09-03** were published under
> MIT and **remain** MIT-licensed on their own terms — a licence already granted
> is not withdrawn. ⚠️ That boundary is a **date, not a version number**: this
> release's number says nothing about which terms govern a copy somebody already
> holds, and `LICENSE.md` §7 is the authority on both.
>
> The protocol reference no longer ships with the package. Google FlatBuffers
> under `Runtime/Infrastructure/Serialization/FlatBuffers/` keeps its Apache 2.0
> licence, unchanged. Read [`LICENSE.md`](LICENSE.md) and the
> [changelog](CHANGELOG.md) before upgrading.

## Requirements

| Requirement      | Version                               |
| ---------------- | ------------------------------------- |
| Unity            | Unity 2022.3 LTS or later             |
| .NET Standard    | 2.1                                   |
| RTMPE Gateway    | ≥ 3.0.0                               |
| Backend protocol | v5                                    |

**Supported platforms:** Windows, macOS, Linux, Android, iOS. WebGL is **not**
supported: the SDK drives its transport from a dedicated background thread,
which the browser player does not provide. A transport installed through
`SetTransportFactory` replaces the socket beneath that thread and not the thread
itself, so it does not close the gap — see
[Troubleshooting](Documentation~/troubleshooting.md#webgl-is-not-a-supported-platform).

## Installation (UPM)

1. Open **Window → Package Manager**.
2. Click **+** → **Add package from git URL…**
3. Paste:
   ```
   https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git
   ```

Or add manually to your project's `Packages/manifest.json`:

```json
"com.rtmpe.sdk": "https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git"
```

## Quick Start

**Dashboard → Unity → Setup Wizard → connect.** The flow starts outside Unity:
the API key and the three connection values the SDK needs are issued by a
project in the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard),
and the SDK reaches no gateway without them.

### 1. Create a project in the dashboard

Sign in to the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard),
create a project, and take four values from it — an **API key** from the project's
**API Keys** panel (copy it at once: a key is shown only when it is created) and
three from **Connection settings**. The wizard in step 2 asks for all four:

| From the dashboard | What it is |
| ------------------ | ---------- |
| **API key** | identifies the project; it is the one secret of the four |
| **Server Host** / **Server Port** | which gateway to reach — the port is the gateway's UDP port, `7777` by default |
| **Sealed-Box Public Key (X25519)** | the key the API key is sealed to — the gateway accepts no other envelope |
| **Pinned Server Public Key (Ed25519)** | the gateway identity Strict pinning (the default) compares against |

### 2. Run the Setup Wizard

**Window → RTMPE → Setup Wizard** is the supported path through everything
below, and it opens by itself once per Editor session, on the first script load
that is not entering Play mode. **Window → RTMPE → Auto-Open Setup Wizard**
turns that off for this project and keeps it off across restarts; **Window →
RTMPE → Setup Wizard** reopens it at any time.

Its six steps verify the SDK assemblies, offer to add a `NetworkManager` to the
open scene, and take the four values from step 1. On **Finish** it creates
`Assets/RTMPE/NetworkSettings.asset` (or reuses the one the project already
has), writes the connection settings onto it, and hands the API key to the OS
credential vault — which is step 3 below, done for you. The key is never
written into a scene, a prefab, or the settings asset.

> ⚠️ **Check the scene before you call it done.** Adding the `NetworkManager` is
> a button on the wizard's second step, not something **Finish** requires, and
> the asset is bound only to a manager the wizard itself added — one that was
> already in the scene keeps whatever `Settings` reference it had. An unbound or
> absent manager is the same first-run dead end either way: `Instance` is `null`,
> or it falls back to loopback defaults.

### 3. Or configure the project by hand

The SDK does not connect to `127.0.0.1:7777` by default — every
`NetworkManager` requires a `NetworkSettings` asset that names the gateway
host, port, and sealed-box key.  Without it, `Connect()` runs on the loopback
defaults under Strict pinning with no pin, so the first call fails with a
logged reason and an `OnConnectionFailed` callback — a key or pin error or a
connection timeout, not a silent hang.  The wizard does all four of the steps
below; to do them yourself instead:

1. **Create the settings asset.** In the **Project** panel, right-click an
   `Assets/` folder and choose **Create → RTMPE → Settings**.
   Name the result (for example `RTMPESettings_Dev.asset`).
2. **Configure the asset.** Select it and fill in the Inspector fields.
   Copy `Server Host`, `Server Port`, `Pinned Server Public Key Hex` and
   `Api Key Seal Server Public Key Hex` from the RTMPE developer dashboard.  The
   sealed-box key is required — it is what the API key is sealed to, and the
   gateway accepts no other envelope.  See the [Getting Started
   guide §2](Documentation~/getting-started.md#step-2--create-the-networksettings-asset)
   for the full field reference.
3. **Add the NetworkManager.** Create an empty GameObject in your boot
   scene, name it `[RTMPE] NetworkManager`, and add the `NetworkManager`
   component (**Component → RTMPE → NetworkManager**).
4. **Bind the asset.** Drag the `RTMPESettings_Dev.asset` you created in
   step 1 onto the `Settings` field of the NetworkManager Inspector.

### 4. Connect from code

With the asset bound and the key in the vault, resolve the key through
`RTMPE.Core.ApiKeySource` and hand it to `Connect` — the vault the wizard
wrote is the first source it consults in the Editor:

```csharp
using RTMPE.Core;
using UnityEngine;

NetworkManager.Instance.OnConnected += () =>
{
    // Prefab registrations persist across reconnects; an object pool (if used)
    // is rebuilt on every Connect()/Reconnect(), so install it here.
    NetworkManager.Instance.Spawner.RegisterPrefab(prefabId: 1, prefab: playerPrefab);
    NetworkManager.Instance.Rooms.CreateRoom(new RTMPE.Rooms.CreateRoomOptions
    {
        Name       = "My Room",
        MaxPlayers = 4,
        IsPublic   = true,
    });
};

if (ApiKeySource.TryResolve(out string apiKey))
    NetworkManager.Instance.Connect(apiKey);
else
    Debug.LogError(Application.isEditor
        ? "[RTMPE] No API key — run Window → RTMPE → Setup Wizard."
        : "[RTMPE] No API key. A build carries no Editor vault, so supply one: "
          + "ApiKeySource.SetProvider, --rtmpe-api-key-file <path>, or RTMPE_API_KEY.");
```

The Editor reads the key the setup wizard stored; **no build carries that vault**,
so a player build needs a source of its own and a shipped game can use only
`ApiKeySource.SetProvider` — see
[Giving a player build its API key](Documentation~/getting-started.md#giving-a-player-build-its-api-key).

Full walkthrough — including reconnect, late-join snapshots, and object
pooling — in the [Getting Started guide](Documentation~/getting-started.md).

## Two components that write the boilerplate for you

| Component | What you stop writing |
| --- | --- |
| `RtmpeConnectionBootstrap` | The entry flow: resolve a key, connect, enter a room, spawn the local player, and re-enter that room after a drop. |
| `RtmpeSceneLoader` | Scene loading on the room's instruction, and reporting back when the load finishes. |

Both are optional by attachment: every event they subscribe to stays public, so a
project that wants to drive either flow itself simply does not add them. See
[Connecting, without writing any](Documentation~/api/index.md#connecting-without-writing-any).

## Samples

Import samples from **Window → Package Manager → RTMPE SDK → Samples**:

| Sample | Description |
| --- | --- |
| Basic Connection | The minimal connect / disconnect loop, with the live state machine on screen. |
| Player Spawn Flow | Connect, open a room and spawn the local player. The prefab id is resolved from the generated registry rather than typed into a field. |
| Scene Transitions | A room moving between two scenes with no scene-loading code: `RtmpeSceneLoader` does the loading and the reporting. |
| Two Player Room | Two clients in one room, moving. Matchmaking opens the room so no room id is typed, a component supplies the API key a built player cannot get from the Editor, and the avatar requires **both** motion components rather than trusting a prefab to carry them. |

One **larger demo** — `SimpleFPS`, an RPC damage chain — lives under the
repository's `Samples/` folder, outside the UPM package, and so is *not* listed
in the Samples panel. It is a set of scripts plus a scene rather than a
self-contained Unity project: copy its `Scripts/` and `Scenes/` into a project
that already has the SDK installed.

## Documentation

Full documentation lives in [`Documentation~/index.md`](Documentation~/index.md).

- [Getting Started](Documentation~/getting-started.md)
- [Automation](Documentation~/automation.md) — scoring a project, and the convert → re-score loop
- [Architecture](Documentation~/architecture.md)
- [API Reference](Documentation~/api/index.md)
- [Performance Tuning](Documentation~/performance-tuning.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [Analyzer Rule Reference](Documentation~/diagnostics.md)

## License

Proprietary and service-linked — see [`LICENSE.md`](LICENSE.md) **in this
package**. The terms that govern a copy are the ones shipped inside it, not a
file on a branch that moves; that is why neither this line nor the manifest
points at one. Use of this SDK is tied to an RTMPE account, and implementing,
hosting or operating a server that speaks the RTMPE wire protocol is not
permitted.
