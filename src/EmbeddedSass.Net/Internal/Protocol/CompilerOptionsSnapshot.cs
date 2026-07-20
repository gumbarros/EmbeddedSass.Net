namespace EmbeddedSass.Internal.Protocol;

internal sealed class CompilerOptionsSnapshot
{
    private CompilerOptionsSnapshot(
        string compilerPath,
        IReadOnlyList<string> compilerArguments,
        int maxConcurrentCompilations,
        int maxPacketBytes,
        int maxPendingWrites,
        int maxPendingLogEvents,
        int maxCapturedStderrBytes,
        TimeSpan handshakeTimeout,
        TimeSpan shutdownGracePeriod)
    {
        CompilerPath = compilerPath;
        CompilerArguments = compilerArguments;
        MaxConcurrentCompilations = maxConcurrentCompilations;
        MaxPacketBytes = maxPacketBytes;
        MaxPendingWrites = maxPendingWrites;
        MaxPendingLogEvents = maxPendingLogEvents;
        MaxCapturedStderrBytes = maxCapturedStderrBytes;
        HandshakeTimeout = handshakeTimeout;
        ShutdownGracePeriod = shutdownGracePeriod;
    }

    public string CompilerPath { get; }

    public IReadOnlyList<string> CompilerArguments { get; }

    public int MaxConcurrentCompilations { get; }

    public int MaxPacketBytes { get; }

    public int MaxPendingWrites { get; }

    public int MaxPendingLogEvents { get; }

    public int MaxCapturedStderrBytes { get; }

    public TimeSpan HandshakeTimeout { get; }

    public TimeSpan ShutdownGracePeriod { get; }

    public static CompilerOptionsSnapshot Create(SassCompilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CompilerPath);
        ArgumentNullException.ThrowIfNull(options.CompilerArguments);

        if (options.CompilerArguments.Any(static argument => argument is null))
        {
            throw new ArgumentException(
                "Compiler arguments cannot contain null values.",
                nameof(options));
        }

        if (!Path.IsPathFullyQualified(options.CompilerPath))
        {
            throw new ArgumentException(
                "The compiler path must be fully qualified.",
                nameof(options));
        }

        ValidatePositive(
            options.MaxConcurrentCompilations,
            nameof(options.MaxConcurrentCompilations));

        ValidatePositive(
            options.MaxPacketBytes,
            nameof(options.MaxPacketBytes));

        ValidatePositive(
            options.MaxPendingWrites,
            nameof(options.MaxPendingWrites));

        ValidatePositive(
            options.MaxPendingLogEvents,
            nameof(options.MaxPendingLogEvents));

        if (options.MaxCapturedStderrBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxCapturedStderrBytes cannot be negative.");
        }

        ValidatePositive(
            options.HandshakeTimeout,
            nameof(options.HandshakeTimeout));

        ValidatePositive(
            options.ShutdownGracePeriod,
            nameof(options.ShutdownGracePeriod));

        return new CompilerOptionsSnapshot(
            options.CompilerPath,
            options.CompilerArguments.ToArray(),
            options.MaxConcurrentCompilations,
            options.MaxPacketBytes,
            options.MaxPendingWrites,
            options.MaxPendingLogEvents,
            options.MaxCapturedStderrBytes,
            options.HandshakeTimeout,
            options.ShutdownGracePeriod);
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SassCompilerOptions),
                $"{name} must be positive.");
        }
    }

    private static void ValidatePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SassCompilerOptions),
                $"{name} must be positive.");
        }
    }
}