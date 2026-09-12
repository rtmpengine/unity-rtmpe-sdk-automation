using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// Four lines on screen: the connection state, whether this client is in a
    /// room, how many avatars are spawned here, and whether this client was
    /// given an API key at all. Enough to tell, from either window, which of the
    /// two clients has arrived — and why one of them never did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn with <c>OnGUI</c> rather than read with <c>Input</c>: a project
    /// configured for the Input System package alone throws out of
    /// <c>UnityEngine.Input</c>, and a readout that cannot run in a supported
    /// project tells nobody anything. <c>OnGUI</c> needs no package and no
    /// Canvas.
    /// </para>
    /// <para>
    /// 🔑 The credential line is here because the failure this sample produces
    /// most often is a launch flag somebody forgot, and the SDK reports that to
    /// the console — which in a built player is a <c>Player.log</c> file whose
    /// path nobody knows. Three lines saying <c>Disconnected / no / 0</c> name
    /// no cause; a fourth saying the key never arrived names it exactly.
    /// </para>
    /// <para>
    /// ⛔ It reports the FACT and only the fact. Never the key, never its
    /// length: a length is a fingerprint of a secret, and a readout is the one
    /// surface in a game that gets screenshotted and streamed.
    /// </para>
    /// <para>
    /// ⚠️ It reads an answer <c>TwoPlayerCredentials</c> already took rather
    /// than asking itself. Resolving a key reads a file, and this method runs
    /// on every repaint — several times a frame.
    /// </para>
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

            // Three states, and the third is not padding: no ask at all is what
            // a scene missing TwoPlayerCredentials looks like, and reporting
            // that as "none" would be a wrong answer rather than a missing one.
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
