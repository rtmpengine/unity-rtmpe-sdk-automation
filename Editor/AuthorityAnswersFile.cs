// RTMPE SDK — Editor/AuthorityAnswersFile.cs
//
// The project's recorded answers to the readiness scan's authority questions.
//
// 🔑 Its own file, and an INPUT to the scan — never the artifact.  The readiness
// artifact is rewritten whole by every run, so an answer stored there would
// survive exactly until the next scan, which is the run that was supposed to
// read it.  What this file writes, `readiness --answers` reads.
//
// The file NAME is not stated here: it arrives in the artifact this window has
// already loaded (`answersFile`), so the tool remains its single author.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Reads and writes <c>network-authority-answers.json</c>: one recorded
    /// answer per component type whose authority the code does not state.
    /// </summary>
    // JsonUtility binds these by reflection — see the note in
    // ReadinessArtifactData.cs for why the suppression is scoped here.
#pragma warning disable CS0649
    [Serializable]
    internal sealed class AuthorityAnswersFile
    {
        public List<Entry> answers = new List<Entry>();

        [Serializable]
        internal sealed class Entry
        {
            public string name;
            public string decidedBy;
            public string note;
        }

        /// <summary>
        /// Reads the file, or an empty set when it is not there yet. A file that
        /// exists and cannot be read returns null with an error: overwriting it
        /// would destroy answers this window never saw.
        /// </summary>
        public static AuthorityAnswersFile Load(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "no answers file path — the readiness artifact does not name one.";
                return null;
            }

            if (!File.Exists(path))
            {
                return new AuthorityAnswersFile();
            }

            try
            {
                string text = File.ReadAllText(path);
                if (string.IsNullOrEmpty(text.Trim()))
                {
                    return new AuthorityAnswersFile();
                }

                var parsed = JsonUtility.FromJson<AuthorityAnswersFile>(text);
                if (!IsUsable(parsed))
                {
                    error = "The answers file parsed but carries no answers array: " + path;
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
        /// 🔑 Split out for the reason its twin in <c>RuntimeChecksFile</c> was:
        /// the second limb is unreachable from the test shard, whose
        /// <c>JsonUtility.FromJson</c> stub returns <see langword="default"/>
        /// unconditionally — so <c>parsed == null</c> short-circuits every case
        /// and weakening the rest survived a full green suite. Under the real
        /// serialiser <c>{}</c> parses to a NON-null object with a null list, and
        /// that limb is what stops this window overwriting answers it could not
        /// read. The repair was made in one file of three; this is the second.
        /// </remarks>
        public static bool IsUsable(AuthorityAnswersFile parsed)
            => parsed != null && parsed.answers != null;

        /// <summary>
        /// The merge rule, in memory: one answer replaces its own type's entry
        /// and touches no other. A null or empty <paramref name="decidedBy"/>
        /// withdraws the entry.
        /// </summary>
        /// <remarks>
        /// 🔑 Separated from the write so it can be driven without Unity. What
        /// this method decides is ours; what <c>JsonUtility</c> then writes is
        /// Unity's, and holding the two in one method would mean the merge could
        /// only ever be asserted where Unity runs — which is nowhere in CI.
        /// </remarks>
        public static void Apply(AuthorityAnswersFile file, string typeName, string decidedBy, string note)
        {
            if (file == null || string.IsNullOrEmpty(typeName))
            {
                return;
            }

            if (file.answers == null)
            {
                file.answers = new List<Entry>();
            }

            file.answers.RemoveAll(entry => entry != null
                && string.Equals(entry.name, typeName, StringComparison.Ordinal));

            if (!string.IsNullOrEmpty(decidedBy))
            {
                file.answers.Add(new Entry
                {
                    name = typeName,
                    decidedBy = decidedBy,
                    note = note ?? string.Empty,
                });
            }

            // Stable order, so the file a developer commits does not churn on the
            // order the window happened to be clicked in.
            file.answers.Sort((left, right) =>
                string.CompareOrdinal(left == null ? null : left.name, right == null ? null : right.name));
        }

        /// <summary>
        /// Records one answer, preserving every other. A null or empty
        /// <paramref name="decidedBy"/> withdraws the answer for that type.
        /// </summary>
        /// <remarks>
        /// ⛔ Read-modify-write, never a fresh document: the file holds decisions
        /// about types this artifact may not even describe — a project scanned
        /// one directory at a time still has one answers file — and a writer that
        /// rebuilt it from what is on screen would silently discard the rest.
        /// </remarks>
        public static bool Record(
            string path, string typeName, string decidedBy, string note, out string error)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                error = "no type to record an answer for.";
                return false;
            }

            var file = Load(path, out error);
            if (file == null)
            {
                // ⛔ Never an overwrite. A file this window could not read holds
                // decisions it cannot see, and writing over it destroys them.
                return false;
            }

            Apply(file, typeName, decidedBy, note);

            string staging = path + ".tmp";
            try
            {
                // Staged and moved, like every other writer in this toolchain: a
                // half-written answers file is a scan that refuses every answer
                // the project has ever recorded.
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
                // the two leaves the developer with no answers file at all and a
                // recovery copy under a name they have no reason to look for —
                // which is a worse outcome than the overwrite it was avoiding.
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
