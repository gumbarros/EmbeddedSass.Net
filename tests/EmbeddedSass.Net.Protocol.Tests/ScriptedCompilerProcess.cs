using System.Collections.Concurrent;
using System.IO.Pipelines;
using Google.Protobuf;
using Sass.EmbeddedProtocol;
using EmbeddedSass.Net.Internal.Process;
using EmbeddedSass.Net.Internal.Protocol;
using EmbeddedSass.Net.Internal.Transport;

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
        ProtocolPacket? packet = await PacketCodec.ReadAsync(
            FromHost,
            1024 * 1024,
            cancellationToken);
        if (packet is null)
        {
            throw new EndOfStreamException("The host stream ended before the expected packet.");
        }

        return (
            packet.Value.CompilationId,
            InboundMessage.Parser.ParseFrom(packet.Value.Payload.Span));
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
        ProtocolPacket? packet = await PacketCodec.ReadAsync(
            FromHost,
            1024 * 1024,
            cancellationToken);
        Assert.Null(packet);
    }
}

internal sealed class ScriptedCompilerProcess : ICompilerProcess
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Pipe _hostToCompiler = new();
    private readonly Pipe _compilerToHost = new();
    private readonly Pipe _standardError = new();
    private readonly Stream _standardInput;
    private readonly Stream _standardOutput;
    private readonly Stream _standardErrorStream;
    private readonly Task _scriptTask;
    private readonly int _configuredExitCode;
    private int? _exitCode;

    public ScriptedCompilerProcess(
        Func<ScriptedCompilerEndpoint, CancellationToken, Task> script,
        int exitCode)
    {
        _configuredExitCode = exitCode;
        _standardInput = _hostToCompiler.Writer.AsStream();
        _standardOutput = _compilerToHost.Reader.AsStream();
        _standardErrorStream = _standardError.Reader.AsStream();

        var endpoint = new ScriptedCompilerEndpoint(
            _hostToCompiler.Reader,
            _compilerToHost.Writer,
            _standardError.Writer);
        _scriptTask = Task.Run(() => RunScriptAsync(script, endpoint));
    }

    public Stream StandardInput => _standardInput;

    public Stream StandardOutput => _standardOutput;

    public Stream StandardError => _standardErrorStream;

    public int? ExitCode => _exitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _scriptTask.WaitAsync(cancellationToken);

    public void Kill() => _cancellation.Cancel();

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try
        {
            await _scriptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _standardInput.DisposeAsync().ConfigureAwait(false);
        await _standardOutput.DisposeAsync().ConfigureAwait(false);
        await _standardErrorStream.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
    }

    private async Task RunScriptAsync(
        Func<ScriptedCompilerEndpoint, CancellationToken, Task> script,
        ScriptedCompilerEndpoint endpoint)
    {
        try
        {
            await script(endpoint, _cancellation.Token).ConfigureAwait(false);
            _exitCode = _configuredExitCode;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _exitCode = -1;
        }
        finally
        {
            await endpoint.FromHost.CompleteAsync().ConfigureAwait(false);
            await endpoint.ToHost.CompleteAsync().ConfigureAwait(false);
            await endpoint.StandardError.CompleteAsync().ConfigureAwait(false);
        }
    }
}
