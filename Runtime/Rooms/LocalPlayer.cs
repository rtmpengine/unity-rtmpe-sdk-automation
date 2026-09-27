// RTMPE SDK — Runtime/Rooms/LocalPlayer.cs
//
// Convenience wrapper around RoomManager.SetPlayerProperties that scopes
// the operation to the local player (the authenticated session's owner).
// Matches the Photon-style `NetworkManager.LocalPlayer.SetProperty(...)` API
// so the SDK reads naturally for developers migrating from Photon PUN.
//
// Obtain an instance via `NetworkManager.LocalPlayer` — constructed lazily
// by NetworkManager once the session is established (LocalPlayerStringId
// is non-empty).  Calls made before a session exists log an error and
// become no-ops.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Writes this client's player properties without repeating its player id. Get it from
    /// <see cref="RTMPE.Core.NetworkManager.LocalPlayer"/>.
    /// </summary>
    /// <remarks>
    /// You can keep the reference: every call goes to the current <see cref="RoomManager"/>,
    /// including after a reconnect.
    /// </remarks>
    public sealed class LocalPlayerContext
    {
        // Resolved on every call so a Reconnect-driven
        // RecreateRoomAndSpawnManagers swap is observed without re-allocating
        // the public-facing context object — the application caches the
        // result of NetworkManager.LocalPlayer indefinitely (Photon-compatible
        // long-lived reference) so a stale captured RoomManager would route
        // every property write to a defunct PacketBuilder + dead lambdas.
        private readonly Func<RoomManager> _roomsProvider;
        private readonly Func<string>      _getLocalPlayerId;

        // Snapshot of the RoomManager passed at construction time.  Retained
        // ONLY for back-compat with the prior fixed-reference constructor —
        // when callers use the new provider constructor this field is null
        // and every method routes through _roomsProvider().
        private readonly RoomManager _roomsFixed;

        internal LocalPlayerContext(Func<RoomManager> roomsProvider, Func<string> getLocalPlayerId)
        {
            _roomsProvider    = roomsProvider    ?? throw new ArgumentNullException(nameof(roomsProvider));
            _getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
        }

        // Legacy constructor preserved so existing test fixtures and any
        // out-of-tree callers that constructed with a fixed RoomManager
        // continue to compile.  New code MUST use the Func<RoomManager>
        // overload — capturing a single RoomManager instance breaks across
        // reconnects, where the manager is recreated and a stale capture
        // would silently route every call to the dead instance.
        internal LocalPlayerContext(RoomManager rooms, Func<string> getLocalPlayerId)
        {
            _roomsFixed       = rooms ?? throw new ArgumentNullException(nameof(rooms));
            _getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
        }

        // Resolve the live RoomManager.  When the provider returns null
        // (transport not yet up) callers fall through to the no-session-yet
        // log so the application sees an actionable warning rather than a
        // NullReferenceException.
        private RoomManager Rooms => _roomsProvider != null ? _roomsProvider() : _roomsFixed;

        /// <summary>
        /// This client's player id in its current room
        /// (<see cref="RTMPE.Core.NetworkManager.LocalPlayerStringId"/>), or an empty string
        /// outside a room.
        /// </summary>
        public string PlayerId => _getLocalPlayerId() ?? string.Empty;

        /// <summary>
        /// Writes one of this client's player properties through
        /// <see cref="RoomManager.SetPlayerProperties"/>.
        /// <see cref="RoomManager.OnPlayerPropertiesChanged"/> is raised on every client when
        /// the server applies it.
        /// </summary>
        /// <remarks>
        /// Outside a room this client has no player id: the call logs an error and sends
        /// nothing. To write several keys, use <see cref="SetProperties"/>: a write sent before
        /// the previous one is applied is refused.
        /// </remarks>
        /// <param name="key">The property key.</param>
        /// <param name="value">The value, or <see cref="PropertyValue.Deletion"/> to remove the
        /// key.</param>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty, or the
        /// write breaks a property rule.</exception>
        public void SetProperty(string key, PropertyValue value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key must not be null or empty.", nameof(key));

            string playerId = PlayerId;
            if (string.IsNullOrEmpty(playerId))
            {
                Debug.LogError(
                    "[RTMPE] LocalPlayer.SetProperty: no authenticated session yet — call after handshake completes.");
                return;
            }

            var rooms = Rooms;
            if (rooms == null)
            {
                Debug.LogError(
                    "[RTMPE] LocalPlayer.SetProperty: no RoomManager available — connection state is invalid.");
                return;
            }
            rooms.SetPlayerProperties(
                playerId,
                new Dictionary<string, PropertyValue> { { key, value } });
        }

        /// <summary>
        /// Writes several of this client's player properties in one request, through
        /// <see cref="RoomManager.SetPlayerProperties"/>.
        /// </summary>
        /// <remarks>
        /// Prefer it to several <see cref="SetProperty"/> calls in a row: the keys are written
        /// together, and each client raises one
        /// <see cref="RoomManager.OnPlayerPropertiesChanged"/>. Outside a room the call logs an
        /// error and sends nothing.
        /// </remarks>
        /// <param name="properties">The properties to write.</param>
        /// <exception cref="ArgumentNullException"><paramref name="properties"/> is
        /// <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The write breaks a property rule.</exception>
        public void SetProperties(IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            string playerId = PlayerId;
            if (string.IsNullOrEmpty(playerId))
            {
                Debug.LogError(
                    "[RTMPE] LocalPlayer.SetProperties: no authenticated session yet — call after handshake completes.");
                return;
            }
            var rooms = Rooms;
            if (rooms == null)
            {
                Debug.LogError(
                    "[RTMPE] LocalPlayer.SetProperties: no RoomManager available — connection state is invalid.");
                return;
            }
            rooms.SetPlayerProperties(playerId, properties);
        }
    }
}
