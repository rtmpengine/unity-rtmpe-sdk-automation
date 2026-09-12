# NetworkVariable Generation

The first identity-bearing layer of the conversion toolchain: it turns eligible
plain fields into `NetworkVariable<T>` members with **`nameof` wiring**.
Everything here is deterministic — no AI, no configuration, no randomness — and
every identity comes from one place: the **source itself**. A variable's wire id
is the FNV-1a 32-bit hash of `"OwnerTypeName.memberName"` — the owner's
metadata full name, or the generic definition's for a constructed generic — so the
construction the transform writes carries everything the identity depends on.

## The two conversion arms (`RTMPE2002`)

| Arm | Fires on | The fix |
|---|---|---|
| **In-place** | a plain **`private`**, non-Unity-serialized field of a mapped type on a concrete `NetworkBehaviour` | retype the field, construct it in `protected override void OnNetworkSpawn()`, rewrite every read/write to `.Value` |
| **Companion** | a **Unity-serialized** field (public, or `[SerializeField]`) of a mapped type **that gameplay code writes** | add a separate `private NetworkVariableX <name>Net;` seeded `initialValue: <configField>` — the config field itself is **never retyped** (its effective value lives in scene/prefab YAML the compiler cannot see) |

A serialized field that is only *read* is pure Inspector config and stays
silent. Names matching `key|token|secret|password` never receive the companion
suggestion — "replicate this to every client" is a security anti-suggestion.

## The closed type map

`int → NetworkVariableInt`, `float → NetworkVariableFloat`,
`bool → NetworkVariableBool`, `string → NetworkVariableString`,
`Vector2 → NetworkVariableVector2`, `Vector2Int → NetworkVariableVector2Int`,
`Vector3 → NetworkVariableVector3`, `Quaternion → NetworkVariableQuaternion`
(always seeded `Quaternion.identity`; an absent or zero-quaternion initializer
is normalised — the receive path rejects the zero rotation anyway).
`List<T>` is deferred (construction exists; usage rewriting is unstudied);
`double`/`long`/`Color`/`byte[]`/enums/user structs have no runtime wrapper.

## The id record — `<FullyQualifiedType>.rtmpe-ids.json`

A version-controlled sidecar co-located with the type's source file:

```json
{
  "schema_version": 2,
  "type": "Game.Player",
  "variables": { "_score": 3095767681 },
  "rpcs": {}
}
```

- **It records; it does not decide.** Every number here is a function of the
  type name and the member name, so deleting the file loses nothing: re-running
  the conversion writes the same bytes. What it buys is a review — an identity
  moving shows up as a diff instead of as a desync.
- **A wire break is a rename, not a deletion.** Deleting a member frees nothing
  and burns nothing; re-adding a member of the same name on the same type gets
  the same identity back, which is the property that made the old `retired` set
  and its acknowledge verb unnecessary. Renaming the type or the member is the
  break, and it needs every peer rebuilt together.
- **The record is hostile input.** The reader accepts exactly one restricted
  JSON shape and refuses everything else: unknown/duplicate keys, floats,
  arrays, out-of-range ids, a BOM, oversized files, a `type` binding that does
  not match the requesting type, and two members recorded on one identity — which
  a derivation cannot produce, so it is a corrupted or hand-edited file.
- **`schema_version` 1 is refused, not migrated.** Its numbers were issued by an
  allocator and correspond to nothing the wire now carries. Delete the file and
  re-run the conversion; the refusal says so.
- **The one fault left to report** is two constructions on one type naming the
  same member — `_shield = new NetworkVariableInt(this, nameof(_health))`, which
  the compiler is content with because both names resolve. It is refused, and
  named against both members.
- `.gitattributes` pins `*.rtmpe-ids.json` to LF so the byte-determinism the
  tests assert survives `core.autocrlf` checkouts.

## Hosts

- **`make convert FILE=<path.cs> TYPE=<Fq.Name> MEMBER=<_field[:companion]>
  [APPLY=1]`** — the headless host: prints one reviewable diff covering **both**
  the source edit and the ledger; `APPLY=1` persists them atomically (symlinked
  sidecars refused; the ledger is re-read and byte-compared at persist time so
  a concurrent writer fails the apply instead of being clobbered).
- **`make convert-batch ARGS="--file <a.cs> --type <T> --member <_f> [--type <T2> …]
  [--file <b.cs> …]" [APPLY=1]`**, or the host directly as
  `convert-batch --file … [--apply]` — N types, one plan, one diff, one commit. The
  grammar is positional: `--type` belongs to the `--file` before it and
  `--member` to the `--type` before it. Every type is planned and
  every guard runs while **nothing** has been written, and the whole set is
  committed through one transaction — every ledger, then every source, and only
  what actually changed. The alternative it replaces is the wizard looping over
  `convert`, where the fourth iteration failing leaves three types converted and
  a tree no single command produced.
  - Refused with a reason, never applied in part: a type named twice in one
    batch (each half would write the same record); a
    file named twice (the second plan would discard the first); sidecar names
    that collide case-insensitively, which are one file on a Windows or macOS
    checkout.
  - 🔑 Two types in one file do **not** contend: the shared `using RTMPE.Sync;`
    is added only when it is not already in scope, `OnNetworkSpawn` belongs to
    the type rather than the file, and the ledger is keyed on the type — so each
    type gets its own sidecar and its own id space. Measured, not assumed.
- **IDE lightbulb** — converts the field in place and names the member it is
  assigned to. It reads no record and needs none, because the edit itself
  carries the identity; it used to be offered only where a ledger already held
  an issued id, since a `CodeAction` cannot persist a sidecar atomically with a
  document edit.
- **Conversion Wizard** — the in-editor chrome over the same calls; see
  `CONVERSION_WIZARD.md`.

## What the transform refuses (fail-closed, with a reason)

Parse-error trees; `partial` types; non-`private` fields (in-place arm);
Unity-serialized fields (in-place arm); `const`, `readonly` and `static` fields
(a `const` folds into its use sites, a `readonly` cannot be assigned in
`OnNetworkSpawn`, and a `static` would share one replicated slot across every
instance — the companion arm is the safe shape for all three);
`#if` directive trivia inside the
declaration; initializers outside the allowlist (literals, `default`, the
well-known pure statics); every use that needs a storage location rather than a
value — `ref`/`out` arguments, a `ref` local or `ref` return binding, and
address-of — because the converted member is read through a property; access in
a pre-spawn context (`Awake`/`Start`/`OnEnable`/`OnValidate`/`Reset`,
constructors, other field initializers — the variable is `null` until
`OnNetworkSpawn`); any ambiguous binding (shadowing locals/parameters/patterns,
receivers other than `this`, references outside the declaring type — **except**
where the other type declares a member of that name and reads it as a bare
identifier, which binds in its own type's scope and cannot reach this private
one; a member **access** is still refused, because `outer.inner._f` does cross a
nesting boundary);
`[NetworkVariable]`-attributed fields; companion-name collisions, counting
nested types and delegates, events in either form, and the enclosing type's own
name. A refusal is a whole-type no-op — never a partial edit.

## Source-readiness only

The source-diff-only caveat applies unchanged — see
[`CONVERSION_WIZARD.md`](CONVERSION_WIZARD.md): the conversion sees source, not
scenes.
Inspector values, prefab wiring, and string-keyed dispatch are outside every
static check here — the human diff approval covers source drift only.
