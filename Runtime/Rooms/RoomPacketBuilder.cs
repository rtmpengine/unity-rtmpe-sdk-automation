// RTMPE SDK — Runtime/Rooms/RoomPacketBuilder.cs
//
// Builds payload bytes for room-related packets (0x20–0x23).
// The caller wraps the returned payload with PacketBuilder.Build() to produce
// the full wire packet (13-byte header + payload).
//
// Wire formats (all little-endian):
//
// ── RoomCreate (0x20) Client → Server ──────────────────────────────────────
//  [name_len:2 LE][name:N UTF-8]
//  [max_players:1]
//  [is_public:1]
//  [request_id_len:2 LE][request_id:32 ASCII hex]  ← correlation trailer
//
// The request_id trailer is appended by the internal overload used by
// RoomManager when the pending-create correlator is active.  Gateways that
// consume only the first four fields are unaffected; a gateway that returns the
// field promotes TryMatch from FIFO fallback to id-based correlation, which is
// what lets a reply answering a request this client no longer holds be told
// apart from a first answer.
//
// ── RoomLeave (0x22) Client → Server ───────────────────────────────────────
//  [room_id_len:2 LE][room_id:N UTF-8]           (absent entirely before 8.0.0)
//
// The room is named so the operation is bound to the room the caller meant.
// An empty payload leaves the server to resolve one from the session at
// delivery time, which is what a re-sent copy used to apply to the wrong room.
//
// ── RoomJoin (0x21) Client → Server ────────────────────────────────────────
//  [room_id_len:2 LE][room_id:N UTF-8]           (empty if joining by code)
//  [room_code_len:2 LE][room_code:N UTF-8]       (empty if joining by ID)
//  [display_name_len:2 LE][display_name:N UTF-8]
//
// ── RoomLeave (0x22) Client → Server ───────────────────────────────────────
//  (empty payload — server identifies player by session)
//
// ── RoomList (0x23) Client → Server ────────────────────────────────────────
//  [public_only:1]
//
// What this file will not do is decide, on the Room Service's behalf, that a
// value it was handed is close enough.  The rules a room field is admitted
// under belong to the server and are restated in RoomFieldLimits; a value that
// breaks one is refused here, where the caller can still see which argument it
// passed, rather than shortened or moved into range and sent as though it had
// been asked for.

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Builds the create, join, leave and list requests that <see cref="RoomManager"/> sends.
    /// Not intended to be called from game code.
    /// </summary>
    /// <remarks>
    /// A value that breaks a rule in <see cref="RoomFieldLimits"/> throws
    /// <see cref="ArgumentException"/>; values are never shortened or moved into range. Each
    /// call returns a new array.
    /// </remarks>
    public static class RoomPacketBuilder
    {
        // A room id and a join code are compared, not displayed, and a value
        // shortened to fit is a different value: it either matches nothing or,
        // worse, matches something else.  Both are therefore encoded whole and
        // bounded only by what the wire's length field can express, while the
        // two fields a player reads are bounded by the rule the server applies
        // to them.
        private const int MaxUtf8BytesPerRune = 4;
        private const int MaxNameBytes        = RoomFieldLimits.MaxRoomNameRunes    * MaxUtf8BytesPerRune;
        private const int MaxDisplayNameBytes = RoomFieldLimits.MaxDisplayNameRunes * MaxUtf8BytesPerRune;

        // ── CreateRoom payload ─────────────────────────────────────────────────

        /// <summary>
        /// Builds a room-creation request.
        /// </summary>
        /// <param name="options">The room's options, or <see langword="null"/> for the
        /// defaults.</param>
        /// <returns>The encoded request.</returns>
        /// <exception cref="ArgumentException">
        /// <see cref="CreateRoomOptions.Name"/> or <see cref="CreateRoomOptions.MaxPlayers"/>
        /// breaks a rule in <see cref="RoomFieldLimits"/>. The message names the rule.
        /// </exception>
        public static byte[] BuildCreateRoomPayload(CreateRoomOptions options)
        {
            if (options == null) options = new CreateRoomOptions();
            RequireCreatable(options);

            byte[] nameBytes = EncodeUtf8(options.Name, MaxNameBytes, nameof(options));

            // Layout: [name_len:2][name:N][max_players:1][is_public:1]
            int size = 2 + nameBytes.Length + 1 + 1;
            var buf = new byte[size];
            int offset = 0;

            WriteU16LE(buf, ref offset, (ushort)nameBytes.Length);
            WriteBytes(buf, ref offset, nameBytes);
            buf[offset++] = (byte)options.MaxPlayers;
            buf[offset++] = (byte)(options.IsPublic ? 1 : 0);

            return buf;
        }

        /// <summary>
        /// Build the payload for a <c>RoomCreate</c> (0x20) request, appending a
        /// client-generated correlation identifier the gateway echoes in the
        /// response, so a reply can be matched to the request it answers.
        /// </summary>
        /// <remarks>
        /// Wire layout:
        /// <c>[name_len:2][name:N][max_players:1][is_public:1][request_id_len:2][request_id:32 hex]</c>
        ///
        /// The trailing field is omitted by the public overload; a gateway that
        /// predates it either stops reading at <c>is_public</c> (length-driven
        /// parsers) or silently ignores the extra bytes.  The response parser reads
        /// the echoed id conditionally, so both gateway generations interoperate.
        /// </remarks>
        internal static byte[] BuildCreateRoomPayload(CreateRoomOptions options, Guid requestId)
        {
            if (options == null) options = new CreateRoomOptions();
            RequireCreatable(options);

            byte[] nameBytes  = EncodeUtf8(options.Name, MaxNameBytes, nameof(options));
            // Encode the GUID as 32 lowercase hex characters (no dashes, no curly
            // braces).  Fixed length avoids a length-to-format ambiguity on the
            // receiving side and keeps the field a known 34 bytes on the wire.
            byte[] reqIdBytes = Encoding.UTF8.GetBytes(requestId.ToString("N"));

            // Layout: [name_len:2][name:N][max_players:1][is_public:1]
            //         [request_id_len:2][request_id:32]
            int size = 2 + nameBytes.Length + 1 + 1 + 2 + reqIdBytes.Length;
            var buf = new byte[size];
            int offset = 0;

            WriteU16LE(buf, ref offset, (ushort)nameBytes.Length);
            WriteBytes(buf, ref offset, nameBytes);
            buf[offset++] = (byte)options.MaxPlayers;
            buf[offset++] = (byte)(options.IsPublic ? 1 : 0);
            WriteU16LE(buf, ref offset, (ushort)reqIdBytes.Length);
            WriteBytes(buf, ref offset, reqIdBytes);

            return buf;
        }

        // ── JoinRoom payload ───────────────────────────────────────────────────

        /// <summary>
        /// Builds a join request, by room id or by join code: pass one and leave the other
        /// <see langword="null"/> or empty.
        /// </summary>
        /// <remarks>
        /// The join code is upper-cased before it is checked; the room id is sent as given.
        /// </remarks>
        /// <param name="roomId">The room id, or <see langword="null"/>.</param>
        /// <param name="roomCode">The join code, or <see langword="null"/>.</param>
        /// <param name="options">Join options, or <see langword="null"/> for the
        /// defaults.</param>
        /// <returns>The encoded request.</returns>
        /// <exception cref="ArgumentException">
        /// The display name or the join code breaks a rule in <see cref="RoomFieldLimits"/>.
        /// The message names the rule.
        /// </exception>
        public static byte[] BuildJoinRoomPayload(
            string roomId,
            string roomCode,
            JoinRoomOptions options)
        {
            if (options == null) options = new JoinRoomOptions();

            roomCode = RoomFieldLimits.NormaliseRoomCode(roomCode);
            if (!string.IsNullOrEmpty(roomCode))
                Require(RoomFieldLimits.ValidateRoomCode(roomCode), nameof(roomCode));
            Require(RoomFieldLimits.ValidateDisplayName(options.DisplayName), nameof(options));

            byte[] roomIdBytes      = EncodeIdentifier(roomId, nameof(roomId));
            byte[] roomCodeBytes    = EncodeIdentifier(roomCode, nameof(roomCode));
            byte[] displayNameBytes = EncodeUtf8(options.DisplayName, MaxDisplayNameBytes, nameof(options));

            // Layout: [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
            //        [display_name_len:2][display_name:N]
            int size = 2 + roomIdBytes.Length
                     + 2 + roomCodeBytes.Length
                     + 2 + displayNameBytes.Length;
            var buf = new byte[size];
            int offset = 0;

            WriteU16LE(buf, ref offset, (ushort)roomIdBytes.Length);
            WriteBytes(buf, ref offset, roomIdBytes);
            WriteU16LE(buf, ref offset, (ushort)roomCodeBytes.Length);
            WriteBytes(buf, ref offset, roomCodeBytes);
            WriteU16LE(buf, ref offset, (ushort)displayNameBytes.Length);
            WriteBytes(buf, ref offset, displayNameBytes);

            return buf;
        }

        // ── LeaveRoom payload ──────────────────────────────────────────────────

        /// <summary>
        /// Builds a leave request that names no room: the server applies it to the room this
        /// client is in when the request arrives. Prefer
        /// <see cref="BuildLeaveRoomPayload(string)"/>.
        /// </summary>
        /// <returns>The encoded request.</returns>
        public static byte[] BuildLeaveRoomPayload()
            => Array.Empty<byte>();

        /// <summary>
        /// Builds a request to leave the room <paramref name="roomId"/>.
        /// </summary>
        /// <param name="roomId">The room to leave.</param>
        /// <returns>The encoded request.</returns>
        public static byte[] BuildLeaveRoomPayload(string roomId)
        {
            byte[] roomIdBytes = EncodeIdentifier(roomId, nameof(roomId));

            var buf = new byte[2 + roomIdBytes.Length];
            int offset = 0;
            WriteU16LE(buf, ref offset, (ushort)roomIdBytes.Length);
            WriteBytes(buf, ref offset, roomIdBytes);
            return buf;
        }

        // ── ListRooms payload ──────────────────────────────────────────────────

        /// <summary>
        /// Builds a room-list request.
        /// </summary>
        /// <param name="publicOnly">Whether to leave out private rooms.</param>
        /// <returns>The encoded request.</returns>
        public static byte[] BuildListRoomsPayload(bool publicOnly = true)
            => new byte[] { (byte)(publicOnly ? 1 : 0) };

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Refuse a value the Room Service would refuse, naming the rule.
        /// </summary>
        /// <remarks>
        /// The refusal is deliberately not a substitution. A name cut to fit
        /// and a player cap moved into range both leave the caller believing
        /// it asked for something it did not, and the room that opens is not
        /// the room the game described — whereas a refusal reaches the
        /// developer while the argument that caused it is still in front of
        /// them.
        /// </remarks>
        private static void Require(string reason, string parameterName)
        {
            if (reason != null)
                throw new ArgumentException("[RTMPE] " + reason, parameterName);
        }

        private static void RequireCreatable(CreateRoomOptions options)
        {
            Require(RoomFieldLimits.ValidateRoomName(options.Name), nameof(options));
            Require(RoomFieldLimits.ValidateMaxPlayers(options.MaxPlayers), nameof(options));
        }

        /// <summary>
        /// Encode a value already judged against the rule that bounds it.
        /// </summary>
        /// <remarks>
        /// ⛔ The refusal below is unreachable from every path in this file, and
        /// deliberately kept. <paramref name="maxBytes"/> is the field's rune
        /// budget times the widest encoding a single rune has, so a value that
        /// passed <see cref="RoomFieldLimits"/> meets the ceiling at its
        /// extreme and cannot pass it — sixty-four four-byte characters are
        /// exactly two hundred and fifty-six bytes.
        ///
        /// It stands because the arithmetic is the only thing making it
        /// unreachable: a rune budget raised without its byte budget following,
        /// or a caller reaching this without judging first, both turn a
        /// silently shortened name back into a live defect. The premise is
        /// asserted in the tests rather than trusted here.
        /// </remarks>
        private static byte[] EncodeUtf8(string value, int maxBytes, string parameterName)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();

            byte[] raw = Encoding.UTF8.GetBytes(value);
            if (raw.Length > maxBytes)
                throw new ArgumentException(
                    $"[RTMPE] value encodes to {raw.Length} bytes, past the {maxBytes} this field carries.",
                    parameterName);

            return raw;
        }

        /// <summary>
        /// Encode an identifier whole.
        /// </summary>
        /// <remarks>
        /// The bound here is the one the encoding itself imposes: each field is
        /// preceded by a 16-bit length, and a longer value would be written as
        /// its own remainder and read as a different identifier.
        ///
        /// ⛔ It is not the bound a caller meets. One datagram carries far less
        /// than sixty-five thousand bytes, so an identifier long enough to
        /// matter is refused by <c>PacketBuilder</c> first — which is why
        /// <c>RoomManager</c> reports rather than raises: an over-long id is a
        /// bad argument like any other, and this class refuses it at whichever
        /// layer notices.
        /// </remarks>
        private static byte[] EncodeIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();

            byte[] raw = Encoding.UTF8.GetBytes(value);
            if (raw.Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"[RTMPE] identifier encodes to {raw.Length} bytes, which the wire's " +
                    "16-bit length field cannot express.",
                    parameterName);

            return raw;
        }

        private static void WriteU16LE(byte[] buf, ref int offset, ushort value)
        {
            buf[offset++] = (byte)(value & 0xFF);
            buf[offset++] = (byte)(value >> 8);
        }

        private static void WriteBytes(byte[] buf, ref int offset, byte[] data)
        {
            if (data.Length == 0) return;
            Buffer.BlockCopy(data, 0, buf, offset, data.Length);
            offset += data.Length;
        }
    }
}
