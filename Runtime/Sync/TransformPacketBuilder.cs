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
    /// Builds binary payloads for client-to-server transform update packets.
    /// All methods are static and allocation-minimal (one <c>new byte[48]</c> per call).
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

        /// <summary>
        /// Size in bytes of a transform update payload.
        /// ObjectID(8) + Position(12) + Rotation(16) + Scale(12) = 48 bytes.
        /// </summary>
        public const int PAYLOAD_SIZE = 48;

        /// <summary>
        /// Size of a full-precision transform payload extended with the SDKS-01
        /// trailing input tick: <see cref="PAYLOAD_SIZE"/> + 4 (u32 LE) = 52.
        /// The gateway accepts both 48- and 52-byte forms; the SDK send path
        /// always emits this extended form so the server can echo the tick.
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

        /// <summary>
        /// Total wire size of the quantized payload variant.
        /// flags(1) + object_id(8) + half-float position(6) + packed rotation(4)
        /// + half-float scale(6) = 25 bytes.
        /// </summary>
        public const int QUANTIZED_PAYLOAD_SIZE = 25;

        /// <summary>
        /// Size of a quantized transform payload extended with the SDKS-01
        /// trailing input tick: <see cref="QUANTIZED_PAYLOAD_SIZE"/> + 4 = 29.
        /// </summary>
        public const int QUANTIZED_PAYLOAD_SIZE_WITH_TICK = QUANTIZED_PAYLOAD_SIZE + INPUT_TICK_SIZE;

        /// <summary>
        /// Bit set in the <c>flags</c> byte of a quantized payload.  A future
        /// extension may add additional flags in higher bits without breaking
        /// the dispatcher's length-then-flag detection.
        /// </summary>
        public const byte FLAG_QUANTIZED = 0x01;

        // ── Factory method ────────────────────────────────────────────────────

        /// <summary>
        /// Build a 48-byte binary payload encoding <paramref name="objectId"/>
        /// and <paramref name="state"/> in the RTMPE transform update wire format.
        /// </summary>
        /// <param name="objectId">
        /// Unity <c>NetworkObject.NetworkObjectId</c> — identifies the object
        /// being updated on the server.
        /// </param>
        /// <param name="state">
        /// The current transform snapshot to encode.
        /// </param>
        /// <returns>
        /// A newly allocated 48-byte array, or <see langword="null"/> when
        /// <paramref name="state"/> carries a non-finite position or scale —
        /// see <see cref="BuildUpdatePayloadInto(byte[], int, ulong, TransformState)"/>
        /// for why that record is refused rather than repaired.  Pass to
        /// <c>NetworkManager.Instance.SendData()</c> to transmit.
        /// </returns>
        public static byte[] BuildUpdatePayload(ulong objectId, TransformState state)
        {
            var payload = new byte[PAYLOAD_SIZE];
            int written = BuildUpdatePayloadInto(payload, 0, objectId, state);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Build a 52-byte full-precision payload that appends the SDKS-01
        /// <paramref name="inputTick"/> (u32 LE) after the transform fields.
        /// The server echoes the tick back on the object's <c>StateDelta</c> so
        /// the owning client can acknowledge its input buffer up to that
        /// watermark and replay only still-in-flight inputs.
        /// </summary>
        public static byte[] BuildUpdatePayload(ulong objectId, TransformState state, uint inputTick)
        {
            var payload = new byte[PAYLOAD_SIZE_WITH_TICK];
            int written = BuildUpdatePayloadInto(payload, 0, objectId, state, inputTick);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Pooled-buffer variant: writes the 48-byte transform payload into
        /// <paramref name="dest"/> starting at <paramref name="destOffset"/>.
        /// Callers may rent <paramref name="dest"/> from
        /// <c>ArrayPool&lt;byte&gt;.Shared</c> to keep the per-tick send path
        /// allocation-free.
        /// </summary>
        /// <returns>
        /// <see cref="PAYLOAD_SIZE"/>, or <c>0</c> when the state carries a
        /// non-finite position or scale, in which case nothing is written to
        /// <paramref name="dest"/> at all.
        /// </returns>
        /// <remarks>
        /// 🔑 Zero is the same answer <see cref="BuildQuantizedUpdatePayloadInto(byte[], int, ulong, TransformState)"/>
        /// already gives for the same condition, so the full-precision encoder
        /// stops being the fallback that emits what the quantized one refused.
        /// The caller's <c>written == 0</c> arm around the quantized attempt is
        /// the FALLBACK, not a refusal test — acting on this return needed a
        /// second arm after it, added in the same change.
        /// </remarks>
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
        /// Pooled-buffer variant that appends the SDKS-01 <paramref name="inputTick"/>
        /// (u32 LE) after the 48-byte transform body, writing 52 bytes total.
        /// Returns <see cref="PAYLOAD_SIZE_WITH_TICK"/>, or <c>0</c> when the
        /// core record is refused for a non-finite position or scale.  The tick is strictly
        /// trailing, so the 48-byte prefix is byte-identical to the legacy
        /// layout and a legacy decoder ignores the extra bytes.
        /// </summary>
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
        /// Build the 25-byte quantized variant of the transform-update payload.
        /// Encodes position and scale as half-precision floats and rotation as
        /// a smallest-three packed quaternion.  Returns <see langword="null"/>
        /// when any source value is non-finite (NaN/Inf) or the rotation is
        /// degenerate — the caller falls back to the full-precision builder
        /// or skips the update; emitting a sentinel-laced quantized payload
        /// would corrupt the receiver's transform.
        /// </summary>
        public static byte[] BuildQuantizedUpdatePayload(ulong objectId, TransformState state)
        {
            var payload = new byte[QUANTIZED_PAYLOAD_SIZE];
            int written = BuildQuantizedUpdatePayloadInto(payload, 0, objectId, state);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Build a 29-byte quantized payload that appends the SDKS-01
        /// <paramref name="inputTick"/> (u32 LE) after the quantized body.
        /// Returns <see langword="null"/> when the source state is non-finite or
        /// the rotation is degenerate (matching the tick-less factory), so the
        /// caller can fall back to the full-precision encoder.
        /// </summary>
        public static byte[] BuildQuantizedUpdatePayload(ulong objectId, TransformState state, uint inputTick)
        {
            var payload = new byte[QUANTIZED_PAYLOAD_SIZE_WITH_TICK];
            int written = BuildQuantizedUpdatePayloadInto(payload, 0, objectId, state, inputTick);
            return written > 0 ? payload : null;
        }

        /// <summary>
        /// Pooled-buffer variant of <see cref="BuildQuantizedUpdatePayload"/>.
        /// Writes the 25-byte payload into <paramref name="dest"/> starting at
        /// <paramref name="destOffset"/>.  Returns the number of bytes
        /// written (<see cref="QUANTIZED_PAYLOAD_SIZE"/>) on success, or
        /// <c>0</c> when the source state is non-finite or the rotation is
        /// degenerate — matching the legacy <c>null</c> return contract.
        /// </summary>
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
        /// Pooled-buffer variant that appends the SDKS-01 <paramref name="inputTick"/>
        /// (u32 LE) after the 25-byte quantized body, writing 29 bytes total.
        /// Returns <see cref="QUANTIZED_PAYLOAD_SIZE_WITH_TICK"/> on success, or
        /// <c>0</c> when the source state is non-finite or the rotation is
        /// degenerate (no tick is written in that case, mirroring the tick-less
        /// overload's contract so the caller's fallback is unchanged).
        /// </summary>
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
