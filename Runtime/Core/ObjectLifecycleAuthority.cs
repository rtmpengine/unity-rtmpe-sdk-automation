// RTMPE SDK — Runtime/Core/ObjectLifecycleAuthority.cs
//
// The client's half of the gateway's per-object authority rule.
//
// EVERY per-object mutation the gateway relays is authorised against that
// object's recorded owner, through one predicate (`egress::object_admits`):
// a StateSync transform, a Despawn, a VariableUpdate, an owner-only RPC, an
// ownership transfer, and — as the id-collision rule — a Spawn.  A Spawn's
// owner CLAIM is checked separately and just as hard
// (`classify_spawn_authority`).  A refusal is answered with nothing at all —
// the forwarder returns before it publishes, and the originator is never told
// — so an operation this side commits locally and the room refuses leaves
// exactly one client holding a different world from every other.
//
// This seam is the client's half of that rule for the two object-lifecycle
// operations.  ⛔ It is NOT the whole of the client's half: a StateSync and a
// VariableUpdate are authorised on the same terms and are gated elsewhere, or
// not at all — see the SDK audit register.
//
// OwnershipManager.RequestOwnershipTransfer has always asked this question
// before sending.  This is the same question, stated once, for the other two.
//
// The test is deliberately ONE-SIDED: it refuses only on positive knowledge of
// a mismatch — both identities present and different.
//
// ⛔ The reason is NOT that the gateway forwards the other cases.  It refuses
// them too: classify_spawn_authority answers NoClaim for a session carrying no
// server-derived identity and forward_spawn refuses that arm outright, and
// forward_despawn bails on "session not in a room" before it reaches its owner
// check at all.  The reason is narrower, and it is that a refusal has to be
// worth what it costs:
//
//   • Despawn's refusal exists to prevent DIVERGENCE — the object gone on this
//     client and standing on every other.  A client with no seat has had no
//     spawn of its own relayed, so no peer holds the object: the local teardown
//     is the whole of the operation, and refusing it would deny a caller the
//     destruction of a genuinely local object against a divergence that cannot
//     occur.  An object whose recorded owner is empty is one this client made
//     before it had a seat, and the same argument covers it.
//   • Spawn with no seat can form no claim the gateway would accept, so the
//     object is local-only whatever this side decides.  Refusing turns a usable
//     local object into null for a caller whose actual mistake — spawning
//     before OnRoomJoined — is a documented precondition, not this check's
//     subject.
//
// ⛔ So what this does NOT close is the pre-room spawn's own silence.

using System;

namespace RTMPE.Core
{
    internal static class ObjectLifecycleAuthority
    {
        /// <summary>
        /// True when <paramref name="ownerPlayerId"/> is known to name a player
        /// other than <paramref name="localPlayerId"/> — the one case in which
        /// the gateway refuses to relay a lifecycle operation this client
        /// issues against the object.  Either identity being absent is an
        /// unanswered question, not a refusal.
        /// </summary>
        internal static bool RefusesForeignOwner(string ownerPlayerId, string localPlayerId)
            => RefusesForeignAuthority(ownerPlayerId, localPlayerId);

        /// <summary>
        /// True when an inbound spawn claims an object id minted inside this
        /// client's own session while naming somebody else as its owner.
        /// </summary>
        /// <param name="objectId">The id the spawn carries.</param>
        /// <param name="ownerPlayerId">The owner the spawn declares.</param>
        /// <param name="localSessionId">This client's gateway session id.</param>
        /// <param name="localPlayerId">This client's seat in the room.</param>
        /// <remarks>
        /// Every id this SDK allocates folds its own session's digest into the
        /// high half, so the only party that can legitimately mint one in this
        /// space is this client — and this client's own spawns come back naming
        /// it as owner. A remote owner on such an id is a peer reserving ids
        /// this client has not reached yet.
        /// <para>
        /// One-sided on the same terms as <see cref="RefusesForeignAuthority"/>,
        /// and for the same reason: every identity here can be legitimately
        /// unanswered. A session id of zero is a client with no session; an
        /// empty owner claim is a spawn that names nobody; and an empty local
        /// seat is a client the room has not yet told who it is — the local
        /// player id is an optional field on the room reply, so a whole room
        /// stay can pass without one. Refusing on any of those absences would
        /// discard legitimate objects for the length of the window, which is the
        /// `G1-04` hazard this file is named for: depth must not refuse what the
        /// authority would admit.
        /// </para>
        /// <para>
        /// A free function rather than a condition at the receive path, because
        /// that file is compiled by no shard: a condition written there can only
        /// be asserted over its own source, and a source rule cannot tell a
        /// condition that is right from one that has been inverted.
        /// </para>
        /// </remarks>
        internal static bool RefusesForeignClaimOnOwnIdSpace(
            ulong objectId, string ownerPlayerId, ulong localSessionId, string localPlayerId)
        {
            if (localSessionId == 0) return false;
            if (string.IsNullOrEmpty(ownerPlayerId)) return false;
            if (string.IsNullOrEmpty(localPlayerId)) return false;
            if (string.Equals(ownerPlayerId, localPlayerId, StringComparison.Ordinal)) return false;

            return ObjectIdMath.BelongsToSession(objectId, localSessionId);
        }

        /// <summary>
        /// True when an inbound despawn names an object this client both owns
        /// and minted.
        /// </summary>
        /// <param name="objectId">The id the despawn names.</param>
        /// <param name="registeredOwnerPlayerId">
        /// The owner recorded on the locally registered object, or null when
        /// this client holds no such object.
        /// </param>
        /// <param name="localSessionId">This client's gateway session id.</param>
        /// <param name="localPlayerId">This client's seat in the room.</param>
        /// <remarks>
        /// The room relays a despawn from the object's recorded owner and from
        /// nobody else, and it excludes the originator from its own fan-out — so
        /// a despawn naming an object this client owns is one the room could not
        /// have produced from a legitimate sender. What produces it is a peer
        /// that reserved the id first and holds the record: this client refuses
        /// its spawn, but the record stands, and the despawn that follows is
        /// honoured against this client's own object.
        /// <para>
        /// ⚠️ There is a second producer, and it is not an attack. The gateway's
        /// authority check is **fail-open on an unknown object** — `None => true`
        /// in `object_authority_ok`, stated in `forward_despawn`'s own comment —
        /// so a room that has forgotten a record relays whoever asks. That case
        /// wants the same answer for a different reason: the room no longer
        /// knows who owns the object, and this client does.
        /// </para>
        /// <para>
        /// Both conditions are required, and the id-space half is what keeps the
        /// rule from touching anything else: an object this client owns but did
        /// not mint was handed to it, and the party that handed it over may
        /// legitimately be the one taking it back.
        /// </para>
        /// <para>
        /// ⛔ The residual is stated rather than hidden. This client's record of
        /// who owns an object is its own, and a transfer notice lost in flight
        /// leaves it stale — so an object this client minted, transferred away,
        /// and did not learn it had lost would have the new owner's despawn
        /// refused, and would survive locally as an object nobody else holds.
        /// That is a bounded local artefact; honouring the despawn instead is
        /// the destruction of an object on somebody else's word.
        /// </para>
        /// </remarks>
        internal static bool RefusesDespawnOfAnObjectThisClientOwns(
            ulong objectId, string registeredOwnerPlayerId, ulong localSessionId, string localPlayerId)
        {
            if (localSessionId == 0) return false;
            if (string.IsNullOrEmpty(localPlayerId)) return false;
            if (string.IsNullOrEmpty(registeredOwnerPlayerId)) return false;
            if (!string.Equals(registeredOwnerPlayerId, localPlayerId, StringComparison.Ordinal))
            {
                return false;
            }

            return ObjectIdMath.BelongsToSession(objectId, localSessionId);
        }

        /// <summary>
        /// The same one-sided test, for a write whose authority is held by a
        /// named player who is not the object's owner: the room's master client
        /// for a room-property write, the seat's occupant for a player-property
        /// write.
        /// </summary>
        /// <remarks>
        /// One body, two names, because the rule is one rule and the domains are
        /// two: <see cref="RefusesForeignOwner"/> reads correctly at an object's
        /// lifecycle and would read as a lie beside a room's property map.
        ///
        /// <para>🔑 The one-sidedness matters MORE here than it does for an
        /// object, not less. A room's master id is derived from the roster, and
        /// the roster arrives asynchronously — a client that has not yet been
        /// told who the host is knows strictly less than the server does, and a
        /// two-sided test would refuse every property write in that window.
        /// That is the `G1-04` hazard by name: depth must not refuse what the
        /// authority would admit.</para>
        /// </remarks>
        internal static bool RefusesForeignAuthority(string authorityPlayerId, string localPlayerId)
        {
            if (string.IsNullOrEmpty(authorityPlayerId)) return false;
            if (string.IsNullOrEmpty(localPlayerId)) return false;
            return authorityPlayerId != localPlayerId;
        }

        /// <summary>
        /// True when a <c>Spawn</c> issued now would be created locally and
        /// relayed to nobody, because this client holds no room seat for its
        /// owner claim to name.
        /// </summary>
        /// <remarks>
        /// The gateway's <c>classify_spawn_authority</c> answers <c>NoClaim</c>
        /// for a session with no server-derived identity and
        /// <c>forward_spawn</c> refuses that arm without publishing; nothing
        /// re-announces the object when a room is later joined.
        ///
        /// <para>🔑 It lives here rather than inline in the caller because
        /// <c>SpawnManager.cs</c> is compiled by no shard: a condition written
        /// at the call site can only be asserted over its own source, and a
        /// source rule cannot tell a condition that is right from one that has
        /// been inverted or had a dead clause prepended. Here it is executed.
        /// </para>
        ///
        /// <para>Disconnected is outside the question deliberately: that object
        /// reaches nobody either, and saying so tells a developer running the
        /// SDK offline something they already know once per spawn.</para>
        /// </remarks>
        internal static bool SpawnReachesNobody(bool isConnected, string localPlayerId)
            => isConnected && string.IsNullOrEmpty(localPlayerId);
    }
}
