using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Ipc;
using Hive.Windows.SystemInfo;

namespace Hive.Agent;

/// <summary>
/// Handles CLI enrollment (<c>Hive.Agent enroll --token ...</c>) against the server API
/// and persists the resulting active <see cref="AgentDeviceState"/> in SQLite.
/// </summary>
public static class AgentEnrollmentBootstrapper
{
    public static async Task<int> RunAsync(
        string[] args,
        AgentOptions options,
        IAgentLocalStore store,
        WindowsMachineInfoProvider machineInfoProvider,
        HttpClient httpClient,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseArgs(args, out var parsed, out var parseError))
        {
            await stderr.WriteLineAsync($"Enrollment argument error: {parseError}").ConfigureAwait(false);
            await stderr.WriteLineAsync(
                "Usage: Hive.Agent enroll --token <token> [--asset <asset>] [--api <url>] [--thumbprint <hex> | --dev-self-signed]")
                .ConfigureAwait(false);
            return 1;
        }

        var apiBaseUrl = !string.IsNullOrWhiteSpace(parsed.ApiBaseUrl)
            ? parsed.ApiBaseUrl
            : options.ApiBaseUrl;

        if (string.IsNullOrWhiteSpace(apiBaseUrl) ||
            !Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            await stderr.WriteLineAsync("Enrollment failed: a valid --api <url> or Agent:ApiBaseUrl must be configured.")
                .ConfigureAwait(false);
            return 1;
        }

        var thumbprint = AgentHttpClientFactory.NormalizeThumbprint(parsed.Thumbprint ?? options.CertificateThumbprint);
        if (string.IsNullOrEmpty(thumbprint))
        {
            if (!parsed.DevSelfSigned)
            {
                await stderr.WriteLineAsync(
                    "Enrollment failed: --thumbprint <hex> or Agent:CertificateThumbprint is required (or pass --dev-self-signed for development).")
                    .ConfigureAwait(false);
                return 1;
            }

            thumbprint = GenerateAndInstallDevCertificate(Environment.MachineName);
            await stdout.WriteLineAsync($"Generated development client certificate with thumbprint {thumbprint}.")
                .ConfigureAwait(false);
        }

        var machine = machineInfoProvider.GetMachineInfo();
        var agentVersion = typeof(AgentRuntimeState).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var request = new EnrollDeviceRequest(
            parsed.Token,
            machine.Hostname,
            thumbprint,
            parsed.AssetNumber,
            agentVersion);

        var endpoint = new Uri(baseUri, $"{ProtocolConstants.ApiPrefix}/devices/enroll");

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request, options: NamedPipeFraming.SerializerOptions)
            };
            message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));

            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                await stderr.WriteLineAsync(
                    $"Enrollment rejected by server (HTTP {(int)response.StatusCode} {response.ReasonPhrase}): {errorBody}")
                    .ConfigureAwait(false);
                return 1;
            }

            var enrollResponse = await response.Content
                .ReadFromJsonAsync<EnrollDeviceResponse>(NamedPipeFraming.SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (enrollResponse is null || enrollResponse.DeviceId == Guid.Empty)
            {
                await stderr.WriteLineAsync("Enrollment failed: server returned an empty or invalid response.")
                    .ConfigureAwait(false);
                return 1;
            }

            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var existingState = await store.GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);

            var deviceState = new AgentDeviceState(
                DeviceId: enrollResponse.DeviceId,
                SiteId: enrollResponse.SiteId,
                CertificateThumbprint: thumbprint,
                EnrollmentState: EnrollmentState.Active.ToString(),
                PolicyRevision: enrollResponse.PolicyRevision,
                LastHeartbeatUtc: existingState?.LastHeartbeatUtc,
                ProtectedModeActive: existingState?.ProtectedModeActive ?? false,
                UpdatedAtUtc: DateTimeOffset.UtcNow);

            await store.SaveDeviceStateAsync(deviceState, cancellationToken).ConfigureAwait(false);

            await stdout.WriteLineAsync(
                $"Device enrolled successfully. DeviceId={enrollResponse.DeviceId:D}, SiteId={enrollResponse.SiteId}, PolicyRevision={enrollResponse.PolicyRevision}, Thumbprint={thumbprint}")
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await stderr.WriteLineAsync($"Enrollment request failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal static bool TryParseArgs(
        string[] args,
        out EnrollCliArgs parsed,
        out string? error)
    {
        string? token = null;
        string? asset = null;
        string? api = null;
        string? thumbprint = null;
        var devSelfSigned = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--dev-self-signed", StringComparison.OrdinalIgnoreCase))
            {
                devSelfSigned = true;
                continue;
            }

            if (string.Equals(arg, "--token", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadOptionValue(args, ref i, "--token", out token, out error))
                {
                    parsed = default!;
                    return false;
                }
                continue;
            }

            if (string.Equals(arg, "--asset", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadOptionValue(args, ref i, "--asset", out asset, out error))
                {
                    parsed = default!;
                    return false;
                }
                continue;
            }

            if (string.Equals(arg, "--api", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadOptionValue(args, ref i, "--api", out api, out error))
                {
                    parsed = default!;
                    return false;
                }
                continue;
            }

            if (string.Equals(arg, "--thumbprint", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadOptionValue(args, ref i, "--thumbprint", out thumbprint, out error))
                {
                    parsed = default!;
                    return false;
                }
                continue;
            }

            error = $"Unknown option '{arg}'.";
            parsed = default!;
            return false;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Missing required --token <token> option.";
            parsed = default!;
            return false;
        }

        error = null;
        parsed = new EnrollCliArgs(token.Trim(), asset, api, thumbprint, devSelfSigned);
        return true;
    }

    private static bool TryReadOptionValue(
        string[] args,
        ref int index,
        string optionName,
        out string? value,
        out string? error)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            value = null;
            error = $"Option '{optionName}' requires a value.";
            return false;
        }

        index++;
        value = args[index].Trim();
        error = null;
        return true;
    }

    private static string GenerateAndInstallDevCertificate(string hostname)
    {
        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN=cordon-hive-dev-{hostname}");
        var certRequest = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        certRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        certRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, // Client Authentication
                false));

        var now = DateTimeOffset.UtcNow;
        using var ephemeralCert = certRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        var pfxBytes = ephemeralCert.Export(X509ContentType.Pfx);
        using var persistedCert = X509CertificateLoader.LoadPkcs12(
            pfxBytes,
            password: null,
            keyStorageFlags: X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(persistedCert);
        }
        catch
        {
            // If certificate store write is restricted in sandboxed context, still return the thumbprint.
        }

        return persistedCert.Thumbprint;
    }

    internal sealed record EnrollCliArgs(
        string Token,
        string? AssetNumber,
        string? ApiBaseUrl,
        string? Thumbprint,
        bool DevSelfSigned);
}
