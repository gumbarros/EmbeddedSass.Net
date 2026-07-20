namespace EmbeddedSass.Compilation;

public static class SassCompilerExtensions
{
    extension(ISassCompiler compiler)
    {
        public Task<SassCompileResult> CompileStringAsync(string source,
            SassSyntax syntax = SassSyntax.Scss,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(compiler);
            ArgumentNullException.ThrowIfNull(source);

            return compiler.CompileAsync(
                new SassCompileRequest(new SassStringInput(source, syntax)),
                cancellationToken);
        }

        public Task<SassCompileResult> CompileFileAsync(string path,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(compiler);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            return compiler.CompileAsync(
                new SassCompileRequest(new SassFileInput(path)),
                cancellationToken);
        }
    }
}
