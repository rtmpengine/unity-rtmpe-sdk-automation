using System.Runtime.CompilerServices;

// The scorer's analyzer list is fixed by design, so the one path that cannot be
// reached from any input — a shipped analyzer THROWING — is driven through an
// internal seam the analyzer shard alone can see.
[assembly: InternalsVisibleTo("RTMPE.SDK.Analyzers.Tests")]
