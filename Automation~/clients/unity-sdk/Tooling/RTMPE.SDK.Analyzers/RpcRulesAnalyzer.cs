using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Moves the <c>[RtmpeRpc]</c> rules the runtime enforces at first spawn
    /// (<c>RpcRegistry.Validate</c>) to compile time: a method must be public and
    /// instance, carry only serializable parameters, be declared on a
    /// <c>NetworkBehaviour</c>, and resolve to a method id that collides with
    /// neither a sibling method nor a reserved built-in id.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RpcRulesAnalyzer : DiagnosticAnalyzer
    {
        private const string RtmpeRpcAttributeMetadataName = "RTMPE.Rpc.RtmpeRpcAttribute";
        private const string NetworkBehaviourMetadataName = "RTMPE.Core.NetworkBehaviour";
        private const string NetworkSerializableMetadataName = "RTMPE.Rpc.INetworkSerializable";

        private static readonly DiagnosticDescriptor MustBePublicInstance = new DiagnosticDescriptor(
            DiagnosticIds.RpcMustBePublicInstance,
            "RPC method must be public and instance",
            "[RtmpeRpc] method '{0}' must be a public instance method (not static or abstract)",
            "RTMPE.Rpc", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.RpcMustBePublicInstance));

        private static readonly DiagnosticDescriptor UnsupportedParameterType = new DiagnosticDescriptor(
            DiagnosticIds.RpcUnsupportedParameterType,
            "RPC parameter type is not serializable",
            "Parameter '{0}' of [RtmpeRpc] method '{1}' has type '{2}', which RpcSerializer cannot encode",
            "RTMPE.Rpc", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.RpcUnsupportedParameterType));

        private static readonly DiagnosticDescriptor DuplicateMethodId = new DiagnosticDescriptor(
            DiagnosticIds.RpcDuplicateMethodId,
            "RPC methods share a method id",
            "[RtmpeRpc] method '{0}' on '{1}' resolves to method id 0x{2} which collides with '{3}'",
            "RTMPE.Rpc", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.RpcDuplicateMethodId));

        private static readonly DiagnosticDescriptor ReservedMethodIdCollision = new DiagnosticDescriptor(
            DiagnosticIds.RpcReservedMethodIdCollision,
            "RPC method id collides with a reserved id",
            "[RtmpeRpc] method '{0}' on '{1}' resolves to method id 0x{2} which collides with a reserved RpcMethodId",
            "RTMPE.Rpc", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.RpcReservedMethodIdCollision));

        private static readonly DiagnosticDescriptor RequiresNetworkBehaviour = new DiagnosticDescriptor(
            DiagnosticIds.RpcRequiresNetworkBehaviour,
            "RPC method must be declared on a NetworkBehaviour",
            "[RtmpeRpc] method '{0}' must be declared on a type that inherits RTMPE.Core.NetworkBehaviour",
            "RTMPE.Rpc", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.RpcRequiresNetworkBehaviour));

        // Mirrors the dispatcher's own limits (NetworkBehaviour.InvokeRpc): the
        // deserialized argument vector is boxed objects checked by
        // IsInstanceOfType — a by-ref parameter can never accept one — and an
        // open generic method cannot be closed by reflection at dispatch. A
        // non-void return, by contrast, dispatches fine (Invoke discards it), so
        // it is deliberately NOT flagged.
        private static readonly DiagnosticDescriptor UndispatchableShape = new DiagnosticDescriptor(
            DiagnosticIds.RpcUndispatchableShape,
            "RPC method shape cannot be dispatched",
            "[RtmpeRpc] method '{0}' {1}, so the reflection dispatcher can never invoke it",
            "RTMPE.Rpc", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.RpcUndispatchableShape));

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(
                MustBePublicInstance, UnsupportedParameterType, DuplicateMethodId,
                ReservedMethodIdCollision, RequiresNetworkBehaviour, UndispatchableShape);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var rpcAttribute = context.Compilation.GetTypeByMetadataName(RtmpeRpcAttributeMetadataName);
            if (rpcAttribute is null)
            {
                return; // The SDK's RPC surface is not referenced; there is nothing to analyze.
            }

            var model = new RpcModel(
                rpcAttribute,
                context.Compilation.GetTypeByMetadataName(NetworkBehaviourMetadataName),
                context.Compilation.GetTypeByMetadataName(NetworkSerializableMetadataName),
                context.Compilation);

            context.RegisterSymbolAction(c => AnalyzeMethod(c, model), SymbolKind.Method);
            context.RegisterSymbolAction(c => AnalyzeTypeCollisions(c, model), SymbolKind.NamedType);
        }

        private static void AnalyzeMethod(SymbolAnalysisContext context, RpcModel model)
        {
            var method = (IMethodSymbol)context.Symbol;

            // A partial method surfaces as two symbols (definition + implementation);
            // analyze it once, at its defining declaration.
            if (method.PartialDefinitionPart is not null)
            {
                return;
            }

            if (!model.HasRtmpeRpc(method))
            {
                return;
            }

            if (model.NetworkBehaviour is not null && !InheritsFrom(method.ContainingType, model.NetworkBehaviour))
            {
                context.ReportDiagnostic(Diagnostic.Create(RequiresNetworkBehaviour, PrimaryLocation(method), method.Name));
            }

            if (method.DeclaredAccessibility != Accessibility.Public || method.IsStatic || method.IsAbstract)
            {
                context.ReportDiagnostic(Diagnostic.Create(MustBePublicInstance, PrimaryLocation(method), method.Name));
            }

            if (method.IsGenericMethod)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UndispatchableShape, PrimaryLocation(method), method.Name,
                    "is generic — reflection cannot close its type parameters"));
            }

            foreach (var parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UndispatchableShape, PrimaryLocation(parameter), method.Name,
                        "declares by-reference parameter '" + parameter.Name
                        + "' — a boxed wire argument can never satisfy a ref/out/in slot"));
                }
            }

            foreach (var parameter in method.Parameters)
            {
                if (!model.IsSerializable(parameter.Type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedParameterType, PrimaryLocation(parameter), parameter.Name, method.Name, parameter.Type.ToDisplayString()));
                }
            }
        }

        private static void AnalyzeTypeCollisions(SymbolAnalysisContext context, RpcModel model)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Class
                || model.NetworkBehaviour is null
                || !InheritsFrom(type, model.NetworkBehaviour))
            {
                return;
            }

            var rpcMethods = model.GatherDispatchableRpcMethods(type);
            if (rpcMethods.Count == 0)
            {
                return;
            }

            // The id is the FNV-1a hash of "<qualified type name>.<method name>".
            // The qualified name is what the runtime hashes — an unqualified one
            // would put this rule on a different id space than the wire, so a
            // reserved collision would be reported for methods that do not have
            // one and missed for methods that do. Reserved collisions are reported
            // before sibling duplicates so a method that is both is reported as the
            // stronger fault — matching the runtime validator's precedence.
            var typeName = FullMetadataNameOf(type);
            var seen = new Dictionary<uint, IMethodSymbol>();
            foreach (var method in rpcMethods)
            {
                uint id = Fnv1a.ComputeMethodId(typeName, method.Name);

                if (ReservedRpcIds.IsReserved(id))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        ReservedMethodIdCollision, CollisionLocation(method, type), method.Name, typeName, id.ToString("X8")));
                    continue;
                }

                if (seen.TryGetValue(id, out var prior))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateMethodId, CollisionLocation(method, type), method.Name, typeName, id.ToString("X8"), prior.Name));
                    continue;
                }

                seen[id] = method;
            }
        }

        // The declaration's metadata full name: namespaces joined by '.',
        // containing types by '+', each generic arity as a `n suffix — the
        // spelling System.Type.FullName reports for the same declaration, which
        // is what the runtime derives its ids from. Built from the symbol here
        // because Roslyn exposes no metadata-qualified name of its own, and
        // ToDisplayString spells nesting and arity differently.
        private static string FullMetadataNameOf(INamedTypeSymbol type)
        {
            var name = type.MetadataName;
            for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
            {
                name = containing.MetadataName + "+" + name;
            }

            var prefix = string.Empty;
            for (var ns = type.ContainingNamespace; ns is not null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            {
                prefix = prefix.Length == 0 ? ns.Name : ns.Name + "." + prefix;
            }

            return prefix.Length == 0 ? name : prefix + "." + name;
        }

        private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (var current = type?.BaseType; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }

        private static Location PrimaryLocation(ISymbol symbol)
        {
            foreach (var location in symbol.Locations)
            {
                if (location.IsInSource)
                {
                    return location;
                }
            }

            return symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;
        }

        // An inherited RPC method may be declared in another assembly; fall back
        // to the offending type's location so the diagnostic always lands in
        // source the developer can act on.
        private static Location CollisionLocation(IMethodSymbol method, INamedTypeSymbol type)
        {
            foreach (var location in method.Locations)
            {
                if (location.IsInSource)
                {
                    return location;
                }
            }

            return PrimaryLocation(type);
        }

        private sealed class RpcModel
        {
            private readonly INamedTypeSymbol _rpcAttribute;
            private readonly INamedTypeSymbol _serializable; // null when the SDK interface is absent
            private readonly INamedTypeSymbol _vector3;
            private readonly INamedTypeSymbol _color;
            private readonly INamedTypeSymbol _quaternion;

            public RpcModel(
                INamedTypeSymbol rpcAttribute,
                INamedTypeSymbol networkBehaviour,
                INamedTypeSymbol serializable,
                Compilation compilation)
            {
                _rpcAttribute = rpcAttribute;
                NetworkBehaviour = networkBehaviour;
                _serializable = serializable;
                _vector3 = compilation.GetTypeByMetadataName("UnityEngine.Vector3");
                _color = compilation.GetTypeByMetadataName("UnityEngine.Color");
                _quaternion = compilation.GetTypeByMetadataName("UnityEngine.Quaternion");
            }

            public INamedTypeSymbol NetworkBehaviour { get; }

            public bool HasRtmpeRpc(IMethodSymbol method)
                => method.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _rpcAttribute));

            public bool IsSerializable(ITypeSymbol type)
            {
                switch (type.SpecialType)
                {
                    case SpecialType.System_Int32:
                    case SpecialType.System_Single:
                    case SpecialType.System_Boolean:
                    case SpecialType.System_UInt64:
                    case SpecialType.System_String:
                        return true;
                }

                if (type is IArrayTypeSymbol array && array.Rank == 1 && array.ElementType.SpecialType == SpecialType.System_Byte)
                {
                    return true; // byte[] only — a multidimensional byte array is not encodable.
                }

                if (Matches(type, _vector3) || Matches(type, _color) || Matches(type, _quaternion))
                {
                    return true;
                }

                // A parameter declared as the interface itself is serializable too;
                // AllInterfaces never lists the type as one of its own interfaces.
                return _serializable is not null
                    && (SymbolEqualityComparer.Default.Equals(type, _serializable)
                        || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _serializable)));
            }

            // Mirrors RpcRegistry's discovery scope — public instance methods,
            // including those inherited from a base NetworkBehaviour — collapsed to
            // one slot per signature like GetMethods. Any override supersedes the
            // base slot it overrides, so a base [RtmpeRpc] whose override drops the
            // attribute is no longer dispatchable (the attribute is not inherited);
            // a `new`-shadowed method is not an override and stays distinct (both
            // reach the dispatch table).
            public List<IMethodSymbol> GatherDispatchableRpcMethods(INamedTypeSymbol type)
            {
                var collected = new List<IMethodSymbol>();
                var overridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

                for (var current = type;
                     current is not null && !SymbolEqualityComparer.Default.Equals(current, NetworkBehaviour);
                     current = current.BaseType)
                {
                    foreach (var member in current.GetMembers())
                    {
                        if (member is not IMethodSymbol method
                            || method.MethodKind != MethodKind.Ordinary
                            || method.DeclaredAccessibility != Accessibility.Public
                            || method.IsStatic)
                        {
                            continue;
                        }

                        if (method.IsOverride && method.OverriddenMethod is not null)
                        {
                            overridden.Add(method.OverriddenMethod.OriginalDefinition);
                        }

                        if (HasRtmpeRpc(method))
                        {
                            collected.Add(method);
                        }
                    }
                }

                return collected.Where(m => !overridden.Contains(m.OriginalDefinition)).ToList();
            }

            private static bool Matches(ITypeSymbol type, INamedTypeSymbol candidate)
                => candidate is not null && SymbolEqualityComparer.Default.Equals(type, candidate);
        }
    }
}
