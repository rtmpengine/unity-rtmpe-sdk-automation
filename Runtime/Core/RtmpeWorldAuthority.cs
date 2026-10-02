// RTMPE SDK — Runtime/Core/RtmpeWorldAuthority.cs
//
// The place a room's shared state lives: one object per room, owned by the
// host, handed to the next host when the host leaves, replayed to whoever joins
// late, and filled by the game with NetworkVariables like any spawned object.
//
// 🔑 Everything a game needs for state no player owns — the pickups, a random
// layout, the round — already had a runtime primitive: ownership moves to the
// host when an owner leaves (DestroyWithOwner=false), the object replay reaches
// a late joiner, and a snapshot follows it (§8 of architecture.md).  What it
// did not have was a PATH: the primitives reach the players who were present,
// and not the one who arrives after the host has gone — against a gateway that
// predates plan §10/2 the departed owner's rows leave the replay buffer, and
// the new host cannot re-send an object under an id space that is not its own.
// This component is that path, and the spawner beside it (RtmpeWorldSpawner)
// is who opens it; where the room's replay follows the owner the room keeps
// the path itself, and this component keeps the world it inherits.
//
// 🔑 Three things, each decided elsewhere and executed here:
//   • which of possibly several instances answers a key, and when to say so —
//     WorldAuthorityRegistry, on facts every client shares;
//   • a world this client inherited keeps its identity where the gateway says
//     the room's replay follows an object's owner (CapabilityFlags.
//     ReplayFollowsOwner) — the room hands a departed owner's buffered spawn
//     to its host, so the replay already names this client — and is spawned
//     again under this client's own id space with its state carried over where
//     the gateway does not, so the room's replay names a host who is present —
//     WorldAuthorityRegistry says when, WorldStateCopy says how; a world
//     inherited from a host whose state never reached this client (gone before
//     it arrived), or — handed to it before it held it — reached it only in
//     part, is born anew and then sent whole, kept or copied on the same terms,
//     the registry reading from the wire which of the two it is;
//   • every NetworkBehaviour on the object survives its owner's departure,
//     declared from here in Awake — the wire carries the ANCHOR component's
//     flag (the first NetworkBehaviour in GetComponents order), so a
//     NetworkTransform ahead of this component in the list would otherwise
//     make the world an object that dies with its host.
//
// ⛔ The host is an authority on a client.  A world spawned with Shared
// Authority is addressable by any member; what decides which member's call
// runs is the method's declared Caller ([RtmpeRpc(…, Caller = RpcCaller.Host)]
// runs only the host's), enforced on every receiver from what the gateway
// attests (plan §10/1) — an undeclared method answers any member's request,
// and what it applies is the game's own check inside the handler.  A decision
// no client may make belongs to a server function (RpcCaller.Server).
//
// 🔑 Game code keeps no reference to the instance.  It can change at a
// migration — against a gateway whose replay does not follow the owner, the
// new host's copy is a new object — so the API is Find(key) and the static
// OnWorldReady, which fires on every client each time the key's answer becomes
// a different live instance.

using System;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Marks a prefab as a room's world object, which holds state no player owns:
    /// one instance per key per room, spawned by the host, handed to the next host
    /// when the host leaves, and received by players who join later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Put it on a prefab beside the <see cref="NetworkBehaviour"/> that holds the
    /// world's NetworkVariables, give that prefab an id in <b>Window → RTMPE →
    /// Network Prefabs</b>, and add an <see cref="RtmpeWorldSpawner"/> to the scene.
    /// Reach the live instance with <see cref="Find"/>, learn of it from
    /// <see cref="OnWorldReady"/>, and populate a new world in
    /// <see cref="OnWorldBorn"/>.
    /// </para>
    /// <para>
    /// The prefab's root GameObject must be active and this component enabled; the
    /// spawner refuses a prefab that is not, except that an inactive root is
    /// accepted when an object pool is installed. Every
    /// <see cref="NetworkBehaviour"/> on the world survives its owner leaving,
    /// whatever its own <see cref="NetworkBehaviour.DestroyWithOwner"/> says.
    /// </para>
    /// <para>
    /// When the host leaves, the new host keeps the same object only when the
    /// server hands the departed host's objects over to it. Otherwise the new host
    /// spawns a copy that carries the world's state and removes the original, so a
    /// reference kept across frames can name an object that is gone: call
    /// <see cref="Find"/> when you need the world.
    /// </para>
    /// </remarks>
    [AddComponentMenu("RTMPE/World Authority")]
    [DisallowMultipleComponent]
    public sealed class RtmpeWorldAuthority : NetworkBehaviour, IWorldAuthorityInstance
    {
        // ── Configuration ──────────────────────────────────────────────────────

        [Header("World")]
        [Tooltip("The name game code finds this world by. One world per key per room; use the same "
                 + "key on every client.")]
        [SerializeField] private string _worldKey = "world";

        [Tooltip("Spawn the world so that any member of the room can call its RPCs. Off, only its "
                 + "owner can, so players who are not the host cannot ask the world for anything.")]
        [SerializeField] private bool _sharedAuthority = true;

        [Tooltip("Keep this client's copy across scene loads. Off, a Single scene load destroys "
                 + "the copy; when this client owns it, the room is told, and the next scene's "
                 + "spawner creates a new world with new contents.")]
        [SerializeField] private bool _persistAcrossSceneLoads = true;

        /// <summary>The configured key: the name <see cref="Find"/> looks this world up by.</summary>
        public string WorldKey => _worldKey;

        /// <summary>
        /// Whether the world is spawned so that any member of the room can call its
        /// RPCs. Every spawn of the prefab uses this value.
        /// </summary>
        public bool SharedAuthority => _sharedAuthority;

        /// <summary>Whether this client's copy is kept across scene loads.</summary>
        public bool PersistAcrossSceneLoads => _persistAcrossSceneLoads;

        /// <summary>
        /// <see langword="true"/> on a copy this client spawned to replace another
        /// instance of the world, with its contents carried over. On other clients
        /// the copy reads <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// A world is born once. A copy raises <see cref="OnWorldBorn"/> only when
        /// the instance it replaces never held the room's state: a spawn the room
        /// refused before its first frame, or a world whose state never reached this
        /// client.
        /// </remarks>
        public bool WasRecreated => _recreated;

        // ── The API ────────────────────────────────────────────────────────────

        /// <summary>
        /// The live world for <paramref name="worldKey"/> on this client, or
        /// <see langword="null"/> when none has spawned here or the only instance
        /// is a spawn the room refused.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call it when you need the world rather than keeping a reference: the
        /// instance can change. An instance answers from its first frame, the frame
        /// on which <see cref="OnWorldBorn"/> is raised, so an
        /// <see cref="OnWorldBorn"/> handler can reach its own world through this
        /// method.
        /// </para>
        /// <para>
        /// When two instances of one key exist for a moment, for example because two
        /// hosts' spawns crossed, the one with the smaller object id normally answers
        /// and the other's owner removes it. On other clients a new instance can
        /// answer before its variables arrive: read the world's state through the
        /// variables' change events.
        /// </para>
        /// </remarks>
        /// <param name="worldKey">The world's key.</param>
        /// <returns>The world, or <see langword="null"/>.</returns>
        public static RtmpeWorldAuthority Find(string worldKey)
            => s_registry.Elected(worldKey) as RtmpeWorldAuthority;

        /// <summary>
        /// Raised on every client when a key's live world becomes a different
        /// instance, including its first spawn. The argument is the instance
        /// <see cref="Find"/> now returns.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One event for all keys: read <see cref="WorldKey"/> from the argument. It
        /// is raised from the instance's first <c>Update</c>, after every
        /// component's <c>OnNetworkSpawn</c> and, on the owner, after
        /// <see cref="OnWorldBorn"/>. The world's variables arrive through their own
        /// change events.
        /// </para>
        /// <para>
        /// Nothing is raised when a world goes away, and an announced instance can
        /// later be replaced, for example the loser when two instances cross, or a
        /// spawn the room refused. When the host leaves and the same object is kept,
        /// nothing is raised. When the new host spawns a copy instead, the original's
        /// components run <c>OnNetworkDespawn</c>, and <see cref="Find"/> can return
        /// <see langword="null"/> between the original's despawn and the copy's first
        /// frame; neither means the world is gone for good.
        /// </para>
        /// </remarks>
        public static event Action<RtmpeWorldAuthority> OnWorldReady;

        /// <summary>
        /// Raised on the owner when a world that holds none of the room's state
        /// becomes the key's answer: populate the world here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Subscribe from a sibling component's <c>OnNetworkSpawn</c>. The event is
        /// raised from the instance's first <c>Update</c>, after every component's
        /// <c>OnNetworkSpawn</c>, and <see cref="Find"/> returns this instance inside
        /// the handler.
        /// </para>
        /// <para>
        /// It is raised for a world this client spawned, and for a world handed to
        /// this client whose state never reached it or reached it only in part; the
        /// values that did arrive are kept. It is not raised for a world that holds
        /// the room's state, nor for a copy of a world that was already born: a
        /// world is born once, whatever the room refused on the way.
        /// </para>
        /// <para>
        /// A world can be born in a room that still holds objects spawned for an
        /// earlier world, because objects whose
        /// <see cref="NetworkBehaviour.DestroyWithOwner"/> is <see langword="false"/>
        /// pass to the new host. If the handler spawns objects, count what the room
        /// already holds first. When other players already hold the world, it is
        /// then sent whole, and they take this client's values in place of theirs.
        /// </para>
        /// </remarks>
        public event Action OnWorldBorn;

        // ── Internals ──────────────────────────────────────────────────────────

        private static readonly WorldAuthorityRegistry s_registry = new WorldAuthorityRegistry();

        /// <summary>Whether any instance of the key is held here, announced or not.  Read by the spawner.</summary>
        internal static bool HasLiveInstance(string worldKey) => s_registry.HasLive(worldKey);

        // Re-armed on play-mode entry, as every static in this package is: with
        // domain reload off the registry and the subscribers of the previous
        // Play survive into the next one, naming objects that no longer exist.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            s_registry.Clear();
            OnWorldReady = null;
        }

        private bool   _registered;
        private string _registeredKey;
        private bool   _createdBySelf;
        private bool   _handedBeforeHeld;
        private bool   _recreated;
        private bool   _firstFrameDone;

        // A copy this client received of a world the gateway says the room's
        // host did not send (audit P4-E1): never registered, so it answers
        // nothing, rivals nothing and holds no spawner back, and dropped here
        // on its first frame.
        private bool   _unadmitted;
        private long   _lastUnadmittedWarnTicks;

        // When the world, sent whole at an adoption or a birth on an adopted
        // instance, is to be sent whole once more; zero when nothing is due.
        // A peer that missed the promotion forgets the previous host's clocks
        // only SpawnManager.DepartedClocksDelayMillis after the departure, and
        // until then refuses what this client sends — the one whole send
        // included, which nothing else would repeat.
        private long   _sendWholeAgainAtMillis;
        private const long SendWholeAgainAfterMillis = SpawnManager.DepartedClocksDelayMillis + 1000L;

        // The spawn manager this instance asked about its own spawn's fate;
        // held so the subscription is released on the life's end.
        private SpawnManager _askedSpawner;

        private long _lastRecreateRefusedWarnTicks;
        private long _lastCopySkippedWarnTicks;
        private long _lastSubscriberThrewWarnTicks;
        private long _lastEmptyKeyWarnTicks;
        private long _lastSpawnRefusedWarnTicks;

        ulong  IWorldAuthorityInstance.ObjectId      => NetworkObjectId;
        bool   IWorldAuthorityInstance.IsLive        => this != null && IsSpawned;
        bool   IWorldAuthorityInstance.IsOwnedBySelf => IsOwner;
        string IWorldAuthorityInstance.OwnerPlayerId => OwnerPlayerId;
        // What the wire delivered to this copy, over every sibling: a world
        // whose variables never received a value from their owner holds none
        // of the room's state, one handed to this client before it held it
        // holds it only where every variable received one, and one that
        // declares no variables cannot say (WorldAuthorityRegistry.
        // HoldsTheRoomsState decides).  ⚠️ A re-creation's copy carries its
        // state by a local copy rather than over the wire and so reads false
        // here whatever it holds; the registry asks this of an adopted instance
        // alone, which is the one instance the reading is about.
        bool   IWorldAuthorityInstance.HoldsOwnersState => WorldAuthorityRegistry.HoldsTheRoomsState(
            DeclaresReplicatedState, _handedBeforeHeld, InboundValuesDelivered, EveryVariableHoldsADeliveredValue);
        // What the wire delivered to this copy under the owner it names now: a
        // copy handed to a new owner carries the old owner's values, and says
        // nothing about whether the new one holds it.
        bool?  IWorldAuthorityInstance.ReceivedValues => DeclaresReplicatedState ? InboundValuesUnderCurrentOwner > 0 : (bool?)null;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        // Declared before the spawn reads it: SpawnManager.Spawn instantiates
        // the prefab — which runs Awake — and then puts the ANCHOR component's
        // DestroyWithOwner on the wire, where every client applies it to every
        // component of the object.  Setting it on every sibling here makes the
        // declaration independent of which component happens to be first.
        private void Awake()
        {
            var siblings = GetComponents<NetworkBehaviour>();
            for (int i = 0; i < siblings.Length; i++)
            {
                if (siblings[i] != null) siblings[i].DestroyWithOwner = false;
            }
        }

        protected override void OnNetworkSpawn()
        {
            // Ownership and the spawn's origin distinguish the ways an instance
            // arrives: a local Spawn seats this client as owner, and a spawn
            // from the wire names its sender or the owner the room has handed
            // it to since.  Ownership that moves later is the migration, and it
            // is recorded by OnOwnershipChanged rather than inferred here.
            //
            // ⚠️ One wire spawn names this client: the promotion catch-up
            // replays the room's objects to a new host (plan §10/2), and a world
            // it never held arrives handed to it already.  That is an adoption,
            // not a creation — recorded on the first frame, once the frames held
            // for the world have been applied at its spawn, so the adoption
            // reads whether it carries the room's state (a world that does is
            // kept as it is; one that does not is born in place).  ⛔ Told by
            // the origin, never by the id's space: a same-session leave and
            // rejoin keeps the space, so a world this client spawned in an
            // earlier stint of the room, handed to the host when it left, comes
            // back from a promotion under an id of its own.
            bool spawnedHere  = SpawnedByThisClient();
            _createdBySelf    = IsOwner && spawnedHere;
            _handedBeforeHeld = IsOwner && !_createdBySelf;
            _recreated      = false;
            _firstFrameDone = false;
            _sendWholeAgainAtMillis = 0L;
            // The key at the spawn, so an Inspector edit on a live instance
            // cannot leave its entry under a name nothing unregisters.
            _registeredKey = _worldKey;

            // ⛔ Admitted to the election only when this client spawned it or
            // the gateway says the room's host sent it (audit P4-E1).  The
            // election is by object id and every client computes it alike, so
            // a world any member spawned would otherwise be elected everywhere
            // over the host's.  Not registered, the copy is invisible to Find,
            // to the election and to the spawner's "is there a world" — and it
            // is dropped here, with nothing sent, on its first frame.
            _unadmitted = !WorldAuthorityRegistry.Admits(
                spawnedHere, GatewayAttestsSpawnHost(), SpawnSentByHost(), ThisClientIsHost());
            if (_unadmitted)
            {
                _registered = false;
                return;
            }

            _registered    = s_registry.Register(_registeredKey, this, _createdBySelf);
            if (!_registered && string.IsNullOrEmpty(_registeredKey)
                && WarnGate.ShouldEmit(ref _lastEmptyKeyWarnTicks))
                Debug.LogError(
                    "[RTMPE] RtmpeWorldAuthority has an empty World Key: nothing can find this "
                    + "world, it announces nothing, and when its host leaves it is handed on but "
                    + "never re-created, so no later joiner receives it. Set the key on the prefab.",
                    this);

            // A spawn this client issued can still be refused by the room —
            // at its object ceiling, most likely — and a refusal arrives later,
            // by an event on the spawn manager.  Asked from here, once per life,
            // so a refused world re-creates itself until the room takes a copy
            // (waiting while the room holds a world of somebody else's); and
            // the spawn is booked as an attempt, so the refusal is acted on one
            // interval after the spawn rather than on receipt — a slot the room
            // had no time to free is not asked for again a round trip later.
            if (_createdBySelf && _registered)
            {
                s_registry.RecreateAttempted(_registeredKey, this, HeldVariableUpdates.NowMillis());
                var manager = NetworkManager.Instance;
                _askedSpawner = manager != null ? manager.Spawner : null;
                if (_askedSpawner != null)
                {
                    _askedSpawner.OnSpawnRejected -= HandleSpawnRejected;
                    _askedSpawner.OnSpawnRejected += HandleSpawnRejected;
                }
            }

            // A root object only: DontDestroyOnLoad refuses a child, and a pool
            // that parents its instances has decided their lifetime itself.
            if (_persistAcrossSceneLoads && transform.parent == null)
                DontDestroyOnLoad(gameObject);
        }

        protected override void OnNetworkDespawn() => Unregister();

        protected override void OnDestroy()
        {
            // Destroyed by something other than the SDK — a scene load with
            // persistence off, a pool that parented it, the game's own
            // Object.Destroy — while still spawned and owned here: the room is
            // told, or every peer keeps electing a copy nobody will ever write
            // and the room's replay hands it to whoever joins next.  The SDK's
            // own teardown despawns before it destroys, so IsSpawned is false
            // here on that path and nothing is sent twice; a spawn the room
            // refused was never the room's to be told about; and an instance
            // whose entry a colliding id's replacement took is not the one the
            // room routes to, so its id is not its own to despawn.
            if (_registered && IsSpawned && IsOwner && s_registry.IsHeldForTheRoom(_registeredKey, this)) RelayDespawnOfADestroyedWorld();
            Unregister();
            base.OnDestroy();
        }

        protected override void OnOwnershipChanged(string previousOwner, string newOwner)
        {
            if (!_registered) return;
            // The migration's signal, whichever of the two room events
            // completes the reassignment: the SDK hands a departed host's
            // surviving objects to the new host from PlayerLeft when the new
            // host is already known, and from MasterClientChanged otherwise.
            // Both arrive here as one ownership change, and the decision is
            // taken on the next frame rather than inside the handover loop
            // that raised it.
            if (IsOwner)
            {
                // ⛔ EVERY handover, including a voluntary transfer the previous
                // owner survives — and the reason is not the one it looks like.
                // Against a gateway that predates plan §10/2 the re-creation is
                // not only about naming a host who is present: it is the only
                // thing that refreshes the room's replay.  There the buffered
                // entry holds the payload stamped at the ORIGINAL spawn, and an
                // ownership transfer re-points that row's owner KEY and leaves
                // its payload alone — so without a re-creation every client
                // joining after a transfer would spawn the world owned by the
                // client that first spawned it, and a later departure of that
                // client would migrate a world somebody else is driving.
                //
                // A gateway that asserts CapabilityFlags.ReplayFollowsOwner has
                // the Room Service rewrite the payload on every transfer and
                // hand a departed owner's to its host, so there the adoption is
                // recorded and the instance KEPT (VerdictFor): the world keeps
                // its identity, and the replay names this client already.
                if (!_createdBySelf) Adopt(ReplayFollowsOwner());
                return;
            }
            // On a peer the same handover reads as an owner change under an
            // instance it does not own; recorded with the clock, because a
            // new host that never held this instance will spawn a world of
            // its own beside it, and after a grace this copy is dropped.
            s_registry.MarkReassigned(_registeredKey, this, HeldVariableUpdates.NowMillis());
        }

        // The room refused this client's spawn of the world.  At the object
        // ceiling the refusal is momentary — a slot frees when anything
        // despawns — so the world re-creates itself until a copy is taken,
        // waiting while the room holds a world of somebody else's; for any
        // other reason a retry would meet the same answer, so the object
        // stays as SpawnManager leaves it: here, and nowhere else.
        //
        // ⚠️ The refusal is one datagram, delivered like every other packet
        // the gateway originates (SpawnManager.OnSpawnRejected): best-effort
        // on UDP, reliable on KCP.  Lost, it leaves this instance indistinct
        // from one the room took — elected, announced, holding the key against
        // the spawner — and nothing reaches the sender of an accepted spawn
        // that could tell the two apart.
        private void HandleSpawnRejected(ulong objectId, SpawnPacketParser.SpawnRejectReason reason)
        {
            if (objectId != NetworkObjectId || !_registered) return;
            if (reason == SpawnPacketParser.SpawnRejectReason.RoomAtObjectCeiling)
            {
                s_registry.MarkRefused(_registeredKey, this);
                return;
            }
            if (WarnGate.ShouldEmit(ref _lastSpawnRefusedWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] World '{_registeredKey}' (object {NetworkObjectId}) was refused by the "
                    + $"room ({reason}) and exists on this client alone; no other client will see it.",
                    this);
        }

        private void Update()
        {
            if (_unadmitted)
            {
                DropUnadmitted();
                return;
            }
            if (!_registered || !IsSpawned) return;
            long now = HeldVariableUpdates.NowMillis();
            bool replayFollowsOwner = ReplayFollowsOwner();

            if (!_firstFrameDone)
            {
                // Every component's spawn callback has run by now, so a sibling
                // that subscribed from its OnNetworkSpawn is on the list, and
                // the instance may answer the key from here on.  A world handed
                // to this client before it held it is adopted here, after the
                // frames held for it were applied at its spawn.
                _firstFrameDone = true;
                if (_handedBeforeHeld)
                {
                    // Handed before it was held is a fact of this adoption and
                    // of no later one: should ownership leave and come back, the
                    // world has been held as a peer in between, and is read so.
                    Adopt(replayFollowsOwner);
                    _handedBeforeHeld = false;
                }
                s_registry.MarkReady(_registeredKey, this);
            }

            // Born on the first frame it is the key's answer — after ready, so
            // a handler of the birth reaches its own world through Find, and
            // before the announcement, which is made from this frame and not
            // before.  The registry knows whether this world, on this instance
            // or on the one it was re-created from, was born already: a copy
            // of a born world is not born again, and a copy of a spawn the
            // room refused before its first frame — never the answer, never
            // born — is born in its place.  A world that is not the answer on
            // its first frame for any other reason is about to be removed.
            //
            // A world born on an instance this client adopted is one every peer
            // already holds, with the previous owner's values in it: sent whole
            // once the birth has written it, or a peer keeps whatever the birth
            // leaves alone — and a list the birth adds to reaches it as edits
            // of the list it holds.  A world of this client's own spawning is
            // the prefab's everywhere until its owner writes it, and a copy's
            // re-creation has sent it whole already (TryRecreate).
            if (s_registry.ShouldBeBorn(_registeredKey, this, replayFollowsOwner))
            {
                s_registry.MarkBorn(_registeredKey, this);
                RaiseWorldBorn();
                if (!_createdBySelf) SendWorldWhole();
            }

            // …and once more, past the moment a peer that missed the promotion
            // starts taking this client's writes.
            if (_sendWholeAgainAtMillis != 0L && now >= _sendWholeAgainAtMillis)
            {
                _sendWholeAgainAtMillis = 0L;
                if (IsOwner) MarkWorldDirty();
            }

            Announce();

            switch (s_registry.VerdictFor(_registeredKey, this, now, replayFollowsOwner))
            {
                case WorldInstanceVerdict.Recreate:        TryRecreate(now, heldByRoom: true);  break;
                case WorldInstanceVerdict.RecreateRefused: TryRecreate(now, heldByRoom: false); break;
                case WorldInstanceVerdict.DespawnSelf:     DespawnSelf();                       break;
                case WorldInstanceVerdict.DropLocally:     DropLocally();                       break;
            }
        }

        // Whether this session's gateway replays an object under its current
        // owner.  Asked each frame, of the published manager only — never a walk
        // of the scene — because a reconnect can reach a gateway of another
        // generation, and the answer is the gateway's, not this component's.
        private static bool ReplayFollowsOwner()
            => NetworkManager.TryGetPublishedInstance(out var manager) && manager.ReplayFollowsOwner;

        // Whether this session's gateway writes the byte that says which spawns
        // the room's host sent.  Asked of the published manager only, like the
        // replay question above.
        private static bool GatewayAttestsSpawnHost()
            => NetworkManager.TryGetPublishedInstance(out var manager) && manager.AttestedSpawnHost;

        // Whether the wire spawn running this instance's OnNetworkSpawn carried
        // that byte — asked from inside it, where the spawn manager knows.
        private static bool SpawnSentByHost()
            => NetworkManager.TryGetPublishedInstance(out var manager)
               && manager.Spawner != null
               && manager.Spawner.IsCreatingSpawnSentByHost;

        // Whether this client is the room's host, as its roster says — the one
        // condition under which the gateway marks this client's own spawn as
        // the host's on every other client.
        private static bool ThisClientIsHost()
            => NetworkManager.TryGetPublishedInstance(out var manager)
               && manager.Rooms != null
               && manager.Rooms.IsMasterClient;

        // Whether the spawn running this instance's OnNetworkSpawn is this
        // client's own Spawn — asked from inside it, where the spawn manager
        // knows which kind of spawn it is running.  Anything else reached it
        // from the wire, whoever the room names as its owner now.
        private static bool SpawnedByThisClient()
            => NetworkManager.TryGetPublishedInstance(out var manager)
               && manager.Spawner != null
               && manager.Spawner.IsCreatingOwnSpawn;

        // This client now owns an instance it did not create — a migration's
        // reassignment, or a world handed to it before it held it — and the
        // registry records the adoption.  Kept with the room's state (the
        // replay follows the owner), every peer holds this very object carrying
        // whatever of the previous owner's writes reached it, not necessarily
        // what reached this client: dirty, so the next flush sends this
        // client's values, the owner's from now on, to every peer, as a
        // re-creation does for its copy (TryRecreate).  One adopted with none
        // of the room's state, or — handed to this client before it held it —
        // with only part of it, is born instead (ShouldBeBorn) and sent whole
        // after its birth has written it; marked here it would first send the
        // prefab's values over the state every peer still holds.
        private void Adopt(bool replayFollowsOwner)
        {
            s_registry.MarkAdopted(_registeredKey, this);
            if (!replayFollowsOwner || !((IWorldAuthorityInstance)this).HoldsOwnersState) return;
            SendWorldWhole();
        }

        // The world sent whole — now, and once more when a peer that missed the
        // promotion has started taking this client's writes.  A re-creation's
        // copy needs no second send: every peer receives it as a new object,
        // whose gates open for its first frame.
        private void SendWorldWhole()
        {
            MarkWorldDirty();
            _sendWholeAgainAtMillis = HeldVariableUpdates.NowMillis() + SendWholeAgainAfterMillis;
        }

        // Every variable of every component of the object dirty, so the next
        // flush sends the whole world as this client holds it — a list as one
        // FullSync, whatever edits of it were waiting.  The codecs apply an
        // inbound value, and a local copy, without marking anything.
        private void MarkWorldDirty()
        {
            var components = ObjectComponents;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] != null) components[i].MarkAllVariablesDirty();
            }
        }

        // ── The decisions, executed ────────────────────────────────────────────

        private void Unregister()
        {
            if (_askedSpawner != null)
            {
                _askedSpawner.OnSpawnRejected -= HandleSpawnRejected;
                _askedSpawner = null;
            }
            if (!_registered) return;
            _registered = false;
            s_registry.Unregister(_registeredKey, this);
        }

        private void RelayDespawnOfADestroyedWorld()
        {
            // Instance answers null while the application quits, and the
            // session is ending with it: nothing to tell.
            var manager = NetworkManager.Instance;
            var spawner = manager != null ? manager.Spawner : null;
            if (spawner == null) return;
            spawner.RelayDespawnOfExternallyDestroyed(NetworkObjectId);
        }

        // A copy this client holds of a world its owner never held — or a spawn
        // of this client's own the room refused and another of this client's
        // refused spawns outranks: dropped here, with nothing on the wire.
        private void DropLocally()
        {
            var manager = NetworkManager.Instance;
            var spawner = manager != null ? manager.Spawner : null;
            if (spawner == null) return;
            RtmpeLog.Info(IsOwner
                ? $"[RTMPE] World '{_registeredKey}' (object {NetworkObjectId}) was refused by the room and "
                  + "is outranked by another world of this client's; this copy is dropped."
                : $"[RTMPE] World '{_registeredKey}' (object {NetworkObjectId}) was handed to a host "
                  + "that has been running a world of its own beside it; this client's copy is dropped.");
            spawner.DestroyLocal(NetworkObjectId);
        }

        // A world the election may not admit (audit P4-E1).  A copy this client
        // owns is despawned on the wire: every other client drops it anyway,
        // and only its owner can end the room's record of it and the row that
        // replays it to every later joiner — this client's own spawn made while
        // it was not the host, or a world handed to it that its sender never
        // had the host's word for.  A copy another client owns is torn down
        // here alone, with nothing sent: its owner's despawn is the one the
        // room would take.  Retried each frame until a spawn manager is there
        // to do it.
        private void DropUnadmitted()
        {
            if (!IsSpawned)
            {
                _unadmitted = false;
                return;
            }
            var manager = NetworkManager.Instance;
            var spawner = manager != null ? manager.Spawner : null;
            if (spawner == null) return;
            _unadmitted = false;
            if (WarnGate.ShouldEmit(ref _lastUnadmittedWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] World '{_registeredKey}' (object {NetworkObjectId}) was not spawned by the "
                    + "room's host as the server records it, so it is removed and the room's own world is "
                    + "kept. Only the host spawns a world; spawn it with a World Spawner. A world stored by "
                    + "the server before it recorded who sent each spawn is removed the same way, and the "
                    + "host's World Spawner creates a new one.",
                    this);
            if (IsOwner) spawner.Despawn(NetworkObjectId);
            else spawner.DestroyLocal(NetworkObjectId);
        }

        private void Announce()
        {
            var elected = s_registry.TakeAnnouncement(_registeredKey) as RtmpeWorldAuthority;
            if (elected == null) return;
            var handlers = OnWorldReady;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<RtmpeWorldAuthority>)d)(elected); }
                catch (Exception ex) { ReportSubscriberThrow("OnWorldReady", ex); }
            }
        }

        private void RaiseWorldBorn()
        {
            var handlers = OnWorldBorn;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception ex) { ReportSubscriberThrow("OnWorldBorn", ex); }
            }
        }

        // Spawn a copy of this object under this client's own id space, carry
        // the state across, and remove this one — the order that leaves the
        // room's replay naming a host who is present.  The same path serves a
        // spawn the room refused at its ceiling: the copy is a fresh attempt,
        // and this object — which no other client holds — is torn down here
        // alone behind it, there being nobody to tell.  A world left
        // un-migrated is a world no later joiner will see.
        //
        // The clock enters this method to be booked and for nothing else: the
        // registry decides when an attempt is due, and books the next one
        // before this one can fail — on this instance, and through
        // MarkSuccessor on the copy, which is the instance a refusal reaches.
        // The interval it books is the one the warnings quote.
        private void TryRecreate(long now, bool heldByRoom)
        {
            long retryInMillis = s_registry.RecreateAttempted(_registeredKey, this, now);

            var manager = NetworkManager.Instance;
            var spawner = manager != null ? manager.Spawner : null;
            if (spawner == null) return;

            if (!spawner.TryGetPrefabIdOfObject(NetworkObjectId, out uint prefabId))
            {
                if (WarnGate.ShouldEmit(ref _lastRecreateRefusedWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] World '{_registeredKey}' (object {NetworkObjectId}), {Provenance(heldByRoom)}, "
                        + "cannot be re-created: the spawn manager holds no prefab id for it. Asked again "
                        + $"in {retryInMillis} ms; until it succeeds, late joiners will not receive this "
                        + "world.", this);
                return;
            }

            // New first, then old: the copy needs its source alive, and a peer
            // that receives the two in either order settles them by id.  On a
            // room at its object ceiling the price of this order is one
            // refusal: the copy is asked for while the object it replaces
            // still counts — refused unless the despawn overtakes the spawn
            // on the way — and the room takes a later attempt instead.
            var anchor = spawner.Spawn(prefabId, transform.position, transform.rotation, null, _sharedAuthority);
            var replacement = anchor != null ? anchor.GetComponent<RtmpeWorldAuthority>() : null;
            if (replacement == null)
            {
                if (WarnGate.ShouldEmit(ref _lastRecreateRefusedWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] World '{_registeredKey}' (object {NetworkObjectId}), {Provenance(heldByRoom)}, "
                        + "could not be spawned again (see the spawn manager's own message). Retrying "
                        + $"in {retryInMillis} ms; until it succeeds, late joiners will not receive this "
                        + "world.", this);
                return;
            }

            replacement._recreated = true;
            // The registry says why a record is refused, and the warning says
            // it in the terms of the two objects; what follows still applies,
            // the copy being spawned.  Named from an instance that ends with
            // this method, so once per attempt.
            var record = s_registry.MarkSuccessor(_registeredKey, this, replacement);
            if (record != SuccessorRecord.Recorded)
                Debug.LogWarning(
                    $"[RTMPE] World '{_registeredKey}' (object {NetworkObjectId}) was re-created, but the copy "
                    + $"(object {replacement.NetworkObjectId}) is not recorded as its successor: "
                    + $"{SuccessorRefusedCause(record, replacement)}", this);
            int copied = CopyStateInto(replacement, out int skipped);
            if (skipped > 0 && WarnGate.ShouldEmit(ref _lastCopySkippedWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] World '{_registeredKey}': {copied} variable(s) handed to the "
                    + $"migrated copy and {skipped} not — a component or variable the two instances "
                    + "do not share at the same position. The copy starts those at their defaults.",
                    this);

            // Dirty, so the next flush sends the carried state to every peer:
            // the codecs applied it as an inbound write, which marks nothing.
            replacement.MarkWorldDirty();

            // An inherited world is despawned on the wire, because the room and
            // every peer hold it; one the room refused is torn down here alone,
            // on the teardown a wire despawn takes and with nothing sent.
            if (heldByRoom) spawner.Despawn(NetworkObjectId);
            else spawner.DestroyLocal(NetworkObjectId);
        }

        private static string Provenance(bool heldByRoom)
            => heldByRoom ? "passed to this client by a host migration" : "refused by the room at its object ceiling";

        // Each refusal the registry can give, in the terms of the two objects.
        // The copy registered under the key its prefab carries now; this
        // instance under the key it carried at its spawn — they differ only
        // when the prefab's key was edited while this world was live.  A copy
        // spawned with no seat to own it is nobody's here: held as a peer's
        // copy would be, written by nobody.  The other two — this instance's
        // own entry gone, or the spawner handing this very object back — are
        // named for completeness; the verdict that reaches this method holds
        // an entry for this instance, and a live object is not in any pool.
        private static string SuccessorRefusedCause(SuccessorRecord record, RtmpeWorldAuthority replacement)
        {
            switch (record)
            {
                case SuccessorRecord.CopyUnknown:
                    return $"its prefab's World Key is now '{replacement._registeredKey}', changed while this "
                        + "world was live, so the copy is a world of that key, born anew on top of the "
                        + "contents it was handed. Keep the key fixed on the prefab.";
                case SuccessorRecord.CopyNotOfThisClientsMaking:
                    return "it was spawned with no seat to own it, so this client holds it as it would hold "
                        + "another player's copy — answering Find, written by nobody, never re-created and "
                        + "never born.";
                case SuccessorRecord.SourceUnknown:
                    return "this world's own registry entry is gone, so the copy carries its contents as a "
                        + "world of its own, born anew on top of them.";
                case SuccessorRecord.SameInstance:
                    return "the spawn manager handed this very object back as its copy: no copy exists, "
                        + "and this object is torn down as the one replaced.";
                default:
                    return record.ToString();
            }
        }

        private int CopyStateInto(RtmpeWorldAuthority replacement, out int skipped)
        {
            skipped = 0;
            var mine   = ObjectComponents;
            var theirs = replacement.ObjectComponents;
            int pairs  = Math.Min(mine.Count, theirs.Count);
            skipped   += Math.Abs(mine.Count - theirs.Count);

            int copied = 0;
            for (int i = 0; i < pairs; i++)
            {
                var source = mine[i];
                var target = theirs[i];
                if (source == null || target == null || source.GetType() != target.GetType())
                {
                    skipped++;
                    continue;
                }
                copied  += WorldStateCopy.CopyTrackedVariables(
                    source.TrackedVariables, target.TrackedVariables, out int skippedHere);
                skipped += skippedHere;
            }
            return copied;
        }

        private void DespawnSelf()
        {
            var manager = NetworkManager.Instance;
            var spawner = manager != null ? manager.Spawner : null;
            if (spawner == null) return;
            RtmpeLog.Info(
                $"[RTMPE] World '{_registeredKey}' arrived twice; this client's copy "
                + $"(object {NetworkObjectId}) lost the election and is removed.");
            spawner.Despawn(NetworkObjectId);
        }

        private void ReportSubscriberThrow(string eventName, Exception ex)
        {
            if (WarnGate.ShouldEmit(ref _lastSubscriberThrewWarnTicks))
                Debug.LogError(
                    $"[RTMPE] RtmpeWorldAuthority.{eventName} subscriber threw "
                    + $"{ex.GetType().Name}: {ex.Message}", this);
        }
    }
}
