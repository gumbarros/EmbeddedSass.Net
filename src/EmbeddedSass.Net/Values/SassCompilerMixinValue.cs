namespace EmbeddedSass.Values;

/// <summary>
/// Represents a mixin defined by Sass. It may be returned to Sass during the
/// same custom-function call in which it was received.
/// </summary>
public sealed class SassCompilerMixinValue : SassValue
{
    internal SassCompilerMixinValue(uint id) => Id = id;

    internal uint Id { get; }
}
