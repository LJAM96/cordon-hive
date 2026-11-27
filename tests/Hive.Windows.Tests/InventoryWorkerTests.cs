using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hive.Agent;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Inventory;
using Hive.Windows.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class InventoryWorkerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public InventoryWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CordonHiveInventoryWorkerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "agent-inventory-test.db");
    }

    [Fact]
    public async Task ExecuteSingleCycleAsync_OfflineQueuesCoalescedSnapshot_AndDrainsOnReconnect()
    {
        await using var store = AgentLocalStore.FromFilePath(_dbPath, maxQueueItems: 20);
        await store.InitializeAsync();
        await SeedActiveDeviceAsync(store, policyRevision: 9);

        var collector = new StubInventoryCollector();
        using var server = new LoopbackInventoryServer(HttpStatusCode.ServiceUnavailable);
        var options = Options.Create(new AgentOptions
        {
            ApiBaseUrl = server.BaseUrl,
            InventoryIntervalMinutes = 15,
            HeartbeatIntervalSeconds = 10
        });
        using var httpClientFactory = new AgentHttpClientFactory(options, NullLogger<AgentHttpClientFactory>.Instance);
        var worker = new InventoryWorker(
            store,
            collector,
            httpClientFactory,
            options,
            NullLogger<InventoryWorker>.Instance);

        // First offline failure queues one snapshot.
        var firstOutcome = await worker.ExecuteSingleCycleAsync(CancellationToken.None);
        Assert.False(firstOutcome.HaltWorker);
        Assert.Equal(EnrollmentState.Active, firstOutcome.EnrollmentState);

        // Second offline failure coalesces via coalesceKey = "software_inventory" so count stays 1.
        var secondOutcome = await worker.ExecuteSingleCycleAsync(CancellationToken.None);
        Assert.False(secondOutcome.HaltWorker);

        var pending = await store.GetPendingOutboundAsync();
        var queued = Assert.Single(pending);
        Assert.Equal("software_inventory", queued.ItemType);
        Assert.Equal("software_inventory", queued.CoalesceKey);
        Assert.False(queued.IsCritical);

        // Reconnect: server now returns 200 OK. Worker drains queued item AND sends latest snapshot.
        server.ClearRequests();
        server.StatusCode = HttpStatusCode.OK;

        var reconnectOutcome = await worker.ExecuteSingleCycleAsync(CancellationToken.None);
        Assert.False(reconnectOutcome.HaltWorker);
        Assert.Equal(TimeSpan.FromMinutes(15), reconnectOutcome.NextDelay);

        Assert.Equal(0, await store.GetOutboundCountAsync());
        Assert.Equal(2, server.ReceivedRequests.Count);
        Assert.All(server.ReceivedRequests, req =>
        {
            Assert.Equal("PUT", req.Method);
            Assert.Equal("/api/v1/agent/inventory", req.Path);
            Assert.Equal(9, req.Snapshot.DetectionRevision);
        });
    }

    [Fact]
    public async Task ExecuteSingleCycleAsync_OnUnauthorized_RevokesEnrollmentAndHalts()
    {
        await using var store = AgentLocalStore.FromFilePath(_dbPath, maxQueueItems: 20);
        await store.InitializeAsync();
        await SeedActiveDeviceAsync(store, policyRevision: 3);

        var collector = new StubInventoryCollector();
        using var server = new LoopbackInventoryServer(HttpStatusCode.Unauthorized);
        var options = Options.Create(new AgentOptions
        {
            ApiBaseUrl = server.BaseUrl,
            InventoryIntervalMinutes = 15
        });
        using var httpClientFactory = new AgentHttpClientFactory(options, NullLogger<AgentHttpClientFactory>.Instance);
        var worker = new InventoryWorker(
            store,
            collector,
            httpClientFactory,
            options,
            NullLogger<InventoryWorker>.Instance);

        var outcome = await worker.ExecuteSingleCycleAsync(CancellationToken.None);

        Assert.True(outcome.HaltWorker);
        Assert.Equal(EnrollmentState.Revoked, outcome.EnrollmentState);

        var updatedState = await store.GetDeviceStateAsync();
        Assert.NotNull(updatedState);
        Assert.Equal(EnrollmentState.Revoked.ToString(), updatedState!.EnrollmentState);
    }

    private static Task SeedActiveDeviceAsync(IAgentLocalStore store, long policyRevision) =>
        store.SaveDeviceStateAsync(new AgentDeviceState(
            DeviceId: Guid.NewGuid(),
            SiteId: "lab-01",
            CertificateThumbprint: null,
            EnrollmentState: EnrollmentState.Active.ToString(),
            PolicyRevision: policyRevision,
            LastHeartbeatUtc: DateTimeOffset.UtcNow,
            ProtectedModeActive: false,
            UpdatedAtUtc: DateTimeOffset.UtcNow));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class StubInventoryCollector : ISoftwareInventoryCollector
    {
        private int _callCount;

        public Task<InventorySnapshotRequest> CollectAsync(
            long detectionRevision,
            SoftwareInventoryOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var seq = Interlocked.Increment(ref _callCount);
            var now = DateTimeOffset.UtcNow;
            var snapshot = new InventorySnapshotRequest(
                detectionRevision,
                IsComplete: true,
                Items:
                [
                    new InstalledSoftwareItemDto(
                        DisplayName: $"StubTool-{seq}",
                        Publisher: "Cordon",
                        ObservedVersionRaw: "1.0.0.0",
                        DetectionSource: "RegistryMachine64",
                        InstallScope: "Machine",
                        ConfidenceState: "Verified",
                        ObservedAtUtc: now)
                ],
                CapturedAtUtc: now);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class LoopbackInventoryServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly List<(string Method, string Path, InventorySnapshotRequest Snapshot)> _requests = new();
        private readonly object _lock = new();

        public LoopbackInventoryServer(HttpStatusCode initialStatusCode)
        {
            StatusCode = initialStatusCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public string BaseUrl { get; }
        public HttpStatusCode StatusCode { get; set; }

        public IReadOnlyList<(string Method, string Path, InventorySnapshotRequest Snapshot)> ReceivedRequests
        {
            get { lock (_lock) { return _requests.ToArray(); } }
        }

        public void ClearRequests()
        {
            lock (_lock)
            {
                _requests.Clear();
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var headers = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (headers is null)
                    {
                        break;
                    }

                    var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    var requestLineParts = lines[0].Split(' ');
                    var method = requestLineParts[0];
                    var path = requestLineParts[1];

                    var contentLength = 0;
                    var isChunked = false;
                    foreach (var line in lines.Skip(1))
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0)
                        {
                            var headerName = line[..colon].Trim();
                            var headerValue = line[(colon + 1)..].Trim();
                            if (headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(headerValue, out var parsedLength))
                            {
                                contentLength = parsedLength;
                            }
                            else if (headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                                headerValue.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                            {
                                isChunked = true;
                            }
                        }
                    }

                    byte[] bodyBytes;
                    if (isChunked)
                    {
                        bodyBytes = await ReadChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        bodyBytes = new byte[contentLength];
                        if (contentLength > 0)
                        {
                            await stream.ReadExactlyAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var snapshot = JsonSerializer.Deserialize<InventorySnapshotRequest>(
                        bodyBytes,
                        NamedPipeFraming.SerializerOptions);
                    if (snapshot is not null)
                    {
                        lock (_lock)
                        {
                            _requests.Add((method, path, snapshot));
                        }
                    }

                    var status = StatusCode;
                    var responseBody = status == HttpStatusCode.OK
                        ? JsonSerializer.SerializeToUtf8Bytes(
                            new InventorySnapshotResponse(snapshot?.Items.Count ?? 0, DateTimeOffset.UtcNow),
                            NamedPipeFraming.SerializerOptions)
                        : Array.Empty<byte>();

                    var responseHeader = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {(int)status} {status}\r\n" +
                        $"Content-Type: application/json\r\n" +
                        $"Content-Length: {responseBody.Length}\r\n" +
                        "Connection: close\r\n\r\n");

                    await stream.WriteAsync(responseHeader, cancellationToken).ConfigureAwait(false);
                    if (responseBody.Length > 0)
                    {
                        await stream.WriteAsync(responseBody, cancellationToken).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsciiAsync(stream, cancellationToken).ConfigureAwait(false);
                if (sizeLine is null)
                {
                    break;
                }

                var semi = sizeLine.IndexOf(';');
                var hexPart = semi >= 0 ? sizeLine[..semi] : sizeLine;
                var chunkSize = Convert.ToInt32(hexPart.Trim(), 16);
                if (chunkSize == 0)
                {
                    // Consume trailing CRLF after 0-byte chunk
                    _ = await ReadLineAsciiAsync(stream, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var chunk = new byte[chunkSize];
                await stream.ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false);
                ms.Write(chunk, 0, chunk.Length);
                // Consume CRLF following chunk data
                var crlf = new byte[2];
                await stream.ReadExactlyAsync(crlf, cancellationToken).ConfigureAwait(false);
            }

            return ms.ToArray();
        }

        private static async Task<string?> ReadLineAsciiAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new List<byte>(64);
            var singleByte = new byte[1];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                if (singleByte[0] == '\n')
                {
                    if (buffer.Count > 0 && buffer[^1] == '\r')
                    {
                        buffer.RemoveAt(buffer.Count - 1);
                    }

                    return Encoding.ASCII.GetString(buffer.ToArray());
                }

                buffer.Add(singleByte[0]);
            }

            return null;
        }

        private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new List<byte>(1024);
            var singleByte = new byte[1];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                buffer.Add(singleByte[0]);
                var count = buffer.Count;
                if (count >= 4 &&
                    buffer[count - 4] == '\r' &&
                    buffer[count - 3] == '\n' &&
                    buffer[count - 2] == '\r' &&
                    buffer[count - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer.ToArray());
                }
            }

            return null;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore shutdown races.
            }

            _cts.Dispose();
        }
    }
}
