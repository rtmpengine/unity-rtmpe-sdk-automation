using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// The metadata reference set an in-memory compilation needs to resolve the
    /// base library cleanly.
    ///
    /// Every host that compiles a stub in-process — the readiness scorer, the
    /// advisory compile gate, and the test harnesses — needs the running
    /// framework's trusted-platform assemblies, or its compilation is littered
    /// with unresolved-type errors that mask the diagnostics under test. The load
    /// was previously written out at each of those sites; a difference between two
    /// of them would mean two judges compiling against different base libraries,
    /// which is exactly the silent divergence a shared surface exists to prevent.
    /// </summary>
    public static class CompilationReferences
    {
        /// <summary>
        /// The framework's trusted-platform assemblies as metadata references, or a
        /// single <c>System.Private.CoreLib</c> reference when the host does not
        /// publish the list. Returned as a neutral read-only list; a caller wanting
        /// an <c>ImmutableArray</c> materialises one at its own call site.
        /// </summary>
        public static IReadOnlyList<MetadataReference> TrustedPlatform()
        {
            var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrEmpty(trusted))
            {
                return new MetadataReference[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                };
            }

            return trusted
                .Split(Path.PathSeparator)
                .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray();
        }
    }
}
