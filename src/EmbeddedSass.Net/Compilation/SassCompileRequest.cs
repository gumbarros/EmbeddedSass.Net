using EmbeddedSass.Net.Diagnostics;

namespace EmbeddedSass.Net.Compilation;

public sealed record SassCompileRequest(SassInput Input)
{
    public SassOutputStyle OutputStyle { get; init; } = SassOutputStyle.Expanded;

    public bool GenerateSourceMap { get; init; }

    public bool IncludeSourcesInSourceMap { get; init; }

    public bool EmitCharset { get; init; } = true;

    public bool UseTerminalColors { get; init; }

    public bool UseAsciiDiagnostics { get; init; }

    public bool VerboseDeprecations { get; init; }

    public bool QuietDependencies { get; init; }

    public bool Silent { get; init; }

    public IReadOnlyList<string> LoadPaths { get; init; } = [];

    public IReadOnlyList<string> FatalDeprecations { get; init; } = [];

    public IReadOnlyList<string> SilencedDeprecations { get; init; } = [];

    public IReadOnlyList<string> FutureDeprecations { get; init; } = [];

    public SassLogHandler? LogHandler { get; init; }
}
