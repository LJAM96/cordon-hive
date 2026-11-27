using Hive.Windows.Inventory;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class SoftwareInventoryCollectorTests
{
    [Fact]
    public async Task CollectAsync_LiveRegistryDiscovery_CompletesCleanlyAndReturnsMachineItems()
    {
        var collector = new WindowsSoftwareInventoryCollector();

        var snapshot = await collector.CollectAsync(detectionRevision: 7);

        Assert.Equal(7, snapshot.DetectionRevision);
        Assert.True(snapshot.IsComplete);
        Assert.NotEmpty(snapshot.Items);
        Assert.Contains(snapshot.Items, item => string.Equals(item.InstallScope, "Machine", StringComparison.Ordinal));

        var dedupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            Assert.Contains(item.ConfidenceState, new[] { "Verified", "Partial", "Unknown" });
            Assert.True(
                dedupKeys.Add($"{item.DisplayName}|{item.ObservedVersionRaw ?? string.Empty}|{item.DetectionSource}|{item.InstallScope}"),
                $"Duplicate inventory item found: {item.DisplayName} ({item.DetectionSource})");
        }
    }

    [Fact]
    public async Task CollectAsync_PortableAppRules_DetectsCopiedBinaryAndIgnoresMissingPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"hive-portable-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourceBinary = typeof(object).Assembly.Location;
            Assert.True(File.Exists(sourceBinary));

            var targetBinary = Path.Combine(tempDir, "PortableProbe.dll");
            File.Copy(sourceBinary, targetBinary, overwrite: true);

            var missingBinary = Path.Combine(tempDir, "DoesNotExist.exe");

            var collector = new WindowsSoftwareInventoryCollector();
            var options = new SoftwareInventoryOptions(
                IncludeCurrentUserRegistry: false,
                PortableApps:
                [
                    new PortableAppRule("PortableProbe", targetBinary, "TestPublisher"),
                    new PortableAppRule("MissingTool", missingBinary, "TestPublisher")
                ]);

            var snapshot = await collector.CollectAsync(detectionRevision: 12, options: options);

            var detected = Assert.Single(
                snapshot.Items,
                item => string.Equals(item.DisplayName, "PortableProbe", StringComparison.Ordinal));

            Assert.Equal("PortableFile", detected.DetectionSource);
            Assert.Equal("Portable", detected.InstallScope);
            Assert.Equal("TestPublisher", detected.Publisher);
            Assert.Equal("Verified", detected.ConfidenceState);
            Assert.False(string.IsNullOrWhiteSpace(detected.ObservedVersionRaw));

            Assert.DoesNotContain(
                snapshot.Items,
                item => string.Equals(item.DisplayName, "MissingTool", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Vendor.AppName_2.5.0.0_x64__8wekyb3d8bbwe", "Vendor.AppName", "2.5.0.0", "Verified")]
    [InlineData("My_Forensic_Tool_1.2.3.4_x64__8wekyb3d8bbwe", "My_Forensic_Tool", "1.2.3.4", "Verified")]
    [InlineData("Contoso.Toolkit_3.0.1.0_arm64_neutral_8wekyb3d8bbwe", "Contoso.Toolkit", "3.0.1.0", "Verified")]
    public void ParseMsixFullName_ValidNames_ParsesDisplayNameVersionAndVerifiedConfidence(
        string fullName,
        string expectedName,
        string expectedVersion,
        string expectedConfidence)
    {
        var (displayName, version, confidence) = WindowsSoftwareInventoryCollector.ParseMsixFullName(fullName);

        Assert.Equal(expectedName, displayName);
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedConfidence, confidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoSegmentsAtAll")]
    [InlineData("Only_Three_Parts")]
    [InlineData("Vendor.App_NotAVersion_x64__8wekyb3d8bbwe")]
    [InlineData("Vendor.App_1.0.0.0_badarch__8wekyb3d8bbwe")]
    [InlineData("_1.0.0.0_x64__8wekyb3d8bbwe")]
    [InlineData("Vendor.App_1.0.0.0_x64__")]
    public void ParseMsixFullName_MalformedNames_ReturnsUnknownWithNullVersion(string fullName)
    {
        var (_, version, confidence) = WindowsSoftwareInventoryCollector.ParseMsixFullName(fullName);

        Assert.Null(version);
        Assert.Equal("Unknown", confidence);
    }
}
