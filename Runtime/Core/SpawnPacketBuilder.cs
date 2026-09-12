// RTMPE SDK — Runtime/Core/SpawnPacketBuilder.cs
//
// Builds payload bytes for Spawn/Despawn request packets.
// The caller wraps the returned payload with PacketBuilder.Build() to produce
// the full wire packet (13-byte standard header + spawn payload).
//
// Wire formats (all little-endian):
//
// ── SpawnRequest (client → server, type 0x30) ──────────────────────────────
//  [prefab_id   : 4 LE u32]
//  [object_id   : 8 LE u64]  — client-generated from GenerateObjectId()
//  [owner_len   : 2 LE u16]
//  [owner       : N UTF-8]   — room player UUID
//  [pos_x       : 4 LE f32]
//  [pos_y       : 4 LE f32]
//  [pos_z       : 4 LE f32]
//  [rot_x       : 4 LE f32]
//  [rot_y       : 4 LE f32]
//  [rot_z       : 4 LE f32]
//  [rot_w       : 4 LE f32]
//  [authority   : 1]         — optional; see SpawnAuthorityFlags (audit S4-02)
//  [lifetime    : 1]         — optional; see SpawnLifetimeFlags
//
// Total: 42 + owner_len bytes, 43 + owner_len with the authority declaration,
// 44 + owner_len with both.  The tail ahead of them is fixed-length, so the
// trailing bytes are unambiguous and positional: a reader seeing 42 + owner_len
// finds no declaration at all and resolves the object owner-only and surviving
// its owner, which is what every sender that predates each field meant.
//
// ── DespawnRequest (client → server, type 0x31) ────────────────────────────
//  [object_id   : 8 LE u64]
//
// Total: 8 bytes.

using System;
using System.Text;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Builds payload byte arrays for Spawn/Despawn protocol packets.
    /// All methods are static and produce a fresh byte[] on each call.
    /// </summary>
    public static class SpawnPacketBuilder
    {
        // Static and one-per-second: the mistake repeats for every object a
        // misconfigured spawner creates, and the message is the same each time.
        private static long _lastRotationSubstitutionWarnTicks;

        // Its own gate, for the same reason the transform and physics builders
        // keep two: a substituted rotation still spawns the object, a non-finite
        // position does not, and one gate would let either silence the other.
        private static long _lastNonFinitePositionWarnTicks;

        /// <summary>
        /// Build the payload for a Spawn request (PacketType.Spawn = 0x30).
        /// </summary>
        /// <param name="prefabId">Registered prefab identifier.</param>
        /// <param name="objectId">Client-generated unique object ID.</param>
        /// <param name="ownerPlayerId">Room player UUID of the owner.</param>
        /// <param name="position">World-space spawn position.</param>
        /// <param name="rotation">World-space spawn rotation.</param>
        /// <param name="authority">
        /// Who may address this object with an Enhanced RPC — a value from
        /// <see cref="SpawnAuthorityFlags"/>.  Defaults to
        /// <see cref="SpawnAuthorityFlags.OwnerOnly"/>, which is also what the
        /// gateway resolves for a payload that carries no declaration, so the
        /// byte is always emitted and the two agree by construction rather than
        /// by the caller remembering to pass it.
        /// </param>
        /// <returns>
        /// Spawn payload ready for <c>PacketBuilder.Build()</c>, or
        /// <see langword="null"/> when <paramref name="position"/> is
        /// non-finite — a spawn the receiving parser refuses is not built.
        /// </returns>
        public static byte[] BuildSpawnRequest(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            byte authority = SpawnAuthorityFlags.OwnerOnly,
            bool destroyWithOwner = false)
        {
            byte[] ownerBytes = string.IsNullOrEmpty(ownerPlayerId)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(ownerPlayerId);

            if (ownerBytes.Length > SpawnPacketParser.MaxOwnerIdBytes)
                throw new ArgumentException(
                    $"ownerPlayerId UTF-8 encoding exceeds {SpawnPacketParser.MaxOwnerIdBytes} bytes — " +
                    "the limit the receiving SpawnPacketParser accepts.",
                    nameof(ownerPlayerId));

            // Refused rather than repaired, and refused rather than thrown —
            // and asked AFTER the owner-id length, so a call that is wrong
            // both ways still gets the throw. Returning null there would
            // downgrade a truncated identity into a silent no-spawn.
            //
            // `SpawnPacketParser` drops a spawn whose position is non-finite, so
            // building one leaves the spawner the only client that can see the
            // object — the exact failure the rotation sanitiser below exists to
            // prevent, one field over.  There is no nearest valid position to
            // substitute: the origin and the caller's last known point are both
            // somewhere the object is not, and spawning it there for every other
            // player is worse than not spawning it.
            //
            // ⚠️ Three answers to bad input in one method, deliberately.  An
            // over-long owner id THROWS: it is a caller-supplied string, checked
            // before anything exists, and a throw is the only answer that stops
            // a truncated identity from reaching the wire.  A non-unit rotation
            // is SUBSTITUTED: it has a nearest valid value.  A non-finite
            // position returns NULL: it has none, and by the time this runs the
            // object is already created locally, so throwing would leave the
            // caller's own Update half-way through a spawn it cannot undo.
            if (!RTMPE.Sync.WireVector.IsFinite(position))
            {
                if (WarnGate.ShouldEmit(ref _lastNonFinitePositionWarnTicks))
                {
                    Debug.LogWarning(
                        $"[RTMPE] SpawnPacketBuilder: object {objectId} was given a non-finite " +
                        "spawn position (NaN or Infinity), so no spawn was sent for it. The " +
                        "object exists on this client only. There is no nearest valid position " +
                        "to substitute, and the packet would have been refused by every peer.");
                }
                return null;
            }

            // 4 + 8 + 2 + N + 7*4 + 1 + 1 = 44 + N
            int size = 4 + 8 + 2 + ownerBytes.Length + 28 + 1 + 1;
            var buf = new byte[size];
            int o = 0;

            WriteU32LE(buf, ref o, prefabId);
            WriteU64LE(buf, ref o, objectId);
            WriteU16LE(buf, ref o, (ushort)ownerBytes.Length);
            if (ownerBytes.Length > 0)
            {
                Buffer.BlockCopy(ownerBytes, 0, buf, o, ownerBytes.Length);
                o += ownerBytes.Length;
            }
            WriteF32LE(buf, ref o, position.x);
            WriteF32LE(buf, ref o, position.y);
            WriteF32LE(buf, ref o, position.z);
            // The owner id above is validated against the limit the receiving
            // parser accepts; the rotation was not, and the parser's quaternion
            // band is exactly as load-bearing.  A `default(Quaternion)` — which
            // is (0,0,0,0), not identity — built, relayed through the gateway
            // verbatim, and was refused by every peer, so the spawner stayed the
            // only client that could see the object.
            //
            // Sanitised rather than refused: this call is on the path a
            // component's spawn takes, and a throw there surfaces as an
            // exception out of the caller's own Update with the object half
            // created.  The nearest valid rotation is delivered instead, and
            // said once a second.
            var wireRotation = RTMPE.Sync.WireQuaternion.ForWire(rotation, out bool rotationSubstituted);
            if (rotationSubstituted && WarnGate.ShouldEmit(ref _lastRotationSubstitutionWarnTicks))
            {
                Debug.LogWarning(
                    $"[RTMPE] SpawnPacketBuilder: object {objectId} was given a spawn rotation the " +
                    "protocol does not carry (a zero, non-finite or grossly non-unit quaternion). " +
                    "Spawning with the nearest valid rotation — the original would have been " +
                    "refused by every peer, leaving this client the only one that could see the " +
                    "object. `default(Quaternion)` is (0,0,0,0), not identity.");
            }
            WriteF32LE(buf, ref o, wireRotation.x);
            WriteF32LE(buf, ref o, wireRotation.y);
            WriteF32LE(buf, ref o, wireRotation.z);
            WriteF32LE(buf, ref o, wireRotation.w);
            buf[o++] = authority;
            buf[o++] = destroyWithOwner
                ? SpawnLifetimeFlags.DestroyWithOwner
                : SpawnLifetimeFlags.SurvivesOwner;

            return buf;
        }

        /// <summary>
        /// Build the payload for a Despawn request (PacketType.Despawn = 0x31).
        /// </summary>
        /// <param name="objectId">The network object ID to despawn.</param>
        /// <returns>Despawn payload ready for <c>PacketBuilder.Build()</c>.</returns>
        public static byte[] BuildDespawnRequest(ulong objectId)
        {
            var buf = new byte[8];
            int o = 0;
            WriteU64LE(buf, ref o, objectId);
            return buf;
        }

        // ── LE writers ─────────────────────────────────────────────────────────

        private static void WriteU16LE(byte[] buf, ref int offset, ushort value)
        {
            buf[offset++] = (byte)(value);
            buf[offset++] = (byte)(value >> 8);
        }

        private static void WriteU32LE(byte[] buf, ref int offset, uint value)
        {
            buf[offset++] = (byte)(value);
            buf[offset++] = (byte)(value >> 8);
            buf[offset++] = (byte)(value >> 16);
            buf[offset++] = (byte)(value >> 24);
        }

        private static void WriteU64LE(byte[] buf, ref int offset, ulong value)
        {
            buf[offset++] = (byte)(value);
            buf[offset++] = (byte)(value >> 8);
            buf[offset++] = (byte)(value >> 16);
            buf[offset++] = (byte)(value >> 24);
            buf[offset++] = (byte)(value >> 32);
            buf[offset++] = (byte)(value >> 40);
            buf[offset++] = (byte)(value >> 48);
            buf[offset++] = (byte)(value >> 56);
        }

        private static void WriteF32LE(byte[] buf, ref int offset, float value)
        {
            // Use SingleToInt32Bits + explicit byte extraction for endian-safe LE encoding.
            // BitConverter.GetBytes(float) is platform-endian and would produce wrong byte
            // order on big-endian platforms. This matches TransformPacketBuilder.WriteF32LE.
            int bits = BitConverter.SingleToInt32Bits(value);
            buf[offset++] = (byte) bits;
            buf[offset++] = (byte)(bits >>  8);
            buf[offset++] = (byte)(bits >> 16);
            buf[offset++] = (byte)(bits >> 24);
        }
    }
}
