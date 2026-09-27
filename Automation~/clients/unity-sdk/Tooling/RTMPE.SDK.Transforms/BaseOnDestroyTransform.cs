using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Chains <c>base.OnDestroy()</c> from a declared <c>OnDestroy</c> so the
    /// network object's spawn registration is released rather than leaked — from an
    /// override and from a declaration that merely hides the hook alike, since Unity
    /// dispatches both. Appends the call as the last statement. Idempotent — a body
    /// already calling base is returned unchanged.
    ///
    /// ⛔ Which is why a body that can LEAVE before its last statement is declined:
    /// appending after an early <c>return</c> or <c>throw</c> answers an error
    /// diagnostic by silencing it — the analyzer asks whether the call exists, not
    /// whether it runs — and the leak the rule exists for survives the fix.
    ///
    /// The transform only ever appends within an existing statement list; it never
    /// reshapes a body. A shape it cannot append to safely is declined with a
    /// reason rather than rewritten, because the diagnostic it answers is an error
    /// and a developer who is told nothing assumes the tool disagrees.
    /// </summary>
    public static class BaseOnDestroyTransform
    {
        public static MethodDeclarationSyntax Apply(MethodDeclarationSyntax onDestroy)
            => Apply(onDestroy, out _);

        /// <summary>
        /// As <see cref="Apply(MethodDeclarationSyntax)"/>, reporting why an
        /// unchanged result was unchanged. A <c>null</c> reason means the method
        /// already chains — the idempotent case, not a refusal.
        /// </summary>
        public static MethodDeclarationSyntax Apply(
            MethodDeclarationSyntax onDestroy, out string refusalReason)
        {
            refusalReason = null;
            if (onDestroy is null)
            {
                return null;
            }

            // Idempotence is decided before shape: a method that already chains is
            // correct however it is written, and reporting a shape complaint over
            // it would send a developer to fix something that is not broken.
            if (AlreadyChains(onDestroy))
            {
                return onDestroy;
            }

            if (onDestroy.Body is not BlockSyntax body)
            {
                refusalReason = onDestroy.ExpressionBody is null
                    ? "the method has no body to chain from"
                    : MayNotReachTheEnd(onDestroy.ExpressionBody.Expression)
                        // ⛔ The general advice constructs the very shape the
                        // scan below refuses: converting `=> throw …` to a block
                        // and appending gives `{ throw …; base.OnDestroy(); }`,
                        // which is CS0162 and never runs, with the rule silenced.
                        ? "the method's expression body cannot reach an appended call — chaining here "
                            + "means restructuring so 'base.OnDestroy();' runs on every path, which is "
                            + "a decision about this method's behaviour rather than an edit"
                        : "the method has an expression body — chaining needs a statement body, so add "
                            + "'base.OnDestroy();' by hand after converting it to one";
                return onDestroy;
            }

            // `base.OnDestroy()` is an instance call: a static or value-returning
            // declaration of that name is a different member, and splicing the
            // chain into it would not compile.
            if (!BlockEditing.CanHostInstanceStatement(onDestroy))
            {
                refusalReason = "the declaration is static or value-returning — it is not the Unity "
                    + "message this fix chains, and 'base.OnDestroy()' could not compile there";
                return onDestroy;
            }

            if (!BlockEditing.IsMultiLineBody(body))
            {
                refusalReason = "the body is written on a single line — there is no line structure to "
                    + "extend, so add 'base.OnDestroy();' by hand";
                return onDestroy;
            }

            // A body carrying a CONDITIONAL compilation boundary is refused:
            // appending after the last statement can land the call inside an
            // #if region (the #endif rides the close brace's leading trivia),
            // making the release this fix exists to guarantee silently
            // conditional. Cosmetic directives (#region, #pragma) stay editable.
            if (BlockEditing.ContainsConditionalDirectives(body))
            {
                refusalReason = "the body carries #if boundaries — an appended call could land inside a "
                    + "conditional region and make the release conditional with it";
                return onDestroy;
            }

            // 🔴 A body that can leave before its last statement is refused, and
            // this is the shape the fix used to get WRONG rather than decline. An
            // appended call sits after the early exit: the object destroyed on that
            // path still leaks its spawn registration — and because the analyzer
            // asks only whether a `base.OnDestroy()` invocation EXISTS, the call it
            // never reaches silences an Error-severity rule for good. A fix that
            // leaves the defect and removes the report is worse than one that
            // declines, because the author is now told there is nothing to fix.
            //
            // ⛔ `throw` counts with `return`: it leaves the same way, and a throw
            // as the last statement would additionally make the appended call
            // unreachable code. ⛔ A `return` inside a local function or a lambda
            // does not — it leaves that body, not this one — which is the same
            // boundary AlreadyChains draws, for the same reason.
            var exits = body
                .DescendantNodes(descendIntoChildren: node => !IsDeferredBody(node))
                .Where(MayNotReachTheEnd)
                .ToList();
            if (exits.Count > 0)
            {
                refusalReason = "the body can leave before its last statement, or never arrive at it — "
                    + "an appended 'base.OnDestroy();' would sit after that exit or outside a loop with "
                    + "no way out, so the registration would still leak while the rule went quiet; "
                    + "place the call on every path by hand";
                return onDestroy;
            }

            // The preceding statement already ends its line, so the appended call
            // carries only the indent and supplies its own trailing newline before
            // the closing brace.
            var call = SyntaxFactory.ParseStatement("base.OnDestroy();")
                .WithLeadingTrivia(BlockEditing.Indent(body))
                .WithTrailingTrivia(BlockEditing.DetectNewLine(body));

            return onDestroy.WithBody(body.WithStatements(body.Statements.Add(call)));
        }

        /// <summary>
        /// Whether this node can stop the method body arriving at its end, so a
        /// statement appended there would not be reached.
        /// </summary>
        /// <remarks>
        /// 🚨 Asked as a capability because the two-kind list it replaces was a
        /// list of SPELLINGS, and a <c>throw</c> written as an EXPRESSION is
        /// neither of them. It is not an exotic shape — <c>x ?? throw new …</c>,
        /// a switch arm, an expression-bodied accessor — and the run that met one
        /// appended the call after it, exit 0: the registration still leaked on
        /// the throwing path, and because the paired analyzer asks only whether a
        /// <c>base.OnDestroy()</c> invocation EXISTS, an Error-severity rule went
        /// quiet for good over a defect that was still there.
        ///
        /// 🚨 And leaving is not the only way to miss the end. A first version
        /// of this comment asserted that a <c>goto</c> "stays inside the body and
        /// still reaches its end"; <c>Top: …; goto Top;</c> does not, and neither
        /// does <c>while (true)</c> or <c>for (;;)</c> with no way out — each was
        /// measured taking an appended call, which the customer's compiler then
        /// reports as unreachable (CS0162) while the Error-severity rule stays
        /// quiet. A forward <c>goto</c> does reach the end, and is refused with
        /// them: syntax cannot tell the two apart, and the cost of the stricter
        /// answer is one line added by hand.
        ///
        /// ⛔ <c>yield break</c> cannot occur — a <c>void</c> method is not an
        /// iterator, and <c>BlockEditing.CanHostInstanceStatement</c> has already
        /// required <c>void</c> by the time this runs. Ending the PROCESS is
        /// deliberately not read as leaving: an appended call is not what fails
        /// when there is no process left to run it. A method that never returns
        /// because a HELPER throws is beyond a pass that resolves nothing —
        /// <c>[DoesNotReturn]</c> is a semantic fact — and is stated as a limit.
        /// </remarks>
        private static bool MayNotReachTheEnd(SyntaxNode node)
            => node is ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax
                or GotoStatementSyntax
                || NeverCompletes(node);

        // A loop the body cannot get past: a condition that is written `true`, or
        // absent as in `for (;;)`, with no `break` bound to it.
        private static bool NeverCompletes(SyntaxNode node)
        {
            StatementSyntax loopBody;
            switch (node)
            {
                case WhileStatementSyntax loop when IsWrittenTrue(loop.Condition):
                    loopBody = loop.Statement;
                    break;
                case DoStatementSyntax loop when IsWrittenTrue(loop.Condition):
                    loopBody = loop.Statement;
                    break;
                case ForStatementSyntax loop when loop.Condition is null || IsWrittenTrue(loop.Condition):
                    loopBody = loop.Statement;
                    break;
                default:
                    return false;
            }

            return !BreaksOutOf(loopBody);
        }

        private static bool IsWrittenTrue(ExpressionSyntax condition)
        {
            while (condition is ParenthesizedExpressionSyntax parenthesized)
            {
                condition = parenthesized.Expression;
            }

            return condition.IsKind(SyntaxKind.TrueLiteralExpression);
        }

        // ⚠️ A `break` inside a nested loop or switch belongs to THAT one, so
        // those subtrees are not searched — counting one would read a loop with
        // no way out as having one, which is the admitting direction.
        private static bool BreaksOutOf(StatementSyntax loopBody)
            => loopBody
                .DescendantNodesAndSelf(descendIntoChildren: node =>
                    ReferenceEquals(node, loopBody)
                    || (!BindsItsOwnBreak(node) && !IsDeferredBody(node)))
                .Any(node => node is BreakStatementSyntax);

        private static bool BindsItsOwnBreak(SyntaxNode node)
            => node is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax
                or ForEachStatementSyntax or ForEachVariableStatementSyntax or SwitchStatementSyntax;

        // Read across the whole declaration rather than a block, so the expression
        // form `=> base.OnDestroy()` counts as chaining exactly as the block form
        // does — but never into a body that does not run where it is written. A call
        // inside a local function or a lambda executes only when something invokes
        // it, which is why the paired analyzer still reports the leak; counting it
        // here would answer an error diagnostic with a silent no-op.
        private static bool AlreadyChains(MethodDeclarationSyntax onDestroy)
            => onDestroy.DescendantNodes(descendIntoChildren: node => !IsDeferredBody(node))
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax access
                    && access.Expression is BaseExpressionSyntax
                    && access.Name.Identifier.ValueText == "OnDestroy");

        /// <summary>
        /// Rewrites a declaration that merely HIDES the inherited hook into one
        /// that overrides it — <c>private void OnDestroy()</c> becomes
        /// <c>protected override void OnDestroy()</c> — leaving the body, the
        /// attributes and the surrounding trivia exactly where they were.
        /// <para>
        /// 🔑 Chaining <c>base.OnDestroy()</c> from a hiding declaration is correct
        /// at RUNTIME and wrong as C#: Unity dispatches the message by name to the
        /// most derived declaration, so the release happens, but the compiler
        /// reports CS0114 on every build from then on. A fix that answers an error
        /// diagnostic by leaving a permanent warning behind has not finished.
        /// </para>
        /// <para>
        /// ⚠️ Purely syntactic, and therefore NOT self-authorising: whether
        /// <c>override</c> is legal here depends on the base hook being virtual and
        /// on its declared accessibility, neither of which is visible in this
        /// method's inputs. <paramref name="accessibility"/> is that verdict,
        /// reached where the semantic model is, and a caller with no verdict passes
        /// none and gets the declaration back untouched.
        /// </para>
        /// </summary>
        /// <param name="accessibility">
        /// The overridden member's own declared accessibility, spelled in C# —
        /// <c>protected</c>, <c>public</c>, <c>internal</c>,
        /// <c>protected internal</c> or <c>private protected</c>. An override must
        /// repeat it exactly (CS0507), which is why it is carried rather than
        /// assumed. Null, blank or unrecognised leaves the method unchanged.
        /// </param>
        public static MethodDeclarationSyntax WithOverrideModifiers(
            MethodDeclarationSyntax method, string accessibility)
        {
            if (method is null || string.IsNullOrWhiteSpace(accessibility))
            {
                return method;
            }

            // Already an override, or not the instance message at all: either way
            // there is no hiding to repair, and rewriting would be a change made
            // for its own sake.
            // ⛔ `new` is the author SAYING they meant to hide. There is no CS0114
            // on such a declaration, so the justification for reshaping it does not
            // apply — and converting it to an override changes virtual dispatch for
            // every caller holding a base-typed reference. Declined.
            if (method.Modifiers.Any(SyntaxKind.OverrideKeyword)
                || method.Modifiers.Any(SyntaxKind.StaticKeyword)
                || method.Modifiers.Any(SyntaxKind.PartialKeyword)
                || method.Modifiers.Any(SyntaxKind.NewKeyword)
                // Arity is part of a C# signature: `OnDestroy<T>()` overrides
                // nothing (CS0115) and hides nothing. The analyzer stopped issuing
                // a verdict for it, but this method is public API on a shipped DLL
                // and a caller outside the analyzer can hand it anything.
                || method.TypeParameterList is not null)
            {
                return method;
            }

            // ⚠️ This rewrite REBUILDS the modifier list, and everything an author
            // wrote between those tokens lives in their trivia — a comment, a
            // `#if`, a `#pragma`. Rebuilding drops it. A source transform must
            // never silently delete an author's words, so a modifier list
            // carrying anything but whitespace is left exactly as written and the
            // caller gets the chain alone; CS0114 is the smaller harm.
            //
            // The first modifier's LEADING trivia is exempt because it is carried
            // across verbatim below — that is where the indentation, the XML doc
            // comment and the attribute list's aftermath actually sit, and
            // refusing on it would decline almost every real declaration.
            for (int i = 0; i < method.Modifiers.Count; i++)
            {
                if ((i > 0 && CarriesAuthoredTrivia(method.Modifiers[i].LeadingTrivia))
                    || CarriesAuthoredTrivia(method.Modifiers[i].TrailingTrivia))
                {
                    return method;
                }
            }

            // Cleared below when there is a modifier list to move the indent off,
            // so a comment sitting immediately before the return type is at risk
            // in exactly that case.
            if (method.Modifiers.Count > 0 && CarriesAuthoredTrivia(method.ReturnType.GetLeadingTrivia()))
            {
                return method;
            }

            // Matched WHOLE, against the five spellings C# actually allows on an
            // override — never word by word. Tokenising against a keyword set
            // accepted `private`, `public internal` and `protected protected`, none
            // of which compiles, on a method that is public API of a shipped DLL.
            if (!LegalOverrideAccessibilities.TryGetValue(accessibility.Trim(), out var accessibilityKinds))
            {
                return method;
            }

            var accessibilityTokens = new List<SyntaxToken>();
            foreach (var kind in accessibilityKinds)
            {
                accessibilityTokens.Add(SyntaxFactory.Token(kind));
            }

            // The indent lives on whichever token opens the declaration, and that
            // is the modifier list only when there is one — an attribute list or a
            // bare return type carries it otherwise.
            var leading = method.Modifiers.Count > 0
                ? method.Modifiers[0].LeadingTrivia
                : method.ReturnType.GetLeadingTrivia();

            var rebuilt = new List<SyntaxToken>(accessibilityTokens)
            {
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword),
            };

            // Anything that is not an accessibility, a virtuality or the explicit
            // hiding marker is the author's and is kept in the order they wrote it.
            foreach (var existing in method.Modifiers)
            {
                if (!ReplacedModifiers.Contains(existing.Kind()))
                {
                    rebuilt.Add(existing.WithoutTrivia());
                }
            }

            for (int i = 0; i < rebuilt.Count; i++)
            {
                rebuilt[i] = rebuilt[i].WithTrailingTrivia(SyntaxFactory.Space);
            }

            rebuilt[0] = rebuilt[0].WithLeadingTrivia(leading);

            return method
                .WithModifiers(SyntaxFactory.TokenList(rebuilt))
                .WithReturnType(method.ReturnType.WithLeadingTrivia(SyntaxTriviaList.Empty));
        }

        // Whitespace and line breaks are layout this transform may reflow; anything
        // else — a comment, a documentation comment, a preprocessor directive — is
        // the author's and is not this transform's to move or drop.
        private static bool CarriesAuthoredTrivia(SyntaxTriviaList trivia)
        {
            foreach (var piece in trivia)
            {
                if (!piece.IsKind(SyntaxKind.WhitespaceTrivia) && !piece.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly Dictionary<string, SyntaxKind[]> LegalOverrideAccessibilities =
            new Dictionary<string, SyntaxKind[]>(StringComparer.Ordinal)
            {
                { "public", new[] { SyntaxKind.PublicKeyword } },
                { "protected", new[] { SyntaxKind.ProtectedKeyword } },
                { "internal", new[] { SyntaxKind.InternalKeyword } },
                { "protected internal", new[] { SyntaxKind.ProtectedKeyword, SyntaxKind.InternalKeyword } },
                { "private protected", new[] { SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword } },
            };

        // Dropped when the declaration becomes an override: the accessibility is
        // replaced by the overridden member's, `new` is the explicit statement of
        // the hiding being repaired, and `virtual`/`abstract`/`sealed` cannot
        // stand beside `override` on a declaration that was not one.
        private static readonly HashSet<SyntaxKind> ReplacedModifiers = new HashSet<SyntaxKind>
        {
            SyntaxKind.PublicKeyword, SyntaxKind.ProtectedKeyword,
            SyntaxKind.InternalKeyword, SyntaxKind.PrivateKeyword,
            SyntaxKind.NewKeyword, SyntaxKind.VirtualKeyword,
            SyntaxKind.AbstractKeyword, SyntaxKind.SealedKeyword,
        };

        private static bool IsDeferredBody(SyntaxNode node)
            => node is LocalFunctionStatementSyntax
                || node is AnonymousFunctionExpressionSyntax;
    }
}
