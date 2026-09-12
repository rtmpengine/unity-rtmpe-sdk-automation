using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RTMPE.SDK.Analysis;
using RTMPE.SDK.Conversion.Core;
using RTMPE.SDK.Analyzers;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// The headless Network-Readiness emission host (completion-plan W1.1): the
    /// first production writer of the <c>network-readiness.json</c>/<c>.md</c>
    /// artifact, which until now existed only as a CI test side-effect. The
    /// scoring and serialisation are the proven Phase-5 engines
    /// (<see cref="NetworkReadinessScorer"/> + <see cref="ReadinessReportSerializer"/>);
    /// this verb only assembles the compilation and writes the two files, so the
    /// artifact the in-editor Readiness window renders is byte-identical to the
    /// one CI publishes. Exit codes: 0 success, 1 usage — including a named
    /// <c>--source</c>/<c>--repo-root</c> directory that does not exist, which is
    /// the sibling verbs' answer for a named path that is not there, and an
    /// answers file or a runtime record this host refuses, which are
    /// operator-supplied inputs the tool will not act on rather than a broken
    /// environment — 2 environment (a file or directory that exists and cannot be
    /// read, either input included), 4 write hazard, and — only with
    /// <c>--fail-on-findings</c> — 8, the artifact carries at least one to-do
    /// row. ⛔ That last one is opt-in on purpose: a finding has never failed
    /// this host, and making it do so by default would turn every existing green
    /// job red on an upgrade nobody asked for.
    /// </summary>
    public static class ReadinessCli
    {
        private const string Usage =
            "usage: readiness --repo-root <path> [--define <SYMBOL>]... [--answers <path>]\n"
            + "                 [--runtime <path>] [--assets <dir>]... [--out <dir>]\n"
            + "       readiness --source <dir> [--source <dir> ...] [--define <SYMBOL>]...\n"
            + "                 [--answers <path>] [--runtime <path>] [--assets <dir>]...\n"
            + "                 [--out <dir>]\n"
            + "  --repo-root  score five scripts from two shipping SDK samples: SimpleFPS under\n"
            + "               <path>/clients/unity-sdk/Samples, and the spawn flow the package\n"
            + "               offers under Packages/com.rtmpe.sdk/Samples~/PlayerSpawnFlow\n"
            + "  --source     score every .cs under <dir> instead (obj, bin, Library, Temp, Logs,\n"
            + "               .git, Packages, PackageCache, Editor and Tests segments are skipped)\n"
            + "  --define     a preprocessor symbol to parse with, repeatable; without one every\n"
            + "               #if region is inactive text and is scored as absent\n"
            + "  --answers    the project's recorded authority answers (default: "
            + AuthorityQuestionnaire.AnswerFileName + " in the output\n"
            + "               directory, when it is there); a named path that is not there is an error\n"
            + "  --runtime    what a RUN of this project established (default: "
            + RuntimeVerification.RecordFileName + " in\n"
            + "               the output directory, when it is there); nothing this host reads from\n"
            + "               source can set one — the five checks stay untested until a run says\n"
            + "               otherwise\n"
            + "  --assets     also read every *.prefab under <dir>, repeatable, and add a to-do\n"
            + "               row for each prefab that carries NetworkTransform with no\n"
            + "               NetworkTransformInterpolator — the configuration whose replicas\n"
            + "               freeze on every OTHER client, which a session you run alone\n"
            + "               cannot surface; without it no prefab is read and the artifact is\n"
            + "               unchanged\n"
            + "  --out        directory receiving network-readiness.json/.md (default: current"
            + " directory)\n"
            + "  --fail-on-findings\n"
            + "               exit 8 when the artifact carries any to-do row, so a CI job can\n"
            + "               act on one. Opt-in: without it a run that finds faults still\n"
            + "               exits 0, which is what every existing job expects. A row saying\n"
            + "               the check established nothing counts — a gate that cannot tell\n"
            + "               has not said yes.";

        public const string JsonArtifactName = "network-readiness.json";
        public const string MarkdownArtifactName = "network-readiness.md";


        // Directory segments never worth scoring: build output, Unity caches, VCS
        // metadata, the SDK package itself, and editor/test code — mirroring the
        // Conversion Wizard's scan exclusions so the two surfaces agree on what
        // "the project's scripts" means.
        private static readonly string[] ExcludedSegments =
            { "obj", "bin", "Library", "Temp", "Logs", ".git", "Packages", "PackageCache", "Editor", "Tests" };

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            string repoRoot = null;
            string outDir = null;
            string answersPath = null;
            string runtimePath = null;
            var sourceDirs = new List<string>();
            var assetDirs = new List<string>();
            bool failOnFindings = false;
            var defines = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--repo-root" when CliArguments.HasValue(args, i):
                        repoRoot = args[++i];
                        break;
                    case "--source" when CliArguments.HasValue(args, i):
                        sourceDirs.Add(args[++i]);
                        break;
                    // ⛔ Beside --source rather than instead of it, and legal in
                    // BOTH input modes: prefabs are not scored and never were —
                    // this reads a different kind of file to answer a different
                    // question, and a run that scores the pinned samples has the
                    // same reason to ask it as one that scores a project.
                    case "--assets" when CliArguments.HasValue(args, i):
                        assetDirs.Add(args[++i]);
                        break;
                    case "--fail-on-findings":
                        failOnFindings = true;
                        break;
                    case "--define" when CliArguments.HasValue(args, i):
                        string symbol = args[++i];
                        if (!SyntaxFacts.IsValidIdentifier(symbol))
                        {
                            stderr.WriteLine(
                                "error: '" + symbol + "' is not a valid preprocessor symbol —"
                                + " --define takes a C# identifier");
                            return 1;
                        }

                        defines.Add(symbol);
                        break;
                    case "--answers" when CliArguments.HasValue(args, i):
                        answersPath = args[++i];
                        break;
                    case "--runtime" when CliArguments.HasValue(args, i):
                        runtimePath = args[++i];
                        break;
                    case "--out" when CliArguments.HasValue(args, i):
                        outDir = args[++i];
                        break;
                    default:
                        stderr.WriteLine(Usage);
                        return 1;
                }
            }

            // Exactly one input mode: the pinned sample set, or explicit source
            // directories — never both, so the artifact's provenance is unambiguous.
            if ((repoRoot == null) == (sourceDirs.Count == 0))
            {
                stderr.WriteLine(Usage);
                return 1;
            }

            // A directory the operator named that no path API can use is a usage
            // fault, and the sibling verbs all answer it with the usage exit. Left
            // to Directory.Exists it returns false instead of throwing, so the run
            // reported a missing directory and took the environment exit — the same
            // input answered two different ways depending on which verb received it.
            // The value is checked rather than replaced: walked paths reach the
            // warnings and the artifact, so resolving them here would move output
            // for every well-formed run in order to answer a malformed one.
            string pathOption = repoRoot == null ? "--source" : "--repo-root";
            foreach (string candidate in repoRoot == null ? (IEnumerable<string>)sourceDirs : new[] { repoRoot })
            {
                if (!CliArguments.TryResolveFullPath(candidate, pathOption, stderr, out _))
                {
                    return 1;
                }

                // 🔑 A directory the operator named that is not there is the same
                // operator mistake as a --file that is not there, and the six
                // sibling verbs all answer that one with the usage exit. Answered
                // here with the environment exit, a single typo meant two
                // different things depending on which verb received it — the
                // asymmetry the paragraph above set out to remove, left standing
                // one case short. Both named paths are covered: --repo-root used
                // to reach the sample walk and report a *derived* file path, which
                // names the layout rather than the argument that was wrong.
                if (!Directory.Exists(candidate))
                {
                    stderr.WriteLine("error: " + pathOption + " directory not found: " + candidate);
                    return 1;
                }
            }

            // The same two guards the scored roots get, for the same reason: a
            // directory no path API can use is a usage fault, and one that is not
            // there is the operator's own mistake rather than a broken environment.
            foreach (string candidate in assetDirs)
            {
                if (!CliArguments.TryResolveFullPath(candidate, "--assets", stderr, out _))
                {
                    return 1;
                }

                if (!Directory.Exists(candidate))
                {
                    stderr.WriteLine("error: --assets directory not found: " + candidate);
                    return 1;
                }
            }

            List<string> files;
            var unreadableDirectories = new List<string>();
            string assemblyName;
            if (repoRoot != null)
            {
                files = SampleFiles(repoRoot).ToList();
                // The same compilation name the CI suite uses, so the two runs
                // are comparable end to end.
                assemblyName = "RtmpeSampleFiles";
            }
            else
            {
                files = new List<string>();
                // Existence was settled for every named directory above, in one
                // place, so this walk states it nowhere: two spellings of one rule
                // is how the two exits got out of step to begin with. A directory
                // that disappears between the two is caught by the walk's own
                // reader (DirectoryNotFoundException is an IOException) and
                // reported as unreadable rather than as a fresh usage fault.
                foreach (var dir in sourceDirs)
                {
                    files.AddRange(EnumerateProjectSources(dir, unreadableDirectories));
                }

                unreadableDirectories.Sort(StringComparer.Ordinal);
                foreach (string directory in unreadableDirectories)
                {
                    stdout.WriteLine(
                        "warning: " + directory + " could not be read; any scripts beneath it are"
                        + " scored as absent");
                }

                // Deterministic input order ⇒ deterministic artifact bytes.
                files.Sort(StringComparer.Ordinal);
                assemblyName = "RtmpeReadinessProject";

                if (files.Count == 0)
                {
                    stderr.WriteLine("error: no .cs files found under the given --source directories");
                    return 2;
                }
            }

            // Roslyn parses one configuration at a time, so there is no symbol set
            // under which every #if region is active — the operator's project
            // defines which one this score describes. The contract stub carries no
            // directives and is parsed with the defaults either way.
            var parseOptions = new CSharpParseOptions(preprocessorSymbols: defines);
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(SdkContract.Stub, path: "SdkStub.cs") };
            var withInactiveRegions = new List<string>();
            foreach (var file in files)
            {
                if (!File.Exists(file))
                {
                    stderr.WriteLine("error: source file not found: " + file);
                    return 2;
                }

                // Existence was checked a moment ago, which is not the same as
                // being readable: a permission, a lock, or a file removed in
                // between all surface here. The sibling hosts each guard their
                // read; an unguarded one turns a routine environment fault into a
                // crashed process where the documented answer is exit 2.
                string text;
                try
                {
                    // Lenient, deliberately: this host scores and never writes a
                    // source file back, and the artifact carries type names and
                    // evidence — identifiers, which no substituted byte can alter.
                    // Strictness would cost a whole project's report over one
                    // stray byte in one comment while protecting nothing.
                    text = File.ReadAllText(file);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    stderr.WriteLine("error: cannot read " + file + ": " + e.Message);
                    return 2;
                }

                var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path: file);
                trees.Add(tree);

                if (tree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
                    .Any(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
                {
                    withInactiveRegions.Add(file);
                }
            }

            // Code the parse left out is scored as absent, and absent networked
            // code reads as a cleaner project: hiding a type behind a platform #if
            // raises the score and erases its authority verdict. The score is still
            // the right answer for the configuration it was given — but which
            // configuration that was has to be visible, or the report is optimistic
            // exactly where it is blind.
            foreach (string file in withInactiveRegions)
            {
                stdout.WriteLine(
                    "warning: " + file + " has #if regions that are inactive under "
                    + (defines.Count == 0
                        ? "the empty symbol set — pass --define for the symbols this project builds with"
                        : "[" + string.Join(", ", defines) + "]")
                    + "; the code inside them is scored as absent");
            }

            string destination = outDir ?? Directory.GetCurrentDirectory();

            // 🔑 The answers are an INPUT, and they are read from their own file
            // rather than from the artifact this run is about to overwrite: an
            // answer stored in the artifact survives exactly until the next scan,
            // which is the run that was supposed to read it.
            // ⛔ Before anything is read, let alone written: an --answers path that
            // is one of this run's own outputs is destroyed by the run that reads
            // it — applied once, overwritten by the artifact, and refused by every
            // scan after that. Refused rather than warned, because by the time the
            // warning is legible the file is already the artifact.
            // ⚠️ Stated over the inputs rather than for the answers file alone:
            // the runtime record is the same kind of file — an operator-supplied
            // input this run reads and then writes over — and a rule that names
            // one of two is a rule the second input walks straight past.
            foreach ((string option, string path) in
                new[] { ("--answers", answersPath), ("--runtime", runtimePath) })
            {
                if (path == null) continue;

                foreach (string output in new[] { JsonArtifactName, MarkdownArtifactName })
                {
                    if (SamePath(path, Path.Combine(destination, output)))
                    {
                        stderr.WriteLine(
                            "error: " + option + " names " + output + ", which this run writes — the "
                            + "recorded input would be replaced by the artifact that read it");
                        return 1;
                    }
                }
            }

            // ⛔ And against each other: one path given to both options is read
            // twice and refused by whichever parser is handed the other's shape,
            // which reports a malformed file rather than the mistake that was made.
            if (answersPath != null && runtimePath != null && SamePath(answersPath, runtimePath))
            {
                stderr.WriteLine(
                    "error: --answers and --runtime name the same file, and they are two different "
                    + "records — the authority answers and what a run established");
                return 1;
            }

            if (!TryReadAnswers(answersPath, destination, stdout, stderr, out var answers, out int answersExit))
            {
                return answersExit;
            }

            if (!TryReadRuntime(runtimePath, destination, stdout, stderr, out var runtime, out int runtimeExit))
            {
                return runtimeExit;
            }

            var compilation = CreateCompilation(trees, assemblyName);
            var report = NetworkReadinessScorer.Score(compilation, answers, runtime);

            // ⛔ AFTER the score and never inside it. NetworkReadinessScorer.Score
            // measures source, over three public overloads each with its own
            // tests; a prefab is not source and this is not a dimension of the
            // score. The rows join the to-do list, which already flows through
            // both serialisers and the Editor window — so nothing about the
            // artifact's SHAPE changes, and with no --assets nothing about its
            // bytes does either.
            report = WithPrefabMotion(report, assetDirs, stdout);

            string json = ReadinessReportSerializer.ToJson(report);
            string markdown = ReadinessReportSerializer.ToMarkdown(report);

            string jsonPath = Path.Combine(destination, JsonArtifactName);
            string markdownPath = Path.Combine(destination, MarkdownArtifactName);
            try
            {
                Directory.CreateDirectory(destination);
                // The sibling hosts' hardened temp-then-move (CreateNew refuses a
                // pre-planted .tmp), so all four verbs share one write discipline.
                // A half-written pair is a readiness report whose JSON and
                // Markdown describe different runs, and the editor window reads
                // one while the developer reads the other. Staging both before
                // publishing either moves the whole cost of an ordinary failure to
                // a phase where neither artifact has been touched; the two renames
                // that follow are still two operations, which is why the failure
                // below distinguishes them.
                ConversionCli.WriteAllAtomically(
                    new[] { (jsonPath, json), (markdownPath, markdown) });
            }
            catch (ConversionCli.CommitFailedException e)
            {
                // 4, as in every sibling host. Not interchangeable with the
                // refusal below: here one artifact may already be the new run's
                // while the other is the previous run's, and an operator told
                // "cannot write" would trust a pair that no longer agrees.
                stderr.WriteLine(
                    "error: the readiness artifacts were not both committed: " + e.Message
                    + " — " + Path.GetFileName(jsonPath) + " and " + Path.GetFileName(markdownPath)
                    + " may now describe different runs; "
                    + ConversionCli.RecoveryAfter(e, "re-run the readiness host"));
                return 4;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // 4, as in every sibling host: a failed or refused write is an
                // apply-time safety stop, not an input fault, and one meaning per
                // code is what makes the wizard's bare "Engine exit N" legible.
                stderr.WriteLine("error: cannot write the readiness artifact: " + e.Message);
                return 4;
            }

            stdout.WriteLine("Network readiness: project score " + report.ProjectScore
                + "% over " + report.Types.Count + " scored type(s), "
                + report.Authority.Count + " authority verdict(s), "
                + report.Questions.Count + " open authority question(s).");
            // Beside the score and never folded into it, in the one line an
            // operator reading a CI log actually sees.
            stdout.WriteLine(RuntimeVerification.Headline(report.Runtime) + " (static score decided from"
                + " source; these from a run).");
            stdout.WriteLine("  " + jsonPath);
            stdout.WriteLine("  " + markdownPath);

            // ⛔ Last, and only when asked. The artifact is written and the
            // account printed before this decides anything, so the run that
            // fails a job still leaves behind everything needed to see why —
            // a gate that suppresses its own evidence is one an operator
            // switches off.
            //
            // 🔑 8, continuing the doubling this host's codes already use
            // (1 usage, 2 environment, 4 write hazard), so a wizard rendering a
            // bare "Engine exit N" keeps one meaning per code. Opt-in because
            // the alternative is turning every green job red on an upgrade
            // nobody asked for, which is a decision for whoever owns the job.
            if (failOnFindings && report.Todo.Count > 0)
            {
                stderr.WriteLine(
                    "error: " + report.Todo.Count + " readiness to-do row(s) — failing because"
                    + " --fail-on-findings was given; the rows are in "
                    + Path.GetFileName(markdownPath));
                return 8;
            }

            return 0;
        }

        /// <summary>
        /// <paramref name="report"/> with a to-do row for every prefab under
        /// <paramref name="assetDirs"/> that sends motion nothing on it applies.
        /// </summary>
        /// <remarks>
        /// ⛔ Returns the SAME report when no directory was named. Not an
        /// equivalent one — the same object — so a run without <c>--assets</c>
        /// cannot differ from one taken before this existed by so much as a
        /// re-ordered list.
        /// <para>
        /// ⚠️ A prefab that cannot be read is a WARNING, not the environment exit
        /// the scored sources take. A locked or permission-denied prefab costs an
        /// advisory; ending the run over it would cost the score, the authority
        /// verdicts and the runtime section as well — and the artifact those
        /// produce is the reason the host exists. What must not happen is silence,
        /// so the file is named and the count the scan reports is the truth about
        /// what was looked at.
        /// </para>
        /// </remarks>
        private static ReadinessReport WithPrefabMotion(
            ReadinessReport report, List<string> assetDirs, TextWriter stdout)
        {
            if (assetDirs.Count == 0) return report;

            var unreadableDirectories = new List<string>();
            var prefabs = new List<string>();
            foreach (string dir in assetDirs)
            {
                prefabs.AddRange(EnumerateProjectSources(
                    dir, unreadableDirectories, "*" + PrefabMotionScanner.PrefabExtension));
            }

            unreadableDirectories.Sort(StringComparer.Ordinal);
            foreach (string directory in unreadableDirectories)
            {
                stdout.WriteLine(
                    "warning: " + directory + " could not be read; any prefabs beneath it are"
                    + " not checked for remote motion");
            }

            // Deterministic input order ⇒ deterministic artifact bytes.
            prefabs.Sort(StringComparer.Ordinal);

            var assets = new List<PrefabAsset>();
            foreach (string prefab in prefabs)
            {
                try
                {
                    assets.Add(new PrefabAsset(prefab, File.ReadAllText(prefab)));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    stdout.WriteLine(
                        "warning: cannot read " + prefab + ": " + e.Message
                        + "; it is not checked for remote motion");
                }
            }

            var scan = PrefabMotionScanner.Scan(assets);

            // 🔑 Said out loud whether or not anything was found. A scan that
            // quietly read nothing and a scan that quietly found nothing publish
            // the same artifact, and this line is the only thing between them.
            stdout.WriteLine("Remote motion: " + PrefabMotionScanner.Describe(scan) + ".");

            var rows = PrefabMotionScanner.TodoLines(scan);
            if (rows.Count == 0) return report;

            var todo = new List<string>(report.Todo);
            todo.AddRange(rows);
            return new ReadinessReport(
                report.ProjectScore, report.Types, todo, report.Authority,
                report.Questions, report.Runtime);
        }

        // The project's recorded authority answers, from the path the operator
        // named or from the output directory — the one the editor window writes
        // into, and the one `make readiness` is told to write the artifact to.
        //
        // 🔑 Every outcome is said out loud, including "there are none". A scan
        // that quietly found no answers and a scan that quietly failed to look
        // publish the same artifact, and the developer's evidence that the tool
        // read their work is this line.
        // Two paths naming one file. Compared after resolution, because "./a.json"
        // and "a.json" are the same file and only one of them is a string match.
        /// <summary>
        /// A path with its links followed, so two names for one file compare equal.
        /// </summary>
        /// <remarks>
        /// 🚨 <c>Path.GetFullPath</c> normalises and resolves no link, so the
        /// self-overwrite refusal was bypassed by pointing <c>--answers</c> at a
        /// symlink to the artifact: exit 0, and afterwards the file the operator
        /// named held the readiness report instead of their recorded answers.
        /// That is the precise loss the refusal is written against — "by the time
        /// the warning is legible the file is already the artifact" — and against
        /// <c>--runtime</c> it destroys every recorded run outcome.
        /// </remarks>
        private static string Resolved(string path)
        {
            string full = Path.GetFullPath(path);

            // ⚠️ Only an EXISTING path can be a link, and asking about one that is
            // not there throws — which SamePath catches and answers "different",
            // silently losing the refusal for the commonest case of all: naming an
            // output this run has not written yet. Caught by the suite the moment
            // the resolution was added; the normalised path is the right answer
            // for anything that is not a link.
            if (!File.Exists(full))
            {
                return full;
            }

            var target = File.ResolveLinkTarget(full, returnFinalTarget: true);
            return target == null ? full : Path.GetFullPath(target.FullName);
        }

        private static bool SamePath(string left, string right)
        {
            try
            {
                return string.Equals(Resolved(left), Resolved(right), StringComparison.Ordinal);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException
                || e is PathTooLongException || e is IOException || e is UnauthorizedAccessException)
            {
                // A path no API can resolve is answered by the --answers handling
                // below, which reports it as the operator's own mistake.
                return false;
            }
        }

        private static bool TryReadAnswers(
            string named,
            string destination,
            TextWriter stdout,
            TextWriter stderr,
            out IReadOnlyList<AuthorityAnswer> answers,
            out int exit)
        {
            answers = null;
            exit = 0;

            string path = named ?? Path.Combine(destination, AuthorityQuestionnaire.AnswerFileName);
            if (named != null && !CliArguments.TryResolveFullPath(named, "--answers", stderr, out _))
            {
                exit = 1;
                return false;
            }

            if (!File.Exists(path))
            {
                // ⛔ A path the operator NAMED that is not there is their mistake,
                // answered the way every sibling verb answers one. A default path
                // that is not there is the ordinary case — a project that has not
                // been asked anything yet — and is reported, not refused.
                if (named != null)
                {
                    stderr.WriteLine("error: --answers file not found: " + named);
                    exit = 1;
                    return false;
                }

                stdout.WriteLine("authority answers: none recorded at " + path);
                return true;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                stderr.WriteLine("error: cannot read " + path + ": " + e.Message);
                exit = 2;
                return false;
            }

            if (!AuthorityAnswerFile.TryParse(text, out answers, out string fault))
            {
                stderr.WriteLine("error: " + path + " is not a usable authority answers file: " + fault);
                exit = 1;
                return false;
            }

            stdout.WriteLine("authority answers: " + answers.Count + " recorded in " + path);

            // ⚠️ The artifact carries the DEFAULT name for the editor window,
            // which writes beside the artifact. An operator who pointed --answers
            // somewhere else has a run whose artifact would send the window to a
            // different file — said here, where the divergence is created, rather
            // than discovered later as an answer that never reaches a score.
            string standard = Path.Combine(destination, AuthorityQuestionnaire.AnswerFileName);
            if (!string.Equals(
                    Path.GetFullPath(path), Path.GetFullPath(standard), StringComparison.Ordinal))
            {
                stdout.WriteLine(
                    "note: this artifact names " + AuthorityQuestionnaire.AnswerFileName
                    + " for the Readiness window, which writes beside the artifact — the window and"
                    + " this run would use different files");
            }

            return true;
        }

        // What a run of this project established, from the path the operator named
        // or from the output directory beside the artifact.
        //
        // 🔑 The same shape as the answers reader above, deliberately: both are
        // operator-supplied inputs whose absence is ordinary and whose presence
        // must be said out loud. A scan that quietly found no runtime record and
        // a scan that quietly failed to look publish the same artifact — five
        // untested rows — and this line is the only thing that separates them.
        private static bool TryReadRuntime(
            string named,
            string destination,
            TextWriter stdout,
            TextWriter stderr,
            out IReadOnlyList<RuntimeCheck> runtime,
            out int exit)
        {
            runtime = null;
            exit = 0;

            string path = named ?? Path.Combine(destination, RuntimeVerification.RecordFileName);
            if (named != null && !CliArguments.TryResolveFullPath(named, "--runtime", stderr, out _))
            {
                exit = 1;
                return false;
            }

            if (!File.Exists(path))
            {
                // A path the operator NAMED that is not there is their mistake; a
                // default path that is not there is the ordinary case — a project
                // nobody has run yet — and is reported, not refused.
                if (named != null)
                {
                    stderr.WriteLine("error: --runtime file not found: " + named);
                    exit = 1;
                    return false;
                }

                stdout.WriteLine("runtime checks: none recorded at " + path
                    + " — all " + RuntimeVerification.Checks.Count + " remain untested");
                return true;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                stderr.WriteLine("error: cannot read " + path + ": " + e.Message);
                exit = 2;
                return false;
            }

            if (!RuntimeCheckFile.TryParse(text, out runtime, out string fault))
            {
                stderr.WriteLine("error: " + path + " is not a usable runtime record: " + fault);
                exit = 1;
                return false;
            }

            stdout.WriteLine("runtime checks: " + runtime.Count + " recorded in " + path);

            // ⚠️ The artifact carries the DEFAULT name for the editor window,
            // which writes beside the artifact. An operator who pointed --runtime
            // elsewhere has a run whose artifact would send the window to a
            // different file — said where the divergence is created rather than
            // discovered later as an outcome that never reaches a report.
            string standard = Path.Combine(destination, RuntimeVerification.RecordFileName);
            if (!string.Equals(
                    Path.GetFullPath(path), Path.GetFullPath(standard), StringComparison.Ordinal))
            {
                stdout.WriteLine(
                    "note: this artifact names " + RuntimeVerification.RecordFileName
                    + " for the Readiness window, which writes beside the artifact — the window and"
                    + " this run would use different files");
            }

            return true;
        }

        /// <summary>The five shipping sample scripts the CI readiness suite scores.</summary>
        public static IReadOnlyList<string> SampleFiles(string repoRoot)
        {
            string samples = Path.Combine(repoRoot, "clients", "unity-sdk", "Samples");
            // Two roots: the spawn-flow pair moved into the
            // package so Package Manager can offer it, and SimpleFPS stayed a
            // repository demo. The scored set is unchanged.
            string shipped = Path.Combine(
                repoRoot, "clients", "unity-sdk", "Packages", "com.rtmpe.sdk", "Samples~");
            return new[]
            {
                Path.Combine(samples, "SimpleFPS", "Scripts", "FPSController.cs"),
                Path.Combine(samples, "SimpleFPS", "Scripts", "HealthController.cs"),
                Path.Combine(samples, "SimpleFPS", "Scripts", "ShootingController.cs"),
                Path.Combine(shipped, "PlayerSpawnFlow", "Scripts", "PlayerController.cs"),
                Path.Combine(shipped, "PlayerSpawnFlow", "Scripts", "GameManager.cs"),
            };
        }

        /// <summary>
        /// The same in-memory compilation shape the CI readiness suite builds:
        /// the contract stub plus the scored sources, referenced against the
        /// running framework's trusted platform assemblies so the semantic model
        /// is clean rather than littered with unresolved-type errors.
        /// </summary>
        public static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees, string assemblyName)
            => CSharpCompilation.Create(
                assemblyName,
                trees,
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

        private static IReadOnlyList<MetadataReference> BuildReferences()
            => CompilationReferences.TrustedPlatform();

        // The exclusion applies to the path *relative to the scanned root* — an
        // absolute-path match would silently drop everything when the project
        // itself happens to live under a directory named like a build folder
        // (e.g. a test fixture under bin/).
        // Directory.EnumerateFiles abandons the entire walk at the first folder it
        // cannot open, so one unreadable directory anywhere under a project yields
        // no report at all. Walking explicitly keeps the rest of the tree
        // scoreable and, as importantly, collects what was left out: a score
        // computed over fewer files than the project holds reads as a cleaner
        // project, which is the wrong direction for a gate to be wrong in.
        // Excluded directories are pruned as they are met rather than filtered
        // out of a full listing — the same segment set, without descending into
        // build output to discard it afterwards.
        // ⚠️ The pattern is a parameter and the guards are not. What this walk
        // buys — pruning build output, surviving one unreadable directory,
        // COLLECTING what it could not read — is the same for a prefab as for a
        // script, and a second walk written beside it would have been a second
        // place for that reasoning to be wrong.
        private static List<string> EnumerateProjectSources(
            string root, List<string> unreadable, string pattern = "*.cs")
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                string[] subdirectories;
                string[] files;
                try
                {
                    subdirectories = Directory.GetDirectories(directory);
                    files = Directory.GetFiles(directory, pattern);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    unreadable.Add(directory);
                    continue;
                }

                foreach (string subdirectory in subdirectories)
                {
                    if (!ExcludedSegments.Contains(Path.GetFileName(subdirectory), StringComparer.Ordinal))
                    {
                        pending.Push(subdirectory);
                    }
                }

                found.AddRange(files);
            }

            return found;
        }

    }
}
