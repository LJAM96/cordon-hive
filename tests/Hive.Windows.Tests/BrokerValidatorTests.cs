using Hive.Contracts;
using Hive.Installer;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class BrokerValidatorTests
{
    private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void BrokerOptions_IsDisabledByDefault()
    {
        var options = new BrokerOptions();
        Assert.False(options.Enabled);
        Assert.Contains("validated-msi-no-restart", options.AllowedInstallProfiles);
    }

    [Fact]
    public void Validate_RejectsValidRequest_WhenBrokerDisabled()
    {
        var validator = new BrokerCommandValidator(new BrokerOptions());
        var request = new BrokerInstallRequest(Guid.NewGuid(), Guid.NewGuid(), ValidSha256, Guid.NewGuid());

        var result = validator.Validate(request);

        Assert.False(result.Accepted);
        Assert.Equal("BROKER_DISABLED", result.ReasonCode);
    }

    [Theory]
    [InlineData("", "INVALID_MANIFEST_DIGEST")]
    [InlineData("not-hex-64-chars-long-000000000000000000000000000000000000000000", "INVALID_MANIFEST_DIGEST")]
    [InlineData("0123456789abcdef", "INVALID_MANIFEST_DIGEST")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00", "INVALID_MANIFEST_DIGEST")]
    public void Validate_RejectsMalformedManifestDigest_WhenEnabled(string digest, string expectedCode)
    {
        var validator = new BrokerCommandValidator(new BrokerOptions { Enabled = true });
        var request = new BrokerInstallRequest(Guid.NewGuid(), Guid.NewGuid(), digest, Guid.NewGuid());

        var result = validator.Validate(request);

        Assert.False(result.Accepted);
        Assert.Equal(expectedCode, result.ReasonCode);
    }

    [Fact]
    public void Validate_RejectsEmptyGuidsAndDisallowedProfile_WhenEnabled()
    {
        var validator = new BrokerCommandValidator(new BrokerOptions { Enabled = true });

        Assert.Equal("INVALID_REQUEST", validator.Validate(null).ReasonCode);
        Assert.Equal("INVALID_TARGET_ID", validator.Validate(new BrokerInstallRequest(Guid.Empty, Guid.NewGuid(), ValidSha256, Guid.NewGuid())).ReasonCode);
        Assert.Equal("INVALID_RELEASE_ID", validator.Validate(new BrokerInstallRequest(Guid.NewGuid(), Guid.Empty, ValidSha256, Guid.NewGuid())).ReasonCode);
        Assert.Equal("INVALID_LEASE_ID", validator.Validate(new BrokerInstallRequest(Guid.NewGuid(), Guid.NewGuid(), ValidSha256, Guid.Empty)).ReasonCode);

        var validReq = new BrokerInstallRequest(Guid.NewGuid(), Guid.NewGuid(), ValidSha256, Guid.NewGuid());
        var badProfile = validator.Validate(validReq, installProfile: "arbitrary-powershell");
        Assert.False(badProfile.Accepted);
        Assert.Equal("DISALLOWED_INSTALL_PROFILE", badProfile.ReasonCode);

        var accepted = validator.Validate(validReq);
        Assert.True(accepted.Accepted);
        Assert.Equal("VALIDATED", accepted.ReasonCode);
    }
}
