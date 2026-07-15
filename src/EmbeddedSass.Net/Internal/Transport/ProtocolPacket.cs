namespace EmbeddedSass.Net.Internal.Transport;

internal readonly record struct ProtocolPacket(
    uint CompilationId,
    ReadOnlyMemory<byte> Payload);
