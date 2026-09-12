using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using RTMPE.SDK.Conversion.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Surfaces the single-player → multiplayer conversion opportunities the
    /// code-fixes act on. All are informational, never warnings, so legacy
    /// projects are offered a fix without being flooded:
    /// <list type="bullet">
    /// <item><c>RTMPE2001</c> — a <c>MonoBehaviour</c> (not yet a <c>NetworkBehaviour</c>)
    /// holding plain, non-Unity-serialized replicable state is a rebase candidate.</item>
    /// <item><c>RTMPE2002</c> — on a concrete <c>NetworkBehaviour</c>: a plain
    /// <c>private</c> field of mappable type is an in-place NetworkVariable
    /// candidate; a Unity-serialized field of mappable type that gameplay code
    /// <em>writes</em> earns a companion-NetworkVariable suggestion (the config
    /// field itself is never retyped — its effective value lives in scene/prefab
    /// YAML the compiler cannot see).</item>
    /// <item><c>RTMPE2005</c> — on a concrete <c>NetworkBehaviour</c>: a settable
    /// auto-property of mappable type holds its state in a field the compiler
    /// wrote, which no rewrite can retype and no author can name, so the
    /// opportunity is reported with the edit that serves it — a NetworkVariable
    /// behind the property, its interface unchanged.</item>
    /// <item><c>RTMPE2003</c> — a <c>NetworkBehaviour</c> whose <c>Update</c> drives
    /// owner-owned state without the <c>if (!IsOwner) return;</c> guard runs that
    /// state on every client, not just the owner. Reported on the evidence, never
    /// on the guard's absence alone: a loop that only draws must run everywhere.</item>
    /// <item><c>RTMPE2004</c> — a public owner-guarded state-mutating method on a
    /// <c>NetworkBehaviour</c>, with serializable parameters and no networked send
    /// yet, is an Enhanced-RPC conversion candidate. Detection only: the
    /// conversion itself runs solely on an explicitly designated method, and the
    /// audience is never chosen here.</item>
    /// </list>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ConversionOpportunityAnalyzer : DiagnosticAnalyzer
    {
        private const string NetworkBehaviourMetadataName = "RTMPE.Core.NetworkBehaviour";
        private const string MonoBehaviourMetadataName = "UnityEngine.MonoBehaviour";
        private const string SerializeFieldMetadataName = "UnityEngine.SerializeField";
        private const string NonSerializedMetadataName = "System.NonSerializedAttribute";
        private const string NetworkVariableAttributeMetadataName = "RTMPE.Sync.NetworkVariableAttribute";
        private const string Vector3MetadataName = "UnityEngine.Vector3";
        private const string QuaternionMetadataName = "UnityEngine.Quaternion";
        private const string ColorMetadataName = "UnityEngine.Color";
        private const string RtmpeRpcAttributeMetadataName = "RTMPE.Rpc.RtmpeRpcAttribute";
        private const string IsOwnerPropertyName = "IsOwner";

        // A "replicate this to every client" hint on a credential-looking field
        // would be a security anti-suggestion even at Info severity.
        private static readonly string[] SecretNameFragments = { "key", "token", "secret", "password" };

        // Engine and SDK entry points a frame loop or the runtime itself calls:
        // suggesting any of these as a remotely callable endpoint would invite a
        // per-frame RPC storm or re-entrancy into the lifecycle machinery. The
        // per-frame input/render/animation messages are included even though they
        // are rarer, because a public one that mutates guarded state would
        // otherwise be surfaced as a candidate — the exact storm this list exists
        // to prevent.
        private static readonly string[] LifecycleMethodNames =
        {
            "Update", "FixedUpdate", "LateUpdate", "Awake", "Start",
            "OnEnable", "OnDisable", "OnDestroy", "OnValidate", "Reset", "OnGUI",
            "OnNetworkSpawn", "OnNetworkDespawn", "OnFixedTick", "OnOwnershipChanged",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnMouseOver",
            "OnMouseDrag", "OnMouseUpAsButton",
            "OnAnimatorMove", "OnAnimatorIK",
            "OnRenderObject", "OnWillRenderObject", "OnPreCull", "OnPreRender", "OnPostRender",
            "OnRenderImage", "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnTriggerStay", "OnCollisionStay", "OnParticleCollision", "OnParticleTrigger",
            "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
            "OnBecameVisible", "OnBecameInvisible",
        };

        // A body that already sends — legacy or enhanced — is on the network
        // path; re-suggesting it would stack a second send on top of the first.
        // 🚨 `SendEnhancedRpcAsync` is a real send and was missing from this list,
        // so RTMPE2004 recommended converting a body that ALREADY sends — the
        // outcome the comment above exists to prevent. A prefix match rather than
        // three exact spellings: every send this SDK ships is one of these three
        // names or a suffixed form of one (`…Async`), and a name that merely
        // starts with `RPC` cannot reach the SDK's surface without also being
        // resolved against it by the caller.
        private static readonly string[] NetworkedSendNames = { "SendRpc", "SendEnhancedRpc", "RPC" };

        private static bool IsNetworkedSendName(string name)
        {
            foreach (string send in NetworkedSendNames)
            {
                if (name.StartsWith(send, System.StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static readonly DiagnosticDescriptor RebaseCandidate = new DiagnosticDescriptor(
            DiagnosticIds.ConversionRebaseCandidate,
            "MonoBehaviour with replicable state is a NetworkBehaviour candidate",
            "Type '{0}' is a MonoBehaviour holding replicable state — rebase it to NetworkBehaviour to replicate that state",
            "RTMPE.Conversion", DiagnosticSeverity.Info, isEnabledByDefault: true,
            description: "Offers the rebase fix; never fires on a Unity-serialized field (public or [SerializeField]), which the conversion leaves as Inspector data.",
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.ConversionRebaseCandidate));

        // RTMPE2002 deliberately spans TWO descriptors with DIFFERENT tags, and the
        // difference is load-bearing: the in-place arm reports from a symbol action
        // (live IDE analysis — no CompilationEnd tag), while the companion arm
        // reports from compilation end (its evidence is the whole-compilation write
        // scan) and MUST carry WellKnownDiagnosticTags.CompilationEnd. Moving either
        // report site, or merging the descriptors, silently changes when the IDE
        // shows or drops RTMPE2002 in partial analysis — keep tag and report site
        // paired.
        private static readonly DiagnosticDescriptor InPlaceCandidate = new DiagnosticDescriptor(
            DiagnosticIds.ConversionNetworkVariableCandidate,
            "Plain field is a NetworkVariable candidate",
            "Field '{0}' on '{1}' is plain gameplay state — convert it to a NetworkVariable to replicate it",
            "RTMPE.Conversion", DiagnosticSeverity.Info, isEnabledByDefault: true,
            description: "The in-place arm: fires only on private, non-Unity-serialized fields of a type the deterministic map covers.",
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.ConversionNetworkVariableCandidate));

        private static readonly DiagnosticDescriptor CompanionCandidate = new DiagnosticDescriptor(
            DiagnosticIds.ConversionNetworkVariableCandidate,
            "Written serialized field is a companion-NetworkVariable candidate",
            "Serialized field '{0}' on '{1}' is written at runtime — introduce a companion NetworkVariable seeded from it; the config field itself is never retyped",
            "RTMPE.Conversion", DiagnosticSeverity.Info, isEnabledByDefault: true,
            description: "The companion arm: a Unity-serialized field that gameplay code writes is live state wearing a config costume; the fix reproduces the HealthController seed pattern.",
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.ConversionNetworkVariableCandidate),
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        // RTMPE2005 is the complement of the field scan's implicitly-declared
        // exclusion, and reports the one shape the conversion set used to pass
        // over in silence. There is no fix behind it on purpose: the edit moves
        // state from a compiler-written field into one the author declares, and
        // the property's body is theirs to keep.
        //
        // 🔑 The sentence carries two clauses a reader would not expect, and both
        // were found by running the remedy rather than reading it. An initializer
        // is a COMPILE error on a property that has accessor bodies, so "keep the
        // property" is not achievable for `{ get; set; } = 100` without moving the
        // seed — and moving it silently is a changed starting value. And a
        // NetworkVariable's setter refuses a write from a non-owner and a write
        // after despawn, which an auto-property accepted; that is the difference
        // most likely to be discovered as a bug rather than read as a caveat.
        private static readonly DiagnosticDescriptor AutoPropertyState = new DiagnosticDescriptor(
            DiagnosticIds.ConversionAutoPropertyState,
            "Auto-property holds replicable state no conversion can retype",
            "Property '{0}' on '{1}' is auto-implemented, so its state lives in a compiler-generated field this conversion cannot retype — keep the property and put a private {2} behind it, constructed in OnNetworkSpawn, with the property reading and writing its Value. Carry any property initializer into that constructor, because a property with accessor bodies cannot hold one; and the property becomes owner-writable only, refusing writes from a client that does not own the object and after despawn, where the auto-property accepted every one.",
            "RTMPE.Conversion", DiagnosticSeverity.Info, isEnabledByDefault: true,
            description: "Fires on a settable, non-static auto-property of a mapped type on a concrete NetworkBehaviour. A [field: SerializeField] auto-property is Inspector data and stays outside: its edit is the companion arm's, and naming the wrong remedy is worse than naming none.",
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.ConversionAutoPropertyState));

        private static readonly DiagnosticDescriptor MissingOwnerGuard = new DiagnosticDescriptor(
            DiagnosticIds.ConversionMissingOwnerGuard,
            "Frame loop without an IsOwner guard",
            "'{0}.{1}' writes this object's own state and has no 'if (!IsOwner) return;' guard, so every client runs it",
            "RTMPE.Conversion", DiagnosticSeverity.Info, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.ConversionMissingOwnerGuard));

        private static readonly DiagnosticDescriptor RpcCandidate = new DiagnosticDescriptor(
            DiagnosticIds.ConversionRpcCandidate,
            "Owner-guarded mutation is an Enhanced-RPC candidate",
            "Method '{0}' on '{1}' is an owner-guarded state mutation — an Enhanced-RPC conversion candidate; designate it explicitly to the RPC generation tool, which reports what the audience you chose actually reaches",
            "RTMPE.Conversion", DiagnosticSeverity.Info, isEnabledByDefault: true,
            description: "Detection only, never a rewrite: the owner guard is necessary but not sufficient, so the conversion runs solely on an explicitly designated method, the audience is human-chosen, and a body that already sends is excluded.",
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.ConversionRpcCandidate));

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(
                RebaseCandidate, InPlaceCandidate, CompanionCandidate, AutoPropertyState,
                MissingOwnerGuard, RpcCandidate);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            // The two SDK/Unity base types are the load-bearing anchors; without
            // both, this is not an RTMPE-on-Unity compilation and there is nothing
            // to convert.
            var monoBehaviour = context.Compilation.GetTypeByMetadataName(MonoBehaviourMetadataName);
            var networkBehaviour = context.Compilation.GetTypeByMetadataName(NetworkBehaviourMetadataName);
            if (monoBehaviour is null || networkBehaviour is null)
            {
                return;
            }

            var sdk = new SdkSurface(
                monoBehaviour,
                networkBehaviour,
                context.Compilation.GetTypeByMetadataName(SerializeFieldMetadataName),
                context.Compilation.GetTypeByMetadataName(NonSerializedMetadataName),
                context.Compilation.GetTypeByMetadataName(NetworkVariableAttributeMetadataName),
                context.Compilation.GetTypeByMetadataName(Vector3MetadataName),
                context.Compilation.GetTypeByMetadataName(QuaternionMetadataName),
                context.Compilation.GetTypeByMetadataName(ColorMetadataName),
                context.Compilation.GetTypeByMetadataName(RtmpeRpcAttributeMetadataName),
                OwnedStateWrites.Surface.Resolve(context.Compilation),
                networkBehaviour.GetMembers(IsOwnerPropertyName).OfType<IPropertySymbol>().FirstOrDefault());

            // The companion arm needs every write site the compilation can see: a
            // private serialized field is written only inside its own type, and a
            // public one anywhere in this compilation, so one compilation-wide
            // collection covers every write that is visible here. A write to a
            // public field from another assembly is outside this compilation and so
            // outside this scan — the documented bound of a per-compilation
            // analyzer, not a scope this collection can reach.
            var writes = new FieldWriteCollector();
            context.RegisterOperationAction(
                c => writes.Record(c.Operation),
                OperationKind.SimpleAssignment,
                OperationKind.CompoundAssignment,
                OperationKind.CoalesceAssignment,
                OperationKind.DeconstructionAssignment,
                OperationKind.Increment,
                OperationKind.Decrement,
                OperationKind.Argument);

            var companions = new CompanionCandidateCollector();
            context.RegisterSymbolAction(c => AnalyzeType(c, sdk, companions), SymbolKind.NamedType);
            context.RegisterSyntaxNodeAction(c => AnalyzeFrameLoop(c, sdk), SyntaxKind.MethodDeclaration);
            context.RegisterSyntaxNodeAction(c => AnalyzeRpcCandidate(c, sdk), SyntaxKind.MethodDeclaration);
            context.RegisterCompilationEndAction(c => ReportCompanions(c, writes, companions));
        }

        // RTMPE2001 on MonoBehaviours and the two RTMPE2002 arms on concrete
        // NetworkBehaviours. Pure symbol inspection, so no semantic model is needed.
        private static void AnalyzeType(
            SymbolAnalysisContext context, SdkSurface sdk, CompanionCandidateCollector companions)
        {
            var type = (INamedTypeSymbol)context.Symbol;

            // Abstract bases are templates, not the concrete components a designer
            // attaches.
            if (type.TypeKind != TypeKind.Class || type.IsStatic || type.IsAbstract)
            {
                return;
            }

            if (InheritsFrom(type, sdk.NetworkBehaviour))
            {
                AnalyzeNetworkBehaviourFields(context, type, sdk, companions);
                ReportAutoPropertyState(context, type, sdk);
                return;
            }

            if (InheritsFrom(type, sdk.MonoBehaviour) && HoldsPlainReplicableState(type, sdk))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RebaseCandidate, BaseListLocation(type, context.CancellationToken), type.Name));
            }
        }

        // RTMPE2002 — per field on the destination shape. The in-place arm fires
        // immediately; companion candidates wait for the compilation-wide write
        // scan (a serialized field that is only read is pure Inspector config).
        private static void AnalyzeNetworkBehaviourFields(
            SymbolAnalysisContext context, INamedTypeSymbol type, SdkSurface sdk,
            CompanionCandidateCollector companions)
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared
                    || !IsReplicableValueType(field.Type, sdk)
                    || UnitySerializationClassifier.HasAttribute(field, sdk.NetworkVariableAttribute))
                {
                    continue;
                }

                if (HasSecretLikeName(field.Name))
                {
                    // A credential-shaped name is never a conversion candidate on
                    // either arm: replicating a secret to every peer is the outcome
                    // the suggestion must not invite, whether the field is serialized
                    // or a plain private one.
                    continue;
                }

                if (UnitySerializationClassifier.IsUnitySerialized(field, sdk.SerializeField, sdk.NonSerialized))
                {
                    companions.Add(field);
                }
                else if (field.DeclaredAccessibility == Accessibility.Private)
                {
                    // Private-only in v1: the transform is compilation-unit-scoped,
                    // and only a private field provably has every reference inside
                    // the unit it rewrites.
                    context.ReportDiagnostic(Diagnostic.Create(
                        InPlaceCandidate, GetReportLocation(field), field.Name, type.Name));
                }
            }
        }

        // RTMPE2005 — stated as the COMPLEMENT of the field scan's
        // implicitly-declared exclusion rather than as an independent notion of
        // "auto-property": the domain is exactly the fields that filter drops for
        // having been written by the compiler on a property's behalf, so what is
        // excluded there and what is offered here cannot drift apart.
        private static void ReportAutoPropertyState(
            SymbolAnalysisContext context, INamedTypeSymbol type, SdkSurface sdk)
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.IsImplicitlyDeclared || field.AssociatedSymbol is not IPropertySymbol property)
                {
                    continue;
                }

                // The backing field's own readonly-ness is what decides here, and it
                // decides BOTH shapes that cannot move: `{ get; }` and `{ get; init; }`
                // — the second of which still has a setter, so a test on the accessor
                // would admit it. ⛔ And a test on the accessor as well as this one
                // would be a condition that can never refuse anything, which reads to
                // the next author as though it were doing the work this line does.
                // Static is the shared-slot shape the in-place arm declines for its own
                // reason: one replicated slot rebound by whichever object spawned last.
                if (field.IsStatic || field.IsReadOnly)
                {
                    continue;
                }

                // Asked of the map, which also yields the wrapper the message
                // names — so the suggestion is a type that ships, not a shape the
                // author has to go and find.
                if (!NetworkVariableTypeMap.ByFieldTypeName.TryGetValue(
                        MapSpellingOf(field.Type), out string wrapper))
                {
                    continue;
                }

                // A credential-shaped name is refused on every arm; replicating a
                // secret to every peer is the outcome no suggestion may invite.
                if (HasSecretLikeName(property.Name))
                {
                    continue;
                }

                // `[field: SerializeField]` makes the value Inspector data, whose
                // edit is the companion arm's and not this one's; and a property
                // already carrying [NetworkVariable] is RTMPE1013's subject, where
                // a second diagnostic on one member would only compete with it.
                if (UnitySerializationClassifier.HasAttribute(field, sdk.SerializeField)
                    || CarriesNetworkVariableAttribute(property, sdk))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    AutoPropertyState, GetReportLocation(property), property.Name, type.Name, wrapper));
            }
        }

        // The attribute is written on the property, not on the field the compiler
        // derives from it, so the field-shaped helper beside this one cannot answer.
        private static bool CarriesNetworkVariableAttribute(IPropertySymbol property, SdkSurface sdk)
            => sdk.NetworkVariableAttribute is not null
                && property.GetAttributes().Any(
                    a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, sdk.NetworkVariableAttribute));

        private static void ReportCompanions(
            CompilationAnalysisContext context, FieldWriteCollector writes, CompanionCandidateCollector companions)
        {
            foreach (var field in companions.Snapshot())
            {
                if (writes.IsWritten(field))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        CompanionCandidate, GetReportLocation(field), field.Name, field.ContainingType.Name));
                }
            }
        }

        // RTMPE2003 — a NetworkBehaviour frame loop that writes this object's own
        // state without opening with the owner guard. A syntax-node action so the
        // semantic model the owner-guard check needs is provided by the framework,
        // never built here (RS1030).
        //
        // 🔑 Every loop Unity drives, not just Update: physics-driven movement is
        // written in FixedUpdate and follow cameras in LateUpdate, so a rule that
        // knew one name was silent on the two the state most likely to need
        // fencing actually moves in.
        private static void AnalyzeFrameLoop(SyntaxNodeAnalysisContext context, SdkSurface sdk)
        {
            var declaration = (MethodDeclarationSyntax)context.Node;
            if (!UnityFrameLoops.IsFrameLoopName(declaration.Identifier.ValueText)
                || context.SemanticModel.GetDeclaredSymbol(declaration) is not IMethodSymbol loop
                // A static or parameterised member wearing the name is no Unity
                // message, and cannot reference the instance IsOwner either.
                || !UnityFrameLoops.IsDrivenBy(loop)
                || !InheritsFrom(loop.ContainingType, sdk.NetworkBehaviour))
            {
                return;
            }

            // An already-guarded loop is the converted shape: stay silent so the
            // fix is unavailable and a re-run is a no-op.
            if (sdk.IsOwner is not null
                && declaration.Body?.Statements.FirstOrDefault() is IfStatementSyntax guard
                && OwnerGuardSyntax.IsOwnerGuard(guard, context.SemanticModel, sdk.IsOwner))
            {
                return;
            }

            // ⛔ Reported on POSITIVE evidence that the loop drives owner-owned
            // state, never on the absence of a guard. A loop that refreshes a
            // label, reads a score into the HUD or animates a local effect must
            // run on every client, and the guard is exactly wrong there — it was
            // recommended anyway, applied, and took the game's status display
            // down while the readiness score rose.
            // ⛔ Positive evidence means exactly that: a body whose writes could
            // not be read is silence here, never a recommendation. The score
            // spends the same answer the other way — see OwnedStateWrites.Reading.
            if (OwnedStateWrites.Read(
                    declaration, context.SemanticModel, loop.ContainingType, sdk.OwnedState)
                != OwnedStateWrites.Reading.OwnedState)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                MissingOwnerGuard, declaration.Identifier.GetLocation(),
                loop.ContainingType.Name, loop.Name));
        }

        // RTMPE2004 — a public owner-guarded state-mutating method on a concrete
        // NetworkBehaviour whose parameters the RPC serializer can carry and
        // whose body does not already send. Every leg of the conjunction is
        // conservative: dropping any one of them turns a suggestion into either
        // a per-frame RPC storm (lifecycle names), a dead-end (unserializable
        // parameters), a double send (networked bodies), or the unguarded
        // broadcast-mutator shape the runtime cannot protect against.
        private static void AnalyzeRpcCandidate(SyntaxNodeAnalysisContext context, SdkSurface sdk)
        {
            var declaration = (MethodDeclarationSyntax)context.Node;
            if (declaration.Body is null
                || LifecycleMethodNames.Contains(declaration.Identifier.ValueText)
                || HasSecretLikeName(declaration.Identifier.ValueText)
                || sdk.IsOwner is null)
            {
                return;
            }

            if (context.SemanticModel.GetDeclaredSymbol(declaration) is not IMethodSymbol method
                || method.MethodKind != MethodKind.Ordinary
                || method.DeclaredAccessibility != Accessibility.Public
                || method.IsStatic
                || method.IsAbstract
                || method.IsOverride // the Inherited=false contract makes overrides a hand-reviewed shape
                || method.IsGenericMethod
                || !method.ReturnsVoid
                || method.PartialImplementationPart is not null
                || method.PartialDefinitionPart is not null)
            {
                return;
            }

            var type = method.ContainingType;
            if (type is null || type.TypeKind != TypeKind.Class || type.IsAbstract
                || !InheritsFrom(type, sdk.NetworkBehaviour))
            {
                return;
            }

            if (sdk.RtmpeRpcAttribute is not null
                && method.GetAttributes().Any(a =>
                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, sdk.RtmpeRpcAttribute)))
            {
                return;
            }

            foreach (var parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None || parameter.IsParams || parameter.IsOptional
                    || !IsRpcSerializableParameter(parameter.Type, sdk))
                {
                    return;
                }
            }

            if (declaration.Body.Statements.FirstOrDefault() is not IfStatementSyntax guard
                || !OwnerGuardSyntax.IsOwnerGuard(guard, context.SemanticModel, sdk.IsOwner)
                || ContainsNetworkedSend(declaration)
                || !MutatesInstanceState(declaration, context.SemanticModel, type))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                RpcCandidate, declaration.Identifier.GetLocation(), method.Name, type.Name));
        }

        // The serializer's closed set, minus INetworkSerializable implementers:
        // an implementer without its hand-written RpcTypeRegistry registration
        // arrives null at the receiver, and the headless engine refuses such a
        // parameter — so suggesting it would be a dead end.
        private static bool IsRpcSerializableParameter(ITypeSymbol type, SdkSurface sdk)
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

            if (type is IArrayTypeSymbol array)
            {
                return array.Rank == 1 && array.ElementType.SpecialType == SpecialType.System_Byte;
            }

            return (sdk.Vector3 is not null && SymbolEqualityComparer.Default.Equals(type, sdk.Vector3))
                || (sdk.Color is not null && SymbolEqualityComparer.Default.Equals(type, sdk.Color))
                || (sdk.Quaternion is not null && SymbolEqualityComparer.Default.Equals(type, sdk.Quaternion));
        }

        private static bool ContainsNetworkedSend(MethodDeclarationSyntax declaration)
            => declaration.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(
                invocation => invocation.Expression switch
                {
                    IdentifierNameSyntax name => IsNetworkedSendName(name.Identifier.ValueText),
                    MemberAccessExpressionSyntax access => IsNetworkedSendName(access.Name.Identifier.ValueText),
                    // `nm?.SendRpc(...)` — a conditional access invokes through a
                    // member binding, not a member access; matching it too keeps a
                    // body that already sends from being re-suggested (double send).
                    MemberBindingExpressionSyntax binding => IsNetworkedSendName(binding.Name.Identifier.ValueText),
                    _ => false,
                });

        // A write whose target resolves to an instance field or property of the
        // containing type (or a member reached through such a field, e.g. a
        // NetworkVariable's .Value) — the state an RPC endpoint exists to change.
        // A method invoked on such a member — _items.Add(x), _queue.Clear() —
        // counts too: a syntax pass cannot know whether the callee writes, and the
        // receiver is the state it would write; the leftmost-receiver resolution
        // below still confirms the receiver is one of this type's instance members,
        // so a bare or non-member call (a local, a static, a helper) never qualifies.
        private static bool MutatesInstanceState(
            MethodDeclarationSyntax declaration, SemanticModel model, INamedTypeSymbol type)
        {
            foreach (var node in declaration.Body.DescendantNodes())
            {
                ExpressionSyntax written = node switch
                {
                    AssignmentExpressionSyntax assignment => assignment.Left,
                    PrefixUnaryExpressionSyntax prefix
                        when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                            || prefix.IsKind(SyntaxKind.PreDecrementExpression)
                        => prefix.Operand,
                    PostfixUnaryExpressionSyntax postfix
                        when postfix.IsKind(SyntaxKind.PostIncrementExpression)
                            || postfix.IsKind(SyntaxKind.PostDecrementExpression)
                        => postfix.Operand,
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax receiver }
                        => receiver.Expression,
                    _ => null,
                };

                if (written is null)
                {
                    continue;
                }

                var root = LeftmostReceiverOf(written);
                if (root is null)
                {
                    continue;
                }

                var member = model.GetSymbolInfo(root).Symbol;
                if (member is (IFieldSymbol or IPropertySymbol)
                    && !member.IsStatic
                    && IsInstanceMemberOf(member, type))
                {
                    return true;
                }
            }

            return false;
        }

        internal static ExpressionSyntax LeftmostReceiverOf(ExpressionSyntax written)
        {
            var expression = written;
            while (true)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;
                    case ElementAccessExpressionSyntax element:
                        expression = element.Expression;
                        continue;
                    // ⛔ `this.` and `base.` both name storage this object holds,
                    // so the access itself is the receiver and the walk stops here.
                    // Descending past them lands on `this`/`base`, which is no
                    // member — and a null root reads as "no write at all", so
                    // `base._hp -= 1` in a frame loop was a write neither reader
                    // could see. The receiver is judged by the shared rule, under
                    // whatever wrappers it admits: a rule testing the raw node let
                    // `(this)._hp -= 1` walk past, and its parenthesis-only repair
                    // let `((Player)this)._score.Value = 1` past one spelling later.
                    case MemberAccessExpressionSyntax access
                        when ThisInstance.Denotes(access.Expression):
                        return access;
                    case MemberAccessExpressionSyntax access:
                        expression = access.Expression;
                        continue;
                    case IdentifierNameSyntax identifier:
                        return identifier;
                    default:
                        return null;
                }
            }
        }

        internal static bool IsInstanceMemberOf(ISymbol member, INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(member.ContainingType, current))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HoldsPlainReplicableState(INamedTypeSymbol type, SdkSurface sdk)
            => type.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field => !field.IsStatic
                    && !field.IsConst
                    && !field.IsReadOnly // fixed after construction — not the live state a replica tracks
                    && !field.IsImplicitlyDeclared // exclude property backing fields
                    // A credential-shaped field is never the state that justifies a
                    // rebase — the field arms already refuse to network one, and a
                    // type whose only replicable state is a secret must not be nudged
                    // toward replicating it either.
                    && !HasSecretLikeName(field.Name)
                    && !UnitySerializationClassifier.IsUnitySerialized(field, sdk.SerializeField, sdk.NonSerialized)
                    && IsReplicableValueType(field.Type, sdk));

        private static bool HasSecretLikeName(string name)
        {
            foreach (string fragment in SecretNameFragments)
            {
                if (name.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // The deterministic §6.3 conversion type map's value types — the gameplay
        // state a NetworkVariable can carry. Reference types and SDK/Unity service
        // handles are excluded: they are wiring, not replicable state.
        //
        // 🚨 ASKED OF THE MAP, never restated. This predicate used to name the
        // six types somebody remembered, and when `Vector2` and `Vector2Int`
        // joined the map they reached the transform, the runtime, the contract
        // and the documentation while the diagnostic that OFFERS the conversion
        // silently never fired for either — half a feature, every suite green.
        // A type added to NetworkVariableTypeMap is now offered by being added.
        private static bool IsReplicableValueType(ITypeSymbol type, SdkSurface sdk)
            => NetworkVariableTypeMap.ByFieldTypeName.ContainsKey(MapSpellingOf(type));

        /// <summary>
        /// A type as the closed map spells it, or the empty string when the map
        /// could not name it at all.
        /// </summary>
        /// <remarks>
        /// ⛔ The keyword for the four the language names, and the bare name for
        /// a UnityEngine struct — which is exactly how an author writes a field,
        /// and exactly what the map is keyed on. Anything outside UnityEngine
        /// answers nothing rather than its bare name, so a game's own
        /// <c>Vector2</c> is not mistaken for the engine's.
        /// </remarks>
        private static string MapSpellingOf(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Int32:   return "int";
                case SpecialType.System_Single:  return "float";
                case SpecialType.System_Boolean: return "bool";
                case SpecialType.System_String:  return "string";
            }

            return type.ContainingNamespace is { } space
                   && space.ToDisplayString() == "UnityEngine"
                ? type.Name
                : string.Empty;
        }

        // A partial type has one report location but several declarations, and only
        // the part that spells the base can be rebased. Anchoring anywhere else
        // leaves the diagnostic with no fix behind it, and points a reader at a part
        // that says nothing about the base it is being told to change. The base is
        // matched by the symbol's own name rather than a literal, so the two cannot
        // drift; a spelling the match cannot recognise falls back to the symbol's
        // own location, which is where this reported before.
        private static Location BaseListLocation(INamedTypeSymbol type, CancellationToken cancellationToken)
        {
            string baseName = type.BaseType?.Name;
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration
                    && declaration.BaseList != null
                    && declaration.BaseList.Types.Any(t => RightmostName(t.Type) == baseName))
                {
                    return declaration.Identifier.GetLocation();
                }
            }

            return GetReportLocation(type);
        }

        private static string RightmostName(TypeSyntax type)
        {
            switch (type)
            {
                case QualifiedNameSyntax qualified:
                    return RightmostName(qualified.Right);
                case AliasQualifiedNameSyntax aliased:
                    return RightmostName(aliased.Name);
                case SimpleNameSyntax simple:
                    return simple.Identifier.ValueText;
                default:
                    return null;
            }
        }

        private static Location GetReportLocation(ISymbol symbol)
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

        // Every operation shape that stores into a field: assignments (simple,
        // compound, coalesce), ++/--, and passing the field by ref/out. Collected
        // compilation-wide under a lock because the analyzer runs concurrently.
        private sealed class FieldWriteCollector
        {
            private readonly object _gate = new object();
            private readonly HashSet<IFieldSymbol> _written =
                new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);

            public void Record(IOperation operation)
            {
                // A deconstruction assigns each tuple element in parallel, so its
                // target is a tuple of lvalues rather than one field reference; a
                // serialized field written only this way — `(_hp, _) = (v, 0)` — is
                // live state the plain-assignment scan never saw.
                if (operation is IDeconstructionAssignmentOperation { Target: ITupleOperation tuple })
                {
                    var targets = new List<IFieldSymbol>();
                    CollectTupleFields(tuple, targets);
                    if (targets.Count > 0)
                    {
                        lock (_gate)
                        {
                            foreach (var target in targets)
                            {
                                _written.Add(target);
                            }
                        }
                    }

                    return;
                }

                IFieldSymbol field = operation switch
                {
                    IAssignmentOperation { Target: IFieldReferenceOperation reference } => reference.Field,
                    IIncrementOrDecrementOperation { Target: IFieldReferenceOperation reference } => reference.Field,
                    IArgumentOperation { Parameter: { RefKind: RefKind.Ref or RefKind.Out }, Value: IFieldReferenceOperation reference }
                        => reference.Field,
                    _ => null,
                };

                if (field is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _written.Add(field);
                }
            }

            // The field-reference targets of a deconstruction, recursing into a
            // nested tuple (`(_a, (_b, _c)) = …`). A deconstruction target is an
            // lvalue, so Roslyn applies any element-type conversion to the value
            // side, never to the target field reference — the target stays a plain
            // IFieldReferenceOperation even when the field type widens its slot. A
            // declared local or a discard is not a field and contributes nothing.
            private static void CollectTupleFields(ITupleOperation tuple, List<IFieldSymbol> into)
            {
                foreach (var element in tuple.Elements)
                {
                    switch (element)
                    {
                        case IFieldReferenceOperation reference:
                            into.Add(reference.Field);
                            break;
                        case ITupleOperation nested:
                            CollectTupleFields(nested, into);
                            break;
                    }
                }
            }

            public bool IsWritten(IFieldSymbol field)
            {
                lock (_gate)
                {
                    return _written.Contains(field);
                }
            }
        }

        private sealed class CompanionCandidateCollector
        {
            private readonly object _gate = new object();
            private readonly List<IFieldSymbol> _candidates = new List<IFieldSymbol>();

            public void Add(IFieldSymbol field)
            {
                lock (_gate)
                {
                    _candidates.Add(field);
                }
            }

            public IReadOnlyList<IFieldSymbol> Snapshot()
            {
                lock (_gate)
                {
                    return _candidates.ToArray();
                }
            }
        }

        // The resolved SDK/Unity symbols, threaded through the per-type and
        // per-method actions so each is looked up once per compilation.
        private sealed class SdkSurface
        {
            public SdkSurface(
                INamedTypeSymbol monoBehaviour, INamedTypeSymbol networkBehaviour, INamedTypeSymbol serializeField,
                INamedTypeSymbol nonSerialized, INamedTypeSymbol networkVariableAttribute,
                INamedTypeSymbol vector3, INamedTypeSymbol quaternion, INamedTypeSymbol color,
                INamedTypeSymbol rtmpeRpcAttribute, OwnedStateWrites.Surface ownedState,
                IPropertySymbol isOwner)
            {
                MonoBehaviour = monoBehaviour;
                NetworkBehaviour = networkBehaviour;
                SerializeField = serializeField;
                NonSerialized = nonSerialized;
                NetworkVariableAttribute = networkVariableAttribute;
                Vector3 = vector3;
                Quaternion = quaternion;
                Color = color;
                RtmpeRpcAttribute = rtmpeRpcAttribute;
                OwnedState = ownedState;
                IsOwner = isOwner;
            }

            public INamedTypeSymbol MonoBehaviour { get; }
            public INamedTypeSymbol NetworkBehaviour { get; }
            public INamedTypeSymbol SerializeField { get; }
            public INamedTypeSymbol NonSerialized { get; }
            public INamedTypeSymbol NetworkVariableAttribute { get; }
            public INamedTypeSymbol Vector3 { get; }
            public INamedTypeSymbol Quaternion { get; }
            public INamedTypeSymbol Color { get; }
            public INamedTypeSymbol RtmpeRpcAttribute { get; }
            public OwnedStateWrites.Surface OwnedState { get; }
            public IPropertySymbol IsOwner { get; }
        }
    }
}
