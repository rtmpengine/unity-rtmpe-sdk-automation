// RTMPE SDK — Runtime/Rooms/JoinRoomOptions.cs
//
// Options for the RoomManager.JoinRoom() / JoinRoomByCode() calls.

namespace RTMPE.Rooms
{
    /// <summary>
    /// Options for <see cref="RoomManager.JoinRoom"/> and
    /// <see cref="RoomManager.JoinRoomByCode"/>. Every property has a default; pass
    /// <see langword="null"/> or a new instance to use them all.
    /// </summary>
    public sealed class JoinRoomOptions
    {
        /// <summary>
        /// The name other players see, up to 32 characters. Default empty: your game decides
        /// what to show for a player without a name.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;
    }
}
