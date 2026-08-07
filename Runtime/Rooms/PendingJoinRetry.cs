// RTMPE SDK — Runtime/Rooms/PendingJoinRetry.cs
//
// Outbound JoinRoom reliability for RoomManager.  A JoinRoom request travels
// as a best-effort control-plane packet: on the plain-UDP transport nothing
// retransmits it, so a single lost request — or a lost reply — leaves the
// client wedged in a "joining…" state that never resolves.  This primitive
// parks the exact request bytes and re-emits them on an exponential-backoff
// schedule until the response disarms it or the attempt budget is spent.  It
// is the request-side complement to the gateway's idempotent JoinRoom
// recovery: a resend that reaches an already-seated session is recovered
// server-side rather than rejected, so retransmission is safe.
//
// Scope: a client occupies one room at a time, so a single in-flight slot is
// sufficient and a fresh JoinRoom supersedes any earlier pending one.
//
// Threading: main-thread only, in common with the rest of RoomManager.  The
// clock is supplied by the caller as monotonic seconds, so the retransmit
// schedule is exercised deterministically under test without a real timer,
// and a wall-clock step cannot perturb the ladder.
//
// The type is deliberately free of UnityEngine so it can be unit-tested beside
// the other Rooms correlator primitives; the caller supplies a self-guarding
// resend so a transport fault is surfaced at the call site, not here.

using System;
using System.Diagnostics;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Single-slot retransmit state for the outstanding JoinRoom request.
    /// </summary>
    public sealed class PendingJoinRetry
    {
        // The ladder mirrors the shape of ReliableChannel's ARQ timers but is
        // retuned for the control plane's slower, rarer round-trip: a half-
        // second floor keeps a merely-slow reply from drawing an immediate
        // burst of duplicate joins, while the retransmit cap bounds the total
        // wait before the caller is told the join timed out.  With these
        // constants the retransmits land at roughly +0.5, 1, 2, 4, 8, 12 s
        // after the initial send and the join is declared timed out near 16 s.
        private const double InitialRtoSeconds = 0.5;
        private const double MaxRtoSeconds     = 4.0;
        private const int    MaxRetransmits    = 6;

        private bool   _armed;
        private byte[] _packet;
        private string _label;
        private int    _retransmits;        // performed by Tick; 0 means only the initial send has gone out
        private double _nextSendAtSeconds;

        /// <summary>True while a JoinRoom request is outstanding.</summary>
        public bool IsArmed => _armed;

        /// <summary>
        /// Monotonic seconds reading, unaffected by wall-clock steps.  Callers
        /// that arm and tick from the same clock read it through here so the
        /// two operations share one time base.
        /// </summary>
        public static double NowSeconds() =>
            Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>
        /// Begin tracking <paramref name="packet"/> as the outstanding join
        /// request.  The caller performs the initial transmit itself; the first
        /// retransmit is scheduled one RTO out.  Any prior pending join is
        /// superseded — the newer request is the one the caller now awaits.
        /// </summary>
        public void Arm(byte[] packet, string label, double nowSeconds)
        {
            _packet            = packet ?? throw new ArgumentNullException(nameof(packet));
            _label             = label;
            _retransmits       = 0;
            _nextSendAtSeconds = nowSeconds + InitialRtoSeconds;
            _armed             = true;
        }

        /// <summary>
        /// Stop tracking the outstanding request.  Called when its response
        /// arrives (success or error) and on session teardown.  Idempotent.
        /// </summary>
        public void Disarm()
        {
            _armed       = false;
            _packet      = null;
            _label       = null;
            _retransmits = 0;
        }

        /// <summary>
        /// Re-emit the outstanding request once its retransmit timer has
        /// expired, then reschedule under exponential backoff.  When the
        /// retransmit budget is spent the entry disarms itself and
        /// <paramref name="onExhausted"/> is invoked with the join label so the
        /// caller can surface a timeout instead of hanging forever.  No-op when
        /// nothing is pending or the timer has not yet expired.
        /// </summary>
        public void Tick(double nowSeconds, Action<byte[]> resend, Action<string> onExhausted)
        {
            if (resend == null) throw new ArgumentNullException(nameof(resend));
            if (!_armed) return;
            // Tolerate a single pathological clock sample rather than letting it
            // stall or storm the ladder; a steady stream is the caller's bug.
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) return;
            if (nowSeconds < _nextSendAtSeconds) return;

            if (_retransmits >= MaxRetransmits)
            {
                string label = _label;
                Disarm();
                onExhausted?.Invoke(label);
                return;
            }

            // Advance the attempt count and schedule the next wake BEFORE the
            // resend, so a resend that throws (the caller's self-guarding lambda
            // is expected to contain that, but belt-and-braces) still leaves the
            // ladder walking toward exhaustion rather than pinned on this rung.
            _retransmits++;
            double interval = InitialRtoSeconds * (double)(1 << Math.Min(_retransmits - 1, 16));
            if (interval > MaxRtoSeconds) interval = MaxRtoSeconds;
            _nextSendAtSeconds = nowSeconds + interval;

            resend(_packet);
        }
    }
}
