# ![EmbeddedSass.Net](Logo.png)

EmbeddedSass.Net is an asynchronous, thread-safe .NET host for the [Embedded Sass Protocol](https://github.com/sass/sass/blob/df76115b586b048f161a7545ae5d0557b99e2a28/spec/embedded-protocol.md#the-embedded-sass-protocol).

The library runs a trusted Embedded Dart Sass executable supplied by the
application or by the optional compiler package.

# Getting Started

Add the package to the project:

```xml
<PackageReference Include="EmbeddedSass.Net"
                  Version="1.0.1" />
```

```csharp
using EmbeddedSass.Net;
using EmbeddedSass.Net.Compiler;

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
using EmbeddedSass.Net;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Compiler;

var options = new SassCompilerOptions().UseBundledDartSass();
await using var compiler = new SassCompiler(options);
```

## Imports

Filesystem imports can be resolved from one or more load paths:

```csharp
var result = await compiler.CompileAsync(
    new SassCompileRequest(new SassStringInput("@use 'theme';"))
    {
        LoadPaths = ["/absolute/path/to/styles"]
    });
```

## MSBuild integration

`EmbeddedSass.Net.MsBuild` compiles Sass before ASP.NET Core resolves static web assets. The package includes the Sass compiler, so a Sass installation is not required.

Add the package to the project:

```xml
<PackageReference Include="EmbeddedSass.Net.MsBuild"
                  Version="1.0.1"
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

## AI Notice
AI tools were used as part of the development process and are disclosed here for transparency.
The final code was reviewed, refactored and tested by a human (specifically me, [@gumbarros](https://www.github.com/gumbarros) ).
