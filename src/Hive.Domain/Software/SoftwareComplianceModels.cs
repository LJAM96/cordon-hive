namespace Hive.Domain.Software;

public enum ReleaseApprovalState
{
    Draft,
    Approved,
    Withdrawn
}

public enum DetectionConfidence
{
    Verified,
    Partial,
    Unknown
}

public sealed record SoftwareProduct(
    Guid ProductId,
    string Name,
    string Publisher,
    VersionScheme VersionScheme,
    string MatchPattern,
    long DetectionRevision,
    IReadOnlyList<string> ProtectedProcessNames,
    DateTimeOffset CreatedAtUtc);

public sealed record SoftwareRelease(
    Guid ReleaseId,
    Guid ProductId,
    string VersionRaw,
    VersionScheme VersionScheme,
    ReleaseApprovalState ApprovalState,
    string ValidationReference,
    string? PackageSha256,
    string? ManifestDigest,
    string CreatedBySid,
    string? ApprovedBySid,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? WithdrawnAtUtc,
    string? WithdrawnReason);

public sealed record SoftwareExemption(
    Guid ExemptionId,
    Guid? DeviceId,
    Guid ProductId,
    string? AllowedVersionRaw,
    string Reason,
    string ApprovedBySid,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedBySid,
    string? RevokedReason)
{
    public bool IsActiveAt(DateTimeOffset nowUtc) =>
        RevokedAtUtc is null &&
        nowUtc >= StartsAtUtc &&
        nowUtc < ExpiresAtUtc;
}

public sealed record DeviceSoftwareInstallation(
    string DisplayName,
    string? Publisher,
    string? ObservedVersionRaw,
    string DetectionSource,
    string InstallScope,
    DetectionConfidence Confidence,
    DateTimeOffset ObservedAtUtc);

public sealed record ComplianceEvaluationResult(
    ComplianceStatus Status,
    string? ObservedVersionRaw,
    string Reason,
    SoftwareExemption? ActiveExemption);

/// <summary>
/// Evaluates software compliance for a device/product using product-specific version semantics.
/// Unknown, incomplete, conflicting, or unparseable detections never evaluate to Compliant.
/// </summary>
public static class SoftwareComplianceEvaluator
{
    public static bool MatchesProduct(DeviceSoftwareInstallation installation, SoftwareProduct product)
    {
        if (string.IsNullOrWhiteSpace(installation.DisplayName) ||
            string.IsNullOrWhiteSpace(product.MatchPattern))
        {
            return false;
        }

        return installation.DisplayName.Contains(product.MatchPattern.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static ComplianceEvaluationResult Evaluate(
        Guid deviceId,
        SoftwareProduct product,
        SoftwareRelease? activeApprovedRelease,
        IReadOnlyList<DeviceSoftwareInstallation> matchingInstallations,
        bool inventoryIsComplete,
        IReadOnlyList<SoftwareExemption> exemptions,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(matchingInstallations);
        ArgumentNullException.ThrowIfNull(exemptions);

        if (!inventoryIsComplete && matchingInstallations.Count == 0)
        {
            return new ComplianceEvaluationResult(
                ComplianceStatus.Unknown,
                null,
                "Inventory snapshot is incomplete or not yet reported.",
                null);
        }

        var distinctVersions = matchingInstallations
            .Select(i => i.ObservedVersionRaw?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var singleObservedVersion = distinctVersions.Length == 1 ? distinctVersions[0] : null;

        // Check active scoped exemptions (device-specific first, then global).
        var candidateExemptions = exemptions
            .Where(e => e.ProductId == product.ProductId &&
                        (e.DeviceId is null || e.DeviceId == deviceId) &&
                        e.IsActiveAt(nowUtc))
            .OrderByDescending(e => e.DeviceId.HasValue)
            .ThenByDescending(e => e.ExpiresAtUtc)
            .ToArray();

        foreach (var exemption in candidateExemptions)
        {
            if (string.IsNullOrWhiteSpace(exemption.AllowedVersionRaw))
            {
                return new ComplianceEvaluationResult(
                    ComplianceStatus.Exempt,
                    singleObservedVersion,
                    $"Exempt until {exemption.ExpiresAtUtc:u}: {exemption.Reason}",
                    exemption);
            }

            if (singleObservedVersion is not null &&
                SoftwareVersionComparer.Compare(singleObservedVersion, exemption.AllowedVersionRaw, product.VersionScheme) ==
                VersionComparisonOutcome.Equal)
            {
                return new ComplianceEvaluationResult(
                    ComplianceStatus.Exempt,
                    singleObservedVersion,
                    $"Exempt version {singleObservedVersion} until {exemption.ExpiresAtUtc:u}: {exemption.Reason}",
                    exemption);
            }
        }

        if (matchingInstallations.Count == 0)
        {
            if (activeApprovedRelease is null)
            {
                return new ComplianceEvaluationResult(
                    ComplianceStatus.Unknown,
                    null,
                    "Product not installed and no approved target release is configured.",
                    null);
            }

            return new ComplianceEvaluationResult(
                ComplianceStatus.NotInstalled,
                null,
                $"Not installed; approved version is {activeApprovedRelease.VersionRaw}.",
                null);
        }

        if (distinctVersions.Length > 1)
        {
            return new ComplianceEvaluationResult(
                ComplianceStatus.Unknown,
                string.Join(", ", distinctVersions),
                "Multiple conflicting versions detected without an active exemption.",
                null);
        }

        var primary = matchingInstallations[0];
        if (matchingInstallations.Any(i => i.Confidence != DetectionConfidence.Verified) ||
            string.IsNullOrWhiteSpace(singleObservedVersion))
        {
            return new ComplianceEvaluationResult(
                ComplianceStatus.Unknown,
                singleObservedVersion,
                "Detection confidence is partial or version metadata is missing.",
                null);
        }

        if (activeApprovedRelease is null)
        {
            return new ComplianceEvaluationResult(
                ComplianceStatus.Unknown,
                singleObservedVersion,
                "No active approved release target is configured for this product.",
                null);
        }

        var comparison = SoftwareVersionComparer.Compare(
            singleObservedVersion,
            activeApprovedRelease.VersionRaw,
            product.VersionScheme);

        return comparison switch
        {
            VersionComparisonOutcome.Equal => new ComplianceEvaluationResult(
                ComplianceStatus.Compliant,
                singleObservedVersion,
                $"Matches approved version {activeApprovedRelease.VersionRaw}.",
                null),
            VersionComparisonOutcome.Older or VersionComparisonOutcome.NotEqual => new ComplianceEvaluationResult(
                ComplianceStatus.UpdateRequired,
                singleObservedVersion,
                $"Observed {singleObservedVersion}; approved target is {activeApprovedRelease.VersionRaw}.",
                null),
            VersionComparisonOutcome.Newer => new ComplianceEvaluationResult(
                ComplianceStatus.AheadUnapproved,
                singleObservedVersion,
                $"Observed {singleObservedVersion} is ahead of approved target {activeApprovedRelease.VersionRaw}.",
                null),
            _ => new ComplianceEvaluationResult(
                ComplianceStatus.Unknown,
                singleObservedVersion,
                $"Version '{singleObservedVersion}' could not be compared under scheme {product.VersionScheme}.",
                null)
        };
    }
}
