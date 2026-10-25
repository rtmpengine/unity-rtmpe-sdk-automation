// RTMPE SDK — Runtime/Core/RtmpeLog.cs
//
// Internal logging facade that respects NetworkSettings.enableDebugLogs.
//
// Motivation:
//  Direct calls to UnityEngine.Debug.LogError are picked up by every crash
//  reporter the host app has installed (Crashlytics, Bugsnag, Unity Cloud
//  Diagnostics, Sentry, etc.) regardless of severity.  Routine transport
//  blips — a peer rebooting, a roaming Wi-Fi handoff, a backgrounded mobile
//  client — must not look like crashes in those dashboards.
//
// Policy:
//  * "Routine" runtime errors (transport exceptions, malformed inbound
//    packets, dropped frames) route through Error()/Warning() which are
//    SUPPRESSED unless enableDebugLogs is true on the active NetworkSettings.
//    When suppressed they are emitted at Debug.Log severity so they remain
//    visible in editor consoles for SDK developers but are not ingested
//    by crash pipelines.
//  * "Fatal" conditions (cryptography failures, configuration errors that
//    prevent any session from starting, nonce exhaustion) continue to call
//    UnityEngine.Debug.LogError directly — those genuinely warrant a crash
//    report because the SDK cannot recover.
//
// Two questions, two answers:
//  The console severity above answers "which pipelines may ingest this",
//  which is a policy about the host app's crash reporting.  It does not
//  answer "how serious is this condition", and a consumer that needs the
//  second question cannot recover it from the first: once suppressed, a
//  transport error and a routine trace are the same LogType.  Downgraded
//  carries the suppressed call at the severity the condition actually holds,
//  for consumers reached out of band rather than through the console.  It is
//  raised on exactly the branch that lowers the console severity, so a
//  subscriber watching Unity's log callback as well sees each fault once.
//
// The gate read is a single volatile bool field deref through a cached
// NetworkSettings reference.  No allocations, no locks.

using System;
using UnityEngine;

namespace RTMPE.Core
{
    internal static class RtmpeLog
    {
        // Cached settings handle.  Set by NetworkManager.Awake and cleared by
        // OnDestroy / ResetStaticState.  A test seam is provided so unit tests
        // can flip the flag without instantiating a full NetworkManager.
        private static volatile NetworkSettings _settings;

        // Test override: when non-null, beats the live _settings reference.
        // Lets edit-mode tests verify gating without spinning a manager.
        private static volatile NetworkSettings _testOverride;

        internal static void SetActiveSettings(NetworkSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Test-only: force the gate to a specific NetworkSettings (or null
        /// to revert).  Tests should pair this with a TearDown that restores
        /// the previous state.
        /// </summary>
        internal static void SetTestOverride(NetworkSettings settings)
        {
            _testOverride = settings;
        }

        /// <summary>
        /// Raised for a routine fault whose console severity has been lowered,
        /// carrying the severity the condition itself holds.  The parameters
        /// are Unity's log-callback shape — condition, stack trace, type — so
        /// one handler can serve this source and the console channel alike.
        /// The stack trace is empty: every call site writes a message that
        /// names the fault, and a synthesised trace would cost the capture
        /// budget of the entries it sits beside.
        ///
        /// Subscribers are reached on the thread that logged, which for the
        /// transport faults is the background I/O thread; a handler must be
        /// safe to run concurrently with itself.
        ///
        /// The severity carried here is the condition's, which means a
        /// consumer that treats an error as urgent will treat these as urgent.
        /// That is deliberate: the console severity is lowered for the host
        /// app's crash reporters, and it is the same fault either way — a
        /// transport error is exactly the entry worth getting off a connection
        /// that is about to drop.  It is also what already happens whenever
        /// verbose logs are on, so the default and the verbose configuration
        /// now agree rather than differing by a category.
        /// </summary>
        internal static event Action<string, string, LogType> Downgraded;

        // Reset on Play-Mode entry so a second run does not inherit the first
        // one's state.  With domain reload disabled these statics outlive the
        // session that populated them, and a subscriber carried across is an
        // object from a torn-down session still being handed faults.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Downgraded = null;
            _settings = null;
            _testOverride = null;
        }

        // Several callers log from inside a catch arm whose whole purpose is
        // that teardown continues afterwards — HandleTransportError and four
        // session-boundary handlers do exactly this. A subscriber that threw
        // would escape the logger into those arms and defeat them, so the raise
        // is isolated. The failure is not reported anywhere: the only channel
        // available for reporting it is the one that has just failed.
        private static void Publish(string message, LogType severity)
        {
            Action<string, string, LogType> subscribers = Downgraded;
            if (subscribers == null) return;
            try { subscribers(message, string.Empty, severity); }
            catch { }
        }

        private static bool DebugLogsEnabled
        {
            get
            {
                var ov = _testOverride;
                if (ov != null) return ov.enableDebugLogs;
                var s = _settings;
                return s != null && s.enableDebugLogs;
            }
        }

        // Gating rule: hot- and warm-path callers should test IsDebugEnabled
        // before building the interpolated string they pass to LogDebug.  The
        // interpolation step allocates a fresh formatted System.String even
        // when the downstream sink later suppresses the message, so any site
        // that runs more than a handful of times per second per session must
        // gate the format work to keep steady-state GC flat.

        /// <summary>
        /// Hot- and warm-path gate.  Callers that build a debug message via
        /// string interpolation should test this property first so the format
        /// step is elided entirely when verbose logging is off — string
        /// interpolation otherwise allocates a fresh formatted
        /// <see cref="System.String"/> per call regardless of whether the
        /// downstream sink consumes it.  At ~600 packets/s the elided
        /// allocations dominate inbound steady-state GC pressure.
        /// </summary>
        public static bool IsDebugEnabled => DebugLogsEnabled;

        /// <summary>
        /// Log a routine runtime error.  Surfaces as Debug.LogError only when
        /// the user has explicitly opted into verbose logs; otherwise emitted
        /// at Debug.Log severity so crash reporters do not ingest it, and
        /// announced on <see cref="Downgraded"/> as an error.
        /// </summary>
        public static void Error(string message)
        {
            if (DebugLogsEnabled)
            {
                Debug.LogError(message);
                return;
            }

            Debug.Log(message);
            Publish(message, LogType.Error);
        }

        /// <summary>
        /// Log a routine warning.  Suppressed (downgraded to Debug.Log) when
        /// the verbose flag is off, and announced on <see cref="Downgraded"/>
        /// as a warning.  Use for peer-induced or transient conditions that do
        /// not represent SDK bugs.
        /// </summary>
        public static void Warning(string message)
        {
            if (DebugLogsEnabled)
            {
                Debug.LogWarning(message);
                return;
            }

            Debug.Log(message);
            Publish(message, LogType.Warning);
        }

        /// <summary>
        /// Informational log.  Emitted only when verbose logs are enabled.
        /// </summary>
        public static void Info(string message)
        {
            if (DebugLogsEnabled) Debug.Log(message);
        }
    }
}
