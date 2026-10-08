using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// The list arm of the generation transform: a <c>List&lt;T&gt;</c> field
    /// whose element type the closed map carries becomes the synchronised list
    /// that carries it.
    /// </summary>
    /// <remarks>
    /// The scalar arm retypes a field and then reads every reference through
    /// <c>.Value</c>; a list needs the first half and not the second, because
    /// after the conversion the field IS the list and <c>Add</c>, <c>RemoveAt</c>,
    /// the indexer, <c>Count</c> and <c>foreach</c> bind exactly as they did. What
    /// the arm has to refuse instead is every use that binds only to
    /// <c>List&lt;T&gt;</c>: a member the synchronised list does not carry, the
    /// field standing as a value where a <c>List&lt;T&gt;</c> is expected, and an
    /// assignment after the declaration — the construction moves into the spawn
    /// hook, where the scalar's does, and a second assignment would replace the
    /// registered variable with an unregistered list.
    ///
    /// <para>⛔ Constructed in <c>OnNetworkSpawn</c>, like every other variable
    /// this transform emits, and for the same reason: it is the one hook every
    /// object reaches once the runtime knows it. The runtime does admit a list
    /// built before the spawn — its pre-spawn write window is open — but the
    /// only site that could hold such a construction is <c>Awake</c>, and an
    /// <c>Awake</c> this rewrite adds is hidden by any <c>Awake</c> a subclass in
    /// another file declares, on every instance of that subclass, silently. A
    /// refusal the author can restructure costs less than a null dereference they
    /// cannot see coming, so a list populated in a pre-spawn hook is refused with
    /// the remedy that fits a networked list anyway — populate it where the owner
    /// is known.</para>
    /// </remarks>
    public static partial class NetworkVariableGenerationTransform
    {
        private const string ListTypeName = "List";
        private const string OwnerPropertyName = "IsOwner";
        private const string SeedSuffix = "Seed";

        // Every List<T> member the synchronised list does not carry, each with
        // what to write instead. A member outside this table and outside the
        // list's own surface — a LINQ operator, an extension of the author's —
        // is left to the compile gate, which judges the rewrite against the
        // contract and refuses one that no longer builds. ⛔ `Reverse` is in the
        // table for a sharper reason than absence: with System.Linq imported the
        // call still COMPILES, against the lazy operator, and reverses nothing —
        // a rewrite the gate would pass and the game would notice as a level
        // that never mirrored. `ToArray` and `ToList` are not here: LINQ's copy
        // the list, which is what the originals did.
        private static readonly Dictionary<string, string> UnsupportedListMembers =
            new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["AddRange"] = "add the elements one at a time with Add",
                ["InsertRange"] = "insert the elements one at a time with Insert",
                ["RemoveRange"] = "remove the elements one at a time with RemoveAt",
                ["RemoveAll"] = "walk the list from the end and RemoveAt each match",
                ["Sort"] = "order a copy (OrderBy over the list) and write it back with Clear and Add",
                ["Reverse"] = "reverse a copy and write it back with Clear and Add",
                ["Find"] = "use FirstOrDefault — the synchronised list is an IEnumerable<T>",
                ["FindAll"] = "use Where — the synchronised list is an IEnumerable<T>",
                ["FindIndex"] = "walk the list by index, or use IndexOf for a whole element",
                ["FindLast"] = "use LastOrDefault — the synchronised list is an IEnumerable<T>",
                ["FindLastIndex"] = "walk the list by index from the end",
                ["Exists"] = "use Any — the synchronised list is an IEnumerable<T>",
                ["TrueForAll"] = "use All — the synchronised list is an IEnumerable<T>",
                ["ForEach"] = "use a foreach statement",
                ["ConvertAll"] = "use Select — the synchronised list is an IEnumerable<T>",
                ["GetRange"] = "use Skip and Take — the synchronised list is an IEnumerable<T>",
                ["CopyTo"] = "copy with ToArray or ToList first",
                ["BinarySearch"] = "use IndexOf, or search a sorted copy",
                ["LastIndexOf"] = "walk the list by index from the end",
                ["AsReadOnly"] = "expose the synchronised list itself — every client but the owner already reads it read-only",
                ["Capacity"] = "the synchronised list has no capacity; MaxCount is its ceiling",
                ["EnsureCapacity"] = "the synchronised list has no capacity; MaxCount is its ceiling",
                ["TrimExcess"] = "the synchronised list has no capacity to trim",
            };

        // ── Refusals ────────────────────────────────────────────────────────────

        private static string RefuseList(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, PlannedConversion conversion,
            FieldDeclarationSyntax field, VariableDeclaratorSyntax declarator)
        {
            string member = conversion.MemberName;

            if (conversion.Arm == ConversionArm.InPlace)
            {
                string reason = CheckListDeclarationForm(field, member);

                if (reason == null && !IsPrivate(field))
                {
                    reason = "'" + member + "' is not private — references outside this file cannot be rewritten";
                }

                if (reason == null && IsUnitySerializedSyntactically(field))
                {
                    reason = "'" + member + "' is Unity-serialized — retyping it discards Inspector values";
                }

                reason ??= CheckListInitializer(declarator, member);
                if (reason == null && declarator.Initializer?.Value is { } seeded
                    && IsListCreation(seeded, out _, out var seedElements) && seedElements.Count > 0)
                {
                    reason = OwnerGuardShadowedReason(target);
                }

                reason ??= CheckListReferences(root, target, member, conversion.NetworkVariableTypeName, field.Declaration.Type);

                return reason == null ? null : reason + CompanionArmRemedy(member);
            }

            string companion = conversion.CompanionFieldName;
            if (string.IsNullOrEmpty(companion))
            {
                return "'" + member + "' has no companion field name in the plan";
            }

            string guard = OwnerGuardShadowedReason(target);
            if (guard != null)
            {
                return guard;
            }

            if (MemberNameExists(target, companion))
            {
                return "companion name '" + companion + "' already exists on the type";
            }

            // The seed loop declares an iteration variable, and a name already
            // spelled anywhere in the type — a member, a local, a parameter —
            // could be shadowed by it or shadow it, which C# refuses (CS0136) in
            // some of those positions and silently rebinds in others.
            string seed = companion + SeedSuffix;
            if (target.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == seed))
            {
                return "the seed loop's variable '" + seed + "' is already spelled in the type — rename the"
                    + " companion, or free that name";
            }

            return null;
        }

        // The seed is written under `if (IsOwner)`, the inherited property; a type
        // that declares its own member of that name would bind the guard to
        // whatever it declared, so the seeded shapes refuse rather than guess.
        private static string OwnerGuardShadowedReason(ClassDeclarationSyntax target)
            => MemberNameExists(target, OwnerPropertyName)
                ? "the type declares its own '" + OwnerPropertyName + "', so the seed's owner guard would"
                    + " bind to it rather than to the runtime's — rename that member, or seed the list yourself"
                : null;

        // `const` and `static` are refused on the scalar's terms; `readonly` is
        // not, because the arm removes it with the initializer it guarded.
        private static string CheckListDeclarationForm(FieldDeclarationSyntax field, string member)
        {
            if (field.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                return "'" + member + "' is const — a list cannot be";
            }

            if (field.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                return "'" + member + "' is static — one replicated slot would be shared by every instance"
                    + " of the type, and each spawn would rebind it to whichever object spawned last";
            }

            return null;
        }

        // The initializer a list field carries is dropped, not relocated: an
        // empty list is what the construction produces anyway, and the elements
        // of a collection initializer become Add calls the owner makes at spawn.
        private static string CheckListInitializer(VariableDeclaratorSyntax declarator, string member)
        {
            var initializer = declarator.Initializer?.Value;
            if (initializer is null || initializer.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return null;
            }

            if (!IsListCreation(initializer, out var arguments, out var elements))
            {
                return "'" + member + "' has an initializer that is not a new list — its value cannot be"
                    + " relocated into the spawn hook without changing when it is evaluated; assign the"
                    + " elements there with Add, under an IsOwner guard, after the conversion";
            }

            // A literal capacity is dropped — the synchronised list has none. Any
            // other single argument is a capacity spelled as a name or a sequence
            // to copy, and syntax cannot tell the two apart; a copy evaluated at
            // spawn time is a semantic change, so the arm refuses and says which
            // edit fits each reading.
            if (arguments.Count > 1
                || (arguments.Count == 1 && !IsIntegerLiteral(arguments[0].Expression)))
            {
                return "'" + member + "' is initialised with an argument the arm cannot read as a"
                    + " capacity — if it is one, drop it (the synchronised list has none); if it is a"
                    + " sequence to copy, seed the list in OnNetworkSpawn with Add, under an IsOwner"
                    + " guard, after the conversion";
            }

            foreach (var element in elements)
            {
                if (!IsRelocatableElement(element))
                {
                    return "'" + member + "' has a collection initializer with an element outside the"
                        + " relocation allowlist (a literal, an allowlisted static, or a struct built from"
                        + " literals) — seed it in OnNetworkSpawn with Add, under an IsOwner guard, after the"
                        + " conversion";
                }
            }

            return null;
        }

        // `new List<T>()`, `new List<T>(16)`, `new()`, `new List<T> { a, b }` —
        // the shapes a list field is initialised with, and their parts.
        private static bool IsListCreation(
            ExpressionSyntax expression,
            out SeparatedSyntaxList<ArgumentSyntax> arguments,
            out SeparatedSyntaxList<ExpressionSyntax> elements)
        {
            arguments = default;
            elements = default;

            switch (expression)
            {
                case ObjectCreationExpressionSyntax creation when IsListTypeSyntax(creation.Type):
                    arguments = creation.ArgumentList?.Arguments ?? default;
                    elements = creation.Initializer?.Expressions ?? default;
                    return creation.Initializer is null
                        || creation.Initializer.IsKind(SyntaxKind.CollectionInitializerExpression);
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    arguments = implicitCreation.ArgumentList?.Arguments ?? default;
                    elements = implicitCreation.Initializer?.Expressions ?? default;
                    return implicitCreation.Initializer is null
                        || implicitCreation.Initializer.IsKind(SyntaxKind.CollectionInitializerExpression);
                default:
                    return false;
            }
        }

        // `List<T>` however it is spelled — bare, or under its namespace, which
        // is how the analyzer's own fixtures spell it and how a file with no
        // `using System.Collections.Generic` has to. The declared type is read
        // through the same qualification by DeclaredTypeName; an initializer
        // read more narrowly refused a field whose declaration had been admitted.
        private static bool IsListTypeSyntax(TypeSyntax type)
        {
            // A name a `using` directive in this file binds IS the type it
            // names, and the DECLARED type is admitted through the same
            // expansion — so `new Cells()` under `using Cells = List<int>;`
            // reads as the list creation it is, rather than refusing a field
            // whose declaration was already admitted.
            type = ResolveAlias(type);
            var name = type is QualifiedNameSyntax qualified ? qualified.Right : type;
            return name is GenericNameSyntax generic
                && generic.Identifier.ValueText == ListTypeName
                && generic.TypeArgumentList.Arguments.Count == 1;
        }

        private static bool IsIntegerLiteral(ExpressionSyntax expression)
            => expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.NumericLiteralExpression)
                && literal.Token.Value is int;

        // An element the seed may relocate into the spawn hook: the scalar arm's
        // own allowlist, plus a struct built from literals — `new Vector2Int(3, 4)`
        // is how a grid layout is written and evaluates to the same value at any
        // time.
        private static bool IsRelocatableElement(ExpressionSyntax element)
        {
            if (element is LiteralExpressionSyntax literal && !literal.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return true;
            }

            if (element is PrefixUnaryExpressionSyntax negated
                && negated.IsKind(SyntaxKind.UnaryMinusExpression)
                && negated.Operand is LiteralExpressionSyntax)
            {
                return true;
            }

            string text = element.ToString();
            foreach (string allowed in AllowlistedStaticInitializers)
            {
                if (text == allowed || text.EndsWith("." + allowed, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // `new Vector2Int(3, 4)` and, since C# 9 — the declared-minimum
            // Unity's language — `new(3, 4)`, which the element type of the
            // collection initializer resolves exactly as the seed's Add does.
            switch (element)
            {
                case ObjectCreationExpressionSyntax creation:
                    return creation.Initializer is null
                        && creation.ArgumentList != null
                        && creation.ArgumentList.Arguments.Count > 0
                        && creation.ArgumentList.Arguments.All(a => IsRelocatableElement(a.Expression));
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    return implicitCreation.Initializer is null
                        && implicitCreation.ArgumentList.Arguments.Count > 0
                        && implicitCreation.ArgumentList.Arguments.All(a => IsRelocatableElement(a.Expression));
                default:
                    return false;
            }
        }

        private static string CheckListReferences(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, string member,
            string wrapper, TypeSyntax declaredType)
        {
            string unseen = DisabledTextReason(target, member) ?? ShadowingReason(target, member);
            if (unseen != null)
            {
                return unseen;
            }

            var references = ReferencesIn(target, member, out string collectReason);
            if (collectReason != null)
            {
                return collectReason;
            }

            var preSpawnReachable = PreSpawnReachableMembers(target);

            foreach (var identifier in references)
            {
                string storage = StorageLocationDemandReason(identifier, member);
                if (storage != null)
                {
                    return storage;
                }

                // Before any shape below: `_cells.Add(x)` in Awake is a member
                // access the surface admits, and still a null dereference.
                string preSpawn = PreSpawnContextOf(identifier, preSpawnReachable);
                if (preSpawn != null)
                {
                    return "'" + member + "' is accessed in " + preSpawn
                        + ", which runs before OnNetworkSpawn constructs it — the rewrite would be a"
                        + " runtime NRE; populate the list in OnNetworkSpawn under an IsOwner guard, where"
                        + " every other client receives what the owner adds";
                }

                var reference = ThroughParentheses(identifier);

                if (IsListWriteTarget(reference))
                {
                    return "'" + member + "' is assigned after its declaration — the synchronised list is"
                        + " constructed once, in OnNetworkSpawn, and a later assignment would replace the"
                        + " registered variable with a list nothing replicates; replace the assignment with"
                        + " Clear() followed by Add(...)";
                }

                // `_cells!` is the same receiver to every rule below.
                if (reference.Parent is PostfixUnaryExpressionSyntax suppressed
                    && suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                {
                    reference = suppressed;
                }

                if (reference.Parent is MemberAccessExpressionSyntax access && access.Expression == reference)
                {
                    string name = access.Name.Identifier.ValueText;
                    if (UnsupportedListMembers.TryGetValue(name, out string remedy))
                    {
                        return "'" + member + "." + name + "' has no counterpart on " + wrapper
                            + " — " + remedy;
                    }

                    string overload = OverloadRefusal(root, access, member, name, wrapper);
                    if (overload != null)
                    {
                        return overload;
                    }

                    continue;
                }

                if (reference.Parent is ElementAccessExpressionSyntax element && element.Expression == reference)
                {
                    continue; // the indexer reads and writes on the synchronised list too
                }

                // `_cells?.Count`, `_cells?.Add(x)` — the same access through a
                // null-conditional, still on a reference type; the member after
                // the `?.` is judged as a plain access's is, BOTH ways: the
                // table, and the overload. Judging it by the table alone left
                // `_cells?.IndexOf(x, 1)` — the motivating call, one spelling
                // over — written by an apply and met as CS1501 in the editor.
                if (reference.Parent is ConditionalAccessExpressionSyntax conditional
                    && conditional.Expression == reference)
                {
                    string bound = ConditionallyBoundMemberName(conditional.WhenNotNull);
                    if (bound != null && UnsupportedListMembers.TryGetValue(bound, out string boundRemedy))
                    {
                        return "'" + member + "." + bound + "' has no counterpart on " + wrapper
                            + " — " + boundRemedy;
                    }

                    if (bound != null
                        && ConditionallyBoundArgumentCount(conditional.WhenNotNull) is int bindingArity)
                    {
                        string boundOverload = OverloadRefusal(root, member, bound, bindingArity, wrapper);
                        if (boundOverload != null)
                        {
                            return boundOverload;
                        }
                    }

                    continue;
                }

                if (reference.Parent is CommonForEachStatementSyntax loop && loop.Expression == reference)
                {
                    continue; // foreach — deconstructing or not — binds to the same enumerator
                }

                if (reference.Parent is FromClauseSyntax from && from.Expression == reference)
                {
                    continue; // a query expression reads the sequence the list now is
                }

                if (reference.Parent is LockStatementSyntax held && held.Expression == reference)
                {
                    continue; // still a reference type, still lockable
                }

                if (IsNullComparison(reference))
                {
                    continue; // still a reference type, still comparable with null
                }

                // `_cells is { Count: > 0 }` — a property pattern binds the members
                // it names on the synchronised list, so it is admitted when every
                // name is on the list's surface and refused by name otherwise.
                if (reference.Parent is IsPatternExpressionSyntax { Pattern: RecursivePatternSyntax recursive } patterned
                    && patterned.Expression == reference)
                {
                    // `_cells is { } cells` binds the LIST to a second name, and
                    // every use through that name is outside what this pass reads
                    // — `cells.Reverse()` compiles against LINQ and reverses
                    // nothing, the very trap the table above refuses on the field
                    // by name. An alias is a value position with a designation.
                    if (recursive.Designation != null)
                    {
                        return "'" + member + "' is bound to '" + recursive.Designation.ToString()
                            + "' by a pattern — a use through that name is not checked by this"
                            + " pass, and a List<T> member the synchronised list lacks would bind"
                            + " to LINQ or fail there instead; test " + member + " directly";
                    }

                    string outside = PropertyPatternMemberOutsideTheSurface(recursive);
                    if (outside == null)
                    {
                        continue;
                    }

                    return "'" + member + "' is matched by a property pattern naming '" + outside
                        + "', which " + wrapper + " does not carry — match on Count, or on a copy";
                }

                return "'" + member + "' stands as a value in " + DescribeValuePosition(reference)
                    + " — after the conversion it is a " + wrapper + ", not a " + declaredType
                    + "; pass a copy (" + member + ".ToList()) where a " + declaredType
                    + " is required, or take the synchronised type there";
            }

            return OutsideTypeReason(root, target, member);
        }

        // A ref/out argument, a ref binding, and an address-of each demand a
        // storage location of exactly the declared type — which the retyped
        // field no longer is.
        private static string StorageLocationDemandReason(ExpressionSyntax identifier, string member)
        {
            if (identifier.Ancestors().OfType<ArgumentSyntax>()
                .Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None) && a.Expression.DescendantNodesAndSelf().Contains(identifier)))
            {
                return "'" + member + "' is passed by ref/out — a NetworkVariable cannot stand in for a ref location";
            }

            if (identifier.Ancestors().OfType<RefExpressionSyntax>()
                .Any(r => r.Expression.DescendantNodesAndSelf().Contains(identifier)))
            {
                return "'" + member + "' is bound by ref — a ref local or a ref return needs a storage"
                    + " location of the declared type, which the converted member is not";
            }

            if (identifier.Ancestors().OfType<PrefixUnaryExpressionSyntax>()
                .Any(u => u.IsKind(SyntaxKind.AddressOfExpression)
                    && u.Operand.DescendantNodesAndSelf().Contains(identifier)))
            {
                return "'" + member + "' has its address taken — the converted member is not the declared type";
            }

            return null;
        }

        // The surface a property pattern may name: the one readable property the
        // synchronised list carries. A pattern naming anything else is refused
        // rather than left to the gate, because the gate's CS0117 names no remedy.
        private static string PropertyPatternMemberOutsideTheSurface(RecursivePatternSyntax pattern)
        {
            if (pattern.PositionalPatternClause != null)
            {
                return "a positional pattern";
            }

            if (pattern.PropertyPatternClause is null)
            {
                return null;
            }

            foreach (var subpattern in pattern.PropertyPatternClause.Subpatterns)
            {
                string name = subpattern.NameColon?.Name.Identifier.ValueText;
                if (name != "Count")
                {
                    return name ?? "an unnamed subpattern";
                }
            }

            return null;
        }

        // The scalar arm's write test, minus the one postfix that is not a write:
        // `_cells!` suppresses a nullable warning and assigns nothing.
        private static bool IsListWriteTarget(ExpressionSyntax reference)
        {
            var node = ThroughParentheses(reference);
            if (node.Parent is PostfixUnaryExpressionSyntax postfix
                && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                return false;
            }

            return IsWriteTarget(reference);
        }

        /// <summary>
        /// The refusal for a call the synchronised list has the NAME of and not
        /// the overload — or <see langword="null"/> when the call is one it
        /// carries, one it never carried, or one an extension still binds.
        /// </summary>
        /// <remarks>
        /// 🔑 The table above names members the synchronised list does not
        /// carry AT ALL, which is a question about a name; this is the same
        /// question about a SIGNATURE, and it is the one a name cannot answer.
        /// <c>IndexOf(item, start)</c> is <c>List&lt;T&gt;</c>'s three-overload
        /// member and the list's one-argument one, so the name is carried, the
        /// call is not, and an apply wrote a file that met the author as CS1501
        /// in the editor — with the compile gate standing down, as it does on
        /// any file naming a type the contract does not model.
        /// <para>
        /// ⛔ The surface is DERIVED rather than listed, and it is the
        /// SYNCHRONISED LIST'S — read from the contract the compile gate itself
        /// compiles against, so a member added to the runtime cannot be refused
        /// here while the gate admits it. It is deliberately not
        /// <c>List&lt;T&gt;</c>'s: reflecting that reflects whatever framework
        /// the tool is running on, and the CLI runs on .NET while the wizard
        /// runs inside the editor, so the same file would have earned two
        /// verdicts.
        /// </para>
        /// <para>
        /// ⚠️ A name the list does not carry at ALL is therefore not this
        /// rule's — <c>ToArray()</c>, an author's own extension — and stays
        /// where the generation document puts it: the table where the table
        /// names it, the compile gate otherwise. The rule fires where the list
        /// carries the name and not the arity, and stands down where LINQ
        /// declares that arity and the file imports it
        /// (<c>Contains(value, comparer)</c>).
        /// </para>
        /// <para>
        /// ⛔ The limit, stated: an author's OWN extension at the refused arity
        /// would have bound, and this refuses it — a conversion the author can
        /// still take by using the list's own overload. Refusing a call that
        /// would have compiled costs an author one edit; admitting one that
        /// will not costs them a rewrite the gate could not see.
        /// </para>
        /// </remarks>
        private static string OverloadRefusal(
            CompilationUnitSyntax root, MemberAccessExpressionSyntax access, string member, string name,
            string wrapper)
        {
            if (access.Parent is not InvocationExpressionSyntax call || call.Expression != access)
            {
                return null;
            }

            return OverloadRefusal(root, member, name, call.ArgumentList.Arguments.Count, wrapper);
        }

        private static string OverloadRefusal(
            CompilationUnitSyntax root, string member, string name, int arguments, string wrapper)
        {
            // The name has to be one the list CARRIES. A name it does not carry
            // at any arity was never this rule's: it is the table's where the
            // table names it, and the compile gate's otherwise — `ToArray()`
            // reaches LINQ, an author's own extension reaches the author's.
            if (!SynchronisedListDeclaresTheName(name)) return null;
            if (SynchronisedListDeclares(name, arguments)) return null;
            if (LinqIsImported(root) && EnumerableDeclares(name, arguments)) return null;

            return "'" + member + "." + name + "' is called with " + arguments
                + (arguments == 1 ? " argument" : " arguments")
                + ", and " + wrapper + " declares no such overload — that call binds to List<T> "
                + "and would not compile after the conversion; use the one-argument form, or do "
                + "the work over a copy (ToList()) and write the result back with Clear and Add";
        }

        /// <summary>
        /// The synchronised list's public surface, as the two questions a call
        /// is judged by: which names it carries, and at which argument counts.
        /// </summary>
        private sealed class ListSurface
        {
            internal readonly HashSet<string> Names = new HashSet<string>(System.StringComparer.Ordinal);

            internal readonly HashSet<string> Signatures =
                new HashSet<string>(System.StringComparer.Ordinal);
        }

        // Read once from the contract the compile gate compiles a candidate
        // against: every public method the type and its base declare.
        private static readonly System.Lazy<ListSurface> SynchronisedListSurface =
            new System.Lazy<ListSurface>(ReadSynchronisedListSurface);

        private static ListSurface ReadSynchronisedListSurface()
        {
            var surface = new ListSurface();
            var classes = CSharpSyntaxTree.ParseText(SdkContract.Stub).GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();

            // ⛔ The base chain too, walked in the contract itself: a member the
            // list INHERITS is one a call binds to, and reading the leaf alone
            // would refuse it as an overload the list does not declare. The
            // base is empty today, which is exactly why a later member on it
            // would go unnoticed.
            string name = "NetworkVariableList";
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            while (name != null && seen.Add(name))
            {
                var declaration = classes.FirstOrDefault(c => c.Identifier.ValueText == name);
                if (declaration == null) break;

                foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    // Callable on the concrete type, which is what a call binds
                    // to: an explicit interface implementation is reachable only
                    // through the interface, and a member that is not public is
                    // reachable from no author's code.
                    if (method.ExplicitInterfaceSpecifier != null) continue;
                    if (!method.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;

                    surface.Signatures.Add(
                        Signature(method.Identifier.ValueText, method.ParameterList.Parameters.Count));
                    surface.Names.Add(method.Identifier.ValueText);
                }

                name = declaration.BaseList?.Types.Count > 0
                    ? BaseTypeName(declaration.BaseList.Types[0].Type)
                    : null;
            }

            return surface;
        }

        // The simple name of a base type as the contract spells it, generic
        // arguments and namespace qualification set aside — the chain is
        // matched against the declarations in the same document.
        private static string BaseTypeName(TypeSyntax type)
        {
            while (true)
            {
                switch (type)
                {
                    case QualifiedNameSyntax qualified:
                        type = qualified.Right;
                        continue;
                    case GenericNameSyntax generic:
                        return generic.Identifier.ValueText;
                    case SimpleNameSyntax simple:
                        return simple.Identifier.ValueText;
                    default:
                        return null;
                }
            }
        }

        private static string Signature(string name, int arguments) => name + "/" + arguments;

        private static bool SynchronisedListDeclares(string name, int arguments)
            => SynchronisedListSurface.Value.Signatures.Contains(Signature(name, arguments));

        private static bool SynchronisedListDeclaresTheName(string name)
            => SynchronisedListSurface.Value.Names.Contains(name);

        private static bool EnumerableDeclares(string name, int arguments)
            => typeof(System.Linq.Enumerable).GetMethods()
                .Any(method => method.Name == name && method.GetParameters().Length == arguments + 1);

        // ⚠️ The file's own plain `using System.Linq`, which is how every Unity
        // script that uses LINQ imports it. A `global using` in another file,
        // or a `using static System.Linq.Enumerable`, reads as absent here and
        // costs a refusal of a call that would still have bound — the
        // direction that refuses rather than writes code the editor rejects.
        private static bool LinqIsImported(CompilationUnitSyntax root)
            => root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Any(directive => directive.Name != null
                    && directive.Name.ToString() == "System.Linq"
                    && directive.Alias == null
                    && directive.StaticKeyword.IsKind(SyntaxKind.None));

        // The member a `?.` binds on the list — the FIRST link of whatever follows
        // it: `_a?.Count`, `_a?.Add(x)`, and equally `_a?.Find(p).ToString()` or
        // `_a?.AsReadOnly().Count`, where the list's member is the innermost
        // node and the chain is built outward from it. A reading of the top
        // node alone saw `.ToString()` and admitted `Find` past the table — an
        // adversarial review reached it in two of its cases. An element
        // binding (`_a?[0]`) names no member and is admitted as the indexer is.
        // How many arguments the `?.`-bound member is called with, or null when
        // it is not called at all (`_a?.Count`). Read from the same innermost
        // link the name is, so the two describe one member.
        private static int? ConditionallyBoundArgumentCount(ExpressionSyntax whenNotNull)
        {
            ExpressionSyntax node = whenNotNull;
            while (true)
            {
                switch (node)
                {
                    case InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } call:
                        return call.ArgumentList.Arguments.Count;
                    case InvocationExpressionSyntax invocation:
                        node = invocation.Expression;
                        continue;
                    case MemberAccessExpressionSyntax access:
                        node = access.Expression;
                        continue;
                    case ElementAccessExpressionSyntax element:
                        node = element.Expression;
                        continue;
                    case ConditionalAccessExpressionSyntax nested:
                        node = nested.Expression;
                        continue;
                    default:
                        return null;
                }
            }
        }

        private static string ConditionallyBoundMemberName(ExpressionSyntax whenNotNull)
        {
            ExpressionSyntax node = whenNotNull;
            while (true)
            {
                switch (node)
                {
                    case MemberBindingExpressionSyntax binding:
                        return binding.Name.Identifier.ValueText;
                    case InvocationExpressionSyntax invocation:
                        node = invocation.Expression;
                        continue;
                    case MemberAccessExpressionSyntax access:
                        node = access.Expression;
                        continue;
                    case ElementAccessExpressionSyntax element:
                        node = element.Expression;
                        continue;
                    case ConditionalAccessExpressionSyntax nested:
                        node = nested.Expression;
                        continue;
                    default:
                        return null;
                }
            }
        }

        private static bool IsNullComparison(ExpressionSyntax reference)
        {
            switch (reference.Parent)
            {
                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression):
                    var other = binary.Left == reference ? binary.Right : binary.Left;
                    return other.IsKind(SyntaxKind.NullLiteralExpression);
                case IsPatternExpressionSyntax pattern when pattern.Expression == reference:
                    return pattern.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal }
                            && literal.IsKind(SyntaxKind.NullLiteralExpression)
                        || pattern.Pattern is UnaryPatternSyntax
                        {
                            Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax inner },
                        } && inner.IsKind(SyntaxKind.NullLiteralExpression);
                default:
                    return false;
            }
        }

        private static string DescribeValuePosition(ExpressionSyntax reference)
        {
            foreach (var ancestor in reference.Ancestors())
            {
                switch (ancestor)
                {
                    case ArgumentSyntax argument:
                        return "an argument"
                            + (argument.Parent?.Parent is InvocationExpressionSyntax call
                                ? " to '" + call.Expression.ToString() + "'"
                                : string.Empty);
                    case ReturnStatementSyntax:
                        return "a return statement";
                    case EqualsValueClauseSyntax:
                        return "an initializer";
                    case AssignmentExpressionSyntax:
                        return "an assignment";
                    case StatementSyntax statement:
                        return "a " + statement.Kind().ToString().Replace("Statement", string.Empty).ToLowerInvariant() + " statement";
                }
            }

            return "an expression";
        }

        // ── Member edits ────────────────────────────────────────────────────────

        private static ClassDeclarationSyntax EditListMember(
            ClassDeclarationSyntax current, PlannedConversion conversion,
            FieldDeclarationSyntax field, VariableDeclaratorSyntax declarator,
            IdentifierNameSyntax nvType, SyntaxTrivia newLine, List<StatementSyntax> constructions)
        {
            if (conversion.Arm == ConversionArm.InPlace)
            {
                var seeds = new List<StatementSyntax>();
                if (declarator.Initializer?.Value is { } initializer
                    && IsListCreation(initializer, out _, out var elements))
                {
                    foreach (var element in elements)
                    {
                        seeds.Add(AddCall(declarator.Identifier, element));
                    }
                }

                current = RetypeField(current, field, declarator, nvType, newLine, dropReadOnly: true);
                constructions.Add(Construction(declarator.Identifier, nvType, initialValue: null));
                if (seeds.Count > 0)
                {
                    constructions.Add(OwnerGuarded(seeds));
                }

                return current;
            }

            var companionToken = SyntaxFactory.Identifier(conversion.CompanionFieldName);
            current = InsertCompanion(current, field, companionToken, nvType, newLine);
            constructions.Add(Construction(companionToken, nvType, initialValue: null));

            // The config list seeds the companion, element by element, on the
            // owner alone: a replica's Add is refused by the runtime and reported
            // once a second, and what a replica holds is what the owner sends.
            // Guarded against a null config too — Unity assigns a serialized list
            // on deserialization, but a public field on an object built any other
            // way is whatever its constructor left, and `foreach` over null throws
            // out of the spawn hook.
            // Every token spaced by hand, as the rest of this file spaces its
            // emissions: a factory node carries no whitespace of its own, and
            // the output is written verbatim rather than normalised.
            var seed = SyntaxFactory.Identifier(conversion.CompanionFieldName + SeedSuffix);
            var loop = SyntaxFactory.ForEachStatement(
                    SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                    seed.WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.IdentifierName(declarator.Identifier.WithoutTrivia()),
                    AddCall(companionToken, SyntaxFactory.IdentifierName(seed)))
                .WithForEachKeyword(SyntaxFactory.Token(SyntaxKind.ForEachKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithInKeyword(SyntaxFactory.Token(SyntaxKind.InKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithCloseParenToken(SyntaxFactory.Token(SyntaxKind.CloseParenToken).WithTrailingTrivia(SyntaxFactory.Space));
            constructions.Add(OwnerGuarded(
                new List<StatementSyntax> { loop },
                alsoRequire: SyntaxFactory.BinaryExpression(
                    SyntaxKind.NotEqualsExpression,
                    SyntaxFactory.IdentifierName(declarator.Identifier.WithoutTrivia()).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.ExclamationEqualsToken).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
            return current;
        }

        private static StatementSyntax AddCall(SyntaxToken list, ExpressionSyntax element)
            => SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(list.WithoutTrivia()),
                        SyntaxFactory.IdentifierName("Add")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(element.WithoutTrivia())))));

        // `if (IsOwner) { … }` — or `if (IsOwner && <alsoRequire>) { … }` — laid
        // out by the hook edit, which knows the indent.
        private static StatementSyntax OwnerGuarded(List<StatementSyntax> statements, ExpressionSyntax alsoRequire = null)
        {
            ExpressionSyntax condition = SyntaxFactory.IdentifierName(OwnerPropertyName);
            if (alsoRequire != null)
            {
                condition = SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalAndExpression,
                    condition.WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.AmpersandAmpersandToken).WithTrailingTrivia(SyntaxFactory.Space),
                    alsoRequire);
            }

            return SyntaxFactory.IfStatement(condition, SyntaxFactory.Block(statements));
        }

        /// <summary>
        /// A statement indented for the hook body it joins: a single line for the
        /// constructions, and one line per brace and per inner statement for the
        /// owner-guarded seed block.
        /// </summary>
        private static StatementSyntax LayOut(StatementSyntax statement, string indent, SyntaxTrivia newLine)
        {
            if (statement is not IfStatementSyntax { Statement: BlockSyntax block } guard)
            {
                return statement.WithLeadingTrivia(SyntaxFactory.Whitespace(indent)).WithTrailingTrivia(newLine);
            }

            string inner = indent + BlockEditing.IndentStep(indent);
            var laidOut = block
                .WithOpenBraceToken(
                    SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                        .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                        .WithTrailingTrivia(newLine))
                .WithStatements(SyntaxFactory.List(block.Statements.Select(
                    s => s.WithLeadingTrivia(SyntaxFactory.Whitespace(inner)).WithTrailingTrivia(newLine))))
                .WithCloseBraceToken(
                    SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                        .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                        .WithTrailingTrivia(newLine));

            return guard
                .WithIfKeyword(guard.IfKeyword.WithLeadingTrivia(SyntaxFactory.Whitespace(indent)).WithTrailingTrivia(SyntaxFactory.Space))
                .WithCloseParenToken(guard.CloseParenToken.WithTrailingTrivia(newLine))
                .WithStatement(laidOut);
        }
    }
}
