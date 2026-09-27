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
//     on NetworkBehaviour. An object with DestroyWithOwner=false keeps its
//     owner here — the caller hands it to the room's host — and is booked
//     to forget the departed sender's clocks a moment later if it is still
//     the departed player's by then (ForgetDueDepartedClocks, from Tick).
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
    /// Creates and destroys networked objects from registered prefabs, and relays
    /// each spawn and despawn to the room. Access it through
    /// <see cref="NetworkManager.Spawner"/>.
    /// </summary>
    /// <remarks>
    /// A new spawn manager is built on every <c>Connect()</c> and every reconnect
    /// attempt. Prefab registrations and <see cref="OnSpawnRejected"/> subscribers
    /// carry over; an object pool does not, so install your pool from
    /// <c>NetworkManager.OnConnected</c>. Call every member from the Unity main
    /// thread.
    /// </remarks>
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
        // The room's catch-up is metered by the count cap alone.  The live
        // objects the Room Service replays to a joiner arrive as one burst,
        // once, and cannot be asked for again — ahead of the join reply they
        // are staged and released on the exemption CreateLocalFromStagedCatchUp
        // names, and behind it (the order the room delivers) they arrive live
        // inside the window _catchUp keeps open after entry.  Neither counts
        // against the bucket, or the spawns that follow the burst — a peer's
        // projectile, this client's own avatar — would be refused for the rest
        // of that second, and an inbound spawn refused is never re-sent.  The
        // spawns held for a departed owner's return (_spawnsHeldForReturn) take
        // the same exemption when that owner arrives: live spawns delayed, not
        // multiplied, released together, and bounded where they are held.
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
        private readonly RoomCatchUpWindow _catchUp = new RoomCatchUpWindow();

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

        // The objects the room's despawn relay ended here a moment ago — and
        // any this client ended itself with no owner on record — kept for the
        // same TTL, so a spawn of one of them that arrives afterwards cannot
        // bring it back.  A relayed despawn is its owner's, accepted: the object
        // ended for every owner, and the record names none.
        // The record above answers a despawn that overtook its spawn, and the
        // spawn it answers spends it; this one answers a spawn sent again after
        // the object was gone — a promotion catch-up or a join replay that read
        // the room's buffer before the room had forgotten the object, or a
        // relay the gateway re-sent — and it answers every such spawn until it
        // expires, because the replay ladder sends each object more than once.
        private readonly PendingDespawnTracker _despawnedHere =
            new PendingDespawnTracker();

        // The objects this client ended itself, under the owners it knew each
        // by: torn down with an owner whose departure was reported, under that
        // owner; despawned by its own Despawn or the world's relayed teardown,
        // under the owner at the end, the one before it and the one it was
        // spawned under.  A spawn of one still naming such an owner is the
        // room's copy of what it had not yet forgotten, and is refused while the
        // record stands; a spawn naming an owner this client never saw it under
        // is not — a transfer that raced the end on this client, after which the
        // gateway relays no despawn from an owner it no longer knows, and the
        // object lives on under the new one.  Apart from _despawnedHere because
        // that record refuses whatever owner a spawn names.
        private readonly EndedUnderOwner _endedUnderOwner =
            new EndedUnderOwner();

        // The objects a departed player left behind, booked to forget that
        // sender's clocks once its frames still in flight have landed — and
        // only if the object is still the departed player's then.  A handover
        // forgets them itself, at once, and one of those late frames can pass
        // its fresh gate — as it always could; resetting at the departure as
        // well would have opened that window for every object no handover
        // moves.  Only the owner is compared: an object handed away and back
        // inside the delay, or whose player left again within it, is reset
        // when the first booking falls due.
        private readonly List<DepartedClocks> _departedClocks = new List<DepartedClocks>();

        /// <summary>
        /// How long after a player's departure the objects it left behind
        /// forget its clocks: past the departed sender's frames still in flight,
        /// short enough that a new host this client never heard of is not
        /// refused for long.
        /// </summary>
        internal const long DepartedClocksDelayMillis = 2000L;

        private readonly struct DepartedClocks
        {
            public readonly ulong ObjectId;
            public readonly string Owner;
            public readonly long DueAtMillis;

            public DepartedClocks(ulong objectId, string owner, long dueAtMillis)
            {
                ObjectId = objectId;
                Owner = owner;
                DueAtMillis = dueAtMillis;
            }
        }

        // Whether the spawn in progress — the innermost CreateLocal on the
        // stack — is this client's own Spawn rather than one the wire
        // delivered.  Read by a component's OnNetworkSpawn, which runs inside
        // it (IsCreatingOwnSpawn), and restored as each CreateLocal returns, so
        // a Spawn made from inside a wire spawn's callback answers for itself
        // and leaves the outer answer as it was.
        private bool _creatingOwnSpawn;

        /// <summary>
        /// Whether the spawn now running its components' callbacks is one this
        /// client made with <see cref="Spawn"/>, as opposed to one the wire
        /// delivered — a relay, a replay, or the promotion catch-up handing
        /// this client an object it did not hold.  Only meaningful inside a
        /// spawn: false outside one.
        /// </summary>
        /// <remarks>
        /// The distinction the object's id cannot make.  An id minted in this
        /// session's space is one this client spawned at some point, but a
        /// same-session leave and rejoin keeps the space, so an object this
        /// client spawned in an earlier stint of the room — handed to the host
        /// when it left, and handed back by a promotion — reaches it from the
        /// wire under an id of its own.
        /// </remarks>
        internal bool IsCreatingOwnSpawn => _creatingOwnSpawn;

        // Symmetric bookkeeping for the leave-before-Spawn race: a Spawn (0x30)
        // and its owner's player_left ride different relay paths with no mutual
        // ordering, so a Spawn can land after its owner has left.  This tombstones
        // a departed player so CreateLocal drops that late spawn rather than
        // forming an object no live session can update or despawn.
        private readonly DepartedPlayerTracker _departedPlayers =
            new DepartedPlayerTracker();

        // The spawns that arrived inside a departure tombstone, kept for their
        // owner's return rather than dropped: a player id outlives a departure,
        // and a returning player's first spawn can land before its arrival does
        // (see DepartedOwnerSpawns).  Cleared with the tombstones.
        private readonly DepartedOwnerSpawns<byte[]> _spawnsHeldForReturn =
            new DepartedOwnerSpawns<byte[]>();

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
        private long _lastDepartedClocksBookThrowWarnTicks;
        private long _lastDepartedClocksThrowWarnTicks;

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

        // Replicated values that reached a copy held here from another player's
        // copy — see RemoteValuesDelivered.
        private long _remoteValuesDelivered;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>The live networked objects on this client, by object id.</summary>
        public NetworkObjectRegistry Registry => _registry;

        /// <summary>Moves objects between players; the server decides every transfer.</summary>
        public OwnershipManager Ownership => _ownership;

        /// <summary>
        /// How many replicated values written by ANOTHER player's copy of an
        /// object have reached this client's copy since this manager was built
        /// — the runtime witness that state crosses between players, which no
        /// public event states in aggregate.
        /// </summary>
        /// <remarks>
        /// Counted by the inbound VariableUpdate path, per frame, as the number
        /// of values delivered to the addressed object
        /// (<c>NetworkBehaviour.InboundValuesDelivered</c>) while that object is
        /// owned by somebody else; a value the tick watermark dropped is not one
        /// that arrived, and what the variable did with a delivered value is the
        /// variable's.  Per session, like every count on this manager — a
        /// connect builds a new one — so a reader asking "did any arrive"
        /// latches rather than differencing.
        /// <para>
        /// Internal: read by the Editor's play-mode observer for the <c>sync</c>
        /// runtime check and by nothing a game ships.  It answers the POSITIVE
        /// half of that check only; that a write to somebody else's object did
        /// not cross is a fact about the peer, and no counter here can hold it.
        /// ⚠️ "Another player" is another owner id: a copy owned by an identity
        /// this client held in an earlier session reads as somebody else's here.
        /// This count cannot tell the two apart and does not try; it stays
        /// honest because nothing writes to such a copy — the earlier session is
        /// gone and the room's replay carries spawns, not values — which is a
        /// fact about the fleet, not about this reading.
        /// </para>
        /// </remarks>
        internal long RemoteValuesDelivered => _remoteValuesDelivered;

        /// <summary>
        /// Add to <see cref="RemoteValuesDelivered"/> the values one inbound
        /// frame delivered to an object another player owns.  A non-positive
        /// count is a frame that delivered nothing and is ignored.
        /// </summary>
        internal void NoteRemoteValuesDelivered(long count)
        {
            if (count > 0) _remoteValuesDelivered += count;
        }

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a spawn manager. Not intended to be called from game code: use
        /// <see cref="NetworkManager.Spawner"/>.
        /// </summary>
        /// <param name="registry">The registry of live networked objects.</param>
        /// <param name="ownership">The ownership manager this spawn manager exposes.</param>
        /// <param name="networkManager">The manager that allocates ids and sends the spawns and despawns.</param>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
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
        /// Maps a prefab id to a prefab, replacing an existing mapping with a
        /// warning.
        /// </summary>
        /// <remarks>
        /// Use it for prefabs the registry assigned to
        /// <see cref="NetworkSettings.prefabRegistry"/> does not list. Registrations
        /// made by hand are applied after the registry, so they win, and they carry
        /// over to the spawn manager built on the next connect or reconnect. The
        /// prefab needs a <see cref="NetworkBehaviour"/> to be spawned.
        /// </remarks>
        /// <param name="prefabId">The id every client uses for this prefab.</param>
        /// <param name="prefab">The prefab to register.</param>
        /// <exception cref="ArgumentNullException"><paramref name="prefab"/> is <see langword="null"/>.</exception>
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
        /// Removes the mapping for a prefab id.
        /// </summary>
        /// <remarks>
        /// A prefab the prefab registry lists is registered again when the next
        /// connect or reconnect loads the registry.
        /// </remarks>
        /// <param name="prefabId">The prefab id to remove.</param>
        /// <returns><see langword="true"/> when a mapping was removed.</returns>
        public bool UnregisterPrefab(uint prefabId) => _prefabs.Remove(prefabId);

        /// <summary>Whether a prefab is registered under <paramref name="prefabId"/>.</summary>
        /// <param name="prefabId">The prefab id to look up.</param>
        public bool HasPrefab(uint prefabId) => _prefabs.ContainsKey(prefabId);

        /// <summary>
        /// The prefab id this session spawns <paramref name="prefab"/> under.
        /// </summary>
        /// <remarks>
        /// Use it instead of reading <see cref="NetworkPrefabRegistry.Entries"/>: a
        /// registration made by hand can give an id the registry lists to another
        /// prefab. When several ids map to the prefab, the lowest is returned.
        /// </remarks>
        /// <param name="prefab">The prefab to find an id for.</param>
        /// <param name="prefabId">The id, when one is registered; otherwise 0.</param>
        /// <returns><see langword="true"/> when an id is registered for the prefab.</returns>
        public bool TryGetPrefabId(GameObject prefab, out uint prefabId)
            => PrefabTableOps.TryFindId(_prefabs, prefab, out prefabId);

        /// <summary>
        /// The prefab id a live object was spawned from — the id the spawn
        /// packet carried, whichever side of the wire it came from.
        /// </summary>
        /// <remarks>
        /// Read by the world-authority migration, which spawns a new copy of
        /// an object it inherited: the inherited object's own prefab is the
        /// one answer that holds on every client, since every client built it
        /// from that id.  Answered from the same table the teardown reconciles
        /// by, so an object that is gone answers false.
        /// </remarks>
        /// <param name="objectId">The network object id.</param>
        /// <param name="prefabId">The prefab id it was created from, when the object is live.</param>
        internal bool TryGetPrefabIdOfObject(ulong objectId, out uint prefabId)
            => _prefabOfObject.TryGetValue(objectId, out prefabId);

        /// <summary>
        /// Tell the room that an object this client owns is gone, for an object
        /// something other than the SDK is destroying — a scene load, a pool
        /// that parented it, the game's own <c>Object.Destroy</c> — so the peers
        /// and the room's replay drop it with this client, instead of keeping a
        /// copy nobody will ever write again.
        /// </summary>
        /// <remarks>
        /// The wire half of <see cref="Despawn"/>, and its record of the end
        /// (<see cref="RememberEndedHere"/>): the local teardown is already under
        /// way in the caller's <c>OnDestroy</c>, and
        /// <see cref="OnExternallyDestroyed"/> reconciles the bookkeeping behind
        /// it.  Refused, silently, for an object this client does not own — a
        /// non-owner's despawn is dropped by the gateway anyway — and for one
        /// the registry no longer holds.  Called by the world-authority object
        /// alone: an avatar destroyed by a scene load is destroyed on every
        /// client by the same load, a world is what a peer keeps.
        /// </remarks>
        internal void RelayDespawnOfExternallyDestroyed(ulong objectId)
        {
            var relayed = objectId == 0UL ? null : _registry.Get(objectId);
            if (relayed == null || !CanDespawn(objectId)) return;
            SendDespawnPacket(objectId);
            // Ended here, as by Despawn: under every owner it had here.
            RememberEndedHere(objectId, relayed.OwnerPlayerId, relayed.PreviousOwnerPlayerId,
                              relayed.SpawnOwnerPlayerId);
        }

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
        /// Registers every prefab a <see cref="NetworkPrefabRegistry"/> lists.
        /// </summary>
        /// <remarks>
        /// The SDK loads the registry assigned to
        /// <see cref="NetworkSettings.prefabRegistry"/> itself, whenever a session is
        /// built. Call this for a registry you obtain at run time, for example from an
        /// asset bundle or Addressables. Rows without a prefab are skipped, and a row
        /// whose id is already registered replaces that mapping; both are reported in
        /// one warning. A <see langword="null"/> registry is ignored.
        /// </remarks>
        /// <param name="registry">The registry to load.</param>
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
        /// Routes the instantiation and destruction of networked objects through
        /// <paramref name="pool"/>, from the next spawn on.
        /// </summary>
        /// <remarks>
        /// Objects already alive are released to whichever pool is installed when
        /// they despawn. The pool does not carry over to the spawn manager built on
        /// the next connect or reconnect; install it from
        /// <c>NetworkManager.OnConnected</c>.
        /// </remarks>
        /// <param name="pool">The pool, or <see langword="null"/> to use <c>Instantiate</c> and <c>Destroy</c> again.</param>
        public void SetObjectPool(INetworkObjectPool pool) => _pool = pool;

        /// <summary>Removes the installed pool, so objects are created with <c>Instantiate</c> and destroyed with <c>Destroy</c>.</summary>
        public void ClearObjectPool() => _pool = null;

        /// <summary>The installed pool, or <see langword="null"/> when none is installed.</summary>
        public INetworkObjectPool ObjectPool => _pool;

        // ── Spawn / Despawn (Public API) ───────────────────────────────────────

        /// <summary>
        /// Creates a networked object from a registered prefab and relays the spawn
        /// to the room.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Spawn after <c>Rooms.OnRoomJoined</c>. An object spawned before this client
        /// has a seat in a room is created locally, reaches no other player, and a
        /// warning is logged.
        /// </para>
        /// <para>
        /// The object is created through the object pool when one is installed. Its
        /// id is allocated from this session's own id range, so two clients never
        /// pick the same id. The <see cref="NetworkBehaviour.DestroyWithOwner"/> value
        /// of the returned component, read at spawn time, decides whether the object
        /// is destroyed or given to the room's host when its owner leaves.
        /// </para>
        /// </remarks>
        /// <param name="prefabId">The id the prefab is registered under.</param>
        /// <param name="position">The world-space position.</param>
        /// <param name="rotation">The world-space rotation.</param>
        /// <param name="ownerPlayerId">
        /// The owner. <see langword="null"/> or empty means this client, the only owner
        /// a client can spawn under: another player's id is refused with an error. To
        /// give an object to another player, spawn it and then call
        /// <see cref="OwnershipManager.RequestOwnershipTransfer"/>.
        /// </param>
        /// <param name="sharedAuthority">
        /// <see langword="false"/> (the default) lets only the owner call RPCs on the
        /// object; <see langword="true"/> lets any member of the room call them. Use
        /// <see langword="true"/> for objects the room shares, such as a door or a
        /// scoreboard.
        /// </param>
        /// <returns>
        /// The object's first <see cref="NetworkBehaviour"/>, or <see langword="null"/>
        /// when the prefab id is not registered, the prefab has no
        /// <see cref="NetworkBehaviour"/>, <paramref name="ownerPlayerId"/> names another
        /// player, or <see cref="NetworkSettings.maxSpawnsPerSecond"/> or
        /// <see cref="NetworkSettings.maxSpawnsPerRoom"/> is reached. Check the result.
        /// </returns>
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

            var nb = CreateLocal(prefabId, objectId, owner, position, rotation, exemptFromRateCap: false, ownSpawn: true);
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
        /// Raised when the room refused a spawn this client made. The arguments are
        /// the object id and the reason.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The local object is kept, but it exists on this client only and reaches no
        /// other player. If you cannot use it, destroy it through the reference
        /// <see cref="Spawn"/> returned, not with <see cref="Despawn"/>, which would
        /// relay a despawn for an object the room never accepted.
        /// </para>
        /// <para>
        /// Only refusals the client cannot detect itself arrive here; the others are
        /// reported by <see cref="Spawn"/> when it is called. The refusal notice can be
        /// lost on the network, and then this event is not raised. A handler that
        /// throws is caught and logged.
        /// </para>
        /// </remarks>
        public event Action<ulong, SpawnPacketParser.SpawnRejectReason> OnSpawnRejected;

        /// <summary>
        /// Carry the subscribers of a spawn manager this one replaces, as the
        /// room, lobby and matchmaking managers carry theirs.  A spawn manager
        /// is rebuilt on every <c>Connect()</c> and every reconnect attempt; a
        /// handler attached before the first connect, or during a session, is
        /// on the instance the rebuild discards, and this event is the only
        /// report of a refused spawn.  (The ownership manager it holds carries
        /// its own, from the rebuild site.)
        /// </summary>
        internal void AdoptSubscribersFrom(SpawnManager previous)
        {
            if (previous == null || ReferenceEquals(previous, this)) return;
            OnSpawnRejected = SubscriberAdoption.Carry(OnSpawnRejected, previous.OnSpawnRejected);
        }

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
        /// Whether <see cref="Despawn"/> would be relayed to the room, rather than
        /// refused because the object belongs to another player.
        /// </summary>
        /// <remarks>
        /// Use it to decide whether to offer a "destroy" action; <see cref="Despawn"/>
        /// makes the same check itself.
        /// </remarks>
        /// <param name="objectId">The network object id to test.</param>
        /// <returns>
        /// <see langword="true"/> for an object this client owns, an object with no
        /// owner, and an id this client does not know.
        /// </returns>
        public bool CanDespawn(ulong objectId)
        {
            var nb = _registry.Get(objectId);
            return nb == null
                || !ObjectLifecycleAuthority.RefusesForeignOwner(
                       nb.OwnerPlayerId, _networkManager.LocalPlayerStringId);
        }

        /// <summary>
        /// Calls <see cref="Despawn"/> when this client may despawn the object, and
        /// reports whether it did instead of logging a refusal.
        /// </summary>
        /// <param name="objectId">The network object id to despawn.</param>
        /// <returns>
        /// <see langword="false"/>, doing nothing, when the object belongs to another
        /// player; <see langword="true"/> when <see cref="Despawn"/> was called. A call
        /// for an object that is already being despawned also returns
        /// <see langword="true"/>, and does nothing.
        /// </returns>
        public bool TryDespawn(ulong objectId)
        {
            if (!CanDespawn(objectId)) return false;
            Despawn(objectId);
            return true;
        }

        /// <summary>
        /// Destroys a networked object (or returns it to the pool) and relays the
        /// despawn to the room.
        /// </summary>
        /// <remarks>
        /// Refused, with a logged error and nothing destroyed, for an object another
        /// player owns; see <see cref="CanDespawn"/> and <see cref="TryDespawn"/>.
        /// </remarks>
        /// <param name="objectId">The network object id to despawn.</param>
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

            // The owners it had here, read before the teardown forgets them: a
            // copy naming one of them is stale once the despawn lands, and one
            // naming another is the object living on (RememberEndedHere).
            var despawned = _registry.Get(objectId);
            string ownerAtEnd = despawned != null ? despawned.OwnerPlayerId : null;
            string ownerBefore = despawned != null ? despawned.PreviousOwnerPlayerId : null;
            string ownerAtSpawn = despawned != null ? despawned.SpawnOwnerPlayerId : null;

            // Tear down the local instance BEFORE the wire send so that any
            // re-entrant Despawn arriving inside the same call stack
            // observes an empty registry slot and short-circuits.
            // DestroyLocal is idempotent: the in-flight set + the
            // registry-presence check together guarantee a second call
            // does not double-decrement counters, fire callbacks twice,
            // or record a stale pending-despawn entry.
            DestroyLocal(objectId);
            RememberEndedHere(objectId, ownerAtEnd, ownerBefore, ownerAtSpawn);

            SendDespawnPacket(objectId);
        }

        /// <summary>
        /// Record that the room's despawn relay ended <paramref name="objectId"/>
        /// on this client, so a spawn of it the wire delivers within the
        /// pending-despawn TTL is dropped: neither applied nor held for its
        /// owner's return.
        /// </summary>
        /// <remarks>
        /// The room forgets a despawned object one database write after it
        /// relays the despawn, and a replay it read in between — the promotion
        /// catch-up, or a join's replay ladder — carries the object still.  A
        /// despawn for an object this client did not hold was already on
        /// record (the pending-despawn record, spent by the first spawn it
        /// answers); this record is for one it held, and is not spent.  The
        /// relayed despawn is its owner's, accepted, so it ended the object for
        /// every owner; one this client ends itself is kept under its owner
        /// (<see cref="RememberEndedHere"/>).
        /// </remarks>
        internal void RememberDespawned(ulong objectId)
        {
            if (objectId == 0UL) return;
            _despawnedHere.Record(objectId, NowMillis());
        }

        /// <summary>
        /// Record that this client ended <paramref name="objectId"/> itself — by
        /// its own <see cref="Despawn"/> or the world's relayed teardown — under
        /// every owner it had here: <paramref name="ownerAtEnd"/>, the one before
        /// it, and the one it was spawned under.  A spawn of it naming one of
        /// them, delivered while the record stands, is dropped: neither applied
        /// nor held for its owner's return.
        /// </summary>
        /// <remarks>
        /// Under those owners, and no others, because the gateway relays a
        /// despawn only from the owner it knows: a transfer that raced this one
        /// leaves the object alive under an owner this client never saw it
        /// under, and a copy naming that owner is the room's live object.  A
        /// copy naming an earlier owner is the room's row read before it
        /// re-keyed it — a transfer's re-key trails its grant, and a row can be
        /// named for its spawner throughout.  An object with no owner on record
        /// is kept for every owner, as the room's despawn relay is.
        /// ⚠️ A transfer back to one of those earlier owners that raced this
        /// despawn leaves the object alive under it, and a copy of it is refused
        /// while the record stands; and an owner held before the one before the
        /// last is not on record.
        /// </remarks>
        private void RememberEndedHere(ulong objectId, string ownerAtEnd, string ownerBefore,
                                       string ownerAtSpawn)
        {
            if (objectId == 0UL) return;
            long now = NowMillis();
            if (string.IsNullOrEmpty(ownerAtEnd))
            {
                _despawnedHere.Record(objectId, now);
                return;
            }
            _endedUnderOwner.Record(ownerAtEnd, objectId, now);
            _endedUnderOwner.Record(ownerBefore, objectId, now);
            _endedUnderOwner.Record(ownerAtSpawn, objectId, now);
        }

        /// <summary>
        /// Forget the departed sender's clocks on every object booked by a
        /// departure whose delay has passed, where the object is still owned by
        /// the player who left — a handover since has forgotten them itself —
        /// and still here.  Driven from <see cref="Tick"/>.
        /// </summary>
        private void ForgetDueDepartedClocks(long nowMillis)
        {
            for (int i = _departedClocks.Count - 1; i >= 0; i--)
            {
                var booked = _departedClocks[i];
                if (nowMillis < booked.DueAtMillis) continue;
                _departedClocks.RemoveAt(i);

                var held = _registry.Get(booked.ObjectId);
                if (held == null || !string.Equals(held.OwnerPlayerId, booked.Owner, StringComparison.Ordinal))
                    continue;
                try
                {
                    var components = held.ObjectComponents;
                    for (int c = 0; c < components.Count; c++)
                    {
                        if (components[c] != null) components[c].ForgetDepartedSendersClocks();
                    }
                }
                catch (Exception ex)
                {
                    if (WarnGate.ShouldEmit(ref _lastDepartedClocksThrowWarnTicks))
                        Debug.LogException(ex);
                }
            }
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
        /// A join reply was applied: for the reach of the Room Service's
        /// replay ladder from that reply's arrival, a spawn is admitted on the
        /// per-room count cap alone and charged to no rate bucket — the room's
        /// live objects are arriving, once, behind the reply.
        /// </summary>
        /// <remarks>
        /// Called from the handler of every applied join reply — a first
        /// join, a matchmade seat, the explicit join of a two-step create —
        /// which is the reply the replay follows, and ahead of the transition
        /// that releases what was staged before it.  Closed by
        /// <see cref="ClearAll"/>, which every room leave and session end
        /// reaches.  The window's length is the world election's
        /// <see cref="WorldSpawnElection.PeerReplayGraceMillis"/>, held to the
        /// Room Service's ladder by that election's tests; its clock is the
        /// reply's arrival, so a main thread stalled in the join's own handler
        /// does not spend it.
        /// </remarks>
        internal void BeginRoomCatchUp() => _catchUp.Open(ArrivalMillis());

        /// <summary>
        /// When the packet being dispatched arrived, on this class's clock —
        /// now, for a call that is not inside a dispatched packet.
        /// </summary>
        /// <remarks>
        /// The dispatcher drains a bounded number of items per frame, so a
        /// packet is applied in the frame the main thread reaches it and not
        /// the one it landed in; a window measured from one packet's arrival
        /// to another's reads the stamp the dispatcher put on the item, and
        /// not the frame.
        /// </remarks>
        private long ArrivalMillis()
            => NowMillis() - RTMPE.Threading.MainThreadDispatcher.CurrentItemAgeMillis;

        /// <summary>
        /// Instantiate a networked object from the pre-room staging buffer's
        /// release — or from the spawns held for a departed owner's return —
        /// exempt from the per-second rate cap.
        /// </summary>
        /// <remarks>
        /// That cap bounds a SUSTAINED stream and DISCARDS what it refuses.  The
        /// staged set is a burst a client is handed that it can never ask for
        /// again — the room's live objects, replayed once as the session binds —
        /// so metering it against the cap loses most of it, which is the whole
        /// of <c>CORE-RD-01</c>.  It is bounded by the staging buffer's capacity
        /// and released a bounded number per frame by the caller.  The spawns
        /// held for a departed owner's return are the other: live spawns that
        /// arrived ahead of their owner's arrival, released together when it
        /// lands, and bounded by <see cref="DepartedOwnerSpawns{T}"/>.
        /// ⛔ The per-room COUNT cap below still applies to every one of them,
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
            bool? declaredDestroyWithOwner = null,
            bool ownSpawn = false)
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

            // Despawn-then-Spawn-again: an object ended here a moment ago for
            // every owner — by the room's despawn relay, or by this client with
            // no owner on record — is not brought back by a spawn of it sent
            // since: the room's replay can read its buffer before it has
            // forgotten the object.  A spawn of this client's own draws a fresh
            // id, so the question is the wire's alone.
            if (!ownSpawn)
            {
                if (_despawnedHere.IsRecorded(objectId, NowMillis()))
                {
                    RtmpeLog.Info(
                        "[SpawnManager] Spawn dropped: the object was despawned here moments ago; a replay " +
                        "read before the room forgot it does not bring it back.");
                    return null;
                }
                // Nor one this client ended itself under the owner this spawn
                // still names — torn down with that owner's departure, or
                // despawned here: the copy the room had not yet forgotten.  A
                // copy naming another owner is the object living on.
                if (_endedUnderOwner.Names(ownerPlayerId, objectId, NowMillis()))
                {
                    RtmpeLog.Info(
                        "[SpawnManager] Spawn dropped: the object ended here moments ago under the owner it " +
                        "names; a copy still naming that owner does not bring it back.");
                    return null;
                }
            }

            // Leave-before-Spawn: a Spawn whose owner has already left the room
            // raced behind that player's player_left on a separate relay path.
            // Creating it would leave an object owned by a departed player that no
            // live session can update or despawn (a permanent ghost); drop it.
            // The inbound path judges such a spawn before it gets here — holding
            // one the gateway sent after the departure for the owner's return and
            // dropping the rest (NetworkManager.ApplySpawnPacket →
            // HoldForOwnersReturn) — so what reaches this is a caller that judges
            // nothing.
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
            // hostile spawns is rejected before allocating a GameObject.  A
            // spawn that arrived inside the room's catch-up window is outside
            // the rate cap on the same terms as the staged release, and is
            // charged to the bucket only when it is metered by it.
            bool meteredByRate = !exemptFromRateCap && !_catchUp.IsOpen(ArrivalMillis());
            if (!CheckSpawnAdmission(exemptFromRateCap: !meteredByRate)) return null;

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
            if (meteredByRate) _spawnsThisSecond++;
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
            bool prevOwnSpawn = _creatingOwnSpawn;
            _creatingOwnSpawn = ownSpawn;
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
                _creatingOwnSpawn = prevOwnSpawn;
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
                    if (meteredByRate && _spawnsThisSecond > 0) _spawnsThisSecond--;
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
                // GameObject; the SDK is responsible for what the instance still
                // holds of the life that ended, which the despawn above left in
                // place on purpose so OnNetworkDespawn could read it.
                if (_pool != null)
                {
                    try { ReturnToPool(prefabId, nb); }
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

        // The pool hand-back's reset walks the SDK's own variable types; the
        // user callbacks it raises on the way (OnValueChanged, OnListChanged)
        // are each isolated inside the variable that raises them, so what
        // reaches this reporter is a defect of the SDK's — reported as that,
        // and not as the despawn callback's, with a budget of its own so a
        // faulting despawn cannot silence it.
        private static readonly Action<INbLifecycle, Exception> LogRecycleFault =
            (component, ex) =>
            {
                if (!WarnGate.ShouldEmit(ref _lastRecycleFaultWarnTicks)) return;
                Debug.LogError(
                    $"[RTMPE] the pool hand-back reset threw on " +
                    $"{component.GetType().Name}: {ex.GetType().Name}: {ex.Message} — the " +
                    "instance's other components are reset and it is handed to the pool " +
                    "regardless; on this component the variables from the fault onward " +
                    "carry their previous life's replicated state into the next.",
                    component as UnityEngine.Object);
            };

        private static long _lastRecycleFaultWarnTicks;

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
        /// The one way an instance goes back to the pool.  A pooled instance is
        /// reused, not reconstructed, so what its components still hold of the
        /// life that ended — every NetworkVariable registered outside
        /// <c>OnNetworkSpawn</c>, on the owner and on every replica — would be
        /// the state the next life begins from, on each peer a different one.
        /// The reset runs after the despawn, so <c>OnNetworkDespawn</c> saw the
        /// life's final values, and before the pool's <c>Release</c>, because
        /// the next life's own pre-spawn writes begin the moment the pool
        /// activates the instance again.
        /// </summary>
        /// <remarks>
        /// A component whose reset throws is reported and the others still
        /// reset; the release then proceeds, because an instance the pool never
        /// learns about is a leak the pool cannot see.  The rollback of a failed
        /// spawn does not come through here: it destroys rather than releases,
        /// for its own stated reason.
        /// </remarks>
        private void ReturnToPool(uint prefabId, NetworkBehaviour anchor)
        {
            SpawnLifecycleOps.RecycleAll(anchor.ObjectComponents, LogRecycleFault);
            _pool.Release(prefabId, anchor.gameObject);
        }

        /// <summary>
        /// Marks every NetworkVariable on every object this client owns to be sent
        /// again at the next flush, so a player who has just joined receives the
        /// current values. <c>OnValueChanged</c> does not fire.
        /// </summary>
        /// <remarks>
        /// The SDK calls it, together with <see cref="ScheduleFollowUpResync()"/>,
        /// when another player joins the room. Each <c>NetworkVariableList</c> is sent
        /// whole, so the other players raise <c>OnListChanged</c> with
        /// <c>NetworkListChangeKind.FullSync</c> for it, usually with unchanged
        /// content: a handler should compare rather than rebuild.
        /// </remarks>
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

        // ── The follow-up resync ─────────────────────────────────────────────
        //
        // The snapshot MarkAllVariablesDirtyForResync sends reaches the joiner
        // unacknowledged — the ARQ extension covers this client's own link to
        // the gateway, never the copy the gateway relays — and nothing re-sends
        // it; it can also be fanned out before the joiner is a receiver at all,
        // since the room publishes the join event ahead of the join reply that
        // seats it.  The Room Service re-replays the joiner's catch-up OBJECTS
        // after +300 ms and again +700 ms later for exactly that reason, and
        // the variables had no ladder of their own.  One more
        // resync, a second after the join, costs one extra flush per join.
        // Where it lands relative to the joiner's spawns is not guaranteed —
        // the last object re-replay is about a second after the first — and
        // does not need to be: a frame that still precedes its object's spawn
        // is held on the joiner and applied when the object appears, exactly
        // as the first snapshot's is.  The joiner's side (HeldVariableUpdates)
        // settles the ORDER; this settles a LOSS, and a lost scalar was
        // otherwise lost until its owner wrote it again.

        /// <summary>
        /// How long after a player joins the second snapshot is sent, in
        /// milliseconds.  A second: past the join reply and the first object
        /// replay on any link this SDK is deployed over, and short enough
        /// that a value the first snapshot lost is wrong for about a second.
        /// </summary>
        internal const long FollowUpResyncDelayMillis = 1000L;

        // Zero when nothing is scheduled.  Two joins inside the window share one
        // follow-up, at the LATER of the two times — the one that is past the
        // second joiner's reply, which is what the delay is for.
        //
        // So a stream of joins closer together than the delay defers the
        // follow-up for as long as the stream lasts, and that is not a loss:
        // every join sends the immediate snapshot to the whole room, so an
        // earlier joiner is covered by the next joiner's snapshot — the same
        // bytes, sooner than its own follow-up would have been. The follow-up
        // exists for the join that has nobody behind it.
        private long _followUpResyncDueMillis;

        /// <summary>
        /// Schedules a second <see cref="MarkAllVariablesDirtyForResync"/> one second
        /// from now, in case the first set of values did not reach a player who was
        /// joining. The SDK calls it when another player joins. Calling it again
        /// before the follow-up is sent moves the follow-up to one second after the
        /// latest call.
        /// </summary>
        public void ScheduleFollowUpResync() => ScheduleFollowUpResync(NowMillis());

        internal void ScheduleFollowUpResync(long nowMillis)
            => _followUpResyncDueMillis = nowMillis + FollowUpResyncDelayMillis;

        /// <summary>Whether a follow-up snapshot is scheduled and not yet sent.</summary>
        internal bool FollowUpResyncPending => _followUpResyncDueMillis != 0L;

        /// <summary>
        /// Send the scheduled follow-up snapshot once its time has come.
        /// Reached from <see cref="Tick"/>, unconditionally, because no packet
        /// arrives to trigger it.  Returns whether it fired.
        /// </summary>
        internal bool TickFollowUpResync(long nowMillis)
        {
            if (_followUpResyncDueMillis == 0L || nowMillis < _followUpResyncDueMillis)
                return false;

            // Retired before the walk: a subscriber re-entering the registry
            // must find nothing scheduled, or a spawn from inside the walk
            // would read a stale deadline.
            _followUpResyncDueMillis = 0L;
            MarkAllVariablesDirtyForResync();
            return true;
        }

        // Same rationale for the ClearAll teardown — Registry.Clear dispatches
        // user OnNetworkDespawn callbacks, any of which may legally read the
        // registry, and we cannot leave the shared GetAll buffer parked across
        // that re-entry window.
        private readonly List<NetworkBehaviour> _clearAllScratch =
            new List<NetworkBehaviour>(64);

        // ── Owner Leave Handling ───────────────────────────────────────────────

        /// <summary>
        /// Handles a player leaving the room: destroys, on this client, every object
        /// the player owns whose <see cref="NetworkBehaviour.DestroyWithOwner"/> is
        /// <see langword="true"/>. Not intended to be called from game code.
        /// </summary>
        /// <remarks>
        /// The player's other objects are left in place; the SDK gives them to the
        /// room's host with <see cref="OwnershipManager.ReassignObjectsToNewOwner"/>.
        /// </remarks>
        /// <param name="playerId">The id of the player who left.</param>
        public void OnPlayerLeftRoom(string playerId) => OnPlayerLeftRoom(playerId, departureSendCounter: -1);

        /// <summary>
        /// <see cref="OnPlayerLeftRoom(string)"/>, for a departure the gateway
        /// sent under <paramref name="departureSendCounter"/> — its send counter
        /// on this session, <c>-1</c> when the departure arrived with none.
        /// </summary>
        internal void OnPlayerLeftRoom(string playerId, long departureSendCounter)
        {
            if (string.IsNullOrEmpty(playerId)) return;

            // Tombstone the departure so a Spawn that races behind this
            // player_left (separate relay path, no mutual ordering) is not
            // created as a ghost owned by an absent player: one the gateway sent
            // before the departure is dropped, one it sent after is held for the
            // player's return (HoldForOwnersReturn).  And what was held from a
            // life the player has just ended again goes with it.
            long now = NowMillis();
            _departedPlayers.Record(playerId, now, departureSendCounter);
            _departedPlayers.TryGetTombstone(playerId, now, out _, out long departedAt);
            _spawnsHeldForReturn.DropSentBefore(playerId, departedAt);
            // What an earlier report of this departure tore down stays refused
            // as long as the tombstone this report re-arms.
            _endedUnderOwner.Rearm(playerId, now);

            var owned = _ownership.GetObjectsOwnedBy(playerId);
            foreach (var obj in owned)
            {
                if (obj == null) continue;

                if (obj.DestroyWithOwner)
                {
                    ulong objectId = obj.NetworkObjectId;
                    // Exception isolation: continue processing remaining objects.
                    try
                    {
                        // Kept on record under the owner that left — ahead of the
                        // teardown, so one that throws still leaves it: the room
                        // forgets the object with the departure, and a spawn of it
                        // sent since still naming that owner — a replay read
                        // before the room forgot it, a relay re-sent — would
                        // otherwise be held for the owner's return and handed back
                        // to a player who no longer holds it.
                        _endedUnderOwner.Record(playerId, objectId, now);
                        DestroyLocal(objectId);
                    }
                    catch (Exception ex)
                    {
                        if (WarnGate.ShouldEmit(ref _lastOwnedObjectTeardownThrowWarnTicks))
                            Debug.LogException(ex);
                    }
                }
                else
                {
                    // Reassigned to the room host by the caller
                    // (OwnershipManager.ReassignObjectsToNewOwner) — when this
                    // client knows who that is, which after a host's own
                    // departure it learns from a best-effort room event.  Its
                    // next writer writes on a clock of its own either way, so
                    // the departed sender's clocks are booked to be forgotten
                    // once its frames still in flight have landed
                    // (ForgetDueDepartedClocks).  Isolated like the teardown
                    // beside it.
                    try
                    {
                        _departedClocks.Add(new DepartedClocks(
                            obj.NetworkObjectId, playerId, now + DepartedClocksDelayMillis));
                    }
                    catch (Exception ex)
                    {
                        if (WarnGate.ShouldEmit(ref _lastDepartedClocksBookThrowWarnTicks))
                            Debug.LogException(ex);
                    }
                }
            }
        }

        /// <summary>
        /// Handles a player joining or rejoining the room: spawns from that player
        /// are accepted again after an earlier departure. Not intended to be called
        /// from game code.
        /// </summary>
        /// <param name="playerId">The id of the player who joined.</param>
        public void OnPlayerJoinedRoom(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            _departedPlayers.Remove(playerId);
        }

        /// <summary>
        /// Whether a spawn owned by <paramref name="ownerPlayerId"/> arrives
        /// inside that owner's departure tombstone.
        /// </summary>
        internal bool IsOwnerDeparted(string ownerPlayerId)
            => _departedPlayers.IsDeparted(ownerPlayerId, NowMillis());

        /// <summary>
        /// Judge a spawn packet whose owner's departure tombstone stands.
        /// <see cref="HeldSpawnVerdict.Held"/> — kept for the owner's return
        /// until the tombstone expires — only when the gateway sent it after the
        /// departure (<paramref name="relaySendCounter"/> past the counter the
        /// tombstone recorded), no despawn of the object has reached this
        /// client first, and the object was neither ended here for every owner —
        /// by the room's despawn relay, or by this client with no owner on record
        /// — nor ended here by this client under the owner the spawn names.
        /// <see cref="HeldSpawnVerdict.Dropped"/> otherwise: a
        /// straggler of the life that ended, or an object already despawned —
        /// the tombstone's answer before holds existed.
        /// <see cref="HeldSpawnVerdict.Refused"/> when a bound of the hold is full.
        /// </summary>
        internal HeldSpawnVerdict HoldForOwnersReturn(
            string ownerPlayerId, ulong objectId, long relaySendCounter, byte[] spawnPacket)
        {
            long now = NowMillis();
            if (!_departedPlayers.TryGetTombstone(ownerPlayerId, now, out long expiresAtMs, out long departedAt)
                || !DepartedOwnerSpawns<byte[]>.SentAfterDeparture(departedAt, relaySendCounter))
                return HeldSpawnVerdict.Dropped;
            // A despawn that reached this client first is the object's last
            // word, as it is for any spawn (CreateLocal): judged here, while it
            // is on record, rather than at a release that may come after its
            // record has expired.
            _pendingDespawns.Prune(now);
            if (_pendingDespawns.Consume(objectId)) return HeldSpawnVerdict.Dropped;
            // So is an object the room's despawn relay ended here, or one this
            // client ended itself under the owner this spawn names — torn down
            // with that owner's departure, or despawned: a spawn of it sent
            // since is the room's copy of what it had not yet forgotten, not
            // the returner's.
            if (_despawnedHere.IsRecorded(objectId, now)) return HeldSpawnVerdict.Dropped;
            if (_endedUnderOwner.Names(ownerPlayerId, objectId, now)) return HeldSpawnVerdict.Dropped;
            return _spawnsHeldForReturn.Hold(ownerPlayerId, spawnPacket, relaySendCounter, expiresAtMs, now)
                ? HeldSpawnVerdict.Held
                : HeldSpawnVerdict.Refused;
        }

        /// <summary>
        /// Take the spawns held for <paramref name="playerId"/>'s return, in the
        /// order they arrived — called once its arrival has lifted the tombstone.
        /// </summary>
        internal List<byte[]> TakeSpawnsHeldFor(string playerId)
            => _spawnsHeldForReturn.Take(playerId, NowMillis());

        // ── Cleanup ────────────────────────────────────────────────────────────

        /// <summary>
        /// Despawns every networked object on this client: each component's
        /// <c>OnNetworkDespawn</c> runs, then the objects are destroyed or returned to
        /// the pool.
        /// </summary>
        /// <remarks>
        /// Nothing is sent to the room, so other players keep their copies. The SDK
        /// calls it when leaving a room and when disconnecting.
        /// </remarks>
        /// <param name="resetObjectIdSpace">
        /// Whether to restart this session's object id sequence. Pass
        /// <see langword="false"/> when you call it while staying connected: with
        /// <see langword="true"/>, objects spawned afterwards can reuse ids the room
        /// still remembers, and other players do not see them.
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
                        ReturnToPool(entry.PrefabId, nb);
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
            _despawnedHere.Clear();
            _endedUnderOwner.Clear();
            _departedClocks.Clear();
            _departedPlayers.Clear();
            _spawnsHeldForReturn.Clear();
            ClearGeneration++;
            _despawnInFlight.Clear();
            // A follow-up snapshot scheduled for a room this client has left
            // would walk an empty registry at best, and the next room's
            // objects at worst.
            _followUpResyncDueMillis = 0L;

            // Reset spawn admission counters: a fresh room must start with a
            // clean rate bucket and a zero live count, and no catch-up expected.
            _spawnsThisSecond           = 0;
            _spawnRateBucketStartTicks  = 0;
            _currentSpawnCount          = 0;
            _rateLimitWarnedThisBucket  = false;
            _countLimitWarnedThisBucket = false;
            _catchUp.Close();

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
            _despawnedHere.Prune(nowMs);
            _endedUnderOwner.Prune(nowMs);
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

        /// <summary>
        /// The highest counter this session has issued an object id under, or
        /// 0 when it has issued none.
        /// </summary>
        /// <remarks>
        /// The receive path's id-space rule reads it to tell a peer reserving
        /// an id this client has not reached from an object this client created
        /// and has since handed on (<see
        /// cref="ObjectLifecycleAuthority.RefusesForeignClaimOnOwnIdSpace"/>).
        /// It carries across a room leave with the counter, and resets with it
        /// at a session end.
        /// </remarks>
        internal ulong LastIssuedLocalCounter => (ulong)Math.Max(0L, Interlocked.Read(ref _nextLocalId));

        /// <summary>
        /// The owner recorded on the registered object with this id, or null
        /// when none is registered.
        /// </summary>
        internal string RegisteredOwnerOf(ulong objectId)
        {
            var held = _registry.Get(objectId);
            return held != null ? held.OwnerPlayerId : null;
        }

        /// <summary>
        /// How many times <see cref="ClearAll"/> has run.  A caller applying a
        /// batch of packets reads it before the batch and stops when it moves:
        /// user code a spawn runs can end the room or the session, and what the
        /// batch still carries belongs to the state that was cleared.
        /// </summary>
        internal int ClearGeneration { get; private set; }

        /// <summary>Spawns held for their owners' return.</summary>
        internal int SpawnsHeldForReturnCount => _spawnsHeldForReturn.Count;

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

        /// <summary>
        /// Drive the per-frame clocks of the components this manager owns.
        /// </summary>
        /// <remarks>
        /// One hop, and it exists because the ownership manager is reached through
        /// this one rather than held by NetworkManager directly: its
        /// outstanding-transfer deadlines had no periodic driver at all, so a
        /// request the server refused aged out of a table nothing swept — and
        /// nobody was told (S4-47).
        /// <para>
        /// ⛔ Unconditional, deliberately.  The case the sweep exists for is a
        /// session where no further ownership packet arrives, so a gate on being
        /// in a room, on holding objects, or on anything else the wire decides
        /// would close it again on exactly that case.
        /// </para>
        /// </remarks>
        internal void Tick()
        {
            _ownership?.Tick();
            ForgetDueDepartedClocks(NowMillis());
            TickFollowUpResync(NowMillis());
            // What was held for a return that never came goes with the
            // tombstone that held it, whether or not another spawn or arrival
            // comes along to touch the hold.
            _spawnsHeldForReturn.Prune(NowMillis());
        }
    }
}
