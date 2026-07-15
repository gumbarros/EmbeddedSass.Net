using Google.Protobuf;
using Sass.EmbeddedProtocol;
using EmbeddedSass.Net.Diagnostics;
using EmbeddedSass.Net.Internal.Process;
using EmbeddedSass.Net.Internal.Transport;

namespace EmbeddedSass.Net.Protocol.Tests;

public sealed class CompilationDispatcherTests
{
    [Fact]
    public async Task VersionResponseCompletesHandshake()
    {
        using var dispatcher = CreateDispatcher();
        dispatcher.Dispatch(Packet(
            0,
            new OutboundMessage
            {
                VersionResponse = new OutboundMessage.Types.VersionResponse
                {
                    Id = 1,
                    CompilerVersion = "1.0.0",
                    ImplementationName = "Dart Sass",
                    ImplementationVersion = "1.0.0",
                    ProtocolVersion = "3.2.0"
                }
            }));

        SassCompilerInfo info = await dispatcher.Version;
        Assert.Equal("3.2.0", info.ProtocolVersion);
    }

    [Fact]
    public void UnknownCompilationIdIsRejected()
    {
        using var dispatcher = CreateDispatcher();

        SassProtocolException exception = Assert.Throws<SassProtocolException>(() =>
            dispatcher.Dispatch(Packet(42, Success("unused"))));

        Assert.Contains("unknown compilation ID 42", exception.Message);
    }

    [Fact]
    public async Task TerminalResponseCompletesAndRemovesOperation()
    {
        using var dispatcher = CreateDispatcher();
        CompilationOperation operation = await dispatcher.RegisterAsync(null, CancellationToken.None);
        ProtocolPacket response = Packet(operation.CompilationId, Success("compiled"));

        dispatcher.Dispatch(response);

        Assert.Equal("compiled", (await operation.Completion.Task).Css);
        Assert.Throws<SassProtocolException>(() => dispatcher.Dispatch(response));
    }

    [Fact]
    public async Task UnsupportedCompilerCallbackIsRejected()
    {
        using var dispatcher = CreateDispatcher();
        CompilationOperation operation = await dispatcher.RegisterAsync(null, CancellationToken.None);

        SassProtocolException exception = Assert.Throws<SassProtocolException>(() =>
            dispatcher.Dispatch(Packet(
                operation.CompilationId,
                new OutboundMessage
                {
                    CanonicalizeRequest = new OutboundMessage.Types.CanonicalizeRequest()
                })));

        Assert.Contains("unsupported callback", exception.Message);
        dispatcher.FailAll(exception);
        await Assert.ThrowsAsync<SassProtocolException>(() => operation.Completion.Task);
    }

    [Fact]
    public async Task FailAllFailsEveryPendingOperation()
    {
        using var dispatcher = CreateDispatcher(maximumConcurrentCompilations: 2);
        CompilationOperation first = await dispatcher.RegisterAsync(null, CancellationToken.None);
        CompilationOperation second = await dispatcher.RegisterAsync(null, CancellationToken.None);
        var failure = new IOException("compiler stopped");

        dispatcher.FailAll(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first.Completion.Task));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => second.Completion.Task));
    }

    [Fact]
    public async Task PendingLogLimitIsEnforcedWithoutBlockingDispatch()
    {
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = new CompilationDispatcher(
            maximumConcurrentCompilations: 1,
            maximumPendingLogs: 1,
            CancellationToken.None);
        CompilationOperation operation = await dispatcher.RegisterAsync(
            async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task;
            },
            CancellationToken.None);
        ProtocolPacket log = Packet(
            operation.CompilationId,
            new OutboundMessage
            {
                LogEvent = new OutboundMessage.Types.LogEvent
                {
                    Type = LogEventType.Warning,
                    Message = "warning"
                }
            });

        dispatcher.Dispatch(log);
        await handlerEntered.Task;
        dispatcher.Dispatch(log);

        SassProtocolException exception = Assert.Throws<SassProtocolException>(() =>
            dispatcher.Dispatch(log));

        Assert.Contains("pending log event limit", exception.Message);
        releaseHandler.TrySetResult();
        dispatcher.FailAll(exception);
        await Assert.ThrowsAsync<SassProtocolException>(() => operation.Completion.Task);
    }

    private static CompilationDispatcher CreateDispatcher(
        int maximumConcurrentCompilations = 1) =>
        new(maximumConcurrentCompilations, maximumPendingLogs: 2, CancellationToken.None);

    private static ProtocolPacket Packet(uint compilationId, OutboundMessage message) =>
        new(compilationId, message.ToByteArray());

    private static OutboundMessage Success(string css) =>
        new()
        {
            CompileResponse = new OutboundMessage.Types.CompileResponse
            {
                Success = new OutboundMessage.Types.CompileResponse.Types.CompileSuccess
                {
                    Css = css
                }
            }
        };
}
