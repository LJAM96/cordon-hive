using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Hive.Windows.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Agent;

public sealed class AgentUserIpcWorker : BackgroundService
{
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
}
