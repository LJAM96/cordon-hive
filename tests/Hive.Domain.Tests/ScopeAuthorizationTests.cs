using Hive.Domain.Identity;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class ScopeAuthorizationTests
{
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

    private static ActorIdentity CreateDevice(Guid? deviceId = null, params HiveRole[] roles) =>
        new(
            UserSid: "device",
            DisplayName: "LAB-PC-01",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"]),
            AllSitesAccess: true,
            IsDevice: true,
            DeviceId: deviceId ?? Guid.NewGuid());

    [Fact]
    public void EmptyRoles_FailClosedOnAllUserOperations()
    {
        var actor = CreateUser(roles: [], sites: ["site-a"], allSites: true);

        Assert.False(ScopeAuthorizationService.CanCreateEnrollmentToken(actor, "site-a"));
        Assert.False(ScopeAuthorizationService.CanViewAllWorkstations(actor));
        Assert.False(ScopeAuthorizationService.CanViewWorkstation(actor, "site-a", actor.UserSid));
        Assert.False(ScopeAuthorizationService.CanManageAssignment(actor, "site-a"));
        Assert.False(ScopeAuthorizationService.CanRevokeDevice(actor, "site-a"));
        Assert.False(ScopeAuthorizationService.CanViewAudit(actor));
    }

    [Fact]
    public void DeviceActor_CannotCallUserEndpoints_EvenWithRolesAndAllSites()
    {
        var deviceId = Guid.NewGuid();
        var device = CreateDevice(deviceId, Enum.GetValues<HiveRole>());

        Assert.False(ScopeAuthorizationService.CanCreateEnrollmentToken(device, "site-a"));
        Assert.False(ScopeAuthorizationService.CanViewAllWorkstations(device));
        Assert.False(ScopeAuthorizationService.CanViewWorkstation(device, "site-a", "device"));
        Assert.False(ScopeAuthorizationService.CanManageAssignment(device, "site-a"));
        Assert.False(ScopeAuthorizationService.CanRevokeDevice(device, "site-a"));
        Assert.False(ScopeAuthorizationService.CanViewAudit(device));

        Assert.True(ScopeAuthorizationService.CanSendHeartbeat(device, deviceId));
        Assert.False(ScopeAuthorizationService.CanSendHeartbeat(device, Guid.NewGuid()));
    }

    [Fact]
    public void UserActor_CannotSendDeviceHeartbeat()
    {
        var deviceId = Guid.NewGuid();
        var admin = CreateUser(roles: [HiveRole.Administrator], allSites: true);

        Assert.False(ScopeAuthorizationService.CanSendHeartbeat(admin, deviceId));
    }

    [Theory]
    [InlineData(HiveRole.Examiner, false, false, false, false, false)]
    [InlineData(HiveRole.Supervisor, false, true, true, false, false)]
    [InlineData(HiveRole.SoftwareAdmin, false, false, false, false, false)]
    [InlineData(HiveRole.SoftwareApprover, false, false, false, false, false)]
    [InlineData(HiveRole.RemoteAccess, false, false, false, false, false)]
    [InlineData(HiveRole.Administrator, true, true, true, true, true)]
    [InlineData(HiveRole.Auditor, false, true, false, false, true)]
    public void SingleRole_EnforcesExpectedCapabilities(
        HiveRole role,
        bool canCreateToken,
        bool canViewAllWorkstations,
        bool canManageAssignment,
        bool canRevokeDevice,
        bool canViewAudit)
    {
        var actor = CreateUser(roles: [role], sites: ["site-a"]);

        Assert.Equal(canCreateToken, ScopeAuthorizationService.CanCreateEnrollmentToken(actor, "site-a"));
        Assert.Equal(canViewAllWorkstations, ScopeAuthorizationService.CanViewAllWorkstations(actor));
        Assert.Equal(canManageAssignment, ScopeAuthorizationService.CanManageAssignment(actor, "site-a"));
        Assert.Equal(canRevokeDevice, ScopeAuthorizationService.CanRevokeDevice(actor, "site-a"));
        Assert.Equal(canViewAudit, ScopeAuthorizationService.CanViewAudit(actor));
    }

    [Theory]
    [InlineData(HiveRole.Supervisor)]
    [InlineData(HiveRole.Administrator)]
    [InlineData(HiveRole.Auditor)]
    public void CrossSiteAccess_DeniedWhenSiteNotAllowed(HiveRole role)
    {
        var actor = CreateUser(roles: [role], sites: ["site-a"], allSites: false);

        Assert.True(ScopeAuthorizationService.CanViewWorkstation(actor, "site-a", "S-1-5-21-9999"));
        Assert.False(ScopeAuthorizationService.CanViewWorkstation(actor, "site-b", "S-1-5-21-9999"));
        Assert.False(ScopeAuthorizationService.CanCreateEnrollmentToken(actor, "site-b"));
        Assert.False(ScopeAuthorizationService.CanManageAssignment(actor, "site-b"));
        Assert.False(ScopeAuthorizationService.CanRevokeDevice(actor, "site-b"));
    }

    [Fact]
    public void AllSitesAccess_GrantsCrossSiteWhenRolePermits()
    {
        var admin = CreateUser(roles: [HiveRole.Administrator], allSites: true);

        Assert.True(ScopeAuthorizationService.CanCreateEnrollmentToken(admin, "site-z"));
        Assert.True(ScopeAuthorizationService.CanViewWorkstation(admin, "site-z", null));
        Assert.True(ScopeAuthorizationService.CanManageAssignment(admin, "site-z"));
        Assert.True(ScopeAuthorizationService.CanRevokeDevice(admin, "site-z"));
        Assert.False(ScopeAuthorizationService.CanCreateEnrollmentToken(admin, " "));
    }

    [Fact]
    public void Examiner_CanViewOnlyAssignedWorkstation()
    {
        var examiner = CreateUser(sid: "S-1-5-21-2001", roles: [HiveRole.Examiner], sites: ["site-a"]);

        Assert.True(ScopeAuthorizationService.CanViewWorkstation(examiner, "site-a", "s-1-5-21-2001"));
        Assert.False(ScopeAuthorizationService.CanViewWorkstation(examiner, "site-a", "S-1-5-21-2002"));
        Assert.False(ScopeAuthorizationService.CanViewWorkstation(examiner, "site-a", null));
        Assert.False(ScopeAuthorizationService.CanViewAllWorkstations(examiner));
    }
}
