// RTMPE SDK — Runtime/Core/NetworkManager.Fields.cs
//
// Inspector-serialised fields, runtime state, telemetry counters, public properties.
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
using RTMPE.Core.Aead;
using RTMPE.Core.Rpc;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Inspector fields ───────────────────────────────────────────────────

        [SerializeField]
        [Tooltip("The NetworkSettings asset to connect with. Without one, a default with no " +
                 "server keys is used and every connection is refused.")]
        private NetworkSettings _settings;

        // ── Runtime state (main-thread only) ──────────────────────────────────
        private NetworkState      _state = NetworkState.Disconnected;
        private NetworkThread     _networkThread;
        private NetworkTransport  _transport;
        // The factory that built _transport, null when the built-in UDP
        // transport was — including a fallback taken because an installed
        // factory failed.  A connection attempt that begins under a factory
        // other than this one retires the thread and the transport and builds
        // afresh, which is how a factory installed or cleared between sessions
        // takes effect, and how a failed factory is asked again.
        private TransportFactoryFn _transportBuiltBy;

        /// <summary>
        /// Whether the transport this manager holds was built by an installed
        /// factory rather than being the SDK's own — the fact about the LIVE
        /// transport, where <see cref="HasCustomTransportFactory"/> is the fact
        /// about the next attempt.  Read by the Editor's play-mode observer,
        /// which attributes nothing a session established to a deployed
        /// gateway when the session ran on a transport the project supplied.
        /// </summary>
        internal bool TransportIsCustom => _transportBuiltBy != null;

        // The shaper the live transport was wrapped by, or null: the same
        // record for the second seam, compared against the installed shaper
        // at the next attempt for the same reason.
        private TransportShaperFn _transportShapedBy;

        /// <summary>
        /// Whether the transport this manager holds is wrapped by an installed
        /// shaper — the Editor's Link Simulator — so that what the session
        /// experiences is a shaped link rather than the network.  Read by the
        /// play-mode observer, which records nothing from such a session: a
        /// check that failed under a link the bench made worse says nothing
        /// about the game, and one that passed says nothing the unshaped link
        /// would not.
        /// </summary>
        internal bool TransportIsShaped => _transportShapedBy != null;

        /// <summary>
        /// The live transport as the link simulator, when the session runs
        /// under one; <see langword="null"/> otherwise.  The Link Simulator
        /// panel reads its lanes from here — the manager's transport, not a
        /// reference the bench kept.
        /// </summary>
        internal SimulatedLinkTransport LiveSimulatedLink => _transport as SimulatedLinkTransport;

        private MainThreadDispatcher _dispatcher;
        private Coroutine         _timeoutCoroutine;
        // Which connect or reconnect attempt is the live one.  Taken by
        // StartConnectionAttempt immediately before the network thread is
        // started, and carried by work that is raised on one attempt but can
        // only run on a later frame — by which time an OnDisconnected handler
        // that calls Connect(), the pattern the documentation prescribes, may
        // have made a different attempt the live one.
        private int               _connectionAttemptEpoch;
        // How many state transitions the manager has made.  A path that
        // announces a state and then arms an attempt reads this before the
        // announcement and asks, after it, that nothing but its own transition
        // happened since (nothing at all, where the state was already the one
        // announced).  The state alone cannot tell an announcement that stood
        // from one a handler undid and re-made: Disconnect() then Connect()
        // inside OnStateChanged leaves the manager in Connecting with a second
        // attempt already armed.
        private int               _transitionSerial;
        private Coroutine         _connectCoroutine;

        // Most recently observed gateway-side per-session backpressure (0–255).
        // Updated on every HeartbeatAck.  Read from any thread via Volatile.Read
        // because production code on the receive path writes from the network
        // thread and app code reads from the main thread.
        // 0 = bucket full (no throttling); 255 = bucket empty (max throttling).
        private int _serverBackpressure;

        // Session tokens populated on SessionAck
        // (crypto_id, session keys, replay window, and atomic counters now
        // live as a cohesive bundle in _sessionKeyStore — see further down.)
        private string _jwtToken;
        private string _reconnectToken;
        /// <summary>
        /// S4-19 — what the gateway said about how long <see cref="_reconnectToken"/>
        /// is good for, against this client's own clock.  Written wherever the
        /// token is, and forgotten wherever the token is, so the statement can
        /// never be read against a different credential.  Read by the reconnect
        /// ladder to decide how many attempts an over-age token is worth.
        /// </summary>
        private readonly ReconnectTokenLife _reconnectTokenLife = new ReconnectTokenLife();
        /// <summary>N-8: 32-byte HMAC key for IP-migration proofs, derived alongside session keys.</summary>
        private byte[] _ipMigrationKey;
        /// <summary>
        /// 32-byte AEAD key (HKDF info suffix <c>\x03</c>) used to decrypt the
        /// SessionAck payload when the handshake negotiated
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck"/>
        /// and the SessionAck therefore arrives carrying
        /// <see cref="PacketFlags.Encrypted"/>.
        /// </summary>
        private byte[] _sessionAckKey;

        /// <summary>
        /// The 32-byte Ed25519 server static identity public key, captured
        /// once the handshake <see cref="PacketType.Challenge"/> signature
        /// has been verified against it.  When the gateway advertises
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.IdentitySignedJwt"/>
        /// the SessionAck handler verifies the JWT signature against this
        /// key rather than relying on out-of-band NetworkSettings key
        /// configuration.  How strong an anchor it is depends on the
        /// server-key pinning mode that captured it.
        /// </summary>
        private byte[] _serverIdentityPublicKey;

        /// <summary>
        /// Owns the per-session ARQ sequence space.  Allocates the 4-byte
        /// sub-header value emitted on the wire when
        /// <see cref="NetworkSettings.EmitArqSequence"/> is enabled and the
        /// outbound packet carries <see cref="PacketFlags.Reliable"/>.  The
        /// retransmit table will be wired in once gateway-side ACK plumbing
        /// lands; until then this instance only services sequence allocation.
        /// </summary>
        private readonly ReliableChannel _outboundReliableChannel = new ReliableChannel();

        /// <summary>
        /// Session-effective capability bitmask negotiated against the
        /// gateway during the handshake.  Equal to
        /// <c>client_caps &amp; gateway_caps</c> where <c>client_caps</c>
        /// is the SDK's advertisement (currently mirrors
        /// <see cref="NetworkSettings.EmitArqSequence"/>) and
        /// <c>gateway_caps</c> is parsed from the <c>SessionAck</c> tail.
        /// Reset to <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/>
        /// alongside the rest of the session-bound AEAD state in
        /// <c>ClearSessionData</c>.
        /// </summary>
        /// <remarks>
        /// Single-threaded access by construction.  The write happens in
        /// <c>OnSessionAck</c>, which is dispatched onto the Unity main
        /// thread by <see cref="Infrastructure.Threading.MainThreadDispatcher"/>
        /// before any handler runs.  The three read sites — <c>Send</c>
        /// (<c>NetworkManager.Connection.cs</c>), the AEAD emit
        /// gate (<c>NetworkManager.AeadPipeline.cs</c>), and the
        /// retransmit tick (<c>NetworkManager.Lifecycle.cs</c>) —
        /// also run on the main thread (caller code, <c>Update</c>, and
        /// <c>Update</c> respectively), so no atomic / volatile read is
        /// required.  Mirrors the access pattern of the other
        /// session-installed fields (<c>_jwtToken</c>,
        /// <c>_reconnectToken</c>, <c>_localPlayerId</c>).
        /// </remarks>
        private RTMPE.Core.Protocol.CapabilityFlags _negotiatedPeerCaps =
            RTMPE.Core.Protocol.CapabilityFlags.None;

        /// <summary>
        /// Whether the gateway this session reached said the room's late-joiner
        /// replay follows an object's owner
        /// (<see cref="RTMPE.Core.Protocol.CapabilityFlags.ReplayFollowsOwner"/>).
        /// Read out of the <c>SessionAck</c> tail like the other one-way
        /// assertions, never advertised back, and cleared with the rest of the
        /// session's negotiated state.
        /// </summary>
        private bool _replayFollowsOwner;

        /// <summary>
        /// Whether this session's gateway said the room's replay follows an
        /// object's owner — what <see cref="RtmpeWorldAuthority"/> reads to keep
        /// a world's identity across a host migration instead of re-creating it.
        /// </summary>
        internal bool ReplayFollowsOwner => _replayFollowsOwner;

        /// <summary>
        /// Whether the gateway this session reached said it writes every
        /// relayed Enhanced RPC's <c>rpc_flags</c> byte itself
        /// (<see cref="RTMPE.Core.Protocol.CapabilityFlags.AttestedRpcCaller"/>).
        /// Read out of the <c>SessionAck</c> tail like the other one-way
        /// assertions and cleared with the rest of the session's negotiated
        /// state; <see cref="RTMPE.Rpc.EnhancedRpcVerifier.GatewayAttestsCaller"/>
        /// reads it.
        /// </summary>
        private bool _attestedRpcCaller;

        /// <summary>
        /// The gateway's send counter of the packet being dispatched — the AEAD
        /// nonce counter it stamped when it encrypted the packet for this
        /// session — or <c>-1</c> outside a dispatch and for a packet that
        /// carried none.  One order for everything the gateway sends this
        /// client, room events and the frames it relays alike: what tells a
        /// spawn the gateway sent before a player's departure — a straggler of
        /// the life that ended — from one it sent after
        /// (<see cref="SpawnManager.HoldForOwnersReturn"/>).
        /// Set once the packet is authenticated and admitted by the replay
        /// window, and cleared when its dispatch ends.
        /// </summary>
        private long _inboundSendCounter = -1;

        private ulong  _localPlayerId;
        private ulong  _currentRoomId;

        // Last-room snapshot (kept across a token-preserving ClearSessionData so
        // Reconnect() can rejoin automatically).  Both fields are cleared when
        // the reconnect token is cleared — they have no meaning without it.
        //
       // LastRoomId is the RoomInfo.RoomId (UUID string) returned by the room
        // service at join time; it survives session teardown precisely so the
        // SDK can feed it back into RoomManager.JoinRoom on a successful
        // ReconnectInit → SessionAck.  LastRoomCode is preserved as a fallback:
        // if the server has evicted the UUID but still knows the human-readable
        // code, apps can call JoinRoomByCode(LastRoomCode) from OnConnected.
        private string _lastRoomId;
        private string _lastRoomCode;

        // Room-level player identity (UUID string assigned by room service on JoinRoom).
        // Distinct from the gateway session ID stored in _localPlayerId (u64).
        // Populated by RoomManager via SetLocalRoomPlayerId() when JoinRoom succeeds,
        // or by tests via SetLocalPlayerStringId().
        // Used by NetworkBehaviour.IsOwner for object ownership checks.
        private string _localPlayerStringId;

        // RPC request correlation IDs are sourced from the CSPRNG-backed
        // RequestIdAllocator so that reply spoofing requires guessing a
        // 32-bit cryptographically-random value rather than a predictable
        // monotonic counter.

        // Monotone client-side tick counter for CSP (client-side prediction).
        // Incremented once per 30 Hz variable-flush cycle while in a room.
        // Wraps naturally at uint.MaxValue with no ill effect (all comparisons are
        // tick-relative within a small window so overflow is safe by design).
        private uint _localTick;

        // Outbound AEAD nonce counter (separate from the application sequence number
        // assigned by PacketBuilder).  Starts at -1L so the first Interlocked.Increment
        // returns 0, matching the gateway NonceGenerator which also starts at 0.
        // Reset to -1L in ClearSessionData() and in Connect() so every new session
        // begins with counter = 0.
        //
       // Using long avoids the int→uint cast ambiguity: the counter advances from
        // 0 to uint.MaxValue (4,294,967,295) and then hard-stops rather than
        // wrapping silently back to 0 and reusing nonces.
        //
       // These thresholds mirror the Rust gateway's SEQUENCE_EXHAUSTION_THRESHOLD
        // and NEAR_EXHAUSTION_MARGIN (nonce.rs) so the SDK terminates sessions
        // proactively before the gateway's replay-window would reject inbound traffic.
        private const long OutboundNonceExhaustionThreshold    = (long)uint.MaxValue + 1L; // 2^32
        private const long OutboundNonceNearExhaustionMargin   = 1_048_576L;               // ~9.7 h @ 30 Hz
        // _outboundNonceCounter is now part of _sessionKeyStore (see below).

        // Per-NetworkManager outbound gameplay-sequence counter.  Two
        // concurrent NetworkManager instances (e.g. a host + a spectator
        // co-resident in a single process) used to share a static counter
        // in GameplaySequencePrefix — their sequence streams interleaved
        // and the receiver's RFC-1982 ordering buffer rejected the alien
        // values as out-of-window.  The counter is now per-instance;
        // Interlocked.Increment keeps producer-thread safety inside a
        // single manager, and the unchecked cast preserves the original
        // RFC-1982 wraparound semantics.
        private int _outboundGameplaySequenceCounter;

        // Application-level monotonic sequence layered on top of the AEAD nonce
        // counter when NetworkSettings.preserveApplicationSequence is on.  The
        // wire header's Sequence field is overwritten by the nonce counter at
        // encryption time, so the application-level sequence travels in the
        // AAD instead — receivers can deduplicate or order without decrypting
        // first.  Starts at -1L so the first Increment yields 0 to match the
        // wire Sequence convention.  Reset alongside the nonce counter on every
        // session boundary.
        private long _outboundAppSequenceCounter = -1L;

        // _lastInboundAppSequence is now part of _sessionKeyStore (see below).
        // Exposed via the public LastInboundApplicationSequence property for
        // receivers that want to surface dedup / ordering metadata above the
        // AEAD layer.

        // ── Session crypto state ────────────────────────────────────────────
        //
        // _sessionKeyStore is the cohesive bundle of every per-session AEAD
        // input: the SessionKeys (encrypt + decrypt + N-8 + sessionAck), the
        // sliding-bitmap inbound replay window, the gateway-assigned crypto
        // identifier, the Interlocked outbound nonce counter, and the
        // monotonic Interlocked-CAS last-inbound-app-sequence.  Wrapping
        // these five pieces of state into one object makes the lifecycle
        // invariant explicit: either ALL of them are valid (active session)
        // or ALL are reset (no session) — mixing the two is the failure
        // mode that produces AEAD nonce reuse.  See
        // Runtime/Core/Aead/SessionKeyStore.cs for the API + threading
        // contract.
        private readonly SessionKeyStore _sessionKeyStore = new SessionKeyStore();

        // RpcReplayBuffer owns the ordering barrier / re-entry guard, the
        // historical (buffered) and live (pending) catch-up queues, the running
        // byte counter, and the dropped-count atomic, plus the bounded,
        // resumable drain. Centralising these in one class makes the ordering
        // invariant (historical events drain BEFORE live RPCs from the same
        // delivery window) reviewable in isolation. See
        // Runtime/Core/Rpc/RpcReplayBuffer.cs for the threading + caps.
        private readonly RpcReplayBuffer _rpcReplayBuffer = new RpcReplayBuffer();

        // Holds Spawn / Despawn packets that arrive before the join reply admits
        // the client to InRoom, then releases them in arrival order once the
        // room context exists (see EarlyObjectPacketBuffer).  The server replays
        // a late-joiner's catch-up object set as the session binds, which can
        // land just ahead of the reply; staging rather than dropping it keeps
        // those objects from being lost on the common first-join path.  Capacity
        // sits above the server's per-room spawn-buffer ceiling so a full
        // catch-up set is never partially shed.
        private const int EarlyObjectBufferCapacity = 2048;
        private readonly EarlyObjectPacketBuffer _earlyObjectBuffer =
            new EarlyObjectPacketBuffer(EarlyObjectBufferCapacity);

        // Holds VariableUpdate frames whose object is not spawned here yet —
        // the late-join snapshot the peers flush on `player_joined`, which the
        // Room Service publishes BEFORE it answers the joiner and BEFORE it
        // replays the room's objects to them — and applies them, in arrival
        // order, from inside the spawn that makes the object known (see
        // HeldVariableUpdates).  Its own hold rather than a fourth kind in the
        // staging buffer above: one resync burst is a frame per component per
        // owned object, and sharing the buffer's capacity with it would evict
        // the Spawns the snapshot is waiting for.  Same capacity, same reason.
        private const int HeldVariableUpdateCapacity = 2048;
        private readonly HeldVariableUpdates _heldVariableUpdates =
            new HeldVariableUpdates(HeldVariableUpdateCapacity);
        private long _lastHeldVariableUpdateEvictWarnTicks;
        private long _lastHeldVariableUpdateFaultWarnTicks;

        /// <summary>
        /// NetworkVariable updates held for objects that have not spawned on this client yet.
        /// </summary>
        /// <remarks>
        /// Non-zero while a room is being entered, and empty again once the room's objects
        /// have spawned. A value that stays high outside a room entry means a peer is writing
        /// variables for an object this client never spawns. The hold is bounded in updates
        /// and in bytes, and reaching a bound is reported at most once a second.
        /// </remarks>
        public int HeldVariableUpdateCount => _heldVariableUpdates.Count;

        /// <summary>
        /// The bytes the updates counted by <see cref="HeldVariableUpdateCount"/> occupy.
        /// </summary>
        public long HeldVariableUpdateBytes => _heldVariableUpdates.Bytes;

        /// <summary>
        /// Spawn, despawn and RPC packets held while this client is still entering a room.
        /// </summary>
        /// <remarks>
        /// Non-zero while a room is being entered; the packets are applied once the entry
        /// completes.
        /// </remarks>
        public int StagedCatchUpPacketCount => _earlyObjectBuffer.Count;

        /// <summary>
        /// The bytes the packets counted by <see cref="StagedCatchUpPacketCount"/> occupy.
        /// </summary>
        public long StagedCatchUpPacketBytes => _earlyObjectBuffer.Bytes;

        /// <summary>
        /// Spawns held for a player who left and is expected back: the server sent them after
        /// that player's departure, before the player's return reached this client.
        /// </summary>
        /// <remarks>
        /// Held for five seconds at most, and bounded to 32 spawns for one player and 128 in
        /// all.
        /// </remarks>
        public int SpawnsHeldForReturnCount => _spawnManager?.SpawnsHeldForReturnCount ?? 0;
        // The release's apply, converted once (a field initialiser may not
        // name an instance method, so it is bound on first use) — and its twin
        // for an object handed to this client before it held it.
        private System.Action<byte[]> _applyHeldVariableUpdateFrame;
        private System.Action<byte[]> _applyHeldVariableUpdateFrameToAHandedObject;

        // The sequence every room entry is begun on, by whichever manager
        // begins it.  Per component rather than per manager pair: the managers
        // are recreated on every connect and reconnect, and a value issued
        // before that must stay below every value issued after it, because a
        // staged frame carries the value and nothing else says which entry it
        // arrived for.
        private readonly RTMPE.Rooms.RoomEntrySequence _roomEntries =
            new RTMPE.Rooms.RoomEntrySequence();

        private HandshakeHandler _handshakeHandler;
        private PacketBuilder    _packetBuilder;

        // Persistent server-static-key pin store, used by the Trust-On-First-Use
        // mode of NetworkSettings.serverPinningMode.  Lazily initialised to a
        // MigratingPinStore on first read (hardened EncryptedFilePinStore primary
        // + lazy migration from the legacy PlayerPrefsPinStore — see SDK-H1 fix
        // in MigratingPinStore.cs); tests inject a custom store via
        // SetPinStore() before Connect() to avoid touching player storage.
        private IServerKeyPinStore _pinStore;

        // Channel-binding context for the current handshake.
        //
       // Holds the exact bytes the client emitted as the HandshakeInit
        // payload (the ChaCha20-Poly1305 envelope around the API key).  The
        // gateway hashes the same bytes and folds the SHA-256 into the
        // transcript it signs, so the SDK must keep them around between
        // Round 1 send and Round 1 reply receipt to recompute the matching
        // transcript.  Null in the reconnect flow (the absent-sentinel is
        // used instead — see HandshakeHandler.ValidateChallenge).
        private byte[]           _lastHandshakeInitCiphertext;

        // The exact Round-1 payload this attempt put on the wire: the sealed
        // API-key envelope on a fresh handshake, the ReconnectInit payload on a
        // reconnect.  SHA-256 over it is the transcript's client_init_hash, so
        // it is what makes a Challenge answer this attempt and no other.
        //
        // Held separately from _lastHandshakeInitCiphertext, which is Init-only
        // and drives the InitHashEcho capability and its echo bytes.  One field
        // serving both would put a control decision and a binding input on the
        // same value, and a reconnect — which owes a binding but not an echo —
        // is exactly where the two come apart.
        private byte[]           _lastRoundOnePayload;
        private HeartbeatManager _heartbeatManager;

        // SDK diagnostic uplink (Diagnostics 0x0C); null unless enabled in
        // NetworkSettings. Lifecycle mirrors _heartbeatManager.
        private Diagnostics.DiagnosticsUplink _diagnosticsUplink;

        // Room event timeline — a bounded FIFO of recent lifecycle events
        // (join, leave, player-join, player-leave) for editor diagnostics.
        // Written and read on the main thread only; no locking required.
        private const int RoomTimelineCapacity = 20;
        private readonly System.Collections.Generic.Queue<RoomTimelineEntry> _roomTimeline =
            new System.Collections.Generic.Queue<RoomTimelineEntry>(RoomTimelineCapacity);

        // Room management
        private RoomManager  _roomManager;

        // Lobby management
        private LobbyManager _lobbyManager;

        // Matchmaking
        private MatchmakingManager _matchmakingManager;

        // Spawn management
        private SpawnManager _spawnManager;

        // Scene-event serialization.  Unity's SceneManager.sceneUnloaded and
        // sceneLoaded callbacks both run on the main thread today, so a true
        // data race is not the concern; the concern is reentrancy ordering.
        // A scripted single-mode load fires sceneUnloaded for the previous
        // scene immediately followed by sceneLoaded for the new scene, and a
        // long-running PruneDestroyed (e.g. behind a user-supplied
        // INetworkObjectPool destroy callback that yields back to coroutines)
        // could interleave with RecreateRoomAndSpawnManagers triggered by
        // reconnect logic.  Holding the lock around both handlers establishes
        // a documented ordering: sceneUnloaded → Prune → sceneLoaded → Prune
        // (and never overlapping with a Recreate from another code path).
        // The handlers are idempotent so the lock is correctness theatre, not
        // load-bearing — but documenting the invariant is the point.
        private readonly object _sceneTransitionLock = new object();

        // Cached heartbeat send callback — avoids per-frame closure allocation
        // at the call site in Update().
        private System.Action<byte[]> _sendPacketCallback;

        // The un-batched per-object sender the 30 Hz flush hands out. Cached
        // in a field because a method-group conversion allocates on every read,
        // and bound to the (byte[], int) overload so callers can pass an
        // oversized cached/pooled buffer plus a length. It holds the batch
        // manager's contained sender rather than the raw send: the flush loop
        // must complete whatever one object's payload does — see
        // VariableBatchManager.ContainedSingleSender.
        private System.Func<byte[], int, bool> _sendVariableUpdateDelegate;

        // Where the next flush starts its walk of the owned objects and, inside
        // each, of its components — turned once per tick like an odometer, so
        // a deferred flush is not the same object's, nor the same component's,
        // every tick.  See FlushWalkCursor and FlushDirtyNetworkVariables.
        private readonly FlushWalkCursor _flushWalk = new FlushWalkCursor();

        // Emission gate for the off-main-thread Send report.  A caller on the
        // wrong thread is on it every frame, so the report is systematic rather
        // than incidental and one line a second locates it.
        private static long _lastSendOffMainThreadWarnTicks;

        // The remote-motion advisory's rate bound — both of its faults, the
        // missing interpolator and the switched-off one. The advisory's own
        // latch is per object and is dropped at every session end, so on a
        // reconnect flap the same broken replicas are re-announced once per
        // attempt — a line whose cadence the network chooses, which is the one
        // property a diagnostic on an inbound path must not have.
        private static long _lastMissingInterpolatorWarnTicks;

        // Cached per-payload dispatch delegate for the catch-up replay drain —
        // stored so the per-frame DrainReplayQueue continuation does not
        // allocate a method-group delegate while a large buffer is draining.
        private System.Action<byte[]> _drainReplayDispatch;

        // GC Round 3 (2026-05-02) — per-direction 12-byte AEAD nonce
        // scratch buffers reused across packets.  Replaces the per-packet
        // `new byte[12]` that AeadNonce.Build allocated.  Two separate
        // fields keep the inbound and outbound paths re-entrancy-safe even
        // if a future change runs them concurrently; both are written
        // before being read inside the same Seal/Open call so a stale-read
        // from the prior packet is never observable.  Sized exactly to
        // AeadNonce.Size — the wire-format invariant is enforced at every
        // call site that consumes them.
        private readonly byte[] _outboundNonceScratch = new byte[RTMPE.Core.Aead.AeadNonce.Size];
        private readonly byte[] _inboundNonceScratch  = new byte[RTMPE.Core.Aead.AeadNonce.Size];

        // Per-direction AAD scratch buffer reused across packets, sized by the
        // layout's own maximum (2 baseline + 4 arq_seq, on a session that
        // negotiated CapabilityFlags.ArqSeqAad, + 4 app_seq + 4 gameplay_seq =
        // 14 bytes) so a field added to AeadAad grows this with it.  ChaCha20Poly1305Impl.Seal/OpenInto accept
        // (aad, aadOffset, aadLength) so the meaningful prefix is signalled
        // explicitly — the trailing slack bytes never participate in the
        // Poly1305 computation.
        //
        // Two separate fields keep the inbound and outbound paths
        // re-entrancy-safe even if a future change runs them concurrently;
        // both are fully overwritten before being passed to Seal/Open
        // inside the same call so a stale read from the prior packet is
        // never observable.  Mirrors the threading invariant of the
        // nonce scratch fields above.
        private const int AeadAadScratchCapacity = RTMPE.Core.Protocol.AeadAad.MaxLength;
        private readonly byte[] _outboundAadScratch = new byte[AeadAadScratchCapacity];
        private readonly byte[] _inboundAadScratch  = new byte[AeadAadScratchCapacity];

        // Default-fallback tick interval used until NetworkSettings has been
        // resolved (e.g. very early Awake reentrancy, edit-mode tests with no
        // settings asset).  Matches the SDK's historical 30 Hz canonical rate.
        private const float DefaultVariableFlushInterval = 1f / 30f;

        // Accumulator for the SIMULATION tick — the half of the driver that
        // advances _localTick and dispatches OnFixedTick.  Charged in game time,
        // because OnFixedTick is where client-side prediction samples and ships
        // input: a driver that kept calling it at Time.timeScale = 0 would ship
        // the last input it was handed for the whole of a pause menu.
        //
        // Cadence is driven by _variableFlushInterval below, which is resolved
        // from NetworkSettings.tickRate at initialisation so tick-rate-sensitive
        // titles (FPS / fighting / mobile RPG) can change cadence without
        // forking the SDK.  Replication shares that interval and not this
        // accumulator; see _replicationAccum.
        private float _simTickAccum;

        // Accumulator for REPLICATION — the half that flushes dirty
        // NetworkVariables.  Charged in real time, because a paused client is
        // still a connected one and the room around it keeps playing: state a
        // game writes while its own clock is stopped still has to reach the
        // other players.
        private float _replicationAccum;

        // The counter every NetworkVariable update is stamped with.
        //
        // Separate from _localTick, and it has to be.  A receiver orders updates
        // by the tick they carry, so a replication cadence stamping a _localTick
        // frozen by Time.timeScale = 0 would emit a run of updates all sharing
        // one tick — which the far side reads as a retransmit of a frame it has
        // already applied, and drops.
        private uint _replicationTick;

        // Resolved per-tick interval (seconds).  Initialised to the default
        // so any access prior to InitialiseNetwork still sees a coherent
        // value (the field, not a const, is read by every tick site so a
        // later settings load propagates immediately).
        private float _variableFlushInterval = DefaultVariableFlushInterval;

        /// <summary>
        /// Seconds per simulation tick, <c>1 / NetworkSettings.tickRate</c>: the time step that
        /// <see cref="NetworkBehaviour"/>'s <c>OnFixedTick</c> and prediction replay receive.
        /// </summary>
        public float FixedTickInterval => _variableFlushInterval;

        /// <summary>
        /// Seconds since the last simulation tick boundary, in
        /// <c>[0, FixedTickInterval)</c>.
        /// </summary>
        /// <remarks>
        /// The manager updates it before other components' <c>Update</c> runs, so a component
        /// reading it in its own <c>Update</c> sees the current frame's value. It is <c>0</c>
        /// on a frame that dropped surplus ticks after a long frame.
        /// </remarks>
        public float SubTickResidualSeconds => _simTickAccum;

        // Hard cap on the number of ticks EACH of the driver's two catch-up
        // loops may advance after a long hitch.  ⚠️ Not a bound on the frame:
        // the simulation half and the replication half each spend up to this
        // many, so one Update() worst case is twice it.  Without this guard a 30 s editor pause
        // would queue 900 ticks of work into one frame and produce a
        // multi-second stutter on resume.  The cap drops the "extra" time
        // and resyncs against wall-clock — the server is the source of truth
        // for late-binding state, so a few lost ticks are recoverable on
        // the next reconciliation.
        private const int MaxTicksPerFrame = 8;

        // 5-second accumulator for the periodic RPC callback purge sweep.
        private float _rpcPurgeAccum;
        private const float RpcPurgeInterval = 5f;

        // Accumulator for the periodic prune of SpawnManager's pending-
        // despawn tracker.  Cadence is at the same order as the tracker's
        // TTL (5 s) so a stale entry is never more than one period past
        // its expiry — bounding the worst-case occupancy without scheduling
        // overhead.
        private float _pendingDespawnPruneAccum;
        private const float PendingDespawnPruneInterval = 5f;

        // ── Telemetry counters (Feature: Network Debugger window) ──────────────
        //
       // Atomic 64-bit counters incremented on every wire-level send and receive.
        // Reads on 64-bit platforms (the only platforms Unity supports for
        // dedicated multiplayer titles in 2026) are atomic by construction; we
        // additionally use Interlocked.Read in the public accessors for ARM64
        // where some implementations historically had relaxed ordering on
        // unaligned long reads.  The increment cost is a single LOCK XADD —
        // measured under 5 ns on commodity x64, well below any per-packet
        // budget.  No allocations.
        //
       // The counters are read by the Editor-only Network Debugger window
        // and (optionally) by user telemetry sinks; they are not part of any
        // wire protocol.
        private long _packetsOut;
        private long _bytesOut;
        private long _packetsIn;
        private long _bytesIn;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>The connection state.</summary>
        public NetworkState State => _state;

        /// <summary>
        /// <see langword="true"/> in <see cref="NetworkState.Connected"/> and
        /// <see cref="NetworkState.InRoom"/>.
        /// </summary>
        public bool IsConnected => _state == NetworkState.Connected
                                || _state == NetworkState.InRoom;

        /// <summary><see langword="true"/> in <see cref="NetworkState.InRoom"/>.</summary>
        public bool IsInRoom => _state == NetworkState.InRoom;

        /// <summary>
        /// How loaded the server is with this session's traffic, from the last heartbeat
        /// reply: <c>0</c> means no throttling, and values near <c>255</c> mean the server is
        /// about to drop packets from this client.
        /// </summary>
        /// <remarks>
        /// A client that sends often should lower its send rate as the value rises. Reads
        /// <c>0</c> outside a session. May be read from any thread.
        /// </remarks>
        public byte ServerBackpressure
            => (byte)System.Threading.Volatile.Read(ref _serverBackpressure);

        /// <summary>
        /// The settings in use: the assigned asset, or a default instance when none is
        /// assigned.
        /// </summary>
        public NetworkSettings Settings => _settings;

        /// <summary>
        /// The store that holds pinned server keys for
        /// <see cref="ServerPinningMode.TrustOnFirstUse"/>: a <see cref="MigratingPinStore"/>
        /// unless replaced with <see cref="SetPinStore"/>.
        /// </summary>
        /// <remarks>
        /// The default store keeps keys in an <see cref="EncryptedFilePinStore"/> and moves a
        /// key found in <see cref="PlayerPrefsPinStore"/> into it on first read. To provision
        /// a key before the first connection, call <c>PinStore.Save</c> with the endpoint
        /// <see cref="ServerKeyPinning.CanonicalEndpoint"/> returns.
        /// </remarks>
        public IServerKeyPinStore PinStore
        {
            get
            {
                if (_pinStore == null) _pinStore = new MigratingPinStore();
                return _pinStore;
            }
        }

        /// <summary>
        /// Replaces the pin store. Call it before <see cref="Connect(string)"/>.
        /// </summary>
        /// <param name="store">The store to use, or <see langword="null"/> to restore the
        /// default <see cref="MigratingPinStore"/>.</param>
        public void SetPinStore(IServerKeyPinStore store) => _pinStore = store;

        /// <summary>
        /// Forgets the pinned key for the configured <c>serverHost:serverPort</c>, so the next
        /// <see cref="ServerPinningMode.TrustOnFirstUse"/> connection stores the server's key
        /// again. Use it after the server's identity key has changed.
        /// </summary>
        /// <remarks>
        /// The default store logs a storage failure instead of throwing. Exceptions from a
        /// store installed with <see cref="SetPinStore"/> propagate, and the old key then
        /// stays pinned.
        /// </remarks>
        /// <exception cref="System.IO.IOException">
        /// A store installed with <see cref="SetPinStore"/>, such as an
        /// <see cref="EncryptedFilePinStore"/>, could not rewrite its storage.
        /// </exception>
        public void ClearPinnedKey()
        {
            if (_settings == null) return;
            var endpoint = ServerKeyPinning.CanonicalEndpoint(
                _settings.serverHost, _settings.serverPort);
            PinStore.Clear(endpoint);
        }

        /// <summary>
        /// The numeric session id the server assigned to this connection. Valid from
        /// <see cref="OnConnected"/>; <c>0</c> when there is no session.
        /// </summary>
        /// <remarks>
        /// RPC senders are identified by this id (<see cref="CurrentRpcSenderId"/>). It is not
        /// the player id objects and rosters use; that is <see cref="LocalPlayerStringId"/>.
        /// </remarks>
        public ulong LocalPlayerId => _localPlayerId;

        /// <summary>
        /// Inside an <c>[RtmpeRpc]</c> method: the session id of the call's sender, comparable
        /// with <see cref="LocalPlayerId"/>. <c>0</c> for a call the server made, and outside
        /// an RPC.
        /// </summary>
        /// <remarks>
        /// This is a session id, not the player id <see cref="NetworkBehaviour.OwnerPlayerId"/>
        /// uses.
        /// </remarks>
        public ulong CurrentRpcSenderId { get; private set; }

        /// <summary>
        /// Inside an <c>[RtmpeRpc]</c> method: what the server reports about the caller of the
        /// running call. <see cref="RTMPE.Rpc.RpcCallerFacts.None"/> outside an RPC and when
        /// the server reports nothing about the caller.
        /// </summary>
        /// <remarks>
        /// <see cref="RTMPE.Rpc.RpcCallerFacts.OwnsObject"/> describes the object the call
        /// addressed. In a handler that reaches into another object, read that object's
        /// <c>CurrentRpcCaller</c> (on <see cref="NetworkBehaviour"/>) instead, which reports
        /// ownership only on the addressed object.
        /// </remarks>
        public RTMPE.Rpc.RpcCallerFacts CurrentRpcCallerFacts { get; private set; }

        /// <summary>
        /// The network object the RPC currently executing addressed — valid ONLY
        /// inside a <c>[RtmpeRpc]</c> handler body, 0 at every other time.  What
        /// a behaviour reads of <see cref="CurrentRpcCallerFacts"/> is scoped by
        /// it: owning the object a call addressed is not owning another one the
        /// handler reaches into.
        /// </summary>
        internal ulong CurrentRpcObjectId { get; private set; }

        /// <summary>
        /// Always <c>0</c>: room ids are strings. Use <c>Rooms.CurrentRoom.RoomId</c>
        /// (<see cref="RoomInfo.RoomId"/>).
        /// </summary>
        public ulong CurrentRoomId => _currentRoomId;

        // ── Telemetry counters (read-only) ─────────────────────────────────────
        //
       // Snapshot accessors return the current counter value.  Subtract two
        // sampled values across a known interval to compute a rate.  The
        // Network Debugger Editor window does this at ~250 ms cadence to
        // render packets-per-second / bytes-per-second.

        /// <summary>Packets sent since this manager was created.</summary>
        public long PacketsOutCounter =>
            System.Threading.Interlocked.Read(ref _packetsOut);

        /// <summary>Bytes sent since this manager was created.</summary>
        public long BytesOutCounter =>
            System.Threading.Interlocked.Read(ref _bytesOut);

        /// <summary>Packets received since this manager was created.</summary>
        public long PacketsInCounter =>
            System.Threading.Interlocked.Read(ref _packetsIn);

        /// <summary>Bytes received since this manager was created.</summary>
        public long BytesInCounter =>
            System.Threading.Interlocked.Read(ref _bytesIn);

        /// <summary>
        /// Packets dropped because the send queue held
        /// <see cref="NetworkSettings.sendQueueMaxItems"/> packets. Steady growth means this
        /// client produces packets faster than the network takes them.
        /// </summary>
        /// <remarks>Counted by the network thread; reads <c>0</c> while there is none.</remarks>
        public long SendQueueDroppedCount =>
            _networkThread?.SendQueueDroppedCount ?? 0L;

        /// <summary>
        /// Reliable messages (spawns, despawns, RPCs, room and ownership operations) sent once,
        /// without retransmission, because the reliable window was full. Steady growth means
        /// this client sends reliable messages faster than the link acknowledges them.
        /// </summary>
        /// <remarks>Reset when the session ends.</remarks>
        public long ReliableControlRefusedCount => _outboundReliableChannel.ControlRefusedCount;

        /// <summary>
        /// Reliable messages (spawns, despawns, RPCs, room operations) abandoned after every
        /// retransmission went unacknowledged.
        /// </summary>
        /// <remarks>
        /// Each one is a message peers did not receive; a warning is logged at most once a
        /// second. Reset when the session ends.
        /// </remarks>
        public long ReliableSendsDroppedCount => _reliableSendsDropped;

        private long _reliableSendsDropped;

        /// <summary>
        /// Reliable messages sent again because no acknowledgement arrived in time.
        /// </summary>
        /// <remarks>
        /// The server delivers a message once however many copies reach it, so on a lossy
        /// link this grows while <see cref="EnhancedRpcDuplicatesReceivedCount"/> on the
        /// receivers stays at <c>0</c>. Reset when the session ends.
        /// </remarks>
        public long ReliableRetransmitsCount => _outboundReliableChannel.RetransmitCount;

        /// <summary>
        /// The retransmission timeout in force, in seconds. It follows the round trip measured
        /// from acknowledgements and grows after a timeout.
        /// </summary>
        public float ReliableRtoSeconds => _outboundReliableChannel.CurrentRtoSeconds;

        /// <summary>
        /// The smoothed round trip measured from acknowledgements of reliable messages, in
        /// seconds; <c>0</c> before the first.
        /// </summary>
        public float ReliableSmoothedRttSeconds => _outboundReliableChannel.SmoothedRttSeconds;

        /// <summary>
        /// RPCs delivered to this client a second time within a recent window. <c>0</c> on a
        /// healthy session.
        /// </summary>
        /// <remarks>
        /// A repeat is counted, not refused, because two calls from one sender can share a
        /// request id by chance. Reset when the session ends, so buffered RPCs replayed when
        /// a room is rejoined count as first deliveries. The window uses up to about 300 KB
        /// per manager.
        /// </remarks>
        public long EnhancedRpcDuplicatesReceivedCount => _enhancedRpcDuplicatesReceived;

        private long _enhancedRpcDuplicatesReceived;

        /// <summary>
        /// RPCs this client refused because the caller did not satisfy the method's declared
        /// <see cref="RTMPE.Rpc.RtmpeRpcAttribute.Caller"/>, judged by what the server reports
        /// about the caller.
        /// </summary>
        /// <remarks>
        /// A client calling a method it may not call moves this count on every other client in
        /// the room. When the server reports nothing about callers, every call to a method
        /// with a declared caller is refused and counted. Reset when the session ends.
        /// </remarks>
        public long RpcCallerRefusedCount => _rpcCallerRefused;

        private long _rpcCallerRefused;

        // Main thread only, like every dispatch that reaches it.
        internal void CountRefusedRpcCaller() => _rpcCallerRefused++;

        // The identities the count above is read against.
        private readonly RecentRpcIdentityWindow _recentRpcIdentities = new RecentRpcIdentityWindow();

        // The give-up warning's own budget. Its own, for the reason every gate
        // here has one: a flood of dropped frames must not decide whether a
        // different fault is ever printed.
        private long _lastReliableDropWarnTicks;

        /// <summary>
        /// NetworkVariable flushes postponed to a later tick because replication's share of
        /// the reliable window was in use.
        /// </summary>
        /// <remarks>
        /// No value is lost: the variables stay dirty and are sent at a later tick. Steady
        /// growth means variables replicate below the requested rate on this link; replicate
        /// fewer objects or variables, or lower the send rate of the variables that change
        /// most. Reset when the session ends.
        /// </remarks>
        public long ReplicationFlushesDeferredCount => _outboundReliableChannel.ReplicationDeferredCount;

        /// <summary>
        /// Replication messages sent once, without retransmission, because replication's share
        /// of the reliable window was in use and the message could not wait.
        /// </summary>
        /// <remarks>
        /// Stays <c>0</c> unless <see cref="NetworkSettings.enableVariableBatching"/> is on.
        /// Reset when the session ends.
        /// </remarks>
        public long ReplicationFlushesDowngradedCount => _outboundReliableChannel.ReplicationDowngradedCount;

        /// <summary>
        /// Sends that found the operating system's send buffer full. Read it with
        /// <see cref="SendQueueDroppedCount"/> to spot a saturated uplink.
        /// </summary>
        /// <remarks>Counted by the network thread; reads <c>0</c> while there is none.</remarks>
        public long EnobufsCount =>
            _networkThread?.EnobufsCount ?? 0L;

        /// <summary>
        /// Transport faults that cost one packet rather than the session, such as an oversized
        /// datagram or a briefly unreachable route. Steady growth is packet loss.
        /// </summary>
        /// <remarks>Counted by the network thread; reads <c>0</c> while there is none.</remarks>
        public long PerPacketFaultCount =>
            _networkThread?.PerPacketFaultCount ?? 0L;

        /// <summary>
        /// Packets waiting in the send queue.
        /// </summary>
        /// <remarks>Reads <c>0</c> while there is no network thread.</remarks>
        public int SendQueueCount =>
            _networkThread?.SendQueueCount ?? 0;

        /// <summary>
        /// This client's player id in the current room: the id
        /// <see cref="NetworkBehaviour.OwnerPlayerId"/> and <see cref="PlayerInfo.PlayerId"/>
        /// use. Set when a room is entered; <see langword="null"/> outside a room.
        /// </summary>
        /// <remarks>
        /// <see cref="NetworkBehaviour.IsOwner"/> compares an object's owner with this id. It
        /// is not the session id (<see cref="LocalPlayerId"/>).
        /// </remarks>
        public string LocalPlayerStringId => _localPlayerStringId;

        /// <summary>
        /// The simulation tick counter. Advances at <see cref="NetworkSettings.tickRate"/>
        /// while in a room and wraps at <c>uint.MaxValue</c>.
        /// </summary>
        /// <remarks>
        /// <see cref="RTMPE.Sync.NetworkTransform"/> stamps prediction input with it. Compare
        /// two ticks by subtracting them, which stays correct across the wrap.
        /// </remarks>
        public uint LocalTick => _localTick;

        /// <summary>
        /// The counter NetworkVariable updates are stamped with; receivers order a variable's
        /// updates by it.
        /// </summary>
        /// <remarks>
        /// It advances with the replication cadence, in real time and only while in a room. At
        /// most 8 ticks are counted per frame and the surplus after a long frame is dropped, so
        /// it does not measure elapsed time. It is unrelated to <see cref="LocalTick"/>; do not
        /// compare the two.
        /// </remarks>
        public uint ReplicationTick => _replicationTick;

        /// <summary>
        /// Rooms: create, join, leave, list, and read or write room properties.
        /// </summary>
        /// <remarks>
        /// Replaced on every <see cref="Connect(string)"/> and reconnect attempt; its events
        /// keep their subscribers. Read it from the manager when you need it rather than
        /// keeping a reference.
        /// </remarks>
        public RoomManager Rooms => _roomManager;

        /// <summary>
        /// Lobby room browsing: join a lobby, list its rooms with filters and receive updates.
        /// </summary>
        /// <remarks>
        /// Replaced on every <see cref="Connect(string)"/> and reconnect attempt, like
        /// <see cref="Rooms"/>.
        /// </remarks>
        public LobbyManager Lobby => _lobbyManager;

        /// <summary>
        /// Join-or-create matchmaking: finds a room for a game mode, or creates one.
        /// </summary>
        /// <remarks>
        /// Replaced on every <see cref="Connect(string)"/> and reconnect attempt, like
        /// <see cref="Rooms"/>.
        /// </remarks>
        public MatchmakingManager Matchmaking => _matchmakingManager;

        /// <summary>
        /// Whether this client is the host of <see cref="RoomManager.CurrentRoom"/>, read from
        /// the room's roster.
        /// </summary>
        /// <remarks>
        /// <see langword="false"/> outside a room and before this client's player id is known.
        /// </remarks>
        public bool IsMasterClient
        {
            get
            {
                if (_roomManager == null) return false;
                var room = _roomManager.CurrentRoom;
                if (room == null) return false;
                var master = room.MasterId;
                var localId = _localPlayerStringId;
                return !string.IsNullOrEmpty(master)
                    && !string.IsNullOrEmpty(localId)
                    && master == localId;
            }
        }

        /// <summary>
        /// Writes this client's player properties without repeating its player id, for example
        /// <c>LocalPlayer.SetProperty(key, value)</c>. See <see cref="LocalPlayerContext"/>.
        /// </summary>
        /// <remarks>
        /// Never <see langword="null"/>, and the same instance for the manager's lifetime, so
        /// it may be kept. A write made while this client has no player id logs an error and
        /// sends nothing.
        /// </remarks>
        public LocalPlayerContext LocalPlayer
        {
            get
            {
                if (_localPlayer == null)
                    _localPlayer = new LocalPlayerContext(() => _roomManager, () => _localPlayerStringId);
                return _localPlayer;
            }
        }
        private LocalPlayerContext _localPlayer;

        /// <summary>
        /// Networked objects: register prefabs, spawn and despawn objects, and reach the live
        /// objects and their ownership.
        /// </summary>
        /// <remarks>
        /// Replaced on every <see cref="Connect(string)"/> and reconnect attempt, like
        /// <see cref="Rooms"/>. Prefab registrations carry over; an object pool does not, so
        /// install one from <see cref="OnConnected"/>.
        /// </remarks>
        public SpawnManager Spawner => _spawnManager;

        /// <summary>
        /// Internal accessor for editor diagnostics tooling.  The
        /// NetworkDebuggerWindow uses this to enumerate the registry without
        /// reflecting on private fields, which is brittle across renames.
        /// Not part of the public API — gameplay code should use
        /// <see cref="Spawner"/>.
        /// </summary>
        internal SpawnManager SpawnManagerInternal => _spawnManager;

        /// <summary>
        /// The local UDP endpoint assigned by the OS after a successful
        /// <see cref="Connect"/>.  Reflects the real outgoing interface rather
        /// than <c>0.0.0.0</c>.  Returns <see langword="null"/> before
        /// <see cref="Connect"/> or when a non-UDP transport is active.
        /// Exposed for editor diagnostics; not part of the public API.
        /// </summary>
        internal System.Net.IPEndPoint TransportLocalEndPoint
            => _transport?.LocalEndPoint;

        /// <summary>
        /// Cumulative count of inbound datagrams rejected by the source-IP pin
        /// (off-path packets from an unexpected remote endpoint).  A sustained
        /// non-zero rate may indicate an on-path attacker or routing anomaly.
        /// Returns 0 for non-UDP transports.  Exposed for editor diagnostics.
        /// </summary>
        internal long TransportDroppedSourceMismatchCount
            => (Underlying(_transport) as UdpTransport)?.DroppedSourceMismatchCount ?? 0L;

        /// <summary>
        /// Cumulative count of <see cref="UdpTransport.Send"/> calls that
        /// surfaced ENOBUFS (kernel send-buffer exhaustion).  A sustained
        /// non-zero rate indicates uplink saturation.  Returns 0 for non-UDP
        /// transports.  Exposed for editor diagnostics.
        /// </summary>
        internal long TransportSendBufferExhaustedCount
            => (Underlying(_transport) as UdpTransport)?.SendBufferExhaustedCount ?? 0L;

        // The transport beneath a link simulator, or the transport itself: the
        // two UDP readings above are about the socket, which under the
        // simulator is one layer down, and a bench that hid them would show a
        // clean socket over a shaped link.
        private static NetworkTransport Underlying(NetworkTransport transport)
            => transport is SimulatedLinkTransport shaped ? shaped.Inner : transport;

        /// <summary>
        /// Cumulative count of <c>Poll(0)</c> calls that found a datagram
        /// waiting (hits).  Returns 0 when the network thread is inactive.
        /// Exposed for editor diagnostics; not part of the public API.
        /// </summary>
        internal long NetworkThreadPollHitCount  => _networkThread?.PollHitCount  ?? 0L;

        /// <summary>
        /// Cumulative count of <c>Poll(0)</c> calls that found no data
        /// (misses ≈ wasted wakeups).  Returns 0 when the thread is inactive.
        /// Exposed for editor diagnostics; not part of the public API.
        /// </summary>
        internal long NetworkThreadPollMissCount => _networkThread?.PollMissCount ?? 0L;

        // Append a room lifecycle event to the bounded timeline, evicting the
        // oldest entry when the capacity is reached.  Main thread only.
        private void RecordRoomEvent(string description)
        {
            if (_roomTimeline.Count >= RoomTimelineCapacity)
                _roomTimeline.Dequeue();
            _roomTimeline.Enqueue(new RoomTimelineEntry(description));
        }

        /// <summary>
        /// Recent room lifecycle events for editor diagnostics tooling.
        /// Ordered oldest-first; capped at <see cref="RoomTimelineCapacity"/>
        /// entries.  Not part of the public API.
        /// </summary>
        internal System.Collections.Generic.IEnumerable<RoomTimelineEntry> RoomTimeline
            => _roomTimeline;

        /// <summary>
        /// Room-wide scene changes: the host loads a scene for the whole room, and every
        /// client reports when it has loaded it. See <see cref="NetworkSceneManager"/>.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> until the manager's <c>Awake</c> has run, so read it from
        /// <c>Start</c> or later.
        /// </remarks>
        public NetworkSceneManager Scene
        {
            get
            {
                if (_roomManager == null) return null;
                if (_sceneManager == null)
                {
                    _sceneManager = new NetworkSceneManager(() => _roomManager);
                    // The façade outlives every reconnect, so the deployment's
                    // budget is read here rather than per load.
                    if (_settings != null)
                        _sceneManager.SceneReadyTimeoutSeconds =
                            NetworkSceneManager.ResolveConfiguredTimeout(
                                _settings.sceneReadyTimeoutSeconds);
                }
                return _sceneManager;
            }
        }
        private NetworkSceneManager _sceneManager;

        /// <summary>
        /// The session's bearer token (a JWT), issued when the connection is established;
        /// empty when there is no session.
        /// </summary>
        /// <remarks>
        /// Wrapped in <see cref="RedactedString"/> so it is not printed by accident:
        /// <c>ToString()</c>, string interpolation and <c>Debug.Log</c> show
        /// <c>&lt;redacted&gt;</c>. Call <see cref="RedactedString.Reveal"/> where you use the
        /// value, and do not store the result.
        /// </remarks>
        public RedactedString JwtToken => new RedactedString(_jwtToken);

        /// <summary>
        /// The token <see cref="Reconnect"/> uses; empty when none is held.
        /// </summary>
        /// <remarks>
        /// Wrapped in <see cref="RedactedString"/>, like <see cref="JwtToken"/>. To find out
        /// whether a reconnect is possible, read <see cref="CanReconnect"/>.
        /// </remarks>
        public RedactedString ReconnectToken => new RedactedString(_reconnectToken);

        /// <summary>
        /// Whether a reconnect token is held. It does not check that the server still accepts
        /// the token.
        /// </summary>
        public bool CanReconnect => !string.IsNullOrEmpty(_reconnectToken);

        /// <summary>
        /// The last round-trip time in milliseconds, measured by the heartbeat; <c>-1</c>
        /// before the session's first measurement.
        /// </summary>
        public float LastRttMs { get; private set; } = -1f;

        /// <summary>
        /// The <see cref="RoomInfo.RoomId"/> of the room this client was last in, kept across a
        /// disconnect that keeps the reconnect token so <see cref="Reconnect"/> can rejoin it.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> after leaving the room, after <see cref="Disconnect"/>, and
        /// whenever the reconnect token is discarded.
        /// </remarks>
        public string LastRoomId => _lastRoomId;

        /// <summary>
        /// The room code (<see cref="RoomInfo.RoomCode"/>) of <see cref="LastRoomId"/>, with the
        /// same lifetime. Use it with <see cref="RoomManager.JoinRoomByCode"/> when rejoining
        /// by id fails.
        /// </summary>
        public string LastRoomCode => _lastRoomCode;

    }

    /// <summary>
    /// A single room lifecycle event in the
    /// <see cref="NetworkManager.RoomTimeline"/>.
    /// </summary>
    internal readonly struct RoomTimelineEntry
    {
        /// <summary>Wall-clock time when the event was recorded.</summary>
        public readonly System.DateTime Timestamp;

        /// <summary>Human-readable description of the event.</summary>
        public readonly string Description;

        public RoomTimelineEntry(string description)
        {
            Timestamp   = System.DateTime.Now;
            Description = description;
        }
    }
}
