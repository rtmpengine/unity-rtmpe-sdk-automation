// RTMPE SDK — Editor/DocumentationLinks.cs
//
// Where each editor window's "Documentation" control points.
//
// 🔑 It points ONLINE, at the rendered page — never at the copy inside the
// package.  The pages ship under Documentation~, a folder Unity's importer
// ignores by design, so the only way to open one locally is to hand the
// operating system a `file://` URL to a raw `.md`.  What happens next is not
// ours to decide: the OS routes it to whatever owns the `.md` extension, and on
// a machine where that is an R/RStudio-family app the reader gets
// "R not found — could not locate an R installation on the system" instead of a
// guide.  That is exactly what a v2.6.0 tester got on macOS, on both windows.
// A help control must open something READABLE on every machine, and only the
// rendered copy is.
//
// The local copy is not lost, and neither window pretends it is: the pages still
// ship under Documentation~/, and both windows NAME that path in their own
// message text, so a reader working offline is told which file to open by hand
// rather than being handed a control that opens nothing.
//
// Resolved here rather than in the windows so the answer is one answer: two
// windows spelling the same URL are two places for it to rot.
//
// Kept free of UnityEngine/UnityEditor, and outside `#if UNITY_EDITOR`, so the
// resolution is exercised by the off-Editor test shard — the same arrangement as
// ConversionCliLocator beside it.

using System;

namespace RTMPE.Editor
{
    /// <summary>
    /// Resolution of the documentation page behind an editor window's help
    /// control.  Stateless: every input arrives as an argument, so the same
    /// contract holds inside Unity and under the test shard.
    /// </summary>
    internal static class DocumentationLinks
    {
        /// <summary>Folder the package's pages ship in, and the path segment they render under.</summary>
        internal const string DocumentationFolder = "Documentation~";

        /// <summary>The automation guide — readiness artifact, conversion host, the wizard.</summary>
        internal const string AutomationPage = "automation.md";

        /// <summary>The per-rule reference, and the manual route when no host is reachable.</summary>
        internal const string DiagnosticsPage = "diagnostics.md";

        /// <summary>The integration guide — the wizard's production step points into it.</summary>
        internal const string GettingStartedPage = "getting-started.md";

        /// <summary>
        /// The guide's section on the key a player build reads, as the rendered
        /// page anchors it: the heading, lower-cased, with spaces as hyphens.
        /// </summary>
        /// <remarks>
        /// Held to the heading in the shipped page by a test, because an anchor
        /// is a claim about another file's text and nothing else re-derives it.
        /// </remarks>
        internal const string PlayerCredentialSection = "#giving-a-player-build-its-api-key";

        /// <summary>
        /// The documentation index — no control opens it, but it is what
        /// <c>package.json</c>'s <c>documentationUrl</c> advertises, and the two
        /// are held to each other by a test.
        /// </summary>
        internal const string IndexPage = "index.md";

        /// <summary>
        /// The published mirror the package advertises — <c>package.json</c>'s
        /// <c>documentationUrl</c>, <c>changelogUrl</c> and <c>licensesUrl</c>, and
        /// the <c>helpLinkUri</c> compiled into every analyzer, all resolve here.
        /// Used on its own when no page is named.
        /// </summary>
        internal const string OnlineRepository =
            "https://github.com/rtmpengine/unity-rtmpe-sdk-automation";

        /// <summary>Where the rendered pages live inside that repository.</summary>
        private const string RenderedPrefix = OnlineRepository + "/blob/main/" + DocumentationFolder + "/";

        /// <summary>
        /// What to hand <c>Application.OpenURL</c> for <paramref name="page"/>:
        /// always the rendered copy, because that is the one that opens in a
        /// browser on every platform regardless of the reader's file
        /// associations.  A page nobody named falls back to the repository root,
        /// which at least lands the reader among the pages.
        /// </summary>
        internal static string Resolve(string page)
            => IsShippedPageName(page) ? RenderedPrefix + page.Trim() : OnlineRepository;

        /// <summary>
        /// <see cref="Resolve(string)"/> with a section of the page: the anchor
        /// travels only with a page this package ships — a fragment on the
        /// repository root would name a section of nothing — and only when it
        /// is one hash followed by a slug, which is the one shape a rendered
        /// heading anchor has.
        /// </summary>
        internal static string Resolve(string page, string anchor)
        {
            string resolved = Resolve(page);
            return IsShippedPageName(page) && IsHeadingAnchor(anchor) ? resolved + anchor : resolved;
        }

        private static bool IsHeadingAnchor(string anchor)
        {
            if (string.IsNullOrEmpty(anchor) || anchor[0] != '#' || anchor.Length == 1)
            {
                return false;
            }

            for (int i = 1; i < anchor.Length; i++)
            {
                char c = anchor[i];
                bool slug = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!slug) return false;
            }

            return true;
        }

        /// <summary>
        /// One of the pages this class knows the package ships.
        /// <para>
        /// 🔑 Identity, not shape. The URL is built by concatenation, so whatever
        /// reaches it becomes a path under the mirror's own repository, and a
        /// shape test admits every well-formed name that leads nowhere:
        /// <c>Resolve("totally-made-up.md")</c> produced a 404 while the method
        /// called itself a shipped-page test. Four pages are named here and
        /// <see cref="DocumentationLinksTests"/> holds each to the file on disk;
        /// anything else lands at the repository root, where a reader at least
        /// finds the index.
        /// </para>
        /// </summary>
        private static bool IsShippedPageName(string page)
        {
            if (string.IsNullOrWhiteSpace(page))
            {
                return false;
            }

            string trimmed = page.Trim();
            return string.Equals(trimmed, AutomationPage, StringComparison.Ordinal)
                || string.Equals(trimmed, DiagnosticsPage, StringComparison.Ordinal)
                || string.Equals(trimmed, GettingStartedPage, StringComparison.Ordinal)
                || string.Equals(trimmed, IndexPage, StringComparison.Ordinal);
        }
    }
}
