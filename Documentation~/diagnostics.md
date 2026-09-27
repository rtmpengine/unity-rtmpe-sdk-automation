# RTMPE SDK — Analyzer Rule Reference

> SDK Version: `com.rtmpe.sdk 1.0.7`

The SDK ships Roslyn analyzers that check your scripts for networking mistakes
and point out code that can be converted to networked code. This page lists
every rule, what it reports, and whether it has a quick fix.

The analyzers are the DLLs in the package's `Analyzers/` folder. Unity runs them
every time it compiles your scripts, and your IDE runs them as you type. The
link on a diagnostic's rule id in your IDE opens that rule's entry on this page.

Most rules have no quick fix, because the right remedy is a design decision
rather than a mechanical edit. On those rules your IDE offers only
*Suppress / Configure*, and that is the complete menu for the rule.

## Contents

- [Rule index](#rule-index)
- [Where each diagnostic appears](#where-each-diagnostic-appears)
- [Applying a quick fix](#applying-a-quick-fix)
- [The lightbulb is missing — read in this order](#the-lightbulb-is-missing--read-in-this-order)
- [Changing a rule's severity](#changing-a-rules-severity)
- [Rules](#rules)

---

## Rule index

| Rule | Category | Severity | Quick fix |
| --- | --- | --- | --- |
| RTMPE1000 | RTMPE.Usage | Info | none |
| RTMPE1001 | RTMPE.Rpc | Error | none |
| RTMPE1002 | RTMPE.Rpc | Error | none |
| RTMPE1003 | RTMPE.Rpc | Error | none |
| RTMPE1004 | RTMPE.Rpc | Error | none |
| RTMPE1005 | RTMPE.Rpc | Error | none |
| RTMPE1006 | RTMPE.Rpc | Error | none |
| RTMPE1010 | RTMPE.Sync | Error | none |
| RTMPE1011 | RTMPE.Sync | Warning | none |
| RTMPE1012 | RTMPE.Sync | Warning | none |
| RTMPE1013 | RTMPE.Sync | Warning | none |
| RTMPE1020 | RTMPE.Lifecycle | Error | Call base.OnDestroy() |
| RTMPE1021 | RTMPE.Lifecycle | Warning | none |
| RTMPE1022 | RTMPE.Lifecycle | Error | none |
| RTMPE1030 | RTMPE.Usage | Warning | none |
| RTMPE2001 | RTMPE.Conversion | Info | Rebase to NetworkBehaviour |
| RTMPE2002 | RTMPE.Conversion | Info | Convert to NetworkVariable / Add companion NetworkVariable |
| RTMPE2003 | RTMPE.Conversion | Info | Add the IsOwner guard |
| RTMPE2004 | RTMPE.Conversion | Info | none |
| RTMPE2005 | RTMPE.Conversion | Info | none |
| RTMPE9001 | RTMPE.Authority | Info | none |

Four rules carry a fix. They are `RTMPE1020`, `RTMPE2001`, `RTMPE2002` and
`RTMPE2003`. `RTMPE1020` is an `Error`; the other three are `Info`, which most
editors show as a faint hint rather than a squiggle — see
[When a diagnostic does not show in your IDE](#when-a-diagnostic-does-not-show-in-your-ide).

---

<a id="where-diagnostics-appear"></a>
## Where each diagnostic appears

A diagnostic can appear in two places, and they do not show the same set.

| Where | What it shows |
| --- | --- |
| **Unity Console** | `Error` and `Warning` rules only |
| **IDE** (squiggles, Problems, Error List) | every rule, `Info` included; the two whole-solution rules need solution-wide analysis |
| **IDE lightbulb** (`Ctrl+.` / `Cmd+.`) | the quick fixes of the four fixable rules; every other rule offers only *Suppress / Configure* |

Fourteen of the twenty-one rules are `Error` or `Warning` and reach both. The
remaining seven — `RTMPE1000`, `RTMPE2001`–`RTMPE2005` and `RTMPE9001` — are
`Info`, and the Unity Console does not show `Info` diagnostics, whatever your
project settings say.

An empty Console after compiling a script that raises only `Info` rules is
therefore expected. To confirm that the analyzers are loaded, use
[the one-file check](#the-one-file-check), which raises an `Error`.

### Supported IDEs

Your IDE loads the analyzers through the project files Unity generates.

- **JetBrains Rider**, **Visual Studio 2022** and **VS Code with C# Dev Kit**
  show every diagnostic and offer the quick fixes.
- **VS Code with the legacy OmniSharp extension** shows the diagnostics but
  offers no quick fixes: its lightbulb has only *Suppress / Configure*.
  [Troubleshooting → Authoring-tool issues](troubleshooting.md#authoring-tool-issues-roslyn-analyzer--conversion-quick-fixes)
  explains how to switch to C# Dev Kit.

After you import a sample or add scripts outside your IDE, reload the project so
the new files are analysed (VS Code: *Developer: Reload Window*; Rider and
Visual Studio: reopen the solution).

### When a diagnostic does not show in your IDE

- **Info severity** — editors play `Info` down. VS Code draws a faint dotted
  underline instead of a squiggle, Visual Studio lists `Info` diagnostics under
  *Messages* in the Error List, and Rider lists them in *Inspection Results*. In
  VS Code the lightbulb appears only while the caret is inside the reported
  span: put the caret on the reported name and press `Ctrl+.` (`Cmd+.` on
  macOS).
- **Whole-solution rules** — `RTMPE1010` and the companion arm of `RTMPE2002`
  are decided from the whole compilation rather than from one file, so your IDE
  reports them only when it analyses the whole solution; a build reports them
  either way. VS Code analyses open files only by default: set
  `dotnet.backgroundAnalysis.analyzerDiagnosticsScope` to `fullSolution` to see
  them. Rider and Visual Studio have an equivalent solution-wide analysis
  setting.

  `RTMPE1010` is split across that boundary, because only half of it needs the
  whole compilation. A type that repeats a name among **its own** constructions
  is decided from that type alone and is reported in any analysis scope,
  default settings included. A type that reuses a name **its base class already
  holds** is not: that collision is only visible once the whole compilation is
  in hand, and it is the half the setting above controls.

### What the shipped samples raise

The five samples in **Window → Package Manager → RTMPE SDK → Samples** raise
fifteen diagnostics between them, and all fifteen are `Info`. The Unity Console
therefore stays empty for every sample; open the scripts in your IDE to see
them.

| Sample | Script | Rule | What it reports |
| --- | --- | --- | --- |
| Basic Connection | `ConnectionTest` | `RTMPE2001` | a `MonoBehaviour` holding plain state (`_statusLine`, `_rttLine`, `_shouldReconnect`, `_styleReady`) |
| Basic Connection | `ConnectionTest` | `RTMPE9001` | the type's advisory authority classification |
| Player Spawn Flow | `GameManager` | `RTMPE9001` | the type's advisory authority classification |
| Player Spawn Flow | `PlayerController` | `RTMPE1000` | the marker every `NetworkBehaviour` carries |
| Player Spawn Flow | `PlayerController` | `RTMPE2004` | `AddScore` is an owner-guarded state change, a candidate for an Enhanced RPC |
| Player Spawn Flow | `PlayerController` | `RTMPE9001` | the type's advisory authority classification |
| Scene Transitions | `SceneSwitcher` | `RTMPE9001` | the type's advisory authority classification |
| Shared World | `SharedWorldBoard` | `RTMPE9001` | the type's advisory authority classification |
| Shared World | `SharedWorldState` | `RTMPE1000` | the marker every `NetworkBehaviour` carries |
| Shared World | `SharedWorldState` | `RTMPE9001` | the type's advisory authority classification |
| Two Player Room | `TwoPlayerAvatar` | `RTMPE1000` | the marker every `NetworkBehaviour` carries |
| Two Player Room | `TwoPlayerAvatar` | `RTMPE9001` | the type's advisory authority classification |
| Two Player Room | `TwoPlayerCredentials` | `RTMPE9001` | the type's advisory authority classification |
| Two Player Room | `TwoPlayerRoomHud` | `RTMPE9001` | the type's advisory authority classification |
| Two Player Room | `TwoPlayerSpawnPoints` | `RTMPE9001` | the type's advisory authority classification |

None of them needs action: `RTMPE2001` and `RTMPE2004` are conversion
suggestions, and the rest are informational.

### Help links and the Documentation buttons

The link on a rule id opens this page on the web, at that rule's entry. The
**Documentation** button in the Conversion Wizard and in the Network Readiness
window opens [Automation](automation.md) the same way. Both need a network
connection and show the online copy, which follows the latest release. The
pages for the version you installed ship inside the package, in its
`Documentation~` folder.

---

## Applying a quick fix

Put the caret on the diagnostic and open the lightbulb (`Ctrl+.` in VS Code and
Visual Studio, `Alt+Enter` in Rider), then choose the fix.

*Fix All* is offered for `RTMPE1020` and `RTMPE2003`. It is not offered for
`RTMPE2001` and `RTMPE2002`, whose edits in one file overlap (the `using`
directive, the `OnNetworkSpawn` method): apply those one at a time, or convert
several members of a type in one step with the
[Conversion Wizard or `make convert`](automation.md#5-convert).

After applying a fix:

1. Read the edit before you save it — every fix rewrites your source.
2. Save the file and let Unity finish compiling.
3. Check that the Console shows no compile errors.
4. Check that the RTMPE diagnostic is gone from your IDE, and that no new C#
   warning appeared.
5. Re-check scenes and prefabs by hand if a component's fields or base class
   changed: quick fixes edit C# source only.

---

## The lightbulb is missing — read in this order

1. **Is the diagnostic reported at all?** A lightbulb belongs to a diagnostic;
   where none is reported there is nothing to fix. Check the rule's entry for
   what it needs: `RTMPE2001`, for example, needs a `MonoBehaviour` holding
   plain state that is not Inspector data, so a class with no such field
   raises nothing. Remember too that **three of the four** fixable rules are `Info`,
   which the Unity Console never shows — look in your IDE. `RTMPE1020` is the
   exception: it is an `Error` and also appears in the Console.
2. **Is the rule fixable?** Only four rules carry a fix; on every rule marked
   `none` in the [rule index](#rule-index), *Suppress / Configure* is the whole
   menu. None of the five `Warning` rules (`RTMPE1011`, `RTMPE1012`,
   `RTMPE1013`, `RTMPE1021`, `RTMPE1030`) has a fix, so a session spent on
   warnings never shows a lightbulb.
3. **Is the fix's precondition met?** A fix that cannot produce a correct edit
   is not offered. [Preconditions](#preconditions) lists every such case per
   rule.
4. **Is your editor loading the quick-fix assembly?** Only then is it an editor
   problem — see
   [Troubleshooting → Authoring-tool issues](troubleshooting.md#authoring-tool-issues-roslyn-analyzer--conversion-quick-fixes).

### The one-file check

`RTMPE1020` is the quickest way to confirm the whole chain works: it is an
`Error`, so it appears in the Unity Console as well as your IDE, and the class
below meets every condition its fix needs. Paste it into a new script exactly as
written — a body collapsed onto one line is one of the shapes the fix declines.

```csharp
using RTMPE.Core;

public class ProbeBehaviour : NetworkBehaviour
{
    protected override void OnDestroy()
    {
        // RTMPE1020 (Error) — this override never calls base.OnDestroy()
    }
}
```

The lightbulb should offer *Call base.OnDestroy()*. If it does, the analyzers,
the quick-fix assembly and your editor are all working, and any other missing
lightbulb is explained by step 2 or step 3. Delete the script afterwards.

### Preconditions

A fix is offered only when it can produce a correct edit, and there are two
kinds of reason it may be held back.

**The edit would not compile.** Every quick fix compiles the file as the edit
would leave it, against your project's own references and sibling files, and is
not offered when the edit adds a compile error. The check reads one file, so an
edit that breaks another file is outside it. When the file already has compile
errors, the check is skipped and the fix is offered as usual.

The Conversion Wizard and the `make` commands run the same check against a
built-in description of the SDK rather than against your project, and skip it
(printing a `note:`) for a file that names types the description does not know
— which most project files do. So if the lightbulb is missing but the command
applies the edit without a refusal, the edit probably does not compile in your
project: check that it builds before you keep it.

**The shape of the code.** Each fixable rule declines the shapes it cannot
rewrite safely.

`RTMPE1020` — *Call base.OnDestroy()* is not offered when `OnDestroy`:

- has an expression body (`=> …`) or no body;
- is `static` or returns a value;
- is written on a single line;
- has `#if` directives in its body — an appended call could land inside a
  conditional region;
- can leave before its last statement (`return`, `throw`, `goto`) or never
  reach it (`while (true)` or `for (;;)` with no `break`), because an appended
  call would not run on every path. Place the call by hand.

`RTMPE2001` — *Rebase to NetworkBehaviour* is not offered when the class reaches
`MonoBehaviour` through an intermediate base class. The rule reports every class
derived from `MonoBehaviour`, but the fix only replaces a `MonoBehaviour`
written directly in the base list; rebase the intermediate class instead.

`RTMPE2002` — *Convert to NetworkVariable* and *Add companion NetworkVariable*
are not offered when:

- the class is `partial`, or the file does not parse;
- the field is declared inside an `#if` region, or its initialiser is not a
  literal, `default` or a well-known constant such as `Vector3.zero`,
  `Quaternion.identity` or `string.Empty` (moving any other expression into
  `OnNetworkSpawn` would change when it runs);
- a use of the field is ambiguous: shadowed by a local, parameter or other
  variable of the same name, reached through a receiver other than `this`, made
  from outside the declaring class, or inside an inactive `#if` branch;
- a use of the field runs before the variable exists: in `Awake`, `Start`,
  `OnEnable`, `OnValidate`, `Reset`, `OnAfterDeserialize`, `OnBeforeSerialize`,
  a constructor or another field's initialiser, or in any method, property or
  event one of those reaches — including through `Invoke`, `InvokeRepeating`,
  `StartCoroutine`, `SendMessage` and the other Unity calls that name a method;
- the field is used where a variable is needed rather than a value: passed by
  `ref` or `out`, bound to a `ref` local or `ref` return, or with its address
  taken;
- the field is a struct (a `Vector3`, for example) whose members are written or
  whose methods are called in place — `_aim.x = 1f`, `_aim.Normalize()` — which
  after the conversion would change a copy;
- an existing `OnNetworkSpawn` does not override the SDK's hook, has an
  expression body, is written on a single line, or sits inside an `#if` region;
- the companion's name is already used by a member, nested type or delegate of
  the class, or is the class's own name.

`RTMPE2003` — *Add the IsOwner guard* is not offered when the frame loop:

- has an expression body or is written on a single line;
- has `#if` directives in its body;
- is in a class, or has a body, that already declares something named
  `IsOwner`, which the inserted guard would read instead of the ownership flag;
- opens with a test of `IsOwner` — `if (IsOwner) return;`, or an
  `if (IsOwner) { … } else { … }` that splits the body — where a guard placed
  above it would stop the branch written for other clients. A leading exit
  that does not mention ownership, such as `if (_target == null) return;`, is
  fine: the guard goes above it.

`RTMPE2004` has no lightbulb; it is converted with `make gen-rpc` or the
Conversion Wizard, which refuse when:

- the class is `partial`, declares no base class, or the file does not parse;
- the method is overloaded, `static`, `abstract`, not `public`, generic, returns
  a value, or is declared inside an `#if` region;
- a parameter carries `ref`, `out`, `in` or `params`, has a default value, or
  has a type outside the set listed under [`RTMPE1002`](#RTMPE1002);
- a call to the method cannot become a send: the name is shadowed, or also
  declared on a base class in the same file; the call goes through `base` or a
  receiver other than `this`, or comes from outside the class or from a nested
  type; the method is used as a method group or calls itself; or a call uses
  named or `ref`/`out` arguments, leaves out an argument, or has a comment or
  `#if` inside its argument list;
- the class, or a base class in the same file, already has a member named `RPC`,
  which the generated `this.RPC(…)` would call instead of the SDK's send;
- the target does not fit the body — see [`RTMPE2004`](#RTMPE2004).

### Finding out why a fix was withheld

Your IDE cannot say why it offers nothing. The conversion engine can: run the
same conversion from the command line and it prints `refused:` followed by the
condition from the list above.

| Rule | Command |
| --- | --- |
| `RTMPE1020` | `make fix FILE=… TYPE=… KIND=base-ondestroy` |
| `RTMPE2001` | `make fix FILE=… TYPE=… KIND=rebase` |
| `RTMPE2002` | `make convert FILE=… TYPE=… MEMBER=…` |
| `RTMPE2003` | `make fix FILE=… TYPE=… KIND=owner-guard` |
| `RTMPE2004` | `make gen-rpc FILE=… TYPE=… METHOD=…` |

The Conversion Wizard shows the same reason in its error box.

The commands and the wizard are two hosts for the same conversion engine, which
ships inside this package under `Automation~`: run the commands from that
folder, whose `Makefile` defines them, and the wizard finds the folder by itself.
These hosts need the .NET 8 SDK. Both build the engine before they run it, so
the first run also needs network access to restore NuGet packages, and the
`make` commands also need GNU make. [Automation](automation.md) covers the
setup and the full convert → re-score loop.

Until the engine can be built, this page is the substitute for the reason
string: [Preconditions](#preconditions) lists, per rule, every condition under
which a fix is withheld. Compare your code with the entry for the rule you
expected a fix from, and apply the edit by hand.

### The path that needs no IDE

**Window → RTMPE → Conversion Wizard** runs the same transforms as the
lightbulb, outside the IDE, and produces the same edits with one exception: for
`RTMPE1020` it appends `base.OnDestroy()` but leaves a declaration that hides
the base method as written (see [`RTMPE1020`](#RTMPE1020)). It is the supported
route for `RTMPE2002` inside Unity wherever the conversion engine is reachable:
it converts several fields, and several types, in one previewed edit, which the
lightbulb cannot. See [Automation](automation.md).

---

## Changing a rule's severity

Rule severity is set with the standard `.editorconfig` keys, which Roslyn-based
IDEs honour:

```ini
[*.cs]
# Show the conversion suggestions as warnings.
dotnet_diagnostic.RTMPE2001.severity = warning
dotnet_diagnostic.RTMPE2003.severity = warning

# Hide an advisory rule.
dotnet_diagnostic.RTMPE9001.severity = none
```

Severity changes how a rule is reported, never whether it has a quick fix:
promoting a rule to `warning` does not give it a lightbulb.

---

## Rules

Every entry below states the rule's severity, whether it has a quick fix, what
it detects, why it matters and how to fix it.

<a id="RTMPE1000"></a>
### RTMPE1000 — Type inherits NetworkBehaviour

**Severity:** Info · **Category:** RTMPE.Usage

**No quick fix** — the rule reports a fact about the type, not a problem.

**What it detects.** Every class that derives, directly or indirectly, from
`RTMPE.Core.NetworkBehaviour`.

**Why it matters.** It marks the networked components in your code at a glance.

**How to fix.** Nothing to fix. To hide it, set its severity to `none`
([Changing a rule's severity](#changing-a-rules-severity)).

<a id="RTMPE1001"></a>
### RTMPE1001 — RPC method must be public and instance

**Severity:** Error · **Category:** RTMPE.Rpc

**No quick fix** — making a method public or non-static changes its contract
with every caller.

**What it detects.** A method marked `[RtmpeRpc]` that is not `public`, or is
`static` or `abstract`.

**Why it matters.** The SDK discovers and dispatches public instance methods
only, so an incoming call never reaches this one.

**How to fix.** Make it a `public`, non-static, non-abstract method.

<a id="RTMPE1002"></a>
### RTMPE1002 — RPC parameter type is not serializable

**Severity:** Error · **Category:** RTMPE.Rpc

**No quick fix** — the remedy is a different parameter type or payload, and
only you can choose it.

**What it detects.** A parameter of an `[RtmpeRpc]` method whose type the RPC
serialiser cannot carry. The supported types are `int`, `float`, `bool`,
`ulong`, `string`, a one-dimensional `byte[]`, `Vector3`, `Color`, `Quaternion`,
and any type implementing `RTMPE.Rpc.INetworkSerializable`.

**Why it matters.** A parameter of any other type cannot be sent.

**How to fix.** Change the parameter type, or implement `INetworkSerializable`
on it and register the type before the first call arrives:

- `RpcTypeRegistry.Register<T>()` — preferred, because IL2CPP can see and keep
  the constructor it calls;
- `RpcTypeRegistry.Register(Type)`;
- `[RtmpeRpcSerializable]` on the type together with
  `RpcTypeRegistry.AllowAppDomainScan = true`, which is off by default.

Implementing `INetworkSerializable` alone does not register a type.

<a id="RTMPE1003"></a>
### RTMPE1003 — RPC methods share a method id

**Severity:** Error · **Category:** RTMPE.Rpc

**No quick fix** — the id comes from the method's name, so the remedy is
renaming a method and every call to it.

**What it detects.** Two `[RtmpeRpc]` methods on one type that resolve to the
same method id — most often two overloads of one name.

**Why it matters.** A method id is derived from the type and the method name,
and an incoming call names only the id, so two methods sharing one cannot both
be reached. The SDK refuses the type at run time rather than dispatch one of
them silently.

**How to fix.** Rename one of the methods.

<a id="RTMPE1004"></a>
### RTMPE1004 — RPC method id collides with a reserved id

**Severity:** Error · **Category:** RTMPE.Rpc

**No quick fix** — the id comes from the method's name, so the remedy is
renaming the method.

**What it detects.** An `[RtmpeRpc]` method whose id equals one the SDK reserves
for a built-in message.

**Why it matters.** The id is already taken, so it cannot also address this
method. The SDK refuses the type at run time.

**How to fix.** Rename the method; any new name gives it a new id.

<a id="RTMPE1005"></a>
### RTMPE1005 — RPC method must be declared on a NetworkBehaviour

**Severity:** Error · **Category:** RTMPE.Rpc

**No quick fix** — either the attribute is on the wrong method or the class has
the wrong base, and the two remedies differ.

**What it detects.** `[RtmpeRpc]` on a method of a class that does not derive
from `RTMPE.Core.NetworkBehaviour`.

**Why it matters.** Nothing registers the method, so it can never be called
over the network.

**How to fix.** Move the method onto a `NetworkBehaviour`, or remove the
attribute.

<a id="RTMPE1006"></a>
### RTMPE1006 — RPC method shape cannot be dispatched

**Severity:** Error · **Category:** RTMPE.Rpc

**No quick fix** — the declaration itself has to change, which changes the
method's contract.

**What it detects.** An `[RtmpeRpc]` method that has a `ref`, `out` or `in`
parameter, or is generic.

**Why it matters.** The SDK invokes an RPC with a list of received argument
values: a by-reference parameter can never accept one, and a generic method's
type arguments are unknown at dispatch. A return value is fine — it is
discarded — and is not reported.

**How to fix.** Remove the type parameter, or drop the `ref`/`out`/`in` modifier
and return the value some other way.

<a id="RTMPE1010"></a>
### RTMPE1010 — NetworkVariables derive one identity

**Severity:** Error · **Category:** RTMPE.Sync

**No quick fix** — the remedy is giving each variable the right name, which
only you can choose.

**What it detects.** Two `NetworkVariable` constructions on one object that pass
the same member name — for example `nameof(_health)` written for two members,
or one name used in a class and again in a class derived from it. Every
construction on `this` whose name is known at compile time (`nameof(...)` or a
string literal) is compared. Two constructions in opposite branches of one
`if`/`else`, `?:` or switch expression, or in different sections of a `switch`
statement with no `goto`, are one variable chosen at run time and are not
reported.

A type that repeats a name among its own constructions is reported from that
type alone, in any analysis scope. A type that reuses a name its base class
already holds is reported only when the whole compilation is analysed — see
[When a diagnostic does not show in your IDE](#when-a-diagnostic-does-not-show-in-your-ide).

**Why it matters.** A variable's identity is derived from the concrete type of
the object and the name it is constructed with, and an update names that
identity and nothing else. The SDK registers each identity once per object, so
constructing the second variable throws out of `OnNetworkSpawn`, and the object
is destroyed rather than spawned.

**How to fix.** Give each construction the name of the member it is assigned
to, as `nameof(_field)`, and use different member names in a base class and the
classes derived from it. Renaming a member that has already shipped changes its
identity: clients built with the old name and the new one cannot exchange that
variable, so update them together.

<a id="RTMPE1011"></a>
### RTMPE1011 — NetworkVariable constructed outside OnNetworkSpawn

**Severity:** Warning · **Category:** RTMPE.Sync

**No quick fix** — moving the construction also moves any initialisation that
relied on it running early, and only you can check that.

**What it detects.** A `NetworkVariable` constructed in a field or property
initialiser, a constructor, or one of the Unity messages that run before the
object spawns: `Awake`, `Start`, `OnEnable`, `OnValidate`, `Reset`,
`OnAfterDeserialize` or `OnBeforeSerialize`. It is reported only in classes
derived from `NetworkBehaviour`, the only classes that have `OnNetworkSpawn`.

**Why it matters.** Ownership is valid from `OnNetworkSpawn` on. A variable
constructed earlier — while Unity is still creating or enabling the object, or
in the Editor outside any session (`OnValidate`, `Reset`) — is constructed
before the object's owner is known.

**How to fix.** Construct the variable in `OnNetworkSpawn`, after
`base.OnNetworkSpawn()`, and use it only from then on.

<a id="RTMPE1012"></a>
### RTMPE1012 — NetworkVariableQuaternion seeded with default, not identity

**Severity:** Warning · **Category:** RTMPE.Sync

**No quick fix** — `Quaternion.identity` is the usual seed but not always the
intended one, and only you know which rotation you meant.

**What it detects.** A `NetworkVariableQuaternion` whose initial value is left
out, or is `default`, `default(Quaternion)`, `new Quaternion()` or
`new Quaternion(0, 0, 0, 0)`.

**Why it matters.** `(0, 0, 0, 0)` is not a rotation, and interpolating through
it gives an undefined orientation. At run time the SDK sends
`Quaternion.identity` in its place and logs a warning (at most once a second),
so other clients see identity rather than the rotation you intended.

**How to fix.** Seed the variable with `Quaternion.identity` or the rotation you
intend:

```csharp
_rotation = new NetworkVariableQuaternion(this, nameof(_rotation), Quaternion.identity);
```

<a id="RTMPE1013"></a>
### RTMPE1013 — [NetworkVariable] on a non-NetworkVariable member

**Severity:** Warning · **Category:** RTMPE.Sync

**No quick fix** — changing the member's type and removing the attribute lead to
opposite results, and the rule cannot tell which you meant.

**What it detects.** `[NetworkVariable]` on a field or property whose type does
not derive from `NetworkVariableBase`.

**Why it matters.** The SDK ignores the attribute on such a member, so the
state never replicates.

**How to fix.** Change the member's type to a `NetworkVariable` type, or remove
the attribute.

<a id="RTMPE1020"></a>
### RTMPE1020 — OnDestroy must call base.OnDestroy()

**Severity:** Error · **Category:** RTMPE.Lifecycle

**Quick fix: Call base.OnDestroy()** — appends `base.OnDestroy();` as the last
statement. *Fix All* is available. The fix is withheld for some shapes; see
[Preconditions](#preconditions).

**What it detects.** An `OnDestroy` in a class derived from `NetworkBehaviour`
that does not call `base.OnDestroy()`. This covers an override and a
declaration that merely hides the base method, such as the
`private void OnDestroy()` a converted `MonoBehaviour` often carries — Unity
calls either. A call inside a lambda or a local function does not count, because
it runs only if that function is invoked. The rule is raised only where
`base.OnDestroy()` can compile, that is, when the nearest `OnDestroy` up the base
chain is not `static`, `abstract` or inaccessible.

**Why it matters.** `NetworkBehaviour.OnDestroy` releases the object's spawn
registration. An override that does not call it leaks the registration for the
rest of the session.

**How to fix.** Call `base.OnDestroy()` as the last statement, or apply the
quick fix.

On a declaration that hides the base method, the fix is offered as
*Call base.OnDestroy() and override the hook*, and it repairs the declaration
too, which also clears the compiler's `CS0114` warning. Before:

```csharp
private void OnDestroy()
{
    Cleanup();
}
```

After:

```csharp
protected override void OnDestroy()
{
    Cleanup();
    base.OnDestroy();
}
```

The override takes the base method's own accessibility (`protected`), as C#
requires. The fix adds the call but leaves the declaration as written when
changing it would cost more than the warning it removes:

| The declaration | Why it is left as written |
| --- | --- |
| carries `new` | you have said the hiding is intended, and `new` already silences the warning |
| is `public` or `internal` | an override cannot change accessibility, so repairing it would break callers outside the class (`CS0122`) |
| carries `[Obsolete]` while the base method does not, or the reverse | the repair would trade one warning for another (`CS0809` / `CS0672`) |
| has a comment or directive between its modifiers | rebuilding the modifiers would delete what you wrote |
| is `partial` | the declaration is split across parts |
| hides a base method that is not `virtual` | there is nothing to override; the compiler reports `CS0108`, which only `new` removes |

The command-line equivalent, `make fix … KIND=base-ondestroy`, which the
Conversion Wizard also runs, reads one file and cannot see the base class, so
it appends the call only and prints a note naming the declaration to review.

<a id="RTMPE1021"></a>
### RTMPE1021 — Lifecycle-hook name that does not override the hook

**Severity:** Warning · **Category:** RTMPE.Lifecycle

**No quick fix** — the rule cannot tell which hook, or which signature, you
intended.

**What it detects.** A method in a class derived from `NetworkBehaviour` that
has the name of one of the SDK's lifecycle hooks but does not override it:

- `OnNetworkSpawn()`
- `OnNetworkDespawn()`
- `OnOwnershipChanged(string previousOwner, string newOwner)`
- `OnFixedTick(float deltaTime)`

All four are `protected virtual void`. The rule reports a method with the right
name and a different signature (when the type and its base classes override
that hook nowhere), and a method with the right signature declared with `new`.
A method with the right signature and neither `override` nor `new` already
draws the compiler's `CS0114` warning and is not reported again.

**Why it matters.** The SDK never calls such a method. It compiles, looks wired
up, and silently never runs.

**How to fix.** Declare it as `protected override void` with the hook's exact
parameters.

<a id="RTMPE1022"></a>
### RTMPE1022 — OnNetworkSpawn must call base.OnNetworkSpawn()

**Severity:** Error · **Category:** RTMPE.Lifecycle

**No quick fix** — where the call belongs depends on what the rest of your
method does.

**What it detects.** An `OnNetworkSpawn` override that does not call
`base.OnNetworkSpawn()`, in a class whose base class (other than
`NetworkBehaviour` itself) also overrides `OnNetworkSpawn`. The message names
the base override that will not run. `NetworkBehaviour`'s own `OnNetworkSpawn`
is empty, so a class deriving directly from it is not reported. A call inside a
lambda or a local function does not count.

**Why it matters.** A class builds its `NetworkVariable`s in `OnNetworkSpawn`.
When the override skips its base class's, every variable the base class builds
stays `null`, and code that uses one throws a `NullReferenceException`.

**How to fix.** Call `base.OnNetworkSpawn()` first, so the base class's
variables exist before yours are built:

```csharp
protected override void OnNetworkSpawn()
{
    base.OnNetworkSpawn();
    _ammo = new NetworkVariableInt(this, nameof(_ammo));
}
```

<a id="RTMPE1030"></a>
### RTMPE1030 — Instantiate creates a networked object nothing spawns

**Severity:** Warning · **Category:** RTMPE.Usage

**No quick fix** — spawning needs a registered prefab id and an ownership
decision, and neither is in the call being replaced.

**What it detects.** A call to Unity's `Instantiate` whose copy, as far as the
source shows, carries a `NetworkBehaviour`:

- the original is a `NetworkBehaviour` reference or its `gameObject`
  (`Instantiate(_enemyPrefab)` with `Enemy _enemyPrefab`,
  `Instantiate(_enemyPrefab.gameObject)`, or `Instantiate(gameObject)` inside a
  `NetworkBehaviour`), including a type parameter constrained to a
  `NetworkBehaviour` type;
- or the code asks the copy for one — `Instantiate(prefab).GetComponent<Enemy>()`,
  `TryGetComponent(out Enemy enemy)` — directly or through the local variable
  that holds the copy.

It is not reported for:

- a prefab held as a plain `GameObject`, because the source cannot tell a
  networked prefab from a menu panel or an effect — the readiness report's
  authority section advises on such a type instead;
- copies of assets such as a `Material` or a `ScriptableObject`;
- `GetComponentInParent`, which searches the copy's parent;
- code in a class implementing `INetworkObjectPool`, which creates the instances
  the SDK then spawns;
- code that never ships in a player: Unity's editor assemblies for scripts
  under an `Editor` folder (`Assembly-CSharp-Editor`,
  `Assembly-CSharp-Editor-firstpass`); a type that derives from or implements a
  `UnityEditor` type (a custom inspector, an editor window, a property drawer,
  an importer, a build hook) and the types nested in it; and test code — a type
  with a test, fixture, set-up or tear-down attribute (`[Test]`, `[TestCase]`,
  `[UnityTest]`, `[TestFixture]`, `[SetUp]` and the like) on itself or a method,
  or deriving from such a type.

Your own editor-only assembly definitions and static editor utilities on plain
classes are analysed; suppress the warning there.

**Why it matters.** Only `SpawnManager.Spawn` puts an object on the network. A
copy made with `Instantiate` runs its `NetworkBehaviour`s with nothing behind
them: no other client sees it, none of its `NetworkVariable`s replicate, and a
write to one is refused because the object never spawned.

**How to fix.** Spawn the object from its registered prefab id:

```csharp
// Before: a local copy no other client sees
var enemy = Instantiate(_enemyPrefab, at, Quaternion.identity);

// After: a networked spawn
if (NetworkManager.TryGetInstance(out var manager)
    && manager.Spawner.TryGetPrefabId(_enemyPrefab.gameObject, out uint prefabId))
{
    manager.Spawner.Spawn(prefabId, at, Quaternion.identity);
}
```

A copy that is meant to stay local — a character on a selection screen, a
preview — is correct as it is. Suppress the warning at that call with
`#pragma warning disable RTMPE1030`, or with
`[SuppressMessage("RTMPE.Usage", "RTMPE1030")]` on the method, accessor, local
function or type (`[UnconditionalSuppressMessage]` works too). An attribute
aimed at a return value, backing field or parameter (`return:`, `field:`,
`param:`) suppresses nothing. A suppression written in the source also removes
the call from the readiness report's authority classification; one made in
`.editorconfig` or a global suppression file hides the warning only.

<a id="RTMPE2001"></a>
### RTMPE2001 — MonoBehaviour with replicable state is a NetworkBehaviour candidate

**Severity:** Info · **Category:** RTMPE.Conversion

**Quick fix: Rebase to NetworkBehaviour** — changes the base class to
`NetworkBehaviour` and adds `using RTMPE.Core;`. *Fix All* is not available.
The fix is withheld for a class with an intermediate base; see
[Preconditions](#preconditions).

**What it detects.** A concrete class derived from `MonoBehaviour` (and not
already a `NetworkBehaviour`) with at least one instance field of a replicable
type — `int`, `float`, `bool`, `string`, `Vector2`, `Vector2Int`, `Vector3`,
`Quaternion`, or a `List<T>` of `int`, `float`, `bool`, `string`, `Vector2`,
`Vector2Int` or `Vector3`. The field must not be Unity-serialised (`public` or
`[SerializeField]` fields are Inspector configuration), `static`, `const` or a
property's backing field, and its name must not look like a credential
(containing `key`, `token`, `secret` or `password`). A `readonly` field counts
only when it is a list, because `private readonly List<T> _items = new()` is how
a list is usually declared and the conversion removes the modifier.

**Why it matters.** That state would have to replicate for the behaviour to
work in multiplayer, and only a `NetworkBehaviour` can hold replicated state.

**How to fix.** Apply the quick fix, or change the base class by hand. Then
convert the state itself — see [`RTMPE2002`](#RTMPE2002).

<a id="RTMPE2002"></a>
### RTMPE2002 — Field is a NetworkVariable conversion candidate

**Severity:** Info · **Category:** RTMPE.Conversion

**Quick fix: Convert to NetworkVariable** (in-place arm) or
**Add companion NetworkVariable** (companion arm). *Fix All* is not available.
The fix is withheld for some shapes; see [Preconditions](#preconditions).

**What it detects.** A field of a concrete class derived from `NetworkBehaviour`
that holds state other clients should see. The rule has two arms, which your
IDE titles differently:

- **In place** — *Plain field is a NetworkVariable candidate*: a private field
  that is not Unity-serialised. Only private fields are offered, because the
  conversion rewrites one file and only a private field has every use inside
  it.
- **Companion** — *Written serialized field is a companion-NetworkVariable
  candidate*: a Unity-serialised field (`public` or `[SerializeField]`) that
  your code writes at run time. This arm is a whole-solution rule (see
  [When a diagnostic does not show in your IDE](#when-a-diagnostic-does-not-show-in-your-ide)).

The field must be of a replicable type — the types listed under
[`RTMPE2001`](#RTMPE2001), a `List<T>` included — and not `static`, `const`,
a property's backing field, already marked `[NetworkVariable]`, or named like a
credential (`key`, `token`, `secret`, `password`). A `readonly` list is offered:
the conversion removes the modifier, because the list is constructed in
`OnNetworkSpawn`.

**Why it matters.** A plain field is local to each client; a `NetworkVariable`
is written by the object's owner and replicated to every other client.

**How to fix.** Apply the quick fix. The in-place arm changes the field's type
to the matching wrapper, constructs it in `OnNetworkSpawn`, and rewrites every
use in the class to `.Value` (a list keeps its uses as they are):

```csharp
// Before
private int _score;

// After
private NetworkVariableInt _score;

protected override void OnNetworkSpawn()
{
    base.OnNetworkSpawn();
    _score = new NetworkVariableInt(this, nameof(_score));
}
```

The companion arm keeps the serialised field and its Inspector value, and adds
a variable named after it with a `Net` suffix (`_maxHealth` gets
`_maxHealthNet`), seeded from the field in `OnNetworkSpawn`. It does not change
any use of the original field: move the reads and writes that should replicate
onto the companion's `Value`.

The fix reads no identity record: the identity follows from the type and the
member name the edit writes. Do not give a derived class's variable a name its
base class already uses for one ([`RTMPE1010`](#RTMPE1010)). To convert several
fields, or several types, in one step, use the Conversion Wizard or
`make convert` ([Automation](automation.md#5-convert)).

<a id="RTMPE2003"></a>
### RTMPE2003 — Frame loop without an IsOwner guard

**Severity:** Info · **Category:** RTMPE.Conversion

**Quick fix: Add the IsOwner guard** — inserts `if (!IsOwner) return;` as the
loop's first statement. *Fix All* is available. The fix is withheld for some
shapes; see [Preconditions](#preconditions).

**What it detects.** An `Update`, `FixedUpdate` or `LateUpdate` in a class
derived from `NetworkBehaviour` that does not open with the owner guard and
writes the object's own state: its own fields, a `NetworkVariable` it holds, an
element of its own collection, or its own `transform` — directly, through a
`ref` or `out` argument, or through a method the class declares. Each loop is
judged and reported on its own, and the message names it. These are not
evidence, so a loop doing only this is not reported:

- reading input;
- writing through a component the class holds — `_animator.SetBool(…)`,
  `_rb.velocity = v`, or a `Transform` it keeps in a field;
- updating a label or an effect that every client should run.

A loop that already opens with the guard is not reported.

**Why it matters.** Every client runs the frame loops of every copy of the
object. Without the guard, each client simulates the object on its own and the
copies drift apart.

**How to fix.** Apply the quick fix, or add the guard by hand. Do not add it to a
loop that applies received state on the other clients — typically one that
exits early for the owner through a test of its own, such as
`if (_locallyOwned) return;`. The rule reports that loop because the write is
real, and the fix is offered because the rule cannot read your own flag, but the
guard would leave the loop running on no client that needs it. The readiness
score's Ownership dimension marks such a loop as unguarded too; leave it as it
is.

<a id="RTMPE2004"></a>
### RTMPE2004 — Owner-guarded mutation is an Enhanced-RPC candidate

**Severity:** Info · **Category:** RTMPE.Conversion

**No quick fix** — the conversion needs a target audience, and choosing one is
a design decision.

**What it detects.** A method that opens with the owner guard and changes the
object's state, making it a candidate for an Enhanced RPC. Specifically, a
`public`, non-static, non-abstract, non-override, non-generic, non-partial
`void` method with a block body, in a concrete class derived from
`NetworkBehaviour`, that:

- is not already marked `[RtmpeRpc]`;
- takes only parameters of type `int`, `float`, `bool`, `ulong`, `string`,
  `byte[]`, `Vector3`, `Color` or `Quaternion`, with no `ref`, `out`, `in`,
  `params` or default values;
- opens with `if (!IsOwner) return;` and then changes the object's own state;
- does not already send (it calls no `SendRpc…`, `SendEnhancedRpc…` or `RPC…`
  method).

Unity messages, SDK hooks and methods whose names look like credentials are
never reported. The owner guard alone is not enough — a method that changes no
state is not a candidate. A method that applies state on every client has no
owner guard by design and is not reported here; it is converted with the
*Applies on receivers* designation instead.

**Why it matters.** An owner-guarded method runs only on the owner's client;
sending it as an RPC is how the change reaches the other clients.

**How to fix.** Convert it with `make gen-rpc` or the Conversion Wizard's
*Generate Enhanced RPC*, naming the target. The conversion keeps the owner
guard, and the target decides where the body runs:

- `Server` — the body runs only in the project's server function, an HTTPS
  endpoint you run and register in the portal. With none registered the call
  reaches no one.
- `Others` — the sender is excluded and every receiver is a non-owner, so the
  guarded body runs on no client at all.
- `All` — the owner is the only client the guard admits, so the direct local
  call becomes the same call after a round trip through the server.

The command and the wizard state the reach of the target you chose on the diff.
[Automation](automation.md#5-convert) covers the targets and the refusals in
full.

<a id="RTMPE2005"></a>
### RTMPE2005 — Auto-property holds replicable state no conversion can retype

**Severity:** Info · **Category:** RTMPE.Conversion

**No quick fix** — the property's accessors are your code, and making it
depend on the object having spawned is a decision about the type.

**What it detects.** A settable, non-static auto-property of a replicable scalar
type (see [`RTMPE2001`](#RTMPE2001); lists are not reported) on a concrete class
derived from `NetworkBehaviour` — for example `public int Score { get; set; }`.
Properties named like credentials are excluded. A `[field: SerializeField]`
auto-property holds Inspector data and is not reported. Neither the
[`RTMPE2002`](#RTMPE2002) quick fix nor `make convert` handles it: write the
companion shape by hand — keep the property as Inspector data, add a separate
`NetworkVariable`, and seed it from the property in `OnNetworkSpawn`.

**Why it matters.** An auto-property's value lives in a field the compiler
generates, which no conversion can retype or name, so the conversions pass it
by.

**How to fix.** Keep the property and put a `NetworkVariable` behind it; its
callers do not change:

```csharp
private NetworkVariableInt _score;

public int Score
{
    get => _score.Value;
    set => _score.Value = value;
}

protected override void OnNetworkSpawn()
{
    base.OnNetworkSpawn();
    _score = new NetworkVariableInt(this, nameof(_score));
}
```

The property then behaves differently in three ways:

| | Auto-property | Backed by a `NetworkVariable` |
| --- | --- | --- |
| Read before the object spawns | returns `default` | throws `NullReferenceException` — the variable does not exist yet |
| Write on a client that does not own the object, or after `OnNetworkDespawn` | stored | refused, with a warning |
| Write of a value the variable cannot send (for example a non-finite `float` or vector component) | stored | refused, with a warning |

If callers must write from any client, send the owner an RPC or request
ownership. If they must read before spawn, keep a plain field and seed the
variable from it — the companion shape.

Carry the property's initialiser into the construction:
`public int Score { get; set; } = 100;` becomes
`new NetworkVariableInt(this, nameof(_score), 100)`, because a property with
accessor bodies cannot keep an initialiser. A `NetworkVariableQuaternion` needs
`Quaternion.identity` as its seed ([`RTMPE1012`](#RTMPE1012)). A base class and a
derived class that both apply this change must use different variable names
([`RTMPE1010`](#RTMPE1010)).

<a id="RTMPE9001"></a>
### RTMPE9001 — Type authority classification (advisory)

**Severity:** Info · **Category:** RTMPE.Authority

**No quick fix** — the rule is advisory and reports no defect.

**What it detects.** Every concrete class derived from `MonoBehaviour`,
`NetworkBehaviour`s included. It reports the authority posture a deterministic
rubric reads from the type's own code — its base class, replicated state, owner
guards, RPCs, lifecycle overrides and scene wiring — as `Authoritative`,
`OwnerPartitioned`, `Presentation`, `Orchestrator` or `Undetermined`, with the
reason.

**Why it matters.** It shows where each type's decisions are made before you
convert it. It changes no source, transfers no ownership and enforces nothing.
The same classification feeds the readiness report's Authority section and its
Authority dimension.

**How to fix.** Nothing to fix. For a networked type classified `Undetermined`,
the readiness report asks who decides for it — see
[Automation](automation.md#4-answer-the-authority-questions). To hide the rule,
set its severity to `none`.

---

## See also

- [Automation](automation.md) — the readiness score, the Conversion Wizard and
  the convert → re-score loop
- [Troubleshooting](troubleshooting.md) — editor and runtime problems
- [Getting Started](getting-started.md) — installing and configuring the SDK

---

*RTMPE SDK 1.0.7*
