using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Trivia helpers shared by the conversion rewriters so an inserted statement
    /// lands on its own, correctly-indented line — preserving the file's existing
    /// indentation and line endings without depending on a Workspaces formatter.
    /// In Roslyn's model the newline ending a line is the previous token's trailing
    /// trivia and a statement's leading trivia is only its indent, which is why a
    /// front-inserted and an appended statement need mirror-image trivia.
    /// </summary>
    internal static class BlockEditing
    {
        /// <summary>
        /// True when <paramref name="node"/> carries a CONDITIONAL compilation
        /// boundary — #if / #elif / #else / #endif or disabled text — the only
        /// directives that make an element's final region ambiguous to a list
        /// insert or a rewrite. Cosmetic directives (#region, #pragma, #nullable,
        /// #line) never change which elements compile, so they are deliberately
        /// not matched: refusing on them would regress perfectly safe shapes.
        /// </summary>
        public static bool ContainsConditionalDirectives(SyntaxNode node)
        {
            foreach (var trivia in node.DescendantTrivia(descendIntoTrivia: true))
            {
                if (trivia.IsKind(SyntaxKind.IfDirectiveTrivia)
                    || trivia.IsKind(SyntaxKind.ElifDirectiveTrivia)
                    || trivia.IsKind(SyntaxKind.ElseDirectiveTrivia)
                    || trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia)
                    || trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The first index in <paramref name="list"/> that a conditional-compilation
        /// region encloses, or <c>list.Count</c> when no element is conditionally
        /// included. Everything before that index is unconditional in every build
        /// configuration, so it is the only position at which an inserted element is
        /// guaranteed to compile everywhere.
        ///
        /// Appending is not that position. A region's <c>#endif</c> is leading trivia
        /// of the token that follows the region, not a member of the list, so a list
        /// whose tail sits inside <c>#if</c> ends — as far as the list is concerned —
        /// while the region is still open, and an appended element is swallowed by it.
        ///
        /// Depth is read from each element's leading trivia, where the directives
        /// preceding it live. <c>#elif</c>/<c>#else</c> do not change depth: they
        /// continue a region rather than open or close one. Cosmetic directives
        /// (#region, #pragma, #nullable, #line) never change which elements compile
        /// and are deliberately not counted.
        ///
        /// The reading is at element granularity, which is where every region that
        /// matters lives: a region opening inside one element and closing after
        /// another already leaves that element unterminated in the excluded
        /// configuration, so such a file does not compile in both configurations
        /// with or without an insert. Scanning deeper would buy nothing and would
        /// misread the common, self-contained <c>#if</c> inside a single body.
        /// </summary>
        public static int FirstConditionalIndex<TNode>(SyntaxList<TNode> list)
            where TNode : SyntaxNode
        {
            int depth = 0;
            for (int i = 0; i < list.Count; i++)
            {
                foreach (var trivia in list[i].GetLeadingTrivia())
                {
                    if (trivia.IsKind(SyntaxKind.IfDirectiveTrivia))
                    {
                        depth++;
                    }
                    else if (trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia))
                    {
                        depth--;
                    }
                }

                if (depth > 0)
                {
                    return i;
                }
            }

            return list.Count;
        }

        /// <summary>
        /// The block's statement indent (no newline). An inserted statement carries
        /// this as its leading trivia and a detected newline as its trailing trivia:
        /// the preceding token already ends its own line, so the new statement only
        /// needs to indent itself and end its line before what follows.
        /// </summary>
        public static SyntaxTriviaList Indent(BlockSyntax body)
            => SyntaxFactory.TriviaList(SyntaxFactory.Whitespace(IndentText(body)));

        /// <summary>
        /// True when the block is laid out across multiple lines — the open brace
        /// ends its line (or, for an empty block, the close brace sits on its own
        /// line). A single-line <c>{ stmt; }</c> body has no line structure to
        /// extend, so the statement-inserting transforms leave it untouched rather
        /// than glue the new statement onto an existing line.
        /// </summary>
        public static bool IsMultiLineBody(BlockSyntax body)
            => body.OpenBraceToken.TrailingTrivia.Any(IsNewLine)
                || (body.Statements.Count > 0 && body.Statements[0].GetLeadingTrivia().Any(IsNewLine))
                || (body.Statements.Count == 0 && body.CloseBraceToken.LeadingTrivia.Any(IsNewLine));

        /// <summary>
        /// True when a method can host the statements these transforms splice.
        /// Both reach the instance — <c>base.OnDestroy()</c> and the inherited
        /// <c>IsOwner</c> — and both assume a control flow where a bare
        /// <c>return</c> is legal, so a static or value-returning declaration is
        /// not the Unity message they target and could not compile what they
        /// insert. Refusing the shape is the difference between declining an edit
        /// and writing a build break to disk.
        /// </summary>
        public static bool CanHostInstanceStatement(MethodDeclarationSyntax method)
            => method is not null
                && !method.Modifiers.Any(SyntaxKind.StaticKeyword)
                && method.ReturnType is PredefinedTypeSyntax returnType
                && returnType.Keyword.IsKind(SyntaxKind.VoidKeyword);

        /// <summary>
        /// One indentation level in the file's own style: a tab where the
        /// surrounding indent already uses tabs, four spaces otherwise. Keeps an
        /// inserted deeper line from mixing tabs and spaces with the code it joins.
        /// </summary>
        public static string IndentStep(string surroundingIndent)
            => surroundingIndent.IndexOf('\t') >= 0 ? "\t" : "    ";

        /// <summary>
        /// The newline the node already uses (so inserted code does not mix CRLF and
        /// LF), defaulting to CRLF only when the node carries no line ending at all.
        /// </summary>
        public static SyntaxTrivia DetectNewLine(SyntaxNode node)
        {
            foreach (var trivia in node.DescendantTrivia())
            {
                if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    return SyntaxFactory.EndOfLine(trivia.ToString());
                }
            }

            return SyntaxFactory.CarriageReturnLineFeed;
        }

        // The indentation a statement in this block sits at: the indent of the
        // existing statements, or one level past the closing brace when empty. Tabs
        // or spaces are preserved exactly as the file uses them.
        private static string IndentText(BlockSyntax body)
        {
            if (body.Statements.Count > 0)
            {
                return IndentOf(body.Statements[0].GetLeadingTrivia());
            }

            string closingIndent = IndentOf(body.CloseBraceToken.LeadingTrivia);
            return closingIndent + IndentStep(closingIndent);
        }

        private static string IndentOf(SyntaxTriviaList leading)
            => leading.Reverse()
                .TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia))
                .Reverse()
                .Aggregate("", (text, t) => text + t.ToString());

        private static bool IsNewLine(SyntaxTrivia trivia) => trivia.IsKind(SyntaxKind.EndOfLineTrivia);
    }
}
