using RTMPE.Rpc;

namespace RTMPE.Core.Rpc
{
    /// <summary>
    /// One warn-rate gate per failure reason, for responses nothing on the send
    /// side is waiting for.
    /// </summary>
    /// <remarks>
    /// 🔑 One each rather than one shared, for the reason the serializer's own
    /// gates give: the reasons name different repairs — bind a handler for that
    /// id, grant the caller, fix the handler, shrink the payload — and a project
    /// sitting on any one of them permanently would otherwise spend a shared
    /// gate every second and hide the rest indefinitely. That is not a remote
    /// case here: until a deployment binds a server handler, every
    /// Server-targeted call returns the same reason, for ever.
    ///
    /// <para>The slot mapping is exhaustive by test rather than by hope — every
    /// member of the code enum must map somewhere distinct, so a code added
    /// later fails a test instead of quietly sharing a neighbour's budget. It is
    /// a switch and not an index because the wire's catch-all member is
    /// <c>0xFFFF</c>, and an array indexed by the enum's value would be sixty-five
    /// thousand longs to hold six.</para>
    /// </remarks>
    internal sealed class RpcFailureGates
    {
        /// <summary>Distinct budgets, one per reason the wire can name.</summary>
        internal const int SlotCount = 6;

        private readonly long[] _ticks = new long[SlotCount];

        /// <summary>
        /// True when this reason may be reported now. Each reason is held to one
        /// line per second on its own budget.
        /// </summary>
        internal bool ShouldWarn(RpcErrorCode code)
            => WarnGate.ShouldEmit(ref _ticks[SlotOf(code)]);

        /// <summary>
        /// The budget a reason spends. Deliberately total: an unmapped code
        /// would share, silently, which is the property this class exists to
        /// refuse — so the default slot is the catch-all member's own, and the
        /// test that every member maps distinctly is what keeps it honest.
        /// </summary>
        internal static int SlotOf(RpcErrorCode code)
        {
            switch (code)
            {
                case RpcErrorCode.OK:               return 0;
                case RpcErrorCode.Unauthorized:     return 1;
                case RpcErrorCode.UnknownMethod:    return 2;
                case RpcErrorCode.HandlerError:     return 3;
                case RpcErrorCode.OversizedPayload: return 4;
                default:                            return 5;
            }
        }
    }
}
