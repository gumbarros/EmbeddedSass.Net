using Sass.EmbeddedProtocol;
using EmbeddedSass.Net.Compilation;
using EmbeddedSass.Net.Diagnostics;

namespace EmbeddedSass.Net.Internal.Protocol;

internal static class CompileRequestMapper
{
    public static MappedCompileRequest Map(SassCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);

        var compileRequest = new InboundMessage.Types.CompileRequest
        {
            Style = request.OutputStyle switch
            {
                SassOutputStyle.Expanded => OutputStyle.Expanded,
                SassOutputStyle.Compressed => OutputStyle.Compressed,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.OutputStyle,
                    "Unknown Sass output style.")
            },
            SourceMap = request.GenerateSourceMap,
            SourceMapIncludeSources = request.IncludeSourcesInSourceMap,
            Charset = request.EmitCharset,
            AlertColor = request.UseTerminalColors,
            AlertAscii = request.UseAsciiDiagnostics,
            Verbose = request.VerboseDeprecations,
            QuietDeps = request.QuietDependencies,
            Silent = request.Silent
        };

        switch (request.Input)
        {
            case SassFileInput file:
                ArgumentException.ThrowIfNullOrWhiteSpace(file.Path);
                compileRequest.Path = Path.GetFullPath(file.Path);
                break;

            case SassStringInput text:
                ArgumentNullException.ThrowIfNull(text.Source);
                if (text.Url is { IsAbsoluteUri: false })
                {
                    throw new ArgumentException("A string input URL must be absolute.", nameof(request));
                }

                compileRequest.String = new InboundMessage.Types.CompileRequest.Types.StringInput
                {
                    Source = text.Source,
                    Syntax = text.Syntax switch
                    {
                        SassSyntax.Scss => Syntax.Scss,
                        SassSyntax.Indented => Syntax.Indented,
                        SassSyntax.Css => Syntax.Css,
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(request),
                            text.Syntax,
                            "Unknown Sass syntax.")
                    },
                    Url = text.Url?.AbsoluteUri ?? string.Empty
                };
                break;

            default:
                throw new ArgumentException(
                    $"Unsupported Sass input type '{request.Input.GetType().FullName}'.",
                    nameof(request));
        }

        AddLoadPaths(compileRequest, request.LoadPaths);
        AddStrings(compileRequest.FatalDeprecation, request.FatalDeprecations, nameof(request.FatalDeprecations));
        AddStrings(compileRequest.SilenceDeprecation, request.SilencedDeprecations, nameof(request.SilencedDeprecations));
        AddStrings(compileRequest.FutureDeprecation, request.FutureDeprecations, nameof(request.FutureDeprecations));

        return new MappedCompileRequest(
            new InboundMessage { CompileRequest = compileRequest },
            request.LogHandler);
    }

    private static void AddLoadPaths(
        InboundMessage.Types.CompileRequest target,
        IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string? path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            target.Importers.Add(new InboundMessage.Types.CompileRequest.Types.Importer
            {
                Path = System.IO.Path.GetFullPath(path)
            });
        }
    }

    private static void AddStrings(
        Google.Protobuf.Collections.RepeatedField<string> target,
        IReadOnlyList<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Values cannot be null or whitespace.", parameterName);
            }

            target.Add(value);
        }
    }
}

internal sealed record MappedCompileRequest(
    InboundMessage Message,
    SassLogHandler? LogHandler);
