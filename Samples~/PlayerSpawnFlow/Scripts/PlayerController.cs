using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Samples.PlayerSpawnFlow
{
    /// <summary>
    /// Sample player controller. <c>NetworkTransform</c> sends the owner's
    /// position and rotation to the other clients, and
    /// <c>NetworkTransformInterpolator</c> plays them back smoothly on each copy.
    /// </summary>
    /// <remarks>
    /// <para>Both components are required: without the interpolator, other
    /// players' copies of this object do not move. Unity adds both when you
    /// attach this script to the player prefab.</para>
    /// <para>The movement below reads the legacy Input Manager. In a project set
    /// to the Input System package only, <c>UnityEngine.Input</c> throws: set
    /// <b>Active Input Handling</b> to <i>Both</i> in Player settings, or delete
    /// <c>Update</c>; the spawn flow this sample shows does not need input.</para>
    /// </remarks>
    [RequireComponent(typeof(NetworkTransform), typeof(NetworkTransformInterpolator))]
    public class PlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _rotateSpeed = 120f;

        // Synced score. Created in OnNetworkSpawn, where 'this' can be passed
        // as the owner; its network identity comes from the class and field
        // names, so no id has to be chosen.
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
        /// Adds to this player's score. Only the owning client can change it; the
        /// new value reaches the other clients automatically.
        /// </summary>
        /// <param name="points">The points to add.</param>
        public void AddScore(int points)
        {
            if (!IsOwner) return;
            if (_score == null) return;
            _score.Value += points;
        }
    }
}
