namespace EmbeddedSass.Net.Diagnostics;

public sealed class SassProtocolException : Exception
{
    public SassProtocolException(string message)
        : base(message)
    {
    }

    public SassProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
