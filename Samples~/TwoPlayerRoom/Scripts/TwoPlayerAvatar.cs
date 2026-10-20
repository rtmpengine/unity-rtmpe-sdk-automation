using RTMPE.Core;
using RTMPE.Sync;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// The avatar the other player sees move: owner-driven WASD motion, with
    /// replication and smoothing done by components rather than code.
    /// </summary>
    /// <remarks>
    /// <para><c>NetworkTransform</c> sends the owner's pose to the other clients
    /// and <c>NetworkTransformInterpolator</c> plays it back on each copy; without
    /// the interpolator, other players' copies of the avatar do not move.
    /// <c>[RequireComponent]</c> makes Unity add both when this script is attached
    /// and keeps either from being removed.</para>
    /// <para>The movement below reads the legacy Input Manager. In a project set
    /// to the Input System package only, <c>UnityEngine.Input</c> throws: set
    /// <b>Active Input Handling</b> to <i>Both</i> in Player settings, or delete
    /// <c>Update</c>; the two-client flow this sample shows does not depend on
    /// input.</para>
    /// </remarks>
    [RequireComponent(typeof(NetworkTransform), typeof(NetworkTransformInterpolator))]
    public sealed class TwoPlayerAvatar : NetworkBehaviour
    {
        /// <summary>
        /// How many avatars are spawned in this process right now: the local
        /// player's and every copy of a peer's. The on-screen readout shows it,
        /// so a developer running two clients can see whether the second one
        /// arrived.
        /// </summary>
        /// <remarks>
        /// Raised in <c>OnNetworkSpawn</c> and lowered in <c>OnNetworkDespawn</c>,
        /// or in <c>OnDestroy</c> when Unity destroys a spawned avatar without a
        /// despawn.
        /// </remarks>
        internal static int LiveCount;

        // Resets the count on entering Play mode. With domain reload disabled
        // in Enter Play Mode Options, statics keep their values between runs,
        // and leaving Play mode does not despawn the avatars (a Disconnect()
        // does), so the count would otherwise keep growing from run to run.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            LiveCount = 0;
        }

        // Constants rather than serialized fields: this sample has nothing to
        // configure.
        private const float MoveSpeed = 5f;
        private const float RotateSpeed = 120f;

        protected override void OnNetworkSpawn()
        {
            LiveCount++;
        }

        protected override void OnNetworkDespawn()
        {
            LiveCount--;
        }

        // OnNetworkDespawn is not called when Unity destroys the avatar itself,
        // as a non-additive scene load does, so the count is corrected here.
        protected override void OnDestroy()
        {
            // IsSpawned is already false when OnNetworkDespawn has run, so an
            // avatar that was despawned first is not counted out twice.
            if (IsSpawned) LiveCount--;

            base.OnDestroy();
        }

        private void Update()
        {
            // Only the owning client drives input. Every other client is holding
            // a replica of this object, and a replica that reads local input
            // fights the pose arriving from its owner.
            if (!IsOwner) return;

            float h = Input.GetAxis("Horizontal");
            float v = Input.GetAxis("Vertical");

            Vector3 move = transform.forward * v + transform.right * h;
            transform.position += move * (MoveSpeed * Time.deltaTime);

            float mouseX = Input.GetAxis("Mouse X");
            transform.Rotate(0f, mouseX * RotateSpeed * Time.deltaTime, 0f);
        }
    }
}
