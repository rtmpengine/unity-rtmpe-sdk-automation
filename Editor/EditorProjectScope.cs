// RTMPE SDK — Editor/EditorProjectScope.cs
//
// What makes a piece of Editor state belong to THIS project.
//
// `EditorPrefs` is per-user and per-Unity-install — not per-project — and the
// OS credential vaults are keyed by whatever service/account name they are
// given. Neither carries a project identity of its own, so anything the SDK
// stores in them under a fixed name is shared by every RTMPE project on the
// machine, and the last one to write wins.
//
// That is not a tidiness problem. The two things the SDK keeps there are the
// API key — which authenticates to the gateway AS A TENANT — and the wizard's
// pinned Ed25519 key and X25519 seal key, which get written onto a committed
// `NetworkSettings` asset. A developer with two RTMPE projects open in turn
// would have the second silently adopt the first's credentials and connect as
// the wrong tenant, with nothing anywhere reporting it.
//
// `PlayerSettings.productGUID` is Unity's own stable per-project identifier: it
// survives a rename, a move and a version-control round trip, and it is not
// something the developer edits by hand the way `productName` is.

#if UNITY_EDITOR
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace RTMPE.Editor
{
    /// <summary>
    /// Qualifies a machine-wide Editor storage name with the identity of the
    /// project it belongs to.
    /// </summary>
    internal static class EditorProjectScope
    {
        /// <summary>The suffix appended when no project identity is available.</summary>
        /// <remarks>
        /// ⛔ A distinct marker rather than an empty string, so the unscoped
        /// state is visible in the stored name instead of looking like a name
        /// that was never scoped at all.
        /// </remarks>
        internal const string UnknownProjectSuffix = "unknown-project";

        private static bool _warned;

        /// <summary>
        /// This project's stable identifier, or
        /// <see cref="UnknownProjectSuffix"/> when Unity has none to give.
        /// </summary>
        internal static string Id
        {
            get
            {
                string id = PlayerSettings.productGUID.ToString("N");

                // A GUID of all zeroes is Unity saying it has no identity for
                // this project — every project would then share one scope,
                // which is the defect this type exists to remove. Said once,
                // because the alternative is a silent return to it.
                if (string.IsNullOrEmpty(id) || id.Trim('0').Length == 0)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Debug.LogWarning(
                            "[RTMPE] This project has no PlayerSettings.productGUID, so Editor " +
                            "credentials cannot be scoped to it and are shared with every other " +
                            "RTMPE project on this machine. Saving the project once gives Unity " +
                            "a chance to assign one.");
                    }
                    return UnknownProjectSuffix;
                }

                return id;
            }
        }

        /// <summary>
        /// <paramref name="name"/> qualified with <see cref="Id"/>.
        /// </summary>
        internal static string Scoped(string name) => name + "." + Id;

        /// <summary>Test seam: reopen the one-shot warning.</summary>
        internal static void ResetWarningForTest() => _warned = false;
    }
}
#endif // UNITY_EDITOR
