namespace EmbeddedSass.Values;

public sealed class SassCalculationString(string value) : SassCalculationArgument
{
    public string Value { get; } = value;
}
