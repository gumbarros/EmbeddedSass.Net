using EmbeddedSass.Compilation;

namespace EmbeddedSass.Importing;

/// <summary>Marks a host-defined importer that may be registered for a compilation.</summary>
public interface ISassImporter;

/// <summary>Canonicalizes Sass URLs and loads their stylesheet contents.</summary>
public interface ISassContentImporter : ISassImporter
{
    IReadOnlyList<string> NonCanonicalSchemes => [];

    ValueTask<SassCanonicalizeResult?> CanonicalizeAsync(
        SassCanonicalizeContext context,
        CancellationToken cancellationToken);

    ValueTask<SassImportResult?> LoadAsync(
        Uri canonicalUrl,
        CancellationToken cancellationToken);
}

/// <summary>Redirects Sass URLs to files while Dart Sass handles file resolution.</summary>
public interface ISassFileImporter : ISassImporter
{
    ValueTask<SassFileImportResult?> FindFileUrlAsync(
        SassFileImportContext context,
        CancellationToken cancellationToken);
}

public sealed record SassCanonicalizeContext(
    Uri Url,
    bool FromImport,
    Uri? ContainingUrl);

public sealed record SassCanonicalizeResult(
    Uri CanonicalUrl,
    bool ContainingUrlUnused = false);

public sealed record SassImportResult(
    string Contents,
    SassSyntax Syntax = SassSyntax.Scss,
    Uri? SourceMapUrl = null);

public sealed record SassFileImportContext(
    Uri Url,
    bool FromImport,
    Uri? ContainingUrl);

public sealed record SassFileImportResult(
    Uri FileUrl,
    bool ContainingUrlUnused = false);
