namespace Hive.Domain.Identity;

public enum HiveRole
{
    Examiner,
    Supervisor,
    SoftwareAdmin,
    SoftwareApprover,
    RemoteAccess,
    BreakGlassCustodian,
    Administrator,
    Auditor
}

public sealed record ActorIdentity(
    string UserSid,
    string DisplayName,
    IReadOnlySet<HiveRole> Roles,
    IReadOnlySet<string> AllowedSiteIds,
    bool AllSitesAccess,
    bool IsDevice = false,
    Guid? DeviceId = null)
{
    public bool HasRole(HiveRole role) => Roles.Contains(role);

    public bool HasAnyRole(params HiveRole[] roles) => roles.Any(Roles.Contains);

    public bool CanAccessSite(string siteId) =>
        !string.IsNullOrWhiteSpace(siteId) && (AllSitesAccess || AllowedSiteIds.Contains(siteId));
}

public static class ScopeAuthorizationService
{
    public static bool CanCreateEnrollmentToken(ActorIdentity actor, string siteId) =>
        !actor.IsDevice &&
        actor.HasRole(HiveRole.Administrator) &&
        actor.CanAccessSite(siteId);

    public static bool CanViewAllWorkstations(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.Supervisor, HiveRole.Administrator, HiveRole.Auditor);

    public static bool CanViewWorkstation(ActorIdentity actor, string siteId, string? assignedUserSid)
    {
        if (actor.IsDevice)
        {
            return false;
        }

        if (actor.HasAnyRole(HiveRole.Supervisor, HiveRole.Administrator, HiveRole.Auditor) &&
            actor.CanAccessSite(siteId))
        {
            return true;
        }

        return actor.HasRole(HiveRole.Examiner) &&
               !string.IsNullOrWhiteSpace(assignedUserSid) &&
               string.Equals(actor.UserSid, assignedUserSid, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanManageAssignment(ActorIdentity actor, string siteId) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.Supervisor, HiveRole.Administrator) &&
        actor.CanAccessSite(siteId);

    public static bool CanRevokeDevice(ActorIdentity actor, string siteId) =>
        !actor.IsDevice &&
        actor.HasRole(HiveRole.Administrator) &&
        actor.CanAccessSite(siteId);

    public static bool CanViewAudit(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.Auditor, HiveRole.Administrator);

    public static bool CanManageUsers(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.HasRole(HiveRole.Administrator);

    public static bool CanViewSoftwareCatalogue(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.Roles.Count > 0;

    public static bool CanManageSoftwareCatalogue(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.SoftwareAdmin, HiveRole.Administrator);

    public static bool CanApproveSoftwareRelease(ActorIdentity actor, string createdBySid) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.SoftwareApprover, HiveRole.Administrator) &&
        !string.IsNullOrWhiteSpace(createdBySid) &&
        !string.Equals(actor.UserSid, createdBySid, StringComparison.OrdinalIgnoreCase);

    public static bool CanManageSoftwareExemptions(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.SoftwareApprover, HiveRole.Administrator);

    public static bool CanViewFleetCompliance(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.HasAnyRole(
            HiveRole.Supervisor,
            HiveRole.SoftwareAdmin,
            HiveRole.SoftwareApprover,
            HiveRole.Administrator,
            HiveRole.Auditor);

    public static bool CanPublishNotifications(ActorIdentity actor) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.Supervisor, HiveRole.SoftwareAdmin, HiveRole.Administrator);

    public static bool CanCancelNotification(ActorIdentity actor, string createdBySid) =>
        !actor.IsDevice &&
        (actor.HasRole(HiveRole.Administrator) ||
         (actor.HasRole(HiveRole.Supervisor) &&
          !string.IsNullOrWhiteSpace(createdBySid) &&
          string.Equals(actor.UserSid, createdBySid, StringComparison.OrdinalIgnoreCase)));

    public static bool CanCompleteNotification(ActorIdentity actor, string notificationType)
    {
        if (actor.IsDevice || actor.Roles.Count == 0)
        {
            return false;
        }

        if (string.Equals(notificationType, "SoftwareUpdate", StringComparison.OrdinalIgnoreCase))
        {
            return actor.HasAnyRole(HiveRole.SoftwareAdmin, HiveRole.Administrator);
        }

        return true;
    }

    public static bool CanRequestRdp(ActorIdentity actor, string siteId) =>
        !actor.IsDevice &&
        actor.HasAnyRole(HiveRole.RemoteAccess, HiveRole.BreakGlassCustodian, HiveRole.Administrator) &&
        actor.CanAccessSite(siteId);

    public static bool CanEscrowWorkstationCredential(ActorIdentity actor, string siteId, string? assignedUserSid)
    {
        if (actor.IsDevice)
        {
            return false;
        }

        if (actor.HasAnyRole(HiveRole.BreakGlassCustodian, HiveRole.Administrator) &&
            actor.CanAccessSite(siteId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(assignedUserSid) &&
               actor.Roles.Count > 0 &&
               string.Equals(actor.UserSid, assignedUserSid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strict custody boundary: requires explicit BreakGlassCustodian role in the target site.
    /// Administrator, Supervisor, RemoteAccess, and Auditor have zero implicit reveal access.
    /// </summary>
    public static bool CanAccessBreakGlassCredential(ActorIdentity actor, string siteId) =>
        !actor.IsDevice &&
        actor.HasRole(HiveRole.BreakGlassCustodian) &&
        actor.CanAccessSite(siteId);

    public static bool CanSendHeartbeat(ActorIdentity actor, Guid deviceId) =>
        actor.IsDevice &&
        actor.DeviceId == deviceId;
}
