// RTMPE SDK — Runtime/Core/SpawnManager.cs
//
// Manages the lifecycle of networked objects: prefab registration,
// local instantiation/destruction, and owner-leave cleanup.
//
// Architecture notes:
//   • SpawnManager is the CENTRAL hub for object lifecycle. It owns the
//     NetworkObjectRegistry and OwnershipManager (exposed as properties).
//   • Spawn() / Despawn() create/destroy locally AND send a packet to
//     the server for relay to other clients in the room. When the server
//     relays a Spawn/Despawn to a receiving client, the NetworkManager
//     packet handler calls CreateLocal() / DestroyLocal() directly.
//   • Both are authorised against the object's owner before they commit
//     anything locally (ObjectLifecycleAuthority). The gateway relays a
//     spawn only under the spawner's own identity and a despawn only from
//     the object's owner, and answers a refusal with no packet at all — so
//     an operation committed here and refused there leaves this client
//     holding a world no other client shares. CreateLocal / DestroyLocal
//     carry no such check and must not: they are the receiving side, where
//     a foreign owner is the normal case.
//   • CreateLocal() / DestroyLocal() are internal so only the SDK itself
//     (or tests via InternalsVisibleTo) can call them. These are the
//     primitives that the server-driven spawn path will invoke.
//   • OnPlayerLeftRoom() handles the DestroyWithOwner contract defined
//     on NetworkBehaviour. For objects with DestroyWithOwner=false, the
//     server is responsible for sending ownership grants — no local
//     mutation occurs here (server-authoritative ownership).
//   • ClearAll() is called on disconnect / room leave to tear down all
//     spawned objects (fires OnNetworkDespawn for each, then destroys GOs).
//   • Object IDs are 64-bit values produced by ObjectIdMath.Compose:
//     high 32 bits = avalanche-mixed digest of the FULL u64 gateway
//     session id, low 32 bits = monotonic per-session counter. The wire
//     field (SpawnPacketBuilder: object_id u64 LE) carries the full 64
//     bits with no truncation, and the digest mixes every session-id byte
//     so reconnects that reuse the low half of a prior session id cannot
//     collide with that session's still-live object ids.

using System;
using System.Collections.Generic;
using System.Threading;
using RTMPE.Core.Diagnostics;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Manages network object spawning, prefab registration, and owner-leave cleanup.
    /// Access via <see cref="NetworkManager.Spawner"/>.
    /// All methods must be called from the Unity main thread.
    /// </summary>
    public sealed class SpawnManager
    {
        private readonly NetworkObjectRegistry _registry;
        private readonly OwnershipManager _ownership;
        private readonly NetworkManager _networkManager;

        private readonly Dictionary<uint, GameObject> _prefabs =
            new Dictionary<uint, GameObject>();

        // Remembers which prefab each spawned GameObject came from so that
        // Release() can route it back to the correct pool bucket at despawn.
        // Populated by CreateLocal; consulted (and cleared) in DestroyLocal.
        // Runtime cost is O(N_live_objects) — negligible vs the per-frame
        // registry traversal.
        private readonly Dictionary<ulong, uint> _prefabOfObject =
            new Dictionary<ulong, uint>();

        // Optional pluggable pool — null means "no pooling" (use Instantiate/Destroy).
        // Assigned via SetObjectPool; cleared via ClearObjectPool.  Main-thread only.
        private INetworkObjectPool _pool;

        // ── Spawn rate / count caps ────────────────────────────────────────────
        //
        // A hostile gateway can flood the receiver with Spawn frames in an
        // attempt to exhaust the main-thread Instantiate budget and the
        // GameObject heap (an OOM crash on mobile within seconds).  Two caps
        // gate every CreateLocal entry path:
        //
        //   • _spawnsThisSecond rolls inside a one-second bucket; bursts
        //     above NetworkSettings.maxSpawnsPerSecond are dropped.
        //   • _currentSpawnCount tracks the live object total against
        //     NetworkSettings.maxSpawnsPerRoom regardless of arrival rate.
        //
        // The bucket start is stored as Stopwatch ticks rather than wall-clock
        // ticks so the counter is immune to system-time adjustments (NTP,
        // user clock changes) that could otherwise either freeze the bucket
        // or roll it backwards into a permanent throttle.  The decrement on
        // unregister keeps the live total accurate even when an external
        // caller destroys an object outside the normal Despawn path.
        private int _spawnsThisSecond;
        private long _spawnRateBucketStartTicks;
        private int _currentSpawnCount;
        private bool _rateLimitWarnedThisBucket;
        private bool _countLimitWarnedThisBucket;

        // Re-entry guard: a user callback fired inside CreateLocal (Initialize,
        // OnNetworkSpawn, a custom INetworkObjectPool.Acquire) that synchronously
        // calls back into Spawn / CreateLocal would otherwise observe transient
        // state.  Counters are incremented eagerly (see CreateLocal) so the cap
        // is enforced on the re-entrant call, and this flag exists purely so
        // that re-entry is loud in the logs — silent recursion has historically
        // hidden cap-bypass bugs from review.
        private bool _isCreatingLocal;

        // ── Out-of-order despawn tracking ──────────────────────────────────────
        //
        // UDP reorder can deliver Despawn(id) before Spawn(id) for the same
        // object id when the gateway uses a different relay path for each
        // (rare but real on roaming mobile networks).  Bookkeeping is owned
        // by PendingDespawnTracker which keeps a dictionary, an insertion-
        // order LinkedList, and a side map of (id → node) in lockstep so
        // every state transition is O(1) on every axis and the order list
        // cannot accumulate ghost ids.
        private readonly PendingDespawnTracker _pendingDespawns =
            new PendingDespawnTracker();
        internal const long PendingDespawnTtlMs = PendingDespawnTracker.TtlMs;
        internal const int MaxPendingDespawns = PendingDespawnTracker.MaxEntries;

        // Symmetric bookkeeping for the leave-before-Spawn race: a Spawn (0x30)
        // and its owner's player_left ride different relay paths with no mutual
        // ordering, so a Spawn can land after its owner has left.  This tombstones
        // a departed player so CreateLocal drops that late spawn rather than
        // forming an object no live session can update or despawn.
        private readonly DepartedPlayerTracker _departedPlayers =
            new DepartedPlayerTracker();

        // Monotonic counter for the low 32 bits of locally-generated object IDs.
        // Stored as `long` (not `ulong`) so that `Interlocked.Increment` — whose
        // public overload on .NET Standard 2.1 only accepts `ref long` — can be
        // used to guarantee thread safety.  The contract remains main-thread-only
        // for correctness of the GameObject lifecycle, but atomic increment
        // preserves uniqueness even if a third-party integration accidentally
        // calls Spawn() from a background thread.
        //
        // Starts at 0 so that the first Interlocked.Increment returns 1 — preserving
        // the historical behaviour of the previous post-increment implementation
        // (`_nextLocalId = 1` + `_nextLocalId++`).  The low 32 bits are masked at
        // use-time; values cast back to ulong are always in [1, uint.MaxValue].
        private long _nextLocalId;

        // One warning per second: an application spawning a wave of objects
        // before it has joined a room has one condition, not one per prefab.
        private long _lastPreRoomSpawnWarnTicks;
        // One gate per reason, each a named field. A gateway that is refusing
        // for one reason must not decide whether the other is ever printed, and
        // the unknown-reason line is the one an integrator cannot act on
        // without seeing it.
        private long _lastSpawnCollisionWarnTicks;
        private long _lastSpawnCeilingWarnTicks;
        private long _lastSpawnForeignIdSpaceWarnTicks;
        private long _lastSpawnUnknownRejectWarnTicks;
        // A malformed payload and a reason this build does not know are
        // different faults, and sharing one gate between them means a peer
        // flooding the first decides whether the second is ever printed — the
        // second being the one that tells an integrator to update the SDK.
        private long _lastMalformedSpawnRejectWarnTicks;
        // A subscriber that throws does so once per rejection, on the inbound
        // path, so the exception line is paced by whoever is sending.
        private long _lastSpawnRejectSubscriberThrowWarnTicks;

        // The inbound half of the same rule.  CreateLocal runs once per spawn
        // packet, so a gateway relaying prefab ids this build does not carry —
        // a version skew, a mismatched registration, a peer sending what it
        // pleases — writes one console line per datagram on the main thread.
        // One gate per reason: which of the three faults is happening is the
        // whole content of the line.
        private long _lastUnregisteredPrefabWarnTicks;

        // 🚨 STATIC, and it is the only gate in this class that is. Every other
        // one bounds a per-frame path inside one session, where an instance
        // field is exactly the right scope. This one bounds a reconnect ladder —
        // and a ladder builds a NEW SpawnManager per attempt, so an instance
        // field is discarded before it can refuse anything.
        //
        // ⛔ Measured, because the first version of this line was an instance
        // field under a comment claiming the bound it did not have: five
        // attempts emitted five lines, where one instance emits one.
        private static long _lastPrefabRegistryWarnTicks;
        private long _lastPoolReturnedNullWarnTicks;
        private long _lastPrefabWithoutBehaviourWarnTicks;
        private long _lastRegistrationRefusedWarnTicks;

        // The three the diagnostic's own SPELLING hid: two write a full managed
        // stack trace through Debug.LogException, which is heavier than either
        // severity beside it, and one routes through RtmpeLog, which chooses a
        // severity and never a rate. Each is reached once per inbound despawn,
        // or once per owned object when a player leaves.
        private long _lastPendingDespawnCapWarnTicks;
        private long _lastPoolReleaseThrowWarnTicks;
        private long _lastOwnedObjectTeardownThrowWarnTicks;

        // A spawn packet whose callback re-enters, and a resync that walks every
        // owned object: the first is one line per inbound spawn, the second one
        // stack trace per object on a path a reconnect drives.
        private long _lastReentrantCreateWarnTicks;
        private long _lastResyncFaultWarnTicks;

        // 🚨 Teardown walks every live object, so this is one stack trace PER
        // OBJECT and not, as an exemption of mine once claimed, one per session.
        private long _lastTeardownFaultWarnTicks;

        // Sticky flag latched once GenerateObjectId observes the counter at
        // the u32 ceiling.  Subsequent Spawn calls fail loudly until the
        // session is reset via ClearAll — preventing a silent wrap that
        // would re-issue an id whose previous owner is still alive on the
        // wire and blow up registry uniqueness.  The ceiling is well past
        // any plausible session lifetime (≈49 days at 1k spawns/sec) so
        // hitting it in production almost certainly indicates a leak or
        // hostile flooding rather than legitimate throughput.
        private bool _localIdSpaceExhausted;

        // Tracks objects whose DestroyLocal teardown has begun but not yet
        // completed.  A re-entrant Despawn (server-relayed echo arriving
        // inside the same call stack, an OnNetworkDespawn callback that
        // synchronously calls back into Despawn, or Unity's own OnDestroy
        // dispatched mid-teardown) checks this set and short-circuits, so
        // the registry-presence-based idempotency cannot misclassify the
        // re-entry as "Despawn arrived before Spawn" and record a stray
        // pending-despawn TTL entry.
        private readonly HashSet<ulong> _despawnInFlight = new HashSet<ulong>();

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>The shared object registry for all spawned objects.</summary>
        public NetworkObjectRegistry Registry => _registry;

        /// <summary>The ownership manager for server-authoritative ownership.</summary>
        public OwnershipManager Ownership => _ownership;

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Create a SpawnManager.
        /// </summary>
        /// <param name="registry">Shared object registry.</param>
        /// <param name="ownership">Ownership manager (created by this or externally).</param>
        /// <param name="networkManager">Active NetworkManager for ID generation and future sends.</param>
        public SpawnManager(
            NetworkObjectRegistry registry,
            OwnershipManager ownership,
            NetworkManager networkManager)
        {
            _registry       = registry       ?? throw new ArgumentNullException(nameof(registry));
            _ownership      = ownership      ?? throw new ArgumentNullException(nameof(ownership));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        }

        // ── Prefab Registration ────────────────────────────────────────────────

        /// <summary>
        /// Register a prefab for network spawning.
        /// The prefab must have a <see cref="NetworkBehaviour"/> component.
        /// Overwrites any previous registration with the same ID (logs warning).
        /// </summary>
        /// <param name="prefabId">Unique identifier for this prefab type.</param>
        /// <param name="prefab">The prefab <c>GameObject</c> to register.</param>
        public void RegisterPrefab(uint prefabId, GameObject prefab)
        {
            if (prefab == null)
                throw new ArgumentNullException(nameof(prefab));

            if (_prefabs.ContainsKey(prefabId))
                Debug.LogWarning(
                    $"[SpawnManager] RegisterPrefab: overwriting existing prefabId {prefabId}.");

            _prefabs[prefabId] = prefab;
        }

        /// <summary>
        /// Remove a prefab registration. Returns true if it was registered.
        /// </summary>
        public bool UnregisterPrefab(uint prefabId) => _prefabs.Remove(prefabId);

        /// <summary>True if a prefab is registered under <paramref name="prefabId"/>.</summary>
        public bool HasPrefab(uint prefabId) => _prefabs.ContainsKey(prefabId);

        /// <summary>
        /// The id this session would spawn <paramref name="prefab"/> under, or
        /// <see langword="false"/> when none is registered for it.
        /// </summary>
        /// <remarks>
        /// 🔑 The reverse of <see cref="RegisterPrefab"/>, and it exists because
        /// the alternative is a guess. An application holds a prefab reference —
        /// an Inspector field, a pool, a spawn table of its own — and needs the
        /// number the room agrees on; the ids themselves come from the Editor
        /// ledger, which surfaces them as <c>RtmpePrefabIds</c> constants that
        /// exist only in a project that has generated them. So the code that
        /// cannot name a constant used to scan
        /// <see cref="NetworkPrefabRegistry.Entries"/> instead.
        /// <para>
        /// ⛔ That scan is not this question. The registry is one of the table's
        /// two writers and the weaker one — <see cref="AdoptPrefabsFrom"/>
        /// applies hand registrations AFTER it, deliberately, so an id the asset
        /// gives one prefab may belong to another here. A caller that resolved
        /// from the asset would hand <see cref="Spawn"/> an id that is
        /// registered to something else, and nothing would refuse it: the spawn
        /// path asks whether the id is known, never which prefab it names.
        /// </para>
        /// <para>
        /// Where several ids name one prefab the lowest is returned, so two
        /// clients asking the same question of the same table get the same
        /// answer rather than one that depends on enumeration order.
        /// </para>
        /// </remarks>
        /// <param name="prefab">The prefab to find an id for.</param>
        /// <param name="prefabId">The id, when one is registered.</param>
        public bool TryGetPrefabId(GameObject prefab, out uint prefabId)
            => PrefabTableOps.TryFindId(_prefabs, prefab, out prefabId);

        /// <summary>
        /// Adopt the prefab registrations of a prior SpawnManager. The prefab
        /// table is static configuration rather than session state, so it carries
        /// across the SpawnManager rebuild a (re)connect performs — a prefab
        /// registered once stays registered for the application's lifetime,
        /// independent of when it was registered relative to <c>Connect</c>.
        /// </summary>
        internal void AdoptPrefabsFrom(SpawnManager previous)
        {
            if (previous == null) return;
            PrefabTableOps.CopyInto(previous._prefabs, _prefabs);
        }

        /// <summary>
        /// Load a generated <see cref="NetworkPrefabRegistry"/> into the prefab
        /// table, so a project registers its prefabs by assigning an asset
        /// rather than by writing a <see cref="RegisterPrefab"/> call per prefab
        /// and remembering where to put it.
        /// </summary>
        /// <remarks>
        /// Called when a session is built, and BEFORE
        /// <see cref="AdoptPrefabsFrom"/>: a registration the application made
        /// by hand outranks the generated table, so a project can override one
        /// row without abandoning the asset for all of them.
        /// <para>
        /// A null registry is not an error. The asset is opt-in, and every
        /// project that predates it has none.
        /// </para>
        /// <para>
        /// Public so an application can load a registry it obtained for itself —
        /// from an asset bundle, from Addressables, from downloadable content —
        /// which is the case the documentation otherwise sends to a
        /// <see cref="RegisterPrefab"/> call per prefab.
        /// </para>
        /// </remarks>
        public void LoadPrefabRegistry(NetworkPrefabRegistry registry)
        {
            if (registry == null) return;

            // The message is composed where a test can read it. No project in
            // this repository compiles this file, so a line written here is a
            // line nothing can hold — which is how the gate above spent a whole
            // batch claiming a bound it did not have.
            string report = PrefabTableOps.DescribeLoad(
                registry.name, PrefabTableOps.LoadInto(registry.Entries, _prefabs));
            if (report == null) return;
            if (!WarnGate.ShouldEmit(ref _lastPrefabRegistryWarnTicks)) return;

            Debug.LogWarning(report);
        }

        // ── Pooling ────────────────────────────────────────────────────────────

        /// <summary>
        /// Install a custom <see cref="INetworkObjectPool"/>.
        /// From the next spawn onwards, <see cref="SpawnManager"/> routes every
        /// instantiate/destroy through the pool.  Pass <see langword="null"/>
        /// to restore the built-in Instantiate/Destroy path.
        /// </summary>
        /// <remarks>
        /// Swapping pools at runtime is allowed but does NOT migrate already-live
        /// objects — they will be released to whichever pool is active at despawn
        /// time.  Apps that need to migrate should despawn + respawn explicitly.
        /// </remarks>
        public void SetObjectPool(INetworkObjectPool pool) => _pool = pool;

        /// <summary>Remove any installed pool, reverting to Instantiate/Destroy.</summary>
        public void ClearObjectPool() => _pool = null;

        /// <summary>The currently-installed pool, or <see langword="null"/> when none is set.</summary>
        public INetworkObjectPool ObjectPool => _pool;

        // ── Spawn / Despawn (Public API) ───────────────────────────────────────

        /// <summary>
        /// Spawn a networked object from a registered prefab.
        ///
        /// Creates the object locally and sends a SpawnRequest to the server
        /// for relay to other clients in the room.
        /// </summary>
        /// <param name="prefabId">Registered prefab identifier.</param>
        /// <param name="position">World-space spawn position.</param>
        /// <param name="rotation">World-space spawn rotation.</param>
        /// <param name="ownerPlayerId">
        /// Room player UUID of the owner. When <see langword="null"/>,
        /// defaults to <see cref="NetworkManager.LocalPlayerStringId"/>.
        ///
        /// <para>A client may only spawn under its own identity: the gateway
        /// refuses a spawn whose owner claim names another player, and relays
        /// nothing, so naming one here returns <see langword="null"/> rather
        /// than an object no other client would ever see.  To seat an object
        /// with somebody else, spawn it locally and hand it over with
        /// <see cref="OwnershipManager.RequestOwnershipTransfer"/>.</para>
        /// </param>
        /// <param name="sharedAuthority">
        /// When <see langword="true"/>, any member of the room may address this
        /// object with an Enhanced RPC.  Leave it <see langword="false"/> —
        /// the default — for an object only its owner should drive (audit
        /// <c>S4-02</c>).
        ///
        /// <para>The default is restrictive on purpose.  Before this axis
        /// existed the gateway relayed every RPC without consulting ownership,
        /// so any room member could invoke any method on anyone's object; an
        /// opt-out default would have preserved exactly that.  Mark the objects
        /// a room genuinely shares — a door, a scoreboard, a world button —
        /// and leave player-driven objects alone.</para>
        /// </param>
        /// <returns>The spawned <see cref="NetworkBehaviour"/>, or null on failure.</returns>
        public NetworkBehaviour Spawn(
            uint prefabId,
            Vector3 position,
            Quaternion rotation,
            string ownerPlayerId = null,
            bool sharedAuthority = false)
        {
            if (!_prefabs.ContainsKey(prefabId))
            {
                Debug.LogError($"[SpawnManager] Spawn: prefab {prefabId} is not registered.");
                return null;
            }

            // An empty owner reads as "none specified", not as "deliberately
            // unowned".  The gateway refuses an in-room spawn that claims no
            // owner at all (SpawnAuthority::UnownedInRoom) — only a modified
            // client emits one — so resolving it to this client's own identity
            // keeps an honest call working and leaves the refusal below to the
            // one case that genuinely names somebody else.  Before the room
            // reply arrives there is no identity to resolve to; that spawn is
            // refused by the gateway as well (SpawnAuthority::NoClaim) and is
            // admitted here anyway, because refusing it would take away a local
            // object rather than prevent a divergence — see
            // ObjectLifecycleAuthority.
            var owner = string.IsNullOrEmpty(ownerPlayerId)
                ? (_networkManager.LocalPlayerStringId ?? string.Empty)
                : ownerPlayerId;

            // Refused ahead of GenerateObjectId so a rejected spawn spends
            // nothing from the per-session id space.
            if (ObjectLifecycleAuthority.RefusesForeignOwner(
                    owner, _networkManager.LocalPlayerStringId))
            {
                Debug.LogError(
                    "[SpawnManager] Spawn refused: ownerPlayerId " +
                    $"'{UntrustedLogText.Sanitise(owner)}' is not the local player. " +
                    "The gateway relays a spawn only under the spawner's own identity, " +
                    "so this object would exist on this client alone. Spawn it under " +
                    "the local player and hand it over with " +
                    "OwnershipManager.RequestOwnershipTransfer, or have its intended " +
                    "owner spawn it.");
                return null;
            }

            // A spawn made before this client has been told its seat carries no
            // owner claim the gateway can accept — classify_spawn_authority
            // answers NoClaim and forward_spawn refuses that arm — so the object
            // is created here and relayed to nobody, and nothing re-announces it
            // when the room is later joined.  It is admitted, because a local
            // object is still a usable object and the caller may want one; what
            // was missing is that the SDK said nothing at all about it, while
            // Spawn's own documentation declares the precondition.
            if (ObjectLifecycleAuthority.SpawnReachesNobody(
                    _networkManager.IsConnected, _networkManager.LocalPlayerStringId)
                && WarnGate.ShouldEmit(ref _lastPreRoomSpawnWarnTicks))
            {
                Debug.LogWarning(
                    $"[SpawnManager] Spawn of prefab {prefabId} before this client has a " +
                    "room seat: the object is created locally, reaches no other client, and " +
                    "is not re-announced when a room is later joined. Spawn after " +
                    "OnRoomCreated or OnRoomJoined fires.");
            }

            var objectId = GenerateObjectId();

            var nb = CreateLocal(prefabId, objectId, owner, position, rotation);
            if (nb != null)
                SendSpawnPacket(
                    prefabId, objectId, owner, position, rotation, sharedAuthority,
                    // Taken from the object that was actually created, because
                    // this is a statement about what every client will do with
                    // it — and what they will do is read this component's flag.
                    nb.DestroyWithOwner);
            return nb;
        }

        /// <summary>
        /// Fired when the room refused a <see cref="Spawn"/> this client asked
        /// for, naming the object and why.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The object is <b>not</b> destroyed. The application asked for it,
        /// holds the reference and may already have wired it into a scene, so
        /// tearing it down asynchronously would trade one surprise for a worse
        /// one — the same reasoning that admits a pre-room spawn in
        /// <see cref="Spawn"/>. What it will not do is reach another player: it
        /// exists on this client alone, ticks, runs its behaviours and accepts
        /// writes, and no peer will ever hear of it. A game that cannot use such
        /// an object should destroy it from this handler — through the
        /// <c>NetworkBehaviour</c> reference <see cref="Spawn"/> returned, not
        /// through <see cref="Despawn"/>, which relays a despawn to the room
        /// for an object the room never accepted.
        /// </para>
        /// <para>
        /// Only refusals this client could not have derived arrive here. A spawn
        /// with an owner that is not this client's, one with no owner declared
        /// while in a room, and one issued before the join reply has seated this
        /// client are all decidable locally, and are reported where they are
        /// decided rather than after a round trip.
        /// </para>
        /// <para>
        /// ⚠️ Delivery is best-effort, like every other server-originated
        /// packet: a lost <c>SpawnRejected</c> leaves the object exactly as it
        /// was before this event existed. The window is narrower than the
        /// unconditional silence it replaces; it is not zero.
        /// </para>
        /// </remarks>
        public event Action<ulong, SpawnPacketParser.SpawnRejectReason> OnSpawnRejected;

        /// <summary>
        /// Apply a <c>SpawnRejected</c> (0x32) payload.
        /// </summary>
        internal void HandleSpawnRejected(byte[] payload)
        {
            if (!SpawnPacketParser.TryParseSpawnRejected(
                    payload, out ulong objectId, out var reason))
            {
                if (WarnGate.ShouldEmit(ref _lastMalformedSpawnRejectWarnTicks))
                    Debug.LogWarning(
                        "[SpawnManager] Malformed SpawnRejected payload, dropped.");
                return;
            }

            // ⛔ The gate holds the log line and nothing else. An application
            // event is not a diagnostic, and a gate around one answers the
            // second refusal in a second with silence — which is the trade a
            // repair one batch earlier had to have undone.
            switch (reason)
            {
                case SpawnPacketParser.SpawnRejectReason.OwnerCollision:
                    if (WarnGate.ShouldEmit(ref _lastSpawnCollisionWarnTicks))
                        Debug.LogWarning(
                            $"[SpawnManager] The room refused the spawn of object {objectId}: " +
                            "that object id already belongs to another player. The object " +
                            "exists on this client and reaches no other.");
                    break;
                case SpawnPacketParser.SpawnRejectReason.RoomAtObjectCeiling:
                    if (WarnGate.ShouldEmit(ref _lastSpawnCeilingWarnTicks))
                        Debug.LogWarning(
                            $"[SpawnManager] The room refused the spawn of object {objectId}: " +
                            "the room is at its object ceiling. The object exists on this " +
                            "client and reaches no other.");
                    break;
                case SpawnPacketParser.SpawnRejectReason.ForeignIdSpace:
                    if (WarnGate.ShouldEmit(ref _lastSpawnForeignIdSpaceWarnTicks))
                        Debug.LogWarning(
                            $"[SpawnManager] The room refused the spawn of object {objectId}: " +
                            "that object id belongs to another session's id space. The object " +
                            "exists on this client and reaches no other. An id this SDK " +
                            "allocated cannot be refused this way, so the session that " +
                            "allocated it is no longer the one this client is connected on.");
                    break;
                default:
                    if (WarnGate.ShouldEmit(ref _lastSpawnUnknownRejectWarnTicks))
                        Debug.LogWarning(
                            $"[SpawnManager] The room refused the spawn of object {objectId} " +
                            "for a reason this SDK build does not know. Update the SDK to " +
                            "match the gateway.");
                    break;
            }

            SafeRaiseRejected(objectId, reason);
        }

        // Same discipline as RoomManager.SafeRaise: these run during inbound
        // packet processing, so one subscriber throwing must not deny delivery
        // to those behind it or abandon the rest of the frame.
        private void SafeRaiseRejected(
            ulong objectId, SpawnPacketParser.SpawnRejectReason reason)
        {
            var handler = OnSpawnRejected;
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    ((Action<ulong, SpawnPacketParser.SpawnRejectReason>)subs[i])(
                        objectId, reason);
                }
                catch (Exception ex)
                {
                    if (WarnGate.ShouldEmit(ref _lastSpawnRejectSubscriberThrowWarnTicks))
                        Debug.LogException(ex);
                }
            }
        }

        /// <summary>
        /// Whether <see cref="Despawn"/> would be relayed to the room, or
        /// refused because the object belongs to another player.
        /// </summary>
        /// <remarks>
        /// True for an object this client owns, for one that carries no owner
        /// claim, and for an id this client has never seen — the cases the
        /// gateway does not refuse.  Consult it when the choice of whether to
        /// offer a "destroy" affordance is the caller's; <see cref="Despawn"/>
        /// asks the same question again, so a caller that simply wants the
        /// object gone need not.
        /// </remarks>
        /// <param name="objectId">The network object ID to test.</param>
        public bool CanDespawn(ulong objectId)
        {
            var nb = _registry.Get(objectId);
            return nb == null
                || !ObjectLifecycleAuthority.RefusesForeignOwner(
                       nb.OwnerPlayerId, _networkManager.LocalPlayerStringId);
        }

        /// <summary>
        /// Despawn the object if this client may, reporting the outcome instead
        /// of logging it.
        /// </summary>
        /// <remarks>
        /// <see cref="Despawn"/> returns void, so a refusal there is visible
        /// only in the console.  This overload is for a caller that wants to
        /// branch on it.
        /// </remarks>
        /// <param name="objectId">The network object ID to despawn.</param>
        /// <returns>
        /// False when the object belongs to another player and nothing was
        /// done; true when <see cref="Despawn"/> was called.  ⚠️ True is not a
        /// receipt: a re-entrant call for an id already being torn down is a
        /// no-op there, and this returns true for it — the answer is about
        /// authority, which is the question a caller cannot ask any other way.
        /// </returns>
        public bool TryDespawn(ulong objectId)
        {
            if (!CanDespawn(objectId)) return false;
            Despawn(objectId);
            return true;
        }

        /// <summary>
        /// Despawn (destroy) a networked object.
        ///
        /// Destroys the object locally and sends a DespawnRequest to the server
        /// for relay to other clients in the room.  Refused, with a message, for
        /// an object owned by another player — see <see cref="CanDespawn"/>.
        /// </summary>
        /// <param name="objectId">The network object ID to despawn.</param>
        public void Despawn(ulong objectId)
        {
            // Re-entrant suppression: if a callback fired from inside an
            // outer DestroyLocal calls back into Despawn for the same id,
            // observe a no-op for both the local teardown AND the wire
            // send.  Without this gate the inner call would emit a second
            // DespawnRequest packet for the same object, which is wasted
            // bandwidth and surfaces as a duplicate-despawn at peers.
            if (_despawnInFlight.Contains(objectId)) return;

            // Only the owner's despawn is relayed; the gateway drops anyone
            // else's and answers nothing.  Tearing the object down here anyway
            // would remove it from this client and leave it standing for every
            // other — the one outcome a destroy call must not produce silently.
            if (!CanDespawn(objectId))
            {
                Debug.LogError(
                    $"[SpawnManager] Despawn refused for object {objectId}: it is owned " +
                    $"by '{UntrustedLogText.Sanitise(_registry.Get(objectId)?.OwnerPlayerId)}', " +
                    "not by the local player. The gateway relays a despawn only from the " +
                    "object's owner, so destroying it here would remove it from this client " +
                    "alone. Ask the owner to despawn it, or take ownership first with " +
                    "OwnershipManager.RequestOwnershipTransfer.");
                return;
            }

            // Tear down the local instance BEFORE the wire send so that any
            // re-entrant Despawn arriving inside the same call stack
            // observes an empty registry slot and short-circuits.
            // DestroyLocal is idempotent: the in-flight set + the
            // registry-presence check together guarantee a second call
            // does not double-decrement counters, fire callbacks twice,
            // or record a stale pending-despawn entry.
            DestroyLocal(objectId);

            SendDespawnPacket(objectId);
        }

        // ── Internal: Server-Driven Operations ─────────────────────────────────

        /// <summary>
        /// Instantiate a networked object locally from a registered prefab.
        /// Called by the inbound Spawn packet handler or by <see cref="Spawn"/>.
        /// </summary>
        /// <returns>The spawned <see cref="NetworkBehaviour"/>, or null on failure.</returns>
        internal NetworkBehaviour CreateLocal(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            bool? declaredDestroyWithOwner = null)
            => CreateLocal(prefabId, objectId, ownerPlayerId, position, rotation,
                           exemptFromRateCap: false,
                           declaredDestroyWithOwner: declaredDestroyWithOwner);

        /// <summary>
        /// Instantiate a networked object from the pre-room staging buffer's
        /// release, exempt from the per-second rate cap.
        /// </summary>
        /// <remarks>
        /// That cap bounds a SUSTAINED stream and DISCARDS what it refuses.  The
        /// staged set is the one burst a client is handed that it can never ask
        /// for again — the room's live objects, replayed once as the session
        /// binds — so metering it against the cap loses most of it, which is the
        /// whole of <c>CORE-RD-01</c>.  It is bounded by the staging buffer's
        /// capacity and released a bounded number per frame by the caller, and
        /// ⛔ the per-room COUNT cap below still applies to every one of them,
        /// so the allocation bound this class exists to enforce is intact.
        /// </remarks>
        internal NetworkBehaviour CreateLocalFromStagedCatchUp(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            bool? declaredDestroyWithOwner = null)
            => CreateLocal(prefabId, objectId, ownerPlayerId, position, rotation,
                           exemptFromRateCap: true,
                           declaredDestroyWithOwner: declaredDestroyWithOwner);

        private NetworkBehaviour CreateLocal(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            bool exemptFromRateCap,
            bool? declaredDestroyWithOwner = null)
        {
            // Zero is never a valid network object ID (defense-in-depth;
            // the wire parser already rejects it before reaching this path).
            if (objectId == 0) return null;

            // Out-of-order Despawn-before-Spawn: if a despawn for this id
            // landed first (still inside its TTL), the object is logically
            // dead — creating it now would produce a ghost.  Consume the
            // pending entry and skip the spawn.
            _pendingDespawns.Prune(NowMillis());
            if (_pendingDespawns.Consume(objectId))
            {
                RtmpeLog.Info(
                    "[SpawnManager] Spawn dropped: matching Despawn already arrived (out-of-order delivery).");
                return null;
            }

            // Leave-before-Spawn: a Spawn whose owner has already left the room
            // raced behind that player's player_left on a separate relay path.
            // Creating it would leave an object owned by a departed player that no
            // live session can update or despawn (a permanent ghost); drop it.
            // For the default DestroyWithOwner=true object this is exactly right —
            // an in-order arrival would have been destroyed with its owner.  A
            // DestroyWithOwner=false object caught in this narrow pre-leave race is
            // dropped too: host reassignment only reaches objects already
            // registered when the owner left, so a still-in-flight spawn is out of
            // its scope — an accepted limitation for that rare opt-in.
            if (_departedPlayers.IsDeparted(ownerPlayerId, NowMillis()))
            {
                RtmpeLog.Info(
                    "[SpawnManager] Spawn dropped: owner already left the room (late spawn after player_left).");
                return null;
            }

            // Rate / count gates run before any Instantiate work so a flood of
            // hostile spawns is rejected before allocating a GameObject.
            if (!CheckSpawnAdmission(exemptFromRateCap)) return null;

            if (!_prefabs.TryGetValue(prefabId, out var prefab))
            {
                if (WarnGate.ShouldEmit(ref _lastUnregisteredPrefabWarnTicks))
                    Debug.LogWarning(
                        $"[SpawnManager] CreateLocal: prefab {prefabId} not registered.");
                return null;
            }

            // Re-entry visibility: a user callback fired during CreateLocal
            // that synchronously re-enters CreateLocal observes the eagerly
            // incremented counters, so the cap holds — but log it because
            // recursive spawn is almost always a bug worth surfacing.
            if (_isCreatingLocal)
            {
                if (WarnGate.ShouldEmit(ref _lastReentrantCreateWarnTicks))
                    RtmpeLog.Warning(
                        "[SpawnManager] CreateLocal re-entered from a user callback; " +
                        "the spawn cap is still enforced on the inner call.");
            }

            // Counters incremented eagerly so a user callback that re-enters
            // CreateLocal observes the post-spawn count, not the pre-spawn
            // count.  The Initialize / OnNetworkSpawn / pool-Acquire callbacks
            // below all run user code; without eager increment, recursion
            // depth N would let N extra spawns slip past the per-room cap
            // (each inner call sees the outer's not-yet-applied increment).
            _spawnsThisSecond++;
            _currentSpawnCount++;

            bool committed = false;
            NetworkBehaviour[] rollbackComponents = null;
            // Hoisted out of the try for one reason: the rollback below has to
            // be able to reach the instance.  Declared inside, it could not —
            // which is why the rollback unregistered a failed spawn and left its
            // GameObject standing in the scene.
            GameObject acquired = null;
            bool prevCreating = _isCreatingLocal;
            _isCreatingLocal = true;
            try
            {
                // Route through the pool when installed; fall back to Instantiate
                // otherwise.  A null return from a pool is a contract violation and
                // is treated as a fatal error — the surrounding game code assumes
                // Spawn produces a live object.
                // ⚠️ Published the instant each branch produces an instance,
                // never at the join below it.  Two statements run against a
                // pooled instance between Acquire and that join — a transform
                // write and a SetActive — and either throws on an instance the
                // pool held across a scene unload.  Published at the join, the
                // rollback would find nothing to destroy on exactly the path a
                // pool makes likely.
                GameObject go;
                if (_pool != null)
                {
                    go = acquired = _pool.Acquire(prefabId, prefab, position, rotation);
                    if (go == null)
                    {
                        if (WarnGate.ShouldEmit(ref _lastPoolReturnedNullWarnTicks))
                            Debug.LogError(
                                $"[SpawnManager] CreateLocal: INetworkObjectPool.Acquire " +
                                $"returned null for prefabId {prefabId}. Pools MUST return " +
                                "a live GameObject. Falling back to Instantiate this time.");
                        go = acquired = UnityEngine.Object.Instantiate(prefab, position, rotation);
                    }
                    else
                    {
                        // The pool may have handed us a cached instance whose position
                        // was set at a previous despawn.  Force both transform fields
                        // before the NetworkBehaviour wakes up so OnNetworkSpawn sees
                        // the correct pose.  Use localPosition/localRotation for safety
                        // when the pool parents instances under a reuse bucket.
                        // ⛔ A wire-driven transform write that the axis gates deliberately do
                        // NOT cover. `_syncPosition` / `_syncRotation` decide whether a peer may
                        // MOVE this object during its life; where it first appears is not that
                        // question — an object placed at the origin because its spawner's axis
                        // was unsynced would be wrong for everyone, including the spawner.
                        // Stated here because it is the one place the gates' contract does not
                        // reach, and an unstated exception is one somebody closes by mistake.
                        go.transform.SetPositionAndRotation(position, rotation);
                        if (!go.activeSelf) go.SetActive(true);
                    }
                }
                else
                {
                    go = acquired = UnityEngine.Object.Instantiate(prefab, position, rotation);
                }

                // A networked object carries one NetworkBehaviour per script and
                // always at least two — NetworkTransform is itself a
                // NetworkBehaviour and is required on any synced object — so the
                // spawn lifecycle must drive every component, not just the anchor.
                var nbs = rollbackComponents = go.GetComponents<NetworkBehaviour>();
                NetworkBehaviour anchor = null;
                for (int i = 0; i < nbs.Length; i++)
                {
                    if (nbs[i] != null) { anchor = nbs[i]; break; }
                }
                if (anchor == null)
                {
                    if (WarnGate.ShouldEmit(ref _lastPrefabWithoutBehaviourWarnTicks))
                        Debug.LogError(
                            $"[SpawnManager] CreateLocal: prefab {prefabId} has no " +
                            "NetworkBehaviour component. Destroying instantiated object.");
                    // A pooled instance that lost its NetworkBehaviour somehow —
                    // destroy rather than returning it to the pool to prevent a
                    // corrupted instance from being reused.
                    UnityEngine.Object.Destroy(go);
                    // Hand the rollback an empty binding: this is the one exit
                    // inside the try that disposes of the instance itself, and
                    // ⚠️ Unity's Object.Destroy is DEFERRED — `go == null` stays
                    // false for the rest of this frame — so without clearing it
                    // the rollback below would issue a second Destroy.  Harmless
                    // in Unity, but the rollback would be acting on a belief
                    // that is not true, which is how a later edit inherits one.
                    acquired = null;
                    return null;
                }

                // Two-phase publish (invariant: an object is never visible
                // through _registry.Get() while still mid-initialisation).
                //
                // Phase 1 — finalise initialisation: initialise then wake EVERY
                // NetworkBehaviour and fire its user OnNetworkSpawn callbacks
                // BEFORE the registry adopts the anchor.  Two passes so a callback
                // that reaches a sibling observes it already carrying the object's
                // id and owner.  A re-entrant lookup from within OnNetworkSpawn
                // therefore observes either "not yet registered" or "fully
                // initialised" — never a half-initialised state.
                SpawnLifecycleOps.InitializeAll(nbs, objectId, ownerPlayerId ?? string.Empty);

                // How an object leaves its owner's departure is a property of
                // the object, and the spawner is the only party in a position to
                // state it.  The room is told on the spawn and acts on it — the
                // ownership record is released for a declared object, and an
                // object no record names is one every member may drive — so a
                // receiver answering the same question from its own component
                // would be deciding a departure the room had already been told
                // the other answer to.  One declaration, applied to every
                // component before the spawn callbacks run, so a component that
                // reads it reads what the room believes.
                if (declaredDestroyWithOwner.HasValue)
                {
                    for (int i = 0; i < nbs.Length; i++)
                        if (nbs[i] != null) nbs[i].DestroyWithOwner = declaredDestroyWithOwner.Value;
                }

                SpawnLifecycleOps.SpawnAll(nbs);

                // Phase 2 — atomic publish: register exactly one anchor.  Inbound
                // routing is one-NetworkBehaviour-per-object; the anchor is the
                // first component, identical to the prior GetComponent result, so
                // variable / RPC / state routing is unchanged.  _prefabOfObject is
                // updated alongside the registry slot to keep the lifecycle
                // bookkeeping coupled.
                if (!_registry.Register(anchor, out var evicted))
                {
                    // The registry refused the slot, so nothing routes to this
                    // object: no variable update, no RPC, no despawn.  Reporting
                    // the anchor anyway hands the caller a live GameObject that
                    // is networked in appearance only — initialised, spawned,
                    // rendering and ticking, and reachable through no SDK call
                    // for the rest of the session.
                    //
                    // Leaving `committed` false is the whole of the repair: the
                    // rollback below already unspawns the components and destroys
                    // the instance, for the failure one line further on.  This
                    // returns null, which is what the summary on this method has
                    // always promised for a failure.
                    if (WarnGate.ShouldEmit(ref _lastRegistrationRefusedWarnTicks))
                        Debug.LogError(
                            $"[SpawnManager] CreateLocal: the registry refused object "
                            + $"{objectId} (prefabId {prefabId}); the spawn is being rolled "
                            + "back rather than reported as a live object nothing can reach.");
                    return null;
                }

                // A same-id collision evicts rather than refuses, so the
                // instance that held this slot stopped being a networked object
                // one line above.  Two things are owed and they are owed
                // together: the count it was charged, which the registry states
                // as an obligation it cannot discharge because the count lives
                // above it; and the claim on its teardown, which is only sound
                // once that count is back.  Left unclaimed, the teardown
                // reconciles by object id — and the id now names the object
                // being registered here, so it would unregister a live object
                // and leave it broadcasting into a slot nothing routes to.
                if (evicted != null)
                {
                    if (_currentSpawnCount > 0) _currentSpawnCount--;
                    SpawnLifecycleOps.MarkEvictedAll(evicted.ObjectComponents);
                }

                _prefabOfObject[objectId] = prefabId;

                committed = true;
                return anchor;
            }
            finally
            {
                _isCreatingLocal = prevCreating;
                // Roll the eager increments back when the spawn never produced
                // a live registered object (null prefab GO, missing component,
                // user callback throw).  The catch clause inside CheckSpawn-
                // Admission already runs before this point, so failure here
                // is strictly post-admission and never a false-positive
                // cap-headroom restore.
                //
                // When the failure lands AFTER _registry.Register / the
                // prefab-map insertion (e.g. a user OnNetworkSpawn callback
                // throws inside SetSpawned), the eager-increment rollback
                // alone leaves the registry in an inconsistent state: the
                // counter has been rolled back but the registry slot is
                // still occupied.  Later in the session, when the
                // not-properly-spawned GameObject is destroyed, the
                // OnExternallyDestroyed path decrements the counter a
                // SECOND time — undercounting the live population by one
                // and silently extending the per-room spawn cap.  Roll
                // back the registry insertion together with the counter
                // so the live-set / counter / prefab-map invariant is
                // exact across every failure path.
                if (!committed)
                {
                    if (_spawnsThisSecond > 0) _spawnsThisSecond--;
                    if (_currentSpawnCount > 0) _currentSpawnCount--;
                    _registry.Unregister(objectId);
                    _prefabOfObject.Remove(objectId);

                    // ⚠️ And the instance itself.  Rolling back the bookkeeping
                    // without it left a GameObject in the scene that nothing
                    // tracks: not in the registry, not in the count, not
                    // reachable through any SDK call — so it is never despawned,
                    // never destroyed, and on a prefab that renders or ticks it
                    // keeps doing both.  A user OnNetworkSpawn that throws is
                    // all it takes, and SpawnAll takes no fault reporter where
                    // its sibling UnspawnAll does.
                    //
                    // ⛔ Destroyed rather than released, even when a pool is
                    // installed, and the reason is this rollback's own — NOT the
                    // one the branch above gives.  That branch refuses a
                    // STRUCTURALLY invalid instance and no user code has run on
                    // it; this one refuses an instance whose OnNetworkSpawn
                    // callbacks have run an unknown amount of application code
                    // and left it in an unknown state.  Reissuing that is worse
                    // than losing it.  ⚠️ It is a real cost, not a free choice: a
                    // pool that tracks issued instances loses one permanently,
                    // because Release is how it learns.  A pool that needs that
                    // accounting should treat a never-returned instance as
                    // destroyed, which is what INetworkObjectPool.Release already
                    // tells it to do with the uint.MaxValue sentinel.
                    //
                    // ⛔ The binding is null on exactly two paths and both are
                    // deliberate: before the instance exists, and after the
                    // one exit inside the try that destroys it itself.  It is
                    // NOT null merely because that exit destroyed the object —
                    // Unity's Destroy is deferred and `== null` stays false for
                    // the rest of the frame, which is why that exit clears the
                    // binding explicitly instead of relying on the operator.
                    // ⚠️ Tear the components down, exactly as DestroyLocal does
                    // — DespawnAll, not MarkEvictedAll.  Marking alone is only a
                    // CLAIM on the counter reconciliation: it stops each
                    // component's deferred OnDestroy reporting an external
                    // destruction against an id InitializeAll has already
                    // written (which would let the corpse of a failed spawn
                    // deregister a same-frame respawn of the same id) — and it
                    // fires no OnNetworkDespawn and releases no spawn-scoped
                    // registration.  SpawnAll may have carried several
                    // components through OnNetworkSpawn before one of them
                    // threw; those ran their spawn and are owed their unspawn.
                    // ⛔ An earlier draft of this rollback marked and stopped
                    // there, under a comment claiming parity with DestroyLocal
                    // and ClearAll — both of which unspawn.
                    if (rollbackComponents != null)
                        SpawnLifecycleOps.DespawnAll(rollbackComponents, LogDespawnFault);

                    if (acquired != null)
                        UnityEngine.Object.Destroy(acquired);
                }
            }
        }

        /// <summary>
        /// Destroy a networked object locally.
        /// Fires <see cref="NetworkBehaviour.OnNetworkDespawn"/>, unregisters,
        /// then destroys the <c>GameObject</c>.
        /// </summary>
        internal void DestroyLocal(ulong objectId)
        {
            // Re-entrant teardown short-circuit: a callback fired from
            // within DestroyLocal that calls back here for the SAME id
            // (server-echoed despawn arriving mid-teardown, user code
            // re-invoking Despawn from OnNetworkDespawn, Unity's own
            // OnDestroy dispatched as a side effect of the impending
            // Object.Destroy below) must observe a no-op.  Without this
            // guard the second call would see an already-Unregister'd
            // registry slot, fall through to the "Despawn before Spawn"
            // branch, and record a stale pending-despawn entry whose
            // matching Spawn will never arrive.
            if (_despawnInFlight.Contains(objectId)) return;

            var nb = _registry.Get(objectId);
            if (nb == null)
            {
                // Despawn arrived before Spawn (UDP reorder).  Record the
                // intent under a TTL so the eventual Spawn for this id can
                // see "already despawned" and skip creating a ghost object.
                long now = NowMillis();
                _pendingDespawns.Prune(now);
                if (_pendingDespawns.Record(objectId, now))
                {
                    // Redacted: only the cap is logged, never the offending id.
                    if (WarnGate.ShouldEmit(ref _lastPendingDespawnCapWarnTicks))
                        RtmpeLog.Warning(
                            $"[SpawnManager] Pending-despawn cap reached ({MaxPendingDespawns}); evicting oldest entry.");
                }

                // Still try to clear any stale prefab mapping for this id so the
                // dictionary doesn't accumulate orphans when objects are
                // externally destroyed.
                _prefabOfObject.Remove(objectId);
                return;
            }

            // Despawn arrived AFTER Spawn — normal path.  Consume any pending
            // entry so the order list cannot retain a ghost.
            _pendingDespawns.Consume(objectId);

            // Mark teardown in flight so any re-entrant DestroyLocal for the
            // same id (callback path / server-echoed despawn) short-circuits
            // at the top of the method.  Cleared in the finally below once
            // the destroy / pool-release has completed.
            _despawnInFlight.Add(objectId);

            try
            {
                // Tear down EVERY NetworkBehaviour on the object, not just the
                // registered anchor, so each fires OnNetworkDespawn.  All are
                // flagged evicted FIRST so their later OnDestroy (which Unity
                // dispatches during the Destroy() call below) observes the
                // teardown is already in progress and skips OnExternallyDestroyed —
                // without which the counter double-decrements on any pool-less
                // path that completes synchronously inside the same frame.
                SpawnLifecycleOps.DespawnAll(nb.ObjectComponents, LogDespawnFault);
                _registry.Unregister(objectId);

                // Live-count decrement mirrors the increment in CreateLocal.
                // Clamp at zero so an external destroy that bypasses CreateLocal
                // can never drive the counter negative and silently extend the cap.
                if (_currentSpawnCount > 0) _currentSpawnCount--;

                // ⚠️ The lookup's ANSWER is load-bearing, and discarding it made
                // a miss indistinguishable from a hit on prefab 0.  `out uint`
                // leaves 0 behind on a miss, and 0 is a perfectly ordinary prefab
                // id — so an instance that was never tagged (created outside
                // CreateLocal, or whose entry was already reaped) went back to
                // the pool in prefab 0's bucket and was handed out as a prefab 0
                // on the next spawn.  `INetworkObjectPool.Release` documents
                // uint.MaxValue for exactly this case and tells the pool to
                // destroy rather than reuse; the ClearAll path already sends it.
                uint prefabId = _prefabOfObject.TryGetValue(objectId, out uint mapped)
                    ? mapped
                    : uint.MaxValue;
                _prefabOfObject.Remove(objectId);

                // Unity null check: the GO may have been destroyed externally.
                if (nb == null) return;

                // When a pool is installed, return the instance for reuse instead
                // of destroying it.  The pool is responsible for deactivating the
                // GameObject — but we do a final IsOwner-safe NetworkBehaviour
                // reset so the pooled instance is in a known state next spawn.
                if (_pool != null)
                {
                    try { _pool.Release(prefabId, nb.gameObject); }
                    catch (Exception ex)
                    {
                        if (WarnGate.ShouldEmit(ref _lastPoolReleaseThrowWarnTicks))
                            Debug.LogException(ex);
                        // If the pool throws, fall back to destroy to avoid a leak.
                        UnityEngine.Object.Destroy(nb.gameObject);
                    }
                }
                else
                {
                    UnityEngine.Object.Destroy(nb.gameObject);
                }
            }
            finally
            {
                _despawnInFlight.Remove(objectId);
            }
        }

        // One component's OnNetworkDespawn must not cost its siblings theirs: a
        // component left spawned is skipped by the next InitializeAll and never
        // spawns again.
        private static readonly Action<INbLifecycle, Exception> LogDespawnFault =
            (component, ex) =>
            {
                if (!WarnGate.ShouldEmit(ref _lastDespawnFaultWarnTicks)) return;
                Debug.LogError(
                    $"[RTMPE] NetworkBehaviour.OnNetworkDespawn threw on " +
                    $"{component.GetType().Name}: {ex.GetType().Name}: {ex.Message}",
                    component as UnityEngine.Object);
            };

        // A despawn faults per component, and a room emptying despawns every
        // object at once — so the rate here is the wire's, not the application's.
        // Static because the delegate is: one budget for the condition, which is
        // what a console has anyway.
        private static long _lastDespawnFaultWarnTicks;

        /// <summary>
        /// Reconcile counters and registry when a NetworkObject is destroyed
        /// via the Unity API (Object.Destroy on the GameObject) rather than
        /// the SpawnManager's <see cref="DestroyLocal"/> entry point.
        /// Idempotent so a normal <c>DestroyLocal</c> followed by Unity's
        /// own OnDestroy of the same instance does not double-decrement —
        /// the registry-presence check is the canonical "is this still
        /// owned by SpawnManager?" question.
        /// </summary>
        /// <remarks>
        /// Without this hook, code that calls
        /// <c>UnityEngine.Object.Destroy(networkBehaviour.gameObject)</c>
        /// directly leaks a slot in <c>_currentSpawnCount</c>, an entry in
        /// <c>_prefabOfObject</c>, and a registry binding.  Saturation of
        /// the live-count cap follows after enough such bypasses.
        /// </remarks>
        internal void OnExternallyDestroyed(ulong objectId, NetworkBehaviour destroyed)
        {
            if (objectId == 0) return;

            var registered = _registry.Get(objectId);

            // ⛔ An id is not an identity.  A same-id collision leaves the
            // evicted instance alive under an id the registry has already given
            // to its replacement, and that instance's own teardown arrives here:
            // reconciled by id alone it unregisters the LIVE object and returns
            // a slot the replacement is still using, which is the whole of the
            // finding this seam reports.  The window is real rather than
            // theoretical — the eviction runs the evicted object's despawn
            // callbacks, and application code on them may destroy it there.
            //
            // Compared by GameObject, not by component: every component's
            // OnDestroy reaches this, and only one of them is the registry's
            // anchor.  ReferenceEquals rather than `==`, because the question is
            // which managed object this is and not whether Unity has already
            // torn it down — by the time the last component reports, it has.
            if (destroyed != null
                && registered != null
                && !ReferenceEquals(registered.gameObject, destroyed.gameObject))
                return;

            // Registry presence is the authority: a prior DestroyLocal has
            // already called _registry.Unregister(objectId), so this branch
            // returns immediately and the counters are not touched twice.
            if (registered == null && !_prefabOfObject.ContainsKey(objectId))
                return;

            _registry.Unregister(objectId);

            if (_currentSpawnCount > 0) _currentSpawnCount--;

            _prefabOfObject.Remove(objectId);
        }

        // ── Late-Join Resync ───────────────────────────────────────────────────

        /// <summary>
        /// Mark every NetworkVariable on every locally-owned object as dirty
        /// so the next 30 Hz flush retransmits its current value.  Called by
        /// <see cref="NetworkManager"/> when another player joins the current
        /// room, giving the new joiner a full state snapshot within ~33 ms
        /// instead of waiting for a future value change.
        /// <para>
        /// No-op when the registry is empty or no objects are locally owned.
        /// Runs on the Unity main thread.
        /// </para>
        /// <para>
        /// Current scope: only <c>NetworkVariable</c> values participate.
        /// <c>NetworkTransform</c> already forces its next broadcast on each
        /// interpolation frame, so pose sync is naturally covered.  The
        /// trade-off of marking ALL variables (vs only unchanged ones) is a
        /// single redundant flush cycle per join — negligible at typical
        /// object counts, and far cheaper than a new protocol round trip.
        /// </para>
        /// </summary>
        public void MarkAllVariablesDirtyForResync()
        {
            // MarkAllVariablesDirty walks user subscribers; one of them may
            // legally re-enter the registry (e.g. by spawning or despawning in
            // response to the resync).  Using a private snapshot list keeps
            // this walk independent of the shared GetAll buffer so the inner
            // re-entry cannot perturb iteration here.
            _registry.GetAllSnapshot(_resyncScratch);
            for (int i = 0; i < _resyncScratch.Count; i++)
            {
                var nb = _resyncScratch[i];
                if (nb == null) continue;
                // Object-wide: a variable on any component replicates when it
                // changes, so a joiner that arrives after the last change has
                // no way to learn it except from a resync that reaches the
                // component holding it.
                try { ObjectDispatchOps.MarkAllVariablesDirtyAll(nb.ObjectComponents); }
                catch (Exception ex)
                {
                    // Isolate per-object failure: one misbehaving NetworkBehaviour
                    // must not block resync for the rest of the owned roster.
                    if (WarnGate.ShouldEmit(ref _lastResyncFaultWarnTicks))
                        Debug.LogException(ex);
                }
            }
            _resyncScratch.Clear();
        }

        // Pre-allocated snapshot buffer for hot resync walks.  Owned by this
        // SpawnManager so it cannot collide with the registry's shared GetAll
        // buffer when a callback fires further iteration.
        private readonly List<NetworkBehaviour> _resyncScratch =
            new List<NetworkBehaviour>(64);

        // Same rationale for the ClearAll teardown — Registry.Clear dispatches
        // user OnNetworkDespawn callbacks, any of which may legally read the
        // registry, and we cannot leave the shared GetAll buffer parked across
        // that re-entry window.
        private readonly List<NetworkBehaviour> _clearAllScratch =
            new List<NetworkBehaviour>(64);

        // ── Owner Leave Handling ───────────────────────────────────────────────

        /// <summary>
        /// Handle a player leaving the room.
        /// Destroys all objects owned by <paramref name="playerId"/> that
        /// have <see cref="NetworkBehaviour.DestroyWithOwner"/> set to
        /// <see langword="true"/>.
        ///
        /// <para>Objects with <c>DestroyWithOwner=false</c> are left intact
        /// HERE — they are reassigned to the room host by the caller
        /// (<c>NetworkManager.OnRoomManagerPlayerLeft</c> →
        /// <see cref="OwnershipManager.ReassignObjectsToNewOwner"/>,
        /// NEW-OWNERSHIP-1).  Reassignment is orchestrated there rather than in
        /// this method because the host (CurrentRoom.MasterId) and roster live
        /// on the RoomManager, not on the SpawnManager.</para>
        /// </summary>
        /// <param name="playerId">Room player UUID of the player who left.</param>
        public void OnPlayerLeftRoom(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;

            // Tombstone the departure so a Spawn that races behind this
            // player_left (separate relay path, no mutual ordering) is dropped by
            // CreateLocal instead of forming a ghost owned by an absent player.
            _departedPlayers.Record(playerId, NowMillis());

            var owned = _ownership.GetObjectsOwnedBy(playerId);
            foreach (var obj in owned)
            {
                if (obj == null) continue;

                if (obj.DestroyWithOwner)
                {
                    // Exception isolation: continue processing remaining objects.
                    try { DestroyLocal(obj.NetworkObjectId); }
                    catch (Exception ex)
                    {
                        if (WarnGate.ShouldEmit(ref _lastOwnedObjectTeardownThrowWarnTicks))
                            Debug.LogException(ex);
                    }
                }
                // DestroyWithOwner=false objects are reassigned to the room host
                // by the caller via OwnershipManager.ReassignObjectsToNewOwner.
            }
        }

        /// <summary>
        /// A player has (re)joined the room.  A player present in the room owns
        /// legitimate spawns, so any tombstone recorded by an earlier departure
        /// under the same room player id no longer applies and is lifted here,
        /// symmetrically to <see cref="OnPlayerLeftRoom"/>.  The server reuses the
        /// derived player id across a session-preserving rejoin, so a tombstone
        /// key can recur while its owner is in fact back on the roster.
        /// </summary>
        /// <param name="playerId">Room player UUID of the player who joined.</param>
        public void OnPlayerJoinedRoom(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            _departedPlayers.Remove(playerId);
        }

        // ── Cleanup ────────────────────────────────────────────────────────────

        /// <summary>
        /// Destroy all spawned objects and reset state.
        /// Called on room leave and disconnect.
        /// Fires <see cref="NetworkBehaviour.OnNetworkDespawn"/> for each live object,
        /// then destroys all <c>GameObject</c>s.
        /// </summary>
        /// <param name="resetObjectIdSpace">
        /// When <see langword="true"/> (a session end / disconnect) the per-session
        /// object-id counter and its exhaustion latch are reset to zero.  A room
        /// leave that keeps the transport session passes <see langword="false"/> so
        /// ids stay monotonic across the rejoin and cannot re-collide with the
        /// room's despawn tombstones.
        /// <para>
        /// State this explicitly at every call site.  The default rewinds the id
        /// space, which is correct only when the transport session ends with it;
        /// taking that default on a session-preserving path re-issues ids the room
        /// still holds under a despawn tombstone, so peers and late joiners never
        /// see the object.
        /// </para>
        /// </param>
        public void ClearAll(bool resetObjectIdSpace = true)
        {
            // Two-pass teardown is mandatory because user code in
            // OnNetworkDespawn (fired from pass 1) may legitimately call
            // Object.Destroy on its own GameObject or release it back to a
            // custom pool.  Pass 2 must therefore re-check the Unity-engine
            // null state of every captured reference before invoking Release
            // or Destroy — Unity's overloaded == operator returns true for a
            // C# reference whose underlying engine object has been destroyed,
            // and a custom pool's Release(prefabId, gameObject) on such a
            // reference may NRE when it tries to call SetActive.
            //
            // The snapshot is a list of (NetworkBehaviour, prefabId) pairs
            // captured BEFORE the despawn callbacks fire, so a callback that
            // unregisters or otherwise mutates the live registry cannot
            // perturb the iteration order or skip an entry.

            // Capture the live (NB, prefabId) tuples BEFORE Registry.Clear
            // fires the despawn callbacks; the snapshot is the authoritative
            // iteration order for pass 2 even if user code mutates the
            // registry from inside OnNetworkDespawn.  Using GetAllSnapshot
            // populates a private list so the registry's shared GetAll buffer
            // is not parked across the Clear call (which itself dispatches
            // user code that may invoke GetAll re-entrantly).
            _registry.GetAllSnapshot(_clearAllScratch);
            var snapshot = new List<(NetworkBehaviour Nb, uint PrefabId)>(_clearAllScratch.Count);
            for (int i = 0; i < _clearAllScratch.Count; i++)
            {
                var nb = _clearAllScratch[i];
                if (nb == null) continue;
                // Pre-mark every captured object's components so their imminent
                // OnDestroy calls (driven by either the pool's Release or the
                // direct UnityEngine.Object.Destroy in pass 2 below) skip the
                // SpawnManager reconciliation path — ClearAll resets the
                // counters wholesale, and a per-object decrement on top of
                // that would underflow.  Object-wide because every component
                // carries its own OnDestroy and its own latch, so a mark left
                // on the anchor speaks for the anchor alone.
                SpawnLifecycleOps.MarkEvictedAll(nb.ObjectComponents);
                uint prefabId = _prefabOfObject.TryGetValue(nb.NetworkObjectId, out var pid)
                    ? pid : uint.MaxValue;
                snapshot.Add((nb, prefabId));
            }
            _clearAllScratch.Clear();

            // Pass 1: fire despawn callbacks via the registry sweep.
            // Registry.Clear captures its own snapshot under its lock and
            // invokes SetSpawned(false) outside the lock, so user code in
            // OnNetworkDespawn that re-enters the registry does not deadlock.
            _registry.Clear();

            // Pass 2: destroy or release surviving GameObjects.  Each access
            // is guarded by Unity's destroyed-object null check on both the
            // NetworkBehaviour itself (the user may have called Destroy on
            // the parent GameObject) and the GameObject reference (defence-
            // in-depth — typically redundant when the NB null-check fires).
            for (int i = 0; i < snapshot.Count; i++)
            {
                var entry = snapshot[i];
                var nb = entry.Nb;

                // The Unity == operator returns true for any reference whose
                // underlying engine object has been destroyed; this filters
                // out objects already torn down by user OnNetworkDespawn code.
                if (nb == null) continue;

                GameObject go;
                try { go = nb.gameObject; }
                catch (MissingReferenceException) { continue; }
                if (go == null) continue;

                try
                {
                    if (_pool != null)
                    {
                        // prefabId == uint.MaxValue means we never recorded a
                        // mapping (e.g. an object created outside CreateLocal);
                        // the pool must either route by its own bookkeeping or
                        // fall through to Destroy in its Release implementation.
                        _pool.Release(entry.PrefabId, go);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(go);
                    }
                }
                catch (Exception ex)
                {
                    if (WarnGate.ShouldEmit(ref _lastTeardownFaultWarnTicks))
                        Debug.LogException(ex);
                }
            }

            _prefabOfObject.Clear();
            _pendingDespawns.Clear();
            _departedPlayers.Clear();
            _despawnInFlight.Clear();

            // Reset spawn admission counters: a fresh room must start with a
            // clean rate bucket and a zero live count.
            _spawnsThisSecond           = 0;
            _spawnRateBucketStartTicks  = 0;
            _currentSpawnCount          = 0;
            _rateLimitWarnedThisBucket  = false;
            _countLimitWarnedThisBucket = false;

            // The object-id space is scoped to the transport session, not the
            // room: the low 32 bits pair with a session-stable high half via
            // ObjectIdMath.Compose, so a room leave that keeps the session must
            // carry the counter forward — restarting it on a rejoin re-issues ids
            // the room still holds under a 60 s despawn tombstone, so peers and
            // late joiners never receive the rejoined object.  Only a session end
            // resets the space and its exhaustion latch; Interlocked.Exchange keeps
            // the reset atomic against a concurrent Increment.
            if (resetObjectIdSpace)
            {
                _localIdSpaceExhausted = false;
                Interlocked.Exchange(ref _nextLocalId, 0);
            }

            // Every object this method just tore down is gone, so anything
            // latched on their ids is holding keys no live object carries.
            // ⛔ Outside the branch above, and deliberately: a room leave keeps
            // the session and its id counter, so the entries would otherwise
            // survive every room a player visits and spend a budget meant for
            // one room's worth of misconfiguration on all of them. Re-entering
            // the same room re-warns nothing, because the objects come back
            // under ids the carried-forward counter has not issued before.
            // The interpolator advisory is the only such latch today.
            RTMPE.Core.Diagnostics.RemoteInterpolatorAdvisory.ResetLatch();
        }

        // ── Despawn-before-Spawn (out-of-order) ────────────────────────────────

        // Test seams: every state transition keeps the three coupled
        // structures inside PendingDespawnTracker in lockstep.  Surfacing
        // each axis lets the adversarial coverage prove that normal
        // consumption never leaves ghost entries in the order list.
        /// <summary>
        /// Driver hook for the NetworkManager.Update loop: prune expired
        /// pending-despawn entries on a regular cadence so a stream of UDP-
        /// reordered Despawns whose matching Spawns never arrive cannot
        /// linger past their TTL.  Without periodic invocation the tracker
        /// is only swept inside CreateLocal / DestroyLocal — which are not
        /// guaranteed to fire after the last out-of-order Despawn of a
        /// session, leaving the tracker holding entries until next room.
        /// </summary>
        internal void PruneIfStale(long nowMs)
        {
            _pendingDespawns.Prune(nowMs);
        }

        // Exposed for the NetworkManager driver so the periodic prune can
        // share the SpawnManager's monotonic clock without duplicating it.
        internal long PendingDespawnNowMillis() => NowMillis();

#if UNITY_INCLUDE_TESTS
        // Test seam: lets the SpawnManagerTests force the counter to a
        // value just below the u32 ceiling so the wrap-detection branch
        // in GenerateObjectId can be exercised without 4 billion spawns.
        // Compiled only when UNITY_INCLUDE_TESTS is defined so the
        // shipped Player assembly carries no entry point capable of
        // colliding the global object-id counter.
        internal void DangerousSetNextLocalIdForTest(long value)
        {
            Interlocked.Exchange(ref _nextLocalId, value);
        }
#endif // UNITY_INCLUDE_TESTS

        internal bool LocalIdSpaceExhausted => _localIdSpaceExhausted;

        internal int PendingDespawnCount      => _pendingDespawns.Count;
        internal int PendingDespawnOrderCount => _pendingDespawns.OrderCount;
        internal int PendingDespawnNodeCount  => _pendingDespawns.NodeCount;

        private static long NowMillis()
        {
            // Stopwatch-based monotonic clock — survives wall-time adjustments.
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            return ticks * 1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        // ── Spawn Admission ────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when the spawn may proceed; false if either the
        /// per-second rate cap or the per-room concurrent-object cap is
        /// already saturated.  Bucket roll uses Stopwatch ticks so wall-clock
        /// adjustments cannot stick the rate limiter shut or open.
        /// </summary>
        private bool CheckSpawnAdmission(bool exemptFromRateCap = false)
        {
            int rateCap  = ResolveSpawnRateCap();
            int countCap = ResolveSpawnCountCap();

            RollSpawnBucketIfElapsed();

            if (!exemptFromRateCap && _spawnsThisSecond >= rateCap)
            {
                if (!_rateLimitWarnedThisBucket)
                {
                    _rateLimitWarnedThisBucket = true;
                    // Redacted: only the cap is logged.  An attacker reading
                    // the log cannot infer which prefab / owner was clipped.
                    RtmpeLog.Warning(
                        $"[SpawnManager] Spawn rate cap reached ({rateCap}/s); excess spawns dropped this bucket.");
                }
                return false;
            }

            if (_currentSpawnCount >= countCap)
            {
                if (!_countLimitWarnedThisBucket)
                {
                    _countLimitWarnedThisBucket = true;
                    RtmpeLog.Warning(
                        $"[SpawnManager] Spawn count cap reached ({countCap}); spawn dropped.");
                }
                return false;
            }

            return true;
        }

        private int ResolveSpawnRateCap()
        {
            var settings = _networkManager?.Settings;
            return settings != null && settings.maxSpawnsPerSecond > 0
                ? settings.maxSpawnsPerSecond
                : 100;
        }

        private int ResolveSpawnCountCap()
        {
            var settings = _networkManager?.Settings;
            return settings != null && settings.maxSpawnsPerRoom > 0
                ? settings.maxSpawnsPerRoom
                : 5_000;
        }

        // Roll the bucket if the wall-time second has elapsed.  Using the
        // monotonic Stopwatch frequency avoids any reliance on system
        // wall-clock stability — an NTP step cannot freeze or open the gate.
        private void RollSpawnBucketIfElapsed()
        {
            long nowTicks       = System.Diagnostics.Stopwatch.GetTimestamp();
            long ticksPerSecond = System.Diagnostics.Stopwatch.Frequency;
            if (_spawnRateBucketStartTicks != 0
                && nowTicks - _spawnRateBucketStartTicks < ticksPerSecond) return;

            _spawnRateBucketStartTicks  = nowTicks;
            _spawnsThisSecond           = 0;
            _rateLimitWarnedThisBucket  = false;
            _countLimitWarnedThisBucket = false;
        }

        // ── Private ────────────────────────────────────────────────────────────

        /// <summary>
        /// Generate a locally-unique 64-bit object ID.
        /// High 32 bits: avalanche-mixed digest of the FULL u64 gateway session
        /// id (xor-fold of high and low halves followed by a SplitMix64-style
        /// finalizer).  Mixing every input byte means two sessions whose low
        /// halves coincide map to different high-half digests with 1-in-2^32
        /// probability — closing the reconnect-collision class that the prior
        /// "low 32 bits of session id" scheme accepted by construction.
        /// Low 32 bits: per-session monotonic counter.  At 1000 spawns/s a 32-bit
        /// counter wraps in ≈49 days, comfortably outside any plausible session
        /// lifetime; <see cref="ClearAll"/> resets it to 0 on disconnect so a
        /// fresh session always starts from a clean slate.
        /// </summary>
        /// <remarks>
        /// The wire (SpawnPacketBuilder: object_id u64 LE) carries the full 64
        /// bits — no truncation happens at the framing layer.  The high/low
        /// split is purely a client-side allocation policy and can be replaced
        /// by a server-issued id space without touching the wire format.
        /// </remarks>
        private ulong GenerateObjectId()
        {
            // Refuse new allocations once the u32 ceiling has been hit.
            // Crossing it silently would wrap the low half of the object id
            // and re-issue an id whose previous owner is potentially still
            // alive on the wire — registry-uniqueness invariants would break
            // and the second SetSpawned would clobber the first object's
            // bookkeeping.  Surfacing this condition is the only correct
            // response; ClearAll on disconnect resets the latch.
            if (_localIdSpaceExhausted) return 0UL;

            // Interlocked.Increment returns the post-increment value, so first
            // call yields 1.  Allocations beyond uint.MaxValue would wrap the
            // low 32 bits used as the counter component of the object id; we
            // detect this by checking whether the post-increment value masked
            // to u32 is zero (a fresh wrap) OR whether the underlying long has
            // exceeded uint.MaxValue.
            long raw = Interlocked.Increment(ref _nextLocalId);
            if (raw <= 0 || raw > uint.MaxValue)
            {
                _localIdSpaceExhausted = true;
                Debug.LogError(
                    "[SpawnManager] Local object-id counter has reached the 32-bit ceiling " +
                    $"(uint.MaxValue = {uint.MaxValue}).  Further Spawn calls will be rejected " +
                    "until the session is reset (NetworkManager disconnect / ClearAll).  " +
                    "This indicates either an extreme spawn-leak or a flooding attacker — " +
                    "investigate before reconnecting.");
                return 0UL;
            }

            ulong counter = (ulong)raw;
            return ObjectIdMath.Compose(_networkManager.LocalPlayerId, counter);
        }

        /// <summary>
        /// Build and send a Spawn packet through the NetworkManager.
        /// Silently skips if not connected (local-only spawn still succeeds).
        /// </summary>
        private void SendSpawnPacket(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation,
            bool sharedAuthority,
            bool destroyWithOwner)
        {
            if (!_networkManager.IsConnected) return;

            // The declaration is emitted on every spawn, including the
            // restrictive one.  The gateway resolves an absent byte to
            // owner-only anyway, but emitting it keeps the wire shape constant
            // and makes the two agree because the same value was sent — not
            // because both sides happened to pick the same default.
            var payload = SpawnPacketBuilder.BuildSpawnRequest(
                prefabId, objectId, ownerPlayerId, position, rotation,
                sharedAuthority
                    ? SpawnAuthorityFlags.Shared
                    : SpawnAuthorityFlags.OwnerOnly,
                destroyWithOwner);

            // The builder refused the position as one the receiving parser
            // drops, and has already said so.  The object stays spawned
            // locally — the same outcome as spawning while disconnected, which
            // the guard above treats the same way — rather than being torn down
            // from under a caller that is still inside its own Spawn call.
            if (payload == null) return;

            _networkManager.Send(
                _networkManager.BuildPacket(PacketType.Spawn, PacketFlags.Reliable, payload),
                reliable: true);
        }

        /// <summary>
        /// Build and send a Despawn packet through the NetworkManager.
        /// Silently skips if not connected (local-only despawn still succeeds).
        /// </summary>
        private void SendDespawnPacket(ulong objectId)
        {
            if (!_networkManager.IsConnected) return;

            var payload = SpawnPacketBuilder.BuildDespawnRequest(objectId);
            _networkManager.Send(
                _networkManager.BuildPacket(PacketType.Despawn, PacketFlags.Reliable, payload),
                reliable: true);
        }
    }
}
