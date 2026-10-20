// RTMPE SDK — Runtime/Crypto/Internal/Field25519.cs
//
// Arithmetic in GF(2^255 - 19), the field under X25519 (RFC 7748) and Ed25519
// (RFC 8032): an element is sixteen signed 64-bit limbs of 16 bits each,
// little-endian, value = Σ limb[i] · 2^(16·i).
//
// The representation and every routine here follow TweetNaCl (Daniel J.
// Bernstein, Bernard van Gastel, Wesley Janssen, Tanja Lange, Peter Schwabe
// and Sjaak Smetsers), which is placed in the public domain.
//
// It replaced System.Numerics.BigInteger, and for two reasons (audit P1-L33).
// A BigInteger operation allocates: one connection's four X25519 ladders and
// one Ed25519 verify came to about a hundred thousand short-lived objects on
// the main thread, most of them inside the Challenge's frame, which on a
// low-end phone running IL2CPP is a garbage collection there as well as the
// arithmetic.  Here every temporary is on the stack.  And a BigInteger
// operation's time depends on its operands; nothing here branches or indexes
// on an element's value, so the X25519 ladder above it runs in time that does
// not depend on the secret scalar.
//
// Limb bounds: Mul carries twice, after which every limb is within a few bits
// of 16 (the top limb's carry wraps into limb 0 times 38, as 2^256 ≡ 38 mod
// p).  Add and Sub do not carry; every caller feeds their result to Mul within
// three levels (a public key's x: its sign, Negate, then PointAdd's Add or Sub),
// and a negation does not grow a magnitude, so each product and each 31-term
// sum stays far inside 64 bits — the budget TweetNaCl's own call pattern keeps.

using System;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// Arithmetic in GF(2^255 − 19) over sixteen 16-bit limbs (TweetNaCl's
    /// representation).
    /// </summary>
    internal static class Field25519
    {
        /// <summary>Limbs per element.</summary>
        internal const int Limbs = 16;

        /// <summary>A new element, zero.</summary>
        internal static long[] Create() => new long[Limbs];

        /// <summary>A new element holding the small non-negative integer <paramref name="v"/>.</summary>
        internal static long[] FromInt(int v)
        {
            var e = new long[Limbs];
            e[0] = v & 0xffff;
            e[1] = v >> 16;
            return e;
        }

        internal static void Set(Span<long> o, ReadOnlySpan<long> a) => a.Slice(0, Limbs).CopyTo(o);

        internal static void SetInt(Span<long> o, int v)
        {
            o.Slice(0, Limbs).Clear();
            o[0] = v & 0xffff;
            o[1] = v >> 16;
        }

        /// <summary>Propagates carries so each limb is back in 16 bits.</summary>
        internal static void Carry(Span<long> o)
        {
            for (int i = 0; i < Limbs; i++)
            {
                o[i] += 1L << 16;
                long c = o[i] >> 16;
                if (i < Limbs - 1) o[i + 1] += c - 1;
                else o[0] += 38 * (c - 1);
                o[i] -= c << 16;
            }
        }

        /// <summary>
        /// Exchanges <paramref name="p"/> and <paramref name="q"/> when
        /// <paramref name="b"/> is 1 and leaves them when it is 0, by masking —
        /// the same instructions either way.
        /// </summary>
        internal static void Swap(Span<long> p, Span<long> q, long b)
        {
            long c = ~(b - 1);
            for (int i = 0; i < Limbs; i++)
            {
                long t = c & (p[i] ^ q[i]);
                p[i] ^= t;
                q[i] ^= t;
            }
        }

        /// <summary>
        /// Writes the canonical 32-byte little-endian encoding — the value fully
        /// reduced into [0, p).
        /// </summary>
        internal static void Pack(Span<byte> o, ReadOnlySpan<long> n)
        {
            Span<long> t = stackalloc long[Limbs];
            Span<long> m = stackalloc long[Limbs];
            n.Slice(0, Limbs).CopyTo(t);
            Carry(t);
            Carry(t);
            Carry(t);
            for (int j = 0; j < 2; j++)
            {
                m[0] = t[0] - 0xffed;
                for (int i = 1; i < 15; i++)
                {
                    m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                    m[i - 1] &= 0xffff;
                }
                m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
                long b = (m[15] >> 16) & 1;
                m[14] &= 0xffff;
                Swap(t, m, 1 - b);
            }
            for (int i = 0; i < Limbs; i++)
            {
                o[2 * i]     = (byte)(t[i] & 0xff);
                o[2 * i + 1] = (byte)(t[i] >> 8);
            }
            t.Clear();
            m.Clear();
        }

        /// <summary>
        /// Reads a 32-byte little-endian encoding, ignoring the top bit (RFC 7748
        /// §5 masks it; RFC 8032 §5.1.3 carries the sign of x there).  A value
        /// at or above p is read as itself and reduces in arithmetic.
        /// </summary>
        internal static void Unpack(Span<long> o, ReadOnlySpan<byte> n)
        {
            for (int i = 0; i < Limbs; i++) o[i] = n[2 * i] + ((long)n[2 * i + 1] << 8);
            o[15] &= 0x7fff;
        }

        /// <summary>Whether two elements are the same field value.</summary>
        internal static bool Equal(ReadOnlySpan<long> a, ReadOnlySpan<long> b)
        {
            Span<byte> c = stackalloc byte[32];
            Span<byte> d = stackalloc byte[32];
            Pack(c, a);
            Pack(d, b);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= c[i] ^ d[i];
            return diff == 0;
        }

        /// <summary>Whether the element is zero.</summary>
        internal static bool IsZero(ReadOnlySpan<long> a)
        {
            Span<byte> c = stackalloc byte[32];
            Pack(c, a);
            int acc = 0;
            for (int i = 0; i < 32; i++) acc |= c[i];
            return acc == 0;
        }

        /// <summary>The low bit of the canonical encoding — the "sign" RFC 8032 names.</summary>
        internal static int Parity(ReadOnlySpan<long> a)
        {
            Span<byte> c = stackalloc byte[32];
            Pack(c, a);
            return c[0] & 1;
        }

        internal static void Add(Span<long> o, ReadOnlySpan<long> a, ReadOnlySpan<long> b)
        {
            for (int i = 0; i < Limbs; i++) o[i] = a[i] + b[i];
        }

        internal static void Sub(Span<long> o, ReadOnlySpan<long> a, ReadOnlySpan<long> b)
        {
            for (int i = 0; i < Limbs; i++) o[i] = a[i] - b[i];
        }

        /// <summary>
        /// <paramref name="o"/> = <paramref name="a"/> · <paramref name="b"/>.
        /// Either factor may be the output.
        /// </summary>
        internal static void Mul(Span<long> o, ReadOnlySpan<long> a, ReadOnlySpan<long> b)
        {
            Span<long> t = stackalloc long[31];
            t.Clear();
            for (int i = 0; i < Limbs; i++)
            {
                long ai = a[i];
                for (int j = 0; j < Limbs; j++) t[i + j] += ai * b[j];
            }
            // 2^256 ≡ 38: fold the high half onto the low.
            for (int i = 0; i < 15; i++) t[i] += 38 * t[i + 16];
            for (int i = 0; i < Limbs; i++) o[i] = t[i];
            t.Clear();
            Carry(o);
            Carry(o);
        }

        internal static void Square(Span<long> o, ReadOnlySpan<long> a) => Mul(o, a, a);

        /// <summary>
        /// <paramref name="o"/> = <paramref name="i"/>^(p−2), the inverse of a
        /// non-zero element (Fermat); zero maps to zero.
        /// </summary>
        internal static void Invert(Span<long> o, ReadOnlySpan<long> i)
        {
            Span<long> c = stackalloc long[Limbs];
            i.Slice(0, Limbs).CopyTo(c);
            for (int a = 253; a >= 0; a--)
            {
                Square(c, c);
                if (a != 2 && a != 4) Mul(c, c, i);
            }
            c.CopyTo(o);
            c.Clear();
        }

        /// <summary><paramref name="o"/> = <paramref name="i"/>^((p−5)/8), for square roots.</summary>
        internal static void Pow2523(Span<long> o, ReadOnlySpan<long> i)
        {
            Span<long> c = stackalloc long[Limbs];
            i.Slice(0, Limbs).CopyTo(c);
            for (int a = 250; a >= 0; a--)
            {
                Square(c, c);
                if (a != 1) Mul(c, c, i);
            }
            c.CopyTo(o);
            c.Clear();
        }
    }
}
