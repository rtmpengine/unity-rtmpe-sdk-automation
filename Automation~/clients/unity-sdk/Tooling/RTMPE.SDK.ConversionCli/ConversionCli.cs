using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Analysis;
using RTMPE.SDK.Conversion.Core;
using RTMPE.SDK.Transforms;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// The headless allocation host (§6.6): detection → plan → unified diff →
    /// optional apply of the source file and its id ledger. Each file is written
    /// atomically (temp then move), the ledger first, so a crash between the two
    /// leaves the durable id record ahead of the source that cites it. This is the
    /// surface DD-P3-8's "run the conversion tool" message refers to, and the
    /// executable proof of the host contract until the Unity wizard wraps the same
    /// calls. Exit codes: 0 success/no-op, 1 usage, 2 unreadable input or
    /// allocator verdict, 3 refusal (the request cannot be carried out on this
    /// input), 4 apply-time safety stop — nothing has been written unless the
    /// message says which file was, which only the source write after a
    /// successful ledger write can produce.
    /// </summary>
    public static class ConversionCli
    {
        private const string Usage =
            "usage: convert --file <path.cs> --type <Fully.Qualified.Type> "
            + "--member <field>[:<companionName>] [--member ...] "
            + "[--apply]";

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            string filePath = null;
            string typeName = null;
            bool apply = false;
            var members = new List<(string Field, string Companion)>();
            // Every member name this run will occupy on the type — the fields it
            // converts and the companions it mints — mapped to how it was claimed,
            // so a collision can say which side already holds the name.
            var claimedNames = new Dictionary<string, string>(StringComparer.Ordinal);

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
                    case "--member" when CliArguments.HasValue(args, i):
                        if (!TryClaimMember(args[++i], claimedNames, stderr, out var member))
                        {
                            return 1;
                        }

                        members.Add(member);
                        break;
                    case "--apply":
                        apply = true;
                        break;
                    default:
                        stderr.WriteLine(Usage);
                        return 1;
                }
            }

            if (filePath == null || typeName == null || members.Count == 0)
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

            // One read, decoded once: the text that is transformed and the bytes
            // the apply-time guard compares against must describe the same file,
            // and a second read could not promise that.
            if (!CliArguments.TryReadAllBytes(filePath, filePath, stderr, out byte[] sourceSnapshot))
            {
                return 2;
            }

            if (!TryDecodeUtf8(sourceSnapshot, out string source))
            {
                stderr.WriteLine(Utf8Refusal(filePath));
                return 2;
            }

            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
            var target = FindClass(root, typeName);
            if (target is null)
            {
                stderr.WriteLine("error: type '" + typeName + "' not found in " + filePath);
                return 3;
            }

            int allocated = AllocateForType(
                target, typeName, Path.GetDirectoryName(filePath), members,
                stdout, stderr, out var allocation);
            if (allocated != 0)
            {
                return allocated;
            }

            var newRoot = NetworkVariableGenerationTransform.Apply(
                root, target, new ConversionPlan(allocation.Conversions), out string refusal);
            if (refusal != null)
            {
                stderr.WriteLine("refused: " + refusal);
                return 3;
            }

            string ledgerPath = allocation.LedgerPath;
            byte[] ledgerSnapshot = allocation.LedgerSnapshot;
            string oldLedger = allocation.OldLedgerText;
            string newSource = newRoot.ToFullString();
            string newLedger = allocation.NewLedgerText;

            if (newSource == source && newLedger == oldLedger)
            {
                // `no-op:` is the idempotency marker the Conversion Wizard parses,
                // not prose: without it a re-run reaches the empty-diff branch and
                // is surfaced to the developer as an error.
                stdout.WriteLine("no-op: the conversion is already applied");
                return 0;
            }

            PrintDiff(stdout, Path.GetFileName(filePath), source, newSource);
            PrintDiff(stdout, allocation.LedgerFileName, oldLedger, newLedger);

            if (!apply)
            {
                stdout.WriteLine("(preview only — pass --apply to write the source and its ledger)");
                return 0;
            }

            // Apply-time safety, on both files the run persists: never write through
            // a symlink, and re-confirm neither file moved under the plan between it
            // and this approval — the source is guarded exactly as the ledger is.
            int sourceGuard = GuardBeforeWrite(filePath, sourceSnapshot, "source file", stderr);
            if (sourceGuard != 0)
            {
                return sourceGuard;
            }

            int ledgerGuard = GuardBeforeWrite(ledgerPath, ledgerSnapshot, "ledger", stderr);
            if (ledgerGuard != 0)
            {
                return ledgerGuard;
            }

            int readable = GuardLedgerIsReadable(newLedger, allocation.LedgerFileName, stderr);
            if (readable != 0)
            {
                return readable;
            }

            // Both read-back guards run before either write, so a failure of either
            // one costs a refusal and leaves the tree untouched. Ordering them after
            // the writes would make the source guard report a state it could no
            // longer restore — the ledger would already be on disk.
            int parseable = GuardSourceIsParseable(newSource, Path.GetFileName(filePath), stderr);
            if (parseable != 0)
            {
                return parseable;
            }

            // Parsing is the floor, not the ceiling. Held HERE — beside its
            // sibling and ahead of the ledger write — because the ledger is
            // written first: an id recorded against a file that will not compile
            // is a state no re-run can repair.
            int compiles = GuardSourceCompiles(
                source, newSource, Path.GetFileName(filePath), stdout, stderr);
            if (compiles != 0)
            {
                return compiles;
            }

            // The ledger is the durable identity record the source cites, so it is
            // written first: a crash between the two atomic writes leaves the issued
            // id recorded ahead of the field that uses it, and a re-run re-adopts
            // that id rather than minting a divergent one. The two writes are
            // reported apart because that ordering makes the same exit code mean two
            // different states on disk, and only the message can tell them apart.
            try
            {
                // Both files are staged — written, flushed to the device — before
                // either is made visible. The ledger is still committed first, so
                // the ordering argument above survives; what changes is that the
                // whole cost of an ordinary failure (unwritable file, full disk,
                // stale temp) is now paid while NEITHER target has been touched.
                WriteAllAtomically(new[] { (ledgerPath, newLedger), (filePath, newSource) });
            }
            catch (StagingFailedException ex)
            {
                stderr.WriteLine("error: the write could not be staged, so nothing was changed: "
                    + ex.Message);
                return 4;
            }
            catch (CommitFailedException ex)
            {
                // Reached only if a RENAME failed — the small window the split
                // narrows but cannot close. The ledger is committed first, so it
                // may already be visible while the source is not; naming that
                // state is the difference between an operator who re-runs and one
                // who starts looking for corruption.
                stderr.WriteLine(
                    "error: the staged files were not all committed: " + ex.Message
                    + " — " + allocation.LedgerFileName + " may already hold its new contents"
                    + " and any recorded ids stay valid; " + RecoveryAfter(ex, "re-run --apply"));
                return 4;
            }

            stdout.WriteLine("applied: " + Path.GetFileName(filePath) + " + " + allocation.LedgerFileName);
            return 0;
        }

        /// <summary>
        /// Parses one <c>--member</c> specification and claims the names it puts
        /// on the type, reporting to <paramref name="stderr"/> and returning
        /// false when it may not be claimed.
        /// </summary>
        /// <remarks>
        /// The companion half is the one name in this host that no source
        /// declaration vouches for — the field is resolved against the file, but
        /// the companion is minted from the argument. It becomes both a C#
        /// identifier in the rewrite and a key in the ledger, so an unchecked one
        /// writes source that does not compile and a sidecar the parser will
        /// refuse, leaving the type with no readable identity record. It is
        /// checked here, before anything is planned.
        ///
        /// A companion is a new member declared on the same type, so it occupies
        /// that type's namespace exactly as a converted field does. Two requests
        /// naming the same one emit two declarations of it and record it in the
        /// ledger once — a file that no longer compiles, written and exited 0. A
        /// companion colliding with a name the type already carries is the
        /// transform's own refusal; this covers the names only this run knows
        /// about.
        ///
        /// 🔑 <paramref name="claimedNames"/> is one type's namespace, so the
        /// batch verb gives each type its own — two types in one file may each
        /// carry a member of the same name, and refusing that would refuse an
        /// ordinary file.
        /// </remarks>
        internal static bool TryClaimMember(
            string spec, Dictionary<string, string> claimedNames, TextWriter stderr,
            out (string Field, string Companion) member)
        {
            int colon = spec.IndexOf(':');
            member = colon < 0
                ? (Field: spec, Companion: (string)null)
                : (Field: spec.Substring(0, colon), Companion: spec.Substring(colon + 1));

            foreach (string name in new[] { member.Field, member.Companion })
            {
                if (name != null && !VariableIdLedger.IsValidMemberName(name))
                {
                    stderr.WriteLine(
                        "error: '" + name + "' is not a valid C# identifier — "
                        + "--member takes <field>[:<companionName>]");
                    return false;
                }
            }

            foreach (var (name, claim) in new[]
            {
                (member.Field, "converts"),
                (member.Companion, "mints"),
            })
            {
                if (name is null)
                {
                    continue;
                }

                if (claimedNames.TryGetValue(name, out string priorClaim))
                {
                    stderr.WriteLine(
                        "error: duplicate --member name '" + name + "' — this run already "
                        + priorClaim + " it; each member of the type is named once");
                    return false;
                }

                claimedNames[name] = claim;
            }

            return true;
        }

        /// <summary>
        /// One type's ledger resolution and id allocation — everything between
        /// "this is the type" and "this is the plan, and this is the ledger text
        /// it produces".
        /// </summary>
        /// <remarks>
        /// 🔑 Shared rather than reimplemented. The batch verb converts several
        /// types in one decision, and every one of them needs exactly this: the
        /// canonical name, the sidecar path, and the derivation's verdict. A
        /// second copy would be a second set of rules about wire identity, and
        /// the two would drift in the direction nobody tests.
        /// </remarks>
        internal static int AllocateForType(
            ClassDeclarationSyntax target, string typeName, string directory,
            IReadOnlyList<(string Field, string Companion)> members,
            TextWriter stdout, TextWriter stderr, out TypeAllocation allocation)
        {
            allocation = null;

            // The ledger is keyed on the type's metadata name — generic arity and
            // nested '+' included — so the headless host and the IDE fix, which
            // reads it from the compiled symbol, resolve the same sidecar.
            string canonicalType = MetadataNameOf(target);

            var fileName = VariableIdLedger.FileNameFor(canonicalType);
            if (!fileName.IsValid)
            {
                stderr.WriteLine("error: " + fileName.Error);
                return 2;
            }

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

            LedgerDocument ledger = null;
            string oldLedger = string.Empty;
            if (ledgerSnapshot != null)
            {
                if (!TryDecodeUtf8(ledgerSnapshot, out oldLedger))
                {
                    stderr.WriteLine(Utf8Refusal(ledgerPath));
                    return 2;
                }

                var parsed = VariableIdLedger.Parse(oldLedger);
                if (!parsed.IsValid)
                {
                    stderr.WriteLine("error: " + fileName.FileName + ": " + parsed.Error);
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
                // is a function of its name.
                string orphan = FindOrphanedRecord(directory, out string orphanedType);
                if (orphan != null)
                {
                    stderr.WriteLine(OrphanedRecordRefusal(orphan, orphanedType, canonicalType));
                    return 3;
                }
            }

            // Build the derivation's view of this file: every this-owned
            // NetworkVariable construction, plus the requested new members.
            var constructions = ScanConstructions(target, canonicalType);
            var newMembers = new List<string>();
            var planned = new List<PlannedConversion>();
            foreach (var (field, companion) in members)
            {
                var declaration = target.Members.OfType<FieldDeclarationSyntax>()
                    .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == field));
                if (declaration is null)
                {
                    stderr.WriteLine("error: field '" + field + "' not found on '" + typeName + "'");
                    return 1;
                }

                string declaredType = TypeName(declaration.Declaration.Type);
                if (!NetworkVariableTypeMap.TryMap(declaredType, out string nvType))
                {
                    // An already-converted field carries the NetworkVariable type
                    // itself: keep it in the plan so the run resolves to a no-op
                    // instead of an error — re-running must be provably safe.
                    if (declaredType.StartsWith("NetworkVariable", StringComparison.Ordinal))
                    {
                        nvType = declaredType;
                    }
                    else
                    {
                        // ⚠️ This is the refusal an AUTHOR meets — it returns before
                        // the transform is reached, so the transform's own sentence
                        // for the same fault is seen only through the library API.
                        // Both read one remedy so they cannot drift apart.
                        stderr.WriteLine(
                            "error: field '" + field + "' has type '" + declaration.Declaration.Type
                            + "', which the closed map does not cover ("
                            + NetworkVariableTypeMap.SupportedTypeList + ") — "
                            + NetworkVariableTypeMap.DeclareYourOwnVariableRemedy);
                        return 2;
                    }
                }

                newMembers.Add(companion ?? field);
                planned.Add(new PlannedConversion(
                    field, nvType,
                    companion is null ? ConversionArm.InPlace : ConversionArm.Companion, companion));
            }

            string baseName = BaseTypeName(target);

            // A type that inherits a networked surface must be rebased before it
            // is converted, so `base.OnNetworkSpawn()` is chained rather than
            // silently skipped. ⛔ Unrelated to identity: two readings decide it,
            // and RebaseTransform owns both, so the hosts and the transform cannot
            // disagree about one base list.
            var need = RebaseTransform.RebaseNeededBy(target);
            if (need != RebaseTransform.RebaseNeed.No)
            {
                stderr.WriteLine(RebaseFirstRefusal(typeName, baseName, need));
                return 3;
            }

            var verdict = VariableIdDerivation.Plan(new DerivationRequest(
                canonicalType, constructions, newMembers,
                ledger?.Rpcs));

            if (!verdict.Accepted)
            {
                foreach (var error in verdict.Errors)
                {
                    stderr.WriteLine("error: " + error.Message);
                }

                return 2;
            }

            // A member's identity is derived from the DECLARING type here and
            // from the CONCRETE type at run time, and those agree for every shape
            // but one: a name a base already spends is spent again by this type,
            // and on an instance of this type the two fold to a single id. The
            // second registration throws out of OnNetworkSpawn and the object is
            // destroyed rather than spawned, so the record about to be written
            // would name identities no instance of this type can carry.
            //
            // Asked by putting both sides into one request under THIS type's
            // scope — the scope the run time will use — so the derivation's own
            // collision reporter answers it, rather than a second rule that would
            // then have to be held in agreement with the first.
            var inherited = InheritedConstructions(target);
            if (inherited.Count > 0)
            {
                var acrossTheChain = VariableIdDerivation.Plan(new DerivationRequest(
                    canonicalType,
                    constructions.Concat(inherited).ToList(),
                    newMembers,
                    ledger?.Rpcs));

                if (!acrossTheChain.Accepted)
                {
                    foreach (var error in acrossTheChain.Errors)
                    {
                        stderr.WriteLine("error: " + error.Message);
                    }

                    stderr.WriteLine(
                        "error: a member named above is inherited, and this file declares the base"
                        + " it comes from. An identity is derived from the concrete type at run"
                        + " time, so a name a base spends cannot be spent again by a type that"
                        + " inherits it: rename one of the two members, or give one construction a"
                        + " distinct nameof(...).");
                    return 2;
                }
            }

            allocation = new TypeAllocation(
                canonicalType,
                fileName.FileName,
                ledgerPath,
                ledgerSnapshot,
                oldLedger,
                VariableIdLedger.Serialize(verdict.Record),
                planned);
            return 0;
        }

        // The apply-time guard for one of the two files the run persists: refuse
        // (4) a reparse point or a file whose bytes changed since the plan read
        // them, and return 0 when the write may proceed. A null snapshot means the
        // file was absent at plan time, so it must still be absent now.
        internal static int GuardBeforeWrite(string path, byte[] planned, string label, TextWriter stderr)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                stderr.WriteLine("error: the " + label + " is a symlink — refusing to write through it");
                return 4;
            }

            return GuardPlanStillStands(path, planned, label, stderr);
        }

        /// <summary>
        /// Refuses (4) a file whose bytes no longer match what the plan was made
        /// against, and returns 0 when they do.
        /// </summary>
        /// <remarks>
        /// 🔑 A different question from "may I write here", and asked of a wider
        /// set. Every file the plan READ is a file the plan depends on, whether or
        /// not this run replaces it: a ledger decided from a source that has since
        /// changed records ids for text that no longer exists, and the source is
        /// not among the targets that would catch it.
        /// </remarks>
        internal static int GuardPlanStillStands(string path, byte[] planned, string label, TextWriter stderr)
        {
            byte[] current = null;
            if (File.Exists(path) && !CliArguments.TryReadAllBytes(path, label, stderr, out current))
            {
                return 4;
            }

            if (!BytesEqual(current, planned))
            {
                stderr.WriteLine("error: the " + label + " changed while planning — re-run to re-plan");
                return 4;
            }

            return 0;
        }


        // ⛔ `GuardBaseLedgersStillStand` stood here, and its subject no longer
        // exists. It re-confirmed that a BASE type's identity record still held
        // the bytes a derived type's allocation was decided against — a question
        // only an allocator has, because only an allocator asks which numbers are
        // free. An identity is derived from the declaring type and the member's
        // own name now, so a derived type reads nothing from its bases and the
        // list it was handed had been unconditionally empty since: a guard
        // iterating over nothing refuses nothing, and one that cannot refuse is
        // worse than absent, because it reads as coverage.

        internal static string BaseTypeName(ClassDeclarationSyntax target)
        {
            var first = target.BaseList?.Types.FirstOrDefault()?.Type;
            return first switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                _ => first?.ToString(),
            };
        }

        // One wording for a refusal two hosts issue, so the instruction a developer
        // is given cannot differ by which verb they happened to reach it through.
        //
        // 🔑 Two causes, two remedies, and the difference is not cosmetic: the
        // rebase verb SWAPS a MonoBehaviour base and refuses a type that has none,
        // because adding one would change what the type is rather than how it
        // replicates. Sending a base-less type there would answer a dead end with
        // another one — which is the whole defect this refusal exists to end.
        internal static string RebaseFirstRefusal(
            string typeName, string baseName, RebaseTransform.RebaseNeed need)
            => "error: " + WhatItInheritsFrom(typeName, baseName)
                + ", so it inherits none of the surface a conversion emits — " + RemedyFor(need);

        /// <summary>
        /// How an author is told to give a type a networked surface, in the one
        /// place it is said. Keyed on the reading that fired rather than on the
        /// message that reports it: the rebase SWAPS a MonoBehaviour base and
        /// refuses a type that has none, so a caller choosing its own wording ends
        /// half its authors at a second refusal — which every host that reaches
        /// this state did, each in its own words, until they asked here instead.
        /// </summary>
        internal static string RemedyFor(RebaseTransform.RebaseNeed need)
            => need switch
            {
                RebaseTransform.RebaseNeed.SwapMonoBehaviour
                    => "rebase it onto NetworkBehaviour first (fix --kind rebase), then re-run",
                RebaseTransform.RebaseNeed.ForeignNetworkBehaviour
                    => "that NetworkBehaviour is not RTMPE's — this conversion targets "
                        + "RTMPE.Core.NetworkBehaviour only, and a spawn hook emitted onto another "
                        + "framework's base is never called; move the type onto the SDK's base "
                        + "(and `using RTMPE.Core;`) first, then re-run",
                _ => "give it a ': NetworkBehaviour' base (the rebase verb swaps a MonoBehaviour base,"
                    + " it does not add one), then re-run",
            };

        /// <summary>The cause, in the words every host reporting it uses.</summary>
        internal static string WhatItInheritsFrom(string typeName, string baseName)
            => "'" + typeName + "' "
                + (baseName is null ? "declares no base type" : "derives from '" + baseName + "'");

        // The located type's metadata name — namespaces joined by '.', containing
        // types by '+', each generic arity as a `n suffix — computed from syntax to
        // match the spelling the IDE fix derives from the compiled symbol, so both
        // hosts key the ledger's filename and type binding identically.
        internal static string MetadataNameOf(ClassDeclarationSyntax target)
        {
            string name = target.Identifier.ValueText + ArityBacktick(target.TypeParameterList);
            foreach (var ancestor in target.Ancestors())
            {
                switch (ancestor)
                {
                    case TypeDeclarationSyntax type:
                        name = type.Identifier.ValueText + ArityBacktick(type.TypeParameterList) + "+" + name;
                        break;
                    case BaseNamespaceDeclarationSyntax ns:
                        name = NamespaceText(ns.Name) + "." + name;
                        break;
                }
            }

            return name;
        }

        // A namespace's name, read as IDENTIFIERS rather than as source text.
        // `ToString()` returns the span verbatim: it keeps a verbatim `@` prefix,
        // a `\uXXXX` escape and any interior trivia — none of which appear in the
        // metadata name the runtime reports, so a namespace spelled `@event` or
        // written across two lines would derive an identity no build computes and
        // a filename no type accounts for.
        private static string NamespaceText(NameSyntax name)
            => name switch
            {
                QualifiedNameSyntax qualified
                    => NamespaceText(qualified.Left) + "." + NamespaceText(qualified.Right),
                AliasQualifiedNameSyntax alias => NamespaceText(alias.Name),
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => name.ToString(),
            };

        internal static string ArityBacktick(TypeParameterListSyntax typeParameters)
        {
            int arity = typeParameters?.Parameters.Count ?? 0;
            return arity > 0
                ? "`" + arity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
        }


        // The fallback claim test, used only when a sidecar's content cannot be
        // read: its filename stem is derived from the owning type, so a type
        // declared here that would produce this exact name accounts for it.
        /// <summary>
        /// Reports a record in <paramref name="directory"/> whose bound type no
        /// source file there declares, or <c>null</c> when every record is
        /// claimed.
        /// </summary>
        /// <remarks>
        /// 🔑 A record is discovered by a filename derived from its type's own
        /// metadata name, so renaming the type — or moving its file — hides the
        /// record rather than invalidating it. Under the allocator this mattered
        /// because a fresh record would reissue ids a shipped peer had burned;
        /// that reasoning is gone with the allocator, and none of it is true now.
        /// What survives is the EVIDENCE: an abandoned record beside a type being
        /// recorded for the first time is what a rename looks like from here, and
        /// a rename changes every identity on the type — the one break derivation
        /// does not remove. Nothing else in the toolchain reports it.
        ///
        /// <para>Claim is decided by CONTENT, not by filename, so several types
        /// sharing a folder is never mistaken for an orphan: a record is claimed
        /// when some <c>.cs</c> file in the directory declares its type. An
        /// unreadable record falls back to the filename, which is narrower than
        /// it looks — the stem comes from the owning type, so a declared type
        /// still accounts for the file, and a record nothing accounts for is
        /// exactly the shape a rename leaves.</para>
        /// </remarks>
        internal static string FindOrphanedRecord(string directory, out string orphanedType)
        {
            orphanedType = null;
            string[] sidecars;
            string[] sources;
            try
            {
                sidecars = Directory.GetFiles(directory, "*" + VariableIdLedger.FileSuffix);
                sources = Directory.GetFiles(directory, "*.cs");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null; // an unreadable directory is the caller's own read's problem
            }

            if (sidecars.Length == 0)
            {
                return null;
            }

            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (string sourcePath in sources)
            {
                // A glob is a filter, not a guarantee, and this half fails the
                // more dangerous way: a `.cs.tmp` admitted here has its types
                // counted as declared, which ACCOUNTS FOR a record and so
                // withdraws the refusal.
                if (!Path.GetFileName(sourcePath).EndsWith(".cs", StringComparison.Ordinal))
                {
                    continue;
                }

                string text;
                try
                {
                    // Lenient by design, and the one place that is: this scan
                    // reads for evidence of a declaration and writes nothing, so
                    // byte fidelity is not at stake, while a substituted
                    // character in a comment cannot change which types a file
                    // declares. Reading strictly would drop a neighbour's
                    // declaration and turn its live record into a phantom orphan.
                    text = DecodeUtf8Lenient(File.ReadAllBytes(sourcePath));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var type in CSharpSyntaxTree.ParseText(text)
                             .GetCompilationUnitRoot()
                             .DescendantNodes()
                             .OfType<ClassDeclarationSyntax>())
                {
                    declared.Add(MetadataNameOf(type));
                }
            }

            Array.Sort(sidecars, StringComparer.Ordinal); // a deterministic first report
            foreach (string sidecarPath in sidecars)
            {
                if (!Path.GetFileName(sidecarPath).EndsWith(
                        VariableIdLedger.FileSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string text;
                bool readable;
                try
                {
                    readable = TryDecodeUtf8(File.ReadAllBytes(sidecarPath), out text);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    readable = false;
                    text = null;
                }

                var parsed = readable ? VariableIdLedger.Parse(text) : null;
                if (parsed is null || !parsed.IsValid)
                {
                    if (ClaimedByFileName(sidecarPath, declared))
                    {
                        continue;
                    }

                    return Path.GetFileName(sidecarPath);
                }

                if (!declared.Contains(parsed.Document.TypeName))
                {
                    orphanedType = parsed.Document.TypeName;
                    return Path.GetFileName(sidecarPath);
                }
            }

            return null;
        }

        internal static string OrphanedRecordRefusal(
            string sidecarFileName, string orphanedType, string canonicalType)
            => orphanedType is null
                ? "error: about to write a first identity record for '" + canonicalType + "', but '"
                    + sidecarFileName + "' in the same directory cannot be read.\n"
                    + "A record beside a type being recorded for the first time is what a rename"
                    + " leaves behind, and this host cannot tell whose it is — repair or remove it,"
                    + " then re-run."
                : "error: about to write a first identity record for '" + canonicalType + "', but '"
                + sidecarFileName + "' in the same directory records identities for '" + orphanedType
                + "', a type no source file here declares.\n"
                + "If '" + orphanedType + "' became '" + canonicalType
                + "', every identity on it changed with the name — that is a wire break, and every"
                + " peer needs rebuilding together. Delete the stale record once you have"
                + " acknowledged that.\n"
                + "If that type is genuinely gone, delete its record to confirm it.";

        private static bool ClaimedByFileName(string sidecarPath, IEnumerable<string> declared)
        {
            string fileName = Path.GetFileName(sidecarPath);
            foreach (string type in declared)
            {
                var candidate = VariableIdLedger.FileNameFor(type);
                if (candidate.IsValid
                    && string.Equals(candidate.FileName, fileName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }




        /// <summary>
        /// The class declaration <paramref name="requestedName"/> names, or
        /// <c>null</c> when this file declares no such type.
        /// </summary>
        /// <remarks>
        /// 🔑 A candidate is matched on its own METADATA NAME — the same name the
        /// wire identity is derived from — so what the caller resolved and what
        /// the ids are computed for cannot be two different types. The previous
        /// form matched a simple name against the enclosing NAMESPACES only,
        /// which ignored containing types: given a nested <c>Combat.Vehicle.Turret</c>
        /// declared above a top-level <c>Combat.Turret</c>, <c>--type Combat.Turret</c>
        /// resolved to the nested one, and the host then recorded ids for it and
        /// rewrote its call sites while reporting success. Nested types were also
        /// unreachable: no spelling of <c>--type</c> could name one.
        ///
        /// <para>Nesting is accepted in either spelling — the metadata <c>+</c>
        /// or the <c>.</c> a caller naturally writes — because both name one type
        /// unambiguously and refusing the second would be a trap rather than a
        /// safeguard. Two declarations can still match: partials of one type,
        /// which ARE one type and derive one identity, so the first is returned
        /// rather than treated as a conflict.</para>
        ///
        /// <para>⚠️ The arity suffix is optional, in a SECOND pass. A caller
        /// naming a generic type writes <c>Holder</c>, not <c>Holder`1</c>, and
        /// the previous form accepted that because it matched a bare identifier —
        /// so requiring the backtick would refuse a spelling that has always
        /// worked. Exact names are resolved first, so a non-generic
        /// <c>Holder</c> declared beside <c>Holder&lt;T&gt;</c> still wins its own
        /// name instead of the arity-stripped fallback taking it.</para>
        /// </remarks>
        internal static ClassDeclarationSyntax FindClass(CompilationUnitSyntax root, string requestedName)
        {
            var candidates = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();

            foreach (var candidate in candidates)
            {
                string metadataName = MetadataNameOf(candidate);
                if (metadataName == requestedName
                    || metadataName.Replace('+', '.') == requestedName)
                {
                    return candidate;
                }
            }

            foreach (var candidate in candidates)
            {
                string withoutArity = StripArity(MetadataNameOf(candidate));
                if (withoutArity == requestedName
                    || withoutArity.Replace('+', '.') == requestedName)
                {
                    return candidate;
                }
            }

            return null;
        }

        // A metadata name with each `n arity suffix removed — the spelling a
        // caller writes for a generic type.
        private static string StripArity(string metadataName)
        {
            int backtick = metadataName.IndexOf('`');
            if (backtick < 0) return metadataName;

            var stripped = new System.Text.StringBuilder(metadataName.Length);
            for (int i = 0; i < metadataName.Length; i++)
            {
                if (metadataName[i] != '`')
                {
                    stripped.Append(metadataName[i]);
                    continue;
                }

                i++;
                while (i < metadataName.Length && char.IsDigit(metadataName[i])) i++;
                i--;
            }

            return stripped.ToString();
        }

        // Every NetworkVariable constructed on its own `this` by a base this
        // file declares, keyed by a name no member of the target can carry: the
        // plan is keyed by member name, and a base and a derived type spending
        // one name is precisely the collision being asked about.
        private static List<SourceConstruction> InheritedConstructions(ClassDeclarationSyntax target)
        {
            var inherited = new List<SourceConstruction>();
            foreach (var declaration in NetworkVariableGenerationTransform.VisibleDeclarationsOf(target))
            {
                if (declaration == target)
                {
                    continue;
                }

                string declaringName = declaration.Identifier.ValueText;
                foreach (var creation in declaration.DescendantNodes()
                             .OfType<BaseObjectCreationExpressionSyntax>())
                {
                    if (creation.FirstAncestorOrSelf<TypeDeclarationSyntax>() != declaration
                        || !ConstructsAWrapper(creation, declaration))
                    {
                        continue;
                    }

                    string member = MemberAssignedFrom(creation);
                    var arguments = creation.ArgumentList?.Arguments ?? default;
                    string named = NameArgumentTextOf(arguments);

                    // A name this tool cannot read belongs to the run that
                    // converts the base. Carried here it would raise an
                    // unreadable-name error against a member the caller did not
                    // name and cannot edit from this invocation.
                    if (member is null || named is null)
                    {
                        continue;
                    }

                    bool ownerIsThis =
                        arguments.Count > 0 && arguments[0].Expression is ThisExpressionSyntax;
                    inherited.Add(new SourceConstruction(
                        declaringName, declaringName + "." + member, ownerIsThis, named));
                }
            }

            return inherited;
        }

        // Every this-owned NetworkVariable construction in the file, as the
        // allocator's SourceConstruction: assignments and declarator initializers
        // whose created type name starts with "NetworkVariable".
        private static List<SourceConstruction> ScanConstructions(
            ClassDeclarationSyntax target, string typeName)
        {
            var constructions = new List<SourceConstruction>();
            foreach (var creation in target.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                // ⛔ Only what THIS declaration constructs. A nested type declares
                // its own members on its own object, so its `this` is not this
                // type's — charging its constructions here records another type's
                // member under this one's identity space. `IdArgumentsByMember`
                // has said so since it was written; this scanner did not, and the
                // asymmetry decided the recorded name.
                if (creation.FirstAncestorOrSelf<TypeDeclarationSyntax>() != target)
                {
                    continue;
                }

                if (!ConstructsAWrapper(creation, target))
                {
                    continue;
                }

                string member = MemberAssignedFrom(creation);
                if (member is null)
                {
                    continue;
                }

                var arguments = creation.ArgumentList?.Arguments ?? default;
                bool ownerIsThis = arguments.Count > 0 && arguments[0].Expression is ThisExpressionSyntax;
                constructions.Add(new SourceConstruction(
                    typeName, member, ownerIsThis, NameArgumentTextOf(arguments)));
            }

            return constructions;
        }

        // `Ns.Outer+Base`1` → `Base`. The ledger records a fully-qualified
        // metadata name; the source shows an identifier.
        //
        // ⚠️ Both separators. Namespaces join with '.' and nested types with
        // '+' — the same spelling MetadataNameOf writes — so splitting on the
        // dot alone reduces a nested base to `Outer+Base`, which matches no
        // identifier any source can spell and refuses every type derived from
        // one.
        private static string SimpleTypeName(string metadataName)
        {
            int lastSeparator = metadataName.LastIndexOfAny(new[] { '.', '+' });
            string simple = lastSeparator < 0 ? metadataName : metadataName.Substring(lastSeparator + 1);
            return BareTypeName(simple);
        }

        // `Base<T>` and `Base`1` → `Base`.
        private static string BareTypeName(string name)
        {
            int arity = name.IndexOfAny(new[] { '`', '<' });
            return arity < 0 ? name : name.Substring(0, arity);
        }

        // Which member a construction is bound to. The three spellings a
        // conversion can leave behind — a plain assignment, a `this.`-qualified
        // one, and a declarator's initialiser — and no fourth: a construction
        // nothing binds is not a member's id and must not be read as one.
        internal static string MemberAssignedFrom(BaseObjectCreationExpressionSyntax creation)
            => creation.Parent switch
            {
                AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } => left.Identifier.ValueText,
                AssignmentExpressionSyntax
                {
                    Left: MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access,
                } => access.Name.Identifier.ValueText,
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => declarator.Identifier.ValueText,
                _ => null,
            };

        // Whether a construction is of a runtime wrapper, by the closed set the
        // runtime declares. 🚨 This was `StartsWith("NetworkVariable")` over the
        // spelled type, which minted an identity for a user type named
        // `NetworkVariableRegistry` and could not see the one spelling the
        // declared Unity floor admits and the prefix has no type for: a
        // target-typed `new(this, nameof(_hp))`, whose type is the member's own
        // declaration. That construction constructs exactly what the explicit
        // one does and was leaving the ledger.
        private static bool ConstructsAWrapper(BaseObjectCreationExpressionSyntax creation, ClassDeclarationSyntax target)
        {
            string spelled = creation switch
            {
                ObjectCreationExpressionSyntax explicitNew => TypeName(explicitNew.Type),
                ImplicitObjectCreationExpressionSyntax => DeclaredTypeNameOf(MemberAssignedFrom(creation), target),
                _ => null,
            };

            return NetworkVariableWrappers.IsWrapperTypeName(spelled);
        }

        // The declared type of the member a target-typed construction is bound
        // to — the field or property this declaration spells with that name.
        private static string DeclaredTypeNameOf(string member, ClassDeclarationSyntax target)
        {
            if (member is null)
            {
                return null;
            }

            foreach (var field in target.Members.OfType<FieldDeclarationSyntax>())
            {
                if (field.Declaration.Variables.Any(v => v.Identifier.ValueText == member))
                {
                    return TypeName(field.Declaration.Type);
                }
            }

            foreach (var property in target.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (property.Identifier.ValueText == member)
                {
                    return TypeName(property.Type);
                }
            }

            return null;
        }

        // Which argument carries the id, asked once. A reader that answers it and
        // a writer that answers it again are two rules that agree until one of
        // them is edited, and the two of them disagreeing is a renumber written
        // over the wrong argument.
        /// <summary>
        /// The expression a construction derives its identity from: the
        /// <c>memberName</c> argument, named or in position.
        /// </summary>
        internal static ExpressionSyntax NameArgumentOf(SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            foreach (var argument in arguments)
            {
                if (argument.NameColon?.Name.Identifier.ValueText == "memberName")
                {
                    return argument.Expression;
                }
            }

            return arguments.Count >= 2 && arguments[1].NameColon is null
                ? arguments[1].Expression
                : null;
        }

        // Every NetworkVariable construction the type declares, by member name,
        // paired with the expression that gives its id. The renumbering hosts
        // need the nodes themselves; everything else needs only the value
        // ScanConstructions reads off them.
        //
        // ⛔ ALL of them, never the last one seen. A member constructed twice —
        // a field initialiser and a spawn-hook assignment is the ordinary
        // shape, and the one RTMPE1011 exists to flag — has two ids in the
        // build, and a renumber that moved one of them would leave the other
        // holding the value the ledger just gave away.
        //
        // ⛔ And only what THIS declaration body constructs. A nested type
        // declares its own members and owns its own record; rewriting a literal
        // inside one because its member name matches would move an id no ledger
        // read here accounts for.
        internal static Dictionary<string, List<ExpressionSyntax>> IdArgumentsByMember(
            ClassDeclarationSyntax target)
        {
            var byMember = new Dictionary<string, List<ExpressionSyntax>>(StringComparer.Ordinal);
            foreach (var creation in target.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (!ConstructsAWrapper(creation, target))
                {
                    continue;
                }

                if (creation.FirstAncestorOrSelf<TypeDeclarationSyntax>() != target)
                {
                    continue;
                }

                string member = MemberAssignedFrom(creation);
                if (member is null)
                {
                    continue;
                }

                var argument = NameArgumentOf(creation.ArgumentList?.Arguments ?? default);
                if (argument == null)
                {
                    continue;
                }

                if (!byMember.TryGetValue(member, out var arguments))
                {
                    byMember[member] = arguments = new List<ExpressionSyntax>();
                }

                arguments.Add(argument);
            }

            return byMember;
        }

        /// <summary>
        /// The name a construction derives its identity from, or null when the
        /// argument is neither <c>nameof(...)</c> nor a string literal.
        /// </summary>
        /// <remarks>
        /// Both spellings are read because both are what an author may have
        /// written: the toolchain emits <c>nameof</c>, and the package's own
        /// getting-started page has always shown hand-written constructions.
        /// Reading only the emitted one would report a hand-written type as
        /// unreadable and refuse a file nothing is wrong with.
        /// </remarks>
        private static string NameArgumentTextOf(SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            ExpressionSyntax expression = NameArgumentOf(arguments);

            if (expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText;
            }

            return expression is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax callee
                && callee.Identifier.ValueText == "nameof"
                && invocation.ArgumentList.Arguments.Count == 1
                && invocation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax named
                    ? named.Identifier.ValueText
                    : null;
        }

        // 🔴 This used to be a second copy of the transform's spelling, and the two
        // had drifted: only this one stripped generic arity, so the transform
        // resolved an already-converted `NetworkVariableList<int>` differently from
        // the plan this method built and refused what should have been a no-op.
        // One statement now, in the transform, which this project already
        // references.
        private static string TypeName(TypeSyntax type)
            => NetworkVariableGenerationTransform.DeclaredTypeName(type);

        // A minimal LCS line diff — enough for the human approval read; ledgers
        // and single files are small, so the quadratic table is irrelevant.
        // The lines between the shared head and tail, which is the whole of what the
        // diff has to reason about.
        private static string[] Segment(string[] lines, int head, int tail)
        {
            int length = lines.Length - head - tail;
            var segment = new string[length];
            Array.Copy(lines, head, segment, 0, length);
            return segment;
        }

        internal static void PrintDiff(TextWriter stdout, string label, string before, string after)
        {
            if (before == after)
            {
                return;
            }

            stdout.WriteLine("--- a/" + label);
            stdout.WriteLine("+++ b/" + label);

            string[] fullBefore = before.Split('\n');
            string[] fullAfter = after.Split('\n');

            // The table below is O(before × after) in memory, and these edits are
            // small and local in files that are not: a few hundred changed lines in
            // a script of tens of thousands would otherwise cost gigabytes to
            // describe. Identical leading and trailing lines belong to the longest
            // common subsequence by definition and this diff prints no context, so
            // trimming them costs nothing in output and bounds the table to the
            // region that actually differs.
            int head = 0;
            int maxHead = Math.Min(fullBefore.Length, fullAfter.Length);
            while (head < maxHead && fullBefore[head] == fullAfter[head])
            {
                head++;
            }

            int tail = 0;
            int maxTail = Math.Min(fullBefore.Length, fullAfter.Length) - head;
            while (tail < maxTail
                && fullBefore[fullBefore.Length - 1 - tail] == fullAfter[fullAfter.Length - 1 - tail])
            {
                tail++;
            }

            string[] a = Segment(fullBefore, head, tail);
            string[] b = Segment(fullAfter, head, tail);

            // Trimming bounds the usual case, where an edit sits in one place. It does
            // not bound the case where edits bracket a long file — an inserted import
            // near the top and a rewritten member near the bottom leave everything
            // between them inside the differing region. The table is quadratic in that
            // region, so past this many cells the preview says what changed and how
            // much rather than spending gigabytes to lay it out line by line; the
            // change itself is unaffected, and `--apply` writes the same bytes either
            // way.
            const long MaxCells = 4L * 1024 * 1024;
            if ((long)(a.Length + 1) * (b.Length + 1) > MaxCells)
            {
                stdout.WriteLine(
                    "@@ " + a.Length + " line(s) replaced by " + b.Length + " @@");
                stdout.WriteLine(
                    "(preview condensed: the changed region spans too much of the file to"
                    + " lay out line by line — apply and review the result in your diff tool)");
                return;
            }

            int[,] lcs = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
            {
                for (int j = b.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < a.Length && y < b.Length)
            {
                if (a[x] == b[y])
                {
                    x++;
                    y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1])
                {
                    stdout.WriteLine("-" + a[x++]);
                }
                else
                {
                    stdout.WriteLine("+" + b[y++]);
                }
            }

            while (x < a.Length)
            {
                stdout.WriteLine("-" + a[x++]);
            }

            while (y < b.Length)
            {
                stdout.WriteLine("+" + b[y++]);
            }
        }

        // A replacement-fallback decoder turns a byte the file never meant as text
        // into U+FFFD, and every writing host re-encodes the decoded string over
        // the original — so a byte the tool cannot read would be rewritten
        // permanently, on a line the changed-lines diff never prints. Decoding is
        // strict for that reason, as it has always been on the wire side; a file
        // the tool cannot read losslessly is refused rather than repaired.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // For reads whose result is inspected and discarded, never written back.
        // The distinction is the whole point: substitution is destructive only
        // because the decoded text is what the writing hosts re-encode.
        internal static string DecodeUtf8Lenient(byte[] bytes)
            => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);

        internal static bool TryDecodeUtf8(byte[] bytes, out string text)
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = null;
                return false;
            }
        }

        // Names the file rather than the offset: the operator's remedy is to
        // re-save it as UTF-8, for which the byte position is no help.
        internal static string Utf8Refusal(string path)
            => "error: " + Path.GetFileName(path) + " is not valid UTF-8 — re-save it as UTF-8 "
                + "and re-run; rewriting it from a lossy decode would replace the offending "
                + "bytes for good";

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a is null || b is null)
            {
                return ReferenceEquals(a, b);
            }

            return a.AsSpan().SequenceEqual(b);
        }

        // Temp-write then move, so a crash mid-write never leaves a torn file.
        // The temp is created with CreateNew so a pre-planted <path>.tmp — a
        // symlink an attacker could leave in the source directory to redirect
        // the write outside the tree — makes the create fail rather than be
        // followed. (The final path is symlink-checked separately in
        // GuardBeforeWrite; File.Move renames onto it without following.)
        /// <summary>
        /// Reads the ledger text back through the parser before it is allowed to
        /// reach disk. The reader is stricter than the writer by design — it
        /// treats a sidecar as hostile input — so a document the writer can emit
        /// is not automatically one the toolchain can open. A sidecar that fails
        /// here would strand its type: every later run, including the verbs that
        /// would repair it, begins by parsing it.
        ///
        /// This is a last line rather than the first: names are validated where
        /// they enter. It holds for the paths nobody thought to check.
        /// </summary>
        internal static int GuardLedgerIsReadable(string ledgerText, string fileName, TextWriter stderr)
        {
            var reparsed = VariableIdLedger.Parse(ledgerText);
            if (reparsed.IsValid)
            {
                return 0;
            }

            stderr.WriteLine("error: refusing to write " + fileName + " — the tool cannot read back what it "
                + "just produced (" + reparsed.Error + "); nothing was written");
            return 4;
        }

        /// <summary>
        /// The source-side counterpart of <see cref="GuardLedgerIsReadable"/>:
        /// re-parses the rewritten text before it is allowed to reach disk, and
        /// refuses it on the same terms the transforms refuse their INPUT — a tree
        /// carrying an error-severity diagnostic. Every transform tests that of the
        /// file it reads; nothing tested it of the file it writes.
        ///
        /// The argument is the ledger's, and it lands harder here. A rewriter that
        /// builds its nodes from typed factories should not be able to emit an
        /// unparseable tree, exactly as a writer emitting its own canonical form
        /// should not be able to emit an unreadable sidecar — and the ledger is
        /// guarded anyway, because "should not" is not a check. What makes the
        /// source the worse half to leave unguarded is the write ORDER: the ledger
        /// is deliberately persisted first, so a defect caught only after that
        /// point leaves the issued id recorded against a file that no longer
        /// compiles — and the re-run cannot repair it, because every host begins by
        /// refusing a file that does not parse. Held ahead of both writes, the same
        /// defect costs a refusal and nothing else.
        /// </summary>
        internal static int GuardSourceIsParseable(string source, string fileName, TextWriter stderr)
        {
            var fault = CSharpSyntaxTree.ParseText(source).GetDiagnostics()
                .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (fault == null)
            {
                return 0;
            }

            // The line is the rewritten file's own, which is the only file this
            // message can send a reader to: the input parsed cleanly, so no
            // position in it corresponds to the fault.
            int line = fault.Location.GetLineSpan().StartLinePosition.Line + 1;
            stderr.WriteLine("error: refusing to write " + fileName + " — the tool cannot parse back what it "
                + "just produced (line " + line.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ": " + fault.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                + "); nothing was written");
            return 4;
        }

        /// <summary>
        /// The parse guard's stronger sibling: a tree can parse cleanly and still
        /// not compile. A dropped <c>using</c>, a companion whose type never
        /// resolved, a hook injected onto a base that does not declare it — every
        /// one of those parses, and the developer meets it as a red editor rather
        /// than as a refusal here.
        ///
        /// 🔑 It asks the DIFFERENTIAL question — did the rewrite introduce an
        /// error the input did not already have — and that is not a softer
        /// version of "does it compile", it is the only sound one available. The
        /// SDK contract stub knows about twenty-five types; a real file naming
        /// <c>Debug</c>, <c>Time</c>, or any type from its own project fails an
        /// absolute compile. Measured, not assumed: a shipped SDK sample fails it
        /// with <c>CS0103</c> and <c>CS0246</c>. A host refusing on that verdict
        /// would refuse nearly every legitimate conversion, which is a worse
        /// outcome than the defect it set out to catch.
        ///
        /// ⛔ So an empty verdict here does NOT mean the file compiles. It means
        /// this tool did not break it, which is the only thing this tool is
        /// entitled to claim.
        /// </summary>
        internal static int GuardSourceCompiles(
            string original, string rewritten, string fileName, TextWriter stdout, TextWriter stderr)
        {
            // ⛔ THE PRECONDITION, and it is the whole honesty of this guard.
            //
            // Subtracting the input's errors from the output's is not enough,
            // because an error the input ALREADY had can cause a DIFFERENT error
            // once the rewrite starts using the broken thing. Measured on the
            // house fixture: a type inheriting `NetworkBehaviour` without
            // importing it carries an unresolved base both before and after, and
            // the rewrite's `new NetworkVariableInt(this, …)` then fails CS1503
            // — a signature the input never had, and a refusal the developer
            // could do nothing about.
            //
            // So the guard judges only what it is in a position to judge: a file
            // that compiled cleanly against the contract stub to begin with.
            // Anything else, it stands down and says nothing.
            //
            // ⚠️ In a REAL Unity project this stands down often — the stub knows
            // about twenty-five types and an ordinary file names `Debug`,
            // `Time`, or its own project's classes. That is a real limit on this
            // guard's reach, not a defect in it, and the alternative is a tool
            // that refuses work it has no grounds to refuse. It is recorded here
            // rather than discovered later by someone wondering why the gate
            // never fires.
            //
            // ⚠️ And it says so. A gate that stands down in silence is
            // indistinguishable from one that checked and approved, which is the
            // reading an author will make of an apply that printed nothing —
            // exactly when the write went out unverified. Ids only, never
            // messages or source text, so the note inherits the gate's own rule
            // that nothing from the candidate travels back through it.
            var inherited = CompileGate.Check(original);
            if (inherited.Refusal != null)
            {
                // Nothing was compiled, so there is no verdict to stand on and
                // no inherited-error list to name. The parse check has already
                // run and is all this write is held to.
                stdout.WriteLine(
                    "note: the compile check stood down on " + fileName + " — " + inherited.Refusal
                    + ", so the rewrite could not be compared against the input;"
                    + " only the parse check applied");
                return 0;
            }

            if (!inherited.Ok)
            {
                stdout.WriteLine(
                    "note: the compile check stood down on " + fileName + " — it already reports "
                    + string.Join(", ", inherited.DiagnosticIds)
                    + " against the SDK contract, so an error introduced by the rewrite could not"
                    + " be told apart from one it inherited; only the parse check applied");
                return 0;
            }

            var introduced = CompileGate.NewErrorsFromRewrite(original, rewritten);
            if (introduced.Count == 0)
            {
                return 0;
            }

            stderr.WriteLine("error: refusing to write " + fileName + " — the rewrite introduces "
                + introduced.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " compile error(s) the input did not have; nothing was written");

            // Bounded, because a single structural mistake can cascade into
            // hundreds and burying the operator's terminal helps nobody. The
            // count above is the whole truth; this is the readable part of it.
            foreach (string error in introduced.Take(5))
            {
                stderr.WriteLine("  " + error);
            }

            if (introduced.Count > 5)
            {
                stderr.WriteLine("  … and "
                    + (introduced.Count - 5).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " more");
            }

            return 4;
        }

        /// <summary>
        /// A replacement written, flushed to the device, and stamped with its
        /// target's mode — everything except the rename that makes it visible.
        /// </summary>
        internal sealed class StagedWrite
        {
            public StagedWrite(string path, string tempPath)
            {
                Path = path;
                TempPath = tempPath;
            }

            public string Path { get; }

            public string TempPath { get; }
        }

        /// <summary>
        /// Raised when a run failed while staging. It is the guarantee that
        /// distinguishes the two failure shapes: staging touches no target, so a
        /// caller seeing this may say "nothing was changed" and be right. A
        /// failure during the commit is a different statement and is NOT wrapped
        /// in this type.
        /// </summary>
        internal sealed class StagingFailedException : IOException
        {
            public StagingFailedException(string message, Exception inner) : base(message, inner) { }
        }

        /// <summary>
        /// Raised when a rename failed. Earlier targets may already carry their
        /// new contents, so a caller may not say "nothing was changed" — and
        /// <see cref="Stranded"/> names the replacements that could neither be
        /// committed nor cleared, which are what stands between the operator and
        /// the re-run that finishes the change.
        /// </summary>
        internal sealed class CommitFailedException : IOException
        {
            public CommitFailedException(Exception inner, IReadOnlyList<string> stranded)
                : base(inner.Message, inner) => Stranded = stranded;

            public IReadOnlyList<string> Stranded { get; }
        }

        /// <summary>
        /// Writes every file, or none of them.
        ///
        /// 🔑 Writing two files one after the other leaves a failure on the
        /// second with the first already replaced — and for the
        /// identity-allocating verbs the first is the LEDGER, which means an id
        /// recorded against a source file that never landed. Staging every
        /// replacement first moves the whole cost of failure to a phase where no
        /// target has been touched.
        ///
        /// <para>⚠️ This is not multi-file atomicity and must not be read as it.
        /// POSIX has no way to rename N files as one operation, so a crash
        /// between two renames still leaves a mixed tree. What the split removes
        /// is the LARGE window — encoding, flushing, and the device round-trip
        /// for every later file — and leaves only the small one: N renames back
        /// to back, each of them itself atomic.</para>
        /// </summary>
        internal static void WriteAllAtomically(IReadOnlyList<(string Path, string Content)> writes)
        {
            if (writes == null) throw new ArgumentNullException(nameof(writes));

            var staged = new List<StagedWrite>(writes.Count);
            try
            {
                foreach (var (path, content) in writes)
                {
                    staged.Add(Stage(path, content));
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Every temp this run created goes, including the ones that
                // succeeded: leaving them behind would make the NEXT run refuse
                // on a stale .tmp, turning one failure into a manual cleanup.
                DiscardAll(staged);
                throw new StagingFailedException(e.Message, e);
            }

            CommitAll(staged);
        }

        /// <summary>Writes one file, or none. The single-file spelling of <see cref="WriteAllAtomically"/>.</summary>
        internal static void WriteAtomically(string path, string content)
            => WriteAllAtomically(new[] { (path, content) });

        /// <summary>
        /// Makes every staged replacement visible. Each rename is atomic on its
        /// own; the SET is not, so a failure here means some targets may already
        /// carry their new contents — which is why callers report this case in
        /// different words from a staging failure.
        /// </summary>
        /// <remarks>
        /// The replacements that never landed are cleared before the fault leaves
        /// this method, and what could not be cleared is reported rather than
        /// assumed away. The write path refuses to open over an existing temp, so
        /// a survivor blocks the re-run the callers advise — for the very file
        /// that failed. Completing the change by renaming one by hand is possible
        /// but is not the sanctioned recovery, and its content is a pure function
        /// of the input, so a re-run reproduces it.
        ///
        /// <para>⚠️ What makes the clearing narrow is the list, not the contents:
        /// only paths THIS run staged with <c>CreateNew</c> are in it. The binding
        /// between a path and the file behind it is not re-verified here, so a
        /// path whose file was replaced under the run is removed as though it were
        /// ours — the same exposure the staging path has always carried, bounded
        /// by <c>File.Delete</c> never following a link.</para>
        /// </remarks>
        internal static void CommitAll(IReadOnlyList<StagedWrite> staged)
        {
            for (int i = 0; i < staged.Count; i++)
            {
                try
                {
                    File.Move(staged[i].TempPath, staged[i].Path, overwrite: true);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // From `i`, never from 0: the entries before it had their
                    // temps consumed by a successful rename, so those names are
                    // free again and a concurrent run may legitimately hold them.
                    throw new CommitFailedException(e, DiscardAll(staged, from: i));
                }
            }
        }

        /// <summary>
        /// Removes staged replacements, from <paramref name="from"/> onward, and
        /// returns the ones that survived. Best-effort by design: a temp that
        /// cannot be deleted is a worse thing to throw about than the fault that
        /// brought us here, and the original exception is the one the operator
        /// needs — but the survivors are named, because a rename and an unlink are
        /// gated by the same permission on the same directory. The fault that
        /// stopped the commit is very often the one that stops the clearing, which
        /// is exactly when an unqualified "they have been removed" would be false.
        /// </summary>
        private static IReadOnlyList<string> DiscardAll(IReadOnlyList<StagedWrite> staged, int from = 0)
        {
            var stranded = new List<string>();
            for (int i = from; i < staged.Count; i++)
            {
                try
                {
                    File.Delete(staged[i].TempPath);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    stranded.Add(staged[i].TempPath);
                }
            }

            return stranded;
        }

        /// <summary>
        /// How an operator gets from a failed commit back to a finished change.
        /// One wording for every host, because the recovery is the same one and
        /// the branch that decides it is not something a caller should re-derive.
        /// </summary>
        internal static string RecoveryAfter(CommitFailedException failure, string rerun)
            => failure.Stranded.Count == 0
                ? "the replacements that did not land have been cleared, so " + rerun
                : "these staged replacements could not be cleared and will block a retry — delete "
                    + string.Join(", ", failure.Stranded) + " by hand, then " + rerun;

        private static StagedWrite Stage(string path, string content)
        {
            string temp = path + ".tmp";
            // CreateNew refuses an existing temp — the symlink defence above — but
            // an interrupted earlier run can leave an orphaned .tmp behind, and
            // the bare "file exists" IOException gives no recovery path. Name the
            // cause and the remedy; still fail-closed (never delete it blindly:
            // the leftover is exactly what a planted symlink would look like).
            if (File.Exists(temp))
            {
                throw new IOException(
                    "stale '" + temp + "' left by an interrupted run — inspect it,"
                    + " delete it by hand, then re-run");
            }

            // The replacement is a different file wearing the target's name, so it
            // carries the process umask rather than the target's own mode. Read the
            // mode first and restore it after: a source file a version-control system
            // leaves read-only until it is checked out — Perforce and Plastic both do —
            // would otherwise come back writable, and the bit that says "not checked
            // out" is the developer's, not this tool's, to clear.
            UnixFileMode? mode = ExistingFileMode(path);

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, utf8))
            {
                writer.Write(content);

                // Temp-then-move is atomic against a process death, which is what
                // closing the handle covers. It is NOT atomic against a power loss
                // or a kernel death: the rename can reach the filesystem journal
                // while the replacement's data blocks are still in the page cache,
                // and the file that survives is the new name over empty content.
                // ext4's auto_da_alloc heuristic covers this exact write-close-
                // rename shape on most installs, but it is a heuristic and not
                // every filesystem has one, and the file at risk is the durable id
                // record — losing it shifts wire identities with no way to recover
                // them. The two flushes are ordered and both are required: the
                // writer holds the encoded bytes and hands them to the stream, and
                // only the stream can ask the device to persist them.
                //
                // A flush that fails throws before the rename, so the target still
                // holds its previous contents and every caller's "nothing was
                // changed" stays true.
                //
                // ⚠️ This makes the CONTENT durable, not the directory entry. .NET
                // exposes no portable directory fsync, so a power loss between the
                // rename and the directory's own writeback can still lose the
                // rename — leaving the PREVIOUS file intact, which is the outcome
                // the atomic-write shape exists to guarantee. The failure this
                // closes is the other one: a rename that survives over content
                // that did not.
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // The preserved mode is stamped on the replacement while it is still
            // the temp file — rename keeps it — so the swap is the last operation
            // of all. Every caller reports a failed write as "nothing was
            // changed", and that stays true only while no fault can land after
            // the target has already been replaced; stamping here rather than
            // after the move is what keeps the last step a bare rename.
            if (mode.HasValue && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, mode.Value);
            }

            return new StagedWrite(path, temp);
        }

        // The target's mode, or null when there is nothing to preserve — a first
        // write, or a platform that does not model one. Never fails the write: the
        // content is the deliverable and a mode that cannot be read is not worth
        // refusing it over.
        private static UnixFileMode? ExistingFileMode(string path)
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return File.GetUnixFileMode(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    internal static class Program
    {
        // 🔑 An encoding is an agreement between two processes, and a host that
        // declares neither side of it is read under whatever the machine happens
        // to default to. This output carries em dashes, ellipses and a key
        // emoji, and the editor that drives this host reads it as the diff an
        // author approves — so the bytes written here are pinned rather than
        // inherited.
        //
        // ⛔ Not through `Console.OutputEncoding`: setting it calls
        // SetConsoleOutputCP, which fails when there is no console — and the
        // editor always launches this host with CreateNoWindow, which is exactly
        // that case. Replacing the writer works in both.
        //
        // ⚠️ No byte-order mark, and AutoFlush: the caller reads these streams
        // line by line while the process runs, so a buffered final line that
        // arrives only at exit is a diff missing its last row.
        private static int Main(string[] args)
        {
            Console.SetOut(Utf8Writer(Console.OpenStandardOutput()));
            Console.SetError(Utf8Writer(Console.OpenStandardError()));

            return Dispatch(args, Console.Out, Console.Error);
        }

        internal static TextWriter Utf8Writer(Stream stream)
            => new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        // `gen-rpc` selects the Enhanced-RPC host, `readiness` the readiness
        // emitter, and `fix` the identity-free transform host.
        //
        // 🔑 `convert` names the NetworkVariable host, and the verb-less spelling
        // reaches the same place. Both are required, for different callers: the
        // `make convert` target has always passed the options alone, while every
        // usage line that host prints opens with the word `convert` — so a reader
        // typing back what they were just shown must arrive somewhere. Until this
        // arm existed the word fell through to the verb-less branch as a stray
        // leading token, which answered it with the very usage line that had
        // named it: the one loop a usage message must never close.
        //
        // Split from Main so the routing is reachable by test. Main's signature
        // belongs to the runtime, and a dispatcher only the runtime can call is a
        // dispatcher nothing holds to the names the host itself prints.
        internal static int Dispatch(string[] args, TextWriter stdout, TextWriter stderr)
            => args.Length == 0
                ? ConversionCli.Run(args, stdout, stderr)
                : args[0] switch
                {
                    "convert" => ConversionCli.Run(args.Skip(1).ToArray(), stdout, stderr),
                    "convert-batch" => BatchCli.Run(args.Skip(1).ToArray(), stdout, stderr),
                    "gen-rpc" => RpcCli.Run(args.Skip(1).ToArray(), stdout, stderr),
                    "readiness" => ReadinessCli.Run(args.Skip(1).ToArray(), stdout, stderr),
                    "fix" => FixCli.Run(args.Skip(1).ToArray(), stdout, stderr),
                    _ => ConversionCli.Run(args, stdout, stderr),
                };
    }
}
