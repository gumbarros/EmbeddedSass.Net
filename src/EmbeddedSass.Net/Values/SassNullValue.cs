namespace EmbeddedSass.Values;

public sealed class SassNullValue : SassValue
{
    private SassNullValue()
    {
    }

    public static SassNullValue Instance { get; } = new();
}
