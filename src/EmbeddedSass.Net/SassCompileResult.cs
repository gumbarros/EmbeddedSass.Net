namespace EmbeddedSass;

public sealed class SassCompileResult(
    string css,
    string? sourceMap,
    Uri[] loadedUrls)
{
    public string Css { get; } = css;
    public string? SourceMap { get;  } = sourceMap;
    public Uri[] LoadedUrls { get; } = loadedUrls;
}
