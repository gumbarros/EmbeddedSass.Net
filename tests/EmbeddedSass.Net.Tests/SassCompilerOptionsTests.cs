using EmbeddedSass.Compilation;
using EmbeddedSass.Diagnostics;

namespace EmbeddedSass.Tests;

public sealed class SassCompilerOptionsTests
{
    [Fact]
    public void RelativeCompilerPathIsRejectedWithoutStartingAProcess()
    {
        var options = new SassCompilerOptions { CompilerPath = "dart-sass" };

        var exception = Assert.Throws<ArgumentException>(
            () => new SassCompiler(options));

        Assert.Contains("fully qualified", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstructorDoesNotRequireCompilerToExist()
    {
        var options = new SassCompilerOptions
        {
            CompilerPath = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "missing", "dart-sass")
        };

        var compiler = new SassCompiler(options);

        Assert.Null(compiler.CompilerInfo);
    }

    [Fact]
    public async Task MissingExecutableFailsOnFirstCompilation()
    {
        var options = new SassCompilerOptions
        {
            CompilerPath = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "missing", "dart-sass")
        };
        await using var compiler = new SassCompiler(options);

        var exception = await Assert.ThrowsAsync<SassCompilerException>(
            () => compiler.CompileStringAsync("a {}"));

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
