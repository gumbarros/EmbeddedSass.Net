using EmbeddedSass.Importing;

namespace EmbeddedSass;

//todo: When C# 15 releases change this to type union

public abstract record SassInput;

public sealed record SassFileInput(string Path) : SassInput;

public sealed record SassStringInput(
    string Source,
    SassSyntax Syntax = SassSyntax.Scss,
    Uri? Url = null) : SassInput
{
    public ISassImporter? Importer { get; init; }
}
