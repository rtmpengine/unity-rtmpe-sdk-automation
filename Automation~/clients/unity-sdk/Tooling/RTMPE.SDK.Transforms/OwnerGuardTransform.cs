using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Fences an <c>Update</c> loop behind the owner guard so a non-owner does not
    /// drive simulation: inserts <c>if (!IsOwner) return;</c> as the first statement.
    /// Idempotent — an Update already opening with an IsOwner guard is returned
    /// unchanged, so re-running the conversion is a byte-identical no-op.
    ///
    /// The transform only ever inserts into an existing statement list; it never
    /// reshapes a body. A shape it cannot fence safely is declined with a reason
    /// rather than rewritten.
    /// </summary>
    public static class OwnerGuardTransform
    {
        private const string OwnerFlagName = "IsOwner";

        public static MethodDeclarationSyntax Apply(MethodDeclarationSyntax update)
            => Apply(update, out _);

        /// <summary>
        /// As <see cref="Apply(MethodDeclarationSyntax)"/>, reporting why an
        /// unchanged result was unchanged. A <c>null</c> reason means the loop is
        /// already fenced — the idempotent case, not a refusal.
        /// <para>
        /// The loop is whichever frame message the caller selected — <c>Update</c>,
        /// <c>FixedUpdate</c> or <c>LateUpdate</c>; this transform reads the
        /// declaration it is handed and never its name.
        /// </para>
        /// </summary>
        public static MethodDeclarationSyntax Apply(
            MethodDeclarationSyntax update, out string refusalReason)
        {
            refusalReason = null;
            if (update is null)
            {
                return null;
            }

            if (update.Body is not BlockSyntax body)
            {
                refusalReason = update.ExpressionBody is not null
                    ? "the method has an expression body — the guard needs a statement body, so add "
                        + "'if (!IsOwner) return;' by hand after converting it to one"
                    : "the method has no body to fence";
                return update;
            }

            // Idempotence before shape: a loop already opening with the guard is
            // correct, and a shape complaint over it would be noise.
            if (OpensWithOwnerGuard(body))
            {
                // The leading test names IsOwner and exits, but not on the non-owner
                // side — `if (IsOwner) return;` hands the frame to everyone except the
                // owner. Fencing above it would return for every caller, so the body is
                // left alone either way; separating the two says which happened, since
                // this one is an inversion to look at rather than a conversion already
                // done.
                if (!OpensWithNonOwnerExit(body))
                {
                    refusalReason = "the body opens with an 'IsOwner' test that exits for the owner "
                        + "rather than the non-owner — a guard inserted above it would return for "
                        + "every caller, so the intended direction has to be settled by hand";
                }

                return update;
            }

            if (body.Statements.Count == 0)
            {
                // A body whose statements all sit inside a conditional region
                // parses with none at all, which is indistinguishable from an
                // empty one by count. Saying "empty" there sends the author
                // looking for a body that is plainly in front of them; the reason
                // this shape is declined is the directive, and the remedy differs.
                refusalReason = BlockEditing.ContainsConditionalDirectives(body)
                    ? "every statement in the body sits inside #if boundaries, so a guard inserted "
                        + "above them could fence only some builds"
                    : "the body is empty — there is nothing to fence behind the guard";
                return update;
            }

            // `IsOwner` is inherited instance state and the guard exits with a bare
            // `return`: neither is available in a static or value-returning
            // declaration, which is not the Unity message this fix targets.
            if (!BlockEditing.CanHostInstanceStatement(update))
            {
                refusalReason = "the declaration is static or value-returning — it is not the Unity "
                    + "message this fix guards, and neither 'IsOwner' nor a bare return is available there";
                return update;
            }

            if (!BlockEditing.IsMultiLineBody(body))
            {
                refusalReason = "the body is written on a single line — there is no line structure to "
                    + "extend, so add 'if (!IsOwner) return;' by hand";
                return update;
            }

            // A body carrying a CONDITIONAL compilation boundary is refused: a
            // statement inserted into a list cannot know which #if region the
            // final text places it in, so the guard could silently become
            // conditional. Cosmetic directives (#region, #pragma) stay editable.
            if (BlockEditing.ContainsConditionalDirectives(body))
            {
                refusalReason = "the body carries #if boundaries — an inserted guard could land inside a "
                    + "conditional region and fence only some builds";
                return update;
            }

            // The guard names `IsOwner` unqualified, so it binds to whatever that
            // spelling means where it lands. A declaration of the name on the type
            // or inside the body takes the binding away from the inherited
            // ownership flag: the fence then tests the author's own value, or does
            // not compile at all. Neither is a rewrite a machine may make silently.
            string taken = NameBinding.Describe(update, OwnerFlagName);
            if (taken != null)
            {
                refusalReason = "'" + OwnerFlagName + "' is already " + taken
                    + " here, so the inserted guard would not read the inherited ownership flag";
                return update;
            }

            // 🔑 The inversion arm above reads a leading `IsOwner` test only when
            // it EXITS. The same hazard is written without an exit —
            // `if (IsOwner) { Send(); } else { Apply(); }` partitions owner from
            // replica in branches — and a guard fenced above one takes every
            // non-owner frame first, so the branch written for them becomes
            // unreachable in output that compiles clean and re-runs as already
            // converted.
            //
            // ⛔ Scoped to conditions that NAME the ownership flag, which is the
            // whole of what this host can know. A leading exit that does not —
            // `if (!enabled) return;`, `if (_target == null) return;` — is a
            // precondition, and fencing above one is exactly the intended edit;
            // refusing those withheld the fix on the commonest `Update` shape
            // there is while still leaving this one offered.
            if (body.Statements.FirstOrDefault() is IfStatementSyntax leading
                && MentionsOwnerFlag(leading.Condition))
            {
                refusalReason = "the body opens with a test of '" + OwnerFlagName + "' that does not "
                    + "exit — a guard inserted above it would take every non-owner frame first, so the "
                    + "branch written for them could not run; settle the two by hand";
                return update;
            }

            // Front insert: the open brace already ends its line, so the guard
            // carries only the indent and supplies the newline before the next
            // statement via its trailing trivia.
            var guard = SyntaxFactory.ParseStatement("if (!IsOwner) return;")
                .WithLeadingTrivia(BlockEditing.Indent(body))
                .WithTrailingTrivia(BlockEditing.DetectNewLine(body));

            return update.WithBody(body.WithStatements(body.Statements.Insert(0, guard)));
        }

        // A leading owner guard is left untouched: re-running never stacks a second
        // guard, and a hand-written owner-conditional early return is preserved
        // rather than fenced above — a guard inserted over one takes every non-owner
        // frame first, which would make the author's own branch unreachable.
        private static bool OpensWithOwnerGuard(BlockSyntax body)
            => body.Statements.FirstOrDefault() is IfStatementSyntax guard
                && AlwaysExits(guard.Statement)
                && MentionsOwnerFlag(guard.Condition);

        // Whether a condition reads the ownership flag anywhere within it. Read
        // from syntax alone and by name, exactly as the surrounding arms do: a
        // same-named flag on another type answers the same way, and every answer
        // here leads to the body being left untouched, so the cost of the
        // over-match is a report the author reads rather than an edit they do not
        // expect.
        private static bool MentionsOwnerFlag(ExpressionSyntax condition)
            => condition.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == OwnerFlagName);

        // Whether the leading test's exit is the non-owner's — the direction that
        // makes it a fence. Read from syntax alone, in the spellings the runtime
        // treats as equivalent: `!IsOwner`, `IsOwner == false`, `IsOwner != true`,
        // each optionally one operand of a top-level `||` chain, which only widens
        // the set of frames that leave early. Unlike the analyzer's reading, no
        // symbol is bound here, so a same-named flag on another type answers the
        // same way — acceptable, because both answers lead to the body being left
        // untouched and only the wording of the report differs.
        private static bool OpensWithNonOwnerExit(BlockSyntax body)
            => body.Statements.FirstOrDefault() is IfStatementSyntax guard
                && AnyDisjunctExitsNonOwner(Unparenthesize(guard.Condition));

        private static bool AnyDisjunctExitsNonOwner(ExpressionSyntax condition)
        {
            if (condition is BinaryExpressionSyntax disjunction
                && disjunction.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return AnyDisjunctExitsNonOwner(Unparenthesize(disjunction.Left))
                    || AnyDisjunctExitsNonOwner(Unparenthesize(disjunction.Right));
            }

            switch (condition)
            {
                case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    return NamesOwnerFlag(Unparenthesize(not.Operand));
                case BinaryExpressionSyntax equals when equals.IsKind(SyntaxKind.EqualsExpression):
                    return ComparesOwnerFlagTo(equals, SyntaxKind.FalseLiteralExpression);
                case BinaryExpressionSyntax notEquals when notEquals.IsKind(SyntaxKind.NotEqualsExpression):
                    return ComparesOwnerFlagTo(notEquals, SyntaxKind.TrueLiteralExpression);
                default:
                    return false;
            }
        }

        private static bool ComparesOwnerFlagTo(BinaryExpressionSyntax comparison, SyntaxKind literal)
        {
            var left = Unparenthesize(comparison.Left);
            var right = Unparenthesize(comparison.Right);
            return (NamesOwnerFlag(left) && right.IsKind(literal))
                || (NamesOwnerFlag(right) && left.IsKind(literal));
        }

        // The flag is inherited, so it reads as a bare name or through a receiver
        // that can only mean this object — `this.`/`base.` under whatever wrappers
        // the shared rule admits, so `((Player)this).IsOwner` is the same flag.
        private static bool NamesOwnerFlag(ExpressionSyntax expression)
            => expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText == OwnerFlagName,
                MemberAccessExpressionSyntax access =>
                    RTMPE.SDK.Analyzers.ThisInstance.Denotes(access.Expression)
                        && access.Name.Identifier.ValueText == OwnerFlagName,
                _ => false,
            };

        private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }

        // The branch exits whenever an unconditional `return;` or `throw` sits anywhere
        // in its own statement list — control cannot fall out of a block past one — so a
        // guard that runs non-owner-side work before returning fences the method
        // exactly as the bare form does. This is the same return-recognition the
        // analyzer's OwnerGuardSyntax applies when deciding whether the diagnostic
        // this transform answers fires at all. The two are not required to agree in
        // both directions — this check is the more reluctant, since an
        // `if (IsOwner) return;` is not a guard yet fencing above it would exit for
        // every frame — but a differential test pins the direction that matters:
        // whatever the analyzer calls guarded, this must leave alone.
        private static bool AlwaysExits(StatementSyntax statement)
            => statement is ReturnStatementSyntax or ThrowStatementSyntax
                || (statement is BlockSyntax block
                    && block.Statements.Any(
                        inner => inner is ReturnStatementSyntax or ThrowStatementSyntax));
    }
}
