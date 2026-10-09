// RTMPE SDK — Runtime/Core/Diagnostics/DiagnosticsBatcher.cs
//
// Pure, UnityEngine-free framing seam for the SDK diagnostics uplink
// (Diagnostics packet, 0x0C). Accumulates captured log lines and drains them
// into one or more wire payloads, each bounded by the per-packet entry cap and
// the MTU-safe application-payload budget so the PacketBuilder.Build() that
// follows can never throw on size.
//
// Wire layout (little-endian) — mirrors the gateway decoder in
// modules/gateway/src/diagnostics.rs:
//   [schema_ver:u8][entry_count:u16]
//   per entry: [level:u8][ts_ms:u32][msg_len:u16][msg][stack_len:u16][stack]
//
// The backlog is bounded.  Intake is whatever the game logs — an exception
// thrown every frame arrives at frame rate — while the drain is a few packets
// per flush interval, so an un-sent backlog left unbounded grows for as long as
// the storm lasts, on the client, with the operator's view falling further
// behind every second.  The oldest entry gives way when the bound is reached,
// as it does in the capture queue ahead of this buffer: what survives a storm is
// the window nearest to whatever it ends in.  What was dropped is counted, and
// the count travels: a notice naming it opens the next packet — or the one
// after, when the next packet would otherwise be a notice alone — so a gap in
// the stream reads as a gap and not as a quiet client.
//
// Journal-safety (control-char / ANSI stripping) is deliberately NOT done here:
// the gateway is the single source of truth for what is safe to write to
// journald, so this seam only bounds size and frames the bytes. Keeping the two
// concerns apart lets each be tested in isolation.

using System;
using System.Collections.Generic;
using System.Text;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Frames captured diagnostic log entries into MTU-bounded wire payloads.
    /// Single-threaded by contract: the uplink drains its concurrent capture
    /// queue into this batcher on the main thread only.
    /// </summary>
    internal sealed class DiagnosticsBatcher
    {
        /// <summary>Wire schema version; must match the gateway decoder.</summary>
        internal const byte SchemaVersion = 1;

        /// <summary>schema_ver(1) + entry_count(2).</summary>
        internal const int PayloadHeaderBytes = 3;

        /// <summary>level(1) + ts_ms(4) + msg_len(2) + stack_len(2).</summary>
        internal const int EntryFixedBytes = 9;

        /// <summary>
        /// Wire level of the notice that opens a packet after entries were
        /// dropped — <c>warning</c> in the scheme the gateway labels
        /// (0=log 1=warning 2=error 3=exception 4=assert).
        /// </summary>
        internal const byte DroppedNoticeLevel = 1;

        private readonly int _maxEntriesPerPacket;
        private readonly int _maxPayloadBytes;
        private readonly int _maxMsgBytes;
        private readonly int _maxStackBytes;
        private readonly int _maxPendingEntries;
        private readonly List<Entry> _pending = new List<Entry>();

        // Entries lost since the last notice went out, and over the batcher's
        // whole life.  The first is spent by the packet that reports it; the
        // second is the figure a test reads.
        private long _droppedSinceNotice;
        private long _droppedTotal;

        // Whether the last drained packet carried an entry of its own, or only
        // a notice.  A notice is owed a packet that also carries an entry, so a
        // configuration of one entry per packet under sustained loss still
        // ships an entry every other packet rather than notices only.
        private bool _lastPacketCarriedAnEntry = true;

        private readonly struct Entry
        {
            public readonly byte Level;
            public readonly uint TsMs;
            public readonly byte[] Msg;
            public readonly byte[] Stack;

            public Entry(byte level, uint tsMs, byte[] msg, byte[] stack)
            {
                Level = level;
                TsMs = tsMs;
                Msg = msg;
                Stack = stack;
            }

            public int WireSize => EntryFixedBytes + Msg.Length + Stack.Length;
        }

        /// <param name="maxEntriesPerPacket">Hard cap on entries per drained packet.</param>
        /// <param name="maxPayloadBytes">
        /// Per-packet payload ceiling — pass <c>PacketBuilder.MaxApplicationPayloadBytes</c>
        /// so a drained packet always survives <c>Build()</c> unsplit.
        /// </param>
        /// <param name="maxMsgBytes">Per-entry message truncation cap, in UTF-8 bytes.</param>
        /// <param name="maxStackBytes">Per-entry stack truncation cap, in UTF-8 bytes.</param>
        /// <param name="maxPendingEntries">
        /// Ceiling on entries buffered but not yet drained.  When an entry
        /// arrives at the ceiling the oldest buffered entry is dropped to make
        /// room, counted, and reported by the next drained packet.
        /// </param>
        public DiagnosticsBatcher(
            int maxEntriesPerPacket, int maxPayloadBytes, int maxMsgBytes, int maxStackBytes,
            int maxPendingEntries)
        {
            if (maxEntriesPerPacket < 1)
                throw new ArgumentOutOfRangeException(nameof(maxEntriesPerPacket));
            if (maxMsgBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxMsgBytes));
            if (maxStackBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxStackBytes));
            if (maxPendingEntries < 1)
                throw new ArgumentOutOfRangeException(nameof(maxPendingEntries));

            // A single worst-case entry must fit one packet or TryDrainPacket
            // could never make progress; enforce the invariant at construction
            // so the drain loop is guaranteed to advance.
            int worstEntry = EntryFixedBytes + maxMsgBytes + maxStackBytes;
            if (maxPayloadBytes < PayloadHeaderBytes + worstEntry)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxPayloadBytes),
                    "maxPayloadBytes is too small to hold a single maximum-size entry");
            }

            _maxEntriesPerPacket = maxEntriesPerPacket;
            _maxPayloadBytes = maxPayloadBytes;
            _maxMsgBytes = maxMsgBytes;
            _maxStackBytes = maxStackBytes;
            _maxPendingEntries = maxPendingEntries;
        }

        /// <summary>Entries buffered but not yet drained into a packet.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Ceiling on <see cref="PendingCount"/>.</summary>
        public int MaxPendingEntries => _maxPendingEntries;

        /// <summary>
        /// Entries dropped over this batcher's life: those that gave way at the
        /// ceiling here, plus every loss a caller reported through
        /// <see cref="NoteDropped"/>.
        /// </summary>
        public long DroppedTotal => _droppedTotal;

        /// <summary>
        /// Buffer one entry, redacting local filesystem paths and truncating
        /// message and stack to their UTF-8 byte caps so a single entry can always
        /// be framed within one packet.  At the ceiling the oldest buffered entry
        /// gives way, and is counted.
        /// </summary>
        /// <remarks>
        /// S4-39 — the redaction is here because this is the one place every entry
        /// passes through on its way to a packet, whichever capture path produced
        /// it, and because it is reached on the main thread at flush time rather
        /// than inside the log callback, which fires on any thread and must stay
        /// allocation-light.
        /// <para>
        /// ⛔ Before the truncation, not after: a path cut off mid-way by the byte
        /// cap is still a path, and the part that survives is the ABSOLUTE part.
        /// </para>
        /// </remarks>
        public void Add(byte level, uint tsMs, string message, string stack)
        {
            if (_pending.Count >= _maxPendingEntries)
            {
                _pending.RemoveAt(0);
                NoteDropped(1);
            }

            _pending.Add(new Entry(
                level,
                tsMs,
                TruncateUtf8(LocalPathRedaction.Redact(message), _maxMsgBytes),
                TruncateUtf8(LocalPathRedaction.Redact(stack), _maxStackBytes)));
        }

        /// <summary>
        /// Record <paramref name="count"/> entries lost before they reached this
        /// buffer, so the notice that opens the next packet accounts for every
        /// stage of the uplink rather than for this one alone.
        /// </summary>
        public void NoteDropped(long count)
        {
            if (count <= 0) return;
            _droppedSinceNotice += count;
            _droppedTotal += count;
        }

        /// <summary>
        /// Serialise the next packet's worth of buffered entries into
        /// <paramref name="payload"/> and remove them from the buffer. Returns
        /// <see langword="false"/> (and a null payload) when nothing is pending.
        /// Each call yields at most <c>maxEntriesPerPacket</c> entries and a
        /// payload no larger than <c>maxPayloadBytes</c>.
        /// </summary>
        public bool TryDrainPacket(out byte[] payload)
        {
            if (_pending.Count == 0)
            {
                payload = null;
                return false;
            }

            // A gap is reported ahead of the first entry that follows it, and
            // the notice is an entry like any other: it takes a slot under the
            // per-packet cap and bytes under the payload ceiling, so the loop
            // below sizes it the way it sizes everything else.  Stamped with
            // the surviving entry's time, which is when the gap ended.  Held
            // back when the previous packet was a notice alone, so a notice
            // never displaces every entry: the count keeps accruing and goes
            // out with the packet after.
            bool noticeCarried = _droppedSinceNotice > 0 && _lastPacketCarriedAnEntry;
            if (noticeCarried)
            {
                _pending.Insert(0, new Entry(
                    DroppedNoticeLevel,
                    _pending[0].TsMs,
                    TruncateUtf8(DroppedNotice(_droppedSinceNotice), _maxMsgBytes),
                    Array.Empty<byte>()));
                _droppedSinceNotice = 0;
            }

            int size = PayloadHeaderBytes;
            int count = 0;
            while (count < _pending.Count
                   && count < _maxEntriesPerPacket
                   && size + _pending[count].WireSize <= _maxPayloadBytes)
            {
                size += _pending[count].WireSize;
                count++;
            }
            // The constructor guarantees one max-size entry fits, so count >= 1.

            byte[] buf = new byte[size];
            int p = 0;
            buf[p++] = SchemaVersion;
            WriteU16(buf, ref p, count);
            for (int i = 0; i < count; i++)
            {
                Entry e = _pending[i];
                buf[p++] = e.Level;
                WriteU32(buf, ref p, e.TsMs);
                WriteU16(buf, ref p, e.Msg.Length);
                Buffer.BlockCopy(e.Msg, 0, buf, p, e.Msg.Length);
                p += e.Msg.Length;
                WriteU16(buf, ref p, e.Stack.Length);
                Buffer.BlockCopy(e.Stack, 0, buf, p, e.Stack.Length);
                p += e.Stack.Length;
            }

            _pending.RemoveRange(0, count);
            _lastPacketCarriedAnEntry = count > (noticeCarried ? 1 : 0);
            payload = buf;
            return true;
        }

        /// <summary>
        /// The text of the notice that opens a packet after
        /// <paramref name="dropped"/> entries were lost.
        /// </summary>
        internal static string DroppedNotice(long dropped) =>
            "[RTMPE] diagnostics uplink: " + dropped
            + (dropped == 1 ? " entry was" : " entries were")
            + " dropped ahead of the entries that follow; the uplink could not carry them.";

        private static void WriteU16(byte[] buf, ref int p, int value)
        {
            buf[p++] = (byte)(value & 0xFF);
            buf[p++] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] buf, ref int p, uint value)
        {
            buf[p++] = (byte)(value & 0xFF);
            buf[p++] = (byte)((value >> 8) & 0xFF);
            buf[p++] = (byte)((value >> 16) & 0xFF);
            buf[p++] = (byte)((value >> 24) & 0xFF);
        }

        /// <summary>
        /// UTF-8 encode <paramref name="s"/>, clipped to <paramref name="maxBytes"/>
        /// without splitting a multi-byte sequence. A null/empty input or a
        /// non-positive cap yields an empty array.
        /// </summary>
        internal static byte[] TruncateUtf8(string s, int maxBytes)
        {
            if (string.IsNullOrEmpty(s) || maxBytes <= 0) return Array.Empty<byte>();

            byte[] full = Encoding.UTF8.GetBytes(s);
            if (full.Length <= maxBytes) return full;

            // Back up off any UTF-8 continuation byte (10xxxxxx) so the clip
            // lands on a character boundary rather than mid-code-point.
            int end = maxBytes;
            while (end > 0 && (full[end] & 0xC0) == 0x80) end--;

            byte[] clipped = new byte[end];
            Buffer.BlockCopy(full, 0, clipped, 0, end);
            return clipped;
        }
    }
}
