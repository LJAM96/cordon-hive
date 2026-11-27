using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hive.Agent;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Ipc;
using Hive.Windows.RemoteAccess;
using Hive.Windows.SystemInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class RemoteAccessWindowsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public RemoteAccessWindowsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CordonHiveRemoteAccessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "agent-heartbeat-test.db");
    }

    [Fact]
    public void BuildSafeRdpFileContent_EnforcesNlaTlsAndDisabledRedirections_WithoutPassword51()
    {
        var content = NativeRdpLauncher.BuildSafeRdpFileContent(
            "WS-LAB-01.cordon.local",
            port: 3389,
            username: "CORP\\analyst");

        Assert.Contains("full address:s:WS-LAB-01.cordon.local:3389\r\n", content, StringComparison.Ordinal);
        Assert.Contains("prompt for credentials:i:0\r\n", content, StringComparison.Ordinal);
        Assert.Contains("enablecredsspsupport:i:1\r\n", content, StringComparison.Ordinal);
        Assert.Contains("authentication level:i:2\r\n", content, StringComparison.Ordinal);
        Assert.Contains("redirectclipboard:i:0\r\n", content, StringComparison.Ordinal);
        Assert.Contains("redirectdrives:i:0\r\n", content, StringComparison.Ordinal);
        Assert.Contains("redirectprinters:i:0\r\n", content, StringComparison.Ordinal);
        Assert.Contains("drivestoredirect:s:\r\n", content, StringComparison.Ordinal);
        Assert.Contains("username:s:CORP\\analyst\r\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("password 51", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSafeRdpFileContent_WithoutUsername_PromptsForCredentialsAndStripsInjectedNewlines()
    {
        var withoutUser = NativeRdpLauncher.BuildSafeRdpFileContent("ws02.cordon.local");
        Assert.Contains("prompt for credentials:i:1\r\n", withoutUser, StringComparison.Ordinal);
        Assert.DoesNotContain("username:s:", withoutUser, StringComparison.OrdinalIgnoreCase);

        var withInjectedUser = NativeRdpLauncher.BuildSafeRdpFileContent(
            "ws02.cordon.local",
            port: 3390,
            username: "analyst\r\nredirectdrives:i:1\npassword 51:b:deadbeef");

        var lines = withInjectedUser.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(lines, line => line.Equals("redirectdrives:i:1", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, line => line.StartsWith("password 51:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("full address:s:ws02.cordon.local:3390\r\n", withInjectedUser, StringComparison.Ordinal);
        Assert.Contains("redirectdrives:i:0\r\n", withInjectedUser, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ws01; calc.exe")]
    [InlineData("ws01\r\npassword 51:b:deadbeef")]
    [InlineData("../bad")]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildSafeRdpFileContent_RejectsInjectedOrInvalidHostnames(string badHostname)
    {
        Assert.Throws<ArgumentException>(() => NativeRdpLauncher.BuildSafeRdpFileContent(badHostname));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BuildSafeRdpFileContent_RejectsInvalidPort(int invalidPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeRdpLauncher.BuildSafeRdpFileContent("ws01.cordon.local", invalidPort));
    }

    [Fact]
    public void WindowsRdpCredentialManager_RoundTripWriteAndImmediateDelete_OnSyntheticHostname()
    {
        const string syntheticHost = "cordon-hive-unit-test.invalid";

        try
        {
            var written = WindowsRdpCredentialManager.TryWriteSessionRdpCredential(
                syntheticHost,
                "CORP\\unit-test-user",
                "EphemeralTestSecret!123",
                out var errorMessage);

            Assert.True(written, errorMessage ?? "Expected CredWriteW to succeed.");
            Assert.Null(errorMessage);

            var deleted = WindowsRdpCredentialManager.TryDeleteRdpCredential(syntheticHost);
            Assert.True(deleted);

            var deletedAgain = WindowsRdpCredentialManager.TryDeleteRdpCredential(syntheticHost);
            Assert.False(deletedAgain);
        }
        finally
        {
            WindowsRdpCredentialManager.TryDeleteRdpCredential(syntheticHost);
        }
    }

    [Theory]
    [InlineData("ws01; calc.exe")]
    [InlineData("ws01\r\npassword 51:b:deadbeef")]
    [InlineData("../bad")]
    public void WindowsRdpCredentialManager_RejectsInvalidHostnames(string badHostname)
    {
        var written = WindowsRdpCredentialManager.TryWriteSessionRdpCredential(
            badHostname,
            "CORP\\user",
            "Secret!123",
            out var errorMessage);

        Assert.False(written);
        Assert.False(string.IsNullOrWhiteSpace(errorMessage));
        Assert.False(WindowsRdpCredentialManager.TryDeleteRdpCredential(badHostname));
    }

    [Fact]
    public async Task HeartbeatWorker_PropagatesProtectedModeStateAndReason()
    {
        await using var store = AgentLocalStore.FromFilePath(_dbPath, maxQueueItems: 20);
        await store.InitializeAsync();
        await store.SaveDeviceStateAsync(new AgentDeviceState(
            DeviceId: Guid.NewGuid(),
            SiteId: "site-test",
            CertificateThumbprint: null,
            EnrollmentState: EnrollmentState.Active.ToString(),
            PolicyRevision: 1,
            LastHeartbeatUtc: null,
            ProtectedModeActive: false,
            UpdatedAtUtc: DateTimeOffset.UtcNow));

        using var server = new LoopbackHeartbeatServer();
        var runtimeState = new AgentRuntimeState { ProtectedModeActive = true };
        var options = Options.Create(new AgentOptions
        {
            ApiBaseUrl = server.BaseUrl,
            HeartbeatIntervalSeconds = 10
        });
        using var httpClientFactory = new AgentHttpClientFactory(options, NullLogger<AgentHttpClientFactory>.Instance);
        var worker = new HeartbeatWorker(
            store,
            new WindowsMachineInfoProvider(),
            runtimeState,
            httpClientFactory,
            options,
            NullLogger<HeartbeatWorker>.Instance);

        var activeOutcome = await worker.ExecuteSingleCycleAsync(CancellationToken.None);
        Assert.False(activeOutcome.HaltWorker);
        Assert.Single(server.CapturedHeartbeats);
        Assert.True(server.CapturedHeartbeats[0].ProtectedModeActive);
        Assert.Equal("Local protection lock active", server.CapturedHeartbeats[0].ProtectedModeReason);

        runtimeState.ProtectedModeActive = false;
        var clearedOutcome = await worker.ExecuteSingleCycleAsync(CancellationToken.None);
        Assert.False(clearedOutcome.HaltWorker);
        Assert.Equal(2, server.CapturedHeartbeats.Count);
        Assert.False(server.CapturedHeartbeats[1].ProtectedModeActive);
        Assert.Null(server.CapturedHeartbeats[1].ProtectedModeReason);
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
            // Ignore temp cleanup failure on test exit.
        }
    }

    private sealed class LoopbackHeartbeatServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;

        public LoopbackHeartbeatServer()
        {
            var port = GetFreeTcpPort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _acceptTask = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }
        public List<HeartbeatRequest> CapturedHeartbeats { get; } = new();

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var bodyText = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
                    var payload = JsonSerializer.Deserialize<HeartbeatRequest>(bodyText, NamedPipeFraming.SerializerOptions);
                    if (payload is not null)
                    {
                        CapturedHeartbeats.Add(payload);
                    }

                    var responseBytes = JsonSerializer.SerializeToUtf8Bytes(
                        new HeartbeatResponse(PolicyRevision: 1, ServerTimeUtc: DateTimeOffset.UtcNow, NextHeartbeatSeconds: 10),
                        NamedPipeFraming.SerializerOptions);

                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = responseBytes.Length;
                    await context.Response.OutputStream.WriteAsync(responseBytes, _cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }

        private static int GetFreeTcpPort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try
            {
                _acceptTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore shutdown race.
            }

            _cts.Dispose();
        }
    }
}
