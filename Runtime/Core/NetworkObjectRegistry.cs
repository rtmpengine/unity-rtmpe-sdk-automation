// RTMPE SDK — Runtime/Core/NetworkObjectRegistry.cs
//
// Central registry of all live networked objects.
//
// Design decisions:
//  • All methods are main-thread only (Unity objects must be accessed from
//    the main thread). The lock is retained for defensive safety in case of
//    future async operations, but callers should treat this as single-threaded.
//  • Get() performs a Unity null check (op_Equality override) to detect
//    destroyed GameObjects and auto-evicts them, preventing stale references.
//  • Clear() despawns all objects before clearing so that OnNetworkDespawn()
//    fires and _isSpawned is set to false for every registered object.
//    Despawning happens OUTSIDE the lock to prevent re-entrance deadlocks if
//    an OnNetworkDespawn callback calls registry methods.
//  • GetAll() returns a defensive snapshot (IReadOnlyList) so callers
//    iterating the list can't observe concurrent modifications.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// The live networked objects on this client, by object id. Access it through
    /// <see cref="SpawnManager.Registry"/>.
    /// </summary>
    /// <remarks>
    /// <para>Each object is registered under its first
    /// <see cref="NetworkBehaviour"/>. An object is registered after its
    /// <c>OnNetworkSpawn</c> has run, so <see cref="Get"/> returns
    /// <see langword="null"/> for it inside <c>OnNetworkSpawn</c>. The registry
    /// is cleared when this client leaves a room.</para>
    /// <para>Main thread only.</para>
    /// </remarks>
    public sealed class NetworkObjectRegistry
    {
        private readonly Dictionary<ulong, NetworkBehaviour> _objects =
            new Dictionary<ulong, NetworkBehaviour>();

        private readonly object _lock = new object();

        // Re-entrance guard for the despawn callback.  The Register flow
        // releases the lock before invoking SetSpawned(false) on the evicted
        // entry so an OnNetworkDespawn handler that calls back into the
        // registry does not deadlock.  That same lock release, however,
        // exposes a window where re-entrant Register on the same id would
        // clobber the just-installed entry and then despawn it — the new
        // registration's predecessor is observed inside the inner Register
        // as the just-installed object, and the inner SetSpawned(false)
        // tears it down before the outer caller's SetSpawned(true) lands.
        // Tracking depth via [ThreadStatic] is sufficient because the
        // registry contract is main-thread only; any future cross-thread
        // call would be a separate bug surfaced loudly by Unity's
        // main-thread-only API checks.  Counter form (rather than a bool)
        // tolerates legitimate nesting one level deeper than the current
        // single-frame despawn we expect, without changing semantics.
        [System.ThreadStatic]
        private static int _despawnReentryDepth;

        // ── Mutation ───────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a spawned object under its
        /// <see cref="NetworkBehaviour.NetworkObjectId"/>. Used by
        /// <see cref="SpawnManager"/>; not intended to be called from game code.
        /// </summary>
        /// <remarks>
        /// <para>If a different object is already registered under the same id, it
        /// is replaced: an error is logged and
        /// <see cref="NetworkBehaviour.OnNetworkDespawn"/> runs on each of its
        /// components, but its GameObject is not destroyed.</para>
        /// <para>A call made from an <c>OnNetworkDespawn</c> that the registry is
        /// running is refused with a logged error; register the object on a later
        /// frame instead.</para>
        /// </remarks>
        /// <param name="obj">The object to register.</param>
        /// <returns>
        /// <see langword="true"/> when the object is registered;
        /// <see langword="false"/> when <paramref name="obj"/> is
        /// <see langword="null"/> or the call was refused.
        /// </returns>
        public bool Register(NetworkBehaviour obj) => Register(obj, out _);

        /// <summary>
        /// Registers a spawned object and reports the object it replaced. Used by
        /// <see cref="SpawnManager"/>; not intended to be called from game code.
        /// </summary>
        /// <remarks>
        /// Behaves like <see cref="Register(NetworkBehaviour)"/>.
        /// </remarks>
        /// <param name="obj">The object to register.</param>
        /// <param name="evicted">
        /// The object that held the id before, or <see langword="null"/> when the
        /// id was free, <paramref name="obj"/> already held it, or the call was
        /// refused.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the object is registered;
        /// <see langword="false"/> when <paramref name="obj"/> is
        /// <see langword="null"/> or the call was refused.
        /// </returns>
        public bool Register(NetworkBehaviour obj, out NetworkBehaviour evicted)
        {
            evicted = null;
            if (obj == null) return false;

            // Reject re-entrant registrations issued from within an
            // OnNetworkDespawn handler that the registry itself is currently
            // dispatching.  See the field-level comment on
            // _despawnReentryDepth for the corruption pattern this prevents.
            if (_despawnReentryDepth > 0)
            {
                UnityEngine.Debug.LogError(
                    "[RTMPE] NetworkObjectRegistry.Register: re-entrant call " +
                    $"detected from inside an OnNetworkDespawn callback (objectId " +
                    $"{obj.NetworkObjectId}).  Re-registration during despawn would " +
                    "clobber the outer call's slot and silently despawn the new " +
                    "object.  Defer the registration to the next frame (e.g. via " +
                    "a deferred queue drained from Update).  Rejected.");
                return false;
            }

            NetworkBehaviour previous = null;
            lock (_lock)
            {
                _objects.TryGetValue(obj.NetworkObjectId, out previous);
                _objects[obj.NetworkObjectId] = obj;
            }

            // Despawn the evicted object outside the lock to prevent re-entrance
            // if OnNetworkDespawn calls registry methods.
            // ReferenceEquals guard skips the no-op case of re-registering the same instance.
            if (previous != null && !ReferenceEquals(previous, obj))
            {
                evicted = previous;

                // Surface the collision so operators see SpawnManager
                // bookkeeping divergence rather than discovering it later
                // as a "ghost object" in the scene.
                UnityEngine.Debug.LogError(
                    "[RTMPE] NetworkObjectRegistry.Register: same-id collision " +
                    $"on objectId {obj.NetworkObjectId}.  The previous instance has " +
                    "been despawned and no longer routes; its GameObject remains " +
                    "live and is safe to destroy.  This indicates an upstream " +
                    "id-allocation bug.");
                _despawnReentryDepth++;
                try
                {
                    // Unspawned, not despawned: the eviction flag is a claim on
                    // a live-count this type does not keep, so setting it here
                    // would strand the evicted instance's slot.  The claim
                    // belongs with the return of that count, which is why the
                    // instance travels back to the caller instead.
                    UnspawnObject(previous);
                }
                finally
                {
                    // Decrement in finally so an exception in user code does
                    // not leave the depth counter pinned and break every
                    // subsequent Register call on this thread.
                    _despawnReentryDepth--;
                }
            }

            return true;
        }

        /// <summary>
        /// Removes the entry for <paramref name="objectId"/>, if there is one. Used
        /// by <see cref="SpawnManager"/>; not intended to be called from game code.
        /// </summary>
        /// <param name="objectId">The object's id.</param>
        public void Unregister(ulong objectId)
        {
            lock (_lock)
            {
                _objects.Remove(objectId);
            }
            // Drop hysteresis state so a future spawn that re-uses the id
            // starts hidden, matching the "first contact" semantics of the
            // interest filter.
            Rooms.InterestManager.ForgetObject(objectId);
        }

        // ── Query ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the object registered under <paramref name="objectId"/>, or
        /// <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// An entry whose GameObject was destroyed is removed, and
        /// <see langword="null"/> is returned for it.
        /// </remarks>
        /// <param name="objectId">The object's id.</param>
        public NetworkBehaviour Get(ulong objectId)
        {
            lock (_lock)
            {
                if (!_objects.TryGetValue(objectId, out var obj)) return null;

                // Unity overloads == so that a destroyed UnityEngine.Object
                // compares equal to null even though the C# reference is not null.
                if (obj == null)
                {
                    _objects.Remove(objectId);
                    Rooms.InterestManager.ForgetObject(objectId);
                    return null;
                }

                return obj;
            }
        }

        /// <summary>
        /// Returns a new list of the live objects. Entries whose GameObject was
        /// destroyed are left out.
        /// </summary>
        /// <remarks>
        /// Allocates a list on every call. To avoid that, use
        /// <see cref="GetAllSnapshot"/> with a list you keep.
        /// </remarks>
        public IReadOnlyList<NetworkBehaviour> GetAll()
        {
            lock (_lock)
            {
                // Always return a fresh copy.  The caller may dispatch into
                // arbitrary user code while iterating the result; any
                // shared-buffer optimisation would let a nested or sibling
                // GetAll call mutate the outer walker's view mid-iteration.
                var snapshot = new List<NetworkBehaviour>(_objects.Count);
                foreach (var obj in _objects.Values)
                {
                    // Unity null check: skip destroyed-but-not-unregistered entries.
                    if (obj != null) snapshot.Add(obj);
                }
                return snapshot;
            }
        }

        /// <summary>
        /// Clears <paramref name="destination"/> and fills it with the live
        /// objects, without allocating a list.
        /// </summary>
        /// <param name="destination">The list to fill.</param>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
        public void GetAllSnapshot(IList<NetworkBehaviour> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            lock (_lock)
            {
                foreach (var obj in _objects.Values)
                {
                    if (obj != null) destination.Add(obj);
                }
            }
        }

        /// <summary>
        /// Removes every entry whose GameObject Unity has destroyed, for example
        /// by a scene load, and returns how many were removed.
        /// </summary>
        /// <remarks>
        /// <see cref="NetworkBehaviour.OnNetworkDespawn"/> does not run for the
        /// removed objects; to have it run, despawn them with
        /// <see cref="SpawnManager.Despawn"/> before they are destroyed. The SDK
        /// calls this method after scene loads.
        /// </remarks>
        /// <returns>The number of entries removed.</returns>
        public int PruneDestroyed()
        {
            List<ulong> staleIds = null;
            lock (_lock)
            {
                // Single pass: collect keys whose GameObject has been Unity-
                // destroyed.  Mutating a Dictionary while iterating throws
                // InvalidOperationException, so we accumulate into a scratch
                // list first and remove afterwards.
                foreach (var kv in _objects)
                {
                    // Unity's overloaded == compares destroyed UnityEngine.Object
                    // to null even when the managed reference is still live.
                    if (kv.Value == null)
                    {
                        (staleIds ??= new List<ulong>()).Add(kv.Key);
                    }
                }

                if (staleIds == null) return 0;

                foreach (var id in staleIds)
                {
                    _objects.Remove(id);
                    Rooms.InterestManager.ForgetObject(id);
                }
                return staleIds.Count;
            }
        }

        /// <summary>
        /// Removes every object. Used by <see cref="SpawnManager"/> when leaving a
        /// room; not intended to be called from game code.
        /// </summary>
        /// <remarks>
        /// <see cref="NetworkBehaviour.OnNetworkDespawn"/> runs on each component
        /// of every live object, and <see cref="NetworkBehaviour.IsSpawned"/>
        /// becomes <see langword="false"/>. The GameObjects are not destroyed. An
        /// exception from one object's <c>OnNetworkDespawn</c> is logged and the
        /// others still run.
        /// </remarks>
        public void Clear()
        {
            List<NetworkBehaviour> snapshot;
            List<ulong> ids;
            lock (_lock)
            {
                snapshot = new List<NetworkBehaviour>(_objects.Values);
                ids      = new List<ulong>(_objects.Keys);
                _objects.Clear();
            }

            // S4-50 — the interest filter is told, as it is on all three of the
            // other removal paths.  It was not told here, so the receive
            // filter's visible set outlived the room it was built in: every id
            // this registry ever held stayed "visible" into the next room,
            // where those ids belong to different objects or to none.
            //
            // Ahead of the despawn callbacks below, and outside the lock they
            // were taken under: a handler that spawns during teardown must not
            // find its own fresh object already forgotten.
            for (int i = 0; i < ids.Count; i++)
                Rooms.InterestManager.ForgetObject(ids[i]);

            // Call despawn callbacks outside the lock under the same
            // re-entrance guard that Register's eviction path uses, so a
            // user OnNetworkDespawn handler that calls Register from
            // inside Clear() is rejected with the same diagnostic instead
            // of partially repopulating the just-cleared registry.
            _despawnReentryDepth++;
            try
            {
                foreach (var obj in snapshot)
                {
                    // Unity null check: skip already-destroyed GameObjects.
                    if (obj == null) continue;

                    // Isolate per-object despawn: an exception in one
                    // object's OnNetworkDespawn callback must not prevent
                    // others from being despawned.
                    try   { UnspawnObject(obj); }
                    catch (Exception ex)
                    {
                        // One line per COMPONENT on a room teardown, and a
                        // teardown despawns every object at once — the identical
                        // condition SpawnManager.LogDespawnFault was gated for
                        // (`RPC-RD-05`).  A LogException is the severity every
                        // crash reporter a host application installs ingests, so
                        // ungated it is a burst of them for one fault.
                        if (WarnGate.ShouldEmit(ref s_lastTeardownDespawnFaultWarnTicks))
                            Debug.LogException(ex);
                    }
                }
            }
            finally
            {
                // Decrement in finally so an unhandled exception that
                // escapes the inner try/catch (e.g. OutOfMemoryException)
                // does not pin the depth counter.
                _despawnReentryDepth--;
            }
        }

        // Reported the same way the per-object catch below reports, because it
        // is the same fault at a finer grain: user code in OnNetworkDespawn.
        // 🚨 A field EACH, and the first version shared one.  They report the
        // same kind of fault at two grains — a whole object failing to unspawn,
        // and one component's OnNetworkDespawn throwing — but a shared budget
        // means a flood of one decides whether the other is ever seen, which is
        // what `NoGateIsSharedBetweenTwoMembers` refuses and is right to.
        // The whole-object unspawn fault inside Clear's teardown loop.
        private static long s_lastTeardownDespawnFaultWarnTicks;

        private static long s_lastComponentDespawnFaultWarnTicks;

        private static readonly Action<INbLifecycle, Exception> ReportDespawnFault =
            (component, ex) =>
            {
                if (WarnGate.ShouldEmit(ref s_lastComponentDespawnFaultWarnTicks))
                    Debug.LogException(ex);
            };

        /// <summary>
        /// Unspawn the object <paramref name="anchor"/> stands for — every
        /// <see cref="NetworkBehaviour"/> on it, not the registered entry alone.
        /// </summary>
        /// <remarks>
        /// A registry entry is a routing slot, one per object, while
        /// <c>OnNetworkDespawn</c> and the spawn-scoped release it drives are
        /// owed to each component that took <c>OnNetworkSpawn</c>.  Left at the
        /// anchor, a sibling keeps <c>IsSpawned</c> true through a teardown and
        /// re-runs its spawn callback on the next acquire against registrations
        /// it never released.
        /// <para>Faults are isolated per component for the reason
        /// <see cref="SpawnLifecycleOps.UnspawnAll"/> states: this method's
        /// callers isolate per object, and a component skipped inside one of
        /// them stays spawned and never spawns again.</para>
        /// <para>Eviction is deliberately not claimed here: these objects'
        /// <c>GameObject</c>s outlive the call, and the counter reconciliation
        /// their eventual destroy performs belongs to whoever destroys them.</para>
        /// </remarks>
        private static void UnspawnObject(NetworkBehaviour anchor)
        {
            SpawnLifecycleOps.UnspawnAll(anchor.ObjectComponents, ReportDespawnFault);
        }
    }
}
