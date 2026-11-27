namespace Hive.Contracts;

public sealed record MeResponse(
    string UserSid,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> AllowedSiteIds,
    bool AllSitesAccess);
