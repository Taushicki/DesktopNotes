using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DesktopNotes.Presentation;

// Decorations change only visual elements, never the Markdown document or its undo history.
// No decoration depends on focus or the active line: editing and reading have identical geometry.
internal sealed class InlineMarkdownLayout
{
    internal enum DecorationKind { Hidden, Bullet, Number, Task, Quote, Indent, Rule, TableEdge, TableGap, TableRule, Spacing }
    internal sealed record Cell(int Start, int Length);
    internal sealed record Decoration(int Start, int Length, DecorationKind Kind, string Label = "", double Indent = 0,
        Cell? Cell = null, IReadOnlyList<Cell>? Column = null, IReadOnlyList<IReadOnlyList<Cell>>? Columns = null);
    [Flags]
    internal enum Style { None = 0, Bold = 1, Italic = 2, Code = 4, Strike = 8, Highlight = 16, Link = 32 }
    internal sealed record StyledRange(int Start, int Length, Style Style, double Scale = 1);
    internal List<Decoration> Decorations { get; } = [];
    internal List<StyledRange> Styles { get; } = [];
    private readonly List<(int Start, int End)> _code = [];
    private readonly List<(int Start, int End)> _tables = [];
    private readonly List<(int Start, int End, double Indent)> _items = [];

    internal static InlineMarkdownLayout Create(string source, MarkdownDocument document)
    {
        var layout = new InlineMarkdownLayout();
        layout.VisitBlocks(document, source);
        var offset = 0;
        foreach (var line in source.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (!layout._code.Concat(layout._tables).Any(range => offset >= range.Start && offset <= range.End))
                layout.DecorateLine(text, offset);
            offset += line.Length + 1;
        }
        layout.Decorations.Sort((a, b) => a.Start.CompareTo(b.Start));
        return layout;
    }

    private void VisitBlocks(ContainerBlock blocks, string source)
    {
        foreach (var block in blocks)
        {
            if (block is CodeBlock)
            {
                _code.Add((block.Span.Start, block.Span.End));
                Styles.Add(new(block.Span.Start, block.Span.Length, Style.Code, 12d / 14));
                continue;
            }
            if (block is Markdig.Extensions.Tables.Table table) DecorateTable(table, source);
            if (block is ListItemBlock item)
            {
                var lineStart = source.LastIndexOf('\n', Math.Clamp(item.Span.Start - 1, 0, Math.Max(0, source.Length - 1))) + 1;
                var lineEnd = source.IndexOf('\n', lineStart); if (lineEnd < 0) lineEnd = source.Length;
                var prefix = ListPrefix.Match(source[lineStart..lineEnd]);
                if (prefix.Success) _items.Add((lineStart, item.Span.End, prefix.Groups["indent"].Length * 8 + 16));
            }
            if (block is LeafBlock leaf && leaf.Inline is not null) VisitInlines(leaf.Inline, source);
            if (block is ContainerBlock container) VisitBlocks(container, source);
        }
    }

    private static readonly Regex ListPrefix = new(@"^(?<indent>[ \t]*)(?<marker>[-+*]|\d+[.)]) (?:(?<task>\[[ xX]\]) )?", RegexOptions.Compiled);
    private void DecorateLine(string text, int offset)
    {
        var list = ListPrefix.Match(text);
        if (list.Success)
        {
            var marker = list.Groups["marker"].Value;
            var kind = list.Groups["task"].Success ? DecorationKind.Task : char.IsDigit(marker[0]) ? DecorationKind.Number : DecorationKind.Bullet;
            Decorations.Add(new(offset, list.Length, kind, kind == DecorationKind.Task ? list.Groups["task"].Value : marker,
                list.Groups["indent"].Length * 8));
            return;
        }
        var heading = Regex.Match(text, @"^ {0,3}(#{1,6}) ");
        if (heading.Success)
        {
            Decorations.Add(new(offset, heading.Length, DecorationKind.Hidden));
            Styles.Add(new(offset, text.Length, Style.Bold, 1.9 - heading.Groups[1].Length * 0.12));
            return;
        }
        var quote = Regex.Match(text, @"^ {0,3}(?:> ?)+");
        if (quote.Success) { Decorations.Add(new(offset, quote.Length, DecorationKind.Quote)); return; }
        if (Regex.IsMatch(text, @"^ {0,3}(?:-{3,}|\*{3,}|_{3,}) *$"))
        { Decorations.Add(new(offset, text.Length, DecorationKind.Rule)); return; }
        var parent = _items.Where(item => offset > item.Start && offset <= item.End).OrderByDescending(item => item.Start).FirstOrDefault();
        if (parent.Indent > 0 && text.Length > 0)
        {
            var spaces = text.Length - text.TrimStart(' ', '\t').Length;
            Decorations.Add(new(offset, spaces, DecorationKind.Indent, Indent: parent.Indent));
        }
    }

    private void Hide(int start, int length)
    {
        if (length > 0) Decorations.Add(new(start, length, DecorationKind.Hidden));
    }

    private void DecorateTable(Markdig.Extensions.Tables.Table table, string source)
    {
        _tables.Add((table.Span.Start, table.Span.End));
        var rows = new List<(int Start, string Text, Cell[] Cells)>();
        var offset = table.Span.Start;
        foreach (var raw in source.Substring(table.Span.Start, table.Span.Length).Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            var pipes = Regex.Matches(text, @"(?<!\\)\|").Select(match => match.Index).ToArray();
            // Pipe-less edge cells are valid Markdown too; insert visual edges without adding source characters.
            var edges = new List<int> { -1 }; edges.AddRange(pipes); edges.Add(text.Length);
            var cells = new List<Cell>();
            for (var index = 0; index < edges.Count - 1; index++)
            {
                var start = edges[index] + 1; var end = edges[index + 1];
                if ((index == 0 || index == edges.Count - 2) && string.IsNullOrWhiteSpace(text[start..end])) continue;
                while (start < end && char.IsWhiteSpace(text[start])) start++;
                while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
                cells.Add(new(offset + start, end - start));
            }
            if (cells.Count > 0) rows.Add((offset, text, cells.ToArray()));
            offset += raw.Length + 1;
        }
        bool IsSeparator((int Start, string Text, Cell[] Cells) row) => row.Cells.All(cell => Regex.IsMatch(source.Substring(cell.Start, cell.Length), @"^:?-{3,}:?$"));
        var count = rows.Select(row => row.Cells.Length).DefaultIfEmpty().Max();
        var columns = Enumerable.Range(0, count).Select(index => (IReadOnlyList<Cell>)rows.Where(row => !IsSeparator(row) && row.Cells.Length > index)
            .Select(row => row.Cells[index]).ToArray()).ToArray();
        foreach (var row in rows)
        {
            if (IsSeparator(row)) { Decorations.Add(new(row.Start, row.Text.Length, DecorationKind.TableRule, Columns: columns)); continue; }
            if (row.Start == table.Span.Start) Styles.Add(new(row.Start, row.Text.Length, Style.Bold));
            Decorations.Add(new(row.Start, row.Cells[0].Start - row.Start, DecorationKind.TableEdge));
            for (var index = 0; index < row.Cells.Length; index++)
            {
                var cell = row.Cells[index]; var end = cell.Start + cell.Length;
                var next = index + 1 < row.Cells.Length ? row.Cells[index + 1].Start : row.Start + row.Text.Length;
                Decorations.Add(new(end, next - end, DecorationKind.TableGap, Cell: cell, Column: columns[index]));
            }
        }
    }
    private void VisitInlines(ContainerInline container, string source)
    {
        foreach (var inline in container)
        {
            var start = inline.Span.Start; var length = inline.Span.Length;
            if (start < 0 || length <= 0 || start + length > source.Length) continue;
            switch (inline)
            {
                case EmphasisInline emphasis:
                    var count = emphasis.DelimiterCount;
                    Hide(start, count); Hide(start + length - count, count);
                    var style = emphasis.DelimiterChar switch
                    { '~' => Style.Strike, '=' => Style.Highlight, _ => count >= 2 ? Style.Bold | (count == 3 ? Style.Italic : Style.None) : Style.Italic };
                    Styles.Add(new(start, length, style));
                    VisitInlines(emphasis, source);
                    break;
                case CodeInline:
                    var ticks = source.AsSpan(start, length).IndexOfAnyExcept('`');
                    if (ticks > 0) { Hide(start, ticks); Hide(start + length - ticks, ticks); }
                    Styles.Add(new(start, length, Style.Code));
                    break;
                case WikiLinkInline wiki:
                    var pipe = source.IndexOf('|', start + 2, length - 4);
                    Hide(start, pipe >= 0 ? pipe - start + 1 : 2); Hide(start + length - 2, 2);
                    Styles.Add(new(start, length, Style.Link));
                    break;
                case LinkInline link when !link.IsImage && !link.IsAutoLink && !link.IsShortcut:
                    var close = source.IndexOf("](", start, length, StringComparison.Ordinal);
                    if (close >= 0) { Hide(start, 1); Hide(close, start + length - close); }
                    Styles.Add(new(start, length, Style.Link)); VisitInlines(link, source);
                    break;
                case ContainerInline child: VisitInlines(child, source); break;
            }
        }
    }
}
