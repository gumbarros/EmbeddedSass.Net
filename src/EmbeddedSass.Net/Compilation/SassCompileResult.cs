namespace EmbeddedSass.Net.Compilation;

public sealed class SassCompileResult(
    string css,
    string? sourceMap,
    IReadOnlyList<Uri> loadedUrls)
{
    public string Css { get; } = css;
    public string? SourceMap { get;  } = sourceMap;
    public IReadOnlyList<Uri> LoadedUrls { get; } = loadedUrls;
}
