using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DesktopNotes.Application;

public sealed record MarkdownTextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

public static class MarkdownFormatting
{
    [Flags]
    private enum Format { None = 0, Bold = 1, Italic = 2, Strike = 4, Highlight = 8, Code = 16, Underline = 32 }
    private sealed record Wrapper(int Start, int End, int Open, int Close, Format Format);
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseEmphasisExtras().UsePreciseSourceLocation().DisableHtml().Build();
    private const string Paragraphs = @"(\r?\n[ \t]*\r?\n(?:[ \t]*\r?\n)*)";

    public static MarkdownTextEdit ToggleBold(string text, int start, int length) => ToggleInline(text, start, length, "**");

    public static MarkdownTextEdit ToggleInline(string text, int start, int length, string marker)
    {
        if (start < 0 || length <= 0 || start > text.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        var requested = marker switch { "**" => Format.Bold, "*" => Format.Italic, "~~" => Format.Strike, "==" => Format.Highlight, "`" => Format.Code, "++" => Format.Underline, _ => throw new ArgumentException("Unsupported inline marker.", nameof(marker)) };
        var end = start + length;
        var wrappers = Wrappers(text);
        // Selection normally contains visible text only. Include its exact Markdown
        // shells, from the innermost to the outermost, without absorbing neighbours.
        bool expanded;
        do
        {
            expanded = false;
            foreach (var wrapper in wrappers.OrderBy(w => w.End - w.Start))
                if (wrapper.Start + wrapper.Open == start && wrapper.End - wrapper.Close == end)
                { start = wrapper.Start; end = wrapper.End; expanded = true; }
        } while (expanded);

        var enclosing = wrappers.Where(w => w.Format == requested && w.Start + w.Open <= start && w.End - w.Close >= end)
            .OrderBy(w => w.End - w.Start).FirstOrDefault();
        if (enclosing is not null)
        {
            // Removing a style from only part of a larger styled span splits that
            // span; the unselected text keeps its formatting.
            var before = Decorate(text[(enclosing.Start + enclosing.Open)..start], marker);
            var selected = text[start..end];
            var after = Decorate(text[end..(enclosing.End - enclosing.Close)], marker);
            return new(enclosing.Start, enclosing.End - enclosing.Start, before + selected + after, before.Length, selected.Length);
        }
        var original = text[start..end];
        var leading = original.Length - original.TrimStart().Length;
        var content = original.Trim();
        if (content.Length == 0) return new(start, original.Length, original, 0, original.Length);
        var parts = Regex.Split(content, Paragraphs);
        var remove = parts.Where((_, i) => i % 2 == 0).All(part => Read(part.Trim()).Formats.HasFlag(requested));
        int selectionStart = 0, selectionLength = 0;
        for (var i = 0; i < parts.Length; i += 2)
        {
            var part = parts[i]; var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var offset = part.Length - part.TrimStart().Length;
            var (core, formats, links) = Read(trimmed);
            formats = remove ? formats & ~requested : formats | requested;
            // Remove redundant copies of the requested style inside a mixed selection.
            if (!remove && requested != Format.Code && !formats.HasFlag(Format.Code))
                core = Strip(core, requested);
            var formatted = Build(core, formats, links);
            if (parts.Length == 1)
            {
                selectionStart = offset;
                selectionLength = formatted.Text.Length;
            }
            parts[i] = part[..offset] + formatted.Text + part[(offset + trimmed.Length)..];
        }
        var result = string.Concat(parts);
        return new(start, original.Length, original[..leading] + result + original[(leading + content.Length)..],
            parts.Length == 1 ? leading + selectionStart : leading, parts.Length == 1 ? selectionLength : result.Length);
    }

    private static string Decorate(string content, string marker)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0) return content;
        var leading = content.Length - content.TrimStart().Length;
        return content[..leading] + marker + trimmed + marker + content[(leading + trimmed.Length)..];
    }

    private static (string Core, Format Formats, List<(string Open, string Close)> Links) Read(string source)
    {
        var formats = Format.None;
        var links = new List<(string, string)>();
        while (true)
        {
            var shell = Wrappers(source).Where(w => w.Start == 0 && w.End == source.Length).OrderByDescending(w => w.Open + w.Close).FirstOrDefault();
            if (shell is null) break;
            formats |= shell.Format;
            if (shell.Format == Format.None) links.Add((source[..shell.Open], source[^shell.Close..]));
            source = source[shell.Open..^shell.Close];
            if (shell.Format == Format.Code) break; // Contents of code remain literal.
        }
        return (source, formats, links);
    }

    private static string Strip(string source, Format format)
    {
        var deletions = Wrappers(source).Where(w => w.Format == format)
            .SelectMany(w => new[] { (Start: w.Start, Length: w.Open), (Start: w.End - w.Close, Length: w.Close) }).OrderByDescending(w => w.Start);
        foreach (var deletion in deletions) source = source.Remove(deletion.Start, deletion.Length);
        return source;
    }

    private static (string Text, int Prefix) Build(string core, Format formats, List<(string Open, string Close)> links)
    {
        var prefix = ""; var suffix = "";
        // Code is innermost: emphasis tokens must stay outside literal code content.
        if (formats.HasFlag(Format.Code))
        {
            var longest = Regex.Matches(core, "`+").Select(m => m.Length).DefaultIfEmpty().Max();
            var ticks = new string('`', longest + 1);
            var pad = core.StartsWith('`') || core.EndsWith('`') ? " " : "";
            prefix = ticks + pad; suffix = pad + ticks;
        }
        foreach (var (flag, marker) in new[] { (Format.Italic, "*"), (Format.Bold, "**"), (Format.Underline, "++"), (Format.Strike, "~~"), (Format.Highlight, "==") })
            if (formats.HasFlag(flag)) { prefix = marker + prefix; suffix += marker; }
        foreach (var link in links.AsEnumerable().Reverse()) { prefix = link.Open + prefix; suffix += link.Close; }
        return (prefix + core + suffix, prefix.Length);
    }

    private static List<Wrapper> Wrappers(string source)
    {
        var result = new List<Wrapper>();
        void Inlines(ContainerInline container)
        {
            foreach (var inline in container)
            {
                var start = inline.Span.Start; var end = inline.Span.End + 1;
                if (start < 0 || end > source.Length || end <= start) continue;
                if (inline is EmphasisInline emphasis)
                {
                    var format = emphasis.DelimiterChar switch
                    { '*' or '_' => emphasis.DelimiterCount == 2 ? Format.Bold : Format.Italic, '~' => Format.Strike, '=' => Format.Highlight, '+' => Format.Underline, _ => Format.None };
                    if (format != Format.None) result.Add(new(start, end, emphasis.DelimiterCount, emphasis.DelimiterCount, format));
                }
                else if (inline is CodeInline)
                {
                    var ticks = source.AsSpan(start, end - start).IndexOfAnyExcept('`');
                    if (ticks > 0) result.Add(new(start, end, ticks, ticks, Format.Code));
                }
                else if (inline is LinkInline { IsImage: false, IsAutoLink: false, IsShortcut: false })
                {
                    var close = source.IndexOf("](", start, end - start, StringComparison.Ordinal);
                    if (close >= 0) result.Add(new(start, end, 1, end - close, Format.None));
                }
                if (inline is ContainerInline child) Inlines(child);
            }
        }
        void Blocks(ContainerBlock container)
        {
            foreach (var block in container)
            {
                if (block is LeafBlock { Inline: { } inline }) Inlines(inline);
                if (block is ContainerBlock child) Blocks(child);
            }
        }
        Blocks(Markdown.Parse(source, Pipeline));
        return result;
    }
}
