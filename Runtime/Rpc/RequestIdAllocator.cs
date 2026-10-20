// RTMPE SDK — Runtime/Rpc/RequestIdAllocator.cs
//
// Centralised allocator for the 32-bit request_id field used in RPC requests
// (legacy RpcPacketBuilder and EnhancedRpcPacketBuilder share the same field).
//
// Why a dedicated allocator:
//  • The wire field is caller-supplied; without enforcement, application
//    code can recycle a small counter and let an attacker on the wire
//    race a forged response into the open correlation slot before the
//    real reply arrives.  Sourcing IDs from a CSPRNG raises the bid for
//    such an attack from "increment to N" to "predict 32 random bits".
//  • Pending callbacks need a TTL — without one, an unanswered request
//    leaks its slot indefinitely, and (worst case) a delayed forged
//    reply can correlate against a long-stale request_id.
//
// Wire field is 32 bits, so collision risk after ~2^16 outstanding
// requests reaches the birthday bound (~50 % chance).  In practice the
// pending map is in the tens, well below that bound; the allocator
// re-rolls if it ever picks zero (zero is reserved as "unused" by
// BuildPing fallbacks) or an in-flight ID.
//
// Threading: all members are thread-safe.  RegisterPending / Resolve /
// PurgeExpired use a single lock; ID generation uses RandomNumberGenerator
// which is already thread-safe.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Allocates random RPC request ids and tracks the requests awaiting an answer, each with a
    /// time limit. Used by the SDK; not intended to be called from game code.
    /// </summary>
    /// <remarks>
    /// Ids come from a cryptographic random number generator, so they cannot be predicted.
    /// All members are thread-safe.
    /// </remarks>
    public static class RequestIdAllocator
    {
        /// <summary>
        /// The time limit <see cref="AllocateAndRegister"/> uses when none is given: 30 seconds.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        // RNG instance is thread-safe per .NET docs and reused across calls
        // to avoid the per-allocation overhead of CreateInstance.
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        // Process-wide monotonic millisecond source for deadline tracking.
        // A running Stopwatch is monotonic and immune to NTP / wall-clock
        // adjustments that can make DateTime.UtcNow run backward, and it is
        // available on every Unity scripting backend and API-compatibility
        // level (unlike Environment.TickCount64, which is .NET Standard 2.1+).
        // Only relative deltas are ever compared, so the arbitrary origin is
        // immaterial; reads are thread-safe.
        private static readonly Stopwatch MonotonicClock = Stopwatch.StartNew();

        // Pending registry: id → (deadlineMs, optional timeout callback).
        // Deadline stored as monotonic milliseconds from MonotonicClock.
        private struct Entry
        {
            public long DeadlineMs;
            public Action OnTimeout;
        }

        private static readonly Dictionary<uint, Entry> Pending = new Dictionary<uint, Entry>(64);
        private static readonly object Lock = new object();

        /// <summary>
        /// Returns a new random, non-zero request id that no pending request uses. The id is not
        /// registered; see <see cref="RegisterPending"/>.
        /// </summary>
        public static uint Next()
        {
            Span<byte> buf = stackalloc byte[4];
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Rng.GetBytes(buf);
                uint candidate = (uint)(buf[0]
                                      | (buf[1] << 8)
                                      | (buf[2] << 16)
                                      | (buf[3] << 24));
                if (candidate == 0) continue;

                lock (Lock)
                {
                    if (!Pending.ContainsKey(candidate))
                        return candidate;
                }
            }
            // Extreme bad luck (or a saturated map) — fall through with a
            // non-zero best-effort value.  RNG already gave us something
            // unpredictable; we accept the rare collision over an infinite
            // loop.  PurgeExpired() invocation by the caller is recommended.
            Span<byte> fallback = stackalloc byte[4];
            Rng.GetBytes(fallback);
            uint v = (uint)(fallback[0]
                          | (fallback[1] << 8)
                          | (fallback[2] << 16)
                          | (fallback[3] << 24));
            return v == 0 ? 1u : v;
        }

        /// <summary>
        /// Allocates a request id and registers it as pending.
        /// </summary>
        /// <param name="timeout">How long to wait for the answer. Default
        /// <see cref="DefaultTimeout"/>.</param>
        /// <param name="onTimeout">Called by <see cref="PurgeExpired"/> once the time limit
        /// passes without <see cref="Resolve"/>, or by <see cref="DropPending"/>.</param>
        /// <returns>The request id.</returns>
        public static uint AllocateAndRegister(TimeSpan? timeout = null, Action onTimeout = null)
        {
            uint id = Next();
            RegisterPending(id, timeout ?? DefaultTimeout, onTimeout);
            return id;
        }

        /// <summary>
        /// Registers <paramref name="id"/>, from <see cref="Next"/>, as pending. An id of 0 is
        /// ignored.
        /// </summary>
        /// <param name="id">The request id.</param>
        /// <param name="timeout">How long to wait for the answer.</param>
        /// <param name="onTimeout">Called by <see cref="PurgeExpired"/> once the time limit
        /// passes without <see cref="Resolve"/>, or by <see cref="DropPending"/>.</param>
        public static void RegisterPending(uint id, TimeSpan timeout, Action onTimeout = null)
        {
            if (id == 0) return;
            // Monotonic millisecond deadline — see MonotonicClock above for why
            // a Stopwatch is preferred over DateTime.UtcNow (NTP immunity) and
            // over Environment.TickCount64 (API-compatibility portability).
            long deadline = MonotonicClock.ElapsedMilliseconds + (long)timeout.TotalMilliseconds;
            lock (Lock)
            {
                Pending[id] = new Entry { DeadlineMs = deadline, OnTimeout = onTimeout };
            }
        }

        /// <summary>
        /// Marks a request as answered and stops tracking it.
        /// </summary>
        /// <param name="id">The request id.</param>
        /// <returns><see langword="true"/> when the request was pending.</returns>
        public static bool Resolve(uint id)
        {
            lock (Lock)
            {
                return Pending.Remove(id);
            }
        }

        /// <summary>
        /// Stops tracking every request past its time limit, and calls its timeout callback.
        /// <c>NetworkManager</c> calls it periodically.
        /// </summary>
        /// <remarks>
        /// A callback that throws is logged, and the other callbacks still run.
        /// </remarks>
        /// <returns>The number of requests removed.</returns>
        public static int PurgeExpired()
        {
            long nowMs = MonotonicClock.ElapsedMilliseconds;
            List<Action> callbacks = null;
            int purged = 0;

            lock (Lock)
            {
                if (Pending.Count == 0) return 0;

                List<uint> toRemove = null;
                foreach (var kv in Pending)
                {
                    if (kv.Value.DeadlineMs <= nowMs)
                    {
                        if (toRemove == null) toRemove = new List<uint>();
                        toRemove.Add(kv.Key);
                        if (kv.Value.OnTimeout != null)
                        {
                            if (callbacks == null) callbacks = new List<Action>();
                            callbacks.Add(kv.Value.OnTimeout);
                        }
                    }
                }
                if (toRemove != null)
                {
                    purged = toRemove.Count;
                    foreach (uint id in toRemove) Pending.Remove(id);
                }
            }

            // Fire callbacks outside the lock to avoid reentrancy hazards.
            //
            // Subscriber-isolation discipline: a buggy timeout callback must
            // not abort the sweep across siblings, but it MUST surface a
            // diagnostic so operator dashboards observe the regression.  A
            // bare `catch {}` makes the failure invisible — a callback that
            // throws every invocation looks identical to one that simply
            // does nothing.  Symmetric with the M19-CORE-07 / M19-SYNC-01
            // isolation already adopted across the SDK's hot paths.
            if (callbacks != null)
            {
                foreach (var cb in callbacks)
                {
                    try { cb(); }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError(
                            "[RTMPE] RequestIdAllocator: timeout callback threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Sweep continues " +
                            "with remaining timeouts.");
                    }
                }
            }
            return purged;
        }

        /// <summary>
        /// The number of requests awaiting an answer.
        /// </summary>
        public static int PendingCount
        {
            get { lock (Lock) return Pending.Count; }
        }

        /// <summary>
        /// Stops tracking every request, and calls each one's timeout callback.
        /// <c>NetworkManager</c> calls it when a session ends.
        /// </summary>
        /// <remarks>
        /// A callback that throws is logged, and the other callbacks still run.
        /// </remarks>
        /// <returns>The number of requests removed.</returns>
        public static int DropPending()
        {
            List<Action> callbacks = null;
            int dropped;
            lock (Lock)
            {
                dropped = Pending.Count;
                if (dropped == 0) return 0;
                foreach (var kv in Pending)
                {
                    if (kv.Value.OnTimeout != null)
                    {
                        if (callbacks == null) callbacks = new List<Action>();
                        callbacks.Add(kv.Value.OnTimeout);
                    }
                }
                Pending.Clear();
            }

            if (callbacks != null)
            {
                foreach (var cb in callbacks)
                {
                    try { cb(); }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError(
                            "[RTMPE] RequestIdAllocator.DropPending: timeout callback threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Drain continues.");
                    }
                }
            }
            return dropped;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test seam: clear all pending entries without firing callbacks.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal static void ResetForTest()
        {
            lock (Lock) Pending.Clear();
        }
#endif // UNITY_INCLUDE_TESTS
    }
}
