// RTMPE SDK — Runtime/Sync/PhysicsPacketBuilder.cs
//
// Builds binary payloads for physics-state packets sent by NetworkRigidbody
// and NetworkRigidbody2D.  Payloads are transmitted as PacketType.PhysicsSync
// (0x45) — a type of their own, because this layout and the quantized
// transform layout share lengths and nothing in the bytes separates them.
//
// ── What the type markers do ──────────────────────────────────────────────────
//
// Bit 0x40 (3-D) and bit 0x80 (2-D) in the changed_mask select which of the two
// field layouts below the payload is written in, and they carry no length of
// their own (DataFieldMask = 0x3F bounds the fields).  Telling a rigidbody
// frame from a transform is the packet type's job, not theirs: the two
// layouts overlap in length, so a marker read out of a transform's object_id
// says nothing.
//
// ── 3-D Physics wire format ───────────────────────────────────────────────────
//
//  [0..7]  object_id    : u64 LE
//  [8]     changed_mask : u8  (TypeMarker3D = 0x40 always set)
//            bit 0x01 = position        (3 × f32 LE = 12 bytes)
//            bit 0x02 = rotation        (4 × f32 LE = 16 bytes, x y z w)
//            bit 0x04 = velocity        (3 × f32 LE = 12 bytes)
//            bit 0x08 = angular_velocity(3 × f32 LE = 12 bytes)
//            bit 0x10 = is_sleeping     (u8: 0x00 = awake, 0x01 = sleeping)
//            bit 0x20 = constraint_mask (u8: bitmask of UnityEngine.RigidbodyConstraints)
//            bit 0x40 = TYPE MARKER     (always set; causes transform parser reject)
//  [9+]    conditional fields in the bit-order listed above
//  Min: 9 bytes (header only).  Max: 9 + 12 + 16 + 12 + 12 + 1 + 1 = 63 bytes.
//
// ── 2-D Physics wire format ───────────────────────────────────────────────────
//
//  [0..7]  object_id    : u64 LE
//  [8]     changed_mask : u8  (TypeMarker2D = 0x80 always set)
//            bit 0x01 = position        (2 × f32 LE = 8 bytes)
//            bit 0x02 = rotation        (1 × f32 LE = 4 bytes, degrees)
//            bit 0x04 = velocity        (2 × f32 LE = 8 bytes)
//            bit 0x08 = angular_velocity(1 × f32 LE = 4 bytes, deg/s)
//            bit 0x10 = is_sleeping     (u8: 0x00 = awake, 0x01 = sleeping)
//            bit 0x20 = constraint_mask (u8: bitmask of UnityEngine.RigidbodyConstraints2D)
//            bit 0x80 = TYPE MARKER     (always set; also discriminates from 3-D)
//  [9+]    conditional fields in the bit-order listed above
//  Min: 9 bytes.  Max: 9 + 8 + 4 + 8 + 4 + 1 + 1 = 35 bytes.
//
// ── Security ──────────────────────────────────────────────────────────────────
//
// No AEAD here.  The surrounding gateway pipeline applies ChaCha20-Poly1305
// encryption before any packet leaves the device.

using System;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Builds the physics-state payloads that <see cref="NetworkRigidbody"/> and
    /// <see cref="NetworkRigidbody2D"/> send. Used by the SDK; not intended to be
    /// called from game code.
    /// </summary>
    public static class PhysicsPacketBuilder
    {
        // Static and one-per-second, as in the other two writers: the condition
        // repeats at the send rate for as long as the object exists, and the
        // message is the same every time.
        private static long _lastRotationSubstitutionWarnTicks;

        // Separate from the rotation gate above: the two report different
        // defects with different outcomes — a substituted rotation still goes
        // out, a non-finite coordinate does not — and sharing one gate lets
        // whichever fires first silence the other for the rest of the second.
        private static long _lastNonFiniteCoordinateWarnTicks;

        // ── Changed-field bit constants ────────────────────────────────────────
        //
       // Bits 0x01–0x10 are shared between 3-D and 2-D packets.
        // Bits 0x40 / 0x80 are exclusive type markers.

        /// <summary>Field bit: the payload carries the position.</summary>
        public const byte ChangedPosition = 0x01;

        /// <summary>Field bit: the payload carries the rotation.</summary>
        public const byte ChangedRotation = 0x02;

        /// <summary>Field bit: the payload carries the linear velocity.</summary>
        public const byte ChangedVelocity = 0x04;

        /// <summary>Field bit: the payload carries the angular velocity.</summary>
        public const byte ChangedAngularVelocity = 0x08;

        /// <summary>Field bit: the payload carries the sleep state.</summary>
        public const byte ChangedSleep = 0x10;

        /// <summary>
        /// Field bit: the payload carries the body's constraints. The components
        /// send them only when they change.
        /// </summary>
        public const byte ChangedConstraints = 0x20;

        /// <summary>Set in the field mask of every 3-D payload.</summary>
        public const byte TypeMarker3D = 0x40;

        /// <summary>Set in the field mask of every 2-D payload.</summary>
        public const byte TypeMarker2D = 0x80;

        /// <summary>
        /// All the field bits, from <see cref="ChangedPosition"/> to
        /// <see cref="ChangedConstraints"/>.
        /// </summary>
        public const byte DataFieldMask = 0x3F;

        /// <summary>The position of the field-mask byte in a payload.</summary>
        public const int ChangedMaskOffset = 8;

        /// <summary>
        /// The size, in bytes, of a payload that carries no fields.
        /// </summary>
        public const int PayloadMinSize = 9;

        // ── 3-D builder ───────────────────────────────────────────────────────

        /// <summary>
        /// Builds a 3-D physics payload in a new array.
        /// </summary>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The state to write.</param>
        /// <param name="dataMask">
        /// The field bits of the fields to include; other bits are ignored.
        /// </param>
        /// <returns>
        /// The payload, or <see langword="null"/> when a selected position,
        /// velocity or angular velocity is NaN or infinite.
        /// </returns>
        /// <remarks>
        /// A rotation that is not a valid unit quaternion is written as the
        /// nearest valid rotation, with a warning.
        /// </remarks>
        public static byte[] BuildPayload(ulong objectId, PhysicsState state, byte dataMask)
        {
            int size = ComputePayloadSize(dataMask, twoDee: false);
            var buf = new byte[size];
            int written = BuildPayloadInto(buf, 0, objectId, state, dataMask);
            return written > 0 ? buf : null;
        }

        /// <summary>
        /// Returns the size, in bytes, of the payload
        /// <see cref="BuildPayloadInto"/> (3-D) or
        /// <see cref="Build2DPayloadInto"/> (2-D) writes for
        /// <paramref name="dataMask"/>.
        /// </summary>
        /// <param name="dataMask">The field bits of the fields to include.</param>
        /// <param name="twoDee"><see langword="true"/> for a 2-D payload.</param>
        /// <returns>The payload size in bytes.</returns>
        public static int ComputePayloadSize(byte dataMask, bool twoDee)
        {
            byte changedMask = (byte)(dataMask & DataFieldMask);
            int size = PayloadMinSize;
            if (twoDee)
            {
                if ((changedMask & ChangedPosition)        != 0) size +=  8;
                if ((changedMask & ChangedRotation)        != 0) size +=  4;
                if ((changedMask & ChangedVelocity)        != 0) size +=  8;
                if ((changedMask & ChangedAngularVelocity) != 0) size +=  4;
            }
            else
            {
                if ((changedMask & ChangedPosition)        != 0) size += 12;
                if ((changedMask & ChangedRotation)        != 0) size += 16;
                if ((changedMask & ChangedVelocity)        != 0) size += 12;
                if ((changedMask & ChangedAngularVelocity) != 0) size += 12;
            }
            if ((changedMask & ChangedSleep)           != 0) size += 1;
            if ((changedMask & ChangedConstraints)     != 0) size += 1;
            return size;
        }

        /// <summary>
        /// Writes a 3-D physics payload into <paramref name="dest"/> at
        /// <paramref name="destOffset"/>.
        /// </summary>
        /// <param name="dest">The buffer to write to, for example one rented from <c>ArrayPool&lt;byte&gt;.Shared</c>.</param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The state to write.</param>
        /// <param name="dataMask">The field bits of the fields to include; other bits are ignored.</param>
        /// <returns>
        /// The number of bytes written (see <see cref="ComputePayloadSize"/>), or
        /// <c>0</c> when a selected position, velocity or angular velocity is NaN
        /// or infinite; nothing is written then.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="destOffset"/> is negative, or <paramref name="dest"/> is
        /// too small for the payload at <paramref name="destOffset"/>.
        /// </exception>
        public static int BuildPayloadInto(byte[] dest, int destOffset, ulong objectId, PhysicsState state, byte dataMask)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int size = ComputePayloadSize(dataMask, twoDee: false);
            if (destOffset < 0 || (long)destOffset + size > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a 3-D physics payload at the given offset.");

            byte changedMask = (byte)((dataMask & DataFieldMask) | TypeMarker3D);
            int off = destOffset;

            // ── Refused, not repaired ─────────────────────────────────────────
            // PhysicsPacketParser drops the whole record when any coordinate it
            // reads is non-finite — including the fields that were fine, because
            // a masked record is refused whole — so an unchecked write here is a
            // frame this SDK builds for its own reader to discard.
            //
            // ⛔ No production path parses one TODAY: no Sync Service consumer
            // ingests rigidbody state and the gateway's PhysicsSync handler
            // checks the framing and drops the frame.  So what the refusal buys
            // right now is the bandwidth and — the part that persists — the
            // caller's `_lastSentState`, which becomes the baseline every
            // threshold is measured against and is false forever once it holds a
            // NaN.  The parser agreement is what keeps that true when the ingest
            // path lands.
            //
            // Rotation is absent from the test on purpose: it is sanitised a few
            // lines down, where a non-unit quaternion has a nearest valid answer
            // and a coordinate does not.
            if (!CoordinatesAreFinite(state, changedMask, out string nonFiniteField))
            {
                ReportNonFiniteCoordinate(objectId, nonFiniteField);
                return 0;
            }

            // ── Header ────────────────────────────────────────────────────────
            WriteU64LE(dest, off, objectId);  off += 8;
            dest[off++] = changedMask;

            // ── Conditional data fields (in bit-order) ────────────────────────
            if ((changedMask & ChangedPosition) != 0)
            {
                WriteF32LE(dest, off, state.Position.x); off += 4;
                WriteF32LE(dest, off, state.Position.y); off += 4;
                WriteF32LE(dest, off, state.Position.z); off += 4;
            }
            if ((changedMask & ChangedRotation) != 0)
            {
                // Held to the same contract as every other raw-quaternion
                // writer: PhysicsPacketParser refuses |q|² outside the band, so
                // an unsanitised write here is a record its own reader drops.
                //
                // Reported on the same terms too.  Discarding the flag here
                // would leave one of the three writers silently repairing what
                // the other two report, and an asymmetry with no reason behind
                // it is one the next reader has to rediscover.
                var rotation = WireQuaternion.ForWire(state.Rotation, out bool rotationSubstituted);
                if (rotationSubstituted && Core.WarnGate.ShouldEmit(ref _lastRotationSubstitutionWarnTicks))
                {
                    Debug.LogWarning(
                        $"[RTMPE] PhysicsPacketBuilder: object {objectId} was given a rotation the " +
                        "protocol does not carry (a zero, non-finite or grossly non-unit " +
                        "quaternion). Sending the nearest valid rotation instead — the original " +
                        "would have been dropped by the receiving parser. `default(Quaternion)` " +
                        "is (0,0,0,0), not identity.");
                }
                WriteF32LE(dest, off, rotation.x); off += 4;
                WriteF32LE(dest, off, rotation.y); off += 4;
                WriteF32LE(dest, off, rotation.z); off += 4;
                WriteF32LE(dest, off, rotation.w); off += 4;
            }
            if ((changedMask & ChangedVelocity) != 0)
            {
                WriteF32LE(dest, off, state.Velocity.x); off += 4;
                WriteF32LE(dest, off, state.Velocity.y); off += 4;
                WriteF32LE(dest, off, state.Velocity.z); off += 4;
            }
            if ((changedMask & ChangedAngularVelocity) != 0)
            {
                WriteF32LE(dest, off, state.AngularVelocity.x); off += 4;
                WriteF32LE(dest, off, state.AngularVelocity.y); off += 4;
                WriteF32LE(dest, off, state.AngularVelocity.z); off += 4;
            }
            if ((changedMask & ChangedSleep) != 0)
            {
                dest[off] = state.IsSleeping ? (byte)0x01 : (byte)0x00;
                off++;
            }
            if ((changedMask & ChangedConstraints) != 0)
            {
                dest[off++] = state.ConstraintMask;
            }

            // No interpolated assert here — the per-tick allocation it
            // would create (string + boxed args) defeats the pooling.  The
            // size/branch-coverage agreement is structurally guaranteed by
            // ComputePayloadSize sharing the same mask switches.
            return off - destOffset;
        }

        // ── 2-D builder ───────────────────────────────────────────────────────

        /// <summary>
        /// Builds a 2-D physics payload in a new array.
        /// </summary>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The state to write.</param>
        /// <param name="dataMask">
        /// The field bits of the fields to include; other bits are ignored.
        /// </param>
        /// <returns>
        /// The payload, or <see langword="null"/> when a selected field is NaN or
        /// infinite.
        /// </returns>
        public static byte[] Build2DPayload(ulong objectId, PhysicsState2D state, byte dataMask)
        {
            int size = ComputePayloadSize(dataMask, twoDee: true);
            var buf = new byte[size];
            int written = Build2DPayloadInto(buf, 0, objectId, state, dataMask);
            return written > 0 ? buf : null;
        }

        /// <summary>
        /// Writes a 2-D physics payload into <paramref name="dest"/> at
        /// <paramref name="destOffset"/>.
        /// </summary>
        /// <param name="dest">The buffer to write to.</param>
        /// <param name="destOffset">Where in <paramref name="dest"/> to start.</param>
        /// <param name="objectId">The <c>NetworkObjectId</c> of the object.</param>
        /// <param name="state">The state to write.</param>
        /// <param name="dataMask">The field bits of the fields to include; other bits are ignored.</param>
        /// <returns>
        /// The number of bytes written (see <see cref="ComputePayloadSize"/>), or
        /// <c>0</c> when a selected field is NaN or infinite; nothing is written
        /// then.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="dest"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="destOffset"/> is negative, or <paramref name="dest"/> is
        /// too small for the payload at <paramref name="destOffset"/>.
        /// </exception>
        public static int Build2DPayloadInto(byte[] dest, int destOffset, ulong objectId, PhysicsState2D state, byte dataMask)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int size = ComputePayloadSize(dataMask, twoDee: true);
            if (destOffset < 0 || (long)destOffset + size > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a 2-D physics payload at the given offset.");

            byte changedMask = (byte)((dataMask & DataFieldMask) | TypeMarker2D);
            int off = destOffset;

            // Same contract as the 3-D encoder, with the rotation INSIDE the
            // test rather than beside it: a 2-D rotation is a scalar angle, so
            // it has no unit-length band to be pulled back into and no
            // `default(...)` trap either — zero is a legal angle — and the only
            // value the parser refuses is a non-finite one, which has no nearest
            // valid angle any more than a coordinate has a nearest valid point.
            if (!CoordinatesAreFinite2D(state, changedMask, out string nonFiniteField))
            {
                ReportNonFiniteCoordinate(objectId, nonFiniteField);
                return 0;
            }

            WriteU64LE(dest, off, objectId); off += 8;
            dest[off++] = changedMask;

            if ((changedMask & ChangedPosition) != 0)
            {
                WriteF32LE(dest, off, state.Position.x); off += 4;
                WriteF32LE(dest, off, state.Position.y); off += 4;
            }
            if ((changedMask & ChangedRotation) != 0)
            {
                WriteF32LE(dest, off, state.Rotation); off += 4;
            }
            if ((changedMask & ChangedVelocity) != 0)
            {
                WriteF32LE(dest, off, state.Velocity.x); off += 4;
                WriteF32LE(dest, off, state.Velocity.y); off += 4;
            }
            if ((changedMask & ChangedAngularVelocity) != 0)
            {
                WriteF32LE(dest, off, state.AngularVelocity); off += 4;
            }
            if ((changedMask & ChangedSleep) != 0)
            {
                dest[off] = state.IsSleeping ? (byte)0x01 : (byte)0x00;
                off++;
            }
            if ((changedMask & ChangedConstraints) != 0)
            {
                dest[off++] = state.ConstraintMask;
            }

            // No interpolated assert here — see BuildPayloadInto for rationale.
            return off - destOffset;
        }

        // ── Wire-coordinate contract ──────────────────────────────────────────

        // Only the fields the mask actually selects are asked about.  A
        // rigidbody whose angular velocity has diverged but whose position has
        // not still sends its position, provided this tick's mask does not carry
        // the angular velocity — the alternative, testing the whole struct, ends
        // updates for a field that is fine because a field that is not is
        // sitting unsent beside it.
        //
        // ⛔ That case is reachable through the PUBLIC encoder, and through the
        // shipped components only for an infinity.  `NetworkRigidbody.
        // BuildChangedMask` compares the live value against the last sent one:
        // a comparison involving a NaN is false, so a field that has JUST become
        // NaN never gets its bit set and reaches this encoder only through
        // `BuildFullMask()`, the first send after spawn — while an infinite
        // position, velocity or angular velocity component makes the distance
        // infinite, which does select the bit.  The scoping is still the right
        // rule, because it is the parser's rule.
        //
        // The offending field is named so the console line points at one of
        // four, rather than at the object.  It is built on the refusal path
        // only, which is gated to one line a second, so the per-tick send path
        // pays nothing for it.
        private static bool CoordinatesAreFinite(PhysicsState state, byte changedMask, out string field)
        {
            if ((changedMask & ChangedPosition) != 0 && !WireVector.IsFinite(state.Position))
            { field = "position"; return false; }
            if ((changedMask & ChangedVelocity) != 0 && !WireVector.IsFinite(state.Velocity))
            { field = "velocity"; return false; }
            if ((changedMask & ChangedAngularVelocity) != 0 && !WireVector.IsFinite(state.AngularVelocity))
            { field = "angular velocity"; return false; }
            field = null;
            return true;
        }

        private static bool CoordinatesAreFinite2D(PhysicsState2D state, byte changedMask, out string field)
        {
            if ((changedMask & ChangedPosition) != 0
                && !WireVector.IsFinite(state.Position.x, state.Position.y))
            { field = "position"; return false; }
            if ((changedMask & ChangedRotation) != 0 && !WireVector.IsFinite(state.Rotation))
            { field = "rotation"; return false; }
            if ((changedMask & ChangedVelocity) != 0
                && !WireVector.IsFinite(state.Velocity.x, state.Velocity.y))
            { field = "velocity"; return false; }
            if ((changedMask & ChangedAngularVelocity) != 0 && !WireVector.IsFinite(state.AngularVelocity))
            { field = "angular velocity"; return false; }
            field = null;
            return true;
        }

        private static void ReportNonFiniteCoordinate(ulong objectId, string field)
        {
            if (!Core.WarnGate.ShouldEmit(ref _lastNonFiniteCoordinateWarnTicks)) return;
            Debug.LogWarning(
                $"[RTMPE] PhysicsPacketBuilder: object {objectId} has a non-finite {field} " +
                "(NaN or Infinity), so no physics update was sent for it. There is no nearest " +
                "valid value to substitute — sending one would move the object somewhere it is " +
                "not for every other player — and a reader of this record refuses it in full, " +
                "including the fields that were fine. This field will not replicate again until " +
                "it is finite. The usual cause is a physics step that diverged, or a force " +
                "applied with a zero or NaN scale.");
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

        // WriteF32LE writes an IEEE 754 single-precision float in little-endian
        // byte order using BitConverter.SingleToInt32Bits for zero-allocation
        // bit reinterpretation (available in .NET Standard 2.1 / Unity 2019.3+).
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
