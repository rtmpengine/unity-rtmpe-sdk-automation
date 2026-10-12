using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Moves the <c>NetworkBehaviour</c> lifecycle rules to compile time: a
    /// declared <c>OnDestroy</c> must chain <c>base.OnDestroy()</c> (or the
    /// spawn registration leaks), and a method named like a lifecycle hook must
    /// override the hook (or it silently never runs).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class LifecycleRulesAnalyzer : DiagnosticAnalyzer
    {
        private const string NetworkBehaviourMetadataName = "RTMPE.Core.NetworkBehaviour";
        private const string OnDestroyMethodName = "OnDestroy";
        private const string OnNetworkSpawnMethodName = "OnNetworkSpawn";

        private static readonly DiagnosticDescriptor MissingBaseOnDestroy = new DiagnosticDescriptor(
            DiagnosticIds.LifecycleMissingBaseOnDestroy,
            "OnDestroy must call base.OnDestroy()",
            "OnDestroy on '{0}' does not call base.OnDestroy(), so the network object's spawn registration leaks",
            "RTMPE.Lifecycle", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.LifecycleMissingBaseOnDestroy));

        /// <remarks>
        /// ⛔ An Error, and it fires ONLY where the harm is established: when an
        /// ancestor between this type and <c>NetworkBehaviour</c> overrides
        /// <c>OnNetworkSpawn</c>, so a missing chain skips work that is really
        /// there. <c>NetworkBehaviour</c>'s own override is empty, so an override
        /// that chains nothing above it costs nothing and is not reported at all.
        ///
        /// <para>🔑 That is what settles the severity rather than trading it off.
        /// The objection to an Error was that it would break integrator builds
        /// which are correct today — and this one cannot, because in those builds
        /// it does not fire. Where it does fire the base's NetworkVariables stay
        /// null and the object throws on its first frame, which is not a warning.
        /// </para>
        ///
        /// <para>⚠️ Unlike <c>RTMPE1020</c>, whose harm is unconditional: a
        /// missing <c>base.OnDestroy()</c> leaks the spawn registration whatever
        /// the hierarchy looks like.</para>
        /// </remarks>
        private static readonly DiagnosticDescriptor MissingBaseOnNetworkSpawn = new DiagnosticDescriptor(
            DiagnosticIds.LifecycleMissingBaseOnNetworkSpawn,
            "OnNetworkSpawn must call base.OnNetworkSpawn()",
            "OnNetworkSpawn on '{0}' does not call base.OnNetworkSpawn(), so '{1}' never runs and the NetworkVariables it builds stay null",
            "RTMPE.Lifecycle", DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.LifecycleMissingBaseOnNetworkSpawn));

        private static readonly DiagnosticDescriptor HookSignatureMismatch = new DiagnosticDescriptor(
            DiagnosticIds.LifecycleHookSignatureMismatch,
            "Lifecycle-hook name that does not override the hook",
            "'{0}' has a NetworkBehaviour lifecycle-hook name but does not override the hook, so it is never called",
            "RTMPE.Lifecycle", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            helpLinkUri: DiagnosticHelp.LinkFor(DiagnosticIds.LifecycleHookSignatureMismatch));

        // The hooks whose signature an author can silently get wrong, keyed by
        // name to their (all void-returning) parameter special-types. The input
        // hooks (GatherInput/ApplyInput) are intentionally out of scope here.
        private static readonly ImmutableDictionary<string, SpecialType[]> Hooks =
            ImmutableDictionary.CreateRange(new[]
            {
                Pair("OnNetworkSpawn", Array.Empty<SpecialType>()),
                Pair("OnNetworkDespawn", Array.Empty<SpecialType>()),
                Pair("OnOwnershipChanged", new[] { SpecialType.System_String, SpecialType.System_String }),
                Pair("OnFixedTick", new[] { SpecialType.System_Single }),
            });

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(
                MissingBaseOnDestroy, MissingBaseOnNetworkSpawn, HookSignatureMismatch);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var networkBehaviour = context.Compilation.GetTypeByMetadataName(NetworkBehaviourMetadataName);
            if (networkBehaviour is null)
            {
                return; // The SDK's core surface is not referenced; there is nothing to analyze.
            }

            var compilation = context.Compilation;
            context.RegisterOperationBlockAction(c => AnalyzeOnDestroy(c, networkBehaviour, compilation));
            context.RegisterOperationBlockAction(c => AnalyzeOnNetworkSpawn(c, networkBehaviour));
            context.RegisterSymbolAction(c => AnalyzeHook(c, networkBehaviour), SymbolKind.Method);
        }

        private static void AnalyzeOnDestroy(
            OperationBlockAnalysisContext context, INamedTypeSymbol networkBehaviour, Compilation compilation)
        {
            if (context.OwningSymbol is not IMethodSymbol method
                || method.Name != OnDestroyMethodName
                || !IsDestroyHook(method, compilation)
                || !InheritsFrom(method.ContainingType, networkBehaviour))
            {
                return;
            }

            // A call nested inside a local function or a lambda runs only if that
            // body is invoked, which this rule cannot establish — so it does not
            // count as chaining. The remaining bound is honest and documented:
            // a call in a statically dead branch still silences the rule (AUD-L6).
            bool callsBase = context.OperationBlocks.Any(block => block.Syntax
                .DescendantNodesAndSelf(descendIntoChildren: n => !IsDeferredBody(n))
                .OfType<InvocationExpressionSyntax>()
                .Any(IsBaseOnDestroyInvocation));

            if (!callsBase)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingBaseOnDestroy,
                    method.Locations[0],
                    HiddenHookProperties(method, compilation),
                    method.ContainingType.Name));
            }
        }

        /// <summary>
        /// An <c>OnNetworkSpawn</c> override that skips an ancestor's.
        /// </summary>
        private static void AnalyzeOnNetworkSpawn(
            OperationBlockAnalysisContext context, INamedTypeSymbol networkBehaviour)
        {
            if (context.OwningSymbol is not IMethodSymbol method
                || method.Name != OnNetworkSpawnMethodName
                || !method.IsOverride
                || !method.ReturnsVoid
                || method.Parameters.Length != 0
                || !InheritsFrom(method.ContainingType, networkBehaviour))
            {
                return;
            }

            // The override this one hides. NetworkBehaviour's own is empty, so
            // skipping it costs nothing and is not this rule's subject; anything
            // between the two is work that will not run.
            var skipped = method.OverriddenMethod;
            if (skipped is null
                || SymbolEqualityComparer.Default.Equals(skipped.ContainingType, networkBehaviour))
            {
                return;
            }

            // Same reasoning as OnDestroy's: a call inside a lambda or a local
            // function runs only if that body is invoked, which this rule cannot
            // establish, so it does not count as chaining.
            bool callsBase = context.OperationBlocks.Any(block => block.Syntax
                .DescendantNodesAndSelf(descendIntoChildren: n => !IsDeferredBody(n))
                .OfType<InvocationExpressionSyntax>()
                .Any(IsBaseOnNetworkSpawnInvocation));

            if (!callsBase)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingBaseOnNetworkSpawn,
                    method.Locations[0],
                    method.ContainingType.Name,
                    skipped.ContainingType.Name + ".OnNetworkSpawn"));
            }
        }

        private static bool IsBaseOnNetworkSpawnInvocation(InvocationExpressionSyntax invocation)
            => invocation.ArgumentList.Arguments.Count == 0
               && invocation.Expression is MemberAccessExpressionSyntax access
               && access.Expression is BaseExpressionSyntax
               && access.Name.Identifier.ValueText == OnNetworkSpawnMethodName;

        /// <summary>
        /// The declared accessibility an <c>override</c> would have to carry, for a
        /// declaration that merely HIDES an overridable base hook — and nothing at
        /// all for one that does not.
        /// <para>
        /// 🔑 Chaining alone leaves <c>private void OnDestroy()</c> hiding
        /// <c>protected virtual void OnDestroy()</c>, which is CS0114 on every
        /// compile: correct at runtime (Unity dispatches the message by name to the
        /// most derived declaration, which now chains) and wrong as C#. The paired
        /// fix can repair the declaration too, but only the semantic model can say
        /// whether that is LEGAL — a base hook that is not virtual makes
        /// <c>override</c> CS0506, and one whose accessibility differs makes it
        /// CS0507. So the answer is decided here, where the model is, and travels
        /// to the fix as a diagnostic property rather than being guessed there.
        /// </para>
        /// </summary>
        internal static ImmutableDictionary<string, string> HiddenHookProperties(
            IMethodSymbol method, Compilation compilation)
        {
            if (method.IsOverride || !method.ReturnsVoid)
            {
                return ImmutableDictionary<string, string>.Empty;
            }

            var hook = NearestDestroyHook(method.ContainingType, compilation);
            if (hook is null || hook.IsSealed || !(hook.IsVirtual || hook.IsOverride))
            {
                return ImmutableDictionary<string, string>.Empty;
            }

            // ⚠️ Obsolescence must MATCH across an override or the compiler
            // complains either way: an obsolete member overriding a live one is
            // CS0809, and a live one overriding an obsolete member is CS0672.
            // Repairing across that difference trades one permanent warning for
            // another, which is the very thing this repair exists to stop doing —
            // and the first version of this guard tested only one direction.
            if (IsObsolete(method) != IsObsolete(hook))
            {
                return ImmutableDictionary<string, string>.Empty;
            }

            string accessibility = AccessibilityKeywords(hook.DeclaredAccessibility);

            // 🔑 `protected internal` reached from ANOTHER assembly must be
            // overridden as plain `protected`: the internal half is not visible
            // there and repeating the pair is CS0507. `IsSymbolAccessibleWithin`
            // does NOT screen this — a derived type in another assembly reaches
            // such a member through the protected half, so it answers true — and
            // the two RTMPE assemblies are exactly this shape: the SDK runtime is
            // its own assembly definition and user code is Assembly-CSharp.
            //
            // ⚠️ The test is FRIEND ACCESS, not "a different assembly". C# asks
            // whether the internal half is visible, and `InternalsVisibleTo` makes
            // it visible across an assembly boundary — at which point the override
            // must repeat `protected internal` and emitting `protected` is CS0507
            // again. The SDK's own runtime declares InternalsVisibleTo, so an
            // assembly-inequality test would have been wrong in this very tree.
            if (hook.DeclaredAccessibility == Accessibility.ProtectedOrInternal
                && !hook.ContainingAssembly.GivesAccessTo(method.ContainingAssembly))
            {
                accessibility = "protected";
            }

            // ⛔ Repair only where the declaration is not NARROWED. An override may
            // not change the overridden member's accessibility (CS0507), so a
            // `public void OnDestroy()` hider would have to become `protected` —
            // and every call site outside the type then fails with CS0122. A fix
            // may leave a warning behind; it may not break another file. `private`
            // is exempt because nothing outside the type can be calling it.
            if (accessibility is null
                || (method.DeclaredAccessibility != Accessibility.Private
                    && AccessibilityKeywords(method.DeclaredAccessibility) != accessibility))
            {
                return ImmutableDictionary<string, string>.Empty;
            }

            return ImmutableDictionary<string, string>.Empty.Add(
                OverrideAccessibilityProperty, accessibility);
        }

        private static bool IsObsolete(IMethodSymbol method)
            => method.GetAttributes().Any(
                a => a.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute");

        /// <summary>
        /// The property key the paired code fix reads. An override must repeat the
        /// overridden member's declared accessibility exactly, so the keyword is
        /// carried rather than assumed to be <c>protected</c>.
        /// </summary>
        public const string OverrideAccessibilityProperty = "overrideAccessibility";

        // C#'s spelling of each accessibility an override may legally repeat.
        // Private is absent because a private member is not overridable at all,
        // and its absence is what makes the caller's null check meaningful.
        private static string AccessibilityKeywords(Accessibility accessibility)
        {
            switch (accessibility)
            {
                case Accessibility.Public: return "public";
                case Accessibility.Protected: return "protected";
                case Accessibility.Internal: return "internal";
                case Accessibility.ProtectedOrInternal: return "protected internal";
                case Accessibility.ProtectedAndInternal: return "private protected";
                default: return null;
            }
        }

        // Unity dispatches the destroy message to a parameterless instance method
        // of that name and discards whatever it returns, so a declaration that
        // merely hides the inherited hook — the idiomatic `private void
        // OnDestroy()` a rebased MonoBehaviour carries in — runs in the override's
        // place and leaks the same registration. The return type is not part of a
        // C# signature and is not part of this test either: a value-returning
        // hider leaks exactly as a void one does. An overload carrying parameters
        // is not the message and is left alone, and neither is a GENERIC one:
        // arity is part of a C# signature, so `OnDestroy<T>()` hides nothing, the
        // base declaration still runs, and Unity cannot dispatch to a method
        // definition that needs type arguments anyway. Reporting it was a false
        // positive on its own — and once the paired fix began repairing
        // declarations it became a fix that emits CS0115.
        //
        // The base declaration is required, not assumed: it is what makes
        // `base.OnDestroy()` — the reported remedy and the paired code fix —
        // compile, so a shape that cannot chain is never told to.
        private static bool IsDestroyHook(IMethodSymbol method, Compilation compilation)
            => !method.IsStatic
                && method.Parameters.Length == 0
                && !method.IsGenericMethod
                && InheritsChainableDestroyHook(method.ContainingType, compilation);

        // The nearest declaration wins, and the walk stops there. C# hiding is by
        // name and parameters alone, so `base.OnDestroy()` binds to the first
        // parameterless OnDestroy up the chain whatever its shape — looking past a
        // static or inaccessible one to a usable ancestor would report a chain the
        // compiler then rejects.
        private static bool InheritsChainableDestroyHook(INamedTypeSymbol type, Compilation compilation)
            => NearestDestroyHook(type, compilation) is not null;

        // As above, answering WHICH declaration the chain binds to rather than
        // merely that one exists — the override repair needs its virtuality and
        // its declared accessibility, and re-walking the chain in a second place
        // is how the two answers drift apart.
        private static IMethodSymbol NearestDestroyHook(INamedTypeSymbol type, Compilation compilation)
        {
            for (var current = type?.BaseType; current is not null; current = current.BaseType)
            {
                var nearest = current.GetMembers(OnDestroyMethodName)
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.Parameters.Length == 0);

                if (nearest is null)
                {
                    continue;
                }

                // Accessibility is asked of the compilation rather than compared
                // against an enum: `internal` across an assembly boundary is
                // visible to the symbol model and unusable from the call site.
                // An abstract declaration is visible and callable-looking, and
                // `base.OnDestroy()` against it is CS0205 — the chain was broken by
                // whichever ancestor declared it that way, and no type below has a
                // legal way to restore it.
                return !nearest.IsStatic
                    && !nearest.IsAbstract
                    && nearest.ReturnsVoid
                    && compilation.IsSymbolAccessibleWithin(nearest, type)
                    ? nearest
                    : null;
            }

            return null;
        }

        // Bodies that do not run where they are written: a local function or a
        // lambda executes only when something invokes it.
        private static bool IsDeferredBody(SyntaxNode node)
            => node is LocalFunctionStatementSyntax
                || node is AnonymousFunctionExpressionSyntax;

        private static void AnalyzeHook(SymbolAnalysisContext context, INamedTypeSymbol networkBehaviour)
        {
            var method = (IMethodSymbol)context.Symbol;

            // A partial method surfaces as two symbols; analyze it once.
            if (method.PartialDefinitionPart is not null)
            {
                return;
            }

            if (!Hooks.TryGetValue(method.Name, out var parameterTypes)
                || !InheritsFrom(method.ContainingType, networkBehaviour))
            {
                return;
            }

            if (method.IsOverride)
            {
                return;
            }

            // The right signature without `override` hides the virtual hook: the
            // base runs in its place and this never does. An implicit hide the
            // compiler already flags (CS0114); an explicit `new` silences that
            // warning, leaving the "silently never runs" hazard with no signal at
            // all — so the rule speaks exactly there, and nowhere the compiler
            // already does.
            if (HasHookSignature(method, parameterTypes))
            {
                if (HidesWithNewModifier(method))
                {
                    context.ReportDiagnostic(Diagnostic.Create(HookSignatureMismatch, method.Locations[0], method.Name));
                }

                return;
            }

            // When the hook is correctly overridden anywhere on the type or its
            // base chain, this wrong-signature method is a deliberate overload —
            // the hook still runs — so it is not a failed hook.
            if (!HookOverriddenInChain(method.ContainingType, method.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(HookSignatureMismatch, method.Locations[0], method.Name));
            }
        }

        // A hook that carries the `new` modifier hides the virtual base hook
        // explicitly, which is the one hide the compiler does not warn on. The
        // modifier lives in syntax, not on the symbol, so it is read from the
        // declaration — across every part of a partial type, any of which may
        // carry it.
        private static bool HidesWithNewModifier(IMethodSymbol method)
            => method.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<MethodDeclarationSyntax>()
                .Any(declaration => declaration.Modifiers.Any(SyntaxKind.NewKeyword));

        private static bool HasHookSignature(IMethodSymbol method, SpecialType[] parameterTypes)
            => method.ReturnsVoid
                && method.Parameters.Length == parameterTypes.Length
                && method.Parameters.All(p => p.RefKind == RefKind.None) // ref/in/out changes the signature
                && method.Parameters.Select(p => p.Type.SpecialType).SequenceEqual(parameterTypes);

        private static bool HookOverriddenInChain(INamedTypeSymbol type, string hookName)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.GetMembers(hookName).OfType<IMethodSymbol>().Any(m => m.IsOverride))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBaseOnDestroyInvocation(InvocationExpressionSyntax invocation)
            => invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Expression is BaseExpressionSyntax
                && memberAccess.Name.Identifier.ValueText == OnDestroyMethodName;

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

        private static KeyValuePair<string, SpecialType[]> Pair(string name, SpecialType[] parameterTypes)
            => new KeyValuePair<string, SpecialType[]>(name, parameterTypes);
    }
}
