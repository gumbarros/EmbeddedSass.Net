using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Internal.Protocol;

namespace EmbeddedSass.Net.Internal.Process;

internal sealed class SassCompilerConnection : IAsyncDisposable
{
    private readonly CompilerOptionsSnapshot _options;
    private readonly IProcessLauncher _launcher;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private CompilerRuntime? _runtime;
    private int _disposed;

    public SassCompilerConnection(
        CompilerOptionsSnapshot options,
        IProcessLauncher? launcher = null)
    {
        _options = options;
        _launcher = launcher ?? new ProcessLauncher();
    }

    public SassCompilerInfo? Info { get; private set; }

    public async Task<SassCompileResult> CompileAsync(
        MappedCompileRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CompilerRuntime runtime = await GetRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.CompileAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
                _runtime = null;
            }
        }
        finally
        {
            _startGate.Release();
            _startGate.Dispose();
        }
    }

    private async Task<CompilerRuntime> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        CompilerRuntime? current = Volatile.Read(ref _runtime);
        if (current is { IsAvailable: true })
        {
            return current;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            current = _runtime;
            if (current is { IsAvailable: true })
            {
                return current;
            }

            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }

            current = await CompilerRuntime.StartAsync(_options, _launcher, cancellationToken)
                .ConfigureAwait(false);
            _runtime = current;
            Info = current.Info;
            return current;
        }
        finally
        {
            _startGate.Release();
        }
    }
}
