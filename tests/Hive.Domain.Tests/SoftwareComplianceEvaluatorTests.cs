using Hive.Domain.Identity;
using Hive.Domain.Software;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class SoftwareComplianceEvaluatorTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 27, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid DeviceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherDeviceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProductId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SoftwareProduct CreateProduct(
        VersionScheme scheme = VersionScheme.SemVer,
        string matchPattern = "Autopsy") =>
        new(
            ProductId,
            "Autopsy Forensic Browser",
            "Basis Technology",
            scheme,
            matchPattern,
            DetectionRevision: 1,
            ProtectedProcessNames: ["autopsy64.exe"],
            CreatedAtUtc: NowUtc.AddDays(-30));

    private static SoftwareRelease CreateApprovedRelease(
        string versionRaw = "4.21.0",
        VersionScheme scheme = VersionScheme.SemVer) =>
        new(
            Guid.NewGuid(),
            ProductId,
            versionRaw,
            scheme,
            ReleaseApprovalState.Approved,
            "VAL-2026-001",
            PackageSha256: null,
            ManifestDigest: null,
            CreatedBySid: "S-1-5-21-100",
            ApprovedBySid: "S-1-5-21-200",
            CreatedAtUtc: NowUtc.AddDays(-10),
            ApprovedAtUtc: NowUtc.AddDays(-9),
            WithdrawnAtUtc: null,
            WithdrawnReason: null);

    private static DeviceSoftwareInstallation CreateInstallation(
        string? versionRaw,
        DetectionConfidence confidence = DetectionConfidence.Verified,
        string displayName = "Autopsy 4.21.0") =>
        new(
            displayName,
            "Basis Technology",
            versionRaw,
            "Registry64",
            "Machine",
            confidence,
            NowUtc.AddMinutes(-5));

    private static SoftwareExemption CreateExemption(
        Guid? deviceId = null,
        string? allowedVersionRaw = null,
        DateTimeOffset? startsAtUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? revokedAtUtc = null,
        string reason = "Active case continuity") =>
        new(
            Guid.NewGuid(),
            deviceId,
            ProductId,
            allowedVersionRaw,
            reason,
            ApprovedBySid: "S-1-5-21-200",
            StartsAtUtc: startsAtUtc ?? NowUtc.AddDays(-2),
            ExpiresAtUtc: expiresAtUtc ?? NowUtc.AddDays(5),
            RevokedAtUtc: revokedAtUtc,
            RevokedBySid: revokedAtUtc is null ? null : "S-1-5-21-200",
            RevokedReason: revokedAtUtc is null ? null : "Case closed");

    private static ActorIdentity CreateActor(
        string sid = "S-1-5-21-1001",
        bool isDevice = false,
        params HiveRole[] roles) =>
        new(
            UserSid: sid,
            DisplayName: "Test Actor",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: true,
            IsDevice: isDevice,
            DeviceId: isDevice ? DeviceId : null);

    [Theory]
    [InlineData(VersionScheme.SemVer, "4.21.0", "4.21.0")]
    [InlineData(VersionScheme.NumericSegments, "2.10", "2.10.0")]
    [InlineData(VersionScheme.Exact, "2026.11-r1", "2026.11-R1")]
    public void Evaluate_MatchingApprovedVersion_ReturnsCompliant(
        VersionScheme scheme,
        string observed,
        string approved)
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(scheme),
            CreateApprovedRelease(approved, scheme),
            [CreateInstallation(observed)],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Compliant, result.Status);
        Assert.Equal(observed, result.ObservedVersionRaw);
        Assert.Null(result.ActiveExemption);
    }

    [Theory]
    [InlineData(VersionScheme.SemVer, "4.20.0", "4.21.0")]
    [InlineData(VersionScheme.NumericSegments, "2.2.0", "2.10.0")]
    [InlineData(VersionScheme.Exact, "2026.11-R1", "2026.11-R2")]
    public void Evaluate_OlderOrMismatchedVersion_ReturnsUpdateRequired(
        VersionScheme scheme,
        string observed,
        string approved)
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(scheme),
            CreateApprovedRelease(approved, scheme),
            [CreateInstallation(observed)],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.UpdateRequired, result.Status);
        Assert.Equal(observed, result.ObservedVersionRaw);
        Assert.Null(result.ActiveExemption);
    }

    [Theory]
    [InlineData(VersionScheme.SemVer, "4.22.0", "4.21.0")]
    [InlineData(VersionScheme.NumericSegments, "2.10.0", "2.2.0")]
    public void Evaluate_NewerThanApprovedVersion_ReturnsAheadUnapproved(
        VersionScheme scheme,
        string observed,
        string approved)
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(scheme),
            CreateApprovedRelease(approved, scheme),
            [CreateInstallation(observed)],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.AheadUnapproved, result.Status);
        Assert.Equal(observed, result.ObservedVersionRaw);
    }

    [Fact]
    public void Evaluate_CompleteInventoryWithNoInstallationsAndApprovedRelease_ReturnsNotInstalled()
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            matchingInstallations: [],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.NotInstalled, result.Status);
        Assert.Null(result.ObservedVersionRaw);
    }

    [Fact]
    public void Evaluate_IncompleteInventoryWithNoInstallations_ReturnsUnknown()
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            matchingInstallations: [],
            inventoryIsComplete: false,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Unknown, result.Status);
    }

    [Theory]
    [InlineData(DetectionConfidence.Partial)]
    [InlineData(DetectionConfidence.Unknown)]
    public void Evaluate_NonVerifiedDetectionConfidence_ReturnsUnknown(DetectionConfidence confidence)
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.21.0", confidence)],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Unknown, result.Status);
    }

    [Fact]
    public void Evaluate_MixedVerifiedAndPartialDetections_ReturnsUnknown()
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [
                CreateInstallation("4.21.0", DetectionConfidence.Verified),
                CreateInstallation("4.21.0", DetectionConfidence.Partial)
            ],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Unknown, result.Status);
    }

    [Theory]
    [InlineData(null, VersionScheme.SemVer)]
    [InlineData("", VersionScheme.SemVer)]
    [InlineData("   ", VersionScheme.NumericSegments)]
    [InlineData("v4.21.0", VersionScheme.SemVer)]
    [InlineData("4.21.0-alpha", VersionScheme.NumericSegments)]
    [InlineData("not-a-version", VersionScheme.NumericSegments)]
    public void Evaluate_NullOrUnparseableObservedVersion_ReturnsUnknown(
        string? observedVersion,
        VersionScheme scheme)
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(scheme),
            CreateApprovedRelease("4.21.0", scheme),
            [CreateInstallation(observedVersion)],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Unknown, result.Status);
    }

    [Fact]
    public void Evaluate_ConflictingMultipleInstalledVersions_ReturnsUnknown()
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [
                CreateInstallation("4.20.0", DetectionConfidence.Verified),
                CreateInstallation("4.21.0", DetectionConfidence.Verified)
            ],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Unknown, result.Status);
        Assert.Contains("4.20.0", result.ObservedVersionRaw, StringComparison.Ordinal);
        Assert.Contains("4.21.0", result.ObservedVersionRaw, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_DuplicateIdenticalVerifiedVersions_EvaluatesNormally()
    {
        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [
                CreateInstallation("4.21.0", DetectionConfidence.Verified, "Autopsy (x64)"),
                CreateInstallation("4.21.0", DetectionConfidence.Verified, "Autopsy (User)")
            ],
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Compliant, result.Status);
        Assert.Equal("4.21.0", result.ObservedVersionRaw);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_MissingApprovedReleaseTarget_ReturnsUnknown(bool isInstalled)
    {
        var installations = isInstalled
            ? new[] { CreateInstallation("4.21.0") }
            : Array.Empty<DeviceSoftwareInstallation>();

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            activeApprovedRelease: null,
            installations,
            inventoryIsComplete: true,
            exemptions: [],
            NowUtc);

        Assert.Equal(ComplianceStatus.Unknown, result.Status);
    }

    [Fact]
    public void Evaluate_ActiveExemptionWithMatchingAllowedVersion_ReturnsExempt()
    {
        var exemption = CreateExemption(deviceId: DeviceId, allowedVersionRaw: "4.19.0");

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.19.0")],
            inventoryIsComplete: true,
            exemptions: [exemption],
            NowUtc);

        Assert.Equal(ComplianceStatus.Exempt, result.Status);
        Assert.Equal("4.19.0", result.ObservedVersionRaw);
        Assert.Equal(exemption, result.ActiveExemption);
    }

    [Fact]
    public void Evaluate_ActiveWildcardExemption_ReturnsExempt()
    {
        var exemption = CreateExemption(deviceId: null, allowedVersionRaw: null);

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.18.0")],
            inventoryIsComplete: true,
            exemptions: [exemption],
            NowUtc);

        Assert.Equal(ComplianceStatus.Exempt, result.Status);
        Assert.Equal(exemption, result.ActiveExemption);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Evaluate_ExpiredExemptionAtOrAfterExpiresAtUtc_RevertsToUpdateRequired(int secondsPastExpiry)
    {
        var expiresAt = NowUtc.AddSeconds(-secondsPastExpiry);
        var expired = CreateExemption(
            deviceId: DeviceId,
            allowedVersionRaw: "4.19.0",
            startsAtUtc: expiresAt.AddDays(-7),
            expiresAtUtc: expiresAt);

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.19.0")],
            inventoryIsComplete: true,
            exemptions: [expired],
            NowUtc);

        Assert.Equal(ComplianceStatus.UpdateRequired, result.Status);
        Assert.Null(result.ActiveExemption);
    }

    [Fact]
    public void Evaluate_NotYetStartedExemption_RevertsToUpdateRequired()
    {
        var futureExemption = CreateExemption(
            deviceId: DeviceId,
            allowedVersionRaw: "4.19.0",
            startsAtUtc: NowUtc.AddMinutes(1),
            expiresAtUtc: NowUtc.AddDays(5));

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.19.0")],
            inventoryIsComplete: true,
            exemptions: [futureExemption],
            NowUtc);

        Assert.Equal(ComplianceStatus.UpdateRequired, result.Status);
        Assert.Null(result.ActiveExemption);
    }

    [Fact]
    public void Evaluate_RevokedExemption_RevertsToUpdateRequired()
    {
        var revoked = CreateExemption(
            deviceId: DeviceId,
            allowedVersionRaw: "4.19.0",
            revokedAtUtc: NowUtc.AddMinutes(-1));

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.19.0")],
            inventoryIsComplete: true,
            exemptions: [revoked],
            NowUtc);

        Assert.Equal(ComplianceStatus.UpdateRequired, result.Status);
        Assert.Null(result.ActiveExemption);
    }

    [Fact]
    public void Evaluate_AllowedVersionMismatch_DoesNotGrantExemption()
    {
        var exemption = CreateExemption(deviceId: DeviceId, allowedVersionRaw: "4.20.0");

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.19.0")],
            inventoryIsComplete: true,
            exemptions: [exemption],
            NowUtc);

        Assert.Equal(ComplianceStatus.UpdateRequired, result.Status);
        Assert.Null(result.ActiveExemption);
    }

    [Fact]
    public void Evaluate_DeviceScopedExemption_TakesPrecedenceOverGlobalExemption()
    {
        var globalExemption = CreateExemption(
            deviceId: null,
            allowedVersionRaw: "4.19.0",
            expiresAtUtc: NowUtc.AddDays(30),
            reason: "Global fallback");
        var deviceExemption = CreateExemption(
            deviceId: DeviceId,
            allowedVersionRaw: "4.19.0",
            expiresAtUtc: NowUtc.AddDays(3),
            reason: "Device-specific hold");
        var otherDeviceExemption = CreateExemption(
            deviceId: OtherDeviceId,
            allowedVersionRaw: "4.19.0",
            expiresAtUtc: NowUtc.AddDays(60),
            reason: "Other device");

        var result = SoftwareComplianceEvaluator.Evaluate(
            DeviceId,
            CreateProduct(),
            CreateApprovedRelease("4.21.0"),
            [CreateInstallation("4.19.0")],
            inventoryIsComplete: true,
            exemptions: [globalExemption, otherDeviceExemption, deviceExemption],
            NowUtc);

        Assert.Equal(ComplianceStatus.Exempt, result.Status);
        Assert.Equal(deviceExemption, result.ActiveExemption);
    }

    [Fact]
    public void MatchesProduct_MatchesCaseInsensitivelyAndRejectsBlankInputs()
    {
        var product = CreateProduct(matchPattern: "autopsy");
        Assert.True(SoftwareComplianceEvaluator.MatchesProduct(CreateInstallation("4.21.0", displayName: "Basis AUTOPSY 4.21"), product));
        Assert.False(SoftwareComplianceEvaluator.MatchesProduct(CreateInstallation("4.21.0", displayName: "   "), product));
        Assert.False(SoftwareComplianceEvaluator.MatchesProduct(CreateInstallation("4.21.0"), CreateProduct(matchPattern: "   ")));
    }

    [Theory]
    [InlineData(HiveRole.SoftwareApprover, "S-1-5-21-100", "S-1-5-21-200", true)]
    [InlineData(HiveRole.Administrator, "S-1-5-21-100", "S-1-5-21-200", true)]
    [InlineData(HiveRole.SoftwareApprover, "S-1-5-21-100", "S-1-5-21-100", false)]
    [InlineData(HiveRole.SoftwareApprover, "S-1-5-21-100", "s-1-5-21-100", false)]
    [InlineData(HiveRole.Administrator, "S-1-5-21-100", "S-1-5-21-100", false)]
    [InlineData(HiveRole.SoftwareApprover, "S-1-5-21-100", "", false)]
    [InlineData(HiveRole.SoftwareAdmin, "S-1-5-21-100", "S-1-5-21-200", false)]
    [InlineData(HiveRole.Supervisor, "S-1-5-21-100", "S-1-5-21-200", false)]
    [InlineData(HiveRole.Examiner, "S-1-5-21-100", "S-1-5-21-200", false)]
    [InlineData(HiveRole.Auditor, "S-1-5-21-100", "S-1-5-21-200", false)]
    public void CanApproveSoftwareRelease_EnforcesRoleAndDeniesSelfApproval(
        HiveRole role,
        string actorSid,
        string createdBySid,
        bool expected)
    {
        var actor = CreateActor(sid: actorSid, roles: role);
        Assert.Equal(expected, ScopeAuthorizationService.CanApproveSoftwareRelease(actor, createdBySid));
    }

    [Theory]
    [InlineData(HiveRole.Examiner, false, false, false)]
    [InlineData(HiveRole.Supervisor, false, false, true)]
    [InlineData(HiveRole.SoftwareAdmin, true, false, true)]
    [InlineData(HiveRole.SoftwareApprover, false, true, true)]
    [InlineData(HiveRole.RemoteAccess, false, false, false)]
    [InlineData(HiveRole.Administrator, true, true, true)]
    [InlineData(HiveRole.Auditor, false, false, true)]
    public void SoftwareAuthorizationMethods_EnforceExpectedRoleScopes(
        HiveRole role,
        bool canManageCatalogue,
        bool canManageExemptions,
        bool canViewFleetCompliance)
    {
        var actor = CreateActor(roles: role);

        Assert.Equal(canManageCatalogue, ScopeAuthorizationService.CanManageSoftwareCatalogue(actor));
        Assert.Equal(canManageExemptions, ScopeAuthorizationService.CanManageSoftwareExemptions(actor));
        Assert.Equal(canViewFleetCompliance, ScopeAuthorizationService.CanViewFleetCompliance(actor));
    }

    [Fact]
    public void SoftwareAuthorizationMethods_DenyDeviceActorsEvenWithAllRoles()
    {
        var device = CreateActor(isDevice: true, roles: Enum.GetValues<HiveRole>());

        Assert.False(ScopeAuthorizationService.CanManageSoftwareCatalogue(device));
        Assert.False(ScopeAuthorizationService.CanApproveSoftwareRelease(device, "S-1-5-21-999"));
        Assert.False(ScopeAuthorizationService.CanManageSoftwareExemptions(device));
        Assert.False(ScopeAuthorizationService.CanViewFleetCompliance(device));
    }
}
