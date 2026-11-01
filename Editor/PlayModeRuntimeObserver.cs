// RTMPE SDK — Editor/PlayModeRuntimeObserver.cs
//
// The third writer of `network-runtime-checks.json`: the Editor watching a
// play session and recording what it saw, so the runtime result moves from
// events rather than from a button somebody presses afterwards.
//
// Shape: one static observer, armed by [InitializeOnLoad].  When play mode is
// entered it opens a ledger; on every editor update while playing it finds the
// live NetworkManager, subscribes once to the one transition event and to the
// room manager's seat and departure events, and polls the three facts that
// are states rather than events (the room snapshot, the spawn registry, the
// sync witness); when play mode is left it detaches BEFORE the teardown and
// writes the ledger beside the readiness artifact in the same call.
//
// 🔑 The seat events (`RoomManager.OnRoomJoined`, and `OnRoomCreated` for a
// creator who does not auto-join) are subscribed as well as polled, for one
// reason: the identity this client holds is learned from them.  A seat and a
// drop applied in ONE blocked frame — a breakpoint — leave no poll between
// them, and a ledger that learned identities from polls alone would meet the
// lingering seat with nothing to recognise it by.  `OnRoomLeft` is subscribed
// for the same frame: a leave and a drop applied between two polls would
// leave the ledger holding the left room as the room at the drop, and a
// resume that re-entered it would read as a room come back to.  The room
// manager is rebuilt on every connect, so the subscriptions follow the
// instance the manager holds, re-made whenever that reference changes.
//
// 🔑 And the identity is REMEMBERED where it is learned, in the Editor's
// session state, and handed to the next session's ledger at its birth.  The
// seat outlives the play session: the Disconnect sent on Stop is one
// best-effort datagram, a session already dropped at Stop sends none, and the
// server keeps the seat until its timeout — so the seat the last session held
// can be on the roster the next session joins, and a ledger born knowing
// nothing would count it a peer (ledger header).  Session state, not
// EditorPrefs: a second Editor on this machine playing the other player — a
// project clone, a virtual player — must not read this Editor's seats as its
// own, and EditorPrefs is one store for every Editor the user runs; session
// state is this Editor's alone.  What that leaves out is an Editor restarted
// within a seat's lifetime, documented as a limit.
//
// ⛔ Detached AND written at ExitingPlayMode, in that order.  After it the
// runtime tears the session down and announces transitions as it does, and
// nothing that happens after the developer pressed Stop is an observation
// about the game — so the ledger is closed first.  Written there rather than
// at EnteredEditMode because nothing may sit between the session's end and
// its record: a script edited while playing under "Recompile After Finished
// Playing" reloads the domain on the way out, and a ledger held in a static
// across that reload is a ledger lost with its session unrecorded.
//
// ⛔ Nothing from a session on a transport the project installed.  The fact
// is the runtime's (`NetworkManager.TransportIsCustom`, the transport the LIVE
// session was built on), read when a transition arrives and on every poll, and
// handed to the ledger as a latch; the ledger then answers no outcome and the
// console says why.  A play-mode test that completes a handshake through a
// stub records nothing, rather than "connection passed" against a host the
// wire never reached.  And nothing from a session under the Editor's link
// simulator (`NetworkManager.TransportIsShaped`, read at the same two
// places): the link was the bench's, so a check that failed may have failed
// the bench and one that passed says nothing the unshaped link would not.
//
// 🔑 Every decision about WHAT the facts establish is PlayModeObservationLedger's;
// this file only reads the runtime and hands the facts over.  Read that file's
// header for the three rules the record rests on: half of each two-client
// check is all one client sees, an identity this client has held is never
// "another player", and passes only.
//
// ⚠️ The record is written beside the artifact at the default path, under the
// name the artifact carries — the same rule as the window, and the file name
// is never stated here — and it YIELDS: a check another writer has already
// passed OR failed is left as that writer wrote it (RuntimeChecksFile.RecordMany),
// so the harness's whole observation, either way it went, and the developer's
// own word are never replaced by this Editor's half.
// With no artifact there is nowhere to write: the session's outcomes are
// reported to the console with the command that creates the artifact, once
// per Editor session, and dropped — with a caveat for an Editor playing from
// a project root whose Assets is a link (a Multiplayer Play Mode virtual
// player under the main project's Library/VP/, a project clone): as the
// second Editor of a two-player setup it should leave recording to the main
// Editor, because the scan command for its own root would write an orphaned
// second artifact and record there.  An artifact that is there and cannot be
// read is a fault, said on every play session.  After a write the console
// says what the record now stands at: written, already on file, or a file the
// next scan will refuse whole.
// A domain reload during play (a script recompile while playing)
// discards the ledger with the rest of this assembly's state; the observer
// then RESUMES on a fresh ledger from the reload — the static constructor
// finds the Editor playing and begins again — so the seat the game takes when
// it reconnects after the reload is remembered like any other, and what the
// remainder of the session establishes is recorded.  The facts before the
// reload are gone with the ledger that held them: passes only, so a remainder
// can add true rows and never a false one.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using RTMPE.Core;
using RTMPE.Rooms;
using UnityEditor;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Records the runtime checks a play session establishes, from what the
    /// Editor observes of the running <see cref="NetworkManager"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeRuntimeObserver
    {
        // The registry is walked at most this often: the fact it answers is a
        // state, not an event, and a walk per editor frame buys nothing a walk
        // four times a second does not.  The same cadence the debugger samples
        // its counters at.
        private const double RegistrySampleIntervalSeconds = 0.25;

        // Where this Editor keeps the player ids it has been seated under,
        // between play sessions — see the header, and RememberedIdentities for
        // the shape.
        private const string RememberedIdentitiesKey = "RTMPE.PlayModeObserver.IdentitiesHeld";

        // Whether this Editor session has already been told there is nowhere
        // to write.  A project that does not use the readiness kit connects on
        // every Play, and the advice to run the scan is worth one console line
        // per Editor session, not one per Play.
        private const string NowhereToWriteSaidKey = "RTMPE.PlayModeObserver.NowhereToWriteSaid";

        private static PlayModeObservationLedger s_ledger;
        private static bool s_observing;
        private static string s_lastRemembered;
        private static NetworkManager s_attached;
        private static RoomManager s_rooms;
        private static RoomInfo s_lastRoom;
        private static double s_lastRegistrySample;

        // Whether the observation running now was begun by a domain reload that
        // found the Editor already playing — the static constructor's Begin.  The
        // EnteredPlayMode that follows such a reload belongs to the same session,
        // and a second Begin there replaced the ledger with an empty one: a
        // connection completed in between was a row lost.  Per domain (a reload
        // resets it) and cleared at Stop, so a later session never inherits it.
        private static bool s_begunByReload;
        private static readonly List<NetworkBehaviour> s_registryScratch = new List<NetworkBehaviour>(64);
        private static readonly List<string> s_ownerScratch = new List<string>(64);
        private static readonly List<ObservedPlayer> s_rosterScratch = new List<ObservedPlayer>(16);

        static PlayModeRuntimeObserver()
        {
            // Runs on Editor load and after every domain reload — including the
            // one that precedes EnteredPlayMode, so the subscription is in place
            // before the session that needs it begins.  With domain reloading
            // disabled the statics survive instead and nothing here runs twice.
            // A reload that finds the Editor playing begins the observation
            // here, on a fresh ledger: for a reload DURING play that is the
            // whole of the resumption (no EnteredPlayMode follows it — header),
            // and whether the reload on the way INTO play mode already reports
            // the Editor playing is not relied on either way: if it does, the
            // EnteredPlayMode that follows keeps the ledger begun here rather
            // than beginning again — the ledger was not always empty by then,
            // and a connection completed in the gap was a row the second Begin
            // dropped.
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
            if (EditorApplication.isPlaying)
            {
                Begin();
                s_begunByReload = true;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    // The reload on the way in may already have begun this
                    // session; beginning again would drop what it has seen.
                    if (!s_begunByReload) Begin();
                    s_begunByReload = false;
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    Stop();
                    Publish();
                    break;
            }
        }

        private static void Begin()
        {
            // Let go of a manager a previous Begin attached, rather than drop
            // the reference: Begin runs from the static constructor as well as
            // from EnteredPlayMode, and a reference dropped with its handler
            // still on the manager's event would hold that handler twice once
            // the update re-attached.
            Detach();

            // Born knowing the seats earlier sessions of this Editor held, so a
            // seat still lingering from the last one is this client's.
            s_ledger = new PlayModeObservationLedger(
                RememberedIdentities.Parse(SessionState.GetString(RememberedIdentitiesKey, string.Empty)));
            s_observing = true;
            s_lastRoom = null;
            s_lastRegistrySample = 0d;
            s_lastRemembered = null;
        }

        // The end of observation, ahead of the teardown.  The flag is what
        // keeps the update below from re-attaching to a manager that is still
        // alive between ExitingPlayMode and the edit mode that follows —
        // Publish closes the ledger in the same call, and the flag covers the
        // window in which a manager outlives it.
        private static void Stop()
        {
            s_observing = false;
            s_begunByReload = false;
            Detach();
        }

        private static void OnEditorUpdate()
        {
            if (!s_observing || s_ledger == null || !EditorApplication.isPlaying) return;

            // HasInstance rather than Instance: the getter warns, once, when no
            // manager exists, and a scene with none is the ordinary state of a
            // project's menu and a warning the observer provokes is a warning
            // about the observer.
            if (!NetworkManager.HasInstance)
            {
                Detach();
                return;
            }

            var manager = NetworkManager.Instance;
            if (manager == null) return;

            if (!ReferenceEquals(manager, s_attached))
            {
                Detach();
                Attach(manager);
            }

            Sample(manager);
        }

        // The one event the ledger reads.  OnConnectionFailed and
        // OnReconnectFailed are deliberately not among them: both are raised for
        // a local configuration fault or a cancelled loop as readily as for the
        // wire's refusal, and the ledger records no failures — see its header.
        private static void Attach(NetworkManager manager)
        {
            manager.OnStateChanged += OnStateChanged;
            s_attached = manager;
        }

        private static void Detach()
        {
            // A manager the engine has destroyed is still a managed object with
            // a delegate list; unsubscribing from it is harmless and keeps the
            // rule simple.  `s_attached` is compared by reference above for the
            // same reason — the engine's overloaded null never enters into it.
            var manager = s_attached;
            s_attached = null;
            TrackRooms(null);
            if (manager is null) return;

            manager.OnStateChanged -= OnStateChanged;
        }

        // Follow the room manager the network manager holds — rebuilt on every
        // connect — subscribing to the seat events on each instance and letting
        // go of the last.  Removed before added: the rebuild adopts the previous
        // instance's subscribers, so a plain add would hold the handler twice.
        private static void TrackRooms(RoomManager rooms)
        {
            if (ReferenceEquals(rooms, s_rooms)) return;

            if (s_rooms != null)
            {
                s_rooms.OnRoomJoined -= OnSeated;
                s_rooms.OnRoomCreated -= OnSeated;
                s_rooms.OnRoomLeft -= OnLeft;
            }

            s_rooms = rooms;
            if (rooms == null) return;

            rooms.OnRoomJoined -= OnSeated;
            rooms.OnRoomJoined += OnSeated;
            rooms.OnRoomCreated -= OnSeated;
            rooms.OnRoomCreated += OnSeated;
            rooms.OnRoomLeft -= OnLeft;
            rooms.OnRoomLeft += OnLeft;
        }

        // ── Transitions, from the runtime's events ──────────────────────────────

        private static void OnStateChanged(NetworkState previous, NetworkState next)
        {
            var ledger = s_ledger;
            var manager = s_attached;
            if (ledger == null) return;

            // Read at the transition as well as on the poll: a session that
            // begins and ends inside one frame is announced and never polled.
            if (manager != null && manager.TransportIsCustom) ledger.TransportIsNotTheSdks();
            if (manager != null && manager.TransportIsShaped) ledger.LinkIsShaped();

            ledger.StateChanged(
                PhaseOf(previous),
                PhaseOf(next),
                manager != null && manager.Settings != null ? manager.Settings.serverHost : null);
        }

        // The seat, as the room manager announces it — a join, or a create
        // that seated the creator without a join: the same reading the poll
        // makes of the snapshot, taken in the call that made it — so the
        // identity it carries is held before anything can clear it.
        private static void OnSeated(RoomInfo room)
        {
            var ledger = s_ledger;
            var manager = s_attached;
            if (ledger == null || manager is null) return;

            s_lastRoom = room;
            ObserveRoom(ledger, manager, room);
        }

        // The seat given up, as the room manager announces it — the room is
        // already forgotten when this is raised, so the reading is the one the
        // poll would make of the empty snapshot, taken in the call that
        // emptied it.
        private static void OnLeft()
        {
            var ledger = s_ledger;
            var manager = s_attached;
            if (ledger == null || manager is null) return;

            s_lastRoom = null;
            ObserveRoom(ledger, manager, null);
        }

        /// <summary>
        /// The runtime's state as the ledger reads it.  A switch over the enum,
        /// so a renamed member fails to compile rather than silently mapping to
        /// <see cref="ObservedPhase.Other"/>.
        /// </summary>
        private static ObservedPhase PhaseOf(NetworkState state)
        {
            switch (state)
            {
                case NetworkState.Connected:    return ObservedPhase.Connected;
                case NetworkState.InRoom:       return ObservedPhase.InRoom;
                case NetworkState.Reconnecting: return ObservedPhase.Reconnecting;
                default:                        return ObservedPhase.Other;
            }
        }

        // ── States, polled ───────────────────────────────────────────────────────

        private static void Sample(NetworkManager manager)
        {
            var ledger = s_ledger;
            if (ledger == null) return;

            if (manager.TransportIsCustom) ledger.TransportIsNotTheSdks();
            if (manager.TransportIsShaped) ledger.LinkIsShaped();

            // The room snapshot is immutable and replaced on every change to it
            // — a seat taken or left, a host change, a property write — so a
            // reference comparison is the whole of the change detection, and
            // the roster is rebuilt only then: a handful of times per session
            // for a room whose properties are quiet, at the write rate for one
            // whose game writes them every frame, which is still one small
            // roster per write in an Editor-only observer.
            var rooms = manager.Rooms;
            TrackRooms(rooms);
            var room = rooms != null ? rooms.CurrentRoom : null;
            if (!ReferenceEquals(room, s_lastRoom))
            {
                s_lastRoom = room;
                ObserveRoom(ledger, manager, room);
            }

            var spawner = manager.Spawner;
            if (spawner == null) return;

            if (ledger.StillWants(PlayModeObservationLedger.SyncId))
            {
                ledger.RemoteValuesDelivered(spawner.RemoteValuesDelivered);
            }

            if (ledger.StillWants(PlayModeObservationLedger.BothPlayersId))
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - s_lastRegistrySample >= RegistrySampleIntervalSeconds)
                {
                    s_lastRegistrySample = now;
                    ObserveRegistry(ledger, manager, spawner);
                }
            }
        }

        private static void ObserveRoom(PlayModeObservationLedger ledger, NetworkManager manager, RoomInfo room)
        {
            // The seat's identity, remembered in the call that learned it — the
            // ledger holds it for this session, the session state for the next.
            Remember(manager.LocalPlayerStringId);

            if (room == null)
            {
                ledger.RoomObserved(null, manager.LocalPlayerStringId, null);
                return;
            }

            s_rosterScratch.Clear();
            var players = room.Players;
            if (players != null)
            {
                for (int i = 0; i < players.Length; i++)
                {
                    var player = players[i];
                    if (player == null) continue;
                    s_rosterScratch.Add(new ObservedPlayer(player.PlayerId, player.IsHost));
                }
            }

            ledger.RoomObserved(room.RoomId, manager.LocalPlayerStringId, s_rosterScratch);
        }

        // An identity this client holds, kept for the ledgers of later play
        // sessions.  The session state is read once per NEW seat: the room
        // snapshot is replaced on every property write too, and each reading
        // of it reaches here, so the id last remembered is compared first and
        // the store is touched only when the seat changed.
        private static void Remember(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return;
            if (string.Equals(identity, s_lastRemembered, StringComparison.Ordinal)) return;
            s_lastRemembered = identity;

            string stored = SessionState.GetString(RememberedIdentitiesKey, string.Empty);
            string remembered = RememberedIdentities.Remember(stored, identity);
            if (string.Equals(remembered, stored, StringComparison.Ordinal)) return;

            SessionState.SetString(RememberedIdentitiesKey, remembered);
        }

        private static void ObserveRegistry(
            PlayModeObservationLedger ledger, NetworkManager manager, SpawnManager spawner)
        {
            var registry = spawner.Registry;
            if (registry == null) return;

            // A snapshot rather than the live list: the registry's own rule for
            // any caller that may be interleaved with a spawn, and the observer
            // runs between frames rather than inside the dispatch.
            s_registryScratch.Clear();
            registry.GetAllSnapshot(s_registryScratch);

            // The OWNER IDS, not IsOwner / IsOwnedByAnotherPlayer: whose an
            // object is, is the ledger's to decide, because it alone knows every
            // identity this client has held — and the runtime's own answer calls
            // this client's earlier seat somebody else.
            s_ownerScratch.Clear();
            for (int i = 0; i < s_registryScratch.Count; i++)
            {
                var behaviour = s_registryScratch[i];
                if (behaviour == null || !behaviour.IsSpawned) continue;
                s_ownerScratch.Add(behaviour.OwnerPlayerId);
            }

            s_registryScratch.Clear();
            ledger.ObjectsObserved(manager.LocalPlayerStringId, s_ownerScratch);
            s_ownerScratch.Clear();
        }

        // ── The write, after play mode ──────────────────────────────────────────

        private static void Publish()
        {
            var ledger = s_ledger;
            s_ledger = null;
            s_lastRoom = null;
            if (ledger == null) return;

            var outcomes = ledger.Outcomes();
            if (outcomes.Count == 0)
            {
                if (ledger.ThroughShapedLink)
                {
                    Debug.Log(
                        "[RTMPE] The play session ran under the Link Simulator (Window > RTMPE > "
                        + "Network Debugger): a link the bench shaped is not the network the players "
                        + "will have, so nothing it established is attributed to anything, and nothing "
                        + "was recorded.");
                }
                else if (ledger.ThroughCustomTransport)
                {
                    Debug.Log(
                        "[RTMPE] The play session ran on a transport this project installed "
                        + "(NetworkManager.SetTransportFactory), not the SDK's; nothing it established "
                        + "is attributed to a deployed gateway, and nothing was recorded.");
                }

                return;
            }

            string artifactPath = ReadinessArtifactData.DefaultPath();
            var artifact = ReadinessArtifactData.Load(artifactPath, out string loadError);
            if (artifact == null)
            {
                // Nowhere to write: the artifact is what names the record.  An
                // artifact that is there and cannot be read is a fault, said on
                // every play session; one that is not there is the ordinary
                // state of a project that never runs the scan, said once per
                // Editor session — and said with a caveat from a root whose
                // Assets is a link: a virtual player or a clone plays the
                // second client and the main Editor records, while a checkout
                // that links its Assets by choice is an ordinary project, so
                // the command is given either way.
                if (loadError != null)
                {
                    Debug.LogWarning(
                        "[RTMPE] The play session established " + ledger.Summary() + ", and the readiness "
                        + "artifact at " + artifactPath + " could not be read (" + loadError + "), so "
                        + "nothing was recorded. Regenerate it with\n    "
                        + ReadinessArtifactData.RegenerateCommand());
                    return;
                }

                if (AlreadySaidThisEditorSession(NowhereToWriteSaidKey)) return;

                Debug.Log(
                    "[RTMPE] The play session established " + ledger.Summary() + ", and there is no "
                    + "readiness artifact at " + artifactPath + " to record that beside"
                    + (ReadinessArtifactData.ProjectRootIsLinked()
                        ? " — and this project root links to another project's (a Multiplayer Play Mode "
                          + "virtual player, a project clone): as the second Editor of a two-player setup, "
                          + "leave it to the main Editor, which records beside its own artifact; as a "
                          + "project of its own, run\n    "
                        : ". Run\n    ")
                    + ReadinessArtifactData.RegenerateCommand()
                    + "\nand play again: the observer records what it sees on every play session "
                    + "(said once per Editor session).");
                return;
            }

            string recordPath = ReadinessArtifactData.RecordPathBeside(artifactPath, artifact.runtimeFile);
            if (recordPath == null)
            {
                Debug.Log(
                    "[RTMPE] The play session established " + ledger.Summary() + ", but the readiness "
                    + "artifact at " + artifactPath + " predates the runtime checks and names no record "
                    + "file. Regenerate it with\n    " + ReadinessArtifactData.RegenerateCommand()
                    + "\nand play again.");
                return;
            }

            if (artifact.RecordsCollide())
            {
                Debug.LogWarning(
                    "[RTMPE] The play session established " + ledger.Summary() + ", but the readiness "
                    + "artifact names one file for its authority answers and its runtime record, and "
                    + "they are two different records; nothing was written. Regenerate it with\n    "
                    + ReadinessArtifactData.RegenerateCommand());
                return;
            }

            if (!RuntimeChecksFile.RecordMany(
                    recordPath, outcomes, RuntimeChecksFile.EditorObserver, RtmpeSdk.Version,
                    yieldToAnotherWritersClaim: true, out bool written, out string error))
            {
                Debug.LogWarning(
                    "[RTMPE] The play session established " + ledger.Summary() + ", and the runtime "
                    + "record at " + recordPath + " could not be written: " + error);
                return;
            }

            // The promise below is the window's sentence and carries the
            // window's condition: the merge keeps every entry it did not touch,
            // a foreign or hand-edited row among them, and a record the next
            // scan refuses whole reaches no report — so that is what is said
            // over such a record, written to or not.
            string refusal = RuntimeChecksFile.WhyTheScanWouldRefuseTheRecordAt(
                recordPath, artifact.RuntimeCheckIds());
            if (refusal != null)
            {
                Debug.LogWarning(
                    "[RTMPE] The play session established, as observed by this Editor: " + ledger.Summary()
                    + " — " + (written ? "offered to " : "already on file at ") + recordPath
                    + ", but the next scan will refuse the record whole: " + refusal
                    + ". It reaches no report until that is fixed; the Readiness window says the same.");
                return;
            }

            if (!written)
            {
                // The ordinary session: what it established is what the record
                // already holds, under this Editor's name with the stamp of its
                // first making, or under another writer's — and the record is
                // what says which.
                Debug.Log(
                    "[RTMPE] The play session established, as observed by this Editor: " + ledger.Summary()
                    + " — already on file at " + recordPath + " (a check the load harness or the developer "
                    + "has recorded, passed or failed, keeps their entry).");
                return;
            }

            // The report does not move until the scan runs again, and saying so
            // is the difference between a tool that records and one that only
            // appears to — the same sentence the window prints for a button.
            // "Offered": a check another writer has passed or failed keeps that
            // writer's entry, and the record is what says which.
            Debug.Log(
                "[RTMPE] The play session established, as observed by this Editor: " + ledger.Summary()
                + " — offered to " + recordPath + " (a check the load harness or the developer has "
                + "already recorded, passed or failed, keeps their entry). The Readiness window shows"
                + " the record now; it reaches the report on the next scan:\n    "
                + ReadinessArtifactData.RegenerateCommand());
        }

        // Whether the advice under `key` has been given in this Editor session,
        // marking it given if not — session state, so a domain reload does not
        // reset the count and an Editor restart does.
        private static bool AlreadySaidThisEditorSession(string key)
        {
            if (SessionState.GetBool(key, false)) return true;
            SessionState.SetBool(key, true);
            return false;
        }
    }
}
#endif
