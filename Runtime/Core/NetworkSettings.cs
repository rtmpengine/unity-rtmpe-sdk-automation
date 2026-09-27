// RTMPE SDK — Runtime/Core/NetworkSettings.cs
//
// Project-wide RTMPE connection settings stored as a ScriptableObject asset.
//
// Create via: Assets > Create > RTMPE > Settings
// Then assign the created asset to the NetworkManager's "Settings" field in the Inspector.
//
// If no asset is assigned at runtime, NetworkManager.Awake() calls
// NetworkSettings.CreateDefault() to produce a runtime-only instance with the
// defaults defined here.  NOTE (audit S3-3): the default server-pinning mode is
// Strict with no pin configured, so a fresh zero-config instance deliberately
// REFUSES every connection (fail-closed — the correct security default) until
// you EITHER set pinnedServerPublicKeyHex to the gateway's static key OR choose
// a relaxed serverPinningMode (TrustOnFirstUse for first-run capture, or
// InsecureNoPinning for trusted local development).  "Zero configuration" does
// not mean "connects out of the box" — pin configuration is required first.

using UnityEngine;
using RTMPE.Crypto;

namespace RTMPE.Core
{
    /// <summary>
    /// The project's connection settings, stored as an asset and assigned to the
    /// <see cref="NetworkManager"/>'s Settings field.
    /// </summary>
    /// <remarks>
    /// <para>The Setup Wizard fills in the project's settings asset, and creates one if there is
    /// none; Create → RTMPE → Settings creates another. Keep several, for example one per
    /// environment, to swap between. Set the fields in the Inspector or from code.</para>
    /// <para>The asset holds no API key: a key in a serialised field would be saved in the asset
    /// and shipped with every build. Supply the key through <see cref="ApiKeySource"/>.</para>
    /// </remarks>
    [CreateAssetMenu(
        fileName = "RTMPESettings",
        menuName  = "RTMPE/Settings",
        order     = 1)]
    public sealed class NetworkSettings : ScriptableObject
    {
        // ── Server ──────────────────────────────────────────────────────────────

        [Header("Server")]
        [Tooltip("Host name or IP address of the RTMPE server. The dashboard shows yours.")]
        public string serverHost = "127.0.0.1";

        [Tooltip("UDP port of the RTMPE server.")]
        [Range(1, 65535)]
        public int serverPort = 7777;

        // ── Timing ──────────────────────────────────────────────────────────────

        [Header("Timing")]
        [Tooltip("Milliseconds between heartbeats, the packets that keep the session alive " +
                 "and measure the round trip.")]
        [Range(100, 60_000)]
        public int heartbeatIntervalMs = 5_000;

        [Tooltip(
            "Milliseconds the session may go without an acknowledged heartbeat before it is " +
            "declared lost. 0 uses twice three heartbeat intervals (30 seconds at the default " +
            "interval); other values are raised to at least three intervals. Raise it for " +
            "clients that can stall briefly, such as mobile devices.")]
        [Range(0, 300_000)]
        public int heartbeatLivenessGraceMs = 0;

        [Tooltip("Milliseconds a connection or reconnect attempt may take before it fails.")]
        [Range(1_000, 60_000)]
        public int connectionTimeoutMs = 10_000;

        [Tooltip("This client's tick rate in Hz: how often the tick counter advances, " +
                 "NetworkVariables are sent and a moved transform may be broadcast.\n\n" +
                 "The server runs at a fixed 30 Hz, so 30 matches it. Above 30, the server " +
                 "keeps only the latest update per tick and may drop the surplus. Below 30, " +
                 "other players see this client's movement at the lower rate.")]
        [Range(1, 128)]
        public int tickRate = 30;

        // ── Diagnostics uplink ──────────────────────────────────────────────────

        [Header("Diagnostics")]
        [Tooltip(
            "Send errors and exceptions from the Unity log to the RTMPE server, for diagnosis " +
            "during testing. The SDK's own errors are sent at their real severity even when " +
            "Enable Debug Logs is off. Leave it off in production: it captures the whole " +
            "process's logs, which may include anything the game code prints.")]
        public bool enableDiagnosticsUplink = false;

        [Tooltip("Also send warnings, including the SDK's own.")]
        public bool diagnosticsCaptureWarnings = false;

        [Tooltip(
            "Milliseconds between diagnostics batches. Errors and exceptions are sent promptly " +
            "regardless.")]
        [Range(250, 60_000)]
        public int diagnosticsFlushIntervalMs = 2_000;

        [Tooltip(
            "Log entries per diagnostics packet. A packet with more entries than the server " +
            "accepts (50 by default) is refused whole.")]
        [Range(1, 50)]
        public int diagnosticsMaxEntriesPerPacket = 50;

        [Tooltip(
            "Diagnostics packets sent per batch at most, so a crash loop cannot flood the " +
            "connection.")]
        [Range(1, 32)]
        public int diagnosticsMaxPacketsPerInterval = 4;

        // ── Reconnect behaviour ─────────────────────────────────────────────────

        [Header("Reconnect")]
        [Tooltip(
            "After a successful Reconnect(), rejoin the room this client was last in. The " +
            "result arrives through RoomManager.OnRoomJoined or OnRoomError. Turn it off to " +
            "choose the room yourself.")]
        public bool autoRejoinLastRoomOnReconnect = true;

        [Tooltip(
            "Attempts one Reconnect() call makes, with a random, growing delay between them. " +
            "When every attempt fails, the session is cleared and OnReconnectFailed is raised.")]
        [Range(1, 50)]
        public int maxReconnectAttempts = 5;

        // ── Buffers ─────────────────────────────────────────────────────────────

        [Header("Buffers")]
        // Defaults sized for a typical 30 Hz, 16-player burst
        // (~480 datagrams/second).  The previous 4 KiB cap held only ~3
        // datagrams worth of kernel buffer and produced silent receive-side
        // drops on every tick boundary; 256 KiB absorbs the worst burst
        // with headroom while staying inside the per-socket rmem_max on
        // unmodified Linux and Windows hosts.
        [Tooltip("Socket send buffer size in bytes. The default, 256 KiB, suits a 16-player " +
                 "room at 30 Hz.")]
        [Range(4_096, 4_194_304)]
        public int sendBufferBytes = 262_144;

        // The floor is a datagram, not a burst. SO_RCVBUF is the queue the
        // kernel fills before the network thread reads it, so a value below one
        // maximal datagram cannot hold the server's largest reply at all — the
        // kernel discards it before `ReceiveFrom` is ever called, and no size of
        // scratch buffer downstream can recover it. Below that the setting
        // guarantees the loss the buffer beneath it exists to prevent.
        [Tooltip("Socket receive buffer size in bytes. The default, 256 KiB, suits a " +
                 "16-player room at 30 Hz. Smaller buffers lose datagrams in the operating " +
                 "system without a message; the minimum is the largest datagram.")]
        [Range(RTMPE.Protocol.PacketBuilder.MaxDatagramBytes, 4_194_304)]
        public int receiveBufferBytes = 262_144;

        [Tooltip("Size of the buffer each received datagram is read into. A larger " +
                 "datagram is lost without a message, so the minimum is the largest " +
                 "datagram UDP carries.")]
        // Sized against what the server is permitted to send rather than
        // against typical traffic. A full lobby room list already exceeds the
        // 8 KiB this used to hold — a hundred rooms measures ~17 KB — and it
        // arrives as one datagram: too large for the buffer it is lost before
        // any parser sees it, and on neither platform does anything say so. No
        // room list, no problem code, and a join that never completes.
        //
        // The floor is PacketBuilder.MaxDatagramBytes, the largest datagram a
        // UDP path will carry. The default is the allocator bucket above it:
        // the network thread rents this buffer per datagram from a pool that
        // rounds to a power of two, so any value in 32 769..65 536 costs the
        // same 64 KiB and the first byte past it costs 128 KiB. Held to both by
        // scripts/check-reply-capacity-contract.sh.
        //
        // ⚠️ A datagram this large is delivered by IP fragmentation, which some
        // paths drop. Making the buffer big enough is what stops the loss being
        // certain; it does not make a 17 KB reply as reliable as a small one.
        [Range(RTMPE.Protocol.PacketBuilder.MaxDatagramBytes, 4_194_304)]
        public int networkThreadBufferBytes = 65_536;

        /// <summary>
        /// The most packets the send queue holds. When it is full, new packets are dropped and
        /// counted in <see cref="NetworkManager.SendQueueDroppedCount"/>.
        /// </summary>
        /// <remarks>
        /// It bounds memory when this client produces packets faster than the network takes
        /// them. The default, 4096, is about 4 MB at 1200 bytes a packet. Watch
        /// <see cref="NetworkManager.SendQueueDroppedCount"/> to detect a saturated uplink.
        /// </remarks>
        [Tooltip("The most packets the send queue holds. When it is full, new packets are " +
                 "dropped and counted in NetworkManager.SendQueueDroppedCount. The default, " +
                 "4096, is about 4 MB.")]
        [Range(64, 65_536)]
        public int sendQueueMaxItems = 4_096;

        // ── Interest management ─────────────────────────────────────────────────

        [Header("Interest Management")]
        [Tooltip(
            "Extra distance, in world units, an object must move past " +
            "InterestManager.ReceiveFilterRadius before it leaves the visible set, so objects " +
            "at the edge do not flicker in and out.\n\n" +
            "At 0 or more it replaces every InterestManager's own Hysteresis Margin; -1 uses " +
            "each component's value.")]
        [Range(-1f, 50f)]
        public float interestHysteresisMargin = 1f;

        // ── Sync hardening (physics & transform plausibility) ──────────────────

        [Header("Sync Hardening")]
        [Tooltip("Received physics state with a linear speed above this, in units per " +
                 "second, is refused. 0 turns the limit off.")]
        [Range(0f, 100_000f)]
        public float maxLinearVelocity = 1_000f;

        [Tooltip("Received physics state with an angular speed above this is refused " +
                 "(radians per second in 3-D, degrees per second in 2-D). 0 turns the limit off.")]
        [Range(0f, 100_000f)]
        public float maxAngularVelocity = 1_000f;

        [Tooltip("Received physics state that moves an object further than this from its " +
                 "last accepted position, in world units, is refused. 0 turns the limit off.")]
        [Range(0f, 100_000f)]
        public float maxPositionDeltaPerTick = 50f;

        [Tooltip("Physics updates accepted per object per second; the rest are dropped. " +
                 "0 turns the limit off.")]
        [Range(0f, 1_000f)]
        public float maxPhysicsPacketsPerSecond = 240f;

        [Tooltip("Apply Rigidbody constraint changes a sender makes at run time. Off, " +
                 "constraints stay as they were at spawn.")]
        public bool allowDynamicConstraints = false;

        [Tooltip("With Allow Dynamic Constraints on, the constraint bits a sender may " +
                 "change. 255 allows all of them.")]
        [Range(0, 255)]
        public int dynamicConstraintsAllowMask = 0xFF;

        [Tooltip("Largest correction from the server, in world units, that an owned " +
                 "NetworkTransform, NetworkRigidbody or NetworkRigidbody2D accepts. A larger " +
                 "one is refused with a warning and the local position is kept. 0 turns the " +
                 "limit off.")]
        [Range(0f, 100_000f)]
        public float maxServerCorrectionDistance = 50f;

        [Tooltip("Refuse corrections that would place an object outside the box set by " +
                 "World Bounds Center and World Bounds Extents.")]
        public bool worldBoundsEnabled = false;

        [Tooltip("Centre of the world bounds box, in world units. Used when World Bounds " +
                 "Enabled is on.")]
        public Vector3 worldBoundsCenter = Vector3.zero;

        [Tooltip("Half-size of the world bounds box on each axis, in world units. Used when " +
                 "World Bounds Enabled is on.")]
        public Vector3 worldBoundsExtents = new Vector3(10_000f, 10_000f, 10_000f);

        // ── Lobby / matchmaking JSON hardening ─────────────────────────────────

        [Header("Lobby Hardening")]
        [Tooltip("Rooms accepted in one lobby room list; a larger list is refused whole. " +
                 "The minimum is 100, the most rooms the server sends in one list.")]
        // The minimum is the Room Service's own reply ceiling, not 1: a cap
        // below it refuses lists an honest server is allowed to send, and on a
        // join reply that refusal reports the client out of the lobby the
        // gateway has just subscribed it to. Values below it are floored at
        // read time for assets authored before this bound existed.
        [Range(RTMPE.Rooms.LobbyPacketParser.LobbyRoomListServerMaxEntries, 100_000)]
        public int maxLobbyRoomEntries = 256;

        [Tooltip("Longest string, in bytes, accepted in a lobby or matchmaking reply, such " +
                 "as a room code, a lobby name or an error message.")]
        [Range(16, 65_536)]
        public int maxLobbyStringBytes = 256;

        [Tooltip("Seconds a networked scene load may go without every player reporting it " +
                 "loaded before NetworkSceneManager.OnSceneLoadTimedOut is raised. The load " +
                 "itself is not cut short. 0 uses the SDK default of 60 seconds; -1 turns the " +
                 "report off.")]
        // ⛔ Zero is "not configured", not "off".  A field added to this class
        // reads back as zero on every asset written before it existed, and on
        // any instance a deserialiser fills without running the initialiser —
        // so a zero that meant "off" would ship the report dark on exactly the
        // installed base it was written for, with an Inspector value that looks
        // deliberate.  -1 is the off switch because nothing can produce it by
        // omission.
        //
        // ⚠️ Min rather than Range, and the difference is the point: a slider
        // puts "use the default" and "switch it off" at adjacent points of one
        // continuum, so a drag that lands a hair below zero disables the report
        // with nothing on screen to say so, and one a hair above it asks for a
        // budget of milliseconds.  A typed field makes both a decision.  No
        // ceiling is needed — an unreachable budget saturates rather than
        // wrapping, so it reads as "not soon", which is what it says.
        //
        // Read through NetworkSceneManager.ResolveConfiguredTimeout, which owns
        // what a value means; no shard compiles this file, so a decision left
        // here would be one no test could reach.
        [Min(-1f)]
        public float sceneReadyTimeoutSeconds;

        // ── Spawnable prefabs ──────────────────────────────────────────────────

        [Header("Spawnable Prefabs")]
        [Tooltip("The prefab registry that Window → RTMPE → Network Prefabs generates. When " +
                 "assigned, every prefab in it is registered for spawning automatically; " +
                 "leave it empty to register prefabs with SpawnManager.RegisterPrefab.")]
        // ⚠️ Every prefab the registry names becomes a hard reference and enters
        // every build, spawned or not.  That is the same trade Unity's own
        // NetworkPrefabsList makes and it is not a defect, but it is a cost an
        // author should meet here rather than in a build report.
        //
        // ⛔ Empty is not a misconfiguration.  The asset is opt-in, every project
        // written before it existed has none, and a field added to this class
        // reads back as null on all of them — so a load that treated null as an
        // error would fail every existing project on upgrade.
        public NetworkPrefabRegistry prefabRegistry;

        // ── Client-side prediction ─────────────────────────────────────────────

        [Header("Client-Side Prediction")]
        [Tooltip("Position error, in world units, below which a correction from the server " +
                 "is accepted without a visible correction. The default for every " +
                 "NetworkTransform with prediction on that does not set its own. Raise it for " +
                 "fast, low-precision games; lower it where 10 cm of drift is visible.")]
        [Range(0f, 10f)]
        public float reconcileLerpThreshold = 0.1f;

        [Tooltip("Position error, in world units, above which a predicted object snaps to " +
                 "the server's pose instead of blending toward it. The default for every " +
                 "NetworkTransform with prediction on that does not set its own. Set it high " +
                 "enough that ordinary latency does not cause snaps; raise it for vehicles and " +
                 "projectiles. A value below Reconcile Lerp Threshold is raised to it.")]
        [Range(0f, 1_000f)]
        public float reconcileSnapThreshold = 2.0f;

        [Tooltip("Correct objects this client owns against the state the server sends " +
                 "back. Leave it off unless your server simulates movement authoritatively: " +
                 "otherwise the state an owner receives is its own movement, a tick late, and " +
                 "correcting against it pulls owned objects backwards.")]
        public bool reconcileOwnedObjects = false;

        // ── Bandwidth optimisation ────────────────────────────────────────────

        [Header("Bandwidth Optimisation")]
        [Tooltip("Send NetworkTransform poses in a compact encoding: half-precision position " +
                 "and scale and a packed rotation, with about 0.1% position error and 0.1° " +
                 "rotation error. Receivers accept both encodings. Leave it off unless the " +
                 "server you connect to supports it.")]
        public bool quantizeTransforms = false;

        [Tooltip("Reserved; has no effect.")]
        public bool enableGameplayOrdering = false;

        [Tooltip("Reserved; has no effect.")]
        [Range(2, 64)]
        public int gameplayOrderingBufferSize = 8;

        [Tooltip("Add an authenticated application sequence number to every encrypted " +
                 "packet, so a receiver can order packets and drop duplicates without " +
                 "decrypting them. See NetworkManager.LastInboundApplicationSequence. Leave it " +
                 "off unless the server you connect to supports it.")]
        public bool preserveApplicationSequence = false;

        [Tooltip("Highest speed, in world units per second, at which a NetworkTransform " +
                 "broadcasts its owner's movement; faster movement is limited to it. Move " +
                 "instantly with NetworkTransform.OwnerTeleportTo. 0 turns the limit off.")]
        [Range(0f, 1_000f)]
        public float maxOwnerVelocityMetersPerSecond = 50f;

        [Tooltip("Pack each tick's NetworkVariable updates from all owned objects into as " +
                 "few packets as possible, which saves bandwidth when many objects change a " +
                 "little. Leave it off unless the server you connect to supports it.")]
        public bool enableVariableBatching = false;

        [Tooltip("The most NetworkVariable updates in one batch packet; more are split " +
                 "across packets. The server accepts at most 64.")]
        [Range(1, 64)] // VariableBatchBuilder.GatewayEntryCap — gateway drops batches above this
        public int maxVariablesPerBatch = 32;

        // ── Spawn hardening ────────────────────────────────────────────────────

        [Header("Spawn Hardening")]
        [Tooltip("Spawns, local and received, accepted per second; the rest are dropped. " +
                 "The limit does not apply for a second and a half after a room is entered, " +
                 "while the room's existing objects arrive; Max Spawns Per Room still does.")]
        [Range(1, 1_000)]
        public int maxSpawnsPerSecond = 100;

        [Tooltip("Networked objects that may exist on this client at once. Further spawns " +
                 "are refused until objects despawn.")]
        [Range(100, 50_000)]
        public int maxSpawnsPerRoom = 5_000;

        // ── Replication hardening ──────────────────────────────────────────────

        [Header("Replication Hardening")]
        [Tooltip("Elements a NetworkVariableList may hold, and the most a client accepts " +
                 "in one list. The owner's writes are also limited by what one datagram " +
                 "carries — 283 ints, 141 Vector2Ints, 94 Vector3s — because a joining " +
                 "player receives the whole list at once. The default covers typical lists " +
                 "such as inventories.")]
        [Range(1, 65_535)]
        public int maxNetworkVariableListSize = 1024;

        [Tooltip("Seconds a NetworkVariableList may go without being sent before the " +
                 "whole list is sent again, which repairs another player's copy after a lost " +
                 "update. Every send restarts the interval. 0 turns the resend off.")]
        [Range(0f, 300f)]
        public float networkVariableListFullSyncIntervalSeconds =
            DefaultNetworkVariableListFullSyncIntervalSeconds;

        /// <summary>
        /// The default of <see cref="networkVariableListFullSyncIntervalSeconds"/>, in seconds,
        /// and the value a negative setting is restored to.
        /// </summary>
        public const float DefaultNetworkVariableListFullSyncIntervalSeconds = 5f;

        // ── Debug ────────────────────────────────────────────────────────────────

        [Header("Debug")]
        [Tooltip("Log verbose SDK activity to the Console. When off, routine SDK faults are " +
                 "logged at Debug.Log level so crash reporters do not collect them. Turn it " +
                 "on only in development.")]
        public bool enableDebugLogs;

        // ── RPC authorisation ──────────────────────────────────────────────────

        [Tooltip("Check the sender of built-in method-id calls (RpcMethodId) before they " +
                 "run. Only the Editor reads this setting: player builds always check the " +
                 "sender.")]
        public bool requireLegacyRpcSender = true;

        // ── Forward-compat protocol toggles ────────────────────────────────────
        //
        // The toggles below gate the ARQ sub-header features whose gateway
        // counterpart is behind an environment variable / capability bit.
        // The SDK mirrors the active toggles onto its CapabilityFlags
        // advertisement during the handshake, so the negotiated session set
        // never claims a feature the local side will not honour — a toggle
        // the gateway has not also enabled simply stays dormant (the send
        // safely downgrades to best-effort).
        //
        // SessionAck bootstrap encryption is NOT a toggle here: it is
        // negotiated automatically via CapabilityFlags.EncryptedSessionAck,
        // which the SDK always advertises and the gateway always offers.

        [Header("Reliable Delivery")]
        [SerializeField]
        [Tooltip(
            "Retransmit reliable messages (spawns, despawns, RPCs, room operations) until the " +
            "server acknowledges them. Off, they are sent once and a one-time warning is " +
            "logged. Leave it on unless the server you connect to is configured without it.")]
        private bool _emitArqSequence = true;

        /// <summary>
        /// Whether reliable messages are retransmitted until the server acknowledges them. Set
        /// with the Inspector field Emit Arq Sequence; on by default.
        /// </summary>
        /// <remarks>
        /// When off, a reliable message, including one sent with
        /// <see cref="NetworkManager.Send(byte[], bool)"/> and <c>reliable: true</c>, is sent
        /// once, and a one-time warning is logged. Retransmission also needs the server's
        /// support, which is agreed when the connection is established.
        /// </remarks>
        public bool EmitArqSequence => _emitArqSequence;

        [SerializeField]
        [Tooltip("Add a gameplay sequence number to packets marked for gameplay ordering. " +
                 "Leave it off unless the server you connect to supports it.")]
        private bool _emitGameplaySequencePrefix;

        /// <summary>
        /// Whether a gameplay sequence number is added to packets marked for gameplay ordering.
        /// Set with the Inspector field Emit Gameplay Sequence Prefix; off by default.
        /// </summary>
        public bool EmitGameplaySequencePrefix => _emitGameplaySequencePrefix;

        // ── Crypto ─────────────────────────────────────────────────────────────

        [Header("Crypto")]
        [Tooltip(
            "The server's Ed25519 identity key, as 64 hexadecimal characters; copy it from the " +
            "dashboard.\n\n" +
            "Required under the default Server Pinning Mode, Strict, which refuses to connect " +
            "without it. It is a different key from Api Key Seal Server Public Key Hex.")]
        public string pinnedServerPublicKeyHex = "";

        [Tooltip(
            "Required. The server's X25519 public key, as 64 hexadecimal characters: the API " +
            "key is sealed to it before it is sent, and without it no connection is made.\n\n" +
            "Copy the Sealed-Box Public Key from the dashboard. It is a different key from " +
            "Pinned Server Public Key Hex; do not paste the identity key here.")]
        public string apiKeySealServerPublicKeyHex = "";

        [Tooltip("Deprecated; use Server Pinning Mode. When enabled it behaves like Strict.")]
        public bool requirePinnedServerPublicKey;

        [Tooltip(
            "The issuer (iss claim) the session token must carry. The default matches the " +
            "RTMPE service. Empty accepts any issuer; do not ship it empty.")]
        public string expectedJwtIssuer = "rtmpe-gateway";

        [Tooltip(
            "The audience (aud claim) the session token must carry. The default matches the " +
            "RTMPE service. Empty accepts any audience; do not ship it empty.")]
        public string expectedJwtAudience = "rtmpe-session";

        [Tooltip(
            "Allowed clock difference, in seconds, when checking the session token's exp and " +
            "nbf claims. Two minutes covers ordinary clock drift on players' devices.")]
        public int jwtClockSkewSeconds = 120;

        [Tooltip(
            "Which server identity keys are accepted.\n\n" +
            "Strict (default): the key must equal Pinned Server Public Key Hex; with none set, " +
            "every connection is refused. Use it for builds you ship.\n\n" +
            "TrustOnFirstUse: the first connection to a host and port stores the server's key " +
            "on the device; later connections must present the same key.\n\n" +
            "InsecureNoPinning: any key is accepted, with a warning each session. For local " +
            "testing only.")]
        public ServerPinningMode serverPinningMode = ServerPinningMode.Strict;

        [Tooltip(
            "Under TrustOnFirstUse, refuse an endpoint whose key was not provisioned in the pin " +
            "store beforehand (for example with NetworkManager.PinStore.Save), instead of " +
            "trusting the key the first connection presents.\n\n" +
            "A key stored only in PlayerPrefs (PlayerPrefsPinStore) does not count: connect " +
            "once with this setting off to move it into the pin store.")]
        public bool requireFirstUseProvisioned = false;

        // ── Derived ──────────────────────────────────────────────────────────────

        /// <summary>Seconds per tick: <c>1 / tickRate</c>.</summary>
        public float TickInterval => 1f / Mathf.Max(1, tickRate);

        /// <summary>
        /// The pinning mode in force: <see cref="ServerPinningMode.Strict"/> when
        /// <see cref="requirePinnedServerPublicKey"/> is set, otherwise
        /// <see cref="serverPinningMode"/>.
        /// </summary>
        public ServerPinningMode EffectivePinningMode
        {
            get
            {
                if (requirePinnedServerPublicKey) return ServerPinningMode.Strict;
                return serverPinningMode;
            }
        }

        /// <summary>
        /// <see cref="heartbeatIntervalMs"/>, raised to <see cref="MinHeartbeatIntervalMs"/>.
        /// </summary>
        /// <remarks>
        /// The Inspector enforces the minimum, but an asset loaded at run time or a value set
        /// from code can be lower. Such a value is raised to the minimum, and a warning is
        /// logged.
        /// </remarks>
        public int EffectiveHeartbeatIntervalMs
        {
            get
            {
                if (heartbeatIntervalMs >= MinHeartbeatIntervalMs) return heartbeatIntervalMs;

                if (RTMPE.Core.WarnGate.ShouldEmit(ref s_lastHeartbeatFloorWarnTicks))
                    UnityEngine.Debug.LogWarning(
                        $"[RTMPE] NetworkSettings.heartbeatIntervalMs is {heartbeatIntervalMs} ms, " +
                        $"below the {MinHeartbeatIntervalMs} ms floor the heartbeat requires. " +
                        $"Using {MinHeartbeatIntervalMs} ms. A Range attribute bounds the " +
                        "Inspector only — this value reached the asset from somewhere else.");

                return MinHeartbeatIntervalMs;
            }
        }

        /// <summary>The shortest heartbeat interval, in milliseconds: 100.</summary>
        public const int MinHeartbeatIntervalMs = 100;

        private static long s_lastHeartbeatFloorWarnTicks;

        /// <summary>
        /// <see cref="pinnedServerPublicKeyHex"/> decoded to 32 bytes; <see langword="null"/>
        /// when it is empty.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// The value is not 64 hexadecimal characters.
        /// </exception>
        public byte[] PinnedServerPublicKeyBytes
        {
            get
            {
                if (string.IsNullOrEmpty(pinnedServerPublicKeyHex)) return null;
                return Crypto.KeyHex.Decode32(pinnedServerPublicKeyHex);
            }
        }

        /// <summary>
        /// <see cref="apiKeySealServerPublicKeyHex"/> decoded to 32 bytes; <see langword="null"/>
        /// when it is empty, and then no connection can be made.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// The value is not 64 hexadecimal characters.
        /// </exception>
        public byte[] ApiKeySealServerPublicKeyBytes =>
            string.IsNullOrEmpty(apiKeySealServerPublicKeyHex)
                ? null
                : Crypto.KeyHex.Decode32(apiKeySealServerPublicKeyHex);

        // ── JWT signature verification (JWKS pin) ──────────────────────────────
        //
        // When a pin is configured below, SessionAck JWTs whose signature does
        // not validate against the pinned key are rejected before any claim is
        // trusted. Without a pin the SDK validates structure + temporal claims
        // + iss/aud only and emits a one-time advisory warning so integrators
        // discover the gap before shipping. AEAD channel binding (RequiresEncryption)
        // is the second line of defence; signature verification closes the gap when
        // the channel keys themselves cannot be assumed trustworthy.

        /// <summary>
        /// How the session token's signature is verified: the type of
        /// <see cref="jwtSignatureAlgorithm"/>.
        /// </summary>
        /// <remarks>
        /// When no key is configured for the selected algorithm, a token is verified with the
        /// Ed25519 identity key the server proves during the handshake. The token's <c>alg</c>
        /// header must match the algorithm used, or the token is refused.
        /// </remarks>
        public enum JwtSignatureAlgorithm
        {
            /// <summary>
            /// No configured key. Without an identity key from the server, the token's
            /// signature is not checked and an error is logged.
            /// </summary>
            None = 0,

            /// <summary>
            /// EdDSA over Ed25519 (<c>alg</c> EdDSA), with the key in
            /// <see cref="jwtSigningKeyHex"/>.
            /// </summary>
            Ed25519 = 1,

            /// <summary>
            /// RSA PKCS#1 v1.5 with SHA-256 (<c>alg</c> RS256), with the key in
            /// <see cref="jwtSigningKeyPem"/>: a PEM-encoded RSA public key of at least 2048
            /// bits.
            /// </summary>
            RsaPkcs1Sha256 = 2,
        }

        [Header("Server JWT signature verification")]
        [Tooltip(
            "How the session token's signature is verified.\n\n" +
            "Ed25519 (default): with Jwt Signing Key Hex when it is set, otherwise with the " +
            "identity key the server proves during the handshake. No configuration is needed.\n\n" +
            "RsaPkcs1Sha256 (RS256): with Jwt Signing Key Pem, for tokens from an external " +
            "issuer.\n\n" +
            "None: with the server's identity key when it proves one; otherwise the signature " +
            "is not checked and an error is logged.")]
        // Ed25519 is the secure default: the gateway signs SessionAck JWTs with its
        // Ed25519 identity key, and the IdentitySignedJwt capability delivers that
        // key to the client during the handshake, so the standard deployment
        // verifies signatures without any key configuration. None stays available
        // as an explicit opt-out for integrators who accept the documented risk;
        // JwtValidator escalates a LogError when it is selected.
        public JwtSignatureAlgorithm jwtSignatureAlgorithm = JwtSignatureAlgorithm.Ed25519;

        [Tooltip(
            "An Ed25519 public key, as 64 hexadecimal characters, that every session token " +
            "must be signed with. Used only with the Ed25519 algorithm. When set, it is used " +
            "instead of the key the server proves during the handshake. The RTMPE server signs " +
            "tokens with its identity key, so leave it empty or set it to Pinned Server Public " +
            "Key Hex.")]
        public string jwtSigningKeyHex = "";

        [Tooltip(
            "A PEM-encoded RSA public key (a -----BEGIN PUBLIC KEY----- block, at least 2048 " +
            "bits) that every session token must be signed with. Used only with the " +
            "RsaPkcs1Sha256 algorithm. When set, it is used instead of the key the server " +
            "proves during the handshake.")]
        [TextArea(3, 12)]
        public string jwtSigningKeyPem = "";

        // ── Internal helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Create a runtime-only instance with factory-default values.
        /// Used by <see cref="NetworkManager"/> when no settings asset is assigned.
        /// Not saved to disk; garbage-collected when the manager is destroyed.
        /// </summary>
        internal static NetworkSettings CreateDefault()
        {
            var s = CreateInstance<NetworkSettings>();
            s.name = "RTMPESettings (runtime default)";
            return s;
        }

        /// <summary>
        /// Coerce any non-finite (NaN / Infinity) values configured on
        /// world-bounds Vector3 fields back to a sane default.  Reachable
        /// from the Inspector when an artist accidentally drags the value
        /// into a degenerate state, and from runtime callers that build a
        /// settings object via <see cref="ScriptableObject.CreateInstance{T}()"/>
        /// and assign Vector3.PositiveInfinity.  Without this guard the
        /// reconciliation bounds-check at <c>NetworkTransform.ApplyReconciliation</c>
        /// short-circuits to false on every comparison, silently disabling
        /// the bound entirely.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="OnValidate"/> in the Editor and from
        /// <see cref="EnsureFiniteWorldBoundsForRuntime"/> by the runtime
        /// after asset load — both are idempotent and safe to invoke
        /// repeatedly.
        /// </remarks>
        internal void EnsureFiniteWorldBoundsForRuntime()
        {
            worldBoundsCenter  = ClampVector3Finite(worldBoundsCenter,  Vector3.zero);
            worldBoundsExtents = ClampVector3Finite(
                worldBoundsExtents,
                new Vector3(10_000f, 10_000f, 10_000f));
            // Extents must be non-negative; a negative half-extent reverses
            // the inside-out test and accepts every server position as
            // out-of-bounds.  Clamp to zero rather than abs() so an
            // accidentally-negative configuration surfaces as an obviously-
            // empty box rather than a silently-mirrored one.
            if (worldBoundsExtents.x < 0f) worldBoundsExtents.x = 0f;
            if (worldBoundsExtents.y < 0f) worldBoundsExtents.y = 0f;
            if (worldBoundsExtents.z < 0f) worldBoundsExtents.z = 0f;

            // Range attributes only fire from the Inspector — assets loaded
            // through Addressables / AssetBundle / direct deserialisation
            // bypass that path.  Mirror the Inspector floors at runtime so
            // a degenerate setting (zero or negative) cannot silently
            // disable the spawn-rate gate or the room-wide spawn cap.
            if (maxSpawnsPerSecond           < 1)     maxSpawnsPerSecond           = 1;
            if (maxSpawnsPerSecond           > 1000)  maxSpawnsPerSecond           = 1000;
            if (maxSpawnsPerRoom             < 100)   maxSpawnsPerRoom             = 100;
            if (maxSpawnsPerRoom             > 50000) maxSpawnsPerRoom             = 50000;
            if (maxNetworkVariableListSize   < 1)     maxNetworkVariableListSize   = 1;
            if (maxNetworkVariableListSize   > 65535) maxNetworkVariableListSize   = 65535;

            // Zero is the documented way to switch the refresh off, so only a
            // negative — which the Inspector floor cannot produce — is restored
            // to the default rather than clamped to the nearest legal value.
            if (networkVariableListFullSyncIntervalSeconds < 0f)
                networkVariableListFullSyncIntervalSeconds =
                    DefaultNetworkVariableListFullSyncIntervalSeconds;
            if (networkVariableListFullSyncIntervalSeconds > 300f)
                networkVariableListFullSyncIntervalSeconds = 300f;

            // The receive buffer, for the same reason and with a sharper edge:
            // the Inspector floor below reaches a newly created asset, and every
            // asset that already exists keeps whatever it was saved with. A
            // datagram larger than this buffer is truncated by the socket, fails
            // its authentication tag and is discarded with no diagnostic — so an
            // asset saved before the floor existed goes on losing every reply
            // above its size, silently, and the setting that would fix it is one
            // nobody has a reason to open.
            if (networkThreadBufferBytes < RTMPE.Protocol.PacketBuilder.MaxDatagramBytes)
                networkThreadBufferBytes = RTMPE.Protocol.PacketBuilder.MaxDatagramBytes;
            if (networkThreadBufferBytes > 4_194_304) networkThreadBufferBytes = 4_194_304;

            // The kernel queue in front of it, for the same reason: below one
            // maximal datagram the largest replies never reach the buffer above.
            if (receiveBufferBytes < RTMPE.Protocol.PacketBuilder.MaxDatagramBytes)
                receiveBufferBytes = RTMPE.Protocol.PacketBuilder.MaxDatagramBytes;
            if (receiveBufferBytes > 4_194_304) receiveBufferBytes = 4_194_304;
        }

        private static Vector3 ClampVector3Finite(Vector3 candidate, Vector3 fallback)
        {
            if (!IsFiniteFloat(candidate.x)
             || !IsFiniteFloat(candidate.y)
             || !IsFiniteFloat(candidate.z))
                return fallback;
            return candidate;
        }

        private static bool IsFiniteFloat(float v)
            => !float.IsNaN(v) && !float.IsInfinity(v);

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Inspector-time hardening.  Range attributes already clamp the
            // numeric scalars; the world-bounds Vector3 fields have no
            // Range support, so the finiteness guard runs here.
            EnsureFiniteWorldBoundsForRuntime();

            // Strict pinning declared but no key supplied — every connection
            // will fail at runtime because the SDK has nowhere to compare the
            // server's key against.  EffectivePinningMode folds the legacy
            // requirePinnedServerPublicKey flag into the enum, so this single
            // check covers both the enum default (Strict) and the legacy
            // boolean.  Surface the conflict at edit time so it is caught
            // before a device build.
            if (ServerKeyPinning.StrictModeRequiresPinButNoneConfigured(
                    EffectivePinningMode, pinnedServerPublicKeyHex))
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: server pinning is Strict but " +
                    "pinnedServerPublicKeyHex is empty — every connection attempt " +
                    "will be rejected.  Supply the 64-char hex key, or select a " +
                    "non-Strict ServerPinningMode.",
                    this);
            }

            // The sealed-box key is the gateway's X25519 key; the pin is its
            // Ed25519 identity key.  They are different credentials and never
            // coincide, so an exact match is almost always the Ed25519 pin
            // pasted into the sealed-box field — which the gateway cannot open.
            // Flag it at edit time rather than as a silent handshake timeout.
            if (ServerKeyPinning.ApiKeySealKeyMatchesPinnedKey(
                    apiKeySealServerPublicKeyHex, pinnedServerPublicKeyHex))
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: apiKeySealServerPublicKeyHex equals " +
                    "pinnedServerPublicKeyHex — these are different keys.  The sealed-box " +
                    "field takes the gateway's X25519 key; the pin takes its Ed25519 " +
                    "identity key.  Paste the X25519 key from the gateway-config endpoint.",
                    this);
            }

            // The same mistake with no pin to compare against: bit 255 is clear
            // in every X25519 u-coordinate and set in roughly half of Ed25519
            // public keys, so a set bit identifies the wrong key on its own.
            // Decoding first also puts a malformed value in front of the
            // integrator here rather than at the first connect attempt.
            byte[] sealKeyBytes = null;
            string sealKeyDecodeError = null;
            try { sealKeyBytes = ApiKeySealServerPublicKeyBytes; }
            catch (System.Exception ex) { sealKeyDecodeError = ex.Message; }

            if (string.IsNullOrWhiteSpace(apiKeySealServerPublicKeyHex))
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: apiKeySealServerPublicKeyHex is empty.  It is the only " +
                    "API-key envelope the gateway accepts, so a build with this field unset " +
                    "cannot connect at all.  Paste the gateway's 64-character hex X25519 key " +
                    "from the developer dashboard or the gateway-config endpoint.",
                    this);
            }
            else if (sealKeyDecodeError != null)
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: apiKeySealServerPublicKeyHex does not decode to a " +
                    $"32-byte key ({sealKeyDecodeError}).  Supply the gateway's 64-character " +
                    "hex X25519 key.",
                    this);
            }
            else if (sealKeyBytes != null
                  && !ServerKeyPinning.IsWellFormedX25519PublicKey(sealKeyBytes))
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: apiKeySealServerPublicKeyHex is not a valid X25519 " +
                    "public key — bit 255 is set, which an X25519 u-coordinate never has " +
                    "and an Ed25519 public key often does.  The gateway's Ed25519 identity " +
                    "key belongs in pinnedServerPublicKeyHex.",
                    this);
            }

            // Ordering buffer below the architectural minimum: a single-slot
            // buffer cannot resolve a two-packet reorder and will stall delivery.
            if (enableGameplayOrdering && gameplayOrderingBufferSize < 2)
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: enableGameplayOrdering requires " +
                    $"gameplayOrderingBufferSize ≥ 2 (current: {gameplayOrderingBufferSize}).",
                    this);
            }

            // Variable batching requires at least one variable per batch;
            // zero or negative values would produce malformed wire packets.
            if (enableVariableBatching && maxVariablesPerBatch < 1)
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: enableVariableBatching requires " +
                    $"maxVariablesPerBatch ≥ 1 (current: {maxVariablesPerBatch}).",
                    this);
            }

            // A configured JWT key is enforced over the key the handshake
            // delivers, so one that does not decode refuses every SessionAck
            // at the first connect rather than being masked by the delivered
            // key; and under Strict pinning the gateway's identity key IS the
            // pin, so a hex that names another key refuses every token an
            // identity-signing gateway issues.  Both are put in front of the
            // integrator here.
            if (jwtSignatureAlgorithm == JwtSignatureAlgorithm.Ed25519
                && !string.IsNullOrWhiteSpace(jwtSigningKeyHex))
            {
                string jwtKeyDecodeError = null;
                try { Crypto.KeyHex.Decode32(jwtSigningKeyHex); }
                catch (System.Exception ex) { jwtKeyDecodeError = ex.Message; }

                if (jwtKeyDecodeError != null)
                {
                    UnityEngine.Debug.LogError(
                        $"[RTMPE] {name}: jwtSigningKeyHex does not decode to a 32-byte key " +
                        $"({jwtKeyDecodeError}).  It is enforced over the key the handshake " +
                        "delivers, so every SessionAck will be refused.  Supply the gateway's " +
                        "64-character hex Ed25519 identity key, or clear the field.",
                        this);
                }
                else if (EffectivePinningMode == ServerPinningMode.Strict
                      && !string.IsNullOrWhiteSpace(pinnedServerPublicKeyHex)
                      && !string.Equals(
                             jwtSigningKeyHex, pinnedServerPublicKeyHex,
                             System.StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[RTMPE] {name}: jwtSigningKeyHex differs from pinnedServerPublicKeyHex.  " +
                        "A gateway that signs its tokens with its identity key — the standard " +
                        "deployment — will have every SessionAck refused by this key.  Set it to " +
                        "the pinned key, or clear it to verify against the key the handshake " +
                        "delivers; keep it only for a gateway configured with a separate " +
                        "GATEWAY_JWT_KEYS keyring.",
                        this);
                }
            }
            else if (jwtSignatureAlgorithm == JwtSignatureAlgorithm.RsaPkcs1Sha256
                  && !string.IsNullOrWhiteSpace(jwtSigningKeyPem)
                  && EffectivePinningMode == ServerPinningMode.Strict)
            {
                // The gateway signs with Ed25519 in both of its modes, so an RSA
                // pin under Strict pinning refuses every token the pinned
                // gateway can issue.
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] {name}: jwtSignatureAlgorithm is RS256 with a PEM configured, under " +
                    "Strict pinning.  The pinned gateway signs its tokens with Ed25519 — its " +
                    "identity key, or a GATEWAY_JWT_KEYS keyring — so every SessionAck will be " +
                    "refused by this configuration.  RS256 is for an external issuer; for the " +
                    "gateway, select Ed25519.",
                    this);
            }
        }
#endif
    }
}
