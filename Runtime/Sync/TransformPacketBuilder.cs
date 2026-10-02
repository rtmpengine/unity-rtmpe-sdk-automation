// RTMPE SDK — Runtime/Sync/TransformPacketBuilder.cs
//
// Builds the binary payload for a client-to-server transform update packet.
//
// Wire format — full precision (48 bytes, all fields little-endian, the
// historical "v0" layout that omits any control byte for back-compat):
//  [0..7]   object_id : u64  — Unity NetworkObject.NetworkObjectId
//  [8..11]  pos_x     : f32  — world-space position X
//  [12..15] pos_y     : f32  — world-space position Y
//  [16..19] pos_z     : f32  — world-space position Z
//  [20..23] rot_x     : f32  — quaternion X  (world-space rotation)
//  [24..27] rot_y     : f32  — quaternion Y
//  [28..31] rot_z     : f32  — quaternion Z
//  [32..35] rot_w     : f32  — quaternion W
//  [36..39] scale_x   : f32  — local-space scale X
//  [40..43] scale_y   : f32  — local-space scale Y
//  [44..47] scale_z   : f32  — local-space scale Z
//
// Wire format — quantized (25 bytes, gated on FLAG_QUANTIZED, only emitted
// when NetworkSettings.quantizeTransforms is true and the gateway has
// negotiated support for the encoding):
//  [0]      flags     : u8   — bit 0x01 set indicates the quantized layout
//  [1..8]   object_id : u64
//  [9..14]  position  : 3 × half-float (binary16) LE  (6 bytes)
//  [15..18] rotation  : smallest-three packed u32 LE  (4 bytes)
//  [19..24] scale     : 3 × half-float (binary16) LE  (6 bytes)
//
// Both layouts may carry the owner's input tick (+4 bytes: 52 / 29), and
// behind it a flags byte (+1: 53 / 30) whose one defined value, 0x01, marks the
// pose a teleport (audit P6-E2).  The flags byte is sent only to a gateway that
// asserts CapabilityFlags.TransformTeleport; one that does not refuses those
// two lengths.
//
// Detection: the legacy 48-byte payload has a fixed total length of 48,
// while the quantized variant is 25 bytes.  The receiver dispatches on
// length first; on a length-25 payload it then verifies the leading flags
// byte has bit 0x01 set before treating the remainder as quantized
// fields.  Any unrecognised length / flag combination is rejected.
//
// The payload is wrapped in a 13-byte RTMPE header (PacketType.StateSync,
// 0x40) by the caller via NetworkManager.SendStateSync().
//
// The layout matches the Go server's ObjectState struct field order so that
// future server-side deserialisers can read the raw bytes directly.
//
//  Go reference: modules/synchronization/domain/entities/object_state.go
//    type ObjectState struct {
//        ObjectID uint64
//        Position Vec3         // float32 × 3
//        Rotation Quaternion   // float32 × 4 (X Y Z W)
//        Scale    Vec3         // float32 × 3
//    }
//
// Security note: no AEAD here. The surrounding gateway pipeline applies
// ChaCha20-Poly1305 encryption before the packet leaves the device.

using System;
using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Builds the transform payloads <see cref="NetworkTransform"/> sends. Used by
    /// the SDK; not intended to be called from game code.
    /// </summary>
    public static class TransformPacketBuilder
    {
        // One line a second across every object, not per object per tick: the
        // condition is a caller-side construction mistake that repeats at the
        // send rate for as long as the object exists, and the message says the
        // same thing every time.  Static for the same reason the unknown-
        // variable-id gate is — a many-object scene must not multiply it.
        private static long _lastRotationSubstitutionWarnTicks;

        // A second gate, not a shared one.  The two conditions are different
        // defects with different repairs — a rotation is substituted and the
        // send proceeds, a coordinate is not and the record is dropped — and a
        // single gate lets whichever fires first suppress the other for the rest
        // of the second, so an object diverging into NaN could report a rotation
        // warning and nothing about the update that never went out.
        private static long _lastNonFiniteCoordinateWarnTicks;

        // ── Layout constants ──────────────────────────────────────────────────

        /// <summary>The size, in bytes, of a full-precision transform payload.</summary>
        public const int PAYLOAD_SIZE = 48;

        /// <summary>
        /// The size, in bytes, of a full-precision transform payload that also
        /// carries the owner's input tick.
        /// </summary>
        public const int PAYLOAD_SIZE_WITH_TICK = PAYLOAD_SIZE + INPUT_TICK_SIZE;

        /// <summary>Width of the trailing <c>input_tick</c> field (u32 LE).</summary>
        internal const int INPUT_TICK_SIZE = 4;

        /// <summary>Byte offset of <c>object_id</c> within the payload.</summary>
        internal const int OFFSET_OBJECT_ID = 0;

        /// <summary>Byte offset of <c>pos_x</c> within the payload.</summary>
        internal const int OFFSET_POSITION = 8;

        /// <summary>Byte offset of <c>rot_x</c> within the payload.</summary>
        internal const int OFFSET_ROTATION = 20;

        /// <summary>Byte offset of <c>scale_x</c> within the payload.</summary>
        internal const int OFFSET_SCALE = 36;

        /// <summary>The size, in bytes, of a quantized transform payload.</summary>
        public const int QUANTIZED_PAYLOAD_SIZE = 25;

        /// <summary>
        /// The size, in bytes, of a quantized transform payload that also carries
        /// the owner's input tick.
        /// </summary>
        public const int QUANTIZED_PAYLOAD_SIZE_WITH_TICK = QUANTIZED_PAYLOAD_SIZE + INPUT_TICK_SIZE;

        /// <summary>The flag that marks a payload as quantized.</summary>
        public const byte FLAG_QUANTIZED = 0x01;

        /// <summary>Width of the trailing <c>flags</c> byte, after the input tick.</summary>
        internal const int FLAGS_SIZE = 1;

        /// <summary>
        /// The one value the trailing <c>flags</c> byte is written with: the pose
        /// is a teleport (<see cref="NetworkTransform.OwnerTeleportTo"/>).
        /// </summary>
        internal const byte FLAG_TELEPORT = 0x01;

        /// <summary>
        /// The size, in bytes, of a full-precision transform payload carrying the
        /// input tick and the flags byte: 53.
        /// </summary>
        internal const int PAYLOAD_SIZE_WITH_FLAGS = PAYLOAD_SIZE_WITH_TICK + FLAGS_SIZE;

        /// <summary>
        /// The size, in bytes, of a quantized transform payload carrying the input
        /// tick and the flags byte: 30.
        /// </summary>
        internal const int QUANTIZED_PAYLOAD_SIZE_WITH_FLAGS = QUANTIZED_PAYLOAD_SIZE_WITH_TICK + FLAGS_SIZE;

        /// <summary>
        /// Mark the payload of <paramref name="written"/> bytes at
        /// <paramref name="destOffset"/> in <paramref name="dest"/> as a teleport,
        /// by appending the flags byte, and return the new length — or
        /// <c>0</c>, appending nothing, when the payload is not one of the two
        /// with an input tick (only those take a flags byte) or
        /// <paramref name="dest"/> has no room for it.
        /// </summary>
        internal static int AppendTeleportFlag(byte[] dest, int destOffset, int written)
        {
            if (dest == null) return 0;
            if (written != PAYLOAD_SIZE_WITH_TICK && written != QUANTIZED_PAYLOAD_SIZE_WITH_TICK) return 0;
            if (destOffset < 0 || destOffset > dest.Length - written - FLAGS_SIZE) return 0;
            dest[destOffset + written] = FLAG_TELEPORT;
            return written + FLAGS_SIZE;
        }

        // ── Factory method ────────────────────────────────────────────────────

        /// <summary>
        /// Builds a full-precision transform payload in a new array.
        /// </summary>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <returns>
        /// The payload, or <see langword="null"/> when the position or scale has a
        /// NaN or infinite component.
        /// </returns>
        /// <remarks>
        /// A rotation that is not a valid unit quaternion is written as the
        /// nearest valid rotation, with a warning.
        /// </remarks>
        public static byte[] BuildUpdatePayload(ulong objectId, TransformState state)
        {
            var payload = new byte[PAYLOAD_SIZE];
            int written = BuildUpdatePayloadInto(payload, 0, objectId, state);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Builds a full-precision transform payload that also carries
        /// <paramref name="inputTick"/>, in a new array.
        /// </summary>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <param name="inputTick">
        /// The owner's input tick the pose was produced at. The server returns it
        /// with its corrections, for client-side prediction.
        /// </param>
        /// <returns>
        /// The payload, or <see langword="null"/> when the position or scale has a
        /// NaN or infinite component.
        /// </returns>
        public static byte[] BuildUpdatePayload(ulong objectId, TransformState state, uint inputTick)
        {
            var payload = new byte[PAYLOAD_SIZE_WITH_TICK];
            int written = BuildUpdatePayloadInto(payload, 0, objectId, state, inputTick);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Writes a full-precision transform payload into <paramref name="dest"/>
        /// at <paramref name="destOffset"/>.
        /// </summary>
        /// <param name="dest">The buffer to write to, for example one rented from <c>ArrayPool&lt;byte&gt;.Shared</c>.</param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <returns>
        /// <see cref="PAYLOAD_SIZE"/>, or <c>0</c> when the position or scale has
        /// a NaN or infinite component; nothing is written then.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="destOffset"/> is negative, or <paramref name="dest"/> is
        /// too small for the payload at <paramref name="destOffset"/>.
        /// </exception>
        public static int BuildUpdatePayloadInto(byte[] dest, int destOffset, ulong objectId, TransformState state)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            // Use a long-typed sum so a short dest (length < PAYLOAD_SIZE)
            // does not wrap into a positive uint and skip the check.
            if (destOffset < 0 || (long)destOffset + PAYLOAD_SIZE > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a transform payload at the given offset.");

            // ── Position and scale: refused, not repaired ─────────────────────
            //
            // The gateway refuses this record for us and says nothing to anyone:
            // `parse_full_transform` reads every component through `read_f32_le`,
            // which answers None on a non-finite one, and the datagram is
            // dropped.  So the pre-existing cost was one lost update per send,
            // the bandwidth to send it, and no signal on the client that caused
            // it. ⛔ NOT a lost frame of other objects — a StateSync uplink
            // carries one record, and the cascade that costs every object after
            // it belongs to the ROTATION path, where the sync service clamps
            // only non-finite quaternion components and a zero quaternion
            // survives into a downlink batch.
            //
            // The rotation two blocks down is SUBSTITUTED because a non-unit
            // quaternion has a nearest valid rotation; a NaN coordinate has no
            // nearest valid coordinate, and putting the origin or the last known
            // value on the wire moves the object somewhere it is not for every
            // other player.
            //
            // 🔑 The durable damage is at the CALLER, not here: a non-finite
            // pose recorded as the last one sent makes every later
            // change-detection and velocity-cap comparison false, so the object
            // stops broadcasting for good.  Returning 0 is what lets the caller
            // leave that baseline alone.
            //
            // The check precedes the first write, so a refused record leaves
            // `dest` exactly as it was — a caller reusing a pooled buffer across
            // ticks never sends a half-written frame from it.
            if (!WireVector.IsFinite(state.Position) || !WireVector.IsFinite(state.Scale))
            {
                if (WarnGate.ShouldEmit(ref _lastNonFiniteCoordinateWarnTicks))
                {
                    Debug.LogWarning(
                        $"[RTMPE] TransformPacketBuilder: object {objectId} has a non-finite " +
                        "position or scale (NaN or Infinity), so no transform update was sent " +
                        "for it. The gateway would have dropped the frame anyway, and there is " +
                        "no nearest valid coordinate to substitute — sending one would move the " +
                        "object somewhere it is not for every other player. This object will " +
                        "not replicate again until its transform is finite. The usual cause is " +
                        "a physics step that diverged or a division by zero feeding the " +
                        "transform.");
                }
                return 0;
            }

            // ── ObjectID (u64 LE) ─────────────────────────────────────────────
            WriteU64LE(dest, destOffset + OFFSET_OBJECT_ID, objectId);

            // ── Position (3 × f32 LE) ─────────────────────────────────────────
            WriteF32LE(dest, destOffset + OFFSET_POSITION + 0,  state.Position.x);
            WriteF32LE(dest, destOffset + OFFSET_POSITION + 4,  state.Position.y);
            WriteF32LE(dest, destOffset + OFFSET_POSITION + 8,  state.Position.z);

            // ── Rotation (4 × f32 LE, x y z w) ──────────────────────────────
            //
            // Sanitised rather than written through.  `TransformState` is a
            // public struct with public fields, so `new TransformState { Position
            // = p }` is a legal construction that leaves Rotation at
            // `default(Quaternion)` — (0,0,0,0), which is not identity and not a
            // rotation.  Written to the wire it is refused by every receiving
            // parser, and a StateDelta batch has no per-record length to
            // resynchronise on, so the refusal costs every object after it in
            // the frame as well.
            var rotation = WireQuaternion.ForWire(state.Rotation, out bool rotationSubstituted);
            if (rotationSubstituted && WarnGate.ShouldEmit(ref _lastRotationSubstitutionWarnTicks))
            {
                Debug.LogWarning(
                    $"[RTMPE] TransformPacketBuilder: object {objectId} was given a rotation the " +
                    "protocol does not carry (a zero, non-finite or grossly non-unit quaternion). " +
                    "Sending the nearest valid rotation instead — the original would have been " +
                    "dropped by every receiver, together with every object after it in the same " +
                    "state frame. `default(Quaternion)` is (0,0,0,0), not identity.");
            }
            WriteF32LE(dest, destOffset + OFFSET_ROTATION + 0,  rotation.x);
            WriteF32LE(dest, destOffset + OFFSET_ROTATION + 4,  rotation.y);
            WriteF32LE(dest, destOffset + OFFSET_ROTATION + 8,  rotation.z);
            WriteF32LE(dest, destOffset + OFFSET_ROTATION + 12, rotation.w);

            // ── Scale (3 × f32 LE) ────────────────────────────────────────────
            WriteF32LE(dest, destOffset + OFFSET_SCALE + 0, state.Scale.x);
            WriteF32LE(dest, destOffset + OFFSET_SCALE + 4, state.Scale.y);
            WriteF32LE(dest, destOffset + OFFSET_SCALE + 8, state.Scale.z);

            return PAYLOAD_SIZE;
        }

        /// <summary>
        /// Writes a full-precision transform payload that also carries
        /// <paramref name="inputTick"/> into <paramref name="dest"/> at
        /// <paramref name="destOffset"/>.
        /// </summary>
        /// <param name="dest">The buffer to write to.</param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <param name="inputTick">The owner's input tick the pose was produced at.</param>
        /// <returns>
        /// <see cref="PAYLOAD_SIZE_WITH_TICK"/>, or <c>0</c> when the position or
        /// scale has a NaN or infinite component; nothing is written then.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="destOffset"/> is negative, or <paramref name="dest"/> is
        /// too small for the payload at <paramref name="destOffset"/>.
        /// </exception>
        public static int BuildUpdatePayloadInto(
            byte[] dest, int destOffset, ulong objectId, TransformState state, uint inputTick)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (destOffset < 0 || (long)destOffset + PAYLOAD_SIZE_WITH_TICK > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a transform payload with input tick at the given offset.");

            // Write the 48-byte transform core (its own bounds check is a
            // subset of the one above and therefore passes), then append the
            // tick at the fixed trailing offset.
            //
            // A refused core is propagated rather than completed: a 52-byte
            // frame whose first 48 bytes were never written is a record built
            // from whatever the pooled buffer last held, and it would carry a
            // valid-looking tick on top of it.
            if (BuildUpdatePayloadInto(dest, destOffset, objectId, state) == 0) return 0;
            WriteU32LE(dest, destOffset + PAYLOAD_SIZE, inputTick);
            return PAYLOAD_SIZE_WITH_TICK;
        }

        /// <summary>
        /// Builds a quantized transform payload in a new array: position and scale
        /// as half-precision floats and the rotation compressed.
        /// </summary>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <returns>
        /// The payload, or <see langword="null"/> when a value is NaN or
        /// infinite, a position or scale component is larger in magnitude than
        /// <see cref="TransformQuantization.HalfMaxFinite"/>, or the rotation has
        /// zero length. Send a full-precision payload instead.
        /// </returns>
        public static byte[] BuildQuantizedUpdatePayload(ulong objectId, TransformState state)
        {
            var payload = new byte[QUANTIZED_PAYLOAD_SIZE];
            int written = BuildQuantizedUpdatePayloadInto(payload, 0, objectId, state);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Builds a quantized transform payload that also carries
        /// <paramref name="inputTick"/>, in a new array.
        /// </summary>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <param name="inputTick">The owner's input tick the pose was produced at.</param>
        /// <returns>
        /// The payload, or <see langword="null"/> in the cases
        /// <see cref="BuildQuantizedUpdatePayload(ulong, TransformState)"/>
        /// describes.
        /// </returns>
        public static byte[] BuildQuantizedUpdatePayload(ulong objectId, TransformState state, uint inputTick)
        {
            var payload = new byte[QUANTIZED_PAYLOAD_SIZE_WITH_TICK];
            int written = BuildQuantizedUpdatePayloadInto(payload, 0, objectId, state, inputTick);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Writes a quantized transform payload into <paramref name="dest"/> at
        /// <paramref name="destOffset"/>.
        /// </summary>
        /// <param name="dest">The buffer to write to.</param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <returns>
        /// <see cref="QUANTIZED_PAYLOAD_SIZE"/>, or <c>0</c> in the cases
        /// <see cref="BuildQuantizedUpdatePayload(ulong, TransformState)"/>
        /// describes.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="destOffset"/> is negative, or <paramref name="dest"/> is
        /// too small for the payload at <paramref name="destOffset"/>.
        /// </exception>
        public static int BuildQuantizedUpdatePayloadInto(byte[] dest, int destOffset, ulong objectId, TransformState state)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (destOffset < 0 || (long)destOffset + QUANTIZED_PAYLOAD_SIZE > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a quantized transform payload at the given offset.");

            // Half-precision floats lose ~20 bits of mantissa; rejecting NaN/Inf
            // at the encoder keeps the wire format total over its declared
            // domain (finite rigid-body poses) and prevents a degenerate
            // simulation from propagating sentinel bit patterns to peers.
            if (!WireVector.IsFinite(state.Position) || !WireVector.IsFinite(state.Scale))
                return 0;

            dest[destOffset + 0] = FLAG_QUANTIZED;

            WriteU64LE(dest, destOffset + 1, objectId);

            if (!TransformQuantization.TryWriteHalf(dest, destOffset +  9, state.Position.x)) return 0;
            if (!TransformQuantization.TryWriteHalf(dest, destOffset + 11, state.Position.y)) return 0;
            if (!TransformQuantization.TryWriteHalf(dest, destOffset + 13, state.Position.z)) return 0;

            if (!TransformQuantization.TryWriteSmallestThree(dest, destOffset + 15, state.Rotation)) return 0;

            if (!TransformQuantization.TryWriteHalf(dest, destOffset + 19, state.Scale.x)) return 0;
            if (!TransformQuantization.TryWriteHalf(dest, destOffset + 21, state.Scale.y)) return 0;
            if (!TransformQuantization.TryWriteHalf(dest, destOffset + 23, state.Scale.z)) return 0;

            return QUANTIZED_PAYLOAD_SIZE;
        }

        /// <summary>
        /// Writes a quantized transform payload that also carries
        /// <paramref name="inputTick"/> into <paramref name="dest"/> at
        /// <paramref name="destOffset"/>.
        /// </summary>
        /// <param name="dest">The buffer to write to.</param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The pose to write.</param>
        /// <param name="inputTick">The owner's input tick the pose was produced at.</param>
        /// <returns>
        /// <see cref="QUANTIZED_PAYLOAD_SIZE_WITH_TICK"/>, or <c>0</c> in the cases
        /// <see cref="BuildQuantizedUpdatePayload(ulong, TransformState)"/>
        /// describes.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="destOffset"/> is negative, or <paramref name="dest"/> is
        /// too small for the payload at <paramref name="destOffset"/>.
        /// </exception>
        public static int BuildQuantizedUpdatePayloadInto(
            byte[] dest, int destOffset, ulong objectId, TransformState state, uint inputTick)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (destOffset < 0 || (long)destOffset + QUANTIZED_PAYLOAD_SIZE_WITH_TICK > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a quantized transform payload with input tick at the given offset.");

            int core = BuildQuantizedUpdatePayloadInto(dest, destOffset, objectId, state);
            if (core == 0) return 0; // non-finite / degenerate → caller falls back
            WriteU32LE(dest, destOffset + QUANTIZED_PAYLOAD_SIZE, inputTick);
            return QUANTIZED_PAYLOAD_SIZE_WITH_TICK;
        }

        // ── Private write helpers ─────────────────────────────────────────────

        // WriteU64LE writes an unsigned 64-bit integer in little-endian byte order.
        private static void WriteU64LE(byte[] buf, int off, ulong v)
        {
            buf[off + 0] = (byte) v;
            buf[off + 1] = (byte)(v >>  8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
            buf[off + 4] = (byte)(v >> 32);
            buf[off + 5] = (byte)(v >> 40);
            buf[off + 6] = (byte)(v >> 48);
            buf[off + 7] = (byte)(v >> 56);
        }

        // WriteU32LE writes an unsigned 32-bit integer in little-endian byte
        // order.  Used for the SDKS-01 trailing input-tick field.
        private static void WriteU32LE(byte[] buf, int off, uint v)
        {
            buf[off + 0] = (byte) v;
            buf[off + 1] = (byte)(v >>  8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
        }

        // WriteF32LE writes an IEEE 754 single-precision float in little-endian
        // byte order using BitConverter.SingleToInt32Bits for zero-allocation
        // bit reinterpretation.
        private static void WriteF32LE(byte[] buf, int off, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            buf[off + 0] = (byte) bits;
            buf[off + 1] = (byte)(bits >>  8);
            buf[off + 2] = (byte)(bits >> 16);
            buf[off + 3] = (byte)(bits >> 24);
        }
    }
}
