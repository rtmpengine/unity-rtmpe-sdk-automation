// RTMPE SDK — Runtime/Core/NetworkPrefabRegistry.cs
//
// The asset that carries the Editor's prefab ledger into the player.
//
// The ledger itself lives in the Editor and knows GUIDs; a build does not have
// one, and a GUID resolves to nothing at runtime. Until this asset existed the
// crossing was made by hand — a RegisterPrefab call per prefab, written by the
// integrator, in a place they had to remember to reach before spawning — and a
// missed call is a spawn that fails on every client but the one that made it.
//
// ⚠️ Every prefab named here is a HARD reference, so every one of them enters
// every build whether or not the game ever spawns it. That is the same trade
// Unity's own NetworkPrefabsList makes, and it is stated here and in the
// documentation rather than left to be discovered in a build report.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// A generated table of prefab id → prefab, loaded into the spawn manager
    /// when a session is built. Assign it to
    /// <see cref="NetworkSettings.prefabRegistry"/>; the Editor's
    /// <c>Window → RTMPE → Network Prefabs</c> writes it alongside the
    /// <c>RtmpePrefabIds</c> constants, from the same button and the same ledger.
    /// </summary>
    /// <remarks>
    /// A hand edit survives until the next generation, which replaces every row.
    /// Nothing about it is policed: a row whose prefab is empty, and a row
    /// reusing an id another row already took, are both reported and applied
    /// rather than refused — one unspawnable id is not a reason to leave a
    /// project unable to spawn anything.
    /// <para>
    /// ⛔ What the asset cannot do is ALLOCATE an id. That stays the Editor
    /// ledger's, which is where a project's peers and its constants agree; an
    /// id typed in here is a claim the ledger has not made, and the next
    /// generation removes it.
    /// </para>
    /// </remarks>
    [CreateAssetMenu(
        fileName = "RtmpePrefabRegistry",
        menuName = "RTMPE/Prefab Registry",
        order    = 2)]
    public sealed class NetworkPrefabRegistry : ScriptableObject, ISerializationCallbackReceiver
    {
        [SerializeField]
        [Tooltip("Prefab id → prefab, generated from the Editor ledger. " +
                 "Every prefab listed here is pulled into the build.")]
        private List<NetworkPrefabEntry> entries = new List<NetworkPrefabEntry>();

        /// <summary>
        /// The rows this asset carries, in the order they were written. Never
        /// null; individual rows may be, and are reported rather than refused.
        /// </summary>
        /// <remarks>
        /// The null coalesce is defence rather than a claim about a mechanism:
        /// the field is serialised, so what it holds on load is decided by the
        /// asset's own YAML and by whatever wrote it — a hand edit that dropped
        /// the key, a type that changed shape. Answering an empty list there
        /// costs nothing; answering null makes <c>foreach</c> throw in a player
        /// and nowhere else.
        /// <para>
        /// ⛔ A wrapper rather than the list itself.  <c>IReadOnlyList</c> is a
        /// statement about the interface, not about the object behind it, and a
        /// caller that casts back to <c>List&lt;T&gt;</c> was editing the
        /// asset's own rows at runtime — in the Editor that is a change written
        /// to disk on the next save.  The wrapper is built once per registry and
        /// reused, so the read stays free on the path that matters: the load
        /// runs on every connect.
        /// </para>
        /// </remarks>
        public IReadOnlyList<NetworkPrefabEntry> Entries
            => _readOnly ?? (_readOnly = new ReadOnlyCollection<NetworkPrefabEntry>(
                   entries ?? (entries = new List<NetworkPrefabEntry>())));

        [NonSerialized]
        private ReadOnlyCollection<NetworkPrefabEntry> _readOnly;

        /// <summary>
        /// Replace every row. The generator's entry point — the Editor
        /// assembly reaches it through <c>InternalsVisibleTo</c> rather than
        /// through a serialised-property edit, so the shape written is the
        /// shape this type declares and not a hand-built copy of it.
        /// </summary>
        internal void SetEntries(List<NetworkPrefabEntry> replacement)
        {
            entries   = replacement ?? new List<NetworkPrefabEntry>();
            // The wrapper wraps the list that was there; a replacement leaves it
            // describing rows this registry no longer holds.
            _readOnly = null;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        /// <remarks>
        /// 🔑 <see cref="SetEntries"/> is not the only writer of the field it
        /// clears. Unity assigns a fresh list into a serialised field whenever
        /// it deserialises the object — a re-import, an undo, a revert — and
        /// none of those goes through this type at all, so the wrapper was left
        /// describing rows the asset no longer holds and every reader that took
        /// <see cref="Entries"/> afterwards got the old ones.
        /// <para>
        /// ⚠️ A field assignment and nothing else: this runs on a loading thread,
        /// where the Unity API is not available.
        /// </para>
        /// </remarks>
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            _readOnly = null;
        }
    }
}
