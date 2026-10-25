// RTMPE SDK — Runtime/Sync/TransformState.cs
//
// Plain value struct holding the transform fields that are synchronised over
// the network by NetworkTransform.
//
// Design decisions:
//  • Uses UnityEngine.Vector3 and UnityEngine.Quaternion directly so that
//    NetworkTransform can assign/read Unity transform fields without an
//    intermediate conversion type.
//  • No UnityEngine behaviour or MonoBehaviour dependencies — pure data.
//  • TransformPacketBuilder and TransformPacketParser operate on this type,
//    making both serialisation and deserialisation paths type-safe.
//  • Identity static property gives a canonical "at rest" value useful for
//    initialisation and test default construction.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// A networked object's position, rotation and scale at one moment, with the
    /// ticks a received update carries. Returned by
    /// <see cref="NetworkTransform.GetState"/> and accepted by
    /// <see cref="NetworkTransform.ApplyState"/>.
    /// </summary>
    public struct TransformState
    {
        /// <summary>World-space position.</summary>
        public Vector3 Position;

        /// <summary>World-space rotation.</summary>
        public Quaternion Rotation;

        /// <summary>Local scale.</summary>
        public Vector3 Scale;

        /// <summary>
        /// On a correction the owner receives: the owner's latest input tick the
        /// server had applied. Client-side prediction replays only the inputs
        /// after it. Valid only when <see cref="HasConfirmedInputTick"/> is
        /// <see langword="true"/>.
        /// </summary>
        public uint ConfirmedInputTick;

        /// <summary>
        /// Whether <see cref="ConfirmedInputTick"/> holds a value. When it does
        /// not, client-side prediction assumes the server has applied every
        /// input up to this client's previous tick.
        /// </summary>
        public bool HasConfirmedInputTick;

        /// <summary>
        /// On a received update: the server broadcast tick it was sent in, which
        /// <see cref="NetworkTransformInterpolator"/> can time the pose by. Valid
        /// only when <see cref="HasServerTick"/> is <see langword="true"/>.
        /// </summary>
        public uint ServerTick;

        /// <summary>
        /// Whether <see cref="ServerTick"/> holds a value.
        /// </summary>
        public bool HasServerTick;

        /// <summary>
        /// A state at the world origin, with no rotation and unit scale.
        /// </summary>
        public static TransformState Identity => new TransformState
        {
            Position              = Vector3.zero,
            Rotation              = Quaternion.identity,
            Scale                 = Vector3.one,
            ConfirmedInputTick    = 0u,
            HasConfirmedInputTick = false,
            ServerTick            = 0u,
            HasServerTick         = false,
        };
    }
}
