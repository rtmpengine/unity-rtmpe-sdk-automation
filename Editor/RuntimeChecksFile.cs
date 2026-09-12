// RTMPE SDK — Editor/RuntimeChecksFile.cs
//
// The project's record of what a RUN established — the second result, beside
// the static readiness score rather than inside it.
//
// 🔑 Its own file, and an INPUT to the scan — never the artifact.  The readiness
// artifact is rewritten whole by every run, so an outcome stored there would
// survive exactly until the next scan, which is the run that was supposed to
// read it.  What this file writes, `readiness --runtime` reads.
//
// ⛔ This window is the SECOND writer.  The first is the load harness, which
// records what it measured; this one records what the developer saw with their
// own two clients.  Both write the same three evidence fields, and the reader
// refuses an outcome missing any of them — which is why `Record` takes the
// observer and the version rather than inventing either.
//
// The file NAME is not stated here: it arrives in the artifact this window has
// already loaded (`runtimeFile`), so the tool remains its single author.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Reads and writes <c>network-runtime-checks.json</c>: one recorded outcome
    /// per runtime check a run has reported on.
    /// </summary>
    // JsonUtility binds these by reflection — see the note in
    // ReadinessArtifactData.cs for why the suppression is scoped here.
#pragma warning disable CS0649
    [Serializable]
    internal sealed class RuntimeChecksFile
    {
        public List<Entry> checks = new List<Entry>();

        [Serializable]
        internal sealed class Entry
        {
            public string check;
            public string result;
            public string observedBy;
            public string observedAt;
            public string sdkVersion;
            public string detail;
        }

        /// <summary>
        /// What the window writes into <c>observedBy</c> for an outcome the
        /// developer recorded themselves.
        /// </summary>
        /// <remarks>
        /// ⚠️ Deliberately distinguishable from a harness name. The tool cannot
        /// verify either one — a record is a record — but a reader can tell a
        /// measured scenario from somebody's recollection only if the two are
        /// written differently, and hiding the difference would be the tool
        /// claiming more than it knows.
        /// </remarks>
        public const string Developer = "developer";

        /// <summary>
        /// Reads the file, or an empty set when it is not there yet. A file that
        /// exists and cannot be read returns null with an error: overwriting it
        /// would destroy outcomes this window never saw.
        /// </summary>
        public static RuntimeChecksFile Load(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "no runtime record path — the readiness artifact does not name one.";
                return null;
            }

            if (!File.Exists(path))
            {
                return new RuntimeChecksFile();
            }

            try
            {
                string text = File.ReadAllText(path);
                if (string.IsNullOrEmpty(text.Trim()))
                {
                    return new RuntimeChecksFile();
                }

                var parsed = JsonUtility.FromJson<RuntimeChecksFile>(text);
                if (!IsUsable(parsed))
                {
                    error = "The runtime record parsed but carries no checks array: " + path;
                    return null;
                }

                return parsed;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>
        /// Whether a parsed document is one this window may write back over.
        /// </summary>
        /// <remarks>
        /// 🔑 Split out because the second limb is unreachable from the test
        /// shard: its <c>JsonUtility.FromJson</c> stub returns
        /// <see langword="default"/> unconditionally, so <c>parsed == null</c>
        /// short-circuits every case and weakening the rest survived a full green
        /// suite. Under the real serialiser <c>{}</c> parses to a NON-null object
        /// with a null list — which is the limb that decides whether an
        /// unreadable record gets overwritten.
        /// </remarks>
        public static bool IsUsable(RuntimeChecksFile parsed)
            => parsed != null && parsed.checks != null;

        /// <summary>
        /// The merge rule, in memory: one outcome replaces its own check's entry
        /// and touches no other. A null or empty <paramref name="result"/>
        /// withdraws the entry, which returns that check to untested.
        /// </summary>
        /// <remarks>
        /// 🔑 Separated from the write so it can be driven without Unity, exactly
        /// as the answers file separates its own. What this method decides is
        /// ours; what <c>JsonUtility</c> then writes is Unity's.
        /// </remarks>
        public static void Apply(
            RuntimeChecksFile file,
            string check,
            string result,
            string observedBy,
            string observedAt,
            string sdkVersion,
            string detail)
        {
            if (file == null || string.IsNullOrEmpty(check))
            {
                return;
            }

            if (file.checks == null)
            {
                file.checks = new List<Entry>();
            }

            file.checks.RemoveAll(entry => entry != null
                && string.Equals(entry.check, check, StringComparison.Ordinal));

            if (!string.IsNullOrEmpty(result))
            {
                file.checks.Add(new Entry
                {
                    check = check,
                    result = result,
                    observedBy = observedBy ?? string.Empty,
                    observedAt = observedAt ?? string.Empty,
                    sdkVersion = sdkVersion ?? string.Empty,
                    detail = detail ?? string.Empty,
                });
            }

            // Stable order, so the file a developer commits does not churn on the
            // order the window happened to be clicked in.
            file.checks.Sort((left, right) =>
                string.CompareOrdinal(left == null ? null : left.check, right == null ? null : right.check));
        }

        /// <summary>
        /// The timestamp format the reader accepts: UTC, round-trip, sortable.
        /// </summary>
        /// <remarks>
        /// ⚠️ UTC rather than local. A record travels — into a repository, onto
        /// another machine, into a CI log — and a local time with no offset is
        /// read as whatever the reader's clock means by it.
        /// </remarks>
        public static string Now() => Stamp(DateTimeOffset.UtcNow);

        /// <summary>
        /// <paramref name="at"/> as the record spells an instant: the UTC moment,
        /// whatever offset it was observed in.
        /// </summary>
        /// <remarks>
        /// 🚨 Split from <see cref="Now"/> so the UTC claim can be DRIVEN. Written
        /// as one method reading the clock, `DateTime.UtcNow` → `DateTime.Now`
        /// survived every test: the assertion compared the stamp's date against
        /// today's UTC date, which can only discriminate on a host whose local
        /// date differs — and this box and the CI runners are all `Etc/UTC`. The
        /// test was green because of the clock, not the code. The Go writer's
        /// twin has taken an explicit time all along, and its UTC case passes an
        /// offset zone on purpose.
        /// </remarks>
        public static string Stamp(DateTimeOffset at)
            => at.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>
        /// Records one outcome, preserving every other. A null or empty
        /// <paramref name="result"/> withdraws it, returning that check to
        /// untested.
        /// </summary>
        /// <remarks>
        /// ⛔ Read-modify-write, never a fresh document: the file holds outcomes
        /// from runs this window did not make — the load harness writes into the
        /// same record — and a writer that rebuilt it from what is on screen
        /// would silently discard them.
        /// </remarks>
        public static bool Record(
            string path,
            string check,
            string result,
            string observedBy,
            string sdkVersion,
            string detail,
            out string error)
        {
            if (string.IsNullOrEmpty(check))
            {
                error = "no check to record an outcome for.";
                return false;
            }

            var file = Load(path, out error);
            if (file == null)
            {
                // ⛔ Never an overwrite. A file this window could not read holds
                // outcomes it cannot see, and writing over it destroys them.
                return false;
            }

            Apply(file, check, result, observedBy, Now(), sdkVersion, detail);

            string staging = path + ".tmp";
            try
            {
                // Staged and moved, like every other writer in this toolchain: a
                // half-written record is a scan that refuses every outcome the
                // project has ever recorded.
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // ⛔ CreateNew, which is the symlink defence the CLI's own staging
                // helper names: WriteAllText FOLLOWS a planted `.tmp` symlink and
                // writes through it, so a link left in the project root turns this
                // window into a writer of somebody else's file. The tests asserted
                // no `.tmp` is LEFT behind; none asserted a pre-planted one is
                // refused.
                using (var stagingFile = new FileStream(
                    staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stagingFile))
                {
                    writer.Write(JsonUtility.ToJson(file, true));
                }

                // ⛔ Replace, never delete-then-move. A process that stops between
                // the two leaves the developer with no record at all and a
                // recovery copy under a name they have no reason to look for.
                if (File.Exists(path))
                {
                    File.Replace(staging, path, null);
                }
                else
                {
                    File.Move(staging, path);
                }

                error = null;
                return true;
            }
            catch (Exception e)
            {
                // A failed publish must not leave its staging file behind to be
                // found later and mistaken for the real one.
                try
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }
                catch (Exception)
                {
                    // Nothing useful to say about a cleanup that also failed; the
                    // fault below is the one the developer needs.
                }

                error = e.Message;
                return false;
            }
        }
    }
#pragma warning restore CS0649
}
#endif
