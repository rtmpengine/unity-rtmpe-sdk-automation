// AeadAad.cs — the Additional Authenticated Data an AEAD frame is sealed and
// opened under, written once for both directions.
//
// Until 2026-09-15 the SDK built this twice, inline: once in the send path and
// once in the receive path, each a hand-copied ladder of offsets with the same
// comment above it — and the comment was wrong in both places in the same
// words (see ArqSeq below). Two declarations of one layout are two places to
// disagree with each other and with the gateway, and nothing compiled by any
// test here could see either. One function, compiled by the capability shard
// and held by a known-answer vector the gateway's `write_aad` test asserts
// byte for byte.


namespace RTMPE.Core.Protocol
{
    /// <summary>
    /// Writes the AEAD AAD for one frame:
    /// <c>[packet_type][flags][arq_seq?][app_seq?][gameplay_seq?]</c>, each
    /// optional field four bytes little-endian, in the sub-header's own wire
    /// order.  Mirrors <c>write_aad</c> in
    /// <c>modules/gateway/src/crypto/pipeline.rs</c> — the two are one layout
    /// in two languages, and a field appended out of turn on either side is a
    /// Poly1305 failure on every frame that carries it.
    /// </summary>
    internal static class AeadAad
    {
        /// <summary>The longest AAD this layout can produce: 2 + 4 + 4 + 4.</summary>
        public const int MaxLength = 14;

        /// <summary>
        /// Writes the AAD into <paramref name="aad"/> from offset 0 and returns
        /// the number of bytes that are the AAD; bytes past it are not part of
        /// the tag.
        /// </summary>
        /// <param name="aad">A buffer of at least <see cref="MaxLength"/> bytes.</param>
        /// <param name="packetType">The wire type byte.</param>
        /// <param name="flags">
        /// The flags byte as the sender wrote it before sealing — WITHOUT
        /// <see cref="PacketFlags.Encrypted"/>, which both sides add after the
        /// AAD is fixed and strip before rebuilding it.
        /// </param>
        /// <param name="bindArqSeq">
        /// Whether <paramref name="arqSeq"/> is bound.  ⛔ This is the
        /// SESSION's answer — <c>FLAG_RELIABLE</c> is set on the frame and
        /// <see cref="CapabilityFlags.ArqSeqAad"/> was negotiated — never
        /// something read off the frame, because a frame that could choose
        /// could ask to be authenticated less.
        /// </param>
        /// <param name="arqSeq">The reliable sub-header's sequence.</param>
        /// <param name="bindAppSeq">Whether <c>FLAG_APP_SEQUENCE</c> is set.</param>
        /// <param name="appSeq">The application sequence sub-header.</param>
        /// <param name="bindGameplaySeq">Whether <c>FLAG_GAMEPLAY_ORDERED</c> is set.</param>
        /// <param name="gameplaySeq">The gameplay sequence sub-header.</param>
        /// <returns>The AAD's length, between 2 and <see cref="MaxLength"/>.</returns>
        /// <remarks>
        /// 🚨 <c>arq_seq</c> was left out of the AAD on both sides on the
        /// stated ground that <i>"a retransmit reuses the same ciphertext with
        /// a different arq_seq, so binding it would force a fresh AEAD seal per
        /// retry"</i>.  Both halves were false against the code: a retransmit
        /// re-runs the whole seal — a fresh nonce, a fresh tag — under the SAME
        /// <c>arq_seq</c> the entry was registered with, because the peer's
        /// cumulative acknowledgement depends on that value standing still.
        /// The seal was already fresh and the value already constant; binding
        /// costs four bytes and authenticates the field that decides which
        /// retransmit entry an acknowledgement clears and, against a gateway
        /// that keeps a receive window, whether the frame is routed at all.
        /// </remarks>
        public static int Write(
            byte[] aad,
            byte packetType,
            byte flags,
            bool bindArqSeq, uint arqSeq,
            bool bindAppSeq, uint appSeq,
            bool bindGameplaySeq, uint gameplaySeq)
        {
            aad[0] = packetType;
            aad[1] = flags;
            int length = 2;
            if (bindArqSeq)      length = WriteLittleEndian(aad, length, arqSeq);
            if (bindAppSeq)      length = WriteLittleEndian(aad, length, appSeq);
            if (bindGameplaySeq) length = WriteLittleEndian(aad, length, gameplaySeq);
            return length;
        }

        private static int WriteLittleEndian(byte[] aad, int at, uint value)
        {
            aad[at]     = (byte) value;
            aad[at + 1] = (byte)(value >>  8);
            aad[at + 2] = (byte)(value >> 16);
            aad[at + 3] = (byte)(value >> 24);
            return at + 4;
        }
    }
}
