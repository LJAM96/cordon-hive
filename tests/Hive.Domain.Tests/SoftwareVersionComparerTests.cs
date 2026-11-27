using Hive.Domain.Software;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class SoftwareVersionComparerTests
{
    [Theory]
    [InlineData("2.10.0", "2.2.0", VersionComparisonOutcome.Newer)]
    [InlineData("2.2.0", "2.10.0", VersionComparisonOutcome.Older)]
    [InlineData("1.2", "1.2.0", VersionComparisonOutcome.Equal)]
    [InlineData("10.0.19045.5131", "10.0.19045.5131", VersionComparisonOutcome.Equal)]
    [InlineData("1.0.0", "1.0.1", VersionComparisonOutcome.Older)]
    public void NumericSegments_ComparesNumericallyNotLexicographically(
        string observed,
        string target,
        VersionComparisonOutcome expected)
    {
        var outcome = SoftwareVersionComparer.Compare(observed, target, VersionScheme.NumericSegments);
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-beta", VersionComparisonOutcome.Older)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", VersionComparisonOutcome.Newer)]
    [InlineData("1.0.0-beta", "1.0.0", VersionComparisonOutcome.Older)]
    [InlineData("1.0.0", "1.0.0-beta", VersionComparisonOutcome.Newer)]
    [InlineData("1.0.0-1", "1.0.0-alpha", VersionComparisonOutcome.Older)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", VersionComparisonOutcome.Older)]
    [InlineData("1.0.0+build.1", "1.0.0+build.2", VersionComparisonOutcome.Equal)]
    [InlineData("2.10.0", "2.2.0", VersionComparisonOutcome.Newer)]
    public void SemVer_FollowsSemVerPrecedence(
        string observed,
        string target,
        VersionComparisonOutcome expected)
    {
        var outcome = SoftwareVersionComparer.Compare(observed, target, VersionScheme.SemVer);
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("2026.11-R1", "2026.11-R1", VersionComparisonOutcome.Equal)]
    [InlineData("2026.11-r1", "2026.11-R1", VersionComparisonOutcome.Equal)]
    [InlineData("2026.11-R1", "2026.11-R2", VersionComparisonOutcome.NotEqual)]
    public void Exact_MatchesCaseInsensitively(
        string observed,
        string target,
        VersionComparisonOutcome expected)
    {
        var outcome = SoftwareVersionComparer.Compare(observed, target, VersionScheme.Exact);
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData(null, "1.0.0", VersionScheme.SemVer)]
    [InlineData("1.0.0", null, VersionScheme.SemVer)]
    [InlineData("   ", "1.0.0", VersionScheme.NumericSegments)]
    [InlineData("1.0.0", "", VersionScheme.Exact)]
    [InlineData("v1.0.0", "1.0.0", VersionScheme.SemVer)]
    [InlineData("1.0.0-alpha", "1.0.0", VersionScheme.NumericSegments)]
    [InlineData("invalid", "1.0.0", VersionScheme.NumericSegments)]
    [InlineData("1.0", "1.0.0", VersionScheme.SemVer)]
    public void NullOrUnparseable_ReturnsUnknown(
        string? observed,
        string? target,
        VersionScheme scheme)
    {
        var outcome = SoftwareVersionComparer.Compare(observed, target, scheme);
        Assert.Equal(VersionComparisonOutcome.Unknown, outcome);
    }
}
