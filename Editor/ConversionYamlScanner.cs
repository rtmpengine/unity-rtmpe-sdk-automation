// RTMPE SDK — Editor/ConversionYamlScanner.cs
//
// What a source-only conversion leaves behind in scenes and prefabs.
//
// The conversion is source-only by design: Unity's scene and prefab YAML
// references a component by GUID and is neither read nor rewritten. The wizard
// has always said so, in a standing warning telling the author to "re-wire the
// affected prefabs/scenes by hand" — and named neither the assets nor what in
// them was affected. This is the part that can be named.
//
// 🔑 The failure it exists for is silent, and that is why it matters. The RPC
// conversion keeps a method's name and signature and rewrites its C# CALL SITES
// to `this.RPC("Fire")`. A UnityEvent wired in the Inspector does not go through
// a call site: it invokes `Fire` by name, by reflection, at runtime. So after a
// conversion the button in the scene still runs the method LOCALLY — no compile
// error, nothing in the diff, and the one thing the conversion was for does not
// happen. `SendMessage`, `Invoke` and animation events are the same shape.
//
// ⚠️ Matched on the VALUE, never on the key. The obvious rule — look for
// `m_MethodName:` — is a claim about Unity's serialisation format, which this
// repository cannot verify: no editor here, and the two sample scenes carry no
// UnityEvent at all. A rule built on an unverified key that turns out to be
// wrong reports "nothing found", which is the worst answer available because it
// reads as "checked and clean". So the name the author just converted is what is
// searched for, wherever it appears as a scalar, and the key it sat under is
// REPORTED as evidence rather than used as a filter. The scan is wrong in the
// direction of saying too much, which a human can dismiss.
//
// Free of UnityEditor and UnityEngine: the caller supplies the text, so every
// rule below is reachable from a test that supplies a string.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RTMPE.Editor
{
    /// <summary>One asset's text, as the caller read it.</summary>
    public readonly struct YamlAsset
    {
        public YamlAsset(string assetPath, string text)
        {
            AssetPath = assetPath;
            Text = text;
        }

        public string AssetPath { get; }

        public string Text { get; }
    }

    /// <summary>One place a converted member's name appears in a serialised asset.</summary>
    public sealed class YamlNameSighting
    {
        public YamlNameSighting(string assetPath, int line, string key, string name)
        {
            AssetPath = assetPath;
            Line = line;
            Key = key;
            Name = name;
        }

        public string AssetPath { get; }

        /// <summary>1-based, so it matches what an editor shows.</summary>
        public int Line { get; }

        /// <summary>
        /// The YAML key the name sat under — <c>m_MethodName</c> for a UnityEvent,
        /// <c>functionName</c> for an animation event, and whatever else Unity
        /// writes. Evidence for the reader, never a filter.
        /// </summary>
        public string Key { get; }

        /// <summary>The converted member this matched.</summary>
        public string Name { get; }
    }

    /// <summary>What one scan looked at and what it found.</summary>
    public sealed class ConversionYamlScan
    {
        public ConversionYamlScan(
            IReadOnlyList<YamlNameSighting> sightings,
            int assetsRead,
            int assetsCarryingByNameBindings,
            bool truncated)
        {
            Sightings = sightings ?? throw new ArgumentNullException(nameof(sightings));
            AssetsRead = assetsRead;
            AssetsCarryingByNameBindings = assetsCarryingByNameBindings;
            Truncated = truncated;
        }

        public IReadOnlyList<YamlNameSighting> Sightings { get; }

        /// <summary>
        /// How many assets were actually read. ⛔ The number that makes an empty
        /// result mean something: a scan of nothing and a scan that found nothing
        /// are the same list, and only this tells them apart.
        /// </summary>
        public int AssetsRead { get; }

        /// <summary>
        /// How many of them carry a by-name binding of any kind — a key whose
        /// whole purpose is to name a method. ⚠️ Zero here with assets read is
        /// worth saying out loud: the scan had nothing of that shape to match, so
        /// "no sightings" is not the same as "checked and clean".
        /// </summary>
        public int AssetsCarryingByNameBindings { get; }

        /// <summary>A file hit the per-asset cap and was not read to the end.</summary>
        public bool Truncated { get; }
    }

    /// <summary>
    /// Finds the names a conversion changed the meaning of, in the assets a
    /// conversion cannot change.
    /// </summary>
    public static class ConversionYamlScanner
    {
        /// <summary>
        /// Keys Unity uses to name a method it will call by reflection. Used only
        /// to answer "did this asset contain any binding at all", never to decide
        /// whether a sighting counts.
        /// </summary>
        private static readonly string[] ByNameBindingKeys =
        {
            "m_MethodName", "functionName", "m_TargetAssemblyTypeName",
        };

        /// <summary>The most sightings reported for one asset.</summary>
        /// <remarks>
        /// A scene is a hundred thousand lines and a name like <c>Update</c> would
        /// match everywhere. The cap keeps a runaway result readable; that it was
        /// hit is reported rather than swallowed, because a truncated list that
        /// looks complete is the same lie as an empty one.
        /// </remarks>
        public const int MaxSightingsPerAsset = 64;

        /// <summary>The longest line considered. Beyond it, Unity wrote data, not a binding.</summary>
        public const int MaxLineLength = 4096;

        /// <summary>
        /// The names a conversion of <paramref name="fullTypeName"/> and
        /// <paramref name="memberSpec"/> put at risk in serialised assets: the
        /// type's simple name, and the member the author named.
        /// </summary>
        /// <remarks>
        /// The spec is <c>member[:something]</c> — a companion name for a
        /// NetworkVariable, an audience for an RPC — so the member is the part in
        /// front of the colon. ⛔ The namespace is dropped from the type on
        /// purpose: Unity records a persistent call's declaring type as
        /// <c>Player, Assembly-CSharp</c>, without one.
        /// </remarks>
        public static IReadOnlyList<string> NamesAtRisk(string fullTypeName, string memberSpec)
        {
            var names = new List<string>();

            string type = fullTypeName;
            if (!string.IsNullOrEmpty(type))
            {
                int dot = type.LastIndexOf('.');
                if (dot >= 0) type = type.Substring(dot + 1);
                if (type.Length > 0) names.Add(type);
            }

            string member = memberSpec;
            if (!string.IsNullOrEmpty(member))
            {
                int colon = member.IndexOf(':');
                if (colon >= 0) member = member.Substring(0, colon);
                member = member.Trim();
                if (member.Length > 0) names.Add(member);
            }

            return names;
        }

        public static ConversionYamlScan Scan(
            IEnumerable<YamlAsset> assets, IReadOnlyCollection<string> names)
        {
            if (assets == null) throw new ArgumentNullException(nameof(assets));
            if (names == null) throw new ArgumentNullException(nameof(names));

            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            wanted.Remove(null);
            wanted.Remove(string.Empty);

            var sightings = new List<YamlNameSighting>();
            int read = 0;
            int carrying = 0;
            bool truncated = false;

            foreach (var asset in assets)
            {
                if (asset.Text == null) continue;

                read++;
                bool carriesBinding = false;
                int inThisAsset = 0;
                int line = 0;

                foreach (string raw in SplitLines(asset.Text))
                {
                    line++;
                    if (raw.Length > MaxLineLength) continue;

                    if (!TrySplit(raw, out string key, out string value)) continue;

                    for (int i = 0; i < ByNameBindingKeys.Length; i++)
                    {
                        if (string.Equals(key, ByNameBindingKeys[i], StringComparison.Ordinal))
                        {
                            carriesBinding = true;
                            break;
                        }
                    }

                    if (!wanted.Contains(value)) continue;

                    if (inThisAsset == MaxSightingsPerAsset)
                    {
                        truncated = true;
                        break;
                    }

                    inThisAsset++;
                    sightings.Add(new YamlNameSighting(asset.AssetPath, line, key, value));
                }

                if (carriesBinding) carrying++;
            }

            return new ConversionYamlScan(sightings, read, carrying, truncated);
        }

        /// <summary>
        /// A one-line account of what the scan looked at, for a reader deciding
        /// how much the result is worth.
        /// </summary>
        public static string Describe(ConversionYamlScan scan)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));

            if (scan.AssetsRead == 0)
            {
                return "no scenes or prefabs were read, so this says nothing about them";
            }

            string looked = "read " + Count(scan.AssetsRead, "scene or prefab");

            if (scan.Sightings.Count > 0)
            {
                return looked + "; " + Count(scan.Sightings.Count, "reference")
                    + " to a converted member"
                    + (scan.Truncated ? " (an asset hit the per-file cap; there may be more)" : string.Empty);
            }

            return scan.AssetsCarryingByNameBindings == 0
                ? looked + ", none of which binds a method by name — so there was nothing of that "
                    + "shape to find, which is not the same as checked and clean"
                : looked + ", " + Count(scan.AssetsCarryingByNameBindings, "of them")
                    + " binding methods by name, and none names a converted member";
        }

        // `key: value`, with the sequence dash Unity writes in front of list items
        // taken off first. A comment line and a document separator carry neither.
        private static bool TrySplit(string raw, out string key, out string value)
        {
            key = null;
            value = null;

            int at = 0;
            while (at < raw.Length && (raw[at] == ' ' || raw[at] == '\t')) at++;
            if (at < raw.Length && raw[at] == '-')
            {
                at++;
                while (at < raw.Length && raw[at] == ' ') at++;
            }

            if (at >= raw.Length) return false;

            // ⛔ No explicit comment test, and its absence is deliberate: `#` is
            // not a key character, so the scan below refuses a comment on the same
            // pass that refuses `---` and every other non-key line. A separate `#`
            // check was here until a mutation showed it could be deleted with every
            // case still green — which is the definition of a line doing no work.
            int start = at;
            while (at < raw.Length && (char.IsLetterOrDigit(raw[at]) || raw[at] == '_')) at++;
            if (at == start || at >= raw.Length || raw[at] != ':') return false;

            key = raw.Substring(start, at - start);
            value = Scalar(raw.Substring(at + 1));
            return true;
        }

        // The scalar a key carries: trimmed, unquoted, and cut at the comma that
        // separates a type name from its assembly — `Player, Assembly-CSharp` is
        // how Unity records the declaring type of a persistent call.
        private static string Scalar(string text)
        {
            string trimmed = text.Trim();

            if (trimmed.Length >= 2
                && ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                    || (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }

            int comma = trimmed.IndexOf(',');
            return comma >= 0 ? trimmed.Substring(0, comma).TrimEnd() : trimmed;
        }

        // Hand-written rather than String.Split, to split on LF and hand back the
        // line without the CR a Windows checkout leaves in front of it.
        //
        // ⚠️ What that does NOT buy, corrected because the comment here claimed it
        // did: the CR is not what would break matching — `Scalar` trims the value
        // and a trailing CR is whitespace, so a name still matches with this strip
        // removed. Measured, by deleting it. What it buys is an accurate
        // `raw.Length` for the line cap below, and a line handed to a reader
        // without a stray control character in it.
        private static IEnumerable<string> SplitLines(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;

                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                yield return text.Substring(start, end - start);
                start = i + 1;
            }

            if (start < text.Length)
            {
                int end = text.Length;
                if (end > start && text[end - 1] == '\r') end--;
                yield return text.Substring(start, end - start);
            }
        }

        private static string Count(int n, string noun)
            => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");
    }
}
