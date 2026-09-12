using System.Text;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// Deterministic mirror of the SDK runtime's wire-identity hash
    /// (<c>RTMPE.Core.WireIdHash</c>). The FNV-1a 32-bit hash of
    /// <c>"scope.member"</c> as a UTF-8 byte stream; the authoring toolchain
    /// must reproduce it bit-for-bit so an id computed at author time equals the
    /// one the runtime computes at first spawn.
    ///
    /// <para>⚠️ TWO subsystems address by this hash and they pass the SAME
    /// scope: the type's <b>fully-qualified metadata</b> name, matching
    /// <c>Type.FullName</c>. An enhanced-RPC method id and a NetworkVariable's
    /// identity both fold it in, because both address a component of an object
    /// the wire does not discriminate. Passing an unqualified name produces an
    /// id the runtime disagrees with, and nothing at author time would say
    /// so.</para>
    /// </summary>
    public static class Fnv1a
    {
        private const uint OffsetBasis = 2166136261u;
        private const uint Prime = 16777619u;

        /// <summary>
        /// Computes the method id for <paramref name="typeName"/> and
        /// <paramref name="methodName"/> joined by a single <c>'.'</c>
        /// separator. Neither argument is normalised, so
        /// <paramref name="typeName"/> must already be the qualified metadata
        /// name the runtime hashes — see the convention on the class.
        /// </summary>
        public static uint ComputeMethodId(string typeName, string methodName)
        {
            // Hashing the UTF-8 bytes of the joined string is identical to the
            // runtime's per-segment fold: '.' is a single ASCII byte, and the
            // UTF-8 encoding of a concatenation is the concatenation of the
            // encodings, so the byte stream is byte-for-byte the same.
            byte[] bytes = Encoding.UTF8.GetBytes(typeName + "." + methodName);

            unchecked
            {
                uint hash = OffsetBasis;
                foreach (byte b in bytes)
                {
                    hash = (hash ^ b) * Prime;
                }

                return hash;
            }
        }
    }
}
