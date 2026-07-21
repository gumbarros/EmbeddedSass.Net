namespace EmbeddedSass.Diagnostics;

public sealed class SassLogEvent
{
    public SassLogLevel Level { get; }

    public string Message { get; }

    public string FormattedMessage { get; }

    public SassSourceSpan? Span { get; }

    public string? StackTrace { get; }

    public string? DeprecationId { get; }

    public SassLogEvent(
        SassLogLevel level,
        string message,
        string formattedMessage,
        SassSourceSpan? span,
        string? stackTrace,
        string? deprecationId)
    {
        Level = level;
        Message = message;
        FormattedMessage = formattedMessage;
        Span = span;
        StackTrace = stackTrace;
        DeprecationId = deprecationId;
    }

}