using System.Collections.Concurrent;
using EmbeddedSass.Diagnostics;
using EmbeddedSass.Internal.Protocol;
using EmbeddedSass.Internal.Transport;
using Google.Protobuf;
using Sass.EmbeddedProtocol;

namespace EmbeddedSass.Internal.Process;

internal sealed class CompilationDispatcher : IDisposable
{
    private const uint VersionRequestId = 1;

    private readonly ConcurrentDictionary<uint, CompilationOperation> _compilations = new();
    private readonly ProtocolIdAllocator _compilationIds = new();
    private readonly SemaphoreSlim _compilationSlots;
    private readonly int _maximumPendingLogs;
    private readonly CancellationToken _connectionCancellation;
    private readonly Func<uint, InboundMessage, Task> _sendAsync;
    private readonly Action<Exception> _fatalCallbackFailure;
    private readonly TaskCompletionSource<SassCompilerInfo> _version =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CompilationDispatcher(
        int maximumConcurrentCompilations,
        int maximumPendingLogs,
        CancellationToken connectionCancellation,
        Func<uint, InboundMessage, Task>? sendAsync = null,
        Action<Exception>? fatalCallbackFailure = null)
    {
        _compilationSlots = new SemaphoreSlim(
            maximumConcurrentCompilations,
            maximumConcurrentCompilations);
        _maximumPendingLogs = maximumPendingLogs;
        _connectionCancellation = connectionCancellation;
        _sendAsync = sendAsync ?? ((_, _) =>
            Task.FromException(new SassProtocolException("No callback sender was configured.")));
        _fatalCallbackFailure = fatalCallbackFailure ?? FailAll;
    }

    public Task<SassCompilerInfo> Version => _version.Task;

    public static InboundMessage CreateVersionRequest() =>
        new()
        {
            VersionRequest = new InboundMessage.Types.VersionRequest { Id = VersionRequestId }
        };

    public async Task<CompilationOperation> RegisterAsync(
        SassLogHandler? logHandler,
        CancellationToken cancellationToken,
        ImporterRegistry? importers = null)
    {
        await _compilationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

        uint compilationId;
        try
        {
            compilationId = _compilationIds.Rent(_compilations.ContainsKey);
        }
        catch
        {
            _compilationSlots.Release();
            throw;
        }

        var operation = new CompilationOperation(
            compilationId,
            logHandler,
            importers ?? new ImporterRegistry(),
            _maximumPendingLogs,
            _sendAsync,
            _fatalCallbackFailure,
            () => _compilationSlots.Release(), 
            _connectionCancellation);

        if (_compilations.TryAdd(compilationId, operation))
        {
            return operation;
        }

        var exception = new SassProtocolException(
            $"Duplicate compilation ID {compilationId} was allocated.");
        operation.Fail(exception);
        throw exception;
    }

    public void Dispatch(ProtocolPacket packet)
    {
        if (packet.CompilationId == uint.MaxValue)
        {
            throw new SassProtocolException(
                "The protocol error sentinel cannot be used as a packet compilation ID.");
        }

        OutboundMessage message;
        try
        {
            message = OutboundMessage.Parser.ParseFrom(packet.Payload);
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

        if (!_compilations.TryGetValue(packet.CompilationId, out CompilationOperation? operation))
        {
            throw new SassProtocolException(
                $"The compiler sent a message for unknown compilation ID {packet.CompilationId}.");
        }

        switch (message.MessageCase)
        {
            case OutboundMessage.MessageOneofCase.LogEvent:
                if (!operation.TryAddLog(message.LogEvent))
                {
                    throw new SassProtocolException(
                        $"Compilation {packet.CompilationId} exceeded the pending log event limit.");
                }

                break;

            case OutboundMessage.MessageOneofCase.CompileResponse:
                CompilationOperation.Validate(message.CompileResponse);
                if (!_compilations.TryRemove(packet.CompilationId, out CompilationOperation? completed) ||
                    !ReferenceEquals(completed, operation))
                {
                    throw new SassProtocolException(
                        $"Compilation {packet.CompilationId} received a duplicate terminal response.");
                }

                operation.Complete(message.CompileResponse);
                break;

            case OutboundMessage.MessageOneofCase.CanonicalizeRequest:
                operation.HandleCanonicalize(message.CanonicalizeRequest);
                break;

            case OutboundMessage.MessageOneofCase.ImportRequest:
                operation.HandleImport(message.ImportRequest);
                break;

            case OutboundMessage.MessageOneofCase.FileImportRequest:
                operation.HandleFileImport(message.FileImportRequest);
                break;

            case OutboundMessage.MessageOneofCase.FunctionCallRequest:
                throw new SassProtocolException(
                    $"The compiler sent unsupported callback {message.MessageCase} for compilation {packet.CompilationId}.");

            default:
                throw new SassProtocolException(
                    $"The compiler sent {message.MessageCase} with nonzero compilation ID {packet.CompilationId}.");
        }
    }

    public void Fail(CompilationOperation operation, Exception exception)
    {
        if (_compilations.TryRemove(operation.CompilationId, out CompilationOperation? removed) &&
            ReferenceEquals(removed, operation))
        {
            operation.Fail(exception);
        }
    }

    public void FailAll(Exception exception)
    {
        _version.TrySetException(exception);
        foreach ((uint compilationId, CompilationOperation _) in _compilations)
        {
            if (_compilations.TryRemove(compilationId, out var operation))
            {
                operation.Fail(exception);
            }
        }
    }

    public void Dispose() => _compilationSlots.Dispose();

    private void DispatchVersion(OutboundMessage message)
    {
        if (message.MessageCase != OutboundMessage.MessageOneofCase.VersionResponse)
        {
            throw new SassProtocolException(
                $"The compiler sent {message.MessageCase} with reserved compilation ID 0.");
        }

        var response = message.VersionResponse;
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
}
