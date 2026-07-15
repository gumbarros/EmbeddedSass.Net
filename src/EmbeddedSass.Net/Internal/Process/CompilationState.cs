using System.Threading.Channels;
using Sass.EmbeddedProtocol;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Diagnostics;
using EmbeddedSass.Net.Internal.Protocol;

namespace EmbeddedSass.Net.Internal.Process;

internal sealed class CompilationState
{
    private readonly Channel<SassLogEvent> _logs;
    private readonly SassLogHandler? _logHandler;
    private readonly CancellationToken _connectionCancellation;
    private readonly Action _release;
    private readonly Task _logWorker;
    private int _completed;

    public CompilationState(
        uint compilationId,
        SassLogHandler? logHandler,
        int maximumPendingLogs,
        CancellationToken connectionCancellation,
        Action release)
    {
        CompilationId = compilationId;
        _logHandler = logHandler;
        _connectionCancellation = connectionCancellation;
        _release = release;
        _logs = Channel.CreateBounded<SassLogEvent>(new BoundedChannelOptions(maximumPendingLogs)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _logWorker = RunLogWorkerAsync();
    }

    public uint CompilationId { get; }

    public TaskCompletionSource<SassCompileResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryAddLog(OutboundMessage.Types.LogEvent message)
    {
        SassSourceSpan? span = message.Span is null
            ? null
            : SourceSpanMapper.Map(message.Span);

        var logEvent = new SassLogEvent(
            message.Type switch
            {
                LogEventType.Warning => SassLogLevel.Warning,
                LogEventType.DeprecationWarning => SassLogLevel.DeprecationWarning,
                LogEventType.Debug => SassLogLevel.Debug,
                _ => throw new SassProtocolException($"Unknown log event type {message.Type}.")
            },
            message.Message,
            message.Formatted,
            span,
            message.StackTrace.Length == 0 ? null : message.StackTrace,
            message.HasDeprecationType ? message.DeprecationType : null);

        return _logs.Writer.TryWrite(logEvent);
    }

    public void Complete(OutboundMessage.Types.CompileResponse response)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _logs.Writer.TryComplete();
        _ = CompleteAfterLogsAsync(response);
    }

    public static void Validate(OutboundMessage.Types.CompileResponse response)
    {
        switch (response.ResultCase)
        {
            case OutboundMessage.Types.CompileResponse.ResultOneofCase.Success:
                var seenUrls = new HashSet<string>(StringComparer.Ordinal);
                foreach (string value in response.LoadedUrls)
                {
                    if (!seenUrls.Add(value))
                    {
                        throw new SassProtocolException(
                            $"Compile response contains duplicate loaded URL '{value}'.");
                    }

                    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? url) || !url.IsAbsoluteUri)
                    {
                        throw new SassProtocolException($"Loaded URL '{value}' is not absolute.");
                    }
                }

                break;

            case OutboundMessage.Types.CompileResponse.ResultOneofCase.Failure:
                if (response.Failure.Span is not null)
                {
                    SourceSpanMapper.Map(response.Failure.Span);
                }

                break;

            default:
                throw new SassProtocolException(
                    "A compile response is missing its mandatory result.");
        }
    }

    public void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _logs.Writer.TryComplete(exception);
        Completion.TrySetException(exception);
        _release();
    }

    private async Task CompleteAfterLogsAsync(OutboundMessage.Types.CompileResponse response)
    {
        try
        {
            await _logWorker.ConfigureAwait(false);

            switch (response.ResultCase)
            {
                case OutboundMessage.Types.CompileResponse.ResultOneofCase.Success:
                    Completion.TrySetResult(MapSuccess(response));
                    break;

                case OutboundMessage.Types.CompileResponse.ResultOneofCase.Failure:
                    OutboundMessage.Types.CompileResponse.Types.CompileFailure failure = response.Failure;
                    Completion.TrySetException(new SassCompilationException(
                        failure.Message,
                        failure.Formatted,
                        failure.Span is null ? null : SourceSpanMapper.Map(failure.Span),
                        failure.StackTrace.Length == 0 ? null : failure.StackTrace));
                    break;

                default:
                    Completion.TrySetException(new SassProtocolException(
                        "A compile response is missing its mandatory result."));
                    break;
            }
        }
        catch (Exception exception)
        {
            Completion.TrySetException(exception);
        }
        finally
        {
            _release();
        }
    }

    private static SassCompileResult MapSuccess(
        OutboundMessage.Types.CompileResponse response)
    {
        var loadedUrls = new Uri[response.LoadedUrls.Count];
        for (int index = 0; index < loadedUrls.Length; index++)
        {
            string value = response.LoadedUrls[index];
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? url) || !url.IsAbsoluteUri)
            {
                throw new SassProtocolException($"Loaded URL '{value}' is not absolute.");
            }

            loadedUrls[index] = url;
        }

        string sourceMap = response.Success.SourceMap;
        return new SassCompileResult(
            response.Success.Css,
            sourceMap.Length == 0 ? null : sourceMap,
            Array.AsReadOnly(loadedUrls));
    }

    private async Task RunLogWorkerAsync()
    {
        if (_logHandler is null)
        {
            await foreach (SassLogEvent _ in _logs.Reader.ReadAllAsync(_connectionCancellation)
                .ConfigureAwait(false))
            {
            }

            return;
        }

        await foreach (SassLogEvent logEvent in _logs.Reader.ReadAllAsync(_connectionCancellation)
            .ConfigureAwait(false))
        {
            await _logHandler(logEvent, _connectionCancellation).ConfigureAwait(false);
        }
    }
}
