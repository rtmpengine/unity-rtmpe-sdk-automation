using System.Runtime.CompilerServices;

// The write-time readback guard is deliberately unreachable through the verbs —
// names are validated where they enter — so its own failure path can only be
// exercised across an internal seam. Testing it through a CLI invocation would
// mean weakening the entry validation that makes it a last line rather than a
// first one.
[assembly: InternalsVisibleTo("RTMPE.SDK.ConversionGolden.Tests")]
