using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Hive.Domain.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Hive.Server.Authentication;

public sealed class JwtAuthOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Issuer { get; init; } = "cordon-hive";

    public string Audience { get; init; } = "cordon-hive-clients";

    public string? SigningKey { get; init; }

    public int AccessTokenMinutes { get; init; } = 15;

    public int RefreshTokenDays { get; init; } = 7;
}

public sealed record IssuedAccessToken(
    string Token,
    DateTimeOffset ExpiresAtUtc);

public sealed class JwtTokenService
{
    private const int MinSigningKeyBytes = 32;

    private readonly IOptionsMonitor<JwtAuthOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public JwtTokenService(
        IOptionsMonitor<JwtAuthOptions> options,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public JwtAuthOptions CurrentOptions => _options.CurrentValue;

    public SymmetricSecurityKey GetValidatedSigningKey()
    {
        var opts = _options.CurrentValue;
        var rawKey = !string.IsNullOrWhiteSpace(opts.SigningKey)
            ? opts.SigningKey.Trim()
            : _configuration["hive_jwt_signing_key"]?.Trim();

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            throw new InvalidOperationException(
                "JWT signing key is not configured. Set Authentication:Jwt:SigningKey or the hive_jwt_signing_key secret.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(rawKey);
        if (keyBytes.Length < MinSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"JWT signing key must be at least {MinSigningKeyBytes} bytes in UTF-8 encoding.");
        }

        return new SymmetricSecurityKey(keyBytes);
    }

    public TokenValidationParameters CreateTokenValidationParameters()
    {
        var opts = _options.CurrentValue;
        var signingKey = GetValidatedSigningKey();

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = opts.Issuer,
            ValidateAudience = true,
            ValidAudience = opts.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                if (!expires.HasValue)
                {
                    return false;
                }

                var now = _timeProvider.GetUtcNow();
                var skew = TimeSpan.FromSeconds(30);

                if (notBefore.HasValue)
                {
                    var nbf = new DateTimeOffset(DateTime.SpecifyKind(notBefore.Value, DateTimeKind.Utc));
                    if (now < nbf - skew)
                    {
                        return false;
                    }
                }

                var exp = new DateTimeOffset(DateTime.SpecifyKind(expires.Value, DateTimeKind.Utc));
                return now < exp + skew;
            }
        };
    }

    public IssuedAccessToken CreateAccessToken(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var opts = _options.CurrentValue;
        var signingKey = GetValidatedSigningKey();
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var nowUtc = _timeProvider.GetUtcNow();
        var accessMinutes = Math.Clamp(opts.AccessTokenMinutes, 1, 1440);
        var expiresAtUtc = nowUtc.AddMinutes(accessMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString("D")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new(HiveClaimTypes.ActorType, "User"),
            new(HiveClaimTypes.UserSid, user.SubjectId),
            new(ClaimTypes.Name, user.DisplayName)
        };

        foreach (var role in user.Roles.OrderBy(r => r.ToString(), StringComparer.Ordinal))
        {
            claims.Add(new Claim(HiveClaimTypes.Role, role.ToString()));
        }

        foreach (var siteId in user.AllowedSiteIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(HiveClaimTypes.Site, siteId));
        }

        if (user.AllSitesAccess)
        {
            claims.Add(new Claim(HiveClaimTypes.AllSites, "true"));
        }

        var token = new JwtSecurityToken(
            issuer: opts.Issuer,
            audience: opts.Audience,
            claims: claims,
            notBefore: nowUtc.UtcDateTime,
            expires: expiresAtUtc.UtcDateTime,
            signingCredentials: credentials);

        var serialized = _tokenHandler.WriteToken(token);
        return new IssuedAccessToken(serialized, expiresAtUtc);
    }
}
