// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyPacketParser.cs
//
// Parses the server reply for LobbyJoin (0x27), LobbyList (0x29), and
// LobbyRoomListUpdate (0x2A).  All three carry the same payload — the
// canonical shape is a versioned JSON envelope of room summaries, with a
// legacy bare-array fallback retained for rolling-upgrade compatibility:
//
//   • Canonical (Room Service v2026-05-10+):
//       {"version": 1, "rooms": [ {...}, {...} ]}
//   • Legacy (pre-versioning):
//       [ {...}, {...} ]
//
// The gateway strips the outer NatsReply envelope before forwarding the raw
// payload to the client, so the client receives the inner shape directly.
//
// ── Schema-version guard ─────────────────────────────────────────────────────
//
// The `version` field encodes the wire-schema generation:
//   • 0 / absent  — legacy bare-array shape; treated as v1 semantics.
//   • 1           — current envelope, parsed as documented.
//   • > 1         — unknown future shape; the parser MUST refuse rather
//                   than read v1 fields under v2 semantics (a silent
//                   misread would surface as bogus rooms in the lobby UI
//                   that no operator dashboard could correlate to a wire
//                   format change).
//
// ── Hardening (2026-04-27) ────────────────────────────────────────────────────
// The parser is hand-rolled (no external JSON dependency) but has been
// hardened to defend against a hostile or compromised server:
//  • Top-level entry count is capped at NetworkSettings.maxLobbyRoomEntries,
//    never below the rooms the Room Service may itself return
//    (default 256, parity with RoomPacketParser).
//  • Per-string fields are length-capped at NetworkSettings.maxLobbyStringBytes.
//  • String fields support JSON escapes (\\, \", \/, \b, \f, \n, \r, \t,
//    \uXXXX) and reject embedded NUL or control chars.
//  • Maximum nesting depth is bounded so a deeply-nested payload cannot
//    exhaust the parser's call stack.

using System;
using System.Collections.Generic;
using System.Text;
using RTMPE.Core;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Parses lobby response payloads into <see cref="LobbyRoomInfo"/> lists.
    /// </summary>
    internal static class LobbyPacketParser
    {
        // Hard ceilings used when no NetworkSettings instance is available
        // (defensive fallback — e.g. in EditMode unit tests that exercise the
        // parser directly without a NetworkManager).
        private const int FallbackMaxEntries     = 256;
        private const int FallbackMaxStringBytes = 256;
        private const int MaxNestingDepth        = 32;

        /// <summary>
        /// Highest wire-schema version this consumer understands.  Payloads
        /// declaring a version above this value are dropped rather than
        /// parsed under v1 semantics — a silent misread would surface as
        /// bogus rooms in the lobby UI with no operator-visible failure
        /// signal.
        ///
        /// <para>Coordinated with the publisher constant
        /// <c>LobbyRoomListEnvelopeVersion</c> in
        /// <c>modules/room/infrastructure/messaging/nats_handler.go</c>.
        /// Bumping this constant is a two-side change and should be made
        /// in lock-step with the Go publisher.</para>
        /// </summary>
        internal const int MaxKnownEnvelopeVersion = 1;

        /// <summary>
        /// The most rooms a lobby list reply can carry, mirroring
        /// <c>ports.LobbyListMaxResults</c> in the Room Service — every lobby
        /// query there is clamped to it, whatever the client asks for.
        ///
        /// <para>It is the floor under the configured entry cap rather than a
        /// second cap of its own.  Exceeding the cap refuses the whole payload,
        /// and on a join reply that refusal reports the client out of a lobby
        /// the gateway has subscribed it to — so a cap set below what an honest
        /// server sends is not a bound on a hostile one, it is an ejection from
        /// every lobby that fills.</para>
        ///
        /// <para>Held equal to the Go constant by
        /// <c>scripts/check-reply-capacity-contract.sh</c>, which also refuses a
        /// <c>NetworkSettings</c> range or default that sits below it.</para>
        /// </summary>
        internal const int LobbyRoomListServerMaxEntries = 100;

        // Strict UTF-8 codec.  The lax decoder silently substitutes U+FFFD
        // for malformed bytes, which lets a hostile or compromised server
        // smuggle bytes that survive the parse but mutate downstream
        // string-equality (the existing _abandonedLobbyName defence in
        // LobbyManager keys on string equality, which U+FFFD substitution
        // defeats — two distinct lobby names can collapse to the same
        // fingerprint).  Symmetric with M19-PROTO-04 / M19-RPC-04/05.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Why a parse produced the list it did.
        /// </summary>
        /// <remarks>
        /// An empty list answered five different questions — the lobby is empty,
        /// the envelope is from a newer service, the bytes are not readable, the
        /// payload declared more rooms than the configured cap, the payload was
        /// absent — and a caller that clears its live list on all five turns any
        /// one of them into "this lobby has no rooms", permanently and silently.
        /// The list is still returned empty in every failing case; what is new is
        /// that the caller can tell which case it is.
        /// </remarks>
        internal enum LobbyListOutcome
        {
            /// <summary>The payload was read. The list is what it said.</summary>
            Ok = 0,

            /// <summary>
            /// Absent, not valid UTF-8, not JSON, or a shape that is neither the
            /// array nor the envelope.
            /// </summary>
            Unreadable = 1,

            /// <summary>
            /// A versioned envelope declaring a version above
            /// <see cref="MaxKnownEnvelopeVersion"/>. Dropped on purpose: reading
            /// v1 fields under later semantics is a misread, not a degradation.
            /// </summary>
            UnsupportedVersion = 2,

            /// <summary>
            /// More rooms than the configured cap. Dropped whole rather than
            /// truncated, so a partial list is never mistaken for the lobby.
            /// </summary>
            TooManyEntries = 3,

            /// <summary>
            /// The list was read, but at least one room was dropped because a
            /// string field could not be. The rooms that survived are usable and
            /// the caller should apply them; what this reports is that the lobby
            /// is being shown short, which is otherwise indistinguishable from a
            /// lobby that really has fewer rooms.
            /// </summary>
            OkWithDroppedRows = 4,

            /// <summary>
            /// The gateway marked the envelope <c>refused</c>: the Room Service
            /// declined the request — rate limit, tenancy, a database fault, a
            /// name it would not accept — and the empty list that came with it is
            /// not this lobby's contents.
            /// </summary>
            /// <remarks>
            /// A gateway that predates the marker sends none, which reads as not
            /// refused: the behaviour this had before, on a payload it could not
            /// have distinguished anyway.
            /// </remarks>
            Refused = 5,
        }

        // Raised once per process when an asset authored before the floor
        // existed still carries a cap below it. Silently raising it would be a
        // setting that does not do what it says; refusing to raise it would
        // eject the player from every lobby that fills.
        //
        // Process-wide, so the first read that hits it silences every later
        // one — including across the tests in a run, which is why the flag is
        // observable and resettable rather than private. Without that, a test of
        // the one-shot property would pass or fail on the order the runner
        // happened to pick.
        private static int _serverFloorReported;

        /// <summary>Whether the floor has already been reported this process.</summary>
        internal static bool ServerFloorReported
        {
            get => System.Threading.Volatile.Read(ref _serverFloorReported) != 0;
            set => System.Threading.Volatile.Write(ref _serverFloorReported, value ? 1 : 0);
        }

        /// <summary>
        /// The configured entry cap, never below what the Room Service is
        /// allowed to send.
        /// </summary>
        internal static int EffectiveMaxEntries(int configured)
        {
            if (configured >= LobbyRoomListServerMaxEntries) return configured;

            if (System.Threading.Interlocked.Exchange(ref _serverFloorReported, 1) == 0)
            {
#if UNITY_2017_1_OR_NEWER
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] NetworkSettings.maxLobbyRoomEntries is " + configured + ", below the " +
                    LobbyRoomListServerMaxEntries + " rooms the Room Service may return in one " +
                    "lobby reply. A reply larger than the cap is refused whole, and on a join " +
                    "reply that refusal reports this client out of the lobby — so " +
                    LobbyRoomListServerMaxEntries + " is being used instead. Raise the setting " +
                    "to silence this.");
#endif
            }
            return LobbyRoomListServerMaxEntries;
        }

        /// <summary>
        /// Parses a UTF-8 JSON array of room summaries from a lobby response
        /// payload.  Returns an empty list on parse failure (never throws).
        /// </summary>
        public static List<LobbyRoomInfo> ParseRoomList(byte[] payload)
        {
            int maxEntries     = FallbackMaxEntries;
            int maxStringBytes = FallbackMaxStringBytes;
            var settings = NetworkManager.Instance?.Settings;
            if (settings != null)
            {
                if (settings.maxLobbyRoomEntries > 0)
                    maxEntries = EffectiveMaxEntries(settings.maxLobbyRoomEntries);
                if (settings.maxLobbyStringBytes > 0)
                    maxStringBytes = settings.maxLobbyStringBytes;
            }
            return ParseRoomList(payload, maxEntries, maxStringBytes);
        }

        /// <summary>
        /// The same parse as <see cref="ParseRoomList(byte[])"/>, reporting which
        /// of the outcomes produced the list.
        /// </summary>
        internal static List<LobbyRoomInfo> ParseRoomList(
            byte[] payload, out LobbyListOutcome outcome)
        {
            return ParseRoomList(payload, out outcome, out _);
        }

        /// <summary>
        /// The settings-driven parse, additionally reporting the lobby the
        /// envelope names.
        /// </summary>
        internal static List<LobbyRoomInfo> ParseRoomList(
            byte[] payload, out LobbyListOutcome outcome, out string envelopeLobby)
        {
            int maxEntries     = FallbackMaxEntries;
            int maxStringBytes = FallbackMaxStringBytes;
            var settings = NetworkManager.Instance?.Settings;
            if (settings != null)
            {
                if (settings.maxLobbyRoomEntries > 0)
                    maxEntries = EffectiveMaxEntries(settings.maxLobbyRoomEntries);
                if (settings.maxLobbyStringBytes > 0)
                    maxStringBytes = settings.maxLobbyStringBytes;
            }
            return ParseRoomList(payload, maxEntries, maxStringBytes, out outcome,
                                 out envelopeLobby);
        }

        /// <summary>
        /// Test-friendly overload that accepts explicit caps.  Public for use
        /// by EditMode tests that exercise the parser without a live
        /// <see cref="NetworkManager"/>.
        /// </summary>
        internal static List<LobbyRoomInfo> ParseRoomList(
            byte[] payload, int maxEntries, int maxStringBytes)
        {
            return ParseRoomList(payload, maxEntries, maxStringBytes, out _);
        }

        internal static List<LobbyRoomInfo> ParseRoomList(
            byte[] payload, int maxEntries, int maxStringBytes, out LobbyListOutcome outcome)
        {
            return ParseRoomList(payload, maxEntries, maxStringBytes, out outcome, out _);
        }

        /// <summary>
        /// The same parse, additionally reporting the lobby the envelope says
        /// the list belongs to, or the empty string when it says nothing.
        /// </summary>
        /// <remarks>
        /// A room carries its own lobby name, so a populated reply has always
        /// been self-identifying. An empty one carried nothing — and a client
        /// switches lobbies by issuing a second join, against replies that are
        /// not ordered against each other, so an empty reply for the lobby it
        /// walked away from was indistinguishable from one for the lobby it is
        /// waiting on and completed the wrong join.
        /// <para>A gateway or Room Service that predates the field sends none,
        /// which reads as the empty string: the behaviour this had before, on a
        /// payload it could not have told apart anyway.</para>
        /// </remarks>
        internal static List<LobbyRoomInfo> ParseRoomList(
            byte[] payload, int maxEntries, int maxStringBytes,
            out LobbyListOutcome outcome, out string envelopeLobby)
        {
            var rooms = new List<LobbyRoomInfo>();
            outcome = LobbyListOutcome.Unreadable;
            envelopeLobby = string.Empty;
            if (payload == null || payload.Length == 0) return rooms;
            if (maxEntries     <= 0) maxEntries     = FallbackMaxEntries;
            if (maxStringBytes <= 0) maxStringBytes = FallbackMaxStringBytes;

            try
            {
                // Strict UTF-8 routes a malformed-byte payload through the
                // existing bare-catch into the empty-list return path,
                // exactly as a parser-throw would.  No new failure shape on
                // the call site contract.
                var json = StrictUtf8.GetString(payload);

                // Wire-shape dispatch:
                //   • leading '['  →  legacy bare-array shape (pre-versioning).
                //   • leading '{'  →  versioned envelope; require the declared
                //                     version is at or below this consumer's
                //                     `MaxKnownEnvelopeVersion`, then parse the
                //                     `rooms` array within.  Higher versions
                //                     drop the payload outright — silently
                //                     reading v1 fields under v2 semantics
                //                     would surface as inconsistent UI state
                //                     with no operator-visible failure signal.
                //   • anything else → unrecognised; treat as empty.
                int firstNonWs = SkipWhitespace(json, 0);
                if (firstNonWs >= json.Length) return rooms;
                char first = json[firstNonWs];
                if (first == '[')
                {
                    // Legacy bare-array shape — the payload itself is the
                    // room array, starting at this first non-whitespace char.
                    outcome = ParseJsonArray(json, firstNonWs, rooms, maxEntries, maxStringBytes);
                }
                else if (first == '{')
                {
                    // Versioned envelope.  Both the schema-version guard and
                    // the room-array lookup resolve their fields by walking
                    // only the envelope's own top-level members, so a
                    // `version` key or an array nested inside a room object
                    // is never mistaken for the envelope's own field — a
                    // non-canonical or hostile payload cannot smuggle either.
                    // An unreadable version is treated as a version this build
                    // does not know, never as its absence: the gate below is the
                    // only thing standing between a future envelope and v1
                    // semantics, and a value it cannot represent is exactly the
                    // case it exists for.
                    int declaredVersion = 0;
                    if (TryFindTopLevelMember(json, firstNonWs, "version",
                                              out int versionValueStart))
                    {
                        declaredVersion =
                            TryReadIntValueAt(json, versionValueStart, out int parsedVersion)
                                ? parsedVersion
                                : MaxKnownEnvelopeVersion + 1;
                    }
                    // A refusal wears the room list's own shape, so the marker
                    // is read before the rooms: the empty list travelling with it
                    // is not this lobby's contents, and applying it would replace
                    // what the client holds with the server's inability to answer.
                    //
                    // It is also read before the version, because the gateway
                    // writes this document itself rather than forwarding one —
                    // the marker is the same fact whatever envelope version the
                    // Room Service has reached, and reading it second would turn
                    // every refusal into an unreadable envelope the day that
                    // version moves.
                    if (TryFindTopLevelMember(json, firstNonWs, "refused",
                                              out int refusedValueStart)
                        && refusedValueStart < json.Length
                        && json[refusedValueStart] == 't')
                    {
                        outcome = LobbyListOutcome.Refused;
                        return rooms;
                    }
                    if (declaredVersion > MaxKnownEnvelopeVersion)
                    {
                        // Future-version envelope from a Room Service build
                        // ahead of this consumer — drop without raising the
                        // partial list, mirroring the GatewayEnvelope
                        // versioning policy on the Sync side.
                        outcome = LobbyListOutcome.UnsupportedVersion;
                        return rooms;
                    }
                    // The lobby the envelope claims, read from the envelope's
                    // own members. Read after the refusal and version gates and
                    // before the rooms, so a payload this build will not apply
                    // never reports a tag either.
                    envelopeLobby = ReadStringField(json, firstNonWs, "lobby_name",
                                                    maxStringBytes) ?? string.Empty;

                    // Parse the `rooms` member by key.  Selecting it by key
                    // rather than by the first '[' in the payload prevents a
                    // non-canonical envelope that places another array ahead
                    // of `rooms` from being parsed as the room list.
                    if (TryFindTopLevelMember(json, firstNonWs, "rooms",
                                              out int roomsValueStart)
                        && roomsValueStart < json.Length
                        && json[roomsValueStart] == '[')
                    {
                        outcome = ParseJsonArray(json, roomsValueStart, rooms,
                                                 maxEntries, maxStringBytes);
                    }
                }
            }
            catch
            {
                // The list is discarded along with the outcome: a payload that
                // threw part-way through leaves whatever rooms were accumulated
                // before the fault, and half a lobby presented as the whole one
                // is the misread this parser exists to avoid.
                rooms.Clear();
                outcome = LobbyListOutcome.Unreadable;
            }
            return rooms;
        }

        // Skip ASCII whitespace (space, tab, CR, LF) starting from `pos`.
        // Used by the version-shape dispatcher to tolerate compact-formatted
        // and pretty-printed payloads symmetrically.
        private static int SkipWhitespace(string json, int pos)
        {
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
                pos++;
            }
            return pos;
        }

        // ── Top-level envelope member lookup ─────────────────────────────────
        //
        // The schema-version guard and the `rooms` array selection resolve
        // their fields against the envelope's ROOT object only.  Walking just
        // the depth-1 members means a `version` key or an array nested inside
        // a room object is never read as the envelope's own field, so a
        // non-canonical or hostile payload cannot shadow either by embedding
        // it in a nested object.

        // Find the closing quote of a JSON string whose opening quote has
        // already been consumed; `start` is the first content char.  Returns
        // the index of the unescaped terminating quote, or -1 if unterminated.
        private static int IndexOfStringEnd(string json, int start)
        {
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\') { i++; continue; }   // skip the escaped char
                if (c == '"') return i;
            }
            return -1;
        }

        // Skip one JSON value beginning at json[valueStart].  Returns the
        // index immediately past the value, or -1 on a malformed/truncated
        // value.  Strings and balanced object/array spans are skipped whole;
        // a primitive (number, true, false, null) is scanned up to the next
        // structural delimiter.
        private static int SkipValue(string json, int valueStart)
        {
            if (valueStart >= json.Length) return -1;
            char c = json[valueStart];
            if (c == '"')
            {
                int end = IndexOfStringEnd(json, valueStart + 1);
                return end < 0 ? -1 : end + 1;
            }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                bool inString = false;
                for (int i = valueStart; i < json.Length; i++)
                {
                    char ch = json[i];
                    if (inString)
                    {
                        if (ch == '\\') { i++; continue; }
                        if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"') { inString = true; continue; }
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                }
                return -1;   // unbalanced object/array
            }
            // Primitive value — consume up to the next structural delimiter.
            int j = valueStart;
            while (j < json.Length)
            {
                char ch = json[j];
                if (ch == ',' || ch == '}' || ch == ']') break;
                j++;
            }
            return j;
        }

        // Locate the value of a depth-1 member of the root object whose
        // opening brace is at json[rootBraceIndex].  On success sets
        // <paramref name="valueStart"/> to the index of the value's first
        // non-whitespace char and returns true; returns false when the key
        // is absent or the envelope is malformed.
        private static bool TryFindTopLevelMember(
            string json, int rootBraceIndex, string key, out int valueStart)
        {
            valueStart = -1;
            if (rootBraceIndex < 0 || rootBraceIndex >= json.Length
                || json[rootBraceIndex] != '{')
                return false;

            int i = rootBraceIndex + 1;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',')
                {
                    i++;
                    continue;
                }
                if (c == '}') return false;     // end of root object — key absent
                if (c != '"') return false;     // malformed — a member must start with a key

                int keyStart = i + 1;
                int keyEnd   = IndexOfStringEnd(json, keyStart);
                if (keyEnd < 0) return false;

                int colon = SkipWhitespace(json, keyEnd + 1);
                if (colon >= json.Length || json[colon] != ':') return false;

                int vStart = SkipWhitespace(json, colon + 1);
                if (vStart >= json.Length) return false;

                if (keyEnd - keyStart == key.Length
                    && string.CompareOrdinal(json, keyStart, key, 0, key.Length) == 0)
                {
                    valueStart = vStart;
                    return true;
                }

                int afterValue = SkipValue(json, vStart);
                if (afterValue < 0) return false;
                i = afterValue;
            }
            return false;
        }

        // ── Minimal JSON array parser ────────────────────────────────────────
        // We avoid UnityEngine.JsonUtility (no List<T> support without wrappers)
        // and Newtonsoft (optional dependency) by hand-rolling a lightweight
        // parser sufficient for the fixed lobby-response schema.

        /// <summary>
        /// Appends the rooms in the array at <paramref name="arrayStart"/> to
        /// <paramref name="out_"/>.
        /// </summary>
        /// <returns>
        /// The outcome, distinguishing a cap breach from a structure that did
        /// not hold — an operator can act on the first and not the second. The
        /// list is emptied for both: every early return here leaves the rooms
        /// accumulated before the fault, and a prefix of a lobby handed to a
        /// caller who cannot tell it from the whole is the misread the cap
        /// exists to prevent.
        /// </returns>
        private static LobbyListOutcome ParseJsonArray(
            string json,
            int arrayStart,
            List<LobbyRoomInfo> out_,
            int maxEntries,
            int maxStringBytes)
        {
            // arrayStart is the index of the opening '[' located by the
            // caller: the bare-array dispatch passes the payload's first
            // non-whitespace char; the envelope dispatch passes the start of
            // the `rooms` member's array value.
            int pos = arrayStart;
            if (pos < 0 || pos >= json.Length || json[pos] != '[')
                return Refuse(out_, LobbyListOutcome.Unreadable);

            int depth = 0;
            int objStart = -1;
            bool dropped = false;
            for (int i = pos; i < json.Length; i++)
            {
                char c = json[i];
                // A brace or bracket inside a string literal (e.g. a room
                // name containing "}") is data, not structure — skipping the
                // whole string keeps the depth counter and the object-slice
                // boundaries anchored to real structural characters only.
                if (c == '"')
                {
                    int end = IndexOfStringEnd(json, i + 1);
                    if (end < 0)                       // unterminated string
                        return Refuse(out_, LobbyListOutcome.Unreadable);
                    i = end;
                    continue;
                }
                if (c == '{')
                {
                    if (depth == 1) objStart = i;
                    depth++;
                    if (depth > MaxNestingDepth)
                        return Refuse(out_, LobbyListOutcome.Unreadable);
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 1 && objStart >= 0)
                    {
                        // Reject the entire payload once the configured cap is
                        // exceeded — truncating silently would mask buggy /
                        // hostile servers.
                        if (out_.Count >= maxEntries)
                            return Refuse(out_, LobbyListOutcome.TooManyEntries);

                        var obj = json.Substring(objStart, i - objStart + 1);
                        var room = ParseRoomObject(obj, maxStringBytes);
                        if (room != null) out_.Add(room);
                        else dropped = true;
                        objStart = -1;
                    }
                }
                else if (c == '[')
                {
                    depth++;
                    if (depth > MaxNestingDepth)
                        return Refuse(out_, LobbyListOutcome.Unreadable);
                }
                else if (c == ']')
                {
                    // A nested array must give its depth back, or the counter
                    // stays high for the rest of the payload: the room's own
                    // closing brace then never satisfies the depth == 1 test,
                    // no room is ever collected, and the walk runs off the end.
                    // One array-valued field anywhere in the room schema would
                    // take the whole list out.
                    depth--;
                    if (depth <= 0)
                        return dropped
                            ? LobbyListOutcome.OkWithDroppedRows
                            : LobbyListOutcome.Ok;
                }
            }
            // Ran off the end without the array closing.
            return Refuse(out_, LobbyListOutcome.Unreadable);
        }

        private static LobbyListOutcome Refuse(List<LobbyRoomInfo> out_, LobbyListOutcome why)
        {
            out_.Clear();
            return why;
        }

        private static LobbyRoomInfo ParseRoomObject(string obj, int maxStringBytes)
        {
            // Reject objects whose own nesting exceeds a tight per-object bound.
            // ParseJsonArray already enforces top-level array depth; this catches
            // deeply nested custom_properties or similar server-added fields that
            // would not appear in the outer traversal.
            int nestDepth = 0;
            for (int ci = 0; ci < obj.Length; ci++)
            {
                char ch = obj[ci];
                // Braces and brackets inside string literals are data, not
                // structure, and must not move the depth counter.
                if (ch == '"')
                {
                    int end = IndexOfStringEnd(obj, ci + 1);
                    if (end < 0) return null;   // unterminated string
                    ci = end;
                    continue;
                }
                if (ch == '{' || ch == '[') nestDepth++;
                else if (ch == '}' || ch == ']') nestDepth--;
                if (nestDepth > MaxNestingDepth) return null;
            }

            // Resolve every field against the room object's own depth-1
            // members.  Selecting fields structurally — rather than by the
            // first "key" substring anywhere in the slice — prevents an
            // attacker-chosen value (notably the room name) that embeds a
            // `"room_id":"…"` fragment from shadowing the genuine field.
            int rootBrace = SkipWhitespace(obj, 0);
            if (rootBrace >= obj.Length || obj[rootBrace] != '{')
                return null;

            string roomId      = ReadStringField(obj, rootBrace, "room_id",      maxStringBytes);
            string roomCode    = ReadStringField(obj, rootBrace, "room_code",    maxStringBytes);
            string name        = ReadStringField(obj, rootBrace, "name",         maxStringBytes);
            int    playerCount = ReadIntField(obj,    rootBrace, "player_count");
            int    maxPlayers  = ReadIntField(obj,    rootBrace, "max_players");
            bool   isPublic    = ReadBoolField(obj,   rootBrace, "is_public");
            string lobbyName   = ReadStringField(obj, rootBrace, "lobby_name",   maxStringBytes);

            // Reject the row entirely if any string was malformed.  `null` from
            // ReadStringField means exactly that — a truncated escape, a raw
            // control byte, or a value past the length cap; an absent field and
            // a non-string value both come back as the empty string.  Coercing
            // the two together admits a room whose id could not be read, and an
            // empty id is a join target the caller cannot distinguish from a
            // real one.
            if (roomId == null || roomCode == null || name == null || lobbyName == null)
                return null;

            // Sanity-check numeric fields: negative counts and zero-capacity
            // rooms are protocol violations from a hostile or buggy server.
            if (playerCount < 0) playerCount = 0;
            if (maxPlayers  < 1) maxPlayers  = 1;
            if (playerCount > maxPlayers) playerCount = maxPlayers;

            return new LobbyRoomInfo(roomId, roomCode, name, playerCount, maxPlayers, isPublic, lobbyName);
        }

        // Read a string field of the room object whose opening brace is at
        // json[rootBrace].  The key is located among the object's depth-1
        // members only — a `"key":"…"` fragment buried inside another field's
        // string value cannot shadow it.  Returns null on a malformed value
        // (truncated escape, embedded control char, exceeds maxBytes); returns
        // empty string when the field is absent or is not a JSON string.
        private static string ReadStringField(string json, int rootBrace, string key, int maxBytes)
        {
            if (!TryFindTopLevelMember(json, rootBrace, key, out int valueStart))
                return string.Empty;
            if (valueStart >= json.Length || json[valueStart] != '"')
                return string.Empty;   // present, but not a string value
            return ReadStringValueAt(json, valueStart + 1, maxBytes);
        }

        // Decode a JSON string value whose content begins at json[contentStart]
        // (i.e. immediately after the opening quote), applying length caps and
        // full escape handling.  Returns null on malformed input (truncated
        // escape, embedded control char, exceeds maxBytes).
        private static string ReadStringValueAt(string json, int contentStart, int maxBytes)
        {
            var sb = new StringBuilder();
            int i = contentStart;
            while (i < json.Length)
            {
                char c = json[i];

                if (c == '"')
                {
                    // Closing quote — caller already required the value to be
                    // double-quoted via the pattern.  Backslash-escape was
                    // consumed in the c == '\\' branch below, so this is the
                    // genuine string terminator.
                    return sb.ToString();
                }

                // Reject literal NUL or any C0 control character.  JSON strings
                // must encode them as \u00XX; receiving a raw byte here is a
                // protocol violation that we refuse to silently accept.
                if (c == '\0' || c < 0x20)
                    return null;

                if (c == '\\')
                {
                    if (i + 1 >= json.Length) return null;
                    char esc = json[++i];
                    switch (esc)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 >= json.Length) return null;
                            int cp = 0;
                            for (int k = 1; k <= 4; k++)
                            {
                                int hex = HexDigit(json[i + k]);
                                if (hex < 0) return null;
                                cp = (cp << 4) | hex;
                            }
                            i += 4;
                            // Reject any \u00XX in the C0 control range.
                            if (cp == 0 || cp < 0x20) return null;
                            sb.Append((char)cp);
                            break;
                        default: return null; // unknown escape
                    }
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }

                // Guard against pathological inputs by capping output length in
                // UTF-8 byte units.  Conservative upper-bound: each char is at
                // most 4 UTF-8 bytes; bail when the length × 4 would exceed
                // maxBytes.  Saves the cost of re-encoding to validate.
                if (sb.Length * 4 > maxBytes && Encoding.UTF8.GetByteCount(sb.ToString()) > maxBytes)
                    return null;
            }
            return null; // unterminated string
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return -1;
        }

        // Read an integer JSON value beginning at json[pos] (leading spaces
        // are skipped first).  The scan length is capped so an absurdly long
        // digit run cannot drive an unbounded substring allocation.  Returns
        // 0 when no integer digits are present.
        private static int ReadIntValueAt(string json, int pos)
        {
            return TryReadIntValueAt(json, pos, out int v) ? v : 0;
        }

        // ⛔ Absent and unreadable are different answers, and returning 0 for
        // both is what let a version the parser cannot represent be read as no
        // version at all: `{"version":9999999999}` overflows an int, and
        // `int.TryParse` leaves its output 0 on failure — so the gate that
        // refuses anything past MaxKnownEnvelopeVersion saw 1 and read a future
        // envelope under v1 semantics.  The scan cap makes a long digit run
        // unreadable for the same reason and must reach the same answer.
        private static bool TryReadIntValueAt(string json, int pos, out int value)
        {
            value = 0;
            int start = pos;
            while (start < json.Length && json[start] == ' ') start++;
            int end = start;
            int maxScan = Math.Min(json.Length, start + 16);
            while (end < maxScan && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return false;
            // A digit still standing at the cap means the number was longer than
            // the scan, so what was read is a prefix of it rather than it.
            if (end == maxScan && end < json.Length && char.IsDigit(json[end])) return false;
            return int.TryParse(json.Substring(start, end - start), out value);
        }

        // Read an integer field of the room object whose opening brace is at
        // json[rootBrace].  The key is resolved among the object's depth-1
        // members, so a `"key":` fragment nested inside another field's value
        // cannot shadow it.  Returns 0 when the field is absent.
        private static int ReadIntField(string json, int rootBrace, string key)
        {
            if (!TryFindTopLevelMember(json, rootBrace, key, out int valueStart))
                return 0;
            return ReadIntValueAt(json, valueStart);
        }

        // Read a boolean field of the room object whose opening brace is at
        // json[rootBrace], resolved among the object's depth-1 members.
        // Returns false when the field is absent.  TryFindTopLevelMember
        // already advances valueStart past leading whitespace.
        private static bool ReadBoolField(string json, int rootBrace, string key)
        {
            if (!TryFindTopLevelMember(json, rootBrace, key, out int valueStart))
                return false;
            return valueStart < json.Length && json[valueStart] == 't'; // "true" starts with 't'
        }
    }
}
