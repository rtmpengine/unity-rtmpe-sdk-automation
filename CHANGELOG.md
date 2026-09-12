# RTMPE SDK — Changelog

All notable changes to this package are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Version scheme: `MAJOR.MINOR.PATCH[-pre-release]`.

---

## [Unreleased]

## [1.0.5] - 2026-09-12

The conversion toolchain's unreplicated-motion advisory now reaches the author
it was written for. No runtime code changed and the wire is untouched.

### Fixed

- **"This type moves a transform and nothing replicates the movement" is said to
  a `NetworkBehaviour` too.** The advisory sat below the rule that claims every
  `NetworkBehaviour`, so it reached an author before they adopted the SDK and
  never after — the sentence vanished at exactly the step it had asked for.
  Proven with a minimal pair: two scripts with one identical body, differing only
  in base class. One predicate now, read by every rule that needs it: the type
  writes a transform, holds no replicated state and no RPC surface, and neither
  is nor requires a motion replicator. An owner guard and a lifecycle override buy
  no silence — they decide who moves and when, never whether the movement leaves
  the machine.

## [1.0.4] - 2026-09-11

A refusal that names the arm which converts.

⛔ **Why this is 1.0.4 and not a re-cut 1.0.3.**  `1.0.3` had already been sealed
and published to the release store when this landed.  Nothing had downloaded it —
the downloads counter carried no series for it — but a published version number
must name one set of bytes whether or not somebody happened to take them.

### Changed

- **A conversion the in-place arm refuses now says how to convert it anyway.**
  The tool has two arms: it can retype a field where that is safe, and where it
  is not it can declare a replicated *companion* beside it.  Every refusal the
  in-place arm produces has the same way out — and four of some twenty said so,
  in words (*"the companion arm is the safe shape"*) that named no syntax.  A
  field assigned in `Awake` is the case that matters: retyping it is a runtime
  `NullReferenceException`, the refusal said only that, and the arm that converts
  it was one colon away — `--member direction:networkedDirection`.  Every
  in-place refusal now ends with that sentence, carrying the member's own name.
  ⚠️ It promises exactly what the arm does: the companion is declared,
  constructed and seeded from the field, and **no bridge is written back** — the
  author still routes reads and writes.


## [1.0.3] - 2026-09-11

Repairs an audit found in 1.0.2's own tree.

⛔ **Why this is 1.0.3 and not a re-cut 1.0.2.**  The changes below landed in the
package tree after `1.0.2` had already been sealed and served, so for a few
hours one version number named two different sets of bytes.  A version that has
been delivered is not edited; it is superseded.

### Fixed

- **A matchmade client is sized by the room it was seated in.**  The matchmaking
  search packs players before spreading them, so a client asking for 8 is
  preferentially routed into the fullest room that will take it, whatever its
  size — and the reply carried the client's own request back to it.  At ~40
  peers in a 100-slot room the legitimate fan-out exceeded a budget built for 8
  and gameplay packets were discarded before decryption with nothing saying so.
  ⚠️ Needs a Room Service carrying the same change; an older server sends no
  capacity and is read exactly as before.

- **A JoinRoom reply now answers the request it was sent for.**  Three
  orderings, each of which a player could reach:
  - A reply for one room answered an outstanding request for another — the
    second room's retransmit ladder was disarmed, the client was seated in the
    first, and the room it actually asked for was never answered and could not
    time out.
  - A reply arriving after a completed `LeaveRoom` put the client back into the
    room it had just left, with no seat there and the menu gone.
  - A refusal arriving after the timeout was silent, while the timeout message
    states that the request is still outstanding and a late reply is still
    accepted.  It now reaches `OnRoomError` like any other refusal.

- **Diagnostics that an inbound packet can drive are rate-limited** at nine more
  sites — object teardown, the network thread's subscriber faults, an
  unauthorised ownership transfer, a handler fault, a refused legacy RPC, a
  frame that will not decompress, and an unconfigured replay window.  A remote
  peer could previously drive any of them at whatever rate it sent.

- **Four more of them, in the connection core**, where the rule that holds every
  other file could not see: a `Disconnect` arriving before the session is
  established is refused on every arrival and now says so at most once a second;
  a transport error — which an unreachable peer produces once per send, through
  ICMP — is bounded the same way; the outbound-nonce *near* exhaustion advisory
  no longer writes roughly a million lines over the ~9.7 h of warning it exists
  to give; and a packet the builder mis-sizes is refused per send and reported
  per second.  In each case the refusal itself is unchanged and unconditional;
  only the console line is bounded.

- **Three different header faults no longer share one budget.**  A malformed
  flags byte, an unknown packet type and a too-short header spent the same
  one-per-second allowance, so a flood of one decided whether the others were
  ever printed.  Each carries its own now; the two reports that are the same
  sentence still share one, deliberately.

- **The dropped-rented-packet warning uses the SDK's own rate gate** instead of
  a hand-written copy of it.  The copy had drifted: it lacked the clamp that
  re-opens the gate when a timestamp reads behind the last recorded one, so a
  clock step could hold it shut.

### Changed

- **The conversion tool names the type that exists.**  A field the automatic
  conversion cannot retype — a `List<T>`, say — was answered with advice to
  derive from `NetworkVariableBase` and implement an event, an ownership gate
  and both halves of the wire.  `NetworkVariableList<T>` ships in this package
  with four concrete types and two members to fill in, and the refusal now says
  so.

- **A `MonoBehaviour` that moves something is no longer classified as
  presentation.**  Where the visible movement lives on objects created at
  runtime rather than on the networked root, the readiness report used to file
  the mover as a HUD and score the project well with nothing replicated.  It is
  now reported as undetermined, with the two ways to settle it.


## [1.0.2] - 2026-09-11

A build that says what kind of build it is.

### Changed

- **`NetworkTransform` now requires `NetworkTransformInterpolator`.** A non-owner
  replica carrying the first and not the second does not stutter — it freezes,
  silently, while its replication traffic keeps arriving. Unity adds the
  interpolator when `NetworkTransform` is added and refuses to remove it while
  `NetworkTransform` remains, so the mistake is prevented rather than reported.
  ⚠️ Existing prefabs are unaffected until opened; the readiness report still
  names those.

- **A release build with no credential path says so while it is still
  cancellable.** Only one of a player's API-key sources is visible at build
  time — a provider registered in the project's own code; the launch line and
  the environment are chosen later, and a development build's injected key is by
  construction not in a release build. A release build whose project calls
  `ApiKeySource.SetProvider` nowhere now warns that the player will authenticate
  only if launched with an argument or an environment variable, neither of which
  a double-clicked application inherits. ⚠️ A warning and never a refusal: a
  launch-line deployment is a legitimate shape this check cannot tell from a
  mistake.

- **The banner states whether the build is a development build.**
  `NetworkManager.Awake` now writes
  `[RTMPE] SDK <version> — Unity <version>, <platform>, development build.` (or
  `release build.`). The key a build can carry is injected into a development
  build and read only by one, so a player reporting that it has no API key had
  two causes that produced the identical log — a project that never switched the
  injection on, and a release build behaving exactly as it must. The line now
  separates them without a round trip to whoever ran the build.


## [1.0.1] - 2026-09-10

Remote motion that renders what it was sent, and a development build that can
carry its own key. No API moved and the wire is untouched.

### Fixed
- **A remote replica no longer spins on the spot.** The render-side absorber
  spread a correction's position error over its window and wrote the facing
  straight through, so on the arming frame the body held still while the head
  turned the whole correction — measured at up to 89.7° on a frame whose body
  moved 0.0000 units. Both halves now decay on one arm and one window, including
  on a replica configured with `Sync Position` off, where the absorber had never
  armed at all.
- **Re-opening a `Sync Position` or `Sync Rotation` gate no longer slides the
  replica in from where it stood when the gate shut.** A pose this component did
  not write is no longer treated as one it did.
- **A switched-off `NetworkTransformInterpolator` is now reported.**
  `GetComponent` returns a disabled component, so the receive path found one,
  handed it the motion and nothing rendered it — with nothing in the console. The
  advisory now distinguishes the two faults and gives each its own remedy.
- The Network Prefabs window and the `NetworkTransform` inspector now count a
  switched-off `NetworkTransform` the way `make readiness` always has: one that
  is off today is a line of somebody's `OnNetworkSpawn` away from being on, so a
  prefab carrying one is still advised about its missing interpolator rather
  than passing in silence.

### Added
- **A key a development build can carry.** The Setup Wizard's vault is the
  Editor's, so a player built from a fully configured project still had no
  credential — and a double-clicked build inherits neither an argument vector nor
  your shell's environment, which left writing code as the only way to test a
  standalone build. the Setup Wizard's **Inject this key into development
  builds** switch has the build write `Assets/StreamingAssets/rtmpe.devkey` while it runs and remove it when it
  finishes, so a build made with *Development Build* ticked connects with no code
  and the project holds no credential between builds. ⛔ A release build never
  reads it, and a release build that finds one does not build.
⛔ Two entries that stood here have moved up: `RtmpeSdk.Version` and the fourth
sample both reached the archive `1.0.0` published, and its notes had been written
before they were committed. The `1.0.0` section above now says what that release
actually carried.

### Changed
- `NetworkObjectEditor` is no longer `sealed`, and its `OnEnable` is
  `protected virtual`, so the package's own inspectors can derive from it.

## [1.0.0] - 2026-09-10

The first release published under the RTMPE SDK licence, and the first one
offered as a finished product rather than as a generation of an internal
toolchain. There is no upgrade path described below because there is nothing
public to upgrade from: what follows is what the package contains.

### The package

- **A Unity multiplayer runtime over an encrypted UDP transport.**
  `NetworkManager` is the singleton every other surface hangs from; rooms,
  spawning, ownership, synchronised transforms, synchronised variables and RPC
  each have their own manager reached through it. The wire is ChaCha20-Poly1305
  over an X25519 handshake, and the API key is sealed to the gateway's static
  key rather than presented in the clear.

- **State replication with the failure modes written down.**
  `NetworkVariable` covers eight scalar types and four list types; a variable's
  send rate is a per-declaration annotation; `NetworkTransform` interpolates
  remote motion and reconciles the owner's own. Each ceiling is declared in one
  place and the few that are necessarily stated twice — the datagram size the
  builder and the transport must agree on — say so at the declaration and carry
  a drift guard. Refusing a value is documented wherever substituting one would
  move somebody else's object.

- **An entry flow that is a component, not a recipe.**
  `RtmpeConnectionBootstrap` connects, resolves the credential and enters a room
  without the integrator writing a state machine first. A player build resolves
  its own API key through `RTMPE.Core.ApiKeySource`; the Editor's credential
  vault sits one tier below whatever provider the integrator ships, so a
  developer's key never competes with a game's.

- **Four samples that build.** `BasicConnection`, `PlayerSpawnFlow`,
  `SceneTransitions` and `TwoPlayerRoom` compile against the shipped package and
  are held to it by the SDK's own suite — a sample that names a symbol the
  runtime does not declare fails a test rather than a customer's first hour.

- **`RTMPE.Core.RtmpeSdk.Version`, logged once per process.**
  `NetworkManager.Awake` writes `[RTMPE] SDK <version> — Unity <version>,
  <platform>.` unconditionally, because the integrator whose version cannot be
  established is precisely the one without verbose logging on.

- **Twenty Roslyn diagnostics, four of them with a one-click fix.** The
  analyzer ships as a loaded assembly under `Analyzers/`; every rule resolves to
  a section of [`Documentation~/diagnostics.md`](Documentation~/diagnostics.md),
  and that page names which four carry a fix and what the other sixteen ask you
  to decide.

- **A conversion toolchain under `Automation~/`.** A headless CLI that turns a
  single-player `MonoBehaviour` into a `NetworkBehaviour`, allocates and records
  wire identities in committed ledgers, and scores a project's network
  readiness. It sits in a folder Unity ignores, so it costs an import nothing
  and is built only by an integrator who wants it.

### Licence

- **This package is not MIT, and the history is dated rather than renumbered.**
  [`LICENSE.md`](LICENSE.md) is a limited, service-linked licence: the SDK may
  be installed, read, modified for your own integration and shipped inside an
  application that connects to the RTMPE service; it may not be used to
  implement, host or operate a server that speaks the RTMPE wire protocol.

  **Versions published between 2026-05-22 and 2026-09-03** carried the MIT
  licence, and a copy obtained under it remains governed by it on its own terms —
  whenever that copy was taken. A licence cannot be withdrawn from a copy already
  given, and this one is not being claimed back. Versions published
  **before 2026-05-22** were not MIT either — the package carried an express
  proprietary notice from 2026-04-29 and no licence file before that — and
  `LICENSE.md` §7 states that rather than sweeping them in. ⚠️ These boundaries
  are **dates, not version numbers**; this release's number says nothing about
  which terms govern a copy somebody already holds.

- **Third-party components keep their own terms.** Google FlatBuffers under
  `Runtime/Infrastructure/Serialization/FlatBuffers/` keeps its Apache 2.0
  licence and carries the modification notices Apache §4(b) requires; the
  poly1305-donna arithmetic in `Runtime/Crypto/Internal/` keeps its
  public-domain dedication. Both are named in `LICENSE.md` §6, which also states
  that it lists code taken from a third party rather than specifications this
  package implements independently.

- **There is no `licensesUrl` in the manifest.** It would point at a branch that
  is replaced on every release, and so would describe whichever licence is
  current rather than the one that governs the copy in your project. The terms
  that apply to a copy are the `LICENSE.md` shipped inside it.

- **The automation kit carries the licence too.** Published as a release asset
  it is part of the SDK, so it ships `LICENSE.md` at its root and its README
  says what governs it.

### Not in the package

- **The wire specification.** The protocol reference is internal. The runtime
  states the wire by implementing it — the opcode table lives in
  `NetworkConstants.cs` — so the format is derivable; what the licence forbids
  is acting on it to operate a server. What the package deliberately withholds
  is narrower than the format: the **key schedule, the AAD construction and the
  nonce layout**. Those are not properties of the product a subscriber buys.

- **A self-hosting path.** The SDK connects to the RTMPE service. There is no
  supported deployment of the gateway that is not the service itself.

---

## Before this release

This package existed for six months as an internal toolchain — 2026-03-07 to
2026-09-09 — and went through fifteen major lines before it was offered
publicly. That history is not
reproduced here: it records the construction of the thing above rather than any
change a reader of this file can act on, and every claim in it about an earlier
licence is superseded by the dated boundaries stated in **Licence**.

⚠️ **A `1.0.0` was released internally on 2026-04-17 and is not this release.**
It carried **no licence file at all** — the package's first `LICENSE.md`, an
express proprietary notice, was added on 2026-04-29, twelve days later — as well
as a different wire generation and a smaller surface. `LICENSE.md` §7 is the
authority on what governs a copy from that window. If you hold a copy dated before 2026-09-10, its own
`package.json` and `LICENSE.md` are the authority on what it is.

