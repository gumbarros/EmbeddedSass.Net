namespace EmbeddedSass.Values;

public sealed class SassBooleanValue(bool value) : SassValue
{
    public bool Value { get; } = value;
}
