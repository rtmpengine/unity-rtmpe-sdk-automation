// RTMPE SDK — Runtime/Core/Diagnostics/DiagnosticsEntryBudget.cs
//
// How many diagnostics entries the uplink may still send: the gateway's
// per-session budget, kept on this side of the wire (audit P8-M3).
//
// A default gateway writes 50 entries a second for one session, after a burst
// of 600, and drops the rest where nothing the developer reads says so.  The
// routine path sends within that by its defaults, but an Error releases a flush
// every 250 ms of up to maxPacketsPerInterval packets, so a crash loop could
// send several hundred entries a second for as long as it lasted.  Both paths
// draw on this one bucket, so neither can.  What the bucket holds back stays in
// the batcher, whose own bound drops the oldest and names the gap.
//
// Pure and UnityEngine-free, with the time passed in, so the rule is
// verifiable without a session or a clock.

using System;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// A token bucket of diagnostics entries, refilled at the gateway's default
    /// per-session rate and holding at most its default burst.
    /// </summary>
    internal sealed class DiagnosticsEntryBudget
    {
        /// <summary>
        /// Entries a second a default gateway writes for one session
        /// (<c>GATEWAY_SDK_DIAGNOSTICS_ENTRIES_PER_SEC</c>).
        /// </summary>
        internal const int GatewayEntriesPerSecond = 50;

        /// <summary>
        /// Entries a default gateway writes for one session in a burst before
        /// the rate applies (<c>GATEWAY_SDK_DIAGNOSTICS_ENTRY_BURST</c>).
        /// </summary>
        internal const int GatewayEntryBurst = 600;

        private readonly double _perMs;
        private readonly double _burst;
        private double _credit;
        private long _refilledAtMs;
        private bool _clocked;

        /// <summary>The gateway's default budget, full.</summary>
        internal DiagnosticsEntryBudget()
            : this(GatewayEntriesPerSecond, GatewayEntryBurst)
        {
        }

        /// <summary>A budget of <paramref name="perSecond"/> entries a second, holding at most <paramref name="burst"/>, full.</summary>
        internal DiagnosticsEntryBudget(int perSecond, int burst)
        {
            if (perSecond < 1) throw new ArgumentOutOfRangeException(nameof(perSecond));
            if (burst < 1) throw new ArgumentOutOfRangeException(nameof(burst));
            _perMs = perSecond / 1000.0;
            _burst = burst;
            _credit = burst;
        }

        /// <summary>Whole entries that may be sent at <paramref name="nowMs"/>.</summary>
        internal int Available(long nowMs)
        {
            if (!_clocked)
            {
                _clocked = true;
                _refilledAtMs = nowMs;
            }
            // A clock that steps backwards refills nothing.
            long elapsed = Math.Max(0L, nowMs - _refilledAtMs);
            _credit = Math.Min(_burst, _credit + elapsed * _perMs);
            _refilledAtMs = nowMs;
            return _credit >= int.MaxValue ? int.MaxValue : (int)Math.Floor(_credit);
        }

        /// <summary>Spend <paramref name="entries"/> that were sent.</summary>
        internal void Spend(int entries)
        {
            if (entries > 0) _credit = Math.Max(0.0, _credit - entries);
        }
    }
}
