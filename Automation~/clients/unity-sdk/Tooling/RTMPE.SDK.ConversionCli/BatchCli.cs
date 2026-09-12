using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Conversion.Core;
using RTMPE.SDK.Transforms;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// Several types converted as one decision: one plan, one diff, one approval,
    /// one commit.
    ///
    /// 🔑 The verb exists because the alternative — the wizard looping over the
    /// single-type verb — is a partial application waiting to happen. Each
    /// iteration writes; the fourth one failing leaves three types converted, one
    /// not, and a developer holding a tree no single command produced. Here every
    /// type is planned and verified while nothing has been written, and the whole
    /// set is committed through the one transaction.
    ///
    /// Grammar: <c>--file</c> opens a file, <c>--type</c> opens a type within the
    /// file that precedes it, and <c>--member</c> attaches to the type that
    /// precedes it. The flag spellings are the single-type verb's,
    /// unchanged, so the wizard passes what it already knows how to build.
    ///
    /// Exit codes are the single-type verb's: 0 success/no-op, 1 usage, 2
    /// unreadable input or derivation verdict, 3 refusal, 4 apply-time safety stop.
    /// </summary>
    public static class BatchCli
    {
        private const string Usage =
            "usage: convert-batch --file <path.cs> --type <Fully.Qualified.Type> "
            + "--member <field>[:<companionName>] [--member ...] "
            + "[--type <Type2> --member ...] [--file <path2.cs> --type ...] [--apply]";

        /// <summary>One type's request, with its own member namespace.</summary>
        private sealed class TypeRequest
        {
            internal TypeRequest(string typeName) => TypeName = typeName;

            internal string TypeName { get; }

            internal List<(string Field, string Companion)> Members { get; } = new List<(string, string)>();


            // Per type, never per run: two types in one file may each declare a
            // member of the same name, and a shared claim table would refuse an
            // ordinary file.
            internal Dictionary<string, string> ClaimedNames { get; }
                = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private sealed class FileRequest
        {
            internal FileRequest(string path) => Path = path;

            internal string Path { get; set; }

            internal List<TypeRequest> Types { get; } = new List<TypeRequest>();
        }

        /// <summary>
        /// One replacement this run will make, carrying everything the diff, the
        /// apply-time guards, the commit and the report need about it.
        /// </summary>
        private sealed class PlannedWrite
        {
            internal PlannedWrite(
                string path, string label, string oldText, string newText, byte[] snapshot, bool isSource)
            {
                Path = path;
                Label = label;
                OldText = oldText;
                NewText = newText;
                Snapshot = snapshot;
                IsSource = isSource;
            }

            internal string Path { get; }

            /// <summary>The bare file name — the diff's label and the guards' subject.</summary>
            internal string Label { get; }

            internal string OldText { get; }

            internal string NewText { get; }

            /// <summary>The target's bytes at plan time; <c>null</c> when it did not exist.</summary>
            internal byte[] Snapshot { get; }

            /// <summary>Sources take the parse and compile guards; sidecars take the readback.</summary>
            internal bool IsSource { get; }
        }

        /// <summary>One file's decided plan: what it held, what it should hold,
        /// and every sidecar its types decided.</summary>
        private sealed class PlannedFile
        {
            internal PlannedFile(
                string sourcePath, byte[] snapshot, string oldSource, string newSource,
                List<TypeAllocation> allocations)
            {
                SourcePath = sourcePath;
                Snapshot = snapshot;
                Allocations = allocations;
                Writes = ReplacementsFor(sourcePath, snapshot, oldSource, newSource, allocations);
            }

            internal List<TypeAllocation> Allocations { get; }

            /// <summary>
            /// The source this file's plan was read from, and the bytes it held
            /// then. Kept apart from <see cref="Writes"/> because it is an INPUT
            /// to the plan whether or not the plan replaces it.
            /// </summary>
            internal string SourcePath { get; }

            internal byte[] Snapshot { get; }

            /// <summary>
            /// The replacements this file contributes, source first — the order a
            /// reader wants a diff in. Empty when the file and all its sidecars
            /// already hold what the plan decided.
            /// </summary>
            /// <remarks>
            /// 🔑 Decided once, here, and read by the diff, the apply-time guards,
            /// the commit and the report alike. A batch may name a type that is
            /// already converted beside one that is not, and "which targets does
            /// this run touch" is a single fact: re-deriving it per consumer is how
            /// a file ends up guarded but not written, or written but not reported.
            /// </remarks>
            internal List<PlannedWrite> Writes { get; }

            private static List<PlannedWrite> ReplacementsFor(
                string sourcePath, byte[] snapshot, string oldSource, string newSource,
                List<TypeAllocation> allocations)
            {
                var writes = new List<PlannedWrite>();
                if (newSource != oldSource)
                {
                    writes.Add(new PlannedWrite(
                        sourcePath, System.IO.Path.GetFileName(sourcePath),
                        oldSource, newSource, snapshot, isSource: true));
                }

                foreach (var allocation in allocations)
                {
                    if (allocation.NewLedgerText != allocation.OldLedgerText)
                    {
                        writes.Add(new PlannedWrite(
                            allocation.LedgerPath, allocation.LedgerFileName,
                            allocation.OldLedgerText, allocation.NewLedgerText,
                            allocation.LedgerSnapshot, isSource: false));
                    }
                }

                return writes;
            }
        }

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            var files = new List<FileRequest>();
            FileRequest currentFile = null;
            TypeRequest currentType = null;
            bool apply = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--file" when CliArguments.HasValue(args, i):
                        currentFile = new FileRequest(args[++i]);
                        currentType = null;
                        files.Add(currentFile);
                        break;
                    case "--type" when CliArguments.HasValue(args, i):
                        if (currentFile is null)
                        {
                            stderr.WriteLine(
                                "error: --type before any --file — every type belongs to the file named before it");
                            return 1;
                        }

                        currentType = new TypeRequest(args[++i]);
                        currentFile.Types.Add(currentType);
                        break;
                    case "--member" when CliArguments.HasValue(args, i):
                        if (currentType is null)
                        {
                            stderr.WriteLine(
                                "error: --member before any --type — every member belongs to the type named before it");
                            return 1;
                        }

                        if (!ConversionCli.TryClaimMember(
                            args[++i], currentType.ClaimedNames, stderr, out var member))
                        {
                            return 1;
                        }

                        currentType.Members.Add(member);
                        break;
                    case "--apply":
                        apply = true;
                        break;
                    default:
                        stderr.WriteLine(Usage);
                        return 1;
                }
            }

            if (files.Count == 0)
            {
                stderr.WriteLine(Usage);
                return 1;
            }

            // ⚠️ Paths are judged BEFORE the grammar's completeness, and the order
            // is load-bearing rather than cosmetic. A value no path API can use is
            // an unhandled exception waiting at the first file call — the abort the
            // shared CLI argument contract exists to prevent — and a verb that
            // reported "names no --type" first would answer that case from its
            // structural gate having never looked at the path, leaving the guard
            // unreached and its regression invisible.
            //
            // A file named twice would also be planned twice against its ORIGINAL
            // text, and the second plan's write would silently discard the first's.
            var namedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!CliArguments.TryResolveFullPath(file.Path, "--file", stderr, out string resolved))
                {
                    return 1;
                }

                if (!File.Exists(resolved))
                {
                    stderr.WriteLine("error: no such file: " + resolved);
                    return 1;
                }

                if (!namedFiles.Add(CanonicalPath(resolved)))
                {
                    stderr.WriteLine(
                        "error: --file names '" + resolved + "' twice — every type in one file goes in"
                        + " that file's own group, or the second plan discards the first");
                    return 1;
                }

                file.Path = resolved;
            }

            foreach (var file in files)
            {
                if (file.Types.Count == 0)
                {
                    stderr.WriteLine("error: --file " + file.Path + " names no --type");
                    return 1;
                }

                foreach (var type in file.Types)
                {
                    if (type.Members.Count == 0)
                    {
                        stderr.WriteLine("error: --type " + type.TypeName + " names no --member");
                        return 1;
                    }
                }
            }

            var plannedFiles = new List<PlannedFile>();
            foreach (var file in files)
            {
                int planned = PlanFile(file, stdout, stderr, out var result);
                if (planned != 0)
                {
                    return planned;
                }

                plannedFiles.Add(result);
            }

            var allocations = plannedFiles.SelectMany(p => p.Allocations).ToList();

            // 🔑 The recovered write-set guard (§2.6), over the whole batch rather
            // than one type: two types differing only by case resolve to sidecar
            // names that are one file on a Windows or macOS checkout, so the
            // second would clobber the first's identity record. Judged on full
            // paths, because the hazard is two names for one file — which is a
            // property of the directory, not of the stem.
            //
            // ⚠️ Every sidecar the batch decided, not only the ones it will write.
            // Two types sharing one file on a case-insensitive checkout share it
            // whether or not both change, and a single write into that file is
            // enough to overwrite the other type's record.
            //
            // Canonical, for the reason the base-ledger check gives: a directory
            // link above two sidecars gives one file two spellings, and one type
            // declared under each spelling then passes a comparison that reads
            // their names side by side.
            var issues = VariableIdDerivation.ValidateWriteSet(
                allocations.Select(allocation => CanonicalPath(allocation.LedgerPath)));
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    stderr.WriteLine("error: " + issue.Message);
                }

                return 2;
            }

            var writes = plannedFiles.SelectMany(file => file.Writes).ToList();
            if (writes.Count == 0)
            {
                // `no-op:` is the Conversion Wizard's idempotency marker, not
                // prose — the single-type verb's contract, kept verbatim.
                stdout.WriteLine("no-op: every conversion in the batch is already applied");
                return 0;
            }

            // Deterministic and explainable: files in the order they were named,
            // and within each file its source followed by its types' sidecars in
            // the order those types were named. The developer approves the diff
            // they asked for, in the order they asked for it.
            foreach (var write in writes)
            {
                ConversionCli.PrintDiff(stdout, write.Label, write.OldText, write.NewText);
            }

            if (!apply)
            {
                stdout.WriteLine("(preview only — pass --apply to write every file and ledger above)");
                return 0;
            }

            // 🔑 First, every file the plan READ still holds what it held then —
            // written or not. A source this run does not replace can still be the
            // source a ledger was decided from, and that ledger IS written: an
            // editor save during the seconds the batch spends planning later files
            // would otherwise commit wire ids for text that no longer exists.
            // "What did the plan depend on" is a wider question than "what will
            // this run touch", and only the second one is `writes`.
            foreach (var file in plannedFiles)
            {
                int intact = ConversionCli.GuardPlanStillStands(
                    file.SourcePath, file.Snapshot, "source file " + file.SourcePath, stderr);
                if (intact != 0)
                {
                    return intact;
                }

                foreach (var allocation in file.Allocations)
                {
                    intact = ConversionCli.GuardPlanStillStands(
                        allocation.LedgerPath, allocation.LedgerSnapshot,
                        "ledger " + allocation.LedgerPath, stderr);
                    if (intact != 0)
                    {
                        return intact;
                    }
                }
            }

            // Then every guard that belongs to a replacement, before the first one
            // lands. In a batch this is the property, not a detail: a guard that
            // fired after the second file's commit would be reporting a state it
            // could no longer restore for the first.
            foreach (var write in writes)
            {
                int guard = ConversionCli.GuardBeforeWrite(
                    write.Path, write.Snapshot,
                    (write.IsSource ? "source file " : "ledger ") + write.Path, stderr);
                if (guard != 0)
                {
                    return guard;
                }

                if (!write.IsSource)
                {
                    guard = ConversionCli.GuardLedgerIsReadable(write.NewText, write.Label, stderr);
                    if (guard != 0)
                    {
                        return guard;
                    }

                    continue;
                }

                int parseable = ConversionCli.GuardSourceIsParseable(write.NewText, write.Label, stderr);
                if (parseable != 0)
                {
                    return parseable;
                }

                int compiles = ConversionCli.GuardSourceCompiles(
                    write.OldText, write.NewText, write.Label, stdout, stderr);
                if (compiles != 0)
                {
                    return compiles;
                }
            }

            // Every ledger, then every source. The single-type verb commits the
            // ledger before the source it describes so that a failure between the
            // two leaves an id recorded rather than an id cited by nothing; across
            // a batch the same argument holds for every pair, and putting all the
            // sidecars first satisfies it without depending on the order the files
            // were named.
            var writeSet = writes.Where(write => !write.IsSource)
                .Concat(writes.Where(write => write.IsSource))
                .ToList();

            try
            {
                ConversionCli.WriteAllAtomically(
                    writeSet.Select(write => (write.Path, write.NewText)).ToList());
            }
            catch (ConversionCli.StagingFailedException ex)
            {
                stderr.WriteLine(
                    "error: the write could not be staged, so nothing was changed: " + ex.Message);
                return 4;
            }
            catch (ConversionCli.CommitFailedException ex)
            {
                stderr.WriteLine(
                    "error: the staged files were not all committed: " + ex.Message
                    + " — of " + string.Join(", ", writeSet.Select(write => write.Label))
                    + ", those committed before the fault already hold their new contents and any"
                    + " recorded ids stay valid; " + ConversionCli.RecoveryAfter(ex, "re-run --apply"));
                return 4;
            }

            // Reported from the write set, so the line names what was written
            // rather than what was asked for — the two differ exactly when part of
            // the batch was already applied.
            stdout.WriteLine(
                "applied: " + string.Join(", ", writeSet.Select(write => write.Label)));
            return 0;
        }

        /// <summary>
        /// Reads one file once, allocates for every type it names, and composes
        /// the rewrites. Nothing here writes.
        /// </summary>
        private static int PlanFile(
            FileRequest file, TextWriter stdout, TextWriter stderr, out PlannedFile planned)
        {
            planned = null;

            // One read, decoded once — the text that is transformed and the bytes
            // the apply-time guard compares against must describe the same file.
            if (!CliArguments.TryReadAllBytes(file.Path, file.Path, stderr, out byte[] snapshot))
            {
                return 2;
            }

            if (!ConversionCli.TryDecodeUtf8(snapshot, out string source))
            {
                stderr.WriteLine(ConversionCli.Utf8Refusal(file.Path));
                return 2;
            }

            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
            string directory = Path.GetDirectoryName(file.Path);

            var allocations = new List<TypeAllocation>();
            var entries = new List<CompositeConversion>();
            foreach (var type in file.Types)
            {
                var target = ConversionCli.FindClass(root, type.TypeName);
                if (target is null)
                {
                    stderr.WriteLine("error: type '" + type.TypeName + "' not found in " + file.Path);
                    return 3;
                }

                int allocated = ConversionCli.AllocateForType(
                    target, type.TypeName, directory, type.Members,
                    stdout, stderr, out var allocation);
                if (allocated != 0)
                {
                    return allocated;
                }

                allocations.Add(allocation);
                entries.Add(new CompositeConversion(
                    type.TypeName, new ConversionPlan(allocation.Conversions)));
            }

            var newRoot = CompositePlan.Apply(
                root, new CompositePlan(entries), ConversionCli.FindClass,
                out string refusal, out string refusedType);
            if (refusal != null)
            {
                stderr.WriteLine(
                    "refused: " + Path.GetFileName(file.Path)
                    + (refusedType is null ? string.Empty : ": '" + refusedType + "'")
                    + ": " + refusal);
                return 3;
            }

            planned = new PlannedFile(file.Path, snapshot, source, newRoot.ToFullString(), allocations);
            return 0;
        }

        /// <summary>
        /// One spelling for one file: the whole path chain resolved, links and
        /// all, so two ways of naming the same sidecar compare equal.
        /// </summary>
        /// <remarks>
        /// 🔑 <c>Path.GetFullPath</c> is not enough and the gap is not academic.
        /// It collapses <c>.</c> and <c>..</c> and stops there, so with a symlinked
        /// directory anywhere above it — <c>Scripts/current -> v2</c> is an
        /// ordinary way to lay a project out — the same ledger reached two ways
        /// yields two strings. A base-ledger argument that misses its match is a
        /// derived type allocating against a record this run is about to replace,
        /// and the collision does not surface until two members answer to one wire
        /// id.
        ///
        /// <para>Comparison is case-insensitive for the reason the sidecar
        /// write-set guard already gives: on a Windows or macOS checkout two
        /// spellings that differ only by case are one file. On Linux they are two,
        /// so this errs toward refusing a batch it need not — which is the
        /// direction a wire identity is worth erring in.</para>
        /// </remarks>
        internal static string CanonicalPath(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException
                || e is PathTooLongException)
            {
                return path; // unusable as a path; it names none of this run's targets either way
            }

            // ⚠️ The directory first, and the file only afterwards. A sidecar this
            // run is about to MINT does not exist yet, and asking the file layer to
            // resolve a link for a path that is not there throws — which, asked
            // first, abandons the directory resolution too and hands back the raw
            // string. That is a silent miss in a comparison whose whole job is to
            // catch two names for one file.
            string directory = Path.GetDirectoryName(full);
            string resolved = directory == null
                ? full
                : Path.Combine(CanonicalDirectory(directory), Path.GetFileName(full));

            try
            {
                var link = new FileInfo(resolved).ResolveLinkTarget(returnFinalTarget: true);
                if (link == null)
                {
                    return resolved;
                }

                // Where the link lands is a path in its own right, so its own
                // directories go through the same resolution the argument's did.
                string target = Path.GetFullPath(link.FullName);
                string targetDirectory = Path.GetDirectoryName(target);
                return targetDirectory == null
                    ? target
                    : Path.Combine(CanonicalDirectory(targetDirectory), Path.GetFileName(target));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Absent, or a link chain that does not terminate: the directory
                // resolution above already stands, and it is the half that carries
                // the hazard.
                return resolved;
            }
        }

        // The kernel's own ceiling on how many links one lookup may follow. A
        // cycle spanning several components resolves forever otherwise, and a
        // bound the operating system already imposes is the honest place to stop.
        private const int MaxLinkDepth = 40;

        private static string CanonicalDirectory(string directory)
            => CanonicalDirectory(directory, MaxLinkDepth);

        private static string CanonicalDirectory(string directory, int budget)
        {
            if (budget > 0)
            {
                try
                {
                    var link = Directory.ResolveLinkTarget(directory, returnFinalTarget: true);
                    if (link != null)
                    {
                        // 🔑 Resolved again rather than returned. The final-target
                        // overload follows the chain of THIS link and stops there;
                        // the directories its answer passes through are components
                        // nobody has asked about, and one of them being a link is
                        // how two spellings survive a comparison written to
                        // collapse them.
                        return CanonicalDirectory(Path.GetFullPath(link.FullName), budget - 1);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // A component that is absent or circular resolves to itself; the
                    // write path refuses such a target on its own terms.
                }
            }

            string parent = Path.GetDirectoryName(directory);
            return parent == null || parent == directory
                ? directory
                : Path.Combine(CanonicalDirectory(parent, budget), Path.GetFileName(directory));
        }

    }
}
