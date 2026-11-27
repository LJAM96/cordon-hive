using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Hive.Contracts;

namespace Hive.Windows.Ipc;

/// <summary>
/// Length-prefixed (4-byte little-endian int32 + UTF-8 JSON) framing for local IPC pipes.
/// Enforces <see cref="ProtocolConstants.MaxIpcMessageBytes"/> on both send and receive.
/// </summary>
public static class NamedPipeFraming
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    public static async Task WriteEnvelopeAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
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

    public static async Task<IpcEnvelope?> ReadEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

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

        IpcEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<IpcEnvelope>(buffer, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed IPC envelope JSON.", ex);
        }

        if (envelope is null ||
            string.IsNullOrWhiteSpace(envelope.MessageType) ||
            envelope.CorrelationId is null ||
            envelope.PayloadJson is null)
        {
            throw new InvalidDataException("IPC envelope is missing required fields.");
        }

        return envelope;
    }

    public static string SerializePayload<T>(T payload) =>
        JsonSerializer.Serialize(payload, SerializerOptions);

    public static T DeserializePayload<T>(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new InvalidDataException("IPC payload JSON must not be empty.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payloadJson, SerializerOptions)
                   ?? throw new InvalidDataException($"IPC payload deserialized to null for {typeof(T).Name}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Malformed IPC payload JSON for {typeof(T).Name}.", ex);
        }
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
}
