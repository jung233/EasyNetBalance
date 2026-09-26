using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EasyBalance.Shared;

/// <summary>Discovers Windows network interfaces without using their mutable friendly names as identity.</summary>
public sealed class NetworkAdapterProvider : IAdapterProvider
{
    private static readonly string[] VirtualNameMarkers =
    [
        "virtualbox", "host-only", "hyper-v", "vethernet", "docker", "wsl", "vmware",
        "tailscale", "wireguard", "tap-windows", "openvpn", "tun", "tunnel", "loopback",
        "npcap", "bluetooth"
    ];

    private static readonly HashSet<NetworkInterfaceType> NonPhysicalTypes =
    [
        NetworkInterfaceType.Loopback,
        NetworkInterfaceType.Tunnel
    ];

    public IReadOnlyList<NetworkAdapterInfo> GetAdaptersSnapshot(CancellationToken cancellationToken = default)
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                adapters.Add(CreateInfo(networkInterface));
            }
            catch (NetworkInformationException)
            {
                // Interfaces can disappear while the snapshot is being collected.
            }
            catch (ArgumentException)
            {
                // A transient or malformed system address is skipped; the next refresh can retry.
            }
        }

        return adapters
            .OrderByDescending(adapter => adapter.IsPhysical)
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ValueTask<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetAdaptersSnapshot(cancellationToken));

    public ValueTask<IReadOnlyList<NetworkAdapterInfo>> GetAdapters(CancellationToken cancellationToken = default) =>
        GetAdaptersAsync(cancellationToken);

    /// <summary>Applies persisted user choices using interface GUIDs, never friendly names.</summary>
    public static void ApplyUsabilityOverrides(
        IEnumerable<NetworkAdapterInfo> adapters,
        IReadOnlyDictionary<string, bool> overrides)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(overrides);

        foreach (var adapter in adapters)
        {
            if (overrides.TryGetValue(adapter.Id, out var isAllowed))
            {
                adapter.IsUserAllowed = isAllowed;
            }
        }
    }

    private static NetworkAdapterInfo CreateInfo(NetworkInterface networkInterface)
    {
        var properties = networkInterface.GetIPProperties();
        var unicastAddresses = properties.UnicastAddresses.Select(item => item.Address).ToArray();
        var gateways = properties.GatewayAddresses.Select(item => item.Address).ToArray();
        var isVirtual = IsVirtualInterface(networkInterface, gateways, unicastAddresses);
        var isPhysical = IsPhysicalInterface(networkInterface, isVirtual, gateways, unicastAddresses);

        return new NetworkAdapterInfo
        {
            Id = CanonicalizeInterfaceId(networkInterface.Id),
            Name = networkInterface.Name,
            Description = networkInterface.Description,
            NetworkInterfaceType = networkInterface.NetworkInterfaceType,
            OperationalStatus = networkInterface.OperationalStatus,
            Speed = networkInterface.Speed,
            IPv4Addresses = unicastAddresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IPv6Addresses = unicastAddresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IPv4Gateways = gateways
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IsUnspecified(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IPv6Gateways = gateways
                .Where(address => address.AddressFamily == AddressFamily.InterNetworkV6 && !IsUnspecified(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DnsServers = properties.DnsAddresses
                .Where(address => !IsUnspecified(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsPhysical = isPhysical,
            IsVirtual = isVirtual,
            IsUserAllowed = isPhysical
        };
    }

    private static string CanonicalizeInterfaceId(string interfaceId)
    {
        if (Guid.TryParse(interfaceId, out var guid))
        {
            return guid.ToString("D");
        }

        // Preserve the native identifier for non-Windows or unusual drivers instead of
        // substituting a friendly name that may change.
        return interfaceId.Trim().Trim('{', '}').ToLowerInvariant();
    }

    private static bool IsVirtualInterface(
        NetworkInterface networkInterface,
        IReadOnlyCollection<IPAddress> gateways,
        IReadOnlyCollection<IPAddress> addresses)
    {
        if (NonPhysicalTypes.Contains(networkInterface.NetworkInterfaceType))
        {
            return true;
        }

        var metadata = string.Concat(networkInterface.Name, " ", networkInterface.Description);
        if (VirtualNameMarkers.Any(marker => metadata.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Point-to-point, PPP and software-only interfaces rarely represent a direct
        // physical Internet adapter. Keep unknown Ethernet/Wi-Fi types eligible.
        return networkInterface.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.AsymmetricDsl
            && gateways.Count == 0
            && addresses.Count == 0;
    }

    private static bool IsPhysicalInterface(
        NetworkInterface networkInterface,
        bool isVirtual,
        IReadOnlyCollection<IPAddress> gateways,
        IReadOnlyCollection<IPAddress> addresses)
    {
        if (isVirtual || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Unknown)
        {
            return false;
        }

        var hasUsableAddress = addresses.Any(address => !IsUnspecified(address) && !IPAddress.IsLoopback(address));
        var hasGateway = gateways.Any(address => !IsUnspecified(address));
        var supportedMedium = networkInterface.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.Wireless80211 or
            NetworkInterfaceType.Wman or
            NetworkInterfaceType.HighPerformanceSerialBus or
            NetworkInterfaceType.Ethernet3Megabit;

        // Hardware medium, usable addresses, and route metadata are combined so an
        // Ethernet-like virtual adapter with no Internet path is not promoted by name alone.
        return supportedMedium && hasUsableAddress && (hasGateway || networkInterface.OperationalStatus == OperationalStatus.Up);
    }

    private static bool IsUnspecified(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None);
}
