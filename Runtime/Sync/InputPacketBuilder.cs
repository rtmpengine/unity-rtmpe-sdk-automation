// RTMPE SDK — Runtime/Sync/InputPacketBuilder.cs
//
// Serialises a batch of unacknowledged InputPayload frames into the wire
// format consumed by gateway packet type 0x43 (InputPayload) — the
// server-authoritative input opcode added in Phase 2.x (2026-04-25).
//
// Wire format (all little-endian, raw binary):
//
//  [0..1]      count : u16  — number of InputPayload entries that follow
//  [2..2+13*N] payload entries, each 13 bytes:
//    [+0..3] tick   : u32
//    [+4..7] move_x : f32
//    [+8..11] move_y : f32
//    [+12]    flags  : u8 (bit 0 = Jump)
//
// Total wire size: 2 + 13 * count bytes.
//
// Player identity is NOT carried in the payload — the gateway resolves
// session_id → authoritative player_id and embeds both in the NATS envelope
// before the Sync Service ever sees the bytes.  This eliminates the
// client-spoofing surface that would exist if a client could stamp any
// player_id it liked on its own inputs.
//
// MUST stay in sync with:
//  - PacketType.InputPayload = 0x43 (NetworkConstants.cs)
//  - PacketType::InputPayload = 0x43 (modules/gateway/src/packet/header.rs)
//  - InputPayloadParser (Go side, modules/synchronization/.../input_payload.go)
//
// No UnityEngine dependency — testable from pure .NET xunit projects.

using System;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>
    /// Builds the payload that carries a batch of <see cref="InputPayload"/>
    /// samples from an owner using client-side prediction to the server. Used
    /// by the SDK; not intended to be called from game code.
    /// </summary>
    public static class InputPacketBuilder
    {
        // ── Wire constants ─────────────────────────────────────────────────────

        /// <summary>The size, in bytes, of a batch's header.</summary>
        public const int BatchHeaderSize = 2;

        /// <summary>
        /// The most input samples one payload carries: as many as the input
        /// buffer holds, so a full buffer fits in one payload.
        /// </summary>
        public const int MaxBatchSize = InputBuffer.Capacity;

        // ── Build ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a payload of the first <paramref name="count"/> samples of
        /// <paramref name="payloads"/> in a new array.
        /// </summary>
        /// <param name="payloads">The input samples; only the first <paramref name="count"/> are read.</param>
        /// <param name="count">
        /// The number of samples to include, from 0 to <see cref="MaxBatchSize"/>.
        /// </param>
        /// <returns>The payload.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payloads"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is outside 0 to <see cref="MaxBatchSize"/>, or
        /// larger than the array.
        /// </exception>
        public static byte[] BuildBatchPayload(InputPayload[] payloads, int count)
        {
            ValidateBuildArgs(payloads, count);
            var buf = new byte[BatchHeaderSize + count * InputPayload.WireSize];
            BuildBatchPayloadInto(buf, 0, payloads, count);
            return buf;
        }

        /// <summary>
        /// Returns the size, in bytes, of the payload
        /// <see cref="BuildBatchPayloadInto"/> writes for
        /// <paramref name="count"/> samples.
        /// </summary>
        /// <param name="count">The number of samples, from 0 to <see cref="MaxBatchSize"/>.</param>
        /// <returns>The payload size in bytes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is outside 0 to <see cref="MaxBatchSize"/>.
        /// </exception>
        public static int ComputeBatchPayloadSize(int count)
        {
            if (count < 0 || count > MaxBatchSize)
                throw new ArgumentOutOfRangeException(nameof(count), count,
                    $"count must be in [0, {MaxBatchSize}].");
            return BatchHeaderSize + count * InputPayload.WireSize;
        }

        /// <summary>
        /// Writes a payload of the first <paramref name="count"/> samples of
        /// <paramref name="payloads"/> into <paramref name="dest"/> at
        /// <paramref name="destOffset"/>.
        /// </summary>
        /// <param name="dest">
        /// The buffer to write to, for example one rented from
        /// <c>ArrayPool&lt;byte&gt;.Shared</c>, with room for
        /// <see cref="ComputeBatchPayloadSize"/> bytes.
        /// </param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="payloads">The input samples.</param>
        /// <param name="count">The number of samples to include, from 0 to <see cref="MaxBatchSize"/>.</param>
        /// <returns>The number of bytes written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> or <paramref name="payloads"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is out of range, or <paramref name="dest"/>
        /// is too small for the payload at <paramref name="destOffset"/>.
        /// </exception>
        public static int BuildBatchPayloadInto(byte[] dest, int destOffset, InputPayload[] payloads, int count)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            ValidateBuildArgs(payloads, count);
            int size = BatchHeaderSize + count * InputPayload.WireSize;
            if (destOffset < 0 || (long)destOffset + size > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for an input batch payload at the given offset.");

            // [0..1] count : u16 LE
            dest[destOffset + 0] = (byte)(count       & 0xFF);
            dest[destOffset + 1] = (byte)((count >> 8) & 0xFF);

            // [2..2+13*N] InputPayload entries
            int offset = destOffset + BatchHeaderSize;
            for (int i = 0; i < count; i++)
            {
                payloads[i].WriteTo(dest, offset);
                offset += InputPayload.WireSize;
            }

            return size;
        }

        private static void ValidateBuildArgs(InputPayload[] payloads, int count)
        {
            if (payloads == null) throw new ArgumentNullException(nameof(payloads));
            if (count < 0 || count > MaxBatchSize)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"count must be in [0, {MaxBatchSize}].");
            if (count > payloads.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"count ({count}) exceeds payloads.Length ({payloads.Length}).");
        }
    }
}
