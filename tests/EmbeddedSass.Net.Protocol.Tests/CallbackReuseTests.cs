using System.Buffers;
using EmbeddedSass.Diagnostics;
using EmbeddedSass.Functions;
using EmbeddedSass.Importing;
using EmbeddedSass.Internal.Process;
using EmbeddedSass.Internal.Protocol;
using EmbeddedSass.Internal.Transport;
using EmbeddedSass.Values;
using Google.Protobuf;
using Sass.EmbeddedProtocol;

namespace EmbeddedSass.Protocol.Tests;

public sealed class CallbackReuseTests
{
    [Theory]
    [InlineData("canonicalize")]
    [InlineData("import")]
    [InlineData("file")]
    [InlineData("function")]
    public async Task ResponseAllowsImmediateIdReuseBeforeSenderCompletes(string callbackType)
    {
        var pendingSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reuseFailure = null;
        var responses = 0;
        CompilationDispatcher? dispatcher = null;
        ProtocolPacket request = default;
        dispatcher = new CompilationDispatcher(1, 2, CancellationToken.None, (_, _) =>
        {
            if (++responses == 1)
            {
                // Simulate a compiler that receives the response before SendAsync resumes.
                reuseFailure = Record.Exception(() => dispatcher!.Dispatch(request));
                return pendingSend.Task;
            }

            return Task.CompletedTask;
        });
        using (dispatcher)
        {
            var importers = new ImporterRegistry();
            var contentId = importers.Register(new ContentImporter());
            var fileId = importers.Register(new FileImporter());
            var functions = new FunctionRegistry();
            functions.Register(new SassFunction("value()", (_, _) =>
                ValueTask.FromResult<SassValue>(new SassStringValue("result"))));
            var operation = await dispatcher.RegisterAsync(null, CancellationToken.None, importers, functions);
            var message = callbackType switch
            {
                "canonicalize" => new OutboundMessage
                {
                    CanonicalizeRequest = new() { Id = 0, ImporterId = contentId, Url = "theme" }
                },
                "import" => new OutboundMessage
                {
                    ImportRequest = new() { Id = 0, ImporterId = contentId, Url = "virtual:theme" }
                },
                "file" => new OutboundMessage
                {
                    FileImportRequest = new() { Id = 0, ImporterId = fileId, Url = "theme" }
                },
                "function" => new OutboundMessage
                {
                    FunctionCallRequest = new() { Id = 0, Name = "value" }
                },
                _ => throw new ArgumentOutOfRangeException(nameof(callbackType))
            };
            request = new ProtocolPacket(operation.CompilationId, new ReadOnlySequence<byte>(message.ToByteArray()));
            try
            {
                dispatcher.Dispatch(request);
                Assert.Null(reuseFailure);
                Assert.Equal(2, responses);
            }
            finally
            {
                pendingSend.TrySetResult();
                dispatcher.FailAll(new OperationCanceledException());
            }
        }
    }

    [Fact]
    public async Task PreviousSenderDoesNotRemoveReusedPendingId()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();
        CompilationDispatcher? dispatcher = null;
        ProtocolPacket request = default;
        dispatcher = new CompilationDispatcher(1, 2, lifetime.Token, (_, _) =>
        {
            // The second callback stays inside its importer when the first sender returns.
            dispatcher!.Dispatch(request);
            return Task.CompletedTask;
        });
        using (dispatcher)
        {
            var importers = new ImporterRegistry();
            var id = importers.Register(new ContentImporter(release.Task, blockAfterFirst: true));
            var operation = await dispatcher.RegisterAsync(null, CancellationToken.None, importers);
            request = new ProtocolPacket(operation.CompilationId, new ReadOnlySequence<byte>(new OutboundMessage
            {
                ImportRequest = new() { Id = 0, ImporterId = id, Url = "virtual:theme" }
            }.ToByteArray()));
            try
            {
                dispatcher.Dispatch(request);
                var duplicate = Assert.Throws<SassProtocolException>(() => dispatcher.Dispatch(request));
                Assert.Contains("Duplicate pending ImportRequest callback ID 0", duplicate.Message);
            }
            finally
            {
                // Cancellation leaves the blocked callback without a reply, avoiding another reentry.
                lifetime.Cancel();
                dispatcher.FailAll(new OperationCanceledException());
                release.TrySetCanceled();
            }
        }
    }

    [Fact]
    public async Task DuplicateIdIsRejectedWhileImporterIsStillPending()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = new CompilationDispatcher(1, 2, CancellationToken.None,
            (_, _) => Task.CompletedTask);
        var importers = new ImporterRegistry();
        var id = importers.Register(new ContentImporter(release.Task));
        var operation = await dispatcher.RegisterAsync(null, CancellationToken.None, importers);
        var request = new ProtocolPacket(operation.CompilationId, new ReadOnlySequence<byte>(new OutboundMessage
        {
            ImportRequest = new() { Id = 0, ImporterId = id, Url = "virtual:theme" }
        }.ToByteArray()));
        try
        {
            dispatcher.Dispatch(request);
            Assert.Throws<SassProtocolException>(() => dispatcher.Dispatch(request));
        }
        finally
        {
            release.TrySetResult();
            dispatcher.FailAll(new OperationCanceledException());
        }
    }

    private sealed class ContentImporter(Task? wait = null, bool blockAfterFirst = false) : ISassContentImporter
    {
        private int _loads;

        public ValueTask<SassCanonicalizeResult?> CanonicalizeAsync(SassCanonicalizeContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<SassCanonicalizeResult?>(new(new Uri("virtual:theme")));

        public async ValueTask<SassImportResult?> LoadAsync(Uri url, CancellationToken cancellationToken)
        {
            if (wait is not null && (!blockAfterFirst || ++_loads > 1))
            {
                await wait.WaitAsync(cancellationToken);
            }

            return new SassImportResult("$color: red;");
        }
    }

    private sealed class FileImporter : ISassFileImporter
    {
        public ValueTask<SassFileImportResult?> FindFileUrlAsync(SassFileImportContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<SassFileImportResult?>(new(new Uri(Path.GetFullPath("theme.scss"))));
    }
}
