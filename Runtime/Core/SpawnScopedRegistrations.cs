// RTMPE SDK — Runtime/Core/SpawnScopedRegistrations.cs
//
// A networked component's self-registering collections hold entries of several
// lifetimes. A field initialiser or a constructor runs once, and its
// registration is valid for every life the object has. A Start or a lazy
// Update runs once too, but later. What OnNetworkSpawn registers is re-created
// on the next acquire, because the pool re-runs that callback on the same
// instance — so those entries, and only those, have to go when the life does.
//
// Nothing in a list distinguishes them, so the span the callback occupies is
// recorded around the call and released as a block: entries before it and
// entries after it both belong to the object rather than to the spawn, and a
// block removal shifts the later ones down intact. Expressed over List<T>
// rather than the Unity-only component so it can be exercised in isolation.
//
// ⚠ OnNetworkSpawn is not the only callback a pooled acquire re-runs —
// re-activating the GameObject re-runs OnEnable too, and a registration made
// there lands after the previous release and before the next mark, outside
// every span. That collides on the second life exactly as this mechanism
// exists to prevent, which is why the analyzer (RTMPE1011) and the
// documentation both put construction in OnNetworkSpawn: this bracket holds
// the callback the SDK owns, not every place a variable can be built.

using System.Collections.Generic;

namespace RTMPE.Core
{
    internal static class SpawnScopedRegistrations
    {
        /// <summary>
        /// Where the spawn callback's own registrations will begin. Take this
        /// immediately before the callback runs.
        /// </summary>
        internal static int Mark<T>(IReadOnlyList<T> registrations)
            => registrations?.Count ?? 0;

        /// <summary>
        /// How many registrations the callback added, given the
        /// <paramref name="mark"/> taken before it. Take this immediately after
        /// it returns — including when it throws, or the count describes a life
        /// that has already ended.
        /// </summary>
        internal static int SpanSince<T>(IReadOnlyList<T> registrations, int mark)
        {
            int count = (registrations?.Count ?? 0) - mark;
            return count > 0 ? count : 0;
        }

        /// <summary>
        /// Drop the <paramref name="count"/> registrations beginning at
        /// <paramref name="mark"/>, leaving everything on either side of them in
        /// place and in order.
        /// </summary>
        /// <remarks>
        /// A block rather than a tail: entries added after the callback belong
        /// to the object and are not re-created when it is acquired again, so
        /// releasing to the end of the list would unregister them permanently.
        /// The block is contiguous because the list only ever grows at the end
        /// and the callback holds the thread while it runs — a registration
        /// made into <em>another</em> component's list from inside it lands in
        /// that component's list outside any span, and is a leak rather than a
        /// misplaced removal.
        /// <para>A span outside the list is treated as "nothing to release"
        /// rather than as an error: this runs on the teardown path, and a throw
        /// here would strand the rest of it over a discrepancy that costs one
        /// stale entry.</para>
        /// </remarks>
        internal static void ReleaseSpan<T>(List<T> registrations, int mark, int count)
        {
            if (registrations == null || mark < 0 || count <= 0) return;
            if (mark >= registrations.Count) return;

            int available = registrations.Count - mark;
            registrations.RemoveRange(mark, count < available ? count : available);
        }
    }
}
