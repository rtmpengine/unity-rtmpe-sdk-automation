// RTMPE SDK — Editor/WizardConfigValidator.cs
//
// Pure, Unity-free validation of the Setup Wizard's connection inputs. Kept
// apart from the EditorWindow so the rules can be exercised by the off-Editor
// test shard (the wizard itself is GUI-bound and compiles only inside Unity).

namespace RTMPE.Editor
{
    /// <summary>
    /// Stateless validation of the values the Setup Wizard writes into a
    /// NetworkSettings asset. Has no UnityEngine/UnityEditor dependency, so the
    /// exact contract is verified both here (Unity) and by the dotnet test shard.
    /// </summary>
    internal static class WizardConfigValidator
    {
        /// <summary>
        /// Validates the wizard's connection inputs. The wizard writes a
        /// Strict-pinning asset (the SDK default), which fail-closed refuses every
        /// connection until the server's public key is pinned — so the pinned key
        /// is required and the port must be IANA-valid (1..65535; the earlier
        /// &gt;1024/&lt;65535 gate wrongly rejected 1024 and 65535). The API-key seal
        /// key is optional because only the sealed-box envelope path uses it (the
        /// PSK path supplies the secret on the asset instead), but a non-empty value
        /// must still be well-formed. Returns (false, reason) naming the specific
        /// missing piece, or (true, null) when the configuration is connectable.
        /// </summary>
        internal static (bool ok, string message) Validate(
            string apiKey, int port, string pinnedKey, string sealKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return (false, "Provide your project API key (copied from the dashboard).");
            if (port < 1 || port > 65535)
                return (false, "Provide a gateway port in the range 1–65535.");
            if (!IsHex64(pinnedKey))
                return (false, "Paste the 64-hex pinned server public key (Connection settings in the " +
                               "dashboard). Strict pinning is the default and refuses every connection without it.");
            if (!string.IsNullOrWhiteSpace(sealKey) && !IsHex64(sealKey))
                return (false, "The API-key seal public key must be 64 hex characters (or left blank for the PSK path).");
            return (true, null);
        }

        /// <summary>True when <paramref name="s"/> is exactly 64 hexadecimal characters — a 32-byte key.</summary>
        internal static bool IsHex64(string s)
        {
            if (s == null) return false;
            s = s.Trim();
            if (s.Length != 64) return false;
            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
