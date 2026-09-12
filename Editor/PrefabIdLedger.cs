// RTMPE SDK — Editor/PrefabIdLedger.cs
//
// The committed identity ledger for spawnable prefabs: asset GUID → spawn
// prefab id.
//
// A NetworkVariable and an Enhanced RPC need no ledger at all: their identities
// are DERIVED from the owning type's name and the member's, so every machine
// computes the same number from the same source and the `<Type>.rtmpe-ids.json`
// sidecar beside them is provenance, not authority. A prefab has no such name to
// derive from — the asset's identity is its GUID, which is not a wire value —
// and `SpawnManager.RegisterPrefab(uint, GameObject)` takes a
// number the author picks by hand, and the two failure modes that follow — no
// prefab under an id, and two builds disagreeing about which prefab an id names
// — are documented in the SDK's own troubleshooting guide as things that happen.
// This ledger closes that asymmetry: the id is allocated once, recorded against
// the asset's GUID, and travels with the project.
//
// Keyed on the GUID rather than the asset path because a prefab that is renamed
// or moved is the same prefab, and a ledger that disagreed with that would burn
// an id on every refactor. The path is never stored: the editor resolves it live
// from the GUID, so there is no second copy of it to drift.
//
// Deliberately free of UnityEditor and UnityEngine types. The file format is the
// part that has to be right, and keeping it addressable without an editor is
// what lets it be tested as a format rather than as a window.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RTMPE.Editor
{
    /// <summary>One parsed prefab ledger.</summary>
    public sealed class PrefabLedgerDocument
    {
        public PrefabLedgerDocument(
            IReadOnlyDictionary<string, uint> prefabs,
            IEnumerable<uint> retired)
        {
            // ⚠️ Copied, never adopted. `IReadOnlyDictionary` is a facade over
            // whatever was handed in, so a caller keeping its own reference — or
            // one that casts the property back — edits a document somebody else
            // is holding. Every invariant here is established once, and only a
            // private copy keeps it true afterwards.
            var ownPrefabs = new Dictionary<string, uint>(StringComparer.Ordinal);
            if (prefabs != null)
            {
                foreach (var entry in prefabs) ownPrefabs[entry.Key] = entry.Value;
            }

            var ownRetired = new SortedSet<uint>();
            if (retired != null)
            {
                foreach (uint id in retired) ownRetired.Add(id);
            }

            Prefabs = ownPrefabs;
            Retired = ownRetired;
        }

        /// <summary>Asset GUID → the spawn prefab id registered for it.</summary>
        public IReadOnlyDictionary<string, uint> Prefabs { get; }

        /// <summary>
        /// Ids burned by a prefab leaving the project or being re-issued,
        /// ascending. A burned id is never handed out again: a peer built before
        /// the change still spawns it, and re-using the number would put a
        /// different prefab on the far side of the same wire value.
        /// </summary>
        /// <remarks>
        /// 🔑 A set of ids, not a map from GUID. What is burned is the NUMBER; the
        /// asset that held it may be gone, or may still be here under a new id.
        /// Keying this by GUID made those two states inexpressible — one burned id
        /// per asset — so a prefab could be retired and never re-issued, and the
        /// error naming the way out named an operation that did not exist.
        /// </remarks>
        public IReadOnlyCollection<uint> Retired { get; }

        /// <summary>A fresh empty ledger — the state of a project that has never allocated.</summary>
        /// <remarks>
        /// ⛔ A new instance per read. As a shared singleton, one stray cast of its
        /// facade anywhere in the editor domain would poison every later
        /// allocation for the session.
        /// </remarks>
        public static PrefabLedgerDocument Empty => new PrefabLedgerDocument(null, null);
    }

    /// <summary>A parse that either produced a document or says why it did not.</summary>
    public sealed class PrefabLedgerParseResult
    {
        private PrefabLedgerParseResult(PrefabLedgerDocument document, string error)
        {
            Document = document;
            Error = error;
        }

        public PrefabLedgerDocument Document { get; }

        public string Error { get; }

        public bool IsValid => Document != null;

        public static PrefabLedgerParseResult Valid(PrefabLedgerDocument document)
            => new PrefabLedgerParseResult(document, null);

        public static PrefabLedgerParseResult Invalid(string error)
            => new PrefabLedgerParseResult(null, error);
    }

    /// <summary>
    /// Reads, writes and allocates against the spawn prefab ledger.
    /// </summary>
    public static class PrefabIdLedger
    {
        /// <summary>The schema this build writes, and the only one it reads.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// The ledger's name. ⛔ Resolved against the folder that HOLDS
        /// <c>Assets/</c>, not against <c>Assets/</c> itself — it sits beside
        /// <c>Packages/</c> and <c>ProjectSettings/</c>, where Unity does not
        /// import it and it needs no <c>.meta</c>. An earlier version of this
        /// line said Assets, and sent a reader to the wrong directory.
        /// </summary>
        public const string FileName = "rtmpe-prefabs.json";

        /// <summary>
        /// A ledger larger than this is not a ledger, and it is the only bound
        /// there is: at forty bytes an entry an independent count could fire only
        /// on a document this already refuses.
        /// </summary>
        public const int MaxTextLength = 1024 * 1024;

        /// <summary>The largest id this ledger will issue or admit.</summary>
        /// <remarks>
        /// ⛔ <c>uint.MaxValue</c> is spoken for. <c>INetworkObjectPool.Release</c>
        /// publishes it as the sentinel for "this instance carried no prefab id",
        /// <c>SpawnManager</c> passes it on every despawn it cannot map, and the
        /// pool the getting-started guide ships destroys the instance on sight of
        /// it. A prefab registered under that number is destroyed on despawn
        /// instead of pooled, on every client, for ever.
        /// 🔑 Zero, by contrast, is ordinary — that is the same comment's other
        /// half, and the reason the sentinel had to be at the top of the range.
        /// </remarks>
        public const uint MaxAllocatableId = uint.MaxValue - 1;

        private const string Newline = "\n";
        private const int AssetGuidLength = 32;

        // A uint is ten digits at most. ⚠️ This bounds the WORK, not the answer:
        // a longer run fails `uint.TryParse` either way, so what the cap buys is
        // refusing a 900,000-digit sequence before a substring of it is
        // materialised. Nothing it admits or refuses differs.
        private const int MaxDigits = 10;

        // What an error may quote back from the file. A ledger is a megabyte of
        // hostile text as easily as it is a hand edit, and the reason lands in
        // the Unity console.
        private const int MaxQuotedLength = 64;

        /// <summary>
        /// The GUID shape Unity writes into a <c>.meta</c> file: thirty-two
        /// lowercase hex digits. Public because the ledger is not the only party
        /// that needs it — a caller that mints a key this parser will later refuse
        /// writes a file the toolchain can never open again.
        /// </summary>
        public static bool IsValidAssetGuid(string guid)
        {
            if (guid == null || guid.Length != AssetGuidLength)
            {
                return false;
            }

            foreach (char c in guid)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Parses ledger text. A <c>null</c> argument is an error rather than an
        /// empty ledger: an absent file and an unreadable one are different
        /// answers, and only the caller knows which it is holding.
        /// </summary>
        public static PrefabLedgerParseResult Parse(string text)
            => Parse(text, admitDuplicateIds: false);

        /// <summary>
        /// The reading the repair surface needs: a ledger whose only defect is one
        /// id held by two prefabs — the shape two branches produce when each
        /// allocates the next free number — comes back as data so it can be shown
        /// and resolved. Every other deviation stays fail-closed exactly as in the
        /// strict path.
        /// </summary>
        public static PrefabLedgerParseResult ParseForRepair(string text)
            => Parse(text, admitDuplicateIds: true);

        private static PrefabLedgerParseResult Parse(string text, bool admitDuplicateIds)
        {
            if (text == null)
            {
                return PrefabLedgerParseResult.Invalid(
                    "no ledger text — an absent file is represented by not calling Parse");
            }

            if (text.Length > MaxTextLength)
            {
                return PrefabLedgerParseResult.Invalid("ledger exceeds the size cap");
            }

            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                return PrefabLedgerParseResult.Invalid("ledger begins with a byte-order mark");
            }

            return new Reader(text).ParseDocument(admitDuplicateIds);
        }

        /// <summary>
        /// Serializes to the single canonical form: registrations ordinal-ascending
        /// by GUID, burned ids numerically ascending, one entry per line, LF, and
        /// a trailing newline. A ledger is committed and merged, so a writer that
        /// reordered on rewrite would produce a diff on every save and a conflict
        /// on every branch that touched an unrelated entry.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// A key that is not an asset GUID, or an id past
        /// <see cref="MaxAllocatableId"/>. ⛔ Checked rather than trusted: what
        /// this class offers is that its own reader accepts what it writes, and a
        /// hand-built document could otherwise serialise into a well-formed file
        /// describing different entries than the one it held.
        /// </exception>
        public static string Serialize(PrefabLedgerDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            foreach (var entry in document.Prefabs)
            {
                if (!IsValidAssetGuid(entry.Key))
                {
                    throw new ArgumentException(
                        "'" + Quote(entry.Key) + "' is not an asset GUID", nameof(document));
                }

                RefuseReservedId(entry.Value);
            }

            foreach (uint id in document.Retired)
            {
                RefuseReservedId(id);
            }

            var builder = new StringBuilder();
            builder.Append("{").Append(Newline);
            builder.Append("  \"schema_version\": ")
                .Append(CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture))
                .Append(",").Append(Newline);

            AppendRegistrations(builder, document.Prefabs);
            AppendRetired(builder, document.Retired);

            builder.Append("}").Append(Newline);
            return builder.ToString();
        }

        /// <summary>
        /// The id a fresh allocation would take: the smallest value neither
        /// registered nor burned. False when every allocatable id is spoken for.
        /// </summary>
        /// <remarks>
        /// Smallest-free rather than highest-plus-one, so the numbers stay small
        /// and an allocation is a function of the ledger's contents alone.
        /// <para>
        /// ⚠️ The ceiling on the scan states the contract — this never issues the
        /// pool's sentinel — but nothing can reach it: exhausting the range needs
        /// four billion entries and the size cap admits about twenty-four
        /// thousand. What actually keeps the sentinel out of a written ledger is
        /// <see cref="Serialize"/>, which refuses it, and the reader, which
        /// refuses it on the way back in.
        /// </para>
        /// </remarks>
        public static bool TryNextFreeId(PrefabLedgerDocument document, out uint id)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            var taken = new HashSet<uint>();
            foreach (uint registered in document.Prefabs.Values) taken.Add(registered);
            foreach (uint burned in document.Retired) taken.Add(burned);

            for (uint candidate = 0; candidate <= MaxAllocatableId; candidate++)
            {
                if (!taken.Contains(candidate))
                {
                    id = candidate;
                    return true;
                }
            }

            id = 0;
            return false;
        }

        /// <summary>
        /// Returns the id this prefab already holds, or allocates the next free
        /// one. <paramref name="updated"/> is the ledger to write back; it is the
        /// same instance when nothing changed.
        /// </summary>
        public static bool TryAllocate(
            PrefabLedgerDocument document,
            string guid,
            out uint id,
            out PrefabLedgerDocument updated,
            out string error)
        {
            id = 0;
            updated = document;
            error = null;

            if (document == null) throw new ArgumentNullException(nameof(document));

            if (!IsValidAssetGuid(guid))
            {
                error = "'" + Quote(guid) + "' is not an asset GUID";
                return false;
            }

            if (document.Prefabs.TryGetValue(guid, out uint existing))
            {
                id = existing;
                return true;
            }

            return TryIssue(document, guid, document.Retired, out id, out updated, out error);
        }

        /// <summary>
        /// Moves a registration into the burned set, and the prefab out of the
        /// ledger.
        /// </summary>
        public static bool TryRetire(
            PrefabLedgerDocument document,
            string guid,
            out PrefabLedgerDocument updated,
            out string error)
        {
            updated = document;
            error = null;

            if (document == null) throw new ArgumentNullException(nameof(document));

            if (!document.Prefabs.TryGetValue(guid, out uint id))
            {
                error = "'" + Quote(guid) + "' holds no registration to retire";
                return false;
            }

            // ⛔ Burning an id another prefab still holds writes a ledger NO
            // reading opens — not the strict one and not the repair one — so the
            // merge conflict this operation was reached for becomes a file only a
            // hand edit recovers. Re-issue one of the two instead; that is the
            // operation that resolves a duplicate.
            if (StillHeldByAnother(document, guid, id))
            {
                error = "id " + id.ToString(CultureInfo.InvariantCulture)
                    + " is registered to more than one prefab; re-issue one of them before retiring";
                return false;
            }

            updated = new PrefabLedgerDocument(
                Without(document.Prefabs, guid), Plus(document.Retired, id));
            return true;
        }

        /// <summary>
        /// Burns this prefab's current id and issues it a new one — the operation
        /// that resolves a duplicate, and the way a re-added prefab returns to the
        /// ledger without reclaiming a number its old peers still hold.
        /// </summary>
        public static bool TryReissue(
            PrefabLedgerDocument document,
            string guid,
            out uint id,
            out PrefabLedgerDocument updated,
            out string error)
        {
            id = 0;
            updated = document;
            error = null;

            if (document == null) throw new ArgumentNullException(nameof(document));

            if (!document.Prefabs.TryGetValue(guid, out uint current))
            {
                error = "'" + Quote(guid) + "' holds no registration to re-issue";
                return false;
            }

            // 🔑 The old id is burned only when nobody else still holds it. Where
            // two prefabs share one, re-issuing this prefab leaves the number live
            // and correct for the other — which is what resolves the merge, and
            // burning it there would strand that prefab's peers.
            var retired = StillHeldByAnother(document, guid, current)
                ? document.Retired
                : Plus(document.Retired, current);

            return TryIssue(document, guid, retired, out id, out updated, out error);
        }

        /// <summary>
        /// Every id held by more than one registration, ascending. Empty for any
        /// ledger <see cref="Parse"/> accepted; non-empty is what a merge of two
        /// branches that each allocated produces, and it is the reason the repair
        /// reading exists.
        /// </summary>
        public static IReadOnlyList<uint> DuplicatedIds(PrefabLedgerDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            var seen = new HashSet<uint>();
            var duplicated = new SortedSet<uint>();
            foreach (uint id in document.Prefabs.Values)
            {
                if (!seen.Add(id))
                {
                    duplicated.Add(id);
                }
            }

            return new List<uint>(duplicated);
        }

        // The shared tail of allocate and re-issue: take the next free id against
        // the burned set this operation will write, and refuse before writing a
        // ledger the reader would not open.
        private static bool TryIssue(
            PrefabLedgerDocument document,
            string guid,
            IReadOnlyCollection<uint> retired,
            out uint id,
            out PrefabLedgerDocument updated,
            out string error)
        {
            id = 0;
            updated = document;
            error = null;

            var basis = new PrefabLedgerDocument(Without(document.Prefabs, guid), retired);
            if (!TryNextFreeId(basis, out uint next))
            {
                error = "every allocatable prefab id is registered or retired";
                return false;
            }

            var candidate = new PrefabLedgerDocument(With(basis.Prefabs, guid, next), retired);

            // ⛔ The bound is the reader's, measured against what this allocation
            // would actually write. An independent entry count would be a second
            // limit to keep in step, and a wrong one: at forty bytes an entry it
            // could fire only on a document the size cap has already refused, so
            // the ledger it admitted is one the next Parse cannot open.
            //
            // ⚠️ Rendering the whole document per allocation made "assign every
            // unassigned prefab" quadratic in the ledger AND in the count, on the
            // editor's main thread. The upper bound below is generous and cheap;
            // `Serialize` stays the authority and is reached only where the
            // estimate says the answer might be no.
            if (LargestPossibleLength(candidate) > MaxTextLength
                && Serialize(candidate).Length > MaxTextLength)
            {
                error = "the ledger is at the size its reader admits";
                return false;
            }

            id = next;
            updated = candidate;
            return true;
        }

        // An upper bound on what Serialize would produce, never an equal.
        //
        // 🚨 The first version said forty-eight per registration while the comment
        // beside it enumerated fifty-two — a comma, a newline, four spaces, a
        // quoted thirty-two-character GUID, a colon, a space and ten digits — and
        // the code was the half that was wrong. Being four short per entry makes
        // this a LOWER bound in the worst case, and the caller's short-circuit
        // (`estimate > Max && Serialize > Max`) never consults the authority when
        // the estimate is under. Measured, not reasoned: one registration with a
        // ten-digit id serialises to 114 against an estimated 112, so a ledger
        // near the cap could be written past it and then open in neither reader.
        //
        // Fifty-two per registration, sixteen per burned id (twenty, kept loose),
        // and ninety-six for scaffolding whose measured worst case is sixty-six.
        // Held to the serialiser by a test rather than to this comment.
        internal static int LargestPossibleLength(PrefabLedgerDocument document)
            => (document.Prefabs.Count * 52) + (document.Retired.Count * 20) + 96;

        private static bool StillHeldByAnother(PrefabLedgerDocument document, string guid, uint id)
        {
            foreach (var entry in document.Prefabs)
            {
                if (entry.Value == id && !string.Equals(entry.Key, guid, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RefuseReservedId(uint id)
        {
            if (id > MaxAllocatableId)
            {
                throw new ArgumentException(
                    "id " + id.ToString(CultureInfo.InvariantCulture)
                        + " is the pool's 'not tagged' sentinel and cannot name a prefab",
                    "document");
            }
        }

        // Error text quotes the file back, and the file is not trusted.
        private static string Quote(string value)
        {
            if (value == null) return "<null>";
            return value.Length <= MaxQuotedLength
                ? value
                : value.Substring(0, MaxQuotedLength) + "…";
        }

        private static IReadOnlyDictionary<string, uint> With(
            IReadOnlyDictionary<string, uint> source, string key, uint value)
        {
            var copy = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var entry in source) copy[entry.Key] = entry.Value;
            copy[key] = value;
            return copy;
        }

        private static IReadOnlyDictionary<string, uint> Without(
            IReadOnlyDictionary<string, uint> source, string key)
        {
            var copy = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var entry in source)
            {
                if (!string.Equals(entry.Key, key, StringComparison.Ordinal)) copy[entry.Key] = entry.Value;
            }

            return copy;
        }

        private static IReadOnlyCollection<uint> Plus(IReadOnlyCollection<uint> source, uint id)
            => new SortedSet<uint>(source) { id };

        private static void AppendRegistrations(
            StringBuilder builder, IReadOnlyDictionary<string, uint> prefabs)
        {
            builder.Append("  \"prefabs\": {");

            var keys = new List<string>(prefabs.Keys);
            keys.Sort(StringComparer.Ordinal);

            for (int i = 0; i < keys.Count; i++)
            {
                builder.Append(i == 0 ? Newline : "," + Newline);
                builder.Append("    \"").Append(keys[i]).Append("\": ")
                    .Append(prefabs[keys[i]].ToString(CultureInfo.InvariantCulture));
            }

            if (keys.Count > 0) builder.Append(Newline).Append("  ");
            builder.Append("},").Append(Newline);
        }

        private static void AppendRetired(StringBuilder builder, IReadOnlyCollection<uint> retired)
        {
            builder.Append("  \"retired\": [");

            bool first = true;
            foreach (uint id in retired)
            {
                builder.Append(first ? Newline : "," + Newline);
                builder.Append("    ").Append(id.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            if (!first) builder.Append(Newline).Append("  ");
            builder.Append("]").Append(Newline);
        }

        // A reader for exactly this document and nothing wider. The ledger is
        // written by this file and read by it, so admitting JSON it will never
        // emit — nesting, escapes, numbers it cannot represent — would only widen
        // what a corrupted file can be mistaken for.
        private sealed class Reader
        {
            private readonly string _text;
            private int _index;

            public Reader(string text)
            {
                _text = text;
            }

            public PrefabLedgerParseResult ParseDocument(bool admitDuplicateIds)
            {
                Dictionary<string, uint> prefabs = null;
                SortedSet<uint> retired = null;
                uint? schemaVersion = null;

                SkipWhitespace();
                if (!Take('{')) return Fail("expected '{'");

                SkipWhitespace();
                if (!Peek('}'))
                {
                    while (true)
                    {
                        SkipWhitespace();
                        if (!TryReadString(out string key)) return Fail("expected a key");

                        SkipWhitespace();
                        if (!Take(':')) return Fail("expected ':' after '" + Quote(key) + "'");

                        SkipWhitespace();
                        switch (key)
                        {
                            case "schema_version":
                                if (schemaVersion.HasValue) return Fail("duplicate key 'schema_version'");
                                if (!TryReadUInt(out uint version)) return Fail("'schema_version' is not a number");
                                schemaVersion = version;
                                break;

                            case "prefabs":
                                if (prefabs != null) return Fail("duplicate key 'prefabs'");
                                if (!TryReadRegistrations(out prefabs, out string prefabError)) return Fail(prefabError);
                                break;

                            case "retired":
                                if (retired != null) return Fail("duplicate key 'retired'");
                                if (!TryReadRetired(out retired, out string retiredError)) return Fail(retiredError);
                                break;

                            default:
                                return Fail("unknown key '" + Quote(key) + "'");
                        }

                        SkipWhitespace();
                        if (Take(',')) continue;
                        break;
                    }
                }

                SkipWhitespace();
                if (!Take('}')) return Fail("expected '}'");

                SkipWhitespace();
                if (_index != _text.Length) return Fail("trailing content after the document");

                if (!schemaVersion.HasValue) return Fail("'schema_version' is missing");
                if (schemaVersion.Value != CurrentSchemaVersion)
                {
                    return Fail("unsupported schema_version "
                        + schemaVersion.Value.ToString(CultureInfo.InvariantCulture));
                }

                prefabs = prefabs ?? new Dictionary<string, uint>(StringComparer.Ordinal);
                retired = retired ?? new SortedSet<uint>();

                var document = new PrefabLedgerDocument(prefabs, retired);

                // ⛔ A burned id that is also live is refused in BOTH readings. The
                // repair surface exists for two branches that each allocated a
                // LIVE id; an id simultaneously burned and in use is a ledger no
                // operation here can produce, and the one a naive hand-repair
                // writes.
                foreach (uint id in prefabs.Values)
                {
                    if (retired.Contains(id))
                    {
                        return Fail("id " + id.ToString(CultureInfo.InvariantCulture)
                            + " is registered and retired at once");
                    }
                }

                if (!admitDuplicateIds && DuplicatedIds(document).Count > 0)
                {
                    return Fail("id " + DuplicatedIds(document)[0].ToString(CultureInfo.InvariantCulture)
                        + " is registered to more than one prefab");
                }

                return PrefabLedgerParseResult.Valid(document);
            }

            private bool TryReadRegistrations(out Dictionary<string, uint> map, out string error)
            {
                map = new Dictionary<string, uint>(StringComparer.Ordinal);
                error = null;

                if (!Take('{'))
                {
                    error = "expected '{' opening the registrations";
                    return false;
                }

                SkipWhitespace();
                if (Take('}')) return true;

                while (true)
                {
                    SkipWhitespace();
                    if (!TryReadString(out string guid))
                    {
                        error = "expected a GUID key";
                        return false;
                    }

                    if (!IsValidAssetGuid(guid))
                    {
                        error = "'" + Quote(guid) + "' is not an asset GUID";
                        return false;
                    }

                    if (map.ContainsKey(guid))
                    {
                        error = "duplicate GUID '" + guid + "'";
                        return false;
                    }

                    SkipWhitespace();
                    if (!Take(':'))
                    {
                        error = "expected ':' after '" + guid + "'";
                        return false;
                    }

                    SkipWhitespace();
                    if (!TryReadUInt(out uint id))
                    {
                        error = "'" + guid + "' does not carry a prefab id";
                        return false;
                    }

                    if (id > MaxAllocatableId)
                    {
                        error = "'" + guid + "' carries the pool's 'not tagged' sentinel as its id";
                        return false;
                    }

                    map[guid] = id;

                    SkipWhitespace();
                    if (Take(',')) continue;

                    if (Take('}')) return true;

                    error = "expected ',' or '}' in the registrations";
                    return false;
                }
            }

            private bool TryReadRetired(out SortedSet<uint> ids, out string error)
            {
                ids = new SortedSet<uint>();
                error = null;

                if (!Take('['))
                {
                    error = "expected '[' opening the retired ids";
                    return false;
                }

                SkipWhitespace();
                if (Take(']')) return true;

                while (true)
                {
                    SkipWhitespace();
                    if (!TryReadUInt(out uint id))
                    {
                        error = "expected a retired id";
                        return false;
                    }

                    if (id > MaxAllocatableId)
                    {
                        error = "the pool's 'not tagged' sentinel is listed as a retired id";
                        return false;
                    }

                    // A set holds no duplicates, so a repeated element is a file
                    // saying the same thing twice — which the canonical writer
                    // never emits, and which two hand edits merged produce.
                    if (!ids.Add(id))
                    {
                        error = "retired id " + id.ToString(CultureInfo.InvariantCulture) + " is listed twice";
                        return false;
                    }

                    SkipWhitespace();
                    if (Take(',')) continue;

                    if (Take(']')) return true;

                    error = "expected ',' or ']' in the retired ids";
                    return false;
                }
            }

            private bool TryReadString(out string value)
            {
                value = null;
                if (!Take('"')) return false;

                // ⛔ No escape handling, and no branch refusing one either. Every
                // key this format admits is hex or a fixed word, so a backslash
                // cannot appear in one however it is read: taken literally the key
                // matches nothing, and an escaped quote ends the string early and
                // leaves content the structural pass refuses.
                int start = _index;
                while (_index < _text.Length && _text[_index] != '"')
                {
                    _index++;
                }

                if (_index >= _text.Length) return false;

                value = _text.Substring(start, _index - start);
                _index++;
                return true;
            }

            private bool TryReadUInt(out uint value)
            {
                value = 0;
                int start = _index;
                while (_index < _text.Length && _text[_index] >= '0' && _text[_index] <= '9')
                {
                    _index++;
                }

                int digits = _index - start;
                if (digits == 0 || digits > MaxDigits)
                {
                    return false;
                }

                // 🔑 One spelling per value. The canonical writer never emits a
                // leading zero, so admitting `007` would let two texts describe
                // one document — and a diff between them would show a change
                // where none happened.
                if (digits > 1 && _text[start] == '0')
                {
                    return false;
                }

                return uint.TryParse(
                    _text.Substring(start, digits),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value);
            }

            private void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    char c = _text[_index];

                    // Exactly JSON's four. A Windows checkout can hand this a CRLF
                    // ledger, so '\r' is admitted deliberately; widening further
                    // would start reading control characters as layout.
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _index++;
                    else break;
                }
            }

            private bool Peek(char c) => _index < _text.Length && _text[_index] == c;

            private bool Take(char c)
            {
                if (!Peek(c)) return false;
                _index++;
                return true;
            }

            private static PrefabLedgerParseResult Fail(string error)
                => PrefabLedgerParseResult.Invalid(error);
        }
    }
}
