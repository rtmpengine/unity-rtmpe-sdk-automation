// RTMPE SDK — Editor/NetworkTransformEditor.cs
//
// The one-click repair for the fault that is invisible while you test alone.
//
// 🔑 A non-owner replica carrying NetworkTransform and no
// NetworkTransformInterpolator FREEZES: both receive paths in
// NetworkManager.GameData return without touching the transform when the
// interpolator is absent. The runtime does say so — but on the RECEIVE path,
// which is only reached once a second client is in the room, so a developer
// testing alone never hears it and ships a game whose objects stand still for
// everybody else.
//
// ⛔ The DECISION is not made here. It is NetworkPrefabsInventory.ClassifyMotion,
// which is compiled and driven by tests; this file is the GUI around it. A
// judgement written inline in OnInspectorGUI is a judgement no test can reach,
// and no compiler in this repository reads this file at all.
//
// 🚨 DERIVED FROM NetworkObjectEditor, and that is load-bearing rather than
// tidy. NetworkObjectEditor is [CustomEditor(typeof(NetworkBehaviour), true)] —
// it draws the network-identity block, the ownership badge and the play-mode
// actions for every NetworkBehaviour, NetworkTransform included. Unity resolves
// the MOST SPECIFIC CustomEditor for a type, so a drawer here declared against
// UnityEditor.Editor would REPLACE that one for NetworkTransform and silently
// delete the identity panel from every project on upgrade — an unrequested
// change of exactly the kind [RequireComponent] on this component was refused
// for. Deriving and calling base keeps every pixel and adds one block.
//
// ⚠️ [RequireComponent] is NOT the repair. An object carrying NetworkTransform
// beside NetworkRigidbody would begin having transform.position written by the
// interpolator, changing motion in existing projects on upgrade. This offers the
// component; it does not impose it.

#if UNITY_EDITOR
using System.Collections.Generic;
using RTMPE.Core;
using RTMPE.Sync;
using UnityEditor;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// <see cref="NetworkObjectEditor"/> plus the remote-motion advisory and the
    /// button that settles it.
    /// </summary>
    [CustomEditor(typeof(NetworkTransform), true)]   // true = apply to subclasses
    [CanEditMultipleObjects]
    public sealed class NetworkTransformEditor : NetworkObjectEditor
    {
        /// <summary>
        /// What the Undo history calls the repair this drawer performs.
        /// </summary>
        /// <remarks>
        /// One label for both shapes of it — adding the component and switching
        /// an existing one on — because a reader undoing a mixed selection is
        /// undoing one action they took, whatever this had to do to each member.
        /// </remarks>
        private const string UndoLabel = "Repair remote motion";

        public override void OnInspectorGUI()
        {
            // ⛔ First, and unconditionally: the whole of the base drawer's
            // panel, which is the network-identity block, the ownership badge
            // and the play-mode actions.
            base.OnInspectorGUI();
            DrawMotionPairing();
        }

        // ⛔ Over `targets`, never `target`. `target` is the FIRST member of a
        // multi-selection, so deciding from it hides the advisory whenever that
        // one object happens to be paired, while every object behind it stays
        // broken — and the button below repairs all of them, so the question
        // asked here has to be the question that button answers. Drawn on ANY
        // unpaired member: an advisory withheld until every member is unpaired
        // is withheld in exactly the mixed selection a developer cannot see the
        // fault in.
        private void DrawMotionPairing()
        {
            bool anyUnpaired = false;
            bool switchedOff = false;
            foreach (var selected in targets)
            {
                if (NeedsInterpolator(selected, out GameObject root))
                {
                    anyUnpaired = true;

                    // Which of the two faults the reader is looking at decides
                    // both sentences below. Taken from the first unpaired member
                    // rather than from the whole selection, because that is the
                    // one the inspector is titled after and the walk stops there.
                    switchedOff = DisabledInterpolatorOn(root) != null;
                    break;
                }
            }

            if (!anyUnpaired) return;

            EditorGUILayout.Space(6);

            // ⚠️ The subject is decided by the SELECTION, not by the object the
            // inspector happens to be titled after. Drawn on any unpaired member,
            // the advisory can be about one the reader is not looking at — and a
            // sentence beginning "this object" would then be false for the object
            // in front of them and send them checking a component that is there.
            // The count is left out on purpose: reporting it means walking every
            // member on every repaint, where finding one is enough to draw.
            EditorGUILayout.HelpBox(
                // ⚠️ "is set up to send", not "sends": this advisory is drawn for
                // a NetworkTransform whatever its switch, and a switched-off one
                // sends nothing at all — its pose broadcast is Update-driven. The
                // pairing is still owed, because the switch is one line of
                // somebody's OnNetworkSpawn away from changing; the present tense
                // was not.
                (targets.Length == 1
                    ? "This object is set up to send motion and nothing on it applies "
                      + "motion. "
                    : "An object in this selection is set up to send motion and nothing "
                      + "on it applies motion. ")
                    + NetworkPrefabsInventory.NetworkTransformInterpolatorSimpleName
                    + " is the RECEIVING half: without it every OTHER player's copy of this "
                    + "object stands still, and nothing says so until a second client joins — "
                    + "so a session you test by yourself looks correct."
                    + (switchedOff
                        ? " It is already on this object and SWITCHED OFF, which does the same "
                          + "thing as not being there: Unity runs no Update on a disabled "
                          + "behaviour."
                        : string.Empty),
                MessageType.Warning);

            if (GUILayout.Button(
                (switchedOff ? "Enable " : "Add ")
                + NetworkPrefabsInventory.NetworkTransformInterpolatorSimpleName))
            {
                AddInterpolatorToSelection();
            }
        }

        // ⛔ Every selected object, not just `target`. A button repairing the
        // first one alone leaves the rest broken under a warning that has just
        // cleared, because the advisory goes as soon as no member is unpaired.
        private void AddInterpolatorToSelection()
        {
            foreach (var selected in targets)
            {
                if (!NeedsInterpolator(selected, out GameObject root)) continue;

                // The component the object is missing may already be on it,
                // switched off — in which case adding is the one repair that
                // leaves the object worse than it was found.
                var switchedOff = DisabledInterpolatorOn(root);
                if (switchedOff != null)
                {
                    Undo.RecordObject(switchedOff, UndoLabel);
                    switchedOff.enabled = true;
                    EditorUtility.SetDirty(switchedOff);
                    continue;
                }

                // Undo.AddComponent rather than AddComponent: this is an edit a
                // developer made in the Inspector, and one they must be able to
                // take back the way they take back every other Inspector edit.
                Undo.AddComponent<NetworkTransformInterpolator>(root);
                EditorUtility.SetDirty(root);
            }
        }

        /// <summary>
        /// Whether <paramref name="selected"/> is an object that sends motion
        /// with nothing on it to apply motion, and if so the object to repair.
        /// </summary>
        /// <remarks>
        /// One predicate for both the advisory and the button. Asked twice in
        /// two spellings they would answer differently the moment either moved,
        /// and the pair a developer sees is a warning and the control that
        /// clears it: a warning no button acts on, or a button that fires with
        /// no warning, is worse than neither.
        /// </remarks>
        // Qualified: `targets` is UnityEngine.Object[], and no compiler in this
        // repository reads this file — an ambiguity introduced by a later
        // `using System;` would be found by a developer's Unity console rather
        // than by anything here.
        private bool NeedsInterpolator(UnityEngine.Object selected, out GameObject root)
        {
            root = null;

            // A selection can hold something that is not one of ours, and a
            // missing script serialises as a null entry.
            var behaviour = selected as NetworkBehaviour;
            if (behaviour == null) return false;

            root = behaviour.gameObject;
            return NetworkPrefabsInventory.ClassifyMotion(RootComponentTypeNames(root))
                == PrefabMotionPairing.TransformWithoutInterpolator;
        }

        /// <summary>
        /// The switched-off receiving half on <paramref name="root"/>, or
        /// <see langword="null"/> where there is none.
        /// </summary>
        /// <remarks>
        /// ⛔ The reason the repair is not one action. A component that is there
        /// and off is unpaired in exactly the way an absent one is, and repaired
        /// in a completely different way: this type carries no
        /// <c>[DisallowMultipleComponent]</c>, so adding one leaves the object
        /// with two, the second doing the work while the first stays a puzzle
        /// for whoever opens the prefab next.
        /// </remarks>
        private static MonoBehaviour DisabledInterpolatorOn(GameObject root)
        {
            if (root == null) return null;

            var components = root.GetComponents<MonoBehaviour>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null || components[i].enabled) continue;

                if (string.Equals(
                    NetworkPrefabsInventory.CanonicalMotionTypeName(components[i].GetType()),
                    NetworkPrefabsInventory.NetworkTransformInterpolatorTypeName,
                    System.StringComparison.Ordinal))
                {
                    return components[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The full names of the behaviours on <paramref name="root"/> itself.
        /// </summary>
        /// <remarks>
        /// 🚨 <c>MonoBehaviour</c>, and the reason is the whole reason this block
        /// exists. <c>RTMPE.Sync.NetworkTransformInterpolator</c> derives from
        /// <c>UnityEngine.MonoBehaviour</c> — it PLAYS BACK a pose that arrived,
        /// it does not own one, so it is not a <c>NetworkBehaviour</c> and never
        /// has been. Read as <c>GetComponents&lt;NetworkBehaviour&gt;()</c> this
        /// list could not contain the interpolator under any configuration, so
        /// <c>ClassifyMotion</c> could never answer <c>Paired</c> and this drawer
        /// accuses every correctly-paired object it is opened on: a permanent
        /// HelpBox, and a permanent button that appends another copy of a
        /// component the object already carries.
        /// <para>
        /// ⛔ The TIGHTEST argument that can return both subjects, and not one
        /// step wider. <c>NetworkBehaviour</c> is itself a <c>MonoBehaviour</c>
        /// (<c>Runtime/Core/NetworkBehaviour.cs</c>), so this sees the sender as
        /// well as the receiver; <c>Component</c> or <c>Object</c> would also
        /// see both and would additionally drag in every Transform, Collider and
        /// Renderer on the root for a classifier that matches two full names.
        /// </para>
        /// <para>
        /// ⛔ <c>GetComponents</c> on the object the component is ON, and never
        /// <c>InChildren</c> or <c>InParent</c>:
        /// <c>NetworkBehaviour.CachedNetworkTransformInterpolator</c> is
        /// <c>GetComponent&lt;NetworkTransformInterpolator&gt;()</c> on the SAME
        /// GameObject, so an interpolator anywhere else is one the receive path
        /// never finds — and a check that accepted it would call this object fine
        /// while every remote replica of it freezes.
        /// </para>
        /// <para>
        /// ⚠️ Widening the argument does NOT widen what is accused.
        /// <c>ClassifyMotion</c> matches two full names and ignores everything
        /// else, so the extra components this now returns change no verdict —
        /// what changed is that the verdict <c>Paired</c> became reachable.
        /// </para>
        /// <para>
        /// A null entry is walked past: a missing script serialises as one, and
        /// asking it for its type throws out of OnInspectorGUI.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<string> RootComponentTypeNames(GameObject root)
        {
            if (root == null) return null;

            // The counting rule is shared with the Network Prefabs window: the
            // two surfaces answer the same question about the same object, and a
            // reader that disagreed with the window here would be a second
            // opinion nobody asked for.
            return RemoteMotionRootReader.MotionTypeNamesOn(
                root.GetComponents<MonoBehaviour>());
        }
    }
}
#endif
