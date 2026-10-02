using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Analyzers;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Analysis
{
    /// <summary>One node of the project dependency graph: a concrete component type.</summary>
    public sealed class AuthorityGraphNode
    {
        internal AuthorityGraphNode(string typeName, AuthoritySignals signals)
        {
            TypeName = typeName;
            Signals = signals;
        }

        /// <summary>The fully-qualified type name (the stable sort key).</summary>
        public string TypeName { get; }

        /// <summary>The intrinsic authority signals the classifier consumes.</summary>
        public AuthoritySignals Signals { get; }
    }

    /// <summary>The code-reference dependency graph over one compilation's component types.</summary>
    public sealed class AuthorityGraph
    {
        internal static readonly AuthorityGraph Empty =
            new AuthorityGraph(Array.Empty<AuthorityGraphNode>(), Array.Empty<AuthorityEdge>());

        internal AuthorityGraph(IReadOnlyList<AuthorityGraphNode> nodes, IReadOnlyList<AuthorityEdge> edges)
        {
            Nodes = nodes;
            Edges = edges;
        }

        /// <summary>Every node, ordered by type name (ordinal).</summary>
        public IReadOnlyList<AuthorityGraphNode> Nodes { get; }

        /// <summary>Every de-duplicated directed edge, ordered by (from, to, kind).</summary>
        public IReadOnlyList<AuthorityEdge> Edges { get; }
    }

    /// <summary>
    /// Builds the Phase-5 dependency graph: nodes are the compilation's concrete
    /// source component types (MonoBehaviour/NetworkBehaviour subclasses), edges
    /// are the code references between them — [RequireComponent], component-typed
    /// members, GetComponent-family lookups, and reads/invocations/writes through
    /// node-typed expressions. Code-reference-only by design (DD-P5-2): scene and
    /// prefab references have no in-repo ground truth and are explicitly deferred.
    /// SDK and Unity types are signals on a node, never nodes themselves.
    /// </summary>
    public static class DependencyGraphBuilder
    {
        public static AuthorityGraph Build(Compilation compilation)
        {
            if (compilation is null) throw new ArgumentNullException(nameof(compilation));
            return Build(compilation, AuthoritySdkSymbols.Resolve(compilation));
        }

        internal static AuthorityGraph Build(Compilation compilation, AuthoritySdkSymbols sdk)
        {
            if (!sdk.HasUnitySurface)
            {
                return AuthorityGraph.Empty;
            }

            var nodeSymbols = SourceTypes(compilation)
                .Where(sdk.IsProjectNode)
                .OrderBy(FullName, StringComparer.Ordinal)
                .ToList();

            var nodeNameBySymbol = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            foreach (var symbol in nodeSymbols)
            {
                nodeNameBySymbol[symbol] = FullName(symbol);
            }

            // One model cache for the pass: every node revisits its file to bind a
            // model both while extracting its signals and while collecting its
            // edges, and several nodes share a file, so a single cache turns
            // per-node-per-file rebinding into once per file.
            var models = new SemanticModelCache(compilation);

            var nodes = nodeSymbols
                .Select(s => new AuthorityGraphNode(
                    nodeNameBySymbol[s], AuthoritySignalExtractor.Extract(s, models, sdk)))
                .ToList();

            var edges = new HashSet<(string From, string To, AuthorityEdgeKind Kind)>();
            foreach (var symbol in nodeSymbols)
            {
                CollectEdges(symbol, nodeNameBySymbol[symbol], models, sdk, nodeNameBySymbol, edges);
            }

            var orderedEdges = edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => (int)e.Kind)
                .Select(e => new AuthorityEdge(e.From, e.To, e.Kind))
                .ToList();

            return new AuthorityGraph(nodes, orderedEdges);
        }

        private static void CollectEdges(
            INamedTypeSymbol type,
            string fromName,
            SemanticModelCache models,
            AuthoritySdkSymbols sdk,
            IReadOnlyDictionary<INamedTypeSymbol, string> nodeNameBySymbol,
            ISet<(string, string, AuthorityEdgeKind)> edges)
        {
            // Member edges are visible on the symbol without touching syntax. A
            // static member couples the holder to the node it carries exactly as an
            // instance member does — a singleton reference (`static Player
            // _instance`) is a coupling, not an exception — so statics are no longer
            // filtered out. A property's compiler-generated backing field still is:
            // its AssociatedSymbol is the property, which is read on its own arm.
            foreach (var member in type.GetMembers())
            {
                var memberType = member switch
                {
                    IFieldSymbol field when field.AssociatedSymbol is null => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => null,
                };

                foreach (var held in HeldNodeTypes(memberType, nodeNameBySymbol))
                {
                    edges.Add((fromName, held, AuthorityEdgeKind.Member));
                }
            }

            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not TypeDeclarationSyntax declaration)
                {
                    continue;
                }

                var model = models.GetSemanticModel(declaration.SyntaxTree);

                // [RequireComponent(typeof(T))] — the attribute name is matched
                // syntactically (it resolves in Unity but may not in a stub) and
                // the typeof argument is resolved semantically, so only a real
                // project type produces an edge.
                foreach (var attributeList in declaration.AttributeLists)
                {
                    foreach (var attribute in attributeList.Attributes)
                    {
                        if (AttributeSimpleName(attribute.Name) is not ("RequireComponent" or "RequireComponentAttribute"))
                        {
                            continue;
                        }

                        foreach (var argument in attribute.ArgumentList?.Arguments
                                     ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>))
                        {
                            if (argument.Expression is TypeOfExpressionSyntax typeOf
                                && model.GetSymbolInfo(typeOf.Type).Symbol is INamedTypeSymbol required
                                && nodeNameBySymbol.TryGetValue(required, out var requiredTarget))
                            {
                                edges.Add((fromName, requiredTarget, AuthorityEdgeKind.RequireComponent));
                            }
                        }
                    }
                }

                // Stop descent at any nested type declaration (class/struct/record/
                // interface/enum) — a nested type is its own node and collects its
                // own edges; its lookups/accesses must not leak in as edges of the
                // enclosing type (mirrors the signal extractor's OwnNodes guard).
                foreach (var node in declaration.DescendantNodes(n => n == declaration || n is not BaseTypeDeclarationSyntax))
                {
                    switch (node)
                    {
                        // GetComponent<T>()-family lookups: the invocation itself may
                        // not bind (an unresolved receiver), but the generic type
                        // argument still resolves — exactly how the samples reference
                        // one another.
                        case InvocationExpressionSyntax invocation
                            when GenericCallee(invocation.Expression) is GenericNameSyntax generic
                                && IsComponentLookupName(generic.Identifier.ValueText):
                        {
                            foreach (var typeArgument in generic.TypeArgumentList.Arguments)
                            {
                                if (model.GetSymbolInfo(typeArgument).Symbol is INamedTypeSymbol lookedUp
                                    && nodeNameBySymbol.TryGetValue(lookedUp, out var lookupTarget))
                                {
                                    edges.Add((fromName, lookupTarget, AuthorityEdgeKind.ComponentLookup));
                                }
                            }

                            break;
                        }

                        // Reads, invocations, writes, and event subscriptions through
                        // an expression typed as another node. `this.` accesses are
                        // intra-type, not edges.
                        case MemberAccessExpressionSyntax access
                            when access.Expression is not ThisExpressionSyntax:
                        {
                            if (model.GetTypeInfo(access.Expression).Type is not INamedTypeSymbol receiver)
                            {
                                break;
                            }

                            // A receiver typed as a concrete node binds directly. One
                            // typed as an abstract base or an interface names no single
                            // node — the instance behind it is one of the types that
                            // satisfy it — so it resolves to the lone project node that
                            // does; a receiver several nodes could satisfy stays
                            // unresolved, the fail-safe under-report over a guessed owner.
                            if (!TryResolveNode(receiver, nodeNameBySymbol, out var accessTarget))
                            {
                                break;
                            }

                            var kind = AccessKind(access, model, sdk, nodeNameBySymbol);

                            // A receiver of this type's own node is navigation between
                            // two peer instances, not a coupling to a second node — save
                            // a replicated write, where one instance mutating another's
                            // networked state of the same type is the cross-instance
                            // hazard the mismatch advisory exists to surface.
                            if (accessTarget == fromName && kind != AuthorityEdgeKind.WriteReplicated)
                            {
                                break;
                            }

                            edges.Add((fromName, accessTarget, kind));
                            break;
                        }

                        // A null-conditional receiver — `other?.Method()`,
                        // `other?.Field` — couples to its node exactly as the plain
                        // form does; the guard changes when the access runs, not
                        // what it reaches. The `?.` chain is a ConditionalAccess,
                        // not a MemberAccess, so it needs its own arm.
                        case ConditionalAccessExpressionSyntax conditional
                            when conditional.Expression is not ThisExpressionSyntax:
                        {
                            if (model.GetTypeInfo(conditional.Expression).Type is not INamedTypeSymbol condReceiver
                                || !nodeNameBySymbol.TryGetValue(condReceiver, out var condTarget)
                                || condTarget == fromName)
                            {
                                break;
                            }

                            edges.Add((fromName, condTarget, ConditionalAccessKind(conditional)));
                            break;
                        }
                    }
                }
            }
        }

        // The node types a member declaration holds. A member reaches a node
        // directly (`PlayerController _player`), through an array, or through a
        // generic's type arguments — `List<PlayerController> _players` couples
        // this type to PlayerController exactly as a single field would, and the
        // orchestrators this edge exists to describe hold their children in
        // collections far more often than in one-per-field. Mirrors the unwrapping
        // the signal extractor already applies when it looks for held GameObjects.
        //
        // Recursion covers the nestings that occur in practice
        // (`Dictionary<int, PlayerController[]>`); a generic that is itself a node
        // yields itself and its arguments both, since holding one is a coupling
        // either way.
        private static IEnumerable<string> HeldNodeTypes(
            ITypeSymbol memberType, IReadOnlyDictionary<INamedTypeSymbol, string> nodeNameBySymbol)
        {
            switch (memberType)
            {
                case IArrayTypeSymbol array:
                    foreach (var held in HeldNodeTypes(array.ElementType, nodeNameBySymbol))
                    {
                        yield return held;
                    }

                    break;

                case INamedTypeSymbol named:
                    if (nodeNameBySymbol.TryGetValue(named, out var target))
                    {
                        yield return target;
                    }

                    foreach (var argument in named.TypeArguments)
                    {
                        foreach (var held in HeldNodeTypes(argument, nodeNameBySymbol))
                        {
                            yield return held;
                        }
                    }

                    break;
            }
        }

        // How the referencing type uses the node it reached.
        //
        // Invocation is read at the access itself: only `other.Method()` calls
        // into the node, while a call further along the chain targets whatever
        // intermediate member it hangs off — `other.Variable.Value.ToString()`
        // is a read of the node, not a call on it.
        //
        // Mutation is read at the end of the chain instead, because the SDK
        // spells a replicated write `other.Variable.Value = x`: the assignment
        // sits one hop above the access that carries the node type, so judging
        // it beside the access sees only the intervening member and reports
        // observation for the very cross-object write the authority advisory
        // exists to surface.
        private static AuthorityEdgeKind AccessKind(
            MemberAccessExpressionSyntax access, SemanticModel model, AuthoritySdkSymbols sdk,
            IReadOnlyDictionary<INamedTypeSymbol, string> nodeNameBySymbol)
        {
            if (access.Parent is InvocationExpressionSyntax invocation && invocation.Expression == access)
            {
                return AuthorityEdgeKind.Invoke;
            }

            ExpressionSyntax chain = access;
            while (chain.Parent is MemberAccessExpressionSyntax outer && outer.Expression == chain)
            {
                chain = outer;
            }

            // An access that is itself a hop TO another node — its own value is a
            // node and the chain continues through it — only navigates to the next
            // hop; the operation the chain performs belongs to the deepest node,
            // which is visited on its own. Judging this intermediate by the chain's
            // terminal verdict would attribute a write (or a mutation) to a type
            // this one merely reads to reach the owner, and surface the mismatch
            // advisory against it. Reading it is exactly what happens.
            if (chain != access
                && model.GetTypeInfo(access).Type is INamedTypeSymbol accessType
                && TryResolveNode(accessType, nodeNameBySymbol, out _))
            {
                return AuthorityEdgeKind.Observe;
            }

            switch (chain.Parent)
            {
                case AssignmentExpressionSyntax assignment when assignment.Left == chain:
                    return IsSubscription(assignment, model)
                        ? AuthorityEdgeKind.Observe
                        : WriteKind(chain, model, sdk);

                case PrefixUnaryExpressionSyntax:
                case PostfixUnaryExpressionSyntax:
                    return SyntaxShapes.IsIncrementOrDecrement(chain.Parent)
                        ? WriteKind(chain, model, sdk)
                        : AuthorityEdgeKind.Observe;

                default:
                    return AuthorityEdgeKind.Observe;
            }
        }

        // How a null-conditional receiver is used. A direct call on it —
        // `other?.Method(...)`, where the guarded expression is an invocation of
        // the receiver's own member — is an invocation of the node; every other
        // shape is read as observation. The bound is deliberate: a replicated
        // write reached through `?.` (`other?.Variable.Value = x`, a rare spelling)
        // records the dependency without surfacing the write, so the conditional
        // arm never manufactures a mismatch advisory the plain arm would qualify
        // more carefully. Under-reporting a coupling is the fail-safe direction.
        private static AuthorityEdgeKind ConditionalAccessKind(ConditionalAccessExpressionSyntax conditional)
            => conditional.WhenNotNull is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberBindingExpressionSyntax
                    ? AuthorityEdgeKind.Invoke
                    : AuthorityEdgeKind.Observe;

        // A cross-object write is replicated only when its target is a
        // NetworkVariable's Value — `other.Variable.Value = x`, the one write the
        // SDK actually propagates to other clients. The write target is the end of
        // the chain, so its receiver is the member being assigned through: a
        // receiver deriving from NetworkVariableBase makes the write replicated. A
        // write to a plain member of a networked type (`other.Score = x`) touches
        // only the local copy, so it stays a generic Write — recorded as a
        // dependency, never surfaced as writing networked state.
        private static AuthorityEdgeKind WriteKind(
            ExpressionSyntax target, SemanticModel model, AuthoritySdkSymbols sdk)
            => target is MemberAccessExpressionSyntax member
                && model.GetTypeInfo(member.Expression).Type is INamedTypeSymbol carrier
                && DerivesFrom(carrier, sdk.NetworkVariableBase)
                    ? AuthorityEdgeKind.WriteReplicated
                    : AuthorityEdgeKind.Write;

        // Walks the base chain including the type itself, so a member typed as a
        // NetworkVariable subclass — or NetworkVariableBase directly — is caught.
        // A null base (the SDK sync surface unreferenced) derives from nothing.
        private static bool DerivesFrom(ITypeSymbol type, INamedTypeSymbol baseType)
        {
            if (baseType is null)
            {
                return false;
            }

            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }

        // The node a type names, whether it names one directly or stands for the
        // single project node behind an abstraction. Every site that turns a type
        // into a node resolves through here, so a chain typed at its abstraction is
        // judged the same way a concrete one is; two sites resolving differently
        // let a hop count as navigation in one place and as its endpoint in another.
        private static bool TryResolveNode(
            INamedTypeSymbol type,
            IReadOnlyDictionary<INamedTypeSymbol, string> nodeNameBySymbol,
            out string target)
            => nodeNameBySymbol.TryGetValue(type, out target)
                || TryResolveNodeReceiver(type, nodeNameBySymbol, out target);

        // Resolves a receiver typed as an abstract base or interface to the node it
        // couples to. Such a receiver names no single node — its runtime instance is
        // one of the concrete types deriving from the base or implementing the
        // interface — so the coupling is attributed only when exactly one project
        // node qualifies. None, or more than one, leaves it unresolved: attributing
        // the edge to a guessed owner over-reports, and to all candidates fans a
        // single write out across the set, so the builder keeps its standing
        // under-report instead. A concrete receiver is already handled directly.
        private static bool TryResolveNodeReceiver(
            INamedTypeSymbol receiver,
            IReadOnlyDictionary<INamedTypeSymbol, string> nodeNameBySymbol,
            out string target)
        {
            target = null;
            if (receiver.TypeKind != TypeKind.Interface && !receiver.IsAbstract)
            {
                return false;
            }

            // The abstraction has to be one the project declares. A framework
            // interface a node merely happens to implement — IDisposable,
            // IComparable, IEnumerable — expresses no design relationship, so
            // resolving through it would couple every unrelated use of that
            // interface to whichever node implements it: the guessed owner this
            // method refuses everywhere else, arrived at from the other direction.
            if (!receiver.Locations.Any(l => l.IsInSource))
            {
                return false;
            }

            foreach (var pair in nodeNameBySymbol)
            {
                bool satisfies = receiver.TypeKind == TypeKind.Interface
                    ? pair.Key.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, receiver))
                    : DerivesFrom(pair.Key, receiver);
                if (!satisfies)
                {
                    continue;
                }

                if (target is not null)
                {
                    target = null;
                    return false;
                }

                target = pair.Value;
            }

            return target is not null;
        }

        // `+=`/`-=` is an event subscription on an event and a compound write on
        // anything else; the bound symbol decides, not the operator. A target
        // that does not bind is read as observation, so an unresolved expression
        // can never manufacture a cheat advisory.
        private static bool IsSubscription(AssignmentExpressionSyntax assignment, SemanticModel model)
        {
            if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)
                && !assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
            {
                return false;
            }

            var target = model.GetSymbolInfo(assignment.Left).Symbol;
            return target is null || target is IEventSymbol;
        }

        // `!x`, `-x` and `~x` share the unary node shapes with `++`/`--` and read
        // rather than mutate, so the operator kind is checked, not the node type.
        private static GenericNameSyntax GenericCallee(ExpressionSyntax callee)
            => callee switch
            {
                GenericNameSyntax generic => generic,
                MemberAccessExpressionSyntax access => access.Name as GenericNameSyntax,
                MemberBindingExpressionSyntax binding => binding.Name as GenericNameSyntax,
                _ => null,
            };

        // A generic lookup whose type argument names another node. This mirrors the
        // signal extractor's ComponentAcquisitionNames — the two describe the same
        // set of scene-object spellings and must stay aligned, so Instantiate<T>,
        // FindObjectOfType<T> and FindObjectsOfType<T> are recognised here as they
        // are there: each names a node in its type argument exactly as
        // GetComponent<T> does. (The lists are kept apart, not shared, because the
        // extractor ships inside the analyzer package and the graph does not, so
        // one would drag the other's assembly into every edit.)
        private static bool IsComponentLookupName(string name)
            => name is "GetComponent" or "GetComponentInChildren" or "GetComponentInParent"
                or "GetComponentsInChildren" or "GetComponentsInParent" or "AddComponent"
                or "Instantiate" or "FindObjectOfType" or "FindObjectsOfType";

        private static string AttributeSimpleName(NameSyntax name)
            => name switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                _ => null,
            };

        // The same fully-qualified display format the readiness scorer keys on,
        // so a graph node and a scored type share one name.
        private static readonly SymbolDisplayFormat FullNameFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

        private static string FullName(INamedTypeSymbol type) => type.ToDisplayString(FullNameFormat);

        private static IEnumerable<INamedTypeSymbol> SourceTypes(Compilation compilation)
            => DescendTypes(compilation.Assembly.GlobalNamespace)
                .Where(t => t.Locations.Any(l => l.IsInSource));

        private static IEnumerable<INamedTypeSymbol> DescendTypes(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    foreach (var type in DescendTypes(childNamespace))
                    {
                        yield return type;
                    }
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in NestedTypes(type))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in NestedTypes(nested))
                {
                    yield return deeper;
                }
            }
        }
    }
}
