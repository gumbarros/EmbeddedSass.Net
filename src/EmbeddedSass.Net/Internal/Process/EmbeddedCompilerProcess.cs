using EmbeddedSass.Compilation;
using EmbeddedSass.Diagnostics;
using EmbeddedSass.Internal.Protocol;
using EmbeddedSass.Internal.Transport;
using Google.Protobuf;

namespace EmbeddedSass.Internal.Process;

internal sealed class EmbeddedCompilerProcess : IAsyncDisposable
{
    private readonly CompilerOptionsSnapshot _options;
    private readonly ICompilerProcess _process;
    private readonly ProtocolTransport _transport;
    private readonly CompilationDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly BoundedByteTail _standardError;
    private readonly TaskCompletionSource _terminated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _disposeGate = new();
    private readonly Task _readerLoop;
    private readonly Task _writerLoop;
    private readonly Task _stderrLoop;
    private readonly Task _exitLoop;
    private Task? _disposeTask;

    private EmbeddedCompilerProcess(
        CompilerOptionsSnapshot options,
        ICompilerProcess process)
    {
        _options = options;
        _process = process;
        _transport = new ProtocolTransport(
            process.StandardInput,
            process.StandardOutput,
            options.MaxPendingWrites,
            options.MaxPacketBytes,
            _lifetime.Token);
        _dispatcher = new CompilationDispatcher(
            options.MaxConcurrentCompilations,
            options.MaxPendingLogEvents,
            _lifetime.Token,
            SendAsync,
            Fail);
        _standardError = new BoundedByteTail(options.MaxCapturedStderrBytes);

        _readerLoop = ReadProtocolAsync();
        _writerLoop = ObserveWriterAsync();
        _stderrLoop = ReadStandardErrorAsync();
        _exitLoop = ObserveExitAsync();
    }

    public SassCompilerInfo? Info { get; private set; }

    public bool IsAvailable => !_terminated.Task.IsCompleted;

    public static async Task<EmbeddedCompilerProcess> StartAsync(
        CompilerOptionsSnapshot options,
        IProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        var process = launcher.Launch(options);
        var compiler = new EmbeddedCompilerProcess(options, process);

        try
        {
            await compiler.SendAsync(
                0,
                CompilationDispatcher.CreateVersionRequest()).ConfigureAwait(false);

            try
            {
                compiler.Info = await compiler._dispatcher.Version
                    .WaitAsync(options.HandshakeTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new SassCompilerException(
                    $"The embedded compiler did not complete its version handshake within {options.HandshakeTimeout}.",
                    standardErrorTail: compiler._standardError.ToString(),
                    innerException: exception);
            }

            ProtocolCompatibility.EnsureCompatible(compiler.Info.ProtocolVersion);
            return compiler;
        }
        catch
        {
            await compiler.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SassCompileResult> CompileAsync(
        MappedCompileRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        var operation = await _dispatcher
            .RegisterAsync(request.LogHandler, cancellationToken, request.Importers)
            .ConfigureAwait(false);

        try
        {
            await SendAsync(operation.CompilationId, request.Message)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _dispatcher.Fail(operation, exception);
            throw;
        }

        return await operation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeOnceAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeOnceAsync()
    {
        _terminated.TrySetResult();
        await DisposeCoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        var disposed = new OperationCanceledException("The Sass compiler connection was disposed.");
        _dispatcher.FailAll(disposed);
        _transport.CompleteWrites();

        try
        {
            await _writerLoop.WaitAsync(_options.ShutdownGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        try
        {
            await _process.WaitForExitAsync()
                .WaitAsync(_options.ShutdownGracePeriod)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _process.Kill();
        }

        await _lifetime.CancelAsync();
        _process.Kill();

        await AwaitIgnoringFailureAsync(_writerLoop).ConfigureAwait(false);
        await AwaitIgnoringFailureAsync(_readerLoop).ConfigureAwait(false);
        await AwaitIgnoringFailureAsync(_stderrLoop).ConfigureAwait(false);
        await AwaitIgnoringFailureAsync(_exitLoop).ConfigureAwait(false);
        await _transport.CompleteReaderAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        _dispatcher.Dispose();
        _lifetime.Dispose();
    }

    private async Task SendAsync(uint compilationId, IMessage message)
    {
        ThrowIfUnavailable();
        await _transport.SendAsync(compilationId, message).ConfigureAwait(false);
    }

    private async Task ReadProtocolAsync()
    {
        try
        {
            await _transport.ReadAllAsync(_dispatcher.Dispatch).ConfigureAwait(false);
            if (IsAvailable)
            {
                if (_process.ExitCode is not null)
                {
                    await AwaitIgnoringFailureAsync(_stderrLoop).ConfigureAwait(false);
                }

                Fail(new SassCompilerException(
                    "The embedded compiler closed its protocol output unexpectedly.",
                    _process.ExitCode,
                    _standardError.ToString()));
            }
        }
        catch (OperationCanceledException) when (!IsAvailable)
        {
        }
        catch (Exception exception)
        {
            Fail(exception is SassProtocolException
                ? exception
                : WrapIoFailure("reading from", exception));
        }
    }

    private async Task ObserveWriterAsync()
    {
        try
        {
            await _transport.WriterCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!IsAvailable)
        {
        }
        catch (Exception exception)
        {
            Fail(WrapIoFailure("writing to", exception));
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await _process.StandardError
                    .ReadAsync(buffer, _lifetime.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                _standardError.Append(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (!IsAvailable)
        {
        }
        catch (Exception exception)
        {
            Fail(WrapIoFailure("reading stderr from", exception));
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await AwaitIgnoringFailureAsync(_stderrLoop).ConfigureAwait(false);
            if (IsAvailable)
            {
                Fail(new SassCompilerException(
                    "The embedded compiler exited unexpectedly.",
                    _process.ExitCode,
                    _standardError.ToString()));
            }
        }
        catch (Exception exception) when (IsAvailable)
        {
            Fail(WrapIoFailure("waiting for", exception));
        }
    }

    private void Fail(Exception exception)
    {
        if (!_terminated.TrySetResult())
        {
            return;
        }

        _dispatcher.FailAll(exception);
        _transport.CompleteWrites(exception);
        _process.Kill();
    }

    private void ThrowIfUnavailable()
    {
        if (!IsAvailable)
        {
            throw new SassCompilerException("The embedded compiler connection is not available.");
        }
    }

    private SassCompilerException WrapIoFailure(string operation, Exception exception) =>
        new(
            $"An I/O failure occurred while {operation} the embedded compiler.",
            _process.ExitCode,
            _standardError.ToString(),
            exception);

    private static async Task AwaitIgnoringFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
    }
}
