# Automation — scoring a project and converting it

> SDK Version: `com.rtmpe.sdk 1.0.5`

This page is the whole automated route from a single-player Unity project to a
networked one: what the tooling is, what you need before it will run, the
commands, and the loop you repeat until the score stops moving.

> **Where this sits.** It comes after the project is configured, not before:
> [dashboard](getting-started.md#3-before-unity--create-your-project-in-the-dashboard)
> → [Setup Wizard](getting-started.md#4-the-supported-path--the-setup-wizard),
> then this page — score, read the diagnostics, convert, re-score — and then the
> runtime work in [Getting Started](getting-started.md) from Step 4 on. Scoring
> reads source and needs no gateway, so you can run it earlier; converting a
> project you cannot yet connect with only moves the first failure later.

Two Unity windows front it — **Window → RTMPE → Network Readiness** and
**Window → RTMPE → Conversion Wizard** — and both are viewers over the same
headless engine. Neither window scores anything or rewrites anything by itself.
That is a deliberate design and it is the reason for every prerequisite below.

> **If you opened a window and it said something was missing, you are in the
> right place.** Two absences are reported here. The Conversion Wizard reports
> both: the conversion host has not been built on this machine, or the readiness
> artifact has not been generated for this project. The Readiness window reports
> only the second, because it reads the artifact and never launches the host.
> [Prerequisites](#prerequisites) covers the first,
> [Score your project](#2-score-your-project) the second.

---

## What the automation actually is

| Piece | What it does | Where it lives |
|---|---|---|
| **Analyzers** | Flag what stands between a type and a networked one (`RTMPE1000`–`RTMPE2005`, `RTMPE9001`) | This package — `Analyzers/`, loaded by Unity |
| **Quick fixes** | Apply four of those from the IDE lightbulb — [mapping below](#ide-quick-fixes) | This package |
| **Readiness scorer** | Scores every type and writes `network-readiness.json` | This package — `Automation~/` |
| **Conversion host** | Rewrites source, allocates wire identities | This package — `Automation~/` |
| **Readiness window** | Renders the artifact | This package |
| **Conversion Wizard** | Drives the conversion host behind a diff you approve | This package |

⛔ **The scorer and the conversion host are .NET projects, and Unity compiles
neither.** They ship all the same, under `Automation~/`: a folder whose trailing
`~` Unity's asset pipeline skips outright, so Roslyn-dependent sources travel
inside a UPM package without being imported into the project that installs it.
The Conversion Wizard shells out to what it finds there; the Readiness window
launches nothing and reads the artifact a run of the scorer leaves behind.

So a project that installed the package alone has everything — the analyzers,
the quick fixes, both windows and the engine behind them. What it still needs is
the **.NET 8 SDK** and one build command, which is what
[Prerequisites](#prerequisites) covers. Nothing has to be obtained from anywhere
else.

<a id="ide-quick-fixes"></a>
### IDE quick fixes

Four rules carry a lightbulb. Nothing outside this table has one, and on every
other rule *Suppress / Configure* is the complete and correct menu — not a
broken toolchain.

| Diagnostic | Lightbulb entry | What it edits |
|---|---|---|
| `RTMPE1020` | *Call base.OnDestroy()* — or *Call base.OnDestroy() and override the hook* on a hiding declaration | appends `base.OnDestroy();`, and corrects `private void` to `protected override void` where that is legal |
| `RTMPE2001` | *Rebase to NetworkBehaviour* | changes the base type and imports `RTMPE.Core` |
| `RTMPE2002` | *Convert to NetworkVariable* — or *Add companion NetworkVariable* on a serialized field | retypes a private field, or seeds a companion beside an Inspector field |
| `RTMPE2003` | *Add the IsOwner guard* | inserts `if (!IsOwner) return;` as the flagged frame loop's first statement |

⚠️ **Where you will see them.** Only `RTMPE1020` is `Error`; the other three are
`Info`, and **Unity's Console never renders `Info`** — those four exist only in
your IDE. An empty Console is not evidence the analyzers failed to load. The
full surface table, the supported IDEs, the post-fix checklist and the exact
conditions under which each fix is withheld are in
[the rule reference](diagnostics.md#where-diagnostics-appear).

---

## Prerequisites

### 1. The .NET 8 SDK, on `PATH`

The conversion host targets `net8.0`. Any 8.x SDK will do — the exact pin inside
the repository's `Tooling/` folder governs byte-compared analyzer builds only and
does not bind your machine.

```bash
dotnet --list-sdks    # one line must start with 8.
```

⚠️ Check with `--list-sdks`, not `--version`. `dotnet --version` reports the SDK
*selected here*, so a machine with both 8 and 9 installed prints `9.x` and looks
wrong while being entirely correct. What the kit needs is an 8.x entry in the
list — and a .NET 8 runtime, which its SDK installs.

### 2. The conversion host

The Conversion Wizard and the Network Readiness window are viewers over a
headless .NET engine. Unity does not compile that engine — it targets `net8.0`
and uses Roslyn — so it ships inside the package in a folder Unity ignores:

```
<package>/Automation~/
```

**You already have it.** Nothing to download, no licence key and no request to
us — the engine is governed by the same `LICENSE.md` as the rest of the package,
which is service-linked and asks for an account in good standing to *use*, never
to obtain. The trailing `~` is what makes this possible: Unity's asset
pipeline skips such a folder outright, so the engine's sources are never
imported into your project and never compiled by the Editor that carries them.

Where `<package>` is depends on how you installed the SDK, and the wizard
resolves it for you — but if you want to look, it is the folder holding
`package.json`, under `Library/PackageCache/` for a registry or git install and
under `Packages/` for a local one.

Inside it:

```
Automation~/
  README.md                     what to run, and what to send back
  Makefile                      the verbs below
  Directory.Build.props
  nuget.config
  clients/unity-sdk/Tooling/    the engine — 6 projects, 68 C# files
  scripts/                      the Roslyn floor probe
```

`Tooling/global.json` names the SDK floor. In the copy shipped to you it is
`rollForward: latestFeature`, so **any 8.x SDK at or above 8.0.100 satisfies
it** — you are never asked to install one exact build. (Inside an RTMPE checkout the
same file pins one exact patch, because the repository compares its analyzer
assemblies byte for byte against a fresh build; that pin binds our CI, not your
machine.)

The engine carries no binaries, no server code and no credentials — it is the
client-side conversion toolchain and nothing else.

Build it once from a terminal, which is where restore errors are readable:

```bash
cd <package>/Automation~/clients/unity-sdk
dotnet build Tooling/RTMPE.SDK.ConversionCli -c Release --nologo
```

#### The same engine, as a standalone archive

You need this only if you want the toolchain **outside** a Unity project — a
build server, a batch conversion across several projects, or a machine with no
Editor on it. Every SDK release publishes it:

<https://github.com/rtmpengine/unity-rtmpe-sdk-automation/releases/latest>

Verify it against the digest in that release's notes:

```bash
sha256sum rtmpe-automation-kit.tar.gz
```

Extract it anywhere outside a Unity project, then build it exactly as above with
`<kit>` in place of `<package>/Automation~`.

⚠️ **Never extract it into `Assets/`.** Unity compiles every `.cs` file under
`Assets/`, and the archive carries 68 C# files that depend on Roslyn — placing it
there fills the project with compile errors that read as an SDK fault. The copy
shipped in `Automation~/` has no such hazard, which is why it is the default.

⚙️ **If you hold an RTMPE repository checkout**, you already have the engine at
`clients/unity-sdk/Tooling/`, and the wizard prefers it over the shipped copy so
that an edit to a transform is what the next run executes. `make automation-kit`
is how the published archive is produced.

The first build downloads NuGet packages, so it needs network access. Later runs
do not.

🔑 Before it is published, each kit is built **and executed** in a directory
holding nothing else of the repository, and made to score a project there. What
you download is known to work standalone, not merely known to have compiled on
ours.


---

## The loop

### 1. Open the wizard — there is nothing to point it at

**Window → RTMPE → Conversion Wizard**. The wizard resolves the copy shipped
under `Automation~/` on its own, and inside an RTMPE checkout it resolves the
live source there instead, so in both cases the window opens on the scan step
with no configuration.

⚠️ A **Browse…** field appears only when nothing resolved, and it is then the
whole of what that screen offers. Select the **CLI project folder** —

```
<kit-or-checkout>/clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli
```

— not the repository root. The choice is remembered per user, not per project,
and it is consulted ahead of everything else: a path left over from an earlier
session keeps the shipped copy from ever being reached.

### 2. Score your project

Both windows read one file: `network-readiness.json`, in your **project root** —
the folder holding `Assets/`. Write it there:

```bash
cd <kit-or-checkout>
make readiness SOURCE="/path/to/YourUnityProject" OUT="/path/to/YourUnityProject"
```

Or, without `make`, against the assembly you built above:

```bash
dotnet clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli/bin/Release/net8.0/RTMPE.SDK.ConversionCli.dll \
  readiness --source "/path/to/YourUnityProject" --out "/path/to/YourUnityProject"
```

🔑 **`SOURCE` and `OUT` are both load-bearing, and a bare `make readiness` means
something different in each copy.** In the engine shipped under `Automation~/` it
fails outright — it looks for five scripts from the SDK's own samples, which
travel with the repository and not with the kit, and exits naming the first one.
In a checkout it succeeds, and then describes types you do not have, in a
directory neither window reads.

Now open **Window → RTMPE → Network Readiness** and press **Refresh**.

#### What is scanned, and what is skipped

`--source` walks every `.cs` beneath the directory, pruning `obj`, `bin`,
`Library`, `Temp`, `Logs`, `.git`, `Packages`, `PackageCache`, and any `Editor`
or `Tests` segment. Pointing it at the project root and at `Assets` therefore
give the same answer for a normal project.

Code inside an inactive `#if` is not parsed and so is scored as **absent**, which
reads as a cleaner project. Pass the symbols your project builds with, and the
run warns you when it found regions it left out:

```bash
make readiness SOURCE="…" OUT="…" DEFINE="UNITY_EDITOR MY_FEATURE"
```

### 3. Read the score

Both the window and the generated Markdown head the number **Static readiness**,
followed by the number of types it was measured over — `Static readiness: 90% —
2 types scored`.

> **A project that has not been converted yet scores 0%, with an empty "Scored
> types" table and no to-do items. That is correct, not a failure.**

The score covers types that already inherit `NetworkBehaviour`; before any
conversion there are none. The section that is populated is **Authority**, which
classifies every `MonoBehaviour` in the project — and that list is what the
wizard's **Scan** step offers you.

⚠️ Two things the percentage does not say. It is a mean over the **scored** types
only, so it climbs as types join that set rather than as the project converges —
which is why the count is stated beside it, and why neither surface offers it as
a fraction of the components you have: most of those are `MonoBehaviour`s that
should stay that way. And every dimension behind it is decided from **source**, so
a cleared one means the rule holds as written, never that the converted game has
been run. Two clients still have to play it.

Six dimensions, weighted:

| Dimension | Weight | Cleared when |
|---|---:|---|
| Structural | 25 | the type inherits `RTMPE.Core.NetworkBehaviour` |
| State | 20 | it **constructs** at least one `NetworkVariable` — a declared-but-never-constructed member is null and replicates nothing |
| Ownership | 20 | the type runs no frame loop (`Update`, `FixedUpdate` or `LateUpdate`), every frame loop that drives its own state opens with the owner guard, or no frame loop writes state of its own |
| RPC | 15 | no RPC rule fires on it |
| Lifecycle | 10 | no lifecycle rule fires on it |
| Authority | 10 | its authority posture is determinate — read from the code, or answered by you |

The artifact carries the reason verbatim, and these are the only four:
Ownership clears as `no frame loop to guard`, as
`every frame loop that drives its own state opens with the IsOwner guard`,
as `no frame loop writes state of its own to guard`, or as
`cleared on a partial reading — a write in a frame loop rests on a type that did not resolve`.

The fourth is a clear that states its own reservation. A write whose type this
compilation could not resolve answers "reaches nothing" to every question the
rule asks of it, and the weight is still granted — the toolchain resolves the SDK
contract rather than UnityEngine, so a held `Rigidbody` or a UI `Text` is unbound
in an ordinary, correct project. The **To-do** list names every type scored that
way, so the number is a claim you can see the edge of.

#### The one dimension the tool asks you about

The rubric never guesses. A `NetworkBehaviour` that declares nothing — no
`NetworkVariable`, no owner guard, no RPC, no lifecycle hook — is genuinely
undecidable from the code, and no amount of better code fixes that, because what
is missing is a **decision**. So the artifact carries a question instead:

> **`RoundClock` — Who decides when the round starts and ends?**
>
> - `owner` — the player who owns this object decides; everybody else is shown the result.
> - `host` — one player, whoever is the room's host, decides for everybody.
> - `server` — the server decides; a client asks and waits.
> - `each-client` — every client works it out for itself; nothing has to agree.

Answer it in the **Network Readiness** window (the buttons are under *Authority —
questions for you*) or by hand in `network-authority-answers.json`, beside the
artifact:

```json
{ "answers": [ { "name": "Game.RoundClock", "decidedBy": "server", "note": "" } ] }
```

Then re-score. The next scan reads the file, the Authority dimension clears, and
the report states the distribution the answer implies — including, for `server`,
that the handler is Go code in the Room Service and there is none by default.

⛔ **The answer is kept in its own file, never in `network-readiness.json`.** The
artifact is rewritten whole by every run, so an answer stored there would be
destroyed by the very scan meant to read it.

⚠️ **It buys the Authority weight and nothing else.** State, Ownership, RPC and
Lifecycle still measure your code — a project cannot answer its way to 100 % over
code that replicates nothing — and the report keeps saying `Undetermined` for
what the *rubric* derived, with your declaration recorded beside it.

The **To-do** list names the uncleared dimension per type — that is the remaining
conversion work, in order.

#### The number is not how finished your game is

⛔ It is a weighted sum over six dimensions — Structural 25, State 20, Ownership
20, RPC 15, Lifecycle 10, Authority 10 — then a **mean over the types the scorer
accepted**. Two consequences follow, and both surprise people:

- It **climbs when a clean type joins the set**, not only when your game
  converges. Adding one well-formed `NetworkBehaviour` raises the mean.
- Every dimension is decided from **source**. A project can score 100 % having
  never opened a socket, because nothing in the rubric can tell whether the game
  was run.

So the window carries a second result beside it: **Runtime verification**, five
checks that only a run can answer.

| Check | A pass means |
|---|---|
| The client reaches the gateway | a handshake completed against a deployed gateway |
| Two clients are in one room | both joined the same room and each roster names the other |
| Each player appears for the other | each client's spawn reached the other, under its own owner |
| State crosses between them | a replicated value crossed, and a foreign write did not |
| A dropped client comes back | a session resumed on its reconnect token |

Every one starts **not tested**, and nothing the scan reads can change that. An
outcome is recorded in `network-runtime-checks.json` beside the artifact — by
the buttons under *Runtime verification*, or by the load harness with
`-runtime-record` — and it must name **who observed it, when, and the SDK
version**. A record missing any of the three is refused, whole, on the next
scan.

⛔ **In the same file for the same reason as the answers:** the artifact is
rewritten by every run, so an outcome stored there would be destroyed by the
scan meant to read it.

⚠️ **Neither number moves the other**, and the runtime result is a count of five
rather than a percentage — deliberately, so two percentages side by side are not
read as two measurements of one thing.

### 4. Convert

In the wizard: **Scan** → tick a script → choose the conversion → **Preview** →
approve the diff → **Apply**. **Revert** restores byte-for-byte from a snapshot
taken before the write.

⚠️ That snapshot belongs to the open window and to the apply that produced it.
It survives the script recompile the apply triggers, which is what makes Revert
reachable at all; it does not survive closing the window, and a second apply
replaces it. Revert is the way back out of the conversion you just looked at —
for anything older, use source control.

The same conversions headlessly, all of which print a diff and change nothing
until `APPLY=1`:

| Conversion | Command |
|---|---|
| `MonoBehaviour` → `NetworkBehaviour` | `make fix FILE=… TYPE=… KIND=rebase` |
| Owner guard in the frame loops | `make fix FILE=… TYPE=… KIND=owner-guard` |
| `base.OnDestroy()` | `make fix FILE=… TYPE=… KIND=base-ondestroy` |
| Field → `NetworkVariable` | `make convert FILE=… TYPE=… MEMBER=_field` |
| Method → Enhanced RPC | `make gen-rpc FILE=… TYPE=… METHOD=Name:Target` |

`FILE` is a path, `TYPE` is the fully-qualified type name, and every verb is a
no-op the second time — re-running one is byte-identical to not running it.

`METHOD` takes a `Name:Target` pair, and the target decides where the body runs.
Omit the `:Target` and the engine applies its own rule: a method that mutates
instance state is **refused** until you designate one, and the wizard's Audience
popup opens on **— choose one —** for the same reason.
⛔ **`Server` is not a target the SDK can satisfy on its own.** A Server-targeted
Enhanced RPC executes in a backend handler registered against its method id; with
no handler registered the send resolves to `RpcErrorUnknownMethod`, and because
the rewrite replaces the local body with a send, the work stops happening
anywhere. Registering that handler is server-side work in the Room Service, not
something a Unity project can do — so choose `Server` when that handler exists or
is planned, and `Others`/`All` when the mutation is meant to run on peers. The
wizard states this beside the audience popup and the headless verbs state it on
the diff.

⚠️ `Others` and `All` have a cost of their own that the target alone does not
show: a body opening with `if (!IsOwner) return;` runs on no receiver under
`Others` — which excludes the sender, leaving only non-owners — and only on the
sender under `All`. That one depends on the method, so the engine states it on
the diff, where the body is in hand.

`MEMBER` and `METHOD` take a **list**, so one command converts a whole type:

```bash
make convert FILE=Player.cs TYPE=Player MEMBER="_score _name:_displayName" APPLY=1
```

That is not shorthand for running the verb twice. The host writes the source and
its identity ledger as one transaction, so a request naming the whole set either
lands whole or leaves the type untouched — split across runs, a failure between
them leaves half a type converted and its ledger already advanced.

Several **types** in one run go to the batch host, whose flags are positional: a
`--type` belongs to the `--file` before it, and each `--member` to the `--type`
before it.

```bash
make convert-batch APPLY=1 ARGS="--file Player.cs --type Player --member _score \
                                 --file Turret.cs --type Turret --member _heat"
```

⚠️ `ARGS` reaches the shell unquoted — that is what lets it carry repeating
flags — so a path containing a space needs quotes of its own **inside** it.

#### A type that inherits from another of your types

Convert each type on its own — there is no chain of issued numbers to allocate
around, because the conversion derives each identity from the type it is
converting and the member's name rather than from anything a base recorded.

⚠️ **One shape is refused rather than converted: a base and a derived class
spending the same member name.** At run time an identity is derived from the
**concrete** type of the object, so on an instance of the derived class those two
members are one identity — the second registration throws out of
`OnNetworkSpawn` and the object is destroyed rather than spawned. The conversion
detects the pair when this file declares the base, reports both members and exits
`2`; rename one of them and re-run.

```bash
make convert FILE=Ship.cs    TYPE=Ship    MEMBER=_hull   APPLY=1
make convert FILE=Frigate.cs TYPE=Frigate MEMBER=_armour APPLY=1
```

⚠️ **A list is separated by whitespace, and no quoting changes that** — so an
`INCLUDE_SOURCE` path containing a space cannot travel this
way. `MEMBER` and `METHOD` are identifiers and have nothing to escape. For a
spaced path, call the host directly rather than through `make`, where your
quotes reach the shell.

### 5. Re-score, and repeat

```bash
make readiness SOURCE="…" OUT="…"
```

**Refresh** the window. The score moves, the to-do list shrinks, and what remains
is the next thing to convert. Stop when the list is empty.

### Not here: the advisory pass over the score

The score names the uncleared dimensions; it does not say which to take first, or
what a type's authority model ought to be. An advisory layer answers that, and
it is **not part of the toolchain you have**: it runs in the RTMPE backend,
under credentials of its own, and it ships in no distribution of this SDK —
not in the package, not in the standalone archive.

`make advise` exists in the kit's Makefile only so the verb explains itself
rather than failing on a missing project. It will refuse.

⛔ Nothing on this page requires it. The readiness score, every conversion, the
repair verbs and the batch forms are all local and complete without it — the
loop above is the whole loop.

---

## Two things the conversion does not do

**Prefabs and scenes are invisible to it.** Conversions are source-only: Unity's
scene and prefab YAML references your component by GUID and is not read, let
alone rewritten. After converting a type used in a scene or prefab, re-wire it by
hand.

The failure that costs you is silent, so it is worth stating plainly. Converting
a method to an RPC keeps its name and signature and rewrites the **C# call
sites** to `this.RPC("Fire")`. A UnityEvent wired in the Inspector — a Button's
`OnClick`, an animation event, a `SendMessage` — does not go through a call site:
it names the method as text and invokes it by reflection at runtime. So after the
conversion that button still runs `Fire` **locally, on the clicking client only**.
Nothing fails to compile, nothing appears in the diff, and the one thing the
conversion was for does not happen.

The wizard's **Scan scenes & prefabs for by-name references** button reads every
`.unity`, `.prefab`, `.asset`, `.anim`, `.controller` and `.playable` under
`Assets/` and lists the places that name the type or member you converted, with
the file, the line and the YAML key. It reports how many assets it read and how
many of them bind a method by name at all — because a scan that read nothing and
a scan that found nothing are the same empty list, and only those numbers tell
them apart.

**The identity record is a record, not a decision.** Converting a field to a
`NetworkVariable`, or a method to an RPC, derives a wire identity from the type
being converted and the member's own name and writes it to
`<Type>.rtmpe-ids.json` beside the script. Commit it — it is what makes a review
show an identity moving — but losing it changes nothing: re-running the
conversion reproduces the same numbers, because the source already decides them.

⚠️ What *does* move an identity is **renaming the type or the member**. That is a
wire break, and every peer needs rebuilding together.

---

## When something does not work

**"Could not run `dotnet` — is the .NET SDK on PATH?" while the terminal is
fine.** A GUI application does not inherit your shell's `PATH`, and on macOS the
.NET installer puts `dotnet` in `/usr/local/share/dotnet` with a symlink in
`/usr/local/bin` — neither of which a Finder-launched Unity usually has.

The window looks for the installers' own locations when `PATH` cannot answer:
`DOTNET_ROOT` if you set it, then the macOS package and its symlink, Homebrew on
either architecture, the two Linux package layouts, and `~/.dotnet` from the
`dotnet-install` script. ⚠️ `PATH` is still asked **first**, so a machine that
already resolves `dotnet` keeps launching exactly the one it always did.

If your .NET lives somewhere else, launch the editor from a terminal:

```bash
/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity \
  -projectPath "/path/to/YourUnityProject"
```

⚠️ Setting `DOTNET_ROOT` in `~/.zshrc` does **not** help a Finder-launched
editor: it is read from the same process environment `PATH` is, and a GUI launch
inherits neither. Either use the terminal above, or set it where a GUI launch can
see it — `launchctl setenv DOTNET_ROOT /path/to/dotnet`, which survives until the
machine restarts.

**"The SDK 'x.y.z' was not found" / "requested SDK version 8.0.x".** You are in
an **RTMPE repository checkout**, building from inside `Tooling/`, whose
`global.json` pins one exact SDK for the repository's byte-compared analyzer
builds. That pin binds our CI, not your machine. Build from the directory above
it — `cd <checkout>/clients/unity-sdk` — and pass the project path to `dotnet
build`; `global.json` is resolved by walking up from the working directory, so
from there it is not consulted at all. The wizard launches from the same place
for the same reason.

⛔ The engine shipped in the package and in the standalone archive does **not**
carry that pin, so this error cannot come from either of them. If you are seeing
it outside a checkout, the `global.json` in your copy has been edited.

**"The conversion host was not found."** The copy under `Automation~/` is
missing from this install, or a **Browse…** path recorded in an earlier session
does not point at the `RTMPE.SDK.ConversionCli` folder itself — that path is
consulted ahead of both the checkout and the shipped copy. Without a host,
conversion is a manual edit —
[the rule reference](diagnostics.md) gives the exact shape each rule expects,
per rule, including every condition under which a quick fix is withheld.

**"no script by that name under Assets/".** The artifact describes types this
project does not contain — almost always a bare `make readiness` run inside a
checkout, which scores the SDK's own samples. Re-run it with `SOURCE` and `OUT`.

**"A batch converts scripts from one folder."** The engine labels the files in
its diff without their folders, so one approval covers one directory. Convert the
other folder as a second batch.

**"changed on disk after the preview — preview again."** Something wrote to a
file the approved diff names while you were reading it. The wizard refuses rather
than applying an edit you did not see; preview again.

**An artifact more than 24 hours old** is flagged as stale rather than silently
rendered. Re-run the command in the banner.

**Exit codes**, when driving the host from a script: `0` success or no-op, `1`
usage — including a path you named that is not there, `2` an environment fault
such as a file that exists and cannot be read, `3` a refusal (the request cannot
be carried out on this input), `4` a safety stop at apply time, with nothing
written unless the message names the file it wrote.

---

## Related pages

- [Analyzer rule reference](diagnostics.md) — every rule, and the manual edit for each
- [Quick start](getting-started.md) — installing and configuring the SDK itself
- [Troubleshooting](troubleshooting.md) — runtime and connection problems

---

*RTMPE SDK 1.0.5 — [Rule Reference](diagnostics.md) — [Quick Start](getting-started.md)*
