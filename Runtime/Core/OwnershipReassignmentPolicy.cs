// OwnershipReassignmentPolicy — pure decision logic for NEW-OWNERSHIP-1.
//
// When the owner of a NetworkObject leaves the room, objects flagged
// DestroyWithOwner=false used to freeze: the SDK left them owned by the
// departed player awaiting a "server ownership grant" that no server ever
// emitted.  The fix reassigns those surviving objects to the current room host
// (MasterClient) deterministically on every client — the canonical
// host-migration behaviour for this class of SDK.
//
// The *when-to-reassign* decision is isolated here, free of UnityEngine, so it
// is unit-testable under the dotnet/xunit harness.  The MonoBehaviour-coupled
// OwnershipManager/SpawnManager that perform the actual reassignment cannot be
// compiled outside Unity, so keeping this guard pure is what makes the
// load-bearing correctness rule (notably: do NOT steal objects on a voluntary
// in-room master transfer) testable without the Unity runtime.

namespace RTMPE.Core
{
    /// <summary>
    /// Decides whether the surviving objects of a player who left, or of the
    /// previous host, pass to a new owner (the room's host).
    /// </summary>
    /// <remarks>
    /// The SDK applies this rule when a player leaves and when the host changes;
    /// game code does not need to call it.
    /// </remarks>
    public static class OwnershipReassignmentPolicy
    {
        /// <summary>
        /// Whether the objects <paramref name="formerOwnerId"/> owns that survive
        /// their owner (<see cref="NetworkBehaviour.DestroyWithOwner"/> is
        /// <see langword="false"/>) pass to <paramref name="newOwnerId"/>.
        /// </summary>
        /// <remarks>
        /// Returns <see langword="false"/> when either id is null or empty, when the
        /// two ids are equal, or when <paramref name="formerStillInRoom"/> is
        /// <see langword="true"/>: a host that hands the host role to another player
        /// and stays in the room keeps its objects.
        /// </remarks>
        /// <param name="formerOwnerId">The player whose objects are considered.</param>
        /// <param name="newOwnerId">The player who would receive them, normally the room's host.</param>
        /// <param name="formerStillInRoom">
        /// Whether the former owner is still in the room: <see langword="false"/> for
        /// a player who left; for a host change, whether the previous host is still
        /// in the room.
        /// </param>
        /// <returns><see langword="true"/> when the objects should be reassigned.</returns>
        public static bool ShouldReassign(string formerOwnerId, string newOwnerId, bool formerStillInRoom)
        {
            if (string.IsNullOrEmpty(formerOwnerId)) return false;
            if (string.IsNullOrEmpty(newOwnerId)) return false;
            if (formerOwnerId == newOwnerId) return false;
            if (formerStillInRoom) return false;
            return true;
        }
    }
}
