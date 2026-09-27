# Basic Connection

The smallest complete use of the RTMPE SDK: one script that connects to the
RTMPE server when the scene starts, shows the connection state on screen, and
disconnects when it is destroyed. When a session drops, the script resumes it
with the SDK's reconnect token instead of starting a new one.

The sample contains one script, `Scripts/ConnectionTest.cs`, and no scene. You
add the script to a scene of your own.

## Requirements

- Unity 2022.3 or newer, with the RTMPE SDK installed.
- A project in the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard)
  and an API key for it.
- The Setup Wizard (**Window → RTMPE → Setup Wizard**), run once as described
  in step 2.

## Setup

1. **Create a project in the portal.** In the
   [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard), create a
   project and an API key. The key is shown only once, so copy it. The
   project's **Connection settings** list the other values Unity needs:
   **Server Host**, **Server Port**, **Sealed-Box Public Key (X25519)** and
   **Pinned Server Public Key**.
2. **Run the Setup Wizard.** Open **Window → RTMPE → Setup Wizard** and go
   through it to **Finish**. On the **Gateway Configuration** step, paste the
   API key and the four connection values. The wizard keeps the key in your
   operating system's credential vault, outside the project, and writes the
   connection values to the project's `NetworkSettings` asset
   (`Assets/RTMPE/NetworkSettings.asset` if the project has none yet). You can
   pass over the steps that add components to the open scene; step 4 adds
   them to the scene this sample runs in.
3. **Import the sample.** In **Window → Package Manager**, select
   **RTMPE SDK**, open **Samples** and press **Import** next to
   **Basic Connection**. Unity copies it to
   `Assets/Samples/RTMPE SDK/<version>/Basic Connection/`.
4. **Build the scene.**
   1. Open a scene, or create one with **File → New Scene**.
   2. Create an empty GameObject with **GameObject → Create Empty**.
   3. With it selected, choose **Add Component → RTMPE → NetworkManager** and
      drag the `NetworkSettings` asset from step 2 onto its **Settings** field.
   4. Choose **Add Component → Scripts → RTMPE.Samples.BasicConnection →
      Connection Test** on the same object.
   5. Leave **Connect On Start** ticked. **Reconnect Delay** is how many
      seconds the script waits before trying again after a failed attempt or
      a dropped session; `0` turns retrying off.
5. **Press Play.**

### Where the API key comes from

The script never stores the key. Each time it connects it asks
`ApiKeySource`, which takes the first of these sources that has one:

| Order | Source | Use it for |
| --- | --- | --- |
| 1 | A provider your code registers with `ApiKeySource.SetProvider` | A game you ship. It is the only source a player's copy of your game can use. |
| 2 | The Setup Wizard's key | The Editor reads the credential vault the wizard writes. A development build reads the key the wizard adds to it when **Inject this key into development builds** is ticked; a release build never carries it, and Android builds cannot read it. |
| 3 | `--rtmpe-api-key-file <path>` on the command line | Builds you start yourself. The file contains only the key. |
| 4 | `--rtmpe-api-key <key>` on the command line | Only on a machine nobody else uses: other accounts can read a process's command line. |
| 5 | The `RTMPE_API_KEY` environment variable | Builds you start yourself. |

For a game you ship, see
[Giving a player build its API key](https://github.com/rtmpengine/unity-rtmpe-sdk-automation/blob/main/Documentation~/getting-started.md#giving-a-player-build-its-api-key)
in the SDK documentation.

## What you should see

An overlay titled `[RTMPE] BasicConnection Demo` appears in the Game view.

- The status line shows `Connecting…`, then a `State: …` line for each state
  change, and settles on `Connected!`. The Console logs
  `[ConnectionTest] Connected to RTMPE gateway.`
- An `RTT: … ms` line appears once the first heartbeat is answered.
- The overlay offers **Disconnect** while connected and **Connect** while
  disconnected. **Disconnect** ends the session and stops the automatic
  retry; the status line then reads `Disconnected (ClientRequest)`.

Stopping Play closes the connection without a `Disconnected` line in the
Console, because the script removes its event handlers before it disconnects.

### Retrying and resuming

With **Reconnect Delay** above `0`, the script tries again once the delay has
passed:

- After a **failed attempt** — no answer from the server, or a configuration
  error — it connects from scratch.
- After a **dropped session**, it resumes the same session with
  `NetworkManager.Reconnect()`. To see this, connect, then take the machine
  offline (switch Wi-Fi off or disable the network adapter) until the Console
  logs `[ConnectionTest] Disconnected — reason: ConnectionLost`, about 30
  seconds with the default heartbeat settings. Bring the network back straight
  away: after the delay the status line shows `Resuming session…` and then
  `Connected!`.

The SDK also keeps the reconnect token when the server closes the session
itself, for example while it restarts, so that session is resumed the same
way. A session ended by `Disconnect()` has no token left, and the next attempt
starts from scratch.

## How it works

`ConnectionTest` is a single `MonoBehaviour`:

| Member | What it does |
| --- | --- |
| `Awake` | Subscribes to `OnStateChanged`, `OnConnected`, `OnDisconnected`, `OnConnectionFailed` and `OnRttUpdated` on `NetworkManager.Instance`. |
| `Start` | Calls `TryConnect()` when **Connect On Start** is ticked. |
| `TryConnect()` | Gets the key with `ApiKeySource.TryResolve` and calls `Connect(apiKey)`. With no key, it logs a warning that names the key sources, and does not connect. |
| `OnConnectionFailed`, `OnDisconnected` | Start the retry when **Reconnect Delay** is above `0`. |
| `ReconnectAfterDelay` | Calls `NetworkManager.Reconnect()` when `CanReconnect` is true, and `TryConnect()` otherwise. |
| `TryDisconnect()` | Stops retrying and calls `NetworkManager.Disconnect()`, which also discards the reconnect token. |
| `OnDestroy` | Removes the event handlers, then disconnects if still connected. |
| `OnGUI` | Draws the overlay and the **Connect** or **Disconnect** button. |

`NetworkManager.Instance` returns the `NetworkManager` in the scene. The
manager keeps itself alive across scene loads, so one instance serves the
whole session.

## Troubleshooting

The script's own Console messages start with `[ConnectionTest]`.

| Symptom | Cause | Fix |
| --- | --- | --- |
| The status line reads `ERROR: no NetworkManager.` | The scene has no `NetworkManager`. | Add one as in step 4. |
| `Cannot connect: no API key` | No source had a key. | In the Editor, enter the key in the Setup Wizard (step 2). A build needs one of the other sources in [Where the API key comes from](#where-the-api-key-comes-from). If the message includes `A configured source failed`, a source you set up could not be read, for example a key file that does not exist. |
| `Connection failed — Server not pinned` | The settings asset has no pinned key, and **Server Pinning Mode** is `Strict`, the default. | Enter the portal's **Pinned Server Public Key** in the Setup Wizard and press **Finish** again. |
| `Connection failed — No API-key envelope configured` | The settings asset has no sealed-box key. | Enter the portal's **Sealed-Box Public Key (X25519)** in the Setup Wizard and press **Finish** again. The wizard writes it to the asset's **Api Key Seal Server Public Key Hex** field. |
| `Connection failed — apiKeySealServerPublicKeyHex is not a valid X25519 public key`, or `… equals pinnedServerPublicKeyHex …` | The sealed-box field holds another value, most often the pinned key. | Copy both keys again from the portal, each into its own field. |
| `Connection failed — Connection timeout.` after about 10 seconds | The server did not answer: **Server Host** or **Server Port** is wrong, a firewall drops UDP, or the sealed-box key is not your project's. | Check the values against the portal and allow outgoing UDP. The Console line that starts `[NM] connection failed after` says how far the attempt got. |
| `Connection failed — Server identity verification failed` after about 10 seconds | The pinned key does not match the server's key. | Copy the **Pinned Server Public Key** again. For local testing only, you can set **Server Pinning Mode** on the settings asset to `InsecureNoPinning`. `TrustOnFirstUse` still checks a pin that is filled in, so clear **Pinned Server Public Key Hex** before you use it. Under `Strict`, an empty pin refuses every connection rather than turning pinning off. |
| The status line keeps returning to `Reconnecting in 5 s…` | Every attempt fails, and the script retries after each one. | Read the first `Connection failed` line in the Console. Set **Reconnect Delay** to `0` while you fix it. |
