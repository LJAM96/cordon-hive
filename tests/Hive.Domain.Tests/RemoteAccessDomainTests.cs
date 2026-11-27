using System.Security.Cryptography;
using Hive.Domain.Devices;
using Hive.Domain.Identity;
using Hive.Domain.RemoteAccess;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class RemoteAccessDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 14, 0, 0, TimeSpan.Zero);

    private static byte[] CreateTestKey(byte seed = 0x42)
    {
        var key = new byte[EscrowCredentialCrypto.KeySizeBytes];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(seed + i);
        }
        return key;
    }

    private static Device CreateDevice(
        string hostname = "DFU-WS-01",
        EnrollmentState enrollmentState = EnrollmentState.Active,
        DateTimeOffset? lastSeenUtc = null,
        bool protectedModeActive = false,
        string? protectedModeReason = null) =>
        new(
            DeviceId: Guid.NewGuid(),
            SiteId: "site-a",
            Hostname: hostname,
            AssetNumber: "ASSET-001",
            AssignedUserSid: "S-1-5-21-1001",
            CertificateThumbprint: "AABBCCDD",
            EnrollmentState: enrollmentState,
            EnrolledAtUtc: Now.AddDays(-10),
            LastSeenUtc: lastSeenUtc,
            BootId: Guid.NewGuid(),
            AgentVersion: "1.0.0",
            PolicyRevision: 1,
            RowVersion: 1,
            ProtectedModeActive: protectedModeActive,
            ProtectedModeReason: protectedModeReason);

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

    private static ActorIdentity CreateDeviceActor(
        string sid = "device",
        Guid? deviceId = null,
        params HiveRole[] roles) =>
        new(
            UserSid: sid,
            DisplayName: "DFU-WS-01",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: true,
            IsDevice: true,
            DeviceId: deviceId ?? Guid.NewGuid());

    #region EscrowCredentialCrypto

    [Fact]
    public void EscrowCrypto_EncryptDecryptRoundTrip_Succeeds()
    {
        var key = CreateTestKey();
        var deviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        const int keyVersion = 1;
        const string password = "Cordon-DFU!BreakGlass#2026_£€";

        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, keyVersion, password);

        Assert.Equal(EscrowCredentialCrypto.NonceSizeBytes, nonce.Length);
        Assert.Equal(EscrowCredentialCrypto.TagSizeBytes, tag.Length);
        Assert.NotEmpty(ciphertext);

        var decrypted = EscrowCredentialCrypto.Decrypt(key, deviceId, credentialId, keyVersion, ciphertext, nonce, tag);
        Assert.Equal(password, decrypted);
    }

    [Fact]
    public void EscrowCrypto_EncryptProducesUniqueNoncesAcrossCalls()
    {
        var key = CreateTestKey();
        var deviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        const string password = "SamePasswordForNonceTest!123";

        var nonces = new HashSet<string>(StringComparer.Ordinal);
        var ciphertexts = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 16; i++)
        {
            var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, 1, password);
            Assert.True(nonces.Add(Convert.ToBase64String(nonce)));
            Assert.True(ciphertexts.Add(Convert.ToBase64String(ciphertext)));

            var decrypted = EscrowCredentialCrypto.Decrypt(key, deviceId, credentialId, 1, ciphertext, nonce, tag);
            Assert.Equal(password, decrypted);
        }
    }

    [Fact]
    public void EscrowCrypto_TamperedCiphertext_ThrowsCryptographicException()
    {
        var key = CreateTestKey();
        var deviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, 1, "TopSecretDFU!99");

        var tamperedCiphertext = ciphertext.ToArray();
        tamperedCiphertext[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            EscrowCredentialCrypto.Decrypt(key, deviceId, credentialId, 1, tamperedCiphertext, nonce, tag));
    }

    [Fact]
    public void EscrowCrypto_TamperedTag_ThrowsCryptographicException()
    {
        var key = CreateTestKey();
        var deviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, 1, "TopSecretDFU!99");

        var tamperedTag = tag.ToArray();
        tamperedTag[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            EscrowCredentialCrypto.Decrypt(key, deviceId, credentialId, 1, ciphertext, nonce, tamperedTag));
    }

    [Fact]
    public void EscrowCrypto_TamperedNonce_ThrowsCryptographicException()
    {
        var key = CreateTestKey();
        var deviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, 1, "TopSecretDFU!99");

        var tamperedNonce = nonce.ToArray();
        tamperedNonce[0] ^= 0x80;

        Assert.ThrowsAny<CryptographicException>(() =>
            EscrowCredentialCrypto.Decrypt(key, deviceId, credentialId, 1, ciphertext, tamperedNonce, tag));
    }

    [Fact]
    public void EscrowCrypto_WrongDeviceIdAad_ThrowsCryptographicException()
    {
        var key = CreateTestKey();
        var deviceId = Guid.NewGuid();
        var otherDeviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, 1, "TopSecretDFU!99");

        Assert.ThrowsAny<CryptographicException>(() =>
            EscrowCredentialCrypto.Decrypt(key, otherDeviceId, credentialId, 1, ciphertext, nonce, tag));
    }

    [Fact]
    public void EscrowCrypto_WrongKeyVersionAad_ThrowsCryptographicException()
    {
        var key = CreateTestKey();
        var deviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, 1, "TopSecretDFU!99");

        Assert.ThrowsAny<CryptographicException>(() =>
            EscrowCredentialCrypto.Decrypt(key, deviceId, credentialId, 2, ciphertext, nonce, tag));
    }

    [Fact]
    public void EscrowCrypto_WrongKey_ThrowsCryptographicException()
    {
        var key = CreateTestKey(0x11);
        var wrongKey = CreateTestKey(0x99);
        var deviceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(key, deviceId, credentialId, 1, "TopSecretDFU!99");

        Assert.ThrowsAny<CryptographicException>(() =>
            EscrowCredentialCrypto.Decrypt(wrongKey, deviceId, credentialId, 1, ciphertext, nonce, tag));
    }

    [Fact]
    public void EscrowCrypto_ParseMasterKey_AcceptsValidBase64And64CharHex()
    {
        var expected = CreateTestKey(0x10);
        var base64 = Convert.ToBase64String(expected);
        var hexLower = Convert.ToHexString(expected).ToLowerInvariant();
        var hexUpper = Convert.ToHexString(expected).ToUpperInvariant();

        Assert.Equal(expected, EscrowCredentialCrypto.ParseMasterKey($"  {base64}  "));
        Assert.Equal(expected, EscrowCredentialCrypto.ParseMasterKey(hexLower));
        Assert.Equal(expected, EscrowCredentialCrypto.ParseMasterKey($" {hexUpper} "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcd")]
    [InlineData("00112233445566778899aabbccddeeff")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("not-valid-base64!@#$")]
    public void EscrowCrypto_ParseMasterKey_RejectsInvalidOrShortKeys(string? rawKey)
    {
        Assert.Throws<FormatException>(() => EscrowCredentialCrypto.ParseMasterKey(rawKey));
    }

    [Fact]
    public void EscrowCrypto_ParseMasterKey_RejectsShortBase64Key()
    {
        var shortBase64 = Convert.ToBase64String(new byte[16]);
        Assert.Throws<FormatException>(() => EscrowCredentialCrypto.ParseMasterKey(shortBase64));
    }

    #endregion

    #region RdpPolicyEvaluator

    [Fact]
    public void RdpPolicy_OnlineUnprotectedNoSessions_AllowedWithoutActiveSessionWarning()
    {
        var device = CreateDevice(
            hostname: "  DFU-WS-01.lab.local  ",
            enrollmentState: EnrollmentState.Active,
            lastSeenUtc: Now.AddSeconds(-10),
            protectedModeActive: false);

        var result = RdpPolicyEvaluator.Evaluate(device, [], Now);

        Assert.True(result.Allowed);
        Assert.Equal("DFU-WS-01.lab.local", result.ApprovedHostname);
        Assert.Null(result.BlockedReason);
        Assert.Equal(ConnectivityState.Online, result.ConnectivityState);
        Assert.Single(result.Warnings);
        Assert.Contains("NLA", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RdpPolicy_OnlineUnprotectedWithActiveSessions_AllowedWithActiveSessionWarning()
    {
        var device = CreateDevice(
            hostname: "DFU-WS-02",
            enrollmentState: EnrollmentState.Active,
            lastSeenUtc: Now.AddSeconds(-5),
            protectedModeActive: false);

        var sessions = new[]
        {
            new DeviceSession(device.DeviceId, Guid.NewGuid(), 1, "S-1-5-21-1001", "Active", Now.AddSeconds(-5))
        };

        var result = RdpPolicyEvaluator.Evaluate(device, sessions, Now);

        Assert.True(result.Allowed);
        Assert.Equal("DFU-WS-02", result.ApprovedHostname);
        Assert.Null(result.BlockedReason);
        Assert.Contains(result.Warnings, w => w.Contains("Active session detected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, w => w.Contains("locks the physical screen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RdpPolicy_ProtectedModeActive_Denied()
    {
        var device = CreateDevice(
            hostname: "DFU-WS-03",
            enrollmentState: EnrollmentState.Active,
            lastSeenUtc: Now.AddSeconds(-5),
            protectedModeActive: true,
            protectedModeReason: "Active Cellebrite extraction");

        var result = RdpPolicyEvaluator.Evaluate(device, [], Now);

        Assert.False(result.Allowed);
        Assert.Null(result.ApprovedHostname);
        Assert.Contains("Protected Work Mode", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Active Cellebrite extraction", result.BlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void RdpPolicy_StaleWorkstation_Denied()
    {
        var device = CreateDevice(
            hostname: "DFU-WS-04",
            enrollmentState: EnrollmentState.Active,
            lastSeenUtc: Now.AddSeconds(-60),
            protectedModeActive: false);

        var result = RdpPolicyEvaluator.Evaluate(device, [], Now);

        Assert.False(result.Allowed);
        Assert.Null(result.ApprovedHostname);
        Assert.Equal(ConnectivityState.Stale, result.ConnectivityState);
        Assert.Contains("Stale", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(180)]
    [InlineData(null)]
    public void RdpPolicy_OfflineWorkstation_Denied(int? ageSeconds)
    {
        var lastSeen = ageSeconds.HasValue ? Now.AddSeconds(-ageSeconds.Value) : (DateTimeOffset?)null;
        var device = CreateDevice(
            hostname: "DFU-WS-05",
            enrollmentState: EnrollmentState.Active,
            lastSeenUtc: lastSeen,
            protectedModeActive: false);

        var result = RdpPolicyEvaluator.Evaluate(device, [], Now);

        Assert.False(result.Allowed);
        Assert.Null(result.ApprovedHostname);
        Assert.Equal(ConnectivityState.Offline, result.ConnectivityState);
        Assert.Contains("Offline", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EnrollmentState.Revoked)]
    [InlineData(EnrollmentState.Pending)]
    [InlineData(EnrollmentState.Decommissioned)]
    public void RdpPolicy_NonActiveEnrollmentState_Denied(EnrollmentState state)
    {
        var device = CreateDevice(
            hostname: "DFU-WS-06",
            enrollmentState: state,
            lastSeenUtc: Now.AddSeconds(-5),
            protectedModeActive: false);

        var result = RdpPolicyEvaluator.Evaluate(device, [], Now);

        Assert.False(result.Allowed);
        Assert.Null(result.ApprovedHostname);
        Assert.Contains(state.ToString(), result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ws01; calc")]
    [InlineData("ws..01")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ws01 /v:evil")]
    [InlineData("-ws01")]
    [InlineData("ws01\r\nfull address:s:evil")]
    public void RdpPolicy_InjectedOrInvalidHostname_Denied(string badHostname)
    {
        Assert.False(RdpPolicyEvaluator.IsValidSafeHostname(badHostname));

        var device = CreateDevice(
            hostname: badHostname,
            enrollmentState: EnrollmentState.Active,
            lastSeenUtc: Now.AddSeconds(-5),
            protectedModeActive: false);

        var result = RdpPolicyEvaluator.Evaluate(device, [], Now);

        Assert.False(result.Allowed);
        Assert.Null(result.ApprovedHostname);
        Assert.Contains("hostname", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region ScopeAuthorizationService

    [Theory]
    [InlineData(HiveRole.RemoteAccess, true)]
    [InlineData(HiveRole.BreakGlassCustodian, true)]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.Supervisor, false)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.SoftwareAdmin, false)]
    [InlineData(HiveRole.SoftwareApprover, false)]
    [InlineData(HiveRole.Auditor, false)]
    public void CanRequestRdp_AllowsExpectedRolesInSite_DeniesOthers(HiveRole role, bool expected)
    {
        var actor = CreateUser(roles: [role], sites: ["site-a"]);

        Assert.Equal(expected, ScopeAuthorizationService.CanRequestRdp(actor, "site-a"));
        Assert.False(ScopeAuthorizationService.CanRequestRdp(actor, "site-b"));
        Assert.False(ScopeAuthorizationService.CanRequestRdp(actor, " "));
    }

    [Fact]
    public void CanRequestRdp_DeviceActorDenied_EvenWithAllRolesAndAllSites()
    {
        var deviceActor = CreateDeviceActor(roles: Enum.GetValues<HiveRole>());

        Assert.False(ScopeAuthorizationService.CanRequestRdp(deviceActor, "site-a"));
    }

    [Fact]
    public void CanEscrowWorkstationCredential_AllowsAssignedUserAndSiteCustodianOrAdmin_DeniesOthers()
    {
        var assignedExaminer = CreateUser(sid: "S-1-5-21-1001", roles: [HiveRole.Examiner], sites: ["site-a"]);
        var otherExaminer = CreateUser(sid: "S-1-5-21-1002", roles: [HiveRole.Examiner], sites: ["site-a"]);
        var noRolesAssignedUser = CreateUser(sid: "S-1-5-21-1001", roles: [], sites: ["site-a"]);
        var custodianInSite = CreateUser(sid: "S-1-5-21-3001", roles: [HiveRole.BreakGlassCustodian], sites: ["site-a"]);
        var adminInSite = CreateUser(sid: "S-1-5-21-4001", roles: [HiveRole.Administrator], sites: ["site-a"]);
        var supervisorInSite = CreateUser(sid: "S-1-5-21-5001", roles: [HiveRole.Supervisor], sites: ["site-a"]);
        var remoteAccessInSite = CreateUser(sid: "S-1-5-21-6001", roles: [HiveRole.RemoteAccess], sites: ["site-a"]);
        var auditorInSite = CreateUser(sid: "S-1-5-21-7001", roles: [HiveRole.Auditor], sites: ["site-a"]);
        var deviceActor = CreateDeviceActor(sid: "S-1-5-21-1001", roles: Enum.GetValues<HiveRole>());

        Assert.True(ScopeAuthorizationService.CanEscrowWorkstationCredential(assignedExaminer, "site-a", "s-1-5-21-1001"));
        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(otherExaminer, "site-a", "S-1-5-21-1001"));
        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(assignedExaminer, "site-a", null));
        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(noRolesAssignedUser, "site-a", "S-1-5-21-1001"));

        Assert.True(ScopeAuthorizationService.CanEscrowWorkstationCredential(custodianInSite, "site-a", "S-1-5-21-1001"));
        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(custodianInSite, "site-b", "S-1-5-21-1001"));

        Assert.True(ScopeAuthorizationService.CanEscrowWorkstationCredential(adminInSite, "site-a", "S-1-5-21-1001"));
        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(adminInSite, "site-b", "S-1-5-21-1001"));

        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(supervisorInSite, "site-a", "S-1-5-21-1001"));
        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(remoteAccessInSite, "site-a", "S-1-5-21-1001"));
        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(auditorInSite, "site-a", "S-1-5-21-1001"));

        Assert.False(ScopeAuthorizationService.CanEscrowWorkstationCredential(deviceActor, "site-a", "S-1-5-21-1001"));
    }

    [Theory]
    [InlineData(HiveRole.BreakGlassCustodian, true)]
    [InlineData(HiveRole.Administrator, false)]
    [InlineData(HiveRole.Supervisor, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    [InlineData(HiveRole.Auditor, false)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.SoftwareAdmin, false)]
    [InlineData(HiveRole.SoftwareApprover, false)]
    public void CanAccessBreakGlassCredential_StrictlyRequiresBreakGlassCustodianInSite(HiveRole role, bool expected)
    {
        var scopedActor = CreateUser(roles: [role], sites: ["site-a"], allSites: false);
        var allSitesActor = CreateUser(roles: [role], sites: [], allSites: true);

        Assert.Equal(expected, ScopeAuthorizationService.CanAccessBreakGlassCredential(scopedActor, "site-a"));
        Assert.False(ScopeAuthorizationService.CanAccessBreakGlassCredential(scopedActor, "site-b"));
        Assert.Equal(expected, ScopeAuthorizationService.CanAccessBreakGlassCredential(allSitesActor, "site-b"));
        Assert.False(ScopeAuthorizationService.CanAccessBreakGlassCredential(allSitesActor, " "));
    }

    [Fact]
    public void CanAccessBreakGlassCredential_AllNonCustodianRolesCombined_StillDenied()
    {
        var superAdminWithoutCustodian = CreateUser(
            roles:
            [
                HiveRole.Administrator,
                HiveRole.Supervisor,
                HiveRole.RemoteAccess,
                HiveRole.Auditor,
                HiveRole.Examiner,
                HiveRole.SoftwareAdmin,
                HiveRole.SoftwareApprover
            ],
            sites: ["site-a"],
            allSites: true);

        Assert.False(ScopeAuthorizationService.CanAccessBreakGlassCredential(superAdminWithoutCustodian, "site-a"));
    }

    [Fact]
    public void CanAccessBreakGlassCredential_DeviceActor_Denied()
    {
        var deviceActor = CreateDeviceActor(roles: [HiveRole.BreakGlassCustodian]);

        Assert.False(ScopeAuthorizationService.CanAccessBreakGlassCredential(deviceActor, "site-a"));
    }

    #endregion
}
