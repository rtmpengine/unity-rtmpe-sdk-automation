// RTMPE SDK — Editor/ApiKeyStore.cs
//
// Editor-only credential store for the RTMPE API key.
//
// ============================================================================
// SECURITY / THREAT MODEL
// ============================================================================
// The Unity Editor stores per-user "EditorPrefs" in plaintext (Windows
// registry HKCU, macOS ~/Library/Preferences plist, Linux ~/.config/unity3d).
// Persisting an RTMPE API key — which is sufficient to authenticate
// against the gateway as the developer's project — to plaintext disk is a
// credential-theft risk: any process running under the developer's user
// account (browser extensions, malicious npm packages, recovered backups)
// can read the key.
//
// This class hides that secret behind the platform's user-scoped
// credential vault:
//
//   • Windows  — DPAPI (CryptProtectData / CryptUnprotectData with
//                CRYPTPROTECT_LOCAL_MACHINE = 0). Ciphertext is bound to
//                the current user's Windows login; another local user
//                cannot recover the key. Per Microsoft DPAPI guidance.
//
//   • macOS    — Keychain Services via the `security` CLI (add/find/
//                delete-generic-password). The default user keychain is
//                gated by the user's login password (or biometrics on
//                Apple silicon). Same isolation guarantees as Xcode's
//                Apple-ID storage.
//
//   • Linux    — libsecret via the `secret-tool` CLI (freedesktop.org
//                Secret Service API). The active user's session keyring
//                (gnome-keyring, KWallet) decrypts the secret only while
//                the desktop session is unlocked.
//
//   • Other / unsupported — clear log warning + fallback to obfuscated
//                EditorPrefs. The fallback uses a per-machine random
//                32-byte vault key (also stored in EditorPrefs) and
//                ChaCha20-Poly1305 to detect tampering. This is
//                obfuscation, not encryption — explicitly documented in
//                the warning log so integrators can choose to opt out.
//
// IMPORTANT: this file is Editor-only. It is NEVER shipped with builds.
// The key persisted here is a development convenience: EditorApiKeyProvider
// registers Load() with RTMPE.Core.ApiKeySource, so entering Play mode uses
// the vault rather than a value typed into a scene. A built player never
// reaches this store: it gets its key from a provider the integrator registers,
// from a key staged for a development build, from the command line or from the
// environment — see ApiKeySource for
// what each of those does and does not protect. There is no API-key field on
// NetworkSettings, and there must not be: a serialized field is written into
// the asset, committed with it, and shipped inside the player.
//
// References:
//   • Microsoft DPAPI:    learn.microsoft.com/dotnet/standard/security/
//                         how-to-use-data-protection
//   • Apple Keychain:     developer.apple.com/documentation/security/
//                         keychain_services
//   • Freedesktop Secret: specifications.freedesktop.org/secret-service/
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace RTMPE.Editor
{
    /// <summary>
    /// Reads and writes the RTMPE API key from a per-user OS credential
    /// vault. Editor-only; never compiled into player builds.
    /// </summary>
    /// <remarks>
    /// Thread-affine to the Editor main thread (calls
    /// <c>UnityEditor.EditorPrefs</c> in the fallback path).
    /// </remarks>
    public static class ApiKeyStore
    {
        // Single canonical service name so all SDK Editor tooling reads and
        // writes the same vault.
        private const string ServiceName = "com.rtmpe.sdk";

        // ⛔ The account, and both fallback markers, carry the PROJECT.
        //
        // They were fixed strings, and neither EditorPrefs nor an OS vault is
        // project-scoped on its own — so one record served every RTMPE project
        // on the machine and the last one saved won. A developer working on two
        // projects in turn had the second silently adopt the first's key and
        // authenticate to the gateway AS THE FIRST TENANT, with nothing
        // reporting it.
        private static string AccountName          => Scoped(LegacyAccountName);
        private static string FallbackEntropyPref  => Scoped(LegacyFallbackEntropyPref);
        private static string FallbackBlobPref     => Scoped(LegacyFallbackBlobPref);

        private static string Scoped(string name) => EditorProjectScope.Scoped(name);

        // The unscoped names an earlier SDK version wrote. Read only to REPORT
        // that something is there — never adopted; see LoadInternal.
        private const string LegacyAccountName         = "ApiKey";
        private const string LegacyFallbackEntropyPref = "RTMPE_ApiKey.fallback.entropy.v1";
        private const string LegacyFallbackBlobPref    = "RTMPE_ApiKey.fallback.blob.v1";

        // Migration: SetupWizard previously stored the API key in a
        // plaintext EditorPrefs entry under this name. Reads transparently
        // upgrade the entry into the secure store and delete the plaintext.
        private const string LegacyPlaintextPref = "RTMPE_ApiKey";

        /// <summary>
        /// Read the API key, returning <c>""</c> if no key has been saved.
        /// On first call, transparently migrates legacy plaintext entries
        /// into the secure store.
        /// </summary>
        public static string Load()
        {
            // ⛔ There is no step here that ADOPTS a record written before the
            // store was project-scoped, and that is the whole of EDITOR-R01.
            //
            // A migration used to sit at the top of this method: read the
            // unscoped plaintext entry, Save() it into this project's vault,
            // delete the plaintext and return it — BEFORE this project's own
            // key was ever consulted. That is the finding verbatim. A developer
            // opening a second RTMPE project inherited the first's key and Play
            // mode connected to the gateway as the first project's tenant; and
            // because the Save() was unconditional, a project that already had
            // a key of its own had it overwritten.
            //
            // Which project an unscoped record belongs to is not knowable from
            // here, so it is reported instead — see ReportUnscopedRecordOnce.

            // 2. OS-keychain path.
            try
            {
                if (TryReadFromOsKeychain(out var fromOs)) return fromOs ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RTMPE] OS keychain read failed: {ex.GetType().Name} — {ex.Message}");
            }

            // 3. Fallback path.
            var fallback = TryReadFallback();
            if (fallback != null) return fallback;

            // 4. Nothing under this project's scope. An older SDK version stored
            //    one record for the whole machine, and it may belong to another
            //    project — so it is reported, never adopted.
            ReportUnscopedRecordOnce();
            return "";
        }

        private static bool _reportedUnscopedRecord;

        /// <summary>Test seam: reopen the one-shot unscoped-record report.</summary>
        internal static void ResetUnscopedReportForTest() => _reportedUnscopedRecord = false;

        /// <summary>
        /// Say once that a key saved by an earlier SDK version exists and has
        /// not been taken.
        /// </summary>
        /// <remarks>
        /// ⛔ Reported and not adopted, deliberately. Adoption is a guess about
        /// which project the record belongs to, and the guess that is wrong
        /// hands this project another tenant's credential and lets Play mode
        /// connect as them — quietly, which is the defect being closed rather
        /// than a smaller version of it. A missing key fails loudly at the
        /// handshake instead, and re-entering one is a ten-second action.
        ///
        /// <para>⛔ And nothing here DELETES the legacy record: it may be the
        /// only copy another project still needs. It stops being read the
        /// moment this project has a key of its own.</para>
        /// </remarks>
        private static void ReportUnscopedRecordOnce()
        {
            if (_reportedUnscopedRecord) return;

            // ⛔ Latched whatever the answer, and before the probe. Latching
            // only on a hit meant the ordinary "no key configured yet"
            // developer spawned a `security` / `secret-tool` child on EVERY
            // Load(), for ever — a read probe paying for a record that is not
            // there.
            _reportedUnscopedRecord = true;

            bool present = EditorPrefs.HasKey(LegacyFallbackBlobPref)
                        || EditorPrefs.HasKey(LegacyPlaintextPref);
            if (!present)
            {
                try
                {
                    // Only whether it is there — the value is not bound beyond
                    // this test and is never logged.
                    present = TryReadFromOsKeychain(LegacyAccountName, out var found)
                              && !string.IsNullOrEmpty(found);
                }
                catch
                {
                    // A probe that cannot run has nothing to report; staying
                    // silent is the honest answer, not a cleared flag.
                }
            }
            if (!present) return;

            Debug.LogWarning(
                "[RTMPE] An API key saved by an earlier SDK version is stored for this machine " +
                "rather than for this project, and has NOT been adopted — it may belong to a " +
                "different RTMPE project, and using it would connect as that project's tenant. " +
                "Enter this project's key in Window → RTMPE → Setup Wizard; the machine-wide " +
                "record is left alone in case another project still needs it. If it is the " +
                "plaintext EditorPrefs entry an old SDK wrote, remove it yourself once every " +
                "RTMPE project on this machine has a key of its own.");
        }

        /// <summary>
        /// Persist the API key. Empty / null clears the entry.
        /// </summary>
        public static void Save(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Delete();
                return;
            }

            try
            {
                if (TryWriteToOsKeychain(apiKey))
                {
                    // The secure vault now holds the key; drop any obfuscated
                    // EditorPrefs fallback left by an earlier keychain-unavailable
                    // save so rotating into the vault leaves nothing recoverable
                    // behind. The vault is read before the fallback, so no key is
                    // lost by clearing it here.
                    EditorPrefs.DeleteKey(FallbackBlobPref);
                    EditorPrefs.DeleteKey(FallbackEntropyPref);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RTMPE] OS keychain write failed: {ex.GetType().Name} — {ex.Message}");
            }

            WriteFallback(apiKey);
            Debug.LogWarning(
                "[RTMPE] OS-keychain credential store unavailable — falling back to " +
                "obfuscated EditorPrefs. The API key is NOT cryptographically " +
                "protected against another process running as your user. Install " +
                "the platform credential helper (libsecret on Linux) or run the " +
                "Editor on a supported platform to enable secure storage.");
        }

        /// <summary>Delete any saved API key from every backend.</summary>
        public static void Delete()
        {
            try { TryDeleteFromOsKeychain(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RTMPE] OS keychain delete failed: {ex.GetType().Name} — {ex.Message}");
            }

            EditorPrefs.DeleteKey(FallbackEntropyPref);
            EditorPrefs.DeleteKey(FallbackBlobPref);

            // ⛔ And nothing unscoped. Clearing the key in THIS project must not
            // destroy a machine-wide record another project may be the only
            // holder of — the deletion that used to be here did exactly that.
            // ⚠️ The plaintext entry is a credential on disk and leaving it is a
            // real cost; it is the developer's to remove once every project has
            // a key of its own, and the report at Load() says so. It is not a
            // cost this method may pay on another project's behalf.
        }

        // ── Platform dispatch ────────────────────────────────────────────────

        private static bool TryReadFromOsKeychain(out string apiKey)
            => TryReadFromOsKeychain(AccountName, out apiKey);

        /// <summary>
        /// Read the record stored under <paramref name="account"/>.
        /// </summary>
        /// <remarks>
        /// The account is a parameter and not the property so the same readers
        /// can PROBE the unscoped record an older SDK version left, without any
        /// path being able to adopt it by accident.
        /// </remarks>
        private static bool TryReadFromOsKeychain(string account, out string apiKey)
        {
            apiKey = null;
#if UNITY_EDITOR_WIN
            return TryReadDpapi(account, out apiKey);
#elif UNITY_EDITOR_OSX
            return TryReadMacKeychain(account, out apiKey);
#elif UNITY_EDITOR_LINUX
            return TryReadSecretTool(account, out apiKey);
#else
            return false;
#endif
        }

        private static bool TryWriteToOsKeychain(string apiKey)
        {
#if UNITY_EDITOR_WIN
            return TryWriteDpapi(apiKey);
#elif UNITY_EDITOR_OSX
            return TryWriteMacKeychain(apiKey);
#elif UNITY_EDITOR_LINUX
            return TryWriteSecretTool(apiKey);
#else
            return false;
#endif
        }

        private static void TryDeleteFromOsKeychain()
        {
#if UNITY_EDITOR_WIN
            TryDeleteDpapi();
#elif UNITY_EDITOR_OSX
            TryDeleteMacKeychain();
#elif UNITY_EDITOR_LINUX
            TryDeleteSecretTool();
#endif
        }

        // ── Windows: DPAPI ───────────────────────────────────────────────────
        //
        // DPAPI is invoked via P/Invoke (System.Security.Cryptography.
        // ProtectedData is .NET-Framework-only — Unity's Mono runtime does
        // not expose it). The ciphertext is persisted to a per-user file
        // under %APPDATA%/RTMPE/ApiKey.bin so the secret never touches
        // EditorPrefs / the Windows registry.

#if UNITY_EDITOR_WIN
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
            int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
            int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// Where the DPAPI ciphertext for <paramref name="account"/> lives.
        /// </summary>
        /// <remarks>
        /// ⛔ One file per account, and the account carries the project — a
        /// single <c>ApiKey.bin</c> under <c>%APPDATA%\RTMPE</c> was the
        /// Windows half of the same defect: two projects, one blob, last save
        /// wins. The name is taken from the account rather than composed here
        /// so a rename of the scope cannot leave the two out of step.
        /// </remarks>
        private static string DpapiFilePath(string account)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "RTMPE");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, SanitiseFileName(account) + ".bin");
        }

        /// <summary>Reduce an account name to characters a path accepts.</summary>
        private static string SanitiseFileName(string account)
        {
            var sb = new StringBuilder(account.Length);
            foreach (char c in account)
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static bool TryWriteDpapi(string apiKey)
        {
            var plaintext = Encoding.UTF8.GetBytes(apiKey);
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            var handle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = plaintext.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (!CryptProtectData(ref inBlob, "RTMPE-ApiKey", IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return false;

                var ciphertext = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, ciphertext, 0, outBlob.cbData);
                File.WriteAllBytes(DpapiFilePath(AccountName), ciphertext);
                return true;
            }
            finally
            {
                Array.Clear(plaintext, 0, plaintext.Length);
                if (handle.IsAllocated) handle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static bool TryReadDpapi(string account, out string apiKey)
        {
            apiKey = null;
            var path = DpapiFilePath(account);
            if (!File.Exists(path)) return false;
            var ciphertext = File.ReadAllBytes(path);
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            var handle = GCHandle.Alloc(ciphertext, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = ciphertext.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return false;

                var plaintext = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, plaintext, 0, outBlob.cbData);
                apiKey = Encoding.UTF8.GetString(plaintext);
                Array.Clear(plaintext, 0, plaintext.Length);
                return true;
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static void TryDeleteDpapi()
        {
            var path = DpapiFilePath(AccountName);
            if (File.Exists(path)) File.Delete(path);
        }
#endif

        // ── macOS: Keychain Services via `security` CLI ──────────────────────

#if UNITY_EDITOR_OSX
        private static bool TryWriteMacKeychain(string apiKey)
        {
            // The API key MUST NOT appear in argv: every user on the system
            // can read another user's argv via `ps -ef` / `ps aux`, and on
            // shared CI runners or developer workstations with multiple
            // accounts that exposure leaks the key beyond the Editor's
            // owning user.  Mirror the Linux secret-tool flow: spawn
            // `security -i` (interactive command mode), then write the
            // command — including the secret — to stdin.  Stdin is a pipe
            // visible only to the parent and child processes; argv is not.
            var psi = new ProcessStartInfo("security", "-i")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            // `security -i` reads one command per line.  Account / service
            // / label values are quoted with backslash-escaping so a stray
            // quote in those (developer-supplied) constants cannot break
            // the parser; the apiKey is similarly quoted so embedded
            // whitespace survives.  Documented `security` quoting rules
            // accept C-style backslash escapes inside double quotes.
            p.StandardInput.WriteLine(
                $"add-generic-password -U -a \"{EscapeShell(AccountName)}\" -s \"{EscapeShell(ServiceName)}\" -w \"{EscapeShell(apiKey)}\"");
            p.StandardInput.Close();
            return TryWaitForCleanExit(p, 5_000, out int exit) && exit == 0;
        }

        private static bool TryReadMacKeychain(string account, out string apiKey)
        {
            apiKey = null;
            var psi = new ProcessStartInfo("security",
                $"find-generic-password -a \"{account}\" -s \"{ServiceName}\" -w")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (!TryReadStdout(p, 5_000, out string stdout)) return false;
            if (!TryWaitForCleanExit(p, 5_000, out int exit) || exit != 0) return false;
            apiKey = stdout.TrimEnd('\r', '\n');
            return !string.IsNullOrEmpty(apiKey);
        }

        private static void TryDeleteMacKeychain()
        {
            var psi = new ProcessStartInfo("security",
                $"delete-generic-password -a \"{AccountName}\" -s \"{ServiceName}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            TryWaitForCleanExit(p, 5_000, out _);
        }

        private static string EscapeShell(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
#endif

        // ── Linux: libsecret via `secret-tool` CLI ───────────────────────────

#if UNITY_EDITOR_LINUX
        private static bool TryWriteSecretTool(string apiKey)
        {
            if (!CommandExists("secret-tool")) return false;
            var psi = new ProcessStartInfo("secret-tool",
                $"store --label=\"RTMPE API Key\" service {ServiceName} account {AccountName}")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p.StandardInput.Write(apiKey);
            p.StandardInput.Close();
            return TryWaitForCleanExit(p, 5_000, out int exit) && exit == 0;
        }

        private static bool TryReadSecretTool(string account, out string apiKey)
        {
            apiKey = null;
            if (!CommandExists("secret-tool")) return false;
            var psi = new ProcessStartInfo("secret-tool",
                $"lookup service {ServiceName} account {account}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (!TryReadStdout(p, 5_000, out apiKey)) { apiKey = null; return false; }
            apiKey = apiKey.TrimEnd('\r', '\n');
            return !string.IsNullOrEmpty(apiKey);
        }

        private static void TryDeleteSecretTool()
        {
            if (!CommandExists("secret-tool")) return;
            var psi = new ProcessStartInfo("secret-tool",
                $"clear service {ServiceName} account {AccountName}")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            TryWaitForCleanExit(p, 5_000, out _);
        }

        private static bool CommandExists(string cmd)
        {
            try
            {
                var psi = new ProcessStartInfo("/bin/sh", $"-c \"command -v {cmd}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                // Drained through the same helper rather than left unread: the
                // output is one short line today, and "today" is the part of
                // that sentence a rule cannot rely on.
                return TryReadStdout(p, 2_000, out _);
            }
            catch { return false; }
        }
#endif

        // Wait for the child to terminate within timeoutMs and report its exit
        // code.  Process.WaitForExit(int) returns FALSE on timeout WITHOUT
        // killing the child; reading p.ExitCode in that state throws
        // InvalidOperationException("Process must exit before requested
        // information can be determined").  A wedged `security` (macOS) or
        // `secret-tool` (Linux) child — gnome-keyring locked, libsecret D-Bus
        // broker hung, mid-update keychain — would otherwise propagate that
        // exception out of the keystore reader and silently downgrade the
        // developer to the obfuscated-EditorPrefs fallback path.  Killing on
        // timeout keeps the keystore semantics observable.
        private static bool TryWaitForCleanExit(Process p, int timeoutMs, out int exitCode)
        {
            if (p == null) { exitCode = -1; return false; }
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { /* already exited or platform refused — best-effort */ }
                exitCode = -1;
                return false;
            }
            try { exitCode = p.ExitCode; }
            catch (InvalidOperationException) { exitCode = -1; return false; }
            return true;
        }

        /// <summary>
        /// Run <paramref name="p"/> to completion within
        /// <paramref name="timeoutMs"/> and hand back what it wrote to stdout.
        /// </summary>
        /// <remarks>
        /// ⛔ The reads are STARTED before the wait, and stderr is drained
        /// alongside stdout. `ReadToEnd()` ahead of `WaitForExit(ms)` is
        /// unbounded: a wedged `security` or `secret-tool` child blocks the
        /// Editor's main thread for as long as it lives and the timeout below
        /// is never reached — the file said a five-second bound applied, and on
        /// both read paths it did not. Redirecting stderr and never reading it
        /// is the same hazard from the other end: a child that fills that pipe
        /// blocks writing to it, which no timeout on the parent can see.
        ///
        /// <para>After a clean exit both pipes are closed, so the reads have
        /// finished or are about to; the second bound is there because "about
        /// to" is not a guarantee.</para>
        /// </remarks>
        /// <remarks>
        /// <c>internal</c> so the suite can drive it against a real child — a
        /// bound nothing runs is a bound nobody has measured, and neither OS
        /// vault path compiles outside the Editor.
        /// </remarks>
        internal static bool TryReadStdout(Process p, int timeoutMs, out string stdout)
        {
            stdout = null;
            if (p == null) return false;

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            Observe(errTask);

            if (!TryWaitForCleanExit(p, timeoutMs, out int exit) || exit != 0)
            {
                Observe(outTask);
                return false;
            }

            if (!outTask.Wait(1_000)) { Observe(outTask); return false; }
            stdout = outTask.Result;
            return true;
        }

        /// <summary>
        /// Keep a discarded read's fault from surfacing as an unobserved task
        /// exception on the finalizer thread.
        /// </summary>
        private static void Observe(System.Threading.Tasks.Task task)
            => task.ContinueWith(
                // 🚨 `Debug.Assert` is [Conditional("DEBUG")], so the compiler
                // removes the call AND its argument: with Code Optimization set
                // to Release the Editor assemblies carry no DEBUG, `t.Exception`
                // was never read, and 300 faulted reads produced 300 unobserved
                // task exceptions — precisely what this exists to prevent.
                t => { _ = t.Exception; },
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);


        // ── Fallback: ChaCha20-Poly1305 over EditorPrefs ─────────────────────
        //
        // This is OBFUSCATION, not encryption — the AEAD key is generated
        // once and stored alongside the ciphertext under EditorPrefs, so
        // any process with access to EditorPrefs can decrypt. The point is
        // (a) tamper-detection (bit-flip in EditorPrefs is detected) and
        // (b) reduction of accidental disclosure (e.g. a developer
        // screen-sharing the Unity preferences plist will not show the
        // API key in cleartext).

        private static byte[] FallbackKey()
        {
            var hex = EditorPrefs.GetString(FallbackEntropyPref, null);
            if (!string.IsNullOrEmpty(hex) && hex.Length == 64)
            {
                return HexToBytes(hex);
            }
            var key = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            EditorPrefs.SetString(FallbackEntropyPref, BytesToHex(key));
            return key;
        }

        private static void WriteFallback(string apiKey)
        {
            var key = FallbackKey();
            try
            {
                var nonce = new byte[12];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
                var plaintext = Encoding.UTF8.GetBytes(apiKey);
                var ciphertext = RTMPE.Crypto.Internal.ChaCha20Poly1305Impl.Seal(
                    key, nonce, plaintext, Array.Empty<byte>());
                var blob = new byte[12 + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, blob, 0, 12);
                Buffer.BlockCopy(ciphertext, 0, blob, 12, ciphertext.Length);
                EditorPrefs.SetString(FallbackBlobPref, Convert.ToBase64String(blob));
                Array.Clear(plaintext, 0, plaintext.Length);
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        private static string TryReadFallback()
        {
            var b64 = EditorPrefs.GetString(FallbackBlobPref, null);
            if (string.IsNullOrEmpty(b64)) return null;
            byte[] blob;
            try { blob = Convert.FromBase64String(b64); }
            catch { return null; }
            if (blob.Length < 12 + 16) return null;

            var key = FallbackKey();
            try
            {
                var nonce = new byte[12];
                Buffer.BlockCopy(blob, 0, nonce, 0, 12);
                var ct = new byte[blob.Length - 12];
                Buffer.BlockCopy(blob, 12, ct, 0, ct.Length);
                var pt = RTMPE.Crypto.Internal.ChaCha20Poly1305Impl.Open(
                    key, nonce, ct, Array.Empty<byte>());
                if (pt == null) return null;
                var s = Encoding.UTF8.GetString(pt);
                Array.Clear(pt, 0, pt.Length);
                return s;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        private static string BytesToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
            return b;
        }
    }
}
#endif
