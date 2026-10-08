using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Reports every concrete type that inherits the SDK's
    /// <c>RTMPE.Core.NetworkBehaviour</c>. The rule is informational: it proves
    /// the analyzer pipeline resolves SDK symbols and surfaces diagnostics end
    /// to end before any enforcing rule is introduced.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class Rtmpe1000NetworkBehaviourAnalyzer : DiagnosticAnalyzer
    {
        private const string NetworkBehaviourMetadataName = "RTMPE.Core.NetworkBehaviour";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.NetworkBehaviourSubtype,
            title: "Type inherits NetworkBehaviour",
            messageFormat: "Type '{0}' inherits RTMPE.Core.NetworkBehaviour",
            category: "RTMPE.Usage",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.NetworkBehaviourSubtype));

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            // Resolve the base type once per compilation by its fully-qualified
            // metadata name. The simple name "NetworkBehaviour" is intentionally
            // never matched: UnityEngine and other netcode stacks ship a type of
            // the same name, and matching by simple name would flag them all.
            var networkBehaviour = context.Compilation.GetTypeByMetadataName(NetworkBehaviourMetadataName);
            if (networkBehaviour is null)
            {
                return;
            }

            context.RegisterSymbolAction(
                symbolContext => AnalyzeNamedType(symbolContext, networkBehaviour), SymbolKind.NamedType);
        }

        private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol networkBehaviour)
        {
            var type = (INamedTypeSymbol)context.Symbol;

            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType, networkBehaviour))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, GetReportLocation(type), type.Name));
                    return;
                }
            }
        }

        private static Location GetReportLocation(INamedTypeSymbol type)
        {
            // Prefer the first in-source declaration so the diagnostic is stable
            // for partial types across compilations.
            foreach (var location in type.Locations)
            {
                if (location.IsInSource)
                {
                    return location;
                }
            }

            return type.Locations.Length > 0 ? type.Locations[0] : Location.None;
        }
    }
}
