namespace EmbeddedSass.Values;

public sealed class SassListValue(
    IReadOnlyList<SassValue> contents,
    SassListSeparator separator = SassListSeparator.Comma,
    bool hasBrackets = false) : SassValue
{
    public IReadOnlyList<SassValue> Contents { get; } = contents;

    public SassListSeparator Separator { get; } = separator;

    public bool HasBrackets { get; } = hasBrackets;
}
