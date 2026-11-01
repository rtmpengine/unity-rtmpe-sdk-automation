// RTMPE SDK — Runtime/Core/Protocol/VariableBatchFrames.cs
//
// Reads a VariableBatchUpdate (0x44) the server relays and hands each of its
// entries on as the VariableUpdate (0x41) packet that entry would have arrived
// as alone (audit P2-H2).
//
// Why a batch arrives at all:
//   A client's batch used to be relayed entry by entry, so one batch of up to
//   sixty-four entries reached every other member of the room as sixty-four
//   datagrams — each decrypted, queued and dispatched on its own — while the
//   sender paid the server one datagram for it.  A session that negotiates
//   CapabilityFlags.VariableBatchRelay is sent the batch as the one packet it
//   was, carrying only the entries the server admitted.
//
// Why each entry becomes a whole packet:
//   Everything that happens to a variable update downstream — held for an
//   object that has not spawned, held while a room is being entered, released
//   from inside a spawn, applied — is keyed on the 0x41 packet.  An entry IS a
//   0x41 payload, byte for byte, so giving it the batch's own header with the
//   type and length rewritten makes it exactly that packet, and the batch takes
//   every one of those paths with no second grammar to keep in step.
//
// Framing first:
//   The whole batch is read before any entry is handed on: a batch whose count,
//   lengths or end do not agree is refused whole, and nothing of it is applied.
//   The server built this frame from entries it decided one by one, so a batch
//   that does not read back is not one it sent.

using System;
using RTMPE.Protocol;

namespace RTMPE.Core.Protocol
{
    internal static class VariableBatchFrames
    {
        /// <summary>
        /// Hands each entry of the batch <paramref name="packet"/> carries to
        /// <paramref name="dispatch"/>, in order, as a complete VariableUpdate
        /// packet — but only once the whole batch has been read.
        /// </summary>
        /// <param name="packet">A decrypted VariableBatchUpdate packet, header included.</param>
        /// <param name="dispatch">Receives one VariableUpdate packet per entry.</param>
        /// <returns>
        /// The number of entries handed on, or -1 when the batch does not read
        /// back; then none was.
        /// </returns>
        internal static int Split(byte[] packet, Action<byte[]> dispatch)
        {
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
            if (packet == null || packet.Length < PacketProtocol.HEADER_SIZE) return -1;

            ReadOnlySpan<byte> batch = PacketParser.ExtractPayloadSpan(packet);
            int count = CountWellFramedEntries(batch);
            if (count <= 0) return -1;

            int off = RTMPE.Sync.VariableBatchBuilder.BatchHeaderBytes;
            for (int i = 0; i < count; i++)
            {
                int len = batch[off] | (batch[off + 1] << 8);
                off += RTMPE.Sync.VariableBatchBuilder.EntryHeaderBytes;

                var frame = new byte[PacketProtocol.HEADER_SIZE + len];
                Buffer.BlockCopy(packet, 0, frame, 0, PacketProtocol.HEADER_SIZE);
                frame[PacketProtocol.OFFSET_TYPE] = (byte)PacketType.VariableUpdate;
                frame[PacketProtocol.OFFSET_PAYLOAD_LEN]     = (byte)len;
                frame[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] = (byte)(len >> 8);
                frame[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] = 0;
                frame[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] = 0;
                batch.Slice(off, len).CopyTo(new Span<byte>(frame, PacketProtocol.HEADER_SIZE, len));
                off += len;

                dispatch(frame);
            }
            return count;
        }

        /// <summary>
        /// The entry count when <paramref name="batch"/> is one well-framed batch
        /// — a non-zero count, every entry's length within what follows it and
        /// within <see cref="RTMPE.Sync.VariableBatchBuilder.MaxEntryPayloadBytes"/>,
        /// and nothing after the last — and -1 otherwise.
        /// </summary>
        internal static int CountWellFramedEntries(ReadOnlySpan<byte> batch)
        {
            if (batch.Length < RTMPE.Sync.VariableBatchBuilder.BatchHeaderBytes) return -1;
            int count = batch[0];
            if (count == 0) return -1;

            int off = RTMPE.Sync.VariableBatchBuilder.BatchHeaderBytes;
            for (int i = 0; i < count; i++)
            {
                if (batch.Length - off < RTMPE.Sync.VariableBatchBuilder.EntryHeaderBytes) return -1;
                int len = batch[off] | (batch[off + 1] << 8);
                off += RTMPE.Sync.VariableBatchBuilder.EntryHeaderBytes;
                if (len > batch.Length - off) return -1;
                if (len > RTMPE.Sync.VariableBatchBuilder.MaxEntryPayloadBytes) return -1;
                off += len;
            }
            return off == batch.Length ? count : -1;
        }
    }
}
