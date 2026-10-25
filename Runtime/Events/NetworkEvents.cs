// RTMPE SDK — Runtime/Events/NetworkEvents.cs
//
// Structured event data types for the RTMPE SDK public event surface.
// All structs are value types to avoid GC pressure on the hot event path.

namespace RTMPE.Events
{
    /// <summary>
    /// Value types that describe connection and room events, for applications that pass
    /// SDK events through their own event code.
    /// </summary>
    /// <remarks>
    /// The SDK does not raise these types. Its events are delegates on
    /// <see cref="Core.NetworkManager"/> and its managers, for example
    /// <see cref="Core.NetworkManager.OnStateChanged"/> and
    /// <see cref="Rooms.RoomManager.OnRoomJoined"/>.
    /// </remarks>
    public static class NetworkEvents
    {
        // ── Connection ────────────────────────────────────────────────────────

        /// <summary>A change of <see cref="Core.NetworkState"/>.</summary>
        public struct StateChangedArgs
        {
            /// <summary>The state before the change.</summary>
            public Core.NetworkState Previous;
            /// <summary>The state after the change.</summary>
            public Core.NetworkState Current;
        }

        // The OnConnected event is raised as a bare `Action` (see
        // NetworkManager.Events.cs).  Apps that need the session-issued
        // tokens read them directly from NetworkManager.Instance.JwtToken
        // / .ReconnectToken at the moment they need them, where the
        // RedactedString wrapper enforces the no-accidental-leak contract
        // at compile time.  No structured event payload is exposed for
        // this transition; if a future flow surfaces additional connect-
        // time fields, define a fresh args struct alongside the typed
        // event declaration so the two stay co-located.

        // ── Disconnection ─────────────────────────────────────────────────────

        /// <summary>A connection that ended.</summary>
        public struct DisconnectedArgs
        {
            /// <summary>Why the connection ended.</summary>
            public Core.DisconnectReason Reason;
        }

        // ── Room ─────────────────────────────────────────────────────

        /// <summary>
        /// A room this client joined, identified by a number. <see cref="RoomJoinedArgs"/>
        /// carries the full <see cref="Rooms.RoomInfo"/> instead.
        /// </summary>
        public struct JoinedRoomArgs
        {
            /// <summary>The room's numeric identifier.</summary>
            public ulong RoomId;
        }

        /// <summary>A room this client created.</summary>
        public struct RoomCreatedArgs
        {
            /// <summary>The new room.</summary>
            public Rooms.RoomInfo Room;
        }

        /// <summary>A room this client entered.</summary>
        public struct RoomJoinedArgs
        {
            /// <summary>The room, including its player roster.</summary>
            public Rooms.RoomInfo Room;
        }

        /// <summary>This client left its room.</summary>
        public struct RoomLeftArgs { }

        /// <summary>Another player entered the current room.</summary>
        public struct PlayerJoinedArgs
        {
            /// <summary>The player who entered.</summary>
            public Rooms.PlayerInfo Player;
        }

        /// <summary>Another player left the current room.</summary>
        public struct PlayerLeftArgs
        {
            /// <summary>The id of the player who left.</summary>
            public string PlayerId;
        }

        /// <summary>A list of rooms, such as the reply to <see cref="Rooms.RoomManager.ListRooms"/>.</summary>
        public struct RoomListReceivedArgs
        {
            /// <summary>The rooms in the list.</summary>
            public Rooms.RoomInfo[] Rooms;
        }

        // ── Heartbeat / RTT ───────────────────────────────────────────────────

        /// <summary>One measured heartbeat round trip.</summary>
        public struct HeartbeatAckArgs
        {
            /// <summary>The round-trip time, in milliseconds.</summary>
            public float RttMs;
        }
    }
}
