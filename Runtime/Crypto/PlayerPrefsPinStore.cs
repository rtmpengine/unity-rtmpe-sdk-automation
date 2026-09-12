// RTMPE SDK — Runtime/Crypto/PlayerPrefsPinStore.cs
//
// Default IServerKeyPinStore backed by UnityEngine.PlayerPrefs.
//
// Each pin is stored as a 64-character lowercase hex string under the key
// "RTMPE.ServerPin.<host>:<port>".  PlayerPrefs is platform-appropriate
// (Windows registry, macOS plist, Android SharedPreferences, iOS user
// defaults, WebGL IndexedDB) and is the only cross-platform persistent
// store Unity exposes without additional packages.
//
// Pins are NOT confidential — they are the server's PUBLIC key — so the
// modest tamper resistance of PlayerPrefs is acceptable.  An attacker who
// can write to PlayerPrefs can also write to the application binary, which
// is a strictly broader compromise.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Crypto
{
    // Storage backend abstraction.  PlayerPrefs is a static Unity API and
    // therefore cannot be exercised under xUnit without an Editor process;
    // the interface lets us substitute an in-memory implementation in unit
    // tests while production builds keep the zero-overhead PlayerPrefs path.
    // Internal so the SDK's public surface (constructor / interface) is
    // unchanged for game code.
    internal interface IPersistentKeyValueStore
    {
        string GetString(string key, string defaultValue);
        void   SetString(string key, string value);
        void   DeleteKey(string key);
        void   Save();
    }

    // Default backend forwarding to UnityEngine.PlayerPrefs.  Kept private
    // to PlayerPrefsPinStore so the abstraction is invisible to callers.
    internal sealed class UnityPlayerPrefsStore : IPersistentKeyValueStore
    {
        public string GetString(string key, string defaultValue) => PlayerPrefs.GetString(key, defaultValue);
        public void   SetString(string key, string value)        => PlayerPrefs.SetString(key, value);
        public void   DeleteKey(string key)                      => PlayerPrefs.DeleteKey(key);
        public void   Save()                                     => PlayerPrefs.Save();
    }

    /// <summary>
    /// Default <see cref="IServerKeyPinStore"/> implementation that persists
    /// pins via <see cref="UnityEngine.PlayerPrefs"/>.  Used by the SDK when
    /// no custom store has been injected.
    /// </summary>
    public sealed class PlayerPrefsPinStore : IServerKeyPinStore, IPinStoreAvailability
    {
        // Namespaced prefix avoids accidental collision with game-app keys.
        // Changing this prefix would invalidate every pin in existing
        // installs — treat it as a wire-format constant.
        internal const string KeyPrefix = "RTMPE.ServerPin.";

        private readonly IPersistentKeyValueStore _backend;

        // Endpoints already reported, keyed by endpoint AND by which of the two
        // conditions was reported.  One key per endpoint let whichever fired
        // first silence the other for the life of the process, and the two say
        // opposite things: one that the pin is being kept and the connection
        // refused, the other that the pin is gone and the next connection will
        // capture a replacement.  Suppressing the second behind the first is
        // suppressing the only warning that precedes a trust reset.
        //
        // Guarded because it is the only mutable state on a type that had none.
        // The interface asks implementations for main-thread safety and no
        // more, but this class is public, the property that hands it out is
        // public, and an unsynchronised set does not merely lose a warning
        // under concurrent use — it throws out of Load, which is the one thing
        // the read path below exists to prevent.
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _reportLock = new object();

        /// <summary>
        /// Construct the default Unity-backed pin store.  Production callers
        /// use this — pins are persisted to platform-appropriate storage via
        /// <see cref="UnityEngine.PlayerPrefs"/>.
        /// </summary>
        public PlayerPrefsPinStore() : this(new UnityPlayerPrefsStore()) { }

        // Test seam.  An in-memory <see cref="IPersistentKeyValueStore"/>
        // exercises the storage path without an active Unity Editor process,
        // which the static PlayerPrefs API requires.  Internal so external
        // callers cannot accidentally bypass the platform-appropriate
        // backend on a shipped build.
        internal PlayerPrefsPinStore(IPersistentKeyValueStore backend)
        {
            _backend = backend ?? new UnityPlayerPrefsStore();
        }

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

            string hex;
            try
            {
                hex = _backend.GetString(KeyPrefix + endpoint, string.Empty);
            }
            catch (Exception ex)
            {
                // The backing store is out of reach rather than empty.  A read
                // that did not happen describes this attempt and says nothing
                // about the endpoint, so the pinning decision is told that
                // rather than being handed a pin-shaped absence.
                //
                // It also stops here.  Before this, a throwing backend left
                // the exception to unwind through the composite and the
                // pinning decision into the handshake, which no arm on that
                // path is written for.  The one caller that does read an
                // exception from a store — the write-then-read-back in
                // ServerKeyPinning.PersistFirstUse — is told about an
                // unreadable store through the return value instead, so it
                // still reports the failure it actually had.
                WarnUnreadableOnce(
                    endpoint,
                    "the preferences backend refused the read (" + ex.GetType().Name + ")",
                    recoverable: true);
                return false;
            }

            // Nothing was ever written under this key.  That is a fact about
            // the endpoint, and the only reading here that licenses a first-use
            // capture.
            if (string.IsNullOrEmpty(hex)) return true;

            try
            {
                pin = KeyHex.Decode32(hex);
                return true;
            }
            catch (Exception)
            {
                // A value is present and this build cannot make sense of it.
                // That is the case the file-backed store settles the other way
                // from an unreachable file, and for the reason it records: a
                // value that no longer decodes protects nothing, nothing here
                // can recover it, and refusing on it would trade a pin that is
                // already lost for an endpoint that can never be reached again.
                // So the answer stays authoritative and the loss is stated,
                // because a first-use capture is about to replace a pin the
                // operator believes is still in force.
                pin = null;
                WarnUnreadableOnce(
                    endpoint,
                    "the stored value is not a 64-character hex pin",
                    recoverable: false);
                return true;
            }
        }

        // One line per endpoint per condition.  Every connect attempt arrives
        // here and the condition lasts until someone clears the entry, so an
        // ungated line would repeat for the life of the process.  The key is
        // built from the endpoints the game itself connects to and never from
        // anything that arrives over the network.
        private void WarnUnreadableOnce(string endpoint, string reason, bool recoverable)
        {
            lock (_reportLock)
            {
                if (!_reported.Add((recoverable ? "r:" : "u:") + endpoint)) return;
            }
            Debug.LogWarning(
                $"[RTMPE] PlayerPrefsPinStore: the pin for endpoint {endpoint} could not be " +
                $"read — {reason}.  " +
                (recoverable
                    ? "Pinned endpoints are refused rather than captured again while that " +
                      "lasts: recapturing would trust whatever key the network delivers, over " +
                      "a pin the store still holds."
                    : "That pin is unrecoverable, so this endpoint now reads as unpinned: " +
                      "unless a pin is configured, trust-on-first-use captures the key the " +
                      "next connection offers and persists it in place of the one this " +
                      "endpoint was provisioned with.  Verify the replacement against the " +
                      "server's published key out of band."));
        }

        public void Save(string endpoint, byte[] pin)
        {
            if (string.IsNullOrEmpty(endpoint) || pin == null || pin.Length != 32) return;

            // Hex-encode for human inspection in the OS-level prefs store.
            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < 32; i++) sb.Append(pin[i].ToString("x2"));
            string nextHex = sb.ToString();

            // Detect pin replacement against the existing entry.  TOFU policy
            // captures the pin on the very first connect, so any subsequent
            // change is either a legitimate operator rotation (a fresh key
            // pair was deployed) or a tampering attempt (an attacker with
            // PlayerPrefs write access swapped in a key they control).  The
            // SDK has no out-of-band signal to distinguish those two cases,
            // but surfacing the transition via a structured warning gives
            // ops a forensics trail that a silent overwrite would not.
            string priorHex = _backend.GetString(KeyPrefix + endpoint, string.Empty);
            if (!string.IsNullOrEmpty(priorHex) &&
                !string.Equals(priorHex, nextHex, System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning(
                    $"[RTMPE] PlayerPrefsPinStore: replacing existing pin for endpoint {endpoint}. " +
                    "This is expected after an operator-driven key rotation; an unexpected change " +
                    "indicates either a re-installation or unauthorised modification of local " +
                    "preferences.  Verify the new pin against the server's published key out of band.");
            }

            _backend.SetString(KeyPrefix + endpoint, nextHex);
            // Save() is required on iOS/Android to flush before app suspend;
            // skipping it would lose the pin if the OS killed the process
            // immediately after the first handshake.
            _backend.Save();
        }

        public void Clear(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            _backend.DeleteKey(KeyPrefix + endpoint);
            _backend.Save();
        }
    }
}
