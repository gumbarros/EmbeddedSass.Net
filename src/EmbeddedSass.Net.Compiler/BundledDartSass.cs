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

        string runtimeIdentifier = GetRuntimeIdentifier();
        string fileName = OperatingSystem.IsWindows() ? "dart.exe" : "dart";
        string root = FindCompilerDirectory(runtimeIdentifier);
        string executable = Path.Combine(root, fileName);
        string snapshot = Path.Combine(root, "sass.snapshot");

        if (!OperatingSystem.IsWindows())
        {
            MakeExecutable(executable);
        }

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

    private static string FindCompilerDirectory(string runtimeIdentifier)
    {
        string outputDirectory = AppContext.BaseDirectory;
        string direct = Path.Combine(outputDirectory, "EmbeddedSass.Net.Compiler");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        return Path.Combine(
            outputDirectory,
            "runtimes",
            runtimeIdentifier,
            "native",
            "EmbeddedSass.Net.Compiler");
    }

    [UnsupportedOSPlatform("windows")]
    private static void MakeExecutable(string path)
    {
        UnixFileMode mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute);
    }
}
