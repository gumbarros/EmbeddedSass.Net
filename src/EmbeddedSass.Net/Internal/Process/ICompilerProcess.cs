namespace EmbeddedSass.Internal.Process;

internal interface ICompilerProcess : IAsyncDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    int? ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    void Kill();
}
