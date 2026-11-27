using System.Text.Json;

namespace Hive.App.Services;

public sealed class AppSettings
{
    public const string DefaultBaseUrl = "https://localhost:5001";
    public const int DefaultTimeoutSeconds = 15;

    public string BaseUrl { get; private set; } = DefaultBaseUrl;
    public int RequestTimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public bool TrySetBaseUrl(string? candidateUrl, out string? validationError)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl))
        {
            validationError = "Server URL cannot be empty.";
            return false;
        }

        if (!Uri.TryCreate(candidateUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            validationError = "Enter a valid HTTP or HTTPS LAN URL (for example, https://localhost:5001).";
            return false;
        }

        BaseUrl = uri.ToString().TrimEnd('/');
        validationError = null;
        return true;
    }

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            return settings;
        }

        try
        {
            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("HiveServer", out var serverSection))
            {
                if (serverSection.TryGetProperty("BaseUrl", out var urlProp) &&
                    urlProp.ValueKind == JsonValueKind.String)
                {
                    settings.TrySetBaseUrl(urlProp.GetString(), out _);
                }

                if (serverSection.TryGetProperty("RequestTimeoutSeconds", out var timeoutProp) &&
                    timeoutProp.TryGetInt32(out var timeout) &&
                    timeout is >= 3 and <= 120)
                {
                    settings.RequestTimeoutSeconds = timeout;
                }
            }
        }
        catch
        {
            // Fall back to safe LAN defaults if local settings cannot be parsed.
        }

        return settings;
    }
}
