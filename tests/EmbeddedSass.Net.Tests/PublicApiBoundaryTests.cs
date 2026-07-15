using System.Reflection;

namespace EmbeddedSass.Net.Tests;

public sealed class PublicApiBoundaryTests
{
    [Fact]
    public void GeneratedProtocolTypesAreNotPublic()
    {
        Assembly assembly = typeof(SassCompiler).Assembly;

        Assert.DoesNotContain(
            assembly.GetExportedTypes(),
            type => type.Namespace?.StartsWith("Sass.EmbeddedProtocol", StringComparison.Ordinal) == true);
    }
}
