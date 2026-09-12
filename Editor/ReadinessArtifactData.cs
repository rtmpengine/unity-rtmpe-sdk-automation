// RTMPE SDK — Editor/ReadinessArtifactData.cs
//
// The readiness artifact's DTOs and loader, shared by every editor surface
// that renders it (NetworkReadinessWindow, ConversionWizard).  One definition
// of the schema and of "is this file a readiness artifact" keeps the two
// windows from drifting apart.
//
// Field names mirror the JSON keys byte-for-byte — JsonUtility maps by field
// name, and the keys are pinned by ReadinessReportSerializer's golden tests.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Deserialized <c>network-readiness.json</c>: project score, per-type
    /// dimension verdicts, the advisory authority block, and the to-do list.
    /// </summary>
    // JsonUtility binds these by reflection, so the compiler sees every field
    // written by nobody.  Scoped to the declarations rather than set on the test
    // project, the way RTMPE.Rooms.MatchmakingManager scopes the same warning
    // over its reply DTOs: a project-wide NoWarn silences a genuinely unassigned
    // field anywhere in the shard, and the shard is where that mistake is most
    // likely to be made and least likely to be noticed.
#pragma warning disable CS0649
    [Serializable]
    internal sealed class ReadinessArtifactData
    {
        public int projectScore;
        public string answersFile;
        public string runtimeFile;
        public List<TypeEntry> types;
        public List<AuthorityEntry> authority;
        public List<QuestionEntry> questions;
        public List<RuntimeEntry> runtime;
        public List<string> todo;

        [Serializable]
        internal sealed class TypeEntry
        {
            public string name;
            public int score;
            public List<DimensionEntry> dimensions;
        }

        [Serializable]
        internal sealed class DimensionEntry
        {
            public string dimension;
            public int weight;
            public bool cleared;
            public string detail;
        }

        [Serializable]
        internal sealed class AuthorityEntry
        {
            public string name;
            public string role;
            public string declared;
            public string declaredNote;
            public List<string> evidence;
            public List<string> recommendations;
            public List<string> dependsOnAuthority;
        }

        [Serializable]
        internal sealed class QuestionEntry
        {
            public string name;
            public string subject;
            public string prompt;
            public List<OptionEntry> options;
        }

        /// <summary>
        /// One of the five runtime checks and what a run said about it. The
        /// title and the meaning travel WITH the row rather than being known
        /// here: the tool that defines the vocabulary is the one that should
        /// state it, and a second copy is a second thing to keep in step.
        /// </summary>
        [Serializable]
        internal sealed class RuntimeEntry
        {
            public string check;
            public string title;
            public string establishes;
            public string result;
            public string observedBy;
            public string observedAt;
            public string sdkVersion;
            public string detail;
        }

        [Serializable]
        internal sealed class OptionEntry
        {
            public string id;
            public string label;
            public string consequence;
            public bool needsServerImplementation;
        }

        public const string FileName = "network-readiness.json";

        /// <summary>
        /// Whether a parsed document carries the four sections that make it this
        /// artifact rather than a foreign JSON file.
        /// </summary>
        /// <remarks>
        /// 🔑 Split out for the reason its twins in <c>RuntimeChecksFile</c> and
        /// <c>AuthorityAnswersFile</c> were: every limb after
        /// <c>parsed == null</c> is unreachable from the test shard, whose
        /// <c>JsonUtility.FromJson</c> stub returns <see langword="default"/>
        /// unconditionally — so deleting the section checks survived a full green
        /// suite. Under the real serialiser a foreign document parses to a
        /// NON-null object whose lists are null, which is exactly the file this
        /// test separates out.
        /// <para>
        /// ⛔ Four sections and not six: <c>questions</c> and <c>runtime</c> are
        /// absent from an artifact written by an older package, and calling that
        /// file foreign would be wrong twice over — it is ours, and the reader
        /// would be told the wrong reason.
        /// </para>
        /// </remarks>
        public static bool CarriesTheSections(ReadinessArtifactData parsed)
            => parsed != null && parsed.types != null && parsed.authority != null && parsed.todo != null;

        /// <summary>
        /// The simple name of a type the artifact names.
        /// <para>
        /// 🔑 Here rather than in each window: both render rows keyed on these
        /// names, from this one file, and two private copies of the rule were
        /// exactly that — one rule, two spellings, nothing holding them equal.
        /// Null and empty pass through, because a name the emitter did not write
        /// is the caller's problem to report, not this helper's to invent.
        /// </para>
        /// </summary>
        public static string ShortName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return fullName;
            }

            int lastDot = fullName.LastIndexOf('.');
            return lastDot >= 0 && lastDot + 1 < fullName.Length
                ? fullName.Substring(lastDot + 1)
                : fullName;
        }

        /// <summary>
        /// The score line as a window states it: what the number measures, and
        /// how many types it was measured over.
        /// </summary>
        /// <remarks>
        /// 🔑 Two facts belong to the number and neither is legible from it. It
        /// is a mean over the types the scorer accepted — concrete
        /// <c>NetworkBehaviour</c> subclasses — so it climbs as types JOIN that
        /// set rather than as the project converges. And every dimension behind
        /// it is decided from source, so a cleared one says the rule holds as
        /// written, never that the converted game has been run.
        /// <para>
        /// ⛔ A count, never a fraction of the component types the artifact
        /// describes. Most of those are MonoBehaviours the scorer is right to
        /// leave alone — an orchestrator that wires a scene is finished work,
        /// not a shortfall — so a denominator would read as a target and invite
        /// the conversion of types that must not be converted. The advisory
        /// block below the headline carries that wider set already, under the
        /// framing that says what it is.
        /// </para>
        /// <para>
        /// The percentage is stated even when nothing was scored, because the
        /// shipped guide promises exactly that reading — an unconverted project
        /// scores 0% and that is correct, not a failure — and the clause beside
        /// it is what supplies the reason.
        /// </para>
        /// </remarks>
        public string ScoreHeadline()
        {
            int scored = types == null ? 0 : types.Count;
            string coverage = scored == 0
                ? "no types scored"
                : scored + (scored == 1 ? " type scored" : " types scored");

            return "Static readiness: " + projectScore + "% — " + coverage;
        }

        /// <summary>
        /// The runtime result's own headline: how many of the five checks a run
        /// has established.
        /// </summary>
        /// <remarks>
        /// ⛔ A count, never a percentage, and never combined with the score
        /// above. Two percentages side by side read as two measurements of one
        /// thing; these are a mean over source-decided dimensions and five fixed
        /// questions only a run can answer. An artifact written before this
        /// section existed carries no rows, and "not run" is the honest reading
        /// of that — the same reading as a project nobody has run.
        /// </remarks>
        /// <summary>
        /// How many runtime checks the vocabulary defines.
        /// </summary>
        /// <remarks>
        /// ⚠️ A second statement of <c>RuntimeVerification.Checks.Count</c>, which
        /// this assembly cannot reference, held to it by
        /// <c>TheEditorsHeadlineIsWordedExactlyAsTheToolsIs</c>.
        /// </remarks>
        public const int ExpectedChecks = 5;

        public string RuntimeHeadline()
        {
            if (runtime == null || runtime.Count == 0)
            {
                return "Runtime verification: not run";
            }

            int passed = 0;
            int failed = 0;
            foreach (var entry in runtime)
            {
                if (entry == null) continue;
                if (entry.result == "passed") passed++;
                else if (entry.result == "failed") failed++;
            }

            // ⛔ The VOCABULARY's count, not this artifact's row count. The tool's
            // copy of this sentence refuses the argument-denominator in a comment
            // — a one-row artifact would otherwise read "1 of 1 established",
            // which is a complete run — and this copy WAS that foot-gun. The two
            // agree on every artifact the emitter writes, which always carries
            // all five; they diverged only on a hand-edited one, which is exactly
            // where a reader has least reason to doubt what they are shown.
            string headline = "Runtime verification: " + passed + " of " + ExpectedChecks + " established";
            return failed == 0 ? headline : headline + ", " + failed + " failed";
        }

        /// <summary>
        /// The Unity project root (the folder holding <c>Assets/</c>), where
        /// <c>make readiness</c> drops the artifact.
        /// </summary>
        public static string DefaultPath()
            => Path.Combine(ProjectRoot(), FileName);

        /// <summary>The folder holding <c>Assets/</c>.</summary>
        public static string ProjectRoot()
            => Directory.GetParent(Application.dataPath).FullName;

        /// <summary>
        /// The command that writes this artifact for THIS project.
        /// <para>
        /// 🚨 Here rather than in each message, because the message is where it
        /// went wrong: two windows offered a bare <c>make readiness</c> — the
        /// empty state and the staleness banner — and that command scores the
        /// SDK's own five samples and writes the file into the repository. A
        /// reader who ran exactly what they were shown got somebody else's score,
        /// in a directory this window never reads, and no error anywhere. The
        /// paths are interpolated rather than described so the line can be copied
        /// as it stands.
        /// </para>
        /// </summary>
        public static string RegenerateCommand()
        {
            string root = ProjectRoot();
            return "make readiness SOURCE=\"" + root + "\" OUT=\"" + root + "\"";
        }

        // The emitter always writes every section, so a document that names them is the
        // artifact whatever it holds. Matched on the quoted key rather than the parsed
        // value, which is the one signal JsonUtility's defaulting cannot manufacture.
        private static bool NamesArtifactFields(string text)
            => !string.IsNullOrEmpty(text)
                && text.IndexOf("\"projectScore\"", StringComparison.Ordinal) >= 0
                && text.IndexOf("\"types\"", StringComparison.Ordinal) >= 0
                && text.IndexOf("\"authority\"", StringComparison.Ordinal) >= 0
                && text.IndexOf("\"todo\"", StringComparison.Ordinal) >= 0;

        /// <summary>
        /// Reads and validates the artifact. Returns null with a non-null
        /// <paramref name="error"/> on a parse failure or a foreign JSON file;
        /// returns null with a null error when the file simply does not exist
        /// (the caller renders that as "not generated yet", not as a fault).
        /// </summary>
        public static ReadinessArtifactData Load(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                string text = File.ReadAllText(path);

                // JsonUtility default-initializes missing sections rather than failing,
                // so a foreign JSON file "parses" into an artifact whose sections are
                // merely empty. What separates the two is whether the document names the
                // artifact's own fields, not whether it carries any entries: a project
                // with no networked types yet scores zero over empty sections, and that
                // is the emitter's own output rather than a foreign file.
                if (!NamesArtifactFields(text))
                {
                    error = "The file parsed but is not a readiness artifact (no readiness sections).";
                    return null;
                }

                var parsed = JsonUtility.FromJson<ReadinessArtifactData>(text);
                if (!CarriesTheSections(parsed))
                {
                    error = "The file parsed but is not a readiness artifact (no readiness sections).";
                    return null;
                }

                // ⛔ The authority questions are NOT part of the test above, and
                // deliberately: the four sections named there are what makes a
                // document this artifact, and an artifact written by an older
                // package carries them without carrying these. Calling that file
                // foreign would be wrong twice over — it is ours, and the reader
                // would be told the wrong thing about why. Absent here is simply
                // "nothing to ask", which is also the reading for a project whose
                // every type declares its own posture.
                // ⚠️ A convenience, not the guard. Every window that renders these
                // sections tolerates a null one on its own — which is where the
                // property is actually held and actually tested — so deleting
                // these two lines changes nothing observable. They are here so a
                // future reader of the DTO is not the first to discover that an
                // older artifact leaves them null.
                parsed.questions = parsed.questions ?? new List<QuestionEntry>();
                parsed.answersFile = parsed.answersFile ?? string.Empty;
                parsed.runtime = parsed.runtime ?? new List<RuntimeEntry>();
                parsed.runtimeFile = parsed.runtimeFile ?? string.Empty;
                return parsed;
            }
            catch (Exception e)
            {
                // A truncated or foreign file must degrade to a visible error,
                // never to an exception loop inside OnGUI.
                error = e.Message;
                return null;
            }
        }
    }
#pragma warning restore CS0649
}
#endif
