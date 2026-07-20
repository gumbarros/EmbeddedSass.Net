using System.Buffers;
using EmbeddedSass.Diagnostics;
using EmbeddedSass.Internal.Process;
using EmbeddedSass.Internal.Protocol;
using Sass.EmbeddedProtocol;

namespace EmbeddedSass.Protocol.Tests;

public sealed class EmbeddedCompilerProcessTests
{
    [Fact]
    public async Task HandshakeCompilationAndLogsUseEmbeddedSassDomainTypes()
    {
        var launcher = new ScriptedProcessLauncher();
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            (uint compilationId, InboundMessage request) = await endpoint.ReadAsync(cancellationToken);
            Assert.Equal(InboundMessage.MessageOneofCase.CompileRequest, request.MessageCase);

            await endpoint.SendAsync(
                compilationId,
                new OutboundMessage
                {
                    LogEvent = new OutboundMessage.Types.LogEvent
                    {
                        Type = LogEventType.Warning,
                        Message = "warning",
                        Formatted = "WARNING: warning"
                    }
                },
                cancellationToken);
            await endpoint.SendSuccessAsync(compilationId, "a { color: red; }", cancellationToken);
            await endpoint.DrainUntilEofAsync(cancellationToken);
        });

        var logs = new List<SassLogEvent>();
        await using var connection = CreateConnection(launcher);
        SassCompileResult result = await connection.CompileAsync(
            CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("a {color: red}"))
            {
                LogHandler = (logEvent, _) =>
                {
                    logs.Add(logEvent);
                    return ValueTask.CompletedTask;
                }
            }),
            CancellationToken.None);

        Assert.Equal("a { color: red; }", result.Css);
        Assert.Equal(new Uri("file:///entry.scss"), Assert.Single(result.LoadedUrls));
        Assert.Equal(SassLogLevel.Warning, Assert.Single(logs).Level);
        Assert.Equal("3.2.0", connection.Info?.ProtocolVersion);
    }

    [Fact]
    public async Task ConcurrentCompilationsAreDispatchedByCompilationId()
    {
        var launcher = new ScriptedProcessLauncher();
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            (uint firstId, InboundMessage first) = await endpoint.ReadAsync(cancellationToken);
            (uint secondId, InboundMessage second) = await endpoint.ReadAsync(cancellationToken);

            Assert.NotEqual(firstId, secondId);
            await endpoint.SendSuccessAsync(
                secondId,
                second.CompileRequest.String.Source,
                cancellationToken);
            await endpoint.SendSuccessAsync(
                firstId,
                first.CompileRequest.String.Source,
                cancellationToken);
            await endpoint.DrainUntilEofAsync(cancellationToken);
        });

        await using var connection = CreateConnection(launcher);
        Task<SassCompileResult> first = connection.CompileAsync(
            CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("first"))),
            CancellationToken.None);
        Task<SassCompileResult> second = connection.CompileAsync(
            CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("second"))),
            CancellationToken.None);

        SassCompileResult[] results = await Task.WhenAll(first, second);
        Assert.Equal("first", results[0].Css);
        Assert.Equal("second", results[1].Css);
    }

    [Fact]
    public async Task CanceledCompilationIsDrainedBeforeItsSlotIsReused()
    {
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new ScriptedProcessLauncher();
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            (uint firstId, _) = await endpoint.ReadAsync(cancellationToken);
            firstReceived.SetResult();
            await sendFirstResponse.Task.WaitAsync(cancellationToken);
            await endpoint.SendSuccessAsync(firstId, "abandoned", cancellationToken);

            (uint secondId, _) = await endpoint.ReadAsync(cancellationToken);
            secondReceived.SetResult();
            await endpoint.SendSuccessAsync(secondId, "second", cancellationToken);
            await endpoint.DrainUntilEofAsync(cancellationToken);
        });

        await using var connection = CreateConnection(launcher, maximumConcurrentCompilations: 1);
        using var cancellation = new CancellationTokenSource();
        Task<SassCompileResult> first = connection.CompileAsync(
            CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("first"))),
            cancellation.Token);
        await firstReceived.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Task<SassCompileResult> second = connection.CompileAsync(
            CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("second"))),
            CancellationToken.None);
        await Task.Delay(50);
        Assert.False(secondReceived.Task.IsCompleted);

        sendFirstResponse.SetResult();
        Assert.Equal("second", (await second).Css);
        Assert.True(secondReceived.Task.IsCompleted);
    }

    [Fact]
    public async Task UnexpectedExitIncludesBoundedStderrAndNextCallRestarts()
    {
        var launcher = new ScriptedProcessLauncher();
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            await endpoint.ReadAsync(cancellationToken);
            endpoint.StandardError.Write("compiler crashed"u8);
            await endpoint.StandardError.FlushAsync(cancellationToken);
        }, exitCode: 17);
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            (uint compilationId, _) = await endpoint.ReadAsync(cancellationToken);
            await endpoint.SendSuccessAsync(compilationId, "restarted", cancellationToken);
            await endpoint.DrainUntilEofAsync(cancellationToken);
        });

        await using var connection = CreateConnection(launcher);
        SassCompilerException failure = await Assert.ThrowsAsync<SassCompilerException>(
            () => connection.CompileAsync(
                CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("crash"))),
                CancellationToken.None));

        Assert.Equal(17, failure.ExitCode);
        Assert.Contains("compiler crashed", failure.StandardErrorTail);

        SassCompileResult recovered = await connection.CompileAsync(
            CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("recover"))),
            CancellationToken.None);
        Assert.Equal("restarted", recovered.Css);
        Assert.Equal(2, launcher.LaunchCount);
    }

    [Fact]
    public async Task CompilerFailureBecomesRichCompilationException()
    {
        var launcher = new ScriptedProcessLauncher();
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            (uint compilationId, _) = await endpoint.ReadAsync(cancellationToken);
            await endpoint.SendAsync(
                compilationId,
                new OutboundMessage
                {
                    CompileResponse = new OutboundMessage.Types.CompileResponse
                    {
                        Failure = new OutboundMessage.Types.CompileResponse.Types.CompileFailure
                        {
                            Message = "Undefined variable.",
                            Formatted = "Error: Undefined variable.",
                            StackTrace = "entry.scss 1:1",
                            Span = new SourceSpan
                            {
                                Text = "$missing",
                                Url = "file:///entry.scss",
                                Start = new SourceSpan.Types.SourceLocation
                                {
                                    Offset = 0,
                                    Line = 0,
                                    Column = 0
                                },
                                End = new SourceSpan.Types.SourceLocation
                                {
                                    Offset = 8,
                                    Line = 0,
                                    Column = 8
                                }
                            }
                        }
                    }
                },
                cancellationToken);
            await endpoint.DrainUntilEofAsync(cancellationToken);
        });

        await using var connection = CreateConnection(launcher);
        SassCompilationException exception = await Assert.ThrowsAsync<SassCompilationException>(
            () => connection.CompileAsync(
                CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("$missing"))),
                CancellationToken.None));

        Assert.Equal("Error: Undefined variable.", exception.FormattedMessage);
        Assert.Equal(new Uri("file:///entry.scss"), exception.Span?.Url);
        Assert.Equal("entry.scss 1:1", exception.CompilerStackTrace);
    }

    [Fact]
    public async Task MalformedProtobufIsAFatalProtocolFailure()
    {
        var launcher = new ScriptedProcessLauncher();
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            (uint compilationId, _) = await endpoint.ReadAsync(cancellationToken);
            await EmbeddedSass.Internal.Transport.PacketCodec.WriteAsync(
                endpoint.ToHost,
                compilationId,
                new byte[] { 0xff },
                1024,
                cancellationToken);
            await endpoint.DrainUntilEofAsync(cancellationToken);
        });

        await using var connection = CreateConnection(launcher);
        SassProtocolException exception = await Assert.ThrowsAsync<SassProtocolException>(
            () => connection.CompileAsync(
                CompileRequestMapper.Map(new SassCompileRequest(new SassStringInput("a {}"))),
                CancellationToken.None));

        Assert.Contains("protobuf", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForResourceTeardown()
    {
        var launcher = new GatedDisposalProcessLauncher();
        launcher.Enqueue(async (endpoint, cancellationToken) =>
        {
            await endpoint.CompleteHandshakeAsync(cancellationToken);
            await endpoint.DrainUntilEofAsync(cancellationToken);
        });
        CompilerOptionsSnapshot options = CreateOptions();
        EmbeddedCompilerProcess compiler = await EmbeddedCompilerProcess.StartAsync(
            options,
            launcher,
            CancellationToken.None);

        Task firstDisposal = compiler.DisposeAsync().AsTask();
        await launcher.DisposalStarted.Task;
        Task secondDisposal = compiler.DisposeAsync().AsTask();

        Assert.False(secondDisposal.IsCompleted);

        launcher.AllowDisposal();
        await Task.WhenAll(firstDisposal, secondDisposal);
    }

    private static SassCompilerConnection CreateConnection(
        ScriptedProcessLauncher launcher,
        int maximumConcurrentCompilations = 4)
    {
        CompilerOptionsSnapshot options = CreateOptions(maximumConcurrentCompilations);
        return new SassCompilerConnection(options, launcher);
    }

    private static CompilerOptionsSnapshot CreateOptions(int maximumConcurrentCompilations = 4) =>
        CompilerOptionsSnapshot.Create(new SassCompilerOptions
        {
            CompilerPath = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "scripted", "dart-sass"),
            MaxConcurrentCompilations = maximumConcurrentCompilations,
            HandshakeTimeout = TimeSpan.FromSeconds(2),
            ShutdownGracePeriod = TimeSpan.FromSeconds(2)
        });
}
