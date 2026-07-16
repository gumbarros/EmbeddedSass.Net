# EmbeddedSass.Net benchmarks

This project compares steady-state in-memory SCSS compilation with:

- EmbeddedSass.Net using its persistent Embedded Sass process;
- DartSassHost using JavaScriptEngineSwitcher.Jint;
- AspNetCore.SassCompiler using its runtime `ISassCompiler` API.

The persistent compilers are warmed during global setup. AspNetCore.SassCompiler starts a
Dart Sass process for every call by design, so that startup remains part of its measurement.
All three compile the same source with expanded output and no source map.

## Results

Results from a BenchmarkDotNet short run on July 16, 2026:

- BenchmarkDotNet 0.15.8
- .NET SDK 10.0.110 / .NET 10.0.10
- Linux Ubuntu 26.04, x64
- Intel Core 7 150U, 10 physical cores and 12 logical cores
- 3 warmup iterations, 3 measurement iterations, 1 launch

| Compiler | Mean | Standard deviation | Ratio | Allocated |
|---|---:|---:|---:|---:|
| EmbeddedSass.Net | 824.0 us | 111.6 us | 1.01 | 7.98 KB |
| AspNetCore.SassCompiler | 8,111.4 us | 197.2 us | 9.96 | 104.54 KB |
| DartSassHost (Jint) | 65,665.3 us | 3,392.1 us | 80.62 | 4,203.17 KB |



### Architectural asymmetry

EmbeddedSass.Net starts an Embedded Sass process and reuses it across compilations. DartSassHost
similarly reuses its initialized Jint engine. AspNetCore.SassCompiler instead launches a new Dart
Sass process for every runtime compilation call.

This is a key asymmetry: Jint and EmbeddedSass.Net reuse engines or processes, while
AspNetCore.SassCompiler launches Dart Sass per call. Startup and steady-state results therefore
need distinct interpretation. The table above measures warmed, steady-state calls for
EmbeddedSass.Net and DartSassHost, but includes unavoidable process startup for every
AspNetCore.SassCompiler call. It is not a comparison of one-time cold initialization costs.

Run the benchmarks from the repository root:

```shell
dotnet run -c Release --project samples/EmbeddedSass.Net.Benchmarks
```

Pass normal BenchmarkDotNet arguments after `--`, for example:

```shell
dotnet run -c Release --project samples/EmbeddedSass.Net.Benchmarks -- --filter "*SassCompilation*" --job short
```
