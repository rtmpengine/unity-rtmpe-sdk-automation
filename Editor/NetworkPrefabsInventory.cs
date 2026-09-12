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
    /// What one recorded registration turned out to be once resolved against the
    /// project.
    /// </summary>
    /// <remarks>
    /// A single value per registration rather than a set of flags, because the
    /// consumer is a list with one status per row. Where a registration is more
    /// than one of these at once the order below is the precedence, worst first:
    /// a shared id is wrong for every client in the session, a vanished asset is
    /// wrong only for the one trying to spawn it.
    /// </remarks>
    public enum PrefabRegistrationStatus
    {
        /// <summary>This id is recorded against more than one prefab.</summary>
        Duplicate,

        /// <summary>No asset in the project carries this GUID any more.</summary>
        Missing,

        /// <summary>
        /// The asset is there and carries no networked component on its root, so
        /// the runtime refuses to spawn it.
        /// </summary>
        /// <remarks>
        /// Below <see cref="Missing"/> because a vanished asset is a
        /// registration that describes nothing, while this one describes
        /// something that cannot do the job — and above
        /// <see cref="Unverified"/> because this is a measurement and that is
        /// the absence of one.
        /// </remarks>
        NotNetworked,

        /// <summary>
        /// Resolved, but the asset could not be inspected — nothing is claimed
        /// about it either way.
        /// </summary>
        /// <remarks>
        /// ⛔ Not a fault, and deliberately not folded into
        /// <see cref="NotNetworked"/>: an asset mid-import answers no question,
        /// and reporting "carries no networked component" about one is an
        /// accusation nobody measured. It follows that this status decides
        /// nothing either: a row in this state is offered to the generated
        /// registry exactly as <see cref="Registered"/> is, and whether it
        /// reaches the asset is settled where every row is settled — at the
        /// write, by whether a prefab loads. A verdict carrying no information
        /// must not be spent as though it carried a bad one.
        /// </remarks>
        Unverified,

        /// <summary>Recorded, resolved, and unique.</summary>
        /// <remarks>
        /// ⚠️ Says nothing about spawnability unless the inventory was built
        /// with an inspector. Built without one, no row is asked, and this is
        /// the same claim it has always been.
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

        /// <summary>Loaded, and carrying none.</summary>
        Inert,
    }

    /// <summary>
    /// Whether one prefab root pairs the component that SENDS motion with the one
    /// that APPLIES it on a replica.
    /// </summary>
    /// <remarks>
    /// 🔑 A non-owner replica carrying <c>NetworkTransform</c> and no
    /// <c>NetworkTransformInterpolator</c> FREEZES: both receive paths in
    /// <c>NetworkManager.GameData</c> return without touching the transform when
    /// the interpolator is absent. The runtime says so — but only on the receive
    /// path, which is reached once a SECOND client is in the room, so a developer
    /// testing alone never hears it. This is the same fact asked of the asset,
    /// before anyone runs anything.
    /// <para>
    /// ⛔ Not folded into <see cref="PrefabRegistrationStatus"/> and deliberately
    /// not part of <see cref="PrefabInventory.IsClean"/>: such a prefab is
    /// registerable, spawnable and has a valid id, and <c>IsClean</c> gates
    /// constant generation. Refusing <c>Generate</c> over a motion advisory would
    /// stop a project building over something that is not a fault of the ledger.
    /// </para>
    /// </remarks>
    public enum PrefabMotionPairing
    {
        /// <summary>Nothing was measured — the asset answered no component list at all.</summary>
        /// <remarks>
        /// ⛔ Its own value rather than a synonym for <see cref="NoTransform"/>.
        /// "This prefab has no NetworkTransform" is a claim; an asset mid-import
        /// supports no claim, and reporting the two the same way is the reassuring
        /// direction — the one direction an advisory must never be wrong in.
        /// </remarks>
        Unknown,

        /// <summary>Measured, and no <c>NetworkTransform</c> on the root.</summary>
        NoTransform,

        /// <summary>Both components on the root: motion is sent and applied.</summary>
        Paired,

        /// <summary>
        /// <c>NetworkTransform</c> on the root and no interpolator beside it —
        /// the configuration a remote replica cannot move under.
        /// </summary>
        TransformWithoutInterpolator,
    }

    /// <summary>
    /// One registered prefab whose root carries motion the receiving half cannot
    /// apply, in the words the window shows.
    /// </summary>
    public sealed class PrefabMotionAdvisory
    {
        public PrefabMotionAdvisory(string assetPath, string name, PrefabMotionPairing pairing)
        {
            AssetPath = assetPath;
            Name = name;
            Pairing = pairing;
        }

        /// <summary>Where the asset sat when the advisory was taken.</summary>
        public string AssetPath { get; }

        /// <summary>The asset's file name without its extension.</summary>
        public string Name { get; }

        /// <summary>The verdict that produced the row.</summary>
        public PrefabMotionPairing Pairing { get; }

        /// <summary>
        /// What is wrong, in the words the window renders.
        /// </summary>
        /// <remarks>
        /// 🚨 Asked of the row rather than composed while drawing, for the reason
        /// <see cref="PrefabRegistration.Explanation"/> is: a sentence chosen
        /// inline in <c>OnGUI</c> is a sentence no test can reach, and this one
        /// has to name the component a developer must add.
        /// </remarks>
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
        /// Where the asset sat when this inventory was built, or
        /// <see langword="null"/> when nothing carried the GUID. Never persisted:
        /// the ledger stores only the GUID, because a prefab that is moved or
        /// renamed is the same prefab and a recorded path would be the one thing
        /// in this system that disagrees with that.
        /// <para>
        /// 🚨 This is a SNAPSHOT, not a live read, and the first version of this
        /// comment claimed otherwise. An inventory outlives the asset database
        /// state it was built from, so whoever holds one is responsible for
        /// rebuilding it when the project changes — <c>NetworkPrefabsWindow</c>
        /// does that from <c>OnProjectChange</c>. Without it a deleted prefab
        /// keeps rendering as registered and its id keeps reaching the generated
        /// constants.
        /// </para>
        /// </summary>
        public string AssetPath { get; }

        public PrefabRegistrationStatus Status { get; }

        /// <summary>
        /// Whether burning this id is an operation that can succeed.
        /// </summary>
        /// <remarks>
        /// 🔑 Here rather than in the window, which is where it started. Which
        /// operations a row offers is policy, and policy derived inline while
        /// drawing is a rule no test can reach. It agrees with
        /// <c>PrefabIdLedger.TryRetire</c>'s own refusal by construction — an id
        /// two prefabs hold cannot be burned without stranding the other's peers —
        /// and the two are held together by a test rather than by coincidence.
        /// </remarks>
        public bool CanRetire => Status != PrefabRegistrationStatus.Duplicate;

        /// <summary>
        /// Whether moving this prefab to a fresh id is the operation this row
        /// needs. True exactly for a shared id, which is the state re-issuing
        /// exists to resolve.
        /// </summary>
        public bool CanReissue => Status == PrefabRegistrationStatus.Duplicate;

        /// <summary>
        /// What is wrong with this registration, in the words the window shows,
        /// or <see langword="null"/> when nothing is.
        /// </summary>
        /// <remarks>
        /// 🚨 Asked of the row rather than decided while drawing, for the same
        /// reason <see cref="CanRetire"/> is — and this one had to be moved.
        /// The window chose between two sentences with a ternary on
        /// <see cref="PrefabRegistrationStatus.Duplicate"/>, so the arrival of a
        /// third state would have rendered "no asset in this project carries
        /// this GUID" underneath a prefab that is sitting right there. A rule
        /// with an else-branch is a rule that answers every question it is asked,
        /// including the ones it has never heard of.
        /// </remarks>
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
        /// Ascending by id, then by GUID. The second key is not decoration: two
        /// registrations sharing an id is the whole reason the repair reading
        /// exists, and without it those rows would order by whatever the
        /// dictionary happened to hand back.
        /// </summary>
        public IReadOnlyList<PrefabRegistration> Registrations { get; }

        public int DuplicateCount { get; }

        public int MissingCount { get; }

        /// <summary>Rows whose asset carries no networked component on its root.</summary>
        public int NotNetworkedCount { get; }

        /// <summary>Rows the inventory could not inspect.</summary>
        /// <remarks>
        /// ⛔ Not part of <see cref="IsClean"/>: an unanswered question is not a
        /// fault, and a project mid-import would otherwise read as broken.
        /// </remarks>
        public int UnverifiedCount { get; }

        /// <summary>
        /// No registration is duplicated and none has lost its asset.
        /// <para>
        /// ⛔ Not the same as "the ledger parsed" — a ledger read for repair parses
        /// with duplicates in it, which is exactly what this reports as unclean.
        /// ⛔ And not "generation will succeed": two prefabs whose names resolve to
        /// one member are a clean ledger that <see cref="GenerateConstants"/> still
        /// refuses, because the decision there is a rename rather than a re-issue.
        /// </para>
        /// </summary>
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
        /// Whether a file was there at all. ⛔ Kept apart from
        /// <see cref="Error"/>: an absent ledger is an empty project and a safe
        /// place to allocate from, an unreadable one is a file somebody must look
        /// at, and conflating them offers the destructive action in the one case
        /// where it destroys something.
        /// </summary>
        public bool Existed { get; }

        /// <summary>
        /// The document came back through the repair reading, so it holds at
        /// least one id claimed by two prefabs.
        /// </summary>
        public bool NeedsRepair { get; }

        public string Error { get; }

        public bool IsUsable => Document != null;

        /// <summary>No file — an empty ledger, and nothing wrong.</summary>
        public static PrefabLedgerRead Absent()
            => new PrefabLedgerRead(PrefabLedgerDocument.Empty, false, false, null);

        /// <summary>
        /// The file was there and could not be read as bytes at all — a lock, a
        /// permission, a device error. The caller supplies the reason because only
        /// it knows what it was doing.
        /// </summary>
        public static PrefabLedgerRead Unreadable(string error)
            => new PrefabLedgerRead(null, true, false, error
                ?? "the ledger could not be read");

        /// <summary>
        /// Reads ledger text: strictly first, then through the repair reading,
        /// which admits the one defect a merge produces and nothing else.
        /// </summary>
        /// <remarks>
        /// ⛔ The error reported on total failure is the STRICT reader's. The
        /// repair reader accepts a superset, so its complaint about a file neither
        /// accepts is a rewording of the same fault — and the strict message is
        /// the one that also describes files the repair reading would have taken.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="text"/> is <see langword="null"/>. ⛔ Deliberately not
        /// treated as an absent file: only the caller knows whether it holds no
        /// file or unreadable bytes, and <see cref="Absent"/> is how it says the
        /// first. Conflating them offers the destructive action in the one case
        /// where it destroys something.
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

    /// <summary>One thing the user selected, resolved by the editor.</summary>
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
    /// What allocating an id for one selected asset came to: a ledger to write,
    /// or a sentence saying why there is none.
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
    /// One row of the generated registry asset: an id, and the asset the Editor
    /// resolved it to at generation time.
    /// </summary>
    /// <remarks>
    /// A path rather than the prefab itself, because this type is decided
    /// without an asset database — the caller turns the path into a reference,
    /// and every rule about WHICH registrations become rows is then reachable
    /// from a test that has no Unity.
    /// </remarks>
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
        /// The asset GUID the ledger is keyed on — what a writer should resolve
        /// through, because it is the thing that does not move.
        /// </summary>
        public string Guid { get; }

        /// <summary>
        /// Where the prefab sat when the inventory was built. A snapshot, and
        /// carried for a diagnostic rather than for a lookup: the ledger records
        /// no path precisely because a prefab that is moved or renamed is the
        /// same prefab.
        /// </summary>
        public string AssetPath { get; }
    }

    /// <summary>
    /// Resolves a ledger against the project and projects it into C# constants.
    /// </summary>
    public static class NetworkPrefabsInventory
    {
        /// <summary>The generated type's name, and the path it is written to.</summary>
        public const string GeneratedTypeName = "RtmpePrefabIds";

        /// <summary>
        /// Where the generated file goes, relative to the project root. Inside
        /// <c>Assets/</c> because Unity compiles nothing outside it, and under a
        /// directory of our own so deleting the whole thing is a safe way to
        /// start over.
        /// </summary>
        public const string GeneratedAssetPath = "Assets/RTMPE/Generated/" + GeneratedTypeName + ".cs";

        /// <summary>
        /// Where the generated <c>NetworkPrefabRegistry</c> asset goes. Beside
        /// the constants, from the same button and the same ledger, because the
        /// two are one fact in two shapes: what a project types to spawn, and
        /// what the player resolves it to.
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
        /// The resolver is a delegate rather than a call into AssetDatabase so the
        /// classification is reachable without an editor. Nothing below catches
        /// what it throws, because a failing asset database is not a state this
        /// layer can describe — and the overload beside this one takes a second
        /// delegate on the same terms, so the caller owns both.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Either argument is <see langword="null"/>. ⛔ Stated because the caller
        /// is a window: an exception out of here escapes <c>OnGUI</c>, which runs
        /// every frame, so a surface that can throw is one the window must have
        /// decided about rather than discovered.
        /// </exception>
        public static PrefabInventory Build(
            PrefabLedgerDocument document, Func<string, string> resolveAssetPath)
            => Build(document, resolveAssetPath, null);

        /// <summary>
        /// As the two-argument form, and additionally asks
        /// <paramref name="inspect"/> what each resolved asset carries.
        /// </summary>
        /// <remarks>
        /// ⛔ An overload rather than a third parameter: this method is public
        /// and the window is not its only caller, so widening the signature
        /// would be a break for a question every caller does not have to ask.
        /// <para>
        /// A <see langword="null"/> inspector asks nothing, and that is not the
        /// same as asking and being told nothing: no row is inspected, none is
        /// counted as unverified, and the result is what this method has always
        /// returned. Only a caller that supplies one can see
        /// <see cref="PrefabRegistrationStatus.NotNetworked"/>.
        /// </para>
        /// <para>
        /// ⚠️ Asked only where the answer can be used — a row that already has a
        /// worse verdict is not inspected at all, which keeps the cost to one
        /// asset load per row that would otherwise have read as fine.
        /// </para>
        /// <para>
        /// ⚠️ <paramref name="inspect"/> is uncaught on the same terms as the
        /// resolver, and the window's own reaches the asset database — so a
        /// throw from it escapes <c>OnGUI</c> exactly as one from the resolver
        /// does.
        /// </para>
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
        /// Decides what allocating an id for one selection means, given the path
        /// and GUID the editor resolved for it.
        /// </summary>
        /// <remarks>
        /// Here rather than in the window because each arm is a rule rather than a
        /// rendering: what may be allocated against, what an editor answering with
        /// no GUID means, and that asking twice for the same asset is an answer
        /// rather than a failure.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="document"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// The document holds an entry <see cref="PrefabIdLedger.Serialize"/>
        /// refuses — a key that is not a GUID, or the pool's sentinel id. ⛔ Not
        /// reachable from a parsed ledger, which is why the window guards
        /// <c>Serialize</c> at the commit rather than here; a hand-built document
        /// is the only way in.
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
        /// Allocates for a whole selection in one pass, chaining each result into
        /// the next so the batch is a single ledger to write.
        /// </summary>
        /// <remarks>
        /// 🔑 The button says "for selection", and a selection is however many
        /// things the user highlighted. Serving only the active one is a silent
        /// drop: eleven of twelve prefabs get no id and nothing says so. ⛔ And the
        /// chaining is what makes it correct rather than merely plural — allocating
        /// each against the ORIGINAL document would hand every one of them the same
        /// next-free number.
        /// <para>
        /// A candidate that is not a prefab is counted rather than refused, because
        /// selecting a folder alongside three prefabs is an ordinary thing to do and
        /// refusing the batch for it would be useless. The count is reported, so it
        /// is not silent either.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
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
        /// True for a path whose extension is <c>.prefab</c>. Used to refuse an
        /// allocation against a material or a scene, which the ledger itself
        /// cannot refuse — every asset in a Unity project has a GUID of exactly
        /// the shape it accepts.
        /// <para>
        /// ⛔ An extension test, NOT a type test, and the difference is real both
        /// ways: a `readme.txt` renamed to `.prefab` imports as a DefaultAsset and
        /// passes this, while an `.fbx` model root — which Unity DOES import as a
        /// prefab — fails it. Refusing the model is right, since it carries no
        /// NetworkBehaviour, but the reason is the extension rather than the type.
        /// The type question is <c>AssetDatabase.GetMainAssetTypeAtPath</c>, an
        /// editor call this layer is deliberately free of; the window asks it.
        /// </para>
        /// </summary>
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
        /// <see langword="null"/> when there is none. Hand-written rather than
        /// Path.GetFileNameWithoutExtension because Unity asset paths are always
        /// '/'-separated regardless of platform, and the framework helper splits
        /// on the platform's separator — which on Windows also treats '\' as one,
        /// so a prefab legitimately named with a backslash would lose everything
        /// before it.
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
        /// The member name a registration would be published under, before
        /// collisions are resolved. <see langword="null"/> only for an absent or
        /// empty name — ⛔ never for a name with no identifier CHARACTER in it,
        /// which becomes one underscore per character and is a legal member name.
        /// `!!!` is `___`, not a fallback to the id.
        /// </summary>
        /// <remarks>
        /// The admitted character set is C#'s, narrowed to what
        /// <see cref="char.IsLetterOrDigit(char)"/> answers — a strict subset of
        /// what the language allows, and the same subset the variable-id sidecar
        /// admits for member names one assembly over. Narrower than the compiler
        /// is the safe direction: a name this refuses becomes an underscore, a
        /// name it wrongly admits is a generated file that does not build.
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
        /// The member name for a registration with no name to derive one from —
        /// usually an asset that is gone, but equally a path with no stem, which a
        /// hand-edited ledger can hold. Either way the id is live on the wire.
        /// </summary>
        public static string FallbackMemberName(uint id)
            => "Prefab_" + id.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Projects an inventory into a compilable C# source file.
        /// </summary>
        /// <remarks>
        /// ⛔ Refuses a ledger holding a duplicate rather than emitting it. Two
        /// prefabs under one id is a state where clients disagree about what that
        /// number names, and generating from it would publish the disagreement as
        /// source that compiles — the failure would then surface as the wrong
        /// object spawning, at runtime, on somebody else's machine. Re-issue one
        /// of the two first; that is the operation that resolves it.
        /// <para>
        /// A missing asset is emitted, because the opposite reasoning applies: the
        /// id is spoken for by every peer already running, and omitting it would
        /// let the next allocation look free to a reader of this file.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="inventory"/> is <see langword="null"/>. ⛔ Every other
        /// refusal — a bad namespace, a shared id, two prefabs resolving to one
        /// member — is a returned reason rather than a throw, because each is a
        /// state a project can legitimately be in and the caller has to render it.
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
        /// Every prefab asset path the project holds, in the order the caller
        /// found them. The order is preserved: the window offers them as a list
        /// and a list that reshuffles between scans is one nobody can act on.
        /// </param>
        /// <param name="resolveGuid">Asset path to GUID.</param>
        /// <param name="inspect">What each asset carries.</param>
        /// <remarks>
        /// ⛔ Only what is BOTH spawnable and unregistered. A prefab with no
        /// networked component is not something the author forgot to register —
        /// it is not a networked prefab — and offering it would turn "allocate
        /// for all" into a button that burns wire ids on scenery.
        /// <para>
        /// ⚠️ An asset that could not be inspected is not offered either. This
        /// is the one place where <see cref="PrefabSpawnability.Unknown"/> is
        /// treated as a refusal rather than as an absence of information, and
        /// the asymmetry is deliberate: allocating an id is a WRITE, and a write
        /// made on a question nobody could answer is one somebody has to undo.
        /// </para>
        /// </remarks>
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
        /// ⛔ Only registrations that resolve. A <see cref="PrefabRegistrationStatus.Missing"/>
        /// one names no asset, so there is nothing to reference — its id stays
        /// in the constants, where it is documented as spoken for, and stays out
        /// of the registry, where a row would have to point somewhere. A
        /// <see cref="PrefabRegistrationStatus.Duplicate"/> one cannot be
        /// written either: two prefabs sharing an id is one row that has to name
        /// both. Generation refuses a ledger holding any duplicate before this
        /// is reached, so that arm is defence rather than policy — and defence
        /// is the right word, because this method is public and the refusal is
        /// somewhere else.
        /// <para>
        /// A <see cref="PrefabRegistrationStatus.NotNetworked"/> one is left out
        /// for a third reason, and it is NOT that a row would move the failure
        /// to play time: its constant stays either way, so code naming it
        /// compiles and the spawn fails at play time whether the row is there or
        /// not. What differs is which failure — with a row the runtime
        /// instantiates the prefab, finds no networked component on the root and
        /// destroys the instance; without one the spawn is refused before
        /// anything is created, under a line that names the id and not the
        /// prefab. The reason to leave it out is the registry's own contract:
        /// every row is a hard reference, so a row here pulls the prefab and its
        /// whole dependency tree into memory for an id nothing can spawn. The
        /// Editor warning is where the fault is actionable, and neither
        /// arrangement makes it disappear.
        /// </para>
        /// <para>
        /// A <see cref="PrefabRegistrationStatus.Unverified"/> one is a row, and
        /// the reason is that it resolves and is unique — what failed is the
        /// inspection, not the registration. An inventory built with no
        /// inspector offers that same prefab, so refusing it here would make the
        /// output depend on an answer defined to carry none: asking and being
        /// told nothing would decide against the row where not asking decides
        /// for it.
        /// </para>
        /// <para>
        /// ⛔ A row is a CANDIDATE and not an entry. Whether one reaches the
        /// asset is settled at the write, which resolves the GUID and keeps only
        /// what loads as a prefab — so admitting this status widens what may be
        /// written and moves no decision out of the writer. What it buys is the
        /// case the two differ in: an inspection taken before an import finished
        /// no longer excludes a prefab that is there by the time the registry is
        /// written.
        /// </para>
        /// <para>
        /// Order is the inventory's, which is ascending by id. A registry whose
        /// row order moved with nothing else would show a diff on every
        /// regeneration.
        /// </para>
        /// </remarks>
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
        /// The full names of the behaviours on the ROOT, or
        /// <see langword="null"/> when nothing was measured.
        /// ⚠️ Every behaviour, not only the networked ones. The RECEIVING half
        /// of the pair — <c>NetworkTransformInterpolator</c> — is a plain
        /// <c>MonoBehaviour</c>, so a caller that handed over only the
        /// <c>NetworkBehaviour</c>s could never make this answer
        /// <see cref="PrefabMotionPairing.Paired"/> and would accuse every
        /// correctly-paired prefab it was pointed at.
        /// </param>
        /// <remarks>
        /// 🔑 Type NAMES rather than types, so this rule is reachable from a test
        /// that supplies a list of strings — the same accommodation
        /// <c>ReadinessArtifactData.ExpectedChecks</c> already makes for a value
        /// its assembly cannot reference. The window turns an asset into the list;
        /// every judgement made about it is here.
        /// <para>
        /// ⚠️ A STATED limit: the match is on the full name, so a project's own
        /// class deriving from <c>NetworkTransform</c> is not recognised and this
        /// answers <see cref="PrefabMotionPairing.NoTransform"/> for it. That is
        /// silence, never a false accusation — and it is the same bound
        /// <c>PrefabMotionScanner</c> states in the text it emits, because both
        /// read a declaration rather than a type hierarchy.
        /// </para>
        /// <para>
        /// ⛔ An interpolator with no transform beside it is
        /// <see cref="PrefabMotionPairing.NoTransform"/> and not a fault: nothing
        /// on that root sends motion, so nothing is owed a receiver.
        /// </para>
        /// <para>
        /// A <see langword="null"/> entry is walked past rather than dereferenced.
        /// A missing script serialises as a null component, which is exactly what
        /// the runtime's own root read walks past.
        /// </para>
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
        /// Answers an asset path with the full names of the behaviours on its root,
        /// or <see langword="null"/> when the asset could not be read. ⚠️ Every
        /// behaviour: see <see cref="ClassifyMotion"/>, whose receiving half is a
        /// plain <c>MonoBehaviour</c>.
        /// </param>
        /// <remarks>
        /// The reader is a delegate for the reason every other question here takes
        /// one: the asset database lives in the window, and every rule stays
        /// reachable from a test that supplies a dictionary.
        /// <para>
        /// ⚠️ Asked only of rows the ledger has nothing worse to say about. A
        /// missing asset cannot be read, an inert one carries no networked
        /// component at all, and an unverified one has already declined to load —
        /// so asking would cost an asset load per row to learn nothing.
        /// </para>
        /// <para>
        /// ⚠️ <paramref name="readRootComponentTypeNames"/> is uncaught, on the
        /// same terms as <c>Build</c>'s inspector: the window's own reaches the
        /// asset database, so a throw from it escapes <c>OnGUI</c>.
        /// </para>
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
