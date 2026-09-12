// RTMPE SDK — Runtime/Sync/ITransformAxisGates.cs
//
// The two Inspector toggles that decide whether the network may write this
// object's position and rotation, expressed as the narrowest thing a consumer
// needs rather than as the component that owns them.
//
// Why an interface and not a direct NetworkTransform reference: the consumer is
// NetworkTransformInterpolator, which otherwise needs nothing from
// NetworkTransform at all — a component carrying client-side prediction, an
// input buffer, a velocity budget and a reconciliation ladder. Naming the whole
// type to read two booleans makes every test of the interpolator carry all of
// it.
//
// ⚠️ Read on each apply rather than cached: the flags are [SerializeField]
// toggles an integrator can flip in the Inspector while the game is playing,
// and a value captured once would go stale exactly when someone is
// experimenting with it.

namespace RTMPE.Sync
{
    /// <summary>
    /// Whether inbound transform updates may write each axis of this object.
    /// </summary>
    internal interface ITransformAxisGates
    {
        /// <summary>Whether inbound position updates may write this transform.</summary>
        bool SyncPosition { get; }

        /// <summary>Whether inbound rotation updates may write this transform.</summary>
        bool SyncRotation { get; }

        /// <summary>Whether inbound scale updates may write this transform.</summary>
        /// <remarks>
        /// ⚠️ All three, not the two a repair happened to need. The interface is
        /// named for the axis gates, and one that declared two of three would
        /// leave the third where it was — decided by a duplicate Inspector
        /// checkbox on the consumer, kept in step with this one by a tooltip.
        /// </remarks>
        bool SyncScale { get; }
    }
}
