namespace EmbeddedSass.Values;

public sealed class SassMapValue(IReadOnlyList<SassMapEntry> entries) : SassValue
{
    public IReadOnlyList<SassMapEntry> Entries { get; } = entries;
}
