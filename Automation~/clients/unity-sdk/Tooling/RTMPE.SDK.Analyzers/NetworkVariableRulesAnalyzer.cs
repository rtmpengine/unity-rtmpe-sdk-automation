using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Moves the <c>NetworkVariable</c> rules the runtime enforces at spawn to
    /// compile time: no two members derive one identity, the
    /// variable is constructed in <c>OnNetworkSpawn</c>, a rotation variable is
    /// seeded with <c>Quaternion.identity</c>, and <c>[NetworkVariable]</c> sits
    /// only on a <c>NetworkVariableBase</c> member.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NetworkVariableRulesAnalyzer : DiagnosticAnalyzer
    {
        private const string NetworkVariableBaseMetadataName = "RTMPE.Sync.NetworkVariableBase";
        private const string NetworkBehaviourMetadataName = "RTMPE.Core.NetworkBehaviour";
        private const string NetworkVariableQuaternionMetadataName = "RTMPE.Sync.NetworkVariableQuaternion";
        private const string NetworkVariableAttributeMetadataName = "RTMPE.Sync.NetworkVariableAttribute";
        private const string UnityQuaternionMetadataName = "UnityEngine.Quaternion";
        private const string MemberNameParameterName = "memberName";
        private const string InitialValueParameterName = "initialValue";

        private static readonly DiagnosticDescriptor DuplicateId = new DiagnosticDescriptor(
            DiagnosticIds.NetworkVariableDuplicateId,
            "NetworkVariables derive one identity",
            "Two NetworkVariables on '{0}' both derive their identity from '{1}' — an object registers each identity once, so constructing both throws out of OnNetworkSpawn and the object is destroyed rather than spawned",
            "RTMPE.Sync", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.NetworkVariableDuplicateId),
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        // The same rule, decided from one type alone.  Uniqueness is enforced
        // object-wide at runtime, so a collision against an inherited variable is
        // only knowable once the whole compilation is in hand — but a type that
        // repeats an id among its own constructions needs nothing beyond itself,
        // and reporting that from a symbol action keeps it out of the
        // whole-compilation set the IDE's default open-files scope never runs.
        // Without this arm an Error-severity rule is silent in an editor left on
        // its defaults, and a collision surfaces as a variable that never updates.
        //
        // Identical to the descriptor above in every field the rule reference and
        // the release notes read — id, title, category, severity, help link — so
        // the two are one rule that differs only in what it took to decide.
        private static readonly DiagnosticDescriptor DuplicateIdWithinType = new DiagnosticDescriptor(
            DiagnosticIds.NetworkVariableDuplicateId,
            "NetworkVariables derive one identity",
            "Two NetworkVariables on '{0}' both derive their identity from '{1}' — an object registers each identity once, so constructing both throws out of OnNetworkSpawn and the object is destroyed rather than spawned",
            "RTMPE.Sync", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.NetworkVariableDuplicateId));

        private static readonly DiagnosticDescriptor ConstructedOutsideSpawn = new DiagnosticDescriptor(
            DiagnosticIds.NetworkVariableConstructedOutsideSpawn,
            "NetworkVariable constructed outside OnNetworkSpawn",
            "NetworkVariable should be constructed in OnNetworkSpawn (where ownership is valid), not in {0}",
            "RTMPE.Sync", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.NetworkVariableConstructedOutsideSpawn));

        private static readonly DiagnosticDescriptor QuaternionDefault = new DiagnosticDescriptor(
            DiagnosticIds.NetworkVariableQuaternionDefault,
            "NetworkVariableQuaternion seeded with default, not identity",
            "NetworkVariableQuaternion should be initialised to Quaternion.identity — default(Quaternion) is (0,0,0,0), not a valid rotation",
            "RTMPE.Sync", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.NetworkVariableQuaternionDefault));

        private static readonly DiagnosticDescriptor AttributeOnNonVariable = new DiagnosticDescriptor(
            DiagnosticIds.NetworkVariableAttributeOnNonVariable,
            "[NetworkVariable] on a non-NetworkVariable member",
            "[NetworkVariable] on '{0}' has no effect: its type is not a NetworkVariableBase, so the runtime ignores it",
            "RTMPE.Sync", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.NetworkVariableAttributeOnNonVariable));

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(
                DuplicateId, DuplicateIdWithinType, ConstructedOutsideSpawn, QuaternionDefault, AttributeOnNonVariable);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var networkVariableBase = context.Compilation.GetTypeByMetadataName(NetworkVariableBaseMetadataName);
            if (networkVariableBase is null)
            {
                return; // The SDK's sync surface is not referenced; there is nothing to analyze.
            }

            var model = new NvModel(
                networkVariableBase,
                context.Compilation.GetTypeByMetadataName(NetworkVariableQuaternionMetadataName),
                context.Compilation.GetTypeByMetadataName(NetworkVariableAttributeMetadataName),
                context.Compilation.GetTypeByMetadataName(UnityQuaternionMetadataName),
                context.Compilation.GetTypeByMetadataName(NetworkBehaviourMetadataName));

            // variableId uniqueness is enforced object-wide at runtime (base- and
            // derived-class variables share one tracked list), so it is collected
            // across constructions and resolved over the inheritance chain at the
            // end; RTMPE1011/1012 are decided per construction inline.
            var collector = new VariableIdCollector();
            context.RegisterOperationAction(c => AnalyzeConstruction(c, model, collector), OperationKind.ObjectCreation);
            context.RegisterCompilationEndAction(collector.ReportDuplicates);

            // The half of RTMPE1010 one type settles on its own.  Collected and
            // reported per symbol so the diagnostic reaches an editor analysing
            // open files only; the compilation-end pass above keeps the half that
            // genuinely needs the inheritance chain, and the two are disjoint by
            // construction — see ReportDuplicates.
            context.RegisterSymbolStartAction(OnTypeStart, SymbolKind.NamedType);

            // RTMPE1013 is per-member.
            context.RegisterSymbolAction(c => AnalyzeMember(c, model), SymbolKind.Field, SymbolKind.Property);

            void OnTypeStart(SymbolStartAnalysisContext typeContext)
            {
                if (typeContext.Symbol is not INamedTypeSymbol type)
                {
                    return;
                }

                var own = new OwnIdSites(type);
                typeContext.RegisterOperationAction(c => own.Collect(c, model), OperationKind.ObjectCreation);
                typeContext.RegisterSymbolEndAction(own.ReportDuplicates);
            }
        }

        private static void AnalyzeConstruction(OperationAnalysisContext context, NvModel model, VariableIdCollector collector)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            if (creation.Type is not INamedTypeSymbol created || !model.IsNetworkVariable(created))
            {
                return;
            }

            // RTMPE1011 — construction site. The remedy names OnNetworkSpawn, a
            // hook only a NetworkBehaviour has, so the rule speaks only where that
            // hook exists: a plain helper class holding a NetworkVariable has no
            // later window to move the construction into, and reporting there asks
            // for something the author cannot do.
            string site = ConstructionSite(context.ContainingSymbol);
            if (site is not null && model.HasSpawnHook(context.ContainingSymbol?.ContainingType))
            {
                context.ReportDiagnostic(Diagnostic.Create(ConstructedOutsideSpawn, creation.Syntax.GetLocation(), site));
            }

            // RTMPE1012 — a rotation variable seeded with the zero quaternion.
            if (model.IsQuaternionVariable(created) && SeedsDefaultQuaternion(creation, model.UnityQuaternion))
            {
                context.ReportDiagnostic(Diagnostic.Create(QuaternionDefault, creation.Syntax.GetLocation()));
            }

            // RTMPE1010 — record the name for the end-of-compilation duplicate check.
            if (TryGetContributedId(creation, context.ContainingSymbol, out var owningType, out string id))
            {
                collector.Add(owningType, id, creation.Syntax);
            }
        }

        /// <summary>
        /// The member name a construction contributes to its declaring
        /// type's tracked list, and that type.  False when the construction
        /// contributes none: only a <c>this</c>-owned variable lands on this
        /// object, and only a compile-time constant id can be compared.
        ///
        /// <para>Both arms of RTMPE1010 read the rule's input through here.  The
        /// compilation-end pass subtracts what the per-type pass already reported,
        /// which is sound only while the two agree on what a contribution is —
        /// stating that once removes the second definition that could drift.</para>
        /// </summary>
        private static bool TryGetContributedId(
            IObjectCreationOperation creation,
            ISymbol containingSymbol,
            out INamedTypeSymbol declaringType,
            out string id)
        {
            declaringType = null;
            id = null;

            if (containingSymbol?.ContainingType is not INamedTypeSymbol owner
                || !OwnerIsThis(creation)
                || !TryGetConstantMemberName(creation, out id))
            {
                return false;
            }

            declaringType = owner;
            return true;
        }

        private static void AnalyzeMember(SymbolAnalysisContext context, NvModel model)
        {
            var symbol = context.Symbol;
            if (!symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, model.NetworkVariableAttribute)))
            {
                return;
            }

            var memberType = symbol switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => null,
            };

            if (memberType is not null && !model.IsNetworkVariable(memberType))
            {
                context.ReportDiagnostic(Diagnostic.Create(AttributeOnNonVariable, symbol.Locations[0], symbol.Name));
            }
        }

        private static string ConstructionSite(ISymbol containingSymbol)
        {
            switch (containingSymbol)
            {
                case IFieldSymbol _:
                case IPropertySymbol _:
                    return "a field initializer";
                case IMethodSymbol constructor when constructor.MethodKind == MethodKind.Constructor:
                    return "a constructor";
                // Every message Unity runs before the network object has spawned,
                // read from the one declaration the conversion transform's
                // reachability closure reads too. 🚨 This arm carried three of
                // those seven, so a construction in OnValidate, Reset or a
                // serialization callback was refused by the converter and scored
                // clean here — and a `static void Awake()` or a `Start(int)`,
                // which Unity never dispatches, was reported.
                case IMethodSymbol method when PreSpawnHooks.IsDispatchedAs(method):
                    return method.Name;
                default:
                    return null; // OnNetworkSpawn (or any later call site) is correct.
            }
        }

        private static bool OwnerIsThis(IObjectCreationOperation creation)
        {
            var owner = creation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0);
            return Unwrap(owner?.Value) is IInstanceReferenceOperation;
        }

        // `memberName` is the SDK's own parameter name, not a reserved word: a
        // user-written NetworkVariableBase subclass may declare a parameter of that
        // name with any type at all. Only a string constant is read, which is what
        // `nameof(...)` and a literal both produce; anything else is left
        // uncollected rather than coerced, because a conversion that throws takes
        // the whole analyzer down with it and every rule it owns stops reporting.
        //
        // 🔑 Reading the NAME rather than an id is what keeps this rule alive.
        // Identity is derived from it, so two constructions naming one member on
        // one chain are two members answering to one identity — and the compiler
        // is otherwise content, because both names resolve.
        private static bool TryGetConstantMemberName(IObjectCreationOperation creation, out string memberName)
        {
            memberName = null;
            var arg = creation.Arguments.FirstOrDefault(a => a.Parameter?.Name == MemberNameParameterName);
            if (arg is null || !arg.Value.ConstantValue.HasValue)
            {
                return false;
            }

            memberName = arg.Value.ConstantValue.Value as string;
            return memberName != null;
        }

        private static bool SeedsDefaultQuaternion(IObjectCreationOperation creation, INamedTypeSymbol unityQuaternion)
        {
            var arg = creation.Arguments.FirstOrDefault(a => a.Parameter?.Name == InitialValueParameterName);
            if (arg is null || arg.ArgumentKind == ArgumentKind.DefaultValue)
            {
                return true; // omitted -> defaults to default(Quaternion)
            }

            var value = Unwrap(arg.Value);
            return value is IDefaultValueOperation              // `default` / `default(Quaternion)`
                || IsZeroQuaternion(value, unityQuaternion);    // `new Quaternion(0, 0, 0, 0)`
        }

        // The zero quaternion written as a constructor: `new Quaternion(0, 0, 0,
        // 0)` (and the parameterless `new Quaternion()`) is the same invalid
        // rotation as default(Quaternion). The all-zero requirement is what keeps
        // identity written longhand — `new Quaternion(0, 0, 0, 1)` — silent.
        private static bool IsZeroQuaternion(IOperation value, INamedTypeSymbol unityQuaternion)
            => unityQuaternion is not null
                && value is IObjectCreationOperation creation
                && SymbolEqualityComparer.Default.Equals(creation.Type, unityQuaternion)
                && creation.Arguments.All(a => IsZeroConstant(a.Value));

        private static bool IsZeroConstant(IOperation operation)
        {
            // Each Quaternion component is a float parameter, so the argument's
            // converted constant is a Single even when the component is written as
            // a widening char or integer literal — a direct float test is total.
            var constant = operation.ConstantValue;
            return constant.HasValue && constant.Value is float component && component == 0f;
        }

        private static IOperation Unwrap(IOperation operation)
            => operation is IConversionOperation conversion ? conversion.Operand : operation;

        // Accumulates `this`-owned constant member names per declaring type, then
        // resolves duplicates over each behaviour's inheritance chain — matching
        // the runtime, where base- and derived-class variables share one object.
        private sealed class VariableIdCollector
        {
            private readonly object _gate = new object();

            // Keyed by each type's original definition. A construction inside a
            // generic behaviour is recorded against the open definition, while a
            // subclass names the constructed form — `Derived : Base<int>` — and the
            // default symbol comparer holds those unequal. Both sides normalise here
            // so the chain walk meets the sites a generic base contributes.
            private readonly Dictionary<INamedTypeSymbol, List<IdSite>> _byType =
                new Dictionary<INamedTypeSymbol, List<IdSite>>(SymbolEqualityComparer.Default);

            public void Add(INamedTypeSymbol declaringType, string id, SyntaxNode construction)
            {
                var owner = declaringType.OriginalDefinition;
                lock (_gate)
                {
                    if (!_byType.TryGetValue(owner, out var sites))
                    {
                        sites = new List<IdSite>();
                        _byType[owner] = sites;
                    }

                    sites.Add(new IdSite(id, construction, owner));
                }
            }

            public void ReportDuplicates(CompilationAnalysisContext context)
            {
                foreach (var behaviour in _byType.Keys)
                {
                    // Collisions the type settles alone are the symbol arm's to
                    // report; skipping them here is what keeps one id from being
                    // raised twice against the same construction.
                    var ownGround = new HashSet<SyntaxNode>(
                        Collisions(OrderedSites(_byType, behaviour)).Select(c => c.Site.Construction));

                    foreach (var collision in Collisions(VisibleSites(behaviour)))
                    {
                        var site = collision.Site;

                        // Report once, at the more-derived construction, so a
                        // base-only duplicate is not restated under every subclass.
                        if (!SymbolEqualityComparer.Default.Equals(site.DeclaringType, behaviour))
                        {
                            continue;
                        }

                        // What is left to this pass is the collision that needed the
                        // chain: the id was already held by a construction the type
                        // inherited rather than by one of its own.
                        if (ownGround.Contains(site.Construction)
                            || collision.Held.All(h => SymbolEqualityComparer.Default.Equals(h.DeclaringType, behaviour)))
                        {
                            continue;
                        }

                        context.ReportDiagnostic(Diagnostic.Create(
                            DuplicateId, site.Construction.GetLocation(), behaviour.Name, site.Id));
                    }
                }
            }

            // Sites that land on an id another site already holds, paired with the
            // holders they collided with.  Order-sensitive: the caller supplies the
            // sequence, and the first site to claim an id is the one kept.
            internal static IEnumerable<(IdSite Site, List<IdSite> Held)> Collisions(IEnumerable<IdSite> ordered)
            {
                var holders = new Dictionary<string, List<IdSite>>(StringComparer.Ordinal);

                foreach (var site in ordered)
                {
                    if (!holders.TryGetValue(site.Id, out var held))
                    {
                        holders[site.Id] = new List<IdSite> { site };
                        continue;
                    }

                    // Two constructions in opposite arms of one conditional never
                    // both run, so an id whose seed or type is selected at spawn
                    // is one variable, not a collision. The test runs against
                    // every site already holding the id rather than one of them:
                    // a construction can be exclusive with one arm and still share
                    // an arm with another, and that second pair does collide on
                    // every instance taking it. Comparing against a single
                    // representative would answer differently depending on which
                    // arm the compilation happened to hand over first.
                    if (held.All(holder => SelectedApart(holder.Construction, site.Construction)))
                    {
                        held.Add(site);
                        continue;
                    }

                    yield return (site, held);
                }
            }

            // Source order within one type: file path then position, so a partial
            // class spread over several files still yields a stable sequence and
            // the site kept for an id does not depend on compilation order.
            internal static IEnumerable<IdSite> Ordered(IEnumerable<IdSite> sites)
                => sites
                    .OrderBy(s => s.Construction.SyntaxTree.FilePath, StringComparer.Ordinal)
                    .ThenBy(s => s.Construction.SpanStart);

            private static IEnumerable<IdSite> OrderedSites(
                Dictionary<INamedTypeSymbol, List<IdSite>> byType, INamedTypeSymbol type)
                => byType.TryGetValue(type, out var sites) ? Ordered(sites) : Enumerable.Empty<IdSite>();

            // True when the language guarantees at most one of the two constructions
            // executes: they sit in different arms of the same selection construct.
            // Read from the syntax the two share — the nearest node enclosing both,
            // and the child of it each descends from.
            private static bool SelectedApart(SyntaxNode first, SyntaxNode second)
            {
                if (first.SyntaxTree != second.SyntaxTree)
                {
                    return false;
                }

                var enclosing = new HashSet<SyntaxNode>(first.AncestorsAndSelf());
                var common = second.AncestorsAndSelf().FirstOrDefault(enclosing.Contains);
                if (common is null || common == first || common == second)
                {
                    return false; // one contains the other; both run
                }

                var firstArm = ArmUnder(common, first);
                var secondArm = ArmUnder(common, second);

                switch (common)
                {
                    // The condition is not an arm: it is evaluated whichever way the
                    // selection goes, so a construction inside it always runs.
                    case IfStatementSyntax branch:
                        return (firstArm == branch.Statement && secondArm == branch.Else)
                            || (firstArm == branch.Else && secondArm == branch.Statement);
                    case ConditionalExpressionSyntax ternary:
                        return (firstArm == ternary.WhenTrue && secondArm == ternary.WhenFalse)
                            || (firstArm == ternary.WhenFalse && secondArm == ternary.WhenTrue);
                    // Two sections are exclusive only while control cannot travel
                    // between them: `goto case`, `goto default` and a plain `goto`
                    // to a label in another section each make both run, and the
                    // rule is an Error — a missed collision is cheaper than a
                    // wrong one, but a wrong silence is not free either.
                    case SwitchStatementSyntax switchStatement:
                        return firstArm is SwitchSectionSyntax
                            && secondArm is SwitchSectionSyntax
                            && !switchStatement.DescendantNodes().OfType<GotoStatementSyntax>().Any();
                    case SwitchExpressionSyntax _:
                        return firstArm is SwitchExpressionArmSyntax && secondArm is SwitchExpressionArmSyntax;
                    default:
                        return false;
                }
            }

            // The child of <paramref name="common"/> that <paramref name="node"/>
            // descends from — the arm the construction belongs to.
            private static SyntaxNode ArmUnder(SyntaxNode common, SyntaxNode node)
            {
                for (var current = node; current is not null; current = current.Parent)
                {
                    if (current.Parent == common)
                    {
                        return current;
                    }
                }

                return null;
            }

            // Every construction visible on a behaviour instance: its own and those
            // its base classes contribute, ordered base-first then by source so the
            // result is deterministic across runs.
            private IEnumerable<IdSite> VisibleSites(INamedTypeSymbol behaviour)
            {
                var chain = new List<INamedTypeSymbol>();
                for (var current = behaviour; current is not null; current = current.BaseType)
                {
                    chain.Add(current.OriginalDefinition);
                }

                chain.Reverse();

                foreach (var type in chain)
                {
                    if (!_byType.TryGetValue(type, out var sites))
                    {
                        continue;
                    }

                    foreach (var site in Ordered(sites))
                    {
                        yield return site;
                    }
                }
            }
        }

        // The constructions one type contributes itself, gathered while that type
        // is being analysed rather than across the compilation, so the duplicates
        // it can settle alone are reported from a symbol action.
        private sealed class OwnIdSites
        {
            private readonly object _gate = new object();
            private readonly INamedTypeSymbol _type;

            // One of these exists per named type in the compilation, so the list
            // is created only once a type turns out to contribute an id.  Almost
            // none do, and the analyzer runs again on every edit.
            private List<IdSite> _sites;

            public OwnIdSites(INamedTypeSymbol type) => _type = type.OriginalDefinition;

            public void Collect(OperationAnalysisContext context, NvModel model)
            {
                var creation = (IObjectCreationOperation)context.Operation;
                if (creation.Type is not INamedTypeSymbol created || !model.IsNetworkVariable(created))
                {
                    return;
                }

                if (!TryGetContributedId(creation, context.ContainingSymbol, out var owner, out string id))
                {
                    return;
                }

                // A nested type is its own symbol and receives its own start
                // action, so operations reaching here already belong to this type.
                // The ownership test states that boundary rather than relying on
                // it: an id charged to the wrong type would be an Error raised
                // against source that does not hold it, and the check costs one
                // symbol comparison on a path already doing symbol work.
                if (!SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, _type))
                {
                    return;
                }

                lock (_gate)
                {
                    (_sites ??= new List<IdSite>(2)).Add(new IdSite(id, creation.Syntax, _type));
                }
            }

            public void ReportDuplicates(SymbolAnalysisContext context)
            {
                IdSite[] snapshot;
                lock (_gate)
                {
                    // A single contribution cannot collide with anything, which is
                    // the shape most types that reach here have.
                    if (_sites is null || _sites.Count < 2)
                    {
                        return;
                    }

                    snapshot = _sites.ToArray();
                }

                foreach (var collision in VariableIdCollector.Collisions(VariableIdCollector.Ordered(snapshot)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateIdWithinType,
                        collision.Site.Construction.GetLocation(),
                        _type.Name,
                        collision.Site.Id));
                }
            }
        }

        private readonly struct IdSite
        {
            public IdSite(string id, SyntaxNode construction, INamedTypeSymbol declaringType)
            {
                Id = id;
                Construction = construction;
                DeclaringType = declaringType;
            }

            public string Id { get; }

            public SyntaxNode Construction { get; }

            public INamedTypeSymbol DeclaringType { get; }
        }

        private sealed class NvModel
        {
            private readonly INamedTypeSymbol _networkVariableBase;
            private readonly INamedTypeSymbol _networkVariableQuaternion; // may be null
            private readonly INamedTypeSymbol _networkBehaviour;         // may be null

            public NvModel(
                INamedTypeSymbol networkVariableBase,
                INamedTypeSymbol networkVariableQuaternion,
                INamedTypeSymbol networkVariableAttribute,
                INamedTypeSymbol unityQuaternion,
                INamedTypeSymbol networkBehaviour)
            {
                _networkVariableBase = networkVariableBase;
                _networkVariableQuaternion = networkVariableQuaternion;
                NetworkVariableAttribute = networkVariableAttribute;
                UnityQuaternion = unityQuaternion;
                _networkBehaviour = networkBehaviour;
            }

            public INamedTypeSymbol NetworkVariableAttribute { get; }

            // The UnityEngine.Quaternion value type; null when UnityEngine is not
            // referenced, in which case no quaternion construction can appear.
            public INamedTypeSymbol UnityQuaternion { get; }

            public bool IsNetworkVariable(ITypeSymbol type)
            {
                for (var current = type; current is not null; current = current.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(current, _networkVariableBase))
                    {
                        return true;
                    }
                }

                return false;
            }

            // True when the type reaches OnNetworkSpawn — the window RTMPE1011 tells
            // an author to move a construction into. The SDK's sync surface cannot
            // be referenced without its core, so the null case is a compilation with
            // no NetworkBehaviour in it at all, where the rule has nothing to say.
            public bool HasSpawnHook(INamedTypeSymbol type)
            {
                for (var current = type; current is not null; current = current.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(current, _networkBehaviour))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool IsQuaternionVariable(ITypeSymbol type)
                => _networkVariableQuaternion is not null
                    && SymbolEqualityComparer.Default.Equals(type, _networkVariableQuaternion);
        }
    }
}
