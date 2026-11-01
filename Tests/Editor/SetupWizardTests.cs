// RTMPE SDK — Tests/Editor/SetupWizardTests.cs
//
// Verifies the Setup Wizard's Editor-side contracts:
//  1. The auto-open opt-out flips its per-project EditorPrefs flag.
//  2. The macOS Keychain write never places the API key in a process argument.
//  3. The wizard's connection settings reach the NetworkSettings asset.
//
// The API key itself lives in the vault the wizard writes (ApiKeyStore), whose
// contract is held by its own suites.

using System;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEditor;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Editor;

namespace RTMPE.Tests.Editor
{
    [TestFixture]
    [Category("SetupWizard")]
    public class SetupWizardTests
    {
        // ── Auto-open opt-out ────────────────────────────────────────────

        [Test]
        [Description("ToggleAutoOpen flips the EditorPrefs flag back and forth deterministically.")]
        public void ToggleAutoOpen_TogglesEditorPrefFlag()
        {
            // The key AS STORED — scoped to this project. Reading the bare
            // constant here would watch a name ToggleAutoOpen no longer writes,
            // and every assertion below would pass on an absence.
            string key = SetupWizard.AutoOpenDisabledPref;

            bool hadPrev = EditorPrefs.HasKey(key);
            bool prev    = hadPrev && EditorPrefs.GetBool(key, false);

            try
            {
                // Start from a known-disabled state: flag absent / false.
                EditorPrefs.DeleteKey(key);
                Assert.IsFalse(EditorPrefs.GetBool(key, false),
                    "Pre-condition: auto-open is enabled by default.");

                SetupWizard.ToggleAutoOpen();
                Assert.IsTrue(EditorPrefs.GetBool(key, false),
                    "After first toggle: auto-open must be disabled.");

                SetupWizard.ToggleAutoOpen();
                Assert.IsFalse(EditorPrefs.GetBool(key, false),
                    "After second toggle: auto-open must be re-enabled.");
            }
            finally
            {
                EditorPrefs.DeleteKey(key);
                if (hadPrev) EditorPrefs.SetBool(key, prev);
            }
        }

        // ── M-044: macOS Keychain write must not place the API key in argv ──

        [Test]
        [Description(
            "Static check: the macOS keychain write path constructs the " +
            "`security` ProcessStartInfo with NO secret material in its " +
            "Arguments string.  The secret is fed through stdin via the " +
            "`security -i` interactive-command channel — which is not " +
            "visible to other users via `ps -ef`.")]
        public void MacKeychainWrite_DoesNotPlaceApiKeyInArgv()
        {
            // The Editor scripts are platform-gated (#if UNITY_EDITOR_OSX),
            // so the only universally-runnable assertion is a textual
            // contract: ApiKeyStore.cs must NOT contain the previous
            // pattern that interpolated the secret into `-w "..."` argv.
            string sourcePath = System.IO.Path.Combine(
                UnityEngine.Application.dataPath, "..",
                "Packages", "com.rtmpe.sdk", "Editor", "ApiKeyStore.cs");

            // Resolve via package layout if not under Assets/.
            if (!System.IO.File.Exists(sourcePath))
            {
                // Search the package by GUID-less convention.
                var candidates = System.IO.Directory.GetFiles(
                    UnityEngine.Application.dataPath + "/..",
                    "ApiKeyStore.cs",
                    System.IO.SearchOption.AllDirectories);
                if (candidates.Length > 0) sourcePath = candidates[0];
            }

            Assume.That(System.IO.File.Exists(sourcePath),
                "ApiKeyStore.cs must be locatable for the source-pattern check.");

            string src = System.IO.File.ReadAllText(sourcePath);

            // The OS X branch must use `security -i` (stdin command channel)
            // and must NOT pass the API key as an argv `-w "..."` value to
            // `add-generic-password`.
            // ⚠️ The WRITE block opens with `#if UNITY_EDITOR_OSX`; the first
            // occurrence of the bare symbol is an `#elif` inside the small
            // per-platform dispatcher above it, whose six lines carry neither
            // `security -i` nor a stdin write — so a slice from there failed on
            // every platform, telling the integrator the OSX path was wrong.
            int osxStart = src.IndexOf("#if UNITY_EDITOR_OSX", StringComparison.Ordinal);
            int osxEnd   = src.IndexOf("#endif", osxStart, StringComparison.Ordinal);
            Assert.Greater(osxEnd, osxStart, "Failed to locate the OSX block.");
            string osxBlock = src.Substring(osxStart, osxEnd - osxStart);

            StringAssert.Contains("\"security\", \"-i\"", osxBlock,
                "OSX write path must spawn `security -i` (stdin command mode).");
            StringAssert.Contains("StandardInput.WriteLine", osxBlock,
                "OSX write path must write the add-generic-password command to stdin.");
            // The command that carries the key must never be a process ARGUMENT:
            // argv is visible to every process on the machine. The read path's
            // `find-generic-password … -w` is argv and carries no key, so the
            // assertion is about add-generic-password specifically.
            Assert.IsFalse(
                System.Text.RegularExpressions.Regex.IsMatch(osxBlock, @"ProcessStartInfo\([^;]*add-generic-password"),
                "OSX write path must not pass add-generic-password (and the API key) as a process argument.");
        }

        // ── S3-4: wizard connection config propagates to the asset ───────────

        [Test]
        [Description(
            "S3-4: ApplyConnectionConfig copies the wizard's gateway host/port/tick " +
            "onto the NetworkSettings asset, so a developer who points the wizard at " +
            "a non-default gateway no longer silently falls back to 127.0.0.1:7777.")]
        public void ApplyConnectionConfig_CopiesHostPortTickToAsset()
        {
            var settings = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                SetupWizard.ApplyConnectionConfig(settings, "10.0.0.5", 9000, 45, "", "", "", "");

                Assert.AreEqual("10.0.0.5", settings.serverHost, "serverHost must be copied from the wizard");
                Assert.AreEqual(9000, settings.serverPort, "serverPort must be copied from the wizard");
                Assert.AreEqual(45, settings.tickRate, "tickRate must be copied from the wizard");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        [Description(
            "ApplyConnectionConfig copies the dashboard pinned (Ed25519) and seal " +
            "(X25519) public keys onto the asset, trimming incidental whitespace, so " +
            "the wizard can fully configure a Strict-pinned sealed-box connection.")]
        public void ApplyConnectionConfig_CopiesPinnedAndSealKeysTrimmed()
        {
            var settings = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                // Synthetic fixtures — SHA-256 of a label, not keys any gateway
                // holds. What is under test is that the wizard copies and trims
                // the two fields, so the values are opaque to it; a real
                // deployment's keys would add nothing and this file is published.
                const string pin  = "f5863b2f772c4d4dbbbd87e8801e3986e786e4bd91293e4cff272fb066140e1b";
                const string seal = "a05a74c730ffb7e01816d0f73c36359023347c424ad5e79f6449dc39d3a84d76";

                SetupWizard.ApplyConnectionConfig(settings, "h", 1, 30, "  " + pin + "\t", " " + seal + " ", "", "");

                Assert.AreEqual(pin,  settings.pinnedServerPublicKeyHex,
                    "pinned key must be copied and trimmed");
                Assert.AreEqual(seal, settings.apiKeySealServerPublicKeyHex,
                    "seal key must be copied and trimmed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        [Description(
            "Blank wizard key fields must NOT clobber keys the developer pasted " +
            "directly onto the asset — only host/port/tick overwrite unconditionally.")]
        public void ApplyConnectionConfig_BlankKeys_DoNotClobberExistingAssetValues()
        {
            var settings = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                settings.pinnedServerPublicKeyHex     = "existing-pin";
                settings.apiKeySealServerPublicKeyHex = "existing-seal";

                // Wizard run with the key fields left empty / whitespace-only.
                SetupWizard.ApplyConnectionConfig(settings, "h", 1, 30, "", "   ", "", "");

                Assert.AreEqual("existing-pin",  settings.pinnedServerPublicKeyHex,
                    "blank pinned field must leave the asset's pin untouched");
                Assert.AreEqual("existing-seal", settings.apiKeySealServerPublicKeyHex,
                    "blank seal field must leave the asset's seal key untouched");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        [Description(
            "ApplyConnectionConfig copies the dashboard JWT issuer/audience onto the " +
            "asset, trimming incidental whitespace but preserving case (claims are " +
            "compared byte-for-byte, unlike the case-insensitive hex keys).")]
        public void ApplyConnectionConfig_CopiesJwtClaimsTrimmedCasePreserved()
        {
            var settings = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                SetupWizard.ApplyConnectionConfig(
                    settings, "h", 1, 30, "", "", "  RTMPE-Gateway\t", " rtmpe-session ");

                Assert.AreEqual("RTMPE-Gateway", settings.expectedJwtIssuer,
                    "issuer must be copied, trimmed, and case-preserved");
                Assert.AreEqual("rtmpe-session", settings.expectedJwtAudience,
                    "audience must be copied and trimmed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        [Description(
            "Blank wizard JWT fields must NOT clobber issuer/audience the developer " +
            "set directly on the asset, mirroring the pinned/seal-key contract.")]
        public void ApplyConnectionConfig_BlankJwtClaims_DoNotClobberExistingAssetValues()
        {
            var settings = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                settings.expectedJwtIssuer   = "existing-iss";
                settings.expectedJwtAudience = "existing-aud";

                SetupWizard.ApplyConnectionConfig(settings, "h", 1, 30, "", "", "", "   ");

                Assert.AreEqual("existing-iss", settings.expectedJwtIssuer,
                    "blank issuer field must leave the asset's issuer untouched");
                Assert.AreEqual("existing-aud", settings.expectedJwtAudience,
                    "blank audience field must leave the asset's audience untouched");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        [Description("ApplyConnectionConfig is null-safe (no throw on a null asset).")]
        public void ApplyConnectionConfig_NullAsset_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => SetupWizard.ApplyConnectionConfig(null, "h", 1, 2, "p", "s", "i", "a"));
        }
    }
}
