using System.IO.Pipes;
using Hive.Contracts;

namespace Hive.Windows.Ipc;

/// <summary>
/// Local-only named pipe client using length-prefixed JSON framing.
/// </summary>
public sealed class NamedPipeClient : IAsyncDisposable, IDisposable
{
    private readonly NamedPipeClientStream _stream;

    public NamedPipeClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    public Stream RawStream => _stream;

    public Task ConnectAsync(int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default) =>
        _stream.ConnectAsync(timeoutMilliseconds, cancellationToken);

    public async Task<IpcEnvelope> SendAsync(IpcEnvelope request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await NamedPipeFraming.WriteEnvelopeAsync(_stream, request, cancellationToken).ConfigureAwait(false);
        return await NamedPipeFraming.ReadEnvelopeAsync(_stream, cancellationToken).ConfigureAwait(false)
               ?? throw new EndOfStreamException("Named pipe server closed the connection before returning a response.");
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    public void Dispose() => _stream.Dispose();
}
