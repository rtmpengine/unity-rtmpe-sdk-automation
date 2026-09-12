using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Scope-aware using management shared by the transforms: an import counts
    /// only when a plain <c>using</c> (not an alias, not <c>using static</c>) in
    /// the file or an enclosing namespace brings the namespace into the target's
    /// scope, and a new import lands at file level, keeping any file header on
    /// top.
    /// </summary>
    internal static class ImportEditing
    {
        public static bool IsInScope(SyntaxNode target, string namespaceName)
            => target.Ancestors().Any(
                ancestor => UsingsOf(ancestor).Any(u => IsPlainImportOf(u, namespaceName)));

        public static CompilationUnitSyntax AddFileLevelImport(CompilationUnitSyntax root, string namespaceName)
        {
            var import = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName(namespaceName).WithLeadingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(BlockEditing.DetectNewLine(root));

            // The import must bind in every build configuration, because the
            // declaration that needs it does. Appending would place it after the
            // last using — which, when the file ends with the `#if UNITY_EDITOR /
            // using UnityEditor; / #endif` idiom, is inside that region: the type
            // is rebased unconditionally while its namespace is imported only in
            // the editor build. Landing ahead of the first conditional import is
            // unconditional by construction, and an import that one configuration
            // does not consume is at worst unused, never unresolved.
            int position = BlockEditing.FirstConditionalIndex(root.Usings);

            // Whatever the import displaces from the top of the file carries the
            // file's own header with it — a copyright or licence banner belongs to
            // the file, not to the element that happens to hold it as trivia, and
            // an import written above it reads as if the header applied to nothing.
            if (position == 0)
            {
                root = CarryHeaderOnto(root, ref import);
            }

            return root.WithUsings(root.Usings.Insert(position, import));
        }

        // Moves the file header — the leading trivia up to the first directive —
        // from whatever currently opens the file onto the import about to be placed
        // above it. With no usings the header is the whole leading trivia of the
        // first token; with a conditional first using it is the part before the
        // directive that opens the region, which stays with the using it guards.
        private static CompilationUnitSyntax CarryHeaderOnto(
            CompilationUnitSyntax root, ref UsingDirectiveSyntax import)
        {
            if (root.Usings.Count == 0)
            {
                var first = root.GetFirstToken();
                if (first.LeadingTrivia.Count == 0)
                {
                    return root;
                }

                import = import.WithLeadingTrivia(first.LeadingTrivia);
                return root.ReplaceToken(first, first.WithLeadingTrivia());
            }

            var displaced = root.Usings[0];
            var leading = displaced.GetLeadingTrivia();
            int directive = 0;
            while (directive < leading.Count && !leading[directive].IsDirective)
            {
                directive++;
            }

            if (directive == 0 || directive == leading.Count)
            {
                return root; // nothing ahead of a directive to carry
            }

            import = import.WithLeadingTrivia(leading.Take(directive));
            return root.ReplaceNode(displaced, displaced.WithLeadingTrivia(leading.Skip(directive)));
        }

        private static SyntaxList<UsingDirectiveSyntax> UsingsOf(SyntaxNode node)
        {
            switch (node)
            {
                case CompilationUnitSyntax compilationUnit:
                    return compilationUnit.Usings;
                case NamespaceDeclarationSyntax namespaceDeclaration:
                    return namespaceDeclaration.Usings;
                case FileScopedNamespaceDeclarationSyntax fileScoped:
                    return fileScoped.Usings;
                default:
                    return default;
            }
        }

        private static bool IsPlainImportOf(UsingDirectiveSyntax directive, string namespaceName)
            => directive.Alias is null
                && directive.StaticKeyword.IsKind(SyntaxKind.None)
                && directive.Name?.ToString().Replace("global::", string.Empty) == namespaceName;
    }
}
