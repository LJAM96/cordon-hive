using System.Net;
using System.Net.Http.Json;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Ipc;
using Hive.Windows.SystemInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Agent;

public sealed class HeartbeatWorker : BackgroundService
{
    private static readonly string[] Capabilities = ["p1-heartbeat", "p1-ipc", "p2-telemetry"];
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(120);

    private readonly IAgentLocalStore _store;
    private readonly WindowsMachineInfoProvider _machineInfoProvider;
    private readonly AgentRuntimeState _runtimeState;
    private readonly AgentHttpClientFactory _httpClientFactory;
    private readonly AgentOptions _options;
    private readonly ILogger<HeartbeatWorker> _logger;
    private int _consecutiveFailures;

    public HeartbeatWorker(
        IAgentLocalStore store,
        WindowsMachineInfoProvider machineInfoProvider,
        AgentRuntimeState runtimeState,
        AgentHttpClientFactory httpClientFactory,
        IOptions<AgentOptions> options,
        ILogger<HeartbeatWorker> logger)
    {
        _store = store;
        _machineInfoProvider = machineInfoProvider;
        _runtimeState = runtimeState;
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
                _logger.LogWarning("HeartbeatWorker halting because device enrollment is {State}.", outcome.EnrollmentState);
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

    public async Task<HeartbeatCycleOutcome> ExecuteSingleCycleAsync(CancellationToken cancellationToken)
    {
        var baseInterval = TimeSpan.FromSeconds(Math.Clamp(_options.HeartbeatIntervalSeconds, 5, 300));
        var state = await _store.GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        var enrollmentState = ParseEnrollmentState(state?.EnrollmentState);

        if (enrollmentState is EnrollmentState.Revoked or EnrollmentState.Decommissioned)
        {
            return new HeartbeatCycleOutcome(HaltWorker: true, enrollmentState, TimeSpan.Zero);
        }

        if (state is null || enrollmentState != EnrollmentState.Active)
        {
            return new HeartbeatCycleOutcome(HaltWorker: false, enrollmentState, baseInterval);
        }

        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl) ||
            !Uri.TryCreate(_options.ApiBaseUrl, UriKind.Absolute, out var baseUri))
        {
            _consecutiveFailures++;
            return new HeartbeatCycleOutcome(HaltWorker: false, enrollmentState, ComputeJitteredBackoff(_consecutiveFailures, baseInterval));
        }

        var machine = _machineInfoProvider.GetMachineInfo();
        var sessions = machine.ObservedSessions is { Count: > 0 }
            ? machine.ObservedSessions
            : [new ObservedSessionDto(machine.CurrentSessionId, UserSid: null, "Active", machine.CapturedAtUtc)];

        var protectedModeActive = _runtimeState.ProtectedModeActive;
        var request = new HeartbeatRequest(
            machine.BootId,
            _runtimeState.AgentVersion,
            Capabilities,
            sessions,
            machine.Hostname,
            ProtectedModeActive: protectedModeActive,
            ProtectedModeReason: protectedModeActive ? "Local protection lock active" : null);

        var endpoint = new Uri(baseUri, $"{ProtocolConstants.ApiPrefix}/agent/heartbeat");
        var httpClient = _httpClientFactory.GetOrCreateClient(state.CertificateThumbprint);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request, options: NamedPipeFraming.SerializerOptions)
            };
            message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));

            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Heartbeat rejected with HTTP {StatusCode}; transitioning local enrollment state to Revoked and halting.",
                    (int)response.StatusCode);

                await _store.UpdateEnrollmentStateAsync(
                    EnrollmentState.Revoked.ToString(),
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                return new HeartbeatCycleOutcome(HaltWorker: true, EnrollmentState.Revoked, TimeSpan.Zero);
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content
                .ReadFromJsonAsync<HeartbeatResponse>(NamedPipeFraming.SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            var nowUtc = DateTimeOffset.UtcNow;
            _runtimeState.RecordHeartbeatSuccess(nowUtc);
            _consecutiveFailures = 0;

            var newPolicyRevision = body?.PolicyRevision ?? state.PolicyRevision;
            if (newPolicyRevision != state.PolicyRevision || state.LastHeartbeatUtc is null || (nowUtc - state.LastHeartbeatUtc.Value) > TimeSpan.FromMinutes(1))
            {
                await _store.SaveDeviceStateAsync(
                    state with
                    {
                        PolicyRevision = newPolicyRevision,
                        LastHeartbeatUtc = nowUtc,
                        UpdatedAtUtc = nowUtc
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var nextInterval = body is not null && body.NextHeartbeatSeconds > 0
                ? TimeSpan.FromSeconds(Math.Clamp(body.NextHeartbeatSeconds, 5, 300))
                : baseInterval;

            return new HeartbeatCycleOutcome(HaltWorker: false, enrollmentState, nextInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            var backoff = ComputeJitteredBackoff(_consecutiveFailures, baseInterval);
            _logger.LogWarning(
                ex,
                "Heartbeat attempt failed (consecutive failures: {Failures}). Backing off for {BackoffSeconds:F1}s.",
                _consecutiveFailures,
                backoff.TotalSeconds);

            return new HeartbeatCycleOutcome(HaltWorker: false, enrollmentState, backoff);
        }
    }

    internal static EnrollmentState ParseEnrollmentState(string? rawState) =>
        Enum.TryParse<EnrollmentState>(rawState, ignoreCase: true, out var parsed)
            ? parsed
            : EnrollmentState.Pending;

    internal static TimeSpan ComputeJitteredBackoff(int consecutiveFailures, TimeSpan baseInterval)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 5);
        var rawSeconds = Math.Min(MaxBackoff.TotalSeconds, baseInterval.TotalSeconds * Math.Pow(2, exponent));
        var jitterFactor = 0.8 + (Random.Shared.NextDouble() * 0.4); // +/- 20%
        return TimeSpan.FromSeconds(Math.Clamp(rawSeconds * jitterFactor, 1.0, MaxBackoff.TotalSeconds));
    }
}

public sealed record HeartbeatCycleOutcome(
    bool HaltWorker,
    EnrollmentState EnrollmentState,
    TimeSpan NextDelay);
