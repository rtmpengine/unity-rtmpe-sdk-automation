// RTMPE SDK — Sample: Shared World
//
// State no player owns: a board of food cells, laid out at random, on the
// room's world object beside RtmpeWorldAuthority. It replicates like any
// spawned object's variables.
//
// The host writes, everybody asks. The board is a NetworkVariableList only the
// owner may change, and the owner is the room's current host: the SDK hands
// the world, with its state, to the next host when the host leaves. A player
// who wants to eat a cell sends an RPC; the owner's copy applies it, and the
// change reaches everyone through the list.
//
// The board is filled from OnWorldBorn, not OnNetworkSpawn. OnNetworkSpawn
// runs on every client for every instance of the world; OnWorldBorn runs on
// the owner for a world that holds none, or only part, of the room's state.
// Populate tops the board up rather than regenerating it, because a world
// born this way may already hold some cells.
//
// The owner's handler trusts the cell it is asked about: its methods do not
// check the caller, so a modified client could ask for any cell. Put a
// decision no client may make in the project's server function.

using System;
using RTMPE.Core;
using RTMPE.Rpc;
using RTMPE.Sync;
using UnityEngine;

namespace RTMPE.Samples.SharedWorld
{
    /// <summary>
    /// The shared board: which cells hold food, and how many have been eaten.
    /// Goes on the world prefab beside <see cref="RtmpeWorldAuthority"/>.
    /// </summary>
    /// <remarks>
    /// It derives from <see cref="NetworkBehaviour"/>, and that abstract base
    /// is never added as a component on its own. Reach the live instance
    /// through <c>RtmpeWorldAuthority.Find(WorldKey)</c> and
    /// <c>GetComponent</c>; hold no reference across frames, because a host
    /// migration can replace the object.
    /// </remarks>
    public class SharedWorldState : NetworkBehaviour
    {
        /// <summary>The key the world prefab's <c>RtmpeWorldAuthority</c> carries.</summary>
        public const string WorldKey = "shared-world";

        /// <summary>Cells per side of the square board.</summary>
        public const int GridSize = 8;

        /// <summary>How many cells hold food at any time.</summary>
        public const int FoodCount = 6;

        // Created in OnNetworkSpawn, where the object's identity is known.
        // Both replicate to every client; only the owner can write them.
        private NetworkVariableListVector2Int _food;
        private NetworkVariableInt            _eaten;

        private RtmpeWorldAuthority _authority;
        private readonly System.Random _random = new System.Random();

        /// <summary>Raised on this client whenever the board or the count changes.</summary>
        public event Action OnBoardChanged;

        /// <summary>How many cells have been eaten, room-wide.</summary>
        public int Eaten => _eaten != null ? _eaten.Value : 0;

        /// <summary>Whether <paramref name="cell"/> holds food right now.</summary>
        public bool HasFoodAt(Vector2Int cell) => _food != null && _food.Contains(cell);

        // ── Lifecycle ──────────────────────────────────────────────────────────

        protected override void OnNetworkSpawn()
        {
            _food  = new NetworkVariableListVector2Int(this, nameof(_food));
            _eaten = new NetworkVariableInt(this, nameof(_eaten));
            _food.OnListChanged   += HandleFoodChanged;
            _eaten.OnValueChanged += HandleEatenChanged;

            // Subscribed here so it is on the list by the time the world's
            // first frame raises it — on the owner, for a brand-new world.
            _authority = GetComponent<RtmpeWorldAuthority>();
            if (_authority != null) _authority.OnWorldBorn += Populate;
        }

        protected override void OnNetworkDespawn()
        {
            if (_authority != null) _authority.OnWorldBorn -= Populate;
            _authority = null;
        }

        // ── The owner's half ───────────────────────────────────────────────────

        // Fills the board on the owner until it holds FoodCount cells. A world
        // handed over with its state already has its cells; one that arrived
        // with part of its state keeps them and gets the rest.
        //
        // The loop also stops when the list is full: the list refuses to grow
        // past its configured ceiling, which may be lower than FoodCount.
        private void Populate()
        {
            if (!IsOwner) return;
            for (int i = _food.Count; i < FoodCount && !_food.IsFull; i++)
            {
                if (!TryFindFreeCell(out var cell) || !_food.TryAdd(cell)) break;
            }
        }

        /// <summary>
        /// Ask the world to eat the food at <paramref name="cell"/>. Any client
        /// may ask; the owner decides.
        /// </summary>
        public void AskToEat(Vector2Int cell) => RPC(nameof(RequestEat), cell.x, cell.y);

        /// <summary>
        /// The request, delivered to every client's copy of the world. Only the
        /// owner's copy applies it — the list refuses a write from anyone
        /// else — and the result reaches the rest as list changes.
        /// </summary>
        [RtmpeRpc(RpcTarget.All)]
        public void RequestEat(int x, int y)
        {
            if (!IsOwner) return;
            if (!_food.Remove(new Vector2Int(x, y))) return;
            _eaten.Value = _eaten.Value + 1;
            if (TryFindFreeCell(out var cell)) _food.TryAdd(cell);
        }

        // Finds a random cell with no food; false only when every cell holds
        // food. Random draws are capped, then a scan finds any free cell the
        // draws missed.
        private bool TryFindFreeCell(out Vector2Int cell)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                cell = new Vector2Int(_random.Next(GridSize), _random.Next(GridSize));
                if (!_food.Contains(cell)) return true;
            }
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    cell = new Vector2Int(x, y);
                    if (!_food.Contains(cell)) return true;
                }
            }
            cell = default;
            return false;
        }

        // ── Reporting ──────────────────────────────────────────────────────────

        private void HandleFoodChanged(NetworkVariableListChangeEvent<Vector2Int> change) => OnBoardChanged?.Invoke();

        private void HandleEatenChanged(int previous, int current) => OnBoardChanged?.Invoke();
    }
}
