using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The fixed set of method ids the SDK runtime reserves for built-in
    /// messages — Ping, TransferOwnership, RequestDamage, ApplyDamage,
    /// GameStateChange, SyncGameState. These are raw small constants, not
    /// hashes; a generated method whose FNV-1a id lands on one of them cannot be
    /// dispatched and is refused fail-closed.
    /// </summary>
    public static class ReservedRpcIds
    {
        private static readonly HashSet<uint> Ids = new HashSet<uint>
        {
            100u, // Ping
            200u, // TransferOwnership
            300u, // RequestDamage
            301u, // ApplyDamage
            400u, // GameStateChange
            401u, // SyncGameState
        };

        // Handing out the backing set typed as read-only would leave every
        // refusal one cast away from being switched off for the process: the
        // collision guard, the allocator's reserved check, and the advisor's
        // guard tool all consult this one instance. The wrapper is built once
        // because the set is immutable in fact as well as in contract.
        private static readonly ReadOnlyCollection<uint> View =
            new ReadOnlyCollection<uint>(Ids.OrderBy(id => id).ToList());

        /// <summary>The reserved ids as a read-only view, in ascending order.</summary>
        public static IReadOnlyCollection<uint> Values => View;

        /// <summary>True when <paramref name="id"/> is one of the reserved ids.</summary>
        public static bool IsReserved(uint id) => Ids.Contains(id);
    }
}
