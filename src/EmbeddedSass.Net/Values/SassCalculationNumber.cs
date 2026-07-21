namespace EmbeddedSass.Values;

public sealed class SassCalculationNumber(SassNumberValue value) : SassCalculationArgument
{
    public SassNumberValue Value { get; } = value;
}
