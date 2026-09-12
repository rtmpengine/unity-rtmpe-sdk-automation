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
    /// The Synchronization Service's fixed room broadcast rate.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>NetworkSettings.tickRate</c>, which governs this client's
    /// own simulation and flush cadence. The two answer different questions: how
    /// often the server emits, and how often this client samples. A record
    /// carrying a server broadcast tick is placed on the timeline this rate
    /// defines; a record carrying an owner input tick is placed on the owning
    /// client's cadence.
    /// </remarks>
    public static class ServerTickRate
    {
        /// <summary>Room broadcast frequency in hertz.</summary>
        public const int Hz = 30;

        /// <summary>Seconds between consecutive room broadcast ticks.</summary>
        public const double IntervalSeconds = 1.0 / Hz;
    }
}
