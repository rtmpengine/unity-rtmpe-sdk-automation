// RTMPE SDK — Runtime/Core/IDamageable.cs
//
// Interface for objects that can receive server-authorised damage via ApplyDamage RPC.
// Lives in the SDK Runtime assembly so NetworkManager can dispatch damage RPCs
// without a compile-time dependency on game-specific types (e.g. HealthController
// in the Samples assembly).
//
// Game code implements this on any component that should receive ApplyDamage (301) RPCs.

namespace RTMPE.Core
{
    /// <summary>
    /// Receives the built-in <c>ApplyDamage</c> call
    /// (<see cref="RTMPE.Rpc.RpcMethodId.ApplyDamage"/>) for a networked object.
    /// </summary>
    /// <remarks>
    /// <para>The RTMPE service does not send this call, so
    /// <see cref="ReceiveApplyDamage"/> is not called in a game connected to it.
    /// Use <c>[RtmpeRpc]</c> methods instead: for damage the server decides,
    /// declare a method with <c>Caller = RpcCaller.Server</c> and send it from
    /// your server function.</para>
    /// <para>The SDK finds the handler with
    /// <c>GetComponentInParent&lt;IDamageable&gt;()</c>, starting from the
    /// object's first <see cref="NetworkBehaviour"/>.</para>
    /// </remarks>
    public interface IDamageable
    {
        /// <summary>
        /// Called on each client that receives the <c>ApplyDamage</c> call, on
        /// the main thread.
        /// </summary>
        /// <param name="damage">
        /// The damage amount; always positive. Calls with zero or negative damage
        /// are dropped before this method is called.
        /// </param>
        void ReceiveApplyDamage(int damage);
    }
}
