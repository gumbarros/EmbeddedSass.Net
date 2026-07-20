using System.Buffers;

namespace EmbeddedSass.Internal.Transport;

internal readonly record struct ProtocolPacket(
    uint CompilationId,
    ReadOnlySequence<byte> Payload);
