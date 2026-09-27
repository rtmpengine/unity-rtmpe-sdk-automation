// RTMPE SDK — Runtime/Core/INetworkObjectPool.cs
//
// Pluggable object-pool contract used by SpawnManager for every network
// spawn/despawn.  Pooling is OPTIONAL — when no pool is installed,
// SpawnManager falls back to Object.Instantiate / Object.Destroy (the
// historical behaviour).  Apps with high spawn churn (bullets, hit FX,
// short-lived props) install a pool to avoid per-spawn GC pressure and
// the Instantiate/Destroy cost.
//
// Contract:
//  • Acquire must return a fully-configured GameObject with the same
//    NetworkBehaviour component the prefab has.  It MUST NOT return null
//    on success; instead, throw or return a fresh Instantiate if the
//    pool is empty.  SpawnManager treats a null return as a fatal error.
//  • Release is called instead of Object.Destroy when an object despawns.
//    Implementations SHOULD deactivate the GameObject and keep it for
//    later reuse.  Implementations MAY choose to Destroy rarely-used
//    prefabs to cap pool memory; that decision is purely internal.
//  • All calls happen on the Unity main thread.  Implementations do NOT
//    need to be thread-safe.
//
// Why an interface rather than a concrete class:
//  Different games want wildly different pooling strategies (global pool,
//  per-scene pool, LRU-capped, warm-up on scene load, etc.).  The SDK
//  intentionally ships no built-in pool so we don't paint consumers into
//  a specific design.  Users with no pooling needs pay zero overhead.

using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// A pool that <see cref="SpawnManager"/> uses instead of <c>Instantiate</c>
    /// and <c>Destroy</c> for networked objects.
    /// </summary>
    /// <remarks>
    /// <para>Install it with <see cref="SpawnManager.SetObjectPool"/>. Without a
    /// pool, the SDK instantiates and destroys objects itself. The SDK does not
    /// include a pool implementation.</para>
    /// <para>The SDK calls both methods on the main thread, so an implementation
    /// does not need to be thread-safe.</para>
    /// </remarks>
    public interface INetworkObjectPool
    {
        /// <summary>
        /// Returns an instance of <paramref name="prefab"/> for a spawn.
        /// </summary>
        /// <remarks>
        /// The SDK sets the instance's position and rotation and activates it. Do
        /// not return <see langword="null"/>: the SDK logs an error and falls back
        /// to <c>Instantiate</c>.
        /// </remarks>
        /// <param name="prefabId">The id the prefab is registered under.</param>
        /// <param name="prefab">The prefab to return an instance of; instantiate it when the pool has none.</param>
        /// <param name="position">The world-space position the object spawns at.</param>
        /// <param name="rotation">The world-space rotation the object spawns with.</param>
        /// <returns>An instance of <paramref name="prefab"/>; never <see langword="null"/>.</returns>
        GameObject Acquire(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation);

        /// <summary>
        /// Takes back an instance when its object despawns. Typically, deactivate
        /// it and keep it for reuse.
        /// </summary>
        /// <remarks>
        /// <para>Before this call, every NetworkVariable on the instance that was
        /// created outside <c>OnNetworkSpawn</c> (in a field initialiser,
        /// <c>Awake</c> or <c>OnEnable</c>) is reset to the value its constructor
        /// set, and its change event fires, so the next spawn starts from the
        /// prefab's values.</para>
        /// <para>If this method throws during a single despawn, the SDK logs the
        /// exception and destroys the instance. When leaving a room or
        /// disconnecting, it only logs the exception.</para>
        /// </remarks>
        /// <param name="prefabId">
        /// The id the instance was spawned under, or <see cref="uint.MaxValue"/>
        /// when the SDK does not know it; destroy such an instance.
        /// </param>
        /// <param name="instance">The instance to take back.</param>
        void Release(uint prefabId, GameObject instance);
    }
}
