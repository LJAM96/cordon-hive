namespace Hive.Contracts;

public sealed record LoginRequest(
    string Username,
    string Password,
    string? ClientLabel = null);

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    bool MustChangePassword,
    MeResponse User);

public sealed record RefreshTokenRequest(
    string RefreshToken);

public sealed record LogoutRequest(
    string? RefreshToken = null);

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);

public sealed record UserAccountDto(
    Guid UserId,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> AllowedSiteIds,
    bool AllSitesAccess,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateUserRequest(
    string Username,
    string DisplayName,
    string InitialPassword,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> AllowedSiteIds,
    bool AllSitesAccess);

public sealed record UpdateUserRequest(
    string? DisplayName,
    IReadOnlyList<string>? Roles,
    IReadOnlyList<string>? AllowedSiteIds,
    bool? AllSitesAccess,
    bool? IsActive);
