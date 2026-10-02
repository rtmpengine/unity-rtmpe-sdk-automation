// RTMPE SDK — Editor/LinkSimulatorPanel.cs
//
// The Link Simulator panel of the Network Debugger: the bench's dials, its
// status line, and — while a session runs — what the shaped link is doing
// to it and what the session is doing about it: the reliable ladder's
// timeout and round trip, its resends and give-ups, the RPC repeats the
// receiver witnessed, and each replica's resolver, absorbed error and
// underruns.
//
// Drawn from a view the window fills, not from the manager: the window is
// the one file that reads NetworkManager, so everything decided here — which
// line says what, in which state — is driven whole under the headless test
// runner with the Editor shimmed.  The panel decides nothing about the
// readings; it shows what the runtime counted.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using RTMPE.Transport;

namespace RTMPE.Editor
{
    /// <summary>One replica, as the panel shows it.</summary>
    internal readonly struct ReplicaReading
    {
        internal ReplicaReading(
            string name, ulong id, int buffered, string resolver,
            float lagBehindNewest, float absorbing, long frozenFrames, double newestSampleAgeSeconds)
        {
            Name                   = name;
            Id                     = id;
            Buffered               = buffered;
            Resolver               = resolver;
            LagBehindNewest        = lagBehindNewest;
            Absorbing              = absorbing;
            FrozenFrames           = frozenFrames;
            NewestSampleAgeSeconds = newestSampleAgeSeconds;
        }

        internal string Name { get; }
        internal ulong  Id { get; }
        /// <summary>Snapshots in the interpolator's buffer.</summary>
        internal int    Buffered { get; }
        /// <summary>Which resolver answered the last frame: interpolated, single, extrapolated, or none.</summary>
        internal string Resolver { get; }
        /// <summary>
        /// How far the pose on screen stands from the newest snapshot the owner
        /// sent, in world units — the replica's positional lag behind what it
        /// knows; negative while nothing has been rendered or received.
        /// </summary>
        internal float  LagBehindNewest { get; }
        /// <summary>The pose error the render absorber is still walking out, in world units; 0 when nothing is being absorbed.</summary>
        internal float  Absorbing { get; }
        /// <summary>Frames on which the pose on screen stood still for want of data.</summary>
        internal long   FrozenFrames { get; }
        /// <summary>Seconds since the newest snapshot was accepted (never negative), or −1 while none has been.</summary>
        internal double NewestSampleAgeSeconds { get; }
    }

    /// <summary>
    /// What the window hands the panel each repaint.  Reused across repaints;
    /// <see cref="Clear"/> before filling.
    /// </summary>
    internal sealed class LinkSimulatorView
    {
        internal bool Playing;
        internal bool ManagerExists;
        internal bool LiveIsShaped;

        internal bool         HasLanes;
        internal LaneReadings Outbound;
        internal LaneReadings Inbound;

        internal float RtoSeconds;
        internal float SmoothedRttSeconds;
        internal float HeartbeatRttMs;
        internal long  Retransmits;
        internal long  GivenUp;
        internal long  RpcRepeats;

        internal readonly List<ReplicaReading> Replicas = new List<ReplicaReading>(16);

        internal void Clear()
        {
            Playing = ManagerExists = LiveIsShaped = HasLanes = false;
            Outbound = default;
            Inbound = default;
            RtoSeconds = SmoothedRttSeconds = HeartbeatRttMs = 0f;
            Retransmits = GivenUp = RpcRepeats = 0L;
            Replicas.Clear();
        }
    }

    /// <summary>The Link Simulator panel's controls and readings.</summary>
    internal static class LinkSimulatorPanel
    {
        internal const string ToggleLabel      = "Simulate the link";
        internal const string DelayLabel       = "Delay (ms, one way)";
        internal const string JitterLabel      = "Jitter (± ms)";
        internal const string LossLabel        = "Loss (%)";
        internal const string ReorderLabel     = "Allow reordering";
        internal const string AcceptanceButton = "Preset: 250 ms ± 50 ms, 5 % loss";

        // The dials' reach.  LinkConditions admits more; a bench dial that
        // ran to ten seconds would spend most of its travel on links nobody
        // tests against.
        internal const int   DelayDialMaxMs  = 2_000;
        internal const int   JitterDialMaxMs = 500;
        internal const float LossDialMax     = 100f;

        internal const string RpcRepeatsLabel = "RPC repeats received";
        internal const string ResentLabel     = "Re-sent";

        // The controls' labels and tooltips, built once: the window's rule is
        // no per-repaint allocation beyond what a panel needs, and a label
        // needs none.
        private static readonly GUIContent ToggleContent = new GUIContent(ToggleLabel,
            "Wrap the next session's transport in a link simulator: every datagram in " +
            "both directions is held for the delay (± the jitter), one in the loss share " +
            "is dropped, and jitter may reorder only when allowed. Editor only; the " +
            "setting is this Editor's and ends with it.");
        private static readonly GUIContent DelayContent = new GUIContent(DelayLabel,
            "Held this long in each direction; the round trip costs twice it.");
        private static readonly GUIContent JitterContent = new GUIContent(JitterLabel,
            "A uniform draw within ± this is added to each datagram's hold.");
        private static readonly GUIContent LossContent = new GUIContent(LossLabel,
            "Share of datagrams dropped, in each direction. A datagram lost on the way in is " +
            "lost for good: the server does not resend it.");
        private static readonly GUIContent ReorderContent = new GUIContent(ReorderLabel,
            "Let jitter deliver a datagram ahead of one sent before it. Off, a datagram waits for the one " +
            "ahead of it, so jitter bunches datagrams as a queue on one path does.");

        /// <summary>Draw the panel's body (the window owns the foldout around it).</summary>
        internal static void Draw(LinkSimulatorView view)
        {
            bool enabled = EditorGUILayout.Toggle(ToggleContent, LinkSimulatorBench.Enabled);
            if (enabled != LinkSimulatorBench.Enabled) LinkSimulatorBench.SetEnabled(enabled);

            LinkConditions set = LinkSimulatorBench.Conditions;
            int delay = EditorGUILayout.IntSlider(DelayContent, set.DelayMs, 0, DelayDialMaxMs);
            int jitter = EditorGUILayout.IntSlider(JitterContent, set.JitterMs, 0, JitterDialMaxMs);
            float loss = EditorGUILayout.Slider(LossContent, set.LossPercent, 0f, LossDialMax);
            bool reorder = EditorGUILayout.Toggle(ReorderContent, set.Reorder);

            // Handed over on every repaint; the bench is the one place that
            // decides whether anything changed.
            LinkSimulatorBench.SetConditions(new LinkConditions(delay, jitter, loss, reorder));

            if (GUILayout.Button(AcceptanceButton))
                LinkSimulatorBench.SetConditions(LinkSimulatorBench.AcceptanceProfile);

            EditorGUILayout.HelpBox(
                LinkSimulatorBench.Status(view.Playing, view.ManagerExists, view.LiveIsShaped, view.HasLanes),
                LinkSimulatorBench.Enabled ? MessageType.Info : MessageType.None);

            if (!view.Playing || !view.ManagerExists) return;

            using (new EditorGUI.DisabledScope(true))
            {
                DrawLanes(view);
                DrawReliable(view);
                DrawReplicas(view);
            }
        }

        private static void DrawLanes(LinkSimulatorView view)
        {
            if (!view.HasLanes) return;
            EditorGUILayout.LabelField("Link (out)", Lane(view.Outbound));
            // A datagram lost on the way in is not resent by the relay: what
            // it carried — an RPC, a spawn, a property write — this client
            // never gets.  Said on the line, beside the count.
            EditorGUILayout.LabelField("Link (in)", Lane(view.Inbound)
                + (view.Inbound.Lost > 0 ? " · lost here stays lost (the relay does not resend)" : string.Empty));
        }

        // What one lane did, in the order a reader asks: delivered, lost,
        // still held; then only what happened.
        internal static string Lane(LaneReadings lane)
        {
            var c = CultureInfo.InvariantCulture;
            string text = lane.Delivered.ToString("N0", c) + " delivered · "
                + lane.Lost.ToString("N0", c) + " lost · "
                + lane.Queued.ToString(c) + " held";
            if (lane.Reordered > 0)       text += " · " + lane.Reordered.ToString("N0", c) + " reordered";
            if (lane.Overflowed > 0)      text += " · " + lane.Overflowed.ToString("N0", c) + " overflowed";
            if (lane.Refused > 0)         text += " · " + lane.Refused.ToString("N0", c) + " refused";
            if (lane.Discarded > 0)       text += " · " + lane.Discarded.ToString("N0", c) + " discarded at a close or reconnect";
            if (lane.BufferExhausted > 0) text += " · " + lane.BufferExhausted.ToString("N0", c) + " send-buffer waits";
            return text;
        }

        private static void DrawReliable(LinkSimulatorView view)
        {
            var c = CultureInfo.InvariantCulture;

            EditorGUILayout.LabelField(
                "Reliable RTO",
                (view.RtoSeconds * 1000f).ToString("F0", c) + " ms");
            EditorGUILayout.LabelField(
                "Smoothed RTT",
                view.SmoothedRttSeconds > 0f
                    ? (view.SmoothedRttSeconds * 1000f).ToString("F0", c) + " ms  (frame → DataAck)"
                    : "— (no acknowledgement sampled yet)");
            EditorGUILayout.LabelField(
                "Heartbeat RTT",
                view.HeartbeatRttMs < 0f ? "— (not yet measured)" : view.HeartbeatRttMs.ToString("F0", c) + " ms");
            EditorGUILayout.LabelField(
                ResentLabel,
                view.Retransmits.ToString("N0", c) + "  (more copies; the relay routes each frame once)");
            EditorGUILayout.LabelField(
                "Given up",
                view.GivenUp == 0
                    ? "0"
                    : view.GivenUp.ToString("N0", c) + "  [frames no acknowledgement ever reached]");
            EditorGUILayout.LabelField(
                RpcRepeatsLabel,
                view.RpcRepeats == 0
                    ? "0  (once-delivery holds)"
                    : view.RpcRepeats.ToString("N0", c)
                        + "  [non-zero: an RPC was applied twice — or two calls of one sender drew one request id]");
        }

        private static void DrawReplicas(LinkSimulatorView view)
        {
            if (view.Replicas.Count == 0)
            {
                EditorGUILayout.LabelField("Replicas", "none (no remote NetworkTransform in the room)");
                return;
            }

            for (int i = 0; i < view.Replicas.Count; i++)
                EditorGUILayout.LabelField(Title(view.Replicas[i]), Replica(view.Replicas[i]));
        }

        internal static string Title(ReplicaReading r)
            => r.Name + "  (id " + r.Id.ToString(CultureInfo.InvariantCulture) + ")";

        // One replica in one line: which resolver is answering, how far the
        // pose on screen lags the newest snapshot, what the absorber is still
        // walking out, how deep the buffer is, how many frames froze, and how
        // long since its owner was last heard from.
        internal static string Replica(ReplicaReading r)
        {
            var c = CultureInfo.InvariantCulture;
            string lag = r.LagBehindNewest < 0f ? "lag —" : "lag " + r.LagBehindNewest.ToString("F2", c);
            string age = r.NewestSampleAgeSeconds < 0
                ? "no sample yet"
                : "last sample " + (r.NewestSampleAgeSeconds * 1000.0).ToString("F0", c) + " ms ago";
            return r.Resolver
                + " · " + lag
                + " · absorbing " + r.Absorbing.ToString("F2", c)
                + " · " + r.Buffered.ToString(c) + " buffered"
                + " · " + r.FrozenFrames.ToString("N0", c) + " frozen frames"
                + " · " + age;
        }
    }
}
#endif
