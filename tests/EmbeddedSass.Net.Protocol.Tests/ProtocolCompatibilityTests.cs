using EmbeddedSass.Diagnostics;
using EmbeddedSass.Internal.Protocol;

namespace EmbeddedSass.Protocol.Tests;

public sealed class ProtocolCompatibilityTests
{
    [Theory]
    [InlineData("3.2.0")]
    [InlineData("3.9.1")]
    public void AcceptsCompatibleVersions(string version)
    {
        ProtocolCompatibility.EnsureCompatible(version);
    }

    [Theory]
    [InlineData("2.99.0")]
    [InlineData("3.1.9")]
    [InlineData("4.0.0")]
    [InlineData("pending")]
    public void RejectsIncompatibleOrInvalidVersions(string version)
    {
        Assert.Throws<SassProtocolException>(
            () => ProtocolCompatibility.EnsureCompatible(version));
    }
}
