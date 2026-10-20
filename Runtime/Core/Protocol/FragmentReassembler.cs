// RTMPE SDK — Runtime/Core/Protocol/FragmentReassembler.cs
//
// The pieces of a frame the gateway split because it was too large for one
// datagram (PacketType.Fragment, 0x12), put back together.

using System;
using System.Collections.Generic;

namespace RTMPE.Core.Protocol
{
    /// <summary>
    /// Reassembles a frame the gateway sent as <see cref="PacketType.Fragment"/>
    /// packets, when the session negotiated <see cref="CapabilityFlags.Fragments"/>.
    /// </summary>
    /// <remarks>
    /// <para>A fragment's payload is <c>[message_id:4 LE][index:1][count:1][chunk]</c>;
    /// the chunks of one message, in index order, are the frame's plaintext packet,
    /// header and payload. Each fragment was authenticated on its own before it
    /// reaches here.</para>
    /// <para>Bounded three ways, because a part that never completes holds memory:
    /// by the fragments one frame may have, by the bytes they may add up to, and by
    /// the frames held part-way at once — the oldest goes when another begins.  A
    /// part is never let go for its age: pieces are read when the main thread
    /// reaches them, so a stall between two pieces of one frame says nothing about
    /// whether the rest was lost, and the gateway's later copies of a piece already
    /// read are dropped as copies and could not replace it.</para>
    /// <para>Not thread-safe: the receive path runs it on the main thread.</para>
    /// </remarks>
    internal sealed class FragmentReassembler
    {
        /// <summary><c>[message_id:4][index:1][count:1]</c>.</summary>
        internal const int HeaderLength = 6;

        /// <summary>
        /// The most fragments one frame has: the gateway's <c>MAX_FRAGMENTS</c>,
        /// which <c>TheFragmentLayerIsTheGatewaysTests</c> reads.
        /// </summary>
        internal const int MaxFragments = 64;

        /// <summary>The most bytes the pieces of one frame add up to: each fits a 1200-byte datagram.</summary>
        internal const int MaxFrameBytes = MaxFragments * 1200;

        /// <summary>The most frames held part-way at once.</summary>
        internal const int MaxPartialFrames = 8;

        /// <summary>
        /// The header flags a frame sent in pieces may carry: compression and the
        /// sub-header flags are undone by the decrypt step, which a reassembled
        /// frame does not pass through, so the gateway never splits a frame that
        /// carries one (its <c>SPLITTABLE_FLAGS</c>).
        /// </summary>
        internal const PacketFlags FlagsAFrameInPiecesMayCarry = PacketFlags.EnhancedRpc;

        private sealed class Partial
        {
            public uint     Id;
            public byte     Count;
            public byte[][] Chunks;
            public int      Received;
            public int      Bytes;
        }

        // In arrival order, so the first is the oldest.
        private readonly List<Partial> _partials = new List<Partial>(MaxPartialFrames);

        /// <summary>Frames put back together whole.</summary>
        internal long Reassembled { get; private set; }

        /// <summary>
        /// Frames let go part-way: pushed out by newer ones, contradicted by a
        /// later piece, or grown past the bytes one frame may have.
        /// </summary>
        internal long Abandoned { get; private set; }

        /// <summary>Fragments whose header no frame could have: too short, or a count or index out of range.</summary>
        internal long Refused { get; private set; }

        /// <summary>Frames held part-way now.</summary>
        internal int PartialCount => _partials.Count;

        /// <summary>
        /// Takes one fragment's payload, and returns the whole frame when this was
        /// its last missing piece; <see langword="null"/> otherwise.
        /// </summary>
        /// <param name="payload">The fragment's payload, header included.</param>
        internal byte[] Add(byte[] payload)
        {
            if (payload == null || payload.Length <= HeaderLength)
            {
                Refused++;
                return null;
            }
            uint id = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
            byte index = payload[4];
            byte count = payload[5];
            if (count < 2 || count > MaxFragments || index >= count)
            {
                Refused++;
                return null;
            }

            Partial part = Find(id);
            if (part == null)
            {
                if (_partials.Count >= MaxPartialFrames)
                {
                    _partials.RemoveAt(0);
                    Abandoned++;
                }
                part = new Partial { Id = id, Count = count, Chunks = new byte[count][] };
                _partials.Add(part);
            }
            else if (part.Count != count)
            {
                _partials.Remove(part);
                Abandoned++;
                return null;
            }

            // A second copy of a piece already held.
            if (part.Chunks[index] != null) return null;

            int length = payload.Length - HeaderLength;
            if (part.Bytes + length > MaxFrameBytes)
            {
                _partials.Remove(part);
                Abandoned++;
                return null;
            }
            var chunk = new byte[length];
            Buffer.BlockCopy(payload, HeaderLength, chunk, 0, length);
            part.Chunks[index] = chunk;
            part.Received++;
            part.Bytes += length;
            if (part.Received < part.Count) return null;

            _partials.Remove(part);
            var whole = new byte[part.Bytes];
            int at = 0;
            foreach (var piece in part.Chunks)
            {
                Buffer.BlockCopy(piece, 0, whole, at, piece.Length);
                at += piece.Length;
            }
            Reassembled++;
            return whole;
        }

        /// <summary>Lets go of every frame held part-way: a session that ended leaves none to finish.</summary>
        internal void Clear() => _partials.Clear();

        /// <summary>
        /// Why a reassembled frame is not one the gateway could have split, or
        /// <see langword="null"/> when it is.  A split frame is plaintext, carries
        /// no flag but <see cref="FlagsAFrameInPiecesMayCarry"/>, declares the
        /// payload it has, and is of a type that travels in pieces.  Called after
        /// the header is validated, so <paramref name="length"/> covers a header.
        /// </summary>
        internal static string WhyNotSplitByTheGateway(byte[] frame, int length, PacketType type, bool wasEncrypted)
        {
            if (wasEncrypted) return "claims a seal";
            if ((frame[PacketProtocol.OFFSET_FLAGS] & ~(int)FlagsAFrameInPiecesMayCarry) != 0)
                return "carries a flag only the decrypt step undoes";
            uint declared = (uint)(
                  frame[PacketProtocol.OFFSET_PAYLOAD_LEN]
                | (frame[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] << 8)
                | (frame[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] << 16)
                | (frame[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] << 24));
            if (declared != (uint)(length - PacketProtocol.HEADER_SIZE))
                return "declares a payload length other than its own";
            if (!MayArriveInPieces(type)) return "is never sent in pieces";
            return null;
        }

        /// <summary>
        /// Whether a frame of <paramref name="type"/> can arrive in pieces: any the
        /// session seals, and none of the handshake's, which a client reads before
        /// or as the session that could reassemble them exists.  Exactly the types
        /// the gateway's <c>PacketType::may_travel_in_fragments</c> answers
        /// <c>false</c> for; <c>TheFragmentLayerIsTheGatewaysTests</c> reads that
        /// match and holds the two equal.
        /// </summary>
        internal static bool MayArriveInPieces(PacketType type)
        {
            switch (type)
            {
                case PacketType.Handshake:
                case PacketType.HandshakeAck:
                case PacketType.HandshakeInit:
                case PacketType.Challenge:
                case PacketType.HandshakeResponse:
                case PacketType.SessionAck:
                case PacketType.ReconnectInit:
                case PacketType.ReconnectAck:
                case PacketType.HandshakeError:
                case PacketType.HandshakeCookie:
                case PacketType.CookieHandshakeInit:
                case PacketType.Fragment:
                    return false;
                default:
                    return true;
            }
        }

        private Partial Find(uint id)
        {
            for (int i = 0; i < _partials.Count; i++)
                if (_partials[i].Id == id) return _partials[i];
            return null;
        }
    }
}
