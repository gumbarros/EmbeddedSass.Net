using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Internal.Protocol;

namespace EmbeddedSass.Net.Tests;

public sealed class CompileRequestMapperTests
{
    [Fact]
    public void FilePathsAreNormalizedBeforeCreatingAPacket()
    {
        MappedCompileRequest mapped = CompileRequestMapper.Map(
            new SassCompileRequest(new SassFileInput("styles/main.scss"))
            {
                LoadPaths = ["styles/shared"]
            });

        Assert.True(Path.IsPathFullyQualified(mapped.Message.CompileRequest.Path));
        Assert.True(Path.IsPathFullyQualified(
            mapped.Message.CompileRequest.Importers[0].Path));
    }

    [Fact]
    public void RelativeStringUrlIsRejected()
    {
        var request = new SassCompileRequest(
            new SassStringInput("a {}", Url: new Uri("relative.scss", UriKind.Relative)));

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => CompileRequestMapper.Map(request));

        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownInputSubtypeIsRejected()
    {
        var request = new SassCompileRequest(new UnsupportedInput());

        Assert.Throws<ArgumentException>(() => CompileRequestMapper.Map(request));
    }

    private sealed record UnsupportedInput : SassInput;
}
