using System;
using System.Collections.Generic;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The deterministic ground-truth surface that resolves a proposed set of
    /// RPCs into method ids and validates them against the SDK's id rules. It
    /// computes ids and reports collisions; it never issues, mutates, or emits
    /// anything, so a caller may propose a plan but cannot self-assign an
    /// identity.
    /// </summary>
    public static class PlanGuard
    {
        /// <summary>
        /// Resolves every entry in <paramref name="plan"/> to its FNV-1a method
        /// id and refuses any that collides with a reserved id or duplicates an
        /// earlier id on the same type. Collisions are aggregated, not
        /// fail-fast; the verdict is accepted only when none are found.
        /// </summary>
        public static PlanVerdict ResolveAndValidatePlan(IReadOnlyList<PlannedRpc> plan)
            => ResolveAndValidatePlan(plan, ReservedRpcIds.IsReserved);

        // Reserved membership is taken as a predicate so the classification
        // logic can be exercised in isolation: a real hash effectively never
        // lands on the canonical reserved constants, so the wiring is otherwise
        // unreachable from a test.
        internal static PlanVerdict ResolveAndValidatePlan(
            IReadOnlyList<PlannedRpc> plan, Func<uint, bool> isReserved)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var ids = new List<IdEntry>(plan.Count);
            var collisions = new List<Collision>();

            // Intra-type duplicates are scoped per type: an id collides only
            // with an earlier method on the same type, matching the runtime
            // validator's per-type discovery.
            var seenByType = new Dictionary<string, Dictionary<uint, string>>();

            foreach (var entry in plan)
            {
                uint id = Fnv1a.ComputeMethodId(entry.TypeName, entry.MethodName);
                ids.Add(new IdEntry(entry.TypeName, entry.MethodName, id));

                // Reserved is tested before the intra-type duplicate so a method
                // that is both is reported as reserved (the stronger fault).
                if (isReserved(id))
                {
                    collisions.Add(new Collision(
                        CollisionKind.Reserved, entry.TypeName, entry.MethodName, id, null));
                    continue;
                }

                if (!seenByType.TryGetValue(entry.TypeName, out var seen))
                {
                    seen = new Dictionary<uint, string>();
                    seenByType[entry.TypeName] = seen;
                }

                if (seen.TryGetValue(id, out var prior))
                {
                    collisions.Add(new Collision(
                        CollisionKind.IntraType, entry.TypeName, entry.MethodName, id, prior));
                    continue;
                }

                seen[id] = entry.MethodName;
            }

            return new PlanVerdict(collisions.Count == 0, ids, collisions);
        }
    }
}
