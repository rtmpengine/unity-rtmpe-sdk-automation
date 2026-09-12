# Enhanced-RPC Generation

The second identity-bearing conversion layer: turn an owner-guarded,
state-mutating handler on a `NetworkBehaviour` into a dispatchable
Enhanced-RPC — `[RtmpeRpc(RpcTarget.X)]` on the method, every intra-type call
site rewritten to `this.RPC("Method", args)`, and the id's provenance recorded
in the type's `<Type>.rtmpe-ids.json` ledger — without a human ever typing an
id or the machine ever choosing an audience for a state change.

## The one fact everything rests on

An Enhanced-RPC method id is **derived, not issued**:

```
methodId = FNV-1a-32( utf8(Type.FullName) ++ '.' ++ utf8(MethodName) )
```

with basis `2166136261` and prime `16777619`, exactly as
`RpcRegistry.ComputeMethodId` computes it at first spawn. There is no
free-list, no allocation, and no way to "burn" an id — a re-added method of
the same name re-derives the same id. Two consequences shape the whole design:

- **Issue and re-adopt are the same idempotent operation.** The ledger's
  `rpcs` map (`"MethodName": "0xHHHHHHHH"`) is *provenance*, recorded so a
  type rename or hand-edit is caught fail-closed as the wire break it is —
  never numbering.
- **One name derivation, used for both.** The id, the ledger filename and the
  binding key all name the type by its **full** metadata name (namespace +
  nested `+` + arity) — the `Type.FullName` the runtime hashes. Two types of
  one short name in different namespaces therefore reach different ids, which
  is what lets both be mounted on one object. The `unity-sdk-rpc` shard pins the
  tooling's `Fnv1a` byte-for-byte against the **real compiled runtime**
  (`ToolingRuntimeDifferentialTests`), including non-ASCII and lone-surrogate
  names, and pins the `RpcAudience` mirror against the runtime `RpcTarget`
  names and wire bytes.

## Detection — `RTMPE2004` (suggestion only, never a rewrite)

`ConversionOpportunityAnalyzer` surfaces an Info-level candidate when **all**
hold: public ordinary instance method (non-static/abstract/override/generic,
void, no lifecycle name, no secret-like name) on a concrete `NetworkBehaviour`;
every parameter inside the serializer's closed set; a **leading owner guard**
(`if (!IsOwner) return;`, disjunctions like `!IsOwner || x == null` included);
a write to instance state; and **no existing send** (`SendRpc` /
`SendEnhancedRpc` / `RPC(`) in the body. The real shipping samples pin the
exact set: `FPSController.TakeDamage`, `HealthController.TakeDamage`,
`HealthController.Respawn`, `PlayerController.AddScore` — and nothing else
(`ReceiveApplyDamage` is unguarded *by design* and must never be suggested).

## The headless host

```
make gen-rpc FILE=Samples/.../HealthController.cs TYPE=RTMPE.Samples.SimpleFPS.HealthController \
             METHOD=Respawn:Server [APPLY=1]
```

Pipeline: audience policy → `PlanGuard` over the **union** of the type's RPC
surface (every method already carrying `[RtmpeRpc]`, one entry per annotated
declaration, plus the designated ones) → `RpcIdRecorder` into the ledger →
`RpcGenerationTransform` → unified diff → atomic **ledger-first** apply behind
the same symlink/TOCTOU guards as `make convert`. Exit codes match the NV
host: `0` ok/no-op, `1` usage, `2` guard/ledger verdict, `3` transform
refusal, `4` apply-time safety stop.

### Audience policy (the security spine)

The runtime gateway authenticates the **sender**, but nothing binds a caller
to the object it targets — a broadcast RPC that mutates state is therefore a
client-authoritative write any room member can invoke (audit finding M3).
The policy is mechanical:

| Situation | Outcome |
|---|---|
| `--method Name:Server` | honored — the fail-closed choice (a client never executes a Server-declared method) |
| `--method Name` on a **mutating** method | **refused** (exit 2) — the audience must be designated explicitly |
| `--method Name` on a non-mutating method | defaults to `Server`, with a printed note |
| `--method Name:All/Others` on a mutating method **without** a leading owner guard | **transform refuses** — every receiving client would apply the write |
| `--method Name:AllBuffered` | honored only because it was spelled out; the persistence/late-join-replay cost is printed |

The transform writes the enum literal; the human approves it in the diff.
Nothing anywhere infers a broadcast for a mutation.

The `IsOwner` guard is trusted **by spelling**, so it is only recognised when
the name is not shadowed: a method **parameter** named `IsOwner` is a
wire-supplied argument, and a **member** named `IsOwner` shadows the SDK
property — either makes the guard untrustworthy, and the mutating-broadcast
refusal fires instead.

### What "Server" (and any guarded broadcast) actually does at runtime

A generated RPC is dispatched, not a local call — so the destination decides
whether the body runs anywhere, and the CLI **prints the consequence** for each:

- **`Server`** is consumed by a **backend** handler you must register
  server-side (`RegisterServerRpc`); with none registered the send resolves to
  "unknown method" and the body runs on **no node**. The direct call that was
  rewritten into `this.RPC(...)` no longer runs locally either. Convert to
  `Server` only when you are also writing the server handler.
- **Guarded `Others`** excludes the sender, and every receiver is a non-owner,
  so the `if (!IsOwner) return;` body runs on **no client** — a no-op.
- **Guarded `All`** runs the body only on the owning sender, after a server
  round-trip — a delayed self-echo of what the deleted local call did instantly.

These are notes, not refusals: the shapes are legal, but the tool tells you
what you are getting.

### There is no IDE quick-fix, and that is the design

`RTMPE2004` carries no lightbulb. It had one — a single action that emitted
`[RtmpeRpc(RpcTarget.Server)]` and rewrote the call sites — and the reason it is
gone is the audience.

⛔ **An audience is not a detail a one-click action can decide**, because for a
method this rule flags all three are wrong by default. `RTMPE2004` requires a
body opening with `if (!IsOwner) return;` and the conversion keeps that guard,
so the three answers are: `Server` runs only in a backend handler bound to its
method id, and this deployment binds none; `Others` excludes the sender and
every receiver is a non-owner, so the guarded body runs on no client; `All`
reaches the owner, the only peer the guard admits, turning a direct local call
into the same call after a server round-trip. The table two sections up prints
exactly these three notes, and each is a design question about the method rather
than a setting.

🔑 The quick fix also **cleared the diagnostic** as it went, which is what made
it worse than no fix: the author was left with working code replaced by dead
code, and the one signal pointing at the decision removed by the same click. The
`Info` rule stays, and both surfaces that convert — `make gen-rpc` and the
Conversion Wizard — ask which audience is wanted and state what each one reaches
before the choice is made.

### Transform guarantees

Mirrors the NV transform's discipline: typed `SyntaxFactory` factories over
original tokens only, batch `ReplaceNodes`, refuse-whole with a
human-actionable reason (never a partial edit), byte-identical no-op re-runs,
trivia/indent fidelity (doc comments stay above the inserted attribute), and
`using RTMPE.Rpc;` added only when absent. The refuse surface covers: partial
types, non-parsing trees, missing/overloaded/static/non-public/generic/
non-void methods, ref/out/params/default parameters, parameters outside the
closed set (`INetworkSerializable` implementers are refused — an unregistered
one arrives null, so annotate those by hand with their
`RpcTypeRegistry.Register<T>()`), recursion, named-argument or
receiver-qualified or `base.` call sites, method-group references, references
outside the type or in nested types, directive trivia on the method or a call
site, the name appearing in **disabled** `#if` text (an invisible call site),
a comment inside a call's argument list (the send rebuilds that list, so the
comment would be silently deleted), a member named `RPC` on the type or on a
base declared in the same file (the emitted send would bind to it and carry
nothing over the wire), and the unguarded broadcast mutator above.

## Known limitations of the syntax-only engine

The transform reads one compilation unit with no semantic model, so a few
correctness questions it cannot answer are handled by **refusing** or by a
printed **caution**, never by guessing:

- **Only the call sites the pass rewrites carry the conversion.** `RPC(string,
  params object[])` boxes each argument by its own static type and the receiver
  binds on that boxed type, so a direct call's implicit widening
  (`byte`/`short`/`char`/`enum` to `int`, `int`/`uint` to `ulong`) would be lost
  and the call refused at dispatch. Every call site the transform rewrites
  therefore restates the declared parameter type as an explicit cast, and needs
  no manual one. What it cannot reach is a call site it never rewrote — one in
  another file, since the pass reads a single compilation unit, or a
  `this.RPC(...)` written by hand afterwards. Those still box by static type and
  **fail to serialize at send** when the argument is narrower; cast to the
  parameter type there. The CLI **cautions** whenever a converted method takes
  arguments, because it cannot see whether such a call site exists.
- **A base type in another file is not seen.** Same-named inherited methods
  (overload ambiguity) and inherited mutated fields (audience-gate input) are
  detected only when the base is declared in the **same file**; a cross-file
  base is a blind spot the runtime's own spawn-time collision throw still
  backstops for ids, but not for the mutation gate. Same-name shadows and
  in-file base overloads **refuse**.
- **Disabled `#if` text is matched literally.** A call site under an inactive
  `#if` refuses the plan when its text contains the method name; a
  unicode-escaped spelling (`Respawn`) can slip that literal match. Prefer
  converting with the relevant `#if` symbols defined.

## What this generator deliberately does not do

- **No auto-rewrite from detection.** RTMPE2004 is a suggestion; the engine
  runs only on an explicitly designated method (CLI target or lightbulb click).
- **No `INetworkSerializable` parameter emission.** Refused with the manual
  path spelled out (registration is an authorship decision).
- **No wizard hosting yet.** The Unity-editor shell inherits the same host
  contract as the NV engine (one diff, both files, atomic per type).
- **No wire change of any kind.** The generator emits *source that uses* the
  existing contract; packet formats, FlatBuffers, crypto, and ports are
  untouched.

## Test map

| Shard | What it pins |
|---|---|
| `unity-sdk-rpc` / `ToolingRuntimeDifferentialTests` | tooling FNV == **real** `RpcRegistry.ComputeMethodId` (ASCII/generic/nested/non-ASCII/surrogate); `RpcAudience` == `RpcTarget` names+bytes |
| `unity-sdk-analyzers` / `RpcCandidateDetectionTests` | RTMPE2004 positives, per-leg negatives, exact real-sample set |
| `unity-sdk-analyzers` / `Fnv1aGuardTests` | golden vectors, reserved set == runtime constants, `PlanGuard` reserved/duplicate classification (reserved precedence) |
| `unity-sdk-conversion-golden` / `RpcGenerationTransformTests` | byte-exact before→after goldens, idempotent re-run, full refuse matrix |
| `unity-sdk-conversion-golden` / `RpcIdRecorderTests` | idempotent record, fail-closed mismatch, vanish warning, canonical `rpcs` serialization + round-trip |
| `unity-sdk-conversion-golden` / `RpcCliTests` | end-to-end diff/apply/no-op, audience refusals, union collision exit 2, symlink stop, NV-ledger coexistence |
| `unity-sdk-codefixes` / `CodeFixProviderTests` | no provider in the assembly claims RTMPE2004, the rule still fires, and its message recommends no audience |
