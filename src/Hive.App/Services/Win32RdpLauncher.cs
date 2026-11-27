using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Hive.Domain.RemoteAccess;

namespace Hive.App.Services;

public sealed record RdpLaunchResult(
    bool Launched,
    bool CredentialInjected,
    bool CredentialStoreBlocked,
    string? ErrorMessage,
    int? Win32ErrorCode = null);

/// <summary>
/// Launches native mstsc.exe on the LAN using validated hostnames, hardened .rdp policy
/// settings (NLA + TLS level 2, clipboard/drive/printer redirection disabled, no password 51),
/// and ephemeral session-scoped Windows Credential Manager entries when performing assisted RDP.
/// </summary>
public static class Win32RdpLauncher
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistSession = 1;
    private static readonly TimeSpan EphemeralCredentialRetentionWindow = TimeSpan.FromSeconds(4);

    public static Task<RdpLaunchResult> LaunchLanRdpAsync(
        string hostname,
        int rdpPort = 3389,
        CancellationToken cancellationToken = default) =>
        LaunchCoreAsync(hostname, rdpPort, accountUsername: null, password: null, cancellationToken);

    public static Task<RdpLaunchResult> LaunchAssistedRdpAsync(
        string hostname,
        int rdpPort,
        string accountUsername,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountUsername) || string.IsNullOrEmpty(password))
        {
            return Task.FromResult(new RdpLaunchResult(
                Launched: false,
                CredentialInjected: false,
                CredentialStoreBlocked: false,
                ErrorMessage: "Escrowed account username and password are required for assisted RDP."));
        }

        return LaunchCoreAsync(hostname, rdpPort, accountUsername.Trim(), password, cancellationToken);
    }

    private static async Task<RdpLaunchResult> LaunchCoreAsync(
        string hostname,
        int rdpPort,
        string? accountUsername,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!RdpPolicyEvaluator.IsValidSafeHostname(hostname))
        {
            return new RdpLaunchResult(
                Launched: false,
                CredentialInjected: false,
                CredentialStoreBlocked: false,
                ErrorMessage: "Target hostname failed safety validation; RDP launch blocked.");
        }

        if (rdpPort is < 1 or > 65535)
        {
            return new RdpLaunchResult(
                Launched: false,
                CredentialInjected: false,
                CredentialStoreBlocked: false,
                ErrorMessage: $"RDP port {rdpPort} is out of valid range (1-65535).");
        }

        var safeHost = hostname.Trim();
        var targetName = $"TERMSRV/{safeHost}";
        var useEphemeralCredential = !string.IsNullOrWhiteSpace(accountUsername) && !string.IsNullOrEmpty(password);
        var credentialWritten = false;
        string? tempRdpPath = null;

        try
        {
            if (useEphemeralCredential)
            {
                if (!TryWriteSessionCredential(targetName, accountUsername!, password!, out var win32Error))
                {
                    return new RdpLaunchResult(
                        Launched: false,
                        CredentialInjected: false,
                        CredentialStoreBlocked: true,
                        ErrorMessage: $"Windows Credential Manager rejected ephemeral TERMSRV entry (Win32 error {win32Error}).",
                        Win32ErrorCode: win32Error);
                }

                credentialWritten = true;
            }

            tempRdpPath = WriteHardenedRdpFile(safeHost, rdpPort, accountUsername);

            var mstscPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "mstsc.exe");

            var startInfo = new ProcessStartInfo
            {
                FileName = mstscPath,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(tempRdpPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new RdpLaunchResult(
                    Launched: false,
                    CredentialInjected: credentialWritten,
                    CredentialStoreBlocked: false,
                    ErrorMessage: "Failed to start mstsc.exe.");
            }

            // Allow mstsc.exe a short window to consume the temporary .rdp profile and
            // session-scoped TERMSRV/* credential before deleting both in finally.
            await Task.WhenAny(
                process.WaitForExitAsync(CancellationToken.None),
                Task.Delay(EphemeralCredentialRetentionWindow, cancellationToken)).ConfigureAwait(false);

            return new RdpLaunchResult(
                Launched: true,
                CredentialInjected: credentialWritten,
                CredentialStoreBlocked: false,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return new RdpLaunchResult(
                Launched: false,
                CredentialInjected: credentialWritten,
                CredentialStoreBlocked: false,
                ErrorMessage: "RDP launch was cancelled.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new RdpLaunchResult(
                Launched: false,
                CredentialInjected: credentialWritten,
                CredentialStoreBlocked: false,
                ErrorMessage: $"Unable to launch mstsc.exe ({ex.Message}).");
        }
        finally
        {
            if (credentialWritten)
            {
                DeleteCredentialQuietly(targetName);
            }

            DeleteTempFileQuietly(tempRdpPath);
        }
    }

    private static string WriteHardenedRdpFile(string safeHost, int rdpPort, string? accountUsername)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CordonHive");
        Directory.CreateDirectory(tempDir);

        var filePath = Path.Combine(tempDir, $"rdp-{Guid.NewGuid():N}.rdp");
        var fullAddress = rdpPort == 3389 ? safeHost : $"{safeHost}:{rdpPort}";

        var sb = new StringBuilder();
        sb.Append("full address:s:").AppendLine(fullAddress);
        sb.AppendLine("authentication level:i:2");
        sb.AppendLine("enablecredsspsupport:i:1");
        sb.AppendLine("redirectclipboard:i:0");
        sb.AppendLine("redirectprinters:i:0");
        sb.AppendLine("redirectdrives:i:0");
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:0");
        sb.AppendLine("drivestoredirect:s:");

        if (!string.IsNullOrWhiteSpace(accountUsername))
        {
            var sanitizedUser = SanitizeRdpSingleLineValue(accountUsername);
            if (!string.IsNullOrEmpty(sanitizedUser))
            {
                sb.Append("username:s:").AppendLine(sanitizedUser);
            }
            sb.AppendLine("prompt for credentials:i:0");
        }
        else
        {
            sb.AppendLine("prompt for credentials:i:1");
        }

        // Explicitly never write "password 51" to .rdp files.
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return filePath;
    }

    private static string SanitizeRdpSingleLineValue(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
             .Replace("\n", string.Empty, StringComparison.Ordinal)
             .Trim();

    private static bool TryWriteSessionCredential(
        string targetName,
        string username,
        string password,
        out int win32Error)
    {
        win32Error = 0;
        byte[]? passwordBytes = null;
        var blobPtr = IntPtr.Zero;
        var byteCount = 0;

        try
        {
            passwordBytes = Encoding.Unicode.GetBytes(password);
            byteCount = passwordBytes.Length;
            blobPtr = Marshal.AllocHGlobal(byteCount);
            Marshal.Copy(passwordBytes, 0, blobPtr, byteCount);

            var credential = new CREDENTIALW
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = targetName,
                Comment = "Cordon Hive ephemeral assisted RDP session",
                CredentialBlobSize = (uint)byteCount,
                CredentialBlob = blobPtr,
                Persist = CredPersistSession,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = username
            };

            if (!CredWriteW(ref credential, 0))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }
        finally
        {
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            if (blobPtr != IntPtr.Zero)
            {
                for (var i = 0; i < byteCount; i++)
                {
                    Marshal.WriteByte(blobPtr, i, 0);
                }

                Marshal.FreeHGlobal(blobPtr);
            }
        }
    }

    private static void DeleteCredentialQuietly(string targetName)
    {
        try
        {
            CredDeleteW(targetName, CredTypeGeneric, 0);
        }
        catch
        {
            // Best-effort cleanup of session-scoped credential.
        }
    }

    private static void DeleteTempFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignore transient lock on temp .rdp file.
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIALW userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint flags);
}
