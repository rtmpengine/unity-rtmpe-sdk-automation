using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// Where each client's avatar appears: four points on a ring, so the two
    /// clients stand apart instead of inside each other.
    /// </summary>
    /// <remarks>
    /// <para>It supplies <c>RtmpeConnectionBootstrap.ChooseSpawnPose</c>. Without
    /// it, the bootstrap spawns every avatar at its own transform, which is the
    /// same place on both clients.</para>
    /// <para>Where a player appears is a decision about your game (a team, a
    /// lobby seat, a saved position); this is a minimal example.</para>
    /// </remarks>
    public sealed class TwoPlayerSpawnPoints : MonoBehaviour
    {
        // The avatar is Unity's built-in Capsule: two units tall with its pivot
        // at the centre, so the centre stands 1 unit above the ground. Use a
        // different value for an avatar of another height, and 0 for a prefab
        // whose pivot is at its feet.
        private const float StandingHeight = 1f;

        // Far enough apart to read as two players at a glance — a capsule is one
        // unit wide — and inside what the scene's camera can see: it sits at
        // (0, 3, -10) looking down +Z, and the ground plane is 50 × 50.
        private const float RingRadius = 3f;

        // Literal points rather than trigonometry, so you can read where players
        // appear and edit the numbers. Four points for a two-player room: the
        // spare ones are used by a third and a fourth client.
        private static readonly Vector3[] Points =
        {
            new Vector3(0f, StandingHeight, -RingRadius),
            new Vector3(RingRadius, StandingHeight, 0f),
            new Vector3(0f, StandingHeight, RingRadius),
            new Vector3(-RingRadius, StandingHeight, 0f),
        };

        // Each avatar faces the middle of the ring rather than the camera.
        private static readonly Quaternion[] Facings =
        {
            Quaternion.Euler(0f, 0f, 0f),
            Quaternion.Euler(0f, 270f, 0f),
            Quaternion.Euler(0f, 180f, 0f),
            Quaternion.Euler(0f, 90f, 0f),
        };

        // The room's host takes this point and no one else can: a room has one
        // host, so two clients never share a point.
        private const int HostPoint = 0;

        // Every other client is spread over the points from here on, which
        // keeps guests off the host's point.
        private const int FirstGuestPoint = 1;

        private void Awake()
        {
            // Registered in Awake: every Awake in a scene runs before any Start,
            // and the bootstrap connects from Start and asks for the pose only
            // once the room has been entered.
            var bootstrap = GetComponent<RtmpeConnectionBootstrap>();
            if (bootstrap == null)
            {
                Debug.LogWarning(
                    "[TwoPlayerSpawnPoints] There is no RtmpeConnectionBootstrap on this "
                    + "GameObject, so nothing asks this component where to spawn: every avatar "
                    + "will appear at the bootstrap's own transform, which is one point and "
                    + "therefore the same point on both clients. Put the two components on the "
                    + "same object.");
                return;
            }

            // The only line here that uses the SDK.
            bootstrap.ChooseSpawnPose = ChooseSpawnPose;
        }

        /// <summary>
        /// The pose this client's avatar starts in.
        /// </summary>
        /// <remarks>
        /// Must not throw or be slow: the bootstrap calls it once, when the room
        /// is entered, and if it throws the client stays in the room with no
        /// avatar.
        /// </remarks>
        private Pose ChooseSpawnPose()
        {
            int index = PointFor(NetworkManager.Instance);
            return new Pose(Points[index], Facings[index]);
        }

        /// <summary>
        /// Which point this client takes: the host takes <see cref="HostPoint"/>,
        /// and every other client takes one of the remaining points, chosen from
        /// its <c>LocalPlayerId</c>.
        /// </summary>
        /// <remarks>
        /// <para>The SDK has no per-room seat index, and clients can list
        /// <c>CurrentRoom.Players</c> in different orders, so neither gives a
        /// point that every client agrees on.</para>
        /// <para><c>LocalPlayerId</c> is set before the room is entered and is
        /// unique among live sessions, but its values are not consecutive. With two
        /// clients the host rule keeps them apart; a third and a fourth client can
        /// land on the same point. A game that needs a distinct point for every
        /// player should claim points through the room's shared properties.</para>
        /// <para>In the rare case that no client reads as host, all of them use the
        /// guest points, where their session ids still usually separate them.</para>
        /// </remarks>
        private static int PointFor(NetworkManager manager)
        {
            // Instance is null with no manager in the scene and while the
            // application is quitting.
            if (manager == null) return HostPoint;

            if (manager.IsMasterClient) return HostPoint;

            return FirstGuestPoint
                   + (int)(manager.LocalPlayerId % (ulong)(Points.Length - FirstGuestPoint));
        }
    }
}
