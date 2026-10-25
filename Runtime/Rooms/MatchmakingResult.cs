// RTMPE SDK — Runtime/Rooms/MatchmakingResult.cs
//
// Carries the server reply from a MatchmakingResponse (0x2B) packet.

using RTMPE.Core;

namespace RTMPE.Rooms
{
    /// <summary>
    /// The outcome of a successful matchmaking request, delivered through
    /// <see cref="MatchmakingManager.OnMatchmakingComplete"/>.
    /// </summary>
    public sealed class MatchmakingResult
    {
        /// <summary>The id of the room the player was placed in.</summary>
        public string RoomId { get; }

        /// <summary>The room's six-character join code.</summary>
        public string RoomCode { get; }

        /// <summary>
        /// <see langword="true"/> when the server created the room for this match;
        /// <see langword="false"/> when it placed the player in an existing room.
        /// </summary>
        public bool Created { get; }

        internal MatchmakingResult(string roomId, string roomCode, bool created)
        {
            RoomId   = roomId   ?? string.Empty;
            RoomCode = roomCode ?? string.Empty;
            Created  = created;
        }

        /// <summary>
        /// Returns a description for logs. <see cref="RoomCode"/> is shown as <c>***</c>.
        /// </summary>
        public override string ToString()
            => $"MatchmakingResult(roomId={RoomId}, roomCode={LogRedaction.RoomCode(RoomCode)}, created={Created})";
    }
}
