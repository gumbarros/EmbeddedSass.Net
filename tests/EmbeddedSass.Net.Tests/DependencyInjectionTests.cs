using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.DependencyInjection;

namespace EmbeddedSass.Net.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task RegistersOneLazyCompilerSingleton()
    {
        var services = new ServiceCollection();
        services.AddEmbeddedSass(options =>
            options.CompilerPath = Path.Combine(
                Path.GetPathRoot(Environment.CurrentDirectory)!,
                "missing",
                "dart-sass"));

        await using ServiceProvider provider = services.BuildServiceProvider();
        ISassCompiler first = provider.GetRequiredService<ISassCompiler>();
        ISassCompiler second = provider.GetRequiredService<ISassCompiler>();

        Assert.Same(first, second);
        Assert.IsType<SassCompiler>(first);
    }

    [Fact]
    public void InvalidOptionsFailWhenCompilerIsFirstResolved()
    {
        var services = new ServiceCollection();
        services.AddEmbeddedSass(options => options.CompilerPath = "relative/dart-sass");
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<ISassCompiler>());

        Assert.Contains("fully qualified", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
