using System.Runtime.CompilerServices;

// The test shard exercises the guard's collision-classification paths through
// an internal seam so reserved membership can be supplied independently of the
// canonical reserved set.
[assembly: InternalsVisibleTo("RTMPE.SDK.Analyzers.Tests")]
