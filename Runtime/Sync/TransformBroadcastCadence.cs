// RTMPE SDK — Runtime/Sync/TransformBroadcastCadence.cs
//
// Cadence gate for an owning NetworkTransform's outbound broadcast.  The owner
// samples its transform every visual frame, but the authoritative tick engine
// coalesces owner state to a single sample per simulation tick and rebroadcasts
// at that rate; a per-frame send therefore only spends the per-session state
// budget on poses the server discards, and at high frame rates overruns that
// budget so the surplus is dropped — surfacing as jitter.  This predicate caps
// the send to the tick cadence, matching the input-batch gate in the same
// component.  Kept UnityEngine-free so it is exercisable from the headless
// dotnet xunit runner.

namespace RTMPE.Sync
{
    internal static class TransformBroadcastCadence
    {
        /// <summary>
        /// Returns <see langword="true"/> when a broadcast is due: none has been
        /// sent yet, or the simulation tick has advanced since the last one.
        /// Inequality (not <c>&gt;</c>) is used so a tick reset or a uint wrap on
        /// reconnect still releases the next sample rather than stalling it.
        /// </summary>
        internal static bool TickAdvanced(bool hasLastTick, uint currentTick, uint lastTick)
            => !hasLastTick || currentTick != lastTick;

        /// <summary>
        /// Whether this frame may offer a tick-cadence broadcast: any frame,
        /// unless the frame's transforms travel as one batch (audit P3-E3) — then
        /// only the first frame after the simulation tick moved, so every object
        /// owed a broadcast in a tick offers it in the same frame and they leave
        /// as one datagram.  Offered in whichever frame each came due, a host's
        /// objects spread a tick over as many datagrams as it has frames — up to
        /// five a tick at 144 fps, past the server's per-session message budget.
        /// An object that comes due mid-tick waits for the next tick's frame,
        /// under one tick, and sends the pose it has then.
        /// </summary>
        /// <param name="batching">Whether the gateway reads a batch.</param>
        /// <param name="tickMovedSincePreviousUpdate">
        /// Whether the tick moved since the component's previous <c>Update</c> —
        /// the component's own reading, so it holds whatever order it and the
        /// manager run in.
        /// </param>
        internal static bool OnTheTickBoundary(bool batching, bool tickMovedSincePreviousUpdate)
            => !batching || tickMovedSincePreviousUpdate;

        /// <summary>
        /// Returns <see langword="true"/> when an idle owner should emit a
        /// keepalive: none has been sent yet, or at least
        /// <paramref name="keepaliveTicks"/> simulation ticks have elapsed since
        /// the last send.  An unchanged pose is otherwise never broadcast, so
        /// without this the tick engine ages the object out of its state set and
        /// a late joiner receives no pose for it until the owner next moves.
        /// Unsigned subtraction measures the elapsed span and wraps correctly
        /// across a uint rollover; a tick reset (<paramref name="currentTick"/>
        /// below <paramref name="lastTick"/>) yields a large span and simply
        /// releases the next keepalive, which is harmless.
        /// </summary>
        internal static bool KeepaliveDue(
            bool hasLastTick, uint currentTick, uint lastTick, uint keepaliveTicks)
            => !hasLastTick || (currentTick - lastTick) >= keepaliveTicks;

        /// <summary>
        /// Wall-clock keepalive floor: <see langword="true"/> when at least
        /// <paramref name="keepaliveSeconds"/> of real time have elapsed since the
        /// last send.  <see cref="KeepaliveDue"/> is measured in simulation ticks,
        /// but the tick cursor can lag real time — the sim catch-up loop caps a
        /// long frame at a fixed tick budget and drops the surplus, so a severe
        /// hitch (GC, synchronous load, mobile foreground-resume) or a sustained
        /// sub-tick frame rate stretches the tick-based keepalive's wall-clock
        /// period.  The server ages an object out on a WALL-CLOCK timeout, so a
        /// purely tick-based keepalive can let a present owner's idle object be
        /// evicted.  This floor anchors the refresh to real time so that never
        /// happens while the owner is running.  Requires a prior send
        /// (<paramref name="hasLastSend"/> — the first broadcast is released by
        /// the tick path); <paramref name="keepaliveSeconds"/> MUST stay below the
        /// server's per-object stale timeout.
        /// </summary>
        internal static bool KeepaliveDueWallClock(
            bool hasLastSend, double nowSeconds, double lastSendSeconds, double keepaliveSeconds)
            => hasLastSend && (nowSeconds - lastSendSeconds) >= keepaliveSeconds;
    }
}
