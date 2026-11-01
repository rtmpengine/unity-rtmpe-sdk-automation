using System;
using System.Collections.Generic;
using System.Globalization;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// Writes the resolved RPC method ids into a ledger's <c>rpcs</c> map as
    /// provenance. Unlike variable ids, an RPC id is a pure function of the
    /// type and method name, so recording is idempotent — issuing and
    /// re-adopting are the same operation — and the map exists to make a
    /// rename or hand-edit visible, never to number anything. A recorded entry
    /// that no longer matches the freshly derived id is refused fail-closed:
    /// silently updating it would paper over a wire break (the id peers still
    /// send is the old one). An entry whose method vanished from the type is a
    /// warning, not an error — it stays in the map as the audit record of the
    /// break, because a re-added method of the same name re-derives the same
    /// id and there is no id-space to protect by burning it.
    /// </summary>
    public static class RpcIdRecorder
    {
        /// <summary>
        /// Verifies and records <paramref name="resolvedIds"/> — an accepted
        /// <see cref="PlanGuard"/> resolution covering every RPC on the ledger's
        /// type — into <paramref name="document"/>'s <c>rpcs</c> map.
        /// </summary>
        public static RpcRecordResult Record(LedgerDocument document, IReadOnlyList<IdEntry> resolvedIds)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (resolvedIds == null) throw new ArgumentNullException(nameof(resolvedIds));

            var errors = new List<string>();
            var warnings = new List<string>();

            var rpcs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in document.Rpcs)
            {
                rpcs.Add(pair.Key, pair.Value);
            }

            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in resolvedIds)
            {
                current.Add(entry.MethodName);

                if (!rpcs.TryGetValue(entry.MethodName, out string recorded))
                {
                    rpcs.Add(entry.MethodName, entry.IdHex);
                    continue;
                }

                // Compare by VALUE, not spelling: a hand-lowercased or otherwise
                // non-canonical hex records the same wire id and is not a break —
                // it is rewritten to the canonical form so the ledger
                // re-serializes stably. Only a genuinely different (or
                // unparseable) id is the rename/edit wire break.
                if (!TryParseId(recorded, out uint recordedId) || recordedId != entry.Id)
                {
                    errors.Add(
                        "the ledger records RPC '" + entry.MethodName + "' as " + recorded
                        + " but the source now derives " + entry.IdHex
                        + " — the type was renamed or the ledger was edited; peers still dispatch"
                        + " on the recorded id, so acknowledge the wire break explicitly instead"
                        + " of letting the record drift");
                }
                else if (!string.Equals(recorded, entry.IdHex, StringComparison.Ordinal))
                {
                    rpcs[entry.MethodName] = entry.IdHex; // same id, canonicalise the spelling
                }
            }

            foreach (var pair in rpcs)
            {
                if (!current.Contains(pair.Key))
                {
                    warnings.Add(
                        "recorded RPC '" + pair.Key + "' (" + pair.Value + ") no longer matches any"
                        + " [RtmpeRpc] method on the type — a rename or delete is a wire break for"
                        + " peers still sending it; the entry is kept as the audit record (a re-added"
                        + " method of the same name re-derives the same id)");
                }
            }

            if (errors.Count > 0)
            {
                return new RpcRecordResult(null, errors, warnings);
            }

            var updated = new LedgerDocument(document.TypeName, document.Variables, rpcs);
            return new RpcRecordResult(updated, errors, warnings);
        }

        // Parses a recorded id spelling — an optional "0x"/"0X" prefix over hex
        // digits of either case — back to its numeric value, so equality is
        // judged on the wire id rather than the exact characters on disk.
        private static bool TryParseId(string recorded, out uint id)
        {
            id = 0;
            if (string.IsNullOrEmpty(recorded))
            {
                return false;
            }

            string digits = recorded.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? recorded.Substring(2)
                : recorded;

            return uint.TryParse(
                digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        }
    }
}
