using System.Security.Cryptography;
using System.Text;

namespace Hive.Domain.Enrollment;

public sealed record EnrollmentToken(
    Guid TokenId,
    string TokenHashSha256,
    string SiteId,
    string CreatedBySid,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RedeemedAtUtc,
    Guid? RedeemedByDeviceId)
{
    public bool IsUsableAt(DateTimeOffset nowUtc) =>
        RedeemedAtUtc is null && nowUtc < ExpiresAtUtc;
}

public static class EnrollmentTokenHasher
{
    public static string GenerateRawToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static string ComputeSha256Hex(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        var bytes = Encoding.UTF8.GetBytes(rawToken.Trim());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
