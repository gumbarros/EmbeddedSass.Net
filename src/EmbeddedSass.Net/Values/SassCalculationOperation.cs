namespace EmbeddedSass.Values;

public sealed class SassCalculationOperation(
    SassCalculationOperator @operator,
    SassCalculationArgument left,
    SassCalculationArgument right) : SassCalculationArgument
{
    public SassCalculationOperator Operator { get; } = @operator;

    public SassCalculationArgument Left { get; } = left;

    public SassCalculationArgument Right { get; } = right;
}
