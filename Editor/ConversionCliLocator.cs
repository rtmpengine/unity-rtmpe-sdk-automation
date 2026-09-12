// RTMPE SDK — Editor/ConversionCliLocator.cs
//
// Locates the headless conversion host the Conversion Wizard drives.
//
// The wizard rewrites source through the same engine the Makefile verbs use, so
// that a wizard edit and a `make fix` edit are the same edit.  That engine is a
// .NET project, not a Unity assembly, so it travels inside the package under a
// folder the asset pipeline skips rather than as compiled code — and a checkout
// carries the live source of the same engine one directory tree over.
// Resolution therefore has to answer two questions separately — where the host
// is, and, when nothing resolves, what the author can do about it — because the
// first has different answers for the two audiences that open this window:
// someone working inside the repository checkout, and someone who installed the
// package into a project of their own.
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
        // ⛔ Scoped: this holds an ABSOLUTE PATH and Resolve() consults it ahead
        // of the layout guess, so under a fixed name setting it in one project
        // silently pointed every other RTMPE project's conversion CLI at the
        // first one's tooling.
        internal static string OverridePrefKey => EditorProjectScope.Scoped(BaseOverridePrefKey);
        internal const  string BaseOverridePrefKey = "RTMPE.Conversion.CliProjectPath";

        /// <summary>
        /// Directory inside the package that carries the automation kit.  The
        /// trailing <c>~</c> is what makes shipping the engine possible at all:
        /// Unity's asset pipeline ignores such a folder outright, so the kit's
        /// Roslyn-dependent sources are never imported and never compiled by the
        /// Editor that carries them.
        /// </summary>
        internal const string PackageAutomationFolder = "Automation~";

        /// <summary>
        /// Candidate directories, in the order they are consulted:
        /// an explicit override first, so an author who has the tooling in an
        /// unusual place is never overruled by a layout guess; then the
        /// repository layout, where the Unity project sits two levels under the
        /// repository root; then the copy shipped inside the package itself.
        /// Entries are returned whether or not they exist —
        /// <see cref="Resolve"/> applies the existence test, and a caller
        /// reporting failure needs the paths that were tried.
        /// <para>
        /// The repository layout is consulted before the shipped copy on
        /// purpose.  Inside a checkout both resolve, and there the engine under
        /// <c>Tooling/</c> is the live source while the package&apos;s is a
        /// published copy of it: an author editing a transform expects the
        /// wizard to run what they just edited.  Outside a checkout only the
        /// shipped copy exists, so the ordering costs that reader nothing.
        /// </para>
        /// </summary>
        internal static IReadOnlyList<string> Candidates(string projectRoot, string overridePath)
            => Candidates(projectRoot, overridePath, null);

        /// <summary>
        /// As <see cref="Candidates(string,string)"/>, with the resolved
        /// location of this package on disk.  Supplied by the Editor rather
        /// than derived here because a package may be installed from a
        /// registry, from git or from a local folder, and only the package
        /// manager knows which — while this type must stay free of
        /// <c>UnityEditor</c> so the shard can drive it.
        /// </summary>
        internal static IReadOnlyList<string> Candidates(
            string projectRoot, string overridePath, string packageRoot)
        {
            var candidates = new List<string>(3);

            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                candidates.Add(Path.GetFullPath(overridePath.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(
                    projectRoot, "..", "..", "clients", "unity-sdk", "Tooling", CliProjectName)));
            }

            if (!string.IsNullOrWhiteSpace(packageRoot))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(
                    packageRoot, PackageAutomationFolder,
                    "clients", "unity-sdk", "Tooling", CliProjectName)));
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
            => Resolve(projectRoot, overridePath, null, exists);

        /// <summary>
        /// As <see cref="Resolve(string,string,System.Func{string,bool})"/>,
        /// including the copy shipped inside the package at
        /// <paramref name="packageRoot"/>.
        /// </summary>
        internal static string Resolve(
            string projectRoot, string overridePath, string packageRoot,
            System.Func<string, bool> exists)
        {
            if (exists is null)
            {
                return null;
            }

            foreach (var candidate in Candidates(projectRoot, overridePath, packageRoot))
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
                "The wizard rewrites source through the headless conversion host. That host "
                + "travels inside this package under " + PackageAutomationFolder + " — it is "
                + "a .NET project, which Unity cannot compile, so it sits where the asset "
                + "pipeline leaves it alone. None of the paths above resolved, which leaves "
                + "two causes: this install is missing that copy, or the Browse path below "
                + "was recorded in an earlier session and no longer names a "
                + CliProjectName + " folder.\n\n"
                + "Three ways forward, in order of least work:\n\n"
                + "  1. Clear the Browse path below if one is set: it is consulted ahead of "
                + "everything else, so a stale entry hides the copy that ships with the "
                + "package.\n"
                + "  2. If you have a clone of the RTMPE repository, press Browse below and "
                + "select the " + CliProjectName + " folder inside it.\n"
                + "  3. Convert by hand: Documentation~/diagnostics.md gives the exact shape "
                + "each rule expects, per rule, including the ones whose quick fix is "
                + "deliberately withheld.\n\n"
                + "Either host needs the .NET 8 SDK on PATH — and a Unity launched from the "
                + "Finder or the Dock does not inherit your shell's PATH, so `dotnet` can be "
                + "installed and still be unreachable from here. "
                + "Documentation~/automation.md covers all of it.");

            return text.ToString();
        }
    }
}
