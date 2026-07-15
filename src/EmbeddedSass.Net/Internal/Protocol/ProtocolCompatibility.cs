using EmbeddedSass.Net.Diagnostics;

namespace EmbeddedSass.Net.Internal.Protocol;

internal static class ProtocolCompatibility
{
    internal const int SupportedMajor = 3;
    internal const int MinimumMinor = 2;

    public static void EnsureCompatible(string version)
    {
        if (!Version.TryParse(version, out Version? parsed) || parsed.Build < 0)
        {
            throw new SassProtocolException(
                $"The compiler returned invalid protocol version '{version}'.");
        }

        if (parsed.Major != SupportedMajor || parsed.Minor < MinimumMinor)
        {
            throw new SassProtocolException(
                $"Embedded Sass Protocol {version} is incompatible. " +
                $"EmbeddedSass.Net supports major {SupportedMajor}, minor {MinimumMinor} or newer.");
        }
    }
}
