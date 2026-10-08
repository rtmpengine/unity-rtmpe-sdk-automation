// RTMPE SDK — Runtime/Protocol/PacketParser.cs
//
// Parse inbound RTMPE packet payloads.
//
// This class only parses payloads that the CLIENT receives from the server:
//   - Challenge   (0x06): [ephemeral:32][static:32][sig:64] = 128 bytes
//   - SessionAck  (0x08): [crypto_id:4 LE][jwt_len:2 LE][jwt:N][rc_len:2 LE][rc:R]
//
// Header validation (magic, version) is done in NetworkManager.ProcessPacket.
// PacketParser only handles the *payload* (bytes after the 13-byte header).
//
// ReadOnlySpan<byte> overloads exist alongside the byte[] overloads so callers
// holding a pool-rented buffer can parse without an intermediate ExtractPayload
// allocation.  The byte[] overloads delegate to the span overloads to keep the
// two paths bit-for-bit identical and trivially auditable.

using System;
using System.Text;
using RTMPE.Core;

namespace RTMPE.Protocol
{
    /// <summary>
    /// Reads received RTMPE packets: checks the header, extracts the payload,
    /// and parses the handshake payloads the client receives
    /// (<c>Challenge</c> and <c>SessionAck</c>).
    /// </summary>
    /// <remarks>
    /// All methods are static, thread-safe and allocate as little as possible.
    /// The drop counters are process-wide. Used by the SDK; not intended to be
    /// called from game code.
    /// </remarks>
    public static class PacketParser
    {
        // ── Header extraction ─────────────────────────────────────────────────

        // Cumulative count of packets rejected by <see cref="ExtractPayloadSpan"/>
        // because the declared payload length exceeded the 1 MiB sanity cap.
        // Exposed for backpressure observability — every legitimate gateway
        // emits packets well under this limit, so any non-zero rate
        // surfaces either a hostile sender or a protocol-version mismatch.
        private static long _droppedOversizedCount;
        private static long _droppedTruncatedCount;
        private static long _droppedHeaderInvalidCount;

        /// <summary>
        /// Number of packets dropped because their declared payload length was
        /// above 1 MiB. Never reset.
        /// </summary>
        public static long DroppedOversizedCount =>
            System.Threading.Interlocked.Read(ref _droppedOversizedCount);

        /// <summary>
        /// Number of packets dropped because they were shorter than their
        /// declared payload length. Never reset.
        /// </summary>
        public static long DroppedTruncatedCount =>
            System.Threading.Interlocked.Read(ref _droppedTruncatedCount);

        /// <summary>
        /// Number of packets dropped because their magic or version bytes did
        /// not match the protocol this SDK speaks. A non-zero value means
        /// traffic that is not RTMPE reached the socket, or the SDK and the
        /// server speak different protocol versions. Never reset.
        /// </summary>
        public static long DroppedHeaderInvalidCount =>
            System.Threading.Interlocked.Read(ref _droppedHeaderInvalidCount);

        /// <summary>
        /// Checks that the header's <c>payload_len</c> field equals the frame
        /// length the transport delivered minus the 13-byte header.
        /// </summary>
        /// <remarks>
        /// The encrypted receive path uses this before it slices the frame, so
        /// a packet whose declared length disagrees with the bytes delivered is
        /// rejected first.
        /// </remarks>
        /// <param name="packet">The full wire packet (header + payload).</param>
        /// <param name="frameLength">
        /// Meaningful byte count of <paramref name="packet"/> as reported by
        /// the transport.  May be shorter than <c>packet.Length</c> when the
        /// buffer is rented from a pool.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the header's declared length matches the
        /// frame; <see langword="false"/> for a null, short, or inconsistent packet.
        /// </returns>
        public static bool HeaderPayloadLengthMatchesFrame(byte[] packet, int frameLength)
        {
            if (packet == null
                || frameLength < PacketProtocol.HEADER_SIZE
                || frameLength > packet.Length)
                return false;

            uint declared =
                  (uint) packet[PacketProtocol.OFFSET_PAYLOAD_LEN]
                | ((uint) packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] <<  8)
                | ((uint) packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] << 16)
                | ((uint) packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] << 24);

            return declared == (uint)(frameLength - PacketProtocol.HEADER_SIZE);
        }

        /// <summary>
        /// Returns a copy of the payload of a complete packet (header and
        /// payload). Returns an empty array when the packet is
        /// <see langword="null"/>, has no payload, or is malformed, in the ways
        /// <see cref="ExtractPayloadSpan"/> describes.
        /// </summary>
        /// <param name="rawPacket">The complete packet.</param>
        public static byte[] ExtractPayload(byte[] rawPacket)
        {
            if (rawPacket == null) return Array.Empty<byte>();
            return ExtractPayloadCopy(new ReadOnlySpan<byte>(rawPacket));
        }

        /// <summary>
        /// Returns the payload of a complete packet as a slice, without copying;
        /// suited to a buffer rented from a pool.
        /// </summary>
        /// <remarks>
        /// Returns an empty span when the packet has no payload, or when it is
        /// shorter than the header, has the wrong magic or version, declares a
        /// payload longer than 1 MiB, or is shorter than its declared length;
        /// the last three cases are counted in <see cref="DroppedHeaderInvalidCount"/>,
        /// <see cref="DroppedOversizedCount"/> and <see cref="DroppedTruncatedCount"/>.
        /// </remarks>
        /// <param name="rawPacket">The complete packet.</param>
        public static ReadOnlySpan<byte> ExtractPayloadSpan(ReadOnlySpan<byte> rawPacket)
        {
            if (rawPacket.Length < PacketProtocol.HEADER_SIZE)
                return ReadOnlySpan<byte>.Empty;

            // Validate magic + version inside the parser, not at the caller.
            // The function is `public static` and any future caller (test
            // harness, alternate transport adapter, replay/diagnostic tool)
            // inherits the trust boundary; relying on out-of-band caller
            // discipline lets non-RTMPE noise be admitted as a "valid" empty-
            // payload slice and weakens the DroppedTruncated /
            // DroppedOversized counters as observability signals.  Same
            // defence-in-depth principle as the rest of this parser:
            // refuse non-RTMPE input independent of caller discipline.
            ushort magic = (ushort)(rawPacket[0] | (rawPacket[1] << 8));
            if (magic != PacketProtocol.MAGIC || rawPacket[2] != PacketProtocol.VERSION)
            {
                System.Threading.Interlocked.Increment(ref _droppedHeaderInvalidCount);
                return ReadOnlySpan<byte>.Empty;
            }

            uint payloadLen = (uint)(rawPacket[9]
                                   | (rawPacket[10] << 8)
                                   | (rawPacket[11] << 16)
                                   | (rawPacket[12] << 24));

            // Sanity cap: reject any payload claim larger than 1 MiB.
            // Without this guard, a crafted packet with payload_len ≥ 2^31 causes
            // the (int) cast below to go negative, bypasses the length check, and
            // then a downstream allocation throws OverflowException.
            const uint MaxPayload = 1 * 1024 * 1024;
            if (payloadLen > MaxPayload)
            {
                System.Threading.Interlocked.Increment(ref _droppedOversizedCount);
                return ReadOnlySpan<byte>.Empty;
            }

            int expectedTotal = PacketProtocol.HEADER_SIZE + (int)payloadLen;
            if (rawPacket.Length < expectedTotal)
            {
                System.Threading.Interlocked.Increment(ref _droppedTruncatedCount);
                return ReadOnlySpan<byte>.Empty;
            }
            if (payloadLen == 0) return ReadOnlySpan<byte>.Empty;

            return rawPacket.Slice(PacketProtocol.HEADER_SIZE, (int)payloadLen);
        }

        // Allocation-bearing convenience used by the byte[] overload.  The span
        // overload is preferred everywhere else.
        private static byte[] ExtractPayloadCopy(ReadOnlySpan<byte> rawPacket)
        {
            var slice = ExtractPayloadSpan(rawPacket);
            if (slice.IsEmpty) return Array.Empty<byte>();
            return slice.ToArray();
        }

        // ── Challenge (0x06) ──────────────────────────────────────────────────

        /// <summary>
        /// Parses the 128-byte <c>Challenge</c> payload:
        /// <c>[server_ephemeral_pub:32][server_static_pub:32][ed25519_sig:64]</c>.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="serverEphemeralPub">Receives the server's X25519 ephemeral public key.</param>
        /// <param name="serverStaticPub">Receives the server's Ed25519 static public key.</param>
        /// <param name="ed25519Sig">Receives the 64-byte signature.</param>
        /// <returns>
        /// <see langword="true"/> when the payload is exactly 128 bytes;
        /// otherwise <see langword="false"/>, with every output
        /// <see langword="null"/>.
        /// </returns>
        public static bool ParseChallenge(
            byte[] payload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            out byte[] ed25519Sig)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;
            ed25519Sig         = null;

            if (payload == null) return false;
            return ParseChallenge(new ReadOnlySpan<byte>(payload),
                                  out serverEphemeralPub,
                                  out serverStaticPub,
                                  out ed25519Sig);
        }

        /// <summary>
        /// Parses the 128-byte <c>Challenge</c> payload from a span. The
        /// outputs are new arrays, so they remain valid after the source
        /// buffer is reused.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="serverEphemeralPub">Receives the server's X25519 ephemeral public key.</param>
        /// <param name="serverStaticPub">Receives the server's Ed25519 static public key.</param>
        /// <param name="ed25519Sig">Receives the 64-byte signature.</param>
        /// <returns>
        /// <see langword="true"/> when the payload is exactly 128 bytes;
        /// otherwise <see langword="false"/>, with every output
        /// <see langword="null"/>.
        /// </returns>
        public static bool ParseChallenge(
            ReadOnlySpan<byte> payload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            out byte[] ed25519Sig)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;
            ed25519Sig         = null;

            if (payload.Length != 128) return false;

            // The three sub-fields are returned as independent arrays because
            // they outlive the enclosing packet — one is fed to Ed25519Verify,
            // another is stored as the server's static identity for pinning.
            // Allocating once on a successful Challenge is unavoidable; the
            // span path simply ensures we don't allocate the redundant
            // intermediate `payload` byte[] that the legacy ExtractPayload did.
            serverEphemeralPub = payload.Slice(  0, 32).ToArray();
            serverStaticPub    = payload.Slice( 32, 32).ToArray();
            ed25519Sig         = payload.Slice( 64, 64).ToArray();
            return true;
        }

        // ── SessionAck (0x08) ─────────────────────────────────────────────────

        /// <summary>
        /// Parses a <c>SessionAck</c> payload, discarding the server's
        /// capability flags. Use an overload that returns
        /// <c>gatewayCaps</c> instead.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="cryptoId">Receives the <c>crypto_id</c> field.</param>
        /// <param name="jwtToken">Receives the session token (a JWT); empty when the server sent none.</param>
        /// <param name="reconnectToken">Receives the reconnect token; empty when the server sent none.</param>
        /// <returns><see langword="true"/> when the payload is well formed.</returns>
        [System.Obsolete(
            "Use the five-argument overload that surfaces gatewayCaps — " +
            "this overload silently discards the capability-negotiation tail " +
            "and will be removed in a future SDK version.")]
        public static bool ParseSessionAck(
            byte[] payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken)
            => ParseSessionAck(
                payload,
                out cryptoId,
                out jwtToken,
                out reconnectToken,
                out _);

        /// <summary>
        /// Parses a <c>SessionAck</c> payload, including the server's
        /// capability flags:
        /// <c>[crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:R][gateway_caps:4 LE?]</c>.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="cryptoId">Receives the <c>crypto_id</c> field.</param>
        /// <param name="jwtToken">Receives the session token (a JWT); empty when the server sent none.</param>
        /// <param name="reconnectToken">Receives the reconnect token; empty when the server sent none.</param>
        /// <param name="gatewayCaps">
        /// Receives the server's capability flags, or
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/> when the
        /// payload ends before them.
        /// </param>
        /// <returns><see langword="true"/> when the payload is well formed.</returns>
        public static bool ParseSessionAck(
            byte[] payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken,
            out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps)
            => ParseSessionAck(
                payload,
                out cryptoId,
                out jwtToken,
                out reconnectToken,
                out gatewayCaps,
                out _);

        /// <summary>
        /// Parses a <c>SessionAck</c> payload, including the server's
        /// capability flags and the reconnect token's lifetime:
        /// <c>[crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:R][gateway_caps:4 LE?][reconnect_token_lifetime_secs:4 LE?]</c>.
        /// </summary>
        /// <remarks>
        /// The lifetime field is present only when the capability flags include
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.ReconnectTokenLifetime"/>.
        /// Without that flag <paramref name="reconnectTokenLifetimeSeconds"/> is
        /// <c>0</c>, which means the server stated no lifetime, not that the
        /// token has expired.
        /// </remarks>
        /// <param name="payload">The payload.</param>
        /// <param name="cryptoId">Receives the <c>crypto_id</c> field.</param>
        /// <param name="jwtToken">Receives the session token (a JWT); empty when the server sent none.</param>
        /// <param name="reconnectToken">Receives the reconnect token; empty when the server sent none.</param>
        /// <param name="gatewayCaps">
        /// Receives the server's capability flags, or
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/> when the
        /// payload ends before them.
        /// </param>
        /// <param name="reconnectTokenLifetimeSeconds">
        /// Receives the reconnect token's lifetime in seconds, or <c>0</c> when
        /// the server did not state one.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the payload is well formed;
        /// <see langword="false"/> when it is <see langword="null"/> or
        /// malformed (see the span overload).
        /// </returns>
        public static bool ParseSessionAck(
            byte[] payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken,
            out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps,
            out uint   reconnectTokenLifetimeSeconds)
        {
            cryptoId                      = 0;
            jwtToken                      = null;
            reconnectToken                = null;
            gatewayCaps                   = RTMPE.Core.Protocol.CapabilityFlags.None;
            reconnectTokenLifetimeSeconds = 0;

            if (payload == null) return false;
            return ParseSessionAck(new ReadOnlySpan<byte>(payload),
                                   out cryptoId,
                                   out jwtToken,
                                   out reconnectToken,
                                   out gatewayCaps,
                                   out reconnectTokenLifetimeSeconds);
        }

        /// <summary>
        /// Parses a <c>SessionAck</c> payload from a span, discarding the
        /// server's capability flags. Use an overload that returns
        /// <c>gatewayCaps</c> instead.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="cryptoId">Receives the <c>crypto_id</c> field.</param>
        /// <param name="jwtToken">Receives the session token (a JWT); empty when the server sent none.</param>
        /// <param name="reconnectToken">Receives the reconnect token; empty when the server sent none.</param>
        /// <returns><see langword="true"/> when the payload is well formed.</returns>
        [System.Obsolete(
            "Use the five-argument Span overload that surfaces gatewayCaps — " +
            "this overload silently discards the capability-negotiation tail " +
            "and will be removed in a future SDK version.")]
        public static bool ParseSessionAck(
            ReadOnlySpan<byte> payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken)
            => ParseSessionAck(
                payload,
                out cryptoId,
                out jwtToken,
                out reconnectToken,
                out _);

        /// <summary>
        /// Parses a <c>SessionAck</c> payload from a span, including the
        /// server's capability flags. Strings are decoded directly from the
        /// span. The reconnect token's lifetime, when the flags announce it,
        /// must be present but is not returned.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="cryptoId">Receives the <c>crypto_id</c> field.</param>
        /// <param name="jwtToken">Receives the session token (a JWT); empty when the server sent none.</param>
        /// <param name="reconnectToken">Receives the reconnect token; empty when the server sent none.</param>
        /// <param name="gatewayCaps">
        /// Receives the server's capability flags, or
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/> when the
        /// payload ends before them.
        /// </param>
        /// <returns><see langword="true"/> when the payload is well formed.</returns>
        public static bool ParseSessionAck(
            ReadOnlySpan<byte> payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken,
            out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps)
            => ParseSessionAck(
                payload,
                out cryptoId,
                out jwtToken,
                out reconnectToken,
                out gatewayCaps,
                out _);

        /// <summary>
        /// Parses a <c>SessionAck</c> payload from a span, including the
        /// server's capability flags and the reconnect token's lifetime.
        /// Strings are decoded directly from the span.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Layout:
        /// <c>[crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:R][gateway_caps:4 LE?][reconnect_token_lifetime_secs:4 LE?]</c>.
        /// </para>
        /// <para>
        /// The capability flags are optional; the lifetime field is present only
        /// when they include
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.ReconnectTokenLifetime"/>.
        /// Bytes after the fields this parser knows are ignored, so later
        /// protocol versions can append fields.
        /// </para>
        /// </remarks>
        /// <param name="payload">The payload.</param>
        /// <param name="cryptoId">Receives the <c>crypto_id</c> field.</param>
        /// <param name="jwtToken">Receives the session token (a JWT); empty when the server sent none.</param>
        /// <param name="reconnectToken">Receives the reconnect token; empty when the server sent none.</param>
        /// <param name="gatewayCaps">
        /// Receives the server's capability flags, or
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/> when the
        /// payload ends before them.
        /// </param>
        /// <param name="reconnectTokenLifetimeSeconds">
        /// Receives the reconnect token's lifetime in seconds, or <c>0</c> when
        /// the server did not state one (which does not mean the token has
        /// expired).
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the payload is well formed;
        /// <see langword="false"/> when it is shorter than 8 bytes, a length
        /// runs past its end, a token is not valid UTF-8, or the lifetime is
        /// announced but missing.
        /// </returns>
        public static bool ParseSessionAck(
            ReadOnlySpan<byte> payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken,
            out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps,
            out uint   reconnectTokenLifetimeSeconds)
        {
            cryptoId                      = 0;
            jwtToken                      = null;
            reconnectToken                = null;
            gatewayCaps                   = RTMPE.Core.Protocol.CapabilityFlags.None;
            reconnectTokenLifetimeSeconds = 0;

            if (payload.Length < 8) return false; // 4 + 2 + 0 + 2 minimum

            int offset = 0;

            cryptoId = (uint)(payload[offset]
                            | (payload[offset + 1] << 8)
                            | (payload[offset + 2] << 16)
                            | (payload[offset + 3] << 24));
            offset += 4;

            int jwtLen = payload[offset] | (payload[offset + 1] << 8);
            offset += 2;
            // Subtraction-form bounds check: the additive form
            // (offset + jwtLen > payload.Length) can overflow int when
            // jwtLen is near ushort.MaxValue and offset is large, admitting
            // the read.  Subtracting from payload.Length (always non-
            // negative, bounded by the receive ceiling) cannot wrap.
            if (jwtLen > payload.Length - offset) return false;

            try
            {
                jwtToken = jwtLen > 0
                    ? DecodeUtf8(payload.Slice(offset, jwtLen))
                    : string.Empty;
                offset += jwtLen;

                if (offset > payload.Length - 2) return false;
                int rcLen = payload[offset] | (payload[offset + 1] << 8);
                offset += 2;
                if (rcLen > payload.Length - offset) return false;

                reconnectToken = rcLen > 0
                    ? DecodeUtf8(payload.Slice(offset, rcLen))
                    : string.Empty;
                offset += rcLen;
            }
            catch (System.Text.DecoderFallbackException)
            {
                // Malformed UTF-8 in either token.  The strict decoder has
                // already discarded its partial state; reset the outputs and
                // surface a clean parse failure to the caller.
                jwtToken       = null;
                reconnectToken = null;
                return false;
            }

            // Optional `gateway_caps:4 LE` tail.  Absent on legacy gateways
            // that pre-date capability negotiation — the parser treats the
            // missing field as `CapabilityFlags.None`, which the negotiator
            // intersects with the SDK's advertised caps to disable every
            // optional feature for the session.  Any bytes beyond the cap
            // field are ignored so the wire stays forward-extensible.
            if (RTMPE.Core.Protocol.CapabilityFlagsWire.TryReadLittleEndian(
                    payload, offset, out gatewayCaps))
            {
                offset += RTMPE.Core.Protocol.CapabilityFlagsWire.WireSize;

                // `reconnect_token_lifetime_secs:4 LE`, present only when the
                // advertisement just read announces it.  Keyed on the bit and
                // never on the bytes being there: four trailing bytes from a
                // future extension are not this field, and reading them as one
                // would hand the reconnect ladder a lifetime nobody stated.
                // A gateway that advertises the bit and truncates the field is
                // malformed, and the parse fails rather than inventing a value.
                if ((gatewayCaps & RTMPE.Core.Protocol.CapabilityFlags.ReconnectTokenLifetime) != 0)
                {
                    if (offset > payload.Length - 4) return false;
                    reconnectTokenLifetimeSeconds =
                          (uint) payload[offset]
                        | ((uint) payload[offset + 1] <<  8)
                        | ((uint) payload[offset + 2] << 16)
                        | ((uint) payload[offset + 3] << 24);
                }
            }

            return true;
        }

        // Strict UTF-8 decoder.  Encoding.UTF8 silently substitutes U+FFFD
        // for any malformed sequence; that lets a hostile gateway smuggle
        // bytes that survive the parse but mutate downstream string-equality
        // invariants (host comparisons, reconnect-token equality with the
        // server's view).  A throwOnInvalidBytes encoder converts the same
        // input into a clean DecoderFallbackException that the caller maps
        // to a parse failure.
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Encoding.UTF8 (and its strict cousin built above) accept
        // ReadOnlySpan<byte> on .NET Standard 2.1 (Unity 2021.2+) and .NET
        // 5+.  Wrapped so call sites stay focused on parsing logic; profile
        // shows the span overload is genuinely alloc-free for ASCII tokens
        // (which JWT and reconnect tokens are).  The strict decode adds no
        // ASCII-path overhead since the validation is integrated into the
        // existing UTF-8 state machine.
        private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
            => StrictUtf8.GetString(bytes);
    }
}
