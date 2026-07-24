# RTMPE SDK — Analyzer Rule Reference

> SDK Version: `com.rtmpe.sdk 2.0.11`

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
| RTMPE2001 | RTMPE.Conversion | Info | Rebase to NetworkBehaviour |
| RTMPE2002 | RTMPE.Conversion | Info | Convert to NetworkVariable (or add a companion) |
| RTMPE2003 | RTMPE.Conversion | Info | Add the IsOwner guard |
| RTMPE2004 | RTMPE.Conversion | Info | Convert to Server Enhanced-RPC |
| RTMPE9001 | RTMPE.Authority | Info | none |

Five rules carry a fix. All but `RTMPE1020` are **Info** severity, which most
editors render as a faint hint rather than a squiggle — see
[Finding the Info-severity rules](#finding-the-info-severity-rules) if you have
never seen one.

---

## The lightbulb is missing — read in this order

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
  analysis scope is open files only, so these two do not appear until
  `dotnet.backgroundAnalysis.analyzerDiagnosticsScope` is set to
  `fullSolution`. Rider and Visual Studio have the equivalent setting under
  solution-wide analysis.

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

| Rule | Withheld when |
| --- | --- |
| `RTMPE1020` | The declaration is a shape the call cannot be appended to: an expression body (`=> …`) or no body at all; a body written entirely on one line, which has no line structure to extend; a `static` or value-returning method of that name, which is not the Unity message and could not host the call; or a body carrying `#if` boundaries, where an appended call could land inside a conditional region and make the release conditional with it. |
| `RTMPE2001` | The type reaches `MonoBehaviour` through an intermediate base class. The rule fires on any transitive subclass, but the rebase only swaps a `MonoBehaviour` written directly in the base list — for a class hierarchy the correct edit is to rebase the intermediate base, so the fix defers rather than rewrite the wrong type. |
| `RTMPE2002` | Two independent gates. **Identity** — no `<Type>.rtmpe-ids.json` ledger reaches the compiler as an additional file, or the one that does has issued no id for this member; the lightbulb never allocates a wire id, because allocation belongs to the ledger-owning host. **Nothing in a stock Unity project puts the ledger on that channel, and the SDK ships no wiring that does**, so inside Unity this fix is expected to be absent — use the Conversion Wizard. **Shape** — the enclosing type is `partial` or does not parse; an existing `OnNetworkSpawn` does not override the runtime hook, is expression-bodied, has a single-line body, or sits in a conditional-compilation region; the field's initializer is outside the allowed set (literals, `default`, the well-known pure statics); a reference to it is ambiguous (shadowed, reached through a receiver other than `this`, or from outside the declaring type) or sits in a pre-spawn context — `Awake`, `Start`, `OnEnable`, `OnValidate`, `Reset`, a constructor, another field initializer — where the variable is still `null`; the member is used somewhere that needs a storage location rather than a value — passed by `ref`/`out`, bound as a `ref` local or `ref` return, or with its address taken — since the converted member is read through a property; or the companion name collides with an existing member, a nested type or delegate, or the enclosing type's own name. |
| `RTMPE2003` | The same shapes as `RTMPE1020` — expression-bodied, bodiless, single-line, `static`/value-returning, or `#if`-bearing — plus an empty body, which has nothing to fence, and a type or body that already declares the name `IsOwner`, where the inserted guard would read that member or local instead of the inherited ownership flag. |
| `RTMPE2004` | The rewrite cannot be proven safe from this one file alone. This is the widest set of the five, and it spans three levels: the enclosing type (does not parse, is `partial`, declares no base type), the declaration (`static`/`abstract`/non-public/generic/value-returning, a parameter carrying a modifier or a default, a parameter type outside the serializer's closed set, `#if` trivia), and every call site (a shadowed name, the same name on a base type, a call through `base` or through a receiver other than `this`, a reference from outside the declaring type or inside a nested type, a method-group reference, recursion, named or `ref`/`out` arguments, a partial argument list, `#if` trivia, a comment inside the argument list — the send rebuilds that list and the comment would be deleted). A member named `RPC` on the type, or on a base declared in the same file, refuses the whole plan: the emitted `this.RPC(…)` would bind to it rather than to the SDK's send. |

### The path that needs no IDE

**Window → RTMPE → Conversion Wizard** runs the same
`RTMPE.SDK.Transforms` core out of process and produces byte-identical output.
It is a full-capability alternative to the lightbulb, not a degraded mode, and
it is the supported route for `RTMPE2002` inside Unity.

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
noting that an implementer also needs a hand-written
`RpcTypeRegistry.Register<T>()` before it can actually round-trip, which is why
the conversion tooling declines to generate for one.

**No quick fix** — the remedy is a different parameter type or a different payload shape,
and only the call sites can say which.

<a id="RTMPE1003"></a>
### RTMPE1003 — RPC methods share a method id

Method ids are derived deterministically from the declaring type and method name
(an FNV-1a hash of `TypeName.MethodName`), and the receiver dispatches on the id
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
### RTMPE1010 — NetworkVariables share a variableId

Inbound updates dispatch by `variableId`, so two variables sharing one id means
only the first is ever updated. Give each construction a distinct `variableId`
argument. Compared across constructions this object owns whose id is a compile-time
constant, and reported at the end of compilation. Two constructions in opposite
arms of one conditional — the two sides of an `if`/`else` or `?:`, or different
`switch` sections — are one variable selected at runtime rather than a collision,
and are not reported; sections a `goto` can chain together are, because both then
run. See
[whole-solution rules](#finding-the-info-severity-rules) if it does not appear in
your editor.

**No quick fix** — renumbering a variable that has already shipped breaks the wire for
builds already in the field. `make repair` and `make retire` exist to make that
decision explicit and record it in the ledger.

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
wrong rotation.

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
Offered only when a ledger has already issued this member's wire id; see
[Preconditions](#preconditions). In Unity, run the Conversion Wizard instead —
it owns the ledger.

<a id="RTMPE2003"></a>
### RTMPE2003 — Update without an IsOwner guard

Every client runs `Update` on every replica. Without an owner guard each one
simulates the object independently and their states diverge.

**The rule does not read the body.** It fires on any parameterless, non-static,
`void` `Update()` declared on a `NetworkBehaviour` that does not open with
`if (!IsOwner) return;` — so a render-only or empty `Update` is reported too,
and whether the guard belongs there is your call. An `Update` that already opens
with the guard is silent, which is what makes applying the fix converge.

**Quick fix — *Add the IsOwner guard*.** Opens the method with
`if (!IsOwner) return;`. Withheld on the shapes it cannot insert into safely;
see [Preconditions](#preconditions).

<a id="RTMPE2004"></a>
### RTMPE2004 — Owner-guarded mutation is an Enhanced-RPC candidate

An owner-guarded state mutation is the shape an Enhanced-RPC replaces. This is
detection only: the owner guard is necessary but not sufficient evidence, so
nothing is rewritten until a human designates the method.

Unlike `RTMPE2003`, this rule **does** read the body, and its filter is narrow —
which is usually the answer to "why was my method not flagged?". It requires a
`public`, non-static, non-abstract, non-`override`, non-generic, non-`partial`,
`void` method with a statement body, on a concrete `NetworkBehaviour` class; not
already `[RtmpeRpc]`; every parameter RPC-serializable and free of
`ref`/`out`/`in`, `params`, and default values; a body that opens with the owner
guard, mutates instance state, and does not already send. Unity lifecycle names
and credential-shaped method names are excluded outright.

**Quick fix — *Convert to Server Enhanced-RPC*.** Emits
`[RtmpeRpc(RpcTarget.Server)]` and rewrites intra-type call sites. The
lightbulb only ever offers **Server** — a wrong audience then fails closed to a
no-op rather than becoming a client-authoritative broadcast. Broadcast
audiences are chosen explicitly through `make gen-rpc`.

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

*RTMPE SDK 2.0.11*
