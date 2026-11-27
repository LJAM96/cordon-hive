using System.Diagnostics;
using System.Text;
using Hive.Domain.RemoteAccess;

namespace Hive.Windows.RemoteAccess;

public sealed record RdpLaunchOutcome(
    bool Launched,
    bool EphemeralCredentialStored,
    string? CredentialWarning,
    string? ErrorMessage,
    int? ProcessId);

/// <summary>
/// Generates hardened temporary <c>.rdp</c> connection profiles and launches native
/// <c>mstsc.exe</c> without shell execution or plaintext credential embedding.
/// </summary>
public static class NativeRdpLauncher
{
    private static readonly TimeSpan DefaultCleanupDelay = TimeSpan.FromSeconds(10);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string BuildSafeRdpFileContent(string hostname, int port = 3389, string? username = null)
    {
        if (!RdpPolicyEvaluator.IsValidSafeHostname(hostname))
        {
            throw new ArgumentException("Hostname failed safety validation.", nameof(hostname));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "RDP port must be between 1 and 65535.");
        }

        var safeHostname = hostname.Trim();
        var sanitizedUsername = SanitizeSingleLineValue(username);
        var promptForCredentials = sanitizedUsername is null ? 1 : 0;

        var builder = new StringBuilder(256);
        builder.Append("full address:s:").Append(safeHostname).Append(':').Append(port).Append("\r\n");
        builder.Append("prompt for credentials:i:").Append(promptForCredentials).Append("\r\n");
        builder.Append("enablecredsspsupport:i:1\r\n");
        builder.Append("authentication level:i:2\r\n");
        builder.Append("redirectclipboard:i:0\r\n");
        builder.Append("redirectdrives:i:0\r\n");
        builder.Append("redirectprinters:i:0\r\n");
        builder.Append("drivestoredirect:s:\r\n");

        if (sanitizedUsername is not null)
        {
            builder.Append("username:s:").Append(sanitizedUsername).Append("\r\n");
        }

        return builder.ToString();
    }

    public static async Task<RdpLaunchOutcome> LaunchNativeRdpAsync(
        string hostname,
        int port = 3389,
        string? username = null,
        string? ephemeralPassword = null,
        TimeSpan? cleanupDelay = null,
        CancellationToken ct = default)
    {
        var rdpContent = BuildSafeRdpFileContent(hostname, port, username);
        ct.ThrowIfCancellationRequested();

        var safeHostname = hostname.Trim();
        var sanitizedUsername = SanitizeSingleLineValue(username);
        var tempRdpPath = Path.Combine(Path.GetTempPath(), $"cordon-hive-rdp-{Guid.NewGuid():N}.rdp");

        var ephemeralCredentialStored = false;
        var credentialWriteAttempted = false;
        string? credentialWarning = null;

        try
        {
            await File.WriteAllTextAsync(tempRdpPath, rdpContent, Utf8NoBom, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(ephemeralPassword))
            {
                if (string.IsNullOrEmpty(sanitizedUsername))
                {
                    credentialWarning = "Username was omitted; ephemeral credential was not stored and mstsc.exe will prompt.";
                }
                else
                {
                    credentialWriteAttempted = true;
                    ephemeralCredentialStored = WindowsRdpCredentialManager.TryWriteSessionRdpCredential(
                        safeHostname,
                        sanitizedUsername,
                        ephemeralPassword,
                        out var credError);

                    if (!ephemeralCredentialStored)
                    {
                        credentialWarning = string.IsNullOrWhiteSpace(credError)
                            ? "Windows Credential Manager blocked storing the ephemeral RDP credential; mstsc.exe will prompt."
                            : credError;
                    }
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(tempRdpPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new RdpLaunchOutcome(
                    Launched: false,
                    EphemeralCredentialStored: ephemeralCredentialStored,
                    CredentialWarning: credentialWarning,
                    ErrorMessage: "Failed to start mstsc.exe process.",
                    ProcessId: null);
            }

            var processId = process.Id;
            var effectiveDelay = cleanupDelay ?? DefaultCleanupDelay;
            if (effectiveDelay > TimeSpan.Zero)
            {
                await Task.WhenAny(process.WaitForExitAsync(ct), Task.Delay(effectiveDelay, ct)).ConfigureAwait(false);
            }

            return new RdpLaunchOutcome(
                Launched: true,
                EphemeralCredentialStored: ephemeralCredentialStored,
                CredentialWarning: credentialWarning,
                ErrorMessage: null,
                ProcessId: processId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RdpLaunchOutcome(
                Launched: false,
                EphemeralCredentialStored: ephemeralCredentialStored,
                CredentialWarning: credentialWarning,
                ErrorMessage: $"Failed to launch mstsc.exe: {ex.Message}",
                ProcessId: null);
        }
        finally
        {
            if (credentialWriteAttempted)
            {
                WindowsRdpCredentialManager.TryDeleteRdpCredential(safeHostname);
            }

            TryDeleteTempFile(tempRdpPath);
        }
    }

    private static string? SanitizeSingleLineValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var trimmed = builder.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of temp .rdp profile.
        }
    }
}
