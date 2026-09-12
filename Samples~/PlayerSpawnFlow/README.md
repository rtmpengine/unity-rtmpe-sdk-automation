# Player Spawn Flow Sample

The path from a cold start to a player standing in a room — connect, open a
room, spawn this client's avatar — with the prefab id **asked for rather than
typed**.

## What is in the box

| | |
|---|---|
| `Scripts/GameManager.cs` | The flow. Connects on `Start`, creates a room, spawns the local player when the room is entered, and lets go of it on disconnect. |
| `Scripts/PlayerController.cs` | What goes on the player prefab: a `NetworkBehaviour` with a replicated score, beside `NetworkTransform`. |
| `Scenes/PlayerSpawnFlow.unity` | A camera, a light, a ground plane and an empty `GameManager` object. The wiring below is what you add to it. |

## Prerequisites

Start in the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard):
the gateway address and every key below belong to a project created there, and
**Window → RTMPE → Setup Wizard** takes those values and writes the settings
asset for you.

- Unity 2022.3 LTS or newer.
- A gateway you can reach. Its host, port and keys are issued by the dashboard;
  the settings asset's own defaults point at `127.0.0.1:7777`, which is a
  placeholder rather than a server this package provides.
- A `NetworkSettings` asset (**Assets → Create → RTMPE → Settings**) carrying
  both keys: `apiKeySealServerPublicKeyHex` (the gateway's X25519 key, which the
  API key is sealed to) and `pinnedServerPublicKeyHex` (its Ed25519 key). The
  default `serverPinningMode` is `Strict`, which refuses to connect without the
  pin.
- An API key, supplied from **outside the project** — see step 5.

## Setting it up

1. **Import.** **Window → Package Manager → RTMPE SDK → Samples → Player Spawn
   Flow → Import**. Unity copies it to
   `Assets/Samples/RTMPE SDK/<version>/Player Spawn Flow/`.

2. **Open** `Scenes/PlayerSpawnFlow.unity`. It holds Main Camera, Directional
   Light, `GameManager` and Ground.

3. **NetworkManager.** Create an empty GameObject, name it
   `[RTMPE] NetworkManager`, **Add Component → RTMPE → NetworkManager**
   (`RTMPE.Core`),
   and drag your `NetworkSettings` asset onto its **Settings** field. That
   binding is what the connection reads: left blank, `Awake` substitutes an
   empty default that can complete no handshake.

4. **The player prefab.**
   1. `3D Object → Capsule`, name it `Player`.
   2. **Add Component → Physics → Character Controller** (Unity built-in).
   3. **Add Component → RTMPE → Network Transform** (`RTMPE.Sync`) — 30 Hz
      transform sync.
   4. **Add Component → Scripts → RTMPE.Samples.PlayerSpawnFlow → Player
      Controller**.
      The network identity comes from this script: it derives from `NetworkBehaviour`,
      and that abstract base is never added as a component on its own.
   5. Drag `Player` into a `Prefabs/` folder to make a prefab, and delete the
      copy left in the scene.

5. **Give the prefab an id.** Open **Window → RTMPE → Network Prefabs**. The
   window has no drop target and no object field — it reads your **Project
   window** selection — and it takes two presses, not one.

   1. Select the `Player` prefab in the Project window, not in the scene, and
      press `Allocate id for selection`. Until a prefab **asset** is selected —
      and a scene object is not one — the window shows `— select a prefab in
      the Project window —` and the button is greyed out. (Or press `Scan the
      project for spawnable prefabs with no id`; when it finds something, an
      `Allocate an id for all …` button appears beneath it, and the number in
      that button is what it found.)
   2. Then press `Generate RtmpePrefabIds.cs and the prefab registry`. It writes
      `Assets/RTMPE/Generated/RtmpePrefabIds.cs` and
      `Assets/RTMPE/Generated/RtmpePrefabRegistry.asset`.

   Assign the generated registry to the `NetworkSettings` asset's **Prefab
   Registry** field.

   ⚠️ **Generate does not allocate anything.** It writes out whatever the id
   ledger already holds, and on an empty one it writes a registry whose only
   content is the comment `// The ledger records no prefabs.` — and reports
   success. Skipping the first press therefore leaves you with a success
   message here and `no prefab id is registered` at spawn time.

   ⛔ This step is not optional and there is no number to type instead. At spawn
   time the sample asks the SDK — `Spawner.TryGetPrefabId(prefab, out var id)` —
   which id this session would spawn that prefab under, and without a
   registration there is no answer; the console says so and names this window.

   A project that gets its prefabs from somewhere else — an asset bundle,
   Addressables — registers them with `Spawner.RegisterPrefab` instead, and the
   sample works unchanged: it asks the same table either way.

6. **GameManager.** Select the `GameManager` object → **Add Component →
   Scripts → RTMPE.Samples.PlayerSpawnFlow → Game Manager**. Three fields, and
   no more:
   - **Room Name** — what the room is called.
   - **Max Players** — 1–100; the platform refuses anything outside that range
     before the request is sent.
   - **Player Prefab** — the prefab from step 5.

   The object's own transform is the spawn point, so move it to move where the
   player appears.

7. **The API key is not one of those fields, and must not become one.** Unity
   writes a serialized string into the scene asset, which is committed and
   present in every build made from it. Supply it from outside the project
   instead — any one of:
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

8. **Press Play.**

## What you should see

```
[GameManager] Connected — creating room.
[GameManager] Entered room — spawning local player.
```

and a capsule at the `GameManager` object's position. Unity's built-in Capsule
is two units tall with its pivot at the **centre**, so at the shipped position
of `(0, 0, 0)` it is half-buried in the ground plane — raise the `GameManager`
object to `y = 1`, or give the prefab a child mesh offset by one unit, before
reading anything into how the avatar looks.
`AddScore(10)` on the spawned `PlayerController` replicates through
`NetworkVariableInt`; disconnecting removes the player on every other client,
because a spawn is destroyed with its owner.

## If you want none of this code

Everything `GameManager.cs` does — connect, enter a room, spawn the local player,
let go of it on a drop — is also a component the SDK ships, **and it does one
thing more**: it re-enters that room when the connection comes back, which this
sample deliberately does not: **`RtmpeConnectionBootstrap`**
(`Component → RTMPE → Connection Bootstrap`, or the Setup Wizard's third step).
Attach it to the object carrying your `NetworkManager`, give it the same player
prefab, and delete `GameManager` entirely.

This sample keeps the code because reading it is how the flow is learned, and
because a project that wants to change one step of it starts from here. The
component is for the projects that want none of the steps changed.

## What this sample deliberately does not do

- **It does not put two clients in the same room.** Every client that runs it
  *creates* a room, so a second Editor opens a second room of the same name and
  the two never meet. Entering an existing room is a different call —
  `Rooms.JoinRoom(roomId)`, or `Matchmaking.StartMatchmaking(...)`, which is the
  join-or-create verb with its own timeout and its own four outcomes. Which of
  the three a game wants is a game decision, and this sample takes the one that
  needs nothing to exist first.
- **It never reconnects at all.** `GameManager` calls `Connect(apiKey)` and
  nothing else; the sample carries no `RtmpeConnectionBootstrap` and calls
  `Reconnect()` nowhere, so a drop here ends the session. ⛔ The SDK's
  auto-rejoin (`NetworkSettings.autoRejoinLastRoomOnReconnect`, `true` by
  default) runs only after a **successful token reconnect**, which requires
  `Reconnect()` to have been called — so it is not what keeps this sample
  correct, and the spawn guard above protects only the `OnRoomJoined` /
  `OnRoomCreated` double-fire on a single entry. Add
  `Component → RTMPE → Connection Bootstrap` if you want the resume path.
- **It does not read input in the flow.** `PlayerController` does, through the
  legacy Input Manager. On a project configured for the Input System package
  alone `UnityEngine.Input` throws, so set **Active Input Handling** to *Both*
  in Player settings, or delete that `Update` — nothing in the spawn flow
  depends on it.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `No API key` | No source supplied one. In the Editor, store it via **Window → RTMPE → Setup Wizard**. In a player the wizard's vault does not travel — register a provider with `ApiKeySource.SetProvider`, launch with `--rtmpe-api-key-file <path>`, or set `RTMPE_API_KEY`; they are consulted in that order. |
| `no prefab id is registered for …` | The first press in step 5 was skipped, or only the second was done. Pressing `Generate …` on an empty ledger reports success and allocates nothing, so the prefab still needs `Allocate id for selection` (or the scan-and-allocate pair) first. Also check that `RtmpePrefabRegistry` really is on the `NetworkSettings` asset's **Prefab Registry** field. |
| Nothing after `Connected` | The room request failed. Subscribe to `Rooms.OnRoomError` — the reason arrives there rather than as an exception. |
| Stuck connecting, then `Connection failed` | `apiKeySealServerPublicKeyHex` is blank, or is not the X25519 key your gateway holds the private half of. |
| `Connection failed` with a signature error | `pinnedServerPublicKeyHex` does not match the gateway's Ed25519 key. For local development set `serverPinningMode` to `InsecureNoPinning`, or `TrustOnFirstUse` to capture the key on first connect — under the default `Strict` mode, clearing the pin refuses every connection rather than disabling pinning. |
