using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RTMPE.SDK.Conversion.Core;
using RTMPE.SDK.Analyzers;

namespace RTMPE.SDK.Analysis
{
    /// <summary>The compile gate's answer: pass/fail plus the error ids that failed it.</summary>
    public sealed class CompileGateVerdict
    {
        public CompileGateVerdict(bool ok, IReadOnlyList<string> diagnosticIds)
        {
            Ok = ok;
            DiagnosticIds = diagnosticIds;
        }

        /// <summary>
        /// The verdict for a candidate the gate declined to read.
        /// </summary>
        /// <remarks>
        /// ⛔ Not <c>Ok</c>, and not a compile failure either: nothing was
        /// compiled. A caller that treats every non-<c>Ok</c> answer as "this
        /// source has errors" is wrong about a refusal, which is why the reason
        /// is a separate property rather than a synthetic diagnostic id.
        /// </remarks>
        public CompileGateVerdict(string refusal)
        {
            Ok = false;
            DiagnosticIds = EmptyIds;
            Refusal = refusal;
        }

        private static readonly string[] EmptyIds = new string[0];

        /// <summary>
        /// Why the gate declined to read the candidate, or <c>null</c> when it
        /// read it and <see cref="Ok"/> is its answer.
        /// </summary>
        public string Refusal { get; }

        /// <summary>True when the candidate compiles with zero error-severity diagnostics.</summary>
        public bool Ok { get; }

        /// <summary>
        /// The distinct error ids (e.g. <c>CS0246</c>), sorted ordinally. Ids
        /// only — never messages or source text, so nothing from the candidate
        /// leaks back through the gate.
        /// </summary>
        public IReadOnlyList<string> DiagnosticIds { get; }
    }

    /// <summary>
    /// The in-process compile check the parent plan names <c>compile_gate</c>:
    /// a candidate source file is compiled against the SDK contract stub and the
    /// verdict is only <c>{ok, diagnosticIds[]}</c>. Purely in-memory — no file
    /// is written, no assembly is emitted, no code is executed — so the gate is
    /// safe to expose to an untrusted proposer: the worst a hostile input can
    /// achieve is its own list of compiler errors.
    ///
    /// <para>⚠️ It lives in <c>RTMPE.SDK.Analysis</c> rather than beside its first
    /// caller, and the reason is worth keeping: the hosts that WRITE source need
    /// to ask this question too, and this is the only assembly both they and the
    /// advisory layer can reach. The obvious alternative — pushing it down into
    /// <c>Conversion.Core</c> — is wrong three ways: that assembly declares zero
    /// package references by design, it is one of the four DLLs shipped into every
    /// Unity editor, and this gate uses <c>RTMPE.SDK.Analyzers</c>, which already
    /// references <c>Conversion.Core</c>, so the move would close a cycle. Analysis
    /// is referenced by both callers, ships to nobody, and needed no new reference
    /// to host this: <c>SdkContract</c> and <c>CompilationReferences</c> were
    /// already inside its graph.</para>
    /// </summary>
    public static class CompileGate
    {
        /// <summary>
        /// The largest candidate this gate will read, in UTF-16 code units.
        /// </summary>
        public static readonly int MaxSourceLength = 256 * 1024;

        /// <summary>
        /// The deepest bracket nesting this gate will parse, counted over the
        /// lexer's own token stream.
        /// </summary>
        public static readonly int MaxBracketDepth = 256;

        /// <summary>
        /// The deepest syntax tree this gate will bind.
        /// </summary>
        /// <remarks>
        /// Higher than <see cref="MaxBracketDepth"/> on purpose: a tree counts
        /// nodes the lexer has no token for, so the same code measures deeper
        /// here, and a ceiling equal to the bracket one would refuse a candidate
        /// the bracket rule had just admitted.
        /// </remarks>
        public static readonly int MaxSyntaxDepth = 512;

        // The contract stub is a constant, and a SyntaxTree is immutable — so it
        // is parsed once for the process rather than once per question. The gate
        // is asked three times per file on the writing path (an absolute verdict
        // on the input, then a differential over both versions), and a batch
        // multiplies that by the number of files it converts.
        private static readonly SyntaxTree StubTree =
            CSharpSyntaxTree.ParseText(SdkContract.Stub, path: "SdkStub.cs");

        public static CompileGateVerdict Check(string candidateSource)
        {
            if (candidateSource == null) throw new ArgumentNullException(nameof(candidateSource));

            if (!TryRead(candidateSource, out var candidate, out string refusal))
            {
                return new CompileGateVerdict(refusal);
            }

            var trees = new[] { StubTree, candidate };

            var compilation = CSharpCompilation.Create(
                "RtmpeCompileGate",
                trees,
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Distinct sorted ids give a deterministic verdict regardless of how
            // many sites repeat the same fault or which order Roslyn reports them.
            var ids = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            return new CompileGateVerdict(ids.Length == 0, ids);
        }

        /// <summary>
        /// Whether a rewrite introduced a compile error the input did not
        /// already have.
        ///
        /// 🔑 <see cref="Check"/> is the wrong question for a file taken from a
        /// real project, and measuring says so rather than reasoning: the SDK
        /// contract stub declares roughly twenty-five types, so a shipped SDK
        /// sample — correct, compiling code — fails it with <c>CS0103</c> and
        /// <c>CS0246</c> for every <c>Debug</c>, <c>Time</c> or project type the
        /// stub has never heard of. A host that refused on that verdict would
        /// refuse nearly every legitimate conversion.
        ///
        /// <para>The difference is sound where the absolute answer is not: both
        /// compilations see the same incomplete stub, so everything the stub
        /// cannot resolve fails identically on each side and cancels. What
        /// survives the subtraction is what the REWRITE did.</para>
        ///
        /// <para>⚠️ Compared by id AND message, not by id alone. A rewrite that
        /// turns one missing symbol into a different missing symbol keeps the id
        /// set unchanged — both are <c>CS0246</c> — and the message is the only
        /// part that names which symbol. Messages are rendered invariant so the
        /// verdict does not depend on the machine's locale.</para>
        ///
        /// <para>⛔ This overload's diagnostics are OPERATOR-facing: they carry
        /// compiler messages, which quote identifiers from the file. They must
        /// not be handed to an untrusted proposer — that is what <see cref="Check"/>
        /// is for, and why it returns ids only.</para>
        /// </summary>
        /// <returns>
        /// The new errors, ordered and distinct; empty when the rewrite
        /// introduced none. An empty result is NOT a claim that either version
        /// compiles.
        /// </returns>
        public static IReadOnlyList<string> NewErrorsFromRewrite(string original, string rewritten)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (rewritten == null) throw new ArgumentNullException(nameof(rewritten));

            var before = new HashSet<string>(ErrorSignatures(original), StringComparer.Ordinal);

            return ErrorSignatures(rewritten)
                .Where(signature => !before.Contains(signature))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(signature => signature, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// One candidate's error-severity diagnostics as <c>ID: message</c>.
        /// Locations are deliberately excluded: a rewrite inserts lines, so
        /// every diagnostic below the insertion point would shift and read as
        /// new — which would make the subtraction report the whole file.
        /// </summary>
        private static IEnumerable<string> ErrorSignatures(string candidateSource)
        {
            // A refusal is one signature, so the subtraction handles it the way
            // it handles everything else: refused on both sides cancels, refused
            // only after the rewrite is an error the rewrite introduced.
            if (!TryRead(candidateSource, out var candidate, out string refusal))
            {
                return new[] { RefusalSignature + refusal };
            }

            var trees = new[] { StubTree, candidate };

            var compilation = CSharpCompilation.Create(
                "RtmpeCompileGateDifferential",
                trees,
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id + ": " + d.GetMessage(CultureInfo.InvariantCulture))
                .ToArray();
        }

        /// <summary>
        /// The marker a refused candidate contributes to the differential, kept
        /// distinct from any compiler id so the two can never be confused.
        /// </summary>
        private const string RefusalSignature = "RTMPE_GATE_REFUSED: ";

        /// <summary>
        /// The candidate, read only if reading it is safe.
        /// </summary>
        /// <remarks>
        /// Roslyn parses and binds by recursive descent, and neither stage is
        /// bounded by anything this process can recover from: a .NET stack
        /// overflow is an immediate abort rather than an exception, and the
        /// binder does its work on thread-pool threads, so there is no frame a
        /// <c>try</c> could occupy that would keep the caller alive. Measured on
        /// this toolchain, the parser abandons ~35,000 levels of parenthesis on
        /// an 8 MB stack — about 240 bytes a level, so roughly 4,000 on the 1 MB
        /// stack a thread-pool thread carries — and the binder abandons ~11,000
        /// levels of any construct. Both are reachable in a few tens of
        /// kilobytes of text: 20 KB of <c>~</c> is enough for the second.
        ///
        /// <para>The two hazards are bounded separately because they have to be.
        /// Parse cost is QUADRATIC in nesting — 64 KB of nested parenthesis
        /// takes 39 seconds — so the parser's own hazard cannot be measured by
        /// parsing; it is bounded ahead of time over the LEXER's token stream,
        /// which is iterative, exact, and ~20 ms for a file the size of the
        /// largest this repository ships. Counting brackets in raw text instead
        /// is not a weaker version of that, it is a broken one: a closer inside
        /// a comment cancels counted depth without cancelling real depth, so
        /// <c>(/*)*/</c> repeated measures 3 and aborts the process.</para>
        ///
        /// <para>What the lexer cannot see — a prefix operator chain, a cast
        /// chain, a right-associative operator chain — recurses in the parser
        /// under Roslyn's own stack guard and reaches the tree intact, where the
        /// binder would abort on it. That is measured after the parse, over the
        /// tree, iteratively.</para>
        ///
        /// <para>⛔ The ceilings are sized against the code they exist to serve:
        /// across the 480 C# files shipped from this repository the largest is
        /// 143 KB and the deepest bracket nesting is 17.</para>
        /// </remarks>
        private static bool TryRead(string candidateSource, out SyntaxTree tree, out string refusal)
        {
            tree = null;

            if (candidateSource.Length > MaxSourceLength)
            {
                refusal = "the candidate is "
                    + candidateSource.Length.ToString(CultureInfo.InvariantCulture)
                    + " UTF-16 code units and this gate reads at most "
                    + MaxSourceLength.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            // ⚠️ On the parser's stack as well: the lexer scans the balanced text
            // of an interpolation hole RECURSIVELY, one frame per bracket, and it
            // produces the token only once the whole hole is scanned — so 30,000
            // parentheses in a hole overflowed the counter before the counter
            // could refuse them. Linear, and bounded by the length cap.
            int brackets = OnAParserStack(() => BracketDepth(candidateSource));
            if (brackets > MaxBracketDepth)
            {
                refusal = "the candidate nests brackets " + Overflowing(brackets)
                    + " deep and this gate parses at most "
                    + MaxBracketDepth.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            SyntaxTree parsed;
            try
            {
                // 🔴 On a stack sized for the worst the length cap admits, never on
                // the caller's. The bracket ceiling above is a fast path, not the
                // guarantee: a second pass found two shapes it could not see —
                // `A<A<A<…>>>` (no bracket token) at 24 KB and 30,000 parentheses
                // inside an interpolation hole (one token to the lexer) at 60 KB —
                // and each ended the process from a thread-pool stack. Measured on
                // this toolchain: the costliest shape is ~1 KB of stack per level
                // and a level costs at least one character, so 512 MB carries twice
                // the deepest candidate the cap can hold; every bracket-free shape
                // tried (130,000 `~`, 40,000 lambdas, 30,000 casts, 40,000
                // ternaries, 130,000 type arguments) parsed on it in under 1.3 s.
                // Reserved, not committed — the kernel commits stack pages on touch.
                parsed = OnAParserStack(() => CSharpSyntaxTree.ParseText(candidateSource, path: "Candidate.cs"));
            }
            catch (InsufficientExecutionStackException)
            {
                // Roslyn's own guard, reporting that even that stack is too small
                // for the candidate. Catchable, unlike the abort the ceilings above
                // exist to prevent, and the same answer.
                refusal = "the candidate is nested too deeply for the parser";
                return false;
            }

            int depth = SyntaxDepth(parsed.GetRoot());
            if (depth > MaxSyntaxDepth)
            {
                refusal = "the candidate's syntax nests " + Overflowing(depth)
                    + " deep and this gate binds at most "
                    + MaxSyntaxDepth.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            tree = parsed;
            refusal = null;
            return true;
        }

        // A measurement that stopped at its ceiling reports the ceiling, so the
        // reason says "more than" rather than a number that is not the answer.
        private static string Overflowing(int measured)
            => "more than " + (measured - 1).ToString(CultureInfo.InvariantCulture) + " levels";

        // The parser's stack. Sized from the length cap rather than from any one
        // shape: 2 KB per character the cap admits, which is twice the costliest
        // level measured (type-argument nesting, ~1 KB). See TryRead.
        private const int ParserStackBytes = 512 * 1024 * 1024;

        private static T OnAParserStack<T>(Func<T> work)
        {
            T result = default;
            Exception failure = null;
            var parser = new Thread(
                () =>
                {
                    try
                    {
                        result = work();
                    }
                    catch (Exception caught)
                    {
                        failure = caught;
                    }
                },
                ParserStackBytes);
            parser.IsBackground = true;
            parser.Start();
            parser.Join();

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return result;
        }

        /// <summary>
        /// The deepest nesting in the candidate that the parser will recurse on,
        /// counted over Roslyn's own token stream so that comments, string bodies
        /// and character literals contribute nothing. Lexing is iterative, so
        /// asking this question is safe on any stack, and it is the fast path:
        /// the parse behind it is quadratic in nesting, so a candidate refused
        /// here is one the parser never sees.
        /// </summary>
        /// <remarks>
        /// 🚨 Two token kinds the first version could not see, both found by an
        /// adversarial pass with the process ended each time. Type arguments nest
        /// on <c>&lt;</c>, which is no bracket token, so <c>A&lt;A&lt;A&lt;…&gt;&gt;&gt;</c>
        /// measured zero; and an interpolated string is ONE token to the lexer,
        /// so 30,000 parentheses inside its hole measured zero as well — where
        /// the doc-comment above this method had promised the opposite. Angle
        /// brackets are counted with the running depth clamped at zero (a
        /// comparison's <c>&lt;</c> has no closer; across the 424 files shipped
        /// here the clamped depth peaks at 48 against a ceiling of 256), and an
        /// interpolated token's text is scanned for the brackets its holes carry.
        /// ⛔ That scan over-counts a bracket in the hole's literal text — a
        /// refusal, never an admission, which is the direction a ceiling may err.
        /// </remarks>
        private static int BracketDepth(string candidateSource)
        {
            int brackets = 0;
            int angles = 0;
            int deepest = 0;

            // Enough to refuse; lexing the rest would only make the number larger,
            // and the enumerable is lazy, so stopping here is what keeps a hostile
            // candidate from being read in full.
            bool Deeper()
            {
                if (brackets + angles > deepest)
                {
                    deepest = brackets + angles;
                }

                return deepest > MaxBracketDepth;
            }

            foreach (var token in SyntaxFactory.ParseTokens(candidateSource))
            {
                var kind = token.Kind();
                switch (kind)
                {
                    case SyntaxKind.OpenParenToken:
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.OpenBraceToken:
                        brackets++;
                        if (Deeper())
                        {
                            return deepest;
                        }

                        break;

                    // An unmatched closer is the compiler's to report, and it
                    // buys no depth: clamping at zero stops a leading `)` from
                    // funding extra nesting later in the file.
                    case SyntaxKind.CloseParenToken:
                    case SyntaxKind.CloseBracketToken:
                    case SyntaxKind.CloseBraceToken:
                        brackets = Math.Max(0, brackets - 1);
                        break;

                    // ⛔ `<` is counted on its own ledger, because a comparison's
                    // `<` has no closer and three hundred of them in one method
                    // are ordinary code. The ledger is emptied by any token that
                    // cannot stand inside a type-argument list — `;`, `{`, `=`,
                    // `&&`, a literal, a keyword — which every comparison reaches
                    // within a few tokens and a nested `A<A<A<…` never does.
                    case SyntaxKind.LessThanToken:
                        angles++;
                        if (Deeper())
                        {
                            return deepest;
                        }

                        break;

                    case SyntaxKind.GreaterThanToken:
                        angles = Math.Max(0, angles - 1);
                        break;

                    case SyntaxKind.InterpolatedStringToken:
                        foreach (char c in token.Text)
                        {
                            switch (c)
                            {
                                case '(':
                                case '[':
                                case '{':
                                case '<':
                                    brackets++;
                                    if (Deeper())
                                    {
                                        return deepest;
                                    }

                                    break;

                                case ')':
                                case ']':
                                case '}':
                                case '>':
                                    brackets = Math.Max(0, brackets - 1);
                                    break;
                            }
                        }

                        break;

                    default:
                        if (!MayStandInsideTypeArguments(kind))
                        {
                            angles = 0;
                        }

                        break;
                }
            }

            return deepest;
        }

        // What a type-argument list may contain: names, the tokens that join and
        // qualify them, nullable and pointer markers, array and tuple brackets,
        // and the predefined type keywords. Anything else ends the list.
        private static bool MayStandInsideTypeArguments(SyntaxKind kind)
            => kind switch
            {
                SyntaxKind.IdentifierToken => true,
                SyntaxKind.DotToken => true,
                SyntaxKind.CommaToken => true,
                SyntaxKind.QuestionToken => true,
                SyntaxKind.AsteriskToken => true,
                SyntaxKind.ColonColonToken => true,
                SyntaxKind.OpenBracketToken => true,
                SyntaxKind.CloseBracketToken => true,
                SyntaxKind.OpenParenToken => true,
                SyntaxKind.CloseParenToken => true,
                SyntaxKind.LessThanToken => true,
                SyntaxKind.GreaterThanToken => true,
                _ => SyntaxFacts.IsPredefinedType(kind),
            };

        /// <summary>
        /// The depth of the parsed tree, walked with an explicit stack: a
        /// recursive measurement of a tree this exists to call too deep would
        /// abort on the very input it is measuring.
        /// </summary>
        private static int SyntaxDepth(SyntaxNode root)
        {
            int deepest = 0;
            var pending = new Stack<KeyValuePair<SyntaxNode, int>>();
            pending.Push(new KeyValuePair<SyntaxNode, int>(root, 1));

            while (pending.Count > 0)
            {
                var entry = pending.Pop();
                if (entry.Value > deepest)
                {
                    deepest = entry.Value;
                    if (deepest > MaxSyntaxDepth)
                    {
                        return deepest;
                    }
                }

                foreach (var child in entry.Key.ChildNodes())
                {
                    pending.Push(new KeyValuePair<SyntaxNode, int>(child, entry.Value + 1));
                }
            }

            return deepest;
        }

        // The running framework's trusted platform assemblies, exactly as the
        // readiness host resolves them, so the two compilations agree on what
        // the base library looks like.
        private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

        private static IReadOnlyList<MetadataReference> BuildReferences()
            => CompilationReferences.TrustedPlatform();
    }
}
