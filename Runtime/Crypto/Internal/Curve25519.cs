// RTMPE SDK — Runtime/Crypto/Internal/Curve25519.cs
//
// X25519 Diffie-Hellman function (RFC 7748 §5).
//
// The Montgomery ladder of RFC 7748 §5 over Field25519's limb arithmetic,
// following TweetNaCl's crypto_scalarmult (public domain; see Field25519.cs).
//
// Key derivation:
//   GenerateKeyPair()  → (privateKey[32], publicKey[32])
//   SharedSecret(myPrivate, peerPublic) → sharedSecret[32]
//
// ============================================================================
// SECURITY / THREAT MODEL
// ============================================================================
// This is a PURE-MANAGED C# cryptographic implementation.
//
// WHAT IT PROTECTS AGAINST (in scope):
//   • Passive key agreement eavesdroppers: the Montgomery ladder DH prevents
//     a network observer from deriving the shared secret.
//   • Key substitution attacks: Ed25519 signature on the server ephemeral key
//     prevents man-in-the-middle key replacement before ECDH proceeds.
//   • Timing of the scalar: the ladder runs the same instructions for every
//     scalar bit — the swap is a mask, not a branch — and the field arithmetic
//     neither branches nor indexes on a value.  The BigInteger implementation
//     this replaced (audit P1-L33) did both, and allocated on every operation:
//     about a hundred thousand short-lived objects per connection, on the main
//     thread.
//
// WHAT IT DOES NOT PROTECT AGAINST (out of scope):
//   • What the managed runtime itself does: a JIT or IL2CPP is free to compile
//     a mask into a branch, and nothing in managed C# can forbid it.  The
//     scalar is ephemeral — fresh per handshake — so no attacker accumulates
//     samples across uses of one key.
//   • Small-subgroup attacks are not this function's to refuse: the clamp
//     (RFC 7748 §5) makes them harmless, and SharedSecret refuses the all-zero
//     output a low-order point produces (RFC 7748 §6.1).
//
// TESTING:
//   RFC 7748 §5.2 and §6.1 vectors, the iterated vector, and agreement with the
//   BigInteger implementation it replaced on random and edge inputs.
// ============================================================================

using System;
using System.Security.Cryptography;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// X25519 ephemeral key pair generation and Diffie-Hellman shared-secret computation.
    /// All operations are in GF(2^255-19) (<see cref="Field25519"/>).
    /// </summary>
    internal static class Curve25519
    {
        // a24 = (486662 - 2) / 4 = 121665 — the ladder's constant.
        private static readonly long[] A24 = Field25519.FromInt(121665);

        // ── Montgomery ladder (RFC 7748 §5) ──────────────────────────────────

        /// <summary>
        /// Compute the X25519 function: multiply the u-coordinate <paramref name="u_bytes"/>
        /// by the scalar <paramref name="k_bytes"/>.
        /// Both arrays must be exactly 32 bytes (little-endian).
        /// </summary>
        /// <remarks>
        /// The scalar is clamped (RFC 7748 §5) on a copy, and the u-coordinate's
        /// top bit is ignored; a u at or above p is read as itself and reduced,
        /// which RFC 7748 §5 requires.
        /// </remarks>
        internal static byte[] ScalarMult(byte[] k_bytes, byte[] u_bytes)
        {
            // Refused as the BigInteger implementation's copy refused them, rather
            // than as an index past the end of the u-coordinate.
            if (k_bytes == null) throw new ArgumentNullException(nameof(k_bytes));
            if (u_bytes == null) throw new ArgumentNullException(nameof(u_bytes));
            if (k_bytes.Length < 32 || u_bytes.Length < 32)
                throw new ArgumentException("X25519 takes a 32-byte scalar and a 32-byte u-coordinate.");

            var z = new byte[32];
            Buffer.BlockCopy(k_bytes, 0, z, 0, 32);
            z[0]  &= 248;   // clear bits 0, 1, 2
            z[31] &= 127;   // clear bit 255
            z[31] |= 64;    // set bit 254

            var x = new long[Field25519.Limbs];
            var a = new long[Field25519.Limbs];
            var b = new long[Field25519.Limbs];
            var c = new long[Field25519.Limbs];
            var d = new long[Field25519.Limbs];
            var e = new long[Field25519.Limbs];
            var f = new long[Field25519.Limbs];
            try
            {
                Field25519.Unpack(x, u_bytes);
                // (x2, z2) = (1, 0) in a, c; (x3, z3) = (u, 1) in b, d.
                Field25519.Set(b, x);
                a[0] = 1;
                d[0] = 1;

                for (int t = 254; t >= 0; t--)
                {
                    long bit = (z[t >> 3] >> (t & 7)) & 1;
                    Field25519.Swap(a, b, bit);
                    Field25519.Swap(c, d, bit);
                    Field25519.Add(e, a, c);
                    Field25519.Sub(a, a, c);
                    Field25519.Add(c, b, d);
                    Field25519.Sub(b, b, d);
                    Field25519.Square(d, e);
                    Field25519.Square(f, a);
                    Field25519.Mul(a, c, a);
                    Field25519.Mul(c, b, e);
                    Field25519.Add(e, a, c);
                    Field25519.Sub(a, a, c);
                    Field25519.Square(b, a);
                    Field25519.Sub(c, d, f);
                    Field25519.Mul(a, c, A24);
                    Field25519.Add(a, a, d);
                    Field25519.Mul(c, c, a);
                    Field25519.Mul(a, d, f);
                    Field25519.Mul(d, b, x);
                    Field25519.Square(b, e);
                    Field25519.Swap(a, b, bit);
                    Field25519.Swap(c, d, bit);
                }

                // x2 / z2
                Field25519.Invert(c, c);
                Field25519.Mul(a, a, c);
                var output = new byte[32];
                Field25519.Pack(output, a);
                return output;
            }
            finally
            {
                // The clamped scalar and every intermediate of the ladder are
                // derived from the private key: wiped, which the immutable
                // BigInteger values this replaced could not be.
                Array.Clear(z, 0, z.Length);
                Array.Clear(x, 0, x.Length);
                Array.Clear(a, 0, a.Length);
                Array.Clear(b, 0, b.Length);
                Array.Clear(c, 0, c.Length);
                Array.Clear(d, 0, d.Length);
                Array.Clear(e, 0, e.Length);
                Array.Clear(f, 0, f.Length);
            }
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Generate an X25519 ephemeral key pair using the system CSPRNG.
        /// Returns (privateKey[32], publicKey[32]).
        /// </summary>
        internal static (byte[] privateKey, byte[] publicKey) GenerateKeyPair()
        {
            var privateKey = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(privateKey);

            // RFC 7748 §5: the X25519 secret scalar is clamped before any
            // scalar multiplication. Applying the clamp to the stored bytes
            // keeps the returned private key in its canonical form — ScalarMult
            // clamps a working copy regardless, so the public key and every
            // derived shared secret are identical either way, but a caller that
            // inspects or persists the private scalar observes the value the
            // curve actually uses.
            privateKey[0]  &= 248;
            privateKey[31] &= 127;
            privateKey[31] |= 64;

            // Public key = ScalarMult(private, base_point_u = 9).
            var basePoint = new byte[32];
            basePoint[0] = 9;
            var publicKey = ScalarMult(privateKey, basePoint);
            return (privateKey, publicKey);
        }

        /// <summary>
        /// Compute the X25519 shared secret from this side's private key and the peer's public key.
        /// Returns 32 bytes of shared secret material.
        /// Returns null if the computed shared secret is the all-zero string (low-order point detected).
        /// </summary>
        /// <remarks>
        /// All-zero rejection (RFC 7748 §6.1, last paragraph): a peer that
        /// supplies any of the small-order u-coordinates listed in §7
        /// causes the Montgomery ladder to output the all-zero string. An
        /// attacker can use this to force a known shared secret and
        /// fingerprint the session keys. The check is performed with a
        /// constant-time accumulator so that the success/failure decision
        /// does not branch on the secret bytes (although the secret is
        /// already returned to the caller in the success path, so the
        /// only secret-dependent branch eliminated here is the early-exit
        /// in the previous implementation).
        /// </remarks>
        internal static byte[] SharedSecret(byte[] myPrivateKey, byte[] peerPublicKey)
        {
            var result = ScalarMult(myPrivateKey, peerPublicKey);

            // Constant-time all-zero check.
            int diff = 0;
            for (int i = 0; i < result.Length; i++) diff |= result[i];
            if (diff == 0)
            {
                // Wipe the (zero) buffer for hygiene and return null so the
                // caller aborts the handshake. Cf. RFC 7748 §6.1.
                Array.Clear(result, 0, result.Length);
                return null;
            }
            return result;
        }
    }
}
