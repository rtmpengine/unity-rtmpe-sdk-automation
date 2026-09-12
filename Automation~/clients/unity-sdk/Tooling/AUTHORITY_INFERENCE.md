# Dependency Graph & Authority Inference

The first **whole-project** layer of the toolchain: a deterministic dependency
graph over the project's component types, and a documented, reproducible rubric
that classifies each one's *authority posture* — surfaced as advice a human
reads, never as a rewrite. It changes **no source, no ownership, no wire
byte, and no identity ledger**; its entire output is the readiness report's
authority block and one informational diagnostic.

## The honest premise everything rests on

The RTMPE runtime is **client-authoritative-with-relay**. `IsOwner` is a local
string-UUID comparison; there is no `IsServer`. The one authority surface the
runtime genuinely enforces is **`NetworkVariable` owner-writes** (the client
flush gate plus the gateway's `object_authority_ok` check). `RpcTarget.Server`
routes to a backend registry that is **empty by default** — an unregistered
Server RPC resolves to `RpcErrorUnknownMethod` and runs nowhere. Authority
inference can therefore recommend *placement and guards*; it cannot promise
enforcement the runtime does not perform, and every recommendation it emits
says so explicitly.

## Roles

| Role | Meaning |
|---|---|
| `Authoritative` | a `NetworkBehaviour` holding replicated `NetworkVariable` state — the runtime-enforced owner-write surface |
| `OwnerPartitioned` | a `NetworkBehaviour` whose logic is split by ownership (owner guards, an RPC surface, or lifecycle hooks) without replicated state of its own |
| `Presentation` | a plain `MonoBehaviour` leaf: no authority surface, no scene-object wiring — it may hold and observe an Inspector-injected reference |
| `Orchestrator` | a plain `MonoBehaviour` that wires scene objects (assigns component members, instantiates, looks components up) — bootstrap, not authority |
| `Undetermined` | the explicit fallback when signals conflict or are absent — the classifier never guesses |

## The rubric (fixed, first match wins)

Signals are **intrinsic** — extracted by `AuthoritySignalExtractor` from the
type's own symbol and syntax together with those of its **project-declared
bases**, up to (not including) `NetworkBehaviour`/`MonoBehaviour`: an inherited
guard, RPC, lifecycle override or wiring counts exactly as a locally declared
one does, so a bare-looking leaf can still earn an authority verdict. (A nested
type is its own node; its syntax never leaks into the enclosing type's signals.) **Type identity is always semantic**
(`GetTypeByMetadataName` + symbol equality — the base classes, `NetworkVariable`
member types, the `[RtmpeRpc]` attribute, `GameObject`, and the owner-guard's
`IsOwner` binding); the **call-name families are syntactic by design**, matching
the conversion analyzer's own send detection: `SendRpc`/`SendEnhancedRpc`/`RPC(`
invocations, the `GetComponent`/`AddComponent`/`Instantiate` acquisition family,
the lifecycle override names, and `RpcMethodId` in its type-qualifier shape
(`RpcMethodId.X` — a bare same-named local or `nameof` operand is not a
surface). The signal set: NetworkBehaviour base, `NetworkVariable` member count,
an owner-guarded method (any spelling `OwnerGuardSyntax` accepts, disjunctions
included), an RPC surface (**both** shapes — legacy `SendRpc`/`RpcMethodId`
and enhanced `[RtmpeRpc]`/`RPC(`, because the shipping samples use only the
legacy one), network lifecycle overrides, and scene-object wiring.

1. NetworkBehaviour + replicated state → `Authoritative`
2. NetworkBehaviour + (owner guard | RPC surface | lifecycle override) → `OwnerPartitioned`
3. NetworkBehaviour, bare → `Undetermined` (points at RTMPE2002/2003/2004/2005)
4. non-NetworkBehaviour with networking signals → `Undetermined` (points at RTMPE2001 — the runtime discovers none of them there)
5. non-NetworkBehaviour that moves a transform → `Undetermined` (a mover is not presentation)
6. non-NetworkBehaviour that wires scene objects → `Orchestrator`
7. anything else → `Presentation`

### The unreplicated-motion reading

Rules 2, 3 and 5 additionally carry it, on one predicate, when the type writes a
transform and holds **no carrier** — no replicated state, no RPC surface — and is
not itself a motion replicator nor requires one through `[RequireComponent]`.

⛔ An owner guard and a lifecycle override buy no silence: they decide who moves
and when, never whether the result leaves the machine.  A rule that let them
silence it would delete the advisory at exactly the step rule 3 asks the author to
take, which is the defect this reading was added to close.

🔑 It is worded as a **reading**, never as a fact about the object, because two
carriers are outside what the rubric binds: a **private** member of a precompiled
base, and a write into a component this type does not declare.  A sentence of the
form "nothing replicates this" would be false in both.

⚠️ Three limits are known and deliberate, and each is the quiet direction rather
than the safe one:

- **Rule 1 outranks it.** A mover carrying *any* `NetworkVariable` — a score, a
  name — is claimed as `Authoritative` before the predicate runs, so its
  unreplicated motion is not reported.  The signal counts variables, not their
  types.
- **The replicator is matched by name**, so a project type called
  `NetworkTransform` silences the reading.  A semantic match was not available:
  the SDK assembly is absent from the contract every headless score compiles
  against, so symbol comparison answers "no replicator" for every project the CLI
  scores.
- **`[RequireComponent]` is a statement about attachment, not about existing
  objects.**  Unity acts when a script is added and when a requirement is removed;
  it audits no prefab already carrying the script.  The suppression is right for
  everything built after the declaration and optimistic for anything built before.

⛔ Where it surfaces is narrower than the other recommendations: at rules 2 and 5
the role is not `Undetermined`, so the Authority dimension is cleared and the
sentence reaches the report's advisory block without a to-do row beside it.

**Connectivity never outweighs the intrinsic signals.** The samples'
`GameManager` is the pinned counterexample: a high-fan-out `MonoBehaviour`
driving the whole SDK (connect, create room, spawn) with zero
`NetworkVariable`/`IsOwner`/RPC — a fan-in/fan-out heuristic would call it
authoritative; the rubric classifies it `Orchestrator`.

**Wiring vs observation** is what splits `Orchestrator` from `Presentation`:
assigning **another script** reference (a `MonoBehaviour`/`NetworkBehaviour`
member — the clear cross-object-orchestration signal), holding a `GameObject`
member (the type itself, an array, or a generic collection of it), or acquiring
components in code (`GetComponent`/`Instantiate`/…) is wiring; an
Inspector-injected reference that is only read or subscribed to is observation,
so a HUD that watches a manager stays `Presentation`. Caching one's own
`Transform`/`Camera`/`Rigidbody` (a plain `Component`, not a `MonoBehaviour`) is
presentation infrastructure — indistinguishable from `_t = transform` — and is
deliberately **not** counted as wiring, so a HUD is never mislabelled an
orchestrator for caching its transform.

## Known limitations of the extraction (advisory-safe, each deliberate)

- **One preprocessor configuration at a time.** A type inside an inactive `#if`
  is not in the syntax tree, so it yields no signals and no verdict at all —
  hiding a component behind a platform symbol erases its role rather than
  downgrading it. No symbol set makes every region active, so the headless host
  takes `--define <SYMBOL>` and warns per file when a region is inactive under
  the set it was given; the editor's generated csproj supplies the project's
  symbols.
- **Cross-assembly `private` members are invisible.** Roslyn imports only
  public/protected members from referenced assemblies, so a leaf whose
  replicated state lives entirely in a precompiled base's private
  `NetworkVariable` fields degrades toward `Undetermined`/`OwnerPartitioned` —
  the honest "I don't know", never a wrong `Authoritative`.
- **The syntactic name families can be spoofed by same-named user methods**
  (a leftover shim named `RPC`, a helper named `Instantiate`) — the same
  deliberate trade the conversion analyzer makes; the verdict is Info-level
  advice a human reads.
- **Evidence counts members, not variables:** a field plus a hand-written
  wrapper property of the same `NetworkVariable` type counts twice in the
  evidence line (the role threshold is unaffected).
- **Wiring keys on source assignments of a script reference,** so a
  hand-written setter that assigns a `MonoBehaviour` member is wiring while the
  equivalent auto-property is not, and a field *initializer* (`= …`) is not an
  assignment (its acquisition call, when any, is still caught by the invocation
  family). A nested `enum`/`class`/`struct`/`record`'s syntax is its own node's
  and never leaks into the enclosing type's signals.

## The dependency graph (code-reference-only, by design)

`DependencyGraphBuilder.Build(Compilation)` — nodes are the compilation's
concrete source component types; edges are `[RequireComponent(typeof(T))]`,
component-typed members, `GetComponent`-family lookups (the generic type
argument resolves even when the invocation itself does not bind), and
reads/invocations/writes through node-typed expressions. A write is read at the
end of the member chain the node-typed access heads, so the SDK's own spelling
of a cross-object write — `other.Variable.Value = x`, assignment and compound
and `++`/`--` alike — classifies as `Write` rather than as a read of
`other.Variable`; an invocation is read at the access itself, so a call further
along the chain (`other.Variable.Value.ToString()`) stays observation. `+=`/`-=`
is a subscription only when its target binds to an event. SDK and Unity types
are *signals on* a node, never nodes — so the graph describes the developer's
own architecture.

**Unity scene/prefab references are deliberately out of scope** (plan DD-P5-2):
the repository's sample scenes carry no `MonoBehaviour` blocks, no `m_Script`
guids, and no committed prefabs, so scene→script resolution has no in-repo
ground truth. If it is ever added, it requires a fresh-authored
scene/prefab/`.cs.meta` fixture and treats the YAML as untrusted input.

## Where the verdicts surface

One pure function — `AuthorityClassifier.Classify` in `Conversion.Core`, no
Roslyn, no I/O — feeds all three surfaces, so they cannot disagree:

- **`RTMPE9001`** (Info, `AuthorityInfoAnalyzer`): one advisory diagnostic per
  component type, carrying the role and its evidence, in the IDE and the Unity
  console. It is classification, not a fault; nothing pairs it with a code fix.
- **The readiness report's authority block** (`ReadinessReport.Authority`):
  every graph node — `MonoBehaviour` orchestrators and presentation leaves
  included, a strict superset of the scored `Types` list, kept as its own block
  so the scored table and its pinned type count are untouched. Each entry adds
  the edge-derived annotations: *depends on authority* (who observes whom), and
  the *mismatch advisory* when a type writes another type's networked state.
- **The `Authority` score dimension** (weight 10): a scored type earns it when
  the rubric assigns any role other than `Undetermined`, **or** when the project
  has answered the question below for it.

## The other way out of `Undetermined` — asking

The rubric refuses to guess, and that leaves a project whose components declare
no posture sitting at a ceiling better code cannot lift: the code is not what is
missing, the **decision** is. `AuthorityQuestionnaire` puts that decision to the
person holding it. Asking is not guessing — the answer is authored, and it is
recorded where it can be read again.

**Who is asked.** Exactly one of the three ways a verdict reaches
`Undetermined`: rule 3, a `NetworkBehaviour` that declares nothing. The
questionnaire reads `AuthorityClassifier.Classify`'s own verdict rather than
restating the rubric, then excludes the other two by the single signal that
defines each:

| cause | asked? | why |
|---|---|---|
| rule 0 — `ReadPartially` | **no** | the reader did not open the whole chain; an answer would certify code nobody looked at. Reference the assembly instead |
| rule 3 — a bare `NetworkBehaviour` | **yes** | the type is on the network and states nothing; the intent is the missing fact |
| rule 4 — networking signals off a `NetworkBehaviour` | **no** | the runtime discovers none of them there, so no declaration makes the intent reachable — RTMPE2001 |

**What is asked.** One question per type, phrased from the project's own
vocabulary: the type's simple name is split into words — at case boundaries and
at anything that is not a letter or digit — and each word is matched **whole**
against a fixed table, with a short list of endings (`s`, `ing`, `er`, `ment`, …)
so `PlayerMovement` and `Spawner` still land. `RoundClock` → *when the round
starts and ends*; a name the table does not know falls back to a neutral wording.
⚠️ That derivation is a **phrasing aid and never a signal** — it decides how a
question reads, never what is asked, of whom, or what an answer means. 🔑 Whole
words rather than substrings, because a substring rule reads `Skill` as *kill*,
`Steam` as *team*, `Runtime` as *run* and `Claim` as *aim* — all ordinary Unity
type names, and a confidently wrong noun is worse here than no noun at all. A
missed match costs the neutral wording; a wrong one costs the question's whole
claim to be in the developer's words.

**The vocabulary**, four answers, carried in the artifact beside the question so
no renderer restates them:

| id | who decides | what it costs here |
|---|---|---|
| `owner` | the client that owns the object | the one surface the runtime enforces — `NetworkVariable` owner-writes at the flush gate and at `object_authority_ok` |
| `host` | whoever is the room's host | a convention, not an enforcement: the host is a client, and the check is ownership. Gate on `IsMasterClient` — reached through `NetworkManager.TryGetInstance(out var manager)`, not `Instance`, which is null after `OnApplicationQuit` and off the main thread — **and** keep the object owned by the host |
| `server` | the server | ⚠️ the one answer needing code outside Unity: a handler registered with `RegisterServerRpc` in the Room Service, bound to the method id — unregistered is `RpcErrorUnknownMethod` |
| `each-client` | every client, for itself | nothing replicates and no authority is owed; the type may not need to be a `NetworkBehaviour` at all |

**Where the answer lives.** `network-authority-answers.json`, beside the
artifact — its own file, and an **input** to the scan. ⛔ Never inside
`network-readiness.json`: that artifact is rewritten whole by every run, so an
answer kept there would survive exactly until the next scan, which is the run
that was supposed to read it. The readiness host reads it through `--answers`,
defaulting to the output directory; the editor window writes it, under the name
the artifact itself carries (`answersFile`), so the engine stays its sole author.

**What an answer buys, and what it does not.** It clears the `Authority`
dimension — ten points of a hundred, per type — and nothing else. `State`,
`Ownership`, `RPC` and `Lifecycle` still measure the code, so a project cannot
answer its way to a hundred over code that replicates nothing. And the rubric's
`Role` is **not** rewritten: it still reports `Undetermined`, with the
declaration beside it in its own field and in the evidence, because putting an
answered verdict under the rubric's name is the one thing this document forbids
the rubric to do.

**An answer the scan cannot honour is reported, never dropped** — a to-do line
per orphan, naming which of four things happened: this compilation resolved no
`NetworkBehaviour` at all and so asked nothing, the type is gone or renamed, the
code now states the posture itself (the answer is spent), or the `Undetermined`
is one of the two a declaration cannot settle. A type answered twice gets a line
of its own; the answers **file** refuses that outright, and the library entry
point takes the last and says so, because choosing between a developer's two
decisions in silence is not the tool's to do.

## The re-baselined score

Adding the sixth dimension rebalanced `DimensionWeights` to
`25/20/20/15/10/10` and deliberately re-pinned every derived score in the same
change: the crafted two-type shape `88 → 90`, the stateless controller
`75 → 80`, the shipping-sample project score `94 → 95`, the conversion-shape
fixture `75 → 80`, the golden `network-readiness.json`/`.md` artifacts, and the
Markdown table header. Nothing drifted silently: every derived score in the tree
was enumerated and re-pinned in the one change that moved the weights.

## What every recommendation is honest about

- An unguarded `Authoritative` type is told that `NetworkVariable` owner-writes
  are runtime-enforced but a local mutator still runs on every client — add the
  guard (RTMPE2003) or route through a Server RPC, **and** that a Server RPC
  executes only in a backend handler registered via `RegisterServerRpc`
  (unregistered = `RpcErrorUnknownMethod`, a no-op).
- A type that writes another type's networked state gets the mismatch advisory
  with the same Server-cost note.
- **A broadcast audience is never advised for anything** — the M3 bound
  (sender is authenticated, caller→object authority is not) makes a broadcast
  mutator a cheat primitive the runtime cannot close.

## What authority inference deliberately does not do

No source rewrite (the actionable pointers are the conversions and generators
documented beside this file); no ownership transfer; no authority change; no
scene/prefab parsing; no ledger write (`<Type>.rtmpe-ids.json` bytes are pinned
untouched by test); no new Warning/Error severity; no `Workspaces`/MSBuild
dependency; no AI. The graph and classification output is a structured contract
a ranking layer may consume — deterministic data it may order but can never
overrule.

## Test map (all in `unity-sdk-analyzers`)

| Suite | What it pins |
|---|---|
| `AuthorityClassifierTests` | the rubric table rule by rule; recommendation honesty (Server cost note); the no-broadcast sweep over every signal combination; determinism |
| `AuthorityInferenceTests` | signal extraction over real syntax (chain NV count, disjunction guards, both RPC shapes, wiring-vs-observation); the pinned 4-edge project graph over the five real samples; `RTMPE9001` role pins incl. **`GameManager` = `Orchestrator`**; `ConnectionTest` (own compilation) = `Presentation`; the authored `ScoreManager`/`HudController` fixture separation; the write-mismatch advisory; authority block in both artifact formats; the ledger-bytes-untouched guarantee |
| `NetworkReadinessScorerTests` / `SampleFilesReadinessTests` / `ReadinessArtifactEmitterTests` / `ConversionDoDTests` | the re-baselined pins (90/80/95/100) and artifact goldens |
| `WhatTheToolAsksWhenItCannotTellTests` | who is asked and who is not (all three `Undetermined` causes, every derivable role); that an answer clears `Authority` and moves the score by exactly its weight while every other dimension stands; that the rubric's role is not rewritten; the three unapplied-answer reports; the applied ⊆ asked invariant; the vocabulary's round trip and its single server-implementation cost; determinism under answer order |

Two more live outside that shard, where the surfaces they hold are compiled:
`TheAnswersFileIsOneFormatTests` and `WhatTheWindowAsksAndRecordsTests` in
`unity-sdk-setup-wizard` (the editor writer against the engine's real parser, and
the window's questions section pressed rather than reflected into), and the
`--answers` contract in `ReadinessCliTests` in `unity-sdk-conversion-golden`.
