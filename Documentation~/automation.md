# Automation — scoring a project and converting it

> SDK Version: `com.rtmpe.sdk 1.0.7`

The SDK can measure how ready your scripts are for multiplayer and convert them
for you. This page covers the tools that do it, what they need, and the loop you
repeat — score, convert, re-score — until nothing is left to convert.

Scoring reads only your source code, so you can run it at any time. Converting
is most useful once the project is set up: first
[create your project in the dashboard](getting-started.md#3-before-unity--create-your-project-in-the-dashboard)
and run the [Setup Wizard](getting-started.md#4-the-supported-path--the-setup-wizard),
then continue with the runtime steps in [Getting Started](getting-started.md).

## Contents

- [What the automation is](#what-the-automation-is)
- [Prerequisites](#prerequisites)
- [The loop](#the-loop)
- [Verifying with a run](#verifying-with-a-run)
- [Command reference](#command-reference)
- [What the conversion does not do](#what-the-conversion-does-not-do)
- [When something does not work](#when-something-does-not-work)
- [Related pages](#related-pages)

---

## What the automation is

| Part | What it does | Where it is |
|---|---|---|
| Analyzers | report what stands between a script and a networked one — see the [rule reference](diagnostics.md) | `Analyzers/` in this package; Unity and your IDE load them |
| IDE quick fixes | apply four of those fixes from the lightbulb | `Analyzers/` in this package; your IDE loads them |
| Conversion engine | scores every script and writes the readiness report; rewrites source for a conversion | `Automation~/` in this package |
| Network Readiness window | shows the report, asks the authority questions, records runtime checks | **Window → RTMPE → Network Readiness** |
| Conversion Wizard | previews a conversion from the engine and applies it once you approve the diff | **Window → RTMPE → Conversion Wizard** |

The engine is a .NET 8 program, not Unity code. It ships in `Automation~`
because Unity's asset pipeline skips any folder whose name ends in `~`, so the
engine's sources travel with the package without being imported into or
compiled by your project. The Conversion Wizard runs the engine directly. The
Network Readiness window never runs it: it reads the report the engine writes,
and saves your answers and runtime outcomes beside it.

On this page, `<package>` means the folder that holds the SDK's `package.json`:
under `Library/PackageCache/` in your project for a registry or Git install,
under `Packages/` for an embedded package, or the folder you added it from for a
local install.

### IDE quick fixes

Four rules carry a lightbulb:

| Rule | Lightbulb entry | What it changes |
|---|---|---|
| `RTMPE1020` | *Call base.OnDestroy()*, or *Call base.OnDestroy() and override the hook* when the method hides the base one | appends `base.OnDestroy();`, and turns `private void OnDestroy()` into `protected override void OnDestroy()` where that is legal |
| `RTMPE2001` | *Rebase to NetworkBehaviour* | changes the base class to `NetworkBehaviour` and adds `using RTMPE.Core;` |
| `RTMPE2002` | *Convert to NetworkVariable*, or *Add companion NetworkVariable* on a serialised field | turns a private field into a `NetworkVariable`, or adds a `NetworkVariable` seeded from an Inspector field |
| `RTMPE2003` | *Add the IsOwner guard* | inserts `if (!IsOwner) return;` as the first statement of the reported frame loop |

`RTMPE1020` is an `Error`, so it also appears in the Unity Console; the other
three are `Info` and appear only in your IDE.
[Where each diagnostic appears](diagnostics.md#where-diagnostics-appear)
explains the difference, and the rule reference lists the conditions under
which each fix is withheld.

### The standalone archive

The same engine is published as `rtmpe-automation-kit.tar.gz` with every SDK
release, for use outside a Unity project — on a build server, across several
projects, or on a machine without the Editor:

<https://github.com/rtmpengine/unity-rtmpe-sdk-automation/releases/latest>

Check the download against the SHA-256 digest in the release notes, then
extract it anywhere outside your Unity project:

```bash
sha256sum rtmpe-automation-kit.tar.gz
tar -xzf rtmpe-automation-kit.tar.gz
```

The archive extracts to a `rtmpe-automation-kit` folder laid out exactly like
`Automation~`; wherever this page says `<package>/Automation~`, use that folder
instead. The archive is produced from the same sources by `make automation-kit`.

> **Warning:** Never extract the archive into `Assets/`. Unity compiles every
> `.cs` file under `Assets/`, and the engine's sources depend on compiler
> libraries a Unity project does not reference, so the project fills with
> compile errors.

---

## Prerequisites

- **The .NET 8 SDK, on `PATH`.** The engine targets .NET 8. Check with
  `dotnet --list-sdks`: one line must start with `8.`. `dotnet --version` is not
  a reliable check — it reports the SDK selected for the current folder, so a
  machine with both .NET 8 and .NET 9 installed prints `9.x`.
- **Network access for the first build.** The first build downloads the
  engine's NuGet packages from nuget.org; later builds work offline.
- **GNU make**, only for the `make` commands on this page. Without it, run the
  engine with `dotnet` directly — see [Command reference](#command-reference).

The Conversion Wizard and the `make` commands build the engine themselves before
every run, so they need no separate build step. Running the engine's DLL with
`dotnet` directly needs one build first, and a terminal is also the easiest
place to read errors from the first build:

```bash
cd "<package>/Automation~"
dotnet build clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli -c Release --nologo
```

### When Unity cannot find `dotnet`

A Unity Editor started from the macOS Finder or Dock does not inherit your
shell's `PATH`, so `dotnet` can work in a terminal and still be missing for
Unity. When `PATH` has no `dotnet`, the Editor tries the standard install
locations in this order: `DOTNET_ROOT` if you set it, `/usr/local/share/dotnet`,
`/usr/local/bin`, `/opt/homebrew/bin`, `/usr/local/opt/dotnet/bin`,
`/usr/lib/dotnet`, `/usr/share/dotnet` and `~/.dotnet`.

If your .NET SDK is somewhere else, start Unity from a terminal:

```bash
/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity -projectPath "/path/to/YourUnityProject"
```

or make its location visible to apps started from the Finder, until the next
restart:

```bash
launchctl setenv DOTNET_ROOT /path/to/dotnet
```

Setting `DOTNET_ROOT` in `~/.zshrc` does not help: an Editor started from the
Finder does not read that file.

---

## The loop

Repeat these steps until the readiness report has nothing left to convert.

### 1. Open the Conversion Wizard

Open **Window → RTMPE → Conversion Wizard**. The wizard finds the engine in
`Automation~` by itself and opens on its first step, *Pick a script*.

If the wizard reports *The conversion host was not found*, the package's
`Automation~` folder is missing: reinstall the package, or point the wizard at
an extracted archive. The **CLI project folder** field appears only in that
state; press **Browse…** and select
`<archive>/clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli` — the project
folder itself, not the archive's root. The wizard saves that folder for this
project in your Editor preferences and, for as long as the folder exists, uses
it ahead of the package's own copy without showing the field again.

### 2. Score your project

Run the scorer from the engine folder, and point both `SOURCE` and `OUT` at your
project root — the folder that holds `Assets/`:

```bash
cd "<package>/Automation~"
make readiness SOURCE="/path/to/YourUnityProject" OUT="/path/to/YourUnityProject"
```

Then open **Window → RTMPE → Network Readiness** and press **Refresh**. While no
report exists, the window shows this command with your project's paths filled
in.

Always pass both. Without `SOURCE` the command scores the SDK's own sample
scripts, which the engine folder does not include, and stops with an error;
without `OUT` it writes the report inside the engine folder, where neither
window looks.

| `make` variable | Command-line option | Effect |
|---|---|---|
| `SOURCE=<dir>` | `--source <dir>` | scores every `.cs` file under the folder, skipping `obj`, `bin`, `Library`, `Temp`, `Logs`, `.git`, `Packages`, `PackageCache`, `Editor` and `Tests` folders; the option can be repeated |
| `OUT=<dir>` | `--out <dir>` | where the report is written; use your project root |
| `DEFINE="A B"` | `--define <SYMBOL>` | preprocessor symbols to read the code with; the option can be repeated |
| `ASSETS=<dir>` | `--assets <dir>` | also reads every `.prefab` under the folder and adds a to-do item for each prefab that has a `NetworkTransform` but no `NetworkTransformInterpolator` — such an object looks frozen to every other client; the option can be repeated |
| `ANSWERS=<file>` | `--answers <file>` | the authority answers to apply; by default `network-authority-answers.json` in the output folder |
| `RUNTIME=<file>` | `--runtime <file>` | the runtime record to read; by default `network-runtime-checks.json` in the output folder |
| — | `--fail-on-findings` | exits with code `8` when the report has any to-do item, so a build job can fail on unfinished work |

Code inside an inactive `#if` branch is not read, so it counts as absent and the
project looks cleaner than it is. The scorer prints a warning for each file
with inactive regions; pass the symbols your project builds with:

```bash
make readiness SOURCE="/path/to/YourUnityProject" OUT="/path/to/YourUnityProject" DEFINE="UNITY_EDITOR MY_FEATURE"
```

The run writes two files to `OUT`:

- `network-readiness.json` — the report both windows read;
- `network-readiness.md` — the same report as Markdown.

It also reads two files from the same folder, which you and the Editor write:
`network-authority-answers.json` ([step 4](#4-answer-the-authority-questions))
and `network-runtime-checks.json` ([Verifying with a run](#verifying-with-a-run)).
They are kept apart from the report because every run rewrites the report.

### 3. Read the score

The window and the Markdown report open with a headline such as
`Static readiness: 90% — 2 types scored`, and the window shows the runtime
result beside it, such as `Runtime verification: 0 of 5 established`.

- The **scored types** are the concrete classes that derive from
  `NetworkBehaviour`. Each earns up to 100 points across six dimensions, and
  the project score is their average.
- A project that has not been converted yet scores `0%` with no types scored.
  That is expected: the report's **Authority** section still lists every
  `MonoBehaviour`, and that list is what the Conversion Wizard offers you.
- The score is decided from source code. It rises when a well-formed type joins
  the scored set, not only when your game gets closer to working, and a project
  can reach 100% without ever connecting. The runtime checks
  ([Verifying with a run](#verifying-with-a-run)) are the other half.

| Dimension | Weight | Cleared when |
|---|---:|---|
| Structural | 25 | the type derives from `RTMPE.Core.NetworkBehaviour` — every scored type does |
| State | 20 | it constructs at least one `NetworkVariable` (its own or inherited), and neither `RTMPE1010` nor `RTMPE1012` is reported on it or its base classes |
| Ownership | 20 | every frame loop it runs (`Update`, `FixedUpdate`, `LateUpdate`, its own or inherited) that writes its own state opens with `if (!IsOwner) return;` — see below |
| RPC | 15 | none of `RTMPE1001`–`RTMPE1006` is reported on it or its base classes |
| Lifecycle | 10 | none of `RTMPE1011`, `RTMPE1020` or `RTMPE1021` is reported on it or its base classes |
| Authority | 10 | its authority posture can be read from its code, or you have answered its question ([step 4](#4-answer-the-authority-questions)) |

The Ownership dimension clears in one of four ways, and the report quotes the
reason:

- `no frame loop to guard`
- `every frame loop that drives its own state opens with the IsOwner guard`
- `no frame loop writes state of its own to guard`
- `cleared on a partial reading — a write in a frame loop rests on a type that did not resolve`

The last one means a frame loop writes through a type the scorer could not
resolve — it reads your scripts without Unity's own libraries, so a UI `Text` or
an `Animator` the class holds is unresolved. The weight is granted, and the
to-do list names the type so you can check that loop yourself.

The **To-do** list names every uncleared dimension per type — the conversion
work that remains. It also names any type whose base class could not be
resolved, which is left out of the score.

### 4. Answer the authority questions

The scorer never guesses who decides something. When a `NetworkBehaviour`'s
authority cannot be read from its code — for example, it has no
`NetworkVariable`, owner guard, RPC or lifecycle override — the report asks you
instead:

> **`RoundClock` — Who decides when the round starts and ends?**
>
> - `owner` — the player who owns this object decides; everybody else is shown the result.
> - `host` — one player — whoever is the room's host — decides for everybody.
> - `server` — the server decides; a client asks and waits. This answer needs code outside Unity: the project's server function.
> - `each-client` — every client works it out for itself; nothing has to agree.

Answer with the buttons under *Authority — questions for you* in the Network
Readiness window, or by hand in `network-authority-answers.json` next to the
report:

```json
{ "answers": [ { "name": "Game.RoundClock", "decidedBy": "server", "note": "" } ] }
```

`name` is the type's full name, `decidedBy` one of the four answers, and `note`
your reason. Then score again: the next run reads the file and the type's
Authority dimension clears. In the window, each answer is shown with what it
means for your code, and **Withdraw this answer** removes one. For `server`, the
work belongs in the project's server function — an HTTPS endpoint you run and
register in the portal under **Project → Server functions**.

An answer settles the Authority dimension and nothing else: State, Ownership,
RPC and Lifecycle still measure your code. The report goes on showing the
posture the rubric derived (`Undetermined`) with your answer beside it, and an
answer the scan could not apply — to a type that has been renamed, for example
— appears in the to-do list with the reason.

### 5. Convert

In the Conversion Wizard:

1. Press **Scan**. The wizard lists the types in the report's Authority section
   with their advisory role. A type whose script it cannot find under
   `Assets/`, or finds more than once, is listed with the reason and cannot be
   selected.
2. Select a type and choose the conversion.
3. Name the member or method, then press **Preview diff**.
4. Read the diff, press **Apply this exact diff…** and confirm. Unity then
   recompiles the changed scripts.

**Revert** restores the files exactly as they were before the last apply. It
stays available until you close the window or apply another conversion; for
anything older, use version control.

Each conversion runs one engine command, which you can also run yourself from
`<package>/Automation~`:

| Wizard conversion | Rule | Command |
|---|---|---|
| Rebase to NetworkBehaviour | `RTMPE2001` | `make fix FILE=… TYPE=… KIND=rebase` |
| Insert owner guard in Update | `RTMPE2003` | `make fix FILE=… TYPE=… KIND=owner-guard` |
| Chain base.OnDestroy() | `RTMPE1020` | `make fix FILE=… TYPE=… KIND=base-ondestroy` |
| Generate NetworkVariable | `RTMPE2002` | `make convert FILE=… TYPE=… MEMBER=…` |
| Generate Enhanced RPC | `RTMPE2004` | `make gen-rpc FILE=… TYPE=… METHOD=…` |

`FILE` is the script's path and `TYPE` the type's full name, namespace included.
Each command prints the diff and changes nothing; add `APPLY=1` to write the
files. `APPLY` and `REPLICA_APPLY` are read from the `make` command line only:
if either is set in your shell's environment, the command stops with an error
instead of writing. Running a conversion that is already applied changes
nothing.

**The owner guard.** The guard goes into exactly one frame loop. When the type
declares more than one of `Update`, `FixedUpdate` and `LateUpdate`, name the one
`RTMPE2003` reported — `make fix FILE=… TYPE=… KIND=owner-guard METHOD=FixedUpdate`.
The wizard's owner-guard conversion does not take a loop name, so use the
command for such a type.

**Fields.** `MEMBER` takes a list. A plain name converts the field in place;
`field:companion` keeps a serialised field as it is and adds a replicated
companion seeded from it:

```bash
make convert FILE=Player.cs TYPE=Game.Player MEMBER="_score _maxHealth:_maxHealthNet" APPLY=1
```

In place, the field's type becomes the matching `NetworkVariable` wrapper, the
variable is constructed in `OnNetworkSpawn`, and every use in the class is
rewritten to `.Value` (a list keeps its uses as they are); only private,
non-serialised fields can be converted this way. The companion form leaves the
original field and every use of it unchanged — move the reads and writes that
should replicate onto the companion's `Value` yourself. When the in-place form
is refused (the field is serialised, not private, or used before spawn, for
example), the message says why and suggests the companion form. All the members
named in one command are converted in one step: a failure leaves the type
unconverted rather than half converted.

**Several types in one step.** `convert-batch` converts several types as one
change. Its options are positional: each `--type` belongs to the `--file` before
it, and each `--member` to the `--type` before it.

```bash
make convert-batch APPLY=1 ARGS="--file Player.cs --type Game.Player --member _score --file Turret.cs --type Game.Turret --member _heat"
```

`ARGS` reaches the shell unquoted, so a path containing a space needs its own
quotes inside `ARGS`. In the wizard, the NetworkVariable conversion lists the
other types under *Convert more types in the same batch*; tick them and name a
member for each. A batch must come from one folder.

**Methods.** `METHOD` takes a list of `Name:Target` pairs, and the target
decides where the body runs:

| Target | Who runs the method |
|---|---|
| `All` | every client in the room, the sender included, after a round trip through the server |
| `Others` | every client except the sender |
| `Server` | no client: it executes only in the project's server function, an HTTPS endpoint you run and register in the portal (**Project → Server functions**) |
| `AllBuffered` | like `All`, and replayed to clients that join later; the most expensive target, used only when you name it |

Without a `:Target`, a method that changes the object's state is refused until
you choose one, and the wizard's **Audience** list starts on *— choose one —*
for the same reason; a method that changes nothing defaults to `Server`. With
no server function registered, a `Server` call reaches no one, and because the
conversion replaces the local body with a send, the work then happens nowhere.
Choose `Server` only when the server function exists or is planned; the wizard
says so beside the list, and the command says so on the diff.

A body that opens with `if (!IsOwner) return;` runs on no client under
`Others`, which excludes the sender and leaves only non-owners, and only on the
owner under `All`. The command states this on the diff too. A method that
changes state with `All`, `Others` or `AllBuffered` and no owner guard is
refused, because every receiving client would apply the change — unless it is
meant to, as described next.

**Methods that apply state on every client.** Some methods are meant to run on
every receiver — applying a hit, an explosion, a layout the owner decided — and
so have no owner guard. Tick **Applies on receivers** in the wizard, or pass
`REPLICA_APPLY=1` to `make gen-rpc`, and choose `Others`, `All` or
`AllBuffered`. The engine then checks the calls instead of the body: every call
to the method in its file must sit under an authority guard — `if (IsOwner)`,
`if (manager.IsMasterClient)` or an early `if (!IsOwner) return;` — and the
conversion is refused naming the line of the first call that does not. The
calls must be in the method's own class. A change inside the body that runs
only under the owner or the host is refused too, because under this
designation the whole body is what every receiver applies.

The server relays an Enhanced RPC on an owner-only object — which is what
`Spawn` creates unless told otherwise — from the object's owner alone, and drops
any other sender's call without a word to that client. So a call guarded on
`IsMasterClient` reaches the room only while the host owns the object. A world
object (`RtmpeWorldAuthority`) is spawned shared by default, and so is anything
spawned with `sharedAuthority: true`: there any member's call is relayed, and
the guard in your code is the only gate. Under `Others` the sender is left out
and its direct call is gone, so apply the state locally beside the send, or
choose `All`.

**A type that inherits from another of your types.** Convert each type on its
own. A variable's identity comes from the object's concrete type and the
member's name, so a base class and a derived class must not both use one member
name for a variable: on the derived type the two would be one identity. When
the converted file also declares the base class, the conversion detects the
pair, names both members and exits with code `2`; rename one and run it again.

**The identity record.** Converting a field or a method writes its identity to
`<Type>.rtmpe-ids.json` next to the script, named after the type's full name
(for example `Game.Player.rtmpe-ids.json`). Commit it, so a review shows when an
identity changes. Deleting it loses nothing, because the next conversion
derives the same identities from the source. Renaming the type or the member
does change its identity, and every client must then be rebuilt together. When
a folder holds a record for a type no script there declares — which is what a
rename leaves behind — the next conversion in that folder stops and names it;
delete the old record once you have accounted for the change.

### 6. Re-score, and repeat

Run the same `make readiness` command again and press **Refresh** in the
Network Readiness window. The score moves, the to-do list shrinks, and what
remains is the next thing to convert. Stop when the to-do list is empty.

---

## Verifying with a run

The static score says nothing about whether the game works. The Network
Readiness window keeps a second result beside it — **Runtime verification**,
five checks that only a run can answer:

| Check | A pass means |
|---|---|
| The client reaches the gateway | a handshake completed against a deployed gateway: the API key was accepted and a session exists |
| Two clients are in one room | both clients joined the same room and each roster names the other, with exactly one host |
| Each player appears for the other | each client's spawn reached the other, under its own owner |
| State crosses between them | a replicated value written by its owner arrived at the other client unchanged, and a write to somebody else's object did not |
| A dropped client comes back | a session that ended was resumed with its reconnect token, and the room was still there to come back to |

Every check starts as *not tested*, and nothing the scan reads can change that.
Outcomes are recorded in `network-runtime-checks.json` next to the report, in
two ways:

- **By you**, with the **I saw this work**, **It failed** and **Not tested**
  buttons on each row of the window. The text field above the buttons is saved
  with the outcome as your note.
- **By the Editor**, which watches every play session (below).

Each outcome records who observed it, when, and against which SDK version; the
next scan refuses a record that lacks any of the three. The window shows a new
outcome at once, marked *recorded since the last scan*; the report's own count
changes on the next scan. The two results never move each other.

### What the Editor records during Play

Press Play with a `NetworkManager` in the scene; when play mode ends, the
Editor records what this client observed, as `editor-observer`:

| Check | Recorded as passed when this client observes… |
|---|---|
| The client reaches the gateway | the connection reaching `Connected` or `InRoom` |
| Two clients are in one room | its roster naming itself and another player, with exactly one host |
| Each player appears for the other | a live object owned by this client and one owned by another player |
| State crosses between them | a `NetworkVariable` value written by another player's copy arriving at this client's copy |
| A dropped client comes back | the session resuming on its reconnect token and returning first to the room it held when it dropped |

- **Passes only.** The Editor never records a failure and never takes a pass
  back; a check the session did not reach is left as it was. Use **It failed**
  to record a failure.
- **Half of each two-client check.** One client sees its own roster, its own
  objects and the values that reached it. Each outcome's detail says which half
  was seen, and the part of *State crosses between them* that only the other
  client could see — a foreign write *not* crossing — is never recorded from
  here. A session with one player records the connection and none of the
  two-client checks.
- **Your outcomes stand.** An outcome you recorded with the buttons is never
  replaced.
- **Your own earlier seats are not other players.** When a session drops and a
  new `Connect` starts over, the earlier seat can stay on the room's roster for
  a while under its old player id. The Editor counts every id it has been
  seated under, in this play session and earlier ones, as this client; it
  forgets them when the Editor restarts.
- **Nothing from a test transport or a shaped link.** The Editor records nothing
  when the session ran on a transport installed with
  `NetworkManager.SetTransportFactory`, or under the **Link Simulator** in
  **Window → RTMPE → Network Debugger** — neither is the network your players
  will have. The Console says so; turn the simulator off and play again to
  record.
- **Nowhere to record.** With no report at the project root, nothing is
  recorded and the Console shows the scan command, once per Editor session. An
  Editor playing the second client of a two-player setup (a Multiplayer Play
  Mode virtual player or a project clone) should leave recording to the main
  Editor.
- **Recompiling during play.** A script recompile while playing discards what
  the Editor had observed so far; it records what the rest of the session
  establishes.

---

## Command reference

Run every command from `<package>/Automation~`, or from the root of an extracted
archive. Paths can be absolute or relative to that folder.

| `make` command | Without `make` |
|---|---|
| `make readiness SOURCE=… OUT=…` | `dotnet <dll> readiness --source … --out …` |
| `make convert FILE=… TYPE=… MEMBER=…` | `dotnet <dll> convert --file … --type … --member … [--apply]` |
| `make convert-batch ARGS="…"` | `dotnet <dll> convert-batch --file … --type … --member … [--apply]` |
| `make fix FILE=… TYPE=… KIND=…` | `dotnet <dll> fix --file … --type … --kind … [--method …] [--apply]` |
| `make gen-rpc FILE=… TYPE=… METHOD=…` | `dotnet <dll> gen-rpc --file … --type … --method Name:Target [--replica-apply] [--apply]` |

Here `<dll>` is
`clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli/bin/Release/net8.0/RTMPE.SDK.ConversionCli.dll`,
which exists once you have built the engine ([Prerequisites](#prerequisites)).
Each `--member`, `--method` and `--source` option can be repeated. For example:

```bash
dotnet clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli/bin/Release/net8.0/RTMPE.SDK.ConversionCli.dll readiness --source "/path/to/YourUnityProject" --out "/path/to/YourUnityProject" --fail-on-findings
```

The engine folder's `Makefile` also lists `advise`. It is not part of the
distributed engine: it prints a notice and exits with an error. Nothing on this
page needs it.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | success — a preview, an applied change, or nothing to do |
| `1` | usage: a missing or malformed option, a file or folder you named that does not exist, or (for `readiness`) an answers or runtime file the scorer will not accept |
| `2` | an input that exists but cannot be read, or a problem with the request itself: a field type the conversion does not support, an identity collision, an RPC method-id clash, or an RPC target you must choose; `readiness` also exits `2` when it finds no `.cs` file to score |
| `3` | refused: the conversion cannot be carried out on this input, and the message says why |
| `4` | stopped when writing: nothing was written unless the message names the file that was |
| `8` | `readiness --fail-on-findings` only: the report has at least one to-do item |

---

## What the conversion does not do

- **It does not edit scenes or prefabs.** Conversions change C# source only.
  After converting a type used in a scene or prefab, check the component's
  serialised fields and references by hand.
- **By-name calls keep running locally.** Converting a method to an RPC rewrites
  the C# calls to it in its class. A UnityEvent wired in the Inspector — a
  Button's `OnClick`, for example — an animation event or a `SendMessage` names
  the method as text and calls it directly at run time. After the conversion it
  still runs the method locally, on the clicking client only; nothing fails to
  compile and nothing shows in the diff. The wizard's
  **Scan scenes & prefabs for by-name references** button reads the `.unity`,
  `.prefab`, `.asset`, `.anim`, `.controller` and `.playable` files under
  `Assets/` and lists each place that names the converted type or member, with
  its file, line and YAML key. It also says how many assets it read, so an empty
  result can be told apart from a scan that read nothing.
- **It reads one file at a time.** A conversion rewrites the script it converts
  and nothing else: that is why in-place field conversion is limited to private
  fields, and why a call to a converted method from another script still calls
  it directly.
- **It does not choose for you.** An RPC's target and an authority answer are
  your decisions. The tools state what each choice costs, but never pick one.

---

## When something does not work

**"Could not run `dotnet` — is the .NET SDK on PATH?" while `dotnet` works in a
terminal.** See [When Unity cannot find `dotnet`](#when-unity-cannot-find-dotnet).

**"The conversion engine timed out (180 s)."** The first build restores NuGet
packages and can be slow. Run the build once from a terminal
([Prerequisites](#prerequisites)), then use the wizard again.

**"The conversion engine failed to build".** The wizard shows the build output;
run the same build in a terminal to read it in full. The first build needs
network access to nuget.org.

**"A compatible .NET SDK was not found" ("Requested SDK version").** You ran
`dotnet` from inside the engine's `clients/unity-sdk/Tooling` folder, whose
`global.json` asks for a .NET 8 SDK. Install one, or run the commands from the
engine folder's root, as this page does.

**"The conversion host was not found."** The package's `Automation~` folder is
missing from this install. Reinstall the package, or select an extracted
archive's engine folder ([step 1](#1-open-the-conversion-wizard)). Until the
engine can run, convert by hand: the
[rule reference](diagnostics.md) gives the expected shape for each rule and
every condition under which a quick fix is withheld.

**"no script by that name under Assets/".** The report describes types this
project does not contain, because it was produced for another folder. Score
again with `SOURCE` and `OUT` set to this project's root.

**"ambiguous: … scripts named …".** More than one script under `Assets/` has the
type's file name, so the wizard will not guess. Convert that type with a `make`
command, which takes the file's path.

**"A batch converts scripts from one folder."** Convert the scripts in the
other folder as a second batch.

**"changed on disk after the preview — preview again."** A file in the approved
diff changed while you were reading it. Preview again, and approve the new diff.

**The report is more than 24 hours old.** The window marks it as stale and shows
the command that regenerates it.

---

## Related pages

- [Analyzer Rule Reference](diagnostics.md) — every rule, its severity, and
  whether it has a quick fix
- [Getting Started](getting-started.md) — installing and configuring the SDK
- [Troubleshooting](troubleshooting.md) — runtime and connection problems
- [API Reference — Remote Procedure Calls](api/index.md#remote-procedure-calls)
  — RPC targets and server functions

---

*RTMPE SDK 1.0.7 — [Rule Reference](diagnostics.md) · [Getting Started](getting-started.md)*
