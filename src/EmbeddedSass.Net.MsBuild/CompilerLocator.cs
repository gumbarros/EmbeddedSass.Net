using System.Runtime.InteropServices;

namespace EmbeddedSass.MsBuild;

internal static class CompilerLocator
{
    public static SassCompilerOptions CreateOptions()
    {
        var operatingSystem = GetOperatingSystem();

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                "EmbeddedSass.Net.MsBuild supports x64 and ARM64 processes.")
        };
        var assemblyDirectory = Path.GetDirectoryName(typeof(CompileSassTask).Assembly.Location)!;
        var direct = Path.Combine(assemblyDirectory, "EmbeddedSass.Net.Compiler");
        var compilerDirectory = Directory.Exists(direct)
            ? direct
            : Path.Combine(
                assemblyDirectory,
                "runtimes",
                $"{operatingSystem}-{architecture}",
                "native",
                "EmbeddedSass.Net.Compiler");
        var executable = Path.Combine(
            compilerDirectory,
            OperatingSystem.IsWindows() ? "dart.exe" : "dart");
        var snapshot = Path.Combine(compilerDirectory, "sass.snapshot");

        if (!File.Exists(executable) || !File.Exists(snapshot))
        {
            throw new FileNotFoundException(
                $"The bundled Dart Sass compiler was not found under '{compilerDirectory}'.");
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(executable);
            File.SetUnixFileMode(
                executable,
                mode |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute);
        }

        return new SassCompilerOptions
        {
            CompilerPath = executable,
            CompilerArguments = [snapshot]
        };
    }

    private static string GetOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
            return "win";
        if (OperatingSystem.IsMacOS())
            return "osx";
        if (OperatingSystem.IsLinux())
            return "linux";

        throw new PlatformNotSupportedException(
            "EmbeddedSass.Net.MsBuild supports Windows, macOS, and Linux.");
    }
}
