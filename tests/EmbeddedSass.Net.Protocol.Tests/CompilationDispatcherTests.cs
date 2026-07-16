using Google.Protobuf;
using Sass.EmbeddedProtocol;
using EmbeddedSass.Net.Diagnostics;
using EmbeddedSass.Net.Internal.Process;
using EmbeddedSass.Net.Internal.Protocol;
using EmbeddedSass.Net.Internal.Transport;
using EmbeddedSass.Net.Importing;

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
    public async Task UnsupportedFunctionCallbackIsRejected()
    {
        using var dispatcher = CreateDispatcher();
        CompilationOperation operation = await dispatcher.RegisterAsync(null, CancellationToken.None);

        SassProtocolException exception = Assert.Throws<SassProtocolException>(() =>
            dispatcher.Dispatch(Packet(
                operation.CompilationId,
                new OutboundMessage
                {
                    FunctionCallRequest = new OutboundMessage.Types.FunctionCallRequest()
                })));

        Assert.Contains("unsupported callback", exception.Message);
        dispatcher.FailAll(exception);
        await Assert.ThrowsAsync<SassProtocolException>(() => operation.Completion.Task);
    }

    [Fact]
    public async Task ContentImporterCanonicalizeCallbackSendsResponse()
    {
        var sent = new TaskCompletionSource<InboundMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = CreateDispatcher((_, message) =>
        {
            sent.TrySetResult(message);
            return Task.CompletedTask;
        });
        var importer = new RecordingContentImporter();
        var registry = new ImporterRegistry();
        uint importerId = registry.Register(importer);
        CompilationOperation operation = await dispatcher.RegisterAsync(
            null,
            CancellationToken.None,
            registry);

        dispatcher.Dispatch(Packet(
            operation.CompilationId,
            new OutboundMessage
            {
                CanonicalizeRequest = new OutboundMessage.Types.CanonicalizeRequest
                {
                    Id = 9,
                    ImporterId = importerId,
                    Url = "theme",
                    FromImport = true,
                    ContainingUrl = "virtual:entry"
                }
            }));

        InboundMessage response = await sent.Task;
        Assert.Equal(9u, response.CanonicalizeResponse.Id);
        Assert.Equal("virtual:theme", response.CanonicalizeResponse.Url);
        Assert.True(response.CanonicalizeResponse.ContainingUrlUnused);
        Assert.Equal(new Uri("theme", UriKind.Relative), importer.CanonicalizeContext?.Url);
        Assert.True(importer.CanonicalizeContext?.FromImport);
        dispatcher.FailAll(new OperationCanceledException());
    }

    [Fact]
    public async Task ContentImporterExceptionBecomesImportErrorResponse()
    {
        var sent = new TaskCompletionSource<InboundMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = CreateDispatcher((_, message) =>
        {
            sent.TrySetResult(message);
            return Task.CompletedTask;
        });
        var registry = new ImporterRegistry();
        uint importerId = registry.Register(new ThrowingContentImporter());
        CompilationOperation operation = await dispatcher.RegisterAsync(
            null,
            CancellationToken.None,
            registry);

        dispatcher.Dispatch(Packet(
            operation.CompilationId,
            new OutboundMessage
            {
                ImportRequest = new OutboundMessage.Types.ImportRequest
                {
                    Id = 10,
                    ImporterId = importerId,
                    Url = "virtual:theme"
                }
            }));

        InboundMessage response = await sent.Task;
        Assert.Equal("could not load theme", response.ImportResponse.Error);
        dispatcher.FailAll(new OperationCanceledException());
    }

    [Fact]
    public async Task FileImporterCallbackSendsLocalFileUrl()
    {
        var sent = new TaskCompletionSource<InboundMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = CreateDispatcher((_, message) =>
        {
            sent.TrySetResult(message);
            return Task.CompletedTask;
        });
        var registry = new ImporterRegistry();
        uint importerId = registry.Register(new RecordingFileImporter());
        CompilationOperation operation = await dispatcher.RegisterAsync(
            null,
            CancellationToken.None,
            registry);

        dispatcher.Dispatch(Packet(
            operation.CompilationId,
            new OutboundMessage
            {
                FileImportRequest = new OutboundMessage.Types.FileImportRequest
                {
                    Id = 11,
                    ImporterId = importerId,
                    Url = "theme"
                }
            }));

        InboundMessage response = await sent.Task;
        Assert.Equal(11u, response.FileImportResponse.Id);
        Assert.True(Uri.TryCreate(response.FileImportResponse.FileUrl, UriKind.Absolute, out Uri? fileUrl));
        Assert.True(fileUrl.IsFile);
        dispatcher.FailAll(new OperationCanceledException());
    }

    [Fact]
    public async Task AwaitingImporterDoesNotBlockAnotherCompilation()
    {
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = new CompilationDispatcher(
            maximumConcurrentCompilations: 2,
            maximumPendingLogs: 2,
            CancellationToken.None,
            (_, _) =>
            {
                callbackSent.TrySetResult();
                return Task.CompletedTask;
            });
        var registry = new ImporterRegistry();
        uint importerId = registry.Register(
            new BlockingContentImporter(callbackEntered, releaseCallback));
        CompilationOperation waiting = await dispatcher.RegisterAsync(
            null,
            CancellationToken.None,
            registry);
        CompilationOperation independent = await dispatcher.RegisterAsync(
            null,
            CancellationToken.None);

        dispatcher.Dispatch(Packet(
            waiting.CompilationId,
            new OutboundMessage
            {
                CanonicalizeRequest = new OutboundMessage.Types.CanonicalizeRequest
                {
                    Id = 12,
                    ImporterId = importerId,
                    Url = "theme"
                }
            }));
        await callbackEntered.Task;

        dispatcher.Dispatch(Packet(independent.CompilationId, Success("independent")));
        Assert.Equal("independent", (await independent.Completion.Task).Css);

        releaseCallback.TrySetResult();
        await callbackSent.Task;
        dispatcher.FailAll(new OperationCanceledException());
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

    private static CompilationDispatcher CreateDispatcher(
        Func<uint, InboundMessage, Task> sendAsync) =>
        new(1, maximumPendingLogs: 2, CancellationToken.None, sendAsync);

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

    private sealed class RecordingContentImporter : ISassContentImporter
    {
        public SassCanonicalizeContext? CanonicalizeContext { get; private set; }

        public ValueTask<SassCanonicalizeResult?> CanonicalizeAsync(
            SassCanonicalizeContext context,
            CancellationToken cancellationToken)
        {
            CanonicalizeContext = context;
            return ValueTask.FromResult<SassCanonicalizeResult?>(
                new(new Uri("virtual:theme"), ContainingUrlUnused: true));
        }

        public ValueTask<SassImportResult?> LoadAsync(
            Uri canonicalUrl,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<SassImportResult?>(new("$color: purple;"));
    }

    private sealed class ThrowingContentImporter : ISassContentImporter
    {
        public ValueTask<SassCanonicalizeResult?> CanonicalizeAsync(
            SassCanonicalizeContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult<SassCanonicalizeResult?>(null);

        public ValueTask<SassImportResult?> LoadAsync(
            Uri canonicalUrl,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<SassImportResult?>(new InvalidOperationException("could not load theme"));
    }

    private sealed class RecordingFileImporter : ISassFileImporter
    {
        public ValueTask<SassFileImportResult?> FindFileUrlAsync(
            SassFileImportContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<SassFileImportResult?>(
                new(new Uri(Path.GetFullPath("theme.scss"))));
    }

    private sealed class BlockingContentImporter(
        TaskCompletionSource entered,
        TaskCompletionSource release) : ISassContentImporter
    {
        public async ValueTask<SassCanonicalizeResult?> CanonicalizeAsync(
            SassCanonicalizeContext context,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return null;
        }

        public ValueTask<SassImportResult?> LoadAsync(
            Uri canonicalUrl,
            CancellationToken cancellationToken) => ValueTask.FromResult<SassImportResult?>(null);
    }
}
