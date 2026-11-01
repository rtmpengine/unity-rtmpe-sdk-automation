// RTMPE SDK — Runtime/Core/WorldStateCopy.cs
//
// Carry every replicated variable of one object into the same variable of
// another, on one client — the state half of the world-authority migration.
//
// 🔑 Through the wire format, not through the values.  NetworkVariableBase
// declares Serialize and Deserialize on every variable, shipped or
// integrator-written, and nothing else it declares reaches a value generically:
// the scalar's Value is on the generic subclass, the list's elements are its
// own.  A copy written over the codecs the variables already implement covers
// every shipped type, and an integrator-written one on the same terms as long
// as its Serialize writes its state — a type of the list's shape, whose
// Serialize writes a change log, would need SerializeSnapshot overridden, and
// that member is this assembly's alone today.
//
// ⛔ Positional, and it has to be: a variable's identity is the VariableId
// hashed from its declaring type and member name (WireIdHash), which two
// instances of one prefab share member for member.  The pairing is checked —
// same runtime type, same id — and a pair that disagrees is skipped rather
// than guessed at, because a value applied to the wrong variable replicates to
// every peer as that variable's.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RTMPE.Sync;

namespace RTMPE.Core
{
    /// <summary>
    /// Copies the current state of every variable in one tracked list into
    /// the matching variable of another.
    /// </summary>
    internal static class WorldStateCopy
    {
        /// <summary>
        /// Copy each variable of <paramref name="from"/> into the variable at
        /// the same position of <paramref name="to"/>, where the two agree in
        /// runtime type and <see cref="NetworkVariableBase.VariableId"/>.
        /// </summary>
        /// <param name="from">The source object's tracked variables.</param>
        /// <param name="to">The destination object's tracked variables.  Its owner must be spawned, or every codec refuses the write.</param>
        /// <param name="skipped">How many positions were not copied: a pairing that disagreed, a null on either side, a length difference, or a codec that threw.</param>
        /// <returns>
        /// How many variables were handed to their codec.  A codec that
        /// refuses a value silently — an owner not spawned, a value it will not
        /// hold — counts as copied here, on the same terms the receive path
        /// counts an applied entry.
        /// </returns>
        public static int CopyTrackedVariables(
            IReadOnlyList<NetworkVariableBase> from,
            IReadOnlyList<NetworkVariableBase> to,
            out int skipped)
        {
            skipped = 0;
            if (from == null || to == null) return 0;

            int copied = 0;
            int pairs = Math.Min(from.Count, to.Count);
            skipped += Math.Abs(from.Count - to.Count);

            MemoryStream buffer = null;
            BinaryWriter writer = null;
            BinaryReader reader = null;
            try
            {
                for (int i = 0; i < pairs; i++)
                {
                    var source = from[i];
                    var target = to[i];
                    if (source == null || target == null
                        || source.GetType() != target.GetType()
                        || source.VariableId != target.VariableId)
                    {
                        skipped++;
                        continue;
                    }

                    if (buffer == null)
                    {
                        buffer = new MemoryStream();
                        writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
                        reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
                    }

                    // One codec at a time, and a throw in either half is that
                    // variable's alone: the object after it is still owed its
                    // state, exactly as the flush loop isolates one variable's
                    // fault from the rest of the batch.
                    try
                    {
                        buffer.SetLength(0);
                        source.SerializeSnapshot(writer);
                        writer.Flush();
                        buffer.Position = 0;
                        target.Deserialize(reader);
                        copied++;
                    }
                    catch (Exception)
                    {
                        skipped++;
                    }
                }
            }
            finally
            {
                reader?.Dispose();
                writer?.Dispose();
                buffer?.Dispose();
            }
            return copied;
        }
    }
}
