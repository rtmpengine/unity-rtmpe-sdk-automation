// RTMPE SDK — Runtime/Sync/PhysicsState.cs
//
// Plain value struct holding the 3-D physics fields synchronised over the
// network by NetworkRigidbody.
//
// Design decisions:
//  • Mirrors TransformState but adds Velocity, AngularVelocity, IsSleeping,
//    and ConstraintMask for Rigidbody-driven objects.  Position and Rotation
//    are included so the physics component is self-contained and does not
//    require a co-located NetworkTransform.
//  • Uses UnityEngine types directly — no intermediate conversion type.
//  • IsSleeping enables remote Rigidbodies to enter sleep state when the owner
//    physics engine idles the body, eliminating micro-movement from floating-
//    point noise on stationary objects.
//  • AngularVelocity is in radians/second (Unity's native Rigidbody unit).
//  • ConstraintMask mirrors RigidbodyConstraints (Unity's bitmask) so that
//    runtime constraint changes (e.g. freezing axes mid-flight) are preserved
//    across the network rather than relying on inspector-set defaults.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// A 3-D <see cref="UnityEngine.Rigidbody"/>'s physics state, as
    /// <see cref="NetworkRigidbody"/> sends and receives it. Returned by
    /// <see cref="NetworkRigidbody.GetState"/>.
    /// </summary>
    public struct PhysicsState
    {
        /// <summary>World-space position.</summary>
        public Vector3 Position;

        /// <summary>World-space rotation as a unit quaternion.</summary>
        public Quaternion Rotation;

        /// <summary>World-space linear velocity, in units per second.</summary>
        public Vector3 Velocity;

        /// <summary>World-space angular velocity, in radians per second.</summary>
        public Vector3 AngularVelocity;

        /// <summary>Whether the body is asleep.</summary>
        public bool IsSleeping;

        /// <summary>
        /// The body's <see cref="UnityEngine.RigidbodyConstraints"/> as a
        /// bitmask; <c>0</c> is none. A receiver applies it only when
        /// <c>NetworkSettings.allowDynamicConstraints</c> is on.
        /// </summary>
        public byte ConstraintMask;

        /// <summary>
        /// The last-sent record after <paramref name="current"/> went out under
        /// <paramref name="dataMask"/>: every field the mask selected takes the
        /// value the wire carried, and every field it did not keeps
        /// <paramref name="previous"/>.
        /// </summary>
        /// <remarks>
        /// A masked send is a statement about the selected fields only, and the
        /// record is the baseline every change threshold is measured against —
        /// so it moves only for them.  The field a mask leaves out is not
        /// always one that did not change: a coordinate that has diverged to NaN
        /// compares false against any threshold, so its bit is never set, and a
        /// send carrying only the sleep or constraint bit passes the encoder's
        /// finiteness check without it.  Recorded whole, that snapshot would put
        /// the NaN into the baseline, where every later comparison is false and
        /// the field never replicates again, however finite it has since become.
        /// The rotation is recorded as <see cref="WireQuaternion.ForWire"/>
        /// writes it, for the same reason: the baseline is the value the wire
        /// carried, and a raw rotation the wire would not carry — NaN above all
        /// — must not be measured against either.
        /// </remarks>
        internal static PhysicsState AfterSend(PhysicsState previous, PhysicsState current, byte dataMask)
        {
            var sent = previous;
            if ((dataMask & PhysicsPacketBuilder.ChangedPosition) != 0)
                sent.Position = current.Position;
            if ((dataMask & PhysicsPacketBuilder.ChangedRotation) != 0)
                sent.Rotation = WireQuaternion.ForWire(current.Rotation, out _);
            if ((dataMask & PhysicsPacketBuilder.ChangedVelocity) != 0)
                sent.Velocity = current.Velocity;
            if ((dataMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0)
                sent.AngularVelocity = current.AngularVelocity;
            if ((dataMask & PhysicsPacketBuilder.ChangedSleep) != 0)
                sent.IsSleeping = current.IsSleeping;
            if ((dataMask & PhysicsPacketBuilder.ChangedConstraints) != 0)
                sent.ConstraintMask = current.ConstraintMask;
            return sent;
        }
    }
}
