# Network Readiness Score

A deterministic, explainable `0–100%` measure of how ready a project's
`NetworkBehaviour` types are for multiplayer, computed by
`RTMPE.SDK.Analysis.NetworkReadinessScorer.Score(Compilation)`. No AI, no
heuristics beyond the rules below — the same compilation always yields the same
report, byte for byte.

## Weights

Each scored type earns a `0–100` score by summing the weight of every dimension
it clears. The weights sum to 100. The `Authority` dimension
re-baselined the table — every pinned score and the golden CI artifact were
re-pinned in the same change (see `AUTHORITY_INFERENCE.md`).

| Dimension | Weight | Cleared when |
|---|---:|---|
| Structural | 25 | the type inherits `RTMPE.Core.NetworkBehaviour` (every scored type, by selection) |
| State | 20 | the type **constructs** at least one `NetworkVariableBase` field/property **and** raises no `RTMPE1010`/`RTMPE1012` diagnostic (two members deriving one identity, or a default-rotation quaternion is present-but-unsound state) |
| Ownership | 20 | the type runs no frame loop (`Update`, `FixedUpdate` or `LateUpdate`, its own or inherited); or every frame loop that drives its own state opens with an early return for non-owners (`if (!IsOwner) return;`, or an equivalent such as `IsOwner == false` / `IsOwner != true`); or no frame loop writes state of its own — nothing into this object's own fields or their components, into an element of its own storage, into a `NetworkVariable` it holds, or into its own `transform`, whether written directly, handed out through an `out`/`ref` argument, or reached through a helper the type declares — because a loop that only draws must run on every client and a guard there is exactly wrong |
| RPC | 15 | the type raises no `RTMPE1001`–`RTMPE1006` diagnostic |
| Lifecycle | 10 | the type raises no `RTMPE1011`/`RTMPE1020`/`RTMPE1021` diagnostic |
| Authority | 10 | the authority rubric assigns the type a discernible role — replicated state, an owner guard, an RPC surface, or a lifecycle override; a bare `NetworkBehaviour` with none of them is `Undetermined` and does not earn the weight |

⚠️ **A replica-side apply is faulted here, and the diagnostics guide says the
guard is wrong in it — both are correct.** A loop that writes this object's own
transform from received state runs on every client *except* the owner; the
dimension reads what a loop WRITES, never why, so it cannot tell that loop from
an unguarded simulation. Such a component scores 80 and stays at 80. That is the
number stating what it measured, not a defect to repair — and repairing it by
adding the guard is the one edit that stops the component working.

The artifact carries the reason verbatim, and these are the only four:
Ownership clears as `no frame loop to guard`, as
`every frame loop that drives its own state opens with the IsOwner guard`,
as `no frame loop writes state of its own to guard`, or as
`cleared on a partial reading — a write in a frame loop rests on a type that did not resolve`.  A loop that faults names itself, so a report says which of
them the guard is missing from.

⛔ **The fourth is a clear that says it might be wrong, and it is deliberately
not a fault.** Every question this rule asks of a write is asked through a
symbol, so a member whose type did not bind answers "reaches nothing" to all of
them — and the weight used to be granted on that, in silence. It is still
granted, because the headless toolchain resolves a 25-type contract and never
UnityEngine: a held `Rigidbody`, a UI `Text` or a `CharacterController` all
arrive unbound, and deducting for them would fault the presentation loop this
dimension exists to leave alone.  What changes is the claim — the detail says the
reading was partial, and the **To-do** list names the type — so a hundred earned
this way is one the reader can see the reservation on.

**Static readiness** (the project score) = the mean of every scored type's
score, rounded to the nearest integer (`MidpointRounding.AwayFromZero`). It is a
mean over the types the scorer accepted — concrete `NetworkBehaviour` subclasses
— so both renderings state that count beside it, and neither offers it as a
fraction of the components a project holds.

## Single source of truth

The Structural, RPC, and Lifecycle dimensions are the shipped analyzers' own
verdicts: the engine runs `Rtmpe1000NetworkBehaviourAnalyzer`,
`RpcRulesAnalyzer`, `NetworkVariableRulesAnalyzer`, and `LifecycleRulesAnalyzer`
over the compilation and buckets their diagnostics by the enclosing type. The
score therefore cannot disagree with what the IDE shows. Ownership is a positive
source check the score computes directly from the symbol and syntax model rather
than from a diagnostic: the `RTMPE2xxx` rules (`RTMPE2001`–`RTMPE2004`) are
Info-level conversion hints, not the Structural/RPC/Lifecycle
faults the other dimensions consume. State
combines such a positive check — is replicated state present? — with the same
analyzers' `RTMPE1010`/`RTMPE1012` correctness verdicts, so present-but-unsound
state does not earn the weight. Authority is the authority rubric's verdict
(`AuthorityClassifier` over `AuthoritySignalExtractor`), the same pure function
that drives the `RTMPE9001` advisory diagnostic and the report's authority
block — the three surfaces cannot disagree.

## What is scored, and what is not

- **Scored:** every concrete (non-abstract) class declared in the compilation
  that inherits `NetworkBehaviour`. A `MonoBehaviour` orchestrator such as the
  samples' `GameManager` is **not** networked and is excluded entirely — never
  penalised. (It still appears in the report's *advisory* authority block,
  which classifies every component type — see `AUTHORITY_INFERENCE.md`; the
  scored table and its type count are untouched by that block.)
- **`[SerializeField]` config is never state.** A serialized field is Inspector
  data whose value lives in `.prefab`/`.unity` YAML, invisible to Roslyn. It is
  neither counted toward the State dimension nor penalised; only a real
  `NetworkVariable` member earns the State weight.
- **Declared is not replicated.** A `NetworkVariableInt _v;` that nothing
  constructs is null for the object's whole life: it registers with no
  behaviour, the flush loop never reaches it, and no peer sees a value. Only a
  member something CONSTRUCTS is counted, and a type whose declarations are all
  unconstructed does not clear the dimension — the detail names them. Where a
  type constructs some and not others, the cleared verdict names the rest.
  ⛔ Construction is resolved through the semantic model, so a local or a
  parameter of the same spelling does not count as constructing the member; and
  it is looked for anywhere in the compilation, because a base class, another
  partial part or a helper called from the spawn hook is an ordinary place for
  it — the question is whether anything constructs it, not where.

## One preprocessor configuration at a time

Roslyn parses a file under a single symbol set, so code inside an inactive
`#if` is not in the syntax tree and is scored as absent — and absent networked
code reads as a *cleaner* project. A type behind `#if UNITY_ANDROID` would
otherwise raise the score and lose its authority verdict.

There is no symbol set that makes every region active — defining `UNITY_ANDROID`
deactivates the `#if UNITY_EDITOR` blocks — so the configuration is the
operator's to state. `make readiness DEFINE="UNITY_ANDROID DEVELOPMENT_BUILD"`
forwards each symbol to the verb's repeatable `--define <SYMBOL>`, and the run
names the set it parsed with so a report is never read under a configuration
its reader assumed. It warns, per file, when a region is inactive under that
set. In
the editor the generated csproj supplies the project's symbols, so this is a
property of the unattended run. The same caveat governs the authority block —
see `AUTHORITY_INFERENCE.md`.

## Source-readiness only (the load-bearing caveat)

This is a **source-readiness** score. Roslyn's semantic model sees source code
only; an entire category of Unity wiring — scene/prefab references, string-keyed
spawn ids, Inspector-assigned values, `NetworkTransform` placement — lives
outside it and is **out of scope**. A `100%` score means the source shape is
ready, not that the editor-bound wiring is correct. Treat the score as a
move-left signal, not a certificate.

⛔ **And it is not a percentage of game completion.** It is a weighted sum over
six dimensions — Structural 25, State 20, Ownership 20, RPC 15, Lifecycle 10,
Authority 10 — averaged over the types the scorer accepted. So it climbs as
clean types JOIN that set rather than as the game converges, and a project can
reach 100 % having never opened a socket. What a run establishes is the
**second result**, below, and neither number moves the other.

## Pinned sample score

Over the five shipping sample scripts' scored shape
(`Samples/SimpleFPS` + the package's `Samples~/PlayerSpawnFlow`, which is where
the spawn-flow pair moved in v14.0.4):

| Type | Score | Note |
|---|---:|---|
| `FPSController` | 100% | replicated `NetworkVariable`, guarded `Update`, spawn-time init — `Authoritative` |
| `HealthController` | 100% | replicated `NetworkVariable`, server-authoritative (no `Update`) — `Authoritative` |
| `PlayerController` | 100% | replicated `NetworkVariable`, guarded `Update` — `Authoritative` |
| `ShootingController` | 80% | a stateless RPC sender — holds no `NetworkVariable`, so the State dimension is not cleared (listed in the to-do, not a fault); its guard + RPC surface classify it `OwnerPartitioned`, so Authority clears |
| `GameManager` | — | a `MonoBehaviour` orchestrator — excluded from scoring; classified `Orchestrator` in the advisory authority block |

**Static readiness: 95% — 4 types scored** (mean of `100, 100, 100, 80`). This value is pinned over
the **real** sample scripts by
`SampleFilesReadinessTests.RealSampleScripts_ProduceThePinnedReadinessScore` and,
over the equivalent crafted shape, by
`NetworkReadinessScorerTests.SampleShape_ProducesThePinnedScore`. (Before the
re-baseline the same shapes pinned `75`/`94` under the five-dimension
weights.)

## CI artifact

`ReadinessReportSerializer` renders the report to deterministic JSON and
Markdown (fixed `\n` line endings, invariant number formatting, canonical type
and dimension order). The emitter — `ReadinessArtifactEmitterTests` in the
`unity-sdk-analyzers` shard — scores the five real sample scripts, asserts the
pinned `95%`, and writes `network-readiness.json` + `network-readiness.md` beside
the test assembly. Both formats now carry the advisory authority block
(`"authority"` in the JSON; `## Authority (advisory)` in the Markdown). The `Unity SDK .NET Unit Tests` workflow uploads them as the
`network-readiness` CI artifact, so the score is trackable over time.

> The samples reference Unity gameplay APIs that the headless build box does not
> resolve; the emitter scores the real sample **sources** against the SDK
> contracts, which is sufficient for every dimension (the one Unity type any
> dimension reads — `Transform`, for Ownership — is in the contract, which is
> why it is there).

## Headless emission host — `make readiness`

The CI test emitter above was the artifact's only writer; the `readiness` verb
of `RTMPE.SDK.ConversionCli` is its first production host:

```bash
make readiness                 # scores the five SDK samples, writes the artifact
                               # pair into clients/unity-sdk/ (git-ignored)
make readiness SOURCE=<dir>    # scores every .cs under <dir> instead (obj, bin,
                               # Library, Temp, Logs, .git, Packages,
                               # PackageCache, Editor, Tests segments skipped)
make readiness OUT=<dir>       # override the output directory
make readiness ANSWERS=<path>  # the project's recorded authority answers;
                               # without it the verb looks for
                               # network-authority-answers.json in OUT
make readiness RUNTIME=<path>  # what a RUN of this project established; without
                               # it the verb looks for
                               # network-runtime-checks.json in OUT
```

The verb compiles the sources against the **same SDK contract stub and
reference set** the CI suite uses; `ReadinessCliParityTests` (analyzers shard)
pins the stub verbatim-equal and the serialized artifact byte-identical to the
CI fixture's, so the two writers cannot diverge silently. `--source` mode
carries the same source-readiness caveat as everything else here: unresolved
gameplay APIs degrade signals toward the honest "I don't know", never a wrong
verdict.

⚠️ **Ownership is the exception, and it degrades upward.** A dimension has two
answers, not three, so "no recognised own-state write" is spelled *cleared*, and
anything the rule does not read earns the weight rather than withholding it.
⛔ Two distinct reasons it may not read a write, and only one of them is about
resolution: a write reaching **through** a component the type holds is excluded
by the rule itself — `_rb.velocity = v` is not counted even where `Rigidbody`
resolves perfectly, because it is the same shape as `_animator.SetBool(…)` — and
separately, a symbol the contract does not model cannot be read at all. The
evidence the rule does read is chosen to be resolution-independent (this object's
own fields, a `NetworkVariable` it holds, its own `transform`, all in the
contract), but a component that drives everything through cached components is
scored generously here and nowhere else.

## The authority answers the scan reads

`Undetermined` is the rubric's honest fallback, and for one of its three causes
— a `NetworkBehaviour` that declares no posture — the missing fact is a decision
rather than a signal. The verb asks, and reads the answer back on the next run;
the rubric and the vocabulary are specified in
[`AUTHORITY_INFERENCE.md`](AUTHORITY_INFERENCE.md).

⛔ **The answers live in their own file, not in the artifact.** `readiness`
rewrites `network-readiness.json` whole on every run, so an answer stored there
would survive exactly until the next scan — the run that was supposed to read
it. `network-authority-answers.json` is an **input**; the artifact carries the
answers back out (`authority[].declared`, plus a `questions` block) so the editor
window can render them, and carries the file's own name (`answersFile`) so the
window writes where the engine reads.

Every outcome is said out loud on stdout — `authority answers: none recorded at
<path>` or `authority answers: N recorded in <path>` — because a scan that
quietly found none and one that quietly failed to look publish the same artifact.
A named `--answers` path that is not there is a usage error (exit 1), as it is
for `--source`; a file the engine refuses stops the run rather than applying the
entries it happened to understand. An answer the scan cannot honour — no
networked surface resolved at all, nothing classified, a renamed type, a posture
the code now states for itself, or an `Undetermined` a declaration cannot settle
— becomes a to-do line naming which of the **five** it was.

**An answer clears the `Authority` dimension and nothing else.** State,
Ownership, RPC and Lifecycle still measure the code, so ten points of a hundred
per type is the whole of what a declaration buys.

⚠️ The artifact names the **default** answers file (`answersFile`) for the editor
window, which writes beside the artifact. An `--answers` path anywhere else makes
the two disagree, and the verb says so on the run that creates the divergence
rather than leaving it to be found as an answer that never reaches a score.

## The second result: what a run established

The score above is decided from source. These five are decided by a **run**, and
the scan cannot reach one by reading anything:

| Check | A pass means |
|---|---|
| `connection` | a handshake completed against a deployed gateway |
| `shared-room` | two clients joined one room and each roster names the other |
| `both-players` | each client's spawn reached the other, under its own owner |
| `sync` | a replicated value crossed, and a foreign write did not |
| `reconnect` | a dropped session resumed on its token |

Every one starts **`not-tested`**, and the only thing that moves it is an entry
in `network-runtime-checks.json` — an **input** to the scan, beside the
artifact, for the same reason the answers file is one: the artifact is rewritten
whole on every run.

```json
{
  "checks": [
    { "check": "sync", "result": "passed", "observedBy": "room-pair",
      "observedAt": "2026-09-07T09:14:22Z", "sdkVersion": "<the package version>",
      "detail": "an update crossed and a claim on another player's object did not" }
  ]
}
```

⛔ **An outcome names who observed it, when, and against which version.** A
record that omits any of the three is **refused whole** — not skipped — because
a pass with nothing behind it is a claim anybody can type, and a reader who
dropped the entry would publish five untested rows and say nothing.

Two things write it:

- **The load harness.** `go run . -scenario room-pair -runtime-record <path>
  -sdk-version <v>` merges what the run reached. `room-pair` reaches the first
  four; `reconnect-storm` reaches `connection` and `reconnect`. ⛔ Neither
  claims a check it did not exercise, and a run that FAILS still records the
  checks it got past plus the one it died on. `-runtime-record` without
  `-sdk-version` is refused at the command line.
- **The Readiness window**, for what a developer saw with their own two clients.
  Those outcomes are stamped `observedBy: developer`. ⚠️ The tool cannot verify
  either kind — a record is a record — but the two are written differently so a
  reader can tell a measured scenario from a recollection.

## In-editor viewer — `Window/RTMPE/Network Readiness`

`NetworkReadinessWindow` (in `RTMPE.SDK.Editor`) renders the artifact: project
score, per-type dimension verdicts (uncleared = the type's blockers), the
advisory authority block, the open authority questions, and the aggregated to-do
list. It hosts no scorer and adds no `Microsoft.CodeAnalysis` reference to the
editor assembly (DD-5 Option A), and it flags a stale (>24 h) artifact instead of
silently rendering it as current. Regenerate with `make readiness`, then Refresh.

⚠️ It writes exactly two things, each into its own file beside the artifact: an
answer to an authority question, into `network-authority-answers.json`, and an
outcome the developer observed, into `network-runtime-checks.json`. Neither
number on the window moves when it does — the score is produced by the **scan** — and the
window says so, with the regenerate command for this project, rather than
letting a click read as a score change. Its in-editor render is human-verified in the
same floor-editor session as the analyzer load confirmation in
[`COMPILER_COMPATIBILITY.md`](COMPILER_COMPATIBILITY.md).
