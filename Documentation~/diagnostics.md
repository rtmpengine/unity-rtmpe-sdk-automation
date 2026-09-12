# RTMPE SDK — Analyzer Rule Reference

> SDK Version: `com.rtmpe.sdk 1.0.5`

Every diagnostic the SDK's Roslyn analyzers raise, what it means, and — the
question this page exists to answer first — **whether it has a quick fix at
all**.

Most RTMPE rules deliberately have no quick fix: they report a condition whose
correct resolution is a design decision, not a mechanical edit. On those rules
your editor offers only *Suppress / Configure*, and that is the complete and
correct behaviour, not a broken toolchain. Only the rules marked below carry a
lightbulb.

Each rule's id is a link target: the "?" beside a diagnostic in your IDE lands
on its entry here.

---

## Rule index

| Id | Category | Severity | Quick fix |
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
| RTMPE2001 | RTMPE.Conversion | Info | Rebase to NetworkBehaviour |
| RTMPE2002 | RTMPE.Conversion | Info | Convert to NetworkVariable (or add a companion), see † |
| RTMPE2003 | RTMPE.Conversion | Info | Add the IsOwner guard |
| RTMPE2004 | RTMPE.Conversion | Info | none |
| RTMPE2005 | RTMPE.Conversion | Info | none |
| RTMPE9001 | RTMPE.Authority | Info | none |

† **`RTMPE2002`'s fix is offered on its own.** It reads no identity record and
needs none: a variable's wire id is derived from the type of the object it is on
and the name the variable is constructed with, so the edit carries everything the
identity depends on. ⚠️ At run time that type is the **concrete** one, which is
the same type the tool converts in every shape but one: a base and a derived
class spending a single member name fold to one identity on an instance of the
derived type, and the conversion refuses that pair rather than emitting it. It
used to withhold itself in a stock Unity project, because no
`<Type>.rtmpe-ids.json` reaches the compiler as an additional file there and the
fix could not mint an id it was unable to persist. What still withholds it is the
shape gate alone — see [the rule's entry](#RTMPE2002).

Four rules carry a fix. They are `RTMPE1020`, `RTMPE2001`, `RTMPE2002` and
`RTMPE2003`; all but `RTMPE1020` are **Info** severity, which
most editors render as a faint hint rather than a squiggle — see
[Finding the Info-severity rules](#finding-the-info-severity-rules) if you have
never seen one.

---

<a id="where-diagnostics-appear"></a>
## Where each diagnostic appears

⚠️ **Read this before concluding the analyzers are not loaded.** The two places
a diagnostic can show up do not show the same set, and the difference is not a
setting you got wrong.

| Surface | Shows | Does not show |
| --- | --- | --- |
| **Unity Console** | `Error` and `Warning` rules only | **every `Info` rule** |
| **IDE** (squiggles, Problems, Error List) | all severities, `Info` included | the two **whole-solution** rules below, until a full build |
| **IDE lightbulb** (`Ctrl+.` / `Cmd+.`) | the four fixable rules | the rest offer only *Suppress / Configure* |

⚠️ **Two rules are held back until the end of compilation** — `RTMPE1010` and
the companion arm of `RTMPE2002` — so a stock editor, which analyses open files
only, does not show them until you build. That is the one exception to the IDE
row above; it is explained, with the setting that widens the scope, under
[Whole-solution rules](#finding-the-info-severity-rules). Every other rule in
the table is present in the open-files scope — measured against the analyzers,
not assumed.

Thirteen of the twenty rules are `Error` or `Warning` and reach both. The
remaining seven — `RTMPE1000`, `RTMPE2001`–`RTMPE2005` and `RTMPE9001` — are
`Info` and **can never appear in the Unity Console**, whatever your project
settings say. Unity's Console renders warnings and errors; it has no informational tier
for compiler diagnostics.

So an empty Console after compiling a class that raises only `Info` rules is the
system working. To confirm the analyzers loaded, use the `Error`-severity
one-file check below — not an `Info` rule.

### Supported IDEs

Any editor that loads Roslyn analyzers from the package's `Analyzers/` folder:

| Editor | Diagnostics | Quick fixes | Notes |
| --- | --- | --- | --- |
| **JetBrains Rider** | yes | yes | *Inspection Results* also lists `Info` |
| **Visual Studio 2022** | yes | yes | `Info` appears in the Error List under *Messages* |
| **VS Code + C# Dev Kit** (Roslyn LSP) | yes | yes | |
| **VS Code + OmniSharp** (legacy C# extension) | yes | **no** | it supplies no Workspaces layer, so the lightbulb offers only *Suppress / Configure* |

⛔ **The OmniSharp row is not a caveat, it is the commonest cause of "the quick
fix is missing" on VS Code.** The remedy is in
[Troubleshooting → Authoring-tool issues](troubleshooting.md#authoring-tool-issues-roslyn-analyzer--conversion-quick-fixes),
which is the authoritative copy of this table; the rows here restate it and must
not drift from it.

⚠️ Whichever you use, the analyzers are read from the `.csproj` Unity generates.
After importing a sample or adding scripts outside your editor, **reload the
window** (VS Code: *Developer: Reload Window*; Rider/VS: reopen the solution) —
a file that reached disk after the project was loaded is analysed only once the
project is re-read.

### What the shipped samples raise

The four samples the package offers (*Package Manager → RTMPE SDK → Samples*)
raise exactly twelve diagnostics between them, and **every one is `Info`** — so
the Unity Console stays empty on all four, and you see them by opening the
scripts in your IDE.

| Sample | Type | Id | Why |
| --- | --- | --- | --- |
| Basic Connection | `ConnectionTest` | `RTMPE2001` | a `MonoBehaviour` holding plain non-serialized state (`_statusLine`, `_rttLine`, `_shouldReconnect`) |
| Basic Connection | `ConnectionTest` | `RTMPE9001` | its advisory authority classification — *Presentation* |
| Player Spawn Flow | `GameManager` | `RTMPE9001` | *Orchestrator* — it drives the session and holds none of its state |
| Player Spawn Flow | `PlayerController` | `RTMPE1000` | the structural note every `NetworkBehaviour` earns |
| Player Spawn Flow | `PlayerController` | `RTMPE9001` | *Authoritative* — replicated state, an owner-guarded member, a lifecycle override |
| Player Spawn Flow | `PlayerController` | `RTMPE2004` | `AddScore` is an owner-guarded mutation, so an Enhanced-RPC conversion is on offer |
| Scene Transitions | `SceneSwitcher` | `RTMPE9001` | *Presentation* — two buttons and one call |
| Two Player Room | `TwoPlayerAvatar` | `RTMPE1000` | the structural note every `NetworkBehaviour` earns |
| Two Player Room | `TwoPlayerAvatar` | `RTMPE9001` | *OwnerPartitioned* — an owner-guarded member and a lifecycle override, with the replicated state held by components rather than declared here |
| Two Player Room | `TwoPlayerCredentials` | `RTMPE9001` | *Presentation* — it registers a credential source and reports; it holds no game state |
| Two Player Room | `TwoPlayerRoomHud` | `RTMPE9001` | *Presentation* — four labels and no state at all |
| Two Player Room | `TwoPlayerSpawnPoints` | `RTMPE9001` | *Presentation* — a table of spawn points and the one delegate it registers |

Every one is advisory and every one is correct. Two rules raise nothing at all:
`RTMPE2002` offers to replicate state held in a plain field — the shipped
player's plain fields are Inspector configuration and the two-player avatar
declares none — and `RTMPE2003` offers the `IsOwner` guard, which every mutating
member already carries.

This inventory is pinned by a test **with its file and line**, so a rule that
starts or stops firing on a shipped sample fails the build rather than making
this page wrong.

### The Documentation buttons need a network connection

Both windows' **Documentation** control opens the rendered page in your browser,
because a raw `.md` handed to the operating system is routed by whatever owns
that file extension — which on some machines is not a reader at all. Offline, the
same pages ship inside the package under `Documentation~/`; open them from there
by hand.

### After applying a quick fix

1. Read the edit before saving it — every fix is a source rewrite you own.
2. Save the file and let Unity finish compiling.
3. Confirm **0 compilation errors** in the Console.
4. Confirm the RTMPE diagnostic is gone from the IDE.
5. Confirm no new C# warning was introduced. If one is, it is a defect —
   report it. (`RTMPE1020` used to leave `CS0114`; see its entry below.)
6. Conversions are **source-only**: prefabs and scenes are not rewritten, so
   re-check any component whose fields or base type changed.

---

## The lightbulb is missing — read in this order

0. **Is the diagnostic reported at all?** A lightbulb answers a diagnostic; where
   there is none there is nothing for it to offer, and the three steps below all
   presume one fired. Find the rule's entry and check its trigger — the commonest
   surprise is `RTMPE2001`, which needs a `MonoBehaviour` **holding plain,
   non-Inspector state**: a class with no fields raises nothing and is correct.
   And remember that **three of the four** fixable rules are `Info`, which the
   Unity Console never renders — look in your IDE, not in the Console. The
   exception is `RTMPE1020`, which is `Error` and does reach the Console; that is
   why it, and not an `Info` rule, is the one-file check below.

1. **Is the rule in the fixable set?** Check the table. On any rule marked
   `none`, *Suppress / Configure* is the only entry there will ever be. This is
   the most common cause by a wide margin: the four Warning-severity rules
   (`RTMPE1011`, `RTMPE1012`, `RTMPE1013`, `RTMPE1021`) have no fix between
   them, so a session spent on warnings never sees a lightbulb.

2. **Is the fix's precondition met?** A fix that cannot produce a correct edit
   withholds itself rather than offering an inert action — see
   [Preconditions](#preconditions) for the case that applies to each rule.

3. **Is your editor loading the fix assembly?** Only then is it a host problem.
   That path is covered in
   [Troubleshooting → Authoring-tool issues](troubleshooting.md#authoring-tool-issues-roslyn-analyzer--conversion-quick-fixes).

### The one-file check

`RTMPE1020` is the fastest confirmation that the whole chain works: it is
**Error** severity, so it cannot be missed, and the declaration below satisfies
every one of its fix's preconditions. Paste it as written — a body collapsed
onto one line (`OnDestroy() { }`) is one of the withheld shapes below, and would
make this check report a fault that is not there.

```csharp
using RTMPE.Core;

public class ProbeBehaviour : NetworkBehaviour
{
    protected override void OnDestroy()
    {
        // RTMPE1020 (Error) — this override never chains base.OnDestroy()
    }
}
```

Expected: *Call base.OnDestroy()*. If that appears, the analyzer, the code-fix
assembly, and your editor's Workspaces layer are all working, and any other
missing lightbulb is explained by step 1 or step 2 above.

### Finding the Info-severity rules

`RTMPE2001`–`RTMPE2004` are the conversion opportunities — the rules the SDK
exists to offer — and all four are **Info**. Editors de-emphasise Info by
default:

- **VS Code** — Info diagnostics are marked with a faint dotted underline
  rather than a squiggle, so they are easy to scroll past. The lightbulb also
  only appears while the caret is inside the reported span: put the caret on
  the reported symbol and press `Ctrl+.` / `Cmd+.` rather than looking for a
  gutter icon.
- **Whole-solution rules** — `RTMPE1010` and the companion arm of `RTMPE2002`
  are reported at the end of compilation, because their evidence is a scan of
  the whole compilation rather than one file. VS Code's default background
  analysis scope is open files only, so what is reported that way does not
  appear until `dotnet.backgroundAnalysis.analyzerDiagnosticsScope` is set to
  `fullSolution`. Rider and Visual Studio have the equivalent setting under
  solution-wide analysis.

  `RTMPE1010` is split across that boundary, because only half of it needs the
  scan. A type that repeats an id among **its own** constructions is decided from
  that type alone and is reported in any scope, default settings included. A type
  that reuses an id **its base class already holds** is not: that collision is
  only visible once the whole compilation is in hand, and it is the half the
  setting above governs. Both halves report the same id at the same severity, so
  a project that never changes the setting still sees the common case — an
  editor left on its defaults is not silent about an Error.

### Preconditions

A fix withholds itself when it cannot produce output identical to what the
headless converter would produce. A withheld fix is a *not-offered* lightbulb,
never a partial edit.

**A lightbulb has no channel to explain its own absence** — the IDE simply shows
nothing. The headless hosts drive the same transforms and *do* report it: each
prints `refused: <reason>` on stderr, naming the exact condition below. When you
expect a fix and do not get one, run the same conversion through the host for
that rule and read the reason:

| Rule | Host |
| --- | --- |
| `RTMPE1020` | `make fix KIND=base-ondestroy` |
| `RTMPE2001` | `make fix KIND=rebase` |
| `RTMPE2002` | `make convert` |
| `RTMPE2003` | `make fix KIND=owner-guard` |
| `RTMPE2004` | `make gen-rpc` |

The **Conversion Wizard** surfaces the same text: a refused run leaves the
reason in the window's error box rather than failing silently.

> **These hosts need the .NET 8 SDK, and nothing beyond it.** The `make` verbs
> above and the Conversion Wizard all drive the same headless conversion engine,
> and that engine ships inside this package under `Automation~/` — the `make`
> verbs are the shipped `Automation~/Makefile`'s, and the wizard resolves the
> same folder without being told where it is. One build from a terminal arms
> both.
>
> Until that build has been run, this page is the substitute for the reason
> string: the table below states, per rule, every condition under which a fix is
> withheld. Compare your declaration against the row for the rule you expected a
> fix from, and apply the edit by hand.
>
> [Automation](automation.md) is the full route: prerequisites, the one build
> command, and the convert → re-score loop.

| Rule | Withheld when |
| --- | --- |
| `RTMPE1020` | The declaration is a shape the call cannot be appended to: an expression body (`=> …`) or no body at all; a body written entirely on one line, which has no line structure to extend; a `static` or value-returning method of that name, which is not the Unity message and could not host the call; or a body carrying `#if` boundaries, where an appended call could land inside a conditional region and make the release conditional with it. |
| `RTMPE2001` | The type reaches `MonoBehaviour` through an intermediate base class. The rule fires on any transitive subclass, but the rebase only swaps a `MonoBehaviour` written directly in the base list — for a class hierarchy the correct edit is to rebase the intermediate base, so the fix defers rather than rewrite the wrong type. |
| `RTMPE2002` | One gate, where there used to be two. The **identity** gate is gone: the fix consulted `<Type>.rtmpe-ids.json` through AdditionalFiles and withheld itself when no record had issued this member an id, because a `CodeAction` cannot persist a sidecar atomically with a document edit. An identity is derived from the type the member is declared on and its own name now, so the edit carries it and nothing needs persisting. **Shape** — the enclosing type is `partial` or does not parse; an existing `OnNetworkSpawn` does not override the runtime hook, is expression-bodied, has a single-line body, or sits in a conditional-compilation region; the field's initializer is outside the allowed set (literals, `default`, the well-known pure statics); a reference to it is ambiguous (shadowed, reached through a receiver other than `this`, or from outside the declaring type) or sits in a pre-spawn context — `Awake`, `Start`, `OnEnable`, `OnValidate`, `Reset`, a constructor, another field initializer, **or any member one of those reaches**, including through a generic call and through Unity's by-name dispatchers (`Invoke`, `StartCoroutine`, `SendMessage`) — where the variable is still `null`; the member is used somewhere that needs a storage location rather than a value — passed by `ref`/`out`, bound as a `ref` local or `ref` return, or with its address taken — since the converted member is read through a property; or the companion name collides with an existing member, a nested type or delegate, or the enclosing type's own name. |
| `RTMPE2003` | Expression-bodied, single-line, or `#if`-bearing declarations; a type or body that already declares the name `IsOwner`, where the inserted guard would read that member or local instead of the inherited ownership flag; and a body whose **leading test reads `IsOwner` without exiting** — `if (IsOwner) { … } else { … }` — where a guard placed above it would take every non-owner frame first and strand the branch written for them. ⛔ A leading exit that says nothing about ownership is a precondition, not a partition, and the guard is placed above it as asked. An empty, bodiless, `static` or value-returning `Update` raises no diagnostic in the first place, so no lightbulb is withheld there; `make fix KIND=owner-guard` calls the transform directly and still refuses each of them by name. |
| `RTMPE2004` (through `make gen-rpc`, which is where this conversion lives — the rule itself offers no lightbulb) | The rewrite cannot be proven safe from this one file alone. This is the widest refusal set of the five conversions, and it spans three levels: the enclosing type (does not parse, is `partial`, declares no base type), the declaration (`static`/`abstract`/non-public/generic/value-returning, a parameter carrying a modifier or a default, a parameter type outside the serializer's closed set, `#if` trivia), and every call site (a shadowed name, the same name on a base type, a call through `base` or through a receiver other than `this`, a reference from outside the declaring type or inside a nested type, a method-group reference, recursion, named or `ref`/`out` arguments, a partial argument list, `#if` trivia, a comment inside the argument list — the send rebuilds that list and the comment would be deleted). A member named `RPC` on the type, or on a base declared in the same file, refuses the whole plan: the emitted `this.RPC(…)` would bind to it rather than to the SDK's send. |

### The path that needs no IDE

**Window → RTMPE → Conversion Wizard** runs the same
`RTMPE.SDK.Transforms` core out of process and produces byte-identical output.
It is a full-capability alternative to the lightbulb, not a degraded mode, and
it is the supported route for `RTMPE2002` inside Unity — **wherever the
conversion engine is reachable**. The wizard launches that engine from the copy
shipped under `Automation~/`, which it resolves on its own; inside an RTMPE
repository checkout it prefers the live source there instead. Until the engine
has been built there is nothing to launch, and conversion is a manual edit
guided by [Preconditions](#preconditions).

---

## Rules

<a id="RTMPE1000"></a>
### RTMPE1000 — Type inherits NetworkBehaviour

Informational marker: the type participates in the network object lifecycle.
Raised so tooling and readers can identify networked components at a glance; it
reports no problem and needs no action.

**No quick fix** — there is nothing to repair; the rule states a fact about the type.

<a id="RTMPE1001"></a>
### RTMPE1001 — RPC method must be public and instance

The registry discovers and dispatches public instance methods only, so a
declaration that is non-public, `static`, or `abstract` is never reached by an
inbound call. Make the method a `public` non-static, non-abstract method.

**No quick fix** — widening a method's accessibility, or dropping `static`, changes its
contract with every existing caller.

<a id="RTMPE1002"></a>
### RTMPE1002 — RPC parameter type is not serializable

`RpcSerializer` encodes a closed set: `int`, `float`, `bool`, `ulong`,
`string`, a one-dimensional `byte[]`, `Vector3`, `Color`, `Quaternion`, and any
type implementing `RTMPE.Rpc.INetworkSerializable`. Anything else — including a
multidimensional byte array — cannot cross the wire. Change the parameter type,
or implement `INetworkSerializable` on it —
noting that an implementer must also be **registered** before it can round-trip.
Three paths do that: `RpcTypeRegistry.Register<T>()` (preferred — the
constructor call is statically dispatched, so IL2CPP can trace and preserve it),
`RpcTypeRegistry.Register(Type)`, or `[RtmpeRpcSerializable]` together with
`RpcTypeRegistry.AllowAppDomainScan = true`, which is **off by default**. That
registration is not automatic under any of them, which is why the conversion
tooling declines to generate for one.

⚠️ Before **8.0.0** only the first path worked: the other two recorded the type
for name resolution and installed no way to construct it, so a payload
registered either way resolved and then surfaced as `null` — on Mono and in the
Editor as much as under IL2CPP, though the API documentation described it as an
AOT concern.

**No quick fix** — the remedy is a different parameter type or a different payload shape,
and only the call sites can say which.

<a id="RTMPE1003"></a>
### RTMPE1003 — RPC methods share a method id

Method ids are derived deterministically from the runtime type and method name
(an FNV-1a hash of `Type.FullName.MethodName`), and the receiver dispatches on the id
alone. Two methods resolving to one id means only one of them can ever be
reached. Rename one of them.

**No quick fix** — the id is derived from the method name, so clearing a collision means
renaming a method and every call to it.

<a id="RTMPE1004"></a>
### RTMPE1004 — RPC method id collides with a reserved id

The derived id lands on an id the runtime reserves for a built-in message, so
the id is already spoken for and cannot also address this method. Rename the
method — the id is a hash of the type and method name, so any rename that keeps
the type moves it.

**No quick fix** — the id is derived from the method name, so clearing a reserved
collision means renaming the method.

<a id="RTMPE1005"></a>
### RTMPE1005 — RPC method must be declared on a NetworkBehaviour

`[RtmpeRpc]` outside a `RTMPE.Core.NetworkBehaviour` subclass is inert: nothing
registers the method for dispatch. Move it onto a `NetworkBehaviour`, or drop
the attribute.

**No quick fix** — either the attribute sits on the wrong method or the type has the wrong
base, and the two remedies are not interchangeable.

<a id="RTMPE1006"></a>
### RTMPE1006 — RPC method shape cannot be dispatched

The dispatcher invokes through reflection over a boxed argument vector. A
`ref`/`out`/`in` parameter can never bind to one, and an open generic method
cannot be closed at dispatch time. A non-void return is fine — the result is
simply discarded — and is deliberately not flagged.

**No quick fix** — the declaration itself has to change: a type parameter removed,
or a `ref`/`out`/`in` modifier dropped and the value returned some other way. Both
rewrite the method's contract rather than its body.

<a id="RTMPE1010"></a>
### RTMPE1010 — NetworkVariables derive one identity

A variable's wire identity is derived from the **concrete** type of the object it
is on and the name it is constructed with, and an inbound update names that
identity and nothing else — so two constructions naming one member means only the
first is ever updated. Give each construction the name of the member it is
assigned to, as `nameof(_field)`. The concrete type is also why a base class and
a derived class cannot both spend one name: on an instance of the derived type
they are a single identity, and the second registration throws out of
`OnNetworkSpawn` rather than desyncing quietly.

Compared across every construction this object owns whose member name the
compiler can evaluate — `nameof(...)` and string literals both. A type that
repeats a name among its own constructions is reported from that type alone, in
any analysis scope; a type that reuses a name its base class already holds is
reported at the end of compilation, because only then is the base's contribution
in hand. Two constructions in opposite
arms of one conditional — the two sides of an `if`/`else` or `?:`, or different
`switch` sections — are one variable selected at runtime rather than a collision,
and are not reported; sections a `goto` can chain together are, because both then
run. See
[whole-solution rules](#finding-the-info-severity-rules) if it does not appear in
your editor.

**No quick fix** — the identity is derived from the member name, so the fix is to
give each construction the name of the member it is assigned to. ⚠️ Renaming a
member that has already shipped is a new identity and therefore a wire break for
builds already in the field; the generated `.rtmpe-ids.json` record is where that
shows up as a diff.

<a id="RTMPE1011"></a>
### RTMPE1011 — NetworkVariable constructed outside OnNetworkSpawn

Ownership is not yet valid while Unity is still activating the object, so a
variable constructed there can capture the wrong write permissions. Flagged in a
field or property initializer, a constructor, `Awake`, `Start`, and `OnEnable`;
`OnNetworkSpawn` and anything later is correct. Raised only inside a
`NetworkBehaviour` — the remedy names a hook no other type has, so a
`NetworkVariable` built in a plain helper class is left alone rather than told to
move somewhere that does not exist there.

**No quick fix** — moving the construction into `OnNetworkSpawn` moves whatever
initialization depended on it having run that early.

<a id="RTMPE1012"></a>
### RTMPE1012 — NetworkVariableQuaternion seeded with default, not identity

`default(Quaternion)` is `(0,0,0,0)` — not a rotation. Any interpolation through
it produces undefined orientation. Raised when the initial value is
`default`/`default(Quaternion)`, `new Quaternion(0, 0, 0, 0)`, or — the most
common case — **omitted entirely**, which lands on the same zero quaternion.
Seed with `Quaternion.identity`.

**No quick fix** — `Quaternion.identity` is the usual seed but not always the intended
one; substituting it silently would replace a wrong rotation with a different
wrong rotation. The analyzer will not edit your source for you, which is why the
diagnostic exists.

⚠️ **The runtime does substitute, and that is a different decision.** Every
raw-quaternion sender passes what it is about to write through
`WireQuaternion.ForWire`, so a zero quaternion leaves as identity rather than as
a record every peer refuses. The two positions are not in tension: at the source
level there is a right answer only the author knows, and the analyzer asks for
it; on the wire the alternative to substituting is not "the author's rotation",
it is **nothing at all** — `NetworkVariableQuaternion.Deserialize` refuses a
non-unit value and keeps the prior one, so the variable never propagates. The
substitution is reported once a second at runtime; this diagnostic is still how
you fix the cause.

<a id="RTMPE1013"></a>
### RTMPE1013 — [NetworkVariable] on a non-NetworkVariable member

The runtime only registers members whose type derives from
`NetworkVariableBase`; on anything else the attribute has no effect and the
state silently never replicates. Either change the member's type or remove the
attribute.

**No quick fix** — changing the member's type and removing the attribute lead to opposite
outcomes, and the rule cannot tell which was meant.

<a id="RTMPE1020"></a>
### RTMPE1020 — OnDestroy must call base.OnDestroy()

`NetworkBehaviour.OnDestroy` releases the object's spawn registration. An
override that does not chain leaks that registration for the lifetime of the
session. Raised only where the chain is legal to write: when the nearest
`OnDestroy` up the base chain is `static`, inaccessible, or `abstract`,
`base.OnDestroy()` does not compile, and no type below has a way to act on the
report — the ancestor that declared it that way is where the chain broke.

**Quick fix — *Call base.OnDestroy()*.** Appends the call as the last statement,
reaching both the `override` and the declaration that merely hides the hook —
Unity dispatches the two identically. Withheld on the shapes it cannot append to
safely; see [Preconditions](#preconditions).

On a declaration that **hides** the hook — the idiomatic `private void
OnDestroy()` a rebased `MonoBehaviour` carries in — the fix is offered as
***Call base.OnDestroy() and override the hook*** and repairs the declaration
too:

```csharp
private void OnDestroy()              →  protected override void OnDestroy()
{                                        {
    Cleanup();                               Cleanup();
}                                            base.OnDestroy();
                                         }
```

🔑 The chain alone was not enough. It is correct at runtime — Unity dispatches
the destroy message by name to the most derived declaration, which then chains —
but as C# the method still hides `protected virtual void OnDestroy()`, so the
compiler reports **`CS0114`** on every build from then on. Before 2.6.1 that
warning was left behind; it is now cleared by the same action.

The accessibility written is the one C# requires of an override — the overridden
member's own, except that a `protected internal` hook reached from an assembly
that is not a friend must be overridden as plain `protected`, because the
internal half is not visible there. Getting either wrong is `CS0507`. Where the nearest `OnDestroy` up the chain is
**not** virtual there is nothing to override (`CS0506`), so the fix chains and
leaves the declaration alone. ⚠️ The compiler's complaint in that shape is
**`CS0108`**, not `CS0114` — `CS0114` is specifically about hiding a *virtual*
member — and it is unavoidable there short of writing `new`; the chain is still
the right thing to do. The declaration is likewise left alone when it already
says `new`: that is the author stating the hide is deliberate, and it suppresses
the warning outright.

The declaration is also left alone — chain only — in four more cases, each
because repairing it would cost more than the warning it clears:

| The declaration | Why it is left as written |
| --- | --- |
| carries `new` | that is you saying the hide is deliberate, and it suppresses the warning outright |
| is `public` or `internal` | an override may not change the accessibility, so repairing it would narrow the method and break every call site outside the type (`CS0122`) |
| carries `[Obsolete]` while the hook does not, or the reverse | `CS0809` / `CS0672` — one permanent warning traded for another |
| carries a comment or a `#region`/`#pragma` between its modifiers | rebuilding the modifier list would delete what you wrote, and a source rewrite never does that |

⚠️ The headless `fix --kind base-ondestroy` verb does **not** repair the
declaration. It is a single-file syntactic host with no view of the base type,
so it cannot prove the override is legal; it appends the chain and prints a note
naming the declaration to correct by hand.

<a id="RTMPE1021"></a>
### RTMPE1021 — Lifecycle-hook name that does not override the hook

The method carries the name of a `NetworkBehaviour` lifecycle hook but a
signature that does not override one, so nothing ever calls it: the method
compiles, reads as wired up, and silently never runs. Four hooks are in scope —
`OnNetworkSpawn()`, `OnNetworkDespawn()`, `OnOwnershipChanged(string, string)`,
and `OnFixedTick(float)`; all return `void`. Correct the signature to match.

**No quick fix** — four hooks are in scope and the rule cannot tell which one the name was
reaching for; correcting the signature toward the wrong one would compile and
still never run.

<a id="RTMPE2001"></a>
### RTMPE2001 — MonoBehaviour with replicable state is a NetworkBehaviour candidate

The type holds plain gameplay state that would need to replicate for the
behaviour to work in multiplayer — an instance field of a replicable value type.
Unity-serialized fields (`public` or `[SerializeField]`) are not that state: the
conversion leaves them as Inspector data. Neither are `static`, `const`, or
`readonly` fields, nor property backing fields.

**Quick fix — *Rebase to NetworkBehaviour*.** Rewrites the base type and adds
the `RTMPE.Core` using directive, through the same transform the Conversion
Wizard drives. Offered only where `MonoBehaviour` is written directly in the
base list; a type that reaches it through an intermediate base is reported but
not rewritten, because the intermediate base is the one to rebase.

<a id="RTMPE2002"></a>
### RTMPE2002 — Field is a NetworkVariable conversion candidate

Two arms with different remedies:

- **In-place** — a private, non-serialized field of a mapped type. Converted to
  a `NetworkVariable` directly. Private-only by design: the transform is scoped
  to one compilation unit, and only a private field provably has every reference
  inside the unit being rewritten.
- **Companion** — a Unity-serialized field that gameplay code writes at
  runtime: config in shape, live state in use. The config field keeps its type
  and a companion `NetworkVariable` is seeded from it. Reported at the end of
  compilation.

Neither arm considers a `static`, `const`, or `readonly` field, a property
backing field, a field already carrying `[NetworkVariable]`, or a field whose
type is outside the replicable set. **A field whose name reads like a credential
— containing `key`, `token`, `secret`, or `password` — is excluded from both
arms outright**, because replicating a secret to every peer is precisely the
outcome a suggestion must never invite.

**Quick fix — *Convert to NetworkVariable* / *Add companion NetworkVariable*.**
Offered wherever the shape allows it; see [Preconditions](#preconditions). No
identity record is consulted — the conversion names the member it is assigned to
and the identity follows from that — so the fix works the same inside Unity as it
does from a repository checkout.

<a id="RTMPE2003"></a>
### RTMPE2003 — Frame loop without an IsOwner guard

Every client runs the frame loops on every replica. Without an owner guard each
one simulates the object independently and their states diverge.

**The rule reports on evidence, not on the absence of a guard.** It fires on a
parameterless, non-static, `void` `Update`, `FixedUpdate` or `LateUpdate` declared on a
`NetworkBehaviour` that does not open with `if (!IsOwner) return;` **and** writes
this object's own
state — its own fields, a `NetworkVariable` it holds, an element of its own
storage, or its own `transform` — directly, through an `out`/`ref` argument, or
through a helper the type declares. A loop that only refreshes a label,
drives an animator or reads a value into the HUD must run on every client, and
the guard is exactly wrong there, so it is silent on those. A loop that
already opens with the guard is silent too, which is what makes applying the fix
converge.

⚠️ **All three loops, judged one at a time.** Physics-driven movement belongs in
`FixedUpdate` and a follow camera in `LateUpdate`, so a rule that watched only
`Update` was silent exactly where owned state is most likely to move. Each loop
is reported on its own evidence, and the message names the one it is about.

**Whether the guard belongs there is still your call**, and there is one shape
where it plainly does not: a loop that *applies* replicated state writes this
object's own transform on every client **except** the owner — usually behind a
test of its own, `if (_locallyOwned) return;`. The rule reports it, because the
write it saw is real; the guard would fence the complementary frames and leave
the body running for nobody. ⛔ **The quick fix cannot recognise that shape** —
`_locallyOwned` is a name only you can read — so it is offered there and the
judgement is yours. What it does refuse is the partition it *can* read: a
leading test of `IsOwner` that branches instead of exiting, where fencing above
it would strand the branch written for non-owners. See
[Preconditions](#preconditions).

⚠️ The readiness score's **Ownership** dimension faults that loop as well, for
the same reason and with the same blindness — it reads what a loop writes, never
why. A replica-side apply scores 80 and stays there; the number is stating what
it measured.

⛔ What is **not** evidence, so an `Update` doing only these is silent: input
polling on its own (a frame that samples has decided nothing until it writes);
and any write reaching **through** a component the type holds —
`_animator.SetBool(…)` and `_rb.velocity = v` are one shape to a syntax pass, so
a loop driving physics through a cached `Rigidbody`, or its transform through a
cached `Transform`, is not reported.

**Quick fix — *Add the IsOwner guard*.** Opens the method with
`if (!IsOwner) return;`. Withheld on the shapes it cannot insert into safely;
see [Preconditions](#preconditions).

<a id="RTMPE2004"></a>
### RTMPE2004 — Owner-guarded mutation is an Enhanced-RPC candidate

An owner-guarded state mutation is the shape an Enhanced-RPC replaces. This is
detection only: the owner guard is necessary but not sufficient evidence, so
nothing is rewritten until a human designates the method.

Both rules read the body; this one's filter is much the narrower of the two, and
that is usually the answer to "why was my method not flagged?". It requires a
`public`, non-static, non-abstract, non-`override`, non-generic, non-`partial`,
`void` method with a statement body, on a concrete `NetworkBehaviour` class; not
already `[RtmpeRpc]`; every parameter RPC-serializable and free of
`ref`/`out`/`in`, `params`, and default values; a body that opens with the owner
guard, mutates instance state, and does not already send. Unity lifecycle names
and credential-shaped method names are excluded outright.

**No quick fix** — deliberately. The conversion needs an audience, and for a
method this rule flags, **all three are wrong by default**, which is why there
is nobody a one-click action could decide on behalf of.

The rule fires only on a body that opens with `if (!IsOwner) return;`, and the
conversion keeps that guard. So: `Server` runs the method only inside a backend
handler bound to its method id, and this deployment binds none. `Others`
excludes the sender and every receiver is a non-owner, so the guarded body runs
on **no client at all**. `All` reaches the owner too — and the owner is the only
one the guard admits, so a direct local call becomes the same call after a
server round-trip. Each is a different answer to a design question about the
method, not a setting.

⛔ And the lightbulb cleared this diagnostic as it went, which is what made it
worse than nothing: the author was left with working code replaced by dead code
and no signal that anything was still owed. Convert through `make gen-rpc` or
the Conversion Wizard: the audience is stated before you choose, and the runtime
reach of the one you chose is reported on the diff.

⚠️ Both need the conversion engine, so a Unity project that has not built the
copy under `Automation~/` has no automated route for this rule — see
[The path that needs no IDE](#the-path-that-needs-no-ide). Applying the shape by
hand is the remaining option, and it was the honest one all along: the lightbulb
this rule used to carry produced a call that ran on no node.

<a id="RTMPE2005"></a>
### RTMPE2005 — Auto-property holds replicable state no conversion can retype

`public int Score { get; set; }` holds its value in a field the **compiler**
wrote. Every conversion in this set works by retyping a field the author
declared and rewriting the references to it; a compiler-generated field can be
neither retyped nor named, so the whole conversion set passes over an
auto-property — and until this rule existed it did so in silence, which read
like a judgement that the member was not worth replicating.

The rule fires on a settable, non-static auto-property of a mapped type on a
concrete `NetworkBehaviour`, and names the wrapper the map already carries for
that type.

**The edit keeps the property.** Its callers never learn anything changed:

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

⚠️ The variable is constructed in `OnNetworkSpawn`, not in a field initializer —
`RTMPE1011` states that rule and applies here unchanged.

⛔ **The property does not behave identically afterwards, and the differences are
on the write side as well as the read.** They were measured against the runtime,
not reasoned:

| | Auto-property | Behind a NetworkVariable |
| --- | --- | --- |
| Read before spawn | returns `default` | `NullReferenceException` — there is no variable yet |
| Write on a client that does not own the object | stored | **refused**, with a rate-gated warning (`NetworkVariable.Value` → `RefuseUnownedWrite`) |
| Write after `OnNetworkDespawn` | stored | **refused** |
| Write of an unsendable value (`NaN`, a zero quaternion) | stored | **refused** |

The refusals are correct — a value stored on a non-owner is announced locally and
sent to nobody, diverging permanently — but they are a change to what the member
does, and the second row is the one that gets discovered as a bug. If callers
must write from any client, send the owner an RPC or request ownership; if they
must read before spawn, keep a plain backing field and seed the variable from it,
which is the companion shape.

⚠️ **Carry the initializer.** `public int Score { get; set; } = 100;` cannot keep
that initializer once the property has accessor bodies — C# allows one only on an
auto-property (`CS8050`) — so the seed moves into the constructor:
`new NetworkVariableInt(this, nameof(_score), 100)`. Dropping it changes the
component's starting value with nothing to say so.

⚠️ **`NetworkVariableQuaternion` needs its identity seed.** `default(Quaternion)`
is `(0,0,0,0)`, not a rotation, so the construction is
`new NetworkVariableQuaternion(this, nameof(_rot), Quaternion.identity)` —
`RTMPE1012` says the same thing and will fire on the un-seeded form.

⚠️ **A name spent twice in one hierarchy is a fatal identity collision.** A
variable's wire id is derived from the concrete type and the name it is
constructed with, so a base and a derived class that both apply this remedy to a
property called `Score` — each with `nameof(_score)` — derive **one** identity on
an instance of the derived type, and `RTMPE1010` reports it as an error:
constructing both throws out of `OnNetworkSpawn` and the object is destroyed
rather than spawned. Give the two backing variables different names. ⛔ Unlike
[`RTMPE2002`](#RTMPE2002), whose fix refuses that pair rather than emitting it,
this remedy is applied by hand and nothing refuses it for you.

**No quick fix** — deliberately. The property's accessors are the author's own
code, an accessor may hold logic the conversion cannot preserve, and the read
that is now spawn-dependent is a decision about the type rather than an edit to
it.

⛔ A `[field: SerializeField]` auto-property is outside this rule: its value is
Inspector data, and retyping or shadowing it discards what the scene assigned.
That shape takes the companion arm of [`RTMPE2002`](#RTMPE2002) instead — a
separate variable seeded from the serialized value.

<a id="RTMPE9001"></a>
### RTMPE9001 — Type authority classification (advisory)

Reports the authority posture a deterministic rubric infers from the type's own
static signals — base type, replicated state, owner guards, RPC surface,
lifecycle overrides, scene wiring — as one of `Authoritative`,
`OwnerPartitioned`, `Presentation`, `Orchestrator`, or `Undetermined`. Purely
advisory: it changes no source,
transfers no ownership, and promises no enforcement the
client-authoritative-with-relay runtime does not perform. The same classifier
feeds the readiness report, so the IDE and the CI artifact cannot disagree.

**No quick fix** — the rule is advisory and reports no defect to repair.

---

<a id="RTMPE1022"></a>
### RTMPE1022 — OnNetworkSpawn must call base.OnNetworkSpawn()

A `NetworkBehaviour` builds its `NetworkVariable`s in `OnNetworkSpawn`. An
override that does not chain skips the one its ancestor declares, so every
variable that ancestor builds stays `null` and the object throws on its first
frame — on every client, deterministically.

⛔ **Raised only where an ancestor actually overrides the hook.**
`NetworkBehaviour`'s own `OnNetworkSpawn` is empty, so a type deriving straight
from it skips nothing: that shape is not reported at all. This is what makes an
`Error` safe here rather than a policy argument — in the projects where not
chaining costs nothing today, the rule says nothing, and where it fires the cost
is a crash.

The message names the override that will not run:

```
OnNetworkSpawn on 'TurretController' does not call base.OnNetworkSpawn(),
so 'WeaponBase.OnNetworkSpawn' never runs and the NetworkVariables it builds
stay null
```

```csharp
protected override void OnNetworkSpawn()      protected override void OnNetworkSpawn()
{                                         →   {
    _ammo = new NetworkVariableInt(            base.OnNetworkSpawn();
        this, nameof(_ammo));                  _ammo = new NetworkVariableInt(
}                                                  this, nameof(_ammo));
                                              }
```

⚠️ A call inside a lambda or a local function does not count as chaining: it runs
only if that body is invoked, which the analyzer cannot establish. Put the call in
the method body — first, so the ancestor's variables exist before yours are built.

**No quick fix** — where the call belongs is a question about your code, not about
the syntax. `RTMPE1020` can append `base.OnDestroy()` as the last statement because
teardown order is teardown order; here the ancestor's variables must exist *before*
the ones this override builds read or wrap them, and only the author knows whether
anything above the insertion point already depends on them.

---

## Changing a rule's severity

RTMPE rules carry no custom configuration surface — they respond to the
standard `.editorconfig` severity keys, which every Roslyn host honours:

```ini
[*.cs]
# Promote the conversion opportunities from Info to Warning so they surface in
# the Problems pane and in CI output.
dotnet_diagnostic.RTMPE2001.severity = warning
dotnet_diagnostic.RTMPE2003.severity = warning

# Silence an advisory that does not apply to this project.
dotnet_diagnostic.RTMPE9001.severity = none
```

Severity governs how loudly — or whether — a rule is reported. It never changes
whether the rule has a quick fix: that is a property of the rule, listed in the
index above, and promoting a rule to `warning` will not give it a lightbulb it
does not have.

---

*RTMPE SDK 1.0.5*
