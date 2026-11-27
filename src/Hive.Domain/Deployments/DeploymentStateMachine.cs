namespace Hive.Domain.Deployments;

public enum DeploymentState
{
    Pending,
    Downloading,
    Staged,
    Deferred,
    Installing,
    Verifying,
    Verified,
    PendingReboot,
    Failed,
    Unknown,
    Cancelled
}

/// <summary>
/// Enforces legal target deployment transitions per docs/03_Implementation/SOFTWARE_MANAGEMENT.md.
/// </summary>
public static class DeploymentStateMachine
{
    private static readonly IReadOnlyDictionary<DeploymentState, DeploymentState[]> AllowedTransitions =
        new Dictionary<DeploymentState, DeploymentState[]>
        {
            [DeploymentState.Pending] = [DeploymentState.Downloading, DeploymentState.Cancelled, DeploymentState.Failed],
            [DeploymentState.Downloading] = [DeploymentState.Staged, DeploymentState.Deferred, DeploymentState.Failed, DeploymentState.Unknown],
            [DeploymentState.Staged] = [DeploymentState.Deferred, DeploymentState.Installing, DeploymentState.Cancelled, DeploymentState.Failed],
            [DeploymentState.Deferred] = [DeploymentState.Staged, DeploymentState.Cancelled, DeploymentState.Failed],
            [DeploymentState.Installing] = [DeploymentState.Verifying, DeploymentState.Unknown, DeploymentState.Failed],
            [DeploymentState.Verifying] = [DeploymentState.Verified, DeploymentState.PendingReboot, DeploymentState.Failed],
            [DeploymentState.PendingReboot] = [DeploymentState.Verifying, DeploymentState.Failed],
            [DeploymentState.Unknown] = [DeploymentState.Verifying, DeploymentState.Failed],
            [DeploymentState.Verified] = [],
            [DeploymentState.Failed] = [],
            [DeploymentState.Cancelled] = []
        };

    public static bool CanTransition(DeploymentState from, DeploymentState to) =>
        AllowedTransitions.TryGetValue(from, out var next) && next.Contains(to);

    public static DeploymentState Transition(DeploymentState from, DeploymentState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Illegal deployment state transition from {from} to {to}.");
        }

        return to;
    }
}
