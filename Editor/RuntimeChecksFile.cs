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
// ⛔ Three writers, told apart by `observedBy`.  The load harness records what
// it measured; the Readiness window records what the developer saw with their
// own two clients; the play-mode observer (PlayModeRuntimeObserver) records
// what the Editor itself saw happen during a play session.  All three write
// the same three evidence fields, and the reader refuses an outcome missing
// any of them — which is why `Record` takes the observer and the version
// rather than inventing either.
//
// The file NAME is not stated here: it arrives in the artifact the caller has
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
        /// <summary>
        /// What generation of the format this document is.  Declared first, so
        /// both writers lay the file out the same way.
        /// </summary>
        /// <remarks>
        /// 🔑 The file has four parties — the load harness writes it in Go, the
        /// Readiness window and the play-mode observer write it here, and the
        /// scan reads it — and each of them decodes into a shape carrying the
        /// fields it knows, so a key none of them declares is DROPPED on the next
        /// write rather than preserved.  That is the right behaviour for one
        /// generation of the format and a silent data loss across two, and until
        /// this field existed no reader could tell which it was looking at.
        ///
        /// <para>Absent reads as <see cref="Schema"/>: every record written
        /// before the field existed is this generation.  A HIGHER number is
        /// refused whole by every reader — a record from a newer SDK holds
        /// fields this one would drop, and refusing is what keeps the newer
        /// writer's outcomes on disk.</para>
        /// </remarks>
        public int schemaVersion;

        public List<Entry> checks = new List<Entry>();

        /// <summary>The generation this SDK writes and reads.</summary>
        public const int Schema = 1;

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
        /// What the play-mode observer writes into <c>observedBy</c>: an outcome
        /// the Editor saw happen, recorded by nobody's hand.
        /// </summary>
        /// <remarks>
        /// A third spelling, distinct from both the developer's and any harness
        /// scenario's, for the reason <see cref="Developer"/> gives: a reader
        /// can weigh an outcome only if the record says what kind of witness
        /// stood behind it, and the observer is a different kind — it sees one
        /// client's half of a two-client fact and says so in <c>detail</c>.
        /// </remarks>
        public const string EditorObserver = "editor-observer";

        /// <summary>
        /// The three outcome words, as this assembly states them: the engine's
        /// vocabulary a second time, held to the engine by
        /// <c>ThePlayModeObserverRecordsFromWhatItSawTests</c>.  Every
        /// comparison and every write in this assembly reads these; a literal
        /// elsewhere is a copy held by nothing.
        /// </summary>
        public const string PassedWord    = "passed";
        public const string FailedWord    = "failed";
        public const string NotTestedWord = "not-tested";

        /// <summary>
        /// What this writer appends to the record's path for its staging file.
        /// </summary>
        /// <remarks>
        /// 🔑 The Editor's own suffix, not the <c>.tmp</c> the load harness
        /// stages under.  The two write one file and may do so at once — a
        /// harness run ending as play mode does — and under one suffix the
        /// Editor's <c>CreateNew</c> met the harness's staging file, refused,
        /// and its cleanup then deleted a file it had not created out from
        /// under the harness's rename.  Under its own suffix the Editor meets
        /// only its own leftovers.  ⚠️ What stays is the race on the record
        /// itself: two writers that read it, merge, and rename within the same
        /// moment leave the later rename's merge and lose the earlier one's,
        /// silently — a harness run ending in the same second as a play
        /// session, and nothing here retries.
        /// </remarks>
        public const string StagingSuffix = ".editor.tmp";

        /// <summary>
        /// One outcome a writer hands to <see cref="RecordMany"/>: the check,
        /// the outcome word, and the observer's own words.  The evidence fields
        /// are the writer's to stamp, not the caller's to invent.
        /// </summary>
        public readonly struct Outcome
        {
            public Outcome(string check, string result, string detail)
            {
                Check  = check ?? string.Empty;
                Result = result ?? string.Empty;
                Detail = detail ?? string.Empty;
            }

            /// <summary>The check's wire id.</summary>
            public string Check { get; }

            /// <summary>The outcome word; empty withdraws the entry.</summary>
            public string Result { get; }

            /// <summary>The observer's own words; empty when none.</summary>
            public string Detail { get; }
        }

        /// <summary>
        /// Reads the file, or an empty set when it is not there yet. A file that
        /// exists and cannot be read returns null with an error: overwriting it
        /// would destroy outcomes this writer never saw.
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

                if (parsed.schemaVersion > Schema)
                {
                    error = "The runtime record is generation " + parsed.schemaVersion + " and this SDK "
                        + "reads generation " + Schema + ": it was written by a newer SDK, and writing it "
                        + "back from here would drop what that one recorded. Update the package, or move "
                        + "the file aside: " + path;
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
        /// The timestamp spellings the reader accepts — the round-trip form both
        /// writers emit, with and without fractional seconds, with <c>Z</c> or
        /// an offset.  A second statement of the reader's list, held to it by
        /// <c>ThePlayModeObserverRecordsFromWhatItSawTests</c>.
        /// </summary>
        public static IReadOnlyList<string> AcceptedStampFormats => AcceptedStamps;

        private static readonly string[] AcceptedStamps =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        };

        /// <summary>
        /// Why the next scan would refuse <paramref name="file"/> whole, or null
        /// when nothing here says it would: an entry naming a check outside
        /// <paramref name="knownChecks"/>, a result outside the three words, a
        /// claimed outcome with any of its three evidence fields empty or its
        /// stamp not a timestamp, or a check reported twice.
        /// </summary>
        /// <remarks>
        /// The reader's refusals, as far as this assembly can state them without
        /// the engine: the check ids come from the artifact's own rows, the words
        /// are the closed set this type states, the stamp formats are the
        /// reader's list restated.  ⛔ Not the reader — a fault this misses is
        /// still the scan's to name.  What it buys is that the window stops
        /// telling every good row "it reaches the report on the next scan" over
        /// a file that scan will refuse.
        /// </remarks>
        public static string WhyTheScanWouldRefuse(RuntimeChecksFile file, IReadOnlyCollection<string> knownChecks)
        {
            if (file == null || file.checks == null || knownChecks == null) return null;

            // The scan refuses a generation it does not read, and so does this:
            // the two answers are one fact, and a window that promised a report
            // the scan will not write is the thing this predicate exists to
            // prevent.
            if (file.schemaVersion > Schema)
            {
                return "it is generation " + file.schemaVersion + " and this SDK reads generation "
                    + Schema + " — it was written by a newer SDK";
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in file.checks)
            {
                if (entry == null) return "an entry is missing";

                bool known = false;
                foreach (string check in knownChecks)
                {
                    if (string.Equals(check, entry.check, StringComparison.Ordinal)) { known = true; break; }
                }

                if (!known) return "\"" + entry.check + "\" is not a runtime check";
                if (!seen.Add(entry.check)) return "\"" + entry.check + "\" is reported more than once";

                // Exactly the reader's three words: an empty result is not
                // "untested said quietly", it is a word the reader cannot parse.
                bool untested = string.Equals(entry.result, NotTestedWord, StringComparison.Ordinal);
                bool claimed = string.Equals(entry.result, PassedWord, StringComparison.Ordinal)
                    || string.Equals(entry.result, FailedWord, StringComparison.Ordinal);
                if (!untested && !claimed) return "\"" + entry.check + "\" reports \"" + entry.result + "\", which is not an outcome";
                if (!claimed) continue;

                if (string.IsNullOrWhiteSpace(entry.observedBy)) return "\"" + entry.check + "\" names no observer";
                if (string.IsNullOrWhiteSpace(entry.observedAt)) return "\"" + entry.check + "\" names no time";
                if (!DateTimeOffset.TryParseExact(
                        entry.observedAt, AcceptedStamps, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out _))
                {
                    return "\"" + entry.check + "\" has an observedAt that is not a timestamp: \"" + entry.observedAt + "\"";
                }

                if (string.IsNullOrWhiteSpace(entry.sdkVersion)) return "\"" + entry.check + "\" names no SDK version";
            }

            return null;
        }

        /// <summary>
        /// Whether a parsed document is one a writer may write back over.
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
        /// from runs this writer did not make — the load harness writes into the
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

            // The button is the developer's explicit word and overrides whatever
            // any writer put there — the one writer that does.
            return RecordMany(
                path, new[] { new Outcome(check, result, detail) }, observedBy, sdkVersion,
                yieldToAnotherWritersClaim: false, out _, out error);
        }

        /// <summary>
        /// Why the next scan would refuse the record at <paramref name="path"/>
        /// whole, or null when nothing here says it would: the file's own
        /// unreadability, a file that is there and holds no document, else
        /// <see cref="WhyTheScanWouldRefuse"/> over what it holds.  A record
        /// that is not there is refused by nothing.
        /// </summary>
        /// <remarks>
        /// For a writer that has just merged into the record and is about to
        /// promise "it reaches the report on the next scan", and for the window
        /// reading the record beside the artifact: the merge keeps every entry
        /// it did not touch, a foreign or hand-edited row among them, and the
        /// promise is false over a file that row makes the scan refuse.
        /// ⚠️ The empty file is where <see cref="Load"/> and the scan part:
        /// a writer reads an empty file as "nothing yet" and writes over it —
        /// the load harness does the same — while the scan parses it as JSON
        /// and refuses what is not a document.  A preview that answered from
        /// the loaded document alone called that file acceptable.
        /// </remarks>
        public static string WhyTheScanWouldRefuseTheRecordAt(string path, IReadOnlyCollection<string> knownChecks)
        {
            var file = Load(path, out string error);
            if (file == null) return error ?? "the record could not be read";
            if (IsThereAndEmpty(path)) return "the record is empty — not a JSON document; delete it, or let a writer fill it";
            return WhyTheScanWouldRefuse(file, knownChecks);
        }

        // Whether a file exists at `path` and holds nothing but whitespace.
        private static bool IsThereAndEmpty(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path)
                    && string.IsNullOrWhiteSpace(File.ReadAllText(path));
            }
            catch (Exception)
            {
                // Unreadable between two reads: Load answered for the first,
                // and the document rule answers for what it loaded.
                return false;
            }
        }

        /// <summary>
        /// Whether the record already holds a CLAIM — a pass or a failure — for
        /// <paramref name="check"/> from a writer other than
        /// <paramref name="observedBy"/>: an entry a yielding writer leaves alone.
        /// </summary>
        /// <remarks>
        /// 🔑 Another writer's claim stands, whichever way it went.  The load
        /// harness sees both ends of a two-client check and the negative clause
        /// of <c>sync</c>; the developer's button is their explicit word; the
        /// play-mode observer sees one client's half.  An observer that replaced
        /// <c>room-pair</c>'s <c>sync passed</c> with its own would downgrade the
        /// record's evidence on every play session while reading as though it
        /// had added to it — and one that replaced <c>room-pair</c>'s
        /// <c>sync failed</c> (a foreign write crossed, which only the harness
        /// can see) with its half-pass would turn the report green from the one
        /// fact it cannot observe.  🚨 The first form of this rule protected the
        /// pass alone.  A writer's own earlier entry is not protected from it: a
        /// re-observation is the newer fact.
        /// </remarks>
        public static bool AnotherWritersClaimStands(RuntimeChecksFile file, string check, string observedBy)
        {
            if (file == null || file.checks == null) return false;
            foreach (var entry in file.checks)
            {
                if (entry == null || !string.Equals(entry.check, check, StringComparison.Ordinal)) continue;
                bool claimed = string.Equals(entry.result, PassedWord, StringComparison.Ordinal)
                    || string.Equals(entry.result, FailedWord, StringComparison.Ordinal);
                return claimed && !string.Equals(entry.observedBy, observedBy, StringComparison.Ordinal);
            }

            return false;
        }

        /// <summary>
        /// The merge of many outcomes, in memory: <see cref="Apply"/> for each,
        /// under one observer, one stamp and one version — skipping an outcome
        /// that names no check and, when <paramref name="yieldToAnotherWritersClaim"/>,
        /// one for a check another writer has already passed or failed.
        /// Returns whether any outcome was applied: false is a document that
        /// stands exactly as it was read.
        /// Separated from the write for the reason <see cref="Apply"/> is: the
        /// rule can be driven without Unity, and the yielding rule is a rule.
        /// </summary>
        public static bool ApplyMany(
            RuntimeChecksFile file,
            IReadOnlyList<Outcome> outcomes,
            string observedBy,
            string observedAt,
            string sdkVersion,
            bool yieldToAnotherWritersClaim)
        {
            if (file == null || outcomes == null) return false;

            bool applied = false;
            foreach (var outcome in outcomes)
            {
                if (string.IsNullOrEmpty(outcome.Check)) continue;
                if (yieldToAnotherWritersClaim && AnotherWritersClaimStands(file, outcome.Check, observedBy)) continue;
                if (SameClaimStands(file, outcome, observedBy, sdkVersion)) continue;
                // A withdrawal of an entry that is not there moves nothing either.
                if (string.IsNullOrEmpty(outcome.Result) && !Holds(file, outcome.Check)) continue;
                Apply(file, outcome.Check, outcome.Result, observedBy, observedAt, sdkVersion, outcome.Detail);
                applied = true;
            }

            return applied;
        }

        /// <summary>Whether the record holds an entry for <paramref name="check"/> at all.</summary>
        private static bool Holds(RuntimeChecksFile file, string check)
        {
            if (file == null || file.checks == null) return false;
            foreach (var entry in file.checks)
            {
                if (entry != null && string.Equals(entry.check, check, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the record already holds this very claim — same check, same
        /// outcome, same observer, same version, same words — so that writing it
        /// again would move nothing but the stamp.
        /// </summary>
        /// <remarks>
        /// The observer establishes <c>connection</c> on every connected play
        /// session; rewritten each time, a committed record churned on every
        /// Play and the window read "1 recorded since the last scan" for ever.
        /// A claim already on file is kept with the stamp of its first making;
        /// a claim that differs in any word is the newer fact and replaces it.
        /// </remarks>
        public static bool SameClaimStands(RuntimeChecksFile file, Outcome outcome, string observedBy, string sdkVersion)
        {
            if (file == null || file.checks == null) return false;
            foreach (var entry in file.checks)
            {
                if (entry == null || !string.Equals(entry.check, outcome.Check, StringComparison.Ordinal)) continue;
                return string.Equals(entry.result, outcome.Result, StringComparison.Ordinal)
                    && string.Equals(entry.observedBy, observedBy, StringComparison.Ordinal)
                    && string.Equals(entry.sdkVersion, sdkVersion, StringComparison.Ordinal)
                    && string.Equals(entry.detail ?? string.Empty, outcome.Detail, StringComparison.Ordinal);
            }

            return false;
        }

        /// <summary>
        /// Records every outcome in <paramref name="outcomes"/> under one
        /// observer, one stamp and one version, preserving every entry the
        /// record holds for other checks — one read, one write.  An outcome
        /// naming no check is skipped; with <paramref name="yieldToAnotherWritersClaim"/>
        /// an outcome for a check another writer has passed or failed is skipped
        /// too (<see cref="AnotherWritersClaimStands"/>); an empty set, or one
        /// that moves nothing on file, writes nothing and succeeds, with
        /// <paramref name="written"/> false — the caller's cue to say "already
        /// on file" rather than "recorded".
        /// </summary>
        /// <remarks>
        /// One write rather than one per outcome, and not for speed: a writer
        /// that publishes five outcomes as five replacements of the file can be
        /// interrupted between two of them, leaving a record that says the
        /// session established three things when it established five — an
        /// UNDER-claim, which is the safe direction, but a claim nobody made.
        /// The play-mode observer is the caller with more than one, and the
        /// caller that yields.
        /// <para>
        /// No write for a document that stands as it was read: the same bytes
        /// under a new write time are a record that "moved" for every reader
        /// watching it — the window would clear the press it is showing and
        /// re-read a file that says what it said — and a play session that
        /// establishes what the record already holds is the ordinary session.
        /// </para>
        /// </remarks>
        public static bool RecordMany(
            string path,
            IReadOnlyList<Outcome> outcomes,
            string observedBy,
            string sdkVersion,
            bool yieldToAnotherWritersClaim,
            out bool written,
            out string error)
        {
            written = false;
            if (outcomes == null || outcomes.Count == 0)
            {
                error = null;
                return true;
            }

            var file = Load(path, out error);
            if (file == null)
            {
                // ⛔ Never an overwrite. A file this writer could not read holds
                // outcomes it cannot see, and writing over it destroys them.
                return false;
            }

            if (!ApplyMany(file, outcomes, observedBy, Now(), sdkVersion, yieldToAnotherWritersClaim))
            {
                error = null;
                return true;
            }

            string staging = path + StagingSuffix;
            bool staged = false;
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
                // helper names: WriteAllText FOLLOWS a planted staging symlink and
                // writes through it, so a link left in the project root turns this
                // writer into a writer of somebody else's file. The tests asserted
                // no staging file is LEFT behind; none asserted a pre-planted one
                // is refused.  A staging file that is there already — a leftover
                // of a write this process did not survive, or a planted link —
                // is refused and named in the error, never removed.
                // Stamped on the way out, by whichever writer got here: the
                // generation is the document's, not one writer's, and a record
                // that predates the field is this generation by definition.
                file.schemaVersion = Schema;

                using (var stagingFile = new FileStream(
                    staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stagingFile))
                {
                    staged = true;
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

                written = true;
                error = null;
                return true;
            }
            catch (Exception e)
            {
                // A failed publish must not leave its staging file behind to be
                // found later and mistaken for the real one — ITS staging file:
                // one that was there before CreateNew was refused above is
                // somebody else's, or the leftover the error names.
                try
                {
                    if (staged && File.Exists(staging)) File.Delete(staging);
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
