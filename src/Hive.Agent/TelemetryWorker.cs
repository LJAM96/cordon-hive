using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Ipc;
using Hive.Windows.SystemInfo;
using Hive.Windows.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Agent;

/// <summary>
/// Samples local system metrics, volumes, and process snapshots on independent intervals,
/// updates <see cref="AgentRuntimeState"/> for instant local IPC visibility, and uploads
/// batches to <c>/api/v1/agent/metrics</c> with durable SQLite offline buffering.
/// </summary>
public sealed class TelemetryWorker : BackgroundService
{
    public const string TelemetryBatchItemType = "telemetry_batch";
    private const int MaxBufferedSamples = 300;
    private const int MaxDrainPerCycle = 20;

    private readonly IAgentLocalStore _store;
    private readonly ITelemetryCollector _collector;
    private readonly WindowsMachineInfoProvider _machineInfoProvider;
    private readonly AgentRuntimeState _runtimeState;
    private readonly AgentHttpClientFactory _httpClientFactory;
    private readonly AgentOptions _options;
    private readonly ILogger<TelemetryWorker> _logger;

    private readonly List<MetricSampleDto> _pendingSamples = new();
    private IReadOnlyList<VolumeSnapshotDto>? _pendingVolumes;
    private ProcessSnapshotDto? _pendingProcessSnapshot;

    private Guid _bootId;
    private long _sampleSequence;
    private DateTimeOffset _nextVolumeCaptureUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextProcessCaptureUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextUploadUtc = DateTimeOffset.MinValue;
    private int _consecutiveUploadFailures;

    public TelemetryWorker(
        IAgentLocalStore store,
        ITelemetryCollector collector,
        WindowsMachineInfoProvider machineInfoProvider,
        AgentRuntimeState runtimeState,
        AgentHttpClientFactory httpClientFactory,
        IOptions<AgentOptions> options,
        ILogger<TelemetryWorker> logger)
    {
        _store = store;
        _collector = collector;
        _machineInfoProvider = machineInfoProvider;
        _runtimeState = runtimeState;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);
        _bootId = _machineInfoProvider.GetMachineInfo().BootId;

        var sampleInterval = TimeSpan.FromSeconds(Math.Clamp(_options.LocalSampleIntervalSeconds, 1, 60));

        while (!stoppingToken.IsCancellationRequested)
        {
            var halt = await ExecuteSingleTickAsync(stoppingToken).ConfigureAwait(false);
            if (halt)
            {
                _logger.LogWarning("TelemetryWorker halting because device enrollment was revoked or decommissioned.");
                break;
            }

            try
            {
                await Task.Delay(sampleInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<bool> ExecuteSingleTickAsync(CancellationToken cancellationToken)
    {
        if (_bootId == Guid.Empty)
        {
            _bootId = _machineInfoProvider.GetMachineInfo().BootId;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var volumeInterval = TimeSpan.FromSeconds(Math.Clamp(_options.VolumeSampleIntervalSeconds, 5, 3600));
        var processInterval = TimeSpan.FromSeconds(Math.Clamp(_options.ProcessSampleIntervalSeconds, 5, 3600));
        var uploadInterval = TimeSpan.FromSeconds(Math.Clamp(_options.TelemetryUploadIntervalSeconds, 2, 300));

        // 1. Sample CPU/RAM metrics every tick
        _sampleSequence++;
        var sample = _collector.SampleSystemMetrics(_bootId, _sampleSequence);
        _runtimeState.UpdateSample(sample);

        _pendingSamples.Add(sample);
        if (_pendingSamples.Count > MaxBufferedSamples)
        {
            _pendingSamples.RemoveAt(0);
        }

        // 2. Refresh volume inventory on its own cadence (default 60s)
        if (nowUtc >= _nextVolumeCaptureUtc)
        {
            var volumes = _collector.CaptureVolumes();
            _runtimeState.UpdateVolumes(volumes);
            _pendingVolumes = volumes;
            _nextVolumeCaptureUtc = nowUtc + volumeInterval;
        }

        // 3. Refresh process snapshot on its own cadence (default 30s)
        if (nowUtc >= _nextProcessCaptureUtc)
        {
            var snapshot = _collector.CaptureProcessSnapshot();
            _runtimeState.UpdateProcessSnapshot(snapshot);
            _pendingProcessSnapshot = snapshot;
            _nextProcessCaptureUtc = nowUtc + processInterval;
        }

        // 4. Check device enrollment state before uploading
        var state = await _store.GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        var enrollmentState = HeartbeatWorker.ParseEnrollmentState(state?.EnrollmentState);

        if (enrollmentState is EnrollmentState.Revoked or EnrollmentState.Decommissioned)
        {
            return true;
        }

        if (state is null || enrollmentState != EnrollmentState.Active)
        {
            return false;
        }

        if (_nextUploadUtc == DateTimeOffset.MinValue)
        {
            _nextUploadUtc = nowUtc + uploadInterval;
            return false;
        }

        if (nowUtc < _nextUploadUtc || _pendingSamples.Count == 0)
        {
            return false;
        }

        return await UploadAndDrainAsync(state, nowUtc, uploadInterval, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> UploadAndDrainAsync(
        AgentDeviceState state,
        DateTimeOffset nowUtc,
        TimeSpan uploadInterval,
        CancellationToken cancellationToken)
    {
        var batch = new MetricsBatchRequest(
            _pendingSamples.ToArray(),
            _pendingVolumes,
            _pendingProcessSnapshot);

        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl) ||
            !Uri.TryCreate(_options.ApiBaseUrl, UriKind.Absolute, out var baseUri))
        {
            await QueueBatchAndBackoffAsync(batch, nowUtc, uploadInterval, "ApiBaseUrl is not configured.", null, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var endpoint = new Uri(baseUri, $"{ProtocolConstants.ApiPrefix}/agent/metrics");
        var httpClient = _httpClientFactory.GetOrCreateClient(state.CertificateThumbprint);

        try
        {
            var authOutcome = await PostBatchAsync(httpClient, endpoint, batch, cancellationToken).ConfigureAwait(false);
            if (authOutcome == BatchPostOutcome.UnauthorizedOrForbidden)
            {
                await HandleRevocationAsync(nowUtc, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (authOutcome != BatchPostOutcome.Success)
            {
                await QueueBatchAndBackoffAsync(batch, nowUtc, uploadInterval, "Server returned non-success status.", null, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            // Upload succeeded: clear in-memory pending data and reset backoff.
            ClearPendingInMemory();
            _consecutiveUploadFailures = 0;
            _nextUploadUtc = nowUtc + uploadInterval;

            // Drain any previously queued offline telemetry batches.
            var drainHalted = await DrainOfflineBatchesAsync(httpClient, endpoint, nowUtc, uploadInterval, cancellationToken)
                .ConfigureAwait(false);
            return drainHalted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await QueueBatchAndBackoffAsync(batch, nowUtc, uploadInterval, ex.Message, ex, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> DrainOfflineBatchesAsync(
        HttpClient httpClient,
        Uri endpoint,
        DateTimeOffset nowUtc,
        TimeSpan uploadInterval,
        CancellationToken cancellationToken)
    {
        var pending = await _store.GetPendingOutboundAsync(MaxDrainPerCycle, cancellationToken).ConfigureAwait(false);
        var telemetryItems = pending
            .Where(item => string.Equals(item.ItemType, TelemetryBatchItemType, StringComparison.Ordinal))
            .ToArray();

        if (telemetryItems.Length == 0)
        {
            return false;
        }

        var succeededIds = new List<long>(telemetryItems.Length);

        foreach (var item in telemetryItems)
        {
            MetricsBatchRequest? queuedBatch;
            try
            {
                queuedBatch = JsonSerializer.Deserialize<MetricsBatchRequest>(
                    item.PayloadJson,
                    NamedPipeFraming.SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Discarding corrupt queued telemetry batch {QueueId}.", item.Id);
                succeededIds.Add(item.Id);
                continue;
            }

            if (queuedBatch is null)
            {
                succeededIds.Add(item.Id);
                continue;
            }

            try
            {
                var outcome = await PostBatchAsync(httpClient, endpoint, queuedBatch, cancellationToken).ConfigureAwait(false);
                if (outcome == BatchPostOutcome.UnauthorizedOrForbidden)
                {
                    if (succeededIds.Count > 0)
                    {
                        await _store.RemoveOutboundBatchAsync(succeededIds, cancellationToken).ConfigureAwait(false);
                    }

                    await HandleRevocationAsync(nowUtc, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                if (outcome != BatchPostOutcome.Success)
                {
                    _consecutiveUploadFailures++;
                    _nextUploadUtc = nowUtc + HeartbeatWorker.ComputeJitteredBackoff(_consecutiveUploadFailures, uploadInterval);
                    break;
                }

                succeededIds.Add(item.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _consecutiveUploadFailures++;
                var backoff = HeartbeatWorker.ComputeJitteredBackoff(_consecutiveUploadFailures, uploadInterval);
                _nextUploadUtc = nowUtc + backoff;
                _logger.LogWarning(ex, "Offline telemetry drain paused; backing off for {BackoffSeconds:F1}s.", backoff.TotalSeconds);
                break;
            }
        }

        if (succeededIds.Count > 0)
        {
            await _store.RemoveOutboundBatchAsync(succeededIds, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<BatchPostOutcome> PostBatchAsync(
        HttpClient httpClient,
        Uri endpoint,
        MetricsBatchRequest batch,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(batch, options: NamedPipeFraming.SerializerOptions)
        };
        message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return BatchPostOutcome.UnauthorizedOrForbidden;
        }

        return response.IsSuccessStatusCode ? BatchPostOutcome.Success : BatchPostOutcome.TransientError;
    }

    private async Task QueueBatchAndBackoffAsync(
        MetricsBatchRequest batch,
        DateTimeOffset nowUtc,
        TimeSpan uploadInterval,
        string reason,
        Exception? ex,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(batch, NamedPipeFraming.SerializerOptions);
        await _store.EnqueueOutboundAsync(
            TelemetryBatchItemType,
            payloadJson,
            isCritical: false,
            coalesceKey: null,
            createdAtUtc: nowUtc,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        ClearPendingInMemory();
        _consecutiveUploadFailures++;
        var backoff = HeartbeatWorker.ComputeJitteredBackoff(_consecutiveUploadFailures, uploadInterval);
        _nextUploadUtc = nowUtc + backoff;

        if (ex is not null)
        {
            _logger.LogWarning(
                ex,
                "Telemetry upload failed ({Reason}); queued offline batch and backing off for {BackoffSeconds:F1}s.",
                reason,
                backoff.TotalSeconds);
        }
        else
        {
            _logger.LogWarning(
                "Telemetry upload failed ({Reason}); queued offline batch and backing off for {BackoffSeconds:F1}s.",
                reason,
                backoff.TotalSeconds);
        }
    }

    private async Task HandleRevocationAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Telemetry upload rejected with HTTP 401/403; transitioning local enrollment state to Revoked and halting.");
        await _store.UpdateEnrollmentStateAsync(
            EnrollmentState.Revoked.ToString(),
            nowUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private void ClearPendingInMemory()
    {
        _pendingSamples.Clear();
        _pendingVolumes = null;
        _pendingProcessSnapshot = null;
    }

    private enum BatchPostOutcome
    {
        Success,
        TransientError,
        UnauthorizedOrForbidden
    }
}
