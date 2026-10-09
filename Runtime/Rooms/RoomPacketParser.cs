// RTMPE SDK — Runtime/Rooms/RoomPacketParser.cs
//
// Parses inbound room-related packet payloads (0x20–0x23).
// These methods operate on the raw payload AFTER the 13-byte header has been
// stripped by PacketParser.ExtractPayload().
//
// Wire formats (all little-endian):
//
// ── RoomCreate Response (0x20) Server → Client ─────────────────────────────
//  [ok:1]
//  if ok=1: [room_id_len:2 LE][room_id:N][room_code_len:2 LE][room_code:N][max_players:1]
//           [local_player_id_len:2 LE][local_player_id:N]   ← appended (v3.1+)
//  if ok=0: [error_len:2 LE][error:N]
//
// ── RoomJoin Response (0x21, msg_kind=0x00) Server → Client ────────────────
//  [msg_kind:1=0x00][ok:1]
//  if ok=1: [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
//           [name_len:2][name:N][player_count:1][max_players:1][is_public:1]
//           for each player:
//             [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
//             [is_host:1][is_ready:1]
//           [local_player_id_len:2][local_player_id:N]       ← appended (v3.1+)
//           [properties_flags:1][properties_len:2][properties:N] ← appended (v4.1+)
//  if ok=0: [error_len:2][error:N]
//
// ── PlayerJoined Notification (0x21, msg_kind=0x01) Server → Client ────────
//  [msg_kind:1=0x01]
//  [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
//  [is_host:1][is_ready:1]
//
// ── RoomLeave Response (0x22, msg_kind=0x00) Server → Client ───────────────
//  [msg_kind:1=0x00][ok:1]
//
// ── PlayerLeft Notification (0x22, msg_kind=0x01) Server → Client ──────────
//  [msg_kind:1=0x01][player_id_len:2][player_id:N]
//
// ── RoomList Response (0x23) Server → Client ───────────────────────────────
//  [room_count:2 LE]
//  for each room:
//    [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
//    [name_len:2][name:N][state_len:2][state:N]
//    [player_count:1][max_players:1][is_public:1]

using System;
using System.Collections.Generic;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Message kind discriminator — first byte of RoomJoin (0x21) and RoomLeave (0x22) payloads.
    /// </summary>
    internal static class RoomMsgKind
    {
        internal const byte Response     = 0x00;
        internal const byte Notification = 0x01;
    }

    /// <summary>
    /// What the trailing room-properties block of a <c>RoomJoin</c> response
    /// turned out to be.  The three failure kinds are kept apart because they
    /// call for different reactions: one is a server that does not send the
    /// block, one is a room whose map did not fit, and one is a block that
    /// arrived damaged.
    /// </summary>
    internal enum JoinPropertiesStatus
    {
        /// <summary>
        /// No block was present.  Either the server predates it, or it could
        /// not read the room.  The snapshot's properties are empty and its
        /// version is 0 — which is what this SDK assumed before the block
        /// existed, so nothing regresses and nothing is claimed.
        /// </summary>
        Absent = 0,

        /// <summary>The block carried the room's whole property map.</summary>
        Complete,

        /// <summary>
        /// The block carried the version but not the map: the server could not
        /// send the whole snapshot.  Property writes work — that is what the
        /// version buys — but reads are missing keys until a writer sends one.
        /// </summary>
        Incomplete,

        /// <summary>
        /// A block was present and could not be read.  Treated exactly as
        /// <see cref="Absent"/> for state — no version and no properties are
        /// adopted from bytes that did not parse — and reported separately,
        /// because absence is normal and damage is not.
        /// </summary>
        Malformed,
    }

    /// <summary>
    /// Reads the room replies and notices the server sends. Used by <see cref="RoomManager"/>;
    /// not intended to be called from game code.
    /// </summary>
    /// <remarks>
    /// Every method returns <see langword="false"/> for a malformed message instead of
    /// throwing.
    /// </remarks>
    public static class RoomPacketParser
    {
        // ── CreateRoom Response (0x20) ─────────────────────────────────────────

        /// <summary>
        /// Reads the reply to a room-creation request.
        /// </summary>
        /// <returns><see langword="true"/> when the reply could be read.</returns>
        public static bool ParseCreateRoomResponse(
            byte[] payload,
            out bool      ok,
            out string    roomId,
            out string    roomCode,
            out int       maxPlayers,
            out string    error)
        {
            return ParseCreateRoomResponse(
                payload, out ok, out roomId, out roomCode,
                out maxPlayers, out _, out error);
        }

        /// <summary>
        /// Parse a <c>RoomCreate</c> (0x20) response payload, also extracting the
        /// local player's room UUID appended by the server (v3.1+ protocol).
        /// <paramref name="localPlayerId"/> is empty string when the server is pre-v3.1
        /// and did not include the field.
        /// </summary>
        internal static bool ParseCreateRoomResponse(
            byte[] payload,
            out bool      ok,
            out string    roomId,
            out string    roomCode,
            out int       maxPlayers,
            out string    localPlayerId,
            out string    error)
        {
            return ParseCreateRoomResponse(
                payload, out ok, out roomId, out roomCode, out maxPlayers,
                out localPlayerId, out _, out error);
        }

        /// <summary>
        /// Parse a <c>RoomCreate</c> (0x20) response payload, extracting the
        /// local player UUID (v3.1+) and the echoed correlation id (v4.0+).
        /// Both fields are optional — old gateways that omit them leave
        /// <paramref name="localPlayerId"/> empty and
        /// <paramref name="echoedRequestId"/> null.
        /// </summary>
        internal static bool ParseCreateRoomResponse(
            byte[] payload,
            out bool      ok,
            out string    roomId,
            out string    roomCode,
            out int       maxPlayers,
            out string    localPlayerId,
            out Guid?     echoedRequestId,
            out string    error)
        {
            ok              = false;
            roomId          = null;
            roomCode        = null;
            maxPlayers      = 0;
            localPlayerId   = string.Empty;
            echoedRequestId = null;
            error           = null;

            if (payload == null || payload.Length < 1) return false;

            int offset = 0;
            ok = payload[offset++] != 0;

            if (ok)
            {
                // [room_id_len:2][room_id:N][room_code_len:2][room_code:N][max_players:1]
                if (!TryReadString(payload, ref offset, out roomId))   return false;
                if (!TryReadString(payload, ref offset, out roomCode)) return false;
                if (offset >= payload.Length)                          return false;
                maxPlayers = payload[offset++];

                // [local_player_id_len:2][local_player_id:N]  — v3.1+ optional field
                if (offset < payload.Length)
                    TryReadString(payload, ref offset, out localPlayerId);

                // [echoed_request_id_len:2][echoed_request_id:32 hex] — optional
                ReadEchoedRequestId(payload, ref offset, out echoedRequestId);

                return true;
            }
            else
            {
                // [error_len:2][error:N]
                if (!TryReadString(payload, ref offset, out error)) return false;

                // [echoed_request_id_len:2][echoed_request_id:32 hex] — optional,
                // read on exactly the terms the success branch reads it.
                //
                // 🔑 A failure that names its request is what keeps this client
                // on ONE matching rule. Read only on success, a failure falls
                // back to arrival order, and the two rules disagree the moment
                // replies come back out of order: the failure consumes the
                // OLDEST pending request rather than its own, and the success
                // that follows for that older request then matches nothing.
                ReadEchoedRequestId(payload, ref offset, out echoedRequestId);
                return true;
            }
        }

        /// <summary>
        /// Read the optional trailing correlation id a gateway returns on a
        /// <c>RoomCreate</c> (0x20) reply, leaving <paramref name="offset"/>
        /// unmoved when the remaining bytes are not one.
        /// </summary>
        /// <remarks>
        /// Exactly 32 hex characters (a GUID in "N" format) is the expected
        /// form. Anything else — a shorter field, a non-hex one, no field at all
        /// — leaves <paramref name="echoedRequestId"/> null and the offset where
        /// it was, so trailing bytes never make the surrounding parse fail.
        /// </remarks>
        private static void ReadEchoedRequestId(
            byte[] payload, ref int offset, out Guid? echoedRequestId)
        {
            echoedRequestId = null;
            if (offset >= payload.Length) return;

            int savedOffset = offset;
            if (TryReadString(payload, ref offset, out string reqIdStr)
                && reqIdStr != null && reqIdStr.Length == 32
                && Guid.TryParseExact(reqIdStr, "N", out Guid parsed))
            {
                echoedRequestId = parsed;
            }
            else
            {
                offset = savedOffset;
            }
        }

        // ── RoomJoin (0x21) — Response or Notification ─────────────────────────

        /// <summary>
        /// Reads the kind of a join message: the reply to this client's join, or a notice that
        /// another player joined.
        /// </summary>
        /// <returns><see langword="true"/> when the message is one of the two kinds.</returns>
        public static bool TryGetJoinMsgKind(byte[] payload, out byte msgKind)
        {
            msgKind = 0;
            if (payload == null || payload.Length < 1) return false;
            msgKind = payload[0];
            return msgKind == RoomMsgKind.Response || msgKind == RoomMsgKind.Notification;
        }

        /// <summary>
        /// Reads the reply to this client's join request.
        /// </summary>
        /// <returns><see langword="true"/> when the reply could be read.</returns>
        public static bool ParseJoinRoomResponse(
            byte[] payload,
            out bool       ok,
            out RoomInfo   room,
            out string     error)
        {
            return ParseJoinRoomResponse(
                payload, out ok, out room, out _, out error);
        }

        /// <summary>
        /// Parse a <c>RoomJoin</c> (0x21) <b>response</b> payload, also extracting the
        /// local player's room UUID appended by the server (v3.1+ protocol).
        /// <paramref name="localPlayerId"/> is empty string when the server is pre-v3.1.
        /// </summary>
        internal static bool ParseJoinRoomResponse(
            byte[] payload,
            out bool       ok,
            out RoomInfo   room,
            out string     localPlayerId,
            out string     error)
        {
            return ParseJoinRoomResponse(
                payload, out ok, out room, out localPlayerId, out _, out error);
        }

        /// <summary>
        /// Parse a <c>RoomJoin</c> (0x21) <b>response</b> payload, also reporting what
        /// the trailing room-properties block (v4.1+) turned out to be.
        /// </summary>
        /// <remarks>
        /// The room's property map and the version it is at travel with the join
        /// reply because a joiner cannot derive either.  Property broadcasts are
        /// DELTAS, so nothing back-fills the keys set before this client arrived;
        /// and the server accepts a write only at exactly its own version + 1,
        /// answering a conflict with no packet at all — so a client that starts
        /// at version 0 in a room already at version n has every property write
        /// it ever makes refused, in silence, for the life of the room.
        ///
        /// The block is optional in both directions.  A server that does not send
        /// it leaves this parser exactly where it was before the block existed,
        /// and a server that sends it appends it after the last field an older
        /// SDK reads — which is why the SDK ignores what it does not recognise
        /// rather than refusing the response.
        /// </remarks>
        internal static bool ParseJoinRoomResponse(
            byte[] payload,
            out bool       ok,
            out RoomInfo   room,
            out string     localPlayerId,
            out JoinPropertiesStatus propertiesStatus,
            out string     error)
            => ParseJoinRoomResponse(
                payload, out ok, out room, out localPlayerId, out propertiesStatus, out _, out error);

        /// <summary>
        /// The same, and whether the players' maps the reply carried are whole
        /// (<see cref="WithPlayerSnapshots"/>); true when it carried none.
        /// </summary>
        internal static bool ParseJoinRoomResponse(
            byte[] payload,
            out bool       ok,
            out RoomInfo   room,
            out string     localPlayerId,
            out JoinPropertiesStatus propertiesStatus,
            out bool       playersWhole,
            out string     error)
        {
            ok               = false;
            room             = null;
            localPlayerId    = string.Empty;
            propertiesStatus = JoinPropertiesStatus.Absent;
            playersWhole     = true;
            error            = null;

            if (payload == null || payload.Length < 2) return false;

            int offset = 0;
            byte msgKind = payload[offset++];
            if (msgKind != RoomMsgKind.Response) return false;

            ok = payload[offset++] != 0;

            if (ok)
            {
                if (!TryReadString(payload, ref offset, out string roomId))   return false;
                if (!TryReadString(payload, ref offset, out string roomCode)) return false;
                if (!TryReadString(payload, ref offset, out string name))     return false;
                if (offset > payload.Length - 3)                              return false;

                int playerCount = payload[offset++];
                int maxPlayers  = payload[offset++];
                bool isPublic   = payload[offset++] != 0;

                // Reject impossibly-sized rosters before allocating the
                // PlayerInfo[].  Every player record needs at least
                // MinPlayerInfoBytes bytes; if the declared count would
                // require more bytes than the remaining payload, the packet
                // is malformed and a malicious server is attempting an
                // alloc-amplification attack against the heap.
                int remaining = payload.Length - offset;
                if ((long)playerCount * MinPlayerInfoBytes > remaining) return false;

                // Read player roster
                var players = new PlayerInfo[playerCount];
                for (int i = 0; i < playerCount; i++)
                {
                    if (!TryReadPlayerInfo(payload, ref offset, out players[i]))
                        return false;
                }

                // [local_player_id_len:2][local_player_id:N]  — v3.1+ optional field
                //
                // Its outcome is now load-bearing: TryReadString advances past
                // the length prefix BEFORE it can fail, so a refused field
                // leaves the cursor mid-record.  Reading the properties block
                // from there would judge whatever followed and report a damaged
                // block, describing a length-cap misconfiguration as wire
                // damage.  Both optional fields are simply absent instead.
                bool cursorIsTrustworthy = true;
                if (offset < payload.Length)
                    cursorIsTrustworthy = TryReadString(payload, ref offset, out localPlayerId);

                // [properties_flags:1][properties_len:2][properties:N] — v4.1+ optional
                Dictionary<string, PropertyValue> properties = null;
                int propertiesVersion = 0;
                if (cursorIsTrustworthy && offset < payload.Length)
                {
                    propertiesStatus = TryReadRoomProperties(
                        payload, ref offset, out properties, out propertiesVersion, out var document);
                    if (propertiesStatus == JoinPropertiesStatus.Malformed)
                    {
                        // Adopt nothing from bytes that did not parse.  A version
                        // taken from a damaged block is worse than no version: it
                        // would be believed, and every write made against it
                        // refused with no reply.
                        properties        = null;
                        propertiesVersion = 0;
                    }
                    else
                    {
                        playersWhole = WithPlayerSnapshots(players, document);
                    }
                }

                room = new RoomInfo(
                    roomId, roomCode, name, "waiting", playerCount, maxPlayers, isPublic,
                    players, properties, propertiesVersion);

                return true;
            }
            else
            {
                return TryReadString(payload, ref offset, out error);
            }
        }

        /// <summary>
        /// Reads the notice that another player joined the room.
        /// </summary>
        /// <returns><see langword="true"/> when the notice could be read.</returns>
        public static bool ParsePlayerJoinedNotification(
            byte[] payload,
            out PlayerInfo player)
        {
            player = null;
            if (payload == null || payload.Length < 1) return false;

            int offset = 0;
            if (payload[offset++] != RoomMsgKind.Notification) return false;

            return TryReadPlayerInfo(payload, ref offset, out player);
        }

        // ── RoomLeave (0x22) — Response or Notification ────────────────────────

        /// <summary>
        /// Reads the kind of a leave message: the reply to this client's leave, or a notice that
        /// another player left.
        /// </summary>
        /// <returns><see langword="true"/> when the message is one of the two kinds.</returns>
        public static bool TryGetLeaveMsgKind(byte[] payload, out byte msgKind)
        {
            msgKind = 0;
            if (payload == null || payload.Length < 1) return false;
            msgKind = payload[0];
            return msgKind == RoomMsgKind.Response || msgKind == RoomMsgKind.Notification;
        }

        /// <summary>
        /// Reads the reply to this client's leave request.
        /// </summary>
        /// <returns><see langword="true"/> when the reply could be read.</returns>
        public static bool ParseLeaveRoomResponse(byte[] payload, out bool ok)
            => ParseLeaveRoomResponse(payload, out ok, out _);

        /// <summary>
        /// Parse a <c>RoomLeave</c> (0x22) response, also reading the optional
        /// trailing room id the server returns — the room the request named.
        /// </summary>
        /// <remarks>
        /// 🔑 A bare ok byte cannot say which leave it answers. A client that has
        /// left one room and asked to leave another holds two requests whose
        /// replies are byte-identical, and applying the first to the second
        /// tears down a room it never asked to leave. <paramref name="roomId"/>
        /// is empty when the server returned none — a server that predates the
        /// field, or a session that held no seat to name — and the caller is
        /// then back on the one rule it always had.
        /// </remarks>
        internal static bool ParseLeaveRoomResponse(
            byte[] payload, out bool ok, out string roomId)
        {
            ok     = false;
            roomId = string.Empty;
            if (payload == null || payload.Length < 2) return false;

            int offset = 0;
            if (payload[offset++] != RoomMsgKind.Response) return false;
            ok = payload[offset++] != 0;

            // Optional, and read on the same terms as every other trailing
            // field here: anything that is not a well-formed one leaves the
            // value empty rather than failing the parse.
            if (offset < payload.Length
                && TryReadString(payload, ref offset, out string named)
                && !string.IsNullOrEmpty(named))
            {
                roomId = named;
            }
            return true;
        }

        /// <summary>
        /// Reads the notice that another player left the room.
        /// </summary>
        /// <returns><see langword="true"/> when the notice could be read.</returns>
        public static bool ParsePlayerLeftNotification(byte[] payload, out string playerId)
        {
            playerId = null;
            if (payload == null || payload.Length < 1) return false;

            int offset = 0;
            if (payload[offset++] != RoomMsgKind.Notification) return false;

            return TryReadString(payload, ref offset, out playerId);
        }

        // ── RoomList Response (0x23) ───────────────────────────────────────────

        /// <summary>
        /// What a room list says about itself beyond its rooms.
        /// <see cref="RoomManager.LastRoomListProblem"/> holds the outcome of the last list.
        /// </summary>
        public enum RoomListOutcome
        {
            /// <summary>The list is the project's rooms, complete.</summary>
            Ok = 0,

            /// <summary>
            /// The server declined the request or could not answer it. The list is empty because
            /// nothing was sent, not because the project has no rooms.
            /// </summary>
            Refused = 1,

            /// <summary>
            /// The list is real but short: the server left some rooms out. The rooms that
            /// arrived are delivered.
            /// </summary>
            RoomsOmitted = 2,

            /// <summary>
            /// The reply carries a status this SDK version does not know. The problem is
            /// reported and the list is not delivered.
            /// </summary>
            UnknownStatus = 3,

            /// <summary>
            /// The reply could not be read as a room list.
            /// </summary>
            Unreadable = 4,
        }

        // ⚠️ 0, 1 and 2 are the WIRE's values and may not be renumbered.
        // UnknownStatus and Unreadable are this parser's own determinations and
        // never appear on the wire — they exist so a caller polling the last
        // outcome has one vocabulary rather than two.

        /// <summary>
        /// Reads a room list.
        /// </summary>
        /// <remarks>
        /// This overload cannot tell a refused request from a project with no rooms; use
        /// <see cref="ParseRoomListResponse(byte[], out RoomInfo[], out RoomListOutcome)"/>.
        /// </remarks>
        /// <returns><see langword="true"/> when the reply could be read.</returns>
        public static bool ParseRoomListResponse(byte[] payload, out RoomInfo[] rooms)
        {
            return ParseRoomListResponse(payload, out rooms, out _);
        }

        /// <summary>
        /// Reads a room list and what the reply says about it.
        /// </summary>
        /// <returns>
        /// Whether the reply could be read. A readable reply can still have an
        /// <paramref name="outcome"/> other than <see cref="RoomListOutcome.Ok"/>, so check both.
        /// </returns>
        public static bool ParseRoomListResponse(
            byte[] payload, out RoomInfo[] rooms, out RoomListOutcome outcome)
        {
            rooms = null;
            // Unreadable until the payload proves otherwise: an `out` left at
            // its optimistic first value by an early return is the shape that
            // reports a fault as a success.
            outcome = RoomListOutcome.Unreadable;
            if (payload == null || payload.Length < 2) return false;

            int offset = 0;
            int roomCount = ReadU16LE(payload, ref offset);

            // Cap room count to prevent oversized allocation from
            // malicious/buggy server claiming 65535 rooms. A valid RTMPE server
            // supports at most 256 rooms per project; this also matches the
            // upstream 1 MiB payload cap (~50 bytes per room summary minimum).
            const int MaxRoomCount = 256;
            if (roomCount > MaxRoomCount) return false;

            // Each room summary needs at minimum 4 length-prefixes (8 bytes)
            // for the four string fields plus 3 trailing bytes
            // (player_count, max_players, is_public).  Reject when the
            // declared count cannot possibly fit in the remaining payload
            // — the per-iteration TryReadString check catches this too,
            // but a pre-allocation guard avoids the worst-case
            // `RoomInfo[256]` heap allocation on a single byte of
            // attacker-controlled count.
            const int MinRoomSummaryBytes = 4 * 2 + 3;
            int remainingForRooms = payload.Length - offset;
            if ((long)roomCount * MinRoomSummaryBytes > remainingForRooms) return false;

            rooms = new RoomInfo[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                // Every arm below leaves the outcome at Unreadable, which it
                // still is: a caller that read the outcome without the return
                // value would otherwise be told a list truncated part-way
                // through an entry is Ok.
                if (!TryReadString(payload, ref offset, out string roomId))   return false;
                if (!TryReadString(payload, ref offset, out string roomCode)) return false;
                if (!TryReadString(payload, ref offset, out string name))     return false;
                if (!TryReadString(payload, ref offset, out string state))    return false;
                if (offset > payload.Length - 3)                              return false;

                int playerCount = payload[offset++];
                int maxPlayers  = payload[offset++];
                bool isPublic   = payload[offset++] != 0;

                rooms[i] = new RoomInfo(roomId, roomCode, name, state, playerCount, maxPlayers, isPublic);
            }

            // Every entry was read, so the list is structurally sound; what
            // remains is whether the gateway qualified it.
            outcome = RoomListOutcome.Ok;

            // The status byte, if the gateway sent one. Read from where the
            // entries ended rather than from the end of the payload: the two
            // differ the moment anything else is appended, and reading the last
            // byte of a longer payload would report on something else entirely.
            if (offset < payload.Length)
            {
                switch (payload[offset])
                {
                    case 0: outcome = RoomListOutcome.Ok; break;
                    case 1: outcome = RoomListOutcome.Refused; break;
                    case 2: outcome = RoomListOutcome.RoomsOmitted; break;
                    default: outcome = RoomListOutcome.UnknownStatus; break;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether a list with <paramref name="outcome"/> holds the project's rooms and should be
        /// shown: <see langword="true"/> for <see cref="RoomListOutcome.Ok"/> and
        /// <see cref="RoomListOutcome.RoomsOmitted"/>.
        /// </summary>
        public static bool CarriesUsableList(RoomListOutcome outcome)
        {
            return outcome == RoomListOutcome.Ok
                || outcome == RoomListOutcome.RoomsOmitted;
        }

        // ── Internal helpers ───────────────────────────────────────────────────

        // Hard ceiling that no per-call cap can exceed.  16-bit length fields
        // top out at 65 535, so 4 096 is already two orders of magnitude
        // above any legitimate field; anything larger is treated as
        // protocol-violation by every well-behaved gateway.
        private const int HardMaxStringBytes = 4096;
        private const int DefaultMaxStringBytes = 4096;

        // Configurable cap surfaced for NetworkManager to align with
        // <c>NetworkSettings.maxLobbyStringBytes</c>.  Static rather than
        // threaded through every Parse* method to avoid touching the broad
        // public API surface; the field is written once on
        // <c>NetworkManager.Awake</c> and never again from production code.
        // Reads are non-volatile because the only concurrent reader is the
        // single main-thread parser.
        private static int _configuredMaxStringBytes = DefaultMaxStringBytes;

        /// <summary>
        /// Sets the longest string, in bytes, the parser accepts in a room message. Values are
        /// clamped to the range 16 to 4096. <c>NetworkManager</c> sets it from
        /// <c>NetworkSettings.maxLobbyStringBytes</c>; not intended to be called from game code.
        /// </summary>
        public static void ConfigureMaxStringBytes(int maxStringBytes)
        {
            if (maxStringBytes < 16) maxStringBytes = 16;
            if (maxStringBytes > HardMaxStringBytes) maxStringBytes = HardMaxStringBytes;
            _configuredMaxStringBytes = maxStringBytes;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Restores the default string-length limit. Present only when
        /// <c>UNITY_INCLUDE_TESTS</c> is defined; not intended to be called from game code.
        /// </summary>
        public static void ResetMaxStringBytesForTests()
        {
            _configuredMaxStringBytes = DefaultMaxStringBytes;
        }
#endif // UNITY_INCLUDE_TESTS

        // Strict UTF-8 decoder.  The default <see cref="Encoding.UTF8"/>
        // silently replaces malformed sequences with U+FFFD, which lets a
        // hostile server smuggle bytes that survive the parser but mutate
        // upstream string-comparison invariants (reserved-key checks, scene
        // names).  The strict variant throws <see cref="DecoderFallbackException"/>
        // on the first invalid sequence; the parser converts that into a
        // clean parse-failure return.
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        /// <summary>
        /// Read a length-prefixed UTF-8 string: [len:2 LE][data:len].
        /// </summary>
        internal static bool TryReadString(byte[] buf, ref int offset, out string value)
        {
            value = null;
            // Use subtraction not addition for the boundary check so
            // `offset + 2` cannot overflow into a permissive read on a
            // pathological large `offset`.  The same pattern is applied to
            // every multi-byte read in this file.
            if (buf == null || offset < 0 || offset > buf.Length - 2) return false;

            int len = ReadU16LE(buf, ref offset);

            // Per-call cap from the configured setting (typically 256 from
            // NetworkSettings.maxLobbyStringBytes); the hard 4 096-byte
            // ceiling is enforced regardless via the clamp in
            // <see cref="ConfigureMaxStringBytes"/>.
            if (len > _configuredMaxStringBytes) return false;

            if (len == 0) { value = string.Empty; return true; }
            if (len > buf.Length - offset) return false;

            try { value = StrictUtf8.GetString(buf, offset, len); }
            catch (DecoderFallbackException) { return false; }
            offset += len;
            return true;
        }

        /// <summary>
        /// Read a PlayerInfo record from the buffer.
        /// Layout: [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
        ///        [is_host:1][is_ready:1]
        /// </summary>
        internal static bool TryReadPlayerInfo(byte[] buf, ref int offset, out PlayerInfo player)
        {
            player = null;

            if (!TryReadString(buf, ref offset, out string playerId))    return false;
            if (!TryReadString(buf, ref offset, out string displayName)) return false;
            if (buf == null || offset > buf.Length - 2)                  return false;

            bool isHost  = buf[offset++] != 0;
            bool isReady = buf[offset++] != 0;

            player = new PlayerInfo(playerId, displayName, isHost, isReady);
            return true;
        }

        // Smallest legal serialised PlayerInfo: 2 (id_len=0) + 0 + 2 (name_len=0) + 0 + 1 (is_host) + 1 (is_ready).
        internal const int MinPlayerInfoBytes = 6;

        // ── Room-properties block (v4.1+) ─────────────────────────────────

        /// <summary>
        /// Bit 0 of <c>properties_flags</c>: the payload carries the room's
        /// whole property map.  Every other bit is reserved and ignored, so a
        /// later server may set one without this parser refusing the block.
        /// </summary>
        private const byte PropertiesCompleteFlag = 0x01;

        /// <summary>
        /// Ceiling on the room-properties payload, matching the gateway's
        /// <c>MAX_RELAYED_ROOM_PROPERTIES_BYTES</c>.
        /// </summary>
        /// <remarks>
        /// This block deliberately does NOT go through
        /// <see cref="TryReadString"/>.  That helper's cap is
        /// <c>NetworkSettings.maxLobbyStringBytes</c> — 256 by default — which
        /// is a bound on a room NAME, and it would refuse a legitimate property
        /// snapshot of a few hundred bytes as if the packet were hostile. The
        /// payload is a document, not a label, so it carries a document's
        /// ceiling; the length is still bounded before a single byte is copied.
        /// </remarks>
        internal const int MaxRoomPropertiesPayloadBytes = 8192;

        /// <summary>
        /// Read the trailing <c>[flags:1][len:2][payload:N]</c> room-properties
        /// block.  Never throws: every failure is reported as
        /// <see cref="JoinPropertiesStatus.Malformed"/> so a damaged block costs
        /// the properties and not the join.
        /// </summary>
        private static JoinPropertiesStatus TryReadRoomProperties(
            byte[] buf,
            ref int offset,
            out Dictionary<string, PropertyValue> properties,
            out int version,
            out PropertyJson.RoomEntryDocument document)
        {
            properties = null;
            version    = 0;
            document   = null;

            if (buf == null || offset < 0 || offset >= buf.Length)
                return JoinPropertiesStatus.Malformed;

            byte flags = buf[offset++];

            // Subtraction, not addition — the same overflow-safe boundary form
            // every other read in this file uses.
            if (offset > buf.Length - 2) return JoinPropertiesStatus.Malformed;
            int len = ReadU16LE(buf, ref offset);
            if (len > MaxRoomPropertiesPayloadBytes)  return JoinPropertiesStatus.Malformed;
            if (len > buf.Length - offset)            return JoinPropertiesStatus.Malformed;
            if (len == 0)                             return JoinPropertiesStatus.Malformed;

            string json;
            try { json = StrictUtf8.GetString(buf, offset, len); }
            catch (DecoderFallbackException) { return JoinPropertiesStatus.Malformed; }
            offset += len;

            return TryDecodeRoomEntryProperties(
                json, (flags & PropertiesCompleteFlag) != 0, out properties, out version, out document);
        }

        /// <summary>
        /// Decode a room-entry property snapshot from the canonical payload the
        /// server authors, and classify what it turned out to be.
        /// </summary>
        /// <remarks>
        /// Shared by both doors into an occupied room — the <c>RoomJoin</c>
        /// response block and the matchmaking reply, which carries the same
        /// document as a JSON string because its envelope is bound by
        /// <c>JsonUtility</c> and cannot hold a map.  One decoder rather than
        /// two: the two paths cannot then disagree about what a snapshot means,
        /// which is the failure mode a second copy would introduce silently.
        /// </remarks>
        internal static JoinPropertiesStatus TryDecodeRoomEntryProperties(
            string json,
            bool complete,
            out Dictionary<string, PropertyValue> properties,
            out int version)
            => TryDecodeRoomEntryProperties(json, complete, out properties, out version, out _);

        /// <summary>
        /// <see cref="TryDecodeRoomEntryProperties(string, bool, out Dictionary{string, PropertyValue}, out int)"/>,
        /// and the players' half of the document: each seated player's map and
        /// version, for <see cref="WithPlayerSnapshots"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="players"/> is <see langword="null"/> whenever the room's
        /// half is not adopted — a document that did not parse says nothing
        /// about anybody.
        /// </remarks>
        internal static JoinPropertiesStatus TryDecodeRoomEntryProperties(
            string json,
            bool complete,
            out Dictionary<string, PropertyValue> properties,
            out int version,
            out PropertyJson.RoomEntryDocument players)
        {
            properties = null;
            version    = 0;
            players    = null;

            if (string.IsNullOrEmpty(json)) return JoinPropertiesStatus.Malformed;
            if (json.Length > MaxRoomPropertiesPayloadBytes) return JoinPropertiesStatus.Malformed;

            try
            {
                var decoded = PropertyJson.DecodeRoomEntryPayload(json);
                version    = decoded.Version;
                properties = decoded.Properties;
                players    = decoded;
            }
            catch (Exception)
            {
                // The decoder is hand-rolled and hostile input reaches it, so
                // the catch is deliberately wide — the same treatment the
                // broadcast path gives the same decoder.  A room entry must not
                // be lost to a property document.
                properties = null;
                version    = 0;
                players    = null;
                return JoinPropertiesStatus.Malformed;
            }

            // A property version is a count of accepted writes and cannot be
            // negative on any server.  It is refused rather than adopted because
            // adopting it would be believed: every subsequent write would be
            // tagged from a number the server has never held, and the conflict
            // arm sends no reply.  Nothing legitimate produces one, which is
            // exactly why it is worth refusing at the trust boundary.
            if (version < 0)
            {
                properties = null;
                version    = 0;
                players    = null;
                return JoinPropertiesStatus.Malformed;
            }

            // A SNAPSHOT never contains a deletion: the server's stored map has
            // no entry for a removed key.  The sentinel is meaningful only in a
            // delta, so one arriving here is dropped rather than stored — a
            // caller reading Properties must never meet "delete me" as a value.
            DropDeletions(properties);

            return complete
                ? JoinPropertiesStatus.Complete
                : JoinPropertiesStatus.Incomplete;
        }

        private static void DropDeletions(Dictionary<string, PropertyValue> properties)
        {
            if (properties == null || properties.Count == 0) return;
            List<string> deletions = null;
            foreach (var kv in properties)
            {
                if (!kv.Value.IsDeletion) continue;
                if (deletions == null) deletions = new List<string>();
                deletions.Add(kv.Key);
            }
            if (deletions != null)
            {
                for (int i = 0; i < deletions.Count; i++) properties.Remove(deletions[i]);
            }
        }

        /// <summary>
        /// <paramref name="roster"/> with each player the room-entry document
        /// names given that player's map and version; a player it does not name
        /// keeps the empty map at version 0 that a player who has never written
        /// holds. Returns whether the document carried every player who has
        /// written a property whole.
        /// </summary>
        /// <remarks>
        /// <para>🔑 Without it every other player starts at version 0 with an empty
        /// map, and the player-property broadcast is a delta: the next change
        /// merges onto nothing, so the joiner sees that change's keys and never
        /// the ones set before it arrived (audit P7-E4). And this client's OWN
        /// entry starts at 0 while the server holds n for a player re-entering a
        /// room it has written in, so every write it makes is refused, without a
        /// reply, for the life of the room (<c>ROOM-RD-09</c>).</para>
        /// <para>An entry carrying the version alone sets the version on an empty
        /// map: the server sends one only for this client's own player, whose
        /// writes need it. An entry for a player not on the roster is ignored,
        /// and so is one with a negative version — a property version counts
        /// accepted writes, and one adopted from a damaged entry would be
        /// believed.</para>
        /// </remarks>
        internal static bool WithPlayerSnapshots(PlayerInfo[] roster, PropertyJson.RoomEntryDocument document)
        {
            if (document == null) return true;
            var players = document.Players;
            if (roster == null || players == null || players.Count == 0) return document.PlayersComplete;

            bool whole = document.PlayersComplete;
            for (int i = 0; i < roster.Length; i++)
            {
                var seat = roster[i];
                if (seat == null || !players.TryGetValue(seat.PlayerId, out var entry)) continue;
                if (entry.Version < 0)
                {
                    whole = false;
                    continue;
                }
                var props = entry.Properties ?? new Dictionary<string, PropertyValue>();
                DropDeletions(props);
                roster[i] = seat.WithProperties(props, entry.Version);
            }
            return whole;
        }

        private static int ReadU16LE(byte[] buf, ref int offset)
        {
            int value = buf[offset] | (buf[offset + 1] << 8);
            offset += 2;
            return value;
        }
    }
}
