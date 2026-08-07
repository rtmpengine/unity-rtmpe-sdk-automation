# RTMPE SDK for Unity

Real-Time Multiplayer Engine — Unity 2022.3 LTS or later / .NET Standard 2.1 client SDK.

> **Current version: `2.0.21`** — ⚠️ **the symmetric API-key envelope is gone.**
> `HandshakeInit` now carries the API key only as an X25519 sealed box, so
> `Api Key Seal Server Public Key Hex` is required and `Api Key Psk Hex` is no
> longer read; an init without `FLAG_SEALED_API_KEY` is refused before its payload
> is read. Protocol stays at **v4** — see the 2.0.20 note below if you are
> upgrading across it. Details in [Protocol Reference §2 and §5](Documentation~/protocol.md).
>
> **`2.0.20`** — wire-format change: rigidbody state moved to its own packet type
> (`PhysicsSync`, 0x45) and the header version byte became **4**; a gateway still
> speaking v3 rejects this client, and vice versa. Upgrade both together.

## Requirements

| Requirement      | Version                               |
| ---------------- | ------------------------------------- |
| Unity            | Unity 2022.3 LTS or later             |
| .NET Standard    | 2.1                                   |
| RTMPE Gateway    | ≥ 3.0.0                               |
| Backend protocol | v4 (MAGIC 0x5254)                     |

**Supported platforms:** Windows, macOS, Linux, Android, iOS. WebGL is
supported via a user-provided WebSocket transport (see
[Architecture §3](Documentation~/architecture.md#3-transport-layer)) — the
default `UdpTransport` cannot run inside the browser sandbox.

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

The SDK does not connect to `127.0.0.1:7777` by default — every
`NetworkManager` requires a `NetworkSettings` asset that names the gateway
host, port, and sealed-box key.  Without it, `Connect()` runs on the loopback
defaults under Strict pinning with no pin, so the first call fails with a
logged reason and an `OnConnectionFailed` callback — a key or pin error or a
connection timeout, not a silent hang.  Create the asset first, then wire
it up:

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
5. **Connect from code:**

```csharp
using RTMPE.Core;

NetworkManager.Instance.Connect("your-api-key");

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
```

> **Tip:** **Window → RTMPE → Setup Wizard** walks through every step
> above — including creating and binding the `NetworkSettings` asset —
> and stores the API key in the OS credential vault for you.

Full walkthrough — including reconnect, late-join snapshots, and object
pooling — in the [Getting Started guide](Documentation~/getting-started.md).

## Samples

Import samples from **Window → Package Manager → RTMPE SDK → Samples**:

| Sample          | Description                                       |
| --------------- | ------------------------------------------------- |
| BasicConnection | Minimal connect / disconnect loop (UPM-importable via the Package Manager → Samples panel) |

Two additional, **larger demos** — `BasicMovement` and `SimpleFPS` —
live under the repository's `Samples/` folder, outside the UPM package
(so they are *not* listed in the Package Manager Samples panel). Each is
a set of scripts plus a scene rather than a self-contained Unity project:
copy its `Scripts/` and `Scenes/` into a project that already has the SDK
installed.

## Documentation

Full documentation lives in [`Documentation~/index.md`](Documentation~/index.md).

- [Getting Started](Documentation~/getting-started.md)
- [Architecture](Documentation~/architecture.md)
- [Protocol Reference](Documentation~/protocol.md)
- [API Reference](Documentation~/api/index.md)
- [Performance Tuning](Documentation~/performance-tuning.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [Analyzer Rule Reference](Documentation~/diagnostics.md)

## License

MIT — see [LICENSE](https://github.com/rtmpengine/unity-rtmpe-sdk-automation/blob/main/LICENSE.md).
