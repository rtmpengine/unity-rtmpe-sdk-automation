using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.CodeFixes
{
    /// <summary>
    /// Locates the node a conversion fix acts on — the innermost
    /// <typeparamref name="T"/> enclosing the diagnostic span — together with the
    /// compilation-unit root the rewrite is applied to, so each provider shares one
    /// node-finding definition.
    /// </summary>
    internal static class CodeFixRoots
    {
        public static async Task<(CompilationUnitSyntax Root, T Node)> FindAsync<T>(CodeFixContext context)
            where T : SyntaxNode
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false)
                as CompilationUnitSyntax;
            var node = root?.FindNode(context.Diagnostics[0].Location.SourceSpan).FirstAncestorOrSelf<T>();
            return (root, node);
        }
    }
}
