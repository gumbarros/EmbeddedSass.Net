using System.Buffers;
using System.IO.Pipelines;
using EmbeddedSass.Diagnostics;
using Google.Protobuf;

namespace EmbeddedSass.Internal.Transport;

internal static class PacketCodec
{
    private const int MaximumVarintBytes = 5;

    public static async ValueTask<bool> ReadAsync(
        PipeReader reader,
        int maximumPacketBytes,
        Action<ProtocolPacket> dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(dispatch);

        if (maximumPacketBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPacketBytes));
        }

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            var sequenceReader = new SequenceReader<byte>(buffer);

            var lengthStatus = TryReadUInt32(ref sequenceReader, out uint packetLength);
            if (lengthStatus == VarintStatus.Malformed)
            {
                reader.AdvanceTo(buffer.End);
                throw new SassProtocolException("The packet length is a malformed or overlong varint.");
            }

            if (lengthStatus == VarintStatus.Incomplete)
            {
                if (result.IsCompleted)
                {
                    reader.AdvanceTo(buffer.End);
                    if (buffer.IsEmpty)
                    {
                        return false;
                    }

                    throw new SassProtocolException("The compiler stream ended within a packet length varint.");
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            if (packetLength == 0)
            {
                reader.AdvanceTo(sequenceReader.Position);
                throw new SassProtocolException("A packet must include a compilation ID.");
            }

            if (packetLength > maximumPacketBytes)
            {
                reader.AdvanceTo(sequenceReader.Position);
                throw new SassProtocolException(
                    $"Packet length {packetLength} exceeds the configured maximum of {maximumPacketBytes} bytes.");
            }

            if (sequenceReader.Remaining < packetLength)
            {
                if (result.IsCompleted)
                {
                    reader.AdvanceTo(buffer.End);
                    throw new SassProtocolException("The compiler stream ended within a packet payload.");
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            var packet = buffer.Slice(sequenceReader.Position, packetLength);
            var packetReader = new SequenceReader<byte>(packet);
            var compilationIdStatus = TryReadUInt32(ref packetReader, out uint compilationId);
            if (compilationIdStatus != VarintStatus.Success)
            {
                var packetEnd = buffer.GetPosition(packetLength, sequenceReader.Position);
                reader.AdvanceTo(packetEnd);
                throw new SassProtocolException("The packet compilation ID is a malformed or truncated varint.");
            }

            var consumed = buffer.GetPosition(packetLength, sequenceReader.Position);
            try
            {
                dispatch(new ProtocolPacket(compilationId, packet.Slice(packetReader.Position)));
            }
            finally
            {
                reader.AdvanceTo(consumed);
            }

            return true;
        }
    }

    public static ValueTask WriteAsync(
        PipeWriter writer,
        uint compilationId,
        IMessage message,
        int maximumPacketBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);
        WritePacketPrefix(writer, compilationId, message.CalculateSize(), maximumPacketBytes);
        message.WriteTo(writer);
        return FlushAsync(writer, cancellationToken);
    }

    public static ValueTask WriteAsync(
        PipeWriter writer,
        uint compilationId,
        ReadOnlyMemory<byte> payload,
        int maximumPacketBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WritePacketPrefix(writer, compilationId, payload.Length, maximumPacketBytes);
        writer.Write(payload.Span);
        return FlushAsync(writer, cancellationToken);
    }

    private static void WritePacketPrefix(
        PipeWriter writer,
        uint compilationId,
        int payloadLength,
        int maximumPacketBytes)
    {
        int compilationIdLength = GetVarintLength(compilationId);
        long packetLength = (long)compilationIdLength + payloadLength;
        if (packetLength > maximumPacketBytes)
        {
            throw new SassProtocolException(
                $"Packet length {packetLength} exceeds the configured maximum of {maximumPacketBytes} bytes.");
        }

        Span<byte> prefix = stackalloc byte[MaximumVarintBytes * 2];
        int prefixLength = WriteUInt32(prefix, checked((uint)packetLength));
        prefixLength += WriteUInt32(prefix[prefixLength..], compilationId);

        writer.Write(prefix[..prefixLength]);
    }

    private static async ValueTask FlushAsync(
        PipeWriter writer,
        CancellationToken cancellationToken)
    {
        FlushResult result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.IsCompleted)
        {
            throw new EndOfStreamException("The compiler input stream closed while writing a packet.");
        }
    }

    public static int WriteUInt32(Span<byte> destination, uint value)
    {
        int index = 0;
        while (value >= 0x80)
        {
            destination[index++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[index++] = (byte)value;
        return index;
    }

    private static int GetVarintLength(uint value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            length++;
            value >>= 7;
        }

        return length;
    }

    private static VarintStatus TryReadUInt32(
        ref SequenceReader<byte> reader,
        out uint value)
    {
        SequenceReader<byte> candidate = reader;
        value = 0;

        for (int index = 0; index < MaximumVarintBytes; index++)
        {
            if (!candidate.TryRead(out byte current))
            {
                return VarintStatus.Incomplete;
            }

            if (index == MaximumVarintBytes - 1 && (current & 0xf0) != 0)
            {
                return VarintStatus.Malformed;
            }

            value |= (uint)(current & 0x7f) << (index * 7);
            if ((current & 0x80) == 0)
            {
                if (index > 0 && current == 0)
                {
                    return VarintStatus.Malformed;
                }

                reader = candidate;
                return VarintStatus.Success;
            }
        }

        return VarintStatus.Malformed;
    }

    private enum VarintStatus
    {
        Success,
        Incomplete,
        Malformed
    }
}
