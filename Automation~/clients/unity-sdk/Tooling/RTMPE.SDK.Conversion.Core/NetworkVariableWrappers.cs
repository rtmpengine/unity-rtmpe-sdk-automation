using System;
using System.Collections.Generic;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The replicated-variable types the runtime ships, by the names a source
    /// file spells them with — the closed set a syntactic host may read a
    /// construction against.
    /// </summary>
    /// <remarks>
    /// 🔑 An explicit set, because the host that reads it has no semantic model
    /// and the kit ships without the runtime to derive it from. It is held equal
    /// to the runtime's own declarations by a test that reads
    /// <c>Runtime/Sync</c>, so a wrapper added there is a red suite here rather
    /// than a construction the ledger silently omits.
    /// <para>
    /// 🚨 The rule this replaces was <c>StartsWith("NetworkVariable")</c>, a
    /// prefix: a user type named <c>NetworkVariableRegistry</c> had an identity
    /// minted for it, and the only spelling that mattered — a target-typed
    /// <c>new(this, nameof(_hp))</c>, legal at the declared Unity floor — was not
    /// an <c>ObjectCreationExpression</c> at all and left the ledger.
    /// </para>
    /// </remarks>
    public static class NetworkVariableWrappers
    {
        /// <summary>The sealed wrappers, one per replicated value type.</summary>
        public static readonly IReadOnlyList<string> SealedNames = new[]
        {
            "NetworkVariableBool",
            "NetworkVariableFloat",
            "NetworkVariableInt",
            "NetworkVariableQuaternion",
            "NetworkVariableString",
            "NetworkVariableVector2",
            "NetworkVariableVector2Int",
            "NetworkVariableVector3",
            "NetworkVariableListFloat",
            "NetworkVariableListInt",
            "NetworkVariableListString",
            "NetworkVariableListVector3",
        };

        /// <summary>
        /// The generic bases a source may construct directly —
        /// <c>NetworkVariable&lt;T&gt;</c> and <c>NetworkVariableList&lt;T&gt;</c> —
        /// by their identifier alone, arity stripped.
        /// </summary>
        public static readonly IReadOnlyList<string> GenericNames = new[]
        {
            "NetworkVariable",
            "NetworkVariableList",
        };

        private static readonly HashSet<string> All = new HashSet<string>(StringComparer.Ordinal);

        static NetworkVariableWrappers()
        {
            foreach (string name in SealedNames) All.Add(name);
            foreach (string name in GenericNames) All.Add(name);
        }

        /// <summary>
        /// Whether <paramref name="typeName"/> — a simple identifier, arity and
        /// qualifier already stripped — names a runtime wrapper.
        /// </summary>
        public static bool IsWrapperTypeName(string typeName)
            => typeName is not null && All.Contains(typeName);
    }
}
