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
            IPropertySymbol isOwner)
        {
            MonoBehaviour = monoBehaviour;
            NetworkBehaviour = networkBehaviour;
            NetworkVariableBase = networkVariableBase;
            GameObject = gameObject;
            RtmpeRpcAttribute = rtmpeRpcAttribute;
            IsOwner = isOwner;
        }

        public INamedTypeSymbol MonoBehaviour { get; }
        public INamedTypeSymbol NetworkBehaviour { get; }
        public INamedTypeSymbol NetworkVariableBase { get; }
        public INamedTypeSymbol GameObject { get; }
        public INamedTypeSymbol RtmpeRpcAttribute { get; }
        public IPropertySymbol IsOwner { get; }

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
                networkBehaviour?.GetMembers("IsOwner").OfType<IPropertySymbol>().FirstOrDefault());
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
                DeclaresMotionReplicator(type, models, sdk));
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
                    if (memberType is not null && AuthoritySdkSymbols.DerivesFrom(memberType, sdk.NetworkVariableBase))
                    {
                        count++;
                    }
                }
            }

            return count;
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
        // The transform members a write to which MOVES the object, and the
        // helpers that move it without naming one.
        //
        // 🔑 Read syntactically rather than through the symbol table, and the
        // reason is stated because it is the weaker choice: `transform` on a
        // MonoBehaviour is a well-known member, but the shapes below also appear
        // on a cached `Transform` field (`_body.position = …`), which a symbol
        // lookup would catch and a name match will not.  What it must never do
        // is answer YES where there is no motion, because the verdict it drives
        // asks the author a question — so it matches an assignment to one of
        // these members, or a call to one of these methods, and nothing looser.
        private static readonly string[] TransformWriteMembers =
        {
            "position", "localPosition", "rotation", "localRotation",
            "eulerAngles", "localEulerAngles", "localScale", "forward", "right", "up",
        };

        private static readonly string[] TransformWriteCalls =
        {
            "Translate", "Rotate", "RotateAround", "SetPositionAndRotation",
            "SetLocalPositionAndRotation", "LookAt", "MovePosition", "MoveRotation",
        };

        private static bool WritesTransform(INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            // The same span every other signal reads: the type and each base
            // above it this project declares.
            foreach (var declaration in OwnChain(type, sdk).SelectMany(current => TypeDeclarations(current, models)))
            {
                foreach (var node in OwnNodes(declaration))
                {
                    switch (node)
                    {
                        case AssignmentExpressionSyntax assignment
                            when assignment.Left is MemberAccessExpressionSyntax member
                                 && TransformWriteMembers.Contains(member.Name.Identifier.ValueText)
                                 && MentionsATransform(member.Expression):
                            return true;

                        case InvocationExpressionSyntax invocation
                            when invocation.Expression is MemberAccessExpressionSyntax call
                                 && TransformWriteCalls.Contains(call.Name.Identifier.ValueText)
                                 && MentionsATransform(call.Expression):
                            return true;
                    }
                }
            }
            return false;
        }

        // Whether the receiver of the write is a transform at all: its rendered
        // text names one — `transform`, `this.transform`, `_segment.transform`,
        // `_rigidbody` — so Unity's own member and a receiver that spells one out
        // are one rule, not two.  ⚠️ A name rule, and it reads like one: a cached
        // `Transform _body` is invisible to it, as the header above says, and
        // `_transformerConfig.position` is a hit.  The miss costs a question the
        // author is not asked; the hit costs a question they are — neither can
        // reach a verdict, which is the only outcome this signal must not decide.
        private static bool MentionsATransform(ExpressionSyntax receiver)
        {
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

        // The nearest thing to a statement about the OBJECT the source carries.
        // ⛔ Both halves are read as NAMES, and that is a departure from this
        // file's rule that type identity is semantic: RTMPE.Sync.NetworkTransform
        // is absent from the contract a headless score compiles against, so a
        // symbol comparison answers "no replicator" for every project scored
        // without the engine — which is every project the CLI scores.
        // ⚠️ DependencyGraphBuilder reads the same attribute and resolves its
        // argument SEMANTICALLY; the two deliberately differ, because an edge in
        // a graph of project types may be dropped when a type does not resolve
        // and a suppression may not.
        // 🔑 A project type of the same name therefore matches. That direction is
        // silence, which is the quiet failure and not the safe one; it is the
        // price of answering at all where the engine is absent.
        private static bool DeclaresMotionReplicator(
            INamedTypeSymbol type, SemanticModelCache models, AuthoritySdkSymbols sdk)
        {
            // A replicator is one: NetworkTransform writes the transform it exists
            // to replicate, and so does every class derived from it.  Read by name
            // for the same reason the requirement below is — the SDK assembly is
            // not referenced by a headless score.
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (MotionReplicators.Contains(current.Name)) return true;
            }

            foreach (var declaration in OwnChain(type, sdk).SelectMany(current => TypeDeclarations(current, models)))
            {
                foreach (var attribute in declaration.AttributeLists.SelectMany(list => list.Attributes))
                {
                    string name = AttributeSimpleName(attribute.Name);
                    if (name != "RequireComponent" && name != "RequireComponentAttribute") continue;

                    var arguments = attribute.ArgumentList?.Arguments
                        ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>);
                    foreach (var argument in arguments)
                    {
                        if (argument.Expression is TypeOfExpressionSyntax typeOf
                            && MotionReplicators.Contains(SimpleTypeName(typeOf.Type.ToString())))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // The components that put a transform on the wire.  ⛔ The interpolator is
        // not among them: it smooths what arrives and replicates nothing, so a type
        // that requires only it is still a silent mover.
        private static readonly System.Collections.Generic.HashSet<string> MotionReplicators =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "NetworkTransform", "NetworkRigidbody", "NetworkRigidbody2D",
            };

        // `RequireComponent`, `UnityEngine.RequireComponent` and
        // `global::UnityEngine.RequireComponentAttribute` name one attribute; the
        // last dotted segment is what they agree on, and an alias qualifier is a
        // prefix like any other.
        private static string AttributeSimpleName(NameSyntax name)
            => SimpleTypeName(name.ToString());

        // The last segment of `Sync.NetworkTransform`, `RTMPE.Sync.NetworkTransform`
        // and `global::RTMPE.Sync.NetworkTransform` alike.  ⚠️ It is a rendering,
        // not a resolution: a `using` alias renders as the alias, a name broken
        // across lines keeps its trivia, and a nested type renders its container —
        // each answers wrongly, and each fails in the direction the caller's own
        // comment accounts for.
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
