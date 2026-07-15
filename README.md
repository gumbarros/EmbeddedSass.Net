# EmbeddedSass.Net

EmbeddedSass.Net is an asynchronous, thread-safe .NET host for the [Embedded Sass Protocol](https://github.com/sass/sass/blob/main/spec/embedded-protocol.md#the-embedded-sass-protocol).

The library runs a trusted Embedded Dart Sass executable supplied by the
application or by the optional compiler package.

```csharp
await using var compiler = new SassCompiler(new SassCompilerOptions
{
    CompilerPath = "/absolute/path/to/dart-sass"
});

var result = await compiler.CompileStringAsync("$color: red; a { color: $color; }");
Console.WriteLine(result.Css);
```

The optional compiler package contains the x64 and ARM64 builds for Windows,
macOS, and Linux, removing the need to manage an external executable:

```csharp
using EmbeddedSass.Net.Compiler;

var options = new SassCompilerOptions().UseBundledDartSass();
await using var compiler = new SassCompiler(options);
```
