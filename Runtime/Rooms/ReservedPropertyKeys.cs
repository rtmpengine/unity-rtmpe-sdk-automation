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
    /// The property keys the SDK reserves. A key that starts with <see cref="Prefix"/> is
    /// reserved: read it freely, and change it through the SDK feature that owns it, such
    /// as <see cref="NetworkSceneManager"/>.
    /// </summary>
    /// <remarks>
    /// A room property write may carry the keys in <see cref="RoomAllowlist"/>; a player
    /// property write may carry no reserved key. A write carrying any other reserved key
    /// throws <see cref="ArgumentException"/> before anything is sent.
    /// </remarks>
    public static class ReservedPropertyKeys
    {
        /// <summary>The prefix every reserved key starts with: <c>__</c>.</summary>
        public const string Prefix = "__";

        /// <summary>
        /// The room property that names the room's scene. <see cref="NetworkSceneManager"/>
        /// writes it.
        /// </summary>
        public const string Scene = "__scene";

        /// <summary>
        /// The room property that holds the scene's load mode: <see langword="true"/> for
        /// additive. Any other value, or no value, means single.
        /// </summary>
        public const string SceneAdditive = "__scene_additive";

        /// <summary>Whether <paramref name="key"/> starts with <see cref="Prefix"/>.</summary>
        public static bool IsReserved(string key) =>
            !string.IsNullOrEmpty(key) && key.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// The reserved keys a room property write may carry: <see cref="Scene"/> and
        /// <see cref="SceneAdditive"/>.
        /// </summary>
        public static readonly ISet<string> RoomAllowlist =
            new HashSet<string>(StringComparer.Ordinal) { Scene, SceneAdditive };

        /// <summary>
        /// The reserved keys a player property write may carry. It is empty: no reserved key
        /// can be written on a player.
        /// </summary>
        public static readonly ISet<string> PlayerAllowlist =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Whether a write may carry <paramref name="key"/>: <see langword="true"/> for a key
        /// that is not reserved, and for a reserved key in <paramref name="allowlist"/>.
        /// </summary>
        /// <param name="key">The property key.</param>
        /// <param name="allowlist">
        /// <see cref="RoomAllowlist"/> or <see cref="PlayerAllowlist"/>.
        /// </param>
        public static bool MayBeWritten(string key, ISet<string> allowlist) =>
            !IsReserved(key) || allowlist.Contains(key);
    }
}
