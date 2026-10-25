// RTMPE SDK — Runtime/Core/Sync/VariableBatchFraming.cs
//
// `HandleVariableUpdatePacket` carried two guards whose message said "rejecting
// packet" and which rejected nothing (`CORE-RD-10`):
//
//   • the cumulative-byte cap returned mid-loop, AFTER every entry before it
//     had already been deserialised and applied;
//   • the trailing-bytes check ran after the loop had finished, so by the time
//     it announced that it was refusing to let an attacker "smuggle bytes
//     through", every byte before the residue was in the object's state.
//
// A third fault had no message of its own at all: a TRUNCATED batch `break`s out
// of the loop and lands in the trailing-bytes check, which then describes bytes
// that are missing as bytes that are extra.
//
// The framing is a property of the six bytes at the head of each entry, so it
// can be settled before a single value is deserialised.  This walks it: the
// caller learns whether the batch is well-formed while its state is still
// untouched, and "rejecting packet" becomes a true sentence.
//
// The pass is `var_count` × 6 bytes with `var_count` bounded by a wire byte, so
// the worst case is 255 × 6 = 1530 bytes of skipping — against a ceiling that
// exists because deserialising the same batch can cost 64 KiB of main-thread
// work.

using System;
using System.IO;

namespace RTMPE.Core.Sync
{
    /// <summary>Why a <c>VariableUpdate</c> batch is not well-formed.</summary>
    internal enum VariableBatchFault
    {
        /// <summary>The batch frames exactly <c>var_count</c> entries and ends with the last one.</summary>
        None,

        /// <summary>
        /// An entry's header or its declared value runs past the end of the
        /// payload.  Distinct from <see cref="TrailingBytes"/>: bytes are
        /// missing, not extra, and describing one as the other sends whoever
        /// reads the log looking for the wrong sender bug.
        /// </summary>
        Truncated,

        /// <summary>
        /// The declared entries end before the payload does.  Residue is either
        /// protocol drift or an attempt to smuggle bytes past a reader that
        /// stops counting at <c>var_count</c>.
        /// </summary>
        TrailingBytes,

        /// <summary>
        /// The batch declares more value bytes than the receiver will spend
        /// main-thread work deserialising.
        /// </summary>
        OverBudget,
    }

    /// <summary>
    /// The verdict on a batch's framing, with the numbers an operator message
    /// needs so the report names what was wrong rather than that something was.
    /// </summary>
    internal readonly struct VariableBatchFraming
    {
        internal VariableBatchFault Fault { get; }

        /// <summary>Entries fully framed before the fault; all of them on <see cref="VariableBatchFault.None"/>.</summary>
        internal int EntriesFramed { get; }

        /// <summary>Bytes past the last declared entry — <see cref="VariableBatchFault.TrailingBytes"/> only.</summary>
        internal long ResidueBytes { get; }

        /// <summary>Declared bytes counted so far — <see cref="VariableBatchFault.OverBudget"/> only.</summary>
        internal int CumulativeBytes { get; }

        internal VariableBatchFraming(
            VariableBatchFault fault, int entriesFramed, long residueBytes, int cumulativeBytes)
        {
            Fault           = fault;
            EntriesFramed   = entriesFramed;
            ResidueBytes    = residueBytes;
            CumulativeBytes = cumulativeBytes;
        }
    }

    /// <summary>
    /// Settles a <c>VariableUpdate</c> batch's framing without deserialising
    /// anything, so a malformed batch can be refused whole.
    /// </summary>
    internal static class VariableBatchFramer
    {
        /// <summary>
        /// Walk <paramref name="varCount"/> entries of
        /// <c>[var_id:4][value_len:2][value_bytes:N]</c> from
        /// <paramref name="batch"/>'s current position.
        /// </summary>
        /// <remarks>
        /// ⚠️ The stream's position is RESTORED before returning, on every path
        /// including the faulting ones.  The caller's next act is to walk the
        /// same bytes again and apply them, and a rewind it has to remember is a
        /// rewind it can forget — the apply loop would then start reading from
        /// wherever the inspection stopped, which for a well-formed batch is the
        /// end and for a truncated one is the middle of an entry.
        ///
        /// 🔑 The bound tests are written in subtraction form
        /// (<c>available &lt; needed</c>) rather than as <c>position + needed &gt;
        /// length</c>, so no addition can wrap past the end of the payload and
        /// report room that is not there.
        /// </remarks>
        internal static VariableBatchFraming Inspect(
            MemoryStream batch, int varCount, int maxCumulativeBytes)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (varCount < 0) throw new ArgumentOutOfRangeException(nameof(varCount));

            long start = batch.Position;
            try
            {
                int cumulative = 0;

                for (int i = 0; i < varCount; i++)
                {
                    // var_id(4) + value_len(2)
                    if (batch.Length - batch.Position < 6)
                        return new VariableBatchFraming(VariableBatchFault.Truncated, i, 0, cumulative);

                    batch.Position += 4;                       // var_id is not framing
                    int lo = batch.ReadByte();
                    int hi = batch.ReadByte();
                    int valueLen = lo | (hi << 8);             // little-endian, matching the wire

                    if (batch.Length - batch.Position < valueLen)
                        return new VariableBatchFraming(VariableBatchFault.Truncated, i, 0, cumulative);

                    cumulative += 6 + valueLen;
                    if (cumulative > maxCumulativeBytes)
                        return new VariableBatchFraming(
                            VariableBatchFault.OverBudget, i, 0, cumulative);

                    batch.Position += valueLen;
                }

                long residue = batch.Length - batch.Position;
                if (residue != 0)
                    return new VariableBatchFraming(
                        VariableBatchFault.TrailingBytes, varCount, residue, cumulative);

                return new VariableBatchFraming(VariableBatchFault.None, varCount, 0, cumulative);
            }
            finally
            {
                batch.Position = start;
            }
        }
    }
}
