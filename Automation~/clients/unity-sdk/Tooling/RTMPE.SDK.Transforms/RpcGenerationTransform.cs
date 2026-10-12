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
    /// decided. A method designated <em>replica-apply</em> — one whose body is
    /// meant to run on every receiver, the sender being the authority — is
    /// held at its send sites instead of at its body: every call to it in the
    /// file has to sit under an authority guard, read on the path to the call
    /// (see <see cref="SendSiteOutsideAuthority"/>). Purely syntactic and deliberately conservative: any shape whose
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
        private const string MasterClientPropertyName = "IsMasterClient";
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
                    reason = CollectSendSites(root, target, method, emission.ReplicaApply, out sites);
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

            // The replica-apply designation inverts the question the gate below
            // asks: the body is MEANT to run on every receiver, so what has to be
            // proven is not that the body refuses a non-owner but that the send
            // is issued by the authority — a question about the send sites, which
            // CollectSendSites answers. Two shapes contradict the designation
            // outright and are refused here, before any site is read.
            if (emission.ReplicaApply)
            {
                return RefuseReplicaApplyMethod(method, emission);
            }

            // A broadcast-audience mutator with no leading owner guard executes
            // the write on every receiving client. The gateway admits an Enhanced
            // RPC on an owner-only object — the SDK's default declaration — from
            // the object's owner alone (audit S4-02), which bounds WHO can send
            // it; it does not make the body owner-only where it runs, and on an
            // object declared shared any room member can send it, so the guard
            // stays the body's own. A method may instead declare who may call
            // it — [RtmpeRpc(…, Caller = RpcCaller.Owner)], enforced on every
            // receiver from what the gateway attests — but this pass writes no
            // Caller, and a method already annotated is not one it converts.
            // Server fails closed instead: a receiving client never executes a
            // Server-declared method.
            if (emission.Audience != RpcAudience.Server && !HasLeadingOwnerGuard(method))
            {
                var enclosing = EnclosingType(method);
                if (MutatesInstanceState(method, enclosing))
                {
                    // The Server remedy is named with what it costs: a Server-targeted
                    // RPC runs on no client, the sender included, and on the server
                    // only in the project's server function — so offered bare,
                    // as it was, it read as "make it run once" and made it run nowhere.
                    // The same sentence stands in the wizard, the enum's own doc and
                    // the API reference, held together by TheServerAudienceIsDescribedOnceTests.
                    return "'" + name + "' mutates instance state with audience '" + emission.Audience
                        + "' but no leading owner guard — every receiving client would apply the write;"
                        + " open the method with 'if (!IsOwner) return;', or use RpcTarget.Server — which"
                        + " executes only in the project's server function (an HTTPS endpoint its owner"
                        + " registers in the portal, Project → Server functions); with none registered"
                        + " the send resolves to an unknown"
                        + " method and no client runs the body, the sender included"
                        + ReplicaApplyIsTheThirdWay;
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
                        + " or open the method with 'if (!IsOwner) return;'"
                        + ReplicaApplyIsTheThirdWay;
                }
            }

            return null;
        }

        // A Server audience runs on no replica, and a leading owner guard is
        // the receiver refusing to be a replica: both make the designation a
        // contradiction the author has to resolve, never one the tool resolves.
        private static string RefuseReplicaApplyMethod(MethodDeclarationSyntax method, PlannedRpcEmission emission)
        {
            string name = emission.MethodName;
            if (emission.Audience == RpcAudience.Server)
            {
                return "'" + name + "' is designated replica-apply with audience 'Server' — a Server RPC"
                    + " runs on no replica; name the receivers with Others, All or AllBuffered";
            }

            if (HasLeadingOwnerGuard(method))
            {
                return "'" + name + "' is designated replica-apply but opens with an owner guard — a replica"
                    + " is never the owner, so the guarded body would run on no receiver; drop the guard"
                    + " (the sender's authority is held at the send sites instead), or drop the designation";
            }

            // The same contradiction anywhere in the body: a write that runs only
            // under the authority — after a mid-body `if (!IsOwner) return;`,
            // inside `if (IsOwner) { … }`, under the host — reaches no replica,
            // while the designation says every receiver applies it. Read with the
            // send-site rule's own dominance, so the two cannot disagree about
            // what "only under the authority" means. A guard that skips the
            // OWNER is the other shape and is left alone: it skips the sender's
            // own echo under All and applies everywhere else.
            var type = EnclosingType(method);
            if (type != null)
            {
                var reading = AuthorityReading.Of(type).At(method);
                var members = InstanceStateNames(type);
                foreach (var (node, written) in MemberWritesIn(method, type))
                {
                    if (DominatedByAuthority(node, reading, out _, out _))
                    {
                        int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                        // ⛔ WHICH authority the guard names, asked rather than
                        // assumed. The two are different clients: a replica is
                        // never the owner, but the room's host is a replica of
                        // every object it does not own — so "a replica is never
                        // the owner" is false about a host guard, and an author
                        // reading it goes looking for an owner guard they never
                        // wrote. Asked by narrowing the reading to one authority
                        // at a time, which is the same dominance walk and cannot
                        // disagree with it.
                        string guard;
                        if (DominatedByAuthority(node, reading.OwnerOnly(), out _, out _))
                        {
                            // Under Others no receiver is the owner; under All and
                            // AllBuffered the sender's own echo is one receiver that is.
                            guard = "an owner guard — a replica is never the owner, so "
                                + (emission.Audience == RpcAudience.Others
                                    ? "no receiver applies it"
                                    : "none but the sender's own echo applies it");
                        }
                        else if (DominatedByAuthority(node, reading.HostOnly(), out _, out _))
                        {
                            guard = "a host guard — the room's host is one receiver of the room and need"
                                + " not be the sender, so that client alone applies it"
                                + (emission.Audience == RpcAudience.Others
                                    ? ", and none at all where the sender IS the host"
                                    : string.Empty)
                                + ", and a migration moves which client that is";
                        }
                        else
                        {
                            guard = "an authority guard — neither the owner nor the room's host is every"
                                + " receiver, so the body reaches at most those two clients";
                        }

                        return "'" + name + "' is designated replica-apply but its write to (or call on) '"
                            + WrittenMemberName(written, members) + "' at line " + line + " runs only under "
                            + guard + "; under the designation the body is what every receiver applies: move"
                            + " authority-only work beside the send, or drop the designation";
                    }
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
            bool replicaApply, out List<InvocationExpressionSyntax> sites)
        {
            sites = new List<InvocationExpressionSyntax>();
            string name = method.Identifier.ValueText;

            // What the type shadows is read once per plan; what a member shadows
            // is read at each call, below.
            var authority = replicaApply ? AuthorityReading.Of(target) : default;

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
                    return "'" + name + "' is referenced outside the declaring type — the rewrite cannot prove the binding"
                        + (replicaApply ? ReplicaApplyCallFromElsewhere : string.Empty);
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
                            return "'" + name + "' is accessed through a receiver other than 'this' — the rewrite cannot prove it targets this instance"
                                + (replicaApply ? ReplicaApplyCallOnAnotherInstance : string.Empty);
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

                // ⛔ Directives INSIDE the call, never the trivia leading to
                // it: the rewrite replaces this expression, so what it could
                // unbalance is a region opened or closed between its own
                // tokens. A region that ENDS above the call leaves its `#endif`
                // in the call's leading trivia, and that is the ordinary shape
                // of an editor-only line above an ordinary one — refusing it
                // named a directive the author could not take the call out of.
                if (BlockEditing.ContainsConditionalDirectivesInside(invocation))
                {
                    return "a call to '" + name + "' has #if directive trivia inside it — a rewrite could unbalance the directives";
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

                if (replicaApply)
                {
                    string unguarded = SendSiteOutsideAuthority(invocation, name, authority.At(invocation));
                    if (unguarded != null)
                    {
                        return unguarded;
                    }
                }

                sites.Add(invocation);
            }

            // The designation is a claim about the sends, so a file with none
            // to read makes it a claim about code this pass cannot see — a call
            // written by hand later, or one in another file — and it is refused
            // rather than honoured on trust.
            if (replicaApply && sites.Count == 0)
            {
                return "'" + name + "' is designated replica-apply but nothing in this file calls it — the"
                    + " designation is held at the send sites, and there is none to hold; write the call"
                    + " under an authority guard first, then convert";
            }

            return null;
        }

        // ── The replica-apply rule: a send sits under an authority guard ────────

        /// <summary>
        /// Why <paramref name="invocation"/> is not under an authority guard, or
        /// null when it is. Stated over the <b>path</b> from the body the call
        /// sits in down to the call, never over the names in the body: a guard
        /// counts only where it dominates the call — the call sits in the branch
        /// the guard admits, or a statement before it, in its own block or an
        /// enclosing one, leaves the body unless the authority holds. Negation is
        /// read with its parity, statements in order, and nothing is descended
        /// into, so a guard nested in an untaken <c>if</c>, one placed after the
        /// call, or one written outside the lambda the call sits in does not
        /// hold — on some path the call runs without it.
        /// </summary>
        /// <remarks>
        /// The authority is <c>IsOwner</c> — bare or <c>this.IsOwner</c>, on the
        /// same shadowing terms as <see cref="HasLeadingOwnerGuard"/> — or the
        /// room host, <c>IsMasterClient</c> read through a receiver
        /// (<c>manager.IsMasterClient</c>, <c>NetworkManager.Instance.IsMasterClient</c>):
        /// the SDK declares that property on the manager, so a bare or
        /// <c>this.</c> spelling is a member of the type itself and is not
        /// trusted. Both are trusted by spelling, the trust every guard reading
        /// in this engine extends. A labelled statement in the body refuses the
        /// call outright: a <c>goto</c> enters the path after any guard, so the
        /// order the rule reads is not the order control takes.
        /// </remarks>
        private static string SendSiteOutsideAuthority(
            InvocationExpressionSyntax invocation, string name, AuthorityReading authority)
        {
            int line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            string site = "the call to '" + name + "' at line " + line;

            // A goto cannot cross a lambda or a local function, so the labels
            // that matter are the body's own — not those of a deferred body it
            // declares.
            SyntaxNode owner = BodyOwnerOf(invocation);
            if (owner.DescendantNodes(descendIntoChildren: node =>
                    node == owner
                    || node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .OfType<LabeledStatementSyntax>().Any())
            {
                return site + " sits in a body with a labelled statement — a goto can reach the call"
                    + " around any guard, so the path cannot be read; remove the label, or move the call";
            }

            if (DominatedByAuthority(invocation, authority, out bool insideDeferredBody, out bool guardUnderDirective))
            {
                return null;
            }

            // The file is read in one configuration — the parse's, with no
            // symbol defined — so a guard a directive keeps is not there and one
            // it removes is. Neither is read as a guard, and the refusal says
            // which limit it met: a guard node carrying the directive, or one
            // that survives only as disabled text.
            string directive = guardUnderDirective || DisabledGuardTextIn(owner)
                ? ". A guard under a preprocessor directive is not read — the file is read in one"
                    + " configuration, with no symbol defined, so a guard the directive keeps is not there"
                    + " and one it removes is; move the guard outside the directive"
                : string.Empty;

            if (insideDeferredBody)
            {
                return site + " sits inside a lambda or local function with no authority guard of its"
                    + " own — such a body runs when it is invoked, not where it is written, so a guard"
                    + " around the place it is written does not hold; open that body with"
                    + " 'if (!IsOwner) return;', or put the call inside 'if (IsOwner) { … }' there"
                    + directive + authority.ShadowNote;
            }

            return site + " is not under an authority guard — under replica-apply every receiver applies"
                + " the body, so the sender has to be the authority: put the call inside"
                + " 'if (IsOwner) { … }' or 'if (manager.IsMasterClient) { … }', or open its method with"
                + " 'if (!IsOwner) return;'. The guard is read on the path to the call, so one placed"
                + " after it, one inside another branch, or one whose condition only sometimes implies"
                + " the authority does not count"
                + directive + authority.ShadowNote;
        }

        // The third way out, named on the audience gate's own refusal: the two
        // remedies it offers both defeat a method whose body is MEANT to run on
        // every receiver, and this refusal is the message such a method's author
        // meets first — a readiness advisory they may never have run is not.
        private const string ReplicaApplyIsTheThirdWay =
            " — or, if every receiver is meant to apply it and the sender is the authority,"
            + " designate it replica-apply (the wizard's Applies on receivers box; --replica-apply),"
            + " which holds every call to it in this file under an authority guard instead";

        // The sentence the two outside-reference refusals carry under the
        // designation: the send-site scan rewrites calls on `this` alone, so a
        // class elsewhere asks this one for a public method that holds the
        // guarded call.
        private const string ReplicaApplyCallFromElsewhere =
            "; under replica-apply the guarded call lives in this class — give this class a public"
            + " method that holds 'if (!IsOwner) return;' and the call, and call that from elsewhere";

        // The same shape seen from inside the class: a call on another instance
        // is a send that instance issues, guarded on its own ownership.
        private const string ReplicaApplyCallOnAnotherInstance =
            "; under replica-apply a call on another instance is a send that instance issues — give the"
            + " class a public method that holds 'if (!IsOwner) return;' and the call, and call that on"
            + " the receiver";

        // True when text a directive disabled, in the body the call sits in,
        // reads as a guard on an authority: the guard the author wrote is not in
        // the tree. A comment that merely names the property is not a guard, and
        // must not draw the sentence onto a refusal that is about something else.
        private static bool DisabledGuardTextIn(SyntaxNode owner)
        {
            foreach (var trivia in owner.DescendantTrivia(descendIntoTrivia: true))
            {
                if (trivia.IsKind(SyntaxKind.DisabledTextTrivia)
                    && ReadsAsAGuard(trivia.ToString()))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the other configuration's text holds a guard on an authority.
        /// </summary>
        /// <remarks>
        /// ⛔ Read as CODE, not as characters.  Disabled text is raw text, so a
        /// commented-out guard, a guard quoted in a string and a guard the
        /// directive really removes all look alike to a pattern — and the
        /// sentence this draws tells an author their guard was lost to a
        /// directive, which is a wrong diagnosis on a refusal that is about
        /// something else entirely.  The parser puts a comment in trivia and a
        /// quotation in a literal, so only a real condition answers here.
        ///
        /// <para>The text is a fragment of a body and will not parse cleanly;
        /// that is expected and costs nothing — the parser is error-tolerant,
        /// and a fragment holding no condition simply yields none.</para>
        /// </remarks>
        private static bool ReadsAsAGuard(string disabledText)
        {
            foreach (var node in SyntaxFactory.ParseCompilationUnit(disabledText).DescendantNodes())
            {
                ExpressionSyntax condition = node switch
                {
                    IfStatementSyntax branch    => branch.Condition,
                    WhileStatementSyntax loop   => loop.Condition,
                    ForStatementSyntax counted  => counted.Condition,
                    _ => null,
                };

                if (condition is not null
                    && condition.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(name =>
                        name.Identifier.ValueText == OwnerPropertyName
                        || name.Identifier.ValueText == MasterClientPropertyName))
                {
                    return true;
                }
            }

            return false;
        }

        // The nearest body the call executes in: a lambda, a local function, an
        // accessor or the member itself. A goto cannot cross any of these, so a
        // label outside the body the call sits in cannot reach it.
        private static SyntaxNode BodyOwnerOf(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is AnonymousFunctionExpressionSyntax
                    or LocalFunctionStatementSyntax
                    or AccessorDeclarationSyntax
                    or MemberDeclarationSyntax)
                {
                    return ancestor;
                }
            }

            return node.SyntaxTree.GetRoot();
        }

        // Climbs from the call to the body that owns it, asking at each step
        // whether the structure just entered admits the call only under the
        // authority: the then-branch of an `if` whose condition implies it, the
        // else-branch of one whose falsity does, or a statement list in which an
        // earlier `if` leaves the body on the other outcome. The climb stops at
        // a lambda, a local function, an accessor or the member: what is outside
        // those either runs at another time or is not on the path at all.
        private static bool DominatedByAuthority(
            SyntaxNode call, AuthorityReading authority, out bool insideDeferredBody, out bool guardUnderDirective)
        {
            insideDeferredBody = false;
            guardUnderDirective = false;
            SyntaxNode node = call;
            while (true)
            {
                SyntaxNode parent = node.Parent;
                switch (parent)
                {
                    case null:
                    case MemberDeclarationSyntax:
                    case AccessorDeclarationSyntax:
                        return false;
                    case AnonymousFunctionExpressionSyntax:
                    case LocalFunctionStatementSyntax:
                        insideDeferredBody = true;
                        return false;
                    case IfStatementSyntax branch when node == branch.Statement:
                        if (ImpliesAuthority(branch.Condition, authority) && Holds(branch, call, ref guardUnderDirective))
                        {
                            return true;
                        }

                        break;
                    // A `while` or `for` body runs only after its condition held,
                    // on every iteration; a `do` body runs once before it is
                    // asked, so it is not read here.
                    case WhileStatementSyntax loop when node == loop.Statement:
                        if (ImpliesAuthority(loop.Condition, authority) && Holds(loop, call, ref guardUnderDirective))
                        {
                            return true;
                        }

                        break;
                    case ForStatementSyntax loop when node == loop.Statement && loop.Condition != null:
                        if (ImpliesAuthority(loop.Condition, authority) && Holds(loop, call, ref guardUnderDirective))
                        {
                            return true;
                        }

                        break;
                    case ElseClauseSyntax alternative
                        when node == alternative.Statement && alternative.Parent is IfStatementSyntax owner:
                        if (ImpliedByNoAuthority(owner.Condition, authority) && Holds(owner, call, ref guardUnderDirective))
                        {
                            return true;
                        }

                        break;
                    case BlockSyntax block:
                        if (GuardedBefore(block.Statements, node, call, authority, ref guardUnderDirective))
                        {
                            return true;
                        }

                        break;
                    case SwitchSectionSyntax section:
                        if (GuardedBefore(section.Statements, node, call, authority, ref guardUnderDirective))
                        {
                            return true;
                        }

                        break;
                }

                node = parent;
            }
        }

        // A guard counts only where it is present in every configuration the
        // call is present in; the flag lets the refusal say that this is the
        // limit the call met.
        private static bool Holds(SyntaxNode guard, SyntaxNode call, ref bool guardUnderDirective)
        {
            if (GuardHoldsAcrossDirectives(guard, call))
            {
                return true;
            }

            guardUnderDirective = true;
            return false;
        }

        // Whether the guard is present, in the shape read, in every configuration
        // the call is present in. The file is read in one configuration, so this
        // is decided over the DIRECTIVES between the two, in tree order: any
        // directive inside the guard's own text before the call changes the
        // guard's shape; after the guard's text, an `#endif` no `#if` between
        // the two opened closes a region the guard is in and the call is not.
        // A region opened and closed between the two, a region that encloses
        // both, and a region before the guard or after the call decide nothing
        // about the pair. An `#else`/`#elif` is not read: one parse keeps one
        // branch of a region, so a guard and a call on two sides of a switch
        // are never both nodes of the tree being read.
        private static bool GuardHoldsAcrossDirectives(SyntaxNode guard, SyntaxNode call)
        {
            int ownText = System.Math.Min(GuardOwnTextEnd(guard, call), call.SpanStart);

            // ⛔ Before the balance: what the OTHER configuration puts between
            // the two.  A region that opens and closes between the guard and
            // the call is balanced and reads as deciding nothing — and the text
            // it holds is text this parse does not see.  Where that text
            // carries a brace or a jump, the other configuration is a different
            // tree:
            //
            //     if (IsOwner)
            //     {
            //     #if PREVIEW
            //         Preview();
            //     }
            //     else
            //     {
            //     #endif
            //         CALL;
            //     }
            //
            // parses here as a call under `if (IsOwner)` and compiles there as
            // a call under its `else` — the send every replica applies, issued
            // by everyone BUT the owner.  The same shape reaches a `switch`
            // through a disabled `case`.  A region whose hidden text is
            // ordinary statements (`Debug.Log(…);`) moves nothing and is left
            // alone, which is the case the reading below exists to admit.
            if (DisabledTextMovesTheCall(guard, call, ownText))
            {
                return false;
            }

            int opened = 0;
            for (var directive = guard.SyntaxTree.GetRoot().GetFirstDirective();
                 directive != null;
                 directive = directive.GetNextDirective())
            {
                if (directive.SpanStart < guard.SpanStart)
                {
                    continue;
                }

                if (directive.SpanStart >= call.SpanStart)
                {
                    break;
                }

                if (directive.SpanStart < ownText)
                {
                    return false;
                }

                switch (directive.Kind())
                {
                    case SyntaxKind.IfDirectiveTrivia:
                        opened++;
                        break;
                    case SyntaxKind.EndIfDirectiveTrivia:
                        if (opened == 0)
                        {
                            return false;
                        }

                        opened--;
                        break;
                }
            }

            return true;
        }

        // Whether the text this parse cannot see, between the guard and the
        // call, could put the call somewhere else.
        //
        // ⚠️ Read over TOKENS a statement's place can turn on — a brace, a
        // jump, a label — rather than over what the text means, because what it
        // means is a parse this configuration does not have.  The reading is
        // deliberately one-sided: it refuses on the possibility, and every
        // refusal it makes is a conversion the author can still get by moving
        // the call out of the region.
        private static bool DisabledTextMovesTheCall(SyntaxNode guard, SyntaxNode call, int ownText)
        {
            foreach (var trivia in guard.SyntaxTree.GetRoot().DescendantTrivia(descendIntoTrivia: true))
            {
                if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia)) continue;
                if (trivia.SpanStart < ownText || trivia.SpanStart >= call.SpanStart) continue;

                string hidden = trivia.ToString();
                if (hidden.IndexOf('{') >= 0
                    || hidden.IndexOf('}') >= 0
                    || System.Text.RegularExpressions.Regex.IsMatch(
                        hidden, @"\b(?:case|default|break|continue|goto|return|else)\b"))
                {
                    return true;
                }
            }

            return false;
        }

        // Where the guard's own text ends — the text a directive would change the
        // shape OF, as against the text it merely sits in.
        //
        // ⛔ For a guard the call sits INSIDE, that is everything up to the brace
        // of the limb the call is in: the guarded code is where the call lives,
        // so a `#region`, a `#pragma` or an `#if … #endif` closed above the call
        // are directives in that code rather than in the guard, and the balance
        // rule is what judges them. Reading the whole statement as the guard's
        // own text made every one of those a refusal — of a guard standing
        // directly above the call, with a remedy ("take the call out of the
        // directive") that named nothing the author could move.
        //
        // ⚠️ Up to the BRACE, not to the header: what a directive between the
        // two can change is which statement the call ends up under. `if (A)`
        // followed by a conditional `if (B) { }` and then `else { CALL }` binds
        // the else to A in one configuration and to B in the other, so the
        // guard read here would not be the guard in force — which is the shape
        // the whole-statement reading was protecting against, kept.
        private static int GuardOwnTextEnd(SyntaxNode guard, SyntaxNode call)
        {
            // A guard that LEAVES the body — `if (!IsOwner) return;` above the
            // call — is decisive to its last token: a directive anywhere in it
            // can make the departure conditional.
            if (!guard.Span.Contains(call.Span)) return guard.Span.End;

            SyntaxNode limb = call;
            while (limb != null && limb.Parent != guard) limb = limb.Parent;
            if (limb is ElseClauseSyntax alternative) limb = alternative.Statement;
            if (limb == null) return guard.Span.End;

            return limb is BlockSyntax body ? body.OpenBraceToken.Span.End : limb.SpanStart;
        }

        // True when a statement before `node` in the same list leaves the body
        // unless the authority holds. Only direct siblings are read: a guard
        // nested inside an earlier statement is on that statement's path, not
        // on this one.
        private static bool GuardedBefore(
            SyntaxList<StatementSyntax> statements, SyntaxNode node, SyntaxNode call, AuthorityReading authority,
            ref bool guardUnderDirective)
        {
            int at = node is StatementSyntax statement ? statements.IndexOf(statement) : -1;
            for (int i = 0; i < at; i++)
            {
                if (statements[i] is IfStatementSyntax guard
                    && LeavesUnlessAuthority(guard, authority)
                    && Holds(guard, call, ref guardUnderDirective))
                {
                    return true;
                }
            }

            return false;
        }

        // `if (c) T else E` followed by the call: when T leaves the body the
        // path continues only with ¬c, which has to imply the authority; when E
        // leaves it, only with c.
        private static bool LeavesUnlessAuthority(IfStatementSyntax guard, AuthorityReading authority)
            => (LeavesTheBody(guard.Statement) && ImpliedByNoAuthority(guard.Condition, authority))
                || (guard.Else != null
                    && LeavesTheBody(guard.Else.Statement)
                    && ImpliesAuthority(guard.Condition, authority));

        // A statement control never falls out of: a jump, or a block with a
        // jump among its own statements — anything after that jump in the block
        // is unreachable, so the block as a whole never completes normally. A
        // plain `goto` is not read (its label is refused above); `goto case`
        // leaves the section, which is what matters here.
        private static bool LeavesTheBody(StatementSyntax statement)
            => statement switch
            {
                ReturnStatementSyntax or ThrowStatementSyntax
                    or BreakStatementSyntax or ContinueStatementSyntax => true,
                YieldStatementSyntax yield => yield.IsKind(SyntaxKind.YieldBreakStatement),
                GotoStatementSyntax jump => jump.IsKind(SyntaxKind.GotoCaseStatement)
                    || jump.IsKind(SyntaxKind.GotoDefaultStatement),
                BlockSyntax block => block.Statements.Any(LeavesTheBody),
                _ => false,
            };

        // Two readings of one condition against the authority A — "the sender
        // is the owner, or the room host" — each a proof obligation answered
        // false when it cannot be met, the safe answer since every caller refuses
        // on false. They are each other's negation, which is what carries `!`
        // through: ¬c ⇒ A is exactly ¬A ⇒ c, and ¬A ⇒ ¬c is exactly c ⇒ A. De
        // Morgan carries the two through `&&`/`||`, and `== true` / `!= false` /
        // `?? false` are the spellings of "is true" this engine's samples use.
        // Under the disjunctive A a single atom is implied by nothing weaker
        // than itself, so the second reading answers an atom with false.

        // c ⇒ A: the branch c admits runs only under the authority.
        private static bool ImpliesAuthority(ExpressionSyntax condition, AuthorityReading authority)
        {
            condition = Unparenthesize(condition);
            switch (condition)
            {
                case BinaryExpressionSyntax both
                    when both.IsKind(SyntaxKind.LogicalAndExpression) || both.IsKind(SyntaxKind.BitwiseAndExpression):
                    return ImpliesAuthority(both.Left, authority) || ImpliesAuthority(both.Right, authority);
                case BinaryExpressionSyntax either
                    when either.IsKind(SyntaxKind.LogicalOrExpression) || either.IsKind(SyntaxKind.BitwiseOrExpression):
                    return ImpliesAuthority(either.Left, authority) && ImpliesAuthority(either.Right, authority);
                case BinaryExpressionSyntax lifted
                    when lifted.IsKind(SyntaxKind.CoalesceExpression)
                        && Unparenthesize(lifted.Right).IsKind(SyntaxKind.FalseLiteralExpression):
                    return ImpliesAuthority(lifted.Left, authority);
                case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    return ImpliedByNoAuthority(not.Operand, authority);
                case BinaryExpressionSyntax equals when equals.IsKind(SyntaxKind.EqualsExpression):
                    return ComparesAuthorityTo(equals, SyntaxKind.TrueLiteralExpression, authority);
                case BinaryExpressionSyntax notEquals when notEquals.IsKind(SyntaxKind.NotEqualsExpression):
                    return ComparesAuthorityTo(notEquals, SyntaxKind.FalseLiteralExpression, authority);
                case IsPatternExpressionSyntax pattern:
                    return MatchesAuthorityAgainst(pattern, SyntaxKind.TrueLiteralExpression, authority);
                default:
                    return authority.IsAuthority(condition);
            }
        }

        // ¬A ⇒ c: without the authority, c holds — so a guard that leaves on c
        // leaves every non-authority behind, and an else-branch of c runs only
        // under the authority.
        private static bool ImpliedByNoAuthority(ExpressionSyntax condition, AuthorityReading authority)
        {
            condition = Unparenthesize(condition);
            switch (condition)
            {
                case BinaryExpressionSyntax both
                    when both.IsKind(SyntaxKind.LogicalAndExpression) || both.IsKind(SyntaxKind.BitwiseAndExpression):
                    return ImpliedByNoAuthority(both.Left, authority) && ImpliedByNoAuthority(both.Right, authority);
                case BinaryExpressionSyntax either
                    when either.IsKind(SyntaxKind.LogicalOrExpression) || either.IsKind(SyntaxKind.BitwiseOrExpression):
                    return ImpliedByNoAuthority(either.Left, authority) || ImpliedByNoAuthority(either.Right, authority);
                case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    return ImpliesAuthority(not.Operand, authority);
                case BinaryExpressionSyntax equals when equals.IsKind(SyntaxKind.EqualsExpression):
                    return ComparesAuthorityTo(equals, SyntaxKind.FalseLiteralExpression, authority);
                case BinaryExpressionSyntax notEquals when notEquals.IsKind(SyntaxKind.NotEqualsExpression):
                    return ComparesAuthorityTo(notEquals, SyntaxKind.TrueLiteralExpression, authority);
                case IsPatternExpressionSyntax pattern:
                    return MatchesAuthorityAgainst(pattern, SyntaxKind.FalseLiteralExpression, authority);
                default:
                    return false;
            }
        }

        // `A == literal` / `A != literal`, either way round. ⚠️ Through `?.` the
        // authority is a `bool?`, and null is neither true nor false: `m?.X !=
        // false` holds for a missing manager and `m?.X == false` does not, so a
        // lifted reading is trusted only where the literal is `true` — "is true"
        // (`== true`) and "leaves unless true" (`!= true`) are the two spellings
        // null cannot satisfy in the wrong direction.
        private static bool ComparesAuthorityTo(
            BinaryExpressionSyntax comparison, SyntaxKind literal, AuthorityReading authority)
        {
            var left = Unparenthesize(comparison.Left);
            var right = Unparenthesize(comparison.Right);
            return (LiftedSafely(left, literal) && authority.IsAuthority(left) && right.IsKind(literal))
                || (LiftedSafely(right, literal) && authority.IsAuthority(right) && left.IsKind(literal));
        }

        // `A is true` / `A is false`, on the same lifted terms as `==`.
        private static bool MatchesAuthorityAgainst(
            IsPatternExpressionSyntax pattern, SyntaxKind literal, AuthorityReading authority)
        {
            var subject = Unparenthesize(pattern.Expression);
            return pattern.Pattern is ConstantPatternSyntax constant
                && Unparenthesize(constant.Expression).IsKind(literal)
                && LiftedSafely(subject, literal)
                && authority.IsAuthority(subject);
        }

        private static bool LiftedSafely(ExpressionSyntax operand, SyntaxKind literal)
            => operand is not ConditionalAccessExpressionSyntax || literal == SyntaxKind.TrueLiteralExpression;

        // Which spellings name the authority at one call. `IsOwner` is the SDK
        // property only while nothing shadows it: a member of that name on the
        // type or on an in-file base shadows both spellings, a parameter or
        // local of the member the call sits in shadows the bare one — the terms
        // HasLeadingOwnerGuard reads it on. `IsMasterClient` is the manager's,
        // reached through a receiver that is not `this` in any wrapping (see
        // ThisInstance): the SDK base declares no such member, so a bare, `this.`,
        // `(this).` or cast-of-`this` spelling is the type's own — and once the
        // type declares one, no receiver spelling is trusted either, because a
        // local alias of `this` is a receiver too. ⚠️ That alias is the limit
        // left: `var self = this;` names an ordinary local to a syntax reading,
        // so a member declared in a base OUTSIDE this file and reached through
        // one is trusted by its spelling.
        private readonly struct AuthorityReading
        {
            private readonly TypeDeclarationSyntax _target;
            private readonly bool _ownerShadowedByAMember;
            private readonly bool _masterShadowedByAMember;
            private readonly string _bareOwnerShadow;

            // Which of the two authorities this reading will recognise. Both,
            // for every decision the transform takes; one, where a refusal has
            // to say WHICH guard it found — the two are not the same client, and
            // a message that names the wrong one sends an author looking for a
            // guard they did not write.
            private readonly bool _ignoreOwner;
            private readonly bool _ignoreHost;

            private AuthorityReading(
                TypeDeclarationSyntax target, bool ownerShadowedByAMember, bool masterShadowedByAMember,
                string bareOwnerShadow, bool ignoreOwner = false, bool ignoreHost = false)
            {
                _target = target;
                _ownerShadowedByAMember = ownerShadowedByAMember;
                _masterShadowedByAMember = masterShadowedByAMember;
                _bareOwnerShadow = bareOwnerShadow;
                _ignoreOwner = ignoreOwner;
                _ignoreHost = ignoreHost;
            }

            /// <summary>This reading with only the owner recognised as an authority.</summary>
            public AuthorityReading OwnerOnly()
                => new AuthorityReading(_target, _ownerShadowedByAMember, _masterShadowedByAMember,
                    _bareOwnerShadow, ignoreOwner: false, ignoreHost: true);

            /// <summary>This reading with only the room's host recognised as an authority.</summary>
            public AuthorityReading HostOnly()
                => new AuthorityReading(_target, _ownerShadowedByAMember, _masterShadowedByAMember,
                    _bareOwnerShadow, ignoreOwner: true, ignoreHost: false);

            /// <summary>What the type shadows, read once per plan.</summary>
            public static AuthorityReading Of(TypeDeclarationSyntax target)
                => new AuthorityReading(
                    target,
                    ownerShadowedByAMember: ShadowedByAMember(target, OwnerPropertyName),
                    masterShadowedByAMember: ShadowedByAMember(target, MasterClientPropertyName),
                    bareOwnerShadow: null);

            /// <summary>The reading at one node: what its member shadows, added.</summary>
            public AuthorityReading At(SyntaxNode node)
            {
                SyntaxNode member = node.AncestorsAndSelf().FirstOrDefault(a => a is MemberDeclarationSyntax) ?? _target;
                // ⛔ The narrowing travels.  Dropped here, `OwnerOnly().At(node)`
                // is the full reading again and a refusal asking which authority
                // dominates would answer "the owner" for a host guard —
                // silently, because the sentence it draws is still a true
                // sentence about something.
                return new AuthorityReading(
                    _target, _ownerShadowedByAMember, _masterShadowedByAMember,
                    bareOwnerShadow: LocalShadowWithin(member, OwnerPropertyName),
                    ignoreOwner: _ignoreOwner, ignoreHost: _ignoreHost);
            }

            /// <summary>
            /// The sentence a refusal carries when a spelling it offers is not
            /// one this call can use, so the author is not sent round in a loop.
            /// </summary>
            public string ShadowNote
            {
                get
                {
                    string note = string.Empty;
                    if (_ownerShadowedByAMember)
                    {
                        note += ". This type (or a base in this file) declares its own '" + OwnerPropertyName
                            + "', which shadows the SDK's for this rule — guard on the manager's "
                            + MasterClientPropertyName + ", or rename the member";
                    }
                    else if (_bareOwnerShadow != null)
                    {
                        note += ". In this member a bare '" + OwnerPropertyName + "' is " + _bareOwnerShadow
                            + ", not the SDK property — write 'this." + OwnerPropertyName + "'";
                    }

                    if (_masterShadowedByAMember)
                    {
                        note += ". This type (or a base in this file) declares its own '" + MasterClientPropertyName
                            + "', which shadows the manager's for this rule — guard on " + OwnerPropertyName
                            + ", or rename the member";
                    }

                    return note;
                }
            }

            public bool IsAuthority(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    case IdentifierNameSyntax bare:
                        return !_ignoreOwner && !_ownerShadowedByAMember && _bareOwnerShadow == null
                            && bare.Identifier.ValueText == OwnerPropertyName;
                    case MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }:
                        return false;
                    // ⛔ `this` however it is wrapped — `(this)`, `((Base)this)`,
                    // `(this as IX)`, `this!` — is ThisInstance's one reading, and
                    // it reaches the type's own members: the owner property the
                    // SDK base declares, and never the manager's IsMasterClient,
                    // which that base does not. Read as a receiver instead,
                    // `(this).IsMasterClient` passed as the host guard whenever
                    // the member answering it was declared in a base outside this
                    // file, where the shadow scan cannot see it.
                    case MemberAccessExpressionSyntax own when RTMPE.SDK.Analyzers.ThisInstance.Denotes(own.Expression):
                        return !_ignoreOwner && !_ownerShadowedByAMember
                            && own.Name.Identifier.ValueText == OwnerPropertyName;
                    case MemberAccessExpressionSyntax through:
                        return !_ignoreHost && !_masterShadowedByAMember
                            && through.Name.Identifier.ValueText == MasterClientPropertyName;
                    case ConditionalAccessExpressionSyntax conditional:
                        if (conditional.WhenNotNull is not MemberBindingExpressionSyntax bound)
                            return IsAuthority(conditional.WhenNotNull);
                        // The same split through `?.`: `this?.X` is the type's own X.
                        return RTMPE.SDK.Analyzers.ThisInstance.Denotes(conditional.Expression)
                            ? !_ignoreOwner && !_ownerShadowedByAMember
                                && bound.Name.Identifier.ValueText == OwnerPropertyName
                            : !_ignoreHost && !_masterShadowedByAMember
                                && bound.Name.Identifier.ValueText == MasterClientPropertyName;
                    default:
                        return false;
                }
            }
        }

        // Describes the first same-named local introduction found anywhere in
        // the type — a parameter, local variable, local function, or foreach/
        // pattern/catch/query range variable — or null when the name is free.
        // A declaration node covers pattern/catch designations; the rest carry
        // the name on a token, so each is matched on its kind (the same
        // discipline the NetworkVariable transform uses for its shadow scan).
        private static string LocalShadowOf(ClassDeclarationSyntax target, string name)
            => LocalShadowWithin(target, name);

        // The same reading over one member: what shadows a bare name inside a
        // method is what that method introduces, not what a sibling does.
        private static string LocalShadowWithin(SyntaxNode scope, string name)
        {
            foreach (var node in scope.DescendantNodes())
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
            => method != null && target != null && MemberWritesIn(method, target).Any();

        // Every node in the body that writes a member of the type, with the
        // expression it writes: the same reading MutatesInstanceState answers
        // yes/no from, kept as one enumeration so the two cannot differ.
        private static IEnumerable<(SyntaxNode Node, ExpressionSyntax Written)> MemberWritesIn(
            MethodDeclarationSyntax method, TypeDeclarationSyntax target)
        {
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
                    yield return (node, written);
                }
            }
        }

        // The member a write lands on, by the walk WritesMember takes: the
        // leftmost identifier under any parenthesis, cast, index or `this.` —
        // and in a deconstruction, the first element that IS a member, since a
        // local beside it is not what the refusal is about.
        private static string WrittenMemberName(ExpressionSyntax written, HashSet<string> members)
        {
            if (written is TupleExpressionSyntax tuple)
            {
                foreach (var argument in tuple.Arguments)
                {
                    if (WritesMember(argument.Expression, members))
                    {
                        return WrittenMemberName(argument.Expression, members);
                    }
                }
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
                    case MemberAccessExpressionSyntax access when RTMPE.SDK.Analyzers.ThisInstance.Denotes(access.Expression):
                        return access.Name.Identifier.ValueText;
                    case MemberAccessExpressionSyntax access:
                        expression = access.Expression;
                        continue;
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.ValueText;
                    default:
                        return expression.ToString();
                }
            }
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
                MemberAccessExpressionSyntax access when RTMPE.SDK.Analyzers.ThisInstance.Denotes(access.Expression)
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
            if (type != null && ShadowedByAMember(type, OwnerPropertyName))
            {
                return false; // a member `IsOwner`, here or on an in-file base, shadows even `this.IsOwner`
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

        // True when a member (field, property, event, or method) of the given
        // name is declared on the type or on a base declared in the same file —
        // a shadow of the SDK property one class up is as much a shadow as one
        // on the type itself. The one type left out is the SDK base declared
        // beside the target under its own name: a test or stub that declares
        // `NetworkBehaviour` in the same file exposes the real inherited IsOwner
        // there, and distrusting that would refuse a legitimate guard.
        private static bool ShadowedByAMember(TypeDeclarationSyntax target, string name)
        {
            foreach (var type in InFileTypeChain(target))
            {
                if (type.Identifier.ValueText != SdkBaseTypeName && DeclaresMember(type, name))
                {
                    return true;
                }
            }

            return false;
        }

        // True when the type itself declares a member (field, property, event,
        // or method) of the given name.
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
                    case MemberAccessExpressionSyntax access when RTMPE.SDK.Analyzers.ThisInstance.Denotes(access.Expression):
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
        // shadowed by a same-named parameter; `this.IsOwner` — `this` in any
        // wrapping ThisInstance reads, never `base.` — is never shadowed by a
        // parameter, so it is accepted regardless (a type-level shadow is ruled
        // out earlier, in HasLeadingOwnerGuard).
        private static bool IsOwnerReference(ExpressionSyntax expression, bool bareShadowed)
            => expression switch
            {
                IdentifierNameSyntax identifier
                    => !bareShadowed && identifier.Identifier.ValueText == OwnerPropertyName,
                MemberAccessExpressionSyntax access
                    when access.Expression is not BaseExpressionSyntax
                        && RTMPE.SDK.Analyzers.ThisInstance.Denotes(access.Expression)
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
