# FlatBuffers C# Runtime — Vendored Source

**Upstream:** https://github.com/google/flatbuffers
**Version:** 25.12.19 (tag `v25.12.19`)
**Licence:** Apache License 2.0 — see `LICENSE.txt` in this directory
**Upstream path:** `net/FlatBuffers/`

## Why it is vendored

Unity does not consume NuGet packages directly, and the `Google.FlatBuffers` NuGet package
references `System.Memory` in a way that conflicts with Unity's class libraries. Shipping
the runtime as source gives the SDK a self-contained serializer that:

- builds under both the Mono and IL2CPP scripting backends without changes;
- adds no package dependencies;
- stays locked to the `flatc` 25.12.19 compiler that generated the bindings under
  `../Generated/`.

## Files

| File | Purpose |
| --- | --- |
| `ByteBuffer.cs` | Backing store for reading and writing FlatBuffers data. |
| `ByteBufferUtil.cs` | Size-prefix helpers. |
| `FlatBufferBuilder.cs` | Encoder for outbound messages. |
| `FlatBufferConstants.cs` | Format constants and version checks. |
| `FlatBufferVerify.cs` | Validator for untrusted input. |
| `IFlatbufferObject.cs` | Interface implemented by every generated type. |
| `Offset.cs` | Strongly typed table offsets. |
| `Struct.cs` | Base for inline structs without a vtable. |
| `Table.cs` | Base for vtable-indexed tables. |

Each modified file carries a "Modified by RTMPE" notice, as the licence requires.

## Local hardening

This copy is not byte-identical with upstream 25.12.19. The files below carry changes that
harden the read path against malformed or hostile input. Re-apply every one of them
whenever the directory is replaced with a newer upstream release.

- `Table.cs`
  - `__string` limits a string to `MaxStringBytes` (4096 bytes), rejects embedded NUL
    bytes and decodes through `DecodeUtf8`, which uses a strict UTF-8 decoder that throws
    on malformed input instead of substituting replacement characters.
  - `__vector_len` limits a vector to `MaxVectorElements` (65536 elements) and rejects a
    negative length.
  - `__vector_as_array<T>` supports big-endian hosts through an element-by-element reader,
    `ReadTypedArrayBigEndian<T>`, instead of throwing, so a received packet cannot stop a
    big-endian build.
  - `__vector_as_span<T>` (compiled only when `ENABLE_SPAN_T` and `UNSAFE_BYTEBUFFER` are
    both defined) refuses multi-byte element types on a big-endian host with an
    `InvalidOperationException`, because a span over the buffer's own bytes cannot be
    byte-swapped; it returns an empty span for an absent vector and computes its byte
    length inside `checked`.
  - `StrictUtf8` is the strict decoder used by the `ENABLE_SPAN_T` code paths. It is
    constructed with `throwOnInvalidBytes: true` like the decoder in `ByteBuffer.cs`.
- `FlatBufferVerify.cs`
  - `Options.DEFAULT_MAX_TABLES` is 16384 instead of upstream's 1,000,000, and the
    `Options()` constructor uses it. A buffer that fits in one datagram never needs more
    tables than that, so the smaller ceiling refuses oversized table graphs early. The
    value must match `VerifiedFlatBuffer.MaxTablesPerBuffer`.
  - `CheckBufferFromStart` compares the file identifier whenever one is supplied. Upstream
    25.12.19 applies the comparison only when the identifier is empty, so a buffer written
    as a different table type would pass verification. Its length check also includes the
    start position, so a size-prefixed buffer too short to hold an identifier is refused
    before it is read. Keep this change until an upstream release applies the comparison
    whenever an identifier is supplied.
- `ByteBuffer.cs`
  - `s_strictUtf8` is a `UTF8Encoding` constructed with `throwOnInvalidBytes: true`. Every
    string decode goes through it.
  - `GetStringUTF8Strict` is an added accessor that range-checks the requested span and
    decodes it with `s_strictUtf8`. `Table.__string` reaches it through `Table.DecodeUtf8`.
  - `AssertOffsetAndLength` always performs its range check. Upstream compiles the check
    out when `BYTEBUFFER_NO_BOUNDS_CHECK` is defined; in this copy that symbol disables
    nothing, so defining it cannot turn the reader into an out-of-range read.
  - `ArraySize<T>` and `ConvertTsToBytes<T>` compute byte counts inside `checked`, so a
    very large array cannot wrap to a small count that passes the range check.
  - `ConvertBytesToTs<T>` rejects a negative byte length and always requires the length to
    be a multiple of `sizeof(T)`, so a truncated payload is refused rather than decoded
    into a shorter array.

## Updating

To move to a newer FlatBuffers release:

1. Replace the nine source files and `LICENSE.txt` with the files from upstream
   `net/FlatBuffers/` at the chosen release tag.
2. Re-apply every change listed under **Local hardening**, and keep the
   "Modified by RTMPE" notices.
3. Regenerate the bindings under `../Generated/` with the matching `flatc` version.

Keep the runtime version and the `flatc` version that generated the bindings the same.

## IL2CPP and AOT

The runtime uses `unsafe` code only inside conditional blocks for the span-based fast
path; the default Unity build is fully managed. The generated bindings call these types
directly rather than through reflection, so FlatBuffers itself needs no `link.xml`
entries.
