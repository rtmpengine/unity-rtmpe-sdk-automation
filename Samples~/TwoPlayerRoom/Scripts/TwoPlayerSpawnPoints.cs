using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// Where each client's avatar appears. Four points on a ring, and the one
    /// this client takes is decided from an identity the gateway issued — so
    /// the two clients stand apart instead of inside each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 This component is one line of work and it exists for that line:
    /// <c>RtmpeConnectionBootstrap.ChooseSpawnPose</c>. Without it the
    /// bootstrap spawns the avatar at <b>its own transform</b> — one place,
    /// which on two clients is the SAME place — and the README puts the
    /// bootstrap on <c>[RTMPE] Session</c>, which the scene puts at the origin.
    /// So the default is two capsules buried to the waist in the ground, on top
    /// of one another, and a readout saying <c>2</c> beside a window showing
    /// one capsule.
    /// </para>
    /// <para>
    /// ⛔ The seam is a delegate rather than a spawn-point list on the bootstrap
    /// because choosing where a player appears is a decision about YOUR game —
    /// a team, a lobby seat, a saved position, the point furthest from an
    /// enemy. This is the smallest honest answer to it, not the shape yours has
    /// to take.
    /// </para>
    /// </remarks>
    public sealed class TwoPlayerSpawnPoints : MonoBehaviour
    {
        // ⛔ y = 1, not 0. The avatar is Unity's built-in Capsule mesh: two
        // units tall with its pivot at the CENTRE, so a capsule whose centre is
        // on the ground plane is buried to the waist. The scene's own `Player`
        // object sits at y = 1 for exactly this reason — this constant restates
        // that number rather than being a margin tuned by eye. An avatar of a
        // different height needs a different one, and a prefab whose pivot is
        // at its feet needs 0.
        private const float StandingHeight = 1f;

        // Far enough apart to read as two players at a glance — a capsule is one
        // unit wide — and inside what the scene's camera can see: it sits at
        // (0, 3, -10) looking down +Z, and the ground plane is 50 × 50.
        private const float RingRadius = 3f;

        // ⛔ A table of literal points rather than trigonometry, because the
        // point of a spawn table is that you can READ where your players will
        // appear and drag the numbers around. Four, for a room the README caps
        // at two: the spare points are what a third and a fourth client get, and
        // what makes the arithmetic below say something rather than being an
        // if/else with a ring drawn around it.
        private static readonly Vector3[] Points =
        {
            new Vector3(0f, StandingHeight, -RingRadius),
            new Vector3(RingRadius, StandingHeight, 0f),
            new Vector3(0f, StandingHeight, RingRadius),
            new Vector3(-RingRadius, StandingHeight, 0f),
        };

        // Each avatar faces the middle of the ring, so the two clients look at
        // each other rather than both facing the camera — which is also how you
        // tell at a glance that the remote capsule is being driven by its owner
        // and not by this client.
        private static readonly Quaternion[] Facings =
        {
            Quaternion.Euler(0f, 0f, 0f),
            Quaternion.Euler(0f, 270f, 0f),
            Quaternion.Euler(0f, 180f, 0f),
            Quaternion.Euler(0f, 90f, 0f),
        };

        // The room's host takes this one and nobody else can: a room has exactly
        // one host, so for two clients the split is a GUARANTEE rather than a
        // probability.
        private const int HostPoint = 0;

        // Everyone else is spread over the points from here on. Keeping the
        // host's point out of that range is the whole of why two clients cannot
        // collide — a remainder taken over ALL the points would let a guest land
        // on the host.
        private const int FirstGuestPoint = 1;

        private void Awake()
        {
            // Awake, because RtmpeConnectionBootstrap connects from Start and
            // every Awake in a scene runs before any Start in it. The pose is
            // not asked for until the room has been entered, which is later
            // still; this only has to be registered before then, and no
            // execution-order setting is needed to make that true.
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

            // The seam, and the only line here that touches the SDK's own
            // surface. A named method rather than a lambda, so the thing a
            // reader has to change is one they can navigate to.
            bootstrap.ChooseSpawnPose = ChooseSpawnPose;
        }

        /// <summary>
        /// The pose this client's avatar starts in.
        /// </summary>
        /// <remarks>
        /// ⚠️ It must not throw and it must not be slow: the bootstrap calls it
        /// once, inside the room-entry callback, and treats a throw as a refusal
        /// — the client is then in the room holding no avatar at all, which is
        /// worse than any pose this could return.
        /// </remarks>
        private Pose ChooseSpawnPose()
        {
            int index = PointFor(NetworkManager.Instance);
            return new Pose(Points[index], Facings[index]);
        }

        /// <summary>
        /// Which point this client takes, from the identities the SDK actually
        /// has at the moment the room is entered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⛔ <b>There is no per-room seat index in this SDK, and the obvious
        /// substitutes are worse than nothing.</b> <c>PlayerInfo</c> carries no
        /// ordinal, and the ORDER of <c>CurrentRoom.Players</c> comes from a
        /// server-side map iteration — so two clients reading the same roster
        /// can see the same players in different orders, and an index taken from
        /// it would not agree between them. <c>PlayerCount</c> is no better: on
        /// the matchmaking path the SDK synthesises the snapshot and falls back
        /// to a count of one when the server's roster read fails.
        /// </para>
        /// <para>
        /// 🔑 <c>LocalPlayerId</c> is the gateway's session id. It is set at
        /// SessionAck, which strictly precedes entering a room, and two live
        /// sessions never share one — so it is <b>distinct</b>. It is not
        /// <b>dense</b>: the gateway seeds it from the clock and counts up, so a
        /// remainder of it is a HASH and two ids can land on the same one.
        /// </para>
        /// <para>
        /// 🔑 <c>IsMasterClient</c> is true for exactly one client in a room, by
        /// construction. So: the host takes <see cref="HostPoint"/> and nobody
        /// else can reach it, and every other client is spread over the points
        /// that are left. For the <b>two</b> clients this sample runs that is a
        /// guarantee. For a third and a fourth it is a hash, and two of them can
        /// collide — a game that needs guaranteed-distinct spawns has to CLAIM a
        /// point through the room's shared properties, because the SDK has no
        /// seat index to do it for you.
        /// </para>
        /// <para>
        /// ⚠️ The one way the host flag can be wrong is a room entry that
        /// carried no local player id — an older server, or a matchmaking reply
        /// with an empty one. Both clients then read <c>false</c> and both fall
        /// to the guest arm, where the session id still separates them most of
        /// the time. That is the reason the host arm is a shortcut into the
        /// table rather than the only thing deciding it.
        /// </para>
        /// </remarks>
        private static int PointFor(NetworkManager manager)
        {
            // Unity's equality, deliberately: `Instance` answers null with no
            // manager in the scene and while the application is quitting.
            if (manager == null) return HostPoint;

            if (manager.IsMasterClient) return HostPoint;

            return FirstGuestPoint
                   + (int)(manager.LocalPlayerId % (ulong)(Points.Length - FirstGuestPoint));
        }
    }
}
