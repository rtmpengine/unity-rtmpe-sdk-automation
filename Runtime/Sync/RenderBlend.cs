// RTMPE SDK — Runtime/Sync/RenderBlend.cs
//
// The weighting curve the render-side error absorber decays a captured pose
// error along.  Split out of NetworkTransformInterpolator for the same reason
// TransformBroadcastCadence is: it is a pure function of one number, it carries
// no UnityEngine dependency at all, and it is the half of the absorber whose
// SHAPE — not its wiring — is what has to be right.
//
// The absorber renders `target + error · Weight(s)` for one window after a
// discontinuity, where s runs 0 → 1 across the window.  The curve therefore has
// to satisfy four conditions:
//
//   w(0) = 1   the first frame of the window reproduces the pose already on
//              screen exactly, so arming the absorber is itself invisible;
//   w(1) = 0   the window ENDS — the replica tracks the resolver again rather
//              than carrying a permanent standing offset;
//   w'(0) = 0  the rendered velocity at the moment of arming is the object's
//              own, so the correction does not start with a kick;
//   w'(1) = 0  and it does not end with one either.
//
// Smoothstep — 1 − (3s² − 2s³) — is the lowest-order polynomial meeting all
// four.  🚨 All four were argued here and held by NOTHING: replacing the whole
// body with `1f - s` left every case in the interpolator shard green.  They are
// asserted now — RenderPathSequenceTests.E10 — including the peak slope 1.5 that
// P1b's and E6's per-frame bounds are derived from.  ⛔ A linear w meets only the first two: it replaces the position
// discontinuity it was introduced to remove with a VELOCITY discontinuity at
// both ends of the window, and at 60 Hz a velocity step is exactly what the eye
// reads as a yank.  The whole point of the absorber is that neither end of it
// is visible.

namespace RTMPE.Sync
{
    /// <summary>
    /// Pure weighting curve for render-side error absorption.
    /// </summary>
    internal static class RenderBlend
    {
        /// <summary>
        /// Smoothstep-complement weight for a normalised window position.
        /// Returns 1 at (or before) the start of the window, 0 at (or after)
        /// its end, and the smoothstep complement in between.
        /// </summary>
        /// <param name="s">
        /// Elapsed fraction of the absorption window.  Callers gate on
        /// <c>0 ≤ s &lt; 1</c>; the clamps here make the function total, and the
        /// upper clamp is written <c>!(s &lt; 1f)</c> so a NaN — which compares
        /// false against everything — resolves to "the window is over" and
        /// hands back 0 rather than propagating into a rendered pose.
        /// </param>
        internal static float Weight(float s)
        {
            if (s <= 0f) return 1f;
            if (!(s < 1f)) return 0f;

            // 3s² − 2s³ is smoothstep; the absorber decays an error, so it
            // wants the complement.
            return 1f - (3f * s * s - 2f * s * s * s);
        }
    }
}
