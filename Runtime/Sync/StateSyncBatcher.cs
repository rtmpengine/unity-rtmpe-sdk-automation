// RTMPE SDK — Runtime/Sync/StateSyncBatcher.cs
//
// The transforms this client's moving objects owe in one frame, framed as the
// one StateSyncBatch (0x46) datagram they travel in where the gateway asserts
// CapabilityFlags.StateBatch (audit P3-E3).
//
// ── Why ─────────────────────────────────────────────────────────────────────
//
// Each NetworkTransform sent its own datagram, so what a client cost the
// server — the gateway's per-session and per-address limiters, the Sync
// Service's per-session tier and its signature checks, all of which count
// messages — grew with how many objects it moved. A client moving four objects
// already sent more than the gateway's per-session limiter admitted, and the
// surplus it dropped took the client's heartbeats and reliable frames with it.
// Batched, the client sends one state datagram a frame whatever it moves.
//
// ── Wire ────────────────────────────────────────────────────────────────────
//
//   [count:1][len_0:1][record_0:len_0]…
//
// Each record is a StateSync (0x40) payload verbatim, in one of its six
// lengths, at most EntryCap of them, no object named twice — the gateway
// refuses a batch that breaks any of that whole (`parse_state_batch`).
//
// ── Contract ────────────────────────────────────────────────────────────────
//
// Add takes a record and answers whether the batch held so far must be sent
// first: when the record would not fit, when the batch is at EntryCap, or when
// the record names an object the batch already carries — which is how a
// teleport's copies (NetworkTransform.TeleportCopies) still leave as separate
// datagrams. Take hands the batch over and empties it; a batch of one record
// is handed over as that record alone, to travel as the StateSync it is.
//
// Main-thread only, like every NetworkTransform send.

using System;

namespace RTMPE.Sync
{
    /// <summary>
    /// Frames the transforms one client owes in one frame as one
    /// <c>StateSyncBatch</c> (0x46) payload (audit P3-E3).
    /// </summary>
    internal sealed class StateSyncBatcher
    {
        /// <summary>
        /// The most records a batch may carry: the gateway refuses a batch whose
        /// count byte says more.
        /// MUST stay in sync with <c>STATE_BATCH_ENTRY_CAP</c> in
        /// <c>modules/gateway/src/packet/header.rs</c>.
        /// </summary>
        internal const int EntryCap = 64;

        // The count byte, and a record's length byte.
        private const int CountBytes = 1;
        private const int LengthBytes = 1;

        private readonly byte[] _batch;
        private readonly byte[] _single = new byte[TransformPacketBuilder.PAYLOAD_SIZE_WITH_FLAGS];
        private readonly ulong[] _objects = new ulong[EntryCap];
        private int _length = CountBytes;
        private int _count;

        /// <summary>
        /// A batcher whose batches are at most <paramref name="capacityBytes"/>
        /// long — the payload one datagram carries.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="capacityBytes"/> cannot hold one record of the longest
        /// transform.
        /// </exception>
        internal StateSyncBatcher(int capacityBytes)
        {
            if (capacityBytes < CountBytes + LengthBytes + TransformPacketBuilder.PAYLOAD_SIZE_WITH_FLAGS)
                throw new ArgumentOutOfRangeException(nameof(capacityBytes), capacityBytes,
                    "A transform batch must hold at least one record of the longest transform.");
            _batch = new byte[capacityBytes];
        }

        /// <summary>The records the batch holds.</summary>
        internal int Count => _count;

        /// <summary>
        /// Whether <paramref name="length"/> is a transform's length — the six a
        /// <c>StateSync</c> payload has, and so the only records a batch takes.
        /// </summary>
        internal static bool IsRecordLength(int length)
            => length == TransformPacketBuilder.PAYLOAD_SIZE
            || length == TransformPacketBuilder.PAYLOAD_SIZE_WITH_TICK
            || length == TransformPacketBuilder.PAYLOAD_SIZE_WITH_FLAGS
            || length == TransformPacketBuilder.QUANTIZED_PAYLOAD_SIZE
            || length == TransformPacketBuilder.QUANTIZED_PAYLOAD_SIZE_WITH_TICK
            || length == TransformPacketBuilder.QUANTIZED_PAYLOAD_SIZE_WITH_FLAGS;

        /// <summary>
        /// The object a transform names: at offset 0 in the full-precision
        /// layout, behind the quantized flag byte in the quantized one.  The
        /// lengths of the two never meet, so the length decides which.
        /// </summary>
        internal static ulong ObjectOf(byte[] record, int length)
        {
            int at = length <= TransformPacketBuilder.QUANTIZED_PAYLOAD_SIZE_WITH_FLAGS ? 1 : 0;
            return BitConverter.IsLittleEndian
                ? BitConverter.ToUInt64(record, at)
                : ReadUInt64LittleEndian(record, at);
        }

        /// <summary>
        /// Whether the batch held so far must be taken before
        /// <paramref name="record"/> is added: it would not fit, the batch is at
        /// <see cref="EntryCap"/>, or the batch already carries its object.
        /// </summary>
        internal bool MustTakeBefore(byte[] record, int length)
        {
            if (_count == 0) return false;
            if (_count >= EntryCap) return true;
            if (_length + LengthBytes + length > _batch.Length) return true;
            ulong objectId = ObjectOf(record, length);
            for (int i = 0; i < _count; i++)
                if (_objects[i] == objectId) return true;
            return false;
        }

        /// <summary>
        /// Adds a transform to the batch.  The caller asks
        /// <see cref="MustTakeBefore"/> first and takes the batch when it says
        /// so; adding without asking is refused rather than written past what
        /// the gateway reads.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="length"/> is not a transform's length.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The record does not belong in this batch (see <see cref="MustTakeBefore"/>).
        /// </exception>
        internal void Add(byte[] record, int length)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!IsRecordLength(length) || length > record.Length)
                throw new ArgumentException(
                    $"A transform batch takes StateSync payloads only (got {length} bytes).",
                    nameof(length));
            if (MustTakeBefore(record, length))
                throw new InvalidOperationException(
                    "The transform batch must be taken before this record is added.");

            _objects[_count] = ObjectOf(record, length);
            _batch[_length] = (byte)length;
            Buffer.BlockCopy(record, 0, _batch, _length + LengthBytes, length);
            _length += LengthBytes + length;
            _count++;
            _batch[0] = (byte)_count;
        }

        /// <summary>
        /// Hands the batch over and empties it: <see langword="false"/> when it
        /// holds nothing.  A batch of one record is handed over as that record
        /// alone (<paramref name="isBatch"/> false) — the <c>StateSync</c> it
        /// would have been unbatched — and a larger one as the batch.
        /// </summary>
        /// <remarks>
        /// The buffer is the batcher's own and is overwritten by the next
        /// <see cref="Add"/>: the caller builds its packet from it before adding
        /// again.
        /// </remarks>
        internal bool TryTake(out byte[] payload, out int length, out bool isBatch)
        {
            if (_count == 0)
            {
                payload = null;
                length = 0;
                isBatch = false;
                return false;
            }
            if (_count == 1)
            {
                length = _batch[CountBytes];
                Buffer.BlockCopy(_batch, CountBytes + LengthBytes, _single, 0, length);
                payload = _single;
                isBatch = false;
            }
            else
            {
                payload = _batch;
                length = _length;
                isBatch = true;
            }
            Clear();
            return true;
        }

        /// <summary>Drops what the batch holds, unsent.</summary>
        internal void Clear()
        {
            _length = CountBytes;
            _count = 0;
        }

        private static ulong ReadUInt64LittleEndian(byte[] bytes, int at)
        {
            ulong value = 0;
            for (int i = 7; i >= 0; i--) value = (value << 8) | bytes[at + i];
            return value;
        }
    }
}
