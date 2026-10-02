// RTMPE SDK — Runtime/Core/ReliableChannel.cs
//
// Application-level Automatic Repeat-reQuest (ARQ) state for reliable
// outbound frames and an inbound dedup window for in-flight reliable
// receives.  Provides the four primitives required to bolt a Selective-
// Repeat reliability layer on top of the existing UDP transport once
// gateway-side ACK plumbing lands:
//
//   1. Per-channel monotonically-increasing sequence numbers, allocated
//      in 32-bit modular sequence space (RFC 1982).
//   2. A retransmit table indexed by sequence, with exponential-backoff
//      timers: a timeout measured from the link (RFC 6298), backed off for
//      every registration that follows a timeout until a sample lands, and
//      climbing per entry from the measured value up to a configurable
//      ceiling.
//   3. Inbound dedup over a fixed-size sliding window so a packet that
//      crosses the wire twice (loss + retransmit, or routing duplication)
//      is delivered to the application exactly once.
//   4. ACK accounting that clears one retransmit entry per gateway DataAck.
//      The gateway acknowledges each reliable frame individually — echoing
//      that frame's own arq_seq with no notion of contiguity — so the
//      outbound table clears the single matching entry and leaves any gap
//      intact for retransmit.  A separate highest-contiguous-ACK accessor
//      over the inbound window feeds piggyback acknowledgement in the
//      receive direction.
//
// What this is NOT:
//
//   • A SACK range/bitmap acknowledging arbitrary spans in one frame.  The
//     gateway's per-frame DataAck makes single-sequence clearing the exact
//     match for the wire protocol, and the SDK's payloads are small
//     (≤ 1.4 KB) and strictly ordered.  Control frames are rare; variable
//     flushes are not — one per dirty component per tick — which is why the
//     window is shared by class below rather than by arrival.
//
// On-wire integration:
//
//   The ARQ sequence IS wired into the on-wire format — it is carried in a
//   dedicated 4-byte sub-header emitted under FLAG_RELIABLE (see
//   NetworkManager.AeadPipeline), independent of the AEAD nonce counter,
//   and the gateway acknowledges it with DataAck (0x11).  ARQ activates
//   only when the caller requests reliable delivery, NetworkSettings.
//   EmitArqSequence is true, and the session negotiated the CAP_ARQ_ACK
//   capability; otherwise the send downgrades to a single best-effort
//   transmission.
//
// All operations are O(1) expected.  Allocation-free after construction
// for the common-case small in-flight window.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// The class of a reliable frame, which decides how it shares the window of
    /// unacknowledged frames (<see cref="ReliableChannel.MaxInFlight"/>).
    /// </summary>
    public enum ReliableTraffic
    {
        /// <summary>
        /// A frame that must be delivered, such as a spawn, a despawn, an RPC, or a
        /// room or ownership operation. No later frame replaces its content. It may
        /// take any free slot of the window.
        /// </summary>
        Control = 0,

        /// <summary>
        /// A frame that describes current state, such as a NetworkVariable flush,
        /// and can wait: if it is refused, its variables stay dirty and go out with
        /// a later flush. It may take a slot only while more than
        /// <see cref="ReliableChannel.ReservedForControl"/> slots are free, so state
        /// cannot fill the window needed by control frames.
        /// </summary>
        Replication = 1,
    }

    /// <summary>
    /// Retransmission and duplicate-detection state for reliable frames: outbound
    /// sequence numbers, the table of unacknowledged frames with their
    /// retransmission timers, and a window that detects duplicate inbound frames.
    /// </summary>
    /// <remarks>
    /// <see cref="NetworkManager"/> keeps its own instance and publishes its
    /// readings, for example <see cref="NetworkManager.ReliableRtoSeconds"/>; you do
    /// not need to create one.
    /// </remarks>
    public sealed class ReliableChannel
    {
        // ── Configuration ──────────────────────────────────────────────────────

        /// <summary>
        /// The most unacknowledged frames tracked at once: 64. When the table is
        /// full, <see cref="TryRegisterOutbound(byte[], double, out uint)"/> returns
        /// <see langword="false"/>.
        /// </summary>
        public const int MaxInFlight = 64;

        /// <summary>
        /// The slots of the window that <see cref="ReliableTraffic.Replication"/>
        /// frames never take, kept for <see cref="ReliableTraffic.Control"/> frames:
        /// 16, a quarter of the window.
        /// </summary>
        /// <remarks>
        /// A replication flush refused at its ceiling is deferred, not lost. A
        /// control frame refused because the whole window is full is sent once
        /// without retransmission.
        /// </remarks>
        public const int ReservedForControl = 16;

        /// <summary>
        /// The most replication frames that may be in flight at once:
        /// <see cref="MaxInFlight"/> less <see cref="ReservedForControl"/>.
        /// </summary>
        public const int ReplicationInFlightCeiling = MaxInFlight - ReservedForControl;

        /// <summary>
        /// The size of the inbound duplicate-detection window, in sequence numbers:
        /// 1024. A frame whose sequence is older than the window is treated as a
        /// duplicate and refused.
        /// </summary>
        public const int DedupWindowSize = 1024;

        // Lower bound on the per-frame RTO.  Anything smaller would let a
        // pathological 0 / negative / NaN setter assignment collapse the
        // retransmit timer into a tight resend loop, exhausting the
        // outbound socket buffer before any ACK can arrive.
        private const float MinRtoSeconds = 0.01f;

        // Upper bound on the per-frame RTO.  Above the test ceiling the
        // retransmit cadence becomes indistinguishable from "no retransmit"
        // for the realtime gameplay window the SDK targets.
        private const float MaxRtoCeilingSeconds = 60f;

        // Lower / upper bounds on the retransmit attempt cap.  A negative
        // value would silently disable retransmission; values above the
        // ceiling exceed any plausible RTT × MaxRto budget.
        private const int MinMaxAttempts = 1;
        private const int MaxAttemptsCeiling = 64;

        private float _initialRtoSeconds = 0.2f;
        private float _maxRtoSeconds     = 2.0f;
        private int   _maxAttempts       = 8;

        // ── Round-trip estimate ────────────────────────────────────────────
        //
        // The retransmit timeout follows the link.  A fixed 200 ms first rung
        // on a link whose round trip is 250 ms retransmits every frame once
        // before its acknowledgement can arrive — and a retransmit is a frame
        // sealed afresh under a new nonce, so the gateway can tell it from the
        // original only by its arq_seq.  The gateway holds a per-session
        // window over that now and routes each sequence once; the estimate
        // here is the sender's half of the same repair, so that the ordinary
        // case is no retransmit at all.
        //
        // RFC 6298 over the acknowledgements: the first sample seeds SRTT and
        // RTTVAR, later samples move them by 1/8 and 1/4, and the timeout is
        // SRTT + max(G, 4·RTTVAR), never under the floor and never over
        // MaxRto.  G is the clock's granularity — here the frame, since an
        // acknowledgement is read once a frame — and it is what keeps the
        // timeout above the estimate on a steady link, where RTTVAR decays
        // toward nothing and an acknowledgement one frame late would
        // otherwise re-send every entry registered that tick.  A sample is
        // taken only from an entry acknowledged on its FIRST send (Karn's
        // rule): an acknowledgement of a retransmitted entry may be answering
        // either copy, and its round trip is not a measurement.
        //
        // And a timeout backs the timeout off (RFC 6298 §5.5): every
        // registration that follows one waits twice as long, until a sample
        // lands and the estimate takes over.  Karn's rule alone cannot seed
        // the estimate on a link slower than the first rung — every entry
        // is re-sent before its acknowledgement arrives, so no entry is ever
        // acknowledged on its first send — and the backoff is what breaks
        // that: a registration under the doubled timeout outlives its round
        // trip and is the first clean sample.  Once per timeout of the
        // timeout in force, not once per expiring entry: the entries a slow
        // link has queued under one value all expire within a few frames of
        // one another, and each of those expiries reports the same fact.
        private const float RttGain      = 1f / 8f;
        private const float RttVarGain   = 1f / 4f;
        private const float RtoVarWeight = 4f;
        private const float RtoGranularitySeconds = 0.05f;
        // The floor: below one main-thread frame at the SDK's own cadence the
        // acknowledgement cannot have been processed, and the gateway
        // acknowledges in-process, so a floor above the frame is the whole of
        // the jitter allowance.
        private const float RtoFloorSeconds = 0.1f;
        // The backoff is capped by MaxRtoSeconds on the way out; this only
        // keeps the shift in range on a link that never answers.
        private const int RtoBackoffCeiling = 16;

        private float _srttSeconds;
        private float _rttVarSeconds;
        private bool  _hasRttSample;

        // The timeout in force is identified by an epoch: a timeout of an
        // entry registered under the current epoch backs the timeout off and
        // opens a new one; a sample opens a new one too, because the link has
        // answered within the value that entry was given.  An entry registered
        // under an earlier epoch that expires says nothing about the value in
        // force now — its own epoch was closed by a timeout that already backed
        // off, or by a sample that already answered.
        private int _rtoBackoffExponent;
        private int _rtoEpoch;

        /// <summary>
        /// The retransmission timeout, in seconds, given to the next registered
        /// frame.
        /// </summary>
        /// <remarks>
        /// Before the first round-trip sample it is <see cref="InitialRtoSeconds"/>.
        /// After it, it is the smoothed round trip plus the larger of four times its
        /// variation and 0.05 s, and at least 0.1 s. It doubles after each timeout
        /// until the next sample, and never exceeds <see cref="MaxRtoSeconds"/>.
        /// </remarks>
        public float CurrentRtoSeconds
        {
            get
            {
                float rto = BaseRtoSeconds * (float)(1 << _rtoBackoffExponent);
                if (rto > MaxRtoSeconds) rto = MaxRtoSeconds;
                return rto;
            }
        }

        /// <summary>
        /// The timeout the link itself warrants, before any backoff: the
        /// estimate once there is one, clamped between the floor and
        /// <see cref="MaxRtoSeconds"/>; <see cref="InitialRtoSeconds"/> until then.
        /// An entry's own retransmit ladder climbs from this, because that
        /// ladder IS the backoff for the entry it belongs to.
        /// </summary>
        private float BaseRtoSeconds
        {
            get
            {
                if (!_hasRttSample) return InitialRtoSeconds;
                float rto = _srttSeconds + Math.Max(RtoGranularitySeconds, RtoVarWeight * _rttVarSeconds);
                if (rto < RtoFloorSeconds) rto = RtoFloorSeconds;
                if (rto > MaxRtoSeconds)  rto = MaxRtoSeconds;
                return rto;
            }
        }

        /// <summary>
        /// The smoothed round trip measured from acknowledgements, in seconds, or 0
        /// before the first sample.
        /// </summary>
        public float SmoothedRttSeconds => _hasRttSample ? _srttSeconds : 0f;

        /// <summary>
        /// The retransmission timeout used before the first round-trip sample, in
        /// seconds. Default 0.2.
        /// </summary>
        /// <remarks>
        /// A value is clamped to 0.01–60; NaN and infinity become 0.01. Setting it
        /// above <see cref="MaxRtoSeconds"/> raises <see cref="MaxRtoSeconds"/> to
        /// the same value.
        /// </remarks>
        public float InitialRtoSeconds
        {
            get => _initialRtoSeconds;
            // Reject NaN / Infinity outright; clamp positive finite values
            // into the documented working range.  An out-of-range value
            // never silently wins — it is always observable as a clamped
            // read on the next get.  The exponential-backoff schedule
            // depends on Initial <= Max so a setter that pushes the floor
            // above the current ceiling lifts the ceiling along with it,
            // mirroring the symmetric guard on MaxRtoSeconds.
            set
            {
                float clamped = ClampRto(value, MinRtoSeconds, MaxRtoCeilingSeconds);
                _initialRtoSeconds = clamped;
                if (_maxRtoSeconds < clamped) _maxRtoSeconds = clamped;
            }
        }

        /// <summary>
        /// The upper bound on the retransmission timeout, in seconds. Default 2.
        /// </summary>
        /// <remarks>
        /// A value is clamped to 0.01–60 (NaN and infinity become 0.01), and to at
        /// least <see cref="InitialRtoSeconds"/>.
        /// </remarks>
        public float MaxRtoSeconds
        {
            get => _maxRtoSeconds;
            // The RTO ceiling must remain >= the RTO floor so the
            // exponential-backoff schedule never inverts; clamp up to the
            // initial RTO when an out-of-order setter assignment would
            // otherwise leave MaxRto < InitialRto.
            set
            {
                float clamped = ClampRto(value, MinRtoSeconds, MaxRtoCeilingSeconds);
                if (clamped < _initialRtoSeconds) clamped = _initialRtoSeconds;
                _maxRtoSeconds = clamped;
            }
        }

        /// <summary>
        /// How many times a frame is retransmitted before it is dropped. Default 8;
        /// a value is clamped to 1–64.
        /// </summary>
        public int MaxAttempts
        {
            get => _maxAttempts;
            set
            {
                if (value < MinMaxAttempts) value = MinMaxAttempts;
                else if (value > MaxAttemptsCeiling) value = MaxAttemptsCeiling;
                _maxAttempts = value;
            }
        }

        // Shared finite-and-clamped helper for the two RTO setters.  Keeping
        // the rule in one place ensures the InitialRto / MaxRto setters
        // always make the same finiteness decision.
        private static float ClampRto(float value, float lo, float hi)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return lo;
            if (value < lo) return lo;
            if (value > hi) return hi;
            return value;
        }

        // ── Outbound state ─────────────────────────────────────────────────────

        private struct OutboundEntry
        {
            public uint    Sequence;
            public byte[]  Payload;
            // ⛔ double, not float. These are ABSOLUTE readings of a clock that
            // counts process uptime, and a float holds ~0.5 s of resolution at
            // 4.19e6 s — 48.5 days. Past that `now + 0.2f == now`, so every
            // reliable frame's retransmit timer expires on the very next frame
            // and the ladder resends continuously (S4-56). The RTO values
            // themselves stay float: they are durations of a fraction of a
            // second, where float has resolution to spare.
            public double  NextSendAt;   // monotonic seconds (caller-supplied clock)
            public double  SentAt;       // the first send, for the round-trip sample
            public int     Attempts;
            public int     RtoEpoch;     // the timeout in force at registration
            public bool    InUse;
        }

        private readonly OutboundEntry[] _outbound = new OutboundEntry[MaxInFlight];
        private uint _nextOutboundSeq;
        private int  _outboundCount;

        // ── Inbound dedup state ────────────────────────────────────────────────
        //
        // A bitmap-backed sliding window.  The window's high watermark is
        // _highestSeenSeq; bit i represents (highestSeen - i).  An incoming
        // sequence is accepted iff it is strictly greater than the high
        // watermark, OR it falls within the window and its bit is unset.

        private readonly ulong[] _dedupBitmap = new ulong[DedupWindowSize / 64];
        private uint _highestSeenSeq;
        private bool _hasInbound;

        // ── Outbound API ───────────────────────────────────────────────────────

        /// <summary>The number of unacknowledged outbound frames currently tracked.</summary>
        public int InFlightCount => _outboundCount;

        /// <summary>
        /// Control frames refused because the whole window was full. The SDK sends
        /// such a frame once, without retransmission. Reset by <see cref="Reset"/>.
        /// </summary>
        public long ControlRefusedCount { get; private set; }

        /// <summary>
        /// Replication frames refused at <see cref="ReplicationInFlightCeiling"/> and
        /// deferred to a later flush. Nothing is lost: the variables stay dirty.
        /// Reset by <see cref="Reset"/>.
        /// </summary>
        public long ReplicationDeferredCount { get; private set; }

        /// <summary>
        /// Replication frames sent once, without retransmission, because replication's
        /// share of the window was in use and the frame could not wait. Reset by
        /// <see cref="Reset"/>.
        /// </summary>
        public long ReplicationDowngradedCount { get; private set; }

        /// <summary>
        /// Frames sent again by <see cref="Tick(double, Action{uint, byte[]}, Action{uint})"/>.
        /// A resend that throws is not counted. Reset by <see cref="Reset"/>.
        /// </summary>
        public long RetransmitCount { get; private set; }

        /// <summary>
        /// A flush asked whether the share was open before building its frame,
        /// was told no, and deferred: counted here, because the refusal never
        /// reached <see cref="TryRegisterOutbound"/> — on the main thread the
        /// probe is authoritative, so in steady state every deferral is one of
        /// these and none is a refused registration.
        /// </summary>
        internal void NoteReplicationDeferral() => ReplicationDeferredCount++;

        /// <summary>
        /// The caller shipped the replication frame the registration just
        /// refused, once and unregistered: that refusal — counted as a
        /// deferral by <see cref="TryRegisterOutbound"/> the statement before
        /// — was not a deferral after all, and moves.  Paired with a refused
        /// registration only, never with <see cref="NoteReplicationDeferral"/>.
        /// </summary>
        internal void NoteReplicationDowngrade()
        {
            if (ReplicationDeferredCount > 0) ReplicationDeferredCount--;
            ReplicationDowngradedCount++;
        }

        /// <summary>
        /// Whether a frame of class <paramref name="traffic"/> would be registered
        /// now. The answer holds only for this instant.
        /// </summary>
        /// <remarks>
        /// Both limits count the frames of both classes: replication is refused once
        /// <see cref="ReplicationInFlightCeiling"/> frames are in flight, control once
        /// <see cref="MaxInFlight"/> are.
        /// </remarks>
        /// <param name="traffic">The class of the frame.</param>
        /// <returns><see langword="true"/> when a frame of the class would be registered.</returns>
        public bool CanRegister(ReliableTraffic traffic) => _outboundCount < CeilingFor(traffic);

        private static int CeilingFor(ReliableTraffic traffic) =>
            traffic == ReliableTraffic.Replication ? ReplicationInFlightCeiling : MaxInFlight;

        /// <summary>
        /// Allocates the next outbound sequence number without registering a
        /// retransmission entry.
        /// </summary>
        /// <remarks>
        /// Use <see cref="TryRegisterOutbound(byte[], double, out uint)"/> when the
        /// frame should also be retransmitted.
        /// </remarks>
        /// <returns>The sequence number.</returns>
        public uint AllocateOutboundSequence() => _nextOutboundSeq++;

        /// <summary>
        /// Allocates the next outbound sequence number and registers
        /// <paramref name="payload"/> for retransmission, as a
        /// <see cref="ReliableTraffic.Control"/> frame.
        /// </summary>
        /// <remarks>
        /// Send the frame right after registering it. The retransmission timer starts
        /// at <paramref name="nowSeconds"/>, which must come from the same clock as
        /// the one passed to <see cref="Tick(double, Action{uint, byte[]}, Action{uint})"/>.
        /// </remarks>
        /// <param name="payload">The frame to retransmit until it is acknowledged.</param>
        /// <param name="nowSeconds">The current time, in seconds, on a monotonic clock.</param>
        /// <param name="sequence">The sequence number allocated, or 0 when the frame was refused.</param>
        /// <returns>
        /// <see langword="true"/> when the frame was registered;
        /// <see langword="false"/> when the table is full.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
        public bool TryRegisterOutbound(byte[] payload, double nowSeconds, out uint sequence)
            => TryRegisterOutbound(payload, nowSeconds, out sequence, ReliableTraffic.Control);

        /// <summary>
        /// <see cref="TryRegisterOutbound(byte[], double, out uint)"/> for a
        /// <see langword="float"/> clock.
        /// </summary>
        /// <remarks>
        /// A <see langword="float"/> reading of process uptime loses the precision
        /// the timers need after about 48 days; prefer the
        /// <see langword="double"/> overload.
        /// </remarks>
        /// <param name="payload">The frame to retransmit until it is acknowledged.</param>
        /// <param name="nowSeconds">The current time, in seconds, on a monotonic clock.</param>
        /// <param name="sequence">The sequence number allocated, or 0 when the frame was refused.</param>
        /// <returns>
        /// <see langword="true"/> when the frame was registered;
        /// <see langword="false"/> when the table is full.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
        public bool TryRegisterOutbound(byte[] payload, float nowSeconds, out uint sequence)
            => TryRegisterOutbound(payload, (double)nowSeconds, out sequence, ReliableTraffic.Control);

        /// <summary>
        /// <see cref="TryRegisterOutbound(byte[], double, out uint, ReliableTraffic)"/>
        /// for a <see langword="float"/> clock.
        /// </summary>
        /// <param name="payload">The frame to retransmit until it is acknowledged.</param>
        /// <param name="nowSeconds">The current time, in seconds, on a monotonic clock.</param>
        /// <param name="sequence">The sequence number allocated, or 0 when the frame was refused.</param>
        /// <param name="traffic">The class of the frame.</param>
        /// <returns><see langword="true"/> when the frame was registered.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
        public bool TryRegisterOutbound(
            byte[] payload, float nowSeconds, out uint sequence, ReliableTraffic traffic)
            => TryRegisterOutbound(payload, (double)nowSeconds, out sequence, traffic);

        /// <summary>
        /// <see cref="TryRegisterOutbound(byte[], double, out uint)"/> for a frame of
        /// the given class.
        /// </summary>
        /// <remarks>
        /// A replication frame is refused while <see cref="ReservedForControl"/> or
        /// fewer slots are free, and counted in <see cref="ReplicationDeferredCount"/>;
        /// a control frame is refused only when no slot is free, and counted in
        /// <see cref="ControlRefusedCount"/>.
        /// </remarks>
        /// <param name="payload">The frame to retransmit until it is acknowledged.</param>
        /// <param name="nowSeconds">The current time, in seconds, on a monotonic clock.</param>
        /// <param name="sequence">The sequence number allocated, or 0 when the frame was refused.</param>
        /// <param name="traffic">The class of the frame.</param>
        /// <returns><see langword="true"/> when the frame was registered.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
        public bool TryRegisterOutbound(
            byte[] payload, double nowSeconds, out uint sequence, ReliableTraffic traffic)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            if (_outboundCount >= CeilingFor(traffic))
            {
                if (traffic == ReliableTraffic.Replication) ReplicationDeferredCount++;
                else ControlRefusedCount++;
                sequence = 0u;
                return false;
            }

            int slot = FindFreeSlot();
            sequence = _nextOutboundSeq++;
            _outbound[slot] = new OutboundEntry
            {
                Sequence   = sequence,
                Payload    = payload,
                NextSendAt = nowSeconds + CurrentRtoSeconds,
                SentAt     = nowSeconds,
                Attempts   = 1,
                RtoEpoch   = _rtoEpoch,
                InUse      = true,
            };
            _outboundCount++;
            return true;
        }

        /// <summary>
        /// Clears the retransmission entry of the one frame an acknowledgement
        /// names, without taking a round-trip sample.
        /// </summary>
        /// <remarks>
        /// An acknowledgement covers that frame alone, never lower-numbered frames,
        /// so the other entries keep their timers.
        /// </remarks>
        /// <param name="sequence">The acknowledged sequence number.</param>
        /// <returns>
        /// 1 when an entry was cleared; 0 when none matched, for example a duplicate
        /// acknowledgement.
        /// </returns>
        public int Acknowledge(uint sequence) => Acknowledge(sequence, float.NaN);

        /// <summary>
        /// Clears the retransmission entry <paramref name="sequence"/> names and, when
        /// the frame was acknowledged without having been retransmitted, takes the
        /// time since it was sent as a round-trip sample.
        /// </summary>
        /// <param name="sequence">The acknowledged sequence number.</param>
        /// <param name="nowSeconds">
        /// The current time, in seconds, on the clock the frame was registered with;
        /// NaN clears the entry without taking a sample.
        /// </param>
        /// <returns>1 when an entry was cleared; 0 when none matched.</returns>
        public int Acknowledge(uint sequence, float nowSeconds)
            => Acknowledge(sequence, (double)nowSeconds);

        /// <summary>
        /// <see cref="Acknowledge(uint, float)"/> for a <see langword="double"/> clock.
        /// </summary>
        /// <param name="sequence">The acknowledged sequence number.</param>
        /// <param name="nowSeconds">
        /// The current time, in seconds, on the clock the frame was registered with;
        /// NaN clears the entry without taking a sample.
        /// </param>
        /// <returns>1 when an entry was cleared; 0 when none matched.</returns>
        public int Acknowledge(uint sequence, double nowSeconds)
        {
            for (int i = 0; i < _outbound.Length; i++)
            {
                if (!_outbound[i].InUse) continue;
                if (_outbound[i].Sequence == sequence)
                {
                    if (_outbound[i].Attempts == 1 && !double.IsNaN(nowSeconds))
                        SampleRoundTrip((float)(nowSeconds - _outbound[i].SentAt));
                    _outbound[i] = default;
                    _outboundCount--;
                    return 1;
                }
            }
            return 0;
        }

        private void SampleRoundTrip(float rttSeconds)
        {
            if (float.IsNaN(rttSeconds) || float.IsInfinity(rttSeconds) || rttSeconds < 0f) return;

            // The link has answered: the timeout follows the estimate again,
            // and a timeout of an entry registered under the backed-off value
            // says nothing about the value in force now.
            _rtoBackoffExponent = 0;
            _rtoEpoch++;

            if (!_hasRttSample)
            {
                _srttSeconds   = rttSeconds;
                _rttVarSeconds = rttSeconds / 2f;
                _hasRttSample  = true;
                return;
            }
            float deviation = Math.Abs(_srttSeconds - rttSeconds);
            _rttVarSeconds += RttVarGain * (deviation - _rttVarSeconds);
            _srttSeconds   += RttGain * (rttSeconds - _srttSeconds);
        }

        /// <summary>
        /// Calls <paramref name="resend"/> for every entry whose retransmission timer
        /// has expired, and drops the entries that have been retransmitted
        /// <see cref="MaxAttempts"/> times.
        /// </summary>
        /// <remarks>
        /// Each retransmission of an entry doubles its next interval, up to
        /// <see cref="MaxRtoSeconds"/>. A NaN or infinite <paramref name="nowSeconds"/>
        /// skips the call. An exception thrown by either callback is caught and
        /// logged, and the other entries are still processed.
        /// </remarks>
        /// <param name="nowSeconds">The current time, in seconds, on the clock the frames were registered with.</param>
        /// <param name="resend">Called with the sequence number and payload of each frame to send again.</param>
        /// <param name="onDropped">Called with the sequence number of each dropped frame; optional.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resend"/> is <see langword="null"/>.</exception>
        public void Tick(float nowSeconds, Action<uint, byte[]> resend, Action<uint> onDropped = null)
            => Tick((double)nowSeconds, resend, onDropped);

        /// <summary>
        /// <see cref="Tick(float, Action{uint, byte[]}, Action{uint})"/> for a
        /// <see langword="double"/> clock.
        /// </summary>
        /// <param name="nowSeconds">The current time, in seconds, on the clock the frames were registered with.</param>
        /// <param name="resend">Called with the sequence number and payload of each frame to send again.</param>
        /// <param name="onDropped">Called with the sequence number of each dropped frame; optional.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resend"/> is <see langword="null"/>.</exception>
        public void Tick(double nowSeconds, Action<uint, byte[]> resend, Action<uint> onDropped = null)
        {
            if (resend == null) throw new ArgumentNullException(nameof(resend));

            // Clamp pathological clock readings so a NaN / negative input
            // cannot stall the retransmit ladder for the rest of the
            // session.  A single bad sample is tolerated (skip this tick);
            // a steady stream of bad samples is the caller's bug to address.
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) return;

            for (int i = 0; i < _outbound.Length; i++)
            {
                ref OutboundEntry e = ref _outbound[i];
                if (!e.InUse) continue;
                if (nowSeconds < e.NextSendAt) continue;

                // Strict greater-than: TryRegisterOutbound seeds Attempts=1 to
                // represent the initial transmit, so the cap measured here is
                // the number of *retransmits* performed by Tick.  With "> "
                // semantics, MaxAttempts=8 yields exactly 8 retransmits before
                // the entry is dropped, matching the field name's contract.
                if (e.Attempts > MaxAttempts)
                {
                    uint dropped = e.Sequence;
                    e = default;
                    _outboundCount--;
                    // Subscriber-isolation: a buggy onDropped handler must
                    // not abort the per-tick sweep.  The entry has already
                    // been cleared, so a thrown exception leaves no
                    // book-keeping gap.
                    try { onDropped?.Invoke(dropped); }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError(
                            $"[RTMPE] ReliableChannel.Tick: onDropped threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Subscriber exception isolated.");
                    }
                    continue;
                }

                // Subscriber-isolation: a thrown resend (transport
                // disposed, send buffer full, etc.) must not abort the
                // sweep across siblings.  Increment attempts and reschedule
                // even on failure so the dropped-after-MaxAttempts ladder
                // still fires for an entry whose resend chronically fails.
                try
                {
                    resend(e.Sequence, e.Payload);
                    RetransmitCount++;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError(
                        $"[RTMPE] ReliableChannel.Tick: resend threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Continuing with backoff.");
                }
                e.Attempts++;

                // The first expiry under the timeout in force backs it off for
                // every registration that follows; the entries already queued
                // under that timeout expire on its heels and report nothing new.
                if (e.RtoEpoch == _rtoEpoch)
                {
                    _rtoEpoch++;
                    if (_rtoBackoffExponent < RtoBackoffCeiling) _rtoBackoffExponent++;
                }

                // The entry's own ladder climbs from the timeout the link
                // warrants — the measured one once there is a sample — and not
                // from the backed-off value, which is this ladder's own effect
                // on later registrations: an entry registered under a backoff
                // waits that value for its first rung and then 2×, 4×, 8× the
                // link's timeout, so a backlog queued during a silence is
                // retried at the link's pace once the silence ends rather than
                // at the pace the silence imposed.  Attempts is post-incremented
                // so the next interval is rto * 2^(attempts-1).  Clamp to
                // MaxRtoSeconds to keep long-stalled connections from
                // hibernating their retransmits.
                float interval = BaseRtoSeconds * (float)(1 << Math.Min(e.Attempts - 1, 16));
                if (interval > MaxRtoSeconds) interval = MaxRtoSeconds;
                e.NextSendAt = nowSeconds + interval;
            }
        }

        // ── Inbound API ────────────────────────────────────────────────────────

        /// <summary>
        /// Records an inbound sequence number and reports whether it is new.
        /// </summary>
        /// <param name="sequence">The sequence number of the inbound frame.</param>
        /// <returns>
        /// <see langword="true"/> when the sequence has not been seen and is not older
        /// than the window (<see cref="DedupWindowSize"/>): deliver the frame.
        /// <see langword="false"/> for a duplicate or a sequence older than the
        /// window.
        /// </returns>
        public bool TryAcceptInbound(uint sequence)
        {
            if (!_hasInbound)
            {
                _hasInbound      = true;
                _highestSeenSeq  = sequence;
                SetBit(0);
                return true;
            }

            int delta = (int)(sequence - _highestSeenSeq);

            if (delta > 0)
            {
                // Advance the window by `delta` slots.  Anything that falls
                // off the trailing edge is permanently considered "seen".
                ShiftWindow(delta);
                _highestSeenSeq = sequence;
                SetBit(0);
                return true;
            }

            int distance = -delta;
            if (distance >= DedupWindowSize)
            {
                // Far below the window — treat as stale duplicate / replay.
                return false;
            }

            if (TestBit(distance)) return false;
            SetBit(distance);
            return true;
        }

        /// <summary>
        /// The highest inbound sequence accepted so far, the head of the
        /// duplicate-detection window; 0 before the first (see
        /// <see cref="HasInbound"/>).
        /// </summary>
        public uint HighestSeenSequence => _highestSeenSeq;

        /// <summary>
        /// The highest sequence of the unbroken run of accepted sequences that starts
        /// at the oldest accepted sequence inside the window, or
        /// <see cref="HighestSeenSequence"/> when there is no gap; 0 before any
        /// inbound frame.
        /// </summary>
        public uint HighestContiguousAck
        {
            get
            {
                if (!_hasInbound) return 0;
                int max = _dedupBitmap.Length * 64;
                // Walk from the oldest tracked distance (max-1) toward the
                // head (distance 0). The first set bit we hit anchors the
                // start of a contiguous run; we then keep walking until a
                // cleared bit terminates the run. The cumulative-ACK seq is
                // the one immediately below the terminating gap, or the
                // head when no gap is found.
                int d = max - 1;
                while (d >= 0 && !TestBit(d)) d--;
                if (d < 0) return _highestSeenSeq;
                while (d >= 0 && TestBit(d)) d--;
                if (d < 0) return _highestSeenSeq;
                return unchecked(_highestSeenSeq - (uint)d - 1u);
            }
        }

        /// <summary>Whether at least one inbound frame has been accepted.</summary>
        public bool HasInbound => _hasInbound;

        // ── Session lifecycle ──────────────────────────────────────────────────

        /// <summary>
        /// Clears the session's state: the retransmission table, the sequence
        /// counter, the duplicate-detection window, the refusal and retransmission
        /// counts and the round-trip estimate.
        /// </summary>
        /// <remarks>
        /// <see cref="InitialRtoSeconds"/>, <see cref="MaxRtoSeconds"/> and
        /// <see cref="MaxAttempts"/> are kept. <see cref="NetworkManager"/> calls it
        /// at every session boundary, so no frame is retransmitted in a later
        /// session.
        /// </remarks>
        public void Reset()
        {
            Array.Clear(_outbound, 0, _outbound.Length);
            _nextOutboundSeq = 0u;
            _outboundCount   = 0;
            // The refusal counters are the session's: the advisory that
            // reports them says so, and a figure carried across sessions
            // would attribute one link's saturation to the next.
            ControlRefusedCount        = 0L;
            ReplicationDeferredCount   = 0L;
            ReplicationDowngradedCount = 0L;
            RetransmitCount            = 0L;

            Array.Clear(_dedupBitmap, 0, _dedupBitmap.Length);
            _highestSeenSeq  = 0u;
            _hasInbound      = false;

            // A new session is a new path until it is measured.
            _srttSeconds        = 0f;
            _rttVarSeconds      = 0f;
            _hasRttSample       = false;
            _rtoBackoffExponent = 0;
            _rtoEpoch           = 0;
        }

        // ── Test hooks ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the next unallocated outbound sequence — exposed for tests.
        /// </summary>
        internal uint NextOutboundSequence => _nextOutboundSeq;

        /// <summary>Test-only seed for the outbound sequence counter.</summary>
        internal void SeedOutboundSequence(uint seed) => _nextOutboundSeq = seed;

        // ── Helpers ────────────────────────────────────────────────────────────

        private int FindFreeSlot()
        {
            // Linear scan — MaxInFlight is small (64) and the table is
            // typically sparse during steady-state operation.
            for (int i = 0; i < _outbound.Length; i++)
                if (!_outbound[i].InUse) return i;
            // Should be unreachable thanks to the saturation check in
            // TryRegisterOutbound; throwing here surfaces a SDK invariant
            // violation rather than silently overwriting an in-flight entry.
            throw new InvalidOperationException("ReliableChannel: in-flight table full");
        }

        private void ShiftWindow(int delta)
        {
            if (delta >= DedupWindowSize)
            {
                Array.Clear(_dedupBitmap, 0, _dedupBitmap.Length);
                return;
            }

            int wholeWords = delta / 64;
            int bitShift   = delta % 64;

            // Shift LEFT (toward older bits) so the newest sample sits at
            // bit index 0 of the bitmap word at index 0.  This is the
            // conventional sliding-window encoding (older entries fall off
            // the trailing edge as they pass under the window).
            if (wholeWords > 0)
            {
                for (int i = _dedupBitmap.Length - 1; i >= 0; i--)
                {
                    int src = i - wholeWords;
                    _dedupBitmap[i] = src >= 0 ? _dedupBitmap[src] : 0UL;
                }
            }
            if (bitShift > 0)
            {
                ulong carry = 0UL;
                for (int i = 0; i < _dedupBitmap.Length; i++)
                {
                    ulong w = _dedupBitmap[i];
                    _dedupBitmap[i] = (w << bitShift) | carry;
                    carry = bitShift == 0 ? 0UL : w >> (64 - bitShift);
                }
            }
        }

        private void SetBit(int distance)
        {
            int word = distance >> 6;
            int bit  = distance & 0x3F;
            _dedupBitmap[word] |= 1UL << bit;
        }

        private bool TestBit(int distance)
        {
            int word = distance >> 6;
            int bit  = distance & 0x3F;
            return (_dedupBitmap[word] & (1UL << bit)) != 0UL;
        }
    }
}
