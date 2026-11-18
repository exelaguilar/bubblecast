using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bubblecast
{
    /// <summary>This PC's IPv4 addresses, for the listen-address setting.</summary>
    public static class NetworkAddresses
    {
        public static IList<string> LocalIPv4()
        {
            List<string> result = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                            result.Add(address.Address.ToString());
                    }
                }
            }
            catch (NetworkInformationException) { }
            return result.Distinct().ToList();
        }

        /// <summary>Blank (all adapters) or one of this PC's current IPv4 addresses.</summary>
        public static bool IsUsableListenAddress(string value)
        {
            return IsUsableListenAddress(value, LocalIPv4());
        }

        public static bool IsUsableListenAddress(string value, IList<string> localAddresses)
        {
            if (String.IsNullOrWhiteSpace(value)) return true;
            IPAddress parsed;
            string trimmed = value.Trim();
            return IPAddress.TryParse(trimmed, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork &&
                localAddresses.Contains(parsed.ToString());
        }
    }
}
