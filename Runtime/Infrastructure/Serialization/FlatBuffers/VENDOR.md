# FlatBuffers C# Runtime — Vendored Source

**Upstream:** https://github.com/google/flatbuffers
**Version:** 25.12.19 (commit tag `v25.12.19`)
**License:** Apache License 2.0 (see `LICENSE.txt` in this directory)
**Source path:** `net/FlatBuffers/` in the upstream repo

## Why vendored

Unity does not consume `dotnet` NuGet packages directly, and the `Google.FlatBuffers` package on NuGet pulls in `System.Memory` references that conflict with Unity's `mscorlib`. Vendoring the 9 hand-picked source files lets the SDK ship with a single self-contained runtime that:

- Builds in IL2CPP and Mono backends without modification
- Has no transitive package dependencies
- Stays version-locked to the `flatc 25.12.19` compiler that emitted the bindings under `../Generated/`

## Files

| File | Purpose |
|---|---|
| `ByteBuffer.cs` | Backing store for read/write of FB messages (1143 lines) |
| `ByteBufferUtil.cs` | Size-prefix helpers (39 lines) |
| `FlatBufferBuilder.cs` | Fluent encoder for outbound messages (1038 lines) |
| `FlatBufferConstants.cs` | Magic numbers, version checks (37 lines) |
| `FlatBufferVerify.cs` | Untrusted-input validator — required for any client-controlled payload |
| `IFlatbufferObject.cs` | Marker interface implemented by every generated type |
| `Offset.cs` | Strongly-typed table offsets |
| `Struct.cs` | Base for inline (no-vtable) structs (Vec3, Quaternion) |
| `Table.cs` | Base for vtable-indexed tables (PlayerState, InputPayload, …) |

Total: ~133 KiB of source; zero new package references.

## Local hardening

The vendored copy is **not byte-identical** with upstream `25.12.19`. The following
files contain receive-path security hardening that must be carried forward whenever
this directory is re-vendored:

- `Table.cs`
  - `__string` now caps length at `MaxStringBytes = 4096`, decodes with a
    strict UTF-8 decoder that throws on malformed input, and rejects
    embedded NUL bytes.
  - `__vector_len` now caps length at `MaxVectorElements = 65536` and
    rejects negative wire values.
  - `__vector_as_array<T>` no longer throws `NotSupportedException` on
    big-endian hosts; it falls back to an endian-aware element-by-element
    reader (`ReadTypedArrayBigEndian<T>`) so a single received packet cannot
    crash a BE-target build.
  - `__vector_as_span<T>` still refuses a big-endian host — for `elementSize > 1`
    only; the byte-vector fast path is kept deliberately. A `Span<T>` over the
    buffer's own bytes cannot be byte-swapped without copying, which is the one
    thing the span accessor exists to avoid. What changed is the diagnostic
    (`InvalidOperationException` naming the requirement rather than
    `NotSupportedException`), its position (upstream threw at method entry, so
    an absent vector now returns an empty span where upstream threw), and the
    `checked` on `len * elementSize`.
    ⚠ This accessor sits behind `ENABLE_SPAN_T`, which no project here defines,
    so nothing in this repository compiles it. Re-apply it anyway: the symbol is
    a build choice a consumer can make, and the delta must survive that choice.
    ⚠ An earlier revision of this file said both accessors fall back. They do
    not, and a reader who believed it would have re-applied the wrong delta.
- `FlatBufferVerify.cs`
  - `Options.DEFAULT_MAX_TABLES` is `16384`, not upstream's `1_000_000`. The
    upstream ceiling is calibrated for arbitrarily large file-backed buffers;
    every payload here is bounded by the UDP MTU, and a caller that forgets to
    pass explicit options must not fall back to a cap that admits a table-bomb.
    It must stay in lockstep with `VerifiedFlatBuffer.MaxTablesPerBuffer`, which
    `scripts/check-flatbuffers-vendor-hardening.sh` reads alongside this one.
    ⚠ Re-apply the **use** as well as the constant: `Options()` assigns
    `max_tables = DEFAULT_MAX_TABLES`, and a ceiling nothing reads is the
    upstream ceiling. Held by
    `TheDefaultVerifierOptionsCarryTheLoweredTableCeiling`.
  - `CheckBufferFromStart` compares the file identifier when one is supplied.
    Upstream 25.12.19 gates that comparison on `identifier.Length == 0` — the
    inverse of what `VerifyBuffer` documents — so a supplied identifier was
    never compared, and the only case that could reach the comparison passed
    `BufferHasIdentifier` a length it throws on. A buffer written as any other
    table was accepted as `NetworkVariableUpdateV2`. This is an **upstream**
    defect, not a transcription error here; the delta can be retired once an
    upstream release carries `identifier.Length != 0`.
    ⚠ Re-applying this is not optional: the identifier is the only field on
    the wire that states which table the sender believed it was writing.
  - The same statement's length pre-check reads
    `verifier_buffer.Length < (startPos + SIZE_U_OFFSET + FILE_IDENTIFIER_LENGTH)`.
    Upstream omits `startPos`, which is correct only for a non-size-prefixed
    buffer; with `sizePrefixed: true` the check admits a buffer four bytes too
    short and `BufferHasIdentifier` then indexes past its end.
    Held by `ASizePrefixedBufferTooShortForItsIdentifierIsRefusedNotRead` in
    `tests/unit/unity-sdk-flatbuffers-hardening`, which drives an eight-byte
    size-prefixed buffer through `VerifyBuffer` and asserts the refusal is a
    RETURN rather than a read past the end — the two forms both refuse, and
    only how they refuse tells them apart.
    ⚠ This bullet sat between another bullet and its "Held by" line, and so
    read as covered by it. It was not: neither of the two holders named below
    refuses this delta, and an adversarial pass removed it with both of them
    green. A bullet inserted above an attribution silently acquires it.
  - The identifier comparison, and only it, is held by
    `tests/unit/unity-sdk-flatbuffers-union/FileIdentifierEnforcementTests.cs`,
    which drives a wrong-identifier buffer through the receive gate and asserts
    the refusal, and by `scripts/check-flatbuffers-file-identifier.sh`, which
    reads the predicate out of this method with comments stripped — and which
    now also requires the declaration to be unique, because its window opens at
    the first match and a decoy overload above the real one moved it.
- `ByteBuffer.cs`
  - `s_strictUtf8` is the `UTF8Encoding` every live decode goes through, and it
    is constructed `throwOnInvalidBytes: true`. Upstream has no such field.
  - `AssertOffsetAndLength` performs an unconditional in-buffer range check.
    Upstream compiles it out under `BYTEBUFFER_NO_BOUNDS_CHECK`; here the
    symbol reaches no `#if` at all, so the flag opts out of nothing and the
    reader cannot be turned into an arbitrary-read primitive by defining it.
    ⚠ This bullet used to say the flag "now only opts out of strict alignment /
    multiple-of-T validation". It does not: `ConvertBytesToTs`'s check is
    unconditional too, and no conditional on that symbol survives in the file.
    Held by `tests/unit/unity-sdk-flatbuffers-hardening`, the one project that
    compiles this directory with the symbol defined.
  - `GetStringUTF8Strict` is a new accessor, reached from `Table.__string`
    through `Table.DecodeUtf8`. It range-checks and then decodes with
    `s_strictUtf8` — a `UTF8Encoding` constructed `throwOnInvalidBytes: true`,
    which is the whole of the delta: without that argument the encoding
    substitutes U+FFFD and a malformed sequence is accepted in silence.
    ⚠ `Table.StrictUtf8` is a second such instance and is **not** the one that
    runs: it is reached only from the `ENABLE_SPAN_T` arms, and that symbol is
    defined by no project here. An earlier revision of this bullet named it as
    the live decoder.
    ⛔ Its **declaration**, however, is compiled by every configuration — only
    its callers are dead — so it must carry `throwOnInvalidBytes: true` too, and
    it is stated as a field rather than as dead code. It had been recorded as
    dead, where the rule holding it tested for a method-like member and was
    therefore false whatever the source said: an always-false test is an empty
    domain, and an empty domain deletes a rule rather than failing it.
  - `ArraySize<T>` computes its byte count inside `checked`. Without it a
    sufficiently large array wraps to a small positive count, which then passes
    `AssertOffsetAndLength` at the call site.
    ⚠ Three overloads carry it and only **two** are compiled here; the
    `Span<T>` one sits behind `ENABLE_SPAN_T`. This bullet used to say "all
    three overloads", which reads as three deltas held and is two.
    ⛔ No test reaches this one: overflowing the multiply needs an array of 2²⁸
    longs. `scripts/check-flatbuffers-vendor-hardening.sh` is its only holder,
    and it asserts that every multiplication in the body is inside the block —
    not that the token appears, which `int t = checked(0);` satisfies.
  - `ConvertTsToBytes<T>` is `checked` for the same reason, on the typed-element
    multiply.
  - `ConvertBytesToTs<T>` rejects a negative byte length and enforces the
    multiple-of-`sizeof(T)` check unconditionally. Upstream gates that check on
    `BYTEBUFFER_NO_BOUNDS_CHECK`; compiled out, integer division truncates a
    non-multiple length and hands the caller a typed array that ends before the
    wire payload does, with nothing saying so.

When upgrading, re-apply these changes by hand or rebase the upstream patch
against this directory before the vendored copy is overwritten.

## Updating

Re-vendoring the FlatBuffers runtime is a maintainer task. To upgrade:
replace the nine source files in this directory (and `LICENSE.txt`) with the
matching files from the upstream `net/FlatBuffers/` directory at the chosen
release tag, re-apply the **Local hardening** changes listed above, then
regenerate the bindings under `../Generated/`.

The vendored version must stay locked to the `flatc` compiler version that
emitted those bindings, and to the FlatBuffers version used by the gateway
and the shared contract definitions, so every runtime stays byte-compatible
on the wire.

## IL2CPP / AOT note

The vendored runtime uses `unsafe` only inside guarded `#if` blocks (Span<byte>
fast-path); the default Unity build path is fully managed.  Generated bindings
under `../Generated/` reference these types via direct calls, NOT reflection,
so no `link.xml` preservation is required for FlatBuffers itself.  If a future
schema introduces `[union]` types the IL2CPP linker may strip the union
discriminator dispatch — re-evaluate then.
