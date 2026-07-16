using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using EmbeddedSass.Net.Internal.Transport;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace EmbeddedSass.Net.Protocol.Tests;

public sealed class ProtocolTransportTests
{
    [Fact]
    public async Task ConcurrentSendsProduceCompleteSerializedPackets()
    {
        var hostToCompiler = new Pipe();
        var compilerToHost = new Pipe();
        using var lifetime = new CancellationTokenSource();
        var transport = new ProtocolTransport(
            hostToCompiler.Writer.AsStream(),
            compilerToHost.Reader.AsStream(),
            maximumPendingWrites: 4,
            maximumPacketBytes: 1024,
            lifetime.Token);

        var first = transport.SendAsync(
            1,
            new BytesValue { Value = ByteString.CopyFrom(1, 2, 3) });
        var second = transport.SendAsync(
            2,
            new BytesValue { Value = ByteString.CopyFrom(4, 5, 6) });
        var packets = new ConcurrentDictionary<uint, byte[]>();
        for (var index = 0; index < 2; index++)
        {
            var packetRead = await PacketCodec.ReadAsync(
                hostToCompiler.Reader,
                1024,
                packet => packets[packet.CompilationId] =
                    BytesValue.Parser.ParseFrom(packet.Payload).Value.ToByteArray(),
                CancellationToken.None);
            Assert.True(packetRead);
        }

        await Task.WhenAll(first, second);
        Assert.Equal(new byte[] { 1, 2, 3 }, packets[1]);
        Assert.Equal(new byte[] { 4, 5, 6 }, packets[2]);

        transport.CompleteWrites();
        await transport.WriterCompletion;
        await transport.CompleteReaderAsync();
        await hostToCompiler.Reader.CompleteAsync();
        await compilerToHost.Writer.CompleteAsync();
    }

    [Fact]
    public async Task ReaderDeliversPacketsUntilEndOfStream()
    {
        var hostToCompiler = new Pipe();
        var compilerToHost = new Pipe();
        using var lifetime = new CancellationTokenSource();
        var transport = new ProtocolTransport(
            hostToCompiler.Writer.AsStream(),
            compilerToHost.Reader.AsStream(),
            maximumPendingWrites: 4,
            maximumPacketBytes: 1024,
            lifetime.Token);
        var packets = new List<(uint CompilationId, byte[] Payload)>();
        var reader = transport.ReadAllAsync(
            packet => packets.Add((packet.CompilationId, packet.Payload.ToArray())));

        await PacketCodec.WriteAsync(
            compilerToHost.Writer,
            7,
            new byte[] { 8, 9 },
            1024,
            CancellationToken.None);
        await compilerToHost.Writer.CompleteAsync();
        await reader;

        var packet = Assert.Single(packets);
        Assert.Equal(7u, packet.CompilationId);
        Assert.Equal(new byte[] { 8, 9 }, packet.Payload);

        transport.CompleteWrites();
        await transport.WriterCompletion;
        await transport.CompleteReaderAsync();
        await hostToCompiler.Reader.CompleteAsync();
    }

    [Fact]
    public async Task WriterFailureFailsCurrentAndQueuedSends()
    {
        var compilerToHost = new Pipe();
        using var lifetime = new CancellationTokenSource();
        var transport = new ProtocolTransport(
            new ThrowingWriteStream(),
            compilerToHost.Reader.AsStream(),
            maximumPendingWrites: 2,
            maximumPacketBytes: 1024,
            lifetime.Token);

        var first = transport.SendAsync(
            1,
            new BytesValue { Value = ByteString.CopyFrom(1) });
        var second = transport.SendAsync(
            2,
            new BytesValue { Value = ByteString.CopyFrom(2) });

        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
        await Assert.ThrowsAsync<IOException>(() => transport.WriterCompletion);

        await transport.CompleteReaderAsync();
        await compilerToHost.Writer.CompleteAsync();
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new IOException("write failed");

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("write failed"));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("write failed");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("write failed"));
    }
}
