// RTMPE SDK — Runtime/Core/RtmpeConnectionBootstrap.cs
//
// Attach it and a project connects, enters a room, and spawns its player.
//
// Those three steps are the same thirty lines in every project that uses this
// SDK: resolve a key, Connect, wait for the connection, ask for a room, wait for
// the room, look up a prefab id, Spawn, and undo all of it on the way out. The
// samples wrote them, the walkthrough wrote them, and every integrator wrote
// them again. This is those thirty lines, once, in the package.
//
// 🔑 The precedent is shipped: RtmpeSceneLoader does exactly this one layer up,
// and the manifest describes its sample as "a room moving between two scenes
// with no scene-loading code". This is the same promise for the way in.
//
// ⛔ Optional by attachment, not default by behaviour. A title with a lobby
// screen, a character selector, a queue of its own is not doing the common
// thing and should keep doing its own: every event this subscribes to stays
// public and stays documented. Automation removes the common pattern; it does
// not confiscate the particular one.
//
// 🔑 Everything it DECIDES is in ConnectionBootstrapOps, which names no Unity
// type. What is left here is the engine calls and the wiring — the part a test
// cannot reach, and therefore the part that must be as small as it can be made.

using System;
using RTMPE.Rooms;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Connects, enters a room and spawns the local player, with no code.
    /// </summary>
    /// <remarks>
    /// Attach one to a root GameObject in your **boot scene** — the scene the
    /// game never reloads — beside the <see cref="NetworkManager"/>. Assign a
    /// player prefab, choose how to enter a room, and press Play.
    /// <para>
    /// ⛔ The boot scene is not a style note. This component makes itself
    /// persistent, so a copy sitting in a scene the game returns to becomes a
    /// SECOND persistent bootstrap every time — and two of them connect twice
    /// and spawn twice. It is the rule <see cref="NetworkManager"/> states for
    /// itself and <c>RtmpeSceneLoader</c> enforces the same way.
    /// </para>
    /// <para>
    /// ⚠️ There is no API-key field here and there must never be one. Unity
    /// serialises a string field into the scene asset, which is committed and
    /// present in every build made from it; the key comes from
    /// <see cref="ApiKeySource"/>, in the order it consults them — a provider
    /// registered in code, then the Setup Wizard's vault in the Editor alone,
    /// then <c>--rtmpe-api-key-file</c>, <c>--rtmpe-api-key</c> and
    /// <c>RTMPE_API_KEY</c>. The vault is registered by the Editor assembly, so
    /// a player build reaches every source but that one.
    /// </para>
    /// </remarks>
    [AddComponentMenu("RTMPE/Connection Bootstrap")]
    [DisallowMultipleComponent]
    public sealed class RtmpeConnectionBootstrap : MonoBehaviour
    {
        // 🚨 [DisallowMultipleComponent] forbids two copies on ONE GameObject and
        // says nothing about two on two — which is exactly what DontDestroyOnLoad
        // manufactures. The most recently enabled instance wins, as
        // RtmpeSceneLoader and InterestManager resolve the same collision, and
        // the WINNER announces the takeover, naming itself: a project that
        // connects twice deserves to be told which copy is acting rather than
        // left to measure it.
        private static RtmpeConnectionBootstrap s_active;

        // ── Configuration ──────────────────────────────────────────────────────

        [Header("Connection")]
        [Tooltip("Connect as soon as the scene starts. Turn this off to call Connect() yourself.")]
        [SerializeField] private bool _connectOnStart = true;

        [Tooltip("After a drop, re-enter the room this component was in rather than opening a new one.")]
        [SerializeField] private bool _rejoinLastRoom = true;

        // ⛔ The half of drop recovery that was missing, and without it the flag
        // above described something unreachable: NOTHING in the SDK calls
        // Reconnect(). The heartbeat watchdog ends the session and says, in its
        // own comment, that it preserves the token "so apps can observe
        // OnDisconnected and call Reconnect()" — so a component that takes the
        // entry flow over and leaves that call to the application has taken over
        // the easy half. The retry itself stays the SDK's: this asks once per
        // session and the SDK's own bounded ladder does the rest.
        [Tooltip("After an unexpected drop, ask the SDK to restore the session with the "
                 + "reconnect token it holds. Turn this off to drive recovery yourself.")]
        [SerializeField] private bool _reconnectOnDrop = true;

        // 🔴 The mobile half, and it had no implementation anywhere in the SDK:
        // `OnApplicationPause` appeared in no file in the package. A suspended
        // app stops receiving Update, so the heartbeat watchdog does not run
        // while the socket is being torn down by the OS — it notices only after
        // the app is back, several missed heartbeats later, and by then a
        // suspension of any real length has outlived the gateway's session
        // timeout and often the reconnect token with it. The player returns to
        // an app that spends seconds looking connected and then reports that it
        // cannot restore anything.
        //
        // ⛔ And the recipe the documentation used to give for this called
        // Disconnect() on pause — which destroys the very token the resume
        // needs. Nothing here disconnects.
        [Tooltip("On mobile, recover as soon as the app returns to the foreground rather than "
                 + "waiting for the heartbeat watchdog to notice the socket died while it was "
                 + "suspended. Requires Reconnect On Drop.")]
        [SerializeField] private bool _recoverOnResume = true;

        // 🔑 The SDK's own instruction, unfollowed. `NetworkManager.Reconnect`
        // documents in its remarks that "on exhaustion the application MUST
        // fall back to a full Connect(string) with credentials" — and both
        // reports this component printed instead said the same thing in prose,
        // to a developer, at runtime: "Call Connect() to authenticate again."
        // A component that has taken the entry flow over and then asks the
        // application to finish recovery by hand has taken over the half that
        // works.
        //
        // ⛔ A reconnect token does not survive an arbitrary suspension, so on
        // mobile this is not an edge case: it is the ordinary way a session
        // ends.
        [Tooltip("When the SDK's reconnect ladder is spent, or there is no session to restore at "
                 + "all, authenticate again rather than leaving the player offline.")]
        [SerializeField] private bool _freshSessionWhenRecoveryFails = true;

        [Tooltip("How many new sessions may be opened in a row without one of them reaching a "
                 + "room. Stops a server outage from becoming a connect loop.")]
        [Range(1, 10)]
        [SerializeField] private int _freshSessionAttempts = 3;

        [Header("Room")]
        // ⛔ CreateRoom, because it is the only policy that works with every
        // other field left empty: the server names the room and chooses its
        // capacity. Matchmaking REFUSES an empty mode — it throws — so shipping
        // it as the default made the component's own out-of-the-box
        // configuration the one configuration it could not run.
        [SerializeField] private RoomEntryPolicy _entryPolicy = RoomEntryPolicy.CreateRoom;

        [Tooltip("CreateRoom only. The room's display name; empty lets the server choose.")]
        [SerializeField] private string _roomName = string.Empty;

        [Tooltip("JoinRoom only. The id of the room to enter, issued by the server.")]
        [SerializeField] private string _roomId = string.Empty;

        [Tooltip("CreateRoom and Matchmaking. 0 lets the server choose; otherwise 1–100.")]
        [Range(0, 100)]
        [SerializeField] private int _maxPlayers;

        [Tooltip("Matchmaking only. Players are matched with others asking for the same mode.")]
        [SerializeField] private string _matchmakingMode = string.Empty;

        [Header("Spawn")]
        [Tooltip("Spawned for this client on entering a room. It must be listed in "
                 + "Window → RTMPE → Network Prefabs, which is where its id comes from. "
                 + "Leave empty to enter the room and spawn nothing.")]
        [SerializeField] private GameObject _playerPrefab;

        // ── The surface an application steers it with ──────────────────────────

        /// <summary>
        /// Choose the prefab to spawn, overriding the Inspector field.
        /// </summary>
        /// <remarks>
        /// A delegate rather than a <c>virtual</c> method, and public rather
        /// than protected, because choosing a character or a team is a decision
        /// a project makes from its own code — not one it should have to
        /// subclass a shipped component to reach.
        /// </remarks>
        public Func<GameObject> ChoosePlayerPrefab;

        /// <summary>
        /// Choose where the player appears. Defaults to this object's transform.
        /// </summary>
        public Func<Pose> ChooseSpawnPose;

        /// <summary>Raised after the local player has been spawned.</summary>
        public event Action<NetworkBehaviour> OnLocalPlayerSpawned;

        /// <summary>
        /// Raised whenever the flow cannot continue, with the reason already
        /// written for a human.
        /// </summary>
        /// <remarks>
        /// ⛔ Every refusal this component makes or observes reaches here as
        /// well as the console. Silence is what makes a network fault look like
        /// a bug in the game, and a component that took the work over owes the
        /// application the news when the work does not happen.
        /// <para>
        /// ⚠️ The limit, stated rather than implied: a few of the SDK's own
        /// refusals are log-only and reach no event at all — a JoinRoom with an
        /// empty id, a CreateRoom while its in-flight table is full, a Connect
        /// on a disabled manager. This component pre-empts the ones its own
        /// configuration can cause; the remainder are visible in the console and
        /// nowhere else, and that is the SDK's contract rather than this
        /// component's choice.
        /// </para>
        /// </remarks>
        public event Action<string> OnBootstrapFailed;

        /// <summary>The avatar this component spawned, or null.</summary>
        public NetworkBehaviour LocalPlayer { get; private set; }

        /// <summary>The room this component last entered, or null.</summary>
        public string CurrentRoomId => _rememberedRoomId;

        // ── State ──────────────────────────────────────────────────────────────

        private NetworkManager      _manager;
        private RoomManager         _rooms;
        private MatchmakingManager  _matchmaking;
        private bool                _subscribed;

        // 🚨 Whether the SDK is entering a room for us, OBSERVED rather than
        // inferred. The first version of this component worked it out at the
        // transition into Connected — previous state, settings flag, snapshot —
        // and that inference was wrong in both directions: the poll ran it
        // against a stale previous state and entered a second time, and a
        // refused rejoin left it true for the rest of the session with no way
        // back. `OnAutoRejoinAttempt` is the SDK saying it, once, immediately
        // before its own JoinRoom.
        private bool _sdkIsEntering;

        // Whether an entry operation has been issued for the session now up.
        private bool _entryIssued;

        // Whether the decision is owed. It is taken on the frame AFTER it is
        // asked for, and that delay is the whole of the interlock. Two facts
        // carry it, and naming the wrong one is how a later reader deletes the
        // deferral: the transition and the auto-rejoin announcement are raised
        // in ONE synchronous handler, so no Update can run between them; and
        // that handler runs on MainThreadDispatcher, which drains the receive
        // queue at execution order −999 — behind NetworkManager's own −1000 and
        // ahead of every component at the default order, this one included. So
        // by the time this Update runs the SDK has finished speaking, and
        // deciding inside the transition handler would be deciding one statement
        // too early.
        private bool _entryPending;

        // Whether this component believes it is in a room. It is what makes
        // OnRoomError an ENTRY failure rather than any failure: RoomManager
        // raises that event for a ListRooms timeout, a property-write conflict
        // and a leave that drew no answer, none of which is this component's
        // business and every one of which used to make it forget its room.
        private bool _inRoom;

        private string _rememberedRoomId;

        // One recovery per session, because the SDK's ladder ends in another
        // Disconnected: asking again there is a client that never stops asking a
        // server that has stopped answering.
        private bool _recoveryOffered;

        // Latched on the way out so the way back in can be told from Unity's
        // start-up call of the same handler.  See OnApplicationPause.
        private bool _wasSuspended;

        // True once this component has actually opened a connection, so a
        // resume can tell "deferred on purpose" from "connected and dropped".
        private bool _everConnected;

        // True while the last departure was one the application asked for.
        // Cleared by a fresh Connect(), because asking to connect withdraws the
        // decision to be disconnected.
        private bool _departureWasRequested;

        // Fresh sessions opened since the last one that reached a room.  Reset
        // by arrival, not by connection: a session that authenticates and then
        // cannot enter a room has not recovered anything, and counting it as
        // success is how a bound stops bounding.
        private int _freshSessionsSpent;

        // Whether the entry now outstanding reports through the ROOM channel.
        // Matchmaking does not — it has three events of its own — so a room
        // error arriving while a match is in flight belongs to something else
        // the application asked for, and used to be reported as this
        // component's own entry failing.
        private bool _entryUsesTheRoomChannel;

        private long _lastDuplicateWarnTicks;
        private long _lastNothingToSpawnWarnTicks;
        private long _lastLeftRoomNoticeTicks;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            // ⛔ Root only. DontDestroyOnLoad moves the object's ROOT, so calling
            // it on a child would silently promote a hierarchy the author did
            // not ask to keep, and Unity warns about it besides. A child of an
            // already-persistent object needs nothing.
            if (transform.parent == null) DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Whether this is the copy that acts.
        /// </summary>
        /// <remarks>
        /// 🚨 A vacant slot is claimed rather than left vacant, and the holder
        /// releasing it on the way out hands it to nobody — the reasoning
        /// RtmpeSceneLoader records for the same collision. ⛔ Until this
        /// existed the slot was WRITTEN and never READ: the console said "it is
        /// the one that will act" while both copies connected and both spawned,
        /// which is worse than saying nothing.
        /// </remarks>
        private bool TheCopyThatActs()
        {
            if (s_active == null) s_active = this;
            return s_active == this;
        }

        private void OnEnable()
        {
            if (!_subscribed && _manager != null) Subscribe();

            var previous = s_active;
            s_active = this;

            string duplicate = BootstrapReports.SecondBootstrapTookOver(
                previous != null && previous != this, gameObject.name);
            if (duplicate != null && WarnGate.ShouldEmit(ref _lastDuplicateWarnTicks))
            {
                Debug.LogError(duplicate, this);
            }
        }

        private void Start()
        {
            if (_connectOnStart) Connect();
        }

        private void OnDisable()
        {
            // 🚨 Disable is not destruction. What is outstanding — the room this
            // component remembers, the avatar it spawned — belongs to a session
            // that is still running, and a title that hides its boot object
            // through a transition has not asked to leave the room. Only the
            // listening ends here.
            Unsubscribe();
            if (s_active == this) s_active = null;
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (s_active == this) s_active = null;
        }

        // The manager is a singleton the scene owns, and every facade it hands
        // out is rebuilt per session: RoomManager and MatchmakingManager are
        // constructed fresh on each (re)connect, so a component holding the
        // previous pair is subscribed to objects nothing raises events on any
        // more. RtmpeSceneLoader re-binds on exactly this reasoning.
        private void Update()
        {
            var live = NetworkManager.TryGetInstance(out var manager) ? manager : null;
            var rooms = live == null ? null : live.Rooms;
            var matchmaking = live == null ? null : live.Matchmaking;

            // ⚠️ All three, not the two that came to mind. The session rebuilds
            // them together today, so comparing the room facade alone happens to
            // be sufficient — a property of the current code, not of the
            // contract, and the kind of thing that stops being true silently.
            if (ReferenceEquals(live, _manager)
                && ReferenceEquals(rooms, _rooms)
                && ReferenceEquals(matchmaking, _matchmaking))
            {
                if (!_subscribed && _manager != null) Subscribe();

                // The decision, one frame after it was asked for. Everything the
                // SDK had to say about entering a room for us has been said by
                // now: its receive queue is drained by MainThreadDispatcher at
                // execution order −999, ahead of this component's default order.
                //
                // ⛔ A component enabled after the connection came up sees no
                // transition at all, so the poll also ASKS: a bootstrap waiting
                // for an event that has been and gone is a bootstrap that does
                // nothing. Asking is not deciding — _entryIssued is what stops a
                // second entry, and _sdkIsEntering is what stops a duplicate of
                // the SDK's own.
                if (_subscribed && _manager != null && _manager.State == NetworkState.Connected)
                {
                    if (!_entryIssued) _entryPending = true;
                    if (_entryPending) EnterARoom();
                }

                return;
            }

            // ⚠️ Every facade is rebuilt per session AND its subscribers are
            // adopted onto the replacement (RoomManager.AdoptSubscribersFrom),
            // so a rebind must not add a second copy of a handler the new
            // instance already carries. Subscribe() removes before it adds for
            // exactly this reason; without it the count grew by one per
            // reconnect ATTEMPT, and one refusal produced a console line per
            // attempt ever made.
            Unsubscribe();
            _manager     = live;
            _rooms       = rooms;
            _matchmaking = matchmaking;

            // 🚨 A rebuilt facade IS a new session, and everything the last one
            // decided has to go with it — including when this component was
            // DISABLED across the drop and saw no OnDisconnected at all. That
            // gap stranded it: the entry stayed spent and `_inRoom` stayed true,
            // so the poll never entered, the room-error channel stayed shut, and
            // the component sat connected, roomless and silent for the rest of
            // the session. Hiding the boot object through a transition is
            // something OnDisable explicitly supports, so this is a supported
            // sequence and not an abuse.
            //
            // ⛔ The remembered room is NOT cleared, for the reason it survives a
            // drop: re-entering it is what the next session is for.
            ForgetTheSession();
            if (_manager == null) return;

            Subscribe();
        }

        // ── The application's entry points ─────────────────────────────────────

        /// <summary>
        /// Resolve a key and open the connection. Called by <c>Start</c> unless
        /// <c>Connect On Start</c> is off.
        /// </summary>
        public void Connect()
        {
            if (!TheCopyThatActs()) return;

            if (RefusedByItsOwnSettings()) return;

            if (!NetworkManager.TryGetInstance(out var manager))
            {
                Fail(BootstrapReports.NoManager);
                return;
            }

            _manager     = manager;
            _rooms       = manager.Rooms;
            _matchmaking = manager.Matchmaking;
            if (!_subscribed) Subscribe();

            // Resolved rather than serialised, and the only supported way: a key
            // typed into the Inspector is written into the scene asset,
            // committed with it, and present in every build made from it.
            if (!ApiKeySource.TryResolve(out string apiKey))
            {
                // Where the report will be read. The wizard's vault is registered
                // by the Editor assembly, which a build does not carry, so the
                // remedy that helps here is not the remedy that helps there.
                // Asked of the engine rather than of the preprocessor: a value
                // keeps both texts compiled in every configuration, and a branch
                // the preprocessor resolves would settle the question at compile
                // time in whichever direction the compiling configuration happens
                // to point.
                Fail(BootstrapReports.NoApiKey(
                    ApiKeySource.CommandLineFileOption,
                    ApiKeySource.EnvironmentVariableName,
                    ApiKeySource.LastError == null ? null : ApiKeySource.LastError.Message,
                    Application.isEditor));
                return;
            }

            _everConnected = true;
            _departureWasRequested = false;
            manager.Connect(apiKey);
        }

        /// <summary>
        /// Forget the remembered room and enter one again under the configured
        /// policy, on the connection that is already up.
        /// </summary>
        /// <remarks>
        /// For the case the events cannot answer on their own: matchmaking that
        /// timed out, a room that refused the join, a player who left and wants
        /// another game. It is deliberately not automatic — retrying a refusal
        /// nobody looked at is how a client hammers a server that has already
        /// said no.
        /// </remarks>
        public void Restart()
        {
            // ⛔ Refused while the session is IN a room, and the refusal is the
            // repair: this used to forget the remembered room and then find it
            // could issue nothing, because an entry is only issued from
            // Connected. A drop arriving after that put the player into a
            // brand-new empty room instead of back into their game. Leaving is
            // the application's call and the SDK's own LeaveRoom is how it is
            // made.
            if (_manager != null && _manager.State == NetworkState.InRoom)
            {
                Fail(BootstrapReports.RestartWhileInARoom);
                return;
            }

            _rememberedRoomId = null;
            _entryIssued      = false;
            _sdkIsEntering    = false;
            _entryPending     = true;
            if (_manager != null && _manager.State == NetworkState.Connected) EnterARoom();
        }

        // ── Wiring ─────────────────────────────────────────────────────────────

        // ⚠️ Every attachment removes before it adds. A `-=` for a handler that
        // is not attached is a no-op, and a handler the SDK's subscriber
        // adoption has already carried onto a replacement facade is exactly the
        // case a bare `+=` doubles.
        //
        // 🔑 It is load-bearing for the two FACADES and defensive for the
        // manager, and the difference is worth knowing before anyone trims it:
        // NetworkManager rebuilds RoomManager and MatchmakingManager on every
        // (re)connect and adopts the old subscribers onto the replacements, so
        // there a bare `+=` adds a second copy once per attempt. The manager is
        // a singleton whose own events are never adopted, and this component
        // never subscribes to it twice without unsubscribing first — so that
        // third pair states the rule rather than repairing anything, and a
        // mutation removing it is invisible by construction.
        private void Subscribe()
        {
            _manager.OnStateChanged      -= HandleStateChanged;
            _manager.OnDisconnected      -= HandleDisconnected;
            _manager.OnReconnectFailed   -= HandleReconnectFailed;
            _manager.OnAutoRejoinAttempt -= HandleSdkIsRejoining;
            _manager.OnStateChanged      += HandleStateChanged;
            _manager.OnDisconnected      += HandleDisconnected;
            // 🔑 The ladder's own verdict, and the only one. OnDisconnected fires
            // once per failed ATTEMPT; this fires once when the attempts are gone.
            _manager.OnReconnectFailed   += HandleReconnectFailed;
            _manager.OnAutoRejoinAttempt += HandleSdkIsRejoining;

            if (_rooms != null)
            {
                // ⛔ OnRoomJoined only, never OnRoomCreated. Creating a room with
                // the default options seats the creator in it, and the join is
                // what makes the session InRoom — a spawn issued on the creation
                // is a spawn issued before there is a room to put it in.
                // CreateRoomOptions says so itself: start gameplay from
                // OnRoomJoined. Matchmaking reaches the same event, so one
                // handler covers all three policies.
                _rooms.OnRoomJoined -= HandleRoomJoined;
                _rooms.OnRoomLeft   -= HandleRoomLeft;
                _rooms.OnRoomError  -= HandleRoomError;
                _rooms.OnRoomJoined += HandleRoomJoined;
                _rooms.OnRoomLeft   += HandleRoomLeft;
                _rooms.OnRoomError  += HandleRoomError;
            }

            if (_matchmaking != null)
            {
                // Only the ways it can END WITHOUT a room. Success arrives as
                // OnRoomJoined like every other entry, and subscribing to it
                // here as well would spawn twice.
                _matchmaking.OnMatchmakingFailed    -= HandleMatchmakingFailed;
                _matchmaking.OnMatchmakingTimedOut  -= HandleMatchmakingTimedOut;
                _matchmaking.OnMatchmakingCancelled -= HandleMatchmakingCancelled;
                _matchmaking.OnMatchmakingFailed    += HandleMatchmakingFailed;
                _matchmaking.OnMatchmakingTimedOut  += HandleMatchmakingTimedOut;
                _matchmaking.OnMatchmakingCancelled += HandleMatchmakingCancelled;
            }

            _subscribed = true;
        }

        // ⚠️ Idempotent, and it has to be: both exits call it, and the poll calls
        // it on the very first frame, before anything has been attached at all.
        private void Unsubscribe()
        {
            if (_manager != null)
            {
                _manager.OnStateChanged      -= HandleStateChanged;
                _manager.OnDisconnected      -= HandleDisconnected;
                _manager.OnReconnectFailed   -= HandleReconnectFailed;
                _manager.OnAutoRejoinAttempt -= HandleSdkIsRejoining;
            }

            if (_rooms != null)
            {
                _rooms.OnRoomJoined -= HandleRoomJoined;
                _rooms.OnRoomLeft   -= HandleRoomLeft;
                _rooms.OnRoomError  -= HandleRoomError;
            }

            if (_matchmaking != null)
            {
                _matchmaking.OnMatchmakingFailed    -= HandleMatchmakingFailed;
                _matchmaking.OnMatchmakingTimedOut  -= HandleMatchmakingTimedOut;
                _matchmaking.OnMatchmakingCancelled -= HandleMatchmakingCancelled;
            }

            _subscribed = false;
        }

        // ── Handlers ───────────────────────────────────────────────────────────

        // ⛔ The decision is not taken here. It is ASKED for here and taken on
        // the next Update, because the SDK has one more thing to say about this
        // very transition — whether it is entering a room itself — and it says
        // it two statements later.
        private void HandleStateChanged(NetworkState previous, NetworkState next)
        {
            if (next != NetworkState.Connected) return;

            _recoveryOffered = false;
            _sdkIsEntering   = false;
            _entryPending    = true;
        }

        // The SDK announcing its own rejoin, immediately before issuing it.
        private void HandleSdkIsRejoining(string roomId)
        {
            _sdkIsEntering = true;
            _rememberedRoomId = string.IsNullOrEmpty(roomId) ? _rememberedRoomId : roomId;
        }

        private void HandleDisconnected(DisconnectReason reason)
        {
            // The session is gone and so is everything it held. The remembered
            // room is NOT cleared: re-entering it is what the next connection is
            // for, and it is the only thing that turns a drop back into the game
            // the player was playing.
            ForgetTheSession();

            if (!TheCopyThatActs() || !_reconnectOnDrop || _manager == null) return;

            // ⛔ A departure the application asked for is not a fault and is not
            // undone. Recovering from it would make Disconnect() mean nothing.
            if (reason == DisconnectReason.ClientRequest)
            {
                // Recorded rather than merely returned on: this method is the
                // only place the reason is visible, and the resume path — which
                // sees a manager in Disconnected with no recovery in flight,
                // the same state a deliberate departure leaves — has no way to
                // ask afterwards.
                _departureWasRequested = true;
                return;
            }

            if (_recoveryOffered)
            {
                // 🔴 SILENT, and this is the correction. A second OnDisconnected
                // does NOT mean the ladder is spent: every failed ATTEMPT inside
                // it transitions Reconnecting → Disconnected, and TransitionTo
                // raises OnDisconnected on that shape — five times over, at the
                // shipped default. Reported as exhaustion, the player was told
                // four times that they were permanently offline while the SDK
                // was still trying, and told it a fifth time in the run where
                // attempt three SUCCEEDED.
                //
                // ⛔ And the true exhaustion arrives on neither: its common
                // branch is already in Disconnected, so it transitions nothing
                // and raises no OnDisconnected at all. The ladder's own outcome
                // is OnReconnectFailed, which is where this report now lives.
                return;
            }

            _recoveryOffered = true;

            // The SDK's own bounded ladder — attempts capped by NetworkSettings,
            // spaced by full-jitter backoff, refusing to stack a second loop.
            // Asked once; everything about how hard to try stays where it was.
            if (_manager.Reconnect()) return;

            // 🔑 The latch guards a ladder that is RUNNING, and the line above
            // has just established that none is: Reconnect() refused, so the
            // next OnDisconnected cannot come from inside one — it comes from
            // the fresh session below failing to authenticate, which is the
            // arrival the budget exists to spend. Left set, it swallowed that
            // arrival and every one after it: measured, three consecutive drops
            // spent ONE of three attempts and then went silent, without even the
            // report that says so. The two sibling call sites both clear it here.
            _recoveryOffered = false;
            TryAFreshSession(BootstrapReports.NoSessionToRestore(reason.ToString()),
                             reason.ToString());
        }

        /// <summary>
        /// The SDK's reconnect ladder has spent every attempt its settings allow.
        /// </summary>
        /// <remarks>
        /// 🔑 The one event that means what the old report claimed. It is raised
        /// once, after the session data is cleared, and it is suppressed when a
        /// handler has already begun a fresh attempt — so it cannot arrive about
        /// a ladder that is still running.
        /// <para>
        /// ⛔ Not gated on <c>_recoveryOffered</c>. The SDK's own auto-reconnect
        /// can run a ladder this component never asked for, and a player left
        /// offline is worth saying either way.
        /// </para>
        /// </remarks>
        private void HandleReconnectFailed(int attempts)
        {
            if (!TheCopyThatActs() || !_reconnectOnDrop) return;

            _recoveryOffered = false;
            TryAFreshSession(BootstrapReports.RecoveryExhausted(attempts),
                             "reconnect ladder exhausted after " + attempts + " attempt(s)");
        }

        /// <summary>
        /// Unity's only signal that the app was suspended and has come back.
        /// </summary>
        /// <remarks>
        /// ⛔ Nothing happens on the way out. There is no useful work to do
        /// while suspended, and the one thing an application is tempted to do —
        /// disconnect cleanly — destroys the reconnect token that is the whole
        /// reason the resume can be cheap.
        /// </remarks>
        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                _wasSuspended = true;
                return;
            }

            // ⚠️ Unity raises this with `false` during start-up on several
            // platforms, before and around Start. A resume handler that trusted
            // the argument alone would run against a component that has not
            // connected yet and open a second connection beside the one
            // Connect On Start is about to make. Only a resume that follows a
            // suspension is a resume.
            if (!_wasSuspended) return;
            _wasSuspended = false;
            RecoverAfterResume();
        }

        /// <summary>
        /// Restore the session the suspension cost, without waiting for the
        /// watchdog to discover it.
        /// </summary>
        private void RecoverAfterResume()
        {
            if (!_recoverOnResume || !_reconnectOnDrop) return;
            if (!TheCopyThatActs() || _manager == null) return;

            // 🔴 Two refusals an adversarial review found this handler missing,
            // and both made it undo a decision somebody had already taken.
            //
            // ⛔ A connection this component never opened is not one it may
            // reopen. `Connect On Start = false` is how a title defers the
            // connection behind a login screen, a terms gate or a region
            // picker — and `_manager` is bound by the per-frame poll whether or
            // not Connect() ran, so without this the first background-and-
            // resume connected every one of them.
            if (!_connectOnStart && !_everConnected) return;

            // ⛔ And a departure the application asked for is not a fault.
            // HandleDisconnected returns on DisconnectReason.ClientRequest
            // BEFORE it sets _recoveryOffered, so a deliberate Disconnect()
            // leaves exactly the two conditions below satisfied: a player who
            // logged out or returned to the menu was silently reconnected —
            // and re-entered the room — on their next resume. The comment
            // forbidding precisely that sits a hundred lines above the code
            // that did it.
            if (_departureWasRequested) return;

            // Android commonly hands the socket back intact. Probing a live
            // session would cost a reconnect nobody needed and would look, from
            // the gateway, like an address trying to displace its own session.
            if (_manager.State != NetworkState.Disconnected) return;

            // A ladder already running owns the recovery; a second ask would
            // stack a loop the SDK refuses anyway, and the refusal would be
            // reported here as a failure that is not one.
            if (_recoveryOffered) return;

            _recoveryOffered = true;
            if (_manager.Reconnect()) return;

            // 🔑 The token did not survive, and this is where the resume path
            // must part company with the drop path. A drop reports
            // NoSessionToRestore because the SDK's own ladder is the retry and
            // the session was alive moments ago; a suspension is unbounded — an
            // app backgrounded overnight has no token by construction — so
            // reporting "nothing to restore" would tell a player they are
            // offline in exactly the case a fresh connection works. The
            // remembered room is not cleared by a drop, so the new session
            // re-enters the game the player was in.
            _recoveryOffered = false;
            TryAFreshSession(BootstrapReports.NoSessionToRestore("app resumed"), "app resumed");
        }

        /// <summary>
        /// Authenticate again, within a bound, after the session could not be
        /// restored.
        /// </summary>
        /// <param name="report">What to say if this component will not try.</param>
        /// <param name="after">Why recovery failed, for the line it prints when it does try.</param>
        /// <remarks>
        /// ⛔ Bounded, and the bound is spent by *attempts* while it is returned
        /// by *arrival in a room*. A session that authenticates and then cannot
        /// enter a room has recovered nothing, and a budget that counted it as
        /// success would be a budget a broken room service could refill
        /// indefinitely.
        /// </remarks>
        private void TryAFreshSession(string report, string after)
        {
            if (!_freshSessionWhenRecoveryFails)
            {
                Fail(report);
                return;
            }

            if (_freshSessionsSpent >= _freshSessionAttempts)
            {
                Fail(BootstrapReports.FreshSessionsExhausted(_freshSessionAttempts));
                return;
            }

            _freshSessionsSpent++;
            Debug.Log(
                BootstrapReports.OpeningAFreshSession(after, _freshSessionsSpent, _freshSessionAttempts),
                this);
            Connect();
        }

        /// <summary>
        /// Let go of everything that belonged to the session that has ended.
        /// </summary>
        /// <remarks>
        /// ⛔ Reached from the drop AND from the rebind, because those are two
        /// different observations of one event and a component that was disabled
        /// only ever makes the second.
        /// </remarks>
        private void ForgetTheSession()
        {
            LocalPlayer    = null;
            _entryIssued   = false;
            _entryPending  = false;
            _sdkIsEntering = false;
            _inRoom        = false;
            _entryUsesTheRoomChannel = false;
        }

        private void HandleRoomJoined(RoomInfo room)
        {
            if (!_subscribed || !TheCopyThatActs()) return;

            _rememberedRoomId = room == null ? null : room.RoomId;
            // Arrival is what a recovery was for, so arrival is what returns the
            // budget that paid for it.
            _freshSessionsSpent = 0;
            _entryUsesTheRoomChannel = false;
            _inRoom        = true;
            _entryIssued   = true;
            _entryPending  = false;
            _sdkIsEntering = false;
            SpawnTheLocalPlayer();
        }

        private void HandleRoomLeft()
        {
            // 🚨 NOT "left on purpose", which is what this said and what made the
            // release below look harmless. RoomManager raises this for four
            // departures: a leave the application asked for, a room SWITCH — it
            // announces the old room before adopting the new one — a KICK of
            // this very client by the room's host, and a seat the SERVER
            // reclaimed because the room stopped seeing this client. All four
            // end in the same teardown, and forgetting the room is right for
            // all four: it is the difference between "reconnect me to my game"
            // and "put me back into a room I am no longer in".
            //
            // ⛔ The fourth is the one that must not be answered automatically.
            // A reclaimed seat means the room has already decided this client
            // was gone; re-entering on its own would race the reaper that took
            // it and, under CreateRoom, charge a new room against the project's
            // quota on every reclaim. Restart() is the way back in.
            //
            // ⛔ And the entry stays SPENT. Releasing it handed the poll back
            // into EnterARoom on the very next frame, so a player who left for
            // the menu was seated in a brand-new room nobody asked for — under
            // the default policy a created one, charged against the project's own
            // room quota, once per departure. Restart() is the way back in, which
            // is what its documentation has said all along.
            _rememberedRoomId = null;
            LocalPlayer  = null;
            _inRoom      = false;

            // ⛔ Said, and said as information rather than as a fault. The three
            // departures are indistinguishable here — the event carries nothing
            // — so calling a deliberate leave a failure would raise a false
            // alarm on the common case, and saying nothing left a player removed
            // by the host with a component that had quietly stopped working. The
            // programmatic channel for this is the SDK's own RoomManager.OnRoomLeft,
            // which stays public and says exactly as much as this does.
            if (WarnGate.ShouldEmit(ref _lastLeftRoomNoticeTicks))
            {
                Debug.Log(BootstrapReports.OutOfTheRoom, this);
            }
        }

        private void HandleRoomError(string reason)
        {
            if (!TheCopyThatActs()) return;

            // 🚨 ENTRY errors only. RoomManager raises this for a ListRooms that
            // drew no answer, a property write at a stale version, and a leave
            // that was refused — none of them this component's business, and
            // every one of them used to make it forget the room it was standing
            // in and tell the application it "could not enter a room".
            if (_inRoom) return;

            // ⛔ And an entry that is OURS includes the one the SDK is making on
            // our behalf. Reading only our own left the commonest reconnect
            // outcome — the room was reaped while the player was away, so the
            // SDK's rejoin is refused — reported to nobody, with the component
            // still waiting for an attempt that had already failed.
            if (!_entryUsesTheRoomChannel && !_sdkIsEntering) return;

            // ⛔ The entry is spent, and deliberately not retried: a rejoin
            // refused because the room is gone is not made truer by asking
            // again, and a client that retries a refusal nobody looked at
            // hammers a server that has already said no. Restart() is the way
            // back and it is the application's call — which is why the entry is
            // marked spent here rather than left pending, and why _sdkIsEntering
            // is released: the SDK's attempt is over, and without this the
            // component waited for it for the rest of the session.
            _rememberedRoomId = null;
            _sdkIsEntering    = false;
            _entryPending     = false;
            _entryIssued      = true;
            _entryUsesTheRoomChannel = false;
            Fail(BootstrapReports.RoomRefused(reason));
        }

        private void HandleMatchmakingFailed(string reason)  => MatchmakingEnded("failed: " + reason);
        private void HandleMatchmakingTimedOut()             => MatchmakingEnded("timed out");
        private void HandleMatchmakingCancelled()            => MatchmakingEnded("was cancelled");

        // ⛔ Reported by the copy that acts, and by that one only. A passive
        // duplicate is subscribed to the same facades — it has to be, so it can
        // take the work over — and without this gate one refusal reached the
        // application twice and the console twice, from a component that had
        // issued nothing.
        private void MatchmakingEnded(string outcome)
        {
            if (!TheCopyThatActs()) return;

            Fail(BootstrapReports.MatchmakingEnded(outcome));
        }

        // ── The two things it does ─────────────────────────────────────────────

        private void EnterARoom()
        {
            if (_manager == null || _rooms == null) return;
            if (_entryIssued || !TheCopyThatActs()) return;

            // The SDK's own rejoin, taken as a FACT rather than predicted: it
            // announces one immediately before issuing it, and this component's
            // Update runs after the manager's. Issuing a policy alongside it
            // would put a second room operation into one session — and with
            // CreateRoom that is a new, empty room taken instead of the one the
            // player was in.
            var action = ConnectionBootstrapOps.DecideEntry(
                _entryPolicy, _rejoinLastRoom, _rememberedRoomId, _sdkIsEntering);

            if (action == EntryAction.WaitForTheSdk) return;

            // ⛔ The configuration is judged HERE as well as at Connect, and the
            // reason is that Connect is one of three doors into this method and
            // the only one that passed through it: the poll enters for a
            // component enabled after the session came up, and Restart() enters
            // on an application's call. A policy the SDK refuses by THROWING
            // reached Update through both — said here it is a sentence a
            // developer reads, and unsaid it is an ArgumentException out of a
            // Unity lifecycle callback, which is the exact fault this component
            // exists to prevent. A rejoin is exempt: it carries an id the server
            // issued and none of the configured fields.
            if (action != EntryAction.Rejoin && RefusedByItsOwnSettings())
            {
                _entryIssued  = true;
                _entryPending = false;
                return;
            }

            _entryIssued  = true;
            _entryPending = false;
            _entryUsesTheRoomChannel = action != EntryAction.Matchmake;

            // ⚠️ The two facades refuse differently and only one of them returns:
            // RoomManager logs and carries on, MatchmakingManager THROWS —
            // ArgumentException for a field it will not send, InvalidOperationException
            // for a request overlapping one already in flight, which is what a
            // second Restart() issues while the first is still unanswered. A
            // refusal is news for the application either way, and it is never an
            // exception out of Update or out of a method the application called.
            try
            {
                switch (action)
                {
                    case EntryAction.Rejoin:
                        _rooms.JoinRoom(_rememberedRoomId);
                        break;

                    case EntryAction.Join:
                        _rooms.JoinRoom(_roomId);
                        break;

                    case EntryAction.Matchmake:
                        if (_matchmaking == null) { Fail(BootstrapReports.NoMatchmaking); return; }
                        _matchmaking.StartMatchmaking(new MatchmakingOptions
                        {
                            Mode       = _matchmakingMode,
                            MaxPlayers = _maxPlayers,
                        });
                        break;

                    default:
                        _rooms.CreateRoom(new CreateRoomOptions
                        {
                            Name           = _roomName,
                            MaxPlayers     = _maxPlayers,
                            AutoJoinAsHost = true,
                        });
                        break;
                }
            }
            catch (ArgumentException refused)
            {
                Fail(BootstrapReports.RoomRefused(refused.Message));
            }
            catch (InvalidOperationException refused)
            {
                Fail(BootstrapReports.RoomRefused(refused.Message));
            }
        }

        /// <summary>
        /// Say what this configuration cannot do, once, and answer whether
        /// anything had to be said.
        /// </summary>
        /// <remarks>
        /// 🔑 One statement, reached from every door, because a rule stated at
        /// one call site is a rule about that call site. This one was written
        /// into Connect alone and the entry poll walked straight past it.
        /// </remarks>
        private bool RefusedByItsOwnSettings()
        {
            string unusable = BootstrapReports.Unusable(ConnectionBootstrapOps.Refusals(
                _entryPolicy, _roomName, _roomId, _matchmakingMode, _maxPlayers));
            if (unusable == null) return false;

            Fail(unusable);
            return true;
        }

        // ⚠️ The gate is a second one: HandleRoomJoined is this method's only
        // caller and gates already, so today the two cannot disagree. It stays
        // because "only the copy that acts spawns" is a property of the SPAWN
        // and not of the one path that currently reaches it.
        private void SpawnTheLocalPlayer()
        {
            if (!TheCopyThatActs()) return;

            var prefab = _playerPrefab;
            if (ChoosePlayerPrefab != null && !Ask(ChoosePlayerPrefab, nameof(ChoosePlayerPrefab), out prefab))
            {
                return;
            }

            if (prefab == null)
            {
                if (WarnGate.ShouldEmit(ref _lastNothingToSpawnWarnTicks))
                {
                    Debug.Log(BootstrapReports.NothingToSpawn, this);
                }

                return;
            }

            // Unity's equality, deliberately: a destroyed avatar is a live C#
            // reference and a dead engine object, and only the engine's operator
            // tells them apart.
            var decision = ConnectionBootstrapOps.DecideLocalPlayer(
                LocalPlayer != null, LocalPlayer != null && LocalPlayer.IsSpawned);

            if (decision == LocalPlayerAction.Keep) return;

            if (decision == LocalPlayerAction.ReplaceStale)
            {
                Debug.LogWarning(BootstrapReports.ReplacedStaleAvatar(LocalPlayer.name), this);
                Destroy(LocalPlayer.gameObject);
                LocalPlayer = null;
            }

            if (!_manager.Spawner.TryGetPrefabId(prefab, out uint prefabId))
            {
                Fail(BootstrapReports.PrefabHasNoId(prefab.name));
                return;
            }

            var pose = new Pose(transform.position, transform.rotation);
            if (ChooseSpawnPose != null && !Ask(ChooseSpawnPose, nameof(ChooseSpawnPose), out pose))
            {
                return;
            }

            // ⛔ A registered id is not a spawnable prefab: the session builds
            // the object and answers null when it carries no NetworkBehaviour to
            // drive it. The SDK names the id in the console; what was missing is
            // that the flow stopped here and the application was never told.
            LocalPlayer = _manager.Spawner.Spawn(prefabId, pose.position, pose.rotation);
            if (LocalPlayer == null)
            {
                Fail(BootstrapReports.SpawnRefused(prefab.name));
                return;
            }

            Raise(OnLocalPlayerSpawned, LocalPlayer, nameof(OnLocalPlayerSpawned));
        }

        private void Fail(string reason)
        {
            Debug.LogError(reason, this);
            Raise(OnBootstrapFailed, reason, nameof(OnBootstrapFailed));
        }

        // 🚨 The application's code is called from inside Unity's callbacks, and
        // until 2026-09-07 it was called BARE — so a subscriber that threw took
        // the exception out through Fail, out of RefusedByItsOwnSettings, and out
        // of Update, Connect or the application's own Restart(). Every SDK type
        // this component wraps already refuses to do that: RoomManager,
        // MatchmakingManager and NetworkManager each isolate a subscriber in
        // SafeRaise. The one component the SDK ships that raises events of its
        // own was the exception to its own rule.
        //
        // ⛔ Reported and swallowed, never rethrown: a handler's fault is the
        // handler's, and letting it end the entry flow would make one bad
        // subscriber cost the player their game.
        private void Raise<T>(Action<T> subscribers, T argument, string what)
        {
            if (subscribers == null) return;

            try
            {
                subscribers(argument);
            }
            catch (Exception thrown)
            {
                Debug.LogError(
                    "[RTMPE] A subscriber to RtmpeConnectionBootstrap." + what
                    + " threw, and the exception was contained here so the entry flow could "
                    + "continue: " + thrown, this);
            }
        }

        // The same isolation for the two delegates an application supplies. A
        // ChoosePlayerPrefab that threw left the component in a room, with the
        // entry spent, holding no avatar and saying nothing — for ever.
        private bool Ask<T>(Func<T> chosen, string what, out T answer)
        {
            answer = default;
            if (chosen == null) return false;

            try
            {
                answer = chosen();
                return true;
            }
            catch (Exception thrown)
            {
                Fail("[RTMPE] RtmpeConnectionBootstrap." + what + " threw, so it has no answer to "
                     + "act on and this client has no avatar: " + thrown);
                return false;
            }
        }
    }
}
