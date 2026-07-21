namespace EmbeddedSass.Values;

public sealed class SassColorValue(
    string space,
    double? channel1,
    double? channel2,
    double? channel3,
    double? alpha = 1) : SassValue
{
    public string Space { get; } = space;

    public double? Channel1 { get; } = channel1;

    public double? Channel2 { get; } = channel2;

    public double? Channel3 { get; } = channel3;

    public double? Alpha { get; } = alpha;
}
