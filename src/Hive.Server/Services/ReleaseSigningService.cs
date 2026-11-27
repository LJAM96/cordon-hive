using System.Security.Cryptography;
using Hive.Domain.Deployments;
using Microsoft.Extensions.Options;

namespace Hive.Server.Services;

public sealed class DeploymentSigningOptions
{
    public const string SectionName = "DeploymentSigning";

    public string? PrivateKeyPem { get; set; }

    public string? PublicKeyPem { get; set; }

    public string? KeyId { get; set; }
}

/// <summary>
/// Signs canonical release manifests using an ECDSA P-256 keypair loaded from configuration
/// or generated ephemerally once per server instance.
/// </summary>
public sealed class ReleaseSigningService
{
    private readonly string _privateKeyPem;

    public ReleaseSigningService(IOptions<DeploymentSigningOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value;

        if (!string.IsNullOrWhiteSpace(opts.PrivateKeyPem))
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(opts.PrivateKeyPem);
            _privateKeyPem = opts.PrivateKeyPem.Trim();
            PublicKeyPem = !string.IsNullOrWhiteSpace(opts.PublicKeyPem)
                ? opts.PublicKeyPem.Trim()
                : ecdsa.ExportSubjectPublicKeyInfoPem();
            KeyId = !string.IsNullOrWhiteSpace(opts.KeyId)
                ? opts.KeyId.Trim()
                : ReleaseManifestCrypto.ComputeKeyId(PublicKeyPem);
        }
        else
        {
            var (privatePem, publicPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
            _privateKeyPem = privatePem;
            PublicKeyPem = publicPem;
            KeyId = keyId;
        }
    }

    public string KeyId { get; }

    public string PublicKeyPem { get; }

    public SignedManifestEnvelope SignManifest(SignedReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var boundManifest = string.Equals(manifest.KeyId, KeyId, StringComparison.Ordinal)
            ? manifest
            : manifest with { KeyId = KeyId };
        return ReleaseManifestCrypto.Sign(boundManifest, _privateKeyPem, PublicKeyPem);
    }
}
