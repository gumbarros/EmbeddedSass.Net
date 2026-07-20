namespace EmbeddedSass;

/// <summary>Configures the compiler process and connection resource limits.</summary>
public sealed class SassCompilerOptions
{
    public string CompilerPath { get; set; } = string.Empty;

    public IReadOnlyList<string> CompilerArguments { get; set; } = [];

    public int MaxConcurrentCompilations { get; set; } = Environment.ProcessorCount;

    public int MaxPacketBytes { get; set; } = 16 * 1024 * 1024;

    public int MaxPendingWrites { get; set; } = 256;

    public int MaxPendingLogEvents { get; set; } = 256;

    public int MaxCapturedStderrBytes { get; set; } = 64 * 1024;

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(5);
}
