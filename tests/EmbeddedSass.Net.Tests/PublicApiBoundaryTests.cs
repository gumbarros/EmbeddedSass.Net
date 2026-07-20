using System.Reflection;

namespace EmbeddedSass.Tests;

public sealed class PublicApiBoundaryTests
{
    [Fact]
    public void GeneratedProtocolTypesAreNotPublic()
    {
        var assembly = typeof(SassCompiler).Assembly;

        Assert.DoesNotContain(
            assembly.GetExportedTypes(),
            type => type.Namespace?.StartsWith("Sass.EmbeddedProtocol", StringComparison.Ordinal) == true);
    }
}
