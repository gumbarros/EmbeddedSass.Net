using EmbeddedSass.Values;

namespace EmbeddedSass.Functions;

/// <summary>Defines a custom global Sass function using a delegate.</summary>
public sealed class SassFunction : ISassFunction
{
    private readonly SassFunctionHandler _handler;

    public string Signature { get; }
    
    public SassFunction(string signature, SassFunctionHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        ArgumentNullException.ThrowIfNull(handler);
        Signature = signature;
        _handler = handler;
    }

    public ValueTask<SassValue> InvokeAsync(
        IReadOnlyList<SassValue> arguments,
        CancellationToken cancellationToken) =>
        _handler(arguments, cancellationToken);
}