using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// The per-frame messages Unity drives on a behaviour, and the only methods
    /// an owner guard belongs in.
    /// </summary>
    /// <remarks>
    /// 🔑 Stated once, because the rule reading it is asked in four places — the
    /// <c>RTMPE2003</c> analyzer, the readiness score's Ownership dimension, the
    /// headless fix host and the IDE fix — and a copy that names one loop makes
    /// the others silent about the rest. Naming only <c>Update</c> is the
    /// specific way that went wrong: physics-driven movement belongs in
    /// <c>FixedUpdate</c> and follow cameras in <c>LateUpdate</c>, so a type that
    /// drives its owned state from either was scored as having no loop to guard
    /// — the arm that AWARDS the weight, on exactly the projects the rule exists
    /// for. The SDK's own <c>NetworkRigidbody</c> is that shape.
    /// <para>
    /// ⛔ Only the three loops Unity calls every frame. <c>Awake</c>,
    /// <c>Start</c> and the collision messages run for their own reasons, and
    /// fencing them off from non-owners would suppress setup a replica needs.
    /// </para>
    /// </remarks>
    public static class UnityFrameLoops
    {
        /// <summary>
        /// The loop names in the order Unity drives them within a frame, which
        /// is the order a report over several of them reads in.
        /// </summary>
        public static readonly ImmutableArray<string> Names =
            ImmutableArray.Create("FixedUpdate", "Update", "LateUpdate");

        /// <summary>True when the name is one of the three.</summary>
        public static bool IsFrameLoopName(string name)
        {
            if (name is null)
            {
                return false;
            }

            foreach (string loop in Names)
            {
                if (string.Equals(loop, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when Unity will actually drive this method: one of the three
        /// names, and the signature the engine dispatches — an instance method
        /// returning void, taking nothing and declaring no type parameter. A
        /// member wearing the name in any other shape is never called, and it
        /// cannot reference the inherited <c>IsOwner</c> either.
        /// </summary>
        public static bool IsDrivenBy(IMethodSymbol method)
            => method is not null
                && method.MethodKind == MethodKind.Ordinary
                && !method.IsStatic
                && method.ReturnsVoid
                && method.Parameters.Length == 0
                && method.TypeParameters.Length == 0
                && IsFrameLoopName(method.Name);

        /// <summary>
        /// A declaration worth considering as a frame loop, for hosts that read
        /// one file and have no semantic model.
        /// </summary>
        /// <remarks>
        /// ⛔ Deliberately looser than <see cref="IsDrivenBy(IMethodSymbol)"/>,
        /// and named apart from it so the difference is a decision rather than a
        /// disagreement. A `static` or value-returning member wearing the name
        /// is selected here so the transform can refuse it **by name and with a
        /// reason** — "the declaration is static or value-returning, it is not
        /// the Unity message" — where excluding it would leave the operator with
        /// "this type declares no frame loop", which is not what they are looking
        /// at. What is excluded is what no message could help with: a
        /// parameterised or generic declaration, which is an ordinary helper that
        /// happens to share a name.
        /// </remarks>
        public static bool IsFrameLoopCandidate(MethodDeclarationSyntax declaration)
            => declaration is not null
                && declaration.ParameterList.Parameters.Count == 0
                && declaration.TypeParameterList is null
                && IsFrameLoopName(declaration.Identifier.ValueText);
    }
}
