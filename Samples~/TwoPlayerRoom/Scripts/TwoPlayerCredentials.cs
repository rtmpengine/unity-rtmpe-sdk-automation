using RTMPE.Core;
using UnityEngine;

namespace RTMPE.Samples.TwoPlayerRoom
{
    /// <summary>
    /// Registers this project's API key with the SDK, from outside the scene
    /// asset, and says so before anything tries to connect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Setup Wizard stores a key in your platform credential vault and the
    /// SDK reads that vault back — <b>in the Editor</b>. The registration that
    /// does it lives in the Editor assembly, which no player build compiles, so
    /// a build that has only ever been given a key through the wizard has no key
    /// at all. Play mode working says nothing about the build, and this is the
    /// component that closes the gap: it hands
    /// <see cref="ApiKeySource.SetProvider"/> a source a shipped game has.
    /// </para>
    /// <para>
    /// ⛔ It fetches nothing. Where the key comes from — a file you ship beside
    /// the player, your own account backend, a launcher — is a decision about
    /// your infrastructure, and a fetcher shipped here would be an opinion about
    /// somebody else's backend and a credential-handling surface this package
    /// would then own. <see cref="Supply"/> is the seam; the fetch is yours.
    /// </para>
    /// </remarks>
    public sealed class TwoPlayerCredentials : MonoBehaviour
    {
        // ⛔ This component declares NO serialized field, and must not acquire
        // one for the key. Unity writes a serialized string into the scene
        // asset — which is committed, and is present in every build made from
        // it — and it serializes a PUBLIC field just as readily as one carrying
        // [SerializeField], so "make it private" is not the fix and "no
        // attribute" is not a defence.
        //
        // `static` is what actually keeps this out of the asset: Unity never
        // serializes a static field. It also outlives this object, which is what
        // lets Supply be called before the scene carrying this component loads.
        private static string s_key = string.Empty;

        /// <summary>
        /// Whether a credential source answered, or <see langword="null"/> if
        /// nothing has asked yet. The readout puts this on screen.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔑 The FACT and nothing else — not the key, not its length, not a
        /// prefix of it. A length is a fingerprint of a secret, so a bool is the
        /// widest this may ever be.
        /// </para>
        /// <para>
        /// ⛔ Three states, not two. <c>null</c> means no ask has happened,
        /// which is what a scene missing this component looks like — and that is
        /// a different thing from "asked, and there is no key". Collapsing them
        /// would make the readout say "no key" to somebody whose environment
        /// variable is set perfectly well and who simply never added this
        /// component, which is a wrong answer rather than a missing one.
        /// </para>
        /// </remarks>
        internal static bool? ApiKeyIsAvailable { get; private set; }

        private void Awake()
        {
            // First, and unconditionally: RtmpeConnectionBootstrap connects from
            // Start, and every Awake in a scene runs before any Start in it, so
            // registering here is early enough with no execution-order setting.
            // A named method rather than a lambda, so the line a reader has to
            // edit is one they can navigate to.
            ApiKeySource.SetProvider(ProvideKey);

            // ⛔ Deliberately no SetProvider(null) in OnDestroy. Clearing the
            // registration does not restore "no provider was ever set": it
            // changes which source answers next — in the Editor the wizard's
            // vault, in a player the command line and the environment — so a
            // component being torn down at the end of a scene would silently
            // move the credential to a different source mid-session.

            // Asked once, so the report below is about the state the connection
            // will actually meet.
            //
            // ⛔ IsAvailable, never TryResolve. The question here is whether a
            // key exists, and the answer to that is a bool; asking for the key
            // and ignoring it would put a named reference to the credential in
            // scope for the rest of this method, where a later edit can log it,
            // concatenate it into a message, or measure it. A length is a
            // disclosure too — it narrows what a guess has to cover — so the
            // only safe amount of the secret to hold here is none of it.
            if (ApiKeySource.IsAvailable())
            {
                ApiKeyIsAvailable = true;
                Debug.Log("[TwoPlayerCredentials] An API key is available; "
                          + "this client can enter the room.");
                return;
            }

            ApiKeyIsAvailable = false;

            // Asked at runtime rather than with #if, and the reason is the
            // opposite of what it looks like: a sample IS compiled here, by
            // SampleScriptsCompileTests — in ONE configuration. A branch the
            // preprocessor removes would be a branch nothing ever built.
            //
            // The two remedies are different because the two readers are: the
            // wizard's vault is written by the Editor and travels into no build,
            // so naming that window to a player points them at a tool that
            // cannot help them.
            //
            // ⛔ `--rtmpe-api-key <key>` is deliberately not named here. It puts
            // the key in argv, which `ps` and /proc hand to every account on the
            // machine; the file form does the same job without that.
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
        /// Returning <c>null</c> or an empty string is how a provider says "I
        /// have nothing", and resolution then falls through to the sources
        /// below it. Throwing is a different answer — a configured source that
        /// FAILED — and it ends resolution rather than connecting with a
        /// credential you did not choose.
        /// </remarks>
        private static string ProvideKey()
        {
            return s_key;
        }

        /// <summary>
        /// Hands this sample the key your own code obtained. Call it from
        /// anywhere, at any time before a connection is attempted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ The SDK asks a provider for a <c>string</c> and does not await
        /// one: <c>Func&lt;string&gt;</c> returns, and blocking it would stall
        /// the frame that is trying to connect. So a key that comes from a
        /// backend has to be IN HAND first. The order is: clear
        /// <b>Connect On Start</b> on <c>RtmpeConnectionBootstrap</c>, run your
        /// fetch, call <c>Supply</c> with what it produced, and only then call
        /// the bootstrap's own <c>Connect()</c>.
        /// </para>
        /// <para>
        /// ⛔ What a backend releases this way is still your PROJECT's API key,
        /// withheld from anyone it has not authenticated. It is not a per-player
        /// credential — the wire does not carry one — and what this removes is
        /// the copy in the scene asset, the copy in your repository's history
        /// and the copy in the shipped player.
        /// </para>
        /// </remarks>
        /// <param name="key">
        /// The key. <c>null</c> is stored as empty, which is the same answer as
        /// never having called this at all.
        /// </param>
        public static void Supply(string key)
        {
            s_key = key ?? string.Empty;

            // The readout would otherwise still be showing the answer Awake got,
            // which for the deferred flow this method exists for is always "no
            // key" — the fetch has not finished when Awake runs. Emptiness is
            // the provider contract's own "I have nothing", not a measurement of
            // the secret: nothing derived from `key` is logged, stored or
            // compared beyond this.
            if (!string.IsNullOrEmpty(s_key)) ApiKeyIsAvailable = true;
        }
    }
}
