// RTMPE SDK — Runtime/Rpc/INetworkSerializable.cs
//
// Extension point that lets game code pass user-defined types (typically
// small structs) as RPC parameters.  Without this interface RPCs are limited
// to the nine built-in primitive/Unity types known to RpcSerializer.
//
// Wire integration:
//  • RpcSerializer adds type tag 0x0A "INetworkSerializable".
//  • Encoded record: [tag:1][type_name_len:2 LE][type_name UTF-8][payload …]
//    The type name is the assembly-qualified-free FullName (Namespace.Type).
//    This lets the receiver instantiate the correct concrete type without
//    a separate registry hand-shake — at the cost of ~name-length bytes per
//    parameter.  Apps that send the same type at >1 Hz can pre-register via
//    RpcTypeRegistry to swap the name for a 4-byte hash (Phase 2 — not yet
//    wired through; the on-the-wire fallback is always available).
//
// Failure modes (all surfaced as warnings, never thrown across the read loop):
//  • Unknown type name → null parameter is returned; caller handles a null
//    argument the same way it would handle a malformed packet.
//  • Constructor throws → null parameter is returned, warning logged.
//  • Deserialize() throws → null parameter is returned, warning logged.
//
// Why an interface and not [Serializable]:
//  • Explicit boundary: callers cannot accidentally serialize huge graphs.
//  • Versioning: authors choose how to handle field additions/removals.
//  • Determinism: [Serializable] varies across .NET runtimes; this is stable.

namespace RTMPE.Rpc
{
    /// <summary>
    /// Implement it on a struct or class to use the type as an RPC parameter.
    /// </summary>
    /// <remarks>
    /// <para><see cref="NetworkDeserialize"/> must read the values in the order
    /// <see cref="NetworkSerialize"/> wrote them. It is called on a new instance, so no field
    /// holds a value beforehand. A class needs a public parameterless constructor.</para>
    /// <para>Register the type on every client with <see cref="RpcTypeRegistry.Register{T}"/>
    /// before the first call carrying it arrives. On a client where it is not registered, the
    /// argument arrives as <see langword="null"/> and a warning is logged. When
    /// <see cref="NetworkDeserialize"/> throws or reads past the end of the data, the call is
    /// dropped and a warning is logged.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public struct PlayerScore : INetworkSerializable
    /// {
    ///     public int    Score;
    ///     public float  Accuracy;
    ///     public string PlayerName;
    ///
    ///     public void NetworkSerialize(IRtmpeWriter writer)
    ///     {
    ///         writer.WriteInt32(Score);
    ///         writer.WriteFloat(Accuracy);
    ///         writer.WriteString(PlayerName);
    ///     }
    ///
    ///     public void NetworkDeserialize(IRtmpeReader reader)
    ///     {
    ///         Score      = reader.ReadInt32();
    ///         Accuracy   = reader.ReadFloat();
    ///         PlayerName = reader.ReadString();
    ///     }
    /// }
    ///
    /// // On every client, at start-up:
    /// RpcTypeRegistry.Register&lt;PlayerScore&gt;();
    /// </code>
    /// </example>
    public interface INetworkSerializable
    {
        /// <summary>
        /// Writes this value to <paramref name="writer"/>. Called twice for each argument of
        /// this type when a call is sent: once to measure the value and once to write it.
        /// </summary>
        /// <remarks>
        /// Write the same values both times. If the second pass writes more than the first, the
        /// call is not sent.
        /// </remarks>
        /// <param name="writer">The writer to write the value's fields to.</param>
        void NetworkSerialize(IRtmpeWriter writer);

        /// <summary>
        /// Reads this value from <paramref name="reader"/>, in the order
        /// <see cref="NetworkSerialize"/> wrote it. Called on a new instance for each argument
        /// of this type when a call is received.
        /// </summary>
        /// <param name="reader">The reader to read the value's fields from.</param>
        void NetworkDeserialize(IRtmpeReader reader);
    }
}
