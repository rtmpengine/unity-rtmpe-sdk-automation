// RTMPE SDK — Editor/ConversionScriptScanner.cs
//
// What a one-file conversion leaves behind in the project's other scripts.
//
// The engine rewrites the script it converts and nothing else. A converted
// method's calls in its own class become sends; a direct call from another
// script keeps compiling and keeps running the method locally, on the calling
// client only. The engine cannot see those calls, so this names the scripts that
// mention the member.
//
// Matched on the identifier, outside comments and preprocessor lines: a method
// group passed to AddListener, a SendMessage or Invoke string, and an ordinary
// call are all the same name. The scan says too much rather than too little — a same-named member
// of an unrelated type is reported too, and a reader can dismiss it.
//
// Free of UnityEditor and UnityEngine: the caller supplies the text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RTMPE.Editor
{
    /// <summary>A C# script's project-relative path and text, as the caller read it.</summary>
    public readonly struct ScriptSource
    {
        public ScriptSource(string scriptPath, string text)
        {
            ScriptPath = scriptPath;
            Text = text;
        }

        public string ScriptPath { get; }

        public string Text { get; }
    }

    /// <summary>One line of another script that names a converted member.</summary>
    public sealed class ScriptNameSighting
    {
        public ScriptNameSighting(string scriptPath, int line, string name, string excerpt)
        {
            ScriptPath = scriptPath;
            Line = line;
            Name = name;
            Excerpt = excerpt;
        }

        public string ScriptPath { get; }

        /// <summary>The 1-based line number of the sighting.</summary>
        public int Line { get; }

        /// <summary>The converted member this matched.</summary>
        public string Name { get; }

        /// <summary>The line, trimmed and cut to <see cref="ConversionScriptScanner.MaxExcerptLength"/>.</summary>
        public string Excerpt { get; }
    }

    /// <summary>What one scan of the project's scripts looked at and what it found.</summary>
    public sealed class ConversionScriptScan
    {
        public ConversionScriptScan(IReadOnlyList<ScriptNameSighting> sightings, int scriptsRead, bool truncated)
        {
            Sightings = sightings ?? throw new ArgumentNullException(nameof(sightings));
            ScriptsRead = scriptsRead;
            Truncated = truncated;
        }

        public IReadOnlyList<ScriptNameSighting> Sightings { get; }

        /// <summary>
        /// How many scripts were read. When it is zero, an empty
        /// <see cref="Sightings"/> list says nothing about the project.
        /// </summary>
        public int ScriptsRead { get; }

        /// <summary>
        /// Whether a script reached <see cref="ConversionScriptScanner.MaxSightingsPerScript"/>
        /// sightings or <see cref="ConversionScriptScanner.MaxInterpolationNesting"/>; the
        /// rest of that script was not searched.
        /// </summary>
        public bool Truncated { get; }
    }

    /// <summary>
    /// Searches C# scripts other than the converted one for the name of the
    /// member a conversion changed.
    /// </summary>
    /// <remarks>
    /// A conversion rewrites the converted script only. A call found here is
    /// still a direct call and runs the member locally.
    /// </remarks>
    public static class ConversionScriptScanner
    {
        /// <summary>The most sightings reported for one script.</summary>
        public const int MaxSightingsPerScript = 64;

        /// <summary>The longest excerpt reported for a sighting, in characters.</summary>
        public const int MaxExcerptLength = 160;

        /// <summary>
        /// The deepest interpolated string inside another one's hole that is read;
        /// a script nested deeper stops there and the scan is marked truncated.
        /// </summary>
        public const int MaxInterpolationNesting = 64;

        /// <summary>
        /// Searches <paramref name="scripts"/> for <paramref name="member"/> as an
        /// identifier outside comments, one sighting per line.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="scripts"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="member"/> is null, empty or whitespace.
        /// </exception>
        public static ConversionScriptScan Scan(IEnumerable<ScriptSource> scripts, string member)
        {
            if (scripts == null) throw new ArgumentNullException(nameof(scripts));
            if (string.IsNullOrWhiteSpace(member))
                throw new ArgumentException("name the member to look for", nameof(member));
            member = member.Trim();

            var sightings = new List<ScriptNameSighting>();
            int read = 0;
            bool truncated = false;

            foreach (var script in scripts)
            {
                if (script.Text == null) continue;

                read++;
                if (!ScanOne(script, member, sightings)) truncated = true;
            }

            return new ConversionScriptScan(sightings, read, truncated);
        }

        /// <summary>
        /// Returns a one-line summary of what <paramref name="scan"/> read and found.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="scan"/> is <see langword="null"/>.
        /// </exception>
        public static string Describe(ConversionScriptScan scan)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));

            if (scan.ScriptsRead == 0)
            {
                return "no other scripts were read, so this says nothing about them";
            }

            string looked = "read " + Count(scan.ScriptsRead, "other script");
            if (scan.Sightings.Count == 0)
            {
                return looked + (scan.Truncated
                    ? ", and none names it in what was read — a script hit a scan limit, so there may be more"
                    : ", and none names it");
            }

            return looked + "; " + Count(scan.Sightings.Count, "line") + " name it"
                + (scan.Truncated ? " (a script hit a scan limit; there may be more)" : string.Empty)
                + ". A direct call there is not rewritten: it runs the method locally, on the calling "
                + "client only";
        }

        // False when a cap or the nesting limit stopped the scan.
        private static bool ScanOne(ScriptSource script, string member, List<ScriptNameSighting> sightings)
        {
            var lexer = new Lexer(script, member, sightings);
            return lexer.Code(inHole: false, verbatimHole: false);
        }

        // Reads C# the way the compiler splits it into comments, directives,
        // literals and identifiers, closely enough that a comment marker inside a
        // literal or a directive never hides the source after it.
        private sealed class Lexer
        {
            private readonly ScriptSource _script;
            private readonly string _text;
            private readonly string _member;
            private readonly List<ScriptNameSighting> _sightings;
            private int _at;
            private int _line = 1;
            private int _lineStart;
            private int _lastReportedLine;
            private int _found;
            private bool _lineHasCode;
            private int _nesting;

            public Lexer(ScriptSource script, string member, List<ScriptNameSighting> sightings)
            {
                _script = script;
                _text = script.Text;
                _member = member;
                _sightings = sightings;
            }

            // Source code up to the end of the text or, inside an interpolation
            // hole, up to the brace that closes it. False when the cap was reached.
            public bool Code(bool inHole, bool verbatimHole)
            {
                int braces = 0;
                int nesting = 0;

                while (_at < _text.Length)
                {
                    if (TryNewLine()) continue;

                    char c = _text[_at];

                    if (!inHole && c == '#' && !_lineHasCode)
                    {
                        // A directive's text is neither code nor a comment opener.
                        SkipToEndOfLine();
                        continue;
                    }

                    if (!char.IsWhiteSpace(c)) _lineHasCode = true;

                    // In a regular string's hole a line comment would swallow the
                    // string's end, so only a verbatim string's hole can hold one.
                    if ((!inHole || verbatimHole) && c == '/' && Peek(1) == '/')
                    {
                        SkipToEndOfLine();
                        continue;
                    }

                    if (c == '/' && Peek(1) == '*')
                    {
                        BlockComment();
                        continue;
                    }

                    if (inHole)
                    {
                        if (c == '(' || c == '[')
                        {
                            nesting++;
                        }
                        else if ((c == ')' || c == ']') && nesting > 0)
                        {
                            nesting--;
                        }
                        else if (c == '{')
                        {
                            braces++;
                        }
                        else if (c == '}' && braces-- == 0)
                        {
                            _at++;
                            return true;
                        }
                        else if (c == ':' && braces == 0 && nesting == 0
                            && Peek(1) != ':' && (_at == 0 || _text[_at - 1] != ':'))
                        {
                            // What follows the format separator is string text.
                            _at++;
                            return FormatText(verbatimHole);
                        }
                    }

                    if (c == '"' || c == '\'')
                    {
                        if (!Literal()) return false;
                        continue;
                    }

                    if (IsIdentifierStart(c) || UnicodeEscapeAt(_at, out _, out _))
                    {
                        // Never stop advancing: a scan that hangs freezes the editor.
                        int before = _at;
                        if (!Identifier()) return false;
                        if (_at == before) _at++;
                        continue;
                    }

                    _at++;
                }

                return true;
            }

            // A delimited comment. One that reaches a conditional directive ends
            // there: it may be disabled text the compiler never read as a comment,
            // and reading what follows as code can only report too much.
            private void BlockComment()
            {
                _at += 2;
                while (_at < _text.Length && !(_text[_at] == '*' && Peek(1) == '/'))
                {
                    if (TryNewLine())
                    {
                        if (AtConditionalDirective()) return;
                        continue;
                    }

                    _at++;
                }

                _at = Math.Min(_text.Length, _at + 2);
            }

            // An interpolation's format text, up to the brace that closes its hole,
            // read by the enclosing string's rules. A quote or, in a regular string,
            // a line end is left for the enclosing literal to end on.
            private bool FormatText(bool verbatim)
            {
                int segment = _at;
                while (_at < _text.Length)
                {
                    if (NewLineWidth(_at) > 0)
                    {
                        if (!Search(segment, _at, verbatim)) return false;
                        if (!verbatim) return true;
                        TryNewLine();
                        segment = _at;
                        continue;
                    }

                    char c = _text[_at];
                    if (!verbatim && c == '\\')
                    {
                        _at = Math.Min(_text.Length, _at + (NewLineWidth(_at + 1) > 0 ? 1 : 2));
                        continue;
                    }

                    if (c == '"')
                    {
                        if (verbatim && Peek(1) == '"')
                        {
                            _at += 2;
                            continue;
                        }

                        return Search(segment, _at, verbatim);
                    }

                    if (c == '}')
                    {
                        if (!Search(segment, _at, verbatim)) return false;
                        _at++;
                        return true;
                    }

                    _at++;
                }

                return Search(segment, _text.Length, verbatim);
            }

            // A string or character literal opening at _at, with any $ and @ in
            // front of it. Its contents are searched, because SendMessage and
            // Invoke name a method in a string.
            private bool Literal()
            {
                char quote = _text[_at];
                bool verbatim = false;
                bool interpolated = false;
                for (int back = 1; back <= 2 && _at - back >= 0 && quote == '"'; back++)
                {
                    char p = _text[_at - back];
                    if (p == '@') verbatim = true;
                    else if (p == '$') interpolated = true;
                    else break;
                }

                _at++;
                int segment = _at;
                while (_at < _text.Length)
                {
                    int newLine = NewLineWidth(_at);
                    if (newLine > 0)
                    {
                        // A regular literal cannot span lines: it is unterminated.
                        if (!Search(segment, _at, verbatim)) return false;
                        if (!verbatim) return true;
                        TryNewLine();
                        segment = _at;
                        continue;
                    }

                    char c = _text[_at];

                    if (interpolated && c == '{')
                    {
                        if (Peek(1) == '{')
                        {
                            _at += 2;
                            continue;
                        }

                        if (!Search(segment, _at, verbatim)) return false;
                        _at++;
                        // Each hole is a nested call, so the depth bounds the stack.
                        if (_nesting == MaxInterpolationNesting) return false;
                        _nesting++;
                        bool read = Code(inHole: true, verbatimHole: verbatim);
                        _nesting--;
                        if (!read) return false;
                        segment = _at;
                        continue;
                    }

                    if (!verbatim && c == '\\')
                    {
                        _at = Math.Min(_text.Length, _at + (NewLineWidth(_at + 1) > 0 ? 1 : 2));
                        continue;
                    }

                    if (c == quote)
                    {
                        if (verbatim && Peek(1) == quote)
                        {
                            _at += 2;
                            continue;
                        }

                        if (!Search(segment, _at, verbatim)) return false;
                        _at++;
                        return true;
                    }

                    _at++;
                }

                return Search(segment, _text.Length, verbatim);
            }

            // An identifier at _at, compared as the compiler compares one: Unicode
            // escapes decoded and formatting characters removed.
            private bool Identifier()
            {
                int start = _at;
                bool altered = false;
                var decoded = new StringBuilder();
                while (_at < _text.Length)
                {
                    char c = _text[_at];
                    if (IsIdentifierPart(c))
                    {
                        if (IsFormatting(c)) altered = true;
                        else decoded.Append(c);
                        _at++;
                    }
                    else if (UnicodeEscapeAt(_at, out string value, out int width))
                    {
                        altered = true;
                        if (!(value.Length == 1 && IsFormatting(value[0]))) decoded.Append(value);
                        _at += width;
                    }
                    else
                    {
                        break;
                    }
                }

                return altered ? Consider(decoded.ToString()) : Consider(_text, start, _at);
            }

            // Every whole identifier in one line of a literal's contents.
            private bool Search(int from, int to, bool verbatim)
            {
                if (to <= from) return true;
                string contents = verbatim ? _text.Substring(from, to - from) : Unescape(from, to);
                int at = 0;
                while (at < contents.Length)
                {
                    if (!IsIdentifierStart(contents[at]))
                    {
                        at++;
                        continue;
                    }

                    int start = at;
                    while (at < contents.Length && IsIdentifierPart(contents[at])) at++;
                    if (!Consider(contents, start, at)) return false;
                }

                return true;
            }

            // A regular literal's contents with its escape sequences applied.
            private string Unescape(int from, int to)
            {
                var decoded = new StringBuilder(to - from);
                int at = from;
                while (at < to)
                {
                    char c = _text[at];
                    if (c != '\\' || at + 1 >= to)
                    {
                        decoded.Append(c);
                        at++;
                        continue;
                    }

                    if (UnicodeEscapeAt(at, out string value, out int width))
                    {
                        decoded.Append(value);
                        at += width;
                        continue;
                    }

                    char e = _text[at + 1];
                    if (e == 'x')
                    {
                        int digits = 0;
                        int code = 0;
                        while (digits < 4 && at + 2 + digits < to && HexValue(_text[at + 2 + digits]) >= 0)
                        {
                            code = code * 16 + HexValue(_text[at + 2 + digits]);
                            digits++;
                        }

                        decoded.Append(digits > 0 ? (char)code : 'x');
                        at += 2 + digits;
                        continue;
                    }

                    // Any other escape stands for one character that is not part
                    // of an identifier unless it is the quote or backslash itself.
                    decoded.Append(e == '\'' || e == '"' || e == '\\' ? e : ' ');
                    at += 2;
                }

                return decoded.ToString();
            }

            // \uXXXX or \UXXXXXXXX at `at`.
            private bool UnicodeEscapeAt(int at, out string value, out int width)
            {
                value = null;
                width = 0;
                if (at + 1 >= _text.Length || _text[at] != '\\') return false;
                char kind = _text[at + 1];
                int digits = kind == 'u' ? 4 : kind == 'U' ? 8 : 0;
                if (digits == 0 || at + 2 + digits > _text.Length) return false;

                long code = 0;
                for (int k = 0; k < digits; k++)
                {
                    int v = HexValue(_text[at + 2 + k]);
                    if (v < 0) return false;
                    code = code * 16 + v;
                }

                if (code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF && digits == 8)) return false;
                value = code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32((int)code);
                width = 2 + digits;
                return true;
            }

            private bool Consider(string candidate) => Consider(candidate, 0, candidate.Length);

            // False when text[start, end) is the member and the per-script cap
            // has been reached.
            private bool Consider(string text, int start, int end)
            {
                if (end - start != _member.Length
                    || string.CompareOrdinal(text, start, _member, 0, _member.Length) != 0
                    || _line == _lastReportedLine)
                {
                    return true;
                }

                if (_found == MaxSightingsPerScript) return false;

                _found++;
                _lastReportedLine = _line;
                _sightings.Add(new ScriptNameSighting(_script.ScriptPath, _line, _member, Excerpt()));
                return true;
            }

            private string Excerpt()
            {
                int end = _lineStart;
                while (end < _text.Length && NewLineWidth(end) == 0) end++;
                string excerpt = _text.Substring(_lineStart, end - _lineStart).Trim();
                return excerpt.Length > MaxExcerptLength
                    ? excerpt.Substring(0, MaxExcerptLength) + "…"
                    : excerpt;
            }

            // Whether the line starting at _at is an #if, #elif, #else or #endif.
            private bool AtConditionalDirective()
            {
                int at = SkipBlanks(_at);
                if (at >= _text.Length || _text[at] != '#') return false;
                at = SkipBlanks(at + 1);
                int start = at;
                while (at < _text.Length && char.IsLetter(_text[at])) at++;
                string word = _text.Substring(start, at - start);
                return word == "if" || word == "elif" || word == "else" || word == "endif";
            }

            private int SkipBlanks(int at)
            {
                while (at < _text.Length && (_text[at] == ' ' || _text[at] == '\t')) at++;
                return at;
            }

            private bool TryNewLine()
            {
                int width = NewLineWidth(_at);
                if (width == 0) return false;
                _at += width;
                _line++;
                _lineStart = _at;
                _lineHasCode = false;
                return true;
            }

            private void SkipToEndOfLine()
            {
                while (_at < _text.Length && NewLineWidth(_at) == 0) _at++;
            }

            // The C# line terminators: CR, LF, CR LF, NEL, LS and PS.
            private int NewLineWidth(int at)
            {
                if (at >= _text.Length) return 0;
                switch (_text[at])
                {
                    case '\r':
                        return at + 1 < _text.Length && _text[at + 1] == '\n' ? 2 : 1;
                    case '\n':
                    case '\u0085':
                    case '\u2028':
                    case '\u2029':
                        return 1;
                    default:
                        return 0;
                }
            }

            private char Peek(int ahead) => _at + ahead < _text.Length ? _text[_at + ahead] : '\0';
        }

        private static int HexValue(char c)
            => c >= '0' && c <= '9' ? c - '0'
                : c >= 'a' && c <= 'f' ? c - 'a' + 10
                : c >= 'A' && c <= 'F' ? c - 'A' + 10
                : -1;

        private static bool IsIdentifierStart(char c)
            => char.IsLetter(c) || c == '_' || char.GetUnicodeCategory(c) == UnicodeCategory.LetterNumber;

        private static bool IsIdentifierPart(char c)
        {
            if (char.IsLetterOrDigit(c) || c == '_') return true;
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.ConnectorPunctuation:
                case UnicodeCategory.Format:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsFormatting(char c) => char.GetUnicodeCategory(c) == UnicodeCategory.Format;

        private static string Count(int n, string noun)
            => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");
    }
}
