using Hive.Domain.RemoteAccess;

namespace Hive.Server.Services;

/// <summary>
/// Encrypts and decrypts escrowed break-glass workstation credentials using the
/// configured 256-bit AES-GCM master key.
/// </summary>
public sealed class EscrowVaultService
{
    private const int CurrentKeyVersion = 1;
    private readonly byte[]? _masterKey;

    public EscrowVaultService(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rawKey = !string.IsNullOrWhiteSpace(configuration["EscrowVault:MasterKey"])
            ? configuration["EscrowVault:MasterKey"]
            : configuration["hive_vault_master_key"];

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            _masterKey = null;
            return;
        }

        try
        {
            _masterKey = EscrowCredentialCrypto.ParseMasterKey(rawKey);
        }
        catch (FormatException)
        {
            _masterKey = null;
        }
    }

    public bool IsConfigured => _masterKey is not null;

    public EscrowedCredentialRecord EncryptNew(
        Guid deviceId,
        string accountUsername,
        string plaintextPassword,
        string updatedBySid,
        DateTimeOffset updatedAtUtc)
    {
        if (_masterKey is null)
        {
            throw new InvalidOperationException("Escrow vault master key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(accountUsername))
        {
            throw new ArgumentException("Account username is required.", nameof(accountUsername));
        }

        if (string.IsNullOrEmpty(plaintextPassword))
        {
            throw new ArgumentException("Password is required.", nameof(plaintextPassword));
        }

        var credentialId = Guid.NewGuid();
        var (ciphertext, nonce, tag) = EscrowCredentialCrypto.Encrypt(
            _masterKey,
            deviceId,
            credentialId,
            CurrentKeyVersion,
            plaintextPassword);

        return new EscrowedCredentialRecord(
            credentialId,
            deviceId,
            accountUsername.Trim(),
            ciphertext,
            nonce,
            tag,
            CurrentKeyVersion,
            updatedBySid,
            updatedAtUtc);
    }

    public string Decrypt(EscrowedCredentialRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_masterKey is null)
        {
            throw new InvalidOperationException("Escrow vault master key is not configured.");
        }

        return EscrowCredentialCrypto.Decrypt(
            _masterKey,
            record.DeviceId,
            record.CredentialId,
            record.KeyVersion,
            record.Ciphertext,
            record.Nonce,
            record.Tag);
    }
}
