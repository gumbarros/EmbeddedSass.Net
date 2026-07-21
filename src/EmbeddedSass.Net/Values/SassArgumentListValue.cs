namespace EmbeddedSass.Values;

/// <summary>Represents a variable argument list supplied to a custom function.</summary>
public sealed class SassArgumentListValue : SassValue
{
    private readonly IReadOnlyDictionary<string, SassValue> _keywords;

    internal SassArgumentListValue(
        uint id,
        IReadOnlyList<SassValue> contents,
        SassListSeparator separator,
        IReadOnlyDictionary<string, SassValue> keywords)
    {
        Id = id;
        Contents = contents;
        Separator = separator;
        _keywords = keywords;
    }

    internal uint Id { get; }

    internal bool KeywordsAccessed { get; private set; }

    internal IReadOnlyDictionary<string, SassValue> RawKeywords => _keywords;

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
            return _keywords;
        }
    }
}
