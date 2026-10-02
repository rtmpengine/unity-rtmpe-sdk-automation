// RTMPE SDK — Sample: Scene Transitions
//
// This component decides when the room changes scene. Loading the scene on
// every client and reporting back is done by RtmpeSceneLoader, which ships in
// the SDK, so this file needs no LoadSceneAsync, coroutine, completion callback
// or ReportReady: just a button and a call.
//
// The buttons are drawn with OnGUI rather than read from UnityEngine.Input,
// which throws in a project that uses only the Input System package.

using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.SceneTransitions
{
    /// <summary>
    /// Asks the room to move to another scene when a button is pressed.
    /// </summary>
    /// <remarks>
    /// Attach beside a <c>NetworkManager</c> and an <c>RtmpeSceneLoader</c>.
    /// Both scene names must be in Build Settings.
    /// </remarks>
    public sealed class SceneSwitcher : MonoBehaviour
    {
        [Tooltip("Loaded by the first button. Must be in Build Settings.")]
        public string firstScene = "Arena";

        [Tooltip("Loaded by the second button. Must be in Build Settings.")]
        public string secondScene = "Lobby";

        private void OnGUI()
        {
            const int pad = 12;

            GUI.Box(new Rect(pad, pad, 320, 86), GUIContent.none);
            GUI.Label(new Rect(pad + 8, pad + 6, 300, 20), Headline());

            // Disabled outside a room: LoadScene throws for a caller that is not
            // in a room, and an exception out of OnGUI aborts the rest of the GUI
            // pass. The buttons are not limited to the host (IsMasterClient): the
            // server decides who may change the scene, and this client's view of
            // who is host can briefly be out of date.
            GUI.enabled = CanChangeScene();
            if (GUI.Button(new Rect(pad + 8, pad + 30, 130, 24), firstScene))  Ask(firstScene);
            if (GUI.Button(new Rect(pad + 8, pad + 58, 130, 24), secondScene)) Ask(secondScene);
            GUI.enabled = true;
        }

        private static string Headline()
        {
            if (!NetworkManager.TryGetInstance(out var manager)) return "[RTMPE] not connected";
            if (manager.Scene == null || !manager.IsInRoom)      return "[RTMPE] join a room first";
            return manager.IsMasterClient
                ? "[RTMPE] you are the host — pick a scene"
                : "[RTMPE] the host chooses the scene";
        }

        private static bool CanChangeScene()
            => NetworkManager.TryGetInstance(out var manager)
               && manager.Scene != null
               && manager.IsInRoom;

        private void Ask(string sceneName)
        {
            if (!CanChangeScene()) return;

            // The check above only avoids an exception; it authorises nothing.
            // The server applies only the host's request, and a refusal arrives
            // as RoomManager.OnRoomError.
            //
            // Asking for the scene the room is already on reloads it on every
            // client; that is how a restart is expressed.
            NetworkManager.Instance.Scene.LoadScene(sceneName);
        }
    }
}
