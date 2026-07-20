namespace EmbeddedSass.Diagnostics;

public delegate ValueTask SassLogHandler(
    SassLogEvent logEvent,
    CancellationToken cancellationToken);