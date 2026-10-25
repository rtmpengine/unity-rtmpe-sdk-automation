// RTMPE SDK — Runtime/Core/GameplaySequencePrefix.cs
//
// Helpers for the 4-byte monotonic gameplay-sequence prefix that gameplay
// packets carry when NetworkSettings.enableGameplayOrdering is true.  The
// flag bit PacketFlags.GameplayOrdered is set on the packet header so the
// receiver can dispatch on the prefix without pre-coordinating with the
// payload type.
//
// Layout when present (always at byte 0 of the application payload, BEFORE
// any per-packet-type fields):
//
//   [0..3]  gameplay_sequence : u32 LE   — strictly monotonic, wraparound
//                                          handled by RFC 1982 modular
//                                          comparison in
//                                          GameplayOrderingBuffer.
//
// Allocation discipline: TryStrip never copies; it returns offsets into the
// caller's buffer.  Wrap allocates a new array (the only zero-copy way to
// prepend bytes to an externally-owned payload is unsafe pointer juggling
// or pinned arrays — neither is appropriate at this layer).

using System.Threading;

namespace RTMPE.Core
{
    /// <summary>
    /// Adds and reads the 4-byte gameplay sequence number at the start of a
    /// payload whose packet has <see cref="PacketFlags.GameplayOrdered"/> set.
    /// </summary>
    /// <remarks>
    /// The sequence number is little-endian. The SDK's send path numbers
    /// gameplay-ordered packets with its own counter (see
    /// <see cref="NetworkSettings.EmitGameplaySequencePrefix"/>); these helpers
    /// are for code that builds or reads such payloads itself.
    /// </remarks>
    public static class GameplaySequencePrefix
    {
        /// <summary>The size of the prefix in bytes.</summary>
        public const int PrefixSize = 4;

        // Process-wide monotonic counter.  Interlocked-incremented per outbound
        // gameplay packet so multiple sender threads cannot collide; cast to
        // uint at write time so wraparound matches RFC 1982 semantics.
        private static int _counter;

        /// <summary>
        /// Returns the next value of a process-wide sequence counter. Thread-safe.
        /// </summary>
        /// <remarks>
        /// The first call returns 1. After <see cref="uint.MaxValue"/> the counter
        /// wraps to 0.
        /// </remarks>
        public static uint NextSequence()
        {
            return unchecked((uint)Interlocked.Increment(ref _counter));
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Resets the counter to 0. Available only when <c>UNITY_INCLUDE_TESTS</c>
        /// is defined; not intended to be called from game code.
        /// </summary>
        public static void ResetForTest()
        {
            Interlocked.Exchange(ref _counter, 0);
        }
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Returns a new array holding <paramref name="sequence"/> followed by
        /// <paramref name="payload"/>.
        /// </summary>
        /// <remarks>
        /// Set <see cref="PacketFlags.GameplayOrdered"/> on the packet that
        /// carries the result.
        /// </remarks>
        /// <param name="sequence">The sequence number, written little-endian.</param>
        /// <param name="payload">The payload to follow it; <see langword="null"/> is treated as empty.</param>
        /// <returns>A new array, <see cref="PrefixSize"/> bytes longer than <paramref name="payload"/>.</returns>
        public static byte[] Wrap(uint sequence, byte[] payload)
        {
            int payloadLen = payload != null ? payload.Length : 0;
            var wrapped = new byte[PrefixSize + payloadLen];
            wrapped[0] = (byte)(sequence);
            wrapped[1] = (byte)(sequence >> 8);
            wrapped[2] = (byte)(sequence >> 16);
            wrapped[3] = (byte)(sequence >> 24);
            if (payloadLen > 0)
                System.Buffer.BlockCopy(payload, 0, wrapped, PrefixSize, payloadLen);
            return wrapped;
        }

        /// <summary>
        /// Reads the sequence number at the start of <paramref name="payload"/>
        /// and locates the bytes after it, without copying.
        /// </summary>
        /// <param name="payload">A payload that starts with the prefix.</param>
        /// <param name="sequence">The sequence number, or 0 on failure.</param>
        /// <param name="innerOffset">Where the bytes after the prefix start in <paramref name="payload"/>.</param>
        /// <param name="innerLength">How many bytes follow the prefix.</param>
        /// <returns>
        /// <see langword="true"/> when the prefix was read; <see langword="false"/>
        /// when <paramref name="payload"/> is <see langword="null"/> or shorter than
        /// <see cref="PrefixSize"/>.
        /// </returns>
        public static bool TryStrip(byte[] payload, out uint sequence, out int innerOffset, out int innerLength)
        {
            sequence    = 0;
            innerOffset = 0;
            innerLength = 0;
            if (payload == null || payload.Length < PrefixSize) return false;

            sequence =
                  (uint)payload[0]
                | ((uint)payload[1] << 8)
                | ((uint)payload[2] << 16)
                | ((uint)payload[3] << 24);
            innerOffset = PrefixSize;
            innerLength = payload.Length - PrefixSize;
            return true;
        }
    }
}
