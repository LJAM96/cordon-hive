using System.Globalization;
using System.Text;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Identity;
using Hive.Infrastructure.Repositories;
using Hive.Server.Authentication;
using Hive.Server.Middleware;
using Hive.Server.Services;
using Npgsql;

namespace Hive.Server.Endpoints;

public static class OperationsEndpoints
{
    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/ready", GetReadiness)
            .AllowAnonymous();

        var api = endpoints.MapGroup(ProtocolConstants.ApiPrefix);

        api.MapGet("/operations/status", GetOperationalStatus)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/operations/emergency-stop", UpdateEmergencyStop)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/operations/prune-telemetry", PruneTelemetry)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/audit/export", ExportAuditEvents)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        return endpoints;
    }

    private static async Task<IResult> GetReadiness(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var storageProvider = configuration["Storage:Provider"] ?? "InMemory";
        var nowUtc = timeProvider.GetUtcNow();

        if (string.Equals(storageProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dataSource = serviceProvider.GetService<NpgsqlDataSource>();
                if (dataSource is null)
                {
                    return Results.Json(
                        new
                        {
                            status = "Unready",
                            storageProvider,
                            protocolVersion = ProtocolConstants.ProtocolVersion,
                            utc = nowUtc,
                            error = "PostgreSQL data source is not registered."
                        },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                await cmd.ExecuteScalarAsync(cancellationToken);
            }
            catch (Exception)
            {
                return Results.Json(
                    new
                    {
                        status = "Unready",
                        storageProvider,
                        protocolVersion = ProtocolConstants.ProtocolVersion,
                        utc = nowUtc,
                        error = "PostgreSQL readiness check failed."
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Results.Ok(new
        {
            status = "Ready",
            storageProvider,
            protocolVersion = ProtocolConstants.ProtocolVersion,
            utc = nowUtc
        });
    }

    private static async Task<IResult> GetOperationalStatus(
        HttpContext context,
        IConfiguration configuration,
        DeploymentControlState controlState,
        IDeploymentRepository deploymentRepository,
        IOutboxRepository outboxRepository,
        TelemetryRetentionHostedService retentionService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewOperationalHealth(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view operational status.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var storageProvider = configuration["Storage:Provider"] ?? "InMemory";
        var control = controlState.GetSnapshot();
        var activeLeaseCount = await deploymentRepository.CountActiveLeasesAsync(nowUtc, cancellationToken);
        var pendingOutboxCount = await outboxRepository.CountPendingAsync(cancellationToken);

        return Results.Ok(new OperationalStatusDto(
            "Healthy",
            nowUtc,
            ProtocolConstants.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            storageProvider,
            control.DeploymentsPaused,
            control.PauseReason,
            control.PausedAtUtc,
            control.PausedBySid,
            activeLeaseCount,
            pendingOutboxCount,
            retentionService.LastPruneUtc));
    }

    private static async Task<IResult> UpdateEmergencyStop(
        EmergencyStopRequest? request,
        HttpContext context,
        DeploymentControlState controlState,
        IDeploymentRepository deploymentRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanOperateEmergencyStop(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to operate deployment emergency stop.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            return HiveApiEndpoints.ValidationProblem("Reason is required.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var reason = request.Reason.Trim();
        controlState.Update(request.PauseDeployments, reason, actor.UserSid, nowUtc);

        var invalidatedLeaseCount = 0;
        if (request.InvalidateActiveLeases)
        {
            invalidatedLeaseCount = await deploymentRepository.InvalidateAllActiveLeasesAsync(
                "emergency_stop_invalidated",
                nowUtc,
                cancellationToken);
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "EmergencyStopUpdated",
                "Operations",
                "deployment-control",
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    pauseDeployments = request.PauseDeployments,
                    invalidateActiveLeases = request.InvalidateActiveLeases,
                    invalidatedLeaseCount,
                    reason
                })),
            cancellationToken);

        return Results.Ok(new EmergencyStopResponse(
            request.PauseDeployments,
            invalidatedLeaseCount,
            reason,
            nowUtc));
    }

    private static async Task<IResult> PruneTelemetry(
        HttpContext context,
        TelemetryRetentionHostedService retentionService,
        IAuditRepository auditRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanOperateEmergencyStop(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to trigger telemetry retention pruning.");
        }

        var result = await retentionService.RunOnceAsync(cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "TelemetryRetentionPruned",
                "Telemetry",
                "retention",
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                result.PrunedAtUtc,
                JsonSerializer.Serialize(new
                {
                    deletedMetricSamples = result.DeletedMetricSamples,
                    deletedProcessSnapshots = result.DeletedProcessSnapshots
                })),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ExportAuditEvents(
        string? format,
        string? siteId,
        string? action,
        string? actorId,
        string? result,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        HttpContext context,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewAudit(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to export audit events.");
        }

        var trimmedSiteId = string.IsNullOrWhiteSpace(siteId) ? null : siteId.Trim();
        if (trimmedSiteId is not null && !actor.CanAccessSite(trimmedSiteId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to export audit events for the requested site.");
        }

        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
        {
            return HiveApiEndpoints.ValidationProblem("fromUtc must be earlier than or equal to toUtc.");
        }

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "jsonl"))
        {
            return HiveApiEndpoints.ValidationProblem("Export format must be 'csv' or 'jsonl'.");
        }

        var allEvents = new List<AuditEvent>();
        const int pageSize = 200;
        var page = 1;
        while (true)
        {
            var (items, totalCount) = await auditRepository.QueryAsync(
                page,
                pageSize,
                trimmedSiteId,
                actor.AllowedSiteIds,
                actor.AllSitesAccess,
                action,
                actorId,
                result,
                fromUtc,
                toUtc,
                cancellationToken);

            allEvents.AddRange(items);
            if (items.Count == 0 || allEvents.Count >= totalCount || page >= 500)
            {
                break;
            }

            page++;
        }

        var nowUtc = timeProvider.GetUtcNow();
        context.Response.Headers.CacheControl = "no-store";

        if (normalizedFormat == "jsonl")
        {
            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"hive-audit-{nowUtc:yyyyMMddHHmmss}.jsonl\"";

            var sb = new StringBuilder();
            foreach (var e in allEvents)
            {
                var dto = new AuditEventDto(
                    e.EventId,
                    e.ActorType,
                    e.ActorId,
                    e.Action,
                    e.ObjectType,
                    e.ObjectId,
                    e.Result,
                    e.SiteId,
                    e.CorrelationId,
                    e.OccurredAtUtc,
                    e.SummaryJson);
                sb.AppendLine(JsonSerializer.Serialize(dto, JsonLineOptions));
            }

            return Results.Bytes(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-ndjson; charset=utf-8");
        }

        context.Response.Headers.ContentDisposition =
            $"attachment; filename=\"hive-audit-{nowUtc:yyyyMMddHHmmss}.csv\"";

        var csv = new StringBuilder();
        csv.AppendLine("EventId,OccurredAtUtc,ActorType,ActorId,Action,ObjectType,ObjectId,Result,SiteId,CorrelationId,SummaryJson");
        foreach (var e in allEvents)
        {
            csv.Append(EscapeCsvCell(e.EventId.ToString("D"))).Append(',')
               .Append(EscapeCsvCell(e.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
               .Append(EscapeCsvCell(e.ActorType)).Append(',')
               .Append(EscapeCsvCell(e.ActorId)).Append(',')
               .Append(EscapeCsvCell(e.Action)).Append(',')
               .Append(EscapeCsvCell(e.ObjectType)).Append(',')
               .Append(EscapeCsvCell(e.ObjectId)).Append(',')
               .Append(EscapeCsvCell(e.Result)).Append(',')
               .Append(EscapeCsvCell(e.SiteId)).Append(',')
               .Append(EscapeCsvCell(e.CorrelationId)).Append(',')
               .Append(EscapeCsvCell(e.SummaryJson)).AppendLine();
        }

        return Results.Bytes(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8");
    }

    private static string EscapeCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safe = value;
        var trimmedStart = safe.TrimStart();
        if (safe[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ||
            (trimmedStart.Length > 0 && trimmedStart[0] is '=' or '+' or '-' or '@'))
        {
            safe = "'" + safe;
        }

        if (safe.IndexOfAny([',', '"', '\r', '\n']) >= 0 || safe[0] == '\'')
        {
            return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return safe;
    }
}
