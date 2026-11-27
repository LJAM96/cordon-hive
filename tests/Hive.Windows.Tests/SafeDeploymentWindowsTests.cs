using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Hive.Agent;
using Hive.Contracts;
using Hive.Domain.Deployments;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Installer;
using Hive.Windows.Deployments;
using Hive.Windows.Ipc;
using Hive.Windows.SystemInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class SafeDeploymentWindowsTests : IDisposable
{
    private readonly string _tempDir;

    public SafeDeploymentWindowsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CordonHive.SafeDeployTests.{Guid.NewGuid():N}");
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

    [Fact]
    public async Task ResumablePackageDownloader_ResumesWithHttpRange206_AndVerifiesSha256()
    {
        var fullPayload = Encoding.UTF8.GetBytes("HEADER-0123456789-PAYLOAD-ABCDEFGHIJKLMNOPQRSTUVWXYZ-FOOTER");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(fullPayload)).ToLowerInvariant();
        var splitOffset = 18;

        var destinationPath = Path.Combine(_tempDir, "pkg-resume.msi");
        var partPath = $"{destinationPath}.part";
        await File.WriteAllBytesAsync(partPath, fullPayload[..splitOffset]);

        RangeHeaderValue? observedRange = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            observedRange = request.Headers.Range;
            Assert.NotNull(observedRange);
            var rangeItem = Assert.Single(observedRange.Ranges);
            Assert.Equal(splitOffset, rangeItem.From);
            Assert.Null(rangeItem.To);

            var remaining = fullPayload[splitOffset..];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remaining)
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                splitOffset,
                fullPayload.Length - 1,
                fullPayload.Length);
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new ResumablePackageDownloader(httpClient);

        var result = await downloader.DownloadAsync(
            new Uri("https://hive.local/packages/test.msi"),
            destinationPath,
            expectedSha256);

        Assert.True(result.Success);
        Assert.Equal("download_verified", result.ReasonCode);
        Assert.Equal(destinationPath, result.DestinationPath);
        Assert.Equal(fullPayload.Length, result.BytesOnDisk);
        Assert.Equal(expectedSha256, result.ActualSha256);
        Assert.True(File.Exists(destinationPath));
        Assert.False(File.Exists(partPath));
        Assert.Equal(fullPayload, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task ResumablePackageDownloader_RestartsFromByteZero_WhenServerReturns200Ok()
    {
        var fullPayload = Encoding.UTF8.GetBytes("FRESH-COMPLETE-MSI-CONTENT-1234567890");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(fullPayload)).ToLowerInvariant();

        var destinationPath = Path.Combine(_tempDir, "pkg-restart.msi");
        var partPath = $"{destinationPath}.part";
        await File.WriteAllBytesAsync(partPath, "STALE-CORRUPT-PREFIX"u8.ToArray());

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fullPayload)
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new ResumablePackageDownloader(httpClient);

        var result = await downloader.DownloadAsync(
            new Uri("https://hive.local/packages/restart.msi"),
            destinationPath,
            expectedSha256);

        Assert.True(result.Success);
        Assert.Equal("download_verified", result.ReasonCode);
        Assert.Equal(fullPayload, await File.ReadAllBytesAsync(destinationPath));
        Assert.False(File.Exists(partPath));
    }

    [Fact]
    public async Task ResumablePackageDownloader_DeletesCorruptOutputAndFailsClosed_OnSha256Mismatch()
    {
        var payload = Encoding.UTF8.GetBytes("corrupt-or-tampered-bytes");
        var wrongSha256 = new string('a', 64);

        var destinationPath = Path.Combine(_tempDir, "pkg-corrupt.msi");
        var partPath = $"{destinationPath}.part";
        await File.WriteAllBytesAsync(destinationPath, "old-file"u8.ToArray());

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new ResumablePackageDownloader(httpClient);

        var result = await downloader.DownloadAsync(
            new Uri("https://hive.local/packages/corrupt.msi"),
            destinationPath,
            wrongSha256);

        Assert.False(result.Success);
        Assert.Equal("sha256_mismatch", result.ReasonCode);
        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task SafeMsiBrokerExecutor_DisabledByDefault_FailsClosed()
    {
        var (envelope, stagedPath, request, safetyInput, _) = CreateSignedTestFixture();
        var runner = new RecordingMsiProcessRunner(0);
        var options = new BrokerOptions
        {
            MaintenanceMutexName = UniqueMutexName()
        };
        Assert.False(options.Enabled);

        var executor = new SafeMsiBrokerExecutor(options, runner);
        var result = await executor.ExecuteAsync(request, envelope, stagedPath, safetyInput);

        Assert.False(result.Accepted);
        Assert.Equal(DeploymentState.Deferred, result.ResultingState);
        Assert.Equal("broker_disabled", result.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SafeMsiBrokerExecutor_RejectsTamperedUntrustedAndExpiredManifests()
    {
        var (envelope, stagedPath, request, safetyInput, publicKeyPem) = CreateSignedTestFixture();
        var runner = new RecordingMsiProcessRunner(0);
        var executor = new SafeMsiBrokerExecutor(new BrokerOptions
        {
            Enabled = true,
            MaintenanceMutexName = UniqueMutexName(),
            TrustedSigningPublicKeyPem = publicKeyPem
        }, runner);

        // Tampered signature
        var tamperedSig = envelope with { SignatureBase64 = Convert.ToBase64String(new byte[64]) };
        var tamperedResult = await executor.ExecuteAsync(request, tamperedSig, stagedPath, safetyInput);
        Assert.False(tamperedResult.Accepted);
        Assert.Equal(DeploymentState.Failed, tamperedResult.ResultingState);
        Assert.Equal("manifest_invalid_signature", tamperedResult.ReasonCode);

        // Untrusted key
        var (otherPriv, otherPub, otherKeyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var otherSigned = ReleaseManifestCrypto.Sign(
            envelope.Manifest with { KeyId = otherKeyId },
            otherPriv,
            otherPub);
        var untrustedResult = await executor.ExecuteAsync(request, otherSigned, stagedPath, safetyInput);
        Assert.False(untrustedResult.Accepted);
        Assert.Equal(DeploymentState.Failed, untrustedResult.ResultingState);
        Assert.Equal("manifest_untrusted_key", untrustedResult.ReasonCode);

        // Expired manifest
        var expiredSafety = safetyInput with { UtcNow = envelope.Manifest.ExpiresUtc.AddMinutes(1) };
        var expiredResult = await executor.ExecuteAsync(request, envelope, stagedPath, expiredSafety);
        Assert.False(expiredResult.Accepted);
        Assert.Equal(DeploymentState.Failed, expiredResult.ResultingState);
        Assert.Equal("manifest_expired", expiredResult.ReasonCode);

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SafeMsiBrokerExecutor_BlocksProtectedForensicProcess_WithoutTerminating()
    {
        var (envelope, stagedPath, request, safetyInput, publicKeyPem) = CreateSignedTestFixture(
            protectedProcesses: ["ftk.exe", "encase.exe"]);
        var runner = new RecordingMsiProcessRunner(0);
        var executor = new SafeMsiBrokerExecutor(new BrokerOptions
        {
            Enabled = true,
            MaintenanceMutexName = UniqueMutexName(),
            TrustedSigningPublicKeyPem = publicKeyPem
        }, runner);

        var activeWorkloadInput = safetyInput with
        {
            RunningProcessNames = ["explorer.exe", "ftk.exe"]
        };

        var result = await executor.ExecuteAsync(request, envelope, stagedPath, activeWorkloadInput);

        Assert.False(result.Accepted);
        Assert.Equal(DeploymentState.Deferred, result.ResultingState);
        Assert.Equal("protected_workload_active", result.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SafeMsiBrokerExecutor_BlocksManualLockUnknownStateAndInsufficientDisk()
    {
        var (envelope, stagedPath, request, safetyInput, publicKeyPem) = CreateSignedTestFixture();
        var runner = new RecordingMsiProcessRunner(0);
        var executor = new SafeMsiBrokerExecutor(new BrokerOptions
        {
            Enabled = true,
            MaintenanceMutexName = UniqueMutexName(),
            TrustedSigningPublicKeyPem = publicKeyPem
        }, runner);

        var manualLockResult = await executor.ExecuteAsync(
            request,
            envelope,
            stagedPath,
            safetyInput with { ManualProtectedModeActive = true });
        Assert.False(manualLockResult.Accepted);
        Assert.Equal(DeploymentState.Deferred, manualLockResult.ResultingState);
        Assert.Equal("manual_protection_lock_active", manualLockResult.ReasonCode);

        var unknownStateResult = await executor.ExecuteAsync(
            request,
            envelope,
            stagedPath,
            safetyInput with { ProtectionStateKnown = false });
        Assert.False(unknownStateResult.Accepted);
        Assert.Equal(DeploymentState.Deferred, unknownStateResult.ResultingState);
        Assert.Equal("protection_state_unknown", unknownStateResult.ReasonCode);

        var lowDiskResult = await executor.ExecuteAsync(
            request,
            envelope,
            stagedPath,
            safetyInput with { FreeBytesOnSystemDrive = 512L * 1024L * 1024L });
        Assert.False(lowDiskResult.Accepted);
        Assert.Equal(DeploymentState.Deferred, lowDiskResult.ResultingState);
        Assert.Equal("insufficient_disk_space", lowDiskResult.ReasonCode);

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SafeMsiBrokerExecutor_RejectsConcurrentExecution_WhenMaintenanceMutexHeld()
    {
        var mutexName = UniqueMutexName();
        var (envelope, stagedPath, request, safetyInput, publicKeyPem) = CreateSignedTestFixture();
        var runner = new RecordingMsiProcessRunner(0);
        var executor = new SafeMsiBrokerExecutor(new BrokerOptions
        {
            Enabled = true,
            MaintenanceMutexName = mutexName,
            TrustedSigningPublicKeyPem = publicKeyPem
        }, runner);

        using var acquiredSignal = new ManualResetEventSlim(false);
        using var releaseSignal = new ManualResetEventSlim(false);
        var holderTask = Task.Factory.StartNew(() =>
        {
            using var heldMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            Assert.True(createdNew);
            try
            {
                acquiredSignal.Set();
                releaseSignal.Wait();
            }
            finally
            {
                heldMutex.ReleaseMutex();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        acquiredSignal.Wait();
        try
        {
            var result = await executor.ExecuteAsync(request, envelope, stagedPath, safetyInput);

            Assert.False(result.Accepted);
            Assert.Equal(DeploymentState.Deferred, result.ResultingState);
            Assert.Equal("maintenance_mutex_busy", result.ReasonCode);
            Assert.Empty(runner.Invocations);
        }
        finally
        {
            releaseSignal.Set();
            await holderTask;
        }
    }

    [Fact]
    public void WindowsMsiProcessRunner_UsesStructuredNoRebootArgumentsWithoutShellConcatenation()
    {
        var stagedPath = Path.Combine(_tempDir, "sample installer.msi");
        var startInfo = WindowsMsiProcessRunner.BuildStartInfo("msiexec.exe", stagedPath);

        Assert.Equal("msiexec.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
        Assert.Equal(
            new[] { "/i", stagedPath, "/qn", "/norestart", "REBOOT=ReallySuppress" },
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData(0, true, DeploymentState.Verifying, "msi_exit_success")]
    [InlineData(3010, true, DeploymentState.PendingReboot, "msi_exit_pending_reboot")]
    [InlineData(1641, false, DeploymentState.Failed, "msi_unexpected_reboot_initiated")]
    [InlineData(1603, false, DeploymentState.Failed, "msi_exit_1603")]
    public async Task SafeMsiBrokerExecutor_MapsMsiExitCodesCorrectly(
        int exitCode,
        bool expectedAccepted,
        DeploymentState expectedState,
        string expectedReasonCode)
    {
        var (envelope, stagedPath, request, safetyInput, publicKeyPem) = CreateSignedTestFixture();
        var runner = new RecordingMsiProcessRunner(exitCode);
        var executor = new SafeMsiBrokerExecutor(new BrokerOptions
        {
            Enabled = true,
            MaintenanceMutexName = UniqueMutexName(),
            TrustedSigningPublicKeyPem = publicKeyPem
        }, runner);

        var result = await executor.ExecuteAsync(request, envelope, stagedPath, safetyInput);

        Assert.Equal(expectedAccepted, result.Accepted);
        Assert.Equal(expectedState, result.ResultingState);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
        Assert.Equal(exitCode, result.ExitCode);
        var invokedPath = Assert.Single(runner.Invocations);
        Assert.Equal(stagedPath, invokedPath);
    }

    [Fact]
    public async Task AgentUserIpcWorker_SetAndGetProtectionLock_PersistsAndIgnoresSpoofedSid_AndFlowsToHeartbeat()
    {
        var dbPath = Path.Combine(_tempDir, "agent-state.db");
        await using var store = AgentLocalStore.FromFilePath(dbPath);
        await store.InitializeAsync();

        var deviceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.SaveDeviceStateAsync(new AgentDeviceState(
            DeviceId: deviceId,
            SiteId: "site-alpha",
            CertificateThumbprint: null,
            EnrollmentState: EnrollmentState.Active.ToString(),
            PolicyRevision: 3,
            LastHeartbeatUtc: now.AddSeconds(-10),
            ProtectedModeActive: false,
            UpdatedAtUtc: now.AddSeconds(-10)));

        var runtimeState = new AgentRuntimeState();
        var pipeName = $"CordonHive.Test.{Guid.NewGuid():N}";
        var expectedSid = WindowsIdentity.GetCurrent().User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(expectedSid));

        var ipcWorker = new AgentUserIpcWorker(
            store,
            runtimeState,
            Options.Create(new AgentOptions { UserPipeName = pipeName }),
            NullLoggerFactory.Instance);

        await using var server = new NamedPipeServerHost(
            pipeName,
            NamedPipeSecurityFactory.CreateUserPipeSecurity(),
            ipcWorker.HandleRequestAsync);
        server.Start();

        await using var client = new NamedPipeClient(pipeName);
        await client.ConnectAsync(timeoutMilliseconds: 5000);

        // Attempt to spoof lockedBySid in JSON payload; server must ignore it and use peer SID.
        var spoofedPayloadJson = """{"enable":true,"reasonCode":"forensic_acquisition","lockedBySid":"S-1-5-21-999-999-999-500"}""";
        var setReply = await client.SendAsync(new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.SetProtectionLock,
            Guid.NewGuid().ToString("N"),
            spoofedPayloadJson));

        Assert.Equal(IpcMessageTypes.SetProtectionLock, setReply.MessageType);
        var setLockResp = NamedPipeFraming.DeserializePayload<IpcProtectionLockResponse>(setReply.PayloadJson);
        Assert.True(setLockResp.ProtectedModeActive);
        Assert.Equal("forensic_acquisition", setLockResp.ReasonCode);
        Assert.Equal(expectedSid, setLockResp.LockedBySid);

        // Verify persisted state in SQLite store.
        var savedState = await store.GetDeviceStateAsync();
        Assert.NotNull(savedState);
        Assert.True(savedState!.ProtectedModeActive);
        Assert.Equal(deviceId, savedState.DeviceId);

        var savedCursor = await store.GetSyncCursorAsync(AgentUserIpcWorker.MaintenanceLockCursorKey);
        var persistedLock = AgentUserIpcWorker.TryDeserializeMaintenanceLock(savedCursor);
        Assert.NotNull(persistedLock);
        Assert.True(persistedLock!.ManualProtectedMode);
        Assert.Equal(expectedSid, persistedLock.LockedBySid);

        // Verify GetProtectionLock over IPC.
        var getReply = await client.SendAsync(new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.GetProtectionLock,
            Guid.NewGuid().ToString("N"),
            "{}"));

        Assert.Equal(IpcMessageTypes.GetProtectionLock, getReply.MessageType);
        var getLockResp = NamedPipeFraming.DeserializePayload<IpcProtectionLockResponse>(getReply.PayloadJson);
        Assert.True(getLockResp.ProtectedModeActive);
        Assert.Equal("forensic_acquisition", getLockResp.ReasonCode);
        Assert.Equal(expectedSid, getLockResp.LockedBySid);

        // Verify HeartbeatWorker reads persisted ProtectedModeActive from IAgentLocalStore
        // even with a fresh AgentRuntimeState (e.g., after service restart).
        using var hbServer = new LoopbackHeartbeatServer();
        var freshRuntimeState = new AgentRuntimeState();
        Assert.False(freshRuntimeState.ProtectedModeActive);
        var agentOptions = Options.Create(new AgentOptions
        {
            ApiBaseUrl = hbServer.BaseUrl
        });
        using var httpFactory = new AgentHttpClientFactory(agentOptions, NullLogger<AgentHttpClientFactory>.Instance);
        var heartbeatWorker = new HeartbeatWorker(
            store,
            new WindowsMachineInfoProvider(),
            freshRuntimeState,
            httpFactory,
            agentOptions,
            NullLogger<HeartbeatWorker>.Instance);

        await heartbeatWorker.ExecuteSingleCycleAsync(CancellationToken.None);

        var capturedHeartbeat = Assert.Single(hbServer.ReceivedRequests);
        Assert.True(capturedHeartbeat.ProtectedModeActive);
        Assert.Equal("forensic_acquisition", capturedHeartbeat.ProtectedModeReason);

        // Clear protection lock and verify persisted cleared state.
        var clearReply = await client.SendAsync(new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.SetProtectionLock,
            Guid.NewGuid().ToString("N"),
            NamedPipeFraming.SerializePayload(new IpcProtectionLockRequest(false, "acquisition_complete"))));

        var clearResp = NamedPipeFraming.DeserializePayload<IpcProtectionLockResponse>(clearReply.PayloadJson);
        Assert.False(clearResp.ProtectedModeActive);
        Assert.Equal(expectedSid, clearResp.LockedBySid);

        var clearedState = await store.GetDeviceStateAsync();
        Assert.NotNull(clearedState);
        Assert.False(clearedState!.ProtectedModeActive);
    }

    private (
        SignedManifestEnvelope Envelope,
        string StagedPath,
        BrokerInstallRequest Request,
        SafetyEvaluationInput SafetyInput,
        string PublicKeyPem) CreateSignedTestFixture(IReadOnlyList<string>? protectedProcesses = null)
    {
        var msiBytes = Encoding.UTF8.GetBytes("FAKE-MSI-PACKAGE-BYTES-FOR-BROKER-TESTING");
        var stagedPath = Path.Combine(_tempDir, $"pkg-{Guid.NewGuid():N}.msi");
        File.WriteAllBytes(stagedPath, msiBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();

        var (privPem, pubPem, keyId) = ReleaseManifestCrypto.CreateSigningKeyPair();
        var now = new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero);
        var releaseId = Guid.NewGuid();
        var manifest = new SignedReleaseManifest(
            ReleaseId: releaseId,
            ProductId: Guid.NewGuid(),
            ProductName: "ForensicToolkit",
            Version: "2.1.0",
            InstallerType: "msi",
            PackageSha256: sha256,
            PackageSizeBytes: msiBytes.LongLength,
            DownloadRelativeUrl: $"/api/v1/packages/{releaseId}",
            SilentArgs: "/qn /norestart REBOOT=ReallySuppress",
            VerificationType: "msi_product_code",
            VerificationTarget: "{11111111-1111-1111-1111-111111111111}",
            ExpectedVersion: "2.1.0",
            ProtectedProcesses: protectedProcesses ?? ["ftk.exe"],
            RebootAllowed: false,
            InstallProfile: "validated-msi-no-restart",
            IssuedUtc: now.AddHours(-1),
            ExpiresUtc: now.AddDays(30),
            KeyId: keyId);

        var envelope = ReleaseManifestCrypto.Sign(manifest, privPem, pubPem);
        var manifestDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(envelope.ManifestCanonicalJson))).ToLowerInvariant();

        var request = new BrokerInstallRequest(
            TargetId: Guid.NewGuid(),
            ReleaseId: releaseId,
            ManifestDigest: manifestDigest,
            LeaseId: Guid.NewGuid());

        var safetyInput = new SafetyEvaluationInput(
            ProtectionStateKnown: true,
            ManualProtectedModeActive: false,
            RunningProcessNames: ["explorer.exe", "svchost.exe"],
            ProtectedProcessNames: manifest.ProtectedProcesses,
            FreeBytesOnSystemDrive: 10L * 1024L * 1024L * 1024L,
            PackageSizeBytes: msiBytes.LongLength,
            LeaseActive: true,
            LeaseExpiresUtc: now.AddMinutes(15),
            UtcNow: now);

        return (envelope, stagedPath, request, safetyInput, pubPem);
    }

    private static string UniqueMutexName() => $"Local\\CordonHive.Test.{Guid.NewGuid():N}";

    private sealed class RecordingMsiProcessRunner(int exitCode) : IMsiProcessRunner
    {
        public List<string> Invocations { get; } = [];

        public Task<int> RunMsiAsync(string stagedMsiPath, CancellationToken cancellationToken = default)
        {
            Invocations.Add(stagedMsiPath);
            return Task.FromResult(exitCode);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class LoopbackHeartbeatServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly List<HeartbeatRequest> _requests = [];
        private readonly object _lock = new();

        public LoopbackHeartbeatServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public string BaseUrl { get; }

        public IReadOnlyList<HeartbeatRequest> ReceivedRequests
        {
            get { lock (_lock) { return _requests.ToArray(); } }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var headerBytes = new List<byte>(1024);
                var single = new byte[1];
                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    headerBytes.Add(single[0]);
                    var c = headerBytes.Count;
                    if (c >= 4 && headerBytes[c - 4] == '\r' && headerBytes[c - 3] == '\n' &&
                        headerBytes[c - 2] == '\r' && headerBytes[c - 1] == '\n')
                    {
                        break;
                    }
                }

                var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
                var contentLength = 0;
                var isChunked = false;
                foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = line[..colon].Trim();
                    var val = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var len))
                    {
                        contentLength = len;
                    }
                    else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                             val.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                    {
                        isChunked = true;
                    }
                }

                byte[] bodyBytes;
                if (isChunked)
                {
                    using var ms = new MemoryStream();
                    while (true)
                    {
                        var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                        if (line is null) break;
                        var chunkSize = Convert.ToInt32(line.Split(';')[0].Trim(), 16);
                        if (chunkSize == 0)
                        {
                            _ = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                            break;
                        }

                        var chunk = new byte[chunkSize];
                        await stream.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);
                        ms.Write(chunk);
                        var crlf = new byte[2];
                        await stream.ReadExactlyAsync(crlf, ct).ConfigureAwait(false);
                    }
                    bodyBytes = ms.ToArray();
                }
                else
                {
                    bodyBytes = new byte[contentLength];
                    if (contentLength > 0)
                    {
                        await stream.ReadExactlyAsync(bodyBytes, ct).ConfigureAwait(false);
                    }
                }

                var req = JsonSerializer.Deserialize<HeartbeatRequest>(bodyBytes, NamedPipeFraming.SerializerOptions);
                if (req is not null)
                {
                    lock (_lock)
                    {
                        _requests.Add(req);
                    }
                }

                var respBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new HeartbeatResponse(3, DateTimeOffset.UtcNow, 15),
                    NamedPipeFraming.SerializerOptions);
                var respHeader = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(respHeader, ct).ConfigureAwait(false);
                await stream.WriteAsync(respBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var buf = new List<byte>(32);
            var single = new byte[1];
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                if (read == 0) return null;
                if (single[0] == '\n')
                {
                    if (buf.Count > 0 && buf[^1] == '\r') buf.RemoveAt(buf.Count - 1);
                    return Encoding.ASCII.GetString(buf.ToArray());
                }
                buf.Add(single[0]);
            }
            return null;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }
    }
}
