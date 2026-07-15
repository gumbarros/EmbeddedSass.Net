namespace EmbeddedSass.Net.Diagnostics;

public delegate ValueTask SassLogHandler(
    SassLogEvent logEvent,
    CancellationToken cancellationToken);

public sealed record SassLogEvent(
    SassLogLevel Level,
    string Message,
    string FormattedMessage,
    SassSourceSpan? Span,
    string? StackTrace,
    string? DeprecationId);

public enum SassLogLevel
{
    Warning,
    DeprecationWarning,
    Debug
}
