// RTMPE SDK — Runtime/Rpc/RtmpeRpcAttribute.cs
//
// Marks a method on a NetworkBehaviour as callable over the network.
// The RpcRegistry discovers [RtmpeRpc] methods via reflection on first access
// and assigns each a stable FNV-1a method ID derived from "TypeName.MethodName".
//
// Who may call a method is a second, optional declaration — Caller (see
// RpcCaller.cs) — enforced by every receiving client from what the gateway
// attests about the call.
//
// Rules for [RtmpeRpc] methods:
//  • Must be declared on a class that inherits NetworkBehaviour.
//  • Must be public instance methods (not static, not abstract).
//  • Parameters must be types supported by RpcSerializer:
//      int, float, bool, string, byte[], ulong, Vector3, Color, Quaternion,
//      or any user-defined type implementing INetworkSerializable
//      (recommended: small structs with a public parameterless constructor).
//  • The FNV-1a hash of "RuntimeTypeFullName.MethodName" must not collide with
//    any reserved manual method ID listed in RpcMethodId (100, 200, 300, 301,
//    400, 401).  A collision causes a startup error via RpcRegistry.Validate().

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Marks a <c>NetworkBehaviour</c> method as an RPC: a method one client calls with
    /// <c>NetworkBehaviour.RPC</c> and other clients, or your server function, run.
    /// </summary>
    /// <remarks>
    /// <para>An RPC method must be a public instance method of a <c>NetworkBehaviour</c>
    /// subclass, with a name no other RPC method of the type uses (no overloads). Its parameters
    /// must be <c>int</c>, <c>float</c>, <c>bool</c>, <c>string</c>, <c>byte[]</c>,
    /// <c>ulong</c>, <c>Vector3</c>, <c>Color</c>, <c>Quaternion</c>, or types that implement
    /// <see cref="INetworkSerializable"/>.</para>
    /// <para>The method's id is derived from the type's full name and the method name (see
    /// <see cref="RpcRegistry.ComputeMethodId(Type, string)"/>), and must not equal a reserved
    /// id (<see cref="RpcMethodId"/>) or another RPC method's id. The analyzers report these
    /// problems while you edit (rules RTMPE1001 to RTMPE1006). At run time a collision is
    /// logged when the type first spawns, and that type's RPCs are not delivered.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [RtmpeRpc(RpcTarget.All)]
    /// public void FireRpc(Vector3 origin) { }
    ///
    /// // In the same NetworkBehaviour:
    /// RPC(nameof(FireRpc), transform.position);
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RtmpeRpcAttribute : Attribute
    {
        /// <summary>
        /// Who runs the call. Default <see cref="RpcTarget.All"/>.
        /// </summary>
        public RpcTarget Target { get; }

        /// <summary>
        /// Who may make the call. Default <see cref="RpcCaller.Anyone"/>.
        /// </summary>
        /// <remarks>
        /// <para>The caller's own SDK refuses a call it may not make, and every receiving client
        /// checks the declaration against what the server reports about the caller. For
        /// example, <c>[RtmpeRpc(RpcTarget.All, Caller = RpcCaller.Host)]</c> runs only when the
        /// room's host calls it, and <see cref="RpcCaller.Server"/> only when your server
        /// function sends it. The server satisfies every declaration.</para>
        /// <para>For a <see cref="RpcTarget.Server"/> method, the declaration is checked only
        /// when the call is made. Your server function receives the caller's identity and
        /// decides by it.</para>
        /// </remarks>
        public RpcCaller Caller { get; set; } = RpcCaller.Anyone;

        /// <summary>Marks a method as an RPC run by <paramref name="target"/>.</summary>
        /// <param name="target">Who runs the call. Default <see cref="RpcTarget.All"/>.</param>
        public RtmpeRpcAttribute(RpcTarget target = RpcTarget.All)
        {
            Target = target;
        }
    }
}
