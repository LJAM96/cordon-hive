using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Agent;

/// <summary>
/// Creates <see cref="HttpClient"/> instances configured for optional client certificate mTLS
/// from Windows Certificate Store (<see cref="StoreName.My"/>).
/// Never disables or bypasses server TLS certificate validation.
/// </summary>
public sealed class AgentHttpClientFactory : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly AgentOptions _options;
    private readonly ILogger<AgentHttpClientFactory> _logger;
    private readonly ConcurrentDictionary<string, HttpClient> _cachedClients = new(StringComparer.OrdinalIgnoreCase);

    public AgentHttpClientFactory(
        IOptions<AgentOptions> options,
        ILogger<AgentHttpClientFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public HttpClient GetOrCreateClient(string? certificateThumbprint = null)
    {
        var effectiveThumbprint = NormalizeThumbprint(certificateThumbprint ?? _options.CertificateThumbprint);
        var cacheKey = effectiveThumbprint ?? string.Empty;
        return _cachedClients.GetOrAdd(cacheKey, key => CreateClient(string.IsNullOrEmpty(key) ? null : key));
    }

    public HttpClient CreateClient(string? certificateThumbprint = null)
    {
        var handler = new HttpClientHandler();
        var effectiveThumbprint = NormalizeThumbprint(certificateThumbprint ?? _options.CertificateThumbprint);

        if (!string.IsNullOrEmpty(effectiveThumbprint))
        {
            var cert = FindClientCertificate(effectiveThumbprint);
            if (cert is not null)
            {
                handler.ClientCertificates.Add(cert);
            }
            else
            {
                _logger.LogWarning(
                    "Configured client certificate with thumbprint {Thumbprint} was not found in StoreName.My (LocalMachine or CurrentUser).",
                    effectiveThumbprint);
            }
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = DefaultTimeout
        };
    }

    internal static X509Certificate2? FindClientCertificate(string thumbprint)
    {
        var normalized = NormalizeThumbprint(thumbprint);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
                if (matches.Count == 0)
                {
                    continue;
                }

                // Prefer a certificate that has an associated private key.
                X509Certificate2? chosen = null;
                foreach (var candidate in matches)
                {
                    if (candidate.HasPrivateKey && chosen is null)
                    {
                        chosen = candidate;
                    }
                    else if (chosen is null)
                    {
                        chosen = candidate;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }

                if (chosen is not null)
                {
                    return chosen;
                }
            }
            catch
            {
                // Ignore inaccessible store location and continue fallback.
            }
        }

        return null;
    }

    internal static string? NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        var chars = thumbprint
            .Where(c => !char.IsWhiteSpace(c) && c != ':')
            .ToArray();

        return chars.Length == 0 ? null : new string(chars).ToUpperInvariant();
    }

    public void Dispose()
    {
        foreach (var client in _cachedClients.Values)
        {
            client.Dispose();
        }

        _cachedClients.Clear();
    }
}
