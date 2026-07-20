namespace EmbeddedSass.Values;

public sealed class SassCalculationValue(
    string name,
    IReadOnlyList<SassCalculationArgument> arguments) : SassValue
{
    public string Name { get; } = name;

    public IReadOnlyList<SassCalculationArgument> Arguments { get; } = arguments;
}
