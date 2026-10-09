// RTMPE SDK — Runtime/Infrastructure/Serialization/SafeFlatBufferAccessors.cs
//
// Hardened accessors layered on top of the vendored Google.FlatBuffers
// runtime. The vendored ByteBuffer / Table read APIs were designed for
// trusted producers and silently accept several adversarial inputs:
//
//  • Encoding.UTF8.GetString substitutes U+FFFD for malformed sequences
//    instead of failing closed.
//  • GetFloat / GetDouble reinterpret raw 4 / 8 bytes with no IsFinite
//    check, allowing NaN / +Inf / -Inf to leak into Unity transforms,
//    physics, or interpolators.
//  • A byte that is not a valid ValueType enum value still casts cleanly
//    and reaches the dispatch switch.
//
// This file centralises the validating versions used by VerifiedFlatBuffer
// after the structural verifier has run. Throwing here is intentional — but
// WHERE the throw lands depends on who is reading, and an earlier version of
// this paragraph got that wrong in a way that matters to anyone building on
// the published API:
//
//  • Called from VerifiedFlatBuffer's own semantic pass, a throw is caught
//    inside TryGetRoot and becomes a rate-limited log plus a packet drop.
//
//  • Called from a CALLER's own field read, it is not. FlatBuffers decodes
//    lazily, so every accessor on a returned root runs in the caller's frame
//    after TryGetRoot has returned, outside every catch in that file. The
//    same is true of the vendored reader's own bounds — Table.MaxStringBytes
//    (4096) refuses an oversized string by throwing, right there.
//
// VerifiedFlatBuffer.TryReadField is the catch site for the second case.

using System;
using System.Text;
using Google.FlatBuffers;
using RTMPE.States;
// RTMPE.States.ValueType collides with System.ValueType; alias to the
// schema enum so unqualified references below resolve unambiguously.
using ValueType = RTMPE.States.ValueType;

namespace RTMPE.Infrastructure.Serialization
{
    /// <summary>
    /// Validating accessors for values read from received FlatBuffers:
    /// strict UTF-8, finite floating-point numbers and defined enum values.
    /// The reading methods throw on invalid input, so the caller can drop the
    /// packet from a single catch block; <c>IsFinite</c> and <c>IsValid</c>
    /// only test a value.
    /// </summary>
    public static class SafeFlatBufferAccessors
    {
        /// <summary>
        /// The most elements a received payload may carry across its vectors
        /// (4096). <see cref="VerifiedFlatBuffer.TryGetRoot{TRoot}"/> rejects a
        /// payload that exceeds it, before any per-element objects are
        /// created.
        /// </summary>
        public const int MaxTotalVectorElements = 4096;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Decodes <paramref name="length"/> bytes of <paramref name="bytes"/>,
        /// starting at <paramref name="offset"/>, as strictly validated UTF-8.
        /// </summary>
        /// <param name="bytes">The source buffer.</param>
        /// <param name="offset">Index of the first byte to decode.</param>
        /// <param name="length">Number of bytes to decode.</param>
        /// <returns>The decoded string.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The range lies outside <paramref name="bytes"/>.</exception>
        /// <exception cref="System.Text.DecoderFallbackException">The bytes are not valid UTF-8.</exception>
        /// <exception cref="InvalidOperationException">The bytes contain a NUL character.</exception>
        public static string DecodeStrictUtf8(byte[] bytes, int offset, int length)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || length < 0 || offset > bytes.Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            for (int i = 0; i < length; i++)
            {
                if (bytes[offset + i] == 0)
                {
                    throw new InvalidOperationException("string contains embedded NUL");
                }
            }
            return StrictUtf8.GetString(bytes, offset, length);
        }

        /// <summary>
        /// Reads a 32-bit little-endian float from <paramref name="bb"/> at
        /// <paramref name="offset"/>, refusing a value that is not finite. Use
        /// it for values that will reach transforms, physics or interpolation.
        /// </summary>
        /// <param name="bb">The buffer.</param>
        /// <param name="offset">Byte offset of the value.</param>
        /// <returns>The value.</returns>
        /// <exception cref="InvalidOperationException">The value is NaN or infinite.</exception>
        public static float SafeGetFloat(ByteBuffer bb, int offset)
        {
            float v = bb.GetFloat(offset);
            if (!IsFinite(v))
            {
                throw new InvalidOperationException("float field is not finite");
            }
            return v;
        }

        /// <summary>
        /// Reads a 64-bit little-endian double from <paramref name="bb"/> at
        /// <paramref name="offset"/>, refusing a value that is not finite.
        /// </summary>
        /// <param name="bb">The buffer.</param>
        /// <param name="offset">Byte offset of the value.</param>
        /// <returns>The value.</returns>
        /// <exception cref="InvalidOperationException">The value is NaN or infinite.</exception>
        public static double SafeGetDouble(ByteBuffer bb, int offset)
        {
            double v = bb.GetDouble(offset);
            if (!IsFinite(v))
            {
                throw new InvalidOperationException("double field is not finite");
            }
            return v;
        }

        /// <summary>
        /// True when <paramref name="value"/> is neither NaN nor infinite.
        /// </summary>
        public static bool IsFinite(float value)
        {
            // NaN compared to itself is false; ±Inf has the exponent saturated.
            // Combining both guards covers the full non-finite set.
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// True when <paramref name="value"/> is neither NaN nor infinite.
        /// </summary>
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// True when <paramref name="valueType"/> is a defined member of the
        /// <see cref="ValueType"/> enum.
        /// </summary>
        public static bool IsValid(ValueType valueType)
        {
            switch (valueType)
            {
                case ValueType.Bool:
                case ValueType.Int32:
                case ValueType.Int64:
                case ValueType.Float32:
                case ValueType.Float64:
                case ValueType.String:
                case ValueType.Bytes:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns <paramref name="valueType"/> when it is a defined member of
        /// the <see cref="ValueType"/> enum, so code that switches on it never
        /// sees an undefined value.
        /// </summary>
        /// <exception cref="InvalidOperationException"><paramref name="valueType"/> is not a defined member.</exception>
        public static ValueType RequireValid(ValueType valueType)
        {
            if (!IsValid(valueType))
            {
                throw new InvalidOperationException(
                    "ValueType tag out of defined range: " + (byte)valueType);
            }
            return valueType;
        }
    }

    /// <summary>
    /// Extension methods that read fields of generated FlatBuffers types with
    /// the same validation as <see cref="SafeFlatBufferAccessors"/>.
    /// </summary>
    public static class FlatBufferSafeExtensions
    {
        /// <summary>
        /// Returns <see cref="NetworkVariableUpdate.ValueType"/> after checking
        /// that it is a defined value.
        /// </summary>
        /// <exception cref="InvalidOperationException">The value is not a defined member of the enum.</exception>
        public static ValueType SafeValueType(this NetworkVariableUpdate update)
        {
            return SafeFlatBufferAccessors.RequireValid(update.ValueType);
        }

        /// <summary>
        /// Returns <see cref="InputPayload.MoveX"/> after checking that it is
        /// finite.
        /// </summary>
        /// <exception cref="InvalidOperationException">The value is NaN or infinite.</exception>
        public static float SafeMoveX(this InputPayload payload)
        {
            float v = payload.MoveX;
            if (!SafeFlatBufferAccessors.IsFinite(v))
            {
                throw new InvalidOperationException("InputPayload.MoveX is not finite");
            }
            return v;
        }

        /// <summary>
        /// Returns <see cref="InputPayload.MoveY"/> after checking that it is
        /// finite.
        /// </summary>
        /// <exception cref="InvalidOperationException">The value is NaN or infinite.</exception>
        public static float SafeMoveY(this InputPayload payload)
        {
            float v = payload.MoveY;
            if (!SafeFlatBufferAccessors.IsFinite(v))
            {
                throw new InvalidOperationException("InputPayload.MoveY is not finite");
            }
            return v;
        }
    }
}
