// RTMPE SDK — Runtime/Core/NetworkPrefabEntry.cs
//
// One row of the spawn registry: the id a peer puts on the wire, and the prefab
// this project resolves it to.
//
// Its own file, and depending on nothing but GameObject, because the rule that
// reads it depends on nothing more either. The loading contract is exercised
// against plain rows in a test project where neither ScriptableObject nor
// SpawnManager can be constructed — which is every project in this repository,
// none of which compiles either type.

using System;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// A prefab id and the prefab it names: one row of a
    /// <see cref="NetworkPrefabRegistry"/>.
    /// </summary>
    [Serializable]
    public sealed class NetworkPrefabEntry
    {
        /// <summary>
        /// The id every client in the session uses for this prefab. The Network
        /// Prefabs window allocates it and exposes it as a constant on the
        /// generated <c>RtmpePrefabIds</c> class.
        /// </summary>
        public uint id;

        /// <summary>
        /// The prefab the id stands for. Every prefab in a registry is included
        /// in the build.
        /// </summary>
        public GameObject prefab;

        /// <summary>Creates an empty row.</summary>
        public NetworkPrefabEntry()
        {
        }

        /// <summary>Creates a row for <paramref name="prefab"/> under <paramref name="id"/>.</summary>
        /// <param name="id">The prefab id.</param>
        /// <param name="prefab">The prefab.</param>
        public NetworkPrefabEntry(uint id, GameObject prefab)
        {
            this.id     = id;
            this.prefab = prefab;
        }
    }
}
