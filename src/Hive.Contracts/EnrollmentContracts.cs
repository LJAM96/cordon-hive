namespace Hive.Contracts;

public sealed record CreateEnrollmentTokenRequest(
    string SiteId,
    int TtlMinutes = 15);

public sealed record CreateEnrollmentTokenResponse(
    Guid TokenId,
    string Token,
    string SiteId,
    DateTimeOffset ExpiresAtUtc);

public sealed record EnrollDeviceRequest(
    string Token,
    string Hostname,
    string CertificateThumbprint,
    string? AssetNumber,
    string AgentVersion);

public sealed record EnrollDeviceResponse(
    Guid DeviceId,
    string SiteId,
    long PolicyRevision,
    DateTimeOffset EnrolledAtUtc);
