using System.Buffers;

namespace EmbeddedSass.Net.Internal.Transport;

internal readonly record struct ProtocolPacket(
    uint CompilationId,
    ReadOnlySequence<byte> Payload);
