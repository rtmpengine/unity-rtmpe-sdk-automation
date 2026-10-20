# Changelog

All notable changes to the RTMPE Unity SDK are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and versions
follow `MAJOR.MINOR.PATCH`.

---

## [Unreleased]

## [1.0.14] - 2026-10-19

### Changed

- **An object that spawns long after its variables' updates still receives their values.**
  Updates for an object not spawned here yet are kept per variable once they leave the
  ten-second hold, so a world handed to this client as the room's new host keeps the room's
  values instead of being begun again over them. `HeldVariableUpdateBytes` counts what is
  kept, up to a mebibyte, until the object spawns, despawns or the room changes.
- **What is sent while a room is being left stays in that room.** A call made from a despawn
  callback or an `OnRoomLeft` handler is not sent again into the next room.
- **A value written before a transfer or a leave goes ahead of it whatever its send rate.**
  Every changed value of the object is sent first, over as many datagrams as it takes.

## [1.0.13] - 2026-10-19

### Changed

- **A reconnect whose first packet or its answer is lost completes in seconds.** When the
  gateway issued the reconnect token to be claimed at the resumption's second round — every
  current gateway does — `Reconnect()` sends its `ReconnectInit` again at +0.5, +1, +2 and
  +4 s until a `Challenge` arrives, as a first connection sends its handshake again; the
  gateway answers a repeat with the `Challenge` it already sent. A token issued otherwise is
  sent once, and the reconnect loop's next attempt follows the connection timeout.
- **A key named on the launch line is the key used.** `ApiKeySource` consults
  `--rtmpe-api-key-file` and `--rtmpe-api-key` before the Setup Wizard's vault and before a
  key staged into a development build. A provider registered with `SetProvider` still comes
  first and `RTMPE_API_KEY` last, so a development build launched with
  `--rtmpe-api-key-file` connects with that file's key.
- **A pin directory that disappears is treated as unreadable.** When the directory
  `EncryptedFilePinStore` read earlier in the run is no longer there — an unmounted volume, a
  moved container — `TrustOnFirstUse` refuses the connection, as it does for a pin file it
  cannot read, and a write is refused instead of creating an empty directory in its place.
  A directory missing from the start of the run is a first run, as before, and so is one
  that exists again; each play session starts the record afresh, with or without a domain
  reload.
- **Verifying a `Challenge` is priced at what it costs on the device.** The client measures
  each verification and holds them to 50 ms of main-thread time per second sustained, with
  up to two seconds' worth after a quiet spell, and to 8 ms per frame, one verification per
  frame always allowed. The price lies between the desktop figure and one second's
  allowance, and one slow measurement at most doubles it, so a verification is always
  affordable within a second. A `Challenge` past either bound is dropped; a handshake, and
  a reconnect sent again as above, brings the gateway's again with its resends.
- **A `MainThreadDispatcher` destroyed under a live session is replaced.** When the application
  destroys the dispatcher — for example while clearing every persistent object — the
  `NetworkManager` creates a new one on its next frame and the session carries on; packets that
  arrived in between are dropped, as a full queue drops them.
- **Under `DispatcherFullPolicy.Throw`, a full queue drops a datagram instead of throwing on the
  network thread.** A transport fault reported while the queue is full still ends the session at
  once. A `NetworkThread.OnError` subscriber that throws is logged, and every other subscriber
  still hears the fault, once.
- **A `NetworkManager` replaced within one frame takes over.** After `Destroy(old)` and a new
  manager in the same frame, the new one becomes the instance on the next frame. A second manager
  beside one that stays is destroyed with the duplicate warning, as before, one frame later.
- **`RtmpeConnectionBootstrap` rebuilds the player a level change destroyed.** When a single-mode
  scene load the room asks for destroys the player object it spawned, it spawns a new one when the
  load ends. `LocalPlayer` is `null` once its object is destroyed — a real `null`, so `is null` and
  `?.` agree with `== null`.
- **One more new session when the server was turning the player away.** When every new session
  `RtmpeConnectionBootstrap` opens after a lost connection is refused by the server — at the
  project's player limit, for example, while the lost session still counts — it opens one more
  once the server has forgotten that session, and reports the client offline only if that one is
  refused too.
- **X25519 and Ed25519 run on fixed-size field arithmetic.** A connection's key agreement
  and signature check allocate about 10 KB instead of about 6 MB, take about 40 % less time,
  and the X25519 ladder's running time does not depend on the secret scalar.
- **The Editor holds the API key in one place: the vault the Setup Wizard writes.** An internal
  Editor key store that nothing called, and its in-package tests, are no longer in the package.
- **A value written before an ownership transfer or a room leave goes ahead of it.** The
  object's variables changed in that frame are sent before the transfer, and the frame's changed
  variables before the leave, even past a full replication share.
- **Many teleports in one frame are a few datagrams.** The extra copies of each teleport travel
  in batches of their own: forty teleports are six datagrams.
- **A room entry waits for the room's RPC history.** Live calls that arrive first are held
  until the history is applied, for up to two seconds, and then run in order after it.
- **Leaving a room ends what was sent for it.** Room frames not yet acknowledged are not sent
  again into the next room, and server calls still waiting on the room are cancelled at once.
- **Objects that outlive their owner stay in the room when it is left.** An object with
  `DestroyWithOwner` off, destroyed by a scene load right after `LeaveRoom`, is handed to the
  host instead of being despawned for everyone.
- **A late joiner of a room at its object limit receives every object.**
- **A `NetworkList` built before its object spawns arrives whole.** The owner sends the whole
  list at spawn and on becoming the owner, and a replica replaces a list it holds only with the
  owner's whole list.
- **When the replication share is full, the objects this client owns take turns.** Each is
  flushed in turn, so none waits behind another.
- **`NetworkTransformInterpolator` keeps history for its whole interpolation delay.** ⚠️ The
  delay is held between 0.067 and 0.5 s.
- **RPCs reach the room in the order they were sent.** Over UDP, with a current gateway, a lost
  RPC is sent again and the RPCs sent after it wait for it, for up to two seconds.
- **`RtmpeConnectionBootstrap` acts on what happened while it was disabled.** Enabled again,
  it resumes a reconnect still running, reports one that ended, spawns the player for a room
  entered meanwhile, and handles a departure or a lost connection it missed. Only the copy
  that acts does this, and a reconnect that lands back in its room counts as a success.

### Added

- **Frames too large for one datagram arrive in pieces.** On UDP, with a gateway that offers
  it, a server frame past one 1,200-byte datagram — a large room list, a room's RPC history, a
  room entry — is sent as sealed pieces and put back together before it is handled. With an
  older gateway, or over KCP or WebSocket, it arrives whole as before.
  `CapabilityFlags.Fragments` is always advertised.

## [1.0.12] - 2026-10-12

### Changed

- **A room whose properties pass the room-entry budget still tells a joiner its scene.**
  The entry document and the answer to a refused write carry the scene keys first and as
  many of the room's other properties as fit, beside the version. The SDK merges a cut
  answer to a refused write into the properties it holds, and loads a scene it carries.
- **A call on an object that does not admit this client is refused where it is made.** An
  object spawned owner-only (the `Spawn` default) takes RPCs from its owner alone, and the
  server drops anyone else's without an answer. The SDK refuses such a call before it
  leaves: `SendEnhancedRpc` logs a warning and `SendEnhancedRpcAsync` throws
  `InvalidOperationException` at the call.
- **The package's own test suites compile.** A project made testable as `Tests/README.md`
  describes compiles cleanly and keeps Play Mode available.
- **The troubleshooting guide's scene-load snippet keeps the room's world.** It despawns
  only the objects a load destroys, and leaves those kept across loads — the world among
  them — to the room.
- **A spawn from `OnRoomCreated` is reported.** It is made before the join that seats the
  creator is answered, so it reaches no other player; `Spawn` logs the warning it logs for
  any spawn made before this client has a seat.
- **Another player's spawn waits for its prefab's registration.** A spawn that names a
  prefab this client has not registered yet is held for 10 seconds — up to 32 per prefab and
  128 in all — and applied when `RegisterPrefab` or `LoadPrefabRegistry` registers the id. A
  spawn whose owner leaves meanwhile is applied under the player the room handed the owner's
  objects to: the host when the owner left, or the host promoted next when the owner was the
  host, and whoever the room handed them to after that. A spawn declared destroyed with its
  owner is not applied. A spawn still unclaimed after that is dropped with a warning.
- **A connection's end reports the lobby and list requests it cut short.** A `JoinLobby` or
  a `ListRooms` the connection's end left unanswered is reported through `OnLobbyError` — or
  `Rooms.OnRoomError` for `Rooms.ListRooms` — and so is the lobby the client was in, whose
  room list stops with the connection.
- **Scene loads made in one round trip are each announced.** `LoadScene("Arena")` followed
  at once by `LoadScene("HUD", NetworkSceneLoadMode.Additive)` tells every player to load
  both, in order, and a player who joins later loads Arena and then HUD — also when the
  second load is made from a handler of the first one's announcement. Loads are never
  merged, so 32 can wait behind an unanswered write, and the next one throws
  `InvalidOperationException`.
- **A connection's end reports the room entry it cut short.** A `JoinRoom`,
  `JoinRoomByCode` or `CreateRoom` the connection's end left unanswered — including one
  waiting for this client to leave its room first — is reported once through
  `Rooms.OnRoomError`, naming the call that was made; a creator's unconfirmed seat is
  reported with its room's id. Entries the SDK makes itself — the rejoin after a reconnect,
  and `RtmpeConnectionBootstrap`'s — are made again by the SDK and are not reported.
- **The server's notice that it released this client's seat answers the heartbeat it
  replaces.** A client the server heard nothing from for long enough to release its seat is
  told it left the room and stays connected. Such a silence is a long main-thread stall, or
  a suspension the app resumed without ending its session — `RtmpeConnectionBootstrap` ends
  one that reached the heartbeat's liveness grace.
- **`tickRate` set from code takes effect at the next `Connect`, and so does
  `maxLobbyStringBytes` for room messages.** Lobby room lists read `maxLobbyStringBytes` as
  each one arrives.
- **A development build does not ship a leftover development key it did not ask for.** A
  build that finds `Assets/StreamingAssets/rtmpe.devkey` and will not write it itself — the
  injection is off, or the vault holds no key — stops with an error, as a release build does.

### Added

- **`LobbyManager.LastRoomListSource` and `LobbyManager.LobbyRooms`.** A lobby's own updates
  are unfiltered and arrive through the same `OnRoomListUpdated` as the answer to a filtered
  `ListRooms`; `LastRoomListSource` says which one a list is, so a filtered browser can ignore
  the updates. `LobbyRooms` holds the joined lobby's list, which a query — of that lobby or
  another — leaves as it is. A list once handed out, through the event or either property,
  is never changed: the next one is a new list.

## [1.0.11] - 2026-10-10

### Changed

- **A room's creator is its first player.** Until the creator has joined a new room,
  nobody else can, so another player cannot become its host. A session holds at most
  three rooms it created and has not joined, a room its creator has not joined within five
  minutes is closed, and lobby lists do not offer a room until its creator is in it.
- **A room that is not public is joined with its code.** `JoinRoom` by id admits only
  the room's creator and a player whose seat it is holding; `ListRooms(publicOnly: false)`
  names private rooms without their codes. The SDK's automatic re-join and
  `RtmpeConnectionBootstrap` re-enter the room the client was in by its code, so a client
  whose session could not be resumed gets back into its own private room.
- **A kick lasts as long as the room.** A kicked player cannot join that room again,
  by id, by code or through matchmaking, for as long as it keeps its session — a
  reconnect keeps it. The server knows a player by its session, so a client that starts a
  fresh session is a new player to it.
- **A full replay buffer takes from the player who sent the most.** Every player's
  newest `AllBuffered` calls still reach a later joiner.
- **The handshake refuses a gateway that recorded other capabilities than this client
  sent.** A gateway that echoes them (`CapabilityFlags.ClientCapsEcho`) has its echo
  compared with what the client sent, and a difference ends the attempt; an older
  gateway is accepted as before.
- **Property writes go out one at a time.** A room or player property write made while
  the previous one is unanswered waits for that write's broadcast and goes out at the
  version it set, merged with any others made meanwhile — so
  `SetRoomProperty("Round", 3); LoadScene("Arena2")` now loads the scene.
- **A refused property write is sent again.** When the server answers a write it refused
  with its current properties, the client replaces its map with them, adopts their
  version and sends the refused write again. A client that missed one broadcast is no
  longer refused for the rest of the room. Writes made meanwhile are merged only while
  together they still make one write that can be sent; a write too large to send is
  refused to its caller whether or not one is in flight.
- **A room property the SDK cannot read is refused when it is written.** The server
  stores an `int` only as a whole number in range, a `float`, `Vector3` or `Color` only
  with finite components, and `bytes` only as base64.
- **An object's buffered calls end with the object.** `AllBuffered` calls on an object
  that has been despawned are no longer sent to later joiners.
- **A joiner receives every object the room holds.** The room keeps up to 8,192 objects
  for later joiners, and `maxSpawnsPerRoom` defaults to 8,192 — a `NetworkSettings` asset
  saved with the earlier default of 5,000 is brought to it.
- **An object the owner destroys with `Object.Destroy` is despawned for the room.** Any
  object this client owns that something other than the SDK destroys — the game's own
  `Object.Destroy`, a scene load — is despawned on every client and for later joiners.
- **An object's starting values reach the room.** The values an object is constructed
  with on the client that spawns it are sent on the first flush, so a value chosen by
  the owner (`IsOwner ? name : ""`) reaches every client.
- **A new owner sends the values it holds.** When this client becomes an object's owner,
  it sends every variable whose value it received, so the room and the previous owner
  agree with it. A variable update is sent once, so a value the new owner never received
  is replaced in the room by the one it holds: the room keeps the new owner's view.
- **An ownership transfer that reaches a client before its object waits for it.** The
  transfer is applied when the object is built, instead of being dropped.
- **A host's own ownership transfer is no longer reported as unanswered.**
- **A list that changes often is still refreshed whole.** The periodic full refresh of a
  `NetworkVariableList` is measured from the last refresh, not the last change.
- **Prediction without reconciliation sends no inputs.** With `reconcileOwnedObjects`
  off, the input buffer is no longer filled or sent.
- **A created room whose join the server refuses is left.** `OnRoomError` reports the
  refusal and `OnRoomLeft` follows, instead of the client appearing to be in a room
  without a seat.
- **A create whose reply is lost is asked again.** An unanswered `CreateRoom` is sent
  again at two and six seconds under the same request id.
- **A leave is not undone by a reconnect.** After `LeaveRoom`, a reconnect no longer
  rejoins the room that was left, even if the leave was not yet answered.
- **A seat the server grants for a room this client has left is released.**
- **A server call the room refuses before running it is answered.** A call over the
  session's request budget, or one the room could not check, completes with
  `RpcErrorCode.Unavailable`; a call from a client not in the room completes with
  `RpcErrorCode.Unauthorized` — instead of waiting out its timeout.
- **A scene with a long name or path completes its readiness round.** Any scene name
  the room can hold as its scene is accepted when clients report it loaded.
- **Lobby lists offer only rooms with a free seat.**
- **The diagnostics uplink holds a bounded amount of text.** Each captured log line and
  stack trace is cut to 2,048 characters as it is captured, after the local paths in it
  are shortened.
- **`diagnosticsMaxPacketsPerInterval` defaults to 2.** A batch then stays within the 50
  entries a second the server accepts from one session, and an asset saved with the
  earlier default of 4 is brought to 2. Errors released ahead of the interval draw on the
  same budget — at most 50 entries a second after a burst of 600 — and what it holds back
  is sent later.
- **Every replica missing its interpolator is named.** The advisory names each such
  object once, including several that arrive in the same frame.
- **The build-time checks name a `NetworkSettings` asset that could not be loaded.** A
  build with an unreadable asset beside readable ones is reported as not verified.
- **A release build that ships unpinned is told to keep `Strict`.** The build warning
  names `requireFirstUseProvisioned` for `TrustOnFirstUse` instead of offering it as an
  equal choice.
- **The `NetworkBehaviour` inspector shows the object's identity.** The Network Identity
  rows — network object id, owner and spawn state — are drawn from the component.
- **The Player Spawn Flow sample spawns the avatar from `Rooms.OnRoomJoined` only.**
- **The getting-started guide puts two players in one room.** Its `GameManager` enters a
  room through matchmaking, resolves the player prefab's id from the registry, and the
  guide shows how to remove a networked object with `Spawner.Despawn`.
- **The documentation describes the SDK as it behaves.** Among other corrections: what
  `TrustOnFirstUse` does with a configured pin, when session counters reset, which events
  call every subscriber, how fast objects are limited on both ends, the handshake
  failures that end with `Timeout`, that player properties start empty after an automatic
  re-join, and that a player who joins after a scene finished loading receives no
  all-loaded event. The API reference documents `EnhancedRpcVerifier`.
- **A reconnect proves it is fresh.** The SDK advertises `CapabilityFlags.ResumeProof`,
  and a server that supports it issues the reconnect token on that condition: the token
  is redeemed only when the reconnect answers the server's challenge for that attempt
  with a proof made with the previous session's key. A copy of the reconnect request
  replayed by someone else on the network no longer spends the token or takes the
  player's seat.
- **A busy server can ask the client to show it can be reached.** When a server opens no
  more handshakes for unverified addresses, it answers with a cookie
  (`PacketType.HandshakeCookie`), and the SDK sends its request again with the cookie
  (`PacketType.CookieHandshakeInit`); the handshake then continues as before.

## [1.0.10] - 2026-10-09

### Changed

- **The RPC reference says which members can reach an object.** An object spawned with the
  default `sharedAuthority: false` takes RPCs from its owner alone, and the server drops other
  members' calls without an answer. The RPC section, the `RpcCaller` table and the server-call
  section now say so.
- **A refused `Despawn` or `NetworkVariable` write names the owner as the one who hands the
  object over.** The message points to an RPC to the owner, or to the owner's
  `RequestOwnershipTransfer`.

## [1.0.9] - 2026-10-08

### Added

- **Declarative send rates.** `[NetworkVariable(SendRateHz = 10f)]` on the field or property
  that holds a variable now sets that variable's send rate, so a rate can be declared where
  the variable is declared instead of only assigned after it is constructed. The declaration
  is applied once per spawn, after `OnNetworkSpawn` has returned; setting `SendRateHz` on the
  instance still wins over it.

### Changed

- ⚠️ **`NetworkManager.Send` is main-thread only, and enforced.** Called from another
  thread it logs an error and sends nothing, and the SDK never seals a frame off the main
  thread. Queue a send from a worker thread with `MainThreadDispatcher.Enqueue`.
- **`OnReconnectFailed` reports every reconnect that ends without restoring the session**,
  including one whose session fails the SDK's validation (`DisconnectReason.Unknown`). It
  is not raised when the application ends the attempts with `Disconnect` or `Connect`.
- **Reconnect attempts follow the server's last word.** The lifetime the server states for
  a reconnect token is measured from the last authenticated frame it sent, so a session
  that outlives that lifetime still gets the configured number of reconnect attempts.
- **Hiding the `NetworkManager` pauses its attempt.** Deactivating its GameObject during a
  connect or reconnect pauses the attempt; activating it again resumes it with the time and
  backoff it had left.
- **The session token is judged by its own issue time.** Its `exp` and `nbf` claims are
  checked against its `iat`, so a device whose clock is wrong still connects.
- **`Disconnect()` after a dropped connection discards the reconnect token.** With no
  reconnect running, it forgets the session the SDK was keeping for `Reconnect()`.
- **`Send(data, reliable: true)` marks the frame reliable**, so the gateway acknowledges it
  once and the SDK stops resending it. `Compressed` and `AppSequence` flags set on a frame
  passed to `Send` are cleared.
- **A first-use server key is pinned once its session is established**, not when its
  challenge arrives. When the pin store cannot save it, the key is still kept for the rest
  of the process.
- **Leaving during the handshake tells the gateway** once it has opened a session, so that
  session is released at once rather than when it times out.
- **A socket error keeps the reconnect token**, as missed heartbeats do, so `Reconnect()`
  resumes the session, and a retry loop continues past an attempt that hit one. Only a first
  `Connect()` that hits one clears the token.
- **`OnReconnectFailed` reports the attempts actually made.** A retry loop that ended because
  its token was spent reported one attempt more than it made.
- **`--rtmpe-api-key` given with no value is reported** as a failed source, as an empty
  `--rtmpe-api-key-file` already was, instead of falling through to `RTMPE_API_KEY`.
- **The key file is read within a bound.** A `--rtmpe-api-key-file` holding more than 4,096
  bytes, or giving nothing to read within two seconds (a pipe nothing writes to), is
  reported as a failed source instead of holding the thread that connects.
- **A reconnect after the server closed the session is spread.** Its first attempt waits a
  random 0.1–1 s, so the clients a server restart disconnects do not all reconnect at once.
  A reconnect after any other ending still starts at once.
- **A changed server endpoint takes effect at the next attempt.** `serverHost`,
  `serverPort` and the socket buffer sizes set from code after the `NetworkManager` started
  are used by the next `Connect` or `Reconnect`, which also judges and pins the server's key
  for that endpoint.
- **A pin store that throws refuses the connection and says so.** The attempt ends at once
  with "Server not pinned — refusing handshake" naming the store's exception, instead of
  timing out as a socket that bound too late.
- **The connection-timeout line says whether the server's Challenge verified.** A Challenge
  that arrived and did not verify is reported as `ChallengeNotVerified`, which cannot tell a
  wrong pin from a frame that was not the gateway's; `ServerReplyNotFinalized` now means a
  verified Challenge.
- **Processes that share a device's pin file no longer lose each other's pins.** Saves and
  removals hold a lock file beside it, so an Editor and a player, or two players, can pin
  servers at the same time.
- **A state frame keeps every update after one it refuses.** An update refused for its
  value (a rotation that is not a rotation, a non-finite number) is skipped, and the rest
  of the frame is still applied.
- ⚠️ **`heartbeatIntervalMs` is at most 15 seconds.** A longer interval is used as 15 seconds,
  with a warning: the server releases the room seat of a session it has not heard from for 30
  seconds or more, and an idle client's heartbeats are all it hears.
- **The round trip is the last keep-alive's.** After a keep-alive had to be sent again, the
  round trip reported is the resend's, so one lost datagram no longer adds a whole interval
  to it or raises `OnRttSpikeDetected`.
- **The Basic Connection sample retries once per failure.** It schedules one retry, waits in
  real time, and leaves the SDK's own reconnect attempts to finish.
- **An empty `serverHost` is refused when connecting.** `Connect()` and `Reconnect()` log
  the problem and change no state, instead of staying in `Connecting`; the
  `NetworkManager` still starts, and a host set from code before connecting is used.
- **`OnDisconnected` carries the server's reason.** When the server ends the session, a
  `Disconnect()` called from code it runs on the way out, such as an `OnNetworkDespawn`,
  no longer replaces that reason with `ClientRequest`.
- **`OnConnected` is not raised for a connection already ended.** A handler of
  `OnStateChanged` that disconnects on the way to `Connected` is not then told
  `OnConnected`.
- **A full send buffer is waited out on every platform.** Where the operating system reports
  it as "would block" (Linux, Android, Windows) the connection used to end; the datagram is
  now sent again after a short wait, as on Apple platforms, and before anything queued
  behind it.
- **Brief send errors no longer end the connection.** A reset route, an interrupted call or
  an unreachable host costs only the datagram.
- **A reconnect chooses the server address for the network it is on.** After a change of
  network, the address with a route is chosen again on every connection attempt.
- **Stopping during a slow DNS lookup no longer stalls the caller.** `Disconnect()` while the
  server name is still being resolved returns at once.
- **Socket buffer settings are kept in range at run time.** `sendBufferBytes` is held to
  4096–4194304 bytes and `networkThreadBufferBytes` to at most 65536, also for assets that
  never pass through the Inspector.
- **A network change is noticed at the first missed heartbeat.** When the address this
  device sends from has changed (for example from Wi-Fi to cellular), the session ends with
  `DisconnectReason.ConnectionLost` and keeps its reconnect token, so a reconnect resumes it
  within seconds instead of after the liveness window.
- **IPv6-only networks reach an IPv4 server.** When the device has no IPv4 route, the
  transport uses the server's IPv6 address, or reaches its IPv4 address — a literal one
  included — through the network's NAT64 prefix.
- **A variable whose reliable message was given up on is sent again.** When a reliable
  message goes unacknowledged through every retransmission, every variable on the objects
  this client owns is sent again at the next flush, so a value written once does not stay
  stale at the other players.
- **`RtmpeConnectionBootstrap` issues one room entry at a time.** It enters no room while
  another entry is still unanswered, whoever started it, and `Restart()` is refused with
  `OnBootstrapFailed` until that entry has ended.
- **`RtmpeConnectionBootstrap` says why it stopped opening new sessions.** The report names
  how the last attempt ended.
- **`RtmpeConnectionBootstrap` waits out the player's previous seat.** A rejoin after a new
  session that the room refuses as full, within 70 seconds of the lost connection, is tried
  once more when the server has released the seat the lost connection held.
- **`RtmpeConnectionBootstrap` keeps waiting for `Restart()` across a drop.** After the
  player leaves a room, is removed from it, or is refused the room it asked for, a reconnect
  no longer enters a room on its own.
- **`RtmpeConnectionBootstrap` lets your own reconnect attempts run.** When the application
  has started `NetworkManager.Reconnect` after a drop, the component waits for how they end
  instead of opening a fresh session over them.
- **`RtmpeConnectionBootstrap` recovers on resume after a long suspension.** With Recover
  On Resume on, a session the app was away from for at least the heartbeat's liveness
  window is resumed at once, instead of after the heartbeat notices the silence.
- **`RtmpeConnectionBootstrap.Connect()` starts again.** It gives back the budget of fresh
  sessions, so a later drop is recovered, and enters a room once connected.
- **Entering Play mode without a domain reload starts clean.** A provider registered with
  `ApiKeySource.SetProvider` in the last play session is no longer consulted, and the
  `RtmpeCredentialProvider` the Setup Wizard now writes, like the Two Player Room sample,
  forgets the key supplied in the last play session.

## [1.0.8] - 2026-10-02

### Added

- **Host-only world objects.** When the server says which spawns the room's host sent
  (`CapabilityFlags.AttestedSpawnHost`), a world object is used only when the host spawned
  it; a world another player spawns is removed on every client running this version, and
  the room keeps the host's when the host runs this version too. `SpawnData.SpawnedByHost`,
  a `SpawnData` constructor that takes it, and `SpawnLifetimeFlags.SpawnedByHost` carry the
  server's word.
- **Per-player object limit.** The server limits how many of a room's objects one player
  may create; a spawn past it is reported through `OnSpawnRejected` with
  `SpawnRejectReason.PlayerAtObjectCeiling`. The room's host is not held to it.
- **`NetworkManager.DuplicateCopiesDroppedCount`.** Over UDP the server sends every message
  that happens once (a spawn, a despawn, an RPC, a reply) three times; the client applies
  the first copy and drops the others before decrypting them, and this counts what it
  dropped.
- **Additive scenes for late joiners.** `NetworkSceneManager.LoadScene` records the scenes
  the room has open (`ReservedPropertyKeys.SceneStack`), and a player who joins later is
  told each of them: the single-mode scene, then every additive scene over it.
- **Objects wait for the scene load.** While a client loads the room's scene in single
  mode, what the room sends about its objects is applied when it reports the load done
  (`NetworkSceneManager.ReportReady`), in the new scene and in the order it arrived; RPCs
  that arrive meanwhile run after the room's buffered events. `RtmpeConnectionBootstrap`
  spawns the local player then too.
- **Players' properties on entry.** Joining a room delivers the players' properties and
  property versions with the room, up to about 3 KB of them together, and this player's own
  version always.
- **Batched variable updates arrive batched.** Another player's batch of variable updates
  reaches this client as the one packet that player sent, where the server negotiates it
  (`CapabilityFlags.VariableBatchRelay`), instead of one packet per entry; its entries are
  applied in order, as single updates are.
- **The receive queue keeps room for control messages.** Under a flood, state frames and
  variable updates may fill at most 8000 of the queue's 10000 places; the rest stay for
  spawns, RPCs and room replies.
- **RPCs wait for their object.** An RPC that arrives before the object it addresses runs
  when the object spawns, including the `RpcTarget.AllBuffered` events a late joiner
  receives.
- **Teleports arrive at once for everyone.** `NetworkTransform.OwnerTeleportTo` sends the
  destination on the next tick, marked as a teleport where the server reads the mark
  (`CapabilityFlags.TransformTeleport`), and other players see the object arrive there
  instead of travelling there at their speed limit. One teleport per object per second is
  shown as one.
- **A tick's transforms travel together.** Where the server reads them as one packet
  (`CapabilityFlags.StateBatch`), the moving objects this client owns send the transforms
  they owe in a tick together, up to 21 to a packet (38 quantized), rather than one packet
  each, so a client moving many objects stays within what the server takes from one
  connection.

### Changed

- **Max Spawns Per Second** is counted for each player apart: one player's burst drops
  only that player's spawns. The room as a whole may spawn up to eight times the setting
  in a second.
- **Creating or joining a room from inside one leaves it first.** `CreateRoom`,
  `JoinRoom` and `JoinRoomByCode` called from inside another room send the leave, raise
  `OnRoomLeft` when the server confirms it, and then send the create or join. A room that
  cannot be left is reported through `OnRoomError`, and nothing else is sent.

## [1.0.7] - 2026-09-27

### Added

- **World objects.** `RtmpeWorldAuthority` and `RtmpeWorldSpawner` keep room state that
  no player owns and hand it to the next host. The **Shared World** sample shows both.
- **Server functions.** A call to an `RpcTarget.Server` method runs the project's server
  function, an HTTPS endpoint registered in the portal, and `SendEnhancedRpcAsync`
  returns its answer (`RpcResponse.TryReadResult`, `RpcErrorCode.Timeout`,
  `RpcErrorCode.Unavailable`).
- **RPC caller restrictions.** `[RtmpeRpc(…, Caller = …)]` limits who may call a method
  (`RpcCaller.Owner`, `RpcCaller.Host` or `RpcCaller.Server`), and a handler reads the
  caller through `CurrentRpcCaller` or `NetworkManager.CurrentRpcCallerFacts`. A method
  that declares a caller accepts calls only when the server vouches for the caller, and
  `NetworkManager.RpcCallerRefusedCount` counts the calls refused.
- **Late-join state.** A player who joins a room receives the current value of every
  `NetworkVariable`: updates for an object that has not spawned yet are held until it
  does, and every peer sends its state again one second after the join.
- **Link Simulator.** A Network Debugger panel that adds delay, jitter, loss and
  reordering to a Play session. `SimulatedLinkTransport` and `LinkConditions` apply the
  same conditions in tests.
- **Runtime checks.** The Editor records what it observes during Play — the connection,
  a shared room, both players, synchronised state and a reconnect — and the Network
  Readiness window shows the result.
- **Production step in the Setup Wizard.** Shows which API-key source a release build
  uses, and can write `Assets/RTMPE/RtmpeCredentialProvider.cs`.
- `NetworkVariableListBool`, `NetworkVariableListVector2` and
  `NetworkVariableListVector2Int`.
- Analyzer rule `RTMPE1030`: an `Instantiate` that creates a networked object without
  spawning it.
- Conversion Wizard: a `List<T>` field converts to a synchronised list, and
  **Applies on receivers** converts a method that applies state on every receiver into
  an RPC.
- `NetworkManager` readings, shown in the Network Debugger: `ReliableRetransmitsCount`,
  `ReliableRtoSeconds`, `ReliableSmoothedRttSeconds`,
  `EnhancedRpcDuplicatesReceivedCount`, `HeldVariableUpdateCount`,
  `StagedCatchUpPacketCount` and `SpawnsHeldForReturnCount`.
- A complete API reference, with revised guides, code documentation and Inspector
  tooltips.

### Changed

- ⚠️ A `NetworkVariableList` holds at most what one datagram carries, because a joining
  player receives it whole: 283 `int`s, 141 `Vector2Int`s or 94 `Vector3`s (a `string`
  list is bounded by bytes). `MaxCount` reports the limit, `Add` and `Insert` refuse past
  it, and `CanAdd` checks an element.
- ⚠️ `NetworkVariableList<T>` implements `IReadOnlyList<T>`, so `string.Join`,
  `Assert.Equal` and other `IEnumerable<T>` overloads read its elements.
- ⚠️ `Reconnect()` keeps the player id: other players see the player leave and return
  under the same id. A room whose last player dropped is kept for the reconnect token's
  lifetime, so that player can return to it.
- ⚠️ The automation kit's `make` commands read `APPLY` and `REPLICA_APPLY` from the
  command line only; set in the environment, they stop with an error.
- `NetworkVariableBase.SerializeWithId` returns whether it wrote an entry; a value that
  cannot be written stays dirty.

## [1.0.6] - 2026-09-18

### Added

- **Multiplayer runtime.** `NetworkManager` is the entry point for connecting, rooms,
  spawning, ownership, synchronised state and RPCs. Traffic travels over UDP, encrypted
  and authenticated: the API key is sealed to the server's public key, and the server's
  identity is pinned.
- **Rooms, lobbies and matchmaking.** Create a room or join one by id or code, list public
  rooms, browse lobbies, or let matchmaking seat players together. Room and player
  properties, host transfer and kicks.
- **Networked objects.** Spawn registered prefabs, transfer ownership, keep an object when
  its owner leaves, and pool frequently spawned objects with `INetworkObjectPool`.
- **State synchronisation.** `NetworkVariable` scalar and list types with per-variable
  send rates; `NetworkTransform` with interpolation and optional client-side prediction;
  interest management.
- **RPCs.** `[RtmpeRpc]` methods with a choice of target, custom argument types
  (`INetworkSerializable`) and awaited calls (`SendEnhancedRpcAsync`).
- **Scenes.** Networked scene loading with a readiness barrier across the room.
- **Reconnection.** `Reconnect()` resumes a dropped session with its reconnect token and
  rejoins the last room.
- **Drop-in components.** `RtmpeConnectionBootstrap` connects, enters a room and spawns
  the local player; `RtmpeSceneLoader` loads the scene the room asks for.
- **API keys.** `ApiKeySource` resolves the key at run time. The Setup Wizard stores it in
  the OS credential vault for the Editor, and can stage it for development builds.
- **Editor tooling.** The Setup Wizard, the Network Prefabs and Network Scenes windows, the
  Network Debugger, the Network Readiness window and the Conversion Wizard.
- **Roslyn analyzers.** Twenty diagnostics, four with an IDE quick fix, documented in
  [`Documentation~/diagnostics.md`](Documentation~/diagnostics.md).
- **Automation.** A headless engine under `Automation~/` that scores a project's network
  readiness and converts single-player scripts.
- **Samples.** Basic Connection, Player Spawn Flow, Scene Transitions and Two Player Room.

### Licence

- [`LICENSE.md`](LICENSE.md) is a limited, service-linked licence: the SDK may be
  installed, read, modified for your own integration and shipped inside an application
  that connects to the RTMPE service; it may not be used to implement, host or operate a
  server that speaks the RTMPE wire protocol. Third-party components included in the
  package keep their own terms, listed in `LICENSE.md` §6.
