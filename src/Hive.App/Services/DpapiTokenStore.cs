using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

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
    private const int CryptProtectUiForbidden = 0x1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string FilePath { get; }

    public DpapiTokenStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CordonHive",
                "session.dat")
            : filePath;
    }

    public void Save(StoredSessionTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[]? plainBytes = null;
        try
        {
            plainBytes = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOptions);
            var protectedBytes = Protect(plainBytes);

            var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(tempPath, protectedBytes);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    public StoredSessionTokens? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        byte[]? plainBytes = null;
        try
        {
            var protectedBytes = File.ReadAllBytes(FilePath);
            if (protectedBytes.Length == 0)
            {
                return null;
            }

            plainBytes = Unprotect(protectedBytes);
            var tokens = JsonSerializer.Deserialize<StoredSessionTokens>(plainBytes, JsonOptions);
            if (tokens is null ||
                string.IsNullOrWhiteSpace(tokens.AccessToken) ||
                string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                return null;
            }

            return tokens;
        }
        catch (Exception ex) when (ex is Win32Exception or CryptographicException or JsonException or IOException)
        {
            return null;
        }
        finally
        {
            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (IOException)
        {
            // Ignore transient file deletion errors on sign-out.
        }
    }

    private static byte[] Protect(byte[] plainBytes)
    {
        var inBlob = default(DataBlob);
        var outBlob = default(DataBlob);

        try
        {
            inBlob.cbData = plainBytes.Length;
            inBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);

            if (!CryptProtectData(
                    ref inBlob,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var output = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
            return output;
        }
        finally
        {
            FreeInputBlob(inBlob);
            FreeLocalBlob(outBlob);
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        var inBlob = default(DataBlob);
        var outBlob = default(DataBlob);

        try
        {
            inBlob.cbData = protectedBytes.Length;
            inBlob.pbData = Marshal.AllocHGlobal(protectedBytes.Length);
            Marshal.Copy(protectedBytes, 0, inBlob.pbData, protectedBytes.Length);

            if (!CryptUnprotectData(
                    ref inBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var output = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
            return output;
        }
        finally
        {
            FreeInputBlob(inBlob);
            FreeLocalBlob(outBlob, zeroBeforeFree: true);
        }
    }

    private static void FreeInputBlob(DataBlob blob)
    {
        if (blob.pbData == IntPtr.Zero)
        {
            return;
        }

        ZeroUnmanagedBuffer(blob.pbData, blob.cbData);
        Marshal.FreeHGlobal(blob.pbData);
    }

    private static void FreeLocalBlob(DataBlob blob, bool zeroBeforeFree = false)
    {
        if (blob.pbData == IntPtr.Zero)
        {
            return;
        }

        if (zeroBeforeFree)
        {
            ZeroUnmanagedBuffer(blob.pbData, blob.cbData);
        }

        LocalFree(blob.pbData);
    }

    private static void ZeroUnmanagedBuffer(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length <= 0)
        {
            return;
        }

        for (var i = 0; i < length; i++)
        {
            Marshal.WriteByte(pointer, i, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
