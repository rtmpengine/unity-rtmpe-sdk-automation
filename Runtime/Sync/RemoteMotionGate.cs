// RTMPE SDK — Runtime/Sync/RemoteMotionGate.cs
//
// Plausibility gate for inbound position snapshots of non-owner networked
// objects.
//
// A non-owner object is driven entirely by snapshots the receiver decodes
// from the network; the receiver has no authority over the motion and no
// way to re-derive it.  Without a bound, a peer that streams arbitrary
// coordinates makes its replica appear to teleport across the world on every
// other client's screen.  This helper bounds the position step between two
// consecutive snapshots to the displacement reachable at a configured speed
// ceiling, mirroring the per-update displacement cap NetworkRigidbody already
// applies on its remote-state path.
//
// The logic is a pure function — no Unity scene, no component instance — so
// the bound can be exercised directly under the headless test runner.

using System;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Pure helpers that bound a remote-object position step to a physically
    /// plausible displacement.
    /// </summary>
    internal static class RemoteMotionGate
    {
        /// <summary>
        /// Clamp <paramref name="candidate"/> to the displacement reachable
        /// from <paramref name="previous"/> within the elapsed interval.
        ///
        /// <para>The reachable displacement is
        /// <c>maxSpeed × dt + stepFloor</c>: the speed term scales with the
        /// real interval between snapshots, and the floor absorbs
        /// quantization noise and the degenerate case of two snapshots that
        /// resolve to the same timestamp (<c>dt ≤ 0</c>).  A candidate within
        /// that budget is returned unchanged; a candidate beyond it is pulled
        /// back along the line to <paramref name="previous"/> so the replica
        /// advances at the ceiling instead of snapping to the claimed point.</para>
        /// </summary>
        /// <param name="previous">Last accepted position.</param>
        /// <param name="candidate">Inbound position to validate.</param>
        /// <param name="dtSeconds">
        /// Seconds elapsed since <paramref name="previous"/> was accepted.
        /// A value that is not a positive real number — zero, negative, or not
        /// finite — contributes no speed budget; only
        /// <paramref name="stepFloor"/> applies.
        /// </param>
        /// <param name="maxSpeed">
        /// Speed ceiling in world units per second.  Callers gate on
        /// <c>maxSpeed &gt; 0</c> before invoking; a value that is not a
        /// positive real number collapses the budget to
        /// <paramref name="stepFloor"/> alone, which is the narrow reading
        /// rather than the absent one.
        /// </param>
        /// <param name="stepFloor">
        /// Minimum permitted displacement regardless of the interval, in world
        /// units.  A value that is not a positive real number contributes
        /// nothing.
        /// </param>
        /// <returns>
        /// <paramref name="candidate"/> when it is within budget; otherwise the
        /// point on the segment <c>previous → candidate</c> at the budget
        /// distance.  When either argument carries a non-finite coordinate no
        /// such point exists: a usable <paramref name="previous"/> is returned
        /// so the next step has something to be measured against, and an
        /// unusable one is replaced by <paramref name="candidate"/>.  That last
        /// arm is the only one whose answer can itself be non-finite, and only
        /// when both arguments already were.
        /// </returns>
        public static Vector3 ClampPositionStep(
            Vector3 previous,
            Vector3 candidate,
            double  dtSeconds,
            float   maxSpeed,
            float   stepFloor)
        {
            // Componentwise delta, widened BEFORE the subtraction — read only
            // .x/.y/.z so the helper has no dependency on Vector3 operator
            // overloads.  Two coordinates the receive path accepts as finite
            // can lie further apart than the float type can express, and taken
            // in float their difference is an infinity that no later precision
            // recovers: the scale derived from it below is zero, and infinity
            // times zero is NaN.  What widening buys is RANGE — every
            // difference two floats can have is orders of magnitude inside
            // double's — and not exactness, which no width would buy: operands
            // far enough apart in exponent need more significand bits than a
            // double has, so 1.0f less the smallest denormal still answers 1.0.
            // Half an ulp is immaterial to a budget comparison; an infinity is
            // not.
            double dx = (double)candidate.x - previous.x;
            double dy = (double)candidate.y - previous.y;
            double dz = (double)candidate.z - previous.z;

            // Squared distance, kept wide: the square of a map-scale delta is
            // beyond anything a float can hold — the widest is some 1e77 — so
            // narrowing here would answer infinity for a distance that is
            // merely large, and the comparison below would read it as out of
            // budget for any budget at all.
            double distSq = dx * dx + dy * dy + dz * dz;

            // With the delta taken in double, distSq is non-finite exactly when
            // one of the two points handed in is not a point.  No position on
            // the segment answers that, and whichever position is returned
            // becomes the reference the NEXT step is measured against, so the
            // choice decides whether the condition lasts a frame or a lifetime:
            // a usable reference is held and the step refused, while an
            // unusable one is replaced by the candidate on the same terms as
            // the first snapshot of a stream — unmeasurable, therefore
            // unbounded, and gated normally from the following step onward.
            if (double.IsNaN(distSq) || double.IsInfinity(distSq))
                return IsFinite(previous) ? previous : candidate;

            // Each term contributes only what is a real, positive quantity.
            // Reading a non-positive one as zero is the documented contract; a
            // non-finite one has to be read the same way or the budget stops
            // being a number, because an unbounded ceiling over a zero interval
            // is NaN — and NaN loses every comparison below, so the step is
            // neither accepted nor clamped but scaled by a quantity that is not
            // a ratio.  An unusable ceiling therefore buys the floor and
            // nothing more, which is the reading that cannot be exploited: a
            // gate whose configuration is unreadable must not read as absent.
            double dt      = PositiveFinite(dtSeconds);
            double speed   = PositiveFinite(maxSpeed);
            double floor   = PositiveFinite(stepFloor);
            double allowed = speed * dt + floor;

            // Within the displacement budget — accept verbatim.
            if (distSq <= allowed * allowed)
                return candidate;

            // The budget above is a real number no smaller than zero, and
            // distSq exceeds it, so the distance is positive and the division
            // is defined — no delta small enough to fail this can reach it, the
            // square root of the smallest positive double being some 160 orders
            // of magnitude above it.  Kept as the structural guarantee that
            // `scale` is a ratio of two real numbers however the budget above
            // comes to be derived, rather than as a case that occurs.
            double dist = Math.Sqrt(distSq);
            if (dist <= double.Epsilon)
                return candidate;

            // Narrowed once, at the end.  The interpolated point lies between
            // two finite floats and is therefore representable, while the
            // offset on its own need not be: a budget wider than the float
            // range narrows to an infinity that then carries into the sum.
            // Rounding once rather than twice is the smaller of the two
            // reasons.
            double scale = allowed / dist;
            return new Vector3(
                (float)(previous.x + dx * scale),
                (float)(previous.y + dy * scale),
                (float)(previous.z + dz * scale));
        }

        /// <summary>
        /// <paramref name="value"/> when it is a real quantity greater than
        /// zero, and zero for everything else.
        /// </summary>
        /// <remarks>
        /// The ceiling among the three reaches this from a serialised inspector
        /// field, where a figure too large for the type is stored as an
        /// infinity rather than refused, and the interval is derived from two
        /// timestamps.  Folding an unusable one to zero is what keeps the
        /// budget a real number, which every comparison drawn from it depends
        /// on.
        /// </remarks>
        private static double PositiveFinite(double value)
        {
            return value > 0.0 && !double.IsPositiveInfinity(value)
                ? value
                : 0.0;
        }

        /// <summary>
        /// Whether every component of <paramref name="v"/> is a real number.
        /// </summary>
        /// <remarks>
        /// Stated over the vector rather than over each component at the call
        /// site, because the question this file asks is always about a position
        /// entire: a point with one unusable axis is not a point.
        /// </remarks>
        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }
    }
}
