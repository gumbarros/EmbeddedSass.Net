namespace EmbeddedSass.Values;

/// <summary>
/// Represents a function defined by Sass. It may be returned to Sass during the
/// same custom-function call in which it was received.
/// </summary>
public sealed class SassCompilerFunctionValue : SassValue
{
    internal SassCompilerFunctionValue(uint id) => Id = id;

    internal uint Id { get; }
}
