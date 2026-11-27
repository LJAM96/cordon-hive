using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Hive.Contracts;

namespace Hive.App.Services;

public sealed record LocalIpcResult<T>(
    bool IsAvailable,
    T? Value,
    string StatusMessage);

/// <summary>
/// Lightweight local named-pipe client for CordonHive.Agent.User using Hive.Contracts directly
/// with 4-byte little-endian length-prefixed UTF-8 JSON framing matching Hive.Windows.Ipc.NamedPipeFraming.
/// </summary>
public sealed class LocalAgentIpcClient
{
    public const string DefaultPipeName = "CordonHive.Agent.User";
    public const int DefaultConnectTimeoutMs = 1500;
    private const int OperationTimeoutMs = 3000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;

    public LocalAgentIpcClient(
        string pipeName = DefaultPipeName,
        int connectTimeoutMs = DefaultConnectTimeoutMs)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        _connectTimeoutMs = Math.Clamp(connectTimeoutMs, 250, 10000);
    }

    public Task<LocalIpcResult<IpcAgentStatusResponse>> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<IpcAgentStatusResponse>(IpcMessageTypes.GetStatus, cancellationToken);

    public Task<LocalIpcResult<IpcLocalTelemetryResponse>> GetLocalTelemetryAsync(
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<IpcLocalTelemetryResponse>(IpcMessageTypes.GetLocalTelemetry, cancellationToken);

    private async Task<LocalIpcResult<T>> SendRequestAsync<T>(
        string messageType,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_connectTimeoutMs + OperationTimeoutMs);

            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(_connectTimeoutMs, timeoutCts.Token).ConfigureAwait(false);

            var request = new IpcEnvelope(
                ProtocolConstants.IpcProtocolVersion,
                messageType,
                Guid.NewGuid().ToString("N"),
                "{}");

            await WriteEnvelopeAsync(pipe, request, timeoutCts.Token).ConfigureAwait(false);
            var response = await ReadEnvelopeAsync(pipe, timeoutCts.Token).ConfigureAwait(false);

            if (response is null)
            {
                return new LocalIpcResult<T>(
                    false,
                    default,
                    "Local agent closed the IPC pipe without responding.");
            }

            if (string.Equals(response.MessageType, IpcMessageTypes.Error, StringComparison.OrdinalIgnoreCase))
            {
                var errorMessage = TryExtractErrorMessage(response.PayloadJson)
                    ?? $"Local agent returned an IPC error for '{messageType}'.";
                return new LocalIpcResult<T>(false, default, errorMessage);
            }

            if (string.IsNullOrWhiteSpace(response.PayloadJson))
            {
                return new LocalIpcResult<T>(false, default, "Local agent returned an empty IPC payload.");
            }

            var payload = JsonSerializer.Deserialize<T>(response.PayloadJson, JsonOptions);
            if (payload is null)
            {
                return new LocalIpcResult<T>(false, default, "Local agent returned a null IPC payload.");
            }

            return new LocalIpcResult<T>(true, payload, "Connected to local agent service.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new LocalIpcResult<T>(false, default, "Local agent request was cancelled.");
        }
        catch (TimeoutException)
        {
            return new LocalIpcResult<T>(false, default, "Local agent service is not running (IPC connect timed out).");
        }
        catch (OperationCanceledException)
        {
            return new LocalIpcResult<T>(false, default, "Local agent service is not running or did not respond in time.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or PlatformNotSupportedException)
        {
            return new LocalIpcResult<T>(false, default, $"Local agent IPC unavailable: {ex.Message}");
        }
    }

    private static async Task WriteEnvelopeAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (payloadBytes.Length == 0 || payloadBytes.Length > ProtocolConstants.MaxIpcMessageBytes)
        {
            throw new InvalidDataException(
                $"IPC message size {payloadBytes.Length} bytes exceeds maximum allowed {ProtocolConstants.MaxIpcMessageBytes} bytes.");
        }

        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payloadBytes.Length);

        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IpcEnvelope?> ReadEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthPrefix = new byte[4];
        var headerBytesRead = await ReadAtMostAsync(stream, lengthPrefix, cancellationToken).ConfigureAwait(false);
        if (headerBytesRead == 0)
        {
            return null;
        }

        if (headerBytesRead < 4)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading IPC frame length.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (length <= 0 || length > ProtocolConstants.MaxIpcMessageBytes)
        {
            throw new InvalidDataException(
                $"Invalid IPC frame length {length}; allowed range is 1..{ProtocolConstants.MaxIpcMessageBytes} bytes.");
        }

        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(buffer, JsonOptions);
        if (envelope is null ||
            string.IsNullOrWhiteSpace(envelope.MessageType) ||
            envelope.CorrelationId is null ||
            envelope.PayloadJson is null)
        {
            throw new InvalidDataException("IPC envelope is missing required fields.");
        }

        return envelope;
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static string? TryExtractErrorMessage(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
        }
        catch
        {
            // Ignore malformed error payload.
        }

        return null;
    }
}
