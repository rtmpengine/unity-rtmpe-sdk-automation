# RTMPE SDK — Getting Started

> **SDK Version:** `com.rtmpe.sdk 1.0.8`
> **Unity:** 2022.3 LTS or later
> **Platforms:** Windows, macOS, Linux, Android, iOS. WebGL is not supported — see [Troubleshooting](troubleshooting.md#webgl-is-not-a-supported-platform).

This guide takes a Unity project from nothing to players who see each other move in a
shared room. It follows the order the work actually happens in: create a project in the
dashboard, install the SDK, configure it with the Setup Wizard, then write the game code
that connects, enters a room, spawns a player and synchronises state.

---

## Table of Contents

1. [Overview](#1-overview)
2. [Prerequisites](#2-prerequisites)
3. [Before Unity — Create Your Project in the Dashboard](#3-before-unity--create-your-project-in-the-dashboard)
4. [The Supported Path — the Setup Wizard](#4-the-supported-path--the-setup-wizard)
5. [Step 1 — Install the SDK](#step-1--install-the-sdk)
6. [Step 2 — Create the NetworkSettings Asset](#step-2--create-the-networksettings-asset)
   - [Giving a player build its API key](#giving-a-player-build-its-api-key)
7. [Step 3 — Add NetworkManager to the Scene](#step-3--add-networkmanager-to-the-scene)
8. [Step 4 — Convert a Script to NetworkBehaviour](#step-4--convert-a-script-to-networkbehaviour)
9. [Step 5 — Set Up the Networked Prefab](#step-5--set-up-the-networked-prefab)
10. [Step 6 — Synchronise State with NetworkVariables](#step-6--synchronise-state-with-networkvariables)
11. [Step 7 — Connect, Enter a Room and Spawn](#step-7--connect-enter-a-room-and-spawn)
12. [Step 8 — Room List UI](#step-8--room-list-ui)
13. [Step 9 — Player Join and Leave Events](#step-9--player-join-and-leave-events)
14. [Step 10 — Leaving and Disconnecting](#step-10--leaving-and-disconnecting)
15. [Step 11 — Reconnect after a Drop](#step-11--reconnect-after-a-drop)
16. [Step 12 — Object Pooling (Optional)](#step-12--object-pooling-optional)
17. [Step 13 — Beyond the Basics](#step-13--beyond-the-basics)
18. [Connection State Machine](#connection-state-machine)
19. [Pre-Launch Checklist](#pre-launch-checklist)
20. [Common Errors](#common-errors)

---

## 1. Overview

```text
┌────────────────┐                          ┌────────────────┐
│   Player A     │                          │   Player B     │
│   Unity + SDK  │                          │   Unity + SDK  │
└───────┬────────┘                          └────────┬───────┘
        │   encrypted UDP (port 7777 by default)     │
        ▼                                            ▼
┌──────────────────────────────────────────────────────────────┐
│                         RTMPE server                         │
│        sessions · rooms · object relay · state broadcast     │
└──────────────────────────────────────────────────────────────┘
```

A game built on the SDK works like this:

1. The client calls `NetworkManager.Instance.Connect(apiKey)` and the SDK opens an
   authenticated, encrypted session with the RTMPE server.
2. The client creates or joins a **room** — the unit of play, holding up to 100 players.
3. Each client spawns its own player from a registered prefab. The client that spawns an
   object **owns** it.
4. The owner moves its objects and writes their `NetworkVariable`s; the SDK sends the
   changes at the client's tick rate (30 Hz by default) and every other client in the room
   applies them.

| Term | Meaning |
| --- | --- |
| Room | A group of connected players who see each other's networked objects. |
| Owner | The client allowed to change a networked object's state. `IsOwner` is `true` only there. |
| Host (master client) | One player per room with host authority, for example over shared world state. |
| Networked object | A spawned prefab carrying at least one `NetworkBehaviour`. |
| `NetworkVariable` | A value the owner writes and every client receives. |
| RPC | A method call sent to other clients (`[RtmpeRpc]`). |

---

## 2. Prerequisites

| Requirement | Detail |
| --- | --- |
| Unity | 2022.3 LTS or later |
| API compatibility level | .NET Standard 2.1 |
| Build targets | Windows, macOS, Linux, Android, iOS |
| An RTMPE project | Created in the RTMPE Developer Portal — it issues the API key and the connection values |
| Network | Outbound UDP to the server port (7777 by default) |

---

## 3. Before Unity — Create Your Project in the Dashboard

Nothing in this guide connects until a project exists in the
[RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard). The portal creates the
project, generates its API key and publishes the server's two public keys. The SDK ships
with no default credential and no default server.

1. Sign in to the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard).
2. Create a project and open it.
3. Under **API Keys**, create a key and copy it immediately — a key is shown only once,
   when it is created. If you lose it, delete it and create another.
4. Under **Connection settings**, copy the remaining three values.

| From the dashboard | Where it goes | What it is |
| --- | --- | --- |
| **API key** | The OS credential vault (in the Editor) or your own key source (in a build) | Identifies your project to the server. The only secret of the four. |
| **Server Host** / **Server Port** | The `NetworkSettings` asset | Which server to reach. The port is the server's UDP port, `7777` by default. |
| **Sealed-Box Public Key (X25519)** | The `NetworkSettings` asset | The public key the API key is sealed to before it is sent. |
| **Pinned Server Public Key** | The `NetworkSettings` asset | The server identity that Strict pinning (the default) checks. |

Both public keys are public values and are safe to keep in the project. The API key is
not: it never belongs in an asset, a scene or source code — see
[Giving a player build its API key](#giving-a-player-build-its-api-key).

---

## 4. The Supported Path — the Setup Wizard

**Window → RTMPE → Setup Wizard** configures the project for you. It opens by itself once
per Editor session, after the first script load (unless that load happens while entering
Play mode); **Window → RTMPE → Auto-Open Setup Wizard** turns this off for the project.
You can reopen the wizard from the menu at any time.

The wizard has seven steps:

| Step | What it does |
| --- | --- |
| 1. Verify | Confirms that the SDK assemblies are loaded. |
| 2. NetworkManager | Offers **Add NetworkManager to Scene** when the open scene has none. |
| 3. Connection Bootstrap | Optional. Adds `RtmpeConnectionBootstrap`, which connects, enters a room (create, join or matchmaking), spawns your player prefab and re-enters the room after a drop — no code required. |
| 4. Gateway configuration | Server host and port, both public keys and the API key. The key is stored in the OS credential vault. This step also offers **Inject this key into development builds** (see below). |
| 5. Production | Shows which API-key source a release build will use, and can write a provider script for it. |
| 6. Game type | The tick rate written to the settings asset. |
| 7. Validation | **Validate Configuration** checks that the values are well-formed. It does not contact the server; press Play and open **Window → RTMPE → Network Debugger** to verify connectivity. |

On **Finish** the wizard:

- creates `Assets/RTMPE/NetworkSettings.asset` if the project has no settings asset, or
  reuses the first one it finds;
- writes the host, port and tick rate to it, and writes the public keys and session-token
  settings when the wizard's fields contain them (an empty field never clears a value
  already on the asset);
- stores the API key in the OS credential vault — never in the asset.

> **Important:** check the scene before you continue. Adding the `NetworkManager` is a
> button on step 2 and **Finish** does not require it, and the wizard binds the settings
> asset only to a `NetworkManager` it added itself. A scene without a `NetworkManager`
> leaves `NetworkManager.Instance` `null`; a `NetworkManager` with no settings asset runs
> on the built-in loopback defaults and cannot reach the server.

Steps 2 and 3 below do the same work by hand and document every field. Step 4 onward is
game code the wizard does not write.

---

## Step 1 — Install the SDK

### Option A — Package archive from the portal

1. Sign in to the RTMPE Developer Portal and open the **Download** section of its
   [SDK documentation](https://portal.rtmpengine.com/en/docs). Download
   `com.rtmpe.sdk-<version>.tgz` and check it against the SHA-256 shown beside it.
2. Put the archive in your project's `Packages/` folder.
3. In **Window → Package Manager**, choose **+ → Add package from tarball…** (named
   **Install package from tarball…** in newer Unity versions) and select the archive —
   or reference it in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.rtmpe.sdk": "file:com.rtmpe.sdk-<version>.tgz"
  }
}
```

To upgrade, replace the archive and update the version in `Packages/manifest.json`.

### Option B — Git URL

In Unity, open **Window → Package Manager**, choose **+ → Add package from git URL…** and
enter:

```text
https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git
```

Or add the dependency to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.rtmpe.sdk": "https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git"
  }
}
```

**Upgrading.** Unity records the resolved commit of a Git dependency in
`Packages/packages-lock.json` and keeps using it. To move to a newer release, remove the
package in Package Manager and add the URL again, or delete the `com.rtmpe.sdk` entry
from `Packages/packages-lock.json` and let Unity resolve it again.

### Option C — Local copy

Place the package folder in your project's `Packages/` directory, or reference it by path
in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.rtmpe.sdk": "file:../path/to/com.rtmpe.sdk"
  }
}
```

### Verify installation

Open **Window → Package Manager**. The package is listed as:

```text
RTMPE SDK   1.0.8   ✓
```

The SDK's main namespaces are `RTMPE.Core`, `RTMPE.Rooms`, `RTMPE.Sync`, `RTMPE.Rpc` and
`RTMPE.Transport`. The package identifier is always `com.rtmpe.sdk`; to see
where it was fetched from, check the resolved URL in `Packages/packages-lock.json`.

The package also contains the conversion and scoring engine used by the Conversion Wizard
and the Network Readiness window. You need it only for automated conversion; see
[Automation](automation.md).

---

## Step 2 — Create the NetworkSettings Asset

The `NetworkSettings` asset holds a deployment's connection configuration. You can keep
several (for example one for development and one for production) and assign the one you
need to the `NetworkManager`.

1. In the **Project** window, right-click a folder and choose **Create → RTMPE → Settings**.
2. Name the asset, for example `RTMPESettings_Prod.asset`.
3. Fill in the fields below in the Inspector.

| Field | Default | Notes |
| --- | --- | --- |
| **Server Host** | `127.0.0.1` | The server host from the dashboard. |
| **Server Port** | `7777` | The server's UDP port. |
| **Api Key Seal Server Public Key Hex** | empty | **Required.** The dashboard's Sealed-Box Public Key (X25519, 64 hex characters). |
| **Server Pinning Mode** | `Strict` | How the server's identity is verified — see below. |
| **Pinned Server Public Key Hex** | empty | **Required under `Strict`.** The dashboard's Pinned Server Public Key (Ed25519, 64 hex characters). |
| **Tick Rate** | `30` | This client's simulation rate in Hz. Keep it the same on every client. |
| **Heartbeat Interval Ms** | `5000` | Keep-alive interval. |
| **Connection Timeout Ms** | `10000` | How long a connection attempt may take. |
| **Auto Rejoin Last Room On Reconnect** | on | Rejoin the previous room after a successful `Reconnect()`. |
| **Max Reconnect Attempts** | `5` | Attempts in the retry loop that `Reconnect()` starts. |
| **Prefab Registry** | empty | The generated prefab registry — see [Step 5](#step-5--set-up-the-networked-prefab). |
| **Enable Debug Logs** | off | Connection traces in the Console. Turn on while developing. |

The [API reference](api/index.md#networksettings) lists every field.

**Server pinning.** Under the default `Strict` mode the SDK compares the server's Ed25519
key with **Pinned Server Public Key Hex** and refuses to connect when no pin is
configured. Choose one mode before your first connection:

- **Production:** paste the key from the dashboard and keep `Strict`.
- **First-run capture:** `TrustOnFirstUse` stores the key seen on the first connection to
  each server and requires the same key afterwards.
- **Local testing only:** `InsecureNoPinning` skips the check. Never ship it.

The sealed-box key and the pinned key are different keys: never paste one into the
other's field.

> **Note:** there is deliberately no API-key field on `NetworkSettings`. A serialised
> field is written into the asset, committed with it and shipped in every build. Keep
> production settings assets out of public repositories: they name your server and its
> keys.

### Giving a player build its API key

In the Editor the SDK reads the key the Setup Wizard stored in the OS credential vault.
That vault is registered by the Editor assembly, which no build contains, so a player
build needs a key source of its own. `RTMPE.Core.ApiKeySource` consults the sources in
this order and uses the first one that has a key:

| Order | Source | Where it works |
| --- | --- | --- |
| 1 | a provider registered with `ApiKeySource.SetProvider` | the only one that reaches a player who just double-clicked the game you shipped |
| 2 | the Setup Wizard's vault, or a key staged for a development build | Play mode in your Editor; a development build you run yourself (see below) |
| 3 | `--rtmpe-api-key-file <path>` | your own machine, a test rig, CI, a launcher you control |
| 4 | `--rtmpe-api-key <key>` | the same, on a machine where other accounts cannot read process command lines |
| 5 | `RTMPE_API_KEY` | the same |

A source that *fails* — a provider that throws, or a key file that cannot be read or
holds no key — stops the resolution and is reported instead of falling through to the
next source. Only a source with nothing to give falls through. `ApiKeySource.TryResolve`
returns `false` in both cases, and `ApiKeySource.LastError` carries the failure.

Your own provider always outranks the wizard's vault, so in the Editor the vault is used
only when your provider returns nothing. The vault is stored per project.

**A provider component.** The provider is a `Func<string>`. Register it before anything
connects — `Awake` on an active object in the same scene as the `NetworkManager` is early
enough:

```csharp
using RTMPE.Core;
using UnityEngine;

public sealed class RtmpeCredential : MonoBehaviour
{
    private void Awake()
    {
        // MyCredentialStore is your own code: a launcher argument, a platform
        // login, a value fetched from your backend.
        ApiKeySource.SetProvider(() => MyCredentialStore.RtmpeApiKey);
    }
}
```

You do not have to write it yourself:

- The **production step** of the Setup Wizard writes `Assets/RTMPE/RtmpeCredentialProvider.cs`,
  a provider component with no serialised field, and can add it to the object carrying
  your `NetworkManager`. Call `RtmpeCredentialProvider.Supply(key)` with the key your
  launcher or backend obtained before anything connects. The step also lists every script
  in the project that registers a provider.
- The **Two Player Room** sample ships the same component as `TwoPlayerCredentials`.

**Fetching the key from your backend.** A provider returns a value; it cannot wait. Fetch
first, then connect:

```csharp
using System.Collections;
using RTMPE.Core;
using UnityEngine;
using UnityEngine.Networking;

public sealed class RtmpeFetchedCredential : MonoBehaviour
{
    [SerializeField] private string _endpoint = "https://your-backend.example/rtmpe/key";

    private string _key;

    private void Awake() => ApiKeySource.SetProvider(() => _key);

    private IEnumerator Start()
    {
        using (var request = UnityWebRequest.Get(_endpoint))
        {
            // Authenticate the player: an endpoint that returns the key to anyone
            // exposes it exactly as a key embedded in the build would.
            request.SetRequestHeader("Authorization", "Bearer " + MyAccount.SessionToken);

            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[MyGame] RTMPE key request failed: " + request.error);
                yield break;
            }

            _key = request.downloadHandler.text.Trim();
        }

        if (NetworkManager.TryGetInstance(out var net))
            net.Connect(_key);
    }
}
```

If you use `RtmpeConnectionBootstrap`, clear its **Connect On Start** option and call its
`Connect()` after the key has arrived. The bootstrap asks the provider again whenever it
opens a fresh session, so return the current key from the provider rather than a value
captured once.

The key a client presents is a key that client holds. A provider keeps it out of the
settings asset, your repository history and the shipped build; it does not hide it from
the person running the game.

### Staging a key for a development build

To run a standalone development build yourself without writing a provider first, open
the Setup Wizard's credential step and enable **Inject this key into development builds**.
The choice is stored in this machine's EditorPrefs, not in the project.

- A build made with **Development Build** enabled writes the key to
  `Assets/StreamingAssets/rtmpe.devkey` for the duration of the build and removes it when
  the build succeeds. The player reads the file only in a development build.
- Android keeps streaming assets inside the application archive, so an Android
  development build is made without the key and needs a provider like a release build.
  (WebGL is not a supported platform — see
  [Troubleshooting](troubleshooting.md#webgl-is-not-a-supported-platform).)
- A build that fails part-way leaves the file in the project. The wizard adds
  `rtmpe.devkey` and `rtmpe.devkey.meta` to your repository's `.gitignore`, the Editor
  reports a leftover file on the next script reload, and a release build that finds one
  stops with an error.
- A development build contains the key. Do not give one to testers or submit it to a
  store; on iOS the staged key is a plain file inside the `.ipa`.

---

## Step 3 — Add NetworkManager to the Scene

1. Create an empty GameObject in your first (boot) scene and name it
   `[RTMPE] NetworkManager`.
2. Add the component with **Component → RTMPE → NetworkManager**.
3. Assign your settings asset to its **Settings** field.

```text
Hierarchy (boot scene)
  ├── [RTMPE] NetworkManager   ← only here
  └── ...
```

`NetworkManager` is a singleton that survives scene loads (`DontDestroyOnLoad`). Add it to
one scene only; a second instance logs `Duplicate NetworkManager instance` and destroys
itself while the first keeps running.

---

## Step 4 — Convert a Script to NetworkBehaviour

A script whose state other players must see derives from `NetworkBehaviour` instead of
`MonoBehaviour`. The Conversion Wizard can make this change for you — see
[Automation](automation.md).

**Before:**

```csharp
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 5f;

    private void Update()
    {
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        transform.position += new Vector3(h, 0f, v) * _moveSpeed * Time.deltaTime;
    }
}
```

**After:**

```csharp
using System;
using RTMPE.Core;
using RTMPE.Sync;
using UnityEngine;

[RequireComponent(typeof(NetworkTransform))]
public class PlayerController : NetworkBehaviour
{
    [SerializeField] private float _moveSpeed = 5f;

    private NetworkVariableInt _health;
    private Action<int, int>   _onHealthChanged;

    // Called when the object is spawned on the network. Create NetworkVariables here.
    protected override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _health = new NetworkVariableInt(this, nameof(_health), initialValue: 100);

        _onHealthChanged = (oldValue, newValue) =>
        {
            if (newValue <= 0) Debug.Log($"[{name}] eliminated.");
        };
        _health.OnValueChanged += _onHealthChanged;
    }

    protected override void OnNetworkDespawn()
    {
        if (_health != null) _health.OnValueChanged -= _onHealthChanged;
        base.OnNetworkDespawn();
    }

    private void Update()
    {
        // Only the owner reads input and moves the object.
        // NetworkTransform sends the movement to everyone else.
        if (!IsOwner) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        transform.position += new Vector3(h, 0f, v) * _moveSpeed * Time.deltaTime;
    }

    public void TakeDamage(int amount)
    {
        if (!IsOwner || _health == null) return;
        _health.Value = Mathf.Max(0, _health.Value - amount);
    }
}
```

### The `IsOwner` rule

| Where the code runs | `IsOwner` |
| --- | --- |
| The client that spawned (owns) the object | `true` |
| Every other client | `false` |

Only the owner reads input, moves the object and writes `NetworkVariable.Value`. Every
client receives position and rotation through `NetworkTransform`, and value changes
through `OnValueChanged`. To change something you do not own, send the owner an RPC; the
owner can hand the object to another player with `OwnershipManager.RequestOwnershipTransfer`.

If your class overrides `OnDestroy` or `OnNetworkSpawn`, call the base implementation;
the analyzers report a missing call as `RTMPE1020` and `RTMPE1022` (see the
[rule reference](diagnostics.md)).

---

## Step 5 — Set Up the Networked Prefab

Every prefab that appears on the network carries:

```text
PlayerPrefab
  ├── PlayerController              ← your NetworkBehaviour
  ├── NetworkTransform              ← Component → RTMPE → Network Transform
  └── NetworkTransformInterpolator  ← added automatically with NetworkTransform
```

`NetworkTransform` requires `NetworkTransformInterpolator`, so Unity adds the
interpolator with it. Leave the interpolator enabled: it renders the movement of objects
this client does not own.

**NetworkTransform** — the main Inspector fields:

| Field | Default | Notes |
| --- | --- | --- |
| **Sync Position** | on | Send and apply world position. |
| **Sync Rotation** | on | Send and apply rotation. |
| **Sync Scale** | off | Enable only for objects that change scale. |
| **Position Threshold** | `0.01` | Minimum movement (metres) before a change is sent. |
| **Rotation Threshold** | `0.1` | Minimum rotation (degrees) before a change is sent. |

**NetworkTransformInterpolator** — the main Inspector fields:

| Field | Default | Notes |
| --- | --- | --- |
| **Buffer Size** | `10` | Buffered snapshots. |
| **Interpolation Delay** | `0.1` | Render delay in seconds. With **Adaptive Delay** on (the default) this is the maximum; on a steady link the delay is lower. |
| **Interpolate Scale** | off | Match **Sync Scale**. |

The full list, including prediction and remote-motion timing, is in the
[API reference](api/index.md#networktransform).

### Registering prefabs

Every client must map the same prefab id to the same prefab.
**Window → RTMPE → Network Prefabs** keeps that mapping for you:

1. Select the prefab and press **Allocate id for selection**, or press **Scan the project
   for spawnable prefabs with no id** to list every networked prefab that has none.
2. Press **Generate RtmpePrefabIds.cs and the prefab registry**. The window writes the
   `RtmpePrefabIds` constants and `Assets/RTMPE/Generated/RtmpePrefabRegistry.asset`.
3. Assign the registry asset to the **Prefab Registry** field of your settings asset.

With a registry assigned, every prefab in it is registered on every connection, and you
spawn with the generated constant. The `RtmpePrefabIds` class is generated in the
`RTMPE.Generated` namespace:

```csharp
using RTMPE.Generated;

NetworkManager.Instance.Spawner.Spawn(RtmpePrefabIds.Player, Vector3.zero, Quaternion.identity);
```

Commit `rtmpe-prefabs.json` (the window's record at the project root): it is what keeps
the ids identical on every machine. Ids are keyed by asset GUID, so renaming or moving a
prefab keeps its id, and a retired id is never reused. Every prefab listed in the registry
is loaded with the scene that holds your `NetworkManager`; register rarely used, large
prefabs by hand instead.

To register by hand — for a prefab from an asset bundle, for example — call
`Spawner.RegisterPrefab(id, prefab)` before the first `Spawn`. Hand registrations persist
across reconnects and take precedence over the registry.

---

## Step 6 — Synchronise State with NetworkVariables

A `NetworkVariable` holds a value that the owner writes and every client receives.

### Available types

| Class | Value type |
| --- | --- |
| `NetworkVariableInt` | `int` |
| `NetworkVariableFloat` | `float` |
| `NetworkVariableBool` | `bool` |
| `NetworkVariableVector2` | `Vector2` |
| `NetworkVariableVector2Int` | `Vector2Int` |
| `NetworkVariableVector3` | `Vector3` |
| `NetworkVariableQuaternion` | `Quaternion` |
| `NetworkVariableString` | `string` (UTF-8) |

Synchronised lists — `NetworkVariableListInt`, `…Float`, `…Bool`, `…String`,
`…Vector2`, `…Vector2Int` and `…Vector3` — are described in
[NetworkVariable types](api/index.md#networkvariable-types).

### Rules

1. **Create variables in `OnNetworkSpawn()`**, not in `Awake()` or `Start()`.
2. **Pass `nameof(field)` as the name.** A variable's identity is derived from its
   component's type and that name, so two variables on one component must not share a
   name — including a base-class and a derived-class member with the same name. For the
   same reason, an object carries at most one instance of each `NetworkBehaviour` type.
   A collision throws from `OnNetworkSpawn` and the object is not spawned.
3. **Only the owner writes `Value`.** A write from any other client is refused with a
   warning. Every client reads the value and reacts through `OnValueChanged`.
4. **Changes are sent automatically** at the tick rate. A player who joins later receives
   the current values without any code on your side.

### Example

```csharp
using System;
using RTMPE.Core;
using RTMPE.Sync;
using UnityEngine;

public class MyCharacter : NetworkBehaviour
{
    private NetworkVariableInt    _score;
    private NetworkVariableString _displayName;
    private NetworkVariableBool   _isAlive;

    private Action<int, int>       _onScoreChanged;
    private Action<string, string> _onNameChanged;
    private Action<bool, bool>     _onAliveChanged;

    protected override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _score       = new NetworkVariableInt(this, nameof(_score));
        _displayName = new NetworkVariableString(this, nameof(_displayName), "Player");
        _isAlive     = new NetworkVariableBool(this, nameof(_isAlive), true);

        _onScoreChanged = (oldValue, newValue) => UpdateScoreLabel(newValue);
        _onNameChanged  = (oldValue, newValue) => UpdateNameTag(newValue);
        _onAliveChanged = (oldValue, newValue) => PlayAliveAnimation(newValue);

        _score.OnValueChanged       += _onScoreChanged;
        _displayName.OnValueChanged += _onNameChanged;
        _isAlive.OnValueChanged     += _onAliveChanged;
    }

    protected override void OnNetworkDespawn()
    {
        if (_score != null)       _score.OnValueChanged       -= _onScoreChanged;
        if (_displayName != null) _displayName.OnValueChanged -= _onNameChanged;
        if (_isAlive != null)     _isAlive.OnValueChanged     -= _onAliveChanged;
        base.OnNetworkDespawn();
    }

    public void AddPoints(int points)
    {
        if (IsOwner) _score.Value += points;
    }

    private void UpdateScoreLabel(int score)   { /* update UI */ }
    private void UpdateNameTag(string name)    { /* update label */ }
    private void PlayAliveAnimation(bool alive) { /* play animation */ }
}
```

---

## Step 7 — Connect, Enter a Room and Spawn

> **Shortcut:** `RtmpeConnectionBootstrap` (**Component → RTMPE → Connection Bootstrap**)
> performs this whole step — connect, enter a room, spawn the local player and re-enter
> the room after a drop. See
> [Connecting, without writing any](api/index.md#connecting-without-writing-any). Use the
> code below when you want the flow in your own script; do not use both.

The flow has three events that matter:

- **A fresh connection** — the state moves from `Connecting` to `Connected`. Create or
  join a room here. (`OnConnected` also fires after leaving a room and after a successful
  `Reconnect()`, so it is not the place to create rooms.)
- **`Rooms.OnRoomJoined`** — the player is seated in the room. Spawn here. `CreateRoom`
  joins its creator automatically, so it raises `OnRoomCreated` and then `OnRoomJoined`.
- **`OnConnectionFailed` / `OnDisconnected`** — report the failure or return to a menu.

Place this script on a persistent GameObject in the boot scene:

```csharp
using RTMPE.Core;
using RTMPE.Rooms;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    [Header("Room")]
    [SerializeField] private string _roomName = "My Game Room";
    [Range(1, 100)]
    [SerializeField] private int _maxPlayers = 4;

    [Header("Player")]
    [SerializeField] private GameObject _playerPrefab;
    [SerializeField] private uint _playerPrefabId = 1;   // the same on every client
    [SerializeField] private Vector3 _spawnPosition = new Vector3(0f, 1f, 0f);

    private NetworkBehaviour _localPlayer;

    private void Start()
    {
        if (!NetworkManager.TryGetInstance(out var net))
        {
            Debug.LogError("[GameManager] No NetworkManager in the scene.");
            return;
        }

        // Registrations persist across reconnects; with a generated prefab registry
        // assigned to the settings asset this line is not needed.
        net.Spawner.RegisterPrefab(_playerPrefabId, _playerPrefab);

        net.OnStateChanged     += HandleStateChanged;
        net.OnConnectionFailed += HandleConnectionFailed;
        net.OnDisconnected     += HandleDisconnected;
        net.Rooms.OnRoomJoined += HandleRoomJoined;
        net.Rooms.OnRoomError  += HandleRoomError;

        if (ApiKeySource.TryResolve(out string apiKey))
            net.Connect(apiKey);
        else
            Debug.LogError("[GameManager] No API key. In the Editor, run the Setup Wizard; "
                + "in a build, register a provider with ApiKeySource.SetProvider.");
    }

    private void OnDestroy()
    {
        if (!NetworkManager.TryGetInstance(out var net)) return;

        net.OnStateChanged     -= HandleStateChanged;
        net.OnConnectionFailed -= HandleConnectionFailed;
        net.OnDisconnected     -= HandleDisconnected;
        net.Rooms.OnRoomJoined -= HandleRoomJoined;
        net.Rooms.OnRoomError  -= HandleRoomError;
    }

    private void HandleStateChanged(NetworkState previous, NetworkState current)
    {
        // A fresh connection only. After Reconnect() the SDK rejoins the last room
        // itself, and leaving a room also returns the state to Connected.
        if (previous != NetworkState.Connecting || current != NetworkState.Connected) return;

        NetworkManager.Instance.Rooms.CreateRoom(new CreateRoomOptions
        {
            Name       = _roomName,
            MaxPlayers = _maxPlayers,
            IsPublic   = true,
        });
    }

    private void HandleRoomJoined(RoomInfo room)
    {
        Debug.Log($"[GameManager] Joined {room.Name} ({room.RoomCode}), "
            + $"{room.PlayerCount}/{room.MaxPlayers} players.");

        // Keep a player that is still spawned; replace one left over from a
        // previous session.
        if (_localPlayer != null && _localPlayer.IsSpawned) return;
        if (_localPlayer != null) Destroy(_localPlayer.gameObject);

        _localPlayer = NetworkManager.Instance.Spawner.Spawn(
            _playerPrefabId, _spawnPosition, Quaternion.identity);

        if (_localPlayer == null)
            Debug.LogError("[GameManager] Spawn returned null — check the prefab registration.");
    }

    private void HandleConnectionFailed(string reason) =>
        Debug.LogError($"[GameManager] Connection failed: {reason}");

    private void HandleDisconnected(DisconnectReason reason) =>
        Debug.Log($"[GameManager] Disconnected: {reason}");

    private void HandleRoomError(string error) =>
        Debug.LogError($"[GameManager] Room error: {error}");
}
```

To join an existing room instead of creating one, call
`Rooms.JoinRoom(roomId, options)` or `Rooms.JoinRoomByCode(roomCode, options)`, or use
matchmaking (`Matchmaking`) — see the [API reference](api/index.md#roommanager).

---

## Step 8 — Room List UI

`Rooms.ListRooms()` requests the public rooms; the answer arrives on
`Rooms.OnRoomListReceived`.

```csharp
using RTMPE.Core;
using RTMPE.Rooms;
using UnityEngine;
using UnityEngine.UI;

public class RoomListUI : MonoBehaviour
{
    [SerializeField] private Transform  _container;    // parent for the entries
    [SerializeField] private GameObject _entryPrefab;  // a Text and a Button
    [SerializeField] private InputField _codeInput;    // optional: join by code

    private void OnEnable()
    {
        if (NetworkManager.TryGetInstance(out var net))
            net.Rooms.OnRoomListReceived += Populate;
    }

    private void OnDisable()
    {
        if (NetworkManager.TryGetInstance(out var net))
            net.Rooms.OnRoomListReceived -= Populate;
    }

    public void Refresh() => NetworkManager.Instance.Rooms.ListRooms(publicOnly: true);

    public void JoinByCode()
    {
        string code = _codeInput != null ? _codeInput.text.Trim() : string.Empty;
        if (code.Length > 0) NetworkManager.Instance.Rooms.JoinRoomByCode(code);
    }

    private void Populate(RoomInfo[] rooms)
    {
        foreach (Transform child in _container) Destroy(child.gameObject);

        foreach (var room in rooms)
        {
            var entry = Instantiate(_entryPrefab, _container);
            entry.GetComponentInChildren<Text>().text =
                $"{room.Name}  [{room.PlayerCount}/{room.MaxPlayers}]  #{room.RoomCode}";

            string roomId = room.RoomId;
            entry.GetComponentInChildren<Button>().onClick.AddListener(
                () => NetworkManager.Instance.Rooms.JoinRoom(roomId));
        }
    }
}
```

---

## Step 9 — Player Join and Leave Events

```csharp
private void OnEnable()
{
    if (!NetworkManager.TryGetInstance(out var net)) return;
    net.Rooms.OnPlayerJoined += HandlePlayerJoined;
    net.Rooms.OnPlayerLeft   += HandlePlayerLeft;
}

private void OnDisable()
{
    if (!NetworkManager.TryGetInstance(out var net)) return;
    net.Rooms.OnPlayerJoined -= HandlePlayerJoined;
    net.Rooms.OnPlayerLeft   -= HandlePlayerLeft;
}

private void HandlePlayerJoined(PlayerInfo player) =>
    Debug.Log($"Player joined: {player.DisplayName} ({player.PlayerId})");

private void HandlePlayerLeft(string playerId) =>
    Debug.Log($"Player left: {playerId}");
```

Subscriptions on `Rooms`, `Lobby` and `Matchmaking` are kept across reconnects, so
subscribe once — not in a handler that runs on every connection.

### What happens to a leaving player's objects

`DestroyWithOwner` decides:

- `true` (the default): the object is destroyed on every client when its owner leaves.
- `false`: the object stays, and every client hands it to the room's host.

The value travels with the spawn and applies to the whole object, and the SDK reads it
from the object's first `NetworkBehaviour`. Set it on every `NetworkBehaviour` of the
object, in `Awake`, so the order of the components does not matter:

```csharp
private void Awake()
{
    // Keep this object when its owner leaves.
    foreach (var behaviour in GetComponents<NetworkBehaviour>())
        behaviour.DestroyWithOwner = false;
}
```

---

## Step 10 — Leaving and Disconnecting

- `Rooms.LeaveRoom()` leaves the room and raises `Rooms.OnRoomLeft`; the session stays
  connected.
- `NetworkManager.Instance.Disconnect()` ends the session and raises `OnDisconnected` with
  `DisconnectReason.ClientRequest`. It also discards the reconnect token, so the next
  connection is a full `Connect(apiKey)`.

```csharp
public void QuitToMainMenu()
{
    NetworkManager.Instance.Disconnect();
    UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
}
```

---

## Step 11 — Reconnect after a Drop

After a successful connection the SDK holds a **reconnect token**. When the connection
drops and the token survives, `Reconnect()` resumes the session without the API key, and
the player keeps the same player id.

> **Shortcut:** `RtmpeConnectionBootstrap` reconnects for you, and opens a fresh session
> when the token cannot be used.

### How reconnecting works

- `NetworkManager.CanReconnect` is `true` while a token is held.
- `Reconnect()` starts a bounded retry loop: up to **Max Reconnect Attempts** attempts
  (5 by default) with a jittered back-off between them. It returns `false` when there is
  no token, when the manager is not `Disconnected`, or when a loop is already running.
- `OnDisconnected` is raised after each failed attempt, and `OnReconnectFailed` (with the
  number of attempts) when the loop gives up. The token is discarded at that point.
- After a successful reconnect the SDK rejoins the last room when **Auto Rejoin Last Room
  On Reconnect** is on (the default), raising `OnAutoRejoinAttempt` and then
  `Rooms.OnRoomJoined` — or `Rooms.OnRoomError` if the room no longer exists.

```csharp
private bool _reconnecting;

private void HandleDisconnected(DisconnectReason reason)
{
    if (_reconnecting) return;                 // a failed attempt inside the retry loop

    var net = NetworkManager.Instance;
    if (net.CanReconnect && net.Reconnect())
    {
        _reconnecting = true;
        ShowReconnectingIndicator();
    }
    else
    {
        ReturnToMainMenu();                    // a full Connect(apiKey) is needed
    }
}

private void HandleConnected()                => _reconnecting = false;
private void HandleReconnectFailed(int tries) { _reconnecting = false; ReturnToMainMenu(); }
```

Subscribe these handlers to `OnDisconnected`, `OnConnected` and `OnReconnectFailed`.

### When the token survives

| What ended the session | `DisconnectReason` | Token and last room kept? |
| --- | --- | --- |
| No heartbeat answer within the liveness window | `ConnectionLost` | Yes |
| The server closed the session (for example a restart) | `ServerRequest` | Yes |
| A reconnect attempt that timed out before the server answered | `Timeout` | Yes |
| You called `Disconnect()` | `ClientRequest` | No |
| A first connection that timed out | `Timeout` | No |
| A socket error on the transport | `ConnectionLost` | No |
| The player was kicked | `Kicked` | No |
| A protocol or configuration failure | `ProtocolError` | No, except a pinning refusal during `Reconnect()` |
| The session's encryption counter ran out | `NonceExhausted` | No |

Do not decide from the reason alone — check `CanReconnect`, as the handler above does.

### The last room

`NetworkManager.LastRoomId` and `LastRoomCode` hold the room the player was in and share
the token's lifetime; `Rooms.LeaveRoom()` clears them. To offer your own "Rejoin?" prompt
instead of rejoining automatically, turn off **Auto Rejoin Last Room On Reconnect** and use
these values.

---

## Step 12 — Object Pooling (Optional)

For objects that are spawned and despawned often (projectiles, effects), an
`INetworkObjectPool` replaces `Instantiate` and `Destroy`.

```csharp
using System.Collections.Generic;
using RTMPE.Core;
using UnityEngine;

public sealed class SimplePool : INetworkObjectPool
{
    private readonly Dictionary<uint, Queue<GameObject>> _buckets =
        new Dictionary<uint, Queue<GameObject>>();

    public GameObject Acquire(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (_buckets.TryGetValue(prefabId, out var queue) && queue.Count > 0)
        {
            var instance = queue.Dequeue();
            instance.transform.SetPositionAndRotation(position, rotation);
            instance.SetActive(true);
            return instance;
        }
        return Object.Instantiate(prefab, position, rotation);
    }

    public void Release(uint prefabId, GameObject instance)
    {
        // uint.MaxValue: the instance was not tagged with a prefab id — destroy it.
        if (prefabId == uint.MaxValue) { Object.Destroy(instance); return; }

        instance.SetActive(false);
        if (!_buckets.TryGetValue(prefabId, out var queue))
            _buckets[prefabId] = queue = new Queue<GameObject>();
        queue.Enqueue(instance);
    }
}
```

The `SpawnManager` is rebuilt for every connection. Prefab registrations and event
subscriptions move to the new instance; the pool does not, so install it on every
connection:

```csharp
private readonly SimplePool _pool = new SimplePool();

private void HandleConnected() => NetworkManager.Instance.Spawner.SetObjectPool(_pool);
```

Without a pool, `SpawnManager` uses `Object.Instantiate` and `Object.Destroy`. Variables
created in `OnNetworkSpawn` are created again when a pooled instance is spawned next;
reset any other state of your own in `OnNetworkDespawn`.

---

## Step 13 — Beyond the Basics

Steps 1–12 cover a complete multiplayer loop. The SDK also provides:

- **Synchronised lists.** `NetworkVariableList` types replicate collections (inventory,
  a kill feed, cells on a board) and raise `OnListChanged`. A list is sent whole to a
  joining player, so its size is bounded by one datagram: 283 `int`s, 141 `Vector2Int`s,
  94 `Vector3`s. See [NetworkVariable types](api/index.md#networkvariable-types).
- **Per-variable send rates.** Set a variable's `SendRateHz` after creating it
  (`_score.SendRateHz = 10f;`) to send a frequently changing value less often than the
  tick rate.
- **RPCs.** `[RtmpeRpc(RpcTarget.All)]` marks a method other clients can run, and
  `RPC(nameof(Method), args)` calls it. `Caller` restricts who may call it, and
  `SendEnhancedRpcAsync` calls a server-side method and awaits its reply. See
  [Remote Procedure Calls](api/index.md#remote-procedure-calls).
- **Networked scenes.** The host calls `NetworkManager.Instance.Scene.LoadScene(name, mode)`;
  every client loads the scene and reports back, and `OnAllPlayersSceneLoaded` fires when
  all have. Add `RtmpeSceneLoader` (**Component → RTMPE → Scene Loader**) to a root object
  in the boot scene and the loading and reporting happen for you. Every scene must be in
  **Build Settings**; **Window → RTMPE → Network Scenes** checks that. See
  [Networked scenes](api/index.md#networked-scenes) and
  [Scene loading, without writing any](api/index.md#scene-loading-without-writing-any).
- **Interest management.** An `InterestManager` with the local player assigned to its
  `TrackedTransform` limits the updates a client receives to its surroundings in a large
  world. See [Interest management](api/index.md#interest-management).
- **Host authority.** `NetworkManager.Instance.IsMasterClient` tells whether this client
  is the host; `Rooms.TransferMasterClient(playerId)` hands the role over and
  `Rooms.OnMasterClientChanged` reports changes.
- **Physics.** `NetworkRigidbody` and `NetworkRigidbody2D` are described in the
  [API reference](api/index.md#networkrigidbody--networkrigidbody2d). For a physics-driven
  object that must look the same everywhere, use `NetworkTransform` and make the
  non-owners' `Rigidbody` kinematic.

### Shared world state

State that belongs to no player — pickups, a generated layout, the current round — lives
on the room's **world object**: a prefab carrying `RtmpeWorldAuthority`
(**Component → RTMPE → World Authority**) next to the `NetworkBehaviour` that holds the
world's variables. `RtmpeWorldAuthority` is spawned shared by default (its **Shared
Authority** option), so any member of the room can send RPCs to it; its variables are
still written only by its owner, the host.

- **Spawning.** Set the prefab's **World Key** (for example `board`; the default is
  `world`). Add `RtmpeWorldSpawner` (**Component → RTMPE → World Spawner**) to the scene
  and assign the prefab. The prefab needs an id from **Window → RTMPE → Network Prefabs**
  and an active root. The host spawns the world when it enters the room, and a player who
  becomes host spawns one if the room has none.
- **Surviving the host.** When the host leaves, the world passes to the next host with its
  state, and players who join later receive it as usual.
- **Finding it.** Call `RtmpeWorldAuthority.Find(key)` each time you need the world, and
  subscribe to `RtmpeWorldAuthority.OnWorldReady` to learn when the instance for a key
  changes. Do not cache the reference.
- **Populating it.** Fill the world in `OnWorldBorn`, which runs on the owner when a
  world carries none of the room's state yet — not in `OnNetworkSpawn`, which runs on
  every client for every copy.

```csharp
using RTMPE.Core;
using RTMPE.Rpc;
using RTMPE.Sync;
using UnityEngine;

// On the world prefab, beside RtmpeWorldAuthority.
public class BoardState : NetworkBehaviour
{
    private NetworkVariableListVector2Int _food;

    protected override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        _food = new NetworkVariableListVector2Int(this, nameof(_food));
        GetComponent<RtmpeWorldAuthority>().OnWorldBorn += Populate;
    }

    // Runs on the owner only, for a world that holds none of the room's state.
    private void Populate()
    {
        for (int i = _food.Count; i < 6 && !_food.IsFull; i++)
            if (!_food.TryAdd(RandomCell())) break;
    }

    // Any client may send this; the owner's copy applies it.
    [RtmpeRpc(RpcTarget.All)]
    public void RequestEat(int x, int y)
    {
        if (!IsOwner) return;
        if (_food.Remove(new Vector2Int(x, y))) _food.TryAdd(RandomCell());
    }

    private static Vector2Int RandomCell() => new Vector2Int(Random.Range(0, 16), Random.Range(0, 16));
}
```

```csharp
// Anywhere in the game:
var world = RtmpeWorldAuthority.Find("board");
if (world != null) world.GetComponent<BoardState>().RPC(nameof(BoardState.RequestEat), x, y);
```

The **Shared World** sample wires both components. For the complete API — including
`WasRecreated`, **Persist Across Scene Loads** and restricting callers with
`Caller = RpcCaller.Host` — see [World authority](api/index.md#world-authority).

---

## Connection State Machine

```text
                 Connect(apiKey)
  Disconnected ─────────────────▶ Connecting ── handshake complete ──▶ Connected
       ▲                              │                                  │    ▲
       │                              │ timeout / refused                │    │ LeaveRoom()
       │◀─────────────────────────────┘                   CreateRoom /   │    │
       │                                                  JoinRoom       ▼    │
       │                                                               InRoom ┘
       │
       │   Disconnect() or heartbeat loss:   Connected / InRoom ──▶ Disconnecting ──▶ Disconnected
       │   server close or transport error:  Connected / InRoom ──────────────────▶ Disconnected
       │
       │   Reconnect() (CanReconnect)
       └──────────────────────────────▶ Reconnecting ── success ──▶ Connected ──▶ (auto-rejoin) ──▶ InRoom
                                            │
                                            └─ attempt failed ──▶ Disconnected ──▶ next attempt, or
                                                                                   OnReconnectFailed
```

- Call `Connect()` only in the `Disconnected` state.
- Call `Reconnect()` only when `CanReconnect` is `true`.
- Create or join rooms once connected; spawn once `Rooms.OnRoomJoined` has fired.
- `OnStateChanged(previous, current)` reports every transition.

---

## Pre-Launch Checklist

- [ ] `com.rtmpe.sdk 1.0.8` appears in Package Manager.
- [ ] The settings asset has the production **Server Host**, **Server Port** and both
      public keys, and **Server Pinning Mode** is `Strict`.
- [ ] A `NetworkManager` exists in the boot scene only, with the settings asset assigned.
- [ ] Release builds obtain the API key from a provider registered with
      `ApiKeySource.SetProvider` — never from a serialised field or a literal.
- [ ] Every networked prefab has a `NetworkBehaviour`, a `NetworkTransform` and an enabled
      `NetworkTransformInterpolator`.
- [ ] Prefab ids come from the Network Prefabs window, and `rtmpe-prefabs.json` is committed.
- [ ] Every spawnable prefab is registered before the first `Spawn()` — through the prefab
      registry or `RegisterPrefab()`.
- [ ] `NetworkVariable`s are created in `OnNetworkSpawn()` with unique names per object.
- [ ] Input handling and state writes are guarded with `IsOwner`.
- [ ] Event handlers are stored delegates, subscribed once and unsubscribed in
      `OnDisable`/`OnDestroy`.
- [ ] The `OnDisconnected` handler checks `CanReconnect` before falling back to `Connect(apiKey)`.
- [ ] An object pool, if used, is installed on every connection.
- [ ] **Enable Debug Logs** is off in the production settings asset.
- [ ] IL2CPP builds (iOS, Android) have been run on a device and every `[RtmpeRpc]` fires.
      If **Managed Stripping Level** is above **Low**, preserve your RPC methods and custom
      `NetworkVariable<T>` types in a `link.xml` — see
      [Troubleshooting](troubleshooting.md#il2cpp-missingmethodexception-at-runtime).

---

## Common Errors

### `[RTMPE] NetworkManager.Connect: apiKey must not be null or empty`

No source had a key. In the Editor, run **Window → RTMPE → Setup Wizard**. In a build,
register a provider with `ApiKeySource.SetProvider`, or launch with
`--rtmpe-api-key-file <path>` or `RTMPE_API_KEY` on a machine you control. If a source
failed rather than being empty, `ApiKeySource.LastError` says why.

### `Connect` is ignored because of the current state

`Connect()` runs only from `Disconnected`:

```csharp
var net = NetworkManager.Instance;
if (net.State == NetworkState.Disconnected && ApiKeySource.TryResolve(out string key))
    net.Connect(key);
```

### Players do not see each other move

- The prefab has no `NetworkTransform`.
- The `NetworkTransformInterpolator` is disabled, so received movement is never rendered.
- Movement code runs on every client instead of only where `IsOwner` is `true`.

### `Spawn` returns `null`

No prefab is registered under that id. Assign the generated registry to the settings
asset, or call `Spawner.RegisterPrefab(id, prefab)` before `Spawn()`. When a registry is
assigned, the Console names any entry it skipped on connect; regenerate the registry from
**Window → RTMPE → Network Prefabs**.

### `OnValueChanged` never fires on other clients

The value is written on a client that does not own the object; such a write is refused
with a warning. Write variables only where `IsOwner` is `true`, and create them in
`OnNetworkSpawn()`.

### `Duplicate NetworkManager instance` warning

Two scenes contain a `NetworkManager`. Keep it in the boot scene only; the duplicate
destroys itself and the running manager is unaffected.

### The connection times out

- Outbound UDP to the server port is blocked by a firewall or router.
- **Server Host** or **Server Port** is wrong (a standalone build with the default
  `127.0.0.1` reaches nothing).
- See [Troubleshooting](troubleshooting.md) for the full checklist.

---

*RTMPE SDK 1.0.8 — [API Reference](api/index.md) · [Troubleshooting](troubleshooting.md) · [Performance Tuning](performance-tuning.md)*
