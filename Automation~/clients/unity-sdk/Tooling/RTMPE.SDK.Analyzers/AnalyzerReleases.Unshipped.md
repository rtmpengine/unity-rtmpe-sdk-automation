; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RTMPE1006 | RTMPE.Rpc | Error | An [RtmpeRpc] method shape (generic, or ref/out/in parameter) the reflection dispatcher can never invoke
RTMPE2002 | RTMPE.Conversion | Info | A field is a NetworkVariable conversion candidate
RTMPE2004 | RTMPE.Conversion | Info | An owner-guarded state-mutating method is an Enhanced-RPC conversion candidate
RTMPE2005 | RTMPE.Conversion | Info | An auto-property holds replicable state in a compiler-generated field no conversion can retype
RTMPE9001 | RTMPE.Authority | Info | A component type's advisory authority classification
