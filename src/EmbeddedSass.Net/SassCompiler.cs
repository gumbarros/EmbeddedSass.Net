using EmbeddedSass.Compilation;
using EmbeddedSass.Internal.Process;
using EmbeddedSass.Internal.Protocol;

namespace EmbeddedSass;

public sealed class SassCompiler(SassCompilerOptions options) : ISassCompiler, IAsyncDisposable
{
    private readonly SassCompilerConnection _connection = new(CompilerOptionsSnapshot.Create(options));

    public SassCompilerInfo? CompilerInfo => _connection.Info;

    public Task<SassCompileResult> CompileAsync(
        SassCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        var mapped = CompileRequestMapper.Map(request);
        return _connection.CompileAsync(mapped, cancellationToken);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
