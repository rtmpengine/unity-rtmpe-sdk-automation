using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>What a call to Unity's <c>Object.Instantiate</c> creates, as far as the source can say.</summary>
    internal enum InstantiationKind
    {
        /// <summary>Not a call to Unity's <c>Instantiate</c>, or a copy of an asset rather than of a scene object.</summary>
        None,

        /// <summary>
        /// The created object provably carries a <c>NetworkBehaviour</c>: the
        /// original is one, or the object of one, or the result is asked for one.
        /// Such an object is never networked — no other client sees it and none
        /// of its variables replicate — because only <c>SpawnManager.Spawn</c>
        /// puts an object on the network.
        /// </summary>
        NetworkedObject,

        /// <summary>
        /// Some other scene object — a <c>GameObject</c>, or a component whose
        /// object is copied with it, whose contents the source does not show.  A
        /// UI panel, an effect and a Snake segment all look like this, which is
        /// why this kind is advice and never a finding.
        /// </summary>
        OtherObject,
    }

    /// <summary>
    /// Reads a call to Unity's <c>Instantiate</c> (plan §10/4).  One reading,
    /// shared by the warning at the call (RTMPE1030) and the authority signal the
    /// readiness report classifies on, so the two cannot disagree about which
    /// call creates a networked object.
    /// </summary>
    /// <remarks>
    /// ⛔ Certain where it says <see cref="InstantiationKind.NetworkedObject"/>,
    /// and silent rather than guessing everywhere else: the type of the original,
    /// or a component the result is asked for, is the only evidence the source
    /// carries.  A prefab held as a <c>GameObject</c> may be anything, and what it
    /// carries is in the prefab's YAML, which a compiler never sees.
    /// </remarks>
    internal static class LocalInstantiation
    {
        private const string MethodName = "Instantiate";
        private const string UnityNamespace = "UnityEngine";
        private const string PoolInterfaceName = "INetworkObjectPool";

        // The component lookups whose type argument says what the created object
        // carries.  ⛔ Not GetComponentInParent: it finds a component on the
        // copy's PARENT as readily as on the copy, and the parent is somebody
        // else's object.
        private static readonly string[] ComponentLookupNames =
        {
            "GetComponent", "GetComponentInChildren", "TryGetComponent",
        };

        // The qualifiers `Instantiate` is reached through: it is a static of
        // UnityEngine.Object, inherited by every engine object type.
        private static readonly string[] UnityObjectQualifiers =
        {
            "Object", "GameObject", "Component", "MonoBehaviour", "Behaviour", "ScriptableObject",
        };

        // What a component's object is reached through: Instantiate of either
        // copies the whole object, every component on it included.
        private static readonly string[] ObjectOfAComponent = { "gameObject", "transform" };

        /// <summary>
        /// What <paramref name="invocation"/> creates, and — for a networked
        /// object — the <c>NetworkBehaviour</c> type that proves it.
        /// </summary>
        internal static InstantiationKind Classify(
            InvocationExpressionSyntax invocation,
            SemanticModel model,
            INamedTypeSymbol networkBehaviour,
            out ITypeSymbol networkedType)
        {
            networkedType = null;
            if (AuthoritySignalExtractor.CalleeName(invocation.Expression) != MethodName
                || !BindsToUnityInstantiate(invocation, model))
            {
                return InstantiationKind.None;
            }

            var original = OriginalArgument(invocation);
            var created = CreatedType(invocation, original, model);

            if (networkBehaviour is not null)
            {
                networkedType = NetworkBehaviourIn(created, networkBehaviour);
                if (networkedType is not null)
                {
                    return InstantiationKind.NetworkedObject;
                }

                // `Instantiate(enemy.gameObject)`: the copy is the enemy's object —
                // and `Instantiate(gameObject)` inside a NetworkBehaviour is a
                // copy of its own.
                networkedType = NetworkBehaviourIn(ObjectOwnerOf(original?.Expression, invocation, model), networkBehaviour);
                if (networkedType is not null)
                {
                    return InstantiationKind.NetworkedObject;
                }

                networkedType = ComponentAskedOfTheResult(invocation, model, networkBehaviour);
                if (networkedType is not null)
                {
                    return InstantiationKind.NetworkedObject;
                }
            }

            // A material, a mesh or a ScriptableObject is an asset copied into
            // memory, not an object in the scene anybody could see; nothing about
            // it is networked or owed a spawn.
            return IsSceneObject(created) ? InstantiationKind.OtherObject : InstantiationKind.None;
        }

        // The type whose object `expression` reaches through `gameObject` or
        // `transform`: the receiver's, or — unqualified — the type the call
        // is written in, which is what an implicit `this` names.
        private static ITypeSymbol ObjectOwnerOf(
            ExpressionSyntax expression, InvocationExpressionSyntax invocation, SemanticModel model)
        {
            switch (expression)
            {
                case MemberAccessExpressionSyntax access
                    when ObjectOfAComponent.Contains(access.Name.Identifier.ValueText):
                    return model.GetTypeInfo(access.Expression).Type;
                case IdentifierNameSyntax name when ObjectOfAComponent.Contains(name.Identifier.ValueText):
                    var bound = model.GetSymbolInfo(name).Symbol;
                    if (bound is not null && !bound.IsStatic && bound.Kind == SymbolKind.Property)
                    {
                        return model.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
                    }
                    // Unbound — a headless score, whose contract may not declare
                    // the property — reads the name, as the call itself is read.
                    return bound is null
                        ? model.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType
                        : null;
                default:
                    return null;
            }
        }

        // ── Where a copy is made on purpose ─────────────────────────────────────

        // The SDK's own assemblies, by exact name: the runtime is where spawning
        // is implemented — its own Instantiate is the one the rule sends
        // everybody else to — and its editor and tests make copies on purpose.
        // ⛔ Exact: a prefix handed the exemption to any project that picked a
        // name like them, and read nothing it compiled.
        private static readonly string[] SdkAssemblies =
        {
            "RTMPE.SDK.Runtime", "RTMPE.SDK.Editor", "RTMPE.SDK.Tests", "RTMPE.SDK.Editor.Tests",
            "RTMPE.SDK.Tests.Performance",
        };

        // Unity's editor-only assemblies for the scripts under an `Editor` folder.
        private static readonly string[] EditorAssemblies =
        {
            "Assembly-CSharp-Editor", "Assembly-CSharp-Editor-firstpass",
        };

        private const string EditorNamespace = "UnityEditor";

        // The namespaces a test's attributes come from: NUnit's own, and the
        // Unity Test Framework's (UnityTest, UnitySetUp, …).
        private const string NUnitNamespace = "NUnit.Framework";
        private const string UnityTestToolsNamespace = "UnityEngine.TestTools";

        /// <summary>
        /// Whether no call in <paramref name="assembly"/> is read: one of the
        /// SDK's own, or an editor-only one — where a copy of a networked prefab
        /// is made on purpose (an inspector's preview) and never ships in a
        /// player.
        /// </summary>
        /// <remarks>
        /// ⛔ Not a test assembly by its references.  Unity's "Enable playmode
        /// tests for all assemblies" gives every assembly — the game's own
        /// <c>Assembly-CSharp</c> included — the references a test assembly
        /// has, so a reference would have silenced this rule for a whole game.
        /// Test code is recognised per type instead (<see cref="IsTestType"/>).
        /// </remarks>
        internal static bool IsExemptAssembly(IAssemblySymbol assembly)
        {
            if (assembly is null)
            {
                return false;
            }

            string name = assembly.Name ?? "";
            return SdkAssemblies.Contains(name, StringComparer.Ordinal)
                   || EditorAssemblies.Contains(name, StringComparer.Ordinal);
        }

        /// <summary>
        /// Whether <paramref name="type"/> is test code: it, a type it is nested
        /// in, or a base of either carries a test framework's attribute on
        /// itself or on one of its methods — the fixture, test and Unity test
        /// attributes, a setup or a teardown, or an attribute deriving from one
        /// of them — or implements the Unity Test Framework's
        /// <c>IMonoBehaviourTest</c>.  A play-mode test that instantiates the
        /// prefab it examines does so on purpose.
        /// </summary>
        internal static bool IsTestType(INamedTypeSymbol type)
        {
            for (var outer = type; outer is not null; outer = outer.ContainingType)
            {
                for (var current = outer; current is not null; current = current.BaseType)
                {
                    if (current.TypeKind == TypeKind.Error)
                    {
                        break;
                    }
                    // A method's attributes are read on types declared in source:
                    // the engine's bases carry hundreds of members and no test,
                    // and the classification asks this of every project type.
                    if (current.GetAttributes().Any(IsTestAttribute)
                        || current.Interfaces.Any(IsTestInterface)
                        || (current.Locations.Any(location => location.IsInSource)
                            && current.GetMembers().OfType<IMethodSymbol>()
                                .Any(method => method.GetAttributes().Any(IsTestAttribute))))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // A test's attribute, by the name the list below holds — bound or not:
        // unbound, as a headless score sees it with no test framework, the name
        // its author wrote is the reading (as the call itself is read); bound,
        // it must also come from a test framework's namespace.  ⛔ By name on
        // both paths, never by namespace alone: UnityEngine.TestTools also
        // holds ExcludeFromCoverage, which teams put on production code, and a
        // namespace-only reading exempted it in the IDE while the report — by
        // name — did not, so the two disagreed about a test.
        // The attribute's bases are read the same way: a team's own attribute
        // deriving from NUnit's TestAttribute makes a test to NUnit, and the
        // headless score, which cannot bind that base, reads it by its name.
        private static bool IsTestAttribute(AttributeData attribute)
            => IsTestAttributeClass(attribute.AttributeClass);

        private static bool IsTestAttributeClass(INamedTypeSymbol start)
        {
            for (var type = start; type is not null; type = type.BaseType)
            {
                if (!TestAttributeNames.Contains(type.Name, StringComparer.Ordinal))
                {
                    if (type.TypeKind == TypeKind.Error)
                    {
                        // A class whose base did not bind is "not an attribute
                        // class" to the compiler, which hands its use back as an
                        // error type naming that class: read on through the
                        // class it names, by the same rule.
                        return type is IErrorTypeSymbol error
                               && error.CandidateSymbols.OfType<INamedTypeSymbol>()
                                   .Any(candidate => candidate.TypeKind != TypeKind.Error
                                                     && IsTestAttributeClass(candidate.BaseType));
                    }

                    continue;
                }

                if (type.TypeKind == TypeKind.Error)
                {
                    return true;
                }

                string ns = type.ContainingNamespace?.ToDisplayString() ?? "";
                if (ns == NUnitNamespace || ns == UnityTestToolsNamespace)
                {
                    return true;
                }
            }

            return false;
        }

        // The Unity Test Framework's MonoBehaviour test: a component a
        // [UnityTest] runs through MonoBehaviourTest<T>, which carries no test
        // attribute of its own.  Read by its name when unbound, as an
        // attribute is, and from the framework's namespace when bound.
        private const string MonoBehaviourTestInterface = "IMonoBehaviourTest";

        private static bool IsTestInterface(INamedTypeSymbol type)
            => type.Name == MonoBehaviourTestInterface
               && (type.TypeKind == TypeKind.Error
                   || (type.ContainingNamespace?.ToDisplayString() ?? "") == UnityTestToolsNamespace);

        // The attributes that make a type or a method test code — a test, a
        // fixture, a setup or a teardown — as its author may spell them.  One
        // that only accompanies a test (Category, Ignore, ExcludeFromCoverage)
        // is not among them, and marks nothing.
        private static readonly string[] TestAttributeNames =
        {
            "Test", "TestAttribute", "TestCase", "TestCaseAttribute", "TestCaseSource", "TestCaseSourceAttribute",
            "Theory", "TheoryAttribute",
            "TestFixture", "TestFixtureAttribute", "TestFixtureSource", "TestFixtureSourceAttribute",
            "SetUpFixture", "SetUpFixtureAttribute",
            "SetUp", "SetUpAttribute", "TearDown", "TearDownAttribute",
            "OneTimeSetUp", "OneTimeSetUpAttribute", "OneTimeTearDown", "OneTimeTearDownAttribute",
            "UnityTest", "UnityTestAttribute", "UnitySetUp", "UnitySetUpAttribute",
            "UnityTearDown", "UnityTearDownAttribute",
        };

        /// <summary>
        /// Whether <paramref name="type"/>, or a type it is nested in, is one the
        /// editor drives — it derives from a type of <c>UnityEditor</c>'s (an
        /// inspector, a window, a property drawer, an importer) or implements
        /// one of its interfaces (a build hook) — and so never runs in a player,
        /// whatever assembly it sits in.
        /// </summary>
        /// <remarks>
        /// A static editor utility that derives from and implements nothing of
        /// the editor's — a <c>[MenuItem]</c> method on a plain class in a
        /// runtime assembly — is read as runtime code; a suppression is its
        /// answer.
        /// </remarks>
        internal static bool IsEditorType(INamedTypeSymbol type)
        {
            for (var outer = type; outer is not null; outer = outer.ContainingType)
            {
                for (var current = outer.BaseType; current is not null; current = current.BaseType)
                {
                    if (current.TypeKind == TypeKind.Error)
                    {
                        break;
                    }
                    if (IsInEditorNamespace(current))
                    {
                        return true;
                    }
                }

                if (outer.AllInterfaces.Any(IsInEditorNamespace))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInEditorNamespace(INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Error)
            {
                return false;
            }
            string ns = type.ContainingNamespace?.ToDisplayString() ?? "";
            return ns == EditorNamespace || ns.StartsWith(EditorNamespace + ".", StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether <paramref name="type"/> is an object pool the SDK draws
        /// instances from.  A pool creates the objects <c>SpawnManager</c> then
        /// activates and spawns, so its <c>Instantiate</c> is the spawn path, not
        /// a bypass of it.  By symbol where the SDK binds, by name where it does
        /// not — a headless score compiles against a contract that declares no
        /// pool, and an unresolved base keeps the name its author wrote.
        /// </summary>
        internal static bool IsNetworkObjectPool(INamedTypeSymbol type, INamedTypeSymbol poolInterface)
            => type is not null
               && type.AllInterfaces.Any(i => poolInterface is not null && i.TypeKind != TypeKind.Error
                   ? SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, poolInterface)
                   : i.Name == PoolInterfaceName);

        /// <summary>
        /// Whether the author suppressed <paramref name="id"/> at
        /// <paramref name="node"/> in the source: a <c>#pragma warning disable</c>
        /// in force there, or a <c>[SuppressMessage]</c> naming it on a
        /// declaration the call sits in — a local function, an accessor, a
        /// member, a type or a type around it.  Such a call is one its author has
        /// said stays local, and the classification leaves it out as the warning
        /// does.
        /// </summary>
        /// <remarks>
        /// ⚠️ A suppression made outside the source — a severity of <c>none</c>
        /// in an <c>.editorconfig</c>, or a global suppression file's
        /// <c>[assembly: SuppressMessage]</c> — hides the warning and leaves the
        /// classification as it is: neither is on the path from the call to the
        /// top of its file, which is all this reads.
        /// <para>
        /// The attribute is read from the declared SYMBOLS' attribute data,
        /// which is what the compiler reads: an alias is resolved, a constant or
        /// a concatenation evaluated, the arguments are in parameter order
        /// however they were named, every part of a partial type or method is
        /// merged, and an attribute aimed at the return value, a backing field or
        /// a parameter (<c>return:</c>, <c>field:</c>, <c>param:</c>) sits on
        /// that and not on the declaration.  Reading the syntax instead missed
        /// every one of those shapes in one direction or the other.
        /// </para>
        /// </remarks>
        internal static bool IsSuppressedInSource(SyntaxNode node, string id, SemanticModel model)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));

            // Every declaration the call sits in, innermost first.  ⛔ A lambda
            // declares no symbol here, and the compiler's suppression reads none
            // (measured — InstantiateDetectionTests holds the two readings equal).
            foreach (var ancestor in node.Ancestors())
            {
                ISymbol declared = ancestor switch
                {
                    LocalFunctionStatementSyntax function => model.GetDeclaredSymbol(function),
                    AccessorDeclarationSyntax accessor => model.GetDeclaredSymbol(accessor),
                    BaseMethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
                    BasePropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
                    BaseTypeDeclarationSyntax type => model.GetDeclaredSymbol(type),
                    VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax
                        => model.GetDeclaredSymbol(variable),
                    _ => null,
                };
                if (declared is not null && SuppressesById(declared, id))
                {
                    return true;
                }
            }

            bool suppressed = false;
            var root = (CSharpSyntaxNode)node.SyntaxTree.GetRoot();
            for (var directive = root.GetFirstDirective();
                 directive is not null && directive.SpanStart < node.SpanStart;
                 directive = directive.GetNextDirective())
            {
                if (directive is PragmaWarningDirectiveTriviaSyntax pragma
                    && pragma.IsActive
                    && (pragma.ErrorCodes.Count == 0
                        || pragma.ErrorCodes.Any(code => string.Equals(code.ToString().Trim(), id, StringComparison.Ordinal))))
                {
                    suppressed = pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword);
                }
            }

            return suppressed;
        }

        // The attributes the compiler's suppression honours, by their full name.
        private const string SuppressionNamespace = "System.Diagnostics.CodeAnalysis";
        private static readonly string[] SuppressionAttributes =
        {
            "SuppressMessageAttribute", "UnconditionalSuppressMessageAttribute",
        };

        // `[SuppressMessage("RTMPE.Usage", "RTMPE1030:…")]` on the symbol: the
        // check id is the constructor's second argument, and everything after
        // its colon is a title the author may have written.
        private static bool SuppressesById(ISymbol symbol, string id)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                var type = attribute.AttributeClass;
                if (type is null
                    || !SuppressionAttributes.Contains(type.Name, StringComparer.Ordinal)
                    || type.ContainingNamespace?.ToDisplayString() != SuppressionNamespace
                    || attribute.ConstructorArguments.Length < 2
                    || attribute.ConstructorArguments[1].Value is not string checkId)
                {
                    continue;
                }

                if (checkId == id || checkId.StartsWith(id + ":", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // The call binds to UnityEngine's Instantiate — or to nothing at all, which
        // is what a headless score sees: its contract declares no UnityEngine.Object,
        // so an unqualified `Instantiate(x)` inside a MonoBehaviour resolves nowhere.
        // A call the compiler resolved to a method of the project's own is that
        // project's factory, and is not read.
        private static bool BindsToUnityInstantiate(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            var info = model.GetSymbolInfo(invocation);
            var bound = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (bound is IMethodSymbol method)
            {
                return method.ContainingNamespace?.ToDisplayString() == UnityNamespace;
            }

            return invocation.Expression switch
            {
                IdentifierNameSyntax _ => true,
                GenericNameSyntax _ => true,
                MemberAccessExpressionSyntax access => IsUnityObjectQualifier(access.Expression),
                _ => false,
            };
        }

        private static bool IsUnityObjectQualifier(ExpressionSyntax qualifier)
            => qualifier switch
            {
                IdentifierNameSyntax name => UnityObjectQualifiers.Contains(name.Identifier.ValueText),
                MemberAccessExpressionSyntax access =>
                    access.Expression is IdentifierNameSyntax { Identifier.ValueText: UnityNamespace }
                    && UnityObjectQualifiers.Contains(access.Name.Identifier.ValueText),
                AliasQualifiedNameSyntax alias => IsUnityObjectQualifier(alias.Name),
                _ => false,
            };

        // The argument the copy is made of: named `original`, or the first
        // positional one.
        private static ArgumentSyntax OriginalArgument(InvocationExpressionSyntax invocation)
        {
            var arguments = invocation.ArgumentList.Arguments;
            return arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "original")
                   ?? arguments.FirstOrDefault(a => a.NameColon is null);
        }

        // `Instantiate<T>(T original, …)` names what it creates; every other
        // overload creates a copy of its original.
        private static ITypeSymbol CreatedType(
            InvocationExpressionSyntax invocation, ArgumentSyntax original, SemanticModel model)
        {
            var generic = invocation.Expression switch
            {
                GenericNameSyntax name => name,
                MemberAccessExpressionSyntax { Name: GenericNameSyntax name } => name,
                _ => null,
            };
            if (generic is not null && generic.TypeArgumentList.Arguments.Count == 1)
            {
                var named = model.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type;
                if (named is not null && named.TypeKind != TypeKind.Error)
                {
                    return named;
                }
            }

            return original is null ? null : model.GetTypeInfo(original.Expression).Type;
        }

        // The NetworkBehaviour `type` proves the object carries: itself when it
        // derives from one, and for a type parameter the constraint that makes
        // it one — `T` under `where T : NetworkBehaviour` is one whatever it
        // binds to, and a type parameter has no base type to walk.  A
        // constraint that is itself a type parameter is followed; the depth
        // bounds a cycle the compiler reports as an error anyway.
        private static ITypeSymbol NetworkBehaviourIn(ITypeSymbol type, INamedTypeSymbol networkBehaviour, int depth = 0)
        {
            if (type is ITypeParameterSymbol parameter)
            {
                if (depth > MaxConstraintDepth)
                {
                    return null;
                }
                foreach (var constraint in parameter.ConstraintTypes)
                {
                    var proven = NetworkBehaviourIn(constraint, networkBehaviour, depth + 1);
                    if (proven is not null)
                    {
                        return proven;
                    }
                }
                return null;
            }

            return type is not null && AuthoritySdkSymbols.DerivesFrom(type, networkBehaviour) ? type : null;
        }

        private const int MaxConstraintDepth = 8;

        // A GameObject, or a component — whose object Instantiate copies whole.
        // Read off the declared chain by name as well as by symbol, because a
        // headless score's contract declares both without UnityEngine.Object;
        // and for a type parameter, off its constraints.
        private static bool IsSceneObject(ITypeSymbol type, int depth = 0)
        {
            if (type is ITypeParameterSymbol parameter)
            {
                return depth <= MaxConstraintDepth
                       && parameter.ConstraintTypes.Any(constraint => IsSceneObject(constraint, depth + 1));
            }

            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.TypeKind == TypeKind.Error)
                {
                    return false;
                }
                if (current.ContainingNamespace?.ToDisplayString() == UnityNamespace
                    && (current.Name == "GameObject" || current.Name == "Component"))
                {
                    return true;
                }
            }

            return false;
        }

        // The NetworkBehaviour the result is asked for: on the spot —
        // `Instantiate(prefab).GetComponent<Enemy>()` — or through the local the
        // result is put in, by a lookup that reads the value the call put there.
        private static ITypeSymbol ComponentAskedOfTheResult(
            InvocationExpressionSyntax invocation, SemanticModel model, INamedTypeSymbol networkBehaviour)
        {
            var result = Outermost(invocation);

            if (result.Parent is MemberAccessExpressionSyntax access
                && access.Expression == result
                && access.Parent is InvocationExpressionSyntax onTheSpot
                && onTheSpot.Expression == access)
            {
                return NetworkBehaviourLookedUp(onTheSpot, model, networkBehaviour);
            }
            if (result.Parent is ConditionalAccessExpressionSyntax conditional
                && conditional.Expression == result
                && conditional.WhenNotNull is InvocationExpressionSyntax whenNotNull)
            {
                return NetworkBehaviourLookedUp(whenNotNull, model, networkBehaviour);
            }

            var local = LocalReceiving(result, model, out var write);
            if (local is null)
            {
                return null;
            }

            var body = result.Ancestors().FirstOrDefault(n =>
                n is BaseMethodDeclarationSyntax
                || n is AccessorDeclarationSyntax
                || n is LocalFunctionStatementSyntax
                || n is AnonymousFunctionExpressionSyntax);
            if (body is null)
            {
                return null;
            }

            var writes = WritesTo(local, body, model);
            foreach (var lookup in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // After the call, and with no other value put in the local between
                // the two: a lookup before it, or after the local was reassigned,
                // asks about some other object.
                if (lookup.SpanStart <= write.SpanStart
                    || AuthoritySignalExtractor.CalleeReceiver(lookup) is not IdentifierNameSyntax receiver
                    || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver).Symbol, local)
                    || writes.Any(w => w != write && w.SpanStart > write.SpanStart && w.SpanStart < lookup.SpanStart))
                {
                    continue;
                }

                var asked = NetworkBehaviourLookedUp(lookup, model, networkBehaviour);
                if (asked is not null)
                {
                    return asked;
                }
            }

            return null;
        }

        // The expression that carries the call's value: through parentheses and
        // through a cast or `as` to the type the caller wanted.
        private static ExpressionSyntax Outermost(ExpressionSyntax expression)
        {
            var current = expression;
            while (true)
            {
                switch (current.Parent)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        current = parenthesized;
                        continue;
                    case CastExpressionSyntax cast when cast.Expression == current:
                        current = cast;
                        continue;
                    case BinaryExpressionSyntax asCast
                        when asCast.IsKind(SyntaxKind.AsExpression) && asCast.Left == current:
                        current = asCast;
                        continue;
                    default:
                        return current;
                }
            }
        }

        // The local the result is stored in — its initializer, or a plain
        // assignment to it — and the node that stores it.
        private static ILocalSymbol LocalReceiving(ExpressionSyntax result, SemanticModel model, out SyntaxNode write)
        {
            switch (result.Parent)
            {
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }:
                    write = declarator;
                    return model.GetDeclaredSymbol(declarator) as ILocalSymbol;
                case AssignmentExpressionSyntax assignment
                    when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Right == result:
                    write = assignment;
                    return model.GetSymbolInfo(assignment.Left).Symbol as ILocalSymbol;
                default:
                    write = null;
                    return null;
            }
        }

        // Every place in the body a value is put in the local: its declaration's
        // initializer, an assignment of any kind, and an `out` or `ref` argument.
        private static SyntaxNode[] WritesTo(ILocalSymbol local, SyntaxNode body, SemanticModel model)
        {
            bool IsLocal(ExpressionSyntax expression)
                => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression).Symbol, local);

            return body.DescendantNodes().Where(node => node switch
            {
                VariableDeclaratorSyntax declarator => declarator.Initializer is not null
                    && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declarator), local),
                AssignmentExpressionSyntax assignment => IsLocal(assignment.Left),
                ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None)
                    && !argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword)
                    && IsLocal(argument.Expression),
                _ => false,
            }).ToArray();
        }

        // `x.GetComponent<T>()`, `x.TryGetComponent<T>(out …)` — or with T
        // inferred from the `out` argument's type — and `x.GetComponent(typeof(T))`,
        // for a T that is a NetworkBehaviour.
        private static ITypeSymbol NetworkBehaviourLookedUp(
            InvocationExpressionSyntax lookup, SemanticModel model, INamedTypeSymbol networkBehaviour)
        {
            string name = AuthoritySignalExtractor.CalleeName(lookup.Expression);
            if (name is null || !ComponentLookupNames.Contains(name))
            {
                return null;
            }

            // The compiler's answer first: it has the type argument an inferred
            // call never spells.
            var info = model.GetSymbolInfo(lookup);
            if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol { IsGenericMethod: true } method
                && method.TypeArguments.Length == 1
                && method.TypeArguments[0].TypeKind != TypeKind.Error)
            {
                // A type parameter carries what its constraints promise: `T`
                // under `where T : NetworkBehaviour` is one whatever it binds to.
                return NetworkBehaviourIn(method.TypeArguments[0], networkBehaviour);
            }

            TypeSyntax asked = lookup.Expression switch
            {
                GenericNameSyntax generic when generic.TypeArgumentList.Arguments.Count == 1
                    => generic.TypeArgumentList.Arguments[0],
                MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
                    when generic.TypeArgumentList.Arguments.Count == 1 => generic.TypeArgumentList.Arguments[0],
                MemberBindingExpressionSyntax { Name: GenericNameSyntax generic }
                    when generic.TypeArgumentList.Arguments.Count == 1 => generic.TypeArgumentList.Arguments[0],
                _ => (lookup.ArgumentList.Arguments.FirstOrDefault()?.Expression as TypeOfExpressionSyntax)?.Type,
            };
            if (asked is null)
            {
                return null;
            }

            return NetworkBehaviourIn(model.GetTypeInfo(asked).Type, networkBehaviour);
        }
    }
}
