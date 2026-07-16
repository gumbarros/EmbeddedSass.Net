using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EmbeddedSass.Net.Compiler;

/// <summary>Configures EmbeddedSass.Net to use the Dart Sass binary in this package.</summary>
public static class BundledDartSass
{
    public const string Version = "1.101.0";

    public static SassCompilerOptions UseBundledDartSass(this SassCompilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimeIdentifier = GetRuntimeIdentifier();
        var fileName = OperatingSystem.IsWindows() ? "dart.exe" : "dart";
        var (executable, snapshot) = FindCompilerFiles(
            AppContext.BaseDirectory,
            runtimeIdentifier,
            fileName);

        if (!OperatingSystem.IsWindows())
            MakeExecutable(executable);

        options.CompilerPath = executable;
        options.CompilerArguments = [snapshot];
        
        return options;
    }

    private static string GetRuntimeIdentifier()
    {
        string os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException(
                        "The bundled compiler supports Windows, macOS, and Linux.");

        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                "The bundled compiler supports x64 and ARM64 processes.")
        };

        return $"{os}-{architecture}";
    }

    internal static (string Executable, string Snapshot) FindCompilerFiles(
        string outputDirectory,
        string runtimeIdentifier,
        string executableFileName)
    {
        string[] candidateDirectories =
        [
            Path.Combine(outputDirectory, "EmbeddedSass.Net.Compiler"),
            outputDirectory,
            Path.Combine(
                outputDirectory,
                "runtimes",
                runtimeIdentifier,
                "native",
                "EmbeddedSass.Net.Compiler")
        ];

        foreach (var candidateDirectory in candidateDirectories)
        {
            var executable = Path.Combine(candidateDirectory, executableFileName);
            var snapshot = Path.Combine(candidateDirectory, "sass.snapshot");
            if (File.Exists(executable) && File.Exists(snapshot))
            {
                return (executable, snapshot);
            }
        }

        throw new FileNotFoundException(
            $"Could not find the bundled Dart Sass files for '{runtimeIdentifier}'. " +
            $"Searched: {string.Join(", ", candidateDirectories)}.");
    }

    [UnsupportedOSPlatform("windows")]
    private static void MakeExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute);
    }
}
