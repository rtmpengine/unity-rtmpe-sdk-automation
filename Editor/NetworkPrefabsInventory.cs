// RTMPE SDK — Editor/NetworkPrefabsInventory.cs
//
// The reading half of the spawn prefab ledger: what the recorded registrations
// mean once they are resolved against the project, and the C# constants a
// project consumes them through.
//
// PrefabIdLedger owns the file format and the allocation arithmetic. It
// deliberately knows nothing about assets, because a format is testable and an
// asset database is not. This file is the layer between the two: it takes a
// parsed ledger plus a way to turn a GUID into a path, and answers the two
// questions a maintainer actually has — which registrations still describe
// something, and what do I type in my code to spawn one.
//
// Free of UnityEditor and UnityEngine for the same reason the ledger is. The
// asset lookup arrives as a delegate, so every rule below is reachable from a
// test that supplies a dictionary, and the window supplies AssetDatabase. The
// alternative — a static call into the editor — would put the entire
// classification and naming contract behind a running Unity.
//
// Spawnability IS modelled, and arrives the same way the asset path does: as a
// delegate. The rule is here, the asset database is in the window, and every
// case below is reachable from a test that supplies a dictionary.
//
// 🚨 An earlier version of this comment refused the question on two grounds and
// both were wrong. It said the check meant "naming a Runtime type from the
// Editor assembly" — which this assembly already does: `com.rtmpe.sdk.editor`
// references `RTMPE.SDK.Runtime`, and `NetworkObjectEditor` writes
// `typeof(NetworkBehaviour)` in the shipped package. And it said "loading every
// prefab asset in the project", which is wider than the need: validating the
// ledger loads the ROWS, and the window has already resolved each of their
// GUIDs to a path.
//
// ⛔ The rule mirrors the runtime EXACTLY: at least one non-null networked
// component on the prefab's ROOT. `SpawnManager.CreateLocal` reads
// `go.GetComponents<NetworkBehaviour>()` and walks past null entries — a missing
// script serialises as one — and it looks no deeper. A check that searched the
// children would call a prefab valid that the runtime destroys on spawn, which
// is a validation wrong in the reassuring direction: the one direction a
// validation must never be wrong in.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RTMPE.Editor
{
    /// <summary>
    /// What one ledger registration turned out to be once resolved against the
    /// project.
    /// </summary>
    /// <remarks>
    /// A registration has one status. When more than one applies, the value
    /// declared first is used.
    /// </remarks>
    public enum PrefabRegistrationStatus
    {
        /// <summary>This id is recorded against more than one prefab.</summary>
        Duplicate,

        /// <summary>No asset in the project has this GUID.</summary>
        Missing,

        /// <summary>
        /// The asset exists but has no networked component on its root, so the
        /// runtime refuses to spawn it.
        /// </summary>
        NotNetworked,

        /// <summary>
        /// The asset exists but could not be inspected, for example while it is
        /// still importing.
        /// </summary>
        /// <remarks>
        /// Not a fault: the registration is treated like <see cref="Registered"/>,
        /// including in the generated registry.
        /// </remarks>
        Unverified,

        /// <summary>Recorded, resolved and unique.</summary>
        /// <remarks>
        /// Says the prefab is networked only when the inventory was built with an
        /// inspector; see
        /// <see cref="NetworkPrefabsInventory.Build(PrefabLedgerDocument, Func{string, string}, Func{string, PrefabSpawnability})"/>.
        /// </remarks>
        Registered,
    }

    /// <summary>
    /// What inspecting one prefab asset found.
    /// </summary>
    public enum PrefabSpawnability
    {
        /// <summary>The asset could not be inspected.</summary>
        Unknown,

        /// <summary>At least one non-null networked component on the root.</summary>
        Networked,

        /// <summary>Loaded, with no networked component on the root.</summary>
        Inert,
    }

    /// <summary>
    /// Whether a prefab root pairs <c>NetworkTransform</c>, which sends motion,
    /// with <c>NetworkTransformInterpolator</c>, which applies it on other
    /// players' copies of the object.
    /// </summary>
    /// <remarks>
    /// Without the interpolator, other players' copies of the object do not move.
    /// This is an advisory: it does not affect <see cref="PrefabInventory.IsClean"/>
    /// or constant generation.
    /// </remarks>
    public enum PrefabMotionPairing
    {
        /// <summary>Nothing was measured: no component list could be read for the asset.</summary>
        Unknown,

        /// <summary>Measured, and no <c>NetworkTransform</c> on the root.</summary>
        NoTransform,

        /// <summary>Both components on the root: motion is sent and applied.</summary>
        Paired,

        /// <summary>
        /// <c>NetworkTransform</c> on the root and no interpolator beside it:
        /// other players' copies of the object do not move.
        /// </summary>
        TransformWithoutInterpolator,
    }

    /// <summary>
    /// A registered prefab whose root has a <c>NetworkTransform</c> but no
    /// <c>NetworkTransformInterpolator</c>.
    /// </summary>
    public sealed class PrefabMotionAdvisory
    {
        public PrefabMotionAdvisory(string assetPath, string name, PrefabMotionPairing pairing)
        {
            AssetPath = assetPath;
            Name = name;
            Pairing = pairing;
        }

        /// <summary>The prefab's asset path when the advisory was made.</summary>
        public string AssetPath { get; }

        /// <summary>The asset's file name without its extension.</summary>
        public string Name { get; }

        /// <summary>The verdict that produced the row.</summary>
        public PrefabMotionPairing Pairing { get; }

        /// <summary>
        /// What is wrong and how to fix it, as the Network Prefabs window shows it.
        /// </summary>
        public string Explanation =>
            "carries " + NetworkPrefabsInventory.NetworkTransformSimpleName + " and no working "
            + NetworkPrefabsInventory.NetworkTransformInterpolatorSimpleName
            + " — every OTHER player's copy of this prefab will stand still, because the "
            + "receiving half of remote motion is the interpolator. Add "
            + NetworkPrefabsInventory.NetworkTransformInterpolatorSimpleName
            + " to the prefab root, or tick its checkbox where one is already there and "
            + "switched off.";
    }

    /// <summary>One ledger registration, resolved against the project.</summary>
    public sealed class PrefabRegistration
    {
        public PrefabRegistration(string guid, uint id, string assetPath, PrefabRegistrationStatus status)
        {
            Guid = guid;
            Id = id;
            AssetPath = assetPath;
            Status = status;
        }

        /// <summary>The asset GUID the ledger is keyed on.</summary>
        public string Guid { get; }

        /// <summary>The spawn prefab id this GUID holds.</summary>
        public uint Id { get; }

        /// <summary>
        /// The asset's path when this inventory was built, or
        /// <see langword="null"/> when no asset had the GUID.
        /// </summary>
        /// <remarks>
        /// A snapshot: rebuild the inventory when the project changes. The ledger
        /// itself stores only the GUID, so moving or renaming a prefab does not
        /// change its id.
        /// </remarks>
        public string AssetPath { get; }

        public PrefabRegistrationStatus Status { get; }

        /// <summary>
        /// Whether this registration can be retired. <see langword="false"/> for an
        /// id shared with another prefab, which must be re-issued first.
        /// </summary>
        public bool CanRetire => Status != PrefabRegistrationStatus.Duplicate;

        /// <summary>
        /// Whether this registration needs a new id: <see langword="true"/> for an
        /// id shared with another prefab.
        /// </summary>
        public bool CanReissue => Status == PrefabRegistrationStatus.Duplicate;

        /// <summary>
        /// What is wrong with this registration, as the Network Prefabs window
        /// shows it, or <see langword="null"/> when nothing is.
        /// </summary>
        public string Explanation
        {
            get
            {
                switch (Status)
                {
                    case PrefabRegistrationStatus.Duplicate:
                        return "shares this id with another prefab";
                    case PrefabRegistrationStatus.Missing:
                        return "no asset in this project carries this GUID";
                    case PrefabRegistrationStatus.NotNetworked:
                        return "carries no networked component on its root, so spawning it "
                             + "fails at runtime — add a NetworkBehaviour to the prefab root, "
                             + "or retire the id";
                    case PrefabRegistrationStatus.Unverified:
                        return "could not be inspected — the asset may still be importing";
                    default:
                        return null;
                }
            }
        }

        /// <summary>
        /// The asset's file name without its extension, or <see langword="null"/>
        /// when the GUID resolved to nothing.
        /// </summary>
        public string Name => NetworkPrefabsInventory.AssetNameOf(AssetPath);
    }

    /// <summary>Every registration in one ledger, resolved and ordered.</summary>
    public sealed class PrefabInventory
    {
        public PrefabInventory(IReadOnlyList<PrefabRegistration> registrations)
        {
            if (registrations == null) throw new ArgumentNullException(nameof(registrations));

            // ⛔ Sorted HERE, not only in Build. The order below is documented on
            // the property and the generated file's determinism rests on it, and
            // this constructor is public — so a caller that assembled its own list
            // could produce a file whose member order changed with nothing else.
            // Establishing the invariant where it is stated costs one sort.
            var ordered = new List<PrefabRegistration>(registrations);
            ordered.Sort(static (left, right) => left.Id != right.Id
                ? left.Id.CompareTo(right.Id)
                : string.CompareOrdinal(left.Guid, right.Guid));
            Registrations = ordered;

            int duplicates = 0;
            int missing = 0;
            int inert = 0;
            int unverified = 0;
            foreach (var registration in registrations)
            {
                switch (registration.Status)
                {
                    case PrefabRegistrationStatus.Duplicate:    duplicates++; break;
                    case PrefabRegistrationStatus.Missing:      missing++;    break;
                    case PrefabRegistrationStatus.NotNetworked: inert++;      break;
                    case PrefabRegistrationStatus.Unverified:   unverified++; break;
                }
            }

            DuplicateCount = duplicates;
            MissingCount = missing;
            NotNetworkedCount = inert;
            UnverifiedCount = unverified;
        }

        /// <summary>
        /// Every registration, in ascending order of id and then of GUID.
        /// </summary>
        public IReadOnlyList<PrefabRegistration> Registrations { get; }

        public int DuplicateCount { get; }

        public int MissingCount { get; }

        /// <summary>Rows whose asset carries no networked component on its root.</summary>
        public int NotNetworkedCount { get; }

        /// <summary>Rows the inventory could not inspect.</summary>
        /// <remarks>
        /// Not counted against <see cref="IsClean"/>: an asset that is still
        /// importing is not a fault.
        /// </remarks>
        public int UnverifiedCount { get; }

        /// <summary>
        /// Whether no registration shares its id, has lost its asset, or has no
        /// networked component on its root.
        /// </summary>
        /// <remarks>
        /// A clean inventory can still be refused by
        /// <see cref="NetworkPrefabsInventory.GenerateConstants"/>, for example when
        /// two prefab names map to the same member name.
        /// </remarks>
        public bool IsClean =>
            DuplicateCount == 0 && MissingCount == 0 && NotNetworkedCount == 0;
    }

    /// <summary>
    /// The outcome of a constant-generation attempt: the source to write, or the
    /// reason there is none.
    /// </summary>
    public sealed class PrefabConstantsResult
    {
        private PrefabConstantsResult(string source, string error)
        {
            Source = source;
            Error = error;
        }

        public string Source { get; }

        public string Error { get; }

        public bool IsValid => Source != null;

        public static PrefabConstantsResult Valid(string source)
            => new PrefabConstantsResult(source, null);

        public static PrefabConstantsResult Invalid(string error)
            => new PrefabConstantsResult(null, error);
    }

    /// <summary>
    /// What reading the ledger file produced: a document to work from, or the
    /// reason there is none.
    /// </summary>
    public sealed class PrefabLedgerRead
    {
        private PrefabLedgerRead(
            PrefabLedgerDocument document, bool existed, bool needsRepair, string error)
        {
            Document = document;
            Existed = existed;
            NeedsRepair = needsRepair;
            Error = error;
        }

        /// <summary>The ledger to work from; <see langword="null"/> when unreadable.</summary>
        public PrefabLedgerDocument Document { get; }

        /// <summary>
        /// Whether a ledger file exists. A missing file is an empty ledger; a file
        /// that exists but cannot be read sets <see cref="Error"/>.
        /// </summary>
        public bool Existed { get; }

        /// <summary>
        /// Whether the document was read with
        /// <see cref="PrefabIdLedger.ParseForRepair"/>, so at least one id is
        /// registered to more than one prefab.
        /// </summary>
        public bool NeedsRepair { get; }

        public string Error { get; }

        public bool IsUsable => Document != null;

        /// <summary>The read of a project with no ledger file: an empty ledger.</summary>
        public static PrefabLedgerRead Absent()
            => new PrefabLedgerRead(PrefabLedgerDocument.Empty, false, false, null);

        /// <summary>
        /// The read of a ledger file that exists but could not be read, for example
        /// because of a lock, a permission or a device error.
        /// </summary>
        /// <param name="error">Why the file could not be read.</param>
        public static PrefabLedgerRead Unreadable(string error)
            => new PrefabLedgerRead(null, true, false, error
                ?? "the ledger could not be read");

        /// <summary>
        /// Reads ledger text with <see cref="PrefabIdLedger.Parse(string)"/>, then,
        /// if that fails, with <see cref="PrefabIdLedger.ParseForRepair"/>, which
        /// also accepts ids registered to more than one prefab.
        /// </summary>
        /// <remarks>
        /// When neither accepts the text, <see cref="Error"/> holds the message from
        /// <see cref="PrefabIdLedger.Parse(string)"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="text"/> is <see langword="null"/>. Use
        /// <see cref="Absent"/> when there is no file.
        /// </exception>
        public static PrefabLedgerRead Of(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var strict = PrefabIdLedger.Parse(text);
            if (strict.IsValid)
            {
                return new PrefabLedgerRead(strict.Document, true, false, null);
            }

            var repair = PrefabIdLedger.ParseForRepair(text);
            return repair.IsValid
                ? new PrefabLedgerRead(repair.Document, true, true, null)
                : new PrefabLedgerRead(null, true, false, strict.Error);
        }
    }

    /// <summary>One selected asset: its path and GUID.</summary>
    public readonly struct PrefabCandidate
    {
        public PrefabCandidate(string assetPath, string guid)
        {
            AssetPath = assetPath;
            Guid = guid;
        }

        public string AssetPath { get; }

        public string Guid { get; }
    }

    /// <summary>
    /// The outcome of allocating ids for a selection: the ledger to write, or the
    /// reason there is none.
    /// </summary>
    public sealed class PrefabAllocation
    {
        private PrefabAllocation(PrefabLedgerDocument updated, uint id, string message, bool isError)
        {
            Updated = updated;
            Id = id;
            Message = message;
            IsError = isError;
        }

        /// <summary>
        /// The ledger to save, or <see langword="null"/> when there is nothing to
        /// write — which covers both refusal and an asset that already held an id.
        /// </summary>
        public PrefabLedgerDocument Updated { get; }

        /// <summary>The id the asset holds. Meaningless when <see cref="IsError"/>.</summary>
        public uint Id { get; }

        public string Message { get; }

        public bool IsError { get; }

        internal static PrefabAllocation Refused(string message)
            => new PrefabAllocation(null, 0, message, true);

        internal static PrefabAllocation Unchanged(uint id, string message)
            => new PrefabAllocation(null, id, message, false);

        internal static PrefabAllocation Allocated(PrefabLedgerDocument updated, uint id, string message)
            => new PrefabAllocation(updated, id, message, false);
    }

    /// <summary>
    /// One row of the generated registry asset: an id, and the prefab asset it
    /// resolved to when the registry was generated.
    /// </summary>
    public sealed class PrefabRegistryRow
    {
        public PrefabRegistryRow(uint id, string guid, string assetPath)
        {
            Id        = id;
            Guid      = guid;
            AssetPath = assetPath;
        }

        /// <summary>The spawn prefab id, as allocated by the ledger.</summary>
        public uint Id { get; }

        /// <summary>
        /// The asset GUID the ledger is keyed on. Load the prefab through it: unlike
        /// the path, it does not change when the prefab is moved or renamed.
        /// </summary>
        public string Guid { get; }

        /// <summary>
        /// The prefab's path when the inventory was built, for diagnostics; use
        /// <see cref="Guid"/> to find the asset.
        /// </summary>
        public string AssetPath { get; }
    }

    /// <summary>
    /// Resolves a ledger against the project and projects it into C# constants.
    /// </summary>
    public static class NetworkPrefabsInventory
    {
        /// <summary>
        /// The name of the generated class of prefab id constants; its file is named
        /// after it.
        /// </summary>
        public const string GeneratedTypeName = "RtmpePrefabIds";

        /// <summary>
        /// Where the generated constants file is written, relative to the project root.
        /// </summary>
        public const string GeneratedAssetPath = "Assets/RTMPE/Generated/" + GeneratedTypeName + ".cs";

        /// <summary>
        /// Where the generated <c>NetworkPrefabRegistry</c> asset is written. It is
        /// generated with the constants, from the same ledger.
        /// </summary>
        public const string GeneratedRegistryPath = "Assets/RTMPE/Generated/RtmpePrefabRegistry.asset";

        /// <summary>The namespace used when a caller names none.</summary>
        public const string DefaultNamespace = "RTMPE.Generated";

        /// <summary>The extension Unity gives a prefab asset.</summary>
        public const string PrefabExtension = ".prefab";

        private const string Newline = "\n";

        // C#'s reserved words. Contextual keywords are absent on purpose: `value`,
        // `record` and their siblings are legal member names, and excluding them
        // would rename a member for a rule the compiler does not have.
        private static readonly HashSet<string> ReservedWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while",

            // ⚠️ Undocumented, and reserved all the same: Roslyn lexes these as
            // keyword tokens, so a prefab named after one emits a member the
            // compiler refuses (CS0145). They appear in no specification list,
            // which is exactly why a list assembled from the specification
            // misses them.
            "__arglist", "__makeref", "__reftype", "__refvalue",
        };

        /// <summary>
        /// Resolves every registration in <paramref name="document"/> through
        /// <paramref name="resolveAssetPath"/>, which answers a GUID with an asset
        /// path or with <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Exceptions thrown by <paramref name="resolveAssetPath"/> are not caught.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Either argument is <see langword="null"/>.
        /// </exception>
        public static PrefabInventory Build(
            PrefabLedgerDocument document, Func<string, string> resolveAssetPath)
            => Build(document, resolveAssetPath, null);

        /// <summary>
        /// As the two-argument form, and also asks <paramref name="inspect"/> what
        /// each resolved asset carries.
        /// </summary>
        /// <remarks>
        /// <para>With a <see langword="null"/> <paramref name="inspect"/>, nothing is
        /// inspected, so no registration is
        /// <see cref="PrefabRegistrationStatus.NotNetworked"/> or
        /// <see cref="PrefabRegistrationStatus.Unverified"/>.</para>
        /// <para>Only registrations that would otherwise be
        /// <see cref="PrefabRegistrationStatus.Registered"/> are inspected.
        /// Exceptions thrown by either delegate are not caught.</para>
        /// </remarks>
        public static PrefabInventory Build(
            PrefabLedgerDocument document,
            Func<string, string> resolveAssetPath,
            Func<string, PrefabSpawnability> inspect)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (resolveAssetPath == null) throw new ArgumentNullException(nameof(resolveAssetPath));

            var shared = new HashSet<uint>(PrefabIdLedger.DuplicatedIds(document));

            var registrations = new List<PrefabRegistration>();
            foreach (var entry in document.Prefabs)
            {
                string path = NullIfEmpty(resolveAssetPath(entry.Key));

                var status = shared.Contains(entry.Value)
                    ? PrefabRegistrationStatus.Duplicate
                    : path == null
                        ? PrefabRegistrationStatus.Missing
                        : PrefabRegistrationStatus.Registered;

                if (status == PrefabRegistrationStatus.Registered && inspect != null)
                {
                    switch (inspect(path))
                    {
                        case PrefabSpawnability.Inert:
                            status = PrefabRegistrationStatus.NotNetworked;
                            break;
                        case PrefabSpawnability.Unknown:
                            status = PrefabRegistrationStatus.Unverified;
                            break;
                    }
                }

                registrations.Add(new PrefabRegistration(entry.Key, entry.Value, path, status));
            }

            // The constructor establishes the order; nothing is sorted twice by
            // accident, and Build does not have to be the only door that does it.
            return new PrefabInventory(registrations);
        }

        /// <summary>
        /// Allocates an id for one selected asset, given the path and GUID the
        /// Editor resolved for it.
        /// </summary>
        /// <remarks>
        /// Refused, with a message, when the asset is not a prefab, has no GUID, or
        /// no id can be allocated. An asset that already holds an id keeps it, and
        /// <see cref="PrefabAllocation.Updated"/> is then <see langword="null"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="document"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// The document holds an entry <see cref="PrefabIdLedger.Serialize"/>
        /// refuses: a key that is not an asset GUID, or an id greater than
        /// <see cref="PrefabIdLedger.MaxAllocatableId"/>. A parsed ledger never does.
        /// </exception>
        public static PrefabAllocation AllocateFor(
            PrefabLedgerDocument document, string assetPath, string guid)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            if (!IsPrefabAssetPath(assetPath))
            {
                // ⛔ The ledger cannot make this refusal: every asset in a Unity
                // project carries a GUID of exactly the shape it accepts, so a
                // material would be recorded happily and could never be spawned.
                return PrefabAllocation.Refused("select a prefab asset in the Project window first");
            }

            if (!PrefabIdLedger.IsValidAssetGuid(guid))
            {
                // Unity answers an unimported or in-memory object with an empty
                // string. Recording it would key the ledger on nothing.
                return PrefabAllocation.Refused("Unity has no asset GUID for " + assetPath);
            }

            if (!PrefabIdLedger.TryAllocate(document, guid, out uint id, out var updated, out string error))
            {
                return PrefabAllocation.Refused(error);
            }

            string name = AssetNameOf(assetPath);

            // TryAllocate is idempotent, and saying so is the useful answer: a
            // silent no-op reads as a failure, and a save reads as a change.
            return ReferenceEquals(updated, document)
                ? PrefabAllocation.Unchanged(id, name + " already holds id " + id.ToString(CultureInfo.InvariantCulture))
                : PrefabAllocation.Allocated(updated, id,
                    "allocated id " + id.ToString(CultureInfo.InvariantCulture) + " to " + name);
        }

        /// <summary>
        /// Allocates ids for every selected prefab in one pass, producing a single
        /// ledger to write.
        /// </summary>
        /// <remarks>
        /// Selected items that are not prefab assets are skipped and counted in the
        /// message. The first refusal stops the batch and is returned. A selection
        /// of one item is handled as <see cref="AllocateFor"/> handles it.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
        public static PrefabAllocation AllocateForEach(
            PrefabLedgerDocument document, IReadOnlyList<PrefabCandidate> candidates)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));

            if (candidates.Count == 1)
            {
                // One thing selected is the common case, and its own wording says
                // more than any summary — the name it got, or the id it already had.
                return AllocateFor(document, candidates[0].AssetPath, candidates[0].Guid);
            }

            var running = document;
            int allocated = 0;
            int held = 0;
            int skipped = 0;
            string firstRefusal = null;
            uint last = 0;

            foreach (var candidate in candidates)
            {
                // ⛔ The GUID is checked HERE rather than left to AllocateFor,
                // and the difference is what a batch does about it. Unity answers
                // an empty GUID for an object it has not imported; one of those in
                // a twelve-item selection must not end the batch, so it joins the
                // skipped count. What is left for the refusal below is the size
                // cap — the one condition where continuing means asking the same
                // question again with a bigger document.
                if (!IsPrefabAssetPath(candidate.AssetPath)
                    || !PrefabIdLedger.IsValidAssetGuid(candidate.Guid))
                {
                    skipped++;
                    continue;
                }

                var one = AllocateFor(running, candidate.AssetPath, candidate.Guid);
                if (one.IsError)
                {
                    // ⛔ The first refusal ends the batch rather than being skipped
                    // past: the reachable one is the size cap, and continuing would
                    // be asking the same question again with a bigger document.
                    firstRefusal = one.Message;
                    break;
                }

                last = one.Id;
                if (one.Updated == null)
                {
                    held++;
                }
                else
                {
                    allocated++;
                    running = one.Updated;
                }
            }

            if (firstRefusal != null) return PrefabAllocation.Refused(firstRefusal);

            if (allocated == 0 && held == 0)
            {
                return PrefabAllocation.Refused(
                    "nothing in that selection is a prefab asset");
            }

            string summary = Summarise(allocated, held, skipped);
            return allocated == 0
                ? PrefabAllocation.Unchanged(last, summary)
                : PrefabAllocation.Allocated(running, last, summary);
        }

        private static string Summarise(int allocated, int held, int skipped)
        {
            var parts = new List<string>();
            if (allocated > 0) parts.Add("allocated " + Count(allocated, "id"));
            if (held > 0) parts.Add(Count(held, "prefab") + " already registered");
            if (skipped > 0) parts.Add("skipped " + Count(skipped, "selected item") + " that is not a prefab");
            return string.Join(", ", parts);
        }

        private static string Count(int n, string noun)
            => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");

        /// <summary>
        /// Whether <paramref name="assetPath"/> names a <c>.prefab</c> file.
        /// </summary>
        /// <remarks>
        /// The check is on the extension, not on the asset's type: a model file that
        /// Unity imports as a prefab is not accepted.
        /// </remarks>
        public static bool IsPrefabAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;

            // Case-insensitive because two of the three platforms Unity runs on
            // have case-insensitive file systems, so `.Prefab` reaches the same
            // asset there and would be refused here for a difference the editor
            // does not make.
            if (!assetPath.EndsWith(PrefabExtension, StringComparison.OrdinalIgnoreCase)) return false;

            // A path that is nothing but the extension names no asset.
            return AssetNameOf(assetPath) != null;
        }

        /// <summary>
        /// The file name in <paramref name="assetPath"/> without its extension, or
        /// <see langword="null"/> when there is none. Only <c>/</c> separates
        /// directories, as in every Unity asset path.
        /// </summary>
        public static string AssetNameOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            int slash = assetPath.LastIndexOf('/');
            string file = slash >= 0 ? assetPath.Substring(slash + 1) : assetPath;

            // `dot == 0` is its own case, not a missing extension: a file named
            // `.prefab` is all extension and has no stem to name a member after,
            // and `dot > 0 ? … : file` would hand back the extension itself.
            int dot = file.LastIndexOf('.');
            string name = dot > 0 ? file.Substring(0, dot) : dot == 0 ? string.Empty : file;

            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// The constant name a prefab's registration would get, before clashes with
        /// other names are resolved. <see langword="null"/> only for a
        /// <see langword="null"/> or empty name.
        /// </summary>
        /// <remarks>
        /// Every character that <see cref="char.IsLetterOrDigit(char)"/> rejects,
        /// other than <c>_</c>, becomes <c>_</c>, so <c>!!!</c> becomes <c>___</c>.
        /// A name that starts with a digit or is a C# keyword gets a leading
        /// <c>_</c>.
        /// </remarks>
        public static string CandidateMemberName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;

            var builder = new StringBuilder(prefabName.Length + 1);
            foreach (char c in prefabName)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            // A leading digit is the one case the per-character mapping cannot
            // fix, because the digit itself is legal everywhere else in the name.
            if (char.IsDigit(builder[0]))
            {
                builder.Insert(0, '_');
            }

            string candidate = builder.ToString();

            // A reserved word is a compile error where a member name is expected.
            // Prefixed rather than emitted as `@class`, because the verbatim form
            // would have to be typed at every call site too.
            return ReservedWords.Contains(candidate) ? "_" + candidate : candidate;
        }

        /// <summary>
        /// The constant name, <c>Prefab_&lt;id&gt;</c>, for a registration with no
        /// asset name to derive one from, such as one whose asset is missing.
        /// </summary>
        public static string FallbackMemberName(uint id)
            => "Prefab_" + id.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Generates the C# source of the prefab id constants for an inventory.
        /// </summary>
        /// <param name="inventory">The resolved ledger.</param>
        /// <param name="generatedNamespace">The namespace to declare the constants in;
        /// <see langword="null"/> or empty uses <see cref="DefaultNamespace"/>.</param>
        /// <remarks>
        /// <para>Refused when an id is registered to more than one prefab: re-issue
        /// one of them first. Refused too for an invalid namespace, or when two
        /// prefab names map to the same constant name; rename one of the prefabs.
        /// Each refusal is returned in <see cref="PrefabConstantsResult.Error"/>.</para>
        /// <para>A registration whose asset is missing is still emitted, because its
        /// id stays in use.</para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="inventory"/> is <see langword="null"/>.
        /// </exception>
        public static PrefabConstantsResult GenerateConstants(
            PrefabInventory inventory, string generatedNamespace)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            string ns = string.IsNullOrEmpty(generatedNamespace) ? DefaultNamespace : generatedNamespace;
            if (!IsValidNamespace(ns))
            {
                return PrefabConstantsResult.Invalid("'" + ns + "' is not a namespace");
            }

            if (inventory.DuplicateCount > 0)
            {
                return PrefabConstantsResult.Invalid(
                    "the ledger records " + inventory.DuplicateCount
                    + " registration(s) sharing an id with another; re-issue one of each pair before generating");
            }

            if (!TryResolveMemberNames(inventory, out var names, out string collision))
            {
                return PrefabConstantsResult.Invalid(collision);
            }

            var builder = new StringBuilder();
            AppendFileHeader(builder);
            builder.Append("namespace ").Append(ns).Append(Newline);
            builder.Append("{").Append(Newline);
            builder.Append("    public static class ").Append(GeneratedTypeName).Append(Newline);
            builder.Append("    {").Append(Newline);

            if (inventory.Registrations.Count == 0)
            {
                builder.Append("        // The ledger records no prefabs.").Append(Newline);
            }

            for (int i = 0; i < inventory.Registrations.Count; i++)
            {
                var registration = inventory.Registrations[i];
                if (i > 0) builder.Append(Newline);

                builder.Append("        /// <summary>")
                    .Append(XmlDocText(registration.AssetPath
                        ?? ("No asset carries GUID " + registration.Guid + " — the id is still live for every peer that spawned one.")))
                    .Append("</summary>").Append(Newline);
                builder.Append("        public const uint ").Append(names[i]).Append(" = ")
                    .Append(registration.Id.ToString(CultureInfo.InvariantCulture)).Append(";")
                    .Append(Newline);
            }

            builder.Append("    }").Append(Newline);
            builder.Append("}").Append(Newline);
            return PrefabConstantsResult.Valid(builder.ToString());
        }

        /// <summary>
        /// The prefabs in <paramref name="candidatePaths"/> that carry a
        /// networked component and hold no id in <paramref name="document"/>.
        /// </summary>
        /// <param name="document">The ledger to compare against.</param>
        /// <param name="candidatePaths">
        /// Every prefab asset path the project holds. The result keeps their order.
        /// </param>
        /// <param name="resolveGuid">Returns the GUID of an asset path.</param>
        /// <param name="inspect">Returns what an asset carries.</param>
        /// <remarks>
        /// A prefab that could not be inspected
        /// (<see cref="PrefabSpawnability.Unknown"/>) is not returned.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
        public static IReadOnlyList<string> UnregisteredSpawnables(
            PrefabLedgerDocument document,
            IEnumerable<string> candidatePaths,
            Func<string, string> resolveGuid,
            Func<string, PrefabSpawnability> inspect)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (candidatePaths == null) throw new ArgumentNullException(nameof(candidatePaths));
            if (resolveGuid == null) throw new ArgumentNullException(nameof(resolveGuid));
            if (inspect == null) throw new ArgumentNullException(nameof(inspect));

            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string path in candidatePaths)
            {
                if (!IsPrefabAssetPath(path)) continue;

                // ⛔ The ledger is keyed on the GUID, so "already registered" is
                // a question about the GUID and never about the path: a prefab
                // that was moved since its id was allocated has a new path and
                // the same registration.
                string guid = NullIfEmpty(resolveGuid(path));
                if (guid == null) continue;
                if (!seen.Add(guid)) continue;
                if (document.Prefabs.ContainsKey(guid)) continue;

                if (inspect(path) != PrefabSpawnability.Networked) continue;

                found.Add(path);
            }

            return found;
        }

        /// <summary>
        /// The rows a generated registry asset should carry: the id, and the
        /// asset path whose prefab it names.
        /// </summary>
        /// <remarks>
        /// <para>Only <see cref="PrefabRegistrationStatus.Registered"/> and
        /// <see cref="PrefabRegistrationStatus.Unverified"/> registrations with an
        /// asset become rows. <see cref="PrefabRegistrationStatus.Missing"/> ones
        /// have no asset, <see cref="PrefabRegistrationStatus.Duplicate"/> ones
        /// share an id, and <see cref="PrefabRegistrationStatus.NotNetworked"/> ones
        /// cannot be spawned; their constants are still generated.</para>
        /// <para>Rows are in the inventory's order, ascending by id, so regenerating
        /// an unchanged ledger produces the same asset.</para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="inventory"/> is <see langword="null"/>.
        /// </exception>
        public static IReadOnlyList<PrefabRegistryRow> RegistryRows(PrefabInventory inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            var rows = new List<PrefabRegistryRow>(inventory.Registrations.Count);
            foreach (var registration in inventory.Registrations)
            {
                if (registration.Status != PrefabRegistrationStatus.Registered
                    && registration.Status != PrefabRegistrationStatus.Unverified) continue;

                // ⛔ The status is the classification's answer and the path is
                // the registration's own, so a row is admitted only when both
                // hold. Build cannot separate them; this type's constructor is
                // public, and a row naming nothing is what the paragraph above
                // says this method does not produce.
                if (registration.AssetPath == null) continue;
                rows.Add(new PrefabRegistryRow(
                    registration.Id, registration.Guid, registration.AssetPath));
            }
            return rows;
        }

        /// <summary>
        /// The full name of the component that SENDS motion. ⚠️ A second statement
        /// of <c>RTMPE.Sync.NetworkTransform</c>'s own namespace and class, held to
        /// the runtime source by <c>TheMotionPairingRuleNamesTheRuntimesOwnTypesTests</c> —
        /// no project in this repository compiles that type beside this file, and a
        /// <c>typeof</c> here would put the whole classification behind a running
        /// Unity, which is exactly what the header above says this file does not do.
        /// </summary>
        internal const string NetworkTransformTypeName = "RTMPE.Sync.NetworkTransform";

        /// <summary>
        /// The full name of the component that APPLIES it on a replica. Stated on
        /// the same terms as <see cref="NetworkTransformTypeName"/>.
        /// </summary>
        internal const string NetworkTransformInterpolatorTypeName =
            "RTMPE.Sync.NetworkTransformInterpolator";

        /// <summary>The sender's simple name, for a sentence a developer reads.</summary>
        internal static string NetworkTransformSimpleName => SimpleNameOf(NetworkTransformTypeName);

        /// <summary>The receiver's simple name, on the same terms.</summary>
        internal static string NetworkTransformInterpolatorSimpleName
            => SimpleNameOf(NetworkTransformInterpolatorTypeName);

        /// <summary>
        /// Whether the components named on one prefab root pair the sender of
        /// motion with the receiver of it.
        /// </summary>
        /// <param name="rootComponentTypeFullNames">
        /// The full type names of every component on the prefab's root, not only the
        /// networked ones (<c>NetworkTransformInterpolator</c> is a
        /// <c>MonoBehaviour</c>), or <see langword="null"/> when nothing was read.
        /// <see langword="null"/> entries, such as missing scripts, are ignored.
        /// </param>
        /// <remarks>
        /// <para>Names are matched exactly (<c>RTMPE.Sync.NetworkTransform</c>,
        /// <c>RTMPE.Sync.NetworkTransformInterpolator</c>). Pass a component that
        /// derives from either under that base type's name, or it is not
        /// recognised.</para>
        /// <para>An interpolator without a <c>NetworkTransform</c> is
        /// <see cref="PrefabMotionPairing.NoTransform"/>: nothing on that root sends
        /// motion.</para>
        /// </remarks>
        public static PrefabMotionPairing ClassifyMotion(
            IReadOnlyList<string> rootComponentTypeFullNames)
        {
            // ⛔ Never NoTransform. Nothing was measured, and "this prefab has no
            // NetworkTransform" is a claim about an asset nobody read.
            if (rootComponentTypeFullNames == null) return PrefabMotionPairing.Unknown;

            bool transform = false;
            bool interpolator = false;
            for (int i = 0; i < rootComponentTypeFullNames.Count; i++)
            {
                string name = rootComponentTypeFullNames[i];
                if (name == null) continue;
                if (string.Equals(name, NetworkTransformTypeName, StringComparison.Ordinal))
                {
                    transform = true;
                }
                else if (string.Equals(
                    name, NetworkTransformInterpolatorTypeName, StringComparison.Ordinal))
                {
                    interpolator = true;
                }
            }

            if (!transform) return PrefabMotionPairing.NoTransform;
            return interpolator
                ? PrefabMotionPairing.Paired
                : PrefabMotionPairing.TransformWithoutInterpolator;
        }

        /// <summary>
        /// The name <see cref="ClassifyMotion"/> must see for one component's
        /// type: the motion type it IS or DERIVES FROM, or else its own name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The runtime resolves both components with <c>GetComponent&lt;T&gt;()</c>,
        /// which matches subclasses, so a project that derives its own
        /// interpolator is correctly paired at run time. An exact name
        /// comparison answers otherwise, and this verdict is not advisory: an
        /// unpaired prefab is accused, and the repair offered alongside the
        /// accusation appends a second component.
        /// </para>
        /// <para>
        /// Walked by name up the base chain rather than through
        /// <c>typeof(...)</c>, so this file keeps stating the two types as
        /// strings and takes no dependency on the runtime assembly for one
        /// comparison.
        /// </para>
        /// <para>
        /// The readiness scan cannot answer this. It reads serialised YAML,
        /// where a component is a script GUID and a subclass carries a
        /// different one, so the relationship is not in the file at all; that
        /// path keeps the exact-match answer and states the limit.
        /// </para>
        /// </remarks>
        internal static string CanonicalMotionTypeName(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                string full = t.FullName;
                if (string.Equals(full, NetworkTransformInterpolatorTypeName, StringComparison.Ordinal))
                {
                    return NetworkTransformInterpolatorTypeName;
                }

                if (string.Equals(full, NetworkTransformTypeName, StringComparison.Ordinal))
                {
                    return NetworkTransformTypeName;
                }
            }

            return type == null ? null : type.FullName;
        }

        /// <summary>
        /// Every registration whose prefab sends motion nothing on it can apply.
        /// </summary>
        /// <param name="inventory">The resolved ledger.</param>
        /// <param name="readRootComponentTypeNames">
        /// Returns the full type names of every component on the root of the prefab
        /// at an asset path, or <see langword="null"/> when the asset could not be
        /// read; see <see cref="ClassifyMotion"/>.
        /// </param>
        /// <remarks>
        /// Only <see cref="PrefabRegistrationStatus.Registered"/> rows are checked.
        /// Exceptions thrown by <paramref name="readRootComponentTypeNames"/> are
        /// not caught.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
        public static IReadOnlyList<PrefabMotionAdvisory> MotionAdvisories(
            PrefabInventory inventory,
            Func<string, IReadOnlyList<string>> readRootComponentTypeNames)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (readRootComponentTypeNames == null)
            {
                throw new ArgumentNullException(nameof(readRootComponentTypeNames));
            }

            var rows = new List<PrefabMotionAdvisory>();
            foreach (var registration in inventory.Registrations)
            {
                if (registration.Status != PrefabRegistrationStatus.Registered) continue;
                if (registration.AssetPath == null) continue;

                var pairing = ClassifyMotion(readRootComponentTypeNames(registration.AssetPath));
                if (pairing != PrefabMotionPairing.TransformWithoutInterpolator) continue;

                rows.Add(new PrefabMotionAdvisory(
                    registration.AssetPath, registration.Name, pairing));
            }

            return rows;
        }

        // The leaf of a dotted name. Derived rather than written twice: a sentence
        // naming `NetworkTransformInterpolator` beside a constant naming
        // `RTMPE.Sync.NetworkTransformInterpolator` is one fact in two spellings,
        // and the one a rename does not reach is the sentence.
        private static string SimpleNameOf(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }

        // Names are derived, then disambiguated against every other derived name.
        // A registration whose candidate is unique keeps it: the constant is typed
        // by hand in application code, so a name that changes because an unrelated
        // prefab arrived is a break, and the suffix is worth paying only where the
        // alternative is two members with one name.
        private static bool TryResolveMemberNames(
            PrefabInventory inventory, out IReadOnlyList<string> names, out string error)
        {
            names = null;
            error = null;

            var candidates = new string[inventory.Registrations.Count];
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Length; i++)
            {
                var registration = inventory.Registrations[i];
                candidates[i] = CandidateMemberName(registration.Name)
                    ?? FallbackMemberName(registration.Id);
                seen.TryGetValue(candidates[i], out int count);
                seen[candidates[i]] = count + 1;
            }

            var resolved = new string[candidates.Length];
            var taken = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Length; i++)
            {
                var registration = inventory.Registrations[i];
                // 🚨 Two reasons a candidate cannot stand as written, and the
                // second was missed: a name shared with another registration, and
                // the ENCLOSING TYPE's own name, which C# refuses as a member
                // (CS0542). `taken` only ever holds derived names, so it could
                // never see that one — it needs exactly ONE prefab called
                // RtmpePrefabIds to fire, and two of them collide with each other
                // and get suffixed, so the obvious test passes.
                bool ambiguous = seen[candidates[i]] > 1
                    || string.Equals(candidates[i], GeneratedTypeName, StringComparison.Ordinal);

                resolved[i] = ambiguous
                    ? candidates[i] + "_" + registration.Id.ToString(CultureInfo.InvariantCulture)
                    : candidates[i];

                // The suffix makes a colliding pair unique against each other, and
                // can still land on a third prefab that was already named that. It
                // is refused rather than suffixed again: a second round would move
                // the collision rather than end it, and the fix a developer can
                // actually apply is to rename one asset.
                if (taken.TryGetValue(resolved[i], out string other))
                {
                    error = "'" + Abbreviate(resolved[i]) + "' would name two prefabs — "
                        + Abbreviate(other) + " and " + Abbreviate(Describe(registration))
                        + "; rename one of them";
                    return false;
                }

                taken[resolved[i]] = Describe(registration);
            }

            names = resolved;
            return true;
        }

        private static string Describe(PrefabRegistration registration)
            => registration.AssetPath ?? ("GUID " + registration.Guid);

        // A ledger is a hand-edited, merged file, so an asset path in it is as
        // easily a megabyte of hostile text as a name — and this lands in the
        // Unity console. Bounded the way PrefabIdLedger.Quote bounds the same
        // class of text one file over.
        private const int MaxQuotedLength = 64;

        private static string Abbreviate(string text)
            => text != null && text.Length > MaxQuotedLength
                ? text.Substring(0, MaxQuotedLength) + "…"
                : text;

        private static void AppendFileHeader(StringBuilder builder)
        {
            builder.Append("// <auto-generated/>").Append(Newline);
            builder.Append("//").Append(Newline);
            builder.Append("// Spawn prefab ids, projected from ").Append(PrefabIdLedger.FileName)
                .Append(" by Window > RTMPE > Network Prefabs.").Append(Newline);
            builder.Append("//").Append(Newline);
            builder.Append("// Do not edit. The ledger is the record and this file is a view of it, so an")
                .Append(Newline);
            builder.Append("// edit here is lost at the next write and disagrees with every other client")
                .Append(Newline);
            builder.Append("// until then. Change an id by re-issuing it in the window.").Append(Newline);
            builder.Append(Newline);
        }

        // The doc comment carries an asset path, which is author-controlled text
        // inside a single-line XML comment. Two ways that breaks the generated
        // file: `A<B` ends the summary element, and a line break ends the comment
        // itself and leaves the rest of the path standing as code. `&` is replaced
        // first, or the ampersands introduced by the other two are escaped again.
        //
        // 🚨 The first version folded `\r` and `\n`, which is a rule about the two
        // terminators the author thought of rather than about the language: C#
        // ends a line on U+000D, U+000A, U+0085, U+2028 and U+2029, and the three
        // others are legal in a filename on every platform Unity ships on.
        // Measured: each broke the generated file with CS1585. Folded by
        // CATEGORY now — every control character plus the two separators — the
        // same rule LogRedaction and UntrustedLogText already apply one assembly
        // over, for the same reason: untrusted text crossing into a line-oriented
        // format.
        private static string XmlDocText(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '&': builder.Append("&amp;"); break;
                    case '<': builder.Append("&lt;"); break;
                    case '>': builder.Append("&gt;"); break;
                    default:
                        builder.Append(
                            char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c);
                        break;
                }
            }

            return builder.ToString();
        }

        private static bool IsValidNamespace(string ns)
        {
            foreach (string part in ns.Split('.'))
            {
                if (part.Length == 0) return false;
                if (!char.IsLetter(part[0]) && part[0] != '_') return false;
                for (int i = 1; i < part.Length; i++)
                {
                    if (!char.IsLetterOrDigit(part[i]) && part[i] != '_') return false;
                }

                if (ReservedWords.Contains(part)) return false;
            }

            return true;
        }

        // AssetDatabase answers an unknown GUID with an empty string rather than
        // null, so both spellings of "nothing" arrive here and only one of them
        // would survive a null check.
        private static string NullIfEmpty(string value)
            => string.IsNullOrEmpty(value) ? null : value;
    }
}
