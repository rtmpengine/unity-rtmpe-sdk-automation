// RTMPE SDK — Runtime/Crypto/EncryptedFilePinStore.cs
//
// Defense-in-depth IServerKeyPinStore implementation that persists
// pinned server keys to a binary file under
// UnityEngine.Application.persistentDataPath (an app-private directory
// on every supported platform) with per-record HMAC-SHA256 integrity
// protection.
//
// THREAT-MODEL COMPARISON vs PlayerPrefsPinStore
// ---------------------------------------------
// PlayerPrefsPinStore writes to the platform's preferences store
// (Android SharedPreferences, iOS user defaults, Windows registry).
// On Android the prefs file is readable+writable to anyone with adb
// shell access OR an attacker holding the cloud-backup blob — the
// stored hex pin can be swapped silently for an attacker's key,
// enabling MITM on the next handshake.
//
// EncryptedFilePinStore raises the bar by:
//   1. Storing pins in Application.persistentDataPath (not exposed via
//      the standard adb-backup path on modern Android, and never via
//      cloud sync on iOS) so casual `adb pull` / restore-from-backup
//      cannot read or rewrite the pin file.
//   2. Binding each pin to an HMAC-SHA256 tag derived from
//      SystemInfo.deviceUniqueIdentifier — pins lifted from one
//      device's file and dropped onto another fail integrity check
//      and read as "no pin", forcing fresh TOFU on the second device
//      instead of trusting the lifted value.
//   3. Detecting per-record bitflips, swap-with-attacker-key, and
//      add-a-forged-record at Load time; any tampered or unverified
//      record degrades to "no pin" rather than yielding a value that
//      would silently authenticate an attacker's gateway.
//
// LIMITATIONS (documented honestly)
//   - On a rooted Android device with read access to both the file
//     AND the binary, an attacker can re-derive the MAC key from
//     SystemInfo.deviceUniqueIdentifier and forge valid records.
//     Hardware-backed key storage (Android Keystore / iOS Keychain)
//     is the proper mitigation against that threat; this class is the
//     pure-managed step on the way there.
//   - SystemInfo.deviceUniqueIdentifier reports its own absence three
//     ways — null, the empty string, and the literal "n/a" — and some
//     platforms (WebGL, headless Linux, several Android
//     distributions) do so.  There the derivation falls through to
//     public constants, and the consequence is larger than the lost
//     transplant detection: the MAC key is then computable by anyone
//     holding this source, so on those platforms point 2 above does
//     not hold and point 3 stops distinguishing a forged record from
//     a genuine one.  Write access to the file is sufficient to
//     author a record that verifies — no device access, no rooted
//     handset, and no read of the binary.  The store is still worth
//     more than the legacy one for its storage location, and the
//     integrity claim is what lapses.  Closing it means deriving from
//     a per-install random value the way the Editor credential store
//     already does, which is a file-format change (version 0x02) and
//     a migration, not an edit to this predicate.
//
// FILE FORMAT (binary, little-endian)
//   header:
//     4 bytes magic   = 0x49 0x50 0x54 0x52  ("IPTR" LE → "RTPI")
//     1 byte  version = 0x01
//     4 bytes record_count (LE u32)
//   record:
//     2 bytes endpoint_len (LE u16, max 1024)
//     N bytes endpoint     (UTF-8, N = endpoint_len)
//    32 bytes pin
//    32 bytes hmac        = HMAC-SHA256(macKey, hmacInput)
//   hmacInput:
//     [version][endpoint_len_LE_2B][endpoint][pin]
//   (version is included so a format upgrade that re-uses the same
//   record layout cannot be silently downgraded; endpoint_len is
//   prefixed so a splice attack that shifts endpoint bytes into the
//   pin region is detected.)

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RTMPE.Crypto.Internal;
using UnityEngine;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Hardened <see cref="IServerKeyPinStore"/> implementation that persists
    /// pins to <see cref="Application.persistentDataPath"/> with per-record
    /// HMAC-SHA256 integrity protection.  Suitable for deployments that need
    /// stronger tamper resistance than <see cref="PlayerPrefsPinStore"/>
    /// against an attacker with adb access on a non-rooted device.
    /// </summary>
    public sealed class EncryptedFilePinStore : IServerKeyPinStore, IPinStoreAvailability
    {
        // ── Wire-format constants (treat as load-bearing) ─────────────────
        // Bytes are stored as a little-endian 32-bit word so the on-disk
        // sequence is 'R' 'T' 'P' 'I' regardless of host endianness.
        internal const uint MagicHeader   = 0x49_50_54_52u;
        internal const byte FormatVersion = 0x01;
        internal const int  PinLength     = 32;
        internal const int  HmacLength    = 32;
        internal const int  MaxEndpointLength = 1024;

        // Ceiling on total file size when reading.  A pathologically large
        // file (corrupt or hostile) would otherwise force an unbounded
        // allocation on Load.  1 MiB comfortably fits ~10 k records.
        internal const int  MaxFileBytes  = 1 << 20;

        internal const string DefaultFileName = "rtmpe-pins.bin";

        // HKDF salt / info bind the derived MAC key to the SDK identity and
        // a version tag so a future crypto rotation can move the namespace
        // without colliding on existing files.
        private static readonly byte[] HkdfSalt = Encoding.UTF8.GetBytes("RTMPE-pin-mac-v1");
        private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("RTMPE-pin-integrity");

        private readonly string _filePath;
        private readonly byte[] _macKey;
        private readonly object _lock = new object();
        private readonly bool _deviceIdUnusable;
        private int _deviceBindingWarned;   // 0 until the degradation warning fires once

        /// <summary>
        /// Construct the default file-backed pin store.  The file lives at
        /// <see cref="Application.persistentDataPath"/> /
        /// <c>rtmpe-pins.bin</c>, and the MAC key is derived from
        /// <see cref="SystemInfo.deviceUniqueIdentifier"/>.
        /// </summary>
        public EncryptedFilePinStore()
            : this(DefaultFilePath(), SystemInfo.deviceUniqueIdentifier ?? string.Empty)
        {
        }

        // Test seam.  Internal so production code cannot accidentally bypass
        // the platform-appropriate path/device-ID providers.  Exposed via
        // [InternalsVisibleTo("RTMPE.PinStore.Tests")] in AssemblyInfo.
        internal EncryptedFilePinStore(string filePath, string deviceId)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            _filePath      = filePath;
            _deviceIdUnusable = IsUnusableDeviceId(deviceId);
            _macKey        = DeriveMacKey(deviceId ?? string.Empty);
        }

        // Unity's SystemInfo.unsupportedIdentifier.  Held as a constant rather
        // than read from SystemInfo so the value the test seam passes in is the
        // one production is judged against.
        internal const string UnsupportedDeviceId = "n/a";

        // SystemInfo.deviceUniqueIdentifier does not report its own absence
        // uniformly: some platforms return null, the WebGL stub returns an
        // empty string, and headless hosts along with a number of Android
        // distributions return the literal "n/a".  All three name a platform
        // with no per-device value to bind to, the Editor's credential store
        // already reads them as one condition, and recognising only two of them
        // left the third taking the degradation in silence — which is the half
        // that mattered, because the warning is the only thing that tells an
        // operator the pin file is no longer bound to this machine.
        //
        // The derivation is deliberately untouched.  A device already deriving
        // its MAC key from "n/a" goes on doing so: moving it would make every
        // record in that device's existing pin file stop verifying, which is
        // the silent trust reset this predicate exists to disclose.
        internal static bool IsUnusableDeviceId(string deviceId)
            => string.IsNullOrEmpty(deviceId) || deviceId == UnsupportedDeviceId;

        // With no per-device identifier the record MAC key derives from public
        // constants only: it still detects accidental corruption, and it stops
        // being evidence of anything an attacker would have to defeat — a
        // transplanted file passes the MAC, and so does one an attacker wrote,
        // because the key is computable from this source alone.
        // Surface that degradation once, lazily on first use, so it is never
        // emitted in Strict-with-configured-pin where the store is constructed
        // but never consulted.
        private void WarnDeviceBindingDegradedOnce()
        {
            if (!_deviceIdUnusable) return;
            if (System.Threading.Interlocked.Exchange(ref _deviceBindingWarned, 1) != 0) return;
            Debug.LogWarning(
                "[RTMPE] EncryptedFilePinStore: device identifier unavailable on this " +
                "platform, so the pin file's integrity tag is derived from public " +
                "constants alone. Pins remain functional, and the protection that " +
                "lapses is larger than transplant detection: anyone able to write this " +
                "file can author a record that verifies. Treat the pin file as " +
                "advisory here and configure Strict mode with a known key for anything " +
                "you ship.");
        }

        private static string DefaultFilePath()
        {
            return Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        // ── Public IServerKeyPinStore surface ─────────────────────────────

        public byte[] Load(string endpoint)
        {
            TryLoadAuthoritative(endpoint, out var pin);
            return pin;
        }

        /// <inheritdoc/>
        public bool TryLoadAuthoritative(string endpoint, out byte[] pin)
        {
            pin = null;
            if (string.IsNullOrEmpty(endpoint)) return true;
            WarnDeviceBindingDegradedOnce();
            lock (_lock)
            {
                // Reads serve whatever authenticated, whether or not the rest
                // of the file did: a record carries its own HMAC, so one that
                // verifies is trustworthy beside a neighbour that does not.
                //
                // A file that is out of reach is the one reading that says
                // nothing about the endpoint.  Save already refuses to write
                // over it; the same fact has to reach the pinning decision,
                // because an unpinned answer there is what turns a locked file
                // into a fresh trust-on-first-use capture.
                var reading = ReadAllRecords(out var records);
                if (reading == PinFileReading.Unavailable)
                {
                    WarnPinsUnreachableOnce();
                    return false;
                }

                if (records.TryGetValue(endpoint, out var stored))
                    pin = (byte[])stored.Clone();
                return true;
            }
        }

        // One line per store: a pin file held open stays that way for as long
        // as whatever holds it runs, and every connect in that window arrives
        // here.  The path is the live one rather than the default name, because
        // an operator told to inspect a file needs the one that failed.
        private void WarnPinsUnreachableOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _unreachableWarned, 1) != 0) return;
            Debug.LogWarning(
                "[RTMPE] EncryptedFilePinStore: " + _filePath + " exists and could not be read " +
                "— another process may hold it open, or its permissions may have changed. " +
                "Pinned endpoints are refused rather than captured again while that lasts: " +
                "recapturing would trust whatever key the network delivers, over a pin the " +
                "file still holds.");
        }

        private int _unreachableWarned;

        public void Save(string endpoint, byte[] pin)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            if (pin == null || pin.Length != PinLength) return;
            var endpointBytes = Encoding.UTF8.GetBytes(endpoint);
            if (endpointBytes.Length > MaxEndpointLength) return;
            WarnDeviceBindingDegradedOnce();

            lock (_lock)
            {
                // A rewrite states the whole file, so what it may state depends
                // on what the read could account for.  A file that is merely
                // out of reach — locked, permissions revoked — holds pins that
                // are intact and readable later, and publishing one record over
                // them would downgrade every other endpoint to a fresh
                // trust-on-first-use capture for no lasting reason: that is the
                // case the refusal below exists for.  A file whose bytes cannot
                // be understood is the opposite: those pins have already
                // stopped verifying, nothing here can recover them, and
                // refusing forever would trade them for having no pinning at
                // all.  So it proceeds, carrying forward what did verify, and
                // says what it dropped.
                var reading = ReadAllRecords(out var records);
                RefuseRewriteOfUnreadFile(reading, nameof(Save));
                if (reading == PinFileReading.Unintelligible) WarnRecordsDroppedOnce(records.Count);

                // TOFU pin replacement detection mirrors PlayerPrefsPinStore:
                // surface every overwrite as a structured warning so ops have
                // a forensic trail of when (and which endpoint) the pin
                // rotated under them — silently accepting a swap would hide
                // the dominant detection signal for an attacker who already
                // has file-write access.
                if (records.TryGetValue(endpoint, out var existing)
                    && !BytesEqual(existing, pin))
                {
                    Debug.LogWarning(
                        $"[RTMPE] EncryptedFilePinStore: replacing existing pin for endpoint {endpoint}. " +
                        "This is expected after an operator-driven key rotation; an unexpected change " +
                        "indicates either a re-installation or unauthorised modification of the pin " +
                        "file.  Verify the new pin against the server's published key out of band.");
                }

                var stored = new byte[PinLength];
                Buffer.BlockCopy(pin, 0, stored, 0, PinLength);
                records[endpoint] = stored;
                WriteAllRecordsAtomic(records);
            }
        }

        public void Clear(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            lock (_lock)
            {
                // Same rule as Save, and for the same reason: removing one
                // endpoint rewrites the file that holds all of them.
                //
                // Asked before the removal, because a file that could not be
                // read cannot say whether the endpoint is in it: answering
                // "nothing to do" there would report a rotation as complete
                // while the old pin is still in force.
                var reading = ReadAllRecords(out var records);
                RefuseRewriteOfUnreadFile(reading, nameof(Clear));
                if (!records.Remove(endpoint)) return;
                if (reading == PinFileReading.Unintelligible) WarnRecordsDroppedOnce(records.Count);
                WriteAllRecordsAtomic(records);
            }
        }

        // ── Key derivation ────────────────────────────────────────────────

        private static byte[] DeriveMacKey(string deviceId)
        {
            // HKDF-SHA256(IKM=device_id, salt=HkdfSalt, info=HkdfInfo) → 32 B.
            // A deviceId that names no device is permitted (RFC 5869 admits
            // empty IKM) — the resulting MAC key is still well-defined and
            // stable across launches on the same device.  What it is not is
            // secret: every install on such a platform derives the same key,
            // which is why IsUnusableDeviceId exists to disclose the condition.
            // The consequences are enumerated under LIMITATIONS at the class
            // header; they are larger than a lost transplant check.
            var ikm = Encoding.UTF8.GetBytes(deviceId);
            var prk = HkdfSha256.Extract(HkdfSalt, ikm);
            try
            {
                return HkdfSha256.Expand(prk, HkdfInfo, 32);
            }
            finally
            {
                Array.Clear(prk, 0, prk.Length);
            }
        }

        // ── File IO ───────────────────────────────────────────────────────

        /// <summary>
        /// How much of the pin file a read accounted for, and — where it did
        /// not — whether another attempt could do better.  That second question
        /// is what decides a rewrite: bytes that are merely out of reach must
        /// be preserved, while bytes this installation can never authenticate
        /// again are already worthless and must not hold the store hostage.
        /// </summary>
        private enum PinFileReading
        {
            /// <summary>No file, or an empty one: nothing exists to be lost.</summary>
            Nothing,

            /// <summary>Every byte accounted for and every record authenticated.</summary>
            Whole,

            /// <summary>
            /// The file exists and could not be opened or read — locked by
            /// another process, permissions revoked, larger than this store
            /// will load.  Its contents are intact and a later attempt may well
            /// succeed, so nothing may overwrite them.
            /// </summary>
            Unavailable,

            /// <summary>
            /// The file was read and part of it could not be understood — a
            /// header that is not ours, a truncated tail, records whose HMAC
            /// this device cannot reproduce.  Those bytes are unreadable to
            /// this installation permanently: <c>Load</c> already returns
            /// nothing for them, and refusing to move past them would leave the
            /// endpoint with no pinning at all rather than with none for the
            /// records that are gone.  What authenticated is carried forward.
            /// </summary>
            Unintelligible,
        }

        // Decodes the file into <paramref name="records"/> and reports how much
        // of it that reading covers.  Per-record HMAC mismatches drop the
        // offending entry but preserve the rest, so a tampered record cannot
        // poison unrelated endpoints — and the reading says so, so a caller
        // about to rewrite the file can tell "there is nothing here" from
        // "there is something here I cannot read".
        private PinFileReading ReadAllRecords(out Dictionary<string, byte[]> records)
        {
            records = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            byte[] bytes;
            try
            {
                // Absence is established by opening, not by File.Exists.  That
                // predicate answers false for a path it could not probe just as
                // it does for one that is not there — a directory whose
                // permissions changed, a container that moved, an unmounted
                // root — and reading "there is no pin file" from a directory
                // this process cannot see is the same fail-open one level up.
                // The runtime distinguishes not-found from not-permitted, so
                // the classification is taken from the exception.
                using (var stream = new FileStream(
                           _filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = stream.Length;
                    if (length == 0) return PinFileReading.Nothing;
                    // Past the cap the file is not read at all, and re-appending
                    // past it is something anyone with write access can repeat —
                    // so this is malformed by our own rule rather than temporarily
                    // out of reach, and must not become a permanent refusal.
                    if (length > MaxFileBytes) return PinFileReading.Unintelligible;

                    bytes = new byte[length];
                    int filled = 0;
                    while (filled < bytes.Length)
                    {
                        int n = stream.Read(bytes, filled, bytes.Length - filled);
                        // Short of the length the handle reported: the file is
                        // being written under this read, not understood wrongly.
                        if (n <= 0) return PinFileReading.Unavailable;
                        filled += n;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return PinFileReading.Nothing;
            }
            catch (DirectoryNotFoundException)
            {
                return PinFileReading.Nothing;
            }
            catch (UnauthorizedAccessException)
            {
                return PinFileReading.Unavailable;
            }
            catch (IOException)
            {
                return PinFileReading.Unavailable;
            }

            return DecodeRecords(bytes, records);
        }

        // Refuses a rewrite over a file whose contents are intact but out of
        // reach.  Thrown rather than swallowed because MigratingPinStore
        // distinguishes a durable write from a failed one solely by whether the
        // call throws — reporting this as success would clear the legacy
        // fallback pin on the strength of a write that lost the primary ones.
        //
        // Only Unavailable refuses.  An unintelligible file is one whose
        // records this installation can no longer authenticate, and holding the
        // store closed over them would trade a set of pins that are already
        // unreadable for having no pinning at all — permanently, since nothing
        // makes a changed device identifier or a corrupt header come back.
        private void RefuseRewriteOfUnreadFile(PinFileReading reading, string operation)
        {
            if (reading != PinFileReading.Unavailable) return;
            throw new IOException(
                $"[RTMPE] EncryptedFilePinStore.{operation}: {DefaultFileName} exists but could not " +
                "be read — it is locked by another process, or its permissions no longer admit " +
                "this one. The pins it holds are intact and rewriting it now would replace all " +
                "of them with one, so the write was refused. Retry once the file is readable.");
        }

        // Says once per process that records were dropped.  A rewrite over an
        // unintelligible file is a real loss of pinning for the endpoints in
        // it, even though every one of them had already stopped verifying —
        // the next connect to each captures on first use again, so it is the
        // moment a key change would go unnoticed.
        private void WarnRecordsDroppedOnce(int carriedForward)
        {
            if (System.Threading.Interlocked.Exchange(ref _dropWarned, 1) != 0) return;
            Debug.LogWarning(
                $"[RTMPE] EncryptedFilePinStore: {DefaultFileName} could not be understood — a " +
                "corrupt header, a truncated tail, or records written under a different device " +
                "identifier. Those pins were already unverifiable and are not carried forward" +
                (carriedForward > 0 ? $" ({carriedForward} that still verify are)" : "") +
                ". Affected endpoints capture on first use again on their next connect.");
        }

        private int _dropWarned;

        // Atomic write: stage into a sibling .tmp file, then atomically swap
        // it over the live file.  A process crash mid-write loses the
        // staging file but leaves the previous live file untouched, so the
        // pin store can never end up in a half-written state.
        //
        // The staged bytes are forced to the device before the swap.  Closing a
        // handle hands the content to the page cache and no further; a rename
        // recorded ahead of that writeback survives a power loss over content
        // that does not, and the file that comes back is the new name over
        // nothing — every pin gone, and every endpoint silently back to
        // capturing on first use.  Same discipline as the tooling writers.
        //
        // ⚠️ This makes the CONTENT durable, not the directory entry: .NET
        // exposes no portable directory fsync, so a power loss between the
        // rename and the directory's own writeback can still lose the rename —
        // leaving the PREVIOUS file intact, which is the outcome this shape
        // exists to guarantee.
        private void WriteAllRecordsAtomic(Dictionary<string, byte[]> records)
        {
            var bytes  = EncodeRecords(records);
            var dir    = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmpPath = _filePath + ".tmp";
            try
            {
                using (var stream = new FileStream(
                           tmpPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(_filePath))
                {
                    // File.Replace handles the rename + replace as one
                    // operation on POSIX and uses MoveFileEx on Windows;
                    // both provide crash-safe semantics.
                    File.Replace(tmpPath, _filePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmpPath, _filePath);
                }
            }
            catch (IOException)
            {
                // Clean up the staging file so a failed write leaves no
                // orphaned bytes behind, then propagate.  Callers — notably
                // MigratingPinStore — distinguish a durable write from a failed
                // one solely by whether Save throws: swallowing the failure
                // here would report a non-durable write as success, scrubbing
                // the legacy fallback pin and silently downgrading pinning to a
                // fresh trust-on-first-use capture on the next connect.
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch (IOException) { }
                throw;
            }
        }

        // ── Encode / decode ───────────────────────────────────────────────

        private byte[] EncodeRecords(Dictionary<string, byte[]> records)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(MagicHeader);
            bw.Write(FormatVersion);

            // Written after the loop: the filters below can reject entries, and
            // a count taken before them describes more records than the file
            // holds — which the reader now classifies as unintelligible and
            // carries none of forward.
            long countPosition = ms.Position;
            bw.Write((uint)0);
            uint written = 0;

            foreach (var kv in records)
            {
                var endpointBytes = Encoding.UTF8.GetBytes(kv.Key);
                if (endpointBytes.Length > MaxEndpointLength) continue;
                if (kv.Value == null || kv.Value.Length != PinLength) continue;
                written++;

                bw.Write((ushort)endpointBytes.Length);
                bw.Write(endpointBytes);
                bw.Write(kv.Value);

                var hmac = ComputeRecordHmac(endpointBytes, kv.Value);
                bw.Write(hmac);
                Array.Clear(hmac, 0, hmac.Length);
            }

            bw.Flush();
            var bytes = ms.ToArray();

            // Patch the count in place now that it is known.
            bytes[countPosition + 0] = (byte)(written & 0xff);
            bytes[countPosition + 1] = (byte)((written >> 8) & 0xff);
            bytes[countPosition + 2] = (byte)((written >> 16) & 0xff);
            bytes[countPosition + 3] = (byte)((written >> 24) & 0xff);
            return bytes;
        }

        private PinFileReading DecodeRecords(byte[] bytes, Dictionary<string, byte[]> records)
        {
            // Defensive: every length check below treats truncation /
            // malformed input as a soft failure that keeps the records
            // validated up to the failure point and reports the reading as
            // partial.  Throwing on a malformed file would break the SDK's
            // connect path on an otherwise-recoverable corruption (disk error,
            // half-written backup, etc.) — a read handles "no pin" gracefully
            // via TOFU re-capture, and only a rewrite has to refuse.
            const int HeaderLength = 9;  // 4 magic + 1 version + 4 count

            if (bytes.Length < HeaderLength) return PinFileReading.Unintelligible;

            using var ms = new MemoryStream(bytes, writable: false);
            using var br = new BinaryReader(ms);
            bool unauthenticated = false;

            try
            {
                uint magic = br.ReadUInt32();
                if (magic != MagicHeader) return PinFileReading.Unintelligible;
                byte version = br.ReadByte();
                if (version != FormatVersion) return PinFileReading.Unintelligible;
                uint count = br.ReadUInt32();
                // Sanity-cap the count against the file size so a corrupt
                // record_count cannot drive an outsized loop.
                if (count > (uint)(bytes.Length / (2 + PinLength + HmacLength)))
                    return PinFileReading.Unintelligible;

                for (uint i = 0; i < count; i++)
                {
                    if (ms.Position + 2 > ms.Length) return PinFileReading.Unintelligible;
                    ushort endpointLen = br.ReadUInt16();
                    if (endpointLen == 0 || endpointLen > MaxEndpointLength) return PinFileReading.Unintelligible;

                    if (ms.Position + endpointLen + PinLength + HmacLength > ms.Length) return PinFileReading.Unintelligible;
                    var endpointBytes = br.ReadBytes(endpointLen);
                    var pin           = br.ReadBytes(PinLength);
                    var storedHmac    = br.ReadBytes(HmacLength);

                    var expectedHmac = ComputeRecordHmac(endpointBytes, pin);
                    bool ok = BytesEqual(storedHmac, expectedHmac);
                    Array.Clear(expectedHmac, 0, expectedHmac.Length);

                    if (!ok)
                    {
                        // Drop the tampered / forged record but keep decoding
                        // subsequent ones — a single bitflip should not nullify
                        // the whole store.  The reading is no longer whole,
                        // though: these bytes hold a pin for some endpoint, and
                        // this device cannot say which or whose.
                        unauthenticated = true;
                        continue;
                    }

                    var endpoint = Encoding.UTF8.GetString(endpointBytes);
                    records[endpoint] = pin;
                }
            }
            catch (EndOfStreamException)
            {
                // Truncated tail — keep what was validated and say so.
                //
                // Defence in depth rather than a live path: every read above
                // that can throw at end of stream (ReadUInt32 / ReadByte /
                // ReadUInt16) is preceded by an explicit length check, and
                // ReadBytes returns a short array instead of throwing.  Measured,
                // not assumed.  The handler stays because it is the length
                // checks that make it unreachable, and a future edit to one of
                // them should degrade to a refused rewrite rather than an
                // exception out of Load.
                return PinFileReading.Unintelligible;
            }

            // Reaching the declared count is not reaching the end of the file.
            // A record_count corrupted DOWNWARD leaves fully-formed records
            // beyond the loop — bytes that would authenticate perfectly and
            // that a rewrite would drop without ever having looked at them.
            if (ms.Position != ms.Length) return PinFileReading.Unintelligible;

            return unauthenticated ? PinFileReading.Unintelligible : PinFileReading.Whole;
        }

        // ── HMAC + constant-time compare ──────────────────────────────────

        private byte[] ComputeRecordHmac(byte[] endpointBytes, byte[] pin)
        {
            // Canonical HMAC input:
            //   [version : 1B]
            //   [endpoint_len : 2B LE]
            //   [endpoint    : N B]
            //   [pin         : 32B]
            // Including the format version blocks downgrade attacks where a
            // future v2 record is reformatted as a v1 one with the same
            // bytes; the endpoint length prefix blocks splice attacks that
            // shift endpoint bytes across the endpoint↔pin boundary.
            var input = new byte[1 + 2 + endpointBytes.Length + PinLength];
            int off = 0;
            input[off++] = FormatVersion;
            input[off++] = (byte)(endpointBytes.Length & 0xff);
            input[off++] = (byte)((endpointBytes.Length >> 8) & 0xff);
            Buffer.BlockCopy(endpointBytes, 0, input, off, endpointBytes.Length);
            off += endpointBytes.Length;
            Buffer.BlockCopy(pin, 0, input, off, PinLength);

            using var hmac = new HMACSHA256(_macKey);
            var tag = hmac.ComputeHash(input);
            Array.Clear(input, 0, input.Length);
            return tag;
        }

        // Constant-time equality.  HMAC verification compares
        // attacker-controlled bytes to a derived value; an early-exit
        // comparison would leak the matched-prefix length as a timing
        // side-channel.  Always walks the full buffer.
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
