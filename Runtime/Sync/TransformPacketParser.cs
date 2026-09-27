// RTMPE SDK — Runtime/Sync/TransformPacketParser.cs
//
// Parses server-to-client StateDelta payloads into TransformState values.
//
// Wire format produced by Go's StateDelta.Serialize() (state_delta.go):
//  [0..7]  object_id    : u64  (little-endian)
//  [8]     changed_mask : u8   (bit flags, see constants below)
//  [opt]   position     : 3 × f32 LE   (12 bytes; present iff bit 0x01 set)
//  [opt]   rotation     : 4 × f32 LE   (16 bytes; present iff bit 0x02 set)
//  [opt]   scale        : 3 × f32 LE   (12 bytes; present iff bit 0x04 set)
//  [opt]   input_tick   : u32 LE       (4 bytes;  present iff bit 0x08 set; SDKS-01)
//  [opt]   server_tick  : u32 LE       (4 bytes;  present iff bit 0x10 set; broadcast clock)
//
// Changed-field bit constants MUST match Go's state_delta.go:
//  ChangedPosition   byte = 1 << 0  // 0x01
//  ChangedRotation   byte = 1 << 1  // 0x02
//  ChangedScale      byte = 1 << 2  // 0x04
//  ChangedInputTick  byte = 1 << 3  // 0x08  (SDKS-01)
//  ChangedServerTick byte = 1 << 4  // 0x10
//  knownMask         byte = 0x1F
//
// Unknown bits (bits 5..7) are rejected → TryParseStateDelta returns false.
// This prevents silent field misalignment when the protocol adds new fields.
//
// Caller responsibility:
//  Check changedMask after a successful parse.  Only fields with their
//  corresponding bit set carry meaningful values.  State fields whose bits
//  are NOT set hold zero initialisation values and must be ignored.
//
// Thread safety: all methods are static; no shared state.

using System;
using UnityEngine;
// `IsFinite` below is WireVector's rather than a private copy.  Malformed
// packets can encode IEEE 754 bit patterns that decode as NaN or Inf, and
// applying one to a Unity transform causes undefined physics behaviour, so this
// file refuses them — and TransformPacketBuilder asks the SAME function before
// it writes, so the two directions cannot come apart under an edit to one.
using static RTMPE.Sync.WireVector;

namespace RTMPE.Sync
{
    /// <summary>
    /// Reads the transform updates the server sends, and quantized transform
    /// payloads. Used by the SDK; not intended to be called from game code.
    /// </summary>
    public static class TransformPacketParser
    {
        // ── Changed-field bit flags ────────────────────────────────────────────
        //
       // SYNC RULE: These values must equal the Go constants in state_delta.go.
        //  ChangedPosition   byte = 1 << 0  // 0x01
        //  ChangedRotation   byte = 1 << 1  // 0x02
        //  ChangedScale      byte = 1 << 2  // 0x04
        //  ChangedInputTick  byte = 1 << 3  // 0x08  (SDKS-01)
        //  ChangedServerTick byte = 1 << 4  // 0x10
        //  knownMask         byte = 0x1F
        //
       // A mismatch causes the parser to silently decode wrong fields.

        /// <summary>Field bit: the update carries a position.</summary>
        public const byte ChangedPosition = 0x01;

        /// <summary>Field bit: the update carries a rotation.</summary>
        public const byte ChangedRotation = 0x02;

        /// <summary>Field bit: the update carries a scale.</summary>
        public const byte ChangedScale = 0x04;

        /// <summary>
        /// The position, rotation and scale bits together; the tick bits are not
        /// included (see <see cref="KnownMask"/>).
        /// </summary>
        public const byte ChangedAll = (byte)(ChangedPosition | ChangedRotation | ChangedScale); // 0x07

        /// <summary>
        /// Field bit: the update carries the owner's input tick the server had
        /// applied (see <see cref="TransformState.ConfirmedInputTick"/>).
        /// </summary>
        public const byte ChangedInputTick = 0x08;

        /// <summary>
        /// Field bit: the update carries the server broadcast tick it was sent in
        /// (see <see cref="TransformState.ServerTick"/>).
        /// </summary>
        public const byte ChangedServerTick = 0x10;

        /// <summary>
        /// All the field bits this parser reads. An update with any other bit set
        /// is refused.
        /// </summary>
        public const byte KnownMask = 0x1F;

        /// <summary>
        /// The size, in bytes, of a quantized transform payload built by
        /// <c>TransformPacketBuilder.BuildQuantizedUpdatePayload</c>.
        /// </summary>
        public const int QUANTIZED_UPDATE_SIZE = 25;

        /// <summary>The flag that marks a payload as quantized.</summary>
        public const byte FLAG_QUANTIZED = 0x01;

        /// <summary>
        /// The position of the field-mask byte in an update. Used to tell an
        /// update from a quantized payload of the same length.
        /// </summary>
        public const int CHANGED_MASK_OFFSET = 8;

        // ── Size constants ─────────────────────────────────────────────────────

        /// <summary>
        /// The size, in bytes, of an update that carries no fields. Such an update
        /// is valid.
        /// </summary>
        public const int DELTA_MIN_SIZE = 9;

        private const int POSITION_SIZE   = 12; // 3 × f32
        private const int ROTATION_SIZE   = 16; // 4 × f32 (x y z w)
        private const int SCALE_SIZE      = 12; // 3 × f32
        private const int INPUT_TICK_SIZE = 4;  // 1 × u32 LE (SDKS-01)
        private const int SERVER_TICK_SIZE = 4; // 1 × u32 LE (broadcast clock)

        // ── Parser ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a payload that holds exactly one transform update.
        /// </summary>
        /// <param name="payload">The payload, without the packet header.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object the update describes.</param>
        /// <param name="changedMask">
        /// The update's field mask. Check <see cref="ChangedPosition"/>,
        /// <see cref="ChangedRotation"/> and <see cref="ChangedScale"/> before
        /// reading a field of <paramref name="state"/>.
        /// </param>
        /// <param name="state">
        /// The update read. Only the fields whose bit is set in
        /// <paramref name="changedMask"/> are valid; the others are zero.
        /// </param>
        /// <returns>
        /// <see langword="false"/> for a payload that is <see langword="null"/>,
        /// truncated or too long, or carries unknown field bits, a NaN or
        /// infinite value, or a rotation that is not close to unit length.
        /// </returns>
        public static bool TryParseStateDelta(
            byte[] payload,
            out ulong objectId,
            out byte  changedMask,
            out TransformState state)
        {
            objectId    = 0;
            changedMask = 0;
            state       = default;

            if (payload == null) return false;
            int offset = 0;
            if (!TryParseStateDeltaAt(
                    payload, ref offset,
                    out objectId, out changedMask, out state))
                return false;

            // The single-record overload preserves its strict contract: a
            // well-formed StateDelta must end exactly where the last selected
            // field's bytes end.  Surplus bytes here indicate either an
            // ambiguous concatenated payload (handled by the iteration
            // overload elsewhere) or a protocol-drift / smuggling signal
            // that this overload's callers must reject.
            return offset == payload.Length;
        }

        /// <summary>
        /// Reads one transform update from <paramref name="payload"/> at
        /// <paramref name="offset"/> and, on success, moves
        /// <paramref name="offset"/> past it.
        /// </summary>
        /// <param name="payload">The payload, without the packet header.</param>
        /// <param name="offset">Where the update starts; on success, where the next one starts.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object the update describes.</param>
        /// <param name="changedMask">The update's field mask.</param>
        /// <param name="state">
        /// The update read. Only the fields whose bit is set in
        /// <paramref name="changedMask"/> are valid; the others are zero.
        /// </param>
        /// <returns>
        /// <see langword="false"/> for an update that is truncated or carries
        /// unknown field bits, a NaN or infinite value, or a rotation that is not
        /// close to unit length.
        /// </returns>
        /// <remarks>
        /// Unlike <see cref="TryParseStateDelta"/>, bytes after the update are
        /// allowed, so a payload that carries several updates can be read in a
        /// loop.
        /// </remarks>
        public static bool TryParseStateDeltaAt(
            byte[] payload,
            ref int offset,
            out ulong objectId,
            out byte changedMask,
            out TransformState state)
        {
            objectId    = 0;
            changedMask = 0;
            state       = default;

            if (payload == null || offset < 0 || offset > payload.Length - DELTA_MIN_SIZE)
                return false;

            int off = offset;

            // ObjectID (u64 LE)
            objectId = ReadU64LE(payload, off);
            off += 8;

            // ChangedMask (u8)
            changedMask = payload[off++];

            // Reject unknown bits — any future protocol extension will set
            // a bit outside KnownMask; reading unknown fields would misalign
            // all subsequent offsets and corrupt the decoded state.
            if ((changedMask & ~KnownMask) != 0)
                return false;

            // Start from zero-initialised values; only populate bits that are set.
            var pos   = Vector3.zero;
            var rot   = new Quaternion(0f, 0f, 0f, 0f); // raw zero — NOT identity; caller checks mask
            var scale = Vector3.zero;

            // Position (3 × f32 LE) — conditional on bit 0x01.  Bounds are
            // expressed in subtraction form (`size > available`) so the
            // additive form's int-wrap surface — `off` near int.MaxValue
            // would let `off + N` wrap to a negative value and bypass the
            // check — does not apply here.
            if ((changedMask & ChangedPosition) != 0)
            {
                if (POSITION_SIZE > payload.Length - off) return false;
                pos.x = ReadF32LE(payload, off);     off += 4;
                pos.y = ReadF32LE(payload, off);     off += 4;
                pos.z = ReadF32LE(payload, off);     off += 4;
                if (!IsFinite(pos.x) || !IsFinite(pos.y) || !IsFinite(pos.z)) return false;
            }

            // Rotation (4 × f32 LE, x y z w) — conditional on bit 0x02
            if ((changedMask & ChangedRotation) != 0)
            {
                if (ROTATION_SIZE > payload.Length - off) return false;
                rot.x = ReadF32LE(payload, off);     off += 4;
                rot.y = ReadF32LE(payload, off);     off += 4;
                rot.z = ReadF32LE(payload, off);     off += 4;
                rot.w = ReadF32LE(payload, off);     off += 4;
                if (!IsFinite(rot.x) || !IsFinite(rot.y) || !IsFinite(rot.z) || !IsFinite(rot.w)) return false;

                // Quaternions applied to transform.rotation or fed into physics
                // MUST be unit-length; a non-unit quaternion silently skews
                // interpolation, breaks Quaternion.Slerp, and — in extreme
                // cases — destabilises PhysX.  We reject clearly malformed
                // (|q|² outside a generous ±0.1 band) to catch corruption /
                // protocol bugs, then renormalise to erase benign FP drift so
                // downstream math (Slerp, inverse, multiplication) stays sane.
                float magSq = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                if (magSq < WireQuaternion.MinMagSq || magSq > WireQuaternion.MaxMagSq) return false;
                // magSq ∈ [0.9, 1.1] here — guaranteed > 0, so sqrt and the
                // reciprocal are safe without an extra zero-guard.
                float invMag = 1f / (float)System.Math.Sqrt(magSq);
                rot.x *= invMag;
                rot.y *= invMag;
                rot.z *= invMag;
                rot.w *= invMag;
            }

            // Scale (3 × f32 LE) — conditional on bit 0x04
            if ((changedMask & ChangedScale) != 0)
            {
                if (SCALE_SIZE > payload.Length - off) return false;
                scale.x = ReadF32LE(payload, off);   off += 4;
                scale.y = ReadF32LE(payload, off);   off += 4;
                scale.z = ReadF32LE(payload, off);   off += 4;
                if (!IsFinite(scale.x) || !IsFinite(scale.y) || !IsFinite(scale.z)) return false;
            }

            // InputTick (u32 LE) — conditional on bit 0x08 (SDKS-01).  The
            // server appends this LAST, after every transform field, so the
            // offsets above are byte-identical to a record without the tick.
            // Tick 0 is a legitimate value; presence is carried by the bit, not
            // a sentinel, so HasConfirmedInputTick mirrors the mask exactly.
            uint confirmedInputTick   = 0u;
            bool hasConfirmedInputTick = false;
            if ((changedMask & ChangedInputTick) != 0)
            {
                if (INPUT_TICK_SIZE > payload.Length - off) return false;
                confirmedInputTick   = ReadU32LE(payload, off); off += 4;
                hasConfirmedInputTick = true;
            }

            // ServerTick (u32 LE) — conditional on bit 0x10.  Appended AFTER
            // InputTick (ascending bit order), so a record without the input
            // tick still reads the server tick at the correct offset.  This is
            // the room's broadcast sequence at emit time; the non-owner receive
            // path uses it as the sender-domain clock for jitter-free
            // interpolation.  Tick 0 is legitimate; presence is the bit.
            uint serverTick    = 0u;
            bool hasServerTick = false;
            if ((changedMask & ChangedServerTick) != 0)
            {
                if (SERVER_TICK_SIZE > payload.Length - off) return false;
                serverTick    = ReadU32LE(payload, off); off += 4;
                hasServerTick = true;
            }

            state = new TransformState
            {
                Position              = pos,
                Rotation              = rot,
                Scale                 = scale,
                ConfirmedInputTick    = confirmedInputTick,
                HasConfirmedInputTick = hasConfirmedInputTick,
                ServerTick            = serverTick,
                HasServerTick         = hasServerTick,
            };
            offset = off;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="payload"/> is a quantized transform payload
        /// rather than a transform update of the same length.
        /// </summary>
        /// <param name="payload">The payload to inspect.</param>
        /// <returns><see langword="true"/> for a quantized transform payload.</returns>
        public static bool LooksLikeQuantizedFrame(byte[] payload)
        {
            return payload != null
                && payload.Length == QUANTIZED_UPDATE_SIZE
                && (payload[0] & FLAG_QUANTIZED) != 0
                && (payload[CHANGED_MASK_OFFSET] & ~KnownMask) != 0;
        }

        /// <summary>
        /// Reads a quantized transform payload, the form
        /// <see cref="NetworkTransform"/> sends when
        /// <c>NetworkSettings.quantizeTransforms</c> is on.
        /// </summary>
        /// <param name="payload">The payload, without the packet header.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object the payload describes.</param>
        /// <param name="state">The pose read.</param>
        /// <returns>
        /// <see langword="false"/> for a payload that is <see langword="null"/>,
        /// not the quantized size, or not marked as quantized.
        /// </returns>
        public static bool TryParseQuantizedUpdate(
            byte[] payload,
            out ulong objectId,
            out TransformState state)
        {
            objectId = 0;
            state    = default;

            if (payload == null || payload.Length != QUANTIZED_UPDATE_SIZE) return false;
            if ((payload[0] & FLAG_QUANTIZED) == 0) return false;

            objectId = ReadU64LE(payload, 1);

            float px = TransformQuantization.ReadHalf(payload, 9);
            float py = TransformQuantization.ReadHalf(payload, 11);
            float pz = TransformQuantization.ReadHalf(payload, 13);

            Quaternion rot = TransformQuantization.ReadSmallestThree(payload, 15);

            float sx = TransformQuantization.ReadHalf(payload, 19);
            float sy = TransformQuantization.ReadHalf(payload, 21);
            float sz = TransformQuantization.ReadHalf(payload, 23);

            // ReadHalf already maps malformed inputs to 0, so the only way a
            // non-finite value could land here is an in-band runtime bug;
            // a final guard makes the parser total over its declared domain.
            if (!IsFinite(px) || !IsFinite(py) || !IsFinite(pz)) return false;
            if (!IsFinite(sx) || !IsFinite(sy) || !IsFinite(sz)) return false;

            state = new TransformState
            {
                Position = new Vector3(px, py, pz),
                Rotation = rot,
                Scale    = new Vector3(sx, sy, sz),
            };
            return true;
        }

        // ── Private helpers ────────────────────────────────────────────────────

        // ReadU64LE reads eight consecutive bytes as a little-endian u64.
        private static ulong ReadU64LE(byte[] buf, int off)
            =>  (ulong)buf[off + 0]
             | ((ulong)buf[off + 1] <<  8)
             | ((ulong)buf[off + 2] << 16)
             | ((ulong)buf[off + 3] << 24)
             | ((ulong)buf[off + 4] << 32)
             | ((ulong)buf[off + 5] << 40)
             | ((ulong)buf[off + 6] << 48)
             | ((ulong)buf[off + 7] << 56);

        // ReadU32LE reads four consecutive bytes as a little-endian u32.
        // Used for the SDKS-01 InputTick trailing field.
        private static uint ReadU32LE(byte[] buf, int off)
            =>  (uint)buf[off + 0]
             | ((uint)buf[off + 1] <<  8)
             | ((uint)buf[off + 2] << 16)
             | ((uint)buf[off + 3] << 24);

        // ReadF32LE reads four consecutive bytes as a little-endian IEEE 754 f32.
        // BitConverter.Int32BitsToSingle performs zero-allocation bit reinterpretation
        // (available in .NET Standard 2.1 / Unity 2019.3+).
        private static float ReadF32LE(byte[] buf, int off)
        {
            int bits =  buf[off + 0]
                     | (buf[off + 1] <<  8)
                     | (buf[off + 2] << 16)
                     | (buf[off + 3] << 24);
            return BitConverter.Int32BitsToSingle(bits);
        }
    }
}
