using Hive.Domain.Devices;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class DeviceConnectivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NullLastSeenUtc_ReturnsOffline()
    {
        var state = DeviceConnectivityEvaluator.Evaluate(null, Now);
        Assert.Equal(ConnectivityState.Offline, state);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(44)]
    public void UnderOnlineThreshold_ReturnsOnline(int ageSeconds)
    {
        var lastSeen = Now.AddSeconds(-ageSeconds);
        var state = DeviceConnectivityEvaluator.Evaluate(lastSeen, Now);
        Assert.Equal(ConnectivityState.Online, state);
    }

    [Fact]
    public void FutureLastSeenClockSkew_ReturnsOnline()
    {
        var lastSeen = Now.AddSeconds(5);
        var state = DeviceConnectivityEvaluator.Evaluate(lastSeen, Now);
        Assert.Equal(ConnectivityState.Online, state);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(75)]
    [InlineData(120)]
    public void BetweenOnlineAndStaleThresholdsInclusive_ReturnsStale(int ageSeconds)
    {
        var lastSeen = Now.AddSeconds(-ageSeconds);
        var state = DeviceConnectivityEvaluator.Evaluate(lastSeen, Now);
        Assert.Equal(ConnectivityState.Stale, state);
    }

    [Theory]
    [InlineData(121)]
    [InlineData(300)]
    [InlineData(86400)]
    public void OverStaleThreshold_ReturnsOffline(int ageSeconds)
    {
        var lastSeen = Now.AddSeconds(-ageSeconds);
        var state = DeviceConnectivityEvaluator.Evaluate(lastSeen, Now);
        Assert.Equal(ConnectivityState.Offline, state);
    }

    [Fact]
    public void ExactlyOver120SecondsByMillisecond_ReturnsOffline()
    {
        var lastSeen = Now - TimeSpan.FromSeconds(120) - TimeSpan.FromMilliseconds(1);
        var state = DeviceConnectivityEvaluator.Evaluate(lastSeen, Now);
        Assert.Equal(ConnectivityState.Offline, state);
    }
}
