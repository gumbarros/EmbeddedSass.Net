using EmbeddedSass.Values;

namespace EmbeddedSass.Functions;

/// <summary>Handles a custom Sass function call.</summary>
public delegate ValueTask<SassValue> SassFunctionHandler(IReadOnlyList<SassValue> arguments, CancellationToken cancellationToken);