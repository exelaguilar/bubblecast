using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

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
                    if (nic.OperationalStatus != OperationalStatus.Up || IsVirtualOrTunnel(nic, true)) continue;
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    if (!HasDefaultGateway(properties)) continue;
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        if (IsUsableLanAddress(address.Address)) return address.Address;
                    }
                }
            }
            catch (NetworkInformationException) { }
            return null;
        }

        /// <summary>
        /// One line per IPv4 address for the Settings window, labeled with its adapter. Likely-LAN
        /// adapters (a default gateway and not virtual) come first.
        /// </summary>
        public static string DescribeLocalIPv4()
        {
            List<string> likelyLan = new List<string>();
            List<string> other = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    bool hasGateway = HasDefaultGateway(properties);
                    bool virtualOrTunnel = IsVirtualOrTunnel(nic, false);
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        if (!IsUsableLanAddress(address.Address)) continue;
                        StringBuilder entry = new StringBuilder(nic.Name + " (" + nic.NetworkInterfaceType + "): " + address.Address);
                        if (hasGateway) entry.Append(" (likely LAN; default gateway)");
                        if (virtualOrTunnel) entry.Append(" (virtual or tunnel adapter)");
                        (hasGateway && !virtualOrTunnel ? likelyLan : other).Add(entry.ToString());
                    }
                }
            }
            catch (NetworkInformationException) { }
            List<string> lines = likelyLan.Concat(other).ToList();
            return lines.Count == 0 ? "No active IPv4 address found." : String.Join(Environment.NewLine, lines.ToArray());
        }

        private static bool HasDefaultGateway(IPInterfaceProperties properties)
        {
            return properties.GatewayAddresses.Any(delegate(GatewayIPAddressInformation gateway)
            {
                return gateway.Address != null && gateway.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(gateway.Address);
            });
        }

        /// <summary>Hyper-V, WSL, VPN, and tunnel adapters; with <paramref name="includeVirtualName"/>, anything named "Virtual" too.</summary>
        private static bool IsVirtualOrTunnel(NetworkInterface nic, bool includeVirtualName)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
            string label = nic.Name + " " + nic.Description;
            string[] markers = includeVirtualName ? new[] { "Hyper-V", "WSL", "VPN", "Virtual" } : new[] { "Hyper-V", "WSL", "VPN" };
            foreach (string marker in markers)
                if (label.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool IsUsableLanAddress(IPAddress address)
        {
            return address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal);
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
