// RTMPE SDK — Runtime/Core/HandshakeCookieGate.cs
//
// Which HandshakeCookie (0x0D) frames an attempt answers (audit GW-ING-05).
//
// A busy gateway answers a HandshakeInit with a cookie instead of a Challenge,
// and the client resends its sealed envelope with the cookie in front
// (CookieHandshakeInit, 0x0E).  The frame is pre-session and unauthenticated,
// so this gate decides how much of the client's behaviour such a frame can
// drive, and the answer is: little, and nothing it cannot undo.
//
//   • A cookie of the wrong length is not a cookie.
//   • The cookie the attempt is already carrying is not answered again — a
//     duplicate, or the gateway answering one of our own retransmissions,
//     changes nothing the resend has not already said.
//   • At most MaxResends cookies are answered per attempt.  A forged stream
//     cannot make this client a sender of unbounded traffic, and past the cap
//     the latest cookie still replaces the parked request without moving the
//     retransmit ladder's schedule, so the ladder keeps carrying a current one
//     and still fires on time — re-armed by each, a stream of cookies would
//     push its next re-emission out for as long as it lasted.
//
// What a forged cookie costs is bounded by the gateway, not by this gate: a
// cookie that does not verify is not a refusal there, only an unproven
// handshake, so while the gateway is not under load the envelope behind a
// forged cookie is opened exactly as the envelope behind no cookie would be.
//
// ⛔ What this does not bound: a sender that can reach this client's port from
// the gateway's address can keep replacing the parked cookie with forged ones,
// and while the gateway is under load each forged one it is sent draws a
// genuine one back — a race the forger runs at its own rate.  The gateway
// echoes the request's sequence in the cookie it sends, and that is not
// checked here on purpose: the sequence is a counter that starts from zero on
// every attempt (each builds its own PacketBuilder), so checking it would cost
// such a sender a guess among a handful of values and buy nothing.
//
// Threading: main-thread only, with the rest of the connect path.  Free of
// UnityEngine so it is unit-testable beside the other Core primitives.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Per-attempt decision on which <c>HandshakeCookie</c> frames are answered
    /// with an immediate resend.
    /// </summary>
    internal sealed class HandshakeCookieGate
    {
        /// <summary>The length of a cookie on the wire.</summary>
        public const int CookieLength = 16;

        /// <summary>
        /// How many distinct cookies one attempt answers with an immediate resend.
        /// </summary>
        public const int MaxResends = 4;

        private byte[] _current;
        private int _resends;

        /// <summary>The cookie the attempt's resend carries, or <see langword="null"/>.</summary>
        public byte[] Current => _current;

        /// <summary>
        /// Judge a cookie frame's payload.  Returns the cookie to carry when it is
        /// new to this attempt, and whether to resend at once.
        /// </summary>
        /// <param name="payload">The frame's payload.</param>
        /// <param name="cookie">The cookie to carry, when the payload is one.</param>
        /// <returns>
        /// <see langword="true"/> when the request should be resent now with
        /// <paramref name="cookie"/>; <see langword="false"/> otherwise — though a
        /// new cookie past the cap is still returned in <paramref name="cookie"/>
        /// for the parked request to carry.
        /// </returns>
        public bool Admit(ReadOnlySpan<byte> payload, out byte[] cookie)
        {
            cookie = null;
            if (payload.Length != CookieLength) return false;
            if (_current != null && payload.SequenceEqual(_current)) return false;

            cookie = payload.ToArray();
            _current = cookie;
            if (_resends >= MaxResends) return false;
            _resends++;
            return true;
        }

        /// <summary>Forget the attempt's cookie and its resend count.</summary>
        public void Reset()
        {
            _current = null;
            _resends = 0;
        }
    }
}
