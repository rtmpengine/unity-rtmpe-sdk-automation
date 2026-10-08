// RTMPE SDK — Runtime/Rpc/RpcCaller.cs
//
// Who may invoke an [RtmpeRpc] method on the clients that receive it, and the
// facts about a call's caller that the gateway writes into the Enhanced RPC
// header's rpc_flags byte.
//
// A receiver cannot answer "who called this?" by itself: the caller reaches it
// as a gateway session id, while the roster and every ownership record it holds
// name players.  The gateway holds both, so it answers for every frame it
// relays — it discards whatever the caller wrote into rpc_flags and writes what
// it knows instead (CapabilityFlags.AttestedRpcCaller says it does).  The
// server bit is never relayed from a client: it reaches a receiver only on a
// frame the server published to the room itself, with sender id zero.
//
// Wire bits (Enhanced RPC header byte 25), mirrored by
// modules/gateway/src/nats/forwarder.rs (RPC_CALLER_*) and the Room Service's
// internal/serverfunc/contract.go (Caller*), which writes the server bit on a
// server function's broadcasts and reads the ownership bit into the call:
//   0x01  OwnsObject — the gateway's registry names the caller as the owner of
//                      the object the call addresses
//   0x02  IsHost     — the caller is the room's recorded master client
//   0x04  IsServer   — the server wrote the frame (sender id 0)

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Who may call an <see cref="RtmpeRpcAttribute"/> method. Declare it with
    /// <see cref="RtmpeRpcAttribute.Caller"/>.
    /// </summary>
    /// <remarks>
    /// The caller's own SDK refuses a call it may not make, and every receiving client checks
    /// the declaration against what the server reports about the caller, never against
    /// anything the caller wrote. The server satisfies every declaration.
    /// </remarks>
    public enum RpcCaller : byte
    {
        /// <summary>
        /// Any member of the room. The default.
        /// </summary>
        Anyone = 0,

        /// <summary>
        /// The owner of the object the call addresses, or the server.
        /// </summary>
        Owner = 1,

        /// <summary>
        /// The room's host, or the server.
        /// </summary>
        Host = 2,

        /// <summary>
        /// Only the server: a call your server function sends to the room. No client can make
        /// it, the host included.
        /// </summary>
        Server = 3,
    }

    /// <summary>
    /// What the server reports about the caller of a received call. Read it inside a handler
    /// through <c>NetworkBehaviour.CurrentRpcCaller</c> or
    /// <c>NetworkManager.CurrentRpcCallerFacts</c>.
    /// </summary>
    [Flags]
    public enum RpcCallerFacts : byte
    {
        /// <summary>Nothing is reported about the caller.</summary>
        None = 0,

        /// <summary>
        /// The caller owns the object the call addressed.
        /// </summary>
        OwnsObject = 0x01,

        /// <summary>The caller is the room's host.</summary>
        IsHost = 0x02,

        /// <summary>
        /// The server made the call; no client did.
        /// </summary>
        IsServer = 0x04,
    }

    /// <summary>
    /// What the facts about the call executing now say when they are read from
    /// one object.
    /// </summary>
    internal static class RpcCallerFactsScope
    {
        /// <summary>
        /// <paramref name="facts"/>, attested about a call that addressed the
        /// object <paramref name="addressedObjectId"/>, as read from the object
        /// <paramref name="readerObjectId"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="RpcCallerFacts.OwnsObject"/> is a fact about the object the
        /// call addressed, and about no other: a handler that calls into a
        /// second object — <c>chest.Open()</c> from inside
        /// <c>Player.Interact()</c> — would otherwise have that object read the
        /// caller as ITS owner, for any caller who owns their own player.  The
        /// other two facts are about the caller and the call, and hold wherever
        /// they are read.  Zero is no object, and addresses nothing.
        /// </remarks>
        internal static RpcCallerFacts ReadFrom(
            RpcCallerFacts facts, ulong addressedObjectId, ulong readerObjectId)
            => readerObjectId != 0 && readerObjectId == addressedObjectId
                ? facts
                : facts & ~RpcCallerFacts.OwnsObject;
    }
}
