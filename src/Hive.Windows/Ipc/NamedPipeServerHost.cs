using System.IO.Pipes;
using System.Security.Principal;
using Hive.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hive.Windows.Ipc;

public delegate Task<IpcEnvelope> IpcRequestHandler(
    IpcEnvelope request,
    string callerSid,
    CancellationToken cancellationToken);

/// <summary>
/// Async local named pipe server host that derives the caller's Windows SID server-side
/// via <see cref="NamedPipeServerStream.RunAsClient"/> and never trusts client-supplied SIDs.
/// </summary>
public sealed class NamedPipeServerHost : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly PipeSecurity _pipeSecurity;
    private readonly IpcRequestHandler _handler;
    private readonly ILogger<NamedPipeServerHost> _logger;
    private readonly List<Task> _activeConnections = [];
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public NamedPipeServerHost(
        string pipeName,
        PipeSecurity pipeSecurity,
        IpcRequestHandler handler,
        ILogger<NamedPipeServerHost>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(pipeSecurity);
        ArgumentNullException.ThrowIfNull(handler);

        _pipeName = pipeName;
        _pipeSecurity = pipeSecurity;
        _handler = handler;
        _logger = logger ?? NullLogger<NamedPipeServerHost>.Instance;
    }

    public string PipeName => _pipeName;

    public void Start(CancellationToken externalCancellationToken = default)
    {
        if (_acceptLoopTask is not null)
        {
            throw new InvalidOperationException("NamedPipeServerHost is already started.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Start(cancellationToken);
        if (_acceptLoopTask is not null)
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    _pipeSecurity);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var connectedStream = server;
                server = null;

                var connectionTask = HandleClientAsync(connectedStream, ct);
                TrackConnection(connectionTask);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting named pipe connection on {PipeName}.", _pipeName);
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
        }
    }

    private void TrackConnection(Task connectionTask)
    {
        lock (_lock)
        {
            _activeConnections.Add(connectionTask);
        }

        _ = connectionTask.ContinueWith(
            t =>
            {
                lock (_lock)
                {
                    _activeConnections.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                while (!ct.IsCancellationRequested && server.IsConnected)
                {
                    IpcEnvelope? request;
                    try
                    {
                        request = await NamedPipeFraming.ReadEnvelopeAsync(server, ct).ConfigureAwait(false);
                    }
                    catch (InvalidDataException ex)
                    {
                        _logger.LogWarning(ex, "Rejected invalid IPC frame on pipe {PipeName}.", _pipeName);
                        await TryWriteErrorAsync(server, string.Empty, "INVALID_FRAME", ex.Message, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }

                    if (request is null)
                    {
                        break;
                    }

                    string? callerSid = null;
                    server.RunAsClient(() =>
                    {
                        callerSid = WindowsIdentity.GetCurrent().User?.Value;
                    });

                    if (string.IsNullOrWhiteSpace(callerSid))
                    {
                        _logger.LogWarning("Unable to determine caller SID on pipe {PipeName}; closing connection.", _pipeName);
                        await TryWriteErrorAsync(server, request.CorrelationId, "UNAUTHORIZED", "Caller identity could not be verified.", ct).ConfigureAwait(false);
                        break;
                    }

                    IpcEnvelope response;
                    try
                    {
                        response = await _handler(request, callerSid, ct).ConfigureAwait(false);
                    }
                    catch (InvalidDataException ex)
                    {
                        response = CreateErrorEnvelope(request.CorrelationId, "INVALID_PAYLOAD", ex.Message);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Unhandled error processing IPC message {MessageType}.", request.MessageType);
                        response = CreateErrorEnvelope(request.CorrelationId, "INTERNAL_ERROR", "Request processing failed.");
                    }

                    await NamedPipeFraming.WriteEnvelopeAsync(server, response, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Clean shutdown
            }
            catch (IOException)
            {
                // Client disconnected
            }
        }
    }

    private static async Task TryWriteErrorAsync(
        Stream stream,
        string correlationId,
        string code,
        string message,
        CancellationToken ct)
    {
        try
        {
            var envelope = CreateErrorEnvelope(correlationId, code, message);
            await NamedPipeFraming.WriteEnvelopeAsync(stream, envelope, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort error reply before closing stream
        }
    }

    private static IpcEnvelope CreateErrorEnvelope(string correlationId, string code, string message) =>
        new(
            ProtocolConstants.IpcProtocolVersion,
            IpcMessageTypes.Error,
            correlationId,
            NamedPipeFraming.SerializePayload(new { code, message }));

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] pending;
        lock (_lock)
        {
            pending = _activeConnections.ToArray();
        }

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _cts?.Dispose();
    }
}
