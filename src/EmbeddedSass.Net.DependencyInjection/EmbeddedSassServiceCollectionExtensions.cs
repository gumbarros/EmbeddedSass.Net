using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using EmbeddedSass.Net.Compilation;

namespace EmbeddedSass.Net.DependencyInjection;

public static class EmbeddedSassServiceCollectionExtensions
{
    public static EmbeddedSassBuilder AddEmbeddedSass(
        this IServiceCollection services,
        Action<SassCompilerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<SassCompilerOptions>()
            .Configure(configure)
            .Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<SassCompilerOptions>, SassCompilerOptionsValidator>());

        services.TryAddSingleton(static provider =>
            new SassCompiler(provider.GetRequiredService<IOptions<SassCompilerOptions>>().Value));
        services.TryAddSingleton<ISassCompiler>(static provider =>
            provider.GetRequiredService<SassCompiler>());

        return new EmbeddedSassBuilder(services);
    }

    public static EmbeddedSassBuilder ValidateOptionsOnStart(this EmbeddedSassBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<SassCompilerOptions>().ValidateOnStart();
        return builder;
    }

    private sealed class SassCompilerOptionsValidator : IValidateOptions<SassCompilerOptions>
    {
        public ValidateOptionsResult Validate(string? name, SassCompilerOptions options)
        {
            var failures = new List<string>();

            if (string.IsNullOrWhiteSpace(options.CompilerPath))
            {
                failures.Add("CompilerPath is required.");
            }
            else if (!Path.IsPathFullyQualified(options.CompilerPath))
            {
                failures.Add("CompilerPath must be fully qualified.");
            }

            if (options.CompilerArguments is null)
            {
                failures.Add("CompilerArguments cannot be null.");
            }
            else if (options.CompilerArguments.Any(static argument => argument is null))
            {
                failures.Add("CompilerArguments cannot contain null values.");
            }

            AddPositiveFailure(failures, options.MaxConcurrentCompilations, nameof(options.MaxConcurrentCompilations));
            AddPositiveFailure(failures, options.MaxPacketBytes, nameof(options.MaxPacketBytes));
            AddPositiveFailure(failures, options.MaxPendingWrites, nameof(options.MaxPendingWrites));
            AddPositiveFailure(failures, options.MaxPendingLogEvents, nameof(options.MaxPendingLogEvents));

            if (options.MaxCapturedStderrBytes < 0)
            {
                failures.Add("MaxCapturedStderrBytes cannot be negative.");
            }

            if (options.HandshakeTimeout <= TimeSpan.Zero)
            {
                failures.Add("HandshakeTimeout must be positive.");
            }

            if (options.ShutdownGracePeriod <= TimeSpan.Zero)
            {
                failures.Add("ShutdownGracePeriod must be positive.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        private static void AddPositiveFailure(List<string> failures, int value, string name)
        {
            if (value <= 0)
            {
                failures.Add($"{name} must be positive.");
            }
        }
    }
}
