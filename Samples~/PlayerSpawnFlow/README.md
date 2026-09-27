# Player Spawn Flow

The path from a cold start to a player standing in a room, written out as one
script: connect, create a room, and spawn this client's avatar when the room is
entered. No prefab id is typed anywhere; the script asks the SDK which id the
prefab is registered under.

The sample contains `Scripts/GameManager.cs` (the flow),
`Scripts/PlayerController.cs` (the script on the player prefab) and
`Scenes/PlayerSpawnFlow.unity`.

## Requirements

- Unity 2022.3 or newer, with the RTMPE SDK installed.
- A project in the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard)
  and an API key for it.
- The Setup Wizard (**Window → RTMPE → Setup Wizard**), run once as described
  in step 2.
- To move the avatar, the legacy Input Manager: **Active Input Handling** in
  **Edit → Project Settings → Player** set to *Input Manager (Old)* or *Both*.
  The spawn flow itself reads no input.

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
   pass over the steps that add components to the open scene; the steps below
   add them to the sample's scene.
3. **Import the sample.** In **Window → Package Manager**, select
   **RTMPE SDK**, open **Samples** and press **Import** next to
   **Player Spawn Flow**. Unity copies it to
   `Assets/Samples/RTMPE SDK/<version>/Player Spawn Flow/`.
4. **Open the scene.** Open `Scenes/PlayerSpawnFlow.unity` from the imported
   folder. It holds Main Camera, Directional Light, Ground and an empty
   `GameManager` object.
5. **Add the NetworkManager.** Create an empty GameObject with
   **GameObject → Create Empty** and name it `[RTMPE] NetworkManager`. Choose
   **Add Component → RTMPE → NetworkManager** and drag the `NetworkSettings`
   asset from step 2 onto its **Settings** field.
6. **Make the player prefab.**
   1. Create a capsule with **GameObject → 3D Object → Capsule** and name it
      `Player`.
   2. Choose **Add Component → Physics → Character Controller**.
   3. Choose **Add Component → Scripts → RTMPE.Samples.PlayerSpawnFlow →
      Player Controller**. Unity adds **Network Transform** and
      **Network Transform Interpolator** with it, because the script requires
      both. `PlayerController` derives from `NetworkBehaviour`, which gives the
      object its network identity; `NetworkBehaviour` itself is abstract and
      is never added on its own.
   4. Drag `Player` from the Hierarchy into a folder in the Project window to
      make it a prefab, then delete it from the scene.
7. **Give the prefab an id.** Open **Window → RTMPE → Network Prefabs**. The
   window works on the Project window's selection and has no drop target or
   object field of its own.
   1. Select the `Player` prefab in the Project window, not in the scene, and
      press `Allocate id for selection`. Until a prefab asset is selected, the
      window shows `— select a prefab in the Project window —` and the button
      is disabled. Alternatively, press
      `Scan the project for spawnable prefabs with no id` and then
      `Allocate an id for all <n>`, where `<n>` is how many prefabs the scan
      found.
   2. Press `Generate RtmpePrefabIds.cs and the prefab registry`. It writes
      `Assets/RTMPE/Generated/RtmpePrefabIds.cs` and
      `Assets/RTMPE/Generated/RtmpePrefabRegistry.asset`.
   3. Select the `NetworkSettings` asset and drag `RtmpePrefabRegistry` onto
      its **Prefab Registry** field.

   > **Important:** Generate does not allocate anything. It writes out the ids
   > that are already allocated. With none, the `RtmpePrefabIds` class it
   > writes holds only the comment `// The ledger records no prefabs.`, and the
   > window still reports success.
8. **Add the game manager.** Select `GameManager` and choose
   **Add Component → Scripts → RTMPE.Samples.PlayerSpawnFlow → Game Manager**.
   Set its three fields:
   - **Room Name**: the name of the room it creates.
   - **Max Players**: the room's capacity, from 1 to 100.
   - **Player Prefab**: the `Player` prefab from step 6.

   The `GameManager` object's transform is the spawn point. Set its position
   to `(0, 1, 0)` so the capsule stands on the ground: Unity's capsule is two
   units tall, with its pivot at the centre.
9. **Press Play.**

## What you should see

The Console logs:

```text
[GameManager] Connected — creating room.
[GameManager] Entered room — spawning local player.
```

A capsule appears at the `GameManager` object's position. It moves with WASD
or the arrow keys and turns with the mouse; only the client that owns an
avatar can move it.

## How it works

| Script | What it does |
| --- | --- |
| `GameManager.cs` | In `Start`, gets the key with `ApiKeySource.TryResolve`, subscribes to `OnConnected`, `OnDisconnected`, `Rooms.OnRoomCreated` and `Rooms.OnRoomJoined`, and calls `Connect`. When connected, it creates a room with **Room Name** and **Max Players**. When the room is entered, it asks `Spawner.TryGetPrefabId` for the prefab's id and spawns the avatar at its own position and rotation with `Spawner.Spawn`. It spawns one avatar per session and forgets it on `OnDisconnected`. |
| `PlayerController.cs` | The player prefab's script. It creates a replicated score, a `NetworkVariableInt`, in `OnNetworkSpawn`, and moves the avatar in `Update` on the owning client only. `AddScore(int)`, for your own code to call, changes the score on the owner, and every client logs the change. **Network Transform** sends the owner's position and rotation; **Network Transform Interpolator** plays them back on every other client, where an avatar without it stays frozen. |

An avatar belongs to the client that spawned it and is removed from every other
client when that client leaves.

What the sample leaves out:

- **Each client creates its own room.** A second client opens a second room
  with the same name, and the two never meet. To enter an existing room, call
  `Rooms.JoinRoom(roomId)`, or `Matchmaking.StartMatchmaking`, which joins an
  open room or creates one. The Two Player Room sample uses matchmaking.
- **A drop ends the session.** The sample never calls `Reconnect()`. The
  SDK's Connection Bootstrap (**Add Component → RTMPE → Connection Bootstrap**)
  performs the same flow as a component: it connects, enters a room and spawns
  the player, and it also resumes a dropped session and re-enters the room.
- **Prefabs from bundles.** A prefab loaded from an asset bundle or through
  Addressables can be registered in code with `Spawner.RegisterPrefab` before
  the room is entered; `Spawner.TryGetPrefabId` then answers for it in the
  same way.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `[GameManager] No API key.` in the Console | No source had a key. | In the Editor, enter the key in the Setup Wizard (step 2). For a built player, see [Giving a player build its API key](https://github.com/rtmpengine/unity-rtmpe-sdk-automation/blob/main/Documentation~/getting-started.md#giving-a-player-build-its-api-key). |
| `no prefab id is registered for Player` | The prefab has no id, or the registry is not on the settings asset. Pressing Generate on its own does not allocate an id. | Repeat step 7 in order, and check the **Prefab Registry** field of the `NetworkSettings` asset. |
| Nothing after `[GameManager] Connected — creating room.` | The room could not be created. The Console shows `[RTMPE] RoomManager: CreateRoom failed —` with the reason. | Act on the reason given. Your own code receives it through `Rooms.OnRoomError`. |
| `[GameManager] Disconnected (ProtocolError).` right after starting, with an `[RTMPE]` error that names `apiKeySealServerPublicKeyHex` or says `Server not pinned` | A key from the portal is missing from the settings asset, or the two keys are swapped. | Enter the portal's **Sealed-Box Public Key (X25519)** and **Pinned Server Public Key** in the Setup Wizard and press **Finish** again. |
| `[GameManager] Disconnected (Timeout).` after about 10 seconds, with a Console line that starts `[NM] connection failed after` | The server did not answer: **Server Host** or **Server Port** is wrong, a firewall drops UDP, or a key does not belong to your project. | Check the values against the portal's **Connection settings**, and allow outgoing UDP. |
| The capsule is half in the ground | The spawn point is at `y = 0`, and the capsule's pivot is at its centre. | Move the `GameManager` object to `y = 1`. |
| An `InvalidOperationException` from `UnityEngine.Input` every frame | The project uses the Input System package only. | Set **Active Input Handling** to *Both*, or delete `Update` from `PlayerController`. |
