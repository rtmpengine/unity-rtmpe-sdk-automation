using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RTMPE.Transport
{
    internal static class LocalRouteProbe
    {
        /// <summary>
        /// The current route's address, an address of the surviving family when
        /// the previous family is absent, or null when the path is unknown.
        /// </summary>
        internal static IPAddress Read(
            IPAddress previous, Func<IPAddress> routeAddress, Func<IPAddress[]> localAddresses)
        {
            try
            {
                // Loopback does not depend on which external network is active.
                if (!IPAddress.IsLoopback(previous))
                {
                    var addresses = localAddresses?.Invoke();
                    if (addresses == null) return null;
                    bool sameFamily = false;
                    IPAddress otherFamily = null;
                    foreach (var address in addresses)
                    {
                        if (!IsUsable(address)) continue;
                        if (address.AddressFamily == previous.AddressFamily) sameFamily = true;
                        else otherFamily = address;
                    }

                    // A surviving family is evidence of a handover even if the
                    // old-family routing lookup cannot answer. No network is not.
                    if (!sameFamily) return otherFamily;
                }
                return routeAddress?.Invoke();
            }
            catch (Exception) { return null; }
        }

        /// <summary>Addresses of interfaces that are up, without a DNS lookup.</summary>
        internal static IPAddress[] ActiveAddresses()
        {
            var addresses = new List<IPAddress>();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                    addresses.Add(unicast.Address);
            }
            return addresses.ToArray();
        }

        private static bool IsUsable(IPAddress address)
        {
            if (address == null || IPAddress.IsLoopback(address)) return false;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal;
            if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any))
                return false;
            // Self-assigned link-local addresses do not establish an external path.
            var bytes = address.GetAddressBytes();
            return bytes[0] != 169 || bytes[1] != 254;
        }
    }
}
