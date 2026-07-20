using System.Threading.Channels;
using EmbeddedSass.Diagnostics;
using EmbeddedSass.Importing;
using EmbeddedSass.Internal.Protocol;
using Sass.EmbeddedProtocol;

namespace EmbeddedSass.Internal.Process;

internal sealed class CompilationOperation
{
    private readonly Channel<SassLogEvent>? _logs;
    private readonly SassLogHandler? _logHandler;
    private readonly CancellationToken _connectionCancellation;
    private readonly Action _release;
    private readonly Task _logWorker;
    private readonly ImporterRegistry _importers;
    private readonly Func<uint, InboundMessage, Task> _sendAsync;
    private readonly Action<Exception> _fatalCallbackFailure;
    private readonly HashSet<CallbackKey> _pendingCallbacks = [];
    private readonly Lock _callbackGate = new();
    private int _completed;

    public CompilationOperation(uint compilationId,
        SassLogHandler? logHandler,
        ImporterRegistry importers,
        int maximumPendingLogs,
        Func<uint, InboundMessage, Task> sendAsync,
        Action<Exception> fatalCallbackFailure,
        Action release,
        CancellationToken connectionCancellation)
    {
        CompilationId = compilationId;
        _logHandler = logHandler;
        _importers = importers;
        _connectionCancellation = connectionCancellation;
        _sendAsync = sendAsync;
        _fatalCallbackFailure = fatalCallbackFailure;
        _release = release;
        if (logHandler is null)
        {
            _logWorker = Task.CompletedTask;
        }
        else
        {
            _logs = Channel.CreateBounded<SassLogEvent>(new BoundedChannelOptions(maximumPendingLogs)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _logWorker = RunLogWorkerAsync();
        }
    }

    public uint CompilationId { get; }

    public TaskCompletionSource<SassCompileResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryAddLog(OutboundMessage.Types.LogEvent message)
    {
        var span = message.Span is null
            ? null
            : SourceSpanMapper.Map(message.Span);
        
        var level = message.Type switch
        {
            LogEventType.Warning => SassLogLevel.Warning,
            LogEventType.DeprecationWarning => SassLogLevel.DeprecationWarning,
            LogEventType.Debug => SassLogLevel.Debug,
            _ => throw new SassProtocolException($"Unknown log event type {message.Type}.")
        };

        if (_logs is null)
        {
            return true;
        }

        var logEvent = new SassLogEvent(
            level,
            message.Message,
            message.Formatted,
            span,
            message.StackTrace.Length == 0 ? null : message.StackTrace,
            message.HasDeprecationType ? message.DeprecationType : null);

        return _logs.Writer.TryWrite(logEvent);
    }

    public void HandleCanonicalize(OutboundMessage.Types.CanonicalizeRequest request)
    {
        var importer = GetImporter<ISassContentImporter>(
            request.ImporterId,
            "canonicalize");
        var context = new SassCanonicalizeContext(
            ParseUrl(request.Url, allowRelative: true, "canonicalize URL"),
            request.FromImport,
            request.HasContainingUrl
                ? ParseUrl(request.ContainingUrl, allowRelative: false, "containing URL")
                : null);

        StartCallback(
            OutboundMessage.MessageOneofCase.CanonicalizeRequest,
            request.Id,
            () => CanonicalizeAsync(request.Id, importer, context));
    }

    public void HandleImport(OutboundMessage.Types.ImportRequest request)
    {
        var importer = GetImporter<ISassContentImporter>(request.ImporterId, "load");
        var canonicalUrl = ParseUrl(request.Url, allowRelative: false, "canonical import URL");
        StartCallback(
            OutboundMessage.MessageOneofCase.ImportRequest,
            request.Id,
            () => ImportAsync(request.Id, importer, canonicalUrl));
    }

    public void HandleFileImport(OutboundMessage.Types.FileImportRequest request)
    {
        var importer = GetImporter<ISassFileImporter>(
            request.ImporterId,
            "file import");
        var context = new SassFileImportContext(
            ParseUrl(request.Url, allowRelative: true, "file import URL"),
            request.FromImport,
            request.HasContainingUrl
                ? ParseUrl(request.ContainingUrl, allowRelative: false, "containing URL")
                : null);

        StartCallback(
            OutboundMessage.MessageOneofCase.FileImportRequest,
            request.Id,
            () => FileImportAsync(request.Id, importer, context));
    }

    public void Complete(OutboundMessage.Types.CompileResponse response)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _logs?.Writer.TryComplete();
        _ = CompleteAfterLogsAsync(response);
    }

    public static void Validate(OutboundMessage.Types.CompileResponse response)
    {
        switch (response.ResultCase)
        {
            case OutboundMessage.Types.CompileResponse.ResultOneofCase.Success:
                var seenUrls = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in response.LoadedUrls)
                {
                    if (!seenUrls.Add(value))
                    {
                        throw new SassProtocolException(
                            $"Compile response contains duplicate loaded URL '{value}'.");
                    }

                    if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !url.IsAbsoluteUri)
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

        _logs?.Writer.TryComplete(exception);
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
                    var failure = response.Failure;
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
            var value = response.LoadedUrls[index];
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !url.IsAbsoluteUri)
            {
                throw new SassProtocolException($"Loaded URL '{value}' is not absolute.");
            }

            loadedUrls[index] = url;
        }

        var sourceMap = response.Success.SourceMap;
        return new SassCompileResult(
            response.Success.Css,
            sourceMap.Length == 0 ? null : sourceMap,
            Array.AsReadOnly(loadedUrls));
    }

    private async Task CanonicalizeAsync(
        uint requestId,
        ISassContentImporter importer,
        SassCanonicalizeContext context)
    {
        var response = new InboundMessage.Types.CanonicalizeResponse { Id = requestId };
        try
        {
            var result = await importer
                .CanonicalizeAsync(context, _connectionCancellation)
                .ConfigureAwait(false);
            if (result is not null)
            {
                ArgumentNullException.ThrowIfNull(result.CanonicalUrl);
                EnsureAbsoluteUrl(result.CanonicalUrl, "The canonical importer URL");
                response.Url = result.CanonicalUrl.AbsoluteUri;
                response.ContainingUrlUnused = result.ContainingUrlUnused;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                           !_connectionCancellation.IsCancellationRequested)
        {
            response.Error = CallbackError(exception);
        }

        await SendCallbackAsync(new InboundMessage { CanonicalizeResponse = response })
            .ConfigureAwait(false);
    }

    private async Task ImportAsync(
        uint requestId,
        ISassContentImporter importer,
        Uri canonicalUrl)
    {
        var response = new InboundMessage.Types.ImportResponse { Id = requestId };
        try
        {
            var result = await importer
                .LoadAsync(canonicalUrl, _connectionCancellation)
                .ConfigureAwait(false);
            if (result is not null)
            {
                ArgumentNullException.ThrowIfNull(result.Contents);
                var success = new InboundMessage.Types.ImportResponse.Types.ImportSuccess
                {
                    Contents = result.Contents,
                    Syntax = MapSyntax(result.Syntax)
                };
                if (result.SourceMapUrl is not null)
                {
                    EnsureAbsoluteUrl(result.SourceMapUrl, "The source-map URL");
                    success.SourceMapUrl = result.SourceMapUrl.AbsoluteUri;
                }

                response.Success = success;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                           !_connectionCancellation.IsCancellationRequested)
        {
            response.Error = CallbackError(exception);
        }

        await SendCallbackAsync(new InboundMessage { ImportResponse = response })
            .ConfigureAwait(false);
    }

    private async Task FileImportAsync(
        uint requestId,
        ISassFileImporter importer,
        SassFileImportContext context)
    {
        var response = new InboundMessage.Types.FileImportResponse { Id = requestId };
        try
        {
            var result = await importer
                .FindFileUrlAsync(context, _connectionCancellation)
                .ConfigureAwait(false);
            if (result is not null)
            {
                ArgumentNullException.ThrowIfNull(result.FileUrl);
                EnsureAbsoluteUrl(result.FileUrl, "The file importer URL");
                if (!result.FileUrl.IsFile || !string.IsNullOrEmpty(result.FileUrl.Host))
                {
                    throw new ArgumentException(
                        "The file importer URL must be an absolute local file URL.");
                }

                response.FileUrl = result.FileUrl.AbsoluteUri;
                response.ContainingUrlUnused = result.ContainingUrlUnused;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                           !_connectionCancellation.IsCancellationRequested)
        {
            response.Error = CallbackError(exception);
        }

        await SendCallbackAsync(new InboundMessage { FileImportResponse = response })
            .ConfigureAwait(false);
    }

    private void StartCallback(
        OutboundMessage.MessageOneofCase callbackType,
        uint requestId,
        Func<Task> callback)
    {
        var key = new CallbackKey(callbackType, requestId);
        lock (_callbackGate)
        {
            if (!_pendingCallbacks.Add(key))
            {
                throw new SassProtocolException(
                    $"Duplicate pending {callbackType} callback ID {requestId}.");
            }
        }

        _ = ObserveCallbackAsync(key, callback);
    }

    private async Task ObserveCallbackAsync(
        CallbackKey key,
        Func<Task> callback)
    {
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_connectionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _fatalCallbackFailure(exception);
        }
        finally
        {
            lock (_callbackGate)
            {
                _pendingCallbacks.Remove(key);
            }
        }
    }

    private Task SendCallbackAsync(InboundMessage message) =>
        _sendAsync(CompilationId, message);

    private TImporter GetImporter<TImporter>(uint importerId, string callbackName)
        where TImporter : class, ISassImporter
    {
        if (!_importers.TryGet(importerId, out ISassImporter? importer))
        {
            throw new SassProtocolException(
                $"The compiler requested unknown importer ID {importerId} for {callbackName}.");
        }

        if (importer is not TImporter typed)
        {
            throw new SassProtocolException(
                $"The compiler requested {callbackName} from incompatible importer ID {importerId}.");
        }

        return typed;
    }

    private static Uri ParseUrl(string value, bool allowRelative, string description)
    {
        var kind = allowRelative ? UriKind.RelativeOrAbsolute : UriKind.Absolute;
        if (!Uri.TryCreate(value, kind, out Uri? url) || (!allowRelative && !url.IsAbsoluteUri))
        {
            throw new SassProtocolException($"The compiler sent an invalid {description} '{value}'.");
        }

        return url;
    }

    private static void EnsureAbsoluteUrl(Uri url, string description)
    {
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException($"{description} must be absolute.");
        }
    }

    private static Syntax MapSyntax(SassSyntax syntax) => syntax switch
    {
        SassSyntax.Scss => Syntax.Scss,
        SassSyntax.Indented => Syntax.Indented,
        SassSyntax.Css => Syntax.Css,
        _ => throw new ArgumentOutOfRangeException(nameof(syntax), syntax, "Unknown Sass syntax.")
    };

    private static string CallbackError(Exception exception) =>
        string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message;

    private readonly record struct CallbackKey(
        OutboundMessage.MessageOneofCase Type,
        uint Id);

    private async Task RunLogWorkerAsync()
    {
        if (_logs == null || _logHandler == null)
            return;
        
        await foreach (var logEvent in _logs.Reader.ReadAllAsync(_connectionCancellation)
                           .ConfigureAwait(false))
        {
            await _logHandler(logEvent, _connectionCancellation).ConfigureAwait(false);
        }
    }
}
