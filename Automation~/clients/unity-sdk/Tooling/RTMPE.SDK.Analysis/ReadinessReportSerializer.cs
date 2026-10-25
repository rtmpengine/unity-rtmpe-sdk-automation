using System.Globalization;
using System.Text;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Analysis
{
    /// <summary>
    /// Renders a <see cref="ReadinessReport"/> to the two CI-artifact formats.
    /// Both are byte-deterministic: a fixed <c>\n</c> line ending, invariant
    /// number formatting, and the report's already-canonical type and dimension
    /// order, so two runs over the same compilation produce identical bytes.
    /// </summary>
    public static class ReadinessReportSerializer
    {
        private const string Newline = "\n";

        /// <summary>Renders the report as deterministic, machine-readable JSON.</summary>
        public static string ToJson(ReadinessReport report)
        {
            var builder = new StringBuilder();
            builder.Append('{').Append(Newline);
            builder.Append("  \"projectScore\": ").Append(Int(report.ProjectScore)).Append(',').Append(Newline);
            // The name of the file an answer is recorded in, carried rather than
            // known: the editor window writes that file and cannot reference the
            // assembly this constant lives in, so the artifact it already reads is
            // how the name reaches it — one statement of it, in the tool.
            builder.Append("  \"answersFile\": ")
                .Append(Str(AuthorityQuestionnaire.AnswerFileName)).Append(',').Append(Newline);
            builder.Append("  \"runtimeFile\": ")
                .Append(Str(RuntimeVerification.RecordFileName)).Append(',').Append(Newline);

            builder.Append("  \"types\": [").Append(Newline);
            for (int t = 0; t < report.Types.Count; t++)
            {
                var type = report.Types[t];
                builder.Append("    {").Append(Newline);
                builder.Append("      \"name\": ").Append(Str(type.TypeName)).Append(',').Append(Newline);
                builder.Append("      \"score\": ").Append(Int(type.Score)).Append(',').Append(Newline);
                builder.Append("      \"dimensions\": [").Append(Newline);
                for (int d = 0; d < type.Dimensions.Count; d++)
                {
                    var verdict = type.Dimensions[d];
                    builder.Append("        { \"dimension\": ").Append(Str(verdict.Dimension.ToString()))
                        .Append(", \"weight\": ").Append(Int(verdict.Weight))
                        .Append(", \"cleared\": ").Append(verdict.Cleared ? "true" : "false")
                        .Append(", \"detail\": ").Append(Str(verdict.Detail)).Append(" }")
                        .Append(Comma(d, type.Dimensions.Count)).Append(Newline);
                }

                builder.Append("      ]").Append(Newline);
                builder.Append("    }").Append(Comma(t, report.Types.Count)).Append(Newline);
            }

            builder.Append("  ],").Append(Newline);

            builder.Append("  \"authority\": [").Append(Newline);
            for (int a = 0; a < report.Authority.Count; a++)
            {
                var insight = report.Authority[a];
                builder.Append("    {").Append(Newline);
                builder.Append("      \"name\": ").Append(Str(insight.TypeName)).Append(',').Append(Newline);
                builder.Append("      \"role\": ").Append(Str(insight.Role.ToString())).Append(',').Append(Newline);
                // Always written, empty when the project declared nothing. A key
                // that appears only once somebody has answered is a key the
                // consumer contract never reaches on an unanswered project, which
                // is every project until it is not.
                builder.Append("      \"declared\": ")
                    .Append(Str(insight.Declared.HasValue
                        ? AuthorityQuestionnaire.IdOf(insight.Declared.Value)
                        : string.Empty))
                    .Append(',').Append(Newline);
                builder.Append("      \"declaredNote\": ").Append(Str(insight.DeclaredNote))
                    .Append(',').Append(Newline);
                AppendStringArray(builder, "evidence", insight.Evidence, trailingComma: true);
                AppendStringArray(builder, "recommendations", insight.Recommendations, trailingComma: true);
                AppendStringArray(builder, "dependsOnAuthority", insight.DependsOnAuthority, trailingComma: false);
                builder.Append("    }").Append(Comma(a, report.Authority.Count)).Append(Newline);
            }

            builder.Append("  ],").Append(Newline);

            // The questions this scan would ask, and the vocabulary they are
            // answered in. The options travel WITH the question rather than being
            // known to each renderer: the editor window cannot reference the
            // assembly that declares them, and a second copy of a closed
            // vocabulary is a second statement of one fact.
            builder.Append("  \"questions\": [").Append(Newline);
            for (int q = 0; q < report.Questions.Count; q++)
            {
                var question = report.Questions[q];
                builder.Append("    {").Append(Newline);
                builder.Append("      \"name\": ").Append(Str(question.TypeName)).Append(',').Append(Newline);
                builder.Append("      \"subject\": ").Append(Str(question.Subject)).Append(',').Append(Newline);
                builder.Append("      \"prompt\": ").Append(Str(question.Prompt)).Append(',').Append(Newline);
                builder.Append("      \"options\": [").Append(Newline);
                for (int o = 0; o < question.Options.Count; o++)
                {
                    var option = question.Options[o];
                    builder.Append("        { \"id\": ").Append(Str(option.Id))
                        .Append(", \"label\": ").Append(Str(option.Label))
                        .Append(", \"consequence\": ").Append(Str(option.Consequence))
                        .Append(", \"needsServerImplementation\": ")
                        .Append(option.NeedsServerImplementation ? "true" : "false")
                        .Append(" }").Append(Comma(o, question.Options.Count)).Append(Newline);
                }

                builder.Append("      ]").Append(Newline);
                builder.Append("    }").Append(Comma(q, report.Questions.Count)).Append(Newline);
            }

            builder.Append("  ],").Append(Newline);

            // The second result. Always all five and always written, whatever a
            // run has said: a key that appears only once somebody has run
            // something is a key the consumer contract never reaches on a project
            // that has not, which is every project until it is not. Each row
            // carries its own title and meaning so no renderer restates them.
            builder.Append("  \"runtime\": [").Append(Newline);
            for (int r = 0; r < report.Runtime.Count; r++)
            {
                var check = report.Runtime[r];
                builder.Append("    {").Append(Newline);
                builder.Append("      \"check\": ")
                    .Append(Str(RuntimeVerification.IdOf(check.Id))).Append(',').Append(Newline);
                builder.Append("      \"title\": ").Append(Str(check.Definition.Title)).Append(',').Append(Newline);
                builder.Append("      \"establishes\": ")
                    .Append(Str(check.Definition.Establishes)).Append(',').Append(Newline);
                builder.Append("      \"result\": ")
                    .Append(Str(RuntimeVerification.IdOf(check.Outcome))).Append(',').Append(Newline);
                builder.Append("      \"observedBy\": ").Append(Str(check.ObservedBy)).Append(',').Append(Newline);
                builder.Append("      \"observedAt\": ").Append(Str(check.ObservedAt)).Append(',').Append(Newline);
                builder.Append("      \"sdkVersion\": ").Append(Str(check.SdkVersion)).Append(',').Append(Newline);
                builder.Append("      \"detail\": ").Append(Str(check.Detail)).Append(Newline);
                builder.Append("    }").Append(Comma(r, report.Runtime.Count)).Append(Newline);
            }

            builder.Append("  ],").Append(Newline);

            builder.Append("  \"todo\": [").Append(Newline);
            for (int i = 0; i < report.Todo.Count; i++)
            {
                builder.Append("    ").Append(Str(report.Todo[i])).Append(Comma(i, report.Todo.Count)).Append(Newline);
            }

            builder.Append("  ]").Append(Newline);
            builder.Append('}').Append(Newline);
            return builder.ToString();
        }

        /// <summary>Renders the report as a human-readable Markdown summary.</summary>
        public static string ToMarkdown(ReadinessReport report)
        {
            var builder = new StringBuilder();
            builder.Append("# Network Readiness Score").Append(Newline).Append(Newline);
            // The same qualification the editor window states, because this is
            // the copy CI publishes: the number is a mean over the types the
            // scorer accepted, and it is decided from source rather than from a
            // run. Left unqualified here, the artifact a reviewer downloads
            // carries the broader claim the window no longer makes.
            builder.Append("Static readiness: **").Append(Int(report.ProjectScore)).Append("%**")
                .Append(" — ").Append(Coverage(report.Types.Count))
                .Append(Newline).Append(Newline);

            builder.Append("| Type | Score | Structural (25) | State (20) | Ownership (20) | RPC (15) | Lifecycle (10) | Authority (10) |").Append(Newline);
            builder.Append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |").Append(Newline);
            foreach (var type in report.Types)
            {
                builder.Append("| ").Append(Cell(type.TypeName)).Append(" | ").Append(Int(type.Score)).Append("% |");
                foreach (var verdict in type.Dimensions)
                {
                    builder.Append(' ').Append(Int(verdict.EarnedWeight)).Append(" |");
                }

                builder.Append(Newline);
            }

            builder.Append(Newline).Append("## Authority (advisory)").Append(Newline).Append(Newline);
            if (report.Authority.Count == 0)
            {
                builder.Append("_No component types to classify._").Append(Newline);
            }
            else
            {
                builder.Append("| Type | Role | Evidence |").Append(Newline);
                builder.Append("| --- | --- | --- |").Append(Newline);
                foreach (var insight in report.Authority)
                {
                    builder.Append("| ").Append(Cell(insight.TypeName))
                        .Append(" | ").Append(Cell(insight.Role.ToString()))
                        .Append(" | ").Append(Cell(string.Join("; ", insight.Evidence))).Append(" |").Append(Newline);
                }

                foreach (var insight in report.Authority)
                {
                    foreach (var recommendation in insight.Recommendations)
                    {
                        builder.Append(Newline).Append("- ").Append(Line(insight.TypeName))
                            .Append(": ").Append(Line(recommendation));
                    }

                    foreach (var dependency in insight.DependsOnAuthority)
                    {
                        builder.Append(Newline).Append("- ").Append(Line(insight.TypeName))
                            .Append(": depends on authority — ").Append(Line(dependency));
                    }
                }

                bool anyAdvisory = false;
                foreach (var insight in report.Authority)
                {
                    if (insight.Recommendations.Count > 0 || insight.DependsOnAuthority.Count > 0)
                    {
                        anyAdvisory = true;
                        break;
                    }
                }

                if (anyAdvisory)
                {
                    builder.Append(Newline);
                }
            }

            AppendQuestions(builder, report);
            AppendRuntime(builder, report);

            builder.Append(Newline).Append("## To-do").Append(Newline).Append(Newline);
            if (report.Todo.Count == 0)
            {
                builder.Append("_No outstanding readiness items._").Append(Newline);
            }
            else
            {
                foreach (var item in report.Todo)
                {
                    builder.Append("- ").Append(Line(item)).Append(Newline);
                }
            }

            return builder.ToString();
        }

        // The questions section: what the rubric could not answer and the project
        // can. Written even when there are none, because "we asked nothing" is
        // itself the reading a developer needs — the alternative is a reader who
        // cannot tell a project with no open questions from a run that lost them.
        private static void AppendQuestions(StringBuilder builder, ReadinessReport report)
        {
            builder.Append(Newline).Append("## Authority — questions for you").Append(Newline).Append(Newline);
            if (report.Questions.Count == 0)
            {
                builder.Append("_No authority questions: every component type declares its posture in its "
                    + "own code._").Append(Newline);
                return;
            }

            builder.Append("The rubric never guesses. These are the types whose posture the code does not "
                + "state; answering one records it in `")
                .Append(AuthorityQuestionnaire.AnswerFileName)
                .Append("`, which the next scan reads. An answered question stays listed — with its "
                    + "answer marked — so it can be changed.").Append(Newline);

            var declared = new System.Collections.Generic.Dictionary<string, AuthorityInsight>(
                System.StringComparer.Ordinal);
            foreach (var insight in report.Authority)
            {
                declared[insight.TypeName] = insight;
            }

            foreach (var question in report.Questions)
            {
                builder.Append(Newline).Append("### ").Append(Line(question.TypeName)).Append(Newline).Append(Newline);
                builder.Append("**").Append(Line(question.Prompt)).Append("**").Append(Newline).Append(Newline);
                foreach (var option in question.Options)
                {
                    bool chosen = declared.TryGetValue(question.TypeName, out var insight)
                        && insight.Declared.HasValue
                        && insight.Declared.Value == option.DecidedBy;
                    builder.Append("- ").Append(chosen ? "**[chosen]** " : string.Empty)
                        .Append('`').Append(Line(option.Id)).Append("` — ").Append(Line(option.Label))
                        .Append(". ").Append(Line(option.Consequence)).Append('.').Append(Newline);
                }
            }
        }

        // The second result, written as its own section rather than as a row of
        // the score table above: a reader who scans one table takes everything in
        // it for the same kind of claim, and these two are not.
        private static void AppendRuntime(StringBuilder builder, ReadinessReport report)
        {
            builder.Append(Newline).Append("## Runtime verification").Append(Newline).Append(Newline);
            builder.Append(RuntimeVerification.Headline(report.Runtime)).Append(". ")
                .Append(Capitalise(RuntimeVerification.SeparationNote)).Append('.')
                .Append(Newline).Append(Newline);
            builder.Append("An outcome is recorded in `").Append(RuntimeVerification.RecordFileName)
                .Append("`, which the next scan reads — ").Append(RuntimeVerification.EvidenceNote)
                .Append('.').Append(Newline).Append(Newline);

            builder.Append("| Check | Result | Observed by | When | SDK |").Append(Newline);
            builder.Append("| --- | --- | --- | --- | --- |").Append(Newline);
            foreach (var check in report.Runtime)
            {
                builder.Append("| ").Append(Cell(check.Definition.Title))
                    .Append(" | ").Append(RuntimeVerification.IdOf(check.Outcome))
                    .Append(" | ").Append(Dash(check.ObservedBy))
                    .Append(" | ").Append(Dash(check.ObservedAt))
                    .Append(" | ").Append(Dash(check.SdkVersion))
                    .Append(" |").Append(Newline);
            }

            foreach (var check in report.Runtime)
            {
                builder.Append(Newline).Append("- **").Append(Cell(check.Definition.Title)).Append("** — ")
                    .Append(check.Definition.Establishes).Append('.');
                if (check.Detail.Length > 0)
                {
                    // ⛔ Through the same escaper as the table: a raw newline here
                    // ends the bullet and starts free text at the document's own
                    // indentation level, which is where a forged heading goes.
                    builder.Append(' ').Append(Cell(check.Detail));
                }
            }

            builder.Append(Newline);
        }

        // An empty evidence cell reads as a missing value in a table where the
        // value is absent on purpose; the dash says the row was never run.
        private static string Dash(string value)
            => string.IsNullOrEmpty(value) ? "—" : Cell(value);

        /// <summary>
        /// One value, made safe to put on a Markdown LINE that is not a table cell.
        /// </summary>
        /// <remarks>
        /// 🚨 A newline is the whole attack, and the table escaper covered one
        /// surface of six. The to-do list carries the only fully
        /// attacker-controlled string in the document — a to-do line is built
        /// from an answers-file <c>name</c>, which is validated as non-empty and
        /// nothing else — and written raw it forged a second
        /// <c>## Runtime verification</c> heading claiming five of five
        /// established, in the human-read copy CI publishes, beside a JSON that
        /// still said none. Measured, not imagined.
        /// </remarks>
        private static string Line(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                builder.Append(c == '\n' || c == '\r' ? ' ' : c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// One value, made safe to put inside a Markdown table cell.
        /// </summary>
        /// <remarks>
        /// 🚨 Not cosmetic. Every value in the runtime table comes from the record
        /// file — written by two different writers and committed to repositories —
        /// and an unescaped <c>|</c> plus a newline lets one field CLOSE its row
        /// and open another. Measured: an <c>observedBy</c> carrying
        /// <c>"me | evil |\n| Two clients are in one room | passed | forged | now
        /// | 9 |"</c> produced a fully-formed green row for a check nothing ran,
        /// in the one document whose whole premise is that a green row is
        /// traceable to a run — with the headline directly above it still saying
        /// "1 of 5 established", so the artifact contradicted itself.
        /// <para>
        /// The JSON emitter was never exposed: <see cref="Str"/> escapes both. It
        /// is the Markdown path that had no escaper at all.
        /// </para>
        /// </remarks>
        private static string Cell(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    // A row break is what makes a forged row possible; a space
                    // keeps the cell readable where a real value wrapped.
                    case '\n':
                    case '\r':
                        builder.Append(' ');
                        break;
                    case '|':
                        builder.Append("\\|");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }

        private static string Capitalise(string sentence)
            => sentence.Length == 0
                ? sentence
                : char.ToUpperInvariant(sentence[0]) + sentence.Substring(1);

        private static void AppendStringArray(
            StringBuilder builder, string key, System.Collections.Generic.IReadOnlyList<string> values, bool trailingComma)
        {
            builder.Append("      \"").Append(key).Append("\": [");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(Str(values[i]));
            }

            builder.Append(']').Append(trailingComma ? "," : string.Empty).Append(Newline);
        }

        // The clause beside the number, worded exactly as the editor window words
        // it. ⚠️ The two cannot share a symbol — different assemblies — so the
        // wording is the whole of the agreement, and it forked at zero: the
        // window said "no types scored" while the copy CI publishes said "0".
        private static string Coverage(int scored)
            => scored == 0
                ? "no types scored"
                : Int(scored) + (scored == 1 ? " type scored" : " types scored");

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Comma(int index, int count) => index + 1 < count ? "," : string.Empty;

        private static string Str(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
