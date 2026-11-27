using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hive.App.Services;

/// <summary>
/// Shared Windows DPAPI (CurrentUser scope, CRYPTPROTECT_UI_FORBIDDEN) atomic file storage helper.
/// </summary>
internal static class DpapiStorageHelper
{
    private const int CryptProtectUiForbidden = 0x1;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultAppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CordonHive");

    public static void SaveProtectedJson<T>(string filePath, T payload)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[]? plainBytes = null;
        try
        {
            plainBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            var protectedBytes = Protect(plainBytes);

            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(tempPath, protectedBytes);
                File.Move(tempPath, filePath, overwrite: true);
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

    public static T? LoadProtectedJson<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        byte[]? plainBytes = null;
        try
        {
            var protectedBytes = File.ReadAllBytes(filePath);
            if (protectedBytes.Length == 0)
            {
                return null;
            }

            plainBytes = Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<T>(plainBytes, JsonOptions);
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

    public static void DeleteQuietly(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
            // Ignore transient file deletion errors on sign-out or cache clear.
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
