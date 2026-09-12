// RTMPE SDK — Runtime/Rooms/RtmpeSceneLoader.cs
//
// Attach it and a room's scene instruction loads itself.
//
// The SDK already tells the application when the room has been told to load a
// scene, in which mode, and it already offers a way to report that this client
// finished. What every project then writes is the same fifteen lines: subscribe,
// call LoadSceneAsync, report on completion, unsubscribe. This is those fifteen
// lines, once.
//
// ⛔ Optional by attachment, not default by behaviour. A title that wants a
// loading screen, a staged load, or gameplay held back until an animation ends
// is not doing the common thing, and it should keep doing its own thing: the
// events this subscribes to stay public and stay documented. Automation removes
// the common pattern; it does not confiscate the particular one.
//
// 🔑 Everything it DECIDES is in SceneLoaderOps, which names no Unity type. What
// is left here is the engine calls and the wiring, which is the part a test
// cannot reach anyway — and is therefore the part that must be as small as it
// can be made.

using RTMPE.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Loads the scene the room asks for, and reports when it is loaded.
    /// </summary>
    /// <remarks>
    /// Attach one to a root GameObject in your **boot scene** — the scene the
    /// room never loads — or to the object that carries the
    /// <see cref="NetworkManager"/>, which lives there for the same reason.
    /// Nothing else is required: no configuration, and no call from application
    /// code.
    /// <para>
    /// ⛔ The boot scene is not a style note. This component makes itself
    /// persistent, so a copy sitting in a scene the room loads becomes a SECOND
    /// persistent loader every time the room returns to it, and every loader
    /// answers every instruction — the room's scene is then loaded once per
    /// copy. It is the same rule <see cref="NetworkManager"/> states for itself,
    /// and this component enforces it the same way rather than trusting it.
    /// </para>
    /// <para>
    /// The scene must be in Build Settings, like any scene Unity loads by name.
    /// If it is not, the loader says so rather than leaving the room to time
    /// out against a cause nobody can see.
    /// </para>
    /// </remarks>
    [AddComponentMenu("RTMPE/Scene Loader")]
    [DisallowMultipleComponent]
    public sealed class RtmpeSceneLoader : MonoBehaviour
    {
        // 🚨 [DisallowMultipleComponent] forbids two copies on ONE GameObject and
        // says nothing about two on two — which is exactly what DontDestroyOnLoad
        // manufactures. A loader placed in a scene the room loads is duplicated
        // every time the room returns there, and each copy issues its own load:
        // the scene is loaded once per copy, in sequence, destroying and
        // rebuilding everything the previous load produced. The count grows
        // without bound.
        //
        // The most recently enabled instance wins, exactly as InterestManager
        // resolves the same collision — and the loser says so, because a title
        // that reloads twice per transition deserves to be told why rather than
        // left to measure it.
        private static RtmpeSceneLoader s_active;

        private readonly SceneLoadSequence _sequence = new SceneLoadSequence();

        // The facade this is subscribed to, and the reason the subscription is
        // re-checked rather than made once: NetworkManager.Cleanup disposes the
        // scene manager and nulls the field, so a manager torn down and rebuilt
        // — a title returning to its boot scene between matches — hands out a
        // DIFFERENT facade, and a loader holding the old one is detached with
        // nothing to say so. NetworkSceneManager re-binds to the room manager on
        // exactly this reasoning, one layer down.
        private NetworkSceneManager _bound;

        // Whether the handler is attached to _bound.
        //
        // 🔑 A second fact rather than the field being emptied, because the two
        // answer different questions: which session the outstanding instruction
        // belongs to, and whether this component is listening. Deactivation
        // ends the listening and not the session — and while one field carried
        // both, the poll after a re-enable read an emptied field as a session
        // change and retired a load that was still running.
        private bool _subscribed;

        private long _lastDoomedWarnTicks;
        private long _lastRefusedWarnTicks;
        private long _lastDuplicateWarnTicks;
        private long _lastStackedWarnTicks;
        private long _lastMovedOnWarnTicks;

        /// <summary>The scene this loader is currently loading, or null.</summary>
        /// <remarks>
        /// For a loading screen that wants to name the destination. It is the
        /// instruction's scene, not the engine's progress: a load superseded by
        /// a newer instruction stops being current the moment the new one
        /// arrives, which is the answer a UI wants.
        /// </remarks>
        public string LoadingScene => _sequence.CurrentScene;

        private void Awake()
        {
            // ⛔ Root only. DontDestroyOnLoad moves the object's ROOT, so calling
            // it on a child would silently promote a hierarchy the author did
            // not ask to keep — and Unity warns about it besides. A child of an
            // already-persistent object needs nothing: NetworkManager runs at
            // execution order −1000, so by the time this Awake runs its object
            // has already been moved and this component moved with it.
            if (transform.parent == null) DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            // ⛔ Listening again HERE, not on the next poll. The poll is what
            // BINDS a facade, and a component coming back already has one — so
            // waiting for it costs every instruction announced in between, and
            // the claim two lines below has just taken the active slot from the
            // loader that was still listening. Between the two, an announcement
            // in that frame reaches nobody at all.
            if (!_subscribed && _bound != null) Subscribe();

            var previous = s_active;
            s_active = this;

            string duplicate = SceneLoaderReports.SecondLoaderTookOver(
                previous != null && previous != this, gameObject.name);
            if (duplicate != null && WarnGate.ShouldEmit(ref _lastDuplicateWarnTicks))
            {
                Debug.LogError(duplicate, this);
            }
        }

        private void Update()
        {
            // ⛔ Quietly. NetworkManager.Instance logs a line the first time it
            // is asked before a manager exists, and this component is
            // documented as needing no configuration — so its own poll must not
            // be what produces that line in a project doing nothing wrong.
            var live = NetworkManager.TryGetInstance(out var manager) ? manager.Scene : null;
            if (ReferenceEquals(live, _bound))
            {
                // The same session, so nothing outstanding has changed hands. A
                // missing subscription here is a deactivation and not a rebind,
                // and re-attaching is the whole of what it needs.
                if (!_subscribed && _bound != null) Subscribe();
                return;
            }

            // A different facade is a different session. Whatever was
            // outstanding belonged to the one that ended.
            Unsubscribe();
            _sequence.Clear();

            _bound = live;
            if (_bound == null) return;

            Subscribe();
        }

        // 🚨 Disable is not destruction, and treating them alike lost a load.
        // OnDisable fires when a persistent root is deactivated — a title hiding
        // its UI root through a transition — and retiring the outstanding
        // instruction there means the load still finishes, still calls back, and
        // reports nothing: the room then waits out its whole readiness deadline
        // for a client that had finished loading. The subscription goes, because
        // an inactive component must not act on an instruction; the instruction
        // stays, because it is still being carried out.
        //
        // ⛔ Which is a statement about the poll as much as about this method.
        // Keeping the instruction here buys nothing if the first Update after
        // the component comes back reads the same session as a new one, and
        // that is exactly what emptying the facade field used to make it do.
        //
        // ⚠️ What is NOT repaired, and was not before: an instruction ANNOUNCED
        // while this component is deactivated reaches nobody, and the loader
        // comes back with nothing to say about it — no load, no report, and the
        // room waiting out its readiness deadline for a client that was never
        // told. Catching up would mean deciding that re-enabling the component
        // re-issues whatever the room is on, and a title that deactivates it to
        // take the load into its own hands wants the opposite; that is an
        // owner's call, not a repair.
        private void OnDisable()
        {
            Unsubscribe();
            if (s_active == this) s_active = null;
        }

        private void OnDestroy()
        {
            Unsubscribe();
            // ⛔ Here the load really is orphaned: nothing survives to report it,
            // and a token left outstanding would be one a stale callback could
            // still match.
            _sequence.Clear();
            if (s_active == this) s_active = null;
        }

        private void Subscribe()
        {
            _bound.OnSceneLoadStartedWithMode += HandleSceneLoadStarted;
            _subscribed = true;
        }

        // ⚠️ Idempotent, and it has to be: both exits call it, and the poll calls
        // it on the very first frame, before anything has been attached at all.
        // The null test is what carries that case; removing a handler that is
        // not attached is a no-op either way.
        private void Unsubscribe()
        {
            if (_bound != null) _bound.OnSceneLoadStartedWithMode -= HandleSceneLoadStarted;
            _subscribed = false;
        }

        private void HandleSceneLoadStarted(string sceneName, NetworkSceneLoadMode mode)
        {
            // 🚨 A DEACTIVATED loader does not act, and being detached is not
            // what stops it. NetworkSceneManager raises this event over a COPY
            // of its delegate list, so a handler removed while the raise is
            // running still runs — and an application handler registered in
            // Awake sits ahead of this one, so a subscriber that deactivates
            // the loader's root reaches this method on a component that is
            // already disabled. Left ungated it starts a load, and re-takes the
            // slot it released two statements earlier in OnDisable, from which
            // a correctly placed live loader never recovers it.
            if (!_subscribed) return;

            // 🚨 A vacant slot is claimed here rather than left vacant. The
            // holder releases it on its way out and hands it to nobody, so a
            // copy that took over and was then destroyed by the very load it
            // started left the surviving loader enabled, subscribed, and
            // refusing every instruction for the rest of the session — the
            // failure this component exists to remove, arriving through it, in
            // silence, from the placement its own diagnostic warns about.
            //
            // ⛔ Claimed at the instruction rather than on a poll: an
            // announcement that arrives before the next frame would otherwise
            // be dropped, and the room waits out its deadline for a client
            // that was ready to load. Displacement is not reported here — a
            // slot nobody holds is not a second loader.
            if (s_active == null) s_active = this;

            // Only the live loader acts. Both copies are subscribed — each bound
            // its own facade reference — so without this the room's scene is
            // loaded once per copy.
            //
            // ⛔ Ahead of every verdict this method reaches. A guard that merely
            // stands somewhere above the engine call lets every copy reach the
            // branches that report readiness and write diagnostics, so one
            // client answers the room twice and names its placement fault once
            // per copy.
            if (s_active != this) return;

            bool single = mode != NetworkSceneLoadMode.Additive;

            if (single)
            {
                string doomed = SceneLoaderReports.DoomedByItsOwnLoad(
                    gameObject.name, gameObject.scene.name);
                if (doomed != null && WarnGate.ShouldEmit(ref _lastDoomedWarnTicks))
                {
                    Debug.LogError(doomed, this);
                }
            }
            else
            {
                // 🚨 Additive does not deduplicate. Unity loads a SECOND copy of
                // a scene already open, and the SDK documents re-issuing the
                // same scene as the way to restart a round — so the documented
                // gesture, in additive mode, doubles every object in the scene
                // and there is no unload path anywhere in the SDK to undo it.
                // Refused rather than stacked: a room that asked twice for the
                // same additive scene already has what it asked for.
                // 🚨 The LEAF, not the instruction. GetSceneByName takes a
                // scene's own name and nothing else — handed the path form the
                // room is free to use, it finds nothing and answers a scene
                // that is not loaded, so this refusal would be silently absent
                // for exactly the projects that passed a path to tell two
                // same-named scenes apart.
                string stacked = SceneLoaderReports.AlreadyLoadedAdditively(
                    sceneName,
                    SceneManager.GetSceneByName(SceneLoaderReports.LeafName(sceneName)).isLoaded);
                if (stacked != null)
                {
                    if (WarnGate.ShouldEmit(ref _lastStackedWarnTicks))
                    {
                        Debug.LogWarning(stacked, this);
                    }
                    // Already loaded is already ready, and the room is waiting
                    // for a report rather than for a load.
                    Report(sceneName);
                    return;
                }
            }

            long token = _sequence.Begin(sceneName);

            // ⚠️ A throw is a load that did not start, and it is folded into
            // that one answer rather than given a path of its own. Left to
            // propagate it escapes into NetworkSceneManager's raise, which
            // swallows it into one rate-gated line — and the sequence stays
            // outstanding for ever, so LoadingScene names a scene nothing is
            // loading and no later completion can retire it. Two paths for one
            // fault would also mean two console budgets for it, and the second
            // could then silence a different fault entirely.
            AsyncOperation operation = null;
            string thrown = null;
            try
            {
                operation = SceneManager.LoadSceneAsync(
                    sceneName, single ? LoadSceneMode.Single : LoadSceneMode.Additive);
            }
            catch (System.Exception ex)
            {
                thrown = " (" + ex.GetType().Name + ": " + ex.Message + ")";
            }

            string refused = SceneLoaderReports.CouldNotStart(sceneName, operation != null);
            if (refused != null)
            {
                _sequence.Abandon(token);
                if (WarnGate.ShouldEmit(ref _lastRefusedWarnTicks))
                {
                    Debug.LogError(refused + thrown, this);
                }
                return;
            }

            // ⛔ The token travels with the callback rather than being read from
            // the field when it fires. Unity cannot cancel a load already under
            // way, so a superseded one still completes and still calls back —
            // and a callback that read the CURRENT instruction would report
            // readiness for the load the room has moved on from.
            operation.completed += _ => HandleLoadFinished(token, sceneName);
        }

        private void HandleLoadFinished(long token, string loadedScene)
        {
            if (!_sequence.TryComplete(token)) return;
            Report(loadedScene);
        }

        // 🔑 Reports only for the scene that was actually loaded.
        //
        // ReportReady() names the ROOM's current scene, which is not necessarily
        // the one this load produced: the room's scene can change without the
        // announcement reaching this component — NetworkSceneManager withholds
        // the mode-carrying raise when a subscriber switches room mid-raise —
        // and the report would then claim readiness for a scene this client has
        // never loaded. The whole point of the token is knowing which load
        // finished; throwing that away at the last step would make it decoration.
        private void Report(string loadedScene)
        {
            var scene = NetworkManager.TryGetInstance(out var manager) ? manager.Scene : null;
            if (scene == null) return;

            string movedOn = SceneLoaderReports.RoomMovedOn(loadedScene, scene.CurrentScene);
            if (movedOn != null)
            {
                if (WarnGate.ShouldEmit(ref _lastMovedOnWarnTicks)) Debug.LogWarning(movedOn, this);
                return;
            }

            scene.ReportReady();
        }
    }
}
