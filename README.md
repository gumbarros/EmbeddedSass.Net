# EmbeddedSass.Net

[![CI](https://github.com/gumbarros/EmbeddedSass.Net/actions/workflows/ci.yml/badge.svg)](https://github.com/gumbarros/EmbeddedSass.Net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/EmbeddedSass.Net.svg)](https://www.nuget.org/packages/EmbeddedSass.Net)
[![NuGet downloads](https://img.shields.io/nuget/dt/EmbeddedSass.Net.svg)](https://www.nuget.org/packages/EmbeddedSass.Net)
[![License](https://img.shields.io/github/license/gumbarros/EmbeddedSass.Net.svg)](LICENSE)

EmbeddedSass.Net is a .NET implementation for the [Embedded Sass Protocol](https://github.com/sass/sass/blob/df76115b586b048f161a7545ae5d0557b99e2a28/spec/embedded-protocol.md#the-embedded-sass-protocol).

The library runs a Dart Sass executable supplied by the
application or by the optional compiler package.

# Getting Started

Add the package to the project:

```xml
<PackageReference Include="EmbeddedSass.Net"
                  Version="1.1.0" />
```

```csharp
using EmbeddedSass;

await using var compiler = new SassCompiler(new SassCompilerOptions
{
    CompilerPath = "/absolute/path/to/dart-sass"
});

var result = await compiler.CompileStringAsync("$color: red; a { color: $color; }");
Console.WriteLine(result.Css);
```

The optional compiler package contains the x64 and ARM64 builds for Windows,
macOS, and Linux, removing the need to manage an external executable:

```xml
<PackageReference Include="EmbeddedSass.Net.Compiler"
                  Version="1.1.0" />
```

```csharp
using EmbeddedSass;
using EmbeddedSass.Compiler;

var options = new SassCompilerOptions().UseBundledDartSass();
await using var compiler = new SassCompiler(options);
```

## Dependency injection

Install the dependency-injection package in addition to a package that provides
the compiler executable. The bundled compiler is the simplest option:

```xml
<PackageReference Include="EmbeddedSass.Net.DependencyInjection"
                  Version="1.1.0" />
<PackageReference Include="EmbeddedSass.Net.Compiler"
                  Version="1.1.0" />
```

Register Embedded Sass with an `IServiceCollection` and resolve
`ISassCompiler` where it is needed:

```csharp
using EmbeddedSass.Compiler;
using EmbeddedSass.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEmbeddedSass(options => options.UseBundledDartSass());

var app = builder.Build();

app.MapPost("/compile", async (string source, ISassCompiler compiler,
    CancellationToken cancellationToken) =>
{
    var result = await compiler.CompileStringAsync(
        source, cancellationToken: cancellationToken);
    return Results.Text(result.Css, "text/css");
});

app.Run();
```

To use an externally managed Embedded Dart Sass executable instead, configure
an absolute path and omit the `EmbeddedSass.Net.Compiler` package:

```csharp
services.AddEmbeddedSass(options =>
{
    options.CompilerPath = "/absolute/path/to/dart-sass";
    options.MaxConcurrentCompilations = 4;
});
```

The registration creates one lazy, thread-safe compiler singleton and exposes
it as `ISassCompiler`.

## Imports

Filesystem imports can be resolved from one or more load paths:

```csharp
var result = await compiler.CompileAsync(
    new SassCompileRequest(new SassStringInput("@use 'theme';"))
    {
        LoadPaths = ["/absolute/path/to/styles"]
    });
```

## Custom functions

.NET callbacks can be registered as custom global Sass functions. Arguments
and return values use typed SassScript values, including numbers with units,
strings, colors, lists, maps, booleans, null, calculations, and argument lists.

```csharp
using EmbeddedSass.Functions;
using EmbeddedSass.Values;

var cdnBaseUri = new Uri("https://cdn.example.com/assets/");
var buildVersion = "2026.07.20";

var assetUrl = new SassFunction(
    "asset-url($path)",
    (arguments, _) =>
    {
        var path = (SassStringValue)arguments[0];
        var url = new Uri(cdnBaseUri, path.Text);

        return ValueTask.FromResult<SassValue>(
            new SassStringValue(
                $"{url}?v={Uri.EscapeDataString(buildVersion)}"));
    });

var result = await compiler.CompileAsync(
    new SassCompileRequest(
        new SassStringInput("""
            .hero {
              background-image: url(asset-url("images/hero.webp"));
            }
            """))
    {
        Functions = [assetUrl]
    });

// .hero {
//   background-image: url("https://cdn.example.com/assets/images/hero.webp?v=2026.07.20");
// }
Console.WriteLine(result.Css);
```

Function signatures use normal Sass declaration syntax and may contain
optional or variadic arguments, for example
`lookup($name, $fallback: null)` or `collect($values...)`. Accessing
`SassArgumentListValue.Keywords` reports keyword access back to Dart Sass with
the same semantics as `meta.keywords()`. An exception thrown by a callback is
reported as a Sass compilation error.

Implement `ISassFunction` directly when a class-based callback is preferable
to the `SassFunction` delegate wrapper. Function and importer callbacks may run
concurrently when the compiler processes concurrent requests, so callback
implementations should be thread-safe.

## MSBuild integration

`EmbeddedSass.Net.MsBuild` compiles Sass before ASP.NET Core resolves static web assets. The package includes the Sass compiler, so a Sass installation is not required.

Add the package to the project:

```xml
<PackageReference Include="EmbeddedSass.Net.MsBuild"
                  Version="1.1.0"
                  PrivateAssets="all" />
```

`PrivateAssets="all"` keeps this project-local build dependency from flowing to projects or packages that reference the project.

### Default behavior

When the project contains a `Sass` directory, the package:

* Compiles every `.scss` and `.sass` file that does not begin with `_`.
* Writes generated CSS to `wwwroot/css`.
* Preserves relative directory paths.
* Treats underscore-prefixed files as Sass partials.
* Produces expanded CSS and external source maps in Debug builds.
* Embeds source content in Debug source maps.
* Produces compressed CSS without source maps in other configurations.

### Explicit inputs

Add `EmbeddedSass` items to override the default `Sass`-to-`wwwroot/css` mapping.

```xml
<ItemGroup>
  <EmbeddedSass Include="Client/Styles">
    <OutputPath>wwwroot/assets/css</OutputPath>
    <LoadPaths>Client/Shared;vendor/styles</LoadPaths>
  </EmbeddedSass>

  <EmbeddedSass Include="Admin/admin.scss">
    <OutputPath>wwwroot/admin.css</OutputPath>
  </EmbeddedSass>
</ItemGroup>
```

For a directory item, `OutputPath` is the destination directory. Relative source directories are preserved.

For a file item, `OutputPath` is the exact generated CSS file path.

Explicit items can reference multiple source directories or individual entry files.

### Item metadata

| Parameter    | Applies to        | Description                                                                         |
| ------------ | ----------------- | ----------------------------------------------------------------------------------- |
| `Include`    | Directory or file | Source directory or Sass entry file to compile.                                     |
| `OutputPath` | Directory or file | Destination directory for a directory item, or exact CSS file path for a file item. |
| `LoadPaths`  | Directory or file | Semicolon-separated directories Sass can search when resolving imports and modules. |

### Project properties

```xml
<PropertyGroup>
  <EmbeddedSassEnabled>true</EmbeddedSassEnabled>
  <EmbeddedSassOutputStyle>Auto</EmbeddedSassOutputStyle>
  <EmbeddedSassGenerateSourceMap>Auto</EmbeddedSassGenerateSourceMap>
  <EmbeddedSassIncludeSourcesInSourceMap>true</EmbeddedSassIncludeSourcesInSourceMap>
  <EmbeddedSassQuietDependencies>false</EmbeddedSassQuietDependencies>
  <EmbeddedSassSilencedDeprecations></EmbeddedSassSilencedDeprecations>
  <EmbeddedSassCacheFile>obj/$(Configuration)/EmbeddedSass.cache.json</EmbeddedSassCacheFile>
</PropertyGroup>
```

| Property                                | Values                              | Default behavior                                                                       |
| --------------------------------------- | ----------------------------------- | -------------------------------------------------------------------------------------- |
| `EmbeddedSassEnabled`                   | `true`, `false`                     | Enables or disables Sass compilation.                                                  |
| `EmbeddedSassOutputStyle`               | `Auto`, `Expanded`, `Compressed`    | `Auto` uses expanded CSS for Debug builds and compressed CSS for other configurations. |
| `EmbeddedSassGenerateSourceMap`         | `Auto`, `true`, `false`             | `Auto` generates source maps for Debug builds only.                                    |
| `EmbeddedSassIncludeSourcesInSourceMap` | `true`, `false`                     | Controls whether original Sass source content is embedded in source maps.              |
| `EmbeddedSassQuietDependencies`         | `true`, `false`                     | Suppresses warnings generated by dependencies loaded through Sass load paths.          |
| `EmbeddedSassSilencedDeprecations`      | Semicolon-separated deprecation IDs | Suppresses selected Sass deprecation warnings. Leave empty to suppress none.           |
| `EmbeddedSassCacheFile`                 | File path                           | Stores incremental compilation state. Defaults under `obj/$(Configuration)`.           |

Generated CSS and source-map files are registered as project content and included during publish. Generated files and the incremental cache are removed by the standard MSBuild clean process.

See the [EmbeddedSass.Net.Sample.AspNetCore](https://github.com/gumbarros/EmbeddedSass.Net/tree/main/samples/EmbeddedSass.Net.Sample.AspNetCore) sample for a complete application.

## Benchmarks

- BenchmarkDotNet 0.15.8
- .NET SDK 10.0.110 / .NET 10.0.10
- Linux Ubuntu 26.04, x64
- Intel Core 7 150U, 10 physical cores and 12 logical cores
- 3 warmup iterations, 3 measurement iterations, 1 launch

| Compiler                                                                        |        Mean | Standard deviation | Ratio |   Allocated |
| ------------------------------------------------------------------------------- | ----------: | -----------------: | ----: | ----------: |
| EmbeddedSass.Net                |    688.4 us |           153.8 us |  1.03 |     7.70 KB |
| [AspNetCore.SassCompiler](https://github.com/koenvzeijl/AspNetCore.SassCompiler) |  8,360.0 us |           204.7 us | 12.52 |   104.81 KB |
| [DartSassHost (Jint)](https://github.com/Taritsyn/DartSassHost)                 | 60,094.8 us |         7,904.3 us | 89.98 | 4,144.15 KB |


EmbeddedSass.Net starts an Embedded Sass process and reuses it across compilations. DartSassHost
similarly reuses its initialized Jint engine. AspNetCore.SassCompiler instead launches a new Dart
Sass process for every runtime compilation call.

## AI Notice
AI tools were used as part of the development process and are disclosed here for transparency.
The final code was reviewed, refactored and tested by a human (specifically me, [@gumbarros](https://www.github.com/gumbarros) ).
