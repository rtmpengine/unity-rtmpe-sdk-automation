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
    /// One-click fix for <c>RTMPE2001</c>: rebase the flagged <c>MonoBehaviour</c>
    /// onto <c>NetworkBehaviour</c> and import <c>RTMPE.Core</c>. Delegates the edit
    /// to the shared transform the wizard also drives, so the IDE quick-fix and the
    /// batch conversion produce byte-identical output.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RebaseCodeFixProvider)), Shared]
    public sealed class RebaseCodeFixProvider : CodeFixProvider
    {
        private const string Title = "Rebase to NetworkBehaviour";

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(DiagnosticIds.ConversionRebaseCandidate);

        // No batch fix-all: the rebase also imports RTMPE.Core, so every occurrence in
        // one file produces an edit at the same file-level position. The batch provider
        // merges per-occurrence edits and drops the ones that overlap, which would leave
        // part of the selection on MonoBehaviour while reporting success — a silent
        // partial conversion. The remaining entry points each carry one type at a time
        // and apply it whole or refuse it whole, so neither can half-convert a file.
        public override FixAllProvider GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var (root, type) = await CodeFixRoots.FindAsync<ClassDeclarationSyntax>(context).ConfigureAwait(false);
            if (root is null || type is null)
            {
                return;
            }

            // Offer the fix only when the transform actually rewrites the tree, so a
            // shape the mechanical transform cannot act on never surfaces an inert
            // "nothing happens" quick-fix.
            var newRoot = RebaseTransform.Apply(root, type);
            if (ReferenceEquals(newRoot, root))
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    equivalenceKey: nameof(RebaseCodeFixProvider)),
                context.Diagnostics[0]);
        }
    }
}
