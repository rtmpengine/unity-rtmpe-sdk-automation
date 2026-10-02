// RTMPE SDK — Editor/LinkSimulatorArming.cs
//
// Hands the link simulator bench to the runtime and keeps it across a domain
// reload.  The bench itself (LinkSimulatorBench) knows nothing of the Editor
// or the manager; this is the one place both are named.
//
// Shape: [InitializeOnLoad], so the static constructor runs on Editor load
// and after every domain reload — including the one on the way into play
// mode, ahead of any scene Awake — and there it takes the bench back from
// the Editor's session state and installs the transport shaper on
// NetworkManager, so a bench set in edit mode shapes the session that starts
// next.  With domain reloading disabled the statics survive instead and the
// shaper stays installed.  Every later change to the bench is written to
// the session state and re-installed from here.
//
// Session state, not EditorPrefs, for the reason the play-mode observer
// gives for its seats: a second Editor on this machine playing the other
// player must not read this Editor's simulator as its own, and an Editor
// restarted must not find a slow network it never asked for.

#if UNITY_EDITOR
using UnityEditor;
using RTMPE.Core;

namespace RTMPE.Editor
{
    /// <summary>
    /// Installs the link simulator's transport shaper on
    /// <see cref="NetworkManager"/> and persists the bench in session state.
    /// </summary>
    [InitializeOnLoad]
    internal static class LinkSimulatorArming
    {
        static LinkSimulatorArming()
        {
            LinkSimulatorBench.Restore(
                SessionState.GetString(LinkSimulatorBench.SessionStateKey, string.Empty));
            LinkSimulatorBench.Changed += OnBenchChanged;
            Arm();
        }

        private static void OnBenchChanged()
        {
            SessionState.SetString(LinkSimulatorBench.SessionStateKey, LinkSimulatorBench.Persisted());
            Arm();
        }

        // The runtime's second transport seam: applied over whatever transport
        // the next session was going to run on, and taking effect at its next
        // connection attempt, like a factory change.
        private static void Arm()
        {
            if (LinkSimulatorBench.Enabled)
                NetworkManager.SetTransportShaper(LinkSimulatorBench.Shape);
            else
                NetworkManager.SetTransportShaper(null);
        }
    }
}
#endif
