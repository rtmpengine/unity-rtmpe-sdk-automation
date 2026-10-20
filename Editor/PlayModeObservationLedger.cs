// RTMPE SDK — Editor/PlayModeObservationLedger.cs
//
// What one play session established, decided from what the Editor observed —
// the rules behind the third writer of `network-runtime-checks.json`.
//
// 🔑 Pure: no Unity type and no event subscription lives here.  The observer
// (PlayModeRuntimeObserver) reads the running NetworkManager and hands this
// ledger facts; this ledger decides which of the five runtime checks those
// facts establish and in what words.  Kept apart so the mappings can be driven
// with fabricated facts in a shard that has no engine — and so the one place
// that decides "what counts as observed" is a class rather than a handler.
//
// ⚠️ HALF of each two-client check is all one client can see.  `shared-room`
// asks that EACH client's roster name the other; `both-players` that each
// client's spawn reached the other; `sync` that a value crossed AND a foreign
// write did not.  This client sees its own roster, its own registry and the
// values that reached it.  The ledger therefore records those checks with a
// `detail` that says exactly which half was seen — and the negative clause of
// `sync` is never recorded from here, because nothing on this client can hold
// a fact about the peer.
//
// ⛔ "Another player" is never an identity THIS client has held.  Every fresh
// session seats this client under a new player id, and the seat it held
// before a drop lingers in the room until the server evicts it — so a solo
// developer whose session dropped and started over reads, for a while, as a
// roster of two ids with one host, and their own earlier objects spawn back
// under the earlier id.  This is the START-OVER path — a fresh Connect() from
// a fresh port, which displaces nothing — and not the token resume, where the
// gateway evicts the stale seat itself ("the returning client would otherwise
// appear twice") before the rejoin.  The ledger keeps every identity it has
// been told this client holds and counts a row or an object under any of them
// as this client's own; a peer is an id outside that set.
//
// 🔑 And "held" reaches back past this play session.  The seat lingers on the
// server, not in the Editor: the Disconnect a session sends on Stop is one
// best-effort datagram, and a session that was already dropped when Stop was
// pressed sends none — so the seat the LAST play session held can still be on
// the roster when the next one joins the same room, which under matchmaking is
// the room it is matched into.  A ledger born knowing nothing would read that
// seat as a peer and its objects as a peer's.  The observer therefore hands a
// new ledger the identities earlier sessions of this Editor held
// (RememberedIdentities, kept in the Editor's session state), and the rule is
// the same rule: an id this Editor has been seated under is this client.  What
// it cannot reach is an Editor that has since restarted; a seat lingers for
// minutes and a restart takes about as long, so that residue is documented
// rather than closed.
//
// ⛔ Passes only.  A pass is never taken back within a session, and no failure
// is recorded from here: the events an observer could refute from — a failed
// connection attempt, a reconnect loop that ended — are raised for local
// configuration faults and cancelled loops as readily as for the wire's
// refusal, and a witness that cannot tell the two apart has no failure to
// report.  Absence is not failure: a check the session did not reach is left
// as it stood.  The buttons and the load harness record failures.
//
// ⛔ Nothing from a session that ran on a transport the project installed.
// Every check is defined against a deployed gateway — "the client reaches the
// gateway", a roster the server sent, a value the wire carried — and a
// transport handed in through NetworkManager.SetTransportFactory is the seam a
// play-mode test uses to complete a handshake in-process against nothing.
// Once told the session ran on one, the ledger keeps what it was told and
// answers no outcome at all: a record that said "connection passed against
// gateway.example" for a stub would be a true sentence about a false thing.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace RTMPE.Editor
{
    /// <summary>
    /// The phases of the connection the observer distinguishes.  A projection
    /// of the runtime's <c>NetworkState</c>: the three members the mappings
    /// read, and <see cref="Other"/> for every other state.
    /// </summary>
    internal enum ObservedPhase
    {
        /// <summary>Any state the mappings do not read.</summary>
        Other,

        /// <summary>Authenticated and connected; not in a room.</summary>
        Connected,

        /// <summary>Connected and inside a room.</summary>
        InRoom,

        /// <summary>Resuming a dropped session on its reconnect token.</summary>
        Reconnecting,
    }

    /// <summary>One player on a roster, as much of it as the mappings read.</summary>
    internal readonly struct ObservedPlayer
    {
        public ObservedPlayer(string id, bool isHost)
        {
            Id = id ?? string.Empty;
            IsHost = isHost;
        }

        /// <summary>The player's room-level id.</summary>
        public string Id { get; }

        /// <summary>Whether the roster marks this player host.</summary>
        public bool IsHost { get; }
    }

    /// <summary>
    /// Accumulates what one play session established, from the facts the
    /// observer hands it, and answers with the outcomes to record.
    /// </summary>
    internal sealed class PlayModeObservationLedger
    {
        // ⚠️ The five check ids, stated a third time (the engine and the Go
        // harness state them; the outcome word is RuntimeChecksFile's).  This
        // assembly cannot reference the engine, and the engine refuses a record
        // naming an id it does not know — WHOLE, with every outcome in it — so
        // each of these is held to the engine's vocabulary by
        // ThePlayModeObserverRecordsFromWhatItSawTests.
        public const string ConnectionId  = "connection";
        public const string SharedRoomId  = "shared-room";
        public const string BothPlayersId = "both-players";
        public const string SyncId        = "sync";
        public const string ReconnectId   = "reconnect";

        public const string Passed = RuntimeChecksFile.PassedWord;

        /// <summary>The five, in the order a first run meets them — the record's order.</summary>
        public static readonly IReadOnlyList<string> CheckIds =
            new[] { ConnectionId, SharedRoomId, BothPlayersId, SyncId, ReconnectId };

        // The sentence every two-client detail ends on, so a reader of the
        // record meets the same words whichever check they read.
        private const string ThisClientOnly = "observed on this client";

        // One slot per check, in CheckIds order: the detail of the pass, or
        // null for a check the session has not established.
        private readonly string[] _details = new string[5];

        // Every player id this client has been told it holds — across the
        // sessions of one play run, and the ones handed over at birth from
        // earlier play runs of this Editor.  See the header.
        private readonly HashSet<string> _identities = new HashSet<string>(StringComparer.Ordinal);

        // The reconnect check's memory: the room this client was in when the
        // session dropped, and whether the session has since resumed on its
        // token and not yet entered a room.  The first room it enters after
        // the resume is compared against the first.
        private string _lastRoomId;
        private string _roomBeforeDrop;
        private bool   _resumed;

        // Whether the session ran on a transport the project installed — a
        // latch, because the transport is the session's from its first fact
        // to its last.  See the header.
        private bool _throughCustomTransport;
        private bool _throughShapedLink;

        /// <summary>A ledger that knows no identity yet: the first play session.</summary>
        public PlayModeObservationLedger()
        {
        }

        /// <summary>
        /// A ledger that already knows <paramref name="identitiesHeldEarlier"/>
        /// — the player ids this Editor was seated under in earlier play
        /// sessions, whose seats may still linger on the server.  A row or an
        /// object under any of them is this client's own from the first fact.
        /// </summary>
        public PlayModeObservationLedger(IEnumerable<string> identitiesHeldEarlier)
        {
            if (identitiesHeldEarlier == null) return;
            foreach (string identity in identitiesHeldEarlier) Hold(identity);
        }

        /// <summary>
        /// The connection moved from <paramref name="previous"/> to
        /// <paramref name="next"/>.  <paramref name="gateway"/> is the host the
        /// settings name, quoted in the connection detail.
        /// </summary>
        public void StateChanged(ObservedPhase previous, ObservedPhase next, string gateway)
        {
            bool wasSession = IsSession(previous);
            bool isSession  = IsSession(next);

            // A session exists: a handshake completed, whatever came before it.
            // InRoom is a session too — the runtime reaches it from Connected
            // only, so this arm sees it first as Connected, but a loss FROM
            // InRoom is the loss the reconnect check captures below, and a
            // ledger whose first word of a session is InRoom (none the runtime
            // speaks today) records the connection rather than nothing.
            if (isSession && !wasSession)
            {
                Establish(
                    ConnectionId,
                    ThisClientOnly + ": the connection reached " + next + " against "
                    + (string.IsNullOrEmpty(gateway) ? "the configured gateway" : gateway)
                    + ", so a handshake completed and a session exists");
            }

            if (wasSession && !isSession)
            {
                // The session ended.  What the reconnect check needs is the room
                // this client held at that moment, and that is the last room
                // the observer polled: a drop is announced in the same call that
                // tears the session down, so no poll sees the torn-down room
                // ahead of this event.  ⛔ Captured here and not on entry to
                // Reconnecting — the runtime enters Reconnecting from
                // Disconnected, once per attempt, and by the second attempt the
                // poll has long seen no room at all.
                //
                // 🔑 The ONE place a resume is forgotten.  Every session begun
                // after this loss — a resume, or a Connect() that starts over —
                // begins from here, so a start-over needs no clear of its own:
                // a second clear at session start was a silent backup of this
                // one, and a maintainer removing "redundant" clears one at a
                // time saw green twice while a resume, then a start-over into
                // the room the resume had entered, read as a room come back to.
                _roomBeforeDrop = _lastRoomId;
                _resumed        = false;
            }

            if (previous == ObservedPhase.Reconnecting && isSession)
            {
                // Resumed on the token rather than by starting over: that is
                // what the Reconnecting state means — a session begun any other
                // way carries the clear the loss above made.  Half of the check;
                // the room has to be there to come back to as well, which the
                // first room the resumed session enters decides.
                _resumed = true;
            }
        }

        /// <summary>
        /// The room this client holds, as its own snapshot reads: null or empty
        /// <paramref name="roomId"/> for no room.  Called whenever the snapshot
        /// changes; a roster naming this client and somebody else, with one
        /// host, is this client's half of <c>shared-room</c>.
        /// </summary>
        public void RoomObserved(string roomId, string localPlayerId, IReadOnlyList<ObservedPlayer> players)
        {
            Hold(localPlayerId);

            string room = string.IsNullOrEmpty(roomId) ? null : roomId;
            _lastRoomId = room;
            if (room == null) return;

            // The FIRST room a resumed session enters is the resume's own
            // rejoin — the runtime re-enters the room it dropped from on its
            // way back, and a room entered later is the game's navigation, not
            // the resume's.  So the resume is spent here, whichever room this
            // is: back in the room the session dropped from, the room was still
            // there to come back to; anywhere else, it was not, and a later
            // join to it by a fresh JoinRoom is not a room come back to.  A
            // game that joins a room of its own from OnConnected, ahead of the
            // runtime's rejoin, spends the resume on that room and records no
            // reconnect — passes only, and the definition is the first room.
            if (_resumed)
            {
                _resumed = false;
                if (_roomBeforeDrop != null && string.Equals(room, _roomBeforeDrop, StringComparison.Ordinal))
                {
                    Establish(
                        ReconnectId,
                        ThisClientOnly + ": the session resumed on its reconnect token rather than "
                        + "starting over, and the room it was in before the drop was there to come "
                        + "back to");
                }
            }

            if (players == null) return;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            int hosts = 0;
            int others = 0;
            bool namesThisClient = false;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (string.IsNullOrEmpty(player.Id)) continue;
                if (!ids.Add(player.Id)) continue;
                if (player.IsHost) hosts++;
                if (_identities.Contains(player.Id)) namesThisClient = true;
                else others++;
            }

            // ⛔ This client and somebody who is not any identity of this
            // client's, and exactly one host — the whole of the definition this
            // side can see.  A roster of one is the control case, and so is a
            // roster of this client's own earlier seat: neither records anything.
            // No count in the words — see RemoteValuesDelivered.
            if (others >= 1 && hosts == 1 && namesThisClient)
            {
                Establish(
                    SharedRoomId,
                    ThisClientOnly + ": its roster named this client and at least one other player, "
                    + "with exactly one host; that the peer's roster names this client is not "
                    + "observed");
            }
        }

        /// <summary>
        /// What this client's spawn registry holds: the owner id of every live
        /// object.  One of this client's own and one of somebody else's is this
        /// client's half of <c>both-players</c>.
        /// </summary>
        public void ObjectsObserved(string localPlayerId, IReadOnlyList<string> ownerIds)
        {
            Hold(localPlayerId);
            if (ownerIds == null) return;

            int ownedByThisClient = 0;
            int ownedByOthers = 0;
            for (int i = 0; i < ownerIds.Count; i++)
            {
                string owner = ownerIds[i];
                if (string.IsNullOrEmpty(owner)) continue;
                if (_identities.Contains(owner)) ownedByThisClient++;
                else ownedByOthers++;
            }

            if (ownedByThisClient < 1 || ownedByOthers < 1) return;

            // No count in the words — see RemoteValuesDelivered: how many of
            // each the first qualifying poll happened to catch is timing.
            Establish(
                BothPlayersId,
                ThisClientOnly + ": its registry held a live object owned by this client and one "
                + "owned by another player, each under its owner; that this client's spawn reached "
                + "the peer is not observed");
        }

        /// <summary>
        /// The runtime's count of NetworkVariable values written by another
        /// player's copy and delivered to this client's copy.  Any at all is
        /// this client's half of <c>sync</c> — the positive half.  Variables
        /// only: transform state and RPC-carried state travel other paths and
        /// are not witnessed.
        /// </summary>
        public void RemoteValuesDelivered(long count)
        {
            if (count < 1) return;

            // No count in the words: the number a first observation happens to
            // catch varies from session to session, and a detail that varies is
            // a claim rewritten — and re-stamped — on every Play.  The same rule
            // keeps a count out of every detail this ledger writes.
            //
            // "Unchanged" is the definition's word and not this client's to
            // say: what the owner wrote is on the other side.
            Establish(
                SyncId,
                ThisClientOnly + ": NetworkVariable values written by another player's copy "
                + "reached this client's copy; that they arrived unchanged, the peer's side, and "
                + "the negative clause — that a write to somebody else's object did not cross — "
                + "are not observed");
        }

        /// <summary>
        /// The session runs on a transport the project installed, not the
        /// SDK's.  Nothing observed on it is attributed to a deployed gateway:
        /// from here on <see cref="Outcomes"/> is empty, whatever was or is
        /// established.
        /// </summary>
        public void TransportIsNotTheSdks()
        {
            _throughCustomTransport = true;
        }

        /// <summary>Whether <see cref="TransportIsNotTheSdks"/> was called.</summary>
        public bool ThroughCustomTransport => _throughCustomTransport;

        /// <summary>
        /// The session runs under the Editor's link simulator: the transport
        /// is the SDK's or the project's, but the link is one the bench shaped
        /// — delay, jitter, loss — and not the network the players will have.
        /// Nothing observed under it is attributed to anything: a check that
        /// failed may have failed the bench's link, and one that passed says
        /// nothing the unshaped link would not.  From here on
        /// <see cref="Outcomes"/> is empty.
        /// </summary>
        public void LinkIsShaped()
        {
            _throughShapedLink = true;
        }

        /// <summary>Whether <see cref="LinkIsShaped"/> was called.</summary>
        public bool ThroughShapedLink => _throughShapedLink;

        /// <summary>
        /// Whether the ledger still has use for a fact about
        /// <paramref name="checkId"/>: false once the check has passed, which
        /// is the observer's cue to stop measuring for it.
        /// </summary>
        public bool StillWants(string checkId)
        {
            int slot = SlotOf(checkId);
            return slot >= 0 && _details[slot] == null;
        }

        /// <summary>
        /// Every check this session established, in the record's order, and
        /// nothing for a check the session did not reach — and nothing at all
        /// for a session that ran on a transport the project installed.
        /// </summary>
        public IReadOnlyList<RuntimeChecksFile.Outcome> Outcomes()
        {
            var outcomes = new List<RuntimeChecksFile.Outcome>(CheckIds.Count);
            if (_throughCustomTransport || _throughShapedLink) return outcomes;

            for (int i = 0; i < CheckIds.Count; i++)
            {
                if (_details[i] == null) continue;
                outcomes.Add(new RuntimeChecksFile.Outcome(CheckIds[i], Passed, _details[i]));
            }

            return outcomes;
        }

        /// <summary>
        /// One line naming what was established — <c>connection passed, sync passed</c>
        /// — or <c>nothing</c>, for the console.  What <see cref="Outcomes"/>
        /// answers, in words: a session on a project's transport is <c>nothing</c>.
        /// </summary>
        public string Summary()
        {
            var parts = new List<string>(CheckIds.Count);
            if (_throughCustomTransport || _throughShapedLink) return "nothing";

            for (int i = 0; i < CheckIds.Count; i++)
            {
                if (_details[i] == null) continue;
                parts.Add(CheckIds[i] + " " + Passed);
            }

            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }

        private void Hold(string localPlayerId)
        {
            if (!string.IsNullOrEmpty(localPlayerId)) _identities.Add(localPlayerId);
        }

        private static bool IsSession(ObservedPhase phase)
            => phase == ObservedPhase.Connected || phase == ObservedPhase.InRoom;

        private static int SlotOf(string checkId)
        {
            for (int i = 0; i < CheckIds.Count; i++)
            {
                if (string.Equals(CheckIds[i], checkId, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        // A pass stands once made: the first detail is the one kept, because a
        // later observation of the same fact adds nothing a reader needs.
        private void Establish(string checkId, string detail)
        {
            int slot = SlotOf(checkId);
            if (_details[slot] != null) return;
            _details[slot] = detail;
        }
    }

    /// <summary>
    /// The player ids this Editor has been seated under, as the observer keeps
    /// them between play sessions: one string, the ids in the order they were
    /// held, oldest first, at most <see cref="Capacity"/> of them.  Pure, so
    /// the shape can be driven without the Editor's session state.
    /// </summary>
    internal static class RememberedIdentities
    {
        /// <summary>
        /// How many of the most recent seats are kept.  A seat lingers on the
        /// server for minutes and a play session takes one seat (a resume takes
        /// another), so the seats that can still be on a roster are the last
        /// few; the rest are dropped oldest first, and dropping one costs at
        /// most a ghost the next session reads as a peer.
        /// </summary>
        public const int Capacity = 32;

        /// <summary>
        /// The separator between ids — one no player id carries, ids being
        /// UUID strings.  An id that does carry it is not remembered rather
        /// than remembered as two.
        /// </summary>
        public const char Separator = ' ';

        /// <summary>
        /// The ids <paramref name="stored"/> holds, oldest first, each once;
        /// none for null, empty or unreadable text.
        /// </summary>
        public static IReadOnlyList<string> Parse(string stored)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(stored)) return ids;

            foreach (string part in stored.Split(Separator))
            {
                if (part.Length == 0 || ids.Contains(part)) continue;
                ids.Add(part);
            }

            return ids;
        }

        /// <summary>
        /// <paramref name="stored"/> with <paramref name="identity"/> as its
        /// newest id — moved there if it was held before — and the oldest
        /// dropped past <see cref="Capacity"/>.  An empty identity, or one that
        /// carries the separator, leaves the text as it was.
        /// </summary>
        public static string Remember(string stored, string identity)
        {
            if (string.IsNullOrEmpty(identity) || identity.IndexOf(Separator) >= 0) return stored ?? string.Empty;

            var ids = new List<string>(Parse(stored));
            ids.Remove(identity);
            ids.Add(identity);
            while (ids.Count > Capacity) ids.RemoveAt(0);

            return string.Join(Separator.ToString(), ids);
        }
    }
}
#endif
