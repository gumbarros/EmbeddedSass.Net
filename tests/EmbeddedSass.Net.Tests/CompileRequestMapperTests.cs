using EmbeddedSass.Functions;
using EmbeddedSass.Importing;
using EmbeddedSass.Internal.Protocol;
using EmbeddedSass.Values;

namespace EmbeddedSass.Tests;

public sealed class CompileRequestMapperTests
{
    [Fact]
    public void FilePathsAreNormalizedBeforeCreatingAPacket()
    {
        var mapped = CompileRequestMapper.Map(
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

        var exception = Assert.Throws<ArgumentException>(
            () => CompileRequestMapper.Map(request));

        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomImportersPrecedeLoadPathsAndUseTheirProtocolKinds()
    {
        var mapped = CompileRequestMapper.Map(
            new SassCompileRequest(new SassStringInput("a {}"))
            {
                Importers = [new ContentImporter(), new FileImporter()],
                LoadPaths = ["styles/shared"]
            });

        var importers = mapped.Message.CompileRequest.Importers;
        Assert.Equal(3, importers.Count);
        Assert.Equal(1u, importers[0].ImporterId);
        Assert.Equal(["http"], importers[0].NonCanonicalScheme);
        Assert.Equal(2u, importers[1].FileImporterId);
        Assert.True(Path.IsPathFullyQualified(importers[2].Path));
    }

    [Fact]
    public void StringInputImporterIsMappedSeparately()
    {
        var input = new SassStringInput("a {}", Url: new Uri("virtual:entry"))
        {
            Importer = new ContentImporter()
        };

        var mapped = CompileRequestMapper.Map(new SassCompileRequest(input));

        Assert.Equal(1u, mapped.Message.CompileRequest.String.Importer.ImporterId);
        Assert.Empty(mapped.Message.CompileRequest.Importers);
    }

    [Fact]
    public void InvalidNonCanonicalSchemeIsRejected()
    {
        var request = new SassCompileRequest(new SassStringInput("a {}"))
        {
            Importers = [new InvalidSchemeImporter()]
        };

        Assert.Throws<ArgumentException>(() => CompileRequestMapper.Map(request));
    }

    [Fact]
    public void UnknownInputSubtypeIsRejected()
    {
        var request = new SassCompileRequest(new UnsupportedInput());

        Assert.Throws<ArgumentException>(() => CompileRequestMapper.Map(request));
    }

    [Fact]
    public void CustomFunctionsAreMappedInRegistrationOrder()
    {
        var first = new SassFunction(
            "first($value)",
            static (_, _) => ValueTask.FromResult<SassValue>(SassNullValue.Instance));
        var second = new SassFunction(
            "second()",
            static (_, _) => ValueTask.FromResult<SassValue>(SassNullValue.Instance));

        var mapped = CompileRequestMapper.Map(
            new SassCompileRequest(new SassStringInput("a {}"))
            {
                Functions = [first, second]
            });

        Assert.Equal(["first($value)", "second()"], mapped.Message.CompileRequest.GlobalFunctions);
        Assert.True(mapped.Functions.TryGet("first", out var registered));
        Assert.Same(first, registered);
    }

    [Fact]
    public void DuplicateCustomFunctionNamesAreRejected()
    {
        var request = new SassCompileRequest(new SassStringInput("a {}"))
        {
            Functions =
            [
                new SassFunction("convert($value)", ReturnNull),
                new SassFunction("convert($value, $unit)", ReturnNull)
            ]
        };

        Assert.Throws<ArgumentException>(() => CompileRequestMapper.Map(request));

        static ValueTask<SassValue> ReturnNull(
            IReadOnlyList<SassValue> _,
            CancellationToken __) => ValueTask.FromResult<SassValue>(SassNullValue.Instance);
    }

    private sealed record UnsupportedInput : SassInput;

    private class ContentImporter : ISassContentImporter
    {
        public virtual IReadOnlyList<string> NonCanonicalSchemes => ["http"];

        public ValueTask<SassCanonicalizeResult?> CanonicalizeAsync(
            SassCanonicalizeContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult<SassCanonicalizeResult?>(null);

        public ValueTask<SassImportResult?> LoadAsync(
            Uri canonicalUrl,
            CancellationToken cancellationToken) => ValueTask.FromResult<SassImportResult?>(null);
    }

    private sealed class FileImporter : ISassFileImporter
    {
        public ValueTask<SassFileImportResult?> FindFileUrlAsync(
            SassFileImportContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult<SassFileImportResult?>(null);
    }

    private sealed class InvalidSchemeImporter : ContentImporter
    {
        public override IReadOnlyList<string> NonCanonicalSchemes => ["HTTP"];
    }
}
