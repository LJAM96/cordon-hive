using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hive.Domain.Devices;

namespace Hive.Domain.RemoteAccess;

public static class EscrowRevealPurposes
{
    public const string PhysicalSignIn = "PhysicalSignIn";
    public const string AssistedRdp = "AssistedRdp";

    public static bool IsValid(string? purpose) =>
        string.Equals(purpose, PhysicalSignIn, StringComparison.Ordinal) ||
        string.Equals(purpose, AssistedRdp, StringComparison.Ordinal);
}

public sealed record EscrowedCredentialRecord(
    Guid CredentialId,
    Guid DeviceId,
    string AccountUsername,
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] Tag,
    int KeyVersion,
    string UpdatedBySid,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// AES-256-GCM authenticated encryption for break-glass workstation credentials.
/// Binds AAD to (keyVersion, deviceId, credentialId) so ciphertexts cannot be transplanted
/// between devices or records.
/// </summary>
public static class EscrowCredentialCrypto
{
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    public static byte[] ParseMasterKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            throw new FormatException("Escrow vault master key is missing.");
        }

        var trimmed = rawKey.Trim();

        if (trimmed.Length == KeySizeBytes * 2 && trimmed.All(Uri.IsHexDigit))
        {
            var hexBytes = Convert.FromHexString(trimmed);
            if (hexBytes.Length == KeySizeBytes)
            {
                return hexBytes;
            }
        }

        try
        {
            var base64Bytes = Convert.FromBase64String(trimmed);
            if (base64Bytes.Length == KeySizeBytes)
            {
                return base64Bytes;
            }
        }
        catch (FormatException)
        {
            // Fall through to explicit FormatException below.
        }

        throw new FormatException($"Escrow vault master key must decode to exactly {KeySizeBytes} bytes (256-bit Base64 or 64-char hex).");
    }

    public static byte[] BuildAad(Guid deviceId, Guid credentialId, int keyVersion)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("DeviceId must not be empty.", nameof(deviceId));
        }

        if (credentialId == Guid.Empty)
        {
            throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        }

        if (keyVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion), "KeyVersion must be positive.");
        }

        return Encoding.UTF8.GetBytes($"cordon-hive:escrow:v{keyVersion}:{deviceId:D}:{credentialId:D}");
    }

    public static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> masterKey32,
        Guid deviceId,
        Guid credentialId,
        int keyVersion,
        string plaintextPassword)
    {
        if (masterKey32.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Master key must be {KeySizeBytes} bytes.", nameof(masterKey32));
        }

        ArgumentException.ThrowIfNullOrEmpty(plaintextPassword);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextPassword);
        var ciphertext = new byte[plaintextBytes.Length];
        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];

        try
        {
            RandomNumberGenerator.Fill(nonce);
            var aad = BuildAad(deviceId, credentialId, keyVersion);
            using var aes = new AesGcm(masterKey32, TagSizeBytes);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);
            return (ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public static string Decrypt(
        ReadOnlySpan<byte> masterKey32,
        Guid deviceId,
        Guid credentialId,
        int keyVersion,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> tag)
    {
        if (masterKey32.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Master key must be {KeySizeBytes} bytes.", nameof(masterKey32));
        }

        if (nonce.Length != NonceSizeBytes || tag.Length != TagSizeBytes)
        {
            throw new CryptographicException("Invalid nonce or authentication tag length.");
        }

        var plaintextBytes = new byte[ciphertext.Length];
        try
        {
            var aad = BuildAad(deviceId, credentialId, keyVersion);
            using var aes = new AesGcm(masterKey32, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, aad);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}

public sealed record RdpPolicyEvaluation(
    bool Allowed,
    string? ApprovedHostname,
    string? BlockedReason,
    IReadOnlyList<string> Warnings,
    ConnectivityState ConnectivityState);

/// <summary>
/// Evaluates whether LAN RDP may be initiated against a workstation without risking
/// active forensic workloads or command/file injection.
/// </summary>
public static partial class RdpPolicyEvaluator
{
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex SafeHostnameRegex();

    public static bool IsValidSafeHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return false;
        }

        var trimmed = hostname.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return SafeHostnameRegex().IsMatch(trimmed);
    }

    public static RdpPolicyEvaluation Evaluate(
        Device device,
        IReadOnlyList<DeviceSession> activeSessions,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(activeSessions);

        var connectivity = DeviceConnectivityEvaluator.Evaluate(device.LastSeenUtc, nowUtc);
        var warnings = new List<string>();

        if (device.EnrollmentState != EnrollmentState.Active)
        {
            return new RdpPolicyEvaluation(
                false,
                null,
                $"Device enrollment state is {device.EnrollmentState}; remote access denied.",
                warnings,
                connectivity);
        }

        if (!IsValidSafeHostname(device.Hostname))
        {
            return new RdpPolicyEvaluation(
                false,
                null,
                "Workstation hostname failed validation; remote access denied.",
                warnings,
                connectivity);
        }

        if (connectivity != ConnectivityState.Online)
        {
            return new RdpPolicyEvaluation(
                false,
                null,
                $"Workstation is {connectivity}; protected workload state cannot be confirmed.",
                warnings,
                connectivity);
        }

        if (device.ProtectedModeActive)
        {
            var reasonDetail = string.IsNullOrWhiteSpace(device.ProtectedModeReason)
                ? "Active forensic work protection lock is engaged."
                : device.ProtectedModeReason;
            return new RdpPolicyEvaluation(
                false,
                null,
                $"Workstation is in Protected Work Mode ({reasonDetail}); RDP is blocked.",
                warnings,
                connectivity);
        }

        if (activeSessions.Count > 0)
        {
            warnings.Add(
                $"Active session detected ({activeSessions.Count}); RDP as the signed-in user attaches to their desktop and locks the physical screen.");
        }

        warnings.Add("NLA and TLS certificate validation are enforced; clipboard/drive/printer redirection are disabled by default.");

        return new RdpPolicyEvaluation(
            true,
            device.Hostname.Trim(),
            null,
            warnings,
            connectivity);
    }
}
