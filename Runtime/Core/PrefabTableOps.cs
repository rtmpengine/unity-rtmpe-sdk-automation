// RTMPE SDK — Runtime/Core/PrefabTableOps.cs
//
// Operations over the spawn prefab table (prefab id → prefab GameObject).
//
// The prefab table is static game configuration, not per-session state: a
// given id resolves to the same prefab for the lifetime of the application.
// It is therefore carried forward when the session-scoped SpawnManager is
// rebuilt on (re)connect, so a prefab registered once stays registered rather
// than having to be re-registered after every connection. Keeping the carry as
// a free function over plain dictionaries lets it be exercised in isolation,
// independent of the Unity-only SpawnManager.

using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Core
{
    internal static class PrefabTableOps
    {
        /// <summary>
        /// Copy every entry of <paramref name="source"/> into
        /// <paramref name="target"/>, overwriting on id collision and leaving
        /// any entries present only in <paramref name="target"/> untouched.
        /// A null operand is treated as empty.
        /// </summary>
        internal static void CopyInto(
            IReadOnlyDictionary<uint, GameObject> source,
            IDictionary<uint, GameObject> target)
        {
            if (source == null || target == null) return;

            foreach (var entry in source)
                target[entry.Key] = entry.Value;
        }

        /// <summary>
        /// The id <paramref name="table"/> resolves to
        /// <paramref name="prefab"/>, or <see langword="false"/> when no id
        /// does. A null operand is treated as no answer.
        /// </summary>
        /// <remarks>
        /// 🔑 The question is asked of the TABLE, and the reason is that nothing
        /// else can answer it. The table has two writers — the generated
        /// registry and a hand <c>RegisterPrefab</c>, in that order, the second
        /// outranking the first — so the registry asset is a copy of part of it,
        /// and a caller that scans the asset can resolve an id whose prefab the
        /// table no longer holds. That caller then spawns somebody else's
        /// object, on every client, with nothing logged anywhere: the id IS
        /// registered, so the spawn path has nothing to refuse.
        /// <para>
        /// ⛔ The lowest id wins where several name one prefab, so two clients
        /// asking the same question of the same table get the same answer. That
        /// is an alias rather than a conflict — every one of those ids spawns
        /// the prefab that was asked about — but an answer that depends on
        /// enumeration order is a different answer per run.
        /// </para>
        /// <para>
        /// ⚠️ The limit of that guarantee, stated because the sentence above is
        /// easy to read as more: it is the same TABLE that gives the same
        /// answer. Two clients holding different tables — one with content the
        /// other has not downloaded, registering an alias at a lower id — send
        /// different ids for one prefab whatever the tie-break is, and the
        /// answer to that is one registry per build, not a rule here.
        /// </para>
        /// <para>
        /// ⚠️ The parameter is the concrete dictionary, not an interface: the
        /// interface makes <c>foreach</c> box the struct enumerator, which is
        /// 56 bytes on the Unity main thread every time a caller resolves an id.
        /// </para>
        /// </remarks>
        internal static bool TryFindId(
            Dictionary<uint, GameObject> table, GameObject prefab, out uint prefabId)
        {
            prefabId = 0;
            if (table == null || prefab == null) return false;

            bool found = false;
            foreach (var entry in table)
            {
                // Unity's operator, deliberately: what is being asked is whether
                // this row still names the asset the caller holds, and only the
                // engine's comparison answers that for a destroyed object. It
                // compares instance ids rather than references, so a row the
                // editor has destroyed simply fails to match — it does not
                // "read as null", which is what an earlier version of this
                // sentence claimed and is true only of a comparison AGAINST
                // null, one line above.
                if (entry.Value != prefab) continue;
                if (found && entry.Key >= prefabId) continue;

                prefabId = entry.Key;
                found    = true;
            }

            return found;
        }

        /// <summary>
        /// Load the rows of a generated prefab registry into
        /// <paramref name="target"/>, reporting what could not be taken at face
        /// value. A null operand is treated as empty.
        /// </summary>
        /// <remarks>
        /// ⛔ Tolerant by design, and the reason is asymmetric: a row that names
        /// no prefab costs the project one id it cannot spawn, while refusing
        /// the whole registry over it costs the project every id. So a bad row
        /// is counted and stepped over, and the count is what the caller
        /// reports.
        /// <para>
        /// A row whose id is already present overwrites it and is counted
        /// separately — the same policy <c>SpawnManager.RegisterPrefab</c> has
        /// always had for a hand-made registration, so the SDK has one answer to
        /// the question rather than two.
        /// </para>
        /// <para>
        /// ⚠️ <c>prefab == null</c> is Unity's overload in a player and
        /// reference equality in a test project, and the difference does not
        /// matter to the rule: what is being asked is whether the row resolved
        /// to something, and both answer that. It is the reason a row pointing
        /// at a DELETED asset — Unity's "fake null" — is caught here at all.
        /// </para>
        /// </remarks>
        internal static PrefabRegistryLoad LoadInto(
            IReadOnlyList<NetworkPrefabEntry> entries,
            IDictionary<uint, GameObject> target)
        {
            if (entries == null || target == null) return default;

            int loaded = 0, unresolved = 0, duplicated = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                // ⚠️ Both halves, and only the second is reachable from the
                // Inspector — its list editor adds a constructed element, never
                // a hole. An empty row comes from the other two ways a
                // serialised asset is written: a hand edit of the YAML, and a
                // caller handing SetEntries a list it built itself.
                if (entry == null || entry.prefab == null)
                {
                    unresolved++;
                    continue;
                }

                if (target.ContainsKey(entry.id)) duplicated++;
                else                              loaded++;
                target[entry.id] = entry.prefab;
            }

            return new PrefabRegistryLoad(loaded, unresolved, duplicated);
        }

        /// <summary>
        /// What to tell the console about a load, or <see langword="null"/> when
        /// there is nothing to say.
        /// </summary>
        /// <remarks>
        /// Here rather than at the call site, and the reason is reachability
        /// rather than tidiness: no project in this repository compiles
        /// <c>SpawnManager</c>, so a message composed there is a message no test
        /// can read — including the rule that it stays silent on a clean load.
        /// <para>
        /// Only the clauses that happened. A line reading "1 row(s) name no
        /// prefab, 0 row(s) reused an id" makes the reader check the half that
        /// did not occur.
        /// </para>
        /// </remarks>
        internal static string DescribeLoad(string registryName, PrefabRegistryLoad outcome)
        {
            if (outcome.IsClean) return null;

            var faults = new List<string>(2);
            if (outcome.Unresolved > 0)
                faults.Add(outcome.Unresolved + " row(s) resolved to no prefab and were skipped");
            if (outcome.Duplicated > 0)
                faults.Add(outcome.Duplicated + " row(s) reused an id already taken and overwrote it");

            return "[SpawnManager] prefab registry '"
                + (string.IsNullOrEmpty(registryName) ? "(unnamed)" : registryName)
                + "': loaded " + outcome.Loaded + " prefab(s); " + string.Join("; ", faults)
                + ". Regenerate it from Window → RTMPE → Network Prefabs.";
        }
    }

    /// <summary>
    /// What one registry load turned out to be. Counts rather than a boolean:
    /// the caller reports the two abnormal outcomes and continues either way, so
    /// "did it work" is not a question with an answer here.
    /// </summary>
    internal readonly struct PrefabRegistryLoad
    {
        internal PrefabRegistryLoad(int loaded, int unresolved, int duplicated)
        {
            Loaded     = loaded;
            Unresolved = unresolved;
            Duplicated = duplicated;
        }

        /// <summary>Rows that resolved and took an id nothing else held.</summary>
        internal int Loaded { get; }

        /// <summary>Rows naming no prefab — a deleted asset, or an empty element.</summary>
        internal int Unresolved { get; }

        /// <summary>Rows that resolved onto an id already taken, overwriting it.</summary>
        internal int Duplicated { get; }

        /// <summary>True when every row was taken at face value.</summary>
        internal bool IsClean => Unresolved == 0 && Duplicated == 0;
    }
}
