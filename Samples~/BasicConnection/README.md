# Basic Connection Sample

Demonstrates the minimal connect / disconnect lifecycle for the RTMPE SDK.
The sample ships a single `MonoBehaviour` (`ConnectionTest`); you add it —
together with a `NetworkManager` component — to a scene of your own.

## Prerequisites

Start in the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard):
the gateway address and every key below belong to a project created there, and
**Window → RTMPE → Setup Wizard** — the window Unity opens by itself once per
Editor session — takes those four values and writes the settings asset for you.
The list below is what it produces, and what to build by hand instead.

- Unity 2022.3 LTS or newer (also supports 2023 LTS and Unity 6 / 6000.0+).
- An RTMPE gateway you can reach. Its host, port and keys are issued by the
  RTMPE dashboard; the asset's own defaults point at `127.0.0.1:7777`, which
  is a placeholder rather than a server this package provides.
- An API key issued by the RTMPE dashboard (or any value if your gateway
  is configured for open-access development).
- A `NetworkSettings` asset. The manager *starts* without one — `Awake`
  substitutes an empty default and warns — but that default carries no API-key
  envelope and pins no server key, so no connection can complete. Create one
  via **Assets → Create → RTMPE → Settings** and fill in both keys:
  `apiKeySealServerPublicKeyHex` (the gateway's X25519 key, which the API key
  is sealed to) and `pinnedServerPublicKeyHex` (its Ed25519 key). The default
  `serverPinningMode` is `Strict`, which refuses to connect without the pin.

## Contents

| File | Purpose |
| --- | --- |
| `Scripts/ConnectionTest.cs` | `MonoBehaviour` that connects on Start, displays live status with `OnGUI`, and disconnects on `OnDestroy`. |

> The sample intentionally does **not** ship a `.unity` scene — you wire it
> into a scene of your own. `NetworkManager.Instance` returns the
> `NetworkManager` component placed in the scene; if none exists it returns
> `null` and logs a warning. The scene **must** therefore contain a
> `NetworkManager` (see step 4 below). `NetworkManager` itself calls
> `DontDestroyOnLoad`, so a single instance persists across scene loads.

## Quick start

1. Open the Unity Package Manager (**Window → Package Manager**).
2. Select **RTMPE SDK** → **Samples** → **Basic Connection** → **Import**.
   Unity copies the sample to `Assets/Samples/RTMPE SDK/<version>/Basic Connection/`.
3. Open or create any scene (`File → New Scene → Empty`).
4. Create an empty GameObject and add **both** of these components to it:
   - the `NetworkManager` component (**Component → RTMPE → NetworkManager**)
     — **required**; `ConnectionTest` does nothing without a `NetworkManager`
     in the scene;
   - the **Connection Test** component
     (`Add Component → Scripts → RTMPE.Samples.BasicConnection → Connection Test`).
5. In the Inspector, set:
   - **Connect On Start** — leave enabled.
   - **Reconnect Delay** — `5` (seconds before an automatic retry; `0`
     disables it). The retry resumes the session with the reconnect token
     when the SDK still holds one, and only falls back to a full
     `Connect(apiKey)` when it does not.

   The API key is deliberately not among them. Supply it from outside the
   project instead — any one of:
   - **Window → RTMPE → Setup Wizard**, which stores it in your platform
     credential vault. The Editor is the only thing that reads that vault — no
     build carries it — so the rest of this list is what a player has.

   In the order `ApiKeySource` consults them, a player build reads:

   - a provider you register with `ApiKeySource.SetProvider`, which is the only
     one of these a shipped game can use; see
     **Giving a player build its API key** in the package's own
     `Documentation~/getting-started.md`. A folder whose name ends in `~` is
     hidden from Unity and is not copied when a sample is imported, so open it
     from the package itself — Package Manager's ⋮ menu → **Show in Explorer**
     / **Reveal in Finder** lands in the right folder.
   - `--rtmpe-api-key-file <path>` on the player's command line.
     (`--rtmpe-api-key <key>` also works, but argv is world-readable through
     `ps` and `/proc`, so prefer the file form on any shared machine.)
   - the `RTMPE_API_KEY` environment variable.

   A key typed into an Inspector field is written into the scene asset,
   committed with it, and present in every build made from it.
6. Select the GameObject from step 4 and drag your `NetworkSettings` asset onto
   the `NetworkManager` component's **Settings** field. This assignment is what
   the connection reads: left blank, `Awake` substitutes an empty default and
   the handshake is refused for want of an API-key envelope.

   Fill the asset's own values in **Edit → Project Settings → RTMPE** — `Server
   Host`, `Server Port`, `Api Key Seal Server Public Key Hex` (X25519) and
   `Pinned Server Public Key Hex` (Ed25519), all four from the dashboard. That
   pane edits whichever asset it is pointed at and binds none of them to a
   scene, so it is not a substitute for the field above.
7. Press **Play**. The on-screen overlay shows the live state machine,
   round-trip-time, and any disconnect reason.

## What you should see

- Status line moves from **Idle** through **Connecting…** to **Connected!**
  (the live `NetworkState` is also shown as it transitions).
- An RTT line appears once heartbeats are flowing.
- Stopping play (or calling `TryDisconnect()`) yields a clean
  `Disconnected — reason: …` log entry.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `no API key` warning | No source supplied one. In the Editor, store it via **Window → RTMPE → Setup Wizard**. In a player the wizard's vault does not travel — register a provider with `ApiKeySource.SetProvider`, launch with `--rtmpe-api-key-file <path>`, or set `RTMPE_API_KEY`; they are consulted in that order. |
| Stuck on "Connecting…" | Gateway unreachable. Check `Server Host` / `Server Port` and firewall. |
| Stuck connecting, then `Connection failed` | `apiKeySealServerPublicKeyHex` is blank, or is not the X25519 key your gateway holds the private half of. |
| `Connection failed` with a signature error | `pinnedServerPublicKeyHex` is set but does not match the gateway's Ed25519 key. Set it to the gateway's actual key. For local development, set `serverPinningMode` to `InsecureNoPinning` (or `TrustOnFirstUse` to capture the key on first connect) — under the default `Strict` mode, simply clearing the pin refuses every connection rather than disabling pinning. |

## Manual smoke test

1. Point the settings asset at a gateway you can reach. **Server Host**,
   **Server Port** and both keys come from the RTMPE dashboard — nothing in
   this package starts a server.
2. Follow the **Quick start** above.
3. Verify the status line reaches **Connected** within 5 seconds.
4. Stop play, make the gateway unreachable — stop it if it is yours, or point
   **Server Port** at a port nothing is listening on — press Play again, and
   verify the script surfaces a `ConnectionFailed` reason and (if
   `reconnectDelay > 0`) schedules an automatic retry.
5. To see the **resume** path rather than the redial: connect, then break the
   network at the OS level (disable the adapter, or switch WiFi off) and restore
   it. The heartbeat watchdog ends the session with a preserved token, and the
   status line reads **Resuming session…** rather than **Reconnecting from
   scratch**. ⛔ Stopping the gateway produces the other path — no token survives
   a server that answered the handshake and then refused it.
