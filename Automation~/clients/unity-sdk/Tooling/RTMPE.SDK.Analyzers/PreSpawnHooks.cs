using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// The Unity messages that run before a network object has spawned — so a
    /// <c>NetworkVariable</c> read or constructed in one is read or constructed
    /// before ownership is valid and before <c>OnNetworkSpawn</c> has run.
    /// </summary>
    /// <remarks>
    /// 🔑 One declaration, two readers. The pre-spawn reachability closure in the
    /// conversion transform carried seven names and a helper walk; the
    /// <c>RTMPE1011</c> rule that the readiness score's Lifecycle dimension reads
    /// carried three of them, so a construction in <c>OnValidate</c>, <c>Reset</c>
    /// or a serialization callback was refused by the converter and scored clean
    /// by the analyzer. The same question answered by two lists in two projects is
    /// the shape this file ends.
    /// <para>
    /// <c>OnValidate</c> and <c>Reset</c> run in the editor with no session at
    /// all; <c>OnAfterDeserialize</c>/<c>OnBeforeSerialize</c> run while the object
    /// is being deserialised, before <c>Awake</c> and long before any spawn.
    /// </para>
    /// <para>
    /// ⛔ <c>OnDestroy</c> stays out of every reader on purpose: unsubscribing
    /// <c>OnValueChanged</c> there is the idiomatic teardown, and refusing it
    /// would refuse nearly every real conversion — the bound is stated in
    /// <c>diagnostics.md</c> rather than enforced.
    /// </para>
    /// </remarks>
    public static class PreSpawnHooks
    {
        public static readonly ImmutableArray<string> Names = ImmutableArray.Create(
            "Awake", "Start", "OnEnable", "OnValidate", "Reset",
            "OnAfterDeserialize", "OnBeforeSerialize");

        public static bool IsPreSpawnHookName(string name)
            => Names.Contains(name);

        /// <summary>
        /// Whether Unity would dispatch <paramref name="method"/> as one of these
        /// messages: an instance method of that name taking no parameters. A
        /// static or parameterised member wearing the name is no Unity message.
        /// </summary>
        public static bool IsDispatchedAs(IMethodSymbol method)
            => method is not null
                && !method.IsStatic
                && method.Parameters.Length == 0
                && method.TypeParameters.Length == 0
                && Names.Contains(method.Name);
    }
}
