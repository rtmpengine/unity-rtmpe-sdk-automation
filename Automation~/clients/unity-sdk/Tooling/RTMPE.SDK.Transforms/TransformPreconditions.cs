using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// The precondition every whole-tree transform in this assembly shares: the
    /// type it was asked to rewrite must be a node of the tree it was handed.
    ///
    /// 🔑 Measured, not assumed (Task 5.0). Roslyn's <c>ReplaceNode</c> matches
    /// by node identity: given a target from a DIFFERENT tree it finds nothing,
    /// throws nothing, and returns the tree unchanged. The transform would then
    /// report success having converted nothing — and the import step, which reads
    /// the stale node's ancestors rather than the tree's, would add a second
    /// <c>using</c> the file already has. A run that writes an unconverted file
    /// and exits 0 is exactly the silent partial application this toolchain
    /// exists to avoid.
    ///
    /// ⚠️ The natural way to compose two conversions over one file produces
    /// precisely that: resolve both type declarations from one parse, then apply
    /// one plan after the other. The second target belongs to the tree the first
    /// plan replaced. So the rule lives HERE, in the transform, rather than as a
    /// convention every caller is trusted to keep.
    /// </summary>
    internal static class TransformPreconditions
    {
        internal const string ForeignTargetRefusal =
            "the type does not belong to the tree being rewritten — a conversion applied after"
            + " another must re-resolve its type from the tree the previous one produced,"
            + " because replacing a node of a superseded tree changes nothing and reports nothing";

        internal static bool TargetBelongsToRoot(CompilationUnitSyntax root, SyntaxNode target)
            => root != null && target != null && root.Contains(target);
    }
}
