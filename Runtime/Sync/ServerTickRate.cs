// RTMPE SDK — Runtime/Sync/ServerTickRate.cs
//
// The rate at which the Synchronization Service emits room broadcasts.
//
//  • Source of truth: modules/synchronization/internal/tick/engine.go — TickRateHz
//  • Contract:        shared/contracts/flatbuffers/messages.fbs — "all rooms run
//                     at a fixed 30 Hz"; CreateRoomRequest.tick_rate is reserved
//                     and not consumed by the Room Service.
//
// ⚠  SYNC RULE: this value is a property of the server, not a client preference.
//    It is mirrored here so the receive path can convert a server broadcast tick
//    into elapsed time without consulting any client-side setting, and it must
//    change only together with the Go constant above.

namespace RTMPE.Sync
{
    /// <summary>
    /// The fixed rate at which the RTMPE server broadcasts room state.
    /// </summary>
    /// <remarks>
    /// It is independent of <c>NetworkSettings.tickRate</c>, which sets how often
    /// this client runs its own tick and sends changes. An update timed by the
    /// server's broadcast tick is placed on the timeline this rate defines.
    /// </remarks>
    public static class ServerTickRate
    {
        /// <summary>Room state broadcasts per second.</summary>
        public const int Hz = 30;

        /// <summary>Seconds between two room state broadcasts.</summary>
        public const double IntervalSeconds = 1.0 / Hz;
    }
}
