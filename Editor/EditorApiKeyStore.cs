// RTMPE SDK — Editor/EditorApiKeyStore.cs
//
// Editor-time obfuscated storage for the gateway API key.
//
// Threat model
// ────────────
// In-scope:
//  * Casual disk-image inspection of the developer's home directory
//    (e.g. a stolen laptop image, an unintended backup, a screenshot of
//    the Unity prefs file).  EditorPrefs serialises to a plaintext
//    plist / registry / ini file; storing the API key verbatim there
//    leaks it into every backup tool the developer runs.
// Out-of-scope:
//  * Malware running interactively as the developer's user account.
//    Any process with that privilege can read the device identifier
//    used to derive the key and replay this code path.  Defending
//    against that requires an OS-level keychain or hardware-backed
//    enclave, which is project-integrator territory.  The wizard UI
//    surfaces this caveat explicitly.
//
// Cryptographic construction
// ──────────────────────────
//  KEK = HKDF-SHA256(IKM = SystemInfo.deviceUniqueIdentifier,
//                    salt = STATIC_APP_SALT,
//                    info = "RTMPE/Editor/ApiKeyStore/v1",
//                    L    = 32)
//  ciphertext, tag = ChaCha20-Poly1305(KEK, nonce, plaintext, aad = "v1")
//  stored = base64( "v1" || nonce(12) || ciphertext || tag(16) )
//
// We use the package's managed ChaCha20-Poly1305 implementation rather
// than System.Security.Cryptography.AesGcm because AesGcm was added in
// .NET Core 3.0 / .NET 5+ — Unity's Editor Mono runtime on 2022.3 LTS
// (Mono 6.x) and even the Unity 6 Editor on most platforms do not expose
// it.  Instantiating AesGcm there throws TypeInitializationException the
// first time the developer tries to save an API key.  ChaCha20-Poly1305
// from Runtime/Crypto/Internal/ is pure managed C# and works on every
// runtime the package targets.
//
// A fresh 96-bit nonce is generated for every save.  Poly1305 tag is
// verified before plaintext is returned; on tag failure (corruption,
// machine swap) we treat the stored value as unreadable and return "".
//
// Key derivation portability
// ──────────────────────────
// The KEK is derived from the per-user device identifier ONLY — not from
// Application.dataPath.  Mixing the project path into the KEK made the
// stored ciphertext stop decrypting whenever the developer renamed or
// moved the project folder, silently losing their saved API key.  The
// per-machine binding still defeats casual cross-laptop replay; the
// project path was never part of the security model, only an accidental
// portability foot-gun.
//
// The record an older build left
// ──────────────────────────────
// Older builds wrote the API key directly into EditorPrefs under the key
// "RTMPE_ApiKey" — a name carrying no project, so one entry served every RTMPE
// project on the machine.  Load() used to re-encrypt it into THIS project's
// entry and return it, which is how a developer opening a second project
// inherited the first's key and authenticated to the gateway as the first
// project's tenant.
//
// It is now REPORTED and never adopted, and neither Save() nor Clear() deletes
// it: which project it belongs to is not knowable from here, and it may be the
// only copy another one has.

using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using RTMPE.Crypto.Internal;

namespace RTMPE.Editor
{
    internal static class EditorApiKeyStore
    {
        // ⛔ Scoped, on the same terms as ApiKeyStore. `EditorPrefs` is per-user
        // and per-Unity-install, never per-project, so a fixed name is one
        // record shared by every RTMPE project on the machine — and the record
        // here authenticates to the gateway as a tenant.
        private static string PrefKey        => EditorProjectScope.Scoped(BasePrefKey);
        private const  string BasePrefKey     = "RTMPE_ApiKey_Enc_v1";

        // Unscoped by definition — what an older SDK wrote.
        private const string LegacyPrefKey   = "RTMPE_ApiKey";

        /// <summary>One report per Editor session, whatever the answer.</summary>
        private static bool _reportedLegacySlot;

#if UNITY_INCLUDE_TESTS
        internal static void ResetLegacyReportForTest() => _reportedLegacySlot = false;
#endif
        private const string Version         = "v1";
        private const int    NonceLen        = 12;
        private const int    TagLen          = 16;

        // EditorPrefs slot used to persist the per-Editor random IKM
        // fallback when SystemInfo.deviceUniqueIdentifier is unavailable
        // (WebGL stub, headless / "n/a" platforms).  Stored as base16 so
        // the value survives the EditorPrefs string round-trip on every
        // platform.  Never used when a real device id is present.
        //
        // Threat model (Editor-local):
        //   • In-scope:  cross-machine equality of the derived KEK.  Two
        //     Editors that both fall back to the previous constant string
        //     IKM produced identical KEKs and could decrypt each other's
        //     stored ciphertexts on backup/restore mishaps.  Replacing
        //     the constant with a per-Editor random closes that class.
        //   • Out-of-scope: malware running as the Editor's user account.
        //     Any process at that privilege can read the same EditorPrefs
        //     slot and reproduce the KEK.  Defending against that requires
        //     an OS-level keychain — see ApiKeyStore.cs.
        private static string FallbackIkmPrefKey => EditorProjectScope.Scoped(BaseFallbackIkmPrefKey);
        private const  string BaseFallbackIkmPrefKey = "RTMPE_EditorApiKeyStore_FallbackIkm_v1";
        private const int    FallbackIkmBytes   = 32;

        // A fixed application salt.  This is NOT a secret — its only role is
        // to domain-separate the HKDF output from any other tool that might
        // hash the same device identifier.
        private static readonly byte[] AppSalt =
            Encoding.UTF8.GetBytes("RTMPE.Editor.ApiKeyStore.salt.v1");

        private static readonly byte[] HkdfInfo =
            Encoding.UTF8.GetBytes("RTMPE/Editor/ApiKeyStore/v1");

        private static readonly byte[] Aad =
            Encoding.UTF8.GetBytes(Version);

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Persist the API key in obfuscated form.  Empty / null clears
        /// the stored value.  Leaves any machine-wide entry from an older SDK
        /// alone — it may belong to another project.
        /// </summary>
        public static void Save(string apiKey)
        {
            // ⛔ The legacy slot is NOT scrubbed here. It carries no project, so
            // it belongs to whichever RTMPE project on this machine wrote it —
            // possibly another one, for which it may be the only copy. Saving
            // this project's key is not a licence to destroy that.

            if (string.IsNullOrEmpty(apiKey))
            {
                EditorPrefs.DeleteKey(PrefKey);
                return;
            }

            try
            {
                var encoded = Encrypt(apiKey);
                EditorPrefs.SetString(PrefKey, encoded);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Failed to encrypt API key for storage: {ex.Message}");
            }
        }

        /// <summary>
        /// Read the API key.  Returns "" when nothing is stored or the
        /// ciphertext is unreadable on this machine (corruption, machine
        /// reinstall, etc.).  Reports a machine-wide entry from an older SDK
        /// once, and never adopts it.
        /// </summary>
        public static string Load()
        {
            // ⛔ A machine-wide record is REPORTED, never adopted — the same
            // rule ApiKeyStore states, and for the same reason. The migration
            // that used to sit here read the unscoped plaintext slot, encrypted
            // it into THIS project's entry and returned it, so a developer
            // opening a second RTMPE project inherited the first's key and
            // authenticated to the gateway as the first project's tenant.
            //
            // Which project it belongs to is not knowable from here. A missing
            // key fails loudly; an adopted one fails silently, as somebody else.
            if (EditorPrefs.HasKey(LegacyPrefKey)
                && !string.IsNullOrEmpty(EditorPrefs.GetString(LegacyPrefKey, ""))
                && !_reportedLegacySlot)
            {
                _reportedLegacySlot = true;
                Debug.LogWarning(
                    "[RTMPE] An API key saved by an earlier SDK version is stored for this " +
                    "machine rather than for this project, and has NOT been adopted — it may " +
                    "belong to a different RTMPE project, and using it would connect as that " +
                    "project's tenant. Enter this project's key in Window → RTMPE → Setup " +
                    "Wizard, and remove the machine-wide entry yourself once every RTMPE " +
                    "project here has a key of its own.");
            }

            var encoded = EditorPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(encoded)) return "";

            try
            {
                return Decrypt(encoded);
            }
            catch (CryptographicException)
            {
                // Tag mismatch — most commonly the project was copied to a
                // different developer's machine.  Drop the unreadable blob
                // so the wizard prompts for a fresh key.
                Debug.LogWarning("[RTMPE] Stored API key cannot be decrypted on this machine; clearing.");
                EditorPrefs.DeleteKey(PrefKey);
                return "";
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Failed to decrypt stored API key: {ex.Message}");
                return "";
            }
        }

        /// <summary>Unconditionally remove every stored form of the key.</summary>
        public static void Clear()
        {
            EditorPrefs.DeleteKey(PrefKey);

            // ⛔ And nothing unscoped: clearing this project's key must not
            // destroy a machine-wide record another project may be the only
            // holder of.
        }

        // ── Internals ─────────────────────────────────────────────────────

        // Public for unit tests so they can verify ciphertext round-trips
        // without re-implementing the wire format.
        internal static string Encrypt(string plaintext)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

            var key       = DeriveKey();
            var nonce     = new byte[NonceLen];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonce);

            var ptBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] sealedBlob = null;
            try
            {
                // ChaCha20Poly1305Impl.Seal returns ciphertext || tag.
                sealedBlob = ChaCha20Poly1305Impl.Seal(key, nonce, ptBytes, Aad);

                var versionBytes = Encoding.ASCII.GetBytes(Version);
                var blob = new byte[versionBytes.Length + NonceLen + sealedBlob.Length];
                Buffer.BlockCopy(versionBytes, 0, blob, 0,                                versionBytes.Length);
                Buffer.BlockCopy(nonce,        0, blob, versionBytes.Length,              NonceLen);
                Buffer.BlockCopy(sealedBlob,   0, blob, versionBytes.Length + NonceLen,    sealedBlob.Length);
                return Convert.ToBase64String(blob);
            }
            finally
            {
                // Best-effort scrub of sensitive intermediate buffers from managed heap.
                Array.Clear(ptBytes, 0, ptBytes.Length);
                Array.Clear(key,     0, key.Length);
            }
        }

        internal static string Decrypt(string encoded)
        {
            var blob = Convert.FromBase64String(encoded);
            var versionBytes = Encoding.ASCII.GetBytes(Version);

            if (blob.Length < versionBytes.Length + NonceLen + TagLen)
                throw new CryptographicException("Ciphertext blob is truncated.");

            for (int i = 0; i < versionBytes.Length; i++)
                if (blob[i] != versionBytes[i])
                    throw new CryptographicException("Unknown ciphertext version.");

            var nonce = new byte[NonceLen];
            var ctWithTagLen = blob.Length - versionBytes.Length - NonceLen;
            var ctWithTag = new byte[ctWithTagLen];

            Buffer.BlockCopy(blob, versionBytes.Length,            nonce,     0, NonceLen);
            Buffer.BlockCopy(blob, versionBytes.Length + NonceLen, ctWithTag, 0, ctWithTagLen);

            var key = DeriveKey();
            try
            {
                var pt = ChaCha20Poly1305Impl.Open(key, nonce, ctWithTag, Aad);
                if (pt == null)
                    throw new CryptographicException("ChaCha20-Poly1305 tag verification failed.");
                try
                {
                    return Encoding.UTF8.GetString(pt);
                }
                finally
                {
                    Array.Clear(pt, 0, pt.Length);
                }
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        /// <summary>
        /// HKDF-SHA256 derivation from the per-machine device identifier.
        /// Project path is intentionally excluded so renaming or moving the
        /// project on the same machine does not invalidate the stored
        /// ciphertext.  Per-project isolation is not part of the threat
        /// model: a developer who can read EditorPrefs for project A on a
        /// machine can already trivially open project B in the Editor.
        /// </summary>
        private static byte[] DeriveKey()
        {
            // SystemInfo.deviceUniqueIdentifier is null on a handful of
            // platforms (and "" or "n/a" on others — the WebGL stub returns
            // an empty string, some Android distributions and headless test
            // hosts return the literal "n/a").  Treat any of these
            // pathological values as "unknown" so the KEK is not silently
            // derived from a per-platform constant — which would let every
            // editor on that platform unwrap every other editor's stored
            // ciphertext.  IsNullOrEmpty + the explicit "n/a" check covers
            // every documented case observed in Unity-Editor support
            // bundles.
            var deviceId = SystemInfo.deviceUniqueIdentifier;
            byte[] ikm;
            if (string.IsNullOrEmpty(deviceId) || deviceId == "n/a")
            {
                // Fallback: per-Editor random IKM persisted in EditorPrefs.
                // The previous constant ("rtmpe-unknown-device") produced
                // identical KEKs across every Editor that hit this branch,
                // letting one machine's ciphertext decrypt on any other.
                // A 32-byte random generated on first use, then reused, is
                // unique per Editor install and stable across sessions —
                // so saved API keys remain readable while cross-machine
                // equality is broken.
                ikm = LoadOrCreateFallbackIkm();
            }
            else
            {
                ikm = Encoding.UTF8.GetBytes(deviceId);
            }

            // RFC 5869 HKDF-SHA256, single-block (L = 32 ≤ HashLen).
            byte[] prk;
            using (var hmac = new HMACSHA256(AppSalt))
                prk = hmac.ComputeHash(ikm);

            var t1Input = new byte[HkdfInfo.Length + 1];
            Buffer.BlockCopy(HkdfInfo, 0, t1Input, 0, HkdfInfo.Length);
            t1Input[HkdfInfo.Length] = 0x01;

            byte[] okm;
            using (var hmac = new HMACSHA256(prk))
                okm = hmac.ComputeHash(t1Input);

            Array.Clear(prk, 0, prk.Length);
            Array.Clear(ikm, 0, ikm.Length);
            return okm; // 32 bytes
        }

        /// <summary>
        /// Load (or generate-and-store) the per-Editor random fallback IKM
        /// used when <see cref="SystemInfo.deviceUniqueIdentifier"/> is
        /// missing or returns the literal "n/a".  Always returns a 32-byte
        /// buffer.  First call on a fresh Editor seeds the slot from a
        /// CSPRNG; subsequent calls return the same bytes so persisted
        /// ciphertexts remain decryptable across Editor restarts.
        /// </summary>
        internal static byte[] LoadOrCreateFallbackIkm()
        {
            string hex = EditorPrefs.GetString(FallbackIkmPrefKey, string.Empty);
            byte[] ikm = TryParseHex(hex);
            if (ikm != null && ikm.Length == FallbackIkmBytes)
                return ikm;

            // First use on this Editor — generate fresh random bytes from
            // a CSPRNG.  RandomNumberCryptoServiceProvider / RandomNumberGenerator
            // is the only acceptable source: System.Random is seeded from
            // the system clock and would produce predictable IKMs across
            // installs that started within the same second.
            var fresh = new byte[FallbackIkmBytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(fresh);
            EditorPrefs.SetString(FallbackIkmPrefKey, ToHex(fresh));
            return fresh;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.AppendFormat("{0:x2}", bytes[i]);
            return sb.ToString();
        }

        private static byte[] TryParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            if ((hex.Length & 1) != 0) return null;
            var buf = new byte[hex.Length / 2];
            for (int i = 0; i < buf.Length; i++)
            {
                int hi = HexNibble(hex[2 * i]);
                int lo = HexNibble(hex[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                buf[i] = (byte)((hi << 4) | lo);
            }
            return buf;
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return -1;
        }
    }
}
