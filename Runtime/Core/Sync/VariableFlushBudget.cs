// RTMPE SDK — Runtime/Core/Sync/VariableFlushBudget.cs
//
// One variable's step of the 30 Hz flush: append it to the outbound payload, or
// leave the payload exactly as it was.
//
// The flush appends each dirty variable and then marks it clean, so the value is
// forgotten at the moment it is written — before anything has established that
// the bytes can be sent.  They cannot always be: PacketBuilder refuses a payload
// over MaxApplicationPayloadBytes rather than let IP fragmentation carry it, and
// a NetworkVariableList reaches that on ordinary content (284 ints is already
// past it).  The send guards contain the refusal so the tick survives, and the
// update is then lost with nothing to re-send it — a NetworkVariableList's op
// log is destroyed by MarkClean, and no path re-sends an unchanged variable.
//
// So the decision has to be taken BEFORE the variable is marked clean, and a
// variable that does not fit has to leave the payload byte-identical to what it
// was — otherwise the partial write travels as a truncated value.
//
// This lives here, and not inline in the flush loop, because NetworkBehaviour is
// a MonoBehaviour partial that no test project compiles: inline, the rule could
// only ever be asserted against its own source text.
//
// ⛔ Two costs the design accepts, stated so the next reader does not have to
// measure them:
//
//   * A variable that can never fit is offered, serialised and retracted on
//     every tick it is eligible.  A 10 000-element NetworkVariableListInt is
//     40 KB of writer work at 30 Hz — 1.2 MB/s — and the flush stream keeps the
//     capacity it grew to, because SetLength does not return it.  Keeping the
//     value is still right: the alternative is marking it clean and losing it
//     silently, which is the defect this exists for, and the rate-limited
//     report names the variable so the developer can shrink it.
//   * The caller's stream must have Position == Length on entry.  The
//     measurement is against Length (the payload is taken from the high-water
//     mark) and the retraction is to Position, so a stream that had been sought
//     backwards would report Written without growing, and the retraction would
//     truncate live bytes.  NetworkBehaviour's flush satisfies this — it clears
//     with SetLength(0) and does its one seek-back after the loop — and there
//     is no other caller.

using System.IO;
using RTMPE.Sync;

namespace RTMPE.Core.Sync
{
    /// <summary>
    /// The per-variable append step of <c>NetworkBehaviour.FlushDirtyVariables</c>.
    /// </summary>
    internal static class VariableFlushBudget
    {
        /// <summary>What happened to the variable offered to <see cref="TryAppend"/>.</summary>
        internal enum Outcome
        {
            /// <summary>Appended; the caller marks it clean.</summary>
            Written,

            /// <summary>
            /// It did not fit alongside what is already in the payload. The
            /// payload is unchanged and the variable is untouched, so it stays
            /// dirty and is offered again on the next tick — by which point it
            /// may be first, and fit.
            /// </summary>
            Deferred,

            /// <summary>
            /// It did not fit even as the only variable in the payload, so no
            /// later tick will change the answer. The payload is unchanged and
            /// the variable is untouched; the caller reports it.
            /// </summary>
            TooLargeAlone,
        }

        /// <summary>
        /// Append <paramref name="variable"/> to <paramref name="stream"/> if the
        /// result still fits <paramref name="maxPayloadBytes"/>; otherwise leave
        /// the stream exactly as it was found.
        /// </summary>
        /// <param name="payloadIsEmptySoFar">
        /// True when no variable has been appended yet, which is what separates
        /// "wait for a tick of its own" from "will never fit".
        /// </param>
        internal static Outcome TryAppend(
            MemoryStream       stream,
            BinaryWriter       writer,
            NetworkVariableBase variable,
            bool               payloadIsEmptySoFar,
            int                maxPayloadBytes)
        {
            long before = stream.Position;

            variable.SerializeWithId(writer);

            // BinaryWriter over a MemoryStream does not buffer today, but the
            // length read below is the whole decision and a future buffering
            // implementation would make it read short.
            writer.Flush();

            if (stream.Length <= maxPayloadBytes) return Outcome.Written;

            // Retract the partial write.  Position alone is not enough: the
            // payload is taken from the stream's LENGTH (its high-water mark),
            // so rewinding only the cursor would send the rejected bytes and
            // then overstate var_count against them.
            stream.SetLength(before);
            // Redundant today — SetLength clamps a Position past the new length —
            // and kept because the next line of this method depends on the two
            // being equal, and a clamp is a property of MemoryStream rather than
            // of this code.
            stream.Position = before;

            return payloadIsEmptySoFar ? Outcome.TooLargeAlone : Outcome.Deferred;
        }
    }
}
