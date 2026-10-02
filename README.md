# RTMPE SDK for Unity

The RTMPE SDK connects a Unity game to the RTMPE real-time multiplayer service. Players
meet in rooms, spawn networked objects and see each other's movement and state as it
changes. The package includes a Setup Wizard, drop-in components for the common flows,
Roslyn analyzers, and an automated conversion path for existing single-player code.

> **Current version: `1.0.8`** — see the [changelog](CHANGELOG.md).

## Requirements

| Requirement | Version |
| --- | --- |
| Unity | 2022.3 LTS or later |
| .NET Standard | 2.1 |
| Backend protocol | v5 |

**Platforms:** Windows, macOS, Linux, Android and iOS. WebGL is not supported: the SDK
runs its transport on a dedicated background thread, which the browser player does not
provide — see [Troubleshooting](Documentation~/troubleshooting.md#webgl-is-not-a-supported-platform).

## Installation

**Package archive (recommended).** Sign in to the RTMPE Developer Portal and download
`com.rtmpe.sdk-<version>.tgz` from the Download section of the
[SDK documentation](https://portal.rtmpengine.com/en/docs). Put it in your project's
`Packages/` folder and add it with **Window → Package Manager → + → Add package from
tarball…**.

**Git URL.** In **Window → Package Manager**, choose **+ → Add package from git URL…** and
enter:

```text
https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git
```

Or add it to `Packages/manifest.json`:

```json
"com.rtmpe.sdk": "https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git"
```

Upgrading is covered in [Getting Started](Documentation~/getting-started.md#step-1--install-the-sdk).

## Quick Start

The order is **dashboard → Setup Wizard → your code**.

### 1. Create a project in the dashboard

Sign in to the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard), create a
project and copy four values from it:

| From the dashboard | What it is |
| --- | --- |
| **API key** (API Keys panel) | Identifies your project. It is shown only when created, so copy it immediately. The only secret of the four. |
| **Server Host** / **Server Port** | The server to connect to. The port is the server's UDP port, `7777` by default. |
| **Sealed-Box Public Key (X25519)** | The public key the API key is sealed to before it is sent. |
| **Pinned Server Public Key** | The server identity that Strict pinning (the default) checks. |

### 2. Run the Setup Wizard

**Window → RTMPE → Setup Wizard** takes those four values and configures the project. It
opens by itself once per Editor session; **Window → RTMPE → Auto-Open Setup Wizard**
turns that off. On **Finish** it creates `Assets/RTMPE/NetworkSettings.asset` (or reuses
the project's existing settings asset), writes the connection settings to it and stores
the API key in the OS credential vault. The key is never written to a scene, a prefab or
the settings asset.

> **Important:** the wizard adds a `NetworkManager` to the scene only when you press the
> button on its second step, and it binds the settings asset only to a `NetworkManager`
> it added. Check that the scene has one with its **Settings** field assigned.

### 3. Or configure the project by hand

1. In the **Project** window, right-click a folder and choose **Create → RTMPE → Settings**
   (for example `RTMPESettings_Dev.asset`).
2. Copy **Server Host**, **Server Port**, **Pinned Server Public Key Hex** and
   **Api Key Seal Server Public Key Hex** from the dashboard into the asset — see
   [Getting Started, Step 2](Documentation~/getting-started.md#step-2--create-the-networksettings-asset).
3. Add a GameObject named `[RTMPE] NetworkManager` to your boot scene with
   **Component → RTMPE → NetworkManager**, and assign the asset to its **Settings** field.

Without a settings asset the `NetworkManager` uses loopback defaults, and the first
`Connect()` fails with a logged reason and an `OnConnectionFailed` callback —
not a silent hang.

### 4. Connect

The quickest route needs no code: add **Component → RTMPE → Connection Bootstrap** next
to the `NetworkManager`, assign your player prefab, and the SDK connects, enters a room,
spawns the local player and re-enters the room after a drop. See
[Connecting, without writing any](Documentation~/api/index.md#connecting-without-writing-any).

To drive the flow yourself, resolve the API key through `ApiKeySource` and connect:

```csharp
using RTMPE.Core;
using RTMPE.Rooms;
using UnityEngine;

public class QuickStart : MonoBehaviour
{
    [SerializeField] private GameObject _playerPrefab;
    private const uint PlayerPrefabId = 1;

    private void Start()
    {
        var net = NetworkManager.Instance;
        net.Spawner.RegisterPrefab(PlayerPrefabId, _playerPrefab);
        net.OnStateChanged     += HandleStateChanged;
        net.Rooms.OnRoomJoined += HandleRoomJoined;

        if (ApiKeySource.TryResolve(out string apiKey))
            net.Connect(apiKey);
        else
            Debug.LogError("[RTMPE] No API key: run the Setup Wizard in the Editor, "
                + "or register a provider with ApiKeySource.SetProvider in a build.");
    }

    // Create a room after a fresh connection.
    private void HandleStateChanged(NetworkState previous, NetworkState current)
    {
        if (previous == NetworkState.Connecting && current == NetworkState.Connected)
            NetworkManager.Instance.Rooms.CreateRoom(
                new CreateRoomOptions { Name = "My Room", MaxPlayers = 4 });
    }

    // Spawn once the player is seated in the room.
    private void HandleRoomJoined(RoomInfo room) =>
        NetworkManager.Instance.Spawner.Spawn(PlayerPrefabId, Vector3.zero, Quaternion.identity);
}
```

The player prefab carries a script that derives from `NetworkBehaviour` and a
`NetworkTransform`. In the Editor, `ApiKeySource` reads the key the Setup Wizard stored.
A build has no access to that vault: a shipped game registers a provider with
`ApiKeySource.SetProvider`, and on machines you control you can also launch with
`--rtmpe-api-key-file <path>` or set `RTMPE_API_KEY` — see
[Giving a player build its API key](Documentation~/getting-started.md#giving-a-player-build-its-api-key).

The [Getting Started guide](Documentation~/getting-started.md) continues with prefabs,
state synchronisation, room lists, reconnection and object pooling.

## Components

| Component | What it does |
| --- | --- |
| `RtmpeConnectionBootstrap` | Connects, enters a room (create, join or matchmaking), spawns the local player and re-enters the room after a drop. |
| `RtmpeSceneLoader` | Loads the scene the room asks for, in the requested mode, and reports when the load has finished. |
| `RtmpeWorldAuthority` + `RtmpeWorldSpawner` | Hold room state that no player owns; the world passes to the next host when the host leaves. |

Each is optional: every event they use is public, so you can drive the same flows in your
own code instead.

## Samples

Import them from **Window → Package Manager → RTMPE SDK → Samples**.

| Sample | What it shows |
| --- | --- |
| Basic Connection | Connecting and disconnecting, with the connection state on screen. |
| Player Spawn Flow | Connect, open a room and spawn the local player, with the prefab id taken from the generated registry. |
| Scene Transitions | A room moving between two scenes, with `RtmpeSceneLoader` doing the loading and reporting. |
| Two Player Room | Two clients in one room through matchmaking, each moving its own avatar. |
| Shared World | Room state that belongs to no player, kept on the world object across a change of host. |

## Documentation

- [Getting Started](Documentation~/getting-started.md)
- [Architecture](Documentation~/architecture.md)
- [API Reference](Documentation~/api/index.md)
- [Analyzer Rule Reference](Documentation~/diagnostics.md)
- [Automation](Documentation~/automation.md) — readiness scoring and automated conversion
- [Performance Tuning](Documentation~/performance-tuning.md)
- [Troubleshooting](Documentation~/troubleshooting.md)

## License

The SDK is proprietary and service-linked; the terms are in [`LICENSE.md`](LICENSE.md),
which ships with the package. You may use and modify the SDK to build applications that
connect to the RTMPE service and ship it compiled inside them. You may not implement,
host or operate a server that speaks the RTMPE wire protocol, or redistribute the SDK on
its own. Third-party components included in the package keep their own licences, listed
in `LICENSE.md`.
