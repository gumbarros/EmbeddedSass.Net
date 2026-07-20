namespace EmbeddedSass.Diagnostics;

public sealed record SassSourceLocation(uint Offset, uint Line, uint Column);

public sealed record SassSourceSpan(
    string Text,
    SassSourceLocation Start,
    SassSourceLocation? End,
    Uri? Url,
    string? Context);
