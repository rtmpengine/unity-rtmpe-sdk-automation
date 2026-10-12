// RTMPE SDK — Runtime/Sync/TransformTickAlignment.cs
//
// Maps an owner's frame-instant transform sample back onto the simulation tick
// it is labelled with.
//
// The owner samples its transform in Update(), i.e. at a visual-frame instant,
// but stamps the sample with the simulation tick current at that moment.  A
// receiver reconstructs the sender timeline as tick x tickInterval, so it
// renders the pose as though it had been captured exactly on the tick boundary.
// The gap between the two — the time already elapsed past the boundary when the
// frame ran — is not constant: it walks across the tick as the frame rate beats
// against the tick rate, and that walk is rendered as motion the owner never
// made.
//
// The gap is known locally: it is the simulation driver's leftover accumulator
// after the tick advanced.  Given the previous sample and its instant, the pose
// on the boundary lies between the two samples, so it is recovered by
// interpolation — never extrapolation — and the sample the receiver replays is
// the pose the owner actually held at the tick it is labelled with.
//
// Kept UnityEngine-free so it is exercisable from the headless dotnet xunit
// runner, matching TransformBroadcastCadence in the same folder.

namespace RTMPE.Sync
{
    internal static class TransformTickAlignment
    {
        /// <summary>
        /// Resolve the interpolation parameter that carries a sample taken at
        /// <paramref name="currentSampleTime"/> back to the tick boundary
        /// <paramref name="subTickResidualSeconds"/> earlier, relative to the
        /// previous sample at <paramref name="previousSampleTime"/>.
        ///
        /// <para>Returns <see langword="false"/> — leave the sample untouched —
        /// when there is nothing to correct or when the correction could not be
        /// obtained by interpolating between the two samples:</para>
        /// <list type="bullet">
        ///   <item>a non-finite or negative residual, or a residual of zero: the
        ///     frame already sits on the boundary;</item>
        ///   <item>a non-positive span between the samples, which carries no
        ///     direction to interpolate along;</item>
        ///   <item>a residual reaching back at or beyond the previous sample,
        ///     where the boundary pose is not bracketed by the pair.  Emitting
        ///     the older sample verbatim would report a pose staler than the one
        ///     actually held, so the raw sample is preferred.</item>
        /// </list>
        ///
        /// <para>On success <paramref name="fraction"/> lies in (0, 1): 1 is the
        /// current sample, 0 the previous one.</para>
        /// </summary>
        internal static bool TryResolveFraction(
            double previousSampleTime,
            double currentSampleTime,
            double subTickResidualSeconds,
            out float fraction)
        {
            fraction = 1f;

            if (!IsFinite(previousSampleTime)
                || !IsFinite(currentSampleTime)
                || !IsFinite(subTickResidualSeconds))
                return false;

            // Nothing to correct: the sample was taken on the boundary.
            if (subTickResidualSeconds <= 0.0)
                return false;

            double span = currentSampleTime - previousSampleTime;
            if (span <= 0.0)
                return false;

            // The boundary must fall strictly inside the sample pair for the
            // result to be an interpolation.
            if (subTickResidualSeconds >= span)
                return false;

            fraction = (float)(1.0 - subTickResidualSeconds / span);
            return true;
        }

        // Spelled without double.IsFinite so the SDK compiles on Unity runtimes
        // that do not expose it, matching the interpolator's finiteness gates.
        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
