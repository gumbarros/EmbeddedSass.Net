namespace EmbeddedSass.Values;

public sealed class SassMapEntry(SassValue key, SassValue value)
{
    public SassValue Key { get; } = key;

    public SassValue Value { get; } = value;
}
