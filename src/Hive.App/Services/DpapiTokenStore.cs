namespace Hive.App.Services;

public sealed record StoredSessionTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAtUtc);

/// <summary>
/// Persists local session tokens encrypted with Windows DPAPI (CurrentUser scope).
/// Plaintext passwords are never written to disk.
/// </summary>
public sealed class DpapiTokenStore
{
    public string FilePath { get; }

    public DpapiTokenStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(DpapiStorageHelper.DefaultAppDataDirectory, "session.dat")
            : filePath;
    }

    public void Save(StoredSessionTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        DpapiStorageHelper.SaveProtectedJson(FilePath, tokens);
    }

    public StoredSessionTokens? Load()
    {
        var tokens = DpapiStorageHelper.LoadProtectedJson<StoredSessionTokens>(FilePath);
        if (tokens is null ||
            string.IsNullOrWhiteSpace(tokens.AccessToken) ||
            string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            return null;
        }

        return tokens;
    }

    public void Clear() => DpapiStorageHelper.DeleteQuietly(FilePath);
}
