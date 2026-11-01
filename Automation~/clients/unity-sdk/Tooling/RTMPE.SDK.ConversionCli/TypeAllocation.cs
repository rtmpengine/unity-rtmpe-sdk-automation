using System.Collections.Generic;
using RTMPE.SDK.Transforms;

namespace RTMPE.SDK.ConversionCli
{
    /// <summary>
    /// Everything one type's allocation decided, and nothing it did: the sidecar
    /// it resolved to, the bytes that sidecar held when the decision was made,
    /// the text it should hold afterwards, and the conversions with their issued
    /// ids.
    /// </summary>
    /// <remarks>
    /// 🔑 The snapshot is carried, not re-read. Both hosts re-confirm at apply
    /// time that the ledger still holds exactly the bytes the plan was made
    /// against, and a second read could not promise the comparison is about the
    /// same moment. In a batch this matters more, not less: several types'
    /// decisions are made in sequence and committed together, so each carries
    /// the moment its own was made.
    /// </remarks>
    internal sealed class TypeAllocation
    {
        internal TypeAllocation(
            string canonicalType, string ledgerFileName, string ledgerPath,
            byte[] ledgerSnapshot, string oldLedgerText, string newLedgerText,
            IReadOnlyList<PlannedConversion> conversions)
        {
            CanonicalType = canonicalType;
            LedgerFileName = ledgerFileName;
            LedgerPath = ledgerPath;
            LedgerSnapshot = ledgerSnapshot;
            OldLedgerText = oldLedgerText;
            NewLedgerText = newLedgerText;
            Conversions = conversions;
        }

        internal string CanonicalType { get; }

        internal string LedgerFileName { get; }

        internal string LedgerPath { get; }

        /// <summary>The sidecar's bytes at plan time; <c>null</c> when it did not exist.</summary>
        internal byte[] LedgerSnapshot { get; }

        /// <summary>Empty string when the sidecar did not exist — never null.</summary>
        internal string OldLedgerText { get; }

        internal string NewLedgerText { get; }

        internal IReadOnlyList<PlannedConversion> Conversions { get; }
    }
}
