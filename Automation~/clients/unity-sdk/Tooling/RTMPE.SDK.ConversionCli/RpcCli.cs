using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Conversion.Core;
using RTMPE.SDK.Transforms;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// The headless Enhanced-RPC generation host (`gen-rpc`): resolve every RPC
    /// on the type through the FNV guard → record provenance in the ledger's
    /// <c>rpcs</c> map → annotate the method and rewrite its send sites →
    /// unified diff → optional apply, sharing the NetworkVariable host's
    /// atomic-write, symlink, and TOCTOU machinery. No id is ever chosen here —
    /// each is the hash the runtime itself derives — and no audience is ever
    /// inferred for a state mutation: an omitted target on a mutating method is
    /// refused, and <c>AllBuffered</c> is only ever accepted from an explicit
    /// designation. Exit codes mirror the NV host: 0 success/no-op, 1 usage, 2
    /// unreadable input or guard/ledger verdict, 3 refusal (the request cannot be
    /// carried out on this input), 4 apply-time safety stop.
    /// </summary>
    public static class RpcCli
    {
        private const string Usage =
            "usage: gen-rpc --file <path.cs> --type <Fully.Qualified.Type> "
            + "--method <Name>[:<All|Others|Server|AllBuffered>] [--method ...] [--apply]";

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            string filePath = null;
            string typeName = null;
            bool apply = false;
            var methods = new List<(string Name, string Audience)>();
            var methodNames = new HashSet<string>(StringComparer.Ordinal);

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
                    case "--method" when CliArguments.HasValue(args, i):
                        string spec = args[++i];
                        int colon = spec.IndexOf(':');
                        var method = colon < 0
                            ? (Name: spec, Audience: (string)null)
                            : (Name: spec.Substring(0, colon), Audience: spec.Substring(colon + 1));
                        if (!methodNames.Add(method.Name))
                        {
                            stderr.WriteLine(
                                "error: duplicate --method '" + method.Name
                                + "' — a method converts exactly once");
                            return 1;
                        }

                        methods.Add(method);
                        break;
                    case "--apply":
                        apply = true;
                        break;
                    default:
                        stderr.WriteLine(Usage);
                        return 1;
                }
            }

            if (filePath == null || typeName == null || methods.Count == 0)
            {
                stderr.WriteLine(Usage);
                return 1;
            }

            if (!CliArguments.TryResolveFullPath(filePath, "--file", stderr, out filePath))
            {
                return 1;
            }

            if (!File.Exists(filePath))
            {
                stderr.WriteLine("error: no such file: " + filePath);
                return 1;
            }

            // One read, decoded once — the transformed text and the guarded bytes
            // must describe the same file.
            if (!CliArguments.TryReadAllBytes(filePath, filePath, stderr, out byte[] sourceSnapshot))
            {
                return 2;
            }

            if (!ConversionCli.TryDecodeUtf8(sourceSnapshot, out string source))
            {
                stderr.WriteLine(ConversionCli.Utf8Refusal(filePath));
                return 2;
            }

            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
            var target = ConversionCli.FindClass(root, typeName);
            if (target is null)
            {
                stderr.WriteLine("error: type '" + typeName + "' not found in " + filePath);
                return 3;
            }

            // An RPC is emitted against the SDK base, so a type with no networked
            // surface would have a wire identity derived for a method that can
            // never be invoked, and a record written for it beside the source —
            // the same reason the conversion host refuses it, asked through the
            // transform that owns the reading.
            var need = RebaseTransform.RebaseNeededBy(target);
            if (need != RebaseTransform.RebaseNeed.No)
            {
                stderr.WriteLine(ConversionCli.RebaseFirstRefusal(
                    typeName, ConversionCli.BaseTypeName(target), need));
                return 3;
            }

            // One derivation for both: the ledger filename and binding key name
            // the type, and so does the wire id — exactly the System.Type.FullName
            // the runtime hashes, which is what makes a recorded id and a computed
            // one the same number.
            string canonicalType = ConversionCli.MetadataNameOf(target);

            var fileName = VariableIdLedger.FileNameFor(canonicalType);
            if (!fileName.IsValid)
            {
                stderr.WriteLine("error: " + fileName.Error);
                return 2;
            }

            string directory = Path.GetDirectoryName(filePath);
            string ledgerPath = Path.GetFullPath(Path.Combine(directory, fileName.FileName));
            if (!string.Equals(Path.GetDirectoryName(ledgerPath), directory, StringComparison.Ordinal))
            {
                stderr.WriteLine("error: the ledger path escapes the source directory");
                return 4;
            }

            byte[] ledgerSnapshot = null;
            if (File.Exists(ledgerPath)
                && !CliArguments.TryReadAllBytes(ledgerPath, fileName.FileName, stderr, out ledgerSnapshot))
            {
                return 2;
            }

            LedgerDocument ledger;
            string oldLedger = string.Empty;
            if (ledgerSnapshot != null)
            {
                if (!ConversionCli.TryDecodeUtf8(ledgerSnapshot, out oldLedger))
                {
                    stderr.WriteLine(ConversionCli.Utf8Refusal(ledgerPath));
                    return 2;
                }

                var parsed = VariableIdLedger.Parse(oldLedger);
                if (!parsed.IsValid)
                {
                    stderr.WriteLine("error: " + fileName.FileName + ": " + parsed.Error);
                    return 2;
                }

                if (!string.Equals(parsed.Document.TypeName, canonicalType, StringComparison.Ordinal))
                {
                    stderr.WriteLine(
                        "error: " + fileName.FileName + " is bound to type '" + parsed.Document.TypeName
                        + "', not '" + canonicalType + "' — refusing to record into another type's ledger");
                    return 2;
                }

                ledger = parsed.Document;
            }
            else
            {
                // About to write the first record for this type. A record for a
                // type nothing here declares, sitting in the same directory, is
                // what a rename leaves behind — and a rename is the one wire
                // break derivation still has, because every identity on the type
                // is a function of its name. Both hosts mint into this directory
                // under one filename scheme, so both owe the same refusal.
                string orphan = ConversionCli.FindOrphanedRecord(directory, out string orphanedType);
                if (orphan != null)
                {
                    stderr.WriteLine(
                        ConversionCli.OrphanedRecordRefusal(orphan, orphanedType, canonicalType));
                    return 3;
                }

                ledger = new LedgerDocument(
                    canonicalType,
                    new SortedDictionary<string, uint>(StringComparer.Ordinal),
                    new SortedDictionary<string, string>(StringComparer.Ordinal));
            }

            // Resolve each designated method's audience through the safe-default
            // policy before any identity work: a state-mutating handler never
            // gets an inferred audience, and the highest-blast one is never
            // reachable without being spelled out.
            var emissions = new List<PlannedRpcEmission>();
            foreach (var (name, audienceSpec) in methods)
            {
                var declarations = target.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.ValueText == name)
                    .ToList();
                if (declarations.Count == 0)
                {
                    stderr.WriteLine("error: method '" + name + "' not found on '" + typeName + "'");
                    return 1;
                }

                // A method the developer already annotated by hand keeps its
                // human-chosen audience: the transform treats it as a no-op, so
                // warn rather than let a requested audience be silently dropped.
                if (declarations.Count == 1 && HasRtmpeRpcAttribute(declarations[0]))
                {
                    string existing = ExistingAudienceOf(declarations[0]);
                    if (audienceSpec != null && !string.Equals(audienceSpec, existing, StringComparison.Ordinal))
                    {
                        stdout.WriteLine(
                            "note: '" + name + "' is already [RtmpeRpc(" + (existing ?? "?")
                            + ")]; the requested '" + audienceSpec + "' is ignored — the tool never"
                            + " rewrites a human-owned audience. Edit the attribute by hand to change it");
                    }
                    else
                    {
                        stdout.WriteLine(
                            "note: '" + name + "' is already [RtmpeRpc]; leaving its audience untouched");
                    }

                    emissions.Add(new PlannedRpcEmission(name, RpcAudience.Server));
                    continue;
                }

                RpcAudience audience;
                if (audienceSpec != null)
                {
                    if (!TryParseAudience(audienceSpec, out audience))
                    {
                        stderr.WriteLine(
                            "error: '" + audienceSpec + "' is not an RpcTarget"
                            + " — use All, Others, Server, or AllBuffered");
                        return 1;
                    }

                    if (audience == RpcAudience.AllBuffered)
                    {
                        stdout.WriteLine(
                            "note: '" + name + "' is AllBuffered — every send is persisted server-side"
                            + " and replayed to every late joiner; this is the highest-cost audience"
                            + " and is honored only because it was designated explicitly");
                    }
                }
                else if (declarations.Count == 1
                    && RpcGenerationTransform.MutatesInstanceState(declarations[0], target))
                {
                    stderr.WriteLine(
                        "error: '" + name + "' mutates instance state, so its audience must be designated"
                        + " explicitly (--method " + name + ":<target>). Server is the audience that does"
                        + " not put an unvalidated write on every client — but it runs only in a backend"
                        + " handler registered for its method id (RegisterServerRpc), and with none"
                        + " registered the body runs on no node at all. Neither answer is free, which is"
                        + " why this one is yours");
                    return 2;
                }
                else
                {
                    audience = RpcAudience.Server;
                    stdout.WriteLine(
                        "note: '" + name + "' audience defaulted to Server (the fail-closed choice);"
                        + " pass --method " + name + ":<target> to choose another");
                }

                // A generated RPC changes what its rewritten call sites do at
                // runtime; the audience determines whether the body runs anywhere
                // at all, so spell out each destination's consequence.
                WarnAboutRuntimeReach(stdout, name, audience, declarations[0]);

                emissions.Add(new PlannedRpcEmission(name, audience));
            }

            // The guard resolves the UNION of the type's RPC surface — every
            // method already carrying [RtmpeRpc] plus the designated ones — so a
            // new method that collides with an existing dispatchable one is
            // refused here, before any diff exists, not at first spawn.
            // One entry PER ANNOTATED DECLARATION, not per name: two same-named
            // [RtmpeRpc] overloads are exactly the intra-type collision the
            // runtime throws on, and collapsing them here would hide it from
            // the guard. A designated method that is already annotated was
            // counted by that scan and is not added twice.
            var plan = new List<PlannedRpc>();
            var annotatedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in target.Members.OfType<MethodDeclarationSyntax>())
            {
                if (HasRtmpeRpcAttribute(method))
                {
                    annotatedNames.Add(method.Identifier.ValueText);
                    plan.Add(new PlannedRpc(canonicalType, method.Identifier.ValueText));
                }
            }

            foreach (var emission in emissions)
            {
                if (!annotatedNames.Contains(emission.MethodName))
                {
                    plan.Add(new PlannedRpc(canonicalType, emission.MethodName));
                }
            }

            var verdict = PlanGuard.ResolveAndValidatePlan(plan);
            if (!verdict.Accepted)
            {
                foreach (var collision in verdict.Collisions)
                {
                    stderr.WriteLine(collision.Kind == CollisionKind.Reserved
                        ? "error: '" + collision.Provenance() + "' resolves to " + collision.IdHex
                            + ", a reserved built-in id — rename the method"
                        : "error: '" + collision.Provenance() + "' resolves to " + collision.IdHex
                            + ", colliding with '" + collision.PriorMethod + "' on the same type");
                }

                return 2;
            }

            var record = RpcIdRecorder.Record(ledger, verdict.Ids);
            foreach (var warning in record.Warnings)
            {
                stdout.WriteLine("warning: " + warning);
            }

            if (!record.Accepted)
            {
                foreach (var error in record.Errors)
                {
                    stderr.WriteLine("error: " + error);
                }

                return 2;
            }

            var newRoot = RpcGenerationTransform.Apply(
                root, target, new RpcPlan(emissions), out string refusal);
            if (refusal != null)
            {
                stderr.WriteLine("refused: " + refusal);
                return 3;
            }

            string newSource = newRoot.ToFullString();
            string newLedger = VariableIdLedger.Serialize(record.UpdatedLedger);

            if (newSource == source && newLedger == oldLedger)
            {
                // `no-op:` is the idempotency marker the Conversion Wizard parses,
                // not prose: without it a re-run reaches the empty-diff branch and
                // is surfaced to the developer as an error.
                stdout.WriteLine("no-op: the RPC conversion is already applied");
                return 0;
            }

            ConversionCli.PrintDiff(stdout, Path.GetFileName(filePath), source, newSource);
            ConversionCli.PrintDiff(stdout, fileName.FileName, oldLedger, newLedger);

            if (!apply)
            {
                stdout.WriteLine("(preview only — pass --apply to write the source and its ledger)");
                return 0;
            }

            int sourceGuard = ConversionCli.GuardBeforeWrite(filePath, sourceSnapshot, "source file", stderr);
            if (sourceGuard != 0)
            {
                return sourceGuard;
            }

            int ledgerGuard = ConversionCli.GuardBeforeWrite(ledgerPath, ledgerSnapshot, "ledger", stderr);
            if (ledgerGuard != 0)
            {
                return ledgerGuard;
            }

            int readable = ConversionCli.GuardLedgerIsReadable(newLedger, fileName.FileName, stderr);
            if (readable != 0)
            {
                return readable;
            }

            // Both read-back guards run before either write, for the reason the NV
            // host gives: a source fault caught after the ledger landed is one the
            // re-run cannot repair, because every host refuses a file that does not
            // parse.
            int parseable = ConversionCli.GuardSourceIsParseable(
                newSource, Path.GetFileName(filePath), stderr);
            if (parseable != 0)
            {
                return parseable;
            }

            // Parsing is the floor. The RPC rewrite injects an attribute, a
            // using and a partner method — each of which parses whether or not
            // the type it names resolves — and the ledger lands first, so a
            // rewrite that does not compile must be refused before it does.
            int compiles = ConversionCli.GuardSourceCompiles(
                source, newSource, Path.GetFileName(filePath), stdout, stderr);
            if (compiles != 0)
            {
                return compiles;
            }

            // Ledger first, exactly as the NV host writes: the provenance record
            // lands before the source that relies on it, so a crash between the two
            // atomic writes is re-adopted, never re-derived against a missing
            // record. Reported apart for the same reason as there — the ordering
            // makes one exit code mean two different states on disk.
            try
            {
                // Staged together, committed ledger-first: the ordering argument
                // above is unchanged, but an ordinary failure now costs nothing
                // on disk because neither target has been touched when it lands.
                ConversionCli.WriteAllAtomically(
                    new[] { (ledgerPath, newLedger), (filePath, newSource) });
            }
            catch (ConversionCli.StagingFailedException ex)
            {
                stderr.WriteLine("error: the write could not be staged, so nothing was changed: "
                    + ex.Message);
                return 4;
            }
            catch (ConversionCli.CommitFailedException ex)
            {
                // A failed RENAME — the narrow window the split cannot close.
                stderr.WriteLine(
                    "error: the staged files were not all committed: " + ex.Message
                    + " — the ledger is committed first and may already hold its new contents"
                    + " and any recorded ids stay valid; "
                    + ConversionCli.RecoveryAfter(ex, "re-run --apply"));
                return 4;
            }

            stdout.WriteLine("applied: " + Path.GetFileName(filePath) + " + " + fileName.FileName);
            return 0;
        }

        // The four runtime names, matched exactly: Enum.TryParse would also
        // accept a raw number or any casing, which silently turns a typo into a
        // different audience.
        private static bool TryParseAudience(string spec, out RpcAudience audience)
        {
            switch (spec)
            {
                case "All":
                    audience = RpcAudience.All;
                    return true;
                case "Others":
                    audience = RpcAudience.Others;
                    return true;
                case "Server":
                    audience = RpcAudience.Server;
                    return true;
                case "AllBuffered":
                    audience = RpcAudience.AllBuffered;
                    return true;
                default:
                    audience = default;
                    return false;
            }
        }

        // Spells out where a generated RPC's body actually runs, so a converted
        // method that silently does nothing at runtime is disclosed at author
        // time rather than discovered in play-testing. Enhanced-RPC delivery
        // (SDK RpcTarget + gateway fan-out): Server is consumed by a backend
        // handler, never a client; a broadcast RPC guarded by IsOwner runs only
        // where the guard passes — nowhere for Others (every receiver is a
        // non-owner), and only on the owning sender for All.
        private static void WarnAboutRuntimeReach(
            TextWriter stdout, string name, RpcAudience audience, MethodDeclarationSyntax method)
        {
            // RPC(string, params object[]) boxes each argument by its own static
            // type and the serializer dispatches on that boxed type, so a direct
            // call's implicit widening (byte / short / char / enum to int) would
            // be lost. The transform restates the declared parameter type as a
            // cast on every call site it rewrites; a call site in another file, or
            // one written by hand later, is outside a single-compilation-unit
            // pass. It cannot know whether such a call site exists, so it says so.
            if (method.ParameterList.Parameters.Count > 0)
            {
                stdout.WriteLine(
                    "note: '" + name + "' takes arguments — the call sites rewritten in this file carry"
                    + " an explicit cast to each parameter's type. A call site this pass did not rewrite"
                    + " (another file, or a hand-written this.RPC(...)) boxes the argument as its own"
                    + " static type; an implicitly-narrowed one (byte/short/char/enum to int, or"
                    + " int/uint to ulong) fails to serialize at send — cast to the parameter type there");
            }

            bool guarded = RpcGenerationTransform.HasLeadingOwnerGuard(method);
            switch (audience)
            {
                case RpcAudience.Server:
                    stdout.WriteLine(
                        "note: '" + name + "' is a Server RPC — the send is routed to a backend handler you"
                        + " must register server-side (e.g. RegisterServerRpc); with none registered the"
                        + " call is a no-op, and its rewritten call site no longer runs the body locally");
                    break;
                case RpcAudience.Others when guarded:
                    stdout.WriteLine(
                        "note: '" + name + "' targets Others but opens with an owner guard — Others excludes"
                        + " the sender and every receiver is a non-owner, so the guarded body runs on no"
                        + " client. Drop the guard or reconsider the audience");
                    break;
                case RpcAudience.All when guarded:
                    stdout.WriteLine(
                        "note: '" + name + "' targets All with an owner guard — only the owning sender runs"
                        + " the body, after a server round-trip; the direct local call was replaced by the send");
                    break;
            }
        }

        // The RpcTarget member named inside an existing [RtmpeRpc(RpcTarget.X)]
        // annotation, or null when the attribute takes the default audience.
        private static string ExistingAudienceOf(MethodDeclarationSyntax method)
        {
            foreach (var list in method.AttributeLists)
            {
                foreach (var attribute in list.Attributes)
                {
                    if (RightmostName(attribute.Name) is not ("RtmpeRpc" or "RtmpeRpcAttribute"))
                    {
                        continue;
                    }

                    var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
                    if (argument?.Expression is MemberAccessExpressionSyntax access)
                    {
                        return access.Name.Identifier.ValueText;
                    }

                    return null; // default-audience ctor, no argument
                }
            }

            return null;
        }

        private static bool HasRtmpeRpcAttribute(MethodDeclarationSyntax method)
            => method.AttributeLists.Any(list => list.Attributes.Any(a =>
                RightmostName(a.Name) is "RtmpeRpc" or "RtmpeRpcAttribute"));

        private static string RightmostName(NameSyntax name)
            => name switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                _ => name?.ToString(),
            };
    }

    internal static class CollisionFormatting
    {
        /// <summary>The <c>"TypeName.MethodName"</c> string a collision was derived from.</summary>
        public static string Provenance(this Collision collision)
            => collision.TypeName + "." + collision.MethodName;
    }
}
