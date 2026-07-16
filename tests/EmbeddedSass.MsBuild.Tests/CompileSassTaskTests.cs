using System.Collections;
using EmbeddedSass.Net.MsBuild;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace EmbeddedSass.MsBuild.Tests;

public sealed class CompileSassTaskTests
{
    [Fact]
    public void CompilesNonPartialFilesAndWritesDebugSourceMaps()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.CreateDirectory("Sass");
        directory.Write("Sass/_theme.scss", "$accent: rebeccapurple;");
        directory.Write("Sass/site.scss", "@use 'theme'; .site { color: theme.$accent; }");
        directory.Write("Sass/admin/dashboard.sass", ".dashboard\n  display: grid");
        var buildEngine = new RecordingBuildEngine();
        var item = new TaskItem(source);
        item.SetMetadata("OutputPath", "wwwroot/css");

        var task = CreateTask(directory.Path, buildEngine, item);

        Assert.True(task.Execute());
        string siteCss = directory.File("wwwroot/css/site.css");
        Assert.Contains("rebeccapurple", File.ReadAllText(siteCss));
        Assert.Contains("sourceMappingURL=site.css.map", File.ReadAllText(siteCss));
        Assert.True(File.Exists(siteCss + ".map"));
        Assert.Contains("sourcesContent", File.ReadAllText(siteCss + ".map"));
        Assert.True(File.Exists(directory.File("wwwroot/css/admin/dashboard.css")));
        Assert.False(File.Exists(directory.File("wwwroot/css/_theme.css")));
        Assert.Equal(4, task.GeneratedFiles.Length);
        Assert.Empty(buildEngine.Errors);
    }

    [Fact]
    public void UsesCompressedCssWithoutMapsOutsideDebug()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("Sass/site.scss", ".site { color: red; }");
        var item = new TaskItem("Sass/site.scss");
        item.SetMetadata("OutputPath", "wwwroot/site.css");
        var task = CreateTask(directory.Path, new RecordingBuildEngine(), item);
        Assert.True(task.Execute());
        Assert.True(File.Exists(directory.File("wwwroot/site.css.map")));
        task = CreateTask(directory.Path, new RecordingBuildEngine(), item);
        task.Configuration = "Release";

        Assert.True(task.Execute());

        Assert.Equal(".site{color:red}", File.ReadAllText(directory.File("wwwroot/site.css")).Trim());
        Assert.False(File.Exists(directory.File("wwwroot/site.css.map")));
        Assert.Equal(
            Path.Combine("wwwroot", "site.css.map"),
            Assert.Single(task.RemovedFiles).ItemSpec);
    }

    [Fact]
    public void SupportsLoadPaths()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("Sass/site.scss", "@use 'tokens'; .site { gap: tokens.$gap; }");
        directory.Write("Shared/_tokens.scss", "$gap: 12px;");
        var item = new TaskItem("Sass/site.scss");
        item.SetMetadata("OutputPath", "wwwroot/site.css");
        item.SetMetadata("LoadPaths", "Shared");

        var task = CreateTask(directory.Path, new RecordingBuildEngine(), item);

        Assert.True(task.Execute());
        Assert.Contains("12px", File.ReadAllText(directory.File("wwwroot/site.css")));
    }

    [Fact]
    public void RejectsOutputCollisions()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("one.scss", ".one { color: red; }");
        directory.Write("two.scss", ".two { color: blue; }");
        var first = new TaskItem("one.scss");
        first.SetMetadata("OutputPath", "wwwroot/site.css");
        var second = new TaskItem("two.scss");
        second.SetMetadata("OutputPath", "wwwroot/site.css");
        var buildEngine = new RecordingBuildEngine();

        var task = CreateTask(directory.Path, buildEngine, first, second);

        Assert.False(task.Execute());
        Assert.Contains(
            buildEngine.Errors,
            error => error.Message?.Contains("both map", StringComparison.Ordinal) == true);
    }

    private static CompileSassTask CreateTask(
        string projectDirectory,
        IBuildEngine buildEngine,
        params ITaskItem[] items) =>
        new()
        {
            BuildEngine = buildEngine,
            ProjectDirectory = projectDirectory,
            Configuration = "Debug",
            Compilations = items
        };

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];
        public List<BuildWarningEventArgs> Warnings { get; } = [];

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs) => false;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"embedded-sass-msbuild-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string relativePath)
        {
            string path = File(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Write(string relativePath, string contents)
        {
            string path = File(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, contents);
        }

        public string File(string relativePath) => System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
