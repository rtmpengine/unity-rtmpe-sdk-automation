// RTMPE SDK — Runtime/Crypto/Internal/Ed25519Verify.cs
//
// Ed25519 signature VERIFICATION (RFC 8032 §5.1.7).
//
// This implements verification only (not signing — the server signs, the client verifies).
// All arithmetic is in GF(2^255-19) (Field25519) or on the Edwards25519 group.
//
// Used during the ECDH handshake: verify sign(server_static_privkey, server_ephemeral_pub)
// before proceeding with ECDH, to prevent man-in-the-middle key substitution.
//
// Pure C# — no native dependencies. System.Security.Cryptography.SHA512 is used
// for the SHA-512 hash, which is available in .NET Standard 2.1.  The point
// arithmetic (the complete addition law on extended coordinates, point
// decoding, the reduction mod l) follows TweetNaCl (public domain; see
// Field25519.cs).
//
// ============================================================================
// SECURITY / THREAT MODEL
// ============================================================================
// This is a PURE-MANAGED C# cryptographic implementation.
//
// WHAT IT PROTECTS AGAINST (in scope):
//   • MITM key substitution: verifying the server's Ed25519 signature on the
//     ephemeral key ensures the server holds the known static private key.
//   • Signature forgery by network-level attackers without the private key.
//
// WHAT IT DOES NOT PROTECT AGAINST (out of scope):
//   • Timing: the double-and-add below adds only where a scalar bit is set, so
//     its time depends on S and k.  Both are PUBLIC — S travels in the
//     signature, k is a hash of public values — and no private key is held by
//     this code, so there is nothing for the timing to reveal.
//
// COST (audit P1-L33): the BigInteger implementation this replaced allocated
// on every field operation — tens of thousands of short-lived objects for one
// verify, on the main thread, inside the Challenge's frame.  Every temporary
// here is on the stack, and both scalar multiplications share one pass of
// doublings (Straus).
//
// STRICT-VERIFICATION POLICY:
//   • Public-key and R-point must be canonical (y < p, no encoding ambiguity).
//   • Public-key and R-point must NOT be in the order-8 torsion subgroup.
//   • S must be below the group order l.
//   These rules align this verifier with ed25519-dalek's verify_strict
//   (used by the Rust gateway), eliminating cross-implementation
//   malleability and small-subgroup attacks.
//
// TESTING:
//   RFC 8032 test vectors, signature mutation tests, the eight published
//   low-order Ed25519 points (Hamburg/Ladd Decaf §3), probes that satisfy the
//   equation so only the strict rule refuses them, and agreement with the
//   BigInteger implementation it replaced on random and edge inputs.
// ============================================================================

using System;
using System.Security.Cryptography;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// Ed25519 signature verification (RFC 8032 §5.1).
    /// </summary>
    internal static class Ed25519Verify
    {
        // ── Field / curve constants ─────────────────────────────────────────
        //
        // Derived from their definitions rather than written as limbs, so a
        // mistyped limb cannot be one of them.  Declared before the base point,
        // whose decoding reads them: static fields initialise in textual order.

        // d = -121665/121666 mod p (RFC 8032 §5.1).
        private static readonly long[] D = ComputeD();

        // 2d, which the addition law uses.
        private static readonly long[] D2 = ComputeD2();

        // sqrt(-1) mod p = 2^((p-1)/4).
        private static readonly long[] SqrtM1 = ComputeSqrtM1();

        // Group order l = 2^252 + 27742317777372353535851937790883648493,
        // little-endian, one byte per element.
        private static readonly long[] L =
        {
            0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58,
            0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0x10,
        };

        // The base point B (RFC 8032 §5.1): y = 4/5, x even — encoded 0x58
        // followed by thirty-one 0x66.
        private static readonly Point BasePoint = DecodeBasePoint();

        private static long[] ComputeD()
        {
            var d = Field25519.Create();
            var den = Field25519.FromInt(121666);
            Field25519.Invert(den, den);
            Field25519.Sub(d, d, Field25519.FromInt(121665));
            Field25519.Mul(d, d, den);
            return d;
        }

        private static long[] ComputeD2()
        {
            var d2 = Field25519.Create();
            Field25519.Add(d2, D, D);
            Field25519.Mul(d2, d2, Field25519.FromInt(1));   // reduced
            return d2;
        }

        private static long[] ComputeSqrtM1()
        {
            // 2^((p-1)/4) = (2^((p-5)/8))^2 · 2
            var two = Field25519.FromInt(2);
            var r = Field25519.Create();
            Field25519.Pow2523(r, two);
            Field25519.Square(r, r);
            Field25519.Mul(r, r, two);
            return r;
        }

        private static Point DecodeBasePoint()
        {
            var encoding = new byte[32];
            encoding[0] = 0x58;
            for (int i = 1; i < 32; i++) encoding[i] = 0x66;
            var b = new Point();
            if (!DecodePoint(encoding, 0, b))
                throw new InvalidOperationException("the Ed25519 base point does not decode");
            return b;
        }

        // ── Point representation ─────────────────────────────────────────────

        // Extended homogeneous coordinates (X, Y, Z, T) where x = X/Z, y = Y/Z, T = x*y.
        private sealed class Point
        {
            public readonly long[] X = Field25519.Create();
            public readonly long[] Y = Field25519.Create();
            public readonly long[] Z = Field25519.Create();
            public readonly long[] T = Field25519.Create();

            public static Point Identity()
            {
                var p = new Point();
                p.Y[0] = 1;
                p.Z[0] = 1;
                return p;
            }

            public Point Copy()
            {
                var p = new Point();
                Field25519.Set(p.X, X);
                Field25519.Set(p.Y, Y);
                Field25519.Set(p.Z, Z);
                Field25519.Set(p.T, T);
                return p;
            }
        }

        // ── Edwards25519 point operations ────────────────────────────────────

        /// <summary>
        /// <paramref name="p"/> += <paramref name="q"/>: the complete addition
        /// law on extended coordinates (RFC 8032 §5.1.4), which doubles when
        /// <paramref name="q"/> is <paramref name="p"/>.
        /// </summary>
        private static void PointAdd(Point p, Point q)
        {
            Span<long> a = stackalloc long[Field25519.Limbs];
            Span<long> b = stackalloc long[Field25519.Limbs];
            Span<long> c = stackalloc long[Field25519.Limbs];
            Span<long> d = stackalloc long[Field25519.Limbs];
            Span<long> t = stackalloc long[Field25519.Limbs];
            Span<long> e = stackalloc long[Field25519.Limbs];
            Span<long> f = stackalloc long[Field25519.Limbs];
            Span<long> g = stackalloc long[Field25519.Limbs];
            Span<long> h = stackalloc long[Field25519.Limbs];

            Field25519.Sub(a, p.Y, p.X);
            Field25519.Sub(t, q.Y, q.X);
            Field25519.Mul(a, a, t);
            Field25519.Add(b, p.X, p.Y);
            Field25519.Add(t, q.X, q.Y);
            Field25519.Mul(b, b, t);
            Field25519.Mul(c, p.T, q.T);
            Field25519.Mul(c, c, D2);
            Field25519.Mul(d, p.Z, q.Z);
            Field25519.Add(d, d, d);
            Field25519.Sub(e, b, a);
            Field25519.Sub(f, d, c);
            Field25519.Add(g, d, c);
            Field25519.Add(h, b, a);

            Field25519.Mul(p.X, e, f);
            Field25519.Mul(p.Y, h, g);
            Field25519.Mul(p.Z, g, f);
            Field25519.Mul(p.T, e, h);
        }

        /// <summary>−P: x and T change sign.</summary>
        private static void Negate(Point p)
        {
            Field25519.Sub(p.X, Field25519.Create(), p.X);
            Field25519.Sub(p.T, Field25519.Create(), p.T);
        }

        // ── Point encoding / decoding ─────────────────────────────────────────

        /// <summary>
        /// Decode a compressed 32-byte Ed25519 point per RFC 8032 §5.1.3.
        /// Returns false if the encoding is invalid.
        /// </summary>
        /// <remarks>
        /// Strict canonical-encoding policy (RFC 8032 §5.1.7 + §8.4):
        ///   • The recovered <c>y</c> coordinate must satisfy <c>0 ≤ y &lt; p</c>.
        ///     A peer that emits <c>y ≥ p</c> is using a non-canonical encoding;
        ///     two distinct byte representations would map to the same point and
        ///     enable cross-implementation malleability with strict verifiers
        ///     (notably ed25519-dalek's "strict" mode used by the Rust gateway).
        ///   • If <c>x = 0</c> and the sign bit is 1 the encoding is rejected.
        ///     RFC 8032 §5.1.3 step 3 forbids this combination.
        /// </remarks>
        private static bool DecodePoint(byte[] s, int offset, Point pt)
        {
            Span<byte> buf = stackalloc byte[32];
            new ReadOnlySpan<byte>(s, offset, 32).CopyTo(buf);
            int xSign = (buf[31] >> 7) & 1;
            buf[31] &= 0x7F;   // the sign bit is not part of y

            Field25519.Unpack(pt.Y, buf);

            // Reject non-canonical encodings — the only canonical representation
            // of a y-coordinate is the unique 0 ≤ y < p value (RFC 8032 §5.1.3).
            // Packing reduces mod p, so it reproduces the bytes iff y < p.
            Span<byte> canonical = stackalloc byte[32];
            Field25519.Pack(canonical, pt.Y);
            if (!canonical.SequenceEqual(buf)) return false;

            // x^2 = u / v with u = y^2 - 1, v = d*y^2 + 1; the candidate root
            // x = u v^3 (u v^7)^((p-5)/8) is (u/v)^((p+3)/8) (RFC 8032 §5.1.3).
            Span<long> u   = stackalloc long[Field25519.Limbs];
            Span<long> v   = stackalloc long[Field25519.Limbs];
            Span<long> v3  = stackalloc long[Field25519.Limbs];
            Span<long> w   = stackalloc long[Field25519.Limbs];
            Span<long> chk = stackalloc long[Field25519.Limbs];
            Span<long> one = stackalloc long[Field25519.Limbs];
            one[0] = 1;

            Field25519.Square(u, pt.Y);
            Field25519.Mul(v, u, D);
            Field25519.Sub(u, u, one);
            Field25519.Add(v, v, one);

            Field25519.Square(v3, v);
            Field25519.Mul(v3, v3, v);              // v^3
            Field25519.Square(w, v3);
            Field25519.Mul(w, w, v);                // v^7
            Field25519.Mul(w, w, u);                // u v^7
            Field25519.Pow2523(w, w);               // (u v^7)^((p-5)/8)
            Field25519.Mul(w, w, v3);
            Field25519.Mul(pt.X, w, u);             // x = u v^3 (u v^7)^((p-5)/8)

            // v x^2 == u: x is a root.  v x^2 == -u: x·sqrt(-1) is.  Neither:
            // no point has this y.
            Field25519.Square(chk, pt.X);
            Field25519.Mul(chk, chk, v);
            if (!Field25519.Equal(chk, u))
            {
                Field25519.Mul(pt.X, pt.X, SqrtM1);
                Field25519.Square(chk, pt.X);
                Field25519.Mul(chk, chk, v);
                if (!Field25519.Equal(chk, u)) return false;
            }

            // Adjust sign to match the encoded sign bit.
            if (xSign == 1 && Field25519.IsZero(pt.X)) return false;
            if (Field25519.Parity(pt.X) != xSign)
                Field25519.Sub(pt.X, Field25519.Create(), pt.X);

            Field25519.SetInt(pt.Z, 1);
            Field25519.Mul(pt.T, pt.X, pt.Y);
            return true;
        }

        /// <summary>Check that two group points are equal by cross-multiplying Z coordinates.</summary>
        private static bool PointEqual(Point p1, Point p2)
        {
            // X1/Z1 == X2/Z2  ↔  X1*Z2 == X2*Z1 (mod P)
            // Y1/Z1 == Y2/Z2  ↔  Y1*Z2 == Y2*Z1 (mod P)
            Span<long> l = stackalloc long[Field25519.Limbs];
            Span<long> r = stackalloc long[Field25519.Limbs];
            Field25519.Mul(l, p1.X, p2.Z);
            Field25519.Mul(r, p2.X, p1.Z);
            if (!Field25519.Equal(l, r)) return false;
            Field25519.Mul(l, p1.Y, p2.Z);
            Field25519.Mul(r, p2.Y, p1.Z);
            return Field25519.Equal(l, r);
        }

        /// <summary>
        /// Test whether a decoded Ed25519 point lies in the order-8 torsion
        /// subgroup, i.e. has order dividing the cofactor.
        /// </summary>
        /// <remarks>
        /// Edwards25519 has cofactor h = 8. The eight low-order points
        /// (including identity) satisfy [8]P = identity. Such points
        /// contribute no security (their discrete-log is trivial) and are
        /// the classical input to small-subgroup / cofactor-confusion
        /// attacks — see Hamburg/Ladd "Decaf" §3 and the ed25519-dalek
        /// strict-verification rationale.
        ///
        /// Rejecting them here matches RFC 8032 §5.1.7's recommendation
        /// (final sentence) and the Rust gateway's
        /// <c>verify_strict</c> behavior, eliminating cross-implementation
        /// malleability where the gateway accepts a signature the SDK
        /// rejects, or vice-versa.
        ///
        /// A point on extended coordinates equals identity iff X·Z = 0·Z
        /// and Y·Z = 1·Z, i.e. <c>X == 0</c> and <c>Y == Z</c> in
        /// projective form.
        /// </remarks>
        private static bool IsSmallOrder(Point pt)
        {
            // [8]P = double(double(double(P)))
            var p8 = pt.Copy();
            PointAdd(p8, p8);
            PointAdd(p8, p8);
            PointAdd(p8, p8);
            // Identity in (X:Y:Z:T): X = 0 and Y = Z (any non-zero Y/Z pair).
            return Field25519.IsZero(p8.X) && Field25519.Equal(p8.Y, p8.Z);
        }

        // ── Scalars ──────────────────────────────────────────────────────────

        /// <summary>Whether the 32 little-endian bytes at <paramref name="offset"/> are below l.</summary>
        private static bool BelowGroupOrder(byte[] s, int offset)
        {
            for (int i = 31; i >= 0; i--)
            {
                if (s[offset + i] < L[i]) return true;
                if (s[offset + i] > L[i]) return false;
            }
            return false;   // equal to l
        }

        /// <summary>
        /// Reduces the 64-byte little-endian integer in <paramref name="x"/> (one
        /// byte per element) mod l into <paramref name="r"/>.
        /// </summary>
        private static void ModL(byte[] r, long[] x)
        {
            long carry;
            for (int i = 63; i >= 32; --i)
            {
                carry = 0;
                int j;
                for (j = i - 32; j < i - 12; ++j)
                {
                    x[j] += carry - 16 * x[i] * L[j - (i - 32)];
                    carry = (x[j] + 128) >> 8;
                    x[j] -= carry << 8;
                }
                x[j] += carry;
                x[i] = 0;
            }
            carry = 0;
            for (int j = 0; j < 32; j++)
            {
                x[j] += carry - (x[31] >> 4) * L[j];
                carry = x[j] >> 8;
                x[j] &= 255;
            }
            for (int j = 0; j < 32; j++) x[j] -= carry * L[j];
            for (int i = 0; i < 32; i++)
            {
                x[i + 1] += x[i] >> 8;
                r[i] = (byte)(x[i] & 255);
            }
        }

        private static int Bit(byte[] s, int offset, int i) => (s[offset + (i >> 3)] >> (i & 7)) & 1;

        // ── Each strict rule, observable alone ───────────────────────────────
        //
        // Through Verify a rule is seen only where a signature satisfies the
        // equation and that rule alone refuses it, and for three of them no such
        // signature can be built without a discrete logarithm: a non-canonical
        // encoding exists only for y < 19, S = l needs R = −[k]A for a k that
        // hashes R, and the sign of x is applied to every point alike, the base
        // point included.  Deleting any of them left every Verify test green, so
        // the rules are held here instead, one at a time.  Test seams: nothing in
        // the package calls them.

        /// <summary>Whether <paramref name="encoding"/> decodes to a curve point under the strict rules.</summary>
        internal static bool Decodes(byte[] encoding) => DecodePoint(encoding, 0, new Point());

        /// <summary>
        /// The decoded point encoded again (RFC 8032 §5.1.2), or null when
        /// <paramref name="encoding"/> does not decode.
        /// </summary>
        internal static byte[] Reencode(byte[] encoding)
        {
            var p = new Point();
            if (!DecodePoint(encoding, 0, p)) return null;
            var output = new byte[32];
            Field25519.Pack(output, p.Y);   // Z is 1 after a decode
            output[31] |= (byte)(Field25519.Parity(p.X) << 7);
            return output;
        }

        /// <summary>Whether <paramref name="encoding"/> decodes to a point of order dividing 8.</summary>
        internal static bool DecodesToSmallOrder(byte[] encoding)
        {
            var p = new Point();
            return DecodePoint(encoding, 0, p) && IsSmallOrder(p);
        }

        /// <summary>Whether the 32 little-endian bytes are below the group order l.</summary>
        internal static bool IsBelowGroupOrder(byte[] scalar) => BelowGroupOrder(scalar, 0);

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Verify an Ed25519 signature per RFC 8032 §5.1.7.
        /// </summary>
        /// <param name="publicKeyBytes">32-byte compressed Ed25519 public key.</param>
        /// <param name="message">The message that was signed (server_ephemeral_pub in RTMPE).</param>
        /// <param name="signature">64-byte Ed25519 signature (R || S).</param>
        /// <returns>
        /// <see langword="true"/> if the signature is valid;
        /// <see langword="false"/> for any invalid input or failed verification.
        /// </returns>
        internal static bool Verify(byte[] publicKeyBytes, byte[] message, byte[] signature)
        {
            if (publicKeyBytes == null || publicKeyBytes.Length != 32) return false;
            if (signature      == null || signature.Length      != 64) return false;
            if (message        == null)                                  return false;

            // 1. Decode the public key A.
            var A = new Point();
            if (!DecodePoint(publicKeyBytes, 0, A)) return false;

            // Strict mode (RFC 8032 §5.1.7 final paragraph + §8.4):
            // reject any A in the order-8 torsion subgroup. A small-order A
            // makes the signature equation [S]B == R + [k]A trivially
            // satisfiable in ways that don't bind the message, opening
            // signature-malleability and cross-implementation hazards with
            // the Rust gateway (ed25519-dalek verify_strict).
            if (IsSmallOrder(A)) return false;

            // 2–3. Decode R, the signature's first 32 bytes.
            var R = new Point();
            if (!DecodePoint(signature, 0, R)) return false;

            // Reject small-order R: same rationale as A above. A small-order
            // R lets an attacker craft a signature that verifies for any
            // message under any public key when paired with a small-order A,
            // and is the canonical malleability vector closed by
            // ed25519-dalek's strict mode.
            if (IsSmallOrder(R)) return false;

            // 4. S, the last 32 bytes, little-endian, must be below l.
            if (!BelowGroupOrder(signature, 32)) return false;

            // 5. k = SHA-512(R_bytes || publicKeyBytes || message) mod l.
            byte[] kHash;
            using (var sha = SHA512.Create())
            {
                var hashInput = new byte[32 + 32 + message.Length];
                Buffer.BlockCopy(signature,      0, hashInput,  0, 32);
                Buffer.BlockCopy(publicKeyBytes, 0, hashInput, 32, 32);
                Buffer.BlockCopy(message,        0, hashInput, 64, message.Length);
                kHash = sha.ComputeHash(hashInput);
            }
            var wide = new long[64];
            for (int i = 0; i < 64; i++) wide[i] = kHash[i];
            var k = new byte[32];
            ModL(k, wide);

            // 6. [S]B == R + [k]A, checked as [S]B + [k](−A) == R — the cofactor-
            // less equation, which with S < l and neither point of small order is
            // what verify_strict checks.  Both multiplications share one pass of
            // doublings: at each bit, add B, −A, or B − A as the two bits say.
            Negate(A);
            var BminusA = BasePoint.Copy();
            PointAdd(BminusA, A);

            var Q = Point.Identity();
            for (int i = 255; i >= 0; i--)
            {
                PointAdd(Q, Q);
                int bs = Bit(signature, 32, i);
                int bk = Bit(k, 0, i);
                if (bs == 1 && bk == 1) PointAdd(Q, BminusA);
                else if (bs == 1)       PointAdd(Q, BasePoint);
                else if (bk == 1)       PointAdd(Q, A);
            }

            return PointEqual(Q, R);
        }
    }
}
