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
    /// One-click fix for <c>RTMPE2003</c>: open the flagged frame loop with the
    /// <c>if (!IsOwner) return;</c> guard so a non-owner does not drive simulation.
    /// Delegates to the shared transform for byte-identical output with the wizard.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(OwnerGuardCodeFixProvider)), Shared]
    public sealed class OwnerGuardCodeFixProvider : CodeFixProvider
    {
        private const string Title = "Add the IsOwner guard";

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(DiagnosticIds.ConversionMissingOwnerGuard);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var (root, update) = await CodeFixRoots.FindAsync<MethodDeclarationSyntax>(context).ConfigureAwait(false);
            if (root is null || update is null)
            {
                return;
            }

            // Offer the fix only when the transform actually rewrites the method —
            // an expression-bodied or already-guarded Update yields no change, and an
            // inert quick-fix must never be surfaced.
            var rewritten = OwnerGuardTransform.Apply(update);
            if (ReferenceEquals(rewritten, update))
            {
                return;
            }

            var newRoot = root.ReplaceNode(update, rewritten);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    equivalenceKey: nameof(OwnerGuardCodeFixProvider)),
                context.Diagnostics[0]);
        }
    }
}
