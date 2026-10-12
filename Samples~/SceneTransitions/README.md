# Scene Transitions

A room that moves between two scenes with no scene-loading code. The SDK's
**Scene Loader** component loads the scene the room asks for on every client
and reports back when the load is done; the sample's one script only decides
when to ask.

The sample contains one script, `Scripts/SceneSwitcher.cs`, and no scene. You
provide a boot scene and the two scenes the room moves between.

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
   pass over the steps that add components to the open scene; step 5 adds
   them to the boot scene.
3. **Import the sample.** In **Window → Package Manager**, select
   **RTMPE SDK**, open **Samples** and press **Import** next to
   **Scene Transitions**. Unity copies it to
   `Assets/Samples/RTMPE SDK/<version>/Scene Transitions/`.
4. **Create the scenes.** Create three scenes: a boot scene to start in, and
   the two scenes the room moves between. This guide calls them `Arena` and
   `Lobby`, the names `SceneSwitcher` uses by default. Add all three to
   **File → Build Settings** with the boot scene first: Unity loads a scene by
   name only when it is in that list.
5. **Set up the boot scene.** In the boot scene, create an empty GameObject at
   the root of the Hierarchy (not as a child of another object) and add:
   1. **Add Component → RTMPE → NetworkManager**, with the `NetworkSettings`
      asset from step 2 dragged onto its **Settings** field.
   2. **Add Component → RTMPE → Connection Bootstrap**, which connects and
      enters a room when Play starts. Set **Connect On Start** ticked,
      **Entry Policy** to `Matchmaking`, **Matchmaking Mode** to
      `scene-transitions` (any text, the same on every client) and
      **Max Players** to `4`. Leave **Player Prefab** empty: this sample
      spawns nothing.
   3. **Add Component → RTMPE → Scene Loader**.
   4. **Add Component → Scripts → RTMPE.Samples.SceneTransitions →
      Scene Switcher**. Its **First Scene** and **Second Scene** fields name
      the two scenes; change them if yours have other names.
6. **Press Play** with the boot scene open.

To watch several clients change scene together, build a player with the boot
scene first in the build list and start it while the Editor is in Play mode;
both clients enter the same room because they ask for the same
**Matchmaking Mode**. A built player needs its own API key. For a test build,
tick **Inject this key into development builds** in the Setup Wizard and build
with **Development Build** ticked; see
[Giving a player build its API key](https://github.com/rtmpengine/unity-rtmpe-sdk-automation/blob/main/Documentation~/getting-started.md#giving-a-player-build-its-api-key)
for the other ways.

## What you should see

A box at the top left of the Game view with a headline and two buttons named
after the scenes.

- While the client connects, the headline reads `[RTMPE] join a room first`
  and the buttons are disabled.
- In the room, the host's headline reads
  `[RTMPE] you are the host — pick a scene`; every other client reads
  `[RTMPE] the host chooses the scene`.
- When the host presses a button, every client in the room loads that scene.
  The box stays on screen, because the object that carries it persists across
  scene loads.
- Pressing the button for the scene the room is already on reloads it on
  every client. That is how a round restart is expressed.

The Console also logs that the Connection Bootstrap
`entered the room and has no Player Prefab`. That is expected here.

## How it works

| Part | What it does |
| --- | --- |
| `SceneSwitcher.cs` | Draws the headline and the two buttons with `OnGUI`, so it works with either input system. The buttons are enabled only while this client is in a room, because `NetworkSceneManager.LoadScene` throws when the caller is not in one. They are not limited to the host: the server decides who may change the room's scene. A press calls `NetworkManager.Instance.Scene.LoadScene(sceneName)`, which sets the room's scene for everyone in it. |
| **Scene Loader** (`RtmpeSceneLoader`, part of the SDK) | When the room's scene changes, it loads that scene on this client in the mode the room asked for, then reports this client ready. When every client has reported, `NetworkSceneManager.OnAllPlayersSceneLoaded` is raised on each of them; a game starts its match there. |

**Where the components live.** Put them on a root object in the boot scene,
and in no other scene:

- **A root object**, because a `Single` load destroys every object in the
  scenes it replaces. The NetworkManager, the Connection Bootstrap and the
  Scene Loader each keep their root object alive across loads. On a child
  object, the loader would be destroyed by its own load, and its client would
  never report ready. The loader logs an error before such a load starts.
- **The boot scene only**, because the room never loads it. A loader placed in
  `Arena` or `Lobby` becomes a second persistent loader each time the room
  returns there. Only the most recently enabled loader acts, and it logs that
  it has taken over.

**Taking over the loading.** For a loading screen, subscribe to
`NetworkSceneManager.OnSceneLoadStartedWithMode` and show one; the loader draws
nothing. To finish loading and then wait, for an animation or a countdown, leave
the Scene Loader out: load the scene yourself when that event is raised, and
call `NetworkManager.Instance.Scene.ReportReady()` when this client is ready.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| The headline reads `[RTMPE] not connected` | The boot scene has no `NetworkManager`. | Add one as in step 5. |
| The headline stays at `[RTMPE] join a room first` | This client never entered a room. | Read the Console: messages from the bootstrap start with `[RTMPE] RtmpeConnectionBootstrap`, and an error that names `apiKeySealServerPublicKeyHex` or says `Server not pinned` means a key from the portal is missing. Enter the portal's values in the Setup Wizard and press **Finish** again. |
| A button pressed in another client's window changes nothing | Only the room's host may change the scene. The Console warns that the property is accepted by the server only from the room's host, and the refusal reaches `Rooms.OnRoomError`. | Press the button in the host's window. |
| `the engine refused to load scene '…'` | The scene is not in the build list, or **First Scene** or **Second Scene** is misspelled. | Add the scene in **File → Build Settings**, or correct the name. |
| `RtmpeSceneLoader on '…' is about to load a scene in Single mode, which destroys it` | The loader is on a child object. | Move the components to an object at the root of the boot scene. |
| `A second RtmpeSceneLoader ('…') has taken over from an earlier one` | A copy of the loader sits in a scene the room loads. | Keep the loader in the boot scene only. |
