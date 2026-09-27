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
    /// How every client loads a networked scene. The values match Unity's
    /// <c>LoadSceneMode</c>.
    /// </summary>
    public enum NetworkSceneLoadMode
    {
        /// <summary>Replace the loaded scenes with the new one.</summary>
        Single = 0,

        /// <summary>Load the new scene alongside the loaded scenes.</summary>
        Additive = 1,
    }

    /// <summary>
    /// Coordinates a room-wide scene change. Get it from <see cref="NetworkManager.Scene"/>.
    /// </summary>
    /// <remarks>
    /// <para>The host calls <see cref="LoadScene"/>. Every client, the host included, receives
    /// <see cref="OnSceneLoadStartedWithMode"/>, loads the scene itself and calls
    /// <see cref="ReportReady"/>; when every player has reported,
    /// <see cref="OnAllPlayersSceneLoaded"/> is raised on every client. A player who joins a
    /// room that already has a scene receives <see cref="OnSceneLoadStartedWithMode"/> on
    /// entry.</para>
    /// <para>This class does not load scenes itself. Add <see cref="RtmpeSceneLoader"/> to have
    /// the loading and the report done for you.</para>
    /// </remarks>
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
        /// How long, in seconds, a load may go unsettled before
        /// <see cref="OnSceneLoadTimedOut"/> is raised. Zero or less turns the report off.
        /// </summary>
        /// <remarks>
        /// <para>Starts at <c>NetworkSettings.sceneReadyTimeoutSeconds</c>, or
        /// <see cref="DefaultSceneReadyTimeoutSeconds"/> when that setting is 0. A change
        /// applies from the next load.</para>
        /// <para>Turn it off only if every client always calls <see cref="ReportReady"/>;
        /// otherwise a load that never completes goes unreported.</para>
        /// </remarks>
        public float SceneReadyTimeoutSeconds { get; set; } = DefaultSceneReadyTimeoutSeconds;

        /// <summary>The default of <see cref="SceneReadyTimeoutSeconds"/>: 60 seconds.</summary>
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
        /// Raised on the same occasions as <see cref="OnSceneLoadStartedWithMode"/>, just before
        /// it, without the load mode. The argument is the scene name.
        /// </summary>
        /// <remarks>
        /// Handle one of the two events, not both. Prefer
        /// <see cref="OnSceneLoadStartedWithMode"/>: loading an additive scene as a single one
        /// unloads the scene the room is still in.
        /// </remarks>
        public event Action<string> OnSceneLoadStarted;

        /// <summary>
        /// Raised on every client, the host included, when the room is told to load a scene.
        /// The arguments are the scene name and the load mode: load the scene in that mode, then
        /// call <see cref="ReportReady"/>.
        /// </summary>
        /// <remarks>
        /// <para>Raised for every scene write the room accepts, including one naming the scene
        /// the room is already on, which restarts it, and on entering a room that already has a
        /// scene. Handle this event rather than <see cref="OnSceneLoadStarted"/>.</para>
        /// <para>If you write <c>__scene</c> yourself with
        /// <see cref="RoomManager.SetRoomProperties"/>, write <c>__scene_additive</c> with it;
        /// otherwise the room keeps the mode of the previous load.</para>
        /// </remarks>
        public event Action<string, NetworkSceneLoadMode> OnSceneLoadStartedWithMode;

        /// <summary>
        /// Raised on every client when every player in the room has reported a scene loaded.
        /// The argument is the scene name. Start the round here.
        /// </summary>
        /// <remarks>
        /// Compare the argument with <see cref="CurrentScene"/> before you act on it. A late
        /// report from a load that a newer scene write replaced is not raised while it names
        /// the scene this client has not yet reported, but a late report that names a different
        /// scene is.
        /// </remarks>
        public event Action<string> OnAllPlayersSceneLoaded;

        /// <summary>
        /// Raised when <see cref="SceneReadyTimeoutSeconds"/> passes after a load began without
        /// <see cref="OnAllPlayersSceneLoaded"/>. The argument is the scene name. Raised once per
        /// load; if the room settles later, <see cref="OnAllPlayersSceneLoaded"/> is still
        /// raised.
        /// </summary>
        /// <remarks>
        /// <para>The event cannot name the player who has not reported: the server reports only
        /// the completed load.</para>
        /// <para>Do not start the round from it: each client's timer starts with its own load, so
        /// the room would split. Use it to show a notice, offer to leave, or let the host restart
        /// the load. A player who joins while a scene is already set loads it without a
        /// timeout.</para>
        /// </remarks>
        public event Action<string> OnSceneLoadTimedOut;

        /// <summary>
        /// The room's scene, or an empty string outside a room or when none is set: the
        /// <see cref="RoomInfo.CurrentScene"/> of the current room.
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
        /// The room's load mode, read from the reserved
        /// <see cref="ReservedPropertyKeys.SceneAdditive"/> property;
        /// <see cref="NetworkSceneLoadMode.Single"/> when it is not set.
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
        /// Tells the room to load <paramref name="sceneName"/>, by writing the <c>__scene</c> and
        /// <c>__scene_additive</c> room properties. Every client, this one included, then
        /// receives <see cref="OnSceneLoadStartedWithMode"/>.
        /// </summary>
        /// <remarks>
        /// <para>Only the host's write is applied. A write from another client is refused by the
        /// server and reported by <see cref="RoomManager.OnRoomError"/> after 12 seconds.</para>
        /// <para>Loading the scene the room is already on starts it again for everyone.</para>
        /// </remarks>
        /// <param name="sceneName">The scene name or path, as passed to
        /// <c>SceneManager.LoadSceneAsync</c>.</param>
        /// <param name="mode">How every client loads the scene. Default
        /// <see cref="NetworkSceneLoadMode.Single"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="sceneName"/> is null or
        /// empty.</exception>
        /// <exception cref="InvalidOperationException">This client is not in a room.</exception>
        public void LoadScene(string sceneName, NetworkSceneLoadMode mode = NetworkSceneLoadMode.Single)
        {
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentException("sceneName must not be null or empty.", nameof(sceneName));

            EnsureBound();
            var rooms = _bound;
            // Surface the state-order violation as InvalidOperationException so
            // a caller that runs LoadScene before joining a room gets a stack
            // trace pointing at the misuse rather than a silent log line that
            // they may not see in a CI run or a release-mode build with logs
            // filtered.  The ArgumentException for a null or empty name, a few
            // lines above, already established the throw-on-misuse contract for
            // this method; this check mirrors it.
            if (rooms == null || !rooms.IsInRoom)
                throw new InvalidOperationException(
                    "NetworkSceneManager.LoadScene: caller must be joined to a room.");

            // The host question is the one this client cannot settle — see the
            // summary — and it is not asked here: the scene write below is a
            // reserved-key room write, and RoomManager makes the one-sided
            // decision, and speaks the one console line, for every such write.
            var nm = NetworkManager.Instance;

            // Robustness: prune any NetworkObjects whose GameObjects were
            // destroyed by an out-of-band scene unload BEFORE the server
            // broadcast lands.  Without this, a transitional gap can leave
            // the registry holding entries that compare equal to null when
            // the new scene's RegisterPrefab/Spawn cycle runs, producing
            // silent ID collisions if the gateway re-uses an id near the
            // wrap.  The host's sceneUnloaded handler already prunes; this
            // is a second, defensive sweep tied to the network-driven
            // transition rather than the engine's local unload event.
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
        /// Reports that this client finished loading the room's scene
        /// (<see cref="CurrentScene"/>). Call it when your load completes;
        /// <see cref="RtmpeSceneLoader"/> calls it for you.
        /// </summary>
        /// <remarks>
        /// The room completes a load only when every player, this one included, has reported
        /// it. Does nothing outside a room or when no scene is set.
        /// </remarks>
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
