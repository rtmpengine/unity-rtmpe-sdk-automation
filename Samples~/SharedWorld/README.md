# Shared World

State that belongs to no player, kept on the room's **world object**: a board
of food cells laid out at random. The host spawns the world, every client sees
and eats from the same board, a player who joins late sees the board as it is,
and when the host leaves, the next host carries on with the same board. The
game logic never checks who the host is; the board reads the role only to
label each window.

The sample contains `Scripts/SharedWorldState.cs`, `Scripts/SharedWorldBoard.cs`
and `Scenes/SharedWorld.unity`.

## Requirements

- Unity 2022.3 or newer, with the RTMPE SDK installed, and a Windows, macOS or
  Linux build target for the second client.
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
   pass over the steps that add components to the open scene; the steps below
   add them to the sample's scene.
3. **Import the sample.** In **Window → Package Manager**, select
   **RTMPE SDK**, open **Samples** and press **Import** next to
   **Shared World**. Unity copies it to
   `Assets/Samples/RTMPE SDK/<version>/Shared World/`.
4. **Open the scene.** Open `Scenes/SharedWorld.unity` from the imported
   folder. It holds Main Camera, Directional Light, Ground and an empty
   `[RTMPE] Session` object.
5. **Make the world prefab.**
   1. Create an empty GameObject with **GameObject → Create Empty** and name
      it `World`.
   2. Choose **Add Component → RTMPE → World Authority**. Set **World Key** to
      `shared-world`, the key `SharedWorldState.WorldKey` looks the world up
      by. Leave **Shared Authority** ticked: it lets clients other than the
      host send the world an RPC.
   3. Choose **Add Component → Scripts → RTMPE.Samples.SharedWorld →
      Shared World State**. `SharedWorldState`
      derives from `NetworkBehaviour`, which gives it its network identity;
      `NetworkBehaviour` itself is abstract and is never added on its own.
      The order of the two components does not matter.
   4. Drag `World` from the Hierarchy into a folder in the Project window to
      make it a prefab, then delete it from the scene.
6. **Give the prefab an id.** Open **Window → RTMPE → Network Prefabs**. The
   window works on the Project window's selection and has no drop target or
   object field of its own.
   1. Select the `World` prefab in the Project window, not in the scene, and
      press `Allocate id for selection`. Alternatively, press
      `Scan the project for spawnable prefabs with no id` and then the
      `Allocate an id for all` button that appears.
   2. Press `Generate RtmpePrefabIds.cs and the prefab registry`.
   3. Select the `NetworkSettings` asset and drag `RtmpePrefabRegistry` onto
      its **Prefab Registry** field.

   > **Important:** Generate does not allocate anything. With no ids
   > allocated, the `RtmpePrefabIds` class it writes holds only the comment
   > `// The ledger records no prefabs.`, and the window still reports success.
7. **Set up the session object.** Select `[RTMPE] Session` and add:
   1. **Add Component → RTMPE → NetworkManager**, with the `NetworkSettings`
      asset dragged onto its **Settings** field.
   2. **Add Component → RTMPE → Connection Bootstrap**, set as follows:

      | Field | Value |
      | --- | --- |
      | **Connect On Start** | Ticked |
      | **Entry Policy** | `Matchmaking` |
      | **Matchmaking Mode** | `shared-world` (any text, the same on every client) |
      | **Max Players** | `4` |
      | **Player Prefab** | Empty: the sample has no avatars |

   3. **Add Component → RTMPE → World Spawner**, with the `World` prefab
      dragged onto its **World Prefab** field. The spawner has the room's host
      spawn the world once, on entering the room; the key and the
      shared-authority setting come from the prefab's World Authority.
   4. **Add Component → Scripts → RTMPE.Samples.SharedWorld →
      Shared World Board**.
8. **Press Play.** The Editor client enters a room alone, spawns the world,
   and a board of 8 × 8 cells appears with six dots on it. Click a dot: it
   disappears, another appears elsewhere, and the eaten count goes up. The
   host sends itself the same RPC that every other client uses.
9. **Run a second client.** Stop Play. In **File → Build Settings**, add
   `Scenes/SharedWorld.unity` as the first scene and build a player. Give the
   player an API key as described in the next section. Then press Play in the
   Editor and start the player: the first client in the room is its host.

### Giving the built player its API key

The Editor reads the key from the Setup Wizard's vault; a built player cannot.
The SDK asks these sources in order and uses the first that has a key:

| Source | How to use it |
| --- | --- |
| A provider your code registers | Call `ApiKeySource.SetProvider` before the Connection Bootstrap connects. This is the route for a game you ship. |
| The Setup Wizard, for a development build | Tick **Inject this key into development builds** on the wizard's **Gateway Configuration** step, then build with **Development Build** ticked. A release build never carries the key, and Android builds cannot read it. |
| `--rtmpe-api-key-file <path>` | Start the player with the path of a file that contains only the key. |
| `--rtmpe-api-key <key>` | Works too, but other accounts on the machine can read a process's command line; prefer the file. |
| `RTMPE_API_KEY` | Set the environment variable in the shell you start the player from. |

For example, from a Linux or macOS shell:

```bash
printf '%s' 'YOUR_PROJECT_API_KEY' > ~/.rtmpe-key
chmod 600 ~/.rtmpe-key
./SharedWorld.x86_64 --rtmpe-api-key-file ~/.rtmpe-key                   # Linux
open -n ./SharedWorld.app --args --rtmpe-api-key-file ~/.rtmpe-key       # macOS
```

Give the option an absolute path: the SDK reads the path exactly as given.
Both clients use the same key; each still gets its own player identity when it
connects. For a game you ship, see
[Giving a player build its API key](https://github.com/rtmpengine/unity-rtmpe-sdk-automation/blob/main/Documentation~/getting-started.md#giving-a-player-build-its-api-key)
in the SDK documentation.

## What you should see

The same six dots in both windows. Click one in either window and it
disappears in both, with a new dot appearing at the same place in both. The
eaten count is the same everywhere.

Above the board, each window shows three lines:

```
RTMPE state: InRoom
Role: host
World: object 12884901889 · owned here · eaten 3
```

The object id differs from run to run. The other window shows `Role: guest`
and `owned by another client` over the same object id and the same count.

Then try the three things the world object is for:

1. **A late joiner sees the board as it is.** Eat a few dots in the first
   client, then start the second. It arrives to the current board and the
   current count, not to six new dots and zero.
2. **The host leaving does not stop the world.** End the host: stop Play if
   the Editor is the host, or quit the player. In the remaining window the
   role line changes to `host`, the world line reads `owned here` with the
   same object id, and the board and the count are unchanged: the SDK has
   handed the world to the new host. If the world line reads
   `owned here (migrated copy)` with a new object id, the new host re-created
   the world under its own identity and carried every value across; the board
   and the count are unchanged all the same.
3. **A client joining after that sees the world too.** Start the client you
   ended again. It receives the world, owned by the new host.

## How it works

| Part | What it does |
| --- | --- |
| **World Authority** (`RtmpeWorldAuthority`, part of the SDK) | Marks the prefab as the room's world: one per **World Key** per room, owned by the host, handed to the next host when the host leaves, and sent to clients who join late. Game code finds it with `RtmpeWorldAuthority.Find`. On the owner, `OnWorldBorn` is raised when a world has none of the room's state yet: the place to fill it. |
| **World Spawner** (`RtmpeWorldSpawner`, part of the SDK) | Has the room's host spawn the world prefab once, on entering the room. |
| `SharedWorldState.cs` | The world's state: the food cells as a `NetworkVariableListVector2Int` and the eaten count as a `NetworkVariableInt`, both created in `OnNetworkSpawn`. On `OnWorldBorn` it fills the board up to six cells. `AskToEat(cell)` sends the `RequestEat` RPC to every client's copy of the world; only the owner's copy applies it, and the change reaches everyone through the list and the count. |
| `SharedWorldBoard.cs` | Draws the readout and the board with `OnGUI`. It looks the world up with `RtmpeWorldAuthority.Find` on every repaint rather than keeping a reference, because a host change can replace the object. A click on a dot calls `AskToEat`. |

What the sample leaves out:

- **Avatars.** The world is the only networked object. Add a **Player Prefab**
  to the Connection Bootstrap to spawn avatars beside it; nothing here changes.
- **Checking requests.** The owner eats whatever cell it is asked about,
  because `RequestEat` declares no `Caller`. A method can limit who may call it
  with the `Caller` property of `[RtmpeRpc]`: `RpcCaller.Owner`,
  `RpcCaller.Host` or `RpcCaller.Server`. A decision no client may make
  belongs in your project's server function; see
  [Remote procedure calls](https://github.com/rtmpengine/unity-rtmpe-sdk-automation/blob/main/Documentation~/api/index.md#remote-procedure-calls)
  in the API reference.

## Troubleshooting

In the Editor, messages go to the Console. A built player writes them to
`Player.log`: `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\` on
Windows, `~/Library/Logs/<CompanyName>/<ProductName>/` on macOS and
`~/.config/unity3d/<CompanyName>/<ProductName>/` on Linux.

| Symptom | Cause | Fix |
| --- | --- | --- |
| `World: not here yet` stays in a window whose role is `host` | The world was not spawned. The Console says why: `RtmpeWorldSpawner has no world to spawn` (the **World Prefab** field is empty, or the prefab has no World Authority), `no prefab id is registered for World`, `has an inactive root` or `carries a World Authority that is switched off`. | Assign the prefab (step 7), give it an id (step 6), or tick the checkbox beside the prefab's name or the World Authority component's own checkbox. |
| `World: not here yet` stays in a window whose role is `guest` | The host has not spawned the world. A moment's wait after joining is normal. | Read the host's Console or `Player.log`; the row above lists the messages that say why. |
| Both windows show `Role: host`, with different object ids | The clients are in different rooms. | Use the same **Matchmaking Mode** on every client. |
| `Role: host` above `owned by another client` | `TransferMasterClient` moved the host role; the world moves only when its owner leaves. | Nothing to fix: the previous host still applies the requests, and the board works. |
| Clicking a dot does nothing in a guest's window | **Shared Authority** is off on the prefab's World Authority, so only the owner can send the world an RPC. | Tick **Shared Authority** on the prefab. |
| After the host left, the other window still shows `Role: guest` | The host's process ended without leaving the room: it was killed, crashed or lost its network. Such a host stays in the room until its connection times out, and the other client becomes host only then. | Wait: the window changes to `Role: host` once the departed host's connection has timed out. A host that stops Play or quits normally leaves at once, and the other client takes over straight away. |
| After the host left, the eaten count is back at `0` or a new board appears | The new host had not received all of the world's state: it joined only moments before, or the previous host's process was killed. The world is then treated as new on that client: `OnWorldBorn` is raised, the script fills the board up to six cells, and every window takes the new host's values. | Nothing to fix. |
| No room is entered, and the Console shows an `[RTMPE]` error that names `apiKeySealServerPublicKeyHex` or says `Server not pinned` | A key from the portal is missing from the settings asset, or the two keys are swapped. | Enter the portal's **Sealed-Box Public Key (X25519)** and **Pinned Server Public Key** in the Setup Wizard and press **Finish** again. |
| No room is entered, and after about 10 seconds the Console shows `[NM] connection failed after` | The server did not answer: **Server Host** or **Server Port** is wrong, a firewall drops UDP, or a key does not belong to your project. | Check the values against the portal's **Connection settings**, and allow outgoing UDP. |
| The built player never connects, and `Player.log` says there is no API key | No source had a key. | See [Giving the built player its API key](#giving-the-built-player-its-api-key). |
