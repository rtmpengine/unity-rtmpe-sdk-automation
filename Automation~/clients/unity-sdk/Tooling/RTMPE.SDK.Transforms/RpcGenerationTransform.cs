using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Converts planned methods into Enhanced-RPC endpoints: annotates each with
    /// <c>[RtmpeRpc(RpcTarget.X)]</c> and rewrites its intra-type call sites to
    /// <c>this.RPC("Method", args)</c>. No identity is computed here — the wire
    /// id is a pure function of the type and method name the runtime derives
    /// itself — and the audience is written verbatim from the plan, never
    /// decided. Purely syntactic and deliberately conservative: any shape whose
    /// rewrite cannot be proven safe from this one compilation unit is refused
    /// whole — the transform returns the unmodified root and a reason, never a
    /// partial or speculative edit. All generated code is built from typed
    /// factories over the original tokens, so a hostile method name or argument
    /// cannot splice code.
    /// </summary>
    public static class RpcGenerationTransform
    {
        private const string RpcNamespace = "RTMPE.Rpc";
        private const string SendMethodName = "RPC";
        private const string OwnerPropertyName = "IsOwner";
        private const string SdkBaseTypeName = "NetworkBehaviour";

        public static CompilationUnitSyntax Apply(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, RpcPlan plan)
            => Apply(root, target, plan, out _);

        public static CompilationUnitSyntax Apply(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, RpcPlan plan,
            out string refusalReason)
        {
            refusalReason = null;
            if (root is null || target is null || plan is null || plan.Emissions.Count == 0)
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
                refusalReason = "the type is partial — a method and its call sites may live in another file this rewrite cannot see";
                return root;
            }

            // The runtime dispatches [RtmpeRpc] only on NetworkBehaviour
            // subclasses; a type with no base list at all cannot be one. Deeper
            // base-chain proof is semantic and stays with RTMPE1005 + the
            // registry's own validation.
            if (target.BaseList is null || target.BaseList.Types.Count == 0)
            {
                refusalReason = "the type declares no base type — an RPC endpoint must be a NetworkBehaviour subclass";
                return root;
            }

            var methodEdits = new Dictionary<SyntaxNode, RpcAudience>();
            var siteEdits = new Dictionary<SyntaxNode, MethodDeclarationSyntax>();
            var planned = new HashSet<string>(System.StringComparer.Ordinal);

            foreach (var emission in plan.Emissions)
            {
                if (!planned.Add(emission.MethodName))
                {
                    refusalReason = "'" + emission.MethodName + "' appears twice in the plan — a method converts exactly once";
                    return root;
                }

                var declarations = target.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.ValueText == emission.MethodName)
                    .ToList();
                if (declarations.Count == 0)
                {
                    refusalReason = "no method named '" + emission.MethodName + "' is declared on the type";
                    return root;
                }

                if (declarations.Count > 1)
                {
                    refusalReason = "'" + emission.MethodName + "' is overloaded — the wire id keys on the name alone, so overloads cannot both dispatch";
                    return root;
                }

                var method = declarations[0];
                if (HasRtmpeRpcAttribute(method))
                {
                    continue; // already converted — a re-run is a provable no-op
                }

                List<InvocationExpressionSyntax> sites = null;
                string reason = RefuseMethod(method, emission);
                if (reason == null)
                {
                    reason = CollectSendSites(root, target, method, out sites);
                }

                if (reason != null)
                {
                    refusalReason = reason;
                    return root;
                }

                methodEdits.Add(method, emission.Audience);
                foreach (var site in sites)
                {
                    siteEdits.Add(site, method);
                }
            }

            if (methodEdits.Count == 0)
            {
                return root;
            }

            var newLine = BlockEditing.DetectNewLine(root);

            // One batch replacement over the collected set — never a recursive
            // rewriter. Call sites always live outside the methods being
            // annotated (a site inside a converted method was refused as
            // recursion), but the rewritten argument is used regardless so a
            // node whose descendants were edited is never rebuilt from a stale
            // original.
            var updated = target.ReplaceNodes(
                methodEdits.Keys.Concat(siteEdits.Keys),
                (original, rewritten) =>
                    methodEdits.TryGetValue(original, out var audience)
                        ? Annotate((MethodDeclarationSyntax)rewritten, audience, newLine)
                        : (SyntaxNode)SendSite((InvocationExpressionSyntax)rewritten, siteEdits[original]));

            var replaced = root.ReplaceNode(target, updated);
            return ImportEditing.IsInScope(target, RpcNamespace)
                ? replaced
                : ImportEditing.AddFileLevelImport(replaced, RpcNamespace);
        }

        // ── Refuse-guards — each returns a human-actionable reason ──────────────

        private static string RefuseMethod(MethodDeclarationSyntax method, PlannedRpcEmission emission)
        {
            string name = emission.MethodName;

            if (HasDirectiveTrivia(method))
            {
                return "'" + name + "' is declared under #if directive trivia — a rewrite could unbalance the directives";
            }

            if (method.Modifiers.Any(SyntaxKind.StaticKeyword) || method.Modifiers.Any(SyntaxKind.AbstractKeyword))
            {
                return "'" + name + "' is static or abstract — the registry dispatches public instance methods only";
            }

            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
            {
                return "'" + name + "' is not public — the registry discovers public instance methods only, and widening accessibility is a semantic change a machine must not make";
            }

            if (method.TypeParameterList != null)
            {
                return "'" + name + "' is generic — a wire call carries no type arguments to close it";
            }

            if (method.ReturnType is not PredefinedTypeSyntax returnType
                || !returnType.Keyword.IsKind(SyntaxKind.VoidKeyword))
            {
                return "'" + name + "' returns a value — an RPC send cannot deliver a return value to the caller";
            }

            foreach (var parameter in method.ParameterList.Parameters)
            {
                string parameterName = parameter.Identifier.ValueText;
                if (parameter.Modifiers.Count > 0)
                {
                    return "parameter '" + parameterName + "' of '" + name + "' carries a modifier — ref/out/in/params cannot travel over the wire";
                }

                if (parameter.Default != null)
                {
                    // The remedy names a SEPARATE name deliberately: an overload of
                    // this method is refused by Apply, before this function is
                    // reached, because the wire id keys on the name alone. Guidance
                    // that ended at "keep the default in a wrapper" would be refused
                    // on the author's very next run, by this same transform.
                    //
                    // ⚠️ And "positionally" is not a flourish. "Spelled out" invited
                    // `Hit(amount: 1)` — the most natural way to write a call that
                    // used to rely on a default — which the call-site scan refuses
                    // for a named argument. A remedy is measured by running it, and
                    // that shape was refused on the run after the one this sentence
                    // was written for.
                    return "parameter '" + parameterName + "' of '" + name + "' has a default value — a wire call"
                        + " always carries every argument, so the default is unreachable and misleading; drop it"
                        + " here and keep the short form as a helper under a name of its own that calls '" + name
                        + "' positionally with every argument present — an overload cannot serve, because the"
                        + " wire id keys on the name alone, and a named argument is refused by the call-site"
                        + " scan below";
                }

                string typeName = ParameterTypeName(parameter.Type);
                if (!RpcParameterClassifier.IsSupported(typeName))
                {
                    return "parameter '" + parameterName + "' of '" + name + "' has type '" + parameter.Type
                        + "', which is outside the serializer's closed set (an INetworkSerializable"
                        + " implementer also needs a hand-written RpcTypeRegistry.Register<T>() — annotate it manually)";
                }
            }

            // A broadcast-audience mutator with no leading owner guard executes
            // the write on every receiving client — the client-authoritative
            // cheat primitive the runtime cannot close (no caller→object
            // authorization exists). Server fails closed instead: a receiving
            // client never executes a Server-declared method.
            if (emission.Audience != RpcAudience.Server && !HasLeadingOwnerGuard(method))
            {
                var enclosing = EnclosingType(method);
                if (MutatesInstanceState(method, enclosing))
                {
                    return "'" + name + "' mutates instance state with audience '" + emission.Audience
                        + "' but no leading owner guard — every receiving client would apply the write;"
                        + " use RpcTarget.Server, or open the method with 'if (!IsOwner) return;'";
                }

                // The member set above is drawn from this file alone. Where the chain
                // leaves it, a write this pass cannot attribute is the same broadcast
                // over unguarded state, seen through a smaller window — so it is
                // declined on the same terms rather than cleared for lack of evidence.
                string unattributed = WriteBeyondTheVisibleSurface(method, enclosing);
                if (unattributed != null)
                {
                    return "'" + name + "' writes '" + unattributed
                        + "', which is not declared in this file, with audience '" + emission.Audience
                        + "' and no leading owner guard — if it is state inherited from a base declared"
                        + " elsewhere, every receiving client would apply the write; use RpcTarget.Server,"
                        + " or open the method with 'if (!IsOwner) return;'";
                }
            }

            return null;
        }

        // Resolves every appearance of the method's name inside the file into
        // either a rewritable send site or a refusal. A send site is exactly a
        // direct invocation on the implicit or explicit `this`; every other
        // appearance — another receiver, a method group, a nested type, code
        // outside the target — is ambiguous to a syntax-only pass and refuses
        // the whole plan rather than guess.
        private static string CollectSendSites(
            CompilationUnitSyntax root, ClassDeclarationSyntax target, MethodDeclarationSyntax method,
            out List<InvocationExpressionSyntax> sites)
        {
            sites = new List<InvocationExpressionSyntax>();
            string name = method.Identifier.ValueText;

            // A same-named local — parameter, local variable, local function,
            // foreach/pattern/catch/query variable, or lambda parameter —
            // shadows the method at a bare `Name(...)` call, so the call binds to
            // the local, not the method. A syntax pass cannot tell which is in
            // scope at each site, so any such shadow refuses the whole plan
            // rather than risk rewriting a local invocation into an RPC send.
            string shadow = LocalShadowOf(target, name);
            if (shadow != null)
            {
                return "'" + name + "' is shadowed by " + shadow
                    + " — a bare call could bind to the local rather than the method";
            }

            // A same-named method inherited from a base type declared in this
            // file is an overload the id keys cannot separate: a bare call may
            // bind to the base overload, not the planned method. Refuse rather
            // than retarget it. (A base in another file is invisible here — a
            // documented limitation, backstopped by the runtime's own checks.)
            if (InFileBaseDeclaresMethod(target, name))
            {
                return "'" + name + "' is also declared on a base type in this file — a call could bind"
                    + " to the inherited overload, which the rewrite cannot separate from the RPC method";
            }

            // The send is emitted as `this.RPC(...)`, and a member of that name on
            // the type is found before the inherited extension point. The call then
            // compiles and dispatches nowhere, which is the least visible way this
            // transform can fail.
            string bound = NameBinding.DescribeOnType(target, SendMethodName);
            if (bound != null || InFileBaseDeclaresMethod(target, SendMethodName))
            {
                return "'" + SendMethodName + "' is already " + (bound ?? "declared on a base type in this file")
                    + " — the emitted send would bind to it instead of the SDK's, and carry nothing over the wire";
            }

            // A call site inside an inactive #if branch is not a node at all —
            // it survives only as disabled-text trivia — so the scan below can
            // neither see nor rewrite it, and the direct call would silently
            // remain once that branch compiles. Refuse the whole plan instead.
            foreach (var trivia in target.DescendantTrivia(descendIntoTrivia: true))
            {
                if (trivia.IsKind(SyntaxKind.DisabledTextTrivia)
                    && trivia.ToString().Contains(name))
                {
                    return "'" + name + "' appears inside text disabled by a preprocessor directive"
                        + " — a call site under an inactive #if cannot be seen or rewritten";
                }
            }

            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.ValueText != name || IsInsideNameOf(identifier))
                {
                    continue;
                }

                if (!identifier.Ancestors().Contains(target))
                {
                    return "'" + name + "' is referenced outside the declaring type — the rewrite cannot prove the binding";
                }

                if (!ReferenceEquals(EnclosingType(identifier), target))
                {
                    return "'" + name + "' is referenced inside a nested type — binding is ambiguous without a semantic model";
                }

                InvocationExpressionSyntax invocation;
                switch (identifier.Parent)
                {
                    case InvocationExpressionSyntax direct when direct.Expression == identifier:
                        invocation = direct;
                        break;
                    case MemberAccessExpressionSyntax access when access.Name == identifier:
                        if (access.Expression is BaseExpressionSyntax)
                        {
                            return "'" + name + "' is called through 'base' — a base call is not a send on this instance";
                        }

                        if (access.Expression is not ThisExpressionSyntax
                            || access.Parent is not InvocationExpressionSyntax qualified
                            || qualified.Expression != access)
                        {
                            return "'" + name + "' is accessed through a receiver other than 'this' — the rewrite cannot prove it targets this instance";
                        }

                        invocation = qualified;
                        break;
                    default:
                        return "'" + name + "' is referenced as a method group — a delegate reference cannot become an RPC send";
                }

                if (invocation.Ancestors().Contains(method))
                {
                    return "'" + name + "' calls itself — rewriting the recursive call would make every dispatch re-send the RPC";
                }

                if (HasDirectiveTrivia(invocation))
                {
                    return "a call to '" + name + "' sits under #if directive trivia — a rewrite could unbalance the directives";
                }

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (argument.NameColon != null)
                    {
                        return "a call to '" + name + "' uses a named argument — RPC(string, params object[]) carries positional arguments only";
                    }

                    if (!argument.RefKindKeyword.IsKind(SyntaxKind.None))
                    {
                        return "a call to '" + name + "' passes an argument by ref/out — a wire argument is a value";
                    }
                }

                if (invocation.ArgumentList.Arguments.Count != method.ParameterList.Parameters.Count)
                {
                    return "a call to '" + name + "' does not pass every parameter — a wire call carries the full argument list";
                }

                // The send is emitted as a freshly built argument list, so trivia
                // the original list carried between its own tokens has nowhere to
                // land. The question is asked of the rewrite itself rather than of
                // a hand-listed set of positions, so the two can never disagree
                // about which comments survive.
                if (DropsAComment(invocation, SendSite(invocation, method)))
                {
                    return "a call to '" + name + "' carries a comment inside its argument list — the send"
                        + " rebuilds that list, and deleting an author's comment is not an edit this makes silently";
                }

                sites.Add(invocation);
            }

            return null;
        }

        // Describes the first same-named local introduction found anywhere in
        // the type — a parameter, local variable, local function, or foreach/
        // pattern/catch/query range variable — or null when the name is free.
        // A declaration node covers pattern/catch designations; the rest carry
        // the name on a token, so each is matched on its kind (the same
        // discipline the NetworkVariable transform uses for its shadow scan).
        private static string LocalShadowOf(ClassDeclarationSyntax target, string name)
        {
            foreach (var node in target.DescendantNodes())
            {
                switch (node)
                {
                    case ParameterSyntax parameter when parameter.Identifier.ValueText == name:
                        return "a parameter";
                    case LocalFunctionStatementSyntax localFunction when localFunction.Identifier.ValueText == name:
                        return "a local function";
                    case ForEachStatementSyntax forEach when forEach.Identifier.ValueText == name:
                        return "a foreach variable";
                    case SingleVariableDesignationSyntax designation when designation.Identifier.ValueText == name:
                        return "a pattern variable";
                    case CatchDeclarationSyntax catchDeclaration when catchDeclaration.Identifier.ValueText == name:
                        return "a catch variable";
                    case VariableDeclaratorSyntax declarator
                        when declarator.Identifier.ValueText == name
                            && declarator.Parent is VariableDeclarationSyntax declaration
                            && declaration.Parent is not FieldDeclarationSyntax
                            && declaration.Parent is not EventFieldDeclarationSyntax:
                        return "a local variable";
                    case FromClauseSyntax fromClause when fromClause.Identifier.ValueText == name:
                    case LetClauseSyntax letClause when letClause.Identifier.ValueText == name:
                    case JoinClauseSyntax joinClause when joinClause.Identifier.ValueText == name:
                    case JoinIntoClauseSyntax joinInto when joinInto.Identifier.ValueText == name:
                    case QueryContinuationSyntax continuation when continuation.Identifier.ValueText == name:
                        return "a query range variable";
                }
            }

            return null;
        }

        // True when a base type declared in the same file (not the target
        // itself) declares a method of the given name — an inherited overload
        // the FNV id cannot separate from the target method.
        private static bool InFileBaseDeclaresMethod(ClassDeclarationSyntax target, string name)
        {
            bool isTarget = true;
            foreach (var type in InFileTypeChain(target))
            {
                if (isTarget)
                {
                    isTarget = false;
                    continue;
                }

                if (type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == name))
                {
                    return true;
                }
            }

            return false;
        }

        // ── Shared candidate predicates (also the CLI's policy inputs) ──────────

        /// <summary>
        /// True when the method writes a member of <paramref name="target"/> —
        /// an assignment, compound assignment, increment/decrement, a
        /// <c>ref</c>/<c>out</c> argument, or a method invoked on the member —
        /// whose leftmost receiver is a declared field, property, or event of the
        /// type (directly, through <c>this.</c>, or inherited from a base type
        /// declared in the same file). Syntax-only and deliberately
        /// over-inclusive: it errs toward reporting a mutation (a local shadowing
        /// a member name, a cast around the target, a member passed by reference
        /// or mutated through a call), which only tightens the audience policy
        /// this predicate feeds — a missed mutation would let an unguarded
        /// broadcast slip the policy gate, so the bias is the safe one.
        /// </summary>
        public static bool MutatesInstanceState(MethodDeclarationSyntax method, TypeDeclarationSyntax target)
        {
            if (method is null || target is null)
            {
                return false;
            }

            var members = InstanceStateNames(target);
            foreach (var node in BodyNodes(method))
            {
                // A method invoked on a member — _items.Add(x), _queue.Clear() — may
                // mutate it, and a syntax pass cannot know whether the callee writes, so
                // the receiver joins the direct targets as a possible one. A bare or
                // non-member call carries no such receiver and is left alone, so a logger
                // or a static is not read as a write.
                ExpressionSyntax written = DirectWriteTarget(node)
                    ?? (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax receiver }
                        ? receiver.Expression
                        : null);

                if (written != null && WritesMember(written, members))
                {
                    return true;
                }
            }

            return false;
        }

        // The expression a node writes to directly: an assignment target, an increment's
        // operand, or an argument handed to a callee by reference, which the callee may
        // write and is indistinguishable from a direct write here.
        private static ExpressionSyntax DirectWriteTarget(SyntaxNode node)
            => node switch
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
                ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None)
                    => argument.Expression,
                _ => null,
            };

        // The name a direct write targets, when that target is a bare identifier or a
        // `this.`-qualified one — the only two spellings that can reach an inherited
        // member. A write through any other receiver names something this type does not
        // own, so it is not read here: the point is to notice unattributable state, not
        // to widen what counts as a write.
        private static string DirectlyWrittenName(SyntaxNode node)
            => DirectWriteTarget(node) switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax access when access.Expression is ThisExpressionSyntax
                    => access.Name.Identifier.ValueText,
                _ => null,
            };

        // Every name the method introduces itself — parameters, locals, iteration and
        // pattern variables, and those of any nested lambda or local function. Nested
        // scopes are folded in whole: a name bound anywhere inside the method is not
        // evidence of inherited state, and reading it as such would decline a method
        // that only ever writes its own locals.
        private static HashSet<string> LocallyBoundNames(MethodDeclarationSyntax method)
        {
            var names = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var parameter in method.ParameterList.Parameters)
            {
                names.Add(parameter.Identifier.ValueText);
            }

            foreach (var node in BodyNodes(method))
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax declarator:
                        names.Add(declarator.Identifier.ValueText);
                        break;
                    case SingleVariableDesignationSyntax designation:
                        names.Add(designation.Identifier.ValueText);
                        break;
                    case ForEachStatementSyntax iteration:
                        names.Add(iteration.Identifier.ValueText);
                        break;
                    case ParameterSyntax nested:
                        names.Add(nested.Identifier.ValueText);
                        break;
                }
            }

            return names;
        }

        // True when every base declared by a type in this file is itself declared here,
        // so the member set gathered from the file is the whole inherited surface. Only
        // the first base-list entry can name a class; the rest are interfaces and carry
        // no state.
        private static bool InheritedStateIsVisible(TypeDeclarationSyntax target)
        {
            var byName = TypesDeclaredBeside(target);
            foreach (var type in InFileTypeChain(target))
            {
                var declaredBase = type.BaseList?.Types.FirstOrDefault();
                if (declaredBase is null)
                {
                    continue;
                }

                string baseName = DeclaredTypeName(declaredBase.Type);
                if (baseName is null || baseName == SdkBaseTypeName)
                {
                    continue; // the SDK root declares no state a subclass replicates
                }

                if (!byName.ContainsKey(baseName))
                {
                    return false;
                }
            }

            return true;
        }

        // The first write the file cannot account for, or null when the inherited
        // surface is fully visible or every write lands on something known.
        private static string WriteBeyondTheVisibleSurface(
            MethodDeclarationSyntax method, TypeDeclarationSyntax target)
        {
            if (method is null || target is null || InheritedStateIsVisible(target))
            {
                return null;
            }

            var members = InstanceStateNames(target);
            var locals = LocallyBoundNames(method);
            foreach (var node in BodyNodes(method))
            {
                string written = DirectlyWrittenName(node);
                if (written != null && !members.Contains(written) && !locals.Contains(written))
                {
                    return written;
                }
            }

            return null;
        }

        /// <summary>
        /// True when the method's block body opens with an immediate-return
        /// owner guard — an <c>if</c> whose condition negates <c>IsOwner</c>
        /// (<c>!IsOwner</c>, <c>IsOwner == false</c>, <c>IsOwner != true</c>),
        /// alone or as any operand of a top-level <c>||</c> chain, so a
        /// non-owner always exits before the first write. Syntactic: the
        /// spelling <c>IsOwner</c> / <c>this.IsOwner</c> is trusted to be the
        /// SDK property, the same trust the readiness scorer's samples earn —
        /// but that trust is void when the name is shadowed. A parameter named
        /// <c>IsOwner</c> is a wire-supplied RPC argument, so a bare
        /// <c>IsOwner</c> guarded on it is an attacker-controlled gate, not an
        /// ownership check; and a member named <c>IsOwner</c> on the type
        /// shadows the SDK property for the <c>this.</c> form too. Either
        /// shadow makes the guard untrustworthy, so it is not recognised.
        /// </summary>
        public static bool HasLeadingOwnerGuard(MethodDeclarationSyntax method)
        {
            if (method?.Body?.Statements.FirstOrDefault() is not IfStatementSyntax guard)
            {
                return false;
            }

            var type = EnclosingType(method);
            if (type != null && DeclaresMember(type, OwnerPropertyName))
            {
                return false; // a type-level `IsOwner` shadows even `this.IsOwner`
            }

            bool bareShadowed = method.ParameterList.Parameters
                .Any(p => p.Identifier.ValueText == OwnerPropertyName);

            return ReturnsImmediately(guard.Statement)
                && AnyDisjunctNegatesOwner(Unparenthesize(guard.Condition), bareShadowed);
        }

        // ── Emission: typed factories over original tokens only ────────────────

        private static MethodDeclarationSyntax Annotate(
            MethodDeclarationSyntax method, RpcAudience audience, SyntaxTrivia newLine)
        {
            string indent = IndentText(method);
            // Emitted unqualified, against the `using RTMPE.Rpc;` this transform
            // inserts. A user type named RpcTarget in the same namespace would win
            // the lookup — and then fail to compile, because the attribute's
            // constructor takes the real enum. The collision is loud, immediate,
            // and rare; fully-qualifying every emission to pre-empt it would put
            // RTMPE.Rpc.RpcTarget.Server into source a human reads and maintains.
            // Generated code that reads like hand-written code is worth more than
            // a defence against a failure the compiler already stops.
            var list = SyntaxFactory.AttributeList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Attribute(
                            SyntaxFactory.IdentifierName("RtmpeRpc"),
                            SyntaxFactory.AttributeArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.AttributeArgument(
                                        SyntaxFactory.MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            SyntaxFactory.IdentifierName("RpcTarget"),
                                            SyntaxFactory.IdentifierName(audience.ToString()))))))))
                .WithLeadingTrivia(method.GetLeadingTrivia())
                .WithTrailingTrivia(newLine);

            // The new list takes over the method's leading trivia (doc comment
            // and indent), so whatever token follows it — the first existing
            // attribute list or the method's first modifier — is re-indented to
            // sit alone on the next line.
            if (method.AttributeLists.Count == 0)
            {
                return method
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                    .WithAttributeLists(SyntaxFactory.SingletonList(list));
            }

            var existing = method.AttributeLists;
            var demoted = existing[0].WithLeadingTrivia(SyntaxFactory.Whitespace(indent));
            return method.WithAttributeLists(existing.Replace(existing[0], demoted).Insert(0, list));
        }

        // True when <paramref name="rewritten"/> does not carry every comment
        // <paramref name="original"/> did. Compared as a multiset of comment texts,
        // so a comment that merely moves is kept and one that vanishes is caught.
        private static bool DropsAComment(SyntaxNode original, SyntaxNode rewritten)
        {
            var kept = CommentsOf(rewritten);
            foreach (string comment in CommentsOf(original))
            {
                if (!kept.Remove(comment))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> CommentsOf(SyntaxNode node)
            => node.DescendantTrivia(descendIntoTrivia: true)
                .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                .Select(trivia => trivia.ToString())
                .ToList();

        private static InvocationExpressionSyntax SendSite(
            InvocationExpressionSyntax invocation, MethodDeclarationSyntax method)
        {
            var arguments = new List<ArgumentSyntax>
            {
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(method.Identifier.ValueText))),
            };

            // The site is only collected once its argument count matches the
            // parameter count and no argument is named or passed by ref, so
            // position maps each argument to its parameter.
            var parameters = method.ParameterList.Parameters;
            var passed = invocation.ArgumentList.Arguments;
            for (int i = 0; i < passed.Count; i++)
            {
                var argument = passed[i].WithoutTrivia();
                arguments.Add(argument.WithExpression(
                    AsWireType(argument.Expression, parameters[i].Type)));
            }

            return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(SendMethodName)),
                    SyntaxFactory.ArgumentList(SeparateWithCommaSpace(arguments)))
                .WithTriviaFrom(invocation);
        }

        // The direct call had the compiler convert each argument to its declared
        // parameter type. `RPC(string, params object[])` accepts anything and boxes
        // it exactly as written, while the receiver binds on the boxed type — so
        // dropping that conversion sends an int to a float parameter as Int32 and
        // the whole call is refused at dispatch, from source that compiles clean.
        // Restating the conversion is what keeps the wire type the declared one.
        // The cast always binds: every parameter type here has already been
        // narrowed to the serializer's closed set of well-known spellings.
        private static ExpressionSyntax AsWireType(ExpressionSyntax argument, TypeSyntax parameterType)
            => SyntaxFactory.CastExpression(
                parameterType.WithoutTrivia(),
                IsPrimaryExpression(argument)
                    ? argument
                    : SyntaxFactory.ParenthesizedExpression(argument));

        // A cast binds tighter than every binary operator, so only an expression
        // that is already primary can carry one without changing what it applies
        // to; everything else is parenthesised first.
        private static bool IsPrimaryExpression(ExpressionSyntax expression)
            => expression is LiteralExpressionSyntax
                or IdentifierNameSyntax
                or MemberAccessExpressionSyntax
                or InvocationExpressionSyntax
                or ElementAccessExpressionSyntax
                or ParenthesizedExpressionSyntax
                or ThisExpressionSyntax;

        // ── Small shared helpers ────────────────────────────────────────────────

        private static bool HasRtmpeRpcAttribute(MethodDeclarationSyntax method)
            => method.AttributeLists.Any(list => list.Attributes.Any(
                a => RightmostName(a.Name) is "RtmpeRpc" or "RtmpeRpcAttribute"));

        private static IEnumerable<SyntaxNode> BodyNodes(MethodDeclarationSyntax method)
        {
            if (method.Body != null)
            {
                return method.Body.DescendantNodes();
            }

            return method.ExpressionBody != null
                ? method.ExpressionBody.Expression.DescendantNodesAndSelf()
                : Enumerable.Empty<SyntaxNode>();
        }

        // The names of every field, event, and property declared on the type
        // AND on any base type declared in the same compilation unit. Inherited
        // protected state is the common shape a NetworkBehaviour subclass
        // mutates, so a base in this file must contribute its members or an
        // unguarded broadcast writing inherited state would slip the audience
        // gate. A base in another file cannot be seen and is a documented limit.
        private static HashSet<string> InstanceStateNames(TypeDeclarationSyntax target)
        {
            var names = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var type in InFileTypeChain(target))
            {
                CollectMemberNames(type, names);
            }

            return names;
        }

        private static void CollectMemberNames(TypeDeclarationSyntax type, HashSet<string> names)
        {
            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        foreach (var declarator in field.Declaration.Variables)
                        {
                            names.Add(declarator.Identifier.ValueText);
                        }

                        break;
                    case EventFieldDeclarationSyntax eventField:
                        foreach (var declarator in eventField.Declaration.Variables)
                        {
                            names.Add(declarator.Identifier.ValueText);
                        }

                        break;
                    case PropertyDeclarationSyntax property:
                        names.Add(property.Identifier.ValueText);
                        break;
                }
            }
        }

        // True when the type itself declares a member (field, property, event,
        // or method) of the given name. Used to detect a shadowing
        // re-declaration of the SDK's IsOwner property directly on the target.
        // Deliberately NOT extended to the in-file base chain: a test or stub
        // that declares the NetworkBehaviour base in the same file exposes the
        // real inherited IsOwner there, and distrusting that would refuse a
        // legitimate guard — the sharp case the review reproduced is a shadow on
        // the target type, which this covers.
        private static bool DeclaresMember(TypeDeclarationSyntax target, string name)
        {
            foreach (var member in target.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field
                        when field.Declaration.Variables.Any(v => v.Identifier.ValueText == name):
                    case EventFieldDeclarationSyntax eventField
                        when eventField.Declaration.Variables.Any(v => v.Identifier.ValueText == name):
                    case PropertyDeclarationSyntax property when property.Identifier.ValueText == name:
                    case MethodDeclarationSyntax method when method.Identifier.ValueText == name:
                        return true;
                }
            }

            return false;
        }

        private static bool WritesMember(ExpressionSyntax written, HashSet<string> members)
        {
            // A deconstruction writes every element of its target tuple, so any
            // member among them makes the whole statement a mutation.
            if (written is TupleExpressionSyntax tuple)
            {
                return tuple.Arguments.Any(a => WritesMember(a.Expression, members));
            }

            var expression = written;
            while (true)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;
                    case CastExpressionSyntax cast:
                        expression = cast.Expression;
                        continue;
                    case ElementAccessExpressionSyntax element:
                        expression = element.Expression;
                        continue;
                    case MemberAccessExpressionSyntax access when access.Expression is ThisExpressionSyntax:
                        return members.Contains(access.Name.Identifier.ValueText);
                    case MemberAccessExpressionSyntax access:
                        expression = access.Expression;
                        continue;
                    case IdentifierNameSyntax identifier:
                        return members.Contains(identifier.Identifier.ValueText);
                    default:
                        return false;
                }
            }
        }

        private static bool AnyDisjunctNegatesOwner(ExpressionSyntax condition, bool bareShadowed)
        {
            if (condition is BinaryExpressionSyntax disjunction
                && disjunction.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return AnyDisjunctNegatesOwner(Unparenthesize(disjunction.Left), bareShadowed)
                    || AnyDisjunctNegatesOwner(Unparenthesize(disjunction.Right), bareShadowed);
            }

            switch (condition)
            {
                case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    return IsOwnerReference(Unparenthesize(not.Operand), bareShadowed);
                case BinaryExpressionSyntax equals when equals.IsKind(SyntaxKind.EqualsExpression):
                    return ComparesOwnerToLiteral(equals, SyntaxKind.FalseLiteralExpression, bareShadowed);
                case BinaryExpressionSyntax notEquals when notEquals.IsKind(SyntaxKind.NotEqualsExpression):
                    return ComparesOwnerToLiteral(notEquals, SyntaxKind.TrueLiteralExpression, bareShadowed);
                default:
                    return false;
            }
        }

        private static bool ComparesOwnerToLiteral(
            BinaryExpressionSyntax comparison, SyntaxKind literal, bool bareShadowed)
        {
            var left = Unparenthesize(comparison.Left);
            var right = Unparenthesize(comparison.Right);
            return (IsOwnerReference(left, bareShadowed) && right.IsKind(literal))
                || (IsOwnerReference(right, bareShadowed) && left.IsKind(literal));
        }

        // A bare `IsOwner` counts as the SDK property only when it is not
        // shadowed by a same-named parameter; `this.IsOwner` is never shadowed
        // by a parameter, so it is accepted regardless (a type-level shadow is
        // ruled out earlier, in HasLeadingOwnerGuard).
        private static bool IsOwnerReference(ExpressionSyntax expression, bool bareShadowed)
            => expression switch
            {
                IdentifierNameSyntax identifier
                    => !bareShadowed && identifier.Identifier.ValueText == OwnerPropertyName,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access
                    => access.Name.Identifier.ValueText == OwnerPropertyName,
                _ => false,
            };

        // Deliberately narrower than the guard recognition in OwnerGuardTransform and
        // OwnerGuardSyntax, which accept a branch that returns after doing
        // non-owner-side work. The gate this feeds asks a stronger question — is
        // every write in this method owner-only — and that work could itself be a
        // write, which under a broadcast audience is executed by every receiving
        // client: exactly the cheat primitive the gate exists to refuse. Requiring
        // the immediate return keeps the shape fail-closed. Do not widen this to
        // match the guard predicates without first excluding the branch's own body
        // from the mutation scan.
        private static bool ReturnsImmediately(StatementSyntax statement)
            => statement switch
            {
                ReturnStatementSyntax => true,
                BlockSyntax block => block.Statements.FirstOrDefault() is ReturnStatementSyntax,
                _ => false,
            };

        private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }

        // The nearest enclosing type of ANY kind — class, struct, record, or
        // interface — not only a class. A nested struct or record can declare a
        // same-named method, so restricting this to classes would let a call
        // inside such a nested type escape the "referenced inside a nested type"
        // guard and be rewritten as if it targeted the outer type.
        private static TypeDeclarationSyntax EnclosingType(SyntaxNode node)
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

        // The type plus every base type resolvable within the same file, in
        // declaration-agnostic order, following the base list by simple name. A
        // base in another file is invisible to a syntax pass and is a documented
        // limitation, not an error.
        private static IEnumerable<TypeDeclarationSyntax> InFileTypeChain(TypeDeclarationSyntax target)
        {
            var byName = TypesDeclaredBeside(target);

            var seen = new HashSet<TypeDeclarationSyntax>();
            var queue = new Queue<TypeDeclarationSyntax>();
            queue.Enqueue(target);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is null || !seen.Add(current))
                {
                    continue;
                }

                yield return current;

                if (current.BaseList is null)
                {
                    continue;
                }

                foreach (var baseType in current.BaseList.Types)
                {
                    string baseName = DeclaredTypeName(baseType.Type);
                    if (baseName != null && byName.TryGetValue(baseName, out var resolved))
                    {
                        queue.Enqueue(resolved);
                    }
                }
            }
        }

        // The parameter's unqualified spelling in the form the classifier keys
        // on: rank-1 arrays render as "element[]"; every other composite shape
        // renders verbatim and lands outside the closed set by construction.
        private static string ParameterTypeName(TypeSyntax type)
        {
            if (type is ArrayTypeSyntax array
                && array.RankSpecifiers.Count == 1
                && array.RankSpecifiers[0].Rank == 1)
            {
                return RightmostName(array.ElementType) + "[]";
            }

            return RightmostName(type);
        }

        // Every type declared in the file holding <paramref name="target"/>, indexed by
        // simple name. The first declaration of a given name wins; a same-name collision
        // across namespaces is already outside what a syntax pass can disambiguate.
        private static Dictionary<string, TypeDeclarationSyntax> TypesDeclaredBeside(
            TypeDeclarationSyntax target)
        {
            var byName = new Dictionary<string, TypeDeclarationSyntax>(System.StringComparer.Ordinal);
            foreach (var type in target.SyntaxTree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!byName.ContainsKey(type.Identifier.ValueText))
                {
                    byName[type.Identifier.ValueText] = type;
                }
            }

            return byName;
        }

        // The simple name under which a base-list entry is declared, which is how the
        // in-file type index is keyed. A constructed generic names one declaration —
        // `Weapon&lt;int&gt;` and `Weapon` resolve to the same `class Weapon&lt;T&gt;` — so the
        // type argument is dropped rather than carried into the lookup, where it would
        // match nothing and drop the base's members from the inherited surface.
        private static string DeclaredTypeName(TypeSyntax type)
            => type switch
            {
                GenericNameSyntax generic => generic.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => DeclaredTypeName(qualified.Right),
                AliasQualifiedNameSyntax aliased => DeclaredTypeName(aliased.Name),
                _ => null,
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

        // Only a conditional boundary makes the rewrite ambiguous; #region and
        // #pragma leave the compiled element set untouched and stay editable.
        private static bool HasDirectiveTrivia(SyntaxNode node)
            => BlockEditing.ContainsConditionalDirectives(node);

        private static bool IsInsideNameOf(SyntaxNode node)
            => node.Ancestors().OfType<InvocationExpressionSyntax>().Any(
                invocation => invocation.Expression is IdentifierNameSyntax name
                    && name.Identifier.ValueText == "nameof");

        private static SeparatedSyntaxList<ArgumentSyntax> SeparateWithCommaSpace(List<ArgumentSyntax> arguments)
        {
            var separators = Enumerable.Repeat(
                SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
                arguments.Count - 1);
            return SyntaxFactory.SeparatedList(arguments, separators);
        }

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
