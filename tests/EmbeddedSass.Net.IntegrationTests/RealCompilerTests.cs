using System.Runtime.InteropServices;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Compiler;
using EmbeddedSass.Net.Diagnostics;

namespace EmbeddedSass.Net.IntegrationTests;

public sealed class RealCompilerTests
{
    [Theory]
    [InlineData(SassSyntax.Scss, "$color: red; a { color: $color; }")]
    [InlineData(SassSyntax.Indented, "$color: red\na\n  color: $color")]
    [InlineData(SassSyntax.Css, "a { color: red; }")]
    public async Task CompilesEveryStringSyntax(SassSyntax syntax, string source)
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        await using SassCompiler compiler = CreateCompiler();

        SassCompileResult result = await compiler.CompileAsync(
            new SassCompileRequest(new SassStringInput(source, syntax)));

        Assert.Contains("color: red", result.Css, StringComparison.Ordinal);
        Assert.Equal(BundledDartSass.Version, compiler.CompilerInfo?.ImplementationVersion);
        Assert.Equal("3.2.0", compiler.CompilerInfo?.ProtocolVersion);
    }

    [Fact]
    public async Task CompilesFileWithLoadPathAndSourceMap()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        string directory = CreateTemporaryDirectory();
        try
        {
            string loadPath = Path.Combine(directory, "shared");
            Directory.CreateDirectory(loadPath);
            string entryPath = Path.Combine(directory, "entry.scss");
            string colorsPath = Path.Combine(loadPath, "_colors.scss");
            await File.WriteAllTextAsync(colorsPath, "$accent: rebeccapurple;");
            await File.WriteAllTextAsync(
                entryPath,
                "@use 'colors'; a { color: colors.$accent; }");

            await using SassCompiler compiler = CreateCompiler();
            SassCompileResult result = await compiler.CompileAsync(
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
    public async Task SupportsCompressedOutput()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        await using SassCompiler compiler = CreateCompiler();
        SassCompileResult result = await compiler.CompileAsync(
            new SassCompileRequest(new SassStringInput(".item { color: red; }"))
            {
                OutputStyle = SassOutputStyle.Compressed
            });

        Assert.Equal(".item{color:red}", result.Css.Trim());
    }

    [Fact]
    public async Task DeliversWarningsAndDebugMessages()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        var logs = new List<SassLogEvent>();
        await using SassCompiler compiler = CreateCompiler();
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
        if (!IsSupportedPlatform())
        {
            return;
        }

        await using SassCompiler compiler = CreateCompiler();

        SassCompilationException exception = await Assert.ThrowsAsync<SassCompilationException>(
            () => compiler.CompileStringAsync("a { color: $missing; }"));

        Assert.Contains("Undefined variable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(exception.FormattedMessage);
        Assert.NotNull(exception.Span);
    }

    [Fact]
    public async Task ConcurrentCompilationsShareOneProcess()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        await using SassCompiler compiler = CreateCompiler(maximumConcurrentCompilations: 8);
        Task<SassCompileResult>[] compilations = Enumerable.Range(1, 24)
            .Select(index => compiler.CompileStringAsync(
                $".item-{index} {{ order: {index}; }}"))
            .ToArray();

        SassCompileResult[] results = await Task.WhenAll(compilations);

        for (int index = 1; index <= results.Length; index++)
        {
            Assert.Contains($"order: {index}", results[index - 1].Css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CanceledCompilationIsDrainedAndConnectionRemainsUsable()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        await using SassCompiler compiler = CreateCompiler(maximumConcurrentCompilations: 1);
        await compiler.CompileStringAsync("a { color: red; }");

        using var cancellation = new CancellationTokenSource();
        Task<SassCompileResult> longCompilation = compiler.CompileStringAsync(
            "@for $i from 1 through 100000 { .item-#{$i} { order: $i; } }",
            cancellationToken: cancellation.Token);
        await Task.Delay(10);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => longCompilation);

        SassCompileResult next = await compiler.CompileStringAsync("a { color: green; }")
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
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"embedded-sass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
