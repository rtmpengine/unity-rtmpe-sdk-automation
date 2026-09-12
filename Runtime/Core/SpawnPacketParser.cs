// RTMPE SDK — Runtime/Core/SpawnPacketParser.cs
//
// Parses incoming Spawn/Despawn packets from the server.
// The standard 13-byte packet header has already been stripped —
// this parser operates on the payload portion only.
//
// Wire formats match SpawnPacketBuilder.cs.

using System;
using System.Text;
using UnityEngine;
// `IsFinite` below is WireVector's rather than a private copy — the same
// function SpawnPacketBuilder asks before it writes, so this file's refusal and
// that file's refusal cannot come apart under an edit to one.
using static RTMPE.Sync.WireVector;

namespace RTMPE.Core
{
    /// <summary>
    /// Parsed spawn data from a server Spawn packet.
    /// </summary>
    public readonly struct SpawnData
    {
        public readonly uint PrefabId;
        public readonly ulong ObjectId;
        public readonly string OwnerPlayerId;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        /// <summary>
        /// The raw authority declaration the spawner emitted, or
        /// <see cref="SpawnAuthorityFlags.OwnerOnly"/> when the payload carried
        /// none (audit <c>S4-02</c>).
        /// </summary>
        public readonly byte Authority;

        /// <summary>
        /// True when any member of the room may address this object with an
        /// Enhanced RPC.
        ///
        /// <para>Every value other than the exact shared declaration reads as
        /// owner-only, which is the gateway's rule too: the gateway is the one
        /// that enforces this, and a client-side view that disagreed with it
        /// would be a second answer to the same question.</para>
        /// </summary>
        public bool IsSharedAuthority => Authority == SpawnAuthorityFlags.Shared;

        /// <summary>
        /// The spawner's declaration that every client destroys this object when
        /// its owner leaves the room, or <see langword="false"/> when the payload
        /// carried none.
        /// </summary>
        /// <remarks>
        /// Read here for the same reason the authority byte is: the gateway
        /// relays the payload verbatim, so every receiver sees whatever the
        /// spawner emitted and a parser that refused the field would drop the
        /// spawn outright.  The receiving client acts on its own component
        /// rather than on this value — the declaration exists for the gateway,
        /// which has no component to read.
        /// </remarks>
        public readonly bool DestroyWithOwner;

        public SpawnData(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            byte authority = SpawnAuthorityFlags.OwnerOnly,
            bool destroyWithOwner = false)
        {
            DestroyWithOwner = destroyWithOwner;
            PrefabId      = prefabId;
            ObjectId      = objectId;
            OwnerPlayerId = ownerPlayerId;
            Position      = position;
            Rotation      = rotation;
            Authority     = authority;
        }
    }

    /// <summary>
    /// Parses Spawn/Despawn payload bytes into structured data.
    /// All methods return false on malformed input (no exceptions).
    /// </summary>
    public static class SpawnPacketParser
    {
        /// <summary>
        /// Maximum accepted owner-id length, in UTF-8 bytes.  Generous for any
        /// UUID (max 36 chars) plus margin; a value above this is a protocol
        /// violation, not merely a large owner.  Enforced symmetrically on the
        /// build path (<see cref="SpawnPacketBuilder.BuildSpawnRequest"/>) so a
        /// locally-built Spawn always round-trips through this parser.
        /// </summary>
        public const int MaxOwnerIdBytes = 128;

        /// <summary>
        /// Parse a Spawn payload (received from server).
        /// </summary>
        /// <param name="payload">The payload bytes (after the 13-byte header).</param>
        /// <param name="data">The parsed spawn data if successful.</param>
        /// <returns>True if parsing succeeded.</returns>
        public static bool TryParseSpawn(byte[] payload, out SpawnData data)
        {
            data = default;

            // Minimum: 4 + 8 + 2 + 0 + 28 = 42 bytes (empty owner)
            if (payload == null || payload.Length < 42)
                return false;

            int o = 0;
            uint prefabId = ReadU32LE(payload, ref o);
            ulong objectId = ReadU64LE(payload, ref o);
            // Zero is never a valid network object ID.
            if (objectId == 0) return false;
            ushort ownerLen = ReadU16LE(payload, ref o);

            // Cap owner length before the bounds arithmetic so a ushort.MaxValue-
            // shaped attacker value cannot overflow `o + ownerLen + 28` into a
            // negative int that bypasses the additive-form check.  The ceiling
            // (MaxOwnerIdBytes) is shared with the build path so the two stay
            // in lockstep — a value above it is a protocol violation, not just
            // a large owner.
            if (ownerLen > MaxOwnerIdBytes)
                return false;

            // Subtraction-form bounds: the owner string plus the seven
            // trailing floats (28 B) must fit inside the remaining
            // payload.  Computed as `available - constant >= ownerLen`
            // so neither side of the comparison can wrap.
            if (ownerLen > payload.Length - o - 28)
                return false;

            // Strict UTF-8 — the default Encoding.UTF8 silently substitutes the
            // U+FFFD replacement character for any malformed byte sequence, which
            // turns an attacker-corruptible owner-id into a string that compares
            // unequal to a legitimate UUID but still satisfies non-empty checks.
            // DecodeStrictUtf8 throws on either malformed UTF-8 or an embedded
            // NUL; we treat that the same as any other malformed payload.
            string owner;
            if (ownerLen > 0)
            {
                try
                {
                    owner = DecodeStrictUtf8(payload, o, ownerLen);
                }
                catch (Exception)
                {
                    return false;
                }
            }
            else
            {
                owner = string.Empty;
            }
            o += ownerLen;

            float px = ReadF32LE(payload, ref o);
            float py = ReadF32LE(payload, ref o);
            float pz = ReadF32LE(payload, ref o);
            float rx = ReadF32LE(payload, ref o);
            float ry = ReadF32LE(payload, ref o);
            float rz = ReadF32LE(payload, ref o);
            float rw = ReadF32LE(payload, ref o);

            // Finiteness gate.  GameObject.Instantiate(...) with a NaN /
            // Inf transform corrupts PhysX state on the very first
            // FixedUpdate (the rigidbody is reported as missing from the
            // simulation; ragdoll children become detached).  Reject the
            // spawn payload outright rather than persisting the corruption.
            if (!IsFinite(px) || !IsFinite(py) || !IsFinite(pz)) return false;
            if (!IsFinite(rx) || !IsFinite(ry) || !IsFinite(rz) || !IsFinite(rw)) return false;

            // Quaternion magnitude gate.  The IsFinite check above admits
            // finite-but-degenerate rotations: a (0,0,0,0) zero-quaternion
            // assigned to transform.rotation produces per-frame "Look
            // rotation viewing vector is zero" warnings (each ~1 KB on the
            // managed heap) and silently substitutes identity, while
            // grossly-non-unit quaternions (e.g. 1e10 in each component)
            // produce NaN once squared during downstream parent-transform
            // multiplication.  The transform / physics parsers reject any
            // quaternion whose squared magnitude lies outside [0.9, 1.1]
            // (≈ unit norm with 5 % tolerance for FP rounding); mirror
            // the same band here so the spawn-time pose carries the
            // identical invariant as every other inbound rotation.
            float qMagSq = rx * rx + ry * ry + rz * rz + rw * rw;
            if (qMagSq < RTMPE.Sync.WireQuaternion.MinMagSq
                || qMagSq > RTMPE.Sync.WireQuaternion.MaxMagSq) return false;

            // One optional trailing byte is the S4-02 authority declaration
            // (SpawnAuthorityFlags).  It is read rather than rejected because
            // the gateway relays the spawn payload to peers verbatim, so every
            // receiver sees whatever the spawner emitted.  Absent still means
            // owner-only — the same resolution the gateway applies — so a
            // pre-S4-02 spawner and this parser agree without negotiating.
            byte authority = SpawnAuthorityFlags.OwnerOnly;
            if (o < payload.Length)
                authority = payload[o++];

            // A second optional byte, read on the same terms and positional
            // behind the first: a sender that emits the lifetime declaration
            // emits the authority one as well, so a payload one byte past the
            // floats is the older shape rather than an ambiguous one.  Absent
            // means the object survives its owner's departure, which is what
            // every sender that predates the field meant.
            bool destroyWithOwner = false;
            if (o < payload.Length)
                destroyWithOwner = payload[o++] == SpawnLifetimeFlags.DestroyWithOwner;

            // Reject trailing residue.  A well-formed spawn payload ends after
            // the 7th float, or after one or both of the declaration bytes
            // following it; anything beyond that is a protocol-drift /
            // smuggling signal.
            if (o != payload.Length) return false;

            data = new SpawnData(
                prefabId,
                objectId,
                owner,
                new Vector3(px, py, pz),
                new Quaternion(rx, ry, rz, rw),
                authority,
                destroyWithOwner);

            return true;
        }

        /// <summary>Why the room refused a <c>Spawn</c>.</summary>
        /// <remarks>
        /// Only the two a client cannot work out for itself travel on the wire.
        /// The other three refusals — an owner that is not this client's, no
        /// owner declared while in a room, and a payload carrying no readable
        /// owner claim — are decidable here, and the SDK decides them.
        /// </remarks>
        public enum SpawnRejectReason
        {
            /// <summary>
            /// A reason this build does not know, from a gateway newer than it.
            /// Reported rather than dropped: the spawn was refused whatever the
            /// reason, and that is the half the caller has to act on.
            /// </summary>
            Unknown = 0,

            /// <summary>
            /// The object id already belongs to a different player. The local
            /// object exists and no other client will ever hear of it.
            /// </summary>
            OwnerCollision = 1,

            /// <summary>The room is at its object ceiling.</summary>
            RoomAtObjectCeiling = 2,

            /// <summary>
            /// The object id was minted in another session's id space, so this
            /// client could not have produced it. Every id this SDK allocates
            /// folds the session's own digest into its high half, so an honest
            /// build never sees this: it names a client that composed an id by
            /// some other route, or a session that ended and was replaced.
            /// </summary>
            ForeignIdSpace = 3,
        }

        /// <summary>
        /// Parse a <c>SpawnRejected</c> (0x32) payload:
        /// <c>[object_id:8 LE][reason:1]</c>.
        /// </summary>
        /// <remarks>
        /// Exactly nine bytes. A trailing residue is refused for the same reason
        /// <see cref="TryParseDespawn"/> refuses one — it is protocol drift or
        /// smuggling, and this payload has no growable tail.
        /// </remarks>
        public static bool TryParseSpawnRejected(
            byte[] payload, out ulong objectId, out SpawnRejectReason reason)
        {
            objectId = 0;
            reason = SpawnRejectReason.Unknown;
            if (payload == null || payload.Length != 9) return false;

            int o = 0;
            objectId = ReadU64LE(payload, ref o);
            // Zero is never a valid network object ID, so a rejection naming it
            // correlates to no request this client made.
            if (objectId == 0) return false;

            switch (payload[8])
            {
                case 1: reason = SpawnRejectReason.OwnerCollision; break;
                case 2: reason = SpawnRejectReason.RoomAtObjectCeiling; break;
                case 3: reason = SpawnRejectReason.ForeignIdSpace; break;
                default: reason = SpawnRejectReason.Unknown; break;
            }
            return true;
        }

        /// <summary>
        /// Parse a Despawn payload (received from server).
        /// </summary>
        /// <param name="payload">The payload bytes (after the 13-byte header).</param>
        /// <param name="objectId">The object ID to despawn.</param>
        /// <returns>True if parsing succeeded.</returns>
        public static bool TryParseDespawn(byte[] payload, out ulong objectId)
        {
            objectId = 0;
            // Exactly 8 bytes: the u64 object ID and nothing else.
            // Trailing bytes are a protocol-drift / smuggling signal (same
            // principle as TryParseSpawn's trailing-residue check).
            if (payload == null || payload.Length != 8)
                return false;

            int o = 0;
            objectId = ReadU64LE(payload, ref o);
            // Zero is never a valid network object ID.
            return objectId != 0;
        }

        // ── LE readers ─────────────────────────────────────────────────────────

        private static ushort ReadU16LE(byte[] buf, ref int offset)
        {
            ushort v = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            offset += 2;
            return v;
        }

        private static uint ReadU32LE(byte[] buf, ref int offset)
        {
            uint v = (uint)(
                buf[offset]
              | (buf[offset + 1] << 8)
              | (buf[offset + 2] << 16)
              | (buf[offset + 3] << 24));
            offset += 4;
            return v;
        }

        private static ulong ReadU64LE(byte[] buf, ref int offset)
        {
            ulong v =
                  (ulong)buf[offset]
                | ((ulong)buf[offset + 1] << 8)
                | ((ulong)buf[offset + 2] << 16)
                | ((ulong)buf[offset + 3] << 24)
                | ((ulong)buf[offset + 4] << 32)
                | ((ulong)buf[offset + 5] << 40)
                | ((ulong)buf[offset + 6] << 48)
                | ((ulong)buf[offset + 7] << 56);
            offset += 8;
            return v;
        }

        private static float ReadF32LE(byte[] buf, ref int offset)
        {
            // Explicit byte assembly + Int32BitsToSingle for endian-safe LE decoding.
            // BitConverter.ToSingle(buf, offset) is platform-endian and would misread
            // bytes on big-endian platforms. This matches TransformPacketParser.ReadF32LE.
            int bits = buf[offset]
                     | (buf[offset + 1] <<  8)
                     | (buf[offset + 2] << 16)
                     | (buf[offset + 3] << 24);
            offset += 4;
            return BitConverter.Int32BitsToSingle(bits);
        }

        // Strict UTF-8 codec — the default Encoding.UTF8 silently substitutes
        // U+FFFD for a malformed byte sequence, which would turn an
        // attacker-corruptible owner identifier into a string that compares
        // unequal to a legitimate UUID yet still passes a non-empty check.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Decode a slice of <paramref name="bytes"/> as UTF-8 with strict
        // validation.  Throws DecoderFallbackException for any malformed byte
        // sequence and InvalidOperationException for an embedded NUL — the
        // caller treats either as a malformed payload.
        private static string DecodeStrictUtf8(byte[] bytes, int offset, int length)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || length < 0 || offset > bytes.Length - length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            for (int i = 0; i < length; i++)
            {
                if (bytes[offset + i] == 0)
                    throw new InvalidOperationException("string contains embedded NUL");
            }
            return StrictUtf8.GetString(bytes, offset, length);
        }
    }
}
