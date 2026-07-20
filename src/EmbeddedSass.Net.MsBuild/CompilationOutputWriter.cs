namespace EmbeddedSass.MsBuild;

internal static class CompilationOutputWriter
{
    public static bool Write(string target, SassCompileResult result, bool sourceMaps)
    {
        string? directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string css = result.Css;
        bool removedSourceMap = false;
        if (sourceMaps && result.SourceMap is not null)
        {
            WriteIfChanged(target + ".map", result.SourceMap);
            css = css.TrimEnd() + Environment.NewLine +
                  $"/*# sourceMappingURL={Path.GetFileName(target)}.map */" + Environment.NewLine;
        }
        else
        {
            removedSourceMap = File.Exists(target + ".map");
            File.Delete(target + ".map");
        }

        WriteIfChanged(target, css);
        return removedSourceMap;
    }

    private static void WriteIfChanged(string path, string contents)
    {
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), contents, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, contents);
    }
}
