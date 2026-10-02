// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyInfo.cs
//
// Immutable snapshot of a single room as returned by the lobby system
// (LobbyJoin 0x27 reply and LobbyRoomListUpdate 0x2A push).

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// A read-only snapshot of one room in a lobby's room list.
    /// </summary>
    public sealed class LobbyRoomInfo
    {
        /// <summary>The room id. Pass it to <see cref="RoomManager.JoinRoom"/>.</summary>
        public string RoomId       { get; }
        /// <summary>
        /// The room's six-character join code. Pass it to <see cref="RoomManager.JoinRoomByCode"/>.
        /// </summary>
        public string RoomCode     { get; }
        /// <summary>The room's display name.</summary>
        public string Name         { get; }
        /// <summary>The number of players in the room.</summary>
        public int    PlayerCount  { get; }
        /// <summary>The room's capacity.</summary>
        public int    MaxPlayers   { get; }
        /// <summary>Whether the room appears in public lists.</summary>
        public bool   IsPublic     { get; }
        /// <summary>
        /// The lobby the room belongs to. Empty means no lobby: the room can be joined by
        /// id, by code or through matchmaking, and appears in no lobby's room list.
        /// </summary>
        public string LobbyName    { get; }

        /// <summary>
        /// Creates a snapshot. The SDK builds these from the server's room lists; the
        /// constructor is for tests.
        /// </summary>
        public LobbyRoomInfo(
            string roomId,
            string roomCode,
            string name,
            int    playerCount,
            int    maxPlayers,
            bool   isPublic,
            string lobbyName)
        {
            RoomId      = roomId      ?? string.Empty;
            RoomCode    = roomCode    ?? string.Empty;
            Name        = name        ?? string.Empty;
            PlayerCount = playerCount;
            MaxPlayers  = maxPlayers;
            IsPublic    = isPublic;
            LobbyName   = lobbyName   ?? string.Empty;
        }
    }
}
