// RTMPE SDK — Runtime/Rooms/MasterClientPacketBuilder.cs
//
// Builds the payload bytes for the Phase 2 room-management packets:
//
//  • MasterClientTransfer (0x2D) — client → server
//  • KickPlayer            (0x2E) — client → server
//  • SceneLoaded           (0x2F) — client → server
//
// Every one of them names the room it is for.  All three are sent reliably,
// and a request whose room is read where it lands rather than where it was
// written is applied to whatever room the sender has reached by then.
//
// The payload is a minimal JSON document.  Keeping the builder in a
// dedicated file (mirroring PropertyPacketBuilder) lets the packet layer
// stay Unity-agnostic and keeps the wire format self-documenting.

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Static helpers that serialise the inner JSON payloads for Phase 2
    /// room-management packets.  Every method returns UTF-8 bytes ready to
    /// pass to <see cref="RTMPE.Protocol.PacketBuilder.Build"/>.
    /// </summary>
    public static class MasterClientPacketBuilder
    {
        /// <summary>
        /// Reject a payload that would not say which room it is for.
        /// </summary>
        /// <remarks>
        /// All three packets are sent reliably, so a lost acknowledgement puts
        /// the same bytes on the wire again.  The server used to take the room
        /// from the session at the moment the copy arrived — which is a
        /// different room once the sender has moved on, and for a kick or a
        /// master transfer that is somebody else's room.  The <c>room_id</c>
        /// member below is what pins it.
        /// </remarks>
        private static void RequireRoomId(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
                throw new ArgumentException(
                    "roomId must not be null or empty — a room-scoped request names the room it is for.",
                    nameof(roomId));
        }

        /// <summary>
        /// Build the payload for a <c>MasterClientTransfer</c> (0x2D) packet.
        /// Shape: <c>{"room_id":"...","target_player_id":"..."}</c>.
        /// </summary>
        /// <param name="roomId">The room whose master role is being handed
        /// over.  See <see cref="RequireRoomId"/> for why the request names
        /// it.</param>
        /// <param name="targetPlayerId">The player to promote.</param>
        public static byte[] BuildTransferPayload(string roomId, string targetPlayerId)
        {
            RequireRoomId(roomId);
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));
            return Encoding.UTF8.GetBytes(
                "{\"room_id\":" + JsonEncodeString(roomId)
                + ",\"target_player_id\":" + JsonEncodeString(targetPlayerId) + "}");
        }

        /// <summary>
        /// Build the payload for a <c>KickPlayer</c> (0x2E) packet.
        /// Shape: <c>{"room_id":"...","target_player_id":"..."}</c>.
        /// </summary>
        /// <param name="roomId">The room to remove the player from.</param>
        /// <param name="targetPlayerId">The player to remove.</param>
        public static byte[] BuildKickPayload(string roomId, string targetPlayerId)
        {
            RequireRoomId(roomId);
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));
            return Encoding.UTF8.GetBytes(
                "{\"room_id\":" + JsonEncodeString(roomId)
                + ",\"target_player_id\":" + JsonEncodeString(targetPlayerId) + "}");
        }

        /// <summary>
        /// Build the payload for a <c>SceneLoaded</c> (0x2F) packet.
        /// Shape: <c>{"room_id":"...","scene_name":"..."}</c>.
        /// </summary>
        /// <param name="roomId">The room the scene was loaded for.</param>
        /// <param name="sceneName">The scene that finished loading.</param>
        public static byte[] BuildSceneLoadedPayload(string roomId, string sceneName)
        {
            RequireRoomId(roomId);
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentException("sceneName must not be null or empty.", nameof(sceneName));
            return Encoding.UTF8.GetBytes(
                "{\"room_id\":" + JsonEncodeString(roomId)
                + ",\"scene_name\":" + JsonEncodeString(sceneName) + "}");
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Minimal JSON string encoder: wraps in quotes and escapes the
        /// characters required by RFC 8259.  Kept private so the payload
        /// shape stays owned by this file.
        /// </summary>
        private static string JsonEncodeString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
