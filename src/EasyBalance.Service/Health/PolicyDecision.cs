using EasyBalance.Shared;

namespace EasyBalance.Service.Health;

public sealed class PolicyFamilyRuntime
{
    public string? ActiveInterfaceId { get; private set; }
    public DateTimeOffset LastSwitch { get; private set; } = DateTimeOffset.MinValue;
    public bool NoHealthyInterface { get; private set; }

    public string? Decide(RoutingPolicy policy, HealthState primary, HealthState fallback, DateTimeOffset now)
    {
        var primaryId = policy.PrimaryInterfaceId;
        var fallbackId = policy.FallbackInterfaceId;
        ActiveInterfaceId ??= primaryId;
        var primaryGood = primary == HealthState.Healthy;
        var fallbackGood = fallback == HealthState.Healthy;
        NoHealthyInterface = !primaryGood && !fallbackGood;
        if (NoHealthyInterface) return null;

        if (ActiveInterfaceId == primaryId)
        {
            if (policy.FailoverEnabled && (primary is HealthState.Down or HealthState.Unavailable) && fallbackGood && fallbackId is not null)
                return fallbackId;
            return null;
        }

        if (!fallbackGood && primaryGood) return primaryId;
        if (policy.AutoFailback && primaryGood && now - LastSwitch >= policy.MinimumSwitchHoldTime)
            return primaryId;
        return null;
    }

    public void ConfirmSwitch(string interfaceId, DateTimeOffset now)
    {
        ActiveInterfaceId = interfaceId;
        LastSwitch = now;
        NoHealthyInterface = false;
    }

    public void Initialize(string interfaceId)
    {
        ActiveInterfaceId ??= interfaceId;
    }
}
