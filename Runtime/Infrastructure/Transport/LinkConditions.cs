// RTMPE SDK — Runtime/Infrastructure/Transport/LinkConditions.cs
//
// The shape a SimulatedLinkTransport gives the link: a one-way delay, a
// jitter band around it, a share of datagrams lost, and whether jitter may
// overturn the order datagrams were offered in.  One value for both
// directions — the round trip is twice the delay.

using System;
using System.Globalization;

namespace RTMPE.Transport
{
    /// <summary>
    /// What a <see cref="SimulatedLinkTransport"/> does to every datagram, in
    /// each direction.
    /// </summary>
    /// <remarks>
    /// The constructor clamps every value into its range (a NaN or infinite
    /// loss reads as 0), so the properties always return the values in effect.
    /// </remarks>
    public readonly struct LinkConditions : IEquatable<LinkConditions>
    {
        /// <summary>Ceiling on <see cref="DelayMs"/>, in milliseconds.</summary>
        public const int MaxDelayMs = 10_000;

        /// <summary>Ceiling on <see cref="JitterMs"/>, in milliseconds.</summary>
        public const int MaxJitterMs = 5_000;

        /// <summary>No shaping: every datagram passes at once, in order.</summary>
        public static LinkConditions None => default;

        /// <summary>
        /// One-way delay every datagram is held for, in milliseconds.  Applied
        /// on the way out and on the way in, so a round trip costs twice this.
        /// </summary>
        public int DelayMs { get; }

        /// <summary>
        /// Half-width of the jitter band, in milliseconds: each datagram's
        /// hold is <see cref="DelayMs"/> plus a uniform draw from
        /// [−<see cref="JitterMs"/>, +<see cref="JitterMs"/>], never below zero.
        /// </summary>
        public int JitterMs { get; }

        /// <summary>Share of datagrams dropped in each direction, 0–100.</summary>
        public float LossPercent { get; }

        /// <summary>
        /// Whether jitter may deliver a datagram before one offered earlier.
        /// When <see langword="false"/>, a datagram due sooner than the one
        /// ahead of it waits for it, as on a single network path: jitter then
        /// bunches datagrams together instead of reordering them, and the
        /// average delay is slightly above <see cref="DelayMs"/>.
        /// </summary>
        public bool Reorder { get; }

        /// <summary>
        /// Creates a set of conditions, clamping each value into its range.
        /// </summary>
        /// <param name="delayMs">One-way delay, clamped to 0–<see cref="MaxDelayMs"/>.</param>
        /// <param name="jitterMs">Jitter half-width, clamped to 0–<see cref="MaxJitterMs"/>.</param>
        /// <param name="lossPercent">Loss share, clamped to 0–100; NaN and infinities read as 0.</param>
        /// <param name="reorder">Whether jitter may overturn the offered order.</param>
        public LinkConditions(int delayMs, int jitterMs, float lossPercent, bool reorder)
        {
            DelayMs     = Clamp(delayMs, 0, MaxDelayMs);
            JitterMs    = Clamp(jitterMs, 0, MaxJitterMs);
            LossPercent = float.IsNaN(lossPercent) || float.IsInfinity(lossPercent)
                ? 0f
                : Math.Max(0f, Math.Min(100f, lossPercent));
            Reorder     = reorder;
        }

        /// <summary>
        /// True when nothing is shaped: no delay, no jitter and no loss.
        /// </summary>
        public bool IsTransparent => DelayMs == 0 && JitterMs == 0 && LossPercent <= 0f;

        /// <summary>
        /// Returns a readable one-line summary, such as
        /// <c>250 ms ± 50 ms, 5 % loss, order kept</c>, or <c>no shaping</c>.
        /// </summary>
        public string Describe()
        {
            if (IsTransparent) return "no shaping";
            var c = CultureInfo.InvariantCulture;
            return DelayMs.ToString(c) + " ms ± " + JitterMs.ToString(c) + " ms, "
                + LossPercent.ToString("0.#", c) + " % loss, "
                + (Reorder ? "reorder allowed" : "order kept");
        }

        /// <inheritdoc/>
        public override string ToString() => Describe();

        /// <inheritdoc/>
        public bool Equals(LinkConditions other)
            => DelayMs == other.DelayMs
            && JitterMs == other.JitterMs
            && LossPercent.Equals(other.LossPercent)
            && Reorder == other.Reorder;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is LinkConditions other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int h = DelayMs;
                h = (h * 397) ^ JitterMs;
                h = (h * 397) ^ LossPercent.GetHashCode();
                h = (h * 397) ^ (Reorder ? 1 : 0);
                return h;
            }
        }

        /// <summary>Value equality.</summary>
        public static bool operator ==(LinkConditions left, LinkConditions right) => left.Equals(right);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(LinkConditions left, LinkConditions right) => !left.Equals(right);

        private static int Clamp(int value, int lo, int hi) => value < lo ? lo : value > hi ? hi : value;
    }
}
