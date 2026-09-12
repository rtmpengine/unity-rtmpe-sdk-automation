using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// Reads the project's runtime record — what a run of the converted game
    /// established, which no amount of reading its source can establish.
    /// <para>
    /// Shape: <c>{ "checks": [ { "check": "sync", "result": "passed",
    /// "observedBy": "room-pair", "observedAt": "2026-09-07T09:14:22Z",
    /// "sdkVersion": "&lt;version&gt;", "detail": "" } ] }</c> — exactly what Unity's
    /// <c>JsonUtility</c> writes for the editor window's DTO, because the window
    /// is one of its two writers and this is the reader.
    /// </para>
    /// </summary>
    /// <remarks>
    /// ⛔ The refusal that matters is the evidence one. A recorded pass is the
    /// only thing that can turn a runtime row green, so a record allowed to say
    /// <c>"result": "passed"</c> and nothing else would make the whole second
    /// result a line of text anybody can type. An outcome names its observer,
    /// its time and the version it was seen against, or it is not an outcome —
    /// and the file is refused whole rather than per entry, the way the answers
    /// file is: dropping the entry it cannot read gives back a runtime picture
    /// that silently stopped depending on the run.
    /// </remarks>
    public static class RuntimeCheckFile
    {
        /// <summary>
        /// The timestamp spellings a record may use: the round-trip form both
        /// writers emit, with and without fractional seconds.
        /// </summary>
        /// <remarks>
        /// ⛔ A closed list rather than a culture parse. Every writer of this file
        /// is a program — the load harness and the editor window — so there is no
        /// human spelling to accommodate, and each admitted format is one more way
        /// for a value that is not an instant to read as one.
        /// <para>
        /// ⚠️ Every entry carries a ZONE, and that is the point of listing them
        /// rather than using <c>K</c>: <c>K</c> matches the empty string too, so
        /// an offset-less stamp would pass — and an offset-less stamp read on
        /// another machine is a different instant, which is the property this
        /// field exists to carry. Both writers emit the literal <c>Z</c>.
        /// </para>
        /// <para>
        /// ⛔ The parsed value is DISCARDED — only acceptance matters, and the
        /// record keeps the string verbatim. Do not start reading the parse
        /// result: under a literal <c>'Z'</c> the offset comes back local.
        /// </para>
        /// </remarks>
        private static readonly string[] AcceptedTimestampFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        };

        public static bool TryParse(string json, out IReadOnlyList<RuntimeCheck> checks, out string fault)
        {
            checks = null;

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
                return TryReadEntries(document, out checks, out fault);
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

        private static bool TryReadEntries(JsonDocument document, out IReadOnlyList<RuntimeCheck> checks, out string fault)
        {

            {
                checks = null;
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("checks", out var entries)
                    || entries.ValueKind != JsonValueKind.Array)
                {
                    fault = "the document has no \"checks\" array";
                    return false;
                }

                var parsed = new List<RuntimeCheck>();
                var seen = new HashSet<RuntimeCheckId>();
                int index = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    string where = "checks[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                    index++;

                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        fault = where + " is not an object";
                        return false;
                    }

                    if (!TryString(entry, "check", out string checkId) || checkId.Length == 0)
                    {
                        fault = where + " has no \"check\" naming which of "
                            + string.Join(", ", RuntimeVerification.CheckIds()) + " it reports";
                        return false;
                    }

                    if (!RuntimeVerification.TryParseId(checkId, out var id))
                    {
                        fault = where + " reports \"" + checkId + "\", which is not one of "
                            + string.Join(", ", RuntimeVerification.CheckIds());
                        return false;
                    }

                    if (!TryString(entry, "result", out string resultId))
                    {
                        fault = where + " (" + checkId + ") has no \"result\"";
                        return false;
                    }

                    if (!RuntimeVerification.TryParseOutcome(resultId, out var outcome))
                    {
                        fault = where + " (" + checkId + ") reports \"" + resultId + "\", which is not one of "
                            + string.Join(", ", RuntimeVerification.OutcomeIds());
                        return false;
                    }

                    // 🔑 Refused rather than resolved, as in the answers file: two
                    // entries for one check are two observations, and taking either
                    // is the tool deciding which run counts on a rule nobody wrote.
                    if (!seen.Add(id))
                    {
                        fault = "\"" + checkId + "\" is reported more than once; leave exactly one entry for it";
                        return false;
                    }

                    if (!TryOptionalString(entry, "detail", checkId, where, out string detail, out fault))
                    {
                        return false;
                    }

                    // The whole of the acceptance criterion, in one branch: a
                    // result that is not "not-tested" is a claim about a run, and
                    // it carries the three things that make it traceable to one.
                    if (outcome == RuntimeCheckOutcome.NotTested)
                    {
                        // Explicitly untested is the default said out loud, and
                        // nothing is being claimed, so there is nothing to trace.
                        // ⚠️ Any evidence such an entry carries is DROPPED rather
                        // than refused: "not tested, observed by room-pair at T"
                        // is surplus rather than a contradiction the reader has to
                        // choose between, and rendering an observer beside "not
                        // tested" would say something the record does not.
                        parsed.Add(new RuntimeCheck(
                            RuntimeVerification.DefinitionOf(id), outcome, null, null, null, detail));
                        continue;
                    }

                    if (!TryString(entry, "observedBy", out string observedBy) || observedBy.Trim().Length == 0)
                    {
                        fault = where + " (" + checkId + ") reports \"" + resultId
                            + "\" with no \"observedBy\" — " + RuntimeVerification.EvidenceNote;
                        return false;
                    }

                    if (!TryString(entry, "observedAt", out string observedAt) || observedAt.Trim().Length == 0)
                    {
                        fault = where + " (" + checkId + ") reports \"" + resultId
                            + "\" with no \"observedAt\" — " + RuntimeVerification.EvidenceNote;
                        return false;
                    }

                    // A time that is not a time is a string, and a string cannot
                    // date an observation: the field would be satisfied by the
                    // word "recently".
                    //
                    // 🚨 EXACT, and the loose parse it replaces was wrong in a way
                    // its own comment got backwards. `DateTimeStyles.RoundtripKind`
                    // governs how an offset's Kind is PRESERVED; it constrains the
                    // format not at all. Measured, `DateTimeOffset.TryParse` under
                    // it accepted `"3.14"` as 14 March — a VERSION STRING in the
                    // wrong field passing the timestamp gate — and `"12:00"` as
                    // today's date ON THE READING MACHINE, which is two different
                    // instants read on two days: the exact opposite of the "reads
                    // the same on another machine, and orders" the comment claimed.
                    // `"09/07/2026"` is worse than either, because a human writing
                    // dd/MM means July and the invariant culture means September,
                    // silently.
                    if (!DateTimeOffset.TryParseExact(
                            observedAt,
                            AcceptedTimestampFormats,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out _))
                    {
                        fault = where + " (" + checkId + ") has an \"observedAt\" that is not a timestamp: \""
                            + observedAt + "\" — write it as 2026-09-07T09:14:22Z";
                        return false;
                    }

                    if (!TryString(entry, "sdkVersion", out string sdkVersion) || sdkVersion.Trim().Length == 0)
                    {
                        fault = where + " (" + checkId + ") reports \"" + resultId
                            + "\" with no \"sdkVersion\" — " + RuntimeVerification.EvidenceNote;
                        return false;
                    }

                    parsed.Add(new RuntimeCheck(
                        RuntimeVerification.DefinitionOf(id), outcome, observedBy, observedAt, sdkVersion, detail));
                }

                checks = parsed;
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

        // Optional, but not lenient: absent is empty, present-and-not-a-string is
        // a malformed file. Reading a number here as "no detail" would drop what
        // the observer wrote and say nothing about having dropped it.
        private static bool TryOptionalString(
            JsonElement entry, string name, string checkId, string where, out string value, out string fault)
        {
            value = string.Empty;
            fault = null;
            if (!entry.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                fault = where + " (" + checkId + ") has a \"" + name + "\" that is not text";
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return true;
        }
    }
}
