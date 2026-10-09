// RTMPE SDK — Runtime/Core/Diagnostics/LocalPathRedaction.cs
//
// Strip the developer's machine out of text that is about to leave it.
//
// S4-39.  The diagnostics uplink forwards captured log text and stack traces to
// the gateway, where they land in the system journal and in Loki.  An Editor or
// Mono stack trace carries ABSOLUTE source paths:
//
//   at Player.Update () [0x00012] in /home/alice/Projects/Game/Assets/Scripts/Player.cs:42
//   Player:Update() (at C:\Users\alice\Game\Assets\Scripts\Player.cs:42)
//
// which publishes the developer's account name and directory layout to a server
// log, once per captured error, for as long as the uplink is on.  The only thing
// standing between that and the wire was a tooltip saying "leave OFF in
// production".
//
// ⛔ What this is NOT.  It is not a secrets filter: a game that prints a token
// still prints a token, and the tooltip's warning about capturing the whole
// process's logs stands.  It removes exactly the identifiers the SDK itself puts
// on the wire without anyone choosing to — the absolute part of a path — and says
// so rather than implying more.

using System.Text;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Rewrites absolute filesystem paths in diagnostic text so what leaves the
    /// machine names a file within the project rather than a place on a disk.
    /// </summary>
    internal static class LocalPathRedaction
    {
        /// <summary>What replaces the part of a path above the project.</summary>
        internal const string Elision = "…/";

        /// <summary>
        /// Path segments a Unity project's own tree begins at.  A path containing
        /// one is cut there, so <c>Assets/Scripts/Player.cs</c> survives intact —
        /// which is the half a stack trace is read for.
        /// </summary>
        /// <remarks>
        /// Ordered longest-first so <c>PackageCache</c> is not matched as
        /// <c>Packages</c>; the scan takes the LAST marker in the path, because a
        /// project cloned into a directory itself called <c>Assets</c> would
        /// otherwise be cut at the wrong one.
        /// </remarks>
        private static readonly string[] ProjectMarkers =
            { "PackageCache", "Packages", "Assets", "Library", "ProjectSettings" };

        /// <summary>
        /// How many tokens a run admits between two it commits to.  A token
        /// commits when it is the run's first, when it reads as a segment with
        /// no closed token open before it, or when it carries the project
        /// marker that settles one; a bare word, a date, a closed token and a
        /// segment read past a closed token do not.  Past this many
        /// uncommitted tokens the scan stops and the run ends at its last
        /// commit — so a folder name may hold this many words that touch no
        /// separator, and a project marker may sit this many tokens past a
        /// closed one.  It is also what bounds the scan: the token the bound
        /// falls on is read before the scan stops, so every token is read by
        /// the scan that claims it and by at most one more than this many
        /// scans before it.
        /// </summary>
        private const int MaxTokensPastACommit = 8;

        /// <summary>
        /// <paramref name="text"/> with every absolute path rewritten.  Returns the
        /// same reference when there is nothing to rewrite, which is the common
        /// case and the one that must not allocate.
        /// </summary>
        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!MightContainAnAbsolutePath(text)) return text;

            StringBuilder sb = null;
            int copied = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (!StartsAnAbsolutePath(text, i)) continue;

                int end = EndOfPathRun(text, i);
                string replacement = Shorten(text.Substring(i, end - i));

                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, i - copied);
                sb.Append(replacement);
                copied = end;
                i = end - 1;
            }

            if (sb == null) return text;
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        /// <summary>
        /// A cheap refusal for the overwhelming majority of log lines, which carry
        /// no path at all.  A message with no separator cannot hold one.
        /// </summary>
        private static bool MightContainAnAbsolutePath(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '/' || text[i] == '\\') return true;
            return false;
        }

        /// <summary>
        /// Whether an absolute path begins at <paramref name="i"/>: a POSIX root, a
        /// UNC root, or a Windows drive letter.
        /// </summary>
        /// <remarks>
        /// A leading separator counts only where the character before it cannot be
        /// part of a path, or glues a value to its name — otherwise the middle of
        /// <c>Assets/Scripts</c> would be read as a root and a RELATIVE path, which
        /// is already safe, would be cut down to its file name for nothing.
        /// </remarks>
        private static bool StartsAnAbsolutePath(string text, int i)
        {
            char c = text[i];
            if (i > 0 && IsPathChar(text[i - 1]) && !GluesARoot(text[i - 1])) return false;

            // A root is a separator with a name after it: a lone slash between
            // two words ("3 / 5"), or two ("a // b"), is punctuation, not a place
            // on a disk; a UNC root is two separators and then a host name.
            if (c == '/' || c == '\\')
            {
                int name = i;
                while (name < text.Length && IsSeparator(text[name])) name++;
                return name < text.Length && IsPathChar(text[name]);
            }

            // X:\ or X:/ with a name after the root.
            return i + 3 < text.Length
                && char.IsLetter(c)
                && text[i + 1] == ':'
                && (text[i + 2] == '\\' || text[i + 2] == '/')
                && IsPathChar(text[i + 3]);
        }

        // A path character that a root may follow without a space between:
        // "path=/Users/…", "-projectPath=C:\…", "{/Users/…}" — a value glued to
        // its name, or an interpolation's brace.  A colon is not one: "C:\"
        // is the drive's own, and "://" a link's.
        private static bool GluesARoot(char c) => c == '=' || c == '{';

        /// <summary>
        /// One past the last character of the path starting at <paramref name="from"/>.
        /// </summary>
        /// <remarks>
        /// <para>A path may contain spaces — <c>C:\Users\John Smith\…</c>,
        /// <c>/Users/alice/Unity Projects/My Game/…</c> — so the run does not end
        /// at whitespace.  It ends at the last space-delimited token that reads
        /// as a path segment (a separator with a name beside it), and the scan
        /// for that token stops at a line break, at a link, at a token that
        /// begins an absolute path of its own — a root glued to a bracket or a
        /// quote included — and at punctuation that closes a token without a
        /// space after it.  Punctuation inside a segment — <c>O'Brien</c>,
        /// <c>New folder (2)</c>, <c>Game [WIP]</c> — is part of it, because it
        /// is followed by a path character; the same character followed by a
        /// space, a line break or the end of the text closes the token.  A word
        /// with no separator inside the run (<c>Smith</c> in <c>John Smith
        /// Jr\Game</c>) is part of the path when a later token carries one, and
        /// trailing prose after the path (<c>… Player.cs:42 in method
        /// Update</c>) is not.</para>
        ///
        /// <para>A closed token is where the run ends — <c>(at …/Player.cs:42)
        /// then docs/x.md</c> keeps the file name — unless a later token reaches
        /// a project marker, which is what tells <c>My Game (2) - Copy\Assets\…</c>
        /// from prose: the run then extends to it.</para>
        ///
        /// <para>⚠️ A stack trace's line number rides on the end of the path
        /// (<c>…/Player.cs:42</c>) and a Unity trace wraps it in parentheses
        /// (<c>(at …/Player.cs:42)</c>), so a colon never ends a token.  Keeping
        /// the line number is the point: what is removed is where the file
        /// lives, not which line threw.</para>
        ///
        /// <para>⛔ Shapes the rule cannot read.  A segment holding a comma or a
        /// semicolon followed by a space (<c>Smith, John</c>) ends the run there,
        /// and what follows it — the segment's head included — is published as
        /// it was before; so does a closed bracket or quote followed by a space
        /// when no project marker follows (<c>Game (WIP) Final\save.dat</c>),
        /// and so does a folder name with more than
        /// <see cref="MaxTokensPastACommit"/> words touching no separator, at
        /// the run's last committed token.  Prose followed within that reach by
        /// a relative path with a letter in it (<c>/tmp/a.log and then b/c</c>,
        /// <c>via TCP/IP</c>) reads as one path and is shortened, never leaked;
        /// a date or a fraction (<c>12/09/2026</c>, <c>3/5</c>) carries no
        /// letter and is not a segment.  A root glued to a colon
        /// (<c>path:/Users/…</c>) is not read as one, and a path inside a URL
        /// (<c>file:///Users/…</c>) is a link: both are left alone.</para>
        ///
        /// <para>Linear in the text.  A scan claims a region that ends at a
        /// token boundary and that no later scan enters, reads past it no
        /// further than the token the bound falls on, and stops at every token
        /// that starts an absolute path — so a token is read by the scan that
        /// claims it and by at most one more than
        /// <see cref="MaxTokensPastACommit"/> scans before it, never by one per
        /// root the line holds.</para>
        /// </remarks>
        private static int EndOfPathRun(string text, int from)
        {
            int  end      = from;
            int  closedAt = from;   // the end as it stood when a closed token was crossed
            bool crossing = false;  // past a closed token, with no marker reached since
            int  pending  = 0;      // tokens read since the last one the run committed to
            int  i        = from;

            while (i < text.Length)
            {
                int tokenStart = i;
                while (i < text.Length && ContinuesAToken(text, i)) i++;

                // A token is committed to when it is the run's own first, or a
                // segment reached with no closed token open between them; a
                // segment read past a closed token extends the run only if a
                // marker settles it, and a bare word commits to nothing.
                bool committed = false;
                if (tokenStart == from)
                {
                    end = i;
                    committed = true;
                }
                else if (StartsAnAbsolutePath(text, tokenStart) || IsUrl(text, tokenStart, i))
                {
                    break;
                }
                else if (ReadsAsASegment(text, tokenStart, i))
                {
                    end = i;
                    if (crossing && ContainsAMarkerSegment(text, tokenStart, i)) crossing = false;
                    committed = !crossing;
                }

                if (committed) pending = 0;
                else if (++pending > MaxTokensPastACommit) break;

                if (i < text.Length && ClosesAToken(text, i))
                {
                    if (!crossing) { closedAt = end; crossing = true; }
                    i++;
                }
                else if (i < text.Length && !IsSpaceInsideALine(text[i]))
                {
                    break;
                }
                while (i < text.Length && IsSpaceInsideALine(text[i])) i++;
            }

            return crossing ? closedAt : end;
        }

        private static bool IsPathChar(char c) =>
            !char.IsWhiteSpace(c) && !IsPunctuation(c);

        private static bool IsPunctuation(char c) =>
            c == '(' || c == ')'
            || c == '[' || c == ']'
            || c == '<' || c == '>'
            || c == '"' || c == '\'' || c == ',' || c == ';';

        /// <summary>
        /// Whether the character at <paramref name="i"/> belongs to the token
        /// around it.  A path character does, unless it glues a root to a name;
        /// a bracket, a quote, a comma or a semicolon does only while it sits
        /// inside a segment — followed by a path character and, unless it opens
        /// a group, preceded by one.  A root right after an opening bracket, a
        /// quote, a comma, a semicolon or a glue character begins a path of its
        /// own (<c>(/Users/…</c>, <c>path=/Users/…</c>); after a closing bracket
        /// it is the segment's own separator (<c>(2)\Assets</c>).
        /// </summary>
        private static bool ContinuesAToken(string text, int i)
        {
            char c = text[i];
            if (IsPathChar(c)) return !(GluesARoot(c) && i + 1 < text.Length && StartsAnAbsolutePath(text, i + 1));
            if (char.IsWhiteSpace(c)) return false;

            bool followed = i + 1 < text.Length && IsPathChar(text[i + 1]);
            bool preceded = i > 0 && IsPathChar(text[i - 1]);
            bool closes   = c == ')' || c == ']';
            return followed
                && (preceded || c == '(' || c == '[')
                && (closes || !StartsAnAbsolutePath(text, i + 1));
        }

        /// <summary>
        /// Whether the punctuation at <paramref name="i"/> closes the token before
        /// it: followed by a space, a line break, or the end of the text.
        /// </summary>
        private static bool ClosesAToken(string text, int i) =>
            IsPunctuation(text[i])
            && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]));

        // A path never spans a line: the whitespace a run may cross is any space
        // that is not a line break — a full-width space in a Japanese account
        // name, a no-break space pasted into a folder name — never a line break.
        private static bool IsSpaceInsideALine(char c) =>
            char.IsWhiteSpace(c)
            && c != '\n' && c != '\r' && c != '\u0085' && c != '\u2028' && c != '\u2029';

        /// <summary>
        /// Whether the token at [<paramref name="from"/>, <paramref name="to"/>)
        /// reads as a path segment: a separator with a name beside it, where a
        /// name has a letter in it.  A token that is only separators is
        /// punctuation, as a lone one is at a path's start; a date or a fraction
        /// has separators and no letter, and is prose.
        /// </summary>
        private static bool ReadsAsASegment(string text, int from, int to)
        {
            bool separator = false, letter = false;
            for (int i = from; i < to; i++)
            {
                if (IsSeparator(text[i])) separator = true;
                else if (char.IsLetter(text[i])) letter = true;
            }
            return separator && letter;
        }

        /// <summary>
        /// Whether the token at [<paramref name="from"/>, <paramref name="to"/>)
        /// holds a project marker as a whole segment.
        /// </summary>
        private static bool ContainsAMarkerSegment(string text, int from, int to)
        {
            foreach (string marker in ProjectMarkers)
            {
                int at = text.IndexOf(marker, from, to - from, System.StringComparison.Ordinal);
                while (at >= 0)
                {
                    bool startsSegment = at == from || IsSeparator(text[at - 1]);
                    int after = at + marker.Length;
                    bool endsSegment = after == to || IsSeparator(text[after]);
                    if (startsSegment && endsSegment) return true;
                    at = text.IndexOf(marker, at + 1, to - at - 1, System.StringComparison.Ordinal);
                }
            }
            return false;
        }

        // A scheme is not a directory: "https://…" after a path is a link in
        // the message, and nothing after a link belongs to the path before it.
        private static bool IsUrl(string text, int from, int to) =>
            text.IndexOf("://", from, to - from, System.StringComparison.Ordinal) >= 0;

        /// <summary>
        /// The publishable form of one absolute path: from its last project marker
        /// on, or its file name alone.
        /// </summary>
        private static string Shorten(string path)
        {
            int marker = LastMarker(path);
            if (marker >= 0) return Elision + path.Substring(marker);

            // No project marker: keep the file name alone. A path this rule cannot
            // place inside the project is one it cannot publish any part of the
            // shape of.
            int lastSeparator = LastSeparator(path);
            if (lastSeparator < 0 || lastSeparator == path.Length - 1) return Elision;
            return Elision + path.Substring(lastSeparator + 1);
        }

        /// <summary>
        /// The index at which the last project marker SEGMENT starts, or -1.
        /// </summary>
        /// <remarks>
        /// Segment-aligned on both sides, so <c>MyAssets</c> and <c>Assetsdir</c>
        /// are not markers — a substring match would cut a path in the middle of a
        /// directory name and publish a fragment of it.
        /// </remarks>
        private static int LastMarker(string path)
        {
            int best = -1;

            foreach (string marker in ProjectMarkers)
            {
                int from = path.Length;
                while (from > 0)
                {
                    int at = path.LastIndexOf(marker, from - 1, System.StringComparison.Ordinal);
                    if (at < 0) break;
                    from = at;

                    bool startsSegment = at == 0 || IsSeparator(path[at - 1]);
                    int after = at + marker.Length;
                    bool endsSegment = after == path.Length || IsSeparator(path[after]);

                    if (startsSegment && endsSegment && at > best) best = at;
                    if (at == 0) break;
                }
            }

            return best;
        }

        private static int LastSeparator(string path)
        {
            for (int i = path.Length - 1; i >= 0; i--)
                if (IsSeparator(path[i])) return i;
            return -1;
        }

        private static bool IsSeparator(char c) => c == '/' || c == '\\';
    }
}
