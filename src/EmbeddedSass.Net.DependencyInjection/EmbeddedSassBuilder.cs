using Microsoft.Extensions.DependencyInjection;

namespace EmbeddedSass.Net.DependencyInjection;

public sealed class EmbeddedSassBuilder
{
    internal EmbeddedSassBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }
}
