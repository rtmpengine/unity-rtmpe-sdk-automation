// RTMPE SDK — Editor/DotnetExecutableLocator.cs
//
// Which `dotnet` the conversion engine is launched as.
//
// A GUI application does not inherit the shell's PATH. On macOS the .NET
// installer puts `dotnet` in /usr/local/share/dotnet with a symlink in
// /usr/local/bin, and a Unity launched from the Finder or the Dock has neither
// — so `dotnet` is installed, works in every terminal, and cannot be started
// from the window. The documented answer has been to relaunch Unity from a
// terminal, and testers keep rediscovering it.
//
// 🔑 PATH is still asked FIRST and, when it answers, the bare name is what gets
// launched — byte-identical to what this code did before. A developer who has
// arranged which `dotnet` is on PATH keeps that arrangement; the fallback only
// exists for the window where PATH answers nothing at all.
//
// ⛔ And when nothing answers, the bare name is returned unchanged rather than a
// guess: the launch then fails exactly as it did, with the message that already
// explains PATH and the Finder. A locator that invented a path would replace a
// good diagnostic with a confusing one.

using System;
using System.Collections.Generic;
using System.IO;

namespace RTMPE.Editor
{
    /// <summary>
    /// Resolves the executable name used to launch the conversion engine.
    /// </summary>
    internal static class DotnetExecutableLocator
    {
        /// <summary>
        /// The bare name, resolved by the operating system against PATH. What
        /// this locator returns whenever PATH can answer, and what it falls back
        /// to when nothing can.
        /// </summary>
        internal const string OnPath = "dotnet";

        private static readonly string[] ExecutableNames = { "dotnet", "dotnet.exe" };

        /// <summary>
        /// The places an installer puts <c>dotnet</c>, in the order they are
        /// tried.
        /// </summary>
        /// <param name="dotnetRoot">The <c>DOTNET_ROOT</c> environment variable, or null.</param>
        /// <param name="home">The user's profile directory, or null.</param>
        /// <remarks>
        /// <c>DOTNET_ROOT</c> comes first because it is the one entry the user
        /// stated deliberately. The rest are what the official installers write,
        /// in this order: the macOS package and its symlink, Homebrew on Apple
        /// Silicon and on Intel, the two Linux package layouts, and the per-user
        /// directory the dotnet-install script uses.
        ///
        /// <para>⚠️ Two of them — <c>/usr/local/bin</c> and
        /// <c>/opt/homebrew/bin</c> — are writable by the logged-in user on a
        /// typical Mac. That is the same trust boundary the editor already runs
        /// under, and it is reached only when PATH answers nothing at all; it is
        /// written down because a fallback that executes from a user-writable
        /// directory should be a decision somebody made, not one they inherited.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<string> Candidates(string dotnetRoot, string home)
        {
            var candidates = new List<string>();

            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                AddNames(candidates, dotnetRoot);
            }

            candidates.Add("/usr/local/share/dotnet/dotnet");   // macOS installer
            candidates.Add("/usr/local/bin/dotnet");            // macOS installer's symlink
            candidates.Add("/opt/homebrew/bin/dotnet");         // Homebrew, Apple Silicon
            candidates.Add("/usr/local/opt/dotnet/bin/dotnet"); // Homebrew, Intel
            candidates.Add("/usr/lib/dotnet/dotnet");           // Debian/Ubuntu package
            candidates.Add("/usr/share/dotnet/dotnet");         // Microsoft package feed

            if (!string.IsNullOrEmpty(home))
            {
                AddNames(candidates, Path.Combine(home, ".dotnet")); // dotnet-install script
            }

            return candidates;
        }

        /// <summary>
        /// The executable to launch: the bare name when PATH can find it, an
        /// absolute path when it cannot and an installer's is present, and the
        /// bare name again when neither answers.
        /// </summary>
        /// <param name="pathVariable">The process's <c>PATH</c>, or null.</param>
        /// <param name="dotnetRoot">The <c>DOTNET_ROOT</c> variable, or null.</param>
        /// <param name="home">The user's profile directory, or null.</param>
        /// <param name="exists">Whether a given absolute path is a file.</param>
        internal static string Resolve(
            string pathVariable, string dotnetRoot, string home, Func<string, bool> exists)
        {
            if (exists == null)
            {
                return OnPath;
            }

            if (OnPathAlready(pathVariable, exists))
            {
                return OnPath;
            }

            foreach (string candidate in Candidates(dotnetRoot, home))
            {
                if (exists(candidate))
                {
                    return candidate;
                }
            }

            return OnPath;
        }

        /// <summary>
        /// Whether PATH already names a directory holding the executable.
        /// </summary>
        /// <remarks>
        /// Asked before the candidates so that a machine whose PATH works keeps
        /// launching exactly what it launched before — including a `dotnet`
        /// earlier on PATH than any location listed here.
        /// </remarks>
        private static bool OnPathAlready(string pathVariable, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(pathVariable))
            {
                return false;
            }

            foreach (string entry in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                foreach (string name in ExecutableNames)
                {
                    // A malformed PATH entry — one carrying a character the
                    // platform forbids — is a reason to skip that entry, never
                    // to abandon the search: the entry after it may be the one
                    // that answers.
                    //
                    // ⚠️ Unreachable on .NET Core, which stopped validating path
                    // characters, and reachable on the .NET Standard 2.1 runtime
                    // Unity 2022.3 ships — where the documented behaviour is an
                    // ArgumentException. The shard runs the first of those, so
                    // no test here enters this branch; it is written for the
                    // runtime that does.
                    string probe;
                    try
                    {
                        probe = Path.Combine(entry, name);
                    }
                    catch (ArgumentException)
                    {
                        break;
                    }

                    if (exists(probe))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void AddNames(List<string> candidates, string directory)
        {
            foreach (string name in ExecutableNames)
            {
                try
                {
                    candidates.Add(Path.Combine(directory, name));
                }
                catch (ArgumentException)
                {
                    return;
                }
            }
        }
    }
}
