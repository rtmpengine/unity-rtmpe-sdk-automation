namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// Builds the documentation link an IDE renders beside a diagnostic — the
    /// "?" beside the rule id in the lightbulb menu and the Problems pane.
    /// <para>
    /// Every descriptor derives its <c>helpLinkUri</c> here so the rule
    /// reference has exactly one spelling, and so a rule the reference does not
    /// carry an entry for fails the reference tests rather than shipping a dead
    /// link for a developer to discover.
    /// </para>
    /// </summary>
    public static class DiagnosticHelp
    {
        /// <summary>
        /// The published rule reference. It lives in the package's
        /// <c>Documentation~</c> folder and is served from the automation
        /// repository, matching <c>package.json</c>'s <c>documentationUrl</c>.
        /// </summary>
        public const string RuleReference =
            "https://github.com/rtmpengine/unity-rtmpe-sdk-automation/blob/main/Documentation~/diagnostics.md";

        /// <summary>
        /// The rule reference anchored at <paramref name="diagnosticId"/>'s own
        /// entry. The anchor is the id verbatim: the reference carries an
        /// explicit <c>&lt;a id="RTMPE1020"&gt;</c> per rule rather than relying
        /// on a heading slug, so the link survives any rewording of the heading.
        /// </summary>
        public static string LinkFor(string diagnosticId)
            => RuleReference + "#" + diagnosticId;
    }
}
