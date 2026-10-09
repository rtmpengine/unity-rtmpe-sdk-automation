// RTMPE SDK — Runtime/Core/ObjectDispatchOps.cs
//
// Per-frame dispatch across the NetworkBehaviour components of one networked
// object. The registry keeps a single anchor per object because inbound routing
// needs one entry, but the operations that entry stands for are object-wide:
// the fixed-cadence tick, the 30 Hz NetworkVariable flush and an inbound
// variable update all belong to whichever component declared the state, which
// is not in general the anchor. Same rule as SpawnLifecycleOps, applied to the
// per-frame half of the object's life, and expressed over a seam rather than
// the Unity-only NetworkBehaviour so it can be exercised in isolation.
//
// The wire carries [object_id][variable_id] and no component discriminator, so
// a variable id is routable only while exactly one component on the object
// claims it: ids are unique across the object, not within a component.

using System;
using System.Collections.Generic;
using System.IO;

namespace RTMPE.Core
{
    /// <summary>
    /// The per-frame dispatch surface of a networked component, abstracted from
    /// the Unity-only <c>NetworkBehaviour</c> so the fan-out can run without a
    /// Unity runtime. <c>NetworkBehaviour</c> implements it.
    /// </summary>
    internal interface INbDispatch
    {
        bool IsOwner { get; }
        bool IsSpawned { get; }

        /// <summary>
        /// Whether the component still exists. Asked of the component rather
        /// than tested here, because a Unity object reached through an
        /// interface answers a plain <c>== null</c> with the reference it still
        /// holds — the destroyed-object equality is declared on
        /// <c>UnityEngine.Object</c> and does not apply at this static type.
        /// </summary>
        bool IsAlive { get; }

        /// <summary>One simulated tick's worth of owner-side work.</summary>
        void FixedTick(float deltaTime);

        /// <summary>
        /// Re-flag every variable this component holds so the next flush
        /// carries the whole of its state, not the part that has changed
        /// since — what a late joiner needs and a delta cannot give.
        /// </summary>
        void MarkAllVariablesDirty();

        /// <summary>
        /// Emit this component's eligible dirty variables through
        /// <paramref name="sendPayload"/>, or nothing when it has none.  The
        /// sender answers whether it took the payload: <see langword="false"/>
        /// means the replication share of the reliable window was in flight
        /// and nothing was sent, and the component keeps those variables
        /// dirty for the next tick.
        /// </summary>
        void FlushDirtyVariables(Func<byte[], int, bool> sendPayload);

        /// <summary>Whether this component holds <paramref name="variableId"/>.</summary>
        bool TracksVariable(uint variableId);

        /// <summary>
        /// Apply an inbound value to <paramref name="variableId"/>. Returns
        /// whether this component holds that id — <see langword="true"/> even
        /// when the value is dropped as stale, because the id was resolved and
        /// the delta is spent.
        /// </summary>
        bool TryApplyVariableUpdate(
            uint variableId, BinaryReader reader, uint packetTick, bool hasPacketTick);
    }

    internal static class ObjectDispatchOps
    {
        /// <summary>
        /// Run one simulated tick on every owned, spawned component. A fault in
        /// one component is reported through <paramref name="onFault"/> and the
        /// remaining components still run; with no reporter the fault
        /// propagates, because a per-tick exception that is neither surfaced nor
        /// thrown is a component that has silently stopped simulating.
        /// </summary>
        internal static void FixedTickAll(
            IReadOnlyList<INbDispatch> components, float deltaTime, Action<INbDispatch, Exception> onFault)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || !c.IsAlive || !c.IsOwner || !c.IsSpawned) continue;
                try
                {
                    c.FixedTick(deltaTime);
                }
                catch (Exception ex)
                {
                    if (onFault == null) throw;
                    onFault(c, ex);
                }
            }
        }

        /// <summary>
        /// Offer the flush to every component. Each one decides whether it owes
        /// anything this tick; a component holding no variables costs a call and
        /// no payload.
        /// </summary>
        internal static void FlushAll(
            IReadOnlyList<INbDispatch> components,
            Func<byte[], int, bool> sendPayload,
            Action<INbDispatch, Exception> onFault = null)
            => FlushAll(components, 0, sendPayload, onFault);

        /// <summary>
        /// The same offer, begun at component <paramref name="startIndex"/>
        /// (modulo the count) and wrapping round.  The caller advances it once
        /// per turn of its object walk (see <c>FlushWalkCursor</c>): a share of
        /// the reliable window that frees fewer slots per tick
        /// than an object has dirty components would otherwise be spent on the
        /// same first components every tick, and the last components of every
        /// object would stand still on their replicas for as long as the link
        /// stayed slow — the rotation over objects reaches nothing inside one.
        /// </summary>
        internal static void FlushAll(
            IReadOnlyList<INbDispatch> components,
            int startIndex,
            Func<byte[], int, bool> sendPayload,
            Action<INbDispatch, Exception> onFault = null)
        {
            if (components == null) return;
            int count = components.Count;
            if (count == 0) return;
            int first = startIndex % count;
            if (first < 0) first += count;
            for (int k = 0; k < count; k++)
            {
                int i = first + k;
                if (i >= count) i -= count;
                var c = components[i];
                if (c == null || !c.IsAlive || !c.IsOwner || !c.IsSpawned) continue;

                // The same isolation FixedTickAll has had, and for a sharper
                // reason.  A serializer that throws is not a component that
                // stops: nothing in the chain below catches — TryAppend,
                // FlushDirtyVariables and this loop all pass it through — so it
                // aborts the flush for every component AFTER this one, and the
                // dirty flags it left set bring it back on the next tick, and
                // the next.  One malformed value silently ends outbound
                // replication for the whole object, permanently.
                try
                {
                    c.FlushDirtyVariables(sendPayload);
                }
                catch (Exception ex)
                {
                    // ⚠️ Re-dirtied, because otherwise the tick's data could be
                    // lost.  FlushDirtyVariables marks a variable clean only
                    // once the sender has taken the payload, so a throw from a
                    // serializer or from the send path leaves what it appended
                    // dirty — but that ordering is the flush's own invariant,
                    // and the fault path is the one place that must not depend
                    // on it: a component that reaches here is one whose flush
                    // just did something unforeseen, and the whole of its state
                    // is re-flagged so the next tick carries it regardless.
                    // Without this the fault report would be claiming a retry
                    // that nothing was going to perform.
                    try { c.MarkAllVariablesDirty(); }
                    catch { /* a component that cannot even re-dirty is past
                               helping; the fault below is the report that
                               matters, and it must not be lost to a second
                               throw. */ }

                    if (onFault == null) throw;
                    onFault(c, ex);
                }
            }
        }

        /// <summary>
        /// Re-flag every owned, spawned component's variables so the next flush
        /// carries whole state. A late joiner has no prior value to apply a
        /// delta to, so a variable that has not changed since it joined is one
        /// it never receives at all.
        /// </summary>
        internal static void MarkAllVariablesDirtyAll(IReadOnlyList<INbDispatch> components)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || !c.IsAlive || !c.IsOwner || !c.IsSpawned) continue;
                c.MarkAllVariablesDirty();
            }
        }

        /// <summary>
        /// Deliver one inbound variable value to the component holding
        /// <paramref name="variableId"/>. Returns whether any component did —
        /// <see langword="false"/> means the id belongs to nothing on this
        /// object, which is the caller's cue to report it.
        /// </summary>
        /// <remarks>
        /// Delivery stops at the first holder. A second component holding the
        /// same id is not reachable and cannot be, which is why registration
        /// refuses to create one — see <see cref="FindVariableIdClaimant"/>.
        /// </remarks>
        internal static bool RouteVariableUpdate(
            IReadOnlyList<INbDispatch> components,
            uint         variableId,
            BinaryReader reader,
            uint         packetTick,
            bool         hasPacketTick)
        {
            if (components == null) return false;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || !c.IsAlive) continue;
                if (c.TryApplyVariableUpdate(variableId, reader, packetTick, hasPacketTick))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The component other than <paramref name="claimant"/> already holding
        /// <paramref name="variableId"/> on this object, or <see langword="null"/>
        /// when the id is free.
        /// </summary>
        /// <remarks>
        /// The incumbent wins, and the incumbent is whoever registered first —
        /// which is component order only while every component registers from
        /// <c>OnNetworkSpawn</c>, the order the spawn fan-out imposes.  A
        /// variable constructed from <c>Awake</c> or <c>Start</c> registers
        /// outside that order and can take an id from a component ahead of it.
        /// <para>The binding is per peer, so it is only agreed across a room
        /// while every peer registers the same ids: a construction guarded by
        /// <c>IsOwner</c> claims an id on the owner and leaves it free
        /// elsewhere, and the two ends then read one id as two different
        /// variables.  Registration must be unconditional.</para>
        /// </remarks>
        internal static INbDispatch FindVariableIdClaimant(
            IReadOnlyList<INbDispatch> components, INbDispatch claimant, uint variableId)
        {
            if (components == null) return null;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || !c.IsAlive || ReferenceEquals(c, claimant)) continue;
                if (c.TracksVariable(variableId)) return c;
            }
            return null;
        }
    }
}
