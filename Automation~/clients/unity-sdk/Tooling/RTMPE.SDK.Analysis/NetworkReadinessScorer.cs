using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using RTMPE.SDK.Analyzers;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Analysis
{
    /// <summary>
    /// Computes the deterministic Network Readiness Score (§7) over a
    /// <see cref="Compilation"/>. The structural, RPC, and lifecycle dimensions
    /// are the shipped analyzers' own verdicts — the engine runs them and reads
    /// each diagnostic against the type that carries the fault, attributing a
    /// fault a concrete type inherits from a base to that type, so a leaf is
    /// scored on the members that actually run for it. Ownership is a positive
    /// source check the analyzers do not emit, judged on every frame loop the
    /// type runs — its own or an inherited one; state is a positive source check
    /// qualified by the analyzers' own NetworkVariable correctness faults. The
    /// result is pure and host-neutral: identical in CI, in a test, and behind
    /// the Editor window.
    /// </summary>
    public static class NetworkReadinessScorer
    {
        private const string NetworkBehaviourMetadataName = "RTMPE.Core.NetworkBehaviour";
        private const string NetworkVariableBaseMetadataName = "RTMPE.Sync.NetworkVariableBase";
        private const string IsOwnerPropertyName = "IsOwner";

        private static readonly string[] RpcFaultIds =
        {
            DiagnosticIds.RpcMustBePublicInstance,
            DiagnosticIds.RpcUnsupportedParameterType,
            DiagnosticIds.RpcDuplicateMethodId,
            DiagnosticIds.RpcReservedMethodIdCollision,
            DiagnosticIds.RpcRequiresNetworkBehaviour,
            DiagnosticIds.RpcUndispatchableShape,
        };

        private static readonly string[] LifecycleFaultIds =
        {
            DiagnosticIds.NetworkVariableConstructedOutsideSpawn,
            DiagnosticIds.LifecycleMissingBaseOnDestroy,
            DiagnosticIds.LifecycleHookSignatureMismatch,
        };

        // Replicated-state correctness faults: a duplicate variableId throws at
        // first spawn, and a default-initialised quaternion is an invalid rotation.
        // Either means the state is present but unsound, so it does not earn State.
        private static readonly string[] StateFaultIds =
        {
            DiagnosticIds.NetworkVariableDuplicateId,
            DiagnosticIds.NetworkVariableQuaternionDefault,
        };

        // A fully-qualified, global::-free type name — the stable key that joins
        // an enumerated type to the diagnostics bucketed under it and orders the
        // report deterministically.
        private static readonly SymbolDisplayFormat FullNameFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

        /// <summary>
        /// Scores every concrete <c>NetworkBehaviour</c> declared in
        /// <paramref name="compilation"/>. Returns an empty report when the SDK's
        /// core surface is not referenced (nothing is networked to score).
        /// </summary>
        public static ReadinessReport Score(Compilation compilation)
            => Score(compilation, null, null);

        /// <summary>
        /// Scores <paramref name="compilation"/> with the authority answers the
        /// project has recorded.
        /// </summary>
        /// <param name="answers">
        /// The project's answers to the questions of a previous scan, or null.
        /// </param>
        /// <remarks>
        /// 🔑 An answer is applied only where this scan ASKED — every other one is
        /// reported in the to-do list with the reason, never dropped. A stale
        /// answer for a renamed type, an answer over a chain the reader could not
        /// open, and an answer the code has since made unnecessary all look
        /// identical from the file; silence over any of them is a project whose
        /// score stopped depending on its answers without saying so.
        /// </remarks>
        public static ReadinessReport Score(Compilation compilation, IReadOnlyList<AuthorityAnswer> answers)
            => Score(compilation, answers, null);

        /// <summary>
        /// Scores <paramref name="compilation"/> and carries the project's
        /// runtime record through beside the score.
        /// </summary>
        /// <param name="runtime">
        /// What a run reported, or null for a project no run has reported on.
        /// </param>
        /// <remarks>
        /// ⛔ <paramref name="runtime"/> is CARRIED, never derived. Nothing this
        /// method computes can turn a runtime check green: the outcomes arrive
        /// whole from the record and are merged onto the untested five by
        /// <see cref="RuntimeVerification.Apply"/>, which is the entirety of the
        /// path an outcome can take. That is what makes "no run, no green row" a
        /// property of the code rather than a convention — and it is why the
        /// runtime block sits outside the score rather than as a seventh
        /// dimension, where every existing weight would have had to move.
        /// </remarks>
        public static ReadinessReport Score(
            Compilation compilation,
            IReadOnlyList<AuthorityAnswer> answers,
            IReadOnlyList<RuntimeCheck> runtime)
        {
            if (compilation is null) throw new ArgumentNullException(nameof(compilation));

            var networkBehaviour = compilation.GetTypeByMetadataName(NetworkBehaviourMetadataName);
            if (networkBehaviour is null)
            {
                // Nothing here is networked, so no question was asked and no
                // answer can apply — which is exactly the case an answers file
                // must not pass through in silence.
                return new ReadinessReport(
                    0,
                    Array.Empty<TypeReadiness>(),
                    UnappliedAnswerReports(answers, Array.Empty<AuthorityQuestion>(), null),
                    Array.Empty<AuthorityInsight>(),
                    Array.Empty<AuthorityQuestion>(),
                    runtime);
            }

            var networkVariableBase = compilation.GetTypeByMetadataName(NetworkVariableBaseMetadataName);
            var ownedState = OwnedStateWrites.Surface.Resolve(compilation);
            var isOwner = networkBehaviour.GetMembers(IsOwnerPropertyName).OfType<IPropertySymbol>().FirstOrDefault();
            var diagnosticsByType = BucketAnalyzerDiagnosticsByType(compilation, out var analyzerFaults);

            // The Phase-5 layer: one dependency graph + one classification pass
            // feed both the per-type Authority dimension and the advisory block,
            // so the score and the report cannot disagree.
            var sdk = AuthoritySdkSymbols.Resolve(compilation);
            var graph = DependencyGraphBuilder.Build(compilation, sdk);
            var authorityVerdicts = new Dictionary<string, AuthorityVerdict>(StringComparer.Ordinal);
            var questions = new List<AuthorityQuestion>();
            foreach (var node in graph.Nodes)
            {
                authorityVerdicts[node.TypeName] = AuthorityClassifier.Classify(node.Signals);

                var question = AuthorityQuestionnaire.For(node.TypeName, node.Signals);
                if (question is not null) questions.Add(question);
            }

            // The answers this scan can honour: the ones naming a type it asked
            // about. Built from the question list rather than from the answer
            // list, so the set of applied answers is a subset of the set of asked
            // questions by construction rather than by agreement.
            var asked = new HashSet<string>(questions.Select(q => q.TypeName), StringComparer.Ordinal);
            var declarations = new Dictionary<string, AuthorityAnswer>(StringComparer.Ordinal);
            var answeredTwice = new List<string>();
            if (answers is not null)
            {
                foreach (var answer in answers)
                {
                    if (!asked.Contains(answer.TypeName)) continue;

                    // 🔑 The answers FILE refuses a type answered twice, because
                    // choosing between a developer's own two decisions is not the
                    // tool's to make. This entry point is also a library call, and
                    // a caller that passes both gets the last one — deterministic,
                    // and said out loud rather than resolved in silence.
                    if (declarations.ContainsKey(answer.TypeName)) answeredTwice.Add(answer.TypeName);
                    declarations[answer.TypeName] = answer;
                }
            }

            var scoredTypes = SourceTypes(compilation)
                .Where(t => t.TypeKind == TypeKind.Class && !t.IsAbstract && !t.IsStatic)
                .Where(t => InheritsFrom(t, networkBehaviour))
                .Select(t => ScoreType(
                    t, networkBehaviour, networkVariableBase, ownedState, isOwner, compilation,
                    diagnosticsByType, authorityVerdicts, declarations, sdk))
                .OrderBy(t => t.TypeName, StringComparer.Ordinal)
                .ToList();

            int projectScore = scoredTypes.Count == 0
                ? 0
                : (int)Math.Round(scoredTypes.Average(t => t.Score), MidpointRounding.AwayFromZero);

            var todo = scoredTypes
                .SelectMany(t => t.Dimensions
                    .Where(d => !d.Cleared)
                    .Select(d => $"{t.TypeName}: {d.Dimension} — {d.Detail}"))
                .ToList();

            // ⛔ A class whose base chain the compilation could not resolve is
            // neither scored nor mentioned, and the project score is the MEAN of
            // what survived — so a type that failed to bind raises the number by
            // leaving. That is a silent subset: the reader cannot tell a project
            // of four clean types from a project of five where one did not
            // resolve. Naming them makes the score describe a stated subset.
            //
            // ⛔ NOT a refusal. This host compiles against a 25-type contract
            // stub, never against UnityEngine, so a correct project routinely
            // carries error diagnostics — the five shipped samples carry 73, and
            // every one of them is `GetComponent`, `Time.time`, `Transform.forward`
            // or a project type the stub does not model. Refusing on that verdict
            // would refuse nearly every real project, which is the same trade the
            // conversion compile gate already settled by being differential.
            todo.AddRange(UnresolvableTypes(compilation)
                .Select(name => $"{name}: Unscored — its base type did not resolve in this "
                                + "compilation, so it could not be classified; the score is the "
                                + "mean of the types that could be"));

            // 🔑 And the same discipline for a rule that did not run to the end. The
            // Structural, RPC and Lifecycle dimensions are the analyzers' own
            // verdicts, so an analyzer that threw leaves every type it would have
            // judged reading "clean" — named here, per crash, so the reader knows
            // which verdicts were never actually reached.
            todo.AddRange(analyzerFaults
                .Distinct(StringComparer.Ordinal)
                .Select(fault => "an analyzer this score relies on threw, so the Structural, RPC and "
                                 + "Lifecycle verdicts above were not all reached — " + fault));

            // 🔑 The same discipline one dimension in. A type whose Ownership
            // weight was granted over writes the rule could not read is scored,
            // and the reader is told which — a clear nobody can see the reservation
            // on is the silent route to a hundred that this list exists to close.
            todo.AddRange(scoredTypes
                .Where(scored => scored.Dimensions.Any(
                    verdict => verdict.Dimension == ReadinessDimension.Ownership
                        && verdict.Cleared
                        && verdict.Detail == OwnershipClearedOnAPartialReading))
                .Select(scored => $"{scored.TypeName}: Ownership was cleared on a partial reading — a "
                                  + "write in one of its frame loops rests on a type this compilation "
                                  + "did not resolve, so whether that loop drives owner-owned state is "
                                  + "unknown rather than answered"));

            // An answer the scan could not honour. Reported here rather than
            // beside the type it names, because in the two commonest cases —
            // a renamed type, a deleted one — there is no such entry to sit beside.
            todo.AddRange(UnappliedAnswerReports(answers, questions, authorityVerdicts));
            todo.AddRange(answeredTwice
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => name + ": more than one authority answer names this type; the last was "
                                + "applied — leave exactly one, which is what the answers file itself "
                                + "requires"));

            return new ReadinessReport(
                projectScore,
                scoredTypes,
                todo,
                BuildAuthorityInsights(graph, authorityVerdicts, declarations),
                questions,
                runtime);
        }

        // Why each answer the scan did not apply was not applied, ordered by type
        // name so two runs over one file render identical bytes.
        //
        // 🔑 One rule with its reasons enumerated, never a silent drop: an answer
        // is the developer's own work, and an answers file that has quietly
        // stopped moving the score is the failure this whole surface exists to
        // avoid — a tool that promises and does not deliver.
        private static IReadOnlyList<string> UnappliedAnswerReports(
            IReadOnlyList<AuthorityAnswer> answers,
            IReadOnlyList<AuthorityQuestion> questions,
            IReadOnlyDictionary<string, AuthorityVerdict> verdicts)
        {
            if (answers is null || answers.Count == 0) return Array.Empty<string>();

            var asked = new HashSet<string>(questions.Select(q => q.TypeName), StringComparer.Ordinal);
            var reports = new List<string>();
            foreach (var answer in answers.OrderBy(a => a.TypeName, StringComparer.Ordinal))
            {
                if (asked.Contains(answer.TypeName)) continue;

                string prefix = answer.TypeName + ": the recorded authority answer ("
                    + AuthorityQuestionnaire.IdOf(answer.DecidedBy) + ") was not applied — ";

                // ⛔ Its own reason, not the stale-answer one. This compilation
                // did not resolve the SDK's core surface, so nothing in it was
                // classified at all — telling the developer their type was
                // renamed or deleted would send them looking for a fault that is
                // not in their project.
                if (verdicts is null)
                {
                    reports.Add(prefix + "this scan resolved no RTMPE.Core.NetworkBehaviour, so it "
                        + "classified nothing and asked nothing — the compilation carries no "
                        + "networked surface");
                    continue;
                }

                if (verdicts.Count == 0)
                {
                    // ⛔ Not "your type is gone". The classifier produced no nodes
                    // at all — the engine's Unity surface did not resolve here —
                    // and the type this answer names may be sitting in the scored
                    // table two lines above. Sending a developer to look for a
                    // rename they did not make is worse than saying nothing.
                    reports.Add(prefix + "this scan classified no component types at all, so it asked "
                        + "nothing; the compilation did not resolve the engine surface the rubric "
                        + "reads");
                }
                else if (!verdicts.TryGetValue(answer.TypeName, out var verdict))
                {
                    reports.Add(prefix + "this scan found no such component type; it was renamed, "
                        + "moved out of the scanned directories, or deleted, and the answer is stale");
                }
                else if (verdicts[answer.TypeName].Role != AuthorityRole.Undetermined)
                {
                    reports.Add(prefix + "the code now says it for itself ("
                        + verdicts[answer.TypeName].Role
                        + "), so there is no longer a question here and the answer is spent");
                }
                else
                {
                    reports.Add(prefix + "the rubric left this type Undetermined for a reason a "
                        + "declaration cannot settle — either a base on its chain was not readable, "
                        + "or it carries networking signals without inheriting NetworkBehaviour "
                        + "(RTMPE2001), and the runtime discovers none of them there");
                }
            }

            return reports;
        }

        // The advisory block over the graph node set — MonoBehaviour orchestrators
        // and presentation leaves included — with the edge-derived annotations:
        // who depends on authority, and who writes another type's networked state.
        private static IReadOnlyList<AuthorityInsight> BuildAuthorityInsights(
            AuthorityGraph graph,
            IReadOnlyDictionary<string, AuthorityVerdict> verdicts,
            IReadOnlyDictionary<string, AuthorityAnswer> declarations)
        {
            var insights = new List<AuthorityInsight>(graph.Nodes.Count);
            foreach (var node in graph.Nodes)
            {
                var verdict = verdicts[node.TypeName];

                var dependsOn = new List<string>();
                var recommendations = new List<string>(verdict.Recommendations);
                var evidence = new List<string>(verdict.Evidence);

                // The project's own answer, where it gave one. It rides in the
                // evidence list as well as its own field, so every surface that
                // already renders evidence says where the verdict came from
                // without being touched — and the rubric's own recommendation
                // above it still stands, because declaring who decides is not
                // writing the code that makes it so.
                declarations.TryGetValue(node.TypeName, out var declaration);
                if (declaration is not null)
                {
                    var option = AuthorityQuestionnaire.OptionFor(declaration.DecidedBy);
                    evidence.Add(AuthorityQuestionnaire.DeclarationEvidence(declaration.DecidedBy));
                    recommendations.Add("declared " + option.Label + " ⇒ " + option.Consequence);
                    foreach (string duty in AuthorityQuestionnaire.Responsibilities(declaration.DecidedBy))
                    {
                        recommendations.Add("responsibility — " + duty);
                    }
                }
                foreach (var edge in graph.Edges)
                {
                    if (edge.From != node.TypeName || !verdicts.TryGetValue(edge.To, out var target))
                    {
                        continue;
                    }

                    // A self-edge couples two instances of one type. That is
                    // navigation for every kind but a replicated write, where a
                    // non-owner instance mutating a peer's networked state is a real
                    // cross-instance hazard — the one self-edge the builder emits.
                    bool crossInstance = edge.To == node.TypeName;
                    if (crossInstance && edge.Kind != AuthorityEdgeKind.WriteReplicated)
                    {
                        continue;
                    }

                    bool targetCarriesAuthority = target.Role == AuthorityRole.Authoritative
                        || target.Role == AuthorityRole.OwnerPartitioned;
                    if (!targetCarriesAuthority)
                    {
                        continue;
                    }

                    // A peer-instance write is the type depending on itself, which
                    // reads as noise in the depends-on list; the advisory below
                    // carries the actual finding.
                    if (!crossInstance)
                    {
                        dependsOn.Add($"{edge.To} ({edge.Kind})");
                    }

                    // The authority-mismatch advisory fires only on a replicated
                    // write — other.Variable.Value = x — into an authoritative type,
                    // which bypasses the owner partition on this side. A write to a
                    // plain member changes only the local copy and never replicates,
                    // so it stays a dependency without the advisory.
                    if (edge.Kind == AuthorityEdgeKind.WriteReplicated && target.Role == AuthorityRole.Authoritative)
                    {
                        recommendations.Add(crossInstance
                            ? $"writes another {node.TypeName} instance's networked state — a "
                                + "non-owner instance mutating a peer's replicated state bypasses the "
                                + "owner partition; route the write through the owning instance or a "
                                + "Server-targeted Enhanced-RPC (" + AuthorityClassifier.ServerRpcCostNote + ")"
                            : $"writes networked state of {edge.To} from outside — route the write "
                                + "through the owning type or a Server-targeted Enhanced-RPC ("
                                + AuthorityClassifier.ServerRpcCostNote + ")");
                    }
                }

                insights.Add(new AuthorityInsight(
                    node.TypeName, verdict.Role, evidence, recommendations, dependsOn,
                    declaration?.DecidedBy, declaration?.Note));
            }

            return insights;
        }

        private static TypeReadiness ScoreType(
            INamedTypeSymbol type,
            INamedTypeSymbol networkBehaviour,
            INamedTypeSymbol networkVariableBase,
            OwnedStateWrites.Surface ownedState,
            IPropertySymbol isOwner,
            Compilation compilation,
            IReadOnlyDictionary<string, HashSet<string>> diagnosticsByType,
            IReadOnlyDictionary<string, AuthorityVerdict> authorityVerdicts,
            IReadOnlyDictionary<string, AuthorityAnswer> declarations,
            AuthoritySdkSymbols sdk)
        {
            var faults = InheritedFaults(type, networkBehaviour, diagnosticsByType);

            var dimensions = new[]
            {
                // Structural is satisfied by selection: only NetworkBehaviour
                // subclasses reach here, which is exactly what RTMPE1000 reports.
                new DimensionVerdict(ReadinessDimension.Structural, true, "inherits RTMPE.Core.NetworkBehaviour"),
                StateVerdict(type, networkBehaviour, networkVariableBase, faults, compilation),
                OwnershipVerdict(type, networkBehaviour, ownedState, isOwner, compilation),
                CleanRuleVerdict(ReadinessDimension.Rpc, faults, RpcFaultIds, "RPC methods"),
                CleanRuleVerdict(ReadinessDimension.Lifecycle, faults, LifecycleFaultIds, "lifecycle hooks"),
                AuthorityDimensionVerdict(type, compilation, authorityVerdicts, declarations, sdk),
            };

            return new TypeReadiness(FullName(type), dimensions);
        }

        // Authority — the Phase-5 rubric must assign the type a discernible role.
        // Every scored type is a graph node whenever the Unity surface resolves;
        // the direct-extraction fallback keeps the dimension total even when it
        // does not (the graph is then empty by construction).
        private static DimensionVerdict AuthorityDimensionVerdict(
            INamedTypeSymbol type,
            Compilation compilation,
            IReadOnlyDictionary<string, AuthorityVerdict> authorityVerdicts,
            IReadOnlyDictionary<string, AuthorityAnswer> declarations,
            AuthoritySdkSymbols sdk)
        {
            string name = FullName(type);
            if (!authorityVerdicts.TryGetValue(name, out var verdict))
            {
                verdict = AuthorityClassifier.Classify(AuthoritySignalExtractor.Extract(type, compilation, sdk));
            }

            if (verdict.Role != AuthorityRole.Undetermined)
            {
                return new DimensionVerdict(ReadinessDimension.Authority, true, $"authority posture: {verdict.Role}");
            }

            // 🔑 The dimension asks whether the type's authority is DISCERNIBLE,
            // and an answer the project gave is a way of discerning it — the one
            // this rubric is forbidden to reach on its own. It clears this weight
            // and nothing else: State and Ownership still measure the code, so a
            // declaration buys the ten points it settles and never the forty it
            // does not.
            if (declarations.TryGetValue(name, out var declaration))
            {
                return new DimensionVerdict(
                    ReadinessDimension.Authority,
                    true,
                    AuthorityQuestionnaire.DeclarationEvidence(declaration.DecidedBy)
                        + " — the rubric derived none from the code");
            }

            return new DimensionVerdict(ReadinessDimension.Authority, false,
                verdict.Recommendations.Count > 0
                    ? verdict.Recommendations[0]
                    : "no discernible authority posture");
        }

        // Every analyzer fault that affects this type — its own, plus those an
        // inherited member carries on a base up to (not including) NetworkBehaviour.
        // A fault's diagnostic is bucketed under the type whose declaration encloses
        // it, so a leaf that inherits a leaky OnDestroy or a colliding RPC is scored
        // on it exactly as the runtime and the IDE would surface it.
        private static IReadOnlyCollection<string> InheritedFaults(
            INamedTypeSymbol type,
            INamedTypeSymbol networkBehaviour,
            IReadOnlyDictionary<string, HashSet<string>> diagnosticsByType)
        {
            HashSet<string> aggregate = null;
            for (var current = type;
                 current is not null && !SymbolEqualityComparer.Default.Equals(current, networkBehaviour);
                 current = current.BaseType)
            {
                if (diagnosticsByType.TryGetValue(FullName(current), out var ids))
                {
                    (aggregate ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(ids);
                }
            }

            return aggregate ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        }

        // State — replicated state must live in a NetworkVariable. A [SerializeField]
        // config field is Inspector data, never replicated state, so it is neither
        // counted nor penalised; only a NetworkVariableBase member earns the weight.
        private static DimensionVerdict StateVerdict(
            INamedTypeSymbol type, INamedTypeSymbol networkBehaviour, INamedTypeSymbol networkVariableBase,
            IReadOnlyCollection<string> faults, Compilation compilation)
        {
            if (networkVariableBase is null)
            {
                return new DimensionVerdict(ReadinessDimension.State, false, "the SDK sync surface is not referenced");
            }

            // Present-but-unsound state (a duplicate variableId, a default rotation)
            // does not earn the weight, even where a NetworkVariable member exists.
            var stateFaults = StateFaultIds.Where(faults.Contains).ToList();
            if (stateFaults.Count > 0)
            {
                return new DimensionVerdict(ReadinessDimension.State, false,
                    $"NetworkVariable state reports {string.Join(", ", stateFaults.OrderBy(id => id, StringComparer.Ordinal))}");
            }

            var declared = new List<ISymbol>();
            for (var current = type;
                 current is not null && !SymbolEqualityComparer.Default.Equals(current, networkBehaviour);
                 current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    var memberType = ReplicableMemberType(member);
                    if (memberType is not null && DerivesFrom(memberType, networkVariableBase))
                    {
                        declared.Add(member);
                    }
                }
            }

            if (declared.Count == 0)
            {
                return new DimensionVerdict(
                    ReadinessDimension.State, false, "no NetworkVariable — no replicated state detected");
            }

            // ⛔ Declared is not replicated. A `NetworkVariableInt _health;` that
            // nothing constructs is null for the object's whole life: it registers
            // with no behaviour, the flush loop never reaches it, and no peer ever
            // sees the value. Counting declarations cleared this dimension for a
            // type that replicates nothing — the one thing the dimension is for.
            var unconstructed = declared
                .Where(m => !IsConstructedSomewhere(m, compilation))
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            int count = declared.Count - unconstructed.Count;
            if (count == 0)
            {
                return new DimensionVerdict(ReadinessDimension.State, false,
                    $"declared but never constructed: {string.Join(", ", unconstructed)}"
                    + " — a NetworkVariable nothing constructs is null and replicates nothing");
            }

            return unconstructed.Count == 0
                ? new DimensionVerdict(
                    ReadinessDimension.State, true, $"{count} replicated NetworkVariable member(s)")
                : new DimensionVerdict(
                    ReadinessDimension.State, true,
                    $"{count} replicated NetworkVariable member(s); declared but never constructed: "
                    + string.Join(", ", unconstructed));
        }

        /// <summary>
        /// The Ownership dimension's fourth clearing arm: the weight was granted,
        /// and one of the writes it was granted over could not be read.
        /// </summary>
        /// <remarks>
        /// 🔑 A const because two readers spend it — the dimension states it, and
        /// the report's to-do is derived from the types that carry it. A second
        /// spelling of the same sentence is a to-do list that silently empties the
        /// day somebody rewords the dimension.
        /// </remarks>
        private const string OwnershipClearedOnAPartialReading =
            "cleared on a partial reading — a write in a frame loop rests on a type that did not resolve";

        // How one frame loop stands against the owner rule. Named rather than
        // returned as a bool pair because the dimension reports the FIRST loop
        // that faults and must say which of the two faults it is.
        private enum LoopStanding
        {
            Guarded,
            DrivesNoStateOfItsOwn,
            Unguarded,
            SurfaceUnreadable,
            WritesUnreadable,
        }

        // Ownership — a frame loop that drives owner-owned state must open with
        // the owner guard so non-owners do not drive simulation. A type with no
        // frame loop, own or inherited, has no loop to fault, so the dimension is
        // cleared.
        //
        // 🔑 Every loop Unity drives is judged, and one faulting loop faults the
        // dimension. Scoring Update alone awarded the weight to a type whose
        // owned state moves in FixedUpdate or LateUpdate — the arm that FLATTERS,
        // on the two loops physics-driven movement and follow cameras are
        // actually written in.
        private static DimensionVerdict OwnershipVerdict(
            INamedTypeSymbol type, INamedTypeSymbol networkBehaviour, OwnedStateWrites.Surface ownedState,
            IPropertySymbol isOwner, Compilation compilation)
        {
            var loops = EffectiveFrameLoops(type, networkBehaviour, compilation);

            if (loops.Count == 0)
            {
                return new DimensionVerdict(ReadinessDimension.Ownership, true, "no frame loop to guard");
            }

            bool everyLoopDrivesNothing = true;
            bool anyLoopUnreadable = false;
            foreach (var loop in loops)
            {
                switch (Judge(loop, ownedState, isOwner, type, compilation))
                {
                    case LoopStanding.DrivesNoStateOfItsOwn:
                        break;

                    case LoopStanding.Guarded:
                        everyLoopDrivesNothing = false;
                        break;

                    // ⛔ Where the surface is incomplete the drives-nothing rule
                    // answers `false` for two different reasons and says the same
                    // word for both. Clearing on it awards the weight to a moving
                    // object because a symbol did not resolve — the direction that
                    // flatters the projects this understands least — so an
                    // unreadable surface falls through to the fault, and the
                    // reason says which of the two it is.
                    case LoopStanding.SurfaceUnreadable:
                        return new DimensionVerdict(
                            ReadinessDimension.Ownership, false,
                            $"{loop.Name} is unguarded and the surface this is scored against could not be read");

                    // ⛔ A write whose type did not bind is neither a clear nor a
                    // fault, and the reason it is not a fault was MEASURED rather
                    // than reasoned: this host compiles against a 25-type contract
                    // and never against UnityEngine, so a held `Rigidbody`, a UI
                    // `Text` or a `CharacterController` all arrive here unbound —
                    // and faulting them deducts the weight from the presentation
                    // loop this rule exists to leave alone. What it costs instead
                    // is the CLAIM: the dimension still clears and says it cleared
                    // on a partial reading, and the type is named in the report.
                    case LoopStanding.WritesUnreadable:
                        anyLoopUnreadable = true;
                        break;

                    // Named rather than reached through `default`, so a standing
                    // added to the enum later is a compiler error here instead of
                    // a loop silently reported as unguarded.
                    case LoopStanding.Unguarded:
                        return new DimensionVerdict(
                            ReadinessDimension.Ownership, false,
                            $"{loop.Name} does not open with `if (!IsOwner) return;`");

                    default:
                        throw new InvalidOperationException(
                            "unhandled loop standing for " + loop.Name);
                }
            }

            // ⛔ Asked FIRST, and of the clear rather than of the fault. A loop
            // whose writes could not all be read did not earn either of the two
            // statements below — it earned neither the finding nor the absence of
            // one — so the weight is granted with the reservation attached rather
            // than under a sentence that is not true of it.
            if (anyLoopUnreadable)
            {
                return new DimensionVerdict(ReadinessDimension.Ownership, true, OwnershipClearedOnAPartialReading);
            }

            return everyLoopDrivesNothing
                ? new DimensionVerdict(
                    ReadinessDimension.Ownership, true, "no frame loop writes state of its own to guard")
                : new DimensionVerdict(
                    ReadinessDimension.Ownership, true,
                    "every frame loop that drives its own state opens with the IsOwner guard");
        }

        private static LoopStanding Judge(
            IMethodSymbol loop, OwnedStateWrites.Surface ownedState, IPropertySymbol isOwner,
            INamedTypeSymbol type, Compilation compilation)
        {
            // ⚠️ A partial method's SYMBOL is its declaring half, which has no
            // body — so reading the first syntax reference judged `partial void
            // Update();` and found it drives nothing, while the implementation one
            // file over moved the transform. The helper walk already asks for the
            // implementation by name; the loop has to as well.
            var declaration = (loop.PartialImplementationPart ?? loop)
                .DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                as MethodDeclarationSyntax;

            if (isOwner is not null
                && declaration?.Body?.Statements.FirstOrDefault() is IfStatementSyntax guard
                && OwnerGuardSyntax.IsOwnerGuard(guard, compilation.GetSemanticModel(declaration.SyntaxTree), isOwner))
            {
                return LoopStanding.Guarded;
            }

            // ⛔ A loop that drives no owner-owned state is not short of a guard —
            // it is a loop that must run on every client, and a guard there is
            // exactly wrong. Faulting it deducted a fifth of the score for being
            // correct, and pointed the author at the one edit that would break
            // the game: the project that reported this applied the guard, watched
            // both clients sit on "CONNECTING…", and watched the number rise.
            //
            // 🔑 Asked through the same rule the owner-guard diagnostic asks, not
            // a second copy of it. Two copies would let the tool recommend
            // nothing while the number went on demanding it, which a developer
            // resolves by adding the guard anyway.
            if (!ownedState.CanReadEveryArm)
            {
                return LoopStanding.SurfaceUnreadable;
            }

            if (declaration is null)
            {
                return LoopStanding.Unguarded;
            }

            // ⛔ The three answers are spent separately here and collapsed
            // nowhere. `Unreadable` is what this dimension used to buy the clear
            // with, so mapping it onto DrivesNoStateOfItsOwn would reinstate the
            // defect with the rule reporting it correctly.
            //
            // ⚠️ The catch-all is REQUIRED to compile — an enum-typed switch
            // expression must also answer for values outside the declared set
            // (CS8524, an error here) — so it cannot be the exhaustiveness check
            // it looks like: a fourth `Reading` member would compile clean and
            // arrive as an exception out of a scoring run. What holds it is a
            // count over the enum, asserted where both readers are named.
            return OwnedStateWrites.Read(
                    declaration, compilation.GetSemanticModel(declaration.SyntaxTree), type, ownedState)
                switch
                {
                    OwnedStateWrites.Reading.NothingOwned => LoopStanding.DrivesNoStateOfItsOwn,
                    OwnedStateWrites.Reading.Unreadable => LoopStanding.WritesUnreadable,
                    OwnedStateWrites.Reading.OwnedState => LoopStanding.Unguarded,
                    var unhandled => throw new InvalidOperationException(
                        "unhandled owned-state reading " + unhandled + " for " + loop.Name),
                };
        }

        // The frame loops that run for this type — its own, or the nearest each is
        // inherited from up to (not including) NetworkBehaviour, in the order Unity
        // drives them. A leaf that does not redeclare a loop is still driven by the
        // inherited one, so ownership is judged on the declaration that actually runs.
        //
        // ⛔ Only loops declared in THIS compilation's source. A base type from a
        // referenced assembly has no syntax here, so every arm below it would
        // answer "unguarded" — a fault the developer cannot act on, against a
        // library whose correctness is the library's. It is also a split this
        // scorer must not have: the analyzer is a syntax action and never reports
        // a compiled declaration, so faulting one would make the number demand an
        // edit no diagnostic names. The SDK's own NetworkRigidbody is exactly that
        // shape.
        //
        // ⚠️ "Has a syntax reference" is not the same question. A reference
        // compiled from source rather than metadata keeps its symbols, so an
        // inherited loop can carry a declaration that belongs to ANOTHER
        // compilation — and asking this one for a semantic model over that tree
        // throws rather than answering.
        private static IReadOnlyList<IMethodSymbol> EffectiveFrameLoops(
            INamedTypeSymbol type, INamedTypeSymbol networkBehaviour, Compilation compilation)
        {
            var loops = new List<IMethodSymbol>(UnityFrameLoops.Names.Length);

            foreach (string name in UnityFrameLoops.Names)
            {
                for (var current = type;
                     current is not null && !SymbolEqualityComparer.Default.Equals(current, networkBehaviour);
                     current = current.BaseType)
                {
                    var loop = current.GetMembers(name)
                        .OfType<IMethodSymbol>()
                        .FirstOrDefault(candidate => UnityFrameLoops.IsDrivenBy(candidate)
                            && DeclaredInThisCompilation(candidate, compilation));
                    if (loop is not null)
                    {
                        loops.Add(loop);
                        break;
                    }
                }
            }

            return loops;
        }

        private static bool DeclaredInThisCompilation(IMethodSymbol method, Compilation compilation)
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                if (compilation.ContainsSyntaxTree(reference.SyntaxTree))
                {
                    return true;
                }
            }

            return false;
        }

        private static DimensionVerdict CleanRuleVerdict(
            ReadinessDimension dimension, IReadOnlyCollection<string> faults, string[] faultIds, string subject)
        {
            var present = faultIds.Where(faults.Contains).ToList();
            return present.Count == 0
                ? new DimensionVerdict(dimension, true, $"{subject} clean")
                : new DimensionVerdict(dimension, false, $"{subject} report {string.Join(", ", present.OrderBy(id => id, StringComparer.Ordinal))}");
        }

        /// <summary>Roslyn's id for "an analyzer threw", reported with no source location.</summary>
        private const string AnalyzerCrashId = "AD0001";

        /// <summary>
        /// Analyzers run beside the shipped four, for the analyzer shard only.
        /// No input can make a shipped rule throw on demand, and the arm that
        /// records a crash must be driven rather than believed — so a test hands
        /// in one that throws.
        /// </summary>
        /// <remarks>
        /// 🚨 Flow-local, not process-wide, and the difference was measured. As a
        /// plain static field this seam was set by one test class and read by
        /// every other in the same process: xunit runs classes in parallel, so
        /// while the crash arm was being driven, an unrelated test scoring the
        /// real samples picked up the throwing analyzer and reported a crash
        /// to-do nobody had asked for. The injecting test's <c>finally</c> bounds
        /// the injection in TIME and the hazard is one of SCOPE. Reproduced by
        /// running the shard twice at once: two failures in six rounds, against
        /// none in six on a tree with fewer test classes — a race whose rate
        /// depends on scheduling, which is the kind that reaches CI and not a
        /// developer's machine.
        /// <para>
        /// <see cref="AsyncLocal{T}"/> confines it to the test's own flow, which
        /// is where the analyzers are composed; every other flow reads empty.
        /// </para>
        /// </remarks>
        internal static ImmutableArray<DiagnosticAnalyzer> AdditionalAnalyzersForTests
        {
            // ⚠️ An AsyncLocal that has never been written answers default(T),
            // and default(ImmutableArray<T>) is not the empty array — it wraps a
            // null and throws from AddRange. Every flow but the injecting one
            // reads through this.
            get => _additionalAnalyzersForTests.Value.IsDefault
                ? ImmutableArray<DiagnosticAnalyzer>.Empty
                : _additionalAnalyzersForTests.Value;
            set => _additionalAnalyzersForTests.Value = value;
        }

        private static readonly AsyncLocal<ImmutableArray<DiagnosticAnalyzer>>
            _additionalAnalyzersForTests = new AsyncLocal<ImmutableArray<DiagnosticAnalyzer>>();

        // Runs the shipped analyzers once and groups their diagnostics by the
        // fully-qualified name of the type whose declaration encloses each one.
        private static Dictionary<string, HashSet<string>> BucketAnalyzerDiagnosticsByType(
            Compilation compilation, out IReadOnlyList<string> analyzerFaults)
        {
            var faults = new List<string>();
            analyzerFaults = faults;

            var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
                new Rtmpe1000NetworkBehaviourAnalyzer(),
                new RpcRulesAnalyzer(),
                new NetworkVariableRulesAnalyzer(),
                new LifecycleRulesAnalyzer())
                .AddRange(AdditionalAnalyzersForTests);

            // Batch tool: the analysis runs once per report (CI or Editor), never
            // on a hot path, so blocking on the only async entry point is safe and
            // there is no synchronization context to deadlock against.
            var diagnostics = compilation.WithAnalyzers(analyzers)
                .GetAnalyzerDiagnosticsAsync()
                .GetAwaiter()
                .GetResult();

            var byType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var diagnostic in diagnostics)
            {
                // ⛔ An analyzer that THREW reports AD0001 with no location, and
                // the location test below would have dropped it — so a crashed rule
                // scored as a rule that found nothing, which is the flattering
                // direction on precisely the input that broke it. Kept, and named
                // in the report's to-do, because a dimension whose analyzer died is
                // not clean; it is unread.
                if (diagnostic.Id == AnalyzerCrashId)
                {
                    faults.Add(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
                    continue;
                }

                var location = diagnostic.Location;
                if (!location.IsInSource)
                {
                    continue;
                }

                var node = location.SourceTree.GetRoot().FindNode(location.SourceSpan, getInnermostNodeForTie: true);
                if (node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() is not BaseTypeDeclarationSyntax typeDeclaration
                    || compilation.GetSemanticModel(location.SourceTree).GetDeclaredSymbol(typeDeclaration) is not INamedTypeSymbol typeSymbol)
                {
                    continue;
                }

                string key = FullName(typeSymbol);
                if (!byType.TryGetValue(key, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    byType[key] = ids;
                }

                ids.Add(diagnostic.Id);
            }

            return byType;
        }

        // A field or property that could hold replicable state — excluding static
        // members and a property's compiler-generated backing field (counted via
        // the property itself).
        /// <summary>
        /// Whether <paramref name="member"/> is assigned a construction anywhere
        /// in the source this compilation carries.
        /// </summary>
        /// <remarks>
        /// Resolved through the semantic model rather than by matching the
        /// member's NAME: a local, a parameter or another type's member of the
        /// same spelling would otherwise count as constructing it. The search is
        /// over the whole compilation because a base class, a partial part or an
        /// initialiser helper may be the one that constructs it — and the
        /// question this dimension asks is whether anything does, not where.
        /// </remarks>
        private static bool IsConstructedSomewhere(ISymbol member, Compilation compilation)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                SemanticModel model = null;
                var root = tree.GetRoot();

                // 🔑 Stated over what the member RECEIVES, not over the spelling
                // `new T(…)`. The first version matched ObjectCreationExpression
                // beneath an assignment or a field declarator, and faulted three
                // ordinary constructions as "never constructed": the target-typed
                // `new(this, nameof(_hp))` C# 9 admits and the declared Unity floor
                // compiles, a property initialiser, and a factory — each −20 points
                // and a to-do telling the author to construct what they had. What
                // the dimension asks is whether the member is null for the object's
                // whole life; a value that is not the null literal answers that
                // whatever produced it.
                foreach (var node in root.DescendantNodes())
                {
                    ExpressionSyntax value;
                    System.Func<SemanticModel, ISymbol> target;
                    switch (node)
                    {
                        case AssignmentExpressionSyntax assignment
                            when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                || assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                            value = assignment.Right;
                            target = m => m.GetSymbolInfo(assignment.Left).Symbol;
                            break;
                        case VariableDeclaratorSyntax { Initializer: { } initializer } declarator:
                            value = initializer.Value;
                            target = m => m.GetDeclaredSymbol(declarator);
                            break;
                        case PropertyDeclarationSyntax { Initializer: { } initializer } property:
                            value = initializer.Value;
                            target = m => m.GetDeclaredSymbol(property);
                            break;
                        default:
                            continue;
                    }

                    if (IsNullValued(value))
                    {
                        continue;
                    }

                    var resolved = target(model ??= compilation.GetSemanticModel(tree));
                    if (resolved is not null && SymbolEqualityComparer.Default.Equals(resolved, member))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // `null`, `default` and `default(T)` — the values that leave a reference
        // member exactly as unconstructed as no assignment at all.
        private static bool IsNullValued(ExpressionSyntax value)
        {
            while (value is ParenthesizedExpressionSyntax parenthesised)
            {
                value = parenthesised.Expression;
            }

            return value.IsKind(SyntaxKind.NullLiteralExpression)
                || value.IsKind(SyntaxKind.DefaultLiteralExpression)
                || value is DefaultExpressionSyntax;
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

        /// <summary>
        /// Source classes whose base type could not be resolved, in name order.
        /// </summary>
        /// <remarks>
        /// An unresolved base is the one case where "not a NetworkBehaviour" and
        /// "could not be told" are the same answer from <c>InheritsFrom</c>, and
        /// the second is not a verdict. A plain <c>MonoBehaviour</c> resolves and
        /// is correctly absent; only an error type reaches this list.
        /// </remarks>
        private static IEnumerable<string> UnresolvableTypes(Compilation compilation)
            => SourceTypes(compilation)
                .Where(t => t.TypeKind == TypeKind.Class && !t.IsAbstract && !t.IsStatic)
                .Where(t => HasUnresolvedBase(t))
                .Select(t => t.ToDisplayString())
                .OrderBy(name => name, StringComparer.Ordinal);

        private static bool HasUnresolvedBase(INamedTypeSymbol type)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.TypeKind == TypeKind.Error)
                {
                    return true;
                }
            }

            return false;
        }

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

        private static bool DerivesFrom(ITypeSymbol type, INamedTypeSymbol baseType)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FullName(INamedTypeSymbol type) => type.ToDisplayString(FullNameFormat);
    }
}
