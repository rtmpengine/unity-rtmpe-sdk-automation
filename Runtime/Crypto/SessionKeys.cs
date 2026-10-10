// RTMPE SDK — Runtime/Crypto/SessionKeys.cs
//
// Two directional symmetric keys derived from X25519 ECDH + HKDF-SHA256.
//
// Each session uses two independent
// ChaCha20-Poly1305 keys so that client→server and server→client traffic
// cannot be XOR-combined by a passive observer to reveal plaintext.
//
// The "initiator" is the side with the lexicographically smaller X25519
// public key. Both sides determine this autonomously—no extra signalling.
//
// SessionKeys implements IDisposable.  Calling Dispose() immediately zeroes
// both key arrays in-place, reducing the window in which key material can be
// recovered from a managed-heap dump or a cold-boot attack.
// Dispose() is called automatically by the using-statement in NetworkManager.

using System;

namespace RTMPE.Crypto
{
    /// <summary>
    /// The pair of 32-byte ChaCha20-Poly1305 keys for one session, derived from
    /// the handshake's X25519 shared secret with HKDF-SHA256.
    /// </summary>
    /// <remarks>
    /// <see cref="EncryptKey"/> seals outbound packets and
    /// <see cref="DecryptKey"/> opens inbound ones. Dispose the object when the
    /// session ends to zero both keys.
    /// </remarks>
    public sealed class SessionKeys : IDisposable
    {
        /// <summary>32-byte key for sealing packets sent by this side.</summary>
        public byte[] EncryptKey { get; }

        /// <summary>32-byte key for opening packets received from the peer.</summary>
        public byte[] DecryptKey { get; }

        private bool _disposed;

        internal SessionKeys(byte[] encryptKey, byte[] decryptKey)
        {
            EncryptKey = encryptKey;
            DecryptKey = decryptKey;
        }

        /// <summary>
        /// Zeroes both keys in place. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (EncryptKey != null) Array.Clear(EncryptKey, 0, EncryptKey.Length);
            if (DecryptKey != null) Array.Clear(DecryptKey, 0, DecryptKey.Length);
        }
    }
}
