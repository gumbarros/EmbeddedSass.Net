namespace EmbeddedSass;

/// <summary>Compiles Sass stylesheets asynchronously.</summary>
public interface ISassCompiler
{
    /// <summary>Compiles a Sass request.</summary>
    Task<SassCompileResult> CompileAsync(
        SassCompileRequest request,
        CancellationToken cancellationToken = default);
}
