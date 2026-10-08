// RTMPE SDK — Runtime/Rooms/PlayerInfo.cs
//
// Immutable snapshot of a player's lobby-visible state.
// Mirrors the PlayerInfo message in modules/room/interface/grpc/room.proto.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// A read-only snapshot of a player in a room.
    /// </summary>
    public sealed class PlayerInfo
    {
        /// <summary>The player's id in the room.</summary>
        public string PlayerId { get; }

        /// <summary>
        /// The display name given in <see cref="JoinRoomOptions"/> or
        /// <see cref="MatchmakingOptions"/>, up to 32 characters. May be empty.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>Whether this player is the room's host.</summary>
        public bool IsHost { get; }

        /// <summary>Whether the player has signalled ready.</summary>
        public bool IsReady { get; }

        /// <summary>The player's properties. Never <see langword="null"/>.</summary>
        public IReadOnlyDictionary<string, PropertyValue> Properties { get; }

        /// <summary>
        /// Increases with every write to this player's properties that the server applies.
        /// </summary>
        public int PropertiesVersion { get; }

        /// <summary>
        /// Creates a snapshot. The SDK builds these from what the server sends; the
        /// constructor is for tests.
        /// </summary>
        public PlayerInfo(
            string playerId,
            string displayName,
            bool   isHost,
            bool   isReady,
            IReadOnlyDictionary<string, PropertyValue> properties = null,
            int    propertiesVersion = 0)
        {
            PlayerId          = playerId ?? string.Empty;
            DisplayName       = displayName ?? string.Empty;
            IsHost            = isHost;
            IsReady           = isReady;
            // Defensive copy — see [RoomInfo.FreezeProperties] for rationale.
            Properties        = RoomInfo.FreezeProperties(properties);
            PropertiesVersion = propertiesVersion;
        }

        /// <summary>
        /// Returns a copy of this snapshot with <paramref name="properties"/> and
        /// <paramref name="version"/> in place of the current ones.
        /// </summary>
        /// <remarks>
        /// The map replaces the current properties whole; it is not merged into them.
        /// </remarks>
        public PlayerInfo WithProperties(IReadOnlyDictionary<string, PropertyValue> properties, int version)
            => new PlayerInfo(PlayerId, DisplayName, IsHost, IsReady, properties, version);

        /// <summary>
        /// Returns a copy of this snapshot with <see cref="IsHost"/> set to
        /// <paramref name="isHost"/>.
        /// </summary>
        public PlayerInfo WithIsHost(bool isHost)
            => new PlayerInfo(PlayerId, DisplayName, isHost, IsReady, Properties, PropertiesVersion);

        private static readonly IReadOnlyDictionary<string, PropertyValue> EmptyProperties
            = new Dictionary<string, PropertyValue>(0);

        public override string ToString()
            => $"Player({PlayerId}, \"{DisplayName}\", host={IsHost}, ready={IsReady})";
    }
}
