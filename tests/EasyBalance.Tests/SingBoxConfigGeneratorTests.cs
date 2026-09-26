using System.Text.Json;
using EasyBalance.Service.Core;
using EasyBalance.Shared;
using Xunit;

namespace EasyBalance.Tests;

public sealed class SingBoxConfigGeneratorTests
{
    [Fact]
    public void SharedPoliciesCreateOnlyOneSelectorPerAddressFamily()
    {
        var primaryId = Guid.Parse("a28a8d67-98e6-4e7b-9dbd-c0e57d91a10a").ToString("D");
        var fallbackId = Guid.Parse("7bd60e7c-234f-423a-a7bc-c949069e1b74").ToString("D");
        var defaultPolicy = new RoutingPolicy
        {
            Id = "default-policy",
            Name = "Default",
            PrimaryInterfaceId = primaryId
        };
        var sharedPolicy = new RoutingPolicy
        {
            Id = "shared-policy",
            Name = "Shared",
            PrimaryInterfaceId = primaryId,
            FallbackInterfaceId = fallbackId
        };
        var duplicatePolicy = new RoutingPolicy
        {
            Id = sharedPolicy.Id,
            Name = "Duplicate entry",
            PrimaryInterfaceId = primaryId,
            FallbackInterfaceId = fallbackId
        };
        var settings = new AppSettings
        {
            Ipv6Enabled = true,
            DefaultPolicy = defaultPolicy,
            Policies = [sharedPolicy, duplicatePolicy],
            ApplicationRules =
            [
                new() { Id = "rule-a", PolicyId = sharedPolicy.Id, ProcessName = "game-a.exe" },
                new() { Id = "rule-b", PolicyId = sharedPolicy.Id, ProcessName = "game-b.exe" }
            ]
        };

        using var document = Generate(settings, Adapter(primaryId, "Ethernet"), Adapter(fallbackId, "Wi-Fi"));
        var selectors = Selectors(document.RootElement);

        Assert.Equal(4, selectors.Length); // Default and Shared, each with IPv4 and IPv6.
        Assert.Single(selectors.Where(selector => Tag(selector) ==
            SingBoxConfigGenerator.SelectorTag(sharedPolicy.Id, AddressFamilyKind.IPv4)));
        Assert.Single(selectors.Where(selector => Tag(selector) ==
            SingBoxConfigGenerator.SelectorTag(sharedPolicy.Id, AddressFamilyKind.IPv6)));

        var appRules = document.RootElement.GetProperty("route").GetProperty("rules")
            .EnumerateArray()
            .Where(rule => rule.TryGetProperty("process_name", out _))
            .ToArray();
        Assert.Contains(appRules, rule => rule.GetProperty("outbound").GetString() ==
            SingBoxConfigGenerator.SelectorTag(sharedPolicy.Id, AddressFamilyKind.IPv4));
        Assert.Contains(appRules, rule => rule.GetProperty("outbound").GetString() ==
            SingBoxConfigGenerator.SelectorTag(sharedPolicy.Id, AddressFamilyKind.IPv6));
    }

    [Fact]
    public void ExecutablePathRulesPrecedeGenericRulesAndPriorityOrdersEachGroup()
    {
        var adapterId = Guid.Parse("bf10cb5a-7d35-4cd2-9ab9-78bfa0aa08a4").ToString("D");
        var policy = new RoutingPolicy { Id = "default-policy", PrimaryInterfaceId = adapterId };
        var settings = new AppSettings
        {
            DefaultPolicy = policy,
            Ipv6Enabled = false,
            ApplicationRules =
            [
                new()
                {
                    Id = "generic-low",
                    Priority = 10,
                    PolicyId = policy.Id,
                    ProcessName = "discord.exe"
                },
                new()
                {
                    Id = "path-specific",
                    Priority = 1,
                    PolicyId = policy.Id,
                    ExecutablePath = @"C:\Games\steam.exe",
                    ProcessName = "steam.exe"
                },
                new()
                {
                    Id = "generic-high",
                    Priority = 100,
                    PolicyId = policy.Id,
                    ProcessName = "steam.exe"
                }
            ]
        };

        using var document = Generate(settings, Adapter(adapterId, "Campus LAN"));
        var rules = document.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
            .Where(rule => rule.TryGetProperty("process_path", out _) || rule.TryGetProperty("process_name", out _))
            .ToArray();

        Assert.Equal(3, rules.Length);
        Assert.Equal(4, rules[0].GetProperty("ip_version").GetInt32());
        Assert.Equal(@"C:\Games\steam.exe", rules[0].GetProperty("process_path")[0].GetString());
        Assert.False(rules[0].TryGetProperty("process_name", out _));
        Assert.Equal("steam.exe", rules[1].GetProperty("process_name")[0].GetString());
        Assert.Equal("discord.exe", rules[2].GetProperty("process_name")[0].GetString());
    }

    [Fact]
    public void InterfaceGuidResolvesToItsCurrentFriendlyName()
    {
        var interfaceId = Guid.Parse("2073d117-4bd2-42dc-a803-5c5786fa6019").ToString("D");
        var settings = new AppSettings
        {
            Ipv6Enabled = false,
            DefaultPolicy = new RoutingPolicy
            {
                Id = "default-policy",
                PrimaryInterfaceId = interfaceId
            }
        };

        using var document = Generate(settings, Adapter(interfaceId, "Campus LAN"));
        var directOutbound = document.RootElement.GetProperty("outbounds").EnumerateArray()
            .Single(outbound => outbound.GetProperty("type").GetString() == "direct");

        Assert.Equal(SingBoxConfigGenerator.OutboundTag(interfaceId), directOutbound.GetProperty("tag").GetString());
        Assert.Equal("Campus LAN", directOutbound.GetProperty("bind_interface").GetString());
    }

    [Fact]
    public void IPv4AndIPv6UseSeparateSelectorsAndApplicationRoutes()
    {
        var primaryId = Guid.Parse("a7698cf2-dd67-40a0-aebf-bbce8998e474").ToString("D");
        var fallbackId = Guid.Parse("1a28fdc6-b702-4b09-8d17-9c52666c5f84").ToString("D");
        var policy = new RoutingPolicy
        {
            Id = "dual-stack-policy",
            PrimaryInterfaceId = primaryId,
            FallbackInterfaceId = fallbackId
        };
        var settings = new AppSettings
        {
            DefaultPolicy = policy,
            Ipv6Enabled = true,
            ApplicationRules =
            [new() { Id = "dual-stack-rule", PolicyId = policy.Id, ProcessName = "browser.exe" }]
        };

        using var document = Generate(settings, Adapter(primaryId, "Ethernet"), Adapter(fallbackId, "Wi-Fi"));
        var selectors = Selectors(document.RootElement);
        var ipv4Tag = SingBoxConfigGenerator.SelectorTag(policy.Id, AddressFamilyKind.IPv4);
        var ipv6Tag = SingBoxConfigGenerator.SelectorTag(policy.Id, AddressFamilyKind.IPv6);
        var ipv4Selector = selectors.Single(selector => Tag(selector) == ipv4Tag);
        var ipv6Selector = selectors.Single(selector => Tag(selector) == ipv6Tag);

        Assert.NotEqual(ipv4Tag, ipv6Tag);
        Assert.Equal(SingBoxConfigGenerator.OutboundTag(primaryId), ipv4Selector.GetProperty("default").GetString());
        Assert.Equal(SingBoxConfigGenerator.OutboundTag(primaryId), ipv6Selector.GetProperty("default").GetString());

        var appRules = document.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
            .Where(rule => rule.TryGetProperty("process_name", out _))
            .ToArray();
        Assert.Equal(2, appRules.Length);
        Assert.Equal(4, appRules[0].GetProperty("ip_version").GetInt32());
        Assert.Equal(ipv4Tag, appRules[0].GetProperty("outbound").GetString());
        Assert.Equal(6, appRules[1].GetProperty("ip_version").GetInt32());
        Assert.Equal(ipv6Tag, appRules[1].GetProperty("outbound").GetString());
    }

    private static JsonDocument Generate(AppSettings settings, params NetworkAdapterInfo[] adapters) =>
        JsonDocument.Parse(new SingBoxConfigGenerator().Generate(settings, adapters, CompatibleCapabilities()).Json);

    private static SingBoxCapabilities CompatibleCapabilities() => new()
    {
        VersionText = "1.11.0",
        Version = new Version(1, 11, 0),
        BuildTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "with_clash_api" }
    };

    private static NetworkAdapterInfo Adapter(string id, string name) => new()
    {
        Id = id,
        Name = name,
        IsPhysical = true,
        IsUserAllowed = true
    };

    private static JsonElement[] Selectors(JsonElement root) => root.GetProperty("outbounds")
        .EnumerateArray()
        .Where(outbound => outbound.GetProperty("type").GetString() == "selector")
        .ToArray();

    private static string? Tag(JsonElement selector) => selector.GetProperty("tag").GetString();
}
