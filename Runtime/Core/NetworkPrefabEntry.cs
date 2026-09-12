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
    /// A prefab id and the prefab it names, as stored in a
    /// <see cref="NetworkPrefabRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Public fields rather than properties, because Unity serialises fields:
    /// this type exists to be written into an asset and read back out of one.
    /// </remarks>
    [Serializable]
    public sealed class NetworkPrefabEntry
    {
        /// <summary>
        /// The id every peer in the session uses for this prefab. Allocated by
        /// the Editor ledger and surfaced to application code as a constant on
        /// the generated <c>RtmpePrefabIds</c>.
        /// </summary>
        public uint id;

        /// <summary>
        /// The prefab the id resolves to in this project. A hard reference, so
        /// every registered prefab is pulled into the build.
        /// </summary>
        public GameObject prefab;

        /// <summary>Required by Unity's serialiser and by the Inspector's list editor.</summary>
        public NetworkPrefabEntry()
        {
        }

        /// <summary>Construct a row directly, for a generator or a test.</summary>
        public NetworkPrefabEntry(uint id, GameObject prefab)
        {
            this.id     = id;
            this.prefab = prefab;
        }
    }
}
