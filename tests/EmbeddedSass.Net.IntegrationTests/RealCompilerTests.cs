using System.Runtime.InteropServices;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Compiler;
using EmbeddedSass.Net.Diagnostics;
using EmbeddedSass.Net.Importing;

namespace EmbeddedSass.Net.IntegrationTests;

public sealed class RealCompilerTests
{
    [Fact]
    public void FindsCompilerFilesFlattenedIntoPublishDirectory()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "dart");
            var snapshot = Path.Combine(directory, "sass.snapshot");
            File.WriteAllText(executable, string.Empty);
            File.WriteAllText(snapshot, string.Empty);

            var files = BundledDartSass.FindCompilerFiles(
                directory,
                "linux-x64",
                "dart");

            Assert.Equal(executable, files.Executable);
            Assert.Equal(snapshot, files.Snapshot);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(SassSyntax.Scss, "$color: red; a { color: $color; }")]
    [InlineData(SassSyntax.Indented, "$color: red\na\n  color: $color")]
    [InlineData(SassSyntax.Css, "a { color: red; }")]
    public async Task CompilesEveryStringSyntax(SassSyntax syntax, string source)
    {
        await using var compiler = CreateCompiler();

        var result = await compiler.CompileAsync(
            new SassCompileRequest(new SassStringInput(source, syntax)));

        Assert.Contains("color: red", result.Css, StringComparison.Ordinal);
        Assert.Equal(BundledDartSass.Version, compiler.CompilerInfo?.ImplementationVersion);
        Assert.Equal("3.2.0", compiler.CompilerInfo?.ProtocolVersion);
    }

    [Fact]
    public async Task CompilesFileWithLoadPathAndSourceMap()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var loadPath = Path.Combine(directory, "shared");
            Directory.CreateDirectory(loadPath);
            var entryPath = Path.Combine(directory, "entry.scss");
            var colorsPath = Path.Combine(loadPath, "_colors.scss");
            await File.WriteAllTextAsync(colorsPath, "$accent: rebeccapurple;");
            await File.WriteAllTextAsync(
                entryPath,
                "@use 'colors'; a { color: colors.$accent; }");

            await using var compiler = CreateCompiler();
            var result = await compiler.CompileAsync(
                new SassCompileRequest(new SassFileInput(entryPath))
                {
                    GenerateSourceMap = true,
                    IncludeSourcesInSourceMap = true,
                    LoadPaths = [loadPath]
                });

            Assert.Contains("rebeccapurple", result.Css, StringComparison.Ordinal);
            Assert.Contains("sourcesContent", result.SourceMap, StringComparison.Ordinal);
            Assert.Contains(result.LoadedUrls, url => url.LocalPath == entryPath);
            Assert.Contains(result.LoadedUrls, url => url.LocalPath == colorsPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompilesWithContentImporter()
    {
        await using var compiler = CreateCompiler();
        var result = await compiler.CompileAsync(
            new SassCompileRequest(new SassStringInput(
                "@use 'theme'; a { color: theme.$accent; }"))
            {
                Importers = [new ThemeContentImporter()]
            });

        Assert.Contains("rebeccapurple", result.Css, StringComparison.Ordinal);
        Assert.Contains(new Uri("virtual:theme"), result.LoadedUrls);
    }

    [Fact]
    public async Task StringInputImporterResolvesRelativeImports()
    {
        var input = new SassStringInput(
            "@use 'theme'; a { color: theme.$accent; }",
            Url: new Uri("virtual:entry"))
        {
            Importer = new ThemeContentImporter()
        };
        await using var compiler = CreateCompiler();

        var result = await compiler.CompileAsync(new SassCompileRequest(input));

        Assert.Contains("rebeccapurple", result.Css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileImporterLeavesPartialResolutionToSass()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "_tokens.scss"),
                "$spacing: 12px;");

            await using var compiler = CreateCompiler();
            var result = await compiler.CompileAsync(
                new SassCompileRequest(new SassStringInput(
                    "@use 'tokens'; a { padding: tokens.$spacing; }"))
                {
                    Importers = [new DirectoryFileImporter(directory)]
                });

            Assert.Contains("padding: 12px", result.Css, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SupportsCompressedOutput()
    {
        await using var compiler = CreateCompiler();
        var result = await compiler.CompileAsync(
            new SassCompileRequest(new SassStringInput(".item { color: red; }"))
            {
                OutputStyle = SassOutputStyle.Compressed
            });

        Assert.Equal(".item{color:red}", result.Css.Trim());
    }

    [Fact]
    public async Task DeliversWarningsAndDebugMessages()
    {
        var logs = new List<SassLogEvent>();
        await using var compiler = CreateCompiler();
        await compiler.CompileAsync(
            new SassCompileRequest(new SassStringInput("@debug 'value'; @warn 'careful';"))
            {
                LogHandler = (logEvent, _) =>
                {
                    logs.Add(logEvent);
                    return ValueTask.CompletedTask;
                }
            });

        Assert.Contains(logs, log => log.Level == SassLogLevel.Debug && log.Message.Contains("value"));
        Assert.Contains(logs, log => log.Level == SassLogLevel.Warning && log.Message.Contains("careful"));
    }

    [Fact]
    public async Task SassErrorThrowsCompilationException()
    {
        await using var compiler = CreateCompiler();

        var exception =
            await Assert.ThrowsAsync<SassCompilationException>(() =>
                compiler.CompileStringAsync("a { color: $missing; }"));

        Assert.Contains("Undefined variable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(exception.FormattedMessage);
        Assert.NotNull(exception.Span);
    }

    [Fact]
    public async Task ConcurrentCompilationsShareOneProcess()
    {
        await using var compiler = CreateCompiler(maximumConcurrentCompilations: 8);
        var compilations = Enumerable.Range(1, 24)
            .Select(index => compiler.CompileStringAsync(
                $".item-{index} {{ order: {index}; }}"))
            .ToArray();

        var results = await Task.WhenAll(compilations);

        for (var index = 1; index <= results.Length; index++)
        {
            Assert.Contains($"order: {index}", results[index - 1].Css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CanceledCompilationIsDrainedAndConnectionRemainsUsable()
    {
        await using var compiler = CreateCompiler(maximumConcurrentCompilations: 1);
        await compiler.CompileStringAsync("a { color: red; }");

        using var cancellation = new CancellationTokenSource();
        var longCompilation = compiler.CompileStringAsync(
            "@for $i from 1 through 100000 { .item-#{$i} { order: $i; } }",
            cancellationToken: cancellation.Token);
        await Task.Delay(10, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => longCompilation);

        // ReSharper disable once MethodSupportsCancellation
        var next = await compiler.CompileStringAsync("a { color: green; }")
            // ReSharper disable once MethodSupportsCancellation
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("green", next.Css, StringComparison.Ordinal);
    }

    private static SassCompiler CreateCompiler(int maximumConcurrentCompilations = 4)
    {
        var options = new SassCompilerOptions
        {
            MaxConcurrentCompilations = maximumConcurrentCompilations
        }.UseBundledDartSass();
        return new SassCompiler(options);
    }

    private static bool IsSupportedPlatform() =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"embedded-sass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThemeContentImporter : ISassContentImporter
    {
        public ValueTask<SassCanonicalizeResult?> CanonicalizeAsync(
            SassCanonicalizeContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                context.Url.ToString() is "theme" or "virtual:theme"
                    ? new SassCanonicalizeResult(new Uri("virtual:theme"), ContainingUrlUnused: true)
                    : null);

        public ValueTask<SassImportResult?> LoadAsync(
            Uri canonicalUrl,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<SassImportResult?>(
                canonicalUrl == new Uri("virtual:theme")
                    ? new("$accent: rebeccapurple;")
                    : null);
    }

    private sealed class DirectoryFileImporter(string directory) : ISassFileImporter
    {
        public ValueTask<SassFileImportResult?> FindFileUrlAsync(
            SassFileImportContext context,
            CancellationToken cancellationToken)
        {
            if (context.Url.ToString() != "tokens")
            {
                return ValueTask.FromResult<SassFileImportResult?>(null);
            }

            return ValueTask.FromResult<SassFileImportResult?>(
                new(new Uri(Path.Combine(directory, "tokens")), ContainingUrlUnused: true));
        }
    }
}
