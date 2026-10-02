// RTMPE SDK — Runtime/Rooms/MatchmakingOptions.cs
//
// Configuration for a matchmaking (AutoJoinOrCreate) request.
// Pass an instance to NetworkManager.Matchmaking.StartMatchmaking().

namespace RTMPE.Rooms
{
    /// <summary>
    /// Options for a join-or-create matchmaking request. The server places the player in
    /// an open room of your project with the same <see cref="Mode"/> and
    /// <see cref="LobbyName"/>, or creates one when none is found.
    /// </summary>
    public sealed class MatchmakingOptions
    {
        /// <summary>
        /// The game-mode key, for example <c>"TDM"</c>. Required: players are matched with
        /// others asking for the same mode. Up to 64 bytes of UTF-8, with no control or
        /// invisible formatting characters.
        /// </summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>
        /// The lobby the room belongs to. Optional, unlike
        /// <see cref="LobbyQueryOptions.LobbyName"/>: empty means the room belongs to no
        /// lobby, so it can be found by matchmaking but does not appear in lobby room
        /// lists. A non-empty name must follow the <see cref="RTMPE.Rooms.LobbyName"/>
        /// rules.
        /// </summary>
        public string LobbyName { get; set; } = string.Empty;

        /// <summary>
        /// The number of players needed to start. 0 or less: the server's default of 2.
        /// </summary>
        public int MinPlayers { get; set; } = 0;

        /// <summary>
        /// The capacity of a room created for this match. 0 or less: the server's default
        /// of 100.
        /// </summary>
        public int MaxPlayers { get; set; } = 0;

        /// <summary>
        /// The name other players see, up to 32 characters. Optional.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;
    }
}
