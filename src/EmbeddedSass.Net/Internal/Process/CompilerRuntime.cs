using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading.Channels;
using Google.Protobuf;
using Sass.EmbeddedProtocol;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Diagnostics;
using EmbeddedSass.Net.Internal.Protocol;
using EmbeddedSass.Net.Internal.Transport;

namespace EmbeddedSass.Net.Internal.Process;

internal sealed class CompilerRuntime : IAsyncDisposable
{
    private const uint VersionRequestId = 1;

    private readonly CompilerOptionsSnapshot _options;
    private readonly ICompilerProcess _process;
    private readonly PipeReader _stdout;
    private readonly PipeWriter _stdin;
    private readonly Channel<PendingWrite> _writes;
    private readonly ConcurrentDictionary<uint, CompilationState> _compilations = new();
    private readonly ProtocolIdAllocator _compilationIds = new();
    private readonly SemaphoreSlim _compilationSlots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly BoundedByteTail _standardError;
    private readonly TaskCompletionSource<SassCompilerInfo> _version =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readerLoop;
    private readonly Task _writerLoop;
    private readonly Task _stderrLoop;
    private readonly Task _exitLoop;
    private int _stopping;
    private int _failed;

    private CompilerRuntime(
        CompilerOptionsSnapshot options,
        ICompilerProcess process)
    {
        _options = options;
        _process = process;
        _stdout = PipeReader.Create(process.StandardOutput);
        _stdin = PipeWriter.Create(process.StandardInput);
        _writes = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(options.MaxPendingWrites)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _compilationSlots = new SemaphoreSlim(
            options.MaxConcurrentCompilations,
            options.MaxConcurrentCompilations);
        _standardError = new BoundedByteTail(options.MaxCapturedStderrBytes);

        _readerLoop = ReadLoopAsync();
        _writerLoop = WriteLoopAsync();
        _stderrLoop = ReadStandardErrorAsync();
        _exitLoop = ObserveExitAsync();
    }

    public SassCompilerInfo Info { get; private set; } = null!;

    public bool IsAvailable => !_completion.Task.IsCompleted && Volatile.Read(ref _stopping) == 0;

    public static async Task<CompilerRuntime> StartAsync(
        CompilerOptionsSnapshot options,
        IProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        ICompilerProcess process = launcher.Launch(options);
        var runtime = new CompilerRuntime(options, process);

        try
        {
            var request = new InboundMessage
            {
                VersionRequest = new InboundMessage.Types.VersionRequest { Id = VersionRequestId }
            };
            await runtime.SendAsync(0, request.ToByteArray()).ConfigureAwait(false);

            try
            {
                runtime.Info = await runtime._version.Task
                    .WaitAsync(options.HandshakeTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new SassCompilerException(
                    $"The embedded compiler did not complete its version handshake within {options.HandshakeTimeout}.",
                    standardErrorTail: runtime._standardError.ToString(),
                    innerException: exception);
            }

            ProtocolCompatibility.EnsureCompatible(runtime.Info.ProtocolVersion);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SassCompileResult> CompileAsync(
        MappedCompileRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new SassCompilerException("The embedded compiler connection is not available.");
        }

        await _compilationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        uint compilationId = _compilationIds.Rent(_compilations.ContainsKey);
        var state = new CompilationState(
            compilationId,
            request.LogHandler,
            _options.MaxPendingLogEvents,
            _lifetime.Token,
            () => _compilationSlots.Release());

        if (!_compilations.TryAdd(compilationId, state))
        {
            _compilationSlots.Release();
            throw new SassProtocolException($"Duplicate compilation ID {compilationId} was allocated.");
        }

        try
        {
            await SendAsync(compilationId, request.Message.ToByteArray()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_compilations.TryRemove(compilationId, out CompilationState? removed))
            {
                removed.Fail(exception);
            }

            throw;
        }

        return await state.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            await _completion.Task.ConfigureAwait(false);
            return;
        }

        var disposed = new OperationCanceledException("The Sass compiler connection was disposed.");
        FailPending(disposed);
        _writes.Writer.TryComplete();

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

        _lifetime.Cancel();
        _process.Kill();

        await AwaitIgnoringFailureAsync(_readerLoop).ConfigureAwait(false);
        await AwaitIgnoringFailureAsync(_stderrLoop).ConfigureAwait(false);
        await AwaitIgnoringFailureAsync(_exitLoop).ConfigureAwait(false);
        await _stdout.CompleteAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        _compilationSlots.Dispose();
        _lifetime.Dispose();
        _completion.TrySetResult();
    }

    private async Task SendAsync(uint compilationId, ReadOnlyMemory<byte> payload)
    {
        if (!IsAvailable)
        {
            throw new SassCompilerException("The embedded compiler connection is not available.");
        }

        var pending = new PendingWrite(compilationId, payload);
        await _writes.Writer.WriteAsync(pending, _lifetime.Token).ConfigureAwait(false);
        await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (PendingWrite write in _writes.Reader.ReadAllAsync(_lifetime.Token)
                .ConfigureAwait(false))
            {
                try
                {
                    await PacketCodec.WriteAsync(
                        _stdin,
                        write.CompilationId,
                        write.Payload,
                        _options.MaxPacketBytes,
                        _lifetime.Token).ConfigureAwait(false);
                    write.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    write.Completion.TrySetException(exception);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _stopping) != 0)
        {
        }
        catch (Exception exception)
        {
            failure = WrapIoFailure("writing to", exception);
            Fail(failure);
        }
        finally
        {
            while (_writes.Reader.TryRead(out PendingWrite? pending))
            {
                pending.Completion.TrySetException(
                    failure ?? new OperationCanceledException("The compiler writer stopped."));
            }

            await _stdin.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                ProtocolPacket? packet = await PacketCodec.ReadAsync(
                    _stdout,
                    _options.MaxPacketBytes,
                    _lifetime.Token).ConfigureAwait(false);
                if (packet is null)
                {
                    if (Volatile.Read(ref _stopping) == 0)
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

                    return;
                }

                Dispatch(packet.Value);
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _stopping) != 0)
        {
        }
        catch (Exception exception)
        {
            Fail(exception is SassProtocolException
                ? exception
                : WrapIoFailure("reading from", exception));
        }
    }

    private void Dispatch(ProtocolPacket packet)
    {
        if (packet.CompilationId == uint.MaxValue)
        {
            throw new SassProtocolException("The protocol error sentinel cannot be used as a packet compilation ID.");
        }

        OutboundMessage message;
        try
        {
            message = OutboundMessage.Parser.ParseFrom(packet.Payload.Span);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new SassProtocolException("The compiler sent malformed protobuf data.", exception);
        }

        if (message.MessageCase == OutboundMessage.MessageOneofCase.None)
        {
            throw new SassProtocolException("The compiler sent an outbound message without a value.");
        }

        if (message.MessageCase == OutboundMessage.MessageOneofCase.Error)
        {
            throw new SassProtocolException(
                $"Compiler protocol error {message.Error.Type} for request {message.Error.Id}: {message.Error.Message}");
        }

        if (packet.CompilationId == 0)
        {
            DispatchVersion(message);
            return;
        }

        if (!_compilations.TryGetValue(packet.CompilationId, out CompilationState? state))
        {
            throw new SassProtocolException(
                $"The compiler sent a message for unknown compilation ID {packet.CompilationId}.");
        }

        switch (message.MessageCase)
        {
            case OutboundMessage.MessageOneofCase.LogEvent:
                if (!state.TryAddLog(message.LogEvent))
                {
                    throw new SassProtocolException(
                        $"Compilation {packet.CompilationId} exceeded the pending log event limit.");
                }

                break;

            case OutboundMessage.MessageOneofCase.CompileResponse:
                CompilationState.Validate(message.CompileResponse);
                if (!_compilations.TryRemove(packet.CompilationId, out CompilationState? completed) ||
                    !ReferenceEquals(completed, state))
                {
                    throw new SassProtocolException(
                        $"Compilation {packet.CompilationId} received a duplicate terminal response.");
                }

                state.Complete(message.CompileResponse);
                break;

            case OutboundMessage.MessageOneofCase.CanonicalizeRequest:
            case OutboundMessage.MessageOneofCase.ImportRequest:
            case OutboundMessage.MessageOneofCase.FileImportRequest:
            case OutboundMessage.MessageOneofCase.FunctionCallRequest:
                throw new SassProtocolException(
                    $"The compiler sent unsupported callback {message.MessageCase} for compilation {packet.CompilationId}.");

            default:
                throw new SassProtocolException(
                    $"The compiler sent {message.MessageCase} with nonzero compilation ID {packet.CompilationId}.");
        }
    }

    private void DispatchVersion(OutboundMessage message)
    {
        if (message.MessageCase != OutboundMessage.MessageOneofCase.VersionResponse)
        {
            throw new SassProtocolException(
                $"The compiler sent {message.MessageCase} with reserved compilation ID 0.");
        }

        OutboundMessage.Types.VersionResponse response = message.VersionResponse;
        if (response.Id != VersionRequestId)
        {
            throw new SassProtocolException(
                $"Version response ID {response.Id} does not match request ID {VersionRequestId}.");
        }

        var info = new SassCompilerInfo(
            response.CompilerVersion,
            response.ImplementationName,
            response.ImplementationVersion,
            response.ProtocolVersion);

        if (!_version.TrySetResult(info))
        {
            throw new SassProtocolException("The compiler sent a duplicate version response.");
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = await _process.StandardError
                    .ReadAsync(buffer, _lifetime.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                _standardError.Append(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _stopping) != 0)
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
            if (Volatile.Read(ref _stopping) == 0)
            {
                Fail(new SassCompilerException(
                    "The embedded compiler exited unexpectedly.",
                    _process.ExitCode,
                    _standardError.ToString()));
            }
        }
        catch (Exception exception) when (Volatile.Read(ref _stopping) == 0)
        {
            Fail(WrapIoFailure("waiting for", exception));
        }
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0 || Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        _version.TrySetException(exception);
        FailPending(exception);
        _writes.Writer.TryComplete(exception);
        _process.Kill();
        _completion.TrySetResult();
    }

    private void FailPending(Exception exception)
    {
        foreach ((uint compilationId, CompilationState _) in _compilations)
        {
            if (_compilations.TryRemove(compilationId, out CompilationState? state))
            {
                state.Fail(exception);
            }
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
        }
    }

    private sealed record PendingWrite(uint CompilationId, ReadOnlyMemory<byte> Payload)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
