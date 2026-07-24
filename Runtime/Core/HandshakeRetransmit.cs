// RTMPE SDK — Runtime/Core/HandshakeRetransmit.cs
//
// Outbound reliability for the two client→gateway handshake steps that the SDK
// itself emits: HandshakeInit (0x05) and HandshakeResponse (0x07).  Both travel
// as raw, best-effort datagrams — on the plain-UDP transport nothing retransmits
// them, so a single lost request leaves the attempt to expire against the
// connection watchdog with no packet ever having reached the peer.  This
// primitive parks the exact bytes of the outstanding step and re-emits them on a
// bounded exponential-backoff ladder until the next step's reply disarms it or
// the ladder is spent.
//
// Why identical-byte re-emission is the correct wire behaviour here:
//   • HandshakeInit — the gateway keys a per-envelope replay guard on the
//     leading AEAD nonce, so a byte-identical re-emission of an Init the gateway
//     already accepted is rejected as a replay and changes no server state,
//     while a re-emission of an Init that was lost in flight is seen as fresh
//     and draws the Challenge.  The bytes are unchanged, so the transcript the
//     client later verifies against is unchanged.
//   • HandshakeResponse — the gateway consumes its pending-handshake slot on the
//     first Response, so a duplicate finds nothing and is dropped without
//     touching the established session, while a Response that was lost is
//     completed by the re-emission.
// Both are therefore safe to resend verbatim; the ladder's whole horizon is kept
// far inside the gateway's replay window so a re-emission is never mistaken for
// a fresh handshake.
//
// Scope: exactly one handshake step is outstanding at a time, so a single slot
// suffices; arming a later step supersedes the earlier one.  The reconnect
// initiator (ReconnectInit, 0x09) is deliberately NOT driven through here — its
// token is single-use and consumed on receipt, so a blind re-emission against an
// already-consumed token would be rejected; reconnect retries are owned by the
// bounded reconnect loop instead.
//
// Threading: main-thread only, in common with the rest of the connect path.
// The clock is supplied by the caller as monotonic seconds so the ladder is
// exercised deterministically under test and a wall-clock step cannot perturb
// it.  The type is free of UnityEngine so it is unit-testable beside the other
// Core protocol primitives.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Single-slot retransmit state for the outstanding handshake step the SDK
    /// last emitted (<c>HandshakeInit</c> or <c>HandshakeResponse</c>).
    /// </summary>
    internal sealed class HandshakeRetransmit
    {
        // The ladder is a fixed, self-contained horizon rather than a fraction of
        // the caller's connection timeout: it must stay comfortably inside the
        // gateway's per-envelope replay window (default 60 s) for every timeout
        // an integrator might configure, so that a re-emission is always caught
        // by the replay guard rather than admitted as a new handshake.  With
        // these constants the re-emissions land at +0.5, +1.0, +2.0 and +4.0 s
        // after the initial send (gaps of 0.5, 1.0 then a capped 2.0 s) — dense
        // enough to recover a single loss well within a default 10 s connect
        // budget, and a ~4 s reach that is an order of magnitude below the
        // replay window.
        private const double InitialRtoSeconds = 0.5;
        private const double MaxRtoSeconds      = 2.0;
        private const int    MaxRetransmits     = 4;

        private bool   _armed;
        private byte[] _packet;
        private string _label;
        private int    _retransmits;        // performed by Tick; 0 means only the initial send has gone out
        private double _nextSendAtSeconds;

        /// <summary>True while a handshake step is outstanding.</summary>
        public bool IsArmed => _armed;

        /// <summary>
        /// The label of the outstanding step, or <see langword="null"/> when
        /// idle.  Exposed for diagnostics only.
        /// </summary>
        public string Label => _label;

        /// <summary>
        /// Begin tracking <paramref name="packet"/> as the outstanding handshake
        /// step.  The caller performs the initial transmit itself; the first
        /// re-emission is scheduled one RTO out.  Arming supersedes any earlier
        /// step — the handshake has advanced and the previous step is no longer
        /// the one awaiting a reply.
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
        /// Stop tracking the outstanding step.  Called when the next step's reply
        /// arrives (Challenge disarms the Init, SessionAck disarms the Response)
        /// and on any attempt teardown.  Idempotent.
        /// </summary>
        public void Disarm()
        {
            _armed       = false;
            _packet      = null;
            _label       = null;
            _retransmits = 0;
        }

        /// <summary>
        /// Re-emit the outstanding step once its timer has expired, then
        /// reschedule under exponential backoff.  When the re-emission budget is
        /// spent the slot disarms itself and stops resending — the connection
        /// watchdog remains the single authority that declares the attempt
        /// failed, so exhaustion here simply ends the recovery effort quietly.
        /// No-op when nothing is pending or the timer has not yet expired.
        /// </summary>
        public void Tick(double nowSeconds, Action<byte[]> resend)
        {
            if (resend == null) throw new ArgumentNullException(nameof(resend));
            if (!_armed) return;
            // Tolerate a single pathological clock sample rather than letting it
            // stall or storm the ladder; a steady stream is the caller's bug.
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) return;
            if (nowSeconds < _nextSendAtSeconds) return;

            if (_retransmits >= MaxRetransmits)
            {
                Disarm();
                return;
            }

            // Advance the attempt count and schedule the next wake BEFORE the
            // resend, so a resend that throws still leaves the ladder walking
            // toward exhaustion rather than pinned on this rung.
            _retransmits++;
            double interval = InitialRtoSeconds * (double)(1 << Math.Min(_retransmits - 1, 16));
            if (interval > MaxRtoSeconds) interval = MaxRtoSeconds;
            _nextSendAtSeconds = nowSeconds + interval;

            resend(_packet);
        }
    }
}
