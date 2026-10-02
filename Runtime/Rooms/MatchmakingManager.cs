// RTMPE SDK — Runtime/Rooms/MatchmakingManager.cs
//
// High-level AutoJoinOrCreate API.
// Created by NetworkManager and receives MatchmakingResponse (0x2B) callbacks.
//
// Threading model:
//   • All public methods MUST be called from the Unity main thread.
//   • HandleMatchmakingResponse() is called from NetworkManager.ProcessPacket()
//     which runs on the main thread (via MainThreadDispatcher).
//   • Tick(double) MUST be called from the Unity main thread (typically
//     NetworkManager.Update()).  If never called, timeout enforcement is
//     simply disabled — Cancel and once-only-delivery gates still work.
//
// Reliability guarantees:
//   • Once-only callback delivery — exactly one of OnMatchmakingComplete /
//     OnMatchmakingFailed / OnMatchmakingCancelled / OnMatchmakingTimedOut
//     fires per StartMatchmaking call, even if the server retries the
//     response or the client cancels mid-flight.
//   • Cancel — CancelFindMatch() drops any in-flight request silently;
//     subsequent server responses for that request are discarded.  Fires
//     OnMatchmakingCancelled exactly once.
//   • Timeout — when configured, fires OnMatchmakingTimedOut and latches
//     the request so a late server response is ignored.
//   • Correlation — every request carries a client-generated request_id and
//     the gateway echoes it.  A reply that names a DIFFERENT request is
//     discarded without spending the latch: it answers an attempt this client
//     has already cancelled or timed out, and the one now outstanding is still
//     owed an answer of its own.  A reply that names none is matched against
//     whatever is outstanding, which is what every client did before the
//     gateway echoed anything.

using System;
using System.Text;
using RTMPE.Core;
using RTMPE.Protocol;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Join-or-create matchmaking: the server places the player in an open room with the same
    /// mode and lobby, or creates one. Get it from <see cref="NetworkManager.Matchmaking"/>.
    /// </summary>
    /// <remarks>
    /// <para>Each request ends in exactly one of <see cref="OnMatchmakingComplete"/>,
    /// <see cref="OnMatchmakingFailed"/>, <see cref="OnMatchmakingCancelled"/> and
    /// <see cref="OnMatchmakingTimedOut"/>.</para>
    /// <para>Call its members from the main thread; its events are raised on the main thread.
    /// A new <see cref="MatchmakingManager"/> is built on every <c>Connect()</c> and every
    /// reconnect attempt; its events keep their subscribers.</para>
    /// </remarks>
    public sealed class MatchmakingManager
    {
        // Strict UTF-8 codec.  The default Encoding.UTF8 silently substitutes
        // U+FFFD for malformed sequences — a hostile gateway can use that to
        // smuggle bytes through `room_id` / `room_code` / `error` strings
        // that survive parse but mutate downstream string-equality
        // invariants (e.g., a JoinRoom reply matched against a U+FFFD-folded
        // RoomId).  Symmetric with M19-PROTO-04 / M19-RPC-04/05 / M18-UTF8-01.
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Default per-request timeout used when the caller does not pass an
        // explicit one.  30 s matches typical matchmaking SLA budgets and is
        // long enough to absorb a single server retry.
        internal const double DefaultTimeoutSeconds = 30.0;

        // Hard upper bound on the configurable timeout.  Caps absurd values
        // that would silently block UI flows on a stale request — a
        // matchmaking attempt that has not resolved in 5 minutes is
        // effectively a server fault and the application should restart it
        // with explicit user feedback.
        internal const double MaxTimeoutSeconds = 300.0;

        /// <summary>
        /// Longest mode key the Room Service stores, in UTF-8 bytes.  Mirrors
        /// <c>entities.MaxMatchmakingModeLen</c>, which mirrors the
        /// <c>matchmaking_mode VARCHAR(64)</c> column.
        /// </summary>
        internal const int MaxModeBytes = 64;

        /// <summary>
        /// The reason <see cref="OnMatchmakingFailed"/> carries for a request the
        /// session ended under.  A constant so a test can pin the whole string:
        /// this text is the only thing distinguishing "your session ended" from
        /// "the matchmaker refused you", and a substring match cannot tell one
        /// wording from the other.
        /// </summary>
        internal const string SessionEndedReport = "session ended";

        /// <summary>
        /// The platform ceiling on a room's capacity, mirroring
        /// <c>entities.MaxPlayersLimit</c>.  It is also the capacity the server
        /// applies to a request of zero or less (<c>entities.MaxPlayersDefault</c>).
        /// </summary>
        /// <remarks>
        /// The server resolves both directions silently and the matchmaking reply
        /// carries no capacity, so neither correction ever reaches the client.
        /// The requested figure is what sizes this SDK's inbound flood budget.
        /// </remarks>
        /// <remarks>
        /// Pointed at the one place the platform ceiling is stated on this
        /// side, rather than restated. A second copy of a server constant is
        /// how the two come to disagree, and the guard that holds these to the
        /// Room Service is exhaustive over <see cref="RoomFieldLimits"/> only —
        /// so a copy living here would be outside it.
        /// </remarks>
        internal const int MaxPlayersLimit = RoomFieldLimits.MaxPlayersLimit;

        /// <summary>
        /// The index of the first character <c>entities.firstUnsafeRune</c> would
        /// reject, or -1.
        /// </summary>
        /// <remarks>
        /// The ranges are the Go helper's, transcribed: C0 and C1 controls, the
        /// zero-width and bidi formatting block, the deprecated formatting
        /// characters, and the byte-order mark.  Surrogates are not decoded — a
        /// lone or paired surrogate is outside every range below, and the
        /// characters this excludes are all in the BMP.
        /// </remarks>
        internal static int FirstUnsafeCharacter(string s)
            => RoomFieldLimits.FirstUnsafeCharacterIndex(s);

        /// <summary>
        /// The capacity the Room Service will apply to <paramref name="requested"/>.
        /// </summary>
        internal static int ResolveServerCapacity(int requested)
        {
            if (requested <= 0) return MaxPlayersLimit;   // the server's default
            return requested > MaxPlayersLimit ? MaxPlayersLimit : requested;
        }

        private readonly PacketBuilder  _builder;
        private readonly Action<byte[]> _sendPacket;
        private readonly Func<NetworkState> _getState;

        // Player identity provided by NetworkManager at construction time.
        // Sent as the player_id field in every MatchmakingRequest so the Room
        // Service can record roster membership atomically.
        private readonly Func<string> _getPlayerId;

        // Optional callback to record the server-derived player_id echoed in a
        // matchmaking reply (A5-2), mirroring the JoinRoom path.  Null in unit
        // tests that do not exercise identity propagation.
        private readonly Action<string> _setPlayerId;

        // Optional callback that adopts the matched room into client state.
        // Matchmaking finds-or-creates AND seats the player in a single
        // server-side transaction, so — unlike a bare CreateRoom — the client
        // must NOT issue a second JoinRoom (that would collide on the seat the
        // server already holds).  Instead it surfaces the assignment through
        // this callback, which drives the same room-entry path a join takes so
        // the session reaches the InRoom state and inbound room traffic is no
        // longer rejected.  Null in unit tests that exercise the manager in
        // isolation from the RoomManager.
        private readonly Action<MatchmadeRoom> _enterMatchedRoom;

        // Current in-flight request — null when no matchmaking is pending.
        // The sentinel doubles as the once-only-delivery latch: any inbound
        // response or cancel/timeout transition first checks this is non-null
        // and atomically clears it before invoking callbacks, so a duplicate
        // server response (gateway retry, replay) cannot fire the callback
        // a second time.
        private PendingRequest _pending;

        // A request the session ended under, retired here and not yet reported.
        //
        // The report is NOT raised where the debt is recorded.  ClearState runs
        // inside NetworkManager's teardown chokepoint, so a subscriber reached
        // from there would run application code — a retry, a Disconnect, a scene
        // load — in the middle of tearing the session down; Tick is the sole
        // reporter for the same reason the abandoned lobby query is, and the debt
        // is cleared before the raise so a subscriber that starts a fresh request
        // cannot have it answered by this one.
        private bool _sessionEndedUnreported;

        // One-second gates for the two lines an inbound frame can drive here.
        //
        // The first reports a fault of the SERVER's — a reply naming a request
        // this client is not holding — and that path deliberately does not
        // spend the latch, so nothing else bounds how often a server can drive
        // it.  The second reports a fault of the APPLICATION's, and the latch
        // does bound how often a server reaches it; what it does not bound is
        // the subscriber count, and the sibling reporter in RoomManager has
        // been gated since this class of defect was first measured.
        private long _lastForeignReplyWarnTicks;
        private static long s_lastSubscriberThrowWarnTicks;

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised when the server places the player in a room. The argument names the room.
        /// </summary>
        /// <remarks>
        /// The SDK has already entered the room: <see cref="RoomManager.OnRoomJoined"/> is
        /// raised just before this event.
        /// </remarks>
        public event Action<MatchmakingResult> OnMatchmakingComplete;

        /// <summary>
        /// Raised when the server refuses the request or its reply cannot be read. The argument
        /// is the reason.
        /// </summary>
        /// <remarks>
        /// Also raised with <c>"session ended"</c> when the connection closed or was
        /// re-established while the request was in progress.
        /// </remarks>
        public event Action<string> OnMatchmakingFailed;

        /// <summary>
        /// Raised when <see cref="CancelFindMatch"/> stops a request in progress.
        /// </summary>
        public event Action OnMatchmakingCancelled;

        /// <summary>
        /// Raised when the request's timeout elapses before the server answers.
        /// </summary>
        /// <remarks>
        /// A reply that arrives later raises no matchmaking event. If it places the player, the
        /// SDK can still enter that room and raise <see cref="RoomManager.OnRoomJoined"/>.
        /// </remarks>
        public event Action OnMatchmakingTimedOut;

        /// <summary>
        /// Take over the subscriber lists of the instance this one replaces.
        /// </summary>
        /// <remarks>
        /// A MatchmakingManager is rebuilt whenever the connection is. Every one
        /// of these four events reports the outcome of a request, so a dropped
        /// handler leaves a caller that can start matchmaking and never hear
        /// anything again — not the match, not the failure, not the timeout.
        /// <para>What this carries is the subscriber, not the request.  A
        /// rebuild discards the in-flight <c>_pending</c> along with the
        /// instance holding it; the handler is simply there for the next one.
        /// What that request is owed travels separately, through
        /// <see cref="AdoptUnreportedOutcomeFrom"/>, so that this body stays a
        /// guard and four carries and nothing else.</para>
        /// </remarks>
        internal void AdoptSubscribersFrom(MatchmakingManager previous)
        {
            if (previous == null || ReferenceEquals(previous, this)) return;

            OnMatchmakingComplete =
                SubscriberAdoption.Carry(OnMatchmakingComplete, previous.OnMatchmakingComplete);
            OnMatchmakingFailed =
                SubscriberAdoption.Carry(OnMatchmakingFailed, previous.OnMatchmakingFailed);
            OnMatchmakingCancelled =
                SubscriberAdoption.Carry(OnMatchmakingCancelled, previous.OnMatchmakingCancelled);
            OnMatchmakingTimedOut =
                SubscriberAdoption.Carry(OnMatchmakingTimedOut, previous.OnMatchmakingTimedOut);
        }

        /// <summary>
        /// Take over the session-ended report the instance this one replaces
        /// still owes, retiring whatever it had in flight.  The rebuild calls
        /// this beside <see cref="AdoptSubscribersFrom"/>.
        /// </summary>
        /// <remarks>
        /// A rebuild discards the in-flight <c>_pending</c> along with the
        /// instance holding it, and no matchmaking event will answer that
        /// request any more (a late seat reply is still adopted by the
        /// successor, which never knew the request and enters under no entry
        /// of its own).  The rebuild is reached
        /// synchronously from a <c>Reconnect()</c> issued inside
        /// <c>OnDisconnected</c> — the shipped connection bootstrap's own path —
        /// so no frame runs between the teardown and the replacement, and a
        /// report left on the replaced instance would never be raised.  Moved
        /// here, it is raised by this instance's next <see cref="Tick"/>, as the
        /// timeout is and for the same reason: never from inside a teardown or
        /// a rebuild, where a subscriber would run application code half-way
        /// through the session's own state change.
        /// </remarks>
        internal void AdoptUnreportedOutcomeFrom(MatchmakingManager previous)
        {
            if (previous == null || ReferenceEquals(previous, this)) return;

            if (previous.SurrenderUnreportedOutcome())
                _sessionEndedUnreported = true;
        }

        /// <summary>
        /// Retire whatever this instance still has in flight and hand over the
        /// session-ended report it owes, leaving nothing behind to raise.
        /// </summary>
        /// <returns><see langword="true"/> when a report is owed.</returns>
        private bool SurrenderUnreportedOutcome()
        {
            ClearState();
            bool owed = _sessionEndedUnreported;
            _sessionEndedUnreported = false;
            return owed;
        }

        /// <summary>Whether a request is in progress.</summary>
        public bool IsMatchmaking => _pending != null;

        /// <summary>
        /// A success reply arrived while no request was pending — a seat the
        /// server granted to a request this client cancelled or timed out —
        /// and was dropped.  Raised for the receive path, which holds the
        /// catch-up that ran ahead of it under whatever entry was begun since
        /// and can discard it now; see <c>RoomManager.OnEndedEntryAnswered</c>.
        /// </summary>
        internal event Action OnEndedEntryAnswered;

        // The sequence every room entry is begun on — shared with the room
        // manager, so a request begun here is ordered against a join or a
        // create begun there.  A request records the value it was begun with
        // and the seat it draws is entered under that value, whichever entries
        // were begun since; see RoomManager.CompletingEntry.
        private readonly RoomEntrySequence _entries;

        // The requests most recently retired — cancelled, timed out, or
        // answered — with the entry each was begun with, newest first.  A
        // reply that names one of them can still carry a seat, and the seat
        // is entered under the entry of the request that drew it.  Bounded
        // by count, not by time: a client that has retired more than this
        // many requests before the server answered the oldest has started
        // more matchmaking than any procedure this class describes, and the
        // seat that reply carries is then entered under None.
        private const int RetiredRequestsRemembered = 4;
        private readonly (string RequestId, int Entry)[] _retired =
            new (string, int)[RetiredRequestsRemembered];

        /// <summary>
        /// The entry the request in flight was begun with, or
        /// <see cref="RoomEntrySequence.None"/> while none is.  Exposed for the
        /// receive path's tests; the receive path itself reads the sequence.
        /// </summary>
        internal int PendingEntry => _pending?.Entry ?? RoomEntrySequence.None;

        // ── Constructor ──────────────────────────────────────────────────────────

        internal MatchmakingManager(
            PacketBuilder     builder,
            Action<byte[]>    sendPacket,
            Func<NetworkState> getState,
            Func<string>      getPlayerId,
            Action<string>    setPlayerId      = null,
            Action<MatchmadeRoom> enterMatchedRoom = null,
            RoomEntrySequence entries         = null)
        {
            _builder          = builder     ?? throw new ArgumentNullException(nameof(builder));
            _sendPacket       = sendPacket  ?? throw new ArgumentNullException(nameof(sendPacket));
            _getState         = getState    ?? throw new ArgumentNullException(nameof(getState));
            _getPlayerId      = getPlayerId ?? throw new ArgumentNullException(nameof(getPlayerId));
            _setPlayerId      = setPlayerId;      // optional — see field doc (A5-2)
            _enterMatchedRoom = enterMatchedRoom; // optional — see field doc
            _entries          = entries ?? new RoomEntrySequence();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Starts matchmaking with a 30-second timeout. See
        /// <see cref="StartMatchmaking(MatchmakingOptions, double)"/>.
        /// </summary>
        /// <param name="options">The mode, lobby and room options to match on.</param>
        public void StartMatchmaking(MatchmakingOptions options)
            => StartMatchmaking(options, DefaultTimeoutSeconds);

        /// <summary>
        /// Asks the server to place the player in an open room of your project with the same
        /// <see cref="MatchmakingOptions.Mode"/> and <see cref="MatchmakingOptions.LobbyName"/>,
        /// or to create one. The request ends in exactly one of
        /// <see cref="OnMatchmakingComplete"/>, <see cref="OnMatchmakingFailed"/>,
        /// <see cref="OnMatchmakingCancelled"/> and <see cref="OnMatchmakingTimedOut"/>.
        /// </summary>
        /// <param name="options">
        /// The mode, lobby and room options to match on. <see cref="MatchmakingOptions.Mode"/>
        /// is required.
        /// </param>
        /// <param name="timeoutSeconds">
        /// How long to wait for the server, in seconds. Values above 300 are reduced to 300,
        /// zero or negative values use 30, and <see cref="double.PositiveInfinity"/> means no
        /// timeout.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is
        /// <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <see cref="MatchmakingOptions.Mode"/> is empty, longer than 64 bytes of UTF-8 or
        /// contains a control or invisible formatting character;
        /// <see cref="MatchmakingOptions.LobbyName"/> breaks the <see cref="LobbyName"/> rules;
        /// <see cref="MatchmakingOptions.DisplayName"/> is longer than 32 characters or contains
        /// such a character; or <paramref name="timeoutSeconds"/> is NaN.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// This client is not <see cref="NetworkState.Connected"/> or
        /// <see cref="NetworkState.InRoom"/>, or a request is already in progress (call
        /// <see cref="CancelFindMatch"/> first).
        /// </exception>
        public void StartMatchmaking(MatchmakingOptions options, double timeoutSeconds)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.Mode))
                throw new ArgumentException("MatchmakingOptions.Mode must not be empty.", nameof(options));
            // The mode is both a search predicate and a stored column on the
            // server, so an over-long one fails asymmetrically there — the search
            // matches nothing and the create fails in the driver.  Bounded here so
            // the failure names the field instead of arriving as "no match".
            if (Encoding.UTF8.GetByteCount(options.Mode) > MaxModeBytes)
                throw new ArgumentException(
                    $"MatchmakingOptions.Mode must be at most {MaxModeBytes} UTF-8 bytes.",
                    nameof(options));
            // The server's rule is length AND character class; mirroring only the
            // length leaves the SDK sending modes it refuses.  The set is the one
            // entities.ValidateMatchmakingMode excludes: control characters and
            // the invisible formatting codepoints that make two distinct modes
            // look identical in a log or a dashboard.
            if (FirstUnsafeCharacter(options.Mode) >= 0)
                throw new ArgumentException(
                    "MatchmakingOptions.Mode contains a control or invisible formatting " +
                    $"character at offset {FirstUnsafeCharacter(options.Mode)}.",
                    nameof(options));
            // Optional on this path — an empty name means the matched room joins
            // no lobby — but a non-empty one is held to the same rule the Room
            // Service applies, so a name it would refuse is refused here rather
            // than costing the caller a matchmaking round trip.
            if (!string.IsNullOrEmpty(options.LobbyName)
                && !LobbyName.IsValid(options.LobbyName))
                throw new ArgumentException(
                    "MatchmakingOptions.LobbyName: " + LobbyName.Describe(options.LobbyName),
                    nameof(options));

            // The seat this request wins is taken under this name, and the Room
            // Service holds it to the same rule it holds a name carried by an
            // ordinary join. Refused here, the caller is told which of its own
            // fields was wrong; refused there, a matchmaking round trip ends in
            // a message about a display name the caller has to go and find.
            string displayNameProblem = RoomFieldLimits.ValidateDisplayName(options.DisplayName);
            if (displayNameProblem != null)
                throw new ArgumentException(
                    "MatchmakingOptions.DisplayName: " + displayNameProblem, nameof(options));

            var state = _getState();
            if (state != NetworkState.Connected && state != NetworkState.InRoom)
                throw new InvalidOperationException(
                    $"StartMatchmaking requires Connected or InRoom state; current state is {state}.");

            // Reject overlapping requests so the once-only-delivery latch can
            // remain a single PendingRequest.  Apps that want to switch
            // criteria mid-flight must Cancel first — explicit per design,
            // because silently overwriting a pending request would orphan its
            // callback path.
            if (_pending != null)
                throw new InvalidOperationException(
                    "StartMatchmaking: a matchmaking request is already in flight. " +
                    "Call CancelFindMatch() before issuing a new request.");

            // NaN must be rejected explicitly — the prior chain admits NaN
            // through the `!(timeoutSeconds > 0.0)` branch (which is true
            // for NaN) and silently substitutes the default timeout.
            // Silent coercion of pathological input across an API surface
            // is inconsistent with the matchmaking-options validator (which
            // throws ArgumentException for invalid options) and masks a
            // caller-side bug behind a 30-second wait.
            if (double.IsNaN(timeoutSeconds))
                throw new ArgumentException(
                    "timeoutSeconds must not be NaN.", nameof(timeoutSeconds));

            double effectiveTimeout;
            if (double.IsPositiveInfinity(timeoutSeconds))
                effectiveTimeout = double.PositiveInfinity;
            else if (!(timeoutSeconds > 0.0))
                effectiveTimeout = DefaultTimeoutSeconds;
            else if (timeoutSeconds > MaxTimeoutSeconds)
                effectiveTimeout = MaxTimeoutSeconds;
            else
                effectiveTimeout = timeoutSeconds;

            // Carry the capacity the server will actually apply, so the room
            // snapshot synthesised on success sizes the inbound flood budget to
            // the room rather than to the request.
            //
            // Both directions are resolved here because the reply carries no
            // capacity and nothing re-sizes the budget afterwards — the two call
            // sites of ConfigureForRoomSize both fire once, at room entry.  A
            // request above the ceiling is clamped down to it, and the documented
            // "≤ 0 means the server default" is resolved to that default: left
            // as 0 it sizes the budget for a solo room while the server seats the
            // player in one of a hundred.
            int budgetPlayers = ResolveServerCapacity(options.MaxPlayers);

            // 32 lowercase hex characters, no dashes — the same rendering the
            // RoomCreate correlation trailer uses, so one shape of id crosses
            // the wire for both operations.
            string requestId = Guid.NewGuid().ToString("N");
            _pending = new PendingRequest(effectiveTimeout, budgetPlayers, requestId, _entries.Begin());

            var payload = BuildMatchmakingPayload(options, _getPlayerId() ?? string.Empty, requestId);
            var packet  = _builder.Build(PacketType.MatchmakingRequest, PacketFlags.Reliable, payload);
            _sendPacket(packet);
        }

        /// <summary>
        /// Stops waiting for the request in progress and raises
        /// <see cref="OnMatchmakingCancelled"/>. Does nothing when no request is in progress.
        /// </summary>
        /// <remarks>
        /// The server is not told. A reply can still arrive for the request; it raises no
        /// matchmaking event, but if it places the player the SDK can still enter that room and
        /// raise <see cref="RoomManager.OnRoomJoined"/>.
        /// </remarks>
        public void CancelFindMatch()
        {
            // ConsumePending() flips the latch atomically (single-threaded
            // main-thread contract), so concurrent Cancel + late server
            // response cannot both fire callbacks.  No-op when nothing is
            // pending — a UX-driven Cancel from a "Find Match" button must
            // be safe to press repeatedly.
            var pending = ConsumePending();
            if (pending == null) return;
            SafeRaise(OnMatchmakingCancelled);
        }

        /// <summary>
        /// Retire the in-flight request because the session it was issued in has
        /// ended, recording the report for the next <see cref="Tick"/> to raise.
        /// </summary>
        /// <remarks>
        /// ⛔ The manager is normally discarded with the session, and that is the
        /// defect rather than the reason this is unnecessary: the instance holding
        /// <c>_pending</c> is replaced at the start of the next connection
        /// attempt, so a request outstanding when the link dropped reported
        /// NOTHING — while a manager that is not replaced (an explicit
        /// <c>Disconnect</c> leaves it in place) went on ticking and reported a
        /// timeout about a session that had already ended.  One silence and one
        /// lie, from the same missing teardown.  The replacement adopts the
        /// report through <see cref="AdoptUnreportedOutcomeFrom"/>.
        /// <para>
        /// The reported outcome is <see cref="OnMatchmakingFailed"/> with
        /// <see cref="SessionEndedReport"/>: the request neither succeeded, nor was
        /// cancelled by the player, nor ran out of time — it was issued into a
        /// session that no longer exists, which is a failure of the request and is
        /// the one terminal event that carries a reason the caller can read.
        /// </para>
        /// <para>
        /// Idempotent, and silent when nothing is pending: a session that ends with
        /// no request in flight owes no report, and calling this twice owes one.
        /// </para>
        /// </remarks>
        internal void ClearState()
        {
            if (ConsumePending() == null) return;
            _sessionEndedUnreported = true;
        }

        /// <summary>
        /// Advances the request's timeout, raising <see cref="OnMatchmakingTimedOut"/> when it
        /// elapses. <see cref="NetworkManager"/> calls it every frame; you do not need to.
        /// </summary>
        /// <param name="nowSeconds">The current time, in seconds, from a clock that does not go
        /// backwards, such as <c>Time.unscaledTimeAsDouble</c>.</param>
        public void Tick(double nowSeconds)
        {
            // Reject pathological clock readings.  A NaN nowSeconds would
            // make every comparison below return false (NaN < anything is
            // false; NaN < pending.DeadlineSeconds is false), causing the
            // timeout to fire on the next sane tick — but the deadline
            // captured here would be NaN too, and once captured a NaN
            // deadline would never compare strictly less than any future
            // sample, permanently deferring the timeout.  Skipping the
            // tick is the strictly safer behaviour.
            // The session-ended report is drained ahead of the clock guard
            // deliberately: it is owed for a request that is already retired, so
            // nothing about it depends on the reading this tick was handed, and a
            // host passing a pathological clock must not be the reason a caller
            // never hears what became of its request.
            if (_sessionEndedUnreported)
            {
                _sessionEndedUnreported = false;
                SafeRaise(OnMatchmakingFailed, SessionEndedReport);
                // Nothing can be pending: the debt is recorded only where the
                // pending request is consumed, and a subscriber reached above may
                // have started a fresh one whose deadline belongs to its own tick.
                return;
            }

            if (double.IsNaN(nowSeconds) || double.IsNegativeInfinity(nowSeconds)) return;

            var pending = _pending;
            if (pending == null) return;
            if (double.IsPositiveInfinity(pending.TimeoutSeconds)) return;

            // First Tick after StartMatchmaking captures the deadline.  We
            // intentionally avoid stamping the deadline inside StartMatchmaking
            // so the manager remains decoupled from any specific clock source
            // — a unit test can drive Tick with synthetic timestamps.
            if (!pending.DeadlineSet)
            {
                pending.DeadlineSeconds = nowSeconds + pending.TimeoutSeconds;
                pending.DeadlineSet     = true;
                return;
            }

            if (nowSeconds < pending.DeadlineSeconds) return;

            // Latch & fire — same protocol as Cancel/response paths.  A late
            // server response after timeout is silently discarded by
            // HandleMatchmakingResponse below because _pending is null.
            if (ConsumePending() == null) return;
            SafeRaise(OnMatchmakingTimedOut);
        }

        // ── Inbound packet handler ────────────────────────────────────────────

        /// <summary>
        /// Called by <see cref="NetworkManager"/> when a <c>MatchmakingResponse</c>
        /// (0x2B) arrives.  Parses the JSON payload and fires the appropriate event.
        /// </summary>
        internal void HandleMatchmakingResponse(byte[] payload)
        {
            // Once-only-delivery gate: if no request is pending the response
            // is either (a) a server retry of an already-fired response, (b)
            // an out-of-band response after Cancel, or (c) an out-of-band
            // response after Timeout.  Drop silently — callbacks already
            // fired, surfacing the duplicate would corrupt UI state.
            //
            // ⚠️ The latch is READ here and SPENT at each terminal path below,
            // rather than spent on arrival.  A reply has to be readable before
            // it can be asked which request it answers, and a reply answering a
            // request this client no longer holds must leave the latch alone —
            // the one outstanding is still owed an answer of its own.  Every
            // path that raises calls ConsumePending() immediately before it, so
            // AT MOST one of the four events fires per StartMatchmaking, as
            // before.
            //
            // ⛔ What did change is the other half: this is the first path that
            // can answer a reply with NO event, so "exactly one" now rests on
            // the deadline rather than on the arrival of any reply.  With the
            // default timeout that costs 30 s; with the documented
            // double.PositiveInfinity it costs for ever, which is what
            // disabling the deadline asks for — a reply that names another
            // request is, to this one, indistinguishable from no reply at all.
            var pending = _pending;
            if (pending == null)
            {
                // A reply with no request outstanding is a server retry of an
                // already-fired response, or a reply for an attempt this client
                // cancelled or timed out.  A SUCCESS among them is a seat the
                // gateway has already bound to this session — cancel never
                // reaches the server — so from Connected, the one state a
                // stranding is reached from, adopt it rather than leave the
                // session in a room it can neither see, leave, nor matchmake out
                // of until the lease expires (S4-08).  Entering the room
                // releases the catch-up staged under the retired request's entry
                // (the seat's own), and EnterMatchmadeRoom's same-room guard
                // makes a retry of the room already occupied a no-op, so no
                // arrival fires twice; the OnRoomJoined the entry raises is the
                // report, as the join door's late-reply path reports its own
                // (ROOM-RD-11).  AdoptSeat does not raise OnMatchmakingComplete,
                // so the cancelled/timed-out request keeps the single terminal
                // event it already fired.  When not Connected — already in a
                // room, where the buffer is empty, or gone — there is no
                // stranded seat, and the catch-up staged under the ended entry
                // is discarded ahead of the next entry's own, as before.
                if (TryReadGrantedSeat(payload, out var seat))
                {
                    if (_getState() == NetworkState.Connected)
                        AdoptSeat(seat.data, MaxPlayersLimit, EntryOfRetired(seat.request_id));
                    else
                        SafeRaise(OnEndedEntryAnswered);
                }
                return;
            }

            if (payload == null || payload.Length == 0)
            {
                ConsumePending();
                SafeRaise(OnMatchmakingFailed, "empty response");
                return;
            }

            string json;
            try
            {
                json = StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException)
            {
                ConsumePending();
                SafeRaise(OnMatchmakingFailed, "malformed response");
                return;
            }

            // JsonUtility is the parser used elsewhere in this SDK
            // (NetworkManager.JwtClaimsDto / JwtHeaderDto).  Switching off
            // the prior hand-rolled needle-search helpers eliminates the
            // "true_extra → true" / unescaped-backslash misclassifications
            // that the regex-style scan accepted.  Unknown fields are
            // ignored by JsonUtility, which matches our existing forward-
            // compatibility contract for gateway responses.
            MatchmakingResponseDto dto;
            try
            {
                dto = JsonUtility.FromJson<MatchmakingResponseDto>(json);
            }
            catch (Exception)
            {
                ConsumePending();
                SafeRaise(OnMatchmakingFailed, "malformed response");
                return;
            }

            if (dto == null)
            {
                ConsumePending();
                SafeRaise(OnMatchmakingFailed, "malformed response");
                return;
            }

            // The reply either names this request, names none, or names
            // another.  The third is not this request's answer: it belongs to
            // an attempt already cancelled or timed out, and the outstanding
            // one is still owed a reply of its own, so nothing here spends the
            // latch or raises a matchmaking outcome.
            if (!AnswersRequest(pending, dto.request_id))
            {
                // 🔴 Discarded for the LATCH, never for the SEAT.  A reply for a
                // retired attempt can be a SUCCESS, and the room it names is one
                // the server has already committed this session to — a seat is
                // unique per session, so dropping it whole strands the client in
                // a room it cannot see, cannot leave, and cannot matchmake out
                // of until the lease expires, while the request it is holding
                // fails on that very seat.  Cancel does not reach the server, so
                // "cancel, start another" — which this class's own
                // documentation prescribes for changing criteria — is enough to
                // produce one.
                //
                // ⛔ The budget is sized at the platform ceiling rather than
                // from the request in hand: this room was asked for by an
                // attempt whose capacity nobody kept, and under-sizing an
                // inbound budget is the harmful direction.
                if (dto.ok)
                    AdoptSeat(dto.data, MaxPlayersLimit, EntryOfRetired(dto.request_id));

                // ⚠️ Gated, and the value is not printed.  This path does not
                // spend the latch, so nothing else bounds how often a server
                // can drive it, and the id it names arrived over the wire.
                if (WarnGate.ShouldEmit(ref _lastForeignReplyWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] MatchmakingManager: a MatchmakingResponse named a request other " +
                        "than the one outstanding.  It answers an attempt this client has already " +
                        "cancelled or timed out, so it did not resolve the current request; any " +
                        "room it named has been adopted.");
                return;
            }

            ConsumePending();

            if (!dto.ok)
            {
                SafeRaise(OnMatchmakingFailed, string.IsNullOrEmpty(dto.error) ? "matchmaking failed" : dto.error);
                return;
            }

            string roomId   = string.Empty;
            string roomCode = string.Empty;
            bool   created  = false;
            if (dto.data != null)
            {
                roomId   = dto.data.room_id   ?? string.Empty;
                roomCode = dto.data.room_code ?? string.Empty;
                created  = dto.data.created;
            }

            // Adopt the room the server has already seated us in, transitioning
            // the session to InRoom BEFORE the completion event fires so a
            // subscriber that spawns from OnMatchmakingComplete (or the
            // OnRoomJoined this raises) observes an in-room session — without it
            // every inbound spawn / property packet is rejected as out-of-room.
            AdoptSeat(dto.data, pending.RequestedMaxPlayers, pending.Entry, answersARequestInFlight: true);

            SafeRaise(OnMatchmakingComplete, new MatchmakingResult(roomId, roomCode, created));
        }

        /// <summary>
        /// Take the room a reply announces, without treating that reply as the
        /// answer to whatever request is outstanding.
        /// </summary>
        /// <remarks>
        /// One body for both callers, because the two differ only in whether
        /// the reply also resolves the request: a seat the server has granted
        /// is a fact about the session, not about which request learned of it.
        /// <para>The player_id is resolved FIRST so ownership comparisons are
        /// valid the instant the room becomes visible.</para>
        /// <para><paramref name="entry"/> is the room entry the request that
        /// drew the seat was begun with: the room is entered under it, so the
        /// catch-up staged since that request began is released and what an
        /// earlier, ended entry staged is not.</para>
        /// </remarks>
        private void AdoptSeat(MatchmakingDataDto data, int budgetPlayers, int entry, bool answersARequestInFlight = false)
        {
            if (data == null) return;

            // A5-2: record the server-derived player_id (parity with JoinRoom) so
            // the SDK's local identity is correct after a matchmaking-only flow.
            // Previously it stayed empty, which also fed an empty player_id into
            // any subsequent MatchmakingRequest.
            string playerId = data.player_id ?? string.Empty;
            if (!string.IsNullOrEmpty(playerId))
                _setPlayerId?.Invoke(playerId);

            string roomId = data.room_id ?? string.Empty;
            if (string.IsNullOrEmpty(roomId)) return;

            // 🔑 The room outranks the request, in BOTH directions.  The server
            // decided which room this client sits in; its capacity is a fact
            // about that room, and `budgetPlayers` is only what this client
            // happened to ask for.  A rule that raised the budget and never
            // lowered it would be a rule about the number rather than about who
            // owns it.
            //
            // ⛔ Zero means the reply carried none — see `max_players` on the
            // DTO — so the request-derived figure stands, which is exactly what
            // every Room Service older than that field still produces.
            int seatedCapacity = data.max_players > 0 ? data.max_players : budgetPlayers;

            _enterMatchedRoom?.Invoke(new MatchmadeRoom(
                roomId, data.room_code ?? string.Empty, data.created, playerId,
                seatedCapacity, BuildRoster(data.players),
                data.properties_payload, data.properties_complete, entry, answersARequestInFlight));
        }

        // Whether a reply that resolves no request reports a granted seat, and
        // the parsed reply when it does, so the caller can adopt the seat it
        // names.  Read on the terms the resolving path reads, and refused on any
        // fault: a reply that cannot be read granted nothing this path can act
        // on.
        private static bool TryReadGrantedSeat(byte[] payload, out MatchmakingResponseDto dto)
        {
            dto = null;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                dto = JsonUtility.FromJson<MatchmakingResponseDto>(StrictUtf8.GetString(payload));
            }
            catch (Exception)
            {
                dto = null;
                return false;
            }
            return dto != null && dto.ok && dto.data != null && !string.IsNullOrEmpty(dto.data.room_id);
        }

        // The entry a retired request was begun with, for the seat its late
        // reply carries.  A request retired longer ago than the ring remembers
        // — or a reply naming one this client never sent — enters under None,
        // which releases everything staged: nothing says which entry those
        // frames belong to.  The seat is the session's either way; whether it
        // is entered is the room door's decision, which refuses one in the
        // room this client has since left.
        private int EntryOfRetired(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return RoomEntrySequence.None;
            for (int i = 0; i < _retired.Length; i++)
                if (string.Equals(_retired[i].RequestId, requestId, StringComparison.Ordinal))
                    return _retired[i].Entry;
            return RoomEntrySequence.None;
        }

        /// <summary>
        /// Whether a reply carrying <paramref name="echoedRequestId"/> answers
        /// the request <paramref name="pending"/> holds.
        /// </summary>
        /// <remarks>
        /// A reply naming nothing answers whatever is outstanding — that is
        /// what every client did before the gateway echoed anything, and it is
        /// what a gateway predating the member still gets.  ⛔ Returning false
        /// there instead would make an un-echoed reply wait out the full
        /// timeout, which is a worse failure than the one the echo closes.
        /// <para>Compared as the opaque string it is: the id this SDK writes is
        /// a 32-hex GUID, but a value that is not one names a request that is
        /// not this one, which is the same answer without a second format
        /// contract to keep.</para>
        /// </remarks>
        private static bool AnswersRequest(PendingRequest pending, string echoedRequestId)
        {
            if (string.IsNullOrEmpty(echoedRequestId)) return true;
            return string.Equals(echoedRequestId, pending.RequestId, StringComparison.Ordinal);
        }

        // Project the reply's roster DTOs into the lightweight MatchmadePlayer
        // carrier, skipping null entries so a malformed element cannot leave a
        // phantom seat in the count.  Returns null for an absent or empty roster,
        // which the RoomManager treats as "fall back to the self-seat".
        private static MatchmadePlayer[] BuildRoster(MatchmakingPlayerDto[] dtos)
        {
            if (dtos == null) return null;

            int seated = 0;
            for (int i = 0; i < dtos.Length; i++)
                if (dtos[i] != null) seated++;
            if (seated == 0) return null;

            var roster = new MatchmadePlayer[seated];
            int w = 0;
            for (int i = 0; i < dtos.Length; i++)
            {
                var pd = dtos[i];
                if (pd == null) continue;
                roster[w++] = new MatchmadePlayer(pd.player_id, pd.display_name, pd.is_host, pd.is_ready);
            }
            return roster;
        }

        // ── Response DTOs (consumed by JsonUtility.FromJson) ───────────────────

        // Public fields are required by JsonUtility's reflection-based binder.
        // Names mirror the gateway's snake_case wire schema verbatim.
        //
        // CS0649 is suppressed across the three shapes because the binder
        // assigns them reflectively: no assignment appears in source by design.
        // The suppression is scoped to these declarations so a genuinely
        // unassigned field anywhere else in the file still reports.
#pragma warning disable CS0649
        [Serializable]
        private sealed class MatchmakingResponseDto
        {
            public bool   ok;
            public string error;
            public MatchmakingDataDto data;

            // The correlation id the request carried, echoed by the gateway on
            // every reply it builds — the success, the Room Service's refusal,
            // and its own.  Absent on a gateway that predates the member, which
            // JsonUtility leaves null and AnswersRequest reads as "answers
            // whatever is outstanding".
            public string request_id;
        }

        [Serializable]
        private sealed class MatchmakingDataDto
        {
            public string room_id;
            public string room_code;
            public bool   created;
            public string player_id; // A5-2: server-derived authoritative id
            // Roster of the matchmade room (parity with the JoinRoom reply): the
            // occupants already seated when this client was placed, so a client
            // matchmade into an occupied room sees the full membership at once
            // instead of a roster of one that never back-fills.  Absent on a
            // pre-roster Room Service — JsonUtility leaves it null and adoption
            // falls back to the self-seat below.
            public MatchmakingPlayerDto[] players;

            // The room's property snapshot, as the canonical
            // `{"version":n,"properties":{…}}` document the RoomJoin response
            // carries in its trailing block.  A STRING rather than an object
            // because JsonUtility cannot bind a map; the SDK hands it to the
            // same decoder the other door uses.  Absent on a Room Service that
            // predates it — JsonUtility leaves it null and adoption proceeds
            // with no snapshot, exactly as before.
            public string properties_payload;

            // Whether properties_payload carries the room's whole map.
            public bool properties_complete;

            // The capacity of the room the server actually seated this client
            // in, which is NOT the capacity the request asked for.
            //
            // 🔑 `max_players` is not part of the server's matching predicate —
            // it appears only as the bound `player_count < max_players` — and
            // the search orders by `player_count DESC`, packing players into the
            // fullest room that will take them.  So a request for 8 is routed
            // into a 100-slot room by design, and until this field existed the
            // SDK had nothing to size its inbound budget from but its own ask
            // (`ROOM-RD-05`).
            //
            // ⛔ Absent on a Room Service that predates it, which JsonUtility
            // leaves at 0 — indistinguishable from a literal zero, and read the
            // same way: fall back to the request-derived figure.  A repair that
            // took 0 at face value would size every older deployment's budget
            // for a solo room, which is the defect with a wider blast radius
            // than the one being fixed.
            public int max_players;
        }

        // One roster entry from the matchmaking reply.  Mirrors the Room
        // Service's rosterPlayerWire; public fields are required by
        // JsonUtility's reflection-based binder.
        [Serializable]
        private sealed class MatchmakingPlayerDto
        {
            public string player_id;
            public string display_name;
            public bool   is_host;
            public bool   is_ready;
        }
#pragma warning restore CS0649

        // ── Latch ──────────────────────────────────────────────────────────────

        // Atomically detach the pending request and return it.  The "atomic"
        // qualifier here is the single-threaded main-thread contract — a
        // background thread MUST NOT call into MatchmakingManager.  This
        // method is the only place _pending becomes null after a successful
        // Start, so all four terminal paths (Complete / Failed / Cancelled /
        // TimedOut) funnel through it and the latch invariant holds.
        private PendingRequest ConsumePending()
        {
            var p = _pending;
            _pending = null;
            if (p != null)
            {
                // Remembered for the seat a late reply may still carry, newest
                // first; the oldest remembered request falls off the end.
                for (int i = _retired.Length - 1; i > 0; i--) _retired[i] = _retired[i - 1];
                _retired[0] = (p.RequestId, p.Entry);
            }
            return p;
        }

        // ── Pending-request record ─────────────────────────────────────────────

        // Sealed class instead of struct so the field-replace pattern in
        // ConsumePending is genuinely atomic on the main thread (an
        // assignment to a reference field is a single store).  A struct
        // would require Interlocked or risk a torn read on misaligned
        // platforms (e.g. 32-bit IL2CPP on older mobile).
        private sealed class PendingRequest
        {
            public readonly double TimeoutSeconds;
            public readonly int    RequestedMaxPlayers;

            /// <summary>
            /// The correlation id this request was sent under, as the 32-hex
            /// string it occupies on the wire.
            /// </summary>
            /// <remarks>
            /// Held as the string rather than the <see cref="Guid"/> it was
            /// generated from because the comparison it exists for is against
            /// whatever the gateway echoed, which is an opaque value this SDK
            /// wrote and is not obliged to be parseable — a reply naming
            /// something that is not a GUID names a request that is not this
            /// one, which is the same answer, reached without a second format
            /// contract nobody stated.
            /// </remarks>
            public readonly string RequestId;

            /// <summary>The room entry this request was begun with.</summary>
            public readonly int Entry;

            public bool   DeadlineSet;
            public double DeadlineSeconds;

            public PendingRequest(double timeoutSeconds, int requestedMaxPlayers, string requestId, int entry)
            {
                TimeoutSeconds      = timeoutSeconds;
                RequestedMaxPlayers = requestedMaxPlayers;
                RequestId           = requestId;
                Entry               = entry;
            }
        }

        // ── Room-adoption carrier ──────────────────────────────────────────────

        // The authoritative facts a matchmaking reply provides, handed to the
        // RoomManager so it can enter the already-seated room without a second
        // JoinRoom round-trip.  <see cref="Created"/> distinguishes the host (this
        // client created the room) from a guest (it joined an existing one);
        // <see cref="MaxPlayers"/> is the requested capacity used to size the
        // inbound budget; <see cref="Players"/> is the room's roster at seat time
        // (null on a pre-roster Room Service, where adoption falls back to the
        // self-seat and the membership stream back-fills the rest).
        internal readonly struct MatchmadeRoom
        {
            public readonly string RoomId;
            public readonly string RoomCode;
            public readonly bool   Created;
            public readonly string PlayerId;
            public readonly int    MaxPlayers;
            public readonly MatchmadePlayer[] Players;

            /// <summary>
            /// The room's property snapshot as the server authored it, or
            /// <see langword="null"/> when the reply carried none.  Matchmaking
            /// is the second door into an already-configured room, and a client
            /// arriving through it needs the property VERSION for the same
            /// reason a JoinRoom client does: without it every property write
            /// it makes is refused, with no reply, for the life of the room.
            /// </summary>
            public readonly string PropertiesPayload;

            /// <summary>Whether <see cref="PropertiesPayload"/> is the whole map.</summary>
            public readonly bool PropertiesComplete;

            /// <summary>
            /// The room entry the request that drew this seat was begun with,
            /// or <see cref="RoomEntrySequence.None"/> when nobody kept it.
            /// </summary>
            public readonly int Entry;

            /// <summary>
            /// Whether the reply carrying this seat answers the request in
            /// flight, as against a retired one — cancelled, timed out, or
            /// already answered and answered again by a retransmit.  The room
            /// door refuses a retired reply's seat in the room this client has
            /// since left; a request in flight is seated wherever the server
            /// put it.
            /// </summary>
            public readonly bool AnswersARequestInFlight;

            public MatchmadeRoom(string roomId, string roomCode, bool created, string playerId, int maxPlayers,
                                 MatchmadePlayer[] players = null,
                                 string propertiesPayload = null, bool propertiesComplete = false,
                                 int entry = RoomEntrySequence.None,
                                 bool answersARequestInFlight = false)
            {
                RoomId             = roomId;
                RoomCode           = roomCode;
                Created            = created;
                PlayerId           = playerId;
                MaxPlayers         = maxPlayers;
                Players            = players;
                PropertiesPayload  = propertiesPayload;
                PropertiesComplete = propertiesComplete;
                Entry              = entry;
                AnswersARequestInFlight = answersARequestInFlight;
            }
        }

        // ── Packet serialisation ──────────────────────────────────────────────

        private static byte[] BuildMatchmakingPayload(
            MatchmakingOptions opts, string playerId, string requestId)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            // First, so the member the gateway keys its idempotency table on is
            // in the opening bytes of every request rather than wherever the
            // optional fields above it happen to leave it.
            sb.Append($"\"request_id\":{JsonString(requestId)},");
            sb.Append($"\"mode\":{JsonString(opts.Mode)}");
            if (!string.IsNullOrEmpty(opts.LobbyName))
                sb.Append($",\"lobby_name\":{JsonString(opts.LobbyName)}");
            if (opts.MinPlayers > 0)
                sb.Append($",\"min_players\":{opts.MinPlayers}");
            if (opts.MaxPlayers > 0)
                sb.Append($",\"max_players\":{opts.MaxPlayers}");
            sb.Append($",\"player_id\":{JsonString(playerId)}");
            if (!string.IsNullOrEmpty(opts.DisplayName))
                sb.Append($",\"display_name\":{JsonString(opts.DisplayName)}");
            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── JSON helper ──────────────────────────────────────────────────────

        /// <summary>
        /// Serialises <paramref name="s"/> as a JSON string, using the
        /// canonical <see cref="PropertyJson.AppendJsonString"/> helper that
        /// escapes backslash, double-quote, AND all control characters
        /// (&#x3c; 0x20) as \uXXXX sequences.  The previous hand-rolled
        /// implementation escaped only backslash and double-quote, so a
        /// developer-supplied string containing a tab or newline produced
        /// malformed JSON that was silently rejected by the server (SDKR-03).
        /// </summary>
        private static string JsonString(string s)
        {
            var sb = new System.Text.StringBuilder();
            PropertyJson.AppendJsonString(sb, s ?? string.Empty);
            return sb.ToString();
        }


        // ── Subscriber-isolated event invocation ───────────────────────────────
        //
        // These events are raised during inbound packet processing, and the
        // subscribers now outlive the instance that was current when they
        // registered — a replacement takes them over on every reconnect.  A
        // handler on a destroyed object therefore stays in the list and throws
        // when it runs, and a bare Invoke would let that one failure deny
        // delivery to every subscriber behind it and abandon the rest of the
        // inbound frame.  Walk the invocation list so one throw costs one
        // subscriber.  Same discipline as RoomManager.SafeRaise.

        private static void SafeRaise(Action handler)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action)subs[i])(); }
                catch (Exception ex) { LogSubscriberThrow(ex); }
            }
        }

        private static void SafeRaise<T>(Action<T> handler, T arg)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action<T>)subs[i])(arg); }
                catch (Exception ex) { LogSubscriberThrow(ex); }
            }
        }

        private static void LogSubscriberThrow(Exception ex)
        {
            // A throw per subscriber per raise.  The latch bounds raises to
            // one per request, so a server cannot drive this; the subscriber
            // count is the application's and is bounded by nothing.  One line a
            // second, from whichever throw is first in the window.
            if (!WarnGate.ShouldEmit(ref s_lastSubscriberThrowWarnTicks)) return;
            Debug.LogError(
                "[RTMPE] MatchmakingManager: event subscriber threw " +
                $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                "remaining subscribers.");
        }

    }
}
