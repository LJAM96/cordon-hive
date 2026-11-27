using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Hive.Agent;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class NamedPipeIpcTests
{
    [Fact]
    public async Task LocalNamedPipe_HelloAndGetStatus_ReturnsServerDerivedCallerSid()
    {
        var pipeName = $"CordonHive.Test.{Guid.NewGuid():N}";
        var expectedSid = WindowsIdentity.GetCurrent().User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(expectedSid));

        var deviceId = Guid.NewGuid();
        string? observedServerCallerSid = null;

        await using var server = new NamedPipeServerHost(
            pipeName,
            NamedPipeSecurityFactory.CreateUserPipeSecurity(),
            (request, callerSid, _) =>
            {
                observedServerCallerSid = callerSid;

                if (request.MessageType == IpcMessageTypes.Hello)
                {
                    var helloReq = NamedPipeFraming.DeserializePayload<IpcHelloRequest>(request.PayloadJson);
                    Assert.Equal(1, helloReq.ClientVersion);

                    var resp = new IpcHelloResponse(
                        ProtocolConstants.IpcProtocolVersion,
                        "1.0.0-test",
                        callerSid);

                    return Task.FromResult(new IpcEnvelope(
                        ProtocolConstants.IpcProtocolVersion,
                        IpcMessageTypes.Hello,
                        request.CorrelationId,
                        NamedPipeFraming.SerializePayload(resp)));
                }

                if (request.MessageType == IpcMessageTypes.GetStatus)
                {
                    var status = new IpcAgentStatusResponse(
                        deviceId,
                        "Active",
                        "Online",
                        DateTimeOffset.UtcNow,
                        PolicyRevision: 7,
                        ProtectedModeActive: false,
                        PendingQueueCount: 2);

                    return Task.FromResult(new IpcEnvelope(
                        ProtocolConstants.IpcProtocolVersion,
                        IpcMessageTypes.GetStatus,
                        request.CorrelationId,
                        NamedPipeFraming.SerializePayload(status)));
                }

                throw new InvalidDataException("Unexpected message type.");
            });

        server.Start();

        await using var client = new NamedPipeClient(pipeName);
        await client.ConnectAsync(timeoutMilliseconds: 5000);

        // Even if the client includes a fake SID in ClientName, the server must report the real caller SID.
        var helloEnvelope = new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.Hello,
            Guid.NewGuid().ToString("N"),
            NamedPipeFraming.SerializePayload(new IpcHelloRequest(1, "SpoofedSid:S-1-5-21-0-0-0-500")));

        var helloReply = await client.SendAsync(helloEnvelope);
        Assert.Equal(IpcMessageTypes.Hello, helloReply.MessageType);
        var helloPayload = NamedPipeFraming.DeserializePayload<IpcHelloResponse>(helloReply.PayloadJson);
        Assert.Equal(expectedSid, helloPayload.AuthenticatedCallerSid);
        Assert.Equal(expectedSid, observedServerCallerSid);

        var statusEnvelope = new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.GetStatus,
            Guid.NewGuid().ToString("N"),
            "{}");

        var statusReply = await client.SendAsync(statusEnvelope);
        Assert.Equal(IpcMessageTypes.GetStatus, statusReply.MessageType);
        var statusPayload = NamedPipeFraming.DeserializePayload<IpcAgentStatusResponse>(statusReply.PayloadJson);
        Assert.Equal(deviceId, statusPayload.DeviceId);
        Assert.Equal("Active", statusPayload.EnrollmentState);
        Assert.Equal(7, statusPayload.PolicyRevision);
        Assert.Equal(2, statusPayload.PendingQueueCount);
    }

    [Fact]
    public async Task LocalNamedPipe_GetLocalTelemetry_RoundTripsSnapshot()
    {
        var pipeName = $"CordonHive.Test.{Guid.NewGuid():N}";
        var now = new DateTimeOffset(2026, 11, 27, 13, 30, 0, TimeSpan.Zero);
        var bootId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();

        var expectedTelemetry = new IpcLocalTelemetryResponse(
            LatestSample: new MetricSampleDto(
                bootId,
                SampleSequence: 12,
                SampledAtUtc: now,
                CpuPercent: 23.5,
                MemoryUsedPercent: 48.0,
                MemoryUsedBytes: 8_000_000_000,
                MemoryTotalBytes: 16_000_000_000,
                GpuPercent: null,
                DiskActivePercent: null),
            Volumes:
            [
                new VolumeSnapshotDto("C:\\", "Fixed", 500_000_000_000, 210_000_000_000, now)
            ],
            LatestProcessSnapshot: new ProcessSnapshotDto(
                snapshotId,
                now,
                [new ProcessEntryDto("chrome.exe", 4242, now.AddMinutes(-10), 4.2, 512_000_000)]),
            ConnectivityState: "Online",
            LastHeartbeatUtc: now.AddSeconds(-5));

        await using var server = new NamedPipeServerHost(
            pipeName,
            NamedPipeSecurityFactory.CreateUserPipeSecurity(),
            (request, _, _) =>
            {
                if (request.MessageType == IpcMessageTypes.GetLocalTelemetry)
                {
                    return Task.FromResult(new IpcEnvelope(
                        ProtocolConstants.IpcProtocolVersion,
                        IpcMessageTypes.GetLocalTelemetry,
                        request.CorrelationId,
                        NamedPipeFraming.SerializePayload(expectedTelemetry)));
                }

                throw new InvalidDataException("Unexpected message type.");
            });

        server.Start();

        await using var client = new NamedPipeClient(pipeName);
        await client.ConnectAsync(timeoutMilliseconds: 5000);

        var requestEnvelope = new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.GetLocalTelemetry,
            Guid.NewGuid().ToString("N"),
            "{}");

        var reply = await client.SendAsync(requestEnvelope);
        Assert.Equal(IpcMessageTypes.GetLocalTelemetry, reply.MessageType);

        var actual = NamedPipeFraming.DeserializePayload<IpcLocalTelemetryResponse>(reply.PayloadJson);
        Assert.NotNull(actual.LatestSample);
        Assert.Equal(bootId, actual.LatestSample!.BootId);
        Assert.Equal(12, actual.LatestSample.SampleSequence);
        Assert.Equal(23.5, actual.LatestSample.CpuPercent);
        Assert.Null(actual.LatestSample.GpuPercent);
        Assert.Single(actual.Volumes);
        Assert.Equal("C:\\", actual.Volumes[0].VolumeId);
        Assert.NotNull(actual.LatestProcessSnapshot);
        Assert.Equal(snapshotId, actual.LatestProcessSnapshot!.SnapshotId);
        Assert.Single(actual.LatestProcessSnapshot.Processes);
        Assert.Equal("chrome.exe", actual.LatestProcessSnapshot.Processes[0].ImageName);
        Assert.Equal("Online", actual.ConnectivityState);
        Assert.Equal(now.AddSeconds(-5), actual.LastHeartbeatUtc);
    }

    [Fact]
    public async Task Framing_RejectsOversizedFrames_OnWriteAndRead()
    {
        var oversizedPayload = new string('A', ProtocolConstants.MaxIpcMessageBytes + 1024);
        var envelope = new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.Hello,
            "corr-1",
            oversizedPayload);

        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeFraming.WriteEnvelopeAsync(ms, envelope));

        // Simulate raw stream declaring an oversized frame (> 64 KiB)
        var rawOversizedHeader = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(rawOversizedHeader, ProtocolConstants.MaxIpcMessageBytes + 1);
        using var oversizedStream = new MemoryStream(rawOversizedHeader);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeFraming.ReadEnvelopeAsync(oversizedStream));

        // Simulate negative length
        var rawNegativeHeader = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(rawNegativeHeader, -10);
        using var negativeStream = new MemoryStream(rawNegativeHeader);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeFraming.ReadEnvelopeAsync(negativeStream));
    }

    [Fact]
    public async Task Framing_AndPipeServer_RejectMalformedJson()
    {
        var malformedBytes = Encoding.UTF8.GetBytes("{not-valid-json");
        using var ms = new MemoryStream();
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, malformedBytes.Length);
        await ms.WriteAsync(len);
        await ms.WriteAsync(malformedBytes);
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeFraming.ReadEnvelopeAsync(ms));

        // Verify live pipe server rejects malformed JSON frame with an Error response
        var pipeName = $"CordonHive.Test.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerHost(
            pipeName,
            NamedPipeSecurityFactory.CreateUserPipeSecurity(),
            (_, _, _) => throw new InvalidOperationException("Handler must not run for malformed JSON."));
        server.Start();

        await using var rawClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await rawClient.ConnectAsync(5000);
        await rawClient.WriteAsync(len);
        await rawClient.WriteAsync(malformedBytes);
        await rawClient.FlushAsync();

        var errorReply = await NamedPipeFraming.ReadEnvelopeAsync(rawClient);
        Assert.NotNull(errorReply);
        Assert.Equal(IpcMessageTypes.Error, errorReply.MessageType);
    }

    [Fact]
    public async Task LocalNamedPipe_GetLocalTelemetry_CapsLargeProcessSnapshotWithinMaxIpcMessageBytes()
    {
        var pipeName = $"CordonHive.Test.{Guid.NewGuid():N}";
        var now = new DateTimeOffset(2026, 11, 27, 13, 45, 0, TimeSpan.Zero);
        var bootId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();

        var processes = Enumerable.Range(0, 200)
            .Select(i =>
            {
                var prefix = $"proc-{i:D3}-";
                var imageName = prefix + new string('a', 120 - prefix.Length);
                return new ProcessEntryDto(
                    ImageName: imageName,
                    Pid: 1000 + i,
                    StartTimeUtc: now.AddMinutes(-i),
                    CpuPercent: (i % 50) * 0.5,
                    WorkingSetBytes: (200L - i) * 10_000_000L);
            })
            .ToArray();

        Assert.All(processes, p => Assert.Equal(120, p.ImageName.Length));

        var runtimeState = new AgentRuntimeState();
        runtimeState.RecordHeartbeatSuccess(now.AddSeconds(-3));
        runtimeState.UpdateSample(new MetricSampleDto(
            bootId,
            SampleSequence: 42,
            SampledAtUtc: now,
            CpuPercent: 31.2,
            MemoryUsedPercent: 64.5,
            MemoryUsedBytes: 16_000_000_000,
            MemoryTotalBytes: 32_000_000_000,
            GpuPercent: null,
            DiskActivePercent: null));
        runtimeState.UpdateVolumes(
        [
            new VolumeSnapshotDto("C:\\", "Fixed", 1_000_000_000_000, 420_000_000_000, now)
        ]);
        runtimeState.UpdateProcessSnapshot(new ProcessSnapshotDto(snapshotId, now, processes));

        await using var store = new NoOpAgentLocalStore();
        var worker = new AgentUserIpcWorker(
            store,
            runtimeState,
            Options.Create(new AgentOptions()),
            NullLoggerFactory.Instance);

        await using var server = new NamedPipeServerHost(
            pipeName,
            NamedPipeSecurityFactory.CreateUserPipeSecurity(),
            worker.HandleRequestAsync);
        server.Start();

        await using var client = new NamedPipeClient(pipeName);
        await client.ConnectAsync(timeoutMilliseconds: 5000);

        var requestEnvelope = new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.GetLocalTelemetry,
            Guid.NewGuid().ToString("N"),
            "{}");

        var reply = await client.SendAsync(requestEnvelope);
        Assert.Equal(IpcMessageTypes.GetLocalTelemetry, reply.MessageType);

        var wireBytes = JsonSerializer.SerializeToUtf8Bytes(reply, NamedPipeFraming.SerializerOptions);
        Assert.True(
            wireBytes.Length <= ProtocolConstants.MaxIpcMessageBytes,
            $"Serialized IPC envelope ({wireBytes.Length} bytes) exceeded {ProtocolConstants.MaxIpcMessageBytes} bytes.");
        Assert.True(
            wireBytes.Length <= ProtocolConstants.MaxIpcMessageBytes - AgentRuntimeState.IpcEnvelopeSafetyMarginBytes,
            $"Serialized IPC envelope ({wireBytes.Length} bytes) exceeded safety threshold.");

        var actual = NamedPipeFraming.DeserializePayload<IpcLocalTelemetryResponse>(reply.PayloadJson);
        Assert.NotNull(actual.LatestProcessSnapshot);
        Assert.Equal(snapshotId, actual.LatestProcessSnapshot!.SnapshotId);
        Assert.InRange(actual.LatestProcessSnapshot.Processes.Count, 1, AgentRuntimeState.MaxIpcProcesses);

        // Verify the top memory-consuming processes are preserved in descending WorkingSetBytes order.
        var expectedTop = processes.Take(actual.LatestProcessSnapshot.Processes.Count).ToArray();
        Assert.Equal(expectedTop.Select(p => p.Pid), actual.LatestProcessSnapshot.Processes.Select(p => p.Pid));
        Assert.Equal(expectedTop.Select(p => p.WorkingSetBytes), actual.LatestProcessSnapshot.Processes.Select(p => p.WorkingSetBytes));
    }

    [Fact]
    public async Task LocalNamedPipe_GetLocalTelemetry_ProgressivelyTrimsOversizedEntriesWithinSafetyMargin()
    {
        var pipeName = $"CordonHive.Test.{Guid.NewGuid():N}";
        var now = new DateTimeOffset(2026, 11, 27, 13, 50, 0, TimeSpan.Zero);

        // Even 80 processes with 2000-character names exceed 64 KiB, forcing progressive halving.
        var processes = Enumerable.Range(0, 200)
            .Select(i =>
            {
                var prefix = $"proc-{i:D3}-";
                var imageName = prefix + new string('z', 2000 - prefix.Length);
                return new ProcessEntryDto(
                    ImageName: imageName,
                    Pid: 2000 + i,
                    StartTimeUtc: now.AddMinutes(-i),
                    CpuPercent: 1.5,
                    WorkingSetBytes: (200L - i) * 5_000_000L);
            })
            .ToArray();

        var volumes = Enumerable.Range(0, 20)
            .Select(i => new VolumeSnapshotDto($"V{i}:\\", "Fixed", 100_000_000_000, 50_000_000_000, now))
            .ToArray();

        var runtimeState = new AgentRuntimeState();
        runtimeState.UpdateVolumes(volumes);
        runtimeState.UpdateProcessSnapshot(new ProcessSnapshotDto(Guid.NewGuid(), now, processes));

        await using var store = new NoOpAgentLocalStore();
        var worker = new AgentUserIpcWorker(
            store,
            runtimeState,
            Options.Create(new AgentOptions()),
            NullLoggerFactory.Instance);

        await using var server = new NamedPipeServerHost(
            pipeName,
            NamedPipeSecurityFactory.CreateUserPipeSecurity(),
            worker.HandleRequestAsync);
        server.Start();

        await using var client = new NamedPipeClient(pipeName);
        await client.ConnectAsync(timeoutMilliseconds: 5000);

        var reply = await client.SendAsync(new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.GetLocalTelemetry,
            Guid.NewGuid().ToString("N"),
            "{}"));

        Assert.Equal(IpcMessageTypes.GetLocalTelemetry, reply.MessageType);
        var wireBytes = JsonSerializer.SerializeToUtf8Bytes(reply, NamedPipeFraming.SerializerOptions);
        Assert.True(
            wireBytes.Length <= ProtocolConstants.MaxIpcMessageBytes - AgentRuntimeState.IpcEnvelopeSafetyMarginBytes,
            $"Serialized IPC envelope ({wireBytes.Length} bytes) exceeded safety threshold.");

        var actual = NamedPipeFraming.DeserializePayload<IpcLocalTelemetryResponse>(reply.PayloadJson);
        Assert.NotNull(actual.LatestProcessSnapshot);
        Assert.InRange(actual.LatestProcessSnapshot!.Processes.Count, 1, AgentRuntimeState.MaxIpcProcesses - 1);

        var expectedTop = processes.Take(actual.LatestProcessSnapshot.Processes.Count).ToArray();
        Assert.Equal(expectedTop.Select(p => p.Pid), actual.LatestProcessSnapshot.Processes.Select(p => p.Pid));
    }

    private sealed class NoOpAgentLocalStore : IAgentLocalStore
    {
        public int MaxQueueItems => 100;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentDeviceState?> GetDeviceStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<AgentDeviceState?>(null);
        public Task SaveDeviceStateAsync(AgentDeviceState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateEnrollmentStateAsync(string enrollmentState, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<long?> EnqueueOutboundAsync(string itemType, string payloadJson, bool isCritical = false, string? coalesceKey = null, DateTimeOffset? createdAtUtc = null, CancellationToken cancellationToken = default) => Task.FromResult<long?>(null);
        public Task<IReadOnlyList<OutboundQueueItem>> GetPendingOutboundAsync(int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OutboundQueueItem>>(Array.Empty<OutboundQueueItem>());
        public Task RemoveOutboundAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOutboundBatchAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetOutboundCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<string?> GetSyncCursorAsync(string streamName, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveSyncCursorAsync(string streamName, string cursorValue, DateTimeOffset? updatedAtUtc = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
