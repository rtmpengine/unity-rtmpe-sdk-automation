using System.Collections.Generic;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>A single proposed RPC the guard resolves and validates.</summary>
    public sealed class PlannedRpc
    {
        public PlannedRpc(string typeName, string methodName)
        {
            TypeName = typeName;
            MethodName = methodName;
        }

        public string TypeName { get; }

        public string MethodName { get; }
    }

    /// <summary>Why a resolved method id was refused.</summary>
    public enum CollisionKind
    {
        /// <summary>The id matches one the runtime reserves for a built-in message.</summary>
        Reserved,

        /// <summary>The id duplicates an earlier method on the same type.</summary>
        IntraType,
    }

    /// <summary>A resolved method id with its provenance and hex rendering.</summary>
    public sealed class IdEntry
    {
        public IdEntry(string typeName, string methodName, uint id)
        {
            TypeName = typeName;
            MethodName = methodName;
            Id = id;
        }

        public string TypeName { get; }

        public string MethodName { get; }

        /// <summary>The <c>"TypeName.MethodName"</c> string the id is derived from.</summary>
        public string Provenance => TypeName + "." + MethodName;

        public uint Id { get; }

        /// <summary>The id rendered as the runtime renders it in messages.</summary>
        public string IdHex => "0x" + Id.ToString("X8");
    }

    /// <summary>A refused method id together with the reason it was refused.</summary>
    public sealed class Collision
    {
        public Collision(CollisionKind kind, string typeName, string methodName, uint id, string priorMethod)
        {
            Kind = kind;
            TypeName = typeName;
            MethodName = methodName;
            Id = id;
            PriorMethod = priorMethod;
        }

        public CollisionKind Kind { get; }

        public string TypeName { get; }

        public string MethodName { get; }

        public uint Id { get; }

        public string IdHex => "0x" + Id.ToString("X8");

        /// <summary>The earlier method sharing the id for an intra-type collision; otherwise null.</summary>
        public string PriorMethod { get; }
    }

    /// <summary>The outcome of resolving and validating a plan.</summary>
    public sealed class PlanVerdict
    {
        public PlanVerdict(bool accepted, IReadOnlyList<IdEntry> ids, IReadOnlyList<Collision> collisions)
        {
            Accepted = accepted;
            Ids = ids;
            Collisions = collisions;
        }

        /// <summary>False when the plan contains at least one collision.</summary>
        public bool Accepted { get; }

        /// <summary>Every resolved id, in the order the plan proposed them.</summary>
        public IReadOnlyList<IdEntry> Ids { get; }

        /// <summary>Every refused id, in the order it was encountered.</summary>
        public IReadOnlyList<Collision> Collisions { get; }
    }
}
