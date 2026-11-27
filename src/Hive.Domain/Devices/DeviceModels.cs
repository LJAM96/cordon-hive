namespace Hive.Domain.Devices;

public enum EnrollmentState
{
    Pending,
    Active,
    Revoked,
    Decommissioned
}

public enum ConnectivityState
{
    Online,
    Stale,
    Offline
}

public sealed record Device(
    Guid DeviceId,
    string SiteId,
    string Hostname,
    string? AssetNumber,
    string? AssignedUserSid,
    string CertificateThumbprint,
    EnrollmentState EnrollmentState,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? LastSeenUtc,
    Guid? BootId,
    string? AgentVersion,
    long PolicyRevision,
    long RowVersion,
    bool ProtectedModeActive = false,
    string? ProtectedModeReason = null);

public sealed record DeviceAssignment(
    Guid AssignmentId,
    Guid DeviceId,
    string? UserSid,
    string AssignedBySid,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset? ValidToUtc);

public sealed record DeviceSession(
    Guid DeviceId,
    Guid BootId,
    int SessionId,
    string? UserSid,
    string SessionState,
    DateTimeOffset ObservedAtUtc);

public static class DeviceConnectivityEvaluator
{
    public static readonly TimeSpan DefaultOnlineThreshold = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(120);

    public static ConnectivityState Evaluate(
        DateTimeOffset? lastSeenUtc,
        DateTimeOffset nowUtc,
        TimeSpan? onlineThreshold = null,
        TimeSpan? staleThreshold = null)
    {
        if (lastSeenUtc is null)
        {
            return ConnectivityState.Offline;
        }

        var onlineMax = onlineThreshold ?? DefaultOnlineThreshold;
        var staleMax = staleThreshold ?? DefaultStaleThreshold;
        var age = nowUtc - lastSeenUtc.Value;

        if (age < TimeSpan.Zero)
        {
            return ConnectivityState.Online;
        }

        if (age < onlineMax)
        {
            return ConnectivityState.Online;
        }

        if (age <= staleMax)
        {
            return ConnectivityState.Stale;
        }

        return ConnectivityState.Offline;
    }
}
