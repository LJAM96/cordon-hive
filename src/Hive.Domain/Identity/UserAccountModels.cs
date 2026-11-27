using System.Security.Cryptography;

namespace Hive.Domain.Identity;

public sealed record UserAccount(
    Guid UserId,
    string Username,
    string UsernameNormalized,
    string DisplayName,
    string PasswordHash,
    IReadOnlySet<HiveRole> Roles,
    IReadOnlySet<string> AllowedSiteIds,
    bool AllSitesAccess,
    bool IsActive,
    bool MustChangePassword,
    int FailedLoginCount,
    DateTimeOffset? LockedUntilUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public string SubjectId => $"hive-user:{UserId:D}";

    public bool IsLockedAt(DateTimeOffset nowUtc) =>
        LockedUntilUtc is not null && nowUtc < LockedUntilUtc.Value;

    public static string NormalizeUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return username.Trim().ToLowerInvariant();
    }
}

public sealed record UserRefreshToken(
    Guid TokenId,
    Guid UserId,
    string TokenHashSha256,
    string? ClientLabel,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc)
{
    public bool IsActiveAt(DateTimeOffset nowUtc) =>
        RevokedAtUtc is null && nowUtc < ExpiresAtUtc;
}

public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    public static (bool IsValid, string? ErrorMessage) Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return (false, "Password is required.");
        }

        if (password.Length < MinLength)
        {
            return (false, $"Password must be at least {MinLength} characters.");
        }

        if (password.Length > MaxLength)
        {
            return (false, $"Password must not exceed {MaxLength} characters.");
        }

        return (true, null);
    }
}

/// <summary>
/// PBKDF2-SHA512 password hashing with per-user random salt and constant-time comparison.
/// Format: pbkdf2-sha512$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;
/// </summary>
public static class PasswordHasher
{
    private const string AlgorithmPrefix = "pbkdf2-sha512";
    private const int DefaultIterations = 210_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    public static string HashPassword(string password)
    {
        var (isValid, error) = PasswordPolicy.Validate(password);
        if (!isValid)
        {
            throw new ArgumentException(error, nameof(password));
        }

        Span<byte> salt = stackalloc byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA512,
            HashSizeBytes);

        return $"{AlgorithmPrefix}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}";
    }

    public static bool VerifyPassword(string? password, string? encodedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], AlgorithmPrefix, StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations < 10_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length == 0 || expected.Length == 0)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA512,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
