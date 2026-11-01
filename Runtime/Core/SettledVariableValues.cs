// RTMPE SDK — Runtime/Core/SettledVariableValues.cs
//
// What the VariableUpdate hold lets go of for an object not spawned here,
// kept as each variable's last few entries until the object spawns.

using System;
using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// The entries of VariableUpdate frames the hold evicted or aged out for an
    /// object that has not spawned here, kept per variable — the last
    /// <see cref="EntriesPerVariable"/> of each — and handed back, in the order
    /// they arrived, when the object spawns.
    /// </summary>
    /// <remarks>
    /// <para>The hold keeps whole frames for ten seconds, and the frames past an
    /// object's protected head go first when a stream fills its share — right
    /// for a late joiner, whose objects spawn within a join. A world handed to
    /// this client as the room's new host can spawn here minutes after the
    /// snapshot that carried its values: by then the frames are gone, the world
    /// holds the prefab's seed for every value not written since, and it is born
    /// again over the state every other player holds. What the frames carried
    /// is still the room's state, and a variable needs only its last entries to
    /// say it.</para>
    /// <para>A value is opaque here — the object, and so the type of each
    /// variable, is unknown until it spawns — so a list's full sync cannot be
    /// told from its edits. Each variable keeps its last entries rather than its
    /// last one: a scalar ends at the newest, and a list is whole again when its
    /// last full sync is among them; when it is not, the list is not delivered,
    /// and a world that needed it is born as it was before.</para>
    /// <para>The frames are written by a peer, so every step costs the same
    /// whatever they carry. A variable is bounded in entries and in bytes and
    /// trims only its own history; an object keeps the first
    /// <see cref="VariablesPerObject"/> variables it is given — the late-join
    /// snapshot's, which arrive first — and refuses the rest, so no variable's
    /// last value is given up for another's; and past the whole the object
    /// folded into least recently goes. Bytes are charged for what holds an
    /// entry as well as for the entry. Confined to the receive (main) thread,
    /// as the hold is.</para>
    /// </remarks>
    internal sealed class SettledVariableValues
    {
        /// <summary>The most entries kept for one variable; past it its oldest goes.</summary>
        internal const int EntriesPerVariable = 8;

        /// <summary>
        /// The most bytes kept for one variable, overhead included; past it its
        /// oldest goes, and its newest is always kept.
        /// </summary>
        internal const int PerVariableMaxBytes = 4 * 1024;

        /// <summary>
        /// The largest entry kept: larger than any a peer's frame can carry
        /// through the gateway's relay, and small enough that a variable holds
        /// its newest within <see cref="PerVariableMaxBytes"/>.
        /// </summary>
        internal const int MaxEntryBytes = 2 * 1024;

        /// <summary>The most variables kept for one object; an entry for one past it is refused.</summary>
        internal const int VariablesPerObject = 64;

        /// <summary>The most bytes kept in all, overhead included; past it the object folded into least recently goes.</summary>
        internal const long MaxBytes = 1L << 20;

        // What holds an entry, a variable and an object on a 64-bit runtime,
        // charged against the bounds with the entry bytes: an entry's object
        // and array; a variable's queue and its slot; an object's record, its
        // maps, its header copy and its place in the recency list.
        internal const int EntryOverhead    = 80;
        internal const int VariableOverhead = 160;
        internal const int ObjectOverhead   = 512;

        // The packet header every received frame carries ahead of its payload.
        private const int HeaderBytes = 13;

        // object_id(8) + tick(4) + var_count(1)
        private const int PayloadPrefix = 13;

        // var_id(4) + value_len(2)
        private const int EntryPrefix = 6;

        private sealed class Entry
        {
            public uint   Tick;
            public byte[] Bytes;   // [var_id:4][value_len:2][value]
            public long   Ordinal;
        }

        private sealed class Variable
        {
            public readonly Queue<Entry> Entries = new Queue<Entry>();
            public long Bytes;
        }

        private sealed class Folded
        {
            public byte[] Header;  // the packet header of a frame folded for the object
            public int    RoomEntry;
            public long   Bytes;
            public LinkedListNode<ulong> Recency;
            public readonly Dictionary<uint, Variable> ByVariable = new Dictionary<uint, Variable>();
        }

        private readonly Dictionary<ulong, Folded> _objects = new Dictionary<ulong, Folded>();
        // Objects in the order they were last folded into, least recent first.
        private readonly LinkedList<ulong> _recency = new LinkedList<ulong>();
        private long _bytes;
        private long _nextOrdinal;

        /// <summary>Objects with entries kept.</summary>
        internal int ObjectCount => _objects.Count;

        /// <summary>The bytes the kept entries and what holds them occupy.</summary>
        internal long Bytes => _bytes;

        /// <summary>
        /// Keep the entries of <paramref name="frame"/>, a whole VariableUpdate
        /// packet held for <paramref name="objectId"/> under room entry
        /// <paramref name="roomEntry"/> that the hold is letting go of.  A frame
        /// that does not read back is let go whole, as the apply path would
        /// refuse it.
        /// </summary>
        internal void Fold(ulong objectId, byte[] frame, int roomEntry)
        {
            if (!TryRead(frame, out uint tick, out int count)) return;

            if (_objects.TryGetValue(objectId, out var folded) && folded.RoomEntry != roomEntry)
            {
                // Another room's values are not this one's.
                Forget(objectId, folded);
                folded = null;
            }
            if (folded == null)
            {
                folded = new Folded { RoomEntry = roomEntry, Header = new byte[HeaderBytes] };
                Buffer.BlockCopy(frame, 0, folded.Header, 0, HeaderBytes);
                folded.Recency = _recency.AddLast(objectId);
                _objects[objectId] = folded;
                Charge(folded, ObjectOverhead);
            }
            else
            {
                _recency.Remove(folded.Recency);
                _recency.AddLast(folded.Recency);
            }

            int at = HeaderBytes + PayloadPrefix;
            for (int i = 0; i < count; i++)
            {
                uint varId  = ReadU32(frame, at);
                int  length = frame[at + 4] | (frame[at + 5] << 8);
                int  size   = EntryPrefix + length;
                int  from   = at;
                at += size;

                if (!folded.ByVariable.TryGetValue(varId, out var variable))
                {
                    if (folded.ByVariable.Count >= VariablesPerObject || size > MaxEntryBytes) continue;
                    variable = new Variable();
                    folded.ByVariable[varId] = variable;
                    Charge(folded, VariableOverhead);
                }
                else if (size > MaxEntryBytes)
                {
                    // What it kept is older than a value it cannot keep, so
                    // none of it is the variable's value any more.
                    while (variable.Entries.Count > 0) DropOldest(folded, variable);
                    continue;
                }

                var bytes = new byte[size];
                Buffer.BlockCopy(frame, from, bytes, 0, size);
                variable.Entries.Enqueue(new Entry { Tick = tick, Bytes = bytes, Ordinal = _nextOrdinal++ });
                long charge = size + EntryOverhead;
                variable.Bytes += charge;
                Charge(folded, charge);
                while (variable.Entries.Count > EntriesPerVariable
                       || (variable.Bytes > PerVariableMaxBytes && variable.Entries.Count > 1))
                    DropOldest(folded, variable);
            }

            if (folded.ByVariable.Count == 0) { Forget(objectId, folded); return; }
            while (_bytes > MaxBytes && _recency.First != null)
            {
                ulong least = _recency.First.Value;
                Forget(least, _objects[least]);
            }
        }

        /// <summary>
        /// Hand every entry kept for <paramref name="objectId"/> to
        /// <paramref name="apply"/> as a VariableUpdate frame of its own, in the
        /// order the entries arrived, and forget them — all of them before the
        /// first is applied, so an apply that reaches back in finds nothing of
        /// this object.  Returns how many were applied.
        /// </summary>
        internal int Release(ulong objectId, Action<byte[]> apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            if (!_objects.TryGetValue(objectId, out var folded)) return 0;
            Forget(objectId, folded);

            var entries = new List<Entry>();
            foreach (var variable in folded.ByVariable.Values) entries.AddRange(variable.Entries);
            entries.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            for (int i = 0; i < entries.Count; i++) apply(Frame(folded.Header, objectId, entries[i]));
            return entries.Count;
        }

        /// <summary>Forget what is kept for <paramref name="objectId"/>: it despawned before it spawned here.</summary>
        internal void Discard(ulong objectId)
        {
            if (_objects.TryGetValue(objectId, out var folded)) Forget(objectId, folded);
        }

        /// <summary>
        /// The room entry <paramref name="roomEntry"/> has completed: what was
        /// kept under an earlier one is another room's, and goes.
        /// </summary>
        internal void AdoptEntry(int roomEntry)
        {
            if (_objects.Count == 0) return;
            var stale = new List<ulong>();
            foreach (var pair in _objects)
                if (pair.Value.RoomEntry < roomEntry) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) Forget(stale[i], _objects[stale[i]]);
        }

        /// <summary>Session teardown: forget everything.</summary>
        internal void Clear()
        {
            _objects.Clear();
            _recency.Clear();
            _bytes = 0;
        }

        // A VariableUpdate packet — a header, then a payload of exactly the
        // length the header declares — that reads back entry by entry.
        private static bool TryRead(byte[] frame, out uint tick, out int count)
        {
            tick = 0; count = 0;
            if (frame == null || frame.Length < HeaderBytes + PayloadPrefix) return false;
            if (ReadU32(frame, 9) != (uint)(frame.Length - HeaderBytes)) return false;
            tick  = ReadU32(frame, HeaderBytes + 8);
            count = frame[HeaderBytes + 12];
            int at = HeaderBytes + PayloadPrefix;
            for (int i = 0; i < count; i++)
            {
                if (at + EntryPrefix > frame.Length) return false;
                int length = frame[at + 4] | (frame[at + 5] << 8);
                at += EntryPrefix + length;
                if (at > frame.Length) return false;
            }
            return at == frame.Length && count > 0;
        }

        // One entry as the VariableUpdate frame it arrived in, alone.
        private static byte[] Frame(byte[] header, ulong objectId, Entry entry)
        {
            int payload = PayloadPrefix + entry.Bytes.Length;
            var frame = new byte[header.Length + payload];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            frame[9]  = (byte)payload;
            frame[10] = (byte)(payload >> 8);
            frame[11] = (byte)(payload >> 16);
            frame[12] = (byte)(payload >> 24);
            int at = header.Length;
            for (int i = 0; i < 8; i++) frame[at + i] = (byte)(objectId >> (8 * i));
            frame[at + 8]  = (byte)entry.Tick;
            frame[at + 9]  = (byte)(entry.Tick >> 8);
            frame[at + 10] = (byte)(entry.Tick >> 16);
            frame[at + 11] = (byte)(entry.Tick >> 24);
            frame[at + 12] = 1;
            Buffer.BlockCopy(entry.Bytes, 0, frame, at + PayloadPrefix, entry.Bytes.Length);
            return frame;
        }

        private void Charge(Folded folded, long bytes)
        {
            folded.Bytes += bytes;
            _bytes       += bytes;
        }

        private void DropOldest(Folded folded, Variable variable)
        {
            long charge = variable.Entries.Dequeue().Bytes.Length + EntryOverhead;
            variable.Bytes -= charge;
            Charge(folded, -charge);
        }

        private void Forget(ulong objectId, Folded folded)
        {
            _bytes -= folded.Bytes;
            _objects.Remove(objectId);
            _recency.Remove(folded.Recency);
        }

        private static uint ReadU32(byte[] b, int at)
            => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
    }
}
