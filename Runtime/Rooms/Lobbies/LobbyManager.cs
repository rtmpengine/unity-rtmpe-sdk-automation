// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyManager.cs
//
// Manages lobby browser state: joining/leaving the lobby namespace,
// requesting room lists, and applying server-push updates.
//
// Threading: all public methods must be called from the Unity main thread.
// Callbacks (OnRoomListUpdated) are also invoked on the Unity main thread
// because LobbyManager is only called from NetworkManager's main-thread dispatch path.

using System;
using System.Collections.Generic;
using RTMPE.Core;
using RTMPE.Protocol;

#if UNITY_2017_1_OR_NEWER
using UnityEngine;
#endif

namespace RTMPE.Rooms
{
    /// <summary>
    /// Controls lobby browsing — joining a lobby namespace, requesting room
    /// lists with filters, and receiving push updates when the server
    /// broadcasts a new room list on <c>rtmpe.lobby.update.{lobby_name}</c>.
    /// </summary>
    public sealed class LobbyManager
    {
        private readonly Action<byte[]>         _sendPacket;
        private readonly PacketBuilder          _builder;

        // ── State ──────────────────────────────────────────────────────────────

        /// <summary>Name of the lobby the client is currently browsing ("" while not in a lobby).</summary>
        public string CurrentLobbyName { get; private set; } = string.Empty;

        /// <summary>Whether the client has joined a lobby and is receiving push updates.</summary>
        public bool IsInLobby { get; private set; }

        /// <summary>Last known room list for the current lobby.</summary>
        public IReadOnlyList<LobbyRoomInfo> Rooms => _rooms;
        private readonly List<LobbyRoomInfo> _rooms = new List<LobbyRoomInfo>();

        // Tracks a pending join so IsInLobby is only set true after the server
        // confirms with its first LobbyJoin reply, not optimistically on send
        // and not on a stray LobbyList reply that happens to arrive first.
        private bool   _joinPending;
        private string _pendingLobbyName = string.Empty;

        // Name of the most recently abandoned lobby (set in LeaveLobby and
        // before re-issuing a different JoinLobby).  When a join-reply for
        // this lobby arrives after the client has moved on it must be
        // ignored — without this defence, the JoinA / Leave / JoinB
        // sequence would let A's stale reply re-flip IsInLobby and adopt
        // A's room list as if it belonged to B.
        private string _abandonedLobbyName = string.Empty;
        // Deadline, on whatever clock this instance was given, after which the
        // pending join is considered timed-out and discarded.  Defends against
        // an attacker silently consuming a "pending" slot established hours
        // earlier by feeding a forged LobbyJoin reply at an opportune moment.
        private float  _joinPendingDeadline;

        /// <summary>
        /// How long a join may stay pending before a reply is no longer allowed
        /// to complete it.
        /// </summary>
        /// <remarks>
        /// Ordered against the responder, not chosen: the gateway waits
        /// <c>GATEWAY_NATS_REQUEST_TIMEOUT_SECS</c> — two seconds past the Room
        /// Service's own ten — before it stops listening, so a reply it will
        /// still deliver can arrive up to twelve seconds after the request. At
        /// five, this manager discarded joins the server went on to answer and
        /// admit: the client was left in no lobby, with no room list, while the
        /// gateway forwarded pushes for a subscription it had made.
        ///
        /// <para>It bounds admission and nothing else. A forged reply arriving
        /// after it cannot flip <see cref="IsInLobby"/>, which is what the
        /// deadline is for; what a client asked to join is remembered
        /// separately, because leaving a lobby is not entering one.</para>
        /// </remarks>
        internal const float JoinPendingTimeoutSeconds = 15.0f;

        // The lobby this client last asked to be subscribed to, kept until it
        // leaves one. The gateway registers the session on any join the Room
        // Service did not refuse, and neither the reply's readability nor its
        // timing changes that — so this is written when the join is sent rather
        // than inferred from what comes back, and it is the name LeaveLobby
        // falls back to.
        //
        // Every other handle on that registration is conditional. IsInLobby is
        // cleared by a reply this build could not use; CurrentLobbyName goes
        // with it; and the pending-join slot is discarded on a deadline shorter
        // than the one the gateway itself waits on. A join answered slowly, or
        // answered unreadably, left nothing at all to name — and 0x28 is the
        // only thing that revokes the feed.
        private string _requestedLobbyName = string.Empty;

        // The lobby the last one-shot ListRooms asked about.
        //
        // `ListRooms` is documented as querying a lobby WITHOUT joining it, so
        // its reply names a lobby this manager is neither in nor pending on —
        // and the gate below drops exactly those. It could only ever be applied
        // while the reply carried no name at all; now that an empty reply names
        // its lobby too, nothing would reach the caller without this.
        private string _queriedLobbyName = string.Empty;

        // An outstanding one-shot LobbyList query, and the instant it stops being
        // worth waiting for.  A single watch rather than a table: 0x29 carries no
        // request id, so a second query cannot be told from the first and simply
        // extends the wait.
        //
        // ⛔ Retired by a LobbyList REPLY and by nothing else.  A LobbyJoin reply
        // carries the same payload shape and a push carries it again, and neither
        // answers this request — a client that took either for an answer would
        // stop waiting for a query the server never ran.
        // The join's window, reused rather than a second number: a list query is
        // not a slower operation than a join, and two timeouts on one connection
        // would be two policies to keep above the server's own budget when only
        // one of them is ever revisited.  Fifteen seconds clears the gateway's own
        // twelve-second request timeout with a hop either side.
        private float _listPendingDeadline;
        private bool  _listPending;

        // Replies still owed for queries this client has issued.
        //
        // ⚠️ "Retire on the next LobbyList reply" is wrong for a reason the
        // request side does not show: the query is sent RELIABLE, the gateway
        // builds a reply per copy it receives, and a duplicate is the ordinary
        // outcome of loss recovery — so a late duplicate of an earlier query's
        // reply would retire a later query's watch and its silence would go
        // unreported.  The wire carries no request id, so the count of replies
        // still owed is what a reply is measured against, and a duplicate spends
        // a debt that is not there.
        // ⛔ What the count CANNOT tell apart, because the wire carries no request
        // id: a duplicate of an earlier query's reply arriving after a later query
        // was issued.  It is indistinguishable from that query's own reply and
        // retires the watch, so the later query's silence goes unreported.  Only a
        // correlated reply removes that, which is a change to the gateway and the
        // SDK together under rule 1; what the count does close is the case the
        // client can decide — two queries outstanding and one answer.
        private int _listRepliesOwed;

        // A query this manager still owes the application a report for; null when
        // it owes none.  ⚠️ Separated from the expiry for a DIFFERENT reason than
        // the join's: the join's expiry also runs on the inbound-packet path,
        // where application code must not be interleaved, and this one runs only
        // from Tick.  What the split buys here is the property the raise itself
        // needs — the debt is spent before the handler runs, so a subscriber that
        // re-enters this manager cannot be told twice and can arm a fresh query
        // of its own.
        private string _unreportedAbandonedQuery;


        // Wall clock for the pending-join deadline. Per instance rather than a
        // static, so a test can reach the deadline without spending it and
        // without a shared mutable clock two test classes could race over.
        private readonly Func<float> _clock;

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired on the Unity main thread whenever the room list is refreshed —
        /// either as the reply to <see cref="JoinLobby"/> / <see cref="ListRooms"/>,
        /// or as a server-push <c>LobbyRoomListUpdate</c> (0x2A).
        /// </summary>
        public event Action<IReadOnlyList<LobbyRoomInfo>> OnRoomListUpdated;

        /// <summary>
        /// Fired on the Unity main thread when a <see cref="JoinLobby"/> is
        /// abandoned because the server never answered it.
        /// </summary>
        /// <remarks>
        /// The counterpart of <c>RoomManager.OnRoomError</c>, and the only
        /// failure this class reports as an event: everything else that can go
        /// wrong here is a property of a room-list payload that did arrive, and
        /// is reported through <see cref="LastProblem"/> instead.
        ///
        /// ⛔ A timeout says the join was not confirmed; it does not say the
        /// gateway holds no subscription for this session. The gateway
        /// registers one on any join the Room Service did not refuse, whatever
        /// happens to the reply — so the answer to this event is
        /// <see cref="LeaveLobby"/> or another <see cref="JoinLobby"/>, never
        /// an assumption that nothing was left behind.
        /// </remarks>
        public event Action<string> OnLobbyError;

        /// <summary>
        /// Take over the subscriber list of the instance this one replaces.
        /// </summary>
        /// <remarks>
        /// A LobbyManager is rebuilt whenever the connection is, and the room
        /// browser's handler is registered once, at start-up, against the
        /// manager the application could see.  Without this it stops being
        /// called the moment the connection it was registered before is made.
        /// </remarks>
        internal void AdoptSubscribersFrom(LobbyManager previous)
        {
            if (previous == null || ReferenceEquals(previous, this)) return;

            OnRoomListUpdated =
                SubscriberAdoption.Carry(OnRoomListUpdated, previous.OnRoomListUpdated);
            OnLobbyError =
                SubscriberAdoption.Carry(OnLobbyError, previous.OnLobbyError);
        }

        // ── Constructor ────────────────────────────────────────────────────────

        public LobbyManager(PacketBuilder builder, Action<byte[]> sendPacket)
            : this(builder, sendPacket, DefaultClock)
        {
        }

        internal LobbyManager(PacketBuilder builder, Action<byte[]> sendPacket, Func<float> clock)
        {
            _builder    = builder    ?? throw new ArgumentNullException(nameof(builder));
            _sendPacket = sendPacket ?? throw new ArgumentNullException(nameof(sendPacket));
            _clock      = clock      ?? throw new ArgumentNullException(nameof(clock));
        }

        // Seconds since this process started, in both builds.
        //
        // ⚠️ Measured from an origin rather than from the epoch. A float carries
        // 24 bits of mantissa, so around 1.8e9 — seconds since 1970 — one unit
        // in the last place is 128 seconds: `now + JoinPendingTimeoutSeconds`
        // compares equal to `now`, and a deadline expressed that way never
        // arrives. Unity's own clock is already process-relative; the headless
        // build was not, so the two disagreed about whether a join can time out
        // at all.
        private static readonly System.Diagnostics.Stopwatch ProcessClock =
            System.Diagnostics.Stopwatch.StartNew();

        internal static float DefaultClock()
        {
#if UNITY_2017_1_OR_NEWER
            return Time.realtimeSinceStartup;
#else
            return (float)ProcessClock.Elapsed.TotalSeconds;
#endif
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a <c>LobbyJoin</c> (0x27) request.  The server replies with the
        /// current room list and begins forwarding push updates for the named lobby.
        /// </summary>
        /// <param name="lobbyName">
        /// Lobby to join.  Up to <see cref="LobbyName.MaxBytes"/> characters from
        /// <see cref="LobbyName.Alphabet"/>; there is no default lobby.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="lobbyName"/> is one the Room Service refuses.  It is
        /// checked here rather than sent because a refusal on that path does not
        /// return as one: the gateway answers an empty room list, this manager
        /// reports itself in the lobby, and no push is ever subscribed.
        /// </exception>
        public void JoinLobby(string lobbyName)
        {
            LobbyName.Require(lobbyName, nameof(lobbyName));
            var nextName = lobbyName;

            // If we are switching lobbies (either an earlier pending join is
            // still in flight, or we are already in a different lobby),
            // record the previous target so a late reply for it does not
            // bind to the new IsInLobby/CurrentLobbyName slot.  The matcher
            // in HandleLobbyReply uses this to drop stale acks.
            if (_joinPending && !string.Equals(_pendingLobbyName, nextName, StringComparison.Ordinal))
            {
                _abandonedLobbyName = _pendingLobbyName ?? string.Empty;
            }
            else if (IsInLobby && !string.Equals(CurrentLobbyName, nextName, StringComparison.Ordinal))
            {
                _abandonedLobbyName = CurrentLobbyName ?? string.Empty;
            }

            // A new join targeting the previously-abandoned lobby name retires
            // the abandonment marker so the legitimate reply is not dropped as
            // stale.  Without this clear, Join("A") -> Leave -> Join("A") would
            // see the second join's reply matched against the abandoned-name
            // guard above and dropped, leaving the user blocked for the full
            // join-pending timeout.
            if (string.Equals(_abandonedLobbyName, nextName, StringComparison.Ordinal))
            {
                _abandonedLobbyName = string.Empty;
            }

            _pendingLobbyName    = nextName;
            _requestedLobbyName  = nextName;
            _joinPending         = true;
            _joinPendingDeadline = NowSeconds() + JoinPendingTimeoutSeconds;

            var payload = LobbyPacketBuilder.BuildLobbyJoinPayload(_pendingLobbyName);
            var packet  = _builder.Build(PacketType.LobbyJoin, PacketFlags.Reliable, payload);
            _sendPacket(packet);
        }

        /// <summary>
        /// Sends a <c>LobbyLeave</c> (0x28) fire-and-forget message.
        /// The server stops forwarding push updates to this session.
        /// </summary>
        public void LeaveLobby()
        {
            // The lobby this client last asked for — not the one it believes it
            // is in, and not the pending slot.
            //
            // ⛔ Not because it is always the lobby the gateway holds — it is not.
            // `forward_lobby_join` sets the session's slot only on a join the
            // Room Service ADMITS, so after a refusal, or a join still in
            // flight, the slot holds the previous lobby while this names the new
            // one. Every candidate is wrong in some sequence; what makes this
            // the right one is that it is the only one that is never EMPTY while
            // a subscription may exist, and an empty name is the only outcome
            // that costs anything.
            //
            // ⚠️ The rest is a property of the gateway, not of this code:
            // `forward_lobby_leave` drains the registration with
            // `clear_lobby_subscription(&addr)` — by address, whatever the
            // payload names — and the Room Service validates the name without
            // acting on it. So a name that is wrong still revokes the feed, and
            // reaches the Room Service as a leave for a subscriber set this
            // session is not in, which removes nothing.
            // ⚠️ Kept, not consumed. 0x28 is the only thing that revokes the
            // feed, so the name has to remain available for as long as a
            // subscription might: a datagram that did not arrive leaves nothing
            // else to try, and the gateway may register the subscription after
            // this call returns. Replaced by the next JoinLobby. The cost of
            // keeping it is one packet per redundant leave; the cost of
            // spending it is a room-list feed for the life of the connection.
            var nameToLeave = _requestedLobbyName;
            _joinPending      = false;
            _pendingLobbyName = string.Empty;

            // Capture the lobby we are walking away from so a late join-reply
            // for it cannot resurrect IsInLobby after a subsequent JoinLobby
            // for a different lobby has been issued.  The reply matcher in
            // HandleLobbyReply consults this to drop stale acknowledgements.
            _abandonedLobbyName = nameToLeave ?? string.Empty;

            // Sent whenever there is a lobby to name, not only while this
            // manager believes it is in one.  The gateway's registration
            // outlives that belief — a reply it could not read leaves the
            // subscription in place while IsInLobby is false — and 0x28 is the
            // only thing that revokes it.  Clearing is unconditional on the
            // gateway side, so a leave for a lobby already left costs one
            // packet; withholding it costs a room-list feed for the life of
            // the connection.
            if (string.IsNullOrEmpty(nameToLeave))
            {
                IsInLobby        = false;
                CurrentLobbyName = string.Empty;
                _rooms.Clear();
                return;
            }

            // ⛔ Sent best-effort, and it is the one control operation here
            // that must be.  A reliable frame is re-sent when its
            // acknowledgement is lost, and the gateway orders lobby operations
            // by the counter each one advances *as it is delivered*
            // (`forward_lobby_leave` in the gateway drains before it publishes,
            // for the same reason). A re-sent leave therefore takes an ordering
            // position later than the one it was issued at, and supersedes a
            // `LobbyJoin` the application made in between — leaving the gateway
            // subscribed to nothing while this manager reports the client in the
            // lobby it asked for, with no error raised and nothing that retries.
            // That is the end state the counter exists to prevent, reached from
            // a third side, and no ordering rule on the gateway can close it:
            // the copy genuinely does arrive later.
            //
            // 🔑 What reliability would buy here is small by comparison. This is
            // a fire-and-forget message — the gateway returns no reply, and the
            // local state above is already torn down — so a lost leave costs a
            // room-list feed the client has stopped consuming, until the next
            // join replaces the subscription or the session ends. A bounded loss
            // is the right trade against an unbounded one.
            //
            // ⚠️ It also matches what two transports of three already do:
            // retransmission needs a peer that negotiated `CAP_ARQ_ACK`, which
            // KCP and WebSocket suppress by design, so this is the behaviour a
            // WebSocket or KCP client has always had.
            //
            // Closing it properly needs the leave to carry the position it was
            // issued at, which is a wire change and belongs with the correlation
            // id `MatchmakingRequest` is owed.
            var payload = LobbyPacketBuilder.BuildLobbyLeavePayload(nameToLeave);
            var packet  = _builder.Build(PacketType.LobbyLeave, PacketFlags.None, payload);
            _sendPacket(packet);

            IsInLobby        = false;
            CurrentLobbyName = string.Empty;
            _rooms.Clear();
        }

        /// <summary>
        /// Sends a <c>LobbyList</c> (0x29) request with the given options.
        /// Use this for one-shot filtered queries without joining the lobby.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="opts"/> names a lobby the Room Service refuses, or
        /// carries a filter it cannot compare — one with an empty key, or a
        /// target that is not a string, int, float, double or bool. A
        /// non-finite float or double is refused with them: neither has a JSON
        /// form, and the service refuses the whole query rather than that one
        /// comparison.
        ///
        /// Same reasoning as <see cref="JoinLobby"/>: the refusal arrives as an
        /// empty room list, which is also what a lobby with no rooms looks
        /// like. Use <see cref="LobbyFilterValue.IsValid"/> or
        /// <see cref="LobbyFilterValue.Describe"/> to check a value before
        /// building the query.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="opts"/> is null; there is no default lobby.
        /// </exception>
        public void ListRooms(LobbyQueryOptions opts)
        {
            if (opts == null)
                throw new ArgumentNullException(
                    nameof(opts),
                    "ListRooms needs the lobby to query; there is no default lobby.");
            LobbyName.Require(opts.LobbyName, nameof(opts));
            RequireFilterValues(opts);

            // ⛔ The whole packet is built before any of the bookkeeping below,
            // because the field checks above are not the only way this call can
            // fail. A query is bounded by nothing but the datagram — sixteen
            // filters of ordinary size clear every per-field rule and still
            // exceed it — and PacketBuilder refuses one by throwing. Refused
            // after the four writes below, the query would be counted as owed
            // an answer nothing will send: _listRepliesOwed would stay one too
            // high for the life of the manager, and the deadline would report a
            // timeout over a query that had in fact succeeded.
            var packet = _builder.Build(
                PacketType.LobbyList, PacketFlags.Reliable,
                LobbyPacketBuilder.BuildLobbyListPayload(opts));

            _queriedLobbyName    = opts.LobbyName;
            _listPending         = true;
            _listPendingDeadline = NowSeconds() + JoinPendingTimeoutSeconds;
            _listRepliesOwed++;

            _sendPacket(packet);
        }

        /// <summary>
        /// Refuse a query carrying a filter the Room Service cannot compare.
        /// </summary>
        private static void RequireFilterValues(LobbyQueryOptions opts)
        {
            if (opts.Filters == null) return;

            for (int i = 0; i < opts.Filters.Count; i++)
            {
                var filter = opts.Filters[i];
                string reason = filter == null
                    ? "filter must not be null"
                    : LobbyFilterValue.DescribeOp(filter.Op)
                      ?? LobbyFilterValue.DescribeKey(filter.Key)
                      ?? LobbyFilterValue.Describe(filter.Value);

                if (reason != null)
                    throw new ArgumentException(
                        $"[RTMPE] filter {i}: {reason}", nameof(opts));
            }
        }

        // ── Internal: inbound packet dispatch ──────────────────────────────────

        /// <summary>
        /// Retire a <see cref="JoinLobby"/> the server never answered.  Called
        /// once per frame by <see cref="NetworkManager"/> on the main thread,
        /// and a no-op whenever no join is pending — the steady-state cost is a
        /// single flag test.
        /// </summary>
        /// <remarks>
        /// ⛔ Without a driver the deadline could only be reached from inside
        /// <see cref="HandleLobbyReply"/>, which means it expired only when a
        /// LATER reply arrived — so the one case it exists for, a join that
        /// draws no reply at all, was the one case it could not act on. Nothing
        /// else on this path consults a clock: <see cref="JoinLobby"/> registers
        /// no retransmit, and the class raised no failure of its own.
        /// </remarks>
        internal void Tick()
        {
            ExpirePendingJoinIfDue();
            ExpirePendingQueryIfDue();
            ReportAbandonedJoin();
            ReportAbandonedQuery();
        }

        /// <summary>
        /// Retire a <see cref="ListRooms"/> the server never answered.
        /// </summary>
        /// <remarks>
        /// The query has no other end.  <c>ListRooms</c> returns void and the
        /// cached list is not touched until a reply arrives, so before this a
        /// lost datagram left a browser waiting with nothing said on any channel.
        /// ⚠️ <c>OnRoomListUpdated</c> does fire from elsewhere — a lobby push
        /// raises it too — but only for a client that JOINED a lobby, and the
        /// one-shot query is precisely the caller with no subscription behind it
        /// and therefore no second chance.
        /// </remarks>
        private void ExpirePendingQueryIfDue()
        {
            if (!_listPending) return;
            if (NowSeconds() <= _listPendingDeadline) return;

            _unreportedAbandonedQuery = _queriedLobbyName ?? string.Empty;
            _listPending              = false;
            _listRepliesOwed          = 0;
        }

        private void ReportAbandonedQuery()
        {
            if (_unreportedAbandonedQuery == null) return;

            string abandoned          = _unreportedAbandonedQuery;
            _unreportedAbandonedQuery = null;

            SafeRaise(
                OnLobbyError,
                $"ListRooms '{abandoned}' timed out after {JoinPendingTimeoutSeconds:0.#}s " +
                "with no reply from the server. No room list arrived and none is still expected " +
                "for this query.");
        }
        // An abandoned join this manager still owes the application a report
        // for; null when it owes none.  The expiry and the report are separated
        // because the expiry runs on the inbound-packet path and the report
        // runs application code.
        private string _unreportedAbandonedJoin;

        /// <summary>
        /// Discard the pending join if its deadline has passed.  Records the
        /// abandonment; raises nothing.
        /// </summary>
        /// <remarks>
        /// Idempotent, which is what lets both the frame driver and the reply
        /// path call it: the first to observe the deadline spends the slot, and
        /// the second finds no pending join.
        ///
        /// ⛔ Two names are deliberately NOT cleared, and each has cost a
        /// separate defect.
        ///
        /// <para><c>_requestedLobbyName</c> is the only handle left on a
        /// subscription the gateway may well hold — it registers one on any
        /// join the Room Service did not refuse, whatever becomes of the reply
        /// — and it is what <see cref="LeaveLobby"/> falls back to. A timeout is
        /// the moment that handle matters most.</para>
        ///
        /// <para>🔴 <c>_pendingLobbyName</c> is the name the PUSH gate reads.
        /// With a join unanswered, <see cref="CurrentLobbyName"/> is empty, so
        /// this is the only thing admitting an inbound
        /// <c>LobbyRoomListUpdate</c> for the lobby the client asked for —
        /// pushes it was receiving, and which the gateway goes on sending.
        /// Clearing it on expiry silently ended the room-list feed fifteen
        /// seconds in, which is a worse outcome than the stuck flag this
        /// deadline exists to fix: an application that ignores
        /// <see cref="OnLobbyError"/> would have been left strictly worse off
        /// than before the timeout existed. It is superseded by the next
        /// <see cref="JoinLobby"/> and by nothing else.</para>
        /// </remarks>
        private void ExpirePendingJoinIfDue()
        {
            if (!_joinPending) return;
            if (NowSeconds() <= _joinPendingDeadline) return;

            _unreportedAbandonedJoin = _pendingLobbyName ?? string.Empty;
            _joinPending             = false;
        }

        /// <summary>
        /// Hand the application any abandonment recorded since the last frame.
        /// </summary>
        /// <remarks>
        /// 🔴 Called from <see cref="Tick"/> and from nowhere else, and that is
        /// the whole point of splitting it from the expiry.
        /// <see cref="HandleLobbyReply"/> also has to observe the deadline —
        /// it is the security check that stops a late reply completing a join —
        /// but it is mid-way through interpreting a payload when it does, and
        /// raising there runs application code inside that method. A subscriber
        /// answering "join timed out" with the obvious retry armed a fresh
        /// pending join, and the reply being handled — the ABANDONED one — then
        /// fell through and bound itself to it: the new lobby was reported
        /// joined before the server had seen the request, and its slot was spent
        /// so its real reply could never complete it and it could never time
        /// out.
        ///
        /// The debt is cleared BEFORE the raise, so a subscriber that drives
        /// another frame from inside its handler is not told twice.
        /// </remarks>
        private void ReportAbandonedJoin()
        {
            if (_unreportedAbandonedJoin == null) return;

            string abandoned         = _unreportedAbandonedJoin;
            _unreportedAbandonedJoin = null;

            SafeRaise(
                OnLobbyError,
                $"JoinLobby '{abandoned}' timed out after {JoinPendingTimeoutSeconds:0.#}s " +
                "with no reply from the server.");
        }

        /// <summary>
        /// Drop every session-scoped watch when the connection ends.
        /// </summary>
        /// <remarks>
        /// ⛔ The manager is normally discarded with the session, which is why
        /// nothing called this before — and "normally" is the word that makes it
        /// worth having.  A watch that outlives its link either reports a query
        /// the connection can no longer answer, fifteen seconds after the link
        /// died, or is thrown away with the instance and never reported at all;
        /// the second is the defect this class exists to close, reached on the
        /// reconnect path.  The membership is not touched: leaving a lobby is not
        /// what losing a connection means, and the join's own expiry states the
        /// same distinction.
        /// </remarks>
        internal void ClearState()
        {
            _joinPending              = false;
            _listPending              = false;
            _listRepliesOwed          = 0;
            _unreportedAbandonedJoin  = null;
            _unreportedAbandonedQuery = null;
        }

        /// <summary>
        /// Called by NetworkManager when a LobbyJoin or LobbyList reply arrives.
        /// Only a <see cref="PacketType.LobbyJoin"/> reply with an outstanding
        /// <see cref="JoinLobby"/> in flight (within the timeout window) is
        /// permitted to flip <see cref="IsInLobby"/> to true.  A stray /
        /// out-of-band <see cref="PacketType.LobbyList"/> reply only refreshes
        /// the cached room list.
        /// </summary>
        internal void HandleLobbyReply(PacketType replyType, byte[] payload)
        {
            // Time-out a forgotten pending join before we use it.  State only:
            // the report is owed to the next Tick, because everything below
            // this line is interpreting a payload and must not be interleaved
            // with application code.
            ExpirePendingJoinIfDue();

            // A LobbyList reply is the one thing that answers a one-shot query,
            // so the wait ends here — before the payload is judged, because a
            // reply this build could not read is still a reply and the watch
            // reports only that nothing came back at all.
            if (replyType == PacketType.LobbyList && _listRepliesOwed > 0)
            {
                _listRepliesOwed--;
                if (_listRepliesOwed == 0) _listPending = false;
            }

            // Parse the reply once, up front.  We need the room list both to
            // discriminate stale replies (via the per-room lobby_name tag)
            // and to feed ApplyRoomList below.  The lobby-name discriminant
            // is sourced from the first room — the gateway guarantees every
            // entry in a single reply belongs to the same lobby, so the
            // first row is canonical.
            var parsed = LobbyPacketParser.ParseRoomList(payload, out var outcome,
                                                         out var envelopeLobby);
            if (!Usable(outcome))
            {
                // Dropping the cached list and the membership is right only
                // where the reply proves this session's subscription moved, and
                // two outcomes do.  The gateway registers the subscription when
                // the Room Service admitted the join and answers a refusal with
                // a document it writes itself, so a payload that parsed far
                // enough to carry an envelope version or an entry count is the
                // Room Service's and the join was admitted.  A refusal is not,
                // and bytes this build could not read — an empty or truncated
                // payload among them — say nothing either way.
                //
                // Where the subscription did not move the client is still in
                // the lobby it was in and still being sent its pushes, and
                // clearing would drop every one of them on the lobby-name gate.
                bool subscriptionMoved =
                    outcome == LobbyPacketParser.LobbyListOutcome.UnsupportedVersion
                    || outcome == LobbyPacketParser.LobbyListOutcome.TooManyEntries;

                // Ahead of the event, so a handler that reads LastProblem to
                // tell "unavailable" from "empty" sees the cause of the list it
                // is being handed.
                ReportRoomListProblem(outcome);

                if (replyType == PacketType.LobbyJoin && subscriptionMoved)
                {
                    _rooms.Clear();
                    IsInLobby = false;
                    CurrentLobbyName = string.Empty;
                    SafeRaise(OnRoomListUpdated, _rooms);
                }

                // The pending join stays armed on every path.  A reply carries
                // no request id, and one with no rooms carries no lobby tag
                // either, so this cannot tell which join it answers: spending
                // the slot here would cancel whichever join is in flight, which
                // may be a later one the server went on to admit.  It is cleared
                // by the deadline check above on the next reply, or superseded
                // by the next JoinLobby.
                return;
            }
            if (outcome == LobbyPacketParser.LobbyListOutcome.OkWithDroppedRows)
                ReportRoomListProblem(outcome);
            // The lobby this reply belongs to: from the rooms when it has any,
            // and from the envelope when it has none.
            //
            // 🔑 The empty case is why the envelope carries a name at all. A
            // client switches lobbies by issuing a second join and the replies
            // are not ordered against each other, so without a tag an empty
            // reply for the lobby it walked away from is indistinguishable from
            // one for the lobby it is waiting on — and binding it to the
            // pending join reports membership of a lobby nobody has admitted it
            // to. Null when neither says anything, which is what a server
            // predating the field sends: there the reply binds as before,
            // because there is nothing to bind it against.
            string replyLobby = parsed.Count > 0
                ? (parsed[0].LobbyName ?? string.Empty)
                : (string.IsNullOrEmpty(envelopeLobby) ? null : envelopeLobby);

            // Drop a join-reply that is a ghost from a lobby the client has
            // already left or switched away from.  Without this guard the
            // sequence Join("A") → Leave → Join("B") followed by an
            // out-of-order arrival of A's reply before B's would re-flip
            // IsInLobby and overwrite CurrentLobbyName with the new pending
            // target — leaving the manager claiming to be in B while
            // surfacing A's room list.
            //
            // We can only enforce the discriminant when the reply contains
            // at least one room (replyLobby != null).  An empty room list
            // carries no lobby tag; we fall through to the existing
            // pending-join match in that case, which is safe because the
            // empty list has no rooms to mis-attribute.
            if (replyType == PacketType.LobbyJoin
                && replyLobby != null
                && !string.IsNullOrEmpty(_abandonedLobbyName)
                && string.Equals(replyLobby, _abandonedLobbyName, StringComparison.Ordinal))
            {
                // Stale ack for an abandoned lobby — drop without applying
                // the room list and without flipping any state.  Leave
                // _abandonedLobbyName set so further duplicates are also
                // dropped; it is cleared once the in-flight join's reply
                // arrives below or on a subsequent JoinLobby/LeaveLobby.
                return;
            }

            // Only consume the pending-join slot on a LobbyJoin reply.  Stray
            // LobbyList replies (which carry the same payload shape) cannot
            // promote the manager to "in lobby" — that closes the lobby-reply
            // confusion.
            if (replyType == PacketType.LobbyJoin && _joinPending)
            {
                // If the reply carries a lobby tag, it MUST match the
                // in-flight join name.  A mismatch means the reply belongs
                // to an earlier abandoned join — drop it.
                if (replyLobby != null
                    && !string.Equals(replyLobby, _pendingLobbyName ?? string.Empty, StringComparison.Ordinal))
                {
                    return;
                }

                CurrentLobbyName    = _pendingLobbyName;
                IsInLobby           = true;
                _joinPending        = false;
                _pendingLobbyName   = string.Empty;
                _abandonedLobbyName = string.Empty;
            }

            // Gate the cached room list against the lobby we are actually in
            // (or pending on).  A late reply for a lobby we have already left
            // would otherwise clear the live room list and surface the stale
            // peer set via OnRoomListUpdated, even though the join branch above
            // correctly refuses to flip IsInLobby.
            if (replyLobby != null
                && !string.Equals(replyLobby, CurrentLobbyName ?? string.Empty, StringComparison.Ordinal)
                && !string.Equals(replyLobby, _pendingLobbyName ?? string.Empty, StringComparison.Ordinal)
                && !string.Equals(replyLobby, _queriedLobbyName ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            // Apply the parsed room list directly so we do not parse twice.
            PublishRoomList(parsed, outcome);
        }

        // Test-friendly overload preserved for callers that pre-date the
        // type-discriminating signature; defaults to LobbyJoin so legacy unit
        // tests retain their existing behaviour.
        internal void HandleLobbyReply(byte[] payload)
            => HandleLobbyReply(PacketType.LobbyJoin, payload);

        private float NowSeconds() => _clock();

        /// <summary>
        /// Called by NetworkManager when a LobbyRoomListUpdate (0x2A) push arrives.
        /// Replaces the cached room list and raises <see cref="OnRoomListUpdated"/>.
        /// </summary>
        internal void HandleLobbyRoomListUpdate(byte[] payload)
        {
            ApplyRoomList(payload);
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        private void ApplyRoomList(byte[] payload)
        {
            var parsed = LobbyPacketParser.ParseRoomList(payload, out var outcome,
                                                         out var envelopeLobby);
            if (!Usable(outcome))
            {
                // A payload we could not read is not a lobby with no rooms.
                // Clearing on it replaces the last list the client did
                // understand with an emptiness indistinguishable from the truth,
                // and leaves nothing anywhere saying which it was.
                ReportRoomListProblem(outcome);
                return;
            }
            if (outcome == LobbyPacketParser.LobbyListOutcome.OkWithDroppedRows)
                ReportRoomListProblem(outcome);

            // Gate the push against the lobby we are actually in (or pending
            // on), exactly as HandleLobbyReply gates a reply.  A
            // LobbyRoomListUpdate for a lobby we have already left must not
            // overwrite the live room list and surface its stale peer set.
            //
            // 🔑 An empty push is the case that needed the envelope's own tag:
            // with no first row to read a name off, it applied unconditionally —
            // so a push for a lobby this client had left CLEARED the list of the
            // lobby it was actually browsing, and raised the event with it. The
            // rooms name the lobby when there are any; the envelope names it
            // when there are not; null only when neither does, which is what a
            // server predating the field sends.
            string pushLobby = parsed.Count > 0
                ? (parsed[0].LobbyName ?? string.Empty)
                : (string.IsNullOrEmpty(envelopeLobby) ? null : envelopeLobby);
            if (pushLobby != null
                && !string.Equals(pushLobby, CurrentLobbyName ?? string.Empty, StringComparison.Ordinal)
                && !string.Equals(pushLobby, _pendingLobbyName ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            PublishRoomList(parsed, outcome);
        }

        // Replaces the cached list and announces it.  A payload this build read
        // in full is also the evidence that whatever went wrong before has
        // stopped, so it retires LastProblem: an application polls that member
        // to decide whether to show "room list unavailable", and one that never
        // returns to None cannot tell a lobby that has recovered from one that
        // is still failing.  The per-cause log gate is deliberately not retired
        // with it — a recurring fault has already had its line, and resetting it
        // would trade one line per manager for one line per push.
        private void PublishRoomList(
            List<LobbyRoomInfo> parsed,
            LobbyPacketParser.LobbyListOutcome outcome)
        {
            if (outcome == LobbyPacketParser.LobbyListOutcome.Ok)
                LastProblem = LobbyListProblem.None;

            _rooms.Clear();
            _rooms.AddRange(parsed);
            SafeRaise(OnRoomListUpdated, _rooms);
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

        /// <summary>
        /// Whether an outcome carries a room list the caller should apply.
        /// </summary>
        /// <remarks>
        /// A payload with rows dropped is still the lobby, only short of a few
        /// rooms; a payload that was refused is not the lobby at all. Both are
        /// reported, and only the first is applied.
        /// </remarks>
        private static bool Usable(LobbyPacketParser.LobbyListOutcome outcome)
        {
            return outcome == LobbyPacketParser.LobbyListOutcome.Ok
                || outcome == LobbyPacketParser.LobbyListOutcome.OkWithDroppedRows;
        }

        /// <summary>
        /// How many room-list payloads this manager has refused or shown short.
        /// </summary>
        /// <remarks>
        /// Public because it is the only way application code can tell a lobby
        /// that has stopped updating from a lobby that has stopped changing. The
        /// log line reaches an operator reading a player's Player.log; a game
        /// that wants to show "room list unavailable" needs to read it here.
        /// </remarks>
        public int RefusedRoomLists { get; private set; }

        /// <summary>
        /// What went wrong with the most recent room-list payload, or
        /// <see cref="LobbyListProblem.None"/> once one has been read in full.
        /// </summary>
        /// <remarks>
        /// This tracks the current state rather than the session's history: a
        /// payload that parses completely retires it, so an application may poll
        /// it to raise and clear a "room list unavailable" banner.  Use
        /// <see cref="RefusedRoomLists"/> for the running count.
        /// </remarks>
        public LobbyListProblem LastProblem { get; private set; } = LobbyListProblem.None;

        /// <summary>What went wrong with the last room-list payload.</summary>
        public enum LobbyListProblem
        {
            /// <summary>Nothing has.</summary>
            None = 0,
            /// <summary>The payload could not be read at all.</summary>
            Unreadable = 1,
            /// <summary>It came from a Room Service newer than this SDK build.</summary>
            UnsupportedVersion = 2,
            /// <summary>It declared more rooms than the configured cap allows.</summary>
            TooManyEntries = 3,
            /// <summary>The server declined the request; the empty list is not the lobby.</summary>
            Refused = 5,
            /// <summary>It was applied, with at least one unreadable room omitted.</summary>
            RoomsOmitted = 4,
        }

        // One report per problem per manager: a push arrives whenever any room in
        // the lobby changes, so a persistent mismatch would otherwise fill the
        // log with one line and bury everything after it.
        private readonly HashSet<LobbyListProblem> _reported = new HashSet<LobbyListProblem>();

        private void ReportRoomListProblem(LobbyPacketParser.LobbyListOutcome outcome)
        {
            LobbyListProblem problem = Translate(outcome);
            RefusedRoomLists++;
            LastProblem = problem;
            if (!_reported.Add(problem)) return;
#if UNITY_2017_1_OR_NEWER
            UnityEngine.Debug.LogError(
                "[RTMPE] LobbyManager: " + Advice(problem) +
                "  LobbyManager.LastProblem is " + problem + ".");
#endif
        }

        private static LobbyListProblem Translate(LobbyPacketParser.LobbyListOutcome outcome)
        {
            switch (outcome)
            {
                case LobbyPacketParser.LobbyListOutcome.UnsupportedVersion:
                    return LobbyListProblem.UnsupportedVersion;
                case LobbyPacketParser.LobbyListOutcome.TooManyEntries:
                    return LobbyListProblem.TooManyEntries;
                case LobbyPacketParser.LobbyListOutcome.OkWithDroppedRows:
                    return LobbyListProblem.RoomsOmitted;
                case LobbyPacketParser.LobbyListOutcome.Refused:
                    return LobbyListProblem.Refused;
                default:
                    return LobbyListProblem.Unreadable;
            }
        }

        private static string Advice(LobbyListProblem problem)
        {
            switch (problem)
            {
                case LobbyListProblem.UnsupportedVersion:
                    return "the Room Service is emitting a newer lobby envelope than this " +
                           "SDK build understands, so no room list is being applied; " +
                           "upgrade the SDK.";
                case LobbyListProblem.TooManyEntries:
                    return "the reply declared more rooms than the entry cap allows, so the " +
                           "whole list was refused rather than shown truncated, and this " +
                           "client is now reported out of the lobby. The cap is never below " +
                           "the rooms the Room Service may return, so a reply that exceeds it " +
                           "came from something that is not obeying that limit; raise " +
                           "NetworkSettings.maxLobbyRoomEntries only if you know why.";
                case LobbyListProblem.Refused:
                    return "the server declined this lobby request — most often a rate " +
                           "limit or a lobby name it does not accept — so the empty list " +
                           "it answered with is not the lobby's contents.";
                case LobbyListProblem.RoomsOmitted:
                    return "at least one room was omitted from the list because a field " +
                           "of it could not be read — most often a name longer than " +
                           "NetworkSettings.maxLobbyStringBytes; the lobby is being shown " +
                           "short.";
                default:
                    return "a room-list payload was not readable and has been ignored.";
            }
        }

        // Guarded like the using above: this class compiles without UnityEngine,
        // and the isolation it reports on is worth having in either build.
        private static void LogSubscriberThrow(Exception ex)
        {
#if UNITY_2017_1_OR_NEWER
            UnityEngine.Debug.LogError(
                "[RTMPE] LobbyManager: event subscriber threw " +
                $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                "remaining subscribers.");
#endif
        }

    }
}
