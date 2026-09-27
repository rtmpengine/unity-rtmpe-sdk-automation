using System.Collections.Generic;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The delivery audience an emitted <c>[RtmpeRpc(...)]</c> names — a
    /// tooling mirror of the runtime's <c>RTMPE.Rpc.RpcTarget</c> enum, carried
    /// here so the authoring core stays free of the Unity-bound runtime
    /// assembly. Names and byte values are pinned equal to the runtime enum by
    /// a drift test in the RPC shard; the member the transform writes into the
    /// attribute is the name, so the two must never diverge.
    /// </summary>
    public enum RpcAudience : byte
    {
        /// <summary>Every client in the room, including the sender.</summary>
        All = 0x00,

        /// <summary>Every client in the room except the sender.</summary>
        Others = 0x01,

        /// <summary>
        /// The server only — the safe default for a state-mutating handler: a
        /// receiving client never executes a Server-declared method, so a wrong
        /// choice fails closed to a no-op instead of a client-authoritative write.
        /// </summary>
        Server = 0x02,

        /// <summary>
        /// Every client plus the server-side replay buffer for late joiners.
        /// The highest-blast audience; the tooling never selects it — it is
        /// only ever written from an explicit human designation.
        /// </summary>
        AllBuffered = 0x03,
    }

    /// <summary>One method's Enhanced-RPC conversion, audience already chosen.</summary>
    public sealed class PlannedRpcEmission
    {
        public PlannedRpcEmission(string methodName, RpcAudience audience)
            : this(methodName, audience, replicaApply: false)
        {
        }

        public PlannedRpcEmission(string methodName, RpcAudience audience, bool replicaApply)
        {
            MethodName = methodName;
            Audience = audience;
            ReplicaApply = replicaApply;
        }

        public string MethodName { get; }

        /// <summary>
        /// The audience the attribute names. Chosen by the host's policy layer
        /// (explicit designation, or the Server safe-default) — the transform
        /// writes it verbatim and never decides it.
        /// </summary>
        public RpcAudience Audience { get; }

        /// <summary>
        /// True when the author designated the method as one that applies
        /// state on the receiving replicas: the sender is the authority, so the
        /// body carries no owner guard on purpose — a replica is never the
        /// owner — and the transform holds every send site in the file to an
        /// authority guard instead of asking the body for one. Never inferred:
        /// it is the host's <c>--replica-apply</c> flag and the wizard's
        /// checkbox, and nothing else.
        /// </summary>
        public bool ReplicaApply { get; }
    }

    /// <summary>
    /// The input the RPC generation transform executes. Ids are deliberately
    /// absent: an RPC method id is a pure function of the type and method name
    /// (FNV-1a), so the emitted source never carries one — the wire id is
    /// derived identically by the runtime at first spawn.
    /// </summary>
    public sealed class RpcPlan
    {
        public RpcPlan(IReadOnlyList<PlannedRpcEmission> emissions)
        {
            Emissions = emissions;
        }

        public IReadOnlyList<PlannedRpcEmission> Emissions { get; }
    }

    /// <summary>The outcome of recording resolved RPC ids into a ledger.</summary>
    public sealed class RpcRecordResult
    {
        public RpcRecordResult(
            LedgerDocument updatedLedger,
            IReadOnlyList<string> errors,
            IReadOnlyList<string> warnings)
        {
            UpdatedLedger = updatedLedger;
            Errors = errors;
            Warnings = warnings;
        }

        /// <summary>False when any entry conflicts with the recorded provenance.</summary>
        public bool Accepted => Errors.Count == 0;

        /// <summary>The document to serialize; null when the record was refused.</summary>
        public LedgerDocument UpdatedLedger { get; }

        public IReadOnlyList<string> Errors { get; }

        public IReadOnlyList<string> Warnings { get; }
    }
}
