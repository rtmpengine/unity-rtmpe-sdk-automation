// RTMPE SDK — Runtime/Core/SpawnLifecycleOps.cs
//
// Object-wide lifecycle fan-out over the NetworkBehaviour components of one
// networked object. A networked object carries one NetworkBehaviour per script,
// and always at least two — NetworkTransform is itself a NetworkBehaviour and is
// required on any synced object — so spawn, despawn and ownership must drive
// EVERY component, not only the one chosen as the registry/routing anchor.
// The fan-out is expressed over the INbLifecycle seam rather than the
// Unity-only NetworkBehaviour so it can be exercised in isolation.

using System;
using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// The spawn-lifecycle surface of a networked component, abstracted from the
    /// Unity-only <c>NetworkBehaviour</c> so the fan-out can run without a Unity
    /// runtime. <c>NetworkBehaviour</c> implements it.
    /// </summary>
    internal interface INbLifecycle
    {
        bool IsSpawned { get; }

        /// <summary>
        /// Whether the component still exists.  Asked of the component rather
        /// than tested here: a Unity object reached through an interface answers
        /// a plain <c>== null</c> with the reference it still holds, because the
        /// destroyed-object equality is declared on <c>UnityEngine.Object</c>
        /// and does not apply at this static type.
        /// </summary>
        bool IsAlive { get; }
        void Initialize(ulong objectId, string ownerPlayerId);
        void SetSpawned(bool spawned);
        void MarkExternallyEvicted();

        /// <summary>
        /// Record the object's new owner without announcing it. Returns whether
        /// the value changed, and so whether a notification is owed.
        /// </summary>
        bool AssignOwner(string ownerPlayerId);

        /// <summary>Announce an owner change <c>AssignOwner</c> left owed.</summary>
        void RaiseOwnershipChanged();
    }

    internal static class SpawnLifecycleOps
    {
        /// <summary>
        /// Initialise every not-yet-spawned component with the object's id and
        /// owner — the value <c>IsOwner</c> compares. A null entry (missing
        /// script) or an already-spawned one (pool reuse / duplicate spawn) is
        /// skipped so initialisation never re-runs on a live component.
        /// </summary>
        internal static void InitializeAll(
            IReadOnlyList<INbLifecycle> components, ulong objectId, string ownerPlayerId)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || !c.IsAlive || c.IsSpawned) continue;
                c.Initialize(objectId, ownerPlayerId);
            }
        }

        /// <summary>
        /// Mark every component spawned, firing each one's <c>OnNetworkSpawn</c>.
        /// <c>SetSpawned(true)</c> is idempotent, so an already-spawned component
        /// is a no-op.
        /// </summary>
        internal static void SpawnAll(IReadOnlyList<INbLifecycle> components)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c != null && c.IsAlive) c.SetSpawned(true);
            }
        }

        /// <summary>
        /// Hand every component the object's new owner, then let each announce
        /// it. A component already holding <paramref name="ownerPlayerId"/> is
        /// left alone and announces nothing.
        /// </summary>
        /// <remarks>
        /// The owner is what <c>IsOwner</c> compares, and every component
        /// consults its own copy: <c>NetworkTransform</c> and
        /// <c>NetworkRigidbody</c> refuse to broadcast unless they are the
        /// owner, and a user script on any component reads its own
        /// <c>IsOwner</c> and takes its own <c>OnOwnershipChanged</c>.  A
        /// handover applied to the anchor alone therefore moves the routing
        /// slot while leaving the components that actually drive the object
        /// answering for the previous owner.
        /// <para>Two passes, for the reason the spawn path takes two: the
        /// second pass is user code, and a handler that reaches a sibling has
        /// to find it already carrying the new owner rather than the one being
        /// replaced.  It also means no announcement can leave the object half
        /// transferred — every component holds the new owner before the first
        /// handler runs, whatever that handler does.</para>
        /// <para>Only the second pass is isolated, and the asymmetry is the
        /// point.  <c>RaiseOwnershipChanged</c> runs the application's code,
        /// and a handler that throws must not decide whether the components
        /// after it are told — a component never told keeps its announcement
        /// owed, so the next handover reports a previous owner two transfers
        /// stale.  With no reporter the fault propagates rather than
        /// disappearing, exactly as in <see cref="UnspawnAll"/>.</para>
        /// <para><c>AssignOwner</c> is the SDK's own bookkeeping and is left
        /// unisolated deliberately, but ⛔ that is not the same as saying a
        /// throw there is contained: it leaves this method through whatever
        /// called it, and the caller that matters —
        /// <c>OwnershipManager.ReassignObjectsToNewOwner</c> — reports it and
        /// moves to the next object, so the object it happened on is left
        /// split between two owners with its announcements never made.  That
        /// is the correct disposition for a defect in the SDK's own write
        /// path: it is loud, it is bounded to one object, and it does not let
        /// one broken object end a host migration.  What it is not is
        /// harmless, which is why the write pass must not be able to throw.</para>
        /// <para>The owner also reaches a component at spawn, through
        /// <c>InitializeAll</c>.  Between them they cover every event that
        /// moves the object's side of the comparison; the local player's side
        /// is re-read at <c>SetSpawned(true)</c>, which no handover
        /// accompanies.</para>
        /// </remarks>
        internal static void SetOwnerAll(
            IReadOnlyList<INbLifecycle> components, string ownerPlayerId,
            Action<INbLifecycle, Exception> onFault = null)
        {
            if (components == null) return;

            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c != null && c.IsAlive) c.AssignOwner(ownerPlayerId);
            }

            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || !c.IsAlive) continue;
                try
                {
                    c.RaiseOwnershipChanged();
                }
                catch (Exception ex)
                {
                    if (onFault == null) throw;
                    onFault(c, ex);
                }
            }
        }

        /// <summary>
        /// Tear down every component: flag them all evicted FIRST — so each
        /// component's imminent Unity <c>OnDestroy</c> is recognised as part of
        /// this teardown and cannot double-decrement the live-spawn counter —
        /// then unspawn them all, firing each <c>OnNetworkDespawn</c>.
        /// </summary>
        internal static void DespawnAll(
            IReadOnlyList<INbLifecycle> components, Action<INbLifecycle, Exception> onFault = null)
        {
            MarkEvictedAll(components);
            UnspawnAll(components, onFault);
        }

        /// <summary>
        /// Claim teardown ownership of every component, so each one's imminent
        /// Unity <c>OnDestroy</c> skips the SpawnManager reconciliation the
        /// caller is performing itself.
        /// </summary>
        internal static void MarkEvictedAll(IReadOnlyList<INbLifecycle> components)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c != null && c.IsAlive) c.MarkExternallyEvicted();
            }
        }

        /// <summary>
        /// Unspawn every component, firing each one's <c>OnNetworkDespawn</c>
        /// and running each one's spawn-scoped release.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="DespawnAll"/> because eviction marking is a
        /// claim on the counter reconciliation, not part of unspawning: a caller
        /// that unspawns an object whose <c>GameObject</c> stays live must not
        /// make that claim, or the eventual destroy finds the latch already set
        /// and leaks the object's slot in the live-spawn count.
        /// <para>Isolated per component, because <c>OnNetworkDespawn</c> is user
        /// code and a component that misses its unspawn is not merely missing a
        /// callback: it keeps <c>IsSpawned</c> true, which makes
        /// <see cref="InitializeAll"/> skip it on the next acquire, so it never
        /// spawns again and never releases the registrations of the life it is
        /// still holding open.  With no reporter the fault propagates rather
        /// than disappearing.</para>
        /// </remarks>
        internal static void UnspawnAll(
            IReadOnlyList<INbLifecycle> components, Action<INbLifecycle, Exception> onFault = null)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || !c.IsAlive) continue;
                try
                {
                    c.SetSpawned(false);
                }
                catch (Exception ex)
                {
                    if (onFault == null) throw;
                    onFault(c, ex);
                }
            }
        }
    }
}
