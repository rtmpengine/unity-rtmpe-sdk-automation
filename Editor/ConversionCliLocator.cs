// RTMPE SDK — Editor/ConversionCliLocator.cs
//
// Locates the headless conversion host the Conversion Wizard drives.
//
// The wizard rewrites source through the same engine the Makefile verbs use, so
// that a wizard edit and a `make fix` edit are the same edit.  That engine is a
// .NET project, not a Unity assembly: it is part of the RTMPE repository, not of
// the UPM package, so a project that consumed the package alone has nothing for
// the wizard to run.  Resolution therefore has to answer two questions
// separately — where the host is, and, when it is nowhere, what the author can
// do about it — because those have different answers for the two audiences that
// open this window: someone working inside the repository checkout, and someone
// who installed the package into a project of their own.
//
// Kept apart from the EditorWindow, and free of UnityEngine/UnityEditor, so the
// ordering and the fallbacks are exercised by the off-Editor test shard; the
// window itself is GUI-bound and compiles only inside Unity.  Mirrors
// WizardConfigValidator in the same folder.

using System.Collections.Generic;
using System.IO;

namespace RTMPE.Editor
{
    /// <summary>
    /// Resolution of the conversion host's project directory, and the guidance
    /// shown when it cannot be resolved.  Stateless: every input arrives as an
    /// argument so the same contract holds inside Unity and under the test shard.
    /// </summary>
    internal static class ConversionCliLocator
    {
        /// <summary>Directory name of the conversion host project.</summary>
        internal const string CliProjectName = "RTMPE.SDK.ConversionCli";

        /// <summary>
        /// EditorPrefs key holding an author-supplied path to the host project.
        /// Per-user rather than per-project: the checkout lives at a different
        /// place on each machine, so committing the path would be wrong for
        /// everyone but its author.
        /// </summary>
        internal const string OverridePrefKey = "RTMPE.Conversion.CliProjectPath";

        /// <summary>
        /// Candidate directories, in the order they are consulted:
        /// an explicit override first, so an author who has the tooling in an
        /// unusual place is never overruled by a layout guess; then the
        /// repository layout, where the Unity project sits two levels under the
        /// repository root.  Entries are returned whether or not they exist —
        /// <see cref="Resolve"/> applies the existence test, and a caller
        /// reporting failure needs the paths that were tried.
        /// </summary>
        internal static IReadOnlyList<string> Candidates(string projectRoot, string overridePath)
        {
            var candidates = new List<string>(2);

            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                candidates.Add(Path.GetFullPath(overridePath.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(
                    projectRoot, "..", "..", "clients", "unity-sdk", "Tooling", CliProjectName)));
            }

            return candidates;
        }

        /// <summary>
        /// First candidate that exists, or <see langword="null"/> when none does.
        /// <paramref name="exists"/> is the directory test, injected so the shard
        /// can drive the ordering without touching the file system.
        /// </summary>
        internal static string Resolve(
            string projectRoot, string overridePath, System.Func<string, bool> exists)
        {
            if (exists is null)
            {
                return null;
            }

            foreach (var candidate in Candidates(projectRoot, overridePath))
            {
                if (exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Directory the host's build and run are launched from.
        ///
        /// <para>Only one thing depends on it: <c>dotnet</c> discovers
        /// <c>global.json</c> by walking up from the working directory, and the
        /// host's own <c>global.json</c> pins an exact SDK for byte-compared
        /// analyzer builds — a constraint that must not be imposed on the
        /// author's machine.  Every path handed to <c>dotnet</c> is absolute, so
        /// nothing else reads this.</para>
        ///
        /// <para>The requirement is therefore "outside the folder the host sits
        /// in", and the answer is that folder's parent — one level above the
        /// host's own directory would still be inside it.  Deriving it from the
        /// host rather than from a fixed depth keeps a host in an unusual place
        /// from launching at an unrelated directory.</para>
        /// </summary>
        internal static string WorkingDirectoryFor(string hostPath)
        {
            if (string.IsNullOrWhiteSpace(hostPath))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(hostPath.Trim(), "..", ".."));
        }

        /// <summary>
        /// The message shown when no candidate resolved.  Names every path tried,
        /// states the precondition that a package-only install does not meet, and
        /// gives the two ways forward — point the wizard at a checkout, or convert
        /// by hand — so the window is not a dead end for the audience that cannot
        /// satisfy the precondition.
        /// </summary>
        internal static string UnavailableMessage(IReadOnlyList<string> tried)
        {
            var text = new System.Text.StringBuilder();
            text.Append("The conversion host was not found.\n\n");

            if (tried != null && tried.Count > 0)
            {
                text.Append("Looked in:\n");
                foreach (var path in tried)
                {
                    text.Append("  • ").Append(path).Append('\n');
                }
                text.Append('\n');
            }

            text.Append(
                "The wizard rewrites source through the repository's headless conversion "
                + "host, which ships with the RTMPE repository rather than with this "
                + "package — so a project that installed the package alone has nothing to "
                + "run here.\n\n"
                + "Either point the wizard at a checkout below, or convert by hand: "
                + "Documentation~/diagnostics.md gives the exact shape each rule expects, "
                + "per rule, including the ones whose quick fix is deliberately withheld.");

            return text.ToString();
        }
    }
}
