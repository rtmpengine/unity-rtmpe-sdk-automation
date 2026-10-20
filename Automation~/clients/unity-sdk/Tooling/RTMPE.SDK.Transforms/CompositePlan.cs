using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>One type's conversion inside a composite, named so it can be
    /// re-resolved against the tree the previous conversion produced.</summary>
    public sealed class CompositeConversion
    {
        public CompositeConversion(string typeName, ConversionPlan plan)
        {
            TypeName = typeName;
            Plan = plan;
        }

        /// <summary>
        /// The type's name as the host spells it. The composite never interprets
        /// it — it hands it back to the host's own resolver, so a batch and a
        /// single conversion can never disagree about what a name means.
        /// </summary>
        public string TypeName { get; }

        public ConversionPlan Plan { get; }
    }

    /// <summary>
    /// Several types converted in one compilation unit, as one decision.
    ///
    /// 🔑 The composition is SEQUENTIAL, not a merge — measured in Task 5.0.
    /// `NetworkVariableGenerationTransform` returns a whole new tree, and two
    /// conversions of two types in one file do not contend: the shared
    /// `using RTMPE.Sync;` is added only when it is not already in scope, and
    /// `OnNetworkSpawn` is a member of the target type, never of the file. So
    /// each plan is applied to the tree its predecessor produced.
    ///
    /// ⚠️ Which is the whole reason this type exists rather than a `foreach` at
    /// the call site: after the first conversion, every type declaration the
    /// caller resolved from its original parse belongs to a superseded tree, and
    /// applying a plan against one of those changes nothing while reporting
    /// success. Re-resolution is therefore performed HERE, once, for every entry
    /// — including the first, so the rule has no special case to forget.
    ///
    /// All-or-nothing: any refusal returns the ORIGINAL root, never the partial
    /// rewrite that produced it. A caller cannot write half a batch because it
    /// never holds half a batch.
    /// </summary>
    public sealed class CompositePlan
    {
        public CompositePlan(IReadOnlyList<CompositeConversion> conversions)
        {
            Conversions = conversions;
        }

        public IReadOnlyList<CompositeConversion> Conversions { get; }

        /// <summary>
        /// Applies every conversion in order. <paramref name="resolveType"/> is
        /// the host's own type lookup, applied to the CURRENT tree.
        /// <paramref name="refusedType"/> names the entry that stopped the batch,
        /// so the message a developer reads says which of their selections failed
        /// rather than that "the batch" did.
        /// </summary>
        public static CompilationUnitSyntax Apply(
            CompilationUnitSyntax root,
            CompositePlan plan,
            Func<CompilationUnitSyntax, string, ClassDeclarationSyntax> resolveType,
            out string refusalReason,
            out string refusedType)
        {
            refusalReason = null;
            refusedType = null;

            if (root is null || plan is null || plan.Conversions is null || plan.Conversions.Count == 0)
            {
                return root;
            }

            if (resolveType is null)
            {
                throw new ArgumentNullException(nameof(resolveType));
            }

            // 🔑 Two entries for one type is not a composition, it is a split
            // plan — and each half would have been allocated against the same
            // pre-run ledger, so both would carry the same ids. The transform
            // could not detect that (each half is internally consistent), and
            // the file would compile. Refused here, where the whole set is
            // visible for the only time.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var conversion in plan.Conversions)
            {
                if (conversion is null || string.IsNullOrEmpty(conversion.TypeName))
                {
                    refusalReason = "a conversion in the batch names no type";
                    return root;
                }

                if (!seen.Add(conversion.TypeName))
                {
                    refusedType = conversion.TypeName;
                    refusalReason = "'" + conversion.TypeName + "' appears more than once in one batch"
                        + " — every member of a type is converted by a single plan, or the two halves"
                        + " allocate the same ids against the same ledger";
                    return root;
                }
            }

            var current = root;
            foreach (var conversion in plan.Conversions)
            {
                var target = resolveType(current, conversion.TypeName);
                if (target is null)
                {
                    refusedType = conversion.TypeName;
                    refusalReason = "type '" + conversion.TypeName + "' was not found in the tree being rewritten";
                    return root;
                }

                var next = NetworkVariableGenerationTransform.Apply(
                    current, target, conversion.Plan, out string reason);
                if (reason != null)
                {
                    refusedType = conversion.TypeName;
                    refusalReason = reason;
                    return root;
                }

                current = next;
            }

            return current;
        }
    }
}
