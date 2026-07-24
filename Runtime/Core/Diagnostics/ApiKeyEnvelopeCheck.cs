// RTMPE SDK — Runtime/Core/Diagnostics/ApiKeyEnvelopeCheck.cs
//
// Single source of truth for "does this configuration carry an API-key
// envelope?" — true when either the sealed-box server public key or the shared
// PSK is present.  Outside the Unity Editor the runtime refuses to send the API
// key unencrypted, so a build with neither credential cannot complete a
// handshake.  The runtime unbound-settings warning and the build-time validator
// both consult this predicate, so the definition of "configured" cannot drift
// between them.  Kept UnityEngine-free so it is exercisable from the headless
// dotnet xunit runner.

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Pure predicate for the presence of an API-key envelope credential.
    /// </summary>
    internal static class ApiKeyEnvelopeCheck
    {
        /// <summary>
        /// Returns <see langword="true"/> when at least one API-key envelope
        /// credential is present.  A hex value is treated as absent when it is
        /// <see langword="null"/>, empty, or only whitespace, so a field left
        /// blank (or wiped to spaces) never counts as configured.
        /// </summary>
        /// <param name="sealServerPublicKeyHex">
        /// The gateway's static X25519 public key for the sealed-box path
        /// (<c>NetworkSettings.apiKeySealServerPublicKeyHex</c>).
        /// </param>
        /// <param name="apiKeyPskHex">
        /// The shared PSK for the legacy encrypted-envelope path
        /// (<c>NetworkSettings.apiKeyPskHex</c>).
        /// </param>
        internal static bool IsConfigured(string sealServerPublicKeyHex, string apiKeyPskHex)
            => !string.IsNullOrWhiteSpace(sealServerPublicKeyHex)
            || !string.IsNullOrWhiteSpace(apiKeyPskHex);

        /// <summary>
        /// The build-time verdict for a set of discovered <c>NetworkSettings</c>
        /// assets, from the counts the validator gathers. An asset that fails to
        /// load is neither configured nor blank — it is unreadable — so a build
        /// where every asset failed to load (<see cref="EnvelopeBuildVerdict.NoneLoaded"/>)
        /// is a distinct condition from one where assets loaded but none carries an
        /// envelope (<see cref="EnvelopeBuildVerdict.NoneConfigured"/>), and the two
        /// must not share a diagnostic.
        /// </summary>
        /// <param name="assetCount">Every discovered asset, readable or not.</param>
        /// <param name="configuredCount">Loaded assets that carry an envelope.</param>
        /// <param name="emptyCount">Loaded assets with a blank envelope.</param>
        internal static EnvelopeBuildVerdict ClassifyBuildState(
            int assetCount, int configuredCount, int emptyCount)
        {
            if (assetCount <= 0)
            {
                return EnvelopeBuildVerdict.NoAssets;
            }

            if (configuredCount + emptyCount == 0)
            {
                return EnvelopeBuildVerdict.NoneLoaded;
            }

            if (configuredCount == 0)
            {
                return EnvelopeBuildVerdict.NoneConfigured;
            }

            return emptyCount > 0
                ? EnvelopeBuildVerdict.SomeBlank
                : EnvelopeBuildVerdict.AllConfigured;
        }
    }

    /// <summary>
    /// The outcome of a build-time <c>NetworkSettings</c> envelope scan, mapped by
    /// the validator to a warning, a hard stop, or silence.
    /// </summary>
    internal enum EnvelopeBuildVerdict
    {
        /// <summary>No <c>NetworkSettings</c> asset was discovered.</summary>
        NoAssets,

        /// <summary>Assets were discovered but every one failed to load.</summary>
        NoneLoaded,

        /// <summary>Assets loaded, but not one carries an envelope.</summary>
        NoneConfigured,

        /// <summary>At least one asset carries an envelope and at least one is blank.</summary>
        SomeBlank,

        /// <summary>Every loaded asset carries an envelope.</summary>
        AllConfigured,
    }
}
