using Hive.Contracts;
using Hive.Infrastructure.Repositories;
using Microsoft.Extensions.Options;

namespace Hive.Server.Services;

public sealed class TelemetryRetentionOptions
{
    public const string SectionName = "TelemetryRetention";

    public int MetricsRetentionDays { get; set; } = 7;

    public int ProcessSnapshotRetentionHours { get; set; } = 24;

    public int IntervalMinutes { get; set; } = 60;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Periodically prunes expired telemetry metric samples and process snapshots according to retention policy (V24).
/// </summary>
public sealed class TelemetryRetentionHostedService : BackgroundService
{
    private readonly ITelemetryRepository _telemetryRepository;
    private readonly TelemetryRetentionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelemetryRetentionHostedService> _logger;
    private readonly object _syncRoot = new();
    private DateTimeOffset? _lastPruneUtc;

    public TelemetryRetentionHostedService(
        ITelemetryRepository telemetryRepository,
        IOptions<TelemetryRetentionOptions> options,
        TimeProvider timeProvider,
        ILogger<TelemetryRetentionHostedService> logger)
    {
        _telemetryRepository = telemetryRepository ?? throw new ArgumentNullException(nameof(telemetryRepository));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DateTimeOffset? LastPruneUtc
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastPruneUtc;
            }
        }
    }

    public async Task<TelemetryRetentionPruneResultDto> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var metricsCutoff = nowUtc.AddDays(-Math.Max(0, _options.MetricsRetentionDays));
        var processSnapshotsCutoff = nowUtc.AddHours(-Math.Max(0, _options.ProcessSnapshotRetentionHours));

        var (deletedSamples, deletedSnapshots) = await _telemetryRepository.PruneExpiredAsync(
            metricsCutoff,
            processSnapshotsCutoff,
            cancellationToken);

        lock (_syncRoot)
        {
            _lastPruneUtc = nowUtc;
        }

        return new TelemetryRetentionPruneResultDto(deletedSamples, deletedSnapshots, nowUtc);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pruning expired telemetry data.");
            }
        }
    }
}
