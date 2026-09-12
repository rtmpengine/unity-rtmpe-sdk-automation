// RTMPE SDK — Runtime/Core/WarnGate.cs
//
// A one-per-second emission gate for diagnostics raised from per-tick code.
//
// A warning on a 30 Hz path is either rate-limited or it is noise: at full rate
// it costs more than the condition it reports, and it buries every other line in
// the console. A plain latch is the wrong shape for a condition that can persist
// — it says "this happened" and then never says "it is still happening" — so the
// gate re-opens on a wall of its own.  ⛔ What it does NOT carry is a count of
// what it suppressed: a caller that needs one has to keep it, and none of them
// does.  A reader who assumed otherwise would read a repeated line as a repeated
// condition and a silent second as an absent one.
//
// ⛔ "At most once per second", never "at least once": the compare-exchange
// below has a loser, and the loser stays silent even on a first-ever call. That
// is harmless while a gate decides only whether a line is written, and it is the
// property that would make moving one into control flow quietly lossy.
//
// ⚠️ And it carries no count of what it suppressed. A caller that needs one keeps
// it — `VariableBatchManager` does, and says so in its line — but most do not, so
// a second line is not a second event and a silent second is not an absent one.
//
// The compare-exchange resolves concurrent emitters to one per epoch, and the
// elapsed test is one-sided: a source that steps backwards would otherwise
// produce a negative interval, which reads as "less than a second ago" and
// holds the gate shut for the length of the step. Stopwatch is monotonic where
// it is backed by a performance counter, but the gate does not depend on that
// being true everywhere — silence is the one failure a diagnostic must not
// have, so it is cheaper to reject the reading than to assume the platform.

namespace RTMPE.Core
{
    internal static class WarnGate
    {
        /// <summary>
        /// True when the caller's one-second window has elapsed and it should
        /// emit; false when it should stay silent. <paramref name="lastEmitTicks"/>
        /// is the caller's own gate state and is advanced on a true result.
        /// </summary>
        internal static bool ShouldEmit(ref long lastEmitTicks)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long prev = System.Threading.Interlocked.Read(ref lastEmitTicks);
            long oneSecond = System.Diagnostics.Stopwatch.Frequency;

            // `now >= prev` is the clamp: a reading behind the recorded one
            // carries no information about how long ago that was, so it re-opens
            // the gate rather than suppressing on a negative interval.
            if (prev != 0 && now >= prev && now - prev < oneSecond) return false;
            return System.Threading.Interlocked.CompareExchange(
                ref lastEmitTicks, now, prev) == prev;
        }
    }
}
