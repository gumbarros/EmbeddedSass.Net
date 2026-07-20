using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using EmbeddedSass.Net.MsBuild;

namespace EmbeddedSass.MsBuild.Tests;

public sealed class StaticWebAssetsTests
{
    [Fact]
    public async Task CleanRazorClassLibraryPackIncludesGeneratedCssAsAStaticWebAsset()
    {
        using var directory = new TemporaryDirectory();
        string targets = FindRepositoryFile(
            "src",
            "EmbeddedSass.Net.MsBuild",
            "build",
            "EmbeddedSass.Net.MsBuild.targets");
        string taskAssembly = typeof(CompileSassTask).Assembly.Location;
        directory.Write("Styles/site.scss", ".site { color: rebeccapurple; }");
        directory.Write(
            "TestRcl.csproj",
            $$"""
              <Project Sdk="Microsoft.NET.Sdk.Razor">
                <PropertyGroup>
                  <TargetFramework>net10.0</TargetFramework>
                  <PackageId>TestRcl</PackageId>
                  <Version>1.0.0</Version>
                  <EmbeddedSassEnabled>true</EmbeddedSassEnabled>
                  <EmbeddedSassOutputStyle>Auto</EmbeddedSassOutputStyle>
                  <EmbeddedSassGenerateSourceMap>Auto</EmbeddedSassGenerateSourceMap>
                  <EmbeddedSassIncludeSourcesInSourceMap>true</EmbeddedSassIncludeSourcesInSourceMap>
                  <EmbeddedSassQuietDependencies>false</EmbeddedSassQuietDependencies>
                  <EmbeddedSassTasksAssembly>{{SecurityElement.Escape(taskAssembly)}}</EmbeddedSassTasksAssembly>
                  <EmbeddedSassGeneratedFilesManifest>$(BaseIntermediateOutputPath)$(Configuration)/EmbeddedSass.generated.txt</EmbeddedSassGeneratedFilesManifest>
                  <EmbeddedSassCacheFile>$(BaseIntermediateOutputPath)$(Configuration)/EmbeddedSass.cache.json</EmbeddedSassCacheFile>
                </PropertyGroup>
                <ItemGroup>
                  <EmbeddedSass Include="Styles/site.scss">
                    <OutputPath>wwwroot/css/site.css</OutputPath>
                  </EmbeddedSass>
                </ItemGroup>
                <Import Project="{{SecurityElement.Escape(targets)}}" />
              </Project>
              """);
        Assert.False(File.Exists(directory.File("wwwroot/css/site.css")));

        ProcessResult result = await RunDotNetPackAsync(directory.Path);

        Assert.True(result.ExitCode == 0, result.Output);
        using ZipArchive package = ZipFile.OpenRead(directory.File("packages/TestRcl.1.0.0.nupkg"));
        Assert.Contains(package.Entries, entry => entry.FullName == "staticwebassets/css/site.css");
    }

    private static async Task<ProcessResult> RunDotNetPackAsync(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("pack");
        startInfo.ArgumentList.Add("TestRcl.csproj");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("packages");
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await standardOutput + await standardError;
        return new ProcessResult(process.ExitCode, output);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(path)}'.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"embedded-sass-static-assets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string relativePath, string contents)
        {
            string path = File(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, contents);
        }

        public string File(string relativePath) =>
            System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
