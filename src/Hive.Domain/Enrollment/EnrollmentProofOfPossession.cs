using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Hive.Domain.Enrollment;

/// <summary>
/// Signs and verifies certificate proof-of-possession challenges during device enrollment (V20).
/// </summary>
public static class EnrollmentProofOfPossession
{
    public static string BuildCanonicalChallenge(string tokenHash, string hostname, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        return $"cordon-hive-enroll-v1|{tokenHash.Trim().ToLowerInvariant()}|{hostname.Trim()}|{thumbprint.Trim().ToUpperInvariant()}";
    }

    public static string SignChallenge(X509Certificate2 certificate, string tokenHash, string hostname)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("Certificate must contain a private key to sign the enrollment challenge.");
        }

        var challenge = BuildCanonicalChallenge(tokenHash, hostname, certificate.Thumbprint);
        var challengeBytes = Encoding.UTF8.GetBytes(challenge);

        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null)
        {
            var sig = rsa.SignData(challengeBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(sig);
        }

        using var ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa is not null)
        {
            var sig = ecdsa.SignData(challengeBytes, HashAlgorithmName.SHA256);
            return Convert.ToBase64String(sig);
        }

        throw new NotSupportedException("Unsupported certificate key algorithm for proof-of-possession signing (RSA or ECDSA required).");
    }

    public static bool Verify(
        string? certificatePem,
        string? signatureBase64,
        string expectedThumbprint,
        string tokenHash,
        string hostname,
        DateTimeOffset utcNow,
        out string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(certificatePem) ||
            string.IsNullOrWhiteSpace(signatureBase64) ||
            string.IsNullOrWhiteSpace(expectedThumbprint) ||
            string.IsNullOrWhiteSpace(tokenHash) ||
            string.IsNullOrWhiteSpace(hostname))
        {
            reasonCode = "proof_of_possession_missing";
            return false;
        }

        X509Certificate2? cert = null;
        try
        {
            cert = X509Certificate2.CreateFromPem(certificatePem);
        }
        catch (Exception)
        {
            reasonCode = "certificate_pem_invalid";
            return false;
        }

        using (cert)
        {
            var notBefore = new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero);
            var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (utcNow < notBefore || utcNow > notAfter)
            {
                reasonCode = "certificate_expired_or_not_yet_valid";
                return false;
            }

            if (!string.Equals(cert.Thumbprint, expectedThumbprint.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reasonCode = "certificate_thumbprint_mismatch";
                return false;
            }

            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(signatureBase64);
            }
            catch (FormatException)
            {
                reasonCode = "proof_of_possession_invalid";
                return false;
            }

            var challengeBytes = Encoding.UTF8.GetBytes(
                BuildCanonicalChallenge(tokenHash, hostname, cert.Thumbprint));

            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null)
            {
                var ok = rsa.VerifyData(challengeBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                reasonCode = ok ? "proof_of_possession_valid" : "proof_of_possession_invalid";
                return ok;
            }

            using var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa is not null)
            {
                var ok = ecdsa.VerifyData(challengeBytes, signatureBytes, HashAlgorithmName.SHA256);
                reasonCode = ok ? "proof_of_possession_valid" : "proof_of_possession_invalid";
                return ok;
            }

            reasonCode = "certificate_key_algorithm_unsupported";
            return false;
        }
    }
}
