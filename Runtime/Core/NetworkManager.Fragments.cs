// RTMPE SDK — Runtime/Core/NetworkManager.Fragments.cs
//
// A frame the gateway sent in pieces, put back together and dispatched as if it
// had arrived whole.

using RTMPE.Core.Protocol;
using RTMPE.Protocol;
using UnityEngine;

namespace RTMPE.Core
{
    // All partial declarations of a sealed class must carry the sealed modifier;
    // the C# compiler enforces consistency across translation units.
    public sealed partial class NetworkManager
    {
        // Session-scoped: cleared with the rest of the session's state.
        private readonly FragmentReassembler _fragments = new FragmentReassembler();

        // One budget per diagnostic; both are reached from an inbound packet.
        private long _lastReassembledRefusedWarnTicks;
        private long _lastFrameAbandonedWarnTicks;

        /// <summary>Frames this client received in pieces and put back together.</summary>
        internal long FramesReassembledCount => _fragments.Reassembled;

        /// <summary>Frames this client received part of and let go before the rest arrived.</summary>
        internal long FramesAbandonedCount => _fragments.Abandoned;

        /// <summary>
        /// One authenticated piece of a frame; the frame goes through the receive
        /// path as soon as its last piece is here.
        /// </summary>
        private void OnFragment(byte[] data)
        {
            long abandonedBefore = _fragments.Abandoned;
            byte[] whole = _fragments.Add(PacketParser.ExtractPayload(data));
            // A frame let go is a frame the application never sees, and nothing
            // else on this path reports it.
            if (_fragments.Abandoned != abandonedBefore)
            {
                if (ShouldWarn(ref _lastFrameAbandonedWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] Let go of a frame sent in pieces before all of them arrived " +
                        $"({_fragments.Abandoned} so far): every copy of a piece was lost, or more " +
                        "frames were in flight at once than the client holds part-way.");
            }
            if (whole == null) return;
            if (IsDebugLogEnabled)
                LogDebug($"Reassembled a {whole.Length}-byte frame from fragments.");
            ProcessPacket(whole, whole.Length, reassembled: true);
        }

        /// <summary>
        /// Whether a reassembled frame is refused because the gateway could not
        /// have split it (<see cref="FragmentReassembler.WhyNotSplitByTheGateway"/>).
        /// </summary>
        private bool RefusesReassembled(byte[] data, int length, PacketType type, bool wasEncrypted)
        {
            string why = FragmentReassembler.WhyNotSplitByTheGateway(data, length, type, wasEncrypted);
            if (why == null) return false;
            if (ShouldWarn(ref _lastReassembledRefusedWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] Dropped a frame reassembled from fragments: {type} {why}. " +
                    "A gateway running a different protocol is the likely cause.");
            return true;
        }
    }
}
