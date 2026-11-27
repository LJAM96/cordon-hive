using System.Security.Claims;
using System.Text.Encodings.Web;
using Hive.Domain.Devices;
using Hive.Domain.Identity;
using Hive.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hive.Server.Authentication;

public static class HiveAuthSchemes
{
    public const string User = "UserAuth";
    public const string DeviceCert = "DeviceCertAuth";
}

public static class HiveAuthPolicies
{
    public const string UserOnly = "UserOnly";
    public const string DeviceOnly = "DeviceOnly";
    public const string UserOrDevice = "UserOrDevice";
}

public static class HiveClaimTypes
{
    public const string ActorType = "hive:actor_type";
    public const string UserSid = "hive:user_sid";
    public const string Role = "hive:role";
    public const string Site = "hive:site";
    public const string AllSites = "hive:all_sites";
    public const string DeviceId = "hive:device_id";
}

public sealed class AdRoleMappingOptions
{
    public const string SectionName = "AdRoleMapping";

    public Dictionary<string, string[]> RoleGroupMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string[]> SiteGroupMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string[] AllSitesGroups { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Maps Windows / AD group claims from Negotiate authentication onto Hive roles and site scopes.
/// </summary>
public sealed class AdRoleMappingClaimsTransformation : IClaimsTransformation
{
    private readonly IOptionsMonitor<AdRoleMappingOptions> _options;

    public AdRoleMappingClaimsTransformation(IOptionsMonitor<AdRoleMappingOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        // Only transform user identities (not device certificate principals or already-mapped testing principals).
        if (principal.HasClaim(HiveClaimTypes.ActorType, "Device") ||
            principal.HasClaim(c => c.Type == HiveClaimTypes.UserSid))
        {
            return Task.FromResult(principal);
        }

        var mappedIdentity = new ClaimsIdentity(identity);
        var userSid = principal.FindFirstValue(ClaimTypes.PrimarySid)
            ?? principal.FindFirstValue(ClaimTypes.Sid)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.Identity?.Name;

        if (!string.IsNullOrWhiteSpace(userSid))
        {
            mappedIdentity.AddClaim(new Claim(HiveClaimTypes.ActorType, "User"));
            mappedIdentity.AddClaim(new Claim(HiveClaimTypes.UserSid, userSid));
        }

        var groupValues = principal.Claims
            .Where(c => c.Type is ClaimTypes.GroupSid or ClaimTypes.Role or "groups")
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var opts = _options.CurrentValue;

        foreach (var (roleName, groups) in opts.RoleGroupMappings)
        {
            if (Enum.TryParse<HiveRole>(roleName, ignoreCase: true, out var role) &&
                groups.Any(groupValues.Contains))
            {
                mappedIdentity.AddClaim(new Claim(HiveClaimTypes.Role, role.ToString()));
            }
        }

        foreach (var (siteId, groups) in opts.SiteGroupMappings)
        {
            if (!string.IsNullOrWhiteSpace(siteId) && groups.Any(groupValues.Contains))
            {
                mappedIdentity.AddClaim(new Claim(HiveClaimTypes.Site, siteId));
            }
        }

        if (opts.AllSitesGroups.Any(groupValues.Contains))
        {
            mappedIdentity.AddClaim(new Claim(HiveClaimTypes.AllSites, "true"));
        }

        return Task.FromResult(new ClaimsPrincipal(mappedIdentity));
    }
}

public static class ClaimsPrincipalActorExtensions
{
    public static ActorIdentity ToActorIdentity(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.HasClaim(HiveClaimTypes.ActorType, "Device"))
        {
            var deviceIdRaw = principal.FindFirstValue(HiveClaimTypes.DeviceId);
            var siteId = principal.FindFirstValue(HiveClaimTypes.Site);
            Guid.TryParse(deviceIdRaw, out var deviceId);
            var sites = string.IsNullOrWhiteSpace(siteId)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { siteId };

            return new ActorIdentity(
                deviceId.ToString("D"),
                principal.Identity?.Name ?? deviceId.ToString("D"),
                new HashSet<HiveRole>(),
                sites,
                AllSitesAccess: false,
                IsDevice: true,
                DeviceId: deviceId);
        }

        var userSid = principal.FindFirstValue(HiveClaimTypes.UserSid)
            ?? principal.FindFirstValue(ClaimTypes.PrimarySid)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.Identity?.Name
            ?? string.Empty;

        var displayName = principal.FindFirstValue(ClaimTypes.Name) ?? userSid;

        var roles = new HashSet<HiveRole>();
        foreach (var claim in principal.FindAll(HiveClaimTypes.Role))
        {
            if (Enum.TryParse<HiveRole>(claim.Value, ignoreCase: true, out var parsedRole))
            {
                roles.Add(parsedRole);
            }
        }

        var allowedSites = principal.FindAll(HiveClaimTypes.Site)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allSites = string.Equals(
            principal.FindFirstValue(HiveClaimTypes.AllSites),
            "true",
            StringComparison.OrdinalIgnoreCase);

        return new ActorIdentity(
            userSid,
            displayName,
            roles,
            allowedSites,
            allSites,
            IsDevice: false,
            DeviceId: null);
    }
}

/// <summary>
/// Header-based authentication handler enabled ONLY in the Testing environment when
/// Authentication:EnableTestingHeaderAuth is explicitly true.
/// </summary>
public sealed class TestingHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string UserSidHeader = "X-Test-User-Sid";
    public const string UserNameHeader = "X-Test-User-Name";
    public const string RolesHeader = "X-Test-Roles";
    public const string SitesHeader = "X-Test-Sites";
    public const string DeviceThumbprintHeader = "X-Test-Device-Cert-Thumbprint";

    private readonly IDeviceRepository _deviceRepository;

    public TestingHeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDeviceRepository deviceRepository)
        : base(options, logger, encoder)
    {
        _deviceRepository = deviceRepository ?? throw new ArgumentNullException(nameof(deviceRepository));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.Equals(Scheme.Name, HiveAuthSchemes.DeviceCert, StringComparison.Ordinal))
        {
            return await AuthenticateDeviceAsync();
        }

        if (string.Equals(Scheme.Name, HiveAuthSchemes.User, StringComparison.Ordinal))
        {
            return AuthenticateUser();
        }

        return AuthenticateResult.NoResult();
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private AuthenticateResult AuthenticateUser()
    {
        if (!Request.Headers.TryGetValue(UserSidHeader, out var sidValues))
        {
            return AuthenticateResult.NoResult();
        }

        var userSid = sidValues.ToString().Trim();
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return AuthenticateResult.NoResult();
        }

        var displayName = Request.Headers.TryGetValue(UserNameHeader, out var nameValues) &&
                          !string.IsNullOrWhiteSpace(nameValues.ToString())
            ? nameValues.ToString().Trim()
            : userSid;

        var claims = new List<Claim>
        {
            new(HiveClaimTypes.ActorType, "User"),
            new(HiveClaimTypes.UserSid, userSid),
            new(ClaimTypes.NameIdentifier, userSid),
            new(ClaimTypes.Name, displayName)
        };

        if (Request.Headers.TryGetValue(RolesHeader, out var roleValues))
        {
            foreach (var rawRole in roleValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<HiveRole>(rawRole, ignoreCase: true, out var role))
                {
                    claims.Add(new Claim(HiveClaimTypes.Role, role.ToString()));
                    claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
                }
            }
        }

        if (Request.Headers.TryGetValue(SitesHeader, out var siteValues))
        {
            foreach (var rawSite in siteValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(rawSite, "*", StringComparison.Ordinal))
                {
                    claims.Add(new Claim(HiveClaimTypes.AllSites, "true"));
                }
                else
                {
                    claims.Add(new Claim(HiveClaimTypes.Site, rawSite));
                }
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private async Task<AuthenticateResult> AuthenticateDeviceAsync()
    {
        // Security invariant: NEVER trust X-Device-Id or user headers for device auth.
        if (!Request.Headers.TryGetValue(DeviceThumbprintHeader, out var thumbprintValues))
        {
            return AuthenticateResult.NoResult();
        }

        var thumbprint = thumbprintValues.ToString().Trim();
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return AuthenticateResult.NoResult();
        }

        var device = await _deviceRepository.GetByThumbprintAsync(thumbprint, Context.RequestAborted);
        if (device is null)
        {
            return AuthenticateResult.Fail("Unknown device certificate.");
        }

        if (device.EnrollmentState != EnrollmentState.Active)
        {
            return AuthenticateResult.Fail("Device certificate is revoked or inactive.");
        }

        var claims = new List<Claim>
        {
            new(HiveClaimTypes.ActorType, "Device"),
            new(HiveClaimTypes.DeviceId, device.DeviceId.ToString("D")),
            new(HiveClaimTypes.Site, device.SiteId),
            new(ClaimTypes.NameIdentifier, device.DeviceId.ToString("D")),
            new(ClaimTypes.Name, device.Hostname),
            new(ClaimTypes.Thumbprint, device.CertificateThumbprint)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
