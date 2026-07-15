namespace EmbeddedSass.Net.Diagnostics;

public sealed class SassCompilerException : Exception
{
    public SassCompilerException(
        string message,
        int? exitCode = null,
        string? standardErrorTail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        StandardErrorTail = standardErrorTail;
    }

    public int? ExitCode { get; }

    public string? StandardErrorTail { get; }
}
