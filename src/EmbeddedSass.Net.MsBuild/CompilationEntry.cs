namespace EmbeddedSass.MsBuild;

internal sealed record CompilationEntry(
    string Source,
    string Target,
    string[] LoadPaths);
