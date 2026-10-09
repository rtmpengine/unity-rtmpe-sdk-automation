// RTMPE SDK — Runtime/Rooms/RoomInfo.cs
//
// Immutable snapshot of a room's state.
// Mirrors the GetRoomResponse / RoomSummary messages in room.proto.

using System;
using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// A read-only snapshot of a room, received in <see cref="RoomManager.OnRoomCreated"/>,
    /// <see cref="RoomManager.OnRoomJoined"/>, <see cref="RoomManager.OnRoomPropertiesChanged"/>
    /// and <see cref="RoomManager.OnRoomListReceived"/>.
    /// </summary>
    public sealed class RoomInfo
    {
        /// <summary>The room id. Pass it to <see cref="RoomManager.JoinRoom"/>.</summary>
        public string RoomId { get; }

        /// <summary>
        /// The room's six-character join code. Pass it to <see cref="RoomManager.JoinRoomByCode"/>.
        /// </summary>
        public string RoomCode { get; }

        /// <summary>The room's display name.</summary>
        public string Name { get; }

        /// <summary>The room's state: <c>"waiting"</c>, <c>"playing"</c> or <c>"finished"</c>.</summary>
        public string State { get; }

        /// <summary>The number of players in the room.</summary>
        public int PlayerCount { get; }

        /// <summary>The room's capacity, from 1 to 100.</summary>
        public int MaxPlayers { get; }

        /// <summary>Whether the room appears in public room lists.</summary>
        public bool IsPublic { get; }

        /// <summary>The players in the room. May be empty in a room list.</summary>
        public PlayerInfo[] Players { get; }

        /// <summary>
        /// The room's properties. Never <see langword="null"/>; empty when none are set.
        /// Treat the map as read-only.
        /// </summary>
        public IReadOnlyDictionary<string, PropertyValue> Properties { get; }

        /// <summary>
        /// Increases with every room property write the server applies. A write names the
        /// version it expects to create: this value plus one.
        /// </summary>
        public int PropertiesVersion { get; }

        /// <summary>
        /// The host's player id, read from <see cref="Players"/>, or an empty string when no
        /// player is marked as host.
        /// </summary>
        public string MasterId
        {
            get
            {
                var players = Players;
                if (players == null) return string.Empty;
                for (int i = 0; i < players.Length; i++)
                {
                    var p = players[i];
                    if (p != null && p.IsHost) return p.PlayerId;
                }
                return string.Empty;
            }
        }

        /// <summary>
        /// The room's scene: the value of the reserved <c>__scene</c> property, or an empty
        /// string when none is set. The host changes it with
        /// <see cref="NetworkSceneManager.LoadScene"/>.
        /// </summary>
        public string CurrentScene
        {
            get
            {
                if (Properties != null
                    && Properties.TryGetValue(ReservedPropertyKeys.Scene, out var v)
                    && v.Type == PropertyType.String)
                {
                    return v.AsString();
                }
                return string.Empty;
            }
        }

        /// <summary>
        /// Creates a snapshot. The SDK builds these from what the server sends; the
        /// constructor is for tests.
        /// </summary>
        public RoomInfo(
            string roomId,
            string roomCode,
            string name,
            string state,
            int    playerCount,
            int    maxPlayers,
            bool   isPublic,
            PlayerInfo[] players = null,
            IReadOnlyDictionary<string, PropertyValue> properties = null,
            int    propertiesVersion = 0)
        {
            RoomId            = roomId ?? string.Empty;
            RoomCode          = roomCode ?? string.Empty;
            Name              = name ?? string.Empty;
            State             = state ?? string.Empty;
            PlayerCount       = playerCount;
            MaxPlayers        = maxPlayers;
            IsPublic          = isPublic;
            // Defensive copy of the player roster.  The constructor caller
            // (RoomManager / RoomPacketParser) builds the array imperatively
            // during parse; without this copy a future caller that re-uses
            // its scratch array across packets would silently mutate the
            // RoomInfo snapshot — and the snapshot is supposed to be
            // immutable for its entire lifetime.  An empty roster reuses
            // the canonical Array.Empty&lt;T&gt;() singleton to avoid the copy.
            Players           = (players != null && players.Length > 0)
                ? (PlayerInfo[])players.Clone()
                : Array.Empty<PlayerInfo>();
            // Defensive copy: an IReadOnlyDictionary surface does not prevent
            // the caller from holding a reference to the underlying mutable
            // Dictionary and mutating it after construction.  Copying here
            // guarantees the snapshot is truly immutable for the SDK's
            // lifetime contract.  Skipped for the canonical EmptyProperties
            // singleton and for already-copied readonly dictionaries to avoid
            // redundant allocation.
            Properties        = FreezeProperties(properties);
            PropertiesVersion = propertiesVersion;
        }

        /// <summary>
        /// Returns an immutable snapshot of <paramref name="source"/>.  Reuses
        /// the shared empty singleton when the input is null or empty, and
        /// defensively copies all other inputs so the returned reference is
        /// safe against external mutation of the caller's dictionary.
        /// </summary>
        internal static IReadOnlyDictionary<string, PropertyValue> FreezeProperties(
            IReadOnlyDictionary<string, PropertyValue> source)
        {
            if (source == null || source.Count == 0) return EmptyProperties;
            var copy = new Dictionary<string, PropertyValue>(source.Count);
            foreach (var kv in source) copy[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>
        /// Merge a property delta onto a baseline map, producing the snapshot a
        /// <c>room_properties_updated</c> or <c>player_properties_updated</c>
        /// broadcast leaves behind.  Shared by <see cref="RoomInfo"/> and
        /// <see cref="PlayerInfo"/> so the two never answer it differently.
        /// </summary>
        /// <remarks>
        /// The wire carries the DELTA the writer sent, not the resulting
        /// snapshot: the server merges the request into the stored map
        /// (<c>Room.ApplyRoomProperties</c>) and then broadcasts the request
        /// verbatim.  A receiver that replaces its map with the broadcast keeps
        /// only the keys of the most recent write, so every key another client
        /// ever set disappears from this client's view while remaining live on
        /// the server — and the divergence is permanent, because nothing
        /// re-sends a full snapshot.
        ///
        /// A DELETION is part of that delta: the server removes a key whose
        /// value carries an empty type tag (<c>Room.ApplyRoomProperties</c>),
        /// and re-broadcasts it in the same shape.  This merge consumes it —
        /// <see cref="PropertyType.Deleted"/> removes the key rather than
        /// being stored under it — so a deleted key leaves the local map the
        /// same way it leaves the server's, and the count stays in step.
        ///
        /// 🔑 <b>The empty-baseline fast path is not an optimisation once
        /// deletions exist.</b>  Returning the delta unchanged when there is
        /// nothing to merge onto would publish the sentinel itself as a stored
        /// value — a key whose value is "delete me", visible to every caller
        /// reading <see cref="Properties"/>.  The path is therefore taken only
        /// for a delta that carries no deletion.
        /// </remarks>
        internal static IReadOnlyDictionary<string, PropertyValue> MergeProperties(
            IReadOnlyDictionary<string, PropertyValue> baseline,
            IReadOnlyDictionary<string, PropertyValue> delta)
        {
            if (delta == null || delta.Count == 0) return baseline;

            bool carriesDeletion = false;
            foreach (var kv in delta)
            {
                if (kv.Value.IsDeletion) { carriesDeletion = true; break; }
            }

            if ((baseline == null || baseline.Count == 0) && !carriesDeletion) return delta;

            int baselineCount = baseline?.Count ?? 0;
            var merged = new Dictionary<string, PropertyValue>(baselineCount + delta.Count);
            if (baseline != null)
            {
                foreach (var kv in baseline) merged[kv.Key] = kv.Value;
            }
            foreach (var kv in delta)
            {
                if (kv.Value.IsDeletion) merged.Remove(kv.Key);
                else                     merged[kv.Key] = kv.Value;
            }
            return merged;
        }

        /// <summary>
        /// Returns a copy of this snapshot with <paramref name="properties"/> and
        /// <paramref name="version"/> in place of the current ones.
        /// </summary>
        /// <remarks>
        /// The map replaces the current properties whole; it is not merged into them.
        /// </remarks>
        public RoomInfo WithProperties(IReadOnlyDictionary<string, PropertyValue> properties, int version)
            => new RoomInfo(
                RoomId, RoomCode, Name, State, PlayerCount, MaxPlayers,
                IsPublic, Players, properties, version);

        /// <summary>
        /// Returns a copy of this snapshot with <paramref name="players"/> as the roster and
        /// <see cref="PlayerCount"/> unchanged.
        /// </summary>
        public RoomInfo WithPlayers(PlayerInfo[] players)
            => new RoomInfo(
                RoomId, RoomCode, Name, State, PlayerCount, MaxPlayers,
                IsPublic, players, Properties, PropertiesVersion);

        /// <summary>
        /// Returns a copy of this snapshot with <paramref name="players"/> as the roster and
        /// <see cref="PlayerCount"/> set to its length.
        /// </summary>
        public RoomInfo WithRoster(PlayerInfo[] players)
            => new RoomInfo(
                RoomId, RoomCode, Name, State,
                players?.Length ?? 0, MaxPlayers,
                IsPublic, players, Properties, PropertiesVersion);

        private static readonly IReadOnlyDictionary<string, PropertyValue> EmptyProperties
            = new Dictionary<string, PropertyValue>(0);

        public override string ToString()
            => $"Room({RoomId}, \"{Name}\", {PlayerCount}/{MaxPlayers}, {State})";
    }
}
