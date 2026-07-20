using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace EmbeddedSass.MsBuild;

public sealed class CompileSassTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();

    [Required] public ITaskItem[] Compilations { get; set; } = [];

    [Required] public string ProjectDirectory { get; set; } = string.Empty;

    public string Configuration { get; set; } = "Debug";

    public string OutputStyle { get; set; } = "Auto";

    public string GenerateSourceMap { get; set; } = "Auto";

    public string IncludeSourcesInSourceMap { get; set; } = "true";

    public string QuietDependencies { get; set; } = "false";

    public string SilencedDeprecations { get; set; } = string.Empty;

    public string CacheFile { get; set; } = string.Empty;

    [Output] public ITaskItem[] GeneratedFiles { get; private set; } = [];

    [Output] public ITaskItem[] RemovedFiles { get; private set; } = [];

    public override bool Execute()
    {
        try
        {
            return ExecuteAsync(_cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Log.LogWarning("Sass compilation was canceled.");
            return false;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    public void Cancel() => _cancellation.Cancel();

    private async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!TryResolveSettings(
                out SassOutputStyle style,
                out bool sourceMaps,
                out bool includeSources,
                out bool quietDependencies))
        {
            return false;
        }

        var entries = CompilationDiscovery.Discover(
            Compilations,
            ProjectDirectory,
            Log);
        if (Log.HasLoggedErrors)
        {
            return false;
        }

        if (entries.Count == 0)
        {
            Log.LogMessage(MessageImportance.Low, "No Sass entrypoints were found.");
            return true;
        }

        var compilerOptions = CompilerLocator.CreateOptions();
        var cacheFile = string.IsNullOrWhiteSpace(CacheFile)
            ? Path.Combine(ProjectDirectory, "obj", "EmbeddedSass.cache.json")
            : Path.GetFullPath(
                Path.IsPathFullyQualified(CacheFile)
                    ? CacheFile
                    : Path.Combine(ProjectDirectory, CacheFile));
        await using var executor = new SassCompilationExecutor(
            compilerOptions,
            Log,
            SplitList(SilencedDeprecations),
            cacheFile);
        var outputs = await executor.ExecuteAsync(
            entries,
            style,
            sourceMaps,
            includeSources,
            quietDependencies,
            cancellationToken).ConfigureAwait(false);
        SetOutputs(outputs);

        return !Log.HasLoggedErrors;
    }

    private void SetOutputs(CompilationOutputs outputs)
    {
        GeneratedFiles = outputs.GeneratedFiles
            .Select(static path => (ITaskItem)new TaskItem(path))
            .ToArray();
        var projectDirectory = Path.GetFullPath(ProjectDirectory);
        RemovedFiles = outputs.RemovedFiles
            .Select(ITaskItem (path) => new TaskItem(Path.GetRelativePath(projectDirectory, path)))
            .ToArray();
    }

    private bool TryResolveSettings(
        out SassOutputStyle outputStyle,
        out bool generateSourceMap,
        out bool includeSources,
        out bool quietDependencies)
    {
        var debug = string.Equals(Configuration, "Debug", StringComparison.OrdinalIgnoreCase);
        outputStyle = debug ? SassOutputStyle.Expanded : SassOutputStyle.Compressed;
        generateSourceMap = debug;
        includeSources = true;
        quietDependencies = false;

        if (!string.Equals(OutputStyle, "Auto", StringComparison.OrdinalIgnoreCase) &&
            (!Enum.TryParse(OutputStyle, ignoreCase: true, out outputStyle) || !Enum.IsDefined(outputStyle)))
        {
            Log.LogError("EmbeddedSassOutputStyle must be Auto, Expanded, or Compressed; received '{0}'.", OutputStyle);
        }

        _ = TryParseAutoBoolean(
            GenerateSourceMap,
            debug,
            "EmbeddedSassGenerateSourceMap",
            out generateSourceMap);
        _ = TryParseBoolean(
            IncludeSourcesInSourceMap,
            "EmbeddedSassIncludeSourcesInSourceMap",
            out includeSources);
        _ = TryParseBoolean(
            QuietDependencies,
            "EmbeddedSassQuietDependencies",
            out quietDependencies);

        return !Log.HasLoggedErrors;
    }

    private bool TryParseAutoBoolean(string value, bool automatic, string name, out bool result)
    {
        if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            result = automatic;
            return true;
        }

        if (bool.TryParse(value, out result))
        {
            return true;
        }

        Log.LogError("{0} must be Auto, true, or false; received '{1}'.", name, value);
        return false;
    }

    private bool TryParseBoolean(string value, string name, out bool result)
    {
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        Log.LogError("{0} must be true or false; received '{1}'.", name, value);
        return false;
    }

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}