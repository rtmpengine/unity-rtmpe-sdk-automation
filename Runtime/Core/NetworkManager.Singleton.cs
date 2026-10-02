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
        /// The <see cref="NetworkManager"/> in the scene. <see langword="null"/>, with a
        /// one-time warning, when the scene has none, and after the application starts
        /// quitting. The SDK never creates one for you.
        /// </summary>
        /// <remarks>
        /// Read it from the Unity main thread. Use <see cref="TryGetInstance"/> to check for a
        /// manager without the warning.
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
        /// Like <see cref="Instance"/>, without the warning: <see langword="true"/> when a
        /// manager exists, with the manager in <paramref name="manager"/>.
        /// </summary>
        /// <param name="manager">The manager, or <see langword="null"/> when there is none.</param>
        /// <returns><see langword="true"/> when a manager exists and the application is not
        /// quitting.</returns>
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
        /// The manager an <c>Awake</c> has published, never a walk of the scene
        /// — for a component that asks every frame.
        /// </summary>
        /// <remarks>
        /// ⛔ <see cref="TryGetInstance"/> falls back to <c>FindFirstObjectByType</c>
        /// whenever nothing is published, which is every call in a scene with no
        /// manager — a menu — so the bootstrap, the scene loader and the world
        /// spawner, each asking from <c>Update</c>, walked the scene every frame
        /// apiece.  The walk finds nothing at that point the publication would
        /// not: a live manager's <c>Awake</c> publishes it before any
        /// <c>Update</c> runs, and its <c>OnDestroy</c> withdraws it.  What the
        /// walk is for — a manager asked for from another component's
        /// <c>Awake</c>, ahead of its own — is not a per-frame question.
        /// </remarks>
        internal static bool TryGetPublishedInstance(out NetworkManager manager)
        {
            if (System.Threading.Volatile.Read(ref _applicationIsQuitting))
            {
                manager = null;
                return false;
            }

            var published = System.Threading.Volatile.Read(ref _instance);
            manager = published != null ? published : null;
            return manager != null;
        }

        /// <summary>
        /// Whether a manager exists and the application is not quitting. May be read from any
        /// thread.
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
        //  • The factory is called once per transport built through it: at
        //    InitialiseNetwork() when it was installed before that, otherwise
        //    at the first attempt after it was installed — and again after an
        //    attempt on which it threw or answered null.
        //  • The factory MUST return a non-null, ready-to-Connect() transport.

        /// <summary>
        /// Builds the transport for a connection attempt. It receives the
        /// <see cref="NetworkSettings"/> in use, so it can read the server address and buffer
        /// sizes.
        /// </summary>
        /// <remarks>
        /// The manager owns the transport it returns: it reuses it across attempts and disposes
        /// it when it replaces it or is destroyed. Return a new instance each time, never a
        /// cached one.
        /// </remarks>
        /// <param name="settings">The settings in use.</param>
        /// <returns>A new transport, ready to connect.</returns>
        public delegate RTMPE.Transport.NetworkTransport TransportFactoryFn(NetworkSettings settings);

        private static TransportFactoryFn _transportFactory;

        /// <summary>
        /// Installs a factory that builds the transport the manager connects with, in place of
        /// the built-in <see cref="RTMPE.Transport.UdpTransport"/>: for example a test double,
        /// or a transport of your own.
        /// </summary>
        /// <remarks>
        /// <para>Install it before <see cref="Connect(string)"/> or <see cref="Reconnect"/>. A
        /// factory installed or cleared during a session takes effect at the next attempt.</para>
        /// <para>A factory that returns <see langword="null"/> logs a warning, and one that
        /// throws logs the exception; either way the attempt uses the built-in transport.</para>
        /// <para>A transport replaces the socket below the SDK's network thread, not the
        /// thread, so a factory does not make a WebGL build work.</para>
        /// </remarks>
        /// <param name="factory">The factory, or <see langword="null"/> to restore the built-in
        /// transport.</param>
        public static void SetTransportFactory(TransportFactoryFn factory) => _transportFactory = factory;

        /// <summary>
        /// Removes the installed transport factory, so the next connection attempt uses the
        /// built-in <see cref="RTMPE.Transport.UdpTransport"/>.
        /// </summary>
        public static void ClearTransportFactory() => _transportFactory = null;

        /// <summary>Whether a transport factory is installed.</summary>
        public static bool HasCustomTransportFactory => _transportFactory != null;

        // ── Transport shaping ──────────────────────────────────────────────────
        //
        // A second seam beside the factory, not a use of it: a shaper is
        // applied over whatever transport a session was going to run on — the
        // installed factory's or the built-in UDP one — so the Editor's Link
        // Simulator can shape the link without owning, replacing or restoring
        // a project's factory, and a project's own SetTransportFactory call
        // cannot silently unseat it.  Internal, because the Editor bench is
        // its only caller; a harness of the project's own composes a
        // SimulatedLinkTransport inside its factory instead.

        /// <summary>
        /// Wraps the transport a session was going to run on.  Must answer a
        /// transport ready to <c>Connect()</c> that owns the one it was given,
        /// or <see langword="null"/> to leave it unshaped.
        /// </summary>
        internal delegate RTMPE.Transport.NetworkTransport TransportShaperFn(RTMPE.Transport.NetworkTransport built);

        private static TransportShaperFn _transportShaper;

        /// <summary>
        /// Install a transport shaper, or clear it with <see langword="null"/>.
        /// Like a factory change it takes effect at the next connection
        /// attempt, not on the session in hand.
        /// </summary>
        internal static void SetTransportShaper(TransportShaperFn shaper) => _transportShaper = shaper;

        /// <summary>True when a transport shaper is installed for the next attempt.</summary>
        internal static bool HasTransportShaper => _transportShaper != null;

    }
}
