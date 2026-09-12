// RTMPE SDK — Runtime/Rooms/NetworkSceneManager.cs
//
// High-level scene-synchronisation façade that piggybacks on the Custom
// Properties channel introduced in Phase 1.  The authoritative scene name
// lives in the room's `custom_properties["__scene"]` reserved key; the host
// drives changes via RoomPropertyUpdate (0x24) and every other client
// receives the change as a normal property-update broadcast — which gives
// late-joiners free synchronisation for no extra migration cost.  The load
// mode travels beside the name in `custom_properties["__scene_additive"]`,
// written by the same call and read back from the same snapshot.
//
// Scene-load readiness is reported separately via the SceneLoaded (0x2F)
// packet.  The Room Service aggregates reports and emits
// `all_players_scene_loaded` once the last player is ready; the SDK
// surfaces that as OnAllPlayersSceneLoaded.

using System;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Rooms
{
    /// <summary>
    /// How a networked scene should be loaded on every client.  Mirrors
    /// <c>UnityEngine.SceneManagement.LoadSceneMode</c> so application code
    /// does not need to translate values.  Kept as a separate enum so this
    /// file never takes a hard dependency on <c>UnityEngine.SceneManagement</c>.
    /// </summary>
    public enum NetworkSceneLoadMode
    {
        /// <summary>Replace any currently loaded scene with the new one.</summary>
        Single = 0,

        /// <summary>Load the new scene alongside the currently loaded scene(s).</summary>
        Additive = 1,
    }

    /// <summary>
    /// Façade that drives networked scene loading from the master client and
    /// surfaces scene-transition events to every client in the room.
    ///
    /// The manager is stateless with respect to Unity's
    /// <c>SceneManagement</c> API — it only signals what scene should be
    /// active.  The application is responsible for calling
    /// <c>SceneManager.LoadSceneAsync</c> (or equivalent) in response to
    /// <see cref="OnSceneLoadStarted"/> and for calling
    /// <see cref="ReportReady"/> once the local scene has finished loading.
    ///
    /// Access via <see cref="NetworkManager.Scene"/>.
    /// </summary>
    public sealed class NetworkSceneManager
    {
        // Reconnect-safe: resolved each time the RoomManager identity is
        // checked (room-event bridging, property writes, scene-prune
        // requests) so a Reconnect-driven swap of the underlying RoomManager
        // is observed without dropping the long-lived public façade
        // reference held by the application.
        private readonly Func<RoomManager> _roomsProvider;

        // Most recently observed RoomManager.  Used to detect identity
        // changes so subscriptions are migrated atomically: unsubscribe from
        // the old, subscribe to the new, all under the main-thread contract.
        // Holds the dead instance across the very brief window between
        // Reconnect's call to RecreateRoomAndSpawnManagers and the next
        // method invocation that triggers EnsureBound().
        private RoomManager _bound;

        private bool _disposed;

        // Monotonic clock, injectable so a test can reach the deadline without
        // spending it — the same seam RoomManager and LobbyManager keep for
        // theirs.  Production passes nothing and gets Stopwatch.
        private readonly Func<long> _nowTicks;

        // Whether a load is outstanding, and the instant it stops being given
        // the benefit of the doubt.  The two are kept apart rather than folding
        // "not waiting" into a sentinel instant: a deadline is an arbitrary
        // point on a monotonic clock, and any value chosen to mean "none" is a
        // value some clock will legitimately produce.
        private bool _awaitingReady;
        private long _readyDeadlineTicks;

        // What the outstanding load is for, and whether this client has sent its
        // own report for it.  The second is the one thing about the room's
        // silence this side can actually establish, so it is carried into the
        // diagnostic rather than guessed at there.
        private string _awaitedScene = string.Empty;
        private bool   _localReportSent;

        /// <summary>
        /// All-loaded broadcasts refused because they named the scene this
        /// client was still loading and could not therefore describe a round
        /// this client is in.
        /// </summary>
        /// <remarks>
        /// 🔑 Counted rather than logged: it is the ordinary consequence of a
        /// reload — the superseded round's broadcast is in flight while the new
        /// one arms — so a line per occurrence would be noise on a correct
        /// path.  A refusal that never fires and a refusal that was deleted
        /// look identical without it.
        /// </remarks>
        internal int StaleAllReadyIgnored => _diagStaleAllReadyIgnored;

        private int _diagStaleAllReadyIgnored;

        // The budget the outstanding deadline was computed from.  Read back for
        // the diagnostic instead of the property, which the application may have
        // changed since — a line naming a duration that did not elapse is worse
        // than one that names none.
        private float _armedBudgetSeconds;

        /// <summary>
        /// How long a networked scene load may go unsettled before
        /// <see cref="OnSceneLoadTimedOut"/> is raised, in seconds.  Zero or
        /// negative disables the report entirely.
        /// <para>
        /// ⚠️ <c>NetworkSettings.sceneReadyTimeoutSeconds</c> reads zero
        /// differently — there it means "not configured" and resolves to
        /// <see cref="DefaultSceneReadyTimeoutSeconds"/>, because zero is what a
        /// serialized field returns on an asset written before it existed.  This
        /// property is a live value with no such history, so zero here is the
        /// value it says it is.
        /// </para>
        /// </summary>
        /// <remarks>
        /// A room reaches all-loaded only when the server has heard from every
        /// seat, so a single client that never reports — one that crashed, or
        /// whose application never calls <see cref="ReportReady"/> — leaves the
        /// whole room waiting with nothing said.  The budget is generous
        /// because it is measured against a scene load rather than a round
        /// trip; what it buys is that the wait ends in a report instead of in
        /// silence.
        /// <para>
        /// ⛔ Switching it off costs more than the report.  A broadcast naming a
        /// scene this client has been told to load, and has not reported, is
        /// refused — it belongs to a round this client is not in — and the
        /// deadline expiring is what ends that refusal.  With no deadline, an
        /// application that never calls <see cref="ReportReady"/> receives
        /// <see cref="OnSceneLoadStarted"/> and then nothing at all, for the
        /// life of the room, with nothing said. Leave the budget on unless the
        /// application reports.
        /// </para>
        /// </remarks>
        public float SceneReadyTimeoutSeconds { get; set; } = DefaultSceneReadyTimeoutSeconds;

        /// <summary>Default value of <see cref="SceneReadyTimeoutSeconds"/>.</summary>
        public const float DefaultSceneReadyTimeoutSeconds = 60f;

        /// <summary>
        /// The budget a configured value asks for, resolved against the default.
        /// Zero means "not configured"; any other value is taken as written, so
        /// a negative switches the report off.
        /// </summary>
        /// <remarks>
        /// ⛔ Zero cannot mean "off".  A serialized field added to an existing
        /// asset reads back as zero — that is what every project created before
        /// this setting existed will supply — so an off-switch spelled zero
        /// would ship the report dark on precisely the installed base it was
        /// written for, with an Inspector value that looks deliberate.  Nothing
        /// produces a negative by omission, which is what makes it usable as the
        /// off switch.
        /// </remarks>
        internal static float ResolveConfiguredTimeout(float configured)
            => configured == 0f ? DefaultSceneReadyTimeoutSeconds : configured;

        /// <summary>
        /// Fired when the server has accepted a write of the room's scene name
        /// and every client in the room should begin loading it.  Argument is
        /// the scene name carried by the <see cref="ReservedPropertyKeys.Scene"/>
        /// property.  Fires on every client, including the master that
        /// initiated the change.
        /// <para>
        /// ⚠️ Every accepted write, not every changed value: a write naming the
        /// scene the room is already on is a round restart, and it fires here
        /// like any other.  A handler that reloads unconditionally will reload.
        /// </para>
        /// </summary>
        public event Action<string> OnSceneLoadStarted;

        /// <summary>
        /// <see cref="OnSceneLoadStarted"/> with the room's load mode attached.
        /// Fires on the same occasions and with the same scene name; the second
        /// argument is the mode the room is on, which is what
        /// <see cref="LoadScene"/> wrote alongside the name.  Prefer this event
        /// — an additive load applied as a single one unloads the scene the
        /// room was still in.
        /// </summary>
        /// <remarks>
        /// A second event rather than a wider one: <see cref="OnSceneLoadStarted"/>
        /// is shipped, and widening a delegate breaks every assembly compiled
        /// against it.  Both are raised, in that order, and a subscriber that
        /// throws does not deny the other its turn.
        /// <para>
        /// The mode is the room's authoritative <c>__scene_additive</c>, taken
        /// from the same snapshot on every path — the write, and the late join.
        /// <see cref="LoadScene"/> writes the pair together, so they cannot
        /// drift; a caller writing <c>__scene</c> through
        /// <see cref="RoomManager.SetRoomProperties"/> by hand owns the pair
        /// itself, and leaving the flag behind leaves the room on the mode the
        /// previous load set.
        /// </para>
        /// </remarks>
        public event Action<string, NetworkSceneLoadMode> OnSceneLoadStartedWithMode;

        /// <summary>
        /// Fired when every client has reported local scene-load completion
        /// for the same scene.  Argument is the scene name.  Application
        /// code typically waits for this event before starting the match.
        /// </summary>
        public event Action<string> OnAllPlayersSceneLoaded;

        /// <summary>
        /// Fired when <see cref="SceneReadyTimeoutSeconds"/> elapses after a
        /// load began without <see cref="OnAllPlayersSceneLoaded"/> arriving.
        /// Argument is the scene name the room was loading.  Raised once per
        /// load; a load that settles late still raises
        /// <see cref="OnAllPlayersSceneLoaded"/> when it does.
        /// </summary>
        /// <remarks>
        /// ⛔ The event reports that the room did not settle.  It does not name
        /// the player responsible and cannot: readiness is aggregated by the
        /// server, which broadcasts only the completed rendezvous, so no client
        /// is told who is outstanding — including whether it is itself.
        /// <para>
        /// ⛔ Nor does it stand in for <see cref="OnAllPlayersSceneLoaded"/>.
        /// Each client's budget starts when its own load began, so a room whose
        /// members each began the match on their own timeout would be split
        /// between players who started and players who did not — a worse
        /// outcome than the wait, because the wait is visible.  What to do
        /// about the delay is the application's, and it is the only party that
        /// can decide it.
        /// </para>
        /// </remarks>
        public event Action<string> OnSceneLoadTimedOut;

        /// <summary>
        /// The authoritative scene name currently loaded by the room, or
        /// empty string when no scene has been set yet.  Mirrors
        /// <see cref="RoomInfo.CurrentScene"/> for convenience.
        /// </summary>
        public string CurrentScene
        {
            get
            {
                EnsureBound();
                return _bound?.CurrentRoom?.CurrentScene ?? string.Empty;
            }
        }

        /// <summary>
        /// How the room's authoritative scene is to be loaded, read from the
        /// reserved <see cref="ReservedPropertyKeys.SceneAdditive"/> property.
        /// <see cref="NetworkSceneLoadMode.Single"/> when the room has not set
        /// it, which is what the key's absence means.
        /// </summary>
        public NetworkSceneLoadMode CurrentSceneLoadMode
        {
            get
            {
                EnsureBound();
                return ModeOf(_bound?.CurrentRoom);
            }
        }

        // The room's load mode, from the room's own map.  Every announcement
        // reads it through here — the write path and the late join alike — so
        // the two cannot come to disagree about what the same room is doing.
        private static NetworkSceneLoadMode ModeOf(RoomInfo room)
        {
            var properties = room?.Properties;
            if (properties != null
                && properties.TryGetValue(ReservedPropertyKeys.SceneAdditive, out var flag)
                && flag.Type == PropertyType.Bool
                && flag.AsBool())
            {
                return NetworkSceneLoadMode.Additive;
            }
            return NetworkSceneLoadMode.Single;
        }

        // Provider-based ctor (preferred, reconnect-safe).
        internal NetworkSceneManager(Func<RoomManager> roomsProvider, Func<long> nowTicks = null)
        {
            _roomsProvider = roomsProvider ?? throw new ArgumentNullException(nameof(roomsProvider));
            _nowTicks      = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            EnsureBound();
        }

        // Legacy ctor retained for back-compat with tests / out-of-tree
        // callers that constructed with a fixed RoomManager.  The fixed
        // reference is wrapped in a constant provider so the rest of the
        // class can route exclusively through EnsureBound().
        internal NetworkSceneManager(RoomManager rooms, Func<long> nowTicks = null)
            : this(ConstantProvider(rooms), nowTicks)
        {
        }

        // The argument check belongs ahead of the constructor it chains to, and
        // an expression is the only thing that runs there.
        private static Func<RoomManager> ConstantProvider(RoomManager rooms)
        {
            if (rooms == null) throw new ArgumentNullException(nameof(rooms));
            return () => rooms;
        }

        /// <summary>
        /// Instruct the server (via the Custom Properties pipeline) that the
        /// room should transition to <paramref name="sceneName"/>.  Only the
        /// master client may call this.
        /// <para>
        /// ⚠️ A caller who is not in a room, or who is not the master client,
        /// gets an <see cref="InvalidOperationException"/> — it is refused HERE,
        /// before anything reaches the server, so the bug is surfaced to the
        /// developer rather than producing a silent server-side no-op. Gate the
        /// call, or catch it: a button wired straight to this throws the first
        /// time anybody presses it before joining. (This paragraph said "log an
        /// error and return immediately" until 2026-09-05, and the code has
        /// thrown throughout; the sample built on that sentence threw out of
        /// OnGUI.)
        /// </para>
        /// </summary>
        /// <param name="sceneName">Scene name or path, as passed to
        /// <c>SceneManager.LoadSceneAsync</c>.  Must not be null or empty.</param>
        /// <param name="mode">Load mode.  <see cref="NetworkSceneLoadMode.Single"/>
        /// is the default and the most common choice.</param>
        public void LoadScene(string sceneName, NetworkSceneLoadMode mode = NetworkSceneLoadMode.Single)
        {
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentException("sceneName must not be null or empty.", nameof(sceneName));

            EnsureBound();
            var rooms = _bound;
            // Surface state-order violations as InvalidOperationException so
            // a caller that runs LoadScene before joining a room (or as a
            // non-master) gets a stack trace pointing at the misuse rather
            // than a silent log line that they may not see in a CI run or
            // a release-mode build with logs filtered.  The ArgumentException
            // for a null or empty name, a few lines above, already established
            // the throw-on-misuse contract for this method; the state checks
            // mirror it.
            if (rooms == null || !rooms.IsInRoom)
                throw new InvalidOperationException(
                    "NetworkSceneManager.LoadScene: caller must be joined to a room.");
            // Only the master client may instruct the room to change scene.
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsMasterClient)
                throw new InvalidOperationException(
                    "NetworkSceneManager.LoadScene: only the master client may change the scene.");

            // Robustness: prune any NetworkObjects whose GameObjects were
            // destroyed by an out-of-band scene unload BEFORE the server
            // broadcast lands.  Without this, a transitional gap can leave
            // the registry holding entries that compare equal to null when
            // the new scene's RegisterPrefab/Spawn cycle runs, producing
            // silent ID collisions if the gateway re-uses an id near the
            // wrap.  The host's sceneUnloaded handler already prunes; this
            // is a second, defensive sweep tied to the network-driven
            // transition rather than the engine's local unload event.
            var nm = NetworkManager.Instance;
            try { nm?.Spawner?.Registry?.PruneDestroyed(); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkSceneManager.LoadScene: registry prune threw " +
                    $"{ex.GetType().Name}: {ex.Message}.  Continuing — gateway broadcast still proceeds.");
            }

            var updates = new System.Collections.Generic.Dictionary<string, PropertyValue>
            {
                { ReservedPropertyKeys.Scene,         PropertyValue.OfString(sceneName) },
                { ReservedPropertyKeys.SceneAdditive, PropertyValue.OfBool(mode == NetworkSceneLoadMode.Additive) },
            };
            rooms.SetRoomProperties(updates);
        }

        /// <summary>
        /// Report to the server that the local client has finished loading
        /// the scene identified by <see cref="CurrentScene"/>.  No-op when
        /// not in a room or when no scene has been set.
        /// </summary>
        public void ReportReady()
        {
            EnsureBound();
            var scene = CurrentScene;
            if (string.IsNullOrEmpty(scene)) return;
            // The latch is set by the room manager's own signal rather than
            // here: this class is not the only way a report reaches the wire,
            // and a latch only this path sets is one an application using the
            // room API directly leaves false while the server holds its report.
            if (_bound != null) _bound.TryReportSceneLoaded(scene);
        }

        // ── The outstanding load ──────────────────────────────────────────

        /// <summary>
        /// Advance the outstanding load's deadline.  Called once per frame by
        /// <see cref="NetworkManager"/>.  While nothing is loading it costs the
        /// rebind check below and returns; it is not otherwise a no-op.
        /// </summary>
        /// <remarks>
        /// The deadline is read here and nowhere else on purpose.  Reading it
        /// while handling a broadcast would put it on the path the case it
        /// exists for never takes: a room that never settles receives no
        /// broadcast at all.
        /// </remarks>
        internal void Tick()
        {
            if (_disposed) return;

            // Ahead of the deadline, not after it. A reconnect replaces the
            // RoomManager and this class binds to whichever one is live, so a
            // budget left over from the session that ended would otherwise
            // expire here and report a wait that belongs to nobody. The check
            // is a delegate call and a reference compare in the steady state.
            EnsureBound();

            if (!_awaitingReady) return;
            if (_nowTicks() < _readyDeadlineTicks) return;

            string scene    = _awaitedScene;
            bool   reported = _localReportSent;
            float  budget   = _armedBudgetSeconds;
            int    seats    = RosterSize();

            // The debt is cleared before it is reported.  A subscriber is
            // entitled to start another load from inside the handler, and one
            // that does would otherwise have its fresh deadline overwritten by
            // the bookkeeping of the load it just replaced.
            Disarm();

            Debug.LogWarning(
                $"[RTMPE] NetworkSceneManager: scene '{scene}' has been loading for " +
                $"{budget:0.#}s without every player reporting ready. " +
                $"This client {(reported ? "has sent" : "has NOT sent")} its own report" +
                $"{(seats > 0 ? $"; the room seats {seats} player(s)" : string.Empty)}. " +
                "Which player is outstanding is known only to the server.");

            Raise(OnSceneLoadTimedOut, scene, nameof(OnSceneLoadTimedOut));
        }

        // Every event this class raises goes through one of these, and each
        // subscriber is given its own attempt.
        //
        // Two different failures are being refused, one per raise site.  The
        // timeout is raised from Update, which has no catch above it, so a
        // throw there abandons the rest of the frame's ticks.  The scene-load
        // pair is raised from inside a RoomManager handler, which does catch —
        // but it catches the whole handler, so one throwing subscriber would
        // take the other event with it, and which of the two survived would
        // depend on nothing but the order they are written in below.
        private static void Raise<T>(Action<T> handler, T arg, string eventName)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action<T>)subs[i])(arg); }
                catch (Exception ex) { ReportSubscriberThrow(eventName, ex); }
            }
        }

        private static void Raise<T1, T2>(Action<T1, T2> handler, T1 first, T2 second, string eventName)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action<T1, T2>)subs[i])(first, second); }
                catch (Exception ex) { ReportSubscriberThrow(eventName, ex); }
            }
        }

        // Gated, and the path decides that rather than the severity: three of
        // the four events this class raises are raised while handling an
        // inbound broadcast, so a subscriber that throws throws once per packet
        // somebody else chose to send.  The fourth is the timeout, raised from
        // Tick, which is paced by the caller rather than by the wire.
        //
        // One budget across all four. They report a single fault — a handler of
        // this class threw — and the line names which event it was, so a second
        // budget would buy a distinction nobody is drawing.
        private static long _lastSubscriberThrowWarnTicks;

        internal static void ResetSubscriberThrowGateForTest()
            => System.Threading.Interlocked.Exchange(ref _lastSubscriberThrowWarnTicks, 0);

        private static void ReportSubscriberThrow(string eventName, Exception ex)
        {
            if (!WarnGate.ShouldEmit(ref _lastSubscriberThrowWarnTicks)) return;
            Debug.LogError(
                $"[RTMPE] NetworkSceneManager: an {eventName} subscriber threw " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // The room has been told to load a scene.  Both events carry it — the
        // one that has always existed, and the one that also carries the mode.
        //
        // ⛔ The second raise is re-checked against the room the first was made
        // for.  A subscriber may leave, join another room or end the session
        // from inside its own handler — synchronously, before the call returns —
        // and the pair would then describe two different rooms: the same
        // reasoning RoomManager applies before raising the scene write, one
        // layer down.  Announcing a load to a room the client is no longer in
        // is an instruction nobody can carry out.
        private void AnnounceSceneLoad(string sceneName, NetworkSceneLoadMode mode)
        {
            string announcedFor = _bound?.CurrentRoom?.RoomId;

            Raise(OnSceneLoadStarted, sceneName, nameof(OnSceneLoadStarted));

            if (_disposed || _bound?.CurrentRoom?.RoomId != announcedFor) return;

            Raise(OnSceneLoadStartedWithMode, sceneName, mode, nameof(OnSceneLoadStartedWithMode));
        }

        // Begin giving a load the benefit of the doubt.  Replaces any budget
        // still outstanding: the room has moved on, and a deadline for a scene
        // nobody is loading any more would report against the wrong one.
        private void Arm(string sceneName)
        {
            _awaitedScene    = sceneName;
            _localReportSent = false;

            // NaN fails the comparison and switches the deadline off, which is
            // the right answer for a budget that is not a duration.
            float budget = SceneReadyTimeoutSeconds;
            _awaitingReady = budget > 0f;
            if (!_awaitingReady) return;

            // The property is public and takes any float, so the instant is
            // computed in double and saturated rather than cast blind: a value
            // that overflows the conversion yields an unspecified long, and the
            // one x64 actually produces is negative — a budget of infinity would
            // expire on the frame it was set.  Saturating means an absurd budget
            // reads as "not soon", which is what it says.
            _armedBudgetSeconds = budget;

            long   now   = _nowTicks();
            double ahead = (double)budget * System.Diagnostics.Stopwatch.Frequency;
            _readyDeadlineTicks = ahead >= (double)(long.MaxValue - now)
                ? long.MaxValue
                : now + (long)ahead;
        }

        /// <summary>
        /// Retire the outstanding load without disposing the manager.  Called
        /// from the session-teardown chokepoint alongside every other
        /// session-scoped watch.
        /// </summary>
        /// <remarks>
        /// A disconnect does not replace the <see cref="RoomManager"/> and does
        /// not raise <c>OnRoomLeft</c>, so neither the rebind nor the room-event
        /// path retires this — and the MonoBehaviour survives, so the frame loop
        /// keeps reading the deadline.  Left alone it reports a room that did
        /// not settle, about a session that has already ended.
        /// </remarks>
        internal void ClearState() => Disarm();

        private void Disarm()
        {
            _awaitingReady = false;
            // ⛔ The two below are load-bearing, and were not always: a stale
            // `_awaitedScene` surviving this method leaves the manager awaiting
            // a load nothing is timing, and HandleAllReady then refuses the
            // broadcast for it — including the one a room that settles LATE
            // eventually sends, which OnSceneLoadTimedOut's own documentation
            // promises still reaches the application.  Clearing them is what
            // ends the wait; the flag alone only ends the timing.
            _awaitedScene    = string.Empty;
            _localReportSent = false;
        }

        // The seat count is a convenience for the diagnostic, never a premise:
        // a manager that cannot reach its room still has a timeout to report.
        private int RosterSize()
        {
            var players = _bound?.CurrentRoom?.Players;
            return players?.Length ?? 0;
        }

        // ── Subscription management ───────────────────────────────────────

        // Re-bind subscriptions if the live RoomManager is no longer the one
        // we last subscribed to.  Called from every public/event entry
        // point so a Reconnect that swaps the manager between calls is
        // observed at the next interaction.  Idempotent — when the live
        // instance equals _bound (steady state) the method returns
        // immediately without touching the event delegates.
        private void EnsureBound()
        {
            if (_disposed) return;
            var live = _roomsProvider();
            if (ReferenceEquals(live, _bound)) return;

            // Detach from the previous instance — even if it has been
            // discarded by NetworkManager, our delegates are still rooted
            // in its event invocation list.  Without explicit detach the
            // dead instance leaks until full GC sweeps both objects.
            if (_bound != null)
            {
                _bound.OnRoomSceneWritten      -= HandleSceneWritten;
                _bound.OnSceneReportSent       -= HandleSceneReportSent;
                _bound.OnRoomJoined            -= HandleRoomJoined;
                _bound.OnRoomLeft              -= HandleRoomLeft;
                _bound.OnAllPlayersSceneLoaded -= HandleAllReady;
            }

            _bound = live;
            // A load outstanding against the previous manager is not one the
            // replacement is party to: its roster, its readiness and its
            // broadcasts all start again.  Reporting the old budget against the
            // new session would name a wait nobody is having.
            Disarm();

            if (_bound != null)
            {
                // Detach from the live instance before attaching to it.  A
                // replacement RoomManager takes over its predecessor's
                // subscriber lists, and three of these five handlers are in
                // them — so binding without this leaves two copies of each, and
                // HandleRoomJoined raises OnSceneLoadStarted once per copy.
                // (The other two, the scene write and the report signal, are
                // deliberately outside adoption; both are detached here for the
                // same reason the others are, and because an exemption that has
                // to be remembered is one somebody will forget.)  Removing a
                // handler that is not
                // registered is a no-op, so this costs nothing on the first
                // bind.
                _bound.OnRoomSceneWritten      -= HandleSceneWritten;
                _bound.OnSceneReportSent       -= HandleSceneReportSent;
                _bound.OnRoomJoined            -= HandleRoomJoined;
                _bound.OnRoomLeft              -= HandleRoomLeft;
                _bound.OnAllPlayersSceneLoaded -= HandleAllReady;

                _bound.OnRoomSceneWritten      += HandleSceneWritten;
                _bound.OnSceneReportSent       += HandleSceneReportSent;
                _bound.OnRoomJoined            += HandleRoomJoined;
                _bound.OnRoomLeft              += HandleRoomLeft;
                _bound.OnAllPlayersSceneLoaded += HandleAllReady;
            }
        }

        // ── Room-event bridging ──────────────────────────────────────────

        // A property broadcast whose delta named the scene key.  Every such
        // write is an instruction to load, including one naming the scene the
        // room is already on: that is a round restart, a rematch, a respawn
        // into the same map, and the room has just been told to do it again.
        //
        // ⛔ Nothing here compares the name against the last one seen.  A guard
        // that did would swallow exactly those loads, which is what this class
        // did until the write became observable as a write; the question that
        // guard was really asking — has this room already been announced to this
        // client — is answered by the broadcast's own version, which
        // RoomManager refuses at or below the one the room entry carried.
        private void HandleSceneWritten(RoomInfo room)
        {
            if (room == null) return;
            var scene = room.CurrentScene;
            // The write removed the scene, or wrote something that is not one.
            // Either way the room is no longer loading, and a deadline still
            // outstanding is a wait nobody is having.
            if (string.IsNullOrEmpty(scene))
            {
                Disarm();
                return;
            }
            Arm(scene);
            AnnounceSceneLoad(scene, ModeOf(room));
        }

        /// <summary>
        /// A readiness report for <paramref name="sceneName"/> reached the wire.
        /// </summary>
        /// <remarks>
        /// The latch is what separates a broadcast about this client's round
        /// from one still in flight for the load it replaced: a rendezvous
        /// completes only when every seat has reported, and this client is a
        /// seat.  Only a report for the load being AWAITED counts — one for a
        /// scene the room has moved past belongs to a round that is over.
        /// <para>
        /// 🚨 Awaited, not timed, and the distinction is the whole of it.  The
        /// deadline is a diagnostic an application may switch off; whether this
        /// client has reported is a fact about the rendezvous, and the two are
        /// independent.  Reading the deadline's flag here left the latch
        /// unreachable for anyone who turned the deadline off — the scene was
        /// still awaited, so the refusal below still applied, and every
        /// all-loaded broadcast for that scene was dropped for the life of the
        /// room, with no deadline left to report it either.
        /// </para>
        /// </remarks>
        private void HandleSceneReportSent(string sceneName)
        {
            if (sceneName == _awaitedScene) _localReportSent = true;
        }

        private void HandleRoomJoined(RoomInfo room)
        {
            if (room == null) return;
            // Late-join path: if the room already has an authoritative scene,
            // announce it immediately so the client catches up.
            var scene = room.CurrentScene;
            if (string.IsNullOrEmpty(scene))
            {
                Disarm();
                return;
            }
            // ⛔ Deliberately not armed.  This branch fires because the room
            // already had a scene when this client arrived, and the server's
            // rendezvous for it may have completed long ago — a round is
            // retired when it fires, so the incumbents will never report again
            // and no all-loaded broadcast is coming for this client whatever
            // happens.  Arming here would turn "I joined late" into a report
            // that the room did not settle, on every join and every reconnect,
            // which is how an integrator learns to stop listening to it.
            //
            // The load itself is still announced: the joiner must load the
            // scene, and a room genuinely stuck mid-rendezvous is reported by
            // the peers that were present when it started — they are the ones
            // armed, by the scene write above.
            Disarm();
            AnnounceSceneLoad(scene, ModeOf(room));
        }

        private void HandleRoomLeft()
        {
            Disarm();
        }

        private void HandleAllReady(string sceneName)
        {
            // Only the load being timed.  The server keys each rendezvous on
            // its own scene, so a broadcast naming a different one settles a
            // different load — and retiring this deadline on it would leave the
            // outstanding load unwatched for the rest of the session.
            //
            // 🚨 The same NAME is not the same ROUND, and a restart writes the
            // name the room is already on — which is how a reload is expressed,
            // so same-name rounds are ordinary rather than exotic.  Nothing on
            // the wire dates a broadcast to a round, but the room's own rule
            // settles it: a rendezvous completes only when EVERY seat has
            // reported, and this client is a seat.  A broadcast naming the
            // scene this client is still loading therefore belongs to a round
            // this client is not in — the previous one, still in flight — and
            // acting on it would retire the new load's deadline and tell the
            // application that everybody had finished a scene the room has
            // barely begun.
            //
            // ⛔ Only while a load is awaited, which is the state a scene
            // WRITE leaves this client in and nothing else does.  A late joiner
            // is disarmed on the join — the round it is hearing about may have
            // completed before it arrived — and so is a client that has left,
            // so `_awaitedScene` is empty for both and their broadcasts are
            // delivered untouched.
            //
            // ⚠️ A client that never reports at all is refused, and that is the
            // rule rather than a casualty of it: the room's rendezvous cannot
            // complete without this seat, so a broadcast saying it has is about
            // a round this client is not in.  It is counted rather than logged,
            // because a refusal that never fires and one that was deleted look
            // identical without a reading.
            if (sceneName == _awaitedScene && !_localReportSent)
            {
                _diagStaleAllReadyIgnored++;
                return;
            }

            if (sceneName == _awaitedScene) Disarm();
            // Through the same isolation as the rest.  This is the event an
            // application starts the match on, so a HUD subscriber that throws
            // must not be able to stop the gameplay subscriber behind it.
            Raise(OnAllPlayersSceneLoaded, sceneName, nameof(OnAllPlayersSceneLoaded));
        }

        /// <summary>
        /// Detach from the underlying <see cref="RoomManager"/> events.
        /// Called by <see cref="NetworkManager"/> during cleanup so the
        /// manager does not keep a stale room reference alive after the
        /// socket has been torn down.
        /// </summary>
        internal void Dispose()
        {
            _disposed = true;
            // ⛔ Deliberately redundant with the `_disposed` guards in Tick and
            // EnsureBound, and unobservable because of them — no test can tell
            // this line from its absence, and none can tell either guard from
            // its absence while this line stands.  Stated here so the pair is
            // not mistaken for one line and a spare: what each of them refuses
            // is the same reading, and the cost of keeping all three is nothing.
            Disarm();
            if (_bound == null) return;
            _bound.OnRoomSceneWritten      -= HandleSceneWritten;
            _bound.OnSceneReportSent       -= HandleSceneReportSent;
            _bound.OnRoomJoined            -= HandleRoomJoined;
            _bound.OnRoomLeft              -= HandleRoomLeft;
            _bound.OnAllPlayersSceneLoaded -= HandleAllReady;
            _bound = null;
        }
    }
}
