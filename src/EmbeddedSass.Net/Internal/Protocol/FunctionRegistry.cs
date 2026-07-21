using EmbeddedSass.Functions;

namespace EmbeddedSass.Internal.Protocol;

internal sealed class FunctionRegistry
{
    private Dictionary<string, ISassFunction>? _functions;

    public void Register(ISassFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentException.ThrowIfNullOrWhiteSpace(function.Signature);

        string name = GetName(function.Signature);
        _functions ??= new Dictionary<string, ISassFunction>(StringComparer.Ordinal);
        if (!_functions.TryAdd(name, function))
        {
            throw new ArgumentException(
                $"A custom Sass function named '{name}' is already registered.",
                nameof(function));
        }
    }

    public bool TryGet(string name, out ISassFunction? function)
    {
        if (_functions is not null)
        {
            return _functions.TryGetValue(name, out function);
        }

        function = null;
        return false;
    }

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
