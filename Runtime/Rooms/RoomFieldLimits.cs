// RTMPE SDK — Runtime/Rooms/RoomFieldLimits.cs
//
// The admission rules the Room Service applies to the fields a client names
// when it creates or joins a room.
//
// They are restated here because the SDK is the side that measures first, and
// a value it accepts is encoded, sent, and only then judged by the authority
// that actually decides.  The reply does name the field and the value — the
// gateway forwards the Room Service's message verbatim — but it arrives a round
// trip later, on OnRoomError, detached from the call that caused it: a rule the
// two sides state differently is met by a developer as a room that will not
// open, rather than at the line that passed the argument.
//
// Every constant below has a counterpart in the Room Service's own entities —
// in `domain/entities/validate.go`, except the platform ceiling, which is
// declared beside the room itself in `domain/entities/room.go`.  The guard,
// `scripts/check-room-property-contract.sh`, reads both and holds the two sides
// to each other.  Where the server counts
// runes this counts runes: the wire carries UTF-8, so a budget expressed in
// bytes admits a name the server measures as too long, and rejects one it
// would have taken.

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// The Room Service's admission rules for room create and join fields,
    /// stated on the client so a request that cannot succeed is refused where
    /// the caller can still see which argument it passed.
    /// </summary>
    /// <remarks>
    /// Each validator returns <c>null</c> when the value is acceptable and a
    /// sentence naming the broken rule otherwise, mirroring the
    /// <c>error</c>-or-<c>nil</c> shape of the Go validators it tracks.
    /// </remarks>
    public static class RoomFieldLimits
    {
        /// <summary>Maximum runes in a room's display name.</summary>
        public const int MaxRoomNameRunes = 64;

        /// <summary>Maximum runes in a player's display name.</summary>
        public const int MaxDisplayNameRunes = 32;

        /// <summary>Maximum bytes in a room identifier.</summary>
        public const int MaxRoomIdBytes = 64;

        /// <summary>Exact length, in characters, of a room join code.</summary>
        public const int RoomCodeLength = 6;

        /// <summary>
        /// The characters a room join code is drawn from. Glyph pairs that are
        /// read for one another when a code is copied off a screen — O and 0,
        /// I, L and 1 — are absent by design, so a code is never ambiguous
        /// enough to need a second attempt.
        /// </summary>
        public const string RoomCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        /// <summary>Smallest explicit player cap a room may be created with.</summary>
        public const int MaxPlayersMin = 1;

        /// <summary>Largest player cap any room may hold.</summary>
        public const int MaxPlayersLimit = 100;

        /// <summary>
        /// The value that asks the server to choose the cap. It is a distinct
        /// request rather than a low cap, which is why it sits outside
        /// <see cref="MaxPlayersMin"/> instead of below it.
        /// </summary>
        public const int MaxPlayersServerDefault = 0;

        // ── Validators ─────────────────────────────────────────────────────

        /// <summary>
        /// Judge a room's display name. An empty name is accepted: the server
        /// supplies its own default for one.
        /// </summary>
        public static string ValidateRoomName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            int runes = CountRunes(name);
            if (runes > MaxRoomNameRunes)
                return $"room name is {runes} characters; the limit is {MaxRoomNameRunes}.";

            return DescribeFirstUnsafeRune(name, "room name");
        }

        /// <summary>
        /// Judge a player's display name. An empty name is accepted: a player
        /// without one is shown a default by the game, not by the protocol.
        /// </summary>
        public static string ValidateDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            int runes = CountRunes(name);
            if (runes > MaxDisplayNameRunes)
                return $"display name is {runes} characters; the limit is {MaxDisplayNameRunes}.";

            return DescribeFirstUnsafeRune(name, "display name");
        }

        /// <summary>
        /// Judge a room identifier.
        /// </summary>
        /// <remarks>
        /// ⛔ Offered to callers; deliberately not applied by the SDK on the
        /// join path. A room id is issued by the server, and the server is the
        /// only party that can say whether one names anything — so a client
        /// that refused an id would be out-guessing the authority it is about
        /// to ask, and would start refusing every room the day the id format
        /// widened. What the SDK does instead is send the id exactly as it was
        /// given, because an identifier shortened to fit is a different
        /// identifier.
        ///
        /// Use this where a room id came from somewhere other than this SDK —
        /// typed by a player, restored from a save, carried in from another
        /// system — and a local answer is worth more than a round trip.
        /// </remarks>
        public static string ValidateRoomId(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
                return "room id must not be empty.";

            int bytes = Encoding.UTF8.GetByteCount(roomId);
            if (bytes > MaxRoomIdBytes)
                return $"room id is {bytes} bytes; the limit is {MaxRoomIdBytes}.";

            foreach (char c in roomId)
            {
                if (!IsRoomIdCharacter(c))
                    return $"room id contains '{c}'; only letters, digits, '_' and '-' are allowed.";
            }

            return null;
        }

        /// <summary>
        /// Judge a room join code that has already been through
        /// <see cref="NormaliseRoomCode"/>.
        /// </summary>
        /// <remarks>
        /// 🔑 What makes a local refusal safe here is the generator rather than
        /// the validator. On the path this SDK takes, a code is not checked
        /// against a rule and rejected — it is looked up, and a code that
        /// matches nothing is answered as a room that does not exist. But every
        /// code the platform issues is drawn from <see cref="RoomCodeAlphabet"/>
        /// at exactly <see cref="RoomCodeLength"/> characters, on all three of
        /// the paths that mint one, so a string outside that set cannot name a
        /// room that exists. Refusing it costs the caller nothing and replaces
        /// "no such room" with the character that was wrong.
        /// </remarks>
        public static string ValidateRoomCode(string roomCode)
        {
            if (string.IsNullOrEmpty(roomCode))
                return "room code must not be empty.";

            if (roomCode.Length != RoomCodeLength)
                return $"room code is {roomCode.Length} characters; a code is exactly {RoomCodeLength}.";

            foreach (char c in roomCode)
            {
                if (RoomCodeAlphabet.IndexOf(c) < 0)
                    return $"room code contains '{c}', which is not a character codes are made from.";
            }

            return null;
        }

        /// <summary>
        /// Judge a requested player cap.
        /// </summary>
        public static string ValidateMaxPlayers(int maxPlayers)
        {
            if (maxPlayers == MaxPlayersServerDefault) return null;
            if (maxPlayers >= MaxPlayersMin && maxPlayers <= MaxPlayersLimit) return null;

            return $"max players is {maxPlayers}; it must be {MaxPlayersServerDefault} " +
                   $"to let the server choose, or between {MaxPlayersMin} and {MaxPlayersLimit}.";
        }

        /// <summary>
        /// Put a room code into the case the alphabet is written in.
        /// </summary>
        /// <remarks>
        /// A player reads a code off a screen and types it back, and which case
        /// they type in is not something they were asked to preserve. The
        /// alphabet has no lowercase members, so raising the case cannot
        /// change which code was meant — and the server compares the code as
        /// written, so without this a correctly-read code is answered as a room
        /// that does not exist.
        ///
        /// The conversion is deliberately culture-independent. Under a Turkish
        /// locale the culture-sensitive form maps a dotless <c>i</c> onto a
        /// dotted capital, which is not a character any code contains, so a
        /// player's own regional settings would decide whether their code
        /// worked.
        /// </remarks>
        public static string NormaliseRoomCode(string roomCode)
            => string.IsNullOrEmpty(roomCode) ? roomCode : roomCode.ToUpperInvariant();

        // ── Helpers ────────────────────────────────────────────────────────

        private static bool IsRoomIdCharacter(char c)
            => (c >= 'a' && c <= 'z')
            || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9')
            || c == '_' || c == '-';

        /// <summary>
        /// Count the code points in <paramref name="s"/>.
        /// </summary>
        /// <remarks>
        /// The server measures runes, and a .NET string is measured in UTF-16
        /// code units — so anything outside the Basic Multilingual Plane, every
        /// emoji among them, counts twice in <c>Length</c> and once on the far
        /// side. An unpaired surrogate counts as one, which is what it becomes
        /// when the string is encoded for the wire.
        /// </remarks>
        private static int CountRunes(string s)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    i++;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Name the first codepoint the server would refuse, or return null.
        /// </summary>
        /// <remarks>
        /// Control characters and the invisible formatting codepoints are kept
        /// out of names the server will show to other players: they carry no
        /// glyph, so a name holding them can be made to read as another
        /// player's, and the bidi overrides can reverse the text around them.
        ///
        /// The ranges are written out rather than taken from the framework's
        /// character tables. This set has to be the same set the server holds,
        /// and a table that ships with the runtime is free to grow between one
        /// Unity version and the next.
        /// </remarks>
        private static string DescribeFirstUnsafeRune(string s, string field)
        {
            int at = FirstUnsafeCharacterIndex(s);
            if (at < 0) return null;

            int cp = char.IsHighSurrogate(s[at]) && at + 1 < s.Length && char.IsLowSurrogate(s[at + 1])
                ? char.ConvertToUtf32(s[at], s[at + 1])
                : s[at];

            return $"{field} contains an invisible or control character (U+{cp:X4}) at offset {at}.";
        }

        /// <summary>
        /// The index of the first codepoint the Room Service refuses inside a
        /// name, or <c>-1</c> when there is none.
        /// </summary>
        /// <remarks>
        /// Public because the same rule governs more than one field on more
        /// than one path — a room name, a player's display name, a matchmaking
        /// mode — and the server states it once. Restating it once per call
        /// site is how two of them end up disagreeing.
        /// </remarks>
        public static int FirstUnsafeCharacterIndex(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;

            for (int i = 0; i < s.Length; i++)
            {
                int cp = s[i];
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    // No codepoint above the Basic Multilingual Plane is in the
                    // refused set, but the pair is stepped over as one so an
                    // offset reported here indexes the character that offended.
                    if (IsUnsafeCodepoint(char.ConvertToUtf32(s[i], s[i + 1]))) return i;
                    i++;
                    continue;
                }

                if (IsUnsafeCodepoint(cp)) return i;
            }

            return -1;
        }

        private static bool IsUnsafeCodepoint(int cp)
        {
            if (cp <= 0x001F) return true;                    // C0 controls
            if (cp >= 0x007F && cp <= 0x009F) return true;    // delete and C1 controls
            if (cp >= 0x200B && cp <= 0x200F) return true;    // zero-width and directional marks
            if (cp >= 0x202A && cp <= 0x202E) return true;    // bidi embedding and override
            if (cp >= 0x2060 && cp <= 0x2064) return true;    // word joiner and invisible operators
            if (cp >= 0x2066 && cp <= 0x2069) return true;    // bidi isolates
            if (cp >= 0x206A && cp <= 0x206F) return true;    // deprecated formatting
            if (cp == 0xFEFF) return true;                    // byte-order mark
            return false;
        }
    }
}
