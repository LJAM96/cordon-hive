using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Hive.Domain.RemoteAccess;

namespace Hive.Windows.RemoteAccess;

/// <summary>
/// Manages short-lived Windows Credential Manager (<c>TERMSRV/&lt;hostname&gt;</c>) entries
/// for native <c>mstsc.exe</c> launches using Win32 <c>CredWriteW</c> and <c>CredDeleteW</c>.
/// </summary>
public static class WindowsRdpCredentialManager
{
    internal const uint CredTypeGeneric = 1;
    internal const uint CredPersistSession = 1;
    internal const string TargetPrefix = "TERMSRV/";

    public static bool TryWriteSessionRdpCredential(
        string hostname,
        string username,
        string password,
        out string? errorMessage)
    {
        if (!RdpPolicyEvaluator.IsValidSafeHostname(hostname))
        {
            errorMessage = "Hostname failed validation; refusing to write RDP credential.";
            return false;
        }

        var sanitizedUsername = SanitizeSingleLine(username);
        if (string.IsNullOrEmpty(sanitizedUsername))
        {
            errorMessage = "Username must not be empty.";
            return false;
        }

        if (string.IsNullOrEmpty(password))
        {
            errorMessage = "Password must not be empty.";
            return false;
        }

        var safeHostname = hostname.Trim();
        var targetName = $"{TargetPrefix}{safeHostname}";

        byte[]? passwordBytes = null;
        var targetNamePtr = IntPtr.Zero;
        var userNamePtr = IntPtr.Zero;
        var credentialBlobPtr = IntPtr.Zero;
        var blobByteLength = 0;

        try
        {
            passwordBytes = Encoding.Unicode.GetBytes(password);
            blobByteLength = passwordBytes.Length;

            targetNamePtr = Marshal.StringToHGlobalUni(targetName);
            userNamePtr = Marshal.StringToHGlobalUni(sanitizedUsername);
            credentialBlobPtr = Marshal.AllocHGlobal(blobByteLength);
            Marshal.Copy(passwordBytes, 0, credentialBlobPtr, blobByteLength);

            var credential = new NativeMethods.CREDENTIALW
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = targetNamePtr,
                Comment = IntPtr.Zero,
                LastWritten = default,
                CredentialBlobSize = (uint)blobByteLength,
                CredentialBlob = credentialBlobPtr,
                Persist = CredPersistSession,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userNamePtr
            };

            if (!NativeMethods.CredWriteW(ref credential, 0))
            {
                var win32Error = Marshal.GetLastWin32Error();
                errorMessage = $"CredWriteW failed with Win32 error {win32Error}: {new Win32Exception(win32Error).Message}";
                return false;
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or OutOfMemoryException or ArgumentException)
        {
            errorMessage = $"Failed to store ephemeral RDP credential: {ex.Message}";
            return false;
        }
        finally
        {
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            ZeroAndFreeHGlobal(credentialBlobPtr, blobByteLength);
            ZeroAndFreeHGlobal(userNamePtr, (sanitizedUsername.Length + 1) * sizeof(char));
            if (targetNamePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(targetNamePtr);
            }
        }
    }

    public static bool TryDeleteRdpCredential(string hostname)
    {
        if (!RdpPolicyEvaluator.IsValidSafeHostname(hostname))
        {
            return false;
        }

        var targetName = $"{TargetPrefix}{hostname.Trim()}";
        return NativeMethods.CredDeleteW(targetName, CredTypeGeneric, 0);
    }

    private static string? SanitizeSingleLine(string? value)
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

    private static void ZeroAndFreeHGlobal(IntPtr pointer, int byteLength)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (var i = 0; i < byteLength; i++)
        {
            Marshal.WriteByte(pointer, i, 0);
        }

        Marshal.FreeHGlobal(pointer);
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CREDENTIALW
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWriteW(
            [In] ref CREDENTIALW userCredential,
            uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDeleteW(
            string target,
            uint type,
            uint flags);
    }
}
