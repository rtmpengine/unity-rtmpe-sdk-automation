// RTMPE SDK — Runtime/Rooms/MatchmakingOptions.cs
//
// Configuration for a matchmaking (AutoJoinOrCreate) request.
// Pass an instance to NetworkManager.Matchmaking.StartMatchmaking().

namespace RTMPE.Rooms
{
    /// <summary>
    /// Parameters for an AutoJoinOrCreate matchmaking request.
    /// The server atomically finds an open waiting room that matches
    /// <see cref="Mode"/> + <see cref="LobbyName"/> + project_id, joins
    /// the player, and — when no room is found — creates a new one.
    /// </summary>
    public sealed class MatchmakingOptions
    {
        /// <summary>
        /// Game-mode key used to match rooms (e.g. "TDM", "BR").
        /// Must not be null or empty.
        /// </summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>
        /// Lobby namespace to search within.  Optional here, unlike
        /// <see cref="LobbyQueryOptions.LobbyName"/>: an empty string means the
        /// matched room belongs to no lobby at all, so it is reachable by
        /// matchmaking and invisible to every lobby browser.  A non-empty value
        /// is held to <see cref="RTMPE.Rooms.LobbyName"/>'s rule.
        /// </summary>
        public string LobbyName { get; set; } = string.Empty;

        /// <summary>
        /// Minimum player count required to start the game.
        /// Values ≤ 0 let the server apply its default (2).
        /// </summary>
        public int MinPlayers { get; set; } = 0;

        /// <summary>
        /// Per-room player capacity used when the server creates a new room.
        /// Values ≤ 0 let the server apply its default (see entities.MaxPlayersDefault).
        /// </summary>
        public int MaxPlayers { get; set; } = 0;

        /// <summary>
        /// Display name shown in the room roster for the requesting player.
        /// Optional — leave empty to use the server-side default.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;
    }
}
