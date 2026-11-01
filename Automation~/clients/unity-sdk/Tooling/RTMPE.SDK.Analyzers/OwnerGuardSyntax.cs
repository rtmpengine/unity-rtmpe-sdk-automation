using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Recognises the SDK's canonical owner guard — an <c>Update</c> loop's leading
    /// <c>if (!IsOwner) return;</c> that stops a non-owner from driving simulation —
    /// in every spelling the runtime treats as equivalent. Shared so the readiness
    /// score and the conversion diagnostics judge ownership by one definition and
    /// cannot drift apart.
    /// </summary>
    public static class OwnerGuardSyntax
    {
        /// <summary>
        /// True when <paramref name="guard"/> is a leading owner guard — one whose
        /// non-owner branch always exits the method — resolving <c>IsOwner</c>
        /// against <paramref name="isOwner"/> so a same-named member on another type
        /// is not mistaken for it. <c>IsOwner</c> may stand alone or appear as any
        /// operand of a top-level <c>||</c> chain: <c>if (!IsOwner || _body == null) return;</c>
        /// exits for every non-owner exactly as the single-condition form does, and
        /// the extra disjuncts only widen the set of frames that return early.
        /// </summary>
        public static bool IsOwnerGuard(IfStatementSyntax guard, SemanticModel model, IPropertySymbol isOwner)
            => AlwaysExits(guard.Statement)
                && AnyDisjunctNegatesIsOwner(Unparenthesize(guard.Condition), model, isOwner);

        private static bool AnyDisjunctNegatesIsOwner(
            ExpressionSyntax condition, SemanticModel model, IPropertySymbol isOwner)
        {
            if (condition is BinaryExpressionSyntax disjunction
                && disjunction.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return AnyDisjunctNegatesIsOwner(Unparenthesize(disjunction.Left), model, isOwner)
                    || AnyDisjunctNegatesIsOwner(Unparenthesize(disjunction.Right), model, isOwner);
            }

            return NegatesIsOwner(condition, model, isOwner);
        }

        // The guard's condition holds for a non-owner, in any of its natural
        // spellings: `!IsOwner`, `IsOwner == false`, or `IsOwner != true` (either
        // operand order). The owner-true forms (`IsOwner == true`, `IsOwner != false`)
        // return for the owner instead and are correctly not treated as the guard.
        private static bool NegatesIsOwner(ExpressionSyntax condition, SemanticModel model, IPropertySymbol isOwner)
        {
            switch (condition)
            {
                case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    return BindsToIsOwner(not.Operand, model, isOwner);
                case BinaryExpressionSyntax equals when equals.IsKind(SyntaxKind.EqualsExpression):
                    return ComparesIsOwnerToLiteral(equals, SyntaxKind.FalseLiteralExpression, model, isOwner);
                case BinaryExpressionSyntax notEquals when notEquals.IsKind(SyntaxKind.NotEqualsExpression):
                    return ComparesIsOwnerToLiteral(notEquals, SyntaxKind.TrueLiteralExpression, model, isOwner);
                default:
                    return false;
            }
        }

        private static bool ComparesIsOwnerToLiteral(
            BinaryExpressionSyntax comparison, SyntaxKind literal, SemanticModel model, IPropertySymbol isOwner)
        {
            var left = Unparenthesize(comparison.Left);
            var right = Unparenthesize(comparison.Right);
            return (BindsToIsOwner(left, model, isOwner) && right.IsKind(literal))
                || (BindsToIsOwner(right, model, isOwner) && left.IsKind(literal));
        }

        private static bool BindsToIsOwner(ExpressionSyntax expression, SemanticModel model, IPropertySymbol isOwner)
            => model.GetSymbolInfo(Unparenthesize(expression)).Symbol is IPropertySymbol property
                && SymbolEqualityComparer.Default.Equals(property, isOwner);

        // The branch exits, in any spelling. A block qualifies on an
        // unconditional exit anywhere in its own statement list, not only as its
        // first statement: control cannot fall out of a block past one, so a
        // branch that does non-owner-side work before leaving — the common
        // `if (!IsOwner) { Interpolate(); return; }` — fences the rest of the method
        // exactly as the bare form does. An exit nested inside a further
        // statement is conditional and does not qualify.
        //
        // 🚨 `throw` fences as completely as `return`, and reading only `return`
        // scored a throwing guard identically to NO GUARD — measured: two classes
        // differing only in that keyword drew RTMPE2003 on the correct one, and
        // the readiness artifact this feeds published 100 % against 80 % with a
        // remediation line telling the author to add the guard they had written.
        // This reading is shared by the analyzer, the readiness scorer and the
        // authority extractor, so one omission reached all three.
        private static bool AlwaysExits(StatementSyntax statement)
        {
            switch (statement)
            {
                case ReturnStatementSyntax _:
                case ThrowStatementSyntax _:
                    return true;
                case BlockSyntax block:
                    return block.Statements.Any(
                        inner => inner is ReturnStatementSyntax or ThrowStatementSyntax);
                default:
                    return false;
            }
        }

        private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }
    }
}
