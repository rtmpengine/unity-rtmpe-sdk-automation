// RTMPE SDK — Runtime/Infrastructure/Transport/RoutableAddress.cs
//
// Which of a host's addresses this device can actually send to.
//
// 🔑 Audit P2-M6. The transport preferred IPv4 unconditionally, and a literal
// IPv4 server address skips resolution altogether — so on an IPv6-only network
// behind NAT64 (Apple's App Review network is one; a pod is reached by a literal
// address) the socket had no route, the send failure is non-fatal by design, and
// the attempt ended as a silent "no server reply". Such a network publishes how
// to reach IPv4 through it: `ipv4only.arpa` resolves, through its DNS64, to
// AAAA records that embed the well-known addresses 192.0.0.170/171 in the
// network's NAT64 prefix (RFC 7050), and any IPv4 address is reached by
// embedding it the same way (RFC 6052).
//
// ⛔ Asked of the kernel, never assumed: a route lookup (connect on a datagram
// socket) sends nothing, and a family is used only when the lookup succeeds. A
// device with an IPv4 route keeps exactly the address it used before.

using System;
using System.Net;
using System.Net.Sockets;

namespace RTMPE.Transport
{
    internal static class RoutableAddress
    {
        /// <summary>The name whose AAAA records reveal a NAT64 prefix (RFC 7050).</summary>
        internal const string Nat64DiscoveryName = "ipv4only.arpa";

        // The two well-known IPv4 addresses ipv4only.arpa resolves to.
        private static readonly byte[] WellKnownA = { 192, 0, 0, 170 };
        private static readonly byte[] WellKnownB = { 192, 0, 0, 171 };

        // The prefix lengths RFC 6052 defines, longest first: /96 is the
        // well-known prefix's and the commonest, so it is tried first.
        private static readonly int[] PrefixLengths = { 96, 64, 56, 48, 40, 32 };

        /// <summary>
        /// The address to send to: the first IPv4 address when it has a route,
        /// else the first IPv6 address when it has one, else the IPv4 address
        /// embedded in the network's NAT64 prefix when that has one. When nothing
        /// is provably routable, the old preference — IPv4, then IPv6 — so an
        /// attempt with no network fails exactly as it did.
        /// </summary>
        /// <param name="resolved">The host's addresses, in resolver order.</param>
        /// <param name="routable">Whether the kernel has a route to an address.</param>
        /// <param name="nat64Answers">The AAAA answers for <see cref="Nat64DiscoveryName"/>;
        /// asked only when needed, and a throw is read as "none".</param>
        /// <param name="note">What was chosen instead of IPv4 and why, or <see langword="null"/>.</param>
        internal static IPAddress Choose(
            IPAddress[] resolved, Func<IPAddress, bool> routable, Func<IPAddress[]> nat64Answers,
            out string note)
        {
            note = null;
            if (resolved == null || routable == null) return null;

            IPAddress v4 = null, v6 = null;
            foreach (var a in resolved)
            {
                if (a == null) continue;
                if (v4 == null && a.AddressFamily == AddressFamily.InterNetwork) v4 = a;
                if (v6 == null && a.AddressFamily == AddressFamily.InterNetworkV6) v6 = a;
            }

            if (v4 != null && routable(v4)) return v4;

            if (v6 != null && routable(v6))
            {
                if (v4 != null) note = $"no IPv4 route to {v4}; using {v6}";
                return v6;
            }

            if (v4 != null && nat64Answers != null)
            {
                IPAddress[] answers = null;
                try { answers = nat64Answers(); }
                catch (Exception) { answers = null; }

                IPAddress synthesized = SynthesizeNat64(v4, answers);
                if (synthesized != null && routable(synthesized))
                {
                    note = $"no IPv4 route to {v4}; reaching it through this network's NAT64 as {synthesized}";
                    return synthesized;
                }
            }

            return v4 ?? v6;
        }

        /// <summary>
        /// <paramref name="ipv4"/> embedded in the NAT64 prefix that
        /// <paramref name="discoveryAnswers"/> reveal, or <see langword="null"/>
        /// when they reveal none.
        /// </summary>
        /// <remarks>
        /// RFC 7050 §3: an answer reveals a prefix when one of the two well-known
        /// addresses sits at the position RFC 6052 §2.2 gives for one of its
        /// prefix lengths; the prefix is that answer with the embedded address
        /// and the suffix cleared. Bits 64–71 (the "u" octet) are never part of
        /// an embedding, and are zero in a synthesized address.
        /// </remarks>
        internal static IPAddress SynthesizeNat64(IPAddress ipv4, IPAddress[] discoveryAnswers)
        {
            if (ipv4 == null || ipv4.AddressFamily != AddressFamily.InterNetwork) return null;
            if (discoveryAnswers == null) return null;
            byte[] v4 = ipv4.GetAddressBytes();

            foreach (var answer in discoveryAnswers)
            {
                if (answer == null || answer.AddressFamily != AddressFamily.InterNetworkV6) continue;
                byte[] bytes = answer.GetAddressBytes();

                foreach (int length in PrefixLengths)
                {
                    byte[] embedded = Extract(bytes, length);
                    if (!SameBytes(embedded, WellKnownA) && !SameBytes(embedded, WellKnownB)) continue;

                    var result = new byte[16];
                    Buffer.BlockCopy(bytes, 0, result, 0, length / 8);
                    Embed(result, length, v4);
                    return new IPAddress(result);
                }
            }
            return null;
        }

        // The byte positions RFC 6052 §2.2 places the four IPv4 octets at, for
        // each prefix length — skipping byte 8, the "u" octet.
        private static int[] Positions(int prefixLength)
        {
            switch (prefixLength)
            {
                case 32: return new[] { 4, 5, 6, 7 };
                case 40: return new[] { 5, 6, 7, 9 };
                case 48: return new[] { 6, 7, 9, 10 };
                case 56: return new[] { 7, 9, 10, 11 };
                case 64: return new[] { 9, 10, 11, 12 };
                case 96: return new[] { 12, 13, 14, 15 };
                default: throw new ArgumentOutOfRangeException(nameof(prefixLength));
            }
        }

        private static byte[] Extract(byte[] v6, int prefixLength)
        {
            int[] at = Positions(prefixLength);
            return new[] { v6[at[0]], v6[at[1]], v6[at[2]], v6[at[3]] };
        }

        private static void Embed(byte[] v6, int prefixLength, byte[] v4)
        {
            int[] at = Positions(prefixLength);
            for (int i = 0; i < 4; i++) v6[at[i]] = v4[i];
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
