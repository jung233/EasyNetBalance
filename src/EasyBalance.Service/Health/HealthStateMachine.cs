using EasyBalance.Shared;

namespace EasyBalance.Service.Health;

/// <summary>One state machine per adapter and address family. It has no network dependencies.</summary>
public sealed class HealthStateMachine
{
    public HealthState State { get; private set; } = HealthState.Unknown;
    public int ConsecutiveFailures { get; private set; }
    public int ConsecutiveSuccesses { get; private set; }
    public DateTimeOffset? RecoveringSince { get; private set; }
    public DateTimeOffset LastChanged { get; private set; } = DateTimeOffset.UtcNow;

    public void Reset(DateTimeOffset now)
    {
        State = HealthState.Unknown;
        ConsecutiveFailures = 0;
        ConsecutiveSuccesses = 0;
        RecoveringSince = null;
        LastChanged = now;
    }

    public void MarkUnavailable(DateTimeOffset now) => Change(HealthState.Unavailable, now);

    public HealthState Observe(bool success, int failureThreshold, int recoveryThreshold,
        TimeSpan stabilization, DateTimeOffset now)
    {
        if (success)
        {
            ConsecutiveFailures = 0;
            ConsecutiveSuccesses++;
            if (State is HealthState.Unknown or HealthState.Unavailable)
                Change(HealthState.Healthy, now);
            else if ((State is HealthState.Down or HealthState.Suspect) && ConsecutiveSuccesses >= recoveryThreshold)
            {
                RecoveringSince = now;
                Change(HealthState.Recovering, now);
            }
            else if (State == HealthState.Recovering && RecoveringSince is { } since && now - since >= stabilization)
                Change(HealthState.Healthy, now);
        }
        else
        {
            ConsecutiveSuccesses = 0;
            RecoveringSince = null;
            ConsecutiveFailures++;
            if (ConsecutiveFailures >= failureThreshold)
                Change(HealthState.Down, now);
            else if (State != HealthState.Down)
                Change(HealthState.Suspect, now);
        }
        return State;
    }

    private void Change(HealthState next, DateTimeOffset now)
    {
        if (State != next)
        {
            State = next;
            LastChanged = now;
        }
        if (next == HealthState.Unavailable)
        {
            ConsecutiveFailures = 0;
            ConsecutiveSuccesses = 0;
            RecoveringSince = null;
        }
    }
}
