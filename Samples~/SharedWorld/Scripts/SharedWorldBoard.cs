// RTMPE SDK — Sample: Shared World
//
// Draws the board and turns clicks into requests. The world object is looked
// up on every repaint and never cached: a host migration can replace it with
// a new object, and RtmpeWorldAuthority.Find is a cheap lookup.
//
// A click is a request: this script never writes to the board. It asks the
// world's state component, which sends an RPC that the world's owner applies.
// The same code runs on every client, the host included.

using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.SharedWorld
{
    /// <summary>
    /// Draws the shared board and turns a click on a food cell into a request
    /// to eat it. Attach to any object in the scene.
    /// </summary>
    /// <remarks>
    /// Drawn with <c>OnGUI</c>: it needs no Canvas and no input package, so
    /// it runs in any project the SDK supports.
    /// </remarks>
    public sealed class SharedWorldBoard : MonoBehaviour
    {
        private const float CellSize = 40f;
        private const float BoardLeft = 12f;
        private const float BoardTop = 80f;

        private void OnGUI()
        {
            var manager = NetworkManager.Instance;
            string state = manager == null ? "no NetworkManager" : manager.State.ToString();
            string role  = manager != null && manager.IsInRoom
                ? (manager.IsMasterClient ? "host" : "guest")
                : "not in a room";

            GUI.Label(new Rect(12f, 12f, 560f, 22f), "RTMPE state: " + state);
            GUI.Label(new Rect(12f, 34f, 560f, 22f), "Role: " + role);

            // The world, as of this repaint: null until a host has spawned it
            // and this client has received it, and briefly null while a host
            // migration replaces it.
            var world = RtmpeWorldAuthority.Find(SharedWorldState.WorldKey);
            var board = world != null ? world.GetComponent<SharedWorldState>() : null;
            if (board == null)
            {
                GUI.Label(new Rect(12f, 56f, 560f, 22f), "World: not here yet — a host spawns it on entering the room");
                return;
            }

            // Whose it is, read off the object rather than off the host role:
            // the two agree except between a voluntary TransferMasterClient and
            // the previous host's departure, when the role has moved and the
            // world has not.
            GUI.Label(new Rect(12f, 56f, 560f, 22f),
                      "World: object " + world.NetworkObjectId
                      + (world.IsOwner ? " · owned here" : " · owned by another client")
                      + (world.WasRecreated ? " (migrated copy)" : "")
                      + " · eaten " + board.Eaten);

            for (int y = 0; y < SharedWorldState.GridSize; y++)
            {
                for (int x = 0; x < SharedWorldState.GridSize; x++)
                {
                    var cell = new Vector2Int(x, y);
                    var rect = new Rect(BoardLeft + x * CellSize, BoardTop + y * CellSize, CellSize - 2f, CellSize - 2f);
                    bool food = board.HasFoodAt(cell);
                    if (GUI.Button(rect, food ? "●" : "") && food)
                    {
                        board.AskToEat(cell);
                    }
                }
            }
        }
    }
}
