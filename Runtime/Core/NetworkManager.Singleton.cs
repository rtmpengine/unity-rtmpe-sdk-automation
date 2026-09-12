// RTMPE SDK — Runtime/Core/NetworkManager.Singleton.cs
//
// Singleton contract + static instance plumbing + pluggable transport factory.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
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
        // ── Singleton ──────────────────────────────────────────────────────────
        private static NetworkManager  _instance;
        // Not `volatile` — instead every read site uses Volatile.Read and every
        // write site uses Volatile.Write.  The `volatile` keyword has undefined
        // semantics under IL2CPP on ARM (the C++ compiler is not required to honour
        // C# acquire/release on `volatile` static fields), while the explicit
        // System.Threading.Volatile API is spec-guaranteed to emit the correct
        // barriers on every backend.
        private static bool            _applicationIsQuitting;
        private static readonly object _instLock = new object();

        // Reset static state on each Play-Mode entry (or standalone restart) so that
        // a second Play in the same Editor session gets a clean singleton.
        // SubsystemRegistration fires before Awake and before any scene is loaded.
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (_instLock)
            {
                _instance              = null;
                System.Threading.Volatile.Write(ref _applicationIsQuitting, false);
                _missingInstanceWarned = 0;
                // Re-arm the peer-admission advisory across an editor domain
                // reload that bypasses ClearSessionData, so the first session of
                // a fresh Play run still emits the one-time roster-anchor warning
                // instead of inheriting a latched-quiet state from the prior run.
                System.Threading.Interlocked.Exchange(ref _peerAdmissionAdvisoryEmitted, 0);
                // Same reasoning for the remote-motion-timing pairing advisory.
                // Its latch has only two slots — one per inconsistent pairing —
                // so it cannot re-arm on its own.  Without this, a developer who
                // corrects the configuration and re-enters Play with domain
                // reload disabled sees silence whether they fixed it or not.
                RTMPE.Core.Diagnostics.RemoteMotionTimingAdvisory.ResetLatch();
                // And the interpolator advisory, for a different reason from
                // the one above.  Its per-object key DOES re-arm on its own —
                // a fresh Play run gets a fresh gateway session id, which the
                // composed object id mixes in — so this is not about a
                // developer seeing silence after a fix.  It is that the set of
                // surfaced ids otherwise survives play-mode exit and grows
                // across every run of the editor session.
                RTMPE.Core.Diagnostics.RemoteInterpolatorAdvisory.ResetLatch();
                // Do NOT clear _transportFactory here — tests and custom-transport bootstraps
                // install it once at module init, before any singleton is created.
                // Clearing would break that install-then-play sequence.  Users
                // who need to reset it can call ClearTransportFactory() explicitly.
            }
        }

        // One-shot warning latch so a noisy caller does not spam the console
        // every frame.  Reset whenever a real instance becomes available.
        private static int _missingInstanceWarned;

        /// <summary>
        /// Singleton instance. Returns the scene-placed <see cref="NetworkManager"/>
        /// (or one previously assigned via Awake) if one exists; otherwise returns
        /// <see langword="null"/> and emits a one-time warning instructing the caller
        /// to add a NetworkManager to the scene before subscribing to events.
        /// Returns <see langword="null"/> after <c>OnApplicationQuit</c>.
        /// <b>MUST be called from the Unity main thread.</b>
        /// </summary>
        /// <remarks>
        /// The previous behaviour auto-created a hidden GameObject backed by
        /// <c>NetworkSettings.CreateDefault()</c>.  That allowed any component
        /// whose <c>Awake</c> touched <c>NetworkManager.Instance</c> (e.g. a
        /// subscriber registering for <see cref="OnConnected"/>) to spin up a
        /// permanent stand-in with empty crypto material — a configuration
        /// footgun that surfaced only as silent handshake failures.
        /// Use <see cref="TryGetInstance"/> for non-fatal probing.
        /// </remarks>
        public static NetworkManager Instance
        {
            get
            {
                if (System.Threading.Volatile.Read(ref _applicationIsQuitting)) return null;

                // Fast-path: a scene-placed Awake has already published _instance.
                // Volatile.Read pairs with the release-barrier on the lock-protected
                // writes (Awake / OnDestroy / ResetStaticState / the fallback below)
                // so a background-thread reader observes the published reference
                // without taking the Monitor.  This is the steady-state path that
                // must remain free of cross-thread contention.
                var cached = System.Threading.Volatile.Read(ref _instance);
                if (cached != null) return cached;

                // FindFirstObjectByType is a Unity engine call and is only
                // valid on the main thread; calling it off-thread raises
                // UnityException ("get_isPlayingOrWillChangePlaymode can only
                // be called from the main thread") which is hostile to
                // background callers (transport threads, dispatcher producers)
                // probing the singleton during teardown.  Off-thread callers
                // get a quiet null instead — the on-thread bootstrap is the
                // authoritative producer of _instance via Awake.
                if (!RTMPE.Threading.MainThreadDispatcher.IsMainThread)
                    return null;

                NetworkManager found;
                lock (_instLock)
                {
                    if (System.Threading.Volatile.Read(ref _applicationIsQuitting)) return null;
                    // Re-check inside the lock — another main-thread caller may
                    // have populated _instance between our fast-path read and
                    // the lock acquisition.
                    if (_instance != null) return _instance;

                    // FindFirstObjectByType — Unity 6 replacement for deprecated FindObjectOfType.
                    // Adopt a scene-placed manager whose Awake has not yet run
                    // (e.g. an early Awake from a sibling component on the same frame).
                    found = FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Exclude);
                    if (found != null)
                    {
                        _instance = found;
                        System.Threading.Interlocked.Exchange(ref _missingInstanceWarned, 0);
                        return found;
                    }
                }

                // Outside the lock: a single warning per missing-instance episode.
                if (System.Threading.Interlocked.CompareExchange(ref _missingInstanceWarned, 1, 0) == 0)
                {
                    Debug.LogWarning(
                        "[RTMPE] NetworkManager.Instance accessed before any NetworkManager " +
                        "exists in the scene. Add a NetworkManager component to a scene " +
                        "GameObject (or instantiate one explicitly) before subscribing to " +
                        "events or calling Connect(). Returning null.");
                }
                return null;
            }
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test-only: clear the one-shot missing-instance warning latch so each
        /// test that exercises the no-manager path can assert exactly one
        /// warning regardless of preceding fixtures in the same Play Mode run.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined so the
        /// shipped Player assembly does not expose a mutator on a
        /// process-wide warning latch.
        /// </summary>
        internal static void ResetMissingInstanceWarningForTests()
        {
            System.Threading.Interlocked.Exchange(ref _missingInstanceWarned, 0);
        }
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Non-throwing accessor for callers that want to probe for a manager
        /// without producing a console warning.  Returns <see langword="true"/>
        /// when a valid instance exists and assigns it to <paramref name="manager"/>.
        /// </summary>
        public static bool TryGetInstance(out NetworkManager manager)
        {
            if (System.Threading.Volatile.Read(ref _applicationIsQuitting))
            {
                manager = null;
                return false;
            }

            // Fast-path: cached publication from Awake (volatile read pairs
            // with the lock-protected write barrier in Awake / OnDestroy).
            var cached = System.Threading.Volatile.Read(ref _instance);
            if (cached != null)
            {
                manager = cached;
                return true;
            }

            // FindFirstObjectByType requires the main thread; a background
            // caller that arrives here gets a quiet false rather than a
            // UnityException from deep inside the engine.
            if (!RTMPE.Threading.MainThreadDispatcher.IsMainThread)
            {
                manager = null;
                return false;
            }

            lock (_instLock)
            {
                if (System.Threading.Volatile.Read(ref _applicationIsQuitting))
                {
                    manager = null;
                    return false;
                }
                if (_instance != null)
                {
                    manager = _instance;
                    return true;
                }
                manager = FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Exclude);
                if (manager != null) _instance = manager;
                return manager != null;
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if a valid instance exists and the application
        /// has not begun quitting. Thread-safe; no side effects.
        /// </summary>
        public static bool HasInstance
        {
            get
            {
                lock (_instLock) { return _instance != null && !System.Threading.Volatile.Read(ref _applicationIsQuitting); }
            }
        }

        // ── Transport factory (pluggable) ──────────────────────────────────────
        //
       // The SDK ships with a UDP-only transport that uses System.Net.Sockets
        // directly.  That is correct on every standalone platform (Windows,
        // macOS, Linux, Android, iOS) because the Rust gateway speaks UDP+KCP.
        //
       // The hook exists for tests, which inject a deterministic loopback, and
        // for a project that must reach the gateway some other way.  ⚠️ Only
        // half of "some other way" is here: the stock deployment speaks UDP and
        // KCP, so a transport of another kind needs a server endpoint to match.
        //
       // ⛔ It is not a platform escape hatch, and WebGL is the case that shows
        // why.  The browser sandbox has no raw UDP socket, which a transport
        // could answer — but the layer ABOVE the transport is a dedicated
        // background thread (`NetworkThread`), and the WebGL player has no
        // threads at all.  A factory replaces the socket and leaves that thread
        // exactly where it was, so the platform stays out of reach.  Both public
        // entry points refuse there; see `PlatformRefusesNetworking`.
        //
       // Invariants:
        //  • Assigning replaces any previous factory; assign before Connect().
        //  • A null factory (the default) selects the built-in UdpTransport.
        //  • The factory is called exactly once per InitialiseNetwork().
        //  • The factory MUST return a non-null, ready-to-Connect() transport.

        /// <summary>
        /// Delegate signature for custom transport factories.
        /// Receives the active <see cref="NetworkSettings"/> so the factory
        /// can read host/port/buffer fields.  The returned transport is owned
        /// by the resulting <see cref="NetworkThread"/> and disposed when the
        /// manager is cleaned up.
        /// </summary>
        public delegate RTMPE.Transport.NetworkTransport TransportFactoryFn(NetworkSettings settings);

        private static TransportFactoryFn _transportFactory;

        /// <summary>
        /// Install a custom transport factory (e.g. a mock transport for
        /// integration tests, or a project's own UDP stack).  Pass
        /// <see langword="null"/> to restore the built-in UDP transport.
        /// </summary>
        /// <remarks>
        /// Set this BEFORE calling <see cref="Connect"/> or
        /// <see cref="Reconnect"/>.  Changing the factory after the manager
        /// has initialised does NOT re-create the live transport — call
        /// <see cref="Disconnect"/> first.
        /// <para>
        /// A factory does not extend the SDK to a platform it does not support.
        /// It replaces the socket beneath the background network thread, not the
        /// thread — which is why installing one does not make a WebGL build
        /// work.
        /// </para>
        /// </remarks>
        public static void SetTransportFactory(TransportFactoryFn factory) => _transportFactory = factory;

        /// <summary>
        /// Remove any installed transport factory, restoring the built-in
        /// <see cref="RTMPE.Transport.UdpTransport"/>.
        /// </summary>
        public static void ClearTransportFactory() => _transportFactory = null;

        /// <summary>True when a custom transport factory is installed.</summary>
        public static bool HasCustomTransportFactory => _transportFactory != null;

    }
}
