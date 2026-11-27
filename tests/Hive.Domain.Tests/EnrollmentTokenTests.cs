using Hive.Domain.Enrollment;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class EnrollmentTokenTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = CreatedAt.AddMinutes(15);

    [Fact]
    public void ComputeSha256Hex_IsDeterministicAndNormalized()
    {
        const string raw = "hive-token-example-1234";
        var h1 = EnrollmentTokenHasher.ComputeSha256Hex(raw);
        var h2 = EnrollmentTokenHasher.ComputeSha256Hex($"  {raw}  ");
        var h3 = EnrollmentTokenHasher.ComputeSha256Hex("different-token");

        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);
        Assert.Matches("^[0-9a-f]{64}$", h1);
        Assert.NotEqual(h1, h3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ComputeSha256Hex_RejectsNullOrWhitespace(string? raw)
    {
        Assert.ThrowsAny<ArgumentException>(() => EnrollmentTokenHasher.ComputeSha256Hex(raw!));
    }

    [Fact]
    public void GenerateRawToken_ProducesUnique64CharHex()
    {
        var t1 = EnrollmentTokenHasher.GenerateRawToken();
        var t2 = EnrollmentTokenHasher.GenerateRawToken();

        Assert.Matches("^[0-9a-f]{64}$", t1);
        Assert.Matches("^[0-9a-f]{64}$", t2);
        Assert.NotEqual(t1, t2);
    }

    [Fact]
    public void IsUsableAt_EnforcesExpiryAndSingleUse()
    {
        var token = new EnrollmentToken(
            TokenId: Guid.NewGuid(),
            TokenHashSha256: EnrollmentTokenHasher.ComputeSha256Hex("secret-token"),
            SiteId: "site-a",
            CreatedBySid: "S-1-5-21-1001",
            CreatedAtUtc: CreatedAt,
            ExpiresAtUtc: ExpiresAt,
            RedeemedAtUtc: null,
            RedeemedByDeviceId: null);

        Assert.True(token.IsUsableAt(CreatedAt));
        Assert.True(token.IsUsableAt(ExpiresAt.AddTicks(-1)));
        Assert.False(token.IsUsableAt(ExpiresAt));
        Assert.False(token.IsUsableAt(ExpiresAt.AddSeconds(1)));

        var redeemed = token with
        {
            RedeemedAtUtc = CreatedAt.AddMinutes(2),
            RedeemedByDeviceId = Guid.NewGuid()
        };

        Assert.False(redeemed.IsUsableAt(CreatedAt.AddMinutes(3)));
    }
}
