namespace Hive.Domain.Operations;

public enum VersionCompatibilityStatus
{
    Compatible,
    UpgradeRecommended,
    Incompatible
}

public sealed record VersionCompatibilityResult(
    VersionCompatibilityStatus Status,
    string ReasonCode,
    string Message);

/// <summary>
/// Evaluates compatibility between client/agent versions and the Hive server baseline (V24).
/// </summary>
public static class VersionCompatibilityEvaluator
{
    public static VersionCompatibilityResult Evaluate(
        string? clientVersion,
        string serverProtocolVersion,
        string minimumSupportedVersion)
    {
        if (!TryParseCoreVersion(clientVersion, out var client) ||
            !TryParseCoreVersion(serverProtocolVersion, out var server) ||
            !TryParseCoreVersion(minimumSupportedVersion, out var minimum))
        {
            return new VersionCompatibilityResult(
                VersionCompatibilityStatus.Incompatible,
                "version_unparseable",
                $"Version string could not be parsed cleanly (client='{clientVersion ?? "<null>"}').");
        }

        if (client.Major != server.Major || client < minimum)
        {
            return new VersionCompatibilityResult(
                VersionCompatibilityStatus.Incompatible,
                client.Major != server.Major ? "major_version_mismatch" : "below_minimum_supported_version",
                $"Client version {client} is incompatible with minimum supported version {minimum} (server {server}).");
        }

        if (client.Minor < server.Minor)
        {
            return new VersionCompatibilityResult(
                VersionCompatibilityStatus.UpgradeRecommended,
                "minor_version_behind_server",
                $"Client version {client} is supported, though upgrading to {server} is recommended.");
        }

        return new VersionCompatibilityResult(
            VersionCompatibilityStatus.Compatible,
            "version_compatible",
            $"Client version {client} is compatible with server version {server}.");
    }

    private static bool TryParseCoreVersion(string? raw, out Version parsed)
    {
        parsed = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        var suffixIndex = trimmed.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            trimmed = trimmed[..suffixIndex];
        }

        var parts = trimmed.Split('.', StringSplitOptions.None);
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i < parts.Length)
            {
                if (!int.TryParse(parts[i], out var value) || value < 0)
                {
                    return false;
                }

                numbers[i] = value;
            }
        }

        if (parts.Length == 4 && (!int.TryParse(parts[3], out var fourth) || fourth < 0))
        {
            return false;
        }

        parsed = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }
}
