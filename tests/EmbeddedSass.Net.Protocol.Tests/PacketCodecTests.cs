using System.Buffers;
using System.IO.Pipelines;
using EmbeddedSass.Diagnostics;
using EmbeddedSass.Internal.Transport;

namespace EmbeddedSass.Protocol.Tests;

public sealed class PacketCodecTests
{
    [Fact]
    public async Task WritesGoldenPacketBytes()
    {
        var pipe = new Pipe();

        await PacketCodec.WriteAsync(
            pipe.Writer,
            300,
            new byte[] { 0x08, 0x01 },
            1024,
            CancellationToken.None);

        var result = await pipe.Reader.ReadAsync(CancellationToken.None);
        Assert.Equal(
            new byte[] { 0x04, 0xac, 0x02, 0x08, 0x01 },
            result.Buffer.ToArray());
        pipe.Reader.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task ReadsFragmentedPacket()
    {
        var pipe = new Pipe();
        var read = ReadPacketAsync(
            pipe.Reader,
            1024,
            CancellationToken.None);

        foreach (var value in new byte[] { 0x04, 0xac, 0x02, 0x08, 0x01 })
        {
            pipe.Writer.Write([value]);
            await pipe.Writer.FlushAsync(CancellationToken.None);
            await Task.Yield();
        }

        var packet = Assert.IsType<ProtocolPacket>(await read);
        Assert.Equal(300u, packet.CompilationId);
        Assert.Equal(new byte[] { 0x08, 0x01 }, packet.Payload.ToArray());
    }

    [Fact]
    public async Task ReadsCoalescedPacketsOneAtATime()
    {
        var pipe = new Pipe();
        pipe.Writer.Write(new byte[] {
            0x02, 0x01, 0xaa,
            0x03, 0x02, 0xbb, 0xcc
        });
        await pipe.Writer.FlushAsync(CancellationToken.None);

        var first = Assert.IsType<ProtocolPacket>(
            await ReadPacketAsync(pipe.Reader, 1024, CancellationToken.None));
        var second = Assert.IsType<ProtocolPacket>(
            await ReadPacketAsync(pipe.Reader, 1024, CancellationToken.None));

        Assert.Equal(1u, first.CompilationId);
        Assert.Equal(new byte[] { 0xaa }, first.Payload.ToArray());
        Assert.Equal(2u, second.CompilationId);
        Assert.Equal(new byte[] { 0xbb, 0xcc }, second.Payload.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 })]
    [InlineData(new byte[] { 0x80, 0x00 })]
    public async Task RejectsMalformedOrOverlongLength(byte[] bytes)
    {
        var pipe = new Pipe();
        pipe.Writer.Write(bytes);
        await pipe.Writer.CompleteAsync();

        var exception = await Assert.ThrowsAsync<SassProtocolException>(
            async () => await ReadPacketAsync(
                pipe.Reader,
                1024,
                CancellationToken.None));

        Assert.Contains("varint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsTruncatedPayloadAtEof()
    {
        var pipe = new Pipe();
        pipe.Writer.Write(new byte[] { 0x04, 0x01, 0xaa });
        await pipe.Writer.CompleteAsync();

        var exception = await Assert.ThrowsAsync<SassProtocolException>(
            async () => await ReadPacketAsync(
                pipe.Reader,
                1024,
                CancellationToken.None));

        Assert.Contains("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsOversizedPacketBeforeReadingPayload()
    {
        var pipe = new Pipe();
        pipe.Writer.Write(new byte[] { 0x7f });
        await pipe.Writer.FlushAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SassProtocolException>(
            async () => await ReadPacketAsync(
                pipe.Reader,
                64,
                CancellationToken.None));

        Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProtocolPacket?> ReadPacketAsync(
        PipeReader reader,
        int maximumPacketBytes,
        CancellationToken cancellationToken)
    {
        ProtocolPacket? packet = null;
        var packetRead = await PacketCodec.ReadAsync(
            reader,
            maximumPacketBytes,
            value => packet = new ProtocolPacket(
                value.CompilationId,
                new ReadOnlySequence<byte>(value.Payload.ToArray())),
            cancellationToken);
        return packetRead ? packet : null;
    }
}
