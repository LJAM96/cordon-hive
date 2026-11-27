using Hive.Contracts;
using Microsoft.Extensions.Options;

namespace Hive.Installer;

/// <summary>
/// Validates restricted installer broker requests.
/// Phase 1 performs strict input and policy validation only (no Process.Start or shell execution).
/// </summary>
public sealed class BrokerCommandValidator
{
    private readonly BrokerOptions _options;

    public BrokerCommandValidator(IOptions<BrokerOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public BrokerCommandValidator(BrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public BrokerInstallResult Validate(
        BrokerInstallRequest? request,
        string installProfile = "validated-msi-no-restart")
    {
        if (!_options.Enabled)
        {
            return new BrokerInstallResult(
                Accepted: false,
                ReasonCode: "BROKER_DISABLED",
                Message: "Installer broker is disabled by default configuration.");
        }

        if (request is null)
        {
            return new BrokerInstallResult(
                Accepted: false,
                ReasonCode: "INVALID_REQUEST",
                Message: "Broker install request must not be null.");
        }

        if (request.TargetId == Guid.Empty)
        {
            return new BrokerInstallResult(
                Accepted: false,
                ReasonCode: "INVALID_TARGET_ID",
                Message: "TargetId must be a non-empty GUID.");
        }

        if (request.ReleaseId == Guid.Empty)
        {
            return new BrokerInstallResult(
                Accepted: false,
                ReasonCode: "INVALID_RELEASE_ID",
                Message: "ReleaseId must be a non-empty GUID.");
        }

        if (request.LeaseId == Guid.Empty)
        {
            return new BrokerInstallResult(
                Accepted: false,
                ReasonCode: "INVALID_LEASE_ID",
                Message: "LeaseId must be a non-empty GUID.");
        }

        if (!IsValidSha256Hex(request.ManifestDigest))
        {
            return new BrokerInstallResult(
                Accepted: false,
                ReasonCode: "INVALID_MANIFEST_DIGEST",
                Message: "ManifestDigest must be a valid 64-character hexadecimal SHA-256 digest.");
        }

        if (string.IsNullOrWhiteSpace(installProfile) ||
            !_options.AllowedInstallProfiles.Contains(installProfile, StringComparer.OrdinalIgnoreCase))
        {
            return new BrokerInstallResult(
                Accepted: false,
                ReasonCode: "DISALLOWED_INSTALL_PROFILE",
                Message: "The specified install profile is not permitted by broker policy.");
        }

        return new BrokerInstallResult(
            Accepted: true,
            ReasonCode: "VALIDATED",
            Message: "Broker install request passed Phase 1 validation.");
    }

    public static bool IsValidSha256Hex(string? digest)
    {
        if (string.IsNullOrEmpty(digest) || digest.Length != 64)
        {
            return false;
        }

        foreach (var ch in digest)
        {
            var isHex = (ch >= '0' && ch <= '9') ||
                        (ch >= 'a' && ch <= 'f') ||
                        (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
