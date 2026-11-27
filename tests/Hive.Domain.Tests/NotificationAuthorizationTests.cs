using Hive.Domain.Identity;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class NotificationAuthorizationTests
{
    private static ActorIdentity CreateUser(
        string sid = "S-1-5-21-1001",
        params HiveRole[] roles) =>
        new(
            UserSid: sid,
            DisplayName: "Test User",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: false,
            IsDevice: false,
            DeviceId: null);

    private static ActorIdentity CreateDevice(params HiveRole[] roles) =>
        new(
            UserSid: "device",
            DisplayName: "LAB-PC-01",
            Roles: new HashSet<HiveRole>(roles),
            AllowedSiteIds: new HashSet<string>(["site-a"], StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: true,
            IsDevice: true,
            DeviceId: Guid.NewGuid());

    [Theory]
    [InlineData(HiveRole.Supervisor, true)]
    [InlineData(HiveRole.SoftwareAdmin, true)]
    [InlineData(HiveRole.Administrator, true)]
    [InlineData(HiveRole.Examiner, false)]
    [InlineData(HiveRole.Auditor, false)]
    [InlineData(HiveRole.SoftwareApprover, false)]
    [InlineData(HiveRole.RemoteAccess, false)]
    public void CanPublishNotifications_EnforcesRoleMatrix(HiveRole role, bool expectedAllowed)
    {
        var actor = CreateUser(roles: role);
        Assert.Equal(expectedAllowed, ScopeAuthorizationService.CanPublishNotifications(actor));
    }

    [Fact]
    public void CanPublishNotifications_DeniesDeviceAndEmptyRoles()
    {
        var noRoles = CreateUser();
        var deviceWithAdmin = CreateDevice(HiveRole.Administrator, HiveRole.Supervisor, HiveRole.SoftwareAdmin);

        Assert.False(ScopeAuthorizationService.CanPublishNotifications(noRoles));
        Assert.False(ScopeAuthorizationService.CanPublishNotifications(deviceWithAdmin));
    }

    [Fact]
    public void CanCancelNotification_AllowsCreatorSupervisorAndAnyAdministrator()
    {
        var creatorSupervisor = CreateUser(sid: "S-1-5-21-2001", HiveRole.Supervisor);
        var otherSupervisor = CreateUser(sid: "S-1-5-21-2002", HiveRole.Supervisor);
        var admin = CreateUser(sid: "S-1-5-21-9999", HiveRole.Administrator);
        var examiner = CreateUser(sid: "S-1-5-21-2001", HiveRole.Examiner);
        var softwareAdmin = CreateUser(sid: "S-1-5-21-2001", HiveRole.SoftwareAdmin);
        var auditor = CreateUser(sid: "S-1-5-21-2001", HiveRole.Auditor);
        var device = CreateDevice(HiveRole.Administrator, HiveRole.Supervisor);

        // Case-insensitive creator SID match for Supervisor
        Assert.True(ScopeAuthorizationService.CanCancelNotification(creatorSupervisor, "s-1-5-21-2001"));
        Assert.True(ScopeAuthorizationService.CanCancelNotification(admin, "S-1-5-21-2001"));
        Assert.True(ScopeAuthorizationService.CanCancelNotification(admin, string.Empty));

        Assert.False(ScopeAuthorizationService.CanCancelNotification(otherSupervisor, "S-1-5-21-2001"));
        Assert.False(ScopeAuthorizationService.CanCancelNotification(creatorSupervisor, " "));
        Assert.False(ScopeAuthorizationService.CanCancelNotification(examiner, "S-1-5-21-2001"));
        Assert.False(ScopeAuthorizationService.CanCancelNotification(softwareAdmin, "S-1-5-21-2001"));
        Assert.False(ScopeAuthorizationService.CanCancelNotification(auditor, "S-1-5-21-2001"));
        Assert.False(ScopeAuthorizationService.CanCancelNotification(device, "device"));
    }

    [Theory]
    [InlineData(HiveRole.Examiner, "SoftwareUpdate", false)]
    [InlineData(HiveRole.Supervisor, "SoftwareUpdate", false)]
    [InlineData(HiveRole.Auditor, "softwareupdate", false)]
    [InlineData(HiveRole.SoftwareApprover, "SoftwareUpdate", false)]
    [InlineData(HiveRole.RemoteAccess, "SoftwareUpdate", false)]
    [InlineData(HiveRole.SoftwareAdmin, "SoftwareUpdate", true)]
    [InlineData(HiveRole.Administrator, "SoftwareUpdate", true)]
    [InlineData(HiveRole.Examiner, "RequiredAction", true)]
    [InlineData(HiveRole.Examiner, "Announcement", true)]
    [InlineData(HiveRole.Examiner, "Informational", true)]
    [InlineData(HiveRole.Examiner, "Maintenance", true)]
    [InlineData(HiveRole.Examiner, "CriticalCondition", true)]
    [InlineData(HiveRole.Supervisor, "RequiredAction", true)]
    [InlineData(HiveRole.Supervisor, "Announcement", true)]
    public void CanCompleteNotification_EnforcesTypeAndRoleRules(
        HiveRole role,
        string notificationType,
        bool expectedAllowed)
    {
        var actor = CreateUser(roles: role);
        Assert.Equal(expectedAllowed, ScopeAuthorizationService.CanCompleteNotification(actor, notificationType));
    }

    [Fact]
    public void CanCompleteNotification_DeniesDeviceAndEmptyRoles()
    {
        var noRoles = CreateUser();
        var device = CreateDevice(HiveRole.Administrator, HiveRole.SoftwareAdmin);

        Assert.False(ScopeAuthorizationService.CanCompleteNotification(noRoles, "RequiredAction"));
        Assert.False(ScopeAuthorizationService.CanCompleteNotification(noRoles, "Announcement"));
        Assert.False(ScopeAuthorizationService.CanCompleteNotification(device, "RequiredAction"));
        Assert.False(ScopeAuthorizationService.CanCompleteNotification(device, "SoftwareUpdate"));
    }
}
