using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The parsed form of a per-type <c>&lt;Type&gt;.rtmpe-ids.json</c> sidecar —
    /// the authoritative, version-controlled ledger of every wire identity ever
    /// issued for one <c>NetworkBehaviour</c> type. <c>Variables</c> records what
    /// each member's identity DERIVES to; <c>Rpcs</c> is carried for the
    /// RPC-generation phase and is not consumed here.
    ///
    /// ⛔ A record, never an authority. Nothing reads an id back out of this file
    /// to decide one: the runtime and the toolchain each compute it from the
    /// declaring type and the member's name, so the file cannot disagree with the
    /// wire — it can only be stale, which regenerating fixes. What it buys is
    /// review: a rename changes an identity, and a committed record turns that
    /// into a diff somebody sees.
    ///
    /// ⚠️ The recorded id is the one an instance of THIS type carries. A base
    /// class's members derive against whatever concrete type is instantiated, so
    /// a record written for a type that is only ever inherited from describes an
    /// object nobody spawns.
    /// </summary>
    public sealed class LedgerDocument
    {
        public LedgerDocument(
            string typeName,
            IReadOnlyDictionary<string, uint> variables,
            IReadOnlyDictionary<string, string> rpcs)
        {
            TypeName = typeName;
            Variables = variables;
            Rpcs = rpcs;
        }

        /// <summary>The fully-qualified metadata name the ledger is bound to.</summary>
        public string TypeName { get; }

        /// <summary>Derived identities: member name → variableId.</summary>
        public IReadOnlyDictionary<string, uint> Variables { get; }

        /// <summary>RPC provenance entries owned by the RPC-generation phase.</summary>
        public IReadOnlyDictionary<string, string> Rpcs { get; }
    }

    /// <summary>Outcome of parsing ledger text: a document, or one fail-closed reason.</summary>
    public sealed class LedgerParseResult
    {
        private LedgerParseResult(LedgerDocument document, string error)
        {
            Document = document;
            Error = error;
        }

        public LedgerDocument Document { get; }

        public string Error { get; }

        public bool IsValid => Document != null;

        public static LedgerParseResult Valid(LedgerDocument document)
            => new LedgerParseResult(document, null);

        public static LedgerParseResult Invalid(string error)
            => new LedgerParseResult(null, error);
    }

    /// <summary>Outcome of deriving the sidecar filename for a type.</summary>
    public sealed class LedgerFileNameResult
    {
        private LedgerFileNameResult(string fileName, string error)
        {
            FileName = fileName;
            Error = error;
        }

        public string FileName { get; }

        public string Error { get; }

        public bool IsValid => FileName != null;

        public static LedgerFileNameResult Valid(string fileName)
            => new LedgerFileNameResult(fileName, null);

        public static LedgerFileNameResult Invalid(string error)
            => new LedgerFileNameResult(null, error);
    }

    /// <summary>
    /// Reader, writer, and file-naming rules for the id ledger. The ledger is
    /// treated as hostile input: the reader accepts exactly one restricted JSON
    /// shape — the four known keys, string→integer / string→string maps, no
    /// arrays, no floats, no duplicate keys, no BOM, hard size caps — and fails
    /// closed on anything else. A refused ledger blocks allocation, which is
    /// recoverable; a silently absorbed corruption shifts wire ids, which is not.
    /// The writer emits one canonical serialization (fixed <c>\n</c>, two-space
    /// indent, ordinal-sorted keys, invariant integers) so re-runs are
    /// byte-identical and every change is a reviewable one-line diff.
    /// </summary>
    public static class VariableIdLedger
    {
        /// <summary>
        /// 2 — identities derived rather than allocated, and <c>retired</c> gone
        /// with the allocation it existed to constrain.
        /// </summary>
        /// <remarks>
        /// A version-1 sidecar is refused rather than migrated. Its numbers were
        /// issued by an allocator and correspond to nothing the wire now carries,
        /// so reading them forward would produce a record that is wrong in the one
        /// way a record must not be — quietly.
        /// </remarks>
        public const int CurrentSchemaVersion = 2;

        /// <summary>The sidecar filename suffix, appended to the type's file-name stem.</summary>
        public const string FileSuffix = ".rtmpe-ids.json";

        /// <summary>Hard input cap: a legitimate ledger is kilobytes.</summary>
        public const int MaxTextLength = 1024 * 1024;

        /// <summary>
        /// Hard per-map entry cap. A bound on how large a hostile document may
        /// be, and nothing more — it stopped describing the identity space when
        /// ids stopped being allocated out of one: a derived identity is 32 bits
        /// wide and no member count reserves any of it.
        /// </summary>
        public const int MaxEntries = 65536;

        private const string Newline = "\n";

        /// <summary>
        /// The member-name shape a ledger admits: a C# identifier. Public because
        /// the reader is not the only party that needs it — a host that mints a
        /// name the parser will later reject writes a sidecar the toolchain can
        /// never open again, so every writing surface tests candidates against
        /// this same predicate before a byte reaches disk.
        /// </summary>
        public static bool IsValidMemberName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (!char.IsLetter(name[0]) && name[0] != '_')
            {
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                {
                    return false;
                }
            }

            return true;
        }

        // Windows reserves these device names for any file whose first
        // dot-segment matches, regardless of extension.
        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>Parses ledger text; <c>null</c> text means "no ledger on disk".</summary>
        public static LedgerParseResult Parse(string text)
        {
            if (text == null)
            {
                return LedgerParseResult.Invalid("no ledger text (absent file is represented by not calling Parse)");
            }

            if (text.Length > MaxTextLength)
            {
                return LedgerParseResult.Invalid("ledger exceeds the size cap");
            }

            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                return LedgerParseResult.Invalid("ledger begins with a byte-order mark");
            }

            var reader = new Reader(text);
            return reader.ParseDocument();
        }

        /// <summary>Serializes a document to its single canonical form.</summary>
        public static string Serialize(LedgerDocument document)
        {
            var builder = new StringBuilder();
            builder.Append("{").Append(Newline);
            builder.Append("  \"schema_version\": ")
                .Append(CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture))
                .Append(",").Append(Newline);
            builder.Append("  \"type\": \"").Append(Escape(document.TypeName)).Append("\",").Append(Newline);

            AppendIdMap(builder, "variables", document.Variables, trailingComma: true);
            AppendStringMap(builder, "rpcs", document.Rpcs, trailingComma: false);

            builder.Append("}").Append(Newline);
            return builder.ToString();
        }

        /// <summary>
        /// Derives the sidecar filename from a fully-qualified metadata name.
        /// The name is kept verbatim ('.' and nested-type '+' are legal filename
        /// characters everywhere this runs) except the generic-arity backtick,
        /// which maps to '-' — an injective substitution because identifiers
        /// cannot contain '-'.
        /// </summary>
        public static LedgerFileNameResult FileNameFor(string fullyQualifiedMetadataName)
        {
            if (string.IsNullOrEmpty(fullyQualifiedMetadataName))
            {
                return LedgerFileNameResult.Invalid("empty type name");
            }

            var stem = fullyQualifiedMetadataName.Replace('`', '-');

            foreach (char c in stem)
            {
                if (c < 0x20 || c == '<' || c == '>' || c == ':' || c == '"'
                    || c == '/' || c == '\\' || c == '|' || c == '?' || c == '*')
                {
                    return LedgerFileNameResult.Invalid(
                        "type name contains a character that is not portable in a filename: '" + c + "'");
                }
            }

            string fileName = stem + FileSuffix;
            if (fileName.Length > 240)
            {
                return LedgerFileNameResult.Invalid("sidecar filename exceeds 240 characters");
            }

            string firstSegment = stem;
            int firstDot = stem.IndexOf('.');
            if (firstDot >= 0)
            {
                firstSegment = stem.Substring(0, firstDot);
            }

            foreach (string reserved in ReservedDeviceNames)
            {
                if (string.Equals(firstSegment, reserved, System.StringComparison.OrdinalIgnoreCase))
                {
                    return LedgerFileNameResult.Invalid(
                        "'" + firstSegment + "' is a reserved device name on Windows");
                }
            }

            return LedgerFileNameResult.Valid(fileName);
        }

        private static void AppendIdMap(
            StringBuilder builder, string key, IReadOnlyDictionary<string, uint> map, bool trailingComma)
        {
            builder.Append("  \"").Append(key).Append("\": ");
            if (map.Count == 0)
            {
                builder.Append("{}");
            }
            else
            {
                var names = SortedKeys(map.Keys);
                builder.Append("{").Append(Newline);
                for (int i = 0; i < names.Count; i++)
                {
                    builder.Append("    \"").Append(Escape(names[i])).Append("\": ")
                        .Append(map[names[i]].ToString(CultureInfo.InvariantCulture));
                    builder.Append(i < names.Count - 1 ? "," : string.Empty).Append(Newline);
                }

                builder.Append("  }");
            }

            builder.Append(trailingComma ? "," : string.Empty).Append(Newline);
        }

        private static void AppendStringMap(
            StringBuilder builder, string key, IReadOnlyDictionary<string, string> map, bool trailingComma)
        {
            builder.Append("  \"").Append(key).Append("\": ");
            if (map.Count == 0)
            {
                builder.Append("{}");
            }
            else
            {
                var names = SortedKeys(map.Keys);
                builder.Append("{").Append(Newline);
                for (int i = 0; i < names.Count; i++)
                {
                    builder.Append("    \"").Append(Escape(names[i])).Append("\": \"")
                        .Append(Escape(map[names[i]])).Append("\"");
                    builder.Append(i < names.Count - 1 ? "," : string.Empty).Append(Newline);
                }

                builder.Append("  }");
            }

            builder.Append(trailingComma ? "," : string.Empty).Append(Newline);
        }

        private static List<string> SortedKeys(IEnumerable<string> keys)
        {
            var names = new List<string>(keys);
            names.Sort(System.StringComparer.Ordinal);
            return names;
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
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

            return builder.ToString();
        }

        // A recursive-descent reader over exactly the ledger grammar. Every
        // deviation — unknown key, duplicate key, array, float, boolean, out-of-
        // range id, trailing content — is a single fail-closed error; there is
        // deliberately no tolerance for "almost right" input.
        private struct Reader
        {
            private readonly string _text;
            private int _pos;
            private string _error;

            public Reader(string text)
            {
                _text = text;
                _pos = 0;
                _error = null;
            }

            public LedgerParseResult ParseDocument()
            {
                int schemaVersion = -1;
                string typeName = null;
                SortedDictionary<string, uint> variables = null;
                SortedDictionary<string, string> rpcs = null;

                SkipWhitespace();
                if (!Expect('{'))
                {
                    return Fail();
                }

                bool first = true;
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() == '}')
                    {
                        _pos++;
                        break;
                    }

                    if (!first && !Expect(','))
                    {
                        return Fail();
                    }

                    first = false;
                    SkipWhitespace();

                    if (!ReadString(out string key) || !SkipTo(':'))
                    {
                        return Fail();
                    }

                    SkipWhitespace();
                    switch (key)
                    {
                        case "schema_version":
                            if (schemaVersion >= 0)
                            {
                                return Fail("duplicate key 'schema_version'");
                            }

                            if (!ReadInteger(out schemaVersion))
                            {
                                return Fail();
                            }

                            break;
                        case "type":
                            if (typeName != null)
                            {
                                return Fail("duplicate key 'type'");
                            }

                            if (!ReadString(out typeName))
                            {
                                return Fail();
                            }

                            break;
                        case "variables":
                            if (variables != null)
                            {
                                return Fail("duplicate key 'variables'");
                            }

                            if (!ReadIdMap(out variables))
                            {
                                return Fail();
                            }

                            break;
                        case "rpcs":
                            if (rpcs != null)
                            {
                                return Fail("duplicate key 'rpcs'");
                            }

                            if (!ReadStringMap(out rpcs))
                            {
                                return Fail();
                            }

                            break;
                        default:
                            // ⛔ The version refusal must be reachable for a document
                            // that actually IS the previous version, and a real v1
                            // record carries `retired` — a key this parser no longer
                            // knows. Reported as unknown, the upgrade path landed on
                            // "unknown key 'retired'" and the message written for it
                            // was reachable only from a v1 document that v1 itself
                            // would have refused, which is what its test fed.
                            //
                            // The canonical writer emits `schema_version` first, so
                            // it is in hand by the time any other key is read; a
                            // hand-ordered document that puts it later still gets the
                            // unknown-key answer, which is true of it.
                            if (schemaVersion >= 0 && schemaVersion != CurrentSchemaVersion)
                            {
                                return Fail(UnsupportedSchemaVersion(schemaVersion));
                            }

                            return Fail("unknown key '" + key + "'");
                    }
                }

                SkipWhitespace();
                if (_pos != _text.Length)
                {
                    return Fail("content after the closing brace");
                }

                if (schemaVersion < 0 || typeName == null || variables == null || rpcs == null)
                {
                    return Fail("a required key is missing");
                }

                if (schemaVersion != CurrentSchemaVersion)
                {
                    return Fail(UnsupportedSchemaVersion(schemaVersion));
                }

                // An unpaired surrogate survives parsing but the UTF-8 writer
                // would mangle it, breaking byte-determinism downstream.
                if (!IsWellFormedUtf16(typeName))
                {
                    return Fail("the type name contains an unpaired surrogate");
                }

                string crossError = ValidateIds(variables);
                if (crossError != null)
                {
                    return LedgerParseResult.Invalid(crossError);
                }

                return LedgerParseResult.Valid(new LedgerDocument(typeName, variables, rpcs));
            }

            // Two members recorded on one identity is a corrupted or
            // hand-edited file: the record is generated from a derivation that
            // cannot produce one, so reading it forward would carry a claim that
            // one of those members never replicates.
            //
            // ⛔ The `enforceUniqueIds` parameter is gone with the verb that
            // passed it false. It existed for the merge-repair host, which read a
            // duplicated document in order to renumber around it — a question
            // only an allocator has. Nothing relaxes this now, and a parameter
            // whose only non-default caller has been deleted is a switch nobody
            // can reach and everybody must still reason about.
            private static string ValidateIds(
                SortedDictionary<string, uint> variables)
            {
                var seen = new Dictionary<uint, string>();
                {
                    foreach (var pair in variables)
                    {
                        if (!IsValidMemberName(pair.Key))
                        {
                            return "'" + pair.Key + "' is not a valid member name";
                        }

                        if (seen.TryGetValue(pair.Value, out string holder))
                        {
                            // Two members recorded on one identity. The record is
                            // generated, so this is a corrupted or hand-edited
                            // file rather than a decision anybody took — but it
                            // still describes a member that cannot replicate, and
                            // reading it forward would carry that claim on.
                            return "identity 0x" + pair.Value.ToString("X8", CultureInfo.InvariantCulture)
                                + " is recorded against both '" + holder + "' and '" + pair.Key + "'";
                        }

                        if (!seen.ContainsKey(pair.Value))
                        {
                            seen.Add(pair.Value, pair.Key);
                        }
                    }
                }

                return null;
            }

            private static bool IsWellFormedUtf16(string text)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    if (char.IsHighSurrogate(text[i]))
                    {
                        if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                        {
                            return false;
                        }

                        i++;
                    }
                    else if (char.IsLowSurrogate(text[i]))
                    {
                        return false;
                    }
                }

                return true;
            }


            private bool ReadIdMap(out SortedDictionary<string, uint> map)
            {
                map = new SortedDictionary<string, uint>(System.StringComparer.Ordinal);
                if (!Expect('{'))
                {
                    return false;
                }

                bool first = true;
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() == '}')
                    {
                        _pos++;
                        return true;
                    }

                    if (!first && !Expect(','))
                    {
                        return false;
                    }

                    first = false;
                    SkipWhitespace();
                    if (!ReadString(out string key) || !SkipTo(':'))
                    {
                        return false;
                    }

                    if (map.ContainsKey(key))
                    {
                        _error = "duplicate member '" + key + "'";
                        return false;
                    }

                    SkipWhitespace();
                    if (!ReadDigits(out long value))
                    {
                        return false;
                    }

                    if (value > uint.MaxValue)
                    {
                        _error = "id " + value.ToString(CultureInfo.InvariantCulture)
                            + " is outside the range an identity can occupy";
                        return false;
                    }

                    if (map.Count >= MaxEntries)
                    {
                        _error = "map exceeds the entry cap";
                        return false;
                    }

                    map.Add(key, (uint)value);
                }
            }

            private bool ReadStringMap(out SortedDictionary<string, string> map)
            {
                map = new SortedDictionary<string, string>(System.StringComparer.Ordinal);
                if (!Expect('{'))
                {
                    return false;
                }

                bool first = true;
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() == '}')
                    {
                        _pos++;
                        return true;
                    }

                    if (!first && !Expect(','))
                    {
                        return false;
                    }

                    first = false;
                    SkipWhitespace();
                    if (!ReadString(out string key) || !SkipTo(':'))
                    {
                        return false;
                    }

                    if (map.ContainsKey(key))
                    {
                        _error = "duplicate entry '" + key + "'";
                        return false;
                    }

                    SkipWhitespace();
                    if (!ReadString(out string value))
                    {
                        return false;
                    }

                    // Hold the string map to the same well-formedness bar as the
                    // type name: an unpaired surrogate survives parsing but the
                    // UTF-8 writer would replace it, so a round-trip would no longer
                    // be byte-identical on disk.
                    if (!IsWellFormedUtf16(key) || !IsWellFormedUtf16(value))
                    {
                        _error = "a string-map entry contains an unpaired surrogate";
                        return false;
                    }

                    if (map.Count >= MaxEntries)
                    {
                        _error = "map exceeds the entry cap";
                        return false;
                    }

                    map.Add(key, value);
                }
            }

            private bool ReadInteger(out int value)
            {
                value = 0;
                if (!ReadDigits(out long parsed))
                {
                    return false;
                }

                if (parsed > int.MaxValue)
                {
                    _error = "integer out of range";
                    return false;
                }

                value = (int)parsed;
                return true;
            }

            /// <summary>
            /// The shared digit scanner: canonical spelling only — no sign, no
            /// leading zero, bounded length. Both range checks are stated against
            /// this one reading rather than against a scanner each.
            /// </summary>
            private bool ReadDigits(out long value)
            {
                value = 0;
                int start = _pos;
                while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                {
                    _pos++;
                }

                if (_pos == start || _pos - start > 10)
                {
                    _error = "expected an unsigned integer";
                    return false;
                }

                // The canonical writer never emits a leading zero; accepting one
                // would let two spellings of one document coexist.
                if (_text[start] == '0' && _pos - start > 1)
                {
                    _error = "integer has a leading zero";
                    return false;
                }

                value = long.Parse(_text.Substring(start, _pos - start), CultureInfo.InvariantCulture);
                return true;
            }

            private bool ReadString(out string value)
            {
                value = null;
                if (!Expect('"'))
                {
                    return false;
                }

                var builder = new StringBuilder();
                while (_pos < _text.Length)
                {
                    char c = _text[_pos++];
                    if (c == '"')
                    {
                        value = builder.ToString();
                        return true;
                    }

                    if (c == '\\')
                    {
                        if (_pos >= _text.Length)
                        {
                            break;
                        }

                        char escape = _text[_pos++];
                        switch (escape)
                        {
                            case '"': builder.Append('"'); break;
                            case '\\': builder.Append('\\'); break;
                            case '/': builder.Append('/'); break;
                            case 'b': builder.Append('\b'); break;
                            case 'f': builder.Append('\f'); break;
                            case 'n': builder.Append('\n'); break;
                            case 'r': builder.Append('\r'); break;
                            case 't': builder.Append('\t'); break;
                            case 'u':
                                // JSON spells this escape as exactly four hex digits.
                                // NumberStyles.HexNumber additionally accepts surrounding
                                // whitespace, which would let "\u 05f" and "\u5f  " decode
                                // to the same character as "_" — several spellings of
                                // one member name, in a file that binds names to wire ids,
                                // and none of them readable by a conforming JSON parser.
                                // The digits are checked before they are converted.
                                if (_pos + 4 > _text.Length
                                    || !IsHexDigit(_text[_pos]) || !IsHexDigit(_text[_pos + 1])
                                    || !IsHexDigit(_text[_pos + 2]) || !IsHexDigit(_text[_pos + 3])
                                    || !int.TryParse(
                                        _text.Substring(_pos, 4), NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out int code))
                                {
                                    _error = "malformed \\u escape";
                                    return false;
                                }

                                builder.Append((char)code);
                                _pos += 4;
                                break;
                            default:
                                _error = "unsupported escape '\\" + escape + "'";
                                return false;
                        }

                        continue;
                    }

                    if (c < 0x20)
                    {
                        _error = "unescaped control character in string";
                        return false;
                    }

                    builder.Append(c);
                }

                _error = "unterminated string";
                return false;
            }

            private bool SkipTo(char expected)
            {
                SkipWhitespace();
                return Expect(expected);
            }

            private bool Expect(char expected)
            {
                if (_pos < _text.Length && _text[_pos] == expected)
                {
                    _pos++;
                    return true;
                }

                _error = "expected '" + expected + "' at position " + _pos.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

            private void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                    {
                        return;
                    }

                    _pos++;
                }
            }

            // Actionable, because this is the one refusal an upgrading project
            // actually meets. Under allocation the sidecar WAS the decision, so
            // losing it moved every id; an identity is derived from the declaring
            // type and the member name now, so the file records what the source
            // already settles and rewriting it from scratch reproduces it exactly.
            //
            // Stated once and reached from both arms that refuse a version — the
            // end-of-document check, and the unknown-key arm a v1 `retired` lands
            // on first.
            private static string UnsupportedSchemaVersion(int schemaVersion)
                => "unsupported schema_version "
                   + schemaVersion.ToString(CultureInfo.InvariantCulture)
                   + " (this toolchain writes "
                   + CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)
                   + ") — ids are derived from the declaring type and the member name now, so"
                   + " this file records what the source already decides: delete it and re-run"
                   + " the conversion to write a current one. No id moves.";

            private LedgerParseResult Fail(string error = null)
                => LedgerParseResult.Invalid(error ?? _error ?? "malformed ledger");
        }

        // A JSON hex digit: 0-9, a-f, A-F and nothing else — not the wider set
        // int.TryParse would tolerate around the value.
        private static bool IsHexDigit(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    }
}
