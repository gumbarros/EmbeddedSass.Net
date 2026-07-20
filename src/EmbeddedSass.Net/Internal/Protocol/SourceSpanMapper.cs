using EmbeddedSass.Diagnostics;
using Sass.EmbeddedProtocol;

namespace EmbeddedSass.Internal.Protocol;

internal static class SourceSpanMapper
{
    public static SassSourceSpan Map(SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (span.Start is null)
        {
            throw new SassProtocolException("A source span is missing its mandatory start location.");
        }

        SassSourceLocation start = Map(span.Start);
        SassSourceLocation? end = span.End is null ? null : Map(span.End);
        if (end is not null && end.Offset < start.Offset)
        {
            throw new SassProtocolException("A source span ends before it starts.");
        }

        if (end is null && span.Text.Length != 0)
        {
            throw new SassProtocolException("An empty source span contains text.");
        }

        Uri? url = null;
        if (span.Url.Length != 0 &&
            (!Uri.TryCreate(span.Url, UriKind.Absolute, out url) || !url.IsAbsoluteUri))
        {
            throw new SassProtocolException($"Source span URL '{span.Url}' is not absolute.");
        }

        return new SassSourceSpan(
            span.Text,
            start,
            end,
            url,
            span.Context.Length == 0 ? null : span.Context);
    }

    private static SassSourceLocation Map(SourceSpan.Types.SourceLocation location) =>
        new(location.Offset, location.Line, location.Column);
}
