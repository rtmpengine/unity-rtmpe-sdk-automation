// RTMPE SDK — Runtime/Rooms/SceneLoaderOps.cs
//
// Everything RtmpeSceneLoader decides, with none of what it calls.
//
// Loading a scene is one engine call. What is hard about doing it on a room's
// instruction is SEQUENCE: which instruction a finished load belongs to, whether
// the component that started it will still exist when it finishes, and what to
// say when the load cannot start at all. Those are the three things below, and
// none of them needs Unity to answer — which is what lets every one of them be
// driven by a test rather than described by one.
//
// ⛔ Free of UnityEngine on purpose, and it is not a stylistic preference: the
// scene loader is a MonoBehaviour whose whole body is engine calls, so a
// decision left inside it is a decision reachable only from a running editor.
// The same split that PrefabTableOps makes for the prefab registry.

using System;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Which scene instruction is outstanding, and whether a finished load still
    /// belongs to it.
    /// </summary>
    /// <remarks>
    /// 🔑 A room may issue a second instruction while the first is still
    /// loading — a host that restarts a match, a lobby returning to itself — and
    /// re-issuing the scene the room is already on is exactly how a reload is
    /// expressed, so two instructions naming ONE scene is the ordinary case
    /// rather than an exotic one. A loader that reported readiness for whichever
    /// load happened to finish would report the superseded one, and the room
    /// would count a report for a load nobody is doing any more.
    /// <para>
    /// ⛔ Unity cannot cancel a <c>LoadSceneAsync</c> already under way, so the
    /// superseded load still finishes and still fires its callback. What this
    /// type buys is that the callback is recognised as belonging to nobody.
    /// </para>
    /// </remarks>
    internal sealed class SceneLoadSequence
    {
        /// <summary>Never issued as a token, so it can mean "none".</summary>
        internal const long NoLoad = 0L;

        private long   _issued;
        private long   _current;
        private string _scene;

        /// <summary>The scene the outstanding instruction names, or null.</summary>
        internal string CurrentScene => _current == NoLoad ? null : _scene;

        /// <summary>Nothing is outstanding.</summary>
        internal bool Idle => _current == NoLoad;

        /// <summary>
        /// Record a fresh instruction and take the token that identifies it.
        /// Any instruction still outstanding is superseded by this one.
        /// </summary>
        internal long Begin(string sceneName)
        {
            _scene = sceneName;
            // ⚠️ Monotonic, never reused. A token that could be handed out twice
            // would let a callback from a load two instructions ago be mistaken
            // for the current one — which is the single thing this type exists
            // to prevent.
            _current = ++_issued;
            return _current;
        }

        /// <summary>Whether <paramref name="token"/> names the outstanding instruction.</summary>
        internal bool IsCurrent(long token) => token != NoLoad && token == _current;

        /// <summary>
        /// Whether a load finishing under <paramref name="token"/> is the one to
        /// report — and, when it is, retire it in the same call.
        /// </summary>
        /// <remarks>
        /// ⛔ One call, not a test followed by a clear. Two calls are two places
        /// for a caller to take the answer and forget the bookkeeping, and the
        /// bookkeeping is what stops a second callback for the same load
        /// reporting readiness twice.
        /// </remarks>
        internal bool TryComplete(long token)
        {
            if (!IsCurrent(token)) return false;
            Retire();
            return true;
        }

        /// <summary>
        /// Give up on <paramref name="token"/> without reporting: the load could
        /// not be started at all.
        /// </summary>
        /// <remarks>
        /// Retiring it matters even though nothing is reported. Left
        /// outstanding, the loader would answer <see cref="CurrentScene"/> with a
        /// scene it never began, and an instruction arriving afterwards would
        /// read as a supersession of a load that never existed.
        /// </remarks>
        internal void Abandon(long token)
        {
            if (IsCurrent(token)) Retire();
        }

        /// <summary>Forget the outstanding instruction, whatever it was.</summary>
        /// <remarks>
        /// The session ended, or this component is going away. ⚠️ The issue
        /// counter is deliberately NOT reset: a callback from a load started
        /// before the teardown can still arrive, and a counter that restarted
        /// would eventually hand that callback a token it recognises.
        /// </remarks>
        internal void Clear() => Retire();

        private void Retire()
        {
            _current = NoLoad;
            _scene   = null;
        }
    }

    /// <summary>
    /// What the loader says, and when.
    /// </summary>
    /// <remarks>
    /// Beside the decisions rather than inside the component, for the same
    /// reason the decisions are: a message written at an engine call site is a
    /// message no test can read, and every one of these names a fault an
    /// integrator has to act on.
    /// </remarks>
    internal static class SceneLoaderReports
    {
        /// <summary>
        /// Unity's name for the scene that objects are moved to by
        /// <c>DontDestroyOnLoad</c>.
        /// </summary>
        internal const string PersistentScene = "DontDestroyOnLoad";

        /// <summary>
        /// Whether a component living in <paramref name="sceneName"/> is still
        /// alive after a <c>Single</c> load.
        /// </summary>
        /// <remarks>
        /// 🔑 The question that decides whether this component can do its job at
        /// all. A <c>Single</c> load destroys every object in every loaded
        /// scene; a loader destroyed by the load it started never reports
        /// readiness, so the room waits for a client that no longer exists and
        /// every other player sits at a loading screen until the deadline. It is
        /// the exact failure this component was built to remove, arriving
        /// through the component itself.
        /// <para>
        /// ⚠️ A null or empty name reads as persistent rather than as doomed. An
        /// object that belongs to no loaded scene is not one a scene load
        /// destroys, and guessing the other way would put a red line under a
        /// correctly configured project — which is how an integrator learns to
        /// ignore the line that matters.
        /// </para>
        /// </remarks>
        internal static bool SurvivesSingleLoad(string sceneName)
            => string.IsNullOrEmpty(sceneName)
               || string.Equals(sceneName, PersistentScene, StringComparison.Ordinal);

        /// <summary>
        /// The line for a loader that a <c>Single</c> load is about to destroy,
        /// or null when it will survive.
        /// </summary>
        internal static string DoomedByItsOwnLoad(string objectName, string sceneName)
        {
            if (SurvivesSingleLoad(sceneName)) return null;

            return "[RTMPE] RtmpeSceneLoader on '" + Describe(objectName) + "' is about to load a "
                 + "scene in Single mode, which destroys it before the load completes — so this "
                 + "client never reports ready and the whole room waits for it. Put the loader on "
                 + "a root GameObject (it makes itself persistent), or on the object that carries "
                 + "the NetworkManager.";
        }

        /// <summary>
        /// The line for a load the engine refused to start, or null when it
        /// started.
        /// </summary>
        /// <remarks>
        /// ⛔ Said loudly, because nothing else will say it. <c>LoadSceneAsync</c>
        /// answers null for a scene that is not in the build — the commonest
        /// misconfiguration there is — and from the room's side that is
        /// indistinguishable from a client that is simply slow: the readiness
        /// deadline reports a room that did not settle and names no cause.
        /// </remarks>
        internal static string CouldNotStart(string sceneName, bool started)
        {
            if (started) return null;

            return "[RTMPE] RtmpeSceneLoader: the engine refused to load scene '"
                 + Describe(sceneName) + "'. It is almost certainly missing from Build Settings "
                 + "(File → Build Settings → Scenes In Build), or misspelled in the room's "
                 + "__scene property. Nothing is loading, and this client will not report ready.";
        }

        /// <summary>
        /// The line for a loader that has just displaced another, or null when
        /// there was no other.
        /// </summary>
        /// <remarks>
        /// 🚨 Two loaders is not a hypothetical: the component makes itself
        /// persistent, so a copy sitting in a scene the room LOADS becomes a
        /// second persistent loader every time the room returns there, and the
        /// count grows without bound. Each copy answers every instruction, so
        /// the room's scene is loaded once per copy — in sequence, each load
        /// destroying and rebuilding what the previous one produced.
        /// <para>
        /// The most recently enabled one wins, which is how InterestManager
        /// settles the same collision. The line matters because the symptom —
        /// a transition that visibly happens twice — reads as an engine problem
        /// rather than as a placement one.
        /// </para>
        /// </remarks>
        internal static string SecondLoaderTookOver(bool displacedAnother, string objectName)
        {
            if (!displacedAnother) return null;

            return "[RTMPE] A second RtmpeSceneLoader ('" + Describe(objectName) + "') has taken "
                 + "over from an earlier one. Only the most recently enabled loader acts, so the "
                 + "room's scene is not loaded twice — but a second loader means a copy of the "
                 + "component is sitting in a scene the room loads, and another appears every "
                 + "time the room returns there. Move it to your boot scene, or onto the object "
                 + "that carries the NetworkManager.";
        }

        /// <summary>
        /// The line for an additive instruction naming a scene that is already
        /// open, or null when it is not.
        /// </summary>
        /// <remarks>
        /// ⛔ Unity does not deduplicate an additive load: asked for a scene it
        /// already has open, it opens a SECOND copy, and every object in that
        /// scene then exists twice — a second audio listener, a second set of
        /// lights, a second copy of anything spawned into it. The SDK documents
        /// re-issuing the same scene as the way to restart a round, so the
        /// documented gesture is exactly what triggers it, and no unload path
        /// exists anywhere in the SDK to undo it.
        /// <para>
        /// ⚠️ Refusing is the conservative half of an asymmetry worth stating.
        /// A room that asks additively for a scene it already has open gets what
        /// it asked for either way; a room that wanted a genuine reload has to
        /// unload first, which is a decision about WHICH scene to close and
        /// therefore the application's rather than this component's.
        /// </para>
        /// </remarks>
        internal static string AlreadyLoadedAdditively(string sceneName, bool alreadyLoaded)
        {
            if (!alreadyLoaded) return null;

            return "[RTMPE] RtmpeSceneLoader: scene '" + Describe(sceneName) + "' is already open "
                 + "and the room asked for it additively. Loading it again would open a SECOND "
                 + "copy — Unity does not deduplicate additive loads — so this client reports "
                 + "ready without reloading. To restart an additive scene, unload it first: "
                 + "which scene to close is the application's decision.";
        }

        /// <summary>
        /// The line for a finished load the room has already moved past, or null
        /// when the room is still on it.
        /// </summary>
        /// <remarks>
        /// 🔑 The last step of the token discipline. A report names the ROOM's
        /// current scene, not the load's, so a room whose scene changed without
        /// the announcement reaching the loader would be told this client is
        /// ready for a scene it has never loaded. Silence is the honest answer:
        /// the readiness deadline reports a room that did not settle, which is
        /// true, where a false report would have the room start without a
        /// player who is not there.
        /// </remarks>
        internal static string RoomMovedOn(string loadedScene, string roomScene)
        {
            if (string.Equals(loadedScene, roomScene, StringComparison.Ordinal)) return null;

            return "[RTMPE] RtmpeSceneLoader finished loading '" + Describe(loadedScene)
                 + "' but the room is now on '" + Describe(roomScene) + "', and this client has "
                 + "not loaded that. No readiness is reported for a scene this client is not in. "
                 + "This happens when a subscriber changes room from inside a scene-load handler.";
        }

        /// <summary>
        /// The scene's own name, taken from whatever spelling the room used.
        /// </summary>
        /// <remarks>
        /// 🚨 A room's <c>__scene</c> may hold a name, a partial path or the
        /// whole project-relative path with its extension — Unity's loader
        /// accepts all three, and passing enough of the path is the documented
        /// way to tell two scenes of one name apart. <c>GetSceneByName</c>
        /// accepts only the first: handed a path it finds nothing, answers a
        /// scene that is not valid, and the additive duplicate check above it
        /// then reports "not loaded" for a scene that is open. The check would
        /// be silently absent for exactly the projects that took the advice.
        /// <para>
        /// ⚠️ Two open scenes whose paths differ but whose names do not are
        /// indistinguishable to this, which is the same ambiguity Unity itself
        /// has when a bare name is loaded. Refusing on it is the conservative
        /// side of that: the room asked additively for a scene of this name and
        /// one of that name is already open.
        /// </para>
        /// </remarks>
        internal static string LeafName(string sceneNameOrPath)
        {
            if (string.IsNullOrEmpty(sceneNameOrPath)) return sceneNameOrPath;

            string path = sceneNameOrPath.Replace('\\', '/');

            int slash = path.LastIndexOf('/');
            if (slash >= 0) path = path.Substring(slash + 1);

            if (path.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - SceneExtension.Length);

            return path;
        }

        /// <summary>The extension Unity's scene assets carry.</summary>
        internal const string SceneExtension = ".unity";

        // A name is either the room's or the project's, and both reach a log
        // line. Rendered rather than interpolated so an empty one cannot make
        // the sentence read as though it named something.
        private static string Describe(string value)
            => string.IsNullOrEmpty(value) ? "(unnamed)" : value;
    }
}
