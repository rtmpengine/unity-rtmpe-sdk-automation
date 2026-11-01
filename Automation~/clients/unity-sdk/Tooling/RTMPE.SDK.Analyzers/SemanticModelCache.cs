using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Memoizes <see cref="Compilation.GetSemanticModel(SyntaxTree, bool)"/> per
    /// tree for the lifetime of one analysis pass.
    ///
    /// <see cref="Compilation.GetSemanticModel(SyntaxTree, bool)"/> binds a fresh
    /// model on every call — the compilation does not retain them — so a
    /// per-type extraction that consults a declaration's model several times, and
    /// a per-compilation run that revisits a file once per type declared in it,
    /// rebind the same model repeatedly. Binding is the dominant cost of authority
    /// inference on a large project, and it is pure: the model a tree yields does
    /// not change within a compilation, so caching it changes only how many times
    /// it is built, never what it answers.
    ///
    /// Thread-safe, because the analyzer runs symbol actions concurrently and a
    /// single cache is shared across them.
    /// </summary>
    public class SemanticModelCache
    {
        private readonly Compilation _compilation;
        private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _models =
            new ConcurrentDictionary<SyntaxTree, SemanticModel>();

        public SemanticModelCache(Compilation compilation)
        {
            _compilation = compilation;
        }

        public SemanticModel GetSemanticModel(SyntaxTree tree)
            => _models.GetOrAdd(tree, Bind);

        /// <summary>
        /// Whether <paramref name="tree"/> belongs to the compilation this cache
        /// binds — the question every caller has to ask before handing it a
        /// declaration reached through a symbol.
        /// </summary>
        /// <remarks>
        /// ⛔ A reference compiled from SOURCE rather than emitted to metadata keeps
        /// its symbols, and a base type reached through one carries syntax
        /// references into a tree this compilation has never seen. Asking for a
        /// model over it is not a wrong answer, it is <c>ArgumentException</c> —
        /// out of an analyzer as <c>AD0001</c>, in the IDE, for every type deriving
        /// from an SDK-shipped behaviour once the SDK's asmdef is a project
        /// reference. The answer here lets a reader record "could not read"
        /// instead of dying on it.
        /// </remarks>
        public bool Contains(SyntaxTree tree)
            => _compilation.ContainsSyntaxTree(tree);

        // The binding step, separated from the memoization so a test can count how
        // often it actually runs — the cache's whole guarantee is that this is
        // reached once per tree, not once per lookup.
        protected virtual SemanticModel Bind(SyntaxTree tree)
            => _compilation.GetSemanticModel(tree);
    }
}
