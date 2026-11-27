using Hive.Domain.Telemetry;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class StorageWarningEvaluatorTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 10L * GiB)]
    [InlineData(100L * GiB, -1L)]
    public void InvalidCapacity_ReturnsUnknown(long totalBytes, long freeBytes)
    {
        var level = StorageWarningEvaluator.Evaluate(totalBytes, freeBytes);
        Assert.Equal(StorageHealthLevel.Unknown, level);
    }

    [Theory]
    [InlineData(100L * GiB, 25L * GiB)]
    [InlineData(200L * GiB, 20L * GiB)] // Exactly 10% and 20 GiB
    [InlineData(1000L * GiB, 100L * GiB)] // Exactly 10% and 100 GiB
    public void AboveWarningThresholds_ReturnsHealthy(long totalBytes, long freeBytes)
    {
        var level = StorageWarningEvaluator.Evaluate(totalBytes, freeBytes);
        Assert.Equal(StorageHealthLevel.Healthy, level);
    }

    [Theory]
    [InlineData(500L * GiB, 49L * GiB)] // 9.8% (<10%), 49 GiB (>=20 GiB)
    [InlineData(500L * GiB, 25L * GiB)] // 5.0% (not <5%), 25 GiB (>=20 GiB)
    [InlineData(100L * GiB, 19L * GiB)] // 19% (>=10%), 19 GiB (<20 GiB)
    [InlineData(100L * GiB, 5L * GiB)]  // 5.0% (not <5%), 5 GiB (not <5 GiB)
    public void BelowWarningThresholdsAboveCritical_ReturnsWarning(long totalBytes, long freeBytes)
    {
        var level = StorageWarningEvaluator.Evaluate(totalBytes, freeBytes);
        Assert.Equal(StorageHealthLevel.Warning, level);
    }

    [Theory]
    [InlineData(500L * GiB, 24L * GiB)]       // 4.8% (<5%), though 24 GiB >= 5 GiB
    [InlineData(40L * GiB, 4L * GiB)]         // 10% (>=5%), but 4 GiB (<5 GiB)
    [InlineData(100L * GiB, 5L * GiB - 1)]    // 1 byte below 5 GiB
    [InlineData(100L * GiB, 0L)]              // 0 free
    public void BelowCriticalThresholds_ReturnsCritical(long totalBytes, long freeBytes)
    {
        var level = StorageWarningEvaluator.Evaluate(totalBytes, freeBytes);
        Assert.Equal(StorageHealthLevel.Critical, level);
    }

    [Fact]
    public void FreeBytesExceedingTotalBytes_ClampedBeforeEvaluation()
    {
        // 100 GiB total with 200 GiB reported free -> clamped to 100% free (Healthy)
        var level = StorageWarningEvaluator.Evaluate(100L * GiB, 200L * GiB);
        Assert.Equal(StorageHealthLevel.Healthy, level);
    }
}
