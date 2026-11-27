using Hive.Domain.Identity;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class LocalAuthDomainTests
{
    private const string ValidPassphrase = "correct-horse-battery-staple";

    private static ActorIdentity CreateUser(
        IEnumerable<HiveRole>? roles = null,
        bool allSites = false) =>
        new(
            UserSid: "hive-user:11111111-1111-1111-1111-111111111111",
            DisplayName: "Local Test User",
            Roles: new HashSet<HiveRole>(roles ?? []),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: allSites,
            IsDevice: false,
            DeviceId: null);

    private static UserAccount CreateUserAccount(
        Guid? userId = null,
        DateTimeOffset? lockedUntilUtc = null)
    {
        var id = userId ?? Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        return new UserAccount(
            UserId: id,
            Username: "Forensic.Admin",
            UsernameNormalized: UserAccount.NormalizeUsername("Forensic.Admin"),
            DisplayName: "Forensic Admin",
            PasswordHash: "pbkdf2-sha512$210000$AAAA$BBBB",
            Roles: new HashSet<HiveRole>([HiveRole.Administrator]),
            AllowedSiteIds: new HashSet<string>(),
            AllSitesAccess: true,
            IsActive: true,
            MustChangePassword: false,
            FailedLoginCount: 0,
            LockedUntilUtc: lockedUntilUtc,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
    }

    [Fact]
    public void PasswordHasher_IdenticalInputsProduceUniqueSaltsAndVerify()
    {
        var hash1 = PasswordHasher.HashPassword(ValidPassphrase);
        var hash2 = PasswordHasher.HashPassword(ValidPassphrase);

        Assert.NotEqual(hash1, hash2);

        var parts1 = hash1.Split('$');
        var parts2 = hash2.Split('$');
        Assert.Equal(4, parts1.Length);
        Assert.Equal(4, parts2.Length);
        Assert.Equal("pbkdf2-sha512", parts1[0]);
        Assert.Equal("pbkdf2-sha512", parts2[0]);
        Assert.NotEqual(parts1[2], parts2[2]);

        Assert.True(PasswordHasher.VerifyPassword(ValidPassphrase, hash1));
        Assert.True(PasswordHasher.VerifyPassword(ValidPassphrase, hash2));
        Assert.False(PasswordHasher.VerifyPassword("wrong-passphrase-1234", hash1));
        Assert.False(PasswordHasher.VerifyPassword(ValidPassphrase.ToUpperInvariant(), hash1));
    }

    [Theory]
    [InlineData(null, "pbkdf2-sha512$210000$AAAA$BBBB")]
    [InlineData("", "pbkdf2-sha512$210000$AAAA$BBBB")]
    [InlineData(ValidPassphrase, null)]
    [InlineData(ValidPassphrase, "")]
    [InlineData(ValidPassphrase, "   ")]
    [InlineData(ValidPassphrase, "not-a-valid-hash")]
    [InlineData(ValidPassphrase, "pbkdf2-sha256$210000$AAAA$BBBB")]
    [InlineData(ValidPassphrase, "pbkdf2-sha512$notanumber$AAAA$BBBB")]
    [InlineData(ValidPassphrase, "pbkdf2-sha512$9999$AAAA$BBBB")]
    [InlineData(ValidPassphrase, "pbkdf2-sha512$210000$***invalid-base64***$BBBB")]
    [InlineData(ValidPassphrase, "pbkdf2-sha512$210000$$BBBB")]
    [InlineData(ValidPassphrase, "pbkdf2-sha512$210000$AAAA$")]
    public void PasswordHasher_RejectsNullEmptyAndMalformedInputsSafely(string? password, string? encodedHash)
    {
        Assert.False(PasswordHasher.VerifyPassword(password, encodedHash));
    }

    [Fact]
    public void PasswordHasher_HashPasswordRejectsInvalidPolicyInputs()
    {
        Assert.Throws<ArgumentException>(() => PasswordHasher.HashPassword("short"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void PasswordPolicy_RejectsNullEmptyOrWhitespace(string? password)
    {
        var (isValid, error) = PasswordPolicy.Validate(password);

        Assert.False(isValid);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void PasswordPolicy_EnforcesMinAndMaxLengths()
    {
        var tooShort = new string('a', PasswordPolicy.MinLength - 1);
        var minExact = new string('a', PasswordPolicy.MinLength);
        var maxExact = new string('a', PasswordPolicy.MaxLength);
        var tooLong = new string('a', PasswordPolicy.MaxLength + 1);

        Assert.False(PasswordPolicy.Validate(tooShort).IsValid);
        Assert.True(PasswordPolicy.Validate(minExact).IsValid);
        Assert.True(PasswordPolicy.Validate(ValidPassphrase).IsValid);
        Assert.True(PasswordPolicy.Validate(maxExact).IsValid);
        Assert.False(PasswordPolicy.Validate(tooLong).IsValid);
    }

    [Theory]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.Supervisor, false)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.Auditor, false)]
    [InlineData(HiveRole.SoftwareAdmin, false)]
    [InlineData(HiveRole.SoftwareApprover, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    public void ScopeAuthorizationService_CanManageUsers_EnforcesAdministratorOnly(
        HiveRole role,
        bool expectedAllowed)
    {
        var actor = CreateUser(roles: [role], allSites: true);

        Assert.Equal(expectedAllowed, ScopeAuthorizationService.CanManageUsers(actor));
    }

    [Fact]
    public void ScopeAuthorizationService_CanManageUsers_DeniesEmptyRolesAndDeviceActors()
    {
        var rolelessUser = CreateUser(roles: [], allSites: true);
        var deviceAdmin = new ActorIdentity(
            UserSid: "device",
            DisplayName: "LAB-PC-01",
            Roles: new HashSet<HiveRole>([HiveRole.Administrator]),
            AllowedSiteIds: new HashSet<string>(["site-a"]),
            AllSitesAccess: true,
            IsDevice: true,
            DeviceId: Guid.NewGuid());

        Assert.False(ScopeAuthorizationService.CanManageUsers(rolelessUser));
        Assert.False(ScopeAuthorizationService.CanManageUsers(deviceAdmin));
    }

    [Fact]
    public void UserRefreshToken_IsActiveAt_RespectsExpiryAndRevocation()
    {
        var issuedAt = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var activeToken = new UserRefreshToken(
            TokenId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            TokenHashSha256: "abc123",
            ClientLabel: "Hive.App",
            IssuedAtUtc: issuedAt,
            ExpiresAtUtc: expiresAt,
            RevokedAtUtc: null);

        Assert.True(activeToken.IsActiveAt(issuedAt));
        Assert.True(activeToken.IsActiveAt(expiresAt.AddTicks(-1)));
        Assert.False(activeToken.IsActiveAt(expiresAt));
        Assert.False(activeToken.IsActiveAt(expiresAt.AddMinutes(1)));

        var revokedToken = activeToken with { RevokedAtUtc = issuedAt.AddMinutes(5) };
        Assert.False(revokedToken.IsActiveAt(issuedAt.AddMinutes(1)));
        Assert.False(revokedToken.IsActiveAt(issuedAt.AddMinutes(10)));
    }

    [Fact]
    public void UserAccount_Invariants_LockoutNormalizationAndSubjectId()
    {
        var userId = Guid.Parse("22222222-3333-4444-5555-666666666666");
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

        var unlocked = CreateUserAccount(userId: userId, lockedUntilUtc: null);
        Assert.Equal("hive-user:22222222-3333-4444-5555-666666666666", unlocked.SubjectId);
        Assert.False(unlocked.IsLockedAt(now));

        var lockUntil = now.AddMinutes(15);
        var locked = CreateUserAccount(userId: userId, lockedUntilUtc: lockUntil);
        Assert.True(locked.IsLockedAt(now));
        Assert.True(locked.IsLockedAt(lockUntil.AddTicks(-1)));
        Assert.False(locked.IsLockedAt(lockUntil));
        Assert.False(locked.IsLockedAt(lockUntil.AddMinutes(1)));

        Assert.Equal("admin.user", UserAccount.NormalizeUsername("  Admin.User  "));
        Assert.Throws<ArgumentNullException>(() => UserAccount.NormalizeUsername(null!));
        Assert.Throws<ArgumentException>(() => UserAccount.NormalizeUsername(""));
        Assert.Throws<ArgumentException>(() => UserAccount.NormalizeUsername("   "));
    }
}
