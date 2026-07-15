using System.IO.Pipelines;
using System.Threading.Channels;

namespace EmbeddedSass.Net.Internal.Transport;

internal sealed class ProtocolTransport
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly Channel<PendingWrite> _writes;
    private readonly int _maximumPacketBytes;
    private readonly CancellationToken _lifetimeCancellation;

    public ProtocolTransport(
        Stream standardInput,
        Stream standardOutput,
        int maximumPendingWrites,
        int maximumPacketBytes,
        CancellationToken lifetimeCancellation)
    {
        _reader = PipeReader.Create(standardOutput);
        _writer = PipeWriter.Create(standardInput);
        _writes = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(maximumPendingWrites)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _maximumPacketBytes = maximumPacketBytes;
        _lifetimeCancellation = lifetimeCancellation;
        WriterCompletion = WriteLoopAsync();
    }

    public Task WriterCompletion { get; }

    public async Task SendAsync(uint compilationId, ReadOnlyMemory<byte> payload)
    {
        var pending = new PendingWrite(compilationId, payload);
        await _writes.Writer.WriteAsync(pending, _lifetimeCancellation).ConfigureAwait(false);
        await pending.Completion.Task.ConfigureAwait(false);
    }

    public async Task ReadAllAsync(Action<ProtocolPacket> dispatch)
    {
        while (true)
        {
            ProtocolPacket? packet = await PacketCodec.ReadAsync(
                _reader,
                _maximumPacketBytes,
                _lifetimeCancellation).ConfigureAwait(false);
            if (packet is null)
            {
                return;
            }

            dispatch(packet.Value);
        }
    }

    public void CompleteWrites(Exception? exception = null) =>
        _writes.Writer.TryComplete(exception);

    public ValueTask CompleteReaderAsync(Exception? exception = null) =>
        _reader.CompleteAsync(exception);

    private async Task WriteLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (PendingWrite write in _writes.Reader
                .ReadAllAsync(_lifetimeCancellation)
                .ConfigureAwait(false))
            {
                try
                {
                    await PacketCodec.WriteAsync(
                        _writer,
                        write.CompilationId,
                        write.Payload,
                        _maximumPacketBytes,
                        _lifetimeCancellation).ConfigureAwait(false);
                    write.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    write.Completion.TrySetException(exception);
                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            while (_writes.Reader.TryRead(out PendingWrite? pending))
            {
                pending.Completion.TrySetException(
                    failure ?? new OperationCanceledException("The compiler writer stopped."));
            }

            await _writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private sealed record PendingWrite(uint CompilationId, ReadOnlyMemory<byte> Payload)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
