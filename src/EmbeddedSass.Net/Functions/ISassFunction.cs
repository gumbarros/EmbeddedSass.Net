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