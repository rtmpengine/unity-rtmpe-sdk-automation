// RTMPE SDK — Runtime/Core/RtmpeWorldSpawner.cs
//
// Attach it, assign a world prefab, and the room has a world: spawned by the
// host when the room is entered, exactly once per room, and never by anybody
// else.
//
// 🔑 Who spawns and when is WorldSpawnElection's decision, from the room's own
// events and a clock — this is the engine calls and the wiring, kept as small
// as it can be made, on the pattern RtmpeConnectionBootstrap set with
// ConnectionBootstrapOps.  What happens to the world after it exists — the
// election between two copies, the migration to a new host — is
// RtmpeWorldAuthority's, on the instance itself; this component never touches
// a world that is already there.
//
// ⛔ A scene object, one per scene that has a world.  The world it spawns
// outlives the scene (RtmpeWorldAuthority keeps it, by default); the spawner
// need not, because a spawner bound while the session is already in a room
// asks the same question an entry event would have answered, and finds the
// world already there.

using System;
using RTMPE.Rooms;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Spawns the room's world object, a prefab carrying
    /// <see cref="RtmpeWorldAuthority"/>, from the host, once per room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add it to any object in the scene and assign <b>World Prefab</b>. The prefab
    /// needs an id in <b>Window → RTMPE → Network Prefabs</b> and an
    /// <see cref="RtmpeWorldAuthority"/>, which supplies its key and its
    /// shared-authority setting. The world appears at this object's transform.
    /// </para>
    /// <para>
    /// Only the host spawns. A host who entered the room alone spawns at once; one
    /// who entered with others waits a second and a half for the room's existing
    /// objects and spawns only if no world arrived. A player promoted to host also
    /// waits a second and a half, then spawns a world only if none exists; an
    /// existing world passes to the new host (see <see cref="RtmpeWorldAuthority"/>).
    /// A prefab whose World Authority is disabled, or whose root is inactive while
    /// no object pool is installed, is refused with a warning.
    /// </para>
    /// </remarks>
    [AddComponentMenu("RTMPE/World Spawner")]
    [DisallowMultipleComponent]
    public sealed class RtmpeWorldSpawner : MonoBehaviour
    {
        // ── Configuration ──────────────────────────────────────────────────────

        [Header("World")]
        [Tooltip("The prefab that carries the World Authority component. Give it an id in Window → "
                 + "RTMPE → Network Prefabs. Its World Key and Shared Authority are read from that "
                 + "component.")]
        [SerializeField] private GameObject _worldPrefab;

        /// <summary>
        /// The key of the world this spawner spawns, read from the prefab's
        /// <see cref="RtmpeWorldAuthority"/>; <see langword="null"/> when no prefab
        /// is assigned or it has no <see cref="RtmpeWorldAuthority"/>.
        /// </summary>
        public string WorldKey
        {
            get
            {
                var authority = PrefabAuthority();
                return authority != null ? authority.WorldKey : null;
            }
        }

        // ── State ──────────────────────────────────────────────────────────────

        private readonly WorldSpawnElection _election = new WorldSpawnElection();

        private NetworkManager _manager;
        private RoomManager    _rooms;
        private bool           _subscribed;

        // The prefab's component, resolved once per prefab reference rather
        // than by a GetComponent per frame.
        private GameObject          _resolvedPrefab;
        private RtmpeWorldAuthority _resolvedAuthority;

        private long _lastNoPrefabWarnTicks;
        private long _lastInactiveRootWarnTicks;
        private long _lastDisabledAuthorityWarnTicks;
        private long _lastSpawnRefusedWarnTicks;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void OnEnable() => Rebind();

        // Disable is not departure: the room goes on and the world with it.
        // Only the listening and the pending decision end here — a decision
        // taken while nothing listens would be taken on stale facts.
        private void OnDisable()
        {
            Unsubscribe();
            _election.RoomLeft();
        }

        private void OnDestroy() => Unsubscribe();

        // The manager is a singleton the scene owns, and the room facade it
        // hands out is rebuilt per session — RoomManager is constructed fresh
        // on each (re)connect — so a component holding the previous one is
        // subscribed to an object nothing raises events on any more.
        // RtmpeConnectionBootstrap re-binds on exactly this reasoning.
        private void Update()
        {
            Rebind();
            Decide();
        }

        // ── Wiring ─────────────────────────────────────────────────────────────

        private void Rebind()
        {
            // Published only, never a walk of the scene — Update asks every frame.
            var live  = NetworkManager.TryGetPublishedInstance(out var manager) ? manager : null;
            var rooms = live == null ? null : live.Rooms;

            if (ReferenceEquals(live, _manager) && ReferenceEquals(rooms, _rooms))
            {
                if (!_subscribed && _manager != null) Subscribe();
                return;
            }

            Unsubscribe();
            _manager = live;
            _rooms   = rooms;
            // A rebuilt facade is a new session, and a decision pending from
            // the last one describes a room this session is not in.
            _election.RoomLeft();
            if (_manager == null) return;

            Subscribe();
        }

        // ⚠️ Every attachment removes before it adds: the SDK adopts a facade's
        // subscribers onto its replacement, so a bare `+=` on a re-bind adds a
        // second copy of a handler the new instance already carries.
        private void Subscribe()
        {
            if (_rooms != null)
            {
                _rooms.OnRoomJoined          -= HandleRoomJoined;
                _rooms.OnRoomLeft            -= HandleRoomLeft;
                _rooms.OnMasterClientChanged -= HandleMasterClientChanged;
                // OnRoomJoined only, never OnRoomCreated: the join that follows
                // a creation is what seats the creator, and a spawn issued on
                // the creation is issued before there is a room to put it in.
                // Matchmaking reaches the same event.
                _rooms.OnRoomJoined          += HandleRoomJoined;
                _rooms.OnRoomLeft            += HandleRoomLeft;
                _rooms.OnMasterClientChanged += HandleMasterClientChanged;
            }
            _manager.OnDisconnected -= HandleDisconnected;
            _manager.OnDisconnected += HandleDisconnected;
            _subscribed = true;

            // ⛔ A component enabled after the room was entered sees no entry
            // event at all, so binding also ASKS: the election arms from the
            // room as it stands, and the decision finds the world if a host
            // already spawned it.
            //
            // ⚠️ From the ANNOUNCED entry, never from holding a room. A create
            // reply puts the room on this client while the session is still
            // unseated at the gateway, and a world spawned there is accepted
            // locally, relayed to nobody, and never announced again — the
            // spawner then holds a live instance, so the join that follows
            // decides there is nothing to spawn and the room has no world at
            // all. Every late bind reaches this arm — a mid-game scene load in
            // a room entered long ago reaches it and passes — and the flows
            // where the answer MATTERS are the ones that bind inside that
            // window: a scene whose spawner loads from OnRoomCreated, and an
            // AutoJoinAsHost of false.
            if (_rooms != null && _rooms.RoomEntryAnnounced)
                _election.RoomEntered(_rooms.IsMasterClient, PlayerCountOf(_rooms.CurrentRoom), Now());
        }

        private void Unsubscribe()
        {
            if (_rooms != null)
            {
                _rooms.OnRoomJoined          -= HandleRoomJoined;
                _rooms.OnRoomLeft            -= HandleRoomLeft;
                _rooms.OnMasterClientChanged -= HandleMasterClientChanged;
            }
            if (_manager != null) _manager.OnDisconnected -= HandleDisconnected;
            _subscribed = false;
        }

        // ── Handlers ───────────────────────────────────────────────────────────

        // The SDK's own handlers sit ahead of these on every list — they are
        // wired before the application can reach the manager — so the roster
        // and the host role read here are the ones the event announces.
        private void HandleRoomJoined(RoomInfo room)
            => _election.RoomEntered(_rooms != null && _rooms.IsMasterClient, PlayerCountOf(room), Now());

        private void HandleRoomLeft() => _election.RoomLeft();

        private void HandleMasterClientChanged(string previousMasterId, string newMasterId)
            => _election.HostChanged(_rooms != null && _rooms.IsMasterClient, Now());

        private void HandleDisconnected(DisconnectReason reason) => _election.RoomLeft();

        // ── The decision, executed ─────────────────────────────────────────────

        private void Decide()
        {
            // The same seat the arming asks about: a decision armed for an
            // announced entry can still be executed a frame later, and a room
            // switch between the two leaves this client holding a room it is
            // not yet seated in.
            if (!_subscribed || _rooms == null || !_rooms.RoomEntryAnnounced || !_election.IsArmed) return;

            var authority = PrefabAuthority();
            if (authority == null || string.IsNullOrEmpty(authority.WorldKey))
            {
                // Named while a decision is pending, and gated: a spawner with
                // no world to spawn is a spawner that does nothing, and doing
                // nothing in silence is how this component would be diagnosed.
                if (WarnGate.ShouldEmit(ref _lastNoPrefabWarnTicks))
                    Debug.LogWarning(
                        authority == null
                            ? "[RTMPE] RtmpeWorldSpawner has no world to spawn: assign a prefab that "
                              + "carries RTMPE → World Authority to its World Prefab field."
                            : "[RTMPE] RtmpeWorldSpawner: the World Prefab's World Authority has an "
                              + "empty World Key; nothing could find the world it would spawn. Set "
                              + "the key on the prefab.", this);
                return;
            }

            // ⛔ A world Unity would run nothing on is refused rather than
            // spawned: no Awake, Start or Update reaches an inactive object or
            // a disabled component, so the instance would register from the
            // SDK's own spawn call and then do nothing for the rest of the
            // session — no election, no birth, no re-creation, no migration —
            // and every client would hold an object nobody can write, which no
            // client could repair.
            //
            // ⚠️ The root's own state is read only where nothing will change it:
            // a project that installed an object pool has SpawnManager activate
            // what the pool hands back, so an inactive root is that project's
            // ordinary way of keeping a spare — and refusing it would take the
            // world away from a setup that works.  The component's own switch
            // has no such exception.
            if (RefusedForSomethingUnityWouldNotRun(authority)) return;

            // Not while the room's objects are still on their way: the world the
            // room already has may be among them (WorldSpawnElection.ObjectsStillArriving).
            if (_manager != null && _manager.RoomObjectsStillArriving)
            {
                _election.ObjectsStillArriving(Now());
                return;
            }

            bool worldExists = RtmpeWorldAuthority.HasLiveInstance(authority.WorldKey);
            if (_election.ShouldSpawn(_rooms.IsMasterClient, worldExists, Now()))
                SpawnWorld(authority);
        }

        /// <summary>
        /// Whether the prefab would produce an object Unity runs nothing on,
        /// reported once a second and never spawned.
        /// </summary>
        private bool RefusedForSomethingUnityWouldNotRun(RtmpeWorldAuthority authority)
        {
            // An object pool activates what it hands back (SpawnManager), so an
            // inactive root is only dead where no pool will.
            bool pooled = _manager != null && _manager.Spawner != null
                          && _manager.Spawner.ObjectPool != null;

            if (!_worldPrefab.activeSelf && !pooled)
            {
                if (WarnGate.ShouldEmit(ref _lastInactiveRootWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] RtmpeWorldSpawner: the World Prefab '" + _worldPrefab.name
                        + "' has an inactive root, so Unity would run nothing on the object it "
                        + "spawns — not on this client and not on any replica. Tick the checkbox "
                        + "beside the prefab's name at the top of the Inspector; a world's root "
                        + "must be active, unless this project installs an object pool, which "
                        + "activates what it hands back. Nothing was spawned.", this);
                return true;
            }

            if (!authority.enabled)
            {
                if (WarnGate.ShouldEmit(ref _lastDisabledAuthorityWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] RtmpeWorldSpawner: the World Prefab '" + _worldPrefab.name
                        + "' carries a World Authority that is switched off, so nothing would "
                        + "elect, be born or migrate on the object it spawns — on this client or "
                        + "any other. Tick the component's own checkbox. Nothing was spawned.",
                        this);
                return true;
            }

            return false;
        }

        private void SpawnWorld(RtmpeWorldAuthority prefabAuthority)
        {
            var spawner = _manager != null ? _manager.Spawner : null;
            if (spawner == null) return;

            if (!spawner.TryGetPrefabId(_worldPrefab, out uint prefabId))
            {
                // Named rather than silent, and both supported paths named,
                // because a spawn that does not happen leaves nothing to read.
                Debug.LogError(
                    "[RTMPE] RtmpeWorldSpawner: no prefab id is registered for " + _worldPrefab.name
                    + ", so no client could resolve it. Select the prefab in the Project window, open "
                    + "Window → RTMPE → Network Prefabs and press \"Allocate id for selection\"; then "
                    + "press \"Generate RtmpePrefabIds.cs and the prefab registry\" and assign the "
                    + "generated registry asset to the NetworkSettings asset's Prefab Registry field — "
                    + "or register it yourself with Spawner.RegisterPrefab before the room is entered.",
                    this);
                return;
            }

            // Shared authority as the prefab declares it, so the migration's
            // copy and this spawn declare the same thing to the room.
            var anchor = spawner.Spawn(
                prefabId, transform.position, transform.rotation, null, prefabAuthority.SharedAuthority);
            if (anchor == null && WarnGate.ShouldEmit(ref _lastSpawnRefusedWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] RtmpeWorldSpawner: the world '{prefabAuthority.WorldKey}' was not spawned "
                    + "(see the spawn manager's own message). The room has no world until a host "
                    + "spawns one.", this);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private RtmpeWorldAuthority PrefabAuthority()
        {
            if (_worldPrefab == null) return null;
            if (!ReferenceEquals(_worldPrefab, _resolvedPrefab))
            {
                _resolvedPrefab    = _worldPrefab;
                _resolvedAuthority = _worldPrefab.GetComponent<RtmpeWorldAuthority>();
            }
            return _resolvedAuthority;
        }

        private static int PlayerCountOf(RoomInfo room) => room != null ? room.PlayerCount : 1;

        private static long Now() => HeldVariableUpdates.NowMillis();
    }
}
