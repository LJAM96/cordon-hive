using Hive.Infrastructure.Repositories;
using Hive.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Hive.Server.Services;

/// <summary>
/// Polls pending transactional outbox records and dispatches StateChanged envelopes to
/// authorized SignalR user and site groups.
/// </summary>
public sealed class OutboxDispatcherService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IOutboxRepository _outboxRepository;
    private readonly IHubContext<StateHub> _hubContext;
    private readonly ILogger<OutboxDispatcherService> _logger;

    public OutboxDispatcherService(
        IOutboxRepository outboxRepository,
        IHubContext<StateHub> hubContext,
        ILogger<OutboxDispatcherService> logger)
    {
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching pending outbox events.");
            }

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
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var pending = await _outboxRepository.GetPendingAsync(100, cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var dispatched = 0;
        foreach (var record in pending)
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "site:*"
            };

            if (!string.IsNullOrWhiteSpace(record.SiteId))
            {
                groups.Add($"site:{record.SiteId}");
            }

            if (!string.IsNullOrWhiteSpace(record.TargetUserSid))
            {
                groups.Add($"user:{record.TargetUserSid}");
            }

            var envelope = record.ToEnvelope();
            await _hubContext.Clients
                .Groups(groups.ToArray())
                .SendAsync("StateChanged", envelope, cancellationToken);

            await _outboxRepository.MarkDispatchedAsync(record.EventId, DateTimeOffset.UtcNow, cancellationToken);
            dispatched++;
        }

        return dispatched;
    }
}
