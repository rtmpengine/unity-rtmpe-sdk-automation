// RTMPE SDK — Tests/Editor/SetupWizardTests.cs
//
// Verifies the obfuscated API key store contract:
//  1. Round-trip: Save(x) then Load() returns x.
//  2. On-disk form is NOT plaintext (the key never appears in the
//     EditorPrefs string).
//  3. A machine-wide record an older SDK left under "RTMPE_ApiKey" is
//     REPORTED once and never adopted, and nothing here deletes it.
//  4. Clear() erases THIS project's slot and leaves the machine-wide one.
//  5. AES-GCM tag verification rejects tampered ciphertext.

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
        // ⛔ Scoped, because that is what production writes. Held unscoped,
        // SetUp snapshotted a slot nothing uses while `Save()` overwrote the
        // developer's real per-project key — and TearDown restored the wrong
        // one, so a green run destroyed a stored credential.
        private static string EncPrefKey => EditorProjectScope.Scoped("RTMPE_ApiKey_Enc_v1");

        // Unscoped by definition — what an older SDK wrote, for the machine.
        private const string LegacyPrefKey = "RTMPE_ApiKey";

        private string _savedEnc;
        private string _savedLegacy;
        private bool   _hadEnc;
        private bool   _hadLegacy;

        [SetUp]
        public void SetUp()
        {
            // Snapshot any pre-existing user values so we can restore them.
            _hadEnc      = EditorPrefs.HasKey(EncPrefKey);
            _hadLegacy   = EditorPrefs.HasKey(LegacyPrefKey);
            _savedEnc    = _hadEnc    ? EditorPrefs.GetString(EncPrefKey)    : null;
            _savedLegacy = _hadLegacy ? EditorPrefs.GetString(LegacyPrefKey) : null;

            EditorPrefs.DeleteKey(EncPrefKey);
            EditorPrefs.DeleteKey(LegacyPrefKey);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey(EncPrefKey);
            EditorPrefs.DeleteKey(LegacyPrefKey);
            if (_hadEnc)    EditorPrefs.SetString(EncPrefKey,    _savedEnc);
            if (_hadLegacy) EditorPrefs.SetString(LegacyPrefKey, _savedLegacy);
        }

        // ── Round-trip ───────────────────────────────────────────────────

        [Test]
        [Description("Save then Load returns the original API key.")]
        public void RoundTrip_PreservesApiKey()
        {
            // The fixtures below carry an rtmpe_ prefix, not Stripe's rk_live_.
            // Nothing here has ever been a Stripe key, and borrowing another
            // vendor's live-key prefix made every secret scanner report these
            // files — which is how they came to sit behind a whole-file
            // exemption that also hid everything else in them.
            const string apiKey = "rtmpe_live_xxxxxxxxxxxxxxxxxxxxxxxxxxx";
            EditorApiKeyStore.Save(apiKey);
            var loaded = EditorApiKeyStore.Load();
            Assert.AreEqual(apiKey, loaded);
        }

        [Test]
        [Description("Stored value on disk does NOT contain the plaintext API key.")]
        public void Save_DoesNotPersistPlaintext()
        {
            const string apiKey = "rtmpe_live_DETECTABLE_MARKER_token";
            EditorApiKeyStore.Save(apiKey);

            var stored = EditorPrefs.GetString(EncPrefKey, "");
            Assert.IsNotEmpty(stored, "Encrypted blob should have been written.");
            StringAssert.DoesNotContain(apiKey, stored,
                "Plaintext API key must not appear in the EditorPrefs blob.");
            StringAssert.DoesNotContain("DETECTABLE_MARKER", stored,
                "No plaintext fragment of the API key may leak.");
        }

        [Test]
        [Description("Two encrypts of the same plaintext yield distinct ciphertexts (fresh nonce).")]
        public void Save_UsesFreshNoncePerCall()
        {
            const string apiKey = "rtmpe_test_nonce_freshness";
            EditorApiKeyStore.Save(apiKey);
            var first = EditorPrefs.GetString(EncPrefKey, "");
            EditorApiKeyStore.Save(apiKey);
            var second = EditorPrefs.GetString(EncPrefKey, "");

            Assert.AreNotEqual(first, second,
                "Each Save() must use a fresh random nonce; ciphertexts must differ.");
        }

        // ── The machine-wide record an older SDK left ────────────────────
        //
        // ⛔ These cases used to require the OPPOSITE: that `Load()` adopt the
        // unscoped plaintext into this project's slot and delete it. That is the
        // defect the store was repaired to remove — `EditorPrefs` is per-user and
        // never per-project, so one entry served every RTMPE project on the
        // machine, and adopting it made a developer opening a second project
        // authenticate to the gateway as the FIRST project's tenant. A missing
        // key fails loudly; an adopted one fails silently, as somebody else.

        [Test]
        [Description("A machine-wide record is reported once and never adopted, and Load does not delete it.")]
        public void Load_ReportsTheMachineWideRecord_AndNeitherAdoptsNorDeletesIt()
        {
            const string legacyKey = "rtmpe_legacy_PLAINTEXT_keymaterial";
            EditorPrefs.SetString(LegacyPrefKey, legacyKey);
            EditorPrefs.DeleteKey(EncPrefKey);
            EditorApiKeyStore.ResetLegacyReportForTest();

            LogAssert.Expect(LogType.Warning, new Regex("has NOT been adopted"));
            var loaded = EditorApiKeyStore.Load();

            Assert.AreEqual("", loaded, "A record that may belong to another project must not be returned.");
            Assert.AreEqual(legacyKey, EditorPrefs.GetString(LegacyPrefKey, ""),
                "It may be another project's only copy, so nothing here may delete it.");
            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey),
                "Nothing may be written into this project's slot from it.");
        }

        [Test]
        [Description("The report is made once per Editor session, whatever the answer.")]
        public void Load_ReportsTheMachineWideRecord_OnlyOnce()
        {
            EditorPrefs.SetString(LegacyPrefKey, "rtmpe_legacy_PLAINTEXT_keymaterial");
            EditorPrefs.DeleteKey(EncPrefKey);
            EditorApiKeyStore.ResetLegacyReportForTest();

            LogAssert.Expect(LogType.Warning, new Regex("has NOT been adopted"));
            EditorApiKeyStore.Load();
            EditorApiKeyStore.Load();   // a second warning here would fail the run
        }

        [Test]
        [Description("An empty machine-wide value is not a record, so it is not reported.")]
        public void Load_EmptyMachineWideValue_IsNotReported()
        {
            EditorPrefs.SetString(LegacyPrefKey, "");
            EditorPrefs.DeleteKey(EncPrefKey);
            EditorApiKeyStore.ResetLegacyReportForTest();

            var loaded = EditorApiKeyStore.Load();

            // Stated, not left to the runner's default: an empty slot is not a
            // record, so there is nothing to report and no warning to expect.
            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual("", loaded);
            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey));
        }

        // ── Clear / empty contracts ──────────────────────────────────────

        [Test]
        [Description("Clear erases this project's slot and leaves the machine-wide record alone.")]
        public void Clear_RemovesThisProjectsSlot_AndLeavesTheMachineWideRecord()
        {
            EditorApiKeyStore.Save("anything");
            EditorPrefs.SetString(LegacyPrefKey, "leftover");

            EditorApiKeyStore.Clear();

            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey));
            Assert.AreEqual("leftover", EditorPrefs.GetString(LegacyPrefKey, ""),
                "Clearing this project's key must not destroy a record another project "
                + "may be the only holder of.");
        }

        [Test]
        [Description("Save does not scrub the machine-wide record either.")]
        public void Save_LeavesTheMachineWideRecordAlone()
        {
            EditorPrefs.SetString(LegacyPrefKey, "leftover");

            EditorApiKeyStore.Save("anything");

            Assert.AreEqual("leftover", EditorPrefs.GetString(LegacyPrefKey, ""));
        }

        [Test]
        [Description("Save(empty) clears the encrypted slot rather than persisting an empty blob.")]
        public void SaveEmpty_ClearsEncryptedSlot()
        {
            EditorApiKeyStore.Save("something");
            Assert.IsTrue(EditorPrefs.HasKey(EncPrefKey));

            EditorApiKeyStore.Save("");
            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey));
        }

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

        // ── Tamper detection ─────────────────────────────────────────────

        [Test]
        [Description("Tampering with the ciphertext causes Decrypt to fail and the slot to be cleared.")]
        public void Load_TamperedCiphertext_RejectedAndCleared()
        {
            EditorApiKeyStore.Save("rk_tamper_target");
            var blob = EditorPrefs.GetString(EncPrefKey, "");
            Assert.IsNotEmpty(blob);

            // Flip the final base64 char to a different valid one to corrupt
            // the GCM ciphertext / tag region.
            var raw = Convert.FromBase64String(blob);
            raw[raw.Length - 1] ^= 0x01;
            EditorPrefs.SetString(EncPrefKey, Convert.ToBase64String(raw));

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            var loaded = EditorApiKeyStore.Load();
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual("", loaded, "Tampered blob must not yield plaintext.");
            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey),
                "Unreadable blob should be cleared so the wizard prompts for a fresh key.");
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

        // ── M-046: per-Editor random fallback IKM (no constant string) ───

        private static string FallbackIkmPrefKey =>
            EditorProjectScope.Scoped("RTMPE_EditorApiKeyStore_FallbackIkm_v1");

        [Test]
        [Description(
            "When the device id is missing, the IKM fallback is a CSPRNG-generated " +
            "32-byte value persisted in EditorPrefs — never the constant " +
            "\"rtmpe-unknown-device\" the prior implementation used.")]
        public void FallbackIkm_IsRandom_NotConstant()
        {
            // Snapshot any prior value so we can restore it.
            bool   hadPrior   = EditorPrefs.HasKey(FallbackIkmPrefKey);
            string priorValue = hadPrior ? EditorPrefs.GetString(FallbackIkmPrefKey) : null;

            try
            {
                EditorPrefs.DeleteKey(FallbackIkmPrefKey);

                // First call should populate the slot with 32 random bytes.
                var first = EditorApiKeyStore.LoadOrCreateFallbackIkm();
                Assert.AreEqual(32, first.Length);

                string stored = EditorPrefs.GetString(FallbackIkmPrefKey, "");
                Assert.IsNotEmpty(stored,
                    "Fallback IKM must be persisted so the KEK is stable across Editor restarts.");
                Assert.AreEqual(64, stored.Length,
                    "32 bytes encoded as base16 = 64 hex chars.");

                // Subsequent call must return the SAME bytes (otherwise the
                // KEK changes between runs and previously-saved API keys
                // become unreadable on restart).
                var second = EditorApiKeyStore.LoadOrCreateFallbackIkm();
                Assert.AreEqual(first, second);

                // The constant the prior implementation used must NOT equal
                // the random bytes (statistically impossible at 32 random
                // bytes, but assert it explicitly).
                var constantBytes = Encoding.UTF8.GetBytes("rtmpe-unknown-device");
                Assert.AreNotEqual(constantBytes, first,
                    "Fallback must not equal the legacy constant IKM under any circumstances.");
            }
            finally
            {
                EditorPrefs.DeleteKey(FallbackIkmPrefKey);
                if (hadPrior) EditorPrefs.SetString(FallbackIkmPrefKey, priorValue);
            }
        }

        [Test]
        [Description(
            "Two independent invocations of the fallback IKM generator on a " +
            "machine where the slot is wiped between runs must produce DIFFERENT " +
            "bytes — proving the source is a CSPRNG, not a deterministic constant.")]
        public void FallbackIkm_DifferentRunsProduceDifferentBytes()
        {
            bool   hadPrior   = EditorPrefs.HasKey(FallbackIkmPrefKey);
            string priorValue = hadPrior ? EditorPrefs.GetString(FallbackIkmPrefKey) : null;

            try
            {
                EditorPrefs.DeleteKey(FallbackIkmPrefKey);
                var run1 = EditorApiKeyStore.LoadOrCreateFallbackIkm();

                EditorPrefs.DeleteKey(FallbackIkmPrefKey);
                var run2 = EditorApiKeyStore.LoadOrCreateFallbackIkm();

                Assert.AreEqual(32, run1.Length);
                Assert.AreEqual(32, run2.Length);
                Assert.AreNotEqual(run1, run2,
                    "Two CSPRNG draws must differ; identical output indicates a constant fallback.");
            }
            finally
            {
                EditorPrefs.DeleteKey(FallbackIkmPrefKey);
                if (hadPrior) EditorPrefs.SetString(FallbackIkmPrefKey, priorValue);
            }
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
