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

        /// <summary>The IPv4 address of the first active adapter with a default gateway that isn't virtual, or null.</summary>
        public static IPAddress PrimaryLanIPv4()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string label = nic.Name + " " + nic.Description;
                    if (label.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        label.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    if (!properties.GatewayAddresses.Any(delegate(GatewayIPAddressInformation g) { return g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address); }))
                        continue;
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address) &&
                            !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                            return address.Address;
                    }
                }
            }
            catch (NetworkInformationException) { }
            return null;
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
