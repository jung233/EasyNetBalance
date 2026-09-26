using EasyBalance.Service.Health;
using EasyBalance.Shared;
using Xunit;

namespace EasyBalance.Tests;

public sealed class HealthStateMachineTests
{
    [Fact]
    public void FailureAndRecoveryRequireThresholdAndStabilization()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new HealthStateMachine();
        machine.Observe(true, 3, 3, TimeSpan.FromSeconds(10), now);
        Assert.Equal(HealthState.Healthy, machine.State);
        machine.Observe(false, 3, 3, TimeSpan.FromSeconds(10), now.AddSeconds(1));
        Assert.Equal(HealthState.Suspect, machine.State);
        machine.Observe(false, 3, 3, TimeSpan.FromSeconds(10), now.AddSeconds(2));
        machine.Observe(false, 3, 3, TimeSpan.FromSeconds(10), now.AddSeconds(3));
        Assert.Equal(HealthState.Down, machine.State);
        machine.Observe(true, 3, 3, TimeSpan.FromSeconds(10), now.AddSeconds(4));
        machine.Observe(true, 3, 3, TimeSpan.FromSeconds(10), now.AddSeconds(5));
        Assert.Equal(HealthState.Down, machine.State);
        machine.Observe(true, 3, 3, TimeSpan.FromSeconds(10), now.AddSeconds(6));
        Assert.Equal(HealthState.Recovering, machine.State);
        machine.Observe(true, 3, 3, TimeSpan.FromSeconds(10), now.AddSeconds(17));
        Assert.Equal(HealthState.Healthy, machine.State);
    }

    [Fact]
    public void AddressFamiliesHaveIndependentPolicyDecisions()
    {
        var policy = new RoutingPolicy
        {
            PrimaryInterfaceId = "ethernet", FallbackInterfaceId = "wifi",
            MinimumSwitchHoldTime = TimeSpan.FromSeconds(15)
        };
        var v4 = new PolicyFamilyRuntime();
        var v6 = new PolicyFamilyRuntime();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("wifi", v4.Decide(policy, HealthState.Down, HealthState.Healthy, now));
        Assert.Null(v6.Decide(policy, HealthState.Healthy, HealthState.Healthy, now));
        v4.ConfirmSwitch("wifi", now);
        Assert.Null(v4.Decide(policy, HealthState.Healthy, HealthState.Healthy, now.AddSeconds(5)));
        Assert.Equal("ethernet", v4.Decide(policy, HealthState.Healthy, HealthState.Healthy, now.AddSeconds(16)));
    }

    [Fact]
    public void BothDownKeepsCurrentInterface()
    {
        var policy = new RoutingPolicy { PrimaryInterfaceId = "a", FallbackInterfaceId = "b" };
        var runtime = new PolicyFamilyRuntime();
        Assert.Null(runtime.Decide(policy, HealthState.Down, HealthState.Down, DateTimeOffset.UtcNow));
        Assert.True(runtime.NoHealthyInterface);
    }

    [Fact]
    public void AutoFailbackDisabledKeepsFallbackUntilItFails()
    {
        var policy = new RoutingPolicy
        {
            PrimaryInterfaceId = "a", FallbackInterfaceId = "b", AutoFailback = false
        };
        var runtime = new PolicyFamilyRuntime();
        var now = DateTimeOffset.UtcNow;
        runtime.ConfirmSwitch("b", now);
        Assert.Null(runtime.Decide(policy, HealthState.Healthy, HealthState.Healthy, now.AddMinutes(1)));
        Assert.Equal("a", runtime.Decide(policy, HealthState.Healthy, HealthState.Unavailable, now.AddMinutes(1)));
    }

    [Fact]
    public void MissingPrimaryImmediatelySelectsHealthyFallback()
    {
        var policy = new RoutingPolicy { PrimaryInterfaceId = "a", FallbackInterfaceId = "b" };
        var runtime = new PolicyFamilyRuntime();
        Assert.Equal("b", runtime.Decide(policy, HealthState.Unavailable, HealthState.Healthy, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PrimaryOnlyCannotSwitchAndReportsOutage()
    {
        var policy = new RoutingPolicy { PrimaryInterfaceId = "a" };
        var runtime = new PolicyFamilyRuntime();
        Assert.Null(runtime.Decide(policy, HealthState.Down, HealthState.Unavailable, DateTimeOffset.UtcNow));
        Assert.True(runtime.NoHealthyInterface);
    }
}
