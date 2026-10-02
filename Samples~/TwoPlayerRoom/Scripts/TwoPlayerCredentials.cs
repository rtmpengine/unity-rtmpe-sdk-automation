using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// Registers this project's API key with the SDK, from outside the scene
    /// asset, and says so before anything tries to connect.
    /// </summary>
    /// <remarks>
    /// <para>The Setup Wizard stores a key in your OS credential store, but only
    /// the Editor reads it back, so a player build has no key from that source.
    /// This component gives a player one by registering a provider with
    /// <see cref="ApiKeySource.SetProvider"/>.</para>
    /// <para>It fetches nothing. Where the key comes from (a file shipped beside
    /// the player, your own account backend, a launcher) is up to you: pass it
    /// to <see cref="Supply"/>.</para>
    /// </remarks>
    public sealed class TwoPlayerCredentials : MonoBehaviour
    {
        // Never keep the key in a serialized field: Unity writes serialized
        // fields, public or [SerializeField], into the scene asset, which is
        // committed and included in every build. A static field is never
        // serialized, and it outlives this object, so Supply can be called
        // before the scene with this component loads.
        private static string s_key = string.Empty;

        /// <summary>
        /// Whether an API key is available, or <see langword="null"/> before
        /// anything has checked. The on-screen readout shows it.
        /// </summary>
        /// <remarks>
        /// Only this fact is exposed, never the key or its length.
        /// <see langword="null"/> is kept apart from <see langword="false"/> so
        /// that a scene without this component is not reported as having no key.
        /// </remarks>
        internal static bool? ApiKeyIsAvailable { get; private set; }

        private void Awake()
        {
            // Registered in Awake: every Awake in a scene runs before any Start,
            // and RtmpeConnectionBootstrap connects from Start.
            ApiKeySource.SetProvider(ProvideKey);

            // No SetProvider(null) in OnDestroy: clearing the registration would
            // switch to another source in the middle of a session.

            // IsAvailable rather than TryResolve: this only needs to know whether
            // a key exists, and not holding the key here means a later edit
            // cannot log it.
            if (ApiKeySource.IsAvailable())
            {
                ApiKeyIsAvailable = true;
                Debug.Log("[TwoPlayerCredentials] An API key is available; "
                          + "this client can enter the room.");
                return;
            }

            ApiKeyIsAvailable = false;

            // Application.isEditor is checked at runtime rather than with #if, so
            // both messages are compiled. A player build cannot read the Setup
            // Wizard's vault, so its message names the sources a player can use.
            //
            // --rtmpe-api-key <key> is not suggested: it puts the key on the
            // command line, where other accounts on the machine can read it. The
            // file option does the same job without that.
            Debug.LogWarning("[TwoPlayerCredentials] No API key — nothing will connect. " +
                             (Application.isEditor
                                 ? "Store one via Window > RTMPE > Setup Wizard, call " +
                                   "TwoPlayerCredentials.Supply from your own code before this " +
                                   "scene loads, launch with "
                                 : "The Setup Wizard's vault is written by the Editor and no " +
                                   "build carries it, so this player needs one of these instead: " +
                                   "call TwoPlayerCredentials.Supply from your own code — which " +
                                   "is the ApiKeySource.SetProvider registration this component " +
                                   "already made — launch with ") +
                             ApiKeySource.CommandLineFileOption + " <path>, or set " +
                             ApiKeySource.EnvironmentVariableName + ". " +
                             (ApiKeySource.LastError == null
                                 ? ""
                                 : "A configured source failed: " +
                                   ApiKeySource.LastError.Message));
        }

        /// <summary>
        /// The provider itself: the whole of what this component tells the SDK.
        /// </summary>
        /// <remarks>
        /// Returning <c>null</c> or an empty string means "no key here", and
        /// resolution moves on to the next source. Throwing reports a configured
        /// source that failed, and ends resolution instead.
        /// </remarks>
        private static string ProvideKey()
        {
            return s_key;
        }

        /// <summary>
        /// Hands the SDK the key your own code obtained. Call it at any time
        /// before a connection is attempted.
        /// </summary>
        /// <remarks>
        /// <para>The SDK asks the provider for the key synchronously, so a key
        /// from a backend must be in hand before connecting: clear
        /// <b>Connect On Start</b> on <c>RtmpeConnectionBootstrap</c>, run your
        /// fetch, call <c>Supply</c> with the result, then call the bootstrap's
        /// <c>Connect()</c>.</para>
        /// <para>A key your backend releases this way is still your project's API
        /// key, not a per-player credential; what it avoids is a copy in the scene
        /// asset, in your repository and in the shipped player.</para>
        /// </remarks>
        /// <param name="key">
        /// The key. <see langword="null"/> is stored as empty, the same as never
        /// calling this.
        /// </param>
        public static void Supply(string key)
        {
            s_key = key ?? string.Empty;

            // Update the readout, which otherwise still shows the answer Awake
            // got before the fetch finished. Only emptiness is checked; nothing
            // derived from the key is logged or stored.
            if (!string.IsNullOrEmpty(s_key)) ApiKeyIsAvailable = true;
        }
    }
}
