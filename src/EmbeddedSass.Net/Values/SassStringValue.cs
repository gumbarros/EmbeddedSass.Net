namespace EmbeddedSass.Values;

public sealed class SassStringValue(string text, bool quoted = true) : SassValue
{
    public string Text { get; } = text;

    public bool Quoted { get; } = quoted;
}
