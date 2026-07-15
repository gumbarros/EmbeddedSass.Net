namespace EmbeddedSass.Net.Compilation;

//todo: When C# 15 releases change this to type union

public abstract record SassInput
{
    public static SassFileInput FromFile(string path) => new(path);
    public static SassStringInput FromInput(string source) => new(source);
}

public sealed record SassFileInput(string Path) : SassInput;

public sealed record SassStringInput(
    string Source,
    SassSyntax Syntax = SassSyntax.Scss,
    Uri? Url = null) : SassInput;
