namespace Hive.Contracts;

public sealed record RdpRequest(
    string Reason,
    bool UseEscrowedCredential = false);

public sealed record RdpAuthorizationResponse(
    Guid RequestId,
    Guid DeviceId,
    bool Allowed,
    string? ApprovedHostname,
    int RdpPort,
    string? BlockedReason,
    IReadOnlyList<string> Warnings,
    string ConnectivityState,
    bool ProtectedModeActive,
    bool HasEscrowedCredential,
    IReadOnlyList<ObservedSessionDto> ActiveSessions,
    DateTimeOffset EvaluatedAtUtc);

public sealed record EscrowCredentialStatusDto(
    Guid DeviceId,
    bool HasEscrowedCredential,
    string? AccountUsername,
    string? UpdatedBySid,
    DateTimeOffset? UpdatedAtUtc,
    int KeyVersion);

public sealed record UpsertEscrowCredentialRequest(
    string AccountUsername,
    string Password,
    string? Notes = null);

public sealed record RevealEscrowCredentialRequest(
    string Reason,
    string Purpose);

public sealed record RevealEscrowCredentialResponse(
    Guid DeviceId,
    string Hostname,
    string AccountUsername,
    string Password,
    string Purpose,
    string AuditCorrelationId,
    DateTimeOffset CheckedOutAtUtc,
    int RecommendedClearSeconds);
