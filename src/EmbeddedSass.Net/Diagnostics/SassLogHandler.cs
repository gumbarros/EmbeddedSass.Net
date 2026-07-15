namespace EmbeddedSass.Net.Diagnostics;

public delegate ValueTask SassLogHandler(
    SassLogEvent logEvent,
    CancellationToken cancellationToken);