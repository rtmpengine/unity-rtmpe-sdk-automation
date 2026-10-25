// RTMPE SDK — Runtime/Sync/VariableBatchBuilder.cs
//
// Coalesces variable updates across multiple owned objects into a single
// per-tick batch packet.  Eliminates the per-packet ~61-byte tax (IP+UDP 28B
// + RTMPE header 13B + AEAD 20B) that dominates traffic when many small
// variable deltas leave the client each tick.
//
// Wire format (all fields little-endian):
//
//   [0]      count : u8                — number of entries in this batch
//   [1..]    entries × count where each entry is:
//     [0..1] entry_len : u16           — total bytes of the entry payload
//     [2..]  entry_payload : N bytes   — the bytes produced by the legacy
//                                        per-object VariableUpdate builder
//                                        (object_id + tick + var_count + N
//                                        × {var_id, value_len, value})
//
// Each entry is a legacy 0x41 payload verbatim, so a gateway that
// understands batching can split the batch and replay the inner payloads
// through the existing 0x41 dispatcher.  The batch packet uses a new
// PacketType (VariableBatchUpdate, 0x44) so an old gateway sees an
// unrecognised packet and drops it; clients pre-filter on
// NetworkSettings.enableVariableBatching so a gateway that has not opted
// in never receives the new type.

using System;

namespace RTMPE.Sync
{
    /// <summary>
    /// Combines the variable updates of several objects into one payload. Used
    /// by the SDK when <c>NetworkSettings.enableVariableBatching</c> is on; not
    /// intended to be called from game code.
    /// </summary>
    public static class VariableBatchBuilder
    {
        /// <summary>The most entries a batch can describe.</summary>
        public const int MaxEntries = byte.MaxValue;

        /// <summary>The size, in bytes, of a batch's header.</summary>
        public const int BatchHeaderBytes = 1;

        /// <summary>The size, in bytes, of each entry's header.</summary>
        public const int EntryHeaderBytes = 2;

        /// <summary>
        /// The number of bytes an entry of <paramref name="payloadLength"/> bytes
        /// takes in a batch, header included.
        /// </summary>
        /// <param name="payloadLength">The entry's size, in bytes.</param>
        /// <returns>The entry's size in the batch, in bytes.</returns>
        public static int EntryWireSize(int payloadLength) => EntryHeaderBytes + payloadLength;

        /// <summary>
        /// The most entries the server accepts in one batch; it drops a batch
        /// with more. The SDK never puts more than this into one batch.
        /// </summary>
        public const int GatewayEntryCap = 64;

        /// <summary>
        /// Limits a configured batch size to the range 1 to
        /// <see cref="GatewayEntryCap"/>. Updates beyond the limit are sent in
        /// further batches.
        /// </summary>
        /// <param name="configured">The configured number of entries per batch.</param>
        /// <returns>The number of entries per batch the SDK uses.</returns>
        public static int ClampBatchCap(int configured)
        {
            if (configured < 1) return 1;
            if (configured > GatewayEntryCap) return GatewayEntryCap;
            return configured;
        }

        /// <summary>
        /// The largest entry, in bytes, a batch may hold. <see cref="Build"/>
        /// refuses a larger entry and <see cref="TryParse"/> refuses a batch that
        /// holds one; send such an update on its own instead.
        /// </summary>
        public const int MaxEntryPayloadBytes = 16 * 1024;

        /// <summary>
        /// Builds a batch of the first <paramref name="count"/> entries of
        /// <paramref name="payloads"/> in a new array.
        /// </summary>
        /// <param name="payloads">The entries; a <see langword="null"/> entry is written as empty.</param>
        /// <param name="count">The number of entries to include.</param>
        /// <returns>The batch.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payloads"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or larger than the array.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="count"/> is larger than <see cref="MaxEntries"/>, or an
        /// entry is larger than <see cref="MaxEntryPayloadBytes"/>.
        /// </exception>
        public static byte[] Build(byte[][] payloads, int count)
        {
            int total = ComputeTotalSize(payloads, count);
            var result = new byte[total];
            BuildInto(result, 0, payloads, count);
            return result;
        }

        /// <summary>
        /// Returns the size, in bytes, of the batch <see cref="BuildInto"/> writes
        /// for the first <paramref name="count"/> entries of
        /// <paramref name="payloads"/>. It throws in the same cases as
        /// <see cref="Build"/>.
        /// </summary>
        /// <param name="payloads">The entries.</param>
        /// <param name="count">The number of entries to include.</param>
        /// <returns>The batch size in bytes.</returns>
        public static int ComputeTotalSize(byte[][] payloads, int count)
        {
            if (payloads == null) throw new ArgumentNullException(nameof(payloads));
            if (count < 0 || count > payloads.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count > MaxEntries)
                throw new ArgumentException(
                    $"variable batch holds at most {MaxEntries} entries (got {count}); " +
                    "split into multiple batches at the call site.",
                    nameof(count));

            int total = BatchHeaderBytes; // count byte
            for (int i = 0; i < count; i++)
            {
                int len = payloads[i] != null ? payloads[i].Length : 0;
                if (len > MaxEntryPayloadBytes)
                    throw new ArgumentException(
                        $"variable batch entry {i} is {len} bytes — a batched entry " +
                        $"is capped at the {MaxEntryPayloadBytes}-byte working ceiling " +
                        "(see MaxEntryPayloadBytes); an entry above the ceiling must " +
                        "be sent as a standalone VariableUpdate.",
                        nameof(payloads));
                total += EntryWireSize(len); // length prefix + payload
            }
            return total;
        }

        /// <summary>
        /// Writes a batch of the first <paramref name="count"/> entries of
        /// <paramref name="payloads"/> into <paramref name="dest"/> at
        /// <paramref name="destOffset"/>. It throws in the same cases as
        /// <see cref="Build"/>, and when <paramref name="dest"/> is too small.
        /// </summary>
        /// <param name="dest">The buffer to write to, for example one rented from <c>ArrayPool&lt;byte&gt;.Shared</c>.</param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="payloads">The entries.</param>
        /// <param name="count">The number of entries to include.</param>
        /// <returns>The number of bytes written (see <see cref="ComputeTotalSize"/>).</returns>
        public static int BuildInto(byte[] dest, int destOffset, byte[][] payloads, int count)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int total = ComputeTotalSize(payloads, count);
            if (destOffset < 0 || (long)destOffset + total > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a variable batch payload at the given offset.");

            dest[destOffset] = (byte)count;
            int off = destOffset + BatchHeaderBytes;
            for (int i = 0; i < count; i++)
            {
                int len = payloads[i] != null ? payloads[i].Length : 0;
                dest[off]     = (byte)(len);
                dest[off + 1] = (byte)(len >> 8);
                off += EntryHeaderBytes;
                if (len > 0)
                {
                    Buffer.BlockCopy(payloads[i], 0, dest, off, len);
                    off += len;
                }
            }
            return total;
        }

        /// <summary>
        /// Reads a batch and passes a copy of each entry to
        /// <paramref name="dispatch"/>, in order.
        /// </summary>
        /// <param name="batch">The batch to read.</param>
        /// <param name="dispatch">Called once for each entry.</param>
        /// <returns>
        /// The number of entries, or -1 when the batch is malformed. Entries read
        /// before a fault is found have already been passed on.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="dispatch"/> is <see langword="null"/>.</exception>
        public static int TryParse(byte[] batch, Action<byte[]> dispatch)
        {
            if (batch == null || batch.Length < 1) return -1;
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));

            int count = batch[0];

            // Pre-flight allocation guard.  A malicious sender setting
            // count = 255 with each entry claiming the maximum 65 535-byte
            // payload would otherwise force the dispatcher to attempt
            // ~16 MiB of `new byte[]` allocations — well above any
            // legitimate batch.  Reject early when the declared count
            // cannot possibly fit in the remaining bytes (each entry
            // requires at least the 2-byte length prefix).
            int minBytesNeeded = 1 + count * 2;
            if (minBytesNeeded > batch.Length) return -1;

            int off = 1;
            for (int i = 0; i < count; i++)
            {
                if (off > batch.Length - 2) return -1;
                int len = batch[off] | (batch[off + 1] << 8);
                off += 2;
                if (len > batch.Length - off) return -1;
                // Per-entry working ceiling.  See MaxEntryPayloadBytes
                // remarks: a structurally-legal 65 535-byte entry is
                // allocation-amplification when 255 of them appear in a
                // single batch.  Reject before the per-entry alloc.
                if (len > MaxEntryPayloadBytes) return -1;

                var inner = new byte[len];
                if (len > 0)
                    Buffer.BlockCopy(batch, off, inner, 0, len);
                off += len;
                dispatch(inner);
            }

            // Strict trailing-bytes check.  A well-formed batch ends
            // exactly at the last entry's payload; trailing residue is a
            // protocol-drift / smuggling signal.  Returning -1 surfaces
            // the anomaly to the caller (which the receive path logs)
            // instead of silently accepting the partial parse.
            if (off != batch.Length) return -1;
            return count;
        }
    }
}
