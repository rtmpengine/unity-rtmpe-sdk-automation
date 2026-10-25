// RTMPE SDK — Editor/WizardApiKeyEdit.cs
//
// The one question the Setup Wizard has to answer before it touches the
// credential vault: is the box on screen an instruction, or is it just empty?
// Kept apart from the EditorWindow for the reason WizardConfigValidator is —
// the wizard is GUI-bound and compiles only inside Unity, and a rule nothing
// executes is a rule nothing holds.

namespace RTMPE.Editor
{
    /// <summary>
    /// Decides whether the Setup Wizard's API-key field is saying anything to
    /// the credential vault. Has no UnityEngine/UnityEditor dependency, so the
    /// contract is verified by the off-Editor test shard as well as by Unity.
    /// </summary>
    internal static class WizardApiKeyEdit
    {
        /// <summary>
        /// True when the field carries an instruction: it differs from what the
        /// wizard read out of the store when it opened.
        /// </summary>
        /// <remarks>
        /// 🔴 The wizard used to save the field unconditionally, and
        /// <c>ApiKeyStore.Save("")</c> deletes. An empty box therefore meant two
        /// different things at once — "the developer cleared the key" and "the
        /// store did not answer" — and the second is ordinary: a locked keyring,
        /// a DPAPI blob that will not decrypt, <c>secret-tool</c> missing or slow,
        /// no D-Bus session. In every one of those the wizard opened with a blank
        /// field over a vault that still held a good key, and the next
        /// <b>Next →</b> destroyed it. The developer asked for nothing.
        ///
        /// ⛔ The discrimination is deliberately NOT "why was the read empty".
        /// That answer is not available: the platform readers report absence and
        /// unavailability with the same <c>false</c>, and a fix that rested on
        /// telling them apart would be right on the one arm that throws and wrong
        /// on the four that do not. What a save may act on is what the developer
        /// changed, which is knowable exactly and is the right rule even where
        /// the read succeeded — a Next → that rewrites an unchanged key is a
        /// keychain write nobody asked for, and on a write failure
        /// <c>ApiKeyStore.Save</c> falls back to obfuscated EditorPrefs, quietly
        /// moving a vaulted key to the weaker store.
        ///
        /// ⛔ One behaviour goes with the unconditional save, deliberately. Every
        /// Next → used to re-enter the key, and a successful vault write drops
        /// the obfuscated EditorPrefs fallback — so a key living in the weaker
        /// store was promoted whenever the vault became available. That is not
        /// safe to keep: <c>Load</c> reads the vault FIRST and reaches the
        /// fallback only when the vault did not answer, so promoting an
        /// unchanged field is exactly the case where a stale fallback key would
        /// be written over a newer vaulted one the reader could not see. The
        /// promotion was incidental to the defect and leaves with it; retyping
        /// the key still performs it, and that is a developer saying which key
        /// is current rather than the wizard guessing.
        ///
        /// 🔑 A deliberate clear still reaches the vault: the field read a key,
        /// so blanking it differs, and the delete is exactly what was asked for.
        /// The one thing that cannot be expressed is clearing a key the wizard
        /// never managed to read — and there is nothing on screen to clear.
        /// </remarks>
        internal static bool ShouldWrite(string loaded, string typed)
            => !string.Equals(loaded ?? "", typed ?? "", System.StringComparison.Ordinal);

        /// <summary>
        /// True when the wizard has no key for this project and the field has not
        /// been touched — the state where a blank box means "nothing to say"
        /// rather than "delete it", which is the opposite of what a blank box
        /// used to mean and so has to be said out loud.
        /// </summary>
        internal static bool SaysNothingWasStored(string loaded, string typed)
            => string.IsNullOrEmpty(loaded) && !ShouldWrite(loaded, typed);
    }
}
