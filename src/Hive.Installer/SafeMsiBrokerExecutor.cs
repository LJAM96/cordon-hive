using System.Diagnostics;
using System.Security.Cryptography;
using Hive.Contracts;
using Hive.Domain.Deployments;
using Microsoft.Extensions.Options;

namespace Hive.Installer;

public interface IMsiProcessRunner
{
    Task<int> RunMsiAsync(string stagedMsiPath, CancellationToken cancellationToken = default);
}

public sealed class WindowsMsiProcessRunner : IMsiProcessRunner
{
    private readonly string _msiExecPath;

    public WindowsMsiProcessRunner(string? msiExecPath = null)
    {
        _msiExecPath = string.IsNullOrWhiteSpace(msiExecPath) ? "msiexec.exe" : msiExecPath.Trim();
    }

    public WindowsMsiProcessRunner(IOptions<BrokerOptions> options)
        : this(options?.Value?.MsiExecPath)
    {
    }

    public async Task<int> RunMsiAsync(string stagedMsiPath, CancellationToken cancellationToken = default)
    {
        var startInfo = BuildStartInfo(_msiExecPath, stagedMsiPath);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Failed to start '{_msiExecPath}'.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public static ProcessStartInfo BuildStartInfo(string msiExecPath, string stagedMsiPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(msiExecPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedMsiPath);
        if (!stagedMsiPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Staged package path must point to an .msi file.", nameof(stagedMsiPath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = msiExecPath.Trim(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        // Strictly structured arguments — never shell string concatenation.
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(stagedMsiPath);
        startInfo.ArgumentList.Add("/qn");
        startInfo.ArgumentList.Add("/norestart");
        startInfo.ArgumentList.Add("REBOOT=ReallySuppress");

        return startInfo;
    }
}

public sealed record BrokerExecutionResult(
    bool Accepted,
    DeploymentState ResultingState,
    string ReasonCode,
    int? ExitCode,
    string Message);

/// <summary>
/// Fail-closed restricted MSI installer broker (V05-V09).
/// Validates policy, manifest signature, mutual exclusion, workstation safety gates,
/// and staged package integrity before invoking msiexec with structured no-reboot arguments.
/// </summary>
public sealed class SafeMsiBrokerExecutor
{
    private readonly BrokerOptions _options;
    private readonly BrokerCommandValidator _validator;
    private readonly IMsiProcessRunner _processRunner;

    public SafeMsiBrokerExecutor(
        IOptions<BrokerOptions> options,
        IMsiProcessRunner? processRunner = null)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), processRunner)
    {
    }

    public SafeMsiBrokerExecutor(
        BrokerOptions options,
        IMsiProcessRunner? processRunner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _validator = new BrokerCommandValidator(_options);
        _processRunner = processRunner ?? new WindowsMsiProcessRunner(_options.MsiExecPath);
    }

    public async Task<BrokerExecutionResult> ExecuteAsync(
        BrokerInstallRequest request,
        SignedManifestEnvelope envelope,
        string stagedPackagePath,
        SafetyEvaluationInput safetyInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(safetyInput);

        if (!_options.Enabled)
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Deferred,
                ReasonCode: "broker_disabled",
                ExitCode: null,
                Message: "Installer broker is disabled by default configuration.");
        }

        if (envelope?.Manifest is null)
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: "manifest_missing",
                ExitCode: null,
                Message: "Signed release manifest envelope is required.");
        }

        var validation = _validator.Validate(request, envelope.Manifest.InstallProfile);
        if (!validation.Accepted)
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: validation.ReasonCode,
                ExitCode: null,
                Message: validation.Message ?? "Broker install request validation failed.");
        }

        if (!ReleaseManifestCrypto.Verify(
                envelope,
                out var sigReason,
                _options.TrustedSigningPublicKeyPem,
                safetyInput.UtcNow))
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: sigReason,
                ExitCode: null,
                Message: $"Signed release manifest verification failed ({sigReason}).");
        }

        if (!string.Equals(envelope.Manifest.InstallerType, "msi", StringComparison.OrdinalIgnoreCase))
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: "unsupported_installer_type",
                ExitCode: null,
                Message: $"Broker supports MSI installers only; manifest declared '{envelope.Manifest.InstallerType}'.");
        }

        if (envelope.Manifest.RebootAllowed)
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: "reboot_not_allowed_by_broker",
                ExitCode: null,
                Message: "Broker policy prohibits installers that declare RebootAllowed=true.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Execute the Mutex-guarded critical section on a dedicated thread so Mutex thread affinity
        // holds across acquisition, execution, and ReleaseMutex() even when RunMsiAsync is async.
        return await Task.Factory.StartNew(
            () => ExecuteUnderMutex(envelope.Manifest, stagedPackagePath, safetyInput, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    private BrokerExecutionResult ExecuteUnderMutex(
        SignedReleaseManifest manifest,
        string stagedPackagePath,
        SafetyEvaluationInput safetyInput,
        CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(initiallyOwned: false, _options.MaintenanceMutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner exited without releasing; current thread now owns the mutex.
                acquired = true;
            }

            if (!acquired)
            {
                return new BrokerExecutionResult(
                    Accepted: false,
                    ResultingState: DeploymentState.Deferred,
                    ReasonCode: "maintenance_mutex_busy",
                    ExitCode: null,
                    Message: "Maintenance mutex is held by another installation or maintenance operation.");
            }

            var safetyEval = WorkloadSafetyGuard.Evaluate(safetyInput);
            if (!safetyEval.Allowed)
            {
                return new BrokerExecutionResult(
                    Accepted: false,
                    ResultingState: safetyEval.RecommendedState,
                    ReasonCode: safetyEval.ReasonCode,
                    ExitCode: null,
                    Message: safetyEval.Message);
            }

            var packageCheck = ValidateStagedPackage(stagedPackagePath, manifest);
            if (packageCheck is not null)
            {
                return packageCheck;
            }

            var exitCode = _processRunner.RunMsiAsync(stagedPackagePath, cancellationToken).GetAwaiter().GetResult();

            return exitCode switch
            {
                0 => new BrokerExecutionResult(
                    Accepted: true,
                    ResultingState: DeploymentState.Verifying,
                    ReasonCode: "msi_exit_success",
                    ExitCode: 0,
                    Message: "MSI installation completed with exit code 0; awaiting verification."),
                3010 => new BrokerExecutionResult(
                    Accepted: true,
                    ResultingState: DeploymentState.PendingReboot,
                    ReasonCode: "msi_exit_pending_reboot",
                    ExitCode: 3010,
                    Message: "MSI installation succeeded with exit code 3010 (reboot required); automatic reboot suppressed."),
                1641 => new BrokerExecutionResult(
                    Accepted: false,
                    ResultingState: DeploymentState.Failed,
                    ReasonCode: "msi_unexpected_reboot_initiated",
                    ExitCode: 1641,
                    Message: "MSI installer reported exit code 1641 (unexpected reboot initiated)."),
                _ => new BrokerExecutionResult(
                    Accepted: false,
                    ResultingState: DeploymentState.Failed,
                    ReasonCode: $"msi_exit_{exitCode}",
                    ExitCode: exitCode,
                    Message: $"MSI installer failed with exit code {exitCode}.")
            };
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static BrokerExecutionResult? ValidateStagedPackage(
        string stagedPackagePath,
        SignedReleaseManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(stagedPackagePath) ||
            !stagedPackagePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: "staged_package_invalid_extension",
                ExitCode: null,
                Message: "Staged installer package must be a valid .msi file path.");
        }

        if (!File.Exists(stagedPackagePath))
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: "staged_package_missing",
                ExitCode: null,
                Message: "Staged MSI package does not exist on disk.");
        }

        var fileInfo = new FileInfo(stagedPackagePath);
        if (fileInfo.Length != manifest.PackageSizeBytes)
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: "staged_package_size_mismatch",
                ExitCode: null,
                Message: $"Staged MSI size ({fileInfo.Length} bytes) does not match manifest size ({manifest.PackageSizeBytes} bytes).");
        }

        string actualSha256;
        using (var stream = new FileStream(stagedPackagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var hashBytes = SHA256.HashData(stream);
            actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        if (!string.Equals(actualSha256, manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new BrokerExecutionResult(
                Accepted: false,
                ResultingState: DeploymentState.Failed,
                ReasonCode: "staged_package_sha256_mismatch",
                ExitCode: null,
                Message: $"Staged MSI SHA-256 '{actualSha256}' does not match manifest '{manifest.PackageSha256}'.");
        }

        return null;
    }
}
