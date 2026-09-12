# Two Player Room Sample

Two clients, one room, both capsules moving. It is the sample you build **twice**
— once as the Editor and once as a player — because a second client is the only
thing that shows whether multiplayer is working, and it is the only thing that
shows the two mistakes this sample exists to make impossible.

## What is in the box

| | |
|---|---|
| `Scripts/TwoPlayerAvatar.cs` | What goes on the player prefab: owner-driven WASD motion, and a `[RequireComponent]` that pulls in **both** motion components. It derives from `NetworkBehaviour`, and that abstract base is never added as a component on its own. |
| `Scripts/TwoPlayerCredentials.cs` | The API key, handed to the SDK from outside the scene asset. This is the component a built player needs and the Editor does not. |
| `Scripts/TwoPlayerSpawnPoints.cs` | Where each client's avatar appears. Four points on a ring, chosen through the `ChooseSpawnPose` seam — without it both clients spawn at the same point, half-buried in the ground. |
| `Scripts/TwoPlayerRoomHud.cs` | Four lines on screen — connection state, in-room, how many avatars are spawned in this process, and whether this client was given an API key at all. It is how you tell, from either window, that the second client arrived, and why it did not. |
| `Scenes/TwoPlayerRoom.unity` | A camera, a light, a ground plane, an empty `[RTMPE] Session` object and a `Player` capsule. The wiring below is what you add to it. |

## Prerequisites

Start in the [RTMPE Developer Portal](https://portal.rtmpengine.com/dashboard):
the gateway address and the key below belong to a project created there, and
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
- One API key, supplied from **outside the project** — see *Run two clients*.

## Setting it up

1. **Import.** **Window → Package Manager → RTMPE SDK → Samples → Two Player
   Room → Import**. Unity copies it to
   `Assets/Samples/RTMPE SDK/<version>/Two Player Room/`.

2. **Open** `Scenes/TwoPlayerRoom.unity`. It holds Main Camera, Directional
   Light, Ground, an empty `[RTMPE] Session` object and a `Player` capsule.

   ⛔ `[RTMPE] Session` deliberately carries nothing but its transform. A
   `NetworkManager` authored into a shipped scene would reference a
   `NetworkSettings` asset that does not exist in your project, and a blank
   settings reference is not an error — `Awake` substitutes an empty default
   that points at loopback and completes no handshake. The three steps below are
   what bind it to *your* project.

3. **The avatar prefab.**
   1. Select the `Player` capsule already in the scene.
   2. **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom → Two Player
      Avatar**.

      That is where Unity files a script of your own: under **Scripts**, by its
      namespace, under a name it has nicified from the class. The SDK's own
      components live in **RTMPE** instead because they carry
      `[AddComponentMenu]`; the sample scripts deliberately do not, so what you
      see here is what you will see for every script you write next.

      Watch what Unity adds with it: **Network Transform** and **Network
      Transform Interpolator**, both at once, because the script asks for both.
      Try to remove either and Unity refuses while the avatar script is
      attached. That is the whole point of the pair — see *A capsule that never
      moves* below.
   3. Drag `Player` into a `Prefabs/` folder to make a prefab, and delete the
      copy left in the scene. Every avatar in the room is spawned from the
      prefab; a copy left behind belongs to nobody.

4. **Give the prefab an id.** Open **Window → RTMPE → Network Prefabs**. There
   is nothing to drag into that window — it has no drop target and no object
   field. It reads your **Project window** selection instead, and it works in
   two steps that are easy to mistake for one.

   1. **Allocate the id.** Either:

      - select the `Player` prefab **in the Project window** — not in the scene
        — and press `Allocate id for selection`. Until something is selected the
        window reads `— select a prefab in the Project window —` and the button
        is greyed out; or
      - press `Scan the project for spawnable prefabs with no id`, then
        `Allocate an id for all 1`. The count in that button is however many the
        scan found.

   2. **Then** press `Generate RtmpePrefabIds.cs and the prefab registry`. It
      writes `Assets/RTMPE/Generated/RtmpePrefabIds.cs` and
      `Assets/RTMPE/Generated/RtmpePrefabRegistry.asset`, and the window names
      both paths above the button.

   Finally, drag `RtmpePrefabRegistry` onto the `NetworkSettings` asset's
   **Prefab Registry** field.

   ⚠️ **Generate does not allocate anything.** It writes out whatever the ledger
   already holds — and on an empty ledger it writes a file whose only content is
   the comment `// The ledger records no prefabs.` and reports **success**. So
   pressing Generate alone gets you a green message here and
   `no prefab id is registered for …` at runtime. Step 4.1 is the one that
   allocates.

   ⛔ Not optional, and there is no number to type instead: at spawn time the
   SDK is asked which id *this session* would spawn that prefab under, and
   without a registration there is no answer.

5. **The session object.** Select `[RTMPE] Session` and add the components
   below — all of them on that one object, because two of them find each
   other with `GetComponent`:

   1. **Component → RTMPE → NetworkManager**, with your `NetworkSettings` asset
      dragged onto its **Settings** field.
   2. **Component → RTMPE → Connection Bootstrap**, set to:

      | Field | Value | Why |
      | --- | --- | --- |
      | **Connect On Start** | ticked | nothing else calls `Connect()` in this sample |
      | **Entry Policy** | `Matchmaking` | see below |
      | **Matchmaking Mode** | `two-player-room` | any string, the same on both clients |
      | **Max Players** | `2` | the capacity of the room the server creates, so it fills at two and the next pair gets a room of their own |
      | **Player Prefab** | the prefab from step 4 | spawned for this client on entering the room |

      **Matchmaking rather than Create Room, so that no room id is ever typed.**
      The server *"atomically finds an open waiting room that matches
      `MatchmakingOptions.Mode` … joins the player, or creates a new room if none
      is available"* — `Runtime/Rooms/MatchmakingManager.cs`, lines 252–254. So
      the first client to arrive opens the room and the second one joins it, with
      neither of them naming it and no order to get right. Creating a room does
      the opposite: two clients that each *create* one end up in two rooms of the
      same name and never meet.
   3. **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom → Two Player
      Spawn Points**, on this same object.

      **Without it both clients spawn in the same place, waist-deep in the
      ground, and you see one capsule while the readout says `2`.** The
      bootstrap spawns the local player at **its own transform** unless
      something answers `ChooseSpawnPose`, and its own transform is
      `[RTMPE] Session` — one point, at the origin, therefore the *same* point
      on both clients. `TwoPlayerSpawnPoints` answers it with a ring of four
      points at `y = 1`, which is where a two-unit capsule's centre has to be to
      stand on a plane at `y = 0`.

      **How it keeps the two clients apart.** The room's host takes the first
      point and nobody else can reach it — a room has exactly one host — and
      every other client is spread over the remaining points by its own gateway
      session id. For two clients that is a guarantee. For a third and a fourth
      it is a hash and two of them can collide, because **this SDK has no
      per-room seat index**: `PlayerInfo` carries no ordinal, and the order of
      `CurrentRoom.Players` comes from a server-side map iteration, so two
      clients can read the same roster in different orders. A game that needs
      guaranteed-distinct spawns has to claim a point through the room's shared
      properties. The component says all of this in its own comments, which is
      where you will be when you change it.

   4. **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom → Two Player
      Credentials**, and **Add Component → Scripts → RTMPE.Samples.TwoPlayerRoom
      → Two Player Room Hud**.

      `TwoPlayerCredentials` has no fields, and it must never acquire one for the
      key: Unity writes a serialized string into the scene asset, which is
      committed and present in every build made from it. Where the key comes from
      instead is the next section.

6. **Press Play.** One client alone reaches the room and stands there. That is
   the state this sample exists to get you out of.

## Run two clients

The two clients are **the Editor** and **a built player**, and they differ in
exactly one thing: where the API key comes from.

**Client 1 — the Editor.** Nothing to do. The Setup Wizard stored your key in
your platform credential vault, and the SDK reads that vault back in the Editor.

**Client 2 — the built player.** **File → Build Settings**, add
`Scenes/TwoPlayerRoom.unity`, and build. The wizard's vault does **not** travel
into the build: the code that reads it lives in the Editor assembly, which no
player compiles. Give the built player the key one of these ways, in the order
the SDK consults them:

| Source | How |
| --- | --- |
| a provider you register | call `TwoPlayerCredentials.Supply("<key>")` from your own code before the scene loads — this is the `ApiKeySource.SetProvider` seam, and the only one a game you distribute can use |
| `--rtmpe-api-key-file <path>` | `chmod 600` the file first; it is the form to use on any shared machine |
| `RTMPE_API_KEY` | set it in the environment you launch from |

So, from a Linux or macOS shell:

```bash
printf '%s' 'YOUR_PROJECT_API_KEY' > ~/.rtmpe-key
chmod 600 ~/.rtmpe-key
./TwoPlayerRoom --rtmpe-api-key-file ~/.rtmpe-key
```

⚠️ **Give the option an absolute path.** `~` above is expanded by your *shell*
before the player ever sees it; the SDK calls `File.ReadAllText` on whatever
string it is handed and expands nothing. A `~` that reaches the option — from a
launcher, a `.desktop` entry, a CI step, or a quoted argument — is read as a
directory literally named `~`, and the launch fails with a file-not-found the
path in it looks correct for.

On Windows the option is the same and the permissions are not: write the file,
then remove inherited access to it so only your account can read it.

```
TwoPlayerRoom.exe --rtmpe-api-key-file C:\Users\you\rtmpe.key
```

⚠️ **`--rtmpe-api-key <key>` also works and is the wrong habit.** A command line
is readable by other accounts on the machine — through `ps` and `/proc` on Linux
and macOS, and through the process list on Windows — so the key reaches whoever
is logged in beside you. The file form does the same job and does not.

⛔ **There is no API-key field on any component in this sample, and the Inspector
is the one place the key must not come from.** A field there is written into the
scene asset, committed with it, and shipped inside the player.

**Both clients use the same key, and that is correct.** An API key identifies
your *project*, not a player: it is what the gateway checks at the handshake to
decide which project's rooms you may enter. Who you are *inside* the room is a
separate identity the server issues when the session is established, so two
clients presenting one key are still two players.

**Two of them on one machine is fine.** The SDK binds its UDP socket to port `0`
and the OS assigns an ephemeral source port
(`Runtime/Infrastructure/Transport/UdpTransport.cs`), so a second client on the
same machine takes a different port with nothing to configure. On macOS a second
copy of the same `.app` needs `open -n`:

```bash
open -n ./TwoPlayerRoom.app --args --rtmpe-api-key-file ~/.rtmpe-key
```

Now press Play in the Editor and launch the player. Move one with **WASD** and
watch it move in the other window.

## What you should see

**Two capsules, standing on the ground, 4 to 6 units apart, each turned to face
the middle of the ring** — one at the near edge of the ring and one somewhere
else on it. Both windows show both capsules; move one with **WASD** and it moves
in the other window too.

The two look straight at each other only when the second client lands on the
point opposite the host's; on either of the other two it stands side-on. That is
the sample working — every point on the ring faces the middle, not the host.

And in each window, the readout reaching:

```
RTMPE state: InRoom
In room: yes
Avatars spawned here: 2
API key: supplied
```

`Avatars spawned here` is the number that answers the question. It counts every
avatar spawned in *this* process — the local player's and every replica — so it
reads `1` while you are alone and `2` the moment the second client is in the
room. If one window says `2` and the other says `1`, the second client entered a
different room: check that both are using the same **Matchmaking Mode**.

`API key` is the line that answers the commonest failure. `supplied` means a
source answered before this client tried to connect; `MISSING` means none did,
and nothing will happen no matter how long you wait.

## A capsule that never moves

If the other player's capsule appears and then stands perfectly still while your
own moves, its `NetworkTransformInterpolator` is either absent or switched off —
and the SDK says which in the console, with the offending object's own id and
name in place of the two below:

> `[RTMPE] Networked object 42 ('Player(Clone)') is receiving remote transform
> updates but carries no NetworkTransformInterpolator, so the receive path has
> nowhere to apply the motion: the object stays frozen on this client while its
> replication traffic continues to arrive.  Add a NetworkTransformInterpolator
> component to the prefab alongside NetworkTransform.  Logged once per object,
> for the first few hundred objects of a session.`

The other cause reads differently, and so does its remedy — a component is
already there and adding a second one does not help, because `GetComponent`
hands back the first in add order and that is the one still switched off:

> `[RTMPE] Networked object 42 ('Player(Clone)') is receiving remote transform
> updates, and its NetworkTransformInterpolator is switched off: the component
> never runs, so the motion is buffered and never rendered and the object stays
> frozen on this client while its replication traffic continues to arrive.
> Enable the NetworkTransformInterpolator already on the object — a second one
> is not needed and does not help.  Logged once per object, for the first few
> hundred objects of a session.`

⚠️ `[RequireComponent]` — which `TwoPlayerAvatar` carries — guarantees the
component is *present*, never that it is *ticked*, so it prevents the first
cause and not the second.

Note what it is *not*: not stutter, not lag, not a dropped packet. Updates are
arriving normally and there is nothing on the object to apply them, so the pose
never changes. `NetworkTransform` puts the owner's pose on the wire;
`NetworkTransformInterpolator` is what plays it back on every other client.

⚠️ **It needs a second client to fire**, which is why this sample exists in the
shape it does: a developer testing alone never sees the advisory, because with
nobody else in the room nothing remote is arriving. `TwoPlayerAvatar` states the
requirement instead, so the components arrive together and Unity will not let
either go.

## What this sample deliberately does not do

- **It does not fetch your key from anywhere.** `TwoPlayerCredentials.Supply` is
  the seam and the fetch is yours. A fetcher shipped here would be an opinion
  about somebody else's backend and a credential-handling surface this package
  would then own. `Func<string>` is also synchronous — it returns, it does not
  await — so a key that comes from a backend has to be in hand first: clear
  **Connect On Start**, run your fetch, call `Supply`, then call the bootstrap's
  own `Connect()`. See
  **Giving a player build its API key** in the package's own
  `Documentation~/getting-started.md`. A folder whose name ends in `~` is
  hidden from Unity and is not copied when a sample is imported, so open it
  from the package itself — Package Manager's ⋮ menu → **Show in Explorer**
  / **Reveal in Finder** lands in the right folder.
- **It writes no networking code.** Connecting, entering the room and spawning
  the avatar are all `RtmpeConnectionBootstrap`. If you want to read that flow as
  code rather than as a component, the
  **Player Spawn Flow** sample — imported beside this one, under its own
  display name — is the same sequence written out by hand.
- **It replicates nothing but the transform.** There is no score, no health and
  no RPC here. Those are `NetworkVariable` and `[RtmpeRpc]`, and adding them to
  `TwoPlayerAvatar` changes nothing about the two-client setup above.
- **It reads input through the legacy Input Manager.** On a project configured
  for the Input System package alone `UnityEngine.Input` throws, so set **Active
  Input Handling** to *Both* in Player settings, or delete `Update` from
  `TwoPlayerAvatar` — the two-client flow does not depend on input. The readout
  is `OnGUI` and needs neither.

## Troubleshooting

### Where the messages are

In the Editor they are in the Console. In a **built player** there is no
console, and every message this page quotes goes to `Player.log`:

| Platform | Path |
| --- | --- |
| Linux | `~/.config/unity3d/<CompanyName>/<ProductName>/Player.log` |
| Windows | `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\Player.log` |
| macOS | `~/Library/Logs/<CompanyName>/<ProductName>/Player.log` |

`<CompanyName>` and `<ProductName>` are the two fields at the top of **Edit →
Project Settings → Player**; a project that has never set them is
`DefaultCompany` and the name of the project folder. On Linux you can watch it
while the player runs:

```bash
tail -f ~/.config/unity3d/DefaultCompany/TwoPlayerRoom/Player.log
```

The `API key` line in the on-screen readout is there so that the one failure you
hit most often does not need any of this.

### Symptoms

| Symptom | Likely cause |
| --- | --- |
| The readout says `API key: MISSING` | No source supplied one. In the Editor, store it via **Window → RTMPE → Setup Wizard**. In a player the wizard's vault does not travel — call `TwoPlayerCredentials.Supply`, launch with `--rtmpe-api-key-file <path>`, or set `RTMPE_API_KEY`; they are consulted in that order. |
| The readout says `API key: nothing asked` | `TwoPlayerCredentials` is not in the scene, so nothing has looked. Step 5.4. |
| `No API key — nothing will connect` in the console | The same thing, said by `TwoPlayerCredentials` at startup. In a built player it is in `Player.log`, not on screen. |
| One capsule, waist-deep in the ground, while the readout says `2` | `TwoPlayerSpawnPoints` is missing from `[RTMPE] Session`, or is on a different object from **Connection Bootstrap**. Both clients then spawn at the bootstrap's own transform, which is one point at the origin — so the two capsules are in the same place and each is buried to its middle. Step 5.3; the component warns about the second case in the console. |
| Two capsules, apart, but both sunk into the ground | The avatar prefab's pivot is not at its centre, or it is not the two-unit built-in Capsule. `StandingHeight` in `TwoPlayerSpawnPoints` is the number to change. |
| Both windows say `Avatars spawned here: 1` | The two clients are in different rooms. **Matchmaking Mode** must be the same string on both, and **Max Players** must be at least 2. |
| `Avatars spawned here` starts at `2` or `4` on a fresh Play | Nothing is wrong now, but this is what the count did before it was re-armed on play-mode entry: with **Enter Play Mode Options** on and domain reload off, statics survive play-mode exit and the despawn that would decrement it is never reached. If you copy the counter into your own code, copy its `[RuntimeInitializeOnLoadMethod]` with it. |
| The remote capsule appears and never moves | See *A capsule that never moves* above. |
| `no prefab id is registered for …` | Step 4.1 was skipped, or only step 4.2 was done. Pressing `Generate …` on an empty ledger reports success and allocates nothing — the prefab needs `Allocate id for selection` (or the scan-and-allocate pair) first. Also check that `RtmpePrefabRegistry` really is on the settings asset's **Prefab Registry** field. |
| Stuck connecting, then `Connection failed` | `apiKeySealServerPublicKeyHex` is blank, or is not the X25519 key your gateway holds the private half of. |
| `Connection failed` with a signature error | `pinnedServerPublicKeyHex` does not match the gateway's Ed25519 key. For local development set `serverPinningMode` to `InsecureNoPinning`, or `TrustOnFirstUse` to capture the key on first connect — under the default `Strict` mode, clearing the pin refuses every connection rather than disabling pinning. |
