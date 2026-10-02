// RTMPE SDK — Runtime/Core/NetworkManager.Connection.cs
//
// Connect/Reconnect/Disconnect, Cleanup, InitialiseNetwork, public API.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Initialisation & teardown ──────────────────────────────────────────

        private void InitialiseNetwork()
        {
            // Pluggable transport: a factory installed via SetTransportFactory
            // overrides the built-in UDP transport.  This is the extension point
            // integration tests use for a deterministic loopback, and a project
            // uses to reach the gateway some other way.  It reaches no platform
            // the default cannot — the thread above it is what a browser player
            // lacks — and no endpoint the deployment does not already serve.
            // When no factory is installed we fall back to
            // UdpTransport, preserving the historical behaviour.
            _transport = BuildTransport();

            _networkThread = new NetworkThread(
                _transport,
                _settings.networkThreadBufferBytes,
                _settings.sendQueueMaxItems);
            _networkThread.OnPacketReceivedRented += HandlePacketReceivedRented;
            _networkThread.OnError                += HandleTransportError;

            _packetBuilder = new PacketBuilder();

            // Room & Spawn managers share a single wiring path so InitialiseNetwork,
            // Connect, and Reconnect all produce the same event topology.
            RecreateRoomAndSpawnManagers();

            // Subscribe the state-sync packet handler so incoming StateDelta
            // broadcasts are routed to NetworkTransformInterpolators.
            OnDataReceived += HandleStateSyncPacket;
        }

        // Reached only from OnDestroy and OnApplicationQuit, so it is a
        // one-way door: work already queued on the shared dispatcher may still
        // run after this point, against a component Unity has torn down.
        private bool _cleanedUp;

        private void Cleanup()
        {
            _cleanedUp = true;
            // ⛔ Stopped, then cleared — and the order is the whole point.  This
            // method has TWO callers, as the field comment above says: OnDestroy
            // runs StopAllCoroutines first, so the loop is already dead there and
            // only the stale handle needs clearing.  OnApplicationQuit runs no
            // such thing, so the loop is still RUNNING when it arrives — and
            // nulling the handle alone would discard the only reference by which
            // anything could stop it, while the guard at the top of
            // DisconnectWithReason makes that call a no-op from then on.  The
            // loop would go on to start another attempt against a manager whose
            // handshake handler is disposed.
            if (_reconnectLoopCoroutine != null)
            {
                StopCoroutine(_reconnectLoopCoroutine);
                _reconnectLoopCoroutine = null;
            }
            // S4-28 — the spawned objects, before anything else is taken away.
            //
            // This method's two callers are OnDestroy and OnApplicationQuit, and
            // neither routes through ClearSessionData: the field was simply nulled
            // at the end, so a manager destroyed mid-session fired
            // OnNetworkDespawn on NOTHING and returned no pooled instance to the
            // pool it borrowed from.  Both matter beyond tidiness — the callback is
            // where an application releases what it acquired on spawn, and a pool
            // installed through INetworkObjectPool routinely outlives the manager
            // (a scene reload destroys this component and keeps the pool), so every
            // instance of the session leaked with nothing to say so.
            //
            // ⛔ Early, so the despawn callbacks run against a manager whose state
            // is still coherent.  `_cleanedUp` is already set, which makes
            // DisconnectWithReason a no-op, so application code reached from here
            // cannot re-enter the teardown.
            //
            // ⛔ Isolated, and that is the load-bearing part.  ClearAll dispatches
            // application code and a custom pool's Release; a throw from either
            // would abandon the rest of this method — leaving session keys
            // unzeroed, the network thread running and the Unity log hook still
            // subscribed.  A teardown must finish.
            try
            {
                _spawnManager?.ClearAll(resetObjectIdSpace: true);
            }
            catch (Exception ex)
            {
                if (ShouldWarn(ref _lastTeardownDespawnFaultWarnTicks))
                    Debug.LogError(
                        "[RTMPE] NetworkManager teardown: despawning the live objects threw " +
                        ex.GetType().Name + ": " + ex.Message + ". The rest of the teardown " +
                        "continues; some OnNetworkDespawn callbacks may not have run and some " +
                        "pooled instances may not have been released.");
            }

            _heartbeatManager?.Stop();
            _heartbeatManager = null;
            _diagnosticsUplink?.Stop();  // unsubscribe the Unity log hook
            _diagnosticsUplink = null;
            _handshakeHandler?.Dispose();  // Zero key material before GC can observe it
            _handshakeHandler = null;
            _sessionKeyStore.DisposeKeys();  // Zero session keys before GC can observe it
            // OnDestroy / OnApplicationQuit reach Cleanup directly without
            // routing through ClearSessionData, so the HKDF-derived auxiliary
            // keys must be zeroed here too — otherwise mid-session app quit
            // or scene unload leaves the key bytes on the managed heap until
            // the next GC cycle.  Match the explicit Array.Clear pattern
            // used by ClearSessionData.
            if (_ipMigrationKey != null)
            {
                Array.Clear(_ipMigrationKey, 0, _ipMigrationKey.Length);
                _ipMigrationKey = null;
            }
            if (_sessionAckKey != null)
            {
                Array.Clear(_sessionAckKey, 0, _sessionAckKey.Length);
                _sessionAckKey = null;
            }
            // Release the session bearer credentials too.  Cleanup is reached on
            // OnDestroy / OnApplicationQuit without routing through
            // ClearSessionData — the only other site that clears them — so a
            // manager destroyed mid-session would otherwise leave the JWT and
            // reconnect token referenced for the rest of the heap's lifetime.
            _jwtToken       = null;
            _reconnectToken = null;
            // The gateway's statement about that token goes with it, here as in
            // ClearSessionData — this is the other site that releases the
            // credentials, and a statement outliving its token is one the next
            // token would be judged by.
            _reconnectTokenLife.Forget();
            // Detach the process-static RPC-verifier hooks here too: OnDestroy /
            // OnApplicationQuit reach Cleanup without routing through
            // ClearSessionData, so a manager destroyed while still connected
            // would otherwise leave closures over its torn-down state live on
            // the verifier.
            DetachRpcVerifierHooks();
            // And the per-object advisory latch, for the same reason again:
            // OnDestroy nulls the spawn manager without going through ClearAll,
            // so a title that reloads its scene between matches would otherwise
            // carry every surfaced id into the next one and spend the advisory's
            // budget across a run rather than a session.
            RTMPE.Core.Diagnostics.RemoteInterpolatorAdvisory.ResetLatch();
            _sessionKeyStore.ResetReplayWindow();
            // Detach scene manager BEFORE tearing down the network thread so
            // any in-flight SceneLoaded callbacks don't fire into a disposed manager.
            _sceneManager?.Dispose();
            _sceneManager = null;
            // Unsubscribe before dispose to break delegate references.
            if (_networkThread != null)
            {
                _networkThread.OnPacketReceivedRented -= HandlePacketReceivedRented;
                _networkThread.OnError                -= HandleTransportError;
                _networkThread.Dispose();
            }
            _networkThread = null;
            _transport     = null;

            // Symmetric unsubscribe to defend against future re-init paths
            // that would otherwise accumulate handlers.  InitialiseNetwork
            // attaches HandleStateSyncPacket; the historical Cleanup path did
            // not detach it because the manager was assumed to be re-created
            // (not re-initialised) per session.  A future refactor that calls
            // InitialiseNetwork twice on the same instance would silently
            // double-fire StateSync without this line.
            OnDataReceived -= HandleStateSyncPacket;

            // Break the circular delegate reference: RoomManager holds delegate
            // instances that capture `this` (the NetworkManager).  Without this
            // unsubscription the two objects form a reference cycle that survives
            // until GC finalisation — preventing timely collection after teardown
            // and keeping room-state alive in memory across scene reloads.
            // This is the only place outside RecreateRoomAndSpawnManagers that
            // modifies _roomManager; Disconnect() → ClearSessionData() does NOT
            // unsubscribe, so the subscriptions would leak for the remainder of
            // the component's lifetime if we did not clean them up here.
            if (_roomManager != null)
            {
                _roomManager.OnRoomJoined          -= OnRoomManagerJoined;
                _roomManager.OnRoomLeft            -= OnRoomManagerLeft;
                _roomManager.OnRoomCreated         -= OnRoomManagerCreated;
                _roomManager.OnPlayerLeft          -= OnRoomManagerPlayerLeft;
                _roomManager.OnPlayerJoined        -= OnRoomManagerPlayerJoined;
                _roomManager.OnMasterClientChanged -= OnRoomManagerMasterClientChanged;
                _roomManager.OnEndedEntryAnswered  -= OnEndedEntryAnswered;
                _roomManager = null;
            }
            if (_matchmakingManager != null)
                _matchmakingManager.OnEndedEntryAnswered -= OnEndedEntryAnswered;
            _spawnManager       = null;
            _lobbyManager       = null;
            _matchmakingManager = null;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        // ⚠️ A warning rather than an error, and the cost is stated: a project
        // compiling with warnings-as-errors cannot build at all once the WebGL
        // target is selected, Editor iteration included.  An error would be the
        // honest verdict on the build and would take Play mode with it; a
        // warning leaves the developer that call and still refuses at runtime.
#if UNITY_WEBGL
#warning RTMPE does not run in a WebGL player: its I/O layer needs a background thread the browser does not provide, and a transport installed through NetworkManager.SetTransportFactory sits above that thread rather than replacing it.
#endif

        /// <summary>
        /// Whether the running player is one this SDK cannot drive, reported to
        /// the caller of <paramref name="api"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every public entry point that arms the network thread asks this first,
        /// so the answer is written once: two copies of a platform rule are one
        /// copy and one that goes stale.
        /// </para>
        /// <para>
        /// The Editor sits outside the refusal on purpose. Selecting the WebGL
        /// build target defines <c>UNITY_WEBGL</c> for the Editor too, but Play
        /// mode still runs on the Editor's own player, which has the thread this
        /// refusal is about — so refusing there would break iteration on a
        /// platform that works. That asymmetry is why the compile-time warning
        /// above is not a second copy of this one: without it, a developer
        /// targeting WebGL sees the SDK work in Play mode and learns the truth
        /// only from a shipped build that cannot connect.
        /// </para>
        /// </remarks>
        private static bool PlatformRefusesNetworking(string api)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogError(
                $"[RTMPE] NetworkManager.{api}: WebGL is not a supported platform. The SDK " +
                "drives its transport from a dedicated background thread, which the browser " +
                "player does not provide, and a transport installed through " +
                "SetTransportFactory sits above that thread rather than replacing it. " +
                "Supported platforms are Windows, macOS, Linux, Android and iOS.");
            return true;
#else
            return false;
#endif
        }

        /// <summary>
        /// Starts connecting to the RTMPE server with the given API key and moves to
        /// <see cref="NetworkState.Connecting"/>.
        /// </summary>
        /// <remarks>
        /// <para>Success raises <see cref="OnConnected"/>. A failure raises
        /// <see cref="OnConnectionFailed"/>, then <see cref="OnDisconnected"/>; a session that
        /// fails the SDK's validation when it is established raises only
        /// <see cref="OnDisconnected"/>, with <see cref="DisconnectReason.Unknown"/>.</para>
        /// <para>Refused with a logged error, and no state change, when
        /// <paramref name="apiKey"/> is empty, when the manager is disabled or its GameObject
        /// inactive, when the call is made on a manager that is not the active
        /// <see cref="Instance"/>, and in a WebGL player, which cannot run the SDK's network
        /// thread. Ignored with a warning when the state is not
        /// <see cref="NetworkState.Disconnected"/>. A call made while reconnect attempts wait
        /// between tries stops them.</para>
        /// </remarks>
        /// <param name="apiKey">The project's API key, as resolved by <see cref="ApiKeySource"/>.</param>
        public void Connect(string apiKey)
        {
            if (PlatformRefusesNetworking(nameof(Connect))) return;

            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[RTMPE] NetworkManager.Connect: apiKey must not be null or empty.");
                return;
            }

            // The handshake and the connection-timeout watchdog are coroutines,
            // and the retransmit ladder and the heartbeat are driven from this
            // component's Update.  On an inactive GameObject a coroutine never
            // starts, so a connection begun there is wedged in Connecting with
            // nothing to time it out; on a disabled component the coroutines
            // run but Update does not, so the handshake has no retransmit and a
            // session it reaches has no heartbeat and is dropped by the gateway
            // unnoticed.  Refuse here with an actionable message instead.
            if (!isActiveAndEnabled)
            {
                Debug.LogError(
                    "[RTMPE] NetworkManager.Connect: the NetworkManager is disabled or its " +
                    "GameObject is inactive. Activate it before calling Connect().");
                return;
            }

            // Transport, managers and session state belong to the published
            // singleton, which Awake initialises.  A second, non-canonical
            // component is destroyed at Awake before initialisation runs, so a
            // Connect routed to such a stray reference would dereference fields
            // that were never created.  Require the initialised instance.
            if (_instance != this || _settings == null)
            {
                Debug.LogError(
                    "[RTMPE] NetworkManager.Connect: called on a NetworkManager that is not the " +
                    "initialised singleton. Connect through NetworkManager.Instance.");
                return;
            }

            if (_state != NetworkState.Disconnected)
            {
                Debug.LogWarning($"[RTMPE] NetworkManager.Connect ignored — already in state {_state}.");
                return;
            }

            // A bounded reconnect loop, if one is running, owns the connection
            // lifecycle through its coroutine handle.  Its inter-attempt backoff
            // parks the manager in Disconnected — the same state this method
            // treats as its entry condition — so a full re-authentication would
            // otherwise proceed alongside a sleeping loop that later wakes to
            // drive its own attempt over this fresh session or to run its
            // terminal teardown against it.  An explicit Connect supersedes the
            // loop, so retire it before re-arming, mirroring Disconnect().
            if (_reconnectLoopCoroutine != null)
            {
                StopCoroutine(_reconnectLoopCoroutine);
                _reconnectLoopCoroutine = null;
            }

            int announced = _transitionSerial;
            TransitionTo(NetworkState.Connecting);

            // S4-36 — that transition raises OnStateChanged synchronously, and a
            // handler on it is free to call Disconnect().  Everything below arms an
            // attempt: it rebuilds the managers, starts the network THREAD and two
            // coroutines.  Run after a handler has already torn the session down,
            // it leaves all of that live with the manager sitting in Disconnected —
            // a thread reading a socket for a connection nothing will complete, and
            // a watchdog that will later announce a timeout for an attempt the
            // application cancelled before it began.
            //
            // ⛔ Asked about the STATE and the transition count, not about the
            // attempt epoch, because the epoch cannot see this: it is incremented
            // by StartConnectionAttempt, which is below, so a nested Disconnect
            // leaves it untouched and every epoch check reads "still live".  And
            // the state alone is not exact here: a handler that disconnects and
            // connects again is admitted by the guard above — the manager is in
            // Disconnected when its Connect() runs — and returns with the manager
            // in Connecting and an attempt armed, which this call must not arm
            // over.
            if (!AttemptSurvivedItsOwnAnnouncement(NetworkState.Connecting, nameof(Connect), announced))
                return;

            // Reset the packet builder so sequence numbers start fresh on reconnect.
            _packetBuilder = new PacketBuilder();

            // Reset the outbound AEAD nonce counter so the first encrypted packet
            // of every session starts at counter = 0, matching the gateway's
            // NonceGenerator which also resets to 0 for each new EstablishedSession.
            _sessionKeyStore.ResetOutboundNonceCounter();
            // Mirror the reset for the application-level sequence so the first
            // FLAG_APP_SEQUENCE packet of a fresh session starts at 0.
            System.Threading.Interlocked.Exchange(ref _outboundAppSequenceCounter, -1L);
            _sessionKeyStore.ResetLastInboundAppSequence();

            // Recreate Room/Spawn managers with the fresh PacketBuilder.
            RecreateRoomAndSpawnManagers();

            // Arm the diagnostics uplink for pre-session log capture so errors
            // during the handshake (crypto setup, transport bind, timeout) are
            // buffered and promoted into the first post-session flush on SessionAck.
            // Stop and null any prior instance first; Stop() is idempotent and
            // handles both the normal and pre-session subscribe paths.
            _diagnosticsUplink?.Stop();
            _diagnosticsUplink = null;
            if (_settings.enableDiagnosticsUplink)
            {
                _diagnosticsUplink = new Diagnostics.DiagnosticsUplink(_settings, _packetBuilder);
                _diagnosticsUplink.StartPreSessionCapture();
            }

            // Defensive disposal: the state machine guarantees we are in
            // Disconnected (which always traverses Cleanup), but a future
            // refactor could legitimately call Connect from a different
            // path.  Disposing here is a no-op when the field is already
            // null, and prevents an X25519 ephemeral private key from
            // surviving in heap memory across reconnect attempts.
            _handshakeHandler?.Dispose();
            _sessionKeyStore.DisposeKeys();

            // Create a fresh handshake handler (generates a new X25519 ephemeral keypair).
            _handshakeHandler = new HandshakeHandler();
            ResetChallengeAdmission();

            // Re-arm the network thread before starting it.  Awake() constructs it
            // once, but a prior failed Connect — a Strict-pinning handshake refusal
            // or a connection timeout — stops and nulls the thread on the way back
            // to Disconnected, so a subsequent Connect must reconstruct it.  The
            // reconnect path relies on the same guard; sharing it keeps both entry
            // points symmetric.  Rebuilding is idempotent — a no-op when the
            // thread is still alive — while the epoch taken alongside it is not:
            // it is what makes this attempt distinguishable from the last.
            StartConnectionAttempt();

            // Kick off the async handshake-init coroutine — it waits for the transport
            // to be bound (LocalEndPoint != null) before building and sending the packet.
            _connectCoroutine = StartCoroutine(HandshakeInitCoroutine(apiKey));

            _timeoutCoroutine = StartCoroutine(ConnectionTimeoutRoutine());
        }

        /// <summary>
        /// Recreate the RoomManager and SpawnManager with the current
        /// <see cref="_packetBuilder"/> and fresh registry/ownership objects,
        /// then wire every subscription they need.  Called from
        /// <see cref="Connect"/> and <see cref="Reconnect"/> so the same
        /// event topology is guaranteed on both paths — previously the two
        /// call sites duplicated the wiring which let drifts slip through
        /// (e.g. a new subscription added to one path only).
        /// </summary>
        private void RecreateRoomAndSpawnManagers()
        {
            // Serialize against scene-transition handlers so a Recreate
            // triggered by reconnect cannot interleave with a PruneDestroyed
            // from sceneUnloaded.  Documented ordering when both fire:
            //  sceneUnloaded → Prune → sceneLoaded → Prune
            //  (Recreate runs atomically with respect to the above.)
            lock (_sceneTransitionLock)
            {
                // Held across the rebuild so the replacements can take over
                // their subscribers.  These managers are the SDK's public event
                // surface and this is the call that replaces them, so a handler
                // registered against the instance the application could see
                // would otherwise stop being called with nothing to say so.
                //
                // Adoption carries whatever is attached to the predecessor when
                // it runs, so an SDK subscriber that will attach to the
                // replacement must not still be on the predecessor by then, or
                // it ends up registered twice.  The handlers below are detached
                // from the predecessor first; NetworkSceneManager, which binds
                // lazily to whichever instance is live rather than being wired
                // here, detaches from that instance before attaching to it.
                // Adoption itself happens at the end of this method — see the
                // ordering note there.
                var previousRoomManager        = _roomManager;
                var previousLobbyManager       = _lobbyManager;
                var previousMatchmakingManager = _matchmakingManager;

                // Symmetric detach against the prior instance.  Replacing
                // _roomManager with a new instance leaves the old instance
                // unreferenced from this field, but a transport callback
                // already in-flight on a worker thread may still reach the
                // old delegate list and invoke our handler against
                // already-replaced state.  Detaching first guarantees the
                // old instance dispatches no further callbacks regardless
                // of the GC schedule.
                if (_roomManager != null)
                {
                    _roomManager.OnRoomJoined          -= OnRoomManagerJoined;
                    _roomManager.OnRoomLeft            -= OnRoomManagerLeft;
                    _roomManager.OnRoomCreated         -= OnRoomManagerCreated;
                    _roomManager.OnPlayerLeft          -= OnRoomManagerPlayerLeft;
                    _roomManager.OnPlayerJoined        -= OnRoomManagerPlayerJoined;
                    _roomManager.OnMasterClientChanged -= OnRoomManagerMasterClientChanged;
                }

                // RoomManager shares PacketBuilder with the rest of the outbound
                // pipeline so room packets use a single monotonic sequence counter
                // — using an independent counter would be a protocol violation
                // and may trigger gateway replay protection.
                _roomManager = new RoomManager(
                    _packetBuilder,
                    SendBuiltPacket,
                    () => _state,
                    id => SetLocalRoomPlayerId(id),
                    entries: _roomEntries);
                _roomManager.OnRoomJoined   += OnRoomManagerJoined;
                _roomManager.OnRoomLeft     += OnRoomManagerLeft;
                _roomManager.OnRoomCreated  += OnRoomManagerCreated;
                _roomManager.OnEndedEntryAnswered += OnEndedEntryAnswered;

                _lobbyManager = new LobbyManager(
                    _packetBuilder,
                    SendBuiltPacket);

                _matchmakingManager = new MatchmakingManager(
                    _packetBuilder,
                    SendBuiltPacket,
                    () => _state,
                    () => _localPlayerStringId ?? string.Empty,
                    // A5-2: record the server-derived player_id from the
                    // matchmaking reply, mirroring the JoinRoom path so the
                    // SDK's local identity is correct after a matchmake.
                    id => SetLocalRoomPlayerId(id),
                    entries: _roomEntries,
                    // Adopt the room the server seated the player in during the
                    // same matchmaking transaction, so the session enters
                    // InRoom (and inbound room traffic is accepted) without a
                    // second JoinRoom that would collide on that seat.
                    enterMatchedRoom: entry => _roomManager.EnterMatchmadeRoom(
                        entry.RoomId, entry.RoomCode, entry.Created, entry.PlayerId, entry.MaxPlayers,
                        entry.Players, entry.PropertiesPayload, entry.PropertiesComplete, entry.Entry,
                        entry.AnswersARequestInFlight));
                _matchmakingManager.OnEndedEntryAnswered += OnEndedEntryAnswered;

                var registry  = new NetworkObjectRegistry();
                var ownership = new OwnershipManager(registry, this);
                var previousSpawnManager = _spawnManager;
                _spawnManager = new SpawnManager(registry, ownership, this);
                // The generated registry first, so a registration the
                // application made by hand outranks it: AdoptPrefabsFrom
                // overwrites on collision, and it runs second.  A project can
                // therefore replace one row without abandoning the asset for
                // the rest.
                //
                // ⚠️ Here rather than in InitialiseNetwork, which runs once in
                // Awake while this runs on every connect.  Loading there would
                // leave every reconnect's table to the carry below, and a carry
                // is maintained rather than guaranteed.
                // ⚠️ `!= null` rather than `?.`, and the difference is Unity's:
                // `?.` is a reference comparison the engine's overloaded
                // operator never sees, so a settings object the editor has
                // destroyed reads as present. Every other settings access in
                // this class is written the same way.
                _spawnManager.LoadPrefabRegistry(
                    _settings != null ? _settings.prefabRegistry : null);
                // Prefab registrations are static configuration, not session
                // state; carrying them across the rebuild keeps a single
                // RegisterPrefab call valid for the application's lifetime,
                // independent of when it ran relative to Connect.
                _spawnManager.AdoptPrefabsFrom(previousSpawnManager);
                // And its subscribers, as the room-side managers carry theirs:
                // a refused spawn or an unanswered transfer is reported on
                // these events and nowhere else.
                _spawnManager.AdoptSubscribersFrom(previousSpawnManager);
                _spawnManager.Ownership.AdoptSubscribersFrom(previousSpawnManager?.Ownership);

                // Wire the static EnhancedRpcVerifier hooks to the live
                // session state.
                //
                // The roster-anchored sender verifier admits the local session
                // id and (when in a room) defers to IsRosterMemberSession for
                // peer admission.  The current room wire format (see
                // RoomPacketParser — RoomJoin response and PlayerJoined
                // notification) does NOT carry the gateway session id per
                // roster member; only player UUIDs are exposed.  Without a
                // session-id keyed roster the SDK cannot distinguish a
                // legitimate peer from an arbitrary in-room sender, so the
                // membership predicate accepts any non-zero session id and a
                // one-time advisory is emitted on first peer admission.  This
                // preserves cross-player RPC delivery while keeping the zero
                // sentinel guard (impersonation of the pre-authenticated
                // session) and the self-only path active outside any room.
                //
                // The object verifier requires the inbound objectId to resolve
                // in the spawn registry.  Both hooks are torn down on Cleanup /
                // ClearSessionData so a stale closure cannot outlive the
                // manager that captured it.
                RTMPE.Rpc.EnhancedRpcVerifier.SelfSessionIdProvider =
                    () => _localPlayerId;
                RTMPE.Rpc.EnhancedRpcVerifier.IsRoomJoined =
                    () => _roomManager?.CurrentRoom != null;
                RTMPE.Rpc.EnhancedRpcVerifier.LocalSessionIdProvider =
                    () => _localPlayerId;
                RTMPE.Rpc.EnhancedRpcVerifier.IsRosterMemberSession =
                    AdmitNonZeroPeerWithOneTimeAdvisory;
                RTMPE.Rpc.EnhancedRpcVerifier.SenderVerifier =
                    RTMPE.Rpc.EnhancedRpcVerifier.RoomAnchoredSenderVerifier;
                RTMPE.Rpc.EnhancedRpcVerifier.ObjectExistsVerifier =
                    objectId => objectId != 0UL && registry.Get(objectId) != null;
                // Whether this session's gateway writes every relayed RPC's
                // caller facts itself — read at each frame, so a reconnect to a
                // gateway of another generation is judged by what that gateway
                // said.
                RTMPE.Rpc.EnhancedRpcVerifier.GatewayAttestsCaller =
                    () => _attestedRpcCaller;

                // Use named-method delegates (not inline lambdas) so the
                // delegate instances are not re-allocated per call to
                // RecreateRoomAndSpawnManagers, which fires on every
                // reconnect / scene load.  Inline lambdas would also
                // capture `this` implicitly and prevent the JIT from
                // caching the delegate; method-group references hit the
                // delegate cache and are emitted as a static field by Roslyn.
                _roomManager.OnPlayerLeft          += OnRoomManagerPlayerLeft;
                _roomManager.OnPlayerJoined        += OnRoomManagerPlayerJoined;
                _roomManager.OnMasterClientChanged += OnRoomManagerMasterClientChanged;

                // Last, so the application's handlers sit behind the SDK's in
                // every invocation list — the position they hold on the
                // instance being replaced, since InitialiseNetwork wires these
                // handlers before the application can reach the manager at all.
                // The order is load-bearing: OnRoomManagerJoined is what
                // transitions the session to InRoom, and RoomManager refuses
                // LeaveRoom, the property setters, TransferMasterClient,
                // KickPlayer and ReportSceneLoaded outside that state.  An
                // adopted handler placed ahead of it would be called into a
                // session that silently declines every room operation it makes.
                _roomManager.AdoptSubscribersFrom(previousRoomManager);
                _lobbyManager.AdoptSubscribersFrom(previousLobbyManager);
                _matchmakingManager.AdoptSubscribersFrom(previousMatchmakingManager);

                // Whatever the replaced instance still owes its subscribers moves
                // to the successor, which reports it from Tick — never from here.
                _matchmakingManager.AdoptUnreportedOutcomeFrom(previousMatchmakingManager);
            }
        }

        // Event handlers wired in RecreateRoomAndSpawnManagers — named here
        // to avoid per-subscription delegate allocation on every room join.
        private void OnRoomManagerPlayerLeft(string playerId)
        {
            RecordRoomEvent($"Player left: {playerId ?? "?"}");
            // What the departure does to the room's objects happens in its place
            // among them: while they are still arriving — staged behind a scene
            // load, or still being released — it is staged behind the spawns
            // that preceded it.  Applied now, it tombstoned the leaver ahead of
            // its own staged objects, which the release then dropped as
            // stragglers — a world that outlives its owner among them — when
            // what the room did was keep them and hand them to its host.
            long departedAt = _inboundSendCounter;
            if (StageRosterMarker(EarlyPacketKind.PlayerLeft, playerId, null, departedAt)) return;
            ApplyDepartureToObjects(playerId, departedAt);
        }

        // What a departure does to the room's objects, where it falls among them.
        private void ApplyDepartureToObjects(string playerId, long departureSendCounter)
        {
            // Destroy the leaver's DestroyWithOwner=true objects (existing
            // contract), and tombstone the departure at its place in the
            // gateway's send order.
            _spawnManager?.OnPlayerLeftRoom(playerId, departureSendCounter);

            // NEW-OWNERSHIP-1: reassign the leaver's surviving
            // (DestroyWithOwner=false) objects to the current room host so they
            // do not freeze owned by a player who is gone.  A leaver is, by
            // definition, no longer in the room, so formerStillInRoom = false.
            // If the host itself just left, MasterId may briefly be empty here
            // (the new host arrives via MasterClientChanged) — ShouldReassign
            // then returns false and OnRoomManagerMasterClientChanged completes
            // the reassignment once the new host is known.
            string host = _roomManager?.CurrentRoom?.MasterId;
            if (OwnershipReassignmentPolicy.ShouldReassign(playerId, host, formerStillInRoom: false))
            {
                _spawnManager?.Ownership?.ReassignObjectsToNewOwner(playerId, host);
            }
        }

        /// <summary>
        /// Stage what a roster change does to the room's objects in its place
        /// among them, while they are still arriving
        /// (<see cref="RoomObjectsStillArriving"/>).  Returns whether it was
        /// staged; the caller applies it at once otherwise.
        /// </summary>
        /// <remarks>
        /// The roster itself moves at once — the application is told who left
        /// and who arrived when they do; only the objects wait, because the
        /// objects are what is waiting.
        /// </remarks>
        private bool StageRosterMarker(EarlyPacketKind kind, string player, string other = null, long counter = -1L)
        {
            if (!RoomObjectsStillArriving) return false;
            WarnIfEarlyObjectEvicted(_earlyObjectBuffer.StageMarker(kind, RoomEntryToken, player, other, counter, keepOldest: true));
            return true;
        }

        // A staged roster marker, released in its place among the objects.
        // Isolated: the staged release dequeues before it dispatches, and the
        // reassignment raises the application's ownership callbacks.
        private void ApplyStagedRosterMarker(EarlyObjectPacketBuffer.Staged marker)
        {
            try
            {
                switch (marker.Kind)
                {
                    case EarlyPacketKind.PlayerLeft:
                        ApplyDepartureToObjects(marker.Player, marker.Counter);
                        break;
                    case EarlyPacketKind.PlayerJoined:
                        ApplyArrivalToObjects(marker.Player);
                        break;
                    case EarlyPacketKind.MasterChanged:
                        _spawnManager?.Ownership?.ReassignObjectsToNewOwner(marker.Player, marker.Other);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (ShouldWarn(ref _lastStagedMarkerThrowWarnTicks))
                    Debug.LogError(
                        "[RTMPE] A roster change held behind the room's arriving objects threw " +
                        $"when it was applied; the rest are applied regardless.\n{ex}");
            }
        }

        private void OnRoomManagerPlayerJoined(PlayerInfo player)
        {
            RecordRoomEvent($"Player joined: {player?.PlayerId ?? "?"}");
            _spawnManager?.MarkAllVariablesDirtyForResync();
            // And once more a second from now: the first snapshot can be lost
            // on a best-effort link, and nothing else ever re-sends an
            // unchanged scalar (architecture.md §8).  Whether the second lands
            // before or after the joiner's object replays does not matter —
            // the joiner holds what precedes a spawn and applies it at the
            // spawn.
            _spawnManager?.ScheduleFollowUpResync();
            // What the arrival does to the room's objects waits in its place
            // among them while they are still arriving, as a departure does.
            if (StageRosterMarker(EarlyPacketKind.PlayerJoined, player?.PlayerId)) return;
            ApplyArrivalToObjects(player?.PlayerId);
        }

        // What an arrival does to the room's objects, where it falls among them.
        private void ApplyArrivalToObjects(string playerId)
        {
            // A player back on the roster owns legitimate spawns: lift any
            // departure tombstone still held under its (server-reused) id so the
            // re-spawn is admitted rather than dropped as a late-after-leave race.
            _spawnManager?.OnPlayerJoinedRoom(playerId);
            // What the gateway sent this client for the returning player between
            // its departure and this arrival was held rather than dropped;
            // applied now, in the order it arrived, and last: each one runs the
            // application's spawn callbacks, which must neither cost the arrival
            // its snapshot above nor cost the next held spawn its turn.  Exempt
            // from the per-second rate cap as the staged catch-up is: a bounded
            // burst the client cannot ask for again (the per-room count cap
            // still applies).  A callback that ends the room or the session
            // clears the state these belong to, and the rest go with it.
            var spawns = _spawnManager;
            if (spawns != null)
            {
                int generation = spawns.ClearGeneration;
                foreach (var packet in spawns.TakeSpawnsHeldFor(playerId))
                {
                    if (!ReferenceEquals(spawns, _spawnManager) || spawns.ClearGeneration != generation)
                        break;
                    try
                    {
                        ApplySpawnPacket(packet, fromStagedCatchUp: true);
                    }
                    catch (Exception ex)
                    {
                        if (ShouldWarn(ref _lastHeldSpawnReleaseThrowWarnTicks))
                            Debug.LogError(
                                "[RTMPE] A spawn held for a returning player threw while it was " +
                                $"applied; the rest are applied regardless.\n{ex}");
                    }
                }
            }
        }

        // NEW-OWNERSHIP-1 (host-migration ordering safety net).  When the host
        // leaves, the RoomLeave packet can be processed before
        // MasterClientChanged updates MasterId, so OnRoomManagerPlayerLeft sees
        // no valid host and skips.  This path reassigns the departed host's
        // surviving objects to the newly-promoted host — but ONLY when the
        // previous master has actually left the room.  A voluntary in-room
        // master transfer (the old master stays) must NOT move its objects, so
        // we gate on live roster membership.
        private void OnRoomManagerMasterClientChanged(string previousMasterId, string newMasterId)
        {
            bool prevStillInRoom = IsPlayerInCurrentRoom(previousMasterId);
            if (OwnershipReassignmentPolicy.ShouldReassign(previousMasterId, newMasterId, prevStillInRoom))
            {
                // Decided now, against the roster as it stands; applied in its
                // place among the room's objects while they are still arriving.
                if (StageRosterMarker(EarlyPacketKind.MasterChanged, previousMasterId, newMasterId)) return;
                _spawnManager?.Ownership?.ReassignObjectsToNewOwner(previousMasterId, newMasterId);
            }
        }

        // True iff playerId is a current member of the joined room's roster.
        private bool IsPlayerInCurrentRoom(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            PlayerInfo[] players = _roomManager?.CurrentRoom?.Players;
            if (players == null) return false;
            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].PlayerId == playerId) return true;
            }
            return false;
        }

        // One-time advisory state for the in-room peer-admission fallback.
        // The roster-anchored sender verifier consults this predicate when a
        // non-self senderId arrives while the local SDK is in a room.  The
        // current room wire format does not expose gateway session
        // ids per roster member, so client-side roster anchoring is not yet
        // possible; non-zero peers are admitted with a one-time advisory so
        // legitimate cross-player RPC traffic flows while the architectural
        // limitation remains visible to integrators auditing logs.
        private static int _peerAdmissionAdvisoryEmitted;

        private static bool AdmitNonZeroPeerWithOneTimeAdvisory(ulong senderId)
        {
            if (senderId == 0UL) return false;
            if (System.Threading.Interlocked.CompareExchange(
                    ref _peerAdmissionAdvisoryEmitted, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] EnhancedRpcVerifier admitting peer RPCs without a " +
                    "session-id-keyed roster anchor.  The room wire format does " +
                    "not expose per-member gateway session ids, so peer senderIds " +
                    "cannot be cross-checked against the roster on the client.  " +
                    "Wire EnhancedRpcVerifier.IsRosterMemberSession or call " +
                    "SetServerAttestedSenderVerifier for stricter admission.");
            }
            return true;
        }

        // Detach the static EnhancedRpcVerifier hooks installed by
        // RecreateRoomAndSpawnManagers and restore the conservative self-only
        // defaults.  Invoked from BOTH session-teardown paths — ClearSessionData
        // (Disconnect / reconnect) and Cleanup (OnDestroy / OnApplicationQuit) —
        // so neither a disconnected nor a destroyed manager leaves a captured
        // registry / session-id closure live on the process-static verifier, and
        // a subsequent session that boots without RecreateRoomAndSpawnManagers
        // inherits the self-only policy rather than a stale roster-anchored one.
        private void DetachRpcVerifierHooks()
        {
            // Reset every verifier hook to the conservative self-only default and
            // re-arm the verifier's own warn-once latches, so the next session
            // re-emits the roster-anchor / permissive-fallback advisories instead
            // of inheriting a latched-quiet state from the session just torn down.
            RTMPE.Rpc.EnhancedRpcVerifier.Reset();
            // NetworkManager owns a separate peer-admission advisory latch (the
            // in-room non-zero-sender fallback); re-arm it on the same teardown so
            // each session likewise gets its one-time warning.
            System.Threading.Interlocked.Exchange(ref _peerAdmissionAdvisoryEmitted, 0);
        }

        /// <summary>
        /// Restores the previous session with the reconnect token the SDK holds, making up to
        /// <see cref="NetworkSettings.maxReconnectAttempts"/> attempts.
        /// </summary>
        /// <remarks>
        /// <para>Call it after an unexpected disconnect that kept the token
        /// (<see cref="CanReconnect"/> is <see langword="true"/>). It moves to
        /// <see cref="NetworkState.Reconnecting"/> and returns at once; the attempts run in
        /// the background, with a random, growing delay between them (see
        /// <see cref="ReconnectBackoff"/>). When the server's stated token lifetime has
        /// passed, one attempt is made.</para>
        /// <para>Each failed attempt raises <see cref="OnDisconnected"/>, and the manager waits
        /// in <see cref="NetworkState.Disconnected"/> until the next one. Success raises
        /// <see cref="OnConnected"/>; the SDK then rejoins <see cref="LastRoomId"/> when
        /// <see cref="NetworkSettings.autoRejoinLastRoomOnReconnect"/> is on. When every
        /// attempt fails, the session data is cleared, the reconnect token with it, and
        /// <see cref="OnReconnectFailed"/> is raised: call <see cref="Connect(string)"/> to
        /// start again.</para>
        /// <para>An attempt that ends with <see cref="DisconnectReason.Unknown"/> (a session
        /// that fails the SDK's validation) stops the attempts and discards the token, and
        /// <see cref="OnReconnectFailed"/> is not raised. <see cref="Connect(string)"/> and
        /// <see cref="Disconnect"/> also stop the attempts.</para>
        /// </remarks>
        /// <returns>
        /// <see langword="true"/> when the attempts have started. <see langword="false"/>, with
        /// a logged message, when no reconnect token is held, when the state is not
        /// <see cref="NetworkState.Disconnected"/>, when reconnect attempts are already
        /// running, when the manager is disabled or its GameObject inactive, and in a WebGL
        /// player.
        /// </returns>
        public bool Reconnect()
        {
            if (PlatformRefusesNetworking(nameof(Reconnect))) return false;

            // The retry loop and its per-attempt watchdog are coroutines, and
            // the retransmit ladder and the heartbeat are driven from Update.  On
            // an inactive GameObject the loop never starts, so a reconnect begun
            // there enters Reconnecting with no path out but Disconnect(), which
            // discards the token this call exists to spend; on a disabled
            // component the loop runs but Update does not, so each attempt goes
            // out without a retransmit and a session it reaches is unattended.
            // Refused ahead of the transition, as Connect() refuses, so the state
            // never becomes one no caller can recover from.
            if (!isActiveAndEnabled)
            {
                Debug.LogError(
                    "[RTMPE] NetworkManager.Reconnect: the NetworkManager is disabled or its " +
                    "GameObject is inactive. Activate it before calling Reconnect().");
                return false;
            }

            // A loop already in flight owns the retry lifecycle.  Its backoff
            // gap rests briefly in Disconnected, so the state guard below cannot
            // on its own stop a second call from stacking a rival loop over the
            // same session — one bounded loop drives the retries at a time.
            if (_reconnectLoopCoroutine != null)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.Reconnect ignored — " +
                                 "a reconnect attempt is already in progress.");
                return false;
            }

            if (!CanReconnect)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.Reconnect: no reconnect token — " +
                                 "client must call Connect(apiKey) to re-authenticate.");
                return false;
            }

            if (_state != NetworkState.Disconnected)
            {
                Debug.LogWarning($"[RTMPE] NetworkManager.Reconnect ignored — state is {_state}, " +
                                 "must be Disconnected.");
                return false;
            }

            // Reconnect drives a bounded retry loop internally so a single
            // bad attempt cannot leave the SDK frozen in Reconnecting.  The
            // public method still returns immediately — observers see an
            // immediate transition to Reconnecting and the loop coroutine
            // owns the subsequent state moves.
            int announced = _transitionSerial;
            TransitionTo(NetworkState.Reconnecting);

            // S4-36, the third door.  A handler on that transition may disconnect;
            // the loop started below would then re-announce Reconnecting on its
            // first iteration and drive the attempt the application just
            // cancelled.
            if (!AttemptSurvivedItsOwnAnnouncement(NetworkState.Reconnecting, nameof(Reconnect), announced))
                return false;

            int budget = (_settings != null && _settings.maxReconnectAttempts > 0)
                ? _settings.maxReconnectAttempts
                : 1;

            // S4-19 — a token past the lifetime the gateway stated for it is
            // worth one attempt, not the whole ladder.  Each attempt re-sends
            // the same token to the same gateway and waits out the connection
            // watchdog, so five of them cost a player about a minute of
            // "reconnecting" to learn what the first one established; the
            // fall-back after the ladder — a fresh handshake — is what actually
            // recovers the session, and the sooner it runs the better.
            //
            // ⛔ Still one attempt and never none.  The statement is a lower
            // bound: the gateway renews the token of a session it still holds
            // and an idle eviction does not delete the entry, so an over-age
            // token is often live — refusing to present it would cost the
            // player their session identity and their room in exactly the case
            // where a resume would have worked.
            //
            // ⛔ And the decision is the client's clock alone.  The gateway's
            // refusal arrives on an unauthenticated frame that OnHandshakeError
            // deliberately declines to act on; nothing here reads it.
            budget = _reconnectTokenLife.AttemptBudget(budget, ReconnectTokenLife.NowUnixSeconds);

            _reconnectLoopCoroutine = StartCoroutine(ReconnectLoopCoroutine(budget));
            return true;
        }

        // Coroutine handle for the bounded retry loop.  Held so a user-driven
        // Disconnect() or a successful SessionAck can stop the loop deterministic-
        // ally without leaving an orphaned coroutine that would later try to
        // mutate state on a torn-down session.
        private Coroutine _reconnectLoopCoroutine;

        /// <summary>
        /// Bounded retry loop driven by <see cref="Reconnect"/>.
        /// Performs up to <paramref name="maxAttempts"/> single-attempt
        /// reconnects, spaced by <see cref="ReconnectBackoff"/>.  Exits early
        /// on the first attempt that reaches <see cref="NetworkState.Connected"/>;
        /// on exhaustion transitions to <see cref="NetworkState.Disconnected"/>,
        /// clears session data, and fires <see cref="OnReconnectFailed"/>.
        /// </summary>
        private IEnumerator ReconnectLoopCoroutine(int maxAttempts)
        {
            var backoff = new ReconnectBackoff();
            int attempts = 0;

            while (attempts < maxAttempts)
            {
                attempts++;

                // CanReconnect can flip between attempts (token cleared by
                // a racing Disconnect, or session torn down externally).
                // Bail out quietly if the precondition no longer holds.
                if (!CanReconnect) break;

                // Each attempt starts a fresh per-connection state.  We
                // re-enter Reconnecting here in case a previous failed
                // attempt transitioned us to Disconnected via the timeout
                // coroutine — the loop owns the state lifecycle, not the
                // individual attempt coroutines.
                int announced = _transitionSerial;
                if (_state == NetworkState.Disconnected)
                    TransitionTo(NetworkState.Reconnecting);

                // S4-36, the same defect one door over.  A handler on that
                // transition may disconnect, and Disconnect retires this loop by
                // stopping its coroutine — which takes effect at the next yield,
                // not here, so StartReconnectAttempt would still run and re-arm a
                // network thread the teardown had just retired.  Ending the loop
                // outright is right: the application has said it does not want the
                // session, and the terminal report below belongs to a ladder that
                // ran out of attempts, not to one that was cancelled.
                if (!AttemptSurvivedItsOwnAnnouncement(
                        NetworkState.Reconnecting, nameof(Reconnect), announced))
                {
                    _reconnectLoopCoroutine = null;
                    yield break;
                }

                StartReconnectAttempt();

                // Wait for the attempt to resolve: either Connected (success)
                // or Disconnected (per-attempt timeout / transport error).
                while (_state == NetworkState.Reconnecting)
                    yield return null;

                if (_state == NetworkState.Connected)
                {
                    _reconnectLoopCoroutine = null;
                    yield break;
                }

                // Attempt failed.  Sleep with full-jitter backoff before the
                // next attempt — prevents reconnect storms on a flapping
                // gateway and de-correlates retry timing across clients.
                if (attempts < maxAttempts)
                {
                    // Real-time backoff: the inter-attempt gap is a wall-clock
                    // recovery interval, independent of simulation time, so it
                    // must elapse even while the game is paused (Time.timeScale
                    // = 0) — otherwise a client that dropped during a pause menu
                    // would never advance to its next reconnect attempt.
                    var delay = backoff.NextDelay();
                    yield return new WaitForSecondsRealtime((float)delay.TotalSeconds);
                }
            }

            // All attempts consumed.  Make the failure visible to the app —
            // the previous behaviour silently left the manager in
            // Reconnecting with no event surfaced to game UI.
            // Released before either event, for the reason the watchdog releases
            // its own handle early: Reconnect() refuses while this field is set,
            // so an application retrying from OnReconnectFailed — the obvious
            // place — was told a reconnect was already in progress, every time.
            // And clearing it afterwards would have discarded the handle that
            // retry installed.  From here the loop only unwinds, so the handle is
            // already stale.
            _reconnectLoopCoroutine = null;

            // Read before the transition below, which raises OnDisconnected —
            // the one event from which Connect() and Reconnect() are admitted at
            // all, and the one the documentation tells applications to act on.
            int epoch = System.Threading.Volatile.Read(ref _connectionAttemptEpoch);

            if (_state != NetworkState.Disconnected)
            {
                _networkThread?.Stop();
                ClearSessionData(preserveReconnectToken: false);
                TransitionTo(NetworkState.Disconnected, DisconnectReason.Timeout);
            }
            else
            {
                // The attempt that ended the loop reached Disconnected on its
                // own, and where an unauthenticated refusal prompted it the
                // watchdog kept the reconnect token — correctly, since such a
                // frame must never be allowed to spend a credential.  With the
                // budget gone there is no reader of it left.
                //
                // The obligation is the event's own: OnReconnectFailed states
                // that the manager "has already transitioned back to
                // Disconnected and cleared all session state", and this branch
                // was the one path on which that was not true.  A surviving
                // token also leaves CanReconnect true across the raise, so a
                // Reconnect() from the handler — which the documentation does
                // not prescribe, and which for that reason nobody would look
                // for — replays a token this loop has just spent a whole budget
                // failing on, and does so again for every budget after it.
                //
                // The last-room snapshot goes with the token by the rule stated
                // where those fields are declared: they have no meaning without
                // it.  An application recovering from here is told to call
                // Connect(apiKey), and rejoining is its own business again.
                ClearSessionData(preserveReconnectToken: false);
            }

            // ⛔ A stale OnReconnectFailed is not a late notice, it is a wrong
            // one. If a handler on the transition above has already begun
            // attempt N+1, this event says attempt N failed while N+1 is in
            // flight — and the action it documents is a full Connect(), which
            // from Connecting warns and does nothing. The application has
            // already been told, and has already acted.
            if (!AttemptIsStillLive(epoch))
            {
                LogDebug("Reconnect loop exhausted, but a new attempt began inside " +
                         "OnDisconnected — OnReconnectFailed belongs to the attempt that " +
                         "ended and is not raised against the one that replaced it.");
                yield break;
            }

            SafeRaise(OnReconnectFailed, attempts, nameof(OnReconnectFailed));
        }

        /// <summary>
        /// Drive the per-attempt portion of a reconnect: reset protocol
        /// state, recreate managers, restart the network thread, and start
        /// the ReconnectInit + timeout coroutines.  Called once per
        /// iteration of <see cref="ReconnectLoopCoroutine"/>.
        /// </summary>
        private void StartReconnectAttempt()
        {
            // Reset per-connection protocol state — same pattern as Connect().
            _packetBuilder = new PacketBuilder();
            _sessionKeyStore.ResetOutboundNonceCounter();
            System.Threading.Interlocked.Exchange(ref _outboundAppSequenceCounter, -1L);
            _sessionKeyStore.ResetLastInboundAppSequence();

            // Recreate Room/Spawn managers with identical event wiring to Connect().
            RecreateRoomAndSpawnManagers();

            // Arm pre-session capture, same as Connect(). Stop() handles any
            // prior normal- or pre-session hook left over from the previous attempt.
            _diagnosticsUplink?.Stop();
            _diagnosticsUplink = null;
            if (_settings != null && _settings.enableDiagnosticsUplink)
            {
                _diagnosticsUplink = new Diagnostics.DiagnosticsUplink(_settings, _packetBuilder);
                _diagnosticsUplink.StartPreSessionCapture();
            }

            // Defensive disposal — same rationale as Connect(): the state
            // machine guarantees Cleanup ran before reaching Disconnected,
            // but disposing here is a no-op when the fields are null and
            // protects ephemeral key material against a future state-flow
            // refactor that misses Cleanup on one of the paths.
            _handshakeHandler?.Dispose();
            _sessionKeyStore.DisposeKeys();

            // Drop the previous session's replay-window bitmap.  The new
            // session derives fresh keys whose nonce stream restarts at zero,
            // so a stale bitmap from the previous session would block
            // legitimate low-counter packets after the new SessionAck.
            // OnChallenge re-allocates the window after key derivation; we
            // null the field here so the receive path's strict null-reject
            // remains the only way an AEAD frame can be observed before the
            // new keys are in place.
            _sessionKeyStore.DropReplayWindow();

            _handshakeHandler = new HandshakeHandler();
            ResetChallengeAdmission();

            // Re-arm the network thread, under this attempt's own epoch.  If a
            // previous attempt's timeout or transport error stopped and nulled
            // the thread, this recreates it.
            StartConnectionAttempt();

            // Kick off the reconnect coroutine — waits for transport bind, sends
            // ReconnectInit, then the existing Challenge/HandshakeResponse/SessionAck
            // handlers complete the flow exactly as for a fresh Connect().
            _connectCoroutine = StartCoroutine(ReconnectInitCoroutine());
            _timeoutCoroutine = StartCoroutine(ConnectionTimeoutRoutine());
        }

        /// <summary>
        /// Lazily reconstruct the network thread when a previous connection
        /// attempt's timeout or handshake failure tore it down.  Shared by
        /// <see cref="Connect"/> and the reconnect path so both reconstruct the
        /// thread identically.  The transport instance survives across attempts
        /// (UdpTransport.Connect is re-callable; Disconnect closes the socket
        /// without disposing the wrapper) and is re-bound by the new RunLoop —
        /// for as long as the factory it was built through is the one
        /// installed, and the shaper it was wrapped by is the one installed.
        /// An attempt that begins under another factory or another shaper, or
        /// under none, retires the thread and the transport together and runs
        /// on the installed factory's transport instead, which is the moment
        /// <see cref="SetTransportFactory"/> documents a change as taking
        /// effect.  Manager-lifetime subscriptions such as the state-sync data
        /// handler are NOT touched here — they were wired once in
        /// InitialiseNetwork and must not be re-attached per attempt or every
        /// inbound packet would multi-dispatch.
        /// </summary>
        private void EnsureNetworkThreadReady()
        {
            // Decided ahead of the thread question, because a session the
            // application ended leaves its thread stopped and still held: an
            // attempt that finds one restarts it over the transport it was
            // constructed with, and the factory would never be asked.  A
            // transport built through a factory other than the installed one
            // — including none — is retired with the thread that owns it, and
            // the field is released before the transport is disposed, so a
            // Dispose that throws leaves nothing disposed in it for the next
            // attempt to start on.
            if (_transport != null
                && (!object.Equals(_transportBuiltBy, _transportFactory)
                    || !object.Equals(_transportShapedBy, _transportShaper)))
            {
                RetireNetworkThread();
                RTMPE.Transport.NetworkTransport replaced = _transport;
                _transport = null;
                try { replaced.Dispose(); }
                catch (Exception ex)
                {
                    if (ShouldWarn(ref _lastTransportDisposeThrewWarnTicks))
                        RtmpeLog.Warning(
                            $"[RTMPE] The transport being replaced threw {ex.GetType().Name} " +
                            $"from Dispose: {ex.Message}. The attempt continues on the installed " +
                            "factory's transport.");
                }
            }

            if (_networkThread != null) return;
            if (_transport == null)
                _transport = BuildTransport();
            _networkThread = new NetworkThread(
                _transport,
                _settings.networkThreadBufferBytes,
                _settings.sendQueueMaxItems);
            _networkThread.OnPacketReceivedRented += HandlePacketReceivedRented;
            _networkThread.OnError                += HandleTransportError;
        }

        /// <summary>
        /// The transport this session runs on: the installed factory's, or the
        /// built-in UDP one.
        /// </summary>
        /// <remarks>
        /// ⛔ One statement of the rule, reached from both construction sites.
        /// They had a copy each and the copies disagreed — Awake consulted the
        /// factory and the reconnect path did not — which is the shape a
        /// documented contract fails in without a compile error anywhere.
        ///
        /// A factory that throws or answers null falls back rather than
        /// refusing: it is an extension point, and a session that cannot start
        /// at all is a worse answer than one on the default transport, provided
        /// the substitution is said out loud. It is.
        /// </remarks>
        private RTMPE.Transport.NetworkTransport BuildTransport()
        {
            // One reading of the installed factory for the call and the record
            // alike; the field is static and may be reassigned from anywhere.
            TransportFactoryFn factory = _transportFactory;

            if (factory != null)
            {
                RTMPE.Transport.NetworkTransport built = null;
                try { built = factory(_settings); }
                catch (Exception ex)
                {
                    if (ShouldWarn(ref _lastTransportFactoryThrewWarnTicks))
                        RtmpeLog.Error(
                            $"[RTMPE] Custom transport factory threw {ex.GetType().Name}: " +
                            $"{ex.Message}. Falling back to the built-in UdpTransport for this " +
                            "session.");
                }

                if (built != null)
                {
                    _transportBuiltBy = factory;
                    return Shape(built);
                }

                if (ShouldWarn(ref _lastTransportFactoryNullWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Custom transport factory returned null; " +
                        "falling back to the built-in UdpTransport.");
            }

            // The built-in transport is recorded as built through no factory,
            // so an installed one that failed is asked again on the next
            // attempt rather than silently replaced for the manager's life.
            _transportBuiltBy = null;
            return Shape(new UdpTransport(
                _settings.serverHost,
                _settings.serverPort,
                _settings.sendBufferBytes,
                _settings.receiveBufferBytes));
        }

        /// <summary>
        /// The transport under the installed shaper, or as built when none is
        /// installed — one statement of the second seam, reached from both
        /// arms of <see cref="BuildTransport"/> so a shaper wraps the built-in
        /// transport and a factory's alike.
        /// </summary>
        /// <remarks>
        /// A shaper that throws or answers null leaves the session on the
        /// transport it was given, said out loud, for the reason the factory's
        /// refusals are treated so: a bench that cannot arm is a worse answer
        /// than a session without it.  The record is left empty then, so the
        /// installed shaper is asked again at the next attempt rather than
        /// silently skipped for the manager's life.
        /// </remarks>
        private RTMPE.Transport.NetworkTransport Shape(RTMPE.Transport.NetworkTransport built)
        {
            TransportShaperFn shaper = _transportShaper;
            _transportShapedBy = null;
            if (shaper == null) return built;

            RTMPE.Transport.NetworkTransport shaped = null;
            try { shaped = shaper(built); }
            catch (Exception ex)
            {
                if (ShouldWarn(ref _lastTransportShaperThrewWarnTicks))
                    RtmpeLog.Error(
                        $"[RTMPE] The transport shaper threw {ex.GetType().Name}: {ex.Message}. " +
                        "This session runs on the unshaped transport.");
            }

            if (shaped == null)
            {
                if (ShouldWarn(ref _lastTransportShaperNullWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] The transport shaper returned null; this session runs on the " +
                        "unshaped transport.");
                return built;
            }

            _transportShapedBy = shaper;
            return shaped;
        }

        // The transport factory's two refusals, a budget each.  A factory that
        // fails is asked again on every attempt, so one that fails on every
        // attempt is reported at most once a second rather than once an attempt
        // — and a flood of one refusal must not decide whether the other is ever
        // printed: "threw" and "answered null" are different faults to fix.
        // The third budget is the replaced transport's Dispose: a fault of the
        // integrator's transport that must not end the attempt replacing it.
        private long _lastTransportFactoryThrewWarnTicks;
        private long _lastTransportFactoryNullWarnTicks;
        private long _lastTransportDisposeThrewWarnTicks;
        // And the shaper's two, for the same reason.
        private long _lastTransportShaperThrewWarnTicks;
        private long _lastTransportShaperNullWarnTicks;


        /// <summary>
        /// Whether <paramref name="epoch"/> still names the live connection
        /// attempt.
        /// </summary>
        /// <remarks>
        /// Every teardown in this class hands control to application code before
        /// it has finished — <c>OnConnectionFailed</c>, then <c>OnDisconnected</c>
        /// — and <see cref="SafeRaise"/> runs those handlers on this call stack.
        /// An application that responds by disconnecting and reconnecting, which
        /// is the pattern the documentation prescribes, therefore returns into
        /// the middle of a teardown that is about to stop the coroutines, the
        /// network thread and the session state of the attempt it just started.
        /// Asking this after each raise is what separates the two.
        /// </remarks>
        private bool AttemptIsStillLive(int epoch) =>
            epoch == System.Threading.Volatile.Read(ref _connectionAttemptEpoch);

        /// <summary>
        /// Whether the announcement this path just made still stands: the
        /// manager is in the state it announced, and that announcement is the
        /// only transition since <paramref name="serialBefore"/> was read —
        /// i.e. no handler on the transition ended the attempt, or ended it and
        /// began another, before this one was armed.
        /// </summary>
        /// <remarks>
        /// S4-36.  <c>TransitionTo</c> raises <c>OnStateChanged</c> synchronously, so
        /// every statement after one is running with application code already
        /// behind it.  The callers go on to start a network thread and
        /// coroutines, which a teardown in between makes orphans: a socket read
        /// loop and a watchdog attached to a manager in
        /// <see cref="NetworkState.Disconnected"/>.
        /// <para>
        /// The state alone does not answer this.  A handler that calls
        /// <c>Disconnect()</c> and then <c>Connect()</c> leaves the manager in
        /// Connecting with an attempt of its own already armed, and a caller
        /// that read only the state would arm a second one over it.  The
        /// transition count tells the two apart: the caller's own announcement
        /// is one transition, and anything more is a handler's.
        /// </para>
        /// <para>
        /// ⛔ Not the attempt epoch, which cannot answer this — it is incremented by
        /// <see cref="StartConnectionAttempt"/>, below every call site, so a nested
        /// <c>Disconnect()</c> leaves it unchanged and the check reads "live".
        /// </para>
        /// <para>
        /// Reported once per second rather than silently: a handler that cancels
        /// the call it was raised by is a fault in the application's own wiring,
        /// and the alternative — a <c>Connect()</c> that returns having done
        /// nothing — is the silence this class of finding is about.
        /// </para>
        /// </remarks>
        private bool AttemptSurvivedItsOwnAnnouncement(NetworkState announced, string api, int serialBefore)
        {
            int transitions = _transitionSerial - serialBefore;
            if (_state == announced && transitions <= 1) return true;

            if (ShouldWarn(ref _lastAttemptCancelledByHandlerWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] NetworkManager.{api}: abandoned before it was armed — a handler on " +
                    $"the transition to {announced}, or on the one before it, moved the manager " +
                    $"({transitions} transitions since, now {_state}). The attempt is not started; " +
                    "call " + api + "() again from outside OnStateChanged if that was not intended.");

            return false;
        }

        // A fault thrown out of the teardown's despawn sweep (S4-28).  Its own
        // budget: a pool that throws on Release throws on every object, and a
        // scene-reloading title reaches this once per reload.
        private long _lastTeardownDespawnFaultWarnTicks;

        // Its own budget: this is an application-wiring fault, and a title whose
        // handler disconnects on every transition would otherwise write a line per
        // attempt for as long as it keeps trying.
        private long _lastAttemptCancelledByHandlerWarnTicks;

        /// <summary>
        /// Stop the network thread and give up the field, detaching its handlers
        /// first.  A thread whose <c>Stop()</c> could not join it outlives this
        /// call, and its <c>OnError</c> would otherwise still reach a manager
        /// that has moved on — <see cref="HandleTransportError"/> stamps a fault
        /// with the epoch that is live when it fires, so a fault belonging to an
        /// abandoned instance would be admitted as though it belonged to the
        /// attempt running now.  Detached before the stop, because the stop
        /// itself closes the socket and can be what raises the fault.
        /// </summary>
        private void RetireNetworkThread()
        {
            if (_networkThread == null) return;
            _networkThread.OnPacketReceivedRented -= HandlePacketReceivedRented;
            _networkThread.OnError                -= HandleTransportError;
            _networkThread.Stop();
            // ⛔ Stop() alone is not a hand-over.  It retires the loop by
            // generation only when a join times out, and it does not reach that
            // code at all when the loop is ending on a transport fault — the
            // fault clears the run flags before it raises, so Stop() returns on
            // its first line.  The loop is then still unwinding and still owns
            // the transport this manager is about to hand to a fresh instance,
            // and it closes that socket on the way out.
            _networkThread.Retire();
            _networkThread = null;
        }

        /// <summary>
        /// Arm the network thread for a fresh attempt and stamp that attempt
        /// with its own epoch.  Both entry points — <see cref="Connect"/> and
        /// the reconnect path — go through here, which is what lets work raised
        /// by one attempt tell that it is describing a session already over.
        /// </summary>
        private void StartConnectionAttempt()
        {
            System.Threading.Interlocked.Increment(ref _connectionAttemptEpoch);
            EnsureNetworkThreadReady();
            _networkThread.Start();
        }

        /// <summary>
        /// Closes the connection: tells the server (when connected), stops the network thread
        /// and clears the session, including the reconnect token and the last-room snapshot.
        /// Raises <see cref="OnDisconnected"/> with <see cref="DisconnectReason.ClientRequest"/>.
        /// </summary>
        /// <remarks>
        /// <para>Also stops reconnect attempts, including attempts waiting between tries in
        /// <see cref="NetworkState.Disconnected"/>. In that case <see cref="OnDisconnected"/>
        /// is raised a second time, with <see cref="DisconnectReason.ClientRequest"/>, after the
        /// one the failed attempt raised.</para>
        /// <para>Does nothing when the state is already <see cref="NetworkState.Disconnected"/>
        /// or <see cref="NetworkState.Disconnecting"/> and no reconnect is running, and after
        /// the manager has been destroyed.</para>
        /// </remarks>
        public void Disconnect() => DisconnectWithReason(DisconnectReason.ClientRequest);

        // Shared teardown path used by both user-initiated and internal disconnects.
        // reason is forwarded to OnDisconnected so callers can distinguish nonce
        // exhaustion from a user-initiated or server-initiated disconnect.
        private void DisconnectWithReason(DisconnectReason reason)
        {
            // ⛔ First, because everything below it touches state Cleanup has
            // released.  OnDestroy calls StopAllCoroutines before Cleanup, which
            // kills the reconnect loop WITHOUT running the line that nulls its
            // handle — so the witness below would read a stale non-null
            // Coroutine forever (Cleanup now clears it, and this guard is the
            // second half of that pair), and
            // StopCoroutine on a destroyed MonoBehaviour throws.  The old guard
            // hid this by accident: a manager torn down mid-backoff sits in
            // Disconnected, and returning there was a safe exit as much as an
            // idle check.  Removing the accident means stating the real rule,
            // which this class already states one file over in HandleTransportError.
            if (_cleanedUp) return;

            // ⚠️ "Already Disconnected" is not the same as "idle", and reading it
            // that way made Disconnect() a no-op for the whole reconnect backoff.
            //
            // ReconnectLoopCoroutine waits out its inter-attempt delay in
            // Disconnected — that is where the per-attempt timeout leaves it, and
            // the loop's own comment says so before it transitions back.  So a
            // caller who gave up mid-backoff got nothing: the guard returned
            // above the loop retirement and above ClearSessionData, the token
            // survived, and the next iteration transitioned to Reconnecting and
            // rebuilt the session the caller had just asked to end.
            //
            // A live reconnect loop is therefore work to stop, whatever the
            // state reads.  🔑 The five other call sites cannot reach the new arm,
            // but ⚠️ not for the reason a first reading suggests: `SessionAck` is
            // deliberately exempt from RequiresActiveSession, and OnSessionAck
            // carries no state gate of its own.  What makes those four
            // unreachable during a backoff is that the previous attempt's
            // timeout stopped and nulled the network thread, so no packet is
            // being dispatched at all; nonce exhaustion is on the outbound path
            // and needs installed session keys, which ClearSessionData has
            // dropped.  Both are properties of the teardown, not preconditions
            // anything asserts — a change that kept the transport alive across
            // the gap would make this paragraph false with no test to notice.
            bool reconnectPending = _reconnectLoopCoroutine != null;

            if (!reconnectPending
                && (_state == NetworkState.Disconnected
                    || _state == NetworkState.Disconnecting)) return;

            bool wasConnected = IsConnected;

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }
            if (_connectCoroutine != null)
            {
                StopCoroutine(_connectCoroutine);
                _connectCoroutine = null;
            }
            // A user-initiated disconnect must cancel any pending bounded
            // reconnect retry so we don't see Reconnecting → Disconnected
            // → Reconnecting flap after the user has explicitly given up.
            if (_reconnectLoopCoroutine != null)
            {
                StopCoroutine(_reconnectLoopCoroutine);
                _reconnectLoopCoroutine = null;
            }

            TransitionTo(NetworkState.Disconnecting);

            if (wasConnected)
                SendDisconnect();

            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, reason);
        }

        /// <summary>
        /// Low-level: sends <paramref name="data"/> as a complete RTMPE packet that the caller
        /// has already framed. Main thread only.
        /// </summary>
        /// <remarks>
        /// <para>The SDK encrypts the packet but does not frame it: a buffer shorter than a
        /// packet header is ignored, and the header the caller wrote decides how the packet is
        /// read. Send game messages with RPCs (<see cref="NetworkBehaviour.RPC"/>) and state
        /// with NetworkVariables instead.</para>
        /// <para>With <paramref name="reliable"/> set, the packet is retransmitted until the
        /// server acknowledges it. That takes effect only when
        /// <see cref="NetworkSettings.EmitArqSequence"/> is on and the server supports it;
        /// otherwise the packet is sent once and a one-time warning is logged. It is also sent
        /// once when the reliable window is full (see <see cref="ReliableControlRefusedCount"/>).</para>
        /// <para>Logs a warning and does nothing when not connected. The data is copied, so
        /// the buffer can be reused at once. The call reads session state without locking;
        /// to send from another thread, queue the call with
        /// <see cref="RTMPE.Threading.MainThreadDispatcher"/>.</para>
        /// </remarks>
        /// <param name="data">The complete packet, header included.</param>
        /// <param name="reliable">Whether to retransmit the packet until it is acknowledged.</param>
        public void Send(byte[] data, bool reliable = false)
        {
            if (!IsConnected)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.Send: cannot send while not connected.");
                return;
            }

            if (data == null || data.Length == 0) return;

            FlushStateBeforeAnEnding(data);

            // _negotiatedPeerCaps is read below without synchronisation — the
            // field is written only on the main thread (inside OnSessionAck,
            // dispatched by MainThreadDispatcher), so a call from a worker is a
            // data race on session state.
            //
            // Reported rather than asserted: Debug.Assert is compiled out of a
            // release player, which is the build where a race is least likely to
            // be noticed and most expensive to diagnose.  Rate-gated, because
            // the mistake is systematic — a caller on the wrong thread is on it
            // every frame — and one line a second is enough to find it.
            // Reported rather than asserted: Debug.Assert is compiled out of a
            // release player, and so is the AEAD path's own tripwire further
            // down (it carries [Conditional]) — which leaves the build where a
            // race is least likely to be noticed with no report at all.
            //
            // MainThreadIsKnown first, because IsMainThread is also false before
            // the dispatcher has identified the main thread; that is not a
            // mistake, and outside a Unity player it is the normal state.
            // Rate-gated, because a caller on the wrong thread is on it every
            // frame and this runs per send.
            if (RTMPE.Threading.MainThreadDispatcher.MainThreadIsKnown
                && !RTMPE.Threading.MainThreadDispatcher.IsMainThread
                && WarnGate.ShouldEmit(ref _lastSendOffMainThreadWarnTicks))
            {
                Debug.LogError(
                    "[RTMPE] NetworkManager.Send must be called from the Unity main thread — " +
                    "negotiated session state is read without synchronisation.  Marshal the "  +
                    "call through MainThreadDispatcher.Enqueue.");
            }

            // Copy so the caller can safely reuse or discard its buffer after
            // this call, which matches the original NetworkThread.Send(copy)
            // contract.  When reliable=true the same copy is parked in the
            // retransmit table for re-emission on RTO expiry.
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);

            // Application-layer ARQ requires three predicates to line up:
            //   • caller intent (`reliable: true`)
            //   • deployment opt-in (`NetworkSettings.EmitArqSequence`) —
            //     instructs the SDK to emit the 4-byte ARQ sub-header on
            //     the wire so the gateway can address its DataAck
            //   • negotiated peer capability (`CapabilityFlags.ArqAck` in
            //     `_negotiatedPeerCaps`) — the gateway has promised to
            //     emit DataAck for reliable frames
            // Each predicate has its own actionable when missing, so the
            // two deployment-level causes route to dedicated warn-once
            // advisories below.
            bool emitArqSequence = _settings != null && _settings.EmitArqSequence;
            bool peerSupportsArqAck =
                (_negotiatedPeerCaps & RTMPE.Core.Protocol.CapabilityFlags.ArqAck) != 0;

            // Local-side advisory: caller asked for reliability but the
            // SDK is configured not to emit the ARQ sub-header.  Fires
            // once regardless of peer cap state — fixing the local opt-in
            // is the prerequisite either way.
            RTMPE.Core.Diagnostics.ReliableSendAdvisory.NotifyIfDowngrading(
                emitArqSequence:   emitArqSequence,
                reliableRequested: reliable);

            // Peer-side advisory: local opt-in is on but the session's
            // negotiated cap set excludes ArqAck (legacy gateway, or
            // operator left RTMPE_ADVERTISE_ARQ_CAP off, or the active
            // transport's gateway path intentionally suppresses the cap
            // — KCP / WebSocket).  The advisory's internal predicate
            // requires `emitArqSequence == true`, so the two advisories
            // are mutually exclusive on first emission for a given root
            // cause.
            RTMPE.Core.Diagnostics.PeerCapabilityAdvisory.NotifyIfArqUnavailable(
                emitArqSequence:    emitArqSequence,
                peerSupportsArqAck: peerSupportsArqAck,
                reliableRequested:  reliable);

            bool reliableEnabled = reliable && emitArqSequence && peerSupportsArqAck;

            if (reliableEnabled)
            {
                if (_outboundReliableChannel.TryRegisterOutbound(
                        copy,
                        UnityEngine.Time.unscaledTimeAsDouble,
                        out uint arqSeq))
                {
                    // The retransmit table holds the original packet bytes; a
                    // resend re-runs EncryptAndSendInternal with the same arqSeq
                    // so the receiver observes a stable sequence across retries.
                    EncryptAndSendInternal(copy, hasFixedArqSeq: true, fixedArqSeq: arqSeq);
                    return;
                }

                // Reliability is fully engaged for this session, but the whole
                // in-flight window is full — state replication stops short of
                // it by ReservedForControl slots, so at least that many of the
                // frames in flight are control's own — and the packet ships once
                // with no retry.  Reported, with
                // the running count, by an advisory that gates itself to one line
                // a second while the condition lasts: a distinct, individually
                // actionable cause from the predicate-level downgrades (the link
                // is saturated, not misconfigured), and one that recurs, which a
                // once-per-process line would show only at its first occurrence.
                RTMPE.Core.Diagnostics.ReliableSaturationAdvisory.NotifyOnSaturation(
                    _outboundReliableChannel.ControlRefusedCount);
            }

            // Best-effort path: reliability was not engaged for this send, or the
            // retransmit window was saturated above.  The packet still goes out
            // once; a loss on raw UDP is not recovered.
            EncryptAndSend(copy);
        }

        // The three predicates a reliable send needs, as Send reads them.
        private bool ReliabilityEngaged =>
            _settings != null && _settings.EmitArqSequence
            && (_negotiatedPeerCaps & RTMPE.Core.Protocol.CapabilityFlags.ArqAck) != 0;

        /// <summary>
        /// Whether a replication frame offered now would be registered:
        /// reliability is not engaged, so it ships best-effort, or the
        /// replication share of the ARQ window has room.  A probe for the
        /// batching collector, which must decide before it builds.
        /// </summary>
        internal bool ReplicationWindowOpen =>
            !ReliabilityEngaged
            || _outboundReliableChannel.CanRegister(ReliableTraffic.Replication);

        /// <summary>
        /// The probe as a flush asks it — before building, once per component:
        /// a refusal here IS the deferral (the variables stay dirty for the
        /// next tick), so it is counted and reported as one.  The probe above
        /// stays pure for a reader that only wants the answer.
        /// </summary>
        private bool ReplicationWindowOpenForFlush()
        {
            if (ReplicationWindowOpen) return true;
            _outboundReliableChannel.NoteReplicationDeferral();
            RTMPE.Core.Diagnostics.ReliableSaturationAdvisory.NotifyOnReplicationDeferral(
                _outboundReliableChannel.ReplicationDeferredCount);
            return false;
        }

        /// <summary>
        /// Send a state-replication frame — a NetworkVariable flush — with
        /// acknowledged delivery when the session has it, drawing only on the
        /// replication share of the ARQ window.  Returns <see langword="true"/>
        /// when the frame went out; <see langword="false"/> when the share was
        /// in flight and the frame was NOT sent, so the caller keeps its
        /// variables dirty and offers them again next tick, when they describe
        /// the state then.
        /// </summary>
        /// <remarks>
        /// The difference from <see cref="Send(byte[], bool)"/> is what a full
        /// window means.  A control frame cannot wait — its content is not
        /// superseded by the next one — so it ships once without a retransmit
        /// entry rather than not at all.  A replication frame can: the next
        /// flush carries the same variables' current values, and the retransmit
        /// entry the deferred frame would have taken is one a spawn or a
        /// despawn may need in the meantime.  Deferral is therefore the
        /// correct answer here and would be the wrong one there.  Without ARQ
        /// the frame ships once, as every send does, and is reported as taken.
        /// <para>The refused-registration arm is the race defence, not the
        /// steady state: every flush asks <see cref="ReplicationWindowOpenForFlush"/>
        /// before it builds, and on the main thread nothing takes a slot
        /// between that answer and the registration here, so in practice a
        /// deferral is decided and counted there.  The arm stays because the
        /// registration is the authority and the probe is a courtesy.</para>
        /// </remarks>
        internal bool TrySendReplication(byte[] packet)
        {
            // Not sent, and said so: the variables stay dirty for the session
            // that can carry them.  Unreachable from the flush, which runs only
            // in a room, but a verdict of "taken" here would be a lie.
            if (!IsConnected) return false;
            if (packet == null || packet.Length == 0) return true;

            if (!ReliabilityEngaged)
            {
                EncryptAndSend(packet);
                return true;
            }

            if (!_outboundReliableChannel.TryRegisterOutbound(
                    packet,
                    UnityEngine.Time.unscaledTimeAsDouble,
                    out uint arqSeq,
                    ReliableTraffic.Replication))
            {
                RTMPE.Core.Diagnostics.ReliableSaturationAdvisory.NotifyOnReplicationDeferral(
                    _outboundReliableChannel.ReplicationDeferredCount);
                return false;
            }

            EncryptAndSendInternal(packet, hasFixedArqSeq: true, fixedArqSeq: arqSeq);
            return true;
        }

        /// <summary>
        /// Send a replication frame that cannot wait: registered on the
        /// replication share when the share has room, shipped once without a
        /// retransmit entry when it has not — the downgrade the reservation
        /// keeps off control frames, on the one replication path that cannot
        /// defer.  For a coalesced batch, and a batch's per-entry fallback,
        /// whose entries were taken from their behaviours when the batch
        /// opened and so cannot be offered again.
        /// </summary>
        internal void SendReplicationOrShipOnce(byte[] packet)
        {
            if (!IsConnected) return;
            if (packet == null || packet.Length == 0) return;

            if (!ReliabilityEngaged)
            {
                EncryptAndSend(packet);
                return;
            }

            if (_outboundReliableChannel.TryRegisterOutbound(
                    packet,
                    UnityEngine.Time.unscaledTimeAsDouble,
                    out uint arqSeq,
                    ReliableTraffic.Replication))
            {
                EncryptAndSendInternal(packet, hasFixedArqSeq: true, fixedArqSeq: arqSeq);
                return;
            }

            _outboundReliableChannel.NoteReplicationDowngrade();
            EncryptAndSend(packet);
        }

        /// <summary>
        /// Sends the frame's queued transforms ahead of a packet that ends what
        /// they describe — a despawn, a room leave (audit P3-E3 review).  Each
        /// transform used to leave from its component's <c>Update</c>, ahead of
        /// anything sent later in the frame; queued for <c>LateUpdate</c>, the
        /// last transform of a despawned object or of a room being left would
        /// reach the server after the packet that ended it.
        /// </summary>
        private void FlushStateBeforeAnEnding(byte[] packet)
        {
            if (packet == null || packet.Length <= PacketProtocol.OFFSET_TYPE) return;
            byte type = packet[PacketProtocol.OFFSET_TYPE];
            if (type == (byte)PacketType.Despawn || type == (byte)PacketType.RoomLeave)
                FlushStateSyncBatch();
        }

        /// <summary>
        /// Send a packet this class's own subsystems have just built, honouring
        /// the reliability the BUILDER asked for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔑 The reliability decision is DERIVED from the packet, not passed
        /// alongside it.  `RoomManager`, `LobbyManager` and `MatchmakingManager`
        /// were each handed <c>packet =&gt; EncryptAndSend(packet)</c>, and
        /// <see cref="EncryptAndSend"/> hard-codes <c>hasFixedArqSeq: false</c> —
        /// so the AEAD pipeline cleared <c>FLAG_RELIABLE</c> from every one of
        /// them to keep the flags byte in lockstep with the sub-header it was
        /// not emitting.  Fourteen build sites asked for acknowledged delivery
        /// and none could ever get it, on any configuration, while both
        /// advisories written to report that downgrade sat in
        /// <see cref="Send(byte[],bool)"/> — the path those managers do not take.
        /// </para>
        /// <para>
        /// ⚠️ Reading the flag rather than taking a parameter is deliberate,
        /// and the reason is about tomorrow, not today: measured on 2026-08-27
        /// all fourteen sites set <see cref="PacketFlags.Reliable"/>, so a
        /// blanket <c>reliable: true</c> would currently be indistinguishable
        /// from this. What it would not survive is the first control operation
        /// that should NOT be acknowledged — and a per-call-site argument is a
        /// second declaration of a fact the builder already wrote into
        /// <c>packet[4]</c>, which is how two declarations of one fact drift.
        /// A builder that changes the flag gets the matching behaviour with no
        /// edit here.
        /// </para>
        /// <para>
        /// ⚠️ This method takes no copy — but the reliable path is not
        /// copy-free, and today that is every path. <see cref="Send(byte[],bool)"/>
        /// copies, and it must: the buffer it parks in the retransmit table has
        /// to survive until the RTO ladder is done with it, which is exactly
        /// when the caller is free to reuse its own. Only the unreliable branch
        /// hands the caller's array straight to
        /// <see cref="EncryptAndSend(byte[])"/>.
        /// </para>
        /// </remarks>
        internal void SendBuiltPacket(byte[] packet)
        {
            FlushStateBeforeAnEnding(packet);
            if (!PacketBuilder.AsksForReliableDelivery(packet))
            {
                EncryptAndSend(packet);
                return;
            }

            Send(packet, reliable: true);
        }

    }
}
