using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Internal.Protocol;

namespace EmbeddedSass.Net.Internal.Process;

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
        var runtime = await GetRuntimeAsync(cancellationToken).ConfigureAwait(false);
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

            _disposed = true;
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
                _runtime = null;
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
            var current = _runtime;
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
