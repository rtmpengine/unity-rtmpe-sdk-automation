// RTMPE SDK — Runtime/Core/ObjectIdMath.cs
//
// Pure-arithmetic helpers used by SpawnManager to compose locally-allocated
// 64-bit network object ids.  Kept in its own file (no UnityEngine usage)
// so that xunit-based unit tests can compile-link this exact source without
// pulling in the rest of the SpawnManager surface.  The bit-mixing function
// is the single source of truth for the (session_id, counter) → object_id
// contract — divergence between this file and SpawnManager.GenerateObjectId
// would silently re-introduce the 32-bit truncation collision class the
// helper was created to close.

namespace RTMPE.Core
{
    /// <summary>
    /// Composes and inspects the 64-bit network object ids the SDK allocates.
    /// </summary>
    /// <remarks>
    /// An object id carries a digest of the session id in its high 32 bits and a
    /// per-session counter in its low 32 bits, so each session allocates ids from
    /// its own range. <see cref="SpawnManager"/> allocates ids with these rules;
    /// game code does not need to call them.
    /// </remarks>
    public static class ObjectIdMath
    {
        /// <summary>
        /// Composes an object id: the high 32 bits are the
        /// <see cref="MixSessionId"/> digest of <paramref name="sessionId"/>, the
        /// low 32 bits are the low 32 bits of <paramref name="counter"/>.
        /// </summary>
        /// <remarks>
        /// The digest mixes every byte of the session id, so two sessions whose ids
        /// share their low half still receive different ranges, except for a
        /// 1-in-2^32 digest collision. Stop allocating before the counter leaves
        /// 32 bits; see <see cref="IsCounterExhausted"/>.
        /// </remarks>
        /// <param name="sessionId">The session's 64-bit id.</param>
        /// <param name="counter">The session's allocation counter.</param>
        /// <returns>The composed object id.</returns>
        public static ulong Compose(ulong sessionId, ulong counter)
        {
            ulong digest = MixSessionId(sessionId);
            return (digest << 32) | (counter & 0xFFFFFFFFUL);
        }

        /// <summary>
        /// Whether <paramref name="counter"/> is outside the range the low half of
        /// an object id can carry: <see langword="true"/> for 0 and for any value
        /// above <see cref="uint.MaxValue"/>.
        /// </summary>
        /// <remarks>
        /// An allocator stops issuing ids once this is <see langword="true"/>: a
        /// wrapped counter would repeat an id that may still be in use.
        /// </remarks>
        /// <param name="counter">The allocation counter to test.</param>
        /// <returns><see langword="true"/> when no further id can be composed from the counter.</returns>
        public static bool IsCounterExhausted(ulong counter)
        {
            return counter == 0UL || counter > uint.MaxValue;
        }

        /// <summary>
        /// Whether <paramref name="objectId"/> lies in the id range of
        /// <paramref name="sessionId"/>.
        /// </summary>
        /// <remarks>
        /// Only the high 32 bits are compared with the session's digest; the
        /// counter half is not checked.
        /// </remarks>
        /// <param name="objectId">The object id to test.</param>
        /// <param name="sessionId">The session whose range is asked about.</param>
        /// <returns><see langword="true"/> when the id's high 32 bits equal the session's digest.</returns>
        public static bool BelongsToSession(ulong objectId, ulong sessionId)
            => (uint)(objectId >> 32) == MixSessionId(sessionId);

        /// <summary>
        /// Mixes a 64-bit session id down to the 32-bit digest that forms the high
        /// half of the session's object ids.
        /// </summary>
        /// <remarks>
        /// The id is folded to 32 bits and passed through a SplitMix64 finaliser.
        /// The result is deterministic and allocates nothing.
        /// </remarks>
        /// <param name="sessionId">The session's 64-bit id.</param>
        /// <returns>The 32-bit digest.</returns>
        public static uint MixSessionId(ulong sessionId)
        {
            ulong z = sessionId;
            z ^= z >> 32;                       // fold high half into low half
            z ^= z >> 30; z *= 0xBF58476D1CE4E5B9UL;
            z ^= z >> 27; z *= 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (uint)(z ^ (z >> 32));
        }
    }
}
