using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// The SDK surface the authority signals key off, resolved once per
    /// compilation by metadata name — never by simple-name matching. Any member
    /// may be null when the project does not reference that part of the surface;
    /// the extractor degrades to "signal absent" rather than guessing.
    /// </summary>
    public sealed class AuthoritySdkSymbols
    {
        private AuthoritySdkSymbols(
            INamedTypeSymbol monoBehaviour,
            INamedTypeSymbol networkBehaviour,
            INamedTypeSymbol networkVariableBase,
            INamedTypeSymbol gameObject,
            INamedTypeSymbol rtmpeRpcAttribute,
            IPropertySymbol isOwner,
            IReadOnlyList<INamedTypeSymbol> motionReceivers,
            IReadOnlyList<INamedTypeSymbol> motionReplicators,
            INamedTypeSymbol networkObjectPool)
        {
            MonoBehaviour = monoBehaviour;
            NetworkBehaviour = networkBehaviour;
            NetworkVariableBase = networkVariableBase;
            GameObject = gameObject;
            RtmpeRpcAttribute = rtmpeRpcAttribute;
            IsOwner = isOwner;
            MotionReceivers = motionReceivers;
            MotionReplicators = motionReplicators;
            NetworkObjectPool = networkObjectPool;
        }

        public INamedTypeSymbol MonoBehaviour { get; }
        public INamedTypeSymbol NetworkBehaviour { get; }
        public INamedTypeSymbol NetworkVariableBase { get; }
        public INamedTypeSymbol GameObject { get; }
        public INamedTypeSymbol RtmpeRpcAttribute { get; }
        public IPropertySymbol IsOwner { get; }

        /// <summary>
        /// The engine types whose members move an object, as this compilation
        /// binds them — every one the contract declares, so a headless score and
        /// the IDE compare the same symbols.
        /// </summary>
        public IReadOnlyList<INamedTypeSymbol> MotionReceivers { get; }

        /// <summary>
        /// The SDK's motion replicators, as this compilation binds them — empty in
        /// a headless score, where the SDK assembly is not referenced and the
        /// reading falls back to the name.
        /// </summary>
        public IReadOnlyList<INamedTypeSymbol> MotionReplicators { get; }

        internal static readonly string[] MotionReceiverNames =
        {
            "UnityEngine.Transform", "UnityEngine.Rigidbody", "UnityEngine.Rigidbody2D",
            "UnityEngine.CharacterController", "UnityEngine.AI.NavMeshAgent",
        };

        internal static readonly string[] MotionReplicatorNames =
        {
            "RTMPE.Sync.NetworkTransform", "RTMPE.Sync.NetworkRigidbody", "RTMPE.Sync.NetworkRigidbody2D",
        };

        /// <summary>
        /// <c>RTMPE.Core.INetworkObjectPool</c> — null in a headless score, where the
        /// pool reading falls back to the interface's name.
        /// </summary>
        public INamedTypeSymbol NetworkObjectPool { get; }

        /// <summary>Without MonoBehaviour there are no component types to classify.</summary>
        public bool HasUnitySurface => MonoBehaviour is not null;

        public static AuthoritySdkSymbols Resolve(Compilation compilation)
        {
            var networkBehaviour = compilation.GetTypeByMetadataName("RTMPE.Core.NetworkBehaviour");
            return new AuthoritySdkSymbols(
                compilation.GetTypeByMetadataName("UnityEngine.MonoBehaviour"),
                networkBehaviour,
                compilation.GetTypeByMetadataName("RTMPE.Sync.NetworkVariableBase"),
                compilation.GetTypeByMetadataName("UnityEngine.GameObject"),
                compilation.GetTypeByMetadataName("RTMPE.Rpc.RtmpeRpcAttribute"),
                networkBehaviour?.GetMembers("IsOwner").OfType<IPropertySymbol>().FirstOrDefault(),
                Resolve(compilation, MotionReceiverNames),
                Resolve(compilation, MotionReplicatorNames),
                compilation.GetTypeByMetadataName("RTMPE.Core.INetworkObjectPool"));
        }

        private static IReadOnlyList<INamedTypeSymbol> Resolve(Compilation compilation, string[] metadataNames)
            => metadataNames
                .Select(compilation.GetTypeByMetadataName)
                .Where(type => type is not null)
                .ToList();

        /// <summary>
        /// Whether a type is one of a family, or derives from one — a class
        /// derived from <c>NetworkTransform</c> replicates what its base does, and
        /// <c>RectTransform</c> moves an object as the <c>Transform</c> it is.
        /// By symbol where the family binds in this compilation, by simple name
        /// where it does not — the replicators are outside the headless contract,
        /// and a name is the only reading available there; an unbound base
        /// reaches the chain as an error type carrying the name the author wrote.
        /// </summary>
        internal static bool IsOneOf(ITypeSymbol type, IReadOnlyList<INamedTypeSymbol> family, HashSet<string> names)
            => FamilyLink(type, family, names) is not null;

        /// <summary>
        /// The link of the type's base chain that is one of the family — the type
        /// itself, or the base through which it is one — or null.  A cycle in a
        /// chain is broken by the compiler with an error type whose base is null,
        /// so the walk ends.
        /// </summary>
        internal static ITypeSymbol FamilyLink(ITypeSymbol type, IReadOnlyList<INamedTypeSymbol> family, HashSet<string> names)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (family.Count > 0
                    ? family.Any(member => SymbolEqualityComparer.Default.Equals(member, current.OriginalDefinition))
                    : names.Contains(current.Name))
                {
                    return current;
                }
            }
            return null;
        }

        /// <summary>
        /// True for a type the dependency graph carries as a node: a concrete,
        /// non-static source class deriving from <c>UnityEngine.MonoBehaviour</c>
        /// (directly or through <c>NetworkBehaviour</c>).
        /// </summary>
        public bool IsProjectNode(INamedTypeSymbol type)
            => type is { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false }
                && type.Locations.Any(l => l.IsInSource)
                && DerivesFrom(type.BaseType, MonoBehaviour);

        internal static bool DerivesFrom(ITypeSymbol type, INamedTypeSymbol baseType)
        {
            if (baseType is null) return false;
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Computes one type's intrinsic authority signals (§2.6 of the Phase-5
    /// plan) from its symbol and declaring syntax. Read-only: no diagnostics,
    /// no edits, no disk. The pure rubric that consumes the result lives in
    /// <see cref="AuthorityClassifier"/> so the two cannot drift apart between
    /// the analyzer diagnostic and the readiness report.
    /// </summary>
    public static class AuthoritySignalExtractor
    {
        // The networked send spellings, matching ConversionOpportunityAnalyzer's
        // already-networked detection — both the legacy (SendRpc/RpcMethodId)
        // and enhanced (RPC/SendEnhancedRpc) shapes, because the shipping
        // samples use only the legacy one.
        private static readonly string[] NetworkedSendNames = { "SendRpc", "SendEnhancedRpc", "RPC" };
        private const string LegacyRpcIdTypeName = "RpcMethodId";

        private static readonly string[] LifecycleOverrideNames =
            { "OnNetworkSpawn", "OnNetworkDespawn", "OnOwnershipChanged", "OnFixedTick" };

        // Code that acquires or creates scene objects — wiring, as opposed to
        // reading an Inspector-injected reference.
        private static readonly string[] ComponentAcquisitionNames =
        {
            "GetComponent", "GetComponentInChildren", "GetComponentInParent",
            "GetComponentsInChildren", "GetComponentsInParent",
            "AddComponent", "Instantiate", "FindObjectOfType", "FindObjectsOfType",
        };

        public static AuthoritySignals Extract(INamedTypeSymbol type, Compilation compilation, AuthoritySdkSymbols sdk)
        {
            if (compilation is null) throw new System.ArgumentNullException(nameof(compilation));
            return Extract(type, new SemanticModelCache(compilation), sdk);
        }

        /// <summary>
        /// As <see cref="Extract(INamedTypeSymbol, Compilation, AuthoritySdkSymbols)"/>,
        /// binding semantic models through <paramref name="models"/> so a batch pass
        /// or an analyzer can share one cache across every type it extracts instead
        /// of rebinding each declaration's model per type.
        /// </summary>
        public static AuthoritySignals Extract(
            INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            if (type is null) throw new System.ArgumentNullException(nameof(type));
            if (models is null) throw new System.ArgumentNullException(nameof(models));
            if (sdk is null) throw new System.ArgumentNullException(nameof(sdk));

            bool inheritsNetworkBehaviour = AuthoritySdkSymbols.DerivesFrom(type.BaseType, sdk.NetworkBehaviour);
            var (createsNetworked, createsOther) = Instantiations(type, models, sdk);

            return new AuthoritySignals(
                inheritsNetworkBehaviour,
                CountNetworkVariables(type, sdk),
                HasOwnerGuardedMember(type, models, sdk),
                HasRpcSurface(type, models, sdk),
                OverridesNetworkLifecycle(type, sdk),
                WiresSceneObjects(type, models, sdk),
                UnguardedStateMutators(type, models, sdk),
                ReadPartially(type, models, sdk),
                WritesTransform(type, models, sdk),
                DeclaresMotionReplicator(type, models, sdk),
                CountPositionalState(type, sdk),
                MovesFromReplicatedState(type, models, sdk),
                PublishesTransform(type, models, sdk),
                createsNetworked,
                createsOther && !SpawnsOnTheNetwork(type, models, sdk));
        }

        // Every Unity Instantiate on the own chain, read the way RTMPE1030 reads it
        // (LocalInstantiation): whether one creates an object the source proves
        // carries a NetworkBehaviour, and whether one creates a scene object the
        // source cannot see into.  A pool is the spawn path rather than a bypass
        // of it, and nothing it creates counts; nor does a type in an assembly,
        // an editor type or a test the rule does not read, whose copies are made
        // on purpose; nor a call at which the author suppressed RTMPE1030 in the
        // source — they said that copy stays local, and the classification takes
        // them at their word as the warning does.
        private static (bool Networked, bool Other) Instantiations(
            INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            if (LocalInstantiation.IsNetworkObjectPool(type, sdk.NetworkObjectPool)
                || LocalInstantiation.IsExemptAssembly(type.ContainingAssembly)
                || LocalInstantiation.IsEditorType(type)
                || LocalInstantiation.IsTestType(type))
            {
                return (false, false);
            }

            bool networked = false, other = false;
            foreach (var declaration in OwnChain(type, sdk).SelectMany(current => TypeDeclarations(current, models)))
            {
                SemanticModel model = null;
                foreach (var invocation in OwnNodes(declaration).OfType<InvocationExpressionSyntax>())
                {
                    if (CalleeName(invocation.Expression) != "Instantiate")
                    {
                        continue;
                    }

                    model ??= models.GetSemanticModel(declaration.SyntaxTree);
                    if (LocalInstantiation.IsSuppressedInSource(
                            invocation, DiagnosticIds.InstantiatedNetworkBehaviour, model))
                    {
                        continue;
                    }

                    switch (LocalInstantiation.Classify(invocation, model, sdk.NetworkBehaviour, out _))
                    {
                        case InstantiationKind.NetworkedObject:
                            networked = true;
                            break;
                        case InstantiationKind.OtherObject:
                            other = true;
                            break;
                    }
                }
            }

            return (networked, other);
        }

        // Whether the own chain spawns anything on the network: a call to
        // SpawnManager.Spawn — by symbol where the SDK binds, and by name where it
        // does not, because a headless score declares no SpawnManager.  A name read
        // wrongly here silences advice, which is the quiet direction.
        private static bool SpawnsOnTheNetwork(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            foreach (var declaration in OwnChain(type, sdk).SelectMany(current => TypeDeclarations(current, models)))
            {
                SemanticModel model = null;
                foreach (var invocation in OwnNodes(declaration).OfType<InvocationExpressionSyntax>())
                {
                    if (CalleeName(invocation.Expression) != "Spawn")
                    {
                        continue;
                    }

                    model ??= models.GetSemanticModel(declaration.SyntaxTree);
                    var info = model.GetSymbolInfo(invocation);
                    var bound = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (bound is not IMethodSymbol method
                        || method.ContainingType?.ToDisplayString() == "RTMPE.Core.SpawnManager")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // The span every signal reads: the type itself and each base above it that
        // this project declares, stopping before the SDK's NetworkBehaviour and
        // Unity's MonoBehaviour. A member reached through inheritance is as present
        // on an instance as one declared locally, and `GetMembers()` returns only
        // what a type declares — so a signal that does not walk this would score
        // identical behaviour differently depending on which type in the chain
        // happens to spell it out.
        private static IEnumerable<INamedTypeSymbol> OwnChain(INamedTypeSymbol type, AuthoritySdkSymbols sdk)
        {
            for (var current = type;
                 current is not null
                     && !SymbolEqualityComparer.Default.Equals(current, sdk.NetworkBehaviour)
                     && !SymbolEqualityComparer.Default.Equals(current, sdk.MonoBehaviour);
                 current = current.BaseType)
            {
                yield return current;
            }
        }

        // Members typed as NetworkVariableBase subclasses, matching how the
        // readiness scorer counts replicated state.
        private static int CountNetworkVariables(INamedTypeSymbol type, AuthoritySdkSymbols sdk)
        {
            if (sdk.NetworkVariableBase is null) return 0;

            int count = 0;
            foreach (var current in OwnChain(type, sdk))
            {
                foreach (var member in current.GetMembers())
                {
                    var memberType = ReplicableMemberType(member);
                    if (memberType is not null && HoldsReplicatedState(memberType, sdk.NetworkVariableBase))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        // The types a member can hold: itself, and through an array or a
        // collection what it contains — the reading WiresSceneObjects already
        // makes for a GameObject, narrowed to types that ARE collections
        // (IEnumerable<T> implementers) so a delegate, a task or a lazy that names
        // a wrapper in its signature holds no state of its own.  A member is
        // counted once whatever it holds.
        private static System.Collections.Generic.IEnumerable<ITypeSymbol> HeldTypes(ITypeSymbol memberType)
        {
            yield return memberType;
            switch (memberType)
            {
                case IArrayTypeSymbol array:
                    foreach (var held in HeldTypes(array.ElementType)) yield return held;
                    break;
                case INamedTypeSymbol { IsGenericType: true } generic when IsCollection(generic):
                    foreach (var argument in generic.TypeArguments)
                        foreach (var held in HeldTypes(argument)) yield return held;
                    break;
            }
        }

        private static bool IsCollection(INamedTypeSymbol type)
            => type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
               || type.AllInterfaces.Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);

        /// <summary>
        /// Whether a member of this type holds replicated state — a
        /// <c>NetworkVariableBase</c> subclass itself, or an array or collection
        /// of one.  The one reading the authority signals and the readiness
        /// scorer's State dimension both make, so the two cannot disagree about a
        /// member.
        /// </summary>
        public static bool HoldsReplicatedState(ITypeSymbol memberType, INamedTypeSymbol networkVariableBase)
            => networkVariableBase is not null
               && HeldTypes(memberType).Any(held => AuthoritySdkSymbols.DerivesFrom(held, networkVariableBase));

        // The subset of the count above whose value is a position or a rotation.
        // Decided on the TYPE ARGUMENT of the NetworkVariable<T> /
        // NetworkVariableList<T> base rather than on the wrapper's name, so
        // NetworkVariableVector3, NetworkVariableListVector3 and a wrapper an
        // author derives from NetworkVariable<Vector3> answer alike, and a wrapper
        // named for a position that carries an int does not.
        // ⛔ The argument is compared by simple NAME — a departure from this file's
        // rule that type identity is semantic, made for the same reason
        // DeclaresMotionReplicator makes it: Vector3Int, Vector4 and Pose are not
        // in the headless contract, and a symbol comparison would call a tilemap
        // mover's Vector3Int wrapper "not positional" in every CLI score.
        private static int CountPositionalState(INamedTypeSymbol type, AuthoritySdkSymbols sdk)
        {
            if (sdk.NetworkVariableBase is null) return 0;

            int count = 0;
            foreach (var current in OwnChain(type, sdk))
            {
                foreach (var member in current.GetMembers())
                {
                    var memberType = ReplicableMemberType(member);
                    if (memberType is not null
                        && HeldTypes(memberType).Any(held =>
                               AuthoritySdkSymbols.DerivesFrom(held, sdk.NetworkVariableBase)
                               && CarriesAPosition(held)))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static readonly System.Collections.Generic.HashSet<string> PositionalValueTypes =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "Vector2", "Vector2Int", "Vector3", "Vector3Int", "Vector4", "Quaternion", "Pose",
            };

        // Only the SDK's two generic wrappers decide; a generic somewhere else on
        // the chain — a phantom-typed tag, a collection — carries no value of its
        // own for the type argument to describe.
        private static readonly System.Collections.Generic.HashSet<string> ValueWrapperDefinitions =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "NetworkVariable", "NetworkVariableList",
            };

        private static bool CarriesAPosition(ITypeSymbol memberType)
        {
            for (var current = memberType as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType
                    && current.TypeArguments.Length == 1
                    && ValueWrapperDefinitions.Contains(current.OriginalDefinition.Name)
                    && PositionalValueTypes.Contains(current.TypeArguments[0].Name))
                {
                    return true;
                }
            }
            return false;
        }

        private static ITypeSymbol ReplicableMemberType(ISymbol member)
        {
            switch (member)
            {
                case IFieldSymbol field when !field.IsStatic && field.AssociatedSymbol is null:
                    return field.Type;
                case IPropertySymbol property when !property.IsStatic:
                    return property.Type;
                default:
                    return null;
            }
        }

        // A declared method whose block body opens with the canonical owner
        // guard, in any spelling OwnerGuardSyntax accepts (including the
        // disjunction form `if (!IsOwner || x == null) return;`).
        private static bool HasOwnerGuardedMember(
            INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            if (sdk.IsOwner is null) return false;

            foreach (var current in OwnChain(type, sdk))
            {
                foreach (var declaration in TypeDeclarations(current, models))
                {
                    var model = models.GetSemanticModel(declaration.SyntaxTree);
                    foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                    {
                        if (method.Body?.Statements.FirstOrDefault() is IfStatementSyntax guard
                            && OwnerGuardSyntax.IsOwnerGuard(guard, model, sdk.IsOwner))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // A method any other script can call, which writes a NetworkVariable
        // without first establishing ownership.
        //
        // Three exclusions keep this from firing on correct code, and each is a
        // shape the toolchain itself recommends:
        //
        //  * A leading owner guard is the fix, so a guarded method is not a
        //    finding — including the disjunction spelling.
        //  * An [RtmpeRpc] method is the *other* fix the same advisory offers:
        //    routing the mutation through a Server-targeted RPC. Flagging it
        //    would report the remedy as the defect.
        //  * A method a caller outside the type cannot reach is governed by
        //    whatever guards its callers carry — the textbook shape is a guarded
        //    Update delegating to a private mover, and treating that mover as
        //    exposed would condemn the arrangement the samples teach.
        private static IReadOnlyList<string> UnguardedStateMutators(
            INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            var found = new List<string>();
            if (sdk.IsOwner is null)
            {
                return found;
            }

            foreach (var declaration in OwnChain(type, sdk).SelectMany(current => TypeDeclarations(current, models)))
            {
                var model = models.GetSemanticModel(declaration.SyntaxTree);
                foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    // An expression body carries no leading statement, so it can
                    // hold no guard — it is examined for the write and never for
                    // an exemption it has no room to express.
                    SyntaxNode body = (SyntaxNode)method.Body ?? method.ExpressionBody?.Expression;
                    if (body is null || !IsReachableFromOutside(method))
                    {
                        continue;
                    }

                    // Tested before the guard: an [RtmpeRpc] method is exempt
                    // whatever its body does, because the attribute *is* the
                    // remedy this list recommends. Reporting one would name the fix
                    // as the defect, whichever arm the write happens to sit in.
                    if (model.GetDeclaredSymbol(method) is IMethodSymbol symbol
                        && CarriesRpcAttribute(symbol, sdk.RtmpeRpcAttribute))
                    {
                        continue;
                    }

                    if (method.Body?.Statements.FirstOrDefault() is IfStatementSyntax guard
                        && OwnerGuardSyntax.IsOwnerGuard(guard, model, sdk.IsOwner))
                    {
                        // The guard fences the statements after it, never itself.
                        // Its branch is the one arm every non-owner runs, so a
                        // replicated write placed there is not merely unguarded —
                        // it is reachable *only* off-owner, which is the sharper
                        // form of the defect this list names.
                        if (WritesReplicatedState(guard.Statement, model, sdk))
                        {
                            found.Add(method.Identifier.ValueText);
                        }

                        continue;
                    }

                    if (WritesReplicatedState(body, model, sdk))
                    {
                        found.Add(method.Identifier.ValueText);
                    }
                }
            }

            // Ordered and de-duplicated: the report is byte-compared, and a
            // partial type hands its declarations over in whatever order the
            // compilation happens to hold them.
            found.Sort(StringComparer.Ordinal);
            return found.Distinct().ToList();
        }

        private static bool IsReachableFromOutside(MethodDeclarationSyntax method)
            => method.Modifiers.Any(SyntaxKind.PublicKeyword)
                || method.Modifiers.Any(SyntaxKind.InternalKeyword);

        private static bool CarriesRpcAttribute(IMethodSymbol method, INamedTypeSymbol rpcAttribute)
            => rpcAttribute is not null
                && method.GetAttributes().Any(a =>
                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, rpcAttribute));

        // The names through which a NetworkVariableList mutates its replicated
        // contents. Constructing the list is not among them, exactly as
        // constructing a NetworkVariable is not a write.
        private static readonly HashSet<string> ListMutatorNames =
            new HashSet<string>(StringComparer.Ordinal) { "Add", "Insert", "RemoveAt", "Remove", "Clear" };

        // A write THROUGH a NetworkVariable — `_hp.Value = x`, `-=`, `++` — or
        // through a NetworkVariableList — `_list[i] = x`, `_list.Add(x)`,
        // `_list?.Add(x)` — which is the operation the runtime replicates. A list
        // mutates through its indexer and its Add/Insert/Remove/RemoveAt/Clear
        // members rather than a `.Value` assignment, so those spellings are read
        // here too, including behind a null-conditional. Constructing either is
        // deliberately not a write: that belongs in OnNetworkSpawn and is the
        // sanctioned shape, not an unguarded mutation of live state.
        private static bool WritesReplicatedState(SyntaxNode body, SemanticModel model, AuthoritySdkSymbols sdk)
        {
            foreach (var node in body.DescendantNodesAndSelf())
            {
                ExpressionSyntax written = node switch
                {
                    AssignmentExpressionSyntax assignment => assignment.Left,
                    PrefixUnaryExpressionSyntax prefix when SyntaxShapes.IsIncrementOrDecrement(prefix) => prefix.Operand,
                    PostfixUnaryExpressionSyntax postfix when SyntaxShapes.IsIncrementOrDecrement(postfix) => postfix.Operand,
                    _ => null,
                };

                // `_hp.Value = x` carries the variable on the member's receiver;
                // `_list[i] = x` carries the list on the element access's receiver.
                var writtenCarrier = written switch
                {
                    MemberAccessExpressionSyntax access => access.Expression,
                    ElementAccessExpressionSyntax element => element.Expression,
                    _ => null,
                };

                if (writtenCarrier is not null
                    && model.GetTypeInfo(writtenCarrier).Type is INamedTypeSymbol carrier
                    && AuthoritySdkSymbols.DerivesFrom(carrier, sdk.NetworkVariableBase))
                {
                    return true;
                }

                // A mutating call on a NetworkVariableList replicates as surely as a
                // Value write; the receiver's type is what tells a list mutation from
                // a same-named call on an ordinary collection.
                if (node is InvocationExpressionSyntax invocation
                    && CalleeName(invocation.Expression) is string callee
                    && ListMutatorNames.Contains(callee)
                    && CalleeReceiver(invocation) is ExpressionSyntax listCarrierExpression
                    && model.GetTypeInfo(listCarrierExpression).Type is INamedTypeSymbol listCarrier
                    && AuthoritySdkSymbols.DerivesFrom(listCarrier, sdk.NetworkVariableBase))
                {
                    return true;
                }
            }

            return false;
        }

        // Both RPC shapes: a declared [RtmpeRpc] method (symbol equality against
        // the resolved attribute, never a name match), a SendRpc/SendEnhancedRpc/
        // RPC( invocation, or a reference to the legacy RpcMethodId constants.
        private static bool HasRpcSurface(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            foreach (var current in OwnChain(type, sdk))
            {
                if (sdk.RtmpeRpcAttribute is not null)
                {
                    foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (method.GetAttributes().Any(a =>
                                SymbolEqualityComparer.Default.Equals(a.AttributeClass, sdk.RtmpeRpcAttribute)))
                        {
                            return true;
                        }
                    }
                }

                foreach (var declaration in TypeDeclarations(current, models))
                {
                    foreach (var node in OwnNodes(declaration))
                    {
                        switch (node)
                        {
                            case InvocationExpressionSyntax invocation
                                when NetworkedSendNames.Contains(CalleeName(invocation.Expression)):
                                return true;

                            // RpcMethodId used as a type qualifier (RpcMethodId.RequestDamage)
                            // — a bare identifier (a local, a nameof operand) is not the
                            // legacy constants class.
                            case IdentifierNameSyntax identifier
                                when identifier.Identifier.ValueText == LegacyRpcIdTypeName
                                    && identifier.Parent is MemberAccessExpressionSyntax qualifier
                                    && qualifier.Expression == identifier:
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool OverridesNetworkLifecycle(INamedTypeSymbol type, AuthoritySdkSymbols sdk)
            => OwnChain(type, sdk).Any(current => current.GetMembers().OfType<IMethodSymbol>()
                .Any(m => m.IsOverride && LifecycleOverrideNames.Contains(m.Name)));

        // Wiring, not observation: assigning a component-typed member, holding a
        // GameObject member (a prefab/scene handle), or acquiring components in
        // code. An Inspector-injected reference that is only read or subscribed
        // to does not make a HUD an orchestrator.
        // The members a write to which MOVES the object, and the calls that move
        // it without naming one — on a Transform, a Rigidbody, a CharacterController
        // or a NavMeshAgent, which between them are how Unity code moves a thing.
        // What this must never do is answer YES where there is no motion, because
        // the verdict it drives asks the author a question — so it matches an
        // assignment to one of these members, or a call to one of these methods,
        // on a receiver that is a mover (see IsAMotionReceiver), and nothing looser.
        private static readonly string[] TransformWriteMembers =
        {
            "position", "localPosition", "rotation", "localRotation",
            "eulerAngles", "localEulerAngles", "localScale", "forward", "right", "up",
            "velocity", "linearVelocity", "angularVelocity", "destination",
        };

        private static readonly string[] TransformWriteCalls =
        {
            "Translate", "Rotate", "RotateAround", "SetPositionAndRotation",
            "SetLocalPositionAndRotation", "LookAt", "MovePosition", "MoveRotation",
            "AddForce", "AddTorque", "Move", "SimpleMove", "SetDestination", "Warp",
        };

        // The component types whose members above move the object.
        private static readonly HashSet<string> MotionReceiverTypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Transform", "Rigidbody", "Rigidbody2D", "CharacterController", "NavMeshAgent",
            };

        private static bool WritesTransform(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
            => TransformWrites(type, models, sdk).Any();

        // Every transform write on the chain, with the declaration it sits in so a
        // semantic question can be asked of it.  The same span every other signal
        // reads: the type and each base above it this project declares.
        private static System.Collections.Generic.IEnumerable<(SyntaxNode Write, TypeDeclarationSyntax Declaration)> TransformWrites(
            INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            foreach (var declaration in OwnChain(type, sdk).SelectMany(current => TypeDeclarations(current, models)))
            {
                var model = models.GetSemanticModel(declaration.SyntaxTree);
                foreach (var node in OwnNodes(declaration))
                {
                    switch (node)
                    {
                        case AssignmentExpressionSyntax assignment
                            when assignment.Left is MemberAccessExpressionSyntax member
                                 && TransformWriteMembers.Contains(member.Name.Identifier.ValueText)
                                 && IsAMotionReceiver(model, member.Expression, sdk):
                            yield return (assignment, declaration);
                            break;

                        case InvocationExpressionSyntax invocation
                            when invocation.Expression is MemberAccessExpressionSyntax call
                                 && TransformWriteCalls.Contains(call.Name.Identifier.ValueText)
                                 && IsAMotionReceiver(model, call.Expression, sdk):
                            yield return (invocation, declaration);
                            break;
                    }
                }
            }
        }

        // Whether any transform write is DRIVEN by replicated state: a value the
        // wire delivered reaches the write's source side — the assignment's
        // right-hand side, or the call's arguments.  The wire delivers a value
        // three ways, and each is a source: a member or element access on a
        // NetworkVariableBase receiver (`_lane.Value`, `_lane?.Value`, `_list[0]`,
        // `_list.Count`, the collection a `foreach` enumerates); a parameter of a
        // handler subscribed to such a member's event, which is how OnValueChanged
        // hands the new value on; and one place this type fills from either and
        // reads back — a local or parameter filled ahead of the write in the
        // member that holds both (inside a local function, anywhere in it) or
        // anywhere in a loop the two share, or a member reached from `this`, or
        // through one reference this type holds, filled in any body.  A door
        // turned by a bool and a turret aimed by a float move from the wire as
        // surely as a replica applying a Vector3 does.
        // 🔑 A fill names a PLACE, and so does a read: `_target.x = _lane.Value`
        // fills `_target`, because a component of a value is part of the value;
        // `_next._target = …` fills `_target` on `_next` and nothing on `this`;
        // `_other.health = …` fills that one member and leaves `_other.pos`
        // alone.  The place is the receiver and the member together, never the
        // member's symbol alone — a member's symbol is one symbol for every
        // instance of its type.
        // ⛔ One hop and the write's own source, not the whole body: a `.Value`
        // read for a HUD label beside the write must not silence a mover whose
        // position comes from nowhere.  A value that travels two places, or
        // through a helper's body, is not seen and the reading fires — the loud
        // direction.
        private static bool MovesFromReplicatedState(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            if (sdk.NetworkVariableBase is null) return false;

            var writes = TransformWrites(type, models, sdk).ToList();
            if (writes.Count == 0) return false;

            var chain = OwnChain(type, sdk).ToList();
            var declarations = chain.SelectMany(current => TypeDeclarations(current, models)).ToList();
            var handlerParameters = ReplicatedHandlerParameters(declarations, models, sdk);
            var places = new StorageReader(chain, sdk);

            bool IsReplicatedRead(SemanticModel model, SyntaxNode node)
            {
                if (node is not ExpressionSyntax expression) return false;
                if (expression is IdentifierNameSyntax name
                    && model.GetSymbolInfo(name).Symbol is IParameterSymbol parameter
                    && handlerParameters.Contains(parameter.OriginalDefinition))
                {
                    return true;
                }
                return IsAReceiver(expression)
                       && model.GetSymbolInfo(expression).Symbol is not ITypeSymbol
                       && model.GetTypeInfo(expression).Type is INamedTypeSymbol carrier
                       && AuthoritySdkSymbols.DerivesFrom(carrier, sdk.NetworkVariableBase);
            }

            bool ReadsReplicatedState(SemanticModel model, SyntaxNode span)
                => span.DescendantNodesAndSelf().Any(n => IsReplicatedRead(model, n));

            var fills = Fills(declarations, models, places, ReadsReplicatedState);
            foreach (var (write, declaration) in writes)
            {
                var model = models.GetSemanticModel(declaration.SyntaxTree);
                var source = SourceOf(write);
                if (source is null) continue;
                if (ReadsReplicatedState(model, source) || SourceReachesAFill(model, source, write, places, fills))
                {
                    return true;
                }
            }
            return false;
        }

        // The source side of a write: an assignment's right-hand side, a call's
        // arguments.
        private static SyntaxNode SourceOf(SyntaxNode write)
            => write switch
            {
                AssignmentExpressionSyntax assignment => assignment.Right,
                InvocationExpressionSyntax invocation => invocation.ArgumentList,
                _ => null,
            };

        // Every fill of a place from a source `reads` accepts, with where it
        // happened: a declarator's initializer, an assignment's right-hand side
        // (each element of a tuple it deconstructs into), the collection a
        // `foreach` walks, the operand of a declaration pattern.
        private static Dictionary<Storage, List<SyntaxNode>> Fills(
            IReadOnlyList<TypeDeclarationSyntax> declarations, SemanticModelCache models, StorageReader places,
            Func<SemanticModel, SyntaxNode, bool> reads)
        {
            var fills = new Dictionary<Storage, List<SyntaxNode>>();
            void Record(Storage? place, SyntaxNode at)
            {
                if (place is not { } key) return;
                if (!fills.TryGetValue(key, out var recorded))
                {
                    recorded = new List<SyntaxNode>(1);
                    fills.Add(key, recorded);
                }
                recorded.Add(at);
            }
            void RecordDeclared(SemanticModel model, SyntaxNode declared, SyntaxNode at)
                => Record(places.OfDeclared(model, declared), at);

            foreach (var declaration in declarations)
            {
                var model = models.GetSemanticModel(declaration.SyntaxTree);
                foreach (var node in OwnNodes(declaration))
                {
                    switch (node)
                    {
                        case VariableDeclaratorSyntax { Initializer: { } initializer } declarator
                            when reads(model, initializer.Value):
                            RecordDeclared(model, declarator, declarator);
                            break;

                        case AssignmentExpressionSyntax assignment
                            when reads(model, assignment.Right):
                            foreach (var target in FilledTargets(assignment.Left))
                            {
                                if (target is DeclarationExpressionSyntax declared)
                                {
                                    foreach (var designation in declared.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>())
                                        RecordDeclared(model, designation, assignment);
                                }
                                else
                                {
                                    Record(places.OfTarget(model, target), assignment);
                                }
                            }
                            break;

                        case ForEachStatementSyntax loop
                            when reads(model, loop.Expression):
                            RecordDeclared(model, loop, loop);
                            break;

                        case ForEachVariableStatementSyntax loop
                            when reads(model, loop.Expression):
                            foreach (var designation in loop.Variable.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>())
                                RecordDeclared(model, designation, loop);
                            break;

                        case IsPatternExpressionSyntax { Pattern: DeclarationPatternSyntax { Designation: SingleVariableDesignationSyntax designation } } test
                            when reads(model, test.Expression):
                            RecordDeclared(model, designation, test);
                            break;
                    }
                }
            }
            return fills;
        }

        // Whether a write's source reads a place that was filled ahead of it: a
        // member, from any body; a body-scoped place, ahead of the write in its
        // body or in a loop the two share.
        private static bool SourceReachesAFill(
            SemanticModel model, SyntaxNode source, SyntaxNode write, StorageReader places,
            Dictionary<Storage, List<SyntaxNode>> fills)
        {
            foreach (var name in source.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (places.OfRead(model, name) is { } place
                    && fills.TryGetValue(place, out var recorded)
                    && (!place.IsBodyScoped || recorded.Any(fill => Precedes(fill, write))))
                {
                    return true;
                }
            }
            return false;
        }

        // Whether a transform this type moves reaches a replicated member's
        // write: `_yaw.Value = transform.eulerAngles.y`, `_lanes.Add(transform.
        // position.x)`, `_state.Value = new State { pos = transform.position }` —
        // the owner's half of a replicated mover whose state is not a position
        // wrapper, and the direction the driven reading does not look in.  A
        // carrier for the motion by the same argument: what the object does with
        // its transform leaves the machine.  The transform published has to be
        // one this type moves — its own, under `transform` or a name it cached
        // it in, or another mover it also writes — so a follower that publishes
        // its leader's heading while moving itself from input is not silenced by
        // the leader.  Read on the replicated write's own source side, or through
        // one place filled from such a read, as the driven reading is.
        private static bool PublishesTransform(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            if (sdk.NetworkVariableBase is null) return false;

            var writes = TransformWrites(type, models, sdk).ToList();
            if (writes.Count == 0) return false;

            var chain = OwnChain(type, sdk).ToList();
            var declarations = chain.SelectMany(current => TypeDeclarations(current, models)).ToList();
            var places = new StorageReader(chain, sdk);

            // This object's own components under the names this type gives them:
            // `_body = transform`, `_rb = GetComponent<Rigidbody>()` in Awake are
            // the ordinary caching habit, and the cached name and `transform` are
            // one component to the reading.
            var selfPlaces = Fills(declarations, models, places,
                (model, span) => span is ExpressionSyntax expression && IsSelfComponent(model, expression));
            bool IsSelf(SemanticModel model, ExpressionSyntax expression)
                => IsSelfComponent(model, expression)
                   || (places.OfTarget(model, expression) is { } place && selfPlaces.ContainsKey(place));

            // The movers this type writes: itself, and each cached mover by its place.
            bool movesSelf = false;
            var movers = new HashSet<Storage>();
            foreach (var (write, declaration) in writes)
            {
                var model = models.GetSemanticModel(declaration.SyntaxTree);
                var receiver = write switch
                {
                    AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax member } => member.Expression,
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } => call.Expression,
                    _ => null,
                };
                if (receiver is null) continue;
                if (IsSelf(model, receiver)) movesSelf = true;
                else if (places.OfTarget(model, receiver) is { } mover) movers.Add(mover);
            }

            bool ReadsOwnTransform(SemanticModel model, SyntaxNode span)
                => span.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(access =>
                       TransformWriteMembers.Contains(access.Name.Identifier.ValueText)
                       && IsAMotionReceiver(model, access.Expression, sdk)
                       && (IsSelf(model, access.Expression)
                           ? movesSelf
                           : places.OfTarget(model, access.Expression) is { } read && movers.Contains(read)));

            bool IsReplicated(SemanticModel model, ExpressionSyntax receiver)
                => model.GetTypeInfo(receiver).Type is INamedTypeSymbol carrier
                   && AuthoritySdkSymbols.DerivesFrom(carrier, sdk.NetworkVariableBase);

            var fills = Fills(declarations, models, places, ReadsOwnTransform);

            foreach (var declaration in declarations)
            {
                var model = models.GetSemanticModel(declaration.SyntaxTree);
                foreach (var node in OwnNodes(declaration))
                {
                    SyntaxNode source = node switch
                    {
                        // `_yaw.Value = …`, `_list[i] = …`
                        AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } target } assignment
                            when IsReplicated(model, target.Expression) => assignment.Right,
                        AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax target } assignment
                            when IsReplicated(model, target.Expression) => assignment.Right,
                        // `_list.Add(…)`, `_list.Insert(i, …)`
                        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" or "Insert" } call } invocation
                            when IsReplicated(model, call.Expression) => invocation.ArgumentList,
                        _ => null,
                    };
                    if (source is null) continue;
                    if (ReadsOwnTransform(model, source) || SourceReachesAFill(model, source, node, places, fills))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Whether an expression is this object's own component: `transform`,
        // `this.transform`, `gameObject.transform`, or a `GetComponent<T>()` taken
        // on this object — as opposed to another object's, reached through a
        // field or a call of this type's own.
        private static bool IsSelfComponent(SemanticModel model, ExpressionSyntax expression)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    return IsSelfComponent(model, parenthesized.Expression);
                case ThisExpressionSyntax:
                    return true;
                case IdentifierNameSyntax name:
                    return IsSelfComponentMember(model.GetSymbolInfo(name).Symbol);
                case MemberAccessExpressionSyntax access:
                    return IsSelfComponentMember(model.GetSymbolInfo(access).Symbol)
                           && IsSelfComponent(model, access.Expression);
                case InvocationExpressionSyntax invocation
                    when CalleeName(invocation.Expression) is "GetComponent":
                    return CalleeReceiver(invocation) is not { } receiver || IsSelfComponent(model, receiver);
                default:
                    return false;
            }
        }

        // `transform` and `gameObject` as the engine declares them on Component
        // and GameObject: reached from `this`, they name this object.
        private static bool IsSelfComponentMember(ISymbol symbol)
            => symbol is IPropertySymbol { Name: "transform" or "gameObject" } property
               && property.ContainingType?.Name is "Component" or "GameObject";

        // Whether an expression is read THROUGH — the receiver of a member or
        // element access, of a conditional access, or of a `foreach` — which is
        // how a value leaves a replicated member by a read.
        private static bool IsAReceiver(ExpressionSyntax expression)
            => expression.Parent switch
            {
                MemberAccessExpressionSyntax access => access.Expression == expression,
                ElementAccessExpressionSyntax element => element.Expression == expression,
                ConditionalAccessExpressionSyntax conditional => conditional.Expression == expression,
                ForEachStatementSyntax loop => loop.Expression == expression,
                ForEachVariableStatementSyntax loop => loop.Expression == expression,
                _ => false,
            };

        // The expressions an assignment target fills: itself, or each element of
        // a tuple it deconstructs into.
        private static IEnumerable<ExpressionSyntax> FilledTargets(ExpressionSyntax target)
        {
            if (target is TupleExpressionSyntax tuple)
            {
                foreach (var element in tuple.Arguments)
                    foreach (var inner in FilledTargets(element.Expression)) yield return inner;
            }
            else
            {
                yield return target;
            }
        }

        // A place this type fills and reads back.  `Owner` is the reference the
        // member is reached through, or null for a local, a parameter, and a
        // member reached from `this`; both halves are definitions, so a member
        // of a generic base is one place whichever construction reaches it.
        private readonly struct Storage : IEquatable<Storage>
        {
            public Storage(ISymbol owner, ISymbol member, bool bodyScoped)
            {
                Owner = owner?.OriginalDefinition;
                Member = member.OriginalDefinition;
                IsBodyScoped = bodyScoped;
            }

            public ISymbol Owner { get; }
            public ISymbol Member { get; }

            // A local or parameter lives in one body, so a fill of it counts by
            // position; a member persists, so a fill in any body counts — a member
            // reached through a local reference included, since what it fills
            // outlives the body.
            public bool IsBodyScoped { get; }

            public bool Equals(Storage other)
                => SymbolEqualityComparer.Default.Equals(Owner, other.Owner)
                   && SymbolEqualityComparer.Default.Equals(Member, other.Member);

            public override bool Equals(object obj) => obj is Storage other && Equals(other);

            public override int GetHashCode()
                => unchecked(SymbolEqualityComparer.Default.GetHashCode(Member) * 31
                             + (Owner is null ? 0 : SymbolEqualityComparer.Default.GetHashCode(Owner)));
        }

        // Resolves the place an expression names, for a fill's target and for a
        // read on the write's source side alike, so the two sides name the same
        // storage the same way; two spellings of one place — a field and a
        // property that forwards to it — are two places to this reading.
        private sealed class StorageReader
        {
            private readonly IReadOnlyList<INamedTypeSymbol> _chain;
            private readonly AuthoritySdkSymbols _sdk;

            public StorageReader(IReadOnlyList<INamedTypeSymbol> chain, AuthoritySdkSymbols sdk)
            {
                _chain = chain;
                _sdk = sdk;
            }

            // The place a declaration introduces: a local, a foreach variable, a
            // pattern variable, a field declarator.
            public Storage? OfDeclared(SemanticModel model, SyntaxNode declared)
                => Own(model.GetDeclaredSymbol(declared));

            // The place an assignment target names: a component of a value is the
            // value (`_target.x` → `_target`, `_lanes[i].y` → `_lanes`), a member
            // of a reference is that member on that reference (`_next._target`;
            // `_segments[i]._target` is `_target` on `_segments`), and a member of
            // `this` or a static of the chain is the member.
            public Storage? OfTarget(SemanticModel model, ExpressionSyntax target)
            {
                var expression = Bare(target);
                switch (expression)
                {
                    case IdentifierNameSyntax name:
                        return Own(model.GetSymbolInfo(name).Symbol);
                    case ElementAccessExpressionSyntax element:
                        return OfTarget(model, element.Expression);
                    case MemberAccessExpressionSyntax access:
                        return OfMember(model, access.Expression, model.GetSymbolInfo(access).Symbol);
                    case ConditionalAccessExpressionSyntax { WhenNotNull: MemberBindingExpressionSyntax binding } conditional:
                        return OfMember(model, conditional.Expression, model.GetSymbolInfo(binding).Symbol);
                    default:
                        return null;
                }
            }

            // The place an identifier on the source side reads: the same rule,
            // entered at the identifier — `_target` in `_next._target` is
            // `_next`'s, in `_next?._target` too, and `x` in `_input.x` is a
            // component of `_input`, which is a place only if `_input` is.
            public Storage? OfRead(SemanticModel model, IdentifierNameSyntax name)
            {
                switch (name.Parent)
                {
                    case MemberAccessExpressionSyntax access when access.Name == name:
                        return OfMember(model, access.Expression, model.GetSymbolInfo(access).Symbol);
                    case MemberBindingExpressionSyntax binding when binding.Name == name:
                        return binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional
                            ? OfMember(model, conditional.Expression, model.GetSymbolInfo(binding).Symbol)
                            : null;
                    default:
                        return Own(model.GetSymbolInfo(name).Symbol);
                }
            }

            private Storage? OfMember(SemanticModel model, ExpressionSyntax receiver, ISymbol member)
            {
                if (member is null) return null;
                receiver = Bare(receiver);
                if (receiver is ThisExpressionSyntax or BaseExpressionSyntax
                    || model.GetSymbolInfo(receiver).Symbol is ITypeSymbol)
                {
                    return Own(member);
                }
                if (model.GetTypeInfo(receiver).Type is { IsReferenceType: true })
                {
                    // One reference deep: the holder has to be a place of this
                    // type's own, and the member is qualified by it.  An element
                    // of a collection of references is qualified by the collection
                    // — `_segments[i]._target` is `_target` on `_segments`, which
                    // is member-sensitive and index-insensitive.
                    return OfTarget(model, receiver) is { Owner: null } holder && IsStorageTyped(member)
                        ? new Storage(holder.Member, member, bodyScoped: false)
                        : null;
                }
                // A component of a value is the value.
                return OfTarget(model, receiver);
            }

            // A local, a parameter, or a member declared on this chain, typed as
            // something other than a carrier — a replicated member is read through,
            // never filled and read back.
            private Storage? Own(ISymbol symbol)
            {
                switch (symbol)
                {
                    case ILocalSymbol or IParameterSymbol:
                        return IsStorageTyped(symbol) ? new Storage(null, symbol, bodyScoped: true) : null;
                    case IFieldSymbol or IPropertySymbol when OnChain(symbol.ContainingType) && IsStorageTyped(symbol):
                        return new Storage(null, symbol, bodyScoped: false);
                    default:
                        return null;
                }
            }

            private bool IsStorageTyped(ISymbol symbol)
            {
                ITypeSymbol held = symbol switch
                {
                    ILocalSymbol local => local.Type,
                    IParameterSymbol parameter => parameter.Type,
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => null,
                };
                return held is not null && !AuthoritySdkSymbols.DerivesFrom(held, _sdk.NetworkVariableBase);
            }

            private bool OnChain(INamedTypeSymbol owner)
                => owner is not null
                   && _chain.Any(current => SymbolEqualityComparer.Default.Equals(
                          current.OriginalDefinition, owner.OriginalDefinition));

            private static ExpressionSyntax Bare(ExpressionSyntax expression)
            {
                while (true)
                {
                    switch (expression)
                    {
                        case ParenthesizedExpressionSyntax parenthesized:
                            expression = parenthesized.Expression;
                            continue;
                        case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                            expression = postfix.Operand;
                            continue;
                    }
                    return expression;
                }
            }
        }

        // Where a fill or a write sits in the member that holds it: at its own
        // position, in a lambda's body as anywhere else — a handler that
        // transforms the delivered value and then applies it is ordered like a
        // method is — except inside a local function, where it sits nowhere in
        // particular, because the function is callable from anywhere in its
        // body, ahead of its declaration included.
        private static (SyntaxNode Member, bool Anywhere) Place(SyntaxNode node)
        {
            bool anywhere = false;
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                switch (ancestor)
                {
                    case LocalFunctionStatementSyntax:
                        anywhere = true;
                        break;
                    case MemberDeclarationSyntax member:
                        return (member, anywhere);
                }
            }
            return (null, anywhere);
        }

        // A body-scoped fill reaches a write in the same member when it precedes
        // it in text, when either sits in a local function, or when the two share
        // a loop: the fill at the bottom of a `while` is the value the write at
        // its top reads on every pass after the first.
        private static bool Precedes(SyntaxNode fill, SyntaxNode write)
        {
            var (fillMember, fillAnywhere) = Place(fill);
            var (writeMember, writeAnywhere) = Place(write);
            if (fillMember is null || fillMember != writeMember) return false;
            if (fillAnywhere || writeAnywhere || fill.SpanStart < write.SpanStart) return true;
            for (var ancestor = fill.Parent; ancestor is not null && ancestor != fillMember; ancestor = ancestor.Parent)
            {
                if ((ancestor is ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax
                        or WhileStatementSyntax or DoStatementSyntax)
                    && ancestor.Span.Contains(write.Span))
                {
                    return true;
                }
            }
            return false;
        }

        // The parameters through which a subscribed handler receives a replicated
        // member's value: a lambda, an anonymous method or a method group
        // subscribed to an event a NetworkVariableBase subclass declares, directly
        // (`_lane.OnValueChanged += (_, l) => …`) or through a delegate this type
        // stored first (`_onChanged = (_, l) => …; _lane.OnValueChanged += _onChanged;`).
        // Keyed on definitions, so a handler declared on a generic base is found
        // from the leaf that subscribes it.
        private static HashSet<ISymbol> ReplicatedHandlerParameters(
            IReadOnlyList<TypeDeclarationSyntax> declarations, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            var parameters = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var stored = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (var declaration in declarations)
            {
                var model = models.GetSemanticModel(declaration.SyntaxTree);
                foreach (var subscription in OwnNodes(declaration).OfType<AssignmentExpressionSyntax>())
                {
                    if (subscription.IsKind(SyntaxKind.AddAssignmentExpression)
                        && model.GetSymbolInfo(subscription.Left).Symbol is IEventSymbol @event
                        && AuthoritySdkSymbols.DerivesFrom(@event.ContainingType, sdk.NetworkVariableBase))
                    {
                        AddHandler(model, subscription.Right, parameters, stored);
                    }
                }
            }

            if (stored.Count > 0)
            {
                foreach (var declaration in declarations)
                {
                    var model = models.GetSemanticModel(declaration.SyntaxTree);
                    foreach (var node in OwnNodes(declaration))
                    {
                        switch (node)
                        {
                            case VariableDeclaratorSyntax { Initializer: { } initializer } declarator
                                when model.GetDeclaredSymbol(declarator) is { } declared
                                     && stored.Contains(declared.OriginalDefinition):
                                AddHandler(model, initializer.Value, parameters, null);
                                break;
                            case AssignmentExpressionSyntax assignment
                                when model.GetSymbolInfo(assignment.Left).Symbol is { } target
                                     && stored.Contains(target.OriginalDefinition):
                                AddHandler(model, assignment.Right, parameters, null);
                                break;
                        }
                    }
                }
            }
            return parameters;
        }

        // One handler expression: a lambda or anonymous method contributes its
        // own parameters, a method group the named method's, and a stored
        // delegate is remembered so the handler assigned to it can be found.
        private static void AddHandler(
            SemanticModel model, ExpressionSyntax handler, HashSet<ISymbol> parameters, HashSet<ISymbol> stored)
        {
            if (handler is AnonymousFunctionExpressionSyntax function)
            {
                if (model.GetSymbolInfo(function).Symbol is IMethodSymbol anonymous)
                {
                    foreach (var parameter in anonymous.Parameters) parameters.Add(parameter.OriginalDefinition);
                }
                return;
            }

            var info = model.GetSymbolInfo(handler);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            switch (symbol)
            {
                case IMethodSymbol method:
                    foreach (var parameter in method.Parameters) parameters.Add(parameter.OriginalDefinition);
                    break;
                case IFieldSymbol or IPropertySymbol or ILocalSymbol when stored is not null:
                    stored.Add(symbol.OriginalDefinition);
                    break;
            }
        }

        // Whether the receiver of the write is something that moves the object.
        // Its type decides wherever the type binds — `transform`, a cached
        // `Transform _body`, a `Rigidbody rb`, a `CharacterController` — compared
        // as symbols against the engine types the contract declares, so a
        // receiver whose type binds to anything else is not a mover however it is
        // named: `_transformerConfig.position` is a setting and so is a project
        // type that borrows the name `Transform`.  Where the type does not bind —
        // an integrator's own component outside the headless contract — the
        // receiver's text decides instead, so Unity's own member and a receiver
        // that spells one out are one rule.
        // ⚠️ The text rule is the weaker half and reads like one: an unbound
        // `_body` is invisible to it, and an unbound `_transformerConfig` is a
        // hit.  The miss costs a question the author is not asked; the hit costs a
        // question they are — neither can reach a verdict, which is the only
        // outcome this signal must not decide.
        private static bool IsAMotionReceiver(SemanticModel model, ExpressionSyntax receiver, AuthoritySdkSymbols sdk)
        {
            if (model.GetTypeInfo(receiver).Type is { } type && type.TypeKind != TypeKind.Error)
            {
                return AuthoritySdkSymbols.IsOneOf(type, sdk.MotionReceivers, MotionReceiverTypes);
            }
            string text = receiver.ToString();
            return text.IndexOf("transform", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("rigidbody", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool WiresSceneObjects(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            if (sdk.GameObject is not null)
            {
                foreach (var current in OwnChain(type, sdk))
                {
                    foreach (var member in current.GetMembers())
                    {
                        var memberType = ReplicableMemberType(member);
                        if (memberType is not null && HoldsGameObject(memberType, sdk.GameObject))
                        {
                            return true;
                        }
                    }
                }
            }

            foreach (var declaration in OwnChain(type, sdk).SelectMany(current => TypeDeclarations(current, models)))
            {
                SemanticModel model = null;
                foreach (var node in OwnNodes(declaration))
                {
                    switch (node)
                    {
                        case InvocationExpressionSyntax invocation
                            when ComponentAcquisitionNames.Contains(CalleeName(invocation.Expression)):
                            return true;

                        case AssignmentExpressionSyntax assignment
                            when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                || assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                        {
                            model ??= models.GetSemanticModel(declaration.SyntaxTree);
                            var target = model.GetSymbolInfo(assignment.Left).Symbol;
                            var targetType = target switch
                            {
                                IFieldSymbol field => field.Type,
                                IPropertySymbol property => property.Type,
                                _ => null,
                            };

                            // Wiring keys on assigning another SCRIPT reference
                            // (a MonoBehaviour/NetworkBehaviour) — the clear
                            // cross-object-orchestration signal. Assigning a plain
                            // Component (Transform/Camera/Rigidbody) is almost
                            // always self-caching (`_t = transform`), which is
                            // presentation infrastructure, not orchestration, so
                            // it is deliberately not counted here.
                            if (targetType is not null && AuthoritySdkSymbols.DerivesFrom(targetType, sdk.MonoBehaviour))
                            {
                                return true;
                            }

                            break;
                        }
                    }
                }
            }

            return false;
        }

        // A GameObject handle in any of its member spellings: the type itself,
        // an array of it, or a generic collection carrying it.
        private static bool HoldsGameObject(ITypeSymbol memberType, INamedTypeSymbol gameObject)
        {
            switch (memberType)
            {
                case INamedTypeSymbol named when SymbolEqualityComparer.Default.Equals(named, gameObject):
                    return true;
                case IArrayTypeSymbol array:
                    return HoldsGameObject(array.ElementType, gameObject);
                case INamedTypeSymbol { IsGenericType: true } generic:
                    return generic.TypeArguments.Any(a => HoldsGameObject(a, gameObject));
                default:
                    return false;
            }
        }

        // The simple callee name of an invocation, through the receiver shapes
        // the conversion analyzer also recognises (x.M(), x?.M(), M()).
        internal static string CalleeName(ExpressionSyntax callee)
            => callee switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                _ => null,
            };

        // The expression an invocation is called on, for the same shapes CalleeName
        // reads. `x?.M()` states its receiver on the enclosing conditional access
        // rather than on the invocation, so a caller that inspected only the
        // invocation's own target would see the name and never the type behind it.
        // Null for a bare `M()`, which has no receiver to type.
        internal static ExpressionSyntax CalleeReceiver(InvocationExpressionSyntax invocation)
            => invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Expression,
                MemberBindingExpressionSyntax _
                    when invocation.Parent is ConditionalAccessExpressionSyntax conditional
                        && conditional.WhenNotNull == invocation => conditional.Expression,
                _ => null,
            };

        // The nearest thing to a statement about the OBJECT the source carries:
        // the type is a replicator or derives from one, a `[RequireComponent]` on
        // the chain names one, or the type attaches or takes one at runtime
        // (`AddComponent<NetworkTransform>()`, `GetComponent<NetworkTransform>()`)
        // — each a declaration that the object carries the component.
        // Identity is read from the symbol wherever the replicators bind, and
        // from the name where they do not: RTMPE.Sync.NetworkTransform is absent
        // from the contract a headless score compiles against, so a symbol
        // comparison alone would answer "no replicator" for every project the CLI
        // scores.  🔑 A project type of the same name therefore matches there,
        // and there only.  That direction is silence, which is the quiet failure
        // and not the safe one; it is the price of answering at all where the
        // engine is absent.
        // ⚠️ DependencyGraphBuilder reads the same attribute and resolves its
        // argument semantically only; the two deliberately differ, because an
        // edge in a graph of project types may be dropped when a type does not
        // resolve and a suppression may not.
        private static bool DeclaresMotionReplicator(
            INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            // A replicator is one: NetworkTransform writes the transform it exists
            // to replicate, and so does a class derived from it that leaves its
            // driver in place.
            if (IsAReplicator(type, sdk)) return true;

            foreach (var current in OwnChain(type, sdk))
            {
                // A base compiled elsewhere carries its attributes as metadata, and
                // Unity honours a requirement declared on a base for every script
                // derived from it.
                foreach (var attribute in current.GetAttributes())
                {
                    if (attribute.AttributeClass?.Name == "RequireComponent"
                        && attribute.ConstructorArguments.Any(argument =>
                               argument.Kind == TypedConstantKind.Type
                               && argument.Value is ITypeSymbol required
                               && IsAReplicatorName(required, sdk)))
                    {
                        return true;
                    }
                }

                foreach (var declaration in TypeDeclarations(current, models))
                {
                    var model = models.GetSemanticModel(declaration.SyntaxTree);
                    foreach (var attribute in declaration.AttributeLists.SelectMany(list => list.Attributes))
                    {
                        if (AttributeSimpleName(attribute.Name) != "RequireComponent") continue;
                        var arguments = attribute.ArgumentList?.Arguments
                            ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>);
                        foreach (var argument in arguments)
                        {
                            if (argument.Expression is TypeOfExpressionSyntax typeOf
                                && NamesAReplicator(model, typeOf.Type, sdk))
                            {
                                return true;
                            }
                        }
                    }

                    foreach (var invocation in OwnNodes(declaration).OfType<InvocationExpressionSyntax>())
                    {
                        if (CalleeName(invocation.Expression) is "AddComponent" or "GetComponent"
                            && GenericArgument(invocation.Expression) is { } argument
                            && NamesAReplicator(model, argument, sdk))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // The one type argument of a generic callee (`AddComponent<X>()`,
        // `gameObject.GetComponent<X>()`), or null.
        private static TypeSyntax GenericArgument(ExpressionSyntax callee)
        {
            var name = callee switch
            {
                GenericNameSyntax generic => generic,
                MemberAccessExpressionSyntax access => access.Name as GenericNameSyntax,
                MemberBindingExpressionSyntax binding => binding.Name as GenericNameSyntax,
                _ => null,
            };
            return name is { TypeArgumentList.Arguments.Count: 1 } ? name.TypeArgumentList.Arguments[0] : null;
        }

        // Whether a type the source names is a replicator: by symbol where the
        // replicators bind; otherwise by the simple name of what the spelling
        // binds to — the compiler answers an alias with its target, and an
        // unbound name with an error type carrying its last identifier — and,
        // where nothing binds at all, of the spelling's own tokens, so that
        // `RTMPE.Sync.NetworkTransform`, `global::RTMPE.Sync.NetworkTransform` and
        // a name broken across lines all answer alike in a compilation that
        // carries no such type.
        private static bool NamesAReplicator(SemanticModel model, TypeSyntax syntax, AuthoritySdkSymbols sdk)
        {
            if (model.GetTypeInfo(syntax).Type is { } type
                && (type.TypeKind != TypeKind.Error || !string.IsNullOrEmpty(type.Name)))
            {
                return IsAReplicatorName(type, sdk);
            }
            return MotionReplicators.Contains(
                SimpleTypeName(string.Concat(syntax.DescendantTokens().Select(token => token.ValueText))));
        }

        // A bound type is compared as a symbol where the replicators bind; an
        // error type carries the name the author wrote and nothing else.
        private static bool IsAReplicatorName(ITypeSymbol type, AuthoritySdkSymbols sdk)
            => type.TypeKind == TypeKind.Error
                ? MotionReplicators.Contains(type.Name)
                : IsAReplicator(type, sdk);

        // Whether a type replicates a transform: it is one of the SDK's replicators
        // or derives from one, and nothing between it and that base shadows the
        // driver.  The replicators are driven by an engine message their base
        // declares privately — `Update` on NetworkTransform, `FixedUpdate` on the
        // rigidbodies — and the engine delivers a message to the most derived
        // declaration of its name only, so a derived type declaring the same
        // message leaves the base's driver uncalled and replicates nothing.  Where
        // the replicator binds, the messages it declares are read from it; where
        // it reaches the chain as a name, any engine update message on a derived
        // link is taken as shadowing — the loud side of not knowing.
        private static bool IsAReplicator(ITypeSymbol type, AuthoritySdkSymbols sdk)
        {
            var replicator = AuthoritySdkSymbols.FamilyLink(type, sdk.MotionReplicators, MotionReplicators);
            if (replicator is null) return false;

            var drivers = replicator.TypeKind == TypeKind.Error
                ? EngineUpdateMessages
                : new HashSet<string>(
                    replicator.GetMembers().OfType<IMethodSymbol>()
                        .Where(method => EngineUpdateMessages.Contains(method.Name))
                        .Select(method => method.Name),
                    StringComparer.Ordinal);
            for (var current = type;
                 current is not null && !SymbolEqualityComparer.Default.Equals(current, replicator);
                 current = current.BaseType)
            {
                if (current.GetMembers().OfType<IMethodSymbol>().Any(method =>
                        method.Parameters.Length == 0 && drivers.Contains(method.Name)))
                {
                    return false;
                }
            }
            return true;
        }

        // The engine messages a replicator can be driven by.
        private static readonly HashSet<string> EngineUpdateMessages =
            new HashSet<string>(StringComparer.Ordinal) { "Update", "FixedUpdate", "LateUpdate" };

        // The components that put a transform on the wire.  ⛔ The interpolator is
        // not among them: it smooths what arrives and replicates nothing, so a type
        // that requires only it is still a silent mover.
        private static readonly System.Collections.Generic.HashSet<string> MotionReplicators =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "NetworkTransform", "NetworkRigidbody", "NetworkRigidbody2D",
            };

        // `RequireComponent` and `UnityEngine.RequireComponent` name one attribute;
        // the last dotted segment is what they agree on, and an alias qualifier is
        // a prefix like any other.
        private static string AttributeSimpleName(NameSyntax name)
            => SimpleTypeName(name.ToString());

        // The last segment of `Sync.NetworkTransform`, `RTMPE.Sync.NetworkTransform`
        // and `global::RTMPE.Sync.NetworkTransform` alike.  A rendering, not a
        // resolution: it is the fallback for a spelling the compilation cannot
        // bind, and a nested type's container is a prefix like any other.
        private static string SimpleTypeName(string rendered)
        {
            int cut = rendered.LastIndexOfAny(new[] { '.', ':' });
            return cut < 0 ? rendered : rendered.Substring(cut + 1);
        }


        // The declarations of one type on the chain that THIS compilation can
        // bind. A reference compiled from source keeps its symbols, so a base
        // reached through one carries syntax into a tree the cache has no model
        // for — and asking for one threw out of every signal below as AD0001, in
        // the IDE, for every type deriving from an SDK-shipped behaviour once the
        // SDK's asmdef was a project reference. What is skipped here is recorded
        // by ReadPartially, never spent as "nothing declared".
        private static System.Collections.Generic.IEnumerable<TypeDeclarationSyntax> TypeDeclarations(
            INamedTypeSymbol type, SemanticModelCache models)
            => type.DeclaringSyntaxReferences
                .Where(reference => models.Contains(reference.SyntaxTree))
                .Select(reference => reference.GetSyntax())
                .OfType<TypeDeclarationSyntax>();

        // Whether any declaration on the chain sits outside what the cache binds —
        // the fact the skip above must not be silent about.
        private static bool ReadPartially(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
            => OwnChain(type, sdk).Any(current =>
                current.DeclaringSyntaxReferences.Any(reference => !models.Contains(reference.SyntaxTree)));

        // The declaration's own syntax, stopping at any nested type declaration —
        // class/struct/record/interface/enum (BaseTypeDeclarationSyntax), each of
        // which is its own graph node whose sends/wiring/enum-initializers must
        // not leak into (and double-count against) the enclosing type's signals.
        // A nested delegate carries only a signature (no leakable expression), so
        // it needs no exclusion.
        private static System.Collections.Generic.IEnumerable<SyntaxNode> OwnNodes(TypeDeclarationSyntax declaration)
            => declaration.DescendantNodes(n => n == declaration || n is not BaseTypeDeclarationSyntax);
    }
}
