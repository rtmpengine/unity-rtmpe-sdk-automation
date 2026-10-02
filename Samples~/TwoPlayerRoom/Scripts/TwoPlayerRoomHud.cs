using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// Four lines on screen: the connection state, whether this client is in a
    /// room, how many avatars are spawned here, and whether this client has an
    /// API key. Enough to tell, from either window, which client has arrived and
    /// why one has not.
    /// </summary>
    /// <remarks>
    /// <para>Drawn with <c>OnGUI</c>, which needs no Canvas and no input package,
    /// so it also runs in a project that uses only the Input System package.</para>
    /// <para>The API key line matters most in a player build, which reports a
    /// missing key only in its log file. It shows whether a key arrived, never
    /// the key or its length, and it reads the answer
    /// <c>TwoPlayerCredentials</c> already has rather than resolving the key on
    /// every repaint.</para>
    /// </remarks>
    public sealed class TwoPlayerRoomHud : MonoBehaviour
    {
        private void OnGUI()
        {
            var manager = NetworkManager.Instance;

            // `Instance` answers null with no manager in the scene and while the
            // application is quitting, and OnGUI runs in both.
            string state = manager == null ? "no NetworkManager" : manager.State.ToString();
            string room = manager != null && manager.IsInRoom ? "yes" : "no";

            // Three states: null means nothing has checked, which is what a
            // scene without TwoPlayerCredentials looks like.
            bool? key = TwoPlayerCredentials.ApiKeyIsAvailable;
            string credential =
                key == null
                    ? "nothing asked — add TwoPlayerCredentials to this scene"
                    : key.Value
                        ? "supplied"
                        : "MISSING — this client will not connect; see the console";

            GUI.Label(new Rect(12f, 12f, 560f, 22f), "RTMPE state: " + state);
            GUI.Label(new Rect(12f, 34f, 560f, 22f), "In room: " + room);
            GUI.Label(new Rect(12f, 56f, 560f, 22f),
                      "Avatars spawned here: " + TwoPlayerAvatar.LiveCount);
            GUI.Label(new Rect(12f, 78f, 560f, 22f), "API key: " + credential);
        }
    }
}
