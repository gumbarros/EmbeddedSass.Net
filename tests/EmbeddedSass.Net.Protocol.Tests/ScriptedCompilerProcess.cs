using System.Collections.Concurrent;
using System.IO.Pipelines;
using EmbeddedSass.Net.Internal.Process;
using EmbeddedSass.Net.Internal.Protocol;
using EmbeddedSass.Net.Internal.Transport;
using Google.Protobuf;
using Sass.EmbeddedProtocol;

namespace EmbeddedSass.Net.Protocol.Tests;

internal sealed class ScriptedProcessLauncher : IProcessLauncher
{
    private readonly ConcurrentQueue<ScriptDefinition> _scripts = new();

    public int LaunchCount { get; private set; }

    public void Enqueue(
        Func<ScriptedCompilerEndpoint, CancellationToken, Task> script,
        int exitCode = 0)
    {
        _scripts.Enqueue(new ScriptDefinition(script, exitCode));
    }

    public ICompilerProcess Launch(CompilerOptionsSnapshot options)
    {
        if (!_scripts.TryDequeue(out ScriptDefinition? definition))
        {
            throw new InvalidOperationException("No scripted compiler process remains.");
        }

        LaunchCount++;
        return new ScriptedCompilerProcess(definition.Script, definition.ExitCode);
    }

    private sealed record ScriptDefinition(
        Func<ScriptedCompilerEndpoint, CancellationToken, Task> Script,
        int ExitCode);
}

internal sealed class GatedDisposalProcessLauncher : IProcessLauncher
{
    private readonly ScriptedProcessLauncher _inner = new();
    private readonly TaskCompletionSource _allowDisposal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource DisposalStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Enqueue(Func<ScriptedCompilerEndpoint, CancellationToken, Task> script) =>
        _inner.Enqueue(script);

    public void AllowDisposal() => _allowDisposal.TrySetResult();

    public ICompilerProcess Launch(CompilerOptionsSnapshot options) =>
        new GatedDisposalProcess(
            _inner.Launch(options),
            DisposalStarted,
            _allowDisposal.Task);

    private sealed class GatedDisposalProcess(
        ICompilerProcess inner,
        TaskCompletionSource disposalStarted,
        Task allowDisposal) : ICompilerProcess
    {
        public Stream StandardInput => inner.StandardInput;

        public Stream StandardOutput => inner.StandardOutput;

        public Stream StandardError => inner.StandardError;

        public int? ExitCode => inner.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            inner.WaitForExitAsync(cancellationToken);

        public void Kill() => inner.Kill();

        public async ValueTask DisposeAsync()
        {
            disposalStarted.TrySetResult();
            await allowDisposal.ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class ScriptedCompilerEndpoint
{
    public ScriptedCompilerEndpoint(
        PipeReader fromHost,
        PipeWriter toHost,
        PipeWriter standardError)
    {
        FromHost = fromHost;
        ToHost = toHost;
        StandardError = standardError;
    }

    public PipeReader FromHost { get; }

    public PipeWriter ToHost { get; }

    public PipeWriter StandardError { get; }

    public async Task<(uint CompilationId, InboundMessage Message)> ReadAsync(
        CancellationToken cancellationToken)
    {
        (uint CompilationId, InboundMessage Message)? packet = null;
        var packetRead = await PacketCodec.ReadAsync(
            FromHost,
            1024 * 1024,
            value => packet = (
                value.CompilationId,
                InboundMessage.Parser.ParseFrom(value.Payload)),
            cancellationToken);
        if (!packetRead)
        {
            throw new EndOfStreamException("The host stream ended before the expected packet.");
        }

        return packet!.Value;
    }

    public async Task SendAsync(
        uint compilationId,
        OutboundMessage message,
        CancellationToken cancellationToken)
    {
        await PacketCodec.WriteAsync(
            ToHost,
            compilationId,
            message.ToByteArray(),
            1024 * 1024,
            cancellationToken);
    }

    public Task SendVersionAsync(uint requestId, CancellationToken cancellationToken) =>
        SendAsync(
            0,
            new OutboundMessage
            {
                VersionResponse = new OutboundMessage.Types.VersionResponse
                {
                    Id = requestId,
                    CompilerVersion = "scripted-1.0",
                    ImplementationName = "Dart Sass",
                    ImplementationVersion = "1.99.0",
                    ProtocolVersion = "3.2.0"
                }
            },
            cancellationToken);

    public Task SendSuccessAsync(
        uint compilationId,
        string css,
        CancellationToken cancellationToken) =>
        SendAsync(
            compilationId,
            new OutboundMessage
            {
                CompileResponse = new OutboundMessage.Types.CompileResponse
                {
                    Success = new OutboundMessage.Types.CompileResponse.Types.CompileSuccess
                    {
                        Css = css
                    },
                    LoadedUrls = { "file:///entry.scss" }
                }
            },
            cancellationToken);

    public async Task CompleteHandshakeAsync(CancellationToken cancellationToken)
    {
        (uint compilationId, InboundMessage request) = await ReadAsync(cancellationToken);
        Assert.Equal(0u, compilationId);
        Assert.Equal(InboundMessage.MessageOneofCase.VersionRequest, request.MessageCase);
        await SendVersionAsync(request.VersionRequest.Id, cancellationToken);
    }

    public async Task DrainUntilEofAsync(CancellationToken cancellationToken)
    {
        var packetRead = await PacketCodec.ReadAsync(
            FromHost,
            1024 * 1024,
            _ => { },
            cancellationToken);
        Assert.False(packetRead);
    }
}

internal sealed class ScriptedCompilerProcess : ICompilerProcess
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Pipe _hostToCompiler = new();
    private readonly Pipe _compilerToHost = new();
    private readonly Pipe _standardError = new();
    private readonly Task _scriptTask;
    private readonly int _configuredExitCode;

    public ScriptedCompilerProcess(
        Func<ScriptedCompilerEndpoint, CancellationToken, Task> script,
        int exitCode)
    {
        _configuredExitCode = exitCode;
        StandardInput = _hostToCompiler.Writer.AsStream();
        StandardOutput = _compilerToHost.Reader.AsStream();
        StandardError = _standardError.Reader.AsStream();

        var endpoint = new ScriptedCompilerEndpoint(
            _hostToCompiler.Reader,
            _compilerToHost.Writer,
            _standardError.Writer);
        _scriptTask = Task.Run(() => RunScriptAsync(script, endpoint));
    }

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public int? ExitCode { get; private set; }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _scriptTask.WaitAsync(cancellationToken);

    public void Kill() => _cancellation.Cancel();

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await _scriptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await StandardInput.DisposeAsync().ConfigureAwait(false);
        await StandardOutput.DisposeAsync().ConfigureAwait(false);
        await StandardError.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
    }

    private async Task RunScriptAsync(
        Func<ScriptedCompilerEndpoint, CancellationToken, Task> script,
        ScriptedCompilerEndpoint endpoint)
    {
        try
        {
            await script(endpoint, _cancellation.Token).ConfigureAwait(false);
            ExitCode = _configuredExitCode;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            ExitCode = -1;
        }
        finally
        {
            await endpoint.FromHost.CompleteAsync().ConfigureAwait(false);
            await endpoint.ToHost.CompleteAsync().ConfigureAwait(false);
            await endpoint.StandardError.CompleteAsync().ConfigureAwait(false);
        }
    }
}
