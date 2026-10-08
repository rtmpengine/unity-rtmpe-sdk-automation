// RTMPE SDK — Runtime/Core/Protocol/CapabilityFlags.cs
//
// Per-session capability bitmask exchanged inside the optional tails of
// HandshakeResponse (SDK → Gateway: `client_caps:4 LE`) and SessionAck
// (Gateway → SDK: `gateway_caps:4 LE`).  Each side advertises the set of
// optional protocol features it is willing AND able to honour for the
// duration of the session; the negotiated value is the bitwise AND of the
// two advertisements.  A feature only engages when both peers carry the
// same bit, which keeps every cap individually opt-in on either side and
// safe under mixed-version mesh deployments.
//
// Why a separate enum
// -------------------
// The wire field is a `u32 LE` so additional caps can land without ever
// touching the existing layout — the enum acts as the single canonical
// place where bit positions are reserved and documented.  Adding a new
// capability is a code-only change: pick the next free bit, name it
// here, add it to what `CapabilityFlagsWire.Advertised` answers if the SDK
// is to advertise it, and wire the gate at the consumer.  A bit that is
// declared and gated but never advertised negotiates to nothing on every
// session, with every test of the gate green.  Old peers that did not
// learn the new bit advertise it as 0 and the negotiation downgrades
// cleanly without coordination.
//
// Wire-format contract
// --------------------
// The bitmask is serialised little-endian on the wire.  The 32-bit width
// is enforced through the underlying `uint`; widening would be a wire-
// format break and must be done with a versioned successor field rather
// than by silently extending this one.

using System;

namespace RTMPE.Core.Protocol
{
    /// <summary>
    /// Protocol capability bits exchanged during the handshake. The SDK advertises
    /// the features it supports, the server advertises its own, and a negotiated
    /// feature is active for the session only when both advertised it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some bits are one-way server assertions rather than negotiated features: the
    /// SDK reads them from the server's advertisement and never advertises them
    /// itself. Each bit's documentation says which kind it is.
    /// </para>
    /// <para>
    /// On the wire the set is a 32-bit little-endian value (see
    /// <see cref="CapabilityFlagsWire"/>). A bit position, once used, is never
    /// reused for another feature. A bit not named here is sent as zero and
    /// ignored on receipt. The SDK negotiates these itself; game code does not need
    /// them.
    /// </para>
    /// </remarks>
    [Flags]
    public enum CapabilityFlags : uint
    {
        /// <summary>
        /// No optional feature. Also the value when a peer sends no capability
        /// field.
        /// </summary>
        None = 0u,

        /// <summary>
        /// Bit 0 — acknowledged reliable delivery. When both sides advertise it,
        /// the SDK retransmits a packet sent with <see cref="PacketFlags.Reliable"/>,
        /// up to a retry limit, until the server acknowledges it with a
        /// <see cref="PacketType.DataAck"/>. Without it, the SDK writes no
        /// <c>arq_seq</c> sub-header and clears the reliable flag whatever
        /// <see cref="RTMPE.Core.NetworkSettings.EmitArqSequence"/> says: the packet
        /// is sent once and no acknowledgement is expected.
        /// </summary>
        ArqAck = 1u << 0,

        /// <summary>
        /// Bit 1 — encrypted <see cref="PacketType.SessionAck"/>. When the SDK
        /// advertises it, the server encrypts the SessionAck payload with the
        /// bootstrap key derived from the handshake's key exchange and sets
        /// <see cref="PacketFlags.Encrypted"/>; the SDK decrypts the payload before
        /// reading the JWT and the reconnect token. The SDK always advertises it.
        /// Without it, the SessionAck payload is sent unencrypted.
        /// </summary>
        EncryptedSessionAck = 1u << 1,

        /// <summary>
        /// Bit 2 — the session JWT is signed with the server's Ed25519 identity key,
        /// the key that signs the <see cref="PacketType.Challenge"/>. A one-way server
        /// assertion.
        /// </summary>
        /// <remarks>
        /// When the bit is set and the <see cref="RTMPE.Core.NetworkSettings"/> asset
        /// configures no JWT key (<see cref="RTMPE.Core.NetworkSettings.jwtSigningKeyHex"/>
        /// or <see cref="RTMPE.Core.NetworkSettings.jwtSigningKeyPem"/>), the SDK
        /// verifies the JWT's signature with the identity key; a configured key
        /// always takes precedence. The identity key is as trustworthy as the
        /// server-key pinning mode makes it. Without the bit, the SDK verifies the
        /// JWT with its configured key alone.
        /// </remarks>
        IdentitySignedJwt = 1u << 2,

        /// <summary>
        /// Bit 3 — the SDK includes the 32-byte init-hash echo in every
        /// <see cref="PacketType.HandshakeResponse"/> that is not a reconnect. The
        /// echo is the SHA-256 of the encrypted <see cref="PacketType.HandshakeInit"/>
        /// payload and is written to <c>payload[37..69]</c>, immediately after the
        /// 4-byte capability field at <c>payload[33..37]</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A commitment rather than a negotiated feature: when the SDK advertises it,
        /// the server rejects a HandshakeResponse without the echo; when the SDK does
        /// not, the server accepts one without it.
        /// </para>
        /// <para>
        /// A reconnect (<see cref="PacketType.ReconnectInit"/>) carries no echo: the
        /// single-use reconnect token already ties the exchange together. The echo
        /// binds the key exchange to the HandshakeInit that authenticated the API key,
        /// so a party that captured that packet cannot complete the handshake with a
        /// key of its own.
        /// </para>
        /// </remarks>
        InitHashEcho = 1u << 3,

        /// <summary>
        /// Bit 4 — the <see cref="PacketType.SessionAck"/> states how long the
        /// reconnect token it carries lasts. A one-way server assertion. When the
        /// server sets it, the SessionAck payload carries
        /// <c>[reconnect_token_lifetime_secs:4 LE]</c> immediately after the server's
        /// capability field, and the SDK records it (see
        /// <see cref="RTMPE.Core.ReconnectTokenLife"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The field is present exactly when the server sets the bit. A reader that
        /// does not know the bit stops after the capability field and ignores the
        /// bytes that follow.
        /// </para>
        /// <para>
        /// The lifetime is a lower bound, because the server can renew a token: the
        /// SDK makes fewer reconnect attempts with a token past its stated lifetime,
        /// and still presents it. A lifetime of 0 means the token will be refused.
        /// When nothing is stated, the bit is clear.
        /// </para>
        /// </remarks>
        ReconnectTokenLifetime = 1u << 4,

        /// <summary>
        /// Bit 5 — <c>arq_seq</c> is part of the AEAD additional authenticated data
        /// in both directions. The SDK advertises it together with
        /// <see cref="ArqAck"/>; it is active when both sides advertised it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The 4-byte <c>arq_seq</c> precedes the ciphertext. A retransmission is
        /// encrypted afresh under the same <c>arq_seq</c> the packet was registered
        /// with, so authenticating the sequence costs four bytes of additional data.
        /// Authenticated, the sequence cannot be rewritten in transit to clear the
        /// wrong retransmit entry or to make later packets read as stale.
        /// </para>
        /// <para>
        /// Both sides must agree, because a mismatch fails authentication on every
        /// reliable packet. The binding is taken from the negotiated set, never from
        /// the packet itself.
        /// </para>
        /// </remarks>
        ArqSeqAad = 1u << 5,

        /// <summary>
        /// Bit 6 — the room's replay to players who join later follows each object's
        /// current owner. A one-way server assertion; it adds no field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With the bit set, the spawn replayed to a player who joins later names the
        /// owner the object has now, not the player who spawned it: when a player
        /// leaves, the server hands the objects that survive their owner to the
        /// host, and an ownership transfer updates the replayed spawn. A world object
        /// (<see cref="RTMPE.Core.RtmpeWorldAuthority"/>) handed to a new host
        /// therefore keeps its object id and instance.
        /// </para>
        /// <para>
        /// Without the bit, a new host spawns a copy of the world object under its
        /// own id range, with the state carried over, and removes the original. The
        /// behaviour follows the server the current session reached.
        /// </para>
        /// </remarks>
        ReplayFollowsOwner = 1u << 6,

        /// <summary>
        /// Bit 7 — the server attests the caller of every RPC it relays. A one-way
        /// server assertion; it adds no field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With the bit set, the server writes the <c>rpc_flags</c> byte of every
        /// Enhanced RPC it relays, replacing the caller's byte with what the server
        /// knows: whether the caller owns the addressed object and whether it is the
        /// room's host. The flag that marks a call sent by the server itself is never
        /// relayed from a client.
        /// </para>
        /// <para>
        /// <see cref="RTMPE.Rpc.RtmpeRpcAttribute.Caller"/> relies on it: a receiver
        /// decides from those facts who may call a method. Without the bit, the byte
        /// is only the caller's own claim, so a method that declares a caller refuses
        /// every call.
        /// </para>
        /// </remarks>
        AttestedRpcCaller = 1u << 7,

        /// <summary>
        /// Bit 8 — the server says whether the room's host sent each spawn it
        /// relays. A one-way server assertion; it adds no field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With the bit set, the server writes the lifetime byte of every spawn it
        /// relays, including the copy it stores for players who join later: <see cref="SpawnLifetimeFlags.DestroyWithOwner"/>
        /// when the spawner declared it,
        /// <see cref="SpawnLifetimeFlags.SpawnedByHost"/> when the object survives
        /// its owner and the room's host sent it, and
        /// <see cref="SpawnLifetimeFlags.SurvivesOwner"/> otherwise.
        /// </para>
        /// <para>
        /// <see cref="RtmpeWorldAuthority"/> relies on it: a world this client did
        /// not spawn itself is used only when the host sent it, so a player who is
        /// not the host cannot replace the room's world with one of its own. Without
        /// the bit the byte is only the spawner's claim, and every world is used as
        /// before.
        /// </para>
        /// </remarks>
        AttestedSpawnHost = 1u << 8,

        /// <summary>
        /// Bit 9 — this client reads a batch of variable updates the server
        /// relays as one packet. Negotiated: the SDK always advertises it, and it
        /// is active when the server advertised it too.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With the bit active, another player's batch of variable updates reaches
        /// this client as the one packet that player sent, and the SDK applies its
        /// entries in order, each as a single update would be applied. Without it
        /// the server sends each entry as its own packet. Either way the values are
        /// the same; the difference is how many packets one batch costs every
        /// receiver.
        /// </para>
        /// </remarks>
        VariableBatchRelay = 1u << 9,

        /// <summary>
        /// Bit 10 — the server reads a transform's teleport flag. A one-way server
        /// assertion; it adds no field to the handshake.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With the bit set, <see cref="RTMPE.Sync.NetworkTransform.OwnerTeleportTo"/>
        /// sends the destination at once, marked as a teleport, and the other players
        /// see the object arrive there instead of travelling there at their speed
        /// limit. The server admits one teleport per object per second; a teleport
        /// past that reaches them as an ordinary pose.
        /// </para>
        /// <para>
        /// Without the bit the destination is still sent at once, unmarked, and the
        /// other players move the object there at their speed limit.
        /// </para>
        /// </remarks>
        TransformTeleport = 1u << 10,

        /// <summary>
        /// Bit 11 — the server reads the transforms a client owes in one tick sent
        /// together, in one datagram. A one-way server assertion; it adds no field
        /// to the handshake.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With the bit set, the moving objects this client owns send the
        /// transforms they owe in a tick together, up to 21 full-precision or 38
        /// quantized to a datagram, rather than one datagram each — so the
        /// datagrams a client sends grow with the objects it moves by a twenty-first
        /// as fast.
        /// </para>
        /// <para>
        /// Without the bit each object sends its own datagram, as before.
        /// </para>
        /// </remarks>
        StateBatch = 1u << 11,
    }

    /// <summary>
    /// Writes, reads and negotiates <see cref="CapabilityFlags"/> values in their
    /// wire form: a 32-bit little-endian value.
    /// </summary>
    public static class CapabilityFlagsWire
    {
        /// <summary>
        /// The size of a capability set on the wire, in bytes: 4.
        /// </summary>
        public const int WireSize = 4;

        /// <summary>
        /// Writes <paramref name="caps"/> little-endian into the four bytes starting
        /// at <paramref name="offset"/> in <paramref name="destination"/>.
        /// </summary>
        /// <param name="destination">The buffer to write into.</param>
        /// <param name="offset">The index of the first byte to write.</param>
        /// <param name="caps">The capability set to write.</param>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Fewer than <see cref="WireSize"/> bytes of <paramref name="destination"/>
        /// start at <paramref name="offset"/>, or <paramref name="offset"/> is negative.
        /// </exception>
        public static void WriteLittleEndian(byte[] destination, int offset, CapabilityFlags caps)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (offset < 0 || offset > destination.Length - WireSize)
                throw new ArgumentOutOfRangeException(nameof(offset),
                    "Destination buffer cannot hold a 4-byte capability bitmask at the requested offset.");

            uint value = (uint)caps;
            destination[offset    ] = (byte) value;
            destination[offset + 1] = (byte)(value >>  8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// Reads a capability set from the four bytes starting at
        /// <paramref name="offset"/> in <paramref name="source"/>.
        /// </summary>
        /// <remarks>
        /// Use <see cref="TryReadLittleEndian"/> for a field that may be absent.
        /// </remarks>
        /// <param name="source">The bytes to read from.</param>
        /// <param name="offset">The index of the first byte to read.</param>
        /// <returns>The capability set.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Fewer than <see cref="WireSize"/> bytes of <paramref name="source"/> start
        /// at <paramref name="offset"/>, or <paramref name="offset"/> is negative.
        /// </exception>
        public static CapabilityFlags ReadLittleEndian(ReadOnlySpan<byte> source, int offset)
        {
            if (offset < 0 || offset > source.Length - WireSize)
                throw new ArgumentOutOfRangeException(nameof(offset),
                    "Source span does not hold 4 bytes at the requested offset.");

            uint value = (uint)source[offset]
                       | ((uint)source[offset + 1] <<  8)
                       | ((uint)source[offset + 2] << 16)
                       | ((uint)source[offset + 3] << 24);
            return (CapabilityFlags)value;
        }

        /// <summary>
        /// Reads a capability field that may be absent. A missing field is
        /// equivalent to a peer that advertises no optional feature.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <param name="offset">The index of the first byte to read.</param>
        /// <param name="caps">
        /// The capability set, or <see cref="CapabilityFlags.None"/> when the field is
        /// absent.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when <see cref="WireSize"/> bytes start at
        /// <paramref name="offset"/>; <see langword="false"/> when they do not.
        /// </returns>
        public static bool TryReadLittleEndian(
            ReadOnlySpan<byte> source, int offset, out CapabilityFlags caps)
        {
            if (offset < 0 || offset > source.Length - WireSize)
            {
                caps = CapabilityFlags.None;
                return false;
            }
            caps = ReadLittleEndian(source, offset);
            return true;
        }

        /// <summary>
        /// The features both sides advertised: the bitwise AND of the two sets. The
        /// result is taken at the handshake and holds for the whole session.
        /// </summary>
        /// <param name="local">What this side advertised.</param>
        /// <param name="peer">What the other side advertised.</param>
        /// <returns>The negotiated set.</returns>
        public static CapabilityFlags Negotiate(CapabilityFlags local, CapabilityFlags peer)
            => local & peer;

        /// <summary>
        /// The capability set the SDK advertises for a session.
        /// </summary>
        /// <remarks>
        /// <see cref="CapabilityFlags.EncryptedSessionAck"/> and
        /// <see cref="CapabilityFlags.VariableBatchRelay"/> are always included.
        /// </remarks>
        /// <param name="emitArqSequence">
        /// Whether reliable sends carry the <c>arq_seq</c> sub-header
        /// (<see cref="RTMPE.Core.NetworkSettings.EmitArqSequence"/>). When
        /// <see langword="true"/>, <see cref="CapabilityFlags.ArqAck"/> and
        /// <see cref="CapabilityFlags.ArqSeqAad"/> are included: acknowledgements
        /// and the binding both concern that sub-header.
        /// </param>
        /// <param name="canEchoInitHash">
        /// Whether the encrypted HandshakeInit payload is still held, so the
        /// HandshakeResponse can carry the <see cref="CapabilityFlags.InitHashEcho"/>
        /// echo. It is not held during a reconnect.
        /// </param>
        /// <returns>The set to advertise.</returns>
        public static CapabilityFlags Advertised(bool emitArqSequence, bool canEchoInitHash)
        {
            var caps = CapabilityFlags.EncryptedSessionAck | CapabilityFlags.VariableBatchRelay;
            if (emitArqSequence) caps |= CapabilityFlags.ArqAck | CapabilityFlags.ArqSeqAad;
            if (canEchoInitHash) caps |= CapabilityFlags.InitHashEcho;
            return caps;
        }
    }
}
