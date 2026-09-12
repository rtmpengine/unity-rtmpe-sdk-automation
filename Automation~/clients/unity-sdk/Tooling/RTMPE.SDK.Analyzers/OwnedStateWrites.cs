using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Whether a method drives state its object's owner owns — replicated
    /// state, the object's own simulation fields, or its own transform.
    /// </summary>
    /// <remarks>
    /// 🔑 Declared once and asked by both readers. The owner-guard diagnostic
    /// decides whether to RECOMMEND a guard and the readiness score decides
    /// whether to FAULT a loop for lacking one; two copies of this rule would let
    /// the tool suggest nothing while the number went on demanding it, which is
    /// the shape a developer resolves by adding the guard anyway.
    /// </remarks>
    public static class OwnedStateWrites
    {
        /// <summary>
        /// The symbols this rule reads, resolved once from a compilation so no
        /// caller can hand it a different answer than its sibling.
        /// </summary>
        /// <remarks>
        /// 🔑 Resolved here rather than accepted as parameters. Both readers had
        /// passed the symbols in, and a caller passing <c>null</c> for one of
        /// them silently narrowed the rule to the other's evidence with every
        /// test of this class still green — a call-site defect no test of the
        /// callee can see. A surface a caller cannot misstate removes the shape.
        /// </remarks>
        public readonly struct Surface
        {
            private Surface(INamedTypeSymbol networkVariableBase, IPropertySymbol ownTransform)
            {
                NetworkVariableBase = networkVariableBase;
                OwnTransform = ownTransform;
            }

            /// <summary>Replicated state's base type, or null where the SDK is not referenced.</summary>
            public INamedTypeSymbol NetworkVariableBase { get; }

            /// <summary>
            /// <c>Component.transform</c> — the property every component reads to
            /// reach its own transform, matched by symbol so a Transform-typed
            /// member the author declares is not mistaken for it.
            /// </summary>
            public IPropertySymbol OwnTransform { get; }

            /// <summary>
            /// Whether both symbols this rule reads through actually resolved.
            /// </summary>
            /// <remarks>
            /// ⛔ A reader must ask this before treating a
            /// <see cref="Reading.NothingOwned"/> from
            /// <see cref="Read"/> as "drives nothing". With either symbol
            /// missing the arm that would have seen the write is simply absent,
            /// and the answer is "could not read" wearing the same word. A score
            /// that spends it as the former grades a moving object as needing no
            /// guard, which is the reading that flatters exactly the projects it
            /// understands least.
            /// </remarks>
            public bool CanReadEveryArm => NetworkVariableBase is not null && OwnTransform is not null;

            public static Surface Resolve(Compilation compilation)
                => compilation is null
                    ? default
                    : new Surface(
                        compilation.GetTypeByMetadataName(NetworkVariableBaseMetadataName),
                        OwnTransformProperty(compilation));

            // ⚠️ Found on the ENGINE's own base chain, never by asking for
            // `UnityEngine.Transform` by metadata name: Unity ships the type
            // through a facade as well as CoreModule, and
            // GetTypeByMetadataName answers null the moment a name is declared
            // twice — which would retire the arm in the editor and nowhere
            // else. Whatever `transform` resolves to up there IS the type, by
            // construction. 🔑 The chain is walked because Unity declares the
            // property on Component while the contract stub the headless
            // toolchain compiles against declares it on MonoBehaviour.
            private static IPropertySymbol OwnTransformProperty(Compilation compilation)
            {
                for (var current = compilation.GetTypeByMetadataName(MonoBehaviourMetadataName);
                     current is not null;
                     current = current.BaseType)
                {
                    foreach (var property in current.GetMembers(TransformPropertyName).OfType<IPropertySymbol>())
                    {
                        if (!property.IsStatic)
                        {
                            return property;
                        }
                    }
                }

                return null;
            }
        }

        private const string NetworkVariableBaseMetadataName = "RTMPE.Sync.NetworkVariableBase";
        private const string MonoBehaviourMetadataName = "UnityEngine.MonoBehaviour";
        private const string TransformPropertyName = "transform";

        /// <summary>
        /// What a method was found to drive, and whether the evidence it rests on
        /// could be read.
        /// </summary>
        /// <remarks>
        /// ⛔ Three answers rather than two, for the same reason
        /// <see cref="Surface.CanReadEveryArm"/> exists: a write whose symbols did
        /// not bind is not a write that reaches nothing, and a reader given one
        /// word for both spends the unknown as the clear. Every caller decides what
        /// <see cref="Unreadable"/> costs it, and the two here decide differently —
        /// the diagnostic stays silent, because a recommendation drawn from
        /// evidence nobody could read names an edit against working code; the score
        /// still grants the weight, and says on the record that it granted it over
        /// a reading that was partial.
        /// </remarks>
        public enum Reading
        {
            /// <summary>
            /// A write rests on a symbol or a type that did not bind, so whether it
            /// reaches owner-owned state is unknown. A positive find outranks it:
            /// the answer is <c>Unreadable</c> only where no write was read as
            /// owned.
            /// <para>
            /// ⛔ Declared first so it is <c>default(Reading)</c>. The whole finding
            /// was that the unknown got spent as the clear, and a zero-initialised
            /// field or a lookup miss would hand back exactly that clear again.
            /// </para>
            /// </summary>
            Unreadable,

            /// <summary>Every write was readable, and none reached owner-owned state.</summary>
            NothingOwned,

            /// <summary>The method drives state the object's owner owns.</summary>
            OwnedState,
        }

        /// <summary>
        /// Whether a method drives state the owner owns — replicated state,
        /// this object's own simulation fields, or its own transform — directly
        /// or through a helper it calls.
        /// </summary>
        /// <remarks>
        /// 🔑 The distinction is what the write REACHES, not that a write happens.
        /// <c>_hp -= n</c> writes this object's own state and belongs to whoever
        /// owns it; <c>_label.text = …</c> writes through a held reference and is
        /// this client's view of state it does not own. A predicate that asked
        /// only "does this mutate an instance member" answers yes to both — which
        /// is the rule that recommended a guard for a status display.
        /// <para>
        /// ⚠️ A write THROUGH a member still counts when that member is a
        /// NetworkVariable: <c>_score.Value = 1</c> and <c>_items.Add(x)</c> are
        /// replication, and replication is owner-only whatever the syntax.
        /// </para>
        /// <para>
        /// 🔑 Helpers are followed. A hook rarely holds the work — <c>Update()
        /// { Move(); }</c> is the ordinary shape — and a rule reading only the
        /// loop's own body would go quiet on precisely the codebases that
        /// factor. Only methods this type declares in source are followed, and
        /// each is visited once.
        /// </para>
        /// <para>
        /// ⚠️ The component's OWN <c>transform</c> counts, by symbol identity
        /// rather than by name. It is the state <c>NetworkTransform</c>
        /// replicates and the commonest owner-only write there is, so a rule
        /// blind to it reports nothing on the loop the guard was invented for —
        /// and grants the score's Ownership weight to a component every client
        /// is moving independently. A Transform reached through a member the
        /// author declares is somebody else's and does not count.
        /// </para>
        /// ⛔ What it still does not read: input polling. A frame that only
        /// samples input decides nothing until it writes, and the write is what
        /// this reads.
        /// <para>
        /// ⚠️ Answers <see cref="Reading.Unreadable"/> rather than
        /// <see cref="Reading.NothingOwned"/> where a write's evidence did not
        /// bind. It returns <c>bool</c> for neither reader on purpose: a two-valued
        /// answer here spelled "could not tell" and "reaches nothing" with one
        /// word, and every caller of it took the flattering half.
        /// </para>
        /// </remarks>
        public static Reading Read(
            MethodDeclarationSyntax declaration, SemanticModel model, INamedTypeSymbol type,
            Surface surface)
        {
            // ⚠️ A helper is followed wherever it is DECLARED, and the model
            // handed in binds one tree. `class Player : PlayerBase` with the base
            // in its own file is the ordinary Unity layout, and asking this model
            // about a node from that file is not a wrong answer — it is
            // `ArgumentException: Syntax node is not within syntax tree`, out of
            // the scorer and out of the analyzer as AD0001. The cache is the type
            // this project already uses for the same need, and it binds a tree
            // once however many helpers lead back to it.
            var models = new SemanticModelCache(model.Compilation);

            SemanticModel ModelFor(SyntaxTree tree)
                => tree == model.SyntaxTree ? model : models.GetSemanticModel(tree);

            var pending = new Queue<MethodDeclarationSyntax>();
            var visited = new HashSet<MethodDeclarationSyntax>();
            pending.Enqueue(declaration);
            visited.Add(declaration);

            // 🔑 Carried across the whole walk, helpers included. A loop whose own
            // body is readable and whose helper is not is still a loop this rule
            // cannot answer for, and resetting per method would report the last
            // one visited.
            bool anythingUnreadable = false;

            while (pending.Count > 0)
            {
                var method = pending.Dequeue();

                // An expression body is a body. `Update() => Move();` is the
                // shape a terse codebase writes, and reading only the braced
                // form answers "drives nothing" for every one of them.
                SyntaxNode body = (SyntaxNode)method.Body ?? method.ExpressionBody;
                if (body is null)
                {
                    continue;
                }

                var bodyModel = ModelFor(method.SyntaxTree);

                foreach (var node in body.DescendantNodes())
                {
                    // ⛔ A positive find outranks an unreadable one and returns
                    // here: the question is whether this method drives owned
                    // state, and one write that provably does answers it whatever
                    // else in the body could not be read.
                    switch (ReadWrite(node, bodyModel, type, surface))
                    {
                        case Reading.OwnedState:
                            return Reading.OwnedState;

                        case Reading.Unreadable:
                            anythingUnreadable = true;
                            break;
                    }

                    if (node is InvocationExpressionSyntax invocation)
                    {
                        foreach (var callee in CalleesOfThisType(invocation, bodyModel, type))
                        {
                            // ⚠️ A partial method's SYMBOL is its declaring half,
                            // which carries no body — so following the symbol alone
                            // reads `Update() { Step(); }` as driving nothing while
                            // `Step`'s implementation moves the transform. The
                            // implementation half is a separate declaration and has
                            // to be asked for by name.
                            var reachable = callee.PartialImplementationPart ?? callee;

                            foreach (var reference in reachable.DeclaringSyntaxReferences)
                            {
                                // ⛔ A reference compiled from SOURCE rather than
                                // metadata keeps its syntax, and that syntax can
                                // belong to another compilation — asking this one
                                // for a model over it throws rather than answering.
                                // The scorer states the same hazard over its loop
                                // lookup; here the walk would have carried it into
                                // an AD0001. Not looking is recorded, never spent
                                // as "nothing to find".
                                if (!bodyModel.Compilation.ContainsSyntaxTree(reference.SyntaxTree))
                                {
                                    anythingUnreadable = true;
                                    continue;
                                }

                                if (reference.GetSyntax() is MethodDeclarationSyntax helper
                                    && visited.Add(helper))
                                {
                                    pending.Enqueue(helper);
                                }
                            }
                        }
                    }
                }
            }

            return anythingUnreadable ? Reading.Unreadable : Reading.NothingOwned;
        }

        /// <summary>
        /// The instance methods of <paramref name="type"/> an invocation may reach —
        /// the one it resolved to, or, where overload resolution failed, every
        /// candidate that belongs to this type.
        /// </summary>
        /// <remarks>
        /// 🔴 The candidates are the half that was missing. `Step(Missing.Value)`
        /// binds to no symbol, so the helper was never walked and a loop whose work
        /// lives entirely in `Step` read as driving nothing — the finding's own
        /// sentence, one call deeper. ⛔ An invocation that binds to nothing AND
        /// offers no candidate of this type is left alone rather than called
        /// unreadable: `GetComponent&lt;T&gt;()` is exactly that shape in every
        /// headless compilation here, and it reaches nothing this object owns
        /// whether or not the engine was referenced.
        /// </remarks>
        private static IEnumerable<IMethodSymbol> CalleesOfThisType(
            InvocationExpressionSyntax invocation, SemanticModel model, INamedTypeSymbol type)
        {
            var info = model.GetSymbolInfo(invocation);
            var resolved = info.Symbol is null ? info.CandidateSymbols : ImmutableArray.Create(info.Symbol);

            foreach (var candidate in resolved)
            {
                if (candidate is IMethodSymbol method
                    && !method.IsStatic
                    && InstanceMemberOf(method, type))
                {
                    yield return method;
                }
            }
        }

        // One node's verdict: a direct write to this type's own instance state, any
        // write reaching a NetworkVariable it holds, or — where the symbols the
        // question rests on did not bind — no verdict at all.
        private static Reading ReadWrite(
            SyntaxNode node, SemanticModel model, INamedTypeSymbol type, Surface surface)
        {
            ExpressionSyntax written = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                // 🔑 Not an inference: the companion collector in
                // ConversionOpportunityAnalyzer registers OperationKind.Argument
                // beside the assignments for this reason. `Integrate(ref _x)`
                // hands the callee a storage location, and what comes back is a
                // write whatever the callee does with it.
                ArgumentSyntax { RefKindKeyword: var refKind } argument
                    when refKind.IsKind(SyntaxKind.RefKeyword) || refKind.IsKind(SyntaxKind.OutKeyword)
                    => argument.Expression,
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
                return Reading.NothingOwned;
            }

            // ⛔ A call on a held member says nothing about who owns what it
            // changes: `_items.Add(x)` mutates a list this object owns and
            // `_animator.SetBool(…)` drives this client's view of somebody
            // else's. The two are one shape to a syntax pass, so neither is
            // evidence here — unless the receiver replicates, which the check
            // below still reaches. ⚠️ The sibling predicate for RTMPE2004 counts
            // both, and correctly: it asks whether a method changes state worth
            // routing through the server, not whose frame the change belongs in.
            bool throughACall = node is InvocationExpressionSyntax;

            // ⛔ A shape this rule does not read is not an unreadable one. What
            // comes back here is a simple name or a `this.`/`base.`-qualified one
            // and nothing else, so a null root means the write was reached through
            // an expression — `GetComponent<Rigidbody>().velocity = v` — which is a
            // handle to another object whether or not it binds.
            var root = LeftmostReceiver(written);
            if (root is null)
            {
                return Reading.NothingOwned;
            }

            // ⛔ …and a name that binds to NOTHING is not unreadable either, on a
            // premise worth stating: both readers refuse a type whose base chain
            // carries an error type — the score never reaches it and the
            // diagnostic's InheritsFrom answers false — so every type asked here
            // has a chain that bound, and a bare name that did not is therefore
            // not a member of it. What it is instead is an engine type the
            // headless contract does not model: `Time.timeScale = 0f` reaches
            // here as an unbound `Time`, and calling that unreadable was measured
            // to qualify the score of a loop writing a global nobody owns.
            var member = model.GetSymbolInfo(root).Symbol;
            ITypeSymbol held = member switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => null,
            };

            if (held is null || member.IsStatic || !InstanceMemberOf(member, type))
            {
                return Reading.NothingOwned;
            }

            if (surface.NetworkVariableBase is not null && DerivesFrom(held, surface.NetworkVariableBase))
            {
                return Reading.OwnedState;
            }

            if (surface.OwnTransform is not null
                && SymbolEqualityComparer.Default.Equals(member, surface.OwnTransform))
            {
                return Reading.OwnedState;
            }

            // ⛔ An unbound member type is NOT read as unknown here, and the two
            // reasons are worth keeping. It cannot replicate: an error type has no
            // base chain, so it derives from nothing, and the base it would have to
            // derive from is the SDK's — which `CanReadEveryArm` has already proven
            // resolved. And where the write does not descend into the member,
            // `_handle = x` and `_struct = x` are the same act — this object's own
            // field taking a value — which the walk below answers without asking
            // the type at all. The type decides exactly one question, the hop, and
            // that is where it is asked and where not knowing it is recorded.
            //
            // Direct, meaning the write lands in storage this object holds rather
            // than through a handle to somebody else's.
            return throughACall ? Reading.NothingOwned : ReadStorageHops(root, written, model);
        }

        /// <summary>
        /// Whether the write at <paramref name="written"/> lands inside the
        /// storage <paramref name="root"/> names, rather than through a handle it
        /// holds to another object — answered in <see cref="Reading"/> because at
        /// this point the two questions coincide: <paramref name="root"/> has
        /// already been established as an instance member of the type being
        /// judged, so landing inside it IS owner-owned.
        /// </summary>
        /// <remarks>
        /// 🔑 The step that matters is the one that crosses a REFERENCE. A member
        /// of a struct this object holds is this object's storage —
        /// <c>_velocity.y = 0f</c> changes the same bytes <c>_velocity = …</c>
        /// does — while <c>_label.text = "x"</c> reaches through a handle and
        /// changes a different object. The two are one shape to a syntax pass,
        /// and only the receiver's TYPE tells them apart.
        /// <para>
        /// ⛔ An index never crosses that boundary: <c>_trail[0]</c> is storage
        /// this object owns whether the elements are structs or handles, which is
        /// why an element hop is admitted without asking the type. What the
        /// element hop does not admit is the member hop that FOLLOWS a reference
        /// element — <c>_map[key].position = p</c> — and that is refused by the
        /// same rule as <c>_label.text</c>.
        /// </para>
        /// </remarks>
        private static Reading ReadStorageHops(
            ExpressionSyntax root, ExpressionSyntax written, SemanticModel model)
        {
            var expression = written;
            while (!ReferenceEquals(expression, root))
            {
                switch (expression)
                {
                    case ElementAccessExpressionSyntax element:
                        expression = element.Expression;
                        continue;

                    // ⛔ The hop is decided by the receiver's TYPE, so a receiver
                    // whose type did not bind decides nothing. It is the same
                    // refusal a reference receiver earns and it means the opposite:
                    // `_velocity.y = 0f` against an unresolved struct read as a
                    // write through somebody else's handle.
                    case MemberAccessExpressionSyntax member:
                        switch (Cross(model.GetTypeInfo(member.Expression).Type))
                        {
                            case Hop.Unreadable:
                                return Reading.Unreadable;

                            case Hop.CrossedAReference:
                                return Reading.NothingOwned;
                        }

                        // Hop.StayedInside — a member of a struct this object
                        // holds is the same storage, so the walk keeps going.
                        expression = member.Expression;
                        continue;

                    default:
                        return Reading.NothingOwned;
                }
            }

            return Reading.OwnedState;
        }

        /// <summary>
        /// What one member hop did, judged from the receiver's type alone. Distinct
        /// from <see cref="Reading"/> on purpose: this answers where the write
        /// LANDED, and only the caller's context turns that into an answer about
        /// who owns it.
        /// </summary>
        private enum Hop
        {
            Unreadable,
            StayedInside,
            CrossedAReference,
        }

        private static Hop Cross(ITypeSymbol receiver)
            => receiver is null || receiver.TypeKind == TypeKind.Error
                ? Hop.Unreadable
                : receiver.IsValueType ? Hop.StayedInside : Hop.CrossedAReference;

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

        private static ExpressionSyntax LeftmostReceiver(ExpressionSyntax written)
            => ConversionOpportunityAnalyzer.LeftmostReceiverOf(written);

        private static bool InstanceMemberOf(ISymbol member, INamedTypeSymbol type)
            => ConversionOpportunityAnalyzer.IsInstanceMemberOf(member, type);
    }
}
