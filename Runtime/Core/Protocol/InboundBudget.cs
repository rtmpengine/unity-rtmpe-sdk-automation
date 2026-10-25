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
        /// How long after a room entry its catch-up allowance may be spent.
        /// </summary>
        public const double RoomEntryAllowanceSeconds = 10d;

        // Main-thread-only, as the bucket is.
        private float _entryAllowance;
        private long  _entryAllowanceUntilTicks;

        /// <summary>What is left of the current room entry's catch-up allowance.</summary>
        public float RoomEntryAllowance => _entryAllowance;

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
            _entryAllowance = 0f;
        }

        /// <summary>
        /// Grant the room just entered an allowance of <paramref name="packets"/>
        /// beyond the bucket, spent only once the bucket is empty and only for
        /// <see cref="RoomEntryAllowanceSeconds"/>.
        /// </summary>
        /// <remarks>
        /// A joiner is sent the room's objects at once — every live object, sent
        /// again twice in the first second — with a snapshot of each one's
        /// variables beside them.  The bucket is sized for a room's steady
        /// traffic, so a room near its object limit spent it on the first few
        /// thousand and the rest were dropped for good.  An entry's allowance
        /// replaces the last one's and ends with a leave, so what a flood buys
        /// stays bounded: a bucket a second, and one allowance an entry.
        /// </remarks>
        public void GrantRoomEntry(float packets) => GrantRoomEntry(packets, RoomEntryAllowanceSeconds);

        /// <summary>
        /// <see cref="GrantRoomEntry(float)"/> spendable for
        /// <paramref name="seconds"/>, so a test can see the window end.
        /// </summary>
        internal void GrantRoomEntry(float packets, double seconds)
        {
            if (!(packets > 0f)) return;
            _entryAllowance = packets;
            _entryAllowanceUntilTicks = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
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
        /// Costs are therefore clamped to the bucket's capacity.  ⚠️ The clamp is
        /// not enough for a cost charged AFTER this packet's own admission token,
        /// as the Challenge verify's is: that remainder is payable only up to the
        /// capacity less one, so <see cref="ChallengeVerifyCost"/> is bounded at
        /// its source (<see cref="MaxVerifyMillisEstimate"/>) rather than here.
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
            if (_entryAllowance >= cost && now < _entryAllowanceUntilTicks)
            {
                _entryAllowance -= cost;
                return true;
            }
            return false;
        }

        /// <summary>
        /// What one Challenge verify is priced at before this device has measured
        /// one, and the least it is ever priced at: a rejected Ed25519 verify on
        /// .NET 8 Release.
        /// </summary>
        /// <remarks>
        /// 🚨 This used to be the price itself and to call itself conservative.
        /// It was the opposite: the share of the main thread a flood buys is the
        /// verifies admitted times what each one REALLY costs, so a device ten
        /// times slower than the one this was measured on spent ten times the
        /// permitted share (audit P1-L32).  The price is
        /// <see cref="ChallengeVerifyMillisEstimate"/>, which every verify moves
        /// toward what it cost here — upward from this figure, never below it.
        /// As a floor it IS conservative, in the one direction a floor can be: a
        /// faster device is charged more than its verifies cost, and a run of
        /// frames that fail before the curve work cannot price the next one that
        /// does not at their cost.
        /// </remarks>
        public const float ChallengeVerifyMillis = 3.93f;

        /// <summary>
        /// The most one Challenge verify is ever priced at, in milliseconds: the
        /// whole of <see cref="VerifyBudgetMillisPerSec"/>, which prices a verify
        /// at one second of refill — half a full bucket.
        /// </summary>
        /// <remarks>
        /// 🚨 Without it one slow reading locked every later handshake out.  A
        /// price above the bucket's capacity can never be paid, and a verify that
        /// is never admitted is never measured, so nothing brought the price back
        /// down: one verify that took 100 ms — a breakpoint, a profiler, a
        /// collection that stopped the world, the app suspended mid-verify —
        /// refused every Challenge after it until the process ended (review of
        /// P1-L32).  At this price a verify is payable from any bucket after at
        /// most a second of refill, whatever the room size, because the capacity
        /// is always twice the refill.  A device whose verify really costs more
        /// spends more than the permitted share rather than never connecting.
        /// </remarks>
        public const float MaxVerifyMillisEstimate = VerifyBudgetMillisPerSec;

        /// <summary>
        /// The most time one frame spends verifying Challenges, in milliseconds —
        /// except that one verify a frame is always admitted.
        /// </summary>
        /// <remarks>
        /// 🔑 The bucket bounds a SECOND, and a full bucket carries about two
        /// seconds of verify budget; the dispatcher runs up to 200 items a frame,
        /// so the whole of it could land in one frame.  Half a 60 Hz frame.
        /// </remarks>
        public const float MaxVerifyMillisPerFrame = 8f;

        // How far a cheaper measurement moves the price toward itself.
        private const float VerifyEstimateDecay = 0.1f;

        // How far one dearer measurement can raise the price: double it, at most.
        // ⛔ Not adopted at once, which this first was: a single reading is as
        // likely to be a pause the verify happened to sit in as the device's
        // cost, and a price that jumped to it held for dozens of connections —
        // the decay runs only on verifies, about one a connection — while making
        // the genuine Challenge cheaper to starve.  A device that really is
        // slower reaches its own cost in a few verifies (from the floor to the
        // ceiling in four).
        private const float VerifyEstimateMaxRise = 2f;

        private float  _verifyMillis = ChallengeVerifyMillis;
        private double _verifyFrameStamp = double.NaN;
        private double _verifyMillisThisFrame;

        /// <summary>
        /// What a Challenge verify costs on this device, in milliseconds, as the
        /// verifies so far have measured it — <see cref="ChallengeVerifyMillis"/>
        /// before the first, and always within
        /// [<see cref="ChallengeVerifyMillis"/>, <see cref="MaxVerifyMillisEstimate"/>].
        /// </summary>
        /// <remarks>
        /// A fact about the device, so a session's <see cref="ResetToDefault"/>
        /// keeps it.
        /// </remarks>
        public float ChallengeVerifyMillisEstimate => _verifyMillis;

        /// <summary>
        /// Records what one Challenge verify cost, on the frame identified by
        /// <paramref name="frameStamp"/>.
        /// </summary>
        /// <param name="millis">Wall-clock duration of the verify.</param>
        /// <param name="frameStamp">
        /// A value constant within one frame and different between frames — the
        /// frame's number.
        /// </param>
        public void RecordChallengeVerify(double millis, double frameStamp)
        {
            if (!(millis >= 0d) || double.IsInfinity(millis)) return;
            float m = (float)millis;
            float next = m >= _verifyMillis
                ? Math.Min(m, _verifyMillis * VerifyEstimateMaxRise)
                : _verifyMillis + (m - _verifyMillis) * VerifyEstimateDecay;
            _verifyMillis = Math.Min(MaxVerifyMillisEstimate, Math.Max(ChallengeVerifyMillis, next));
            if (!frameStamp.Equals(_verifyFrameStamp))
            {
                _verifyFrameStamp = frameStamp;
                _verifyMillisThisFrame = 0d;
            }
            _verifyMillisThisFrame += millis;
        }

        /// <summary>
        /// Whether the frame identified by <paramref name="frameStamp"/> may run
        /// one more Challenge verify.  Spends nothing.
        /// </summary>
        /// <remarks>
        /// ⛔ Decided from time already spent and the price — never from what the
        /// frames that spent it turned out to be — and reset every frame, so it is
        /// a rate rather than a lockout: the handshake ladder draws the same
        /// Challenge again on a later frame.  ⚠️ A Challenge refused here is
        /// dropped, not deferred, so one that shares its frame with another on a
        /// device whose verify costs over half the allowance waits for the ladder
        /// — and a reconnect whose ReconnectInit is not on the ladder (a token
        /// claimed on receipt, or no proof) has nothing to draw it again.
        /// </remarks>
        public bool TryAdmitChallengeVerify(double frameStamp)
        {
            if (!frameStamp.Equals(_verifyFrameStamp)) return true;
            return _verifyMillisThisFrame + _verifyMillis <= MaxVerifyMillisPerFrame;
        }

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
        /// It tracks the price too: <see cref="ChallengeVerifyMillisEstimate"/> is
        /// what a verify costs on this device, so the share holds wherever the
        /// SDK runs.  Before the first measurement, at the default out-of-room
        /// refill, it is ~118 tokens: about 12 verifications per second sustained
        /// (50 ms of main-thread work per second), with a full bucket carrying
        /// ~25 — spread over frames by <see cref="MaxVerifyMillisPerFrame"/>. A
        /// handshake needs one.  At its most, one second of refill — half the
        /// bucket — so it is always payable (<see cref="MaxVerifyMillisEstimate"/>).
        /// </para>
        /// </remarks>
        public float ChallengeVerifyCost =>
            _refillPerSec * _verifyMillis / VerifyBudgetMillisPerSec;

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
