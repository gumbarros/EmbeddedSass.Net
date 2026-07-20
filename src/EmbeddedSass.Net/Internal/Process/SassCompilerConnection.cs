using EmbeddedSass.Compilation;
using EmbeddedSass.Internal.Protocol;

namespace EmbeddedSass.Internal.Process;

internal sealed class SassCompilerConnection(
    CompilerOptionsSnapshot options,
    IProcessLauncher? launcher = null)
    : IAsyncDisposable
{
    private readonly IProcessLauncher _launcher = launcher ?? new ProcessLauncher();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private EmbeddedCompilerProcess? _runtime;
    private bool _disposed;

    public SassCompilerInfo? Info { get; private set; }

    public async Task<SassCompileResult> CompileAsync(
        MappedCompileRequest request,
        CancellationToken cancellationToken)
    {
        var runtime = Volatile.Read(ref _runtime);
        if (runtime is not { IsAvailable: true })
        {
            runtime = await GetRuntimeAsync(cancellationToken).ConfigureAwait(false);
        }

        return await runtime.CompileAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var runtime = Interlocked.Exchange(ref _runtime, null);
            _disposed = true;
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<EmbeddedCompilerProcess> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = Volatile.Read(ref _runtime);
            if (current is { IsAvailable: true })
            {
                return current;
            }

            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }

            current = await EmbeddedCompilerProcess.StartAsync(options, _launcher, cancellationToken)
                .ConfigureAwait(false);
            Info = current.Info;
            Volatile.Write(ref _runtime, current);
            return current;
        }
        finally
        {
            _startGate.Release();
        }
    }
}
