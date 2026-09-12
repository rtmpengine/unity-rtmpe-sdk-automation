# RTMPE SDK — Getting Started

> **SDK Version:** `com.rtmpe.sdk 1.0.5`
> **Unity Version Required:** Unity 2022.3 LTS or later
> **Target Platform:** PC, Mac, Linux, Android, iOS. WebGL is not supported — see [Troubleshooting](troubleshooting.md#webgl-is-not-a-supported-platform).

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Prerequisites](#2-prerequisites)
3. [Before Unity — Create Your Project in the Dashboard](#3-before-unity--create-your-project-in-the-dashboard)
4. [The Supported Path — the Setup Wizard](#4-the-supported-path--the-setup-wizard)
5. [Step 1 — Install the SDK](#step-1--install-the-sdk)
6. [Step 2 — Create the NetworkSettings Asset](#step-2--create-the-networksettings-asset)
   - [Giving a player build its API key](#giving-a-player-build-its-api-key)
7. [Step 3 — Add NetworkManager to the Scene](#step-3--add-networkmanager-to-the-scene)
8. [Step 4 — Convert Scripts to NetworkBehaviour](#step-4--convert-scripts-to-networkbehaviour)
9. [Step 5 — Set Up the Networked Prefab](#step-5--set-up-the-networked-prefab)
10. [Step 6 — Synchronize State with NetworkVariables](#step-6--synchronize-state-with-networkvariables)
11. [Step 7 — Create a GameManager](#step-7--create-a-gamemanager)
12. [Step 8 — Room List UI](#step-8--room-list-ui)
13. [Step 9 — Handle Player Join and Leave Events](#step-9--handle-player-join-and-leave-events)
14. [Step 10 — Disconnection and Cleanup](#step-10--disconnection-and-cleanup)
15. [Step 11 — Reconnect after a Drop](#step-11--reconnect-after-a-drop)
16. [Step 12 — Object Pooling (Optional)](#step-12--object-pooling-optional)
17. [Step 13 — Beyond the Basics](#step-13--beyond-the-basics)
18. [Complete API Reference](#complete-api-reference)
19. [Connection State Machine](#connection-state-machine)
20. [Pre-Launch Checklist](#pre-launch-checklist)
21. [Common Errors and Fixes](#common-errors-and-fixes)
22. [Performance Notes](#performance-notes)

---

## 1. Architecture Overview

```
┌────────────────────────────────────────────────────────────┐
│                      GAME CLIENTS                          │
│                                                            │
│   ┌──────────────────┐       ┌──────────────────┐          │
│   │    Player 1      │       │    Player 2      │          │
│   │  Unity 2022.3+   │       │  Unity 2022.3+   │          │
│   │  RTMPE SDK       │       │  RTMPE SDK       │          │
│   └────────┬─────────┘       └───────┬──────────┘          │
│            │ UDP :7777               │ UDP :7777           │
└────────────┼─────────────────────────┼─────────────────────┘
             │                         │
             ▼                         ▼
┌────────────────────────────────────────────────────────────┐
│                    RTMPE Backend                           │
│                                                            │
│   ┌──────────────────────┐   ┌──────────────────────────┐  │
│   │  UDP Gateway (Rust)  │   │   Room Service (Go)      │  │
│   │  Port 7777 (UDP)     │─▶│   CreateRoom / JoinRoom  │  │
│   │  Port 7778 (KCP)     │   │   LeaveRoom / GetRoom    │  │
│   └──────────────────────┘   └──────────────────────────┘  │
│              │                          │                  │
│              └──────────┬───────────────┘                  │
│                         ▼                                  │
│                 ┌──────────────┐                           │
│                 │  NATS Bus    │   Event routing           │
│                 └──────────────┘                           │
│                                                            │
│   PostgreSQL — API Key validation + Room persistence       │
└────────────────────────────────────────────────────────────┘
```

### Connection flow

1. Client calls `NetworkManager.Instance.Connect(apiKey)`.
2. Gateway validates the API key against the database and issues a session token.
3. Client enters `NetworkState.Connected`.
4. Client creates or joins a **Room** (1–100 players, configurable per room).
5. Each client spawns their player object via `SpawnManager.Spawn()`.
6. The server broadcasts state at **30 Hz** to every player in the room.
7. Players see each other moving in real time at P99 < 30 ms latency (within region).

---

## 2. Prerequisites

| Requirement     | Detail                                    |
| --------------- | ----------------------------------------- |
| Unity           | Unity 2022.3 LTS or later                 |
| .NET Standard   | 2.1                                       |
| Build targets   | PC, Mac, Linux, Android, iOS — all supported |
| RTMPE Gateway   | ≥ 3.0.0 — obtain from the RTMPE dashboard |
| API Key         | Issued via the RTMPE developer dashboard  |
| Outbound UDP    | Your firewall must allow **outbound** UDP on port 7777 |

---

## 3. Before Unity — Create Your Project in the Dashboard

Nothing in this guide connects to anything until a project exists in the
[RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard). The portal
is where a project is created, where its API key is generated, and where the
gateway's two public keys are published; the SDK ships with no default
credential and no default gateway, and the loopback defaults it does carry
reach nothing.

1. Sign in to the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard).
2. Create a project and open it.
3. Under **API Keys**, create a key and copy it there and then — a key is shown
   **once, at creation**. A key that was not copied cannot be read back: delete
   it and create another.
4. Under **Connection settings**, copy the remaining three values.

The four together are what the Setup Wizard and the `NetworkSettings` asset ask
for, under the same names:

| From the dashboard | Where it lands | Why it is needed |
| ------------------ | -------------- | ---------------- |
| **API key** | the OS credential vault (never an asset, never a scene) | identifies the project to the gateway |
| **Server Host** / **Server Port** | `NetworkSettings` | which gateway to reach; the asset carries one port, the gateway's UDP port (`7777` by default) |
| **Sealed-Box Public Key (X25519)** | `NetworkSettings` | the key the API key is sealed to; the gateway accepts no other envelope |
| **Pinned Server Public Key (Ed25519)** | `NetworkSettings` | the identity Strict pinning (the default) compares the gateway against |

> Both public keys are **public** values and safe to hold in the project. The
> API key is the credential of the four: in the Editor it belongs in the vault
> the wizard writes; a player build reads it from a provider you register with
> `ApiKeySource.SetProvider`, then `--rtmpe-api-key-file`, then `RTMPE_API_KEY`.
> See [Giving a player build its API key](#giving-a-player-build-its-api-key).

---

## 4. The Supported Path — the Setup Wizard

**Window → RTMPE → Setup Wizard** is the shortest route through Steps 2 and 3
below, and it is the first thing an integrator sees: with the SDK installed it
opens by itself once per Editor session, on the first script load that is not
entering Play mode. **Window → RTMPE → Auto-Open Setup Wizard** turns that off
for this project and it stays off across restarts; **Window → RTMPE → Setup
Wizard** reopens it at any time.

Its six steps verify the SDK assemblies, offer to add a `NetworkManager` to the
open scene, and take the four dashboard values from §3. On **Finish** the wizard:

- creates `Assets/RTMPE/NetworkSettings.asset` if the project has none, or
  reuses the first one it finds — that is **Step 2**;
- writes host, port, tick rate onto it always, and both public keys and the
  session-token claims whenever the wizard's fields carry them, so a blank
  field never clears a value pasted onto the asset directly;
- stores the API key in the OS credential vault, which is the only supported
  editor-side store for it.

> ⚠️ **Step 3 is the half you must check.** Adding the `NetworkManager` is a
> button on the wizard's second step and **Finish** does not require it, so a
> run that clicked straight through leaves the scene without one —
> `NetworkManager.Instance` is then `null` at run time. And the asset is bound
> only to a manager the wizard added: if the scene already had one, the wizard
> reports it as found and leaves its `Settings` field alone, where an unbound
> component falls back to the loopback defaults. Look at both before moving on.

Steps 2 and 3 below are the same work done by hand, with the full field
reference; the rest of the guide (Step 4 onward) is game code the wizard does
not write. Read on if you are configuring several deployment profiles, or if
you want to know what the wizard wrote.

---

## Step 1 — Install the SDK

### Option A — Unity Package Manager (Git URL) — recommended

Open your game's `Packages/manifest.json` and add:

```json
{
  "dependencies": {
    "com.rtmpe.sdk": "https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git"
  }
}
```

Or in Unity: **Window → Package Manager → + → Add package from git URL**, paste:

```
https://github.com/rtmpengine/unity-rtmpe-sdk-automation.git
```

#### Upgrading a Git-URL install

⚠️ **Adding the same URL again does nothing.** Unity resolves a Git dependency
once and records the exact commit in `Packages/packages-lock.json`; every later
resolve reuses that commit, so a project installed at an older version stays
there however many times the URL is re-entered.

To move to a newer release, either:

- **Window → Package Manager → RTMPE SDK → Remove**, then add the URL again; or
- delete the `com.rtmpe.sdk` entry from `Packages/packages-lock.json` and let
  Unity re-resolve on the next Editor focus.

Confirm the version in Package Manager before testing anything — a report
written against the version you thought you had costs a whole pass.

### Option B — Local copy

1. Download or clone the SDK repository.
2. Copy the `com.rtmpe.sdk` folder into your project's `Packages/` directory.
3. Unity auto-detects it on the next Editor refresh.

Or reference it by path in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.rtmpe.sdk": "file:../path/to/com.rtmpe.sdk"
  }
}
```

### Verify installation

Open **Window → Package Manager**. You should see:

```
RTMPE SDK   1.0.5   ✓
```

The RTMPE SDK types — under the `RTMPE.Core`, `RTMPE.Rooms`, `RTMPE.Sync`,
`RTMPE.Rpc`, and `RTMPE.Transport` namespaces — are now available in all
scripts.

### About the install URL

The Git URL above points to `rtmpengine/unity-rtmpe-sdk-automation` — a
flat, UPM-installable repository. You do not need any other repository to
install or use the SDK; the Unity Package Manager pulls everything it
needs from that URL. If you prefer a pinned local copy, use Option B
above.

One thing arrives installed but not runnable, and it is not part of using
the SDK either way: the headless conversion and scoring engine travels
inside the package, in a folder Unity does not import, and building it
once needs the .NET 8 SDK. You need it only to run the automated
conversion described in [Automation](automation.md), and that page
covers the one build command.

The UPM package *identifier* stays `com.rtmpe.sdk` regardless of which
repository it is served from — that name is the package's identity, not
its origin, so Unity caches it as `com.rtmpe.sdk@<hash>`. That is expected
and is not a sign that the wrong repository was fetched; to confirm the
origin, check the resolved URL in `Packages/packages-lock.json`.

---

## Step 2 — Create the NetworkSettings Asset

The `NetworkSettings` asset stores all connection configuration for a deployment target.
You can maintain multiple profiles (e.g. `RTMPESettings_Dev.asset`, `RTMPESettings_Prod.asset`).

1. In the **Project** panel: right-click → **Create → RTMPE → Settings**.
2. Name the asset (e.g. `RTMPESettings_Prod.asset`).
3. Configure the fields in the **Inspector**:

| Field                              | Value                                      | Notes                                         |
| ---------------------------------- | ------------------------------------------ | --------------------------------------------- |
| `Server Host`                      | Your RTMPE gateway hostname or IP          | Obtain from the RTMPE dashboard               |
| `Server Port`                      | `7777`                                     | Default UDP port                              |
| `Heartbeat Interval Ms`            | `5000`                                     | 5-second keepalive interval                   |
| `Connection Timeout Ms`            | `10000`                                    | 10-second handshake timeout                   |
| `Tick Rate`                        | `30`                                       | This client's own cadence; server is fixed 30 |
| `Auto Rejoin Last Room On Reconnect` | `true` (default)                          | Auto-rejoin the last room after a successful token-based `Reconnect()` |
| `Send Buffer Bytes`                | `262144` (256 KiB)                         | UDP socket SO_SNDBUF                          |
| `Receive Buffer Bytes`             | `262144` (256 KiB)                         | UDP socket SO_RCVBUF                          |
| `Network Thread Buffer Bytes`      | `65536`                                    | Background thread read buffer. Floored at the largest datagram a UDP path carries; a smaller one loses the largest server replies silently |
| `Enable Debug Logs`                | `true` during development, `false` in production | Unity Console connection traces         |
| `Api Key Seal Server Public Key Hex` | 64-char hex — the gateway's X25519 **Sealed-Box Public Key**, copied from the RTMPE dashboard | **Required**; the handshake seals the API key to this key — no shared secret to distribute |
| `Server Pinning Mode`              | `Strict` (default)                         | Fail-closed — see **Server pinning** below before your first connect |
| `Pinned Server Public Key Hex`     | 64-char hex — copy from the RTMPE dashboard | **Required** in the default `Strict` mode; the connection is refused without it |

> **API-key envelope.** The SDK seals your API key inside the first handshake
> packet. Paste the gateway's X25519 **Sealed-Box Public Key** (from the RTMPE
> dashboard) into `Api Key Seal Server Public Key Hex`; the gateway opens the box
> with its private half. It is a **public** value, so your build carries no
> secret of the gateway's — which matters because a shipped game can be unpacked.
>
> This field is required: it is the only envelope the gateway accepts, and a
> build with it blank cannot connect. It takes a 64-char hex string and is
> distinct from the Ed25519 `Pinned Server Public Key Hex` below — never paste
> the pin into it.

> **Server pinning — read before your first connect.** `Server Pinning Mode`
> defaults to **`Strict`**, a fail-closed posture: the SDK checks the gateway's
> Ed25519 static public key against the pin you supply and **refuses the
> connection when no pin is configured.** The refusal happens during the
> handshake (when the gateway's challenge arrives), so a project that leaves
> `Pinned Server Public Key Hex` blank sees every `Connect()` fail rather than a
> silent insecure fallback. Choose one before connecting:
>
> - **Production (recommended):** paste the gateway's 64-char Ed25519 public key
>   (from the RTMPE dashboard) into `Pinned Server Public Key Hex`; keep the mode
>   on `Strict`.
> - **First-run capture:** set `Server Pinning Mode` to `TrustOnFirstUse` to pin
>   the key seen on the first connection automatically.
> - **Local development only:** `InsecureNoPinning` disables verification — never
>   ship a build with this mode.

> **Security note — where the API key comes from.** There is no API-key field on
> `RTMPESettings`, and there must not be: a serialized field is written into the
> asset, committed with it, and present in every build made from it. Supply the key
> from outside the project with `RTMPE.Core.ApiKeySource`, which reads, in order:
> a provider you register with `ApiKeySource.SetProvider` (the Editor registers the
> credential vault that **Window → RTMPE → Setup Wizard** writes to), the
> `--rtmpe-api-key-file <path>` / `--rtmpe-api-key <key>` command-line options, then
> the `RTMPE_API_KEY` environment variable.
>
> Prefer the file form on any machine with more than one account: `ps` and
> `/proc/<pid>/cmdline` are world-readable, so a key in argv is visible to every
> other user of that host.
>
> None of these hides the key from the person running your game — a credential a
> client presents is a credential that client holds. What they remove is the copy in
> the asset, the copy in your repository's history and the copy in the shipped
> artifact. To keep the project key off the client entirely, hand `SetProvider` a
> short-lived per-player credential your own backend minted — see
> [Giving a player build its API key](#giving-a-player-build-its-api-key).
>
> Still never commit a production `RTMPESettings` asset to a public repository: it
> carries your gateway host and pinned public keys.

### Giving a player build its API key

The wizard stores the key in your OS credential vault and the SDK registers that
vault as a source — **in the Editor**. The registration lives in the Editor
assembly, which no build compiles, so a player that has only ever been given a key
through the wizard has no key at all. Play mode working says nothing about the
build.

A player reads, in the order `ApiKeySource` consults them:

| Source | What it is for |
| --- | --- |
| a provider registered with `ApiKeySource.SetProvider` | **the only one that reaches a player who just double-clicked the game you shipped** |
| a key staged for a **development build** | the standalone build you are testing yourself — see below |
| `--rtmpe-api-key-file <path>` | your own machine, a test rig, CI, a launcher you control |
| `--rtmpe-api-key <key>` | the same, where argv is not shared — `ps` and `/proc` are world-readable |
| `RTMPE_API_KEY` | the same |

The last three are read from the environment and the launch line of the process,
neither of which a player who double-clicked your game has set — so unless you
ship a launcher that sets them, they end at your own desk. What ships is the
provider.

### The standalone build you are testing yourself

Between "play mode works" and "I have written a provider" there is a build you
just want to run once, and none of the sources above reaches it: a
double-clicked application inherits no argument vector and not your shell's
environment.

Open **Window → RTMPE → Setup Wizard**, and on the credential step tick
**Inject this key into development builds**. Nothing is written to your project
by that: the answer is stored in this machine's EditorPrefs. A build made with
**Development Build** ticked then writes the key to
`Assets/StreamingAssets/rtmpe.devkey` while it runs and removes it when it
finishes — Unity copies that folder into the build verbatim, so the player
connects with no code at all, and your project holds no credential between
builds.

⛔ A release build never gets one, two ways over. The runtime reads the file
**only** when `Debug.isDebugBuild` is true, so a release build ignores one even
if it is sitting there; and a release build that finds one **does not build** —
which is what catches a build that crashed between the writing and the removal.
The wizard adds the file to your `.gitignore` when you switch the injection on,
because that residue is the one way it could reach a repository.

⛔ The one thing neither control can refuse is a **development build you hand to
somebody else**: that build is given the key by design. Do not send one to a
tester or a store — build without *Development Build*, and it will not have a
key to leak.

⚠️ On iOS a staged key is a plain file inside the `.ipa`, so a TestFlight build
made while one is staged hands the credential to every tester who unpacks it.
Android keeps streaming assets inside its APK rather than as files, so the staged
key is not readable there and a development build on it uses a provider
like any other player.

**If the key is already in hand.** The provider is asked for a `string`, so it
returns one rather than fetching one:

```csharp
using RTMPE.Core;
using UnityEngine;

public sealed class RtmpeCredential : MonoBehaviour
{
    private void Awake()
    {
        // Register before anything connects. Awake runs before any Start for
        // objects present when the scene loads, so this is early enough with no
        // execution-order setting — provided the script is in the SAME scene as
        // your NetworkManager, on an active object placed there in the editor.
        // ⚠️ It is NOT early enough on an inactive object, on one you
        // Instantiate, or in a scene loaded after the one that connects: a
        // connection attempt that found no key is reported and never retried.
        ApiKeySource.SetProvider(() => MyCredentialStore.RtmpeApiKey);
    }
}
```

**You do not have to type that one out.** The package ships it, as
`TwoPlayerCredentials` in the **Two Player Room** sample (**Window → Package
Manager → RTMPE SDK → Samples**). It is the component above with the registration
made unconditionally in `Awake`, a named static provider rather than a closure so
the line you edit is one you can navigate to, and a report that tells you whether
a key was found — never the key, and never its length, which is a fingerprint of
it. Drop it on the object carrying your `NetworkManager` and call
`TwoPlayerCredentials.Supply` with whatever your own code obtained. It declares no
serialized field at all, which is deliberate: Unity writes a serialized string
into the scene asset, and it does that for a `public` field exactly as readily as
for one carrying `[SerializeField]`.

**If the key comes from your backend.** `Func<string>` returns, it does not await,
and blocking it would stall the frame that is trying to connect. So the fetch
happens first, the provider hands back what it produced, and nothing connects
until it has:

```csharp
using System.Collections;
using RTMPE.Core;
using UnityEngine;
using UnityEngine.Networking;

// Named apart from the component above because they are two answers to one
// question, not two halves of an arrangement — a project wants one of them.
// Save each in a file named after its class, as Unity requires.
public sealed class RtmpeFetchedCredential : MonoBehaviour
{
    [SerializeField] private string _tokenEndpoint = "https://your-backend.example/rtmpe/key";

    private string _key;

    private void Awake() => ApiKeySource.SetProvider(() => _key);

    private IEnumerator Start()
    {
        using (var request = UnityWebRequest.Get(_tokenEndpoint))
        {
            // ⛔ The endpoint must authenticate the PLAYER. A URL that hands the
            // credential to anyone who calls it is inside the built player
            // exactly as a typed-in key would be, and mints keys for strangers
            // besides. Send whatever your game already signs its player in with.
            request.SetRequestHeader("Authorization", "Bearer " + MyAccount.SessionToken);

            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[MyGame] RTMPE credential request failed: " + request.error);
                yield break;
            }

            _key = request.downloadHandler.text.Trim();
        }

        // Guarded because the throw would land inside a coroutine after a
        // successful fetch — the least obvious moment it could pick. Instance
        // answers null with no manager in the scene, while quitting, and off the
        // main thread; TryGetInstance is the probe that says so without logging.
        if (!NetworkManager.TryGetInstance(out var net))
        {
            Debug.LogError("[MyGame] No NetworkManager in the scene; nothing will connect.");
            yield break;
        }

        net.Connect(_key);
    }
}
```

If you use `RtmpeConnectionBootstrap` rather than connecting by hand, clear its
**Connect On Start** box and call its `Connect()` here instead — see
[Connecting, without writing any](api/index.md#connecting-without-writing-any).
Put this script on the same GameObject: the bootstrap belongs on a root object in
the boot scene and makes itself persistent, and Unity cannot serialise a
reference to it from another scene.

⛔ What your backend releases is still **your project's API key**, withheld from
anyone it has not authenticated. A short-lived per-player credential is a
different thing and the wire does not carry one yet; nothing here pretends
otherwise, and the exposure this removes is the copy in the asset, the copy in
your repository's history and the copy in the shipped player.

**When a source fails rather than having nothing.** A provider that throws, and
`--rtmpe-api-key-file` naming a path that cannot be read, holds no key, or lost
its value to an unset variable, are both reported as failures: resolution stops
there rather than connecting with a credential you did not choose — naming a file
and silently getting the key from somewhere else is the one outcome naming a file
exists to exclude. Only *having nothing to give* falls through: a provider
returning `null` or `""`, an option that is simply absent, an unset variable.
`ApiKeySource.LastError` carries the failure.

⚠️ **`RtmpeConnectionBootstrap` asks the provider again on every connect it
makes for you.** It opens a fresh session after a drop the reconnect token cannot
repair, and that re-resolves the provider — so keep the fetch in a method you can
re-run rather than a value written once, or a credential that has since expired is
handed back until the component's fresh-session budget is spent. A hand-written
`Connect(key)` is the other case: nothing re-asks, and reconnecting with a
credential that has aged out is yours to notice.

⚠️ In the Editor your registration and the wizard's vault are not rivals: the
vault is ranked below yours, so yours wins whenever it has a key, and a play
session where it has nothing — a backend you cannot reach from your desk, a store
you have not populated — still finds the key the wizard stored. The vault sits
above the command line and `RTMPE_API_KEY`, so with both set the wizard's key is
the one Play mode uses; the vault is per project, so it is empty until you have
run the wizard in this one.

---

## Step 3 — Add NetworkManager to the Scene

1. Create an empty **GameObject** in your **first / boot scene**.
2. Name it `[RTMPE] NetworkManager`.
3. Add the `NetworkManager` component (**Component → RTMPE → NetworkManager**).
4. In the Inspector, drag your `RTMPESettings_Prod.asset` into the **Settings** field.

```
Hierarchy (boot scene):
  ├── [RTMPE] NetworkManager   ← add here only
  └── ... (other boot objects)
```

> **Important:** `NetworkManager` calls `DontDestroyOnLoad()` automatically — it persists
> across all scene loads. **Do not add a second NetworkManager in any other scene** or
> you will get duplicate-singleton warnings.

---

## Step 4 — Convert Scripts to NetworkBehaviour

Every GameObject whose state must be visible to all players must derive from
`NetworkBehaviour` instead of `MonoBehaviour`.

### Before (single-player)

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

### After (multiplayer)

```csharp
using System;
using UnityEngine;
using RTMPE.Core;   // NetworkBehaviour, NetworkManager
using RTMPE.Sync;   // NetworkVariable types

[RequireComponent(typeof(NetworkTransform))]          // ← required
public class PlayerController : NetworkBehaviour      // ← changed from MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 5f;

    private NetworkVariableInt   _health;
    private NetworkVariableFloat _score;

    // Store handler references for reliable unsubscription in OnNetworkDespawn.
    // Anonymous lambdas create a new delegate each call — -= (o,n)=>{} removes nothing.
    private Action<int, int> _onHealthChanged;

    // Called by RTMPE when this object is registered on the network.
    // Initialize all NetworkVariables here — NOT in Awake/Start.
    // Use 'protected override' to match the base class modifier (avoids CS0507).
    protected override void OnNetworkSpawn()
    {
        // The identity is derived from the member's name — pass nameof(field).
        _health = new NetworkVariableInt(this, nameof(_health), initialValue: 100);
        _score  = new NetworkVariableFloat(this, nameof(_score), initialValue: 0f);

        // Store the reference BEFORE subscribing so OnNetworkDespawn can remove it.
        _onHealthChanged = (oldHp, newHp) =>
        {
            Debug.Log($"[{name}] HP: {oldHp} → {newHp}");
            if (newHp <= 0) HandleDeath();
        };
        _health.OnValueChanged += _onHealthChanged;
    }

    // Called before this network object is removed from the network.
    protected override void OnNetworkDespawn()
    {
        if (_health != null) _health.OnValueChanged -= _onHealthChanged;
    }

    private void Update()
    {
        // ──────────────────────────────────────────────────────────────────────
        // CRITICAL RULE: Only the INPUT owner moves the character.
        // Other clients receive the position automatically via NetworkTransform.
        // ──────────────────────────────────────────────────────────────────────
        if (!IsOwner) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        transform.position += new Vector3(h, 0f, v) * _moveSpeed * Time.deltaTime;
        // NetworkTransform sends the position update to the server at 30 Hz automatically.
    }

    // Only the owner sets this value; all other clients receive it via OnValueChanged.
    public void TakeDamage(int amount)
    {
        if (!IsOwner) return;
        if (_health == null) return;
        _health.Value = Mathf.Max(0, _health.Value - amount);
    }

    private void HandleDeath()
    {
        // Runs on ALL clients: the owner raises it on assignment, and every
        // receiving client raises it when the update is applied from the wire.
        Debug.Log($"[{name}] eliminated.");
    }
}
```

### The `IsOwner` rule

| Context | `IsOwner` |
| ------- | --------- |
| The player on their own machine | `true` |
| The same player viewed on any other machine | `false` |

**Only the owner should:**
- Read `Input.*`
- Move the character
- Write `NetworkVariable.Value`

**All clients receive automatically:**
- Position and rotation via `NetworkTransform`
- Variable changes via `NetworkVariable.OnValueChanged`

---

## Step 5 — Set Up the Networked Prefab

Attach these components to every prefab that needs to be visible across the network:

```
PlayerPrefab (GameObject)
  ├── PlayerController.cs              ← your script (extends NetworkBehaviour)
  ├── NetworkTransform.cs              ← Runtime/Sync/
  ├── NetworkTransformInterpolator.cs  ← Runtime/Sync/
  └── (any other existing components)
```

### NetworkTransform Inspector settings

| Field                | Recommended | Notes                                      |
| -------------------- | ----------- | ------------------------------------------ |
| `Sync Position`      | ✅ true     | Whether the network may write world-space position — both directions: an unsynced axis neither broadcasts nor is written by a peer |
| `Sync Rotation`      | ✅ true     | The same, for rotation |
| `Sync Scale`         | ❌ false    | Enable only if the object changes scale    |
| `Position Threshold` | `0.01`      | Minimum movement in metres before sending  |
| `Rotation Threshold` | `0.1`       | Minimum rotation in degrees before sending |

### NetworkTransformInterpolator Inspector settings

| Field                | Recommended | Notes                                           |
| -------------------- | ----------- | ----------------------------------------------- |
| `Buffer Size`        | `10`        | Number of state snapshots to buffer             |
| `Interpolation Delay`| `0.1`       | 100 ms lag buffer — smooths jitter              |
| `Interpolate Scale`  | ❌ false    | Match your `Sync Scale` setting                 |

> The interpolator runs on **all clients** to smooth the movement of remote players.

---

## Step 6 — Synchronize State with NetworkVariables

Use `NetworkVariable<T>` for any value that all players must see simultaneously.

### Available types

| Class                        | Type         | Size      |
| ---------------------------- | ------------ | --------- |
| `NetworkVariableInt`         | `int`        | 4 bytes   |
| `NetworkVariableFloat`       | `float`      | 4 bytes   |
| `NetworkVariableBool`        | `bool`       | 1 byte    |
| `NetworkVariableVector2`     | `Vector2`    | 8 bytes   |
| `NetworkVariableVector2Int`  | `Vector2Int` | 8 bytes (**int32**, exact at every magnitude) |
| `NetworkVariableVector3`     | `Vector3`    | 12 bytes  |
| `NetworkVariableQuaternion`  | `Quaternion` | 16 bytes  |
| `NetworkVariableString`      | `string`     | variable (UTF-8) |

### Rules

1. **Initialize in `OnNetworkSpawn()`** — never in `Awake()` or `Start()`.
2. **The identity is derived from the object's concrete type and the name the
   variable is constructed with.** Pass `nameof(_field)`. Two constructions
   naming one member collide — and so does a base class and a derived class each
   declaring a member of the same name, because on an instance of the derived
   class both are that one concrete type.
   The wire carries the id and nothing else, so components on one prefab share a
   single ID namespace; a collision throws out of `OnNetworkSpawn`, and the
   object is destroyed rather than spawned — on every client, deterministically.
3. **Only the owner writes `Value`** — and this is now enforced rather than
   advised. A write from a client that does not own the object is refused, with
   a console warning: the flush skips every component the local player does not
   own, so accepting it would store the value here, announce it to local
   subscribers, and send it to nobody. All clients read and react via
   `OnValueChanged`; to change a value you do not own, send the owner an RPC or
   ask for the object with `OwnershipManager.RequestOwnershipTransfer`.
4. Variables are flushed to the server at **30 Hz** automatically.

### Example

```csharp
using System;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

public class MyCharacter : NetworkBehaviour
{
    private NetworkVariableInt    _health;
    private NetworkVariableInt    _score;
    private NetworkVariableString _displayName;
    private NetworkVariableBool   _isAlive;

    private Action<int, int>       _onHealthChanged;
    private Action<bool, bool>     _onAliveChanged;
    private Action<string, string> _onNameChanged;

    protected override void OnNetworkSpawn()
    {
        _health      = new NetworkVariableInt(this,    nameof(_health), initialValue: 100);
        _score       = new NetworkVariableInt(this,    nameof(_score), initialValue: 0);
        _displayName = new NetworkVariableString(this, nameof(_displayName), initialValue: "Player");
        _isAlive     = new NetworkVariableBool(this,   nameof(_isAlive), initialValue: true);

        _onHealthChanged = (old, next) => UpdateHealthBar(next);
        _onAliveChanged  = (old, next) => OnAliveStateChanged(next);
        _onNameChanged   = (old, next) => UpdateNameTag(next);

        _health.OnValueChanged      += _onHealthChanged;
        _isAlive.OnValueChanged     += _onAliveChanged;
        _displayName.OnValueChanged += _onNameChanged;
    }

    protected override void OnNetworkDespawn()
    {
        if (_health      != null) _health.OnValueChanged      -= _onHealthChanged;
        if (_isAlive     != null) _isAlive.OnValueChanged     -= _onAliveChanged;
        if (_displayName != null) _displayName.OnValueChanged -= _onNameChanged;
    }

    private void UpdateHealthBar(int hp)       { /* update UI */ }
    private void OnAliveStateChanged(bool alive) { /* play animation */ }
    private void UpdateNameTag(string name)    { /* update label */ }
}
```

---

## Step 7 — Create a GameManager

The `GameManager` orchestrates the full lifecycle: connect → create/join room → spawn player.
Place it on a persistent GameObject in your boot scene.

```csharp
using System;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Rooms;

public class GameManager : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    // The API key is not a field here. It is resolved at Start() from a
    // source outside the build — see Step 2's security note.

    [Header("Room")]
    [SerializeField] private string _roomName   = "My Game Room";
    [Range(1, 100)]                       // the platform ceiling; outside it the request is refused
    [SerializeField] private int    _maxPlayers = 4;
    [SerializeField] private bool   _autoCreate = true;  // true = auto-create; false = show lobby list

    [Header("Spawn")]
    [SerializeField] private GameObject _playerPrefab;
    [SerializeField] private uint       _playerPrefabId = 1;   // must be identical on every client
    [SerializeField] private Vector3    _spawnPosition  = new Vector3(0f, 1f, 0f);

    // ── Private ──────────────────────────────────────────────────────────────

    private NetworkBehaviour _localPlayer;

    // Store references so we can unsubscribe precisely in OnDestroy.
    private Action                   _onConnectedHandler;
    private Action<DisconnectReason> _onDisconnectedHandler;
    private Action<string>           _onConnectionFailedHandler;
    private Action<RoomInfo>         _onRoomCreatedHandler;
    private Action<RoomInfo>         _onRoomJoinedHandler;
    private Action                   _onRoomLeftHandler;
    private Action<string>           _onRoomErrorHandler;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void Start()
    {
        if (!ApiKeySource.TryResolve(out string apiKey))
        {
            Debug.LogError(Application.isEditor
                ? "[GameManager] No API key. Store one via Window → RTMPE → Setup Wizard, "
                  + "launch with --rtmpe-api-key-file <path>, or set RTMPE_API_KEY."
                : "[GameManager] No API key. A build carries no Editor vault, so supply "
                  + "one: ApiKeySource.SetProvider, --rtmpe-api-key-file <path>, or "
                  + "RTMPE_API_KEY.");
            return;
        }

        if (_playerPrefab == null)
        {
            Debug.LogError("[GameManager] Player prefab is not assigned.");
            return;
        }

        var net = NetworkManager.Instance;

        // Assign stored references before subscribing.
        _onConnectedHandler        = OnConnected;
        _onDisconnectedHandler     = OnDisconnected;
        _onConnectionFailedHandler = OnConnectionFailed;
        _onRoomCreatedHandler      = room => OnRoomEntered(room);
        _onRoomJoinedHandler       = room => OnRoomEntered(room);
        _onRoomLeftHandler         = OnRoomLeft;
        _onRoomErrorHandler        = OnRoomError;

        net.OnConnected          += _onConnectedHandler;
        net.OnDisconnected       += _onDisconnectedHandler;
        net.OnConnectionFailed   += _onConnectionFailedHandler;
        net.Rooms.OnRoomCreated  += _onRoomCreatedHandler;
        net.Rooms.OnRoomJoined   += _onRoomJoinedHandler;
        net.Rooms.OnRoomLeft     += _onRoomLeftHandler;
        net.Rooms.OnRoomError    += _onRoomErrorHandler;

        net.Connect(apiKey);
    }

    private void OnDestroy()
    {
        var net = NetworkManager.Instance;
        if (net == null) return;

        net.OnConnected          -= _onConnectedHandler;
        net.OnDisconnected       -= _onDisconnectedHandler;
        net.OnConnectionFailed   -= _onConnectionFailedHandler;
        net.Rooms.OnRoomCreated  -= _onRoomCreatedHandler;
        net.Rooms.OnRoomJoined   -= _onRoomJoinedHandler;
        net.Rooms.OnRoomLeft     -= _onRoomLeftHandler;
        net.Rooms.OnRoomError    -= _onRoomErrorHandler;
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private void OnConnected()
    {
        Debug.Log("[GameManager] Connected.");

        // Register the player prefab. The prefab table persists across
        // reconnects, so registering here (or once in Awake/Start) is equally
        // fine — the registration is carried onto the SpawnManager that each
        // connection rebuilds.
        NetworkManager.Instance.Spawner.RegisterPrefab(_playerPrefabId, _playerPrefab);

        if (_autoCreate)
        {
            NetworkManager.Instance.Rooms.CreateRoom(new CreateRoomOptions
            {
                Name       = _roomName,
                MaxPlayers = _maxPlayers,
                IsPublic   = true,
            });
        }
        else
        {
            // Populate a room-list UI instead.
            NetworkManager.Instance.Rooms.ListRooms(publicOnly: true);
        }
    }

    private void OnRoomEntered(RoomInfo room)
    {
        Debug.Log($"[GameManager] Room: {room.Name}  code: {room.RoomCode}  " +
                  $"{room.PlayerCount}/{room.MaxPlayers} players");

        // CreateRoom with AutoJoinAsHost=true (the default) raises BOTH
        // OnRoomCreated and OnRoomJoined, so guard against a double spawn.
        if (_localPlayer != null) return;

        _localPlayer = NetworkManager.Instance.Spawner.Spawn(
            _playerPrefabId,
            _spawnPosition,
            Quaternion.identity);

        if (_localPlayer == null)
            Debug.LogError("[GameManager] Spawn returned null — verify prefab registration.");
    }

    private void OnRoomLeft()
    {
        Debug.Log("[GameManager] Left room.");
        _localPlayer = null;
    }

    private void OnDisconnected(DisconnectReason reason)
    {
        Debug.Log($"[GameManager] Disconnected — {reason}");
        _localPlayer = null;

        UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
    }

    private void OnConnectionFailed(string reason)
    {
        Debug.LogError($"[GameManager] Connection failed: {reason}");
    }

    private void OnRoomError(string error)
    {
        Debug.LogError($"[GameManager] Room error: {error}");
    }

    // ── Public (call from UI buttons) ────────────────────────────────────────

    public void JoinRoom(string roomId)
    {
        NetworkManager.Instance.Rooms.JoinRoom(roomId, new JoinRoomOptions
        {
            DisplayName = "Player",
        });
    }

    public void JoinRoomByCode(string roomCode)
    {
        NetworkManager.Instance.Rooms.JoinRoomByCode(roomCode, new JoinRoomOptions
        {
            DisplayName = "Player",
        });
    }

    public void LeaveRoom()   => NetworkManager.Instance.Rooms.LeaveRoom();
    public void Disconnect()  => NetworkManager.Instance.Disconnect();
}
```

---

## Step 8 — Room List UI

When `_autoCreate = false`, call `ListRooms()` and show the results in a UI panel.

```csharp
using UnityEngine;
using UnityEngine.UI;
using RTMPE.Core;
using RTMPE.Rooms;

public class RoomListUI : MonoBehaviour
{
    [SerializeField] private Transform  _container;       // parent for room entry prefabs
    [SerializeField] private GameObject _entryPrefab;     // prefab with Text + Join Button
    [SerializeField] private InputField _codeInput;       // optional: direct join by code

    private void OnEnable()
    {
        NetworkManager.Instance.Rooms.OnRoomListReceived += Populate;
    }

    private void OnDisable()
    {
        if (NetworkManager.HasInstance)
            NetworkManager.Instance.Rooms.OnRoomListReceived -= Populate;
    }

    private void Populate(RoomInfo[] rooms)
    {
        foreach (Transform child in _container)
            Destroy(child.gameObject);

        foreach (var room in rooms)
        {
            var entry = Instantiate(_entryPrefab, _container);

            // Use TMP_Text (add 'using TMPro;') instead of Text for Unity 6 TMP projects.
            entry.GetComponentInChildren<Text>().text =
                $"{room.Name}  [{room.PlayerCount}/{room.MaxPlayers}]  #{room.RoomCode}";

            var roomIdCopy = room.RoomId;
            entry.GetComponentInChildren<Button>().onClick.AddListener(() =>
                NetworkManager.Instance.Rooms.JoinRoom(roomIdCopy));
        }
    }

    public void JoinByCode()
    {
        var code = _codeInput?.text?.Trim();
        if (!string.IsNullOrEmpty(code))
            NetworkManager.Instance.Rooms.JoinRoomByCode(code);
    }

    public void Refresh() => NetworkManager.Instance.Rooms.ListRooms(publicOnly: true);
}
```

---

## Step 9 — Handle Player Join and Leave Events

```csharp
private void SubscribeToPlayerEvents()
{
    NetworkManager.Instance.Rooms.OnPlayerJoined += OnPlayerJoined;
    NetworkManager.Instance.Rooms.OnPlayerLeft   += OnPlayerLeft;
}

private void OnPlayerJoined(PlayerInfo player)
{
    Debug.Log($"Player joined: {player.DisplayName} (id={player.PlayerId})");
    // Update head-count UI, play join sound, etc.
}

private void OnPlayerLeft(string playerId)
{
    Debug.Log($"Player left: {playerId}");
    // Objects spawned by that player with DestroyWithOwner = true
    // are destroyed automatically on all remaining clients.
}
```

### DestroyWithOwner behaviour

When a player disconnects, any networked object they spawned with
`DestroyWithOwner = true` (the default) is automatically despawned on all clients.
No extra code is required.

```csharp
// Inside OnNetworkSpawn() or Awake() on your NetworkBehaviour:
// DestroyWithOwner is a settable property (not virtual) — do NOT use override.
DestroyWithOwner = false;   // keep object alive after the owner disconnects

// The default is true — if you want the default behaviour, do nothing.
```

---

## Step 10 — Disconnection and Cleanup

```csharp
private void OnDisconnected(DisconnectReason reason)
{
    _localPlayer = null;
    UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
}

// From a Quit button:
public void QuitToMainMenu()
{
    NetworkManager.Instance.Disconnect();
    // OnDisconnected will fire — handle the scene transition there.
}
```

---

## Step 11 — Reconnect after a Drop

After the first successful connection, the SDK receives a **reconnect token**
that can resume the session without re-sending the API key. Transient drops
(heartbeat timeout, WiFi → 4G handoff) preserve the token; explicit
`Disconnect()` calls wipe it.

### Check whether a reconnect is possible

> **You may not have to write this.** `Component → RTMPE → Connection Bootstrap`
> (`RtmpeConnectionBootstrap`) already does exactly what follows — it watches
> `OnDisconnected`, tries `Reconnect()`, and falls back to a full connect when
> the token is gone. The `BasicConnection` sample shows the hand-written form
> below, for a project that wants the decision in its own code.

```csharp
private void OnDisconnected(DisconnectReason reason)
{
    if (NetworkManager.Instance.CanReconnect)
    {
        // Token is still valid — try the shortcut reconnect flow.
        ShowReconnectingUi();
        NetworkManager.Instance.Reconnect();
    }
    else
    {
        // Token is gone (explicit logout, handshake failure, server close).
        // Ask for credentials and Connect(apiKey) from scratch.
        UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
    }
}
```

### Auto room re-join

When `NetworkSettings.autoRejoinLastRoomOnReconnect` is `true` (the default),
the SDK automatically calls `Rooms.JoinRoom(LastRoomId)` after a successful
`Reconnect()`. Subscribe to the new event to update UI:

```csharp
private void Start()
{
    var net = NetworkManager.Instance;

    net.OnAutoRejoinAttempt += OnAutoRejoinAttempt;
    net.Rooms.OnRoomJoined  += OnRoomEntered;     // fires on both manual and auto rejoin
    net.Rooms.OnRoomError   += OnRoomError;       // fires if the room no longer exists
}

private void OnAutoRejoinAttempt(string roomId)
{
    Debug.Log($"[GameManager] Auto-rejoining room {roomId}…");
    // Update UI, e.g. show a "Restoring session…" spinner.
}
```

If your app wants custom lobby UI instead, disable the setting and use the
preserved `LastRoomId` / `LastRoomCode` to drive your own flow:

```csharp
if (!string.IsNullOrEmpty(NetworkManager.Instance.LastRoomId))
{
    // Offer a "Rejoin last room?" prompt to the user.
    ShowRejoinPrompt(NetworkManager.Instance.LastRoomId,
                     NetworkManager.Instance.LastRoomCode);
}
```

### Lifetime of the last-room snapshot

The last-room snapshot shares its lifetime with the reconnect token — both
are preserved together only when the SDK is confident the session is still
server-side valid.

| Event                                                   | `LastRoomId` state |
|---------------------------------------------------------|--------------------|
| Successful `OnRoomJoined` / `OnRoomCreated`             | **Set** to that room |
| 3 missed `HeartbeatAck` (`ConnectionLost`, recoverable) | **Preserved**        |
| `Rooms.LeaveRoom()` succeeds                            | **Cleared**          |
| `NetworkManager.Disconnect()` (`ClientRequest`)         | **Cleared**          |
| Server-initiated `Disconnect` (`ServerRequest`)         | **Cleared**          |
| First-connect handshake timeout (`Timeout`)             | **Cleared**          |
| `Reconnect()` timeout, no validated `Challenge` (`Timeout`) | **Preserved**    |
| Pin configuration refuses while reconnecting (`ProtocolError`)| **Preserved**       |
| Transport `SocketException` (`ConnectionLost`, non-recoverable) | **Cleared** |
| Server kick (`Kicked`)                                  | **Cleared**          |

---

## Step 12 — Object Pooling (Optional)

For games that spawn/despawn frequently (bullets, hit FX, short-lived props),
install an `INetworkObjectPool` to eliminate the GC pressure of repeated
`Instantiate` / `Destroy` calls.

### Minimal pool example

```csharp
using System.Collections.Generic;
using UnityEngine;
using RTMPE.Core;

public sealed class SimplePool : INetworkObjectPool
{
    private readonly Dictionary<uint, Queue<GameObject>> _buckets =
        new Dictionary<uint, Queue<GameObject>>();

    public GameObject Acquire(uint prefabId, GameObject prefab,
                              Vector3 position, Quaternion rotation)
    {
        if (_buckets.TryGetValue(prefabId, out var q) && q.Count > 0)
        {
            var go = q.Dequeue();
            go.transform.SetPositionAndRotation(position, rotation);
            go.SetActive(true);
            return go;
        }
        return Object.Instantiate(prefab, position, rotation);
    }

    public void Release(uint prefabId, GameObject instance)
    {
        if (prefabId == uint.MaxValue) { Object.Destroy(instance); return; }

        instance.SetActive(false);
        if (!_buckets.TryGetValue(prefabId, out var q))
            _buckets[prefabId] = q = new Queue<GameObject>();
        q.Enqueue(instance);
    }
}
```

### Installing the pool

```csharp
private void OnConnected()
{
    var spawner = NetworkManager.Instance.Spawner;
    spawner.RegisterPrefab(_playerPrefabId, _playerPrefab);
    spawner.SetObjectPool(new SimplePool());   // install the pool
    // …CreateRoom / JoinRoom as usual
}
```

### Important notes

- Install the pool **inside `OnConnected()`**, not before `Connect()`. A fresh
  `SpawnManager` is created on every `Connect()`/`Reconnect()`. Event
  subscriptions on `Rooms`, `Lobby` and `Matchmaking` do *not* need this — they
  are carried across the rebuild — but the pool is not, and neither is anything
  else you set on `Spawner` besides registered prefabs.
- When the pool is absent (`spawner.ObjectPool == null`), `SpawnManager` falls
  back to `Object.Instantiate` / `Object.Destroy`.
- The `prefabId` argument passed to `Release` matches the one the object was
  acquired with. `uint.MaxValue` is a sentinel meaning "the SDK lost track —
  please destroy the instance".

---

## Step 13 — Beyond the Basics

Steps 1–12 cover the full path to a working multiplayer game. The SDK also ships
these first-class features; each is fully specified in the
[API Reference](api/index.md).

- **Physics sync** — ⛔ `NetworkRigidbody` / `NetworkRigidbody2D` are shipped but
  **not driven end to end**: the gateway validates their frames and drops them,
  because nothing on the server ingests rigidbody state yet. For a
  physics-driven object today, use `NetworkTransform` and set the non-owner's
  `Rigidbody` to kinematic. See
  [NetworkRigidbody](api/index.md#networkrigidbody--networkrigidbody2d) for what
  is implemented and what is still owed.

- **Synchronised lists** — `NetworkVariableListInt/Float/Vector3/String` for
  replicated collections (inventory, buffs, kill feed), with an `OnListChanged`
  event. Declared like any `NetworkVariable`, on any `NetworkBehaviour` of the
  object — every component is flushed by the owner and applied on receivers. See [NetworkVariable types](api/index.md#networkvariable-types).

- **Per-variable send rate** — annotate a variable with
  `[NetworkVariable(SendRateHz = 10f)]` to throttle high-churn values (health,
  ammo) below the 30 Hz tick.

- **Networked scene loading** — the master client calls
  `NetworkManager.Instance.Scene.LoadScene(name, mode)`; every client receives
  `OnSceneLoadStartedWithMode`, loads with Unity's `SceneManager`, then calls
  `ReportReady()`, and all get `OnAllPlayersSceneLoaded`. Handle
  `OnSceneLoadStartedWithMode` rather than the older `OnSceneLoadStarted`: it
  carries the load mode, and applying an additive load as a single one unloads
  the scene the room was still in. See
  [Networked scenes](api/index.md#networked-scenes).

  Or attach **`RtmpeSceneLoader`** to a root GameObject in your boot scene — the
  scene the room never loads — and skip that middle paragraph entirely: it subscribes, loads in the mode the room asked for, and
  reports — which is the same fifteen lines every project writes. It stays
  optional by attachment, because a title that wants a loading screen or a
  staged load is not doing the common thing and should keep doing its own. See
  [Scene loading, without writing any](api/index.md#scene-loading-without-writing-any)
  and the **Scene Transitions** sample.

  Either way the scene has to be in **Build Settings**, and nothing between
  typing the name and every client failing to load it checks that.
  **Window → RTMPE → Network Scenes** does: it shows the build list — unticked
  entries, missing assets and duplicate names included — and compares the scene
  names your scripts pass to `LoadScene` against it. ⚠️ It reads names *as
  written*: a literal is checked, a name built at runtime is counted as
  unchecked rather than passed over, and the window says how many of each it
  found.

- **Interest management** — attach `InterestManager` and assign the local
  player's `TrackedTransform` to let the gateway spatially cull broadcasts in a
  large world. See [Interest management](api/index.md#interest-management).

- **Master client** — `NetworkManager.Instance.IsMasterClient` for host
  authority; `Rooms.TransferMasterClient(...)` reassigns the host and
  `Rooms.OnMasterClientChanged` observes it (both on `NetworkManager.Instance.Rooms`).

- **Custom messages** — `NetworkManager.Instance.Send(bytes, reliable)` for an
  application-defined channel; receive via the `OnDataReceived` event.

- **Server RPC with a reply** — `await NetworkManager.Instance
  .SendEnhancedRpcAsync(this, nameof(Method), args)` for a `RpcTarget.Server`
  method that returns an `RpcResponse`.

---

## Complete API Reference

### NetworkManager (singleton)

```csharp
// Access
NetworkManager.Instance          // returns null after OnApplicationQuit
NetworkManager.HasInstance       // thread-safe null check

// Transport factory (static, install before Connect())
NetworkManager.SetTransportFactory(settings => new MyTransport(settings));
NetworkManager.ClearTransportFactory();
NetworkManager.HasCustomTransportFactory;

// Connection
void Connect(string apiKey)
bool Reconnect()                 // shortcut reconnect via stored token
void Disconnect()

// State
NetworkState State               // Disconnected / Connecting / Connected / InRoom / Disconnecting / Reconnecting
bool IsConnected                 // true when Connected or InRoom
bool IsInRoom                    // true when inside a room

// Identity & tokens
ulong  LocalPlayerId             // numeric session ID (valid after SessionAck)
string LocalPlayerStringId       // room player UUID (valid after JoinRoom/CreateRoom)
RedactedString JwtToken          // EdDSA (Ed25519) JWT; call .Reveal() for the raw bearer (Room Service REST API)
RedactedString ReconnectToken    // reconnect token; call .Reveal() for the raw value (valid until consumed / cleared)
bool   CanReconnect              // true when a reconnect token is held

// Last-room snapshot
string LastRoomId                // RoomInfo.RoomId — survives token-preserving clear
string LastRoomCode              // RoomInfo.RoomCode — same lifetime as LastRoomId

// Round-trip time
float  LastRttMs                 // in ms; -1 before first heartbeat

// Sub-managers
RoomManager   Rooms
SpawnManager  Spawner

// Events
event Action                              OnConnected
event Action<DisconnectReason>            OnDisconnected
event Action<string>                      OnConnectionFailed
event Action<NetworkState, NetworkState>  OnStateChanged
event Action<float>                       OnRttUpdated
event Action<byte[]>                      OnDataReceived
event Action                              OnDataAcknowledged
event Action<string>                      OnAutoRejoinAttempt
event Action<int>                         OnReconnectFailed     // bounded reconnect loop exhausted maxReconnectAttempts (arg = attempts made)
```

### RoomManager (`NetworkManager.Rooms`)

```csharp
// Operations
void CreateRoom(CreateRoomOptions options = null)
void JoinRoom(string roomId, JoinRoomOptions options = null)
void JoinRoomByCode(string roomCode, JoinRoomOptions options = null)
void LeaveRoom()
void ListRooms(bool publicOnly = true)

// State
RoomInfo CurrentRoom             // null when not in a room
bool IsInRoom

// Events
event Action<RoomInfo>    OnRoomCreated
event Action<RoomInfo>    OnRoomJoined
event Action              OnRoomLeft
event Action<PlayerInfo>  OnPlayerJoined
event Action<string>      OnPlayerLeft          // receives playerId
event Action<RoomInfo[]>  OnRoomListReceived
event Action<string>      OnRoomError
```

### CreateRoomOptions

```csharp
new CreateRoomOptions
{
    Name       = "My Room",   // display name (max 64 chars)
    MaxPlayers = 4,            // 1–100; 0 = server default (100)
    IsPublic   = true,         // visible in ListRooms results
}
```

### JoinRoomOptions

```csharp
new JoinRoomOptions
{
    DisplayName = "Alice",   // visible name in the room (max 32 chars)
}
```

### SpawnManager (`NetworkManager.Spawner`)

```csharp
// Prefab registrations persist across reconnects, so this may be called once
// in Awake/Start, or in OnConnected — register before the first Spawn().
void RegisterPrefab(uint prefabId, GameObject prefab)
bool UnregisterPrefab(uint prefabId)
bool HasPrefab(uint prefabId)
bool TryGetPrefabId(GameObject prefab, out uint prefabId)

// Call Spawn after OnRoomCreated / OnRoomJoined fires.
// ownerPlayerId defaults to NetworkManager.LocalPlayerStringId when null; a
// client may only spawn under its own identity, so naming another player is
// refused and returns null.  Hand an object over with
// OwnershipManager.RequestOwnershipTransfer instead.
NetworkBehaviour Spawn(uint prefabId, Vector3 position, Quaternion rotation, string ownerPlayerId = null,
                       bool sharedAuthority = false)

// Pass the NetworkObjectId (ulong), not the component reference.
// Only the object's owner may despawn it; anyone else's call is refused with a
// console error and destroys nothing.  CanDespawn / TryDespawn ask the same
// question without a log line.
void Despawn(ulong networkObjectId)
bool CanDespawn(ulong networkObjectId)
bool TryDespawn(ulong networkObjectId)

// Object pool (optional; see Step 12).
void SetObjectPool(INetworkObjectPool pool)
void ClearObjectPool()
INetworkObjectPool ObjectPool { get; }

// Auto-called on RoomManager.OnPlayerJoined. Re-flags every owned
// NetworkVariable so late joiners receive a full state snapshot within
// one 30 Hz tick. Apps rarely need to call this directly.
void MarkAllVariablesDirtyForResync()
```

> **Prefab ID rule:** The same `prefabId` (e.g. `1`) **must** map to the same prefab
> on every client. Register identically across all clients.

#### Keeping the ids straight — `Window > RTMPE > Network Prefabs`

`RegisterPrefab` takes a number you choose, and nothing in the SDK checks that
two people chose differently. That goes wrong two ways. One is in
[Troubleshooting](troubleshooting.md#symptom-a-spawned-objects-isowner-is-always-false-the-player-never-moves):
nothing registered under an id, where `Spawn` logs the id and returns `null`.
The other — two builds disagreeing about which prefab an id names — is what the
window below exists to prevent.

The **Network Prefabs** window keeps the answer in a file instead of in your
head. It records `asset GUID → prefab id` in `rtmpe-prefabs.json` at the project
root, allocates the next free number on request, and writes two things from one
button: a `RtmpePrefabIds` class of constants, and
`Assets/RTMPE/Generated/RtmpePrefabRegistry.asset`.

Assign that asset to **`NetworkSettings.prefabRegistry`** and the registration
call disappears — every prefab in the ledger that still resolves to an asset is
registered when a session is built, before your first `Connect()` and again on
every reconnect:

```csharp
// With a registry assigned, this is the whole of it:
Spawner.Spawn(RtmpePrefabIds.Player, Vector3.zero, Quaternion.identity);

// Without one, or for a prefab the ledger does not know about — an asset bundle,
// something built at runtime — register it yourself. A hand registration is
// applied after the asset, so it wins:
Spawner.RegisterPrefab(RtmpePrefabIds.Player, playerPrefab);
```

⚠️ **Every prefab the registry names is loaded into memory with the scene that
carries your `NetworkManager`**, spawned or not — the asset holds hard
references, so each listed prefab and everything it depends on is resident from
that point on. Unity's own
`NetworkPrefabsList` works the same way. On a memory-constrained platform, leave
a large and rarely-spawned prefab out of the ledger and register it by hand when
you load it.

Three things are worth knowing before you use it:

- **Commit `rtmpe-prefabs.json`.** It is the record of which prefab each wire id
  names. A project without it re-derives the numbers per machine, which is the
  disagreement the ledger exists to prevent.
- **Keying is by asset GUID, not by path**, so renaming or moving a prefab keeps
  its id. The ledger stores no path at all; the window resolves each one from the
  GUID when it reads the project, and again whenever the project changes.
- **A retired id is never reissued.** Builds already in the wild still spawn
  through it, so handing the number to a different prefab would spawn the wrong
  object for them. The window burns it instead.

If two branches each allocated, the merged file holds one id under two prefabs.
The window reads that state rather than refusing it, marks both rows, and
offers **Re-issue** — which moves one of them to a fresh number and leaves the
other where every existing build expects it.

##### What the window checks, and what it finds for you

Each row is loaded and inspected, so the window says more than "this id is
recorded":

- **shares this id with another prefab** — the merge case above. Constants
  cannot be generated until one of the pair is re-issued.
- **no asset in this project carries this GUID** — the prefab was deleted
  without retiring its id. The id stays live for every build already running;
  retire it only when none of them still spawns it.
- **carries no networked component on its root** — the prefab is there and
  cannot be spawned. `Spawn` reads the components on the prefab's **root** and
  looks no deeper, so a `NetworkBehaviour` on a child does not count; without
  one on the root the runtime logs an error and destroys the instance. Such a
  row keeps its generated constant — code that names it still compiles — and is
  left **out** of the generated registry, because putting it there only moves
  the failure from the Editor to play mode.
- **could not be inspected** — usually an import still running. Nothing is
  claimed about the row either way; reopen the window when the project has
  settled.

**Scan the project for spawnable prefabs with no id** answers the other
direction: which prefabs carry a networked component and hold no id, so nothing
can spawn them over the network. **Allocate an id for all** takes the whole list
in one write. A prefab with no networked component is never offered — it is not
a networked prefab, and an id spent on it is a number nobody can use — and
neither is one the scan could not inspect, because allocating is a write and a
write made on an unanswered question is one somebody has to undo.

### NetworkBehaviour (base class)

```csharp
ulong  NetworkObjectId       // server-assigned unique object ID
string OwnerPlayerId         // UUID of the owning player
bool   IsOwner               // true only on the owning client
bool   IsSpawned             // true after the object is spawned
bool   DestroyWithOwner      // settable property (not virtual); default: true

// Override with 'protected override':
protected virtual void OnNetworkSpawn()
protected virtual void OnNetworkDespawn()
```

### NetworkVariable types

```csharp
// Constructor signature: (NetworkBehaviour owner, string memberName, T initialValue)
// ⚠️ Declared with the type, not `var`: `nameof(hp)` inside the initializer of
// `var hp` reads a local whose type is not inferred yet, and the compiler
// refuses it (CS0841).
NetworkVariableInt        hp      = new NetworkVariableInt(this, nameof(hp), 100);
NetworkVariableFloat      speed   = new NetworkVariableFloat(this, nameof(speed), 0f);
NetworkVariableBool       alive   = new NetworkVariableBool(this, nameof(alive), true);
NetworkVariableVector3    vel     = new NetworkVariableVector3(this, nameof(vel), Vector3.zero);
NetworkVariableQuaternion lookDir = new NetworkVariableQuaternion(this, nameof(lookDir), Quaternion.identity);
NetworkVariableString     name    = new NetworkVariableString(this, nameof(name), "Player");

// Read (any client)
int currentHp = hp.Value;

// Write (owner only)
hp.Value = 50;

// React (all clients)
hp.OnValueChanged += (oldVal, newVal) => UpdateUI(newVal);
```

### DisconnectReason enum

| Value            | Meaning                                                                 | Reconnect token preserved? |
| ---------------- | ----------------------------------------------------------------------- | -------------------------- |
| `Unknown`        | Unclassified reason                                                     | No                         |
| `ClientRequest`  | You called `Disconnect()`                                               | No                         |
| `ServerRequest`  | Server sent a `Disconnect` packet                                       | No                         |
| `Timeout`        | Handshake or token `Reconnect()` did not complete within `connectionTimeoutMs` | No on a first connect; **yes** on a reconnect that drew no validated `Challenge` |
| `ConnectionLost` | 3 consecutive missed `HeartbeatAck` (recoverable) or a transport `SocketException` (not recoverable) | Heartbeat-miss only |
| `Kicked`         | Server forcibly removed the player                                      | No                         |
| `NonceExhausted` | The outbound AEAD nonce counter reached 2³² packets — the session must be fully re-established | No |
| `ProtocolError`  | The gateway sent a packet that violates the expected protocol sequence; the connection cannot be trusted | No, except a pin configuration that refuses during a reconnect |

The heartbeat-miss path is the ordinary one that preserves the reconnect token,
and two narrower ones do as well — an attempt to resume that never got far
enough to spend it. No reason code settles the question on its own, so check
`NetworkManager.CanReconnect` in your `OnDisconnected` handler and call
`Reconnect()` when it returns `true`; otherwise call `Connect(apiKey)` with
fresh credentials. See [Step 11 — Reconnect after a Drop](#step-11--reconnect-after-a-drop).

---

## Connection State Machine

```
              Connect(apiKey)
Disconnected ──────────────────▶ Connecting
      ▲                              │
      │                              │ Handshake + SessionAck ✅
      │                              ▼
      │                          Connected ◀── CreateRoom / JoinRoom available
      │                              │
      │                              │ CreateRoom / JoinRoom ✅
      │                              ▼
      │                            InRoom ◀── Spawn objects here
      │                              │
      │                              │ LeaveRoom()
      │                              ▼
      │                          Connected
      │                              │
      │                              │ Disconnect()
      │                              ▼
      │                       Disconnecting ──▶ Disconnected
      │
      │  shortcut path for transient drops
      │
      │                          (heartbeat timeout / transport error)
      │                              │
      │                              ▼   (token preserved)
      │                         Disconnected
      │                              │
      │  Reconnect() (CanReconnect = true)
      │                              ▼
      │                        Reconnecting ──ReconnectInit──▶ Challenge ──▶ SessionAck
      │                              │                                          │
      │                              │                                          ▼
      └──────(on token failure)──────┘                                      Connected
                                                                                │
                                                            autoRejoinLastRoomOnReconnect?
                                                                                │
                                                                                ▼
                                                              Rooms.JoinRoom(LastRoomId)
                                                                                │
                                                                                ▼
                                                                             InRoom
```

**Key rules:**
- Call `Connect()` only from `Disconnected` state.
- Call `Reconnect()` only when `CanReconnect == true` (a reconnect token is held).
- Call `CreateRoom()` / `JoinRoom()` only after `OnConnected` fires.
- Call `Spawner.Spawn()` only after `OnRoomCreated` / `OnRoomJoined` fires.
- Call `SetObjectPool()` (if used) inside `OnConnected()` — the object pool is rebuilt on every `Connect()`. The prefab registry is re-loaded on every `Connect()` too, so it needs no call at all; a hand `RegisterPrefab()` may run anytime, and persists across reconnects.

---

## Pre-Launch Checklist

- [ ] SDK installed — `com.rtmpe.sdk 1.0.5` appears in Package Manager
- [ ] `RTMPESettings` asset created with the correct `serverHost` and `serverPort`
- [ ] `NetworkManager` GameObject exists **only in the boot scene** with the Settings asset assigned
- [ ] `serverHost` is correct, and the API key is resolved through `ApiKeySource` — never a `[SerializeField]`, never a literal in source
- [ ] Player prefab has a `NetworkBehaviour` subclass as its main script
- [ ] Player prefab has `NetworkTransform` component attached
- [ ] Player prefab has `NetworkTransformInterpolator` component attached **and its checkbox ticked** — a switched-off interpolator is handed the motion and renders none of it
- [ ] No prefab id is written down anywhere in your own code — every id comes
      from the ledger `Window → RTMPE → Network Prefabs` keeps, which is what
      makes it the same id on every client
- [ ] Every spawnable prefab is registered before the first `Spawn()` — either by
      assigning `NetworkSettings.prefabRegistry`, or by calling `RegisterPrefab()`
      (hand registrations persist across reconnects)
- [ ] `SetObjectPool()` (if used) is called **inside `OnConnected()`**
- [ ] All `NetworkVariable` IDs are unique across each object — every
      `NetworkBehaviour` on one GameObject shares the ID namespace
- [ ] All `NetworkVariable` types are initialized inside `OnNetworkSpawn()`, not `Awake()`/`Start()`
- [ ] Every `Input.*` call is guarded with `if (!IsOwner) return;`
- [ ] All event subscriptions use stored delegate references (not anonymous lambdas)
- [ ] All events are unsubscribed in `OnDestroy()` — a `Rooms` / `Lobby` /
      `Matchmaking` subscription survives every reconnect, so a missed
      unsubscribe keeps the object alive for the rest of the session
- [ ] `Rooms` / `Lobby` / `Matchmaking` are subscribed **once**, not from
      `OnConnected` — they are carried across the rebuild, so re-subscribing
      per connection adds a second copy on the first reconnect and a third on
      the next, and the handler runs once per copy
- [ ] `OnDisconnected` handler checks `CanReconnect` before falling back to `Connect(apiKey)`
- [ ] `autoRejoinLastRoomOnReconnect` matches your UX — disable it to show custom "rejoin?" UI
- [ ] `enableDebugLogs = false` in the production Settings asset
- [ ] **iOS / Android (IL2CPP):** an actual on-device build has been run and every `[RtmpeRpc]` fires. RPCs and `NetworkVariable<T>` are dispatched reflectively; the SDK ships a `link.xml` that preserves its own runtime, but your game's RPC methods and any custom `NetworkVariable<T>` types live in your assembly. When the **Managed Stripping Level** is above **Low**, preserve them in a project `link.xml` — see [Troubleshooting → IL2CPP](troubleshooting.md)

---

## Common Errors and Fixes

### `[RTMPE] NetworkManager.Connect: apiKey must not be null or empty`
**Cause:** No source had a key to give. A source that *failed* never reaches
`Connect` at all — `TryResolve` catches it and the caller reports its own
message — so read `ApiKeySource.LastError` before assuming nothing was
configured.  
**Fix:** In the Editor, store one via **Window → RTMPE → Setup Wizard**. A player
build carries no Editor vault and needs its own source, in the order they are
consulted: register a provider with `ApiKeySource.SetProvider`, launch with
`--rtmpe-api-key-file <path>`, or set the `RTMPE_API_KEY` environment variable.
A shipped game can use only the first — see
[Giving a player build its API key](#giving-a-player-build-its-api-key) for the
two shapes it takes.

---

### `[RTMPE] NetworkManager.Connect ignored — already in state <X>`
**Cause:** `Connect()` was called when the manager was not in `Disconnected` state.  
**Fix:**
```csharp
// TryResolve, not Resolve: the parameterless Resolve() throws when a source
// you configured failed, and this is a reconnect path.
if (NetworkManager.Instance.State == NetworkState.Disconnected
    && ApiKeySource.TryResolve(out string key))
    NetworkManager.Instance.Connect(key);
```

---

### `OnRoomError` fires after a successful `OnConnected`
**Cause:** The API key connected successfully (UDP layer is fine) but is not authorised in the server database.  
**Fix:** Verify the API key is registered and active in the RTMPE developer dashboard.

---

### Players do not see each other moving
**Cause 1:** `NetworkTransform` is missing from the player prefab.  
**Cause 2:** `NetworkTransformInterpolator` is missing or is inside an `if (!IsOwner)` block.  
**Cause 3:** `NetworkTransformInterpolator` is attached but **switched off**. `GetComponent` hands back a disabled component, so the receive path finds one and Unity never calls its `Update`: the states are buffered and nothing renders them.  
**Fix:** Confirm both `NetworkTransform` and `NetworkTransformInterpolator` are attached as separate components in the Inspector, and that the interpolator's checkbox is ticked. Adding a second interpolator does not fix cause 3 — the first one in add order is the one that answers, and it is still off.

---

### `Spawn returned null`
**Cause:** No prefab is registered under that ID — no registry is assigned, the registry does not carry that id, or `RegisterPrefab()` was never called for it.  
**Fix:** Assign `Assets/RTMPE/Generated/RtmpePrefabRegistry.asset` to `NetworkSettings.prefabRegistry`, or call `Spawner.RegisterPrefab(id, prefab)` before `Spawn()`. Hand registrations persist across reconnects, so registering once (e.g. in `OnConnected`) is enough.  
**Check the console first:** if the registry was assigned but a row of it could not be taken at face value, the SDK says so on connect — one line naming what it skipped and what reused an id. A skipped row is a prefab deleted without regenerating: press the button in `Window > RTMPE > Network Prefabs` again. A reused id is a hand edit of the asset, and regenerating replaces it — unless the *ledger* itself holds the duplicate, in which case Generate refuses and you need **Re-issue** on one of the pair first.  
**Stop it recurring:** that window allocates each prefab an id once and writes both the `RtmpePrefabIds` constants and the registry from the same ledger, so the id at the call site, the id in the ledger and the prefab the player resolves cannot drift apart.

---

### `NetworkVariable.OnValueChanged` always fires with the default value
**Cause:** `NetworkVariable` was created in `Awake()` or `Start()` instead of `OnNetworkSpawn()`.  
**Fix:** Move all `new NetworkVariableXxx(...)` calls into `OnNetworkSpawn()`.

---

### `Duplicate NetworkManager instance` warning
**Cause:** Two scenes both contain a `NetworkManager` GameObject.  
**Fix:** Keep `NetworkManager` only in the boot scene. It persists via `DontDestroyOnLoad`.

The duplicate is destroyed on the frame it awakes and the existing manager keeps
running, connection and all. ⚠️ Before **8.0.0** that was not true: the
duplicate's teardown ran in full and reset process-wide state the live manager
owned — including the RPC verifier's session-id hook, whose absence makes the
default verifier reject **every** sender. Additively loading a scene whose
prefab carried a `NetworkManager` therefore ended RPC dispatch silently, and
this warning did not exist to point at it.

---

### Connection times out after 10 seconds
**Cause 1:** Outbound UDP on port 7777 is blocked by a firewall or router.  
**Cause 2:** The RTMPE server is unreachable.  
**Fix 1:** Test on a different network. Ensure outbound UDP 7777 is allowed.  
**Fix 2:** Verify the server is running via the RTMPE dashboard health endpoint.

---

## Performance Notes

| Parameter              | Value / Note                                                  |
| ---------------------- | ------------------------------------------------------------- |
| Tick rate              | 30 Hz — state updates every 33.3 ms                           |
| Latency P99            | < 30 ms within region                                         |
| Max players per room   | 1–100 — set per room via `CreateRoomOptions.MaxPlayers` (`0` = server default of 100) |
| Position threshold     | 0.01 m — sub-centimetre moves are suppressed to save bandwidth |
| Rotation threshold     | 0.1° — tiny rotations are suppressed                          |
| NetworkVariable flush  | 30 Hz — no manual flush needed                                |
| Late-join snapshot     | full NetworkVariable state delivered within one 30 Hz tick after `OnPlayerJoined` |
| Compression            | LZ4 — applied transparently when it shrinks the payload       |
| Thread safety          | `NetworkVariable.Value` is main-thread only — read as well as write |
| Interpolation delay    | 100 ms default — smoother movement at the cost of slight visual delay |

---

*RTMPE SDK 1.0.5 — [API Reference](api/index.md)*
