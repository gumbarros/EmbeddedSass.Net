using EmbeddedSass;
using EmbeddedSass.Compilation;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: EmbeddedSass.Net.Sample.Console <absolute-compiler-path>");
    return 1;
}

await using var compiler = new SassCompiler(new SassCompilerOptions
{
    CompilerPath = args[0]
});

SassCompileResult result = await compiler.CompileStringAsync(
    "$accent: #c35; .sample { color: $accent; }");
Console.WriteLine(result.Css);
return 0;
