using System.Collections.Concurrent;
using EmbeddedSass;
using EmbeddedSass.Compilation;
using EmbeddedSass.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace EmbeddedSass.MsBuild;

internal sealed class SassCompilationExecutor : IAsyncDisposable
{
    private readonly SassCompiler _compiler;
    private readonly TaskLoggingHelper _log;
    private readonly int _maxConcurrentCompilations;
    private readonly IReadOnlyList<string> _silencedDeprecations;
    private readonly CompilationCache _cache;

    public SassCompilationExecutor(
        SassCompilerOptions options,
        TaskLoggingHelper log,
        IReadOnlyList<string> silencedDeprecations,
        string cacheFile)
    {
        _compiler = new SassCompiler(options);
        _log = log;
        _maxConcurrentCompilations = options.MaxConcurrentCompilations;
        _silencedDeprecations = silencedDeprecations;
        _cache = CompilationCache.Load(cacheFile, options);
    }

    public async Task<CompilationOutputs> ExecuteAsync(
        IEnumerable<CompilationEntry> entries,
        SassOutputStyle style,
        bool sourceMaps,
        bool includeSources,
        bool quietDependencies,
        CancellationToken cancellationToken)
    {
        var compilationEntries = entries.ToArray();
        _cache.Retain(compilationEntries);
        var generated = new ConcurrentBag<string>();
        var removed = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            compilationEntries,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxConcurrentCompilations
            },
            async (entry, token) =>
            {
                try
                {
                    string settingsFingerprint = CompilationCache.CreateSettingsFingerprint(
                        entry,
                        style,
                        sourceMaps,
                        includeSources,
                        quietDependencies,
                        _silencedDeprecations);
                    if (_cache.IsFresh(entry, settingsFingerprint, sourceMaps))
                    {
                        _log.LogMessage(
                            MessageImportance.Low,
                            "Skipping unchanged Sass '{0}'.",
                            entry.Source);
                        AddGeneratedFiles(entry.Target, sourceMaps, generated);
                        return;
                    }

                    _log.LogMessage(
                        MessageImportance.Normal,
                        "Compiling Sass '{0}' to '{1}'.",
                        entry.Source,
                        entry.Target);
                    var request = new SassCompileRequest(new SassFileInput(entry.Source))
                    {
                        OutputStyle = style,
                        GenerateSourceMap = sourceMaps,
                        IncludeSourcesInSourceMap = sourceMaps && includeSources,
                        QuietDependencies = quietDependencies,
                        SilencedDeprecations = _silencedDeprecations,
                        LoadPaths = entry.LoadPaths,
                        LogHandler = LogSassEvent
                    };
                    SassCompileResult result = await _compiler.CompileAsync(request, token).ConfigureAwait(false);
                    if (CompilationOutputWriter.Write(entry.Target, result, sourceMaps))
                    {
                        removed.Add(entry.Target + ".map");
                    }

                    AddGeneratedFiles(entry.Target, sourceMaps && result.SourceMap is not null, generated);
                    _cache.Record(entry, settingsFingerprint, result);
                }
                catch (SassCompilationException exception)
                {
                    LogDiagnostic(exception.Message, exception.Span, isError: true);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _log.LogError("Failed to compile Sass '{0}': {1}", entry.Source, exception.Message);
                }
            }).ConfigureAwait(false);

        _cache.Save();

        return new CompilationOutputs(
            generated.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            removed.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public ValueTask DisposeAsync() => _compiler.DisposeAsync();

    private static void AddGeneratedFiles(
        string target,
        bool sourceMap,
        ConcurrentBag<string> generated)
    {
        generated.Add(target);
        if (sourceMap)
        {
            generated.Add(target + ".map");
        }
    }

    private ValueTask LogSassEvent(SassLogEvent logEvent, CancellationToken cancellationToken)
    {
        if (logEvent.Level == SassLogLevel.Debug)
        {
            _log.LogMessage(MessageImportance.Normal, logEvent.Message);
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
            _log.LogError(null, null, null, file, line, column, 0, 0, message);
        }
        else
        {
            _log.LogWarning(null, null, null, file, line, column, 0, 0, message);
        }
    }
}

internal sealed record CompilationOutputs(
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> RemovedFiles);
