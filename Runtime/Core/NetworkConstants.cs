// RTMPE SDK — Runtime/Core/NetworkConstants.cs
//
// Wire-protocol constants shared between:
//  • Rust gateway  — modules/gateway/src/packet/header.rs   (source of truth)
//  • Unity SDK     — this file                               (C# mirror)
//
// ⚠  SYNC RULE: Any change to PacketType values, flag bits, MAGIC, VERSION, or
//   HEADER_SIZE in the Rust gateway MUST be mirrored here immediately, and
//   vice versa. Mismatched values will cause silent protocol failures at runtime.
//
// Header wire layout (13 bytes, all little-endian):
//  [0..1]  magic       : u16  = 0x5254
//  [2]     version     : u8   = 5
//  [3]     packet_type : u8   (see PacketType enum)
//  [4]     flags       : u8   (see PacketFlags enum)
//  [5..8]  sequence    : u32  (monotonic, per-connection)
//  [9..12] payload_len : u32  (byte count of payload following header)

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Constants for the fixed header at the start of every packet.
    /// </summary>
    public static class PacketProtocol
    {
        /// <summary>
        /// The value of the first two bytes of every packet header:
        /// <c>0x5254</c>, written little-endian (<c>0x54</c>, then <c>0x52</c>).
        /// </summary>
        public const ushort MAGIC = 0x5254;

        /// <summary>
        /// The protocol version this SDK writes in every packet header. The server
        /// refuses a packet whose header carries a different version.
        /// </summary>
        public const byte VERSION = 5;

        /// <summary>
        /// Fixed size of every packet header in bytes (13).
        /// Layout: magic(2) + version(1) + type(1) + flags(1) + sequence(4) + payload_len(4).
        /// </summary>
        public const int HEADER_SIZE = 13;

        // ── Header field byte offsets ──────────────────────────────────────────
        internal const int OFFSET_MAGIC       = 0;   // 2 bytes LE
        internal const int OFFSET_VERSION     = 2;   // 1 byte
        internal const int OFFSET_TYPE        = 3;   // 1 byte
        internal const int OFFSET_FLAGS       = 4;   // 1 byte
        internal const int OFFSET_SEQUENCE    = 5;   // 4 bytes LE
        internal const int OFFSET_PAYLOAD_LEN = 9;   // 4 bytes LE

        // ── Flag-bit hygiene ───────────────────────────────────────────────────
        /// <summary>
        /// The flags the SDK accepts on a received packet header. A received
        /// header with any other bit set is refused.
        /// </summary>
        /// <remarks>
        /// <see cref="PacketFlags.SealedApiKey"/> is not included: only the client
        /// sends it.
        /// </remarks>
        public const byte KNOWN_FLAGS = (byte)(
              PacketFlags.Compressed
            | PacketFlags.Encrypted
            | PacketFlags.Reliable
            | PacketFlags.EnhancedRpc
            | PacketFlags.GameplayOrdered
            | PacketFlags.AppSequence);
    }

    /// <summary>
    /// The type of a packet, carried at offset 3 of its header.
    /// </summary>
    public enum PacketType : byte
    {
        // ── Legacy handshake (backward compatibility) ───────────────────────────
        Handshake         = 0x01,   // Client → Server: initial connection request
        HandshakeAck      = 0x02,   // Server → Client: handshake accepted

        // ── ECDH 4-step mutual authentication (production) ───────────────────────
        // Flow: HandshakeInit → Challenge → HandshakeResponse → SessionAck
        HandshakeInit     = 0x05,   // Client → Server, AEAD-sealed:
                                    // [api_key_len:2 LE][api_key:N][client_ephemeral_pub:32]
        Challenge         = 0x06,   // Server → Client: [ephemeral_pub:32][static_pub:32][ed25519_sig:64] = 128 B
        HandshakeResponse = 0x07,   // Client → Server: [client_pub_key:32]
        SessionAck        = 0x08,   // Server → Client: [crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:N][gateway_caps:4 LE?]

        // ── N-1: Reconnect flow ───────────────────────────────────────────────
        // Client presents a previously-issued reconnect token to resume a
        // session without a full API-key re-authentication.  The gateway responds
        // with a normal Challenge (0x06) and the standard 4-step ECDH flow
        // continues from there.
        //
        // ReconnectInit payload: [token_len:2 LE][token:N][proof:32 optional]
        //
        // MUST stay in sync with modules/gateway/src/packet/header.rs
        // (PacketType::ReconnectInit = 0x09, ReconnectAck = 0x0A).
        ReconnectInit     = 0x09,   // Client → Server: resume previous session via reconnect token
        ReconnectAck      = 0x0A,   // Reserved — gateway responds with Challenge (0x06), not this opcode
        // Server → Client: the gateway declined the handshake before a session
        // exists, so the client surfaces an actionable reason instead of waiting
        // out the connect timeout. Plaintext (no keys yet). Rate-limited refusals
        // stay silent — they are never answered with this opcode.
        // Payload: [code:1 u8][reason_len:2 LE u16][reason:reason_len UTF-8].
        // MUST stay in sync with modules/gateway/src/packet/header.rs
        // (PacketType::HandshakeError = 0x0B).
        HandshakeError    = 0x0B,   // Server → Client: handshake declined, with a reason code

        // ── Diagnostics uplink ────────────────────────────────────────────────
        // Client → Server: a batch of SDK diagnostic log lines (level + message +
        // optional stack + relative timestamp) giving live server-side visibility
        // into Unity-side errors during development. Best-effort, AEAD-encrypted,
        // gated OFF by default. Payload is raw length-prefixed binary.
        // MUST stay in sync with modules/gateway/src/packet/header.rs
        // (PacketType::Diagnostics = 0x0C).
        Diagnostics       = 0x0C,   // Client → Server: SDK diagnostic log batch

        // ── Keep-alive ────────────────────────────────────────────────────────
        Heartbeat         = 0x03,   // Client → Server: periodic keepalive
        HeartbeatAck      = 0x04,   // Server → Client: keepalive acknowledged

        // ── Generic data ──────────────────────────────────────────────────────
        Data              = 0x10,   // Client ↔ Server: arbitrary serialised payload
        DataAck           = 0x11,   // Server → Client: data acknowledged

        // ── Room lifecycle ────────────────────────────────────────────────────
        RoomCreate        = 0x20,   // Client → Server: create new room
        RoomJoin          = 0x21,   // Client → Server / Server → Client: join room / join ack
        RoomLeave         = 0x22,   // Client → Server: leave current room
        RoomList          = 0x23,   // Client → Server: request room list

        // ── Custom properties ─────────────────────────────────────────────────
        RoomPropertyUpdate   = 0x24,   // Client → Server → all: room-level property update (JSON payload)
        PlayerPropertyUpdate = 0x25,   // Client → Server → all: per-player property update (JSON payload)

        // ── Matchmaking (AutoJoinOrCreate) ───────────────────────────────────
        // Flow: Client sends MatchmakingRequest; server atomically finds an open
        //      waiting room matching (mode + lobby_name) within the caller's
        //      tenant or creates a new one, joins the player, and replies with
        //      MatchmakingResponse.
        //
        // MatchmakingRequest payload  (JSON):
        //  { "request_id": string, "mode": string, "lobby_name"?: string,
        //    "min_players"?: int, "max_players"?: int,
        //    "player_id": string, "display_name"?: string }
        //
        //  request_id is a client-generated correlation id — 32 lowercase hex
        //  characters, the same rendering the RoomCreate (0x20) trailer uses.
        //  The gateway keys an idempotency table on (session, request_id) and
        //  echoes it on every reply it builds, so a retransmit of this request
        //  is answered from the first attempt rather than matchmaking a second
        //  time.  It is read by the gateway alone — the Room Service decodes
        //  this document directly and declares no field for it, so the id it
        //  carries means nothing to the service that does the work.
        //
        //  The client sends no project_id, and one that arrived would be
        //  overwritten: tenancy is taken from the gateway-issued envelope, and
        //  the Room Service refuses the request outright when the envelope
        //  carries none — after decoding the payload, before acting on it.  The
        //  player_id above is likewise advisory — the server re-derives it from
        //  the session and echoes the value it decided on in the reply, which is
        //  the one the SDK adopts as its identity.
        //
        // MatchmakingResponse payload (JSON):
        //  { "ok": bool, "error"?: string, "request_id"?: string,
        //    "data"?: { "room_id": string, "room_code": string, "created": bool,
        //               "player_id": string,
        //               "players": [ { "player_id": string, "display_name": string,
        //                              "is_host": bool, "is_ready": bool } ],
        //               "properties_payload"?: string,
        //               "properties_complete"?: bool,
        //               "max_players"?: int } }
        //
        //  max_players is the capacity of the room the server SEATED the client
        //  in, which is not the capacity the request asked for.  The matchmaking
        //  search does not match on capacity — it only bounds by it — and it
        //  orders by occupancy descending, packing players into the fullest room
        //  that will take them, so a request for 8 is routed into a 100-slot
        //  room by design.  The SDK sizes its inbound flood budget from this
        //  figure; a client that sized it from its own request discarded
        //  legitimate gameplay packets before AEAD in any room bigger than it
        //  asked for (`ROOM-RD-05`).  Absent on a Room Service that predates it,
        //  which JsonUtility leaves at 0 and the SDK reads as "not carried",
        //  falling back to the request-derived figure.
        //
        //  player_id and players are not decoration.  The SDK adopts the
        //  server-derived id as its own and seats the roster in the same
        //  transaction, so a reply documented without them describes a join
        //  that completes without an identity or a roster.
        //
        //  properties_payload is the room's custom-property snapshot — a version
        //  counter and the property map — in the same canonical document the
        //  RoomJoin (0x21) response carries in its trailing block, delivered
        //  here as a STRING because this reply is bound by JsonUtility, which
        //  cannot bind a map.  PropertyJson decodes it.
        //  It carries the property VERSION, and a client entering an occupied
        //  room without it has every property write refused — the server accepts
        //  only its own version+1 and answers a conflict with no packet at all.
        //  properties_complete is false when the server dropped the map against
        //  its byte budget and kept the version alone.  The same document carries
        //  a players member — each seated player who has written a property,
        //  keyed by player id, with its version and its map — and a
        //  players_complete flag, false when the players' own budget did not
        //  hold them all whole; the joining player's entry is carried first, its
        //  version alone when its map does not fit.  An SDK that predates the
        //  member skips it.
        //
        //  request_id echoes the id the request carried, on the success, on the
        //  Room Service's refusal and on the gateway's own.  A reply naming a
        //  different one is discarded by the SDK without spending its latch —
        //  it answers an attempt already cancelled or timed out.  A reply naming
        //  none is matched against whatever is outstanding, which is what a
        //  gateway predating the member gets.
        //
        // MUST stay in sync with:
        //  modules/gateway/src/packet/header.rs  (PacketType::MatchmakingRequest = 0x26)
        //  modules/room/infrastructure/messaging/nats_matchmaking_handler.go
        MatchmakingRequest  = 0x26,   // Client → Server: AutoJoinOrCreate request
        MatchmakingResponse = 0x2B,   // Server → Client: matchmaking result

        // ── Lobby system (Phase 1.3) ─────────────────────────────────────────
        // Flow: LobbyJoin → server responds with current room list (JSON array).
        //      LobbyLeave is fire-and-forget; no server reply is expected.
        //      LobbyList requests a one-shot room listing with filters / sort.
        //      LobbyRoomListUpdate is a server-push update to subscribed clients.
        LobbyJoin           = 0x27,   // Client ↔ Server: enter lobby browser; the reply comes back under this same type
        LobbyLeave          = 0x28,   // Client → Server: exit lobby browser (fire-and-forget)
        LobbyList           = 0x29,   // Client ↔ Server: filtered room list request; the reply comes back under this same type
        LobbyRoomListUpdate = 0x2A,   // Server → Client: push update when lobby changes

        // ── Room management ───────────────────────────────────────────────────
        MasterClientChanged  = 0x2C,   // Server → All clients: master-client changed (auto or manual)
        MasterClientTransfer = 0x2D,   // Client → Server: request to transfer master-client role
        KickPlayer           = 0x2E,   // Client → Server / Server → All clients: kick request / broadcast
        SceneLoaded          = 0x2F,   // Client → Server / Server → All clients: scene-load readiness

        // ── Networked object lifecycle ────────────────────────────────────────
        Spawn             = 0x30,   // Server → Client: spawn networked object
        Despawn           = 0x31,   // Server → Client: remove networked object
        // Server → Client only: a Spawn the room would not accept.
        // [object_id:8 LE][reason:1]. The two reasons it carries are the two a
        // client cannot work out for itself — the object id already belongs to
        // another player, or the room is at its object ceiling — and both used
        // to leave the spawner holding an object no other player would ever
        // hear of. The gateway has no TryFrom arm for it, so it is an answer
        // this SDK receives and never sends.
        SpawnRejected     = 0x32,

        // ── State synchronisation ─────────────────────────────────────────────
        StateSync         = 0x40,   // Server → Client: authoritative full snapshot
        // ── Network variable delta synchronisation ───────────────────────
        // Payload: [object_id:8 LE][tick:4 LE][var_count:1][for each: [var_id:4 LE][value_len:2 LE][value bytes...]]
        VariableUpdate    = 0x41,   // Client → Server → all room clients: dirty variable delta
        // ── Interest Management (Feature #6) ─────────────────────────────
        // Payload: [x: float LE 4 B][y: float LE 4 B] — total 8 bytes.
        // Client → Server only; opts the session into zone-filtered delivery.
        // Clients that never send this packet receive every room-wide broadcast.
        // MUST stay in sync with PacketType::PositionUpdate = 0x42 in
        // modules/gateway/src/packet/header.rs
        PositionUpdate    = 0x42,   // Client → Server: 2-D world position for interest-zone filtering
        // ── Server-authoritative input batch (Phase 2.x — 2026-04-25) ─────
        // Carries a batch of <see cref="InputPayload"/> frames captured by
        // <see cref="NetworkBehaviour.GatherInput"/>.  The Sync Service consumes
        // these from `rtmpe.input.{room_id}` and applies them in
        // RoomTicker.tickRoom — the foundation for true server-side
        // simulation, lag compensation, and anti-cheat.
        //
       // Payload (raw binary, little-endian):
        //  [count: u16 LE][payload_1: 13 bytes]…[payload_N: 13 bytes]
        //
       // Per-frame layout matches InputPayload.WriteTo (13 B):
        //  [tick: u32 LE][move_x: f32 LE][move_y: f32 LE][flags: u8]
        //
       // Player identity is NOT carried in the payload — the Sync Service
        // resolves it from the gateway's NATS envelope (session_id →
        // authoritative player_id) so a client cannot stamp another
        // player's id on its own inputs.
        //
       // MUST stay in sync with PacketType::InputPayload = 0x43 in
        // modules/gateway/src/packet/header.rs
        InputPayload      = 0x43,   // Client → Server: server-authoritative input batch
        // ── Variable batch (multi-object coalesced delta) ────────────────
        // Payload: [count:1][count × {[entry_len:2 LE][entry:N]}] where each
        // entry is a legacy 0x41 VariableUpdate payload verbatim.  Reduces
        // per-packet wire overhead when many small deltas leave the same
        // sender in one tick.  Only emitted when
        // NetworkSettings.enableVariableBatching is true; gateways that do
        // not negotiate the new type drop unrecognised packets.
        // Server → Client too, to a session that negotiated
        // CapabilityFlags.VariableBatchRelay: another player's batch as the
        // entries the gateway admitted from it, read by VariableBatchFrames
        // and applied entry by entry as 0x41 (audit P2-H2).
        VariableBatchUpdate = 0x44, // Client → Server → room clients: coalesced variable batch
        // ── Rigidbody state (NetworkRigidbody / NetworkRigidbody2D) ──────
        // Payload: [object_id:8 LE][changed_mask:1] followed by the fields the
        // mask selects, in ascending bit order — the layout
        // PhysicsPacketBuilder.ComputePayloadSize derives its size from.
        //
        // Separate from StateSync (0x40) because the two layouts share
        // lengths: a rigidbody frame runs 9–63 bytes and a quantized transform
        // is 25 or 29, so a frame at those lengths satisfies both readings and
        // the byte that would decide between them means the object_id's high
        // half under one and a field mask under the other.  The type byte
        // states the grammar instead, so neither side has to infer it.
        //
        // MUST stay in sync with PacketType::PhysicsSync = 0x45 in
        // modules/gateway/src/packet/header.rs
        PhysicsSync       = 0x45,   // Client → Server: rigidbody state
        // ── Transform batch (audit P3-E3) ─────────────────────────────────
        // Payload: [count:1][count × {[len:1][record:len]}] where each record
        // is a StateSync (0x40) payload verbatim — 25, 29, 30, 48, 52 or 53
        // bytes — at most 64 of them, no object twice.  The transforms this
        // client's moving objects owe in one frame, sent as one datagram where
        // the gateway asserts CapabilityFlags.StateBatch; a gateway that does
        // not has no route for it.  Client → server only, so it is not in
        // PacketGates.IsKnownPacketType.
        //
        // MUST stay in sync with PacketType::StateSyncBatch = 0x46 in
        // modules/gateway/src/packet/header.rs
        StateSyncBatch    = 0x46,   // Client → Server: one tick's transforms
        // ── RPC system ────────────────────────────────────────────────────────
        Rpc               = 0x50,   // Client → Server: RPC request (method_id dispatch)
        RpcResponse       = 0x51,   // Server → Client: RPC response (or broadcast)
        /// <summary>
        /// Server to client: the room's buffered RPCs, replayed to a player who
        /// joins it.
        /// </summary>
        RpcBufferReplay   = 0x52,   // Server → Client: buffered RPC events for late joiners

        // ── Session termination ───────────────────────────────────────────────
        Disconnect        = 0xFF,   // Client-initiated graceful disconnect
    }

    /// <summary>
    /// The flags of a packet, carried at offset 4 of its header.
    /// </summary>
    [Flags]
    public enum PacketFlags : byte
    {
        None        = 0x00,
        Compressed  = 0x01,   // FLAG_COMPRESSED  — payload is LZ4-compressed
        Encrypted   = 0x02,   // FLAG_ENCRYPTED   — payload is ChaCha20-Poly1305 AEAD-encrypted
        Reliable    = 0x04,   // FLAG_RELIABLE    — packet requires KCP acknowledgement
        EnhancedRpc = 0x08,   // FLAG_ENHANCED_RPC — Rpc(0x50) payload uses 27-byte Enhanced RPC header
        GameplayOrdered = 0x10, // FLAG_GAMEPLAY_ORDERED — payload begins with a 4-byte gameplay sequence (LE u32) used to order RPC and StateSync against each other
        // The wire sequence field is the AEAD nonce counter once a session is
        // established, so the original application-level sequence is preserved
        // only inside the encrypted plaintext.  When this flag is set, the AAD
        // additionally binds a 4-byte LE u32 application sequence, allowing the
        // gateway and receiver to deduplicate or order packets without first
        // peeking at decrypted bytes.  Off by default; gateway must opt in.
        AppSequence = 0x20, // FLAG_APP_SEQUENCE — application-level monotonic sequence layered into AAD
        // HandshakeInit (0x05) only: the API-key field is sealed to the gateway's
        // static X25519 public key (anonymous sealed box).  MANDATORY — the
        // gateway refuses a HandshakeInit that leaves this bit clear, before it
        // reads the payload.  Never set on any other packet type.
        SealedApiKey = 0x40, // FLAG_SEALED_API_KEY — sealed-box API key in HandshakeInit
    }

    /// <summary>
    /// A version of the network-variable encoding, which the client states as its
    /// preference during the handshake.
    /// </summary>
    /// <remarks>
    /// The server uses the lower of the client's preference and the highest
    /// version it supports, and does not report its choice to the client. The SDK
    /// sends and reads network-variable updates the same way whichever version is
    /// chosen.
    /// </remarks>
    public enum WireFormatVersion : byte
    {
        V2 = 2,
        V4 = 4,
    }

    /// <summary>
    /// The network-variable encoding versions the handshake uses; see
    /// <see cref="WireFormatVersion"/>.
    /// </summary>
    public static class WireFormat
    {
        /// <summary>
        /// The version the SDK states as its preference in the handshake.
        /// </summary>
        public const WireFormatVersion Default = WireFormatVersion.V4;

        /// <summary>
        /// The version a session uses when the client states no preference.
        /// </summary>
        public const WireFormatVersion LegacyDefault = WireFormatVersion.V2;
    }

    /// <summary>
    /// The values a spawn carries for who may call the object's RPCs.
    /// </summary>
    /// <remarks>
    /// <see cref="SpawnManager.Spawn"/> sends <see cref="Shared"/> when its
    /// <c>sharedAuthority</c> argument is <see langword="true"/>, and
    /// <see cref="OwnerOnly"/> otherwise. A missing or unrecognised value means
    /// <see cref="OwnerOnly"/>.
    /// </remarks>
    public static class SpawnAuthorityFlags
    {
        /// <summary>
        /// Any member of the room may call the object's RPCs.
        /// </summary>
        public const byte Shared = 0x01;

        /// <summary>
        /// Only the object's owner may call its RPCs. Also the meaning of a
        /// missing or unrecognised value.
        /// </summary>
        public const byte OwnerOnly = 0x00;
    }

    /// <summary>
    /// The values a spawn carries for whether the object is destroyed when its
    /// owner leaves the room.
    /// </summary>
    /// <remarks>
    /// The SDK sends <see cref="DestroyWithOwner"/> when the object's
    /// <see cref="NetworkBehaviour.DestroyWithOwner"/> is <see langword="true"/> at
    /// spawn time, and <see cref="SurvivesOwner"/> otherwise. A missing or
    /// unrecognised value means <see cref="SurvivesOwner"/>.
    /// </remarks>
    public static class SpawnLifetimeFlags
    {
        /// <summary>
        /// Every client destroys the object when its owner leaves the room.
        /// </summary>
        public const byte DestroyWithOwner = 0x01;

        /// <summary>
        /// The object passes to the room's host when its owner leaves. Also the
        /// meaning of a missing or unrecognised value.
        /// </summary>
        public const byte SurvivesOwner = 0x00;

        /// <summary>
        /// The object passes to the room's host when its owner leaves, and the
        /// gateway saw the room's host send the spawn. Written by the gateway on
        /// the spawns it relays, never by a sender; must match
        /// <c>SPAWN_LIFETIME_HOST_ATTESTED</c> in the gateway's
        /// <c>nats/forwarder.rs</c>.
        /// </summary>
        /// <remarks>
        /// Read as <see cref="SurvivesOwner"/> by every reader that compares the
        /// byte with <see cref="DestroyWithOwner"/> alone, so a client or service
        /// that predates the value treats the object exactly as before. It is
        /// meaningful only when the gateway states it writes the byte
        /// (<see cref="RTMPE.Core.Protocol.CapabilityFlags.AttestedSpawnHost"/>);
        /// otherwise it is the sender's own claim.
        /// </remarks>
        public const byte SpawnedByHost = 0x80;
    }
}
