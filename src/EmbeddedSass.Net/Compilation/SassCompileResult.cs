namespace EmbeddedSass.Compilation;

public sealed class SassCompileResult
{
    public string Css { get; }
    public string? SourceMap { get;  }
    public IReadOnlyList<Uri> LoadedUrls { get; }
    
    // ReSharper disable once ConvertToPrimaryConstructor
    public SassCompileResult(
        string css,
        string? sourceMap,
        IReadOnlyList<Uri> loadedUrls)
    {
        Css = css;
        SourceMap = sourceMap;
        LoadedUrls = loadedUrls;
    }

}
