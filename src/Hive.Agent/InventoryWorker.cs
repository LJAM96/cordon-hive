using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Inventory;
using Hive.Windows.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Agent;

/// <summary>
/// Periodically collects local software inventory using <see cref="ISoftwareInventoryCollector"/>
/// and uploads snapshots to <c>PUT /api/v1/agent/inventory</c>, coalescing offline snapshots
/// in <see cref="IAgentLocalStore"/> when disconnected.
/// </summary>
public sealed class InventoryWorker : BackgroundService
{
    public const string SoftwareInventoryItemType = "software_inventory";
    public const string SoftwareInventoryCoalesceKey = "software_inventory";

    private readonly IAgentLocalStore _store;
    private readonly ISoftwareInventoryCollector _collector;
    private readonly AgentHttpClientFactory _httpClientFactory;
    private readonly AgentOptions _options;
    private readonly ILogger<InventoryWorker> _logger;
    private int _consecutiveFailures;

    public InventoryWorker(
        IAgentLocalStore store,
        ISoftwareInventoryCollector collector,
        AgentHttpClientFactory httpClientFactory,
        IOptions<AgentOptions> options,
        ILogger<InventoryWorker> logger)
    {
        _store = store;
        _collector = collector;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await ExecuteSingleCycleAsync(stoppingToken).ConfigureAwait(false);
            if (outcome.HaltWorker)
            {
                _logger.LogWarning("InventoryWorker halting because device enrollment is {State}.", outcome.EnrollmentState);
                break;
            }

            try
            {
                await Task.Delay(outcome.NextDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<InventoryCycleOutcome> ExecuteSingleCycleAsync(CancellationToken cancellationToken)
    {
        var baseInterval = TimeSpan.FromMinutes(Math.Clamp(_options.InventoryIntervalMinutes, 1, 1440));
        var pendingPollInterval = TimeSpan.FromSeconds(Math.Clamp(_options.HeartbeatIntervalSeconds, 5, 60));

        var state = await _store.GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        var enrollmentState = HeartbeatWorker.ParseEnrollmentState(state?.EnrollmentState);

        if (enrollmentState is EnrollmentState.Revoked or EnrollmentState.Decommissioned)
        {
            return new InventoryCycleOutcome(HaltWorker: true, enrollmentState, TimeSpan.Zero);
        }

        if (state is null || enrollmentState != EnrollmentState.Active)
        {
            return new InventoryCycleOutcome(HaltWorker: false, enrollmentState, pendingPollInterval);
        }

        var inventoryOptions = new SoftwareInventoryOptions(
            _options.IncludeCurrentUserRegistry,
            _options.PortableApps);

        var snapshot = await _collector
            .CollectAsync(state.PolicyRevision, inventoryOptions, cancellationToken)
            .ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl) ||
            !Uri.TryCreate(_options.ApiBaseUrl, UriKind.Absolute, out var baseUri))
        {
            var delay = await QueueSnapshotAndBackoffAsync(
                snapshot,
                nowUtc,
                pendingPollInterval,
                "ApiBaseUrl is not configured.",
                ex: null,
                cancellationToken).ConfigureAwait(false);
            return new InventoryCycleOutcome(HaltWorker: false, enrollmentState, delay);
        }

        var endpoint = new Uri(baseUri, $"{ProtocolConstants.ApiPrefix}/agent/inventory");
        var httpClient = _httpClientFactory.GetOrCreateClient(state.CertificateThumbprint);

        // Drain any previously queued offline inventory snapshot before uploading the latest snapshot.
        var drainResult = await DrainQueuedInventoryAsync(httpClient, endpoint, nowUtc, pendingPollInterval, cancellationToken)
            .ConfigureAwait(false);
        if (drainResult.Halted)
        {
            return new InventoryCycleOutcome(HaltWorker: true, EnrollmentState.Revoked, TimeSpan.Zero);
        }

        if (!drainResult.Succeeded)
        {
            var delay = await QueueSnapshotAndBackoffAsync(
                snapshot,
                nowUtc,
                pendingPollInterval,
                drainResult.FailureReason ?? "Queued inventory drain failed.",
                drainResult.Exception,
                cancellationToken).ConfigureAwait(false);
            return new InventoryCycleOutcome(HaltWorker: false, enrollmentState, delay);
        }

        try
        {
            var outcome = await PutSnapshotAsync(httpClient, endpoint, snapshot, cancellationToken).ConfigureAwait(false);
            if (outcome == InventoryPutOutcome.UnauthorizedOrForbidden)
            {
                await HandleRevocationAsync(nowUtc, cancellationToken).ConfigureAwait(false);
                return new InventoryCycleOutcome(HaltWorker: true, EnrollmentState.Revoked, TimeSpan.Zero);
            }

            if (outcome != InventoryPutOutcome.Success)
            {
                var delay = await QueueSnapshotAndBackoffAsync(
                    snapshot,
                    nowUtc,
                    pendingPollInterval,
                    "Server returned non-success status.",
                    ex: null,
                    cancellationToken).ConfigureAwait(false);
                return new InventoryCycleOutcome(HaltWorker: false, enrollmentState, delay);
            }

            _consecutiveFailures = 0;
            return new InventoryCycleOutcome(HaltWorker: false, enrollmentState, baseInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var delay = await QueueSnapshotAndBackoffAsync(
                snapshot,
                nowUtc,
                pendingPollInterval,
                ex.Message,
                ex,
                cancellationToken).ConfigureAwait(false);
            return new InventoryCycleOutcome(HaltWorker: false, enrollmentState, delay);
        }
    }

    private async Task<DrainResult> DrainQueuedInventoryAsync(
        HttpClient httpClient,
        Uri endpoint,
        DateTimeOffset nowUtc,
        TimeSpan retryBaseInterval,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(_store.MaxQueueItems, 1, 1000);
        var pending = await _store.GetPendingOutboundAsync(limit, cancellationToken).ConfigureAwait(false);
        var queuedItems = pending
            .Where(item => string.Equals(item.ItemType, SoftwareInventoryItemType, StringComparison.Ordinal))
            .ToArray();

        if (queuedItems.Length == 0)
        {
            return DrainResult.Success();
        }

        var completedIds = new List<long>(queuedItems.Length);

        foreach (var item in queuedItems)
        {
            InventorySnapshotRequest? queuedSnapshot;
            try
            {
                queuedSnapshot = JsonSerializer.Deserialize<InventorySnapshotRequest>(
                    item.PayloadJson,
                    NamedPipeFraming.SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Discarding corrupt queued software inventory snapshot {QueueId}.", item.Id);
                completedIds.Add(item.Id);
                continue;
            }

            if (queuedSnapshot is null)
            {
                completedIds.Add(item.Id);
                continue;
            }

            try
            {
                var outcome = await PutSnapshotAsync(httpClient, endpoint, queuedSnapshot, cancellationToken).ConfigureAwait(false);
                if (outcome == InventoryPutOutcome.UnauthorizedOrForbidden)
                {
                    if (completedIds.Count > 0)
                    {
                        await _store.RemoveOutboundBatchAsync(completedIds, cancellationToken).ConfigureAwait(false);
                    }

                    await HandleRevocationAsync(nowUtc, cancellationToken).ConfigureAwait(false);
                    return DrainResult.Revoked();
                }

                if (outcome != InventoryPutOutcome.Success)
                {
                    if (completedIds.Count > 0)
                    {
                        await _store.RemoveOutboundBatchAsync(completedIds, cancellationToken).ConfigureAwait(false);
                    }

                    return DrainResult.Failed("Queued software inventory upload returned non-success status.", null);
                }

                completedIds.Add(item.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (completedIds.Count > 0)
                {
                    await _store.RemoveOutboundBatchAsync(completedIds, cancellationToken).ConfigureAwait(false);
                }

                return DrainResult.Failed(ex.Message, ex);
            }
        }

        if (completedIds.Count > 0)
        {
            await _store.RemoveOutboundBatchAsync(completedIds, cancellationToken).ConfigureAwait(false);
        }

        _ = retryBaseInterval;
        return DrainResult.Success();
    }

    private static async Task<InventoryPutOutcome> PutSnapshotAsync(
        HttpClient httpClient,
        Uri endpoint,
        InventorySnapshotRequest snapshot,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, endpoint)
        {
            Content = JsonContent.Create(snapshot, options: NamedPipeFraming.SerializerOptions)
        };
        message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return InventoryPutOutcome.UnauthorizedOrForbidden;
        }

        return response.IsSuccessStatusCode ? InventoryPutOutcome.Success : InventoryPutOutcome.TransientError;
    }

    private async Task<TimeSpan> QueueSnapshotAndBackoffAsync(
        InventorySnapshotRequest snapshot,
        DateTimeOffset nowUtc,
        TimeSpan retryBaseInterval,
        string reason,
        Exception? ex,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(snapshot, NamedPipeFraming.SerializerOptions);
        await _store.EnqueueOutboundAsync(
            SoftwareInventoryItemType,
            payloadJson,
            isCritical: false,
            coalesceKey: SoftwareInventoryCoalesceKey,
            createdAtUtc: nowUtc,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _consecutiveFailures++;
        var backoff = HeartbeatWorker.ComputeJitteredBackoff(_consecutiveFailures, retryBaseInterval);

        if (ex is not null)
        {
            _logger.LogWarning(
                ex,
                "Software inventory upload failed ({Reason}); queued coalesced snapshot and backing off for {BackoffSeconds:F1}s.",
                reason,
                backoff.TotalSeconds);
        }
        else
        {
            _logger.LogWarning(
                "Software inventory upload failed ({Reason}); queued coalesced snapshot and backing off for {BackoffSeconds:F1}s.",
                reason,
                backoff.TotalSeconds);
        }

        return backoff;
    }

    private async Task HandleRevocationAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Software inventory upload rejected with HTTP 401/403; transitioning local enrollment state to Revoked and halting.");
        await _store.UpdateEnrollmentStateAsync(
            EnrollmentState.Revoked.ToString(),
            nowUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private enum InventoryPutOutcome
    {
        Success,
        TransientError,
        UnauthorizedOrForbidden
    }

    private readonly record struct DrainResult(bool Succeeded, bool Halted, string? FailureReason, Exception? Exception)
    {
        public static DrainResult Success() => new(true, false, null, null);
        public static DrainResult Revoked() => new(false, true, null, null);
        public static DrainResult Failed(string reason, Exception? ex) => new(false, false, reason, ex);
    }
}

public sealed record InventoryCycleOutcome(
    bool HaltWorker,
    EnrollmentState EnrollmentState,
    TimeSpan NextDelay);
