using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Hive.Agent;
using Hive.Contracts;
using Hive.Domain.Enrollment;
using Hive.Domain.Operations;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Deployments;
using Hive.Windows.Ipc;
using Hive.Windows.SystemInfo;
using Hive.Windows.Telemetry;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class HardeningBenchmarkAndCompatibilityTests : IDisposable
{
    private readonly string _tempDir;

    public HardeningBenchmarkAndCompatibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CordonHive.HardeningTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [Theory]
    [InlineData("RSA")]
    [InlineData("ECDSA")]
    public void V20_EnrollmentProofOfPossession_SignAndVerify_SucceedsForRsaAndEcdsa(string algorithm)
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedCertificate(algorithm, now.AddMinutes(-5), now.AddHours(1));
        var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex("enroll-token-v20");
        const string hostname = "lab-forensic-01";

        var signature = EnrollmentProofOfPossession.SignChallenge(cert, tokenHash, hostname);
        var certPem = cert.ExportCertificatePem();

        var ok = EnrollmentProofOfPossession.Verify(
            certPem,
            signature,
            cert.Thumbprint,
            tokenHash,
            hostname,
            now,
            out var reasonCode);

        Assert.True(ok);
        Assert.Equal("proof_of_possession_valid", reasonCode);
    }

    [Fact]
    public void V20_EnrollmentProofOfPossession_RejectsExpiredWrongThumbprintAndTamperedSignature()
    {
        var now = DateTimeOffset.UtcNow;
        using var validCert = CreateSelfSignedCertificate("RSA", now.AddMinutes(-10), now.AddHours(1));
        using var expiredCert = CreateSelfSignedCertificate("ECDSA", now.AddDays(-10), now.AddDays(-1));

        var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex("enroll-token-v20-fail");
        const string hostname = "lab-forensic-02";

        var expiredSig = EnrollmentProofOfPossession.SignChallenge(expiredCert, tokenHash, hostname);
        var expiredOk = EnrollmentProofOfPossession.Verify(
            expiredCert.ExportCertificatePem(),
            expiredSig,
            expiredCert.Thumbprint,
            tokenHash,
            hostname,
            now,
            out var expiredReason);

        Assert.False(expiredOk);
        Assert.Equal("certificate_expired_or_not_yet_valid", expiredReason);

        var validSig = EnrollmentProofOfPossession.SignChallenge(validCert, tokenHash, hostname);
        var validPem = validCert.ExportCertificatePem();

        var wrongThumbprintOk = EnrollmentProofOfPossession.Verify(
            validPem,
            validSig,
            new string('0', 40),
            tokenHash,
            hostname,
            now,
            out var thumbprintReason);

        Assert.False(wrongThumbprintOk);
        Assert.Equal("certificate_thumbprint_mismatch", thumbprintReason);

        var tamperedSigOk = EnrollmentProofOfPossession.Verify(
            validPem,
            Convert.ToBase64String(new byte[64]),
            validCert.Thumbprint,
            tokenHash,
            hostname,
            now,
            out var sigReason);

        Assert.False(tamperedSigOk);
        Assert.Equal("proof_of_possession_invalid", sigReason);
    }

    [Theory]
    [InlineData("RSA")]
    [InlineData("ECDSA")]
    public async Task V20_AgentEnrollmentBootstrapper_SendsPopCertificateAndSignature_OverMockedHttpHandler(string algorithm)
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSelfSignedCertificate(algorithm, now.AddMinutes(-5), now.AddHours(1));

        var dbPath = Path.Combine(_tempDir, $"agent-enroll-{algorithm}.db");
        await using var store = AgentLocalStore.FromFilePath(dbPath);
        var machineInfoProvider = new WindowsMachineInfoProvider();
        var expectedHostname = machineInfoProvider.GetMachineInfo().Hostname;

        EnrollDeviceRequest? capturedRequest = null;
        var deviceId = Guid.NewGuid();
        var handler = new SequenceHttpMessageHandler(async (request, ct) =>
        {
            var json = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            capturedRequest = JsonSerializer.Deserialize<EnrollDeviceRequest>(json, NamedPipeFraming.SerializerOptions);

            var responseBody = JsonSerializer.Serialize(
                new EnrollDeviceResponse(deviceId, "site-lab", 7, now),
                NamedPipeFraming.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        const string rawToken = "raw-enrollment-token-123";
        var exitCode = await AgentEnrollmentBootstrapper.RunAsync(
            ["enroll", "--token", rawToken, "--api", "https://hive.local", "--thumbprint", cert.Thumbprint],
            new AgentOptions(),
            store,
            machineInfoProvider,
            httpClient,
            stdout,
            stderr,
            _ => cert);

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(stderr.ToString()), stderr.ToString());
        Assert.NotNull(capturedRequest);
        Assert.False(string.IsNullOrWhiteSpace(capturedRequest!.CertificatePem));
        Assert.False(string.IsNullOrWhiteSpace(capturedRequest.ProofOfPossessionSignatureBase64));

        var verified = EnrollmentProofOfPossession.Verify(
            capturedRequest.CertificatePem,
            capturedRequest.ProofOfPossessionSignatureBase64,
            cert.Thumbprint,
            EnrollmentTokenHasher.ComputeSha256Hex(rawToken),
            expectedHostname,
            now,
            out var reasonCode);

        Assert.True(verified, $"PoP verification failed with '{reasonCode}'.");
        Assert.Equal("proof_of_possession_valid", reasonCode);
    }

    [Fact]
    public async Task V20_AgentEnrollmentBootstrapper_WithoutPrivateKey_KeepsLegacyMockPath()
    {
        var now = DateTimeOffset.UtcNow;
        using var fullCert = CreateSelfSignedCertificate("RSA", now.AddMinutes(-5), now.AddHours(1));
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(fullCert.Export(X509ContentType.Cert));
        Assert.False(publicOnlyCert.HasPrivateKey);

        var dbPath = Path.Combine(_tempDir, "agent-enroll-nopriv.db");
        await using var store = AgentLocalStore.FromFilePath(dbPath);
        var machineInfoProvider = new WindowsMachineInfoProvider();

        EnrollDeviceRequest? capturedRequest = null;
        var handler = new SequenceHttpMessageHandler(async (request, ct) =>
        {
            var json = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            capturedRequest = JsonSerializer.Deserialize<EnrollDeviceRequest>(json, NamedPipeFraming.SerializerOptions);

            var responseBody = JsonSerializer.Serialize(
                new EnrollDeviceResponse(Guid.NewGuid(), "site-lab", 1, now),
                NamedPipeFraming.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AgentEnrollmentBootstrapper.RunAsync(
            ["enroll", "--token", "raw-token-no-priv", "--api", "https://hive.local", "--thumbprint", publicOnlyCert.Thumbprint],
            new AgentOptions(),
            store,
            machineInfoProvider,
            httpClient,
            stdout,
            stderr,
            _ => publicOnlyCert);

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest!.CertificatePem);
        Assert.Null(capturedRequest.ProofOfPossessionSignatureBase64);
    }

    [Fact]
    public async Task V16_ResumablePackageDownloader_HandlesIfRangeEtagMismatchAnd200RestartFromByteZero()
    {
        var oldPrefix = Encoding.UTF8.GetBytes("OLD-RELEASE-PREFIX-123456");
        var newPayload = Encoding.UTF8.GetBytes("NEW-RELEASE-FULL-BINARY-PAYLOAD-0123456789ABCDEF");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(newPayload)).ToLowerInvariant();
        var destinationPath = Path.Combine(_tempDir, "pkg-ifrange.msi");

        var attempt = 0;
        var handler = new SequenceHttpMessageHandler((request, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new InterruptedStreamContent(oldPrefix)
                };
                resp.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(resp);
            }

            Assert.NotNull(request.Headers.Range);
            Assert.Equal(oldPrefix.Length, Assert.Single(request.Headers.Range.Ranges).From);
            Assert.Equal("\"v1\"", request.Headers.IfRange?.EntityTag?.Tag);

            var restartResp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(newPayload)
            };
            restartResp.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return Task.FromResult(restartResp);
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new ResumablePackageDownloader(httpClient);
        var uri = new Uri("https://hive.local/packages/app.msi");

        var first = await downloader.DownloadAsync(uri, destinationPath, expectedSha256);
        Assert.False(first.Success);
        Assert.Equal("download_stream_error", first.ReasonCode);
        Assert.True(File.Exists($"{destinationPath}.part"));

        var second = await downloader.DownloadAsync(uri, destinationPath, expectedSha256);
        Assert.True(second.Success);
        Assert.Equal("download_verified", second.ReasonCode);
        Assert.Equal(newPayload, await File.ReadAllBytesAsync(destinationPath));
        Assert.False(File.Exists($"{destinationPath}.part"));
        Assert.False(File.Exists($"{destinationPath}.part.etag"));
    }

    [Fact]
    public async Task V16_ResumablePackageDownloader_RestartsFromByteZero_When206ReturnsMismatchedEtag()
    {
        var oldPrefix = Encoding.UTF8.GetBytes("OLD-V1-PREFIX-BYTES");
        var newPayload = Encoding.UTF8.GetBytes("NEW-V2-COMPLETE-PACKAGE-BYTES-9876543210");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(newPayload)).ToLowerInvariant();
        var destinationPath = Path.Combine(_tempDir, "pkg-206-etag-mismatch.msi");

        var attempt = 0;
        var handler = new SequenceHttpMessageHandler((request, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new InterruptedStreamContent(oldPrefix)
                };
                resp.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(resp);
            }

            if (attempt == 2)
            {
                Assert.NotNull(request.Headers.Range);
                var mismatchedPartial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(newPayload[oldPrefix.Length..])
                };
                mismatchedPartial.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
                mismatchedPartial.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(oldPrefix.Length, newPayload.Length - 1, newPayload.Length);
                return Task.FromResult(mismatchedPartial);
            }

            Assert.Null(request.Headers.Range);
            Assert.Null(request.Headers.IfRange);
            var cleanResp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(newPayload)
            };
            cleanResp.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return Task.FromResult(cleanResp);
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new ResumablePackageDownloader(httpClient);
        var uri = new Uri("https://hive.local/packages/app-v2.msi");

        var first = await downloader.DownloadAsync(uri, destinationPath, expectedSha256);
        Assert.False(first.Success);
        Assert.Equal("download_stream_error", first.ReasonCode);

        var second = await downloader.DownloadAsync(uri, destinationPath, expectedSha256);
        Assert.True(second.Success);
        Assert.Equal("download_verified", second.ReasonCode);
        Assert.Equal(3, attempt);
        Assert.Equal(newPayload, await File.ReadAllBytesAsync(destinationPath));
        Assert.False(File.Exists($"{destinationPath}.part"));
        Assert.False(File.Exists($"{destinationPath}.part.etag"));
    }

    [Fact]
    public void V22_WindowsTelemetryCollector_BenchmarkCompletesWithinBoundAndProducesSanitizedCounters()
    {
        var collector = new WindowsTelemetryCollector();
        var bootId = Guid.NewGuid();

        var sw = Stopwatch.StartNew();
        var firstMetrics = collector.SampleSystemMetrics(bootId, sequence: 1);
        Thread.Sleep(40);
        var secondMetrics = collector.SampleSystemMetrics(bootId, sequence: 2);
        var volumes = collector.CaptureVolumes();
        var processes = collector.CaptureProcessSnapshot(maxProcesses: 200);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Telemetry collection exceeded 3s budget: {sw.Elapsed.TotalMilliseconds:N1}ms");

        Assert.NotNull(firstMetrics.MemoryTotalBytes);
        Assert.True(firstMetrics.MemoryTotalBytes > 0);
        Assert.NotNull(secondMetrics.MemoryUsedBytes);
        Assert.True(secondMetrics.MemoryUsedBytes >= 0);
        Assert.NotNull(secondMetrics.CpuPercent);
        Assert.InRange(secondMetrics.CpuPercent!.Value, 0.0, 100.0);

        Assert.NotEmpty(volumes);
        foreach (var vol in volumes)
        {
            Assert.True(vol.TotalBytes >= 0);
            Assert.True(vol.FreeBytes >= 0);
            Assert.True(vol.FreeBytes <= vol.TotalBytes);
        }

        Assert.NotEmpty(processes.Processes);
        foreach (var proc in processes.Processes)
        {
            Assert.True(proc.Pid >= 0);
            Assert.True(proc.WorkingSetBytes >= 0);
            Assert.False(string.IsNullOrWhiteSpace(proc.ImageName));
            Assert.DoesNotContain('\\', proc.ImageName);
            Assert.DoesNotContain('/', proc.ImageName);
            Assert.DoesNotContain(':', proc.ImageName);
            Assert.DoesNotContain('"', proc.ImageName);
            Assert.DoesNotContain("--", proc.ImageName);
            Assert.DoesNotContain(proc.ImageName, char.IsWhiteSpace);
        }
    }

    [Fact]
    public void V24_PowerShellScripts_ParseWithZeroErrorsAndRespectHygieneRules()
    {
        var repoRoot = FindRepoRoot();
        var scriptNames = new[]
        {
            "backup-hive.ps1",
            "restore-hive-drill.ps1",
            "package-release.ps1"
        };

        foreach (var scriptName in scriptNames)
        {
            var scriptPath = Path.Combine(repoRoot, "scripts", scriptName);
            Assert.True(File.Exists(scriptPath), $"Expected script not found: {scriptPath}");

            var content = File.ReadAllText(scriptPath);
            Assert.DoesNotContain("Win32_Product", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".pem", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".key", content, StringComparison.OrdinalIgnoreCase);

            var escapedPath = scriptPath.Replace("'", "''");
            var parseCommand =
                $"$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{escapedPath}', [ref]$null, [ref]$errors); " +
                "if ($errors -and $errors.Count -gt 0) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }";

            var (exitCode, stdout, stderr) = RunPwsh(["-NoProfile", "-Command", parseCommand]);
            Assert.True(exitCode == 0, $"PowerShell syntax parse failed for {scriptName}: stdout={stdout} stderr={stderr}");
        }
    }

    [Fact]
    public void V24_BackupAndRestoreDrill_DryRunEndToEnd_VerifiesManifestAndDetectsTampering()
    {
        var repoRoot = FindRepoRoot();
        var backupScript = Path.Combine(repoRoot, "scripts", "backup-hive.ps1");
        var restoreScript = Path.Combine(repoRoot, "scripts", "restore-hive-drill.ps1");

        var sourceArtifacts = Path.Combine(_tempDir, "source-artifacts");
        var nestedDir = Path.Combine(sourceArtifacts, "releases", "1.0");
        Directory.CreateDirectory(nestedDir);

        var fileA = Path.Combine(sourceArtifacts, "agent.msi");
        var fileB = Path.Combine(nestedDir, "manifest.json");
        File.WriteAllText(fileA, "msi-binary-content-alpha");
        File.WriteAllText(fileB, "{\"version\":\"1.0.0\"}");

        var backupOut = Path.Combine(_tempDir, "backup-out");
        var restoreTarget = Path.Combine(_tempDir, "restore-target");

        var (backupExit, backupStdout, backupStderr) = RunPwsh([
            "-NoProfile", "-File", backupScript,
            "-ArtifactRoot", sourceArtifacts,
            "-OutputDir", backupOut,
            "-DryRun"
        ]);
        Assert.True(backupExit == 0, $"backup-hive.ps1 failed: stdout={backupStdout} stderr={backupStderr}");

        var manifestPath = Path.Combine(backupOut, "backup-manifest.json");
        Assert.True(File.Exists(manifestPath));

        using (var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            var root = manifestDoc.RootElement;
            Assert.True(DateTimeOffset.TryParse(root.GetProperty("createdUtc").GetString(), out _));
            Assert.Equal("cordon_hive", root.GetProperty("database").GetString());
            Assert.Equal(2, root.GetProperty("artifactCount").GetInt32());
            Assert.True(root.GetProperty("dryRun").GetBoolean());

            var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.Equal(2, artifacts.Length);
            foreach (var entry in artifacts)
            {
                var sha = entry.GetProperty("sha256").GetString();
                Assert.False(string.IsNullOrWhiteSpace(sha));
                Assert.Equal(64, sha!.Length);
            }
        }

        var (restoreExit, restoreStdout, restoreStderr) = RunPwsh([
            "-NoProfile", "-File", restoreScript,
            "-BackupDir", backupOut,
            "-ArtifactRoot", restoreTarget,
            "-DryRun"
        ]);
        Assert.True(restoreExit == 0, $"restore-hive-drill.ps1 failed: stdout={restoreStdout} stderr={restoreStderr}");
        Assert.Contains("Cordon Hive Restore Drill Verification", restoreStdout);
        Assert.Contains("ArtifactsVerified: 2", restoreStdout);
        Assert.Contains("RPO Age", restoreStdout);
        Assert.Contains("RTO Elapsed", restoreStdout);

        // Tamper with an artifact and verify restore drill fails closed on SHA-256 mismatch.
        File.WriteAllText(fileA, "tampered-msi-binary-content");
        var (tamperExit, tamperStdout, tamperStderr) = RunPwsh([
            "-NoProfile", "-File", restoreScript,
            "-BackupDir", backupOut,
            "-ArtifactRoot", restoreTarget,
            "-DryRun"
        ]);
        Assert.NotEqual(0, tamperExit);
        Assert.Contains("SHA-256 mismatch", $"{tamperStdout}\n{tamperStderr}", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V24_PackageReleaseScript_DryRun_GeneratesChecksumsAndReleaseManifest()
    {
        var repoRoot = FindRepoRoot();
        var packageScript = Path.Combine(repoRoot, "scripts", "package-release.ps1");
        var releaseOut = Path.Combine(_tempDir, "release-out");

        var (exitCode, stdout, stderr) = RunPwsh([
            "-NoProfile", "-File", packageScript,
            "-Version", "0.1.0-rc1",
            "-OutputDir", releaseOut,
            "-DryRun"
        ]);

        Assert.True(exitCode == 0, $"package-release.ps1 failed: stdout={stdout} stderr={stderr}");

        var sumsPath = Path.Combine(releaseOut, "SHA256SUMS.txt");
        var manifestPath = Path.Combine(releaseOut, "release-manifest.json");
        Assert.True(File.Exists(sumsPath));
        Assert.True(File.Exists(manifestPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        Assert.Equal("0.1.0-rc1", root.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("gitCommit").GetString()));
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("builtAtUtc").GetString(), out _));
        Assert.True(root.GetProperty("dryRun").GetBoolean());

        var targetNames = root.GetProperty("targets")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(new[] { "Hive.Server", "Hive.Agent", "Hive.Installer", "Hive.App" }, targetNames);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", "1.0.0", VersionCompatibilityStatus.Compatible, "version_compatible")]
    [InlineData("v1.2.3-rc1+build42", "1.2.0", "1.0.0", VersionCompatibilityStatus.Compatible, "version_compatible")]
    [InlineData("1.1.5", "1.2.0", "1.0.0", VersionCompatibilityStatus.UpgradeRecommended, "minor_version_behind_server")]
    [InlineData("1.0.0", "1.2.0", "1.1.0", VersionCompatibilityStatus.Incompatible, "below_minimum_supported_version")]
    [InlineData("2.0.0", "1.2.0", "1.0.0", VersionCompatibilityStatus.Incompatible, "major_version_mismatch")]
    [InlineData("", "1.2.0", "1.0.0", VersionCompatibilityStatus.Incompatible, "version_unparseable")]
    public void V24_VersionCompatibilityEvaluator_EvaluatesAllCompatibilityTiers(
        string? clientVersion,
        string serverVersion,
        string minimumVersion,
        VersionCompatibilityStatus expectedStatus,
        string expectedReasonCode)
    {
        var result = VersionCompatibilityEvaluator.Evaluate(clientVersion, serverVersion, minimumVersion);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
    }

    private static X509Certificate2 CreateSelfSignedCertificate(
        string algorithm,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        var subject = new X500DistinguishedName($"CN=hive-test-{algorithm.ToLowerInvariant()}");
        if (string.Equals(algorithm, "RSA", StringComparison.OrdinalIgnoreCase))
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ephemeral = req.CreateSelfSigned(notBefore, notAfter);
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecReq = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        using var ecEphemeral = ecReq.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadPkcs12(ecEphemeral.Export(X509ContentType.Pfx), password: null);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CordonHive.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate CordonHive.sln in parent directories.");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPwsh(IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(milliseconds: 30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("pwsh command timed out after 30 seconds.");
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private sealed class SequenceHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class InterruptedStreamContent(byte[] initialBytes) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new PartialThenFailingStream(initialBytes));

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(initialBytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            throw new IOException("Simulated mid-stream TCP reset.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = initialBytes.Length + 100;
            return true;
        }
    }

    private sealed class PartialThenFailingStream(byte[] initialBytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => initialBytes.Length + 100;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < initialBytes.Length)
            {
                var toCopy = Math.Min(buffer.Length, initialBytes.Length - _offset);
                initialBytes.AsSpan(_offset, toCopy).CopyTo(buffer.Span);
                _offset += toCopy;
                return ValueTask.FromResult(toCopy);
            }

            throw new IOException("Simulated mid-stream TCP reset.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < initialBytes.Length)
            {
                var toCopy = Math.Min(count, initialBytes.Length - _offset);
                Buffer.BlockCopy(initialBytes, _offset, buffer, offset, toCopy);
                _offset += toCopy;
                return toCopy;
            }

            throw new IOException("Simulated mid-stream TCP reset.");
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
