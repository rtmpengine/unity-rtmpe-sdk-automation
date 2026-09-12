// RTMPE SDK — Runtime/Rooms/RoomManager.cs
//
// High-level Room API for creating, joining, leaving, and listing rooms.
// Created by NetworkManager and receives room packet callbacks from it.
//
// Threading model:
//   • All public methods MUST be called from the Unity main thread.
//   • HandleRoomPacket() is called by NetworkManager.ProcessPacket() which
//     runs on the main thread (via MainThreadDispatcher).
//
// Lifecycle:
//   1. NetworkManager creates RoomManager in InitialiseNetwork().
//   2. ProcessPacket() routes room-type packets to HandleRoomPacket().
//   3. On cleanup, NetworkManager nulls its reference — no explicit Dispose needed.

using System;
using System.Collections.Generic;
using RTMPE.Core;
using RTMPE.Core.Diagnostics;
using RTMPE.Protocol;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Manages room lifecycle from the client perspective.
    /// Access via <see cref="NetworkManager.Rooms"/>.
    /// </summary>
    public sealed class RoomManager
    {
        private readonly PacketBuilder _packetBuilder;
        private readonly Action<byte[]> _sendOwned;
        private readonly Func<NetworkState> _getState;
        // Called with the local player's room UUID after CreateRoom/JoinRoom succeeds.
        // Wired by NetworkManager so NetworkBehaviour.IsOwner comparisons have a valid ID.
        private readonly Action<string> _onLocalPlayerIdResolved;

        // Current room state (null when not in a room).
        private RoomInfo _currentRoom;

        // Retransmit state for the outstanding JoinRoom request.  Where the ARQ
        // capability is negotiated the transport recovers a lost request on its
        // own — it acknowledges one on receipt — and where it is not (KCP,
        // WebSocket) this ladder is that recovery too.  Either way, a request
        // that arrived and whose reply was lost looks identical from here and
        // nothing below this layer can recover it.  That is what this re-emits
        // for, until the reply arrives or the budget is spent, so the client is
        // not stranded in a permanent "joining…" state.
        private readonly PendingJoinRetry _pendingJoin = new PendingJoinRetry();

        // Room id whose arrival has already been announced through OnRoomJoined.
        // Retransmitting a join means the reply can arrive more than once (the
        // gateway recovers a re-sent join idempotently); this lets a duplicate
        // reply for the room we are already in be ignored instead of raising a
        // second, spurious OnRoomJoined.  Distinct from _currentRoom, which the
        // create→auto-join handoff seeds before the arrival is announced.
        private string _lastJoinedAnnouncedRoomId;

        // Room id of the outstanding LeaveRoom request, or null when none is
        // outstanding.  A leave reply is not self-describing — the wire carries
        // an ok flag and nothing else — so the room it settles is whichever one
        // was named when the request went out.  The transport re-sends a frame
        // whose acknowledgement was lost, and the server answers each copy, so
        // without this the second answer would be applied to whatever room the
        // client had entered by then.
        private string _pendingLeaveRoomId;

        // Whether the outstanding leave has already been reported as refused.
        // A refusal leaves the request outstanding — see HandleLeaveResponse —
        // so without this a second copy of the same refusal is a second
        // OnRoomError for one call.
        private bool _pendingLeaveRefused;

        // Local player's room UUID, populated by HandleCreateResponse /
        // HandleJoinResponse when the server echoes the assignment.  Empty
        // string when no membership has been confirmed yet.  Used by the
        // reserved-property guard in SetRoomProperties to determine whether
        // the local session holds the master-client role.
        private string _localPlayerId = string.Empty;

        // Pending CreateRoom requests carry a client-generated UUID (request_id)
        // alongside the original options and a wall-clock send timestamp.
        //
        // Correlation strategy:
        //   • Outbound: each CreateRoom call mints a v4-class GUID and stores
        //     the (id, options, sentAt) tuple in _pendingCreateById and the id
        //     in _pendingCreateOrder.
        //   • Inbound (TryMatchCreateResponse): when the server echoes a
        //     request_id, we look it up by id directly — order-independent,
        //     race-proof.  When the server omits it (current gateway), we fall
        //     back to head-of-_pendingCreateOrder.  Either way the entry is
        //     removed atomically.
        //   • Stale entries (no response within PendingCreateTtlSeconds) are
        //     swept on every CreateRoom and on ClearState — bounded growth.
        //
        // Wire-protocol coordination point, complete on all three sides:
        //   The RoomCreate (0x20) request payload appends an optional correlation
        //   trailer — [request_id_len:2 LE][request_id:32 ASCII hex]; the gateway
        //   keys its own idempotency table on that id and returns it in the
        //   response; ParseCreateRoomResponse surfaces it.  A gateway that
        //   predates the echo leaves it absent and FIFO applies automatically.
        //
        //   🔑 The echo was documented here as the SDK's operative correlation
        //   long before it existed: the gateway had been reading the id since its
        //   replay table shipped and never giving it back, so every match this
        //   correlator ever made was the FIFO fallback.  What the echo buys is
        //   not race-proofing under concurrent creates — FIFO was already correct
        //   for that — but the ability to recognise a reply that answers a
        //   request this client no longer holds, which is what a retransmitted
        //   create produces and what FIFO can only infer from an empty table.
        //
        //   Entries are swept on every CreateRoom call and on ClearState, so
        //   growth is bounded whether or not replies arrive.
        private readonly PendingCreateTable<CreateRoomOptions> _pendingCreates =
            new PendingCreateTable<CreateRoomOptions>(
                capacity: MaxPendingCreates,
                ttlSeconds: PendingCreateTtlSeconds);

        // Monotonic sample source for the pending-create TTL.  Per instance
        // rather than the static call, so a test can reach the deadline without
        // spending it — the same seam LobbyManager keeps for its own deadline,
        // and the reason this repair can be held by behaviour instead of by
        // text.  Production passes nothing and gets Stopwatch.
        private readonly Func<long> _nowTicks;

        private const int    MaxPendingCreates       = 16;
        // 30 s is a comfortable upper bound on a healthy round trip; anything
        // older is treated as a request the gateway will never answer.
        private const double PendingCreateTtlSeconds = 30.0;

        // ── Requests the server answers with silence ───────────────────────────
        //
        // A property write is accepted only at exactly the version it declares,
        // and the two host-only commands only from the seat that currently holds
        // the master role.  Each of those refusals is answered with NO PACKET.
        // The broadcast that follows an accepted request is the only evidence one
        // landed, so a request that draws none within this window is reported.
        //
        // ⛔ The floor is not a guess about the network: it is the Room Service's
        // OWN budget for these requests.  Every one of them is handled under a
        // context carrying `HandlerOptions.RequestTimeout`, ten seconds in the
        // deployed configuration, and that context covers the room read, the
        // transaction and the publish.  A client window inside that budget
        // reports a request the server is at that moment still working on, and
        // then the broadcast arrives: a false report, followed by the success it
        // denied.  Twelve seconds is that budget and a margin, which is why this
        // is far below the create table's thirty and nowhere near a round trip.
        //
        // ⚠️ The margin is for the two hops either side, not for a second
        // deadline: the gateway PUBLISHES the opcodes this window covers rather
        // than requesting them, so its own `nats_request_timeout_secs` — which
        // happens also to be twelve — governs them not at all.  Reading the two
        // as related would be a coincidence mistaken for a derivation.
        //
        // ⚠️ Right after, not inside: the Room Service commits in the repository
        // call and then publishes best-effort — a publish failure is logged and
        // the broadcast dropped.  So an ACCEPTED write can draw no broadcast,
        // and the report below is worded for that: it says the version did not
        // move here, which is true either way, and never that the server
        // refused.
        private const double PendingServerAckTtlSeconds = 12.0;

        // The window for the two opcodes the gateway REQUESTS rather than
        // publishes — RoomLeave and RoomList both reach the Room Service through
        // `forward_room_op` / `forward_room_list`, i.e. a NATS request-reply
        // under the gateway's own `nats_request_timeout_secs`.
        //
        // ⛔ Twelve is therefore the wrong number for them, and would have been
        // wrong in the direction that matters: the gateway's budget is twelve and
        // starts on RECEIPT, so a client window of twelve starting at the call
        // expires before the server chain has given up, every time, by one uplink
        // hop.  Fifteen is that budget and a hop either side, and it is the
        // number the lobby's own join window already carries.
        //
        // ⚠️ It is still not a bound on the worst case, and cannot be.  The
        // request travels the ARQ ladder, whose eight attempts span about eleven
        // seconds, so a copy delivered late leaves the server's budget starting
        // after this window has closed.  That is survivable only because the
        // report is a REPORT: it retires nothing, changes no state, and says in
        // its own words that a late reply is still accepted.  A deadline that
        // acted would need a bound this one does not have.
        private const double RequestReplyAckTtlSeconds = 15.0;

        // An outstanding LeaveRoom, and the two facts a driver needs about it.
        //
        // The request carries no correlation the reply echoes beyond the room's
        // own name, so one watch rather than a table: a reply cannot say which
        // copy of a leave it answers, and a second LeaveRoom for the same room
        // simply extends the wait and re-opens the report.  ⚠️ Not because only
        // one leave can be in flight — two copies of one leave routinely are,
        // which is the whole reason the refusal path below declines to spend the
        // latch.
        //
        // ⛔ Reaching the deadline reports the silence and does NOT retire
        // `_pendingLeaveRoomId`.  That field is the only thing a reply binds to,
        // and the refusal path below already declines to spend it for the same
        // reason: two copies of one leave can be in flight, their order is not
        // guaranteed, and a client that discarded the latch on a timeout would
        // drop the success that arrived a moment later — leaving it holding a
        // room it has already left, with no way back but a second LeaveRoom the
        // application has just been told failed.
        private long _pendingLeaveDeadlineTicks;
        private bool _pendingLeaveTimedOut;

        // The join's half of the same fact: this client has been TOLD its join
        // drew no answer, and the request it was told about is still outstanding
        // on the server.
        //
        // 🔑 A debt, not a latch.  `PendingJoinRetry` disarms itself before
        // calling `onExhausted`, so `wasPending` is false by the time a late
        // reply lands and nothing else survives to say the caller was already
        // given a failure.  This does, and it is spent by the reply that
        // contradicts it.
        //
        // ⛔ Cleared wherever a NEW join is armed.  A retry the application
        // issues BECAUSE of the timeout is answered by its own reply, and
        // announcing a supersession there would contradict a failure that
        // caller was never given.
        private bool _joinReportedExhausted;

        /// <summary>
        /// The label of the join this client is still entitled to a reply for —
        /// a room id from <see cref="JoinRoom"/>, a room code from
        /// <see cref="JoinRoomByCode"/>.
        /// </summary>
        /// <remarks>
        /// ⛔ `HandleJoinResponse` matched no request at all: it disarmed the
        /// ladder and applied whatever room the reply named.  Two consequences
        /// the acceptance argument does not cover, both driven as cases:
        /// <para>• A reply for room A answered an outstanding request for room
        /// B — B's ladder disarmed, the client seated in A, and B never answered
        /// and unable to time out, because nothing is armed to expire.</para>
        /// <para>• A reply arriving after a completed <c>LeaveRoom</c> put the
        /// client back into a room it had left.  The seat-leak argument that
        /// justifies accepting a late reply does not hold there: the leave
        /// already released the seat, so applying it is pure harm.</para>
        /// 🔑 This OUTLIVES the ladder on purpose — that is exactly what makes a
        /// late reply acceptable — and is cleared by
        /// <see cref="ForgetRoomScopedState"/>, so leaving a room revokes the
        /// entitlement without touching the retransmit machinery.
        /// </remarks>
        private string _joinIntentLabel;

        /// <summary>The room this client most recently left, if any.</summary>
        /// <remarks>
        /// ⛔ Outlives <see cref="ForgetRoomScopedState"/> on purpose — it is
        /// what that method has to leave behind so a reply still in flight for
        /// the abandoned room cannot put the player back into it.  Cleared when
        /// a new join is issued, so re-entering the same room is an ordinary
        /// join and not a special case.
        /// </remarks>
        private string _departedRoomId;

        // The gate for the line the debt above pays for.
        private long _lastJoinSupersededWarnTicks;

        /// <summary>Rate gate for a join reply that answers no outstanding request.</summary>
        private long _lastUnsolicitedJoinReplyWarnTicks;

        // An outstanding RoomList, the instant it stops being worth waiting for,
        // and the epoch that says which request a reply is answering.
        //
        // ⛔ One watch rather than a table, because 0x23 carries no request id and
        // the gateway answers only the session that asked — so a second ListRooms
        // extends the wait rather than opening a second one.  ⚠️ But "retire on
        // the next reply" is wrong for a reason the request side does not show:
        // the request is sent RELIABLE, the gateway builds a reply per copy it
        // receives, and a duplicate is therefore the ordinary outcome of loss
        // recovery — so a late duplicate of the FIRST query's reply would retire
        // the second query's watch and its silence would go unreported.
        //
        // The epoch is what a reply is measured against: a reply retires the
        // watch only if the query it could be answering is the one now pending.
        // Since the wire carries no id, that is a count of replies still owed —
        // one per query issued, decremented by each reply — and a duplicate spends
        // a debt that is not there.
        // ⛔ What the count CANNOT tell apart, because the wire carries no request
        // id: a duplicate of an earlier query's reply arriving after a later query
        // was issued.  It is indistinguishable from that query's own reply and
        // retires the watch, so the later query's silence goes unreported.  Only a
        // correlated reply removes that, which is a change to the gateway and the
        // SDK together under rule 1; what the count does close is the case the
        // client can decide — two queries outstanding and one answer.
        private long _roomListDeadlineTicks;
        private bool _roomListPending;
        private int  _roomListRepliesOwed;

        // Whether the last ListRooms this client issued went unanswered.
        //
        // `LastRoomListProblem` reports what was wrong with a list that ARRIVED,
        // and its own remarks call it the way a polling caller tells a project
        // with no rooms from one whose list is not being answered.  A list that
        // never arrives writes no outcome, so without this a caller that polls is
        // the one caller the timeout does not reach.
        private bool _roomListTimedOut;

        private readonly PendingPropertyWrites _pendingPropertyWrites =
            new PendingPropertyWrites();

        private readonly PendingHostCommands _pendingHostCommands =
            new PendingHostCommands();

        // ── Emission gates for wire-driven diagnostics ─────────────────────────
        //
        // Every packet a handler refuses is refused once per packet, and what
        // decides how often that happens is the sender.  A gateway relaying a
        // version it does not share, a truncating link, or a peer replaying one
        // malformed frame all produce the same shape: a console line per inbound
        // datagram, built and written on the main thread, burying every other
        // line while it lasts.  The transport layer already answers this — every
        // diagnostic in NetworkManager.ProcessPacket sits behind a latch, and
        // where two share one it says which and why — and these are the room
        // layer's half of the same discipline.
        //
        // 🔑 One gate per REASON, never one for the family.  A flood of one
        // packet type must not decide whether a different fault is reported;
        // the whole value of the line is which packet was refused.
        //
        // Per manager rather than per process.  ⚠️ That does not bound the
        // console — there is one console and two managers would write into it
        // twice — it bounds the manager, which is the thing a packet arrives at:
        // a session holds one, so per manager and per session are the same
        // ceiling for every deployment this SDK supports.  What it buys beyond
        // that is a gate that starts open for each, which is what lets a test of
        // a gated diagnostic read the rule rather than the residue of whatever
        // ran before it.  ⛔ The variable and RPC gates are static for the
        // opposite reason: those types have an instance per object, and one
        // datagram names many.  The subscriber-throw gate is static because the
        // method that reports it is — every SafeRaise overload reaches it.
        private long _lastMalformedRoomCreateWarnTicks;
        private long _lastMalformedRoomJoinWarnTicks;
        private long _lastMalformedJoinResponseWarnTicks;
        private long _lastMalformedPlayerJoinedWarnTicks;
        private long _lastMalformedRoomLeaveWarnTicks;
        private long _lastMalformedLeaveResponseWarnTicks;
        private long _lastStaleLeaveResponseWarnTicks;
        private long _lastUnmatchedCreateResponseWarnTicks;
        private long _lastMalformedPlayerLeftWarnTicks;

        // Throttles the notice that this client's own seat was released.
        private long _lastSeatReleasedWarnTicks;
        private long _lastMalformedRoomListWarnTicks;
        // One gate per reason, each a named field.
        //
        // ⚠️ These were an array indexed by the outcome, which was wrong three
        // ways. It was sized by the enum's COUNT and indexed by its VALUE — the
        // two agree only while the members are contiguous from zero, so a member
        // added as `= 10` would have borrowed a neighbour's gate, which is the
        // property the sizing comment claimed to guarantee. The comment also
        // cited a test that pinned it, and no such test existed. And a `ref long`
        // returned from a method is not a field, so the rule that every rate gate
        // names a field its type holds could not resolve it at all.
        private long _lastRoomListRefusedWarnTicks;
        private long _lastRoomListShortWarnTicks;
        private long _lastRoomListUnknownStatusWarnTicks;
        private long _lastMalformedMasterChangedWarnTicks;
        private long _lastMalformedKickWarnTicks;
        private long _lastMalformedSceneLoadedWarnTicks;
        private long _lastUnnameableRoomWarnTicks;
        private static long _lastSubscriberThrowWarnTicks;

        /// <summary>
        /// Reopen the subscriber-throw gate.  The gate behind it is static
        /// because the reporter is — every <c>SafeRaise</c> overload is — so a
        /// test that reads the rule rather than the residue of whatever ran
        /// before it has to be able to say where its own window starts.
        /// </summary>
        internal static void ResetSubscriberThrowGateForTest()
            => System.Threading.Interlocked.Exchange(ref _lastSubscriberThrowWarnTicks, 0);

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Current room the player is in. Null if not in a room.</summary>
        public RoomInfo CurrentRoom => _currentRoom;

        /// <summary>True when the local player is in a room.</summary>
        public bool IsInRoom => _currentRoom != null;

        /// <summary>
        /// True when the local player currently holds the master-client (host)
        /// role in <see cref="CurrentRoom"/>.  Returns <see langword="false"/>
        /// when not in a room, when the master id is unknown, or when the
        /// local player UUID has not yet been resolved by a successful
        /// Create/Join response.
        /// </summary>
        public bool IsMasterClient
        {
            get
            {
                if (_currentRoom == null) return false;
                if (string.IsNullOrEmpty(_localPlayerId)) return false;
                var master = _currentRoom.MasterId;
                return !string.IsNullOrEmpty(master) && master == _localPlayerId;
            }
        }

        /// <summary>
        /// Number of CreateRoom requests still awaiting a server response.
        /// Exposed to allow tests and diagnostic tooling to assert on
        /// in-flight book-keeping; production code should treat this as
        /// observational only.
        /// </summary>
        internal int PendingCreateCount => _pendingCreates.Count;

        /// <summary>
        /// True while this client has asked to enter a room and the reply has
        /// not been applied: a <c>JoinRoom</c>/<c>JoinRoomByCode</c> awaiting
        /// its response, or a <c>CreateRoom</c> awaiting the reply that hands
        /// off to the auto-join.
        ///
        /// <para>Read by <c>NetworkManager</c> to decide whether a room-scoped
        /// catch-up frame that arrived before the room was entered belongs to
        /// an entry in progress.  The gateway holds a joiner's personal replay
        /// until it binds the session to the room, and that binding answers a
        /// request this client made — so with nothing outstanding there is no
        /// reply for such a frame to have overtaken.</para>
        ///
        /// <para>Both flags are cleared inside the same synchronous call that
        /// applies the entry (<c>_pendingJoin.Disarm()</c> immediately precedes
        /// <c>ApplyRoomEntry</c>), and inbound packets are dispatched one at a
        /// time on the main thread, so no frame can be observed in the window
        /// between the clear and the state transition.  Matchmaking adoption
        /// does not route through either flag; its own
        /// <c>MatchmakingManager.IsMatchmaking</c> latch covers that path and
        /// the caller consults both.</para>
        /// </summary>
        internal bool IsRoomEntryOutstanding =>
            _pendingJoin.IsArmed || _pendingCreates.Count > 0;

        /// <summary>
        /// Subjects whose property write is still awaiting the broadcast that
        /// would prove it landed.  Observational — for tests and diagnostics.
        /// </summary>
        internal int PendingPropertyWriteCount => _pendingPropertyWrites.Count;

        /// <summary>
        /// Host-only commands still awaiting the broadcast that would prove the
        /// server acted on them.  Observational — for tests and diagnostics.
        /// </summary>
        internal int PendingHostCommandCount => _pendingHostCommands.Count;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>Fired when a CreateRoom request succeeds.</summary>
        public event Action<RoomInfo> OnRoomCreated;

        /// <summary>Fired when a JoinRoom/JoinRoomByCode request succeeds.</summary>
        public event Action<RoomInfo> OnRoomJoined;

        /// <summary>Fired when the local player successfully leaves a room.</summary>
        public event Action OnRoomLeft;

        /// <summary>
        /// Snapshot of the most recently-departed room.  Populated immediately
        /// before <see cref="OnRoomLeft"/> fires, so subscribers can read the
        /// prior room's id, code, host, etc. without having to cache a copy
        /// of <see cref="CurrentRoom"/> on the join path.  Cleared when the
        /// next room is successfully joined.  <see langword="null"/> until
        /// the first leave occurs.
        /// </summary>
        public RoomInfo LastLeftRoom { get; private set; }

        /// <summary>Fired when another player joins the current room.</summary>
        public event Action<PlayerInfo> OnPlayerJoined;

        /// <summary>Fired when another player leaves the current room.</summary>
        public event Action<string> OnPlayerLeft;

        /// <summary>
        /// <see langword="true"/> when the most recent <see cref="ListRooms"/>
        /// went unanswered for <see cref="RequestReplyAckTtlSeconds"/> seconds.
        /// Cleared by the next request and by any reply.
        /// </summary>
        /// <remarks>
        /// <see cref="LastRoomListProblem"/> reports what was wrong with a list
        /// that arrived, and a list that never arrives writes no outcome — so
        /// without this the caller that polls rather than subscribing is the one
        /// caller the timeout does not reach, while the member that exists for
        /// pollers still reads <c>Ok</c>.
        /// </remarks>
        public bool LastRoomListTimedOut => _roomListTimedOut;

        /// <summary>
        /// Fired when a room list arrives that is the project's rooms.
        /// </summary>
        /// <remarks>
        /// Not every response reaches this event. A request the server declined
        /// carries an empty list that is not the project's contents, and raising
        /// it here would tell the game, positively, that there are no rooms; so
        /// a refusal — and a status this build cannot read — is reported through
        /// <see cref="OnRoomError"/> and left off this channel. A list that
        /// arrived short IS raised here, and reported as well.
        /// <see cref="LastRoomListProblem"/> carries the same news to a caller
        /// that polls rather than subscribes.
        /// </remarks>
        public event Action<RoomInfo[]> OnRoomListReceived;

        /// <summary>Fired when a room request fails (create, join, leave).</summary>
        public event Action<string> OnRoomError;

        /// <summary>
        /// Fired after the server accepts a <c>RoomPropertyUpdate</c> and
        /// broadcasts the new state to all clients in the room.  The argument
        /// is the new <see cref="RoomInfo"/> snapshot;
        /// <see cref="RoomInfo.Properties"/> reflects the authoritative
        /// post-update map.  Subscribers that need a delta should diff against
        /// <see cref="CurrentRoom"/> captured BEFORE the event fires — the
        /// RoomManager swaps <see cref="CurrentRoom"/> to the new snapshot
        /// BEFORE invoking this event.
        /// </summary>
        public event Action<RoomInfo> OnRoomPropertiesChanged;

        /// <summary>
        /// Fired when the delta a <c>room_properties_updated</c> broadcast
        /// carried named the reserved <see cref="ReservedPropertyKeys.Scene"/>
        /// key — whatever value it wrote, and whether or not that value differs
        /// from the one the room already held.  The argument is the snapshot
        /// after the merge.
        /// </summary>
        /// <remarks>
        /// <see cref="OnRoomPropertiesChanged"/> cannot answer this question.
        /// It carries the merged map, in which a write of the scene the room is
        /// already on is indistinguishable from a write that never named the
        /// key — and re-loading the current scene is what a round restart, a
        /// rematch and a respawn into the same map all are.  The distinction
        /// exists only in the delta, and the delta exists only here.
        /// <para>
        /// Internal, and deliberately outside
        /// <see cref="AdoptSubscribersFrom"/> — which is safe for one reason,
        /// and not the obvious one.  It is NOT that a replacement manager holds
        /// no room: adoption carries <see cref="OnRoomJoined"/>, so a
        /// replacement can enter a room and then take a property write, all
        /// before anything re-binds.  It is that the re-bind happens first.
        /// <c>NetworkManager</c> runs at execution order −1000 and drives
        /// <c>NetworkSceneManager.Tick</c>, whose first act is to re-bind, while
        /// inbound packets are delivered by the dispatcher behind it — so within
        /// any frame the binding is refreshed before a broadcast can be handled.
        /// ⚠️ That ordering is load-bearing; a build that delivers packets ahead
        /// of the SDK's own <c>Update</c> must carry this event in adoption too.
        /// </para>
        /// </remarks>
        internal event Action<RoomInfo> OnRoomSceneWritten;

        /// <summary>
        /// Fired after the server accepts a <c>PlayerPropertyUpdate</c> and
        /// broadcasts the new state to all clients.  The first argument is the
        /// player UUID; the second is the updated <see cref="PlayerInfo"/> snapshot.
        /// </summary>
        public event Action<string, PlayerInfo> OnPlayerPropertiesChanged;

        /// <summary>
        /// Fired when the room's master client changes, either automatically
        /// (FIFO promotion after the previous master disconnected) or manually
        /// (a <see cref="TransferMasterClient"/> request was accepted).  The
        /// arguments are <c>(previousMasterId, newMasterId)</c>; either may be
        /// an empty string when unknown (e.g. initial assignment).
        /// </summary>
        public event Action<string, string> OnMasterClientChanged;

        /// <summary>
        /// Fired when the host removes a player from the room via
        /// <see cref="KickPlayer"/>.  The arguments are
        /// <c>(kickerId, targetPlayerId)</c>.  Every client in the room
        /// receives this event — the kicked client observes their own ID as
        /// the target and should treat it as an authoritative disconnect.
        /// </summary>
        public event Action<string, string> OnPlayerKicked;

        /// <summary>
        /// Fired when every player in the room has reported scene-loaded
        /// readiness for the authoritative scene (as stored in the reserved
        /// <see cref="ReservedPropertyKeys.Scene"/> property).  The argument
        /// is the scene name that just finished loading for everyone.
        /// </summary>
        public event Action<string> OnAllPlayersSceneLoaded;

        /// <summary>
        /// Take over the subscriber lists of the instance this one replaces.
        /// </summary>
        /// <remarks>
        /// A RoomManager is rebuilt whenever the connection is, so a handler
        /// registered against the manager the application could see would
        /// otherwise be dropped on the first connect and again on every
        /// reconnect attempt — silently, because the SDK re-attaches its own
        /// handlers and half the event block keeps firing.  Subscribing before
        /// <c>Connect()</c> is what the getting-started walkthrough and the
        /// shipped sample both do.
        ///
        /// <para>A handler the replacement is already holding is not carried a
        /// second time — see <see cref="RTMPE.Core.SubscriberAdoption"/>, which
        /// is where that rule lives for all three managers.  <c>NetworkManager</c>
        /// still detaches its own handlers before rebuilding and
        /// <c>NetworkSceneManager</c> still detaches from an instance before
        /// binding to it; both remain correct on their own terms, and neither is
        /// now the only thing standing between a rebuild and a doubled
        /// event.</para>
        ///
        /// <para>⛔ What adoption cannot see is a subscription made after it
        /// runs.  It is the last step of the rebuild, so an application that
        /// subscribes when it is told the session is up adds a handler that was
        /// already carried across, and the count then grows with the number of
        /// reconnects.  Subscribe once, before <c>Connect()</c>, or pair every
        /// <c>+=</c> with the <c>-=</c> that precedes it.</para>
        /// </remarks>
        internal void AdoptSubscribersFrom(RoomManager previous)
        {
            if (previous == null || ReferenceEquals(previous, this)) return;

            OnRoomCreated = SubscriberAdoption.Carry(OnRoomCreated, previous.OnRoomCreated);
            OnRoomJoined = SubscriberAdoption.Carry(OnRoomJoined, previous.OnRoomJoined);
            OnRoomLeft = SubscriberAdoption.Carry(OnRoomLeft, previous.OnRoomLeft);
            OnPlayerJoined = SubscriberAdoption.Carry(OnPlayerJoined, previous.OnPlayerJoined);
            OnPlayerLeft = SubscriberAdoption.Carry(OnPlayerLeft, previous.OnPlayerLeft);
            OnRoomListReceived =
                SubscriberAdoption.Carry(OnRoomListReceived, previous.OnRoomListReceived);
            OnRoomError = SubscriberAdoption.Carry(OnRoomError, previous.OnRoomError);
            OnRoomPropertiesChanged =
                SubscriberAdoption.Carry(OnRoomPropertiesChanged, previous.OnRoomPropertiesChanged);
            OnPlayerPropertiesChanged =
                SubscriberAdoption.Carry(OnPlayerPropertiesChanged, previous.OnPlayerPropertiesChanged);
            OnMasterClientChanged =
                SubscriberAdoption.Carry(OnMasterClientChanged, previous.OnMasterClientChanged);
            OnPlayerKicked = SubscriberAdoption.Carry(OnPlayerKicked, previous.OnPlayerKicked);
            OnAllPlayersSceneLoaded =
                SubscriberAdoption.Carry(OnAllPlayersSceneLoaded, previous.OnAllPlayersSceneLoaded);
        }

        // Emission gate for the CreateRoom failure diagnostic.  The message it
        // carries is the server's, relayed from the wire, and a client that
        // retries a refused create reports it every attempt.
        private static long _lastRoomOpFailureWarnTicks;

        // 🚨 Its own, where the join reply used to share the create reply's.
        // Both carry a reason the SERVER chose, and a run of one refusal was
        // deciding whether the other was ever reported — which is the property
        // the rest of these gates are written to have.
        private static long _lastJoinFailedWarnTicks;

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Create a RoomManager. Called internally by <see cref="NetworkManager"/>.
        /// </summary>
        /// <param name="packetBuilder">Shared packet builder (for sequence numbering).</param>
        /// <param name="sendOwned">Delegate to send a fully built packet without copying.</param>
        /// <param name="getState">Delegate to read the current <see cref="NetworkState"/>.</param>
        /// <param name="onLocalPlayerIdResolved">Raised once the server names this client.</param>
        /// <param name="nowTicks">
        /// Monotonic tick source for the pending-create TTL.  Defaults to
        /// <see cref="PendingCreateTable{T}.NowTicks"/>; supplied by tests that
        /// need to cross the deadline without waiting it out.
        /// </param>
        internal RoomManager(
            PacketBuilder packetBuilder,
            Action<byte[]> sendOwned,
            Func<NetworkState> getState,
            Action<string> onLocalPlayerIdResolved = null,
            Func<long> nowTicks = null)
        {
            _packetBuilder             = packetBuilder ?? throw new ArgumentNullException(nameof(packetBuilder));
            _sendOwned                 = sendOwned     ?? throw new ArgumentNullException(nameof(sendOwned));
            _getState                  = getState      ?? throw new ArgumentNullException(nameof(getState));
            _onLocalPlayerIdResolved   = onLocalPlayerIdResolved;
            _nowTicks                  = nowTicks ?? PendingCreateTable<CreateRoomOptions>.NowTicks;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Request the server to create a new room.
        /// The result arrives asynchronously via <see cref="OnRoomCreated"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void CreateRoom(CreateRoomOptions options = null)
        {
            if (!RequireConnected("CreateRoom")) return;

            // Sweep first so a back-pressured client whose responses are missing
            // does not get permanently locked out by stale book-keeping.
            // Take a single monotonic-clock sample so Sweep and Register share
            // a consistent view of "now" within this request-handling pass.
            long nowTicks = _nowTicks();
            SweepExpiredCreates(nowTicks);

            if (_pendingCreates.IsFull)
            {
                Debug.LogError("[RTMPE] RoomManager.CreateRoom: too many in-flight room creates. " +
                               "Wait for the server to respond before calling CreateRoom again.");
                return;
            }

            var opts = options ?? new CreateRoomOptions();

            // Ahead of the registration, because a request that cannot be sent
            // must not leave a pending entry behind for the sweep to retire.
            if (!RequireAcceptable("CreateRoom", RoomFieldLimits.ValidateRoomName(opts.Name))) return;
            if (!RequireAcceptable("CreateRoom", RoomFieldLimits.ValidateMaxPlayers(opts.MaxPlayers))) return;

            var requestId = _pendingCreates.Register(opts, nowTicks);

            // Pass the registered id to the builder so it is included in the
            // wire payload.  The gateway echoes it back in the response, which
            // is what lets a reply be matched to the request it answers — and a
            // reply that matches none be recognised rather than applied.  A
            // gateway that predates the echo omits the field and the FIFO
            // fallback path remains active.
            if (!TryBuild("CreateRoom",
                    () => _packetBuilder.Build(
                        PacketType.RoomCreate, PacketFlags.Reliable,
                        RoomPacketBuilder.BuildCreateRoomPayload(opts, requestId)),
                    out var packet))
            {
                // The entry above is retired rather than left for the sweep.
                // ⛔ Unreachable today — the widest create payload is under 300
                // bytes against a datagram budget four times that, and the two
                // checks the builder re-runs have already passed — but the rule
                // stated above this registration is that a request which cannot
                // be sent leaves nothing behind, and an exception has one more
                // way in than the checks do. Left in place, the entry would time
                // out thirty seconds later and report a SECOND failure for a
                // call already reported as failed.
                _pendingCreates.TryMatch(requestId, out _);
                return;
            }

            _sendOwned(packet);
        }

        /// <summary>
        /// Request to join an existing room by its server-assigned UUID.
        /// The result arrives asynchronously via <see cref="OnRoomJoined"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void JoinRoom(string roomId, JoinRoomOptions options = null)
        {
            if (!RequireConnected("JoinRoom")) return;
            if (string.IsNullOrEmpty(roomId))
            {
                Debug.LogError("[RTMPE] RoomManager.JoinRoom: roomId must not be null or empty.");
                return;
            }

            if (options != null
                && !RequireAcceptable("JoinRoom", RoomFieldLimits.ValidateDisplayName(options.DisplayName)))
                return;

            if (!TryBuild("JoinRoom",
                    () => _packetBuilder.Build(
                        PacketType.RoomJoin, PacketFlags.Reliable,
                        RoomPacketBuilder.BuildJoinRoomPayload(roomId, null, options)),
                    out var packet))
                return;

            _joinReportedExhausted = false;
            _joinIntentLabel = roomId;
            _departedRoomId = null;
            _pendingJoin.Arm(packet, roomId, PendingJoinRetry.NowSeconds());
            _sendOwned(packet);
        }

        /// <summary>
        /// Request to join an existing room by its 6-character join code.
        /// The result arrives asynchronously via <see cref="OnRoomJoined"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void JoinRoomByCode(string roomCode, JoinRoomOptions options = null)
        {
            if (!RequireConnected("JoinRoomByCode")) return;
            if (string.IsNullOrEmpty(roomCode))
            {
                Debug.LogError("[RTMPE] RoomManager.JoinRoomByCode: roomCode must not be null or empty.");
                return;
            }

            // Raised to the alphabet's case first, so the code that is judged,
            // sent and recorded is the one the server will compare against.
            roomCode = RoomFieldLimits.NormaliseRoomCode(roomCode);
            if (!RequireAcceptable("JoinRoomByCode", RoomFieldLimits.ValidateRoomCode(roomCode))) return;
            if (options != null
                && !RequireAcceptable("JoinRoomByCode", RoomFieldLimits.ValidateDisplayName(options.DisplayName)))
                return;

            if (!TryBuild("JoinRoomByCode",
                    () => _packetBuilder.Build(
                        PacketType.RoomJoin, PacketFlags.Reliable,
                        RoomPacketBuilder.BuildJoinRoomPayload(null, roomCode, options)),
                    out var packet))
                return;

            _joinReportedExhausted = false;
            _joinIntentLabel = roomCode;
            _departedRoomId = null;
            _pendingJoin.Arm(packet, roomCode, PendingJoinRetry.NowSeconds());
            _sendOwned(packet);
        }

        /// <summary>
        /// Request to leave the current room.
        /// The result arrives asynchronously via <see cref="OnRoomLeft"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void LeaveRoom()
        {
            if (!RequireInRoom("LeaveRoom")) return;

            // The room is read here, once, and travels in the request.  Both
            // ends of the operation are then bound to the room the caller was
            // in when it asked, rather than to whichever one either end holds
            // when a re-sent copy is delivered or answered.
            string leaving = _currentRoom.RoomId;
            _pendingLeaveRoomId = leaving;
            _pendingLeaveRefused = false;
            _pendingLeaveTimedOut = false;
            _pendingLeaveDeadlineTicks = ReplyDeadline();

            var payload = RoomPacketBuilder.BuildLeaveRoomPayload(leaving);
            var packet  = _packetBuilder.Build(PacketType.RoomLeave, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        /// <summary>
        /// Request the list of available rooms from the server.
        /// The result arrives asynchronously via <see cref="OnRoomListReceived"/>
        /// when it is the project's rooms, or <see cref="OnRoomError"/> when the
        /// server declined the request — the two are different answers and the
        /// second used to arrive as the first, carrying an empty list.
        /// <see cref="LastRoomListProblem"/> holds the same answer for a caller
        /// that polls.
        /// </summary>
        /// <param name="publicOnly">When true, exclude private rooms.</param>
        public void ListRooms(bool publicOnly = true)
        {
            if (!RequireConnected("ListRooms")) return;

            _roomListPending       = true;
            _roomListTimedOut      = false;
            _roomListDeadlineTicks = ReplyDeadline();
            _roomListRepliesOwed++;

            var payload = RoomPacketBuilder.BuildListRoomsPayload(publicOnly);
            var packet  = _packetBuilder.Build(PacketType.RoomList, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        // ── Custom Properties ──────────────────────────────────────────────────

        /// <summary>
        /// Request the server to update one or more room-level custom properties.
        /// Only the room host may issue this call; the server rejects all others
        /// with no broadcast.
        ///
        /// The update is asynchronous — <see cref="OnRoomPropertiesChanged"/>
        /// fires after the server accepts and broadcasts the change.  On
        /// rejection (non-host, version conflict, oversized) no event fires;
        /// the client must rely on its local <see cref="RoomInfo.PropertiesVersion"/>
        /// remaining unchanged to detect failure.
        /// </summary>
        /// <param name="properties">One or more properties to set.  A
        /// property key limit of <see cref="PropertyLimits.MaxPropertiesPerRoom"/>
        /// is enforced client-side before the packet leaves the SDK.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the local player is not currently in a room.
        /// </exception>
        public void SetRoomProperties(IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (!RequireNameableRoom("SetRoomProperties")) return;
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            // Keys with the reserved "__" prefix are server-managed (e.g.
            // __scene, __scene_additive); the gateway silently discards
            // unauthorised writes to them and never echoes a property update.
            // Surfacing the rejection client-side turns an opaque "no-op"
            // into an actionable diagnostic — the developer sees exactly
            // which key was rejected and why, instead of debugging a missing
            // OnRoomPropertiesChanged.  Non-master writers are blocked
            // outright; the request is not sent.
            if (!IsMasterClient)
            {
                foreach (var key in properties.Keys)
                {
                    if (ReservedPropertyKeys.IsReserved(key))
                    {
                        var error =
                            $"SetRoomProperties: key '{key}' is reserved (prefix '{ReservedPropertyKeys.Prefix}') " +
                            "and may only be written by the room's master client. Request not sent.";
                        Debug.LogWarning("[RTMPE] RoomManager." + error);
                        SafeRaise(OnRoomError, error);
                        return;
                    }
                }
            }

            // ⛔ A non-master's write is NOT refused here, and that is a
            // decision rather than an omission.
            //
            // Every room-property write is host-only — the Room Service locks
            // the room's HOST row for this session
            // (`sqlLockHostPlayerForSession`) and answers a non-host with
            // `ErrUnauthorized`, publishing nothing — so refusing locally looks
            // like the obvious repair, and it was written that way first.
            //
            // 🚨 It is unsafe, and an adversarial pass demonstrated why: this
            // client cannot know that the master id it holds is CURRENT.
            // `MasterId` is derived from `Players[].IsHost`, which the wire
            // fills: `RehostRoom` rewrites it on a promotion — and returns the
            // room UNCHANGED when the promoted master is absent from the local
            // roster copy — while a `PlayerJoined` notification installs
            // `IsHost` from the packet and a prune leaves it empty.  The `MasterClientChanged` event that would correct
            // it is fanned out as three unacknowledged copies with no
            // retransmit ladder (`nats/room_events.rs`), and nothing
            // re-derives the host from an authoritative source afterwards.  So
            // a stale id is reachable, and a local refusal built on one refuses
            // THE ACTUAL HOST — permanently, with nothing sent, on a client the
            // server would have accepted.  That is the `G1-04` hazard exactly:
            // depth must not refuse what the authority would admit, and here
            // depth cannot know what the authority holds.
            //
            // 🔑 What the caller gets instead is the report below: the write
            // goes, the server refuses it in silence, and the pending-write
            // watch says so.  A late answer beats a permanent wrong one.
            //
            // The player-seat check on `SetPlayerProperties` is NOT the same
            // question: `_localPlayerId` is written only by an entry reply and
            // cleared only on exit, so it has no staleness path of this kind.

            // The room is read once, here, and travels in the payload.  This
            // packet is sent reliably: a lost acknowledgement re-sends these
            // exact bytes, and the server used to decide which room they were
            // for from wherever the session was when the copy landed.
            string writingTo = _currentRoom.RoomId;
            int expectedVersion = _currentRoom.PropertiesVersion + 1;
            byte[] payload = PropertyPacketBuilder.BuildRoomPayload(
                writingTo, expectedVersion, properties);
            byte[] packet  = _packetBuilder.Build(PacketType.RoomPropertyUpdate, PacketFlags.Reliable, payload);
            _sendOwned(packet);
            ArmPropertyWriteWatch(PendingPropertyWrites.RoomSubject, expectedVersion);
        }

        /// <summary>
        /// Convenience overload for updating a single room-level property.
        /// </summary>
        public void SetRoomProperty(string key, PropertyValue value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key must not be null or empty.", nameof(key));
            SetRoomProperties(new Dictionary<string, PropertyValue> { { key, value } });
        }

        /// <summary>
        /// Update one or more properties for a specific player.  The local
        /// session must own <paramref name="playerId"/> — the server enforces
        /// the self-only invariant and rejects mismatches.
        /// </summary>
        /// <param name="playerId">The player UUID whose properties to update.
        /// Must equal <see cref="NetworkManager.LocalPlayerStringId"/> — a
        /// foreign id is refused here, with an <see cref="OnRoomError"/>, rather
        /// than sent for the server to drop in silence.</param>
        /// <param name="properties">The properties to set.</param>
        public void SetPlayerProperties(
            string playerId,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (!RequireNameableRoom("SetPlayerProperties")) return;
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("playerId must not be null or empty.", nameof(playerId));
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            // The server accepts a player-property write only from the session
            // that holds that seat (`UpdatePlayerPropertiesIfSelf` →
            // `ErrUnauthorized`), and answers a refusal with no packet.  This
            // method's own documentation has always said so; nothing checked
            // it, although the value that decides it is the one field this
            // class keeps for the purpose.
            //
            // ⛔ One-sided on the same terms as the room write above: a client
            // that has not yet been told its own seat cannot name a foreign one.
            if (ObjectLifecycleAuthority.RefusesForeignAuthority(playerId, _localPlayerId))
            {
                var refusal =
                    $"SetPlayerProperties: playerId '{UntrustedLogText.Sanitise(playerId)}' is not " +
                    "the local player. The server accepts a player-property write only from the " +
                    "session that owns the seat and answers a refusal with no packet at all, so " +
                    "the request is not sent. Ask that player over an RPC.";
                Debug.LogWarning("[RTMPE] RoomManager." + refusal);
                SafeRaise(OnRoomError, refusal);
                return;
            }

            // Look up the player's local view of PropertiesVersion so the
            // request carries the correct expected-version tag.  An unknown
            // player id starts from version 0 + 1 = 1.
            int currentVersion = 0;
            foreach (var p in _currentRoom.Players)
            {
                if (p.PlayerId == playerId)
                {
                    currentVersion = p.PropertiesVersion;
                    break;
                }
            }

            // As above, and with more riding on it: the expected-version check
            // that would refuse a stale room-level write does not refuse a
            // stale player-level one, because a player's version restarts at
            // zero in every room.
            string writingTo = _currentRoom.RoomId;
            int expectedVersion = currentVersion + 1;
            byte[] payload = PropertyPacketBuilder.BuildPlayerPayload(
                writingTo, playerId, expectedVersion, properties);
            byte[] packet  = _packetBuilder.Build(PacketType.PlayerPropertyUpdate, PacketFlags.Reliable, payload);
            _sendOwned(packet);
            ArmPropertyWriteWatch(playerId, expectedVersion);
        }

        // ── Master Client ──────────────────────────────────────────────────────

        /// <summary>
        /// Request the server to hand the master-client role to
        /// <paramref name="targetPlayerId"/>.  Only the current master may
        /// issue this call, and the server refuses anyone else with no reply at
        /// all — so a request that draws no broadcast within
        /// <see cref="PendingServerAckTtlSeconds"/> seconds is reported through
        /// <see cref="OnRoomError"/> rather than left silent.
        ///
        /// The transition fires <see cref="OnMasterClientChanged"/> on every
        /// client in the room (including the sender) once the server accepts
        /// and broadcasts.  No local state change happens synchronously — the
        /// caller must react to the event, not the return value.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="targetPlayerId"/> is null or empty.
        /// </exception>
        public void TransferMasterClient(string targetPlayerId)
        {
            if (!RequireNameableRoom("TransferMasterClient")) return;
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));

            // Named rather than inferred at delivery: a re-sent transfer is
            // otherwise a promotion in whichever room the sender has reached.
            byte[] payload = MasterClientPacketBuilder.BuildTransferPayload(
                _currentRoom.RoomId, targetPlayerId);
            byte[] packet  = _packetBuilder.Build(PacketType.MasterClientTransfer, PacketFlags.Reliable, payload);
            _sendOwned(packet);
            ArmHostCommandWatch(HostCommandKind.MasterTransfer, targetPlayerId);
        }

        /// <summary>
        /// Request the server to remove <paramref name="targetPlayerId"/> from
        /// the room.  Only the current master may issue this call, and the
        /// server refuses anyone else with no reply at all — so a request that
        /// draws no broadcast within <see cref="PendingServerAckTtlSeconds"/>
        /// seconds is reported through <see cref="OnRoomError"/> rather than
        /// left silent.
        ///
        /// Every remaining client (including the kicker) receives
        /// <see cref="OnPlayerKicked"/> once the server accepts.  The kicked
        /// client additionally receives an authoritative
        /// <see cref="OnPlayerLeft"/> — SDK consumers should treat either
        /// event as the signal to drop the player from their local roster.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="targetPlayerId"/> is null or empty.
        /// </exception>
        public void KickPlayer(string targetPlayerId)
        {
            if (!RequireNameableRoom("KickPlayer")) return;
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));

            // Named rather than inferred at delivery: a re-sent kick is
            // otherwise an ejection from whichever room the sender has reached.
            byte[] payload = MasterClientPacketBuilder.BuildKickPayload(
                _currentRoom.RoomId, targetPlayerId);
            byte[] packet  = _packetBuilder.Build(PacketType.KickPlayer, PacketFlags.Reliable, payload);
            _sendOwned(packet);
            ArmHostCommandWatch(HostCommandKind.Kick, targetPlayerId);
        }

        // ── Scene readiness ────────────────────────────────────────────────────

        /// <summary>
        /// Notify the server that this client has finished loading
        /// <paramref name="sceneName"/>.  Idempotent — the server treats
        /// duplicate reports from the same player as a no-op.
        ///
        /// Typically invoked by <c>NetworkSceneManager</c> automatically; most
        /// application code should not need to call this directly.
        /// </summary>
        public void ReportSceneLoaded(string sceneName) => TryReportSceneLoaded(sceneName);

        /// <summary>
        /// <see cref="ReportSceneLoaded"/>, answering whether the report left
        /// the client.  The state guard above it declines silently, and a
        /// caller that records "reported" on the strength of the call having
        /// returned then says so about a report the server never received.
        /// </summary>
        /// <remarks>
        /// A second entry point rather than a return value on the first: the
        /// method is public and shipped, and a signature that gains a result is
        /// a signature every assembly compiled against it no longer finds.  The
        /// precondition stays stated once, here, rather than mirrored by the
        /// caller — a copy of a guard is a copy that stops agreeing with it.
        /// </remarks>
        internal bool TryReportSceneLoaded(string sceneName)
        {
            if (!RequireNameableRoom("ReportSceneLoaded")) return false;
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentException("sceneName must not be null or empty.", nameof(sceneName));

            // Named rather than inferred at delivery: a re-sent report is
            // otherwise readiness declared for a room this client has left.
            byte[] payload = MasterClientPacketBuilder.BuildSceneLoadedPayload(
                _currentRoom.RoomId, sceneName);
            byte[] packet  = _packetBuilder.Build(PacketType.SceneLoaded, PacketFlags.Reliable, payload);
            _sendOwned(packet);
            SafeRaise(OnSceneReportSent, sceneName);
            return true;
        }

        /// <summary>
        /// Raised when a scene-readiness report for <c>sceneName</c> has been
        /// written to the wire, whichever call produced it.
        /// </summary>
        /// <remarks>
        /// ⛔ Here rather than in <see cref="NetworkSceneManager"/>'s own
        /// <c>ReportReady</c>, and the difference is a public method: this class
        /// exposes <see cref="ReportSceneLoaded"/> too, so an application using
        /// the room API directly reports to the server without the scene
        /// manager learning of it.  The scene manager reads this latch to tell a
        /// broadcast about its own round from one still in flight for the
        /// previous load, and a latch that only some reports set would refuse a
        /// broadcast that was genuinely about the round it is waiting on.
        /// </remarks>
        internal event Action<string> OnSceneReportSent;

        // ── Packet handling (called by NetworkManager.ProcessPacket) ───────────

        /// <summary>
        /// Route an inbound room packet to the appropriate handler.
        /// Called by NetworkManager on the main thread.
        /// </summary>
        internal void HandleRoomPacket(PacketType type, byte[] payload)
        {
            switch (type)
            {
                case PacketType.RoomCreate:          HandleCreateResponse(payload);          break;
                case PacketType.RoomJoin:            HandleJoinPacket(payload);              break;
                case PacketType.RoomLeave:           HandleLeavePacket(payload);             break;
                case PacketType.RoomList:            HandleListResponse(payload);            break;
                case PacketType.MasterClientChanged: HandleMasterClientChanged(payload);     break;
                case PacketType.KickPlayer:          HandlePlayerKicked(payload);            break;
                case PacketType.SceneLoaded:         HandleAllPlayersSceneLoaded(payload);   break;
            }
        }

        /// <summary>
        /// Clear room state when the connection drops.
        /// Called by NetworkManager during cleanup.
        /// </summary>
        internal void ClearState()
        {
            ForgetRoomScopedState();
            _pendingCreates.Clear();

            // Connection-scoped rather than room-scoped: ListRooms is answerable
            // outside a room, so its watch belongs to the session and not to the
            // room state cleared above.
            _roomListPending     = false;
            _roomListRepliesOwed = 0;
        }

        /// <summary>
        /// Retire CreateRoom requests the server has not answered within
        /// <see cref="PendingCreateTtlSeconds"/>, reporting each one to the
        /// application.
        /// </summary>
        /// <remarks>
        /// Callers that are already handling a request pass their own
        /// monotonic sample so the sweep and the registration that follows it
        /// share one view of "now"; the per-frame driver takes a fresh one.
        /// </remarks>
        private void SweepExpiredCreates(long nowTicks)
        {
            // The sweep completes before the first report is raised — see
            // SweepExpired's own remarks. A handler for OnRoomError may
            // legitimately disconnect or retry, and both reach the table.
            var retired = _pendingCreates.SweepExpired(nowTicks);
            for (int i = 0; i < retired.Count; i++)
            {
                Guid staleId = retired[i].id;
                Debug.LogWarning(
                    $"[RTMPE] RoomManager: CreateRoom request {staleId} timed out " +
                    $"after {PendingCreateTtlSeconds}s with no response. Discarding.");
                SafeRaise(OnRoomError, "CreateRoom timed out");
            }
        }

        /// <summary>
        /// Drive the outstanding JoinRoom retransmit and retire timed-out
        /// CreateRoom requests.  Called once per frame by
        /// <see cref="NetworkManager"/> on the main thread.  A no-op whenever
        /// neither is outstanding, and allocation-free in that state — which is
        /// what makes running it on every frame affordable, and is measured
        /// against this method rather than against its parts.  When the
        /// retransmit budget is spent, or a create passes
        /// its TTL, it raises <see cref="OnRoomError"/> so a request that never
        /// draws a reply surfaces a timeout instead of hanging the caller.
        /// </summary>
        /// <remarks>
        /// ⛔ The create sweep was previously reachable only from inside
        /// <see cref="CreateRoom"/>, so a client that issued one create and
        /// never heard back was never told and — because
        /// <see cref="IsRoomEntryOutstanding"/> counts the table — held that
        /// predicate open for the rest of the session. Nothing else consults a
        /// clock on this path.
        ///
        /// ⚠️ Calling the sweep on every frame is only free because
        /// <see cref="PendingCreateTable{T}.SweepExpired"/> answers an empty
        /// table with a shared empty result. It was written as a C# iterator,
        /// where *calling* it allocated a state machine and enumerating it
        /// boxed the enumerator; a driver that runs sixty times a second is
        /// what made that worth fixing at the table rather than guarding here.
        /// </remarks>
        internal void Tick()
        {
            // One instant for the deadline sweeps below. Each compares against
            // an absolute deadline, and sampling the clock once per call asks a
            // slightly different question at each of them for no reason anybody
            // could name. ⚠️ The join ladder is not among them: it reads its own
            // clock, in seconds, inside PendingJoinRetry.
            long nowTicks = _nowTicks();

            SweepExpiredCreates(nowTicks);

            _pendingJoin.Tick(
                PendingJoinRetry.NowSeconds(),
                _retransmitJoin ??= RetransmitJoin,
                _reportJoinExhausted ??= ReportJoinExhausted);

            ReportUnansweredPropertyWrites(nowTicks);
            ReportUnansweredHostCommands(nowTicks);
            ReportUnansweredLeave(nowTicks);
            ReportUnansweredRoomList(nowTicks);
        }

        /// <summary>
        /// Report a <see cref="LeaveRoom"/> the server never answered.
        /// </summary>
        /// <remarks>
        /// The server answers a leave either way — <c>forward_seated_leave</c>
        /// always encodes a response — so what this reports is the datagram that
        /// did not arrive, on either side of the round trip, or a gateway whose
        /// own request to the Room Service timed out.  Before it, the request
        /// that drew nothing produced nothing at all — no event, no console line
        /// — and the application waited on <c>OnRoomLeft</c> for the life of the
        /// connection while still holding the room.
        ///
        /// <para>⛔ The report does not retire the request, and it changes
        /// nothing an application can observe — only its own once-per-request
        /// latch.  A leave this client cannot confirm is not a
        /// leave it may assume: the seat is the server's to clear, and a manager
        /// that tore the room down on a timeout would answer a lost datagram by
        /// making the two sides disagree about where the player is.  What the
        /// timeout buys the caller is the one thing it did not have — being
        /// told.</para>
        ///
        /// <para>The debt is spent before the raise, on the same terms as the
        /// lobby's abandoned join: a subscriber that calls back into this manager
        /// must not find the report still owed.</para>
        /// </remarks>
        private void ReportUnansweredLeave(long nowTicks)
        {
            if (_pendingLeaveRoomId == null) return;
            if (_pendingLeaveTimedOut) return;
            // 🚨 A refusal is an ANSWER.  The request stays outstanding after one
            // — deliberately, so a success arriving behind it is still accepted —
            // and a watch reading only that latch therefore spoke a second time,
            // twelve seconds after `OnRoomError` had already carried the server's
            // own refusal, to say nothing came back.  That is the second report
            // for one call `_pendingLeaveRefused` exists to prevent, reached
            // through a channel the flag did not gate.
            if (_pendingLeaveRefused) return;
            if (nowTicks < _pendingLeaveDeadlineTicks) return;

            _pendingLeaveTimedOut = true;

            var error =
                $"LeaveRoom drew no answer this client could act on within " +
                $"{RequestReplyAckTtlSeconds}s. The request is still outstanding and a late reply " +
                "is still accepted — this reports the silence and not a cause. The room has NOT " +
                "been left as far as this client can tell, so the seat, the roster and CurrentRoom " +
                "are unchanged.";
            if (WarnGate.ShouldEmit(ref _lastLeaveUnansweredWarnTicks))
                Debug.LogWarning("[RTMPE] RoomManager: " + error);
            SafeRaise(OnRoomError, error);
        }

        private long _lastLeaveUnansweredWarnTicks;

        /// <summary>
        /// Report a <see cref="ListRooms"/> the server never answered.
        /// </summary>
        /// <remarks>
        /// The handler for a malformed 0x23 has named this gap since the response
        /// outcomes landed: a caller whose answer does not arrive waits on
        /// <c>OnRoomListReceived</c> for ever, and a room browser shows a spinner
        /// with nothing behind it.  <see cref="LastRoomListProblem"/> answers the
        /// question "what was wrong with the list I got"; this answers "there was
        /// no list", which no member could report because none was written.
        /// </remarks>
        private void ReportUnansweredRoomList(long nowTicks)
        {
            if (!_roomListPending) return;
            if (nowTicks < _roomListDeadlineTicks) return;

            _roomListPending     = false;
            _roomListRepliesOwed = 0;
            _roomListTimedOut    = true;

            var error =
                $"ListRooms drew no answer within {RequestReplyAckTtlSeconds}s. No room list " +
                "arrived and none is still expected for this request; a browser waiting on " +
                "OnRoomListReceived should stop waiting and offer a retry.";
            if (WarnGate.ShouldEmit(ref _lastRoomListUnansweredWarnTicks))
                Debug.LogWarning("[RTMPE] RoomManager: " + error);
            SafeRaise(OnRoomError, error);
        }

        private long _lastRoomListUnansweredWarnTicks;

        /// <summary>
        /// Watch a property write for the broadcast that would prove it landed.
        /// </summary>
        private void ArmPropertyWriteWatch(string subject, int expectedVersion)
        {
            if (!_pendingPropertyWrites.Arm(subject, expectedVersion, AckDeadline())
                && WarnGate.ShouldEmit(ref _lastPropertyWatchDroppedWarnTicks))
                Debug.LogWarning(WatchDroppedMessage("A property write"));
        }

        /// <summary>
        /// Watch a host-only command for the broadcast that would prove the
        /// server acted on it.
        /// </summary>
        private void ArmHostCommandWatch(HostCommandKind kind, string targetPlayerId)
        {
            if (!_pendingHostCommands.Arm(kind, targetPlayerId, AckDeadline())
                && WarnGate.ShouldEmit(ref _lastHostCommandWatchDroppedWarnTicks))
                Debug.LogWarning(WatchDroppedMessage("A host-only room command"));
        }

        /// <summary>
        /// What to say when a request goes out unwatched.
        /// </summary>
        /// <remarks>
        /// Reaching a watch's ceiling costs the diagnostic and never the request,
        /// which is the right trade — but a diagnostic that goes missing without
        /// saying so is the defect these watches exist to fix, one level up.
        ///
        /// <para>⛔ Console only, deliberately: the request itself is unaffected,
        /// and an application cannot act on "your diagnostic budget is full" the
        /// way it can act on "this request drew no answer".</para>
        ///
        /// <para>🚨 A gate each, where the two callers shared one — a full
        /// property table was deciding whether a dropped command watch was ever
        /// mentioned.</para>
        /// </remarks>
        private static string WatchDroppedMessage(string what) =>
            "[RTMPE] RoomManager: " + what + " was sent but is not being watched for the " +
            "broadcast that would answer it — this client has more requests outstanding than " +
            "the watch tracks. The request itself is unaffected; a failure of it will go " +
            "unreported.";

        private long _lastPropertyWatchDroppedWarnTicks;
        private long _lastHostCommandWatchDroppedWarnTicks;

        /// <summary>
        /// Say how many reports a room change took with it.
        /// </summary>
        /// <remarks>
        /// 🚨 The sweep is eager — every expired entry is removed from the table
        /// before the first report is raised — so a handler that leaves the room
        /// inside the loop does not defer the reports behind it, it ends them.
        /// Stopping is right: they describe a room this caller is no longer in,
        /// and the departure is the larger signal. Saying nothing was not: this
        /// batch exists because a diagnostic that goes missing in silence is the
        /// defect, and one that goes missing a hundred at a time is the same
        /// defect at scale.
        /// </remarks>
        private void ReportAbandonedByRoomChange(int abandoned)
        {
            if (abandoned <= 0) return;
            if (!WarnGate.ShouldEmit(ref _lastAbandonedReportWarnTicks)) return;
            Debug.LogWarning(
                $"[RTMPE] RoomManager: {abandoned} unanswered-request report(s) were dropped " +
                "because a handler left the room while they were being raised. They belonged to " +
                "the room this client has left; the departure itself is reported by OnRoomLeft.");
        }

        private long _lastAbandonedReportWarnTicks;

        /// <summary>
        /// The instant by which a request sent now is owed its BROADCAST.  One
        /// budget serves the property and host-command watches because both
        /// answer the same server behaviour: a refusal that publishes nothing.
        /// </summary>
        private long AckDeadline() => DeadlineIn(PendingServerAckTtlSeconds);

        /// <summary>
        /// The instant by which a request sent now is owed its REPLY — the two
        /// opcodes the gateway forwards as a NATS request rather than a publish,
        /// and which therefore sit under its own request timeout as well as the
        /// Room Service's.
        /// </summary>
        private long ReplyDeadline() => DeadlineIn(RequestReplyAckTtlSeconds);

        private long DeadlineIn(double seconds)
            => _nowTicks() + (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

        /// <summary>
        /// Report every property write whose deadline passed with no broadcast
        /// carrying its version.
        /// </summary>
        /// <remarks>
        /// The server accepts a write only at exactly the version it declares and
        /// answers a conflict with no packet at all, so a write that draws no
        /// broadcast is the only signal a caller can ever have that one was
        /// refused. Before this it had none: the write returned void, the local
        /// version did not move, and nothing said why.
        ///
        /// <para>⛔ The report says the version did not move, not that the write
        /// was rejected, and the distinction is deliberate. A broadcast carrying
        /// a LATER version resolves the entry — somebody else's write was
        /// accepted after ours, and whether ours landed first and was superseded
        /// or lost the race outright is not a question this side can answer.
        /// Reporting a failure for a write that did land would be worse than
        /// reporting an ambiguity.</para>
        ///
        /// <para>⚠️ The local version is left alone. An expiry records that no
        /// answer came; it does not discard state the next broadcast will
        /// correct, which is the mistake the lobby-timeout repair made and had
        /// to have undone.</para>
        /// </remarks>
        private void ReportUnansweredPropertyWrites(long nowTicks)
        {
            var expired = _pendingPropertyWrites.SweepExpired(nowTicks);
            string roomId = _currentRoom?.RoomId;
            for (int i = 0; i < expired.Count; i++)
            {
                // A handler raised below may leave the room, or be told it was
                // removed from it. `ForgetRoomScopedState` empties the table but
                // never the snapshot already taken from it, so what is left in
                // hand at that point belongs to a room this caller is no longer
                // a member of.
                if (_currentRoom?.RoomId != roomId)
                {
                    ReportAbandonedByRoomChange(expired.Count - i);
                    return;
                }

                string subject = expired[i];
                string scope = subject == PendingPropertyWrites.RoomSubject
                    ? "room"
                    : $"player '{UntrustedLogText.Sanitise(subject)}'";
                var error =
                    $"Property write to the {scope} drew no broadcast within " +
                    $"{PendingServerAckTtlSeconds}s. The server accepts a write only at the " +
                    "version it declares and answers a conflict with no packet, so the version it " +
                    "holds has almost certainly moved past the one this write named. Read the " +
                    "current value and write again.";
                if (WarnGate.ShouldEmit(ref _lastPropertyWriteUnansweredWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: " + error);
                SafeRaise(OnRoomError, error);
            }
        }

        private long _lastPropertyWriteUnansweredWarnTicks;

        /// <summary>
        /// Report every host-only command whose deadline passed with no
        /// broadcast naming its target.
        /// </summary>
        /// <remarks>
        /// The Room Service authorises <see cref="TransferMasterClient"/> and
        /// <see cref="KickPlayer"/> against the seat holding the master role —
        /// the same lock a room-property write goes through — and answers a
        /// refusal with no packet at all. A command that draws no broadcast is
        /// therefore the only signal a caller can ever have that one was dropped.
        /// Before this there was none: both methods return void, and both of
        /// their doc comments described the silence as though it were the
        /// contract.
        ///
        /// <para>⛔ The report names the silence, not a cause, because the arms
        /// that produce it disagree about what the caller should do. Two are
        /// decided by the TARGET rather than by the sender: the Room Service
        /// returns early, before any transaction, when a transfer names the
        /// player who already holds the role and when a kick names the master
        /// itself. A caller who IS the master reaches both — so a message
        /// asserting "you are no longer the master" would be the inverse of the
        /// truth there, and advice to re-read the master id would send the
        /// caller to a value already agreeing with it. The others are the
        /// authorisation refusal, the gateway's own refusal one hop earlier, a
        /// target that has already left, and a broadcast published and lost —
        /// it is best-effort, right after the transaction commits.</para>
        ///
        /// <para>⚠️ No local state is discarded. An expiry records that no answer
        /// came; the roster and the master id are left for the next broadcast to
        /// correct, which is the mistake the lobby-timeout repair made and had to
        /// have undone.</para>
        ///
        /// <para>⛔ A watch is ROOM-scoped, so a session that ends takes its
        /// unanswered requests with it and reports none of them: the heartbeat
        /// teardown clears both tables through
        /// <see cref="ForgetRoomScopedState"/>, and on a link declared dead it
        /// runs before this sweep in the same frame. That is deliberate — a
        /// dropped link is reported by the disconnect, which is a larger signal
        /// than a per-request timeout and does not need repeating once per
        /// outstanding write — but it does mean the window and the liveness
        /// threshold interact: at the shipped five-second heartbeat the teardown
        /// lands at thirty seconds and every report wins, while a heartbeat
        /// interval below two seconds puts it inside this window and the
        /// disconnect becomes the only signal.</para>
        /// </remarks>
        private void ReportUnansweredHostCommands(long nowTicks)
        {
            var expired = _pendingHostCommands.SweepExpired(nowTicks);
            string roomId = _currentRoom?.RoomId;
            for (int i = 0; i < expired.Count; i++)
            {
                // For the reason the property report states: the snapshot
                // outlives the table a handler's departure clears.
                if (_currentRoom?.RoomId != roomId)
                {
                    ReportAbandonedByRoomChange(expired.Count - i);
                    return;
                }

                HostCommand command = expired[i];
                string action = command.Kind == HostCommandKind.Kick
                    ? "Kicking"
                    : "Handing the master-client role to";
                var error =
                    $"{action} player '{UntrustedLogText.Sanitise(command.TargetPlayerId)}' drew no " +
                    $"broadcast within {PendingServerAckTtlSeconds}s. A room command the server " +
                    "declines is answered with no packet at all, so this reports the silence and not " +
                    "a cause: the command may have been refused because this client no longer holds " +
                    "the master-client role, because the target has already left the room, or " +
                    "because of what the command NAMES — a transfer to the player who already holds " +
                    "the role is refused as redundant, and a kick naming the master is refused " +
                    "outright.";
                if (WarnGate.ShouldEmit(ref _lastHostCommandUnansweredWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: " + error);
                SafeRaise(OnRoomError, error);
            }
        }

        private long _lastHostCommandUnansweredWarnTicks;

        // ⚠️ Cached, not written inline at the call site.  A lambda that closes
        // over `this` is allocated afresh on every evaluation — Roslyn caches
        // only closure-free ones — so writing these two inline cost a delegate
        // pair on every frame of the session's life, pending join or not,
        // whether or not anything needed retransmitting.  Measured at 128 bytes
        // a frame, which is roughly 7.7 KB/s of main-thread garbage at 60 fps.
        //
        // 🔑 Found by measuring `Tick` itself.  The test that was meant to hold
        // this measured `PendingCreateTable.SweepExpired` instead — a component
        // of the frame's cost rather than the frame — and so reported zero for a
        // path that was allocating on every call.  `Start()` two files over
        // caches `_sendPacketCallback` for exactly this reason.
        private Action<byte[]> _retransmitJoin;
        private Action<string> _reportJoinExhausted;

        // Self-guards the transport call: a fault here must neither escape into
        // the per-frame update loop nor stall the ladder, which has already
        // advanced its schedule before this runs.
        private void RetransmitJoin(byte[] packet)
        {
            try { _sendOwned(SingleCopyOf(packet)); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] RoomManager: JoinRoom retransmit send failed — {ex.Message}");
            }
        }

        /// <summary>
        /// A copy of <paramref name="packet"/> that asks for one transmission
        /// rather than for a retransmit ladder of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔑 There are two ladders on a join and only one request.  The
        /// transport's ladder recovers a request whose datagram was lost — it is
        /// cleared by the acknowledgement the gateway emits on receipt, before
        /// the request is even routed.  This class's ladder recovers something
        /// the transport cannot see: a request that arrived and whose *reply*
        /// never came back.  Nested, they multiply — each rung of the outer
        /// ladder opened a fresh inner one, so a join whose acknowledgements
        /// were being lost put sixty-three copies on the wire, and the server
        /// answered every delivered one by replaying the room's whole catch-up
        /// stream to this player.
        /// </para>
        /// <para>
        /// ⚠️ The flag is cleared on a copy, never on the parked bytes: the
        /// first transmission is the one that carries the transport's guarantee,
        /// and the ladder re-reads what it parked on every rung.
        /// </para>
        /// </remarks>
        private static byte[] SingleCopyOf(byte[] packet)
        {
            if (packet == null || packet.Length <= PacketBuilder.FlagsOffset) return packet;

            var copy = new byte[packet.Length];
            Buffer.BlockCopy(packet, 0, copy, 0, packet.Length);
            copy[PacketBuilder.FlagsOffset] &=
                unchecked((byte)~(byte)PacketFlags.Reliable);
            return copy;
        }

        /// <summary>
        /// Report a JoinRoom whose retransmit ladder is spent.
        /// </summary>
        /// <remarks>
        /// 🔑 The sentence states the SILENCE, not a cause, and says the request
        /// is still outstanding — because it is, and because a late reply is
        /// still accepted.  <c>ReportUnansweredLeave</c> settled the same
        /// question in the same words one screen down, and for the same reason:
        /// refusing a late answer would leave this client outside a room the
        /// server has already seated it in, with the seat held until the reaper
        /// and nothing to show the player.
        ///
        /// <para>⛔ "Join request timed out — no response from server." is what
        /// this said until 2026-09-11, and it is read as *the join did not
        /// happen*.  An application acts on that — it shows a failure, it asks
        /// again, it goes back to a menu — and then <see cref="OnRoomJoined"/>
        /// arrives for the request it was told had failed (`ROOM-RD-11`).</para>
        ///
        /// <para>The debt is recorded here and spent by the reply that
        /// contradicts it, so the contradiction is visible where a developer
        /// will see it rather than only in the register.</para>
        /// </remarks>
        private void ReportJoinExhausted(string label)
        {
            _joinReportedExhausted = true;
            Debug.LogWarning(
                $"[RTMPE] RoomManager: JoinRoom '{label}' drew no server reply within its " +
                "retransmit budget.  The request is still outstanding and a late reply is " +
                "still accepted.");
            SafeRaise(OnRoomError,
                "JoinRoom drew no answer this client could act on within its retransmit " +
                "budget. The request is still outstanding and a late reply is still " +
                "accepted — this reports the silence and not a cause. The room has NOT been " +
                "joined as far as this client can tell, and OnRoomJoined still arrives if " +
                "the server answers late.");
        }

        // ── Inbound property broadcasts ────────────────────────────────────────
        //
        // These internal entry points are called by NetworkManager's broadcast
        // receiver (or, in tests, directly) when a `room_properties_updated`
        // or `player_properties_updated` event arrives on the wire.  The
        // broadcast receiver is responsible for decoding the NATS
        // RoomEvent → JSON payload → PropertyJson.Decode* — this class
        // consumes the already-parsed typed payload and swaps the current
        // snapshot atomically before firing the public event.

        /// <summary>
        /// Apply a <c>room_properties_updated</c> broadcast to the local
        /// <see cref="CurrentRoom"/> snapshot.  No-op when not in a room or
        /// when the broadcast version is ≤ the local version (stale).
        /// Public so it can be exercised by unit tests; wire routing happens
        /// via the broadcast receiver in NetworkManager.
        /// </summary>
        /// <remarks>
        /// The broadcast carries the writer's DELTA, so it is merged onto the
        /// current map rather than replacing it — see
        /// <see cref="RoomInfo.MergeProperties"/> for why the two differ and
        /// what the server does with the same request.
        /// </remarks>
        public void ApplyRoomPropertiesBroadcast(
            int version,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (_currentRoom == null) return;

            // Ahead of the monotonic guard: this is the answer a pending write
            // was waiting for, and a broadcast the guard drops as stale still
            // carries a version, which is the only thing the watch compares.
            _pendingPropertyWrites.Resolve(PendingPropertyWrites.RoomSubject, version);

            if (version <= _currentRoom.PropertiesVersion) return; // monotonic guard

            _currentRoom = _currentRoom.WithProperties(
                RoomInfo.MergeProperties(_currentRoom.Properties, properties), version);
            WarnIfPropertyMapHasOutgrownTheServers(
                _currentRoom.Properties.Count, PropertyLimits.MaxPropertiesPerRoom, "room");

            // Both events describe one broadcast, so both are handed the same
            // snapshot rather than whatever the field holds by the time the
            // second is reached: a subscriber to the first is free to leave the
            // room or enter another from inside its handler.
            var applied = _currentRoom;
            SafeRaise(OnRoomPropertiesChanged, applied);

            // The scene signal is derived from the general one and follows it,
            // so a subscriber watching the property map has already run by the
            // time the room is told to load.  The test is on the delta and the
            // snapshot is the argument: what the broadcast wrote is a question
            // only the delta answers, and what the room now holds is a question
            // only the merged map answers.
            //
            // ⛔ And the room is asked for again, because the handlers above have
            // run since it was captured.  A subscriber is entitled to leave, to
            // enter another room, to take a further broadcast, or to end the
            // session from inside one — each of those is synchronous — so an
            // instruction issued afterwards can name a room the client is no
            // longer in, or a scene it has already moved past.  Worse than
            // useless in the teardown case: the receiver arms a fresh wait, and
            // the retirement that would have cancelled it has already run.
            //
            // The room AND the version, which is one question in two fields:
            // is the state this broadcast produced still the room's?  Both are
            // needed and neither is enough — the id alone admits a write another
            // has superseded, and the version alone admits a different room that
            // happens to be at the same number.  ⚠️ Identity would be wrong for
            // both: a roster notification handled from inside the property event
            // replaces the snapshot with an equal one, and refusing there would
            // drop a perfectly good instruction because somebody joined.
            if (properties != null
                && properties.ContainsKey(ReservedPropertyKeys.Scene)
                && _currentRoom != null
                && _currentRoom.RoomId == applied.RoomId
                && _currentRoom.PropertiesVersion == applied.PropertiesVersion)
            {
                SafeRaise(OnRoomSceneWritten, applied);
            }
        }

        // The server caps its MERGED map, so a local map larger than that cap is
        // proof this client and the server no longer hold the same properties —
        // the only observable the divergence has.  Deletions are decoded and
        // applied now (RoomInfo.MergeProperties), so the routine way in is a
        // lost broadcast rather than an inexpressible one: the property packet
        // is sent reliable, but a client that entered the room without a
        // property snapshot, or with a truncated one, merges later deltas onto
        // a map that was never the server's.
        //
        // Reported once a second rather than per broadcast: the condition
        // persists for the life of the room once it is reached.
        private long _lastPropertyOverflowWarnTicks;

        // What the room-entry reply said about the property snapshot it carried.
        // Its own gate, not the overflow one: the two report different things,
        // and sharing a gate would let a room in permanent overflow silence
        // every joiner's snapshot report for the life of the session.
        //
        // Only the two abnormal outcomes are reported.  `Absent` is silent on
        // purpose: it is what every server sent before the block existed, and a
        // per-join warning for a healthy older deployment is noise that trains
        // integrators to ignore the channel the other two arrive on.
        // 🚨 One each. They report different faults — a version that arrived
        // without its map, and a map that could not be read at all — and one
        // budget between them let the first decide whether the second was ever
        // written.
        private long _lastPropertySnapshotWarnTicks;
        private long _lastPropertySnapshotIncompleteWarnTicks;

        private void ReportRoomPropertySnapshot(JoinPropertiesStatus status)
        {
            if (status == JoinPropertiesStatus.Complete) return;
            if (status == JoinPropertiesStatus.Absent) return;

            if (status == JoinPropertiesStatus.Incomplete)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastPropertySnapshotIncompleteWarnTicks))
                    Debug.LogWarning(
                    "[RTMPE] RoomManager: the room entry carried the property VERSION but not " +
                    "the map — the server could not send the whole snapshot (it exceeded the " +
                    "reply's byte budget, or one of its values could not be rendered). " +
                    "Property writes from this client are accepted; reads are missing keys " +
                    "until a writer re-sends them.");
                return;
            }

            if (!RTMPE.Core.WarnGate.ShouldEmit(ref _lastPropertySnapshotWarnTicks)) return;
            Debug.LogWarning(
                "[RTMPE] RoomManager: the room entry carried a property snapshot that could not " +
                "be read, so none of it was adopted. This client starts at version 0: its " +
                "property writes will be refused, without a reply, while the room is at any " +
                "other version. Re-joining rebuilds the snapshot.");
        }

        private void WarnIfPropertyMapHasOutgrownTheServers(int held, int serverCap, string scope)
        {
            if (held <= serverCap) return;
            if (!RTMPE.Core.WarnGate.ShouldEmit(ref _lastPropertyOverflowWarnTicks)) return;

            Debug.LogWarning(
                $"[RTMPE] This client holds {held} {scope} properties and the server caps a " +
                $"{scope} at {serverCap}, so the two no longer agree — this client merged " +
                "deltas onto a map that was never the server's, most likely because it " +
                "entered the room without a property snapshot or with a truncated one. " +
                "Re-joining the room rebuilds the map.");
        }

        /// <summary>
        /// Apply a <c>player_properties_updated</c> broadcast to the matching
        /// player in the local roster.  No-op when not in a room, the player
        /// is unknown, or the broadcast is stale.
        /// </summary>
        public void ApplyPlayerPropertiesBroadcast(
            string playerId,
            int version,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (_currentRoom == null || string.IsNullOrEmpty(playerId)) return;

            // Ahead of the roster walk, for the reason the room path states.
            _pendingPropertyWrites.Resolve(playerId, version);

            // Swap the matching player's snapshot in-place on a copied roster.
            var roster = _currentRoom.Players;
            if (roster == null || roster.Length == 0) return;

            PlayerInfo updated = null;
            var newRoster = new PlayerInfo[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == playerId)
                {
                    if (version <= roster[i].PropertiesVersion) return; // stale
                    // A player broadcast is a delta on the same terms as the
                    // room's; merging is not optional on one and not the other.
                    updated    = roster[i].WithProperties(
                        RoomInfo.MergeProperties(roster[i].Properties, properties), version);
                    newRoster[i] = updated;
                }
                else
                {
                    newRoster[i] = roster[i];
                }
            }
            if (updated == null) return; // playerId not on roster

            _currentRoom = _currentRoom.WithPlayers(newRoster);
            WarnIfPropertyMapHasOutgrownTheServers(
                updated.Properties.Count, PropertyLimits.MaxPropertiesPerPlayer, "player");
            SafeRaise(OnPlayerPropertiesChanged, playerId, updated);
        }

        // ── Response handlers ──────────────────────────────────────────────────

        private void HandleCreateResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseCreateRoomResponse(
                    payload, out bool ok, out string roomId,
                    out string roomCode, out int maxPlayers,
                    out string localPlayerId, out Guid? echoedRequestId, out string error))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedRoomCreateWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed RoomCreate response.");
                return;
            }

            // Match by the echoed request_id when the gateway includes it; FIFO
            // fallback for a gateway that omits it.  The match is made once, for
            // both outcomes, because either one answers exactly one request and
            // must consume exactly one entry.
            //
            // 🔑 A reply that matches nothing is not applied.  It answers a
            // request this client no longer holds, which happens two ways and
            // both make applying it wrong: the reply is a second copy of one
            // already acted on — the gateway serves a retransmitted create from
            // its replay table, so the same answer arrives twice — or the
            // request outlived its deadline and the sweep has already told the
            // application it timed out.  Applied, the first re-raises
            // OnRoomCreated over a room the membership stream has since
            // advanced, rebuilds its roster from the creator alone, and issues a
            // second auto-join; the second contradicts a failure the caller was
            // already given.
            if (!_pendingCreates.TryMatch(echoedRequestId, out var matched))
            {
                if (WarnGate.ShouldEmit(ref _lastUnmatchedCreateResponseWarnTicks))
                {
                    Debug.LogWarning(
                        "[RTMPE] RoomManager: a CreateRoom reply arrived for a request " +
                        "that is no longer outstanding and was ignored.");
                }
                return;
            }

            if (ok)
            {
                // The room metadata in the response is authoritative; only the
                // client-supplied IsPublic/Name view comes from the request.
                CreateRoomOptions opts = matched;

                // The creator is the room's sole initial occupant and its host,
                // so it is seated on the roster the snapshot is built with:
                // MasterId / IsMasterClient then resolve to the local player, and
                // the first incremental PlayerJoined extends a host-bearing roster
                // instead of re-deriving the count from an empty one.  A pre-v3.1
                // gateway that omits the local player id leaves the roster unseeded,
                // matching the legacy snapshot shape.
                PlayerInfo[] roster =
                    string.IsNullOrEmpty(localPlayerId)
                        ? null
                        : new[] { new PlayerInfo(localPlayerId, string.Empty, isHost: true, isReady: false) };

                // ⛔ No property map, and that is a premise elsewhere rather
                // than an omission here: a create reply carries none, so this
                // snapshot cannot hold a scene, and the two doors that CAN —
                // the join reply and the matchmaking entry — both announce it
                // through OnRoomJoined.  A create reply that ever gains a
                // property block gains a scene nobody is told about, because a
                // later property broadcast announces only what its own delta
                // names.
                var created = new RoomInfo(
                    roomId, roomCode, opts.Name ?? string.Empty, "waiting",
                    roster?.Length ?? 1, maxPlayers, opts.IsPublic, roster);

                // Creating a room while already in one is a room switch, and it
                // owes the displaced room the same departure an implicit join
                // does.  The auto-join below does not rescue it: by the time its
                // reply arrives _currentRoom already names the new room, so
                // ApplyRoomEntry takes its idempotent same-room branch and the
                // teardown never runs at all.  The snapshot is built above so
                // the departure can complete — spawn registry cleared, OnRoomLeft
                // raised — before the assignment makes the new room visible.
                DepartDisplacedRoom(created);

                LastLeftRoom = null;
                _currentRoom = created;

                // Populate LocalPlayerStringId so IsOwner checks work.
                if (!string.IsNullOrEmpty(localPlayerId))
                {
                    _localPlayerId = localPlayerId;
                    _onLocalPlayerIdResolved?.Invoke(localPlayerId);
                }

                SafeRaise(OnRoomCreated, _currentRoom);

                // Creating a room should leave the caller in it as host — the model
                // every mainstream engine exposes — but the server seats a player
                // only on JoinRoom, so a bare create yields an empty "waiting" room
                // whose creator is never recorded.  Issue that JoinRoom now (opt out
                // via CreateRoomOptions.AutoJoinAsHost for the deliberate two-step
                // flow).  Joining the just-created room is the idempotent same-room
                // branch of HandleJoinResponse: it swaps the client-seeded snapshot
                // for the server's authoritative one and raises OnRoomJoined, which
                // is where gameplay belongs — not the bare-create signal.
                if (opts.AutoJoinAsHost && !string.IsNullOrEmpty(roomId))
                    JoinRoom(roomId);
            }
            else
            {
                // The entry this failure answers was consumed above: a failure
                // still answers exactly one in-flight request, and leaving its
                // entry behind would slide every later create's option lookup
                // onto the wrong request.  The recovered options are irrelevant
                // to an error.
                if (WarnGate.ShouldEmit(ref _lastRoomOpFailureWarnTicks))
                {
                    Debug.LogWarning(
                        "[RTMPE] RoomManager: CreateRoom failed — " +
                        UntrustedLogText.Sanitise(error));
                }
                SafeRaise(OnRoomError, error ?? "Unknown error");
            }
        }

        private void HandleJoinPacket(byte[] payload)
        {
            if (!RoomPacketParser.TryGetJoinMsgKind(payload, out byte msgKind))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedRoomJoinWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed RoomJoin payload (no msg_kind).");
                return;
            }

            if (msgKind == RoomMsgKind.Response)
                HandleJoinResponse(payload);
            else
                HandlePlayerJoinedNotification(payload);
        }

        private void HandleJoinResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseJoinRoomResponse(
                    payload, out bool ok, out RoomInfo room,
                    out string localPlayerId,
                    out JoinPropertiesStatus propertiesStatus, out string error))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedJoinResponseWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed JoinRoom response.");
                return;
            }

            if (ok) ReportRoomPropertySnapshot(propertiesStatus);

            // This reply answers the outstanding request, so stop retransmitting
            // it.  A retransmit already in flight when the first reply lands will
            // still draw a second reply; wasPending distinguishes that duplicate
            // (retransmit budget already released) from the first, load-bearing
            // one so an error is not surfaced twice.  The success path leans on
            // ApplyRoomEntry's same-room dedup instead, which also covers a
            // duplicate that races the create→auto-join handoff.
            bool wasPending = _pendingJoin.IsArmed;

            if (ok && JoinReplyContradictsThisClient(room))
            {
                // ⛔ Not ours: a reply for a room this client did not ask for, or
                // asked for and has since left.  Neither disarm nor apply — the
                // ladder belongs to a request this reply does not answer, and
                // disarming it is how a request came to be neither answered nor
                // able to time out.
                if (WarnGate.ShouldEmit(ref _lastUnsolicitedJoinReplyWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] RoomManager: a JoinRoom reply for room " +
                        $"'{UntrustedLogText.Sanitise(room?.RoomId)}' answers no request this " +
                        "client is waiting on, and was not applied.");
                return;
            }

            _pendingJoin.Disarm();

            if (ok)
            {
                // 🔑 Spent BEFORE the raise, on the same terms as the lobby's
                // abandoned join and the leave's debt: `ApplyRoomEntry` raises
                // `OnRoomJoined` synchronously, and a subscriber that calls back
                // into this manager must not find the report still owed.
                if (_joinReportedExhausted)
                {
                    _joinReportedExhausted = false;
                    // ⛔ Gated as well as debt-bounded, and the gate is the
                    // weaker of the two: the debt already admits one line per
                    // reported timeout, and a timeout costs a whole retransmit
                    // ladder.  It is here because this is an inbound path and
                    // the rule over them is a CAPABILITY — arguing an exemption
                    // for a site that costs nothing to gate is how the ungated
                    // ones came to exist.  🔑 Which also means the gate must not
                    // become what the debt's own tests measure: they assert the
                    // FIELD, not the second line's absence.
                    if (WarnGate.ShouldEmit(ref _lastJoinSupersededWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] RoomManager: a JoinRoom reply for room " +
                        $"'{UntrustedLogText.Sanitise(room?.RoomId)}' supersedes the timeout " +
                        "this client was already given for it.  The join HAS happened; the " +
                        "earlier OnRoomError reported silence, not failure.");
                }
                _joinIntentLabel = null;
                ApplyRoomEntry(room, localPlayerId);
            }
            else if (wasPending || _joinReportedExhausted)
            {
                // 🔑 `_joinReportedExhausted` is the second half, and without it a
                // late REFUSAL was silent: `PendingJoinRetry.Tick` disarms before
                // it calls `onExhausted`, so `wasPending` is already false when
                // the answer lands, and the arm below was guarded by it alone.
                // The timeout this client was given says in as many words that
                // the request is still outstanding and a late reply is still
                // accepted — so an answer that never arrives at the application
                // makes that sentence false for half the outcomes.
                bool late = !wasPending;
                _joinReportedExhausted = false;
                _joinIntentLabel = null;

                if (WarnGate.ShouldEmit(ref _lastJoinFailedWarnTicks))
                {
                    Debug.LogWarning(
                        (late
                            ? "[RTMPE] RoomManager: a late JoinRoom answer supersedes the timeout this "
                              + "client was already given, and it is a refusal — "
                            : "[RTMPE] RoomManager: JoinRoom failed — ") +
                        UntrustedLogText.Sanitise(error));
                }
                SafeRaise(OnRoomError, error ?? "Unknown error");
            }
        }

        /// <summary>
        /// Whether this reply answers a join this client is still entitled to.
        /// </summary>
        /// <remarks>
        /// 🔑 Matched on the label the ladder was ARMED with, which is a room id
        /// through one door and a room code through the other — so the reply is
        /// checked against whichever of its two identifiers the caller actually
        /// named.  ⛔ A reply carrying no room answers nothing; the refusal arm
        /// above is reached by its own two conditions instead, because a refusal
        /// names no room to match.
        /// </remarks>
        private bool JoinReplyContradictsThisClient(RoomInfo room)
        {
            if (room == null) return false;

            // An outstanding request names the room it is for: a reply for any
            // other room does not answer it, whatever else it is.
            if (!string.IsNullOrEmpty(_joinIntentLabel))
            {
                return !string.Equals(_joinIntentLabel, room.RoomId, StringComparison.Ordinal)
                    && !string.Equals(_joinIntentLabel, room.RoomCode, StringComparison.Ordinal);
            }

            // ⛔ And with no request outstanding, the one reply that must still be
            // refused is the one for the room this client has just LEFT.  The
            // seat-leak argument that makes a late reply acceptable is spent
            // there — the leave released the seat — so applying it puts the
            // player back somewhere they have no seat, with the menu gone.
            // 🔑 Deliberately NOT "no request outstanding ⇒ refuse": a room entry
            // that arrives without this client having armed one is how the
            // create→auto-join handoff and every server-seeded entry reach the
            // application, and refusing those would be a far larger change than
            // the defect warrants.
            return !string.IsNullOrEmpty(_departedRoomId)
                && string.Equals(_departedRoomId, room.RoomId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Drop every piece of state scoped to the room this client is leaving.
        /// </summary>
        /// <remarks>
        /// 🔑 One body, and every exit from a room goes through it — the explicit
        /// leave, being kicked, the implicit switch, and the session teardown.
        /// 🚨 It exists because it did not: the pending-property-write watch was
        /// cleared on two of those four paths, so a `LeaveRoom` left an armed
        /// entry behind and, once its window elapsed, reported a write against a
        /// room this client was no longer in — and a `LeaveRoom` followed by a
        /// join carried it into the NEXT room, which is precisely what the
        /// switch path's own comment says must not happen.
        ///
        /// <para>⛔ The rule is held as a capability rather than as four call
        /// sites: nothing but this method assigns <c>_currentRoom = null</c>, so
        /// a fifth exit cannot be written without inheriting all of it. The
        /// caller records <see cref="LastLeftRoom"/> beforehand where its
        /// listeners need it, and raises <see cref="OnRoomLeft"/> afterwards;
        /// this method neither reads the room it is discarding nor announces
        /// anything, so it is safe to call from a teardown that must raise
        /// nothing.</para>
        /// </remarks>
        private void ForgetRoomScopedState()
        {
            // Read before the nulling below, and kept across it — see the field.
            _departedRoomId            = _currentRoom?.RoomId ?? _departedRoomId;
            _currentRoom               = null;
            _localPlayerId             = string.Empty;
            _lastJoinedAnnouncedRoomId = null;
            _pendingPropertyWrites.Clear();
            _pendingHostCommands.Clear();
            _pendingJoin.Disarm();
            _joinReportedExhausted = false;
            // Revoked here, not on the ladder's expiry — see the field.
            _joinIntentLabel = null;
            // Any other way out of a room abandons an outstanding leave for the
            // reason it abandons an outstanding join: the reply names a room
            // this client has already left by another door.
            _pendingLeaveRoomId  = null;
            _pendingLeaveRefused = false;
            _pendingLeaveTimedOut = false;
        }

        /// <summary>
        /// Perform the departure a room switch owes before <paramref name="incoming"/>
        /// takes the current room's place.  A no-op unless a different room is
        /// actually being displaced.
        /// </summary>
        /// <remarks>
        /// Arriving at a room while still holding another — with no intervening
        /// <see cref="LeaveRoom"/> — must mirror the gateway's "leave A then join
        /// B" ordering on this side.  <see cref="OnRoomLeft"/> is a synchronous
        /// <see cref="System.Action"/>: the <c>NetworkManager</c> listener
        /// transitions the state machine back to <c>Connected</c> and runs
        /// <c>SpawnManager.ClearAll</c> on this stack frame, so the displaced
        /// room's GameObjects are destroyed before the caller makes the new room
        /// visible.  Skip it and <see cref="OnRoomJoined"/> fires twice in a row,
        /// room A's spawned objects persist into room B, and ownership
        /// comparisons straddle two rosters.
        ///
        /// <para>The prior room is nulled before the raise so a listener that
        /// consults <see cref="CurrentRoom"/> sees a coherent between-rooms
        /// snapshot; user <c>OnNetworkDespawn</c> callbacks would otherwise
        /// observe a Connected state alongside room A, which no steady state
        /// produces.</para>
        ///
        /// <para>The seat is surrendered with the room.  Every other exit —
        /// <see cref="LeaveRoom"/>, the kick path, <see cref="ClearState"/> —
        /// clears the local player id alongside the room, and a switch owes the
        /// same: that id names a seat on room A's roster, and carrying it into
        /// room B is the second half of the ownership straddle above.  An entry
        /// reply that carries a new seat restores it immediately; one that does
        /// not leaves it empty, which reads as unknown rather than as somebody
        /// else.</para>
        ///
        /// <para>A join still being retransmitted is abandoned for the reason
        /// <see cref="LeaveRoom"/> abandons it: its reply would otherwise
        /// re-enter a room this client has just left.</para>
        ///
        /// <para>Idempotent on rejoin: an entry naming the room already held is
        /// a duplicate arrival — a reliable-channel retransmit echo, or the
        /// auto-join that follows a create — and displaces nothing.</para>
        /// </remarks>
        private void DepartDisplacedRoom(RoomInfo incoming)
        {
            if (_currentRoom == null
                || incoming == null
                || _currentRoom.RoomId == incoming.RoomId)
            {
                return;
            }

            LastLeftRoom = _currentRoom;
            ForgetRoomScopedState();
            SafeRaise(OnRoomLeft);
        }

        /// <summary>
        /// Adopt <paramref name="room"/> as the current room and announce the
        /// arrival via <see cref="OnRoomJoined"/>.  Shared by the JoinRoom
        /// response path and the matchmaking adoption path
        /// (<see cref="EnterMatchmadeRoom"/>), both of which arrive at an
        /// occupied room and must drive the client state machine identically.
        /// </summary>
        private void ApplyRoomEntry(RoomInfo room, string localPlayerId)
        {
            // Ignore a duplicate reply for the room we have already announced.
            // Retransmitting a join means its reply can arrive more than once
            // (the gateway recovers a re-sent join idempotently), and a stale
            // duplicate must neither re-raise OnRoomJoined nor overwrite a roster
            // the membership stream has since advanced.  The guard is anchored on
            // _lastJoinedAnnouncedRoomId rather than _currentRoom alone so the
            // create→auto-join handoff — which seeds _currentRoom with the new
            // room before its arrival is announced — still fires OnRoomJoined the
            // first time.
            if (room != null
                && _currentRoom != null
                && _currentRoom.RoomId == room.RoomId
                && _lastJoinedAnnouncedRoomId == room.RoomId)
            {
                return;
            }

            DepartDisplacedRoom(room);

            LastLeftRoom = null;
            _currentRoom = room;

            // Populate LocalPlayerStringId so IsOwner checks work.
            if (!string.IsNullOrEmpty(localPlayerId))
            {
                _localPlayerId = localPlayerId;
                _onLocalPlayerIdResolved?.Invoke(localPlayerId);
            }

            _lastJoinedAnnouncedRoomId = room?.RoomId;
            SafeRaise(OnRoomJoined, room);
        }

        /// <summary>
        /// Adopt the room a matchmaking reply assigned this client to.
        /// Matchmaking finds-or-creates a room AND seats the player in one
        /// server-side transaction, so the client adopts the assignment
        /// locally rather than issuing a second JoinRoom — which would collide
        /// on the seat the server already holds.  When the reply carries the
        /// room's roster (parity with the JoinRoom reply), the snapshot is built
        /// from it so a client matchmade into an occupied room sees the full
        /// membership at once; the pre-existing occupants' player_joined events
        /// fired before this client was bound and never arrive.  A reply without
        /// a roster (pre-roster Room Service) falls back to the minimal self-seat
        /// the CreateRoom path uses, and the membership stream back-fills the rest.
        /// </summary>
        /// <param name="created"><see langword="true"/> when the server created
        /// the room for this client (making it the host); <see langword="false"/>
        /// when it joined an existing room owned by someone else.  Used only for
        /// the self-seat fallback — when a roster is supplied its
        /// <c>is_host</c> flags are authoritative.</param>
        /// <param name="roster">the matchmade room's occupants at seat time, or
        /// <see langword="null"/> to seat only the local player.</param>
        /// <param name="propertiesPayload">the room's property snapshot as the
        /// server authored it, or <see langword="null"/> when the reply carried
        /// none.  Matchmaking is the second door into an already-configured
        /// room: a client entering without the property VERSION has every
        /// property write it makes refused, with no reply, for as long as the
        /// room lives.</param>
        /// <param name="propertiesComplete">whether
        /// <paramref name="propertiesPayload"/> carries the whole map.</param>
        internal void EnterMatchmadeRoom(
            string roomId, string roomCode, bool created, string localPlayerId, int maxPlayers,
            MatchmadePlayer[] roster = null,
            string propertiesPayload = null, bool propertiesComplete = false)
        {
            if (string.IsNullOrEmpty(roomId)) return;

            // A duplicate matchmaking reply (gateway retransmit) for the room
            // already occupied must not re-announce arrival or reset spawn
            // state — the once-only latch lives in MatchmakingManager, but this
            // guard keeps adoption idempotent independently of it.
            if (_currentRoom != null && _currentRoom.RoomId == roomId) return;

            // Prefer the authoritative roster the reply now carries; it already
            // includes the host with the correct is_host flag, so MasterId /
            // IsMasterClient resolve from it directly.  Fall back to seating only
            // the local player when the reply omits a roster.
            PlayerInfo[] seats = PromoteRoster(roster);
            if (seats == null && !string.IsNullOrEmpty(localPlayerId))
                seats = new[] { new PlayerInfo(localPlayerId, string.Empty, isHost: created, isReady: false) };

            // Decode through the same helper the RoomJoin block uses, so the two
            // doors into a room cannot disagree about what a snapshot means.
            var propertiesStatus = JoinPropertiesStatus.Absent;
            System.Collections.Generic.Dictionary<string, PropertyValue> properties = null;
            int propertiesVersion = 0;
            if (!string.IsNullOrEmpty(propertiesPayload))
            {
                propertiesStatus = RoomPacketParser.TryDecodeRoomEntryProperties(
                    propertiesPayload, propertiesComplete, out properties, out propertiesVersion);
                if (propertiesStatus == JoinPropertiesStatus.Malformed)
                {
                    properties        = null;
                    propertiesVersion = 0;
                }
            }

            var room = new RoomInfo(
                roomId, roomCode, string.Empty, "waiting",
                seats?.Length ?? 1, maxPlayers, isPublic: true, seats,
                properties, propertiesVersion);

            ReportRoomPropertySnapshot(propertiesStatus);
            ApplyRoomEntry(room, localPlayerId);
        }

        /// <summary>
        /// Promote a matchmaking reply's lightweight roster entries to full
        /// <see cref="PlayerInfo"/> snapshots.  The matchmaking reply carries no
        /// custom properties or version counters, so each seat is created with the
        /// empty-property default — identical to a freshly joined player before
        /// any property update.  Returns <see langword="null"/> for an absent or
        /// empty roster so the caller applies its self-seat fallback.
        /// </summary>
        private static PlayerInfo[] PromoteRoster(MatchmadePlayer[] roster)
        {
            if (roster == null || roster.Length == 0) return null;

            var seats = new PlayerInfo[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                var p = roster[i];
                seats[i] = new PlayerInfo(p.PlayerId, p.DisplayName, p.IsHost, p.IsReady);
            }
            return seats;
        }

        private void HandlePlayerJoinedNotification(byte[] payload)
        {
            if (!RoomPacketParser.ParsePlayerJoinedNotification(payload, out PlayerInfo player))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedPlayerJoinedWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed PlayerJoined notification.");
                return;
            }

            // Seat the new player on the local roster BEFORE notifying, so a
            // listener that consults CurrentRoom from inside OnPlayerJoined sees
            // the post-join membership.  This mirrors the leave/kick paths,
            // which prune before notifying, and keeps the roster — the SDK's
            // authoritative source for MasterId / IsMasterClient, host-migration
            // ownership reassignment, and player-property delivery — in step
            // with the server's incremental membership stream.  AddPlayerToRoom
            // is idempotent, so a duplicate notification (e.g. a reliable-channel
            // retransmit echo) never seats the same player twice.
            if (_currentRoom != null)
            {
                _currentRoom = AddPlayerToRoom(_currentRoom, player);
            }

            SafeRaise(OnPlayerJoined, player);
        }

        private void HandleLeavePacket(byte[] payload)
        {
            if (!RoomPacketParser.TryGetLeaveMsgKind(payload, out byte msgKind))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedRoomLeaveWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed RoomLeave payload (no msg_kind).");
                return;
            }

            if (msgKind == RoomMsgKind.Response)
                HandleLeaveResponse(payload);
            else
                HandlePlayerLeftNotification(payload);
        }

        private void HandleLeaveResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseLeaveRoomResponse(
                    payload, out bool ok, out string answeredRoomId))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedLeaveResponseWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed LeaveRoom response.");
                return;
            }

            // Answer the request that is outstanding, or none.  The reply
            // carries an ok flag and no room, so it can only be read against the
            // room the request named; a copy that arrives after that request has
            // been settled describes a departure that has already happened and
            // must not be replayed onto the room now held.
            //
            // 🔑 Two questions, and the second is not the first restated.  The
            // latch says a leave is outstanding — every path that leaves a room
            // by another door runs through ForgetRoomScopedState, which retires
            // one in the same body, so an outstanding leave is a leave for the
            // room now held.  The room the reply NAMES says which leave it
            // answers, and no client state can supply that: a client that left
            // room A and has since asked to leave room B holds two requests
            // whose replies are byte-identical but for this field, and the first
            // reply arriving late would otherwise settle the second request and
            // tear down a room nobody asked to leave.
            //
            // ⛔ An empty name is not a mismatch.  A server that predates the
            // field returns none, and so does a session the gateway found
            // seatless; both mean *the reply cannot say*, which is where this
            // client was before the field and is still safe under the latch.
            if (_pendingLeaveRoomId == null
                || (!string.IsNullOrEmpty(answeredRoomId)
                    && answeredRoomId != _pendingLeaveRoomId))
            {
                if (WarnGate.ShouldEmit(ref _lastStaleLeaveResponseWarnTicks))
                {
                    Debug.LogWarning(
                        "[RTMPE] RoomManager: a LeaveRoom reply arrived for a request " +
                        "that is no longer outstanding and was ignored.");
                }
                return;
            }

            if (ok)
            {
                _pendingLeaveRoomId = null;
                LastLeftRoom = _currentRoom;
                ForgetRoomScopedState();
                SafeRaise(OnRoomLeft);
                return;
            }

            // ⛔ A refusal does NOT retire the request, and the asymmetry is
            // deliberate.  Two copies of one leave can be in flight at once —
            // the seat is cleared only after the Room Service answers, so the
            // second copy is forwarded too and is refused, because by then the
            // room it names is gone.  Which of the two replies arrives first is
            // not ordered.  Retiring the request on the refusal would spend the
            // latch the genuine success needs, and that success would then be
            // discarded as answering nothing — leaving the client holding a room
            // it has already left, with no way back but a second LeaveRoom the
            // application has just been told failed.  A lost success costs more
            // than a duplicated error.
            //
            // The duplicate error is suppressed rather than tolerated, on the
            // same terms `HandleJoinResponse` suppresses its own: one report per
            // request, and the request is still outstanding.
            bool firstRefusal = !_pendingLeaveRefused;
            _pendingLeaveRefused = true;
            if (firstRefusal) SafeRaise(OnRoomError, "LeaveRoom failed");
        }

        private void HandlePlayerLeftNotification(byte[] payload)
        {
            if (!RoomPacketParser.ParsePlayerLeftNotification(payload, out string playerId))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedPlayerLeftWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed PlayerLeft notification.");
                return;
            }

            // 🔴 A departure notice naming THIS client is not a roster edit — it
            // is this client's own removal, and the branch that knows what to do
            // with that was in the sibling handler only.
            //
            // The server reclaims a seat that has stopped being seen
            // (SeatReaper, once ROOM_SEAT_LEASE_EXPIRY_SECS lapses — five minutes
            // by default) and publishes its own event for it, so this
            // notice reaches the very client whose seat was taken. Without this
            // branch the SDK pruned the local player from its own roster, raised
            // OnPlayerLeft with the local id, and left `_currentRoom` set: the
            // client went on believing it was in a room the server had already
            // reclaimed, every object it spawned was dropped locally as a ghost
            // of an absent owner, and nothing anywhere said so.
            //
            // ⛔ Not reported through OnPlayerLeft, for the reason
            // HandlePlayerKicked states one screen down and this handler had to
            // learn separately: that path records a departed-player tombstone,
            // and routing the local id into it is what makes this client's own
            // spawns unreachable. The kick path is mirrored exactly — teardown,
            // then OnRoomLeft — minus the kicker, because a reclaimed seat has
            // nobody to name.
            if (!string.IsNullOrEmpty(_localPlayerId) && playerId == _localPlayerId)
            {
                // Gated like every other wire-driven diagnostic in this file.
                // ⛔ The tempting argument for leaving it ungated is that
                // ForgetRoomScopedState empties _localPlayerId, so the branch
                // cannot be re-entered — true today, held by a method call two
                // screens away, and exactly the shape of premise this file's
                // own rule warns survives its own deletion. The gate emits the
                // first occurrence unthrottled, which is the one that matters.
                if (WarnGate.ShouldEmit(ref _lastSeatReleasedWarnTicks))
                {
                    Debug.LogWarning(
                        "[RTMPE] RoomManager: this client's seat was released by the server "
                        + "(the room stopped seeing it) — leaving the room. Re-enter it to "
                        + "continue playing.");
                }

                LastLeftRoom = _currentRoom;
                ForgetRoomScopedState();
                SafeRaise(OnRoomLeft);
                return;
            }

            // Prune the departed player from the local roster BEFORE notifying,
            // so CurrentRoom.MasterId / IsMasterClient / roster queries reflect
            // the post-leave state.  This mirrors the kick path
            // (HandlePlayerKicked) exactly.  Without it a leaver — including a
            // departing host — lingers in Players with a stale IsHost flag,
            // which (a) leaves IsMasterClient/MasterId wrong after any leave and
            // (b) breaks host-migration ownership reassignment (NEW-OWNERSHIP-1):
            // a departed host still reporting IsHost=true makes MasterId resolve
            // to the gone player, so the reassignment guards skip and the
            // orphaned objects freeze.  RemovePlayerFromRoom is a no-op when the
            // id is absent (e.g. a player who joined after this client's roster
            // snapshot), so this is always safe.
            // 🔴 A departure notification about a room this client is not in is
            // not a roster edit, and until this returned it was routed into one.
            //
            // The self-branch above recognises this client by `_localPlayerId` —
            // which its own teardown EMPTIES. So a second notification naming
            // this client (a reliable-channel retransmit; a client leave racing
            // the reclaim, two datagrams with no ordering between them) missed
            // the branch and fell through to here, and the raise below fired
            // with THIS CLIENT'S OWN ID. That is exactly the harm the branch
            // exists to prevent: NetworkManager keeps OnPlayerLeft subscribed
            // across a room exit, so the local id reached the departed-player
            // tombstone and this client's own spawns became unreachable.
            //
            // ⛔ Stated over the ROOM rather than over the id, because the id is
            // the thing the teardown clears. The comment two screens up called
            // that clearing "a premise held by a method call two screens away"
            // and gated a log line on it; the premise was load-bearing for more
            // than the log line.
            if (_currentRoom == null)
            {
                return;
            }

            _currentRoom = RemovePlayerFromRoom(_currentRoom, playerId);

            SafeRaise(OnPlayerLeft, playerId);
        }

        /// <summary>
        /// What was wrong with the most recent room list, or
        /// <see cref="RoomPacketParser.RoomListOutcome.Ok"/> once one has
        /// arrived whole.
        /// </summary>
        /// <remarks>
        /// This tracks the current state rather than the session's history: a
        /// list that arrives complete retires it, so a game may poll it to
        /// raise and clear a "room browser unavailable" banner. It is the only
        /// way application code can tell a project that has no rooms from one
        /// whose room list is not being answered — <see cref="OnRoomError"/>
        /// carries the same news to a subscriber, and a game that polls rather
        /// than subscribes needs it here.
        /// </remarks>
        public RoomPacketParser.RoomListOutcome LastRoomListProblem { get; private set; }
            = RoomPacketParser.RoomListOutcome.Ok;

        private void HandleListResponse(byte[] payload)
        {
            // The wait ends here, before the payload is judged: a malformed
            // answer is still an answer, and what it costs the caller is reported
            // through LastRoomListProblem below.  The watch reports a different
            // thing — that nothing came back at all — and a reply this manager
            // could not read is not that.
            //
            // ⚠️ The leave watch does NOT do this, and the asymmetry is the
            // point.  What a list caller wants is the list, and an unreadable
            // reply denies it while telling it so.  What a leave caller wants is
            // to know whether it left, and an unreadable reply denies that and
            // says nothing — so the watch stays armed and its wording is "no
            // answer this client could act on", which is true of both silences.
            if (_roomListRepliesOwed > 0)
            {
                _roomListRepliesOwed--;
                if (_roomListRepliesOwed == 0) _roomListPending = false;
                _roomListTimedOut = false;
            }

            bool readable = RoomPacketParser.ParseRoomListResponse(
                payload, out RoomInfo[] rooms,
                out RoomPacketParser.RoomListOutcome outcome);

            // ⛔ A payload that is not a room list is handled exactly as it was:
            // one gated line, and no event. That is the uniform contract this
            // manager keeps for all seven inbound room packet types, and a
            // suite drives a hundred malformed payloads of each to hold it.
            // Reporting one type differently is not what this repair is about.
            //
            // What it leaves standing is that a caller whose RoomList response
            // is malformed waits on an event that never comes. That is the
            // deadline question `ROOM-RD-03` and `ROOM-RD-07` answer for room
            // entry and the lobby, and answering it here means giving RoomList
            // a driven timeout — not raising an error on a packet the manager
            // has always treated as noise.
            if (!readable)
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedRoomListWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed RoomList response.");
                LastRoomListProblem = outcome;
                return;
            }

            // Written on every response, gated on none: a property assignment
            // fans out to nobody, so a peer flooding this costs one store per
            // packet. It is the member an application polls, and it is retired
            // only by a list that arrived whole — a banner raised on a refusal
            // has to come down when the browser recovers, and one that never
            // returns to Ok cannot tell a project that has recovered from one
            // still failing.
            LastRoomListProblem = outcome;

            // ⛔ The gate holds the log line and nothing else, and the report
            // sits outside it.
            //
            // An earlier version had the report inside, on the reasoning that a
            // response is a packet and its rate is therefore the sender's
            // choice. That reasoning is false for this opcode: 0x23 is refused
            // in cleartext at both ends, the gateway is its only producer, and
            // it is only ever sent to the session that asked. There is no peer
            // who can pace it, so `RPC-RD-04`'s premise does not transfer.
            //
            // And the cost was concrete rather than theoretical. The gate is a
            // one-second window, so a player pressing Refresh twice against a
            // refusing service was answered — before this repair — with two
            // wrong lists, and would have been answered with one report and then
            // nothing at all. That is a bounded falsehood traded for an
            // unbounded silence, which is the shape the lobby-timeout repair had
            // to have undone.
            //
            // One gate per REASON, each a named field: a service that is
            // refusing must not decide whether "your SDK is older than the
            // gateway" is ever printed, and that one an integrator cannot
            // recover from without seeing it.
            if (outcome != RoomPacketParser.RoomListOutcome.Ok)
            {
                string what = Describe(outcome);
                switch (outcome)
                {
                    case RoomPacketParser.RoomListOutcome.Refused:
                        if (WarnGate.ShouldEmit(ref _lastRoomListRefusedWarnTicks))
                            Debug.LogWarning("[RTMPE] RoomManager: room list " + what);
                        break;
                    case RoomPacketParser.RoomListOutcome.RoomsOmitted:
                        if (WarnGate.ShouldEmit(ref _lastRoomListShortWarnTicks))
                            Debug.LogWarning("[RTMPE] RoomManager: room list " + what);
                        break;
                    default:
                        if (WarnGate.ShouldEmit(ref _lastRoomListUnknownStatusWarnTicks))
                            Debug.LogWarning("[RTMPE] RoomManager: room list " + what);
                        break;
                }
                SafeRaise(OnRoomError, "Room list " + what);
            }

            // A list shown short is still the project's rooms and is applied; a
            // refused one is not the project's rooms and is not. Raising
            // OnRoomListReceived with a refusal's empty array is the defect
            // itself — it tells the game, positively, that there are no rooms.
            if (RoomPacketParser.CarriesUsableList(outcome))
                SafeRaise(OnRoomListReceived, rooms);
        }

        // ⚠️ Total over the enum, and one arm is not reached today: a payload
        // that is not a room list returns above this, keeping the uniform
        // contract every inbound room packet type has. The arm is kept because
        // the alternative — falling to the default — would describe a known
        // fault as an unexpected state the first time somebody moves that
        // return, and a wrong sentence is worse than a missing one.
        private static string Describe(RoomPacketParser.RoomListOutcome outcome)
        {
            switch (outcome)
            {
                case RoomPacketParser.RoomListOutcome.Refused:
                    return "refused — the server declined the request, so the empty "
                         + "list is not this project's rooms.";
                case RoomPacketParser.RoomListOutcome.RoomsOmitted:
                    return "shown short — some rooms were omitted, so the browser is "
                         + "not the whole set.";
                case RoomPacketParser.RoomListOutcome.UnknownStatus:
                    return "qualified by a status this SDK build does not know; the "
                         + "list was not applied. Update the SDK to match the gateway.";
                case RoomPacketParser.RoomListOutcome.Unreadable:
                    return "unreadable — the response was not a room list.";
                default:
                    return "in an unexpected state.";
            }
        }

        // ── Phase 2 inbound handlers ──────────────────────────────────────────

        /// <summary>
        /// Apply a <c>master_client_changed</c> server broadcast (packet
        /// <c>0x2C</c>) to the local room snapshot and fire
        /// <see cref="OnMasterClientChanged"/>.
        /// Public so unit tests can exercise the path without a live socket;
        /// production code should rely on the routing in
        /// <see cref="HandleRoomPacket"/>.
        /// </summary>
        public void HandleMasterClientChanged(byte[] payload)
        {
            if (!MasterClientPacketParser.ParseChanged(
                    payload, out string previousMasterId, out string newMasterId))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedMasterChangedWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed MasterClientChanged payload.");
                return;
            }

            // Read from the PAYLOAD, never from the roster the edit below
            // produces. The broadcast is the server's word that the role moved,
            // and `RehostRoom` returns the room unchanged when the promoted
            // player is absent from this client's copy — so a resolution
            // conditioned on that edit would miss exactly the case this report
            // exists to serve. Sitting above the edit is ordering hygiene rather
            // than a dependency: `RehostRoom` touches no table.
            _pendingHostCommands.Resolve(HostCommandKind.MasterTransfer, newMasterId);

            if (_currentRoom != null && !string.IsNullOrEmpty(newMasterId))
            {
                _currentRoom = RehostRoom(_currentRoom, newMasterId);
            }
            SafeRaise(OnMasterClientChanged, previousMasterId ?? string.Empty, newMasterId ?? string.Empty);
        }

        /// <summary>
        /// Apply a <c>player_kicked</c> broadcast (packet <c>0x2E</c>
        /// delivered inbound).  For any other player this prunes the roster and
        /// fires <see cref="OnPlayerKicked"/> and <see cref="OnPlayerLeft"/>;
        /// when the target is this client it is a removal from the room, and
        /// <see cref="OnPlayerKicked"/> is followed by the same teardown an
        /// explicit leave performs, ending in <see cref="OnRoomLeft"/>.
        /// </summary>
        public void HandlePlayerKicked(byte[] payload)
        {
            if (!MasterClientPacketParser.ParseKick(
                    payload, out string kickerId, out string targetPlayerId))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedKickWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed KickPlayer payload.");
                return;
            }
            if (string.IsNullOrEmpty(targetPlayerId)) return;

            // Stated once for every target, before the self-branch below tears
            // the room down and clears this table along with it. Either placement
            // settles the entry — that branch would empty the table anyway — and
            // one statement covering both is the point.
            _pendingHostCommands.Resolve(HostCommandKind.Kick, targetPlayerId);

            // Resolved before any teardown clears it.
            bool isSelf = !string.IsNullOrEmpty(_localPlayerId)
                          && targetPlayerId == _localPlayerId;

            if (isSelf)
            {
                // The server has already dropped this client's seat, so what is
                // being applied is a departure, not a roster edit. Reporting it
                // through OnPlayerLeft would route the local id into the departed-
                // player tombstone that path records for others, and every spawn
                // this client went on to make would be dropped locally as a ghost
                // of an absent owner — while the room state it kept said it was
                // still a member.
                SafeRaise(OnPlayerKicked, kickerId ?? string.Empty, targetPlayerId);

                LastLeftRoom = _currentRoom;
                ForgetRoomScopedState();
                SafeRaise(OnRoomLeft);
                return;
            }

            if (_currentRoom != null)
            {
                _currentRoom = RemovePlayerFromRoom(_currentRoom, targetPlayerId);
            }
            SafeRaise(OnPlayerKicked, kickerId ?? string.Empty, targetPlayerId);
            SafeRaise(OnPlayerLeft, targetPlayerId);
        }

        /// <summary>
        /// Apply an <c>all_players_scene_loaded</c> broadcast (packet
        /// <c>0x2F</c> delivered inbound) by firing
        /// <see cref="OnAllPlayersSceneLoaded"/>.  No local state change —
        /// the room's authoritative scene already lives in
        /// <see cref="RoomInfo.CurrentScene"/>.
        /// </summary>
        public void HandleAllPlayersSceneLoaded(byte[] payload)
        {
            if (!MasterClientPacketParser.ParseSceneLoaded(payload, out string sceneName))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedSceneLoadedWarnTicks))
                    Debug.LogWarning("[RTMPE] RoomManager: malformed SceneLoaded broadcast.");
                return;
            }
            SafeRaise(OnAllPlayersSceneLoaded, sceneName ?? string.Empty);
        }

        /// <summary>
        /// Return a new <see cref="RoomInfo"/> identical to
        /// <paramref name="room"/> but with <paramref name="newMasterId"/>
        /// marked as host on the roster.  When the target is not on the
        /// roster the room is returned unchanged — a late-arriving broadcast
        /// for a player the SDK has already pruned should not create a fresh
        /// phantom entry.
        /// </summary>
        private static RoomInfo RehostRoom(RoomInfo room, string newMasterId)
        {
            var roster = room.Players;
            if (roster == null || roster.Length == 0) return room;
            bool targetOnRoster = false;
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == newMasterId)
                {
                    targetOnRoster = true;
                    break;
                }
            }
            if (!targetOnRoster) return room;

            var newRoster = new PlayerInfo[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                var p = roster[i];
                if (p == null) continue;
                newRoster[i] = (p.PlayerId == newMasterId) == p.IsHost
                    ? p
                    : p.WithIsHost(p.PlayerId == newMasterId);
            }
            return room.WithPlayers(newRoster);
        }

        /// <summary>
        /// Return a new <see cref="RoomInfo"/> with <paramref name="player"/>
        /// appended to the roster.  When a player with the same id is already
        /// seated the room is returned unchanged, so a duplicate join
        /// notification — a reliable-channel retransmit echo, or a join that
        /// races the initial room snapshot — cannot seat the same player twice.
        /// The companion to <see cref="RemovePlayerFromRoom"/>: both move
        /// <see cref="RoomInfo.PlayerCount"/> with the roster via
        /// <see cref="RoomInfo.WithRoster"/>.
        /// </summary>
        private static RoomInfo AddPlayerToRoom(RoomInfo room, PlayerInfo player)
        {
            if (player == null || string.IsNullOrEmpty(player.PlayerId)) return room;
            var roster = room.Players ?? Array.Empty<PlayerInfo>();
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == player.PlayerId)
                    return room;
            }

            var newRoster = new PlayerInfo[roster.Length + 1];
            Array.Copy(roster, newRoster, roster.Length);
            newRoster[roster.Length] = player;
            return room.WithRoster(newRoster);
        }

        /// <summary>
        /// Return a new <see cref="RoomInfo"/> with <paramref name="targetId"/>
        /// removed from the roster.  Unchanged when the target is not found.
        /// </summary>
        private static RoomInfo RemovePlayerFromRoom(RoomInfo room, string targetId)
        {
            var roster = room.Players;
            if (roster == null || roster.Length == 0) return room;
            int matchIndex = -1;
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == targetId)
                {
                    matchIndex = i;
                    break;
                }
            }
            if (matchIndex < 0) return room;

            var newRoster = new PlayerInfo[roster.Length - 1];
            int j = 0;
            for (int i = 0; i < roster.Length; i++)
            {
                if (i == matchIndex) continue;
                newRoster[j++] = roster[i];
            }
            return room.WithRoster(newRoster);
        }

        // ── Guards ─────────────────────────────────────────────────────────────

        private bool RequireConnected(string method)
        {
            var state = _getState();
            if (state == NetworkState.Connected || state == NetworkState.InRoom)
                return true;

            Debug.LogWarning($"[RTMPE] RoomManager.{method}: requires Connected or InRoom state (current: {state}).");
            return false;
        }

        /// <summary>
        /// Build a packet, reporting rather than raising when some argument
        /// cannot be encoded.
        /// </summary>
        /// <remarks>
        /// These methods answer a bad argument with a console line and no
        /// packet, and that has to hold for every bad argument rather than for
        /// the ones this class happens to check itself. The layers underneath
        /// refuse by throwing — a value past a field's budget, a payload past
        /// what one datagram carries — and an exception leaving a void method
        /// that reports everything else is a contract with a hole in it.
        ///
        /// ⛔ Scoped to the encoding, and to the exception the encoders raise
        /// for an argument they will not take. Nothing else is caught here.
        ///
        /// 🔑 The console line is rate-gated, unlike the one below it, and the
        /// difference is whose mistake it reports: the wire can reach this and
        /// cannot reach that. A create response drives the auto-join, and the
        /// room id it carries is the server's — so a gateway answering with an
        /// unusable id would otherwise choose how often this class writes to
        /// the console.
        ///
        /// ⚠️ The gate bounds that line and nothing else. <c>OnRoomError</c> is
        /// raised on every refusal, because each call really did fail and the
        /// three methods above document that channel as where their failures
        /// arrive. A gate on the event would decide the application's own rate
        /// for it, which is not this class's to decide — and <c>WarnGate</c>
        /// promises "at most once per second", never "at least once", so a
        /// first-ever refusal can lose its line to a concurrent emitter.
        /// </remarks>
        private bool TryBuild(string method, Func<byte[]> build, out byte[] packet)
        {
            try
            {
                packet = build();
                return true;
            }
            catch (ArgumentException ex)
            {
                packet = null;
                var error = $"{method}: {ex.Message}";
                if (WarnGate.ShouldEmit(ref _lastBuildRefusalWarnTicks))
                    Debug.LogError($"[RTMPE] RoomManager.{error}");
                SafeRaise(OnRoomError, error);
                return false;
            }
        }

        private long _lastBuildRefusalWarnTicks;

        /// <summary>
        /// Report a value the Room Service will refuse and answer whether the
        /// call may go on.
        /// </summary>
        /// <remarks>
        /// The refusal is local because the rule is knowable locally. The
        /// server's own answer does name the field and the value, but it costs
        /// a round trip and arrives on OnRoomError, away from the call that
        /// caused it — so the same news reaches the developer as a room that
        /// did not open rather than as an argument that was wrong.
        ///
        /// Ungated on purpose: every caller of this is an application call, so
        /// the rate is the developer's own and rate-limiting it would hide a
        /// mistake rather than bound a flood.
        ///
        /// ⚠️ And reported on <c>OnRoomError</c> as well as to the console. The
        /// three methods that call this document that event as where their
        /// failures arrive, a console line is invisible in a shipped player,
        /// and a request refused here never reaches the pending tables — so
        /// without the event the call would end in silence no game could
        /// observe, not even as a timeout.
        /// </remarks>
        private bool RequireAcceptable(string method, string reason)
        {
            if (reason == null) return true;

            var error = $"{method}: {reason}";
            Debug.LogError($"[RTMPE] RoomManager.{error}");
            SafeRaise(OnRoomError, error);
            return false;
        }

        private bool RequireInRoom(string method)
        {
            var state = _getState();
            if (state == NetworkState.InRoom && _currentRoom != null)
                return true;

            Debug.LogWarning($"[RTMPE] RoomManager.{method}: requires InRoom state (current: {state}).");
            return false;
        }

        /// <summary>
        /// The gate for a request that has to say WHICH room it is for: in a
        /// room, and that room can be named.
        /// </summary>
        /// <remarks>
        /// Five room-scoped requests carry the current room id, because they are
        /// sent reliably and a copy re-sent after the client moved on used to be
        /// applied wherever it landed.  Their builders refuse to encode a
        /// document that names no room — which would be that old behaviour,
        /// written on purpose — and a builder refuses by throwing.
        ///
        /// ⛔ Nothing refuses an entry reply that carries a zero-length room id,
        /// so `_currentRoom.RoomId` can be empty on a client the server put
        /// there.  Letting the throw out of a public method would make a
        /// malformed reply an exception in application code; the request is
        /// reported instead, on the channel these methods already report their
        /// other refusals on.  Nothing is lost by it: a room that cannot be
        /// named is a room no request could have addressed either.
        /// </remarks>
        private bool RequireNameableRoom(string method)
        {
            if (!RequireInRoom(method)) return false;
            if (!string.IsNullOrEmpty(_currentRoom.RoomId)) return true;

            var error =
                $"{method}: the current room carries no id, so a request cannot say which " +
                "room it is for and is not sent. The entry reply that seated this client " +
                "named no room.";
            // ⛔ Rate-limited, where `RequireInRoom`'s neighbouring line is not,
            // and the difference is whose mistake it reports.  Calling in the
            // wrong state is the developer's, and one line per call is how they
            // find out.  This state is the SERVER's — the entry reply named no
            // room — so a game that writes a property every frame would emit a
            // line every frame for a fault it cannot fix.
            //
            // ⚠️ The gate bounds THIS line and nothing else.  The event below is
            // raised on every call, so an application that logs its
            // `OnRoomError` subscriber will see one per call again — that is the
            // application's choice and its own rate to make, which is exactly
            // what the gate cannot decide for it.  Raising it every time is the
            // right half: each call really did fail, and both property writes
            // already answer their other refusals here.
            if (WarnGate.ShouldEmit(ref _lastUnnameableRoomWarnTicks))
                Debug.LogWarning("[RTMPE] RoomManager." + error);
            SafeRaise(OnRoomError, error);
            return false;
        }

        // ── Subscriber-isolated event invocation ───────────────────────────────
        //
        // Every public OnRoom* / OnPlayer* event is fired during inbound
        // packet processing.  A throwing application subscriber would
        // propagate up through HandleRoomPacket → NetworkManager.ProcessPacket
        // and short-circuit the rest of the inbound buffer for that frame,
        // leaving subsequent same-tick packets to act against half-applied
        // state.  Walk each multicast invocation list explicitly so the
        // failure of one subscriber does not deny delivery to its siblings.
        // Same discipline as M19-SYNC-01 (NetworkVariable.OnValueChanged) and
        // M19-CORE-07 (ReliableChannel callbacks).

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

        private static void SafeRaise<T1, T2>(Action<T1, T2> handler, T1 a1, T2 a2)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action<T1, T2>)subs[i])(a1, a2); }
                catch (Exception ex) { LogSubscriberThrow(ex); }
            }
        }

        private static void LogSubscriberThrow(Exception ex)
        {
            if (!WarnGate.ShouldEmit(ref _lastSubscriberThrowWarnTicks)) return;
            Debug.LogError(
                "[RTMPE] RoomManager: event subscriber threw " +
                $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                "remaining subscribers.");
        }

        // ── Test hooks ────────────────────────────────────────────────────────
        // Events are only accessible as delegates from within the declaring
        // class; tests use these helpers instead of reflection to count
        // subscribers.  Do NOT call from production code.

        internal int GetOnRoomJoinedSubscriberCount()
            => OnRoomJoined?.GetInvocationList()?.Length ?? 0;
        internal int GetOnRoomLeftSubscriberCount()
            => OnRoomLeft?.GetInvocationList()?.Length ?? 0;
        internal int GetOnRoomCreatedSubscriberCount()
            => OnRoomCreated?.GetInvocationList()?.Length ?? 0;
        internal int GetOnPlayerLeftSubscriberCount()
            => OnPlayerLeft?.GetInvocationList()?.Length ?? 0;
        internal int GetOnPlayerJoinedSubscriberCount()
            => OnPlayerJoined?.GetInvocationList()?.Length ?? 0;
    }
}
