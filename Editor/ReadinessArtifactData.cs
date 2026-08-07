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
    [Serializable]
    internal sealed class ReadinessArtifactData
    {
        public int projectScore;
        public List<TypeEntry> types;
        public List<AuthorityEntry> authority;
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
            public List<string> evidence;
            public List<string> recommendations;
            public List<string> dependsOnAuthority;
        }

        public const string FileName = "network-readiness.json";

        /// <summary>
        /// The Unity project root (the folder holding <c>Assets/</c>), where
        /// <c>make readiness</c> drops the artifact.
        /// </summary>
        public static string DefaultPath()
            => Path.Combine(Directory.GetParent(Application.dataPath).FullName, FileName);

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
                if (parsed == null || parsed.types == null || parsed.authority == null || parsed.todo == null)
                {
                    error = "The file parsed but is not a readiness artifact (no readiness sections).";
                    return null;
                }

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
}
#endif
