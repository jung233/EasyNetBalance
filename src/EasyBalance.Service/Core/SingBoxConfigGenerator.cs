using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyBalance.Shared;

namespace EasyBalance.Service.Core;

public sealed class SingBoxGeneratedConfig
{
    internal SingBoxGeneratedConfig(
        string json,
        int apiPort,
        string apiSecret,
        IReadOnlyDictionary<string, string> selectorTags,
        IReadOnlyDictionary<string, string> outboundTags)
    {
        Json = json;
        ApiPort = apiPort;
        ApiSecret = apiSecret;
        SelectorTags = selectorTags;
        OutboundTags = outboundTags;
    }

    public string Json { get; }
    public int ApiPort { get; }

    /// <summary>Kept in memory for the local control client and never included in ordinary logs.</summary>
    public string ApiSecret { get; }

    public IReadOnlyDictionary<string, string> SelectorTags { get; }
    public IReadOnlyDictionary<string, string> OutboundTags { get; }

    public override string ToString() => $"SingBoxGeneratedConfig {{ ApiPort = {ApiPort}, Json = [redacted], ApiSecret = [redacted] }}";
}

/// <summary>
/// Builds the current sing-box JSON schema used by EasyBalance. Stable interface GUIDs are
/// converted to deterministic tags, while bind_interface always uses the current adapter name.
/// </summary>
public sealed class SingBoxConfigGenerator
{
    private const string TunTag = "easybalance-tun";
    private const string BlockTag = "easybalance-unavailable";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public SingBoxGeneratedConfig Generate(
        AppSettings settings,
        IReadOnlyCollection<NetworkAdapterInfo> adapters,
        SingBoxCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(capabilities);
        capabilities.EnsureCompatible();
        if (settings.DefaultPolicy is null)
        {
            throw new InvalidOperationException("A default routing policy must be configured.");
        }

        var allowedAdapters = adapters
            .Where(adapter => adapter.IsUserAllowed &&
                              !string.IsNullOrWhiteSpace(adapter.Id) &&
                              !string.IsNullOrWhiteSpace(adapter.Name))
            .GroupBy(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var outboundTags = allowedAdapters.ToDictionary(
            adapter => adapter.Id,
            adapter => OutboundTag(adapter.Id),
            StringComparer.OrdinalIgnoreCase);
        var outbounds = new JsonArray();
        foreach (var adapter in allowedAdapters)
        {
            outbounds.Add(new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = OutboundTag(adapter.Id),
                ["bind_interface"] = adapter.Name
            });
        }

        var policies = GetPolicies(settings);
        var selectorTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selectorByPolicyFamily = new Dictionary<(string PolicyId, AddressFamilyKind Family), string>();
        var needsUnavailableOutbound = false;

        foreach (var policy in policies)
        {
            AddSelector(policy, AddressFamilyKind.IPv4, outboundTags, selectorTags, selectorByPolicyFamily, outbounds, ref needsUnavailableOutbound);
            if (settings.Ipv6Enabled)
            {
                AddSelector(policy, AddressFamilyKind.IPv6, outboundTags, selectorTags, selectorByPolicyFamily, outbounds, ref needsUnavailableOutbound);
            }
        }

        if (needsUnavailableOutbound)
        {
            outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = BlockTag });
        }

        var rules = new JsonArray();
        foreach (var applicationRule in settings.ApplicationRules
                     .Where(rule => rule.Enabled)
                     .OrderByDescending(rule => !string.IsNullOrWhiteSpace(rule.ExecutablePath))
                     .ThenByDescending(rule => rule.Priority)
                     .ThenBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase))
        {
            var ipv4Selector = GetRuleSelector(
                selectorByPolicyFamily,
                applicationRule.PolicyId,
                AddressFamilyKind.IPv4,
                selectorByPolicyFamily[(settings.DefaultPolicy.Id, AddressFamilyKind.IPv4)]);

            if (HasProcessMatcher(applicationRule))
            {
                rules.Add(CreateProcessRule(applicationRule, ipv4Selector, 4));
            }

            if (settings.Ipv6Enabled)
            {
                if (HasProcessMatcher(applicationRule))
                {
                    var ipv6Selector = GetRuleSelector(
                        selectorByPolicyFamily,
                        applicationRule.PolicyId,
                        AddressFamilyKind.IPv6,
                        selectorByPolicyFamily[(settings.DefaultPolicy.Id, AddressFamilyKind.IPv6)]);
                    rules.Add(CreateProcessRule(applicationRule, ipv6Selector, 6));
                }
            }
        }

        var defaultPolicyId = settings.DefaultPolicy.Id;
        var defaultIpv4 = selectorByPolicyFamily[(defaultPolicyId, AddressFamilyKind.IPv4)];
        rules.Add(new JsonObject
        {
            ["ip_version"] = 4,
            ["action"] = "route",
            ["outbound"] = defaultIpv4
        });

        if (settings.Ipv6Enabled)
        {
            var defaultIpv6 = selectorByPolicyFamily[(defaultPolicyId, AddressFamilyKind.IPv6)];
            rules.Add(new JsonObject
            {
                ["ip_version"] = 6,
                ["action"] = "route",
                ["outbound"] = defaultIpv6
            });
        }

        var apiPort = ReserveEphemeralLoopbackPort();
        var apiSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var addressSet = CreateNonConflictingAddressSet(adapters);
        var tunAddresses = new JsonArray { addressSet.IPv4Address };
        if (settings.Ipv6Enabled)
        {
            tunAddresses.Add(addressSet.IPv6Address);
        }

        var configuration = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = TunTag,
                    ["interface_name"] = "EasyBalance",
                    ["address"] = tunAddresses,
                    ["auto_route"] = true,
                    ["strict_route"] = settings.StrictRoute
                }
            },
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["rules"] = rules,
                ["final"] = defaultIpv4
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{apiPort}",
                    ["secret"] = apiSecret,
                    ["access_control_allow_origin"] = new JsonArray(),
                    ["access_control_allow_private_network"] = false
                }
            }
        };

        return new SingBoxGeneratedConfig(
            configuration.ToJsonString(JsonOptions),
            apiPort,
            apiSecret,
            selectorTags,
            outboundTags);
    }

    public static string SelectorTag(string policyId, AddressFamilyKind family) =>
        $"policy-{StableSuffix(policyId)}-{(family == AddressFamilyKind.IPv4 ? "v4" : "v6")}";

    public static string SelectorTag(string policyId, AddressFamily family) =>
        SelectorTag(policyId, family == AddressFamily.InterNetworkV6 ? AddressFamilyKind.IPv6 : AddressFamilyKind.IPv4);

    public static string OutboundTag(string interfaceId) => $"direct-{StableSuffix(interfaceId)}";

    private static void AddSelector(
        RoutingPolicy policy,
        AddressFamilyKind family,
        IReadOnlyDictionary<string, string> outboundTags,
        IDictionary<string, string> publicSelectorTags,
        IDictionary<(string PolicyId, AddressFamilyKind Family), string> selectorByPolicyFamily,
        JsonArray outbounds,
        ref bool needsUnavailableOutbound)
    {
        var candidates = new List<string>();
        AddCandidate(policy.PrimaryInterfaceId, outboundTags, candidates);
        if (policy.FailoverEnabled && !string.IsNullOrWhiteSpace(policy.FallbackInterfaceId))
        {
            AddCandidate(policy.FallbackInterfaceId, outboundTags, candidates);
        }

        if (candidates.Count == 0)
        {
            candidates.Add(BlockTag);
            needsUnavailableOutbound = true;
        }

        var selectorTag = SelectorTag(policy.Id, family);
        var members = new JsonArray();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            members.Add(candidate);
        }

        outbounds.Add(new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = selectorTag,
            ["outbounds"] = members,
            ["default"] = candidates[0],
            ["interrupt_exist_connections"] = false
        });

        publicSelectorTags[$"{policy.Id}:{family}"] = selectorTag;
        selectorByPolicyFamily[(policy.Id, family)] = selectorTag;
        selectorByPolicyFamily[(policy.Id.ToLowerInvariant(), family)] = selectorTag;
    }

    private static void AddCandidate(string? interfaceId, IReadOnlyDictionary<string, string> outboundTags, ICollection<string> candidates)
    {
        if (!string.IsNullOrWhiteSpace(interfaceId) && outboundTags.TryGetValue(interfaceId, out var outboundTag))
        {
            candidates.Add(outboundTag);
        }
    }

    private static JsonObject CreateProcessRule(ApplicationRule rule, string selectorTag, int ipVersion)
    {
        var json = new JsonObject
        {
            ["ip_version"] = ipVersion,
            ["action"] = "route",
            ["outbound"] = selectorTag
        };

        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath))
        {
            json["process_path"] = new JsonArray(JsonValue.Create(rule.ExecutablePath));
        }
        else if (!string.IsNullOrWhiteSpace(rule.ProcessName))
        {
            json["process_name"] = new JsonArray(JsonValue.Create(rule.ProcessName));
        }

        return json;
    }

    private static bool HasProcessMatcher(ApplicationRule rule) =>
        !string.IsNullOrWhiteSpace(rule.ExecutablePath) || !string.IsNullOrWhiteSpace(rule.ProcessName);

    private static string GetRuleSelector(
        IReadOnlyDictionary<(string PolicyId, AddressFamilyKind Family), string> selectors,
        string policyId,
        AddressFamilyKind family,
        string defaultSelector)
    {
        if (selectors.TryGetValue((policyId, family), out var selector) ||
            selectors.TryGetValue((policyId.ToLowerInvariant(), family), out selector))
        {
            return selector;
        }

        // Rules referencing a disabled or removed policy use the selected default policy.
        return defaultSelector;
    }

    private static List<RoutingPolicy> GetPolicies(AppSettings settings)
    {
        var result = new List<RoutingPolicy> { settings.DefaultPolicy };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { settings.DefaultPolicy.Id };
        foreach (var policy in settings.Policies.Where(policy => policy.Enabled))
        {
            if (!string.IsNullOrWhiteSpace(policy.Id) && seen.Add(policy.Id))
            {
                result.Add(policy);
            }
        }

        return result;
    }

    private static TunAddressSet CreateNonConflictingAddressSet(IReadOnlyCollection<NetworkAdapterInfo> adapters)
    {
        var knownPrefixes = GetKnownPrefixes(adapters);

        string ipv4;
        for (var attempt = 0; ; attempt++)
        {
            if (attempt >= 512)
            {
                throw new InvalidOperationException("Could not choose a non-conflicting IPv4 TUN prefix.");
            }

            var range = RandomNumberGenerator.GetInt32(3);
            var firstOctet = range switch { 0 => 172, 1 => 10, _ => 192 };
            var secondOctet = range switch
            {
                0 => 16 + RandomNumberGenerator.GetInt32(16),
                1 => RandomNumberGenerator.GetInt32(256),
                _ => 168
            };
            var thirdOctet = RandomNumberGenerator.GetInt32(256);
            var networkFourthOctet = RandomNumberGenerator.GetInt32(64) * 4;
            var candidateNetwork = new IPAddress(new byte[] { (byte)firstOctet, (byte)secondOctet, (byte)thirdOctet, (byte)networkFourthOctet });
            if (knownPrefixes.Any(prefix => PrefixesOverlap(new NetworkPrefix(candidateNetwork, 30), prefix)))
            {
                continue;
            }

            var firstHost = new IPAddress(new byte[] { (byte)firstOctet, (byte)secondOctet, (byte)thirdOctet, (byte)(networkFourthOctet + 1) });
            ipv4 = $"{firstHost}/{30}";
            break;
        }

        string ipv6;
        Span<byte> ula = stackalloc byte[6];
        for (var attempt = 0; ; attempt++)
        {
            if (attempt >= 512)
            {
                throw new InvalidOperationException("Could not choose a non-conflicting IPv6 ULA TUN prefix.");
            }

            RandomNumberGenerator.Fill(ula);
            var candidateAddress = IPAddress.Parse($"fd{ula[0]:x2}{ula[1]:x2}:{ula[2]:x2}{ula[3]:x2}:{ula[4]:x2}{ula[5]:x2}::");
            if (knownPrefixes.Any(prefix => PrefixesOverlap(new NetworkPrefix(candidateAddress, 126), prefix)))
            {
                continue;
            }

            ipv6 = $"{candidateAddress}1/126";
            break;
        }

        return new TunAddressSet(ipv4, ipv6);
    }

    private static List<NetworkPrefix> GetKnownPrefixes(IReadOnlyCollection<NetworkAdapterInfo> adapters)
    {
        var result = new List<NetworkPrefix>();
        foreach (var adapter in adapters)
        {
            AddAddressEntries(adapter.IPv4Addresses, AddressFamily.InterNetwork, result);
            AddAddressEntries(adapter.IPv4Gateways, AddressFamily.InterNetwork, result);
            AddAddressEntries(adapter.IPv6Addresses, AddressFamily.InterNetworkV6, result);
            AddAddressEntries(adapter.IPv6Gateways, AddressFamily.InterNetworkV6, result);
        }

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        var bits = unicast.Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                        var prefixLength = Math.Clamp(unicast.PrefixLength, 0, bits);
                        result.Add(new NetworkPrefix(unicast.Address, prefixLength));
                    }
                }
                catch (NetworkInformationException)
                {
                    // Interfaces may appear or disappear during a snapshot.
                }
                catch (ObjectDisposedException)
                {
                    // The interface was removed while properties were queried.
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The adapter snapshot still provides host-address collision checks.
        }

        return result;
    }

    private static void AddAddressEntries(
        IEnumerable<string> values,
        AddressFamily family,
        ICollection<NetworkPrefix> destination)
    {
        foreach (var value in values)
        {
            var parts = value.Split('/', StringSplitOptions.TrimEntries);
            if (!IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != family)
            {
                continue;
            }

            var width = family == AddressFamily.InterNetwork ? 32 : 128;
            var prefixLength = parts.Length > 1 && int.TryParse(parts[1], out var parsed)
                ? Math.Clamp(parsed, 0, width)
                : width;
            destination.Add(new NetworkPrefix(address, prefixLength));
        }
    }

    private static bool PrefixesOverlap(NetworkPrefix left, NetworkPrefix right)
    {
        if (left.Address.AddressFamily != right.Address.AddressFamily)
        {
            return false;
        }

        var bits = Math.Min(left.PrefixLength, right.PrefixLength);
        var leftBytes = left.Address.GetAddressBytes();
        var rightBytes = right.Address.GetAddressBytes();
        var completeBytes = bits / 8;
        for (var index = 0; index < completeBytes; index++)
        {
            if (leftBytes[index] != rightBytes[index])
            {
                return false;
            }
        }

        var remainingBits = bits % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (leftBytes[completeBytes] & mask) == (rightBytes[completeBytes] & mask);
    }

    private static string StableSuffix(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static int ReserveEphemeralLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record NetworkPrefix(IPAddress Address, int PrefixLength);
    private sealed record TunAddressSet(string IPv4Address, string IPv6Address);
}
