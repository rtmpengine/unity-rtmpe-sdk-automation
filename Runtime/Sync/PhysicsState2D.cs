// RTMPE SDK — Runtime/Sync/PhysicsState2D.cs
//
// Plain value struct holding the 2-D physics fields synchronised over the
// network by NetworkRigidbody2D.
//
// Design decisions:
//  • Uses Vector2 for position/velocity (XY plane only).
//  • Rotation is a single float (Z-axis angle in degrees).
//    Matches Rigidbody2D.rotation which Unity exposes in degrees.
//  • AngularVelocity is a single float (degrees/second).
//    Matches Rigidbody2D.angularVelocity which Unity also exposes in deg/s.
//  • IsSleeping mirrors the 3-D convention; Rigidbody2D.IsSleeping() is the
//    corresponding Unity API call.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// A 2-D <see cref="UnityEngine.Rigidbody2D"/>'s physics state, as
    /// <see cref="NetworkRigidbody2D"/> sends and receives it. Returned by
    /// <see cref="NetworkRigidbody2D.GetState"/>.
    /// </summary>
    public struct PhysicsState2D
    {
        /// <summary>World-space 2-D position.</summary>
        public Vector2 Position;

        /// <summary>Rotation about the Z axis, in degrees.</summary>
        public float Rotation;

        /// <summary>Linear velocity, in units per second.</summary>
        public Vector2 Velocity;

        /// <summary>Angular velocity, in degrees per second.</summary>
        public float AngularVelocity;

        /// <summary>Whether the body is asleep.</summary>
        public bool IsSleeping;

        /// <summary>
        /// The body's <see cref="UnityEngine.RigidbodyConstraints2D"/> as a
        /// bitmask; <c>0</c> is none. A receiver applies it only when
        /// <c>NetworkSettings.allowDynamicConstraints</c> is on.
        /// </summary>
        public byte ConstraintMask;

        /// <summary>
        /// The last-sent record after <paramref name="current"/> went out under
        /// <paramref name="dataMask"/>: every field the mask selected takes the
        /// value the wire carried, and every field it did not keeps
        /// <paramref name="previous"/>.  See
        /// <see cref="PhysicsState.AfterSend"/> for why the record moves only
        /// for the selected fields; the 2-D rotation is a plain angle the
        /// encoder writes verbatim, so no wire form is substituted here.
        /// </summary>
        internal static PhysicsState2D AfterSend(PhysicsState2D previous, PhysicsState2D current, byte dataMask)
        {
            var sent = previous;
            if ((dataMask & PhysicsPacketBuilder.ChangedPosition) != 0)
                sent.Position = current.Position;
            if ((dataMask & PhysicsPacketBuilder.ChangedRotation) != 0)
                sent.Rotation = current.Rotation;
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
