// RTMPE SDK — Runtime/Core/Protocol/InboundBudget.cs
//
// Token-bucket admission controller that bounds CPU under a packet flood.
//
// Threat model — why this exists:
//   The SDK speaks to exactly one peer (the gateway), so this is effectively
//   a hostile-gateway / replay-amplifier defence. Without a token bucket, a
//   compromised or hijacked gateway could saturate the SDK's main thread by
//   replaying legitimate AEAD-valid packets at line rate; every replayed
//   packet would otherwise reach DecryptInboundPacket, fail anti-replay
//   admission, and burn an Interlocked.CompareExchange iteration on the way
//   out. Putting the bucket BEFORE any header / AEAD work caps the per-
//   second cost of a flood at a constant.
//
// Capacity choices:
//   • Sustained: scales with room size.  Client inbound is dominated by
//     peer-to-peer fan-out — every other member's transform, rigidbody,
//     variable, and RPC traffic relayed by the gateway — which grows with
//     member count, so a single fixed rate that fits a small room would
//     silently drop legitimate state in a large one.  Before a room is
//     joined the rate holds at the RefillPerSec default; ConfigureForRoomSize
//     resizes it from the negotiated capacity (see that method) and clamps at
//     MaxSustainedPps so the flood defence stays bounded even for a maximum
//     (u8) room or a gateway that inflates the negotiated size.
//   • Burst: twice the sustained rate — absorbs application bursts (resync
//     after pause, mass spawn frame, etc.) without dropping while keeping
//     roughly one second of headroom above steady state.
//
// Threading contract:
//   • TryConsume() and the underlying token-bucket fields are main-thread-
//     only (matches ProcessPacket's single-thread invariant). The bucket
//     cannot underflow into negatives because each call decrements at most
//     once.
//   • The dropped-flood counter is mutated cross-thread in principle (a
//     future receive path that elects to count without consuming would
//     still want atomicity); kept Interlocked-protected to keep that future
//     valid without revisiting the contract.

using System;
using System.Diagnostics;
using System.Threading;

namespace RTMPE.Core.Protocol
{
    internal sealed class InboundBudget
    {
        /// <summary>
        /// Default burst capacity in tokens before a room is joined, and the
        /// floor that <see cref="ConfigureForRoomSize"/> never drops below.
        /// </summary>
        public const float MaxTokens = 3000f;

        /// <summary>
        /// Default sustained refill (tokens/sec) before a room is joined, and
        /// the floor that <see cref="ConfigureForRoomSize"/> never drops below.
        /// </summary>
        public const float RefillPerSec = 1500f;

        /// <summary>
        /// Sustained allowance per other room member.  Covers one transform
        /// (30 Hz) plus one rigidbody (20 Hz) plus a handful of variable
        /// updates per peer with headroom — the components of peer-to-peer
        /// fan-out that scale with member count.
        /// </summary>
        public const float PerPeerPps = 150f;

        /// <summary>
        /// Fan-out-independent overhead: the coalesced authoritative
        /// state-sync stream, heartbeats, and control traffic that do not
        /// grow with player count.
        /// </summary>
        public const float ControlOverheadPps = 300f;

        /// <summary>
        /// Hard ceiling on the room-scaled sustained rate so the flood
        /// defence stays bounded even for a maximum (u8) room.
        /// </summary>
        public const float MaxSustainedPps = 40000f;

        // Effective rates — initialised to the pre-room defaults and
        // recomputed by ConfigureForRoomSize once a room's negotiated
        // capacity is known.
        private float _refillPerSec = RefillPerSec;
        private float _maxTokens    = MaxTokens;

        // Main-thread-only state.
        private long  _lastRefillTicks;
        private float _tokens = MaxTokens;

        // Atomic — see threading contract above.
        private long  _droppedFloodPacketCount;

        /// <summary>
        /// Total packets dropped because <see cref="TryConsume"/> returned
        /// <see langword="false"/> at the gate. Surfaced for backpressure
        /// observability — any persistent non-zero rate means either a
        /// hostile gateway or a configuration mismatch (legitimate burst
        /// above the cap).
        /// </summary>
        public long DroppedFloodPacketCount =>
            Interlocked.Read(ref _droppedFloodPacketCount);

        /// <summary>Current sustained refill rate (tokens/sec) after any room-size scaling.</summary>
        public float CurrentRefillPerSec => _refillPerSec;

        /// <summary>Current burst capacity (tokens) after any room-size scaling.</summary>
        public float CurrentMaxTokens => _maxTokens;

        /// <summary>
        /// Resize the bucket for the negotiated capacity of the room just
        /// joined so peer-to-peer fan-out — which scales with member count —
        /// never trips the gate during legitimate play.  The rate is held at
        /// the <see cref="RefillPerSec"/> floor for small rooms and clamped at
        /// <see cref="MaxSustainedPps"/> for the largest so the flood defence
        /// stays bounded.  Burst tracks twice the sustained rate.  Idempotent
        /// and safe to call on every room transition; only ever caps the live
        /// token count downward, so a resize cannot manufacture a free burst.
        /// </summary>
        /// <param name="roomMaxPlayers">Negotiated room capacity (members).</param>
        public void ConfigureForRoomSize(int roomMaxPlayers)
        {
            int peers = roomMaxPlayers > 1 ? roomMaxPlayers - 1 : 0;
            float sustained = ControlOverheadPps + peers * PerPeerPps;
            if (sustained < RefillPerSec)    sustained = RefillPerSec;
            if (sustained > MaxSustainedPps) sustained = MaxSustainedPps;

            _refillPerSec = sustained;
            _maxTokens    = sustained * 2f;
            if (_tokens > _maxTokens) _tokens = _maxTokens;
        }

        /// <summary>
        /// Restore the pre-room default rates when the client leaves a room
        /// and peer fan-out ceases.
        /// </summary>
        public void ResetToDefault()
        {
            _refillPerSec = RefillPerSec;
            _maxTokens    = MaxTokens;
            if (_tokens > _maxTokens) _tokens = _maxTokens;
        }

        /// <summary>
        /// Attempts to consume one token. Refills the bucket from elapsed
        /// wall-clock time first, capped at the current burst capacity. Returns
        /// <see langword="false"/> when the bucket is empty — caller drops
        /// the packet to bound CPU under flood.
        /// </summary>
        /// <remarks>
        /// Stopwatch ticks are monotonic so an NTP step cannot freeze or
        /// open the gate; <see cref="Stopwatch.Frequency"/> is used to
        /// convert ticks to seconds.
        /// </remarks>
        public bool TryConsume() => TryConsume(1f);

        /// <summary>
        /// Admit one unit of inbound WORK, charging <paramref name="cost"/>
        /// tokens rather than one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔑 The bucket was a bound on the NUMBER of packets, and it was sized
        /// from the cost of an ordinary one. That holds while every packet costs
        /// roughly the same, and one does not: a `Challenge` (0x06) runs an
        /// Ed25519 verify on the main thread, measured at <b>3.93 ms</b> on
        /// .NET 8 Release — four orders of magnitude above the ~1 µs the
        /// per-frame dispatcher cap states as its own sizing premise. At the
        /// out-of-room refill of 1500 pps, an attacker spending every token on
        /// that one packet type buys about six seconds of main-thread work per
        /// second of wall clock.
        /// </para>
        /// <para>
        /// ⚠️ Charging by cost keeps every property the item-counting version
        /// had, and that matters: the reason this bucket — rather than a
        /// per-attempt cap — is the real defence is that <b>a rate limit cannot
        /// lock anything out, because it does not discriminate by outcome</b>.
        /// A cost is a property of the packet TYPE, decided before the payload
        /// is examined and identical for a genuine frame and a forged one, so
        /// that property survives intact. A charge that varied with whether
        /// validation succeeded would not, and would rebuild the lockout the
        /// §81 repair removed.
        /// </para>
        /// <para>
        /// ⛔ A cost above <see cref="MaxTokens"/> can never be paid, so it
        /// would refuse the packet type outright rather than rate-limit it.
        /// Costs are therefore clamped to the bucket's capacity: the strongest
        /// meaning available is "at most one of these per full bucket".
        /// </para>
        /// </remarks>
        public bool TryConsume(float cost)
        {
            if (!(cost >= 1f)) cost = 1f;          // also rejects NaN
            if (cost > _maxTokens) cost = _maxTokens;

            long now = Stopwatch.GetTimestamp();
            long prev = _lastRefillTicks;
            if (prev == 0)
            {
                _lastRefillTicks = now;
            }
            else if (now > prev)
            {
                double elapsedSec = (now - prev) / (double)Stopwatch.Frequency;
                _tokens = Math.Min(
                    _maxTokens,
                    _tokens + (float)(elapsedSec * _refillPerSec));
                _lastRefillTicks = now;
            }

            if (_tokens >= cost)
            {
                _tokens -= cost;
                return true;
            }
            return false;
        }

        /// <summary>Measured cost of one rejected Ed25519 verify, .NET 8 Release.</summary>
        /// <remarks>
        /// ⚠️ A FLOOR. Unity's Mono/IL2CPP <c>BigInteger</c> is slower than
        /// .NET 8's, so the real cost on a player is higher and this ratio is
        /// conservative in the right direction.
        /// </remarks>
        public const float ChallengeVerifyMillis = 3.93f;

        /// <summary>
        /// How much wall-clock time per second handshake signature verification
        /// may consume — the policy this cost is derived FROM.
        /// </summary>
        /// <remarks>
        /// 🔑 5% of one second. Stated as a share of time because that is what
        /// the defect was about: both existing bounds counted items, and this
        /// item costs four orders of magnitude more than the item they were
        /// sized from. A number of packets per second is not a statement about
        /// main-thread load; a number of milliseconds per second is.
        /// </remarks>
        public const float VerifyBudgetMillisPerSec = 50f;

        /// <summary>
        /// What one inbound <c>Challenge</c> (0x06) costs this bucket.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔑 A DERIVATION, and an instance one. It answers "how many tokens
        /// must a verify cost so that spending the whole refill on verifies
        /// consumes exactly the permitted share of wall clock" — so it tracks
        /// <see cref="CurrentRefillPerSec"/>, which
        /// <see cref="ConfigureForRoomSize"/> raises with room size. A constant
        /// would have kept the token price fixed while the token supply grew,
        /// and the share of the main thread would have grown with it.
        /// </para>
        /// <para>
        /// 🚨 Never a literal. The first version of this was written as a ratio
        /// of two millisecond figures — 3930 tokens — which EXCEEDS
        /// <see cref="MaxTokens"/>, so the clamp swallowed it and the policy
        /// silently became "one Challenge per full bucket", about one every two
        /// seconds. A cost that is a number rather than a derivation can be
        /// wrong in a way nothing reads back.
        /// </para>
        /// <para>
        /// At the default out-of-room refill this is ~118 tokens: about 12
        /// verifications per second sustained (50 ms of main-thread work per
        /// second), with a full bucket carrying ~25. A handshake needs one.
        /// </para>
        /// </remarks>
        public float ChallengeVerifyCost =>
            _refillPerSec * ChallengeVerifyMillis / VerifyBudgetMillisPerSec;

        /// <summary>
        /// Records that one inbound packet was dropped at the bucket gate.
        /// Atomic so a future cross-thread caller does not need to revisit
        /// the contract.
        /// </summary>
        public void RecordDrop()
        {
            Interlocked.Increment(ref _droppedFloodPacketCount);
        }
    }
}
