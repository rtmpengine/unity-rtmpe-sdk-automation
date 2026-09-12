// RTMPE SDK — Tooling/RTMPE.SDK.Analysis/PrefabMotionScanner.cs
//
// The prefab fault a CI run can name, and a developer testing alone cannot see.
//
// 🔑 A non-owner replica carrying NetworkTransform and no
// NetworkTransformInterpolator FREEZES: both receive paths in
// NetworkManager.GameData return without touching the transform when the
// interpolator is absent. The runtime does say so — on the RECEIVE path, which
// is only reached once a SECOND client is in the room. So the one session a
// developer runs by themselves is exactly the session that cannot report it.
//
// ⚠️ Matched on the script GUID, because that is what a prefab actually stores:
// Unity serialises a component as `m_Script: {fileID: 11500000, guid: …}`, and
// the type name appears nowhere in the file. The two GUIDs below are the
// package's own .meta files, restated here because this assembly cannot read
// them — held to those files by TheScannerMatchesTheShippedScriptGuids.
//
// 🔑 THREE TIERS, and the two below the first are the honest part. The preferred rule groups
// component documents by the GameObject they name and flags a GROUP carrying the
// transform without the interpolator — which is the question the runtime asks,
// because NetworkBehaviour.CachedNetworkTransformInterpolator is
// GetComponent<NetworkTransformInterpolator>() on the SAME GameObject. But that
// grouping is a claim about Unity's serialisation format, and this repository
// has no editor to verify it with. ConversionYamlScanner states the rule for
// exactly this situation: "a rule built on an unverified key that turns out to
// be wrong reports 'nothing found', which is the worst answer available because
// it reads as 'checked and clean'."
//
// ⛔ So when the grouping finds NO group at all in a file, the scan falls back to
// the whole file — does it carry the transform GUID and not the interpolator's —
// and MARKS the row as asset-level. A wrong format assumption then over-reports
// against a reader who can dismiss it, instead of falling silent against one who
// cannot.
//
// Free of Unity and of the file system: the caller supplies the text, so every
// rule below is reachable from a test that supplies a string.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RTMPE.SDK.Analysis
{
    /// <summary>One prefab's text, as the caller read it.</summary>
    public readonly struct PrefabAsset
    {
        public PrefabAsset(string assetPath, string text)
        {
            AssetPath = assetPath;
            Text = text;
        }

        public string AssetPath { get; }

        public string Text { get; }
    }

    /// <summary>How confidently one sighting was reached.</summary>
    public enum PrefabMotionTier
    {
        /// <summary>
        /// One GameObject in the file carries the transform and not the
        /// interpolator — the same question the runtime asks.
        /// </summary>
        GameObject,

        /// <summary>
        /// The object this is about is not wholly in this file — a prefab
        /// variant's inheritance, or a nested prefab instance — so the file
        /// answers for the components it ADDS and for no others.
        /// </summary>
        /// <remarks>
        /// Between the other two in confidence and unlike either in kind. The
        /// grouping worked and the object is known; what is missing is the rest
        /// of the object. An interpolator on the base pairs a transform added
        /// here, and no reading of this file can see it — while a base that
        /// carries neither leaves this file the only evidence there is, which is
        /// why the row is hedged rather than withheld.
        /// </remarks>
        PartlyElsewhere,

        /// <summary>
        /// The file carries the transform and not the interpolator, and the
        /// per-GameObject grouping did not engage on it at all. ⛔ The two
        /// components could still be on different objects; the row says so.
        /// </summary>
        Asset,
    }

    /// <summary>One prefab that sends motion nothing in it can apply.</summary>
    public sealed class PrefabMotionSighting
    {
        public PrefabMotionSighting(
            string assetPath,
            PrefabMotionTier tier,
            string gameObjectId,
            string evidenceKey,
            bool interpolatorPresentButDisabled = false)
        {
            AssetPath = assetPath;
            Tier = tier;
            GameObjectId = gameObjectId;
            EvidenceKey = evidenceKey;
            InterpolatorPresentButDisabled = interpolatorPresentButDisabled;
        }

        public string AssetPath { get; }

        public PrefabMotionTier Tier { get; }

        /// <summary>
        /// The `fileID` of the GameObject the transform sits on, or
        /// <see langword="null"/> for an asset-level row.
        /// </summary>
        public string GameObjectId { get; }

        /// <summary>
        /// The YAML key the matched GUID sat under — <c>m_Script</c> for an
        /// ordinary component, and whatever else Unity writes. ⚠️ Evidence for the
        /// reader, never a filter: the match is on the VALUE, so a key this
        /// scanner has never heard of still counts.
        /// </summary>
        public string EvidenceKey { get; }

        /// <summary>
        /// Whether the receiving half is on the object and switched off.
        /// </summary>
        /// <remarks>
        /// A different fault from an absent one, and a different repair. Told to
        /// add a component that is already there, a reader adds a second — the
        /// type carries no <c>[DisallowMultipleComponent]</c> — and the original
        /// is still switched off.
        /// </remarks>
        public bool InterpolatorPresentButDisabled { get; }
    }

    /// <summary>What one scan looked at and what it found.</summary>
    public sealed class PrefabMotionScan
    {
        public PrefabMotionScan(
            IReadOnlyList<PrefabMotionSighting> sightings,
            int assetsRead,
            int assetsGrouped,
            int assetsReadAtAssetLevel,
            int assetsPartlyDefinedElsewhere,
            bool truncated)
        {
            Sightings = sightings ?? throw new ArgumentNullException(nameof(sightings));
            AssetsRead = assetsRead;
            AssetsGrouped = assetsGrouped;
            AssetsReadAtAssetLevel = assetsReadAtAssetLevel;
            AssetsPartlyDefinedElsewhere = assetsPartlyDefinedElsewhere;
            Truncated = truncated;
        }

        public IReadOnlyList<PrefabMotionSighting> Sightings { get; }

        /// <summary>
        /// How many prefabs were actually read. ⛔ The number that makes an empty
        /// result mean something: a scan of nothing and a scan that found nothing
        /// are the same list, and only this tells them apart.
        /// </summary>
        public int AssetsRead { get; }

        /// <summary>
        /// How many of them the per-GameObject grouping engaged on. ⚠️ Zero here
        /// with assets read is the signal that the format assumption is wrong for
        /// this project, and every row from such a file is asset-level.
        /// </summary>
        public int AssetsGrouped { get; }

        /// <summary>
        /// How many were judged, in whole or in part, by the whole-file rule —
        /// because nothing in them grouped, or because something in them carried
        /// the transform and could not be attributed to an object.
        /// </summary>
        public int AssetsReadAtAssetLevel { get; }

        /// <summary>
        /// How many carried at least one object whose components are not all in
        /// this file. ⚠️ The number that says how much of the result is hedged
        /// for a reason a reader cannot fix by editing the file in front of
        /// them: the other half of those objects is in the base prefab.
        /// </summary>
        public int AssetsPartlyDefinedElsewhere { get; }

        /// <summary>A file hit the per-asset cap and was not read to the end.</summary>
        public bool Truncated { get; }
    }

    /// <summary>
    /// Finds prefabs that send motion no component on them can apply.
    /// </summary>
    public static class PrefabMotionScanner
    {
        /// <summary>
        /// The script GUID Unity writes for <c>RTMPE.Sync.NetworkTransform</c>.
        /// </summary>
        /// <remarks>
        /// ⚠️ A second statement of <c>Runtime/Sync/NetworkTransform.cs.meta</c>,
        /// which this assembly cannot read — the same accommodation
        /// <c>NetworkPrefabsInventory</c> makes for the type's full name. Held to
        /// the shipped .meta by a test; a GUID that drifts matches nothing, and a
        /// scanner that matches nothing reports a clean project.
        /// </remarks>
        public const string NetworkTransformScriptGuid = "2c18612ab534499daebb939b3f08837b";

        /// <summary>
        /// The script GUID for <c>RTMPE.Sync.NetworkTransformInterpolator</c>,
        /// stated on the same terms.
        /// </summary>
        public const string NetworkTransformInterpolatorScriptGuid = "7459f3d40d564343b51863e913cf078b";

        /// <summary>The extension Unity gives a prefab asset.</summary>
        public const string PrefabExtension = ".prefab";

        /// <summary>The most sightings reported for one asset.</summary>
        /// <remarks>
        /// A prefab can hold hundreds of objects. The cap keeps a runaway result
        /// readable; that it was hit is reported rather than swallowed, because a
        /// truncated list that looks complete is the same lie as an empty one.
        /// </remarks>
        public const int MaxSightingsPerAsset = 64;

        /// <summary>The longest line considered. Beyond it, Unity wrote data.</summary>
        public const int MaxLineLength = 4096;

        /// <summary>
        /// What this scan cannot see, in the words it emits. ⛔ Beside every
        /// finding rather than in a design note: a reader deciding what to do
        /// about a row needs to know what its absence would have meant.
        /// </summary>
        public const string Limits =
            "Remote motion — what this check cannot see: a class of your own deriving from "
            + "NetworkTransform (the scan matches the shipped script, not a type hierarchy); a "
            + "component added at runtime rather than saved on the prefab; a prefab VARIANT's "
            + "inherited components, since each file is judged on what it declares itself; and "
            + "on any row marked ASSET-LEVEL, whether the two components sit on the same object "
            + "at all.";

        // The key naming the GameObject a component document belongs to. ⚠️ The
        // ONE key this scan depends on, and the reason the fallback exists.
        private const string GameObjectKey = "m_GameObject";

        // The key a document carries when the object it describes belongs to an
        // instance of another prefab, and the key the instance itself carries
        // naming that prefab.
        private const string PrefabInstanceKey = "m_PrefabInstance";

        private const string SourcePrefabKey = "m_SourcePrefab";

        // The key carrying a behaviour's own switch.
        private const string EnabledKey = "m_Enabled";

        private const string FileIdKey = "fileID";

        private const string GuidKey = "guid";

        /// <summary>
        /// Every prefab in <paramref name="assets"/> that sends motion nothing on
        /// it can apply.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="assets"/> is <see langword="null"/>.
        /// </exception>
        public static PrefabMotionScan Scan(IEnumerable<PrefabAsset> assets)
        {
            if (assets == null) throw new ArgumentNullException(nameof(assets));

            var sightings = new List<PrefabMotionSighting>();
            int read = 0;
            int grouped = 0;
            int assetLevel = 0;
            int partlyElsewhere = 0;
            bool truncated = false;

            foreach (var asset in assets)
            {
                if (asset.Text == null) continue;

                read++;

                // Insertion-ordered, so two runs over one file report the same
                // rows in the same order and the artifact's bytes are stable.
                var order = new List<string>();
                var carries = new Dictionary<string, Group>(StringComparer.Ordinal);

                string document = null;
                bool wholeFileInterpolator = false;
                bool wholeFileDisabledInterpolator = false;

                // The objects this file names but does not define. Unity marks
                // such a document `stripped` on its own header line and gives it
                // an `m_PrefabInstance` naming the instance it belongs to; a
                // component added to one of them is in this file while the rest
                // of the object is in the base prefab.
                var inherited = new HashSet<string>(StringComparer.Ordinal);
                string anchor = null;
                bool fileNamesAnotherPrefab = false;

                // ⛔ The RECEIVING half's switch, and only that. The
                // interpolator writes the transform from `Update`, which Unity
                // does not call on a switched-off behaviour, so one that is
                // present and off applies nothing.
                //
                // ⚠️ Not a general claim that a switched-off component is inert:
                // NetworkTransform's pose broadcast is `Update`-driven too, but
                // its `OnFixedTick` is dispatched by ObjectDispatchOps, which
                // gates on IsAlive/IsOwner/IsSpawned and never on `enabled` — so
                // a switched-off sender still ships input batches and flushes
                // network variables every tick.
                bool documentEnabled = true;

                // What the grouping could not place.  Kept apart from the file's
                // totals because the two answer different questions: the totals
                // say what is somewhere in the file, and these say what is
                // somewhere in the file AND could not be attributed to an object
                // — which is the only part the whole-file rule may speak for.
                bool unattributedTransform = false;
                string unattributedKey = null;

                var documentGuids = new List<KeyValuePair<string, string>>();

                foreach (string raw in SplitLines(asset.Text))
                {
                    if (StartsDocument(raw))
                    {
                        CloseOrNote(order, carries, document, documentGuids, documentEnabled,
                            ref unattributedTransform, ref unattributedKey);
                        document = null;
                        documentGuids.Clear();

                        documentEnabled = true;
                        anchor = Anchor(raw);
                        if (anchor != null && EndsStripped(raw))
                        {
                            inherited.Add(anchor);
                            fileNamesAnotherPrefab = true;
                        }

                        continue;
                    }


                    if (raw.Length > MaxLineLength) continue;

                    if (!TrySplit(raw, out string key, out string rest)) continue;
                    // The other spelling of the same fact, read inside the
                    // document rather than off its header: a non-zero
                    // m_PrefabInstance says this object belongs to an instance
                    // of another prefab. Both are taken, because one marker is
                    // one claim about a serialisation format nothing here can
                    // verify — and the failure of a single claim is a confident
                    // wrong row rather than a missing one.
                    if (anchor != null
                        && string.Equals(key, PrefabInstanceKey, StringComparison.Ordinal)
                        && !string.Equals(
                            Braced(rest, FileIdKey), "0", StringComparison.Ordinal))
                    {
                        inherited.Add(anchor);
                        fileNamesAnotherPrefab = true;
                    }

                    if (string.Equals(key, SourcePrefabKey, StringComparison.Ordinal))
                    {
                        fileNamesAnotherPrefab = true;
                    }

                    // ⚠️ Read as a VALUE and only as `0`. Unity writes
                    // `m_Enabled: 1` on every ordinary component, so a reader
                    // testing for the key rather than its value calls the whole
                    // project switched off; and a reader treating anything but
                    // `1` as off would do the same to a file whose formatting it
                    // has not seen.
                    if (string.Equals(key, EnabledKey, StringComparison.Ordinal)
                        && string.Equals(rest.Trim(), "0", StringComparison.Ordinal))
                    {
                        documentEnabled = false;
                    }

                    if (document == null
                        && string.Equals(key, GameObjectKey, StringComparison.Ordinal))
                    {
                        // ⛔ `fileID: 0` names NO GameObject. Keyed on it, every
                        // component that carries it collapses into one group, and
                        // an interpolator added to one object would silently pair
                        // a transform on another. Left ungrouped, such a component
                        // falls to the whole-file rule, which says out loud that
                        // it did not establish where anything sits.
                        //
                        // ⚠️ This is NOT the shape a prefab variant's additions
                        // take — those name the stripped GameObject they were
                        // added to, which is what `inherited` below reads. The
                        // two are separate cases and the earlier claim that they
                        // were one is why both are named here.
                        string named = Braced(rest, FileIdKey);
                        if (!string.Equals(named, "0", StringComparison.Ordinal))
                        {
                            document = named;
                        }
                    }

                    // ⚠️ On the RAW line, and under any key at all: the match is on
                    // the value, and the key travels as evidence. `m_Script` is
                    // what Unity writes today, and a rule that FILTERED on it would
                    // go silent the moment that is not what a file carries.
                    string guid = Braced(raw, GuidKey);
                    if (guid == null) continue;

                    documentGuids.Add(new KeyValuePair<string, string>(guid, key));

                    if (string.Equals(
                        guid, NetworkTransformInterpolatorScriptGuid, StringComparison.Ordinal))
                    {
                        // ⛔ Only a receiving half that RUNS answers the
                        // whole-file question. Counted regardless of its switch,
                        // one that is present and off suppresses the row for the
                        // whole file and the scan reports the prefab clean —
                        // the same fault the grouped tier is careful about, one
                        // tier down, where nothing else is watching for it.
                        if (documentEnabled) wholeFileInterpolator = true;
                        else wholeFileDisabledInterpolator = true;
                    }
                }

                CloseOrNote(order, carries, document, documentGuids, documentEnabled,
                    ref unattributedTransform, ref unattributedKey);

                bool partlyHere = false;

                if (order.Count > 0)
                {
                    grouped++;
                    int inThisAsset = 0;
                    foreach (string id in order)
                    {
                        var group = carries[id];
                        if (!group.Transform || group.Interpolator) continue;

                        if (inThisAsset == MaxSightingsPerAsset)
                        {
                            truncated = true;
                            break;
                        }

                        inThisAsset++;
                        bool wholly = !inherited.Contains(id);
                        if (!wholly) partlyHere = true;

                        sightings.Add(new PrefabMotionSighting(
                            asset.AssetPath,
                            wholly ? PrefabMotionTier.GameObject : PrefabMotionTier.PartlyElsewhere,
                            id,
                            group.EvidenceKey,
                            group.DisabledInterpolator));
                    }
                }

                // ⛔ Asked of every file, not only of one that grouped nothing.
                // The grouping engages per DOCUMENT while this branch is chosen
                // per FILE: read as an either/or, one readable document answers
                // for the whole of the file and carries every unreadable one
                // away in silence — a prefab that holds the component reported
                // as one that does not, which is the single answer a scanner
                // standing in a gate must never give wrongly.
                if (order.Count == 0 || unattributedTransform)
                {
                    assetLevel++;
                }

                // ⚠️ The interpolator term is the whole file's while the
                // transform term is only the unplaced part's, and that asymmetry
                // is deliberate: an interpolator anywhere is a reason to doubt
                // the accusation, and this rule cannot say where anything sits.
                // Refusing to accuse on that doubt costs a finding; accusing
                // through it costs the reader's trust in every other row.
                if (unattributedTransform && !wholeFileInterpolator)
                {
                    bool switchedOff = wholeFileDisabledInterpolator;

                    // ⚠️ A file naming another prefab cannot answer at the asset
                    // tier either. That tier hedges WHERE the components sit
                    // while still claiming the file holds them all, and here it
                    // does not: the transform may be paired by an interpolator
                    // in the base.
                    if (fileNamesAnotherPrefab) partlyHere = true;

                    sightings.Add(new PrefabMotionSighting(
                        asset.AssetPath,
                        fileNamesAnotherPrefab
                            ? PrefabMotionTier.PartlyElsewhere
                            : PrefabMotionTier.Asset,
                        null,
                        unattributedKey,
                        switchedOff));
                }

                if (partlyHere) partlyElsewhere++;
            }

            return new PrefabMotionScan(
                sightings, read, grouped, assetLevel, partlyElsewhere, truncated);
        }

        /// <summary>
        /// The rows this scan contributes to the readiness report's to-do list,
        /// including the sentence naming what it cannot see.
        /// </summary>
        /// <remarks>
        /// ⛔ Empty when nothing was found. The to-do list is what is OUTSTANDING;
        /// a line there when nothing is wrong is noise, and the account of what
        /// was looked at belongs in <see cref="Describe"/>, which the host prints
        /// whether or not anything was found.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="scan"/> is <see langword="null"/>.
        /// </exception>
        public static IReadOnlyList<string> TodoLines(PrefabMotionScan scan)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));

            var lines = new List<string>();

            // 🚨 An empty result has two causes and they are opposite answers. A
            // scan that read prefabs and grouped NONE of them did not find a
            // clean project — it found a project it could not open, which is
            // what Force Binary serialisation produces — and the account that
            // says so goes to stdout, where the readiness artifact never sees
            // it. The artifact is the surface the Editor window reads, so
            // silence there is read as "checked, and clean".
            //
            // ⛔ Only when NOTHING grouped. A hedge on every clean project is a
            // permanent line no reader can act on, and a to-do list carrying one
            // stops being read at all.
            if (scan.AssetsRead > 0 && scan.AssetsGrouped == 0)
            {
                lines.Add("Remote motion: none of the "
                    + Count(scan.AssetsRead, "prefab")
                    + " read grouped its components by GameObject, so this check "
                    + "established nothing about any of them. That is what a project "
                    + "serialising assets as binary looks like from here — an empty "
                    + "result below means unread, not clean.");
            }

            if (scan.Sightings.Count == 0) return lines;

            foreach (var sighting in scan.Sightings)
            {
                switch (sighting.Tier)
                {
                    case PrefabMotionTier.GameObject:
                        lines.Add("Remote motion: " + sighting.AssetPath
                            + " — the object with fileID " + sighting.GameObjectId
                            + (sighting.InterpolatorPresentButDisabled
                                ? " carries NetworkTransform and a DISABLED "
                                  + "NetworkTransformInterpolator, so on every OTHER player's "
                                  + "client that object will stand still — a behaviour that is "
                                  + "switched off does no work. Tick its checkbox; do not add "
                                  + "another."
                                : " carries NetworkTransform and no NetworkTransformInterpolator, "
                                  + "so on every OTHER player's client that object will stand "
                                  + "still. Add NetworkTransformInterpolator to it.")
                            + " (matched under " + sighting.EvidenceKey + ")");
                        break;

                    case PrefabMotionTier.Asset when sighting.InterpolatorPresentButDisabled:
                        lines.Add("Remote motion: " + sighting.AssetPath
                            + " — ASSET-LEVEL: this file carries NetworkTransform beside a "
                            + "DISABLED NetworkTransformInterpolator, which does the same thing "
                            + "as having none — a behaviour that is switched off does no work. "
                            + "The per-GameObject grouping did not engage on it, so which object "
                            + "each sits on was not established. Tick its checkbox rather than "
                            + "adding another. (matched under " + sighting.EvidenceKey + ")");
                        break;

                    case PrefabMotionTier.PartlyElsewhere when
                        sighting.InterpolatorPresentButDisabled:
                        lines.Add("Remote motion: " + sighting.AssetPath
                            + " — INHERITED: this file adds NetworkTransform to an object it does "
                            + "not itself define, beside a NetworkTransformInterpolator that is "
                            + "switched off. Tick its checkbox rather than adding another. "
                            + "(matched under " + sighting.EvidenceKey + ")");
                        break;

                    case PrefabMotionTier.PartlyElsewhere:
                        lines.Add("Remote motion: " + sighting.AssetPath
                            + " — INHERITED: this file ADDS NetworkTransform to an object it does "
                            + "not itself define, and the rest of that object is in another file "
                            + "— the prefab this one is a variant of, or the prefab it holds an "
                            + "instance of. An interpolator already on that object pairs this "
                            + "transform and cannot be seen from here; if there is none, add "
                            + "NetworkTransformInterpolator. (matched under "
                            + sighting.EvidenceKey + ")");
                        break;

                    default:
                        lines.Add("Remote motion: " + sighting.AssetPath
                            + " — ASSET-LEVEL: this file carries NetworkTransform and no "
                            + "NetworkTransformInterpolator. The per-GameObject grouping did not "
                            + "engage on it, so which object each sits on was not established. "
                            + "(matched under " + sighting.EvidenceKey + ")");
                        break;
                }
            }

            if (scan.Truncated)
            {
                lines.Add("Remote motion: a prefab hit the per-file cap of "
                    + MaxSightingsPerAsset.ToString(CultureInfo.InvariantCulture)
                    + " findings; there may be more in it.");
            }

            lines.Add(Limits);
            return lines;
        }

        /// <summary>
        /// A one-line account of what the scan looked at, for a reader deciding
        /// how much the result is worth.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="scan"/> is <see langword="null"/>.
        /// </exception>
        public static string Describe(PrefabMotionScan scan)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));

            if (scan.AssetsRead == 0)
            {
                return "no prefabs were read, so this says nothing about them";
            }

            string looked = "read " + Count(scan.AssetsRead, "prefab");
            if (scan.AssetsGrouped == 0)
            {
                // ⛔ "every row this scan produced", not "every row below": this
                // sentence is printed on stdout, where nothing follows it, and a
                // pointer to rows that are not there reads as a lost section.
                looked += ", none of which grouped its components by GameObject — every row this "
                    + "scan produced is asset-level, and the grouping rule may not match this "
                    + "project's format";
            }
            else if (scan.AssetsReadAtAssetLevel > 0)
            {
                looked += ", " + scan.AssetsReadAtAssetLevel.ToString(CultureInfo.InvariantCulture)
                    + " of which carry something the grouping could not place, judged whole";
            }

            if (scan.AssetsPartlyDefinedElsewhere > 0)
            {
                looked += ", "
                    + scan.AssetsPartlyDefinedElsewhere.ToString(CultureInfo.InvariantCulture)
                    + " of which add components to an object defined in another file";
            }

            return scan.Sightings.Count == 0
                ? looked + "; none carries NetworkTransform without NetworkTransformInterpolator"
                : looked + "; " + Count(scan.Sightings.Count, "finding")
                    + (scan.Truncated ? " (a file hit the per-file cap; there may be more)" : string.Empty);
        }

        // One GameObject's components, as the walk found them.
        private sealed class Group
        {
            public bool Transform;
            public bool Interpolator;
            public bool DisabledInterpolator;
            public string EvidenceKey;
        }

        /// <summary>
        /// Close a document into its group, or record what it carried when there
        /// is no group to close it into.
        /// </summary>
        /// <remarks>
        /// The two are one step because they are one decision, taken at every
        /// document boundary and again at end of file. Split, a caller can take
        /// the first without the second at one of those points — and a document
        /// closed at a boundary the note does not cover leaves no trace of what
        /// it carried, which is silence rather than a wrong answer and so shows
        /// up nowhere.
        /// </remarks>
        private static void CloseOrNote(
            List<string> order,
            Dictionary<string, Group> carries,
            string document,
            List<KeyValuePair<string, string>> guids,
            bool enabled,
            ref bool unattributedTransform,
            ref string unattributedKey)
        {
            if (Close(order, carries, document, guids, enabled)) return;

            foreach (var pair in guids)
            {
                if (!string.Equals(
                    pair.Key, NetworkTransformScriptGuid, StringComparison.Ordinal)) continue;

                unattributedTransform = true;
                if (unattributedKey == null) unattributedKey = pair.Value;
            }
        }

        /// <summary>
        /// Whether the document was attributed to a GameObject.
        /// </summary>
        private static bool Close(
            List<string> order,
            Dictionary<string, Group> carries,
            string document,
            List<KeyValuePair<string, string>> guids,
            bool enabled)
        {
            if (document == null || guids.Count == 0) return false;

            if (!carries.TryGetValue(document, out var group))
            {
                group = new Group();
                carries[document] = group;
                order.Add(document);
            }

            foreach (var pair in guids)
            {
                if (string.Equals(pair.Key, NetworkTransformScriptGuid, StringComparison.Ordinal))
                {
                    // ⛔ Counted whatever its switch. A sender that is off today
                    // is one line of somebody's OnNetworkSpawn away from being
                    // on, and the replica then freezes exactly as it would have
                    // before — so reading the switch here buys a reassuring
                    // answer that can be wrong, where reading it on the
                    // RECEIVING half buys an accusing one that a reader who
                    // meant it can dismiss.
                    group.Transform = true;
                    if (group.EvidenceKey == null) group.EvidenceKey = pair.Value;
                }
                else if (string.Equals(
                    pair.Key, NetworkTransformInterpolatorScriptGuid, StringComparison.Ordinal))
                {
                    if (enabled) group.Interpolator = true;
                    else group.DisabledInterpolator = true;
                }
            }

            return true;
        }

        /// <summary>
        /// The anchor a document header declares — the <c>&amp;12345</c> in
        /// <c>--- !u!1 &amp;12345 stripped</c> — or <see langword="null"/> where
        /// the header carries none.
        /// </summary>
        /// <remarks>
        /// Read off the header rather than from a field inside the document,
        /// because it is the id every other document uses to refer to this one:
        /// a component's <c>m_GameObject</c> names it, and that is the join this
        /// scan groups on.
        /// </remarks>
        private static string Anchor(string raw)
        {
            int at = raw.IndexOf('&');
            if (at < 0) return null;

            int end = at + 1;
            while (end < raw.Length && raw[end] != ' ' && raw[end] != '\t') end++;

            return end == at + 1 ? null : raw.Substring(at + 1, end - at - 1);
        }

        /// <summary>
        /// Whether a document header ends in Unity's <c>stripped</c> marker.
        /// </summary>
        /// <remarks>
        /// The whole last token, which is how Unity writes it. A substring test
        /// would additionally accept any header carrying those letters
        /// anywhere — and what a future editor writes on that line is exactly
        /// what no reading of this file can establish, so the narrower test is
        /// the one whose failure mode is a missing hedge rather than a hedge on
        /// every object in the project.
        /// </remarks>
        private static bool EndsStripped(string raw)
        {
            // ⚠️ The carriage return is taken off by SplitLines before a line
            // reaches here, so this trim is about trailing SPACES only; the CR
            // is left in the set because a caller reading raw text is a caller
            // this cannot see.
            int end = raw.Length;
            while (end > 0 && (raw[end - 1] == ' ' || raw[end - 1] == '\t'
                               || raw[end - 1] == '\r')) end--;

            const string Marker = "stripped";
            if (end < Marker.Length) return false;

            int at = end - Marker.Length;
            if (string.CompareOrdinal(raw, at, Marker, 0, Marker.Length) != 0) return false;

            return at > 0 && (raw[at - 1] == ' ' || raw[at - 1] == '\t');
        }

        // Unity separates serialised objects with a line beginning `---`.
        private static bool StartsDocument(string raw)
            => raw.Length >= 3 && raw[0] == '-' && raw[1] == '-' && raw[2] == '-';

        // `key: rest`, with the sequence dash Unity writes in front of list items
        // taken off first. ⛔ No explicit comment test: `#` is not a key character,
        // so the scan below refuses a comment on the same pass that refuses every
        // other non-key line.
        private static bool TrySplit(string raw, out string key, out string rest)
        {
            key = null;
            rest = null;

            int at = 0;
            while (at < raw.Length && (raw[at] == ' ' || raw[at] == '\t')) at++;
            if (at < raw.Length && raw[at] == '-')
            {
                at++;
                while (at < raw.Length && raw[at] == ' ') at++;
            }

            if (at >= raw.Length) return false;

            int start = at;
            while (at < raw.Length && (char.IsLetterOrDigit(raw[at]) || raw[at] == '_')) at++;
            if (at == start || at >= raw.Length || raw[at] != ':') return false;

            key = raw.Substring(start, at - start);
            rest = raw.Substring(at + 1);
            return true;
        }

        // The value of `<name>:` inside a flow mapping — `{fileID: 123, guid: abc,
        // type: 3}` — or null. Read from the raw text rather than from a parsed
        // scalar, because the scalar a key carries here is a whole brace group.
        private static string Braced(string text, string name)
        {
            if (text == null) return null;

            int at = 0;
            while (true)
            {
                at = text.IndexOf(name, at, StringComparison.Ordinal);
                if (at < 0) return null;

                int after = at + name.Length;

                // ⚠️ The name must be a whole word. Without this, `guid` matches
                // inside a key that merely ends in it, and the value taken is
                // another field's.
                bool boundedLeft = at == 0
                    || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '_');
                if (!boundedLeft || after >= text.Length || text[after] != ':')
                {
                    at = after;
                    continue;
                }

                after++;
                while (after < text.Length && text[after] == ' ') after++;

                int start = after;
                while (after < text.Length && (char.IsLetterOrDigit(text[after])
                                               || text[after] == '-' || text[after] == '_'))
                {
                    after++;
                }

                return after > start ? text.Substring(start, after - start) : null;
            }
        }

        // Hand-written rather than String.Split, to split on LF and hand back the
        // line without the CR a Windows checkout leaves in front of it — which is
        // what keeps `raw.Length` an accurate measure for the line cap.
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
