// RTMPE SDK — Runtime/Rooms/CreateRoomOptions.cs
//
// Options for the RoomManager.CreateRoom() call.

namespace RTMPE.Rooms
{
    /// <summary>
    /// Options for <see cref="RoomManager.CreateRoom"/>. Every property has a default;
    /// pass <see langword="null"/> or a new instance to use them all.
    /// </summary>
    public sealed class CreateRoomOptions
    {
        /// <summary>
        /// The room's display name, up to 64 characters. Default empty: the server names the
        /// room.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The room's capacity, from 1 to 100. Default 0: the server's default of 100.
        /// </summary>
        public int MaxPlayers { get; set; }

        /// <summary>
        /// Whether the room appears in public room lists. Default <see langword="true"/>.
        /// </summary>
        public bool IsPublic { get; set; } = true;

        /// <summary>
        /// Whether the creator joins the new room as its host. Default
        /// <see langword="true"/>: <see cref="RoomManager.OnRoomJoined"/> follows
        /// <see cref="RoomManager.OnRoomCreated"/>.
        /// </summary>
        /// <remarks>
        /// With <see langword="false"/>, the room stays empty until you call
        /// <see cref="RoomManager.JoinRoom"/> yourself. Either way, start gameplay from
        /// <see cref="RoomManager.OnRoomJoined"/>, not from
        /// <see cref="RoomManager.OnRoomCreated"/>.
        /// </remarks>
        public bool AutoJoinAsHost { get; set; } = true;
    }
}
