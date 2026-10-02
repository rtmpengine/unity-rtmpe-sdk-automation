# Two Player Room

Two clients in one room, each moving its own capsule and seeing the other's.
You run the sample twice, once in the Editor and once as a built player,
because a second client is what shows that multiplayer works. The networking
is done by SDK components: the **Connection Bootstrap** connects, enters the
room and spawns each client's avatar.

The sample contains four scripts in `Scripts/` and `Scenes/TwoPlayerRoom.unity`.

## Requirements

- Unity 2022.3 or newer, with the RTMPE SDK installed, and a Windows, macOS or
  Linux build target for the second client.
- A project in the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard)
  and an API key for it.
- The Setup Wizard (**Window → RTMPE → Setup Wizard**), run once as described
  in step 2.
- To move the capsules, the legacy Input Manager: **Active Input Handling** in
  **Edit → Project Settings → Player** set to *Input Manager (Old)* or *Both*.

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
   **Two Player Room**. Unity copies it to
   `Assets/Samples/RTMPE SDK/<version>/Two Player Room/`.
4. **Open the scene.** Open `Scenes/TwoPlayerRoom.unity` from the imported
   folder. It holds Main Camera, Directional Light, Ground, an empty
   `[RTMPE] Session` object and a `Player` capsule.
5. **Make the avatar prefab.**
   1. Select the `Player` capsule and choose
      **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom → Two Player Avatar**.
      Unity adds **Network Transform** and **Network Transform Interpolator**
      with it, and keeps both for as long as the script is attached.
      `TwoPlayerAvatar` derives from `NetworkBehaviour`, which gives the object
      its network identity; `NetworkBehaviour` itself is abstract and is never
      added on its own.
   2. Drag `Player` from the Hierarchy into a folder in the Project window to
      make it a prefab, then delete it from the scene.
6. **Give the prefab an id.** Open **Window → RTMPE → Network Prefabs**. The
   window works on the Project window's selection and has no drop target or
   object field of its own.
   1. Select the `Player` prefab in the Project window, not in the scene, and
      press `Allocate id for selection`. Until a prefab asset is selected, the
      window shows `— select a prefab in the Project window —` and the button
      is disabled. Alternatively, press
      `Scan the project for spawnable prefabs with no id` and then
      `Allocate an id for all 1`; the number is how many prefabs the scan found.
   2. Press `Generate RtmpePrefabIds.cs and the prefab registry`. It writes
      `Assets/RTMPE/Generated/RtmpePrefabIds.cs` and
      `Assets/RTMPE/Generated/RtmpePrefabRegistry.asset`.
   3. Select the `NetworkSettings` asset and drag `RtmpePrefabRegistry` onto
      its **Prefab Registry** field.

   > **Important:** Generate does not allocate anything. It writes out the ids
   > that are already allocated. With none, the `RtmpePrefabIds` class it
   > writes holds only the comment `// The ledger records no prefabs.`, and the
   > window still reports success.
7. **Set up the session object.** Select `[RTMPE] Session` and add these
   components to it. They all go on this one object, because
   `TwoPlayerSpawnPoints` looks for the Connection Bootstrap on its own
   object.
   1. **Add Component → RTMPE → NetworkManager**, with the `NetworkSettings`
      asset dragged onto its **Settings** field.
   2. **Add Component → RTMPE → Connection Bootstrap**, set as follows:

      | Field | Value |
      | --- | --- |
      | **Connect On Start** | Ticked |
      | **Entry Policy** | `Matchmaking` |
      | **Matchmaking Mode** | `two-player-room` (any text, the same on both clients) |
      | **Max Players** | `2` |
      | **Player Prefab** | The `Player` prefab from step 5 |

   3. **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom → Two Player Spawn Points**.
   4. **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom → Two Player Credentials**.
   5. **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom → Two Player Room Hud**.
8. **Press Play.** The Editor client enters a room alone, its capsule stands
   in it, and the readout shows `Avatars spawned here: 1`.
9. **Run the second client.** Stop Play. In **File → Build Settings**, add
   `Scenes/TwoPlayerRoom.unity` as the first scene and build a player. Give
   the player an API key as described in the next section. Then press Play in
   the Editor and start the player: the two clients meet in one room.

### Giving the built player its API key

The Editor reads the key from the Setup Wizard's vault; a built player cannot.
The SDK asks these sources in order and uses the first that has a key:

| Source | How to use it |
| --- | --- |
| Your own code | Call `TwoPlayerCredentials.Supply(key)` before the Connection Bootstrap connects. This is the route for a game you ship. |
| The Setup Wizard, for a development build | Tick **Inject this key into development builds** on the wizard's **Gateway Configuration** step, then build with **Development Build** ticked. A release build never carries the key, and Android builds cannot read it. |
| `--rtmpe-api-key-file <path>` | Start the player with the path of a file that contains only the key. |
| `--rtmpe-api-key <key>` | Works too, but other accounts on the machine can read a process's command line; prefer the file. |
| `RTMPE_API_KEY` | Set the environment variable in the shell you start the player from. |

From a Linux or macOS shell, write the key to a file only your account can
read, then start the player with it:

```bash
printf '%s' 'YOUR_PROJECT_API_KEY' > ~/.rtmpe-key
chmod 600 ~/.rtmpe-key
./TwoPlayerRoom.x86_64 --rtmpe-api-key-file ~/.rtmpe-key                   # Linux
open -n ./TwoPlayerRoom.app --args --rtmpe-api-key-file ~/.rtmpe-key       # macOS
```

On Windows, write the file, remove inherited access to it so only your account
can read it, and start the player with the option:

```text
TwoPlayerRoom.exe --rtmpe-api-key-file C:\Users\you\rtmpe.key
```

Give the option an absolute path. The SDK reads the path exactly as given, so
a `~` that reaches it unexpanded, from a launcher or a quoted argument, names a
folder called `~`.

Both clients use the same key. The key identifies your project; each client
still gets its own player identity when it connects. Both clients can run on
one machine, since each uses its own local port.

## What you should see

In both windows, two capsules standing on the ground, 4 to 6 units apart, each
turned toward the middle of the ring. The host's capsule stands at the point
nearest the camera, and the other client's at one of the other three points.
In either window, move that client's capsule with WASD, and it moves in the
other window too.

The readout in each window reaches:

```
RTMPE state: InRoom
In room: yes
Avatars spawned here: 2
API key: supplied
```

- **Avatars spawned here** counts every avatar in this process: this client's
  own and the other client's. It reads `1` while you are alone and `2` once
  the second client is in the room.
- **API key** reads `supplied` when a source had a key as the scene started,
  `MISSING` when none did (the client then does not connect), and
  `nothing asked` when `TwoPlayerCredentials` is not in the scene.

## How it works

The Connection Bootstrap does the networking. It connects when the scene
starts and asks the server for a room with the same **Matchmaking Mode**: the
first client opens a room and the next one joins it, so no room id is typed.
When it enters the room it spawns the **Player Prefab** for this client. With
**Max Players** at `2`, a full room takes nobody else, and a third client gets
a room of its own. The Player Spawn Flow sample shows the same flow written out
as code.

| Script | What it does |
| --- | --- |
| `TwoPlayerAvatar.cs` | The avatar prefab's script. Its `[RequireComponent]` adds **Network Transform**, which sends the owner's position and rotation, and **Network Transform Interpolator**, which plays them back on every other client. Only the owning client reads input. It counts the avatars in this process for the readout. |
| `TwoPlayerSpawnPoints.cs` | Answers the bootstrap's `RtmpeConnectionBootstrap.ChooseSpawnPose` with a point on a ring of four, three units from the centre at `y = 1`, turned toward the middle. The room's host always takes the first point and every other client one of the other three, chosen from its `LocalPlayerId`, so two clients never share a point. A third and a fourth client can. |
| `TwoPlayerCredentials.cs` | Registers a provider with `ApiKeySource.SetProvider` that answers with whatever your code passed to `TwoPlayerCredentials.Supply`. Until your code calls `Supply`, it has no key to give, and the SDK asks the next source. At startup it logs whether any source has a key. |
| `TwoPlayerRoomHud.cs` | Draws the four-line readout with `OnGUI`. |

What the sample leaves out:

- **Game state.** Only the transform is replicated. Add `NetworkVariable`
  fields and `[RtmpeRpc]` methods to `TwoPlayerAvatar` for scores or health;
  the setup above stays the same.
- **Fetching the key.** `TwoPlayerCredentials.Supply` takes a key your code
  already has. For a key that arrives later, for example from your own
  backend, clear **Connect On Start**, call `Supply` once the key is in hand,
  and then call the bootstrap's `Connect()`.
- **Distinct points for larger rooms.** A game that needs every player on a
  different point claims one through the room's properties.

## Troubleshooting

In the Editor, messages go to the Console. A built player writes them to
`Player.log`:

| Platform | Log file |
| --- | --- |
| Windows | `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\Player.log` |
| macOS | `~/Library/Logs/<CompanyName>/<ProductName>/Player.log` |
| Linux | `~/.config/unity3d/<CompanyName>/<ProductName>/Player.log` |

`<CompanyName>` and `<ProductName>` are set in
**Edit → Project Settings → Player**.

| Symptom | Cause | Fix |
| --- | --- | --- |
| The readout shows `API key: MISSING` | No source had a key when the scene started. The Console line that starts `[TwoPlayerCredentials] No API key` lists the sources. | In the Editor, enter the key in the Setup Wizard (step 2). For the built player, see [Giving the built player its API key](#giving-the-built-player-its-api-key). |
| The readout shows `API key: nothing asked` | `TwoPlayerCredentials` is not in the scene. | Add it to `[RTMPE] Session` (step 7). |
| One capsule, half in the ground, while the readout shows `Avatars spawned here: 2` | `TwoPlayerSpawnPoints` is missing, or is on another object than the Connection Bootstrap, so both avatars spawn at the origin. | Add it to `[RTMPE] Session`. On any other object it logs `There is no RtmpeConnectionBootstrap on this GameObject`. |
| Both windows show `Avatars spawned here: 1` | The clients are in different rooms. | Use the same **Matchmaking Mode** on both, with **Max Players** of at least `2`. |
| The other client's capsule appears but never moves | Its **Network Transform Interpolator** is missing or switched off. The Console says which, with `carries no NetworkTransformInterpolator` or `its NetworkTransformInterpolator is switched off`. | Keep `TwoPlayerAvatar` on the prefab, which requires the interpolator, and tick the interpolator's checkbox. |
| Two capsules, apart, but both sunk into the ground | The prefab is not Unity's built-in capsule, or its pivot is not at its centre. | Change `StandingHeight` in `TwoPlayerSpawnPoints.cs`. |
| `cannot spawn Player: no prefab id is registered for it` | The prefab has no id, or the registry is not on the settings asset. Pressing Generate on its own does not allocate an id. | Repeat step 6 in order, and check the **Prefab Registry** field of the `NetworkSettings` asset. |
| No room is entered, and the Console shows an `[RTMPE]` error that names `apiKeySealServerPublicKeyHex` or says `Server not pinned` | A key from the portal is missing from the settings asset, or the two keys are swapped. | Enter the portal's **Sealed-Box Public Key (X25519)** and **Pinned Server Public Key** in the Setup Wizard and press **Finish** again. |
| No room is entered, and after about 10 seconds the Console shows `[NM] connection failed after` | The server did not answer: **Server Host** or **Server Port** is wrong, a firewall drops UDP, or a key does not belong to your project. | Check the values against the portal's **Connection settings**, and allow outgoing UDP. |
