using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DesktopNotes.Presentation;

internal static class MarkdownPipelines
{
    internal static MarkdownPipeline Create(bool tables = true)
    {
        var builder = new MarkdownPipelineBuilder().UseTaskLists().UseEmphasisExtras().UsePreciseSourceLocation().DisableHtml();
        if (tables) builder.UsePipeTables();
        builder.InlineParsers.Insert(0, new WikiLinkParser());
        return builder.Build();
    }

    private sealed class WikiLinkParser : InlineParser
    {
        internal WikiLinkParser() => OpeningCharacters = ['['];
        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var start = slice.Start;
            if (slice.CurrentChar != '[' || slice.PeekChar(1) != '[') return false;
            var end = slice.Text.IndexOf("]]", start + 2, StringComparison.Ordinal);
            if (end < 0 || end + 1 > slice.End || slice.Text.AsSpan(start + 2, end - start - 2).Contains('\n')) return false;
            var parts = slice.Text[(start + 2)..end].Split('|', 2);
            if (string.IsNullOrWhiteSpace(parts[0])) return false;
            processor.Inline = new WikiLinkInline(parts[0], parts.Length == 2 ? parts[1] : parts[0])
            { Span = new SourceSpan(processor.GetSourcePosition(start), processor.GetSourcePosition(end + 1)) };
            slice.Start = end + 2;
            return true;
        }
    }
}

internal sealed class WikiLinkInline(string destination, string label) : LeafInline
{
    internal string Destination { get; } = destination;
    internal string Label { get; } = label;
}
