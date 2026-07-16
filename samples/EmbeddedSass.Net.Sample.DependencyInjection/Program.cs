using Microsoft.Extensions.DependencyInjection;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Compiler;
using EmbeddedSass.Net.DependencyInjection;

var services = new ServiceCollection();

services.AddEmbeddedSass(options=> options.UseBundledDartSass());

await using var provider = services.BuildServiceProvider();
var compiler = provider.GetRequiredService<ISassCompiler>();
var result = await compiler.CompileStringAsync(".sample { display: grid; }");
Console.WriteLine(result.Css);
return 0;
