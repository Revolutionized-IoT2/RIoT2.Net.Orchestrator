using System.Net.NetworkInformation;
using System.Net.Sockets;
using RIoT2.Matter.Clusters;
using MatterNetworkInterface = RIoT2.Matter.Clusters.NetworkInterface;
using SystemNetworkInterface = System.Net.NetworkInformation.NetworkInterface;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// The host's network interfaces as the General Diagnostics cluster reports them.
    /// </summary>
    internal static class MatterHostNetwork
    {
        /// <summary>
        /// Reports the host's operational interfaces. Matter is IPv6-centric, so an interface without an
        /// IPv6 address is of no use to a commissioner and is left out.
        /// </summary>
        public static IReadOnlyList<MatterNetworkInterface> EnumerateInterfaces(ILogger logger)
        {
            var interfaces = new List<MatterNetworkInterface>();

            foreach (var nic in SystemNetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var addresses = nic.GetIPProperties().UnicastAddresses;
                var ipv6 = addresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(a => a.Address.GetAddressBytes())
                    .ToList();

                if (ipv6.Count == 0)
                    continue;

                var ipv4 = addresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.GetAddressBytes())
                    .ToList();

                interfaces.Add(new MatterNetworkInterface
                {
                    // The cluster limits the name to 32 characters; a Windows adapter name easily exceeds that.
                    Name = nic.Name.Length > 32 ? nic.Name[..32] : nic.Name,
                    IsOperational = true,
                    Type = MapInterfaceType(nic.NetworkInterfaceType),
                    HardwareAddress = nic.GetPhysicalAddress().GetAddressBytes(),
                    IPv4Addresses = ipv4,
                    IPv6Addresses = ipv6
                });
            }

            if (interfaces.Count == 0)
            {
                logger?.LogWarning(
                    "No operational IPv6 interface was found. Matter requires IPv6; a commissioner will not discover the bridge.");
            }

            return interfaces;
        }

        internal static InterfaceType MapInterfaceType(NetworkInterfaceType type) => type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx => InterfaceType.Ethernet,
            NetworkInterfaceType.Wireless80211 => InterfaceType.WiFi,
            NetworkInterfaceType.Wman or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => InterfaceType.Cellular,
            _ => InterfaceType.Unspecified
        };
    }
}
