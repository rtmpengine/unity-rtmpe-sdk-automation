using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Rebases a single-player <c>MonoBehaviour</c> onto the SDK's
    /// <c>NetworkBehaviour</c> and ensures <c>using RTMPE.Core;</c> is in scope for the
    /// rebased type, so it can carry networked state. Idempotent — a class already on
    /// NetworkBehaviour, and a type that already has the namespace in scope, are left
    /// unchanged.
    /// </summary>
    public static class RebaseTransform
    {
        private const string MonoBehaviourName = "MonoBehaviour";
        private const string NetworkBehaviourName = "NetworkBehaviour";
        private const string CoreNamespace = "RTMPE.Core";
        private const string UnityEngineNamespace = "UnityEngine";

        public static CompilationUnitSyntax Apply(CompilationUnitSyntax root, ClassDeclarationSyntax target)
            => Apply(root, target, out _);

        /// <summary>
        /// As <see cref="Apply(CompilationUnitSyntax, ClassDeclarationSyntax)"/>,
        /// reporting why an unchanged result was unchanged. A <c>null</c> reason
        /// means the type already sits on <c>NetworkBehaviour</c> — the idempotent
        /// case, not a refusal.
        /// </summary>
        public static CompilationUnitSyntax Apply(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, out string refusalReason)
        {
            refusalReason = null;
            if (root is null || target is null)
            {
                return root;
            }

            if (!TransformPreconditions.TargetBelongsToRoot(root, target))
            {
                refusalReason = TransformPreconditions.ForeignTargetRefusal;
                return root;
            }

            var rebased = RebaseBaseList(target);
            if (ReferenceEquals(rebased, target))
            {
                // No MonoBehaviour base to swap: make no edit at all — in
                // particular, do not import RTMPE.Core a type does not yet need.
                // The two readings are distinguished, because telling an author
                // their plain class is "already converted" is a false statement
                // about the one fact the verb exists to change.
                refusalReason = AlreadyRebased(target)
                    ? null
                    : "the type does not derive from MonoBehaviour — there is no base to swap, so "
                        + "rebasing it would change what the type is rather than how it replicates";
                return root;
            }

            // Scope is read from the original declaration: swapping the base does not
            // move the type between namespaces, so the ancestors that bring an import
            // into scope are the same before and after the replacement.
            var replaced = root.ReplaceNode(target, rebased);
            // Shared scope-aware import management — the same helper the
            // NetworkVariable and RPC transforms use, so `using` semantics can
            // never drift between the conversions.
            return ImportEditing.IsInScope(target, CoreNamespace)
                ? replaced
                : ImportEditing.AddFileLevelImport(replaced, CoreNamespace);
        }

        /// <summary>
        /// What a type with no networked surface has to do about it — and which of
        /// the two, because they are not interchangeable: this transform SWAPS a
        /// MonoBehaviour base and refuses a type that has none, so a caller pairing
        /// the wrong remedy with the wrong cause sends its author to a verb that
        /// will refuse them.
        /// </summary>
        public enum RebaseNeed
        {
            /// <summary>Nothing here says the type is not networked.</summary>
            No,

            /// <summary>It sits on Unity's own base; this transform replaces it.</summary>
            SwapMonoBehaviour,

            /// <summary>
            /// It sits on nothing, or on the root of the type system. There is no
            /// base to swap, so the declaration is what has to change — inventing
            /// one here would alter what the type is rather than how it replicates.
            /// </summary>
            DeclareABase,

            /// <summary>
            /// It sits on a <c>NetworkBehaviour</c> that is not the SDK's: another
            /// framework's, by its namespace, or the bare name in a file that never
            /// imports <c>RTMPE.Core</c>. 🔴 Read as "already rebased" until this
            /// arm existed, so a Netcode-for-GameObjects type — the migrator's own
            /// starting point — was converted, given a spawn hook onto NGO's base,
            /// and recorded in a ledger, exit 0.
            /// </summary>
            ForeignNetworkBehaviour,
        }

        /// <summary>
        /// Whether <paramref name="type"/> carries no networked surface to build on:
        /// it sits on Unity's own <c>MonoBehaviour</c>, or on no base type at all.
        /// Either way the SDK members a conversion emits — <c>OnNetworkSpawn</c>, the
        /// <c>NetworkBehaviour</c> a variable is constructed against — are not in
        /// scope, so a caller planning one has nothing to plan against.
        /// </summary>
        /// <remarks>
        /// Only those readings are decidable from syntax. Any other base is a name
        /// this pass cannot resolve, and is reported as <c>false</c> — unknown,
        /// never "does not need it" — so a caller must keep whatever conservative
        /// treatment it already gives an unrecognised chain. A base list naming the
        /// SDK type, or anything else, falls there by the same rule.
        /// <para>
        /// ⛔ One decidable-looking case is not decided: a base list whose first
        /// entry is an INTERFACE means the type has no base class, and C# does not
        /// let syntax tell an interface from a class by name. A convention would,
        /// and a convention is not a fact — so such a type stays unknown, and its
        /// caller's conservative arm keeps it.
        /// </para>
        /// </remarks>
        public static RebaseNeed RebaseNeededBy(ClassDeclarationSyntax type)
        {
            if (type is null)
            {
                return RebaseNeed.No;
            }

            if (type.BaseList is null)
            {
                // ⚠️ Absent is only decisive for a whole declaration. A `partial`
                // part may leave the base to a sibling part in another file, which
                // one syntax tree cannot see — so the absence is read as unknown
                // there, and a legitimate chain is never refused for being split.
                return type.Modifiers.Any(SyntaxKind.PartialKeyword)
                    ? RebaseNeed.No
                    : RebaseNeed.DeclareABase;
            }

            if (type.BaseList.Types.Any(entry => IsMonoBehaviourBase(entry.Type)))
            {
                return RebaseNeed.SwapMonoBehaviour;
            }

            if (type.BaseList.Types.Any(entry => IsForeignNetworkBehaviourBase(entry.Type, type)))
            {
                return RebaseNeed.ForeignNetworkBehaviour;
            }

            return type.BaseList.Types.Any(entry => IsSystemObjectBase(entry.Type))
                ? RebaseNeed.DeclareABase
                : RebaseNeed.No;
        }

        // The idempotent reading of "nothing to swap": the type is already on the
        // SDK base. Matched by the same two trusted spellings the MonoBehaviour
        // test uses, so the two readings cannot disagree about one base list.
        private static bool AlreadyRebased(ClassDeclarationSyntax type)
            => type.BaseList?.Types.Any(t => IsSdkNetworkBehaviourBase(t.Type, type)) == true;

        // The SDK's own base, by the two spellings a syntactic pass can trust: the
        // qualified name under RTMPE.Core, or the bare name in a file that imports
        // RTMPE.Core (or sits inside it). ⛔ The bare name ALONE is not enough — a
        // file that imports Unity.Netcode and never RTMPE.Core spells a different
        // type with the same word, and the declared Unity floor (C# 9) has no
        // global usings that could bring the SDK's in from another file.
        private static bool IsSdkNetworkBehaviourBase(TypeSyntax candidate, ClassDeclarationSyntax type)
            => candidate switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText == NetworkBehaviourName
                    && ImportsCore(type),
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == NetworkBehaviourName
                    && NamespaceText(qualified.Left) == CoreNamespace,
                _ => false,
            };

        // The same word under another namespace, or bare with no way to be ours.
        private static bool IsForeignNetworkBehaviourBase(TypeSyntax candidate, ClassDeclarationSyntax type)
            => candidate switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText == NetworkBehaviourName
                    && !ImportsCore(type),
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == NetworkBehaviourName
                    && NamespaceText(qualified.Left) != CoreNamespace,
                _ => false,
            };

        // Whether `NetworkBehaviour` written bare in this declaration can resolve
        // to the SDK's: a plain `using RTMPE.Core;` in scope (a `using static` or an
        // alias imports no type by that name), or the type declared inside
        // RTMPE.Core or a namespace beneath it.
        private static bool ImportsCore(ClassDeclarationSyntax type)
        {
            foreach (var ancestor in type.AncestorsAndSelf())
            {
                var usings = ancestor switch
                {
                    CompilationUnitSyntax unit => unit.Usings,
                    BaseNamespaceDeclarationSyntax ns => ns.Usings,
                    _ => default,
                };

                foreach (var directive in usings)
                {
                    if (directive.Alias is null
                        && !directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                        && NamespaceText(directive.Name) == CoreNamespace)
                    {
                        return true;
                    }
                }

                if (ancestor is BaseNamespaceDeclarationSyntax declared)
                {
                    string enclosing = string.Join(".", declared.AncestorsAndSelf()
                        .OfType<BaseNamespaceDeclarationSyntax>()
                        .Reverse()
                        .Select(n => NamespaceText(n.Name)));
                    if (enclosing == CoreNamespace || enclosing.StartsWith(CoreNamespace + ".", System.StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static ClassDeclarationSyntax RebaseBaseList(ClassDeclarationSyntax type)
        {
            var monoBase = type.BaseList?.Types.FirstOrDefault(t => IsMonoBehaviourBase(t.Type));
            if (monoBase is null)
            {
                return type; // already NetworkBehaviour, or no MonoBehaviour base to swap
            }

            // Only the name changes — the original spacing around the base type is
            // kept, and any `UnityEngine.` qualifier is dropped in favour of the using.
            var networkName = SyntaxFactory.IdentifierName(NetworkBehaviourName).WithTriviaFrom(monoBase.Type);
            return type.ReplaceNode(monoBase, monoBase.WithType(networkName));
        }

        // The root of the type system, named explicitly. Deriving from it is the
        // same statement as deriving from nothing — and unlike an arbitrary
        // identifier it is decidable, because no user type may take these names:
        // `object` is a keyword, and `System.Object` is the type it aliases.
        private static bool IsSystemObjectBase(TypeSyntax type)
        {
            switch (type)
            {
                case PredefinedTypeSyntax predefined:
                    return predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword);
                case QualifiedNameSyntax qualified:
                    return qualified.Right.Identifier.ValueText == "Object"
                        && NamespaceText(qualified.Left) == "System";
                default:
                    return false;
            }
        }

        // True only for the two spellings of Unity's own MonoBehaviour a syntactic
        // rewrite can trust: the unqualified name, or `UnityEngine.MonoBehaviour`. A
        // same-named base under another namespace is a different type, left alone
        // rather than silently disinherited by matching on the short name alone.
        private static bool IsMonoBehaviourBase(TypeSyntax type)
        {
            switch (type)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText == MonoBehaviourName;
                case QualifiedNameSyntax qualified:
                    return qualified.Right.Identifier.ValueText == MonoBehaviourName
                        && NamespaceText(qualified.Left) == UnityEngineNamespace;
                default:
                    return false;
            }
        }

        // A name without an optional `global::` qualifier, so a namespace is matched
        // whichever way it is spelled.
        private static string NamespaceText(NameSyntax name)
            => name?.ToString().Replace("global::", string.Empty);
    }
}
