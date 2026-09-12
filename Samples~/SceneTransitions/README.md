# Scene Transitions Sample

A room that moves between two scenes, with **no scene-loading code at all**.

The whole of the loading is one component you attach: `RtmpeSceneLoader`. It
listens for the room's scene instruction, loads the scene in the mode the room
asked for, and reports back when the load finishes. The sample's own script does
the other half — deciding *when* to change scene — which is a game decision and
stays yours.

## What is in the box

| | |
|---|---|
| `Scripts/SceneSwitcher.cs` | The host's decision: a button asks the room to load the other scene. Two on-screen buttons and one call, and none of it loads anything. |
| — | There is no loader script here. `RtmpeSceneLoader` ships in the SDK. |

## Setting it up

1. Two scenes of your own — this README calls them `Arena` and `Lobby` — both
   added to **File → Build Settings → Scenes In Build**. Loading by name is what
   the room instruction carries, and Unity can only load a scene it was built
   with.
2. A **boot scene** you start in — one the room never loads — holding one
   GameObject with:
   - `NetworkManager` (with your `NetworkSettings` asset), and
   - `RtmpeSceneLoader`, and
   - `SceneSwitcher` from this sample.

   ⚠️ **A root GameObject, in a scene the room never loads.** Two rules, and
   both have teeth:

   - *Root*, because a `Single` load destroys every object in every loaded
     scene: a loader living inside one destroys itself with the load it started,
     never reports ready, and leaves the rest of the room waiting until the
     readiness deadline expires. On a root object the loader makes itself
     persistent in `Awake`; on the object that already carries the
     `NetworkManager` it is persistent for the same reason. If it is neither,
     the loader says so before the load begins.
   - *A scene the room never loads*, because the loader is persistent: a copy
     sitting in `Arena` becomes a **second** persistent loader every time the
     room returns to `Arena`, and another on the next return. Only the most
     recently enabled one acts — so the scene is not loaded once per copy — and
     it says so in the console, because the fix is a placement rather than
     anything you can code around. It is the same rule `NetworkManager` states
     for itself.

3. Connect, create or join a room, and press one of the two on-screen buttons
   as the host.

   ⚠️ The buttons are `OnGUI`, not `Input`: `UnityEngine.Input` throws on a
   project configured for the new Input System package with the old Input
   Manager disabled, and a sample that cannot run in a supported project
   teaches nothing.

## What happens

The host writes the room's `__scene` property. The server broadcasts it to
everybody, the SDK raises `OnSceneLoadStartedWithMode`, and every client's
loader — the host's included — loads the scene and reports. When the last report
arrives, every client is raised `OnAllPlayersSceneLoaded`, which is where a game
starts the match.

Pressing the same button twice **reloads** the scene rather than doing nothing:
re-issuing the scene the room is already on is how a restart is expressed, and
every client reloads.

## What this sample deliberately does not do

- **No loading screen.** Subscribe to `NetworkManager.Instance.Scene.OnSceneLoadStartedWithMode`
  yourself and show one; the loader does not own the UI.
- **No staged loading, no held-back gameplay.** A title that wants to finish
  loading and then wait — for an animation, for a countdown — should not attach
  `RtmpeSceneLoader` at all. Its events stay public for exactly that: automation
  removes the common pattern, it does not confiscate the particular one.
- **No authority *decision*.** The buttons are disabled unless this client is
  the host and in a room — but that is about not throwing, not about permission:
  `Scene.LoadScene` refuses locally, **by throwing**, for a caller who is not in
  a room or is not the master client, so a button wired straight to it throws
  the first time anybody presses it before joining. Who may actually change the
  room's scene is the **server's** decision, and a client-side answer is only
  ever an opinion about a roster this client may already be behind on — one that
  would refuse a legitimate host mid-handover.
