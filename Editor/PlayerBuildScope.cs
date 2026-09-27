// RTMPE SDK — Editor/PlayerBuildScope.cs
//
// What a player build compiles, asked of a script the Editor can read.
//
// The Editor compiles more than a player does, and the difference is invisible
// from inside the Editor: a script under an `Editor` folder, a script in an
// assembly definition that declares the Editor as its only platform, and a
// region behind `#if UNITY_EDITOR` all run in Play mode and none of them is in
// the player.  Anything that tells a developer what their BUILD will do has to
// subtract them, or it reports Play mode and calls it the build.
//
// 🔑 One direction only: this answers "is there positive evidence a player
// build leaves this out".  Everything it cannot read — an unreadable assembly
// definition, a symbol that is the project's own, a shape not modelled here —
// reads as INCLUDED.  The claim it protects is a ✅ about a release build, so a
// false "excluded" would send a developer to fix something that is not broken,
// while the cost of a missed exclusion is the answer they already had.
//
// ⛔ No Unity types: the callers are build callbacks and an EditorWindow, which
// no test project compiles, so the decisions live here where they can be driven.
//
// ⚠️ And this is a re-derivation of something Unity itself publishes.
// `CompilationPipeline.GetAssemblies(AssembliesType.Player)` enumerates exactly
// the source files a player build compiles — folder rules, assembly
// definitions, references and all — and asking it would answer the first two
// questions below outright (never the third: a file a player compiles can still
// hold a region it does not).  It is not asked here because the answer would
// then live in code no test project compiles, which is the whole reason this
// file exists; moving to it means passing the answer down from the callers, the
// way the platform name already is, and it is worth doing deliberately rather
// than by halves.

using System.Collections.Generic;

namespace RTMPE.Editor
{
    /// <summary>
    /// Whether a player build compiles a given script, and a given line of it.
    /// </summary>
    internal static class PlayerBuildScope
    {
        /// <summary>
        /// Whether Unity compiles <paramref name="projectPath"/> at all.
        /// </summary>
        /// <remarks>
        /// <c>Assets/StreamingAssets</c> is copied into a build verbatim and
        /// compiled by nothing — in the Editor either. A <c>.cs</c> template
        /// kept there is not a registration any build has, and it is the very
        /// folder the development key is staged into.
        /// </remarks>
        internal static bool CompiledAtAll(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return true;

            string[] segments = projectPath.Split('/');
            return segments.Length < 2
                || !string.Equals(segments[0], "Assets", System.StringComparison.OrdinalIgnoreCase)
                || !string.Equals(segments[1], "StreamingAssets", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Unity's special folder: a script anywhere under a folder named
        /// <c>Editor</c> is compiled into the Editor assembly and into no
        /// player.
        /// </summary>
        /// <param name="projectPath">
        /// The path as Unity shows it — <c>Assets/…/Foo.cs</c>, forward slashes
        /// — which is what the scan already carries.
        /// </param>
        /// <remarks>
        /// ⚠️ A FOLDER named Editor, never a file and never a name that merely
        /// begins with the word: <c>Assets/EditorTools/Foo.cs</c> is an
        /// ordinary script, and <c>Assets/Editor.cs</c> is one too.
        /// <para>
        /// ⛔ Asked only where no assembly definition governs the script: once
        /// one does, the definition is the whole answer and this name means
        /// nothing — see the caller.
        /// </para>
        /// </remarks>
        internal static bool UnderAnEditorFolder(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return false;

            string[] segments = projectPath.Split('/');
            // The last segment is the file itself, and a file named Editor.cs
            // is not a folder.
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], "Editor", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the assembly definition in <paramref name="asmdefJson"/>
        /// produces an assembly no player build carries.
        /// </summary>
        /// <remarks>
        /// Two shapes, and they are the two Unity itself writes:
        /// <list type="bullet">
        ///   <item><c>includePlatforms</c> naming the Editor and nothing else —
        ///   the assembly is built for the Editor alone.</item>
        ///   <item><c>defineConstraints</c> naming
        ///   <c>UNITY_INCLUDE_TESTS</c> — the symbol a player build does not
        ///   carry, which is how every test assembly the Test Framework
        ///   generates stays out of one.</item>
        /// </list>
        /// <para>
        /// ⛔ Null, empty or unreadable answers <see langword="false"/>: a
        /// script with no assembly definition above it belongs to the default
        /// assembly, which every build carries, and a definition this cannot
        /// parse is not evidence of anything.
        /// </para>
        /// </remarks>
        internal static bool EditorOnlyAssembly(string asmdefJson)
        {
            if (string.IsNullOrEmpty(asmdefJson)) return false;

            var platforms = StringsOfArray(asmdefJson, "includePlatforms");
            if (platforms.Count > 0)
            {
                bool editorOnly = true;
                for (int i = 0; i < platforms.Count; i++)
                {
                    if (!string.Equals(platforms[i], "Editor", System.StringComparison.Ordinal))
                    {
                        editorOnly = false;
                        break;
                    }
                }

                if (editorOnly) return true;
            }

            var constraints = StringsOfArray(asmdefJson, "defineConstraints");
            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i] == "UNITY_INCLUDE_TESTS") return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the code at <paramref name="at"/> is still there when
        /// <c>UNITY_EDITOR</c> is not defined — that is, in a player build.
        /// </summary>
        /// <param name="code">
        /// The source as the scan reads it: comments and string literals
        /// already blanked, so a <c>#</c> opening a line is a directive.
        /// </param>
        /// <param name="at">Index into <paramref name="code"/> of the call.</param>
        /// <remarks>
        /// 🔑 Only <c>UNITY_EDITOR</c> and its per-platform spellings are given
        /// a value, because those are the symbols whose value in a player build
        /// is KNOWN — never defined.  Every other symbol is UNKNOWN, and a
        /// region this cannot decide leaves the code in: a project's own symbol
        /// may or may not be set for a build, and neither guess is safe under a
        /// negation.
        /// <para>
        /// <c>#elif</c> and <c>#else</c> are evaluated, because a registration
        /// in the else-branch of <c>#if UNITY_EDITOR</c> is exactly the shape a
        /// careful project writes.  <c>#region</c>, <c>#pragma</c> and the rest
        /// decide nothing and are skipped; an expression this cannot parse
        /// leaves its region included.
        /// </para>
        /// </remarks>
        internal static bool SurvivesWithoutUnityEditor(string code, int at)
        {
            if (string.IsNullOrEmpty(code) || at < 0) return true;

            // Per open region: whether this branch is compiled, and whether any
            // branch of it has been taken yet (which is what #elif / #else ask).
            var compiled = new List<Verdict>();
            var settled = new List<Verdict>();

            int pos = 0;
            while (pos < code.Length && pos < at)
            {
                int end = code.IndexOf('\n', pos);
                if (end < 0) end = code.Length;

                string line = code.Substring(pos, end - pos).Trim();
                if (line.Length > 0 && line[0] == '#')
                {
                    ApplyDirective(line, compiled, settled);
                }

                pos = end + 1;
            }

            // ⛔ Excluded only where a region is definitely not compiled. A
            // region this cannot decide — one that turns on a symbol the
            // project sets for its own builds — leaves the registration in, and
            // that direction is the whole contract of this file: the claim it
            // protects is a ✅ about a release build, so it may only ever
            // subtract on evidence.
            for (int i = 0; i < compiled.Count; i++)
            {
                if (compiled[i] == Verdict.No) return false;
            }

            return true;
        }

        /// <summary>
        /// What a conditional expression is worth in a player build: definitely
        /// compiled, definitely not, or not decidable from the symbols whose
        /// value there is known.
        /// </summary>
        private enum Verdict
        {
            /// <summary>The region is compiled into a player.</summary>
            Yes,

            /// <summary>It is not.</summary>
            No,

            /// <summary>
            /// It turns on a symbol this cannot know — the project's own, set
            /// for some builds and not others.
            /// </summary>
            Unknown,
        }

        private static void ApplyDirective(string line, List<Verdict> compiled, List<Verdict> settled)
        {
            string rest = line.Substring(1).TrimStart();

            if (StartsWithWord(rest, "if"))
            {
                Verdict taken = Evaluate(rest.Substring(2));
                compiled.Add(taken);
                settled.Add(taken);
                return;
            }

            if (StartsWithWord(rest, "elif"))
            {
                if (compiled.Count == 0) return;
                int top = compiled.Count - 1;
                Verdict taken = And(Not(settled[top]), Evaluate(rest.Substring(4)));
                compiled[top] = taken;
                settled[top] = Or(settled[top], taken);
                return;
            }

            if (StartsWithWord(rest, "else"))
            {
                if (compiled.Count == 0) return;
                int top = compiled.Count - 1;
                Verdict taken = Not(settled[top]);
                compiled[top] = taken;
                settled[top] = Verdict.Yes;
                return;
            }

            if (StartsWithWord(rest, "endif"))
            {
                if (compiled.Count == 0) return;
                compiled.RemoveAt(compiled.Count - 1);
                settled.RemoveAt(settled.Count - 1);
            }
        }

        // `#ifdef` is not C#, but `#iffy` would be a stray word and neither is a
        // conditional; a directive is its keyword followed by a delimiter.
        private static bool StartsWithWord(string text, string word)
        {
            if (!text.StartsWith(word, System.StringComparison.Ordinal)) return false;
            if (text.Length == word.Length) return true;
            char next = text[word.Length];
            return !char.IsLetterOrDigit(next) && next != '_';
        }

        // ── The expression ──────────────────────────────────────────────────────

        /// <summary>
        /// The value of a conditional expression in a player build, with every
        /// <c>UNITY_EDITOR*</c> symbol undefined and every other one UNKNOWN.
        /// An expression that does not parse is unknown too.
        /// </summary>
        /// <remarks>
        /// ⛔ Unknown is a third answer, not a guess at one of the two, and the
        /// first version of this did guess: it read every other symbol as
        /// DEFINED, which is conservative where the symbol appears positively
        /// and the opposite under a negation. <c>#if !UNITY_SERVER</c> — the
        /// client half of a project that also builds a dedicated server — then
        /// evaluated to false and reported a registration excluded from a build
        /// that compiles it, with a ⚠️ naming three causes, none of them the
        /// author's.
        /// </remarks>
        private static Verdict Evaluate(string expression)
        {
            int at = 0;
            Verdict value = Or(expression, ref at);
            SkipSpace(expression, ref at);
            return at >= expression.Length || IsTrailingComment(expression, at)
                ? value
                : Verdict.Unknown;
        }

        private static Verdict Not(Verdict value)
            => value switch
            {
                Verdict.Yes => Verdict.No,
                Verdict.No => Verdict.Yes,
                _ => Verdict.Unknown,
            };

        private static Verdict And(Verdict left, Verdict right)
        {
            if (left == Verdict.No || right == Verdict.No) return Verdict.No;
            return left == Verdict.Yes && right == Verdict.Yes ? Verdict.Yes : Verdict.Unknown;
        }

        private static Verdict Or(Verdict left, Verdict right)
        {
            if (left == Verdict.Yes || right == Verdict.Yes) return Verdict.Yes;
            return left == Verdict.No && right == Verdict.No ? Verdict.No : Verdict.Unknown;
        }

        private static bool IsTrailingComment(string text, int at)
            => at + 1 < text.Length && text[at] == '/' && (text[at + 1] == '/' || text[at + 1] == '*');

        private static Verdict Or(string text, ref int at)
        {
            Verdict value = And(text, ref at);
            while (true)
            {
                SkipSpace(text, ref at);
                if (!Match(text, ref at, "||")) return value;
                // Bound before it is combined: an operand folded into a
                // short-circuiting expression is one the parser may never
                // read, and the cursor would stop where the first true was.
                Verdict right = And(text, ref at);
                value = Or(value, right);
            }
        }

        private static Verdict And(string text, ref int at)
        {
            Verdict value = Equality(text, ref at);
            while (true)
            {
                SkipSpace(text, ref at);
                if (!Match(text, ref at, "&&")) return value;
                Verdict right = Equality(text, ref at);
                value = And(value, right);
            }
        }

        /// <summary>
        /// <c>==</c> and <c>!=</c>, which a conditional expression may use
        /// between two of its own operands.
        /// </summary>
        /// <remarks>
        /// ⛔ Read rather than left to fall out as unparseable: an unparsed
        /// expression is unknown, which leaves the region in — so
        /// <c>#if UNITY_EDITOR == true</c> would have read as compiled into a
        /// player.
        /// </remarks>
        private static Verdict Equality(string text, ref int at)
        {
            Verdict value = Unary(text, ref at);
            while (true)
            {
                SkipSpace(text, ref at);
                bool equal = Match(text, ref at, "==");
                if (!equal && !Match(text, ref at, "!=")) return value;

                Verdict right = Unary(text, ref at);
                if (value == Verdict.Unknown || right == Verdict.Unknown)
                {
                    value = Verdict.Unknown;
                    continue;
                }

                bool same = value == right;
                value = (equal ? same : !same) ? Verdict.Yes : Verdict.No;
            }
        }

        private static Verdict Unary(string text, ref int at)
        {
            SkipSpace(text, ref at);
            if (at < text.Length && text[at] == '!' && !(at + 1 < text.Length && text[at + 1] == '='))
            {
                at++;
                return Not(Unary(text, ref at));
            }

            return Primary(text, ref at);
        }

        private static Verdict Primary(string text, ref int at)
        {
            SkipSpace(text, ref at);
            if (at < text.Length && text[at] == '(')
            {
                at++;
                Verdict inner = Or(text, ref at);
                SkipSpace(text, ref at);
                if (at < text.Length && text[at] == ')') at++;
                return inner;
            }

            int start = at;
            while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_')) at++;
            if (at == start) return Verdict.Unknown;

            string symbol = text.Substring(start, at - start);
            if (symbol == "true") return Verdict.Yes;
            if (symbol == "false") return Verdict.No;

            // ⚠️ Every symbol whose name BEGINS with UNITY_EDITOR, which is the
            // rule rather than the four spellings Unity ships today
            // (UNITY_EDITOR, _WIN, _OSX, _LINUX): a player build defines none
            // of them, and none of them is a name a project would take for its
            // own. Everything else is the project's to define, and this cannot
            // know which builds it defines it for.
            return symbol.StartsWith("UNITY_EDITOR", System.StringComparison.Ordinal)
                ? Verdict.No
                : Verdict.Unknown;
        }

        private static bool Match(string text, ref int at, string token)
        {
            if (at + token.Length > text.Length) return false;
            if (string.CompareOrdinal(text, at, token, 0, token.Length) != 0) return false;
            at += token.Length;
            return true;
        }

        private static void SkipSpace(string text, ref int at)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        }

        // ── The assembly definition, read as the JSON it is ─────────────────────

        /// <summary>
        /// Where <paramref name="key"/> appears as a KEY — opening an entry of
        /// an object — rather than anywhere the characters happen to occur.
        /// </summary>
        /// <remarks>
        /// ⛔ A quoted name is also a legal VALUE:
        /// <c>{"name":"includePlatforms","references":["Editor"]}</c> put the
        /// word where a search for the characters found it, and the colon and
        /// the array that followed belonged to the next entry — an assembly
        /// that names every platform read as the Editor's alone.
        /// </remarks>
        private static int KeyPosition(string json, string key)
        {
            string quoted = "\"" + key + "\"";
            int at = json.IndexOf(quoted, System.StringComparison.Ordinal);
            while (at >= 0)
            {
                int before = at - 1;
                while (before >= 0 && char.IsWhiteSpace(json[before])) before--;

                if (before < 0 || json[before] == '{' || json[before] == ',') return at;

                at = json.IndexOf(quoted, at + quoted.Length, System.StringComparison.Ordinal);
            }

            return -1;
        }

        /// <summary>
        /// The strings of the array <paramref name="key"/> names, or an empty
        /// list when the key is absent, is not an array, or the document ends
        /// inside it.
        /// </summary>
        private static List<string> StringsOfArray(string json, string key)
        {
            var values = new List<string>();
            int at = KeyPosition(json, key);
            if (at < 0) return values;

            at = json.IndexOf(':', at + key.Length);
            if (at < 0) return values;

            at++;
            while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
            if (at >= json.Length || json[at] != '[') return values;

            int close = json.IndexOf(']', at);
            if (close < 0) return values;

            string body = json.Substring(at + 1, close - at - 1);
            int cursor = 0;
            while (true)
            {
                int open = body.IndexOf('"', cursor);
                if (open < 0) break;
                int end = body.IndexOf('"', open + 1);
                if (end < 0) break;
                values.Add(body.Substring(open + 1, end - open - 1));
                cursor = end + 1;
            }

            return values;
        }
    }
}
