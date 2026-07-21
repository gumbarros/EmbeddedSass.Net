namespace EmbeddedSass.Values;

/// <summary>Represents a variable argument list supplied to a custom function.</summary>
public sealed class SassArgumentListValue : SassValue
{
    internal SassArgumentListValue(
        uint id,
        SassValue[] contents,
        SassListSeparator separator,
        Dictionary<string, SassValue> keywords)
    {
        Id = id;
        Contents = contents;
        Separator = separator;
        RawKeywords = keywords;
    }

    internal uint Id { get; }

    internal bool KeywordsAccessed { get; private set; }

    internal Dictionary<string, SassValue> RawKeywords { get; }

    public IReadOnlyList<SassValue> Contents { get; }

    public SassListSeparator Separator { get; }

    /// <summary>
    /// Gets keyword arguments and records that they were inspected, matching
    /// the behavior of Sass's <c>meta.keywords()</c> function.
    /// </summary>
    public IReadOnlyDictionary<string, SassValue> Keywords
    {
        get
        {
            KeywordsAccessed = true;
            return RawKeywords;
        }
    }
}
