# Roslyn / Unity Compiler Compatibility — Empirical Record

This file records the compiler surface the RTMPE analyzer is built against and
the Unity host it must load into. It is the canonical record the analyzer
`csproj` pin is written from.

## Host editor

| Surface | Value | Source |
|---|---|---|
| Package floor (`unity`) | `2022.3` — the lowest editor the package declares it loads into | `Packages/com.rtmpe.sdk/package.json` |
| Floor host Roslyn (authoritative for "does the analyzer load") | `4.3.0`, observed on one Unity 2022.3 install — see the verification log for the provenance and its limits | field report, 2026-07-22 |
| Editor checked out in this repo | `6000.3.13f1` (Unity 6.3) | `ProjectSettings/ProjectVersion.txt` |

The two editor rows are deliberately separate. The repo's own editor sits
**above** the floor, so it can never confirm it: a check run here compares the
pin against Roslyn `4.8`+ and passes for any pin ≤ `4.8`, which says nothing
about `2022.3`.

The **floor** editor is authoritative: Unity loads the analyzer into its **own**
bundled `csc`, so the analyzer's `Microsoft.CodeAnalysis.CSharp` reference must
be **≤** the *lowest* supported host's Roslyn version. Any newer editor (Unity 6,
whose Roslyn is `4.8`+) clears the pin by construction, so a single pin at or
below the floor host's Roslyn covers the entire supported range.

## Compiler pin

| Item | Value |
|---|---|
| `Microsoft.CodeAnalysis.CSharp` (analyzer) | **`4.3.0`** |
| Pinned in | `RTMPE.SDK.Analyzers/RTMPE.SDK.Analyzers.csproj` (and the sibling toolchain + test csprojs, one line) |
| Test host (`Microsoft.CodeAnalysis.CSharp`) | `4.3.0` — the shards run against the exact Roslyn Unity 2022.3 bundles, so a behaviour that only holds on a newer compiler cannot pass here unnoticed |

`4.3.0` is the Roslyn version reported by the one Unity 2022.3 install this pin
has been measured against, and the version an analyzer reference `≤ host` must
not exceed on it. Unity does not lower a bundled compiler within an LTS line, so
later `2022.3` patches are expected to clear it; earlier patches in the line are
**unmeasured**, and a host below `4.3.0` would refuse the analyzer with the same
**CS9057** this pin exists to prevent. The analyzer uses only long-stable
APIs (`GetTypeByMetadataName`, `RegisterSymbolAction`,
`RegisterCompilationStartAction`, `SymbolEqualityComparer`), proven compatible
with `4.3.0` by the whole solution building against it with **zero warnings**
(the definitive API-availability gate); the pin governs the shipped DLL's
metadata version, and lowering it from the earlier `4.8.0` is what re-admits the
Unity 2022.3 LTS community that a `4.8.0` reference locked out with **CS9057**.
`3.8` is **not** assumed (it is a 2020/2021 floor).

## Packaging

The editor loads four DLLs as compiler plugins: the analyzer
(`RTMPE.SDK.Analyzers`) and its code-fix providers (`RTMPE.SDK.CodeFixes`), plus
the two engines both close over — `RTMPE.SDK.Conversion.Core` (the FNV-1a /
reserved-id engine the RPC collision rules `RTMPE1003`/`RTMPE1004` reuse) and
`RTMPE.SDK.Transforms` (the shared edit core). All four are staged together — the
editor cannot load a plugin whose dependency is absent, and the code-fixes are
what raise the quick-fix lightbulb the analyzer's diagnostics alone do not. The
identity engine remains a single source of truth shared with the runtime guard;
`Microsoft.CodeAnalysis(.Workspaces)` is the IDE host's to supply and is a
compile-time reference only, never staged.

`scripts/package-analyzers.sh` stages all four DLLs. It defaults to placement **(b)** —
the committed `Analyzers/` folder with a `RoslynAnalyzer`-labelled `.meta` per DLL,
which is what Unity loads and the only layout `check-analyzers-fresh.sh` reads.
⛔ Placement **(a)** remains selectable with `RTMPE_ANALYZER_PLACEMENT=a` and writes to
the Unity-ignored `Analyzers~/` folder, staging only the (git-ignored) DLLs and no
`.meta` — so a plain run never emits an unverified `.meta`. Placement **(b)** (a
non-tilde `Packages/com.rtmpe.sdk/Analyzers/` folder with a `RoslynAnalyzer`
labelled `.meta` per DLL and all player platforms deselected) is the intended
*shipped* layout and is selected with `RTMPE_ANALYZER_PLACEMENT=b`. **As of v1.9.0
placement (b) is the shipped layout:** `Packages/com.rtmpe.sdk/Analyzers/` carries
all four DLLs and a `RoslynAnalyzer`-labelled `.meta` each (all player platforms
deselected). The `.meta` import settings themselves are confirmed in the editor
(post-ship check below). ⛔ `Analyzers~/` no longer exists in the tree — it was the
staging that preceded the load spike, and its `.gitkeep` was removed once (b) became
the committed layout. `package-analyzers.sh` recreates it only when (a) is selected.
Historically it was the Unity-ignored staging folder and
holds a git-ignored copy of the four DLLs, which is why the freshness gate compares
the shipped `Analyzers/` folder and not this one; its `.gitkeep` is retained but
unused.

## Build reproducibility — the freshness gate's contract (2026-07-17)

`check-analyzers-fresh` byte-compares the committed DLLs against a fresh
Release build, which is only meaningful if identical source reproduces
identical bytes on every machine. Three drift channels were found live and are
closed as follows:

| Drift channel | Symptom | Closure |
|---|---|---|
| .NET SDK patch level (unattended apt upgrade `8.0.128 → 8.0.129`, 2026-07-16) | ~147 differing bytes; same size | **`Tooling/global.json`** pins `8.0.131`, `rollForward: disable` — and the pinned patch must be one the distribution still offers, because `disable` accepts no other: an unattended upgrade past a version apt has retired leaves the gate unable to build at all, which reads as a stale DLL rather than as a missing compiler — deliberately scoped to this directory, not the repo root, so only the byte-compared analyzer builds are pinned while the ~40 behavioural test shards (and every developer machine) keep floating on `8.0.x`; `package-analyzers.sh` builds from inside `Tooling/` so the pin cannot be bypassed by the caller's cwd, and the CI `analyzers-fresh` job installs via `setup-dotnet` `global-json-file: clients/unity-sdk/Tooling/global.json` |
| Release PDB codeview path (`/home/ubuntu/RTMPE/...obj.../*.pdb` inside the PE debug directory) | a CI checkout path can never equal the staging path — the gate was unpassable on CI | `<DebugType>none</DebugType>` for Release in each of the four shipped csprojs (no PDB ⇒ no path) |
| Git `SourceRevisionId` stamped into `AssemblyInformationalVersion` | the committed DLL always carries the *previous* HEAD's SHA vs a rebuild's new SHA — unpassable by construction | `<IncludeSourceRevisionInInformationalVersion>false</...>` in each of the four shipped csprojs |

Verified 2026-07-17: two builds from different absolute paths, one outside any
git checkout, are byte-identical to the committed DLLs on the pinned SDK.

**When the pinned SDK disappears from this box** (the next unattended apt
upgrade), any build under `Tooling/` — `make package-analyzers`,
`check-analyzers-fresh`, `make build-analyzers` — fails loudly with
"compatible SDK not found" — that is the intended fail-closed behaviour, not
breakage; everything outside `Tooling/` (the test shards, `make readiness`,
developer machines) is unaffected. Recovery is one step: update
`Tooling/global.json` to the new patch, re-run
`RTMPE_ANALYZER_PLACEMENT=b make package-analyzers`, and commit both together.

## Post-ship confirmation — human-gated (run on the lowest supported editor, Unity 2022.3 LTS)

Placement (b) shipped in v1.9.0 on the decision to **ship now and confirm in the
editor next** (no real subscribers; a failed load is harmless — the analyzer is
additive and simply absent). These remain to be confirmed on the editor machine:

- [ ] Read the bundled `csc.dll` Roslyn version from the Unity 2022.3 LTS floor editor and confirm
      `4.3.0 ≤` it; record the exact host value here. **Turnkey:** on the editor
      machine run `scripts/check-roslyn-version.ps1` (Windows) or
      `scripts/check-roslyn-version.sh` (macOS/Linux, also `make check-roslyn-version`)
      — it locates `csc.dll`, prints `4.3.0 ≤ host? YES/NO`, and appends the
      verdict to the log below.
- [ ] Load the staged analyzer in the editor with **zero load warnings** and
      confirm `RTMPE1000` (Info) surfaces in the Unity console on a
      `NetworkBehaviour` subclass.
- [ ] Confirm both DLLs are **absent from the player assembly** (each carries only
      the `RoslynAnalyzer` role).
- [x] Placement mechanic resolved: **(b)**, shipped in v1.9.0. The `.meta` import
      settings are confirmed by the editor load check above.

## Empirical verification log

Each run of `scripts/check-roslyn-version.{ps1,sh}` on an editor machine appends
a dated `4.3.0 ≤ host` verdict below, together with the `csc.dll` it read so a
verdict measured above the floor is self-evident. **No script run is recorded**:
the build box has no Unity editor, and the editor checked out here is above the
floor.

One host value has been observed outside the script, and it is the only evidence
the pin currently rests on:

- **2026-07-22** — field report, developer on Unity 2022.3. Their editor refused
  the then-`4.8.0` analyzer with **CS9057**, and the diagnostic named both sides:
  analyzer `4.8.0.0` against a running compiler of **`4.3.0.0`**. The refusal is
  itself the measurement — the host compiler identified itself.
  **Limits:** one install, one `2022.3` patch that was not recorded, and a version
  read from a diagnostic rather than from `csc -version`. It establishes that
  `4.3.0` is *reached* on `2022.3` — not that it is the line's minimum.

Until a script run from a `2022.3` editor lands here, treat the floor as
evidenced but unconfirmed. If a CS9057 report ever arrives against the `4.3.0`
pin, the host version named in that diagnostic is the new ceiling: lower the pin
to it and re-stage.

---

## Shipping runbook — analyzer into the package (Phase B)

The headless engineering is complete. **Status: the ship-diff (Step 5) was executed
in v1.9.0 on the ship-now decision** — placement (b) staged, `package.json` → 1.9.0,
`unity` floor → 2022.3 (restored from the v1.9.0 raise to 6), CHANGELOG + AnalyzerReleases rolled, build/tests green. Steps 1–4
below are now the **post-ship in-editor confirmation**, not a pre-ship gate; a failed
load is harmless (additive; the analyzer is simply absent, no runtime effect).

### Pre-verified headless (2026-07-06)

- **Packaging is turnkey.** `RTMPE_ANALYZER_PLACEMENT=b make package-analyzers`
  stages the four DLLs the editor needs — the analyzer (`RTMPE.SDK.Analyzers`),
  the code-fix providers (`RTMPE.SDK.CodeFixes`), and the two engines both close
  over (`RTMPE.SDK.Conversion.Core`, `RTMPE.SDK.Transforms`) — plus a
  `RoslynAnalyzer`-labelled `.meta` each, with every player platform deselected
  (`Any: enabled: 0`) and a deterministic per-DLL guid. The code-fixes are what
  raise the quick-fix lightbulb; the analyzer alone gives diagnostics with only
  the default suppress/configure menu. Verified into a throwaway directory; the
  staged DLLs are byte-identical to the current Release build.

  > The code-fix assembly references `Microsoft.CodeAnalysis.CSharp.Workspaces`,
  > which the IDE host supplies but the Unity compiler (`csc`) does not, so `csc`
  > processing it as an analyzer may log a benign "no analyzers in assembly"
  > notice — cosmetic, never a build break. Whether that notice surfaces in the
  > Unity Console, and whether the lightbulb appears, is the one thing only the
  > on-disk editor can confirm (the load-spike step below).
- **Green baseline.** Tooling solution builds Release `-warnaserror` at 0/0;
  `unity-sdk-analyzers` 182/182, `unity-sdk-codefixes` 11/11.

### Decision to settle first — analyzer pin vs. package `unity` floor (load-bearing)

**Superseded 2026-07-23 — option (B) taken.** Unity loads the analyzer into its
**own** bundled Roslyn, which must be **≥** the analyzer's metadata version. v1.9.0
had settled this with option (A): raise the package `unity` floor from `2022.3` to
`6000.0` so Unity 6's Roslyn `4.8`+ clears a `4.8.0` pin. That locked out the entire
Unity 2022.3 LTS community — the dominant LTS — whose bundled Roslyn `4.3.0` is below
`4.8.0`, so their editors rejected the analyzer with **CS9057** and surfaced no
diagnostics or code-fixes.

Option (B) is now in force: the analyzer/code-fix/toolchain pin was lowered to
**`4.3.0`** (Unity 2022.3 LTS's bundled Roslyn) and everything rebuilt. The whole
Tooling solution compiles against `4.3.0` with **zero warnings** — the definitive
proof the source uses no post-`4.3` API — and every shard passes on a `4.3.0` test
host, so the analyzer now loads and raises its quick-fix lightbulb across the full
`2022.3 → Unity 6` range. The package `unity` floor was restored to `2022.3`
accordingly. Option (C) (ship as-is, document the analyzer as absent on older Unity)
was rejected — silent absence of every diagnostic and fix is exactly the field
symptom that prompted this.

### The floor-editor session (~20 min)

Run this on a **Unity 2022.3 LTS** install. Run on the Unity 6.3 editor it still
passes — `4.3.0 ≤ 4.8` — but confirms nothing about the floor, so step 1's
verdict counts only when the `csc.dll` it prints came from a `2022.3` install.

1. `make check-roslyn-version` → confirm `4.3.0 ≤ host`; the verdict auto-appends
   above with the `csc.dll` path it measured.
2. `RTMPE_ANALYZER_PLACEMENT=b make package-analyzers` → stages `Analyzers/` + `.meta`.
3. In the editor: **zero** load warnings; `RTMPE1000` (Info) surfaces on a
   `NetworkBehaviour` subclass; a deliberately planted RPC-name collision raises
   `RTMPE1004` (Error) **before** Play Mode.
4. Build a player and confirm **both DLLs are absent** from it (RoslynAnalyzer
   role only). If the two-DLL load proves fragile, take the documented fallback —
   merge `Conversion.Core` into the analyzer assembly.

### Post-confirmation ship-diff (apply only after Steps 1–4 pass)

- `package.json`: `1.8.1` → `1.9.0` (additive minor — analyzer + IDE code-fixes;
  no runtime/wire/API break).
- Commit the verified `Analyzers/` DLLs + `.meta`.
- `CHANGELOG.md`: open `## [1.9.0] — <date>` and move the analyzer/tooling
  `### Added` bullets out of `[Unreleased]` into it; correct the now-dead "ships in
  1.2.0 / 1.3.0" clauses to "ships in this release (1.9.0)".
- `AnalyzerReleases`: move every row from `Unshipped.md` **New Rules** into
  `Shipped.md` under `## Release 1.9.0`, leaving `Unshipped.md` with its header
  only; re-run `make build-analyzers` (RS2008 must stay clean after the move).

> **Out of scope for this ship — pre-existing CHANGELOG backlog.** `[Unreleased]`
> also holds runtime changes (the Handshake/NAT fix, the `ApiKeyCipher`
> address-unbinding) that actually shipped across 1.2–1.8.1 but were never rolled
> into versioned sections. Reconcile those separately; do **not** relabel them
> 1.9.0.

### Rollback

If the editor rejects the load: `git checkout -- Packages/com.rtmpe.sdk/Analyzers*`
— there is no `Analyzers~/.gitkeep` to keep any more. No runtime or wire impact — the analyzer is
additive and its absence changes nothing at runtime.
