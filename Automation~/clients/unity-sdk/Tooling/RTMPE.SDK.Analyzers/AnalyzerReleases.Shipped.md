; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.9.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RTMPE1000 | RTMPE.Usage | Info | Type inherits RTMPE.Core.NetworkBehaviour
RTMPE1001 | RTMPE.Rpc | Error | [RtmpeRpc] method must be public instance
RTMPE1002 | RTMPE.Rpc | Error | [RtmpeRpc] parameter type is not serializable
RTMPE1003 | RTMPE.Rpc | Error | [RtmpeRpc] methods on one type share a method id
RTMPE1004 | RTMPE.Rpc | Error | [RtmpeRpc] method id collides with a reserved id
RTMPE1005 | RTMPE.Rpc | Error | [RtmpeRpc] must be declared on a NetworkBehaviour
RTMPE1010 | RTMPE.Sync | Error | NetworkVariables on one type must not derive one identity
RTMPE1011 | RTMPE.Sync | Warning | NetworkVariable should be constructed in OnNetworkSpawn
RTMPE1012 | RTMPE.Sync | Warning | NetworkVariableQuaternion should be initialised to Quaternion.identity
RTMPE1013 | RTMPE.Sync | Warning | [NetworkVariable] must sit on a NetworkVariableBase member
RTMPE1020 | RTMPE.Lifecycle | Error | Overridden OnDestroy must call base.OnDestroy()
RTMPE1021 | RTMPE.Lifecycle | Warning | A lifecycle-hook name must carry the hook's signature
RTMPE1022 | RTMPE.Lifecycle | Error | Overridden OnNetworkSpawn must call base.OnNetworkSpawn() when an ancestor overrides it
RTMPE2001 | RTMPE.Conversion | Info | MonoBehaviour with replicable state is a NetworkBehaviour rebase candidate
RTMPE2003 | RTMPE.Conversion | Info | A frame loop drives simulation without an IsOwner guard
