using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hive.Domain.Enrollment;
using Hive.Domain.Identity;
using Hive.Domain.Operations;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class HardeningDomainTests
{
    private static X509Certificate2 CreateSelfSignedRsaCertificate(
        string subjectName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 CreateSelfSignedEcdsaCertificate(
        string subjectName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={subjectName}",
            ecdsa,
            HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static ActorIdentity CreateUser(
        HiveRole[] roles,
        string sid = "S-1-5-21-1001",
        bool allSites = true) =>
        new(
            UserSid: sid,
            DisplayName: "Test Operator",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: allSites,
            IsDevice: false,
            DeviceId: null);

    private static ActorIdentity CreateDevice(params HiveRole[] roles) =>
        new(
            UserSid: "device",
            DisplayName: "LAB-WS-01",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: true,
            IsDevice: true,
            DeviceId: Guid.NewGuid());

    [Fact]
    public void EnrollmentProofOfPossession_RsaRoundTrip_Succeeds()
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedRsaCertificate("lab-ws-01.cordon.local", now.AddHours(-1), now.AddDays(30));
        const string tokenHash = "a1b2c3d4e5f6";
        const string hostname = "LAB-WS-01";

        var signature = EnrollmentProofOfPossession.SignChallenge(cert, tokenHash, hostname);
        var ok = EnrollmentProofOfPossession.Verify(
            cert.ExportCertificatePem(),
            signature,
            cert.Thumbprint.ToLowerInvariant(),
            tokenHash.ToUpperInvariant(),
            hostname,
            now,
            out var reasonCode);

        Assert.True(ok);
        Assert.Equal("proof_of_possession_valid", reasonCode);
    }

    [Fact]
    public void EnrollmentProofOfPossession_EcdsaRoundTrip_Succeeds()
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedEcdsaCertificate("lab-ws-02.cordon.local", now.AddHours(-1), now.AddDays(30));
        const string tokenHash = "deadbeef0123";
        const string hostname = "LAB-WS-02";

        var signature = EnrollmentProofOfPossession.SignChallenge(cert, tokenHash, hostname);
        var ok = EnrollmentProofOfPossession.Verify(
            cert.ExportCertificatePem(),
            signature,
            cert.Thumbprint,
            tokenHash,
            hostname,
            now,
            out var reasonCode);

        Assert.True(ok);
        Assert.Equal("proof_of_possession_valid", reasonCode);
    }

    [Fact]
    public void EnrollmentProofOfPossession_ExpiredCert_FailsWithExpectedReason()
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedRsaCertificate("expired-ws", now.AddDays(-10), now.AddDays(-1));
        const string tokenHash = "abc123";
        const string hostname = "EXPIRED-WS";

        var signature = EnrollmentProofOfPossession.SignChallenge(cert, tokenHash, hostname);
        var ok = EnrollmentProofOfPossession.Verify(
            cert.ExportCertificatePem(),
            signature,
            cert.Thumbprint,
            tokenHash,
            hostname,
            now,
            out var reasonCode);

        Assert.False(ok);
        Assert.Equal("certificate_expired_or_not_yet_valid", reasonCode);
    }

    [Fact]
    public void EnrollmentProofOfPossession_NotYetValidCert_FailsWithExpectedReason()
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedRsaCertificate("future-ws", now.AddDays(1), now.AddDays(10));
        const string tokenHash = "abc123";
        const string hostname = "FUTURE-WS";

        var signature = EnrollmentProofOfPossession.SignChallenge(cert, tokenHash, hostname);
        var ok = EnrollmentProofOfPossession.Verify(
            cert.ExportCertificatePem(),
            signature,
            cert.Thumbprint,
            tokenHash,
            hostname,
            now,
            out var reasonCode);

        Assert.False(ok);
        Assert.Equal("certificate_expired_or_not_yet_valid", reasonCode);
    }

    [Fact]
    public void EnrollmentProofOfPossession_MismatchedThumbprint_FailsWithExpectedReason()
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedRsaCertificate("ws-03", now.AddHours(-1), now.AddDays(30));
        const string tokenHash = "abc123";
        const string hostname = "WS-03";

        var signature = EnrollmentProofOfPossession.SignChallenge(cert, tokenHash, hostname);
        var ok = EnrollmentProofOfPossession.Verify(
            cert.ExportCertificatePem(),
            signature,
            "0000000000000000000000000000000000000000",
            tokenHash,
            hostname,
            now,
            out var reasonCode);

        Assert.False(ok);
        Assert.Equal("certificate_thumbprint_mismatch", reasonCode);
    }

    [Fact]
    public void EnrollmentProofOfPossession_TamperedHostnameOrTokenHash_FailsWithProofInvalid()
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedEcdsaCertificate("ws-04", now.AddHours(-1), now.AddDays(30));
        const string tokenHash = "valid-token-hash";
        const string hostname = "WS-04";
        var pem = cert.ExportCertificatePem();
        var signature = EnrollmentProofOfPossession.SignChallenge(cert, tokenHash, hostname);

        var okTamperedHost = EnrollmentProofOfPossession.Verify(
            pem,
            signature,
            cert.Thumbprint,
            tokenHash,
            "WS-04-SPOOFED",
            now,
            out var hostReason);

        var okTamperedToken = EnrollmentProofOfPossession.Verify(
            pem,
            signature,
            cert.Thumbprint,
            "tampered-token-hash",
            hostname,
            now,
            out var tokenReason);

        Assert.False(okTamperedHost);
        Assert.Equal("proof_of_possession_invalid", hostReason);
        Assert.False(okTamperedToken);
        Assert.Equal("proof_of_possession_invalid", tokenReason);
    }

    [Fact]
    public void EnrollmentProofOfPossession_MalformedPem_Fails()
    {
        var ok = EnrollmentProofOfPossession.Verify(
            "-----BEGIN CERTIFICATE-----\nnot-valid-base64!!\n-----END CERTIFICATE-----",
            Convert.ToBase64String(new byte[32]),
            "AABBCCDD",
            "token",
            "WS-05",
            DateTimeOffset.UtcNow,
            out var reasonCode);

        Assert.False(ok);
        Assert.Equal("certificate_pem_invalid", reasonCode);
    }

    [Theory]
    [InlineData(null, "sig", "thumb", "token", "host")]
    [InlineData("", "sig", "thumb", "token", "host")]
    [InlineData("pem", null, "thumb", "token", "host")]
    [InlineData("pem", " ", "thumb", "token", "host")]
    [InlineData("pem", "sig", "", "token", "host")]
    [InlineData("pem", "sig", "thumb", "", "host")]
    [InlineData("pem", "sig", "thumb", "token", "   ")]
    public void EnrollmentProofOfPossession_MissingOrEmptyInputs_Fail(
        string? pem,
        string? sig,
        string thumbprint,
        string tokenHash,
        string hostname)
    {
        var ok = EnrollmentProofOfPossession.Verify(
            pem,
            sig,
            thumbprint,
            tokenHash,
            hostname,
            DateTimeOffset.UtcNow,
            out var reasonCode);

        Assert.False(ok);
        Assert.Equal("proof_of_possession_missing", reasonCode);
    }

    [Fact]
    public void EnrollmentProofOfPossession_SignWithoutPrivateKey_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        using var fullCert = CreateSelfSignedRsaCertificate("ws-public-only", now.AddHours(-1), now.AddDays(30));
        using var publicOnlyCert = X509Certificate2.CreateFromPem(fullCert.ExportCertificatePem());

        Assert.False(publicOnlyCert.HasPrivateKey);
        Assert.Throws<InvalidOperationException>(() =>
            EnrollmentProofOfPossession.SignChallenge(publicOnlyCert, "token-hash", "WS-PUBLIC"));
    }

    [Fact]
    public void VersionCompatibilityEvaluator_ExactMatch_IsCompatible()
    {
        var result = VersionCompatibilityEvaluator.Evaluate("1.2.3", "1.2.0", "1.0.0");

        Assert.Equal(VersionCompatibilityStatus.Compatible, result.Status);
        Assert.Equal("version_compatible", result.ReasonCode);
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0", "1.0.0", VersionCompatibilityStatus.Compatible)]
    [InlineData("V1.2.3-rc1+sha9f8e7d", "1.2.0", "1.0.0", VersionCompatibilityStatus.Compatible)]
    [InlineData("v1.1.4-rc1+build42", "1.2.0", "1.0.0", VersionCompatibilityStatus.UpgradeRecommended)]
    public void VersionCompatibilityEvaluator_PrefixAndPrereleaseMetadata_HandledCleanly(
        string clientVersion,
        string serverVersion,
        string minimumVersion,
        VersionCompatibilityStatus expectedStatus)
    {
        var result = VersionCompatibilityEvaluator.Evaluate(clientVersion, serverVersion, minimumVersion);

        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public void VersionCompatibilityEvaluator_MinorLag_RecommendsUpgrade()
    {
        var result = VersionCompatibilityEvaluator.Evaluate("1.1.9", "1.3.0", "1.0.0");

        Assert.Equal(VersionCompatibilityStatus.UpgradeRecommended, result.Status);
        Assert.Equal("minor_version_behind_server", result.ReasonCode);
    }

    [Theory]
    [InlineData("2.0.0", "1.2.0", "1.0.0")]
    [InlineData("0.9.0", "1.2.0", "0.8.0")]
    public void VersionCompatibilityEvaluator_MajorMismatch_IsIncompatible(
        string clientVersion,
        string serverVersion,
        string minimumVersion)
    {
        var result = VersionCompatibilityEvaluator.Evaluate(clientVersion, serverVersion, minimumVersion);

        Assert.Equal(VersionCompatibilityStatus.Incompatible, result.Status);
        Assert.Equal("major_version_mismatch", result.ReasonCode);
    }

    [Fact]
    public void VersionCompatibilityEvaluator_BelowMinimumVersion_IsIncompatible()
    {
        var result = VersionCompatibilityEvaluator.Evaluate("1.0.5", "1.4.0", "1.2.0");

        Assert.Equal(VersionCompatibilityStatus.Incompatible, result.Status);
        Assert.Equal("below_minimum_supported_version", result.ReasonCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("abc")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.-2.0")]
    public void VersionCompatibilityEvaluator_NullEmptyOrMalformed_IsIncompatible(string? clientVersion)
    {
        var result = VersionCompatibilityEvaluator.Evaluate(clientVersion, "1.2.0", "1.0.0");

        Assert.Equal(VersionCompatibilityStatus.Incompatible, result.Status);
        Assert.Equal("version_unparseable", result.ReasonCode);
    }

    [Theory]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.Auditor, false)]
    [InlineData(HiveRole.Supervisor, false)]
    [InlineData(HiveRole.SoftwareAdmin, false)]
    [InlineData(HiveRole.SoftwareApprover, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    [InlineData(HiveRole.BreakGlassCustodian, false)]
    public void ScopeAuthorizationService_CanOperateEmergencyStop_EnforcesAdministratorOnly(
        HiveRole role,
        bool expected)
    {
        var user = CreateUser([role]);

        Assert.Equal(expected, ScopeAuthorizationService.CanOperateEmergencyStop(user));
    }

    [Fact]
    public void ScopeAuthorizationService_CanOperateEmergencyStop_DeniesDeviceAndEmptyRoles()
    {
        var deviceWithAdmin = CreateDevice(HiveRole.Administrator);
        var noRolesUser = CreateUser([]);

        Assert.False(ScopeAuthorizationService.CanOperateEmergencyStop(deviceWithAdmin));
        Assert.False(ScopeAuthorizationService.CanOperateEmergencyStop(noRolesUser));
    }

    [Theory]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.Auditor, true)]
    [InlineData(HiveRole.Supervisor, true)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.SoftwareAdmin, false)]
    [InlineData(HiveRole.SoftwareApprover, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    [InlineData(HiveRole.BreakGlassCustodian, false)]
    public void ScopeAuthorizationService_CanViewOperationalHealth_EnforcesAllowedRoles(
        HiveRole role,
        bool expected)
    {
        var user = CreateUser([role]);

        Assert.Equal(expected, ScopeAuthorizationService.CanViewOperationalHealth(user));
    }

    [Fact]
    public void ScopeAuthorizationService_CanViewOperationalHealth_DeniesDeviceAndEmptyRoles()
    {
        var deviceWithAllowedRoles = CreateDevice(HiveRole.Administrator, HiveRole.Auditor, HiveRole.Supervisor);
        var noRolesUser = CreateUser([]);

        Assert.False(ScopeAuthorizationService.CanViewOperationalHealth(deviceWithAllowedRoles));
        Assert.False(ScopeAuthorizationService.CanViewOperationalHealth(noRolesUser));
    }
}
