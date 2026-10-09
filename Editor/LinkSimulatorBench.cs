// RTMPE SDK — Editor/LinkSimulatorBench.cs
//
// The Editor's link simulator, as a bench: what it is set to, whether it is
// armed, the transport it shapes, and the one line that says which of those
// holds for the session in hand.  The Network Debugger's Link Simulator
// panel draws it; LinkSimulatorArming hands it to the runtime (the transport
// shaper seam on NetworkManager) and keeps it across a domain reload.
//
// Two facts, kept apart on purpose: ARMED is about the next session — the
// shaper is installed and the next connection attempt is wrapped — and
// SHAPING is about the live one, read from the manager's own transport.
// Turning the bench on while a session runs arms it and shapes nothing until
// that session's next connection attempt, which is the rule the transport
// factory documents and the reason the panel is drawn in edit mode too: set
// it, press Play.
//
// The conditions, unlike the arming, reach the live transport at once — a
// dial moved while playing shapes the next datagram — because the transport
// reads its conditions per datagram and this bench keeps the one it shaped.
//
// Nothing here reads UnityEngine or the runtime's manager, so the bench is
// driven whole under the headless test runner; what needs the Editor is in
// LinkSimulatorArming.

#if UNITY_EDITOR
using System;
using System.Globalization;
using RTMPE.Transport;

namespace RTMPE.Editor
{
    /// <summary>
    /// The Editor's link simulator: its conditions, whether it is armed for
    /// the next session, and the transport it is shaping.
    /// </summary>
    internal static class LinkSimulatorBench
    {
        /// <summary>
        /// Where the arming keeps the bench between domain reloads: this
        /// Editor's session state, so a second Editor playing the other
        /// player does not inherit it, and an Editor restarted does not
        /// either — a simulator left on across a restart would read as a slow
        /// network next week.
        /// </summary>
        internal const string SessionStateKey = "RTMPE.LinkSimulator";

        /// <summary>
        /// The profile the roadmap accepts the SDK against: 250 ms each way,
        /// ±50 ms, one datagram in twenty lost, order kept.  Also what the
        /// dials show before anyone moves them.
        /// </summary>
        internal static readonly LinkConditions AcceptanceProfile = new LinkConditions(250, 50, 5f, false);

        private static bool s_enabled;
        private static LinkConditions s_conditions = AcceptanceProfile;
        private static SimulatedLinkTransport s_shaped;

        /// <summary>Whether the next session's transport is to be shaped.</summary>
        internal static bool Enabled => s_enabled;

        /// <summary>The conditions the dials are set to.</summary>
        internal static LinkConditions Conditions => s_conditions;

        /// <summary>
        /// Raised after every change to <see cref="Enabled"/> or
        /// <see cref="Conditions"/>, so the arming can re-install and persist.
        /// </summary>
        internal static event Action Changed;

        /// <summary>Arm or disarm the bench for the next session.</summary>
        internal static void SetEnabled(bool enabled)
        {
            if (s_enabled == enabled) return;
            s_enabled = enabled;
            Changed?.Invoke();
        }

        /// <summary>
        /// Set the dials.  A transport this bench shaped reads the new
        /// conditions on its next datagram.
        /// </summary>
        internal static void SetConditions(LinkConditions conditions)
        {
            if (s_conditions == conditions) return;
            s_conditions = conditions;
            var shaped = s_shaped;
            if (shaped != null) shaped.Conditions = conditions;
            Changed?.Invoke();
        }

        /// <summary>
        /// The shaper the arming installs: wraps the transport a session was
        /// going to run on in a <see cref="SimulatedLinkTransport"/> under the
        /// current conditions, and keeps it so later dial moves reach it.
        /// </summary>
        internal static NetworkTransport Shape(NetworkTransport built)
        {
            if (built == null) return null;
            var shaped = new SimulatedLinkTransport(built, s_conditions);
            s_shaped = shaped;
            return shaped;
        }

        /// <summary>
        /// The transport this bench shaped most recently, or null.  The panel
        /// reads lane counts from the manager's live transport rather than
        /// from here; this is what dial moves are pushed to.
        /// </summary>
        internal static SimulatedLinkTransport Shaped => s_shaped;

        // ── Persistence ───────────────────────────────────────────────────────

        /// <summary>
        /// The bench as one line for the session state:
        /// <c>enabled;delay;jitter;loss;reorder</c>, invariant culture.
        /// </summary>
        internal static string Persisted()
        {
            var c = CultureInfo.InvariantCulture;
            return (s_enabled ? "1" : "0") + ";"
                + s_conditions.DelayMs.ToString(c) + ";"
                + s_conditions.JitterMs.ToString(c) + ";"
                + s_conditions.LossPercent.ToString("R", c) + ";"
                + (s_conditions.Reorder ? "1" : "0");
        }

        /// <summary>
        /// Take the bench back from a line <see cref="Persisted"/> wrote.  A
        /// line that is empty or does not parse whole leaves the bench off at
        /// the acceptance profile — a half-read line would arm a simulator
        /// nobody set.  Raises nothing: this is the state being restored, not
        /// changed.
        /// </summary>
        internal static void Restore(string persisted)
        {
            s_enabled    = false;
            s_conditions = AcceptanceProfile;
            if (string.IsNullOrEmpty(persisted)) return;

            string[] parts = persisted.Split(';');
            if (parts.Length != 5) return;
            var c = CultureInfo.InvariantCulture;
            if (!TryFlag(parts[0], out bool enabled)) return;
            if (!int.TryParse(parts[1], NumberStyles.Integer, c, out int delay)) return;
            if (!int.TryParse(parts[2], NumberStyles.Integer, c, out int jitter)) return;
            if (!float.TryParse(parts[3], NumberStyles.Float, c, out float loss)) return;
            if (!TryFlag(parts[4], out bool reorder)) return;

            s_enabled    = enabled;
            s_conditions = new LinkConditions(delay, jitter, loss, reorder);
        }

        private static bool TryFlag(string text, out bool value)
        {
            value = text == "1";
            return text == "1" || text == "0";
        }

        // ── The status line ───────────────────────────────────────────────────

        /// <summary>
        /// One line for the panel, about the session in hand.
        /// </summary>
        /// <param name="playing">Whether the Editor is in play mode.</param>
        /// <param name="managerExists">Whether a <c>NetworkManager</c> holds a transport.</param>
        /// <param name="liveIsShaped">Whether that transport is one this bench shaped.</param>
        /// <param name="liveIsSimulated">
        /// Whether that transport is a <see cref="SimulatedLinkTransport"/> at
        /// all — the bench's, or one the project composed in its own factory,
        /// which these dials do not reach.
        /// </param>
        internal static string Status(bool playing, bool managerExists, bool liveIsShaped, bool liveIsSimulated)
        {
            string set = s_conditions.Describe();

            if (!s_enabled)
            {
                if (playing && managerExists && liveIsShaped)
                    return "Off — but the live session is still on the simulated link (" + set
                        + "), until its next connection attempt.";
                if (playing && managerExists && liveIsSimulated)
                    return "Off. The live session runs on a link simulator the project installed through its"
                        + " own transport factory; these dials do not reach it, its lanes are shown below.";
                return "Off. The next session runs on the network as it is.";
            }

            if (!playing)
                return "Armed for the next Play: " + set + ". Every datagram in both directions"
                    + " is held for the delay, so the round trip costs twice it.";

            if (!managerExists)
                return "Armed: " + set + ". The session's transport is shaped when the NetworkManager builds it.";

            if (liveIsShaped)
                return "Shaping the live session: " + set + ". A dial moved now shapes the next datagram;"
                    + " the readings below are this session's.";

            if (liveIsSimulated)
                return "Armed (" + set + "), and the transport in hand is a link simulator the project composed"
                    + " in its own factory. At the next connection attempt the bench wraps it in a second"
                    + " one and the two shape in series — the delays add, and the inner one's lanes are"
                    + " not shown here.";

            return "Armed (" + set + "), but the transport in hand was built before the simulator was"
                + " armed. It is shaped at the next connection attempt: Connect again (after Disconnect,"
                + " if a session is live), or stop and start Play.";
        }
    }
}
#endif
