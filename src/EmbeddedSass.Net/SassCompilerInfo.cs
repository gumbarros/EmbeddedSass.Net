namespace EmbeddedSass;

public sealed record SassCompilerInfo(
    string CompilerVersion,
    string ImplementationName,
    string ImplementationVersion,
    string ProtocolVersion);
