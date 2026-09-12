# Conversion Wizard

The `Window/RTMPE/Conversion Wizard` is the in-Editor surface that turns a
single-player `MonoBehaviour` into a multiplayer-ready `NetworkBehaviour` by
applying the mechanical conversions in batch, behind a mandatory
diff-approval gate. It is designed as a **thin shell over the headless transform
core** (`RTMPE.SDK.Transforms`) that the IDE quick-fixes also call: because both
surfaces route through that one core, a conversion done in the wizard and the same
conversion done by a Rider/VS lightbulb are byte-identical **by construction**.
The test suite asserts only the leg it can reach headlessly — that each IDE
code-fix reproduces the transform core verbatim (the parity `[Fact]`s in
`CodeFixProviderTests.cs`, `unity-sdk-codefixes` shard). The wizard shell exists
(`Editor/ConversionWizard.cs`) but compiles only inside Unity, so its side of that
parity rests on the shared core and on the CLI contract and source invariants
pinned in the `unity-sdk-conversion-golden` shard, not on an executed in-editor
test.

> **Status — shell BUILT (v1, 2026-07-17); in-editor verification human-gated.**
> `Editor/ConversionWizard.cs` now exists and implements the flow below. Two
> v1 decisions, both testable headlessly:
>
> - **DD-W3-1 — process-backed thin shell.** v1 drives the proven headless CLI
>   (`fix` / `convert` / `gen-rpc` verbs of `RTMPE.SDK.ConversionCli`) as an
>   external `dotnet` process instead of loading the transform core in-process.
>   It builds the engine and then executes the built assembly, rather than going
>   through `dotnet run`: that verb is a launcher which starts the real binary as
>   a child, so the process the window can cancel would not be the process
>   holding the script and ledger open. The wizard's edit and the CLI's edit are
>   byte-identical by *identity* (same Release build as the `make` verbs), the
>   editor loads **zero** Roslyn, and the whole analyzer load spike
>   is deferred to the post-W0 in-process upgrade — which would change only this
>   window's plumbing, never its flow. The exact CLI surface the wizard shells
>   out to (verb spellings, `+++ b/` diff labels, the `no-op:` marker, the
>   audience set, fail-closed exit codes) is pinned by
>   `WizardCliContractTests` in the `unity-sdk-conversion-golden` shard.
>   Consequence: v1 requires a built copy of the engine — the one shipped under
>   `Automation~/`, or a checkout's — and a .NET SDK on PATH; the window
>   degrades to an explanatory message without them.
> - **DD-W3-2 — ledger `.meta` is import-generated in v1.** The host contract's
>   "deterministic `.meta`" clause is deferred: writing an importer-typed meta
>   blind (before the editor confirms the importer Unity picks for `.json`)
>   risks worse churn than letting `ImportAsset` create it. Revert deletes a
>   created ledger **and** its generated `.meta`, so no orphan remains.
>
> The in-Editor load and the human verification legs (DoD-8/10) still require
> the on-disk Unity 6.3 editor — see
> [Open, human-gated items](#open-human-gated-items).

## What it converts

The three identity-free transforms (now also hosted headlessly by
`make fix FILE=… TYPE=… KIND=rebase|owner-guard|base-ondestroy [APPLY=1]` — the
same engines, so wizard, IDE lightbulb, and CLI produce one output) — no network
identity (a NetworkVariable's, an RPC's) is derived by these, so the wire format,
FlatBuffers, crypto, ports, and migrations are untouched:

| Transform | Mirror diagnostic | Edit |
|---|---|---|
| Rebase | `RTMPE2001` | base `MonoBehaviour` → `NetworkBehaviour`, and add `using RTMPE.Core;` when absent |
| Owner guard | `RTMPE2003` | insert `if (!IsOwner) return;` as the first statement of `Update` |
| Lifecycle chain | `RTMPE1020` | append `base.OnDestroy();` to a declared `OnDestroy` (override or hiding) |

Each edit is **additive and rename-free**, and each carries an idempotency guard,
so re-running the wizard over an already-converted script is a byte-identical
no-op (its mirror diagnostic has stopped firing).

> **Authority inference is advisory, not a conversion.** The wizard hosts
> *conversions*; authority inference rewrites nothing. Its dependency-graph
> classification surfaces as the `RTMPE9001` Info diagnostic and the readiness
> report's authority block — see [`AUTHORITY_INFERENCE.md`](AUTHORITY_INFERENCE.md).
> The wizard may eventually *surface* that advisory report; it adds no new
> transform to the batch above.

**NetworkVariable generation — engine delivered; the wizard hosts it through
the same headless CLI.** `RTMPE2002` detection, the `<Type>.rtmpe-ids.json` id ledger, the
generation transform, and the headless `make convert` host now exist (see
[`NETWORKVARIABLE_GENERATION.md`](NETWORKVARIABLE_GENERATION.md)). When this
window is built it becomes the in-editor **allocation host** and must honor the
host contract: one approval diff covering **both** the `.cs` edit and the
ledger (plus a deterministic `.meta` under import roots), atomic per-type
persistence with a persist-time ledger re-read, and never batch-applying a type
with a pending fail-closed verdict.

**Enhanced-RPC generation — engine delivered; the wizard hosts it through the
same headless CLI, audience always human-designated.** `RTMPE2004` detection, the FNV-derived method id, the
`RpcGenerationTransform`, the `rpcs` ledger provenance, and the headless
`make gen-rpc` host now exist (see [`RPC_GENERATION.md`](RPC_GENERATION.md)).
The wizard hosts it through that same headless CLI, under the host contract
above and one rule of its own: the audience for a state-mutating method is
always the human's designation, never inferred. The Audience popup opens on
*— choose one —* and sends no target until somebody picks, which is why
`RTMPE2004` carries no IDE lightbulb — a one-click action has nobody to ask.

**Out of scope.** The
`RTMPE1021` lifecycle-signature correction is also excluded: rewriting a hook's
parameter list or return type can orphan the references its body already makes,
which no source-diff gate can detect, so it is left to a guided manual fix rather
than a mechanical batch one.

## Flow

Mirrors the existing `SetupWizard` / `NetworkDebuggerWindow` IMGUI windows
(`[MenuItem("Window/RTMPE/Conversion Wizard")]` → `GetWindow<ConversionWizard>().Show()`,
an `OnGUI` stepper with Back / Next / Finish):

1. **Scan.** List every component type the readiness artifact names — the
   authority block's superset, so orchestrators and presentation leaves appear
   too — and resolve each to a script under `Assets/`, excluding `Editor/` and
   `Tests/` paths. Types already at the destination shape are **listed, not
   hidden**: each row carries its role and verdicts, which is what lets the
   author see why a type does or does not need converting. A conversion that
   would be a no-op says so when previewed.
2. **Readiness list.** Show each candidate with its dimension verdicts read from
   the precomputed `network-readiness` artifact on disk (`ReadinessReportSerializer`
   JSON — see [DD-5](#scoring-source)). The wizard does **no** scoring itself.
3. **Diff preview.** For each selected script, render the before → after unified
   diff produced by the transform core — exactly the text that will be written.
4. **Mandatory approval.** `EditorUtility.DisplayDialog` requires explicit
   confirmation of the previewed diff before any file is written. There is no
   silent or auto-apply path.
5. **Apply.** Write the transform-core output and `AssetDatabase.ImportAsset` each
   changed script so the editor recompiles against the new shape.
6. **Undo.** See [the undo model](#undo-model).

## Converting several types as one decision

The **Generate NetworkVariable** conversion accepts more than one type. Beneath
the member field the window lists every other resolved candidate with a checkbox
and its **own** member field — each type names its own member, which is the whole
point — and one approval drives one `convert-batch` invocation.

🔑 **This is not a loop over the single-type verb, and the difference is the
feature.** A loop writes as it goes: the fourth conversion failing leaves three
types converted, one not, and a tree no single command produced — and for this
conversion the three that landed have already spent wire ids. The batch plans
every type and runs every guard while nothing has been written, then commits the
whole set through one transaction.

The single-type path is untouched: with one type selected the window emits
exactly the `convert` vector it always has, verb-less and option-for-option.

⚠️ **A batch converts scripts from one folder**, and the window refuses one that
spans folders, naming the offending script. The engine labels diff files by name
alone, and every pin, snapshot and revert path in the window resolves those names
against one directory — a batch across folders would pin and restore the wrong
files. Two folders are two batches.

⛔ The other four conversions have no batch verb behind them, so the checkboxes
are offered only for this one, and a candidate left checked from an earlier
selection can never leak into a `fix` or `gen-rpc` run.

**Verification.** The window is **compiled** against minimal `UnityEditor` shims
in the SDK's own suite — before that no compiler had ever read it, and its
coverage was a parse — and the argument vector is built through the real method,
then handed to the real verb and run. Read the warning at the top
of `UnityEditorShims.cs` before touching either side: the shims have the right
shapes, not Unity's behaviour, and a shim must never be edited to make the window
compile.

## Scoring source

**DD-5 Option A (default).** The wizard reads the precomputed `network-readiness`
JSON (the same artifact the CI emits, [`NETWORK_READINESS_SCORE.md`](NETWORK_READINESS_SCORE.md))
from disk and renders the per-script list from it. The wizard hosts no scorer.

The **apply** step hosts no engine either. It shells out to the headless
conversion CLI (DD-W3-1), so no `Microsoft.CodeAnalysis` assembly is loaded in the
editor at all and the wizard's edit is the CLI's edit by identity rather than by
resemblance. Hosting the transform core in-process is the post-W0 upgrade, gated
on the analyzer load confirmation in
[`COMPILER_COMPATIBILITY.md`](COMPILER_COMPATIBILITY.md); re-hosting a scorer
in-editor (Option B) is a separate, later stretch.

## Undo model

Unity's `Undo` API does **not** track external file writes, so it cannot revert a
`.cs` edit the wizard makes. The wizard therefore owns its own undo:

- Before writing, it snapshots each target file's original text.
- A **Revert** action restores those snapshots and re-imports the assets.
- Apply re-checks **every file the previewed diff names** — the script and, for
  the identity-allocating conversions, the ledger that decides the wire id —
  against the bytes pinned when that diff was produced, and refuses if any has
  moved. The script itself is read on both sides of the engine run, so a file
  edited while the engine held it open is caught rather than pinned.
- The **mandatory diff-approval gate is the primary safety net** — nothing is
  written without the author confirming the exact diff first.

## Source-diff-only — the load-bearing caveat

Roslyn's `SemanticModel` sees **source only**. An entire category of Unity wiring
is invisible to it and therefore to the wizard's diff:

- `[SerializeField]` Inspector values, which live in `.prefab` / `.unity` YAML;
- scene and prefab object references, and `NetworkTransform` placement;
- string-keyed spawn ids and RPC sends.

Two consequences the wizard surfaces explicitly:

- **It never retypes a `[SerializeField]` field in place.** A serialized field is
  artist-authored Inspector data; converting it to a `NetworkVariable` in source
  would silently orphan that YAML value. Config and replicated state stay
  separate (the `HealthController` sample is the reference shape).
- **The author must manually re-wire prefabs and scenes** after a conversion.
  The source diff is necessary but not sufficient; the wizard shows a standing
  warning to this effect. The diff gate is **blind to YAML drift**.

  What the warning could not do was name the assets, so the window now offers a
  scan. The failure it exists for: the RPC conversion keeps a method's name and
  rewrites its **C# call sites**, while a UnityEvent invokes by name through
  reflection — so a converted method keeps running locally and nothing reports
  it. The scan matches the **value**, wherever it appears as a scalar, and
  reports the key as evidence rather than filtering on it: keying on
  `m_MethodName` would be a claim about Unity's serialisation format that this
  repository cannot verify, and if it were wrong the result would be "nothing
  found" — which reads as "checked and clean".

## Open, human-gated items

These require the on-disk Unity 6.3 editor (`6000.3.13f1`) and cannot be closed
on a headless build box. They inherit the analyzer load spike in
[`COMPILER_COMPATIBILITY.md`](COMPILER_COMPATIBILITY.md), which is resolved first.

- [ ] The `Window/RTMPE/Conversion Wizard` menu item appears; scanning lists the
      candidates; the diff preview renders; approval is required; batch apply
      writes the files; **re-running is a no-op** (DoD-8).
- [ ] The IDE (Rider / VS) surfaces the quick-fix lightbulb on `RTMPE2001`,
      `RTMPE1020`, and `RTMPE2003` (DoD-9).
- [ ] The wizard's **Revert** restores the original source, and the converted
      fixture **compiles in Unity 6.3** (DoD-10).
- [ ] The `RTMPE.SDK.Transforms` engine loads in the editor with zero load
      warnings (inherits the analyzer placement-(b) load spike) (DoD-11).
