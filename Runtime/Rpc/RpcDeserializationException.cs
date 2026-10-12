// RTMPE SDK — Runtime/Rpc/RpcDeserializationException.cs
//
// Thrown by RpcSerializer when an INetworkSerializable parameter fails to
// deserialise.  An earlier design returned a partial-state instance with
// only a Debug.LogWarning, leaving the dispatcher to decide whether to
// invoke the user method with a corrupt argument.  That leaks the policy
// decision into game code; throwing forces the caller (RPC dispatcher) to
// pick a single explicit policy.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Thrown inside the SDK when a received argument of an <see cref="INetworkSerializable"/>
    /// type cannot be read: its <c>NetworkDeserialize</c> threw, or read past the end of the
    /// data.
    /// </summary>
    /// <remarks>
    /// When it reads a received call, the SDK catches it, drops the whole call and logs a
    /// warning. <see cref="RpcSerializer.ReadParam"/> lets it propagate to its caller.
    /// </remarks>
    public sealed class RpcDeserializationException : Exception
    {
        /// <summary>The type name the received call gave for the argument.</summary>
        public string TypeName { get; }

        /// <inheritdoc/>
        public RpcDeserializationException(string typeName, string message)
            : base(message)
        {
            TypeName = typeName ?? string.Empty;
        }

        /// <inheritdoc/>
        public RpcDeserializationException(string typeName, string message, Exception inner)
            : base(message, inner)
        {
            TypeName = typeName ?? string.Empty;
        }
    }
}
