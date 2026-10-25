using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Small syntax-classification predicates shared across the analysis layers.
    /// A predicate lands here once two projects need the same answer to the same
    /// question about a node; RTMPE.SDK.Analysis references this project, so the
    /// one definition is visible to both rather than copied into each.
    /// </summary>
    public static class SyntaxShapes
    {
        /// <summary>
        /// The four mutating unary forms — <c>++x</c>, <c>--x</c>, <c>x++</c>,
        /// <c>x--</c>. A write for authority purposes just as an assignment is, and
        /// read identically by the signal extractor (does a mutator touch a
        /// NetworkVariable) and the graph builder (does a reference mutate the node
        /// it reached), which is why the classification is shared rather than twice
        /// stated.
        /// </summary>
        public static bool IsIncrementOrDecrement(SyntaxNode node)
            => node.IsKind(SyntaxKind.PreIncrementExpression)
                || node.IsKind(SyntaxKind.PreDecrementExpression)
                || node.IsKind(SyntaxKind.PostIncrementExpression)
                || node.IsKind(SyntaxKind.PostDecrementExpression);
    }
}
