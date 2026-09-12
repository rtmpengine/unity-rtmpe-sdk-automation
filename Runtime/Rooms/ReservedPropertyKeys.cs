// RTMPE SDK — Runtime/Rooms/ReservedPropertyKeys.cs
//
// Well-known reserved keys for the room's custom_properties map.  All
// reserved keys share the "__" prefix.  The server rejects any write from a
// client to a reserved key that is not in the allowlist defined here, so
// keep this file in sync with the Go-side ReservedRoomPropertyAllowlist
// (modules/room/domain/entities/properties.go).

using System;
using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Reserved property keys managed by the RTMPE SDK.  Constants are
    /// exposed so application code can read them when needed (for example,
    /// to check whether a <c>RoomInfo.Properties</c> value is the
    /// authoritative scene), but direct writes are discouraged — use the
    /// dedicated helper classes (<c>NetworkSceneManager</c>, etc.) instead.
    /// </summary>
    public static class ReservedPropertyKeys
    {
        /// <summary>Prefix marking a key as belonging to the server-managed namespace.</summary>
        public const string Prefix = "__";

        /// <summary>Reserved key carrying the room's authoritative scene name.</summary>
        public const string Scene = "__scene";

        /// <summary>
        /// Reserved key flagging the scene as loaded via
        /// <c>LoadSceneMode.Additive</c>.  Boolean value; absence implies
        /// <c>LoadSceneMode.Single</c>.
        /// </summary>
        public const string SceneAdditive = "__scene_additive";

        /// <summary>True when <paramref name="key"/> uses the reserved prefix.</summary>
        public static bool IsReserved(string key) =>
            !string.IsNullOrEmpty(key) && key.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// The reserved keys a client may write on a ROOM.  Mirrors
        /// <c>entities.ReservedRoomPropertyAllowlist</c>; the server rejects
        /// every other <c>__</c>-prefixed key with <c>ErrReservedPropertyKey</c>.
        /// </summary>
        public static readonly ISet<string> RoomAllowlist =
            new HashSet<string>(StringComparer.Ordinal) { Scene, SceneAdditive };

        /// <summary>
        /// The reserved keys a client may write on a PLAYER.  Mirrors
        /// <c>entities.ReservedPlayerPropertyAllowlist</c>, which is empty — no
        /// reserved key is writable on a player at all.
        /// </summary>
        public static readonly ISet<string> PlayerAllowlist =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// True when <paramref name="key"/> may be written by a client against
        /// the entry point whose <paramref name="allowlist"/> is supplied.
        /// </summary>
        public static bool MayBeWritten(string key, ISet<string> allowlist) =>
            !IsReserved(key) || allowlist.Contains(key);
    }
}
