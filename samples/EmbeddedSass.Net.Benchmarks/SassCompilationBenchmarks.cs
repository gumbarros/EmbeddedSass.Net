using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using DartSassHost;
using EmbeddedSass.Compilation;
using EmbeddedSass.Compiler;
using JavaScriptEngineSwitcher.Jint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AspNetCoreCompiler = AspNetCore.SassCompiler.ISassCompiler;
using DartSassCompiler = DartSassHost.SassCompiler;
using EmbeddedSassCompiler = EmbeddedSass.SassCompiler;

namespace EmbeddedSass.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SassCompilationBenchmarks
{
    private const string Source = """
        @use "sass:color";
        @use "sass:map";

        $palette: (
          primary: #5b42f3,
          secondary: #00ddeb,
          danger: #dc3545
        );

        @mixin button($name, $color) {
          .button-#{$name} {
            align-items: center;
            background: linear-gradient(135deg, $color, color.adjust($color, $lightness: -12%));
            border: 1px solid color.adjust($color, $lightness: -18%);
            border-radius: 0.375rem;
            color: white;
            display: inline-flex;
            padding: 0.5rem 1rem;

            &:hover {
              background-color: color.adjust($color, $lightness: -8%);
              transform: translateY(-1px);
            }
          }
        }

        @each $name, $value in $palette {
          @include button($name, $value);
        }

        @for $column from 1 through 24 {
          .grid-column-#{$column} {
            grid-column: span $column;
            width: calc(100% * $column / 24);
          }
        }
        """;

    private static readonly string[] AspNetCoreArguments = ["--style=expanded"];

    private EmbeddedSassCompiler _embeddedCompiler = null!;
    private SassCompileRequest _embeddedRequest = null!;
    private DartSassCompiler _dartSassHostCompiler = null!;
    private ServiceProvider _serviceProvider = null!;
    private AspNetCoreCompiler _aspNetCoreCompiler = null!;
    private MemoryStream _aspNetCoreInput = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _embeddedCompiler = new EmbeddedSassCompiler(
            new SassCompilerOptions().UseBundledDartSass());
        _embeddedRequest = new SassCompileRequest(new SassStringInput(Source));

        _dartSassHostCompiler = new DartSassCompiler(
            new JintJsEngineFactory(),
            new CompilationOptions { OutputStyle = OutputStyle.Expanded });

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSassCompilerCore();
        _serviceProvider = services.BuildServiceProvider();
        _aspNetCoreCompiler = _serviceProvider.GetRequiredService<AspNetCoreCompiler>();
        _aspNetCoreInput = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Source), writable: false);

        // Persistent hosts initialize lazily. Warm them so the benchmarks measure
        // compilation throughput rather than one-time process/JavaScript startup.
        await _embeddedCompiler.CompileAsync(_embeddedRequest);
        _dartSassHostCompiler.Compile(Source, indentedSyntax: false);
        await CompileWithAspNetCoreSassCompiler();
    }

    [Benchmark(Baseline = true, Description = "EmbeddedSass.Net")]
    public Task<SassCompileResult> EmbeddedSassNet() =>
        _embeddedCompiler.CompileAsync(_embeddedRequest);

    [Benchmark(Description = "DartSassHost (Jint)")]
    public CompilationResult DartSassHostWithJint() =>
        _dartSassHostCompiler.Compile(Source, indentedSyntax: false);

    [Benchmark(Description = "AspNetCore.SassCompiler")]
    public Task<string> AspNetCoreSassCompiler() =>
        CompileWithAspNetCoreSassCompiler();

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _aspNetCoreInput.DisposeAsync();
        await _serviceProvider.DisposeAsync();
        _dartSassHostCompiler.Dispose();
        await _embeddedCompiler.DisposeAsync();
    }

    private Task<string> CompileWithAspNetCoreSassCompiler()
    {
        _aspNetCoreInput.Position = 0;
        return _aspNetCoreCompiler.CompileToStringAsync(
            _aspNetCoreInput,
            AspNetCoreArguments);
    }
}
