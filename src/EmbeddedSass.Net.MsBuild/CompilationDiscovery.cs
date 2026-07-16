using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace EmbeddedSass.Net.MsBuild;

internal static class CompilationDiscovery
{
    public static List<CompilationEntry> Discover(
        ITaskItem[] compilations,
        string projectDirectory,
        TaskLoggingHelper log)
    {
        projectDirectory = Path.GetFullPath(projectDirectory);
        var entries = new List<CompilationEntry>();
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in compilations)
        {
            string source = ResolvePath(projectDirectory, item.ItemSpec);
            string targetMetadata = item.GetMetadata("OutputPath");
            if (string.IsNullOrWhiteSpace(targetMetadata))
            {
                log.LogError("EmbeddedSass item '{0}' must define OutputPath metadata.", item.ItemSpec);
                continue;
            }

            string target = ResolvePath(projectDirectory, targetMetadata);
            string[] loadPaths = item.GetMetadata("LoadPaths")
                .Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => ResolvePath(projectDirectory, path))
                .ToArray();

            if (File.Exists(source))
            {
                if (!IsSassFile(source) || IsPartial(source))
                {
                    log.LogError("EmbeddedSass source '{0}' must be a non-partial .scss or .sass file.", source);
                    continue;
                }

                AddEntry(source, target, loadPaths);
                continue;
            }

            if (!Directory.Exists(source))
            {
                log.LogError("EmbeddedSass source '{0}' does not exist.", source);
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                         .Where(IsSassFile)
                         .Where(static path => !IsPartial(path))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(source, file);
                string output = Path.Combine(target, Path.ChangeExtension(relative, ".css"));
                AddEntry(file, output, loadPaths);
            }
        }

        return entries;

        void AddEntry(string source, string target, string[] loadPaths)
        {
            target = Path.GetFullPath(target);
            if (targets.TryGetValue(target, out var existing))
            {
                log.LogError("Sass sources '{0}' and '{1}' both map to '{2}'.", existing, source, target);
                return;
            }

            targets.Add(target, source);
            entries.Add(new CompilationEntry(Path.GetFullPath(source), target, loadPaths));
        }
    }

    private static string ResolvePath(string projectDirectory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(projectDirectory, path));

    private static bool IsSassFile(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".scss", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".sass", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPartial(string path) => Path.GetFileName(path).StartsWith('_');
}
