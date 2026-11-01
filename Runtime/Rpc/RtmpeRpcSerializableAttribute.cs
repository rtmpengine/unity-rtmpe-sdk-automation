// RTMPE SDK — Runtime/Rpc/RtmpeRpcSerializableAttribute.cs
//
// Trust model:
//  The Enhanced RPC wire format embeds a UTF-8 type name for every
//  INetworkSerializable parameter and the receiver instantiates the
//  resolved Type via Activator.CreateInstance.  That instantiation is a
//  reflection-driven gadget primitive: any process-loaded type that
//  satisfies the discovery filter (public, parameterless ctor,
//  INetworkSerializable) becomes reachable from a hostile peer or relay.
//
//  To collapse that surface to a known-good set we require explicit
//  author opt-in.  A type only becomes resolvable when one of the
//  following is true:
//    • it carries [RtmpeRpcSerializable], OR
//    • the application called RpcTypeRegistry.Register<T>() at startup.
//
//  Untagged / unregistered types are silently invisible to the inbound
//  resolver — even when RpcTypeRegistry.AllowAppDomainScan is enabled
//  (the scan only picks up attributed types).  This is the intended
//  defence; do not relax it without a corresponding threat model update.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Marks an <see cref="INetworkSerializable"/> type for the automatic registration that
    /// <see cref="RpcTypeRegistry.AllowAppDomainScan"/> turns on.
    /// </summary>
    /// <remarks>
    /// The attribute registers the type only while
    /// <see cref="RpcTypeRegistry.AllowAppDomainScan"/> is <see langword="true"/>. Prefer
    /// <see cref="RpcTypeRegistry.Register{T}"/>, which is required under IL2CPP. On a client
    /// where the type is not registered, an argument of that type arrives as
    /// <see langword="null"/> and a warning is logged.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct,
                    AllowMultiple = false, Inherited = false)]
    public sealed class RtmpeRpcSerializableAttribute : Attribute
    {
    }
}
