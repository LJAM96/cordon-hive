using Hive.Domain.Deployments;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class DeploymentStateMachineTests
{
    [Theory]
    [InlineData(DeploymentState.Pending, DeploymentState.Downloading)]
    [InlineData(DeploymentState.Pending, DeploymentState.Cancelled)]
    [InlineData(DeploymentState.Pending, DeploymentState.Failed)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Staged)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Deferred)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Unknown)]
    [InlineData(DeploymentState.Staged, DeploymentState.Installing)]
    [InlineData(DeploymentState.Staged, DeploymentState.Deferred)]
    [InlineData(DeploymentState.Deferred, DeploymentState.Staged)]
    [InlineData(DeploymentState.Deferred, DeploymentState.Cancelled)]
    [InlineData(DeploymentState.Installing, DeploymentState.Verifying)]
    [InlineData(DeploymentState.Installing, DeploymentState.Unknown)]
    [InlineData(DeploymentState.Verifying, DeploymentState.Verified)]
    [InlineData(DeploymentState.Verifying, DeploymentState.PendingReboot)]
    [InlineData(DeploymentState.PendingReboot, DeploymentState.Verifying)]
    [InlineData(DeploymentState.Unknown, DeploymentState.Verifying)]
    [InlineData(DeploymentState.Unknown, DeploymentState.Failed)]
    public void LegalTransitions_Succeed(DeploymentState from, DeploymentState to)
    {
        Assert.True(DeploymentStateMachine.CanTransition(from, to));
        Assert.Equal(to, DeploymentStateMachine.Transition(from, to));
    }

    [Theory]
    [InlineData(DeploymentState.Pending, DeploymentState.Verified)]
    [InlineData(DeploymentState.Pending, DeploymentState.Installing)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Installing)]
    [InlineData(DeploymentState.Cancelled, DeploymentState.Installing)]
    [InlineData(DeploymentState.Cancelled, DeploymentState.Pending)]
    [InlineData(DeploymentState.Failed, DeploymentState.Staged)]
    [InlineData(DeploymentState.Failed, DeploymentState.Verifying)]
    [InlineData(DeploymentState.Verified, DeploymentState.Failed)]
    [InlineData(DeploymentState.Verified, DeploymentState.Pending)]
    [InlineData(DeploymentState.Staged, DeploymentState.Verified)]
    public void IllegalTransitions_ThrowInvalidOperationException(DeploymentState from, DeploymentState to)
    {
        Assert.False(DeploymentStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => DeploymentStateMachine.Transition(from, to));
    }
}
