using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Converts planned fields into NetworkVariable members: retypes a plain
    /// private field (or adds a companion beside a serialized config field),
    /// constructs it inside <c>protected override void OnNetworkSpawn()</c> with
    /// the allocator-issued id, and rewrites every read/write site to
    /// <c>.Value</c>. Purely syntactic and deliberately conservative: any shape
    /// whose rewrite cannot be proven safe from this one compilation unit is
    /// refused whole — the transform returns the unmodified root and a reason,
    /// never a partial or speculative edit. All generated code is built from
    /// typed factories reusing the original tokens and expression nodes, so a
    /// hostile member name or initializer cannot splice code.
    /// </summary>
    public static class NetworkVariableGenerationTransform
    {
        private const string SyncNamespace = "RTMPE.Sync";
        private const string SpawnHookName = "OnNetworkSpawn";
        private const string ValuePropertyName = "Value";

        // References inside these members — or inside anything they reach — run
        // before OnNetworkSpawn constructs the variable: a rewritten access there
        // is a compile-clean runtime NRE, the exact class of drift that survives
        // a compile gate.
        //
        // ⛔ Pre-spawn only, and `OnDisable`/`OnDestroy` are deliberately absent —
        // but not for the reason it is tempting to give. After a despawn the
        // variable object still exists and the write is dropped; before the FIRST
        // spawn it is null, and a scene that never joins a room reaches
        // `OnDestroy` on quit with the same NRE this list exists to prevent.
        //
        // ⚠️ They stay out because refusing them would refuse nearly every real
        // conversion: unsubscribing `OnValueChanged` in `OnDestroy` is the
        // idiomatic teardown, and it dereferences the variable exactly as a write
        // does. The bound is stated in `diagnostics.md` rather than enforced
        // here; a rule that fires on the common correct shape teaches people to
        // stop reading it.
        // 🔑 The set is declared once, in the analyzer project, because RTMPE1011
        // reads the same question and carried three names of these seven.
        private static readonly string[] PreSpawnHookNames =
            RTMPE.SDK.Analyzers.PreSpawnHooks.Names.ToArray();

        // The idiomatic initial values that are safe to relocate from a field
        // initializer into spawn-time construction: literals, default, and these
        // well-known pure statics. Everything else is a semantic change a machine
        // must not silently make.
        //
        // 🚨 A type joins the closed map and this list does not follow it, and the
        // conversion refuses the idiomatic field: `private Vector2 _aim =
        // Vector2.zero;` was declined while its Vector3 twin converted, so the
        // half of the feature an author actually meets was missing. The contract
        // stub declares these statics precisely because this list names them, and
        // WhatTheContractDeclaresIsWhatTheAllowlistNames holds the two together
        // in the direction that was open.
        private static readonly string[] AllowlistedStaticInitializers =
        {
            "Quaternion.identity",
            "Vector2.zero", "Vector2.one", "Vector2.up", "Vector2.down",
            "Vector2.left", "Vector2.right",
            "Vector2Int.zero", "Vector2Int.one",
            "Vector2Int.up", "Vector2Int.down", "Vector2Int.left", "Vector2Int.right",
            "Vector3.zero", "Vector3.one", "Vector3.up", "Vector3.down",
            "Vector3.left", "Vector3.right", "Vector3.forward", "Vector3.back",
            "string.Empty",
        };

        /// <summary>
        /// The allowlist, for the test that holds it to the contract stub. The
        /// two are one statement in two files and the direction that was open —
        /// a contract static the allowlist does not name — is a conversion that
        /// refuses an idiomatic field.
        /// </summary>
        public static IReadOnlyList<string> AllowlistedInitializersForTest
            => AllowlistedStaticInitializers;

        public static CompilationUnitSyntax Apply(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, ConversionPlan plan)
            => Apply(root, target, plan, out _);

        public static CompilationUnitSyntax Apply(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, ConversionPlan plan,
            out string refusalReason)
        {
            refusalReason = null;
            if (root is null || target is null || plan is null || plan.Conversions.Count == 0)
            {
                return root;
            }

            if (!TransformPreconditions.TargetBelongsToRoot(root, target))
            {
                refusalReason = TransformPreconditions.ForeignTargetRefusal;
                return root;
            }

            if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                refusalReason = "the file does not parse cleanly — converting a broken tree writes broken output";
                return root;
            }

            if (target.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                refusalReason = "the type is partial — a field and its references may live in another file this rewrite cannot see";
                return root;
            }

            // Resolve every planned member to its declarator first; a conversion
            // that already happened (field gone or already the NetworkVariable
            // type) is skipped so a re-run is a provable no-op.
            var pending = new List<(PlannedConversion Plan, FieldDeclarationSyntax Field, VariableDeclaratorSyntax Declarator)>();
            foreach (var conversion in plan.Conversions)
            {
                var (field, declarator) = FindField(target, conversion.MemberName);
                if (field is null)
                {
                    continue;
                }

                if (DeclaredTypeName(field.Declaration.Type) == conversion.NetworkVariableTypeName)
                {
                    continue; // already converted in place
                }

                if (conversion.Arm == ConversionArm.Companion
                    && conversion.CompanionFieldName != null)
                {
                    var (existingCompanion, _) = FindField(target, conversion.CompanionFieldName);
                    if (existingCompanion != null
                        && DeclaredTypeName(existingCompanion.Declaration.Type) == conversion.NetworkVariableTypeName)
                    {
                        continue; // the companion already exists — a re-run is a no-op
                    }
                }

                string reason = Refuse(root, target, conversion, field, declarator);
                if (reason != null)
                {
                    refusalReason = reason;
                    return root;
                }

                pending.Add((conversion, field, declarator));
            }

            if (pending.Count == 0)
            {
                return root;
            }

            // Declaration order governs both construction order and, upstream,
            // id issue order — independent of the plan list's own order.
            pending.Sort((a, b) => a.Field.SpanStart.CompareTo(b.Field.SpanStart));

            var newLine = BlockEditing.DetectNewLine(root);
            var updatedTarget = RewriteReferences(
                target, pending.Where(p => p.Plan.Arm == ConversionArm.InPlace).Select(p => p.Plan.MemberName));
            // Decided from the declaration while it is still attached to its file.
            // Below this line the class node has been rewritten and detached, and a
            // base-chain walk over a detached node sees no siblings to walk.
            updatedTarget = EditMembers(
                updatedTarget, pending, newLine, InheritsAChainableSpawnHook(target),
                out refusalReason);
            if (refusalReason != null)
            {
                return root;
            }

            var replaced = root.ReplaceNode(target, updatedTarget);
            return ImportEditing.IsInScope(target, SyncNamespace)
                ? replaced
                : ImportEditing.AddFileLevelImport(replaced, SyncNamespace);
        }

        // ── Refuse-guards (§6.5) — each returns a human-actionable reason ────────

        private static string Refuse(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, PlannedConversion conversion,
            FieldDeclarationSyntax field, VariableDeclaratorSyntax declarator)
        {
            string member = conversion.MemberName;

            if (HasDirectiveTrivia(field))
            {
                return "'" + member + "' is declared under #if directive trivia — a rewrite could unbalance the directives";
            }

            // The check above sees a directive attached to the field itself; this
            // one sees the field sitting deeper inside a region another member
            // opened. Both matter, and only together do they establish that every
            // converted field is unconditional — the premise the spawn hook's
            // unconditional placement rests on. Converting a conditional field
            // would put its construction in a scope that cannot name it.
            if (target.Members.IndexOf(field) >= BlockEditing.FirstConditionalIndex(target.Members))
            {
                return "'" + member + "' is enclosed by a conditional compilation region — the construction"
                    + " this conversion adds cannot be placed where it provably sees the field";
            }

            if (field.AttributeLists.Any(list => list.Attributes.Any(
                a => RightmostName(a.Name) is "NetworkVariable" or "NetworkVariableAttribute")))
            {
                return "'" + member + "' already carries [NetworkVariable] — RTMPE1013's domain, not a conversion candidate";
            }

            // Two faults answered by two different edits, so two reasons. A type
            // the map does not carry is answered by declaring a variable type; a
            // type the map carries under another wrapper is answered by naming the
            // wrapper it already chose. Under one sentence the second read as the
            // first, and sent an author away to write a type that already ships.
            if (!NetworkVariableTypeMap.TryMap(DeclaredTypeName(field.Declaration.Type), out string mapped))
            {
                return "'" + member + "' has type '" + field.Declaration.Type + "', which is outside the"
                    + " conversion's closed map (" + NetworkVariableTypeMap.SupportedTypeList + ") — "
                    + NetworkVariableTypeMap.DeclareYourOwnVariableRemedy;
            }

            if (mapped != conversion.NetworkVariableTypeName)
            {
                return "'" + member + "' has type '" + field.Declaration.Type + "', which the closed map sends to "
                    + mapped + ", not the " + conversion.NetworkVariableTypeName + " this plan names — name "
                    + mapped + " for this member";
            }

            if (conversion.Arm == ConversionArm.InPlace)
            {
                string reason = CheckDeclarationForm(field, member);

                if (reason == null && !IsPrivate(field))
                {
                    reason = "'" + member + "' is not private — references outside this file cannot be rewritten";
                }

                if (reason == null && IsUnitySerializedSyntactically(field))
                {
                    reason = "'" + member + "' is Unity-serialized — retyping it discards Inspector values";
                }

                reason ??= CheckInitializer(declarator, field);
                reason ??= CheckReferences(root, target, member, field.Declaration.Type);

                // ⛔ The remedy is appended HERE, once, rather than written into
                // each reason — see CompanionArmRemedy. Every refusal this block
                // can produce is in-place only.
                return reason == null ? null : reason + CompanionArmRemedy(member);
            }

            // Companion arm: the config field is never touched, so only the new
            // field's name must be free.
            string companion = conversion.CompanionFieldName;
            if (string.IsNullOrEmpty(companion))
            {
                return "'" + member + "' has no companion field name in the plan";
            }

            if (MemberNameExists(target, companion))
            {
                return "companion name '" + companion + "' already exists on the type";
            }

            return null;
        }

        // The in-place arm retypes the declaration where it stands — keeping its
        // modifiers — and moves the value into the spawn hook. Three modifiers make
        // that shape wrong rather than merely unsupported, so each carries its own
        // reason. The candidate analysis excludes all three from the opportunity set,
        // so only a member named explicitly on the command line reaches here. A
        /// <summary>
        /// What to do instead — appended to every refusal the in-place arm
        /// returns, and named with the syntax that produces it.
        /// </summary>
        /// <remarks>
        /// 🔑 Every reason that block can produce is IN-PLACE ONLY: the companion
        /// branch below it checks exactly one thing, that the new name is free.
        /// So they all have the same way out, and until 2026-09-11 four of some
        /// twenty said so — in the words *"the companion arm is the safe shape"*,
        /// which names no syntax, so a reader still had to find it.
        ///
        /// <para>🔴 An integrator hit this on the member their game is built
        /// around — a direction vector assigned in `Awake` — was told only that
        /// retyping it would be a runtime NRE, and reported the conversion as a
        /// dead end for that class of field. The arm that converts it was one
        /// colon away. ⛔ This is the same defect as the `List&lt;T&gt;` remedy
        /// repaired the same week: a refusal that points past a feature the
        /// package already ships.</para>
        ///
        /// <para>⛔ Measured through the real CLI rather than reasoned — a
        /// pre-spawn access, a `const`, a non-private field and a Unity-serialized
        /// field all refuse in place, and all four convert as
        /// <c>member:companionName</c> with exit 0.</para>
        ///
        /// <para>⚠️ It says "a replicated companion beside it", not "this fixes
        /// your game": the companion arm declares, constructs and seeds the
        /// variable and writes NO bridge back to the original field. Promising
        /// more than that here would be the same mistake one layer up.</para>
        /// </remarks>
        private static string CompanionArmRemedy(string member) =>
            ".  The companion arm converts it: pass --member " + member
            + ":<companionName> to declare a replicated companion beside it, leaving '"
            + member + "' itself untouched.";

        // declaration carrying more than one reports the first match; each reason
        // stands alone as sufficient grounds to decline.
        private static string CheckDeclarationForm(FieldDeclarationSyntax field, string member)
        {
            if (field.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                return "'" + member + "' is const — it binds a compile-time value folded into every use"
                    + " site, not the runtime object this conversion constructs";
            }

            if (field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            {
                return "'" + member + "' is readonly — it can be assigned only in its declaration or a"
                    + " constructor, and this conversion constructs it in " + SpawnHookName;
            }

            if (field.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                return "'" + member + "' is static — one replicated slot would be shared by every instance"
                    + " of the type, and each spawn would rebind it to whichever object spawned last";
            }

            return null;
        }

        private static string CheckInitializer(VariableDeclaratorSyntax declarator, FieldDeclarationSyntax field)
        {
            var initializer = declarator.Initializer?.Value;
            if (initializer is null)
            {
                return null;
            }

            if (initializer is LiteralExpressionSyntax literal
                && !literal.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return null;
            }

            if (initializer.IsKind(SyntaxKind.DefaultLiteralExpression)
                || initializer is DefaultExpressionSyntax)
            {
                return null;
            }

            string text = initializer.ToString();
            foreach (string allowed in AllowlistedStaticInitializers)
            {
                if (text == allowed || text.EndsWith("." + allowed, System.StringComparison.Ordinal))
                {
                    return null;
                }
            }

            if (IsZeroQuaternion(initializer))
            {
                return null; // normalised to identity at emission
            }

            return "'" + declarator.Identifier.ValueText
                + "' has an initializer outside the relocation allowlist — moving its evaluation to spawn time is a semantic change";
        }

        private static string CheckReferences(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, string member,
            TypeSyntax declaredType)
        {
            // A reference inside an inactive #if branch is not a node at all — it
            // survives only as disabled-text trivia — so the rewrite below can
            // neither see nor retype it; once that branch compiles the stale
            // direct-field access would no longer build. Refuse the conversion,
            // exactly as the RPC transform refuses a disabled-text call site.
            foreach (var trivia in target.DescendantTrivia(descendIntoTrivia: true))
            {
                if (trivia.IsKind(SyntaxKind.DisabledTextTrivia)
                    && trivia.ToString().Contains(member))
                {
                    return "'" + member + "' appears inside text disabled by a preprocessor directive"
                        + " — a reference under an inactive #if cannot be seen or rewritten";
                }
            }

            // Any construct that introduces the member's spelling into a narrower
            // scope makes a bare identifier ambiguous to a syntax-only pass, so the
            // conversion refuses rather than bind by name alone. A declaration node
            // covers locals, parameters, and pattern/catch variables; the rest —
            // foreach iteration variables, local functions, and LINQ query range
            // variables — carry the name on a token, so each is matched on its kind.
            foreach (var node in target.DescendantNodes())
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax declarator
                        when declarator.Identifier.ValueText == member
                            && declarator.Parent?.Parent is not FieldDeclarationSyntax:
                        return "'" + member + "' is shadowed by a local declaration — binding is ambiguous without a semantic model";
                    case ParameterSyntax parameter when parameter.Identifier.ValueText == member:
                        return "'" + member + "' is shadowed by a parameter — binding is ambiguous without a semantic model";
                    case SingleVariableDesignationSyntax designation when designation.Identifier.ValueText == member:
                        return "'" + member + "' is shadowed by a pattern variable — binding is ambiguous without a semantic model";
                    case CatchDeclarationSyntax catchDeclaration when catchDeclaration.Identifier.ValueText == member:
                        return "'" + member + "' is shadowed by a catch variable — binding is ambiguous without a semantic model";
                    case ForEachStatementSyntax forEach when forEach.Identifier.ValueText == member:
                        return "'" + member + "' is shadowed by a foreach iteration variable — binding is ambiguous without a semantic model";
                    case LocalFunctionStatementSyntax localFunction when localFunction.Identifier.ValueText == member:
                        return "'" + member + "' is shadowed by a local function — binding is ambiguous without a semantic model";
                    case FromClauseSyntax fromClause when fromClause.Identifier.ValueText == member:
                    case LetClauseSyntax letClause when letClause.Identifier.ValueText == member:
                    case JoinClauseSyntax joinClause when joinClause.Identifier.ValueText == member:
                    case JoinIntoClauseSyntax joinInto when joinInto.Identifier.ValueText == member:
                    case QueryContinuationSyntax continuation when continuation.Identifier.ValueText == member:
                        return "'" + member + "' is shadowed by a query range variable — binding is ambiguous without a semantic model";
                }
            }

            var references = ReferencesIn(target, member, out string collectReason);
            if (collectReason != null)
            {
                return collectReason;
            }

            // Once per member checked rather than once per reference: the closure
            // is a property of the TYPE, and every reference below asks the same
            // question of it. ⚠️ Not once per pass — this method is called for
            // each planned conversion, so a plan converting twenty fields builds
            // it twenty times. Measured at ~35 ms on a 400-method type, which is
            // a fifth of that plan's cost and not worth a cache whose lifetime
            // would have to be reasoned about.
            var preSpawnReachable = PreSpawnReachableMembers(target);

            // 🔴 A call or a write THROUGH the member reaches a COPY once the field
            // is read as a property, and for a mutable struct that copy is thrown
            // away. `_aim.Set(1f, 2f)` becomes `_aim.Value.Set(1f, 2f)`: it
            // compiles in Unity, mutates a temporary, never marks the variable
            // dirty and therefore never reaches a peer. That is worse than a
            // refusal and worse than a compile error, because nothing at all says
            // it happened — the author sees a converted field that silently
            // stopped replicating. A write (`_aim.x = 1`) is the same shape and at
            // least announces itself (CS1612), but only where a compiler runs over
            // the result, which the shipped code fix does not do.
            //
            // ⛔ Scoped by the LANGUAGE rather than by a remembered list of types.
            // A type the language spells with a keyword — `int`, `float`, `bool`,
            // `string` — has no member that can mutate its receiver, so
            // `_score.ToString()` converts as it always has. Everything else the
            // closed map carries is an engine struct with `Set`, `Normalize` and
            // settable components, and syntax alone cannot tell a mutator from a
            // reader: refuse whole, as this transform refuses every other shape it
            // cannot prove.
            if (declaredType is not PredefinedTypeSyntax)
            {
                foreach (var identifier in references)
                {
                    // 🚨 Through the parentheses FIRST. `(_aim)` is the same
                    // expression as `_aim` to the compiler and a different node to
                    // a rule that reads the immediate parent — so the first
                    // version of this guard, which asked whether the parent was a
                    // member access, let `(_aim).Set(1f, 2f)` past. That converts,
                    // compiles, mutates a temporary and never replicates: the
                    // exact outcome the guard exists to prevent, reached by typing
                    // two characters. Measured, not imagined.
                    var reference = ThroughParentheses(identifier);

                    // `_aim[0] = 1f` — an indexer write lands on the same copy, and
                    // the engine structs ship a settable one. A READ through an
                    // indexer is fine and stays allowed, as a read through a member
                    // does.
                    if (reference.Parent is ElementAccessExpressionSyntax element
                        && element.Expression == reference
                        && IsWriteTarget(element))
                    {
                        return "'" + member + "' has an element written through an indexer and its type is a"
                            + " struct — after the conversion that write lands on a copy taken from the"
                            + " variable; assign a whole value instead (" + member + " = new " + declaredType
                            + "(…)) and convert again";
                    }

                    if (reference.Parent is not MemberAccessExpressionSyntax access
                        || access.Expression != reference)
                    {
                        continue;
                    }

                    // And through them again on the way out: `(_aim.x) = 1f` and
                    // `(_aim).Set(…)` differ only in where the parentheses sit.
                    var whole = ThroughParentheses(access);

                    // `(_aim.x, _aim.y) = (1f, 2f)` — a deconstruction target is a
                    // write, and its parent is the tuple rather than the
                    // assignment, so the plain assignment test below cannot see it.
                    if (IsDeconstructionTarget(whole))
                    {
                        return "'" + member + "' is written through a member ('" + access.Name + "') by"
                            + " deconstruction and its type is a struct — after the conversion that write"
                            + " lands on a copy taken from the variable; assign a whole value instead ("
                            + member + " = new " + declaredType + "(…)) and convert again";
                    }

                    if (whole.Parent is InvocationExpressionSyntax invocation && invocation.Expression == whole)
                    {
                        return "'" + member + "' has a method called on it ('" + access.Name + "') and its type is"
                            + " a struct — after the conversion that call reaches a copy taken from the variable,"
                            + " so a mutating one would be discarded silently and never replicate; assign a whole"
                            + " value instead (" + member + " = new " + declaredType + "(…)) and convert again";
                    }

                    if (whole.Parent is AssignmentExpressionSyntax assignment && assignment.Left == whole)
                    {
                        return "'" + member + "' is written through a member ('" + access.Name + "') and its type"
                            + " is a struct — after the conversion that write lands on a copy taken from the"
                            + " variable; assign a whole value instead (" + member + " = new " + declaredType
                            + "(…)) and convert again";
                    }

                    if (whole.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax)
                    {
                        return "'" + member + "' has a member of it incremented or decremented ('" + access.Name
                            + "') and its type is a struct — after the conversion that operates on a copy taken"
                            + " from the variable; assign a whole value instead and convert again";
                    }
                }
            }

            foreach (var identifier in references)
            {
                if (identifier.Ancestors().OfType<ArgumentSyntax>()
                    .Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None) && a.Expression.DescendantNodesAndSelf().Contains(identifier)))
                {
                    return "'" + member + "' is passed by ref/out — a NetworkVariable cannot stand in for a ref location";
                }

                // The rewrite retypes the member and reads it through `.Value`, a
                // property — and a property is a value, not a storage location.
                // Argument position is only the most common place a storage
                // location is demanded; a ref binding and an address-of demand one
                // just as hard, and the compiler refuses each of them (CS0206,
                // CS8156, CS0211) after the edit has already been written to disk.
                if (identifier.Ancestors().OfType<RefExpressionSyntax>()
                    .Any(r => r.Expression.DescendantNodesAndSelf().Contains(identifier)))
                {
                    return "'" + member + "' is bound by ref — a ref local or a ref return needs a storage"
                        + " location, and the converted member is read through a property";
                }

                if (identifier.Ancestors().OfType<PrefixUnaryExpressionSyntax>()
                    .Any(u => u.IsKind(SyntaxKind.AddressOfExpression)
                        && u.Operand.DescendantNodesAndSelf().Contains(identifier)))
                {
                    return "'" + member + "' has its address taken — the converted member is read through a"
                        + " property, which has no address";
                }

                string preSpawn = PreSpawnContextOf(identifier, preSpawnReachable);
                if (preSpawn != null)
                {
                    return "'" + member + "' is accessed in " + preSpawn
                        + ", which runs before OnNetworkSpawn constructs it — the rewrite would be a runtime NRE";
                }
            }

            // References outside the target type in the same file (another type
            // reaching a private via nesting) are not rewritten — refuse instead.
            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.ValueText != member
                    || identifier.Ancestors().Contains(target)
                    || IsInsideNameOf(identifier))
                {
                    continue;
                }

                // 🔑 Except where the other type declares a member of the same
                // name. A bare identifier — or one standing as a receiver — binds
                // in its own type's scope, and this field is private, so the
                // sibling's `_health` is its own. Two components in one file each
                // carrying a `_health` is ordinary, and reading every such name as
                // a foreign reference refused the SECOND conversion of every such
                // pair: measured, on the batch's own fixture and on two sequential
                // single-type runs alike, where the first conversion had already
                // been written and only the second was refused.
                //
                // ⚠️ Never when the identifier is the NAME of a member access.
                // `outer.inner._health` does reach a private across a nesting
                // boundary — the access this guard was written for — and what the
                // enclosing type declares says nothing about what that expression
                // binds to.
                var enclosing = EnclosingTypeOf(identifier);
                bool bindsToItsOwnTypesMember =
                    enclosing != null
                    && !ReferenceEquals(enclosing, target)
                    && NameBinding.DescribeOnType(enclosing, member) != null
                    && !(identifier.Parent is MemberAccessExpressionSyntax access
                        && access.Name == identifier);
                if (bindsToItsOwnTypesMember)
                {
                    continue;
                }

                return "'" + member + "' is referenced outside the declaring type — the rewrite cannot prove the binding";
            }

            return null;
        }

        // ── Reference collection & rewrite ──────────────────────────────────────

        // Yields the expression node to wrap in `.Value` for each true reference:
        // a bare identifier, or the whole `this.member` access. Any receiver other
        // than `this` — another instance, a conditional access — is ambiguous to
        // syntax and reported through <paramref name="refuseReason"/>.
        /// <summary>
        /// <paramref name="expression"/> with every parenthesis wrapped around it,
        /// which is the same expression to the compiler.
        /// </summary>
        /// <remarks>
        /// 🔑 A rule that reads <c>Parent</c> reads a <c>ParenthesizedExpression</c>
        /// and concludes nothing. This repository already knew that — the
        /// conversion analyzer's receiver walk strips parentheses, and its comment
        /// records the two spellings that beat that rule before it — and this
        /// transform's guard was written without reusing the lesson.
        /// </remarks>
        private static ExpressionSyntax ThroughParentheses(ExpressionSyntax expression)
        {
            while (expression.Parent is ParenthesizedExpressionSyntax parenthesised)
            {
                expression = parenthesised;
            }

            return expression;
        }

        /// <summary>Whether <paramref name="expression"/> stands where a value is written.</summary>
        private static bool IsWriteTarget(ExpressionSyntax expression)
        {
            var node = ThroughParentheses(expression);
            return (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
                || node.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                || IsDeconstructionTarget(node);
        }

        /// <summary>
        /// Whether <paramref name="expression"/> is one element of a tuple being
        /// deconstructed into — <c>(a.x, a.y) = (1, 2)</c>.
        /// </summary>
        /// <remarks>
        /// ⛔ Its parent is the tuple, not the assignment, so every rule that asks
        /// "is my parent an assignment whose left is me" answers no. Nested tuples
        /// are climbed, because the shape is legal at any depth.
        /// </remarks>
        private static bool IsDeconstructionTarget(ExpressionSyntax expression)
        {
            SyntaxNode node = expression;
            while (node.Parent is ArgumentSyntax argument && argument.Parent is TupleExpressionSyntax tuple)
            {
                node = tuple;
            }

            return !ReferenceEquals(node, expression)
                && node.Parent is AssignmentExpressionSyntax assignment
                && assignment.Left == node;
        }

        private static IEnumerable<ExpressionSyntax> ReferencesIn(
            ClassDeclarationSyntax target, string member, out string refuseReason)
        {
            var references = new List<ExpressionSyntax>();
            refuseReason = null;

            foreach (var identifier in target.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.ValueText != member || IsInsideNameOf(identifier))
                {
                    continue;
                }

                // Inside a NESTED type a bare identifier binds to that type's own
                // scope (C# has no implicit outer-instance), so it is not a
                // reference to the converted field — but a receiver-form access
                // there could still reach it, which syntax cannot prove.
                if (!ReferenceEquals(EnclosingTypeOf(identifier), target))
                {
                    if (identifier.Parent is MemberAccessExpressionSyntax nestedAccess
                        && nestedAccess.Name == identifier)
                    {
                        refuseReason = "'" + member + "' is accessed inside a nested type through a receiver"
                            + " — binding is ambiguous without a semantic model";
                        return references;
                    }

                    continue;
                }

                switch (identifier.Parent)
                {
                    case NameColonSyntax patternName when patternName.Parent is SubpatternSyntax:
                        // A property pattern's `name:` binds this member and matches
                        // against its declared type, so retyping the field changes what
                        // the pattern tests. Re-pointing the subpattern at the wrapper's
                        // value would also introduce a null test the source never wrote,
                        // which is a semantic choice syntax cannot make on the author's
                        // behalf.
                        refuseReason = "'" + member + "' is matched by a property pattern"
                            + " — the pattern binds the field itself, not its value";
                        return references;
                    case NameColonSyntax: // a named ARGUMENT names a parameter, never this field
                    case NameEqualsSyntax: // an anonymous-member / attribute-argument name
                    case GotoStatementSyntax: // a label shares the spelling, not the symbol
                        continue;
                    case AnonymousObjectMemberDeclaratorSyntax:
                        // `new { _score }` both names the member and reads the field;
                        // rewriting would rename the projected member.
                        refuseReason = "'" + member + "' is projected into an anonymous object"
                            + " — the rewrite would change the projected member's name";
                        return references;
                    case AssignmentExpressionSyntax assignment
                        when assignment.Left == identifier
                            && assignment.Parent is InitializerExpressionSyntax initializer
                            && initializer.IsKind(SyntaxKind.ObjectInitializerExpression):
                        // `new T { member = … }` assigns the CREATED instance's
                        // member — another object even when T is this very type.
                        refuseReason = "'" + member + "' is assigned inside an object initializer"
                            + " — it binds to the created instance, not this field";
                        return references;
                    case MemberAccessExpressionSyntax access when access.Name == identifier:
                        if (access.Expression is ThisExpressionSyntax)
                        {
                            references.Add(access);
                        }
                        else
                        {
                            refuseReason = "'" + member + "' is accessed through a receiver other than 'this'"
                                + " — binding is ambiguous without a semantic model";
                            return references;
                        }

                        break;
                    case MemberBindingExpressionSyntax:
                        refuseReason = "'" + member + "' is accessed through a conditional receiver"
                            + " — binding is ambiguous without a semantic model";
                        return references;
                    case MemberAccessExpressionSyntax:
                        break; // the identifier is the receiver `member.Something` — still a reference
                    default:
                        break;
                }

                if (identifier.Parent is MemberAccessExpressionSyntax parentAccess && parentAccess.Name == identifier)
                {
                    continue; // already added the whole access above
                }

                references.Add(identifier);
            }

            return references;
        }

        private static TypeDeclarationSyntax EnclosingTypeOf(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is TypeDeclarationSyntax type)
                {
                    return type;
                }
            }

            return null;
        }

        private static ClassDeclarationSyntax RewriteReferences(
            ClassDeclarationSyntax target, IEnumerable<string> inPlaceMembers)
        {
            var sites = new List<ExpressionSyntax>();
            foreach (string member in inPlaceMembers)
            {
                sites.AddRange(ReferencesIn(target, member, out _)); // guards already ran
            }

            if (sites.Count == 0)
            {
                return target;
            }

            // One batch replacement over an iteratively collected set — never a
            // recursive rewriter, which pathological nesting can overflow.
            return target.ReplaceNodes(
                sites,
                (original, _) => SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        original.WithoutTrivia(),
                        SyntaxFactory.IdentifierName(ValuePropertyName))
                    .WithTriviaFrom(original));
        }

        // ── Member edits: retype/split, companion insert, spawn-hook edit ───────

        private static ClassDeclarationSyntax EditMembers(
            ClassDeclarationSyntax target,
            List<(PlannedConversion Plan, FieldDeclarationSyntax Field, VariableDeclaratorSyntax Declarator)> pending,
            SyntaxTrivia newLine,
            bool chainsInheritedHook,
            out string refusalReason)
        {
            refusalReason = null;
            var constructions = new List<StatementSyntax>();
            var current = target;

            foreach (var (conversion, _, _) in pending)
            {
                // Nodes were rebuilt by the reference rewrite; locate by name.
                var (field, declarator) = FindField(current, conversion.MemberName);
                var nvType = SyntaxFactory.IdentifierName(conversion.NetworkVariableTypeName);

                if (conversion.Arm == ConversionArm.InPlace)
                {
                    current = RetypeField(current, field, declarator, nvType, newLine);
                    constructions.Add(Construction(
                        declarator.Identifier, nvType,
                        InitialValueFor(declarator, field)));
                }
                else
                {
                    var companionToken = SyntaxFactory.Identifier(conversion.CompanionFieldName);
                    current = InsertCompanion(current, field, companionToken, nvType, newLine);
                    constructions.Add(Construction(
                        companionToken, nvType,
                        SyntaxFactory.IdentifierName(declarator.Identifier.WithoutTrivia())));
                }
            }

            return AddToSpawnHook(
                current, constructions, newLine, chainsInheritedHook, ref refusalReason);
        }

        private static ClassDeclarationSyntax RetypeField(
            ClassDeclarationSyntax target, FieldDeclarationSyntax field,
            VariableDeclaratorSyntax declarator, IdentifierNameSyntax nvType, SyntaxTrivia newLine)
        {
            var retypedDeclarator = SyntaxFactory.VariableDeclarator(declarator.Identifier.WithoutTrivia());
            var retyped = field.WithDeclaration(
                SyntaxFactory.VariableDeclaration(
                    nvType.WithTriviaFrom(field.Declaration.Type),
                    SyntaxFactory.SingletonSeparatedList(retypedDeclarator)));

            if (field.Declaration.Variables.Count == 1)
            {
                return target.ReplaceNode(field, retyped);
            }

            // Split a declarator list: the converted member gets its own
            // declaration; the siblings keep the original type and initializers.
            var siblingVariables = field.Declaration.Variables.Where(v => v != declarator).ToList();
            var siblingSeparators = Enumerable.Repeat(
                SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
                siblingVariables.Count - 1);
            var siblings = field.WithDeclaration(
                    field.Declaration.WithVariables(
                        SyntaxFactory.SeparatedList(siblingVariables, siblingSeparators)))
                .WithLeadingTrivia(IndentOf(field))
                .WithTrailingTrivia(field.GetTrailingTrivia());
            retyped = retyped.WithTrailingTrivia(newLine);

            return target.ReplaceNode(field, new SyntaxNode[] { retyped, siblings });
        }

        private static ClassDeclarationSyntax InsertCompanion(
            ClassDeclarationSyntax target, FieldDeclarationSyntax configField,
            SyntaxToken companionName, IdentifierNameSyntax nvType, SyntaxTrivia newLine)
        {
            var companion = SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(
                        ((IdentifierNameSyntax)nvType.WithoutTrivia()).WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(companionName))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                .WithLeadingTrivia(IndentOf(configField))
                .WithTrailingTrivia(newLine);

            return target.InsertNodesAfter(configField, new[] { companion });
        }

        private static ClassDeclarationSyntax AddToSpawnHook(
            ClassDeclarationSyntax target, List<StatementSyntax> constructions, SyntaxTrivia newLine,
            bool chainsInheritedHook, ref string refusalReason)
        {
            var hook = target.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == SpawnHookName && m.ParameterList.Parameters.Count == 0);

            if (hook is null)
            {
                return AppendSpawnHook(target, constructions, newLine, chainsInheritedHook);
            }

            // A same-named method that is not the override never runs at spawn
            // (a hide) or cannot reference `this` (static) — injecting there
            // would leave the variables forever null or break the build.
            if (hook.Modifiers.Any(SyntaxKind.StaticKeyword)
                || !hook.Modifiers.Any(SyntaxKind.OverrideKeyword))
            {
                refusalReason = "an OnNetworkSpawn method exists but does not override the runtime hook"
                    + " — constructions injected there would never run at spawn";
                return target;
            }

            if (hook.Body is null)
            {
                refusalReason = "OnNetworkSpawn is expression-bodied — there is no block to construct the variables in";
                return target;
            }

            if (!BlockEditing.IsMultiLineBody(hook.Body))
            {
                refusalReason = "OnNetworkSpawn has a single-line body — extending it would glue statements onto one line";
                return target;
            }

            // An existing hook inside a conditional region compiles away in the
            // excluded configuration, taking the constructions injected into it
            // with it while the fields remain — the same null-at-spawn outcome the
            // appended hook is placed to avoid, reached through a method the
            // transform does not get to position.
            if (target.Members.IndexOf(hook) >= BlockEditing.FirstConditionalIndex(target.Members))
            {
                refusalReason = "OnNetworkSpawn is enclosed by a conditional compilation region"
                    + " — constructions injected there would not run in every build configuration";
                return target;
            }

            var indent = BlockEditing.Indent(hook.Body);
            var indented = constructions
                .Select(s => s.WithLeadingTrivia(indent).WithTrailingTrivia(newLine))
                .ToList();
            var body = hook.Body.WithStatements(hook.Body.Statements.InsertRange(0, indented));
            return target.ReplaceNode(hook, hook.WithBody(body));
        }

        private static ClassDeclarationSyntax AppendSpawnHook(
            ClassDeclarationSyntax target, List<StatementSyntax> constructions, SyntaxTrivia newLine,
            bool chainsInheritedHook)
        {
            string memberIndent = target.Members.Count > 0
                ? IndentText(target.Members.Last())
                : IndentText(target) + BlockEditing.IndentStep(IndentText(target));
            string statementIndent = memberIndent + BlockEditing.IndentStep(memberIndent);

            // The inherited hook runs first.  A converted type may sit on another
            // NetworkBehaviour that constructs variables of its own at spawn, and an
            // override that does not chain leaves every one of them null for the whole
            // life of the instance: nothing registers them, nothing replicates them,
            // and the first read in the base class dereferences null on the frame the
            // object spawns — in a build that compiled clean.
            //
            // ⛔ Omitted where the nearest declaration this file can see is abstract.
            // There is no body to run, and `base.OnNetworkSpawn()` against an abstract
            // member is CS0205: the chain was broken by whichever ancestor declared it
            // that way, and no type below has a legal way to restore it.  The runtime's
            // own hook is an empty virtual, so a type sitting directly on it chains to
            // a body that does nothing and costs nothing.
            var statements = new List<StatementSyntax>();

            if (chainsInheritedHook)
            {
                statements.Add(
                    SyntaxFactory
                        .ExpressionStatement(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.BaseExpression(),
                                    SyntaxFactory.IdentifierName(SpawnHookName))))
                        .WithLeadingTrivia(SyntaxFactory.Whitespace(statementIndent))
                        .WithTrailingTrivia(newLine));
            }

            statements.AddRange(constructions
                .Select(s => s
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(statementIndent))
                    .WithTrailingTrivia(newLine)));

            var body = SyntaxFactory.Block(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(memberIndent))
                    .WithTrailingTrivia(newLine),
                SyntaxFactory.List(statements),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(memberIndent))
                    .WithTrailingTrivia(newLine));

            // Protected override, separated from the member above it by one blank
            // line.  The accessibility is not a style choice: the runtime hook is
            // protected, so a public override is CS0507 against the real SDK.
            var method = SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Identifier(SpawnHookName))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                .WithParameterList(SyntaxFactory.ParameterList().WithTrailingTrivia(newLine))
                .WithBody(body)
                .WithLeadingTrivia(newLine, SyntaxFactory.Whitespace(memberIndent));

            // The hook is the only place the converted fields are constructed, so
            // it has to run in every configuration those fields exist in.
            // Appending would place it after the last member — inside a trailing
            // conditional region, when the class has one — leaving the fields
            // declared but never constructed in the excluded configuration: code
            // that compiles clean and dereferences null at spawn. Ahead of the
            // first conditional member the hook is unconditional, and the fields
            // are known unconditional too because Refuse rejects any that are not.
            return target.WithMembers(
                target.Members.Insert(BlockEditing.FirstConditionalIndex(target.Members), method));
        }

        // ── Generated construction: typed factories over original tokens only ───

        /// <summary>
        /// <c>nameof(<paramref name="member"/>)</c>.
        /// </summary>
        /// <remarks>
        /// Built as an invocation of the identifier <c>nameof</c>, which is how
        /// C# spells the operator — it is contextual, not a keyword, so there is
        /// no dedicated syntax kind to construct and a parser reading this back
        /// sees exactly what an author would have typed.
        /// </remarks>
        private static ExpressionSyntax NameOf(SyntaxToken member)
            => SyntaxFactory.InvocationExpression(
                SyntaxFactory.IdentifierName("nameof"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.IdentifierName(member.WithoutTrivia())))));

        private static StatementSyntax Construction(
            SyntaxToken fieldName, IdentifierNameSyntax nvType, ExpressionSyntax initialValue)
        {
            // The member's own name, as `nameof(<field>)` rather than a string
            // literal: the wire identity is derived from it, so it has to be a
            // name the compiler resolves — a literal would let a typo compile
            // and ship as a variable that replicates with nothing.
            //
            // Positional, where `initialValue` is named: `nameof(_hp)` already
            // says what it is, and a label in front of it reads as noise.
            var arguments = new List<ArgumentSyntax>
            {
                SyntaxFactory.Argument(SyntaxFactory.ThisExpression()),
                SyntaxFactory.Argument(NameOf(fieldName)),
            };

            if (initialValue != null)
            {
                arguments.Add(NamedArgument("initialValue", initialValue.WithoutTrivia()));
            }

            var creation = SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                nvType.WithoutTrivia(),
                SyntaxFactory.ArgumentList(SeparateWithCommaSpace(arguments)),
                initializer: null);

            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(fieldName.WithoutTrivia()).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.EqualsToken).WithTrailingTrivia(SyntaxFactory.Space),
                    creation));
        }

        private static ArgumentSyntax NamedArgument(string name, ExpressionSyntax value)
            => SyntaxFactory.Argument(
                SyntaxFactory.NameColon(
                    SyntaxFactory.IdentifierName(name),
                    SyntaxFactory.Token(SyntaxKind.ColonToken).WithTrailingTrivia(SyntaxFactory.Space)),
                default,
                value);

        private static SeparatedSyntaxList<ArgumentSyntax> SeparateWithCommaSpace(List<ArgumentSyntax> arguments)
        {
            var separators = Enumerable.Repeat(
                SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
                arguments.Count - 1);
            return SyntaxFactory.SeparatedList(arguments, separators);
        }

        // The initial value the construction carries: the original initializer
        // node relocated verbatim; the zero quaternion normalised to identity —
        // a disclosed value change, because the receive path rejects the zero
        // rotation anyway; absent → the ctor's own default, except Quaternion,
        // which must always seed identity to keep RTMPE1012 silent.
        private static ExpressionSyntax InitialValueFor(
            VariableDeclaratorSyntax declarator, FieldDeclarationSyntax field)
        {
            var initializer = declarator.Initializer?.Value;
            bool isQuaternion = RightmostName(field.Declaration.Type) == "Quaternion";

            if (initializer is null)
            {
                return isQuaternion ? IdentityFor(field) : null;
            }

            if (isQuaternion && (initializer.IsKind(SyntaxKind.DefaultLiteralExpression)
                || initializer is DefaultExpressionSyntax
                || IsZeroQuaternion(initializer)))
            {
                return IdentityFor(field);
            }

            return initializer;
        }

        // `identity` qualified exactly as the field's own type is spelled, so the
        // emission resolves wherever the original declaration did.
        private static ExpressionSyntax IdentityFor(FieldDeclarationSyntax field)
            => SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)ExpressionFromType(field.Declaration.Type.WithoutTrivia()),
                SyntaxFactory.IdentifierName("identity"));

        private static ExpressionSyntax ExpressionFromType(TypeSyntax type)
            => type is QualifiedNameSyntax qualified
                ? SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    ExpressionFromType(qualified.Left),
                    qualified.Right)
                : SyntaxFactory.IdentifierName(RightmostName(type));

        private static bool IsZeroQuaternion(ExpressionSyntax expression)
            => expression is ObjectCreationExpressionSyntax creation
                && RightmostName(creation.Type) == "Quaternion"
                && creation.ArgumentList != null
                && creation.ArgumentList.Arguments.Count == 4
                && creation.ArgumentList.Arguments.All(
                    a => a.Expression is LiteralExpressionSyntax literal
                        && literal.Token.ValueText.TrimEnd('f', 'F', 'd', 'D') is "0" or "0.0");

        // ── Small shared helpers ────────────────────────────────────────────────

        private static (FieldDeclarationSyntax Field, VariableDeclaratorSyntax Declarator) FindField(
            ClassDeclarationSyntax target, string memberName)
        {
            foreach (var field in target.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var declarator in field.Declaration.Variables)
                {
                    if (declarator.Identifier.ValueText == memberName)
                    {
                        return (field, declarator);
                    }
                }
            }

            return (null, null);
        }

        private static bool IsPrivate(FieldDeclarationSyntax field)
            => !field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)
                || m.IsKind(SyntaxKind.ProtectedKeyword)
                || m.IsKind(SyntaxKind.InternalKeyword));

        private static bool IsUnitySerializedSyntactically(FieldDeclarationSyntax field)
        {
            if (!IsPrivate(field))
            {
                return true;
            }

            bool serializeField = field.AttributeLists.Any(list => list.Attributes.Any(
                a => RightmostName(a.Name) is "SerializeField" or "SerializeFieldAttribute"));
            bool nonSerialized = field.AttributeLists.Any(list => list.Attributes.Any(
                a => RightmostName(a.Name) is "NonSerialized" or "NonSerializedAttribute"));
            return serializeField && !nonSerialized;
        }

        // Every declaration the minted companion would collide with — including the
        // enclosing type's own name, which a member may not repeat (CS0542), and
        // the member forms a type-member scan is easy to stop short of.
        private static bool MemberNameExists(ClassDeclarationSyntax target, string name)
            => NameBinding.DescribeOnType(target, name) != null;

        // Only a conditional boundary makes the rewrite ambiguous; #region and
        // #pragma leave the compiled element set untouched and stay editable.
        private static bool HasDirectiveTrivia(SyntaxNode node)
            => BlockEditing.ContainsConditionalDirectives(node);

        private static bool IsInsideNameOf(SyntaxNode node)
            => node.Ancestors().OfType<InvocationExpressionSyntax>().Any(
                invocation => invocation.Expression is IdentifierNameSyntax name
                    && name.Identifier.ValueText == "nameof");

        /// <summary>
        /// The members a pre-spawn hook can reach, mapped to the hook that
        /// reaches them.
        /// </summary>
        /// <remarks>
        /// 🔑 A hook rarely holds the work itself. `void Start() { ResetBoard(); }`
        /// puts every field the helper touches on the pre-spawn path, and a rule
        /// that asks only whether a reference sits LEXICALLY inside a hook
        /// answers no — which is a rewrite into a null dereference, reached on
        /// the first frame, on a build that compiled.
        /// <para>
        /// ⚠️ Reached by NAME, and any mention counts: an invocation, a method
        /// group handed to a subscriber, a property read. This transform binds
        /// nothing through a semantic model and refuses wherever a name is
        /// ambiguous, so the same discipline holds here — the closure
        /// over-approximates, and over-approximating a REFUSAL costs a
        /// conversion the author can restructure, while under-approximating it
        /// costs a crash they cannot see coming.
        /// </para>
        /// ⛔ `nameof` is excluded where it merely names a member — but NOT where
        /// Unity dispatches by name. `Invoke(nameof(Configure), 0f)` and
        /// `StartCoroutine(nameof(Setup))` are calls, and the modern spelling of
        /// them at that; excluding those read the recommended form of a
        /// pre-spawn call as an inert string. The older string spellings draw no
        /// name node at all, so their first argument is read as one.
        /// </remarks>
        // The key an indexer is declared under. An indexer has no identifier, and
        // the brackets cannot spell a member name, so the two cannot collide.
        private const string IndexerKey = "this[]";

        // The declarations this file can see for the type: the type itself, and
        // every base of it declared in the SAME file, transitively.
        //
        // ⛔ A base in another file is invisible, and this does not refuse over
        // it — the sibling transform states the same policy for the same reason
        // (`ATypeDerivingFromAnIntermediateBase_IsStillEdited`): a single-file
        // syntactic host that refused every type whose base list is not literally
        // `NetworkBehaviour` would refuse most real projects. What it costs is
        // written down rather than guessed at.
        /// <summary>
        /// Whether an override on <paramref name="target"/> may call
        /// <c>base.OnNetworkSpawn()</c>.
        /// </summary>
        /// <remarks>
        /// Answers from the nearest declaration this file carries, and from that one
        /// alone. An abstract or static one is where the chain ends: the call binds to
        /// the nearest declaration whatever its shape, and looking past it to a usable
        /// ancestor would emit a call the compiler then rejects. A declaration the
        /// derived type cannot reach ends it for a different reason — the runtime
        /// dispatches the hook virtually, so a member it cannot bind to is not the
        /// lifecycle hook and carries nothing a chain could run.
        /// <para>
        /// A base the file does not carry is answered <c>true</c>, which is the
        /// reading that is right almost always and loud when it is wrong: the runtime
        /// hook is a concrete virtual and every type between it and here inherits a
        /// body unless one of them deliberately abstracts it, and an ancestor that did
        /// so in another file leaves CS0205 at the call site rather than a null
        /// reference at the first frame.
        /// </para>
        /// </remarks>
        private static bool InheritsAChainableSpawnHook(ClassDeclarationSyntax target)
        {
            foreach (var declaration in VisibleDeclarationsOf(target))
            {
                if (declaration == target)
                {
                    continue;
                }

                var nearest = declaration.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.ValueText == SpawnHookName
                                         && m.ParameterList.Parameters.Count == 0);

                if (nearest is null)
                {
                    continue;
                }

                // ⚠️ Accessibility is read as the ABSENCE of a widening modifier, not
                // as the presence of `private`. A class member with no modifier at all
                // is private in C#, so a rule that looked for the keyword would read
                // `void OnNetworkSpawn()` as reachable and chain to a member no
                // derived type can name.
                bool reachable = nearest.Modifiers.Any(SyntaxKind.PublicKeyword)
                    || nearest.Modifiers.Any(SyntaxKind.ProtectedKeyword)
                    || nearest.Modifiers.Any(SyntaxKind.InternalKeyword);

                return reachable
                    && !nearest.Modifiers.Any(SyntaxKind.AbstractKeyword)
                    && !nearest.Modifiers.Any(SyntaxKind.StaticKeyword);
            }

            return true;
        }

        /// <summary>
        /// <paramref name="target"/> followed by every class it derives from
        /// that this file declares, nearest first.
        /// </summary>
        /// <remarks>
        /// One syntax tree is the whole of what can be resolved without a
        /// compilation, so a base declared elsewhere is absent rather than
        /// unreachable — every caller has to be sound when the chain stops
        /// early.
        /// </remarks>
        public static List<ClassDeclarationSyntax> VisibleDeclarationsOf(ClassDeclarationSyntax target)
        {
            var chain = new List<ClassDeclarationSyntax> { target };
            var visited = new HashSet<ClassDeclarationSyntax> { target };

            var inFile = target.SyntaxTree.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .ToList();

            for (int i = 0; i < chain.Count; i++)
            {
                foreach (var baseType in chain[i].BaseList?.Types ?? default)
                {
                    string name = RightmostName(baseType.Type);
                    var declaration = inFile.FirstOrDefault(c => c.Identifier.ValueText == name);
                    if (declaration != null && visited.Add(declaration))
                    {
                        chain.Add(declaration);
                    }
                }
            }

            return chain;
        }

        private static Dictionary<string, string> PreSpawnReachableMembers(ClassDeclarationSyntax target)
        {
            var bodies = new Dictionary<string, List<SyntaxNode>>(System.StringComparer.Ordinal);
            var constructorKeys = new List<string>();

            void Declare(string name, SyntaxNode body)
            {
                if (name == null || body == null)
                {
                    return;
                }

                if (!bodies.TryGetValue(name, out var declared))
                {
                    bodies[name] = declared = new List<SyntaxNode>();
                }

                declared.Add(body);
            }

            // 🔑 The type AND every base of it this file declares. A base whose
            // `Awake` calls a virtual the leaf overrides puts the override on the
            // pre-spawn path, and reading only the leaf's own members answered
            // that the override is reached by nothing — the template-method shape
            // is how shared behaviour is written in Unity.
            foreach (var declaration in VisibleDeclarationsOf(target))
            {
                // Constructors go under the one key C# guarantees cannot collide
                // with a member: a member may not be named for its enclosing type
                // (CS0542).
                string constructorKey = declaration.Identifier.ValueText;
                constructorKeys.Add(constructorKey);

                foreach (var member in declaration.Members)
                {
                    switch (member)
                    {
                        case MethodDeclarationSyntax method:
                            Declare(method.Identifier.ValueText, (SyntaxNode)method.Body ?? method.ExpressionBody);
                            break;
                        case PropertyDeclarationSyntax property:
                            Declare(property.Identifier.ValueText, (SyntaxNode)property.AccessorList ?? property.ExpressionBody);
                            break;
                        case EventDeclarationSyntax declared:
                            Declare(declared.Identifier.ValueText, declared.AccessorList);
                            break;
                        case IndexerDeclarationSyntax indexer:
                            Declare(IndexerKey, (SyntaxNode)indexer.AccessorList ?? indexer.ExpressionBody);
                            break;
                        case ConstructorDeclarationSyntax constructor:
                            Declare(constructorKey, (SyntaxNode)constructor.Body ?? constructor.ExpressionBody);
                            break;
                    }
                }
            }

            var reachedFrom = new Dictionary<string, string>(System.StringComparer.Ordinal);
            var pending = new Queue<string>();

            // ⚠️ A constructor is a root, not only a context. The refusal already
            // names one as pre-spawn where the reference sits directly inside it;
            // without seeding from it, a helper the constructor is the only
            // caller of was converted into exactly the dereference that refusal
            // exists to prevent.
            foreach (string hook in PreSpawnHookNames.Concat(constructorKeys))
            {
                if (bodies.ContainsKey(hook))
                {
                    reachedFrom[hook] = hook;
                    pending.Enqueue(hook);
                }
            }

            while (pending.Count > 0)
            {
                string current = pending.Dequeue();
                string origin = reachedFrom[current];

                foreach (var body in bodies[current])
                {
                    // ⚠️ SimpleName, not Identifier: `Bind<T>()` parses as a
                    // GenericName, whose only descendants are its type arguments,
                    // so a scan of identifiers alone drew no edge from any generic
                    // helper.
                    foreach (var mention in body.DescendantNodes().OfType<SimpleNameSyntax>())
                    {
                        Reach(mention.Identifier.ValueText,
                            IsInertNameOf(mention) || !StandsForAMemberOfThisType(mention));
                    }

                    // The by-name dispatchers, whose argument IS the call.
                    foreach (var dispatched in DispatchedMemberNames(body))
                    {
                        Reach(dispatched, inert: false);
                    }

                    // An indexer is reached through brackets, which carry no name
                    // for the scan above to see. ⚠️ Through the same receiver
                    // capability, and for the same reason: `base[0]` reaching an
                    // overriding indexer drew no edge while `this[0]` did.
                    foreach (var element in body.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
                    {
                        if (DenotesThisInstance(element.Expression))
                        {
                            Reach(IndexerKey, inert: false);
                        }
                    }

                    // `this?[0]` — the conditional form carries its receiver
                    // above the brackets, exactly as `?.` does above the name.
                    foreach (var element in body.DescendantNodes().OfType<ElementBindingExpressionSyntax>())
                    {
                        if (DenotesThisInstance(ConditionalReceiverOf(element)))
                        {
                            Reach(IndexerKey, inert: false);
                        }
                    }
                }

                void Reach(string name, bool inert)
                {
                    if (inert || !bodies.ContainsKey(name) || reachedFrom.ContainsKey(name))
                    {
                        return;
                    }

                    reachedFrom[name] = origin;
                    pending.Enqueue(name);
                }
            }

            return reachedFrom;
        }

        /// <summary>
        /// Whether a name in a body could stand for a member of the type being
        /// converted, rather than for something that merely spells the same.
        /// </summary>
        /// <remarks>
        /// ⚠️ The closure over-approximates on purpose, and that is not licence
        /// for the REPORT to. Three positions can never bind to this type's
        /// member, and each produced a refusal naming a call the author cannot
        /// find: the name after a dot belongs to the receiver
        /// (<c>_audio.Play()</c> refused the type's own <c>Play</c>), a named
        /// argument's label belongs to the callee's parameter, and an object
        /// initializer's left side belongs to the type being initialised. The
        /// file already discriminates by role this way where it reads references
        /// to the member itself.
        /// <para>
        /// 🚨 "The name after a dot belongs to the receiver" is true of every
        /// receiver but the ones that ARE this instance, and there are more of
        /// those than the two keywords. <c>this.Configure()</c>,
        /// <c>(this).Configure()</c>, <c>((IBootable)this).Configure()</c> — the
        /// only legal spelling for an explicit interface implementation — and
        /// <c>this!.Configure()</c> all name this type's member exactly as the
        /// bare call does, and reading them as somebody else's drew no edge out
        /// of the hook at all. Measured: five spellings, each converted into the
        /// dereference of null this refusal exists to prevent, against a bare
        /// call that was refused. So the receiver is asked as a capability, by
        /// <see cref="DenotesThisInstance"/>, and never by its spelling.
        /// </para>
        /// <para>
        /// ⛔ A local or parameter that shadows a member name is NOT excluded: a
        /// bare identifier is one shape to a pass that binds nothing, and the
        /// safe direction there is the refusal.
        /// </para>
        /// </remarks>
        private static bool StandsForAMemberOfThisType(SimpleNameSyntax mention)
        {
            switch (mention.Parent)
            {
                case MemberAccessExpressionSyntax access when access.Name == mention:
                    return DenotesThisInstance(access.Expression);
                case MemberBindingExpressionSyntax binding:
                    // `x?.Name` — the receiver is the conditional access's own
                    // left side, which sits above this node rather than beside
                    // it. Reading it settles a false REFUSAL as well as a false
                    // admission: every `?.` used to draw an edge, so a serialized
                    // `_animator?.Play()` on a type that also declares `Play`
                    // refused a conversion that was correct.
                    return DenotesThisInstance(ConditionalReceiverOf(binding));
                case NameColonSyntax:
                    return false;
                case AssignmentExpressionSyntax assignment
                    when assignment.Left == mention
                        && assignment.Parent is InitializerExpressionSyntax:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether this expression denotes the instance being converted.
        /// </summary>
        /// <remarks>
        /// ⛔ Wrappers that change the static type or the nullability annotation
        /// and nothing else are transparent here: a parenthesis, a cast, a
        /// null-suppression. What is NOT transparent is an alias — `var self =
        /// this; self.Configure();` — because reading a bare identifier as this
        /// instance is exactly the over-refusal the enclosing rule exists to
        /// prevent (`_audio.Configure()` naming the type's own `Configure`).
        /// That one is a limit of a pass that binds nothing, stated rather than
        /// guessed at.
        ///
        /// ⚠️ A receiver this cannot see at all answers TRUE. The closure
        /// over-approximates by design, and an unknown receiver is the one place
        /// where guessing wrong costs a null dereference in the customer's build
        /// rather than a declined conversion.
        /// <para>
        /// ⛔ That arm is unreachable from the two call sites, measured rather
        /// than assumed: a member or element binding in a parsed tree always
        /// carries a conditional access above it, because the malformed
        /// spellings that would not — a bare <c>.Foo()</c>, a leading
        /// <c>?.Foo()</c> — parse to other node kinds entirely. It is kept
        /// because the alternative is a silent fall to "somebody else's", and
        /// it is exercised directly rather than left as a hole nothing can
        /// reach.
        /// </para>
        /// </remarks>
        private static bool DenotesThisInstance(ExpressionSyntax expression)
            // ⛔ `null` — no receiver at all — is read as this instance, because
            // the alternative is a silent fall to "somebody else's". The wrappers
            // a receiver may wear are the shared rule's, not a copy of it: three
            // copies of this list disagreed about `(this as IBoot)`.
            => expression is null || RTMPE.SDK.Analyzers.ThisInstance.Denotes(expression);

        // The left side of the `?.` a binding belongs to. Null when there is no
        // enclosing conditional access, which DenotesThisInstance reads as
        // unknown and therefore as this instance.
        private static ExpressionSyntax ConditionalReceiverOf(SyntaxNode binding)
            => binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                .FirstOrDefault()?.Expression;

        // Unity's by-name dispatchers. Their first argument names a method the
        // engine will call, so it is a call site written as data — and the
        // `nameof` spelling is the one the manual recommends.
        private static readonly string[] ByNameDispatchers =
        {
            "Invoke", "InvokeRepeating", "StartCoroutine", "StopCoroutine",
            "SendMessage", "SendMessageUpwards", "BroadcastMessage",
        };

        /// <summary>
        /// Whether a <c>nameof</c> operand is inert here — naming a member
        /// without running it. It is not inert when the <c>nameof</c> is the
        /// argument of a by-name dispatcher, which is a call.
        /// </summary>
        private static bool IsInertNameOf(SyntaxNode mention)
            => IsInsideNameOf(mention) && !IsDispatchedByName(mention);

        // ⚠️ `nameof(X)` is itself an invocation, so the first argument list above
        // the mention is the nameof's own. What decides this is where THAT
        // expression sits: as the dispatcher's first argument it is a call
        // written as data; anywhere else in the same call it is a string.
        private static bool IsDispatchedByName(SyntaxNode mention)
        {
            var nameOf = mention.Ancestors().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(invocation => invocation.Expression is IdentifierNameSyntax name
                    && name.Identifier.ValueText == "nameof");

            return nameOf?.Parent is ArgumentSyntax argument
                && argument.Parent is ArgumentListSyntax arguments
                && arguments.Parent is InvocationExpressionSyntax call
                && NamesADispatcher(call)
                && arguments.Arguments.FirstOrDefault() == argument;
        }

        // The member names handed to a by-name dispatcher as string literals —
        // `Invoke("Configure", 0f)`, which draws no name node at all and so
        // reaches the scan through nothing else.
        private static IEnumerable<string> DispatchedMemberNames(SyntaxNode body)
        {
            foreach (var call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!NamesADispatcher(call))
                {
                    continue;
                }

                var first = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                if (first is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    yield return literal.Token.ValueText;
                }
            }
        }

        private static bool NamesADispatcher(InvocationExpressionSyntax call)
        {
            string name = call.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => null,
            };

            return name != null && ByNameDispatchers.Contains(name);
        }

        private static string PreSpawnContextOf(
            SyntaxNode reference, IReadOnlyDictionary<string, string> reachable)
        {
            foreach (var ancestor in reference.Ancestors())
            {
                switch (ancestor)
                {
                    case MethodDeclarationSyntax method
                        when PreSpawnHookNames.Contains(method.Identifier.ValueText):
                        return "'" + method.Identifier.ValueText + "'";
                    case MethodDeclarationSyntax method
                        when reachable.TryGetValue(method.Identifier.ValueText, out string viaMethod):
                        return "'" + method.Identifier.ValueText + "', reached from '" + viaMethod + "'";
                    case PropertyDeclarationSyntax property
                        when reachable.TryGetValue(property.Identifier.ValueText, out string viaProperty):
                        return "'" + property.Identifier.ValueText + "', reached from '" + viaProperty + "'";
                    case EventDeclarationSyntax declared
                        when reachable.TryGetValue(declared.Identifier.ValueText, out string viaEvent):
                        return "'" + declared.Identifier.ValueText + "', reached from '" + viaEvent + "'";
                    case IndexerDeclarationSyntax when reachable.TryGetValue(IndexerKey, out string viaIndexer):
                        return "this type's indexer, reached from '" + viaIndexer + "'";
                    case ConstructorDeclarationSyntax:
                        return "a constructor";
                    case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } }:
                        return "another field's initializer";
                    case ClassDeclarationSyntax:
                        return null;
                }
            }

            return null;
        }

        /// <summary>
        /// A field's declared type as the closed map and a conversion plan spell
        /// it — the rightmost identifier with any generic arity stripped, which
        /// is what an author writes and what the wrapper set is keyed on.
        /// </summary>
        /// <remarks>
        /// 🔴 Public because <c>ConversionCli</c> had a private copy of it, and the
        /// two had drifted: only the CLI's carried the generic arm, so an
        /// already-converted <c>NetworkVariableList&lt;int&gt;</c> reached the map
        /// lookup spelled <c>NetworkVariableList&lt;int&gt;</c>, missed the
        /// already-converted skip above, and drew a refusal telling the author to
        /// hand-write a type the SDK ships. Re-running a conversion must be a
        /// provable no-op, and one spelling in two places is how that stopped
        /// being true.
        /// ⛔ Not <see cref="RightmostName(TypeSyntax)"/>, which answers a
        /// different question — the rightmost identifier as written — and is used
        /// for attribute and base-type names where arity is not being stripped.
        /// </remarks>
        public static string DeclaredTypeName(TypeSyntax type)
            => type switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                QualifiedNameSyntax qualified => DeclaredTypeName(qualified.Right),
                PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
                _ => type?.ToString() ?? string.Empty,
            };

        private static string RightmostName(TypeSyntax type)
            => type switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
                _ => type?.ToString(),
            };

        private static string RightmostName(NameSyntax name)
            => name switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                _ => name?.ToString(),
            };

        private static SyntaxTriviaList IndentOf(SyntaxNode node)
            => SyntaxFactory.TriviaList(SyntaxFactory.Whitespace(IndentText(node)));

        private static string IndentText(SyntaxNode node)
        {
            string indent = string.Empty;
            foreach (var trivia in node.GetLeadingTrivia().Reverse())
            {
                if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    break;
                }

                indent = trivia.ToString() + indent;
            }

            return indent;
        }
    }
}
