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
    /// A spawn, as read by <see cref="SpawnPacketParser.TryParseSpawn"/>.
    /// </summary>
    public readonly struct SpawnData
    {
        /// <summary>The id of the prefab to instantiate.</summary>
        public readonly uint PrefabId;

        /// <summary>The object's network object id.</summary>
        public readonly ulong ObjectId;

        /// <summary>The owner's player id; empty when the spawn named none.</summary>
        public readonly string OwnerPlayerId;

        /// <summary>The world-space position.</summary>
        public readonly Vector3 Position;

        /// <summary>The world-space rotation.</summary>
        public readonly Quaternion Rotation;

        /// <summary>
        /// The authority declaration the spawner sent (a
        /// <see cref="SpawnAuthorityFlags"/> value), or
        /// <see cref="SpawnAuthorityFlags.OwnerOnly"/> when the spawn carried none.
        /// </summary>
        public readonly byte Authority;

        /// <summary>
        /// Whether any member of the room may call RPCs on the object:
        /// <see langword="true"/> only when <see cref="Authority"/> is exactly
        /// <see cref="SpawnAuthorityFlags.Shared"/>.
        /// </summary>
        public bool IsSharedAuthority => Authority == SpawnAuthorityFlags.Shared;

        /// <summary>
        /// Whether the spawner declared that every client destroys the object when
        /// its owner leaves the room; <see langword="false"/> when the spawn carried
        /// no declaration.
        /// </summary>
        /// <remarks>
        /// A receiving client applies this value to
        /// <see cref="NetworkBehaviour.DestroyWithOwner"/> on every component of the
        /// object it creates.
        /// </remarks>
        public readonly bool DestroyWithOwner;

        /// <summary>
        /// Whether the spawn's lifetime byte was
        /// <see cref="SpawnLifetimeFlags.SpawnedByHost"/>: the object survives its
        /// owner and the gateway saw the room's host send it.
        /// </summary>
        /// <remarks>
        /// A fact only when the gateway states it writes the byte
        /// (<see cref="RTMPE.Core.Protocol.CapabilityFlags.AttestedSpawnHost"/>);
        /// otherwise it is whatever the spawner wrote.
        /// </remarks>
        public readonly bool SpawnedByHost;

        /// <summary>Creates a spawn description.</summary>
        /// <param name="prefabId">The id of the prefab to instantiate.</param>
        /// <param name="objectId">The object's network object id.</param>
        /// <param name="ownerPlayerId">The owner's player id.</param>
        /// <param name="position">The world-space position.</param>
        /// <param name="rotation">The world-space rotation.</param>
        /// <param name="authority">The authority declaration, a <see cref="SpawnAuthorityFlags"/> value.</param>
        /// <param name="destroyWithOwner">Whether every client destroys the object when its owner leaves.</param>
        public SpawnData(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            byte authority = SpawnAuthorityFlags.OwnerOnly,
            bool destroyWithOwner = false)
            : this(prefabId, objectId, ownerPlayerId, position, rotation, authority, destroyWithOwner,
                   spawnedByHost: false)
        {
        }

        /// <summary>Creates a spawn description that says whether the host sent it.</summary>
        /// <param name="prefabId">The id of the prefab to instantiate.</param>
        /// <param name="objectId">The object's network object id.</param>
        /// <param name="ownerPlayerId">The owner's player id.</param>
        /// <param name="position">The world-space position.</param>
        /// <param name="rotation">The world-space rotation.</param>
        /// <param name="authority">The authority declaration, a <see cref="SpawnAuthorityFlags"/> value.</param>
        /// <param name="destroyWithOwner">Whether every client destroys the object when its owner leaves.</param>
        /// <param name="spawnedByHost">Whether the lifetime byte said the room's host sent the spawn.</param>
        public SpawnData(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            byte authority,
            bool destroyWithOwner,
            bool spawnedByHost)
        {
            DestroyWithOwner = destroyWithOwner;
            SpawnedByHost = spawnedByHost;
            PrefabId      = prefabId;
            ObjectId      = objectId;
            OwnerPlayerId = ownerPlayerId;
            Position      = position;
            Rotation      = rotation;
            Authority     = authority;
        }
    }

    /// <summary>
    /// Reads the payloads of spawn, despawn and spawn-refusal packets. Every method
    /// returns <see langword="false"/> for a malformed payload and never throws.
    /// </summary>
    /// <remarks>
    /// The SDK uses these on the packets it receives; game code does not need to
    /// call them.
    /// </remarks>
    public static class SpawnPacketParser
    {
        /// <summary>
        /// The longest owner id a spawn may carry, in UTF-8 bytes: 128.
        /// <see cref="SpawnPacketBuilder.BuildSpawnRequest"/> refuses a longer one,
        /// so every spawn this SDK builds can be read back.
        /// </summary>
        public const int MaxOwnerIdBytes = 128;

        /// <summary>
        /// Reads a spawn payload.
        /// </summary>
        /// <remarks>
        /// Refused: a payload too short to hold a spawn, an object id of 0, an owner
        /// id longer than <see cref="MaxOwnerIdBytes"/>, not valid UTF-8 or containing
        /// a NUL character, a position or rotation that is not finite, a rotation far
        /// from unit length, and trailing bytes.
        /// </remarks>
        /// <param name="payload">The packet payload, without the packet header.</param>
        /// <param name="data">The spawn, when the payload is valid.</param>
        /// <returns><see langword="true"/> when the payload is a valid spawn.</returns>
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
            //
            // The same byte carries one more value, written by the gateway rather
            // than the spawner: SpawnedByHost, an object that survives its owner
            // and that the room's host sent.  Compared exactly, like the other,
            // so every value but DestroyWithOwner still means survival.
            byte lifetime = SpawnLifetimeFlags.SurvivesOwner;
            if (o < payload.Length)
                lifetime = payload[o++];
            bool destroyWithOwner = lifetime == SpawnLifetimeFlags.DestroyWithOwner;
            bool spawnedByHost    = lifetime == SpawnLifetimeFlags.SpawnedByHost;

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
                destroyWithOwner,
                spawnedByHost);

            return true;
        }

        /// <summary>
        /// Why the room refused a spawn; reported by
        /// <see cref="SpawnManager.OnSpawnRejected"/>.
        /// </summary>
        /// <remarks>
        /// Refusals the client can detect itself, such as an owner that is not this
        /// client, are reported by <see cref="SpawnManager.Spawn"/> instead.
        /// </remarks>
        public enum SpawnRejectReason
        {
            /// <summary>
            /// A reason this SDK version does not know. The spawn was refused all the
            /// same.
            /// </summary>
            Unknown = 0,

            /// <summary>
            /// The object id already belongs to another player.
            /// </summary>
            OwnerCollision = 1,

            /// <summary>The room holds as many objects as it allows.</summary>
            RoomAtObjectCeiling = 2,

            /// <summary>
            /// The object id belongs to another session's id range. An id the SDK
            /// allocated is refused this way only when it was allocated in an earlier
            /// session than the one this client is connected on.
            /// </summary>
            ForeignIdSpace = 3,

            /// <summary>
            /// This player has created as many of the room's objects as the server
            /// allows one player. The room's host is not held to this limit, and
            /// objects a player created as the host do not count against it.
            /// </summary>
            PlayerAtObjectCeiling = 4,
        }

        /// <summary>
        /// Reads the payload of a spawn refusal: the refused object id and the
        /// reason.
        /// </summary>
        /// <remarks>
        /// A payload of the wrong length or with an object id of 0 is refused; an
        /// unrecognised reason code reads as <see cref="SpawnRejectReason.Unknown"/>.
        /// </remarks>
        /// <param name="payload">The packet payload, without the packet header.</param>
        /// <param name="objectId">The refused object id.</param>
        /// <param name="reason">The reason for the refusal.</param>
        /// <returns><see langword="true"/> when the payload is a valid spawn refusal.</returns>
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
                case 4: reason = SpawnRejectReason.PlayerAtObjectCeiling; break;
                default: reason = SpawnRejectReason.Unknown; break;
            }
            return true;
        }

        /// <summary>
        /// Reads a despawn payload, which holds the object id and nothing else. A
        /// payload of the wrong length or with an object id of 0 is refused.
        /// </summary>
        /// <param name="payload">The packet payload, without the packet header.</param>
        /// <param name="objectId">The object id to despawn.</param>
        /// <returns><see langword="true"/> when the payload is a valid despawn.</returns>
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
