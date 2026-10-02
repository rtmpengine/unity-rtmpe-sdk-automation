// RTMPE SDK — Runtime/Core/NetworkBehaviour.cs
//
// Base class for all networked GameObjects in RTMPE.
//
// Design decisions:
//  • _ownerPlayerId is a string (UUID) to match PlayerInfo.PlayerId and the
//    room service player identifiers.  The gateway session ID (u64) is a
//    DIFFERENT concept stored in NetworkManager.LocalPlayerId.
//  • IsOwner compares string UUIDs and short-circuits on null/empty so that
//    uninitialized objects never falsely claim ownership (unlike a ulong==0
//    comparison which would return true for every uninitialized object).
//  • Initialize / SetSpawned / SetOwner are internal so only the RTMPE SDK
//    itself (SpawnManager) can mutate network object state.
//    RTMPE.SDK.Tests can also call them via InternalsVisibleTo (AssemblyInfo.cs).
//  • DestroyWithOwner is declared here; enforcement is implemented by
//    SpawnManager when it handles PlayerLeft events.
//  • IsOwner accesses NetworkManager.Instance — safe for main-thread MonoBehaviour
//    code (OnNetworkSpawn, OnOwnershipChanged, etc. all run on main thread).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using RTMPE.Rpc;
using RTMPE.Sync;

namespace RTMPE.Core
{
    /// <summary>
    /// The base class for scripts on networked objects. Derive from it instead of
    /// <c>MonoBehaviour</c> for any script that owns networked state or RPCs.
    /// </summary>
    /// <remarks>
    /// Objects are spawned from registered prefabs with <see cref="SpawnManager.Spawn"/>. One
    /// object may carry several <c>NetworkBehaviour</c> components; they share the object's id
    /// and owner. Override the lifecycle methods as <c>protected override</c>.
    /// </remarks>
    public abstract class NetworkBehaviour : MonoBehaviour, INbLifecycle, INbDispatch
    {
        // ── State ──────────────────────────────────────────────────────────────

        private ulong  _networkObjectId;
        private string _ownerPlayerId = string.Empty;
        private bool   _isSpawned;

        // The owners this object had here before the current one that a copy
        // the room has not yet re-keyed can still name: the owner it was
        // spawned under, and the one it had just before the current owner.
        // Read when this client ends the object itself
        // (SpawnManager.RememberEndedHere).
        private string _spawnOwnerPlayerId    = string.Empty;
        private string _previousOwnerPlayerId = string.Empty;

        // The owner as last announced to user code, and whether an
        // announcement is outstanding.  A handover writes the owner on every
        // component of the object before any of them announces, so the two
        // halves are separated in time and the earlier value has to be kept.
        private string _announcedOwnerPlayerId = string.Empty;
        private bool   _ownershipChangePending;

        // Captured at spawn so IsOwner remains correct after the
        // NetworkManager singleton has been torn down (OnApplicationQuit,
        // domain reload, scene unload).  Without this snapshot OnDestroy of a
        // spawned prefab cannot distinguish owner-only cleanup paths from
        // remote-replica paths once Instance returns null, and the owning
        // client leaks any native resources owned by the IsOwner branch.
        // Refreshed by SetSpawned(true) on every spawn transition so a
        // reconnect that produces a new local player id picks up the latest
        // value before user code sees IsOwner.
        private string _cachedLocalPlayerId = string.Empty;

        // Inbound values delivered to this component's variables during the
        // current spawn — one component's share of the object-wide
        // InboundValuesDelivered, where the variables themselves keep only
        // their last tick.  Per spawn, like the owner and the id: a pooled
        // instance re-acquired starts at zero.
        private long _inboundValuesDelivered;

        // Of those, the ones delivered since this component's owner last
        // changed: what the copy has heard under the owner it names now.  A copy
        // handed to a new owner arrives carrying the previous owner's values,
        // and the world object's stand-aside asks about the owner it has.
        private long _inboundValuesUnderCurrentOwner;

        // Every NetworkVariable currently registered with this behaviour, in
        // registration order.  Populated by NetworkVariableBase constructors via
        // TrackVariable().  Flushed at 30 Hz by NetworkManager for owner clients.
        // Entries within the span below last only for the current spawn; the
        // rest last as long as the C# object.
        private readonly List<NetworkVariableBase> _trackedVariables =
            new List<NetworkVariableBase>();

        // Where OnNetworkSpawn's own registrations begin in the list, and how
        // many of them there are.  See SpawnScopedRegistrations.
        private int _spawnScopedVariableMark;
        private int _spawnScopedVariableCount;

        // Emission gate for the unknown-variableId diagnostic, shared by every
        // behaviour because the condition is a property of the sender rather
        // than of the object that happened to receive it.
        private static long _lastUnknownVariableIdWarnTicks;

        // Emission gate for the duplicate-id refusal.  Shared for the same
        // reason: the condition belongs to a prefab, and a prefab spawned at
        // rate reports once per spawn otherwise — including on every pooled
        // re-acquire, which re-runs OnNetworkSpawn and nothing else.
        private static long _lastVariableIdConflictWarnTicks;

        // ── Emission gates for the inbound RPC dispatch ────────────────────────
        //
        // `DispatchEnhancedRpc` runs once per inbound RPC packet and refuses on
        // eight distinct grounds, five of them at LogError — the severity every
        // crash reporter a host application has installed ingests regardless of
        // what it means. A peer needs only a valid object id and a method id
        // this build does not carry to reach the second of them, so the rate
        // and the bill were both the sender's.
        //
        // Static, as the two gates above are and for their reason: one packet
        // names one object, but a flood names as many as the room holds, and a
        // per-instance budget would bound none of it.
        private static long _lastRpcToUnspawnedWarnTicks;
        private static long _lastUnknownRpcMethodWarnTicks;
        private static long _lastRpcAudienceRefusedWarnTicks;
        private static long _lastRpcCallerRefusedWarnTicks;
        private static long _lastRpcArgCountWarnTicks;
        private static long _lastRpcNullArgWarnTicks;
        private static long _lastRpcArgTypeWarnTicks;
        private static long _lastRpcMethodThrewWarnTicks;
        private static long _lastRpcDispatchErrorWarnTicks;

        // Both reached once per spawn packet: a duplicate Spawn the reliable
        // transport retransmitted, and a property getter that throws while the
        // attribute scan reads it.
        private static long _lastDoubleInitializeWarnTicks;
        private static long _lastAttributeScanThrowWarnTicks;

        // Per-type cache of (field/property → NetworkVariableAttribute) maps.
        // Built lazily once per concrete NetworkBehaviour subclass on first
        // TrackVariable() invocation; reused for every subsequent spawn of the
        // same type so the reflection scan is amortised across the whole
        // application lifetime.  HashSet<Type> reads are lock-free under the
        // main-thread invariant, and Dictionary<Type, …> follows the same
        // single-thread access pattern.
        private static readonly Dictionary<Type, IReadOnlyList<NetworkVariableMetadata>>
            _variableMetadataCache =
                new Dictionary<Type, IReadOnlyList<NetworkVariableMetadata>>();

        // Cached reflection result for a single field or property carrying a
        // NetworkVariableAttribute.  Stored once per declaring type and matched
        // to instance variables by reading the field/property value.
        private readonly struct NetworkVariableMetadata
        {
            public readonly FieldInfo    Field;     // null when the source is a property
            public readonly PropertyInfo Property;  // null when the source is a field
            public readonly float        SendRateHz;

            public NetworkVariableMetadata(FieldInfo field, float sendRateHz)
            {
                Field      = field;
                Property   = null;
                SendRateHz = sendRateHz;
            }

            public NetworkVariableMetadata(PropertyInfo property, float sendRateHz)
            {
                Field      = null;
                Property   = property;
                SendRateHz = sendRateHz;
            }

            public object ReadValue(object instance) =>
                Field != null ? Field.GetValue(instance) : Property.GetValue(instance);
        }

        // RPC-collision validation cache.  Each concrete NetworkBehaviour subclass
        // is checked exactly once on first spawn; subsequent spawns of the same
        // type skip the reflection scan.  HashSet<Type> reads are lock-free under
        // the main-thread invariant (all spawns happen on the Unity main thread).
        private static readonly HashSet<Type> _validatedTypes = new HashSet<Type>();

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetValidatedTypes()
        {
            _validatedTypes.Clear();
            _variableMetadataCache.Clear();
        }

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>
        /// The object's id, the same on every client. The client that spawns the object chooses
        /// it from its own id range. <c>0</c> before the object is spawned.
        /// </summary>
        public ulong NetworkObjectId => _networkObjectId;

        /// <summary>
        /// The owner's player id: the <see cref="RTMPE.Rooms.PlayerInfo.PlayerId"/> of the player
        /// who owns this object. <c>""</c> before the object is spawned.
        /// </summary>
        public string OwnerPlayerId => _ownerPlayerId;

        /// <summary>
        /// The owner this object was spawned under on this client.
        /// </summary>
        internal string SpawnOwnerPlayerId => _spawnOwnerPlayerId;

        /// <summary>
        /// The owner this object had on this client just before
        /// <see cref="OwnerPlayerId"/>; empty until its ownership first changes.
        /// </summary>
        internal string PreviousOwnerPlayerId => _previousOwnerPlayerId;

        /// <summary>
        /// Whether this client owns the object. Guard input handling and NetworkVariable writes
        /// with it.
        /// </summary>
        /// <remarks>
        /// <see langword="false"/> for an object with no owner. This client's player id
        /// (<see cref="NetworkManager.LocalPlayerStringId"/>) is captured when the object spawns,
        /// so <c>IsOwner</c> keeps its answer in <c>OnDestroy</c> after the
        /// <see cref="NetworkManager"/> is gone.
        /// </remarks>
        public bool IsOwner
        {
            get
            {
                if (string.IsNullOrEmpty(_ownerPlayerId)) return false;
                // Read the cached id captured at spawn rather than reaching
                // through NetworkManager.Instance: this is a hot-path property
                // (sampled per-frame by NetworkVariable flush, NetworkTransform,
                // user code) and it must remain authoritative after Instance
                // has been torn down so OnDestroy can run owner-only cleanup.
                if (string.IsNullOrEmpty(_cachedLocalPlayerId)) return false;
                return _ownerPlayerId == _cachedLocalPlayerId;
            }
        }

        /// <summary>
        /// True when this object has an owner and that owner is somebody other
        /// than the local player.
        /// </summary>
        /// <remarks>
        /// 🔑 Not the negation of <see cref="IsOwner"/>, and the difference is
        /// the whole of it. <c>IsOwner</c> is false for two unrelated states: an
        /// object owned by another player, and an object that has **no owner at
        /// all** — one spawned before this client was told its seat, offline, or
        /// between rooms. The first is somebody else's; the second is nobody's,
        /// which in practice means purely local.
        ///
        /// <para>A rule that refuses writes to what this client does not own
        /// must ask this question and not that one: the flush skips both, but
        /// only the first is a value another client already holds a different
        /// version of. Refusing the second takes a usable local object away
        /// from a caller who has one.</para>
        /// </remarks>
        internal bool IsOwnedByAnotherPlayer
            => !string.IsNullOrEmpty(_ownerPlayerId) && !IsOwner;

        /// <summary>
        /// How many inbound variable values have been DELIVERED to this OBJECT
        /// — every component on its GameObject, this one included — since its
        /// components were last initialised: values the wire carried, resolved
        /// to one of the object's variables and passed to it past that
        /// variable's tick watermark.  A value dropped as stale is not counted:
        /// it resolved, but nothing was handed it.
        /// </summary>
        /// <remarks>
        /// Delivered, not applied: what the variable then did with the value —
        /// stored it, found it equal to the one held, or refused it as
        /// unsendable — is the variable's, and <c>Deserialize</c> does not say.
        /// A value equal to the one held is still a value that arrived, which is
        /// what the reading is for.
        /// <para>
        /// Object-wide because the wire addresses the object: a value lands on
        /// whichever component declared its id, which is not in general the
        /// anchor the registry answers with, and a count read off the anchor
        /// alone would miss every sibling's.  Each component counts what was
        /// delivered to it; this sums them over <see cref="ObjectComponents"/>.
        /// </para>
        /// <para>
        /// The reading behind <c>SpawnManager.RemoteValuesDelivered</c>, which
        /// the Editor's play-mode observer consults for the <c>sync</c> runtime
        /// check: the difference across one inbound frame, taken on an object
        /// somebody else owns, is the number of replicated values that reached
        /// this client from that owner.  Not a public surface — a game has
        /// <c>OnValueChanged</c> for the stored value per variable.
        /// </para>
        /// </remarks>
        internal long InboundValuesDelivered
        {
            get
            {
                var components = ObjectComponents;
                if (components == null) return _inboundValuesDelivered;

                long delivered = 0L;
                for (int i = 0; i < components.Count; i++)
                {
                    var component = components[i];
                    if (component != null) delivered += component._inboundValuesDelivered;
                }

                return delivered;
            }
        }

        /// <summary>
        /// <see cref="InboundValuesDelivered"/> since the object's owner last
        /// changed — what this client has heard under the owner the object
        /// names now, object-wide.
        /// </summary>
        /// <remarks>
        /// ⚠️ A frame the previous owner had put on the wire before a handover
        /// moved the object can land after it, and counts here.
        /// </remarks>
        internal long InboundValuesUnderCurrentOwner
        {
            get
            {
                var components = ObjectComponents;
                if (components == null) return _inboundValuesUnderCurrentOwner;

                long delivered = 0L;
                for (int i = 0; i < components.Count; i++)
                {
                    var component = components[i];
                    if (component != null) delivered += component._inboundValuesUnderCurrentOwner;
                }

                return delivered;
            }
        }

        /// <summary>
        /// Whether any component of this object has registered a
        /// <see cref="NetworkVariableBase"/> — whether there is replicated
        /// state for the wire to deliver at all.  Object-wide, on the same
        /// terms as <see cref="InboundValuesDelivered"/>: it is the reading
        /// that says what a delivered count of zero means, and a count taken
        /// over the object is answered over the object.
        /// </summary>
        internal bool DeclaresReplicatedState
        {
            get
            {
                var components = ObjectComponents;
                if (components == null) return _trackedVariables.Count > 0;

                for (int i = 0; i < components.Count; i++)
                {
                    var component = components[i];
                    if (component != null && component._trackedVariables.Count > 0) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Whether every variable of this object holds a whole value the wire
        /// delivered during the current spawn
        /// (<see cref="NetworkVariableBase.HoldsDeliveredValue"/>) — object-wide,
        /// on the terms of <see cref="InboundValuesDelivered"/>.  True for an
        /// object that declares none.
        /// </summary>
        /// <remarks>
        /// The reading an adoption needs where <see cref="InboundValuesDelivered"/>
        /// says too little: a world handed to this client before it held it has
        /// only the frames held for it, and a variable those did not reach is
        /// at the prefab's seed whatever its owner had written.
        /// </remarks>
        internal bool EveryVariableHoldsADeliveredValue
        {
            get
            {
                var components = ObjectComponents;
                if (components == null) return TrackedVariablesHoldDeliveredValues();

                for (int i = 0; i < components.Count; i++)
                {
                    var component = components[i];
                    if (component != null && !component.TrackedVariablesHoldDeliveredValues()) return false;
                }

                return true;
            }
        }

        // The reading itself is the variables' (driven by the variable-list
        // shard), because this file is compiled by none.
        private bool TrackedVariablesHoldDeliveredValues()
            => NetworkVariableBase.EveryHoldsADeliveredValue(_trackedVariables);

        /// <summary>
        /// Inside an <c>[RtmpeRpc]</c> method: the session id of the call's sender (see
        /// <see cref="NetworkManager.CurrentRpcSenderId"/>). <c>0</c> for a call the server made,
        /// and outside an RPC.
        /// </summary>
        /// <remarks>
        /// Compare it with <see cref="NetworkManager.LocalPlayerId"/> to tell this client's own
        /// calls from other players'. Reads <c>0</c> when there is no
        /// <see cref="NetworkManager"/>.
        /// </remarks>
        protected ulong CurrentRpcSender =>
            NetworkManager.Instance != null ? NetworkManager.Instance.CurrentRpcSenderId : 0UL;

        /// <summary>
        /// Inside an <c>[RtmpeRpc]</c> method: what the server reports about the caller — that
        /// it owns this object, that it is the room's host, or that it is the server.
        /// <see cref="RTMPE.Rpc.RpcCallerFacts.None"/> outside an RPC and when the server
        /// reports nothing about the caller.
        /// </summary>
        /// <remarks>
        /// <para>A method's declared <see cref="RTMPE.Rpc.RtmpeRpcAttribute.Caller"/> is
        /// enforced before the method runs; read this to branch inside a method any caller may
        /// reach.</para>
        /// <para><see cref="RTMPE.Rpc.RpcCallerFacts.OwnsObject"/> is reported only on the
        /// object the call addressed. A handler that calls into another object
        /// (<c>chest.Open()</c> from inside <c>Player.Interact()</c>) finds it clear there:
        /// owning the player says nothing about who owns the chest.</para>
        /// </remarks>
        protected RTMPE.Rpc.RpcCallerFacts CurrentRpcCaller =>
            NetworkManager.Instance != null
                ? RTMPE.Rpc.RpcCallerFactsScope.ReadFrom(
                    NetworkManager.Instance.CurrentRpcCallerFacts,
                    NetworkManager.Instance.CurrentRpcObjectId,
                    NetworkObjectId)
                : RTMPE.Rpc.RpcCallerFacts.None;

        /// <summary>
        /// <see langword="true"/> from just before <see cref="OnNetworkSpawn"/> runs until just
        /// before <see cref="OnNetworkDespawn"/> runs.
        /// </summary>
        public bool IsSpawned => _isSpawned;

        /// <summary>
        /// Whether the object is destroyed when its owner leaves the room. Default
        /// <see langword="true"/>; with <see langword="false"/>, the room's host takes the object
        /// over.
        /// </summary>
        /// <remarks>
        /// The setting applies to the whole object and is read when the object is spawned: the
        /// spawn carries the value of the object's first <c>NetworkBehaviour</c>, and the other
        /// clients apply it to every component of the object. Set it in <c>Awake</c>, on every
        /// <c>NetworkBehaviour</c> of the object, so that all of them agree. A change made after
        /// the spawn affects only this client.
        /// </remarks>
        public bool DestroyWithOwner { get; set; } = true;

        // ── Enhanced RPC API ──────────────────────────────────────────────────

        /// <summary>
        /// Calls the <c>[RtmpeRpc]</c> method <paramref name="methodName"/> of this component's
        /// type (declared on it or inherited). The method's <see cref="RtmpeRpcAttribute"/>
        /// decides who runs the call and who may make it.
        /// </summary>
        /// <remarks>
        /// <para>Call it in a room, from the Unity main thread. A call that cannot be sent (not
        /// in a room, an unknown method, a <see langword="null"/> or unsupported argument,
        /// arguments too large to send, a declared caller this client does not satisfy) logs a
        /// warning and sends nothing.</para>
        /// <para>The arguments are not checked against the method's parameters before sending:
        /// a call with the wrong number or types of arguments is sent, and every receiver drops
        /// it with an error.</para>
        /// </remarks>
        /// <param name="methodName">
        /// The method's name. Use <c>nameof(...)</c> so that a rename is a compile error.
        /// </param>
        /// <param name="args">
        /// The arguments, in the method's parameter order. Supported types: <c>int</c>,
        /// <c>float</c>, <c>bool</c>, <c>string</c>, <c>byte[]</c>, <c>ulong</c>,
        /// <c>Vector3</c>, <c>Color</c>, <c>Quaternion</c>, and types that implement
        /// <see cref="INetworkSerializable"/> and are registered with
        /// <see cref="RpcTypeRegistry"/> on every client.
        /// </param>
        public void RPC(string methodName, params object[] args)
        {
            var nm = NetworkManager.Instance;
            if (nm == null)
            {
                Debug.LogWarning("[RTMPE] NetworkBehaviour.RPC: NetworkManager not available.");
                return;
            }
            nm.SendEnhancedRpc(this, methodName, args);
        }

        /// <summary>
        /// Dispatch an inbound Enhanced RPC to the [RtmpeRpc]-decorated method
        /// with the matching method ID.  Called by <c>NetworkManager</c> after it
        /// resolves the target object from the registry.
        /// </summary>
        /// <param name="methodId">FNV-1a method ID of the resolved RPC.</param>
        /// <param name="wireTarget">
        /// The <see cref="RTMPE.Rpc.RpcTarget"/> audience decoded from the
        /// packet; checked against the method's declared audience.
        /// </param>
        /// <param name="callerFacts">
        /// What the gateway attests about the caller; checked against the
        /// method's declared <see cref="RTMPE.Rpc.RtmpeRpcAttribute.Caller"/>.
        /// </param>
        /// <param name="args">Deserialized argument vector.</param>
        internal void DispatchEnhancedRpc(
            uint methodId, RTMPE.Rpc.RpcTarget wireTarget, RTMPE.Rpc.RpcCallerFacts callerFacts, object[] args)
        {
            // Lifecycle gate: an RPC that lands after OnNetworkDespawn or
            // before OnNetworkSpawn has no business mutating component state.
            // Reflection-based dispatch would happily Invoke and the method
            // body might read NetworkBehaviour.IsOwner / NetworkManager.Instance
            // and observe a half-torn-down object.  Drop with a single warning
            // so the operator can spot the protocol-level race in player logs.
            if (!_isSpawned)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcToUnspawnedWarnTicks))
                    RTMPE.Core.RtmpeLog.Warning(
                        $"[RTMPE] RPC dispatched to non-spawned NetworkBehaviour " +
                        $"({GetType().Name}, methodId=0x{methodId:X8}); dropping.");
                return;
            }

            if (!RpcRegistry.TryFindMethod(
                    GetType(), methodId, out MethodInfo method, out RtmpeRpcAttribute attr))
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastUnknownRpcMethodWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkBehaviour: no [RtmpeRpc] method with id 0x{methodId:X8} " +
                        $"on {GetType().Name}. Check that the method exists and is decorated with [RtmpeRpc].");
                return;
            }

            // Audience contract: the [RtmpeRpc] declaration is authoritative.
            // An inbound call whose wire audience diverges from the declared
            // audience — or that targets the server, which a client must never
            // execute locally — is refused before any argument is bound.
            if (!RTMPE.Rpc.EnhancedRpcVerifier.IsDispatchPermitted(attr.Target, wireTarget))
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcAudienceRefusedWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] RPC '{GetType().Name}.{method.Name}' refused: wire target " +
                        $"{wireTarget} is not permitted for a method declared {attr.Target}.");
                return;
            }

            // Caller contract: who may make this call is the declaration's to
            // say, and whether this caller qualifies is the gateway's — the only
            // party that knows both the caller's session and the room's records.
            // Refused before any argument is bound, like the audience above, and
            // counted where a developer and an operator both look: a cheating
            // client is refused here on every receiver but its own.
            if (!RTMPE.Rpc.EnhancedRpcVerifier.IsCallerPermitted(attr.Caller, callerFacts))
            {
                var manager = NetworkManager.Instance;
                if (manager != null) manager.CountRefusedRpcCaller();
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcCallerRefusedWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] RPC '{GetType().Name}.{method.Name}' refused: it declares " +
                        $"Caller = {attr.Caller}, and the gateway attests " +
                        (callerFacts == RTMPE.Rpc.RpcCallerFacts.None
                            ? "nothing about this caller"
                            : "only " + callerFacts) +
                        (RTMPE.Rpc.EnhancedRpcVerifier.IsCallerAttested()
                            ? "."
                            : " — this session's gateway does not attest callers, so every " +
                              "declared caller is refused until it does."));
                return;
            }

            // Validate the deserialized argument vector against the method
            // signature BEFORE invoking.  MethodBase.Invoke would otherwise
            // throw TargetParameterCountException / ArgumentException with a
            // generic message that gives the operator no clue which RPC the
            // server-supplied payload failed to satisfy.  A stale registry on
            // the client (e.g. running an older build than the server) is the
            // most common cause of this mismatch in practice.
            var parameters = method.GetParameters();
            int suppliedCount = args == null ? 0 : args.Length;
            if (suppliedCount != parameters.Length)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcArgCountWarnTicks))
                    Debug.LogError(
                        $"[RTMPE] RPC '{GetType().Name}.{method.Name}' arg count mismatch: " +
                        $"server sent {suppliedCount}, method expects {parameters.Length}. " +
                        "Likely cause: client and server SDK are out of sync.");
                return;
            }
            for (int i = 0; i < parameters.Length; i++)
            {
                object value = args[i];
                Type expected = parameters[i].ParameterType;
                if (value == null)
                {
                    // Reference / nullable types accept null; value types do not.
                    if (expected.IsValueType && Nullable.GetUnderlyingType(expected) == null)
                    {
                        if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcNullArgWarnTicks))
                            Debug.LogError(
                                $"[RTMPE] RPC '{GetType().Name}.{method.Name}' arg #{i} is null " +
                                $"but parameter '{parameters[i].Name}' is non-nullable {expected.Name}.");
                        return;
                    }
                    continue;
                }
                if (!expected.IsInstanceOfType(value))
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcArgTypeWarnTicks))
                        Debug.LogError(
                            $"[RTMPE] RPC '{GetType().Name}.{method.Name}' arg #{i} type mismatch: " +
                            $"got {value.GetType().Name}, parameter '{parameters[i].Name}' expects {expected.Name}.");
                    return;
                }
            }

            try
            {
                method.Invoke(this, args);
            }
            catch (TargetInvocationException tie)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcMethodThrewWarnTicks))
                    Debug.LogError(
                        $"[RTMPE] RPC method '{GetType().Name}.{method.Name}' threw: " +
                        $"{tie.InnerException?.Message ?? tie.Message}");
            }
            catch (Exception ex)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRpcDispatchErrorWarnTicks))
                    Debug.LogError(
                        $"[RTMPE] RPC dispatch error for '{GetType().Name}.{method.Name}': {ex.Message}");
            }
        }

        // ── Overridable callbacks ──────────────────────────────────────────────

        /// <summary>
        /// Called on each client when the object is spawned there. Construct NetworkVariables
        /// here.
        /// </summary>
        /// <remarks>
        /// <see cref="IsSpawned"/>, <see cref="IsOwner"/>, <see cref="NetworkObjectId"/> and
        /// <see cref="OwnerPlayerId"/> are already set; the object is not in the registry yet.
        /// If a base class of your component overrides it, call <c>base.OnNetworkSpawn()</c>
        /// first (analyzer rule RTMPE1022).
        /// </remarks>
        protected virtual void OnNetworkSpawn() { }

        /// <summary>
        /// Called when the object leaves the network on this client. Unsubscribe from
        /// NetworkVariable events here.
        /// </summary>
        /// <remarks><see cref="IsSpawned"/> is already <see langword="false"/>.</remarks>
        protected virtual void OnNetworkDespawn() { }

        /// <summary>
        /// Called when the object's owner changes. Not called when the owner stays the same.
        /// </summary>
        /// <remarks>
        /// <see cref="OwnerPlayerId"/> and <see cref="IsOwner"/> already describe the new owner.
        /// </remarks>
        /// <param name="previousOwner">The previous owner's player id.</param>
        /// <param name="newOwner">The new owner's player id.</param>
        protected virtual void OnOwnershipChanged(string previousOwner, string newOwner) { }

        /// <summary>
        /// Keeps the SDK's records right when the GameObject is destroyed outside the SDK, for
        /// example with <c>Object.Destroy</c> or by a scene load.
        /// </summary>
        /// <remarks>
        /// If you override it, call <c>base.OnDestroy()</c> (analyzer rule RTMPE1020).
        /// Otherwise the destroyed object stays in the SDK's records and keeps counting against
        /// <see cref="NetworkSettings.maxSpawnsPerRoom"/>.
        /// </remarks>
        protected virtual void OnDestroy()
        {
            // Idempotency flag: when DestroyLocal already ran, registry-eviction
            // and counter decrement happened there.  SpawnManager's
            // OnExternallyDestroyed double-checks via the registry so the
            // counters cannot move twice — this flag is a fast-path skip.
            if (_externallyEvicted) return;
            _externallyEvicted = true;

            // During application shutdown the singleton accessor may itself
            // throw (object finalisation order is undefined); swallow so the
            // shutdown path remains exception-free.
            try
            {
                var nm = NetworkManager.Instance;
                // `this` travels with the id so the seam can tell which
                // instance is reporting: after a same-id eviction the id
                // names the replacement, and the reconciliation belongs to
                // the object being destroyed rather than to the number.
                nm?.SpawnManagerInternal?.OnExternallyDestroyed(_networkObjectId, this);
            }
            catch { /* best-effort during shutdown */ }
        }

        // Set by either DestroyLocal (when SpawnManager owns the teardown)
        // or by OnDestroy itself (when Unity destroys the GameObject before
        // DestroyLocal runs).  Either way the second caller observes true
        // and skips the reconciliation work.  Internal so SpawnManager can
        // mark the flag from its DestroyLocal path.
        private bool _externallyEvicted;
        internal void MarkExternallyEvicted() => _externallyEvicted = true;

        // Both seams route their liveness question here, and it has to be asked
        // at this static type: the destroyed-object equality lives on
        // UnityEngine.Object, so the same comparison written against an
        // interface reference answers with the reference and admits a component
        // Unity has already torn down.
        private bool ComponentIsAlive => this != null;

        // INbLifecycle — explicit forwarders, because Initialize / SetSpawned /
        // MarkExternallyEvicted / AssignOwner / RaiseOwnershipChanged are
        // internal SDK surface and cannot implicitly satisfy a public
        // interface.  The seam lets SpawnManager and OwnershipManager drive
        // every component of an object (via SpawnLifecycleOps) without that
        // helper depending on this Unity type.  IsSpawned is already public, so
        // it satisfies the interface implicitly.
        bool INbLifecycle.IsAlive => ComponentIsAlive;
        void INbLifecycle.Initialize(ulong objectId, string ownerPlayerId) => Initialize(objectId, ownerPlayerId);
        void INbLifecycle.SetSpawned(bool spawned) => SetSpawned(spawned);
        void INbLifecycle.RecycleForNextLife() => RecycleForNextLife();
        void INbLifecycle.MarkExternallyEvicted() => MarkExternallyEvicted();
        bool INbLifecycle.AssignOwner(string ownerPlayerId) => AssignOwner(ownerPlayerId);
        void INbLifecycle.RaiseOwnershipChanged() => RaiseOwnershipChanged();

        // INbDispatch — the same arrangement for the per-frame half: the tick,
        // the flush and the inbound variable update are object-wide operations
        // that the registry anchors on one component, so ObjectDispatchOps
        // drives them across every component through this seam.  IsOwner and
        // IsSpawned are public and satisfy it implicitly.
        bool INbDispatch.IsAlive => ComponentIsAlive;
        void INbDispatch.FixedTick(float deltaTime) => InvokeOnFixedTick(deltaTime);
        void INbDispatch.MarkAllVariablesDirty() => MarkAllVariablesDirty();
        void INbDispatch.FlushDirtyVariables(Func<byte[], int, bool> sendPayload) => FlushDirtyVariables(sendPayload);
        bool INbDispatch.TracksVariable(uint variableId) => TracksVariable(variableId);
        bool INbDispatch.TryApplyVariableUpdate(
            uint variableId, BinaryReader reader, uint packetTick, bool hasPacketTick)
            => TryApplyVariableUpdate(variableId, reader, packetTick, hasPacketTick);

        // Where the next flush starts its walk of _trackedVariables.  See the
        // rotation note in FlushDirtyVariables: with a per-payload byte budget,
        // a fixed start starves everything behind a large variable.
        private int _flushStartIndex;

        // One-line-per-second gate for the "will never fit" report below.  The
        // variable stays dirty by design, so the condition recurs on every tick
        // until the developer shrinks it.
        private long _lastOversizeVariableWarnTicks;

        // Latched once per instance the first time FlushDirtyVariables hits
        // the 255-variable wire-format cap.  Prevents the diagnostic warning
        // from spamming every flush tick when the configuration is permanent.
        private bool _overflowWarned;

        // Cached serialization resources for FlushDirtyVariables.  Lazy-init
        // on the first dirty flush so objects that are never dirty (read-only
        // replicas) pay zero cost.  MemoryStream and BinaryWriter hold no
        // unmanaged handles; GC collects them when the behaviour is destroyed.
        // Reset via SetLength(0) before each flush — reuses the internal buffer
        // that the stream already allocated, eliminating per-tick heap churn.
        private MemoryStream _flushMs;
        private BinaryWriter _flushWriter;

        // The variables one flush serialised, marked clean only once the sender
        // has taken the payload; cleared on every exit.  Cached for the same
        // per-tick allocation reason as the stream above.
        private readonly List<NetworkVariableBase> _flushAppended = new List<NetworkVariableBase>();

        // ── Sync-component cache ────────────────────────────────────────────
        //
        // The receive hot-path (HandleStateSyncPacket / HandlePhysicsSyncPacket
        // / HandlePhysicsSync2DPacket / HandleVariableUpdatePacket) dispatches
        // each inbound packet to one of these sync components on the same
        // GameObject.  Unity 2022+ resolves GetComponent<T> in O(1) via an
        // internal typed cache (~50–100 ns/call), but at 30 Hz × 8 peers that
        // tallies ~24 µs/sec per object spent on type lookups; under IL2CPP
        // without the JIT cache the overhead is materially higher.
        //
        // These fields hold the lazily-resolved sync components for this
        // GameObject so the hot-path resolves to a single field load.
        // Lookups are gated by a "queried" flag so an object that genuinely
        // lacks a sync component pays the GetComponent cost exactly once
        // per spawn cycle rather than per packet.  Both fields and flags
        // are cleared on despawn — see [ResetSyncComponentCache].
        //
        // The Unity null operator (==) is used everywhere instead of `is`
        // so a destroyed-but-not-finalised component is treated as missing
        // and re-queried; `(object)` checks would observe the destroyed
        // wrapper and incorrectly skip the refetch.
        [NonSerialized] private NetworkTransform              _cachedNetworkTransform;
        [NonSerialized] private bool                          _cachedNetworkTransformQueried;
        [NonSerialized] private NetworkTransformInterpolator  _cachedNetworkTransformInterpolator;
        [NonSerialized] private bool                          _cachedNetworkTransformInterpolatorQueried;
        [NonSerialized] private NetworkRigidbody              _cachedNetworkRigidbody;
        [NonSerialized] private bool                          _cachedNetworkRigidbodyQueried;
        [NonSerialized] private NetworkRigidbody2D            _cachedNetworkRigidbody2D;
        [NonSerialized] private bool                          _cachedNetworkRigidbody2DQueried;

        /// <summary>
        /// Sync-component accessor used by the receive hot-path.  Caches
        /// <see cref="UnityEngine.Component.GetComponent{T}"/> after the
        /// first call within a spawn cycle so per-packet dispatch resolves
        /// to a field load rather than a typed component lookup.
        /// </summary>
        internal NetworkTransform CachedNetworkTransform
        {
            get
            {
                if (_cachedNetworkTransform != null) return _cachedNetworkTransform;
                if (_cachedNetworkTransformQueried) return null;
                _cachedNetworkTransform = GetComponent<NetworkTransform>();
                _cachedNetworkTransformQueried = true;
                return _cachedNetworkTransform;
            }
        }

        /// <summary>
        /// Sync-component accessor — see <see cref="CachedNetworkTransform"/>.
        /// </summary>
        internal NetworkTransformInterpolator CachedNetworkTransformInterpolator
        {
            get
            {
                if (_cachedNetworkTransformInterpolator != null) return _cachedNetworkTransformInterpolator;
                if (_cachedNetworkTransformInterpolatorQueried) return null;
                _cachedNetworkTransformInterpolator = GetComponent<NetworkTransformInterpolator>();
                _cachedNetworkTransformInterpolatorQueried = true;
                return _cachedNetworkTransformInterpolator;
            }
        }

        /// <summary>
        /// Sync-component accessor — see <see cref="CachedNetworkTransform"/>.
        /// </summary>
        internal NetworkRigidbody CachedNetworkRigidbody
        {
            get
            {
                if (_cachedNetworkRigidbody != null) return _cachedNetworkRigidbody;
                if (_cachedNetworkRigidbodyQueried) return null;
                _cachedNetworkRigidbody = GetComponent<NetworkRigidbody>();
                _cachedNetworkRigidbodyQueried = true;
                return _cachedNetworkRigidbody;
            }
        }

        /// <summary>
        /// Sync-component accessor — see <see cref="CachedNetworkTransform"/>.
        /// </summary>
        internal NetworkRigidbody2D CachedNetworkRigidbody2D
        {
            get
            {
                if (_cachedNetworkRigidbody2D != null) return _cachedNetworkRigidbody2D;
                if (_cachedNetworkRigidbody2DQueried) return null;
                _cachedNetworkRigidbody2D = GetComponent<NetworkRigidbody2D>();
                _cachedNetworkRigidbody2DQueried = true;
                return _cachedNetworkRigidbody2D;
            }
        }

        // The object's own NetworkBehaviour set, on the same terms as the sync
        // components above: resolved once per spawn cycle and cleared on
        // despawn.  Every object-wide dispatch reads it at packet or tick rate,
        // so the non-allocating GetComponents overload fills a retained list
        // rather than returning a fresh array 30 times a second.
        [NonSerialized] private List<NetworkBehaviour> _cachedObjectComponents;
        [NonSerialized] private bool                   _cachedObjectComponentsQueried;

        /// <summary>
        /// Every <see cref="NetworkBehaviour"/> on this object's
        /// <c>GameObject</c>, in component order — the fan-out domain for
        /// <see cref="ObjectDispatchOps"/> and for the object-wide half of
        /// <see cref="SpawnLifecycleOps"/>.  Resolved once per spawn cycle; a
        /// component added mid-spawn is picked up on the next spawn, which is
        /// the same bound the sync-component accessors carry.
        /// </summary>
        /// <remarks>
        /// Invalidated at <see cref="Initialize"/> rather than at despawn, and
        /// that is load-bearing: the despawn fan-out iterates this very list
        /// while each component's despawn tail runs, so a reset there would
        /// refill the collection mid-iteration and leave siblings untouched —
        /// the defect the fan-out exists to remove, reintroduced one layer down.
        /// The spawn fan-out, by contrast, walks a freshly-queried array, so
        /// invalidating from under it is not possible.
        /// <para>The set is not re-checked for destroyed entries, because a
        /// destroyed <see cref="NetworkBehaviour"/> takes the whole object out
        /// of the registry: its <c>OnDestroy</c> reaches
        /// <c>SpawnManager.OnExternallyDestroyed</c>, which unregisters the
        /// object id, so no dispatch reaches a component set holding one.</para>
        /// </remarks>
        internal IReadOnlyList<NetworkBehaviour> ObjectComponents
        {
            get
            {
                if (_cachedObjectComponentsQueried) return _cachedObjectComponents;

                // Published, never edited in place: a fan-out holds the list it
                // was handed for the length of the dispatch, and refilling that
                // instance would change its length and contents under a loop
                // already running.  The cost is one list per spawn, against a
                // read per tick and per inbound variable.
                var resolved = new List<NetworkBehaviour>(4);
                GetComponents(resolved);
                _cachedObjectComponents = resolved;
                _cachedObjectComponentsQueried = true;
                return _cachedObjectComponents;
            }
        }

        /// <summary>
        /// Wipe the sync-component cache.  Invoked on every despawn so a
        /// pool-recycled instance does not retain references from its
        /// previous spawn — components attached after pool re-acquire will
        /// be picked up by the next hot-path access.
        /// </summary>
        private void ResetSyncComponentCache()
        {
            _cachedNetworkTransform                   = null;
            _cachedNetworkTransformQueried            = false;
            _cachedNetworkTransformInterpolator       = null;
            _cachedNetworkTransformInterpolatorQueried = false;
            _cachedNetworkRigidbody                   = null;
            _cachedNetworkRigidbodyQueried            = false;
            _cachedNetworkRigidbody2D                 = null;
            _cachedNetworkRigidbody2DQueried          = false;
        }

        // ── Internal SDK API (called by SpawnManager) ──────────────────────────

        /// <summary>
        /// Initialise the network identity of this object.
        /// Called by <c>SpawnManager</c> immediately after instantiation.
        /// </summary>
        /// <param name="objectId">Server-assigned unique object ID (u64).</param>
        /// <param name="ownerId">Room player UUID of the object's owner.</param>
        internal void Initialize(ulong objectId, string ownerId)
        {
            // Guard against double-initialisation — warn if the object is already
            // spawned, which can occur via duplicate Spawn packets on reliable transport.
            if (_networkObjectId != 0
                && RTMPE.Core.WarnGate.ShouldEmit(ref _lastDoubleInitializeWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] NetworkBehaviour.Initialize called twice on object " +
                    $"{_networkObjectId} → overwriting with {objectId}. " +
                    "Possible duplicate Spawn packet received via reliable transport retransmit.");

            // Clear the externally-evicted latch so a pool-recycled instance
            // starts each spawn with the same baseline as a fresh
            // GameObject.  Without the reset, the latch set during the
            // previous DestroyLocal would short-circuit OnDestroy on the
            // next user-driven Object.Destroy(go), skipping
            // OnExternallyDestroyed and leaking one slot from
            // SpawnManager._currentSpawnCount per pool re-acquire cycle.
            _externallyEvicted = false;

            // Re-resolve the object's component set on the next read: a
            // pool-recycled instance may have been re-parented or re-composed
            // since the last spawn, and every object-wide dispatch reads this
            // set.  See ObjectComponents for why the invalidation belongs here
            // and not in the despawn tail.
            _cachedObjectComponentsQueried = false;

            _networkObjectId = objectId;
            _ownerPlayerId   = ownerId ?? string.Empty;
            _spawnOwnerPlayerId    = _ownerPlayerId;
            _previousOwnerPlayerId = string.Empty;

            // A pooled instance is handed its owner here rather than by a
            // handover, and an owner it was never told about is not one it can
            // owe an announcement for.  Set together with the owner so a
            // re-acquire opens with the same bookkeeping a fresh object has.
            _announcedOwnerPlayerId = _ownerPlayerId;
            _ownershipChangePending = false;
            _inboundValuesDelivered = 0L;
            _inboundValuesUnderCurrentOwner = 0L;

            // Snapshot the local player UUID at construction so IsOwner remains
            // correct after the NetworkManager singleton has been torn down
            // (OnApplicationQuit, scene unload, domain reload).  Refreshed
            // again at SetSpawned(true) to absorb any reconnect-driven id
            // change that lands between Initialize and the spawn transition.
            var nm = NetworkManager.Instance;
            _cachedLocalPlayerId = nm != null
                ? (nm.LocalPlayerStringId ?? string.Empty)
                : string.Empty;
        }

        /// <summary>
        /// Transition the spawn state of this object.
        /// Fires <see cref="OnNetworkSpawn"/> or <see cref="OnNetworkDespawn"/> as needed.
        /// Idempotent: calling <c>SetSpawned(true)</c> twice only fires the callback once.
        /// </summary>
        internal void SetSpawned(bool spawned)
        {
            if (spawned && !_isSpawned)
            {
                // Snapshot the local player UUID before user code in
                // OnNetworkSpawn observes IsOwner.  Capturing here (rather
                // than in Initialize) ensures that a reconnect which mints a
                // new local player id is picked up by the next spawn cycle —
                // SetSpawned(true) is invoked after the registry adopts the
                // post-reconnect identity.  Instance is allowed to be null
                // here only during teardown; in that case the empty cached
                // id correctly causes IsOwner to return false.
                var nm = NetworkManager.Instance;
                _cachedLocalPlayerId = nm != null
                    ? (nm.LocalPlayerStringId ?? string.Empty)
                    : string.Empty;

                _isSpawned = true;
                // A pooled instance is handed its owner by Initialize rather than
                // by a handover, so this is the only point at which the sync
                // components can learn that the object changed hands between one
                // life and the next.  Before OnNetworkSpawn, so user code sees
                // components already reconciled to the ownership it reads there.
                ReconcileSyncComponentsToOwnership(startingNewLife: true);

                // Per-life state, reset through one named event.  The inbound
                // tick gate holds a high-water mark drawn from ONE sender's tick
                // clock and was reset only on a handover; a pooled instance
                // reaches its next life through this cycle instead, and a new
                // peer whose ticks start lower is then silently under-replicated
                // until its clock passes a number it never agreed to.
                //
                // ⚠️ The scope is narrower than "every variable", and the reason
                // is worth stating because an earlier comment here got it wrong.
                // SetSpawned(false) releases the SPAWN-SCOPED span through
                // SpawnScopedRegistrations.ReleaseSpan, so a variable built
                // inside OnNetworkSpawn — the documented place — is gone from
                // this list at despawn and is constructed fresh next life with a
                // clean gate.  What survives, and what this loop is for, is
                // everything registered OUTSIDE that span: field initialisers,
                // constructors, Start, OnEnable.
                //
                // ⛔ And it runs BEFORE Mark and before OnNetworkSpawn, so a
                // variable registered in the callback is not in the list yet.
                // Those arm themselves in their own constructor, from an owner
                // that is already spawned by this point.
                for (int i = 0; i < _trackedVariables.Count; i++)
                    _trackedVariables[i].OnOwnerSpawned();
                ValidateRpcMethodsOnce();

                // OnNetworkSpawn is where the SDK tells developers to construct
                // NetworkVariables, and each construction registers itself.
                // Bracket the callback so its own registrations can be told
                // apart both from the ones that came before it and from the
                // ones a Start or a lazy Update adds after it — pooling re-runs
                // this callback and nothing else, so only what it registers is
                // re-registered on the next acquire.  The span is closed even
                // when the callback throws; otherwise it would describe a life
                // that has already ended.
                _spawnScopedVariableMark = SpawnScopedRegistrations.Mark(_trackedVariables);
                try
                {
                    OnNetworkSpawn();
                }
                finally
                {
                    _spawnScopedVariableCount = SpawnScopedRegistrations.SpanSince(
                        _trackedVariables, _spawnScopedVariableMark);
                }
            }
            else if (!spawned && _isSpawned)
            {
                _isSpawned = false;

                // The teardown that follows belongs to the transition, not to
                // the callback, and the callback is user code: the registry
                // despawns each object inside its own catch so one throwing
                // handler cannot strand the others, which leaves this instance
                // running with a half-ended life unless the tail is
                // unconditional.  Everything here is idempotent and reads no
                // state the callback could have invalidated.
                try
                {
                    OnNetworkDespawn();
                }
                finally
                {
                    // The variables OnNetworkSpawn registered end with the life
                    // it opened.  A pooled instance is handed back and
                    // re-acquired without ever being reconstructed, so leaving
                    // them behind would make the next spawn's registrations
                    // collide with their own predecessors — and the collision
                    // guard is a throw, out of the middle of the spawn
                    // pipeline.  Released after the despawn callback so user
                    // code can still read its own variables while tearing down.
                    SpawnScopedRegistrations.ReleaseSpan(
                        _trackedVariables, _spawnScopedVariableMark, _spawnScopedVariableCount);
                    _spawnScopedVariableCount = 0;

                    // Wipe the sync-component cache after user code has run so
                    // OnNetworkDespawn handlers can still observe the cached
                    // references during teardown.  A pool-recycled instance
                    // re-acquired by SpawnManager will populate the cache
                    // afresh on its next hot-path access.
                    ResetSyncComponentCache();
                }
            }
        }

        /// <summary>
        /// The instance is going back to an object pool.  Every variable that
        /// survived the despawn — one registered outside <c>OnNetworkSpawn</c>,
        /// which <see cref="SetSpawned"/> released with the life — sheds the
        /// ended life's state and returns to what its constructor established,
        /// so the next life this instance is handed out for begins where the
        /// prefab says, on the owner and on every replica alike.
        /// </summary>
        /// <remarks>
        /// After the despawn, never instead of it: <c>OnNetworkDespawn</c> has
        /// already seen the life's final values, and <c>_isSpawned</c> is
        /// already false — a pooled instance is deactivated by the pool, not
        /// destroyed, so the state a reset does not reach is the state the
        /// next life inherits.  Reached from the pool hand-back alone; an
        /// instance on its way to <c>Destroy</c> keeps its post-despawn
        /// refusals and is never written again (the one destroyed after the
        /// pool refused it has been reset for nothing, which costs nothing).
        /// The announcements the variables make here fire with
        /// <c>IsSpawned</c> false and the ended life's object id and owner
        /// still set — a subscriber guarded on <c>IsSpawned</c> will not see
        /// them, and one summing changes across objects sees a drop to the
        /// seed.
        /// </remarks>
        internal void RecycleForNextLife()
        {
            for (int i = 0; i < _trackedVariables.Count; i++)
                _trackedVariables[i].OnOwnerRecycled();
        }

        /// <summary>
        /// Run <see cref="RpcRegistry.Validate"/> exactly once per concrete
        /// subclass.  RPC ID collisions are a programming error that must be
        /// fixed before shipping; the runtime logs them as a Unity error so
        /// they are visible in the Editor console and in player logs.
        /// </summary>
        /// <remarks>
        /// We log + swallow rather than throw, because a single misbehaving
        /// prefab should not abort the spawn pipeline for other (correctly
        /// authored) objects.  Tests and editor tooling that want a hard
        /// failure should call <see cref="RpcRegistry.Validate"/> directly.
        /// </remarks>
        private void ValidateRpcMethodsOnce()
        {
            var type = GetType();
            if (_validatedTypes.Contains(type)) return;
            _validatedTypes.Add(type);
            try
            {
                RpcRegistry.Validate(type);
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogError(ex.Message, this);
            }
        }

        /// <summary>
        /// Apply a server-confirmed ownership change.
        /// Only call from <c>OwnershipManager.ApplyOwnershipGrant</c>.
        /// </summary>
        /// <param name="newOwner">Room player UUID of the new owner.</param>
        internal void SetOwner(string newOwner)
        {
            AssignOwner(newOwner);
            RaiseOwnershipChanged();
        }

        /// <summary>
        /// Record a new owner without announcing it, returning whether the
        /// value changed.  Split from the announcement so an object-wide
        /// handover can settle every component before any user handler runs —
        /// see <see cref="SpawnLifecycleOps.SetOwnerAll"/>.
        /// </summary>
        internal bool AssignOwner(string newOwner)
        {
            // Suppress no-change callbacks to avoid redundant notifications on
            // retransmitted ownership updates.
            var normalized = newOwner ?? string.Empty;
            if (_ownerPlayerId == normalized) return false;

            // The owner this component last announced, not the one it held a
            // moment ago: consecutive assignments before an announcement
            // coalesce, so the handler sees the transition the application can
            // actually observe rather than an intermediate it never saw.
            if (!_ownershipChangePending) _announcedOwnerPlayerId = _ownerPlayerId;
            _ownershipChangePending = true;
            _previousOwnerPlayerId  = _ownerPlayerId;
            _ownerPlayerId          = normalized;

            // Nothing is yet heard under the owner it names now.
            _inboundValuesUnderCurrentOwner = 0L;

            // Reset per-variable throttle state on every ownership transfer so
            // the new owner's first flush is not gated behind a stale
            // LastFlushTimeUnscaled inherited from the previous owner's
            // bookkeeping.  Cheap (one float assignment per variable) and
            // correctness-critical for variables with low SendRateHz where the
            // throttle interval can exceed the gap between ownership handoffs.
            //
            // Reset the inbound-tick gate as well: the new owner's first
            // VariableUpdate may carry a tick lower than the highest tick the
            // previous owner observed (a different sender's tick clock), and
            // without resetting the gate that update would be dropped as a
            // stale replay until the new owner's tick passed the high-water
            // mark — visible to the user as a multi-second silent under-
            // replication after every handoff.
            //
            // And tell each variable, with the owner already recorded: a list
            // keeps a log of the edits it is yet to send, which is a sender's
            // — a client that has just stopped being the sender sheds it,
            // rather than flush it against peers whose state it no longer
            // describes should ownership come back.
            for (int i = 0; i < _trackedVariables.Count; i++)
            {
                _trackedVariables[i].ResetThrottleState();
                _trackedVariables[i].ResetInboundTickGate();
                _trackedVariables[i].OnOwnerChanged();
            }

            // The sync components hold state that belongs to the client that
            // was driving the object, and this is the event that replaces it.
            // The object is the same one throughout, which is what separates
            // this from the spawn cycle above.
            ReconcileSyncComponentsToOwnership(startingNewLife: false);
            return true;
        }

        /// <summary>
        /// Announce an owner change <see cref="AssignOwner"/> left owed.  A
        /// no-op when nothing is owed, so it is safe to call on every component
        /// of an object whether or not each one moved.
        /// </summary>
        internal void RaiseOwnershipChanged()
        {
            if (!_ownershipChangePending) return;
            _ownershipChangePending = false;

            var previous = _announcedOwnerPlayerId;
            _announcedOwnerPlayerId = _ownerPlayerId;
            if (previous == _ownerPlayerId) return;

            OnOwnershipChanged(previous, _ownerPlayerId);
        }

        /// <summary>
        /// The player this object's state came from has left the room and the
        /// object outlives it: whoever writes it next writes on a clock of its
        /// own, so the inbound gates drawn from the departed sender's — each
        /// variable's tick watermark and, on a replica, the interpolator's
        /// sender timeline — are forgotten here, as a handover forgets them.
        /// Called a moment after the departure (<c>SpawnManager</c> books it),
        /// once that sender's frames still in flight have landed against the
        /// gates they were sent under, and only if the object is still the
        /// departed player's then.
        /// </summary>
        /// <remarks>
        /// ⚠️ Not a handover, and never instead of one.  The handover that
        /// forgets them too names the next owner, and it reaches this client on
        /// a room event — <c>MasterClientChanged</c> when the host itself left —
        /// delivered best-effort.  A client that missed it kept the object under
        /// the departed owner with gates the new host's lower ticks could not
        /// pass until its clock caught up, which for a host that joined after
        /// the one it replaced is never.  Only the gates move here: the owner,
        /// and everything a handover decides, stay where they are.
        /// </remarks>
        internal void ForgetDepartedSendersClocks()
        {
            for (int i = 0; i < _trackedVariables.Count; i++)
                _trackedVariables[i].ResetInboundTickGate();

            // Derived, like every answer the interpolator is given, and given on
            // a replica alone: there it retires the sender timeline and keeps
            // the buffered snapshots, as a handover between two peers does.
            bool locallyOwned = IsOwner;
            if (!locallyOwned) CachedNetworkTransformInterpolator?.SetLocallyOwned(locallyOwned);
        }

        /// <summary>
        /// Bring the sync components in line with who owns this object now.
        /// </summary>
        /// <remarks>
        /// Ownership decides which component drives the transform, and each of
        /// them carries state that only the client holding the object could have
        /// produced.
        ///
        /// The interpolator's sender-tick high-water is the same kind of gate the
        /// variable loop above resets, over the same kind of counter: on the
        /// owner-tick timeline that tick belongs to whichever client owns the
        /// object, so after a handover the new owner's counter can sit far below
        /// the previous owner's and every inbound record is rejected as a stale
        /// replay until it catches up — a replica frozen for minutes while its
        /// traffic keeps arriving.  When the object came to THIS client the
        /// interpolator must additionally stand down: it renders from the last
        /// execution slot, so a buffered timeline it kept replaying would
        /// overwrite the new owner's own motion every frame and the object would
        /// never move again for the client that just took it.
        ///
        /// <see cref="NetworkTransform"/>'s velocity cap meters a broadcast
        /// against the previous one, and while the object was a replica it made
        /// none — so its baseline is the pose the object spawned at, which is
        /// not where the object is.  Adopting the current pose states that
        /// discontinuity; see
        /// <see cref="NetworkTransform.AdoptCurrentPoseAsVelocityBaseline"/>.
        ///
        /// Called from both events that can change the answer: a server-confirmed
        /// handover, and the start of a spawn cycle — a pooled instance is handed
        /// its owner by <c>Initialize</c>, so it can change hands with no
        /// handover ever being announced.
        /// </remarks>
        private void ReconcileSyncComponentsToOwnership(bool startingNewLife)
        {
            // The sync components are shared by every behaviour on the object,
            // so each call here answers for all of them and the last one wins.
            // A component that carries no object id was never initialised and
            // has no answer to give: IsOwner is false for it whoever owns the
            // object, and a false answer leaves the interpolator driving the
            // transform — so the object would never move again for the client
            // that just took it.  Reached when user code adds a
            // NetworkBehaviour to an already-spawned object; the spawn path
            // initialises every component before any of them reconciles.
            if (_networkObjectId == 0) return;

            bool locallyOwned = IsOwner;

            // A new life on a pooled instance is not a handover: the snapshots
            // and the displacement gate's reference pose describe where the
            // PREVIOUS object was, and this one is somewhere else.  Retiring
            // them is unconditional here, where SetLocallyOwned retires them
            // only for the client the object came to — which is the correct
            // reading for a handover and the wrong one for a reuse, because the
            // premise it rests on (the snapshots still describe this object) is
            // exactly what a reuse breaks.
            if (startingNewLife)
                CachedNetworkTransformInterpolator?.RetireForNewObjectLife();

            CachedNetworkTransformInterpolator?.SetLocallyOwned(locallyOwned);

            if (locallyOwned)
            {
                CachedNetworkTransform?.AdoptCurrentPoseAsVelocityBaseline();
            }
            else
            {
                // The reconciliation blend is a correction to a prediction this
                // client was making, against a start pose captured while it
                // still owned the object.  Losing ownership does not end it —
                // it SUSPENDS it, because the loop that drives it returns early
                // for a non-owner — so the pair sits there until this client
                // owns the object again, and the next frame after that resumes
                // a blend from two lives ago: the object jumps to a start pose
                // it left long before and completes toward a target the server
                // has since superseded.  The owner's own teleport hatch ends it
                // for the mirror-image reason, that the start pose has stopped
                // describing this object.
                CachedNetworkTransform?.AbandonReconciliationBlend();
                // And a teleport this client had not yet sent is no longer
                // its to send: it would go out, marked, with whatever pose the
                // object holds when this client next owns it.
                CachedNetworkTransform?.ForgetPendingTeleport();
            }

            // Both physics components force a replica's body kinematic and
            // neither had an event that could give it back: the object a client
            // is handed mid-session stayed frozen for the rest of its life.
            CachedNetworkRigidbody?.ReconcileKinematicToOwnership(locallyOwned);
            CachedNetworkRigidbody2D?.ReconcileKinematicToOwnership(locallyOwned);
        }

        // ── NetworkVariable registration and flush ─────────────────────────────

        /// <summary>
        /// The NetworkVariables registered on this component, in the order they were created.
        /// </summary>
        /// <remarks>
        /// This is the live list, not a copy: read it on the main thread and do not modify it.
        /// </remarks>
        public IReadOnlyList<NetworkVariableBase> TrackedVariables => _trackedVariables;

        /// <summary>
        /// Register a <see cref="NetworkVariableBase"/> with this behaviour so it
        /// participates in the 30 Hz dirty-flush loop.
        /// Called automatically by the <c>NetworkVariableBase</c> constructor —
        /// user code should never call this directly.
        /// </summary>
        internal void TrackVariable(NetworkVariableBase variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));

            // The clash within one behaviour, which the analyzer also catches
            // at compile time: the inbound update path dispatches to the first
            // match by id, so a duplicate would leave every later variable
            // permanently unreplicated.  Surface it at registration
            // (OnNetworkSpawn) instead of letting it manifest as silent desync —
            // mirroring the RpcRegistry method-id collision guard.
            if (NetworkVariableBase.FindIdHolder(_trackedVariables, variable.VariableId) != null)
                throw new InvalidOperationException(
                    $"[RTMPE] {GetType().Name}: two NetworkVariables derive " +
                    $"identity {variable.VariableId}.  An identity comes from the " +
                    "CONCRETE type and the name a variable is constructed with, so this " +
                    "is either one member constructed twice, or a base of this type " +
                    "declaring a variable under a name this type uses as well — on an " +
                    "instance of this type the two are one identity.  Give each " +
                    "construction the name of the member it is assigned to, as " +
                    "nameof(_field), and rename one member of a base/derived pair.  " +
                    "Inbound updates dispatch by identity and would otherwise reach " +
                    "only the first.");

            // The same clash across two components of one object.  The wire
            // names [object_id][variable_id] and nothing else, so the id has to
            // be unique across the object for the update to be addressable at
            // all; whichever component registers the id first keeps it, and the
            // second is refused.  ⚠️ Which one that is is not a property of
            // component order: registration happens as each component's spawn
            // runs, so a prefab whose two components claim one id has an owner
            // decided by that order and not by distance from the anchor.
            //
            // Reported rather than thrown, unlike the case above: a second
            // component holding a taken id is state that has never replicated,
            // so the object it belongs to is one an application may well be
            // shipping today, and throwing out of OnNetworkSpawn would abort a
            // spawn that currently completes.  The variable stays usable
            // locally and stays off the wire — what changes is that it now says
            // so instead of failing silently.
            var conflict = ObjectDispatchOps.FindVariableIdClaimant(
                ObjectComponents, this, variable.VariableId);
            if (conflict != null && WarnGate.ShouldEmit(ref _lastVariableIdConflictWarnTicks))
            {
                Debug.LogError(
                    $"[RTMPE] {GetType().Name}: identity {variable.VariableId} is already held by " +
                    $"{conflict.GetType().Name} on the same object.  An identity is unique " +
                    "across an object, not within a component — the wire carries no component " +
                    "discriminator.  This variable will not replicate.\n" +
                    "An identity is derived from the owning type and the member name, so two " +
                    "components reach this only by sharing that name: two instances of ONE " +
                    "component type on one object, or two instantiations of one generic type, " +
                    "which are named by the generic definition.  Keep a single instance (add " +
                    "[DisallowMultipleComponent] to say so), or move the member to a second " +
                    "type.  Renaming the member changes the identity on every peer and needs " +
                    "them all rebuilt.", this);
            }
            if (conflict != null) return;

            _trackedVariables.Add(variable);

            // Apply [NetworkVariable(SendRateHz = …)] declaratively, matching
            // attribute declarations to the variable instance by reading the
            // field/property value.  Handled inline so dynamically created
            // NetworkVariable instances (e.g. via TrackVariable from a
            // non-attributed source) keep their default 0 (use global cadence).
            ApplyVariableAttributesIfAny(variable);
        }

        /// <summary>
        /// Match <see cref="NetworkVariableAttribute"/> declarations on this
        /// behaviour's type to <paramref name="variable"/> by reading each
        /// candidate field/property and comparing the value reference against
        /// <paramref name="variable"/>.  When a match is found the attribute's
        /// <see cref="NetworkVariableAttribute.SendRateHz"/> is copied onto the
        /// variable instance so the per-tick flush loop can throttle it.
        ///
       /// <para>The reflection scan is performed at most once per concrete
        /// subclass; subsequent spawns reuse the cached metadata list.</para>
        /// </summary>
        private void ApplyVariableAttributesIfAny(NetworkVariableBase variable)
        {
            var type     = GetType();
            var metadata = GetOrBuildMetadata(type);
            if (metadata.Count == 0) return;

            for (int i = 0; i < metadata.Count; i++)
            {
                var entry = metadata[i];
                object held;
                try { held = entry.ReadValue(this); }
                catch (Exception ex)
                {
                    // A property-getter throwing should not abort registration of
                    // a sibling variable — log once and move on.
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastAttributeScanThrowWarnTicks))
                        Debug.LogWarning(
                            $"[RTMPE] NetworkVariable attribute scan: reading " +
                            $"{type.Name}.{(entry.Field?.Name ?? entry.Property?.Name)} threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Skipping this declaration.");
                    continue;
                }

                if (!ReferenceEquals(held, variable)) continue;

                variable.SendRateHz = entry.SendRateHz;
                return; // a single field/property declares a single variable
            }
        }

        /// <summary>
        /// Build (and cache) the list of <see cref="NetworkVariableAttribute"/>
        /// declarations for <paramref name="type"/>.  Declared fields/properties
        /// up the inheritance chain are included; static members are skipped.
        /// </summary>
        private static IReadOnlyList<NetworkVariableMetadata> GetOrBuildMetadata(Type type)
        {
            if (_variableMetadataCache.TryGetValue(type, out var cached)) return cached;

            var list = new List<NetworkVariableMetadata>();

            // Walk the inheritance chain so attributes declared on a base
            // class are honoured for subclasses too.  Stop at NetworkBehaviour
            // because anything above it (MonoBehaviour, Component, Object)
            // cannot legally hold NetworkVariable fields.
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;

            for (Type t = type; t != null && t != typeof(NetworkBehaviour); t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags))
                {
                    if (!typeof(NetworkVariableBase).IsAssignableFrom(f.FieldType)) continue;
                    var attr = f.GetCustomAttribute<NetworkVariableAttribute>(inherit: true);
                    if (attr == null) continue;
                    list.Add(new NetworkVariableMetadata(f, attr.SendRateHz));
                }

                foreach (var p in t.GetProperties(flags))
                {
                    if (!p.CanRead) continue;
                    if (!typeof(NetworkVariableBase).IsAssignableFrom(p.PropertyType)) continue;
                    var attr = p.GetCustomAttribute<NetworkVariableAttribute>(inherit: true);
                    if (attr == null) continue;
                    // Indexer properties are not supported (they require an index argument).
                    if (p.GetIndexParameters().Length != 0) continue;
                    list.Add(new NetworkVariableMetadata(p, attr.SendRateHz));
                }
            }

            IReadOnlyList<NetworkVariableMetadata> result = list;
            _variableMetadataCache[type] = result;
            return result;
        }

        /// <summary>
        /// Backward-compatibility shim that copies the cached buffer's
        /// leading <c>length</c> bytes into a fresh <c>byte[]</c> before
        /// forwarding to the caller.  Production hot-paths use the
        /// <see cref="FlushDirtyVariables(Func{byte[], int, bool})"/> overload
        /// directly to avoid this per-call copy; this overload exists for
        /// SDK test fixtures that pre-date the GC Round 2 (2026-05-02)
        /// signature change.  A sender of this shape cannot decline a
        /// payload, so it is taken as having sent it.
        /// </summary>
        internal void FlushDirtyVariables(Action<byte[]> sendPayload)
        {
            if (sendPayload == null) return;
            FlushDirtyVariables((buf, len) =>
            {
                var copy = new byte[len];
                if (len > 0) System.Buffer.BlockCopy(buf, 0, copy, 0, len);
                sendPayload(copy);
                return true;
            });
        }

        /// <summary>
        /// Serialize all dirty tracked variables into a single <c>VariableUpdate</c>
        /// payload and call <paramref name="sendPayload"/> with it.
        /// No-op when not spawned, not owner, or all variables are clean.
        /// Called by <c>NetworkManager.FlushDirtyNetworkVariables</c> at 30 Hz.
        /// </summary>
        /// <param name="sendPayload">
        /// Delegate that transmits the built payload bytes.  Receives
        /// <c>(buffer, length)</c> — only the leading <c>length</c> bytes of
        /// <c>buffer</c> are valid (the buffer is the cached MemoryStream's
        /// internal array, which may be larger than the logical payload).
        /// Implementations must NOT retain a reference to the buffer past
        /// the call: it is reused on the next flush tick.  Routes to
        /// <c>NetworkManager.SendVariableUpdate(byte[], int)</c> in the
        /// non-batching path, or <c>VariableBatchManager.CollectIntoBatch</c>
        /// (which copies into a per-pending entry) when batching is enabled.
        /// Answers whether it took the payload.  <see langword="false"/> is
        /// the replication share of the reliable window in flight: nothing
        /// was sent, and every variable this call serialised stays dirty —
        /// with its op log, for a list — to be offered again next tick, when
        /// it describes the state then.  A payload that was taken and then
        /// lost (an exception the sender contained) is answered
        /// <see langword="true"/>: the variables are clean and the loss is
        /// the sender's to count, as it always was.
        /// </param>
        internal void FlushDirtyVariables(Func<byte[], int, bool> sendPayload)
        {
            if (!IsOwner || !IsSpawned || _trackedVariables.Count == 0) return;

            // ── Fast path: skip allocation when nothing is dirty AND eligible.
            //
           // A variable is "eligible to flush this tick" when:
            //  • it has something to send — IsDirty == true, or it is due a
            //    periodic refresh — AND
            //  • either SendRateHz <= 0 (use global cadence; always eligible
            //    while dirty), OR (now - LastFlushTimeUnscaled) >= 1/SendRateHz.
            //
           // Throttled-but-dirty variables remain dirty until the next eligible
            // tick — the dirty flag is preserved across skipped flushes so the
            // most recent value is sent on the first allowed window.
            //
           // ⚠️ The refresh question is asked HERE as well as in the loop below,
            // because this is a bail and not a hint: a variable due a refresh is
            // by definition clean, so a probe that asked only about dirt would
            // return before the loop ran and the refresh would never fire for
            // the one state it exists for — a list that has gone quiet.
            //
           // It is the PURE question here and the arming one below. Arming
            // needs the room left in the payload, which does not exist yet; and
            // a probe that armed would commit a variable this method may then
            // decline to send at all.
            float now = UnityEngine.Time.unscaledTime;
            bool hasEligibleDirty = false;
            for (int i = 0; i < _trackedVariables.Count; i++)
            {
                var v = _trackedVariables[i];
                if (!v.IsDirty && !v.IsDueForPeriodicResync(now)) continue;
                if (!IsThrottleEligible(v, now)) continue;
                hasEligibleDirty = true;
                break;
            }
            if (!hasEligibleDirty) return;

            // Lazy-init cached stream + writer.  Reusing them across ticks
            // eliminates per-flush MemoryStream and BinaryWriter allocations
            // (previously ~700–900 B per call at 30 Hz × N objects).
            // InitialCapacity covers the common case without internal realloc:
            // object_id(8) + tick(4) + count(1) + ~15 variables at ~16 B each ≈ 253 bytes.
            const int InitialCapacity = 256;
            if (_flushMs == null)
            {
                _flushMs     = new MemoryStream(InitialCapacity);
                _flushWriter = new BinaryWriter(_flushMs, Encoding.UTF8, leaveOpen: true);
            }
            else
            {
                // Reset without deallocating the internal buffer — reuses the
                // previously grown capacity without any heap allocation.
                _flushMs.SetLength(0);
            }
            var ms     = _flushMs;
            var writer = _flushWriter;

            // [object_id:8 LE]
            writer.Write(NetworkObjectId);

            // [tick:4 LE] — the sender's current ReplicationTick, a monotonic
            // counter on real time and NOT the CSP LocalTick.  The receiver compares
            // against the per-variable last-applied tick (RFC 1982 modular
            // arithmetic) and rejects deltas whose tick is not strictly
            // greater than the highest tick already applied for that
            // (object, variable) pair.  Without this gate, a re-ordered or
            // late-arriving UDP datagram from a transient routing change
            // would silently overwrite newer state with older state.
            //
            // ⚠️ The gate is not unconditional.  The tick is the SENDER's, so
            // the watermark is meaningful only against the clock it was drawn
            // from; a sender whose watermark nothing has corroborated and which
            // has refused it for a full second is adopted.  See
            // TryAcceptInboundTick.
            //
            // ReplicationTick, not LocalTick: the two are unrelated counters on
            // different clocks, and this is the one that keeps advancing while
            // the game's own is stopped.
            uint flushTick = NetworkManager.Instance != null
                                 ? NetworkManager.Instance.ReplicationTick
                                 : 0u;
            writer.Write(flushTick);

            // Reserve space for var_count; written at the end with the real count.
            long countOffset = ms.Position;
            writer.Write((byte)0);

            // The loop below reads ms.Length to tell a refresh how much room is
            // left, and the budget reads it to decide whether an append fits.
            // BinaryWriter over a MemoryStream does not buffer today; a future
            // implementation that did would make both read short, and a room
            // figure that is too large is what arms a snapshot the payload
            // cannot carry.
            writer.Flush();

            // 🚨 The offer order ROTATES, and the budget below is why.  A
            // variable that does not fit beside the ones already written is
            // deferred to a later tick — and from a fixed starting index, "a
            // later tick" is never: two 700-byte variables both dirty every
            // tick leave the second deferred forever, silently, because
            // `count != 0` also suppresses the will-never-fit report.  Measured
            // at 1000 ticks: sent zero times, reported zero times.
            //
            // Advancing the start by one each flush costs an int and gives
            // every variable its turn at being first: two share the cadence,
            // three take one tick in three, and a variable that cannot fit even
            // alone reaches `count == 0` within one pass and is reported.
            // Emptied ahead of the walk rather than trusted: a serializer that
            // threw out of the previous flush left the list holding what that
            // flush had appended, and marking those clean on this tick's send
            // would retire changes this tick never carried.
            _flushAppended.Clear();

            byte count = 0;
            int  tracked = _trackedVariables.Count;
            if (_flushStartIndex >= tracked) _flushStartIndex = 0;
            for (int k = 0; k < tracked; k++)
            {
                int i = _flushStartIndex + k;
                if (i >= tracked) i -= tracked;

                var v = _trackedVariables[i];

                // A clean variable is skipped unless it is due a periodic
                // refresh, which only a NetworkVariableList ever is: its payload
                // is a delta against state the receiver is assumed to hold, so a
                // lost update is permanent and silent without one.  The decision
                // and its postcondition both live in the callee — a true answer
                // means the variable is now dirty — because this method is in a
                // MonoBehaviour partial no test project compiles.
                //
               // The room left in this tick's payload is part of the decision: a
                // refresh is a re-send of state the replica is assumed to hold,
                // so one that cannot be sent is worth nothing, and for a list it
                // is worth less than nothing — the queued snapshot would displace
                // every later delta until a flush succeeds.
                if (!v.IsDirty && !v.TryBeginPeriodicResync(
                        now,
                        RTMPE.Protocol.PacketBuilder.MaxApplicationPayloadBytes - (int)ms.Length,
                        count == 0))
                    continue;

                // Per-variable throttle: skip serialisation when the
                // configured send-rate window has not yet elapsed.  The dirty
                // flag is intentionally NOT cleared so the next eligible tick
                // still flushes the most recent value.
                if (!IsThrottleEligible(v, now)) continue;

                // Append it only if the result still fits one datagram.  The
                // send path refuses a payload past
                // PacketBuilder.MaxApplicationPayloadBytes rather than let IP
                // fragmentation carry it, and the guards that contain that
                // refusal run AFTER this loop has already marked the variable
                // clean — so an over-cap payload used to cost the update
                // outright, with nothing left to re-send it.  A variable that
                // does not fit is left dirty and untouched, and the payload is
                // left byte-identical to what it was.
                switch (RTMPE.Core.Sync.VariableFlushBudget.TryAppend(
                            ms, writer, v, count == 0,
                            RTMPE.Protocol.PacketBuilder.MaxApplicationPayloadBytes))
                {
                    case RTMPE.Core.Sync.VariableFlushBudget.Outcome.Deferred:
                        // Room ran out this tick; it is still dirty, and on a
                        // later tick it may be first in the payload and fit.
                        continue;

                    case RTMPE.Core.Sync.VariableFlushBudget.Outcome.Withheld:
                        // The variable refused to frame its own value and has
                        // already said why, in a line that names the type and
                        // the fault.  It stays dirty, so nothing it was carrying
                        // is retired by a send that did not include it — which
                        // for a list is its whole op log.
                        continue;

                    case RTMPE.Core.Sync.VariableFlushBudget.Outcome.TooLargeAlone:
                        // No tick will change the answer, so say so — once per
                        // second, because the variable stays dirty and is
                        // offered again on every one.
                        if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastOversizeVariableWarnTicks))
                            UnityEngine.Debug.LogWarning(
                                "[RTMPE] FlushDirtyVariables: variable id " +
                                $"{v.VariableId} on '{name}' (type {GetType().Name}) does not " +
                                "fit a single datagram even on its own and is NOT being sent. " +
                                "Reduce its size — a NetworkVariableString reaches the cap at " +
                                "about eleven hundred characters — or split it across several " +
                                "variables.");
                        continue;
                }

                // Not marked clean here.  The sender may decline the payload —
                // the replication share of the reliable window is in flight —
                // and a variable marked clean for a flush that never left has
                // lost its change, and for a list its op log with it, until
                // something writes it again.  The variables this tick
                // serialised are remembered and marked once the sender has
                // taken them; serialising costs nothing to retract, which is
                // why MarkClean and not Serialize retires a list's op log.
                _flushAppended.Add(v);
                count++;

                // VariableUpdate uses a single-byte count prefix; ensure we
                // never overflow it.  Any remaining dirty variables stay
                // dirty and are sent on the next tick.  This is a hard wire
                // limit; the alternative would be silent data corruption.
                if (count == byte.MaxValue)
                {
                    if (!_overflowWarned)
                    {
                        _overflowWarned = true;
                        UnityEngine.Debug.LogWarning(
                            "[RTMPE] FlushDirtyVariables: " +
                            $"NetworkBehaviour '{name}' (type {GetType().Name}) " +
                            $"hit the 255-variable wire-format cap; remaining dirty " +
                            "variables will be sent on later ticks. Consider splitting " +
                            "the variable set across multiple NetworkBehaviours so each " +
                            "object's flush fits in a single VariableUpdate packet.");
                    }
                    break;
                }
            }

            // Rotate the starting point for the next flush, so a variable
            // deferred behind a larger one this tick is offered ahead of it on
            // a later one.  Advanced whatever happened above, including on the
            // early return below: a flush where nothing fit is exactly the one
            // whose order most needs to change.
            _flushStartIndex = tracked == 0 ? 0 : (_flushStartIndex + 1) % tracked;

            // Nothing survived the budget.  Before it existed every eligible
            // dirty variable was written unconditionally, so this could not
            // happen; now a payload of one over-cap variable retracts to just
            // the header, and sending that would be an empty VariableUpdate on
            // every tick for as long as the variable stays too large.
            if (count == 0) return;

            // Flush the BinaryWriter so its internal buffer is fully committed to ms
            // BEFORE seeking back. BinaryWriter does not buffer in .NET Standard but
            // the Flush() guards against any future implementation change.
            writer.Flush();

            // Write back the actual variable count.
            // ms.ToArray() uses the stream's internal _length (= high-water mark),
            // which was set when we wrote the variable data, so seeking back here
            // to overwrite the placeholder does not truncate the payload.
            ms.Position = countOffset;
            writer.Write(count);
            writer.Flush();

            // GC Round 2 (2026-05-02): hand the cached MemoryStream's
            // backing buffer + written length to sendPayload instead of
            // copying via ms.ToArray().  The non-batching consumer
            // (SendVariableUpdate(byte[], int)) wraps only the leading
            // `length` bytes into a packet and never retains the
            // reference; the batching consumer (CollectIntoBatch) copies
            // into a per-pending entry before returning.  Both paths are
            // safe against the buffer being reused on the next tick.
            // ms.GetBuffer() returns the underlying array (possibly larger
            // than ms.Length); we always pass (int)ms.Length as the
            // payload length so the wire frame's payload_len matches the
            // bytes actually written.
            bool taken;
            try
            {
                taken = sendPayload(ms.GetBuffer(), (int)ms.Length);
            }
            catch
            {
                // A sender that throws has not taken the payload: the variables
                // stay dirty, and the fault travels to the fan-out, which
                // reports it.  Cleared here so the next tick's flush starts
                // from nothing.
                _flushAppended.Clear();
                throw;
            }

            if (taken)
            {
                for (int i = 0; i < _flushAppended.Count; i++)
                {
                    var v = _flushAppended[i];
                    v.MarkClean();
                    v.LastFlushTimeUnscaled = now;
                }
            }
            _flushAppended.Clear();
        }

        /// <summary>
        /// Force every tracked <see cref="NetworkVariableBase"/> back into the
        /// dirty set so the next 30 Hz flush transmits its current value, even
        /// when the stored value has not changed since the last send.
        ///
       /// <para>Used by the SDK to bootstrap late joiners: when another
        /// player joins the room, every existing owner client calls this on
        /// each of their owned objects so the new player sees a full state
        /// snapshot on the next tick instead of waiting for a future
        /// value-change event that may never come for static variables.</para>
        ///
       /// <para>Safe to call on non-owned or non-spawned objects — the dirty
        /// flag is still set, but <see cref="FlushDirtyVariables"/> is a no-op
        /// under those conditions so the flag will remain sticky until this
        /// object is owned + spawned again.  Callers that want to avoid that
        /// corner case should filter via <see cref="IsOwner"/> and
        /// <see cref="IsSpawned"/> before calling.</para>
        /// </summary>
        internal void MarkAllVariablesDirty()
        {
            // NetworkVariableBase.IsDirty setter is protected, so we use
            // the public MarkDirtyForResync hook below that each variable
            // exposes through its own public API via a new internal method.
            //
           // Resetting the per-variable throttle state alongside the dirty
            // flag is intentional: a late joiner must see the full snapshot
            // on the next eligible tick, not be blocked behind a stale
            // throttle window inherited from an earlier private send.
            //
           // Iterate a snapshot rather than the live list so a subscriber
            // callback that registers a NEW NetworkVariable during
            // MarkDirtyForResync (or unregisters one) cannot mutate the
            // underlying collection while the foreach is in progress.
            // Defensive — the SDK does not currently hand application code a
            // synchronous hook here, but that contract is not enforced by the
            // type system and a future refactor that introduces one must not
            // be able to throw InvalidOperationException out of a resync.
            var snapshot = new NetworkVariableBase[_trackedVariables.Count];
            _trackedVariables.CopyTo(snapshot, 0);
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].MarkDirtyForResync();
                snapshot[i].ResetThrottleState();
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="variable"/>'s
        /// configured per-variable send-rate cap permits a flush at
        /// <paramref name="nowUnscaled"/>.  A non-positive
        /// <see cref="NetworkVariableBase.SendRateHz"/> always returns true
        /// (the global flush cadence is the only gate).
        /// </summary>
        private static bool IsThrottleEligible(NetworkVariableBase variable, float nowUnscaled)
        {
            float rate = variable.SendRateHz;
            if (rate <= 0f) return true;

            float interval = 1f / rate;
            float since    = nowUnscaled - variable.LastFlushTimeUnscaled;

            // LastFlushTimeUnscaled == 0 covers two cases:
            //  • freshly registered (never flushed),
            //  • explicitly reset on ownership change / disconnect.
            // In both cases we want to flush immediately rather than wait out
            // a phantom interval against unscaled-time = 0.
            if (variable.LastFlushTimeUnscaled <= 0f) return true;

            // Use ">= interval - epsilon" so that when the global flush tick
            // and the per-variable interval line up exactly (e.g. SendRateHz
            // == 30 == global rate), we don't lose a tick to floating-point
            // jitter.  A 0.5 ms tolerance is well below any actual gameplay
            // tick rate (1/30 ≈ 33 ms) so it cannot cause double-fires.
            const float Epsilon = 0.0005f;
            return since >= interval - Epsilon;
        }

        // ── Client-Side Prediction hook ───────────────────────────────────────

        /// <summary>
        /// Client-side prediction: returns this tick's input on the owner.
        /// </summary>
        /// <remarks>
        /// <para>Only a <see cref="RTMPE.Sync.NetworkTransform"/> calls it, on itself, when its
        /// Enable Prediction setting is on: override it in a class derived from
        /// <c>NetworkTransform</c> and use that component. An override on any other
        /// <c>NetworkBehaviour</c> is never called.</para>
        /// <para>Leave <see cref="InputPayload.Tick"/> at <c>0</c>; the SDK fills it in. Return
        /// <c>default</c> when there is no input.</para>
        /// </remarks>
        /// <returns>This tick's input.</returns>
        protected virtual InputPayload GatherInput() => default;

        /// <summary>
        /// Client-side prediction: applies one input again after a correction from the server.
        /// </summary>
        /// <remarks>
        /// <para>Called, like <see cref="GatherInput"/>, only on a
        /// <see cref="RTMPE.Sync.NetworkTransform"/> itself: override both in the same derived
        /// class. After a correction, the transform is moved to the corrected pose and this is
        /// called once for each input the server had not yet confirmed.</para>
        /// <para>It must be deterministic, so the same pose and input always give the same
        /// result, and must use <paramref name="deltaTime"/>, not <c>Time.deltaTime</c>. The
        /// default does nothing.</para>
        /// </remarks>
        /// <param name="input">The input to apply.</param>
        /// <param name="deltaTime">The simulation step, in seconds
        /// (<see cref="NetworkManager.FixedTickInterval"/>).</param>
        protected virtual void ApplyInput(InputPayload input, float deltaTime) { }

        /// <summary>
        /// Collect this frame's input, stamp it with <paramref name="tick"/>,
        /// and return the result.  Called by <see cref="RTMPE.Sync.NetworkTransform"/>
        /// on the owning client.
        /// </summary>
        internal InputPayload CollectInput(uint tick)
        {
            var p  = GatherInput();
            p.Tick = tick;
            return p;
        }

        /// <summary>
        /// Internal entry point for the reconciliation replay loop.  Forwards
        /// to the protected virtual <see cref="ApplyInput"/> so subclasses can
        /// continue to define replay semantics with the standard access
        /// modifier while the SDK still drives the loop from outside the
        /// class hierarchy.
        /// </summary>
        internal void ReplayInput(InputPayload input, float deltaTime)
            => ApplyInput(input, deltaTime);

        /// <summary>
        /// Called once per simulation tick on every spawned object this client owns, with the
        /// tick interval. Use it for work that must keep a fixed cadence whatever the frame
        /// rate, such as sampling input.
        /// </summary>
        /// <remarks>
        /// Runs only while in a room, at <see cref="NetworkSettings.tickRate"/>. A long frame
        /// calls it several times and a short frame not at all; at most 8 ticks run per frame,
        /// and the surplus is dropped. It follows <c>Time.timeScale</c> and stops while it is
        /// 0. An exception it throws is caught and logged.
        /// </remarks>
        /// <param name="deltaTime">The tick interval, in seconds.</param>
        protected virtual void OnFixedTick(float deltaTime) { }

        /// <summary>
        /// SDK-internal forwarder so the tick driver can invoke the protected
        /// virtual without exposing it on the public surface.
        /// </summary>
        internal void InvokeOnFixedTick(float deltaTime) => OnFixedTick(deltaTime);

        // ── Variable update (server → client) ────────────────────────────────

        /// <summary>
        /// Apply a single variable update received from the server.
        /// Called by <c>NetworkManager.HandleVariableUpdatePacket</c> for each
        /// [var_id:4 LE][value_len:2 LE][value_bytes:N] entry in the payload.
        ///
       /// <paramref name="valueLen"/> is used by the caller to advance the
        /// stream past the value bytes regardless of what this method reads,
        /// guaranteeing subsequent variables in the same packet are parsed from
        /// correct offsets even on unknown-ID or schema-mismatch scenarios.
        /// </summary>
        internal void ApplyVariableUpdate(uint variableId, BinaryReader reader, ushort valueLen = 0)
        {
            ApplyVariableUpdate(variableId, reader, valueLen, packetTick: 0u, hasPacketTick: false);
        }

        /// <summary>
        /// Tick-aware overload.  Drops the deserialised value when
        /// <paramref name="hasPacketTick"/> is set and
        /// <paramref name="packetTick"/> is not strictly greater than the
        /// highest tick already applied to the matching variable — save for
        /// the bounded exception stated on
        /// <see cref="NetworkVariableBase.TryAcceptInboundTick"/>, where a
        /// sender refused for a full second is read as a clock the watermark
        /// was never drawn from, and is adopted.
        /// </summary>
        /// <remarks>
        /// ⚠️ <paramref name="valueLen"/> is NOT read here, and this method does
        /// not seek.  The caller owns the stream, so the caller is where the
        /// entry's length is enforced — `NetworkManager.GameData.cs` hands this
        /// a reader over exactly that many bytes
        /// (<c>VariableEntryReader</c>) and re-seeks the batch afterwards.
        /// The parameter is kept because it is the length this reader is
        /// supposed to be bounded to, and a caller that has to pass it is a
        /// caller that has been asked the question.  It is not the mechanism,
        /// and a reader handed in unbounded will read past its entry exactly as
        /// it always did.
        /// </remarks>
        internal void ApplyVariableUpdate(
            uint         variableId,
            BinaryReader reader,
            ushort       valueLen,
            uint         packetTick,
            bool         hasPacketTick)
        {
            // The packet names an object, and the id it carries belongs to
            // whichever of that object's components declared it — the registry
            // anchor is where the update arrives, not where it necessarily
            // lands.  Ids are unique across the object, so the first holder is
            // the only holder.
            if (ObjectDispatchOps.RouteVariableUpdate(
                    ObjectComponents, variableId, reader, packetTick, hasPacketTick))
                return;

            // Unknown ID: warn but do NOT read — the caller will skip valueLen bytes.
            //
            // Rate-gated, and the gate is static: the dispatch loop above runs
            // once per variable a single wire byte declares, so an id nobody
            // registered costs one line per variable — and the count of objects
            // a sender may name is bounded only by the datagram. A per-instance
            // gate would still let a packet naming many objects emit once each.
            if (WarnGate.ShouldEmit(ref _lastUnknownVariableIdWarnTicks))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkBehaviour: unknown variableId {variableId} in VariableUpdate — " +
                    "skipping value bytes. An identity is derived from the CONCRETE type of the " +
                    "object and the name the variable is constructed with, so it agrees across " +
                    "peers that instantiate the same type: this is a peer built from source " +
                    "where that type or member was renamed, a component absent from this " +
                    "object, or the same prefab carrying a different type here than at the " +
                    "sender — a base where the sender has a derived class, most often.");
            }
        }

        /// <summary>
        /// Whether this component holds <paramref name="variableId"/>.  Read
        /// during registration to keep an object's ids distinct across its
        /// components.
        /// </summary>
        internal bool TracksVariable(uint variableId)
        {
            for (int i = 0; i < _trackedVariables.Count; i++)
            {
                if (_trackedVariables[i].VariableId == variableId) return true;
            }
            return false;
        }

        /// <summary>
        /// Apply an inbound value to this component's copy of
        /// <paramref name="variableId"/>.  Returns whether the id is one of this
        /// component's — <see langword="true"/> for a value dropped as stale as
        /// well, because the id resolved here and the delta is spent either way.
        /// </summary>
        internal bool TryApplyVariableUpdate(
            uint         variableId,
            BinaryReader reader,
            uint         packetTick,
            bool         hasPacketTick)
        {
            foreach (var v in _trackedVariables)
            {
                if (v.VariableId != variableId) continue;

                // Reject older / duplicate deltas before touching the wire
                // value.  Note we still let the caller advance past the
                // valueLen bytes — the seek is owned by the dispatch loop in
                // NetworkManager.HandleVariableUpdatePacket so all variables
                // in the same packet are framed correctly even when one is
                // gated out here.
                // Unscaled, because the bound is about how long a sender has
                // gone unheard and a paused game does not change that.
                if (hasPacketTick
                    && !v.TryAcceptInboundTick(packetTick, Time.unscaledTimeAsDouble))
                    return true;

                v.ApplyInbound(reader);
                _inboundValuesDelivered++;
                _inboundValuesUnderCurrentOwner++;
                return true;
            }
            return false;
        }
    }
}
