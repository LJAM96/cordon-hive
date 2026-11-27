using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hive.Domain.Deployments;

public enum DeploymentPolicy
{
    Mandatory,
    Optional,
    NotifyOnly,
    RingPilot
}

public sealed record PackageArtifact(
    Guid Id,
    string FileName,
    string Sha256,
    long SizeBytes,
    string StoragePath,
    string UploadedBySid,
    DateTimeOffset UploadedUtc);

public sealed record SignedReleaseManifest(
    Guid ReleaseId,
    Guid ProductId,
    string ProductName,
    string Version,
    string InstallerType,
    string PackageSha256,
    long PackageSizeBytes,
    string DownloadRelativeUrl,
    string SilentArgs,
    string VerificationType,
    string VerificationTarget,
    string ExpectedVersion,
    IReadOnlyList<string> ProtectedProcesses,
    bool RebootAllowed,
    string InstallProfile,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    string KeyId);

public sealed record SignedManifestEnvelope(
    SignedReleaseManifest Manifest,
    string ManifestCanonicalJson,
    string SignatureBase64,
    string Algorithm,
    string PublicKeyPem);

public sealed record DeploymentJob(
    Guid Id,
    string SiteId,
    Guid ReleaseId,
    string ProductName,
    string Version,
    DeploymentPolicy Policy,
    string CreatedBySid,
    DateTimeOffset CreatedUtc);

public sealed record DeploymentTarget(
    Guid Id,
    Guid JobId,
    Guid DeviceId,
    DeploymentState State,
    int AttemptCount,
    Guid? ActiveLeaseId,
    DateTimeOffset? LeaseExpiresUtc,
    string? LastReasonCode,
    string? LastMessage,
    DateTimeOffset? DeferredUntilUtc,
    DateTimeOffset UpdatedUtc);

public sealed record DeploymentAttempt(
    Guid Id,
    Guid TargetId,
    int AttemptNumber,
    DeploymentState ResultingState,
    string ReasonCode,
    string? Message,
    int? ExitCode,
    DateTimeOffset OccurredUtc);

public sealed record MaintenanceLock(
    bool ManualProtectedMode,
    string ReasonCode,
    string? LockedBySid,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Signs and verifies release manifests using ECDSA P-256 + SHA-256 over deterministic canonical JSON.
/// </summary>
public static class ReleaseManifestCrypto
{
    public const string SupportedAlgorithm = "ECDSA-P256-SHA256";

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static (string PrivateKeyPem, string PublicKeyPem, string KeyId) CreateSigningKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPem = ecdsa.ExportECPrivateKeyPem();
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        var keyId = ComputeKeyId(publicKeyPem);
        return (privateKeyPem, publicKeyPem, keyId);
    }

    public static string ComputeKeyId(string publicKeyPem)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyPem.Trim()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string Canonicalize(SignedReleaseManifest manifest) =>
        JsonSerializer.Serialize(manifest, CanonicalJsonOptions);

    public static SignedManifestEnvelope Sign(
        SignedReleaseManifest manifest,
        string privateKeyPem,
        string publicKeyPem)
    {
        var canonical = Canonicalize(manifest);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256);
        return new SignedManifestEnvelope(
            manifest,
            canonical,
            Convert.ToBase64String(signature),
            SupportedAlgorithm,
            publicKeyPem);
    }

    public static bool Verify(
        SignedManifestEnvelope? envelope,
        out string reasonCode,
        string? trustedPublicKeyPem = null,
        DateTimeOffset? utcNow = null)
    {
        if (envelope is null || envelope.Manifest is null)
        {
            reasonCode = "manifest_missing";
            return false;
        }

        if (!string.Equals(envelope.Algorithm, SupportedAlgorithm, StringComparison.Ordinal))
        {
            reasonCode = "manifest_unsupported_algorithm";
            return false;
        }

        var expectedCanonical = Canonicalize(envelope.Manifest);
        if (!string.Equals(envelope.ManifestCanonicalJson, expectedCanonical, StringComparison.Ordinal))
        {
            reasonCode = "manifest_canonical_mismatch";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(trustedPublicKeyPem) &&
            !string.Equals(envelope.PublicKeyPem.Trim(), trustedPublicKeyPem.Trim(), StringComparison.Ordinal))
        {
            reasonCode = "manifest_untrusted_key";
            return false;
        }

        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (envelope.Manifest.ExpiresUtc <= now)
        {
            reasonCode = "manifest_expired";
            return false;
        }

        try
        {
            var signatureBytes = Convert.FromBase64String(envelope.SignatureBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(envelope.PublicKeyPem);
            var valid = ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(envelope.ManifestCanonicalJson),
                signatureBytes,
                HashAlgorithmName.SHA256);
            reasonCode = valid ? "manifest_valid" : "manifest_invalid_signature";
            return valid;
        }
        catch (Exception)
        {
            reasonCode = "manifest_invalid_signature";
            return false;
        }
    }
}

public sealed record SafetyEvaluationInput(
    bool ProtectionStateKnown,
    bool ManualProtectedModeActive,
    IReadOnlyCollection<string> RunningProcessNames,
    IReadOnlyCollection<string> ProtectedProcessNames,
    long? FreeBytesOnSystemDrive,
    long PackageSizeBytes,
    bool LeaseActive,
    DateTimeOffset? LeaseExpiresUtc,
    DateTimeOffset UtcNow);

public sealed record SafetyEvaluationResult(
    bool Allowed,
    DeploymentState RecommendedState,
    string ReasonCode,
    string Message);

/// <summary>
/// Fail-closed pre-install safety gate (V05-V08). Never terminates workloads and never uses CPU idle as a safety signal.
/// </summary>
public static class WorkloadSafetyGuard
{
    public const long MinimumRequiredFreeBytesFloor = 2L * 1024L * 1024L * 1024L;

    public static long RequiredFreeBytes(long packageSizeBytes)
    {
        var boundedSize = Math.Max(0L, packageSizeBytes);
        var triple = checked(boundedSize * 3L);
        return Math.Max(triple, MinimumRequiredFreeBytesFloor);
    }

    public static SafetyEvaluationResult Evaluate(SafetyEvaluationInput input)
    {
        if (!input.ProtectionStateKnown)
        {
            return new SafetyEvaluationResult(
                false,
                DeploymentState.Deferred,
                "protection_state_unknown",
                "Workstation protection state is unknown or stale; deployment deferred fail-closed.");
        }

        if (input.ManualProtectedModeActive)
        {
            return new SafetyEvaluationResult(
                false,
                DeploymentState.Deferred,
                "manual_protection_lock_active",
                "Manual Protected Forensic Work Mode lock is active; deployment deferred.");
        }

        var normalizedRunning = input.RunningProcessNames
            .Select(NormalizeProcessName)
            .Where(static n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var protectedProc in input.ProtectedProcessNames)
        {
            var normalizedProtected = NormalizeProcessName(protectedProc);
            if (!string.IsNullOrEmpty(normalizedProtected) && normalizedRunning.Contains(normalizedProtected))
            {
                return new SafetyEvaluationResult(
                    false,
                    DeploymentState.Deferred,
                    "protected_workload_active",
                    $"Protected forensic process '{protectedProc}' is currently running; deployment deferred without terminating workloads.");
            }
        }

        var requiredBytes = RequiredFreeBytes(input.PackageSizeBytes);
        if (!input.FreeBytesOnSystemDrive.HasValue || input.FreeBytesOnSystemDrive.Value < requiredBytes)
        {
            return new SafetyEvaluationResult(
                false,
                DeploymentState.Deferred,
                "insufficient_disk_space",
                $"System volume free space ({input.FreeBytesOnSystemDrive ?? 0} bytes) is below required {requiredBytes} bytes.");
        }

        if (!input.LeaseActive || !input.LeaseExpiresUtc.HasValue || input.LeaseExpiresUtc.Value <= input.UtcNow)
        {
            return new SafetyEvaluationResult(
                false,
                DeploymentState.Deferred,
                "deployment_lease_invalid",
                "A valid non-expired deployment execution lease is required before installing.");
        }

        return new SafetyEvaluationResult(
            true,
            DeploymentState.Installing,
            "safety_checks_passed",
            "All pre-install safety checks passed.");
    }

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(name.Trim());
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }
}
