using System.Security.Cryptography;
using Hive.Domain.Deployments;
using Hive.Domain.Identity;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class SafeDeploymentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 15, 0, 0, TimeSpan.Zero);

    private static SignedReleaseManifest CreateManifest(
        string keyId,
        string version = "4.21.0",
        string packageSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        IReadOnlyList<string>? protectedProcesses = null,
        DateTimeOffset? expiresUtc = null) =>
        new(
            ReleaseId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ProductId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ProductName: "Autopsy Forensic Browser",
            Version: version,
            InstallerType: "msi",
            PackageSha256: packageSha256,
            PackageSizeBytes: 256L * 1024L * 1024L,
            DownloadRelativeUrl: "/api/v1/software/packages/pkg-001",
            SilentArgs: "/qn /norestart",
            VerificationType: "DisplayVersion",
            VerificationTarget: "Autopsy",
            ExpectedVersion: version,
            ProtectedProcesses: protectedProcesses ?? ["autopsy64.exe", "java.exe"],
            RebootAllowed: false,
            InstallProfile: "ForensicWorkstation",
            IssuedUtc: Now.AddDays(-1),
            ExpiresUtc: expiresUtc ?? Now.AddDays(90),
            KeyId: keyId);

    private static SafetyEvaluationInput CreateSafeInput(
        bool protectionStateKnown = true,
        bool manualProtectedModeActive = false,
        IReadOnlyCollection<string>? runningProcessNames = null,
        IReadOnlyCollection<string>? protectedProcessNames = null,
        long? freeBytesOnSystemDrive = 20L * 1024L * 1024L * 1024L,
        long packageSizeBytes = 100L * 1024L * 1024L,
        bool leaseActive = true,
        DateTimeOffset? leaseExpiresUtc = null) =>
        new(
            ProtectionStateKnown: protectionStateKnown,
            ManualProtectedModeActive: manualProtectedModeActive,
            RunningProcessNames: runningProcessNames ?? ["svchost.exe", "explorer.exe"],
            ProtectedProcessNames: protectedProcessNames ?? ["autopsy64.exe", "ftkimager"],
            FreeBytesOnSystemDrive: freeBytesOnSystemDrive,
            PackageSizeBytes: packageSizeBytes,
            LeaseActive: leaseActive,
            LeaseExpiresUtc: leaseExpiresUtc ?? Now.AddMinutes(10),
            UtcNow: Now);

    private static ActorIdentity CreateUser(
        string sid = "S-1-5-21-1001",
        IEnumerable<HiveRole>? roles = null,
        IEnumerable<string>? sites = null,
        bool allSites = false) =>
        new(
            UserSid: sid,
            DisplayName: "Test User",
            Roles: new HashSet<HiveRole>(roles ?? []),
            AllowedSiteIds: new HashSet<string>(sites ?? [], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: allSites,
            IsDevice: false,
            DeviceId: null);

    private static ActorIdentity CreateDeviceActor(params HiveRole[] roles) =>
        new(
            UserSid: "device",
            DisplayName: "DFU-WS-01",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: true,
            IsDevice: true,
            DeviceId: Guid.NewGuid());

    #region ReleaseManifestCrypto

    [Fact]
    public void ReleaseManifestCrypto_CreateSigningKeyPair_GeneratesValidEcdsaP256AndKeyId()
    {
        var (privateKeyPem, publicKeyPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();

        Assert.Contains("BEGIN EC PRIVATE KEY", privateKeyPem, StringComparison.Ordinal);
        Assert.Contains("BEGIN PUBLIC KEY", publicKeyPem, StringComparison.Ordinal);
        Assert.Equal(16, keyId.Length);
        Assert.Equal(ReleaseManifestCrypto.ComputeKeyId(publicKeyPem), keyId);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public void ReleaseManifestCrypto_Canonicalize_IsDeterministicAndCompactCamelCase()
    {
        var manifestA = CreateManifest("key-1234");
        var manifestB = CreateManifest("key-1234");

        var canonicalA = ReleaseManifestCrypto.Canonicalize(manifestA);
        var canonicalB = ReleaseManifestCrypto.Canonicalize(manifestB);

        Assert.Equal(canonicalA, canonicalB);
        Assert.Contains("\"releaseId\":", canonicalA, StringComparison.Ordinal);
        Assert.Contains("\"packageSha256\":", canonicalA, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", canonicalA, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifestCrypto_SignAndVerify_ValidManifestSucceeds()
    {
        var (privateKeyPem, publicKeyPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var manifest = CreateManifest(keyId);

        var envelope = ReleaseManifestCrypto.Sign(manifest, privateKeyPem, publicKeyPem);

        Assert.Equal(ReleaseManifestCrypto.SupportedAlgorithm, envelope.Algorithm);
        Assert.True(ReleaseManifestCrypto.Verify(envelope, out var reasonNoTrustPin, utcNow: Now));
        Assert.Equal("manifest_valid", reasonNoTrustPin);

        Assert.True(ReleaseManifestCrypto.Verify(envelope, out var reasonPinned, trustedPublicKeyPem: publicKeyPem, utcNow: Now));
        Assert.Equal("manifest_valid", reasonPinned);
    }

    [Fact]
    public void ReleaseManifestCrypto_TamperedVersionSha256OrProtectedProcess_RejectedEvenIfCanonicalUpdated()
    {
        var (privateKeyPem, publicKeyPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var manifest = CreateManifest(keyId);
        var envelope = ReleaseManifestCrypto.Sign(manifest, privateKeyPem, publicKeyPem);

        var tamperedVersion = manifest with { Version = "4.21.1" };
        var tamperedSha = manifest with { PackageSha256 = new string('f', 64) };
        var tamperedProc = manifest with { ProtectedProcesses = ["autopsy64.exe"] };

        foreach (var tampered in new[] { tamperedVersion, tamperedSha, tamperedProc })
        {
            var reCanonicalizedEnvelope = envelope with
            {
                Manifest = tampered,
                ManifestCanonicalJson = ReleaseManifestCrypto.Canonicalize(tampered)
            };

            Assert.False(ReleaseManifestCrypto.Verify(reCanonicalizedEnvelope, out var reason, publicKeyPem, Now));
            Assert.Equal("manifest_invalid_signature", reason);
        }
    }

    [Fact]
    public void ReleaseManifestCrypto_CanonicalMismatch_Rejected()
    {
        var (privateKeyPem, publicKeyPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var manifest = CreateManifest(keyId);
        var envelope = ReleaseManifestCrypto.Sign(manifest, privateKeyPem, publicKeyPem);

        var mutatedManifestOnly = envelope with { Manifest = manifest with { Version = "9.9.9" } };
        Assert.False(ReleaseManifestCrypto.Verify(mutatedManifestOnly, out var reasonA, publicKeyPem, Now));
        Assert.Equal("manifest_canonical_mismatch", reasonA);

        var mutatedCanonicalOnly = envelope with { ManifestCanonicalJson = envelope.ManifestCanonicalJson + " " };
        Assert.False(ReleaseManifestCrypto.Verify(mutatedCanonicalOnly, out var reasonB, publicKeyPem, Now));
        Assert.Equal("manifest_canonical_mismatch", reasonB);
    }

    [Fact]
    public void ReleaseManifestCrypto_UntrustedPublicKey_Rejected()
    {
        var (privateKeyPem, publicKeyPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var (_, otherPublicKeyPem, _) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var envelope = ReleaseManifestCrypto.Sign(CreateManifest(keyId), privateKeyPem, publicKeyPem);

        Assert.False(ReleaseManifestCrypto.Verify(envelope, out var reason, trustedPublicKeyPem: otherPublicKeyPem, utcNow: Now));
        Assert.Equal("manifest_untrusted_key", reason);
    }

    [Fact]
    public void ReleaseManifestCrypto_ExpiredManifest_Rejected()
    {
        var (privateKeyPem, publicKeyPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var expiredAtNow = CreateManifest(keyId, expiresUtc: Now);
        var envelopeAtBoundary = ReleaseManifestCrypto.Sign(expiredAtNow, privateKeyPem, publicKeyPem);

        Assert.False(ReleaseManifestCrypto.Verify(envelopeAtBoundary, out var reasonBoundary, publicKeyPem, Now));
        Assert.Equal("manifest_expired", reasonBoundary);

        var expiredEarlier = CreateManifest(keyId, expiresUtc: Now.AddSeconds(-1));
        var envelopeExpired = ReleaseManifestCrypto.Sign(expiredEarlier, privateKeyPem, publicKeyPem);

        Assert.False(ReleaseManifestCrypto.Verify(envelopeExpired, out var reasonPast, publicKeyPem, Now));
        Assert.Equal("manifest_expired", reasonPast);
    }

    [Fact]
    public void ReleaseManifestCrypto_UnsupportedAlgorithmOrMissingEnvelope_Rejected()
    {
        var (privateKeyPem, publicKeyPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var envelope = ReleaseManifestCrypto.Sign(CreateManifest(keyId), privateKeyPem, publicKeyPem);

        var wrongAlg = envelope with { Algorithm = "RSA-PSS-SHA256" };
        Assert.False(ReleaseManifestCrypto.Verify(wrongAlg, out var reasonAlg, publicKeyPem, Now));
        Assert.Equal("manifest_unsupported_algorithm", reasonAlg);

        Assert.False(ReleaseManifestCrypto.Verify(null, out var reasonMissing, publicKeyPem, Now));
        Assert.Equal("manifest_missing", reasonMissing);
    }

    #endregion

    #region WorkloadSafetyGuard

    [Fact]
    public void WorkloadSafetyGuard_UnknownProtectionState_FailsClosed()
    {
        var input = CreateSafeInput(protectionStateKnown: false);

        var result = WorkloadSafetyGuard.Evaluate(input);

        Assert.False(result.Allowed);
        Assert.Equal(DeploymentState.Deferred, result.RecommendedState);
        Assert.Equal("protection_state_unknown", result.ReasonCode);
    }

    [Fact]
    public void WorkloadSafetyGuard_ManualProtectedModeActive_FailsClosed()
    {
        var input = CreateSafeInput(manualProtectedModeActive: true);

        var result = WorkloadSafetyGuard.Evaluate(input);

        Assert.False(result.Allowed);
        Assert.Equal(DeploymentState.Deferred, result.RecommendedState);
        Assert.Equal("manual_protection_lock_active", result.ReasonCode);
    }

    [Theory]
    [InlineData("ftkimager", "ftkimager")]
    [InlineData("autopsy64.exe", "autopsy64")]
    [InlineData("autopsy64", "autopsy64.exe")]
    [InlineData("AUTOPSY64.EXE", "Autopsy64")]
    [InlineData(@"C:\Program Files\Forensics\FTKImager.EXE", "ftkimager.exe")]
    public void WorkloadSafetyGuard_ProtectedRunningProcess_NormalizesExeAndCaseAndDefers(
        string runningProcess,
        string protectedProcess)
    {
        var input = CreateSafeInput(
            runningProcessNames: ["explorer.exe", runningProcess],
            protectedProcessNames: [protectedProcess]);

        var result = WorkloadSafetyGuard.Evaluate(input);

        Assert.False(result.Allowed);
        Assert.Equal(DeploymentState.Deferred, result.RecommendedState);
        Assert.Equal("protected_workload_active", result.ReasonCode);
    }

    [Fact]
    public void WorkloadSafetyGuard_InsufficientDiskSpace_Enforces2GiBFloorAndTriplePackageSize()
    {
        const long twoGiB = WorkloadSafetyGuard.MinimumRequiredFreeBytesFloor;
        const long oneGiB = 1024L * 1024L * 1024L;

        // Small package still requires the 2 GiB minimum floor.
        var belowFloor = CreateSafeInput(freeBytesOnSystemDrive: twoGiB - 1, packageSizeBytes: 10L * 1024L * 1024L);
        var floorResult = WorkloadSafetyGuard.Evaluate(belowFloor);
        Assert.False(floorResult.Allowed);
        Assert.Equal(DeploymentState.Deferred, floorResult.RecommendedState);
        Assert.Equal("insufficient_disk_space", floorResult.ReasonCode);

        var atFloor = CreateSafeInput(freeBytesOnSystemDrive: twoGiB, packageSizeBytes: 10L * 1024L * 1024L);
        Assert.True(WorkloadSafetyGuard.Evaluate(atFloor).Allowed);

        // Large package (1 GiB) requires 3 * 1 GiB = 3 GiB.
        var belowTriple = CreateSafeInput(freeBytesOnSystemDrive: (3L * oneGiB) - 1, packageSizeBytes: oneGiB);
        var tripleResult = WorkloadSafetyGuard.Evaluate(belowTriple);
        Assert.False(tripleResult.Allowed);
        Assert.Equal(DeploymentState.Deferred, tripleResult.RecommendedState);
        Assert.Equal("insufficient_disk_space", tripleResult.ReasonCode);

        var atTriple = CreateSafeInput(freeBytesOnSystemDrive: 3L * oneGiB, packageSizeBytes: oneGiB);
        Assert.True(WorkloadSafetyGuard.Evaluate(atTriple).Allowed);

        // Null disk free space fails closed.
        var unknownDisk = CreateSafeInput(freeBytesOnSystemDrive: null);
        Assert.Equal("insufficient_disk_space", WorkloadSafetyGuard.Evaluate(unknownDisk).ReasonCode);
    }

    [Fact]
    public void WorkloadSafetyGuard_InvalidOrExpiredLease_FailsClosed()
    {
        var inactiveLease = CreateSafeInput(leaseActive: false);
        Assert.Equal("deployment_lease_invalid", WorkloadSafetyGuard.Evaluate(inactiveLease).ReasonCode);

        var missingExpiry = new SafetyEvaluationInput(
            ProtectionStateKnown: true,
            ManualProtectedModeActive: false,
            RunningProcessNames: [],
            ProtectedProcessNames: ["autopsy64.exe"],
            FreeBytesOnSystemDrive: 10L * 1024L * 1024L * 1024L,
            PackageSizeBytes: 50L * 1024L * 1024L,
            LeaseActive: true,
            LeaseExpiresUtc: null,
            UtcNow: Now);
        Assert.Equal("deployment_lease_invalid", WorkloadSafetyGuard.Evaluate(missingExpiry).ReasonCode);

        var boundaryLease = CreateSafeInput(leaseActive: true, leaseExpiresUtc: Now);
        Assert.Equal("deployment_lease_invalid", WorkloadSafetyGuard.Evaluate(boundaryLease).ReasonCode);

        var expiredLease = CreateSafeInput(leaseActive: true, leaseExpiresUtc: Now.AddSeconds(-1));
        var expiredResult = WorkloadSafetyGuard.Evaluate(expiredLease);
        Assert.False(expiredResult.Allowed);
        Assert.Equal(DeploymentState.Deferred, expiredResult.RecommendedState);
        Assert.Equal("deployment_lease_invalid", expiredResult.ReasonCode);
    }

    [Fact]
    public void WorkloadSafetyGuard_AllPreconditionsSatisfied_AllowsInstallWithoutCpuIdleDependency()
    {
        var input = CreateSafeInput();

        var result = WorkloadSafetyGuard.Evaluate(input);

        Assert.True(result.Allowed);
        Assert.Equal(DeploymentState.Installing, result.RecommendedState);
        Assert.Equal("safety_checks_passed", result.ReasonCode);

        var propertyNames = typeof(SafetyEvaluationInput)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();
        Assert.DoesNotContain(propertyNames, n =>
            n.Contains("Cpu", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Idle", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region ScopeAuthorizationService

    [Theory]
    [InlineData(HiveRole.SoftwareAdmin, true)]
    [InlineData(HiveRole.SoftwareApprover, true)]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.Supervisor, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    [InlineData(HiveRole.BreakGlassCustodian, false)]
    [InlineData(HiveRole.Auditor, false)]
    public void CanSignSoftwareRelease_EnforcesExpectedRolesAndDeniesDevices(HiveRole role, bool expected)
    {
        var user = CreateUser(roles: [role], sites: ["site-a"]);
        Assert.Equal(expected, ScopeAuthorizationService.CanSignSoftwareRelease(user));

        var device = CreateDeviceActor(Enum.GetValues<HiveRole>());
        Assert.False(ScopeAuthorizationService.CanSignSoftwareRelease(device));

        var noRole = CreateUser(roles: [], allSites: true);
        Assert.False(ScopeAuthorizationService.CanSignSoftwareRelease(noRole));
    }

    [Theory]
    [InlineData(HiveRole.SoftwareAdmin, true)]
    [InlineData(HiveRole.SoftwareApprover, true)]
    [InlineData(HiveRole.Supervisor, true)]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.Auditor, true)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    [InlineData(HiveRole.BreakGlassCustodian, false)]
    public void CanViewDeployments_EnforcesRoleAndSiteScopeAndDeniesDevices(HiveRole role, bool expected)
    {
        var scopedUser = CreateUser(roles: [role], sites: ["site-a"]);
        Assert.Equal(expected, ScopeAuthorizationService.CanViewDeployments(scopedUser, "site-a"));
        Assert.False(ScopeAuthorizationService.CanViewDeployments(scopedUser, "site-b"));
        Assert.False(ScopeAuthorizationService.CanViewDeployments(scopedUser, " "));

        var allSitesUser = CreateUser(roles: [role], allSites: true);
        Assert.Equal(expected, ScopeAuthorizationService.CanViewDeployments(allSitesUser, "site-b"));

        var device = CreateDeviceActor(Enum.GetValues<HiveRole>());
        Assert.False(ScopeAuthorizationService.CanViewDeployments(device, "site-a"));
    }

    [Theory]
    [InlineData(HiveRole.SoftwareAdmin, true)]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.SoftwareApprover, false)]
    [InlineData(HiveRole.Supervisor, false)]
    [InlineData(HiveRole.Auditor, false)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    [InlineData(HiveRole.BreakGlassCustodian, false)]
    public void CanManageDeployments_EnforcesRoleAndSiteScopeAndDeniesDevices(HiveRole role, bool expected)
    {
        var scopedUser = CreateUser(roles: [role], sites: ["site-a"]);
        Assert.Equal(expected, ScopeAuthorizationService.CanManageDeployments(scopedUser, "site-a"));
        Assert.False(ScopeAuthorizationService.CanManageDeployments(scopedUser, "site-b"));
        Assert.False(ScopeAuthorizationService.CanManageDeployments(scopedUser, ""));

        var allSitesUser = CreateUser(roles: [role], allSites: true);
        Assert.Equal(expected, ScopeAuthorizationService.CanManageDeployments(allSitesUser, "site-z"));

        var device = CreateDeviceActor(Enum.GetValues<HiveRole>());
        Assert.False(ScopeAuthorizationService.CanManageDeployments(device, "site-a"));
    }

    #endregion

    #region DeploymentStateMachine

    [Theory]
    [InlineData(DeploymentState.Pending, DeploymentState.Downloading)]
    [InlineData(DeploymentState.Pending, DeploymentState.Cancelled)]
    [InlineData(DeploymentState.Pending, DeploymentState.Failed)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Staged)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Deferred)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Failed)]
    [InlineData(DeploymentState.Downloading, DeploymentState.Unknown)]
    [InlineData(DeploymentState.Staged, DeploymentState.Deferred)]
    [InlineData(DeploymentState.Staged, DeploymentState.Installing)]
    [InlineData(DeploymentState.Staged, DeploymentState.Cancelled)]
    [InlineData(DeploymentState.Staged, DeploymentState.Failed)]
    [InlineData(DeploymentState.Deferred, DeploymentState.Staged)]
    [InlineData(DeploymentState.Deferred, DeploymentState.Cancelled)]
    [InlineData(DeploymentState.Deferred, DeploymentState.Failed)]
    [InlineData(DeploymentState.Installing, DeploymentState.Verifying)]
    [InlineData(DeploymentState.Installing, DeploymentState.Unknown)]
    [InlineData(DeploymentState.Installing, DeploymentState.Failed)]
    [InlineData(DeploymentState.Verifying, DeploymentState.Verified)]
    [InlineData(DeploymentState.Verifying, DeploymentState.PendingReboot)]
    [InlineData(DeploymentState.Verifying, DeploymentState.Failed)]
    [InlineData(DeploymentState.PendingReboot, DeploymentState.Verifying)]
    [InlineData(DeploymentState.PendingReboot, DeploymentState.Failed)]
    [InlineData(DeploymentState.Unknown, DeploymentState.Verifying)]
    [InlineData(DeploymentState.Unknown, DeploymentState.Failed)]
    public void DeploymentStateMachine_LegalTransitions_Succeed(DeploymentState from, DeploymentState to)
    {
        Assert.True(DeploymentStateMachine.CanTransition(from, to));
        Assert.Equal(to, DeploymentStateMachine.Transition(from, to));
    }

    [Theory]
    [InlineData(DeploymentState.Verified, DeploymentState.Pending)]
    [InlineData(DeploymentState.Verified, DeploymentState.Failed)]
    [InlineData(DeploymentState.Verified, DeploymentState.Cancelled)]
    [InlineData(DeploymentState.Failed, DeploymentState.Pending)]
    [InlineData(DeploymentState.Failed, DeploymentState.Staged)]
    [InlineData(DeploymentState.Failed, DeploymentState.Verified)]
    [InlineData(DeploymentState.Cancelled, DeploymentState.Pending)]
    [InlineData(DeploymentState.Cancelled, DeploymentState.Downloading)]
    [InlineData(DeploymentState.Cancelled, DeploymentState.Staged)]
    [InlineData(DeploymentState.Pending, DeploymentState.Staged)]
    [InlineData(DeploymentState.Pending, DeploymentState.Installing)]
    [InlineData(DeploymentState.Deferred, DeploymentState.Installing)]
    [InlineData(DeploymentState.Installing, DeploymentState.Cancelled)]
    [InlineData(DeploymentState.Installing, DeploymentState.Deferred)]
    [InlineData(DeploymentState.PendingReboot, DeploymentState.Verified)]
    public void DeploymentStateMachine_IllegalOrTerminalTransitions_Throw(DeploymentState from, DeploymentState to)
    {
        Assert.False(DeploymentStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => DeploymentStateMachine.Transition(from, to));
    }

    #endregion
}
