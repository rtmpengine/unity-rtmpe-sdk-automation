using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Samples.PlayerSpawnFlow
{
    /// <summary>
    /// Sample player controller that synchronises position and rotation
    /// over the network using RTMPE's NetworkTransform component.
    /// Attach this to the player prefab alongside NetworkTransform.
    /// The NetworkTransform component handles automatic position/rotation sync.
    /// </summary>
    /// <remarks>
    /// The movement below reads the legacy Input Manager. A project configured
    /// for the Input System package alone has no such manager and
    /// <c>UnityEngine.Input</c> throws there, so set <b>Active Input
    /// Handling</b> to <i>Both</i> in Player settings, or delete
    /// <c>Update</c> — the spawn flow this sample exists to show does not read
    /// input at all.
    /// </remarks>
    /// <remarks>
    /// ⛔ Both motion components, not one. <c>NetworkTransform</c> puts the
    /// owner's pose on the wire and <c>NetworkTransformInterpolator</c> plays it
    /// back on every OTHER client: without it a replica does not stutter, it
    /// <b>freezes</b>, because nothing advances it between the updates that
    /// arrive. The advisory that names the omission needs a second client
    /// already in the room to fire, so a developer testing alone never meets it
    /// — which is why it is stated here, where Unity adds both on attach and
    /// refuses to remove either.
    /// </remarks>
    [RequireComponent(typeof(NetworkTransform), typeof(NetworkTransformInterpolator))]
    public class PlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _rotateSpeed = 120f;

        // Synced score variable. Initialised in OnNetworkSpawn so 'this' is
        // available as the owner reference — the identity is derived from the
        // owner's type and the member's name, so there is nothing to choose.
        private NetworkVariableInt _score;

        private CharacterController _controller;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        protected override void OnNetworkSpawn()
        {
            _score = new NetworkVariableInt(this, nameof(_score), initialValue: 0);
            _score.OnValueChanged += (oldVal, newVal) =>
                Debug.Log($"[{name}] Score: {oldVal} → {newVal}");
        }

        private void Update()
        {
            // Only the owning client drives input.
            if (!IsOwner) return;

            float h = Input.GetAxis("Horizontal");
            float v = Input.GetAxis("Vertical");

            Vector3 move = transform.forward * v + transform.right * h;
            if (_controller != null)
            {
                _controller.Move(move * (_moveSpeed * Time.deltaTime));
            }
            else
            {
                transform.position += move * (_moveSpeed * Time.deltaTime);
            }

            float mouseX = Input.GetAxis("Mouse X");
            transform.Rotate(0f, mouseX * _rotateSpeed * Time.deltaTime, 0f);
        }

        /// <summary>
        /// Increment this player's score (owner-side).
        /// In RTMPE, the owning client updates NetworkVariables; the server
        /// broadcasts the new value to all other clients automatically at 30 Hz.
        /// </summary>
        public void AddScore(int points)
        {
            if (!IsOwner) return;
            if (_score == null) return;
            _score.Value += points;
        }
    }
}
