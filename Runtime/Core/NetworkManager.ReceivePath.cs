// RTMPE SDK — Runtime/Core/NetworkManager.ReceivePath.cs
//
// ProcessPacket dispatch + inbound handlers + transport error path + state machine.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Core.Rpc;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // Malformed UTF-8 must reject the packet, mirroring every room/lobby
        // packet parser.  The default Encoding.UTF8 substitutes U+FFFD
        // instead, which would let a corrupted or hostile payload pass the
        // decode and surface as silently-mangled property keys and values.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // ── Legacy / other handlers ────────────────────────────────────────────

        private void OnHandshakeAck(byte[] _)
        {
            // 🚨 This handler used to tear the connection down, and that made a
            // dead opcode into a remote kill-switch.
            //
            // `HandshakeAck` (0x02) is the W3–W5 legacy handshake reply. It
            // needs no encryption and no established session — correctly, since
            // no key exists that early — so a bare 13-byte plaintext header is
            // all it takes to reach here. Tearing down on it meant one spoofed
            // datagram, carrying no key material and proving nothing, ended
            // every handshake this client attempted; repeated, the client could
            // never connect. The transport pins the source endpoint, so the
            // sender has to forge the gateway's address — trivial on-path, and
            // an off-path guess away.
            //
            // 🔑 The trust relationship was exactly inverted. `OnServerDisconnect`
            // below receives an AEAD-AUTHENTICATED packet and REFUSES to tear
            // down a handshake in progress, in a comment that names this precise
            // failure. The unauthenticated opcode was the one that was honoured.
            //
            // ⛔ Nothing sends this. The gateway builds a `HandshakeAck` in one
            // place, a unit test, and says so where its plaintext arm is declared
            // ("nothing in this crate builds one: the handler that did was
            // deleted"). ⛔ And a `HandshakeAck` that reaches this handler
            // necessarily claims the CURRENT protocol version: `ValidateHeader`
            // refuses any other byte as `UnsupportedVersion` several gates
            // earlier. So it is a packet claiming to speak this protocol while
            // naming an opcode no server of this protocol sends — which says
            // something about the sender and nothing about the session.
            //
            // ⚠️ An earlier version of this comment said a legacy gateway
            // "would stamp protocol version 3". Nothing in this repository
            // records that: the only mention of a version 3 is a stale comment
            // in a shipped test whose code writes `PacketProtocol.VERSION`, and
            // "W3–W5" is a workstream label rather than a version number. The
            // argument does not need the claim and no longer makes it.
            //
            // So it is dropped, and the handshake it interrupted is left alone.
            // Dropping cannot promote state — the concern the old comment
            // raised, that accepting one would leave the session keys null while
            // the client believed it was connected, is a reason not to ACCEPT
            // it, and was never a reason to disconnect on it.
            if (ShouldWarn(ref _lastLegacyHandshakeAckWarnTicks))
                Debug.LogWarning(
                    "[RTMPE] Ignoring HandshakeAck (0x02): the legacy unauthenticated handshake " +
                    "is not part of this protocol version and no gateway emits it. The packet " +
                    "carries no key material and proves nothing about its sender, so it is " +
                    "dropped rather than acted on; the handshake in progress is unaffected.");
        }

        // Its own gate. A flood of these must not silence another diagnostic,
        // and — more to the point — the rate at which this one fires is the only
        // signal that somebody is aiming forged handshake traffic at this client.
        private long _lastLegacyHandshakeAckWarnTicks;

        // Its own gate, on the same reasoning: a peer reserving ids sends one
        // spawn per id it wants, so the rate at which this fires is the measure
        // of the attempt, and spending another diagnostic's budget on it would
        // hide both.
        private long _lastForeignIdClaimWarnTicks;

        // Rate gate for a spawn dropped because the hold kept for its departed
        // owner's return was full.
        private long _lastHeldSpawnRefusedWarnTicks;

        // Rate gate for a spawn held for a returning player that threw while it
        // was applied (OnRoomManagerPlayerJoined).
        private long _lastHeldSpawnReleaseThrowWarnTicks;

        // Its own gate again: a claimant that reserved ids sends one despawn
        // per object it wants gone, and the rate is the measure of that.
        private long _lastForeignDespawnWarnTicks;

        // The two gates above bound the log line, which is what makes the tally
        // beneath them the instrument: a gate answers a sustained refusal with a
        // single entry, so the console cannot distinguish a rule that fired once
        // from one firing every frame — nor either of those from a rule refusing
        // traffic it should have admitted.
        private readonly ObjectIdSpaceRefusals _idSpaceRefusals = new ObjectIdSpaceRefusals();

        /// <summary>
        /// Inbound spawns refused because their object id belongs to this client's own id range
        /// under another player's name, since this manager was created. <c>0</c> in a healthy
        /// room.
        /// </summary>
        public long RefusedForeignIdClaimCount =>
            _idSpaceRefusals.RefusedForeignClaimCount;

        /// <summary>
        /// Inbound despawns refused because they named an object this client both owns and
        /// created, since this manager was created.
        /// </summary>
        /// <remarks>
        /// It usually moves together with <see cref="RefusedForeignIdClaimCount"/>. It can also
        /// grow on its own, for example after a lost ownership-transfer notice.
        /// </remarks>
        public long RefusedForeignDespawnCount =>
            _idSpaceRefusals.RefusedForeignDespawnCount;

        private void OnHeartbeatAck(byte[] data)
        {
            // Gateway puts a single backpressure byte (0-255) in the
            // HeartbeatAck payload.  See modules/gateway/src/main.rs:242-247
            // and src/router/session_limiter.rs::backpressure().  Older
            // gateways (and reconnect-only test fixtures) emit an empty
            // payload — the length check makes this fully backward-compatible.
            var payload = PacketParser.ExtractPayload(data);
            if (payload.Length >= 1)
            {
                System.Threading.Volatile.Write(ref _serverBackpressure, payload[0]);
            }
            _heartbeatManager?.OnAckReceived();
        }

        private void OnHeartbeatTimeout()
        {
            Debug.LogWarning("[RTMPE] Heartbeat timeout — no acknowledged keep-alive within the liveness window. Disconnecting.");
            // Transition through Disconnecting first so listeners observing state
            // changes see the full lifecycle (Connected → Disconnecting → Disconnected),
            // consistent with the explicit Disconnect() path.
            TransitionTo(NetworkState.Disconnecting);
            _networkThread?.Stop();
            // N-1: preserve the reconnect token across the drop so apps can
            // observe OnDisconnected and call Reconnect() without the user
            // having to re-authenticate.  If the app doesn't want a reconnect
            // (e.g. explicit logout), calling Disconnect() still clears it.
            ClearSessionData(preserveReconnectToken: true);
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ConnectionLost);
        }

        /// <summary>
        /// Route room packets to the RoomManager (lifecycle 0x20–0x23,
        /// management 0x2C/0x2E/0x2F).
        /// </summary>
        private void OnRoomPacket(PacketType type, byte[] data)
        {
            if (_roomManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            _roomManager.HandleRoomPacket(type, payload);
        }

        /// <summary>
        /// Routes a LobbyJoin reply (0x27) or LobbyList reply (0x29) to the
        /// LobbyManager.  LobbyLeave (0x28) has no server reply but is passed
        /// here for uniform event notification if needed.
        /// </summary>
        private void OnLobbyPacket(PacketType type, byte[] data)
        {
            if (_lobbyManager == null) return;
            if (type == PacketType.LobbyLeave) return; // fire-and-forget: no reply payload
            var payload = PacketParser.ExtractPayload(data);
            // Forward the discriminating PacketType so the LobbyManager only
            // consumes a pending JoinLobby slot when an actual LobbyJoin reply
            // arrives — a stray LobbyList (0x29) reply must not flip
            // IsInLobby.
            _lobbyManager.HandleLobbyReply(type, payload);
        }

        /// <summary>
        /// Routes a LobbyRoomListUpdate push (0x2A) to the LobbyManager.
        /// </summary>
        private void OnLobbyRoomListUpdate(byte[] data)
        {
            if (_lobbyManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            _lobbyManager.HandleLobbyRoomListUpdate(payload);
        }

        /// <summary>
        /// Handle an inbound <c>RoomPropertyUpdate</c> (0x24) broadcast from
        /// the server.  Decodes the JSON payload and applies the accepted
        /// property snapshot to the local <see cref="RoomManager.CurrentRoom"/>.
        /// </summary>
        private void OnRoomPropertyUpdateBroadcast(byte[] data)
        {
            if (_roomManager == null) return;

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RoomPropertyUpdate broadcast rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (payload == null || payload.Length == 0) return;
            try
            {
                var json = StrictUtf8.GetString(payload);
                var (version, props) = PropertyJson.DecodeRoomPayload(json);
                _roomManager.ApplyRoomPropertiesBroadcast(version, props);
            }
            catch (Exception ex)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RoomPropertyUpdate broadcast: decode failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle an inbound <c>PlayerPropertyUpdate</c> (0x25) broadcast from
        /// the server.  Decodes the JSON payload and applies the accepted
        /// property snapshot to the matching player in
        /// <see cref="RoomManager.CurrentRoom"/>.
        /// </summary>
        private void OnPlayerPropertyUpdateBroadcast(byte[] data)
        {
            if (_roomManager == null) return;

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"PlayerPropertyUpdate broadcast rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (payload == null || payload.Length == 0) return;
            try
            {
                var json = StrictUtf8.GetString(payload);
                var (playerId, version, props) = PropertyJson.DecodePlayerPayload(json);
                _roomManager.ApplyPlayerPropertiesBroadcast(playerId, version, props);
            }
            catch (Exception ex)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"PlayerPropertyUpdate broadcast: decode failed: {ex.Message}");
            }
        }

        // ── Spawn / Despawn inbound handlers ─────────────────────────

        // A staged catch-up packet was shed because the pre-room buffer stood at
        // capacity, so its oldest entry was dropped to make room. Surfaced as a
        // rate-limited warning rather than passing unseen: a lost pre-join Spawn,
        // Despawn, or RPC replay can leave a peer's object unrendered until the
        // next full snapshot.  The oldest may be an ended entry's — a set the
        // transition would have discarded anyway — which the message allows
        // for.  Gated to one line per second so a sustained overflow reports
        // the condition without flooding the log.
        private void WarnIfEarlyObjectEvicted(bool evicted)
        {
            if (evicted && ShouldWarn(ref _lastEarlyObjectEvictWarnTicks))
                Debug.LogWarning(
                    "[RTMPE] Early-object staging buffer reached a ceiling — " +
                    StagedCatchUpPacketCount + " packets, " + StagedCatchUpPacketBytes +
                    " bytes — while holding the room's objects for a room entry or a scene " +
                    "load, and the oldest Spawn, Despawn or ownership transfer was evicted; " +
                    "the RPC catch-up and roster changes are kept. A room entry or load that " +
                    "completes never reaches either ceiling: this is a session receiving " +
                    "lifecycle packets without entering a room, a room with far more live " +
                    "objects than the buffer, or a sender staging far more than that into one.");
        }

        /// <summary>
        /// Handle an inbound <c>Spawn</c> (0x30) packet from the server.
        /// Parses the payload and calls <see cref="SpawnManager.CreateLocal"/>
        /// to instantiate the object on the receiving client.
        /// </summary>
        private void OnSpawnPacket(byte[] data)
        {
            if (_spawnManager == null) return;

            // Game-data packets are applied only after a successful room join;
            // before then the room context does not yet exist.  A Spawn that
            // arrives early is the joiner's own catch-up object — the server
            // replays the live set as the session binds, which can outrun the
            // join reply that opens this gate — so hold it (a defensive copy,
            // the receive buffer may be reused) for ordered release on InRoom
            // rather than dropping it.  Nothing is applied while staged, so the
            // pre-room injection guard is preserved.
            if (_state != NetworkState.InRoom)
            {
                // ⛔ Staged only while a room entry is actually outstanding.
                // The buffer is released or discarded at the NEXT room entry,
                // so a frame staged with no entry in flight does not belong to
                // the room this client is about to be in — it is a straggler
                // from the room it has left, and releasing it there applies
                // another room's lifecycle to this one: an object that exists
                // for nobody else, or a despawn that removes one that is
                // genuinely present.  The RpcBufferReplay path already states
                // this argument, and it is no narrower here.
                //
                // ⚠️ Why the legitimate catch-up survives this is a SEQUENCING
                // argument, not a structural one, and it is written out because
                // the difference matters.  `_pendingJoin.Disarm()` and the
                // transition to InRoom land in the same synchronous dispatch and
                // packets are pumped one at a time, so no catch-up frame is
                // observed between them.
                //
                // ⛔ An entry can also END with no transition — a join ladder
                // exhausted, a reply refused, a matchmaking deadline passed —
                // and what was staged during it then outlives it in this buffer,
                // where the next entry's transition would release it: the
                // previous room's lifecycle applied to the next.  The frame is
                // therefore staged under the entry begun most recently, and the
                // transition into a room says which entry it completes: what
                // was staged before that entry began is discarded unreleased.
                //
                // ⚠️ The residual this leaves is a frame of the ENDED entry
                // arriving after the next one has begun: it is staged under the
                // next entry, and nothing on the frame says otherwise — it
                // carries an object id and no room.  Where the ended entry's
                // own late reply follows (a server that stalled through the
                // ladder and then answered everything in order), that reply is
                // refused and the managers say so (OnEndedEntryAnswered), which
                // discards the buffer ahead of the next entry's catch-up.  A
                // straggler with no reply behind it — a spawn-replay re-send,
                // which lands a few hundred milliseconds after the first — is
                // released into the next entry if the user began it inside that
                // gap.
                //
                // ⚠️ And the property is `(_roomManager != null && …) ||
                // (_matchmakingManager != null && …)`: before those managers
                // exist there is no room entry to be outstanding, so the guard
                // refuses — which is correct, and is not the same statement as
                // RoomManager's own expression.
                if (!IsRoomEntryOutstanding)
                {
                    if (ShouldWarn(ref _lastStraySpawnWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped a Spawn that arrived with no room entry outstanding " +
                            $"(state={_state}). Holding it would carry the previous room's object " +
                            "lifecycle into the next room this client enters.");
                    return;
                }

                StageLifecyclePacket(EarlyPacketKind.Spawn, data);
                return;
            }

            // In the room, while the room's scene loads in single mode: built
            // now, the object goes into the scene the load is about to unload
            // and is destroyed with it, with nothing sent and no re-send coming
            // (audit P3-E1).  Staged where the pre-room catch-up waits, in the
            // same order with the despawns and the RPC catch-up, and released
            // when the load is reported (FlushEarlyObjectPackets).
            if (HoldsObjectsForSceneLoad)
            {
                StageLifecyclePacket(EarlyPacketKind.Spawn, data);
                return;
            }

            ApplySpawnPacket(data, fromStagedCatchUp: false);
        }

        /// <summary>
        /// The room's scene is loading in single mode on this client, and the
        /// objects the room sends wait for it
        /// (<see cref="RTMPE.Rooms.NetworkSceneManager.HoldsObjectsForSceneLoad"/>).
        /// </summary>
        /// <remarks>
        /// Also read by <see cref="RtmpeConnectionBootstrap"/>, which builds the
        /// local player when the hold ends rather than into the scene the load
        /// unloads. Never creates the scene manager: with none, nothing loads.
        /// </remarks>
        internal bool HoldsObjectsForSceneLoad =>
            _sceneManager != null && _sceneManager.HoldsObjectsForSceneLoad;

        /// <summary>
        /// The room's objects are not all built here yet: a scene load is holding
        /// them, or the staged catch-up is still being released a few dozen a
        /// frame.  Read by <see cref="RtmpeWorldSpawner"/>, whose host must not
        /// conclude the room has no world while the room's world may be among them.
        /// </summary>
        internal bool RoomObjectsStillArriving =>
            HoldsObjectsForSceneLoad || _earlyObjectBuffer.Count > 0;

        /// <summary>
        /// Stage a Spawn or Despawn — a defensive copy, the receive buffer may be
        /// reused — under the room entry begun most recently, folded into what
        /// is already staged for its object (<see cref="EarlyObjectPacketBuffer.StageObject"/>).
        /// </summary>
        /// <remarks>
        /// Folded because a scene load stages a whole load's worth of the room:
        /// the replay ladder sends every object three times, and an object a peer
        /// spawns and removes while this client loads is two packets for nothing.
        /// A Despawn that folds away the spawn it ends still owes what applying it
        /// would have done to an object never built here: the frames held for the
        /// object go, and the despawn is kept on record, so a later re-send of the
        /// spawn does not bring it back.  A packet that does not parse is staged
        /// unfolded; its release refuses it as it always did.
        /// </remarks>
        private void StageLifecyclePacket(EarlyPacketKind kind, byte[] data)
        {
            var copy = (byte[])data.Clone();
            var payload = PacketParser.ExtractPayload(copy);
            ulong objectId = 0UL;
            if (kind == EarlyPacketKind.Spawn && SpawnPacketParser.TryParseSpawn(payload, out var spawn))
                objectId = spawn.ObjectId;
            else if (kind == EarlyPacketKind.Despawn && SpawnPacketParser.TryParseDespawn(payload, out ulong despawned))
                objectId = despawned;

            // In the room the buffer keeps what it holds — the room's catch-up is
            // the oldest of it — and refuses what overflows; and the packet's
            // place in the gateway's send order travels with it, for the release
            // to judge a departed owner's spawn by as the live path does.
            var outcome = _earlyObjectBuffer.StageObject(
                kind, copy, RoomEntryToken, objectId, _inboundSendCounter,
                keepOldest: _state == NetworkState.InRoom);
            if (outcome == ObjectStageOutcome.CollapsedTheStagedSpawn)
            {
                _heldVariableUpdates.Discard(objectId);
                _heldRpcEvents.Discard(objectId);
                _spawnManager?.RememberDespawned(objectId);
            }
            WarnIfEarlyObjectEvicted(outcome == ObjectStageOutcome.StagedWithEviction
                                     || outcome == ObjectStageOutcome.Refused);
        }

        /// <summary>
        /// Apply a Spawn packet: one that arrived live and in room, or one the
        /// staged release let through.
        /// </summary>
        /// <param name="fromStagedCatchUp">
        /// True only for the two legitimate bursts a client can never ask for
        /// again: the staged release, and the spawns held for a departed owner's
        /// return, released when that owner arrives
        /// (<see cref="OnRoomManagerPlayerJoined"/>).  It exempts the spawn from
        /// the per-second RATE cap — see
        /// <see cref="SpawnManager.CreateLocalFromStagedCatchUp"/> — because that
        /// cap discards what it refuses.  The per-room COUNT cap still applies,
        /// so the allocation bound is intact.
        /// </param>
        /// <param name="stagedSentAt">
        /// For a staged spawn, its place in the gateway's send order when it was
        /// staged; -1 when it was not recorded.
        /// </param>
        private void ApplySpawnPacket(byte[] data, bool fromStagedCatchUp, long stagedSentAt = -1L)
        {
            var payload = PacketParser.ExtractPayload(data);
            if (!SpawnPacketParser.TryParseSpawn(payload, out var spawnData))
            {
                if (IsDebugLogEnabled)
                    LogDebug("Spawn packet: malformed payload, dropped.");
                return;
            }

            // ⛔ An id in this client's own space, claimed by somebody else.
            //
            // The decision itself lives in ObjectLifecycleAuthority, which a
            // shard executes: this file is compiled by none, so a condition
            // written out here could only ever be asserted over its own text —
            // and text cannot tell a right condition from an inverted one. It
            // is reached through the tally so that taking the decision and
            // recording it are one act rather than two beside each other.
            //
            // ⚠️ Ordered before the dedup deliberately. A reserved id this
            // client has already spawned under is present in the registry, and
            // the dedup would answer "already exists" and return — dropping the
            // frame in silence rather than reporting the claim.
            //
            // An id this client issued and no longer holds as its own is not a
            // claim: it was handed on — by a transfer, or by this client's own
            // departure to the room's host — and a player who left and rejoined
            // within the session is replayed it under its new owner (plan §10/2).
            //
            // ⛔ What this closes is bounded, and the bound is worth stating:
            // the claimant's object is never instantiated here, never
            // registered, and never charged to this client's spawn count. It
            // does NOT unmake the claim, which stands at the gateway and on
            // every other client, so this client's own later spawn under that id
            // is still refused there as a collision.
            if (_idSpaceRefusals.RefusesForeignClaimOnOwnIdSpace(
                    spawnData.ObjectId, spawnData.OwnerPlayerId,
                    _localPlayerId, _localPlayerStringId,
                    _spawnManager.LastIssuedLocalCounter,
                    _spawnManager.RegisteredOwnerOf(spawnData.ObjectId)))
            {
                if (WarnGate.ShouldEmit(ref _lastForeignIdClaimWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Spawn refused: object id " + spawnData.ObjectId +
                        " lies in this client's own id space but is claimed by '" +
                        Diagnostics.UntrustedLogText.Sanitise(spawnData.OwnerPlayerId) +
                        "'. A peer cannot mint an id in another session's space; " +
                        "the spawn was not applied.");
                return;
            }

            // Dedup: if this object was already spawned locally (e.g. server echoed
            // our own Spawn back), skip to avoid creating a duplicate GameObject.
            if (_spawnManager.Registry.Get(spawnData.ObjectId) != null)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Spawn packet: objectId {spawnData.ObjectId} already exists, skipped (dedup).");
                return;
            }

            // A spawn whose owner's departure tombstone stands is judged by where
            // the gateway sent it.  Sent before the departure, it is a straggler
            // of the life that ended and is dropped, as the tombstone always did;
            // sent after — the gateway withholds a relay whose sender has left,
            // gone or been announced departed, and the room replays only what
            // seated players own, within the limits DepartedOwnerSpawns states —
            // it is the returning player's, whose first spawn can reach this
            // client ahead of its arrival, and is held for that arrival
            // (OnRoomManagerPlayerJoined), dropped when none comes.  A staged
            // spawn is judged by the place it was staged at, which the stage
            // recorded; one released without a place — the spawns held for a
            // returning player, released after its arrival lifted the
            // tombstone — is judged a straggler.
            if (_spawnManager.IsOwnerDeparted(spawnData.OwnerPlayerId))
            {
                long sentAt = fromStagedCatchUp ? stagedSentAt : _inboundSendCounter;
                var verdict = _spawnManager.HoldForOwnersReturn(
                    spawnData.OwnerPlayerId, spawnData.ObjectId, sentAt, (byte[])data.Clone());
                if (verdict == HeldSpawnVerdict.Refused && ShouldWarn(ref _lastHeldSpawnRefusedWarnTicks))
                {
                    Debug.LogWarning(
                        "[RTMPE] A spawn from a player who has just come back was dropped: the hold kept " +
                        "for that player's arrival is full.");
                }
                if (verdict == HeldSpawnVerdict.Dropped && IsDebugLogEnabled)
                    LogDebug($"Spawn packet: objectId {spawnData.ObjectId} dropped — its owner has left.");
                return;
            }

            // The lifetime declaration travels with the spawn because the room
            // acts on it: the gateway releases the ownership record of a
            // declared object when its owner leaves, and an object no record
            // names is one every member may drive.  A receiver that answered
            // from its own component instead would be free to keep an object
            // the room has already stopped protecting.
            //
            // Whether the room's host sent it travels the same way, for the
            // one component that asks: a world object is elected only from a
            // spawn the host sent (RtmpeWorldAuthority, audit P4-E1).
            NetworkBehaviour spawned = fromStagedCatchUp
                ? _spawnManager.CreateLocalFromStagedCatchUp(
                    spawnData.PrefabId, spawnData.ObjectId,
                    spawnData.OwnerPlayerId, spawnData.Position, spawnData.Rotation,
                    spawnData.DestroyWithOwner, spawnData.SpawnedByHost)
                : _spawnManager.CreateLocal(
                    spawnData.PrefabId, spawnData.ObjectId,
                    spawnData.OwnerPlayerId, spawnData.Position, spawnData.Rotation,
                    spawnData.DestroyWithOwner, spawnData.SpawnedByHost);

            // The object is known from here — registered, its components'
            // OnNetworkSpawn run and their variables constructed — so the
            // VariableUpdate frames that reached this client ahead of it, the
            // late-join snapshot among them, are applied now, in the order they
            // arrived, before the next inbound packet is dispatched.  Only on a
            // spawn that committed: a refused or rolled-back spawn leaves the
            // object unknown, and its frames wait for the re-send or expire.
            //
            // And then the RPCs that addressed it before it existed, in the order
            // they arrived — after its variables, so a handler reads the state
            // the room holds.
            if (spawned != null)
            {
                ReleaseHeldVariableUpdates(
                    spawnData.ObjectId,
                    !string.IsNullOrEmpty(_localPlayerStringId)
                    && string.Equals(spawnData.OwnerPlayerId, _localPlayerStringId, StringComparison.Ordinal));
                ReleaseHeldRpcEvents(spawnData.ObjectId);
            }
        }

        /// <summary>
        /// Apply a <c>SpawnRejected</c> (0x32): the room would not accept a
        /// spawn this client asked for.
        /// </summary>
        /// <remarks>
        /// ⛔ Not staged behind the room gate that holds <c>Spawn</c> and
        /// <c>Despawn</c>, and the asymmetry is the point: those two carry
        /// another player's object lifecycle and must be replayed in order with
        /// the catch-up set. This is an answer to a request THIS client made,
        /// about an object THIS client already holds, and holding it back would
        /// delay the one report the caller is waiting for — or drop it entirely,
        /// because a rejection for a room this client has left is still true of
        /// the object it is still holding.
        /// </remarks>
        private void OnSpawnRejectedPacket(byte[] data)
        {
            if (_spawnManager == null) return;
            _spawnManager.HandleSpawnRejected(PacketParser.ExtractPayload(data));
        }

        /// <summary>
        /// Handle an inbound <c>Despawn</c> (0x31) packet from the server.
        /// Parses the object ID and calls <see cref="SpawnManager.DestroyLocal"/>.
        /// </summary>
        private void OnDespawnPacket(byte[] data)
        {
            if (_spawnManager == null) return;

            // Held until InRoom for the same reason as Spawn: a despawn that
            // races ahead of the join reply belongs to the catch-up set and must
            // be replayed in order with its spawns so it does not resurrect an
            // object the server has already removed.  See OnSpawnPacket.
            if (_state != NetworkState.InRoom)
            {
                // Staged only while a room entry is actually outstanding; the
                // argument is stated in full at the same guard in OnSpawnPacket
                // and is symmetric — a straggling Despawn released into the next
                // room removes an object that is genuinely present there.
                if (!IsRoomEntryOutstanding)
                {
                    if (ShouldWarn(ref _lastStrayDespawnWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped a Despawn that arrived with no room entry outstanding " +
                            $"(state={_state}). Holding it would carry the previous room's object " +
                            "lifecycle into the next room this client enters.");
                    return;
                }

                StageLifecyclePacket(EarlyPacketKind.Despawn, data);
                return;
            }

            // Behind the spawns the scene load holds, in the same order: applied
            // now it would remove nothing — its object is staged — and the spawn
            // released after it would bring the object back.
            if (HoldsObjectsForSceneLoad)
            {
                StageLifecyclePacket(EarlyPacketKind.Despawn, data);
                return;
            }

            ApplyDespawnPacket(data);
        }

        /// <summary>
        /// Apply a Despawn packet: one that arrived live and in room, or one the
        /// staged release let through.
        /// </summary>
        private void ApplyDespawnPacket(byte[] data)
        {
            var payload = PacketParser.ExtractPayload(data);
            if (!SpawnPacketParser.TryParseDespawn(payload, out var objectId))
            {
                LogDebug("Despawn packet: malformed payload, dropped.");
                return;
            }

            // ⛔ A despawn naming an object this client both owns and minted.
            //
            // The room relays a despawn only from the object's recorded owner
            // and excludes the originator from its own fan-out, so this frame
            // could not have come from a legitimate sender. What produces it is
            // a peer that reserved the id before this client reached it: the
            // spawn is refused above, but the record it created stands at the
            // gateway, and this is the room honouring the claimant's word
            // against this client's own object.
            var held = _spawnManager.Registry.Get(objectId);
            if (held != null
                && _idSpaceRefusals.RefusesDespawnOfAnObjectThisClientOwns(
                       objectId, held.OwnerPlayerId, _localPlayerId, _localPlayerStringId))
            {
                if (WarnGate.ShouldEmit(ref _lastForeignDespawnWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Despawn refused: object id " + objectId +
                        " is owned by this client and was minted by it, so the room had no " +
                        "sender it would relay this from. The object was kept.");
                return;
            }

            _spawnManager.DestroyLocal(objectId);

            // A frame held for this object arrived in the other order — before
            // the Spawn this Despawn is retiring, or for a life this client never
            // saw — and would be applied to the object's NEXT spawn under the
            // same id, which the id space forbids but a late frame cannot know.
            _heldVariableUpdates.Discard(objectId);
            _heldRpcEvents.Discard(objectId);

            // And the despawn kept on record for a spawn of it sent since: a
            // replay the room read before it forgot the object would otherwise
            // bring it back.
            _spawnManager.RememberDespawned(objectId);
        }

        // Per-frame ceiling on the staged release.  At 30 fps this empties a
        // full 2048-entry buffer in about a second — enough to keep a 1000-object
        // catch-up off a single frame, and short enough that everything
        // downstream which assumes a lifecycle packet is applied PROMPTLY still
        // holds: the despawn-tombstone and departed-player windows are 5 s each,
        // and every inbound handler that resolves an object id (VariableUpdate,
        // Rpc, ownership, scene) drops a packet whose object has not been
        // released yet.
        private const int MaxStagedObjectDrainPerFrame = 64;

        /// <summary>
        /// Release the object-lifecycle packets staged while the client was not
        /// yet in a room, in arrival order, a bounded number per frame.
        /// </summary>
        /// <remarks>
        /// 🔑 **Bounded per frame, and exempt from the per-second rate cap.**
        /// This used to release the whole buffer in one call, straight into
        /// <c>CheckSpawnAdmission</c> — which refuses at 100/s and DISCARDS what
        /// it refuses, with nothing to re-send a spawn — so a joiner handed a
        /// 1000-object room kept about a hundred of them and never learned of
        /// the rest.
        ///
        /// ⛔ **Metering the release against that cap was the wrong repair, and
        /// this is the second attempt.** It stretched a 1000-object catch-up to
        /// ten seconds, and over a window that long the 5-second despawn and
        /// departed-player TTLs expire mid-release, every other inbound handler
        /// silently drops packets for objects not yet released, and the
        /// application's own <c>Spawn()</c> is starved of the same bucket. The
        /// cap exists to bound a SUSTAINED hostile stream; this set is bounded
        /// by the buffer, arrives once, and cannot be requested again — so it is
        /// released on its own budget, and the per-room COUNT cap, which is the
        /// actual allocation bound, still applies to every one of them.
        ///
        /// Called on room entry and then every frame while anything is staged.
        /// Spawn deduplicates against objects the client already holds, so a
        /// staged object the server later re-sent live is not instantiated
        /// twice, and a catch-up RpcReplay follows the spawns it may target
        /// because order is preserved within the buffer.
        /// </remarks>
        private void FlushEarlyObjectPackets()
        {
            if (_earlyObjectBuffer.Count == 0) return;

            // ⛔ Not while the room's scene loads: what is staged would be built
            // into the scene the load unloads.  Entering a room that has a scene
            // is exactly when the catch-up is staged and the hold begins — in
            // that order, inside one call (NetworkSceneManager.PrepareForEntry)
            // — and the per-frame call below resumes the release once the load
            // is reported.
            if (HoldsObjectsForSceneLoad) return;

            _earlyObjectBuffer.DrainBounded(
                staged => EarlyObjectPacketBuffer.Dispatch(
                    staged,
                    ReleaseStagedSpawn,
                    ApplyDespawnPacket,
                    HandleRpcBufferReplay,
                    ReleaseStagedLegacyRpc,
                    ApplyStagedRosterMarker,
                    kind =>
                    {
                        // A staged kind with no handler is a programming error — a new
                        // EarlyPacketKind added without a dispatch case — so surface it
                        // rather than mis-route it to one of the real handlers.
                        if (IsDebugLogEnabled)
                            LogDebug($"FlushEarlyObjectPackets: unhandled staged kind {kind}, dropped.");
                    }),
                MaxStagedObjectDrainPerFrame);
        }

        // The staged release's Spawn entry point.  Exists as a named method so
        // the dispatch above names the exemption rather than passing a flag
        // through a lambda, and so nothing can route the staged release back
        // through OnSpawnPacket, which would re-run the pre-room gate.
        //
        // The staged entry carries the spawn's place in the gateway's send
        // order, so a departed owner's spawn is judged at release as the live
        // path judges it: a returning player's avatar, staged between its
        // departure and its arrival, is held for that arrival and not dropped.
        private void ReleaseStagedSpawn(EarlyObjectPacketBuffer.Staged staged)
            => ApplySpawnPacket(staged.Data, fromStagedCatchUp: true, stagedSentAt: staged.Counter);

        // ── RPC inbound handlers ─────────────────────────────────────

        /// <summary>
        /// Handle an inbound <c>Rpc</c> (0x50) request from the server.
        /// Dispatches ownership-related RPCs (200) and damage RPCs (301).
        /// </summary>
        private void OnRpcRequest(byte[] data)
        {
            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RPC request rejected; not in a room (state={_state}).");
                return;
            }

            // Distinguish Enhanced RPC (27-byte header, typed params) from legacy (18-byte).
            bool isEnhanced = (data[PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.EnhancedRpc) != 0;

            var payload = PacketParser.ExtractPayload(data);

            if (isEnhanced)
            {
                OnEnhancedRpcRequest(payload);
                return;
            }

            // Legacy RPC path.
            if (!RpcPacketParser.TryParseRequest(payload, out var request))
            {
                if (IsDebugLogEnabled)
                    LogDebug("RPC request: malformed payload, dropped.");
                return;
            }

            // AEAD authenticates the gateway as the relay, not the originating
            // peer.  The Enhanced RPC path already passes every inbound
            // senderId through EnhancedRpcVerifier.IsSenderAcceptable; the
            // legacy MethodId path applies the same uniform gate so a hostile
            // peer cannot stamp Ping (100) / ApplyDamage (301) /
            // TransferOwnership (200) with a spoofed senderId and have the
            // receiver dispatch as if the gateway had attested origin.
            // Per-method overrides (e.g. IsOwnershipTransferAuthorized) layer
            // on top of this gate at the matching case below.
            //
            // The settings toggle is consulted only inside the Unity Editor,
            // where loopback test rigs may legitimately deliver legacy RPCs
            // from senders outside the active roster.  All other build
            // targets enforce the gate unconditionally — the toggle cannot
            // weaken a distributed binary's security posture.
#if UNITY_EDITOR
            bool gateActive = _settings == null || _settings.requireLegacyRpcSender;
#else
            const bool gateActive = true;
#endif
            if (gateActive
                && !LegacyRpcVerifier.IsLegacyRpcAuthorized(
                       request.SenderId, request.MethodId))
            {
                // One line per inbound RPC the verifier refuses, and it is an
                // AUTHORISATION refusal rather than a malformed request — a peer
                // can drive it at whatever rate it sends (`RPC-RD-05`).
                // ⛔ The `return` is outside the gate: only the line is limited.
                if (WarnGate.ShouldEmit(ref _lastLegacyRpcRefusedWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] Legacy RPC rejected: sender " +
                        $"{LogRedaction.Redact(request.SenderId)} not authorised " +
                        $"for method_id {request.MethodId}.");
                return;
            }

            // An ownership transfer for an object the room has sent and this
            // client has not built — it is staged behind a scene load, or still
            // being released — is staged behind it: judged now it names nothing,
            // is refused as unauthorised, and the object is later built under its
            // previous owner for good.  The parse and the sender gate above have
            // run; the release applies it as it would have been applied here.
            //
            // One entry per object, the latest transfer standing: a sender
            // repeating transfers of its own object would otherwise fill the
            // buffer with them.
            if (request.MethodId == RpcMethodId.TransferOwnership
                && RoomObjectsStillArriving
                && request.Payload.Length >= 8
                && _spawnManager?.Registry.Get(RpcSerializer.ReadU64LE(request.Payload, 0)) == null)
            {
                WarnIfEarlyObjectEvicted(
                    _earlyObjectBuffer.StageTransfer((byte[])data.Clone(), RoomEntryToken,
                                                     RpcSerializer.ReadU64LE(request.Payload, 0), keepOldest: true));
                return;
            }

            ApplyLegacyRpc(request);
        }

        // A staged ownership transfer, released in its place after the spawns
        // that preceded it.  Parsed again from the frame it was staged as; the
        // sender gate ran when it arrived.
        private void ReleaseStagedLegacyRpc(byte[] data)
        {
            if (data == null || !RpcPacketParser.TryParseRequest(PacketParser.ExtractPayload(data), out var request))
                return;
            // Isolated: the staged release dequeues before it dispatches, and
            // the transfer raises the application's ownership callbacks.
            try
            {
                ApplyLegacyRpc(request);
            }
            catch (Exception ex)
            {
                if (ShouldWarn(ref _lastStagedLegacyRpcThrowWarnTicks))
                    Debug.LogError(
                        "[RTMPE] An ownership transfer held behind the room's arriving objects threw " +
                        $"when it was applied; the rest are applied regardless.\n{ex}");
            }
        }

        // The legacy method-id calls, applied: the live path and the staged
        // release both come here.
        private void ApplyLegacyRpc(RpcRequest request)
        {
            // Publish the gateway-attested sender for the legacy handlers too
            // (mirrors the Enhanced path) so an IDamageable / ownership handler
            // can authorize the call via NetworkManager.CurrentRpcSenderId;
            // saved and restored so a nested or throwing handler cannot leak it.
            ulong previousRpcSenderId = CurrentRpcSenderId;
            CurrentRpcSenderId = request.SenderId;
            try
            {
                switch (request.MethodId)
                {
                    case RpcMethodId.TransferOwnership:
                        HandleOwnershipTransferRpc(request);
                        break;
                    // Server-broadcast ApplyDamage (301) → route to target HealthController.
                    case RpcMethodId.ApplyDamage:
                        HandleApplyDamageRpc(request);
                        break;
                    default:
                        if (IsDebugLogEnabled)
                            LogDebug($"RPC request: unhandled method_id {request.MethodId}.");
                        break;
                }
            }
            finally
            {
                CurrentRpcSenderId = previousRpcSenderId;
            }
        }

        /// <summary>
        /// Dispatch an inbound Enhanced RPC packet to the target <c>NetworkBehaviour</c>.
        /// Resolves the object via the spawn registry and invokes the correct
        /// <c>[RtmpeRpc]</c> method via <see cref="RTMPE.Core.NetworkBehaviour.DispatchEnhancedRpc"/>.
        /// </summary>
        private void OnEnhancedRpcRequest(byte[] payload)
        {
            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Enhanced RPC rejected; not in a room (state={_state}).");
                return;
            }

            // Buffered (historical) RPCs must be processed before live RPCs
            // that arrive during the replay window; otherwise a live RPC's
            // state mutation can be overwritten by an older buffered handler
            // (re-entrant dispatch, or a future change that pumps the
            // dispatcher mid-replay, would let a live RPC interleave with
            // the replay loop and break the server-emitted ordering).
            // Queue the live RPC payload for drainage in arrival order once
            // the replay completes — RpcReplayBuffer owns the CAS guard +
            // the per-cap admission policy, see Runtime/Core/Rpc/RpcReplayBuffer.cs.
            //
            // And while the room's objects are still arriving — held for a scene
            // load, or staged and not yet released — and while live calls
            // deferred for that are still queued: the room's buffered history is
            // among the staged packets, released in its place, and a live call
            // dispatched ahead of it (into the per-object hold, released inside
            // the spawn) ran before the history it must follow — a colour the
            // room set to blue ending red for good on the joiner.  Drained by
            // Update once nothing is arriving (DrainReplayQueue).
            if (_rpcReplayBuffer.IsReplayInProgress
                || _rpcReplayBuffer.PendingCount > 0
                || RoomObjectsStillArriving)
            {
                var enqueueResult = _rpcReplayBuffer.TryEnqueue(payload);
                // A drop here represents a lost authoritative game-state RPC
                // during replay catch-up.  The warning surface is rate-limited
                // to one emission per second per cap so a hostile peer cannot
                // turn the buffer into an unbounded log-flood primitive; the
                // cumulative count is always exposed via
                // DroppedRpcReplayBufferCount for application-level alerting.
                switch (enqueueResult)
                {
                    case RpcReplayBuffer.EnqueueResult.DroppedPayloadTooLarge:
                        if (ShouldWarn(ref _lastRpcDropPayloadWarnTicks))
                        {
                            Debug.LogWarning(
                                $"[RTMPE] Enhanced RPC: pending payload " +
                                $"{(payload != null ? payload.Length : 0)} B exceeds per-payload cap " +
                                $"{RpcReplayBuffer.MaxPayloadBytes} B; dropped. " +
                                $"Total drops this session: {_rpcReplayBuffer.DroppedCount}.");
                        }
                        return;
                    case RpcReplayBuffer.EnqueueResult.DroppedCumulativeTooLarge:
                        if (ShouldWarn(ref _lastRpcDropCumulativeWarnTicks))
                        {
                            Debug.LogWarning(
                                $"[RTMPE] Enhanced RPC: cumulative pending bytes would exceed " +
                                $"{RpcReplayBuffer.MaxCumulativeBytes} B; dropped. " +
                                $"Total drops this session: {_rpcReplayBuffer.DroppedCount}.");
                        }
                        return;
                    case RpcReplayBuffer.EnqueueResult.DroppedSlotCapReached:
                        if (ShouldWarn(ref _lastRpcDropSlotWarnTicks))
                        {
                            Debug.LogWarning(
                                "[RTMPE] Enhanced RPC: pending-during-replay queue full " +
                                $"({RpcReplayBuffer.MaxPendingDuringReplay}); dropping to bound memory. " +
                                $"Total drops this session: {_rpcReplayBuffer.DroppedCount}.");
                        }
                        return;
                    case RpcReplayBuffer.EnqueueResult.Ok:
                        return;
                }
            }

            DispatchEnhancedRpcPayload(payload);
        }

        /// <summary>
        /// Decode and dispatch a single Enhanced RPC payload.  Shared by the
        /// live-arrival path and the post-replay drain so both observe
        /// identical parsing / lookup semantics.
        /// </summary>
        private void DispatchEnhancedRpcPayload(byte[] payload)
        {
            // An RPC for an object this client does not hold yet waits for it.
            // Ahead of the parse, whose object gate refuses an unknown id, and of
            // the duplicate witness, which would count the call twice — once
            // here and once when it is released.
            if (HoldRpcForItsObject(payload)) return;

            if (!EnhancedRpcPacketParser.TryParse(payload, out var req))
            {
                LogDebug("Enhanced RPC: malformed payload, dropped.");
                return;
            }

            // Once-delivery's witness on the receiving side.  The sender's
            // ladder re-sends a frame whose acknowledgement was late and the
            // gateway's ARQ window routes each sequence once (S4-01), so a
            // second delivery of one call's identity is that property failing
            // somewhere — counted, never refused, for the reason the window
            // gives.  Ahead of the object lookup: a repeat is a repeat whether
            // or not its object is still here to receive it.
            if (_recentRpcIdentities.Repeats(req.SenderId, req.RequestId))
                _enhancedRpcDuplicatesReceived++;

            var nb = _spawnManager?.Registry?.Get(req.ObjectId);
            if (nb == null)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Enhanced RPC: no spawned object with id {req.ObjectId} — dropped.");
                return;
            }

            // The wire addresses the object, not the component: a [RtmpeRpc]
            // method may be declared on the routing anchor or on any sibling
            // NetworkBehaviour of the same GameObject.  Resolve the owning
            // component before dispatch; the anchor is kept when it owns the id,
            // when nothing owns it, or when the id is ambiguous, so the anchor's
            // existing "no [RtmpeRpc] method" diagnostic is preserved.
            NetworkBehaviour target = ResolveEnhancedRpcTarget(nb, req.MethodId);

            // Publish the gateway-attested sender id for the duration of the
            // handler so game code can authorize the call (e.g. compare against
            // LocalPlayerId).  Saved and restored around the dispatch so the
            // ambient stays correct if a handler ever synchronously triggers
            // another dispatch, and reads 0 outside any dispatch — including the
            // AllBuffered replay drain, which routes through here too.
            //
            // The caller's attested facts travel the same way, for handlers any
            // caller may reach; a method's declared Caller is enforced from the
            // same value inside DispatchEnhancedRpc, before the handler runs.
            //
            // And the object the call addressed, which the facts are about: a
            // behaviour reads OwnsObject only when it IS that object.
            ulong previousRpcSenderId = CurrentRpcSenderId;
            var previousRpcCallerFacts = CurrentRpcCallerFacts;
            ulong previousRpcObjectId = CurrentRpcObjectId;
            CurrentRpcSenderId = req.SenderId;
            CurrentRpcCallerFacts = req.CallerFacts;
            CurrentRpcObjectId = req.ObjectId;
            try
            {
                target.DispatchEnhancedRpc(req.MethodId, req.Target, req.CallerFacts, req.Args);
            }
            finally
            {
                CurrentRpcSenderId = previousRpcSenderId;
                CurrentRpcCallerFacts = previousRpcCallerFacts;
                CurrentRpcObjectId = previousRpcObjectId;
            }
        }

        // The object id an Enhanced RPC payload addresses: after the method id,
        // the sender id and the request id (EnhancedRpcPacketParser).
        private const int EnhancedRpcObjectIdOffset = 4 + 8 + 4;

        /// <summary>
        /// Hold <paramref name="payload"/> for the object it addresses when this
        /// client does not hold that object yet.  Returns whether it was held.
        /// </summary>
        /// <remarks>
        /// <para>🔑 A buffered RPC reaches a late joiner beside the room's
        /// objects, and the objects can be later: the scene the joiner is
        /// loading holds them, the replay ladder re-sends a dropped spawn a
        /// second after the RPCs, and the gateway can deliver the two catch-ups
        /// out of order. Dropped here, as the receive path did, an AllBuffered
        /// event never reached a joiner whose object was not yet built (audit
        /// P3-E2); a live call overtaking a spawn was lost the same way.</para>
        /// <para>Held on the variable updates' terms — per object, bounded in
        /// frames and bytes, ten seconds of age, discarded with its object's
        /// despawn and with a room entry that ended — and released from inside
        /// the spawn. Nothing is parsed or dispatched while held; the release
        /// runs the whole parse, its sender and object gates included. Object id
        /// 0 names no object, and a payload too short to name one is the
        /// parser's to refuse.</para>
        /// </remarks>
        private bool HoldRpcForItsObject(byte[] payload)
        {
            if (_spawnManager == null || payload == null
                || payload.Length < RpcLimits.EnhancedRequestHeaderSize)
            {
                return false;
            }

            ulong objectId = RpcSerializer.ReadU64LE(payload, EnhancedRpcObjectIdOffset);
            if (objectId == 0UL || _spawnManager.Registry.Get(objectId) != null) return false;

            if (_heldRpcEvents.Hold(objectId, payload, RoomEntryToken, HeldVariableUpdates.NowMillis())
                && ShouldWarn(ref _lastHeldRpcEventEvictWarnTicks))
            {
                Debug.LogWarning(
                    "[RTMPE] The hold for RPCs awaiting their object's spawn reached a ceiling — " +
                    _heldRpcEvents.Count + " calls, " + _heldRpcEvents.Bytes + " bytes — and the " +
                    "oldest call was dropped. A peer is calling RPCs on objects this client does not " +
                    "spawn, or a room entry is delivering far more calls than objects.");
            }
            if (IsDebugLogEnabled)
                LogDebug($"Enhanced RPC: object {objectId} not spawned here yet — held until it is.");
            return true;
        }

        /// <summary>
        /// Dispatch every RPC held for <paramref name="objectId"/>, in arrival
        /// order, now that the object has spawned.  Called from inside the spawn,
        /// after its variables.
        /// </summary>
        private void ReleaseHeldRpcEvents(ulong objectId)
        {
            // One delegate for the session: a method group converted per spawn
            // is an allocation, and a spawn burst is where this runs.
            _dispatchHeldRpcEvent ??= DispatchHeldRpcEvent;
            int released = _heldRpcEvents.Release(objectId, _dispatchHeldRpcEvent);
            if (released > 0 && IsDebugLogEnabled)
                LogDebug($"Dispatched {released} RPC(s) held for object {objectId} until it spawned.");
        }

        // Each held call under its own guard: the hold dequeues a call before it
        // runs, so a handler that throws would lose every call behind it.
        private void DispatchHeldRpcEvent(byte[] payload)
        {
            try
            {
                DispatchEnhancedRpcPayload(payload);
            }
            catch (Exception ex)
            {
                LogDebug(
                    "Enhanced RPC held for its object threw on release: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Reused across inbound Enhanced RPC dispatches (which run on the main
        // thread via MainThreadDispatcher) so target resolution allocates nothing
        // per call.  Mirrors the flush-scratch pattern in NetworkManager.Lifecycle.
        private readonly List<NetworkBehaviour> _rpcTargetScratch = new List<NetworkBehaviour>(8);
        private readonly List<Type> _rpcTargetTypeScratch = new List<Type>(8);

        /// <summary>
        /// Resolve the <see cref="NetworkBehaviour"/> that should receive an
        /// Enhanced RPC for <paramref name="methodId"/> on <paramref name="anchor"/>'s
        /// object.  The routing anchor keeps precedence; a uniquely-owning sibling
        /// component is selected otherwise; and the anchor is returned unchanged
        /// when it owns the method, when no component does, or when the id is
        /// ambiguous — so <see cref="NetworkBehaviour.DispatchEnhancedRpc"/> emits
        /// its existing "no [RtmpeRpc] method" warning for a genuine miss.
        ///
        /// <para><b>Not exercised by any dotnet/CI test</b> — this method lives in
        /// the Unity-only compile.  The resolution *decision* is covered by the
        /// pure <see cref="RpcRegistry.TryResolveOwningType"/> unit tests; the
        /// invariants that only manual review can guard here are: the non-allocating
        /// <c>GetComponents(List&lt;T&gt;)</c> overload (which clears the list before
        /// filling), the 1:1 index alignment between <c>_rpcTargetScratch</c> and
        /// <c>_rpcTargetTypeScratch</c>, and the Unity fake-null <c>==</c>/<c>!=</c>
        /// checks that keep a destroyed-but-not-finalised component out of dispatch.
        /// The scratch is filled and released entirely within this call, before the
        /// resolved handler runs, so a handler that re-enters the receive path
        /// cannot observe a half-built list.</para>
        /// </summary>
        private NetworkBehaviour ResolveEnhancedRpcTarget(NetworkBehaviour anchor, uint methodId)
        {
            anchor.GetComponents(_rpcTargetScratch);
            _rpcTargetTypeScratch.Clear();
            int anchorIndex = -1;
            for (int i = 0; i < _rpcTargetScratch.Count; i++)
            {
                var component = _rpcTargetScratch[i];
                // A destroyed-but-not-finalised component reads as fake-null under
                // Unity's == override; record it as a null type so the resolver
                // skips it and the index alignment with _rpcTargetScratch holds.
                _rpcTargetTypeScratch.Add(component != null ? component.GetType() : null);
                if (ReferenceEquals(component, anchor)) anchorIndex = i;
            }

            NetworkBehaviour target = anchor;
            if (RpcRegistry.TryResolveOwningType(
                    _rpcTargetTypeScratch, anchorIndex, methodId, out int idx))
            {
                var resolved = _rpcTargetScratch[idx];
                if (resolved != null && !ReferenceEquals(resolved, anchor))
                {
                    // Nothing to advise here any more: a sibling's NetworkVariable
                    // members replicate as the anchor's do (ObjectDispatchOps
                    // flushes and applies object-wide), so the once-per-type
                    // warning this arm used to raise — "state on a non-anchor
                    // component does NOT replicate" — described a rule the
                    // runtime no longer has, and the first sample to keep its
                    // state beside a shipped component was told, falsely, that
                    // its board would never leave the host.
                    target = resolved;
                }
            }

            _rpcTargetScratch.Clear();
            _rpcTargetTypeScratch.Clear();
            return target;
        }

        /// <summary>
        /// Maximum number of events accepted in a single RpcBufferReplay frame.
        /// A hostile or buggy peer can advertise <c>event_count = 0xFFFF</c>
        /// (65 535); even with the per-event truncation check, a 65 535-iteration
        /// loop on the main thread is a trivial CPU-stall primitive on slower
        /// devices.  The room service legitimately buffers at most a few hundred
        /// catch-up events, so this cap leaves ample headroom while bounding
        /// worst-case work to a fixed budget.
        /// </summary>
        internal const int MaxRpcBufferReplayEvents = 4096;

        // RPC replay state owned by RTMPE.Core.Rpc.RpcReplayBuffer.
        // The ordering barrier, the historical (buffered) and live (pending)
        // queues, the running byte counter, and the dropped-count atomic live
        // there.  The cap constants below are passthroughs so callers can
        // reference them without a direct dependency on RpcReplayBuffer.

        internal const int MaxPendingLiveRpcsDuringReplay   = RpcReplayBuffer.MaxPendingDuringReplay;
        internal const int MaxPendingLiveRpcPayloadBytes    = RpcReplayBuffer.MaxPayloadBytes;
        internal const int MaxPendingLiveRpcCumulativeBytes = RpcReplayBuffer.MaxCumulativeBytes;

        /// <summary>
        /// Wall-clock budget for a single catch-up replay drain pump, in
        /// milliseconds.  A late-join frame is already heavy (object spawns,
        /// full state sync), so a large pre-loaded RPC buffer is drained across
        /// a few frames under this budget rather than dispatched all at once —
        /// each catch-up event invokes arbitrary game code, and a peer that
        /// filled the room's buffer could otherwise stall a joining client's
        /// main thread for seconds.  At 30 Hz a frame is ~33 ms, so this slice
        /// stays well under one frame.
        /// </summary>
        internal const double ReplayDrainBudgetMillis = 4.0;

        /// <summary>
        /// Hard per-pump dispatch ceiling backing the wall-clock budget: a
        /// monotonic clock that fails to advance (or a non-positive budget)
        /// must not let one pump spin without bound.  Sized to the sum of both
        /// queue caps so a fast drain still completes in a single pump.
        /// </summary>
        internal const int MaxReplayDrainPerPump =
            MaxRpcBufferReplayEvents + RpcReplayBuffer.MaxPendingDuringReplay;

        // Monotonic clock for the drain budget and the budget precomputed in
        // Stopwatch ticks.  Stopwatch.Frequency is fixed per process, so the
        // tick budget is computed once; the delegate is cached to keep the
        // per-frame drain path allocation-free.
        private static readonly System.Func<long> ReplayDrainClock =
            System.Diagnostics.Stopwatch.GetTimestamp;
        private static readonly long ReplayDrainBudgetTicks =
            (long)(ReplayDrainBudgetMillis * System.Diagnostics.Stopwatch.Frequency / 1000.0);

        /// <summary>
        /// True while this client is waiting to be placed in a room by a
        /// request it issued — a join, a create that hands off to an auto-join,
        /// or a matchmaking request that ends in adoption.
        ///
        /// <para>Matchmaking is consulted separately because adoption bypasses
        /// <c>RoomManager</c>'s pending tables entirely
        /// (<c>EnterMatchmadeRoom</c> is driven from the matchmaking reply), so
        /// a signal drawn only from the room manager would drop a matchmade
        /// joiner's catch-up.</para>
        /// </summary>
        private bool IsRoomEntryOutstanding =>
            (_roomManager != null && _roomManager.IsRoomEntryOutstanding)
         || (_matchmakingManager != null && _matchmakingManager.IsMatchmaking);

        /// <summary>
        /// The room entry begun most recently, from the sequence both managers
        /// begin their entries on, for the catch-up frames staged while one is
        /// in flight.  The transition into a room presents the entry the
        /// answered request was begun with — <c>RoomManager.CompletingEntry</c>
        /// — and the buffer releases what was staged under it or a later
        /// entry and discards what was staged under an earlier one: an entry
        /// that ended without a transition began before the one completing.
        /// </summary>
        private int RoomEntryToken => _roomEntries.Latest;

        /// <summary>
        /// Handle an <c>RpcBufferReplay</c> (0x52) packet delivered immediately after joining a room.
        /// Decodes the binary replay buffer and dispatches each Enhanced RPC event as if it arrived live.
        /// </summary>
        /// <param name="payload">
        /// Binary payload: [event_count:2 LE u16][for each: [payload_len:2 LE u16][payload:N bytes]]
        /// </param>
        internal void HandleRpcBufferReplay(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                LogDebug("RpcBufferReplay: empty or truncated payload, skipped.");
                return;
            }

            // Game-data packets are valid only after a successful room join.
            // A replay frame that races ahead of the join reply belongs to this
            // joiner's catch-up stream, so — like the Spawn/Despawn catch-up —
            // stage it (a defensive copy, the receive buffer may be reused) for
            // ordered release on InRoom rather than dropping the buffered RPCs.
            // It shares the one ordered buffer so it is replayed after the spawns
            // its RPCs may target.  Nothing is dispatched while staged, so the
            // pre-room injection guard holds.
            if (_state != NetworkState.InRoom)
            {
                // Staging exists for exactly one race: this joiner's catch-up
                // overtaking the reply to a room entry it asked for.  The
                // gateway withholds the personal replay until it binds the
                // session to a room, and that binding answers a request this
                // client made — so with no entry outstanding there is no reply
                // for the frame to have overtaken, and it is a gateway that
                // spoke out of turn.
                //
                // Staging it anyway is worse than dropping it.  Nothing empties
                // the buffer until the next transition, so the frame would
                // survive to the next room entry and be released by the flush
                // there — spending that room's single catch-up admission ahead
                // of the genuine frame, which is then refused as a duplicate.
                // The player would receive another room's buffered RPCs and
                // none of their own.
                if (!IsRoomEntryOutstanding)
                {
                    if (ShouldWarn(ref _lastUnsolicitedReplayWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped an RpcBufferReplay that arrived with no room entry " +
                            $"outstanding (state={_state}). The gateway sends this frame only to a " +
                            "joiner; holding it would carry another room's catch-up into the next " +
                            "room this client enters.");
                    return;
                }

                if (IsDebugLogEnabled)
                    LogDebug($"RpcBufferReplay staged until InRoom (state={_state}).");
                WarnIfEarlyObjectEvicted(
                    _earlyObjectBuffer.Stage(EarlyPacketKind.RpcReplay, (byte[])payload.Clone(), RoomEntryToken));
                return;
            }

            // Behind the spawns the scene load holds: its events address those
            // objects.  Admitted when it is released, so this room entry's one
            // catch-up is spent on the frame that is delivered.
            if (HoldsObjectsForSceneLoad)
            {
                WarnIfEarlyObjectEvicted(
                    _earlyObjectBuffer.Stage(EarlyPacketKind.RpcReplay, (byte[])payload.Clone(), RoomEntryToken,
                                             keepOldest: true));
                return;
            }

            ushort eventCount = (ushort)(payload[0] | (payload[1] << 8));

            // Bound the main-thread work before anything is admitted, so a
            // frame rejected on its own header does not spend this room
            // entry's single catch-up on nothing.
            if (eventCount > MaxRpcBufferReplayEvents)
            {
                LogDebug(
                    $"RpcBufferReplay: event_count {eventCount} exceeds cap " +
                    $"{MaxRpcBufferReplayEvents}; rejecting frame to bound main-thread work.");
                return;
            }

            // Admit one catch-up per room entry and raise the ordering barrier
            // for it.  The gateway emits exactly one replay frame per join, so
            // a second one is a hostile or buggy retry — and re-dispatching it
            // would apply every AllBuffered RPC in the buffer a second time.
            if (!_rpcReplayBuffer.TryEnterDrain())
            {
                LogDebug("RpcBufferReplay: catch-up already delivered for this room entry, dropping frame.");
                return;
            }

            try
            {
                int offset = 2;

                // Decode each catch-up event and queue it in server-emitted
                // order.  Dispatch is deferred to DrainReplayQueue so the
                // historical events share one bounded, resumable drain with any
                // live RPCs that arrive during the window — both run in the
                // correct order (buffered before live) without freezing the
                // main thread when the buffer is large.  Per-event parse,
                // registry lookup, and audience checks happen at dispatch time
                // inside DispatchEnhancedRpcPayload.
                for (int i = 0; i < eventCount; i++)
                {
                    if (offset + 2 > payload.Length)
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: truncated at event {i}/{eventCount}, stopping decode.");
                        break;
                    }
                    ushort payloadLen = (ushort)(payload[offset] | (payload[offset + 1] << 8));
                    offset += 2;

                    if (offset + payloadLen > payload.Length)
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: event {i} payload truncated ({payloadLen} bytes), stopping decode.");
                        break;
                    }

                    var eventPayload = new byte[payloadLen];
                    Array.Copy(payload, offset, eventPayload, 0, payloadLen);
                    offset += payloadLen;

                    _rpcReplayBuffer.EnqueueBuffered(eventPayload);
                }

                // Dispatch as much as this frame's time budget allows.
                // DrainReplayQueue lowers the ordering barrier once the queues
                // are empty; otherwise the per-frame Update continuation
                // finishes the remainder in order.
                DrainReplayQueue();
            }
            finally
            {
                // On the normal path DrainReplayQueue has already lowered the
                // barrier if everything drained.  If an exception escaped the
                // decode, any events queued so far stay enqueued and the Update
                // continuation will deliver them — release the barrier only
                // when nothing is outstanding, so a thrown decode can never
                // strand it raised forever.
                if (!_rpcReplayBuffer.HasPendingWork)
                    _rpcReplayBuffer.ExitDrain();
            }
        }

        /// <summary>
        /// Drain queued catch-up RPCs — historical (buffered) first, then live
        /// (pending) — within this frame's wall-clock budget.  Invoked from
        /// <see cref="HandleRpcBufferReplay"/> when a replay frame arrives and
        /// again each frame from <c>Update</c> while work remains, so a large
        /// catch-up buffer is delivered in order across a few frames instead of
        /// stalling the main thread in one.
        /// </summary>
        private void DrainReplayQueue()
        {
            // The cached delegate is bound in Start(); the method-group fallback
            // only materialises before Start has run (e.g. an EditMode test that
            // invokes the receive path directly) and never allocates in
            // production, where Start always precedes the first inbound packet.
            //
            // The live calls deferred while the room's objects were still
            // arriving wait until none is: drained with the buffered history,
            // they would run ahead of the despawns, transfers and roster changes
            // still staged behind the history's frame.
            _rpcReplayBuffer.DrainBounded(
                _drainReplayDispatch ?? SafeDispatchReplayPayload,
                MaxReplayDrainPerPump,
                ReplayDrainBudgetTicks,
                ReplayDrainClock,
                includePending: !RoomObjectsStillArriving);
        }

        /// <summary>
        /// Dispatch one drained catch-up payload, isolating a throwing
        /// <c>[RtmpeRpc]</c> handler so a single bad event cannot abort the
        /// rest of the drain.  Bound once to <see cref="_drainReplayDispatch"/>
        /// so the per-frame drain pump stays allocation-free.
        /// </summary>
        private void SafeDispatchReplayPayload(byte[] payload)
        {
            try
            {
                DispatchEnhancedRpcPayload(payload);
            }
            catch (Exception ex)
            {
                LogDebug(
                    "RpcBufferReplay: drain dispatch threw: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle an inbound <c>RpcResponse</c> (0x51) from the server.
        /// Routes ownership grant responses to the OwnershipManager.
        /// </summary>
        private void OnRpcResponse(byte[] data)
        {
            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RPC response rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (!RpcPacketParser.TryParseResponse(payload, out var response))
            {
                LogDebug("RPC response: malformed payload, dropped.");
                return;
            }

            // Awaiters registered via SendEnhancedRpcAsync take precedence
            // over the per-method dispatch table: a response with a known
            // request_id corresponds to a server-targeted Enhanced RPC, and
            // the awaiter consumes the structured result regardless of
            // method id.  Returns false when no awaiter is bound, in which
            // case the method-id switch below handles legacy SDK-internal
            // flows (e.g. TransferOwnership grant frames).
            if (TryCompleteServerRpc(response))
                return;

            switch (response.MethodId)
            {
                case RpcMethodId.TransferOwnership:
                    HandleOwnershipTransferResponse(response);
                    break;
                default:
                    WarnIfUnclaimedRpcFailed(response);
                    break;
            }
        }

        /// <summary>
        /// A response no awaiter claimed and no handler here recognises. When it
        /// reports a failure, that report is the only account the caller will
        /// ever get: <c>NetworkBehaviour.RPC</c> is fire-and-forget, so nothing
        /// on the send side is waiting to be told, while the server answers
        /// every Server-targeted Enhanced RPC it accepts, asked to or not.
        /// </summary>
        /// <remarks>
        /// The reason travels with it, which a line naming the method id alone
        /// does not. Every code the wire can carry arrives through here — no
        /// such handler, not permitted, the handler threw, the payload was too
        /// large, and the parser's catch-all for a code outside the contract —
        /// and the first is the state a project sits in for as long as nothing
        /// binds a server handler for that id: the call runs on no node, and the
        /// send site cannot tell.
        ///
        /// <para>A success has to be agreed by BOTH fields. They are independent
        /// on the wire, so either alone can be made to say the wrong thing, and
        /// the reading that believes one of them is the reading a sender picks.
        /// An unrecognised code parses to the catch-all and is a failure here for
        /// the same reason.</para>
        ///
        /// <para>Rate-limited per reason, because the sender chooses how often
        /// this arrives: ungated, a broken or hostile server picks the log's
        /// throughput, and one shared budget would let the reason a deployment
        /// always returns hide every other one. Reported at warning level, which
        /// is the only level a project sees without changing a setting — this
        /// method deliberately consults no debug flag.</para>
        /// </remarks>
        private void WarnIfUnclaimedRpcFailed(RpcResponse response)
        {
            if (response.Success && response.ErrorCode == RpcErrorCode.OK)
            {
                LogUnclaimedRpcSuccess(response);
                return;
            }

            if (!_rpcFailureGates.ShouldWarn(response.ErrorCode))
                return;

            // One call-free declaration: after the gate this member only
            // composes and writes its sentence.
            string advice =
                response.ErrorCode == RpcErrorCode.UnknownMethod
                    ? " No server function handled that method — the project registers none"
                      + " (Project → Server functions in the portal), or the one it registers"
                      + " does not know it — so the call ran on no node."
                : response.ErrorCode == RpcErrorCode.Timeout
                    ? " The project's server function did not answer in time."
                : response.ErrorCode == RpcErrorCode.Unavailable
                    ? " The project's server function could not be called."
                : string.Empty;

            Debug.LogWarning(
                "[RTMPE] An Enhanced RPC failed and nothing was waiting for the answer: " +
                $"method_id {response.MethodId} returned {response.ErrorCode} " +
                $"(success flag {response.Success})." + advice);
        }

        // An unclaimed SUCCESS is an unremarkable legacy flow rather than a
        // fault, so it stays where it was: a debug line for whoever is already
        // reading them. Kept out of the method above so that one can be held to
        // consulting no debug flag at all — a warning re-gated behind
        // `enableDebugLogs` is this finding restored, and the gate is a field
        // that is false until somebody sets it.
        private void LogUnclaimedRpcSuccess(RpcResponse response)
        {
            if (IsDebugLogEnabled)
                LogDebug($"RPC response: unhandled method_id {response.MethodId}.");
        }

        /// <summary>
        /// Process a server-broadcast TransferOwnership RPC that tells this client
        /// to apply an ownership change (server-authoritative grant).
        /// Payload: [object_id:8 LE u64][new_owner_len:2 LE u16][new_owner:N UTF-8].
        /// </summary>
        /// <remarks>
        /// Authorisation policy applied here is defence-in-depth against a
        /// peer that captures and re-emits an authentic grant frame (the
        /// AEAD tag survives replay; the anti-replay window catches the
        /// exact-counter case but a peer in the same room can also forge a
        /// fresh-counter packet through a compromised gateway).  The client
        /// rejects the grant unless one of the following holds:
        ///   • the object's current owner is empty (initial assignment), or
        ///   • the wire-supplied <c>senderId</c> equals the local player's
        ///     gateway session ID and the local player currently owns the
        ///     object (we requested this transfer ourselves), or
        ///   • the new-owner string is a recognised member of the current
        ///     room roster AND the local player is the room master client
        ///     (host-authorised reassignment).
        /// The room-membership cross-check additionally guarantees the
        /// new-owner string is not arbitrary attacker-supplied bytes.
        /// </remarks>
        // Ownership-transfer RPC logic lives in RTMPE.Core.Rpc.OwnershipTransfer.
        // The four-path authorisation predicate is reviewable in isolation;
        // these instance methods are thin passthroughs onto that class.

        private void HandleOwnershipTransferRpc(RpcRequest request)
            => RTMPE.Core.Rpc.OwnershipTransfer.HandleRpc(
                request,
                _spawnManager,
                _localPlayerId,
                _localPlayerStringId,
                IsMasterClient,
                _roomManager);

        /// <summary>
        /// Test-visible passthrough onto
        /// <see cref="RTMPE.Core.Rpc.OwnershipTransfer.IsAuthorized"/>.  Existing
        /// fixtures (Tier0SecurityTests) call this through the NetworkManager
        /// instance; preserving the signature keeps the test surface stable.
        /// </summary>
        internal bool IsOwnershipTransferAuthorized(
            ulong objectId, string newOwner, ulong senderId)
            => RTMPE.Core.Rpc.OwnershipTransfer.IsAuthorized(
                objectId, newOwner, senderId,
                _spawnManager,
                _localPlayerId,
                _localPlayerStringId,
                IsMasterClient,
                _roomManager);

        private void HandleOwnershipTransferResponse(RpcResponse response)
            => RTMPE.Core.Rpc.OwnershipTransfer.HandleResponse(response, _spawnManager);

        /// <summary>RoomManager fires OnRoomCreated → transition to InRoom.</summary>
        private void OnRoomManagerCreated(RoomInfo room)
        {
            RememberRoom(room);
            RecordRoomEvent($"Room created: {room?.RoomId ?? "?"}");
            // Size the inbound flood budget to the room's capacity: peer-to-peer
            // fan-out scales with member count, so a fixed cap would drop
            // legitimate state in a large room.
            _inboundBudget.ConfigureForRoomSize(room.MaxPlayers);

            // Enter the room context so inbound state is accepted while the host
            // seat is established by the join that always follows a create — the
            // AutoJoinAsHost round-trip by default, or the caller's explicit
            // JoinRoom in the two-step flow.  The obsolete OnJoinedRoom signal is
            // left to that join, whose handler (OnRoomManagerJoined) is its single
            // source; raising it here as well would deliver two "joined" callbacks
            // for one room entry.  The modern split is OnRoomCreated (raised by
            // RoomManager) followed by OnRoomJoined.
            if (_state == NetworkState.Connected)
                TransitionTo(NetworkState.InRoom);
        }

        /// <summary>
        /// A manager refused or dropped a success reply for an entry this
        /// client had given up on.  The catch-up that ran ahead of that reply
        /// is what the buffer holds — staged under whatever entry was begun
        /// since, because a frame names no room — and it is that ended
        /// entry's room's: discarded here, ahead of the next entry's own
        /// catch-up, which the ordered channel delivers after this reply.
        /// Nothing to discard while in a room; the buffer is empty there.
        /// </summary>
        private void OnEndedEntryAnswered()
        {
            if (_state == NetworkState.InRoom) return;
            int discarded = _earlyObjectBuffer.Count + _heldVariableUpdates.Count + _heldRpcEvents.Count;
            _earlyObjectBuffer.Clear();
            _heldVariableUpdates.ClearState();
            _heldRpcEvents.ClearState();
            if (discarded > 0 && IsDebugLogEnabled)
                LogDebug($"Discarded {discarded} catch-up packet(s) that ran ahead of a late reply for a room entry this client had given up on.");
        }

        /// <summary>RoomManager fires OnRoomJoined → transition to InRoom.</summary>
        private void OnRoomManagerJoined(RoomInfo room)
        {
            RememberRoom(room);
            RecordRoomEvent($"Joined room: {room?.RoomId ?? "?"}");
            // Size the inbound flood budget to the room's capacity: peer-to-peer
            // fan-out scales with member count, so a fixed cap would drop
            // legitimate state in a large room.
            _inboundBudget.ConfigureForRoomSize(room.MaxPlayers);

            // The room's live objects follow this reply — staged ahead of it
            // and released by the transition below, or live behind it as the
            // room delivers them — so the spawner's catch-up window opens
            // here, on every applied join (the explicit join of a two-step
            // create included, which arrives with the state already InRoom):
            // for the reach of the replay ladder a spawn is admitted on the
            // count cap alone, and the rate cap, which discards what it
            // refuses, does not meet a set that cannot be asked for again.
            _spawnManager?.BeginRoomCatchUp();

            // A room that already has a scene is about to be told to load it, and
            // a single-mode load destroys whatever is built before it ends.  The
            // scene manager decides whether to hold the room's objects HERE,
            // before the transition below releases the catch-up staged ahead of
            // this reply — its own join handler, which announces the load, runs
            // after this one (audit P3-E1).
            _sceneManager?.PrepareForEntry(room);

            // Drive the state machine when we are arriving at InRoom from a
            // not-yet-in-a-room state (Connected on first join; Reconnecting
            // → Connected → InRoom on a fresh connect).  When auto-rejoin
            // fires after a quick disconnect/reconnect the state may already
            // be InRoom by the time RoomManager.OnRoomJoined is raised; the
            // transition itself is a no-op in that case, but the public
            // OnJoinedRoom event MUST still fire so application code that
            // gates spawn / UI work on it observes the rejoin.  Firing the
            // event unconditionally on InRoom arrival makes the contract
            // independent of how the state machine got us here.
            if (_state == NetworkState.Connected)
            {
                TransitionTo(NetworkState.InRoom);
            }

            if (_state == NetworkState.InRoom)
            {
#pragma warning disable CS0618
                SafeRaise(OnJoinedRoom, 0UL, nameof(OnJoinedRoom));
#pragma warning restore CS0618
            }
        }

        /// <summary>RoomManager fires OnRoomLeft → transition back to Connected.</summary>
        private void OnRoomManagerLeft()
        {
            RecordRoomEvent("Left room");
            // Explicit leave = user wants out of this room; clear the
            // last-room snapshot so a subsequent Reconnect() does NOT auto-rejoin.
            _lastRoomId   = null;
            _lastRoomCode = null;

            // The seat goes with the room.  This id names a place on the roster
            // of the room being left, and it is the value every ownership
            // comparison in the SDK is made against — NetworkBehaviour captures
            // it at spawn, ObjectLifecycleAuthority compares a Spawn's owner
            // claim against it, and NetworkVariable asks IsOwner before it will
            // write.  Kept, it answers those questions for the next room with
            // the previous room's seat: an object spawned between rooms, or in a
            // room whose entry reply names no seat, claims an identity the
            // gateway refuses and reaches nobody.  Until now it was cleared only
            // when the transport session ended.
            //
            // Cleared before the teardown below, matching RoomManager's own
            // ordering: a listener that consults it during OnRoomLeft sees a
            // coherent "between rooms" state rather than a seat in a room it has
            // already left.  Objects already spawned are unaffected — they hold
            // the id they captured — and they are destroyed on this stack frame
            // in any case.
            _localPlayerStringId = null;
            // Peer fan-out ceases on leave; restore the pre-room flood budget.
            _inboundBudget.ResetToDefault();
            if (_state == NetworkState.InRoom)
            {
                // Room leave keeps the transport session, so preserve the
                // per-session object-id counter: a rejoin must not re-issue ids
                // the room still holds under a despawn tombstone.
                _spawnManager?.ClearAll(resetObjectIdSpace: false);

                // Catch-up delivery spans several frames under a wall-clock
                // budget, and the drain pump is driven from Update with no
                // room gate of its own.  Anything still queued belongs to the
                // room being left — historical events from its replay frame
                // and live RPCs deferred behind them — so it is dropped here,
                // alongside the spawn registry it was going to address.
                // Left in place it would keep dispatching after the leave, and
                // into the next room on a switch.
                _rpcReplayBuffer.Clear();

                // The transition announces a live session, and a handler on it
                // may begin another attempt before the next line runs — so the
                // question is which session this notice would reach.  Raised
                // into a fresh attempt it names a room that attempt was never
                // in, which is not a late notice but a wrong one.
                //
                // ⛔ Asked about the attempt only, deliberately not about the
                // state.  A handler that merely disconnects leaves the leave
                // itself true, and this raise is the sole one in the SDK: the
                // event's whole contract is that the local player left a room,
                // so withholding it because the session ended afterwards turns
                // a documented notice into silence for an application whose
                // room teardown hangs off it.  A late notice is visible to the
                // integrator; a swallowed one is not.
                int epoch = System.Threading.Volatile.Read(ref _connectionAttemptEpoch);
                TransitionTo(NetworkState.Connected);
                if (!AttemptIsStillLive(epoch)) return;
#pragma warning disable CS0618
                SafeRaise(OnLeftRoom, 0UL, nameof(OnLeftRoom));
#pragma warning restore CS0618
            }
        }

        /// <summary>
        /// Remember the currently-joined room so <see cref="Reconnect"/> can
        /// auto-rejoin it after a token-preserving disconnect.  A null or
        /// empty room argument clears the snapshot (defensive — the room
        /// parsers already return empty strings rather than null IDs).
        /// </summary>
        private void RememberRoom(RoomInfo room)
        {
            if (room == null || string.IsNullOrEmpty(room.RoomId))
            {
                _lastRoomId   = null;
                _lastRoomCode = null;
                return;
            }
            _lastRoomId   = room.RoomId;
            _lastRoomCode = room.RoomCode;
        }

        private void OnServerDisconnect(byte[] data)
        {
            // Reject Disconnect packets that arrive before SessionAck has
            // promoted the session to "established".  During key derivation
            // the receive path is already accepting AEAD-decrypted frames
            // (the session keys exist), but the application-visible session
            // is not yet live; tearing down now would let a forged or
            // mistimed Disconnect interrupt an in-progress handshake and
            // strand the client in Disconnecting/Disconnected with no
            // session to recover.  Leave the in-flight handshake undisturbed.
            if (!_sessionEstablished)
            {
                // ⛔ Gated, and the reason is that the REFUSAL is unconditional
                // while the LINE is not.  The frame is AEAD-authenticated, so
                // only the gateway can send it — but it can send it as often as
                // it likes during the handshake window, and this branch does not
                // end the attempt, so every one of them wrote a console line.
                if (ShouldWarn(ref _lastPreSessionDisconnectWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Ignoring Disconnect received before session establishment — " +
                        "handshake is still in progress; will not tear down session keys.");
                return;
            }
            // Wire format: optional 1-byte reason discriminator at payload[0].
            // Empty payload = legacy gateway → fall back to ServerRequest so
            // old gateways continue to work unchanged.
            var payload = PacketParser.ExtractPayload(data);
            DisconnectReason reason = payload.Length >= 1
                ? DisconnectSignal.FromWire(payload[0])
                : DisconnectReason.ServerRequest;

            _networkThread?.Stop();
            // S4-54 — a reason that speaks about the SERVER's own lifecycle
            // leaves the resumption credential alive; one that speaks about THIS
            // CLIENT does not.  The gateway mirrors every reconnect entry into
            // Redis and re-hydrates the survivors at startup, and its shutdown
            // path deletes none of them — so a graceful shutdown or reload is the
            // one disconnect where a resume is most likely to work, and it was
            // the one where the SDK threw the token away.  `Reconnect()` then
            // refused outright ("no reconnect token"), costing the player their
            // session identity and the room the snapshot beside it could have
            // rejoined, in favour of a fresh authenticated handshake.
            //
            // ⛔ Keeping the token is not deciding it is valid.  The gateway is
            // the authority and invalidates the ones it wants dead — the API-key
            // revocation sweep reaches exactly these entries — so presenting a
            // token the server has retired costs one refused attempt, which S4-19
            // already bounds, against a full re-authentication in the common case.
            ClearSessionData(preserveReconnectToken: DisconnectSignal.TokenSurvives(reason));
            TransitionTo(NetworkState.Disconnected, reason);
        }

        // ── Transport error path ───────────────────────────────────────────────

        // Set by the network thread when the dispatcher could not take the
        // teardown, consumed by Update on the main thread.  Interlocked on both
        // sides: the write happens off the main thread and the read must not be
        // hoisted out of the frame loop.
        private int    _transportErrorTeardownPending;
        private string _transportErrorTeardownMessage;
        private int    _transportErrorTeardownEpoch;

        // Its own budget: a transport fault is not a packet fault, and a flood
        // of one must not decide whether the other is ever printed.
        private long _lastTransportErrorWarnTicks;

        // Its own budget for the same reason, one state earlier: a Disconnect
        // arriving before the session is established is refused on every
        // arrival, and the gateway chooses how many arrive.
        private long _lastPreSessionDisconnectWarnTicks;

        private void HandleTransportError(Exception ex)
        {
            // 🔑 A socket error is remotely drivable: an unreachable peer answers
            // a datagram with ICMP, which surfaces here as an exception per send
            // — so at tick rate this wrote one line per frame for as long as the
            // peer stayed down.  The TEARDOWN below is unconditional; only the
            // line is bounded.
            if (ShouldWarn(ref _lastTransportErrorWarnTicks))
                RtmpeLog.Error($"[RTMPE] Transport error: {ex.Message}");

            string message = ex.Message;
            // Stamped on the network thread, tested on the main thread, because
            // the teardown below cannot run before the next frame at the
            // earliest — see TearDownAfterTransportError.
            int    epoch   = Volatile.Read(ref _connectionAttemptEpoch);

            // The dispatcher refuses work exactly when it is saturated, which is
            // the state a failing transport tends to produce.  A dropped
            // teardown there would leave the session with no route back to
            // Disconnected — network thread running, no OnConnectionFailed, no
            // OnDisconnected — so the frame loop, which runs regardless of queue
            // depth, is the fallback.
            if (_dispatcher == null
             || !_dispatcher.TryEnqueue(() => TearDownAfterTransportError(message, epoch)))
            {
                Volatile.Write(ref _transportErrorTeardownMessage, message);
                Volatile.Write(ref _transportErrorTeardownEpoch, epoch);
                Interlocked.Exchange(ref _transportErrorTeardownPending, 1);
            }
        }

        /// <summary>
        /// Run the transport-error teardown on the main thread.  Reached either
        /// from the dispatcher or, when it could not take the work, from
        /// <c>Update</c>; the latch makes the two mutually exclusive.
        /// </summary>
        private void TearDownAfterTransportError(string message, int epoch)
        {
            // The dispatcher is a scene-level singleton that outlives this
            // component, so a teardown queued just before OnDestroy still runs
            // afterwards.  StopCoroutine on a destroyed MonoBehaviour throws,
            // and Cleanup has already released everything this method would
            // touch, so there is nothing left to do.
            if (_cleanedUp) return;

            // This teardown was raised on the network thread and reaches the
            // main thread a frame or more later, so the attempt it describes may
            // no longer be the live one.  It gets there two ways — the
            // dispatcher and the frame-loop fallback — and applications are told
            // to call Connect() from OnDisconnected, so the very first teardown
            // of an episode can put a fresh attempt in place before a second one
            // arrives.  Without this test the second stops the new attempt's
            // coroutines and network thread and reports its failure, and the
            // application sees a connect it just started fail for a fault that
            // happened before it existed.
            if (!AttemptIsStillLive(epoch)) return;

            bool wasConnecting = _state == NetworkState.Connecting;

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }
            if (_connectCoroutine != null)
            {
                StopCoroutine(_connectCoroutine);
                _connectCoroutine = null;
            }

            if (wasConnecting)
            {
                SafeRaise(OnConnectionFailed, message, nameof(OnConnectionFailed));
                // Asked again, because that handler is free to disconnect and
                // reconnect, and the rest of this teardown belongs to the
                // attempt this fault was raised for.
                if (!AttemptIsStillLive(epoch)) return;
            }

            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ConnectionLost);
        }

        /// <summary>
        /// Frame-loop half of the transport-error teardown.  Claims the latch
        /// atomically so a teardown that also reaches the dispatcher runs once.
        /// </summary>
        private void PumpPendingTransportErrorTeardown()
        {
            if (Interlocked.Exchange(ref _transportErrorTeardownPending, 0) == 0) return;

            TearDownAfterTransportError(
                Volatile.Read(ref _transportErrorTeardownMessage) ?? "Transport error.",
                Volatile.Read(ref _transportErrorTeardownEpoch));
        }

        // ── State machine ──────────────────────────────────────────────────────

        private void TransitionTo(
            NetworkState   next,
            DisconnectReason reason = DisconnectReason.Unknown)
        {
            var prev = _state;
            if (prev == next) return;
            _transitionSerial++;

            // Clear the session-established witness BEFORE the state assignment
            // and event raise so observers (OnDisconnected callbacks) cannot
            // observe a "Disconnected with _sessionEstablished == true"
            // inconsistent snapshot.  Reconnecting is intentionally retained
            // because the existing session keys remain in use until the new
            // SessionAck either confirms the migration or replaces them.
            if (next == NetworkState.Disconnected || next == NetworkState.Disconnecting)
                _sessionEstablished = false;

            _state = next;
            if (IsDebugLogEnabled)
                LogDebug($"State: {prev} \u2192 {next}");
            SafeRaise(OnStateChanged, prev, next, nameof(OnStateChanged));

            switch (next)
            {
                case NetworkState.Connected:
                    SafeRaise(OnConnected, nameof(OnConnected));
                    break;

                case NetworkState.Disconnected when prev != NetworkState.Disconnected:
                    SafeRaise(OnDisconnected, reason, nameof(OnDisconnected));
                    break;
            }

            // Entering a room opens the game-data gate: release the lifecycle
            // packets staged ahead of the join reply — those staged since the
            // entry this transition completes was begun, which the buffer is
            // told first; what an earlier entry staged before it ended without
            // a transition is discarded by that same call — after
            // OnStateChanged so observers learn they are InRoom before the
            // catch-up objects surface.  Every other transition discards the
            // staging buffer so a stale object set can never bleed across a
            // room boundary or an abandoned join attempt.
            if (next == NetworkState.InRoom)
            {
                // Arm this entry's single catch-up before the staged packets
                // are released: a replay frame that raced ahead of the join
                // reply is delivered by the flush below, and the previous
                // room's admission would otherwise discard it.
                _rpcReplayBuffer.ResetForRoomEntry();
                int discarded = _earlyObjectBuffer.AdoptEntry(_roomManager.CompletingEntry);
                if (discarded > 0 && IsDebugLogEnabled)
                    LogDebug($"Discarded {discarded} catch-up packet(s) staged during a room entry that ended without one.");
                // The frames held for objects the catch-up is about to spawn
                // follow the same rule: an ended entry's are discarded here,
                // and the completing entry's wait for their spawns below.
                int heldDiscarded = _heldVariableUpdates.AdoptEntry(_roomManager.CompletingEntry)
                                  + _heldRpcEvents.AdoptEntry(_roomManager.CompletingEntry);
                if (heldDiscarded > 0 && IsDebugLogEnabled)
                    LogDebug($"Discarded {heldDiscarded} VariableUpdate frame(s) and RPC(s) held during a room entry that ended without one.");
                FlushEarlyObjectPackets();
            }
            else
            {
                _earlyObjectBuffer.Clear();
                _heldVariableUpdates.ClearState();
                _heldRpcEvents.ClearState();
            }
        }

    }
}
