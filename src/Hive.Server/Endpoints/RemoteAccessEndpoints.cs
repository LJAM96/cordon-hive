using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Identity;
using Hive.Domain.RemoteAccess;
using Hive.Infrastructure.Repositories;
using Hive.Server.Authentication;
using Hive.Server.Middleware;
using Hive.Server.Services;

namespace Hive.Server.Endpoints;

public static class RemoteAccessEndpoints
{
    private const int DefaultRdpPort = 3389;
    private const int RecommendedClearSeconds = 60;

    public static RouteGroupBuilder MapRemoteAccessEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/workstations/{id:guid}/rdp-request", RequestRdpAccess)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/workstations/{id:guid}/escrow-credential", GetEscrowCredentialStatus)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPut("/workstations/{id:guid}/escrow-credential", UpsertEscrowCredential)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapDelete("/workstations/{id:guid}/escrow-credential", DeleteEscrowCredential)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/workstations/{id:guid}/escrow-credential/reveal", RevealEscrowCredential)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        return api;
    }

    private static async Task<IResult> RequestRdpAccess(
        Guid id,
        RdpRequest request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IEscrowCredentialRepository escrowRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return WorkstationNotFound(id);
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanRequestRdp(actor, device.SiteId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to request remote desktop access for this workstation.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return HiveApiEndpoints.ValidationProblem("Reason is required.");
        }

        var sessions = await deviceRepository.GetSessionsAsync(id, cancellationToken);
        var nowUtc = timeProvider.GetUtcNow();
        var evaluation = RdpPolicyEvaluator.Evaluate(device, sessions, nowUtc);
        var hasEscrowedCredential = await escrowRepository.GetByDeviceIdAsync(id, cancellationToken) is not null;

        var requestId = Guid.NewGuid();
        var reason = request.Reason.Trim();

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "RdpAccessRequested",
                "Device",
                device.DeviceId.ToString("D"),
                evaluation.Allowed ? "Allowed" : "Denied",
                device.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    requestId,
                    reason,
                    useEscrowedCredential = request.UseEscrowedCredential,
                    blockedReason = evaluation.BlockedReason,
                    connectivityState = evaluation.ConnectivityState.ToString(),
                    protectedModeActive = device.ProtectedModeActive,
                    hasEscrowedCredential
                })),
            cancellationToken);

        var sessionDtos = sessions
            .Select(s => new ObservedSessionDto(s.SessionId, s.UserSid, s.SessionState, s.ObservedAtUtc))
            .ToArray();

        return Results.Ok(new RdpAuthorizationResponse(
            requestId,
            device.DeviceId,
            evaluation.Allowed,
            evaluation.ApprovedHostname,
            DefaultRdpPort,
            evaluation.BlockedReason,
            evaluation.Warnings,
            evaluation.ConnectivityState.ToString(),
            device.ProtectedModeActive,
            hasEscrowedCredential,
            sessionDtos,
            nowUtc));
    }

    private static async Task<IResult> GetEscrowCredentialStatus(
        Guid id,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IEscrowCredentialRepository escrowRepository,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return WorkstationNotFound(id);
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanEscrowWorkstationCredential(actor, device.SiteId, device.AssignedUserSid))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view escrow credential metadata for this workstation.");
        }

        var record = await escrowRepository.GetByDeviceIdAsync(id, cancellationToken);
        return Results.Ok(ToStatusDto(device.DeviceId, record));
    }

    private static async Task<IResult> UpsertEscrowCredential(
        Guid id,
        UpsertEscrowCredentialRequest request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IEscrowCredentialRepository escrowRepository,
        EscrowVaultService vault,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return WorkstationNotFound(id);
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanEscrowWorkstationCredential(actor, device.SiteId, device.AssignedUserSid))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to escrow credentials for this workstation.");
        }

        if (string.IsNullOrWhiteSpace(request.AccountUsername) || string.IsNullOrWhiteSpace(request.Password))
        {
            return HiveApiEndpoints.ValidationProblem("AccountUsername and Password are required.");
        }

        if (!vault.IsConfigured)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Escrow vault unavailable",
                "Escrow vault master key is not configured.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var record = vault.EncryptNew(
            device.DeviceId,
            request.AccountUsername.Trim(),
            request.Password,
            actor.UserSid,
            nowUtc);

        var saved = await escrowRepository.UpsertAsync(record, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "WorkstationCredentialEscrowed",
                "Device",
                device.DeviceId.ToString("D"),
                "Success",
                device.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    accountUsername = saved.AccountUsername,
                    keyVersion = saved.KeyVersion
                })),
            cancellationToken);

        return Results.Ok(ToStatusDto(device.DeviceId, saved));
    }

    private static async Task<IResult> DeleteEscrowCredential(
        Guid id,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IEscrowCredentialRepository escrowRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return WorkstationNotFound(id);
        }

        var actor = context.User.ToActorIdentity();
        var isAuthorized = !actor.IsDevice &&
            (ScopeAuthorizationService.CanAccessBreakGlassCredential(actor, device.SiteId) ||
             (actor.HasRole(HiveRole.Administrator) && actor.CanAccessSite(device.SiteId)));
        if (!isAuthorized)
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to delete escrowed credentials for this workstation.");
        }

        var deleted = await escrowRepository.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Escrowed credential not found",
                $"No escrowed credential exists for workstation '{id}'.");
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "WorkstationCredentialDeleted",
                "Device",
                device.DeviceId.ToString("D"),
                "Success",
                device.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                timeProvider.GetUtcNow()),
            cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> RevealEscrowCredential(
        Guid id,
        RevealEscrowCredentialRequest request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IEscrowCredentialRepository escrowRepository,
        EscrowVaultService vault,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return WorkstationNotFound(id);
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanAccessBreakGlassCredential(actor, device.SiteId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller does not hold BreakGlassCustodian permissions for this workstation's site.");
        }

        var trimmedReason = request.Reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length < 5)
        {
            return HiveApiEndpoints.ValidationProblem("Reason must be at least 5 non-whitespace characters.");
        }

        if (!EscrowRevealPurposes.IsValid(request.Purpose))
        {
            return HiveApiEndpoints.ValidationProblem(
                $"Purpose must be '{EscrowRevealPurposes.PhysicalSignIn}' or '{EscrowRevealPurposes.AssistedRdp}'.");
        }

        if (!vault.IsConfigured)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Escrow vault unavailable",
                "Escrow vault master key is not configured.");
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        var nowUtc = timeProvider.GetUtcNow();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

        if (string.Equals(request.Purpose, EscrowRevealPurposes.AssistedRdp, StringComparison.Ordinal))
        {
            var sessions = await deviceRepository.GetSessionsAsync(id, cancellationToken);
            var evaluation = RdpPolicyEvaluator.Evaluate(device, sessions, nowUtc);
            if (!evaluation.Allowed)
            {
                await auditRepository.AppendAsync(
                    AuditEvent.Create(
                        "User",
                        actor.UserSid,
                        "WorkstationCredentialRevealBlocked",
                        "Device",
                        device.DeviceId.ToString("D"),
                        "Blocked",
                        device.SiteId,
                        correlationId,
                        nowUtc,
                        JsonSerializer.Serialize(new
                        {
                            purpose = request.Purpose,
                            reason = trimmedReason,
                            blockedReason = evaluation.BlockedReason
                        })),
                    cancellationToken);

                return HiveApiEndpoints.Problem(
                    StatusCodes.Status409Conflict,
                    "Assisted RDP credential reveal blocked",
                    evaluation.BlockedReason);
            }
        }

        var record = await escrowRepository.GetByDeviceIdAsync(id, cancellationToken);
        if (record is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Escrowed credential not found",
                $"No escrowed credential exists for workstation '{id}'.");
        }

        var plaintextPassword = vault.Decrypt(record);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "WorkstationCredentialRevealed",
                "Device",
                device.DeviceId.ToString("D"),
                "Success",
                device.SiteId,
                correlationId,
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    purpose = request.Purpose,
                    reason = trimmedReason,
                    accountUsername = record.AccountUsername
                })),
            cancellationToken);

        return Results.Ok(new RevealEscrowCredentialResponse(
            device.DeviceId,
            device.Hostname,
            record.AccountUsername,
            plaintextPassword,
            request.Purpose,
            correlationId,
            nowUtc,
            RecommendedClearSeconds));
    }

    private static EscrowCredentialStatusDto ToStatusDto(Guid deviceId, EscrowedCredentialRecord? record) =>
        record is null
            ? new EscrowCredentialStatusDto(deviceId, false, null, null, null, 0)
            : new EscrowCredentialStatusDto(
                deviceId,
                true,
                record.AccountUsername,
                record.UpdatedBySid,
                record.UpdatedAtUtc,
                record.KeyVersion);

    private static IResult WorkstationNotFound(Guid id) =>
        HiveApiEndpoints.Problem(
            StatusCodes.Status404NotFound,
            "Workstation not found",
            $"Workstation '{id}' was not found.");
}
