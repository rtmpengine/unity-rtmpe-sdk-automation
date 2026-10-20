using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace RTMPE.SDK.CodeFixes
{
    /// <summary>
    /// The one way a provider in this assembly offers an edit: the rewrite is
    /// compiled before the lightbulb exists, and a rewrite that would not compile
    /// is never offered at all.
    ///
    /// <para>🔑 The writing hosts have asked this question since the compile gate
    /// was written — <c>make convert</c> refuses to write a file whose rewrite
    /// introduces an error — and the lightbulb, which applies the SAME transforms
    /// to the SAME shapes, asked nothing. That was not a policy difference: the
    /// IDE simply had no equivalent, and the two struct-mutation shapes the
    /// transform now refuses by name (<c>_aim.Value.x = 1f</c>, CS1612, and
    /// <c>_aim.Value.Set(…)</c>, which compiles and silently stops replicating)
    /// are what a missing gate costs — each was found one at a time, after it had
    /// been written into somebody's project.</para>
    ///
    /// <para>⚠️ This gate is STRONGER than the CLI's and weaker in one place.
    /// Stronger because it compiles against the project's own compilation —
    /// real references, the file's real siblings — where the CLI has only the
    /// few-dozen-type contract stub. Weaker because it reads ONE file: a
    /// rewrite that retypes a member another file reads is outside what either
    /// host can see, and it is the CLI's limit too.</para>
    /// </summary>
    internal static class VerifiedCodeFix
    {
        /// <summary>
        /// Registers <paramref name="newRoot"/> as the fix for this context's
        /// diagnostic, unless applying it would introduce a compile error.
        /// </summary>
        public static async Task OfferAsync(
            CodeFixContext context, SyntaxNode newRoot, string title, string equivalenceKey)
        {
            if (!await TheRewriteStillCompilesAsync(context, newRoot).ConfigureAwait(false))
            {
                // ⛔ Silently, and the diagnostic stays. "Reporting without
                // offering" is the pair this assembly already makes wherever a
                // transform refuses a shape: the author keeps the finding and its
                // help link, and loses only a remedy that would not have built.
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    equivalenceKey),
                context.Diagnostics[0]);
        }

        /// <summary>
        /// Whether the rewrite leaves this file compiling, asked of the project's
        /// own compilation.
        /// </summary>
        /// <remarks>
        /// ⚠️ It stands down on a file that does not already compile, and the
        /// reason is the one the CLI gate records at its own call site: an error
        /// the input ALREADY had can cause a DIFFERENT error once the rewrite
        /// starts using the broken thing — a type inheriting <c>NetworkBehaviour</c>
        /// without importing it carries an unresolved base before and after, and
        /// the rewrite's <c>new NetworkVariableInt(this, …)</c> then fails CS1503,
        /// a signature the input never had. Refusing there would withhold the
        /// remedy over a fault the author is already looking at and can do nothing
        /// about from the lightbulb.
        ///
        /// <para>🔑 With a clean baseline the subtraction the CLI performs is a
        /// no-op — nothing to subtract — so "any error after" IS the set the
        /// rewrite introduced, and the predicate is the gate's own:
        /// <c>Severity == Error</c>.</para>
        ///
        /// <para>⛔ Cost: one file bound twice per offer. On the lightbulb path
        /// the before side is what the editor has already computed for the open
        /// document; the after side is the real work, and it is bounded by the
        /// file rather than by the project, because a <c>SemanticModel</c>'s
        /// diagnostics are its own tree's. The two providers that carry the
        /// batch fix-all (<c>WellKnownFixAllProviders.BatchFixer</c>) pay it once
        /// per diagnostic in the batch — each rewrite is judged on its own
        /// against the document as it stands, never on whether the N rewrites
        /// compile TOGETHER; the batch fixer merges edits that were each
        /// verified alone, and that merge is the fix-all's own.</para>
        /// </remarks>
        private static async Task<bool> TheRewriteStillCompilesAsync(CodeFixContext context, SyntaxNode newRoot)
        {
            var model = await context.Document
                .GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                // No model, no verdict. A document outside a compilation is not a
                // document this gate has grounds to refuse.
                return true;
            }

            if (CarriesAnError(model, context.CancellationToken))
            {
                return true;
            }

            var tree = model.SyntaxTree;

            // 🚨 The rewrite is judged as TEXT, re-parsed, and not as the node
            // graph the transform returned. They are not the same program: a
            // contextual keyword is marked by the LEXER, so the `nameof(_score)`
            // this conversion emits — built from `SyntaxFactory` rather than
            // scanned — binds as a call to a method named `nameof` and reports
            // CS0103 against code that compiles perfectly once written. Measured:
            // the node graph reported that error on every in-place conversion,
            // the same bytes re-parsed report nothing. Text is also what the
            // author actually receives, so this is the cheaper claim as well as
            // the true one.
            var rewritten = tree.WithChangedText(newRoot.GetText());

            // The project entire, with this one file swapped: the rewrite is
            // judged against the references and the sibling files it will
            // actually live among, which is the half the contract stub can only
            // approximate.
            var afterwards = model.Compilation.ReplaceSyntaxTree(tree, rewritten);
            return !CarriesAnError(afterwards.GetSemanticModel(rewritten), context.CancellationToken);
        }

        // Syntax, declaration and method-body diagnostics for one tree. Error
        // severity only — a warning is something the author can see and weigh,
        // and the claim this gate makes is about what will not build.
        private static bool CarriesAnError(SemanticModel model, CancellationToken cancellationToken)
            => model.GetDiagnostics(null, cancellationToken)
                .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
