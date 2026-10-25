using RTMPE.Protocol;
using RTMPE.Rpc;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        private string ReadHeldPrefabTransferOwner(byte[] data)
        {
            if (!RpcPacketParser.TryParseRequest(PacketParser.ExtractPayload(data), out var request)
                || request.MethodId != RpcMethodId.TransferOwnership
                || request.SenderId == 0UL
                || (request.SenderId == _localPlayerId && !IsMasterClient)
                || !RTMPE.Core.Rpc.OwnershipTransfer.TryReadPayload(request.Payload, out _, out string newOwner)
                || !IsPlayerInCurrentRoom(newOwner))
                return null;
            return newOwner;
        }

        private void UpdateHeldPrefabOwner(ulong objectId, byte[] data)
        {
            // Only admissible grants affect lifetime; every frame still waits
            // for the authority check at release.
            string newOwner = ReadHeldPrefabTransferOwner(data);
            if (newOwner != null) _spawnManager?.TransferHeldPrefabSpawn(objectId, newOwner);
        }

        internal string HeldPrefabTransferOwner(ulong objectId)
            => _heldTransfers.LastMatchingValue(objectId, ReadHeldPrefabTransferOwner);
    }
}
