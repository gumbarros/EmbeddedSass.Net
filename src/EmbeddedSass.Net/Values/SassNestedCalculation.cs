namespace EmbeddedSass.Values;

public sealed class SassNestedCalculation(SassCalculationValue value) : SassCalculationArgument
{
    public SassCalculationValue Value { get; } = value;
}
