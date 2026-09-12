using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Analyzers;
using RTMPE.SDK.Transforms;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// The headless host for the three identity-free conversions (completion-plan
    /// W3): Rebase (RTMPE2001), Owner guard (RTMPE2003), and the
    /// <c>base.OnDestroy()</c> lifecycle chain (RTMPE1020). It fronts the same
    /// <c>RTMPE.SDK.Transforms</c> engines the IDE code-fixes call, so a fix done
    /// here and the same fix done by a Rider/VS lightbulb are byte-identical by
    /// construction. No identity (<c>variableId</c>, RPC id) is ever touched —
    /// these edits are additive and rename-free, which is why this host carries
    /// no ledger. Exit codes mirror the sibling hosts: 0 success/no-op, 1 usage,
    /// 2 environment, 3 transform refusal, 4 apply-time failure.
    /// </summary>
    public static class FixCli
    {
        private const string Usage =
            "usage: fix --file <path.cs> --type <Fully.Qualified.Type> "
            + "--kind <rebase|owner-guard|base-ondestroy> [--method <FrameLoop>] [--apply]";

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            string filePath = null;
            string typeName = null;
            string kind = null;
            string method = null;
            bool apply = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--file" when CliArguments.HasValue(args, i):
                        filePath = args[++i];
                        break;
                    case "--type" when CliArguments.HasValue(args, i):
                        typeName = args[++i];
                        break;
                    case "--kind" when CliArguments.HasValue(args, i):
                        kind = args[++i];
                        break;
                    case "--method" when CliArguments.HasValue(args, i):
                        method = args[++i];
                        break;
                    case "--apply":
                        apply = true;
                        break;
                    default:
                        stderr.WriteLine(Usage);
                        return 1;
                }
            }

            if (filePath == null || typeName == null
                || (kind != "rebase" && kind != "owner-guard" && kind != "base-ondestroy"))
            {
                stderr.WriteLine(Usage);
                return 1;
            }

            if (!CliArguments.TryResolveFullPath(filePath, "--file", stderr, out filePath))
            {
                return 1;
            }

            // A named source that is not there is an argument fault, the same one the
            // four sibling --file hosts report, and it carries their code so a caller
            // branching on the exit status reads one answer across the verb set. The
            // read below still guards the faults only a read can find.
            if (!File.Exists(filePath))
            {
                stderr.WriteLine("error: no such file: " + filePath);
                return 1;
            }

            // The bytes as the plan read them: the apply-time guard compares
            // against these to establish that nothing moved underneath the run.
            string source;
            if (!CliArguments.TryReadAllBytes(filePath, filePath, stderr, out byte[] sourceSnapshot))
            {
                return 2;
            }

            if (!ConversionCli.TryDecodeUtf8(sourceSnapshot, out source))
            {
                stderr.WriteLine(ConversionCli.Utf8Refusal(filePath));
                return 2;
            }

            var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

            // The same by-name resolution the NetworkVariable host uses, so the two
            // verbs agree on what `--type` means for a syntax-only, single-file host.
            var target = ConversionCli.FindClass(root, typeName);
            if (target == null)
            {
                stderr.WriteLine("error: type '" + typeName + "' not found in " + filePath);
                return 3;
            }

            string rewritten;
            string refusal = null;
            bool onDestroyMayHideHook = false;
            switch (kind)
            {
                case "rebase":
                    rewritten = RebaseTransform.Apply(root, target, out refusal).ToFullString();
                    break;

                case "owner-guard":
                {
                    // RTMPE2003's shape: the guard fences a Unity frame loop —
                    // Update, FixedUpdate or LateUpdate, because owned state moves
                    // in the last two as readily as in the first and a host that
                    // knew only `Update` had nothing to offer the other two.
                    var loops = target.Members.OfType<MethodDeclarationSyntax>()
                        .Where(UnityFrameLoops.IsFrameLoopCandidate)
                        .ToList();
                    if (loops.Count == 0)
                    {
                        stderr.WriteLine("error: '" + typeName + "' declares no frame loop to guard — "
                            + "the guard fences " + string.Join(", ", UnityFrameLoops.Names));
                        return 3;
                    }

                    string declared = string.Join(", ",
                        loops.Select(loop => loop.Identifier.ValueText));

                    if (method != null)
                    {
                        loops = loops
                            .Where(loop => loop.Identifier.ValueText == method)
                            .ToList();
                        if (loops.Count == 0)
                        {
                            stderr.WriteLine("error: '" + typeName + "' declares no frame loop named '"
                                + method + "' — it declares " + declared);
                            return 3;
                        }
                    }

                    // ⛔ One loop, never the set. The guard belongs in the loop that
                    // drives this object's own state, and RTMPE2003 reports each
                    // loop on its own evidence for a reason: a LateUpdate that only
                    // draws must run on every client, and fencing it is the one edit
                    // that takes the HUD down for everybody who is not the owner.
                    // This host reads a single file with no semantic model, so it
                    // cannot tell the two apart — it asks instead of guessing, and
                    // the diagnostic the operator is acting on already names the loop.
                    if (loops.Count > 1)
                    {
                        stderr.WriteLine("error: '" + typeName + "' declares " + loops.Count
                            + " frame loops (" + declared + ") — name the one RTMPE2003 reported,"
                            + " with --method <name>. The guard belongs only in the loop that drives"
                            + " this object's own state; fencing a loop that only draws stops it"
                            + " running for every client that is not the owner.");
                        return 3;
                    }

                    // `IsOwner` arrives from the SDK base, so a type that has no
                    // networked surface cannot carry this guard however it came to
                    // lack one. Asked through the transform that owns the reading,
                    // so this arm and the conversion hosts cannot disagree about
                    // one base list — and the remedy travels with the cause,
                    // because the rebase swaps a base and does not invent one.
                    var need = RebaseTransform.RebaseNeededBy(target);
                    if (need != RebaseTransform.RebaseNeed.No)
                    {
                        stderr.WriteLine("error: "
                            + ConversionCli.WhatItInheritsFrom(
                                typeName, ConversionCli.BaseTypeName(target))
                            + ", so the guard's inherited 'IsOwner' is not in scope — "
                            + ConversionCli.RemedyFor(need));
                        return 3;
                    }

                    var fenced = loops[0];
                    rewritten = root.ReplaceNode(
                        fenced, OwnerGuardTransform.Apply(fenced, out refusal)).ToFullString();
                    break;
                }

                default: // base-ondestroy
                {
                    var onDestroy = TargetMethod(target, "OnDestroy");
                    if (onDestroy == null)
                    {
                        stderr.WriteLine("error: '" + typeName + "' has no OnDestroy method to chain");
                        return 3;
                    }

                    // Whether a base type that is present actually declares the hook
                    // is beyond a single-file syntactic host; that its absence makes
                    // `base.OnDestroy()` uncompilable is not, so the certain case is
                    // refused and the rest is left to the compiler.
                    if (target.BaseList is null)
                    {
                        stderr.WriteLine("error: '" + typeName + "' declares no base type — "
                            + "there is no OnDestroy to chain to");
                        return 3;
                    }

                    rewritten = root.ReplaceNode(onDestroy, BaseOnDestroyTransform.Apply(onDestroy, out refusal)).ToFullString();

                    // ⚠️ The declaration is left as written, and where it merely
                    // HIDES the inherited hook that leaves CS0114 standing on every
                    // later compile — correct at runtime (Unity dispatches the
                    // message by name to the most derived declaration, which now
                    // chains) and wrong as C#. The IDE fix repairs it, because the
                    // analyzer that raises RTMPE1020 has the semantic model.
                    //
                    // 🔑 This host has no such model: it reads ONE file, so it can
                    // see that `override` is absent but not whether adding it would
                    // compile — against a non-virtual base hook that is CS0506, and
                    // against a base that declares no hook at all there is nothing
                    // hidden and no CS0114 either. So the note SAYS what it does not
                    // know rather than prescribing an edit that may not build. An
                    // explicit `new` is excluded outright: that is the author
                    // stating the hide is deliberate, and it suppresses CS0114.
                    onDestroyMayHideHook =
                        !onDestroy.Modifiers.Any(SyntaxKind.OverrideKeyword)
                        && !onDestroy.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && !onDestroy.Modifiers.Any(SyntaxKind.NewKeyword);

                    break;
                }
            }

            if (onDestroyMayHideHook)
            {
                stdout.WriteLine(
                    "note: '" + typeName + ".OnDestroy' carries no 'override', so it hides whatever "
                    + "the base declares under that name — CS0114 where that hook is virtual, CS0108 "
                    + "where it is not. Only the first is repairable, by declaring "
                    + "'protected override void OnDestroy()'; against a non-virtual hook that same edit "
                    + "is CS0506. This host reads one file and cannot tell them apart, so it changes "
                    + "nothing here: the IDE quick fix for RTMPE1020 decides it from the whole "
                    + "compilation.");
            }

            if (rewritten == source)
            {
                // The transform distinguishes the two honest readings of an
                // unchanged output, so this host no longer has to offer both: a
                // stated reason is a refusal the developer must act on, and its
                // absence is idempotence. Reporting a refusal as a no-op would
                // leave an error diagnostic standing with nothing said about why
                // the fix that answers it did nothing.
                if (refusal != null)
                {
                    stderr.WriteLine("refused: " + kind + " on '" + typeName + "' — " + refusal);
                    return 3;
                }

                stdout.WriteLine("no-op: the engine made no change for " + kind + " on '" + typeName
                    + "' — already converted.");
                return 0;
            }

            ConversionCli.PrintDiff(stdout, Path.GetFileName(filePath), source, rewritten);

            if (!apply)
            {
                stdout.WriteLine("(preview only — pass --apply to write the file)");
                return 0;
            }

            // The same gate the two identity-allocating verbs pass before they
            // persist: a reparse point is refused rather than written through —
            // File.Move would replace the link and leave its target untouched
            // while the run reports success — and bytes that changed since the
            // plan read them invalidate the diff the operator approved.
            int guard = ConversionCli.GuardBeforeWrite(filePath, sourceSnapshot, "source file", stderr);
            if (guard != 0)
            {
                return guard;
            }

            // The same read-back guard the identity-allocating verbs pass: a
            // rewritten tree is re-parsed before it is allowed to reach disk. This
            // host writes one file, so the ordering argument the others make does
            // not apply — the guard is here because "the rewriter cannot emit a
            // broken tree" is a claim, and the point of a last line is to hold
            // where the claim does not.
            int parseable = ConversionCli.GuardSourceIsParseable(
                rewritten, Path.GetFileName(filePath), stderr);
            if (parseable != 0)
            {
                return parseable;
            }

            // Parsing is the floor. This host writes one file and allocates no
            // identity, so a bad write here is recoverable — but `base.OnDestroy()`
            // against a base that never declares it, and an owner guard reaching an
            // `IsOwner` that is not in scope, both PARSE and neither compiles.
            // Those are exactly this host's two transforms.
            int compiles = ConversionCli.GuardSourceCompiles(
                source, rewritten, Path.GetFileName(filePath), stdout, stderr);
            if (compiles != 0)
            {
                return compiles;
            }

            try
            {
                ConversionCli.WriteAtomically(filePath, rewritten);
            }
            catch (ConversionCli.CommitFailedException e)
            {
                // One rename, and it failed — so the file is untouched. What may
                // not be untouched is the directory it was staged in, and a temp
                // that could not be cleared is what blocks the re-run.
                stderr.WriteLine("error: apply failed for " + filePath + ": " + e.Message
                    + " — " + ConversionCli.RecoveryAfter(e, "re-run --apply"));
                return 4;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                stderr.WriteLine("error: apply failed for " + filePath + ": " + e.Message);
                return 4;
            }

            stdout.WriteLine("applied: " + kind + " on '" + typeName + "' (" + filePath + ")");
            return 0;
        }

        // The class's own parameterless method of that name — declarations of
        // nested types are excluded so a nested helper's Update can never be
        // edited on behalf of the enclosing component (the analyzers' nested-leak
        // rule), and an overload with parameters is not the Unity message this
        // host may edit (`Update(float)` is an ordinary method).
        // 🔑 Arity is part of a C# signature, so a GENERIC declaration of the name
        // is a different member: `OnDestroy<T>()` hides nothing, Unity cannot
        // dispatch to a definition needing type arguments, and `Update<T>()` is
        // not the frame loop. The analyzer that raises RTMPE1020 stopped treating
        // one as the destroy hook; this host resolves the target by name and
        // parameter count alone, so without the same test it kept rewriting the
        // shape the analyzer had just stopped reporting — and printing a note
        // about a hide that is not happening.
        private static MethodDeclarationSyntax TargetMethod(ClassDeclarationSyntax target, string name)
            => target.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(method => method.Identifier.ValueText == name
                    && method.ParameterList.Parameters.Count == 0
                    && method.TypeParameterList is null);
    }
}
