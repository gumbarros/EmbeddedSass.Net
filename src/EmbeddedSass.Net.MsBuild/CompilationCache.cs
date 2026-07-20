using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbeddedSass.Compilation;

namespace EmbeddedSass.MsBuild;

internal sealed class CompilationCache
{
    private const int CurrentVersion = 1;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string _path;
    private readonly string _compilerFingerprint;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CachedCompilation> _entries;
    private bool _dirty;

    private CompilationCache(
        string path,
        string compilerFingerprint,
        IEnumerable<CachedCompilation> entries,
        bool dirty)
    {
        _path = path;
        _compilerFingerprint = compilerFingerprint;
        _entries = entries.ToDictionary(static entry => entry.Source, PathComparer);
        _dirty = dirty;
    }

    public static CompilationCache Load(string path, SassCompilerOptions options)
    {
        string compilerFingerprint = CreateCompilerFingerprint(options);
        try
        {
            if (File.Exists(path))
            {
                var document = JsonSerializer.Deserialize<CacheDocument>(
                    File.ReadAllText(path));
                if (document is
                    {
                        Version: CurrentVersion,
                        CompilerFingerprint: var cachedFingerprint,
                        Entries: { } cachedEntries
                    } &&
                    string.Equals(cachedFingerprint, compilerFingerprint, StringComparison.Ordinal) &&
                    IsValid(cachedEntries))
                {
                    return new CompilationCache(path, compilerFingerprint, cachedEntries, dirty: false);
                }
            }
        }
        catch (Exception exception) when (exception is
                                              IOException or
                                              UnauthorizedAccessException or
                                              JsonException or
                                              ArgumentException or
                                              InvalidOperationException)
        {
        }

        return new CompilationCache(path, compilerFingerprint, [], dirty: File.Exists(path));
    }

    private static bool IsValid(IReadOnlyList<CachedCompilation> entries) =>
        entries.All(static entry =>
            entry is
            {
                Source: not null,
                Target: not null,
                SettingsFingerprint: not null,
                Dependencies: { } dependencies
            } && dependencies.All(static dependency => dependency is { Path: not null }));

    public bool IsFresh(CompilationEntry entry, string settingsFingerprint, bool sourceMaps)
    {
        CachedCompilation? cached;
        lock (_lock)
        {
            _entries.TryGetValue(entry.Source, out cached);
        }

        if (cached is null ||
            !PathComparer.Equals(cached.Target, entry.Target) ||
            !string.Equals(cached.SettingsFingerprint, settingsFingerprint, StringComparison.Ordinal) ||
            !File.Exists(entry.Target) ||
            sourceMaps != File.Exists(entry.Target + ".map"))
        {
            return false;
        }

        foreach (var dependency in cached.Dependencies)
        {
            var file = new FileInfo(dependency.Path);
            if (!file.Exists ||
                file.Length != dependency.Length ||
                file.LastWriteTimeUtc.Ticks != dependency.LastWriteTimeUtcTicks)
            {
                return false;
            }
        }

        return cached.Dependencies.Count > 0;
    }

    public void Record(
        CompilationEntry entry,
        string settingsFingerprint,
        SassCompileResult result)
    {
        var dependencyPaths = result.LoadedUrls
            .Where(static url => url.IsFile && string.IsNullOrEmpty(url.Host))
            .Select(static url => Path.GetFullPath(url.LocalPath))
            .Append(entry.Source)
            .Distinct(PathComparer)
            .Order(PathComparer);
        var dependencies = new List<CachedDependency>();
        foreach (string path in dependencyPaths)
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                dependencies.Add(new CachedDependency(
                    path,
                    file.Length,
                    file.LastWriteTimeUtc.Ticks));
            }
        }

        var cached = new CachedCompilation(
            entry.Source,
            entry.Target,
            settingsFingerprint,
            dependencies);
        lock (_lock)
        {
            _entries[entry.Source] = cached;
            _dirty = true;
        }
    }

    public void Retain(IEnumerable<CompilationEntry> entries)
    {
        var retained = entries.Select(static entry => entry.Source).ToHashSet(PathComparer);
        lock (_lock)
        {
            foreach (var source in _entries.Keys.ToArray())
            {
                if (!retained.Contains(source) && _entries.Remove(source))
                {
                    _dirty = true;
                }
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new CacheDocument(
                CurrentVersion,
                _compilerFingerprint,
                _entries.Values.OrderBy(static entry => entry.Source, PathComparer).ToArray());
            string temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document));
                File.Move(temporaryPath, _path, overwrite: true);
                _dirty = false;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string CreateSettingsFingerprint(
        CompilationEntry entry,
        SassOutputStyle style,
        bool sourceMaps,
        bool includeSources,
        bool quietDependencies,
        IReadOnlyList<string> silencedDeprecations)
    {
        var values = new List<string>
        {
            entry.Source,
            entry.Target,
            style.ToString(),
            sourceMaps.ToString(),
            includeSources.ToString(),
            quietDependencies.ToString()
        };
        values.AddRange(entry.LoadPaths);
        values.Add(string.Empty);
        values.AddRange(silencedDeprecations);
        return Hash(values);
    }

    private static string CreateCompilerFingerprint(SassCompilerOptions options)
    {
        var values = new List<string>
        {
            GetFileFingerprint(typeof(CompilationCache).Assembly.Location),
            GetFileFingerprint(options.CompilerPath)
        };
        foreach (var argument in options.CompilerArguments)
        {
            values.Add(File.Exists(argument) ? GetFileFingerprint(argument) : argument);
        }

        return Hash(values);
    }

    private static string GetFileFingerprint(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? $"{Path.GetFullPath(path)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}"
            : Path.GetFullPath(path);
    }

    private static string Hash(IEnumerable<string> values)
    {
        var value = string.Join('\0', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed record CacheDocument(
        int Version,
        string CompilerFingerprint,
        IReadOnlyList<CachedCompilation> Entries);

    private sealed record CachedCompilation(
        string Source,
        string Target,
        string SettingsFingerprint,
        IReadOnlyList<CachedDependency> Dependencies);

    private sealed record CachedDependency(
        string Path,
        long Length,
        long LastWriteTimeUtcTicks);
}
