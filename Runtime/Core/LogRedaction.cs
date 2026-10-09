// RTMPE SDK — Runtime/Core/LogRedaction.cs
//
// Helpers that redact sensitive values before they reach a log line.
// Debug logs are frequently captured by analytics, third-party crash
// reporters, or pasted into bug reports — a player_id, invite token, or
// session identifier leaked through a debug print can survive long
// after the session itself.
//
// Two redaction families:
//
//  PII (display names / player ids / invite tokens):
//    • DisplayName : first character + "***".  Preserves a single-letter
//                    fingerprint useful for visual debugging.
//    • PlayerId    : first 4 characters + "***".  UUID-shaped ids retain
//                    enough prefix to correlate logs without exposing the
//                    full id.
//    • RoomCode    : "***".  Six-character invite tokens have insufficient
//                    entropy to allow any prefix leak.
//
//  Identifiers (crypto / session / arbitrary scalars):
//    • Redact(uint)   : 4 leading hex chars + "***" of the 8-char rendering.
//    • Redact(ulong)  : 4 leading hex chars + "***" of the 16-char rendering.
//    • Redact(string) : first 4 chars + "***" with explicit "<null>" /
//                       "<empty>" sentinels so absences themselves remain
//                       loggable.
//
// All redacted forms are deterministic — the same id always renders to the
// same prefix, which is what support workflows want when correlating a
// player-side log with a server-side trace.

using System.Globalization;

namespace RTMPE.Core
{
    /// <summary>
    /// Redacts personal data and identifiers before they are written to a log.
    /// </summary>
    /// <remarks>
    /// Each method gives the same output for the same value, so log lines about
    /// one player or session can still be matched with each other.
    /// </remarks>
    public static class LogRedaction
    {
        /// <summary>
        /// Returns the first character of a display name followed by <c>***</c>,
        /// or an empty string for a <see langword="null"/> or empty name.
        /// </summary>
        /// <remarks>
        /// A control character in the first position is shown as <c>?</c>.
        /// </remarks>
        /// <param name="name">The display name.</param>
        public static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            char first = name[0];
            // Replace control characters with a visible glyph before logging:
            // C0 (incl. the embedded NUL, which would truncate the line at any
            // downstream sink using C-style parsing), DEL, the C1 block, and the
            // Unicode line/paragraph separators (0x2028/0x2029) that a
            // Unicode-aware sink could treat as a line break to spoof an entry.
            if (first < 0x20 || first == 0x7F
                || (first >= 0x80 && first <= 0x9F)
                || first == 0x2028 || first == 0x2029) first = '?';
            // string.Concat(char, string) yields the same shape as the
            // char's ToString concatenated to "***" — using it explicitly
            // sidesteps a `char + string` overload-resolution surprise on
            // older C# language levels where the left-hand char widens to
            // int first and the resulting "0x3F***" form would silently
            // ship through to the log.
            return string.Concat(first.ToString(), "***");
        }

        /// <summary>
        /// Returns the first four characters of a player id followed by
        /// <c>***</c>.
        /// </summary>
        /// <remarks>
        /// An id shorter than four characters gives <c>***</c>, and a
        /// <see langword="null"/> or empty id an empty string.
        /// </remarks>
        /// <param name="id">The player id.</param>
        public static string PlayerId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            if (id.Length < 4) return "***";
            return id.Substring(0, 4) + "***";
        }

        /// <summary>
        /// Returns <c>***</c> for a room code, keeping none of it, or an empty
        /// string for a <see langword="null"/> or empty code.
        /// </summary>
        /// <param name="code">The room code.</param>
        public static string RoomCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            return "***";
        }

        /// <summary>
        /// Returns the first four of the eight hexadecimal digits of
        /// <paramref name="id"/>, followed by <c>***</c>.
        /// </summary>
        /// <param name="id">The identifier.</param>
        public static string Redact(uint id)
        {
            return id.ToString("x8", CultureInfo.InvariantCulture).Substring(0, 4) + "***";
        }

        /// <summary>
        /// Returns the first four of the sixteen hexadecimal digits of
        /// <paramref name="id"/>, followed by <c>***</c>. Used for session ids.
        /// </summary>
        /// <param name="id">The identifier.</param>
        public static string Redact(ulong id)
        {
            return id.ToString("x16", CultureInfo.InvariantCulture).Substring(0, 4) + "***";
        }

        /// <summary>
        /// Returns the first four characters of <paramref name="value"/> followed
        /// by <c>***</c>. A value of four characters or fewer is shown whole.
        /// </summary>
        /// <remarks>
        /// Returns <c>&lt;null&gt;</c> or <c>&lt;empty&gt;</c> for a
        /// <see langword="null"/> or empty value, and <c>&lt;ctrl&gt;***</c> when
        /// one of the first four characters is a control character.
        /// </remarks>
        /// <param name="value">The identifier.</param>
        public static string Redact(string value)
        {
            if (value == null) return "<null>";
            if (value.Length == 0) return "<empty>";
            // Surface inputs that contain control characters as a dedicated
            // sentinel rather than emitting the raw prefix.  Covers C0 (incl.
            // embedded NUL, which would truncate the log line at any C-style
            // sink and collapse two distinct ids into one fingerprint), DEL,
            // the C1 block, and the Unicode line/paragraph separators.
            int prefixLen = value.Length <= 4 ? value.Length : 4;
            for (int i = 0; i < prefixLen; i++)
            {
                char c = value[i];
                if (c < 0x20 || c == 0x7F
                    || (c >= 0x80 && c <= 0x9F)
                    || c == 0x2028 || c == 0x2029) return "<ctrl>***";
            }
            if (value.Length <= 4) return value + "***";
            return value.Substring(0, 4) + "***";
        }
    }
}
