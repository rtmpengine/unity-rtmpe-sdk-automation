using System;
using System.Collections.Generic;
using System.Text.Json;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// Reads the project's authority answers — the file the readiness scan
    /// consults so a question asked once is not asked again.
    /// <para>
    /// Shape: <c>{ "answers": [ { "name": "Ns.Type", "decidedBy": "owner",
    /// "note": "" } ] }</c> — exactly what Unity's <c>JsonUtility</c> writes for
    /// the editor window's DTO, because the window is the writer and this is the
    /// reader, and a format stated twice is a format that drifts.
    /// </para>
    /// </summary>
    /// <remarks>
    /// ⛔ Every refusal names the file's own fault and returns false; nothing is
    /// skipped. An answers file is a developer's recorded decisions, and a parser
    /// that drops the entry it cannot read gives back a score that silently
    /// stopped depending on them.
    /// </remarks>
    public static class AuthorityAnswerFile
    {
        public static bool TryParse(string json, out IReadOnlyList<AuthorityAnswer> answers, out string fault)
        {
            answers = null;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException e)
            {
                fault = "not valid JSON: " + e.Message;
                return false;
            }

            // 🚨 A document can PARSE and still refuse to hand over one of its
            // strings: an unpaired surrogate escape (\ud800 with no pair) is
            // well-formed JSON and throws InvalidOperationException at
            // GetString. Uncaught, that left the process with exit 134 and a
            // stack trace — a code in no documented contract, from a host whose
            // promise is to refuse whole and name the file's own fault.
            try
            {
                return TryReadEntries(document, out answers, out fault);
            }
            catch (InvalidOperationException e)
            {
                fault = "a value in it is not readable text: " + e.Message;
                return false;
            }
            finally
            {
                document.Dispose();
            }
        }

        private static bool TryReadEntries(JsonDocument document, out IReadOnlyList<AuthorityAnswer> answers, out string fault)
        {

            {
                answers = null;
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("answers", out var entries)
                    || entries.ValueKind != JsonValueKind.Array)
                {
                    fault = "the document has no \"answers\" array";
                    return false;
                }

                var parsed = new List<AuthorityAnswer>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int index = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    string where = "answers[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                    index++;

                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        fault = where + " is not an object";
                        return false;
                    }

                    if (!TryString(entry, "name", out string name) || name.Length == 0)
                    {
                        fault = where + " has no \"name\" naming the type it answers for";
                        return false;
                    }

                    if (!TryString(entry, "decidedBy", out string decidedBy))
                    {
                        fault = where + " (" + name + ") has no \"decidedBy\"";
                        return false;
                    }

                    if (!AuthorityQuestionnaire.TryParseId(decidedBy, out var decision))
                    {
                        fault = where + " (" + name + ") answers \"" + decidedBy + "\", which is not one of "
                            + string.Join(", ", AuthorityQuestionnaire.AnswerIds());
                        return false;
                    }

                    // 🔑 Refused rather than resolved. Two answers for one type are
                    // two decisions, and picking either is the tool deciding which
                    // of the developer's own answers counts — silently, on a rule
                    // (last wins) nobody wrote down.
                    if (!seen.Add(name))
                    {
                        fault = "\"" + name + "\" is answered more than once; leave exactly one entry for it";
                        return false;
                    }

                    // Optional, but not lenient: absent is empty, present-and-not-a-string
                    // is a malformed file. Reading a number here as "no note" would
                    // drop what the developer wrote and say nothing.
                    string note = string.Empty;
                    if (entry.TryGetProperty("note", out var noteProperty)
                        && noteProperty.ValueKind != JsonValueKind.Null)
                    {
                        if (noteProperty.ValueKind != JsonValueKind.String)
                        {
                            fault = where + " (" + name + ") has a \"note\" that is not text";
                            return false;
                        }

                        note = noteProperty.GetString() ?? string.Empty;
                    }

                    parsed.Add(new AuthorityAnswer(name, decision, note));
                }

                answers = parsed;
                fault = null;
                return true;
            }
        }

        private static bool TryString(JsonElement element, string name, out string value)
        {
            value = null;
            if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return true;
        }
    }
}
