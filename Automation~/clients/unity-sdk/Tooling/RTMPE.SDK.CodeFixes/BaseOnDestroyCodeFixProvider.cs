using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Analyzers;
using RTMPE.SDK.Transforms;

namespace RTMPE.SDK.CodeFixes
{
    /// <summary>
    /// One-click fix for <c>RTMPE1020</c>: chain <c>base.OnDestroy()</c> from the
    /// flagged method so the network object's spawn registration is released
    /// rather than leaked. Delegates to the shared transform for byte-identical
    /// output with the wizard.
    /// <para>
    /// 🔑 Where the flagged method merely HIDES the inherited hook — the
    /// idiomatic <c>private void OnDestroy()</c> a rebased MonoBehaviour carries
    /// in — the chain alone leaves CS0114 standing on every subsequent compile,
    /// so the declaration is repaired with it. Whether that is legal was decided
    /// by the analyzer, which has the semantic model; it arrives here as a
    /// diagnostic property, and its absence means the minimal edit is the whole
    /// of the fix.
    /// </para>
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BaseOnDestroyCodeFixProvider)), Shared]
    public sealed class BaseOnDestroyCodeFixProvider : CodeFixProvider
    {
        private const string Title = "Call base.OnDestroy()";
        private const string TitleWithOverride = "Call base.OnDestroy() and override the hook";

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(DiagnosticIds.LifecycleMissingBaseOnDestroy);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var (root, onDestroy) = await CodeFixRoots.FindAsync<MethodDeclarationSyntax>(context).ConfigureAwait(false);
            if (root is null || onDestroy is null)
            {
                return;
            }

            // Offer the fix only when the transform actually rewrites the method, so
            // a shape it cannot act on (e.g. an expression-bodied OnDestroy) never
            // surfaces an inert quick-fix.
            var rewritten = BaseOnDestroyTransform.Apply(onDestroy);
            if (ReferenceEquals(rewritten, onDestroy))
            {
                return;
            }

            var diagnostic = context.Diagnostics[0];
            diagnostic.Properties.TryGetValue(
                LifecycleRulesAnalyzer.OverrideAccessibilityProperty, out string accessibility);

            var repaired = BaseOnDestroyTransform.WithOverrideModifiers(rewritten, accessibility);
            bool declarationRepaired = !ReferenceEquals(repaired, rewritten);

            var newRoot = root.ReplaceNode(onDestroy, repaired);
            context.RegisterCodeFix(
                CodeAction.Create(
                    declarationRepaired ? TitleWithOverride : Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    equivalenceKey: nameof(BaseOnDestroyCodeFixProvider)),
                diagnostic);
        }
    }
}
