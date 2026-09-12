// RTMPE SDK — Sample: Scene Transitions
//
// The half of a scene transition that stays yours: deciding WHEN the room should
// change scene. The other half — loading it on every client and reporting back —
// is RtmpeSceneLoader, which ships in the SDK and appears nowhere in this file.
//
// That absence is the sample. There is no LoadSceneAsync here, no coroutine, no
// completion callback and no ReportReady: attaching one component removed all of
// it, and what is left is a button and a call.
//
// ⚠️ Driven from OnGUI rather than from Input, and the reason is not taste:
// UnityEngine.Input THROWS on a project configured for the new Input System
// package with the old Input Manager disabled, which is a growing share of
// them. A sample that cannot run in a supported project teaches nothing. The
// sibling BasicConnection sample is driven the same way.

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

            // ⛔ Disabled rather than hidden, and disabled rather than left
            // pressable. LoadScene REFUSES locally — it throws for a caller who
            // is not in a room and for one who is not the master client — so a
            // button that is always live turns the first thing a user does into
            // an exception out of OnGUI, which aborts the rest of the GUI pass.
            // A sample is the first code an integrator copies.
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
               && manager.IsInRoom
               && manager.IsMasterClient;

        private void Ask(string sceneName)
        {
            if (!CanChangeScene()) return;

            // ⚠️ The gate above is about not throwing out of OnGUI, and nothing
            // more. It is not an authorisation: the SERVER decides whether this
            // client may write the room's scene, and a client-side answer is
            // only ever an opinion about a roster this one may already be behind
            // on — one that would refuse a legitimate host mid-handover, which
            // is the moment a room most needs to move.
            //
            // Asking twice for the scene the room is already on is a RELOAD, not
            // a no-op: it is how a restart is expressed, and every client reloads.
            NetworkManager.Instance.Scene.LoadScene(sceneName);
        }
    }
}
