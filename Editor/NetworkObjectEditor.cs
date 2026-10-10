// RTMPE SDK — Editor/NetworkObjectEditor.cs
//
// Custom Inspector for NetworkBehaviour components.
// Shows network identity, ownership, authority status, and provides
// quick-action buttons for the Editor play-mode workflow.

using UnityEngine;
using UnityEditor;
using RTMPE.Core;

namespace RTMPE.Editor
{
    /// <summary>
    /// Inspector for every <see cref="NetworkBehaviour"/> subclass: the object's
    /// network identity, an ownership badge and, in Play mode, quick actions.
    /// </summary>
    /// <remarks>
    /// Unity uses the most specific <see cref="CustomEditor"/> for a type, so a
    /// custom inspector declared for your own <see cref="NetworkBehaviour"/>
    /// replaces this one. To keep the identity panel, derive your inspector from
    /// this class and call <c>base.OnInspectorGUI()</c>, as
    /// <c>NetworkTransformEditor</c> does.
    /// </remarks>
    [CustomEditor(typeof(NetworkBehaviour), true)]   // true = apply to subclasses
    [CanEditMultipleObjects]
    public class NetworkObjectEditor : UnityEditor.Editor
    {
        // ── Style cache ───────────────────────────────────────────────────────
        private GUIStyle _headerStyle;
        private GUIStyle _badgeStyle;

        // ── Foldout state ─────────────────────────────────────────────────────
        private bool _showAdvanced;

        // Protected and virtual because this type is a base: an inspector derived
        // from it may override it and call base.OnEnable(). It initialises nothing
        // now. The identity panel used to look its three values up with
        // serializedObject.FindProperty, and NetworkBehaviour does not serialise
        // them, so every lookup answered null and the panel drew a heading with
        // nothing under it (audit R11-04). The values are read from the
        // component's own properties instead, where they always are.
        protected virtual void OnEnable()
        {
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EnsureStyles();

            var nb = (NetworkBehaviour)target;

            // ── Header ────────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Network Identity", _headerStyle);
            EditorGUI.indentLevel++;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(
                    new GUIContent("Network Object ID", "The object's network ID, chosen by the client that spawned it and the same on every client. 0 until the object is spawned."),
                    nb.NetworkObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                EditorGUILayout.TextField(
                    new GUIContent("Owner Player ID", "The room player ID (PlayerInfo.PlayerId) of the player who owns this object. Empty until the object is spawned."),
                    nb.OwnerPlayerId ?? string.Empty);
                EditorGUILayout.Toggle(
                    new GUIContent("Is Spawned", "True while the object is live on the network: from OnNetworkSpawn until OnNetworkDespawn."),
                    nb.IsSpawned);
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Authority / ownership badge ───────────────────────────────────
            DrawOwnershipBadge(nb);
            EditorGUILayout.Space(4);

            // ── Advanced: draw component's own serialized fields ──────────────
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Component Fields", true);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                DrawDefaultInspector();
                EditorGUI.indentLevel--;
            }

            // ── Play-mode quick actions ───────────────────────────────────────
            if (Application.isPlaying)
                DrawPlayModeActions(nb);

            serializedObject.ApplyModifiedProperties();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void DrawOwnershipBadge(NetworkBehaviour nb)
        {
            bool isOwner = Application.isPlaying && nb.IsOwner;
            var color    = isOwner ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.8f, 0.5f, 0.2f);
            var label    = isOwner ? "✅  Local Owner" : "🔒  Remote / Not Spawned";

            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = color;
            EditorGUILayout.LabelField(label, _badgeStyle);
            GUI.backgroundColor = prevColor;
        }

        private void DrawPlayModeActions(NetworkBehaviour nb)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Play-Mode Actions", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Log State"))
                    Debug.Log(
                        $"[RTMPE] {nb.GetType().Name} — ObjectId: {nb.NetworkObjectId} " +
                        $"| Owner: {nb.OwnerPlayerId} | IsOwner: {nb.IsOwner} | IsSpawned: {nb.IsSpawned}",
                        nb);

                if (GUILayout.Button("Request Authority"))
                    Debug.LogWarning(
                        "[RTMPE] Ownership is changed by the server, at the owner's request. " +
                        "From the owning client's code, call " +
                        "NetworkManager.Instance.Spawner.Ownership.RequestOwnershipTransfer(objectId, newOwnerPlayerId).",
                        nb);
            }
        }

        private void EnsureStyles()
        {
            if (_headerStyle != null) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin   = new RectOffset(0, 0, 4, 4),
            };

            _badgeStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }
    }
}
