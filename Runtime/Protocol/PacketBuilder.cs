// RTMPE SDK — Runtime/Protocol/PacketBuilder.cs
//
// Builds outbound RTMPE packets with the correct 13-byte header.
//
// Header layout (all little-endian):
//  [0..1]  magic       : u16  = 0x5254 ("RT")
//  [2]     version     : u8   = 5
//  [3]     packet_type : u8
//  [4]     flags       : u8
//  [5..8]  sequence    : u32  (monotonically increasing, per-connection)
//  [9..12] payload_len : u32
//
// The sequence counter is an instance field (NOT static) so each connection
// has its own independent counter. Sharing a PacketBuilder across connections
// is a protocol error.

using System;
using System.Threading;
using RTMPE.Core;

namespace RTMPE.Protocol
{
    /// <summary>
    /// Builds RTMPE packets: the header followed by the payload.
    /// </summary>
    /// <remarks>
    /// Each instance numbers its packets with its own sequence counter, so use
    /// one instance per connection and never share it between connections.
    /// All methods are safe to call from any thread. Used by the SDK; not
    /// intended to be called from game code.
    /// </remarks>
    public sealed class PacketBuilder
    {
        // The sequence counter wraps naturally at uint.MaxValue (2^32-1).
        // To avoid the Unsafe.As IL2CPP issue, we store as int and cast to uint on write.
        // Initialised to -1 so the first Interlocked.Increment returns 0, matching the
        // gateway's expectation that the first packet carries sequence 0.
        private int _sequenceCounter = -1;

        // Midpoint observability: increments by 1 every time the int
        // counter crosses from int.MaxValue → int.MinValue, which
        // corresponds to the wire-domain u32 sequence transitioning from
        // 0x7FFF_FFFF → 0x8000_0000 — the MIDPOINT of u32 space, not the
        // full wrap.  This is the operationally-useful early signal: at
        // the midpoint there are still ~2 billion sends of headroom
        // before the gateway's replay-window logic begins observing
        // duplicate sequences after the actual u32 wrap, giving operators
        // a generous window to plan a re-handshake.  The TRUE u32 wrap
        // (0xFFFF_FFFF → 0x0000_0000) corresponds to the int counter
        // going from -1 to 0 — but at that point the warning is already
        // too late, so we deliberately fire the alert at the midpoint
        // crossing.
        private long _sequenceMidpointCrossingCount;

        /// <summary>
        /// Number of times the packet sequence counter has passed the midpoint
        /// of its 32-bit range (0x7FFF_FFFF to 0x8000_0000) since this builder
        /// was created. A non-zero value means about 2 billion packets remain
        /// before the counter wraps; reconnect before then. The first crossing
        /// also logs a warning.
        /// </summary>
        public long SequenceMidpointCrossingCount =>
            System.Threading.Interlocked.Read(ref _sequenceMidpointCrossingCount);

        /// <summary>
        /// The sequence number of the last packet built, as it appears on the
        /// wire; <see cref="uint.MaxValue"/> before the first packet (the first
        /// packet carries 0).
        /// </summary>
        public uint CurrentSequence => (uint)Volatile.Read(ref _sequenceCounter);

        // ── Public factory methods ────────────────────────────────────────────

        /// <summary>
        /// Builds the packet that opens a handshake, carrying the sealed API key.
        /// </summary>
        /// <param name="encryptedApiKeyPayload">
        /// The X25519 sealed box from <see cref="Crypto.SealedApiKeyCipher"/>.
        /// </param>
        /// <param name="sealedApiKey">
        /// Sets <see cref="PacketFlags.SealedApiKey"/>. The server refuses a
        /// <c>HandshakeInit</c> without this flag, so pass
        /// <see langword="true"/>.
        /// </param>
        /// <returns>The complete packet.</returns>
        public byte[] BuildHandshakeInit(byte[] encryptedApiKeyPayload, bool sealedApiKey)
            => Build(
                PacketType.HandshakeInit,
                sealedApiKey ? PacketFlags.SealedApiKey : PacketFlags.None,
                encryptedApiKeyPayload);

        /// <summary>
        /// Builds the client's handshake response, preferring
        /// <see cref="WireFormat.Default"/> and advertising no capability flags.
        /// </summary>
        /// <param name="clientPublicKey">The client's 32-byte X25519 ephemeral public key.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException"><paramref name="clientPublicKey"/> is not 32 bytes.</exception>
        public byte[] BuildHandshakeResponse(byte[] clientPublicKey)
            => BuildHandshakeResponse(
                clientPublicKey,
                WireFormat.Default,
                RTMPE.Core.Protocol.CapabilityFlags.None);

        /// <summary>
        /// Builds the client's handshake response with an explicit wire-format
        /// preference and no capability flags.
        /// </summary>
        /// <param name="clientPublicKey">The client's 32-byte X25519 ephemeral public key.</param>
        /// <param name="preferredWireFormat">The state-sync wire format the client prefers.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException"><paramref name="clientPublicKey"/> is not 32 bytes.</exception>
        public byte[] BuildHandshakeResponse(
            byte[] clientPublicKey,
            WireFormatVersion preferredWireFormat)
            => BuildHandshakeResponse(
                clientPublicKey,
                preferredWireFormat,
                RTMPE.Core.Protocol.CapabilityFlags.None);

        /// <summary>
        /// Builds the client's handshake response with an explicit wire-format
        /// preference and capability flags, without the init-hash echo.
        /// </summary>
        /// <param name="clientPublicKey">The client's 32-byte X25519 ephemeral public key.</param>
        /// <param name="preferredWireFormat">The state-sync wire format the client prefers.</param>
        /// <param name="clientCaps">The capability flags the client advertises.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="clientPublicKey"/> is not 32 bytes, or
        /// <paramref name="clientCaps"/> includes
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.InitHashEcho"/>, which
        /// requires the echo; use the four-parameter overload then.
        /// </exception>
        public byte[] BuildHandshakeResponse(
            byte[] clientPublicKey,
            WireFormatVersion preferredWireFormat,
            RTMPE.Core.Protocol.CapabilityFlags clientCaps)
            => BuildHandshakeResponse(
                clientPublicKey,
                preferredWireFormat,
                clientCaps,
                initHashEcho: default);

        /// <summary>
        /// Builds the client's handshake response with an explicit wire-format
        /// preference, capability flags and, when the flags include
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.InitHashEcho"/>, the
        /// 32-byte init-hash echo.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The capability flags are carried only when there are any, and the
        /// echo only when the flags include InitHashEcho; an echo passed without
        /// that flag is ignored. The server replies with
        /// its own capability flags in the <c>SessionAck</c>, and the session
        /// uses only the flags both sides set; the server does not report
        /// which wire format it selected.
        /// </para>
        /// </remarks>
        /// <param name="clientPublicKey">The client's 32-byte X25519 ephemeral public key.</param>
        /// <param name="preferredWireFormat">The state-sync wire format the client prefers.</param>
        /// <param name="clientCaps">The capability flags the client advertises.</param>
        /// <param name="initHashEcho">
        /// SHA-256 of the <c>HandshakeInit</c> payload, from
        /// <see cref="ComputeInitHashEcho"/>. Required, and exactly 32 bytes,
        /// when <paramref name="clientCaps"/> includes InitHashEcho.
        /// </param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="clientPublicKey"/> is not 32 bytes, or
        /// <paramref name="clientCaps"/> includes InitHashEcho and
        /// <paramref name="initHashEcho"/> is not 32 bytes.
        /// </exception>
        public byte[] BuildHandshakeResponse(
            byte[] clientPublicKey,
            WireFormatVersion preferredWireFormat,
            RTMPE.Core.Protocol.CapabilityFlags clientCaps,
            ReadOnlySpan<byte> initHashEcho)
        {
            if (clientPublicKey == null || clientPublicKey.Length != 32)
                throw new ArgumentException("clientPublicKey must be exactly 32 bytes.", nameof(clientPublicKey));

            bool advertisesEcho =
                (clientCaps & RTMPE.Core.Protocol.CapabilityFlags.InitHashEcho) != 0;

            if (advertisesEcho && initHashEcho.Length != InitHashEchoLen)
            {
                throw new ArgumentException(
                    $"initHashEcho must be exactly {InitHashEchoLen} bytes when " +
                    $"CapabilityFlags.InitHashEcho is advertised (got {initHashEcho.Length}).",
                    nameof(initHashEcho));
            }

            // Omit the cap tail when nothing is advertised so the on-wire
            // packet stays byte-identical to the pre-cap shape that the
            // legacy gateway parser accepts.  New gateways treat an
            // absent tail as `CapabilityFlags.None` per the parser
            // contract, so the two encodings of "no caps" are observably
            // equivalent — emitting the shorter form keeps the bytes the
            // operator sees in packet captures unchanged for the common
            // case.
            bool emitCaps = clientCaps != RTMPE.Core.Protocol.CapabilityFlags.None;
            int payloadLen;
            if (advertisesEcho)
            {
                // 32 (pub key) + 1 (wire format) + 4 (caps) + 32 (echo)
                payloadLen = InitHashEchoPayloadLen;
            }
            else if (emitCaps)
            {
                payloadLen = 33 + RTMPE.Core.Protocol.CapabilityFlagsWire.WireSize;
            }
            else
            {
                payloadLen = 33;
            }

            // Allocating a fresh buffer keeps the caller's
            // `clientPublicKey` slice untouched (zeroising is the
            // consumer's responsibility upstream).
            var payload = new byte[payloadLen];
            Buffer.BlockCopy(clientPublicKey, 0, payload, 0, 32);
            payload[32] = (byte)preferredWireFormat;
            if (emitCaps)
            {
                RTMPE.Core.Protocol.CapabilityFlagsWire.WriteLittleEndian(
                    payload, offset: 33, clientCaps);
            }
            if (advertisesEcho)
            {
                initHashEcho.CopyTo(new Span<byte>(payload, InitHashEchoOffset, InitHashEchoLen));
            }
            return Build(PacketType.HandshakeResponse, PacketFlags.None, payload);
        }

        /// <summary>
        /// Length in bytes of the init-hash echo a handshake response carries:
        /// the size of a SHA-256 digest.
        /// </summary>
        public const int InitHashEchoLen = 32;

        /// <summary>
        /// Where the flags are in a built packet. The send path reads them there
        /// to decide whether the packet is sent reliably.
        /// </summary>
        public const int FlagsOffset = 4;

        /// <summary>
        /// Whether a built packet asks to be delivered reliably.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔑 A method rather than an expression at the call site, because the
        /// call site cannot be driven by a test: it lives on
        /// <c>NetworkManager</c>, a <c>MonoBehaviour</c> partial that no shard
        /// in this repository compiles, so everything written there is
        /// assertable only as source text. A source rule can see that a flag is
        /// READ; it cannot see whether the sense is right. Measured: inverting
        /// the bit test at the call site — routing every reliable control
        /// operation to the best-effort path and every unreliable one to the
        /// retransmit table — passed the whole suite.
        /// </para>
        /// <para>
        /// ⛔ <c>internal</c>, and deliberately: the paragraph above is the whole
        /// reason this is a method rather than an expression, and testability is
        /// not a reason to commit to an API. Every caller — the send path, and
        /// the shards, which compile this file into their own assembly — is
        /// inside the assembly boundary. A game has no packet of its own to ask
        /// the question about.
        /// </para>
        /// <para>
        /// ⚠️ A buffer too short to hold a flags byte is not reliable, and is
        /// not an exception either. The send path's job is to route it, not to
        /// judge it; a packet that short is refused further down where the
        /// refusal has a caller to report to.
        /// </para>
        /// </remarks>
        internal static bool AsksForReliableDelivery(byte[] packet) =>
            packet != null
            && packet.Length > FlagsOffset
            && (packet[FlagsOffset] & (byte)PacketFlags.Reliable) != 0;

        /// <summary>
        /// Offset within the <see cref="PacketType.HandshakeResponse"/>
        /// payload at which the init-hash echo begins: after the 32-byte
        /// ephemeral public key, the 1-byte wire-format version and the 4-byte
        /// capability flags.
        /// </summary>
        public const int InitHashEchoOffset = 37;

        /// <summary>
        /// Payload length of a <see cref="PacketType.HandshakeResponse"/> that
        /// carries the init-hash echo: <see cref="InitHashEchoOffset"/> +
        /// <see cref="InitHashEchoLen"/> = 69 bytes.
        /// </summary>
        public const int InitHashEchoPayloadLen = InitHashEchoOffset + InitHashEchoLen;

        /// <summary>
        /// Computes the SHA-256 of the <see cref="PacketType.HandshakeInit"/>
        /// payload, for the <c>initHashEcho</c> argument of
        /// <see cref="BuildHandshakeResponse(byte[], WireFormatVersion, RTMPE.Core.Protocol.CapabilityFlags, ReadOnlySpan{byte})"/>.
        /// </summary>
        /// <remarks>
        /// Pass exactly the bytes given to <see cref="BuildHandshakeInit"/> as
        /// <c>encryptedApiKeyPayload</c>: the server hashes the payload it
        /// received, so any difference makes the echo fail to match.
        /// </remarks>
        /// <param name="handshakeInitCiphertext">The <c>HandshakeInit</c> payload that was sent.</param>
        /// <returns>The 32-byte SHA-256 digest.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handshakeInitCiphertext"/> is null.</exception>
        public static byte[] ComputeInitHashEcho(byte[] handshakeInitCiphertext)
        {
            if (handshakeInitCiphertext == null)
                throw new ArgumentNullException(nameof(handshakeInitCiphertext));
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(handshakeInitCiphertext);
        }

        /// <summary>
        /// Builds the request that resumes a session with its reconnect token,
        /// with an IP-migration proof.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reconnect token is single-use. The server answers with a
        /// <see cref="PacketType.Challenge"/>, which the client completes with
        /// a <see cref="PacketType.HandshakeResponse"/> as in a first
        /// connection. The proof lets the server accept the reconnect from a
        /// different IP address.
        /// </para>
        /// </remarks>
        /// <param name="reconnectToken">
        /// The token received in the previous <c>SessionAck</c>: a non-empty
        /// string of at most 128 bytes in UTF-8.
        /// </param>
        /// <param name="clientEphemeralPublicKey">
        /// The client's 32-byte X25519 ephemeral public key for this handshake.
        /// </param>
        /// <param name="proof">
        /// The 32-byte proof from <see cref="ComputeReconnectProof"/>.
        /// </param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="proof"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="reconnectToken"/> is null, empty or longer than 128
        /// bytes in UTF-8, or <paramref name="clientEphemeralPublicKey"/> or
        /// <paramref name="proof"/> is not 32 bytes.
        /// </exception>
        public byte[] BuildReconnectInit(
            string reconnectToken, byte[] clientEphemeralPublicKey, byte[] proof)
        {
            if (proof == null)
                throw new ArgumentNullException(nameof(proof),
                    "proof is required.  Compute it via " +
                    nameof(ComputeReconnectProof) +
                    "(token, ipMigrationKey), or call " +
                    nameof(BuildReconnectInitWithoutProof) +
                    " when no IP-migration key was negotiated.");
            if (proof.Length != 32)
                throw new ArgumentException("proof must be exactly 32 bytes.", nameof(proof));

            return BuildReconnectInitInternal(reconnectToken, clientEphemeralPublicKey, proof);
        }

        /// <summary>
        /// Builds the request that resumes a session with its reconnect token,
        /// without an IP-migration proof.
        /// </summary>
        /// <remarks>
        /// Use it only when the previous session produced no IP-migration key.
        /// Otherwise use <see cref="BuildReconnectInit"/> with a proof, which
        /// lets the server accept the reconnect from a different IP address,
        /// for example after a move from Wi-Fi to mobile data.
        /// </remarks>
        /// <param name="reconnectToken">
        /// The token received in the previous <c>SessionAck</c>: a non-empty
        /// string of at most 128 bytes in UTF-8.
        /// </param>
        /// <param name="clientEphemeralPublicKey">
        /// The client's 32-byte X25519 ephemeral public key for this handshake.
        /// </param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="reconnectToken"/> is null, empty or longer than 128
        /// bytes in UTF-8, or <paramref name="clientEphemeralPublicKey"/> is not
        /// 32 bytes.
        /// </exception>
        public byte[] BuildReconnectInitWithoutProof(
            string reconnectToken, byte[] clientEphemeralPublicKey)
        {
            return BuildReconnectInitInternal(reconnectToken, clientEphemeralPublicKey, null);
        }

        /// <summary>
        /// Computes the 32-byte IP-migration proof for
        /// <see cref="BuildReconnectInit"/>, from the previous session's
        /// IP-migration key.
        /// </summary>
        /// <param name="reconnectToken">
        /// The token received in the previous <c>SessionAck</c>.
        /// </param>
        /// <param name="clientEphemeralPublicKey">
        /// The client's 32-byte X25519 ephemeral public key for this handshake,
        /// as passed to <see cref="BuildReconnectInit"/>.
        /// </param>
        /// <param name="ipMigrationKey">
        /// The 32-byte IP-migration key from the previous session (see
        /// <c>HandshakeHandler.DeriveSessionKeys</c>).
        /// </param>
        /// <returns>The 32-byte proof.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="reconnectToken"/> is null or empty, or a key is not
        /// 32 bytes.
        /// </exception>
        public static byte[] ComputeReconnectProof(
            string reconnectToken, byte[] clientEphemeralPublicKey, byte[] ipMigrationKey)
        {
            if (string.IsNullOrEmpty(reconnectToken))
                throw new ArgumentException("reconnectToken must not be null or empty.", nameof(reconnectToken));
            if (clientEphemeralPublicKey == null || clientEphemeralPublicKey.Length != 32)
                throw new ArgumentException(
                    "clientEphemeralPublicKey must be exactly 32 bytes.",
                    nameof(clientEphemeralPublicKey));
            if (ipMigrationKey == null || ipMigrationKey.Length != 32)
                throw new ArgumentException("ipMigrationKey must be exactly 32 bytes.", nameof(ipMigrationKey));

            // HMAC over token ‖ commitment.  Over the token alone the proof
            // travels beside the very credential it authorises — both in
            // cleartext — so the pair is a bearer token any observer can lift and
            // present from any address.  Covering the key means a copied pair can
            // only re-offer the key it was minted for, whose private half the
            // copier does not hold.  Unambiguous without a separator because the
            // commitment is fixed-width and last.
            var tokenBytes = System.Text.Encoding.UTF8.GetBytes(reconnectToken);
            var message = new byte[tokenBytes.Length + 32];
            Buffer.BlockCopy(tokenBytes, 0, message, 0, tokenBytes.Length);
            Buffer.BlockCopy(clientEphemeralPublicKey, 0, message, tokenBytes.Length, 32);
            using var hmac = new System.Security.Cryptography.HMACSHA256(ipMigrationKey);
            return hmac.ComputeHash(message);
        }

        /// <summary>
        /// Length in bytes of a resume proof.
        /// </summary>
        public const int ResumeProofLen = 32;

        /// <summary>
        /// Computes the resume proof a reconnect sends in its
        /// <see cref="PacketType.HandshakeResponse"/>, from the previous
        /// session's IP-migration key and the server's ephemeral key for this
        /// attempt.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A server that issued the reconnect token under
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.ResumeProof"/> redeems
        /// it only when this proof verifies. It is made over a key the server
        /// chose for this attempt, so a copy of the reconnect request replayed by
        /// someone else cannot produce it.
        /// </para>
        /// </remarks>
        /// <param name="reconnectToken">The token received in the previous <c>SessionAck</c>.</param>
        /// <param name="clientEphemeralPublicKey">
        /// The client's 32-byte X25519 ephemeral public key for this handshake.
        /// </param>
        /// <param name="serverEphemeralPublicKey">
        /// The server's 32-byte X25519 ephemeral public key from the
        /// <see cref="PacketType.Challenge"/> this attempt received.
        /// </param>
        /// <param name="ipMigrationKey">The 32-byte IP-migration key from the previous session.</param>
        /// <returns>The 32-byte proof.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="reconnectToken"/> is null or empty, or a key is not
        /// 32 bytes.
        /// </exception>
        public static byte[] ComputeResumeProof(
            string reconnectToken,
            byte[] clientEphemeralPublicKey,
            byte[] serverEphemeralPublicKey,
            byte[] ipMigrationKey)
        {
            if (string.IsNullOrEmpty(reconnectToken))
                throw new ArgumentException("reconnectToken must not be null or empty.", nameof(reconnectToken));
            if (clientEphemeralPublicKey == null || clientEphemeralPublicKey.Length != 32)
                throw new ArgumentException(
                    "clientEphemeralPublicKey must be exactly 32 bytes.",
                    nameof(clientEphemeralPublicKey));
            if (serverEphemeralPublicKey == null || serverEphemeralPublicKey.Length != 32)
                throw new ArgumentException(
                    "serverEphemeralPublicKey must be exactly 32 bytes.",
                    nameof(serverEphemeralPublicKey));
            if (ipMigrationKey == null || ipMigrationKey.Length != 32)
                throw new ArgumentException("ipMigrationKey must be exactly 32 bytes.", nameof(ipMigrationKey));

            // HMAC-SHA256(ipMigrationKey, label ‖ token ‖ client_ephemeral_pub ‖
            // server_ephemeral_pub) — the ReconnectInit proof's message plus the
            // one value the GATEWAY chose for this attempt (audit GC-01).  The
            // label keeps it from ever being read as that proof.  Unambiguous
            // without a separator: the label leads, and the two keys are
            // fixed-width and last.  The gateway's `resume_proof_message` writes
            // the same bytes; a vector in each suite holds them together.
            var label = System.Text.Encoding.ASCII.GetBytes(ResumeProofLabel);
            var tokenBytes = System.Text.Encoding.UTF8.GetBytes(reconnectToken);
            var message = new byte[label.Length + tokenBytes.Length + 64];
            Buffer.BlockCopy(label, 0, message, 0, label.Length);
            Buffer.BlockCopy(tokenBytes, 0, message, label.Length, tokenBytes.Length);
            Buffer.BlockCopy(clientEphemeralPublicKey, 0, message, label.Length + tokenBytes.Length, 32);
            Buffer.BlockCopy(serverEphemeralPublicKey, 0, message, label.Length + tokenBytes.Length + 32, 32);
            using var hmac = new System.Security.Cryptography.HMACSHA256(ipMigrationKey);
            return hmac.ComputeHash(message);
        }

        // Domain label of the resume proof; the gateway's RESUME_PROOF_LABEL.
        private const string ResumeProofLabel = "RTMPE-RECONNECT-RESUME-v1";

        /// <summary>
        /// Builds a reconnect's <see cref="PacketType.HandshakeResponse"/>,
        /// carrying the resume proof.
        /// </summary>
        /// <param name="clientPublicKey">The client's 32-byte X25519 ephemeral public key.</param>
        /// <param name="preferredWireFormat">The state-sync wire format the client prefers.</param>
        /// <param name="clientCaps">The capability flags the client advertises.</param>
        /// <param name="resumeProof">The 32-byte proof from <see cref="ComputeResumeProof"/>.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="clientPublicKey"/> or <paramref name="resumeProof"/>
        /// is not 32 bytes.
        /// </exception>
        public byte[] BuildReconnectResponse(
            byte[] clientPublicKey,
            WireFormatVersion preferredWireFormat,
            RTMPE.Core.Protocol.CapabilityFlags clientCaps,
            ReadOnlySpan<byte> resumeProof)
        {
            if (clientPublicKey == null || clientPublicKey.Length != 32)
                throw new ArgumentException("clientPublicKey must be exactly 32 bytes.", nameof(clientPublicKey));
            if (resumeProof.Length != ResumeProofLen)
                throw new ArgumentException(
                    $"resumeProof must be exactly {ResumeProofLen} bytes (got {resumeProof.Length}).",
                    nameof(resumeProof));

            // [client_pub:32][wire:1][caps:4][resume_proof:32] — the proof takes
            // the slot a first connection's init-hash echo takes: a reconnect
            // has no sealed envelope to echo, and one position for "this answer
            // belongs to its Round 1" keeps the layout one layout.
            var payload = new byte[InitHashEchoPayloadLen];
            Buffer.BlockCopy(clientPublicKey, 0, payload, 0, 32);
            payload[32] = (byte)preferredWireFormat;
            RTMPE.Core.Protocol.CapabilityFlagsWire.WriteLittleEndian(payload, offset: 33, clientCaps);
            resumeProof.CopyTo(new Span<byte>(payload, InitHashEchoOffset, ResumeProofLen));
            return Build(PacketType.HandshakeResponse, PacketFlags.None, payload);
        }

        /// <summary>
        /// Length in bytes of the cookie a server sends when it asks the client to
        /// show it can be reached before it opens the handshake.
        /// </summary>
        public const int HandshakeCookieLen = 16;

        /// <summary>
        /// Builds the request that opens a handshake again, carrying the cookie a
        /// <see cref="PacketType.HandshakeCookie"/> asked for.
        /// </summary>
        /// <remarks>
        /// A busy server opens only so many handshakes a second for addresses it
        /// has not heard from; past that it answers with a cookie. Sending the
        /// request again with the cookie shows the server this client receives
        /// at the address it sends from, and the server then opens it. The sealed
        /// API key is the one the first request carried, unchanged.
        /// </remarks>
        /// <param name="cookie">The <see cref="HandshakeCookieLen"/>-byte cookie the server sent.</param>
        /// <param name="encryptedApiKeyPayload">
        /// The sealed box the first request carried, as passed to
        /// <see cref="BuildHandshakeInit"/>.
        /// </param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="cookie"/> is not <see cref="HandshakeCookieLen"/> bytes,
        /// or <paramref name="encryptedApiKeyPayload"/> is null or empty.
        /// </exception>
        public byte[] BuildCookieHandshakeInit(ReadOnlySpan<byte> cookie, byte[] encryptedApiKeyPayload)
        {
            if (cookie.Length != HandshakeCookieLen)
                throw new ArgumentException(
                    $"cookie must be exactly {HandshakeCookieLen} bytes (got {cookie.Length}).",
                    nameof(cookie));
            if (encryptedApiKeyPayload == null || encryptedApiKeyPayload.Length == 0)
                throw new ArgumentException(
                    "encryptedApiKeyPayload must not be null or empty.",
                    nameof(encryptedApiKeyPayload));

            // [cookie:16][sealed envelope] — the envelope exactly as the
            // HandshakeInit carried it, so the Challenge transcript and the
            // Round-2 echo, which the gateway computes over the envelope alone,
            // are the ones this client already holds (audit GW-ING-05).
            var payload = new byte[HandshakeCookieLen + encryptedApiKeyPayload.Length];
            cookie.CopyTo(new Span<byte>(payload, 0, HandshakeCookieLen));
            Buffer.BlockCopy(encryptedApiKeyPayload, 0, payload, HandshakeCookieLen, encryptedApiKeyPayload.Length);
            return Build(PacketType.CookieHandshakeInit, PacketFlags.SealedApiKey, payload);
        }

        private byte[] BuildReconnectInitInternal(
            string reconnectToken, byte[] clientEphemeralPublicKey, byte[] proof)
        {
            if (string.IsNullOrEmpty(reconnectToken))
                throw new ArgumentException("reconnectToken must not be null or empty.", nameof(reconnectToken));
            if (clientEphemeralPublicKey == null || clientEphemeralPublicKey.Length != 32)
                throw new ArgumentException(
                    "clientEphemeralPublicKey must be exactly 32 bytes.",
                    nameof(clientEphemeralPublicKey));

            var tokenBytes = System.Text.Encoding.UTF8.GetBytes(reconnectToken);
            if (tokenBytes.Length > 128)
                throw new ArgumentException(
                    $"reconnectToken UTF-8 length {tokenBytes.Length} exceeds 128 bytes (gateway cap).",
                    nameof(reconnectToken));

            // Payload:
            //   [token_len: u16 LE][token: N][client_ephemeral_pub: 32][proof: 32 optional]
            //
            // The commitment names the key Round 2 must offer, as the fresh
            // flow's envelope does.  It is mandatory here for the same reason it
            // is mandatory there — without it the gateway completes ECDH with
            // whoever answers the Challenge — and it matters more here, because
            // this payload is cleartext.
            int proofLen = proof != null ? 32 : 0;
            var payload = new byte[2 + tokenBytes.Length + 32 + proofLen];
            payload[0] = (byte)(tokenBytes.Length & 0xFF);
            payload[1] = (byte)((tokenBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(tokenBytes, 0, payload, 2, tokenBytes.Length);
            Buffer.BlockCopy(clientEphemeralPublicKey, 0, payload, 2 + tokenBytes.Length, 32);
            if (proof != null)
                Buffer.BlockCopy(proof, 0, payload, 2 + tokenBytes.Length + 32, 32);

            return Build(PacketType.ReconnectInit, PacketFlags.None, payload);
        }

        /// <summary>
        /// Builds a keep-alive packet with no payload.
        /// </summary>
        /// <returns>The complete packet.</returns>
        public byte[] BuildHeartbeat()
            => Build(PacketType.Heartbeat, PacketFlags.None, Array.Empty<byte>());

        /// <summary>
        /// Builds the packet that ends a session, with no payload.
        /// </summary>
        /// <returns>The complete packet.</returns>
        public byte[] BuildDisconnect()
            => Build(PacketType.Disconnect, PacketFlags.None, Array.Empty<byte>());

        /// <summary>
        /// Builds a data packet.
        /// </summary>
        /// <param name="payload">The payload; <see langword="null"/> builds an empty one.</param>
        /// <param name="flags">Header flags, for example <see cref="PacketFlags.Encrypted"/> or <see cref="PacketFlags.Compressed"/>.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException"><paramref name="payload"/> is longer than <see cref="MaxApplicationPayloadBytes"/>.</exception>
        public byte[] BuildData(byte[] payload, PacketFlags flags = PacketFlags.None)
            => Build(PacketType.Data, flags, payload ?? Array.Empty<byte>());

        /// <summary>
        /// Builds a diagnostics packet carrying a batch of diagnostic log
        /// entries. It is sent without
        /// the reliable flag, and encrypted like the session's other packets.
        /// </summary>
        /// <param name="payload">The batch; <see langword="null"/> builds an empty one.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException"><paramref name="payload"/> is longer than <see cref="MaxApplicationPayloadBytes"/>.</exception>
        public byte[] BuildDiagnostics(byte[] payload)
            => Build(PacketType.Diagnostics, PacketFlags.None, payload ?? Array.Empty<byte>());

        // ── Core builder ──────────────────────────────────────────────────────

        /// <summary>
        /// Upper bound on a packet's payload length (1 MiB), the same limit
        /// <see cref="PacketParser"/> applies when reading. <c>Build</c> checks
        /// the much smaller <see cref="MaxApplicationPayloadBytes"/> first, so
        /// that is the limit a caller meets in practice.
        /// </summary>
        public const int MaxPayloadBytes = 1 * 1024 * 1024;

        /// <summary>
        /// The largest payload the server may send in one packet: 65,507 bytes.
        /// </summary>
        /// <remarks>
        /// This bounds what the client must be able to receive, not what it may
        /// send: outbound packets are held to
        /// <see cref="MaxApplicationPayloadBytes"/> so that they fit in one
        /// datagram on any link.
        /// </remarks>
        public const int GatewayMaxPayloadBytes = 65_507;

        // What the AEAD envelope adds to a payload on its way to the wire: a
        // 4-byte little-endian sequence prefix sealed with the plaintext, and
        // the 16-byte Poly1305 tag the seal appends.  Named separately because
        // they answer to different constants on the far side —
        // `SEQ_PREFIX_LEN` (the width of the u32 the gateway carries through
        // the envelope) and `TAG_LEN` — and a guard that holds them together
        // has to name each.
        private const int AeadSequencePrefixBytes = 4;
        private const int AeadTagBytes = 16;

        // Appended by the EncryptAndSend pipeline; documented in
        // NetworkManager.cs's encrypt path.  An application caller hands
        // plaintext to the builder, so the outbound cap below pre-deducts what
        // AEAD will add later.
        private const int AeadOverheadBytes = AeadSequencePrefixBytes + AeadTagBytes;

        /// <summary>
        /// The largest datagram a UDP path can carry (65,527 bytes), and so the
        /// smallest receive buffer that can hold any datagram that arrives.
        /// </summary>
        /// <remarks>
        /// The UDP length field is 16 bits and counts the 8-byte UDP header, so
        /// 65,527 bytes is the largest payload over IPv6 (65,507 over IPv4). A
        /// datagram larger than the buffer it is read into is lost before any
        /// parser runs, and nothing reports the loss, so
        /// <c>NetworkSettings.networkThreadBufferBytes</c> is never allowed
        /// below this value.
        /// </remarks>
        public const int MaxDatagramBytes = 65_527;

        // The transport-side datagram envelope is bounded so a legitimately
        // built packet survives every link in the path (PPPoE, IPsec, IPv6
        // minimum-MTU networks).  Constants below mirror UdpTransport's
        // DefaultMaxDatagramSize without taking a Transport assembly
        // dependency from Protocol — the literal 1200 is documented in
        // UdpTransport.DefaultMaxDatagramSize and the two values must move
        // together.  An automated guard against drift is provided by the
        // transport-suite test "DatagramAndApplicationCapsAreInSync".
        private const int DefaultDatagramSizeMirror = 1200;

        // The encrypt path writes up to three 4-byte sub-headers between the
        // fixed header and the ciphertext — arq_seq, app_seq and gameplay_seq,
        // each gated by its own flag (see NetworkManager.AeadPipeline).  They
        // are not part of the payload a caller hands in, so the cap has to
        // reserve them on the caller's behalf.  All three are budgeted rather
        // than the count a given send happens to use: the flags are independent
        // runtime settings, so any smaller reservation is a cap that holds only
        // until a deployment enables one more of them.
        private const int MaxSubHeaderBytes = 3 * 4;

        /// <summary>
        /// The largest payload a packet can carry and still fit in one
        /// 1200-byte datagram (<c>UdpTransport.DefaultMaxDatagramSize</c>) once
        /// the packet's header and the encryption overhead are added: 1155 bytes.
        /// </summary>
        /// <remarks>
        /// <c>Build</c> throws <see cref="ArgumentException"/> for a larger
        /// payload, so an oversized message fails where it is built rather than
        /// being fragmented or lost later. Split larger data at the application
        /// level.
        /// </remarks>
        public const int MaxApplicationPayloadBytes =
            DefaultDatagramSizeMirror
            - PacketProtocol.HEADER_SIZE
            - MaxSubHeaderBytes
            - AeadOverheadBytes;

        /// <summary>
        /// Builds a complete packet: the header followed by
        /// <paramref name="payload"/>. Each call takes the next sequence
        /// number.
        /// </summary>
        /// <param name="type">The packet type.</param>
        /// <param name="flags">The header flags.</param>
        /// <param name="payload">The payload; <see langword="null"/> builds an empty one.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="payload"/> is longer than <see cref="MaxApplicationPayloadBytes"/>.
        /// </exception>
        public byte[] Build(PacketType type, PacketFlags flags, byte[] payload)
        {
            if (payload == null) payload = Array.Empty<byte>();
            return Build(type, flags, payload, payload.Length);
        }

        /// <summary>
        /// Builds a complete packet from the first
        /// <paramref name="payloadLength"/> bytes of <paramref name="payload"/>,
        /// for a buffer that is longer than its content, such as one rented
        /// from <c>ArrayPool&lt;byte&gt;.Shared</c>.
        /// </summary>
        /// <param name="type">The packet type.</param>
        /// <param name="flags">The header flags.</param>
        /// <param name="payload">The buffer holding the payload; <see langword="null"/> reads as empty.</param>
        /// <param name="payloadLength">Number of payload bytes at the start of <paramref name="payload"/>.</param>
        /// <returns>The complete packet.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="payloadLength"/> is negative or greater than the
        /// length of <paramref name="payload"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="payloadLength"/> is greater than
        /// <see cref="MaxApplicationPayloadBytes"/>.
        /// </exception>
        public byte[] Build(PacketType type, PacketFlags flags, byte[] payload, int payloadLength)
        {
            if (payload == null) payload = Array.Empty<byte>();
            if (payloadLength < 0 || payloadLength > payload.Length)
                throw new ArgumentOutOfRangeException(nameof(payloadLength),
                    $"payloadLength {payloadLength} must be in [0, payload.Length={payload.Length}].");

            // Application-layer cap fires first.  Application payloads
            // exceeding the transport MTU envelope silently rely on IP
            // fragmentation (poor on mobile / CGNAT); rejecting at the
            // builder makes the failure diagnosable at the call site instead
            // of late in the pipeline as an opaque SocketException.
            EnsureFitsInDatagram(payloadLength);

            if (payloadLength > MaxPayloadBytes)
                throw new ArgumentException(
                    $"payload length {payloadLength} exceeds PacketBuilder.MaxPayloadBytes ({MaxPayloadBytes})",
                    nameof(payloadLength));

            // Atomic increment — no Unsafe.As required; cast uint at write time.
            // Interlocked.Increment returns int; casting to uint handles wrap-around correctly.
            int rawSeq = Interlocked.Increment(ref _sequenceCounter);
            uint seq   = (uint)rawSeq;
            // Midpoint detection: when the int counter increments from
            // int.MaxValue (= u32 0x7FFFFFFF) to int.MinValue (= u32
            // 0x80000000), the wire-domain u32 has crossed the midpoint
            // of its space.  At this point ~2 billion further sends remain
            // before the actual wrap (u32 0xFFFFFFFF → 0x00000000); the
            // alert fires here precisely so operators have time to plan a
            // re-handshake well before the gateway's replay-window dedup
            // begins observing duplicate sequences.  Log exactly once
            // per builder lifetime.
            if (rawSeq == int.MinValue)
            {
                long count = Interlocked.Increment(ref _sequenceMidpointCrossingCount);
                if (count == 1)
                {
                    UnityEngine.Debug.LogWarning(
                        "[RTMPE] PacketBuilder: wire sequence counter just crossed " +
                        "the u32 midpoint.  A full u32 wrap will occur after another " +
                        "~2 billion sends; plan a re-handshake before the gateway's " +
                        "replay-window dedup begins observing duplicate sequences.");
                }
            }

            var packet = new byte[PacketProtocol.HEADER_SIZE + payloadLength];

            // [0..1] magic (LE u16 = 0x5254)
            packet[0] = (byte)(PacketProtocol.MAGIC & 0xFF);
            packet[1] = (byte)(PacketProtocol.MAGIC >> 8);

            // [2] version
            packet[2] = PacketProtocol.VERSION;

            // [3] type
            packet[3] = (byte)type;

            // [4] flags
            packet[FlagsOffset] = (byte)flags;

            // [5..8] sequence (LE u32)
            packet[5] = (byte)(seq);
            packet[6] = (byte)(seq >> 8);
            packet[7] = (byte)(seq >> 16);
            packet[8] = (byte)(seq >> 24);

            // [9..12] payload_len (LE u32)
            uint payloadLen = (uint)payloadLength;
            packet[9]  = (byte)(payloadLen);
            packet[10] = (byte)(payloadLen >> 8);
            packet[11] = (byte)(payloadLen >> 16);
            packet[12] = (byte)(payloadLen >> 24);

            // Payload
            if (payloadLength > 0)
                Buffer.BlockCopy(payload, 0, packet, PacketProtocol.HEADER_SIZE, payloadLength);

            return packet;
        }

        /// <summary>
        /// Checks that a payload of <paramref name="payloadLength"/> bytes fits
        /// in one datagram once the header and encryption overhead are added.
        /// <c>Build</c> makes the same check, so call this only to test a size
        /// before building a payload.
        /// </summary>
        /// <param name="payloadLength">The payload length to check.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/> is negative.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="payloadLength"/> is greater than <see cref="MaxApplicationPayloadBytes"/>.
        /// </exception>
        public static void EnsureFitsInDatagram(int payloadLength)
        {
            if (payloadLength < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadLength),
                    "payloadLength must be non-negative.");
            if (payloadLength > MaxApplicationPayloadBytes)
                throw new ArgumentException(
                    $"payload length {payloadLength} exceeds " +
                    $"PacketBuilder.MaxApplicationPayloadBytes ({MaxApplicationPayloadBytes}). " +
                    "Fragment the message at the application layer; do not rely on IP fragmentation.",
                    nameof(payloadLength));
        }
    }
}
