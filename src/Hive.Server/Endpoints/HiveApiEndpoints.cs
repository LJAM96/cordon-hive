using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Devices;
using Hive.Domain.Enrollment;
using Hive.Domain.Identity;
using Hive.Domain.Telemetry;
using Hive.Infrastructure.Repositories;
using Hive.Server.Authentication;
using Hive.Server.Middleware;

namespace Hive.Server.Endpoints;

public static class HiveApiEndpoints
{
    public static IEndpointRouteBuilder MapHiveEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", (TimeProvider timeProvider) =>
                Results.Ok(new
                {
                    status = "Healthy",
                    utc = timeProvider.GetUtcNow()
                }))
            .AllowAnonymous();

        var api = endpoints.MapGroup(ProtocolConstants.ApiPrefix);

        api.MapGet("/me", GetMe)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/enrollment-tokens", CreateEnrollmentToken)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/devices/enroll", EnrollDevice)
            .AllowAnonymous();

        api.MapPost("/agent/heartbeat", RecordHeartbeat)
            .RequireAuthorization(HiveAuthPolicies.DeviceOnly);

        api.MapPost("/agent/metrics", IngestMetricsBatch)
            .RequireAuthorization(HiveAuthPolicies.DeviceOnly);

        api.MapGet("/me/workstations", GetMyWorkstations)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/workstations", GetWorkstations)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/workstations/{id:guid}", GetWorkstationById)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/workstations/{id:guid}/telemetry", GetWorkstationTelemetry)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPatch("/workstations/{id:guid}/assignment", UpdateWorkstationAssignment)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/workstations/{id:guid}/revoke", RevokeWorkstation)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/audit", GetAuditEvents)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapAuthAndUserEndpoints();
        api.MapSoftwareEndpoints();
        api.MapNotificationEndpoints();
        api.MapRemoteAccessEndpoints();
        api.MapDeploymentEndpoints();

        return endpoints;
    }

    private static IResult GetMe(HttpContext context)
    {
        var actor = context.User.ToActorIdentity();
        var response = new MeResponse(
            actor.UserSid,
            actor.DisplayName,
            actor.Roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            actor.AllowedSiteIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            actor.AllSitesAccess);

        return Results.Ok(response);
    }

    private static async Task<IResult> CreateEnrollmentToken(
        CreateEnrollmentTokenRequest request,
        HttpContext context,
        IEnrollmentTokenRepository tokenRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SiteId))
        {
            return ValidationProblem("SiteId is required.");
        }

        if (request.TtlMinutes is < 1 or > 1440)
        {
            return ValidationProblem("TtlMinutes must be between 1 and 1440.");
        }

        var actor = context.User.ToActorIdentity();
        var siteId = request.SiteId.Trim();
        if (!ScopeAuthorizationService.CanCreateEnrollmentToken(actor, siteId))
        {
            return ForbiddenProblem("Caller does not have Administrator permissions for the target site.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var rawToken = EnrollmentTokenHasher.GenerateRawToken();
        var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(rawToken);
        var token = new EnrollmentToken(
            Guid.NewGuid(),
            tokenHash,
            siteId,
            actor.UserSid,
            nowUtc,
            nowUtc.AddMinutes(request.TtlMinutes),
            null,
            null);

        await tokenRepository.CreateAsync(token, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "EnrollmentTokenCreated",
                "EnrollmentToken",
                token.TokenId.ToString("D"),
                "Success",
                token.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new { ttlMinutes = request.TtlMinutes })),
            cancellationToken);

        var response = new CreateEnrollmentTokenResponse(
            token.TokenId,
            rawToken,
            token.SiteId,
            token.ExpiresAtUtc);

        return Results.Created($"{ProtocolConstants.ApiPrefix}/enrollment-tokens/{token.TokenId:D}", response);
    }

    private static async Task<IResult> EnrollDevice(
        EnrollDeviceRequest request,
        HttpContext context,
        IEnrollmentTokenRepository tokenRepository,
        IAuditRepository auditRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) ||
            string.IsNullOrWhiteSpace(request.Hostname) ||
            string.IsNullOrWhiteSpace(request.CertificateThumbprint) ||
            string.IsNullOrWhiteSpace(request.AgentVersion))
        {
            return ValidationProblem("Token, Hostname, CertificateThumbprint, and AgentVersion are required.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(request.Token);
        var result = await tokenRepository.RedeemAndEnrollAsync(
            tokenHash,
            request.Hostname,
            request.CertificateThumbprint,
            request.AssetNumber,
            request.AgentVersion,
            nowUtc,
            cancellationToken);

        if (result.Status != EnrollmentStatus.Success || result.Device is null)
        {
            return result.Status switch
            {
                EnrollmentStatus.InvalidOrUnknownToken => Problem(StatusCodes.Status401Unauthorized, "Invalid enrollment token", result.ErrorMessage),
                EnrollmentStatus.TokenExpired => Problem(StatusCodes.Status422UnprocessableEntity, "Enrollment token expired", result.ErrorMessage),
                EnrollmentStatus.TokenAlreadyRedeemed => Problem(StatusCodes.Status422UnprocessableEntity, "Enrollment token already redeemed", result.ErrorMessage),
                EnrollmentStatus.DuplicateAssetNumber => Problem(StatusCodes.Status409Conflict, "Duplicate asset number", result.ErrorMessage),
                EnrollmentStatus.DuplicateCertificateThumbprint => Problem(StatusCodes.Status409Conflict, "Duplicate certificate thumbprint", result.ErrorMessage),
                _ => Problem(StatusCodes.Status400BadRequest, "Enrollment failed", result.ErrorMessage)
            };
        }

        var device = result.Device;
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "Device",
                device.DeviceId.ToString("D"),
                "DeviceEnrolled",
                "Device",
                device.DeviceId.ToString("D"),
                "Success",
                device.SiteId,
                correlationId,
                nowUtc,
                JsonSerializer.Serialize(new { hostname = device.Hostname, assetNumber = device.AssetNumber })),
            cancellationToken);

        await outboxRepository.EnqueueAsync(
            new OutboxEventRecord(
                Guid.NewGuid(),
                "DeviceEnrolled",
                device.DeviceId,
                device.RowVersion,
                device.SiteId,
                null,
                nowUtc),
            cancellationToken);

        var response = new EnrollDeviceResponse(
            device.DeviceId,
            device.SiteId,
            device.PolicyRevision,
            device.EnrolledAtUtc);

        return Results.Created($"{ProtocolConstants.ApiPrefix}/workstations/{device.DeviceId:D}", response);
    }

    private static async Task<IResult> RecordHeartbeat(
        HeartbeatRequest request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!actor.IsDevice || actor.DeviceId is not Guid deviceId || !ScopeAuthorizationService.CanSendHeartbeat(actor, deviceId))
        {
            return ForbiddenProblem("Valid device certificate authentication is required.");
        }

        var existing = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (existing is null || existing.EnrollmentState != EnrollmentState.Active)
        {
            return ForbiddenProblem("Device is not active.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var sessions = (request.Sessions ?? Array.Empty<ObservedSessionDto>())
            .Select(s => new DeviceSession(
                deviceId,
                request.BootId,
                s.SessionId,
                s.UserSid,
                s.SessionState,
                s.ObservedAtUtc))
            .ToArray();

        var updated = await deviceRepository.RecordHeartbeatAsync(
            deviceId,
            request.BootId,
            request.AgentVersion,
            request.Hostname,
            sessions,
            nowUtc,
            cancellationToken,
            request.ProtectedModeActive,
            request.ProtectedModeReason);

        if (updated is null)
        {
            return ForbiddenProblem("Device is not active.");
        }

        await outboxRepository.EnqueueAsync(
            new OutboxEventRecord(
                Guid.NewGuid(),
                "DeviceHeartbeat",
                updated.DeviceId,
                updated.RowVersion,
                updated.SiteId,
                updated.AssignedUserSid,
                nowUtc),
            cancellationToken);

        return Results.Ok(new HeartbeatResponse(
            updated.PolicyRevision,
            nowUtc,
            ProtocolConstants.DefaultHeartbeatSeconds));
    }

    private static async Task<IResult> IngestMetricsBatch(
        MetricsBatchRequest request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ITelemetryRepository telemetryRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!actor.IsDevice || actor.DeviceId is not Guid deviceId || !ScopeAuthorizationService.CanSendHeartbeat(actor, deviceId))
        {
            return ForbiddenProblem("Valid device certificate authentication is required.");
        }

        var existing = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (existing is null || existing.EnrollmentState != EnrollmentState.Active)
        {
            return ForbiddenProblem("Device is not active.");
        }

        if (request.Samples is null || request.Samples.Count == 0)
        {
            return ValidationProblem("At least one metric sample is required.");
        }

        if (request.Samples.Count > 200)
        {
            return ValidationProblem("A metrics batch cannot exceed 200 samples.");
        }

        if (request.Samples.Any(s => s is null))
        {
            return ValidationProblem("Samples must not contain null entries.");
        }

        if (request.Volumes is not null && request.Volumes.Any(v => v is null))
        {
            return ValidationProblem("Volumes must not contain null entries.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var sanitizedSamples = request.Samples
            .Select(s => TelemetrySanitizer.SanitizeSample(deviceId, s, nowUtc))
            .ToArray();

        var sanitizedVolumes = request.Volumes?
            .Select(v => TelemetrySanitizer.SanitizeVolume(deviceId, v))
            .ToArray();

        var sanitizedProcessSnapshot = request.ProcessSnapshot is null
            ? null
            : TelemetrySanitizer.SanitizeProcessSnapshot(deviceId, request.ProcessSnapshot);

        var result = await telemetryRepository.IngestBatchAsync(
            deviceId,
            sanitizedSamples,
            sanitizedVolumes,
            sanitizedProcessSnapshot,
            cancellationToken);

        await outboxRepository.EnqueueAsync(
            new OutboxEventRecord(
                Guid.NewGuid(),
                "DeviceTelemetryUpdated",
                existing.DeviceId,
                existing.RowVersion,
                existing.SiteId,
                existing.AssignedUserSid,
                nowUtc),
            cancellationToken);

        return Results.Ok(new MetricsBatchResponse(
            result.AcceptedSamples,
            result.DuplicateSamples,
            nowUtc));
    }

    private static async Task<IResult> GetMyWorkstations(
        HttpContext context,
        IDeviceRepository deviceRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        var devices = await deviceRepository.ListAsync(
            assignedUserSid: actor.UserSid,
            allSites: true,
            cancellationToken: cancellationToken);

        var nowUtc = timeProvider.GetUtcNow();
        return Results.Ok(devices.Select(d => ToSummaryDto(d, nowUtc)).ToArray());
    }

    private static async Task<IResult> GetWorkstations(
        string? siteId,
        HttpContext context,
        IDeviceRepository deviceRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewAllWorkstations(actor))
        {
            return ForbiddenProblem("Caller role is not authorized to list workstations.");
        }

        if (!string.IsNullOrWhiteSpace(siteId) && !actor.CanAccessSite(siteId.Trim()))
        {
            return ForbiddenProblem("Caller is not authorized for the requested site.");
        }

        var devices = await deviceRepository.ListAsync(
            siteId: string.IsNullOrWhiteSpace(siteId) ? null : siteId.Trim(),
            allowedSiteIds: actor.AllowedSiteIds,
            allSites: actor.AllSitesAccess,
            cancellationToken: cancellationToken);

        var nowUtc = timeProvider.GetUtcNow();
        return Results.Ok(devices.Select(d => ToSummaryDto(d, nowUtc)).ToArray());
    }

    private static async Task<IResult> GetWorkstationById(
        Guid id,
        HttpContext context,
        IDeviceRepository deviceRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Workstation not found", $"Workstation '{id}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewWorkstation(actor, device.SiteId, device.AssignedUserSid))
        {
            return ForbiddenProblem("Caller is not authorized to view this workstation.");
        }

        var sessions = await deviceRepository.GetSessionsAsync(id, cancellationToken);
        var history = await deviceRepository.GetAssignmentHistoryAsync(id, cancellationToken);
        return Results.Ok(ToDetailDto(device, sessions, history, timeProvider.GetUtcNow()));
    }

    private static async Task<IResult> GetWorkstationTelemetry(
        Guid id,
        int? sampleLimit,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ITelemetryRepository telemetryRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Workstation not found", $"Workstation '{id}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewWorkstation(actor, device.SiteId, device.AssignedUserSid))
        {
            return ForbiddenProblem("Caller is not authorized to view this workstation.");
        }

        var safeLimit = Math.Clamp(sampleLimit ?? 60, 1, 500);
        var snapshot = await telemetryRepository.GetLatestTelemetryAsync(id, safeLimit, cancellationToken);
        var nowUtc = timeProvider.GetUtcNow();

        var recentSamples = snapshot.Samples
            .Select(ToMetricSampleDto)
            .ToArray();

        var volumes = snapshot.Volumes
            .Select(v => new VolumeSnapshotDto(
                v.VolumeId,
                v.DriveType,
                v.TotalBytes,
                v.FreeBytes,
                v.ObservedAtUtc))
            .ToArray();

        ProcessSnapshotDto? latestProcessSnapshot = snapshot.LatestProcessSnapshot is null
            ? null
            : new ProcessSnapshotDto(
                snapshot.LatestProcessSnapshot.SnapshotId,
                snapshot.LatestProcessSnapshot.SampledAtUtc,
                snapshot.LatestProcessSnapshot.Processes
                    .Select(p => new ProcessEntryDto(
                        p.ImageName,
                        p.Pid,
                        p.StartTimeUtc,
                        p.CpuPercent,
                        p.WorkingSetBytes))
                    .ToArray());

        var response = new DeviceTelemetryResponse(
            device.DeviceId,
            DeviceConnectivityEvaluator.Evaluate(device.LastSeenUtc, nowUtc).ToString(),
            device.LastSeenUtc,
            recentSamples.FirstOrDefault(),
            recentSamples,
            volumes,
            latestProcessSnapshot);

        return Results.Ok(response);
    }

    private static async Task<IResult> UpdateWorkstationAssignment(
        Guid id,
        UpdateAssignmentRequest request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IAuditRepository auditRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var existing = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Workstation not found", $"Workstation '{id}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageAssignment(actor, existing.SiteId))
        {
            return ForbiddenProblem("Caller is not authorized to manage assignments for this site.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var result = await deviceRepository.UpdateAssignmentAsync(
            id,
            request.AssetNumber,
            request.AssignedUserSid,
            actor.UserSid,
            request.ExpectedRowVersion,
            nowUtc,
            cancellationToken);

        if (result.Status == AssignmentUpdateStatus.NotFound || result.Device is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Workstation not found", $"Workstation '{id}' was not found.");
        }

        if (result.Status == AssignmentUpdateStatus.ConcurrencyConflict)
        {
            return Problem(
                StatusCodes.Status409Conflict,
                "RowVersion conflict",
                "The workstation was modified by another request. Reload and retry with the current RowVersion.");
        }

        if (result.Status == AssignmentUpdateStatus.DuplicateAssetNumber)
        {
            return Problem(
                StatusCodes.Status409Conflict,
                "Duplicate asset number",
                "The specified asset number is already assigned to another workstation.");
        }

        var updated = result.Device;
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "WorkstationAssignmentUpdated",
                "Device",
                updated.DeviceId.ToString("D"),
                "Success",
                updated.SiteId,
                correlationId,
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    assetNumber = updated.AssetNumber,
                    assignedUserSid = updated.AssignedUserSid,
                    rowVersion = updated.RowVersion
                })),
            cancellationToken);

        await outboxRepository.EnqueueAsync(
            new OutboxEventRecord(
                Guid.NewGuid(),
                "WorkstationAssignmentUpdated",
                updated.DeviceId,
                updated.RowVersion,
                updated.SiteId,
                updated.AssignedUserSid,
                nowUtc),
            cancellationToken);

        var sessions = await deviceRepository.GetSessionsAsync(id, cancellationToken);
        var history = await deviceRepository.GetAssignmentHistoryAsync(id, cancellationToken);
        return Results.Ok(ToDetailDto(updated, sessions, history, nowUtc));
    }

    private static async Task<IResult> RevokeWorkstation(
        Guid id,
        RevokeDeviceRequest? request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IAuditRepository auditRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var existing = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Workstation not found", $"Workstation '{id}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanRevokeDevice(actor, existing.SiteId))
        {
            return ForbiddenProblem("Caller is not authorized to revoke devices in this site.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "Revoked by administrator" : request.Reason.Trim();
        var result = await deviceRepository.RevokeAsync(id, actor.UserSid, reason, nowUtc, cancellationToken);

        if (result.Status == RevokeDeviceStatus.NotFound || result.Device is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Workstation not found", $"Workstation '{id}' was not found.");
        }

        if (result.Status == RevokeDeviceStatus.AlreadyRevoked)
        {
            return Problem(StatusCodes.Status409Conflict, "Device already revoked", "This workstation has already been revoked.");
        }

        var updated = result.Device;
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "DeviceRevoked",
                "Device",
                updated.DeviceId.ToString("D"),
                "Success",
                updated.SiteId,
                correlationId,
                nowUtc,
                JsonSerializer.Serialize(new { reason })),
            cancellationToken);

        await outboxRepository.EnqueueAsync(
            new OutboxEventRecord(
                Guid.NewGuid(),
                "DeviceRevoked",
                updated.DeviceId,
                updated.RowVersion,
                updated.SiteId,
                updated.AssignedUserSid,
                nowUtc),
            cancellationToken);

        return Results.Ok(ToSummaryDto(updated, nowUtc));
    }

    private static async Task<IResult> GetAuditEvents(
        int? page,
        int? pageSize,
        string? siteId,
        HttpContext context,
        IAuditRepository auditRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewAudit(actor))
        {
            return ForbiddenProblem("Caller is not authorized to view audit events.");
        }

        if (!string.IsNullOrWhiteSpace(siteId) && !actor.CanAccessSite(siteId.Trim()))
        {
            return ForbiddenProblem("Caller is not authorized to query audit events for the requested site.");
        }

        var safePage = Math.Max(1, page ?? 1);
        var safePageSize = Math.Clamp(pageSize ?? 50, 1, 200);

        var (items, totalCount) = await auditRepository.QueryAsync(
            safePage,
            safePageSize,
            string.IsNullOrWhiteSpace(siteId) ? null : siteId.Trim(),
            actor.AllowedSiteIds,
            actor.AllSitesAccess,
            cancellationToken);

        var dtos = items
            .Select(e => new AuditEventDto(
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
                e.SummaryJson))
            .ToArray();

        return Results.Ok(new PagedResponse<AuditEventDto>(dtos, safePage, safePageSize, totalCount));
    }

    private static MetricSampleDto ToMetricSampleDto(DeviceMetricSample sample) =>
        new(
            sample.BootId,
            sample.SampleSequence,
            sample.SampledAtUtc,
            sample.CpuPercent,
            sample.MemoryUsedPercent,
            sample.MemoryUsedBytes,
            sample.MemoryTotalBytes,
            sample.GpuPercent,
            sample.DiskActivePercent);

    private static DeviceSummaryDto ToSummaryDto(Device device, DateTimeOffset nowUtc) =>
        new(
            device.DeviceId,
            device.SiteId,
            device.Hostname,
            device.AssetNumber,
            device.AssignedUserSid,
            device.EnrollmentState.ToString(),
            DeviceConnectivityEvaluator.Evaluate(device.LastSeenUtc, nowUtc).ToString(),
            device.LastSeenUtc,
            device.PolicyRevision,
            device.RowVersion,
            device.AgentVersion);

    private static DeviceDetailDto ToDetailDto(
        Device device,
        IReadOnlyList<DeviceSession> sessions,
        IReadOnlyList<DeviceAssignment> history,
        DateTimeOffset nowUtc) =>
        new(
            device.DeviceId,
            device.SiteId,
            device.Hostname,
            device.AssetNumber,
            device.AssignedUserSid,
            device.EnrollmentState.ToString(),
            DeviceConnectivityEvaluator.Evaluate(device.LastSeenUtc, nowUtc).ToString(),
            device.LastSeenUtc,
            device.EnrolledAtUtc,
            device.BootId,
            device.AgentVersion,
            device.PolicyRevision,
            device.RowVersion,
            sessions.Select(s => new ObservedSessionDto(s.SessionId, s.UserSid, s.SessionState, s.ObservedAtUtc)).ToArray(),
            history.Select(h => new DeviceAssignmentHistoryDto(h.AssignmentId, h.DeviceId, h.UserSid, h.AssignedBySid, h.ValidFromUtc, h.ValidToUtc)).ToArray());

    internal static IResult ValidationProblem(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed");

    internal static IResult ForbiddenProblem(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden");

    internal static IResult Problem(int statusCode, string title, string? detail) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title);
}
