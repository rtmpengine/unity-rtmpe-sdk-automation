// RTMPE SDK — Runtime/Core/Sync/VariableEntryBounds.cs
//
// One entry of a VariableUpdate (0x41) batch is
// `[var_id:4][value_len:2][value_bytes:N]`, and until this existed the receiver
// passed `value_len` down and never enforced it.  `Deserialize` was handed the
// BATCH reader and took whatever its type wanted:
//
//   • a type that reads more than the entry declared consumed the NEXT entry's
//     bytes as its own value.  Silently — the dispatch loop re-seeks to
//     `valueStart + valueLen` afterwards, so the framing was repaired and
//     nothing was left to notice;
//   • a type that read past the end of the batch threw, and the throw escaped
//     the loop, so the whole datagram went with it — every other variable in it
//     included.
//
// This file is compiled by a test project; the dispatch loop that uses it lives
// in a NetworkManager partial that no project compiles, so the binding between
// the two is held by a source rule instead.

using System;
using System.IO;

namespace RTMPE.Core.Sync
{
    /// <summary>
    /// Hands a <c>VariableUpdate</c> receiver a reader that cannot leave the
    /// entry it was opened for, and cannot carry anything out of it either.
    /// </summary>
    internal sealed class VariableEntryReader
    {
        // One instance per NetworkManager, reused across every entry of every
        // packet.  ⚠️ That makes it shared mutable state where each packet
        // previously owned its own reader: it is safe because the receive path
        // is main-thread only (the transport enqueues and the dispatcher drains
        // on the main thread), and it would NOT survive a re-entrant drain from
        // inside a Deserialize-triggered callback — the outer entry's scratch
        // would be overwritten mid-read.  Nothing does that today; it is written
        // down because the previous shape could not have gone wrong that way.
        private byte[]       _scratch = Array.Empty<byte>();
        private MemoryStream _stream;

        /// <summary>
        /// A reader over exactly <paramref name="valueLen"/> bytes of
        /// <paramref name="batch"/> starting at <paramref name="valueStart"/>,
        /// an offset in the batch stream's own coordinates.
        /// </summary>
        /// <remarks>
        /// 🔑 It takes the STREAM rather than the payload array, and that is the
        /// point.  The batch stream is built over the payload at an offset — the
        /// packet header — so a call site handing over the array has to add that
        /// header back, and one that forgets reads the wrong bytes entirely and
        /// cannot be caught by any rule over a partial no test project compiles.
        /// `MemoryStream.Read` already works in the stream's coordinates, so the
        /// arithmetic does not exist to get wrong.
        ///
        /// 🚨 The <see cref="BinaryReader"/> is built fresh here and the stream
        /// and buffer behind it are not.  That asymmetry is deliberate and was
        /// paid for: `BinaryReader` creates a UTF-8 <c>Decoder</c> in its
        /// constructor and never resets it, so a `ReadString` that ends on an
        /// incomplete sequence leaves those bytes INSIDE the reader and prepends
        /// them to the next call.  Pooled across entries, one variable's
        /// trailing bytes decoded into the next variable's string — on a
        /// different object, in a different datagram.  That is this finding's own
        /// property violated inside its repair, and it was measured, not
        /// reasoned about: `[0x02,0xE2,0x82]` then `[0x01,0xAC]` yields "€" from
        /// the reused reader and U+FFFD from a fresh one.
        ///
        /// The batch stream's own position is left where the read ended; the
        /// dispatch loop re-seeks to the next entry regardless, and restoring it
        /// here would be a second place that has to agree about where that is.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The entry is not wholly inside the batch.  The caller bounds-checks
        /// it first; this refuses rather than trusts.
        /// </exception>
        internal BinaryReader Open(MemoryStream batch, long valueStart, ushort valueLen)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (valueStart < 0 || valueStart > batch.Length)
                throw new ArgumentOutOfRangeException(nameof(valueStart),
                    "the entry does not start inside the batch");
            if (valueLen > batch.Length - valueStart)
                throw new ArgumentOutOfRangeException(nameof(valueLen),
                    "the entry does not end inside the batch");

            // `_stream == null` as well as the size test. 🚨 Without it a first
            // call with valueLen 0 — a legal entry — leaves the scratch big
            // enough by arithmetic (0 < 0 is false) and the stream never built,
            // so the next line dereferences null.
            if (_stream == null || _scratch.Length < valueLen)
            {
                // Doubled, not sized to the entry. 🚨 Growing to exactly what
                // arrived means a sender ramping 1, 2, 3 … 255 bytes inside ONE
                // datagram reallocates on every entry — measured at 255 buffers
                // and 255 streams per packet, a GC channel the receiver's
                // cumulative-byte cap does not bound because none of it is
                // deserialised work.
                int size = Math.Max(valueLen, Math.Max(_scratch.Length * 2, 64));
                _scratch = new byte[size];
                _stream?.Dispose();
                _stream = new MemoryStream(_scratch, 0, _scratch.Length, writable: true);
            }

            // ⚠️ Length BEFORE the copy, always.  SetLength zero-fills when it
            // GROWS, so setting it after the copy erases what was just copied —
            // and the scratch buffer is reused, so the bytes it erased would be
            // the PREVIOUS entry's, which is a plausible-looking value rather
            // than an obvious one.
            _stream.SetLength(valueLen);

            // The bounds check above establishes that the batch holds the whole
            // entry, so this cannot come up short.  A guard here as well was
            // written first and deleted: it refused with the same exception and
            // the same parameter, so no test could tell the two apart — and a
            // line indistinguishable from its own absence is not a guard.
            batch.Position = valueStart;
            batch.Read(_scratch, 0, valueLen);

            _stream.Position = 0;

            // ⚠️ `leaveOpen: true`, and it is load-bearing rather than tidy.
            // The stream behind this reader is a FIELD, reused for every entry
            // of every packet for the life of the NetworkManager — but what is
            // handed out is a `BinaryReader`, which owns its stream by default.
            // One `using var entryReader = …Open(…)` at the call site — the most
            // idiomatic line a maintainer could write, and the one the IDE's own
            // analyser suggests — disposes it, and the next `Open` finds
            // `_stream != null` with the scratch already big enough, so it never
            // rebuilds: measured, entry 0 reads correctly and every entry after
            // it for the process's lifetime throws `NotSupportedException`,
            // surfacing only as a rate-gated line blaming the user's own
            // `Deserialize`. It self-heals only if an entry larger than the
            // high-water mark arrives, and then breaks again.
            //
            // `Encoding.UTF8` is what `new BinaryReader(stream)` passes, so the
            // decoding behaviour is unchanged; only the ownership is.
            return new BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
        }
    }
}
