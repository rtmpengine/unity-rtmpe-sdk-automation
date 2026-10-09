using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Whether an expression can only denote the current instance — <c>this</c>
    /// or <c>base</c> under any number of parentheses, casts, <c>as</c> casts and
    /// null-forgiving suffixes.
    /// </summary>
    /// <remarks>
    /// 🔑 Declared once and asked by every reader that has to decide whether a
    /// receiver is this object's own storage: the pre-spawn reachability closure,
    /// the owner-guard transform's flag test, and the receiver walk the
    /// owner-state rule and the RPC-candidate rule share. Each of them had its own
    /// list — one with casts, one with parentheses only, one with bare
    /// <c>this.</c>/<c>base.</c> — so <c>(this as IBoot).Configure()</c> was
    /// refused by one reader and converted by another, and
    /// <c>((Player)this)._score.Value = 1</c> cleared a score the bare spelling
    /// faulted. Three lists answering one question is the shape this file ends.
    /// <para>
    /// ⛔ A <b>bare identifier</b> is never read as this instance: <c>var self =
    /// this; self.Configure();</c> draws no edge here, because reading names as
    /// receivers is the over-refusal the sibling readers exist to prevent. Stated
    /// as a limit so widening it has to be a decision.
    /// </para>
    /// </remarks>
    public static class ThisInstance
    {
        public static bool Denotes(ExpressionSyntax expression)
        {
            while (true)
            {
                switch (expression)
                {
                    case ThisExpressionSyntax:
                    case BaseExpressionSyntax:
                        return true;
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        break;
                    case CastExpressionSyntax cast:
                        expression = cast.Expression;
                        break;
                    // `(this as IBoot)` — the only legal spelling for reaching an
                    // explicit interface implementation besides the cast, and the
                    // one every list above had left out.
                    case BinaryExpressionSyntax asCast when asCast.IsKind(SyntaxKind.AsExpression):
                        expression = asCast.Left;
                        break;
                    case PostfixUnaryExpressionSyntax suppression
                        when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        expression = suppression.Operand;
                        break;
                    default:
                        return false;
                }
            }
        }
    }
}
