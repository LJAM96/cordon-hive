using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Deployments;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Agent;

public sealed class AgentUserIpcWorker : BackgroundService
{
    public const string MaintenanceLockCursorKey = "maintenance_lock";

    private readonly IAgentLocalStore _store;
    private readonly AgentRuntimeState _runtimeState;
    private readonly AgentOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentUserIpcWorker> _logger;

    public AgentUserIpcWorker(
        IAgentLocalStore store,
        AgentRuntimeState runtimeState,
        IOptions<AgentOptions> options,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _runtimeState = runtimeState;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentUserIpcWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);

        await using var host = new NamedPipeServerHost(
            _options.UserPipeName,
            NamedPipeSecurityFactory.CreateUserPipeSecurity(),
            HandleRequestAsync,
            _loggerFactory.CreateLogger<NamedPipeServerHost>());

        _logger.LogInformation("Starting user IPC named pipe server on {PipeName}.", _options.UserPipeName);
        await host.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    internal async Task<IpcEnvelope> HandleRequestAsync(
        IpcEnvelope request,
        string callerSid,
        CancellationToken cancellationToken)
    {
        return request.MessageType switch
        {
            IpcMessageTypes.Hello => HandleHello(request, callerSid),
            IpcMessageTypes.GetStatus => await HandleGetStatusAsync(request, cancellationToken).ConfigureAwait(false),
            IpcMessageTypes.GetLocalTelemetry => HandleGetLocalTelemetry(request),
            IpcMessageTypes.GetProtectionLock => await HandleGetProtectionLockAsync(request, cancellationToken).ConfigureAwait(false),
            IpcMessageTypes.SetProtectionLock => await HandleSetProtectionLockAsync(request, callerSid, cancellationToken).ConfigureAwait(false),
            _ => new IpcEnvelope(
                ProtocolConstants.IpcProtocolVersion,
                IpcMessageTypes.Error,
                request.CorrelationId,
                NamedPipeFraming.SerializePayload(new
                {
                    code = "UNSUPPORTED_MESSAGE_TYPE",
                    message = $"Message type '{request.MessageType}' is not supported."
                }))
        };
    }

    private IpcEnvelope HandleHello(IpcEnvelope request, string callerSid)
    {
        var helloRequest = NamedPipeFraming.DeserializePayload<IpcHelloRequest>(request.PayloadJson);
        if (helloRequest.ClientVersion <= 0 || string.IsNullOrWhiteSpace(helloRequest.ClientName))
        {
            throw new InvalidDataException("Invalid IpcHelloRequest fields.");
        }

        // Always echo the server-verified Windows SID, never client-supplied identity.
        var response = new IpcHelloResponse(
            ProtocolConstants.IpcProtocolVersion,
            _runtimeState.AgentVersion,
            callerSid);

        return new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.Hello,
            request.CorrelationId,
            NamedPipeFraming.SerializePayload(response));
    }

    private async Task<IpcEnvelope> HandleGetStatusAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var state = await _store.GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        var queueCount = await _store.GetOutboundCountAsync(cancellationToken).ConfigureAwait(false);
        var nowUtc = DateTimeOffset.UtcNow;
        var connectivity = _runtimeState.GetConnectivityState(nowUtc);

        var status = new IpcAgentStatusResponse(
            state?.DeviceId,
            state?.EnrollmentState ?? EnrollmentState.Pending.ToString(),
            connectivity.ToString(),
            _runtimeState.LastHeartbeatUtc,
            state?.PolicyRevision ?? 0,
            _runtimeState.ProtectedModeActive || (state?.ProtectedModeActive ?? false),
            queueCount);

        return new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.GetStatus,
            request.CorrelationId,
            NamedPipeFraming.SerializePayload(status));
    }

    private IpcEnvelope HandleGetLocalTelemetry(IpcEnvelope request)
    {
        var (_, envelope, originalProcessCount, returnedProcessCount) =
            _runtimeState.BuildBoundedLocalTelemetry(DateTimeOffset.UtcNow, request.CorrelationId);

        if (returnedProcessCount < originalProcessCount)
        {
            _logger.LogDebug(
                "Bounded local IPC telemetry process list from {OriginalCount} to {ReturnedCount} processes to fit within {MaxBytes} byte IPC frame limit.",
                originalProcessCount,
                returnedProcessCount,
                ProtocolConstants.MaxIpcMessageBytes);
        }

        return envelope;
    }

    private async Task<IpcEnvelope> HandleGetProtectionLockAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken)
    {
        var lockCursor = await _store.GetSyncCursorAsync(MaintenanceLockCursorKey, cancellationToken).ConfigureAwait(false);
        var state = await _store.GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        var persistedLock = TryDeserializeMaintenanceLock(lockCursor);

        var protectedModeActive = persistedLock is not null
            ? persistedLock.ManualProtectedMode || (state?.ProtectedModeActive ?? false)
            : (state?.ProtectedModeActive ?? false) || _runtimeState.ProtectedModeActive;

        var response = new IpcProtectionLockResponse(
            ProtectedModeActive: protectedModeActive,
            ReasonCode: persistedLock?.ReasonCode ?? (protectedModeActive ? "manual_protection_lock" : "unlocked"),
            LockedBySid: persistedLock?.LockedBySid,
            UpdatedUtc: persistedLock?.UpdatedUtc ?? state?.UpdatedAtUtc);

        return new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.GetProtectionLock,
            request.CorrelationId,
            NamedPipeFraming.SerializePayload(response));
    }

    private async Task<IpcEnvelope> HandleSetProtectionLockAsync(
        IpcEnvelope request,
        string callerSid,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerSid))
        {
            throw new InvalidDataException("Authenticated caller SID is required to modify protection lock.");
        }

        var lockRequest = NamedPipeFraming.DeserializePayload<IpcProtectionLockRequest>(request.PayloadJson);
        var nowUtc = DateTimeOffset.UtcNow;
        var reasonCode = string.IsNullOrWhiteSpace(lockRequest.ReasonCode)
            ? (lockRequest.Enable ? "manual_protection_lock" : "manual_protection_cleared")
            : lockRequest.ReasonCode.Trim();

        // Derive LockedBySid exclusively from the server-verified named pipe peer SID.
        var maintenanceLock = new MaintenanceLock(
            ManualProtectedMode: lockRequest.Enable,
            ReasonCode: reasonCode,
            LockedBySid: callerSid,
            UpdatedUtc: nowUtc);

        var lockJson = NamedPipeFraming.SerializePayload(maintenanceLock);
        await _store.SaveSyncCursorAsync(MaintenanceLockCursorKey, lockJson, nowUtc, cancellationToken).ConfigureAwait(false);

        var existingState = await _store.GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        var updatedState = existingState is not null
            ? existingState with { ProtectedModeActive = lockRequest.Enable, UpdatedAtUtc = nowUtc }
            : new AgentDeviceState(
                DeviceId: null,
                SiteId: null,
                CertificateThumbprint: null,
                EnrollmentState: EnrollmentState.Pending.ToString(),
                PolicyRevision: 0,
                LastHeartbeatUtc: null,
                ProtectedModeActive: lockRequest.Enable,
                UpdatedAtUtc: nowUtc);

        await _store.SaveDeviceStateAsync(updatedState, cancellationToken).ConfigureAwait(false);
        _runtimeState.ProtectedModeActive = lockRequest.Enable;

        var response = new IpcProtectionLockResponse(
            ProtectedModeActive: maintenanceLock.ManualProtectedMode,
            ReasonCode: maintenanceLock.ReasonCode,
            LockedBySid: maintenanceLock.LockedBySid,
            UpdatedUtc: maintenanceLock.UpdatedUtc);

        return new IpcEnvelope(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.SetProtectionLock,
            request.CorrelationId,
            NamedPipeFraming.SerializePayload(response));
    }

    internal static MaintenanceLock? TryDeserializeMaintenanceLock(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MaintenanceLock>(rawJson, NamedPipeFraming.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
