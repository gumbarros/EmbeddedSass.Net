using EmbeddedSass.Values;

namespace EmbeddedSass.Functions;

/// <summary>Defines a custom global Sass function implemented by the host.</summary>
public interface ISassFunction
{
    /// <summary>
    /// Gets the Sass function signature, such as <c>asset-url($path)</c>.
    /// </summary>
    string Signature { get; }

    /// <summary>Invokes the function with arguments supplied by Dart Sass.</summary>
    ValueTask<SassValue> InvokeAsync(
        IReadOnlyList<SassValue> arguments,
        CancellationToken cancellationToken);
}

/// <summary>Handles a custom Sass function call.</summary>
public delegate ValueTask<SassValue> SassFunctionHandler(
    IReadOnlyList<SassValue> arguments,
    CancellationToken cancellationToken);

/// <summary>Defines a custom global Sass function using a delegate.</summary>
public sealed class SassFunction : ISassFunction
{
    private readonly SassFunctionHandler _handler;

    public SassFunction(string signature, SassFunctionHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        ArgumentNullException.ThrowIfNull(handler);
        Signature = signature;
        _handler = handler;
    }

    public string Signature { get; }

    public ValueTask<SassValue> InvokeAsync(
        IReadOnlyList<SassValue> arguments,
        CancellationToken cancellationToken) =>
        _handler(arguments, cancellationToken);
}
