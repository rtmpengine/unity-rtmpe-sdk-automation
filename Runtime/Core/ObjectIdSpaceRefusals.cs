// RTMPE SDK — Runtime/Core/ObjectIdSpaceRefusals.cs
//
// The two inbound id-space refusals, taken and counted in one call.
//
// A refusal on this path is silent by construction.  The frame is dropped, no
// peer is told, and what the application sees is an object that never arrived
// or one that never left.  The only report is a console line behind a
// per-second gate, and a gate answers a sustained refusal with a single entry
// — so a rule firing once and a rule firing four hundred times a second read
// identically, and a rule firing WRONGLY reads exactly like a healthy client.
//
// The gateway takes the same decision on the other side of the wire and counts
// it (`rtmpe_gateway_spawn_foreign_id_space_total`), after the same finding: an
// arm that refuses without counting leaves a refused squat and a quiet room
// indistinguishable from one another.
//
// 🔑 The verdict and the tally are ONE call, deliberately.  Asking
// ObjectLifecycleAuthority at the receive path and incrementing beside it joins
// the two by nothing but discipline, and a missing increment is precisely what
// nobody notices: every suite stays green while the instrument reads zero.
//
// ⛔ The counters are lifetime totals and are never reset.  A refusal storm
// followed by a reconnect is the case the reading exists for; zeroing on
// teardown would erase the evidence at the moment it becomes interesting.

using System.Threading;

namespace RTMPE.Core
{
    /// <summary>
    /// The client's inbound id-space refusals, and the tally of what each of
    /// them refused.
    /// </summary>
    /// <remarks>
    /// One counter per refusal rather than one for both. The two answer
    /// different questions — a peer reserving ids ahead of this client, and the
    /// room spending the records those reservations created — and a client can
    /// be subject to the first without ever reaching the second, so a single
    /// total would report the pair as one indistinguishable quantity.
    /// </remarks>
    internal sealed class ObjectIdSpaceRefusals
    {
        private long _refusedForeignClaims;
        private long _refusedForeignDespawns;

        /// <summary>
        /// Number of inbound spawns refused because the id they carried lies in
        /// this client's own id space under another player's name.
        /// </summary>
        /// <remarks>
        /// Every id this SDK mints folds its own session digest into the id's
        /// high half, so an honest room never moves this counter at all. A
        /// non-zero reading names one of three things: a peer composing ids by
        /// some other route, a session that ended and whose digest was reissued,
        /// or — the reason the reading is worth having — this rule refusing
        /// legitimate traffic, which is otherwise indistinguishable from a room
        /// in which nothing happened.
        /// </remarks>
        public long RefusedForeignClaimCount => Interlocked.Read(ref _refusedForeignClaims);

        /// <summary>
        /// Number of inbound despawns refused because they named an object this
        /// client both owns and minted.
        /// </summary>
        /// <remarks>
        /// It usually climbs behind the other, because the record a despawn
        /// spends is typically the one a refused claim created — but it does not
        /// depend on it, and reading it as a second stage of the same attempt
        /// would be wrong. Two things move it alone: the stale-transfer residual
        /// <see cref="ObjectLifecycleAuthority.RefusesDespawnOfAnObjectThisClientOwns"/>
        /// states — an object transferred away by a notice this client never
        /// received, which then stands locally and nowhere else — and a gateway
        /// that no longer holds an ownership record for the object, whose
        /// authority check admits a despawn it cannot attribute.
        /// </remarks>
        public long RefusedForeignDespawnCount => Interlocked.Read(ref _refusedForeignDespawns);

        /// <summary>
        /// <see cref="ObjectLifecycleAuthority.RefusesForeignClaimOnOwnIdSpace"/>,
        /// counting every refusal it answers.
        /// </summary>
        public bool RefusesForeignClaimOnOwnIdSpace(
            ulong objectId, string ownerPlayerId, ulong localSessionId, string localPlayerId)
        {
            if (!ObjectLifecycleAuthority.RefusesForeignClaimOnOwnIdSpace(
                    objectId, ownerPlayerId, localSessionId, localPlayerId))
            {
                return false;
            }

            Interlocked.Increment(ref _refusedForeignClaims);
            return true;
        }

        /// <summary>
        /// <see cref="ObjectLifecycleAuthority.RefusesDespawnOfAnObjectThisClientOwns"/>,
        /// counting every refusal it answers.
        /// </summary>
        public bool RefusesDespawnOfAnObjectThisClientOwns(
            ulong objectId, string registeredOwnerPlayerId, ulong localSessionId, string localPlayerId)
        {
            if (!ObjectLifecycleAuthority.RefusesDespawnOfAnObjectThisClientOwns(
                    objectId, registeredOwnerPlayerId, localSessionId, localPlayerId))
            {
                return false;
            }

            Interlocked.Increment(ref _refusedForeignDespawns);
            return true;
        }
    }
}
