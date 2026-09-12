// RTMPE SDK — Editor/ReleaseCredentialPathAdvisory.cs
//
// What a RELEASE player will have to authenticate with, decided while the build
// is still cancellable.
//
// 🔑 The sources a player can use are not equal, and only one of them is visible
// at build time. A provider registered with ApiKeySource.SetProvider is code in
// the project, so it can be looked for; --rtmpe-api-key-file, --rtmpe-api-key
// and RTMPE_API_KEY are launch-time choices this hook cannot see; and the
// development-build key is, by construction, not in a release build.
//
// ⛔ So the only honest statement is a conditional one: a release build with no
// provider in the project WILL have no credential unless somebody launches it
// with an argument or an environment variable — and a double-clicked
// application inherits neither. That sentence was learned the expensive way,
// from an integrator who built three times before anyone could say it.
//
// ⚠️ A WARNING and never a refusal. A launch-line deployment — a dedicated
// server, a kiosk, a CI harness — is a legitimate shape that this check cannot
// distinguish from a mistake, and a build hook that refuses correct work is one
// somebody removes.

namespace RTMPE.Editor
{
    internal static class ReleaseCredentialPathAdvisory
    {
        /// <summary>
        /// Whether this build is owed the warning.
        /// </summary>
        /// <remarks>
        /// ⛔ Release builds only. A development build has the injected key when
        /// the project opted in, and when it did not it is told so by the
        /// player's own runtime report — which names the switch. Warning there
        /// too would spend the reader's attention on the case that already has
        /// an answer.
        /// </remarks>
        internal static bool ShouldWarn(bool development, bool providerRegisteredInProject)
            => !development && !providerRegisteredInProject;

        /// <summary>The token a project's own scripts are searched for.</summary>
        /// <remarks>
        /// ⚠️ The registration call, not the type: naming `ApiKeySource` alone
        /// matches a file that merely mentions it — a comment, a using, the
        /// advisory text itself — and a check satisfied by prose is a check that
        /// never fires.
        /// </remarks>
        internal const string ProviderRegistration = "ApiKeySource.SetProvider";

        /// <summary>What the warning says.</summary>
        internal static string Message()
            => "[RTMPE] This release build has no API key source that a build can see. "
             + "Nothing in this project calls " + ProviderRegistration + ", and a release "
             + "build carries no key of its own — so the player will authenticate only if "
             + "it is launched with --rtmpe-api-key-file <path> or --rtmpe-api-key <key>, "
             + "or with RTMPE_API_KEY set. ⛔ A double-clicked application inherits none of "
             + "those. If this build is for a person to open, register a provider with "
             + ProviderRegistration + " before the connection starts; if it is launched by "
             + "a script or a service, this is expected and the build is fine.";
    }
}
