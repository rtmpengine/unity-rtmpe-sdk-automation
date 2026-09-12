// RTMPE SDK — Editor/SceneReferenceScanner.cs
//
// Which scene names a project's own code asks for, read out of the source.
//
// The room's scene is a string. Nothing between the host typing it and every
// client failing to load it ever compares that string to the scenes the project
// actually builds: `LoadScene("Map")` compiles, writes a room property, reaches
// every client, and fails there — at which point the only thing anybody sees is
// a room that never settles. This file is the first half of moving that
// discovery back to the desk it was created on.
//
// ⚠️ It reads LITERALS and nothing else, and that limit is a measurement rather
// than a disclaimer: every call site is counted, and a call whose argument is
// built at runtime is reported as UNCHECKED rather than passed over. A scanner
// that quietly skipped what it could not read would report a clean project by
// looking at less of it, which is the one direction a validation must never be
// wrong in.
//
// 🔑 Free of UnityEditor and UnityEngine, like the prefab ledger beside it. The
// files arrive as text through a delegate, so every rule below is reachable
// from a test that supplies a string, and the window supplies the disk.
//
// ⛔ Text, not a syntax tree. Unity gives an Editor script no compiler to ask,
// so the alternative to reading the source is not reading it better — it is not
// reading it at all. What that costs is stated where it is paid: a name in a
// comment must not be reported, a name inside a string must not be reported,
// and both are handled by blanking every comment and every literal before a
// single call site is looked for.

using System;
using System.Collections.Generic;
using System.Text;

namespace RTMPE.Editor
{
    /// <summary>
    /// One call in the project's own code that asks for a scene by name.
    /// </summary>
    public sealed class SceneReference
    {
        /// <summary>The file the call was found in, as the caller named it.</summary>
        public string FilePath { get; }

        /// <summary>1-based line of the method name.</summary>
        public int Line { get; }

        /// <summary>
        /// What the call was made on — <c>NetworkManager.Instance.Scene</c>,
        /// <c>SceneManager</c>, or empty for an unqualified call.
        /// </summary>
        /// <remarks>
        /// Reported rather than judged. A project is free to have a method of
        /// its own called <c>LoadScene</c>, and a row naming its receiver
        /// explains itself; a scanner that instead guessed which receivers are
        /// "really" scene loads would be silently wrong in both directions.
        /// </remarks>
        public string Receiver { get; }

        /// <summary>The method name as written: <c>LoadScene</c> or <c>LoadSceneAsync</c>.</summary>
        public string Method { get; }

        /// <summary>The first argument exactly as it appears in the source.</summary>
        public string Argument { get; }

        /// <summary>
        /// The scene name the call asks for, or null when the argument is not a
        /// plain string literal.
        /// </summary>
        public string SceneName { get; }

        /// <summary>Whether this call's scene name could be read at all.</summary>
        public bool IsLiteral => SceneName != null;

        public SceneReference(string filePath, int line, string receiver, string method,
                              string argument, string sceneName)
        {
            FilePath  = filePath;
            Line      = line;
            Receiver  = receiver ?? string.Empty;
            Method    = method   ?? string.Empty;
            Argument  = argument ?? string.Empty;
            SceneName = sceneName;
        }
    }

    /// <summary>
    /// Finds scene-load calls in C# source text.
    /// </summary>
    public static class SceneReferenceScanner
    {
        /// <summary>The SDK's own entry point, and Unity's synchronous one.</summary>
        public const string LoadSceneMethod = "LoadScene";

        /// <summary>Unity's asynchronous entry point.</summary>
        public const string LoadSceneAsyncMethod = "LoadSceneAsync";

        /// <summary>
        /// How deep an interpolated string may nest before the walk stops
        /// descending into the literals inside it.
        /// </summary>
        /// <remarks>
        /// 🚨 A ceiling on RECURSION, not on taste. Walking a hole and blanking
        /// a literal call each other, so a file holding twenty thousand
        /// unterminated <c>$"{</c> — which need not be valid C#, and which a
        /// generator can emit — overflowed the stack. A StackOverflowException
        /// cannot be caught: in the Editor that is the process dying with the
        /// scene and prefab work in it. Nothing legible nests past a handful,
        /// and past this the hole is walked for its braces alone.
        /// </remarks>
        public const int MaxNesting = 64;

        /// <summary>
        /// The longest a character literal can be written, in characters.
        /// </summary>
        /// <remarks>
        /// <c>'\U00000041'</c> — a quote, a backslash, a <c>U</c>, eight hex
        /// digits and a quote. Nothing legal is longer, which is what lets an
        /// apostrophe be recognised as prose after a bounded look ahead rather
        /// than after a search of the rest of the file.
        /// </remarks>
        public const int MaxCharLiteralLength = 12;

        // Comments and preprocessor directives are blanked to this rather than
        // to a space, so the argument reader can tell "there was a comment
        // here" from "there was nothing here". A literal's own interior is
        // blanked to spaces, which is why the two cannot share a filler.
        private const char Trivia = '\u0001';

        /// <summary>
        /// The longest receiver expression a row carries, in characters.
        /// </summary>
        /// <remarks>
        /// A receiver is shown so a reader can tell the SDK's scene manager from
        /// their own method of the same name. A chained expression across three
        /// lines answers that question no better than its tail does, and would
        /// take the row's width from the part that matters.
        /// </remarks>
        public const int MaxReceiverLength = 60;

        /// <summary>
        /// Every scene-load call in <paramref name="source"/>.
        /// </summary>
        /// <remarks>
        /// Comments and string literals are removed before anything is looked
        /// for, so a scene name written in a comment — or inside a message about
        /// scene loading — is not reported. Interpolation holes are kept,
        /// because a hole is code.
        /// <para>
        /// ⛔ A method DECLARATION named <c>LoadScene</c> is reported too, and
        /// that is a decision rather than an oversight. Telling a declaration
        /// from a call needs the one thing an Editor script has no access to —
        /// a parser — and every text rule that comes close fails in the
        /// direction this whole check forbids: <c>return LoadScene(x)</c>,
        /// <c>await LoadScene(x)</c> and <c>LoadScene(new N())</c> are all
        /// calls whose shape is a declaration's, so a filter accurate enough to
        /// drop the declaration drops them with it. What the declaration costs
        /// is one row in the UNCHECKED column, because a parameter list is
        /// never a literal — the checked count cannot move — and the row prints
        /// its own argument, which reads as <c>LoadScene(string sceneName)</c>
        /// and explains itself.
        /// </para>
        /// </remarks>
        /// <param name="fullyRead">Whether the read reached the end of the
        /// file. False when a token that legally spans lines — a block comment,
        /// a verbatim string, a raw string, an interpolation hole — was opened
        /// and never closed: everything after it was consumed as part of that
        /// token, so the calls in it were never looked for.
        /// <para>
        /// ⛔ The caller owes this a report, because a file read to its tenth
        /// line and counted as read is a project declared clean by looking at
        /// less of it. ⚠️ It NARROWS that failure rather than forbidding it: a
        /// token opened and never closed which happens to meet a terminator
        /// further down — the quote of a later string — ends there, and text
        /// cannot tell that from a token whose author closed it.
        /// </para></param>
        public static IReadOnlyList<SceneReference> Scan(string filePath, string source,
                                                         out bool fullyRead)
        {
            fullyRead = true;

            var found = new List<SceneReference>();
            if (string.IsNullOrEmpty(source)) return found;

            var literals = new List<LiteralSpan>();
            char[] code = Blank(source, literals, ref fullyRead);

            // ⛔ Built on the first call site and not before. The table is one
            // int per character, so on the overwhelming majority of a project's
            // files — the ones naming no scene at all — it is a large array
            // allocated to answer nothing: over this repository it doubled the
            // pass's allocation, and any file past ~21,800 characters puts it on
            // the large object heap.
            int[] closes = null;
            var lines = new LineCursor();

            int i = 0;
            while (i < code.Length)
            {
                if (!IsIdentifierStart(code[i])) { i++; continue; }

                int nameStart = i;
                while (i < code.Length && IsIdentifierPart(code[i])) i++;
                int nameEnd = i;

                // ⛔ The whole token, never a prefix. `LoadSceneAsync` contains
                // `LoadScene`, and a substring search would report one call as
                // two — and would report `MyLoadScene` as a scene load.
                string method = new string(code, nameStart, nameEnd - nameStart);
                if (method != LoadSceneMethod && method != LoadSceneAsyncMethod) continue;

                // ⛔ A generic call is still a call. `LoadScene<T>("Arena")`
                // is a shape a project's own overload takes, and a scanner
                // requiring the parenthesis to follow the NAME drops those
                // sites with no row at all — which is a hole in the one claim
                // this design rests on, that every call site is counted.
                int open = SkipSpace(code, SkipGenericArguments(code, nameEnd));
                if (open >= code.Length || code[open] != '(') continue;

                closes = closes ?? MatchingParens(code);

                int close = closes[open];
                if (close < 0) continue;

                if (!FirstArgument(code, open + 1, close, out int argStart, out int argEnd)) continue;

                Trim(source, code, ref argStart, ref argEnd);
                StripArgumentName(code, source, ref argStart, ref argEnd);
                Unwrap(code, closes, source, ref argStart, ref argEnd);

                string sceneName = LiteralValue(literals, argStart, argEnd);
                string argument  = argEnd > argStart
                    ? source.Substring(argStart, argEnd - argStart)
                    : string.Empty;

                found.Add(new SceneReference(
                    filePath,
                    lines.At(source, nameStart),
                    ReceiverOf(source, code, nameStart),
                    method,
                    argument,
                    sceneName));

                // ⛔ Just inside the parenthesis, not past the whole call. A
                // scan resuming after the closing paren steps over the
                // argument list — and a scene load written inside one is a
                // readable call site dropped with no row at all, which is the
                // one claim this design rests on.
                i = open + 1;
            }

            return found;
        }

        // ── The blanking pass ────────────────────────────────────────────────

        // One literal token, by position in the source, with the value it
        // carries. Recorded during blanking because that is the only pass that
        // knows where a literal began and how it was escaped.
        private struct LiteralSpan
        {
            public int Start;      // index of the first character of the token
            public int End;        // index just past the closing quote
            public string Value;   // decoded, or null when an escape was unreadable
        }

        // Replaces every comment and every string or character literal with
        // spaces, preserving length and line breaks so an index into the result
        // is an index into the source. Interpolation holes survive, because a
        // hole is code and a call inside one is a call.
        private static char[] Blank(string src, List<LiteralSpan> literals, ref bool complete)
        {
            char[] outp = src.ToCharArray();
            int i = 0;

            while (i < src.Length)
            {
                char c = src[i];

                if (c == '/' && i + 1 < src.Length && (src[i + 1] == '/' || src[i + 1] == '*'))
                {
                    i = BlankComment(src, i, outp, ref complete);
                    continue;
                }

                // A directive is free text, and free text is not code: a `#line`
                // names a path, a `#region` names a section in English, and
                // neither is lexed. `BlankCharLiteral` states what an apostrophe
                // in free text costs when it is read as a token.
                if (c == '#' && StartsTheLine(src, i))
                {
                    while (i < src.Length && src[i] != '\n') { outp[i] = Trivia; i++; }
                    continue;
                }

                if (c == '\'')
                {
                    i = BlankCharLiteral(src, i, outp);
                    continue;
                }

                int prefix = StringPrefixLength(src, i, out bool verbatim, out bool interpolated);
                if (prefix > 0)
                {
                    i = OpensRawString(src, i, prefix, verbatim, out int quotes)
                        ? BlankRawString(src, i, quotes, outp, ref complete)
                        : BlankStringLiteral(src, i, prefix, verbatim, interpolated, outp, literals,
                                             0, ref complete);
                    continue;
                }

                i++;
            }

            return outp;
        }

        // Blanks the comment opening at `start` and answers the index after it.
        //
        // ⛔ A block comment that never closes has eaten the rest of the file,
        // and unlike an apostrophe there is no recovery to make: it legally
        // spans lines, so where the author meant it to end is not a question
        // text can answer. What is answerable is that the read stopped there,
        // and saying so is the difference between a short pass and a clean one.
        private static int BlankComment(string src, int start, char[] outp, ref bool complete)
        {
            int i = start;

            if (src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') { outp[i] = Trivia; i++; }
                return i;
            }

            outp[i] = Trivia; outp[i + 1] = Trivia;
            i += 2;
            while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
            {
                if (src[i] != '\n') outp[i] = Trivia;
                i++;
            }

            if (i < src.Length) { outp[i] = Trivia; outp[i + 1] = Trivia; return i + 2; }

            complete = false;
            return i;
        }

        // Whether nothing but whitespace precedes this index on its line, which
        // is what makes a `#` a directive rather than something inside a token.
        private static bool StartsTheLine(string src, int i)
        {
            for (int back = i - 1; back >= 0; back--)
            {
                char c = src[back];
                if (c == '\n') return true;
                if (!char.IsWhiteSpace(c)) return false;
            }
            return true;
        }

        // Whether the literal opening here is a raw string, and how many quotes
        // open it.
        //
        // 🚨 A run of three quotes is not enough on its own: `@""""` is the
        // ordinary spelling of a VERBATIM string holding one quote, it is C# 9
        // and it is written wherever a project builds a regex — and read as a
        // raw string it searches for a closing run of four that never comes, so
        // it consumes the rest of the file and every call in it. The `@` settles
        // it, because the language admits no verbatim raw string: `"""` and
        // `$"""` open one, `@"""` does not.
        private static bool OpensRawString(string src, int i, int prefix, bool verbatim,
                                           out int quotes)
        {
            quotes = QuoteRunAt(src, i + prefix - 1);
            return !verbatim && quotes >= 3;
        }

        private static int QuoteRunAt(string src, int i)
        {
            int run = 0;
            while (i + run < src.Length && src[i + run] == '"') run++;
            return run;
        }

        // ⛔ A raw string is blanked whole and recorded as no literal at all.
        // Unity compiles C# 9, so one cannot appear in a project today — but
        // read as ordinary quotes it becomes a run of short literals with the
        // text between them left as CODE, and a `LoadScene("Ghost")` written
        // inside a document string is then reported as a call nobody made. A
        // finding pointing at a line whose author wrote no such call is the
        // fastest way to lose a reader's trust.
        private static int BlankRawString(string src, int start, int quotes, char[] outp,
                                          ref bool complete)
        {
            int i = start;
            while (i < start + quotes && i < src.Length) { outp[i] = ' '; i++; }

            while (i < src.Length)
            {
                if (src[i] == '"' && QuoteRunAt(src, i) >= quotes)
                {
                    int run = QuoteRunAt(src, i);
                    for (int q = 0; q < run && i < src.Length; q++, i++) outp[i] = ' ';
                    return i;
                }

                if (src[i] != '\n') outp[i] = ' ';
                i++;
            }

            complete = false;
            return i;
        }

        // The `$`, `@` and `"` a literal opens with, or 0 when this is not one.
        // Both orders are legal, and a project that writes paths uses `@`.
        private static int StringPrefixLength(string src, int i, out bool verbatim, out bool interpolated)
        {
            verbatim = false;
            interpolated = false;

            int j = i;
            while (j < src.Length && (src[j] == '$' || src[j] == '@'))
            {
                if (src[j] == '$') interpolated = true; else verbatim = true;
                j++;
            }

            if (j < src.Length && src[j] == '"') return j - i + 1;

            verbatim = false;
            interpolated = false;
            return 0;
        }

        // 🚨 Decided before anything is written, because writing forward from
        // an apostrophe deletes code. Most apostrophes a scanner meets open no
        // literal at all: C# does not lex the excluded arm of a `#if`, and does
        // not lex a directive's own text, so `This can't be parsed here` in
        // either is a COMPILING file. Read as a character literal it runs to the
        // next apostrophe or to the end of the file, taking every call site
        // after it — not as an unchecked row but as no row, while the coverage
        // line still counts the file as read.
        private static int BlankCharLiteral(string src, int start, char[] outp)
        {
            int close = CharLiteralEnd(src, start);
            if (close < 0)
            {
                outp[start] = ' ';
                return start + 1;
            }

            for (int i = start; i <= close; i++) outp[i] = ' ';
            return close + 1;
        }

        // The quote closing the character literal at `start`, or -1 when what
        // is there is an apostrophe.
        //
        // 🔑 Bounded by the longest literal the language admits, and by the
        // line. Both bounds are the language's own — a character literal may
        // neither exceed that length nor contain a newline — so no valid
        // program is read differently. What the length buys is the pairing an
        // apostrophe must NOT make: without it, two apostrophes far apart on
        // one line of prose bracket everything between them, and a call written
        // there is blanked away with no row at all.
        private static int CharLiteralEnd(string src, int start)
        {
            int limit = Math.Min(src.Length, start + MaxCharLiteralLength);
            for (int i = start + 1; i < limit; i++)
            {
                char c = src[i];
                if (c == '\n') return -1;
                // ⛔ The escape consumes one character and never a newline: a
                // literal may not contain one, so a backslash at the end of a
                // line ends the search rather than reaching over it.
                if (c == '\\')
                {
                    if (i + 1 >= limit || src[i + 1] == '\n') return -1;
                    i++;
                    continue;
                }
                if (c == '\'') return i;
            }
            return -1;
        }

        // Blanks one string literal and records its value when it has one.
        // 🔑 An interpolated string is never recorded as a literal even when it
        // has no holes: what it renders is a question about the whole expression
        // and this layer answers questions about text.
        private static int BlankStringLiteral(string src, int start, int prefix, bool verbatim,
                                              bool interpolated, char[] outp,
                                              List<LiteralSpan> literals, int depth,
                                              ref bool complete)
        {
            var value = new StringBuilder();
            bool readable = true;
            // Whether a closing quote was reached. It is the READ this decides
            // and not the syntax: an unterminated ordinary literal is malformed
            // and still leaves the next line to be scanned, which is all the
            // coverage count claims.
            //
            bool bounded  = false;

            int i = start;
            for (int p = 0; p < prefix; p++, i++) outp[i] = ' ';

            while (i < src.Length)
            {
                char c = src[i];

                if (c == '"')
                {
                    if (verbatim && i + 1 < src.Length && src[i + 1] == '"')
                    {
                        value.Append('"');
                        outp[i] = ' '; outp[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    outp[i] = ' ';
                    i++;
                    bounded = true;
                    break;
                }

                if (!verbatim && c == '\\')
                {
                    int after = DecodeEscape(src, i, value);
                    // ⛔ An escape nobody can read still stops at the line. C#
                    // ends an ordinary literal at a newline whatever precedes
                    // it, so stepping over one here would carry the token onto
                    // the next line and swallow the code there — the defect this
                    // file exists to refuse, one quote over from the apostrophe.
                    if (after < 0)
                    {
                        readable = false;
                        after = i + 1 < src.Length && src[i + 1] != '\n'
                            ? i + 2
                            : i + 1;
                    }
                    for (; i < after; i++) if (src[i] != '\n') outp[i] = ' ';
                    continue;
                }

                if (interpolated && (c == '{' || c == '}'))
                {
                    if (i + 1 < src.Length && src[i + 1] == c)
                    {
                        value.Append(c);
                        outp[i] = ' '; outp[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    if (c == '{')
                    {
                        // ⛔ The hole is left exactly as written. It is code, and
                        // a scene load inside one is a scene load.
                        readable = false;
                        outp[i] = ' ';
                        i = SkipHole(src, i + 1, outp, literals, depth + 1, ref complete);
                        continue;
                    }
                }

                if (!verbatim && c == '\n') break;   // the language ends it here

                value.Append(c);
                if (c != '\n') outp[i] = ' ';
                i++;
            }

            // ⛔ Only a VERBATIM literal can lose the rest of the file. An
            // ordinary one is bounded by its line — by the newline that ends it,
            // or by the end of the file, which is the end of the last line — so
            // there is never anything below it that went unread. A verbatim one
            // legally spans lines, so running out of file inside it is the block
            // comment's failure under another quote.
            if (verbatim && !bounded) complete = false;

            if (!interpolated)
            {
                literals.Add(new LiteralSpan
                {
                    Start = start,
                    End   = i,
                    Value = readable ? value.ToString() : null,
                });
            }

            return i;
        }

        // Walks an interpolation hole to its closing brace, blanking any literal
        // inside it and leaving the rest as code.
        private static int SkipHole(string src, int i, char[] outp, List<LiteralSpan> literals,
                                    int depth, ref bool complete)
        {
            int braces = 1;
            while (i < src.Length && braces > 0)
            {
                char c = src[i];

                // ⛔ Past the ceiling the hole is walked for its braces alone.
                // What that costs is a call written inside a sixty-fifth nested
                // interpolation; what it buys is that a file cannot end the
                // Editor process, which nothing above this can catch.
                if (depth < MaxNesting)
                {
                    int prefix = StringPrefixLength(src, i, out bool verbatim, out bool interpolated);
                    if (prefix > 0)
                    {
                        i = OpensRawString(src, i, prefix, verbatim, out int quotes)
                            ? BlankRawString(src, i, quotes, outp, ref complete)
                            : BlankStringLiteral(src, i, prefix, verbatim, interpolated,
                                                 outp, literals, depth, ref complete);
                        continue;
                    }

                    // 🚨 A hole is CODE, and code has comments and character
                    // literals in it. Left alone, a scene name written in a
                    // comment inside one is reported as a call nobody made —
                    // the false red this file's header forbids — and a `}` in a
                    // character literal ends the hole early, which moves every
                    // brace after it. Both are legal C# 9 inside a hole.
                    if (c == '/' && i + 1 < src.Length && (src[i + 1] == '/' || src[i + 1] == '*'))
                    {
                        i = BlankComment(src, i, outp, ref complete);
                        continue;
                    }

                    if (c == '\'')
                    {
                        i = BlankCharLiteral(src, i, outp);
                        continue;
                    }
                }

                if (c == '{') braces++;
                else if (c == '}')
                {
                    braces--;
                    // 🚨 The hole's own closing brace is BLANKED, and its
                    // opening one already was. Left in place it is an unmatched
                    // `}` in text that is read for balance, so the very next
                    // argument list is judged to end at it — and a call written
                    // beside an interpolated string disappears from the scan
                    // entirely rather than being reported wrongly. Braces
                    // NESTED inside the hole are the hole's own code and stay,
                    // because they are already balanced.
                    if (braces == 0) outp[i] = ' ';
                }

                i++;
            }

            // ⛔ A hole that exhausts the file has consumed everything after it
            // as its own contents. The literal around it cannot report that on
            // its behalf — an ordinary interpolated string is line-bounded, so
            // it has nothing to say about a run to the end of the file.
            if (braces > 0) complete = false;
            return i;
        }

        // Consumes one escape sequence, appending what it stands for. Answers
        // -1 for an escape it cannot read, which makes the whole literal
        // unreadable rather than silently wrong by one character.
        private static int DecodeEscape(string src, int i, StringBuilder value)
        {
            if (i + 1 >= src.Length) return -1;

            char e = src[i + 1];
            switch (e)
            {
                case '\\': value.Append('\\'); return i + 2;
                case '"':  value.Append('"');  return i + 2;
                case '\'': value.Append('\''); return i + 2;
                case '0':  value.Append('\0'); return i + 2;
                case 'a':  value.Append('\a'); return i + 2;
                case 'b':  value.Append('\b'); return i + 2;
                case 'f':  value.Append('\f'); return i + 2;
                case 'n':  value.Append('\n'); return i + 2;
                case 'r':  value.Append('\r'); return i + 2;
                case 't':  value.Append('\t'); return i + 2;
                case 'v':  value.Append('\v'); return i + 2;
                case 'u':
                {
                    if (i + 5 >= src.Length) return -1;
                    string hex = src.Substring(i + 2, 4);
                    if (!ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                         System.Globalization.CultureInfo.InvariantCulture, out ushort code))
                        return -1;
                    value.Append((char)code);
                    return i + 6;
                }
                default:
                    return -1;
            }
        }

        // ── Reading the call ─────────────────────────────────────────────────

        // Whitespace and anything blanked as trivia — a comment, a directive.
        // Reading them apart would make `LoadScene/*x*/(` stop being a call.
        private static bool IsSkippable(char c) => char.IsWhiteSpace(c) || c == Trivia;

        private static int SkipSpace(char[] code, int i)
        {
            while (i < code.Length && IsSkippable(code[i])) i++;
            return i;
        }

        // The `<...>` of a generic invocation, or the index unchanged. Balanced
        // rather than matched to the first `>`, so a nested type argument is one
        // span; a comparison does not reach here, because what follows a
        // non-generic use of `<` is not a parenthesis.
        private static int SkipGenericArguments(char[] code, int i)
        {
            int at = SkipSpace(code, i);
            if (at >= code.Length || code[at] != '<') return i;

            int depth = 0;
            for (int j = at; j < code.Length; j++)
            {
                char c = code[j];
                if (c == '<') depth++;
                else if (c == '>')
                {
                    depth--;
                    if (depth == 0) return j + 1;
                }
                else if (c == ';' || c == '{' || c == '}' || c == '(' || c == ')')
                {
                    return i;
                }
            }

            return i;
        }

        // For every bracket in `code`, the index of the parenthesis that closes
        // it, or -1 — which covers a bracket closed by the wrong kind and one
        // never closed at all.
        //
        // 🔑 One pass for the whole file rather than one scan per call site.
        // Scanning from each opener re-reads the tail of the file for every
        // unmatched bracket before it — 16,000 unclosed `LoadScene(` took 5.6 s
        // in a debug build and 1.9 s optimised, against 101 ms and 43 ms for
        // 2,000 — on the thread that draws the Editor.
        //
        // ⚠️ Measured equivalent to the per-opener scan it replaced for every
        // PARENTHESIS, which is the only entry any caller reads: 0 differences
        // over 937,924 openers, exhaustively across every bracket string up to
        // length seven, and over all 122,840 in this repository. ⛔ For a
        // bracket of another kind the two deliberately DISAGREE — `[)` answers
        // the closer there and -1 here — and that difference is the contract
        // below rather than an accident.
        private static int[] MatchingParens(char[] code)
        {
            var closes = new int[code.Length];
            for (int i = 0; i < closes.Length; i++) closes[i] = -1;

            var open = new Stack<int>();
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '(' || c == '[' || c == '{') open.Push(i);
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (open.Count == 0) continue;
                    int start = open.Pop();
                    // ⛔ A parenthesis only. `(` closed by `]` is a bracket the
                    // caller must not read an argument list out of, and the scan
                    // it replaces answered -1 for exactly that shape.
                    if (c == ')' && code[start] == '(') closes[start] = i;
                }
            }

            return closes;
        }

        // The span of the first argument, or false for a call with none.
        private static bool FirstArgument(char[] code, int from, int close,
                                          out int start, out int end)
        {
            start = from;
            end   = close;

            int depth = 0;
            for (int i = from; i < close; i++)
            {
                char c = code[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0) { end = i; break; }
            }

            return end > start;
        }

        // 🔑 Whitespace is read from the SOURCE and trivia from the blanked
        // copy, because neither alone is enough: a literal's interior is blanked
        // to spaces, so trimming the copy would eat the literal, and a comment
        // is real characters in the source, so trimming that would leave
        // `/*x*/"Arena"` looking like an expression. Reported unchecked, a call
        // like that pads the honesty column with a row whose name is in it.
        private static void Trim(string src, char[] code, ref int start, ref int end)
        {
            while (start < end && (char.IsWhiteSpace(src[start]) || code[start] == Trivia)) start++;
            while (end > start && (char.IsWhiteSpace(src[end - 1]) || code[end - 1] == Trivia)) end--;
        }

        // Redundant parentheses around the whole argument. `LoadScene(("A"))` is
        // the same call with the same name in it, and calling that unreadable
        // would be the check declining to read what it can see.
        private static void Unwrap(char[] code, int[] closes, string src, ref int start, ref int end)
        {
            while (end - start >= 2 && code[start] == '(' && closes[start] == end - 1)
            {
                start++;
                end--;
                Trim(src, code, ref start, ref end);
            }
        }

        // Removes a `name:` prefix from a named argument.
        // 🔑 Without this a caller writing `LoadScene(sceneName: "Arena")` — the
        // spelling an IDE offers — reads as an expression rather than a literal,
        // and the check reports it as unreadable while looking straight at it.
        private static void StripArgumentName(char[] code, string src, ref int start, ref int end)
        {
            int i = start;
            if (i >= end || !IsIdentifierStart(code[i])) return;
            while (i < end && IsIdentifierPart(code[i])) i++;

            int colon = SkipSpace(code, i);
            if (colon >= end || code[colon] != ':') return;
            // `::` is a namespace alias, not an argument name.
            if (colon + 1 < end && code[colon + 1] == ':') return;

            start = colon + 1;
            Trim(src, code, ref start, ref end);
        }

        // The literal the span covers exactly, or null when it covers anything
        // else: a variable, a concatenation, a `nameof`, an interpolation.
        private static string LiteralValue(List<LiteralSpan> literals, int start, int end)
        {
            for (int i = 0; i < literals.Count; i++)
            {
                if (literals[i].Start == start && literals[i].End == end) return literals[i].Value;
            }
            return null;
        }

        // The expression the call was made on, read backwards from the method
        // name. Answers empty for an unqualified call, which is a call this
        // scanner is least sure about and says so by naming nothing.
        private static string ReceiverOf(string src, char[] code, int nameStart)
        {
            int i = nameStart - 1;
            while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
            if (i < 0 || code[i] != '.') return string.Empty;

            int end = i;   // exclusive of the dot
            i--;

            int depth = 0;
            while (i >= 0)
            {
                char c = code[i];
                if (c == ')' || c == ']') { depth++; i--; continue; }
                if (c == '(' || c == '[')
                {
                    if (depth == 0) break;
                    depth--; i--; continue;
                }
                if (depth > 0) { i--; continue; }
                if (IsIdentifierPart(c) || c == '.' || c == '?') { i--; continue; }
                if (char.IsWhiteSpace(c) && i > 0 && code[i - 1] == '.') { i--; continue; }
                break;
            }

            int start = i + 1;
            while (start < end && char.IsWhiteSpace(src[start])) start++;
            if (start >= end) return string.Empty;

            // ⚠️ The null-conditional belongs to the CALL, not to the
            // receiver: `manager.Scene?.LoadScene(…)` is a call on
            // `manager.Scene`, and a row printing `manager.Scene?` names an
            // expression nobody wrote.
            string receiver = src.Substring(start, end - start).Trim().TrimEnd('?');

            // A walk that stopped inside an expression it cannot read — a
            // generic call's `>` ends it — leaves punctuation and no name.
            // Printing `().LoadScene(...)` names an expression nobody wrote,
            // which is the rule stated just above about the null-conditional.
            bool named = false;
            foreach (char c in receiver) { if (IsIdentifierPart(c)) { named = true; break; } }
            if (!named) return string.Empty;

            return receiver.Length > MaxReceiverLength
                ? "…" + receiver.Substring(receiver.Length - MaxReceiverLength)
                : receiver;
        }

        // The 1-based line of an index, counted forward from the last one asked
        // for.
        //
        // 🔑 Call sites are found in order, so the cursor only ever moves
        // forward and the whole file is counted once however many rows it
        // yields. Counting from the start for each row re-reads the file per
        // call site, which is quadratic in a file that has many.
        private sealed class LineCursor
        {
            private int _index = 0;
            private int _line  = 1;

            public int At(string src, int index)
            {
                if (index < _index) return LineOf(src, index);

                while (_index < index && _index < src.Length)
                {
                    if (src[_index] == '\n') _line++;
                    _index++;
                }
                return _line;
            }
        }

        // ⛔ Kept for the cursor's own backward arm, which nothing reaches
        // today: rows arrive in order. It is the answer the cursor is defined
        // against, so a future caller that asks out of order gets the same
        // number rather than a quietly wrong one.
        private static int LineOf(string src, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < src.Length; i++) if (src[i] == '\n') line++;
            return line;
        }

        private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

        private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    }
}
