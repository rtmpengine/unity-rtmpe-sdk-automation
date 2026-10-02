// RTMPE SDK — Runtime/Core/NetworkManager.Events.cs
//
// Public events raised on the Unity main thread.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>Raised on every state change, as <c>(previous, current)</c>.</summary>
        public event Action<NetworkState, NetworkState> OnStateChanged;

        /// <summary>
        /// Raised on every change to <see cref="NetworkState.Connected"/>: when
        /// <see cref="Connect(string)"/> or a reconnect succeeds, and also when this client
        /// leaves a room (<see cref="NetworkState.InRoom"/> to <see cref="NetworkState.Connected"/>).
        /// </summary>
        public event Action OnConnected;

        /// <summary>
        /// Raised when the connection closes, with the reason. Also raised after each failed
        /// reconnect attempt, while later attempts may still follow.
        /// </summary>
        /// <remarks>
        /// <see cref="DisconnectReason"/> describes each reason and whether the reconnect
        /// token survives it (<see cref="CanReconnect"/>).
        /// </remarks>
        public event Action<DisconnectReason> OnDisconnected;

        /// <summary>
        /// Raised when a connection or reconnect attempt fails, with a readable reason: the
        /// attempt timed out (which includes a handshake the server refused and a server key
        /// that does not match the pin), a configuration problem stopped it, or a socket
        /// error ended a <see cref="Connect(string)"/> attempt. <see cref="OnDisconnected"/>
        /// follows.
        /// </summary>
        /// <remarks>
        /// Not raised when the session fails the SDK's validation;
        /// <see cref="OnDisconnected"/> reports that with <see cref="DisconnectReason.Unknown"/>.
        /// </remarks>
        public event Action<string> OnConnectionFailed;

        /// <summary>
        /// Raised when every attempt of a <see cref="Reconnect"/> call has failed, with the
        /// number of attempts made.
        /// </summary>
        /// <remarks>
        /// The manager is <see cref="NetworkState.Disconnected"/> and its session data,
        /// including the reconnect token, is cleared: call <see cref="Connect(string)"/> to
        /// start again. Not raised when an attempt ends with
        /// <see cref="DisconnectReason.Unknown"/>.
        /// </remarks>
        public event Action<int> OnReconnectFailed;

        /// <summary>
        /// Obsolete. Raised when this client enters a room, always with <c>0</c>. Use
        /// <see cref="RoomManager.OnRoomJoined"/> on <see cref="Rooms"/>.
        /// </summary>
        [Obsolete("Use NetworkManager.Rooms.OnRoomJoined or Rooms.OnRoomCreated instead.")]
        public event Action<ulong> OnJoinedRoom;

        /// <summary>
        /// Obsolete. Raised when this client leaves a room, always with <c>0</c>. Use
        /// <see cref="RoomManager.OnRoomLeft"/> on <see cref="Rooms"/>.
        /// </summary>
        [Obsolete("Use NetworkManager.Rooms.OnRoomLeft instead.")]
        public event Action<ulong> OnLeftRoom;

        /// <summary>
        /// Low-level: raised for every inbound data packet and transform-state packet, with
        /// the complete decrypted packet, header included.
        /// </summary>
        /// <remarks>
        /// The SDK itself reads transform state from this event. RPCs and NetworkVariables
        /// do not need it.
        /// </remarks>
        public event Action<byte[]> OnDataReceived;

        /// <summary>
        /// Raised with each new round-trip time, in milliseconds, measured by the heartbeat.
        /// </summary>
        public event Action<float> OnRttUpdated;

        /// <summary>
        /// Low-level: raised each time the server acknowledges a reliable packet this client
        /// sent.
        /// </summary>
        public event Action OnDataAcknowledged;

        /// <summary>
        /// Raised after a successful <see cref="Reconnect"/> when the SDK starts rejoining the
        /// room with this id (<see cref="LastRoomId"/>).
        /// </summary>
        /// <remarks>
        /// The outcome arrives through <see cref="RoomManager.OnRoomJoined"/> or
        /// <see cref="RoomManager.OnRoomError"/>. Not raised when
        /// <see cref="NetworkSettings.autoRejoinLastRoomOnReconnect"/> is off or no last room
        /// is known.
        /// </remarks>
        public event Action<string> OnAutoRejoinAttempt;

    }
}
