namespace RTMPE.SDK.Analyzers
{
    /// <summary>Stable diagnostic ids surfaced by the RTMPE analyzers.</summary>
    public static class DiagnosticIds
    {
        /// <summary>A type inherits <c>RTMPE.Core.NetworkBehaviour</c> (informational).</summary>
        public const string NetworkBehaviourSubtype = "RTMPE1000";

        /// <summary>An <c>[RtmpeRpc]</c> method must be a public instance method.</summary>
        public const string RpcMustBePublicInstance = "RTMPE1001";

        /// <summary>An <c>[RtmpeRpc]</c> parameter type is not serializable by <c>RpcSerializer</c>.</summary>
        public const string RpcUnsupportedParameterType = "RTMPE1002";

        /// <summary>Two <c>[RtmpeRpc]</c> methods on one type resolve to the same method id.</summary>
        public const string RpcDuplicateMethodId = "RTMPE1003";

        /// <summary>An <c>[RtmpeRpc]</c> method id collides with a reserved built-in id.</summary>
        public const string RpcReservedMethodIdCollision = "RTMPE1004";

        /// <summary><c>[RtmpeRpc]</c> may only be declared on a <c>NetworkBehaviour</c> subclass.</summary>
        public const string RpcRequiresNetworkBehaviour = "RTMPE1005";

        /// <summary>An [RtmpeRpc] method whose shape the reflection dispatcher cannot invoke.</summary>
        public const string RpcUndispatchableShape = "RTMPE1006";

        /// <summary>Two <c>NetworkVariable</c>s on one type derive one identity.</summary>
        public const string NetworkVariableDuplicateId = "RTMPE1010";

        /// <summary>A <c>NetworkVariable</c> is constructed outside <c>OnNetworkSpawn</c>.</summary>
        public const string NetworkVariableConstructedOutsideSpawn = "RTMPE1011";

        /// <summary>A <c>NetworkVariableQuaternion</c> is initialised to <c>default</c>, not <c>Quaternion.identity</c>.</summary>
        public const string NetworkVariableQuaternionDefault = "RTMPE1012";

        /// <summary><c>[NetworkVariable]</c> on a member whose type is not a <c>NetworkVariableBase</c>.</summary>
        public const string NetworkVariableAttributeOnNonVariable = "RTMPE1013";

        /// <summary>An overridden <c>OnDestroy</c> does not call <c>base.OnDestroy()</c>.</summary>
        public const string LifecycleMissingBaseOnDestroy = "RTMPE1020";

        /// <summary>A method has a lifecycle-hook name but a signature that does not override it.</summary>
        public const string LifecycleHookSignatureMismatch = "RTMPE1021";

        /// <summary>
        /// An overridden <c>OnNetworkSpawn</c> does not call
        /// <c>base.OnNetworkSpawn()</c> while an ancestor overrides it.
        /// </summary>
        public const string LifecycleMissingBaseOnNetworkSpawn = "RTMPE1022";

        /// <summary>A <c>MonoBehaviour</c> holds plain replicable state — a <c>NetworkBehaviour</c> rebase candidate.</summary>
        public const string ConversionRebaseCandidate = "RTMPE2001";

        /// <summary>A field is a NetworkVariable conversion candidate (in-place for plain private state; companion for written serialized config).</summary>
        public const string ConversionNetworkVariableCandidate = "RTMPE2002";

        /// <summary>A frame loop on a <c>NetworkBehaviour</c> writes this object's own state and has no <c>if (!IsOwner) return;</c> guard, so every client runs it.</summary>
        public const string ConversionMissingOwnerGuard = "RTMPE2003";

        /// <summary>An owner-guarded state-mutating method is an Enhanced-RPC conversion candidate.</summary>
        public const string ConversionRpcCandidate = "RTMPE2004";

        /// <summary>An auto-property on a <c>NetworkBehaviour</c> holds replicable state in a compiler-generated field no conversion can retype.</summary>
        public const string ConversionAutoPropertyState = "RTMPE2005";

        /// <summary>A component type's advisory authority classification (Phase 5).</summary>
        public const string AuthorityClassification = "RTMPE9001";
    }
}
