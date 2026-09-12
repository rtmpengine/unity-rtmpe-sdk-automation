// RTMPE SDK — Runtime/Core/ConnectionBootstrapOps.cs
//
// Everything RtmpeConnectionBootstrap decides, with none of what it calls.
//
// Getting from a cold start to a player standing in a room is four engine-free
// questions — which room operation this connection calls for, whether the
// avatar this component spawned is still the session's, whether the settings it
// was given can work at all, and what to say when one of them cannot. The
// answers are here, where a test can drive them, and the component is left with
// the subscriptions and the calls.
//
// ⛔ Free of UnityEngine on purpose, the same split RtmpeSceneLoader makes with
// SceneLoaderOps and SpawnManager with PrefabTableOps: a decision left inside a
// MonoBehaviour is a decision reachable only from a running editor.

using System;
using System.Collections.Generic;
using System.Text;

namespace RTMPE.Core
{
    /// <summary>
    /// How <see cref="RtmpeConnectionBootstrap"/> gets into a room once the
    /// connection is up.
    /// </summary>
    /// <remarks>
    /// ⛔ There is no "join or create" member, and its absence is deliberate:
    /// that is what <see cref="Matchmaking"/> is. The SDK already implements
    /// join-or-create on the server side of a single request, with its own
    /// timeout and its own four outcomes, and a component that looped
    /// ListRooms → JoinRoom → CreateRoom would be a second, racier answer to a
    /// question already answered.
    /// </remarks>
    public enum RoomEntryPolicy
    {
        /// <summary>Open a room of your own and be its host.</summary>
        CreateRoom,

        /// <summary>Enter one room, named by id.</summary>
        JoinRoom,

        /// <summary>Ask the server for a room to share, or a new one.</summary>
        Matchmaking,
    }

    /// <summary>What the bootstrap should do about entering a room right now.</summary>
    internal enum EntryAction
    {
        /// <summary>Nothing: the SDK is about to rejoin the last room itself.</summary>
        WaitForTheSdk,

        /// <summary>Re-enter the room this component was in before the drop.</summary>
        Rejoin,

        /// <summary>The configured policy.</summary>
        Create,
        Join,
        Matchmake,
    }

    /// <summary>What the bootstrap should do about the avatar it spawned.</summary>
    internal enum LocalPlayerAction
    {
        /// <summary>It is live and the session still holds it.</summary>
        Keep,

        /// <summary>It outlived the session that spawned it; destroy it and spawn again.</summary>
        ReplaceStale,

        /// <summary>There is nothing to keep; spawn.</summary>
        Spawn,
    }

    internal static class ConnectionBootstrapOps
    {
        /// <summary>
        /// The room operation a connection that has just come up calls for.
        /// </summary>
        /// <remarks>
        /// 🔑 <paramref name="sdkWillRejoin"/> is the whole of why this is a
        /// decision rather than a call. After a token-based reconnect the SDK
        /// rejoins the last room ITSELF — `NetworkSettings.autoRejoinLastRoomOnReconnect`
        /// is true by default — and it does so AFTER raising the transition into
        /// Connected. A component that ran its policy on that transition would
        /// therefore issue a second room operation into the same session: with
        /// <see cref="RoomEntryPolicy.CreateRoom"/> that is a brand-new empty
        /// room, taken instead of the one the player was in.
        /// <para>
        /// ⛔ And the rejoin is a JOIN whatever the policy says. Re-running
        /// CreateRoom after a drop opens a room nobody else is in, which reads
        /// as "the other players vanished" and is the failure mode a bootstrap
        /// exists to prevent. A remembered room is entered by id or not at all.
        /// </para>
        /// </remarks>
        /// <param name="policy">The configured entry policy.</param>
        /// <param name="rejoinWanted">Whether the component is configured to re-enter its last room.</param>
        /// <param name="rememberedRoomId">The room this component last entered, or null.</param>
        /// <param name="sdkWillRejoin">Whether the SDK is about to rejoin the last room on its own.</param>
        internal static EntryAction DecideEntry(
            RoomEntryPolicy policy, bool rejoinWanted, string rememberedRoomId, bool sdkWillRejoin)
        {
            if (sdkWillRejoin) return EntryAction.WaitForTheSdk;

            if (rejoinWanted && !string.IsNullOrEmpty(rememberedRoomId)) return EntryAction.Rejoin;

            switch (policy)
            {
                case RoomEntryPolicy.JoinRoom:    return EntryAction.Join;
                case RoomEntryPolicy.Matchmaking: return EntryAction.Matchmake;
                default:                          return EntryAction.Create;
            }
        }

        /// <summary>
        /// What to do with the avatar this component spawned, now that a room
        /// has been entered.
        /// </summary>
        /// <remarks>
        /// 🔑 Three states, not two, and the third is the one a null check
        /// cannot see. Entering a room re-raises on an ordinary rejoin, so the
        /// common case is an avatar that is simply still there. A disconnect
        /// tears the session down and destroys it, and Unity's equality reports
        /// a destroyed object as null, so that case answers
        /// <see cref="LocalPlayerAction.Spawn"/> on its own.
        /// <para>
        /// ⛔ What neither covers is an object that SURVIVED a session it no
        /// longer belongs to. 🚨 The reason first written here — that a reconnect
        /// skips the disconnect teardown — is FALSE: <c>Reconnect</c> refuses
        /// unless the manager is already Disconnected, so the teardown has always
        /// run. What is true is narrower and still reachable: session teardown
        /// despawns before it disposes, and a project with a custom prefab pool
        /// has its avatar RELEASED rather than destroyed, so a component that was
        /// disabled across the drop — and therefore saw no OnDisconnected — comes
        /// back holding a live object the session has never heard of:
        /// spawning beside it leaves an inert copy standing in the scene for the
        /// rest of the game, and keeping it leaves the player driving an object
        /// no peer will ever see move.
        /// </para>
        /// </remarks>
        /// <param name="live">Whether the reference is a live engine object.</param>
        /// <param name="spawned">Whether the current session considers it spawned.</param>
        internal static LocalPlayerAction DecideLocalPlayer(bool live, bool spawned)
        {
            if (!live) return LocalPlayerAction.Spawn;

            return spawned ? LocalPlayerAction.Keep : LocalPlayerAction.ReplaceStale;
        }

        /// <summary>
        /// Everything about this configuration that cannot work, named. Empty
        /// when the component is able to run.
        /// </summary>
        /// <remarks>
        /// ⛔ Asked at every door into a room operation — before the connection
        /// is opened, and again by the poll and by Restart, neither of which
        /// passes through Connect — because every one of these becomes a silence
        /// later, and one of them becomes an exception out of Update: a join with no room id sends nothing,
        /// a capacity outside the platform's range is refused before the request
        /// leaves, and either leaves a developer watching a component that
        /// connected and then did nothing at all.
        /// <para>
        /// A missing player prefab is NOT here. A project may want the entry
        /// flow and no avatar — a spectator, a server-driven cast, a lobby
        /// screen — and refusing that would be refusing a legitimate setup. It
        /// is said once at room entry instead, where it is a fact rather than a
        /// fault.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<string> Refusals(
            RoomEntryPolicy policy, string roomName, string roomId,
            string matchmakingMode, int maxPlayers)
        {
            var refusals = new List<string>();

            // ⛔ Delegated, never restated. The platform's bounds are declared
            // once in RoomFieldLimits and a copy of them here would be a second
            // statement of one fact — which is how the two come to disagree, and
            // the disagreeing one is always the copy. Each validator answers
            // null when there is nothing to say.
            // ⛔ EMPTY only, and the rest of RoomFieldLimits.ValidateRoomId is
            // deliberately not applied — its own documentation says why: a room
            // id is issued by the server, the server is the only party that can
            // say whether one names anything, and a client that judged the
            // FORMAT "would start refusing every room the day the id format
            // widened". An empty one is different in kind: RoomManager.JoinRoom
            // logs and returns on it, so nothing is sent and nothing answers.
            if (policy == RoomEntryPolicy.JoinRoom && string.IsNullOrEmpty(roomId))
            {
                refusals.Add(
                    "the entry policy is JoinRoom and no Room Id is set, so nothing would be "
                    + "sent. Set one, or choose Matchmaking, which finds a room without an id");
            }

            if (policy == RoomEntryPolicy.CreateRoom)
            {
                string named = RTMPE.Rooms.RoomFieldLimits.ValidateRoomName(roomName);
                if (named != null) refusals.Add(named.TrimEnd('.'));
            }

            // 🔴 The refusal that was missing, and it was the one that mattered:
            // MatchmakingManager THROWS on an empty mode, so a component whose
            // configuration reached it with one did not fail — it threw out of a
            // Unity lifecycle callback, having already marked its entry as
            // issued, and then said nothing for the rest of the session.
            //
            // ⛔ The three rules are the SDK's own, reached rather than
            // restated: the same constant it measures against and the same
            // character walk it runs, so a mode this admits is a mode it accepts.
            if (policy == RoomEntryPolicy.Matchmaking)
            {
                if (string.IsNullOrEmpty(matchmakingMode))
                {
                    refusals.Add(
                        "the entry policy is Matchmaking and no Matchmaking Mode is set — the SDK "
                        + "refuses an empty mode, so nothing would be sent. Set one, or choose "
                        + "CreateRoom, which needs no mode");
                }
                else if (Encoding.UTF8.GetByteCount(matchmakingMode) > MatchmakingModeBytes)
                {
                    refusals.Add(
                        "the Matchmaking Mode is " + Encoding.UTF8.GetByteCount(matchmakingMode)
                        + " UTF-8 bytes and the limit is " + MatchmakingModeBytes);
                }
                else if (RTMPE.Rooms.RoomFieldLimits.FirstUnsafeCharacterIndex(matchmakingMode) >= 0)
                {
                    refusals.Add(
                        "the Matchmaking Mode carries a character the server refuses, at index "
                        + RTMPE.Rooms.RoomFieldLimits.FirstUnsafeCharacterIndex(matchmakingMode));
                }
            }

            // Both room-opening policies carry a capacity to the server;
            // JoinRoom does not, and judging it there would refuse a field the
            // configuration never sends.
            if (policy != RoomEntryPolicy.JoinRoom)
            {
                string capacity = RTMPE.Rooms.RoomFieldLimits.ValidateMaxPlayers(maxPlayers);
                if (capacity != null) refusals.Add(capacity.TrimEnd('.'));
            }

            return refusals;
        }

        /// <summary>
        /// The longest matchmaking mode the Room Service stores, in UTF-8 bytes.
        /// </summary>
        /// <remarks>
        /// ⚠️ The one server constant this file restates, and it is restated
        /// because the SDK's own copy — <c>MatchmakingManager.MaxModeBytes</c> —
        /// is internal to a type that drags the whole matchmaking graph into any
        /// project compiling this one. The two are held to each other by
        /// <c>TheStubsMatchTheSdkTests.TheModeBoundIsTheOneTheSdkEnforces</c>,
        /// which reads the shipped constant out of the source rather than
        /// trusting this sentence — 🚨 as it did not until 2026-09-07, when the
        /// named test was found to exist nowhere and the copy below was held by
        /// nothing at all.
        /// </remarks>
        internal const int MatchmakingModeBytes = 64;
    }

    /// <summary>
    /// What the bootstrap says, composed where a test can read it.
    /// </summary>
    /// <remarks>
    /// 🔑 Here rather than at the call sites for the reason SceneLoaderOps gives:
    /// no project in this repository compiles a MonoBehaviour, so a sentence
    /// written inside one is a sentence nothing can hold — and a message is
    /// production code, because its remedy is an instruction somebody follows.
    /// </remarks>
    internal static class BootstrapReports
    {
        /// <summary>Said when a second bootstrap takes the active slot.</summary>
        internal static string SecondBootstrapTookOver(bool displaced, string objectName)
            => !displaced
                ? null
                : "[RTMPE] A second RtmpeConnectionBootstrap became active ('" + objectName
                + "'), and it is the one that will act. This component makes itself persistent, "
                + "so a copy in a scene the game reloads becomes another one every time — put it "
                + "in a boot scene the game never returns to, beside the NetworkManager.";

        /// <summary>Said when the session carries no matchmaking facade.</summary>
        /// <remarks>
        /// ⛔ Its own sentence, because the one it used to borrow said "found no
        /// NetworkManager in the scene" — a manager had been found and reached,
        /// and a message that names the wrong thing sends a reader to fix
        /// something that is not broken.
        /// </remarks>
        internal const string NoMatchmaking =
            "[RTMPE] RtmpeConnectionBootstrap cannot ask for a match: this session exposes no "
            + "matchmaking. The session is rebuilt on every connect, so this is an SDK fault "
            + "rather than a setting — report it, and choose CreateRoom or JoinRoom meanwhile.";

        /// <summary>Said when there is no manager to drive.</summary>
        internal const string NoManager =
            "[RTMPE] RtmpeConnectionBootstrap found no NetworkManager in the scene, so there is "
            + "nothing to connect. Add one (Component → RTMPE → NetworkManager) to this object or "
            + "another in the boot scene, and give it your NetworkSettings asset.";

        /// <summary>Said when no credential source answered.</summary>
        /// <remarks>
        /// ⛔ The two texts differ because one of the sources does not exist where
        /// the other is read. The wizard writes to a vault the EDITOR assembly
        /// registers, and no player build compiles that assembly, so offering it
        /// to somebody reading a `Player.log` sends them to re-run a tool that
        /// cannot reach the build they just made. An integrator did exactly that,
        /// three times, before this parameter existed.
        ///
        /// <paramref name="inEditor"/> is the caller's answer to *where is this
        /// being read*. It arrives as a value rather than as a preprocessor branch
        /// here, so both texts compile in every configuration and a test can drive
        /// each of them — a branch inside this method would leave the one that
        /// matters unreachable by the shard that tests it.
        ///
        /// ⚠️ The PLAYER's remedies are listed in the order a developer needs
        /// them — the one the Setup Wizard can finish for them first — and that
        /// is no longer the order <c>ApiKeySource</c> consults: a provider
        /// registered with <c>SetProvider</c> outranks the staged development
        /// key. The two are never both configured by the same person on the same
        /// build, and the text that would have to be read in consult order is
        /// the one nobody can act on. The Editor's text is unchanged. ⛔ <c>--rtmpe-api-key</c> is
        /// consulted between the file and the environment and is named in neither
        /// text: it puts the key in the process table, and a report is a poor place
        /// to teach that.
        /// </remarks>
        /// <param name="fileOption">Command-line option naming a file holding the key.</param>
        /// <param name="environmentVariable">Name of the variable a player may set.</param>
        /// <param name="lastError">Why a configured source failed, or null when none did.</param>
        /// <param name="inEditor">Whether the report will be read in the Editor.</param>
        internal static string NoApiKey(
            string fileOption,
            string environmentVariable,
            string lastError,
            bool inEditor)
            => "[RTMPE] RtmpeConnectionBootstrap has no API key, so it did not connect. "
               + (inEditor
                   ? "Store one via Window → RTMPE → Setup Wizard, launch with "
                     + fileOption + " <path>, or set the " + environmentVariable
                     + " environment variable."
                   : "The Setup Wizard's vault is written by the Editor and no build carries "
                     + "it, so a player needs one of these instead. For a build you are "
                     + "testing yourself: open Window → RTMPE → Setup Wizard and tick Inject "
                     + "this key into development builds, then build with Development Build "
                     + "ticked — a "
                     + "development build is given the key while it builds and a release build "
                     + "refuses to carry one. For a build you ship: register a provider with "
                     + "ApiKeySource.SetProvider before this component connects — from an "
                     + "Awake in this scene, or by clearing Connect On Start and calling "
                     + "Connect() once you have a key. A launch you control can instead "
                     + "launch with " + fileOption + " <path>, or set the "
                     + environmentVariable + " environment variable — neither of which a "
                     + "double-clicked application inherits.")
               + (string.IsNullOrEmpty(lastError) ? "" : " A configured source failed: " + lastError);

        /// <summary>Said once when a room is entered and there is nothing to spawn.</summary>
        internal const string NothingToSpawn =
            "[RTMPE] RtmpeConnectionBootstrap entered the room and has no Player Prefab, so no "
            + "avatar was spawned. Assign one, or set ChoosePlayerPrefab in code — or ignore this "
            + "if the flow is meant to end at the room.";

        /// <summary>Said when the prefab is not registered with the session.</summary>
        /// <remarks>
        /// 🚨 The wording is the UI's, checked against it. This message used to
        /// say "Add it in Window → RTMPE → Network Prefabs, press Generate" —
        /// and that window has no add affordance at all (it reads the PROJECT
        /// window's selection), while the button whose label starts "Generate"
        /// allocates nothing: on an empty ledger it writes an empty registry and
        /// reports success. So a reader followed this sentence, saw a success
        /// line, and arrived back here. ⛔ Two ALLOCATING buttons and one
        /// EMITTING button, and the order between them is the whole instruction.
        /// </remarks>
        internal static string PrefabHasNoId(string prefabName)
            => "[RTMPE] RtmpeConnectionBootstrap cannot spawn " + prefabName
               + ": no prefab id is registered for it, so no other client could resolve it. Select "
               + "the prefab in the Project window, open Window → RTMPE → Network Prefabs and press "
               + "\"Allocate id for selection\"; then press \"Generate RtmpePrefabIds.cs and the "
               + "prefab registry\" and assign the generated registry to your NetworkSettings "
               + "asset — or register it yourself with Spawner.RegisterPrefab before the room is "
               + "entered.";

        /// <summary>Said when the room operation itself was refused.</summary>
        internal static string RoomRefused(string reason)
            => "[RTMPE] RtmpeConnectionBootstrap could not enter a room: "
               + (string.IsNullOrEmpty(reason) ? "the server gave no reason." : reason);

        /// <summary>Said when the settings this component was given cannot work.</summary>
        internal static string Unusable(IReadOnlyList<string> refusals)
        {
            if (refusals == null || refusals.Count == 0) return null;

            var text = new System.Text.StringBuilder(
                "[RTMPE] RtmpeConnectionBootstrap did not start, because ");
            for (int i = 0; i < refusals.Count; i++)
            {
                if (i > 0) text.Append("; also ");
                text.Append(refusals[i]);
            }

            return text.Append('.').ToString();
        }

        /// <summary>Said when matchmaking gave up.</summary>
        internal static string MatchmakingEnded(string outcome)
            => "[RTMPE] RtmpeConnectionBootstrap's matchmaking request " + outcome
               + ", so this client is connected and in no room. Call Restart() to try again.";

        /// <summary>Said when Restart is asked for while the session is in a room.</summary>
        internal const string RestartWhileInARoom =
            "[RTMPE] RtmpeConnectionBootstrap.Restart() does nothing while this client is in a "
            + "room, and it would have forgotten which room that was. Leave first "
            + "(NetworkManager.Instance.Rooms.LeaveRoom()) and call Restart() when the leave has "
            + "been answered.";

        /// <summary>Said, once, when the client is no longer in a room.</summary>
        /// <remarks>
        /// ⛔ Information and not a fault, because the three ways out are one
        /// event here: a leave the application asked for, a room switch, and a
        /// kick by the host. Calling the first a failure would cry wolf on the
        /// common case; saying nothing left the third — a player removed by the
        /// host — with a component that had quietly stopped working.
        /// </remarks>
        internal const string OutOfTheRoom =
            "[RTMPE] RtmpeConnectionBootstrap is no longer in a room — left, moved, or removed by "
            + "the host. It will not enter another one on its own; call Restart() to enter one "
            + "under the configured policy.";

        /// <summary>Said when the session refused to build the avatar.</summary>
        /// <remarks>
        /// ⛔ Distinct from <see cref="PrefabHasNoId"/>, and the distinction is
        /// the remedy: there the prefab is absent from the session's table, here
        /// it is in the table and the object could not be built from it — which
        /// on a registered prefab means it carries no NetworkBehaviour for the
        /// session to drive.
        /// </remarks>
        internal static string SpawnRefused(string prefabName)
            => "[RTMPE] RtmpeConnectionBootstrap asked the session to spawn " + prefabName
               + " and got nothing back, so this client has no avatar. A registered prefab must "
               + "carry a NetworkBehaviour component for the session to drive; the line above "
               + "names what the spawn itself refused.";

        /// <summary>Said when the ladder this component started ran out.</summary>
        internal static string RecoveryExhausted(int attempts)
            => "[RTMPE] RtmpeConnectionBootstrap could not restore the session: all " + attempts
               + " reconnect attempt(s) the NetworkSettings allow have been spent. The "
               + "client is offline. Call Connect() to authenticate again when there is a "
               + "reason to think the server is reachable.";

        /// <summary>Said when there is no session to restore.</summary>
        /// <remarks>
        /// ⚠️ One sentence for two states on purpose: a connection that never
        /// completed and a session closed deliberately both leave no reconnect
        /// token, and the remedy is the same in each.
        /// </remarks>
        internal static string NoSessionToRestore(string reason)
            => "[RTMPE] RtmpeConnectionBootstrap is not connected (" + reason
               + ") and holds no reconnect token, which is the case when the connection never "
               + "completed and when it was closed deliberately. Call Connect() to authenticate "
               + "again.";

        /// <summary>Said when a fresh session is opened after recovery failed.</summary>
        internal static string OpeningAFreshSession(string after, int spent, int allowed)
            => "[RTMPE] RtmpeConnectionBootstrap could not restore the session (" + after
               + ") and is authenticating again — attempt " + spent + " of " + allowed
               + ". The room it was in is remembered and re-entered if the new session reaches it.";

        /// <summary>Said when the fresh-session budget is spent.</summary>
        /// <remarks>
        /// ⛔ A bound is not optional here. Without one a server that refuses
        /// every connection turns this recovery into a connect loop at whatever
        /// rate the failures come back, which is worse for the player and worse
        /// for the server than being told it is offline.
        /// </remarks>
        internal static string FreshSessionsExhausted(int allowed)
            => "[RTMPE] RtmpeConnectionBootstrap opened " + allowed + " new session(s) after "
               + "recovery failed and none of them reached a room, so it has stopped trying. The "
               + "client is offline. Call Connect() when there is a reason to think the server is "
               + "reachable.";

        /// <summary>Said when a stale avatar is replaced.</summary>
        internal static string ReplacedStaleAvatar(string prefabName)
            => "[RTMPE] The avatar RtmpeConnectionBootstrap spawned (" + prefabName
               + ") outlived the session that spawned it and was destroyed before spawning a "
               + "fresh one; a reconnect rebuilds the spawn registry and the old object belongs "
               + "to nothing.";
    }
}
