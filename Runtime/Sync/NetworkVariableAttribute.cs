// RTMPE SDK — Runtime/Sync/NetworkVariableAttribute.cs
//
// Declarative metadata for NetworkVariable fields and properties.
//
// Primary use case: per-variable bandwidth throttling.  By default every dirty
// NetworkVariable is flushed at the global tick cadence (NetworkManager
// VariableFlushInterval = 30 Hz).  Marking a field with
//  [NetworkVariable(SendRateHz = 10f)]
// caps that variable's outbound rate at 10 Hz independently of its siblings,
// reducing bandwidth for values that change frequently but only need slow
// synchronisation (health bars, ammo counts, kill streaks, …).
//
// Discovery: NetworkBehaviour scans its own type once (via reflection) the
// first time TrackVariable() is called for that type.  Found attributes are
// matched to their corresponding NetworkVariableBase instance by the field /
// property reference (object identity), not by name, so the developer is free
// to rename either side without invalidating the binding.
//
// Performance: the reflection scan happens once per concrete subclass, on a
// single thread (Unity main thread, during OnNetworkSpawn).  The result is
// cached in a per-type dictionary keyed by Type, so subsequent spawns of the
// same NetworkBehaviour subclass do not re-scan.  Per-variable lookup at
// flush time is O(1) — the SendRateHz is copied directly onto each variable
// instance during registration.

using System;

namespace RTMPE.Sync
{
    /// <summary>
    /// Marks a field or property of a <see cref="RTMPE.Core.NetworkBehaviour"/>
    /// that holds a network variable. The analyzer checks where the attribute is
    /// used (rule RTMPE1013).
    /// </summary>
    /// <remarks>
    /// To limit how often a variable is sent, set
    /// <see cref="NetworkVariableBase.SendRateHz"/> on the variable after
    /// constructing it.
    /// </remarks>
    /// <example>
    /// <code>
    /// protected override void OnNetworkSpawn()
    /// {
    ///     _health = new NetworkVariableInt(this, nameof(_health), 100);
    ///     _health.SendRateHz = 10f;   // at most 10 updates per second
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property,
                    AllowMultiple = false,
                    Inherited     = true)]
    public sealed class NetworkVariableAttribute : Attribute
    {
        /// <summary>
        /// A send rate, in updates per second, declared with the attribute.
        /// <c>0</c> (the default) declares none.
        /// </summary>
        /// <remarks>
        /// To limit how often a variable is sent, set
        /// <see cref="NetworkVariableBase.SendRateHz"/> on the variable after
        /// constructing it.
        /// </remarks>
        public float SendRateHz { get; set; } = 0f;
    }
}
