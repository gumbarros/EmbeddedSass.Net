using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using EmbeddedSass.Net;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace EmbeddedSass.Net.MsBuild;

public sealed class CompileSass : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();

    [Required]
    public ITaskItem[] Compilations { get; set; } = [];

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public string Configuration { get; set; } = "Debug";

    public string OutputStyle { get; set; } = "Auto";

    public string GenerateSourceMap { get; set; } = "Auto";

    public string IncludeSourcesInSourceMap { get; set; } = "true";

    public string QuietDependencies { get; set; } = "false";

    public string SilencedDeprecations { get; set; } = string.Empty;

    [Output]
    public ITaskItem[] GeneratedFiles { get; private set; } = [];

    [Output]
    public ITaskItem[] RemovedFiles { get; private set; } = [];

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

        var entries = DiscoverEntries();
        if (Log.HasLoggedErrors)
        {
            return false;
        }

        if (entries.Count == 0)
        {
            Log.LogMessage(MessageImportance.Low, "No Sass entrypoints were found.");
            return true;
        }

        var options = CreateCompilerOptions();
        await using var compiler = new SassCompiler(options);
        var generated = new ConcurrentBag<string>();
        var removed = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            entries,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaxConcurrentCompilations
            },
            async (entry, token) =>
            {
                try
                {
                    Log.LogMessage(MessageImportance.Normal, "Compiling Sass '{0}' to '{1}'.", entry.Source, entry.Target);
                    var request = new SassCompileRequest(new SassFileInput(entry.Source))
                    {
                        OutputStyle = style,
                        GenerateSourceMap = sourceMaps,
                        IncludeSourcesInSourceMap = sourceMaps && includeSources,
                        QuietDependencies = quietDependencies,
                        SilencedDeprecations = SplitList(SilencedDeprecations),
                        LoadPaths = entry.LoadPaths,
                        LogHandler = LogSassEvent
                    };
                    SassCompileResult result = await compiler.CompileAsync(request, token).ConfigureAwait(false);
                    if (WriteResult(entry.Target, result, sourceMaps))
                    {
                        removed.Add(entry.Target + ".map");
                    }
                    generated.Add(entry.Target);
                    if (sourceMaps && result.SourceMap is not null)
                    {
                        generated.Add(entry.Target + ".map");
                    }
                }
                catch (SassCompilationException exception)
                {
                    LogDiagnostic(exception.Message, exception.Span, isError: true);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Log.LogError("Failed to compile Sass '{0}': {1}", entry.Source, exception.Message);
                }
            }).ConfigureAwait(false);

        GeneratedFiles = generated.Order(StringComparer.OrdinalIgnoreCase)
            .Select(static path => (ITaskItem)new TaskItem(path))
            .ToArray();
        string projectDirectory = Path.GetFullPath(ProjectDirectory);
        RemovedFiles = removed.Order(StringComparer.OrdinalIgnoreCase)
            .Select(ITaskItem (path) => new TaskItem(Path.GetRelativePath(projectDirectory, path)))
            .ToArray();
        return !Log.HasLoggedErrors;
    }

    private List<CompilationEntry> DiscoverEntries()
    {
        string projectDirectory = Path.GetFullPath(ProjectDirectory);
        var entries = new List<CompilationEntry>();
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Compilations)
        {
            string source = ResolvePath(projectDirectory, item.ItemSpec);
            string targetMetadata = item.GetMetadata("OutputPath");
            if (string.IsNullOrWhiteSpace(targetMetadata))
            {
                Log.LogError("EmbeddedSass item '{0}' must define OutputPath metadata.", item.ItemSpec);
                continue;
            }

            string target = ResolvePath(projectDirectory, targetMetadata);
            string[] loadPaths = item.GetMetadata("LoadPaths")
                .Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => ResolvePath(projectDirectory, path))
                .ToArray();

            if (File.Exists(source))
            {
                if (!IsSassFile(source) || IsPartial(source))
                {
                    Log.LogError("EmbeddedSass source '{0}' must be a non-partial .scss or .sass file.", source);
                    continue;
                }

                AddEntry(source, target, loadPaths);
                continue;
            }

            if (!Directory.Exists(source))
            {
                Log.LogError("EmbeddedSass source '{0}' does not exist.", source);
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                         .Where(IsSassFile)
                         .Where(static path => !IsPartial(path))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(source, file);
                string output = Path.Combine(target, Path.ChangeExtension(relative, ".css"));
                AddEntry(file, output, loadPaths);
            }
        }

        return entries;

        void AddEntry(string source, string target, IReadOnlyList<string> loadPaths)
        {
            target = Path.GetFullPath(target);
            if (targets.TryGetValue(target, out string? existing))
            {
                Log.LogError("Sass sources '{0}' and '{1}' both map to '{2}'.", existing, source, target);
                return;
            }

            targets.Add(target, source);
            entries.Add(new CompilationEntry(Path.GetFullPath(source), target, loadPaths));
        }
    }

    private bool TryResolveSettings(
        out SassOutputStyle outputStyle,
        out bool generateSourceMap,
        out bool includeSources,
        out bool quietDependencies)
    {
        bool debug = string.Equals(Configuration, "Debug", StringComparison.OrdinalIgnoreCase);
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

    private ValueTask LogSassEvent(SassLogEvent logEvent, CancellationToken cancellationToken)
    {
        if (logEvent.Level == SassLogLevel.Debug)
        {
            Log.LogMessage(MessageImportance.Normal, logEvent.Message);
        }
        else
        {
            LogDiagnostic(logEvent.Message, logEvent.Span, isError: false);
        }

        return ValueTask.CompletedTask;
    }

    private void LogDiagnostic(string message, SassSourceSpan? span, bool isError)
    {
        string? file = span?.Url is { IsFile: true } url ? url.LocalPath : null;
        int line = checked((int)(span?.Start.Line ?? 0)) + 1;
        int column = checked((int)(span?.Start.Column ?? 0)) + 1;
        if (isError)
        {
            Log.LogError(null, null, null, file, line, column, 0, 0, message);
        }
        else
        {
            Log.LogWarning(null, null, null, file, line, column, 0, 0, message);
        }
    }

    private static bool WriteResult(string target, SassCompileResult result, bool sourceMaps)
    {
        string? directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string css = result.Css;
        bool removedSourceMap = false;
        if (sourceMaps && result.SourceMap is not null)
        {
            File.WriteAllText(target + ".map", result.SourceMap);
            css = css.TrimEnd() + Environment.NewLine + $"/*# sourceMappingURL={Path.GetFileName(target)}.map */" + Environment.NewLine;
        }
        else
        {
            removedSourceMap = File.Exists(target + ".map");
            File.Delete(target + ".map");
        }

        File.WriteAllText(target, css);
        return removedSourceMap;
    }

    private static SassCompilerOptions CreateCompilerOptions()
    {
        string operatingSystem = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException(
                        "EmbeddedSass.Net.MsBuild supports Windows, macOS, and Linux.");
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                "EmbeddedSass.Net.MsBuild supports x64 and ARM64 processes.")
        };
        string assemblyDirectory = Path.GetDirectoryName(typeof(CompileSass).Assembly.Location)!;
        string direct = Path.Combine(assemblyDirectory, "EmbeddedSass.Net.Compiler");
        string compilerDirectory = Directory.Exists(direct)
            ? direct
            : Path.Combine(
                assemblyDirectory,
                "runtimes",
                $"{operatingSystem}-{architecture}",
                "native",
                "EmbeddedSass.Net.Compiler");
        string executable = Path.Combine(
            compilerDirectory,
            OperatingSystem.IsWindows() ? "dart.exe" : "dart");
        string snapshot = Path.Combine(compilerDirectory, "sass.snapshot");

        if (!File.Exists(executable) || !File.Exists(snapshot))
        {
            throw new FileNotFoundException(
                $"The bundled Dart Sass compiler was not found under '{compilerDirectory}'.");
        }

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(executable);
            File.SetUnixFileMode(
                executable,
                mode |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute);
        }

        return new SassCompilerOptions
        {
            CompilerPath = executable,
            CompilerArguments = [snapshot]
        };
    }

    private static string ResolvePath(string projectDirectory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(projectDirectory, path));

    private static bool IsSassFile(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".scss", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".sass", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPartial(string path) => Path.GetFileName(path).StartsWith('_');

    private sealed record CompilationEntry(
        string Source,
        string Target,
        IReadOnlyList<string> LoadPaths);
}
