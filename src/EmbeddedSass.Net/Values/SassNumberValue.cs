namespace EmbeddedSass.Values;

public sealed class SassNumberValue(double value) : SassValue
{
    public double Value { get; } = value;

    public IReadOnlyList<string> NumeratorUnits { get; init; } = [];

    public IReadOnlyList<string> DenominatorUnits { get; init; } = [];
}
