// RTMPE SDK — Runtime/Crypto/KeyHex.cs
//
// Decoding for the 32-byte keys the SDK accepts as configuration: the
// gateway's sealed-box X25519 public key, its Ed25519 identity pin, and the
// pin persisted by the pin stores.  All three are 64 lowercase hex characters
// on the wire and in NetworkSettings, and all three are rejected identically
// when they are not.
//
// Deliberately not a cipher and not named for one: the class this replaced
// carried a symmetric API-key envelope, and a decoder that keeps that name
// invites the next reader to look for an encryption path that no longer
// exists.

using System;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Hex decoding for 32-byte key material configured as a string.
    /// </summary>
    public static class KeyHex
    {
        /// <summary>Length in bytes of every key this decoder accepts.</summary>
        public const int KeyLen = 32;

        /// <summary>
        /// Decode a 64-character hex string into a 32-byte array.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The string is null, is not exactly 64 characters, or contains a
        /// character that is not a hex digit.  Length and content are both
        /// rejected here so a mistyped key surfaces at configuration time
        /// rather than as an unanswered handshake.
        /// </exception>
        public static byte[] Decode32(string hex)
        {
            if (hex == null || hex.Length != KeyLen * 2)
                throw new ArgumentException(
                    $"Key hex string must be exactly {KeyLen * 2} characters.", nameof(hex));

            var key = new byte[KeyLen];
            for (int i = 0; i < KeyLen; i++)
            {
                key[i] = (byte)((HexNibble(hex[i * 2]) << 4) | HexNibble(hex[i * 2 + 1]));
            }
            return key;
        }

        private static byte HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return (byte)(c - '0');
            if (c >= 'a' && c <= 'f') return (byte)(c - 'a' + 10);
            if (c >= 'A' && c <= 'F') return (byte)(c - 'A' + 10);
            throw new ArgumentException($"Invalid hex character: '{c}'");
        }
    }
}
