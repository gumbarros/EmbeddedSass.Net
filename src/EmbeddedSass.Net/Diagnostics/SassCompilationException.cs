namespace EmbeddedSass.Diagnostics;

public sealed class SassCompilationException(
    string message,
    string formattedMessage,
    SassSourceSpan? span = null,
    string? compilerStackTrace = null)
    : Exception(message)
{
    public string FormattedMessage { get; } = formattedMessage;

    public SassSourceSpan? Span { get; } = span;

    public string? CompilerStackTrace { get; } = compilerStackTrace;
}
