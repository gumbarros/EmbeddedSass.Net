using EmbeddedSass.Functions;

namespace EmbeddedSass.Internal.Protocol;

internal sealed class FunctionRegistry
{
    private readonly Dictionary<string, ISassFunction> _functions =
        new(StringComparer.Ordinal);

    public void Register(ISassFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentException.ThrowIfNullOrWhiteSpace(function.Signature);

        string name = GetName(function.Signature);
        if (!_functions.TryAdd(name, function))
        {
            throw new ArgumentException(
                $"A custom Sass function named '{name}' is already registered.",
                nameof(function));
        }
    }

    public bool TryGet(string name, out ISassFunction? function) =>
        _functions.TryGetValue(name, out function);

    private static string GetName(string signature)
    {
        int openingParenthesis = signature.IndexOf('(');
        if (openingParenthesis <= 0)
        {
            throw new ArgumentException(
                $"Custom Sass function signature '{signature}' must contain a function name followed by '('.",
                nameof(signature));
        }

        string name = signature[..openingParenthesis].Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"Custom Sass function signature '{signature}' has an invalid function name.",
                nameof(signature));
        }

        return name;
    }
}
