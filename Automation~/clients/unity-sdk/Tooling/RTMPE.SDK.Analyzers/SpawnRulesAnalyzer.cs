using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// RTMPE1030 — a <c>NetworkBehaviour</c> created with <c>Instantiate</c> is
    /// never networked (plan §10/4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <c>SpawnManager.Spawn</c> puts an object on the network.  A copy made
    /// with <c>Instantiate</c> runs every <c>NetworkBehaviour</c> on it with
    /// nothing behind it: no other client sees it, none of its variables
    /// replicate, and a write to one is refused as an unspawned object's.  The
    /// SDK's own troubleshooting says so, and until this rule nothing in the
    /// toolchain did at the place it happens.
    /// </para>
    /// <para>
    /// ⛔ Reported only where the source proves the object carries one — the
    /// original is a <c>NetworkBehaviour</c>, or the result is asked for one.  A
    /// prefab held as a <c>GameObject</c> is silent here: it is as often a UI
    /// panel or an effect, and saying so for every one of them is the noise the
    /// readiness report's decision S.9 refused.  That case is advice on the type
    /// instead (the authority classification's recommendations).
    /// </para>
    /// <para>
    /// Two places are the spawn path rather than a bypass of it and are not read:
    /// an object pool the SDK draws instances from, and the SDK's own assemblies,
    /// which is where spawning is implemented.  Nor is code whose copies are made
    /// on purpose and never ship in a player: Unity's editor assemblies for
    /// <c>Editor</c> folders, a type the editor drives, and a test
    /// (<see cref="LocalInstantiation.IsExemptAssembly"/>,
    /// <see cref="LocalInstantiation.IsEditorType"/>,
    /// <see cref="LocalInstantiation.IsTestType"/>).  A project's own editor-only
    /// assembly definition is read: its name is the project's.
    /// </para>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SpawnRulesAnalyzer : DiagnosticAnalyzer
    {
        private const string NetworkBehaviourMetadataName = "RTMPE.Core.NetworkBehaviour";
        private const string PoolInterfaceMetadataName = "RTMPE.Core.INetworkObjectPool";

        private static readonly DiagnosticDescriptor InstantiatedNetworkBehaviour = new DiagnosticDescriptor(
            DiagnosticIds.InstantiatedNetworkBehaviour,
            "Instantiate creates a networked object nothing spawns",
            "'{0}' creates an object carrying {1} with Instantiate, so it is never networked — no other client sees it and none of its NetworkVariables replicate; spawn it with SpawnManager.Spawn from a registered prefab id, or suppress this where the copy is meant to stay local",
            "RTMPE.Usage", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.InstantiatedNetworkBehaviour));

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(InstantiatedNetworkBehaviour);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            // By metadata name, never by simple name: another netcode stack ships a
            // type called NetworkBehaviour too.
            var networkBehaviour = context.Compilation.GetTypeByMetadataName(NetworkBehaviourMetadataName);
            if (networkBehaviour is null || LocalInstantiation.IsExemptAssembly(context.Compilation.Assembly))
            {
                return;
            }

            var pool = context.Compilation.GetTypeByMetadataName(PoolInterfaceMetadataName);
            context.RegisterSyntaxNodeAction(
                c => AnalyzeInvocation(c, networkBehaviour, pool), SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(
            SyntaxNodeAnalysisContext context, INamedTypeSymbol networkBehaviour, INamedTypeSymbol pool)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (LocalInstantiation.Classify(invocation, context.SemanticModel, networkBehaviour, out var networkedType)
                != InstantiationKind.NetworkedObject)
            {
                return;
            }

            var containingType = context.ContainingSymbol?.ContainingType
                                 ?? context.ContainingSymbol as INamedTypeSymbol;
            if (LocalInstantiation.IsNetworkObjectPool(containingType, pool)
                || LocalInstantiation.IsEditorType(containingType)
                || LocalInstantiation.IsTestType(containingType))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                InstantiatedNetworkBehaviour,
                invocation.GetLocation(),
                containingType?.Name ?? "This code",
                networkedType.Name));
        }
    }
}
