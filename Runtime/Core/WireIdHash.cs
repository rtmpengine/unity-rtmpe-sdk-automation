// RTMPE SDK — Runtime/Core/WireIdHash.cs
//
// The one construction that turns a qualified declaration name into a wire
// identity: FNV-1a 32-bit over the UTF-8 bytes of "scope.member".
//
// Two subsystems address by such an identity — RPC methods and NetworkVariable
// members — and both dispatch on an object that carries no component
// discriminator, so both need the scope folded in rather than appended by the
// caller.  They share this function instead of holding a copy each: a hash is a
// contract with every peer that ever computed one, and two copies of a contract
// drift in exactly the way a copy of a constant does not — silently, and only
// for inputs neither copy's tests reach.
//
// Allocation-free by construction.  It is called once per RPC send and once per
// variable construction, which on a pooled prefab is once per re-acquire, so a
// "scope" + '.' + "member" concatenation would be a per-spawn allocation for
// every networked member in the build.  FNV-1a is defined left-to-right over a
// byte sequence, so the two strings and the separator fold in sequence and the
// result is identical to hashing the concatenation.
//
// ⚠️ The UTF-8 folding is not decoration.  A C# identifier is ASCII, so every
// caller today stays in the one-byte branch — but the scope a caller passes is a
// type name, and a type name reaches this function from `ScopeOf`, which carries
// whatever the source declared.  A branch that handled ASCII alone would hash
// two distinct non-ASCII names to whatever their low bytes happened to be.

using System;

namespace RTMPE.Core
{
    internal static class WireIdHash
    {
        internal const uint OffsetBasis = 2166136261u;
        internal const uint Prime       = 16777619u;

        /// <summary>
        /// The wire identity of <paramref name="member"/> declared in
        /// <paramref name="scope"/> — the FNV-1a 32-bit hash of the UTF-8 bytes
        /// of <c>"scope.member"</c>.
        /// </summary>
        /// <remarks>
        /// A null part folds as an empty one rather than throwing.  The callers
        /// that must reject a missing name reject it for their own reasons and
        /// say so in their own words; a hash that throws would make this
        /// function the place those messages come from, and it knows nothing
        /// about what it is naming.
        /// </remarks>
        internal static uint Of(string scope, string member)
        {
            uint hash = OffsetBasis;
            hash = Fold(hash, scope);
            hash = (hash ^ (byte)'.') * Prime;
            hash = Fold(hash, member);
            return hash;
        }

        /// <summary>
        /// The scope name of <paramref name="type"/> — its metadata full name:
        /// namespaces joined by <c>.</c>, containing types by <c>+</c>, and each
        /// generic arity written as a <c>`n</c> suffix.
        /// </summary>
        /// <remarks>
        /// The conversion tooling derives this same spelling from a declaration,
        /// so an identity recorded beside the source and the one this build
        /// computes are one number.  Two properties of <see cref="Type.FullName"/>
        /// are answered here rather than at each call site:
        /// <list type="bullet">
        /// <item><description>a constructed generic renders its type arguments
        /// assembly-qualified, which is a spelling no declaration has — so the
        /// generic definition supplies the name.  Two instantiations of one type
        /// therefore share a scope.  For a variable that is refused where the ids
        /// meet, at registration; for an RPC the frame still reaches the anchor
        /// when the anchor owns the id, and only two NON-anchor owners are
        /// refused.</description></item>
        /// <item><description>a type built from a type parameter has no full name
        /// at all.  It is never the runtime type of an instance, so no call site
        /// reaches it; the simple name keeps the result deterministic instead of
        /// handing every such type the empty scope.</description></item>
        /// </list>
        /// <para>⛔ One bound, stated rather than handled: an array, pointer or
        /// by-ref type BUILT OVER a constructed generic is not itself a generic
        /// type, so it keeps the assembly-qualified rendering.  No declaration
        /// spells that, so no recorded identity can match it — and nothing can
        /// reach it either, because every call site here passes the runtime type
        /// of a live component.</para>
        /// </remarks>
        internal static string ScopeOf(Type type)
        {
            if (type == null) return null;
            if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
            return type.FullName ?? type.Name;
        }

        /// <summary>
        /// Fold <paramref name="s"/>'s UTF-8 byte sequence into
        /// <paramref name="hash"/> without materialising the bytes.
        /// </summary>
        /// <remarks>
        /// Byte-for-byte identical to folding <c>Encoding.UTF8.GetBytes(s)</c>,
        /// including the replacement character a lone surrogate encodes to —
        /// which is what lets the result be described as a hash of the name's
        /// UTF-8 form rather than of this implementation.
        /// </remarks>
        internal static uint Fold(uint hash, string s)
        {
            if (s == null) return hash;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80)
                {
                    hash = (hash ^ c) * Prime;
                }
                else if (c < 0x800)
                {
                    hash = (hash ^ (uint)(0xC0 | (c >> 6))) * Prime;
                    hash = (hash ^ (uint)(0x80 | (c & 0x3F))) * Prime;
                }
                else if (!char.IsSurrogate(c))
                {
                    hash = (hash ^ (uint)(0xE0 | (c >> 12))) * Prime;
                    hash = (hash ^ (uint)(0x80 | ((c >> 6) & 0x3F))) * Prime;
                    hash = (hash ^ (uint)(0x80 | (c & 0x3F))) * Prime;
                }
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                    hash = (hash ^ (uint)(0xF0 | (cp >> 18))) * Prime;
                    hash = (hash ^ (uint)(0x80 | ((cp >> 12) & 0x3F))) * Prime;
                    hash = (hash ^ (uint)(0x80 | ((cp >> 6) & 0x3F))) * Prime;
                    hash = (hash ^ (uint)(0x80 | (cp & 0x3F))) * Prime;
                }
                else
                {
                    // A lone surrogate is not encodable, and Encoding.UTF8's
                    // fallback substitutes U+FFFD rather than refusing.  Matched
                    // here so a name that survives round-tripping through UTF-8
                    // hashes to what its round-tripped form would.
                    hash = (hash ^ 0xEFu) * Prime;
                    hash = (hash ^ 0xBFu) * Prime;
                    hash = (hash ^ 0xBDu) * Prime;
                }
            }

            return hash;
        }
    }
}
