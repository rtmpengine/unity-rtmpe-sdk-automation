using RTMPE.Core;
using RTMPE.Sync;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// The thing the other player sees move. Owner-driven WASD motion; the
    /// replication and the smoothing are both components, not code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ The attribute is the point of this file. <c>NetworkTransform</c> puts
    /// the owner's pose on the wire; <c>NetworkTransformInterpolator</c> is what
    /// plays it back on every OTHER client. Without the interpolator a replica
    /// does not merely stutter — it <b>freezes</b>, because nothing advances it
    /// between the updates that arrive, and a developer testing alone never sees
    /// that: it needs a second client in the room.
    /// </para>
    /// <para>
    /// 🔑 Stating it as <c>[RequireComponent]</c> rather than shipping a
    /// pre-built prefab is deliberate and is stronger: Unity adds both the
    /// moment this script is attached, and REFUSES to remove either while it is
    /// there. A prefab can be edited into the broken state; this cannot.
    /// </para>
    /// <para>
    /// The movement below reads the legacy Input Manager. A project configured
    /// for the Input System package alone has no such manager and
    /// <c>UnityEngine.Input</c> throws there, so set <b>Active Input
    /// Handling</b> to <i>Both</i> in Player settings, or delete
    /// <c>Update</c> — the two-client flow this sample exists to show does not
    /// depend on input.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(NetworkTransform), typeof(NetworkTransformInterpolator))]
    public sealed class TwoPlayerAvatar : NetworkBehaviour
    {
        /// <summary>
        /// How many avatars are spawned in this process right now — the local
        /// player's and every replica of a peer. It is what the on-screen
        /// readout counts, and the one number that tells a developer running two
        /// clients whether the second one arrived.
        /// </summary>
        /// <remarks>
        /// Moved by the network lifecycle hooks rather than by
        /// <c>OnDestroy</c>: a spawn and a despawn are what this counts, and
        /// those are the two moments the SDK names.
        /// </remarks>
        internal static int LiveCount;

        // 🚨 A static survives play-mode exit when Enter Play Mode Options has
        // domain reload disabled — a standard fast-iteration setting this SDK
        // caters for — and the despawn that would bring this back down is NOT
        // reached on that path. Three independent reasons, each sufficient:
        // NetworkManager's OnApplicationQuit raises its quitting flag before
        // Unity destroys the scene, so NetworkManager.Instance answers null and
        // NetworkBehaviour.OnDestroy has nothing to ask; that Cleanup nulls the
        // spawn manager without going through ClearAll, which is what fires the
        // despawns; and SpawnManager.OnExternallyDestroyed does bookkeeping and
        // never calls SetSpawned(false). Left alone the readout reads 4 on the
        // second run and 6 on the third, and the number this sample exists to
        // make readable becomes the thing that lies to you.
        //
        // ⛔ A clean Disconnect() is fine — that path reaches ClearAll and the
        // despawns fire — so this is not a substitute for OnNetworkDespawn
        // above. It is the re-arm for the one exit Unity does not route through
        // the SDK at all, and it is the same hook the SDK re-arms its own
        // statics from: SubsystemRegistration fires on entering play mode,
        // before any Awake and before any scene loads.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            LiveCount = 0;
        }

        // Constants rather than serialized fields: this sample has nothing to
        // configure, and every Inspector field it does not have is one fewer
        // thing to get wrong before two clients meet.
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

        // 🚨 The hook above is not reached when Unity destroys the object
        // itself, and a non-additive scene load does exactly that: these avatars
        // are not DontDestroyOnLoad, so loading a scene destroys them somewhere
        // the SDK never sees — SpawnManager.OnExternallyDestroyed does its
        // bookkeeping without calling SetSpawned(false). ResetStaticState below
        // cannot cover it either: SubsystemRegistration fires on entering play
        // mode and on process start, and a scene load is neither. In a shipped
        // player nothing at all would bring this back down, and the readout
        // would count avatars that stopped existing one scene ago.
        protected override void OnDestroy()
        {
            // ⛔ IsSpawned rather than a flag of this component's own. It is the
            // SDK's record of whether the despawn above has already run —
            // SetSpawned(false) clears it BEFORE raising OnNetworkDespawn — so a
            // clean teardown arrives here already counted out and a scene load
            // arrives here still counted in. A second flag would be a parallel
            // statement of the same fact, free to disagree with it, and would be
            // replicable state on a NetworkBehaviour besides.
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
